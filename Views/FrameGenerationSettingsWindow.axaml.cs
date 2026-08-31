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

    private readonly FrameGenerationCapabilities _capabilities = new();
    private readonly GameFrameGenerationSettings _initialSettings = new();
    private bool _isUpdating;
    private GamepadDialogNavigationHelper? _gamepadHelper;

    GamepadHelperBase? IGamepadInputHost.GamepadHelper => _gamepadHelper;

    public FrameGenerationSettingsWindow()
    {
        InitializeComponent();
        DialogDimHelper.Register(this);
    }

    public FrameGenerationSettingsWindow(Window owner, Game game, GpuInfo? gpu)
    {
        InitializeComponent();
        DialogDimHelper.Register(this);

        var service = new FrameGenerationConfigurationService();
        _capabilities = service.DetectCapabilities(game, gpu);
        var saved = game.FrameGenerationSettings;
        _initialSettings = new GameFrameGenerationSettings
        {
            Route = saved?.Route ?? FrameGenerationRoute.Disabled,
            Output = saved?.Output ?? FrameGenerationOutput.Auto,
            MultiFrameMode = saved?.MultiFrameMode ?? MultiFrameGenerationMode.X2,
            AdvancedMode = saved?.AdvancedMode ?? false,
            DynamicTargetFps = saved?.DynamicTargetFps,
            AppliedAtUtc = saved?.AppliedAtUtc
        };

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
            PopulateMfgModes(_initialSettings.MultiFrameMode);
            UpdateDependentControlState();
        }
        finally
        {
            _isUpdating = false;
        }
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
            combo.Items.Add(new ComboBoxItem { Content = GetRouteLabel(route), Tag = route });
        SelectTag(combo, selected);
    }

    private void PopulateOutputs(FrameGenerationOutput selected)
    {
        var combo = this.FindControl<ComboBox>("CmbFgOutput");
        if (combo == null) return;
        combo.Items.Clear();
        foreach (var output in _capabilities.AvailableOutputs)
            combo.Items.Add(new ComboBoxItem { Content = GetOutputLabel(output), Tag = output });
        SelectTag(combo, selected);
    }

    private void PopulateMfgModes(MultiFrameGenerationMode selected)
    {
        var combo = this.FindControl<ComboBox>("CmbMfgMultiplier");
        if (combo == null) return;

        var output = GetSelectedTag<FrameGenerationOutput>("CmbFgOutput");
        var route = GetSelectedTag<FrameGenerationRoute>("CmbFgRoute");
        IReadOnlyList<MultiFrameGenerationMode> modes = output == FrameGenerationOutput.XeFg
            ? _capabilities.AvailableMfgModes
            : [MultiFrameGenerationMode.X2];

        combo.Items.Clear();
        foreach (var mode in modes)
            combo.Items.Add(new ComboBoxItem { Content = GetMfgLabel(mode), Tag = mode });
        combo.IsEnabled = route != FrameGenerationRoute.Disabled && modes.Count > 1;
        SelectTag(combo, selected);
    }

    private void CmbFgRoute_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating) return;
        UpdateDependentControlState();
    }

    private void CmbFgOutput_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating) return;
        var selectedMfg = GetSelectedTag<MultiFrameGenerationMode>("CmbMfgMultiplier");
        _isUpdating = true;
        try { PopulateMfgModes(selectedMfg); }
        finally { _isUpdating = false; }
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
        if (output != null) output.IsEnabled = enabled;
        if (multiplier != null)
            multiplier.IsEnabled = enabled && multiplier.Items.Count > 1;
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        Close(new GameFrameGenerationSettings
        {
            Route = GetSelectedTag<FrameGenerationRoute>("CmbFgRoute"),
            Output = GetSelectedTag<FrameGenerationOutput>("CmbFgOutput"),
            MultiFrameMode = GetSelectedTag<MultiFrameGenerationMode>("CmbMfgMultiplier"),
            AdvancedMode = this.FindControl<CheckBox>("ChkAdvancedRoutes")?.IsChecked == true,
            DynamicTargetFps = _initialSettings.DynamicTargetFps,
            AppliedAtUtc = _initialSettings.AppliedAtUtc
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

    private static string GetMfgLabel(MultiFrameGenerationMode mode) => mode switch
    {
        MultiFrameGenerationMode.Auto => "Auto",
        MultiFrameGenerationMode.Dynamic => "Dynamic",
        _ => mode.ToString().Replace("X", "x")
    };

    private static string Resource(string key, string fallback)
        => Application.Current?.TryFindResource(key, out var value) == true && value is string text ? text : fallback;
}
