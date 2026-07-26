using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OptiscalerClient.Models;
using OptiscalerClient.Views;

namespace OptiscalerClient.Helpers;

// Sidebar zone: the 4 top-level nav buttons (Games/Profiles/Help/Settings).
// Split out of GamepadNavigationHelper.cs — see gamepad_refactor_plan.md, Fase 2.
public partial class GamepadNavigationHelper
{
    private int FindCheckedSidebarIndex()
    {
        for (int i = 0; i < SidebarButtonNames.Length; i++)
        {
            if (_window.FindControl<RadioButton>(SidebarButtonNames[i])?.IsChecked == true)
                return i;
        }
        return 0;
    }

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

            // Right resumes the currently open tab
            // without changing which tab is active
            case GamepadButton.DPadRight:
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
            if (_sidebarIndex == 1)
            {
                if (_window is MainWindow mw && mw.IsProfileEditorOpen)
                {
                    _window.FindControl<Avalonia.Controls.TextBox>("TxtProfileNameEd")?.Focus(NavigationMethod.Directional);
                }
                else
                {
                    _window.FindControl<Avalonia.Controls.TextBox>("TxtProfileSearchView")?.Focus(NavigationMethod.Directional);
                }
                return;
            }
            if (_sidebarIndex == 2)
            {
                var helpSidebar = _window.FindControl<Avalonia.Controls.StackPanel>("HelpPagesSidebar");
                if (helpSidebar != null && helpSidebar.Children.Count > 0)
                {
                    helpSidebar.Children[0].Focus(NavigationMethod.Directional);
                }
                return;
            }
            if (_sidebarIndex == 3)
            {
                _window.FindControl<Avalonia.Controls.ComboBox>("CmbLanguage")?.Focus(NavigationMethod.Directional);
                return;
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>Switches back to Sidebar mode and restores focus on the current nav button.</summary>
    private void GoToSidebar()
    {
        ExitGamesActionMode(restoreGameFocus: false);
        _mode = GamepadNavigationMode.Sidebar;
        FocusSidebarButton(_sidebarIndex);
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
}
