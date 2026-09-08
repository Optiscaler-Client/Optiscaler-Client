using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using OptiscalerClient.Services;

namespace OptiscalerClient.Views;

/// <summary>
/// Manual "Add" dialog for RenoDX addons in CacheManagementWindow's "renodx" section. Simpler than
/// the ReShade import dialog it replaces: the wiki links straight to the .addon64/.addon32 file, no
/// archive extraction needed. The game must be picked from the app's own scanned list (CmbGame) —
/// not free text — so a cached addon always maps to a real, known game.
/// </summary>
public partial class AddRenodxAddonWindow : Window
{
    public string? SelectedFilePath { get; private set; }
    public string? GameName { get; private set; }

    public AddRenodxAddonWindow()
    {
        InitializeComponent();
    }

    public AddRenodxAddonWindow(Window owner)
        : this()
    {
        Owner = owner;
        PopulateGames();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void PopulateGames()
    {
        var cmb = this.FindControl<ComboBox>("CmbGame");
        if (cmb == null) return;

        var gameNames = new GamePersistenceService().LoadGames()
            .Select(g => g.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        cmb.ItemsSource = gameNames;
    }

    private async void BtnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select RenoDX addon",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("RenoDX addon (.addon64, .addon32)")
                {
                    Patterns = new[] { "*.addon64", "*.addon32" }
                }
            }
        });
        if (files.Count == 0) return;

        SelectedFilePath = files[0].Path.IsAbsoluteUri ? files[0].Path.LocalPath : files[0].TryGetLocalPath();
        var textBox = this.FindControl<TextBox>("TxtFilePath");
        if (textBox != null) textBox.Text = SelectedFilePath;
        UpdateAddEnabled();
    }

    private void CmbGame_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateAddEnabled();

    private void UpdateAddEnabled()
    {
        var addButton = this.FindControl<Button>("BtnAdd");
        var cmb = this.FindControl<ComboBox>("CmbGame");
        if (addButton != null)
            addButton.IsEnabled = !string.IsNullOrEmpty(SelectedFilePath) && cmb?.SelectedItem is string;
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void BtnAdd_Click(object? sender, RoutedEventArgs e)
    {
        var cmb = this.FindControl<ComboBox>("CmbGame");
        GameName = cmb?.SelectedItem as string;
        if (string.IsNullOrEmpty(SelectedFilePath) || string.IsNullOrEmpty(GameName)) return;
        Close(true);
    }
}
