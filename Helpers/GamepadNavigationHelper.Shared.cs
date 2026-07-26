using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace OptiscalerClient.Helpers;

// Generic focus/activation helpers shared by the Sidebar/Games/Profiles
// partials and by the Tab-based fallback in HandleContentInput. Split out
// of GamepadNavigationHelper.cs — see gamepad_refactor_plan.md, Fase 2.
public partial class GamepadNavigationHelper
{
    private void ScrollRightStickViewport(RightStickScrollTarget target, double deltaY)
    {
        switch (target)
        {
            case RightStickScrollTarget.Games:
                ScrollGamesViewport(deltaY);
                break;

            case RightStickScrollTarget.Help:
                ScrollHelpViewport(deltaY);
                break;
        }
    }

    private void ScrollHelpViewport(double deltaY)
    {
        var scroll = _window.FindControl<ScrollViewer>("HelpContentScrollViewer");
        ScrollViewport(scroll, deltaY);
    }

    private static void ScrollViewport(ScrollViewer? scroll, double deltaY)
    {
        if (scroll == null || !scroll.IsVisible)
            return;

        double currentY = scroll.Offset.Y;
        double maxY = System.Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        double targetY = System.Math.Clamp(currentY + deltaY, 0, maxY);

        scroll.Offset = new Vector(scroll.Offset.X, targetY);
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

    private static bool IsInside(Visual focused, Visual container)
    {
        if (focused == container) return true;
        return focused.GetVisualAncestors().Contains(container);
    }

    private void ActivateFocusedElement()
    {
        var topLevel = TopLevel.GetTopLevel(_window);
        var focused = topLevel?.FocusManager?.GetFocusedElement();

        var allDescendants = _window.GetVisualDescendants().ToList();
        var focusedControl = allDescendants.OfType<Control>().FirstOrDefault(x => x.IsFocused);
        if (focusedControl != null)
        {
            focused = focusedControl;
        }

        if (focused == null) return;

        if (TryGetFocusedButton(focused, out var button))
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return;
        }

        if (focused is Visual visual)
        {
            if (visual is ToggleSwitch t) { t.IsChecked = !(t.IsChecked ?? false); return; }
            if (visual is RadioButton r) { r.IsChecked = true; return; }
            if (visual is CheckBox c) { c.IsChecked = !(c.IsChecked ?? false); return; }
            if (visual is ComboBox cb) { cb.IsDropDownOpen = !cb.IsDropDownOpen; return; }

            var ancestorTgl = visual.GetVisualAncestors().OfType<ToggleSwitch>().FirstOrDefault();
            if (ancestorTgl != null) { ancestorTgl.IsChecked = !(ancestorTgl.IsChecked ?? false); return; }

            var ancestorRb = visual.GetVisualAncestors().OfType<RadioButton>().FirstOrDefault();
            if (ancestorRb != null) { ancestorRb.IsChecked = true; return; }

            var ancestorChk = visual.GetVisualAncestors().OfType<CheckBox>().FirstOrDefault();
            if (ancestorChk != null) { ancestorChk.IsChecked = !(ancestorChk.IsChecked ?? false); return; }

            var childTgl = visual.GetVisualDescendants().OfType<ToggleSwitch>().FirstOrDefault();
            if (childTgl != null) { childTgl.IsChecked = !(childTgl.IsChecked ?? false); return; }

            var childRb = visual.GetVisualDescendants().OfType<RadioButton>().FirstOrDefault();
            if (childRb != null) { childRb.IsChecked = true; return; }

            var childChk = visual.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault();
            if (childChk != null) { childChk.IsChecked = !(childChk.IsChecked ?? false); return; }
        }

        SimulateKey(Key.Enter);
        SimulateKey(Key.Space);
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
                && elem is not Avalonia.Controls.Primitives.ScrollBar
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
}
