using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using OptiscalerClient.Helpers;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace OptiscalerClient.Views
{
    public partial class SteamGridApiGuideWindow : Window, IGamepadInputHost
    {
        private GamepadDialogNavigationHelper? _gamepadHelper;

        GamepadHelperBase? IGamepadInputHost.GamepadHelper => _gamepadHelper;

        public SteamGridApiGuideWindow()
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
        }

        public SteamGridApiGuideWindow(Window? owner)
        {
            InitializeComponent();
            DialogDimHelper.Register(this);

            Opacity = 0;

            if (owner != null)
            {
                var scaling = owner.DesktopScaling;
                double dialogW = 600 * scaling;
                double dialogH = 520 * scaling;

                var x = owner.Position.X + (owner.Bounds.Width * scaling - dialogW) / 2;
                var y = owner.Position.Y + (owner.Bounds.Height * scaling - dialogH) / 2;

                Position = new PixelPoint((int)Math.Max(0, x), (int)Math.Max(0, y));
            }

            var titleBar = this.FindControl<Border>("TitleBar");
            if (titleBar != null)
            {
                titleBar.PointerPressed += (s, e) =>
                {
                    BeginMoveDrag(e);
                };
            }

            Opened += (s, e) =>
            {
                Opacity = 1;
                var rootPanel = this.FindControl<Panel>("RootPanel");
                if (rootPanel != null)
                {
                    AnimationHelper.SetupPanelTransition(rootPanel);
                    rootPanel.Opacity = 1;
                }

                if (_gamepadHelper == null)
                {
                    _gamepadHelper = new GamepadDialogNavigationHelper(this, this.FindControl<ScrollViewer>("MainScrollViewer"));
                }

                if (owner is IGamepadInputHost host)
                    host.GamepadHelper?.SuspendInput();
            };

            Closed += (s, e) =>
            {
                if (owner is IGamepadInputHost closedHost)
                    closedHost.GamepadHelper?.ResumeInput();

                _gamepadHelper?.Dispose();
                _gamepadHelper = null;
            };
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private bool _isAnimatingClose = false;

        private void BtnClose_Click(object sender, RoutedEventArgs e) => _ = CloseAnimated();

        private async Task CloseAnimated()
        {
            if (_isAnimatingClose) return;
            _isAnimatingClose = true;

            DialogDimHelper.HideDimNow(this);
            var rootPanel = this.FindControl<Panel>("RootPanel");
            if (rootPanel != null) rootPanel.Opacity = 0;

            await Task.Delay(220);
            Close();
        }

        private void BtnOpenSteamGridPage_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://www.steamgriddb.com/profile/preferences/api",
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }
    }
}
