using System;
using System.Collections.Generic;
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
        private bool _optiDefaultShowingNightly;
        private bool _optiDefaultShowingCustom;
        private Fsr4DllVariant _extrasDefaultVariant = Fsr4DllVariant.Int8;
        private bool _extrasDefaultTabInitialized;
        private bool _isUpdatingDefaultUpscalingQuality;
        private bool _defaultQualityCustomHandledForOpen;
        private GameFrameGenerationSettings? _defaultFrameGenerationSettings;
        private bool _fsr4SwapAskEveryTime = true;
        private List<string> _fsr4SwapDefaultFileKeys = new();
        private GamepadDialogNavigationHelper? _gamepadHelper;
        private readonly DlssNrOnAmdService _dlssNrService = new();
        /// <summary>Whether CmbDefaultOptiScalerVersion currently shows the "Modded" wrapper releases
        /// (Setup NR default mode = "daniel-and-opti") instead of the normal Stable/Beta/Nightly/
        /// Custom channels — see SetOptiDefaultTabsForModdedMode.</summary>
        private bool _isDlssNrOnAmdModdedActive;

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

            WindowScreenFitHelper.FitToScreen(this);

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

        private string GetResourceString(string key, string fallback)
        {
            return Application.Current?.TryFindResource(key, out var res) == true && res is string str ? str : fallback;
        }

        private void LoadCurrentSettings()
        {
            // Default Versions is now the sole owner of which OptiScaler version is pre-selected —
            // Manage Local Versions no longer has an "always use latest" toggle, so this window
            // always shows a pinned, editable selection (see BtnSave_Click).
            var savedOptiDefault = _componentService.Config.DefaultOptiScalerVersion;
            var customVersions = _componentService.CustomVersions;
            bool savedIsBeta = !string.IsNullOrEmpty(savedOptiDefault) &&
                               _componentService.BetaVersions.Contains(savedOptiDefault);
            bool savedIsNightly = !string.IsNullOrEmpty(savedOptiDefault) &&
                                   _componentService.NightlyVersions.Contains(savedOptiDefault);
            bool savedIsCustom = !string.IsNullOrEmpty(savedOptiDefault) &&
                                 customVersions.Contains(savedOptiDefault);
            if (savedIsCustom || savedIsNightly) savedIsBeta = false;
            if (savedIsCustom) savedIsNightly = false;
            _optiDefaultShowingBeta = savedIsBeta;
            _optiDefaultShowingNightly = savedIsNightly;
            _optiDefaultShowingCustom = savedIsCustom;

            // Show/hide Custom tab
            var btnCustom = this.FindControl<Button>("BtnOptiDefaultCustom");
            var gridTabs = this.FindControl<Grid>("GridOptiDefaultTabs");
            bool hasCustom = customVersions.Count > 0;
            if (btnCustom != null) btnCustom.IsVisible = hasCustom;
            if (gridTabs != null)
                gridTabs.ColumnDefinitions = hasCustom
                    ? new ColumnDefinitions("*,*,*,*")
                    : new ColumnDefinitions("*,*,*");

            UpdateOptiDefaultChannelButtons();
            PopulateDefaultOptiScalerVersionCombo(showBeta: savedIsBeta, showNightly: savedIsNightly, showCustom: savedIsCustom, restoreSaved: true);
            PopulateDefaultExtrasCombo();
            PopulateDefaultOptiPatcherCombo();
            PopulateDefaultFakenvapiCombo();
            PopulateDefaultNukemFGCombo();
            UpdateFakenvapiNukemFGLockState();
            PopulateDefaultInjectionMethodCombo();
            PopulateDefaultProfileCombo();
            PopulateDefaultUpscalingQualityCombo();
            PopulateDefaultOutputUpscalerCombo();
            PopulateDefaultDlssNrOnAmdModeCombo();

            _defaultFrameGenerationSettings = _componentService.Config.DefaultFrameGenerationSettings;
            UpdateDefaultFrameGenerationSummary();

            _fsr4SwapAskEveryTime = _componentService.Config.Fsr4SwapAskEveryTime;
            _fsr4SwapDefaultFileKeys = new List<string>(_componentService.Config.Fsr4SwapDefaultFileKeys);
            UpdateFsr4SwapOptionsSummary();
        }

        // ── OptiScaler Version ──────────────────────────────────────────────

        private void PopulateDefaultOptiScalerVersionCombo(bool showBeta, bool restoreSaved, bool showNightly = false, bool showCustom = false)
        {
            var cmb = this.FindControl<ComboBox>("CmbDefaultOptiScalerVersion");
            if (cmb == null) return;

            cmb.Items.Clear();
            cmb.Items.Add(new ComboBoxItem { Content = "Latest version available", Tag = "auto", Classes = { "SentinelOption" } });

            var allVersions = _componentService.OptiScalerAvailableVersions;
            var betaSet = _componentService.BetaVersions;
            var nightlySet = _componentService.NightlyVersions;
            var customSet = _componentService.CustomVersions;
            var latestStable = _componentService.LatestStableVersion;
            var latestBeta = _componentService.LatestBetaVersion;
            var latestNightly = _componentService.LatestNightlyVersion;

            foreach (var ver in allVersions)
            {
                bool isBeta = betaSet.Contains(ver);
                bool isNightly = nightlySet.Contains(ver);
                bool isCustom = customSet.Contains(ver);

                if (showCustom)
                {
                    if (!isCustom) continue;
                }
                else
                {
                    if (isCustom) continue;
                    if (isBeta != showBeta || isNightly != showNightly) continue;
                }

                bool isLatestInChannel = !showCustom && (showNightly
                    ? ver == latestNightly
                    : showBeta ? ver == latestBeta : ver == latestStable);

                cmb.Items.Add(ManageGameWindow.BuildVersionItem(ver, isBeta: isBeta, isLatest: isLatestInChannel));
            }

            if (cmb.Items.Count == 1)
            {
                // Only the "Latest version available" sentinel — no real versions cached for this channel.
                cmb.IsEnabled = false;
                cmb.SelectedIndex = 0;
                return;
            }

            cmb.IsEnabled = true;
            cmb.SelectedIndex = 0; // "Latest version available" — the default when nothing is pinned

            if (restoreSaved && !_componentService.Config.AutoLatestOptiScalerDefault)
            {
                var saved = _componentService.Config.DefaultOptiScalerVersion;
                if (!string.IsNullOrEmpty(saved))
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

            UpdateFakenvapiNukemFGLockState();
        }

        private void BtnOptiDefaultStable_Click(object? sender, RoutedEventArgs e)
        {
            if (!_optiDefaultShowingBeta && !_optiDefaultShowingNightly && !_optiDefaultShowingCustom) return;
            _optiDefaultShowingBeta = false;
            _optiDefaultShowingNightly = false;
            _optiDefaultShowingCustom = false;
            UpdateOptiDefaultChannelButtons();
            PopulateDefaultOptiScalerVersionCombo(showBeta: false, restoreSaved: false);
        }

        private void BtnOptiDefaultBeta_Click(object? sender, RoutedEventArgs e)
        {
            if (_optiDefaultShowingBeta) return;
            _optiDefaultShowingBeta = true;
            _optiDefaultShowingNightly = false;
            _optiDefaultShowingCustom = false;
            UpdateOptiDefaultChannelButtons();
            PopulateDefaultOptiScalerVersionCombo(showBeta: true, restoreSaved: false);
        }

        private void BtnOptiDefaultNightly_Click(object? sender, RoutedEventArgs e)
        {
            if (_optiDefaultShowingNightly) return;
            _optiDefaultShowingNightly = true;
            _optiDefaultShowingBeta = false;
            _optiDefaultShowingCustom = false;
            UpdateOptiDefaultChannelButtons();
            PopulateDefaultOptiScalerVersionCombo(showBeta: false, showNightly: true, restoreSaved: false);
        }

        private void BtnOptiDefaultCustom_Click(object? sender, RoutedEventArgs e)
        {
            if (_optiDefaultShowingCustom) return;
            _optiDefaultShowingCustom = true;
            _optiDefaultShowingBeta = false;
            _optiDefaultShowingNightly = false;
            UpdateOptiDefaultChannelButtons();
            PopulateDefaultOptiScalerVersionCombo(showBeta: false, showCustom: true, restoreSaved: false);
        }

        private void UpdateOptiDefaultChannelButtons()
        {
            var btnStable = this.FindControl<Button>("BtnOptiDefaultStable");
            var btnBeta = this.FindControl<Button>("BtnOptiDefaultBeta");
            var btnNightly = this.FindControl<Button>("BtnOptiDefaultNightly");
            var btnCustom = this.FindControl<Button>("BtnOptiDefaultCustom");
            if (btnStable == null || btnBeta == null || btnNightly == null) return;

            void SetActive(Button b) { b.Classes.Remove("BtnSecondary"); b.Classes.Add("BtnPrimary"); }
            void SetInactive(Button b) { b.Classes.Remove("BtnPrimary"); b.Classes.Add("BtnSecondary"); }

            if (_optiDefaultShowingCustom)
            {
                SetInactive(btnStable);
                SetInactive(btnBeta);
                SetInactive(btnNightly);
                if (btnCustom != null) SetActive(btnCustom);
            }
            else if (_optiDefaultShowingNightly)
            {
                SetInactive(btnStable);
                SetInactive(btnBeta);
                SetActive(btnNightly);
                if (btnCustom != null) SetInactive(btnCustom);
            }
            else if (_optiDefaultShowingBeta)
            {
                SetInactive(btnStable);
                SetActive(btnBeta);
                SetInactive(btnNightly);
                if (btnCustom != null) SetInactive(btnCustom);
            }
            else
            {
                SetActive(btnStable);
                SetInactive(btnBeta);
                SetInactive(btnNightly);
                if (btnCustom != null) SetInactive(btnCustom);
            }
        }

        private void CmbDefaultOptiScalerVersion_SelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
            => UpdateFakenvapiNukemFGLockState();

        /// <summary>
        /// Since OptiScaler 0.9 Fakenvapi/NukemFG are bundled in the package itself (same rule as
        /// <see cref="ManageGameWindow"/>'s per-game selector, <see cref="ManageGameWindow.IsVersionGreaterOrEqual"/>),
        /// so pinning a default for either one only makes sense below that version. Resolves the
        /// currently-selected OptiScaler default (or its channel's latest, while on "Latest version
        /// available") purely for this UI hint — actual install-time bundling is always recomputed
        /// against the real resolved version by ManageGameWindow/Quick Install, unaffected by this.
        /// </summary>
        private void UpdateFakenvapiNukemFGLockState()
        {
            var cmbOpti = this.FindControl<ComboBox>("CmbDefaultOptiScalerVersion");
            var cmbFakenvapi = this.FindControl<ComboBox>("CmbDefaultFakenvapiVersion");
            var cmbNukemFG = this.FindControl<ComboBox>("CmbDefaultNukemFGVersion");
            if (cmbOpti == null || cmbFakenvapi == null || cmbNukemFG == null) return;

            var tag = (cmbOpti.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            string? effectiveVersion = tag == "auto"
                ? (_optiDefaultShowingNightly ? _componentService.LatestNightlyVersion
                    : _optiDefaultShowingBeta ? _componentService.LatestBetaVersion
                    : _optiDefaultShowingCustom ? null // no reliable numeric ordering for custom builds
                    : _componentService.LatestStableVersion)
                : tag;

            bool locked = ManageGameWindow.IsVersionGreaterOrEqual(effectiveVersion, 0, 9);

            cmbFakenvapi.IsEnabled = !locked;
            cmbNukemFG.IsEnabled = !locked;
            ToolTip.SetTip(cmbFakenvapi, locked ? "Included in OptiScaler 0.9+" : null);
            ToolTip.SetTip(cmbNukemFG, locked ? "Included in OptiScaler 0.9+" : null);
        }

        // ── FSR 4 Swap/FP8 Extras ────────────────────────────────────────────

        private void PopulateDefaultExtrasCombo()
        {
            var cmb = this.FindControl<ComboBox>("CmbDefaultExtrasVersion");
            if (cmb == null) return;

            if (!_extrasDefaultTabInitialized)
            {
                var defaultVersion = _componentService.Config.DefaultExtrasVersion;
                if (!string.IsNullOrWhiteSpace(defaultVersion) &&
                    !defaultVersion.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    _extrasDefaultVariant = _componentService.GetExtrasDllVariant(defaultVersion);
                }
                _extrasDefaultTabInitialized = true;
            }

            UpdateExtrasDefaultVariantButtons();

            cmb.Items.Clear();

            var versions = _componentService.ExtrasAvailableVersions
                .Where(v => _componentService.GetExtrasDllVariant(v) == _extrasDefaultVariant)
                .ToList();

            if (versions.Count == 0)
            {
                cmb.Items.Add(new ComboBoxItem { Content = "No Versions Available", Tag = "none" });
                cmb.SelectedIndex = 0;
                cmb.IsEnabled = false;
                return;
            }

            cmb.IsEnabled = true;
            cmb.Items.Add(new ComboBoxItem { Content = "None", Tag = "none", Classes = { "SentinelOption" } });
            cmb.Items.Add(new ComboBoxItem { Content = "Latest version available", Tag = ComponentManagementService.LatestAvailableTag, Classes = { "SentinelOption" } });

            var latestInVariant = versions.FirstOrDefault();
            foreach (var ver in versions)
            {
                var isLatest = string.Equals(ver, latestInVariant, StringComparison.OrdinalIgnoreCase);
                var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                stack.Children.Add(new TextBlock { Text = _componentService.GetExtrasDllDisplayName(ver), VerticalAlignment = VerticalAlignment.Center });
                if (isLatest)
                {
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
                }
                cmb.Items.Add(new ComboBoxItem { Content = stack, Tag = ver });
            }

            // Determine selection: saved preference wins (if it matches the selected variant);
            // otherwise use the GPU-based intelligent default.
            var saved = _componentService.Config.DefaultExtrasVersion;
            int targetIndex = 0;

            if (!string.IsNullOrEmpty(saved) && !saved.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 1; i < cmb.Items.Count; i++)
                {
                    if ((cmb.Items[i] as ComboBoxItem)?.Tag?.ToString() == saved)
                    {
                        targetIndex = i;
                        break;
                    }
                }
            }
            else if (!string.IsNullOrEmpty(saved))
            {
                targetIndex = 0; // "none"
            }
            else
            {
                bool isRdna4OrRdna3 = false;
                bool isRdna2 = false;
                if (OperatingSystem.IsWindows() && _gpuService != null)
                {
                    try
                    {
                        var gpu = GpuSelectionHelper.GetPreferredGpu(_gpuService, _componentService.Config.DefaultGpuId);
                        isRdna4OrRdna3 = GpuSelectionHelper.IsRdna4(gpu) || GpuSelectionHelper.IsRdna3(gpu);
                        isRdna2 = GpuSelectionHelper.IsRdna2(gpu);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ManageDefaultVersions] GPU detection failed: {ex.Message}");
                    }
                }

                if (!isRdna4OrRdna3)
                {
                    var automaticVersion = isRdna2
                        ? _componentService.GetRdna2PreferredExtrasVersion()
                        : versions.FirstOrDefault();
                    // +2: index 0 is "None", index 1 is "Latest version available", real versions start at 2.
                    targetIndex = automaticVersion == null ? 0 : versions.IndexOf(automaticVersion) + 2;
                }
            }

            cmb.SelectedIndex = targetIndex;
        }

        private void UpdateExtrasDefaultVariantButtons()
        {
            var int8 = this.FindControl<Button>("BtnExtrasDefaultInt8");
            var fp8 = this.FindControl<Button>("BtnExtrasDefaultFp8");
            if (int8 == null || fp8 == null) return;

            void SetActive(Button b) { b.Classes.Remove("BtnSecondary"); b.Classes.Add("BtnPrimary"); }
            void SetInactive(Button b) { b.Classes.Remove("BtnPrimary"); b.Classes.Add("BtnSecondary"); }

            if (_extrasDefaultVariant == Fsr4DllVariant.Int8)
            {
                SetActive(int8);
                SetInactive(fp8);
            }
            else
            {
                SetInactive(int8);
                SetActive(fp8);
            }
        }

        private void BtnExtrasDefaultInt8_Click(object? sender, RoutedEventArgs e)
        {
            if (_extrasDefaultVariant == Fsr4DllVariant.Int8) return;
            _extrasDefaultVariant = Fsr4DllVariant.Int8;
            PopulateDefaultExtrasCombo();
        }

        private void BtnExtrasDefaultFp8_Click(object? sender, RoutedEventArgs e)
        {
            if (_extrasDefaultVariant == Fsr4DllVariant.Fp8) return;
            _extrasDefaultVariant = Fsr4DllVariant.Fp8;
            PopulateDefaultExtrasCombo();
        }

        // ── OptiPatcher ─────────────────────────────────────────────────────

        private void PopulateDefaultOptiPatcherCombo()
        {
            var cmb = this.FindControl<ComboBox>("CmbDefaultOptiPatcherVersion");
            if (cmb == null) return;

            cmb.Items.Clear();

            // "Auto" reproduces the existing per-game behavior (ManageGameWindow / Quick Install):
            // installs the latest OptiPatcher version when the compatibility list flags a game as
            // needing it. Pinning a specific version here (or "None") now overrides that instead of
            // always being ignored.
            cmb.Items.Add(new ComboBoxItem { Content = "Auto", Tag = "auto", Classes = { "SentinelOption" } });
            cmb.Items.Add(new ComboBoxItem { Content = "None", Tag = "none", Classes = { "SentinelOption" } });

            var versions = _componentService.OptiPatcherAvailableVersions;
            if (versions.Count == 0)
            {
                cmb.SelectedIndex = 0;
                return;
            }

            foreach (var ver in versions)
            {
                bool isLatest = ver == _componentService.LatestOptiPatcherVersion;
                cmb.Items.Add(ManageGameWindow.BuildVersionItem(ver, isBeta: false, isLatest: isLatest));
            }

            // Restore saved: null/empty → "Auto" (default), "none" → never install, else a pinned version.
            var saved = _componentService.Config.DefaultOptiPatcherVersion;
            cmb.SelectedIndex = 0; // Auto
            if (!string.IsNullOrEmpty(saved) && !saved.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                cmb.SelectedIndex = 1; // None, unless a pinned version is found below
                for (int i = 2; i < cmb.Items.Count; i++)
                {
                    if ((cmb.Items[i] as ComboBoxItem)?.Tag?.ToString() == saved)
                    {
                        cmb.SelectedIndex = i;
                        break;
                    }
                }
            }
        }

        // ── Fakenvapi ───────────────────────────────────────────────────────

        private void PopulateDefaultFakenvapiCombo()
        {
            var cmb = this.FindControl<ComboBox>("CmbDefaultFakenvapiVersion");
            if (cmb == null) return;

            cmb.Items.Clear();
            cmb.Items.Add(new ComboBoxItem { Content = "None", Tag = "none", Classes = { "SentinelOption" } });
            cmb.Items.Add(new ComboBoxItem { Content = "Latest version available", Tag = ComponentManagementService.LatestAvailableTag, Classes = { "SentinelOption" } });

            foreach (var ver in _componentService.FakenvapiAvailableVersions)
            {
                var isLatest = ver == _componentService.LatestFakenvapiVersion;
                cmb.Items.Add(ManageGameWindow.BuildVersionItem(ver, isBeta: false, isLatest: isLatest));
            }

            var saved = _componentService.Config.DefaultFakenvapiVersion;
            cmb.SelectedIndex = 0;
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

        // ── NukemFG ─────────────────────────────────────────────────────────

        private void PopulateDefaultNukemFGCombo()
        {
            var cmb = this.FindControl<ComboBox>("CmbDefaultNukemFGVersion");
            if (cmb == null) return;

            cmb.Items.Clear();
            cmb.Items.Add(new ComboBoxItem { Content = "None", Tag = "none", Classes = { "SentinelOption" } });
            cmb.Items.Add(new ComboBoxItem { Content = "Latest version available", Tag = ComponentManagementService.LatestAvailableTag, Classes = { "SentinelOption" } });

            foreach (var ver in _componentService.GetDownloadedNukemFGVersions())
            {
                cmb.Items.Add(new ComboBoxItem { Content = ver, Tag = ver });
            }

            var saved = _componentService.Config.DefaultNukemFGVersion;
            cmb.SelectedIndex = 0;
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

        // ── Injection Method ────────────────────────────────────────────────

        private void PopulateDefaultInjectionMethodCombo()
        {
            var cmb = this.FindControl<ComboBox>("CmbDefaultInjectionMethod");
            if (cmb == null) return;

            var saved = _componentService.Config.DefaultInjectionMethod;
            cmb.SelectedIndex = 0; // Auto
            if (!string.IsNullOrEmpty(saved))
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

        // ── AMD DLSS Neural Rendering mod default ────────────────────────────

        /// <summary>Same permissive-when-unknown check as ManageGameWindow.IsSetupNrGpuAllowed —
        /// only a GPU we're sure isn't AMD hides this section, so the default can never end up
        /// configured for hardware that can't use it.</summary>
        private bool IsAmdDefaultGpu()
        {
            if (_gpuService == null) return true;
            var gpu = GpuSelectionHelper.GetPreferredGpu(_gpuService, _componentService.Config.DefaultGpuId);
            return gpu == null || gpu.Vendor == GpuVendor.AMD;
        }

        /// <summary>Gates the whole "Experimental" zone exactly like ManageGameWindow's
        /// PopulateVersionSelectors does for its own copy of the same Border/Chip pair — hidden
        /// entirely unless ShowExperimentalFeatures is on, further locked to AMD hardware since that's
        /// the only GPU vendor the mod itself supports.</summary>
        private void PopulateDefaultDlssNrOnAmdModeCombo()
        {
            var showExperimental = _componentService.Config.ShowExperimentalFeatures && IsAmdDefaultGpu();
            var zone = this.FindControl<Control>("BorderExperimentalZone");
            if (zone != null) zone.IsVisible = showExperimental;
            var chip = this.FindControl<Control>("BorderExperimentalChip");
            if (chip != null) chip.IsVisible = showExperimental;

            var cmb = this.FindControl<ComboBox>("CmbDefaultDlssNrOnAmdMode");
            if (cmb == null) return;

            cmb.SelectionChanged -= CmbDefaultDlssNrOnAmdMode_SelectionChanged;
            var saved = _componentService.Config.DefaultDlssNrOnAmdMode;
            cmb.SelectedIndex = 0; // "none"
            for (int i = 0; i < cmb.Items.Count; i++)
            {
                if ((cmb.Items[i] as ComboBoxItem)?.Tag?.ToString() == saved)
                {
                    cmb.SelectedIndex = i;
                    break;
                }
            }
            cmb.SelectionChanged += CmbDefaultDlssNrOnAmdMode_SelectionChanged;

            ApplyDlssNrOnAmdModeSelection((cmb.SelectedItem as ComboBoxItem)?.Tag as string ?? "none");
        }

        private void CmbDefaultDlssNrOnAmdMode_SelectionChanged(object? sender, SelectionChangedEventArgs e)
            => ApplyDlssNrOnAmdModeSelection((sender as ComboBox)?.SelectedItem is ComboBoxItem item ? item.Tag as string : null);

        /// <summary>Every other default-versions control besides the Daniel version combo itself —
        /// mirrors ManageGameWindow.OptiScalerOptionPanelNames/SetOptiScalerControlsLocked exactly:
        /// "daniel-only" means the mod runs standalone, with no OptiScaler involved at all, so none of
        /// these settings apply. Grid/Button parents cover their child tab buttons for free (Avalonia
        /// disables input on children of a disabled control).</summary>
        private static readonly string[] DefaultOptionsControlNames =
        {
            "GridOptiDefaultTabs", "CmbDefaultOptiScalerVersion", "GridExtrasDefaultTabs", "CmbDefaultExtrasVersion",
            "CmbDefaultOptiPatcherVersion", "CmbDefaultFakenvapiVersion", "CmbDefaultNukemFGVersion",
            "CmbDefaultInjectionMethod", "CmbDefaultProfile", "BtnDefaultFrameGeneration",
            "CmbDefaultUpscalingQuality", "CmbDefaultOutputUpscaler", "BtnFsr4SwapOptions",
        };

        private void SetDefaultOptionsLocked(bool locked)
        {
            var enabled = !locked;
            foreach (var name in DefaultOptionsControlNames)
            {
                var control = this.FindControl<Control>(name);
                if (control != null) control.IsEnabled = enabled;
            }
        }

        /// <summary>Mirrors CmbSetupNr_SelectionChanged's own consequences in ManageGameWindow: the
        /// Daniel version combo enables/populates, "daniel-only" locks every other default (the mod
        /// runs standalone, with no OptiScaler involved), and "daniel-and-opti" switches
        /// CmbDefaultOptiScalerVersion over to the Modded wrapper releases (see
        /// SetOptiDefaultTabsForModdedMode) exactly like CmbOptiVersion does there.</summary>
        private void ApplyDlssNrOnAmdModeSelection(string? tag)
        {
            var mode = tag ?? "none";
            SetDefaultOptionsLocked(mode == "daniel-only");

            var cmbDaniel = this.FindControl<ComboBox>("CmbDefaultDlssNrDanielVersion");
            if (mode == "none")
            {
                if (cmbDaniel != null) { cmbDaniel.IsEnabled = false; cmbDaniel.Items.Clear(); }
                SetOptiDefaultTabsForModdedMode(false);
                return;
            }

            _ = PopulateDefaultDlssNrDanielVersionComboAsync();

            if (mode == "daniel-and-opti")
            {
                SetOptiDefaultTabsForModdedMode(true);
                _ = PopulateDefaultModdedOptiVersionComboAsync();
            }
            else
            {
                SetOptiDefaultTabsForModdedMode(false);
            }
        }

        /// <summary>Populates CmbDefaultDlssNrDanielVersion from danielblnc's own releases — same
        /// source and "pick a specific one, defaulting to latest" shape as ManageGameWindow's
        /// PopulateDlssNrDanielVersionComboAsync, just persisted instead of applied to a live install.</summary>
        private async Task PopulateDefaultDlssNrDanielVersionComboAsync()
        {
            var cmb = this.FindControl<ComboBox>("CmbDefaultDlssNrDanielVersion");
            if (cmb == null) return;

            cmb.Items.Clear();
            cmb.IsEnabled = false;

            List<DlssNrOnAmdRelease> releases;
            try
            {
                releases = await _dlssNrService.GetReleasesAsync();
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[DefaultVersions] Could not list danielblnc releases: {ex.Message}");
                releases = new List<DlssNrOnAmdRelease>();
            }

            if (releases.Count == 0)
            {
                cmb.Items.Add(new ComboBoxItem { Content = GetResourceString("TxtNoOptiDetected", "No version detected"), IsEnabled = false });
                cmb.SelectedIndex = 0;
                return;
            }

            for (int i = 0; i < releases.Count; i++)
                cmb.Items.Add(ManageGameWindow.BuildVersionItem(releases[i].Version, isBeta: false, isLatest: i == 0));

            var saved = _componentService.Config.DefaultDlssNrOnAmdDanielVersion;
            var targetIndex = string.IsNullOrEmpty(saved) ? -1 : releases.FindIndex(r => string.Equals(r.Version, saved, StringComparison.OrdinalIgnoreCase));
            cmb.SelectedIndex = targetIndex >= 0 ? targetIndex : 0;
            cmb.IsEnabled = true;
        }

        /// <summary>Mirrors ManageGameWindow's SetOptiTabsForModdedMode — hides Stable/Beta/Nightly/
        /// Custom and shows the plain "Modded" indicator instead, since that's the only "channel"
        /// available while Setup NR default mode = "daniel-and-opti".</summary>
        private void SetOptiDefaultTabsForModdedMode(bool modded)
        {
            if (_isDlssNrOnAmdModdedActive == modded) return;
            _isDlssNrOnAmdModdedActive = modded;

            var btnStable = this.FindControl<Button>("BtnOptiDefaultStable");
            var btnBeta = this.FindControl<Button>("BtnOptiDefaultBeta");
            var btnNightly = this.FindControl<Button>("BtnOptiDefaultNightly");
            var btnCustom = this.FindControl<Button>("BtnOptiDefaultCustom");
            var btnModded = this.FindControl<Button>("BtnOptiDefaultModded");
            if (btnStable != null) btnStable.IsVisible = !modded;
            if (btnBeta != null) btnBeta.IsVisible = !modded;
            if (btnNightly != null) btnNightly.IsVisible = !modded;
            if (btnCustom != null) btnCustom.IsVisible = !modded && _componentService.CustomVersions.Count > 0;
            if (btnModded != null) btnModded.IsVisible = modded;

            // Leaving Modded — restore whatever normal channel/version was showing before, same as
            // reopening this window fresh would.
            if (!modded)
                PopulateDefaultOptiScalerVersionCombo(showBeta: _optiDefaultShowingBeta, showNightly: _optiDefaultShowingNightly, showCustom: _optiDefaultShowingCustom, restoreSaved: true);
        }

        /// <summary>Populates CmbDefaultOptiScalerVersion with MatheusGViana/dlss-5-amd-project's
        /// releases while Setup NR default mode = "daniel-and-opti" — same source and "pick one,
        /// defaulting to latest" shape as ManageGameWindow's PopulateModdedVersionComboAsync. Tags are
        /// the raw release version (e.g. "1.7.3"), not a registered custom-version name: Quick/Bulk
        /// Install resolve and download the actual wrapper build at install time (see
        /// DlssNrOnAmdService.InstallForQuickPathAsync) — this combo only pins which release to use.</summary>
        private async Task PopulateDefaultModdedOptiVersionComboAsync()
        {
            var cmb = this.FindControl<ComboBox>("CmbDefaultOptiScalerVersion");
            if (cmb == null || !_isDlssNrOnAmdModdedActive) return;

            cmb.SelectionChanged -= CmbDefaultOptiScalerVersion_SelectionChanged;
            cmb.Items.Clear();
            cmb.IsEnabled = false;

            List<DlssNrOnAmdRelease> releases;
            try
            {
                releases = await _componentService.GetAmdWrapperReleasesAsync();
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[DefaultVersions] Could not list MatheusGViana wrapper releases: {ex.Message}");
                releases = new List<DlssNrOnAmdRelease>();
            }

            if (releases.Count == 0)
            {
                cmb.Items.Add(new ComboBoxItem { Content = GetResourceString("TxtNoOptiDetected", "No version detected"), IsEnabled = false });
                cmb.SelectedIndex = 0;
            }
            else
            {
                for (int i = 0; i < releases.Count; i++)
                    cmb.Items.Add(ManageGameWindow.BuildVersionItem(releases[i].Version, isBeta: false, isLatest: i == 0));

                var saved = _componentService.Config.DefaultDlssNrOnAmdWrapperVersion;
                var targetIndex = string.IsNullOrEmpty(saved) ? -1 : releases.FindIndex(r => string.Equals(r.Version, saved, StringComparison.OrdinalIgnoreCase));
                cmb.SelectedIndex = targetIndex >= 0 ? targetIndex : 0;
                cmb.IsEnabled = true;
            }

            cmb.SelectionChanged += CmbDefaultOptiScalerVersion_SelectionChanged;
        }

        // ── Profile ─────────────────────────────────────────────────────────

        private void PopulateDefaultProfileCombo()
        {
            var cmb = this.FindControl<ComboBox>("CmbDefaultProfile");
            if (cmb == null) return;

            cmb.Items.Clear();

            var profileService = new ProfileManagementService();
            var profiles = profileService.GetAllProfiles()
                .OrderByDescending(p => p.Name == OptiScalerProfile.BuiltInDefaultName)
                .ToList();
            foreach (var profile in profiles)
            {
                var item = new ComboBoxItem { Content = profile.Name, Tag = profile.Name };
                ToolTip.SetTip(item, profile.Description);
                cmb.Items.Add(item);
            }

            var saved = _componentService.Config.DefaultProfileName;
            var targetIndex = profiles.FindIndex(p => p.Name == saved);
            cmb.SelectedIndex = targetIndex >= 0 ? targetIndex : 0;
        }

        // ── Upscaling Quality ───────────────────────────────────────────────

        private void PopulateDefaultUpscalingQualityCombo()
        {
            var cmb = this.FindControl<ComboBox>("CmbDefaultUpscalingQuality");
            if (cmb == null) return;

            _isUpdatingDefaultUpscalingQuality = true;
            try
            {
                cmb.Items.Clear();
                ManageGameWindow.AddUpscalingQualityItem(cmb, GetResourceString("TxtQualityGameControlled", "Game controlled"), UpscalingQualityPreset.GameControlled, isSentinel: true);
                ManageGameWindow.AddUpscalingQualityItem(cmb, "Native AA", UpscalingQualityPreset.NativeAa);
                ManageGameWindow.AddUpscalingQualityItem(cmb, "Ultra Quality", UpscalingQualityPreset.UltraQuality);
                ManageGameWindow.AddUpscalingQualityItem(cmb, "Quality", UpscalingQualityPreset.Quality);
                ManageGameWindow.AddUpscalingQualityItem(cmb, "Balanced", UpscalingQualityPreset.Balanced);
                ManageGameWindow.AddUpscalingQualityItem(cmb, "Performance", UpscalingQualityPreset.Performance);
                ManageGameWindow.AddUpscalingQualityItem(cmb, "Ultra Performance", UpscalingQualityPreset.UltraPerformance);
                var fontIcons = this.FindResource("FontIcons") as FontFamily;
                cmb.Items.Add(ComboActionItemHelper.Build(this, GetResourceString("TxtCustom", "Custom"),
                    UpscalingQualityPreset.Custom, glyph: "", glyphFontFamily: fontIcons));

                var selected = _componentService.Config.DefaultUpscalingQualityPreset ?? UpscalingQualityPreset.GameControlled;
                cmb.SelectedIndex = 0;
                for (var index = 0; index < cmb.Items.Count; index++)
                {
                    if (cmb.Items[index] is ComboBoxItem item && item.Tag is UpscalingQualityPreset preset && preset == selected)
                    {
                        cmb.SelectedIndex = index;
                        break;
                    }
                }
            }
            finally
            {
                _isUpdatingDefaultUpscalingQuality = false;
            }
        }

        private void SelectDefaultUpscalingQualityPreset(UpscalingQualityPreset selected)
        {
            var cmb = this.FindControl<ComboBox>("CmbDefaultUpscalingQuality");
            if (cmb == null) return;

            _isUpdatingDefaultUpscalingQuality = true;
            try
            {
                for (var index = 0; index < cmb.Items.Count; index++)
                {
                    if (cmb.Items[index] is ComboBoxItem item && item.Tag is UpscalingQualityPreset preset && preset == selected)
                    {
                        cmb.SelectedIndex = index;
                        return;
                    }
                }
            }
            finally
            {
                _isUpdatingDefaultUpscalingQuality = false;
            }
        }

        private async void CmbDefaultUpscalingQuality_SelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
        {
            if (_isUpdatingDefaultUpscalingQuality || sender is not ComboBox combo
                || combo.SelectedItem is not ComboBoxItem item
                || item.Tag is not UpscalingQualityPreset selected)
                return;

            if (selected == UpscalingQualityPreset.Custom)
            {
                _defaultQualityCustomHandledForOpen = true;
                await HandleCustomUpscalingQualitySelectionAsync();
            }
        }

        private void CmbDefaultUpscalingQuality_DropDownOpened(object? sender, EventArgs e)
            => _defaultQualityCustomHandledForOpen = false;

        private async void CmbDefaultUpscalingQuality_DropDownClosed(object? sender, EventArgs e)
        {
            if (_isUpdatingDefaultUpscalingQuality || _defaultQualityCustomHandledForOpen
                || sender is not ComboBox combo
                || combo.SelectedItem is not ComboBoxItem item
                || item.Tag is not UpscalingQualityPreset.Custom)
                return;

            _defaultQualityCustomHandledForOpen = true;
            await HandleCustomUpscalingQualitySelectionAsync();
        }

        private async System.Threading.Tasks.Task HandleCustomUpscalingQualitySelectionAsync()
        {
            var previousPreset = _componentService.Config.DefaultUpscalingQualityPreset ?? UpscalingQualityPreset.GameControlled;
            var customRatio = _componentService.Config.DefaultUpscalingCustomRatio ?? 0.67;

            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            var outputResolution = screen == null
                ? new PixelSize(2560, 1440)
                : new PixelSize(screen.Bounds.Width, screen.Bounds.Height);
            var dialog = new UpscalingQualityCustomWindow(this, customRatio, outputResolution);
            var result = await dialog.ShowDialog<double?>(this);
            if (result == null)
            {
                SelectDefaultUpscalingQualityPreset(previousPreset);
                return;
            }

            _componentService.Config.DefaultUpscalingCustomRatio = result.Value;
        }

        // ── Output Upscaler ─────────────────────────────────────────────────

        private void PopulateDefaultOutputUpscalerCombo()
        {
            var cmb = this.FindControl<ComboBox>("CmbDefaultOutputUpscaler");
            if (cmb == null) return;

            cmb.Items.Clear();
            ManageGameWindow.AddOutputUpscalerItem(cmb, GetResourceString("TxtOutputUpscalerDefault", "Default"), OutputUpscalerBackend.Default, isSentinel: true);
            ManageGameWindow.AddOutputUpscalerItem(cmb, "FSR 2", OutputUpscalerBackend.Fsr2);
            ManageGameWindow.AddOutputUpscalerItem(cmb, "FSR 3", OutputUpscalerBackend.Fsr3);
            ManageGameWindow.AddOutputUpscalerItem(cmb, "FSR 4", OutputUpscalerBackend.Fsr4);
            ManageGameWindow.AddOutputUpscalerItem(cmb, "XeSS", OutputUpscalerBackend.XeSS);
            ManageGameWindow.AddOutputUpscalerItem(cmb, "DLSS", OutputUpscalerBackend.Dlss);

            var selected = _componentService.Config.DefaultOutputUpscalerBackend ?? OutputUpscalerBackend.Default;
            cmb.SelectedIndex = 0;
            for (var index = 0; index < cmb.Items.Count; index++)
            {
                if (cmb.Items[index] is ComboBoxItem item && item.Tag is OutputUpscalerBackend backend && backend == selected)
                {
                    cmb.SelectedIndex = index;
                    break;
                }
            }
        }

        // ── Frame Generation ────────────────────────────────────────────────

        /// <summary>
        /// Builds a capabilities set with everything enabled, so the reused
        /// <see cref="FrameGenerationSettingsWindow"/> lets the user pick any route/output/multiplier
        /// as a raw default template — there is no specific game here to detect engine/DLSS/FSR
        /// support from, so nothing is gated.
        /// </summary>
        private static FrameGenerationCapabilities BuildPermissiveFrameGenerationCapabilities() => new()
        {
            IsDirectX12 = true,
            IsVulkan = true,
            HasNativeDlssG = true,
            HasNativeFsr3 = true,
            HasStreamline = true,
            HasXeFgDependencies = true,
            HasFsrFgDependencies = true,
            HasNukem = true,
            SupportsDynamicMfg = true,
            // Reserved6 is an unused placeholder kept only to hold Auto's ordinal stable in
            // already-persisted data — never meant to be user-selectable.
            AvailableRoutes = Enum.GetValues<FrameGenerationRoute>()
                .Where(r => r != FrameGenerationRoute.Reserved6)
                .ToArray(),
            AvailableOutputs = Enum.GetValues<FrameGenerationOutput>(),
            AvailableMfgModes = Enum.GetValues<MultiFrameGenerationMode>()
        };

        private async void BtnDefaultFrameGeneration_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var capabilities = BuildPermissiveFrameGenerationCapabilities();
                var dialog = new FrameGenerationSettingsWindow(this, capabilities, _defaultFrameGenerationSettings);
                var settings = await dialog.ShowDialog<GameFrameGenerationSettings?>(this);
                if (settings == null) return;

                _defaultFrameGenerationSettings = settings;
                UpdateDefaultFrameGenerationSummary();
            }
            catch (Exception ex) { DebugWindow.Log($"[ManageDefaultVersions] Frame generation dialog failed: {ex.Message}"); }
        }

        private void UpdateDefaultFrameGenerationSummary()
        {
            var selection = this.FindControl<TextBlock>("TxtDefaultFrameGenerationSelection");
            if (selection == null) return;

            var settings = _defaultFrameGenerationSettings;
            if (settings == null || settings.Route == FrameGenerationRoute.Disabled)
            {
                selection.Text = GetResourceString("TxtFgRouteDisabled", "Disabled");
                return;
            }

            var output = settings.Output switch
            {
                FrameGenerationOutput.Auto => "Auto",
                FrameGenerationOutput.FsrFg => "FSR FG",
                FrameGenerationOutput.XeFg => "Intel XeFG",
                FrameGenerationOutput.Nukem => "Nukem FSR3 FG",
                FrameGenerationOutput.DlssG => "DLSS-G",
                FrameGenerationOutput.DlssGWithNvngx => "DLSS-G + NvNGX",
                _ => settings.Output.ToString()
            };
            var multiplier = settings.MultiFrameMode == MultiFrameGenerationMode.Auto
                ? "Auto"
                : settings.MultiFrameMode.ToString().Replace("X", "x");
            selection.Text = $"{output} {multiplier}";
        }

        // ── FSR 4 Swap Options ──────────────────────────────────────────────

        private async void BtnFsr4SwapOptions_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Fsr4SwapOptionsWindow(this, _fsr4SwapAskEveryTime, _fsr4SwapDefaultFileKeys);
                var saved = await dialog.ShowDialog<bool>(this);
                if (!saved) return;

                _fsr4SwapAskEveryTime = dialog.AskEveryTime;
                _fsr4SwapDefaultFileKeys = dialog.SelectedFileKeys;
                UpdateFsr4SwapOptionsSummary();
            }
            catch (Exception ex) { DebugWindow.Log($"[ManageDefaultVersions] FSR 4 Swap Options dialog failed: {ex.Message}"); }
        }

        private void UpdateFsr4SwapOptionsSummary()
        {
            var selection = this.FindControl<TextBlock>("TxtFsr4SwapOptionsSelection");
            if (selection == null) return;

            selection.Text = _fsr4SwapAskEveryTime
                ? GetResourceString("TxtFsr4SwapAskEveryTime", "Ask me every time")
                : string.Format(GetResourceString("TxtFsr4SwapDefaultsCountFormat", "{0} of {1} files selected"),
                    _fsr4SwapDefaultFileKeys.Count > 0 ? _fsr4SwapDefaultFileKeys.Count : Fsr4Int8DllHelper.LogicalFileKeys.Length,
                    Fsr4Int8DllHelper.LogicalFileKeys.Length);
        }

        // ── Save / Cancel ───────────────────────────────────────────────────

        private void BtnSave_Click(object? sender, RoutedEventArgs e)
        {
            // Save OptiScaler version. This window is now the sole owner of the pinned default —
            // Manage Local Versions no longer has an "always use latest" toggle to defer to.
            // While Setup NR default mode = "daniel-and-opti", CmbDefaultOptiScalerVersion shows
            // Modded wrapper releases instead (see SetOptiDefaultTabsForModdedMode) — that selection
            // is a different setting (DefaultDlssNrOnAmdWrapperVersion, saved further below) and must
            // never overwrite the user's actual non-modded OptiScaler default.
            var cmbOpti = this.FindControl<ComboBox>("CmbDefaultOptiScalerVersion");
            if (!_isDlssNrOnAmdModdedActive && cmbOpti?.SelectedItem is ComboBoxItem optiItem)
            {
                var ver = optiItem.Tag?.ToString();
                if (ver == "auto")
                {
                    _componentService.Config.AutoLatestOptiScalerDefault = true;
                    // Remember which tab "Latest version available" was picked from — see
                    // EffectiveDefaultOptiScalerVersion, which resolves auto through this channel
                    // instead of always assuming Stable.
                    _componentService.Config.DefaultOptiScalerChannel =
                        _optiDefaultShowingNightly ? "nightly" : _optiDefaultShowingBeta ? "beta" : "stable";
                }
                else
                {
                    _componentService.Config.AutoLatestOptiScalerDefault = false;
                    _componentService.Config.DefaultOptiScalerVersion = string.IsNullOrEmpty(ver) ? null : ver;
                }
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

            // Save Fakenvapi version
            var cmbFakenvapi = this.FindControl<ComboBox>("CmbDefaultFakenvapiVersion");
            if (cmbFakenvapi?.SelectedItem is ComboBoxItem fakenvapiItem)
            {
                var ver = fakenvapiItem.Tag?.ToString() ?? "none";
                _componentService.Config.DefaultFakenvapiVersion =
                    ver.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : ver;
            }

            // Save NukemFG version
            var cmbNukemFG = this.FindControl<ComboBox>("CmbDefaultNukemFGVersion");
            if (cmbNukemFG?.SelectedItem is ComboBoxItem nukemFGItem)
            {
                var ver = nukemFGItem.Tag?.ToString() ?? "none";
                _componentService.Config.DefaultNukemFGVersion =
                    ver.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : ver;
            }

            // Save Injection Method
            var cmbInjection = this.FindControl<ComboBox>("CmbDefaultInjectionMethod");
            if (cmbInjection?.SelectedItem is ComboBoxItem injectionItem)
            {
                var method = injectionItem.Tag?.ToString();
                _componentService.Config.DefaultInjectionMethod =
                    string.IsNullOrEmpty(method) || method.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : method;
            }

            // Save AMD DLSS Neural Rendering mod default (only ever offered when experimental
            // features are on and AMD is the configured default GPU — see
            // PopulateDefaultDlssNrOnAmdModeCombo's BorderExperimentalZone/Chip gate).
            var cmbDlssNr = this.FindControl<ComboBox>("CmbDefaultDlssNrOnAmdMode");
            if (cmbDlssNr?.SelectedItem is ComboBoxItem dlssNrItem)
            {
                _componentService.Config.DefaultDlssNrOnAmdMode = dlssNrItem.Tag?.ToString() ?? "none";
            }

            var cmbDlssNrDaniel = this.FindControl<ComboBox>("CmbDefaultDlssNrDanielVersion");
            if (cmbDlssNrDaniel?.SelectedItem is ComboBoxItem danielItem && danielItem.Tag is string danielVer)
            {
                _componentService.Config.DefaultDlssNrOnAmdDanielVersion = danielVer;
            }

            // CmbDefaultOptiScalerVersion doubles as the Modded-wrapper-version picker while
            // _isDlssNrOnAmdModdedActive — its Tag here is the raw wrapper release version (e.g.
            // "1.7.3"), not a registered custom-version name (see PopulateDefaultModdedOptiVersionComboAsync).
            if (_isDlssNrOnAmdModdedActive && cmbOpti?.SelectedItem is ComboBoxItem wrapperItem && wrapperItem.Tag is string wrapperVer)
            {
                _componentService.Config.DefaultDlssNrOnAmdWrapperVersion = wrapperVer;
            }

            // Save Profile
            var cmbProfile = this.FindControl<ComboBox>("CmbDefaultProfile");
            if (cmbProfile?.SelectedItem is ComboBoxItem profileItem && profileItem.Tag is string profileName)
            {
                _componentService.Config.DefaultProfileName = profileName;
            }

            // Save Upscaling Quality
            var cmbQuality = this.FindControl<ComboBox>("CmbDefaultUpscalingQuality");
            if (cmbQuality?.SelectedItem is ComboBoxItem qualityItem && qualityItem.Tag is UpscalingQualityPreset preset)
            {
                _componentService.Config.DefaultUpscalingQualityPreset = preset == UpscalingQualityPreset.GameControlled ? null : preset;
            }

            // Save Output Upscaler
            var cmbOutputUpscaler = this.FindControl<ComboBox>("CmbDefaultOutputUpscaler");
            if (cmbOutputUpscaler?.SelectedItem is ComboBoxItem outputItem && outputItem.Tag is OutputUpscalerBackend backend)
            {
                _componentService.Config.DefaultOutputUpscalerBackend = backend == OutputUpscalerBackend.Default ? null : backend;
            }

            // Save Frame Generation
            _componentService.Config.DefaultFrameGenerationSettings =
                _defaultFrameGenerationSettings?.Route == FrameGenerationRoute.Disabled ? null : _defaultFrameGenerationSettings;

            // Save FSR 4 Swap Options
            _componentService.Config.Fsr4SwapAskEveryTime = _fsr4SwapAskEveryTime;
            _componentService.Config.Fsr4SwapDefaultFileKeys = _fsr4SwapDefaultFileKeys;

            _componentService.SaveConfiguration();
            Close(true);
        }

        private void BtnCancel_Click(object? sender, RoutedEventArgs e)
        {
            Close(false);
        }

        private void BtnViewCompatibilityList_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var psi = new ProcessStartInfo(CompatibilityListService.WikiUrl) { UseShellExecute = true };
                Process.Start(psi);
            }
            catch
            {
                // ignore failures to open browser
            }
        }
    }
}
