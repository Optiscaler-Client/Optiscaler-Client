using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using OptiscalerClient.Helpers;
using OptiscalerClient.Models;
using OptiscalerClient.Services;

namespace OptiscalerClient.Views
{
    public partial class ManageScanSourcesWindow : Window
    {
        private readonly ComponentManagementService _componentService;
        private readonly List<string> _customFolders = new();

        public ManageScanSourcesWindow()
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
            _componentService = new ComponentManagementService();
        }

        public ManageScanSourcesWindow(Window owner, ComponentManagementService componentService)
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
            _componentService = componentService;

            this.Opacity = 0;

            var titleBar = this.FindControl<Border>("TitleBar");
            if (titleBar != null)
            {
                titleBar.PointerPressed += (s, e) => this.BeginMoveDrag(e);
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

            LoadCurrentSettings();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void LoadCurrentSettings()
        {
            var config = _componentService.Config.ScanSources;

            var tglSteam = this.FindControl<ToggleSwitch>("TglSteam");
            var tglHeroic = this.FindControl<ToggleSwitch>("TglHeroic");
            var tglEpic = this.FindControl<ToggleSwitch>("TglEpic");
            var tglGOG = this.FindControl<ToggleSwitch>("TglGOG");
            var tglXbox = this.FindControl<ToggleSwitch>("TglXbox");
            var tglEA = this.FindControl<ToggleSwitch>("TglEA");
            var tglUbisoft = this.FindControl<ToggleSwitch>("TglUbisoft");

            if (tglSteam != null) tglSteam.IsChecked = config.ScanSteam;
            if (tglHeroic != null) tglHeroic.IsChecked = config.ScanHeroic;
            if (tglEpic != null) tglEpic.IsChecked = config.ScanEpic;
            if (tglGOG != null) tglGOG.IsChecked = config.ScanGOG;
            if (tglXbox != null) tglXbox.IsChecked = config.ScanXbox;
            if (tglEA != null) tglEA.IsChecked = config.ScanEA;
            if (tglUbisoft != null) tglUbisoft.IsChecked = config.ScanUbisoft;

            SetFilterMode(config.UpscalerFilter);

            var isWindows = OperatingSystem.IsWindows();
            var gridEpic = this.FindControl<Grid>("GridEpic");
            var gridGOG = this.FindControl<Grid>("GridGOG");
            var gridXbox = this.FindControl<Grid>("GridXbox");
            var gridEA = this.FindControl<Grid>("GridEA");
            var gridUbisoft = this.FindControl<Grid>("GridUbisoft");
            if (gridEpic != null) gridEpic.IsVisible = isWindows;
            if (gridGOG != null) gridGOG.IsVisible = isWindows;
            if (gridXbox != null) gridXbox.IsVisible = isWindows;
            if (gridEA != null) gridEA.IsVisible = isWindows;
            if (gridUbisoft != null) gridUbisoft.IsVisible = isWindows;

            _customFolders.Clear();
            _customFolders.AddRange(config.CustomFolders);
            RefreshCustomFoldersList();
        }

        private void SetFilterMode(UpscalerFilterMode mode)
        {
            var rb1 = this.FindControl<RadioButton>("RbFilterShowAll");
            var rb2 = this.FindControl<RadioButton>("RbFilterHideWithoutUpscaler");
            var rb3 = this.FindControl<RadioButton>("RbFilterSkipWithoutUpscaler");
            if (rb1 == null || rb2 == null || rb3 == null) return;
            rb1.IsChecked = mode == UpscalerFilterMode.ShowAll;
            rb2.IsChecked = mode == UpscalerFilterMode.HideWithoutUpscaler;
            rb3.IsChecked = mode == UpscalerFilterMode.SkipWithoutUpscaler;
        }

        private UpscalerFilterMode GetSelectedFilterMode()
        {
            if (this.FindControl<RadioButton>("RbFilterHideWithoutUpscaler")?.IsChecked == true)
                return UpscalerFilterMode.HideWithoutUpscaler;
            if (this.FindControl<RadioButton>("RbFilterSkipWithoutUpscaler")?.IsChecked == true)
                return UpscalerFilterMode.SkipWithoutUpscaler;
            return UpscalerFilterMode.ShowAll;
        }

        private void RefreshCustomFoldersList()
        {
            var pnlCustomFolders = this.FindControl<StackPanel>("PnlCustomFolders");
            var txtNoCustomFolders = this.FindControl<TextBlock>("TxtNoCustomFolders");

            if (pnlCustomFolders == null) return;

            pnlCustomFolders.Children.Clear();

            if (_customFolders.Count == 0)
            {
                if (txtNoCustomFolders != null)
                {
                    pnlCustomFolders.Children.Add(txtNoCustomFolders);
                }
                return;
            }

            foreach (var folder in _customFolders)
            {
                var card = CreateFolderCard(folder);
                pnlCustomFolders.Children.Add(card);
            }
        }

        private Border CreateFolderCard(string folderPath)
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*, Auto")
            };

            var txtPath = new TextBlock
            {
                Text = folderPath,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = Application.Current?.FindResource("BrTextPrimary") as IBrush ?? Brushes.White,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 12, 0)
            };

            var btnRemove = new Button
            {
                Content = Application.Current?.FindResource("TxtRemove") as string ?? "Remove",
                Classes = { "BtnSecondary" },
                Padding = new Thickness(12, 4),
                FontSize = 11,
                Tag = folderPath
            };
            btnRemove.Click += BtnRemoveFolder_Click;

            grid.Children.Add(txtPath);
            Grid.SetColumn(txtPath, 0);

            grid.Children.Add(btnRemove);
            Grid.SetColumn(btnRemove, 1);

            return new Border
            {
                Background = Application.Current?.FindResource("BrBgSurface") as IBrush ?? Brushes.Transparent,
                BorderBrush = Application.Current?.FindResource("BrBorderSubtle") as IBrush ?? Brushes.DimGray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8),
                Child = grid
            };
        }

        private async void BtnAddFolder_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select Game Folder",
                    AllowMultiple = false
                });

                if (folders != null && folders.Count > 0)
                {
                    var folder = folders[0];
                    var selectedPath = folder.Path.IsAbsoluteUri
                        ? folder.Path.LocalPath
                        : folder.TryGetLocalPath();

                    if (string.IsNullOrEmpty(selectedPath) || !Directory.Exists(selectedPath))
                        return;

                    if (!_customFolders.Contains(selectedPath))
                    {
                        _customFolders.Add(selectedPath);
                        RefreshCustomFoldersList();
                    }
                }
            }
            catch (Exception ex) { DebugWindow.Log($"[ScanSources] Add folder failed: {ex.Message}"); }
        }

        private void BtnRemoveFolder_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string folderPath)
            {
                _customFolders.Remove(folderPath);
                RefreshCustomFoldersList();
            }
        }

        private void BtnSave_Click(object? sender, RoutedEventArgs e)
        {
            var tglSteam = this.FindControl<ToggleSwitch>("TglSteam");
            var tglHeroic = this.FindControl<ToggleSwitch>("TglHeroic");
            var tglEpic = this.FindControl<ToggleSwitch>("TglEpic");
            var tglGOG = this.FindControl<ToggleSwitch>("TglGOG");
            var tglXbox = this.FindControl<ToggleSwitch>("TglXbox");
            var tglEA = this.FindControl<ToggleSwitch>("TglEA");
            var tglUbisoft = this.FindControl<ToggleSwitch>("TglUbisoft");

            _componentService.Config.ScanSources.ScanSteam = tglSteam?.IsChecked ?? true;
            _componentService.Config.ScanSources.ScanHeroic = tglHeroic?.IsChecked ?? true;
            _componentService.Config.ScanSources.ScanEpic = tglEpic?.IsChecked ?? true;
            _componentService.Config.ScanSources.ScanGOG = tglGOG?.IsChecked ?? true;
            _componentService.Config.ScanSources.ScanXbox = tglXbox?.IsChecked ?? true;
            _componentService.Config.ScanSources.ScanEA = tglEA?.IsChecked ?? true;
            _componentService.Config.ScanSources.ScanUbisoft = tglUbisoft?.IsChecked ?? true;
            _componentService.Config.ScanSources.CustomFolders = _customFolders.ToList();
            _componentService.Config.ScanSources.UpscalerFilter = GetSelectedFilterMode();

            _componentService.SaveConfiguration();
            Close(true);
        }

        private void BtnCancel_Click(object? sender, RoutedEventArgs e)
        {
            Close(false);
        }
    }
}
