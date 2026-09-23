using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace OptiscalerClient.Helpers;

/// <summary>
/// Tooltips only open on mouse hover, and a finger has no hover — so on a touchscreen (ROG Ally,
/// Steam Deck, tablets) every "?" help icon was unreachable. This makes a touch tap on an element
/// that carries a tooltip toggle it open, app-wide; the next touch tap anywhere closes it.
///
/// Interactive controls (buttons, combos, text boxes…) are left alone: a tap on them performs
/// their action, and their tooltip stays a mouse-only extra. Mouse and pen are untouched.
/// </summary>
public static class TouchToolTipHelper
{
    private static Control? _openTarget;
    private static bool _registered;

    /// <summary>Class handler on TopLevel: sees every bubbled tap in every window.</summary>
    public static void Register()
    {
        // Idempotent: a second class handler would toggle every tooltip open and straight back shut.
        if (_registered) return;
        _registered = true;
        InputElement.TappedEvent.AddClassHandler<TopLevel>(OnTapped, handledEventsToo: true);
    }

    private static void OnTapped(TopLevel topLevel, TappedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Touch) return;

        var target = FindToolTipTarget(e.Source as Visual);

        if (_openTarget != null && _openTarget != target)
        {
            ToolTip.SetIsOpen(_openTarget, false);
            _openTarget = null;
        }

        if (target == null) return;

        var open = !ToolTip.GetIsOpen(target);
        ToolTip.SetIsOpen(target, open);
        _openTarget = open ? target : null;
    }

    private static Control? FindToolTipTarget(Visual? visual)
    {
        for (var v = visual; v != null; v = v.GetVisualParent())
        {
            if (v is Button or ComboBox or ComboBoxItem or TextBox or Slider) return null;
            if (v is Control control && ToolTip.GetTip(control) != null) return control;
        }
        return null;
    }
}
