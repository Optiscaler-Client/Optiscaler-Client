using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using OptiscalerClient.Helpers;

namespace OptiscalerClient.Views
{
    public partial class UpdateAvailableWindow : Window
    {
        public UpdateAvailableWindow(Window owner, string latestVersion)
        {
            InitializeComponent();
            DialogDimHelper.Register(this);

            // Flicker-free startup: start invisible, show after positioning
            this.Opacity = 0;

            var scaling = owner.DesktopScaling;
            double dialogW = 460 * scaling;
            double dialogH = 260 * scaling;
            var x = owner.Position.X + (owner.Bounds.Width * scaling - dialogW) / 2;
            var y = owner.Position.Y + (owner.Bounds.Height * scaling - dialogH) / 2;
            this.Position = new PixelPoint((int)Math.Max(0, x), (int)Math.Max(0, y));

            var titleBar = this.FindControl<Border>("TitleBar");
            if (titleBar != null)
                titleBar.PointerPressed += (s, e) => this.BeginMoveDrag(e);

            var messageText = this.FindControl<TextBlock>("TxtUpdateMessage");
            if (messageText != null)
            {
                var format = GetResourceString("TxtUpdateAvailablePopupMessage",
                    "A new version of OptiScaler Client is available: v{0}. You're currently using v{1}.");
                messageText.Text = string.Format(format, latestVersion, App.AppVersion);
            }

            this.Opened += (s, e) =>
            {
                this.Opacity = 1;
                var rootPanel = this.FindControl<Panel>("RootPanel");
                if (rootPanel != null)
                {
                    AnimationHelper.SetupPanelTransition(rootPanel);
                    rootPanel.Opacity = 1;
                }
            };
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private bool _isAnimatingClose = false;

        private void BtnLater_Click(object sender, RoutedEventArgs e) => _ = CloseAnimated();

        private void BtnViewOnGithub_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/Optiscaler-Client/Optiscaler-Client/releases",
                    UseShellExecute = true
                });
            }
            catch { }

            _ = CloseAnimated();
        }

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

        private string GetResourceString(string key, string fallback)
        {
            return Application.Current?.TryFindResource(key, out var res) == true && res is string str ? str : fallback;
        }
    }
}
