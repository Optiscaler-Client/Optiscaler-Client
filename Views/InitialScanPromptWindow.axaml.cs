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
    public sealed class InitialScanOptions
    {
        public ScanSourcesConfig ScanSources { get; }
        public List<string> DriveRoots { get; }
        public bool RefreshCoversOnly { get; }
        public UpscalerFilterMode UpscalerFilter { get; }

        public InitialScanOptions(ScanSourcesConfig scanSources, List<string> driveRoots, bool refreshCoversOnly = false, UpscalerFilterMode upscalerFilter = UpscalerFilterMode.ShowAll)
        {
            ScanSources = scanSources;
            DriveRoots = driveRoots;
            RefreshCoversOnly = refreshCoversOnly;
            UpscalerFilter = upscalerFilter;
        }
    }

    public partial class InitialScanPromptWindow : Window
    {
        private readonly ComponentManagementService _componentService;
        private readonly List<DriveToggle> _driveToggles = new();
        private readonly List<string> _customFolders = new();

        public InitialScanPromptWindow()
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
            _componentService = new ComponentManagementService();
        }

        public InitialScanPromptWindow(Window owner, ComponentManagementService componentService, bool isFirstTime = true)
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

            ApplyTitleText(isFirstTime);
            LoadCurrentSettings();
            PopulateDrives();
            LoadCustomFolders();
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

            SetFilterMode(_componentService.Config.ScanSources.UpscalerFilter);
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

        private void LoadCustomFolders()
        {
            _customFolders.Clear();
            _customFolders.AddRange(_componentService.Config.ScanSources.CustomFolders);
            RefreshCustomFoldersList();
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
                    pnlCustomFolders.Children.Add(txtNoCustomFolders);
                return;
            }

            foreach (var folder in _customFolders)
                pnlCustomFolders.Children.Add(CreateFolderCard(folder));
        }

        private Border CreateFolderCard(string folderPath)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*, Auto") };

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
                    var selectedPath = folders[0].Path.IsAbsoluteUri
                        ? folders[0].Path.LocalPath
                        : folders[0].TryGetLocalPath();

                    if (string.IsNullOrEmpty(selectedPath) || !Directory.Exists(selectedPath))
                        return;

                    if (!_customFolders.Contains(selectedPath))
                    {
                        _customFolders.Add(selectedPath);
                        RefreshCustomFoldersList();
                    }
                }
            }
            catch (Exception ex) { DebugWindow.Log($"[ScanPrompt] Add folder failed: {ex.Message}"); }
        }

        private void BtnRemoveFolder_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string folderPath)
            {
                _customFolders.Remove(folderPath);
                RefreshCustomFoldersList();
            }
        }

        private void ApplyTitleText(bool isFirstTime)
        {
            var title = this.FindControl<TextBlock>("TxtTitle");
            var desc = this.FindControl<TextBlock>("TxtDesc");

            var titleKey = isFirstTime ? "TxtInitialScanTitle" : "TxtScanSetupTitle";
            var descKey = isFirstTime ? "TxtInitialScanDesc" : "TxtScanSetupDesc";

            title?.SetValue(TextBlock.TextProperty, GetResourceText(titleKey, title?.Text ?? ""));
            desc?.SetValue(TextBlock.TextProperty, GetResourceText(descKey, desc?.Text ?? ""));
        }

        private static string GetResourceText(string key, string fallback)
        {
            return Application.Current?.FindResource(key) as string ?? fallback;
        }

        private void PopulateDrives()
        {
            var pnlDriveList = this.FindControl<StackPanel>("PnlDriveList");
            var txtNoDrives = this.FindControl<TextBlock>("TxtNoDrives");
            if (pnlDriveList == null) return;

            pnlDriveList.Children.Clear();
            _driveToggles.Clear();

            var drivePaths = OperatingSystem.IsLinux()
                ? GetLinuxDrives()
                : DriveInfo.GetDrives()
                    .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed ||
                                              d.DriveType == DriveType.Removable ||
                                              d.DriveType == DriveType.Network))
                    .Select(d => d.Name)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (drivePaths.Count == 0)
            {
                if (txtNoDrives != null)
                    txtNoDrives.IsVisible = true;
                return;
            }

            if (txtNoDrives != null)
                txtNoDrives.IsVisible = false;

            foreach (var drivePath in drivePaths)
            {
                var toggle = new ToggleSwitch
                {
                    IsChecked = true,
                    OnContent = "",
                    OffContent = ""
                };

                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto")
                };

                var text = new TextBlock
                {
                    Text = drivePath,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = Application.Current?.FindResource("BrTextPrimary") as IBrush ?? Brushes.White
                };

                row.Children.Add(text);
                Grid.SetColumn(text, 0);

                row.Children.Add(toggle);
                Grid.SetColumn(toggle, 1);

                pnlDriveList.Children.Add(row);
                _driveToggles.Add(new DriveToggle(drivePath, toggle));
            }
        }

        /// <summary>
        /// Returns a deduplicated 3-bucket list of relevant mount points on Linux:
        /// root (/), the user's home partition (if on a separate mount), and
        /// removable/external drives under /run/media, /media, or /mnt.
        /// </summary>
        private static List<string> GetLinuxDrives()
        {
            var result = new List<string>();

            // Bucket 1: root is always present
            result.Add("/");

            // Bucket 2: home partition (only if separate from /)
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                var realHome = System.IO.Path.GetFullPath(home);
                var homeMount = FindMountPoint(realHome);
                if (!string.IsNullOrEmpty(homeMount) && homeMount != "/")
                    result.Add(homeMount);
            }

            // Bucket 3: external / removable drives
            var externalPrefixes = new[] { "/run/media", "/media", "/mnt" };
            try
            {
                foreach (var line in File.ReadAllLines("/proc/mounts"))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    var mp = UnescapeMountPoint(parts[1]);
                    if (externalPrefixes.Any(p => mp.StartsWith(p, StringComparison.Ordinal)))
                    {
                        if (!result.Contains(mp))
                            result.Add(mp);
                    }
                }
            }
            catch { /* /proc/mounts unavailable — skip externals */ }

            return result;
        }

        private static string FindMountPoint(string path)
        {
            try
            {
                var current = path;
                while (!string.IsNullOrEmpty(current))
                {
                    if (DriveInfo.GetDrives().Any(d => d.Name == current + "/" || d.Name == current))
                        return current;
                    var parent = System.IO.Path.GetDirectoryName(current);
                    if (parent == current) break;
                    current = parent ?? string.Empty;
                }
            }
            catch { /* ignore */ }
            return "/";
        }

        private static string UnescapeMountPoint(string raw) =>
            raw.Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");

        private static string SafeGetVolumeLabel(DriveInfo drive)
        {
            try
            {
                return drive.VolumeLabel ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private void TglRefreshCoversOnly_IsCheckedChanged(object? sender, RoutedEventArgs e)
        {
            var isCoversOnly = (sender as ToggleSwitch)?.IsChecked ?? false;
            var pnl = this.FindControl<StackPanel>("PnlScanOptions");
            if (pnl != null)
                pnl.IsEnabled = !isCoversOnly;
            var pnlFilter = this.FindControl<StackPanel>("PnlUpscalerFilter");
            if (pnlFilter != null)
                pnlFilter.IsEnabled = !isCoversOnly;
        }

        private void BtnStartScan_Click(object? sender, RoutedEventArgs e)
        {
            var tglSteam = this.FindControl<ToggleSwitch>("TglSteam");
            var tglHeroic = this.FindControl<ToggleSwitch>("TglHeroic");
            var tglEpic = this.FindControl<ToggleSwitch>("TglEpic");
            var tglGOG = this.FindControl<ToggleSwitch>("TglGOG");
            var tglXbox = this.FindControl<ToggleSwitch>("TglXbox");
            var tglEA = this.FindControl<ToggleSwitch>("TglEA");
            var tglUbisoft = this.FindControl<ToggleSwitch>("TglUbisoft");

            var sources = new ScanSourcesConfig
            {
                ScanSteam = tglSteam?.IsChecked ?? true,
                ScanHeroic = tglHeroic?.IsChecked ?? true,
                ScanEpic = tglEpic?.IsChecked ?? true,
                ScanGOG = tglGOG?.IsChecked ?? true,
                ScanXbox = tglXbox?.IsChecked ?? true,
                ScanEA = tglEA?.IsChecked ?? true,
                ScanUbisoft = tglUbisoft?.IsChecked ?? true,
                CustomFolders = _customFolders.ToList()
            };

            // Sync custom folders back to config so ManageScanSourcesWindow sees them
            _componentService.Config.ScanSources.CustomFolders = _customFolders.ToList();
            _componentService.SaveConfiguration();

            var selectedDrives = _driveToggles
                .Where(d => d.Toggle.IsChecked ?? false)
                .Select(d => d.Root)
                .ToList();

            var refreshCoversOnly = this.FindControl<ToggleSwitch>("TglRefreshCoversOnly")?.IsChecked ?? false;
            var filterMode = GetSelectedFilterMode();

            Close(new InitialScanOptions(sources, selectedDrives, refreshCoversOnly, filterMode));
        }

        private void BtnClose_Click(object? sender, RoutedEventArgs e)
        {
            Close(null);
        }

        private sealed class DriveToggle
        {
            public string Root { get; }
            public ToggleSwitch Toggle { get; }

            public DriveToggle(string root, ToggleSwitch toggle)
            {
                Root = root;
                Toggle = toggle;
            }
        }
    }
}
