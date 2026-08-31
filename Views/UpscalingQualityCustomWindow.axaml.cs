using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using OptiscalerClient.Helpers;

namespace OptiscalerClient.Views;

public partial class UpscalingQualityCustomWindow : Window, IGamepadInputHost
{
    private readonly PixelSize _outputResolution;
    private bool _isUpdating;
    private bool _controlsReady;
    private GamepadDialogNavigationHelper? _gamepadHelper;

    GamepadHelperBase? IGamepadInputHost.GamepadHelper => _gamepadHelper;

    public UpscalingQualityCustomWindow()
    {
        InitializeComponent();
        DialogDimHelper.Register(this);
        _outputResolution = new PixelSize(2560, 1440);
        _controlsReady = true;
        SetRatio(1.5);
    }

    public UpscalingQualityCustomWindow(Window owner, double initialRatio, PixelSize outputResolution)
    {
        InitializeComponent();
        DialogDimHelper.Register(this);
        _outputResolution = outputResolution.Width > 0 && outputResolution.Height > 0
            ? outputResolution
            : new PixelSize(2560, 1440);
        _controlsReady = true;

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

        SetRatio(initialRatio);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void SetRatio(double value)
    {
        var ratio = Math.Round(Math.Clamp(value, 1.0, 3.0), 2);
        _isUpdating = true;
        try
        {
            var slider = this.FindControl<Slider>("SldQualityRatio");
            var textBox = this.FindControl<TextBox>("TxtQualityRatio");
            if (slider != null) slider.Value = ratio;
            if (textBox != null) textBox.Text = ratio.ToString("0.00", CultureInfo.InvariantCulture);
        }
        finally
        {
            _isUpdating = false;
        }
        UpdateResolutionPreview(ratio);
    }

    private void SldQualityRatio_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_controlsReady || _isUpdating) return;
        SetRatio(e.NewValue);
    }

    private void TxtQualityRatio_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_controlsReady || _isUpdating || sender is not TextBox textBox
            || !TryParseRatio(textBox.Text, out var ratio)
            || ratio < 1.0 || ratio > 3.0)
            return;

        ratio = Math.Round(ratio, 2);
        _isUpdating = true;
        try
        {
            var slider = this.FindControl<Slider>("SldQualityRatio");
            if (slider != null) slider.Value = ratio;
        }
        finally
        {
            _isUpdating = false;
        }
        UpdateResolutionPreview(ratio);
    }

    private void TxtQualityRatio_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (!_controlsReady || sender is not TextBox textBox) return;
        if (TryParseRatio(textBox.Text, out var ratio))
            SetRatio(ratio);
        else
            SetRatio(this.FindControl<Slider>("SldQualityRatio")?.Value ?? 1.5);
    }

    private static bool TryParseRatio(string? text, out double ratio)
        => double.TryParse(
            text?.Trim().Replace(',', '.'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out ratio);

    private void UpdateResolutionPreview(double ratio)
    {
        var inputWidth = (int)Math.Round(_outputResolution.Width / ratio, MidpointRounding.AwayFromZero);
        var inputHeight = (int)Math.Round(_outputResolution.Height / ratio, MidpointRounding.AwayFromZero);
        var input = this.FindControl<TextBlock>("TxtInputResolution");
        var output = this.FindControl<TextBlock>("TxtOutputResolution");
        if (input != null) input.Text = $"{inputWidth} × {inputHeight}";
        if (output != null) output.Text = $"{_outputResolution.Width} × {_outputResolution.Height}";
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        var text = this.FindControl<TextBox>("TxtQualityRatio")?.Text;
        var value = TryParseRatio(text, out var parsed)
            ? parsed
            : this.FindControl<Slider>("SldQualityRatio")?.Value ?? 1.5;
        Close((double?)Math.Round(Math.Clamp(value, 1.0, 3.0), 2));
    }
}
