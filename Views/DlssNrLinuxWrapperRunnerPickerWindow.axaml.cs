using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OptiscalerClient.Services;

namespace OptiscalerClient.Views;

/// <summary>
/// One-time picker for which installed Wine/Proton runner guentra's DLSS-NR-on-AMD-Linux fork
/// should target for this game (see DlssNrLinuxWrapperService.ListRunnersAsync) — only shown when
/// auto-detection found more than one candidate (or none at all, in which case the list is empty and
/// only "Browse folder" is useful). The caller caches the result on Game.DlssNrLinuxWrapperRunnerPath
/// so this never has to ask again for the same game.
/// </summary>
public partial class DlssNrLinuxWrapperRunnerPickerWindow : Window
{
    /// <summary>The chosen runner root folder, or null if the dialog was cancelled.</summary>
    public string? SelectedPath { get; private set; }

    public DlssNrLinuxWrapperRunnerPickerWindow() => InitializeComponent();

    public DlssNrLinuxWrapperRunnerPickerWindow(Window owner, List<LinuxWrapperRunner> runners) : this()
    {
        Owner = owner;

        var titleBar = this.FindControl<Border>("TitleBar");
        if (titleBar != null)
            titleBar.PointerPressed += (_, e) => BeginMoveDrag(e);

        var list = this.FindControl<ListBox>("LstRunners")!;
        foreach (var runner in runners.OrderByDescending(r => r.Compatible))
        {
            var item = new ListBoxItem
            {
                Content = runner.Path,
                Tag = runner.Path,
                IsEnabled = runner.Compatible,
            };
            if (!string.IsNullOrEmpty(runner.Reason))
                ToolTip.SetTip(item, runner.Reason);
            list.Items.Add(item);
        }
        if (list.Items.Count > 0 && list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.IsEnabled) is { } firstEnabled)
            list.SelectedItem = firstEnabled;
    }

    private void ShowError(string? message)
    {
        var txt = this.FindControl<TextBlock>("TxtError");
        if (txt == null) return;
        txt.Text = message ?? "";
        txt.IsVisible = !string.IsNullOrEmpty(message);
    }

    private void BtnUseSelected_Click(object? sender, RoutedEventArgs e)
    {
        var list = this.FindControl<ListBox>("LstRunners");
        var path = (list?.SelectedItem as ListBoxItem)?.Tag as string;
        if (string.IsNullOrEmpty(path))
        {
            ShowError(GetResourceString("TxtSetupNrLinuxWrapperRunnerNoneSelected", "Select a runner first, or use \"Browse folder\"."));
            return;
        }
        SelectedPath = path;
        Close(true);
    }

    private async void BtnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = GetResourceString("TxtSetupNrLinuxWrapperRunnerBrowseTitle", "Select the Wine/Proton runner folder"),
            AllowMultiple = false,
        });
        if (folders.Count == 0) return;

        var path = folders[0].Path.IsAbsoluteUri ? folders[0].Path.LocalPath : folders[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        SelectedPath = path;
        Close(true);
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close(false);

    private string GetResourceString(string key, string fallback)
    {
        return Application.Current?.TryFindResource(key, out var res) == true && res is string str ? str : fallback;
    }
}
