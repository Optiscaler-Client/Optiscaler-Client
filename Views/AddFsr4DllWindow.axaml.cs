using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
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
            Title = "Select FSR 4 DLL archive",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Archives (7z, zip, rar)")
                {
                    Patterns = new[] { "*.7z", "*.zip", "*.rar" }
                }
            }
        });
        if (files.Count == 0) return;

        SelectedFilePath = files[0].Path.IsAbsoluteUri ? files[0].Path.LocalPath : files[0].TryGetLocalPath();
        var textBox = this.FindControl<TextBox>("TxtFilePath");
        var addButton = this.FindControl<Button>("BtnAdd");

        if (textBox != null) textBox.Text = SelectedFilePath;
        if (addButton != null) addButton.IsEnabled = true;
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
