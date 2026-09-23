using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace OptiscalerClient.Views
{
    // ───────────── Small screens / handhelds ─────────────
    // Pure layout, so it lives in the view. On a small screen (Steam Deck, ROG Ally, 768p laptops)
    // the window fills the work area instead of floating, and opens with the Recommended Config
    // sidebar and Detected Components collapsed, a narrower cover column and tighter spacing; independently, the options grid drops from 3 to 2
    // columns whenever it gets too narrow, and the options area scrolls (ScrollInstallOptions) while
    // the Install row stays pinned below it.
    public partial class ManageGameWindow
    {
        /// <summary>Effective (DIP) work area below which the screen counts as small: covers
        /// 1280×800 handhelds, 1920×1080 at 150% (1280×720) and 1366×768 / 1536×864 laptops.</summary>
        private const double SmallScreenMaxWidth = 1500;
        private const double SmallScreenMaxHeight = 900;

        /// <summary>Options grid width below which 3 columns get too cramped for the combos (the
        /// default 1460px window with the sidebar expanded gives it ~710px).</summary>
        private const double TwoColumnOptionsThreshold = 600;

        private const double CompactCoverColumnWidth = 220;
        private const double CompactLeftColumnMargin = 10;
        private const double CompactCoverMaxWidth = CompactCoverColumnWidth - 2 * CompactLeftColumnMargin;

        private int _optionColumns = 3;
        private bool _lastOptionsCompactLayout = true;

        /// <summary>Small screen: the window covers the work area (not movable, no rounded frame).</summary>
        private bool _fillsScreen;

        /// <summary>Detected Components shows a single row of badges; the rest behind "+N".</summary>
        private bool _componentsCollapsed;
        private int _hiddenComponentsCount = -1;

        /// <summary>Must run before <see cref="SetupCompatSidebarToggle"/>: collapsing the sidebar
        /// before its Width transition exists makes it start collapsed instead of animating shut.</summary>
        private void InitializeResponsiveLayout(Window? owner)
        {
            if (IsSmallScreen(owner))
            {
                ApplyCompactLayout();
                BtnToggleCompatSidebar_Click(null, new RoutedEventArgs());
                _componentsCollapsed = true;

                // The debug "resizable" mode is for testing arbitrary sizes, so it keeps floating.
                if (!DebugWindow.MakeManageWindowResizable)
                    EnableFillScreen(owner);
            }

            if (this.FindControl<ListBox>("LstComponents") is { } components)
            {
                components.LayoutUpdated += (_, _) => UpdateComponentsOverflow();

                // Collapsed, the list is a one-row window over a scrollable list: don't let the mouse
                // wheel scroll the badges out of that row (tunnel, so the list's own ScrollViewer
                // never sees it).
                components.AddHandler(PointerWheelChangedEvent, (_, e) =>
                {
                    if (_componentsCollapsed) e.Handled = true;
                }, RoutingStrategies.Tunnel);
            }

            if (this.FindControl<Grid>("GridInstallOptions") is { } optionsGrid)
                optionsGrid.SizeChanged += (_, e) => UpdateOptionColumns(e.NewSize.Width);

            // Fluent's scrollbar overlays the content — make room for it only while it's needed.
            if (this.FindControl<ScrollViewer>("ScrollInstallOptions") is { } scroll
                && this.FindControl<StackPanel>("PnlInstallOptions") is { } options)
            {
                scroll.ScrollChanged += (_, _) =>
                {
                    var scrolls = scroll.Extent.Height > scroll.Viewport.Height + 1;
                    options.Margin = scrolls ? new Thickness(0, 0, 12, 0) : default;
                };
            }

            if (DebugWindow.MakeManageWindowResizable)
                EnableResizing();
        }

        private static bool IsSmallScreen(Window? owner)
        {
            var screen = owner?.Screens.ScreenFromWindow(owner) ?? owner?.Screens.Primary;
            if (screen == null) return false;

            var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
            return screen.WorkingArea.Width / scaling < SmallScreenMaxWidth
                || screen.WorkingArea.Height / scaling < SmallScreenMaxHeight;
        }

        /// <summary>Narrower cover column and ~30-40% less padding/spacing around the cards.</summary>
        private void ApplyCompactLayout()
        {
            if (this.FindControl<Grid>("GridManageRoot") is { } root && root.ColumnDefinitions.Count > 0)
                root.ColumnDefinitions[0].Width = new GridLength(CompactCoverColumnWidth);
            if (this.FindControl<Grid>("GridLeftColumn") is { } left)
                left.Margin = new Thickness(CompactLeftColumnMargin);

            if (this.FindControl<Border>("BdGameCover") is { } cover)
            {
                cover.MaxWidth = CompactCoverMaxWidth;
                cover.MaxHeight = CompactCoverMaxWidth * 1.4;
            }
            if (this.FindControl<Grid>("GridGameTitle") is { } title) title.MaxWidth = CompactCoverMaxWidth;
            if (this.FindControl<Grid>("GridInstallPath") is { } path) path.MaxWidth = CompactCoverMaxWidth;

            if (this.FindControl<Grid>("GridMainContent") is { } main) main.Margin = new Thickness(6, 6, 10, 8);
            if (this.FindControl<Border>("TitleBar") is { } status)
            {
                status.Margin = new Thickness(0, 2, 0, 6);
                status.Padding = new Thickness(12, 6);
            }
            if (this.FindControl<Border>("BdDetectedComponents") is { } detected)
            {
                detected.Margin = new Thickness(0, 0, 0, 6);
                detected.Padding = new Thickness(10, 6);
            }
            if (this.FindControl<StackPanel>("PnlDetectedComponents") is { } detectedContent) detectedContent.Spacing = 4;
            if (this.FindControl<Border>("BdInstallOptionsCard") is { } card) card.Padding = new Thickness(10, 8);
            if (this.FindControl<Grid>("GridInstallOptions") is { } options)
            {
                options.RowSpacing = 6;
                options.ColumnSpacing = 10;
            }
            if (this.FindControl<Grid>("GridInstallActions") is { } actions) actions.Margin = new Thickness(0, 6, 0, 0);
            // ApplyCompatSidebarCap reads this margin back, so the height cap stays exact.
            if (this.FindControl<Border>("PnlCompatSidebar") is { } sidebar) sidebar.Margin = new Thickness(0, 6, 10, 8);
        }

        // ── Fill the screen ─────────────────────────────────────────────────
        // Sized to the screen's work area by hand rather than WindowState.Maximized: this is an
        // undecorated modal dialog, which not every window manager agrees to maximize. The work
        // area also leaves the taskbar/top bar visible — a window exactly monitor-sized would trip
        // Mutter's legacy-fullscreen handling (see MainWindow.Chrome.cs).

        private void EnableFillScreen(Window? owner)
        {
            _fillsScreen = true;

            if (this.FindControl<Panel>("RootPanel") is { } rootPanel) rootPanel.Margin = default;
            if (this.FindControl<Border>("BdManageFrame") is { } frame)
            {
                frame.CornerRadius = default;
                frame.BorderThickness = default;
                frame.BoxShadow = default;
            }

            // Runs after WindowScreenFitHelper's own Opened handler (registered earlier in the
            // constructor), so its MaxWidth/MaxHeight caps can be lifted here. Applied twice: the
            // platform reports the sizes from the window's SizeToContent phase asynchronously, and
            // those late notifications would otherwise overwrite this size — the second pass runs
            // once they've been processed.
            Opened += (_, _) =>
            {
                FillWorkArea(owner);
                Dispatcher.UIThread.Post(() => FillWorkArea(owner), DispatcherPriority.Background);
            };
        }

        private void FillWorkArea(Window? owner)
        {
            var screen = Screens.ScreenFromWindow(this)
                         ?? (owner != null ? owner.Screens.ScreenFromWindow(owner) : null)
                         ?? Screens.Primary;
            if (screen == null) return;

            var working = screen.WorkingArea;
            var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;

            SizeToContent = SizeToContent.Manual;
            MaxWidth = double.PositiveInfinity;
            MaxHeight = double.PositiveInfinity;
            MinHeight = 0;
            Position = working.Position;
            Width = working.Width / scaling;
            Height = working.Height / scaling;
        }

        // ── Detected Components: one row + "+N" ─────────────────────────────

        private void BtnToggleComponents_Click(object? sender, RoutedEventArgs e)
        {
            _componentsCollapsed = !_componentsCollapsed;
            _hiddenComponentsCount = -1; // force a refresh of the toggle
            UpdateComponentsOverflow();
        }

        /// <summary>Runs on every layout pass of the badge list (the list is repopulated whenever the
        /// game is re-analyzed): counts the badges that wrapped past the first row, clips the list
        /// to that row while collapsed and keeps the toggle in sync. Only writes on real changes,
        /// since each write schedules another layout pass.</summary>
        private void UpdateComponentsOverflow()
        {
            if (this.FindControl<ListBox>("LstComponents") is not { } list) return;
            var toggle = this.FindControl<Button>("BtnToggleComponents");
            var more = this.FindControl<TextBlock>("TxtComponentsMore");
            var chevron = this.FindControl<TextBlock>("TxtComponentsChevron");

            var containers = Enumerable.Range(0, list.ItemCount)
                .Select(list.ContainerFromIndex)
                .OfType<Control>()
                .ToList();
            if (containers.Count == 0)
            {
                // Also the pass in between LoadComponents swapping ItemsSource and the new badges
                // being created: forget the cached count, or a same-sized new list would never
                // bring the toggle back.
                if (toggle != null) toggle.IsVisible = false;
                _hiddenComponentsCount = -1;
                if (!double.IsPositiveInfinity(list.MaxHeight)) list.MaxHeight = double.PositiveInfinity;
                return;
            }

            // Badges not arranged yet (all at 0,0,0,0): nothing reliable to measure — wait for the
            // next pass rather than clipping the list to a bogus height.
            if (containers[0].Bounds.Height < 1) return;

            // The list's ScrollViewer is "Hidden" (not "Disabled") on purpose: badges are then
            // measured at their natural height even while MaxHeight clips the list, so rowHeight
            // stays stable. With "Disabled" they'd be squeezed to MaxHeight, which shrinks rowHeight,
            // which shrinks MaxHeight again — an infinite layout loop.
            var firstRowTop = containers[0].Bounds.Y;
            var rowHeight = containers[0].Bounds.Bottom + list.Padding.Top + list.Padding.Bottom;
            var hidden = containers.Count(c => c.Bounds.Y > firstRowTop + 1);
            var maxHeight = _componentsCollapsed ? rowHeight : double.PositiveInfinity;

            if (!list.MaxHeight.Equals(maxHeight)) list.MaxHeight = maxHeight;

            // Collapsed, the visible row must always be the first one.
            if (_componentsCollapsed && list.FindDescendantOfType<ScrollViewer>() is { } scroll
                && scroll.Offset.Y != 0)
                scroll.Offset = new Vector(scroll.Offset.X, 0);

            if (hidden == _hiddenComponentsCount) return;
            _hiddenComponentsCount = hidden;

            if (toggle != null)
            {
                toggle.IsVisible = hidden > 0;
                ToolTip.SetTip(toggle, GetResourceString(
                    _componentsCollapsed ? "TxtComponentsShowAll" : "TxtComponentsShowLess",
                    _componentsCollapsed ? "Show all" : "Show less"));
            }
            if (more != null)
            {
                more.Text = $"+{hidden}";
                more.IsVisible = _componentsCollapsed;
            }
            if (chevron != null) chevron.Text = _componentsCollapsed ? "▾" : "▴";
        }

        private void UpdateOptionColumns(double gridWidth)
        {
            var cols = gridWidth < TwoColumnOptionsThreshold ? 2 : 3;
            if (cols == _optionColumns) return;
            _optionColumns = cols;

            if (this.FindControl<Grid>("GridInstallOptions") is not { } grid) return;
            grid.ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", cols)));

            if (this.FindControl<Grid>("GridExperimentalZone") is { } experimental)
                Grid.SetColumnSpan(experimental, cols);
            if (this.FindControl<Button>("BtnUninstall") is { } uninstall)
                Grid.SetColumn(uninstall, cols - 1);

            UpdateOptionsLayout(_lastOptionsCompactLayout);
        }

        // ── Debug: resizable Manage window ──────────────────────────────────
        // Opt-in from the Debug window, to test the responsive layout at arbitrary sizes. The window
        // has no system decorations, so it needs its own resize grips (like MainWindow's).

        private void EnableResizing()
        {
            CanResize = true;
            MinWidth = 640;
            MinHeight = 420;

            // SizeToContent would snap the height back after every user resize.
            Opened += (_, _) =>
            {
                Height = Bounds.Height;
                SizeToContent = SizeToContent.Manual;
            };

            if (this.FindControl<Panel>("ManageResizeGrips") is not { } grips) return;
            grips.IsVisible = true;
            foreach (var grip in grips.Children.OfType<Border>())
            {
                grip.PointerPressed += (sender, e) =>
                {
                    if (sender is Border { Tag: string edge } && Enum.TryParse<WindowEdge>(edge, out var windowEdge)
                        && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    {
                        BeginResizeDrag(windowEdge, e);
                        e.Handled = true;
                    }
                };
            }
        }
    }
}
