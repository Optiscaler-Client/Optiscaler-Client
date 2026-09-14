using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using OptiscalerClient.Helpers;

namespace OptiscalerClient.Views
{
    public partial class PromptDialog : Window
    {
        public PromptDialog()
        {
            InitializeComponent();
        }

        public PromptDialog(string title, string label, string defaultValue)
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
            WindowScreenFitHelper.FitToScreen(this);
            
            this.Opacity = 0;
            var titleBar = this.FindControl<Border>("TitleBar");
            if (titleBar != null) titleBar.PointerPressed += (s, e) => this.BeginMoveDrag(e);

            this.Opened += (s, e) =>
            {
                this.Opacity = 1;
                var rootPanel = this.FindControl<Panel>("RootPanel");
                if (rootPanel != null)
                {
                    AnimationHelper.SetupPanelTransition(rootPanel);
                    rootPanel.Opacity = 1;
                }
                
                var input = this.FindControl<TextBox>("TxtInput");
                if (input != null)
                {
                    input.Focus();
                    input.SelectAll();
                }
            };

            var txtTitle = this.FindControl<TextBlock>("TxtTitle");
            if (txtTitle != null) txtTitle.Text = title;
            
            var txtLabel = this.FindControl<TextBlock>("TxtLabel");
            if (txtLabel != null) txtLabel.Text = label;
            
            var txtInput = this.FindControl<TextBox>("TxtInput");
            if (txtInput != null) txtInput.Text = defaultValue;
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void BtnOk_Click(object? sender, RoutedEventArgs e)
        {
            Confirm();
        }

        private void BtnCancel_Click(object? sender, RoutedEventArgs e)
        {
            Close(null);
        }

        private void TxtInput_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Confirm();
            }
            else if (e.Key == Key.Escape)
            {
                Close(null);
            }
        }

        private void Confirm()
        {
            var txtInput = this.FindControl<TextBox>("TxtInput");
            Close(txtInput?.Text?.Trim());
        }
    }
}
