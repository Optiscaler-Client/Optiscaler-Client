using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OptiscalerClient.Helpers;

namespace OptiscalerClient.Views
{
    // ───────────── Custom title bar (SystemDecorations="None") ─────────────
    // Pure window chrome, so it lives in the view: dragging, the three caption buttons and edge
    // resizing are all things the window manager did before. Same behavior on Windows and Linux.
    public partial class MainWindow
    {
        private const double FrameMargin = 12;   // transparent shadow band around the restored window
        private const double FrameRadius = 12;

        private Border? _windowShadow;
        private Border? _windowFrame;
        private Border? _windowOutline;
        private Panel? _resizeGrips;
        private Button? _btnWindowMaximize;
        private PathIcon? _iconWindowMaximize;
        private PathIcon? _iconWindowRestore;
        private IDisposable? _maximizeTipBinding;

        private void InitializeChrome()
        {
            _windowShadow = this.FindControl<Border>("WindowShadow");
            _windowFrame = this.FindControl<Border>("WindowFrame");
            _windowOutline = this.FindControl<Border>("WindowOutline");
            _resizeGrips = this.FindControl<Panel>("ResizeGrips");
            _btnWindowMaximize = this.FindControl<Button>("BtnWindowMaximize");
            _iconWindowMaximize = this.FindControl<PathIcon>("IconWindowMaximizeGlyph");
            _iconWindowRestore = this.FindControl<PathIcon>("IconWindowRestoreGlyph");

            foreach (var dragArea in new Control?[] { this.FindControl<Grid>("TitleBar"), this.FindControl<Border>("SidebarArea") })
            {
                if (dragArea == null) continue;
                dragArea.PointerPressed += OnDragAreaPressed;
                // BeginMoveDrag hands off to the OS's native move, which on Windows is mouse-only:
                // touch (handhelds) drags the window manually instead, and double-taps to maximize.
                WindowDragHelper.EnableDrag(this, dragArea,
                    e => e.Pointer.Type == PointerType.Touch && WindowState == WindowState.Normal);
                dragArea.DoubleTapped += (_, e) =>
                {
                    if (e.Pointer.Type != PointerType.Touch) return;
                    if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) != null) return;
                    ToggleMaximize();
                };
            }

            if (this.FindControl<Button>("BtnWindowMinimize") is { } btnMinimize)
                btnMinimize.Click += (_, _) => WindowState = WindowState.Minimized;
            if (_btnWindowMaximize != null)
                _btnWindowMaximize.Click += (_, _) => ToggleMaximize();
            if (this.FindControl<Button>("BtnWindowClose") is { } btnClose)
                btnClose.Click += (_, _) => Close();

            if (_resizeGrips != null)
                foreach (var grip in _resizeGrips.Children.OfType<Border>())
                    grip.PointerPressed += OnResizeGripPressed;

            PropertyChanged += (_, e) =>
            {
                if (e.Property == WindowStateProperty) OnChromeWindowStateChanged();
            };

            UpdateChromeForState();
        }

        private void OnDragAreaPressed(object? sender, PointerPressedEventArgs e)
        {
            // Touch is WindowDragHelper's (see InitializeChrome) — don't mark it handled here.
            if (e.Pointer.Type == PointerType.Touch) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            if (e.ClickCount == 2)
                ToggleMaximize();
            else
                BeginMoveDrag(e);
            e.Handled = true;
        }

        private void OnResizeGripPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Border { Tag: string edge } && Enum.TryParse<WindowEdge>(edge, out var windowEdge)
                && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginResizeDrag(windowEdge, e);
                e.Handled = true;
            }
        }

        private void ToggleMaximize() =>
            WindowState = WindowState == WindowState.Normal ? WindowState.Maximized : WindowState.Normal;

        private void UpdateChromeForState()
        {
            var maximized = WindowState is WindowState.Maximized or WindowState.FullScreen;
            var normal = WindowState == WindowState.Normal;

            // Restored: floating rounded card with an outline and a shadow. Maximized: square and
            // edge to edge.
            var radius = new CornerRadius(normal ? FrameRadius : 0);
            if (_windowShadow != null)
            {
                _windowShadow.Margin = new Thickness(normal ? FrameMargin : 0);
                _windowShadow.CornerRadius = radius;
                _windowShadow.BoxShadow = normal ? BoxShadows.Parse("0 4 14 0 #80000000") : default;
            }
            if (_windowFrame != null) _windowFrame.CornerRadius = radius;
            if (_windowOutline != null)
            {
                _windowOutline.CornerRadius = radius;
                _windowOutline.BorderThickness = new Thickness(normal ? 1 : 0);
            }
            if (_iconWindowMaximize != null) _iconWindowMaximize.IsVisible = !maximized;
            if (_iconWindowRestore != null) _iconWindowRestore.IsVisible = maximized;
            if (_resizeGrips != null) _resizeGrips.IsVisible = !maximized;

            if (_btnWindowMaximize != null)
            {
                _maximizeTipBinding?.Dispose();
                _maximizeTipBinding = _btnWindowMaximize.Bind(ToolTip.TipProperty,
                    this.GetResourceObservable(maximized ? "TxtWindowRestore" : "TxtWindowMaximize"));
            }
        }

        private void OnChromeWindowStateChanged()
        {
            UpdateChromeForState();

            // GNOME/Mutter treats an undecorated window that ends up exactly monitor-sized as a legacy
            // fullscreen game: it covers the top bar, "restore" snaps back to monitor size and minimize
            // stops responding (e.g. a maximized window dragged between monitors). The app never asks
            // for fullscreen, so undo it, and never let a restored window be monitor-sized.
            if (WindowState == WindowState.FullScreen)
                Dispatcher.UIThread.Post(() => WindowState = WindowState.Maximized);
            else if (WindowState == WindowState.Normal)
                Dispatcher.UIThread.Post(EnsureFloatingSize, DispatcherPriority.Background);
        }

        /// <summary>Shrinks and centers the restored window when it was restored to (almost) the
        /// full monitor size — which would re-trigger Mutter's legacy fullscreen.</summary>
        private void EnsureFloatingSize()
        {
            if (WindowState != WindowState.Normal) return;
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen is null) return;

            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            var width = Bounds.Width * scale;
            var height = Bounds.Height * scale;
            if (width < area.Width * 0.95 && height < area.Height * 0.95) return;

            var newWidth = Math.Max(MinWidth, area.Width * 0.75 / scale);
            var newHeight = Math.Max(MinHeight, area.Height * 0.8 / scale);
            Width = newWidth;
            Height = newHeight;
            Position = new PixelPoint(
                area.X + (int)((area.Width - newWidth * scale) / 2),
                area.Y + (int)((area.Height - newHeight * scale) / 2));
        }
    }
}
