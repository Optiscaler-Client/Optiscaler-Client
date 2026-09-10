using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using OptiscalerClient.Helpers;
using OptiscalerClient.Models;
using OptiscalerClient.Services;

namespace OptiscalerClient.Views;

public partial class FrameGenerationSettingsWindow : Window, IGamepadInputHost
{
    private static readonly FrameGenerationRoute[] AdvancedRoutes =
    [
        FrameGenerationRoute.Auto,
        FrameGenerationRoute.Disabled,
        FrameGenerationRoute.DlssGStreamline,
        FrameGenerationRoute.Nukem,
        FrameGenerationRoute.Fsr31Native,
        FrameGenerationRoute.Fsr30Native,
        FrameGenerationRoute.OptiFg
    ];

    private static readonly FrameGenerationNvngxReplacement[] NvngxReplacementOptions =
    [
        FrameGenerationNvngxReplacement.None,
        FrameGenerationNvngxReplacement.Nukems,
        FrameGenerationNvngxReplacement.Ffx,
        FrameGenerationNvngxReplacement.Arturs,
        FrameGenerationNvngxReplacement.Combo
    ];

    private const string NewDlssEnablerTag = "__new__";
    private const string OutputDisabledTag = "__disabled__";

    private readonly FrameGenerationCapabilities _capabilities = new();
    private readonly FrameGenerationRecommendation _recommendation = new();
    private readonly GameFrameGenerationSettings _initialSettings = new();
    private readonly GpuVendor _gpuVendor;
    private string _selectedDlssEnablerVersion = "";
    private string _selectedStreamlineVersion = ComponentManagementService.LatestAvailableTag;
    // Mirror is the default tab: it's the automated path (download-on-select), Custom is the
    // manual-import fallback. Starts on Custom only when a previously-saved selection is a
    // Custom name (i.e. not tagged with the DlssEnablerMirrorTagPrefix).
    private bool _dlssEnablerShowingMirror = true;
    private bool _isUpdating;
    private GamepadDialogNavigationHelper? _gamepadHelper;

    GamepadHelperBase? IGamepadInputHost.GamepadHelper => _gamepadHelper;

    public FrameGenerationSettingsWindow()
    {
        InitializeComponent();
        DialogDimHelper.Register(this);
    }

    public FrameGenerationSettingsWindow(Window owner, Game game, GpuInfo? gpu)
        : this(
            owner,
            new FrameGenerationConfigurationService().DetectCapabilities(game, gpu),
            game.FrameGenerationSettings,
            gpu)
    {
    }

    public FrameGenerationSettingsWindow(
        Window owner,
        FrameGenerationCapabilities capabilities,
        GameFrameGenerationSettings? saved,
        GpuInfo? gpu = null)
    {
        InitializeComponent();
        DialogDimHelper.Register(this);
        WindowScreenFitHelper.FitToScreen(this);

        _capabilities = capabilities;
        _recommendation = new FrameGenerationConfigurationService().GetRecommendation(capabilities);
        _gpuVendor = gpu?.Vendor ?? GpuVendor.Unknown;
        _initialSettings = new GameFrameGenerationSettings
        {
            Route = saved?.Route ?? FrameGenerationRoute.Disabled,
            Output = saved?.Output ?? FrameGenerationOutput.Auto,
            MultiFrameMode = saved?.MultiFrameMode ?? MultiFrameGenerationMode.Auto,
            AdvancedMode = saved?.AdvancedMode ?? false,
            DynamicTargetFps = saved?.DynamicTargetFps,
            AppliedAtUtc = saved?.AppliedAtUtc,
            NvngxReplacement = saved?.NvngxReplacement ?? FrameGenerationNvngxReplacement.None,
            DlssEnablerVersion = saved?.DlssEnablerVersion,
            StreamlineVersion = saved?.StreamlineVersion
        };
        _selectedDlssEnablerVersion = _initialSettings.DlssEnablerVersion ?? "";
        _selectedStreamlineVersion = _initialSettings.StreamlineVersion ?? ComponentManagementService.LatestAvailableTag;
        _dlssEnablerShowingMirror = string.IsNullOrEmpty(_initialSettings.DlssEnablerVersion)
            || ComponentManagementService.IsDlssEnablerMirrorTag(_initialSettings.DlssEnablerVersion);

        var titleBar = this.FindControl<Border>("TitleBar");
        if (titleBar != null)
            titleBar.PointerPressed += (_, e) => BeginMoveDrag(e);

        Opened += (_, _) =>
        {
            var root = this.FindControl<Panel>("RootPanel");
            if (root != null)
            {
                AnimationHelper.SetupPanelTransition(root);
                root.Opacity = 1;
            }
            _gamepadHelper ??= new GamepadDialogNavigationHelper(this, null);
            if (owner is IGamepadInputHost host)
                host.GamepadHelper?.SuspendInput();
        };
        Closed += (_, _) =>
        {
            if (owner is IGamepadInputHost host)
                host.GamepadHelper?.ResumeInput();
            _gamepadHelper?.Dispose();
            _gamepadHelper = null;
        };

        PopulateControls();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void PopulateControls()
    {
        _isUpdating = true;
        try
        {
            var advanced = this.FindControl<CheckBox>("ChkAdvancedRoutes");
            if (advanced != null) advanced.IsChecked = _initialSettings.AdvancedMode;
            PopulateRoutes(_initialSettings.Route);
            UpdateDlssStreamlineRouteInfo();
            PopulateOutputs(_initialSettings.Route, _initialSettings.Output);
            PopulateNvngxReplacements(_initialSettings.NvngxReplacement);
            var needsVersion = _initialSettings.NvngxReplacement is FrameGenerationNvngxReplacement.Arturs or FrameGenerationNvngxReplacement.Combo;
            var versionPanel = this.FindControl<StackPanel>("PnlDlssEnablerVersion");
            if (versionPanel != null) versionPanel.IsVisible = needsVersion;
            if (needsVersion) PopulateDlssEnablerVersions(_selectedDlssEnablerVersion);
            UpdateStreamlineVersionVisibility();
            PopulateFgMultiplier(_initialSettings.MultiFrameMode);
            var targetFpsBox = this.FindControl<TextBox>("TxtDynamicTargetFps");
            if (targetFpsBox != null)
                targetFpsBox.Text = _initialSettings.DynamicTargetFps is > 0
                    ? _initialSettings.DynamicTargetFps.Value.ToString(CultureInfo.InvariantCulture)
                    : "";
            UpdateDependentControlState();
        }
        finally
        {
            _isUpdating = false;
        }
        UpdateSaveButtonState();
    }

    private void PopulateRoutes(FrameGenerationRoute selected)
    {
        var combo = this.FindControl<ComboBox>("CmbFgRoute");
        if (combo == null) return;

        var advanced = this.FindControl<CheckBox>("ChkAdvancedRoutes")?.IsChecked == true;
        IEnumerable<FrameGenerationRoute> routes = advanced && !_capabilities.IsAntiCheatDetected
            ? AdvancedRoutes
            : _capabilities.AvailableRoutes;

        combo.Items.Clear();
        foreach (var route in routes.Distinct())
        {
            var item = new ComboBoxItem { Content = GetRouteLabel(route), Tag = route };
            ToolTip.SetTip(item, GetRouteTooltip(route));
            combo.Items.Add(item);
        }
        SelectTag(combo, selected);
    }

    /// <summary>The Output combo carries its own "Disabled" entry (mirrors FGRoute=Disabled) so the
    /// top-level UI never needs to show FG route/input at all for the simple on/off case.</summary>
    private void PopulateOutputs(FrameGenerationRoute currentRoute, FrameGenerationOutput selected)
    {
        var combo = this.FindControl<ComboBox>("CmbFgOutput");
        if (combo == null) return;
        combo.Items.Clear();

        var disabledItem = new ComboBoxItem { Content = GetRouteLabel(FrameGenerationRoute.Disabled), Tag = OutputDisabledTag };
        ToolTip.SetTip(disabledItem, GetRouteTooltip(FrameGenerationRoute.Disabled));
        combo.Items.Add(disabledItem);

        foreach (var output in _capabilities.AvailableOutputs)
        {
            var item = new ComboBoxItem { Content = GetOutputLabel(output), Tag = output };
            ToolTip.SetTip(item, GetOutputTooltip(output));
            combo.Items.Add(item);
        }

        if (currentRoute == FrameGenerationRoute.Disabled)
            combo.SelectedIndex = 0;
        else
            SelectTag(combo, selected);
    }

    private bool IsOutputDisabledSelected()
        => (this.FindControl<ComboBox>("CmbFgOutput")?.SelectedItem as ComboBoxItem)?.Tag is string tag && tag == OutputDisabledTag;

    /// <summary>Simplified top-level multiplier: x2 only, unless the output is DLSS-G (x2..x6).
    /// Anything beyond x2 on DLSS-G requires DLSS Enabler, which <see cref="ApplyAutoNvngxReplacement"/>
    /// selects automatically, so no capability lookup is needed here.</summary>
    private void PopulateFgMultiplier(MultiFrameGenerationMode selected)
    {
        var combo = this.FindControl<ComboBox>("CmbMfgMultiplier");
        if (combo == null) return;

        var output = GetSelectedTag<FrameGenerationOutput>("CmbFgOutput");
        List<MultiFrameGenerationMode> modes;
        if (output == FrameGenerationOutput.DlssG)
        {
            modes = [MultiFrameGenerationMode.X2, MultiFrameGenerationMode.X3, MultiFrameGenerationMode.X4, MultiFrameGenerationMode.X5, MultiFrameGenerationMode.X6];
            if (_capabilities.SupportsDynamicMfg) modes.Add(MultiFrameGenerationMode.Dynamic);
        }
        else
        {
            modes = [MultiFrameGenerationMode.X2];
        }

        combo.Items.Clear();
        foreach (var mode in modes)
        {
            var item = new ComboBoxItem { Content = GetMfgLabel(mode), Tag = mode };
            ToolTip.SetTip(item, GetMfgTooltip(mode));
            combo.Items.Add(item);
        }
        combo.IsEnabled = !IsOutputDisabledSelected() && modes.Count > 1;
        SelectTag(combo, selected);
        UpdateDynamicTargetFpsVisibility();
    }

    private void UpdateDynamicTargetFpsVisibility()
    {
        var panel = this.FindControl<StackPanel>("PnlDynamicTargetFps");
        if (panel == null) return;
        panel.IsVisible = !IsOutputDisabledSelected() && GetSelectedTag<MultiFrameGenerationMode>("CmbMfgMultiplier") == MultiFrameGenerationMode.Dynamic;
    }

    /// <summary>Picks the FG Nvngx Replacement (and, when needed, the DLSS Enabler version) that best
    /// fits the selected output/multiplier and detected GPU. Only runs from direct user interaction
    /// with the top-level Output/Multiplier controls, never on initial load (which restores the saved
    /// value as-is) and never from advanced-panel edits (which must not feed back into the top two).</summary>
    private void ApplyAutoNvngxReplacement()
    {
        var output = GetSelectedTag<FrameGenerationOutput>("CmbFgOutput");
        var versionPanel = this.FindControl<StackPanel>("PnlDlssEnablerVersion");

        if (output != FrameGenerationOutput.DlssG)
        {
            PopulateNvngxReplacements(FrameGenerationNvngxReplacement.None);
            if (versionPanel != null) versionPanel.IsVisible = false;
            UpdateStreamlineVersionVisibility();
            return;
        }

        var multiplier = GetSelectedTag<MultiFrameGenerationMode>("CmbMfgMultiplier");
        // x2 needs no replacement provider on NVIDIA: real DLSS-G already runs natively there. AMD
        // gets FSR 3/4 FG, everything else (Intel/unknown) falls back to Nukem. Anything above x2
        // always needs DLSS Enabler regardless of vendor — native MFG needs a specific GPU
        // generation we can't reliably detect from GpuInfo alone.
        var replacement = multiplier != MultiFrameGenerationMode.X2
            ? FrameGenerationNvngxReplacement.Arturs
            : _gpuVendor switch
            {
                GpuVendor.NVIDIA => FrameGenerationNvngxReplacement.None,
                GpuVendor.AMD => FrameGenerationNvngxReplacement.Ffx,
                _ => FrameGenerationNvngxReplacement.Nukems
            };

        PopulateNvngxReplacements(replacement);

        var needsVersion = replacement is FrameGenerationNvngxReplacement.Arturs or FrameGenerationNvngxReplacement.Combo;
        if (versionPanel != null) versionPanel.IsVisible = needsVersion;
        UpdateStreamlineVersionVisibility();
        if (!needsVersion) return;

        _dlssEnablerShowingMirror = true;
        PopulateDlssEnablerVersions("");
        var versionCombo = this.FindControl<ComboBox>("CmbDlssEnablerVersion");
        // Index 0 is the "-- Select version --" placeholder; index 1 is the newest mirror version,
        // since PopulateDlssEnablerVersions sorts them descending.
        if (versionCombo != null && versionCombo.Items.Count > 1)
            versionCombo.SelectedIndex = 1;
        _selectedDlssEnablerVersion = GetSelectedStringTag("CmbDlssEnablerVersion");
    }

    private void PopulateNvngxReplacements(FrameGenerationNvngxReplacement selected)
    {
        var combo = this.FindControl<ComboBox>("CmbFgNvngxReplacement");
        if (combo == null) return;

        combo.Items.Clear();
        foreach (var replacement in NvngxReplacementOptions)
        {
            var item = new ComboBoxItem { Content = GetNvngxReplacementLabel(replacement), Tag = replacement };
            ToolTip.SetTip(item, GetNvngxReplacementTooltip(replacement));
            combo.Items.Add(item);
        }

        var outputIsDlssG = GetSelectedTag<FrameGenerationOutput>("CmbFgOutput") == FrameGenerationOutput.DlssG;
        SelectTag(combo, outputIsDlssG ? selected : FrameGenerationNvngxReplacement.None);
    }

    private void PopulateDlssEnablerVersions(string selected)
    {
        var combo = this.FindControl<ComboBox>("CmbDlssEnablerVersion");
        if (combo == null) return;

        var componentService = new ComponentManagementService();
        combo.Items.Clear();
        combo.Items.Add(new ComboBoxItem { Content = Resource("TxtSelectVersion", "-- Select version --"), Tag = "" });

        if (_dlssEnablerShowingMirror)
        {
            // Union of remotely-known releases and anything already cached locally (covers the
            // case where a version was downloaded before but the GitHub API call just failed).
            // Sorted by parsed Version, not raw string — "4.10.0.0" must sort above "4.9.0.15".
            var versions = componentService.DlssEnablerMirrorAvailableVersions
                .Concat(componentService.GetDownloadedDlssEnablerMirrorVersions())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(v => Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0))
                .ThenByDescending(v => v, StringComparer.OrdinalIgnoreCase);
            foreach (var version in versions)
            {
                var tag = ComponentManagementService.BuildDlssEnablerMirrorTag(version);
                combo.Items.Add(new ComboBoxItem { Content = version, Tag = tag });
            }
        }
        else
        {
            foreach (var version in componentService.GetDownloadedDlssEnablerVersions())
                combo.Items.Add(new ComboBoxItem { Content = version, Tag = version });
            combo.Items.Add(ComboActionItemHelper.Build(this, Resource("TxtNewOrImport", "New / Import..."), NewDlssEnablerTag));
        }

        SelectStringTag(combo, selected);

        var infoBadge = this.FindControl<Border>("BdgDlssEnablerMirrorInfo");
        if (infoBadge != null) infoBadge.IsVisible = _dlssEnablerShowingMirror;
        UpdateDlssEnablerTabButtons();
    }

    private void UpdateDlssEnablerTabButtons()
    {
        var btnMirror = this.FindControl<Button>("BtnDlssEnablerMirror");
        var btnCustom = this.FindControl<Button>("BtnDlssEnablerCustom");
        if (btnMirror == null || btnCustom == null) return;

        void SetActive(Button b) { b.Classes.Remove("BtnSecondary"); b.Classes.Add("BtnPrimary"); }
        void SetInactive(Button b) { b.Classes.Remove("BtnPrimary"); b.Classes.Add("BtnSecondary"); }

        if (_dlssEnablerShowingMirror) { SetActive(btnMirror); SetInactive(btnCustom); }
        else { SetInactive(btnMirror); SetActive(btnCustom); }
    }

    /// <summary>Streamline has one source (NVIDIA's own repo, fetched once at startup — see
    /// ComponentManagementService.CheckForUpdatesAsync) so unlike DLSS Enabler there's no
    /// Mirror/Custom tab pair here, just a plain version list with a "Latest" sentinel — same
    /// literal-string convention (not localized) every other "Latest version available" sentinel in
    /// this codebase already uses (ManageDefaultVersionsWindow, ManageGameWindow).</summary>
    private void PopulateStreamlineVersions(string selected)
    {
        var combo = this.FindControl<ComboBox>("CmbStreamlineVersion");
        if (combo == null) return;

        var componentService = new ComponentManagementService();
        combo.Items.Clear();
        combo.Items.Add(new ComboBoxItem { Content = "Latest version available", Tag = ComponentManagementService.LatestAvailableTag, Classes = { "SentinelOption" } });

        // Union of remotely-known releases and anything already cached locally (covers the case
        // where a version was downloaded before but the GitHub API call just failed).
        var versions = componentService.StreamlineAvailableVersions
            .Concat(componentService.GetDownloadedStreamlineVersions())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(v => Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0))
            .ThenByDescending(v => v, StringComparer.OrdinalIgnoreCase);
        foreach (var version in versions)
            combo.Items.Add(new ComboBoxItem { Content = version, Tag = version });

        SelectStringTag(combo, selected);
    }

    /// <summary>Recomputes whether the current (live combo) settings need Streamline at all — same
    /// FrameGenerationConfigurationService.RequiresStreamline check the install path uses, called with
    /// a null optiscalerVersion since only the narrow nightly-Nukem edge case needs the real one and
    /// that doesn't affect whether this picker should be shown. Called from every point that already
    /// recomputes PnlDlssEnablerVersion's visibility, since both depend on the same Route/Output/
    /// NvngxReplacement selection.</summary>
    private void UpdateStreamlineVersionVisibility()
    {
        var panel = this.FindControl<StackPanel>("PnlStreamlineVersion");
        if (panel == null) return;

        var current = new GameFrameGenerationSettings
        {
            Route = GetSelectedTag<FrameGenerationRoute>("CmbFgRoute"),
            Output = GetSelectedTag<FrameGenerationOutput>("CmbFgOutput"),
            NvngxReplacement = GetSelectedTag<FrameGenerationNvngxReplacement>("CmbFgNvngxReplacement"),
        };
        var needsStreamline = new FrameGenerationConfigurationService().RequiresStreamline(current, _capabilities);
        panel.IsVisible = needsStreamline;
        if (needsStreamline) PopulateStreamlineVersions(_selectedStreamlineVersion);
    }

    private void CmbStreamlineVersion_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating) return;
        _selectedStreamlineVersion = GetSelectedStringTag("CmbStreamlineVersion");
    }

    private void BtnDlssEnablerMirror_Click(object? sender, RoutedEventArgs e)
    {
        if (_dlssEnablerShowingMirror) return;
        _dlssEnablerShowingMirror = true;
        _isUpdating = true;
        try { PopulateDlssEnablerVersions(_selectedDlssEnablerVersion); }
        finally { _isUpdating = false; }
        UpdateSaveButtonState();
    }

    private void BtnDlssEnablerCustom_Click(object? sender, RoutedEventArgs e)
    {
        if (!_dlssEnablerShowingMirror) return;
        _dlssEnablerShowingMirror = false;
        _isUpdating = true;
        try { PopulateDlssEnablerVersions(_selectedDlssEnablerVersion); }
        finally { _isUpdating = false; }
        UpdateSaveButtonState();
    }

    // Advanced-panel edit: per spec this must never feed back into the top-level Output/Multiplier
    // controls, so it only updates its own local banner.
    private void CmbFgRoute_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating) return;
        UpdateDlssStreamlineRouteInfo();
        UpdateStreamlineVersionVisibility();
    }

    /// <summary>Shows an info banner reminding the user that "DLSS-G via Streamline" needs
    /// NVIDIA's native Frame Generation enabled in the game itself — OptiScaler taps into it,
    /// it doesn't generate frames on its own for this route (unlike OptiFG, which does). Fires
    /// both when the route is picked explicitly (Advanced routes) and when it's left on Auto and
    /// silently resolves to DLSS-G via Streamline (the common case — most users never open
    /// Advanced routes at all).</summary>
    private void UpdateDlssStreamlineRouteInfo()
    {
        var panel = this.FindControl<Border>("PnlDlssStreamlineRouteInfo");
        if (panel == null) return;
        var selectedRoute = GetSelectedTag<FrameGenerationRoute>("CmbFgRoute");
        // Mirrors FrameGenerationConfigurationService.ResolveEffectiveRoute's Nukem preference: Auto
        // doesn't actually land on DlssGStreamline when Nukem covers the same native DLSS-G signal
        // without Streamline's crash-prone interposer hook.
        var effectiveRoute = selectedRoute != FrameGenerationRoute.Auto ? selectedRoute
            : _recommendation.Route == FrameGenerationRoute.DlssGStreamline && _capabilities.AvailableRoutes.Contains(FrameGenerationRoute.Nukem)
                ? FrameGenerationRoute.Nukem
                : _recommendation.Route;
        panel.IsVisible = effectiveRoute == FrameGenerationRoute.DlssGStreamline;
    }

    private void CmbFgOutput_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating) return;
        var outputDisabled = IsOutputDisabledSelected();
        _isUpdating = true;
        try
        {
            var routeCombo = this.FindControl<ComboBox>("CmbFgRoute");
            if (routeCombo != null)
            {
                if (outputDisabled)
                    SelectTag(routeCombo, FrameGenerationRoute.Disabled);
                else if (GetSelectedTag<FrameGenerationRoute>("CmbFgRoute") == FrameGenerationRoute.Disabled)
                    SelectTag(routeCombo, FrameGenerationRoute.Auto);
            }
            UpdateDlssStreamlineRouteInfo();
            PopulateFgMultiplier(MultiFrameGenerationMode.X2);
            ApplyAutoNvngxReplacement();
            UpdateDependentControlState();
        }
        finally { _isUpdating = false; }
        UpdateSaveButtonState();
    }

    private void CmbMfgMultiplier_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating) return;
        _isUpdating = true;
        try
        {
            ApplyAutoNvngxReplacement();
            UpdateDynamicTargetFpsVisibility();
            UpdateDependentControlState();
        }
        finally { _isUpdating = false; }
        UpdateSaveButtonState();
    }

    // Advanced-panel edit: per spec this must never feed back into the top-level Output/Multiplier
    // controls, so it only manages its own dependent DLSS Enabler version panel.
    private void CmbFgNvngxReplacement_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating) return;
        var replacement = GetSelectedTag<FrameGenerationNvngxReplacement>("CmbFgNvngxReplacement");
        var needsVersion = replacement is FrameGenerationNvngxReplacement.Arturs or FrameGenerationNvngxReplacement.Combo;

        var versionPanel = this.FindControl<StackPanel>("PnlDlssEnablerVersion");
        if (versionPanel != null) versionPanel.IsVisible = needsVersion;
        UpdateStreamlineVersionVisibility();

        _isUpdating = true;
        try
        {
            if (needsVersion) PopulateDlssEnablerVersions(_selectedDlssEnablerVersion);
            UpdateDependentControlState();
        }
        finally { _isUpdating = false; }
        UpdateSaveButtonState();
    }

    private void CmbDlssEnablerVersion_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating) return;
        var tag = GetSelectedStringTag("CmbDlssEnablerVersion");
        if (tag == NewDlssEnablerTag)
        {
            // Avalonia's ComboBox crashes if its Items are mutated (Clear/re-add) synchronously
            // from within its own SelectionChanged handler — defer to the next dispatcher cycle,
            // same pattern used for combo repopulation elsewhere in this codebase.
            Dispatcher.UIThread.Post(async () =>
            {
                _isUpdating = true;
                try { PopulateDlssEnablerVersions(_selectedDlssEnablerVersion); }
                finally { _isUpdating = false; }

                var cacheWindow = new CacheManagementWindow(this, "dlss-enabler");
                await cacheWindow.ShowDialog(this);

                _isUpdating = true;
                try { PopulateDlssEnablerVersions(_selectedDlssEnablerVersion); }
                finally { _isUpdating = false; }
                UpdateSaveButtonState();
            });
            return;
        }

        _selectedDlssEnablerVersion = tag;
        UpdateSaveButtonState();
    }

    private void ChkAdvancedRoutes_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_isUpdating) return;
        var isAdvanced = this.FindControl<CheckBox>("ChkAdvancedRoutes")?.IsChecked == true;
        // Leaving Advanced mode must drop back to Auto (unless the simple Output is Disabled) —
        // otherwise a route picked while exploring Advanced routes stays selected even though the
        // combo is hidden again, matching the same invariant BtnSave_Click now enforces.
        var selectedRoute = isAdvanced ? GetSelectedTag<FrameGenerationRoute>("CmbFgRoute")
            : IsOutputDisabledSelected() ? FrameGenerationRoute.Disabled : FrameGenerationRoute.Auto;
        _isUpdating = true;
        try
        {
            PopulateRoutes(selectedRoute);
            UpdateDlssStreamlineRouteInfo();
            UpdateStreamlineVersionVisibility();
            UpdateDependentControlState();
        }
        finally { _isUpdating = false; }
    }

    private void UpdateDependentControlState()
    {
        var enabled = !IsOutputDisabledSelected();
        var multiplier = this.FindControl<ComboBox>("CmbMfgMultiplier");
        var advanced = this.FindControl<StackPanel>("PnlAdvancedOptions");
        var nvngxReplacement = this.FindControl<ComboBox>("CmbFgNvngxReplacement");
        var dlssEnablerVersion = this.FindControl<ComboBox>("CmbDlssEnablerVersion");
        if (multiplier != null)
            multiplier.IsEnabled = enabled && multiplier.Items.Count > 1;
        if (advanced != null) advanced.IsEnabled = enabled;

        var outputIsDlssG = enabled && GetSelectedTag<FrameGenerationOutput>("CmbFgOutput") == FrameGenerationOutput.DlssG;
        if (nvngxReplacement != null) nvngxReplacement.IsEnabled = outputIsDlssG;
        if (dlssEnablerVersion != null) dlssEnablerVersion.IsEnabled = outputIsDlssG;
    }

    private void BtnToggleAdvanced_Click(object? sender, RoutedEventArgs e)
    {
        var content = this.FindControl<StackPanel>("PnlAdvancedOptionsContent");
        var chevron = this.FindControl<TextBlock>("TxtAdvancedChevron");
        if (content == null || chevron == null) return;
        content.IsVisible = !content.IsVisible;
        chevron.Text = content.IsVisible ? "" : "";
    }

    private void UpdateSaveButtonState()
    {
        var saveButton = this.FindControl<Button>("BtnSave");
        if (saveButton == null) return;

        var replacement = GetSelectedTag<FrameGenerationNvngxReplacement>("CmbFgNvngxReplacement");
        var needsVersion = replacement is FrameGenerationNvngxReplacement.Arturs or FrameGenerationNvngxReplacement.Combo;
        var selectedVersion = GetSelectedStringTag("CmbDlssEnablerVersion");
        var hasVersion = !string.IsNullOrEmpty(selectedVersion) && selectedVersion != NewDlssEnablerTag;

        saveButton.IsEnabled = !needsVersion || hasVersion;
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        // CmbFgRoute can be left holding a concrete value (e.g. DLSS-G via Streamline) from an
        // earlier visit to Advanced routes even after the checkbox is unchecked again — toggling
        // Advanced off only re-populates the (hidden) combo, it doesn't clear the selection. Outside
        // Advanced mode the route must always be Auto, or a stale concrete pick silently survives
        // into a "simple mode" save and bypasses ResolveEffectiveRoute's route resolution entirely.
        var isAdvanced = this.FindControl<CheckBox>("ChkAdvancedRoutes")?.IsChecked == true;
        var route = isAdvanced ? GetSelectedTag<FrameGenerationRoute>("CmbFgRoute")
            : IsOutputDisabledSelected() ? FrameGenerationRoute.Disabled : FrameGenerationRoute.Auto;
        if (route == FrameGenerationRoute.Disabled)
        {
            Close(new GameFrameGenerationSettings
            {
                Route = FrameGenerationRoute.Disabled,
                Output = FrameGenerationOutput.Auto,
                MultiFrameMode = MultiFrameGenerationMode.Auto,
                AdvancedMode = this.FindControl<CheckBox>("ChkAdvancedRoutes")?.IsChecked == true,
                DynamicTargetFps = _initialSettings.DynamicTargetFps,
                AppliedAtUtc = _initialSettings.AppliedAtUtc,
                NvngxReplacement = FrameGenerationNvngxReplacement.None,
                DlssEnablerVersion = null,
                StreamlineVersion = null
            });
            return;
        }

        var replacement = GetSelectedTag<FrameGenerationNvngxReplacement>("CmbFgNvngxReplacement");
        var needsVersion = replacement is FrameGenerationNvngxReplacement.Arturs or FrameGenerationNvngxReplacement.Combo;
        var selectedVersion = GetSelectedStringTag("CmbDlssEnablerVersion");
        var multiplier = GetSelectedTag<MultiFrameGenerationMode>("CmbMfgMultiplier");
        double? dynamicTargetFps = null;
        if (multiplier == MultiFrameGenerationMode.Dynamic &&
            double.TryParse(this.FindControl<TextBox>("TxtDynamicTargetFps")?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFps) &&
            parsedFps > 0)
        {
            dynamicTargetFps = parsedFps;
        }

        // Only persisted when this session's settings actually need Streamline — otherwise a value
        // picked while it briefly applied (e.g. before switching Output away from DLSS-G) would
        // linger unused. "Latest version available" is stored as null, same as every other
        // LatestAvailableTag sentinel, and re-resolved to whatever's actually latest at install time.
        var needsStreamline = this.FindControl<StackPanel>("PnlStreamlineVersion")?.IsVisible == true;
        var streamlineTag = GetSelectedStringTag("CmbStreamlineVersion");
        var streamlineVersion = needsStreamline && !string.IsNullOrEmpty(streamlineTag) &&
            streamlineTag != ComponentManagementService.LatestAvailableTag
            ? streamlineTag
            : null;

        Close(new GameFrameGenerationSettings
        {
            Route = route,
            Output = GetSelectedTag<FrameGenerationOutput>("CmbFgOutput"),
            MultiFrameMode = multiplier,
            AdvancedMode = this.FindControl<CheckBox>("ChkAdvancedRoutes")?.IsChecked == true,
            DynamicTargetFps = dynamicTargetFps,
            AppliedAtUtc = _initialSettings.AppliedAtUtc,
            NvngxReplacement = replacement,
            DlssEnablerVersion = needsVersion && !string.IsNullOrEmpty(selectedVersion) && selectedVersion != NewDlssEnablerTag
                ? selectedVersion
                : null,
            StreamlineVersion = streamlineVersion
        });
    }

    private static void SelectTag<T>(ComboBox combo, T selected) where T : struct
    {
        for (var index = 0; index < combo.Items.Count; index++)
        {
            if (combo.Items[index] is ComboBoxItem item && item.Tag is T tag && EqualityComparer<T>.Default.Equals(tag, selected))
            {
                combo.SelectedIndex = index;
                return;
            }
        }
        combo.SelectedIndex = combo.Items.Count > 0 ? 0 : -1;
    }

    private T GetSelectedTag<T>(string name) where T : struct
        => (this.FindControl<ComboBox>(name)?.SelectedItem as ComboBoxItem)?.Tag is T value ? value : default;

    private static void SelectStringTag(ComboBox combo, string selected)
    {
        for (var index = 0; index < combo.Items.Count; index++)
        {
            if (combo.Items[index] is ComboBoxItem item && item.Tag is string tag && tag == selected)
            {
                combo.SelectedIndex = index;
                return;
            }
        }
        combo.SelectedIndex = combo.Items.Count > 0 ? 0 : -1;
    }

    private string GetSelectedStringTag(string name)
        => (this.FindControl<ComboBox>(name)?.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private string GetRouteLabel(FrameGenerationRoute route) => route switch
    {
        FrameGenerationRoute.Auto => Resource("TxtFgRouteAuto", "Auto"),
        FrameGenerationRoute.Disabled => Resource("TxtFgRouteDisabled", "Disabled"),
        FrameGenerationRoute.DlssGStreamline => Resource("TxtFgRouteDlssStreamline", "DLSS-G via Streamline"),
        FrameGenerationRoute.Nukem => Resource("TxtFgRouteNukem", "Nukem DLSS-G → FSR3"),
        FrameGenerationRoute.Fsr31Native => Resource("TxtFgRouteFsr31", "Native FSR 3.1 FG"),
        FrameGenerationRoute.Fsr30Native => Resource("TxtFgRouteFsr30", "Native FSR 3.0 FG"),
        FrameGenerationRoute.OptiFg => Resource("TxtFgRouteOptiFg", "OptiFG"),
        _ => route.ToString()
    };

    private string GetRouteTooltip(FrameGenerationRoute route) => route switch
    {
        FrameGenerationRoute.Auto => Resource("TxtFgRouteAutoTooltip", "Lets OptiScaler choose the route from the game's detected FG input."),
        FrameGenerationRoute.Disabled => Resource("TxtFgRouteDisabledTooltip", "Turns off Frame Generation for this game."),
        FrameGenerationRoute.DlssGStreamline => Resource("TxtFgRouteDlssStreamlineTooltip", "Uses the game's DLSS Frame Generation input through NVIDIA Streamline."),
        FrameGenerationRoute.Nukem => Resource("TxtFgRouteNukemTooltip", "Converts a DLSS Frame Generation input to FSR 3 through NukemFG."),
        FrameGenerationRoute.Fsr31Native => Resource("TxtFgRouteFsr31Tooltip", "Uses the game's native FSR 3.1 Frame Generation input."),
        FrameGenerationRoute.Fsr30Native => Resource("TxtFgRouteFsr30Tooltip", "Uses the game's native FSR 3.0 Frame Generation input."),
        FrameGenerationRoute.OptiFg => Resource("TxtFgRouteOptiFgTooltip", "Uses OptiScaler's experimental built-in Frame Generation route."),
        _ => route.ToString()
    };

    private static string GetOutputLabel(FrameGenerationOutput output) => output switch
    {
        FrameGenerationOutput.Auto => "Auto",
        FrameGenerationOutput.FsrFg => "FSR Frame Generation",
        FrameGenerationOutput.XeFg => "Intel Xe Frame Generation",
        FrameGenerationOutput.Nukem => "Nukem FSR3 FG",
        FrameGenerationOutput.DlssG => "DLSS-G",
        FrameGenerationOutput.DlssGWithNvngx => "DLSS-G + NvNGX",
        _ => output.ToString()
    };

    private static string GetOutputTooltip(FrameGenerationOutput output) => output switch
    {
        FrameGenerationOutput.Auto => Resource("TxtFgOutputAutoTooltip", "Keeps the output selected automatically by OptiScaler."),
        FrameGenerationOutput.FsrFg => Resource("TxtFgOutputFsrTooltip", "Generates frames with AMD FSR Frame Generation."),
        FrameGenerationOutput.XeFg => Resource("TxtFgOutputXeTooltip", "Generates frames with Intel Xe Frame Generation."),
        FrameGenerationOutput.Nukem => Resource("TxtFgOutputNukemTooltip", "Uses the NukemFG FSR 3 output."),
        FrameGenerationOutput.DlssG => Resource("TxtFgOutputDlssTooltip", "Outputs NVIDIA DLSS Frame Generation."),
        FrameGenerationOutput.DlssGWithNvngx => Resource("TxtFgOutputDlssNvngxTooltip", "Outputs DLSS Frame Generation through NVIDIA's NVNGX runtime."),
        _ => output.ToString()
    };

    private static string GetNvngxReplacementLabel(FrameGenerationNvngxReplacement replacement) => replacement switch
    {
        FrameGenerationNvngxReplacement.None => Resource("TxtFgNvngxReplacementNone", "None"),
        FrameGenerationNvngxReplacement.Nukems => Resource("TxtFgNvngxReplacementNukems", "Nukem"),
        FrameGenerationNvngxReplacement.Ffx => Resource("TxtFgNvngxReplacementFfx", "FSR 3/4 FG"),
        FrameGenerationNvngxReplacement.Arturs => Resource("TxtFgNvngxReplacementArturs", "Enabler"),
        FrameGenerationNvngxReplacement.Combo => Resource("TxtFgNvngxReplacementCombo", "FFX + Enabler"),
        _ => replacement.ToString()
    };

    private static string GetNvngxReplacementTooltip(FrameGenerationNvngxReplacement replacement) => replacement switch
    {
        FrameGenerationNvngxReplacement.None => Resource("TxtFgNvngxReplacementNoneTooltip", "No NVNGX replacement provider."),
        FrameGenerationNvngxReplacement.Nukems => Resource("TxtFgNvngxReplacementNukemsTooltip", "Uses NukemFG as the NVNGX/DLSS-G replacement."),
        FrameGenerationNvngxReplacement.Ffx => Resource("TxtFgNvngxReplacementFfxTooltip", "Uses AMD FSR 3/4 Frame Generation as the NVNGX/DLSS-G replacement."),
        FrameGenerationNvngxReplacement.Arturs => Resource("TxtFgNvngxReplacementArtursTooltip", "Uses DLSS Enabler (headless) for MFG up to x6, requires dlss-enabler-headless.dll."),
        FrameGenerationNvngxReplacement.Combo => Resource("TxtFgNvngxReplacementComboTooltip", "Combines FSR FG for middle frames with DLSS Enabler for the rest, allows MFG up to x6."),
        _ => replacement.ToString()
    };

    private static string GetMfgLabel(MultiFrameGenerationMode mode) => mode switch
    {
        MultiFrameGenerationMode.Auto => "Auto",
        MultiFrameGenerationMode.Dynamic => "Dynamic",
        _ => mode.ToString().Replace("X", "x")
    };

    private static string GetMfgTooltip(MultiFrameGenerationMode mode) => mode switch
    {
        MultiFrameGenerationMode.Auto => Resource("TxtMfgAutoTooltip", "Lets OptiScaler choose the appropriate multiplier."),
        MultiFrameGenerationMode.Dynamic => Resource("TxtMfgDynamicTooltip", "Adjusts the multiplier dynamically when supported."),
        _ => string.Format(Resource("TxtMfgFixedTooltipFmt", "Generates up to {0} frames for each rendered frame when supported."), GetMfgLabel(mode))
    };

    private static string Resource(string key, string fallback)
        => Application.Current?.TryFindResource(key, out var value) == true && value is string text ? text : fallback;
}
