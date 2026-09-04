using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace OptiscalerClient.Helpers;

/// <summary>
/// Makes a region (typically a custom title bar) drag-move the window it belongs to.
/// Window.BeginMoveDrag hands off to the OS's native window-move (SC_MOVE on Windows), which is
/// mouse-only — a touchscreen/handheld press never reliably starts that native drag. This tracks
/// the pointer manually instead (screen-space delta applied to Window.Position), which works the
/// same for mouse, pen, and touch.
/// </summary>
public static class WindowDragHelper
{
    public static void EnableDrag(Window window, Control dragHandle)
    {
        PixelPoint? startPointerScreenPos = null;
        PixelPoint? startWindowPos = null;

        dragHandle.PointerPressed += (_, e) =>
        {
            // Don't hijack presses on interactive children (e.g. the Close button) inside the
            // drag handle — BeginMoveDrag never had this problem since the native OS drag doesn't
            // steal Avalonia's own pointer routing, but capturing the pointer here would.
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) != null)
                return;

            startPointerScreenPos = window.PointToScreen(e.GetPosition(window));
            startWindowPos = window.Position;
            e.Pointer.Capture(dragHandle);
        };

        dragHandle.PointerMoved += (_, e) =>
        {
            if (startPointerScreenPos == null || startWindowPos == null) return;
            var current = window.PointToScreen(e.GetPosition(window));
            var delta = current - startPointerScreenPos.Value;
            window.Position = new PixelPoint(startWindowPos.Value.X + delta.X, startWindowPos.Value.Y + delta.Y);
        };

        dragHandle.PointerReleased += (_, __) =>
        {
            startPointerScreenPos = null;
            startWindowPos = null;
        };
        dragHandle.PointerCaptureLost += (_, __) =>
        {
            startPointerScreenPos = null;
            startWindowPos = null;
        };
    }
}
