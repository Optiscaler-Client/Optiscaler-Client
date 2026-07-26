using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using OptiscalerClient.Models;
using OptiscalerClient.Views;

namespace OptiscalerClient.Helpers;

// Profiles tab (list + search) and the Profile Editor's sidebar/content
// navigation. Split out of GamepadNavigationHelper.cs — see
// gamepad_refactor_plan.md, Fase 2.
public partial class GamepadNavigationHelper
{
    private enum ProfilesSubZone { ActionSidebar, ContentList }

    private ProfilesSubZone GetProfilesSubZone(IInputElement? focused)
    {
        if (focused is not Avalonia.Visual v) return ProfilesSubZone.ActionSidebar;

        var txtSearch = _window.FindControl<Avalonia.Controls.TextBox>("TxtProfileSearchView");
        var pnlList = _window.FindControl<Avalonia.Controls.StackPanel>("PnlProfilesView");

        if ((txtSearch != null && IsInside(v, txtSearch)) ||
            (pnlList != null && IsInside(v, pnlList)))
        {
            return ProfilesSubZone.ContentList;
        }

        return ProfilesSubZone.ActionSidebar;
    }

    private void HandleProfilesInput(GamepadButton button, IInputElement? focused)
    {
        var zone = GetProfilesSubZone(focused);

        if (zone == ProfilesSubZone.ActionSidebar)
        {
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
                case GamepadButton.DPadLeft:
                case GamepadButton.ThumbLeftLeft:
                    GoToSidebar();
                    break;
                case GamepadButton.DPadRight:
                case GamepadButton.ThumbLeftRight:
                    var txtSearch = _window.FindControl<Avalonia.Controls.TextBox>("TxtProfileSearchView");
                    if (txtSearch != null)
                        txtSearch.Focus(NavigationMethod.Directional);
                    break;
                case GamepadButton.A:
                    ActivateFocusedElement();
                    break;
            }
        }
        else // ContentList
        {
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
                case GamepadButton.DPadLeft:
                case GamepadButton.ThumbLeftLeft:
                    var btnNew = _window.FindControl<Avalonia.Controls.Button>("BtnNewProfileView");
                    if (btnNew != null)
                        btnNew.Focus(NavigationMethod.Directional);
                    break;
                case GamepadButton.DPadRight:
                case GamepadButton.ThumbLeftRight:
                    break; // Do nothing
                case GamepadButton.A:
                    if (focused is Avalonia.Controls.TextBox tb && tb.Name == "TxtProfileSearchView" && _window is MainWindow mw)
                    {
                        mw.OpenVirtualKeyboard("TxtProfileSearchView");
                        return;
                    }
                    ActivateFocusedElement();
                    break;
            }
        }
    }

    private void HandleProfileEditorInput(GamepadButton button, IInputElement? focused)
    {
        if (button == GamepadButton.B)
        {
            var btnBack = _window.FindControl<Avalonia.Controls.Button>("BtnEditorBack");
            if (btnBack != null)
                btnBack.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            return;
        }

        if (button == GamepadButton.A)
        {
            if (focused is Avalonia.Controls.TextBox tb && _window is MainWindow mw)
            {
                mw.OpenVirtualKeyboard(tb.Name ?? "");
                return;
            }
            ActivateFocusedElement();
            return;
        }

        var txtName = _window.FindControl<Avalonia.Controls.TextBox>("TxtProfileNameEd");
        var txtDesc = _window.FindControl<Avalonia.Controls.TextBox>("TxtDescriptionEd");
        var btnEasy = _window.FindControl<Avalonia.Controls.Button>("BtnEasyModeEd");
        var btnAdv = _window.FindControl<Avalonia.Controls.Button>("BtnAdvancedModeEd");
        var sidebarNav = _window.FindControl<Avalonia.Controls.StackPanel>("SidebarNavEd");
        var settingsSearch = _window.FindControl<Avalonia.Controls.TextBox>("TxtSettingsSearchEd");

        bool isInSidebar = false;
        if (focused is Avalonia.Controls.Control c)
        {
            if (c == txtName || c == txtDesc || c == btnEasy || c == btnAdv)
                isInSidebar = true;
            else if (sidebarNav != null && IsInside(c, sidebarNav))
                isInSidebar = true;
        }

        if (isInSidebar)
        {
            switch (button)
            {
                case GamepadButton.DPadUp:
                case GamepadButton.ThumbLeftUp:
                    if (focused == btnEasy || focused == btnAdv)
                        txtDesc?.Focus(NavigationMethod.Directional);
                    else if (focused == txtDesc)
                        txtName?.Focus(NavigationMethod.Directional);
                    else if (sidebarNav != null && focused is Avalonia.Controls.Control fc && IsInside(fc, sidebarNav))
                    {
                        int idx = sidebarNav.Children.IndexOf(fc);
                        if (idx > 1)
                            sidebarNav.Children[idx - 1].Focus(NavigationMethod.Directional);
                        else
                            btnEasy?.Focus(NavigationMethod.Directional);
                    }
                    break;

                case GamepadButton.DPadDown:
                case GamepadButton.ThumbLeftDown:
                    if (focused == txtName)
                        txtDesc?.Focus(NavigationMethod.Directional);
                    else if (focused == txtDesc)
                        btnEasy?.Focus(NavigationMethod.Directional);
                    else if (focused == btnEasy || focused == btnAdv)
                    {
                        if (sidebarNav != null && sidebarNav.Children.Count > 1)
                            sidebarNav.Children[1].Focus(NavigationMethod.Directional);
                    }
                    else if (sidebarNav != null && focused is Avalonia.Controls.Control fc && IsInside(fc, sidebarNav))
                    {
                        int idx = sidebarNav.Children.IndexOf(fc);
                        if (idx >= 1 && idx < sidebarNav.Children.Count - 1)
                            sidebarNav.Children[idx + 1].Focus(NavigationMethod.Directional);
                    }
                    break;

                case GamepadButton.DPadRight:
                case GamepadButton.ThumbLeftRight:
                    if (focused == btnEasy)
                    {
                        btnAdv?.Focus(NavigationMethod.Directional);
                    }
                    else if (sidebarNav != null && focused is Avalonia.Controls.Button navBtn && navBtn.Tag is string secName && _window is MainWindow mwMain)
                    {
                        var targetCtrl = mwMain.GetFirstSettingControlForSection(secName);
                        if (targetCtrl != null)
                        {
                            targetCtrl.Focus(NavigationMethod.Directional);
                        }
                        else
                        {
                            settingsSearch?.Focus(NavigationMethod.Directional);
                        }
                    }
                    else
                    {
                        settingsSearch?.Focus(NavigationMethod.Directional);
                    }
                    break;

                case GamepadButton.DPadLeft:
                case GamepadButton.ThumbLeftLeft:
                    if (focused == btnAdv)
                    {
                        btnEasy?.Focus(NavigationMethod.Directional);
                    }
                    break;
            }
        }
        else
        {
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
                case GamepadButton.DPadRight:
                case GamepadButton.ThumbLeftRight:
                    SimulateKey(Key.Tab);
                    break;
                case GamepadButton.DPadLeft:
                case GamepadButton.ThumbLeftLeft:
                    if (sidebarNav != null && sidebarNav.Children.Count > 1)
                        sidebarNav.Children[1].Focus(NavigationMethod.Directional);
                    else
                        btnEasy?.Focus(NavigationMethod.Directional);
                    break;
            }
        }
    }
}
