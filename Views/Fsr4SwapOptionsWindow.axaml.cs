using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using OptiscalerClient.Helpers;

namespace OptiscalerClient.Views
{
    /// <summary>
    /// "FSR 4 Swap Options" modal (Manage Default Versions). Lets the user pick between being asked
    /// which packaged files to swap/copy every time (current behavior), or pre-selecting a fixed set
    /// of files to use silently from then on — see ExecuteDllSwapAsync (ManageGameWindow) and
    /// RunBulkDllSwapAsync (BulkInstallWindow), the two consumers of AskEveryTime/SelectedFileKeys.
    /// </summary>
    public partial class Fsr4SwapOptionsWindow : Window
    {
        private readonly List<CheckBox> _fileCheckboxes = new();

        public bool AskEveryTime { get; private set; } = true;
        public List<string> SelectedFileKeys { get; private set; } = new();

        public Fsr4SwapOptionsWindow()
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
        }

        public Fsr4SwapOptionsWindow(Window owner, bool askEveryTime, List<string> selectedFileKeys) : this()
        {
            Owner = owner;

            var rbAsk = this.FindControl<RadioButton>("RbAskEveryTime");
            var rbDefaults = this.FindControl<RadioButton>("RbChooseDefaults");
            var container = this.FindControl<Border>("PnlDefaultFilesContainer");
            var panel = this.FindControl<StackPanel>("PnlDefaultFiles");
            var textPrimary = Application.Current?.FindResource("BrTextPrimary") as IBrush ?? Brushes.White;

            var effectiveKeys = selectedFileKeys.Count > 0 ? selectedFileKeys : Fsr4Int8DllHelper.LogicalFileKeys.ToList();
            foreach (var key in Fsr4Int8DllHelper.LogicalFileKeys)
            {
                var cb = new CheckBox
                {
                    Content = Fsr4Int8DllHelper.GetLogicalKeyDisplayName(key),
                    Foreground = textPrimary,
                    IsChecked = effectiveKeys.Contains(key),
                    Tag = key
                };
                _fileCheckboxes.Add(cb);
                panel?.Children.Add(cb);
            }

            void UpdateVisibility() { if (container != null) container.IsVisible = rbDefaults?.IsChecked == true; }

            if (rbAsk != null) rbAsk.IsChecked = askEveryTime;
            if (rbDefaults != null) rbDefaults.IsChecked = !askEveryTime;
            UpdateVisibility();

            if (rbAsk != null) rbAsk.IsCheckedChanged += (s, e) => UpdateVisibility();
            if (rbDefaults != null) rbDefaults.IsCheckedChanged += (s, e) => UpdateVisibility();
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);

        private void BtnSave_Click(object? sender, RoutedEventArgs e)
        {
            var rbAsk = this.FindControl<RadioButton>("RbAskEveryTime");
            AskEveryTime = rbAsk?.IsChecked == true;
            SelectedFileKeys = _fileCheckboxes.Where(c => c.IsChecked == true).Select(c => (string)c.Tag!).ToList();
            Close(true);
        }
    }
}
