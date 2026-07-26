using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using OptiscalerClient.Models;
using OptiscalerClient.Views;

namespace OptiscalerClient.Helpers;

// Games tab: drag & drop (Organize Games) and the Quick Install/Manage
// action-lock mode. Split out of GamepadNavigationHelper.Games.cs to keep
// each file under ~500 lines — see gamepad_refactor_plan.md, Fase 2.
public partial class GamepadNavigationHelper
{
    private void HandleGamesDragInput(MainWindow mainWin, GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.DPadUp:
            case GamepadButton.ThumbLeftUp:
                mainWin.GamepadMoveDrag(-1, 0);
                break;

            case GamepadButton.DPadDown:
            case GamepadButton.ThumbLeftDown:
                mainWin.GamepadMoveDrag(1, 0);
                break;

            case GamepadButton.DPadLeft:
            case GamepadButton.ThumbLeftLeft:
                mainWin.GamepadMoveDrag(0, -1);
                break;

            case GamepadButton.DPadRight:
            case GamepadButton.ThumbLeftRight:
                mainWin.GamepadMoveDrag(0, 1);
                break;

            case GamepadButton.A:
                mainWin.GamepadEndDrag(commit: true);
                break;

            case GamepadButton.B:
                mainWin.GamepadEndDrag(commit: false);
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
                ExitGamesActionMode();
                break;
        }
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
}
