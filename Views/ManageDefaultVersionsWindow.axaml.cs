using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using OptiscalerClient.Helpers;
using OptiscalerClient.Models;
using System.Diagnostics;
using OptiscalerClient.Services;

namespace OptiscalerClient.Views
{
    public partial class ManageDefaultVersionsWindow : Window, IGamepadInputHost
    {
        private readonly ComponentManagementService _componentService;
        private readonly IGpuDetectionService? _gpuService;
        private bool _optiDefaultShowingBeta;
        private bool _optiDefaultShowingCustom;
        private GamepadDialogNavigationHelper? _gamepadHelper;

        GamepadHelperBase? IGamepadInputHost.GamepadHelper => _gamepadHelper;

        public ManageDefaultVersionsWindow()
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
            _componentService = new ComponentManagementService();
        }

        public ManageDefaultVersionsWindow(Window owner, ComponentManagementService componentService)
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
            _componentService = componentService;

            _gpuService = PlatformServiceFactory.CreateGpuDetectionService();

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
                if (_gamepadHelper == null)
                {
                    _gamepadHelper = new GamepadDialogNavigationHelper(this, this.FindControl<ScrollViewer>("MainScrollViewer"));
                }
            };

            this.Closed += (s, e) =>
            {
                _gamepadHelper?.Dispose();
                _gamepadHelper = null;
            };

            LoadCurrentSettings();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void LoadCurrentSettings()
        {
            bool autoLatest = _componentService.Config.AutoLatestOptiScalerDefault;

            // Determine if saved OptiScaler default is beta or custom (irrelevant while auto-latest is on)
            var savedOptiDefault = _componentService.Config.DefaultOptiScalerVersion;
            var customVersions = _componentService.CustomVersions;
            bool savedIsBeta = !autoLatest && !string.IsNullOrEmpty(savedOptiDefault) &&
                               _componentService.BetaVersions.Contains(savedOptiDefault);
            bool savedIsCustom = !autoLatest && !string.IsNullOrEmpty(savedOptiDefault) &&
                                 customVersions.Contains(savedOptiDefault);
            if (savedIsCustom) savedIsBeta = false;
            _optiDefaultShowingBeta = savedIsBeta;
            _optiDefaultShowingCustom = savedIsCustom;

            // Show/hide Custom tab
            var btnCustom = this.FindControl<Button>("BtnOptiDefaultCustom");
            var gridTabs = this.FindControl<Grid>("GridOptiDefaultTabs");
            bool hasCustom = customVersions.Count > 0;
            if (btnCustom != null) btnCustom.IsVisible = hasCustom;
            if (gridTabs != null)
                gridTabs.ColumnDefinitions = hasCustom
                    ? new ColumnDefinitions("*,*,*")
                    : new ColumnDefinitions("*,*");

            UpdateOptiDefaultChannelButtons();
            PopulateDefaultOptiScalerVersionCombo(showBeta: savedIsBeta, showCustom: savedIsCustom, restoreSaved: !autoLatest);
            PopulateDefaultExtrasCombo();
            PopulateDefaultOptiPatcherCombo();
            UpdateOptiAutoLockState();
        }

        /// <summary>
        /// Locks the OptiScaler default controls (channel tabs + version combo) while
        /// AutoLatestOptiScalerDefault is on — that toggle lives in Manage Local Versions.
        /// </summary>
        private void UpdateOptiAutoLockState()
        {
            bool autoLatest = _componentService.Config.AutoLatestOptiScalerDefault;

            var cmb = this.FindControl<ComboBox>("CmbDefaultOptiScalerVersion");
            var btnStable = this.FindControl<Button>("BtnOptiDefaultStable");
            var btnBeta = this.FindControl<Button>("BtnOptiDefaultBeta");
            var btnCustom = this.FindControl<Button>("BtnOptiDefaultCustom");
            var txtNote = this.FindControl<TextBlock>("TxtOptiAutoLatestNote");

            if (cmb != null) cmb.IsEnabled = !autoLatest;
            if (btnStable != null) btnStable.IsEnabled = !autoLatest;
            if (btnBeta != null) btnBeta.IsEnabled = !autoLatest;
            if (btnCustom != null) btnCustom.IsEnabled = !autoLatest;
            if (txtNote != null) txtNote.IsVisible = autoLatest;
        }

        // ── OptiScaler Version ──────────────────────────────────────────────

        private void PopulateDefaultOptiScalerVersionCombo(bool showBeta, bool restoreSaved, bool showCustom = false)
        {
            var cmb = this.FindControl<ComboBox>("CmbDefaultOptiScalerVersion");
            if (cmb == null) return;

            cmb.Items.Clear();

            var allVersions = _componentService.OptiScalerAvailableVersions;
            var betaSet = _componentService.BetaVersions;
            var customSet = _componentService.CustomVersions;
            var latestStable = _componentService.LatestStableVersion;
            var latestBeta = _componentService.LatestBetaVersion;

            foreach (var ver in allVersions)
            {
                bool isBeta = betaSet.Contains(ver);
                bool isCustom = customSet.Contains(ver);

                if (showCustom)
                {
                    if (!isCustom) continue;
                }
                else
                {
                    if (isCustom) continue;
                    if (isBeta != showBeta) continue;
                }

                bool isLatestInChannel = !showCustom && (showBeta
                    ? ver == latestBeta
                    : ver == latestStable);

                ComboBoxItem cbi;
                if (isLatestInChannel)
                {
                    var stack = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    stack.Children.Add(new TextBlock { Text = ver, VerticalAlignment = VerticalAlignment.Center });
                    stack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(4),
                        Background = new SolidColorBrush(Color.Parse(showBeta ? "#D4A017" : "#7C3AED")),
                        Padding = new Thickness(5, 1),
                        Child = new TextBlock
                        {
                            Text = "LATEST",
                            FontSize = 10,
                            Foreground = Brushes.White,
                            FontWeight = FontWeight.Bold,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    });
                    cbi = new ComboBoxItem { Content = stack, Tag = ver };
                }
                else
                {
                    cbi = new ComboBoxItem { Content = ver, Tag = ver };
                }
                cmb.Items.Add(cbi);
            }

            if (cmb.Items.Count == 0)
            {
                cmb.Items.Add(new ComboBoxItem { Content = "No Versions Available", Tag = "auto" });
                cmb.SelectedIndex = 0;
                cmb.IsEnabled = false;
                return;
            }

            cmb.IsEnabled = true;
            cmb.SelectedIndex = 0;

            if (restoreSaved)
            {
                var saved = _componentService.Config.DefaultOptiScalerVersion;
                if (!string.IsNullOrEmpty(saved) && !saved.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    for (int i = 0; i < cmb.Items.Count; i++)
                    {
                        if ((cmb.Items[i] as ComboBoxItem)?.Tag?.ToString() == saved)
                        {
                            cmb.SelectedIndex = i;
                            break;
                        }
                    }
                }
            }
        }

        private void BtnOptiDefaultStable_Click(object? sender, RoutedEventArgs e)
        {
            if (!_optiDefaultShowingBeta && !_optiDefaultShowingCustom) return;
            _optiDefaultShowingBeta = false;
            _optiDefaultShowingCustom = false;
            UpdateOptiDefaultChannelButtons();
            PopulateDefaultOptiScalerVersionCombo(showBeta: false, restoreSaved: false);
        }

        private void BtnOptiDefaultBeta_Click(object? sender, RoutedEventArgs e)
        {
            if (_optiDefaultShowingBeta) return;
            _optiDefaultShowingBeta = true;
            _optiDefaultShowingCustom = false;
            UpdateOptiDefaultChannelButtons();
            PopulateDefaultOptiScalerVersionCombo(showBeta: true, restoreSaved: false);
        }

        private void BtnOptiDefaultCustom_Click(object? sender, RoutedEventArgs e)
        {
            if (_optiDefaultShowingCustom) return;
            _optiDefaultShowingCustom = true;
            _optiDefaultShowingBeta = false;
            UpdateOptiDefaultChannelButtons();
            PopulateDefaultOptiScalerVersionCombo(showBeta: false, showCustom: true, restoreSaved: false);
        }

        private void UpdateOptiDefaultChannelButtons()
        {
            var btnStable = this.FindControl<Button>("BtnOptiDefaultStable");
            var btnBeta = this.FindControl<Button>("BtnOptiDefaultBeta");
            var btnCustom = this.FindControl<Button>("BtnOptiDefaultCustom");
            if (btnStable == null || btnBeta == null) return;

            void SetActive(Button b) { b.Classes.Remove("BtnSecondary"); b.Classes.Add("BtnPrimary"); }
            void SetInactive(Button b) { b.Classes.Remove("BtnPrimary"); b.Classes.Add("BtnSecondary"); }

            if (_optiDefaultShowingCustom)
            {
                SetInactive(btnStable);
                SetInactive(btnBeta);
                if (btnCustom != null) SetActive(btnCustom);
            }
            else if (_optiDefaultShowingBeta)
            {
                SetInactive(btnStable);
                SetActive(btnBeta);
                if (btnCustom != null) SetInactive(btnCustom);
            }
            else
            {
                SetActive(btnStable);
                SetInactive(btnBeta);
                if (btnCustom != null) SetInactive(btnCustom);
            }
        }

        // ── FSR4 INT8 Extras ────────────────────────────────────────────────

        private void PopulateDefaultExtrasCombo()
        {
            var cmb = this.FindControl<ComboBox>("CmbDefaultExtrasVersion");
            if (cmb == null) return;

            cmb.Items.Clear();

            if (_componentService.ExtrasAvailableVersions.Count == 0)
            {
                cmb.Items.Add(new ComboBoxItem { Content = "No Versions Available", Tag = "none" });
                cmb.SelectedIndex = 0;
                cmb.IsEnabled = false;
                return;
            }

            cmb.IsEnabled = true;
            cmb.Items.Add(new ComboBoxItem { Content = "None", Tag = "none" });

            foreach (var ver in _componentService.ExtrasAvailableVersions)
            {
                bool isLatest = ver == _componentService.LatestExtrasVersion;
                ComboBoxItem cbi;
                if (isLatest)
                {
                    var stack = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    stack.Children.Add(new TextBlock { Text = ver, VerticalAlignment = VerticalAlignment.Center });
                    stack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(4),
                        Background = new SolidColorBrush(Color.Parse("#7C3AED")),
                        Padding = new Thickness(5, 1),
                        Child = new TextBlock
                        {
                            Text = "LATEST",
                            FontSize = 10,
                            Foreground = Brushes.White,
                            FontWeight = FontWeight.Bold,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    });
                    cbi = new ComboBoxItem { Content = stack, Tag = ver };
                }
                else
                {
                    cbi = new ComboBoxItem { Content = ver, Tag = ver };
                }
                cmb.Items.Add(cbi);
            }

            // Determine selection: saved preference wins; if none, use GPU-based intelligent default
            var saved = _componentService.Config.DefaultExtrasVersion;

            if (!string.IsNullOrEmpty(saved))
            {
                // Saved preference exists — restore it exactly
                if (saved.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    cmb.SelectedIndex = 0;
                }
                else
                {
                    cmb.SelectedIndex = 0; // fallback to None if not found
                    for (int i = 1; i < cmb.Items.Count; i++)
                    {
                        if ((cmb.Items[i] as ComboBoxItem)?.Tag?.ToString() == saved)
                        {
                            cmb.SelectedIndex = i;
                            break;
                        }
                    }
                }
            }
            else
            {
                // No saved preference — pick intelligently based on GPU
                bool isRdna4OrRdna3 = false;
                if (OperatingSystem.IsWindows() && _gpuService != null)
                {
                    try
                    {
                        var gpu = GpuSelectionHelper.GetPreferredGpu(_gpuService, _componentService.Config.DefaultGpuId);
                        isRdna4OrRdna3 = GpuSelectionHelper.IsRdna4(gpu) || GpuSelectionHelper.IsRdna3(gpu);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ManageDefaultVersions] GPU detection failed: {ex.Message}");
                    }
                }

                // RDNA 4 / RDNA 3 → None (INT8 shader not needed, driver provides it natively); all others → latest version
                cmb.SelectedIndex = isRdna4OrRdna3 ? 0 : (cmb.Items.Count > 1 ? 1 : 0);
            }
        }

        // ── OptiPatcher ─────────────────────────────────────────────────────

        private void PopulateDefaultOptiPatcherCombo()
        {
            var cmb = this.FindControl<ComboBox>("CmbDefaultOptiPatcherVersion");
            if (cmb == null) return;

            cmb.Items.Clear();

            cmb.Items.Add(new ComboBoxItem { Content = "None", Tag = "none" });

            var versions = _componentService.OptiPatcherAvailableVersions;
            if (versions.Count == 0)
            {
                cmb.SelectedIndex = 0;
                return;
            }

            foreach (var ver in versions)
            {
                bool isLatest = ver == _componentService.LatestOptiPatcherVersion;
                ComboBoxItem cbi;
                if (isLatest)
                {
                    var stack = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    stack.Children.Add(new TextBlock { Text = ver, VerticalAlignment = VerticalAlignment.Center });
                    stack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(4),
                        Background = new SolidColorBrush(Color.Parse("#7C3AED")),
                        Padding = new Thickness(5, 1),
                        Child = new TextBlock
                        {
                            Text = "LATEST",
                            FontSize = 10,
                            Foreground = Brushes.White,
                            FontWeight = FontWeight.Bold,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    });
                    cbi = new ComboBoxItem { Content = stack, Tag = ver };
                }
                else
                {
                    cbi = new ComboBoxItem { Content = ver, Tag = ver };
                }
                cmb.Items.Add(cbi);
            }

            // Restore saved
            var saved = _componentService.Config.DefaultOptiPatcherVersion;
            cmb.SelectedIndex = 0; // None
            if (!string.IsNullOrEmpty(saved) && !saved.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 1; i < cmb.Items.Count; i++)
                {
                    if ((cmb.Items[i] as ComboBoxItem)?.Tag?.ToString() == saved)
                    {
                        cmb.SelectedIndex = i;
                        break;
                    }
                }
            }
        }

        // ── Save / Cancel ───────────────────────────────────────────────────

        private void BtnSave_Click(object? sender, RoutedEventArgs e)
        {
            // Save OptiScaler version (skipped while auto-latest is on — the pinned value is preserved untouched)
            var cmbOpti = this.FindControl<ComboBox>("CmbDefaultOptiScalerVersion");
            if (!_componentService.Config.AutoLatestOptiScalerDefault && cmbOpti?.SelectedItem is ComboBoxItem optiItem)
            {
                var ver = optiItem.Tag?.ToString();
                _componentService.Config.DefaultOptiScalerVersion = string.IsNullOrEmpty(ver) ? null : ver;
            }

            // Save Extras version
            var cmbExtras = this.FindControl<ComboBox>("CmbDefaultExtrasVersion");
            if (cmbExtras?.SelectedItem is ComboBoxItem extrasItem)
            {
                var ver = extrasItem.Tag?.ToString() ?? "none";
                _componentService.Config.DefaultExtrasVersion =
                    ver.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : ver;
            }

            // Save OptiPatcher version
            var cmbPatcher = this.FindControl<ComboBox>("CmbDefaultOptiPatcherVersion");
            if (cmbPatcher?.SelectedItem is ComboBoxItem patcherItem)
            {
                var ver = patcherItem.Tag?.ToString() ?? "none";
                _componentService.Config.DefaultOptiPatcherVersion =
                    ver.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : ver;
            }

            _componentService.SaveConfiguration();
            Close(true);
        }

        private void BtnCancel_Click(object? sender, RoutedEventArgs e)
        {
            Close(false);
        }

        private void BtnOptiPatcherSupportLink_Click(object? sender, RoutedEventArgs e)
        {
            var url = "https://github.com/optiscaler/OptiPatcher/blob/main/GameSupport.md";
            try
            {
                var psi = new ProcessStartInfo(url) { UseShellExecute = true };
                Process.Start(psi);
            }
            catch
            {
                // ignore failures to open browser
            }
        }
    }
}
