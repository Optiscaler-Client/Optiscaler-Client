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
/// </summary>
public class GamepadNavigationHelper : IDisposable
{
    private readonly IGamepadDetectionService? _gamepadService;
    private readonly Window _window;
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

    // Sidebar button names in visual top-to-bottom order
    private static readonly string[] SidebarButtonNames =
        { "NavGames", "NavProfiles", "NavHelp", "NavSettings" };

    // Active view name for each sidebar position
    private static readonly string[] SidebarViewNames =
        { "ViewGames", "ViewProfiles", "ViewHelp", "ViewSettings" };

    public event EventHandler<bool>? GamepadConnectionChanged;
    public event EventHandler? GamepadActivity;

    public GamepadNavigationHelper(Window window, IGamepadDetectionService? gamepadService)
    {
        _window = window;
        _gamepadService = gamepadService;

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

    private int FindCheckedSidebarIndex()
    {
        for (int i = 0; i < SidebarButtonNames.Length; i++)
        {
            if (_window.FindControl<RadioButton>(SidebarButtonNames[i])?.IsChecked == true)
                return i;
        }
        return 0;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Input dispatch
    // ──────────────────────────────────────────────────────────────────────

    private void OnGamepadInput(object? sender, GamepadEventArgs e)
    {
        if (!e.IsPressed) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (!_window.IsActive) return;

            GamepadActivity?.Invoke(this, EventArgs.Empty);

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

    // ──────────────────────────────────────────────────────────────────────
    // Sidebar zone
    // ──────────────────────────────────────────────────────────────────────

    private void HandleSidebarInput(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.DPadUp:
            case GamepadButton.ThumbLeftUp:
                if (_sidebarIndex > 0)
                {
                    _sidebarIndex--;
                    FocusSidebarButton(_sidebarIndex);
                }
                break;

            case GamepadButton.DPadDown:
            case GamepadButton.ThumbLeftDown:
                if (_sidebarIndex < SidebarButtonNames.Length - 1)
                {
                    _sidebarIndex++;
                    FocusSidebarButton(_sidebarIndex);
                }
                break;

            // A opens/syncs the selected tab; enters content only if
            // the selected tab is already active.
            case GamepadButton.A:
                ActivateSidebarItem();
                break;

            // Right / RStick-Right resumes the currently open tab
            // without changing which tab is active
            case GamepadButton.DPadRight:
            case GamepadButton.ThumbRightRight:
                ReturnToContent();
                break;

            case GamepadButton.B:
                FocusSidebarButton(_sidebarIndex);
                break;
        }
    }

    private void FocusSidebarButton(int index)
    {
        _window.FindControl<RadioButton>(SidebarButtonNames[index])
               ?.Focus(NavigationMethod.Directional);
    }

    /// <summary>
    /// Behavior contract:
    /// - If highlighted button is NOT the active tab: open that tab, keep focus in sidebar.
    /// - If highlighted button IS the active tab: enter content mode.
    /// </summary>
    private void ActivateSidebarItem()
    {
        var btn = _window.FindControl<RadioButton>(SidebarButtonNames[_sidebarIndex]);
        if (btn == null) return;

        // Second confirmation on the active tab enters content.
        if (_activeTabIndex == _sidebarIndex)
        {
            ReturnToContent();
            return;
        }

        btn.IsChecked = true;
        btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        _activeTabIndex = _sidebarIndex;
        _mode = GamepadNavigationMode.Sidebar;
        FocusSidebarButton(_sidebarIndex);
    }

    /// <summary>
    /// Returns to content mode for the tab that is currently open
    /// (<see cref="_activeTabIndex"/>), without activating a new one.
    /// Used when the user presses D-Pad Right / Right-stick Right from the sidebar
    /// after browsing to a different sidebar item without opening it.
    /// </summary>
    private void ReturnToContent()
    {
        _sidebarIndex = _activeTabIndex;
        _mode = GamepadNavigationMode.Content;

        Dispatcher.UIThread.Post(() =>
        {
            if (_sidebarIndex == 0)
            {
                FocusFirstListItem(saveHeader: false);
                return;
            }

            var view = _window.FindControl<Grid>(SidebarViewNames[_sidebarIndex]);
            if (view == null) return;
            FindFirstFocusable(view)?.Focus(NavigationMethod.Directional);
        }, DispatcherPriority.Background);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Content zone — top-level dispatcher
    // ──────────────────────────────────────────────────────────────────────

    private void HandleContentInput(GamepadButton button)
    {
        // In games action-lock mode, B exits back to game navigation
        // (not to sidebar), keeping the user on the current game card.
        if (_sidebarIndex == 0
            && _isGamesActionMode
            && (button == GamepadButton.B || button == GamepadButton.ThumbRightLeft))
        {
            ExitGamesActionMode();
            return;
        }

        // B always returns to sidebar regardless of sub-zone
        if (button == GamepadButton.B || button == GamepadButton.ThumbRightLeft)
        {
            GoToSidebar();
            return;
        }

        var topLevel = TopLevel.GetTopLevel(_window);
        var focused  = topLevel?.FocusManager?.GetFocusedElement();

        // Games tab: spatial (X/Y) navigation
        if (_sidebarIndex == 0)
        {
            HandleGamesInput(button, focused);
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

    // ──────────────────────────────────────────────────────────────────────
    // Games tab — spatial navigation
    // ──────────────────────────────────────────────────────────────────────

    private enum GamesSubZone { Header, GameList, GameGrid }

    private GamesSubZone GetGamesSubZone(IInputElement? focused)
    {
        if (focused is not Visual v) return GamesSubZone.Header;

        bool isGridActive = IsGridViewActive();

        if (isGridActive)
        {
            var lstGrid = _window.FindControl<ListBox>("LstGamesGrid");
            if (lstGrid != null && IsInside(v, lstGrid))
                return GamesSubZone.GameGrid;
        }
        else
        {
            var lstGames = _window.FindControl<ListBox>("LstGames");
            if (lstGames != null && IsInside(v, lstGames))
                return GamesSubZone.GameList;
        }

        return GamesSubZone.Header;
    }

    private void HandleGamesInput(GamepadButton button, IInputElement? focused)
    {
        if (_isGamesActionMode)
        {
            HandleGamesActionInput(button);
            return;
        }

        switch (GetGamesSubZone(focused))
        {
            case GamesSubZone.Header:   HandleGamesHeaderInput(button);          break;
            case GamesSubZone.GameList: HandleGameListInput(button, focused);    break;
            case GamesSubZone.GameGrid: HandleGameGridInput(button, focused);    break;
        }
    }

    // Header row: Scan, Add Manually, Bulk Install, Search, view toggles…
    private void HandleGamesHeaderInput(GamepadButton button)
    {
        switch (button)
        {
            // Left: go to sidebar when already on the leftmost header control
            case GamepadButton.DPadLeft:
            case GamepadButton.ThumbLeftLeft:
                if (IsAtLeftHeaderEdge())
                    GoToSidebar();
                else
                    SimulateKey(Key.Tab, KeyModifiers.Shift);
                break;

            case GamepadButton.DPadRight:
            case GamepadButton.ThumbLeftRight:
                SimulateKey(Key.Tab);
                break;

            // Down: enter the game list
            case GamepadButton.DPadDown:
            case GamepadButton.ThumbLeftDown:
                FocusFirstListItem();
                break;

            // Up: no-op — already at the top of the content area
            case GamepadButton.DPadUp:
            case GamepadButton.ThumbLeftUp:
                break;

            case GamepadButton.A:
                ActivateFocusedElement();
                break;
        }
    }

    // List view (LstGames): Up/Down only; Left always returns to sidebar
    private void HandleGameListInput(GamepadButton button, IInputElement? focused)
    {
        var lst = _window.FindControl<ListBox>("LstGames");
        if (lst == null || lst.ItemCount == 0) return;

        int currentIndex = GetCurrentListIndex(lst, focused);

        switch (button)
        {
            case GamepadButton.DPadDown:
            case GamepadButton.ThumbLeftDown:
                if (currentIndex < lst.ItemCount - 1)
                {
                    int nextIndex = currentIndex + 1;
                    lst.SelectedIndex = nextIndex;
                    FocusListItem(lst, nextIndex);
                }
                break;

            case GamepadButton.DPadUp:
            case GamepadButton.ThumbLeftUp:
                if (currentIndex <= 0)
                    FocusFirstHeaderButton();
                else
                {
                    int prevIndex = currentIndex - 1;
                    lst.SelectedIndex = prevIndex;
                    FocusListItem(lst, prevIndex);
                }
                break;

            // Single-column list: Left edge = sidebar
            case GamepadButton.DPadLeft:
            case GamepadButton.ThumbLeftLeft:
                GoToSidebar();
                break;

            case GamepadButton.A:
                EnterGamesActionMode(isGrid: false, lst, currentIndex);
                break;
        }
    }

    // Grid view (LstGamesGrid): 4-directional spatial navigation
    // Avalonia's built-in arrow-key handling on ListBox is purely linear
    // (index ± 1), so we compute row/column manually from rendered positions.
    private void HandleGameGridInput(GamepadButton button, IInputElement? focused)
    {
        var lst = _window.FindControl<ListBox>("LstGamesGrid");
        if (lst == null || lst.ItemCount == 0) return;

        int currentIndex = GetCurrentListIndex(lst, focused);

        switch (button)
        {
            case GamepadButton.DPadDown:
            case GamepadButton.ThumbLeftDown:
                NavigateGrid(lst, 0, +1);
                break;

            case GamepadButton.DPadUp:
            case GamepadButton.ThumbLeftUp:
                if (IsAtFirstGridRow(lst))
                    FocusFirstHeaderButton();
                else
                    NavigateGrid(lst, 0, -1);
                break;

            case GamepadButton.DPadLeft:
            case GamepadButton.ThumbLeftLeft:
                if (IsAtFirstGridColumn(lst))
                    GoToSidebar();
                else
                    NavigateGrid(lst, -1, 0);
                break;

            case GamepadButton.DPadRight:
            case GamepadButton.ThumbLeftRight:
                NavigateGrid(lst, +1, 0);
                break;

            case GamepadButton.A:
                EnterGamesActionMode(isGrid: true, lst, currentIndex);
                break;
        }
    }

    private void HandleGamesActionInput(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.DPadUp:
            case GamepadButton.ThumbLeftUp:
            case GamepadButton.DPadLeft:
            case GamepadButton.ThumbLeftLeft:
                MoveGamesActionFocus(-1);
                break;

            case GamepadButton.DPadDown:
            case GamepadButton.ThumbLeftDown:
            case GamepadButton.DPadRight:
            case GamepadButton.ThumbLeftRight:
                MoveGamesActionFocus(+1);
                break;

            case GamepadButton.A:
                ActivateFocusedElement();
                break;

            case GamepadButton.B:
            case GamepadButton.ThumbRightLeft:
                ExitGamesActionMode();
                break;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Games sub-zone helpers
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>Switches back to Sidebar mode and restores focus on the current nav button.</summary>
    private void GoToSidebar()
    {
        ExitGamesActionMode(restoreGameFocus: false);
        _mode = GamepadNavigationMode.Sidebar;
        FocusSidebarButton(_sidebarIndex);
    }

    private void EnterGamesActionMode(bool isGrid, ListBox listBox, int itemIndex)
    {
        if (listBox.ItemCount == 0) return;

        int clampedIndex = Math.Clamp(itemIndex, 0, listBox.ItemCount - 1);
        listBox.SelectedIndex = clampedIndex;
        FocusListItem(listBox, clampedIndex);

        if (!TryGetGameActionButtons(isGrid, listBox, clampedIndex, out var quickInstall, out var manage))
        {
            ActivateFocusedElement();
            return;
        }

        _isGamesActionMode = true;
        _gamesActionIsGrid = isGrid;
        _gamesActionItemIndex = clampedIndex;
        _gamesActionButtonIndex = 0;

        quickInstall?.Focus(NavigationMethod.Directional);
    }

    private void ExitGamesActionMode(bool restoreGameFocus = true)
    {
        if (!_isGamesActionMode) return;

        var list = _gamesActionIsGrid
            ? _window.FindControl<ListBox>("LstGamesGrid")
            : _window.FindControl<ListBox>("LstGames");

        if (_gamesActionIsGrid && list != null)
        {
            if (list.ContainerFromIndex(_gamesActionItemIndex) is ListBoxItem item)
            {
                var card = item.GetVisualDescendants()
                    .OfType<Border>()
                    .FirstOrDefault(x => x.Classes.Contains("GameGridCard"));
                if (card != null)
                    SetGridCardHoverState(card, false);
            }
        }

        int indexToRestore = _gamesActionItemIndex;

        _isGamesActionMode = false;
        _gamesActionItemIndex = -1;
        _gamesActionButtonIndex = 0;

        if (restoreGameFocus && list != null && indexToRestore >= 0 && list.ItemCount > 0)
        {
            FocusListItem(list, Math.Clamp(indexToRestore, 0, list.ItemCount - 1));
        }
    }

    private void MoveGamesActionFocus(int direction)
    {
        if (!_isGamesActionMode) return;

        var list = _gamesActionIsGrid
            ? _window.FindControl<ListBox>("LstGamesGrid")
            : _window.FindControl<ListBox>("LstGames");

        if (list == null)
        {
            ExitGamesActionMode();
            return;
        }

        if (!TryGetGameActionButtons(_gamesActionIsGrid, list, _gamesActionItemIndex, out var quickInstall, out var manage))
        {
            ExitGamesActionMode();
            return;
        }

        _gamesActionButtonIndex = Math.Clamp(_gamesActionButtonIndex + Math.Sign(direction), 0, 1);
        if (_gamesActionButtonIndex == 0)
            quickInstall?.Focus(NavigationMethod.Directional);
        else
            manage?.Focus(NavigationMethod.Directional);
    }

    private bool TryGetGameActionButtons(bool isGrid, ListBox listBox, int itemIndex, out Button? quickInstall, out Button? manage)
    {
        quickInstall = null;
        manage = null;

        if (itemIndex < 0 || itemIndex >= listBox.ItemCount)
            return false;

        listBox.UpdateLayout();
        if (listBox.ContainerFromIndex(itemIndex) is not ListBoxItem item)
            return false;

        if (!isGrid)
        {
            quickInstall = item.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(x => x.Name == "BtnFastInstall");
            manage = item.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(x => x.Name == "BtnManageGame");
            return quickInstall != null && manage != null;
        }

        var card = item.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(x => x.Classes.Contains("GameGridCard"));
        if (card == null) return false;

        SetGridCardHoverState(card, true);

        var actions = card.GetVisualDescendants()
            .OfType<Panel>()
            .FirstOrDefault(x => x.Name == "GridCardHoverActions");
        if (actions == null) return false;

        var buttons = actions.GetVisualDescendants().OfType<Button>().ToList();
        quickInstall = buttons.ElementAtOrDefault(0);
        manage = buttons.ElementAtOrDefault(1);
        return quickInstall != null && manage != null;
    }

    private static void SetGridCardHoverState(Border card, bool isVisible)
    {
        var overlay = card.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(x => x.Name == "GridCardHoverOverlay");
        var actions = card.GetVisualDescendants()
            .OfType<Panel>()
            .FirstOrDefault(x => x.Name == "GridCardHoverActions");

        if (overlay == null || actions == null) return;

        overlay.IsVisible = isVisible;
        overlay.Opacity = isVisible ? 1 : 0;
        actions.IsVisible = isVisible;
        actions.Opacity = isVisible ? 1 : 0;
        actions.IsHitTestVisible = isVisible;
    }

    /// <summary>
    /// Returns true if focus is on BtnScan — the leftmost control in the
    /// Games tab header — so Left should escape to the sidebar.
    /// </summary>
    private bool IsAtLeftHeaderEdge()
    {
        var topLevel = TopLevel.GetTopLevel(_window);
        var focused  = topLevel?.FocusManager?.GetFocusedElement();
        return focused == _window.FindControl<Button>("BtnScan");
    }

    /// <summary>
    /// Returns true if the currently selected grid item is in column 0,
    /// so Left should escape to the sidebar.
    /// </summary>
    private static bool IsAtFirstGridColumn(ListBox listBox)
    {
        int idx = listBox.SelectedIndex;
        if (idx <= 0) return true;
        return idx % CountGridColumns(listBox) == 0;
    }

    /// <summary>
    /// Returns true if the currently focused element is the first focusable
    /// control inside the active tab's view, so Left should escape to the sidebar.
    /// </summary>
    private bool IsAtFirstContentElement()
    {
        var topLevel = TopLevel.GetTopLevel(_window);
        var focused  = topLevel?.FocusManager?.GetFocusedElement();
        var view     = _window.FindControl<Grid>(SidebarViewNames[_sidebarIndex]);
        if (view == null) return false;
        return focused == FindFirstFocusable(view);
    }

    /// <summary>
    /// Returns focus to the header. If the user previously moved focus away
    /// from BtnScan, restores that exact control; otherwise falls back to BtnScan.
    /// </summary>
    private void FocusFirstHeaderButton()
    {
        if (_lastHeaderFocus is Control saved && saved.IsVisible && saved.IsEnabled)
        {
            saved.Focus(NavigationMethod.Directional);
            return;
        }
        _window.FindControl<Button>("BtnScan")?.Focus(NavigationMethod.Directional);
    }

    /// <summary>
    /// Selects and focuses the first item of the currently-visible game list.
    /// Saves the currently focused header control so it can be restored on return.
    /// </summary>
    private void FocusFirstListItem(bool saveHeader = true)
    {
        // Snapshot whatever header control is focused right now (skip when
        // entering from the sidebar so we don't save a NavButton as header focus)
        if (saveHeader)
        {
            var topLevel = TopLevel.GetTopLevel(_window);
            var current  = topLevel?.FocusManager?.GetFocusedElement();
            if (current != null)
                _lastHeaderFocus = current;
        }

        bool isGrid = IsGridViewActive();
        var list = isGrid
            ? _window.FindControl<ListBox>("LstGamesGrid")
            : _window.FindControl<ListBox>("LstGames");

        if (list == null || list.ItemCount == 0) return;

        // Force start-of-list position before focusing index 0.
        var scroll = isGrid
            ? _window.FindControl<ScrollViewer>("GameGridScrollViewer")
            : _window.FindControl<ScrollViewer>("GameListScrollViewer");
        if (scroll != null)
        {
            scroll.Offset = new Vector(scroll.Offset.X, 0);
        }

        list.SelectedIndex = 0;
        FocusListItem(list, 0);
    }

    /// <summary>
    /// Returns true when <paramref name="focused"/> is inside the first
    /// ListBoxItem of <paramref name="listBox"/>, so pressing Up should
    /// escape to the header rather than stay in the list.
    /// </summary>
    private static bool IsAtFirstListItem(ListBox listBox, IInputElement? focused)
    {
        if (focused is not Visual v) return true;
        var firstItem = listBox.ContainerFromIndex(0) as ListBoxItem;
        if (firstItem == null) return true;
        return v == firstItem
            || v.GetVisualAncestors().OfType<Visual>().Contains(firstItem);
    }

    private bool IsGridViewActive()
    {
        var gridScroll = _window.FindControl<ScrollViewer>("GameGridScrollViewer");
        var listScroll = _window.FindControl<ScrollViewer>("GameListScrollViewer");

        if (gridScroll?.IsVisible == true && gridScroll.IsHitTestVisible)
            return true;

        if (listScroll?.IsVisible == true && listScroll.IsHitTestVisible)
            return false;

        // Fallback: if visibility/hittest are temporarily stale during transition,
        // keep previous behavior by preferring list mode.
        return false;
    }

    private static bool IsInside(Visual focused, Visual container)
    {
        if (focused == container) return true;
        return focused.GetVisualAncestors().Contains(container);
    }

    private int GetCurrentListIndex(ListBox listBox, IInputElement? focused)
    {
        if (listBox.SelectedIndex >= 0)
            return listBox.SelectedIndex;

        if (focused is Visual v)
        {
            for (int i = 0; i < listBox.ItemCount; i++)
            {
                if (listBox.ContainerFromIndex(i) is ListBoxItem item
                    && (v == item || v.GetVisualAncestors().Contains(item)))
                    return i;
            }
        }

        return 0;
    }

    private bool TryGetSidebarIndexFromFocus(IInputElement? focused, out int index)
    {
        index = -1;
        if (focused is not Visual v) return false;

        for (int i = 0; i < SidebarButtonNames.Length; i++)
        {
            var btn = _window.FindControl<RadioButton>(SidebarButtonNames[i]);
            if (btn == null) continue;

            if (v == btn || v.GetVisualAncestors().Contains(btn))
            {
                index = i;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when the currently selected grid item is in the first row,
    /// meaning Up should escape to the header.
    /// </summary>
    private static bool IsAtFirstGridRow(ListBox listBox)
    {
        int idx = listBox.SelectedIndex;
        if (idx < 0) return true;
        return idx < CountGridColumns(listBox);
    }

    /// <summary>
    /// Counts the number of items in the first visual row by comparing their
    /// rendered Y-positions. This is more reliable than any width calculation
    /// because it reflects the actual layout regardless of padding or margins.
    /// </summary>
    private static int CountGridColumns(ListBox listBox)
    {
        var items = listBox.GetVisualDescendants()
                           .OfType<ListBoxItem>()
                           .ToList();
        if (items.Count == 0) return 1;

        double firstTop = items[0].Bounds.Top;
        int cols = 0;
        foreach (var item in items)
        {
            if (Math.Abs(item.Bounds.Top - firstTop) < 5.0)
                cols++;
            else
                break;
        }
        return Math.Max(1, cols);
    }

    /// <summary>
    /// Moves selection by (deltaCol, deltaRow) inside the grid.
    /// Left/Right movement is clamped within the current row (no wrap).
    /// Down on the last row or Right on the last column is a no-op.
    /// </summary>
    private static void NavigateGrid(ListBox listBox, int deltaCol, int deltaRow)
    {
        int current   = Math.Max(0, listBox.SelectedIndex);
        int cols      = CountGridColumns(listBox);
        int itemCount = listBox.ItemCount;
        if (itemCount == 0) return;

        int row    = current / cols;
        int col    = current % cols;
        int newCol = deltaCol != 0 ? Math.Clamp(col + deltaCol, 0, cols - 1) : col;
        int newRow = row + deltaRow;

        int newIndex = Math.Clamp(newRow * cols + newCol, 0, itemCount - 1);
        if (newIndex == current) return;

        listBox.SelectedIndex = newIndex;
        FocusListItem(listBox, newIndex);
    }

    /// <summary>
    /// Focuses the ListBoxItem at the given index; Avalonia's focus system
    /// automatically calls BringIntoView on the element.
    /// </summary>
    private static void FocusListItem(ListBox listBox, int index)
    {
        if (index < 0 || index >= listBox.ItemCount) return;

        listBox.SelectedIndex = index;

        if (TryFocusListContainer(listBox, index))
            return;

        // Virtualized containers may not exist yet right after selection changes.
        // Refresh layout and retry on UI queue.
        listBox.UpdateLayout();
        if (TryFocusListContainer(listBox, index))
            return;

        Dispatcher.UIThread.Post(() =>
        {
            listBox.UpdateLayout();
            TryFocusListContainer(listBox, index);
        }, DispatcherPriority.Background);
    }

    private static bool TryFocusListContainer(ListBox listBox, int index)
    {
        if (listBox.ContainerFromIndex(index) is not ListBoxItem item)
            return false;

        item.BringIntoView();
        item.Focus(NavigationMethod.Directional);
        return true;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Shared helpers
    // ──────────────────────────────────────────────────────────────────────

    private void ActivateFocusedElement()
    {
        var topLevel = TopLevel.GetTopLevel(_window);
        var focused = topLevel?.FocusManager?.GetFocusedElement();
        if (focused == null) return;

        if (TryGetFocusedButton(focused, out var button))
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return;
        }

        SimulateKey(Key.Enter);
    }

    private static bool TryGetFocusedButton(IInputElement focused, out Button button)
    {
        if (focused is Button focusedButton)
        {
            button = focusedButton;
            return true;
        }

        if (focused is Visual visual)
        {
            var ancestorButton = visual.GetVisualAncestors().OfType<Button>().FirstOrDefault();
            if (ancestorButton != null)
            {
                button = ancestorButton;
                return true;
            }
        }

        button = null!;
        return false;
    }

    private void SimulateKey(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        var topLevel = TopLevel.GetTopLevel(_window);
        var focused  = topLevel?.FocusManager?.GetFocusedElement();
        var target   = (focused as Interactive) ?? _window;

        target.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent  = InputElement.KeyDownEvent,
            Key          = key,
            Source       = target,
            KeyModifiers = modifiers
        });
    }

    /// <summary>
    /// Depth-first search for the first keyboard-focusable leaf inside
    /// <paramref name="root"/>. Skips ScrollViewer / ScrollBar containers.
    /// </summary>
    private static IInputElement? FindFirstFocusable(Visual root)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is InputElement elem
                && elem is not ScrollViewer
                && elem is not ScrollBar
                && elem.IsEnabled
                && elem.IsVisible
                && elem.Focusable)
            {
                return elem;
            }

            var found = FindFirstFocusable(child);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Switches visual/input context back to mouse mode by clearing any
    /// controller-only state (action lock and focus ring).
    /// </summary>
    public void SwitchToMouseMode()
    {
        Dispatcher.UIThread.Post(() =>
        {
            ExitGamesActionMode(restoreGameFocus: false);
            TopLevel.GetTopLevel(_window)?.FocusManager?.ClearFocus();
        });
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_gamepadService != null)
        {
            _gamepadService.GamepadInputReceived -= OnGamepadInput;
            _gamepadService.GamepadConnectionChanged -= OnGamepadConnectionChanged;
            _gamepadService.StopListening();
        }
    }
}

