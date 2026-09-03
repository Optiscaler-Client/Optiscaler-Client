using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using System;

namespace OptiscalerClient.Views;

public partial class AddDlssEnablerWindow : Window
{
    private static readonly string[] KnownFileNames = { "version.dll", "dlss-enabler-headless.dll" };

    public string? SelectedFilePath { get; private set; }
    public string? SelectedName { get; private set; }

    public AddDlssEnablerWindow()
    {
        InitializeComponent();
    }

    public AddDlssEnablerWindow(Window owner)
        : this()
    {
        Owner = owner;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void BtnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select DLSS Enabler DLL or archive",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("DLL or archive (zip, 7z, rar)")
                {
                    Patterns = new[] { "*.dll", "*.zip", "*.7z", "*.rar" }
                }
            }
        });
        if (files.Count == 0) return;

        SelectedFilePath = files[0].Path.IsAbsoluteUri ? files[0].Path.LocalPath : files[0].TryGetLocalPath();
        var textBox = this.FindControl<TextBox>("TxtFilePath");
        var help = this.FindControl<TextBlock>("TxtAcceptedFiles");
        var isDirectDll = SelectedFilePath?.EndsWith(".dll", System.StringComparison.OrdinalIgnoreCase) == true;
        var hasKnownName = !isDirectDll ||
            Array.Exists(KnownFileNames, n => n.Equals(System.IO.Path.GetFileName(SelectedFilePath ?? string.Empty), System.StringComparison.OrdinalIgnoreCase));

        if (!hasKnownName)
        {
            SelectedFilePath = null;
            if (textBox != null) textBox.Text = null;
            if (help != null)
                help.Foreground = Brushes.OrangeRed;
            UpdateAddEnabled();
            return;
        }

        if (textBox != null) textBox.Text = SelectedFilePath;
        if (help != null)
            help.Foreground = this.FindResource("BrTextSecondary") as IBrush ?? Brushes.Gray;
        UpdateAddEnabled();
    }

    private void TxtName_TextChanged(object? sender, TextChangedEventArgs e) => UpdateAddEnabled();

    private void UpdateAddEnabled()
    {
        var addButton = this.FindControl<Button>("BtnAdd");
        var nameBox = this.FindControl<TextBox>("TxtName");
        if (addButton != null)
            addButton.IsEnabled = !string.IsNullOrEmpty(SelectedFilePath) && !string.IsNullOrWhiteSpace(nameBox?.Text);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void BtnAdd_Click(object? sender, RoutedEventArgs e)
    {
        var nameBox = this.FindControl<TextBox>("TxtName");
        SelectedName = nameBox?.Text?.Trim();
        if (string.IsNullOrEmpty(SelectedFilePath) || string.IsNullOrEmpty(SelectedName)) return;
        Close(true);
    }
}
