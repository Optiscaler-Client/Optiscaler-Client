using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
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

    private readonly FrameGenerationCapabilities _capabilities = new();
    private readonly GameFrameGenerationSettings _initialSettings = new();
    private string _selectedDlssEnablerVersion = "";
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
            game.FrameGenerationSettings)
    {
    }

    public FrameGenerationSettingsWindow(
        Window owner,
        FrameGenerationCapabilities capabilities,
        GameFrameGenerationSettings? saved)
    {
        InitializeComponent();
        DialogDimHelper.Register(this);

        _capabilities = capabilities;
        _initialSettings = new GameFrameGenerationSettings
        {
            Route = saved?.Route ?? FrameGenerationRoute.Disabled,
            Output = saved?.Output ?? FrameGenerationOutput.Auto,
            MultiFrameMode = saved?.MultiFrameMode ?? MultiFrameGenerationMode.Auto,
            AdvancedMode = saved?.AdvancedMode ?? false,
            DynamicTargetFps = saved?.DynamicTargetFps,
            AppliedAtUtc = saved?.AppliedAtUtc,
            NvngxReplacement = saved?.NvngxReplacement ?? FrameGenerationNvngxReplacement.None,
            DlssEnablerVersion = saved?.DlssEnablerVersion
        };
        _selectedDlssEnablerVersion = _initialSettings.DlssEnablerVersion ?? "";

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
            PopulateOutputs(_initialSettings.Output);
            PopulateNvngxReplacements(_initialSettings.NvngxReplacement);
            var needsVersion = _initialSettings.NvngxReplacement is FrameGenerationNvngxReplacement.Arturs or FrameGenerationNvngxReplacement.Combo;
            var versionPanel = this.FindControl<StackPanel>("PnlDlssEnablerVersion");
            if (versionPanel != null) versionPanel.IsVisible = needsVersion;
            if (needsVersion) PopulateDlssEnablerVersions(_selectedDlssEnablerVersion);
            PopulateMfgModes(_initialSettings.MultiFrameMode);
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

    private void PopulateOutputs(FrameGenerationOutput selected)
    {
        var combo = this.FindControl<ComboBox>("CmbFgOutput");
        if (combo == null) return;
        combo.Items.Clear();
        foreach (var output in _capabilities.AvailableOutputs)
        {
            var item = new ComboBoxItem { Content = GetOutputLabel(output), Tag = output };
            ToolTip.SetTip(item, GetOutputTooltip(output));
            combo.Items.Add(item);
        }
        SelectTag(combo, selected);
    }

    private void PopulateMfgModes(MultiFrameGenerationMode selected)
    {
        var combo = this.FindControl<ComboBox>("CmbMfgMultiplier");
        if (combo == null) return;

        var output = GetSelectedTag<FrameGenerationOutput>("CmbFgOutput");
        var route = GetSelectedTag<FrameGenerationRoute>("CmbFgRoute");
        var replacement = GetSelectedTag<FrameGenerationNvngxReplacement>("CmbFgNvngxReplacement");
        IReadOnlyList<MultiFrameGenerationMode> modes = new FrameGenerationConfigurationService()
            .GetAvailableMfgModes(route, output, _capabilities, replacement);

        combo.Items.Clear();
        foreach (var mode in modes)
        {
            var item = new ComboBoxItem { Content = GetMfgLabel(mode), Tag = mode };
            ToolTip.SetTip(item, GetMfgTooltip(mode));
            combo.Items.Add(item);
        }
        combo.IsEnabled = route != FrameGenerationRoute.Disabled && modes.Count > 1;
        SelectTag(combo, selected);
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

        combo.Items.Clear();
        combo.Items.Add(new ComboBoxItem { Content = Resource("TxtSelectVersion", "-- Select version --"), Tag = "" });
        foreach (var version in new ComponentManagementService().GetDownloadedDlssEnablerVersions())
            combo.Items.Add(new ComboBoxItem { Content = version, Tag = version });
        combo.Items.Add(new ComboBoxItem { Content = Resource("TxtNewOrImport", "New / Import..."), Tag = NewDlssEnablerTag });

        SelectStringTag(combo, selected);
    }

    private void CmbFgRoute_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating) return;
        var selectedMfg = GetSelectedTag<MultiFrameGenerationMode>("CmbMfgMultiplier");
        _isUpdating = true;
        try
        {
            PopulateMfgModes(selectedMfg);
            UpdateDependentControlState();
        }
        finally { _isUpdating = false; }
    }

    private void CmbFgOutput_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating) return;
        var selectedMfg = GetSelectedTag<MultiFrameGenerationMode>("CmbMfgMultiplier");
        var selectedReplacement = GetSelectedTag<FrameGenerationNvngxReplacement>("CmbFgNvngxReplacement");
        _isUpdating = true;
        try
        {
            PopulateNvngxReplacements(selectedReplacement);
            var replacement = GetSelectedTag<FrameGenerationNvngxReplacement>("CmbFgNvngxReplacement");
            var needsVersion = replacement is FrameGenerationNvngxReplacement.Arturs or FrameGenerationNvngxReplacement.Combo;
            var versionPanel = this.FindControl<StackPanel>("PnlDlssEnablerVersion");
            if (versionPanel != null) versionPanel.IsVisible = needsVersion;
            if (needsVersion) PopulateDlssEnablerVersions(_selectedDlssEnablerVersion);
            PopulateMfgModes(selectedMfg);
            UpdateDependentControlState();
        }
        finally { _isUpdating = false; }
        UpdateSaveButtonState();
    }

    private void CmbFgNvngxReplacement_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating) return;
        var replacement = GetSelectedTag<FrameGenerationNvngxReplacement>("CmbFgNvngxReplacement");
        var needsVersion = replacement is FrameGenerationNvngxReplacement.Arturs or FrameGenerationNvngxReplacement.Combo;

        var versionPanel = this.FindControl<StackPanel>("PnlDlssEnablerVersion");
        if (versionPanel != null) versionPanel.IsVisible = needsVersion;

        var selectedMfg = GetSelectedTag<MultiFrameGenerationMode>("CmbMfgMultiplier");
        _isUpdating = true;
        try
        {
            if (needsVersion) PopulateDlssEnablerVersions(_selectedDlssEnablerVersion);
            PopulateMfgModes(selectedMfg);
            UpdateDependentControlState();
        }
        finally { _isUpdating = false; }
        UpdateSaveButtonState();
    }

    private async void CmbDlssEnablerVersion_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating) return;
        var tag = GetSelectedStringTag("CmbDlssEnablerVersion");
        if (tag == NewDlssEnablerTag)
        {
            _isUpdating = true;
            try { PopulateDlssEnablerVersions(_selectedDlssEnablerVersion); }
            finally { _isUpdating = false; }

            var cacheWindow = new CacheManagementWindow(this, "dlss-enabler");
            await cacheWindow.ShowDialog(this);

            _isUpdating = true;
            try { PopulateDlssEnablerVersions(_selectedDlssEnablerVersion); }
            finally { _isUpdating = false; }
        }
        else
        {
            _selectedDlssEnablerVersion = tag;
        }
        UpdateSaveButtonState();
    }

    private void ChkAdvancedRoutes_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_isUpdating) return;
        var selectedRoute = GetSelectedTag<FrameGenerationRoute>("CmbFgRoute");
        _isUpdating = true;
        try
        {
            PopulateRoutes(selectedRoute);
            UpdateDependentControlState();
        }
        finally { _isUpdating = false; }
    }

    private void UpdateDependentControlState()
    {
        var enabled = GetSelectedTag<FrameGenerationRoute>("CmbFgRoute") != FrameGenerationRoute.Disabled;
        var output = this.FindControl<ComboBox>("CmbFgOutput");
        var multiplier = this.FindControl<ComboBox>("CmbMfgMultiplier");
        var nvngxReplacement = this.FindControl<ComboBox>("CmbFgNvngxReplacement");
        var dlssEnablerVersion = this.FindControl<ComboBox>("CmbDlssEnablerVersion");
        if (output != null) output.IsEnabled = enabled;
        if (multiplier != null)
            multiplier.IsEnabled = enabled && multiplier.Items.Count > 1;

        var outputIsDlssG = enabled && GetSelectedTag<FrameGenerationOutput>("CmbFgOutput") == FrameGenerationOutput.DlssG;
        if (nvngxReplacement != null) nvngxReplacement.IsEnabled = outputIsDlssG;
        if (dlssEnablerVersion != null) dlssEnablerVersion.IsEnabled = outputIsDlssG;
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
        var replacement = GetSelectedTag<FrameGenerationNvngxReplacement>("CmbFgNvngxReplacement");
        var needsVersion = replacement is FrameGenerationNvngxReplacement.Arturs or FrameGenerationNvngxReplacement.Combo;
        var selectedVersion = GetSelectedStringTag("CmbDlssEnablerVersion");

        Close(new GameFrameGenerationSettings
        {
            Route = GetSelectedTag<FrameGenerationRoute>("CmbFgRoute"),
            Output = GetSelectedTag<FrameGenerationOutput>("CmbFgOutput"),
            MultiFrameMode = GetSelectedTag<MultiFrameGenerationMode>("CmbMfgMultiplier"),
            AdvancedMode = this.FindControl<CheckBox>("ChkAdvancedRoutes")?.IsChecked == true,
            DynamicTargetFps = _initialSettings.DynamicTargetFps,
            AppliedAtUtc = _initialSettings.AppliedAtUtc,
            NvngxReplacement = replacement,
            DlssEnablerVersion = needsVersion && !string.IsNullOrEmpty(selectedVersion) && selectedVersion != NewDlssEnablerTag
                ? selectedVersion
                : null
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
        FrameGenerationRoute.OptiFg => Resource("TxtFgRouteOptiFg", "OptiFG (experimental)"),
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
