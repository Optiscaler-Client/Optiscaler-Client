using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Threading.Tasks;
using OptiscalerClient.Helpers;

namespace OptiscalerClient.Views
{
    public partial class GuideWindow : Window
    {
        public GuideWindow()
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
        }

        public GuideWindow(Window? owner)
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
            WindowScreenFitHelper.FitToScreen(this);

            // 100% Flicker-free startup strategy:
            this.Opacity = 0;
            
            if (owner != null)
            {
                var scaling = owner.DesktopScaling;
                double dialogW = 550 * scaling;
                double dialogH = 620 * scaling;

                var x = owner.Position.X + (owner.Bounds.Width * scaling - dialogW) / 2;
                var y = owner.Position.Y + (owner.Bounds.Height * scaling - dialogH) / 2;
                
                this.Position = new PixelPoint((int)Math.Max(0, x), (int)Math.Max(0, y));
            }

            var titleBar = this.FindControl<Border>("TitleBar");
            if (titleBar != null)
            {
                titleBar.PointerPressed += (s, e) => 
                {
                    this.BeginMoveDrag(e);
                };
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
    }
}
