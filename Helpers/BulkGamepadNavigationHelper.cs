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

public class BulkGamepadNavigationHelper : GamepadHelperBase, IDisposable
{
    private readonly ScrollViewer? _scrollViewer;
    private readonly IGamepadDetectionService? _gamepadService;
    private bool _isDisposed;
    private IInputElement? _lastSidebarFocus;

    private readonly DispatcherTimer _scrollTimer;
    private bool _isRightStickUpHeld;
    private bool _isRightStickDownHeld;
    private double _scrollVelocity;

    public BulkGamepadNavigationHelper(Window window, ScrollViewer? scrollViewer) : base(window)
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

            var focused = TopLevel.GetTopLevel(_window)?.FocusManager?.GetFocusedElement() as Avalonia.Controls.Control;

            switch (e.Button)
            {
                case GamepadButton.DPadDown:
                case GamepadButton.ThumbLeftDown:
                    var openComboDown = GetOpenComboBox();
                    if (openComboDown != null)
                    {
                        if (openComboDown.SelectedIndex < openComboDown.ItemCount - 1)
                            openComboDown.SelectedIndex++;
                        break;
                    }

                    if (IsInGamesList(focused))
                    {
                        if (!IsControlVisibleInScrollViewer(focused))
                        {
                            FocusFirstVisibleGame(false);
                            break;
                        }
                        
                        // Prevent wrapping at the end of the list
                        var gamesList = _window.FindControl<ItemsControl>("GamesList");
                        if (gamesList != null)
                        {
                            var lastItem = gamesList.GetVisualDescendants().OfType<Border>().LastOrDefault(b => b.Classes.Contains("InteractiveOption"));
                            if (focused == lastItem)
                                return;
                        }
                    }
                    
                    if (focused?.Name == "BtnOptiStable" || focused?.Name == "BtnOptiBeta" || focused?.Name == "BtnOptiCustom")
                    {
                        var cmb = _window.FindControl<ComboBox>("CmbOptiVersion");
                        cmb?.Focus(NavigationMethod.Directional);
                    }
                    else
                    {
                        SimulateKey(Key.Tab);
                    }
                    break;

                case GamepadButton.DPadUp:
                case GamepadButton.ThumbLeftUp:
                    var openComboUp = GetOpenComboBox();
                    if (openComboUp != null)
                    {
                        if (openComboUp.SelectedIndex > 0)
                            openComboUp.SelectedIndex--;
                        break;
                    }

                    if (IsInGamesList(focused) && !IsControlVisibleInScrollViewer(focused))
                    {
                        FocusFirstVisibleGame(true);
                        break;
                    }
                    if (focused?.Name == "CmbOptiVersion")
                    {
                        var btn = _window.FindControl<Button>("BtnOptiStable");
                        btn?.Focus(NavigationMethod.Directional);
                    }
                    else
                    {
                        SimulateKey(Key.Tab, KeyModifiers.Shift);
                    }
                    break;

                case GamepadButton.DPadLeft:
                case GamepadButton.ThumbLeftLeft:
                    if (IsInGamesList(focused))
                    {
                        // Return to sidebar remembering last focus
                        if (_lastSidebarFocus != null)
                        {
                            _lastSidebarFocus.Focus(NavigationMethod.Directional);
                        }
                        else
                        {
                            _window.FindControl<Control>("TxtSearch")?.Focus(NavigationMethod.Directional);
                        }
                    }
                    else if (focused?.Name == "TxtSearch")
                    {
                        _window.FindControl<Control>("BorderSelectAll")?.Focus(NavigationMethod.Directional);
                    }
                    else if (focused?.Name == "BtnOptiBeta")
                    {
                        _window.FindControl<Control>("BtnOptiStable")?.Focus(NavigationMethod.Directional);
                    }
                    else if (focused?.Name == "BtnOptiCustom")
                    {
                        var btnBeta = _window.FindControl<Control>("BtnOptiBeta");
                        if (btnBeta != null && btnBeta.IsVisible)
                            btnBeta.Focus(NavigationMethod.Directional);
                        else
                            _window.FindControl<Control>("BtnOptiStable")?.Focus(NavigationMethod.Directional);
                    }
                    else
                    {
                        SimulateKey(Key.Tab, KeyModifiers.Shift);
                    }
                    break;

                case GamepadButton.DPadRight:
                case GamepadButton.ThumbLeftRight:
                    if (!IsInGamesList(focused))
                    {
                        if (focused?.Name == "BorderSelectAll" || focused?.Name == "ChkSelectAll")
                        {
                            _window.FindControl<Control>("TxtSearch")?.Focus(NavigationMethod.Directional);
                        }
                        else if (focused?.Name == "BtnOptiStable")
                        {
                            var btnBeta = _window.FindControl<Control>("BtnOptiBeta");
                            if (btnBeta != null && btnBeta.IsVisible)
                                btnBeta.Focus(NavigationMethod.Directional);
                            else
                            {
                                var btnCustom = _window.FindControl<Control>("BtnOptiCustom");
                                if (btnCustom != null && btnCustom.IsVisible)
                                    btnCustom.Focus(NavigationMethod.Directional);
                                else
                                {
                                    _lastSidebarFocus = focused;
                                    FocusFirstGame();
                                }
                            }
                        }
                        else if (focused?.Name == "BtnOptiBeta")
                        {
                            var btnCustom = _window.FindControl<Control>("BtnOptiCustom");
                            if (btnCustom != null && btnCustom.IsVisible)
                                btnCustom.Focus(NavigationMethod.Directional);
                            else
                            {
                                _lastSidebarFocus = focused;
                                FocusFirstGame();
                            }
                        }
                        else
                        {
                            _lastSidebarFocus = focused;
                            FocusFirstGame();
                        }
                    }
                    else
                    {
                        SimulateKey(Key.Tab);
                    }
                    break;

                case GamepadButton.A:
                    var openComboA = GetOpenComboBox();
                    if (openComboA != null)
                    {
                        openComboA.IsDropDownOpen = false;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => openComboA.Focus(Avalonia.Input.NavigationMethod.Directional), Avalonia.Threading.DispatcherPriority.Background);
                        break;
                    }
                    ActivateFocusedElement();
                    break;

                case GamepadButton.B:
                    // If dropdown open, B should close it.
                    var openComboB = GetOpenComboBox();
                    if (openComboB != null)
                    {
                        openComboB.IsDropDownOpen = false;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => openComboB.Focus(Avalonia.Input.NavigationMethod.Directional), Avalonia.Threading.DispatcherPriority.Background);
                        return;
                    }
                    _window.Close(null);
                    break;
            }
        });
    }

    private ComboBox? GetOpenComboBox()
    {
        return _window.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(c => c.IsDropDownOpen);
    }

    private bool IsInGamesList(Avalonia.Controls.Control? control)
    {
        if (control == null) return false;
        var scrollView = _window.FindControl<ScrollViewer>("GamesScrollViewer");
        if (scrollView == null) return false;
        
        var ancestors = control.GetVisualAncestors();
        return ancestors.Contains(scrollView);
    }

    private bool IsControlVisibleInScrollViewer(Avalonia.Controls.Control? control) =>
        IsControlVisibleInScrollViewer(control, _scrollViewer);

    private void FocusFirstVisibleGame(bool reverse)
    {
        var gamesList = _window.FindControl<ItemsControl>("GamesList");
        if (gamesList != null)
        {
            var items = gamesList.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("InteractiveOption")).ToList();
            if (reverse) items.Reverse();

            Border? firstVisibleItem = null;
            double firstVisibleY = -1;

            foreach (var item in items)
            {
                if (IsControlVisibleInScrollViewer(item))
                {
                    if (firstVisibleItem == null)
                    {
                        firstVisibleItem = item;
                        firstVisibleY = item.Bounds.Top;
                    }
                    else
                    {
                        if (Math.Abs(item.Bounds.Top - firstVisibleY) >= 5.0)
                        {
                            item.Focus(NavigationMethod.Directional);
                            return;
                        }
                    }
                }
            }

            if (firstVisibleItem != null)
            {
                firstVisibleItem.Focus(NavigationMethod.Directional);
                return;
            }
        }
        SimulateKey(Key.Tab);
    }

    private void FocusFirstGame()
    {
        var gamesList = _window.FindControl<ItemsControl>("GamesList");
        if (gamesList != null)
        {
            var items = gamesList.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("InteractiveOption")).ToList();
            
            // Prefer second visible item to avoid snapping back if user scrolled
            Border? firstVisibleItem = null;
            double firstVisibleY = -1;

            foreach (var item in items)
            {
                if (IsControlVisibleInScrollViewer(item))
                {
                    if (firstVisibleItem == null)
                    {
                        firstVisibleItem = item;
                        firstVisibleY = item.Bounds.Top;
                    }
                    else
                    {
                        if (Math.Abs(item.Bounds.Top - firstVisibleY) >= 5.0)
                        {
                            item.Focus(NavigationMethod.Directional);
                            return;
                        }
                    }
                }
            }

            if (firstVisibleItem != null)
            {
                firstVisibleItem.Focus(NavigationMethod.Directional);
                return;
            }

            var firstItem = items.FirstOrDefault();
            if (firstItem != null)
            {
                firstItem.Focus(NavigationMethod.Directional);
                return;
            }
        }
        SimulateKey(Key.Tab);
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
        var allDescendants = _window.GetVisualDescendants().ToList();
        var focused = allDescendants.OfType<Avalonia.Controls.Control>().FirstOrDefault(x => x.IsFocused);

        if (focused != null)
        {
            if (focused is ToggleSwitch t) { t.IsChecked = !(t.IsChecked ?? false); return; }
            if (focused is RadioButton r) { r.IsChecked = true; return; }
            if (focused is CheckBox c) 
            { 
                c.IsChecked = !(c.IsChecked ?? false); 
                c.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return; 
            }
            if (focused is ComboBox cb) { cb.IsDropDownOpen = !cb.IsDropDownOpen; return; }
            if (focused is Button b) { b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return; }

            var childTgl = focused.GetVisualDescendants().OfType<ToggleSwitch>().FirstOrDefault();
            if (childTgl != null) { childTgl.IsChecked = !(childTgl.IsChecked ?? false); return; }

            var childRb = focused.GetVisualDescendants().OfType<RadioButton>().FirstOrDefault();
            if (childRb != null) { childRb.IsChecked = true; return; }

            var childChk = focused.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault();
            if (childChk != null) 
            { 
                childChk.IsChecked = !(childChk.IsChecked ?? false); 
                childChk.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return; 
            }

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
