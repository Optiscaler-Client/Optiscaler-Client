// OptiScaler Client - A frontend for managing OptiScaler installations
// Copyright (C) 2026 Agustín Montaña (Agustinm28)
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using OptiscalerClient.Models;
using OptiscalerClient.Services;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using System.Collections.ObjectModel;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Avalonia.VisualTree;
using OptiscalerClient.Helpers;
using OptiscalerClient.Models.Help;
using Avalonia.Styling;
using System.Text.Json;
using System.Globalization;

namespace OptiscalerClient.Views
{
    public partial class MainWindow : Window, IGamepadInputHost
    {
        private readonly GameScannerService _scannerService;
        private readonly GamePersistenceService _persistenceService;
        private ObservableCollection<Game> _games;
        private List<Game> _allGames = new List<Game>();
        private readonly ComponentManagementService _componentService;
        private IGpuDetectionService _gpuService;

        private GpuInfo? _lastDetectedGpu;
        private ScrollViewer? _gameListScrollViewer;
        private ScrollViewer? _gameGridScrollViewer;
        private bool _isInitializingLanguage = true;
        private bool _isGridView = true;
        private readonly Dictionary<Button, DispatcherTimer> _quickInstallDotTimers = new();
        private readonly Dictionary<Button, double> _quickInstallDotPhases = new();
        private readonly Dictionary<Button, double> _quickInstallOriginalMinWidths = new();
        private readonly CancellationTokenSource _windowLifetimeCts = new();

        private readonly GameAnalyzerService _analyzerService = new();
        private GameMetadataService _metadataService = null!;
        private readonly HelpPageService _helpPageService = new();
        private GamepadNavigationHelper? _gamepadHelper;

        GamepadHelperBase? IGamepadInputHost.GamepadHelper => _gamepadHelper;
        private string _currentHelpPageId = "about";
        private double? _currentPageFontSize;
        private bool _isCursorHiddenByGamepad;
        private Point? _lastPointerPosition;

        private ListBox? _lstGames;
        private ListBox? _lstGamesGrid;
        private TextBlock? _txtStatus;
        private Button? _btnScan;
        private Button? _btnViewList;
        private Button? _btnViewGrid;
        private Button? _btnEditMode;
        private Border? _editModeBanner;
        private Grid? _overlayScanning;
        private Grid? _overlayLoading;
        private TextBox? _txtSearch;
        private TextBlock? _txtSearchPlaceholder;
        private TextBlock? _txtGpuInfo;
        private Avalonia.Controls.Shapes.Path? _txtGamepadIcon;
        private Border? _pnlNoUpscalersFound;
        private CheckBox? _chkHideNoUpscaler;
        private CheckBox? _chkOnlyInstalled;
        private CheckBox? _chkOnlyFavorites;
        private bool _hasScanned = false;
        private bool _isEditMode = false;
        private Game? _draggedGame;

        // Custom pointer-drag state
        private Canvas? _dragGhostCanvas;
        private Border? _dragGhost;
        private Border? _dragInsertLine;
        private bool _isDragging = false;
        private Point _dragStartPos;
        private Point _ghostOffset;
        private int _currentDropIndex = -1;
        private int _originalGamepadDragIndex = -1;
        private int _lastAnimatedDropIndex = -2;
        private Control? _dragCaptureControl;
        private ListBoxItem? _dragSourceContainer;
        private List<double> _itemLayoutTops = new();
        private double _itemLayoutHeight = 0;

        // Grid-view drag animation layout data
        private int _gridColCount = 0;
        private double _gridItemWidth = 0;
        private double _gridItemHeight = 0;
        private double _gridHGap = 0;
        private double _gridVGap = 0;
        private double _gridOriginX = 0;
        private double _gridOriginY = 0;

        // OSK Virtual Keyboard state
        public bool IsVirtualKeyboardOpen { get; private set; }
        private readonly string[] _oskKeys = new string[]
        {
            "1", "2", "3", "4", "5", "6", "7", "8", "9", "0",
            "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P",
            "A", "S", "D", "F", "G", "H", "J", "K", "L", "-",
            "Z", "X", "C", "V", "B", "N", "M", ",", ".", "_"
        };
        private int _oskCol = 0;
        private int _oskRow = 0;
        private TextBox? _oskTargetTextBox = null;

        private void InitializeOsk()
        {
            var grid = this.FindControl<Avalonia.Controls.Primitives.UniformGrid>("OskGrid");
            if (grid == null) return;

            for (int i = 0; i < _oskKeys.Length; i++)
            {
                var border = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#1AFFFFFF")),
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(4),
                    Width = 48,
                    Height = 48,
                    Child = new TextBlock
                    {
                        Text = _oskKeys[i],
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        FontSize = 20,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Brushes.White
                    }
                };
                grid.Children.Add(border);
            }
        }

        public void OpenVirtualKeyboard(string targetTextBoxName = "TxtSearch")
        {
            IsVirtualKeyboardOpen = true;
            _oskTargetTextBox = this.FindControl<TextBox>(targetTextBoxName);
            var overlay = this.FindControl<Border>("VirtualKeyboardOverlay");
            if (overlay != null) overlay.IsVisible = true;
            UpdateOskPreview();
            UpdateOskSelection();
        }

        public void CloseVirtualKeyboard()
        {
            IsVirtualKeyboardOpen = false;
            _oskTargetTextBox = null;
            var overlay = this.FindControl<Border>("VirtualKeyboardOverlay");
            if (overlay != null) overlay.IsVisible = false;
        }

        private void UpdateOskPreview()
        {
            var txtPreview = this.FindControl<TextBlock>("TxtOskPreview");
            if (txtPreview != null && _oskTargetTextBox != null)
            {
                txtPreview.Text = _oskTargetTextBox.Text ?? "";
            }
        }

        private void UpdateOskSelection()
        {
            var grid = this.FindControl<Avalonia.Controls.Primitives.UniformGrid>("OskGrid");
            if (grid == null) return;

            int index = _oskRow * 10 + _oskCol;
            for (int i = 0; i < grid.Children.Count; i++)
            {
                var border = grid.Children[i] as Border;
                if (border == null) continue;

                if (i == index)
                {
                    border.Background = new SolidColorBrush(Color.Parse("#8B73F8")); // Accent color
                    border.BorderBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));
                    border.BorderThickness = new Thickness(2);
                }
                else
                {
                    border.Background = new SolidColorBrush(Color.Parse("#1AFFFFFF"));
                    border.BorderBrush = null;
                    border.BorderThickness = new Thickness(0);
                }
            }
        }

        public void HandleVirtualKeyboardInput(GamepadButton button)
        {
            if (_oskTargetTextBox == null) return;

            switch (button)
            {
                case GamepadButton.DPadUp:
                case GamepadButton.ThumbLeftUp:
                    _oskRow = Math.Max(0, _oskRow - 1);
                    UpdateOskSelection();
                    break;
                case GamepadButton.DPadDown:
                case GamepadButton.ThumbLeftDown:
                    _oskRow = Math.Min(3, _oskRow + 1);
                    UpdateOskSelection();
                    break;
                case GamepadButton.DPadLeft:
                case GamepadButton.ThumbLeftLeft:
                    _oskCol = Math.Max(0, _oskCol - 1);
                    UpdateOskSelection();
                    break;
                case GamepadButton.DPadRight:
                case GamepadButton.ThumbLeftRight:
                    _oskCol = Math.Min(9, _oskCol + 1);
                    UpdateOskSelection();
                    break;
                case GamepadButton.A:
                    int index = _oskRow * 10 + _oskCol;
                    if (index >= 0 && index < _oskKeys.Length)
                    {
                        _oskTargetTextBox.Text = (_oskTargetTextBox.Text ?? "") + _oskKeys[index];
                        _oskTargetTextBox.CaretIndex = _oskTargetTextBox.Text.Length;
                        UpdateOskPreview();
                    }
                    break;
                case GamepadButton.B:
                    CloseVirtualKeyboard();
                    break;
                case GamepadButton.X:
                    if (!string.IsNullOrEmpty(_oskTargetTextBox.Text))
                    {
                        _oskTargetTextBox.Text = _oskTargetTextBox.Text.Substring(0, _oskTargetTextBox.Text.Length - 1);
                        _oskTargetTextBox.CaretIndex = _oskTargetTextBox.Text.Length;
                        UpdateOskPreview();
                    }
                    break;
                case GamepadButton.Y:
                    _oskTargetTextBox.Text = (_oskTargetTextBox.Text ?? "") + " ";
                    _oskTargetTextBox.CaretIndex = _oskTargetTextBox.Text.Length;
                    UpdateOskPreview();
                    break;
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        /// <summary>
        /// Test-only constructor. Injects a custom <see cref="IGpuDetectionService"/> instead
        /// of the platform-default one so unit/headless tests can run without real hardware.
        /// </summary>
        internal MainWindow(IGpuDetectionService? gpuService) : this()
        {
            // Override the service assigned by the public constructor.
            _gpuService = gpuService!;
            // Reset cached GPU so the injected service is actually called.
            _lastDetectedGpu = null;
        }

        public MainWindow()
        {
            InitializeComponent();
            InitializeOsk();
            _scannerService = new GameScannerService();
            _persistenceService = new GamePersistenceService();
            _componentService = new ComponentManagementService();
            _metadataService = new GameMetadataService(_componentService);
            App.ChangeLanguage(_componentService.Config.Language);
            _gpuService = PlatformServiceFactory.CreateGpuDetectionService()!;
            _games = new ObservableCollection<Game>();

            // Debug Window check
            if (_componentService.Config.Debug)
            {
                var debugWindow = new DebugWindow(true);
                debugWindow.Show();
                DebugWindow.Log("Application Started in DEBUG mode.");
            }

            _componentService.OnStatusChanged += ComponentStatusChanged;
            this.Loaded += MainWindow_Loaded;
            this.Closed += MainWindow_Closed;

            // Restore window state
            RestoreWindowState();

            // Handle window state changes
            this.PropertyChanged += MainWindow_PropertyChanged;
            this.PositionChanged += (s, e) => SaveWindowState();
            this.SizeChanged += MainWindow_SizeChanged;
            this.KeyDown += HandleEditorKeyCapture;
            this.AddHandler(InputElement.PointerMovedEvent, MainWindow_PointerMoved, handledEventsToo: true);
        }

        private void ComponentStatusChanged()
        {
            Dispatcher.UIThread.Post(RepopulateVersionCombos);
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            // Save final window state before closing
            SaveWindowState();

            this.RemoveHandler(InputElement.PointerMovedEvent, MainWindow_PointerMoved);

            if (_gamepadHelper != null)
            {
                _gamepadHelper.GamepadConnectionChanged -= GamepadHelper_GamepadConnectionChanged;
                _gamepadHelper.GamepadActivity -= GamepadHelper_GamepadActivity;
            }

            _gamepadHelper?.Dispose();

            if (!_windowLifetimeCts.IsCancellationRequested)
            {
                _windowLifetimeCts.Cancel();
            }

            _windowLifetimeCts.Dispose();
            _componentService.OnStatusChanged -= ComponentStatusChanged;
        }

        private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            try
            {
                _gameListScrollViewer = this.FindControl<ScrollViewer>("GameListScrollViewer");
                _gameGridScrollViewer = this.FindControl<ScrollViewer>("GameGridScrollViewer");
                _lstGames = this.FindControl<ListBox>("LstGames");
                _lstGamesGrid = this.FindControl<ListBox>("LstGamesGrid");
                if (_lstGames != null) _lstGames.ContainerPrepared += GameContainer_FavoriteIconPrepared;
                if (_lstGamesGrid != null) _lstGamesGrid.ContainerPrepared += GameContainer_FavoriteIconPrepared;
                _txtStatus = this.FindControl<TextBlock>("TxtStatus");
                _btnScan = this.FindControl<Button>("BtnScan");
                _btnViewList = this.FindControl<Button>("BtnViewList");
                _btnViewGrid = this.FindControl<Button>("BtnViewGrid");
                _btnEditMode = this.FindControl<Button>("BtnEditMode");
                _editModeBanner = this.FindControl<Border>("EditModeBanner");
                _dragGhostCanvas = this.FindControl<Canvas>("DragGhostCanvas");
                _overlayScanning = this.FindControl<Grid>("OverlayScanning");
                _overlayLoading = this.FindControl<Grid>("OverlayLoading");
                _txtSearch = this.FindControl<TextBox>("TxtSearch");
                _txtSearchPlaceholder = this.FindControl<TextBlock>("TxtSearchPlaceholder");
                _txtGpuInfo = this.FindControl<TextBlock>("TxtGpuInfo");
                _txtGamepadIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("TxtGamepadIcon");
                _pnlNoUpscalersFound = this.FindControl<Border>("PnlNoUpscalersFound");
                _chkHideNoUpscaler = this.FindControl<CheckBox>("ChkHideNoUpscaler");
                _chkOnlyInstalled = this.FindControl<CheckBox>("ChkOnlyInstalled");
                _chkOnlyFavorites = this.FindControl<CheckBox>("ChkOnlyFavorites");

                if (_chkHideNoUpscaler != null) _chkHideNoUpscaler.IsChecked = _componentService.Config.HideGamesWithoutUpscaler;
                if (_chkOnlyInstalled != null) _chkOnlyInstalled.IsChecked = _componentService.Config.ShowOnlyInstalled;
                if (_chkOnlyFavorites != null) _chkOnlyFavorites.IsChecked = _componentService.Config.ShowOnlyFavorites;

                if (_lstGames != null) _lstGames.ItemsSource = _games;
                if (_lstGamesGrid != null) _lstGamesGrid.ItemsSource = _games;

                _isGridView = _componentService.Config.PreferGridView;
                ApplyGameViewMode();

                bool hadSavedGames = LoadSavedGames(_windowLifetimeCts.Token);
                _ = LoadGpuInfoAsync();
                _ = ScheduleStartupUpdatesAsync(_windowLifetimeCts.Token);

                var linuxNotice = this.FindControl<Border>("LinuxNotice");
                if (linuxNotice != null)
                    linuxNotice.IsVisible = OperatingSystem.IsLinux();

                UpdateAnimationsState(_componentService.Config.AnimationsEnabled);

                var gamepadService = PlatformServiceFactory.CreateGamepadDetectionService();
                _gamepadHelper = new GamepadNavigationHelper(this, gamepadService);
                _gamepadHelper.GamepadConnectionChanged += GamepadHelper_GamepadConnectionChanged;
                _gamepadHelper.GamepadActivity += GamepadHelper_GamepadActivity;

                // Show welcome/changelog popup when the app version changes (or on first run)
                if (_componentService.Config.LastSeenAppVersion != App.AppVersion)
                {
                    var welcome = new WelcomeWindow(this);
                    await welcome.ShowDialog(this);
                    _componentService.Config.LastSeenAppVersion = App.AppVersion;
                    _componentService.SaveConfiguration();
                }

                if (!hadSavedGames)
                {
                    if (_componentService.Config.HasCompletedInitialScan)
                    {
                        _componentService.Config.HasCompletedInitialScan = false;
                        _componentService.SaveConfiguration();
                    }

                    var prompt = new InitialScanPromptWindow(this, _componentService, isFirstTime: true);
                    var options = await prompt.ShowDialog<InitialScanOptions?>(this);
                    if (options != null)
                    {
                        _componentService.Config.ScanSources = options.ScanSources;
                        _componentService.Config.ScanDriveRoots = options.DriveRoots;
                        _componentService.Config.HasCompletedInitialScan = true;
                        _componentService.SaveConfiguration();
                        await RunScanAsync(options.UpscalerFilter);
                    }

                    // Never auto-scan on startup when there are no cached games.
                    return;
                }

                // If there are cached games, do not auto-scan on startup.
                // Scans should only run when the user explicitly clicks Scan Games.
            }
            catch (Exception ex) { DebugWindow.Log($"[MainWindow] Loaded handler failed: {ex.Message}"); }
        }

        private void GamepadHelper_GamepadConnectionChanged(object? sender, bool isConnected)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_txtGamepadIcon != null)
                {
                    _txtGamepadIcon.Fill = isConnected ?
                        (this.FindResource("BrAccent") as IBrush ?? Brushes.Purple) :
                        (this.FindResource("BrTextSecondary") as IBrush ?? Brushes.Gray);

                    ToolTip.SetTip(_txtGamepadIcon, isConnected ?
                        (this.FindResource("TxtGamepadConnected") as string ?? "Gamepad connected") :
                        (this.FindResource("TxtGamepadDisconnected") as string ?? "Gamepad disconnected"));
                }

                if (!isConnected)
                {
                    ShowMouseCursor();
                    _gamepadHelper?.SwitchToMouseMode();
                }
            });
        }

        private void GamepadHelper_GamepadActivity(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(HideMouseCursorForGamepad);
        }

        private void MainWindow_PointerMoved(object? sender, PointerEventArgs e)
        {
            var position = e.GetPosition(this);

            if (!_isCursorHiddenByGamepad)
            {
                _lastPointerPosition = position;
                return;
            }

            // Avalonia re-hit-tests under a stationary cursor whenever a Popup
            // appears (e.g. opening a ComboBox dropdown with the gamepad),
            // which raises a synthetic PointerMoved with no real mouse motion.
            // Only genuine movement should switch back to mouse mode —
            // otherwise opening a dropdown via gamepad immediately closes it.
            if (_lastPointerPosition is { } last)
            {
                var delta = position - last;
                if (Math.Abs(delta.X) < 1.0 && Math.Abs(delta.Y) < 1.0)
                    return;
            }

            _lastPointerPosition = position;
            ShowMouseCursor();
            _gamepadHelper?.SwitchToMouseMode();
        }

        private void HideMouseCursorForGamepad()
        {
            if (_isCursorHiddenByGamepad) return;

            // If the pointer was resting over a card before controller input,
            // clear stale hover visuals so UI reflects controller mode.
            if (!IsFocusInsideGridHoverActions())
            {
                ClearGridCardHoverVisuals();
            }

            Cursor = new Cursor(StandardCursorType.None);
            _isCursorHiddenByGamepad = true;
        }

        private void ShowMouseCursor()
        {
            if (!_isCursorHiddenByGamepad) return;
            Cursor = new Cursor(StandardCursorType.Arrow);
            _isCursorHiddenByGamepad = false;
        }

        private bool IsFocusInsideGridHoverActions()
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (focused is not Visual visual) return false;

            return visual.GetVisualAncestors()
                .OfType<Panel>()
                .Any(p => string.Equals(p.Name, "GridCardHoverActions", StringComparison.Ordinal));
        }

        private void ClearGridCardHoverVisuals()
        {
            if (_lstGamesGrid == null) return;

            var cards = _lstGamesGrid.GetVisualDescendants()
                .OfType<Border>()
                .Where(x => x.Classes.Contains("GameGridCard"));

            foreach (var card in cards)
            {
                ToggleGridCardHover(card, false);
            }
        }

        private void MainWindow_SizeChanged(object? sender, SizeChangedEventArgs e)
        {
            UpdateSettingsLayout();
            UpdateEditorWrapLayout();
        }

        private void UpdateSettingsLayout()
        {
            var settingsGrid = this.FindControl<Grid>("SettingsGrid");
            if (settingsGrid == null) return;

            // Determine number of columns based on window width
            int newColumns = this.Width < 1000 ? 1 : 2;

            // Update column definitions if needed
            if (settingsGrid.ColumnDefinitions.Count != newColumns)
            {
                settingsGrid.ColumnDefinitions.Clear();
                for (int i = 0; i < newColumns; i++)
                {
                    settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                }

                // Re-arrange existing elements for new layout
                RearrangeSettingsElements(settingsGrid, newColumns);
            }
        }

        private void RearrangeSettingsElements(Grid settingsGrid, int columns)
        {
            var children = settingsGrid.Children.ToArray();
            settingsGrid.Children.Clear();

            // Get elements in their original order (by row, then column)
            var orderedElements = children
                .Where(child => Grid.GetRow(child) > 0) // Skip header
                .OrderBy(child => Grid.GetRow(child))
                .ThenBy(child => Grid.GetColumn(child))
                .ToList();

            // Add header first
            var header = children.FirstOrDefault(child => Grid.GetRow(child) == 0);
            if (header != null)
            {
                Grid.SetColumnSpan(header, columns);
                settingsGrid.Children.Add(header);
            }

            // Reorganize elements for new layout
            for (int i = 0; i < orderedElements.Count; i++)
            {
                var child = orderedElements[i];
                int newRow = (i / columns) + 1; // +1 for header row
                int newCol = i % columns;

                // Update margins based on new layout
                if (child is Border border)
                {
                    if (columns == 1)
                    {
                        // Single column - no horizontal margins
                        border.Margin = new Thickness(0, 0, 0, 16);
                    }
                    else
                    {
                        // Two columns - add horizontal margins
                        if (newCol == 0)
                        {
                            border.Margin = new Thickness(0, 0, 8, 16);
                        }
                        else
                        {
                            border.Margin = new Thickness(8, 0, 0, 16);
                        }
                    }
                }

                Grid.SetRow(child, newRow);
                Grid.SetColumn(child, newCol);
                settingsGrid.Children.Add(child);
            }

            // Update row definitions
            settingsGrid.RowDefinitions.Clear();
            int totalRows = ((orderedElements.Count + columns - 1) / columns) + 1; // +1 for header
            for (int i = 0; i < totalRows; i++)
            {
                settingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
        }

        private void UpdateSearchPlaceholderVisibility()
        {
            if (_txtSearchPlaceholder == null || _txtSearch == null) return;

            if (_txtSearch.IsFocused)
            {
                _txtSearchPlaceholder.IsVisible = false;
            }
            else
            {
                _txtSearchPlaceholder.IsVisible = string.IsNullOrEmpty(_txtSearch.Text);
            }
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSearchPlaceholderVisibility();
            if (sender is TextBox textBox)
            {
                ApplyFilter(textBox.Text);
            }
        }

        private void ApplyFilter(string? searchText)
        {
            if (_allGames == null) return;

            // In edit mode show all games (including hidden) so the user can reveal them.
            // In normal mode exclude hidden games.
            IEnumerable<Game> source = _isEditMode
                ? _allGames
                : _allGames.Where(g => !g.IsHidden);

            if (_componentService.Config.HideGamesWithoutUpscaler)
                source = source.Where(g => g.HasUpscaler);

            if (_componentService.Config.ShowOnlyInstalled)
                source = source.Where(g => g.IsOptiscalerInstalled);

            if (_componentService.Config.ShowOnlyFavorites)
                source = source.Where(g => g.IsFavorite);

            var filtered = string.IsNullOrWhiteSpace(searchText)
                ? source.ToList()
                : source.Where(g => g.Name != null && g.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase)).ToList();

            _games.Clear();
            foreach (var game in filtered)
            {
                _games.Add(game);
            }

            if (_pnlNoUpscalersFound != null)
                _pnlNoUpscalersFound.IsVisible = _hasScanned && _games.Count == 0;
        }

        private void RefreshGameLists()
        {
            if (_lstGames != null)
            {
                _lstGames.ItemsSource = null;
                _lstGames.ItemsSource = _games;
            }

            if (_lstGamesGrid != null)
            {
                _lstGamesGrid.ItemsSource = null;
                _lstGamesGrid.ItemsSource = _games;
            }
        }

        private void TxtSearch_GotFocus(object sender, GotFocusEventArgs e)
        {
            UpdateSearchPlaceholderVisibility();
        }

        private void TxtSearch_LostFocus(object sender, RoutedEventArgs e)
        {
            UpdateSearchPlaceholderVisibility();
        }

        private void GameListScrollViewer_PointerWheelChanged(object sender, PointerWheelEventArgs e)
        {
            if (sender is ScrollViewer scrollViewer && e.Delta.Y != 0)
            {
                e.Handled = true;
                // Move manually with boost
                var currentOffset = scrollViewer.Offset;
                var newY = currentOffset.Y - (e.Delta.Y * 120); // 120 for fast and fluid
                scrollViewer.Offset = new Vector(currentOffset.X, Math.Max(0, newY));
            }
        }

        private void GameGridCard_PointerEntered(object? sender, PointerEventArgs e)
        {
            if (_isEditMode) return;
            if (_isCursorHiddenByGamepad) return;
            if (sender is Border card)
            {
                ToggleGridCardHover(card, true);
            }
        }

        private void GameGridCard_PointerExited(object? sender, PointerEventArgs e)
        {
            if (sender is Border card)
            {
                ToggleGridCardHover(card, false);
            }
        }

        private void ToggleGridCardHover(Border card, bool isVisible)
        {
            var overlay = card.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(x => x.Name == "GridCardHoverOverlay");

            var actions = card.GetVisualDescendants()
                .OfType<Panel>()
                .FirstOrDefault(x => x.Name == "GridCardHoverActions");

            var favoriteBtn = card.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(x => x.Name == "BtnToggleFavoriteGrid");

            if (overlay == null || actions == null) return;

            bool animationsEnabled = _componentService.Config.AnimationsEnabled;

            if (!animationsEnabled)
            {
                overlay.IsVisible = isVisible;
                actions.IsVisible = isVisible;
                overlay.Opacity = isVisible ? 1 : 0;
                actions.Opacity = isVisible ? 1 : 0;
                actions.IsHitTestVisible = isVisible;

                if (favoriteBtn != null)
                {
                    favoriteBtn.IsVisible = isVisible;
                    favoriteBtn.Opacity = isVisible ? 1 : 0;
                    favoriteBtn.IsHitTestVisible = isVisible;
                }
                return;
            }

            EnsureHoverOpacityTransition(overlay);
            EnsureHoverOpacityTransition(actions);
            if (favoriteBtn != null) EnsureHoverOpacityTransition(favoriteBtn);

            overlay.IsVisible = true;
            actions.IsVisible = true;
            actions.IsHitTestVisible = isVisible;
            overlay.Opacity = isVisible ? 1 : 0;
            actions.Opacity = isVisible ? 1 : 0;

            if (favoriteBtn != null)
            {
                favoriteBtn.IsVisible = true;
                favoriteBtn.IsHitTestVisible = isVisible;
                favoriteBtn.Opacity = isVisible ? 1 : 0;
            }

            if (!isVisible)
            {
                _ = HideGridCardHoverAfterFadeAsync(overlay, actions, favoriteBtn);
            }
        }

        private static void EnsureHoverOpacityTransition(Visual visual)
        {
            if (visual.Transitions == null)
            {
                visual.Transitions = new Avalonia.Animation.Transitions();
            }

            if (!visual.Transitions.OfType<Avalonia.Animation.DoubleTransition>()
                .Any(t => t.Property == Visual.OpacityProperty))
            {
                visual.Transitions.Add(new Avalonia.Animation.DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(150),
                    Easing = new Avalonia.Animation.Easings.CubicEaseOut()
                });
            }
        }

        private static async Task HideGridCardHoverAfterFadeAsync(Border overlay, Panel actions, Button? favoriteBtn = null)
        {
            await Task.Delay(170);

            if (overlay.Opacity <= 0.01)
            {
                overlay.IsVisible = false;
            }

            if (actions.Opacity <= 0.01)
            {
                actions.IsVisible = false;
                actions.IsHitTestVisible = false;
            }

            if (favoriteBtn != null && favoriteBtn.Opacity <= 0.01)
            {
                favoriteBtn.IsVisible = false;
                favoriteBtn.IsHitTestVisible = false;
            }
        }

        private void BtnViewList_Click(object sender, RoutedEventArgs e)
        {
            _isGridView = false;
            _componentService.Config.PreferGridView = false;
            _componentService.SaveConfiguration();
            ApplyGameViewMode();
        }

        private void BtnViewGrid_Click(object sender, RoutedEventArgs e)
        {
            _isGridView = true;
            _componentService.Config.PreferGridView = true;
            _componentService.SaveConfiguration();
            ApplyGameViewMode();
        }

        private void ApplyGameViewMode()
        {
            if (_gameListScrollViewer != null)
            {
                _gameListScrollViewer.IsVisible = !_isGridView;
                _gameListScrollViewer.IsHitTestVisible = !_isGridView;
            }

            if (_gameGridScrollViewer != null)
            {
                _gameGridScrollViewer.IsVisible = _isGridView;
                _gameGridScrollViewer.IsHitTestVisible = _isGridView;
            }

            var activeBg = this.FindResource("BrBgCard") as IBrush ?? Brushes.DimGray;
            var inactiveBg = Brushes.Transparent;
            var activeFg = this.FindResource("BrTextPrimary") as IBrush ?? Brushes.White;
            var inactiveFg = this.FindResource("BrTextSecondary") as IBrush ?? Brushes.Gray;

            if (_btnViewList != null)
            {
                _btnViewList.Background = _isGridView ? inactiveBg : activeBg;
                _btnViewList.Foreground = _isGridView ? inactiveFg : activeFg;
            }

            if (_btnViewGrid != null)
            {
                _btnViewGrid.Background = _isGridView ? activeBg : inactiveBg;
                _btnViewGrid.Foreground = _isGridView ? activeFg : inactiveFg;
            }
        }

        private void BtnEditMode_Click(object? sender, RoutedEventArgs e)
        {
            _isEditMode = !_isEditMode;
            if (_editModeBanner != null) _editModeBanner.IsVisible = _isEditMode;

            if (_btnEditMode != null)
            {
                if (_isEditMode) _btnEditMode.Classes.Add("BtnActive");
                else _btnEditMode.Classes.Remove("BtnActive");
            }

            if (_isEditMode) _lstGames?.Classes.Add("EditMode");
            else _lstGames?.Classes.Remove("EditMode");

            if (_isEditMode) _lstGamesGrid?.Classes.Add("EditMode");
            else _lstGamesGrid?.Classes.Remove("EditMode");

            ApplyFilter(_txtSearch?.Text);
            Dispatcher.UIThread.Post(() => ApplyEditModeToCards(_isEditMode), DispatcherPriority.Loaded);
        }

        private void BtnEditModeDone_Click(object? sender, RoutedEventArgs e)
        {
            _isEditMode = false;
            if (_editModeBanner != null) _editModeBanner.IsVisible = false;
            if (_btnEditMode != null) _btnEditMode.Classes.Remove("BtnActive");
            _lstGames?.Classes.Remove("EditMode");
            _lstGamesGrid?.Classes.Remove("EditMode");

            for (int i = 0; i < _allGames.Count; i++) _allGames[i].DisplayOrder = i;
            _persistenceService.SaveGames(_allGames);

            ApplyFilter(_txtSearch?.Text);
            Dispatcher.UIThread.Post(() => ApplyEditModeToCards(false), DispatcherPriority.Loaded);
        }

        private void ApplyEditModeToCards(bool editMode)
        {
            var warmBrush   = this.FindResource("BrAccentWarm") as IBrush ?? Brushes.Orange;
            var secondaryBrush = this.FindResource("BrTextSecondary") as IBrush ?? Brushes.Gray;

            // List view
            if (_lstGames != null)
            {
                for (int i = 0; i < _games.Count; i++)
                {
                    var container = _lstGames.ContainerFromIndex(i) as ListBoxItem;
                    if (container == null) continue;
                    var game = _games[i];

                    var hideIcon = container.GetVisualDescendants()
                        .OfType<TextBlock>().FirstOrDefault(x => x.Name == "TxtHideIcon");
                    if (hideIcon != null)
                    {
                        hideIcon.Text = game.IsHidden ? "\uE5F5" : "\uE5F2";
                        hideIcon.Foreground = game.IsHidden ? new SolidColorBrush(Color.Parse("#E05252")) : secondaryBrush;
                    }

                    container.Opacity = editMode && game.IsHidden ? 0.4 : 1.0;
                }
            }

            // Grid view
            if (_lstGamesGrid != null)
            {
                for (int i = 0; i < _games.Count; i++)
                {
                    var container = _lstGamesGrid.ContainerFromIndex(i) as ListBoxItem;
                    if (container == null) continue;
                    var game = _games[i];

                    var hideIcon = container.GetVisualDescendants()
                        .OfType<TextBlock>().FirstOrDefault(x => x.Name == "TxtHideIconGrid");
                    if (hideIcon != null)
                    {
                        hideIcon.Text = game.IsHidden ? "\uE5F5" : "\uE5F2";
                        hideIcon.Foreground = game.IsHidden ? new SolidColorBrush(Color.Parse("#E05252")) : secondaryBrush;
                    }

                    var hideLabel = container.GetVisualDescendants()
                        .OfType<TextBlock>().FirstOrDefault(x => x.Name == "TxtHideLabel");
                    if (hideLabel != null)
                    {
                        hideLabel.Text = game.IsHidden ? GetResourceString("TxtShowGame", "Show") : GetResourceString("TxtHideGame", "Hide");
                        hideLabel.Foreground = game.IsHidden ? warmBrush : secondaryBrush;
                    }

                    var overlayBorder = container.GetVisualDescendants()
                        .OfType<Border>().FirstOrDefault(x => x.Name == "GridEditOverlayBorder");
                    if (overlayBorder != null)
                        overlayBorder.Background = game.IsHidden
                            ? new SolidColorBrush(Color.Parse("#CC0B0E16"))
                            : new SolidColorBrush(Color.Parse("#660B0E16"));
                }
            }
        }

        private void BtnMoveUp_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is Game game)
            {
                int idx = _allGames.IndexOf(game);
                if (idx <= 0) return;
                _allGames.RemoveAt(idx);
                _allGames.Insert(idx - 1, game);
                ApplyFilter(_txtSearch?.Text);
                Dispatcher.UIThread.Post(() => ApplyEditModeToCards(true), DispatcherPriority.Loaded);
            }
        }

        private void BtnMoveDown_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is Game game)
            {
                int idx = _allGames.IndexOf(game);
                if (idx < 0 || idx >= _allGames.Count - 1) return;
                _allGames.RemoveAt(idx);
                _allGames.Insert(idx + 1, game);
                ApplyFilter(_txtSearch?.Text);
                Dispatcher.UIThread.Post(() => ApplyEditModeToCards(true), DispatcherPriority.Loaded);
            }
        }

        private void BtnToggleHide_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is Game game)
            {
                game.IsHidden = !game.IsHidden;
                Dispatcher.UIThread.Post(() => ApplyEditModeToCards(true), DispatcherPriority.Loaded);
            }
        }

        private void BtnToggleFavorite_Loaded(object? sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is Game game)
                UpdateFavoriteButton(button, game);
        }

        private void BtnToggleFavorite_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not Game game) return;

            game.IsFavorite = !game.IsFavorite;
            UpdateFavoriteButton(button, game);
            _persistenceService.SaveGames(_allGames);

            // If the "favorites only" filter is active, toggling a game off must remove it
            // from the currently displayed list right away.
            if (_componentService.Config.ShowOnlyFavorites)
                ApplyFilter(_txtSearch?.Text);
        }

        private void UpdateFavoriteButton(Button button, Game game)
        {
            // Content is a logical-tree value available immediately; GetVisualDescendants()
            // requires the ContentPresenter to have materialized the child into the visual
            // tree first, which hasn't happened yet at Loaded time - that's why the star
            // never painted on startup.
            if (button.Content is not TextBlock icon) return;

            var favoriteBrush = this.FindResource("BrAccentWarm") as IBrush ?? Brushes.Orange;
            var secondaryBrush = this.FindResource("BrTextSecondary") as IBrush ?? Brushes.Gray;

            icon.Text = game.IsFavorite ? "★" : "☆";
            icon.Foreground = game.IsFavorite ? favoriteBrush : secondaryBrush;
        }

        // ItemsControl.ContainerPrepared fires every time a container is handed an item -
        // both on first realization AND when a virtualizing ListBox recycles an existing
        // container for a different item (unlike Loaded, which only fires once). Kept as a
        // safety net for scroll-driven recycling on top of the Loaded-based paint.
        private void GameContainer_FavoriteIconPrepared(object? sender, ContainerPreparedEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _games.Count) return;
            if (e.Container is not Control container) return;

            var button = container.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(x => x.Name == "BtnToggleFavorite" || x.Name == "BtnToggleFavoriteGrid");
            if (button == null) return;

            UpdateFavoriteButton(button, _games[e.Index]);
        }

        private void ChkFilters_IsCheckedChanged(object? sender, RoutedEventArgs e)
        {
            _componentService.Config.HideGamesWithoutUpscaler = _chkHideNoUpscaler?.IsChecked == true;
            _componentService.Config.ShowOnlyInstalled = _chkOnlyInstalled?.IsChecked == true;
            _componentService.Config.ShowOnlyFavorites = _chkOnlyFavorites?.IsChecked == true;
            _componentService.SaveConfiguration();
            ApplyFilter(_txtSearch?.Text);
        }

        // ── Pointer-based drag ──────────────────────────────────────────────────

        private void DragHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!_isEditMode) return;
            if (sender is not Control handle || handle.DataContext is not Game game) return;
            if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;

            _draggedGame = game;
            _isDragging   = false;
            _currentDropIndex = -1;
            _dragCaptureControl = handle;

            _dragStartPos = e.GetPosition(_dragGhostCanvas);

            // Find the ListBoxItem container so we can read its bounds and compute offset
            _dragSourceContainer = handle.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
            if (_dragSourceContainer != null && _dragGhostCanvas != null)
            {
                var srcOrigin = _dragSourceContainer.TranslatePoint(new Point(0, 0), _dragGhostCanvas);
                if (srcOrigin.HasValue)
                    _ghostOffset = new Point(_dragStartPos.X - srcOrigin.Value.X,
                                             _dragStartPos.Y - srcOrigin.Value.Y);
            }

            e.Pointer.Capture(handle);
            handle.AddHandler(InputElement.PointerMovedEvent,   OnDragPointerMoved,    handledEventsToo: true);
            handle.AddHandler(InputElement.PointerReleasedEvent, OnDragPointerReleased, handledEventsToo: true);
            e.Handled = true;
        }

        private void OnDragPointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_isEditMode || _draggedGame == null || _dragGhostCanvas == null) return;

            var pos = e.GetPosition(_dragGhostCanvas);

            if (!_isDragging)
            {
                var d = pos - _dragStartPos;
                if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) return;
                _isDragging = true;
                StartDragVisuals();
            }

            // Move ghost to follow the pointer (offset preserves grab point)
            if (_dragGhost != null)
            {
                Canvas.SetLeft(_dragGhost, pos.X - _ghostOffset.X);
                Canvas.SetTop(_dragGhost,  pos.Y - _ghostOffset.Y);
            }

            UpdateDropIndicator(pos);
            e.Handled = true;
        }

        private async void OnDragPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            try
            {
                // Unsubscribe and release capture
                if (_dragCaptureControl != null)
                {
                    _dragCaptureControl.RemoveHandler(InputElement.PointerMovedEvent,   OnDragPointerMoved);
                    _dragCaptureControl.RemoveHandler(InputElement.PointerReleasedEvent, OnDragPointerReleased);
                    e.Pointer.Capture(null);
                }

                if (!_isDragging || _draggedGame == null)
                {
                    CleanupDragState();
                    return;
                }

                int srcIndex = _allGames.IndexOf(_draggedGame);
                int targetIndex = _currentDropIndex;
                var dragged = _draggedGame;

                // Fade out ghost
                if (_dragGhost != null)
                {
                    _dragGhost.Transitions = new Avalonia.Animation.Transitions
                    {
                        new Avalonia.Animation.DoubleTransition
                        {
                            Property = Visual.OpacityProperty,
                            Duration = TimeSpan.FromMilliseconds(140),
                            Easing   = new Avalonia.Animation.Easings.CubicEaseIn()
                        }
                    };
                    _dragGhost.Opacity = 0;
                    await Task.Delay(140);
                }

                CleanupDragState();

                // Execute reorder
                bool noOp = targetIndex < 0 || srcIndex < 0
                            || targetIndex == srcIndex || targetIndex == srcIndex + 1;
                if (!noOp)
                {
                    _allGames.RemoveAt(srcIndex);
                    int insertAt = Math.Clamp(targetIndex > srcIndex ? targetIndex - 1 : targetIndex,
                                              0, _allGames.Count);
                    _allGames.Insert(insertAt, dragged);
                }

                ApplyFilter(_txtSearch?.Text);
                Dispatcher.UIThread.Post(() => ApplyEditModeToCards(true), DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                CleanupDragState();
                DebugWindow.Log($"[MainWindow] Drag/drop failed: {ex.Message}");
            }
        }

        public bool IsEditMode => _isEditMode;
        public bool IsGamepadDragging => _isDragging && _draggedGame != null;

        public void GamepadBeginDrag(int itemIndex)
        {
            if (!_isEditMode || itemIndex < 0 || itemIndex >= _games.Count) return;

            var list = _isGridView ? _lstGamesGrid : _lstGames;
            if (list == null) return;

            var container = list.ContainerFromIndex(itemIndex) as ListBoxItem;
            if (container == null) return;

            if (_isGridView) MeasureGridLayout();
            else _itemLayoutHeight = container.Bounds.Height;

            _draggedGame = _allGames[itemIndex];
            _isDragging = true;
            _currentDropIndex = itemIndex;
            _originalGamepadDragIndex = itemIndex;
            _dragSourceContainer = container;

            container.Opacity = 0.4;
            container.ZIndex = 100;
        }

        public void GamepadMoveDrag(int deltaRow, int deltaCol)
        {
            if (!_isDragging || _draggedGame == null) return;

            int newIndex = _currentDropIndex;
            if (_isGridView)
            {
                int cols = _gridColCount > 0 ? _gridColCount : 1;
                int r = _currentDropIndex / cols;
                int c = _currentDropIndex % cols;

                c = Math.Clamp(c + deltaCol, 0, cols - 1);
                r += deltaRow;

                newIndex = Math.Clamp(r * cols + c, 0, _games.Count - 1);
            }
            else
            {
                newIndex = Math.Clamp(_currentDropIndex + deltaRow, 0, _games.Count - 1);
            }

            if (newIndex != _currentDropIndex)
            {
                _currentDropIndex = newIndex;

                var list = _isGridView ? _lstGamesGrid : _lstGames;
                if (list != null)
                {
                    if (_isGridView)
                        AnimateGridGap(_originalGamepadDragIndex, _currentDropIndex, true);
                    else
                        AnimateListGap(_originalGamepadDragIndex, _currentDropIndex, true);

                    list.ScrollIntoView(_currentDropIndex);
                }
            }
        }

        public void GamepadEndDrag(bool commit)
        {
            if (!_isDragging || _draggedGame == null) return;

            var gameToSave = _draggedGame;
            int finalIndex = _currentDropIndex;
            int originalIndex = _originalGamepadDragIndex;

            CleanupDragState();
            _originalGamepadDragIndex = -1;

            _gamepadHelper?.SuppressVisibilityJump(800);

            if (commit && originalIndex >= 0 && finalIndex != originalIndex)
            {
                int srcIdx = _allGames.IndexOf(gameToSave);
                if (srcIdx >= 0)
                {
                    _allGames.RemoveAt(srcIdx);
                    _allGames.Insert(finalIndex, gameToSave);
                    _games.Move(originalIndex, finalIndex);

                    var list = _isGridView ? _lstGamesGrid : _lstGames;
                    if (list != null)
                    {
                        list.SelectedIndex = finalIndex;
                        list.ScrollIntoView(finalIndex);

                        Dispatcher.UIThread.Post(() =>
                        {
                            var newContainer = list.ContainerFromIndex(finalIndex) as ListBoxItem;
                            newContainer?.Focus();
                        }, DispatcherPriority.Loaded);
                    }
                }
                ApplyFilter(_txtSearch?.Text);
                Dispatcher.UIThread.Post(() => ApplyEditModeToCards(true), DispatcherPriority.Loaded);
            }
            else
            {
                var list = _isGridView ? _lstGamesGrid : _lstGames;
                if (list != null && originalIndex >= 0)
                {
                    list.ScrollIntoView(originalIndex);
                    Dispatcher.UIThread.Post(() =>
                    {
                        var originalContainer = list.ContainerFromIndex(originalIndex) as ListBoxItem;
                        originalContainer?.Focus();
                    }, DispatcherPriority.Loaded);
                }
                if (commit)
                {
                    ApplyFilter(_txtSearch?.Text);
                    Dispatcher.UIThread.Post(() => ApplyEditModeToCards(true), DispatcherPriority.Loaded);
                }
            }
        }

        public void GamepadToggleHide(int itemIndex)
        {
            if (!_isEditMode || _isDragging || itemIndex < 0 || itemIndex >= _games.Count) return;
            var game = _games[itemIndex];
            game.IsHidden = !game.IsHidden;
            Dispatcher.UIThread.Post(() => ApplyEditModeToCards(true), DispatcherPriority.Loaded);
        }

        private void StartDragVisuals()
        {
            if (_dragGhostCanvas == null || _dragSourceContainer == null || _draggedGame == null) return;

            // Dim source item in place (keeps layout gap)
            _dragSourceContainer.Opacity = 0.12;

            // Create floating ghost
            _dragGhost = CreateDragGhost(_draggedGame, _dragSourceContainer);
            var srcOrigin = _dragSourceContainer.TranslatePoint(new Point(0, 0), _dragGhostCanvas);
            if (srcOrigin.HasValue)
            {
                Canvas.SetLeft(_dragGhost, srcOrigin.Value.X);
                Canvas.SetTop(_dragGhost,  srcOrigin.Value.Y);
            }
            _dragGhost.ZIndex = 20;
            _dragGhostCanvas.Children.Add(_dragGhost);

            // Insert line for list view only
            if (!_isGridView && _lstGames != null)
            {
                var accentBrush = this.FindResource("BrAccent") as IBrush ?? Brushes.MediumPurple;
                _dragInsertLine = new Border
                {
                    Height          = 3,
                    Width           = Math.Max(_lstGames.Bounds.Width - 24, 200),
                    Background      = accentBrush,
                    CornerRadius    = new CornerRadius(1.5),
                    IsHitTestVisible= false
                };
                Canvas.SetLeft(_dragInsertLine, 12);
                _dragInsertLine.ZIndex = 10;
                _dragGhostCanvas.Children.Add(_dragInsertLine);
            }

            // Snapshot item layout positions BEFORE any transforms are applied
            SnapshotItemPositions();
            if (_isGridView) MeasureGridLayout();
        }

        private Border CreateDragGhost(Game game, ListBoxItem sourceItem)
        {
            var accentBrush = this.FindResource("BrAccent") as IBrush ?? Brushes.MediumPurple;
            var textBrush   = this.FindResource("BrTextPrimary") as IBrush ?? Brushes.White;
            var secondaryBrush = this.FindResource("BrTextSecondary") as IBrush ?? Brushes.Gray;

            var ghost = new Border
            {
                Background      = new SolidColorBrush(Color.FromArgb(0xEC, 0x16, 0x18, 0x2C)),
                BorderBrush     = accentBrush,
                BorderThickness = new Thickness(1.5),
                CornerRadius    = new CornerRadius(12),
                IsHitTestVisible= false,
                Opacity         = 0.96,
                BoxShadow       = new BoxShadows(new BoxShadow
                {
                    Blur         = 28,
                    Color        = Color.FromArgb(120, 0, 0, 0),
                    OffsetY      = 10
                }),
                RenderTransform       = new ScaleTransform(1.04, 1.04),
                RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                Width   = sourceItem.Bounds.Width,
                Height  = sourceItem.Bounds.Height,
                Child   = new StackPanel
                {
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text                = "\uEEB9",  // game controller icon (FluentSystemIcons-Regular)
                            FontFamily          = new FontFamily("avares://OptiscalerClient/assets/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular"),
                            FontSize            = 28,
                            Foreground          = accentBrush,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                        },
                        new TextBlock
                        {
                            Text                = game.Name,
                            Foreground          = textBrush,
                            FontWeight          = FontWeight.SemiBold,
                            FontSize            = 15,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                            TextWrapping        = TextWrapping.Wrap,
                            MaxWidth            = Math.Max(sourceItem.Bounds.Width - 40, 80),
                            TextAlignment       = TextAlignment.Center
                        },
                        new TextBlock
                        {
                            Text                = game.Platform.ToString(),
                            Foreground          = secondaryBrush,
                            FontSize            = 11,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                        }
                    }
                }
            };
            return ghost;
        }

        private void UpdateDropIndicator(Point posInCanvas)
        {
            if (_dragGhostCanvas == null) return;

            if (_isGridView && _lstGamesGrid != null)
            {
                var origin = _lstGamesGrid.TranslatePoint(new Point(0, 0), _dragGhostCanvas);
                if (!origin.HasValue) return;
                var posInGrid = new Point(posInCanvas.X - origin.Value.X, posInCanvas.Y - origin.Value.Y);
                _currentDropIndex = GetDropIndexFromGridGeometry(posInGrid);

                if (_currentDropIndex != _lastAnimatedDropIndex)
                {
                    _lastAnimatedDropIndex = _currentDropIndex;
                    int srcIdx = _allGames.IndexOf(_draggedGame!);
                    AnimateGridGap(srcIdx, _currentDropIndex);
                }
            }
            else if (_lstGames != null && _itemLayoutTops.Count > 0)
            {
                // Compute drop index from SNAPSHOT positions to avoid transform feedback loop
                int newDrop = _games.Count;
                for (int i = 0; i < _games.Count; i++)
                {
                    if (i >= _itemLayoutTops.Count || double.IsNaN(_itemLayoutTops[i])) continue;
                    if (posInCanvas.Y < _itemLayoutTops[i] + _itemLayoutHeight / 2) { newDrop = i; break; }
                }
                _currentDropIndex = newDrop;

                // Only re-animate when the target slot actually changes
                if (_currentDropIndex != _lastAnimatedDropIndex)
                {
                    _lastAnimatedDropIndex = _currentDropIndex;
                    int srcIdx = _allGames.IndexOf(_draggedGame!);
                    AnimateListGap(srcIdx, _currentDropIndex);
                    PositionInsertLine(_currentDropIndex);
                }
            }
        }

        private void PositionInsertLine(int insertIndex)
        {
            if (_dragInsertLine == null || _itemLayoutTops.Count == 0) return;
            int count = _games.Count;
            if (count == 0) return;

            int clamped = Math.Clamp(insertIndex, 0, count);
            double lineY;

            if (clamped == 0 && !double.IsNaN(_itemLayoutTops[0]))
                lineY = _itemLayoutTops[0];
            else if (clamped >= count && !double.IsNaN(_itemLayoutTops[count - 1]))
                lineY = _itemLayoutTops[count - 1] + _itemLayoutHeight;
            else if (clamped > 0 && clamped < count
                     && !double.IsNaN(_itemLayoutTops[clamped - 1])
                     && !double.IsNaN(_itemLayoutTops[clamped]))
                lineY = (_itemLayoutTops[clamped - 1] + _itemLayoutHeight + _itemLayoutTops[clamped]) / 2.0;
            else
                return;

            Canvas.SetTop(_dragInsertLine, lineY - 1.5);
        }

        private void SnapshotItemPositions()
        {
            _itemLayoutTops.Clear();
            _itemLayoutHeight = _dragSourceContainer?.Bounds.Height ?? 160;
            if (_lstGames == null || _dragGhostCanvas == null || _isGridView) return;

            for (int i = 0; i < _games.Count; i++)
            {
                var c = _lstGames.ContainerFromIndex(i) as ListBoxItem;
                var p = c?.TranslatePoint(new Point(0, 0), _dragGhostCanvas);
                _itemLayoutTops.Add(p.HasValue ? p.Value.Y : double.NaN);
            }
        }

        private void MeasureGridLayout()
        {
            _gridColCount = 0;
            _gridHGap = 0;
            _gridVGap = 0;
            if (_lstGamesGrid == null || _games.Count < 1) return;

            var c0 = _lstGamesGrid.ContainerFromIndex(0) as ListBoxItem;
            if (c0 == null) return;

            _gridItemWidth  = c0.Bounds.Width;
            _gridItemHeight = c0.Bounds.Height;

            var p0 = c0.TranslatePoint(new Point(0, 0), _lstGamesGrid);
            if (!p0.HasValue) return;
            _gridOriginX = p0.Value.X;
            _gridOriginY = p0.Value.Y;

            // Count items on the first row
            for (int i = 0; i < _games.Count; i++)
            {
                var ci = _lstGamesGrid.ContainerFromIndex(i) as ListBoxItem;
                if (ci == null) continue;
                var pi = ci.TranslatePoint(new Point(0, 0), _lstGamesGrid);
                if (!pi.HasValue) continue;
                if (Math.Abs(pi.Value.Y - p0.Value.Y) > 10) break;
                _gridColCount++;
            }
            if (_gridColCount == 0) _gridColCount = 1;

            // Horizontal gap from item 0 → 1
            if (_gridColCount > 1)
            {
                var c1 = _lstGamesGrid.ContainerFromIndex(1) as ListBoxItem;
                var p1 = c1?.TranslatePoint(new Point(0, 0), _lstGamesGrid);
                if (p1.HasValue)
                    _gridHGap = p1.Value.X - (p0.Value.X + _gridItemWidth);
            }

            // Vertical gap from row 0 → 1
            if (_games.Count > _gridColCount)
            {
                var cNext = _lstGamesGrid.ContainerFromIndex(_gridColCount) as ListBoxItem;
                var pNext = cNext?.TranslatePoint(new Point(0, 0), _lstGamesGrid);
                if (pNext.HasValue)
                    _gridVGap = pNext.Value.Y - (p0.Value.Y + _gridItemHeight);
            }
        }

        private void AnimateGridGap(int srcIndex, int dropIndex, bool animateSource = false)
        {
            if (_lstGamesGrid == null || _gridColCount == 0) return;
            int n = _games.Count;
            double cellW = _gridItemWidth  + _gridHGap;
            double cellH = _gridItemHeight + _gridVGap;
            var ic = System.Globalization.CultureInfo.InvariantCulture;

            for (int i = 0; i < n; i++)
            {
                var container = _lstGamesGrid.ContainerFromIndex(i) as ListBoxItem;
                if (container == null) continue;
                if (!animateSource && i == srcIndex) continue;

                // Virtual index: where this item ends up if src is removed and re-inserted at dropIndex
                int vIdx;
                if (i == srcIndex)
                {
                    vIdx = dropIndex;
                }
                else if (dropIndex <= srcIndex)
                {
                    if      (i < dropIndex)  vIdx = i;     // before gap: stay
                    else if (i < srcIndex)   vIdx = i + 1; // between drop and src: shift right
                    else                     vIdx = i;     // after src: stay
                }
                else
                {
                    if      (i > srcIndex && i <= dropIndex) vIdx = i - 1; // between src and drop: shift left
                    else                                     vIdx = i;     // outside range: stay
                }

                // Natural grid position (based on original index)
                double natX = (i % _gridColCount) * cellW;
                double natY = (i / _gridColCount) * cellH;

                // Target grid position (based on virtual index)
                double tgtX = (vIdx % _gridColCount) * cellW;
                double tgtY = (vIdx / _gridColCount) * cellH;

                double dx = tgtX - natX;
                double dy = tgtY - natY;

                EnsureTransformTransition(container);
                if (animateSource && i == srcIndex) container.ZIndex = 100;

                if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)
                    container.RenderTransform = TransformOperations.Parse("translateX(0px) translateY(0px)");
                else
                    container.RenderTransform = TransformOperations.Parse(
                        $"translateX({dx.ToString("F1", ic)}px) translateY({dy.ToString("F1", ic)}px)");
            }
        }

        private void ResetGridItemTransforms()
        {
            if (_lstGamesGrid == null || _gridColCount == 0) return;
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            for (int i = 0; i < _games.Count; i++)
            {
                var container = _lstGamesGrid.ContainerFromIndex(i) as ListBoxItem;
                if (container?.RenderTransform == null) continue;
                EnsureTransformTransition(container);
                container.RenderTransform = TransformOperations.Parse("translateX(0px) translateY(0px)");
                container.ZIndex = 0;
            }
            _gridColCount = 0;
        }

        private void AnimateListGap(int srcIndex, int dropIndex, bool animateSource = false)
        {
            if (_lstGames == null) return;
            double gap = _itemLayoutHeight + 8;
            var ic = System.Globalization.CultureInfo.InvariantCulture;

            for (int i = 0; i < _games.Count; i++)
            {
                var container = _lstGames.ContainerFromIndex(i) as ListBoxItem;
                if (container == null) continue;
                if (!animateSource && i == srcIndex) continue;

                double shift = 0;
                if (i == srcIndex)
                {
                    shift = (dropIndex - srcIndex) * gap;
                }
                else if (dropIndex <= srcIndex)
                {
                    shift = (i >= dropIndex && i < srcIndex) ? gap : 0;
                }
                else
                {
                    shift = (i > srcIndex && i <= dropIndex) ? -gap : 0;
                }

                EnsureTransformTransition(container);
                if (animateSource && i == srcIndex) container.ZIndex = 100;

                if (Math.Abs(shift) < 0.5)
                    container.RenderTransform = TransformOperations.Parse("translateY(0px)");
                else
                    container.RenderTransform = TransformOperations.Parse($"translateY({shift.ToString("F1", ic)}px)");
            }
        }

        private static void EnsureTransformTransition(Control ctrl)
        {
            if (ctrl.Transitions != null &&
                ctrl.Transitions.Any(t => t is Avalonia.Animation.TransformOperationsTransition tot
                                       && Equals(tot.Property, Visual.RenderTransformProperty)))
                return;
            ctrl.Transitions ??= new Avalonia.Animation.Transitions();
            ctrl.Transitions.Add(new Avalonia.Animation.TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = TimeSpan.FromMilliseconds(130),
                Easing   = new Avalonia.Animation.Easings.CubicEaseOut()
            });
        }

        private void ResetItemTransforms()
        {
            if (_lstGames == null || _itemLayoutTops.Count == 0) return;
            for (int i = 0; i < _games.Count; i++)
            {
                var container = _lstGames.ContainerFromIndex(i) as ListBoxItem;
                if (container?.RenderTransform == null) continue;
                container.RenderTransform = TransformOperations.Parse("translateY(0px)");
                container.ZIndex = 0;
            }
            _lastAnimatedDropIndex = -2;
            _itemLayoutTops.Clear();
        }

        private void CleanupDragState()
        {
            ResetItemTransforms();
            ResetGridItemTransforms();
            if (_dragGhostCanvas != null)
            {
                if (_dragGhost      != null) { _dragGhostCanvas.Children.Remove(_dragGhost);      _dragGhost      = null; }
                if (_dragInsertLine != null) { _dragGhostCanvas.Children.Remove(_dragInsertLine); _dragInsertLine = null; }
            }
            if (_dragSourceContainer != null) { _dragSourceContainer.Opacity = 1.0; _dragSourceContainer = null; }
            _isDragging         = false;
            _draggedGame        = null;
            _dragCaptureControl = null;
            _currentDropIndex   = -1;
        }

        private int GetDropIndex(ListBox lb, Point posInLb)
        {
            for (int i = 0; i < _games.Count; i++)
            {
                var container = lb.ContainerFromIndex(i) as ListBoxItem;
                if (container == null) continue;
                var topLeft = container.TranslatePoint(new Point(0, 0), lb);
                if (!topLeft.HasValue) continue;
                if (posInLb.Y < topLeft.Value.Y + container.Bounds.Height / 2)
                    return i;
            }
            return _games.Count;
        }

        // Compute drop index from measured grid geometry (immune to RenderTransform feedback loop).
        private int GetDropIndexFromGridGeometry(Point posInGrid)
        {
            if (_gridColCount == 0) return _games.Count;
            double cellW = _gridItemWidth  + _gridHGap;
            double cellH = _gridItemHeight + _gridVGap;

            double relX = posInGrid.X - _gridOriginX;
            double relY = posInGrid.Y - _gridOriginY;

            int col = Math.Clamp((int)(relX / cellW), 0, _gridColCount - 1);
            int row = Math.Max(0, (int)(relY / cellH));

            // Within the cell, left or right of the item's horizontal centre?
            double localX = relX - col * cellW;
            bool rightHalf = localX > _gridItemWidth / 2.0;

            int idx = row * _gridColCount + col;
            if (rightHalf) idx++;

            return Math.Clamp(idx, 0, _games.Count);
        }

        private async void BtnGuide_Click2(object? sender, RoutedEventArgs e)
        {
            try
            {
                var guide = new GuideWindow(this);
                await guide.ShowDialog(this);
            }
            catch (Exception ex) { DebugWindow.Log($"[MainWindow] Guide dialog failed: {ex.Message}"); }
        }

        private static readonly string[] _viewNames = { "ViewGames", "ViewProfiles", "ViewProfileEditor", "ViewSettings", "ViewHelp" };

        private void SwitchToView(string viewName)
        {
            if (viewName != "ViewProfileEditor")
            {
                IsProfileEditorOpen = false;
            }
            else
            {
                IsProfileEditorOpen = true;
            }

            foreach (var name in _viewNames)
            {
                var grid = this.FindControl<Grid>(name);
                if (grid == null) continue;
                bool isActive = name == viewName;
                grid.Opacity = isActive ? 1.0 : 0.0;
                grid.IsHitTestVisible = isActive;
            }
        }

        private void NavGames_Click(object sender, RoutedEventArgs e)
        {
            SwitchToView("ViewGames");
        }

        private void NavProfiles_Click(object sender, RoutedEventArgs e)
        {
            SwitchToView("ViewProfiles");
            LoadProfilesView();
        }

        public void NavigateToProfiles()
        {
            var nav = this.FindControl<RadioButton>("NavProfiles");
            if (nav != null) nav.IsChecked = true;
            SwitchToView("ViewProfiles");
            LoadProfilesView();
        }

        private void NavHelp_Click(object sender, RoutedEventArgs e)
        {
            SwitchToView("ViewHelp");
            PopulateHelpContent();
        }

        private void NavSettings_Click(object sender, RoutedEventArgs e)
        {
            SwitchToView("ViewSettings");

            _isInitializingLanguage = true;
            var cmbLanguage = this.FindControl<ComboBox>("CmbLanguage");
            if (cmbLanguage != null)
            {
                foreach (var baseItem in cmbLanguage.Items)
                {
                    if (baseItem is ComboBoxItem item && item.Tag?.ToString() == App.CurrentLanguage)
                    {
                        cmbLanguage.SelectedItem = item;
                        break;
                    }
                }
            }
            var tglAutoScan = this.FindControl<ToggleSwitch>("TglAutoScan");
            if (tglAutoScan != null)
            {
                tglAutoScan.IsChecked = _componentService.Config.AutoScan;
            }
            var tglAnimations = this.FindControl<ToggleSwitch>("TglAnimations");
            if (tglAnimations != null)
            {
                tglAnimations.IsChecked = _componentService.Config.AnimationsEnabled;
            }
            var txtSteamGridApiKey = this.FindControl<TextBox>("TxtSteamGridApiKey");
            if (txtSteamGridApiKey != null)
            {
                txtSteamGridApiKey.Text = _componentService.Config.SteamGridDBApiKey ?? string.Empty;
            }

            PopulateDefaultGpuComboBox();
            RepopulateVersionCombos();
        }

        private void RepopulateVersionCombos()
        {
            // Version selectors are now managed via ManageDefaultVersionsWindow.
            // Nothing to repopulate in the config tab.
        }

        private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingLanguage) return;
            var cmbLanguage = sender as ComboBox;
            if (cmbLanguage?.SelectedItem is ComboBoxItem selectedItem)
            {
                string? langCode = selectedItem.Tag?.ToString();
                if (!string.IsNullOrEmpty(langCode))
                {
                    App.ChangeLanguage(langCode);
                    _componentService.Config.Language = langCode;
                    _componentService.SaveConfiguration();
                }
            }
        }

        private async void BtnManageCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cacheWindow = new CacheManagementWindow(this);
                await cacheWindow.ShowDialog<object>(this);
            }
            catch (Exception ex) { DebugWindow.Log($"[MainWindow] Cache management dialog failed: {ex.Message}"); }
        }

        private async void BtnClearAppCache_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ConfirmDialog(
                this,
                GetResourceString("TxtClearAppCacheTitle", "Clear Application Cache"),
                GetResourceString("TxtClearAppCacheDialogMsg", "Warning: This will permanently delete all scanned games, cover art and cached OptiScaler version data.\n\nThe application will close after clearing. On the next launch it will re-scan your library and re-download version information."));

            var confirmed = await dialog.ShowDialog<bool>(this);
            if (!confirmed) return;

            try
            {
                var baseDir = AppPaths.GetAppDataRoot();

                string[] filesToDelete =
                [
                    System.IO.Path.Combine(baseDir, "games.json"),
                    System.IO.Path.Combine(baseDir, "extras_cache.json"),
                    System.IO.Path.Combine(baseDir, "releases_cache.json"),
                    System.IO.Path.Combine(baseDir, "versions.json"),
                    System.IO.Path.Combine(baseDir, "analysis_cache.json"),
                    System.IO.Path.Combine(baseDir, "config.json"),
                ];

                string[] dirsToDelete =
                [
                    System.IO.Path.Combine(baseDir, "Covers"),
                    System.IO.Path.Combine(baseDir, "Cache"),
                ];

                foreach (var file in filesToDelete)
                {
                    if (File.Exists(file))
                        File.Delete(file);
                }

                foreach (var dir in dirsToDelete)
                {
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, recursive: true);
                }

                Close();
            }
            catch (Exception ex)
            {
                await new ConfirmDialog(this, "Error", $"Failed to clear cache: {ex.Message}", isAlert: true).ShowDialog<object>(this);
            }
        }

        private async void BtnManageScanSources_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new ManageScanSourcesWindow(this, _componentService);
                await dialog.ShowDialog<bool?>(this);
            }
            catch (Exception ex) { DebugWindow.Log($"[MainWindow] Scan sources dialog failed: {ex.Message}"); }
        }

        // ── Profiles View ─────────────────────────────────────────────────────────

        private OptiScalerProfile? _selectedProfileView;
        private string _profileSearchTextView = string.Empty;
        private readonly ProfileManagementService _profileService = new ProfileManagementService();
        public bool IsProfileEditorOpen { get; private set; }

        private void LoadProfilesView(bool forceRefresh = true)
        {
            var pnl = this.FindControl<StackPanel>("PnlProfilesView");
            if (pnl == null) return;
            pnl.Children.Clear();

            var defaultName = _componentService.Config.DefaultProfileName;
            if (string.IsNullOrWhiteSpace(defaultName))
                defaultName = OptiScalerProfile.BuiltInDefaultName;

            var allProfiles = _profileService.GetAllProfiles(forceRefresh);
            var customCount = allProfiles.Count(p => !p.IsBuiltIn);

            var filtered = allProfiles.Where(p =>
                string.IsNullOrWhiteSpace(_profileSearchTextView)
                || p.Name.Contains(_profileSearchTextView, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(p.Description) && p.Description.Contains(_profileSearchTextView, StringComparison.OrdinalIgnoreCase))
            ).ToList();

            var txtInfo = this.FindControl<TextBlock>("TxtProfileInfoView");
            if (txtInfo != null)
            {
                txtInfo.Text = string.IsNullOrWhiteSpace(_profileSearchTextView)
                    ? string.Format(GetResourceString("TxtProfInfoFormat", "{0} profile(s) ({1} custom)."), allProfiles.Count, customCount)
                    : string.Format(GetResourceString("TxtProfSearchResultFormat", "{0} result(s) of {1}."), filtered.Count, allProfiles.Count);
            }

            if (!filtered.Any())
            {
                pnl.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(_profileSearchTextView) ? GetResourceString("TxtProfNotFound", "No profiles found.") : GetResourceString("TxtProfSearchNotFound", "No matching profiles found."),
                    Foreground = Brushes.Gray,
                    FontSize = 11,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    Margin = new Thickness(0, 10)
                });
                _selectedProfileView = null;
            }
            else
            {
                foreach (var profile in filtered)
                    pnl.Children.Add(CreateProfileCardView(profile, defaultName));
            }

            // Restore selection
            if (_selectedProfileView != null)
                _selectedProfileView = filtered.FirstOrDefault(p => p.Name.Equals(_selectedProfileView.Name, StringComparison.OrdinalIgnoreCase));

            UpdateProfileViewButtons(defaultName);
            HighlightProfileCardView();
        }

        private Border CreateProfileCardView(OptiScalerProfile profile, string defaultName)
        {
            var titleRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
            titleRow.Children.Add(new TextBlock
            {
                Text = profile.Name,
                FontWeight = FontWeight.Bold,
                Foreground = Application.Current?.FindResource("BrTextPrimary") as IBrush ?? Brushes.White
            });

            if (profile.Name.Equals(defaultName, StringComparison.OrdinalIgnoreCase))
            {
                titleRow.Children.Add(new Border
                {
                    Background = Application.Current?.FindResource("BrBgElevated") as IBrush ?? Brushes.Transparent,
                    BorderBrush = Application.Current?.FindResource("BrBorderSubtle") as IBrush ?? Brushes.DimGray,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6, 2),
                    Child = new TextBlock
                    {
                        Text = GetResourceString("TxtDefaultBadge", "Default"),
                        FontSize = 9,
                        Foreground = Application.Current?.FindResource("BrTextSecondary") as IBrush ?? Brushes.Gray
                    }
                });
            }

            var stack = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            stack.Children.Add(titleRow);
            if (!string.IsNullOrWhiteSpace(profile.Description))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = profile.Description,
                    FontSize = 10,
                    Foreground = Application.Current?.FindResource("BrTextSecondary") as IBrush ?? Brushes.Gray,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            var border = new Border
            {
                Background = Application.Current?.FindResource("BrBgCard") as IBrush ?? Brushes.Transparent,
                BorderBrush = Application.Current?.FindResource("BrBorderSubtle") as IBrush ?? Brushes.DimGray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 10),
                Child = stack,
                Tag = profile,
                Cursor = new Cursor(StandardCursorType.Hand),
                Focusable = true
            };

            void SelectCard(object? s)
            {
                if (s is Border b && b.Tag is OptiScalerProfile p)
                {
                    _selectedProfileView = p;
                    var def = _componentService.Config.DefaultProfileName ?? OptiScalerProfile.BuiltInDefaultName;
                    UpdateProfileViewButtons(def);
                    HighlightProfileCardView();
                }
            }

            border.PointerPressed += (s, e) => SelectCard(s);
            border.KeyUp += (s, e) =>
            {
                if (e.Key == Key.Enter)
                    SelectCard(s);
            };
            border.GotFocus += (s, e) =>
            {
                if (s is Border b)
                    b.BorderBrush = Application.Current?.FindResource("BrAccent") as IBrush ?? Brushes.White;
            };
            border.LostFocus += (s, e) => HighlightProfileCardView();

            return border;
        }

        private void HighlightProfileCardView()
        {
            var pnl = this.FindControl<StackPanel>("PnlProfilesView");
            if (pnl == null) return;
            foreach (var child in pnl.Children)
            {
                if (child is Border b)
                {
                    var selected = b.Tag == _selectedProfileView;
                    b.Background = selected
                        ? Application.Current?.FindResource("BrBgElevated") as IBrush ?? Brushes.Transparent
                        : Application.Current?.FindResource("BrBgCard") as IBrush ?? Brushes.Transparent;
                    b.BorderThickness = selected ? new Thickness(2) : new Thickness(1);
                }
            }
        }

        private void UpdateProfileViewButtons(string defaultName)
        {
            var btnEdit = this.FindControl<Button>("BtnEditProfileView");
            var btnDup = this.FindControl<Button>("BtnDuplicateProfileView");
            var btnDel = this.FindControl<Button>("BtnDeleteProfileView");
            var btnDef = this.FindControl<Button>("BtnSetDefaultView");
            var btnExp = this.FindControl<Button>("BtnExportProfileView");

            bool hasSelection = _selectedProfileView != null;
            if (btnEdit != null) btnEdit.IsEnabled = hasSelection && !(_selectedProfileView?.IsBuiltIn ?? true);
            if (btnDup != null) btnDup.IsEnabled = hasSelection;
            if (btnDel != null) btnDel.IsEnabled = hasSelection && !(_selectedProfileView?.IsBuiltIn ?? true);
            if (btnDef != null) btnDef.IsEnabled = hasSelection && !(_selectedProfileView?.Name.Equals(defaultName, StringComparison.OrdinalIgnoreCase) ?? false);
            if (btnExp != null) btnExp.IsEnabled = hasSelection;
        }

        private async void BtnExportProfileView_Click(object? sender, RoutedEventArgs e)
        {
            if (_selectedProfileView == null) return;
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export profile as OptiScaler.ini",
                SuggestedFileName = "OptiScaler.ini",
                DefaultExtension = "ini",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("INI files") { Patterns = new[] { "*.ini" } }
                }
            });

            if (file == null) return;
            try
            {
                var templatePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "OptiScaler_example.ini");
                var iniContent = _profileService.GenerateOptiScalerIni(_selectedProfileView, templatePath);
                await using var stream = await file.OpenWriteAsync();
                await using var writer = new System.IO.StreamWriter(stream);
                await writer.WriteAsync(iniContent);
            }
            catch (Exception ex)
            {
                await new ConfirmDialog(this, "Export Error", $"Failed to export profile: {ex.Message}").ShowDialog<object>(this);
            }
        }

        private void TxtProfileSearchView_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                _profileSearchTextView = tb.Text?.Trim() ?? string.Empty;
                LoadProfilesView(forceRefresh: false);
            }
        }

        private void BtnNewProfileView_Click(object? sender, RoutedEventArgs e)
        {
            var newProfile = OptiScalerProfile.CreateEmpty();
            OpenProfileEditor(newProfile, isNewProfile: true);
        }

        private async void BtnImportProfileView_Click(object? sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import OptiScaler .ini",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("INI files") { Patterns = new[] { "*.ini" } },
                    new FilePickerFileType("All files")  { Patterns = new[] { "*.*" } }
                }
            });

            if (files.Count == 0) return;

            var path = files[0].Path.LocalPath;
            Dictionary<string, Dictionary<string, string>> iniSettings;
            try
            {
                iniSettings = ParseIniFile(path);
            }
            catch (Exception ex)
            {
                await new ConfirmDialog(this, "Import Failed", $"Could not read the file:\n{ex.Message}", isAlert: true).ShowDialog<object>(this);
                return;
            }

            if (iniSettings.Count == 0)
            {
                await new ConfirmDialog(this, "Import Failed", "The selected file contains no recognisable sections.", isAlert: true).ShowDialog<object>(this);
                return;
            }

            // Pre-populate a new profile and let the user set name/description in the editor
            var profile = OptiScalerProfile.CreateEmpty();
            profile.Name = System.IO.Path.GetFileNameWithoutExtension(path);
            profile.Description = $"Imported from {System.IO.Path.GetFileName(path)}";
            profile.IniSettings = iniSettings;
            OpenProfileEditor(profile, isNewProfile: true);
        }

        /// <summary>
        /// Minimal INI parser: ignores comment lines (;) and blank lines,
        /// collects [Section] → key=value pairs, skips "=auto" values.
        /// </summary>
        private static Dictionary<string, Dictionary<string, string>> ParseIniFile(string path)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var currentSection = string.Empty;

            foreach (var rawLine in System.IO.File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith(';') || line.StartsWith('#'))
                    continue;

                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    currentSection = line[1..^1].Trim();
                    if (!result.ContainsKey(currentSection))
                        result[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    continue;
                }

                if (string.IsNullOrEmpty(currentSection)) continue;

                var eqIdx = line.IndexOf('=');
                if (eqIdx <= 0) continue;

                var key   = line[..eqIdx].Trim();
                var value = line[(eqIdx + 1)..].Trim();

                // Strip inline comments
                var commentIdx = value.IndexOf(';');
                if (commentIdx >= 0) value = value[..commentIdx].Trim();

                if (string.IsNullOrEmpty(key)) continue;

                // Only store non-default values
                if (!string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
                    result[currentSection][key] = value;
            }

            // Remove empty sections
            foreach (var key in new List<string>(result.Keys))
                if (result[key].Count == 0) result.Remove(key);

            return result;
        }

        private void BtnSetDefaultView_Click(object? sender, RoutedEventArgs e)
        {
            if (_selectedProfileView == null) return;
            _componentService.Config.DefaultProfileName = _selectedProfileView.Name;
            _componentService.SaveConfiguration();
            LoadProfilesView();
        }

        private void BtnEditProfileView_Click(object? sender, RoutedEventArgs e)
        {
            if (_selectedProfileView == null) return;
            var wasDefault = _selectedProfileView.Name.Equals(_componentService.Config.DefaultProfileName, StringComparison.OrdinalIgnoreCase);
            OpenProfileEditor(_selectedProfileView, isNewProfile: false, wasDefault: wasDefault);
        }

        private void BtnDuplicateProfileView_Click(object? sender, RoutedEventArgs e)
        {
            if (_selectedProfileView == null) return;
            var dup = _selectedProfileView.Clone();
            dup.Name = $"{_selectedProfileView.Name} (Copy)";
            dup.IsBuiltIn = false;
            OpenProfileEditor(dup, isNewProfile: true);
        }

        private async void BtnDeleteProfileView_Click(object? sender, RoutedEventArgs e)
        {
            if (_selectedProfileView == null) return;
            var dialog = new ConfirmDialog(this, "Delete Profile",
                $"Are you sure you want to delete '{_selectedProfileView.Name}'?", false);
            var result = await dialog.ShowDialog<bool>(this);
            if (!result) return;
            try
            {
                var wasDefault = _selectedProfileView.Name.Equals(_componentService.Config.DefaultProfileName, StringComparison.OrdinalIgnoreCase);
                _profileService.DeleteProfile(_selectedProfileView);
                if (wasDefault)
                {
                    _componentService.Config.DefaultProfileName = OptiScalerProfile.BuiltInDefaultName;
                    _componentService.SaveConfiguration();
                }
                _selectedProfileView = null;
                LoadProfilesView();
            }
            catch (Exception ex)
            {
                await new ConfirmDialog(this, "Error", $"Failed to delete profile: {ex.Message}").ShowDialog<object>(this);
            }
        }

        // ── Profile Editor Inline View ────────────────────────────────────────────

        private OptiScalerProfile? _editorProfile;
        private bool _editorIsNewProfile;
        private bool _editorWasDefault;
        private Dictionary<string, Dictionary<string, SettingControlRef>> _editorSettingControls = new();
        private SettingsSchema? _editorSchema;
        private LayoutSettings _editorLayout = new();
        private WrapPanel? _editorSectionsWrap;
        private Button? _editorKeyCaptureButton;
        private string? _editorKeyCapturePreviousValue;
        private string _editorSearchText = string.Empty;
        private StackPanel? _editorSidebarNav;
        private Dictionary<string, Border> _editorSectionBorders = new();
        private bool _editorIsEasyMode = true;

        private void OpenProfileEditor(OptiScalerProfile profile, bool isNewProfile, bool wasDefault = false)
        {
            IsProfileEditorOpen = true;
            _editorProfile = profile;
            _editorIsNewProfile = isNewProfile;
            _editorWasDefault = wasDefault;
            _editorSettingControls = new();
            _editorSchema = null;
            _editorSearchText = string.Empty;
            _editorIsEasyMode = true;

            var titleBlock = this.FindControl<TextBlock>("TxtEditorTitle");
            if (titleBlock != null)
                titleBlock.Text = isNewProfile
                    ? GetResourceString("TxtEditorNewTitle", "New Profile")
                    : string.Format(GetResourceString("TxtEditorEditTitleFmt", "Edit: {0}"), profile.Name);

            var txtName = this.FindControl<TextBox>("TxtProfileNameEd");
            if (txtName != null)
            {
                txtName.Text = profile.Name;
                txtName.IsReadOnly = profile.IsBuiltIn;
            }

            var txtDesc = this.FindControl<TextBox>("TxtDescriptionEd");
            if (txtDesc != null) txtDesc.Text = profile.Description;

            var txtSearch = this.FindControl<TextBox>("TxtSettingsSearchEd");
            if (txtSearch != null) txtSearch.Text = string.Empty;

            UpdateEditorModeButtons();
            EditorSyncSchemaValues(fromEasyToAdvanced: false);
            BuildEditorSettingsUI();
            SwitchToView("ViewProfileEditor");

            // Explicitly focus the first item so the Gamepad has an immediate anchor
            txtName?.Focus();
        }

        private void BuildEditorSettingsUI()
        {
            var sectionsWrap = this.FindControl<WrapPanel>("SectionsWrapEd");
            var sidebarNav = this.FindControl<StackPanel>("SidebarNavEd");
            if (sectionsWrap == null || sidebarNav == null || _editorProfile == null) return;

            _editorSectionsWrap = sectionsWrap;
            _editorSidebarNav = sidebarNav;
            sectionsWrap.Children.Clear();

            while (sidebarNav.Children.Count > 1)
                sidebarNav.Children.RemoveAt(1);

            _editorSectionBorders.Clear();

            string schemaFileName = _editorIsEasyMode ? "easy_profile_editor_schema.json" : "profile_editor_schema.json";
            string schemaPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "configs", schemaFileName);
            if (!File.Exists(schemaPath))
            {
                sectionsWrap.Children.Add(new TextBlock { Text = $"Error: Settings schema file not found: {schemaFileName}" });
                return;
            }

            try
            {
                var json = File.ReadAllText(schemaPath);
                _editorSchema = JsonSerializer.Deserialize<SettingsSchema>(json);
            }
            catch (Exception ex)
            {
                sectionsWrap.Children.Add(new TextBlock { Text = $"Error parsing schema: {ex.Message}" });
                return;
            }

            if (_editorSchema?.Sections == null) return;
            _editorLayout = _editorSchema.Layout ?? new LayoutSettings();

            foreach (var section in _editorSchema.Sections)
            {
                var sectionName = section.Name;
                if (string.IsNullOrEmpty(sectionName)) continue;

                if (!_editorSettingControls.ContainsKey(sectionName))
                    _editorSettingControls[sectionName] = new Dictionary<string, SettingControlRef>();

                if (!_editorProfile.IniSettings.ContainsKey(sectionName))
                    _editorProfile.IniSettings[sectionName] = new Dictionary<string, string>();

                var sectionCard = BuildEditorSectionCard(sectionName, section);
                if (sectionCard != null)
                {
                    sectionsWrap.Children.Add(sectionCard);
                    _editorSectionBorders[sectionName] = sectionCard;

                    var navButton = new Button
                    {
                        Content = sectionName,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                        Padding = new Thickness(12, 8),
                        FontSize = 12,
                        Tag = sectionName,
                        Margin = new Thickness(0, 0, 8, 0)
                    };
                    navButton.Classes.Add("BtnSecondary");
                    navButton.Click += EditorNavButton_Click;
                    sidebarNav.Children.Add(navButton);
                }
            }

            UpdateEditorWrapLayout();
        }

        private Border BuildEditorSectionCard(string sectionName, SchemaSection section)
        {
            var cardContent = new StackPanel { Spacing = 10 };
            cardContent.Children.Add(new TextBlock
            {
                Text = sectionName,
                FontSize = 15,
                FontWeight = Avalonia.Media.FontWeight.Bold,
                Foreground = Application.Current?.FindResource("BrTextPrimary") as Avalonia.Media.IBrush ?? Avalonia.Media.Brushes.White
            });

            var columns = Math.Max(1, section.Columns);
            var rows = Math.Max(1, section.Rows);
            var sectionGrid = new Grid();
            for (int i = 0; i < columns; i++)
                sectionGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            for (int i = 0; i < rows; i++)
                sectionGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var visibleCount = 0;
            if (section.Settings != null)
            {
                foreach (var setting in section.Settings)
                {
                    if (string.IsNullOrEmpty(setting.Key)) continue;
                    if (!string.IsNullOrWhiteSpace(_editorSearchText))
                    {
                        var labelL = (setting.Label ?? setting.Key).ToLowerInvariant();
                        var ttL = (setting.Tooltip ?? "").ToLowerInvariant();
                        var searchL = _editorSearchText.ToLowerInvariant();
                        if (!labelL.Contains(searchL) && !ttL.Contains(searchL)) continue;
                    }

                    visibleCount++;
                    var settingPanel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 12, 12) };
                    var labelRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
                    labelRow.Children.Add(new TextBlock
                    {
                        Text = setting.Label ?? setting.Key,
                        FontSize = 12,
                        Foreground = Application.Current?.FindResource("BrTextSecondary") as Avalonia.Media.IBrush ?? Avalonia.Media.Brushes.Gray
                    });

                    if (!string.IsNullOrWhiteSpace(setting.Tooltip))
                    {
                        var tooltipIcon = new Border
                        {
                            Width = 16, Height = 16,
                            CornerRadius = new CornerRadius(8),
                            BorderThickness = new Thickness(1),
                            BorderBrush = Application.Current?.FindResource("BrBorderSubtle") as Avalonia.Media.IBrush ?? Avalonia.Media.Brushes.DimGray,
                            Background = Application.Current?.FindResource("BrBgCard") as Avalonia.Media.IBrush ?? Avalonia.Media.Brushes.Transparent,
                            Child = new TextBlock
                            {
                                Text = "?", FontSize = 10, FontWeight = Avalonia.Media.FontWeight.Bold,
                                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                                Foreground = Application.Current?.FindResource("BrTextSecondary") as Avalonia.Media.IBrush ?? Avalonia.Media.Brushes.Gray
                            }
                        };
                        ToolTip.SetTip(tooltipIcon, setting.Tooltip);
                        labelRow.Children.Add(tooltipIcon);
                    }

                    settingPanel.Children.Add(labelRow);

                    var hasValue = _editorProfile!.IniSettings[sectionName].TryGetValue(setting.Key, out var currentValue);
                    if (!hasValue || string.IsNullOrWhiteSpace(currentValue))
                        currentValue = string.Equals(setting.Key, "ShortcutKey", StringComparison.OrdinalIgnoreCase) ? "0x2D" : "auto";

                    Control settingControl;
                    SettingControlRef settingRef;

                    if (string.Equals(setting.ControlType, "keybind", StringComparison.OrdinalIgnoreCase))
                    {
                        var kb = BuildEditorKeybindButton(currentValue);
                        settingControl = kb;
                        settingRef = new SettingControlRef(settingControl, () => kb.Tag?.ToString() ?? "auto", setting.AppliesTo);
                    }
                    else if (string.Equals(setting.ControlType, "text", StringComparison.OrdinalIgnoreCase))
                    {
                        var tb = new TextBox { Text = currentValue == "auto" ? "" : currentValue, Watermark = "auto", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
                        settingControl = tb;
                        settingRef = new SettingControlRef(settingControl, () => string.IsNullOrWhiteSpace(tb.Text) ? "auto" : tb.Text, setting.AppliesTo);
                    }
                    else if (string.Equals(setting.ControlType, "folderpath", StringComparison.OrdinalIgnoreCase))
                    {
                        var pathPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
                        var pathTb = new TextBox { Text = currentValue == "auto" ? "" : currentValue, Watermark = "auto", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, MinWidth = 180 };
                        var browseBtn = new Button { Content = "Browse...", Padding = new Thickness(12, 6), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch };
                        browseBtn.Click += async (s, e) =>
                        {
                            var tl = TopLevel.GetTopLevel(this);
                            if (tl != null)
                            {
                                var fp = await tl.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select Folder", AllowMultiple = false });
                                if (fp.Count > 0) pathTb.Text = fp[0].Path.LocalPath;
                            }
                        };
                        pathPanel.Children.Add(pathTb);
                        pathPanel.Children.Add(browseBtn);
                        settingControl = pathPanel;
                        settingRef = new SettingControlRef(settingControl, () => string.IsNullOrWhiteSpace(pathTb.Text) ? "auto" : pathTb.Text, setting.AppliesTo);
                    }
                    else
                    {
                        var combo = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
                        var optionItems = BuildEditorOptionItems(setting);
                        foreach (var opt in optionItems) combo.Items.Add(opt);
                        combo.SelectedItem = optionItems.FirstOrDefault(x => x.Value == currentValue) ?? optionItems.FirstOrDefault();
                        settingControl = combo;
                        settingRef = new SettingControlRef(settingControl, () => combo.SelectedItem is OptionItem oi ? oi.Value : "auto", setting.AppliesTo);
                    }

                    _editorSettingControls[sectionName][setting.Key] = settingRef;
                    settingPanel.Children.Add(settingControl);
                    Grid.SetRow(settingPanel, Math.Clamp(setting.Row, 0, rows - 1));
                    Grid.SetColumn(settingPanel, Math.Clamp(setting.Column, 0, columns - 1));
                    sectionGrid.Children.Add(settingPanel);
                }
            }

            if (visibleCount == 0 && !string.IsNullOrWhiteSpace(_editorSearchText))
                return null!;

            cardContent.Children.Add(sectionGrid);
            return new Border
            {
                Background = Application.Current?.FindResource("BrBgCard") as Avalonia.Media.IBrush ?? Avalonia.Media.Brushes.Transparent,
                BorderBrush = Application.Current?.FindResource("BrBorderSubtle") as Avalonia.Media.IBrush ?? Avalonia.Media.Brushes.DimGray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 0, _editorLayout.ColumnGap, _editorLayout.RowGap),
                Child = cardContent
            };
        }

        private static List<OptionItem> BuildEditorOptionItems(SchemaSetting setting)
        {
            var items = new List<OptionItem>();
            foreach (var option in setting.Options ?? new List<OptionEntry>())
            {
                var value = option.Value ?? string.Empty;
                if (string.IsNullOrWhiteSpace(value)) continue;
                items.Add(new OptionItem(value, string.IsNullOrWhiteSpace(option.Label) ? value : option.Label!));
            }
            return items;
        }

        private void UpdateEditorWrapLayout()
        {
            if (_editorSectionsWrap == null) return;
            var availableWidth = _editorSectionsWrap.Bounds.Width;
            if (availableWidth <= 0)
                availableWidth = Math.Max(0, Bounds.Width - 120);
            var columns = CalculateEditorColumns(_editorLayout, availableWidth);
            var gap = _editorLayout.ColumnGap;
            var cardWidth = (availableWidth - (columns - 1) * gap) / columns;
            cardWidth = Math.Clamp(cardWidth, _editorLayout.CardMinWidth, _editorLayout.CardMaxWidth);
            _editorSectionsWrap.ItemWidth = cardWidth;
        }

        private static int CalculateEditorColumns(LayoutSettings layout, double width)
        {
            int columns = 1;
            if (layout.Breakpoints != null)
                foreach (var bp in layout.Breakpoints.OrderBy(b => b.MinWidth))
                    if (width >= bp.MinWidth) columns = bp.Columns;
            columns = Math.Min(layout.MaxColumns, Math.Max(1, columns));
            while (columns > 1)
            {
                if ((width - (columns - 1) * layout.ColumnGap) / columns >= layout.CardMinWidth) break;
                columns--;
            }
            return Math.Max(1, columns);
        }

        private async void EditorNavButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button button && button.Tag is string sectionName && _editorSectionBorders.TryGetValue(sectionName, out var sectionBorder))
                {
                    var scrollViewer = this.FindControl<ScrollViewer>("SettingsScrollViewerEd");
                    if (scrollViewer != null && _editorSectionsWrap != null)
                    {
                        await Task.Delay(10);
                        scrollViewer.InvalidateMeasure();
                        scrollViewer.InvalidateArrange();
                        _editorSectionsWrap.InvalidateMeasure();
                        _editorSectionsWrap.InvalidateArrange();
                        await Task.Delay(50);
                        var transform = sectionBorder.TransformToVisual(_editorSectionsWrap);
                        if (transform.HasValue)
                        {
                            var position = transform.Value.Transform(new Point(0, 0));
                            scrollViewer.Offset = new Vector(0, Math.Max(0, position.Y - 20));
                        }
                    }
                }
            }
            catch (Exception ex) { DebugWindow.Log($"[MainWindow] Editor nav scroll failed: {ex.Message}"); }
        }

        public Control? GetFirstSettingControlForSection(string sectionName)
        {
            if (_editorSectionBorders.TryGetValue(sectionName, out var sectionBorder))
            {
                return sectionBorder.GetVisualDescendants()
                    .OfType<Control>()
                    .FirstOrDefault(c => c.Focusable && c.IsVisible && (c is TextBox || c is ComboBox || c is Button));
            }
            return null;
        }

        private void TxtSettingsSearchEd_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                _editorSearchText = tb.Text?.Trim() ?? string.Empty;
                BuildEditorSettingsUI();
            }
        }

        private void BtnEasyModeEd_Click(object? sender, RoutedEventArgs e)
        {
            if (_editorIsEasyMode) return;
            EditorFlushControlValues();
            EditorSyncSchemaValues(fromEasyToAdvanced: false);
            _editorIsEasyMode = true;
            UpdateEditorModeButtons();
            BuildEditorSettingsUI();
        }

        private void BtnAdvancedModeEd_Click(object? sender, RoutedEventArgs e)
        {
            if (!_editorIsEasyMode) return;
            EditorFlushControlValues();
            EditorSyncSchemaValues(fromEasyToAdvanced: true);
            _editorIsEasyMode = false;
            UpdateEditorModeButtons();
            BuildEditorSettingsUI();
        }

        private void EditorFlushControlValues()
        {
            if (_editorProfile == null) return;
            foreach (var section in _editorSettingControls)
            {
                if (!_editorProfile.IniSettings.ContainsKey(section.Key))
                    _editorProfile.IniSettings[section.Key] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var setting in section.Value)
                    _editorProfile.IniSettings[section.Key][setting.Key] = setting.Value.ValueGetter?.Invoke() ?? "auto";
            }
        }

        private void EditorSyncSchemaValues(bool fromEasyToAdvanced)
        {
            if (_editorProfile == null) return;
            try
            {
                var easyPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "configs", "easy_profile_editor_schema.json");
                var masterPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "configs", "profile_editor_schema.json");
                if (!File.Exists(easyPath) || !File.Exists(masterPath)) return;

                var easySchema = JsonSerializer.Deserialize<SettingsSchema>(File.ReadAllText(easyPath));
                var masterSchema = JsonSerializer.Deserialize<SettingsSchema>(File.ReadAllText(masterPath));
                if (easySchema?.Sections == null || masterSchema?.Sections == null) return;

                // key -> canonical section name
                var keyToSection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var sec in masterSchema.Sections)
                {
                    if (sec?.Settings == null || string.IsNullOrWhiteSpace(sec.Name)) continue;
                    foreach (var s in sec.Settings)
                        if (!string.IsNullOrWhiteSpace(s.Key) && !keyToSection.ContainsKey(s.Key))
                            keyToSection[s.Key] = sec.Name!;
                }

                foreach (var easySection in easySchema.Sections)
                {
                    if (easySection?.Settings == null || string.IsNullOrWhiteSpace(easySection.Name)) continue;
                    if (!_editorProfile.IniSettings.ContainsKey(easySection.Name))
                        _editorProfile.IniSettings[easySection.Name] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var s in easySection.Settings)
                    {
                        if (s == null || string.IsNullOrWhiteSpace(s.Key)) continue;

                        bool hasAppliesTo = s.AppliesTo != null && s.AppliesTo.Count > 0;

                        if (!hasAppliesTo)
                        {
                            // passthrough key: sync directly with its canonical section
                            if (!keyToSection.TryGetValue(s.Key, out var passCanon)) continue;
                            if (fromEasyToAdvanced)
                            {
                                if (_editorProfile.IniSettings[easySection.Name].TryGetValue(s.Key, out var v) && !string.IsNullOrWhiteSpace(v))
                                {
                                    if (!_editorProfile.IniSettings.ContainsKey(passCanon))
                                        _editorProfile.IniSettings[passCanon] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                    _editorProfile.IniSettings[passCanon][s.Key] = v;
                                }
                            }
                            else
                            {
                                if (_editorProfile.IniSettings.ContainsKey(passCanon) &&
                                    _editorProfile.IniSettings[passCanon].TryGetValue(s.Key, out var cv) && !string.IsNullOrWhiteSpace(cv))
                                    _editorProfile.IniSettings[easySection.Name][s.Key] = cv;
                            }
                            continue;
                        }

                        if (fromEasyToAdvanced)
                        {
                            if (!_editorProfile.IniSettings[easySection.Name].TryGetValue(s.Key, out var easyVal) || string.IsNullOrWhiteSpace(easyVal)) continue;
                            foreach (var targetKey in s.AppliesTo!)
                            {
                                if (string.IsNullOrWhiteSpace(targetKey)) continue;
                                if (keyToSection.TryGetValue(targetKey, out var canon))
                                {
                                    if (!_editorProfile.IniSettings.ContainsKey(canon))
                                        _editorProfile.IniSettings[canon] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                    _editorProfile.IniSettings[canon][targetKey] = easyVal;
                                }
                            }
                        }
                        else
                        {
                            foreach (var targetKey in s.AppliesTo!)
                            {
                                if (string.IsNullOrWhiteSpace(targetKey)) continue;
                                if (keyToSection.TryGetValue(targetKey, out var canon) &&
                                    _editorProfile.IniSettings.ContainsKey(canon) &&
                                    _editorProfile.IniSettings[canon].TryGetValue(targetKey, out var canVal) &&
                                    !string.IsNullOrWhiteSpace(canVal))
                                {
                                    _editorProfile.IniSettings[easySection.Name][s.Key] = canVal;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { DebugWindow.Log($"[MainWindow] Profile sync failed: {ex.Message}"); }
        }

        private void UpdateEditorModeButtons()
        {
            var btnEasy = this.FindControl<Button>("BtnEasyModeEd");
            var btnAdv = this.FindControl<Button>("BtnAdvancedModeEd");
            if (btnEasy == null || btnAdv == null) return;
            if (_editorIsEasyMode)
            {
                btnEasy.Classes.Remove("BtnSecondary"); btnEasy.Classes.Add("BtnPrimary");
                btnAdv.Classes.Remove("BtnPrimary"); btnAdv.Classes.Add("BtnSecondary");
            }
            else
            {
                btnEasy.Classes.Remove("BtnPrimary"); btnEasy.Classes.Add("BtnSecondary");
                btnAdv.Classes.Remove("BtnSecondary"); btnAdv.Classes.Add("BtnPrimary");
            }
        }

        private void BtnEditorBack_Click(object? sender, RoutedEventArgs e)
        {
            _editorKeyCaptureButton = null;
            IsProfileEditorOpen = false;
            SwitchToView("ViewProfiles");
            this.FindControl<Button>("BtnNewProfileView")?.Focus();
        }

        private void BtnEditorSave_Click(object? sender, RoutedEventArgs e)
        {
            if (_editorProfile == null) return;
            var txtName = this.FindControl<TextBox>("TxtProfileNameEd");
            var txtDesc = this.FindControl<TextBox>("TxtDescriptionEd");
            if (txtName == null || string.IsNullOrWhiteSpace(txtName.Text)) return;

            if (!_editorProfile.IsBuiltIn)
                _editorProfile.Name = txtName.Text.Trim();
            _editorProfile.Description = txtDesc?.Text?.Trim() ?? "";

            // Flush all current control values then sync Easy→canonical
            EditorFlushControlValues();
            if (_editorIsEasyMode)
                EditorSyncSchemaValues(fromEasyToAdvanced: true);

            try
            {
                _profileService.SaveProfile(_editorProfile, isBuiltIn: false);
                if (_editorWasDefault)
                {
                    _componentService.Config.DefaultProfileName = _editorProfile.Name;
                    _componentService.SaveConfiguration();
                }
                _editorKeyCaptureButton = null;
                IsProfileEditorOpen = false;
                SwitchToView("ViewProfiles");
                LoadProfilesView();
                this.FindControl<Button>("BtnNewProfileView")?.Focus();
            }
            catch (Exception ex)
            {
                _ = new ConfirmDialog(this, "Error", $"Failed to save profile: {ex.Message}").ShowDialog<object>(this);
            }
        }

        private Button BuildEditorKeybindButton(string value)
        {
            var button = new Button
            {
                Height = 32,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Content = EditorFormatKeybindLabel(value),
                Tag = value
            };
            button.Click += (_, __) => BeginEditorKeyCapture(button);
            return button;
        }

        private void BeginEditorKeyCapture(Button button)
        {
            _editorKeyCaptureButton = button;
            _editorKeyCapturePreviousValue = button.Tag?.ToString() ?? "auto";
            button.Content = "Press a key...";
        }

        private void HandleEditorKeyCapture(object? sender, KeyEventArgs e)
        {
            if (_editorKeyCaptureButton == null) return;
            e.Handled = true;
            var key = e.Key;
            if (key == Key.Escape)
            {
                SetEditorKeybindValue(_editorKeyCaptureButton, _editorKeyCapturePreviousValue ?? "auto");
                _editorKeyCaptureButton = null;
                _editorKeyCapturePreviousValue = null;
                return;
            }
            string? newValue = key switch
            {
                Key.Back => "auto",
                Key.Delete => "-1",
                _ => TryMapEditorKeyToVirtualKey(key)
            };
            SetEditorKeybindValue(_editorKeyCaptureButton, string.IsNullOrWhiteSpace(newValue) ? (_editorKeyCapturePreviousValue ?? "auto") : newValue);
            _editorKeyCaptureButton = null;
            _editorKeyCapturePreviousValue = null;
        }

        private void SetEditorKeybindValue(Button button, string value)
        {
            button.Tag = value;
            button.Content = EditorFormatKeybindLabel(value);
        }

        private static string? TryMapEditorKeyToVirtualKey(Key key)
        {
            if (key >= Key.A && key <= Key.Z) return $"0x{0x41 + (key - Key.A):X2}";
            if (key >= Key.D0 && key <= Key.D9) return $"0x{0x30 + (key - Key.D0):X2}";
            if (key >= Key.NumPad0 && key <= Key.NumPad9) return $"0x{0x60 + (key - Key.NumPad0):X2}";
            if (key >= Key.F1 && key <= Key.F12) return $"0x{0x70 + (key - Key.F1):X2}";
            return key switch
            {
                Key.Insert => "0x2D", Key.Home => "0x24", Key.End => "0x23",
                Key.PageUp => "0x21", Key.PageDown => "0x22", Key.Back => "0x08",
                Key.Tab => "0x09", Key.Enter => "0x0D", Key.Space => "0x20",
                Key.Left => "0x25", Key.Up => "0x26", Key.Right => "0x27", Key.Down => "0x28",
                Key.Delete => "0x2E", Key.Escape => "0x1B",
                Key.LeftShift or Key.RightShift => "0x10",
                Key.LeftCtrl or Key.RightCtrl => "0x11",
                Key.LeftAlt or Key.RightAlt => "0x12",
                Key.CapsLock => "0x14", Key.PrintScreen => "0x2C", Key.Pause => "0x13",
                _ => null
            };
        }

        private static string EditorFormatKeybindLabel(string value)
        {
            if (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase)) return "Auto";
            if (value == "-1") return "Disabled";
            var normalized = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
            if (int.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                return EditorGetVirtualKeyLabel(code);
            return value;
        }

        private static string EditorGetVirtualKeyLabel(int code)
        {
            if (code >= 0x41 && code <= 0x5A) return ((char)code).ToString();
            if (code >= 0x30 && code <= 0x39) return ((char)code).ToString();
            if (code >= 0x70 && code <= 0x7B) return $"F{code - 0x6F}";
            return code switch
            {
                0x2D => "Insert", 0x24 => "Home", 0x23 => "End",
                0x21 => "Page Up", 0x22 => "Page Down", 0x08 => "Backspace",
                0x09 => "Tab", 0x0D => "Enter", 0x20 => "Space",
                0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
                0x2E => "Delete", 0x1B => "Escape", 0x10 => "Shift",
                0x11 => "Ctrl", 0x12 => "Alt", 0x14 => "Caps Lock",
                0x2C => "Print Screen", 0x13 => "Pause",
                _ => $"Key {code:X2}"
            };
        }

        // ─────────────────────────────────────────────────────────────────────────

        private void TglAutoScan_IsCheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_isInitializingLanguage) return;
            if (sender is ToggleSwitch tgl)
            {
                _componentService.Config.AutoScan = tgl.IsChecked ?? true;
                _componentService.SaveConfiguration();
            }
        }

        private void TglAnimations_IsCheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_isInitializingLanguage) return;
            if (sender is ToggleSwitch tgl)
            {
                _componentService.Config.AnimationsEnabled = tgl.IsChecked ?? true;
                _componentService.SaveConfiguration();
                UpdateAnimationsState(_componentService.Config.AnimationsEnabled);
            }
        }

        private async void BtnManageDefaultVersions_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new ManageDefaultVersionsWindow(this, _componentService);
                await dialog.ShowDialog<bool?>(this);
            }
            catch (Exception ex) { DebugWindow.Log($"[MainWindow] Default versions dialog failed: {ex.Message}"); }
        }

        private void PopulateDefaultGpuComboBox()
        {
            var cmb = this.FindControl<ComboBox>("CmbDefaultGpu");
            if (cmb == null) return;

            _isInitializingLanguage = true;
            cmb.Items.Clear();

            var autoItem = new ComboBoxItem { Content = "Auto (Recommended)", Tag = "auto" };
            cmb.Items.Add(autoItem);

            if (_gpuService != null)
            {
                var gpus = _gpuService.DetectGPUs();
                foreach (var gpu in gpus)
                {
                    var label = $"{gpu.Vendor} - {gpu.Name}";
                    var id = GpuSelectionHelper.BuildGpuId(gpu);
                    cmb.Items.Add(new ComboBoxItem { Content = label, Tag = id });
                }
            }

            var saved = _componentService.Config.DefaultGpuId;
            cmb.SelectedIndex = 0;
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

            _isInitializingLanguage = false;
        }

        private void CmbDefaultGpu_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingLanguage) return;
            if (sender is ComboBox cmb && cmb.SelectedItem is ComboBoxItem item)
            {
                var id = item.Tag?.ToString();
                _componentService.Config.DefaultGpuId = (id == "auto") ? null : id;
                _componentService.SaveConfiguration();
                _lastDetectedGpu = null;
                _ = LoadGpuInfoAsync();
            }
        }

        private void TxtSteamGridApiKey_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox textBox) return;

            _componentService.Config.SteamGridDBApiKey = (textBox.Text ?? string.Empty).Trim();
            _componentService.SaveConfiguration();
        }

        private async void BtnManageProxy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var proxyWindow = new ProxySettingsWindow(this, _componentService);
                await proxyWindow.ShowDialog<object>(this);
            }
            catch (Exception ex) { DebugWindow.Log($"[MainWindow] Proxy settings dialog failed: {ex.Message}"); }
        }

        private async void BtnSteamGridApiGuide_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var guideWindow = new SteamGridApiGuideWindow(this);
                await guideWindow.ShowDialog(this);
            }
            catch (Exception ex) { DebugWindow.Log($"[MainWindow] SteamGrid guide dialog failed: {ex.Message}"); }
        }

        private void SettingsBackground_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.Source is not Visual visual)
                return;

            if (visual.FindAncestorOfType<TextBox>() != null)
                return;
            if (visual.FindAncestorOfType<Button>() != null)
                return;
            if (visual.FindAncestorOfType<ComboBox>() != null)
                return;
            if (visual.FindAncestorOfType<ToggleSwitch>() != null)
                return;

            var focusManager = TopLevel.GetTopLevel(this)?.FocusManager;
            focusManager?.ClearFocus();
        }

        private void SettingsWrap_SizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (sender is not WrapPanel wrap) return;
            ApplySettingsCardWidths(wrap, e.NewSize.Width);
        }

        private void ApplySettingsCardWidths(WrapPanel wrap, double availableWidth)
        {
            // Sidebar is 68px + 24px margin on each side = 116px total chrome
            const double gutter = 48.0;
            const double threshold = 760.0;
            const double halfGap = 16.0; // gap between two cards

            double cardWidth = availableWidth >= threshold
                ? Math.Max(300.0, (availableWidth - gutter - halfGap) / 2.0)
                : availableWidth - gutter;

            foreach (var child in wrap.Children)
            {
                if (child is Border border)
                    border.Width = cardWidth;
            }
        }

        private void UpdateAnimationsState(bool enabled)
        {
            var duration = enabled ? TimeSpan.FromMilliseconds(180) : TimeSpan.Zero;

            // Update main view transitions
            foreach (var viewName in _viewNames)
            {
                var grid = this.FindControl<Grid>(viewName);
                if (grid?.Transitions != null)
                {
                    grid.Transitions.Clear();
                    if (enabled)
                    {
                        grid.Transitions.Add(new Avalonia.Animation.DoubleTransition
                        {
                            Property = Visual.OpacityProperty,
                            Duration = duration,
                            Easing = new Avalonia.Animation.Easings.CubicEaseOut()
                        });
                    }
                }
            }
        }

        private async Task ScheduleStartupUpdatesAsync(CancellationToken cancellationToken)
        {
            bool versionsEmpty = _componentService.OptiScalerAvailableVersions.Count == 0;

            if (versionsEmpty)
            {
                // No cached data — show loading overlay immediately and skip the idle delay
                if (_overlayLoading != null) _overlayLoading.IsVisible = true;
            }

            try
            {
                if (!versionsEmpty)
                {
                    await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
                    if (cancellationToken.IsCancellationRequested) return;
                }

                await CheckUpdatesOnStartupAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (GitHubRateLimitException)
            {
                await ShowGitHubRateLimitDialogAsync();
            }
            catch
            {
            }
            finally
            {
                if (_overlayLoading != null) _overlayLoading.IsVisible = false;
            }
        }

        private async Task CheckUpdatesOnStartupAsync(CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_txtStatus != null) _txtStatus.Text = GetResourceString("TxtCheckingUpdates", "Checking for updates...");

                // Runs independently of the component version check below, and BEFORE it —
                // if that call throws (e.g. GitHub API rate limit), this must still have run.
                // Best-effort, never blocks startup or surfaces an error — see CompatibilityListService.
                try { await new CompatibilityListService().CheckForUpdatesAsync(); }
                catch (Exception ex) { DebugWindow.Log($"[MainWindow] CompatibilityListService refresh failed: {ex.Message}"); }

                await _componentService.CheckForUpdatesAsync();

                // Best-effort, never blocks startup or surfaces an error - see CheckAppUpdateNotificationAsync.
                try { await CheckAppUpdateNotificationAsync(); }
                catch (Exception ex) { DebugWindow.Log($"[MainWindow] App update notification check failed: {ex.Message}"); }
            }
            catch (OperationCanceledException) { }
            catch (GitHubRateLimitException)
            {
                if (_txtStatus != null) _txtStatus.Text = GetResourceString("TxtReady", "Ready");
                throw; // bubble up to ScheduleStartupUpdatesAsync
            }
            catch (Exception ex) { DebugWindow.Log($"[MainWindow] CheckForUpdatesAsync failed: {ex.Message}"); }
            finally
            {
                ComponentStatusChanged();
                if (!cancellationToken.IsCancellationRequested && _txtStatus != null)
                    _txtStatus.Text = GetResourceString("TxtReady", "Ready");
            }
        }

        /// <summary>
        /// Shows the "Update Available" popup once per newly-released app version. Runs on every
        /// startup, but stays silent unless the latest GitHub release is newer than the version
        /// currently running (so it never re-appears just because the user already updated) and
        /// hasn't already been shown and dismissed for that specific version.
        /// </summary>
        private async Task CheckAppUpdateNotificationAsync()
        {
            var appUpdateService = new AppUpdateService(_componentService);
            bool hasUpdate = await appUpdateService.CheckForAppUpdateAsync();
            if (!hasUpdate || string.IsNullOrEmpty(appUpdateService.LatestVersion))
                return;

            var latest = appUpdateService.LatestVersion;
            var config = _componentService.Config;

            bool alreadyNotified = config.LastNotifiedUpdateVersion == latest && config.UpdateNotificationDismissed;
            if (alreadyNotified)
                return;

            var popup = new UpdateAvailableWindow(this, latest);
            await popup.ShowDialog(this);

            config.LastNotifiedUpdateVersion = latest;
            config.UpdateNotificationDismissed = true;
            _componentService.SaveConfiguration();
        }

        private async Task ShowGitHubRateLimitDialogAsync()
        {
            await new ConfirmDialog(
                this,
                "GitHub Rate Limit Reached",
                "Could not fetch version data from GitHub (HTTP 403 — too many requests).\n\nPlease wait a few minutes and restart the application.\n\nIn the meantime, you can still install any OptiScaler versions already cached locally.",
                isAlert: true,
                iconOverride: "\uF4A4"
            ).ShowDialog<object>(this);
        }

        private void PopulateHelpContent()
        {
            var sidebar = this.FindControl<StackPanel>("HelpPagesSidebar");
            var contentArea = this.FindControl<StackPanel>("HelpContentArea");

            if (sidebar == null || contentArea == null) return;

            var pages = _helpPageService.LoadHelpPages();

            sidebar.Children.Clear();

            // Process pages in order, grouping consecutive pages with same category
            var i = 0;
            while (i < pages.Count)
            {
                var currentPage = pages[i];

                if (string.IsNullOrEmpty(currentPage.Category))
                {
                    // Regular page without category
                    var button = new Button
                    {
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                        Padding = new Thickness(12, 10),
                        Margin = new Thickness(0, 0, 0, 4),
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Tag = currentPage.Id
                    };

                    var stack = new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 10
                    };

                    var icon = new TextBlock
                    {
                        Text = currentPage.Icon,
                        FontFamily = new FontFamily("avares://OptiscalerClient/assets/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular"),
                        FontSize = 16,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                    };

                    var title = new TextBlock
                    {
                        Text = string.IsNullOrEmpty(currentPage.TitleKey) ? currentPage.Title : GetResourceString(currentPage.TitleKey, currentPage.Title),
                        FontSize = 14,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                    };

                    stack.Children.Add(icon);
                    stack.Children.Add(title);
                    button.Content = stack;
                    button.Click += (s, e) => LoadHelpPage(currentPage.Id);

                    sidebar.Children.Add(button);
                    i++;
                }
                else
                {
                    // Start of a category group - collect all consecutive pages with same category
                    var category = currentPage.Category;
                    var categoryPages = new List<HelpPage>();

                    while (i < pages.Count && pages[i].Category == category)
                    {
                        categoryPages.Add(pages[i]);
                        i++;
                    }

                    // Create expandable category
                    var categoryContainer = new StackPanel();

                    // Category button (expandable)
                    var categoryButton = new Button
                    {
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                        Padding = new Thickness(12, 10),
                        Margin = new Thickness(0, 0, 0, 4),
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Cursor = new Cursor(StandardCursorType.Hand)
                    };

                    // Remove hover/pressed effects
                    categoryButton.Styles.Add(new Style(x => x.OfType<Button>().Class(":pointerover"))
                    {
                        Setters = { new Setter(Button.BackgroundProperty, Brushes.Transparent) }
                    });
                    categoryButton.Styles.Add(new Style(x => x.OfType<Button>().Class(":pressed"))
                    {
                        Setters = { new Setter(Button.BackgroundProperty, Brushes.Transparent) }
                    });

                    var categoryStack = new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 10
                    };

                    var expandIcon = new TextBlock
                    {
                        Text = "\uF2A3", // ChevronDown
                        FontFamily = new FontFamily("avares://OptiscalerClient/assets/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular"),
                        FontSize = 12,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Foreground = this.FindResource("BrTextSecondary") as IBrush
                    };

                    var categoryIcon = new TextBlock
                    {
                        Text = "\uF4D3", // Library icon for Guides
                        FontFamily = new FontFamily("avares://OptiscalerClient/assets/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular"),
                        FontSize = 16,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Foreground = this.FindResource("BrTextSecondary") as IBrush
                    };

                    var categoryTitle = new TextBlock
                    {
                        Text = (categoryPages.Count > 0 && !string.IsNullOrEmpty(categoryPages[0].CategoryKey))
                            ? GetResourceString(categoryPages[0].CategoryKey!, category)
                            : category,
                        FontSize = 14,
                        FontWeight = FontWeight.SemiBold,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Foreground = this.FindResource("BrTextSecondary") as IBrush
                    };

                    categoryStack.Children.Add(expandIcon);
                    categoryStack.Children.Add(categoryIcon);
                    categoryStack.Children.Add(categoryTitle);
                    categoryButton.Content = categoryStack;

                    // Container for child pages
                    var childrenContainer = new StackPanel
                    {
                        Margin = new Thickness(20, 0, 0, 0),
                        IsVisible = true // Start expanded
                    };

                    // Add pages in this category
                    foreach (var page in categoryPages)
                    {
                        var pageButton = new Button
                        {
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                            Padding = new Thickness(12, 10),
                            Margin = new Thickness(0, 0, 0, 4),
                            Background = Brushes.Transparent,
                            BorderThickness = new Thickness(0),
                            Tag = page.Id
                        };

                        var pageStack = new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            Spacing = 10
                        };

                        var pageIcon = new TextBlock
                        {
                            Text = page.Icon,
                            FontFamily = new FontFamily("avares://OptiscalerClient/assets/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular"),
                            FontSize = 16,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                            Foreground = this.FindResource("BrTextSecondary") as IBrush
                        };

                        var pageTitle = new TextBlock
                        {
                            Text = string.IsNullOrEmpty(page.TitleKey) ? page.Title : GetResourceString(page.TitleKey, page.Title),
                            FontSize = 14,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                            Foreground = this.FindResource("BrTextSecondary") as IBrush
                        };

                        pageStack.Children.Add(pageIcon);
                        pageStack.Children.Add(pageTitle);
                        pageButton.Content = pageStack;
                        pageButton.Click += (s, e) => LoadHelpPage(page.Id);

                        childrenContainer.Children.Add(pageButton);
                    }

                    // Toggle expand/collapse on category button click
                    categoryButton.Click += (s, e) =>
                    {
                        childrenContainer.IsVisible = !childrenContainer.IsVisible;
                        expandIcon.Text = childrenContainer.IsVisible ? "\uF2A3" : "\uF2B6"; // ChevronDown : ChevronUp
                    };

                    categoryContainer.Children.Add(categoryButton);
                    categoryContainer.Children.Add(childrenContainer);
                    sidebar.Children.Add(categoryContainer);
                }
            }

            LoadHelpPage(_currentHelpPageId);
        }

        private void LoadHelpPage(string pageId)
        {
            _currentHelpPageId = pageId;
            var contentArea = this.FindControl<StackPanel>("HelpContentArea");
            var sidebar = this.FindControl<StackPanel>("HelpPagesSidebar");

            if (contentArea == null) return;

            var pages = _helpPageService.LoadHelpPages();
            var page = pages.Find(p => p.Id == pageId);

            if (page == null) return;

            UpdateSidebarSelection(sidebar, pageId);

            contentArea.Children.Clear();

            // Store the current page font size for use in rendering
            _currentPageFontSize = page.FontSize;

            foreach (var section in page.Sections)
            {
                RenderSection(contentArea, section);
            }
        }

        private void UpdateSidebarSelection(StackPanel? sidebar, string selectedPageId)
        {
            if (sidebar == null) return;

            var activeBg = this.FindResource("BrBgCard") as IBrush ?? Brushes.DimGray;
            var inactiveBg = Brushes.Transparent;
            var activeFg = this.FindResource("BrTextPrimary") as IBrush ?? Brushes.White;
            var inactiveFg = this.FindResource("BrTextSecondary") as IBrush ?? Brushes.Gray;

            foreach (var child in sidebar.Children)
            {
                if (child is Button btn)
                {
                    bool isActive = btn.Tag?.ToString() == selectedPageId;
                    btn.Background = isActive ? activeBg : inactiveBg;

                    if (btn.Content is StackPanel stack)
                    {
                        foreach (var item in stack.Children)
                        {
                            if (item is TextBlock tb)
                            {
                                tb.Foreground = isActive ? activeFg : inactiveFg;
                            }
                        }
                    }
                }
                else if (child is StackPanel categoryContainer)
                {
                    // Check nested buttons inside category containers
                    foreach (var categoryChild in categoryContainer.Children)
                    {
                        if (categoryChild is StackPanel nestedContainer)
                        {
                            // This is the children container with the actual page buttons
                            foreach (var nestedChild in nestedContainer.Children)
                            {
                                if (nestedChild is Button nestedBtn)
                                {
                                    bool isActive = nestedBtn.Tag?.ToString() == selectedPageId;
                                    nestedBtn.Background = isActive ? activeBg : inactiveBg;

                                    if (nestedBtn.Content is StackPanel nestedStack)
                                    {
                                        foreach (var item in nestedStack.Children)
                                        {
                                            if (item is TextBlock tb)
                                            {
                                                tb.Foreground = isActive ? activeFg : inactiveFg;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private double GetFontSize(double defaultSize, double? itemFontSize = null)
        {
            return itemFontSize ?? _currentPageFontSize ?? defaultSize;
        }

        private void RenderSection(StackPanel container, HelpSection section)
        {
            switch (section.Type)
            {
                case "disclaimer":
                    RenderDisclaimerSection(container, section);
                    break;
                case "guide-button":
                    RenderGuideButton(container);
                    break;
                case "app-info":
                    RenderAppInfo(container);
                    break;
                case "version-management-info":
                    RenderVersionManagementInfo(container);
                    break;
                case "system-info":
                    RenderSystemInfo(container, section);
                    break;
                case "feedback":
                    RenderFeedback(container);
                    break;
                case "donate":
                    RenderDonate(container);
                    break;
                case "text":
                    RenderTextSection(container, section);
                    break;
                case "steps":
                case "list":
                case "faq":
                    RenderListSection(container, section);
                    break;
            }
        }

        private void RenderGuideButton(StackPanel container)
        {
            var title = new TextBlock
            {
                Text = GetResourceString("TxtGuideTitle", "Guide"),
                FontSize = 18,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = this.FindResource("BrTextPrimary") as IBrush
            };

            var button = new Button
            {
                Content = GetResourceString("TxtBtnGuide", "Open Guide"),
                Padding = new Thickness(16, 12),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 32)
            };
            button.Classes.Add("BtnBase");
            button.Click += BtnGuide_Click2;

            container.Children.Add(title);
            container.Children.Add(button);
        }

        private void RenderDisclaimerSection(StackPanel container, HelpSection section)
        {
            var border = new Border
            {
                Padding = new Thickness(16, 12),
                Margin = new Thickness(0, 0, 0, 24),
                BorderThickness = new Thickness(1),
                Background = this.FindResource("BrBgSurface") as IBrush,
                BorderBrush = this.FindResource("BrBorderSubtle") as IBrush,
                CornerRadius = (CornerRadius)(this.FindResource("RadiusMedium") ?? new CornerRadius(8))
            };

            var stackPanel = new StackPanel();

            if (!string.IsNullOrEmpty(section.Title))
            {
                var title = new TextBlock
                {
                    Text = string.IsNullOrEmpty(section.TitleKey) ? section.Title : GetResourceString(section.TitleKey, section.Title),
                    FontSize = GetFontSize(16, section.FontSize),
                    FontWeight = FontWeight.Bold,
                    Margin = new Thickness(0, 0, 0, 8),
                    Foreground = section.TextColor != null ?
                        new SolidColorBrush(Color.Parse(section.TextColor)) :
                        this.FindResource("BrTextPrimary") as IBrush
                };
                stackPanel.Children.Add(title);
            }

            if (!string.IsNullOrEmpty(section.Content))
            {
                var translatedContent = string.IsNullOrEmpty(section.ContentKey) ? section.Content : GetResourceString(section.ContentKey, section.Content);
                var content = new TextBlock
                {
                    Text = translatedContent!.Replace("\\n", "\n"),
                    FontSize = GetFontSize(14, section.FontSize),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20,
                    Foreground = section.TextColor != null ?
                        new SolidColorBrush(Color.Parse(section.TextColor)) :
                        this.FindResource("BrTextSecondary") as IBrush
                };
                stackPanel.Children.Add(content);
            }

            border.Child = stackPanel;
            container.Children.Add(border);
        }

        private void RenderAppInfo(StackPanel container)
        {
            var title = new TextBlock
            {
                Text = GetResourceString("TxtAppInfoTitle", "Application Info"),
                FontSize = 18,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = this.FindResource("BrTextPrimary") as IBrush
            };

            var border = new Border
            {
                Padding = new Thickness(16, 12),
                Margin = new Thickness(0, 0, 0, 16),
                BorderThickness = new Thickness(1),
                Background = this.FindResource("BrBgSurface") as IBrush,
                BorderBrush = this.FindResource("BrBorderSubtle") as IBrush,
                CornerRadius = (CornerRadius)(this.FindResource("RadiusMedium") ?? new CornerRadius(8))
            };

            var stack = new StackPanel();

            var appGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            appGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            appGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var appLabel = new TextBlock
            {
                Text = "Optiscaler Client",
                FontWeight = FontWeight.SemiBold,
                FontSize = 15,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = this.FindResource("BrTextPrimary") as IBrush
            };

            var appVersion = new TextBlock
            {
                Text = $"v{App.AppVersion}",
                FontWeight = FontWeight.Bold,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = this.FindResource("BrAccent") as IBrush
            };

            Grid.SetColumn(appLabel, 0);
            Grid.SetColumn(appVersion, 1);
            appGrid.Children.Add(appLabel);
            appGrid.Children.Add(appVersion);

            var dateGrid = new Grid();
            dateGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dateGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dateLabel = new TextBlock
            {
                Text = GetResourceString("TxtBuildDateLbl", "Build Date"),
                FontWeight = FontWeight.SemiBold,
                FontSize = 15,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = this.FindResource("BrTextPrimary") as IBrush
            };

            var dateValue = new TextBlock
            {
                FontWeight = FontWeight.Bold,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = this.FindResource("BrTextSecondary") as IBrush
            };

            try
            {
                var buildDate = System.IO.File.GetLastWriteTime(System.AppContext.BaseDirectory);
                dateValue.Text = buildDate.ToString("yyyy-MM-dd");
            }
            catch
            {
                dateValue.Text = "Unknown";
            }

            Grid.SetColumn(dateLabel, 0);
            Grid.SetColumn(dateValue, 1);
            dateGrid.Children.Add(dateLabel);
            dateGrid.Children.Add(dateValue);

            stack.Children.Add(appGrid);
            stack.Children.Add(dateGrid);
            border.Child = stack;

            container.Children.Add(title);
            container.Children.Add(border);
        }

        private void RenderVersionManagementInfo(StackPanel container)
        {
            var title = new TextBlock
            {
                Text = GetResourceString("TxtVersionMgmtInfoTitle", "Version Management"),
                FontSize = 18,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = this.FindResource("BrTextPrimary") as IBrush
            };

            var border = new Border
            {
                Padding = new Thickness(16, 12),
                Margin = new Thickness(0, 0, 0, 16),
                BorderThickness = new Thickness(1),
                Background = this.FindResource("BrBgSurface") as IBrush,
                BorderBrush = this.FindResource("BrBorderSubtle") as IBrush,
                CornerRadius = (CornerRadius)(this.FindResource("RadiusMedium") ?? new CornerRadius(8))
            };

            var infoStack = new StackPanel { Spacing = 8 };

            infoStack.Children.Add(new TextBlock
            {
                Text = GetResourceString("TxtVersionMgmtInfoDesc", "OptiScaler, OptiPatcher, FSR4 INT8, Fakenvapi and NukemFG versions can be managed from the Local cache & Default version management section in Settings."),
                Foreground = this.FindResource("BrTextSecondary") as IBrush,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13
            });

            var githubBtn = new Button
            {
                Content = GetResourceString("TxtGithubBtn", "GitHub Repository"),
                Padding = new Thickness(16, 8),
                Margin = new Thickness(0, 8, 0, 0)
            };
            githubBtn.Classes.Add("BtnBase");
            githubBtn.Click += BtnGithub_Click;

            infoStack.Children.Add(githubBtn);

            border.Child = infoStack;

            container.Children.Add(title);
            container.Children.Add(border);
        }

        private void RenderSystemInfo(StackPanel container, HelpSection section)
        {
            try
            {
                LogToFile("[RenderSystemInfo] Starting...");

                var title = new TextBlock
                {
                    Text = string.IsNullOrEmpty(section.TitleKey)
                        ? (string.IsNullOrWhiteSpace(section.Title) ? "System" : section.Title)
                        : GetResourceString(section.TitleKey, string.IsNullOrWhiteSpace(section.Title) ? "System" : section.Title),
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 0, 0, 12),
                    Foreground = this.FindResource("BrTextPrimary") as IBrush
                };
                LogToFile("[RenderSystemInfo] Title created");

                var border = new Border
                {
                    Padding = new Thickness(16, 12),
                    Margin = new Thickness(0, 0, 0, 24),
                    BorderThickness = new Thickness(1),
                    Background = this.FindResource("BrBgSurface") as IBrush,
                    BorderBrush = this.FindResource("BrBorderSubtle") as IBrush,
                    CornerRadius = (CornerRadius)(this.FindResource("RadiusMedium") ?? new CornerRadius(8))
                };
                LogToFile("[RenderSystemInfo] Border created");

                var stack = new StackPanel();
                LogToFile("[RenderSystemInfo] StackPanel created");

                LogToFile("[RenderSystemInfo] Getting OS name...");
                var osName = GetFriendlyOperatingSystemName();
                LogToFile($"[RenderSystemInfo] OS name: {osName}");
                stack.Children.Add(CreateResourceRow("Operating System", osName, false, false));

                LogToFile("[RenderSystemInfo] Adding Architecture...");
                stack.Children.Add(CreateResourceRow("Architecture", RuntimeInformation.OSArchitecture.ToString(), false, false));

                LogToFile("[RenderSystemInfo] Adding Machine name...");
                stack.Children.Add(CreateResourceRow("Machine", Environment.MachineName, false, false));

                LogToFile("[RenderSystemInfo] Getting GPU info...");
                var gpuInfo = GetHelpGpuInfo();
                LogToFile($"[RenderSystemInfo] GPU info: {gpuInfo.DisplayName}");
                stack.Children.Add(CreateResourceRow("GPU", gpuInfo.DisplayName, false, true));

                border.Child = stack;
                container.Children.Add(title);
                container.Children.Add(border);
                LogToFile("[RenderSystemInfo] Completed successfully");
            }
            catch (Exception ex)
            {
                LogToFile($"[RenderSystemInfo] CRASH: {ex.GetType().Name}: {ex.Message}");
                LogToFile($"[RenderSystemInfo] StackTrace: {ex.StackTrace}");
                throw;
            }
        }

        private (string DisplayName, bool IsLast) GetHelpGpuInfo()
        {
            try
            {
                LogToFile("[GetHelpGpuInfo] Starting...");

                if (_gpuService == null)
                {
                    LogToFile("[GetHelpGpuInfo] _gpuService is null");
                    return ("Not available", true);
                }

                if (_componentService == null)
                {
                    LogToFile("[GetHelpGpuInfo] _componentService is null");
                    return ("Not available", true);
                }

                GpuInfo? gpu = _lastDetectedGpu;
                LogToFile($"[GetHelpGpuInfo] _lastDetectedGpu: {(gpu == null ? "null" : gpu.Name)}");

                if (gpu == null)
                {
                    try
                    {
                        LogToFile("[GetHelpGpuInfo] Detecting GPUs...");
                        var allGpus = _gpuService.DetectGPUs();
                        LogToFile($"[GetHelpGpuInfo] Detected {(allGpus?.Length ?? 0)} GPUs");

                        if (allGpus != null && allGpus.Length > 0)
                        {
                            var defaultGpuId = _componentService.Config?.DefaultGpuId;
                            LogToFile($"[GetHelpGpuInfo] DefaultGpuId: {defaultGpuId ?? "null"}");


                            gpu = GpuSelectionHelper.GetPreferredGpu(_gpuService, defaultGpuId)
                                  ?? allGpus.FirstOrDefault();
                            LogToFile($"[GetHelpGpuInfo] Selected GPU: {gpu?.Name ?? "null"}");
                        }
                    }
                    catch (Exception innerEx)
                    {
                        LogToFile($"[GetHelpGpuInfo] GPU detection failed: {innerEx.Message}");
                    }

                    _lastDetectedGpu = gpu;
                }

                if (gpu == null)
                {
                    LogToFile("[GetHelpGpuInfo] Final GPU is null, returning Not detected");
                    return ("Not detected", true);
                }

                var gpuName = string.IsNullOrWhiteSpace(gpu.Name) ? "Unknown GPU" : gpu.Name;
                var vram = string.IsNullOrWhiteSpace(gpu.VideoMemoryGB) ? string.Empty : $" ({gpu.VideoMemoryGB} VRAM)";
                var result = $"{gpuName}{vram}";
                LogToFile($"[GetHelpGpuInfo] Returning: {result}");
                return (result, true);
            }
            catch (Exception ex)
            {
                LogToFile($"[GetHelpGpuInfo] CRASH: {ex.GetType().Name}: {ex.Message}");
                LogToFile($"[GetHelpGpuInfo] StackTrace: {ex.StackTrace}");
                return ("Detection failed", true);
            }
        }

        private string GetFriendlyOperatingSystemName()
        {
            try
            {
                LogToFile("[GetFriendlyOperatingSystemName] Starting...");

                if (!OperatingSystem.IsWindows())
                {
                    LogToFile("[GetFriendlyOperatingSystemName] Not Windows");
                    return RuntimeInformation.OSDescription;
                }

                LogToFile("[GetFriendlyOperatingSystemName] Opening registry...");
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key == null)
                {
                    LogToFile("[GetFriendlyOperatingSystemName] Registry key is null");
                    return RuntimeInformation.OSDescription;
                }

                var productName = key.GetValue("ProductName")?.ToString();
                var displayVersion = key.GetValue("DisplayVersion")?.ToString();
                var releaseId = key.GetValue("ReleaseId")?.ToString();
                var currentBuild = key.GetValue("CurrentBuild")?.ToString()
                                  ?? key.GetValue("CurrentBuildNumber")?.ToString();
                var ubrValue = key.GetValue("UBR")?.ToString();

                LogToFile($"[GetFriendlyOperatingSystemName] ProductName: {productName}");
                LogToFile($"[GetFriendlyOperatingSystemName] DisplayVersion: {displayVersion}");
                LogToFile($"[GetFriendlyOperatingSystemName] CurrentBuild: {currentBuild}");

                var versionPart = !string.IsNullOrWhiteSpace(displayVersion)
                    ? displayVersion
                    : releaseId;

                var buildPart = string.Empty;
                if (!string.IsNullOrWhiteSpace(currentBuild))
                {
                    buildPart = !string.IsNullOrWhiteSpace(ubrValue)
                        ? $"Build {currentBuild}.{ubrValue}"
                        : $"Build {currentBuild}";
                }

                var name = NormalizeWindowsProductName(productName, currentBuild);
                LogToFile($"[GetFriendlyOperatingSystemName] Normalized name: {name}");

                string result;
                if (!string.IsNullOrWhiteSpace(versionPart) && !string.IsNullOrWhiteSpace(buildPart))
                    result = $"{name} {versionPart} ({buildPart})";
                else if (!string.IsNullOrWhiteSpace(versionPart))
                    result = $"{name} {versionPart}";
                else if (!string.IsNullOrWhiteSpace(buildPart))
                    result = $"{name} ({buildPart})";
                else
                    result = name;

                LogToFile($"[GetFriendlyOperatingSystemName] Returning: {result}");
                return result;
            }
            catch (Exception ex)
            {
                LogToFile($"[GetFriendlyOperatingSystemName] CRASH: {ex.GetType().Name}: {ex.Message}");
                LogToFile($"[GetFriendlyOperatingSystemName] StackTrace: {ex.StackTrace}");
                return RuntimeInformation.OSDescription;
            }
        }

        private static string NormalizeWindowsProductName(string? productName, string? currentBuild)
        {
            var name = string.IsNullOrWhiteSpace(productName) ? "Windows" : productName;

            if (!int.TryParse(currentBuild, out var buildNumber))
                return name;

            if (buildNumber >= 22000 && name.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
            {
                return name.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
            }

            return name;
        }

        private Grid CreateResourceRow(string label, string version, bool showUpdateBadge, bool isLast,
            string? buttonName = null, EventHandler<RoutedEventArgs>? buttonClick = null, string? buttonText = null)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, isLast ? 0 : 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labelBlock = new TextBlock
            {
                Text = label,
                FontWeight = FontWeight.SemiBold,
                FontSize = 15,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = this.FindResource("BrTextPrimary") as IBrush
            };

            var rightStack = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };

            if (buttonClick != null)
            {
                var btn = new Button
                {
                    Content = buttonText ?? GetResourceString("TxtCheckUpdatesBtn", "Check Updates"),
                    Padding = new Thickness(10, 4),
                    Margin = new Thickness(0, 0, 12, 0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };
                if (!string.IsNullOrEmpty(buttonName)) btn.Name = buttonName;
                btn.Classes.Add("BtnBase");
                btn.Click += buttonClick;
                rightStack.Children.Add(btn);
            }

            if (showUpdateBadge)
            {
                var badge = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#2A1F4A")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 4),
                    Margin = new Thickness(0, 0, 12, 0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    IsVisible = _componentService.IsOptiScalerUpdateAvailable
                };
                badge.BorderBrush = this.FindResource("BrAccent") as IBrush;

                var badgeText = new TextBlock
                {
                    Text = GetResourceString("TxtUpdateAvail", "Update Available"),
                    FontSize = 11,
                    Foreground = this.FindResource("BrAccent") as IBrush
                };
                badge.Child = badgeText;
                rightStack.Children.Add(badge);
            }

            var versionBlock = new TextBlock
            {
                Text = version,
                FontWeight = FontWeight.Bold,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                MinWidth = buttonClick != null ? 80 : 0,
                TextAlignment = buttonClick != null ? TextAlignment.Right : TextAlignment.Left,
                Foreground = this.FindResource("BrAccent") as IBrush
            };
            rightStack.Children.Add(versionBlock);

            Grid.SetColumn(labelBlock, 0);
            Grid.SetColumn(rightStack, 1);
            grid.Children.Add(labelBlock);
            grid.Children.Add(rightStack);

            return grid;
        }

        private void RenderFeedback(StackPanel container)
        {
            var title = new TextBlock
            {
                Text = GetResourceString("TxtFeedbackTitle", "Feedback"),
                FontSize = 18,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = this.FindResource("BrTextPrimary") as IBrush
            };

            var border = new Border
            {
                Padding = new Thickness(16, 12),
                Margin = new Thickness(0, 0, 0, 24),
                BorderThickness = new Thickness(1),
                Background = this.FindResource("BrBgCard") as IBrush,
                BorderBrush = this.FindResource("BrBorderSubtle") as IBrush,
                CornerRadius = (CornerRadius)(this.FindResource("RadiusMedium") ?? new CornerRadius(8))
            };

            var stack = new StackPanel();

            var feedbackItems = new[]
            {
                GetResourceString("TxtFeedbackDesc", "We'd love to hear from you!"),
                GetResourceString("TxtFeedbackBugs", "• Report bugs and issues"),
                GetResourceString("TxtFeedbackFeatures", "• Suggest new features"),
                GetResourceString("TxtFeedbackImprovements", "• Share improvement ideas"),
                GetResourceString("TxtFeedbackSystem", "• Help us improve the system")
            };

            for (int i = 0; i < feedbackItems.Length; i++)
            {
                var text = new TextBlock
                {
                    Text = feedbackItems[i],
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, i == feedbackItems.Length - 1 ? 0 : (i == 0 ? 12 : 8)),
                    Foreground = this.FindResource("BrTextSecondary") as IBrush,
                    FontSize = (double)(this.FindResource("FontSizeBody") ?? 14.0)
                };
                stack.Children.Add(text);
            }

            border.Child = stack;
            container.Children.Add(title);
            container.Children.Add(border);
        }

        private void RenderDonate(StackPanel container)
        {
            var title = new TextBlock
            {
                Text = GetResourceString("TxtDonateTitle", "Support Development"),
                FontSize = 18,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = this.FindResource("BrTextPrimary") as IBrush
            };

            var border = new Border
            {
                Padding = new Thickness(16, 12),
                Margin = new Thickness(0, 0, 0, 40),
                BorderThickness = new Thickness(1),
                Background = this.FindResource("BrBgCard") as IBrush,
                BorderBrush = this.FindResource("BrBorderSubtle") as IBrush,
                CornerRadius = (CornerRadius)(this.FindResource("RadiusMedium") ?? new CornerRadius(8))
            };

            var stack = new StackPanel();

            var desc = new TextBlock
            {
                Text = GetResourceString("TxtDonateDesc", "OptiScaler Client is free and developed in my spare time. If it saved you some time, consider supporting its development on Ko-fi. It's entirely optional and never required to use the app."),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = this.FindResource("BrTextSecondary") as IBrush,
                FontSize = (double)(this.FindResource("FontSizeBody") ?? 14.0)
            };

            var kofiBtn = new Button
            {
                Content = GetResourceString("TxtDonateKofiBtn", "Support on Ko-fi"),
                Padding = new Thickness(16, 8),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
            };
            kofiBtn.Classes.Add("BtnBase");
            kofiBtn.Click += BtnKofi_Click;

            stack.Children.Add(desc);
            stack.Children.Add(kofiBtn);

            border.Child = stack;
            container.Children.Add(title);
            container.Children.Add(border);
        }

        private async void BtnKofi_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://ko-fi.com/agustinm28",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                await new ConfirmDialog(this, "Error", $"Could not open browser: {ex.Message}").ShowDialog<object>(this);
            }
        }

        private void RenderTextSection(StackPanel container, HelpSection section)
        {
            if (!string.IsNullOrEmpty(section.Title))
            {
                var title = new TextBlock
                {
                    Text = string.IsNullOrEmpty(section.TitleKey) ? section.Title : GetResourceString(section.TitleKey, section.Title),
                    FontSize = GetFontSize(18, section.FontSize),
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 0, 0, 12),
                    Foreground = this.FindResource("BrTextPrimary") as IBrush
                };
                container.Children.Add(title);
            }

            if (!string.IsNullOrEmpty(section.Content))
            {
                var content = new TextBlock
                {
                    Text = string.IsNullOrEmpty(section.ContentKey) ? section.Content : GetResourceString(section.ContentKey, section.Content),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 24),
                    Foreground = this.FindResource("BrTextSecondary") as IBrush,
                    FontSize = GetFontSize((double)(this.FindResource("FontSizeBody") ?? 14.0), section.FontSize)
                };
                container.Children.Add(content);
            }
        }

        private void RenderListSection(StackPanel container, HelpSection section)
        {
            if (!string.IsNullOrEmpty(section.Title))
            {
                var title = new TextBlock
                {
                    Text = string.IsNullOrEmpty(section.TitleKey) ? section.Title : GetResourceString(section.TitleKey, section.Title),
                    FontSize = GetFontSize(16, section.FontSize),
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 0, 0, 12),
                    Foreground = this.FindResource("BrTextPrimary") as IBrush
                };
                container.Children.Add(title);
            }

            if (section.Items != null)
            {
                foreach (var item in section.Items)
                {
                    // Check if it's a bullet point item (standalone, not inside a card)
                    if (item.Type == "bullet-point")
                    {
                        var bulletGrid = new Grid { Margin = new Thickness(24, 2, 0, 8) };
                        bulletGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        bulletGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                        var bullet = new TextBlock
                        {
                            Text = "•",
                            FontWeight = FontWeight.Bold,
                            FontSize = GetFontSize(14, item.FontSize),
                            Foreground = this.FindResource("BrAccent") as IBrush,
                            Margin = new Thickness(0, 0, 8, 0),
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
                        };

                        // Use SelectableTextBlock with Inlines for mixed formatting
                        var bulletText = new SelectableTextBlock
                        {
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = this.FindResource("BrTextSecondary") as IBrush,
                            FontSize = GetFontSize((double)(this.FindResource("FontSizeBody") ?? 14.0), item.FontSize),
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
                        };

                        if (!string.IsNullOrEmpty(item.Title))
                        {
                            // Add bold title
                            var titleRun = new Avalonia.Controls.Documents.Run($"{(string.IsNullOrEmpty(item.TitleKey) ? item.Title : GetResourceString(item.TitleKey, item.Title))}: ")
                            {
                                FontWeight = FontWeight.Bold,
                                Foreground = this.FindResource("BrTextPrimary") as IBrush
                            };
                            if (bulletText.Inlines != null)
                                bulletText.Inlines.Add(titleRun);

                            // Add regular text
                            var textRun = new Avalonia.Controls.Documents.Run(string.IsNullOrEmpty(item.TextKey) ? item.Text : GetResourceString(item.TextKey, item.Text));
                            if (bulletText.Inlines != null)
                                bulletText.Inlines.Add(textRun);
                        }
                        else
                        {
                            bulletText.Text = string.IsNullOrEmpty(item.TextKey) ? item.Text : GetResourceString(item.TextKey, item.Text);
                        }

                        Grid.SetColumn(bullet, 0);
                        Grid.SetColumn(bulletText, 1);
                        bulletGrid.Children.Add(bullet);
                        bulletGrid.Children.Add(bulletText);

                        container.Children.Add(bulletGrid);
                    }
                    else
                    {
                        // Regular card item (can have sub-items)
                        var itemBorder = new Border
                        {
                            Padding = new Thickness(16, 12),
                            Margin = new Thickness(0, 0, 0, 12),
                            BorderThickness = new Thickness(1),
                            Background = this.FindResource("BrBgSurface") as IBrush,
                            BorderBrush = this.FindResource("BrBorderSubtle") as IBrush,
                            CornerRadius = (CornerRadius)(this.FindResource("RadiusMedium") ?? new CornerRadius(8))
                        };

                        var itemStack = new StackPanel();

                        var label = new TextBlock
                        {
                            Text = string.IsNullOrEmpty(item.LabelKey) ? item.Label : GetResourceString(item.LabelKey, item.Label),
                            FontWeight = FontWeight.SemiBold,
                            FontSize = GetFontSize(14, item.FontSize),
                            Margin = new Thickness(0, 0, 0, 6),
                            Foreground = this.FindResource("BrTextPrimary") as IBrush
                        };

                        var text = new TextBlock
                        {
                            Text = string.IsNullOrEmpty(item.TextKey) ? item.Text : GetResourceString(item.TextKey, item.Text),
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = this.FindResource("BrTextSecondary") as IBrush,
                            FontSize = GetFontSize((double)(this.FindResource("FontSizeBody") ?? 14.0), item.FontSize)
                        };

                        itemStack.Children.Add(label);
                        itemStack.Children.Add(text);

                        // Optional external link (e.g. wiki/compatibility list)
                        if (!string.IsNullOrEmpty(item.Link))
                        {
                            var linkUrl = item.Link;
                            var linkText = string.IsNullOrEmpty(item.LinkLabelKey)
                                ? (item.LinkLabel ?? linkUrl)
                                : GetResourceString(item.LinkLabelKey, item.LinkLabel ?? linkUrl);

                            var linkButton = new Button
                            {
                                Content = linkText,
                                Classes = { "Link" },
                                Background = Brushes.Transparent,
                                BorderThickness = new Thickness(0),
                                Padding = new Thickness(0),
                                Margin = new Thickness(0, 8, 0, 0),
                                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                                FontSize = GetFontSize((double)(this.FindResource("FontSizeBody") ?? 14.0), item.FontSize),
                                Foreground = this.FindResource("BrAccent") as IBrush,
                                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
                            };
                            linkButton.Click += (s, e) =>
                            {
                                try
                                {
                                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                    {
                                        FileName = linkUrl,
                                        UseShellExecute = true
                                    });
                                }
                                catch (Exception ex)
                                {
                                    DebugWindow.Log($"[Help] Could not open link '{linkUrl}': {ex.Message}");
                                }
                            };

                            itemStack.Children.Add(linkButton);
                        }

                        // Add sub-items (bullet points) if they exist
                        if (item.Items != null)
                        {
                            var bulletContainer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

                            foreach (var subItem in item.Items)
                            {
                                var bulletGrid = new Grid { Margin = new Thickness(0, 2, 0, 4) };
                                bulletGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                                bulletGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                                var bullet = new TextBlock
                                {
                                    Text = "•",
                                    FontWeight = FontWeight.Bold,
                                    FontSize = GetFontSize(13, subItem.FontSize),
                                    Foreground = this.FindResource("BrAccent") as IBrush,
                                    Margin = new Thickness(0, 0, 8, 0),
                                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
                                };

                                // Use SelectableTextBlock with Inlines for mixed formatting
                                var bulletText = new SelectableTextBlock
                                {
                                    TextWrapping = TextWrapping.Wrap,
                                    Foreground = this.FindResource("BrTextSecondary") as IBrush,
                                    FontSize = GetFontSize(12, subItem.FontSize),
                                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
                                };

                                if (!string.IsNullOrEmpty(subItem.Title))
                                {
                                    // Add bold title
                                    var titleRun = new Avalonia.Controls.Documents.Run($"{(string.IsNullOrEmpty(subItem.TitleKey) ? subItem.Title : GetResourceString(subItem.TitleKey, subItem.Title))}: ")
                                    {
                                        FontWeight = FontWeight.Bold,
                                        Foreground = this.FindResource("BrTextPrimary") as IBrush
                                    };
                                    if (bulletText.Inlines != null)
                                        bulletText.Inlines.Add(titleRun);

                                    // Add regular text
                                    var textRun = new Avalonia.Controls.Documents.Run(string.IsNullOrEmpty(subItem.TextKey) ? subItem.Text : GetResourceString(subItem.TextKey, subItem.Text));
                                    if (bulletText.Inlines != null)
                                        bulletText.Inlines.Add(textRun);
                                }
                                else
                                {
                                    bulletText.Text = string.IsNullOrEmpty(subItem.TextKey) ? subItem.Text : GetResourceString(subItem.TextKey, subItem.Text);
                                }

                                Grid.SetColumn(bullet, 0);
                                Grid.SetColumn(bulletText, 1);
                                bulletGrid.Children.Add(bullet);
                                bulletGrid.Children.Add(bulletText);

                                bulletContainer.Children.Add(bulletGrid);
                            }

                            itemStack.Children.Add(bulletContainer);
                        }

                        itemBorder.Child = itemStack;
                        container.Children.Add(itemBorder);
                    }
                }
            }

            var spacer = new Border { Height = 16 };
            container.Children.Add(spacer);
        }

        private async void BtnUpdateFakenvapi_Click(object? sender, RoutedEventArgs e)
        {
            var btnUpdateFakenvapi = this.FindControl<Button>("BtnUpdateFakenvapi");
            if (btnUpdateFakenvapi == null) return;

            btnUpdateFakenvapi.IsEnabled = false;
            var originalContent = btnUpdateFakenvapi.Content;
            btnUpdateFakenvapi.Content = "Checking...";
            try
            {
                await _componentService.CheckForUpdatesAsync();

                if (_componentService.IsFakenvapiUpdateAvailable || string.IsNullOrEmpty(_componentService.FakenvapiVersion))
                {
                    btnUpdateFakenvapi.Content = "Downloading...";
                    await _componentService.DownloadAndExtractFakenvapiAsync();
                    await new ConfirmDialog(this, "Success", "Fakenvapi downloaded successfully.").ShowDialog<object>(this);
                    PopulateHelpContent();
                }
                else
                {
                    await new ConfirmDialog(this, "Up to date", "You already have the latest version of Fakenvapi.").ShowDialog<object>(this);
                }
            }
            catch (Exception ex)
            {
                await new ConfirmDialog(this, "Error", $"Error updating Fakenvapi: {ex.Message}").ShowDialog<object>(this);
            }
            finally
            {
                btnUpdateFakenvapi.Content = originalContent;
                btnUpdateFakenvapi.IsEnabled = true;
            }
        }

        private async void BtnUpdateNukemFG_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                bool isUpdate = _componentService.IsNukemFGInstalled;
                DebugWindow.Log($"[NukemFG] Starting manual {(isUpdate ? "update" : "install")}");

                bool result = await _componentService.ProvideNukemFGManuallyAsync(isUpdate);

                if (result)
                {
                    DebugWindow.Log("[NukemFG] Manual process completed successfully.");
                    PopulateHelpContent();
                }
                else
                {
                    DebugWindow.Log("[NukemFG] Manual process cancelled or failed.");
                }
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[NukemFG] Error: {ex.Message}");
                await new ConfirmDialog(this, "Error", $"Error installing NukemFG: {ex.Message}").ShowDialog<object>(this);
            }
        }

        private async void BtnCheckUpdates_Click(object? sender, RoutedEventArgs e)
        {
            var btnCheckUpdates = this.FindControl<Button>("BtnCheckUpdates");
            if (btnCheckUpdates == null) return;

            btnCheckUpdates.IsEnabled = false;
            var originalContent = btnCheckUpdates.Content;
            btnCheckUpdates.Content = GetResourceString("TxtCheckingUpdates", "Checking…");

            try
            {
                // 1. Check for component updates (Fakenvapi, etc)
                await _componentService.CheckForUpdatesAsync();
                PopulateHelpContent();

                // 2. Check for App Updates
                var appUpdateService = new AppUpdateService(_componentService);
                bool hasUpdate = await appUpdateService.CheckForAppUpdateAsync();

                if (hasUpdate && !AppUpdateService.IsInstallDirWritable())
                {
                    // Package-managed install (AUR, Nix store, /usr, ...) - the install dir
                    // isn't writable, so self-updating would fail (or worse, fight the next
                    // package-manager update). Point the user at their package manager instead.
                    var managedTitle = GetResourceString("TxtUpdateAvailableTitle", "Update Available");
                    var managedMsgFormat = GetResourceString("TxtUpdateManagedInstallMsg", "A new version is available (v{0}), but this installation appears to be managed by a package manager (e.g. AUR, Nix). Please update it from there instead of in-app.");
                    var managedMsg = string.Format(managedMsgFormat, appUpdateService.LatestVersion);
                    await new ConfirmDialog(this, managedTitle, managedMsg).ShowDialog<object>(this);
                }
                else if (hasUpdate)
                {
                    var updateTitle = GetResourceString("TxtUpdateAvailableTitle", "Update Available");
                    var updateMsgFormat = GetResourceString("TxtUpdateAvailableMsg", "A new version is available (v{0}). Download now?");
                    var updateMsg = string.Format(updateMsgFormat, appUpdateService.LatestVersion);

                    var dialog = new ConfirmDialog(this, updateTitle, updateMsg, false);
                    if (await dialog.ShowDialog<bool>(this)) // true if confirmed
                    {
                        btnCheckUpdates.Content = GetResourceString("TxtUpdatingApp", "Updating...");

                        await appUpdateService.DownloadAndPrepareUpdateAsync(new Progress<double>(p => {
                            btnCheckUpdates.Content = $"{GetResourceString("TxtUpdatingApp", "Updating")} ({p:F0}%)";
                        }));

                        var readyTitle = GetResourceString("TxtUpdateReady", "Update Ready");
                        var readyMsg = GetResourceString("TxtUpdateReadyMsg", "Update downloaded. Restarting...");

                        await new ConfirmDialog(this, readyTitle, readyMsg).ShowDialog<object>(this);

                        appUpdateService.FinalizeAndRestart();

                        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                        {
                            desktop.Shutdown();
                        }
                    }
                }
                else if (appUpdateService.IsError)
                {
                    var errorMsg = GetResourceString("TxtUpdateCheckError", "There was a problem checking for updates.");
                    await new ConfirmDialog(this, GetResourceString("TxtUpdateError", "Error"), errorMsg).ShowDialog<object>(this);
                }
                else
                {
                    var noUpdateMsg = GetResourceString("TxtNoUpdateFound", "No new updates found.");
                    await new ConfirmDialog(this, GetResourceString("TxtReady", "Updates"), noUpdateMsg).ShowDialog<object>(this);
                }
            }
            catch (ManagedInstallException)
            {
                var managedTitle = GetResourceString("TxtUpdateAvailableTitle", "Update Available");
                var managedMsg = GetResourceString("TxtUpdateManagedInstallMsgGeneric", "This installation appears to be managed by a package manager (e.g. AUR, Nix). Please update it from there instead of in-app.");
                await new ConfirmDialog(this, managedTitle, managedMsg).ShowDialog<object>(this);
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[AppUpdate] Fatal exception: {ex.Message}");
                var errorTitle = GetResourceString("TxtUpdateError", "Error");
                await new ConfirmDialog(this, errorTitle, $"Error: {ex.Message}").ShowDialog<object>(this);
            }
            finally
            {
                btnCheckUpdates.Content = originalContent;
                btnCheckUpdates.IsEnabled = true;
            }
        }

        private async void BtnGithub_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var repoOwner = _componentService.Config.App.RepoOwner ?? "Optiscaler-Client";
                var repoName = _componentService.Config.App.RepoName ?? "Optiscaler-Client";
                var url = $"https://github.com/{repoOwner}/{repoName}";

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                await new ConfirmDialog(this, "Error", $"Could not open browser: {ex.Message}").ShowDialog<object>(this);
            }
        }

        private bool LoadSavedGames(CancellationToken cancellationToken)
        {
            var savedGames = _persistenceService.LoadGames();
            // Honour persisted display order on startup
            _allGames = savedGames.OrderBy(g => g.DisplayOrder).ToList();
            // Normalise DisplayOrder so it's 0-based and gapless
            for (int i = 0; i < _allGames.Count; i++) _allGames[i].DisplayOrder = i;

            // Migrate legacy in-folder backups to external store (idempotent, guarded by version check)
            try
            {
                var migrationService = new StartupMigrationService(new BackupStoreService(), _componentService);
                migrationService.RunIfNeeded(_allGames);
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[MainWindow] Startup migration error (non-fatal): {ex.Message}");
            }

            ApplyFilter(_txtSearch?.Text);

            var loadedFormat = GetResourceString("TxtLoadedGamesFormat", "Loaded {0} games.");
            if (_txtStatus != null) _txtStatus.Text = string.Format(loadedFormat, savedGames.Count);

            if (savedGames.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    GameAnalyzerService.LoadCacheFromDisk();
                    using var coverSemaphore = new SemaphoreSlim(6, 6);
                    var coverTasks = new List<Task>();
                    var analyzedCount = 0;

                    foreach (var game in savedGames)
                    {
                        if (cancellationToken.IsCancellationRequested) return;

                        try { _analyzerService.AnalyzeGame(game); }
                        catch (Exception ex) { DebugWindow.Log($"[MainWindow] AnalyzeGame failed for {game.Name}: {ex.Message}"); }

                        if (string.IsNullOrEmpty(game.CoverImageUrl) || game.CoverImageUrl.StartsWith("http"))
                        {
                            var appIdKey = !string.IsNullOrEmpty(game.AppId) ? game.AppId :
                                         !string.IsNullOrEmpty(game.Name) ? game.Name : Guid.NewGuid().ToString();

                            await coverSemaphore.WaitAsync(cancellationToken);
                            coverTasks.Add(Task.Run(async () =>
                            {
                                try
                                {
                                    game.CoverImageUrl = await _metadataService.FetchAndCacheCoverImageAsync(game.Name, appIdKey);
                                }
                                catch
                                {
                                }
                                finally
                                {
                                    coverSemaphore.Release();
                                }
                            }, cancellationToken));
                        }

                        analyzedCount++;
                        if (analyzedCount % 4 == 0)
                        {
                            await Task.Delay(1, cancellationToken);
                        }
                    }

                    try
                    {
                        await Task.WhenAll(coverTasks);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (cancellationToken.IsCancellationRequested) return;

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (cancellationToken.IsCancellationRequested) return;

                        RefreshGameLists();
                        _persistenceService.SaveGames(savedGames);
                    });
                }, cancellationToken);
            }

            return savedGames.Count > 0;
        }

        private async void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var prompt = new InitialScanPromptWindow(this, _componentService, isFirstTime: false);
                var options = await prompt.ShowDialog<InitialScanOptions?>(this);
                if (options == null)
                    return;

                if (options.RefreshCoversOnly)
                {
                    await RunRefreshCoversAsync();
                    return;
                }

                _componentService.Config.ScanSources = options.ScanSources;
                _componentService.Config.ScanDriveRoots = options.DriveRoots;
                _componentService.Config.HasCompletedInitialScan = true;
                _componentService.SaveConfiguration();

                await RunScanAsync(options.UpscalerFilter);
            }
            catch (Exception ex) { DebugWindow.Log($"[MainWindow] Scan failed: {ex.Message}"); }
        }

        private async Task RunRefreshCoversAsync()
        {
            if (_btnScan != null) _btnScan.IsEnabled = false;
            if (_txtStatus != null) _txtStatus.Text = GetResourceString("TxtRefreshingCovers", "Refreshing missing covers...");

            try
            {
                var missing = _games
                    .Where(g => string.IsNullOrEmpty(g.CoverImageUrl))
                    .ToList();

                if (missing.Count == 0)
                {
                    if (_txtStatus != null) _txtStatus.Text = GetResourceString("TxtReady", "Ready");
                    return;
                }

                // Delete sentinels so FetchAndCacheCoverImageAsync tries again
                foreach (var game in missing)
                {
                    var key = !string.IsNullOrEmpty(game.AppId) ? game.AppId : game.Name;
                    _metadataService.DeleteSentinel(key);
                }

                using var sem = new SemaphoreSlim(6, 6);
                var tasks = missing.Select(game =>
                {
                    var key = !string.IsNullOrEmpty(game.AppId) ? game.AppId : game.Name;
                    return Task.Run(async () =>
                    {
                        await sem.WaitAsync();
                        try { game.CoverImageUrl = await _metadataService.FetchAndCacheCoverImageAsync(game.Name, key); }
                        finally { sem.Release(); }
                    });
                }).ToList();

                await Task.WhenAll(tasks);

                _persistenceService.SaveGames(_games);
                ApplyFilter(_txtSearch?.Text);

                var found = missing.Count(g => !string.IsNullOrEmpty(g.CoverImageUrl));
                ShowToast(string.Format(GetResourceString("TxtCoverRefreshToastFmt", "Cover refresh complete — {0}/{1} covers found."), found, missing.Count));
                _ = HideToastAfterAsync(3500);
                if (_txtStatus != null)
                    _txtStatus.Text = GetResourceString("TxtReady", "Ready");
            }
            catch (Exception ex)
            {
                HideToast();
                await new ConfirmDialog(this, "Error", ex.Message).ShowDialog<object>(this);
                if (_txtStatus != null) _txtStatus.Text = GetResourceString("TxtReady", "Ready");
            }
            finally
            {
                if (_btnScan != null) _btnScan.IsEnabled = true;
            }
        }

        private async Task RunScanAsync(UpscalerFilterMode upscalerFilter = UpscalerFilterMode.ShowAll)
        {
            if (_btnScan != null) _btnScan.IsEnabled = false;
            if (_txtStatus != null) _txtStatus.Text = GetResourceString("TxtScanningShort", "Scanning for games...");
            if (_overlayScanning != null) _overlayScanning.IsVisible = true;

            try
            {
                List<Game> scanResults;
                if (_scannerService != null)
                {
                    var allowedDrives = _componentService.Config.ScanDriveRoots;
                    scanResults = await _scannerService.ScanAllGamesAsync(
                        _componentService.Config.ScanSources,
                        (allowedDrives != null && allowedDrives.Count > 0) ? allowedDrives : null);
                }
                else
                {
                    scanResults = new List<Game>();
                }

                var manualGames = _games.Where(g => g.Platform == GamePlatform.Manual).ToList();

                var existingGames = _games.ToDictionary(
                    g => g.InstallPath,
                    g => g,
                    StringComparer.OrdinalIgnoreCase);

                _games.Clear();

                foreach (var manualGame in manualGames)
                {
                    _analyzerService.AnalyzeGame(manualGame);
                    _games.Add(manualGame);
                }

                foreach (var scannedGame in scanResults)
                {
                    if (!_games.Any(g => g.InstallPath.Equals(scannedGame.InstallPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        bool lacksUpscaler = !scannedGame.HasUpscaler;

                        // SkipWithoutUpscaler: do not add the game at all
                        if (upscalerFilter == UpscalerFilterMode.SkipWithoutUpscaler && lacksUpscaler)
                            continue;

                        if (existingGames.TryGetValue(scannedGame.InstallPath, out var existing) &&
                            !string.IsNullOrEmpty(existing.CoverImageUrl) &&
                            !existing.CoverImageUrl.StartsWith("http"))
                        {
                            scannedGame.CoverImageUrl = existing.CoverImageUrl;
                        }

                        // HideWithoutUpscaler: add the game but mark it hidden
                        if (upscalerFilter == UpscalerFilterMode.HideWithoutUpscaler && lacksUpscaler)
                            scannedGame.IsHidden = true;

                        _games.Add(scannedGame);
                    }
                }

                _allGames = _games.ToList();
                _hasScanned = true;
                ApplyFilter(_txtSearch?.Text);

                var scanCompleteFormat = GetResourceString("TxtScanCompleteFormat", "Scan complete. Total games: {0}");
                if (_txtStatus != null) _txtStatus.Text = string.Format(scanCompleteFormat, _games.Count);

                if (_overlayScanning != null) _overlayScanning.IsVisible = false;
                if (_btnScan != null) _btnScan.IsEnabled = true;

                var gamesNeedingCovers = _games
                    .Where(g => string.IsNullOrEmpty(g.CoverImageUrl) || g.CoverImageUrl.StartsWith("http"))
                    .ToList();

                if (gamesNeedingCovers.Count > 0)
                {
                    var fetchingCoversFormat = GetResourceString("TxtFetchingCoversFormat", "Fetching covers: {0}/{1}...");
                    ShowToast(string.Format(fetchingCoversFormat, 0, gamesNeedingCovers.Count),
                              showProgress: true, progressPercent: 0);
                    if (_txtStatus != null) _txtStatus.Text = string.Format(fetchingCoversFormat, 0, gamesNeedingCovers.Count);

                    var completed = 0;
                    var total = gamesNeedingCovers.Count;
                    using var coverSemaphore = new SemaphoreSlim(6, 6);
                    var coverTasks = gamesNeedingCovers.Select(game =>
                    {
                        var appIdKey = !string.IsNullOrEmpty(game.AppId) ? game.AppId : game.Name;
                        return Task.Run(async () =>
                        {
                            await coverSemaphore.WaitAsync();
                            try
                            {
                                game.CoverImageUrl = await _metadataService.FetchAndCacheCoverImageAsync(game.Name, appIdKey);
                            }
                            finally
                            {
                                coverSemaphore.Release();
                                var done = System.Threading.Interlocked.Increment(ref completed);
                                var pct = (double)done / total * 100.0;
                                var msg = string.Format(fetchingCoversFormat, done, total);
                                Dispatcher.UIThread.Post(() =>
                                {
                                    UpdateToastProgress(msg, pct);
                                    if (_txtStatus != null) _txtStatus.Text = msg;
                                });
                            }
                        });
                    }).ToList();

                    await Task.WhenAll(coverTasks);

                    _persistenceService.SaveGames(_games);

                    var coversCompleteFormat = GetResourceString("TxtCoversCompleteFmt", "Covers loaded. Total games: {0}");
                    Dispatcher.UIThread.Post(() =>
                    {
                        HideToast();
                        if (_txtStatus != null) _txtStatus.Text = string.Format(coversCompleteFormat, _games.Count);
                        RefreshGameLists();
                    });
                }
                else
                {
                    _persistenceService.SaveGames(_games);
                }
            }
            catch (Exception ex)
            {
                var errorFormat = GetResourceString("TxtErrorFormat", "Error: {0}");
                if (_txtStatus != null) _txtStatus.Text = string.Format(errorFormat, ex.Message);
                HideToast();
                await new ConfirmDialog(this, "Error", ex.Message).ShowDialog<object>(this);
            }
            finally
            {
                if (_btnScan != null) _btnScan.IsEnabled = true;
                if (_overlayScanning != null) _overlayScanning.IsVisible = false;
            }
        }

        private async void BtnAddManual_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var files = await this.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = GetResourceString("TxtSelectExe", "Select Game Executable"),
                    AllowMultiple = false,
                    FileTypeFilter = new List<FilePickerFileType>
                    {
                        new FilePickerFileType("Executable Files (*.exe)")
                        {
                            Patterns = new List<string> { "*.exe" }
                        }
                    }
                });

                if (files != null && files.Count > 0)
                {
                    var filePath = files[0].Path.LocalPath;
                    var fileName = System.IO.Path.GetFileNameWithoutExtension(filePath);
                    var installDir = System.IO.Path.GetDirectoryName(filePath) ?? "";

                    var newGame = new Game
                    {
                        Name = fileName,
                        InstallPath = installDir,
                        ExecutablePath = filePath,
                        Platform = GamePlatform.Manual,
                        AppId = "Manual_" + Guid.NewGuid().ToString().Substring(0, 8)
                    };

                    _analyzerService.AnalyzeGame(newGame);
                    newGame.CoverImageUrl = await _metadataService.FetchAndCacheCoverImageAsync(newGame.Name, newGame.AppId);

                    _games.Insert(0, newGame);
                    _allGames = _games.ToList();
                    _persistenceService.SaveGames(_games);

                    RefreshGameLists();

                    if (_txtStatus != null) _txtStatus.Text = string.Format(GetResourceString("TxtAddedRefFormat", "Added {0} manually."), newGame.Name);
                }
            }
            catch (Exception ex)
            {
                await new ConfirmDialog(this, GetResourceString("TxtError", "Error"), ex.Message, isAlert: true).ShowDialog<object>(this);
            }
        }

        private async void BtnBulkInstall_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_games.Count == 0)
                {
                    await new ConfirmDialog(
                        this,
                        GetResourceString("TxtNoGames", "No Games"),
                        GetResourceString("TxtNoGamesFound", "No games found. Please scan for games first."),
                        isAlert: true
                    ).ShowDialog<bool>(this);
                    return;
                }

                var installService = new GameInstallationService();
                var bulkWindow = new BulkInstallWindow(_componentService, installService, _games.ToList(), owner: this);
                await bulkWindow.ShowDialog<object>(this);

                // Persist state and refresh game list after bulk install
                _persistenceService.SaveGames(_games);
                GameAnalyzerService.FlushCacheToDisk();
                RefreshGameLists();
            }
            catch (Exception ex) { DebugWindow.Log($"[MainWindow] Bulk install dialog failed: {ex.Message}"); }
        }

        private async void BtnManage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button button && button.DataContext is Game selectedGame)
                {
                    var manageWindow = new ManageGameWindow(this, selectedGame);
                    await manageWindow.ShowDialog<object>(this);

                    var index = _games.IndexOf(selectedGame);
                    if (index != -1)
                    {
                        _games[index] = selectedGame;
                        _persistenceService.SaveGames(_games);
                        GameAnalyzerService.FlushCacheToDisk();
                    }

                    RefreshGameLists();
                }
            }
            catch (Exception ex) { DebugWindow.Log($"[MainWindow] Manage game dialog failed: {ex.Message}"); }
        }

        private void BtnFastInstall_Loaded(object? sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is Game game)
            {
                UpdateFastInstallButton(button, game);
            }
        }

        private void UpdateFastInstallButton(Button button, Game game)
        {
            if (game.IsOptiscalerInstalled)
            {
                button.Content = GetResourceString("TxtQuickUninstall", "🗑️ Quick Uninstall");
                button.Foreground = this.FindResource("BrAccentWarm") as IBrush ?? Brushes.Orange;
            }
            else
            {
                button.Content = GetResourceString("TxtQuickInstall", "✦ Quick Install");
                button.Foreground = this.FindResource("BrAccent") as IBrush ?? Brushes.Purple;
            }
        }

        private void SetQuickInstallLoading(Button button)
        {
            bool isGridButton = string.Equals(button.Name, "BtnFastInstallGrid", StringComparison.Ordinal);

            if (!_quickInstallOriginalMinWidths.ContainsKey(button))
            {
                _quickInstallOriginalMinWidths[button] = button.MinWidth;
            }

            var minWidth = button.Bounds.Width;
            if (minWidth <= 0) minWidth = isGridButton ? 128 : 140;
            button.MinWidth = Math.Max(button.MinWidth, minWidth);

            var spinner = new ProgressBar
            {
                IsIndeterminate = true,
                Width = 26,
                Height = 6,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Background = Brushes.Transparent
            };

            var dot1 = new Ellipse { Width = 5, Height = 5, Fill = this.FindResource("BrAccent") as IBrush ?? Brushes.Purple, Margin = new Thickness(2, 0) };
            var dot2 = new Ellipse { Width = 5, Height = 5, Fill = this.FindResource("BrAccent") as IBrush ?? Brushes.Purple, Margin = new Thickness(2, 0) };
            var dot3 = new Ellipse { Width = 5, Height = 5, Fill = this.FindResource("BrAccent") as IBrush ?? Brushes.Purple, Margin = new Thickness(2, 0) };

            var t1 = new Avalonia.Media.TranslateTransform();
            var t2 = new Avalonia.Media.TranslateTransform();
            var t3 = new Avalonia.Media.TranslateTransform();
            dot1.RenderTransform = t1;
            dot2.RenderTransform = t2;
            dot3.RenderTransform = t3;

            var dots = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            dots.Children.Add(dot1);
            dots.Children.Add(dot2);
            dots.Children.Add(dot3);

            var stack = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = isGridButton ? 6 : 8,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };

            if (isGridButton)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = "✦",
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = this.FindResource("BrAccent") as IBrush ?? Brushes.Purple
                });
            }
            else
            {
                stack.Children.Add(new TextBlock
                {
                    Text = "✦",
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = this.FindResource("BrAccent") as IBrush ?? Brushes.Purple
                });
            }
            stack.Children.Add(dots);

            button.Content = stack;
            // Don't set IsEnabled = false (styles make it very transparent);
            // make it non-interactive via hit testing and keep full opacity so animation remains visible.
            button.IsHitTestVisible = false;
            button.Opacity = 1.0;
            button.Foreground = this.FindResource("BrTextSecondary") as IBrush ?? Brushes.Gray;

            if (_quickInstallDotTimers.TryGetValue(button, out var existing))
            {
                existing.Stop();
            }

            _quickInstallDotPhases[button] = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            timer.Tick += (s, e) =>
            {
                if (!_quickInstallDotPhases.ContainsKey(button)) return;
                _quickInstallDotPhases[button] += 0.25;
                var phase = _quickInstallDotPhases[button];
                const double amplitude = 6;
                const double phaseOffset = Math.PI * 2 / 3;
                t1.Y = -amplitude * Math.Max(0, Math.Sin(phase));
                t2.Y = -amplitude * Math.Max(0, Math.Sin(phase + phaseOffset));
                t3.Y = -amplitude * Math.Max(0, Math.Sin(phase + phaseOffset * 2));
            };
            _quickInstallDotTimers[button] = timer;
            timer.Start();
        }

        private void ClearQuickInstallLoading(Button button, Game game)
        {
            // Restore interactivity and visual state
            button.IsHitTestVisible = true;
            button.Opacity = 1.0;
            UpdateFastInstallButton(button, game);

            if (_quickInstallDotTimers.TryGetValue(button, out var timer))
            {
                timer.Stop();
                _quickInstallDotTimers.Remove(button);
            }
            _quickInstallDotPhases.Remove(button);

            if (_quickInstallOriginalMinWidths.TryGetValue(button, out var originalMinWidth))
            {
                button.MinWidth = originalMinWidth;
                _quickInstallOriginalMinWidths.Remove(button);
            }
        }

        private async void BtnFastInstall_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is Game selectedGame)
            {
                try
                {
                    // Always refresh install state from disk before deciding install/uninstall path.
                    GameAnalyzerService.InvalidateCacheForPath(selectedGame.InstallPath);
                    _analyzerService.AnalyzeGame(selectedGame, forceRefresh: true);

                    // Check if OptiScaler is already installed
                    if (selectedGame.IsOptiscalerInstalled)
                    {
                        // Uninstall OptiScaler directly without confirmation
                        var installService = new GameInstallationService();
                        installService.UninstallOptiScaler(selectedGame);

                        // Update game status
                        selectedGame.IsOptiscalerInstalled = false;
                        selectedGame.OptiscalerVersion = null;

                        // Refresh UI
                        RefreshGameLists();

                        _persistenceService.SaveGames(_games);
                        GameAnalyzerService.FlushCacheToDisk();
                    }
                    else
                    {
                        // Install OptiScaler
                        var installService = new GameInstallationService();

                        // Determine version to install: use configured default, fall back to latest per channel
                        string versionToInstall;

                        var configuredDefault = _componentService.EffectiveDefaultOptiScalerVersion;
                        if (!string.IsNullOrEmpty(configuredDefault))
                        {
                            versionToInstall = configuredDefault;
                        }
                        else
                        {
                            versionToInstall = _componentService.LatestStableVersion ?? "";
                        }

                        if (string.IsNullOrEmpty(versionToInstall))
                        {
                            await new ConfirmDialog(
                                this,
                                GetResourceString("TxtNoVersions", "No Versions Available"),
                                GetResourceString("TxtNoVersionsFound", "No OptiScaler versions are available for installation."),
                                isAlert: true
                            ).ShowDialog<bool>(this);
                            return;
                        }

                        if (ComponentManagementService.IsOptiScalerDownloadActive(versionToInstall))
                        {
                            var inProgressFmtMain = GetResourceString("TxtDownloadInProgressFormat", "A download is already in progress for v{0}.");
                            ShowSecondaryToast(string.Format(inProgressFmtMain, versionToInstall));
                            return;
                        }

                        // Get cache paths
                        var optiCacheDir = _componentService.GetOptiScalerCachePath(versionToInstall);

                        // Download OptiScaler if not in cache
                        if (!Directory.Exists(optiCacheDir) || Directory.GetFiles(optiCacheDir, "*.*", SearchOption.AllDirectories).Length == 0)
                        {
                            SetQuickInstallLoading(button);
                            ShowToast(string.Format(GetResourceString("TxtInstallingFormat", "Downloading OptiScaler v{0}... {1}%"), versionToInstall, 0), showProgress: true, progressPercent: 0);

                            try
                            {
                                var progress = new Progress<double>(p =>
                                {
                                    UpdateToastProgress(string.Format(GetResourceString("TxtInstallingFormat", "Downloading OptiScaler v{0}... {1}%"), versionToInstall, (int)p), p);
                                });

                                await _componentService.DownloadOptiScalerAsync(versionToInstall, progress);
                                ShowToast(string.Format(GetResourceString("TxtExtractingFormat", "Extracting and installing v{0}..."), versionToInstall), showProgress: true, progressPercent: null);
                            }
                            catch (Exception downloadEx)
                            {
                                if (downloadEx is VersionUnavailableException vex &&
                                    vex.Message.Contains("Download already in progress", StringComparison.OrdinalIgnoreCase))
                                {
                                    var inProgressFmtVex = GetResourceString("TxtDownloadInProgressFormat", "A download is already in progress for v{0}.");
                                    ShowSecondaryToast(string.Format(inProgressFmtVex, vex.Version));
                                    return;
                                }

                                HideToast();

                                // Show error dialog
                                await new ConfirmDialog(
                                    this,
                                    GetResourceString("TxtError", "Error"),
                                    $"Failed to download OptiScaler {versionToInstall}: {downloadEx.Message}",
                                    isAlert: true
                                ).ShowDialog<bool>(this);
                                return;
                            }
                        }

                        // ── Determine Fakenvapi / NukemFG install based on configured defaults
                        // For OptiScaler >= 0.9, these components are bundled; skip them
                        bool versionIncludesBundled = false;
                        {
                            var vMatch = System.Text.RegularExpressions.Regex.Match(versionToInstall, @"^v?(\d+(?:\.\d+)*)");
                            if (vMatch.Success && Version.TryParse(vMatch.Groups[1].Value, out var parsedVer))
                                versionIncludesBundled = parsedVer.Major > 0 || parsedVer.Minor >= 9;
                        }

                        var configuredFakenvapi = versionIncludesBundled ? null : _componentService.Config.DefaultFakenvapiVersion;
                        bool installFakenvapi = !string.IsNullOrEmpty(configuredFakenvapi) &&
                                                !configuredFakenvapi.Equals("none", StringComparison.OrdinalIgnoreCase);

                        var configuredNukemFG = versionIncludesBundled ? null : _componentService.Config.DefaultNukemFGVersion;
                        bool installNukemFG = !string.IsNullOrEmpty(configuredNukemFG) &&
                                              !configuredNukemFG.Equals("none", StringComparison.OrdinalIgnoreCase);

                        var fakeCacheDir = installFakenvapi
                            ? _componentService.GetFakenvapiCachePath(configuredFakenvapi!)
                            : _componentService.GetFakenvapiCachePath();
                        var nukemCacheDir = installNukemFG
                            ? _componentService.GetNukemFGCachePath(configuredNukemFG!)
                            : _componentService.GetNukemFGCachePath();

                        // Download Fakenvapi on-demand if not cached
                        if (installFakenvapi && !_componentService.IsFakenvapiCached(configuredFakenvapi!))
                        {
                            try
                            {
                                ShowToast($"Downloading Fakenvapi v{configuredFakenvapi}...", showProgress: true, progressPercent: 0);
                                var fakeProgress = new Progress<double>(p =>
                                    UpdateToastProgress($"Downloading Fakenvapi v{configuredFakenvapi}... {(int)p}%", p));
                                fakeCacheDir = await _componentService.DownloadFakenvapiAsync(configuredFakenvapi!, fakeProgress);
                            }
                            catch (Exception ex)
                            {
                                HideToast();
                                await new ConfirmDialog(this, GetResourceString("TxtWarning", "Warning"),
                                    $"Failed to download Fakenvapi v{configuredFakenvapi}: {ex.Message}", isAlert: true)
                                    .ShowDialog<bool>(this);
                                installFakenvapi = false;
                            }
                        }

                        // Resolve the configured default profile (null = built-in default → no .ini written)
                        var profileService = new ProfileManagementService();
                        var defaultProfileName = _componentService.Config.DefaultProfileName ?? OptiScalerProfile.BuiltInDefaultName;
                        OptiScalerProfile? defaultProfile = null;
                        if (!string.Equals(defaultProfileName, OptiScalerProfile.BuiltInDefaultName, StringComparison.OrdinalIgnoreCase))
                            defaultProfile = profileService.GetProfileByName(defaultProfileName);

                        // Install with default settings (backup always enabled)
                        SetQuickInstallLoading(button);
                        string? resolvedGameDir = null;
                        await Task.Run(() =>
                        {
                            resolvedGameDir = installService.InstallOptiScaler(
                                selectedGame,
                                optiCacheDir,
                                "dxgi.dll",
                                installFakenvapi: installFakenvapi,
                                fakenvapiCachePath: fakeCacheDir,
                                installNukemFG: installNukemFG,
                                nukemFGCachePath: nukemCacheDir,
                                optiscalerVersion: versionToInstall,
                                profile: defaultProfile
                            );
                        });

                        // ── FSR4 INT8 DLL injection (respect configured default extras)
                        var configuredExtras = _componentService.Config.DefaultExtrasVersion;
                        if (!string.IsNullOrEmpty(configuredExtras) && !configuredExtras.Equals("none", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                ShowToast(string.Format(GetResourceString("TxtDownloadingExtrasFormat", "Downloading FSR4 INT8 v{0}... {1}%"), configuredExtras, 0), showProgress: true, progressPercent: 0);
                                string extrasDllPath;
                                var extrasProgress = new Progress<double>(p => UpdateToastProgress(string.Format(GetResourceString("TxtDownloadingExtrasFormat", "Downloading FSR4 INT8 v{0}... {1}%"), configuredExtras, (int)p), p));
                                extrasDllPath = await _componentService.DownloadExtrasDllAsync(configuredExtras, extrasProgress);

                                // Copy into game directory
                                var installSvc = new GameInstallationService();
                                var gameDir = resolvedGameDir ?? installSvc.DetermineInstallDirectory(selectedGame) ?? selectedGame.InstallPath;
                                var destPath = System.IO.Path.Combine(gameDir, System.IO.Path.GetFileName(extrasDllPath));
                                if (!File.Exists(extrasDllPath))
                                    throw new Exception("FSR4 INT8 package is corrupt or incomplete.");
                                File.Copy(extrasDllPath, destPath, overwrite: true);
                                // Non-RDNA4 GPUs don't get FSR4 automatically like RDNA4 does — OptiScaler needs
                                // Fsr4ForceModel=2 (INT8) explicitly or it silently falls back to FSR3.
                                var preferredGpuForFsr4 = GpuSelectionHelper.GetPreferredGpu(_gpuService, _componentService.Config.DefaultGpuId);
                                var isRdna4 = GpuSelectionHelper.IsRdna4(preferredGpuForFsr4);
                                var isRdna2 = GpuSelectionHelper.IsRdna2(preferredGpuForFsr4);
                                installSvc.ConfigureFsr4IntFallback(gameDir, isRdna4, isRdna2);
                                selectedGame.Fsr4ExtraVersion = configuredExtras;
                                ShowToast($"FSR4 INT8 v{configuredExtras} injected", showProgress: false, progressPercent: null);
                                Dispatcher.UIThread.Post(() =>
                                {
                                    var icon = this.FindControl<TextBlock>("TxtToastIcon");
                                    if (icon != null) icon.Text = string.Empty;
                                });
                            }
                            catch (Exception ex)
                            {
                                HideToast();
                                await new ConfirmDialog(
                                    this,
                                    GetResourceString("TxtWarning", "Warning"),
                                    $"FSR4 INT8 download/inject failed (OptiScaler was still installed):\n{ex.Message}",
                                    isAlert: true
                                ).ShowDialog<bool>(this);
                            }
                        }

                        // ── OptiPatcher install (auto-select the latest version when the wiki's
                        // Compatibility List flags this game as needing it; otherwise respect
                        // the user's configured default)
                        var configuredPatcher = _componentService.Config.DefaultOptiPatcherVersion;
                        try
                        {
                            if (new CompatibilityListService().TryGetForGame(selectedGame.Name, out var compatEntryForPatcher) &&
                                compatEntryForPatcher != null && compatEntryForPatcher.OptiPatcherSupported &&
                                !string.IsNullOrEmpty(_componentService.LatestOptiPatcherVersion))
                            {
                                configuredPatcher = _componentService.LatestOptiPatcherVersion;
                            }
                        }
                        catch (Exception ex) { DebugWindow.Log($"[QuickInstall] Compatibility List lookup failed: {ex.Message}"); }

                        if (!string.IsNullOrEmpty(configuredPatcher) && !configuredPatcher.Equals("none", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                ShowToast($"Downloading OptiPatcher v{configuredPatcher}...", showProgress: true, progressPercent: 0);
                                var patcherProgress = new Progress<double>(p =>
                                    UpdateToastProgress($"Downloading OptiPatcher v{configuredPatcher}... {(int)p}%", p));

                                var optiPatcherAsiPath = await _componentService.DownloadOptiPatcherAsync(configuredPatcher, patcherProgress);

                                ShowToast("Installing OptiPatcher...", showProgress: true, progressPercent: null);

                                await Task.Run(() =>
                                {
                                    var installSvc = new GameInstallationService();
                                    var gameDir = resolvedGameDir ?? installSvc.DetermineInstallDirectory(selectedGame) ?? selectedGame.InstallPath;

                                    var pluginsDir = System.IO.Path.Combine(gameDir, "plugins");
                                    Directory.CreateDirectory(pluginsDir);
                                    var destAsi = System.IO.Path.Combine(pluginsDir, "OptiPatcher.asi");
                                    System.IO.File.Copy(optiPatcherAsiPath, destAsi, overwrite: true);
                                    DebugWindow.Log($"[QuickInstall][OptiPatcher] Installed to {destAsi}");

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
                                        if (!found)
                                            lines.Add("LoadAsiPlugins=true");
                                        System.IO.File.WriteAllLines(iniPath, lines);
                                        DebugWindow.Log("[QuickInstall][OptiPatcher] Patched OptiScaler.ini: LoadAsiPlugins=true");
                                    }
                                });
                            }
                            catch (Exception ex)
                            {
                                HideToast();
                                await new ConfirmDialog(
                                    this,
                                    GetResourceString("TxtWarning", "Warning"),
                                    $"OptiPatcher download/inject failed (OptiScaler was still installed):\n{ex.Message}",
                                    isAlert: true
                                ).ShowDialog<bool>(this);
                            }
                        }

                        // Update game status
                        selectedGame.IsOptiscalerInstalled = true;
                        selectedGame.OptiscalerVersion = versionToInstall;

                        // Refresh UI
                        RefreshGameLists();

                        _persistenceService.SaveGames(_games);

                        // Final toast: report what was installed
                        var parts = new System.Collections.Generic.List<string> { $"OptiScaler {versionToInstall}" };
                        if (installFakenvapi)
                            parts.Add($"Fakenvapi {configuredFakenvapi}");
                        if (installNukemFG)
                            parts.Add($"NukemFG {configuredNukemFG}");
                        var configuredExtrasFinal = _componentService.Config.DefaultExtrasVersion;
                        if (!string.IsNullOrEmpty(configuredExtrasFinal) && !configuredExtrasFinal.Equals("none", StringComparison.OrdinalIgnoreCase))
                            parts.Add($"FSR4 INT8 {configuredExtrasFinal}");
                        if (!string.IsNullOrEmpty(configuredPatcher) && !configuredPatcher.Equals("none", StringComparison.OrdinalIgnoreCase))
                            parts.Add($"OptiPatcher {configuredPatcher}");

                        ShowToast($"Installed {string.Join(" + ", parts)}", showProgress: false, progressPercent: null);
                        Dispatcher.UIThread.Post(() =>
                        {
                            var icon = this.FindControl<TextBlock>("TxtToastIcon");
                            if (icon != null) icon.Text = string.Empty;
                        });

                        await HideToastAfterAsync(1500);
                    }
                }
                catch (Exception ex)
                {
                    await new ConfirmDialog(
                        this,
                        GetResourceString("TxtError", "Error"),
                        ex.Message,
                        isAlert: true
                    ).ShowDialog<bool>(this);
                }
                finally
                {
                    ClearQuickInstallLoading(button, selectedGame);
                }
            }
        }

        private async void BtnRemoveGame_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button button && button.DataContext is Game game)
                {
                    var title = GetResourceString("TxtRemoveGameTitle", "Remove Game");
                    var confirmFormat = GetResourceString("TxtRemoveGameConfirm", "Are you sure you want to remove '{0}' from the list?");
                    var message = string.Format(confirmFormat, game.Name);

                    var dialog = new ConfirmDialog(this, title, message, false);
                    var result = await dialog.ShowDialog<bool>(this); // true if confirmed

                    if (result)
                    {
                        _games.Remove(game);
                        _persistenceService.SaveGames(_games);
                    }
                }
            }
            catch (Exception ex) { DebugWindow.Log($"[MainWindow] Remove game failed: {ex.Message}"); }
        }

        private async Task LoadGpuInfoAsync()
        {
            try
            {
                if (_txtGpuInfo == null) return;

                GpuInfo? gpu;
                if (_lastDetectedGpu != null)
                {
                    gpu = _lastDetectedGpu;
                }
                else
                {
                    _txtGpuInfo!.Text = GetResourceString("TxtDefaultGpu", "Detecting GPU...");
                    gpu = await Task.Run(() =>
                    {
                        if (_gpuService != null)
                        {
                            try
                            {
                                return GpuSelectionHelper.GetPreferredGpu(_gpuService, _componentService.Config.DefaultGpuId);
                            }
                            catch { return null; }
                        }
                        return null;
                    });
                    _lastDetectedGpu = gpu;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (gpu != null)
                    {
                        string icon = "⚪";
                        IBrush color = Brushes.Gray;

                        switch (gpu.Vendor)
                        {
                            case GpuVendor.NVIDIA:
                                icon = "🟢"; color = new SolidColorBrush(Color.FromRgb(118, 185, 0)); break;
                            case GpuVendor.AMD:
                                icon = "🔴"; color = new SolidColorBrush(Color.FromRgb(237, 28, 36)); break;
                            case GpuVendor.Intel:
                                icon = "🔵"; color = new SolidColorBrush(Color.FromRgb(0, 113, 197)); break;
                        }

                        _txtGpuInfo!.Text = $"{icon} {gpu.Name}";
                        _txtGpuInfo.Foreground = color;
                        ToolTip.SetTip(_txtGpuInfo, $"{gpu.Name}\nVendor: {gpu.Vendor}\nVRAM: {gpu.VideoMemoryGB}\nDriver: {gpu.DriverVersion}");
                    }
                    else
                    {
                        _txtGpuInfo!.Text = GetResourceString("TxtNoGpu", "⚠️ No GPU detected");
                        _txtGpuInfo.Foreground = Brushes.Orange;
                        ToolTip.SetTip(_txtGpuInfo, GetResourceString("TxtNoGpuTip", "No GPU was detected on this system"));
                    }
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_txtGpuInfo != null)
                    {
                        _txtGpuInfo.Text = GetResourceString("TxtGpuFail", "⚠️ GPU detection failed");
                        _txtGpuInfo.Foreground = Brushes.Gray;
                        var format = GetResourceString("TxtGpuFailTipFormat", "Error detecting GPU: {0}");
                        ToolTip.SetTip(_txtGpuInfo, string.Format(format, ex.Message));
                    }
                });
            }
        }

        private void ShowToast(string message, bool showProgress = false, double? progressPercent = null)
        {
            var txtToastMessage = this.FindControl<TextBlock>("TxtToastMessage");
            var bdToast = this.FindControl<Border>("BdToast");
            var prgToast = this.FindControl<ProgressBar>("PrgToast");

            Dispatcher.UIThread.Post(() =>
            {
                if (txtToastMessage != null) txtToastMessage.Text = message;
                if (bdToast != null) bdToast.IsVisible = true;
                if (prgToast != null)
                {
                    prgToast.IsVisible = showProgress;
                    prgToast.IsIndeterminate = !progressPercent.HasValue;
                    if (progressPercent.HasValue) prgToast.Value = progressPercent.Value;
                }
            });
        }

        private void UpdateToastProgress(string message, double progressPercent)
        {
            ShowToast(message, showProgress: true, progressPercent: progressPercent);
        }

        private void HideToast()
        {
            var bdToast = this.FindControl<Border>("BdToast");
            var prgToast = this.FindControl<ProgressBar>("PrgToast");

            Dispatcher.UIThread.Post(() =>
            {
                if (bdToast != null) bdToast.IsVisible = false;
                if (prgToast != null) prgToast.IsVisible = false;
            });
        }

        private async Task HideToastAfterAsync(int delayMs)
        {
            await Task.Delay(delayMs);
            HideToast();
        }

        private void ShowSecondaryToast(string message)
        {
            var txtToast = this.FindControl<TextBlock>("TxtToastSecondaryMessage");
            var bdToast = this.FindControl<Border>("BdToastSecondary");

            Dispatcher.UIThread.Post(() =>
            {
                if (txtToast != null) txtToast.Text = message;
                if (bdToast != null) bdToast.IsVisible = true;
            });

            _ = HideSecondaryToastAfterAsync(1500);
        }

        private async Task HideSecondaryToastAfterAsync(int delayMs)
        {
            await Task.Delay(delayMs);
            var bdToast = this.FindControl<Border>("BdToastSecondary");
            Dispatcher.UIThread.Post(() =>
            {
                if (bdToast != null) bdToast.IsVisible = false;
            });
        }

        #region Window State Persistence

        private void MainWindow_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            // Only save state for relevant properties
            if (e.Property == Window.WindowStateProperty ||
                e.Property == Window.WidthProperty ||
                e.Property == Window.HeightProperty)
            {
                SaveWindowState();
            }
        }

        private void RestoreWindowState()
        {
            var config = _componentService.Config;

            // Restore window size
            if (config.WindowWidth > 0 && config.WindowHeight > 0)
            {
                this.Width = config.WindowWidth;
                this.Height = config.WindowHeight;
            }

            // Restore window position (only if valid)
            if (!double.IsNaN(config.WindowLeft) && !double.IsNaN(config.WindowTop) &&
                config.WindowLeft >= 0 && config.WindowTop >= 0)
            {
                this.Position = new PixelPoint((int)config.WindowLeft, (int)config.WindowTop);
            }

            // Restore maximized state
            if (config.WindowMaximized)
            {
                this.WindowState = WindowState.Maximized;
            }
        }

        private void SaveWindowState()
        {
            try
            {
                var config = _componentService.Config;

                // Save window size (only when not maximized)
                if (this.WindowState != WindowState.Maximized)
                {
                    if (this.Width > 0 && this.Height > 0)
                    {
                        config.WindowWidth = this.Width;
                        config.WindowHeight = this.Height;
                    }
                }

                // Save window position
                var position = this.Position;
                if (!double.IsNaN(position.X) && !double.IsNaN(position.Y))
                {
                    config.WindowLeft = position.X;
                    config.WindowTop = position.Y;
                }

                // Save maximized state
                config.WindowMaximized = this.WindowState == WindowState.Maximized;

                // Save configuration
                _componentService.SaveConfiguration();
            }
            catch
            {
                // Ignore errors during window state saving
            }
        }

        #endregion

        private void LogToFile(string message)
        {
            try
            {
                var logPath = System.IO.Path.Combine(AppPaths.GetAppDataRoot(), "crash.log");

                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                System.IO.File.AppendAllText(logPath, $"[{timestamp}] {message}\n");
            }
            catch
            {
                // Si falla el logging, no hacer nada para evitar crash adicional
            }
        }

        private string GetResourceString(string key, string fallback)
        {
            return Application.Current?.TryFindResource(key, out var res) == true && res is string str ? str : fallback;
        }
    }
}

