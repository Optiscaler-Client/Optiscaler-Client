using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace OptiscalerClient.Helpers;

/// <summary>
/// Centers a modal dialog over its owner, clamped to the owner's screen. Avalonia's CenterOwner
/// runs once, before the window is mapped, and on Linux/X11 both the owner's Position (applied by
/// the window manager right after startup) and a SizeToContent dialog's final size arrive
/// asynchronously - so CenterOwner can end up off-center or even on another monitor. This
/// re-centers when the dialog opens, once more after pending window-manager events are processed,
/// and whenever the owner moves while the dialog is open.
/// </summary>
public static class DialogCenterHelper
{
    public static void Register(Window dialog, Window owner)
    {
        // The deferred re-center can run after the dialog (or its owner) was already closed and
        // its platform window disposed, which makes Screens.ScreenFromWindow throw.
        var isOpen = false;
        void CenterIfOpen() { if (isOpen && owner.IsVisible) CenterOnOwner(dialog, owner); }
        void OnOwnerMoved(object? s, PixelPointEventArgs e) => CenterIfOpen();

        dialog.Opened += (_, _) =>
        {
            isOpen = true;
            CenterOnOwner(dialog, owner);
            Dispatcher.UIThread.Post(CenterIfOpen, DispatcherPriority.Background);
            owner.PositionChanged += OnOwnerMoved;
        };
        dialog.Closed += (_, _) =>
        {
            isOpen = false;
            owner.PositionChanged -= OnOwnerMoved;
        };
    }

    public static void CenterOnOwner(Window dialog, Window owner)
    {
        var scaling = owner.DesktopScaling > 0 ? owner.DesktopScaling : 1.0;

        // DesiredSize reflects the latest layout pass even before the platform window has been
        // resized (X11 applies SizeToContent resizes asynchronously, so Bounds can be stale).
        var size = dialog.DesiredSize.Width > 0 && dialog.DesiredSize.Height > 0
            ? dialog.DesiredSize
            : dialog.Bounds.Size;
        var widthPx = (int)(size.Width * scaling);
        var heightPx = (int)(size.Height * scaling);

        var ownerPos = owner.Position;
        var x = ownerPos.X + (int)((owner.Bounds.Width * scaling - widthPx) / 2);
        var y = ownerPos.Y + (int)((owner.Bounds.Height * scaling - heightPx) / 2);

        var screen = owner.Screens?.ScreenFromWindow(owner) ?? owner.Screens?.Primary;
        if (screen != null)
        {
            var working = screen.WorkingArea;
            x = Math.Clamp(x, working.X, Math.Max(working.X, working.X + working.Width - widthPx));
            y = Math.Clamp(y, working.Y, Math.Max(working.Y, working.Y + working.Height - heightPx));
        }

        var target = new PixelPoint(x, y);
        if (dialog.Position != target)
            dialog.Position = target;
    }
}
