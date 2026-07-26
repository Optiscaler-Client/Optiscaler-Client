using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OptiscalerClient.Models;
using OptiscalerClient.Services;
using OptiscalerClient.Views;
using Avalonia;

namespace OptiscalerClient.Helpers;

/// <summary>
/// Tracks whether the gamepad cursor is navigating the sidebar or the
/// active tab's content area.
/// </summary>
public enum GamepadNavigationMode { Sidebar, Content }

/// <summary>
/// Handles gamepad input and routes it through a two-zone state machine:
///
///   Sidebar  ─── A(on active tab) / DPad-Right / RStick-Right ──▶  Content
///   Content  ◀── B ──────────────────────────────────  Sidebar
///
/// Sidebar zone: DPad/LStick Up-Down cycles through the 4 nav buttons
/// (clamped — no wrapping). A opens a tab and keeps focus in sidebar.
/// Pressing A again on the already active tab enters its content.
///
/// Content zone — Games tab uses spatial (X/Y) navigation with three
/// sub-zones: Header, GameList, GameGrid.  All other tabs use Tab-based.
/// A confirms the focused control. B always returns to the Sidebar.
///
/// This class is split across partial files by responsibility — see
/// context/plans/gamepad_refactor_plan.md, Fase 2:
///   - GamepadNavigationHelper.cs          core: dispatch, connection, lifecycle
///   - GamepadNavigationHelper.Sidebar.cs   sidebar zone
///   - GamepadNavigationHelper.Games.cs     Games tab (list/grid/drag/actions)
///   - GamepadNavigationHelper.Profiles.cs  Profiles tab + Profile Editor
///   - GamepadNavigationHelper.Shared.cs    generic focus/activation helpers
/// </summary>
public partial class GamepadNavigationHelper : GamepadHelperBase, IDisposable
{
    private enum RightStickScrollTarget { None, Games, Help }

    private readonly IGamepadDetectionService? _gamepadService;
    private bool _isDisposed;

    private GamepadNavigationMode _mode = GamepadNavigationMode.Sidebar;
    private int _sidebarIndex = 0;   // which sidebar button currently has focus
    private int _activeTabIndex = 0; // which tab is actually open (last activated with A)

    // Remembers the last focused header control so returning from the list
    // restores the exact position (e.g. search box) instead of always BtnScan.
    private IInputElement? _lastHeaderFocus;

    // Games action-lock mode: when active, navigation is constrained to
    // Quick Install / Manage buttons for the current game card.
    private bool _isGamesActionMode;
    private bool _gamesActionIsGrid;
    private int _gamesActionItemIndex = -1;
    private int _gamesActionButtonIndex;

    // Right stick vertical hold state for smooth continuous scroll on Games view.
    private readonly DispatcherTimer _gamesScrollTimer;

    private long _suppressVisibilityCheckUntil = 0;

    public void SuppressVisibilityJump(int milliseconds = 500)
    {
        _suppressVisibilityCheckUntil = Environment.TickCount64 + milliseconds;
    }
    private bool _isRightStickUpHeld;
    private bool _isRightStickDownHeld;
    private double _gamesScrollVelocity;

    // Sidebar button names in visual top-to-bottom order
    private static readonly string[] SidebarButtonNames =
        { "NavGames", "NavProfiles", "NavHelp", "NavSettings" };

    // Active view name for each sidebar position
    private static readonly string[] SidebarViewNames =
        { "ViewGames", "ViewProfiles", "ViewHelp", "ViewSettings" };

    public event EventHandler<bool>? GamepadConnectionChanged;
    public event EventHandler? GamepadActivity;

    public GamepadNavigationHelper(Window window, IGamepadDetectionService? gamepadService) : base(window)
    {
        _gamepadService = gamepadService;
        _gamesScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _gamesScrollTimer.Tick += GamesScrollTimer_Tick;

        if (_gamepadService != null)
        {
            _gamepadService.GamepadInputReceived += OnGamepadInput;
            _gamepadService.GamepadConnectionChanged += OnGamepadConnectionChanged;
            _gamepadService.StartListening();
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Connection
    // ──────────────────────────────────────────────────────────────────────

    private void OnGamepadConnectionChanged(object? sender, bool isConnected)
    {
        GamepadConnectionChanged?.Invoke(this, isConnected);

        if (isConnected)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _mode = GamepadNavigationMode.Sidebar;
                _sidebarIndex = FindCheckedSidebarIndex();
                FocusSidebarButton(_sidebarIndex);
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Input dispatch
    // ──────────────────────────────────────────────────────────────────────

    private void OnGamepadInput(object? sender, GamepadEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsInputSuspended) return;

            if (HandleRightStickVerticalInput(e))
                return;

            if (!e.IsPressed) return;
            if (!_window.IsActive) return;
            if (HasOpenOwnedWindow()) return;

            GamepadActivity?.Invoke(this, EventArgs.Empty);
            MarkGamepadModeActive();

            if (_window is MainWindow mw && mw.IsVirtualKeyboardOpen)
            {
                mw.HandleVirtualKeyboardInput(e.Button);
                return;
            }

            // Keep navigation mode synchronized with the actual focused zone.
            // This prevents "sidebar behaves like content" desyncs.
            var focused = TopLevel.GetTopLevel(_window)?.FocusManager?.GetFocusedElement();
            if (TryGetSidebarIndexFromFocus(focused, out int focusedSidebarIndex))
            {
                _sidebarIndex = focusedSidebarIndex;
                _mode = GamepadNavigationMode.Sidebar;
                ExitGamesActionMode(restoreGameFocus: false);
            }

            if (_mode == GamepadNavigationMode.Sidebar)
                HandleSidebarInput(e.Button);
            else
                HandleContentInput(e.Button);
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
        {
            GamepadActivity?.Invoke(this, EventArgs.Empty);
            MarkGamepadModeActive();
        }

        UpdateGamesScrollTimerState();
        return true;
    }

    private void GamesScrollTimer_Tick(object? sender, EventArgs e)
    {
        var scrollTarget = GetRightStickScrollTarget();
        if (scrollTarget == RightStickScrollTarget.None || _isRightStickUpHeld == _isRightStickDownHeld)
        {
            UpdateGamesScrollTimerState();
            return;
        }

        // Accelerate while holding stick for a smoother, analog-like scroll feel.
        _gamesScrollVelocity = Math.Min(28.0, _gamesScrollVelocity + 1.5);
        double delta = 6.0 + _gamesScrollVelocity;

        if (_isRightStickUpHeld)
            delta = -delta;

        ScrollRightStickViewport(scrollTarget, delta);
    }

    private void UpdateGamesScrollTimerState()
    {
        bool hasDirection = _isRightStickUpHeld ^ _isRightStickDownHeld;
        var scrollTarget = GetRightStickScrollTarget();
        bool shouldScroll = hasDirection && scrollTarget != RightStickScrollTarget.None;

        if (shouldScroll)
        {
            if (!_gamesScrollTimer.IsEnabled)
            {
                _gamesScrollVelocity = 0;
                _gamesScrollTimer.Start();

                // Immediate tiny move so scrolling feels responsive on first press.
                ScrollRightStickViewport(scrollTarget, _isRightStickUpHeld ? -10.0 : 10.0);
            }
            return;
        }

        if (_gamesScrollTimer.IsEnabled)
            _gamesScrollTimer.Stop();

        _gamesScrollVelocity = 0;
    }

    private RightStickScrollTarget GetRightStickScrollTarget()
    {
        if (!_window.IsActive || _mode != GamepadNavigationMode.Content)
            return RightStickScrollTarget.None;

        if (_sidebarIndex == 0 && !_isGamesActionMode)
            return RightStickScrollTarget.Games;

        if (_sidebarIndex == 2)
        {
            var helpScroll = _window.FindControl<ScrollViewer>("HelpContentScrollViewer");
            if (helpScroll?.IsVisible == true && helpScroll.IsHitTestVisible)
                return RightStickScrollTarget.Help;
        }

        return RightStickScrollTarget.None;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Content zone — top-level dispatcher
    // ──────────────────────────────────────────────────────────────────────

    private void HandleContentInput(GamepadButton button)
    {
        var topLevel = TopLevel.GetTopLevel(_window);
        var focused  = topLevel?.FocusManager?.GetFocusedElement();

        // In games action-lock mode, B exits back to game navigation
        // (not to sidebar), keeping the user on the current game card.
        if (_sidebarIndex == 0
            && _isGamesActionMode
            && button == GamepadButton.B)
        {
            ExitGamesActionMode();
            return;
        }

        var openCombo = _window.GetVisualDescendants().OfType<Avalonia.Controls.ComboBox>().FirstOrDefault(x => x.IsDropDownOpen);
        if (openCombo != null)
        {
            switch (button)
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
        }

        if (button == GamepadButton.B)
        {
            if (_window is MainWindow mainWinEdit && mainWinEdit.IsEditMode && !mainWinEdit.IsGamepadDragging)
            {
                var btnDone = _window.FindControl<Avalonia.Controls.Button>("BtnEditModeDone");
                if (btnDone != null)
                {
                    btnDone.Focus(NavigationMethod.Directional);
                    return;
                }
            }

            if (_sidebarIndex == 0)
            {
                var subZone = GetGamesSubZone(focused);
                if (subZone == GamesSubZone.GameList || subZone == GamesSubZone.GameGrid)
                {
                    FocusFirstHeaderButton();
                    return;
                }
            }

            GoToSidebar();
            return;
        }

        // Games tab: spatial (X/Y) navigation
        if (_sidebarIndex == 0)
        {
            HandleGamesInput(button, focused);
            return;
        }

        // Profiles tab: spatial navigation + OSK
        if (_sidebarIndex == 1)
        {
            if (_window is MainWindow mw && mw.IsProfileEditorOpen)
            {
                HandleProfileEditorInput(button, focused);
            }
            else
            {
                HandleProfilesInput(button, focused);
            }
            return;
        }

        // All other tabs: Tab-based linear navigation
        switch (button)
        {
            case GamepadButton.DPadDown:
            case GamepadButton.ThumbLeftDown:
                SimulateKey(Key.Tab);
                break;

            case GamepadButton.DPadUp:
            case GamepadButton.ThumbLeftUp:
                SimulateKey(Key.Tab, KeyModifiers.Shift);
                break;

            // Left at the first focusable element of the tab → back to sidebar
            case GamepadButton.DPadLeft:
            case GamepadButton.ThumbLeftLeft:
                if (IsAtFirstContentElement())
                    GoToSidebar();
                else
                    SimulateKey(Key.Tab, KeyModifiers.Shift);
                break;

            case GamepadButton.DPadRight:
            case GamepadButton.ThumbLeftRight:
                SimulateKey(Key.Tab);
                break;

            case GamepadButton.A:
                ActivateFocusedElement();
                break;
        }
    }

    /// <summary>
    /// Switches visual/input context back to mouse mode by clearing any
    /// controller-only state (action lock and focus ring).
    /// </summary>
    public void SwitchToMouseMode()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _isRightStickUpHeld = false;
            _isRightStickDownHeld = false;
            UpdateGamesScrollTimerState();
            ExitGamesActionMode(restoreGameFocus: false);
            TopLevel.GetTopLevel(_window)?.FocusManager?.ClearFocus();
        });
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _gamesScrollTimer.Stop();
        _gamesScrollTimer.Tick -= GamesScrollTimer_Tick;

        if (_gamepadService != null)
        {
            _gamepadService.GamepadInputReceived -= OnGamepadInput;
            _gamepadService.GamepadConnectionChanged -= OnGamepadConnectionChanged;
            _gamepadService.StopListening();
        }
        UnhookGamepadModeDetection();
    }
}
