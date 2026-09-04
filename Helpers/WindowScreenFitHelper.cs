using System;
using Avalonia;
using Avalonia.Controls;

namespace OptiscalerClient.Helpers;

/// <summary>
/// Keeps a modal window fully within the screen it opens on. These windows use a fixed design
/// Width plus WindowStartupLocation="CenterOwner", which on a small/handheld display (scaled UI,
/// smaller effective viewport than the fixed width) can render partially off-screen — Avalonia's
/// CenterOwner only centers on the owner, it never clamps against screen edges. CanResize is
/// already False on these windows and dragging is already wired separately (BeginMoveDrag on the
/// title bar), so this only needs to fix initial size/position.
/// </summary>
public static class WindowScreenFitHelper
{
    public static void FitToScreen(Window window, double margin = 40)
    {
        window.Opened += (_, _) =>
        {
            var screen = window.Screens?.ScreenFromWindow(window) ?? window.Screens?.Primary;
            if (screen == null) return;

            var working = screen.WorkingArea;
            var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;

            // WorkingArea is in physical pixels; window Width/Height/Position are in DIUs
            // (Position is actually PixelPoint/physical, but the size math below needs DIUs).
            var maxWidth = Math.Max(window.MinWidth, working.Width / scaling - margin);
            var maxHeight = Math.Max(window.MinHeight, working.Height / scaling - margin);

            if (window.Width > maxWidth) window.Width = maxWidth;
            window.MaxWidth = maxWidth;
            window.MaxHeight = maxHeight;

            var widthPx = (int)(window.Width * scaling);
            var heightPx = (int)(window.Height * scaling);
            var pos = window.Position;
            var clampedX = Math.Clamp(pos.X, working.X, Math.Max(working.X, working.X + working.Width - widthPx));
            var clampedY = Math.Clamp(pos.Y, working.Y, Math.Max(working.Y, working.Y + working.Height - heightPx));
            if (clampedX != pos.X || clampedY != pos.Y)
                window.Position = new PixelPoint(clampedX, clampedY);
        };
    }
}
