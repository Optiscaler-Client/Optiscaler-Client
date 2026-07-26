using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OptiscalerClient.Models;
using OptiscalerClient.Views;

namespace OptiscalerClient.Helpers;

// Games tab: header, list view, grid view, drag & drop (Organize Games) and
// the Quick Install/Manage action-lock mode. Split out of
// GamepadNavigationHelper.cs — see gamepad_refactor_plan.md, Fase 2.
public partial class GamepadNavigationHelper
{
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
        if (_window is MainWindow mainWin && mainWin.IsGamepadDragging)
        {
            HandleGamesDragInput(mainWin, button);
            return;
        }

        if (_isGamesActionMode)
        {
            HandleGamesActionInput(button);
            return;
        }

        switch (GetGamesSubZone(focused))
        {
            case GamesSubZone.Header:   HandleGamesHeaderInput(button, focused);          break;
            case GamesSubZone.GameList: HandleGameListInput(button, focused);    break;
            case GamesSubZone.GameGrid: HandleGameGridInput(button, focused);    break;
        }
    }

    // Header row: Scan, Add Manually, Bulk Install, Search, view toggles…
    private void HandleGamesHeaderInput(GamepadButton button, IInputElement? focused)
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
                if (focused is Avalonia.Controls.TextBox tb && tb.Name == "TxtSearch" && _window is MainWindow mw)
                {
                    mw.OpenVirtualKeyboard();
                    return;
                }
                ActivateFocusedElement();
                break;
        }
    }

    // List view (LstGames): Up/Down only; Left always returns to sidebar
    private void HandleGameListInput(GamepadButton button, IInputElement? focused)
    {
        var lst = _window.FindControl<ListBox>("LstGames");
        if (lst == null || lst.ItemCount == 0) return;

        var scroll = _window.FindControl<ScrollViewer>("GameListScrollViewer");
        int currentIndex = GetCurrentListIndex(lst, focused);

        bool isVisible = true;
        if (scroll != null)
        {
            if (Environment.TickCount64 < _suppressVisibilityCheckUntil)
            {
                isVisible = true;
            }
            else
            {
                var container = lst.ContainerFromIndex(currentIndex) as Visual;
                isVisible = container != null && IsControlVisibleInScrollViewer(container, scroll);
            }
        }

        switch (button)
        {
            case GamepadButton.DPadDown:
            case GamepadButton.ThumbLeftDown:
                if (!isVisible && scroll != null)
                {
                    int vis = GetFirstVisibleListIndex(lst, scroll, false);
                    if (vis >= 0)
                    {
                        lst.SelectedIndex = vis;
                        FocusListItem(lst, vis);
                        break;
                    }
                }
                if (currentIndex < lst.ItemCount - 1)
                {
                    int nextIndex = currentIndex + 1;
                    lst.SelectedIndex = nextIndex;
                    FocusListItem(lst, nextIndex);
                }
                break;

            case GamepadButton.DPadUp:
            case GamepadButton.ThumbLeftUp:
                if (!isVisible && scroll != null)
                {
                    int vis = GetFirstVisibleListIndex(lst, scroll, true);
                    if (vis >= 0)
                    {
                        lst.SelectedIndex = vis;
                        FocusListItem(lst, vis);
                        break;
                    }
                }
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
                if (_window is MainWindow mainWinList && mainWinList.IsEditMode)
                    mainWinList.GamepadBeginDrag(currentIndex);
                else
                    EnterGamesActionMode(isGrid: false, lst, currentIndex);
                break;

            case GamepadButton.Y:
                if (_window is MainWindow mainWinListY && mainWinListY.IsEditMode && !mainWinListY.IsGamepadDragging)
                    mainWinListY.GamepadToggleHide(currentIndex);
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

        var scroll = _window.FindControl<ScrollViewer>("GameGridScrollViewer");
        int currentIndex = GetCurrentListIndex(lst, focused);

        bool isVisible = true;
        if (scroll != null)
        {
            if (Environment.TickCount64 < _suppressVisibilityCheckUntil)
            {
                isVisible = true;
            }
            else
            {
                var container = lst.ContainerFromIndex(currentIndex) as Visual;
                isVisible = container != null && IsControlVisibleInScrollViewer(container, scroll);
            }
        }

        switch (button)
        {
            case GamepadButton.DPadDown:
            case GamepadButton.ThumbLeftDown:
                if (!isVisible && scroll != null)
                {
                    int vis = GetFirstVisibleListIndex(lst, scroll, false);
                    if (vis >= 0)
                    {
                        lst.SelectedIndex = vis;
                        FocusListItem(lst, vis);
                        break;
                    }
                }
                NavigateGrid(lst, 0, +1);
                break;

            case GamepadButton.DPadUp:
            case GamepadButton.ThumbLeftUp:
                if (!isVisible && scroll != null)
                {
                    int vis = GetFirstVisibleListIndex(lst, scroll, true);
                    if (vis >= 0)
                    {
                        lst.SelectedIndex = vis;
                        FocusListItem(lst, vis);
                        break;
                    }
                }
                if (IsAtFirstGridRow(lst))
                    FocusFirstHeaderButton();
                else
                    NavigateGrid(lst, 0, -1);
                break;

            case GamepadButton.DPadLeft:
            case GamepadButton.ThumbLeftLeft:
                if (!isVisible && scroll != null)
                {
                    int vis = GetFirstVisibleListIndex(lst, scroll, false);
                    if (vis >= 0)
                    {
                        lst.SelectedIndex = vis;
                        FocusListItem(lst, vis);
                        break;
                    }
                }
                if (IsAtFirstGridColumn(lst))
                    GoToSidebar();
                else
                    NavigateGrid(lst, -1, 0);
                break;

            case GamepadButton.DPadRight:
            case GamepadButton.ThumbLeftRight:
                if (!isVisible && scroll != null)
                {
                    int vis = GetFirstVisibleListIndex(lst, scroll, false);
                    if (vis >= 0)
                    {
                        lst.SelectedIndex = vis;
                        FocusListItem(lst, vis);
                        break;
                    }
                }
                NavigateGrid(lst, +1, 0);
                break;

            case GamepadButton.A:
                if (_window is MainWindow mainWinGrid && mainWinGrid.IsEditMode)
                    mainWinGrid.GamepadBeginDrag(currentIndex);
                else
                    EnterGamesActionMode(isGrid: true, lst, currentIndex);
                break;

            case GamepadButton.Y:
                if (_window is MainWindow mainWinGridY && mainWinGridY.IsEditMode && !mainWinGridY.IsGamepadDragging)
                    mainWinGridY.GamepadToggleHide(currentIndex);
                break;
        }
    }

    private void ScrollGamesViewport(double deltaY)
    {
        var scroll = IsGridViewActive()
            ? _window.FindControl<ScrollViewer>("GameGridScrollViewer")
            : _window.FindControl<ScrollViewer>("GameListScrollViewer");

        ScrollViewport(scroll, deltaY);
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

        var scroll = isGrid
            ? _window.FindControl<ScrollViewer>("GameGridScrollViewer")
            : _window.FindControl<ScrollViewer>("GameListScrollViewer");

        int targetIndex = 0;
        if (scroll != null)
        {
            int vis = GetFirstVisibleListIndex(list, scroll, false);
            if (vis >= 0) targetIndex = vis;
        }

        list.SelectedIndex = targetIndex;
        FocusListItem(list, targetIndex);
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

    private int GetFirstVisibleListIndex(ListBox list, ScrollViewer scroll, bool reverse = false)
    {
        int start = reverse ? list.ItemCount - 1 : 0;
        int end = reverse ? -1 : list.ItemCount;
        int step = reverse ? -1 : 1;

        int firstVisibleIndex = -1;
        double firstVisibleY = -1;

        for (int i = start; i != end; i += step)
        {
            var container = list.ContainerFromIndex(i) as Visual;
            if (container != null && IsControlVisibleInScrollViewer(container, scroll))
            {
                if (firstVisibleIndex == -1)
                {
                    firstVisibleIndex = i;
                    firstVisibleY = container.Bounds.Top;
                }
                else
                {
                    if (Math.Abs(container.Bounds.Top - firstVisibleY) >= 5.0)
                    {
                        return i;
                    }
                }
            }
        }
        return firstVisibleIndex;
    }
}
