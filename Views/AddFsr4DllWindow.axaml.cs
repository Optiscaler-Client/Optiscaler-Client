using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using OptiscalerClient.Helpers;
using OptiscalerClient.Models;

namespace OptiscalerClient.Views;

public partial class AddFsr4DllWindow : Window
{
    public string? SelectedFilePath { get; private set; }
    public Fsr4DllVariant SelectedVariant { get; private set; } = Fsr4DllVariant.Int8;

    public AddFsr4DllWindow()
    {
        InitializeComponent();
    }

    public AddFsr4DllWindow(Window owner)
        : this()
    {
        Owner = owner;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void BtnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select FSR 4 DLL package",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Archives (7z, zip, rar) or DLL")
                {
                    Patterns = new[] { "*.7z", "*.zip", "*.rar", "*.dll" }
                }
            }
        });
        if (files.Count == 0) return;

        SelectedFilePath = files[0].Path.IsAbsoluteUri ? files[0].Path.LocalPath : files[0].TryGetLocalPath();
        var textBox = this.FindControl<TextBox>("TxtFilePath");
        var addButton = this.FindControl<Button>("BtnAdd");
        var help = this.FindControl<TextBlock>("TxtAcceptedFiles");
        var isDirectDll = SelectedFilePath?.EndsWith(".dll", System.StringComparison.OrdinalIgnoreCase) == true;
        var hasKnownName = !isDirectDll || Fsr4Int8DllHelper.IsKnownFileName(System.IO.Path.GetFileName(SelectedFilePath ?? string.Empty));

        if (!hasKnownName)
        {
            SelectedFilePath = null;
            if (textBox != null) textBox.Text = null;
            if (addButton != null) addButton.IsEnabled = false;
            if (help != null)
                help.Foreground = Brushes.OrangeRed;
            return;
        }

        if (textBox != null) textBox.Text = SelectedFilePath;
        if (addButton != null) addButton.IsEnabled = true;
        if (help != null)
            help.Foreground = this.FindResource("BrTextSecondary") as IBrush ?? Brushes.Gray;
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void BtnAdd_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(SelectedFilePath)) return;
        SelectedVariant = (this.FindControl<ComboBox>("CmbVariant")?.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "FP8"
            ? Fsr4DllVariant.Fp8 : Fsr4DllVariant.Int8;
        Close(true);
    }
}
