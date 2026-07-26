using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OptiscalerClient.Models;
using OptiscalerClient.Services;

namespace OptiscalerClient.Helpers;

public class GamepadDialogNavigationHelper : GamepadHelperBase, IDisposable
{
    private readonly ScrollViewer? _scrollViewer;
    private readonly IGamepadDetectionService? _gamepadService;
    private bool _isDisposed;

    private readonly DispatcherTimer _scrollTimer;
    private bool _isRightStickUpHeld;
    private bool _isRightStickDownHeld;
    private double _scrollVelocity;

    public Func<GamepadButton, bool>? CustomNavigationHandler { get; set; }

    public GamepadDialogNavigationHelper(Window window, ScrollViewer? scrollViewer) : base(window)
    {
        _scrollViewer = scrollViewer;

        _scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _scrollTimer.Tick += ScrollTimer_Tick;

        _gamepadService = PlatformServiceFactory.CreateGamepadDetectionService();
        if (_gamepadService != null)
        {
            _gamepadService.GamepadInputReceived += OnGamepadInput;
            _gamepadService.StartListening();
        }
    }

    private void OnGamepadInput(object? sender, GamepadEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsInputSuspended) return;

            if (HandleRightStickVerticalInput(e))
                return;

            if (!e.IsPressed) return;
            if (!_window.IsVisible) return;
            if (HasOpenOwnedWindow()) return;

            MarkGamepadModeActive();

            // While a ComboBox popup is open, D-Pad/left-stick up/down should
            // step through its items instead of Tab-ing away, and A/B should
            // close it and restore focus to the ComboBox itself. Mirrors the
            // fix already applied to GamepadNavigationHelper's Settings tab
            // (see gamepad_implementation_log.md, section 15).
            var openCombo = _window.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(x => x.IsDropDownOpen);
            if (openCombo != null)
            {
                switch (e.Button)
                {
                    case GamepadButton.DPadDown:
                    case GamepadButton.ThumbLeftDown:
                        if (openCombo.SelectedIndex < openCombo.ItemCount - 1)
                            openCombo.SelectedIndex++;
                        return;

                    case GamepadButton.DPadUp:
                    case GamepadButton.ThumbLeftUp:
                        if (openCombo.SelectedIndex > 0)
                            openCombo.SelectedIndex--;
                        return;

                    case GamepadButton.A:
                    case GamepadButton.B:
                        openCombo.IsDropDownOpen = false;
                        Dispatcher.UIThread.Post(() => openCombo.Focus(NavigationMethod.Directional), DispatcherPriority.Background);
                        return;
                }
                return;
            }

            bool handled = CustomNavigationHandler?.Invoke(e.Button) ?? false;
            if (handled) return;

            switch (e.Button)
            {
                case GamepadButton.DPadDown:
                case GamepadButton.ThumbLeftDown:
                case GamepadButton.DPadRight:
                case GamepadButton.ThumbLeftRight:
                    SimulateKey(Key.Tab);
                    break;

                case GamepadButton.DPadUp:
                case GamepadButton.ThumbLeftUp:
                case GamepadButton.DPadLeft:
                case GamepadButton.ThumbLeftLeft:
                    SimulateKey(Key.Tab, KeyModifiers.Shift);
                    break;

                case GamepadButton.A:
                    ActivateFocusedElement();
                    break;

                case GamepadButton.B:
                    _window.Close(null);
                    break;
            }
        });
    }

    private bool HandleRightStickVerticalInput(GamepadEventArgs e)
    {
        if (e.Button != GamepadButton.ThumbRightUp && e.Button != GamepadButton.ThumbRightDown)
            return false;

        if (e.Button == GamepadButton.ThumbRightUp)
            _isRightStickUpHeld = e.IsPressed;
        else
            _isRightStickDownHeld = e.IsPressed;

        if (e.IsPressed)
            MarkGamepadModeActive();

        UpdateScrollTimerState();
        return true;
    }

    private void UpdateScrollTimerState()
    {
        bool hasDirection = _isRightStickUpHeld ^ _isRightStickDownHeld;
        bool shouldScroll = hasDirection && _scrollViewer != null && _window.IsVisible;

        if (shouldScroll)
        {
            if (!_scrollTimer.IsEnabled)
            {
                _scrollVelocity = 0;
                _scrollTimer.Start();
                ScrollViewport(_isRightStickUpHeld ? -10.0 : 10.0);
            }
            return;
        }

        if (_scrollTimer.IsEnabled)
            _scrollTimer.Stop();

        _scrollVelocity = 0;
    }

    private void ScrollTimer_Tick(object? sender, EventArgs e)
    {
        if (_isRightStickUpHeld == _isRightStickDownHeld || _scrollViewer == null || !_window.IsVisible)
        {
            UpdateScrollTimerState();
            return;
        }

        _scrollVelocity = Math.Min(28.0, _scrollVelocity + 1.5);
        double delta = 6.0 + _scrollVelocity;

        if (_isRightStickUpHeld)
            delta = -delta;

        ScrollViewport(delta);
    }

    private void ScrollViewport(double deltaY)
    {
        if (_scrollViewer == null || !_scrollViewer.IsVisible)
            return;

        double currentY = _scrollViewer.Offset.Y;
        double maxY = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
        double targetY = Math.Clamp(currentY + deltaY, 0, maxY);

        _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, targetY);
    }

    private void ActivateFocusedElement()
    {
        // 1. Find the element that currently has focus
        var allDescendants = _window.GetVisualDescendants().ToList();
        var focused = allDescendants.OfType<Avalonia.Controls.Control>().FirstOrDefault(x => x.IsFocused);

        if (focused != null)
        {
            // 2. See if the focused element is a ToggleSwitch/RadioButton/Button itself
            // (e.g. dynamically generated drives that still have Focusable=True)
            if (focused is ToggleSwitch t) { t.IsChecked = !(t.IsChecked ?? false); return; }
            if (focused is RadioButton r) { r.IsChecked = true; return; }
            if (focused is CheckBox c) { c.IsChecked = !(c.IsChecked ?? false); return; }
            if (focused is ComboBox cb) { cb.IsDropDownOpen = !cb.IsDropDownOpen; return; }
            if (focused is Button b) { b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return; }

            // 3. See if the focused element is a container (like our Border InteractiveOption)
            // that contains one of the interactive controls.
            var childTgl = focused.GetVisualDescendants().OfType<ToggleSwitch>().FirstOrDefault();
            if (childTgl != null) { childTgl.IsChecked = !(childTgl.IsChecked ?? false); return; }

            var childRb = focused.GetVisualDescendants().OfType<RadioButton>().FirstOrDefault();
            if (childRb != null) { childRb.IsChecked = true; return; }

            var childChk = focused.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault();
            if (childChk != null) { childChk.IsChecked = !(childChk.IsChecked ?? false); return; }

            var childBtn = focused.GetVisualDescendants().OfType<Button>().FirstOrDefault();
            if (childBtn != null) { childBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return; }
        }

        SimulateKey(Key.Enter);
        SimulateKey(Key.Space);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _scrollTimer.Stop();
        if (_gamepadService != null)
        {
            _gamepadService.GamepadInputReceived -= OnGamepadInput;
            _gamepadService.StopListening();
        }
        UnhookGamepadModeDetection();
    }
}
