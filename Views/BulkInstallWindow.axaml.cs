using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using OptiscalerClient.Models;
using OptiscalerClient.Services;
using OptiscalerClient.Helpers;
using System.Text.RegularExpressions;

namespace OptiscalerClient.Views;

public partial class BulkInstallWindow : Window, IGamepadInputHost
{
    private readonly ComponentManagementService _componentService;
    private readonly GameInstallationService _installService;
    private readonly IGpuDetectionService? _gpuService;
    private readonly ObservableCollection<BulkGameItem> _gameItems;
    private readonly ObservableCollection<BulkGameItem> _filteredGameItems;
    private List<BulkGameItem> _allGames = new List<BulkGameItem>();
    private bool _isInstalling = false;
    private readonly ProfileManagementService _profileService;
    private Window? _ownerWindow;
    private string? _lastSelectedProfileName;
    private bool _isUpdatingProfiles = false;
    private const string NewProfileTag = "__new_profile__";
    private bool _optiShowingBeta;
    private bool _optiShowingNightly;
    private bool _optiShowingCustom;
    private bool _optiTabInitialized;
    private Fsr4DllVariant _extrasVariant = Fsr4DllVariant.Int8;
    private bool _extrasTabInitialized;
    private GameFrameGenerationSettings _frameGenerationSettings = new()
    {
        Route = FrameGenerationRoute.Disabled,
        Output = FrameGenerationOutput.Auto,
        MultiFrameMode = MultiFrameGenerationMode.Auto
    };
    private GameOutputUpscalerSettings _outputUpscalerSettings = new() { Backend = OutputUpscalerBackend.Default };
    private bool _isUpdatingOutputUpscaler;
    private BulkGamepadNavigationHelper? _gamepadHelper;

    GamepadHelperBase? IGamepadInputHost.GamepadHelper => _gamepadHelper;

    public BulkInstallWindow()
    {
        InitializeComponent();
        DialogDimHelper.Register(this);

        // Initialize fields to avoid nullable warnings
        _componentService = null!;
        _profileService = null!;
        _installService = null!;
        _gpuService = null!;
        _gameItems = new ObservableCollection<BulkGameItem>();
        _filteredGameItems = new ObservableCollection<BulkGameItem>();
    }

    public BulkInstallWindow(
        ComponentManagementService componentService,
        GameInstallationService installService,
        List<Game> games,
        Window? owner = null)
    {
        InitializeComponent();
        DialogDimHelper.Register(this);

        _componentService = componentService;
        _installService = installService;
        _profileService = new ProfileManagementService();
        _ownerWindow = owner;
        _gameItems = new ObservableCollection<BulkGameItem>();
        _filteredGameItems = new ObservableCollection<BulkGameItem>();

        // Initialize GPU service
        _gpuService = PlatformServiceFactory.CreateGpuDetectionService();

        // Populate games list
        foreach (var game in games.OrderBy(g => g.Name))
        {
            var gameItem = new BulkGameItem
            {
                Game = game,
                Name = game.Name,
                Platform = game.Platform.ToString(),
                CoverPath = game.CoverImageUrl,
                IsInstalled = game.IsOptiscalerInstalled,
                CanInstall = !game.IsOptiscalerInstalled,
                IsSelected = false, // Start with all items unchecked
                OptiscalerVersion = game.OptiscalerVersion,
                IsOptiscalerInstalled = game.IsOptiscalerInstalled
            };

            _gameItems.Add(gameItem);
            _allGames.Add(gameItem);
            _filteredGameItems.Add(gameItem);
        }

        var gamesList = this.FindControl<ItemsControl>("GamesList");
        if (gamesList != null)
        {
            gamesList.ItemsSource = _filteredGameItems;
        }

        // Load versions
        _ = LoadVersionsAsync();

        // Update selection count
        UpdateSelectionCount();

        // Subscribe to selection changes
        foreach (var item in _gameItems)
        {
            item.PropertyChanged += GameItem_PropertyChanged;
        }

        // Setup version selection handler
        var cmbOptiVersion = this.FindControl<ComboBox>("CmbOptiVersion");
        if (cmbOptiVersion != null)
        {
            cmbOptiVersion.SelectionChanged += CmbOptiVersion_SelectionChanged;
        }



        // Initialize injection method selector
        var cmbInjectionMethod = this.FindControl<ComboBox>("CmbInjectionMethod");
        if (cmbInjectionMethod != null)
        {
            cmbInjectionMethod.SelectedIndex = 0; // Default to dxgi.dll
        }

        // Populate FSR4 INT8 versions
        PopulateExtrasComboBox();

        // Populate OptiPatcher versions
        PopulateOptiPatcherComboBox();

        // Populate Fakenvapi versions
        PopulateFakenvapiComboBox();

        // Populate NukemFG versions
        PopulateNukemFGComboBox();

        // Populate profile selector
        PopulateProfileSelector();

        // Populate output upscaler selector
        PopulateOutputUpscalerSelector();

        UpdateFrameGenerationSummary();

        // Fade in animation
        var rootPanel = this.FindControl<Panel>("RootPanel");
        if (rootPanel != null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                rootPanel.Transitions = new Avalonia.Animation.Transitions
                {
                    new Avalonia.Animation.DoubleTransition
                    {
                        Property = Panel.OpacityProperty,
                        Duration = TimeSpan.FromMilliseconds(200)
                    }
                };
                rootPanel.Opacity = 1;
            }, DispatcherPriority.Render);
        }

        this.Opened += (s, e) =>
        {
            if (_gamepadHelper == null)
            {
                _gamepadHelper = new BulkGamepadNavigationHelper(this, this.FindControl<ScrollViewer>("GamesScrollViewer"));
                _gamepadHelper.GamepadModeActiveChanged += OnGamepadModeActiveChanged;

                if (Owner is IGamepadInputHost seedHost)
                    _gamepadHelper.SeedGamepadModeActive(seedHost.IsGamepadModeActive);
            }
        };

        this.Closed += (s, e) =>
        {
            if (_gamepadHelper != null)
                _gamepadHelper.GamepadModeActiveChanged -= OnGamepadModeActiveChanged;
            _gamepadHelper?.Dispose();
            _gamepadHelper = null;
        };
    }

    private void OnGamepadModeActiveChanged(object? sender, bool isGamepadModeActive)
    {
        var txtX = this.FindControl<TextBlock>("TxtCloseIconX");
        var badgeB = this.FindControl<Border>("BadgeCloseGamepadB");
        if (txtX != null) txtX.IsVisible = !isGamepadModeActive;
        if (badgeB != null) badgeB.IsVisible = isGamepadModeActive;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async Task LoadVersionsAsync()
    {
        // Always ask the service to refresh. It observes the normal cooldown, except when a
        // newly introduced channel (such as Nightly) is missing from an older local cache.
        try { await _componentService.CheckForUpdatesAsync(); }
        catch (GitHubRateLimitException) { /* rate limited — populate from cache */ }
        catch (Exception) { /* network error — populate from cache */ }

        Dispatcher.UIThread.Post(() =>
        {
            var customVersions = _componentService.CustomVersions;

            // Show/hide Custom tab
            var btnCustom = this.FindControl<Button>("BtnOptiCustom");
            var gridTabs = this.FindControl<Grid>("GridOptiTabs");
            bool hasCustom = customVersions.Count > 0;
            if (btnCustom != null) btnCustom.IsVisible = hasCustom;
            if (gridTabs != null)
                gridTabs.ColumnDefinitions = hasCustom
                    ? new ColumnDefinitions("*,*,*,*")
                    : new ColumnDefinitions("*,*,*");

            // Determine initial tab on first load
            if (!_optiTabInitialized)
            {
                var configDefault = _componentService.EffectiveDefaultOptiScalerVersion;
                _optiShowingBeta = !string.IsNullOrEmpty(configDefault) &&
                                   _componentService.BetaVersions.Contains(configDefault);
                _optiShowingNightly = !string.IsNullOrEmpty(configDefault) &&
                                      _componentService.NightlyVersions.Contains(configDefault);
                _optiShowingCustom = !string.IsNullOrEmpty(configDefault) &&
                                     customVersions.Contains(configDefault);
                if (_optiShowingCustom || _optiShowingNightly) _optiShowingBeta = false;
                if (_optiShowingCustom) _optiShowingNightly = false;
                _optiTabInitialized = true;
            }

            UpdateOptiChannelButtons();
            PopulateOptiVersionCombo();
            PopulateExtrasComboBox();
            PopulateOptiPatcherComboBox();
            PopulateFakenvapiComboBox();
            PopulateNukemFGComboBox();
        });
    }

    private void PopulateOptiVersionCombo()
    {
        var allVersions = _componentService.OptiScalerAvailableVersions;
        var betaVersions = _componentService.BetaVersions;
        var nightlyVersions = _componentService.NightlyVersions;
        var customVersions = _componentService.CustomVersions;
        var latestStable = _componentService.LatestStableVersion;
        var latestBeta = _componentService.LatestBetaVersion;
        var latestNightly = _componentService.LatestNightlyVersion;
        string? latestInChannel = _optiShowingCustom ? null : _optiShowingNightly ? latestNightly : (_optiShowingBeta ? latestBeta : latestStable);
        string latestBadgeColor = _optiShowingNightly ? "#0EA5E9" : _optiShowingBeta ? "#D4A017" : "#7C3AED";

        var cmb = this.FindControl<ComboBox>("CmbOptiVersion");
        if (cmb == null) return;

        cmb.SelectionChanged -= CmbOptiVersion_SelectionChanged;
        cmb.Items.Clear();

        // "None" always comes first — lets the user do a batch DLL-only swap (see
        // RunBulkDllSwapAsync) without installing OptiScaler at all. Never auto-selected by the
        // logic below; only reached if the user picks it manually.
        cmb.Items.Add(new ComboBoxItem { Content = "None", Tag = "none", Classes = { "SentinelOption" } });

        if (allVersions.Count == 0 && !_optiShowingCustom)
        {
            cmb.Items.Add(new ComboBoxItem { Content = "No versions available", IsEnabled = false });
            cmb.SelectedIndex = 0;
            cmb.IsEnabled = true;
            cmb.SelectionChanged += CmbOptiVersion_SelectionChanged;
            UpdateSelectionCount();
            return;
        }

        System.Collections.Generic.List<string> versionsToShow;
        if (_optiShowingCustom)
            versionsToShow = allVersions.Where(v => customVersions.Contains(v)).ToList();
        else
            versionsToShow = allVersions.Where(v => !customVersions.Contains(v) &&
                nightlyVersions.Contains(v) == _optiShowingNightly &&
                betaVersions.Contains(v) == _optiShowingBeta).ToList();

        if (versionsToShow.Count == 0)
        {
            cmb.Items.Add(new ComboBoxItem { Content = "No versions available", IsEnabled = false });
            cmb.SelectedIndex = 0;
            cmb.IsEnabled = true;
            cmb.SelectionChanged += CmbOptiVersion_SelectionChanged;
            UpdateSelectionCount();
            return;
        }

        cmb.IsEnabled = true;

        foreach (var ver in versionsToShow)
        {
            bool isLatest = string.Equals(ver, latestInChannel, StringComparison.OrdinalIgnoreCase);
            ComboBoxItem cbi;
            if (isLatest)
            {
                var stack = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
                stack.Children.Add(new TextBlock { Text = ver, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                stack.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Color.Parse(latestBadgeColor)),
                    Padding = new Thickness(5, 1),
                    Child = new TextBlock { Text = "LATEST", FontSize = 10, Foreground = Brushes.White, FontWeight = FontWeight.Bold }
                });
                cbi = new ComboBoxItem { Content = stack, Tag = ver };
            }
            else
            {
                cbi = new ComboBoxItem { Content = ver, Tag = ver };
            }
            cmb.Items.Add(cbi);
        }

        // Index 0 is always "None" and is never picked here — it only gets selected by explicit
        // user action, per the DLL-swap feature's requirement that it never becomes a silent default.
        int selectedIndex = 1;
        var configDefault = _componentService.EffectiveDefaultOptiScalerVersion;
        bool defaultInChannel = !string.IsNullOrEmpty(configDefault) &&
            (_optiShowingCustom
                ? customVersions.Contains(configDefault)
                : !customVersions.Contains(configDefault) &&
                  nightlyVersions.Contains(configDefault) == _optiShowingNightly &&
                  betaVersions.Contains(configDefault) == _optiShowingBeta);
        if (defaultInChannel)
        {
            for (int i = 1; i < cmb.Items.Count; i++)
            {
                if (cmb.Items[i] is ComboBoxItem ci &&
                    string.Equals(ci.Tag?.ToString(), configDefault, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = i;
                    break;
                }
            }
        }

        cmb.SelectedIndex = selectedIndex;
        cmb.SelectionChanged += CmbOptiVersion_SelectionChanged;
        // Rebuilding detaches the event handler, so keep the button-state in sync with
        // the newly selected first version when switching Stable/Beta/Nightly.
        UpdateSelectionCount();
    }

    private void UpdateOptiChannelButtons()
    {
        var btnStable = this.FindControl<Button>("BtnOptiStable");
        var btnBeta = this.FindControl<Button>("BtnOptiBeta");
        var btnNightly = this.FindControl<Button>("BtnOptiNightly");
        var btnCustom = this.FindControl<Button>("BtnOptiCustom");
        if (btnStable == null || btnBeta == null || btnNightly == null) return;

        void SetActive(Button b) { b.Classes.Remove("BtnSecondary"); b.Classes.Add("BtnPrimary"); }
        void SetInactive(Button b) { b.Classes.Remove("BtnPrimary"); b.Classes.Add("BtnSecondary"); }

        if (_optiShowingCustom)
        {
            SetInactive(btnStable);
            SetInactive(btnBeta);
            SetInactive(btnNightly);
            if (btnCustom != null) SetActive(btnCustom);
        }
        else if (_optiShowingNightly)
        {
            SetInactive(btnStable);
            SetInactive(btnBeta);
            SetActive(btnNightly);
            if (btnCustom != null) SetInactive(btnCustom);
        }
        else if (_optiShowingBeta)
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

    private void BtnOptiStable_Click(object? sender, RoutedEventArgs e)
    {
        if (!_optiShowingBeta && !_optiShowingNightly && !_optiShowingCustom) return;
        _optiShowingBeta = false;
        _optiShowingNightly = false;
        _optiShowingCustom = false;
        UpdateOptiChannelButtons();
        PopulateOptiVersionCombo();
    }

    private void BtnOptiBeta_Click(object? sender, RoutedEventArgs e)
    {
        if (_optiShowingBeta) return;
        _optiShowingBeta = true;
        _optiShowingNightly = false;
        _optiShowingCustom = false;
        UpdateOptiChannelButtons();
        PopulateOptiVersionCombo();
    }

    private void BtnOptiCustom_Click(object? sender, RoutedEventArgs e)
    {
        if (_optiShowingCustom) return;
        _optiShowingCustom = true;
        _optiShowingBeta = false;
        _optiShowingNightly = false;
        UpdateOptiChannelButtons();
        PopulateOptiVersionCombo();
    }

    private void BtnOptiNightly_Click(object? sender, RoutedEventArgs e)
    {
        if (_optiShowingNightly) return;
        _optiShowingNightly = true;
        _optiShowingBeta = false;
        _optiShowingCustom = false;
        UpdateOptiChannelButtons();
        PopulateOptiVersionCombo();
    }

    private static ComboBoxItem BuildVersionItem(string ver, bool isBeta, bool isLatest)
    {
        var stack = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
        stack.Children.Add(new TextBlock { Text = ver, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });

        if (isBeta)
        {
            var badge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.Parse("#D4A017")),
                Padding = new Thickness(5, 1),
                Child = new TextBlock { Text = "BETA", FontSize = 10, Foreground = Brushes.White, FontWeight = FontWeight.Bold }
            };
            stack.Children.Add(badge);
        }

        if (isLatest)
        {
            var badge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.Parse("#7C3AED")),
                Padding = new Thickness(5, 1),
                Child = new TextBlock { Text = "LATEST", FontSize = 10, Foreground = Brushes.White, FontWeight = FontWeight.Bold }
            };
            stack.Children.Add(badge);
        }

        return new ComboBoxItem { Content = stack, Tag = ver };
    }

    private void GameItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BulkGameItem.IsSelected))
        {
            UpdateSelectionCount();
            UpdateSelectAllCheckbox();
        }
    }

    private void UpdateSelectionCount()
    {
        var selectedCount = _gameItems.Count(g => g.IsSelected && g.CanInstall);
        var txtCount = this.FindControl<TextBlock>("TxtSelectionCount");
        var btnInstall = this.FindControl<Button>("BtnInstall");

        if (txtCount != null)
        {
            txtCount.Text = selectedCount == 1
                ? "1 game selected"
                : $"{selectedCount} games selected";
        }

        if (btnInstall != null)
        {
            // DLL-swap mode: OptiScaler version is "None". Mirrors ManageGameWindow's
            // UpdateInstallButtonsForSwapState — Opti=None & Extras=none has nothing to do
            // (greyed out), Opti=None & Extras=version relabels to a swap action.
            var cmbOptiVersion = this.FindControl<ComboBox>("CmbOptiVersion");
            var cmbExtrasVersion = this.FindControl<ComboBox>("CmbExtrasVersion");
            var optiTag = (cmbOptiVersion?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var extrasTag = (cmbExtrasVersion?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            bool optiIsNone = string.Equals(optiTag, "none", StringComparison.OrdinalIgnoreCase);
            bool extrasIsNone = string.IsNullOrEmpty(extrasTag) || string.Equals(extrasTag, "none", StringComparison.OrdinalIgnoreCase);
            bool swapMode = optiIsNone && !extrasIsNone;
            bool blockedMode = optiIsNone && extrasIsNone;

            if (swapMode)
            {
                btnInstall.Content = selectedCount == 0
                    ? "Swap DLL"
                    : selectedCount == 1
                        ? "Swap DLL on 1 game"
                        : $"Swap DLL on {selectedCount} games";
            }
            else
            {
                btnInstall.Content = selectedCount == 0
                    ? "Install Selected"
                    : selectedCount == 1
                        ? "Install 1 game"
                        : $"Install {selectedCount} games";
            }
            btnInstall.IsEnabled = selectedCount > 0 && !_isInstalling && !blockedMode;
        }
    }

    private void UpdateSelectAllCheckbox()
    {
        var chkSelectAll = this.FindControl<CheckBox>("ChkSelectAll");
        if (chkSelectAll == null) return;

        var selectableGames = _gameItems.Where(g => g.CanInstall).ToList();
        if (selectableGames.Count == 0)
        {
            chkSelectAll.IsChecked = false;
            return;
        }

        var selectedCount = selectableGames.Count(g => g.IsSelected);

        if (selectedCount == 0)
            chkSelectAll.IsChecked = false;
        else if (selectedCount == selectableGames.Count)
            chkSelectAll.IsChecked = true;
        else
            chkSelectAll.IsChecked = null; // Indeterminate state
    }

    private void ChkSelectAll_Click(object? sender, RoutedEventArgs e)
    {
        var chkSelectAll = sender as CheckBox;
        if (chkSelectAll == null) return;

        bool shouldSelect = chkSelectAll.IsChecked == true;

        foreach (var item in _gameItems.Where(g => g.CanInstall))
        {
            item.IsSelected = shouldSelect;
        }
    }

    private async void BtnInstall_Click(object? sender, RoutedEventArgs e)
    {
        var selectedGames = _gameItems.Where(g => g.IsSelected && g.CanInstall).ToList();
        if (selectedGames.Count == 0) return;

        var cmbOptiVersion = this.FindControl<ComboBox>("CmbOptiVersion");
        var cmbInjectionMethod = this.FindControl<ComboBox>("CmbInjectionMethod");
        var cmbExtrasVersion = this.FindControl<ComboBox>("CmbExtrasVersion");
        var cmbOptiPatcher = this.FindControl<ComboBox>("CmbOptiPatcherVersion");
        var cmbFakenvapiVersion = this.FindControl<ComboBox>("CmbFakenvapiVersion");
        var cmbNukemFGVersion = this.FindControl<ComboBox>("CmbNukemFGVersion");
        var cmbProfile = this.FindControl<ComboBox>("CmbProfile");

        if (cmbOptiVersion?.SelectedItem is not ComboBoxItem selectedItem) return;

        string version = selectedItem.Tag?.ToString() ?? "";

        // Fakenvapi: read version from combobox
        var selectedFakenvapiItem = cmbFakenvapiVersion?.SelectedItem as ComboBoxItem;
        var selectedFakenvapiVersion = selectedFakenvapiItem?.Tag?.ToString();
        bool installFakenvapi = !string.IsNullOrEmpty(selectedFakenvapiVersion) &&
                                !selectedFakenvapiVersion.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                                selectedFakenvapiVersion != "__manage__";

        // NukemFG: read version from combobox
        var selectedNukemFGItem = cmbNukemFGVersion?.SelectedItem as ComboBoxItem;
        var selectedNukemFGVersion = selectedNukemFGItem?.Tag?.ToString();
        bool installNukemFG = !string.IsNullOrEmpty(selectedNukemFGVersion) &&
                              !selectedNukemFGVersion.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                              selectedNukemFGVersion != "__manage__";

        // Get injection method
        var injectionItem = cmbInjectionMethod?.SelectedItem as ComboBoxItem;
        string injectionMethod = injectionItem?.Tag?.ToString() ?? "dxgi.dll";

        // Get selected Extras (FSR4 INT8) version
        var selectedExtrasItem = cmbExtrasVersion?.SelectedItem as ComboBoxItem;
        var selectedExtrasVersion = selectedExtrasItem?.Tag?.ToString();
        bool injectExtras = !string.IsNullOrEmpty(selectedExtrasVersion) &&
                            !selectedExtrasVersion.Equals("none", StringComparison.OrdinalIgnoreCase);
        bool selectedExtrasIsInt8 = injectExtras && _componentService.GetExtrasDllVariant(selectedExtrasVersion!) == Fsr4DllVariant.Int8;

        // ── DLL-swap mode: OptiScaler version is "None" ─────────────────────────────
        // Ignores the normal batch install flow entirely (profile, injection method, Fakenvapi,
        // NukemFG, OptiPatcher — none of that applies to a bare DLL swap). Mirrors ManageGameWindow's
        // ExecuteDllSwapAsync, just looped per selected game with auto-detection only (no per-game
        // manual file picker in a batch context).
        if (string.Equals(version, "none", StringComparison.OrdinalIgnoreCase))
        {
            if (!injectExtras)
            {
                await new ConfirmDialog(this, "Nothing to install",
                    "Select an OptiScaler version or an FSR4 INT8 version before installing.").ShowDialog<object>(this);
                return;
            }

            await RunBulkDllSwapAsync(selectedGames, selectedExtrasVersion!);
            return;
        }

        // Get selected OptiPatcher version
        var selectedOptiPatcherItem = cmbOptiPatcher?.SelectedItem as ComboBoxItem;
        var selectedOptiPatcherVersion = selectedOptiPatcherItem?.Tag?.ToString();
        bool installOptiPatcher = !string.IsNullOrEmpty(selectedOptiPatcherVersion) &&
                                  !selectedOptiPatcherVersion.Equals("none", StringComparison.OrdinalIgnoreCase);

        // Get selected profile
        OptiScalerProfile? selectedProfile = null;
        if (cmbProfile?.SelectedItem is ComboBoxItem profileItem && profileItem.Tag is OptiScalerProfile prof)
            selectedProfile = prof;

        _isInstalling = true;

        var btnInstall = this.FindControl<Button>("BtnInstall");
        var btnCancel = this.FindControl<Button>("BtnCancel");
        var progressSection = this.FindControl<Border>("ProgressSection");
        var txtProgressStatus = this.FindControl<TextBlock>("TxtProgressStatus");
        var txtProgressCount = this.FindControl<TextBlock>("TxtProgressCount");
        var progressBar = this.FindControl<ProgressBar>("ProgressBar");

        if (btnInstall != null) btnInstall.IsEnabled = false;
        if (btnCancel != null) btnCancel.IsEnabled = false;
        if (progressSection != null) progressSection.IsVisible = true;

        int totalGames = selectedGames.Count;
        int currentGame = 0;

        // Same machine for the whole batch, so resolve this once rather than per game.
        var preferredGpuForFsr4 = GpuSelectionHelper.GetPreferredGpu(_gpuService, _componentService.Config.DefaultGpuId);
        var isRdna4ForFsr4 = GpuSelectionHelper.IsRdna4(preferredGpuForFsr4);
        var isRdna2ForFsr4 = GpuSelectionHelper.IsRdna2(preferredGpuForFsr4);
        // Streamline is no longer tied to "is this a Nightly version" — it's tied to whether
        // the shared FG configuration actually needs it for at least one selected game (either
        // FGInput=dlssg or FGOutput=dlssg). isNightlyChannel is kept separately only for the
        // Fakenvapi-bundling quirk of Nightly packages. Per-game need is re-checked inside the
        // loop below, since capabilities (and therefore Auto resolution) can differ per game.
        var isNightlyChannel = _componentService.IsNightlyOptiScalerVersion(version);
        var fgConfigService = new FrameGenerationConfigurationService();
        var installStreamline = selectedGames.Any(item =>
            fgConfigService.RequiresStreamline(_frameGenerationSettings, fgConfigService.DetectCapabilities(item.Game, preferredGpuForFsr4)));
        var mfgWithEnabler = _frameGenerationSettings.Output == FrameGenerationOutput.DlssG &&
            _frameGenerationSettings.NvngxReplacement is FrameGenerationNvngxReplacement.Arturs or FrameGenerationNvngxReplacement.Combo;
        var streamlineCacheDir = string.Empty;
        var dlssEnablerCacheDir = string.Empty;
        string? nightlyFakenvapiCacheDir = null;

        if (installStreamline)
        {
            try
            {
                if (progressBar != null) progressBar.IsIndeterminate = true;
                streamlineCacheDir = await _componentService.DownloadLatestStreamlineAsync();
            }
            catch (Exception ex)
            {
                _isInstalling = false;
                if (progressBar != null) progressBar.IsIndeterminate = false;
                if (progressSection != null) progressSection.IsVisible = false;
                if (btnInstall != null) btnInstall.IsEnabled = true;
                if (btnCancel != null) btnCancel.IsEnabled = true;
                await new ConfirmDialog(this, "Error", ex.Message, isAlert: true).ShowDialog<bool>(this);
                return;
            }
        }

        if (mfgWithEnabler)
        {
            if (string.IsNullOrEmpty(_frameGenerationSettings.DlssEnablerVersion))
            {
                _isInstalling = false;
                if (progressSection != null) progressSection.IsVisible = false;
                if (btnInstall != null) btnInstall.IsEnabled = true;
                if (btnCancel != null) btnCancel.IsEnabled = true;
                await new ConfirmDialog(this, "Error",
                    "No DLSS Enabler version selected. Configure Frame Generation before installing.", isAlert: true).ShowDialog<bool>(this);
                return;
            }
            dlssEnablerCacheDir = _componentService.GetDlssEnablerCachePath(_frameGenerationSettings.DlssEnablerVersion);
        }

        foreach (var gameItem in selectedGames)
        {
            currentGame++;

            if (txtProgressStatus != null)
                txtProgressStatus.Text = $"Installing {gameItem.Name}...";

            if (txtProgressCount != null)
                txtProgressCount.Text = $"{currentGame} / {totalGames}";

            if (progressBar != null)
                progressBar.Value = (currentGame - 1) * 100.0 / totalGames;

            try
            {
                // Apply the common bulk FG configuration as a per-game copy. The installer
                // writes this as the final, narrow INI override after the selected profile.
                gameItem.Game.FrameGenerationSettings = CloneFrameGenerationSettings(_frameGenerationSettings);

                // Apply the shared output-upscaler selection as a per-game copy, same pattern as FG.
                gameItem.Game.OutputUpscalerSettings = new GameOutputUpscalerSettings { Backend = _outputUpscalerSettings.Backend };

                // Get cache paths
                var optiCacheDir = _componentService.GetOptiScalerCachePath(version);
                var installFakenvapiForGame = installFakenvapi;
                var fakeCacheDir = installFakenvapiForGame
                    ? _componentService.GetFakenvapiCachePath(selectedFakenvapiVersion!)
                    : "";
                var nukemCacheDir = installNukemFG
                    ? _componentService.GetNukemFGCachePath(selectedNukemFGVersion!)
                    : "";

                if (isNightlyChannel)
                {
                    var gameDir = _installService.DetermineInstallDirectory(gameItem.Game);
                    var fakenvapiMissing = string.IsNullOrWhiteSpace(gameDir) ||
                        !File.Exists(Path.Combine(gameDir, "fakenvapi.dll"));
                    if (fakenvapiMissing)
                    {
                        if (string.IsNullOrEmpty(nightlyFakenvapiCacheDir))
                        {
                            if (progressBar != null) progressBar.IsIndeterminate = true;
                            nightlyFakenvapiCacheDir = await _componentService.DownloadLatestFakenvapiAsync();
                        }
                        installFakenvapiForGame = true;
                        fakeCacheDir = nightlyFakenvapiCacheDir;
                    }
                    else
                    {
                        installFakenvapiForGame = false;
                        fakeCacheDir = string.Empty;
                    }
                }

                // Re-checked per game: capabilities (and therefore Auto resolution) can differ
                // per game even though the FG configuration itself is shared across the batch.
                var installStreamlineForGame = fgConfigService.RequiresStreamline(
                    gameItem.Game.FrameGenerationSettings!, fgConfigService.DetectCapabilities(gameItem.Game, preferredGpuForFsr4));

                string? resolvedGameDir = null;
                await Task.Run(() =>
                {
                    resolvedGameDir = _installService.InstallOptiScaler(
                        gameItem.Game,
                        optiCacheDir,
                        injectionMethod, // Use selected injection method
                        installFakenvapiForGame,
                        fakeCacheDir,
                        installNukemFG,
                        nukemCacheDir,
                        optiscalerVersion: version,
                        profile: selectedProfile,
                        isRdna4: isRdna4ForFsr4, isRdna2: isRdna2ForFsr4,
                        installStreamline: installStreamlineForGame,
                        streamlineCachePath: streamlineCacheDir,
                        ensureFakenvapiIfMissing: isNightlyChannel,
                        installDlssEnabler: mfgWithEnabler,
                        dlssEnablerCachePath: dlssEnablerCacheDir
                    );
                });

                // ── FSR4 INT8 DLL injection ────────────────────────────────────────
                if (injectExtras && !string.IsNullOrEmpty(selectedExtrasVersion))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (txtProgressStatus != null) txtProgressStatus.Text = $"Downloading FSR4 INT8 v{selectedExtrasVersion} for {gameItem.Name}...";
                        if (progressBar != null) progressBar.IsIndeterminate = false;
                    });

                    string extrasDllPath;
                    try
                    {
                        var extrasProgress = new Progress<double>(p =>
                            Dispatcher.UIThread.Post(() => { if (progressBar != null) progressBar.Value = p; }));

                        extrasDllPath = await _componentService.DownloadExtrasDllAsync(selectedExtrasVersion, extrasProgress);
                    }
                    catch (Exception ex)
                    {
                        DebugWindow.Log($"[BulkInstall] Failed to download FSR4 INT8 v{selectedExtrasVersion}: {ex.Message}");
                        continue; // Skip FSR4 installation but continue with OptiScaler
                    }

                    // Copy FSR4 INT8 DLL to game directory
                    await Task.Run(() =>
                    {
                        var gameDir = resolvedGameDir ?? _installService.DetermineInstallDirectory(gameItem.Game) ?? gameItem.Game.InstallPath;
                        var destPath = System.IO.Path.Combine(gameDir, System.IO.Path.GetFileName(extrasDllPath));
                        System.IO.File.Copy(extrasDllPath, destPath, overwrite: true);
                        if (selectedExtrasIsInt8)
                        {
                            var customAmdxc64Path = _componentService.GetCachedCustomAmdxc64Path(selectedExtrasVersion);
                            if (customAmdxc64Path != null)
                                _installService.InstallCustomAmdxc64(gameDir, customAmdxc64Path);
                            // The fallback configuration only applies to INT8 packages.
                            _installService.ConfigureFsr4IntFallback(gameDir, isRdna4ForFsr4, isRdna2ForFsr4);
                        }
                        gameItem.Game.Fsr4ExtraVersion = selectedExtrasVersion;
                        DebugWindow.Log($"[BulkInstall] Copied FSR4 INT8 DLL to {destPath} for {gameItem.Name}");
                    });
                }

                // ── OptiPatcher ────────────────────────────────────────────────────
                if (installOptiPatcher && !string.IsNullOrEmpty(selectedOptiPatcherVersion))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (txtProgressStatus != null) txtProgressStatus.Text = $"Downloading OptiPatcher {selectedOptiPatcherVersion} for {gameItem.Name}...";
                        if (progressBar != null) progressBar.IsIndeterminate = true;
                    });

                    try
                    {
                        var optiPatcherProgress = new Progress<double>(p =>
                            Dispatcher.UIThread.Post(() => { if (progressBar != null) { progressBar.IsIndeterminate = false; progressBar.Value = p; } }));

                        var optiPatcherAsiPath = await _componentService.DownloadOptiPatcherAsync(selectedOptiPatcherVersion, optiPatcherProgress);

                        await Task.Run(() =>
                        {
                            var gameDir = resolvedGameDir ?? _installService.DetermineInstallDirectory(gameItem.Game) ?? gameItem.Game.InstallPath;

                            // Create plugins folder and copy the .asi
                            var pluginsDir = System.IO.Path.Combine(gameDir, "plugins");
                            System.IO.Directory.CreateDirectory(pluginsDir);
                            var destAsi = System.IO.Path.Combine(pluginsDir, "OptiPatcher.asi");
                            System.IO.File.Copy(optiPatcherAsiPath, destAsi, overwrite: true);
                            DebugWindow.Log($"[BulkInstall][OptiPatcher] Installed to {destAsi}");

                            // Patch OptiScaler.ini: ensure LoadAsiPlugins=true
                            var iniPath = System.IO.Path.Combine(gameDir, "OptiScaler.ini");
                            if (System.IO.File.Exists(iniPath))
                            {
                                var lines = System.IO.File.ReadAllLines(iniPath).ToList();
                                bool found = false;
                                for (int idx = 0; idx < lines.Count; idx++)
                                {
                                    var trimmed = lines[idx].Trim();
                                    if (trimmed.StartsWith("LoadAsiPlugins", StringComparison.OrdinalIgnoreCase) &&
                                        (trimmed.Length == "LoadAsiPlugins".Length || trimmed["LoadAsiPlugins".Length] == '='))
                                    {
                                        lines[idx] = "LoadAsiPlugins=true";
                                        found = true;
                                        break;
                                    }
                                }
                                if (!found) lines.Add("LoadAsiPlugins=true");
                                System.IO.File.WriteAllLines(iniPath, lines);
                                DebugWindow.Log($"[BulkInstall][OptiPatcher] Patched OptiScaler.ini for {gameItem.Name}");
                            }
                        });

                        Dispatcher.UIThread.Post(() => { if (progressBar != null) progressBar.IsIndeterminate = false; });
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.UIThread.Post(() => { if (progressBar != null) progressBar.IsIndeterminate = false; });
                        DebugWindow.Log($"[BulkInstall][OptiPatcher] Failed for {gameItem.Name}: {ex.Message}");
                    }
                }

                gameItem.IsInstalled = true;
                gameItem.CanInstall = false;
                gameItem.IsSelected = false;
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[BulkInstall] Failed to install {gameItem.Name}: {ex.Message}");
            }

            await Task.Delay(100); // Small delay between installations
        }

        if (progressBar != null)
            progressBar.Value = 100;

        await Task.Delay(500);

        _isInstalling = false;

        if (progressSection != null) progressSection.IsVisible = false;
        if (btnCancel != null) btnCancel.IsEnabled = true;

        UpdateSelectionCount();

        // Show completion dialog
        var completedCount = totalGames;
        await new ConfirmDialog(
            this,
            "Bulk Installation Complete",
            $"Successfully installed OptiScaler on {completedCount} game{(completedCount != 1 ? "s" : "")}.",
            isAlert: true
        ).ShowDialog<bool>(this);

        Close();
    }

    /// <summary>
    /// Batch counterpart to ManageGameWindow.ExecuteDllSwapAsync — replaces the FSR4 INT8 DLL
    /// directly in each selected game's folder, without installing OptiScaler. Auto-detection only
    /// (no per-game manual file picker makes sense in a batch context): games where none of
    /// Fsr4Int8DllHelper.SwapTargetFileNames is found are skipped, not treated as a batch failure.
    /// </summary>
    private async Task RunBulkDllSwapAsync(List<BulkGameItem> selectedGames, string extrasVersion)
    {
        _isInstalling = true;

        var btnInstall = this.FindControl<Button>("BtnInstall");
        var btnCancel = this.FindControl<Button>("BtnCancel");
        var progressSection = this.FindControl<Border>("ProgressSection");
        var txtProgressStatus = this.FindControl<TextBlock>("TxtProgressStatus");
        var txtProgressCount = this.FindControl<TextBlock>("TxtProgressCount");
        var progressBar = this.FindControl<ProgressBar>("ProgressBar");

        if (btnInstall != null) btnInstall.IsEnabled = false;
        if (btnCancel != null) btnCancel.IsEnabled = false;
        if (progressSection != null) progressSection.IsVisible = true;

        int totalGames = selectedGames.Count;
        int currentGame = 0;
        int swappedCount = 0;
        int skippedCount = 0;

        foreach (var gameItem in selectedGames)
        {
            currentGame++;

            if (txtProgressStatus != null)
                txtProgressStatus.Text = $"Swapping FSR4 INT8 DLL for {gameItem.Name}...";
            if (txtProgressCount != null)
                txtProgressCount.Text = $"{currentGame} / {totalGames}";
            if (progressBar != null)
                progressBar.Value = (currentGame - 1) * 100.0 / totalGames;

            try
            {
                var gameDir = _installService.DetermineInstallDirectory(gameItem.Game);
                if (string.IsNullOrEmpty(gameDir) || !System.IO.Directory.Exists(gameDir))
                {
                    DebugWindow.Log($"[BulkInstall][DllSwap] Could not determine game directory for {gameItem.Name}, skipping.");
                    skippedCount++;
                    continue;
                }

                var targetPath = Fsr4Int8DllHelper.FindSwapTargetIn(gameDir,
                    _componentService.GetExtrasDllVariant(extrasVersion) == Fsr4DllVariant.Int8);
                if (targetPath == null)
                {
                    DebugWindow.Log($"[BulkInstall][DllSwap] No swap target found in '{gameDir}' for {gameItem.Name}, skipping.");
                    skippedCount++;
                    continue;
                }

                // The RDNA2 companion (amdxc64.dll) has its own separate source and can be absent
                // for a given version — everything else comes from the regular Extras package.
                var targetFileName = System.IO.Path.GetFileName(targetPath);
                string sourcePath;
                if (string.Equals(targetFileName, Fsr4Int8DllHelper.CustomRdna2FileName, StringComparison.OrdinalIgnoreCase))
                {
                    if (_componentService.GetExtrasDllVariant(extrasVersion) != Fsr4DllVariant.Int8)
                    {
                        skippedCount++;
                        continue;
                    }
                    var rdna2Path = _componentService.GetCachedCustomAmdxc64Path(extrasVersion);
                    if (rdna2Path == null)
                    {
                        DebugWindow.Log($"[BulkInstall][DllSwap] FSR4 INT8 v{extrasVersion} has no amdxc64.dll replacement for {gameItem.Name}, skipping.");
                        skippedCount++;
                        continue;
                    }
                    sourcePath = rdna2Path;
                }
                else
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (txtProgressStatus != null) txtProgressStatus.Text = $"Downloading FSR4 INT8 v{extrasVersion} for {gameItem.Name}...";
                        if (progressBar != null) progressBar.IsIndeterminate = false;
                    });

                    var extrasProgress = new Progress<double>(p =>
                        Dispatcher.UIThread.Post(() => { if (progressBar != null) progressBar.Value = p; }));
                    sourcePath = await _componentService.DownloadExtrasDllAsync(extrasVersion, extrasProgress);
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (txtProgressStatus != null) txtProgressStatus.Text = $"Swapping DLL for {gameItem.Name}...";
                    if (progressBar != null) progressBar.IsIndeterminate = true;
                });

                await Task.Run(() => _installService.SwapFsr4Dll(gameItem.Game, targetPath, sourcePath, extrasVersion));

                Dispatcher.UIThread.Post(() => { if (progressBar != null) progressBar.IsIndeterminate = false; });

                gameItem.IsInstalled = true;
                gameItem.CanInstall = false;
                gameItem.IsSelected = false;
                swappedCount++;
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[BulkInstall][DllSwap] Failed to swap DLL for {gameItem.Name}: {ex.Message}");
                skippedCount++;
                Dispatcher.UIThread.Post(() => { if (progressBar != null) progressBar.IsIndeterminate = false; });
            }

            await Task.Delay(100);
        }

        if (progressBar != null)
            progressBar.Value = 100;

        await Task.Delay(500);

        _isInstalling = false;

        if (progressSection != null) progressSection.IsVisible = false;
        if (btnCancel != null) btnCancel.IsEnabled = true;

        UpdateSelectionCount();

        var summary = skippedCount > 0
            ? $"Swapped the FSR4 INT8 DLL on {swappedCount} game{(swappedCount != 1 ? "s" : "")}. Skipped {skippedCount} game{(skippedCount != 1 ? "s" : "")} with no matching DLL to replace."
            : $"Swapped the FSR4 INT8 DLL on {swappedCount} game{(swappedCount != 1 ? "s" : "")}.";
        await new ConfirmDialog(this, "Bulk DLL Swap Complete", summary, isAlert: true).ShowDialog<bool>(this);

        Close();
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        if (!_isInstalling)
        {
            Close();
        }
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        if (!_isInstalling)
        {
            Close();
        }
    }

    private void CmbOptiVersion_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateCheckboxStatesForVersion(sender as ComboBox);
        UpdateSelectionCount();
    }

    private void UpdateCheckboxStatesForVersion(ComboBox? cmb)
    {
        if (cmb == null) return;

        var selectedTag = (cmb?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        bool isBeta = !string.IsNullOrEmpty(selectedTag) && _componentService.BetaVersions.Contains(selectedTag);
        bool isNightly = !string.IsNullOrEmpty(selectedTag) && _componentService.NightlyVersions.Contains(selectedTag);

        // Stable/Beta 0.9+ bundle both components. Nightly resolves Fakenvapi automatically
        // per game when fakenvapi.dll is absent, so its manual selector remains disabled.
        bool includedInPackage = !isNightly && IsVersionGreaterOrEqual(selectedTag, 0, 9);
        bool disableFakenvapi = isNightly || includedInPackage;
        bool disableNukemFG = isNightly || includedInPackage;

        var cmbFakenvapi = this.FindControl<ComboBox>("CmbFakenvapiVersion");
        var cmbNukemFG = this.FindControl<ComboBox>("CmbNukemFGVersion");

        if (disableFakenvapi)
        {
            if (cmbFakenvapi != null)
            {
                cmbFakenvapi.IsEnabled = false;
                cmbFakenvapi.SelectedIndex = 0; // Reset to "None"
                ToolTip.SetTip(cmbFakenvapi, includedInPackage ? "Included in OptiScaler 0.9+" : null);
            }
        }
        else if (cmbFakenvapi != null)
        {
            cmbFakenvapi.IsEnabled = true;
            ToolTip.SetTip(cmbFakenvapi, null);
        }

        if (disableNukemFG)
        {
            if (cmbNukemFG != null)
            {
                cmbNukemFG.IsEnabled = false;
                cmbNukemFG.SelectedIndex = 0; // Reset to "None"
                ToolTip.SetTip(cmbNukemFG, "Included in OptiScaler 0.9+");
            }
        }
        else if (cmbNukemFG != null)
        {
            cmbNukemFG.IsEnabled = true;
            ToolTip.SetTip(cmbNukemFG, null);
        }
    }

    private static bool IsVersionGreaterOrEqual(string? ver, int targetMajor, int targetMinor)
    {
        if (string.IsNullOrEmpty(ver)) return false;

        var m = Regex.Match(ver, "^\\d+(\\.\\d+)*");
        if (!m.Success) return false;

        if (!Version.TryParse(m.Value, out var parsed)) return false;

        if (parsed.Major > targetMajor) return true;
        if (parsed.Major < targetMajor) return false;
        return parsed.Minor >= targetMinor;
    }

    private void PopulateProfileSelector()
    {
        var cmbProfile = this.FindControl<ComboBox>("CmbProfile");
        if (cmbProfile == null) return;

        _isUpdatingProfiles = true;
        cmbProfile.SelectionChanged -= CmbProfile_SelectionChanged;
        cmbProfile.Items.Clear();

        var profiles = _profileService.GetAllProfiles();
        foreach (var profile in profiles)
        {
            var item = new ComboBoxItem { Content = profile.Name, Tag = profile };
            ToolTip.SetTip(item, profile.Description);
            cmbProfile.Items.Add(item);
        }

        cmbProfile.Items.Add(new ComboBoxItem
        {
            Content = "+ New Profile",
            Tag = NewProfileTag
        });

        var defaultName = _profileService.GetDefaultProfile()?.Name;
        var selectedIndex = profiles.FindIndex(p => p.Name == defaultName);
        cmbProfile.SelectedIndex = selectedIndex >= 0 ? selectedIndex : Math.Max(0, profiles.Count - 1);

        if (profiles.Count > 0 && cmbProfile.SelectedIndex >= 0 && cmbProfile.SelectedIndex < profiles.Count)
            _lastSelectedProfileName = profiles[cmbProfile.SelectedIndex].Name;

        cmbProfile.SelectionChanged += CmbProfile_SelectionChanged;
        _isUpdatingProfiles = false;
    }

    private void PopulateOutputUpscalerSelector()
    {
        var combo = this.FindControl<ComboBox>("CmbOutputUpscaler");
        if (combo == null) return;

        _isUpdatingOutputUpscaler = true;
        try
        {
            combo.Items.Clear();
            combo.Items.Add(new ComboBoxItem { Content = GetResourceString("TxtOutputUpscalerDefault", "Default"), Tag = OutputUpscalerBackend.Default, Classes = { "SentinelOption" } });
            combo.Items.Add(new ComboBoxItem { Content = "FSR 2", Tag = OutputUpscalerBackend.Fsr2 });
            combo.Items.Add(new ComboBoxItem { Content = "FSR 3", Tag = OutputUpscalerBackend.Fsr3 });
            combo.Items.Add(new ComboBoxItem { Content = "FSR 4", Tag = OutputUpscalerBackend.Fsr4 });
            combo.Items.Add(new ComboBoxItem { Content = "XeSS", Tag = OutputUpscalerBackend.XeSS });
            combo.Items.Add(new ComboBoxItem { Content = "DLSS", Tag = OutputUpscalerBackend.Dlss });
            combo.SelectedIndex = 0;
        }
        finally
        {
            _isUpdatingOutputUpscaler = false;
        }
    }

    private void CmbOutputUpscaler_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingOutputUpscaler || sender is not ComboBox combo
            || combo.SelectedItem is not ComboBoxItem item
            || item.Tag is not OutputUpscalerBackend selected)
            return;

        _outputUpscalerSettings.Backend = selected;
    }

    private string GetResourceString(string key, string fallback)
    {
        return Application.Current?.TryFindResource(key, out var res) == true && res is string str ? str : fallback;
    }

    private void CmbProfile_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingProfiles) return;
        if (sender is not ComboBox cmbProfile) return;
        if (cmbProfile.SelectedItem is not ComboBoxItem item) return;

        if (item.Tag is OptiScalerProfile profile)
        {
            _lastSelectedProfileName = profile.Name;
            return;
        }

        if (item.Tag is string tag && tag == NewProfileTag)
        {
            var profiles = _profileService.GetAllProfiles();
            var fallbackName = _lastSelectedProfileName ?? _profileService.GetDefaultProfile()?.Name;
            var fallbackIndex = profiles.FindIndex(p => p.Name == fallbackName);

            _isUpdatingProfiles = true;
            cmbProfile.SelectedIndex = fallbackIndex >= 0 ? fallbackIndex : 0;
            _isUpdatingProfiles = false;

            this.Close();
            if (_ownerWindow is MainWindow mainWindow)
                mainWindow.NavigateToProfiles();
        }
    }

    private async void BtnFrameGeneration_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var targetGames = _gameItems
                .Where(item => item.CanInstall && item.IsSelected)
                .Select(item => item.Game)
                .ToList();
            if (targetGames.Count == 0)
            {
                targetGames = _gameItems
                    .Where(item => item.CanInstall)
                    .Select(item => item.Game)
                    .ToList();
            }
            if (targetGames.Count == 0) return;

            var gpu = GpuSelectionHelper.GetPreferredGpu(_gpuService, _componentService.Config.DefaultGpuId);
            var sharedCapabilities = await Task.Run(() =>
            {
                var service = new FrameGenerationConfigurationService();
                var capabilities = targetGames
                    .Select(game => service.DetectCapabilities(game, gpu))
                    .ToList();
                return BuildSharedFrameGenerationCapabilities(capabilities);
            });

            var dialog = new FrameGenerationSettingsWindow(this, sharedCapabilities, _frameGenerationSettings);
            var settings = await dialog.ShowDialog<GameFrameGenerationSettings?>(this);
            if (settings == null) return;

            _frameGenerationSettings = settings;
            UpdateFrameGenerationSummary();
        }
        catch (Exception ex)
        {
            await new ConfirmDialog(this, "Frame Generation",
                $"Could not configure frame generation:\n{ex.Message}").ShowDialog<object>(this);
        }
    }

    private static FrameGenerationCapabilities BuildSharedFrameGenerationCapabilities(
        IReadOnlyList<FrameGenerationCapabilities> capabilities)
    {
        var first = capabilities[0];
        var routes = first.AvailableRoutes.AsEnumerable();
        var outputs = first.AvailableOutputs.AsEnumerable();
        var mfgModes = first.AvailableMfgModes.AsEnumerable();

        foreach (var current in capabilities.Skip(1))
        {
            routes = routes.Intersect(current.AvailableRoutes);
            outputs = outputs.Intersect(current.AvailableOutputs);
            mfgModes = mfgModes.Intersect(current.AvailableMfgModes);
        }

        return new FrameGenerationCapabilities
        {
            IsDirectX12 = capabilities.All(item => item.IsDirectX12),
            IsVulkan = capabilities.All(item => item.IsVulkan),
            HasNativeDlssG = capabilities.All(item => item.HasNativeDlssG),
            HasNativeFsr3 = capabilities.All(item => item.HasNativeFsr3),
            HasStreamline = capabilities.All(item => item.HasStreamline),
            HasXeFgDependencies = capabilities.All(item => item.HasXeFgDependencies),
            HasFsrFgDependencies = capabilities.All(item => item.HasFsrFgDependencies),
            HasNukem = capabilities.All(item => item.HasNukem),
            IsIntelArc = capabilities.All(item => item.IsIntelArc),
            IsAntiCheatDetected = capabilities.Any(item => item.IsAntiCheatDetected),
            AvailableRoutes = routes.ToList(),
            AvailableOutputs = outputs.ToList(),
            AvailableMfgModes = mfgModes.ToList(),
            Warnings = capabilities.SelectMany(item => item.Warnings).Distinct().ToList()
        };
    }

    private void UpdateFrameGenerationSummary()
    {
        var button = this.FindControl<Button>("BtnFrameGeneration");
        var selection = this.FindControl<TextBlock>("TxtFrameGenerationSelection");
        if (button == null || selection == null) return;

        var route = _frameGenerationSettings.Route == FrameGenerationRoute.Auto
            ? Resource("TxtFgRouteAuto", "Auto")
            : GetFrameGenerationRouteSummary(_frameGenerationSettings.Route);
        var output = GetFrameGenerationOutputSummary(_frameGenerationSettings.Output);
        var multiplier = _frameGenerationSettings.MultiFrameMode == MultiFrameGenerationMode.Auto
            ? "Auto"
            : _frameGenerationSettings.MultiFrameMode.ToString().Replace("X", "x");

        selection.Text = _frameGenerationSettings.Route == FrameGenerationRoute.Disabled ? route : output;
        ToolTip.SetTip(button, _frameGenerationSettings.Route == FrameGenerationRoute.Disabled
            ? route
            : $"{route} → {output} · {multiplier}");
    }

    private static string GetFrameGenerationRouteSummary(FrameGenerationRoute route) => route switch
    {
        FrameGenerationRoute.Disabled => Resource("TxtFgRouteDisabled", "Disabled"),
        FrameGenerationRoute.DlssGStreamline => Resource("TxtFgRouteDlssStreamline", "DLSS-G via Streamline"),
        FrameGenerationRoute.Nukem => Resource("TxtFgRouteNukem", "Nukem DLSS-G → FSR3"),
        FrameGenerationRoute.Fsr31Native => Resource("TxtFgRouteFsr31", "Native FSR 3.1 FG"),
        FrameGenerationRoute.Fsr30Native => Resource("TxtFgRouteFsr30", "Native FSR 3.0 FG"),
        FrameGenerationRoute.OptiFg => Resource("TxtFgRouteOptiFg", "OptiFG (experimental)"),
        _ => route.ToString()
    };

    private static string GetFrameGenerationOutputSummary(FrameGenerationOutput output) => output switch
    {
        FrameGenerationOutput.Auto => "Auto",
        FrameGenerationOutput.FsrFg => "FSR FG",
        FrameGenerationOutput.XeFg => "Intel XeFG",
        FrameGenerationOutput.Nukem => "Nukem FSR3 FG",
        FrameGenerationOutput.DlssG => "DLSS-G",
        FrameGenerationOutput.DlssGWithNvngx => "DLSS-G + NvNGX",
        _ => output.ToString()
    };

    private static string Resource(string key, string fallback)
        => Application.Current?.TryFindResource(key, out var value) == true && value is string text
            ? text
            : fallback;

    private static GameFrameGenerationSettings CloneFrameGenerationSettings(GameFrameGenerationSettings source) => new()
    {
        Route = source.Route,
        Output = source.Output,
        MultiFrameMode = source.MultiFrameMode,
        AdvancedMode = source.AdvancedMode,
        DynamicTargetFps = source.DynamicTargetFps,
        NvngxReplacement = source.NvngxReplacement,
        DlssEnablerVersion = source.DlssEnablerVersion
    };

    /// <summary>
    /// Populates CmbExtrasVersion with available Extras versions + a "None" option.
    /// Selects the default based on GPU generation: RDNA 4 → None, others → global default or latest.
    /// </summary>
    private void PopulateExtrasComboBox()
    {
        var cmb = this.FindControl<ComboBox>("CmbExtrasVersion");
        if (cmb == null) return;

        if (!_extrasTabInitialized)
        {
            var defaultVersion = _componentService.Config.DefaultExtrasVersion;
            if (!string.IsNullOrWhiteSpace(defaultVersion) &&
                !defaultVersion.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                _extrasVariant = _componentService.GetExtrasDllVariant(defaultVersion);
            }
            _extrasTabInitialized = true;
        }

        UpdateExtrasVariantButtons();
        cmb.SelectionChanged -= CmbExtrasVersion_SelectionChanged;
        cmb.Items.Clear();

        // Add "None" option
        var noneStack = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
        noneStack.Children.Add(new TextBlock { Text = "None", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        cmb.Items.Add(new ComboBoxItem { Content = noneStack, Tag = "none" });

        // Add available versions
        var versions = _componentService.ExtrasAvailableVersions
            .Where(version => _componentService.GetExtrasDllVariant(version) == _extrasVariant)
            .ToList();
        var latestInVariant = versions.FirstOrDefault();
        foreach (var ver in versions)
        {
            var isLatest = string.Equals(ver, latestInVariant, StringComparison.OrdinalIgnoreCase);
            var stack = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
            stack.Children.Add(new TextBlock { Text = _componentService.GetExtrasDllDisplayName(ver), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            if (isLatest)
            {
                var badge = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#7C3AED")),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1),
                    Margin = new Thickness(0, 0, 4, 0),
                    Child = new TextBlock { Text = "LATEST", FontSize = 10, Foreground = Brushes.White, FontWeight = FontWeight.Bold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }
                };
                stack.Children.Add(badge);
            }
            cmb.Items.Add(new ComboBoxItem { Content = stack, Tag = ver });
        }

        // Determine default selection
        bool isRdna4OrRdna3 = false;
        bool isRdna2 = false;
        if (_gpuService != null)
        {
            try
            {
                var gpu = GpuSelectionHelper.GetPreferredGpu(_gpuService, _componentService.Config.DefaultGpuId);
                isRdna4OrRdna3 = GpuSelectionHelper.IsRdna4(gpu) || GpuSelectionHelper.IsRdna3(gpu);
                isRdna2 = GpuSelectionHelper.IsRdna2(gpu);
            }
            catch (Exception ex) { DebugWindow.Log($"[BulkInstall] GPU detection failed: {ex.Message}"); }
        }

        // Determine target index
        int targetIndex = 0; // Default to None (index 0)
        var globalDefault = _componentService.Config.DefaultExtrasVersion;

        if (!string.IsNullOrEmpty(globalDefault))
        {
            if (globalDefault.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                targetIndex = 0;
            }
            else
            {
                // Global preference exists (e.g. "v1.0.0"), find it in items
                for (int i = 1; i < cmb.Items.Count; i++)
                {
                    var itemVer = (cmb.Items[i] as ComboBoxItem)?.Tag?.ToString();
                    if (itemVer == globalDefault)
                    {
                        targetIndex = i;
                        break;
                    }
                }

                // If not found (e.g. it was an old version), fallback logic:
                if (targetIndex == 0)
                {
                    // Applying same "intelligent" logic if user's favorite version is gone
                    if (!isRdna4OrRdna3 && versions.Count > 0)
                    {
                        var automaticVersion = isRdna2
                            ? _componentService.GetRdna2PreferredExtrasVersion()
                            : versions[0];
                        targetIndex = automaticVersion == null ? 0 : versions.IndexOf(automaticVersion) + 1;
                    }
                }
            }
        }
        else
        {
            // No global default preference set (DefaultExtrasVersion is null/empty)
            // → Use "intelligent" logic
            if (!isRdna4OrRdna3 && versions.Count > 0)
            {
                var automaticVersion = isRdna2
                    ? _componentService.GetRdna2PreferredExtrasVersion()
                    : versions[0];
                targetIndex = automaticVersion == null ? 0 : versions.IndexOf(automaticVersion) + 1;
            }
            else
            {
                targetIndex = 0; // None
            }
        }

        cmb.SelectedIndex = targetIndex;
        cmb.SelectionChanged += CmbExtrasVersion_SelectionChanged;
        UpdateSelectionCount();
    }

    private void UpdateExtrasVariantButtons()
    {
        var int8 = this.FindControl<Button>("BtnExtrasInt8");
        var fp8 = this.FindControl<Button>("BtnExtrasFp8");
        if (int8 == null || fp8 == null) return;

        void SetActive(Button button)
        {
            button.Classes.Remove("BtnSecondary");
            button.Classes.Add("BtnPrimary");
        }

        void SetInactive(Button button)
        {
            button.Classes.Remove("BtnPrimary");
            button.Classes.Add("BtnSecondary");
        }

        if (_extrasVariant == Fsr4DllVariant.Int8)
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

    private void BtnExtrasInt8_Click(object? sender, RoutedEventArgs e)
    {
        if (_extrasVariant == Fsr4DllVariant.Int8) return;
        _extrasVariant = Fsr4DllVariant.Int8;
        PopulateExtrasComboBox();
    }

    private void BtnExtrasFp8_Click(object? sender, RoutedEventArgs e)
    {
        if (_extrasVariant == Fsr4DllVariant.Fp8) return;
        _extrasVariant = Fsr4DllVariant.Fp8;
        PopulateExtrasComboBox();
    }

    private void CmbExtrasVersion_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionCount();
    }

    private static Border CreateFsr4VariantBadge(Fsr4DllVariant variant) => new()
    {
        CornerRadius = new CornerRadius(4),
        Background = new SolidColorBrush(Color.Parse(variant == Fsr4DllVariant.Fp8 ? "#2563EB" : "#16A34A")),
        Padding = new Thickness(5, 1),
        Child = new TextBlock { Text = variant == Fsr4DllVariant.Fp8 ? "FP8" : "INT8", FontSize = 10, Foreground = Brushes.White, FontWeight = FontWeight.Bold }
    };

    private void PopulateOptiPatcherComboBox()
    {
        var cmb = this.FindControl<ComboBox>("CmbOptiPatcherVersion");
        if (cmb == null) return;

        cmb.Items.Clear();
        cmb.Items.Add(new ComboBoxItem { Content = "None", Tag = "none", Classes = { "SentinelOption" } });

        var versions = _componentService.OptiPatcherAvailableVersions;
        foreach (var ver in versions)
        {
            bool isLatest = ver == _componentService.LatestOptiPatcherVersion;
            cmb.Items.Add(BuildVersionItem(ver, isBeta: false, isLatest: isLatest));
        }

        // Respect configured default
        int targetIndex = 0;
        var savedDefault = _componentService.Config.DefaultOptiPatcherVersion;
        if (!string.IsNullOrEmpty(savedDefault) && !savedDefault.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 1; i < cmb.Items.Count; i++)
            {
                if (cmb.Items[i] is ComboBoxItem ci &&
                    string.Equals(ci.Tag?.ToString(), savedDefault, StringComparison.OrdinalIgnoreCase))
                {
                    targetIndex = i;
                    break;
                }
            }
        }

        cmb.SelectedIndex = targetIndex;
    }

    private void PopulateFakenvapiComboBox()
    {
        var cmb = this.FindControl<ComboBox>("CmbFakenvapiVersion");
        if (cmb == null) return;

        cmb.Items.Clear();
        cmb.Items.Add(new ComboBoxItem { Content = "None", Tag = "none", Classes = { "SentinelOption" } });

        var versions = _componentService.FakenvapiAvailableVersions;
        foreach (var ver in versions)
        {
            var isLatest = ver == _componentService.LatestFakenvapiVersion;
            cmb.Items.Add(BuildVersionItem(ver, isBeta: false, isLatest: isLatest));
        }

        cmb.Items.Add(new ComboBoxItem { Content = "Manage versions\u2026", Tag = "__manage__" });

        // Pre-select configured default
        var savedFakenvapi = _componentService.Config.DefaultFakenvapiVersion;
        cmb.SelectedIndex = 0;
        if (!string.IsNullOrEmpty(savedFakenvapi) && !savedFakenvapi.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 1; i < cmb.Items.Count; i++)
            {
                if ((cmb.Items[i] as ComboBoxItem)?.Tag?.ToString() == savedFakenvapi)
                {
                    cmb.SelectedIndex = i;
                    break;
                }
            }
        }

        cmb.SelectionChanged += (s, e) =>
        {
            if (cmb.SelectedItem is ComboBoxItem item && item.Tag?.ToString() == "__manage__")
            {
                cmb.SelectedIndex = 0;
                var cacheWindow = new CacheManagementWindow("fakenvapi");
                cacheWindow.ShowDialog(this);
            }
        };
    }

    private void PopulateNukemFGComboBox()
    {
        var cmb = this.FindControl<ComboBox>("CmbNukemFGVersion");
        if (cmb == null) return;

        cmb.Items.Clear();
        cmb.Items.Add(new ComboBoxItem { Content = "None", Tag = "none", Classes = { "SentinelOption" } });

        var versions = _componentService.GetDownloadedNukemFGVersions();
        foreach (var ver in versions)
        {
            cmb.Items.Add(new ComboBoxItem { Content = ver, Tag = ver });
        }

        cmb.Items.Add(new ComboBoxItem { Content = "Manage versions\u2026", Tag = "__manage__" });

        // Pre-select configured default
        var savedNukemFG = _componentService.Config.DefaultNukemFGVersion;
        cmb.SelectedIndex = 0;
        if (!string.IsNullOrEmpty(savedNukemFG) && !savedNukemFG.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 1; i < cmb.Items.Count; i++)
            {
                if ((cmb.Items[i] as ComboBoxItem)?.Tag?.ToString() == savedNukemFG)
                {
                    cmb.SelectedIndex = i;
                    break;
                }
            }
        }

        cmb.SelectionChanged += (s, e) =>
        {
            if (cmb.SelectedItem is ComboBoxItem item && item.Tag?.ToString() == "__manage__")
            {
                cmb.SelectedIndex = 0;
                var cacheWindow = new CacheManagementWindow("nukemfg");
                cacheWindow.ShowDialog(this);
            }
        };
    }

    // (Replaced by unified version earlier)

    private void TxtSearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            ApplyFilter(textBox.Text);
        }
    }

    private void TxtSearch_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            // Clear focus when clicking outside
            this.Focus();
        }
    }

    private void ApplyFilter(string? searchText)
    {
        _filteredGameItems.Clear();

        if (string.IsNullOrWhiteSpace(searchText))
        {
            // Show all games
            foreach (var game in _allGames)
            {
                _filteredGameItems.Add(game);
            }
        }
        else
        {
            // Filter games
            var filtered = _allGames.Where(g =>
                g.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var game in filtered)
            {
                _filteredGameItems.Add(game);
            }
        }
    }
}

public class BulkGameItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isInstalled;
    private bool _canInstall;

    public Game Game { get; set; } = null!;
    public string Name { get; set; } = "";
    public string Platform { get; set; } = "";
    public string? CoverPath { get; set; }
    public string? OptiscalerVersion { get; set; }
    public bool IsOptiscalerInstalled { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }
    }

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (_isInstalled != value)
            {
                _isInstalled = value;
                OnPropertyChanged(nameof(IsInstalled));
            }
        }
    }

    public bool CanInstall
    {
        get => _canInstall;
        set
        {
            if (_canInstall != value)
            {
                _canInstall = value;
                OnPropertyChanged(nameof(CanInstall));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
