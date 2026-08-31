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
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using OptiscalerClient.Models;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Controls.Shapes;

using Avalonia.Layout;
using OptiscalerClient.Services;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using System.Collections.Generic;
using OptiscalerClient.Helpers;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace OptiscalerClient.Views
{
    public partial class ManageGameWindow : Window, IGamepadInputHost
    {
        // This window predates GamepadHelperBase and manages its own gamepad
        // polling directly (see InitializeGamepadNavigation), so there is no
        // real helper instance to expose here — only IsGamepadModeActive is
        // overridden below, sourced from _isControllerModeActive instead.
        GamepadHelperBase? IGamepadInputHost.GamepadHelper => null;
        bool IGamepadInputHost.IsGamepadModeActive => _isControllerModeActive;

        private readonly Game _game;
        private readonly IGpuDetectionService? _gpuService;
        private Window? _ownerWindow;
        private HashSet<string> _betaVersions = new();
        private HashSet<string> _nightlyVersions = new();
        private HashSet<string> _customVersions = new();
        private bool _optiShowingBeta;
        private bool _optiShowingNightly;
        private bool _optiShowingCustom;
        private bool _optiTabInitialized;
        private Fsr4DllVariant _extrasVariant = Fsr4DllVariant.Int8;
        private bool _extrasTabInitialized;
        private ComponentManagementService? _cachedComponentService;
        private string? _pendingCoverPath;
        private readonly string? _originalCoverPath;
        private const string NewProfileTag = "__NEW_PROFILE__";
        private bool _isUpdatingProfiles;
        private string? _lastSelectedProfileName;
        private string? _defaultProfileName;
        private IGamepadDetectionService? _gamepadService;
        private DateTime _ignoreGamepadInputUntilUtc;
        private bool _isControllerModeActive;
        private bool _isUpdatingUpscalingQuality;
        private bool _qualityCustomHandledForOpen;

        // Right-stick scroll for the compatibility sidebar — read-only content, deliberately not
        // part of the D-pad/left-stick focus navigation (see compatibility_list_sidebar_plan.md).
        private readonly DispatcherTimer _compatSidebarScrollTimer;
        private bool _isRightStickUpHeld;
        private bool _isRightStickDownHeld;
        private double _compatSidebarScrollVelocity;

        public bool NeedsScan { get; private set; }

        // TaskCompletionSource for the corrupt-install-detected modal (3-way: cancel/clean/continue).
        private TaskCompletionSource<string>? _corruptInstallTcs;
        // Set to true when the cleanup modal is opened from the corrupt-install flow.
        // Causes the cleanup Yes/No handlers to resolve _corruptInstallTcs instead of
        // running the cleanup inline, allowing ExecuteInstallAsync to drive the sequence.
        private bool _cleanupIsPreInstall;
        private List<string>? _preInstallCleanupSelectedFiles;

        private static Dictionary<string, string>? _fsrVersionMap;
        private static Dictionary<string, string>? _dlssVersionMap;
        private static Dictionary<string, string>? _xessVersionMap;

        // Set by PopulateCompatibilitySidebar (always runs before PopulateOptiPatcherComboBox —
        // see SetupUI/LoadVersionsAsync ordering in the constructor), reused so the OptiPatcher
        // selector knows whether this game is flagged as needing OptiPatcher.
        private CompatibilityListEntry? _compatEntry;
        private bool _isWaitingForCompatibilityRefresh;
        private bool _isClosed;

        // Points the "View Compatibility List" footer link at this game's own wiki page once
        // PopulateWikiDetailsAsync resolves one, instead of the generic list page.
        private string? _wikiPageUrl;

        // Drives the bouncing-dots animation on the "Fetching game information..." card - same
        // sine-wave bounce (33ms tick, 120° phase offset per dot) as SetQuickInstallLoading in
        // MainWindow, reused here for a consistent loading feel across the app.
        private DispatcherTimer? _wikiFetchDotsTimer;
        private double _wikiFetchDotsPhase;

        // Set once RenderWikiDetails applies the wiki-suggested injection method for the first
        // time this window is open (see ApplySuggestedInjectionMethod), so a later silent refresh
        // of the same wiki page (PopulateWikiDetailsAsync's background cooldown check) never
        // overwrites a selection the user may have since picked by hand.
        private bool _injectionMethodAutoSelected;

        // Must match the Tag values on CmbInjectionMethod's ComboBoxItems in the .axaml exactly.
        private static readonly string[] KnownInjectionDllNames =
        {
            "dxgi.dll", "winmm.dll", "d3d12.dll", "dbghelp.dll", "version.dll", "wininet.dll", "winhttp.dll"
        };

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void PopulateProfileSelector(ProfileManagementService profileService, List<OptiScalerProfile> profiles, string? selectedName = null)
        {
            var cmbProfile = this.FindControl<ComboBox>("CmbProfile");
            if (cmbProfile == null) return;

            _isUpdatingProfiles = true;
            cmbProfile.SelectionChanged -= CmbProfile_SelectionChanged;
            cmbProfile.Items.Clear();

            foreach (var profile in profiles)
            {
                var displayName = profile.Name;
                var item = new ComboBoxItem
                {
                    Content = displayName,
                    Tag = profile
                };
                ToolTip.SetTip(item, profile.Description);
                cmbProfile.Items.Add(item);
            }

            cmbProfile.Items.Add(new ComboBoxItem
            {
                Content = "+ New Profile",
                Tag = NewProfileTag
            });

            var targetName = selectedName;
            if (string.IsNullOrWhiteSpace(targetName))
            {
                targetName = _defaultProfileName;
                if (string.IsNullOrWhiteSpace(targetName))
                {
                    targetName = profileService.GetDefaultProfile().Name;
                }
            }
            var selectedIndex = profiles.FindIndex(p => p.Name == targetName);
            selectedIndex = selectedIndex >= 0 ? selectedIndex : Math.Max(0, profiles.Count - 1);

            cmbProfile.SelectedIndex = selectedIndex;
            if (profiles.Count > 0 && selectedIndex >= 0)
            {
                _lastSelectedProfileName = profiles[selectedIndex].Name;
            }
            else
            {
                _lastSelectedProfileName = targetName;
            }

            cmbProfile.SelectionChanged += CmbProfile_SelectionChanged;
            _isUpdatingProfiles = false;
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
                var profileService = new ProfileManagementService();
                var profiles = profileService.GetAllProfiles();
                var fallbackName = _lastSelectedProfileName
                    ?? _defaultProfileName
                    ?? profileService.GetDefaultProfile().Name;
                var fallbackIndex = profiles.FindIndex(p => p.Name == fallbackName);

                _isUpdatingProfiles = true;
                cmbProfile.SelectedIndex = fallbackIndex >= 0 ? fallbackIndex : 0;
                _isUpdatingProfiles = false;

                this.Close();
                if (_ownerWindow is MainWindow mainWindow)
                    mainWindow.NavigateToProfiles();
            }
        }

        // Avalonia requires an empty parameterless constructor for XAML initialization
        public ManageGameWindow()
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
            _game = null!;
            _gpuService = null!;
            _compatSidebarScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _compatSidebarScrollTimer.Tick += CompatSidebarScrollTimer_Tick;
        }

        public ManageGameWindow(Window owner, Game game)
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
            _game = game;
            _ownerWindow = owner;
            _originalCoverPath = game.CoverImageUrl;

            // Frameless centering logic
            this.Opacity = 0;
            if (owner != null)
            {
                var scaling = owner.DesktopScaling;
                double dialogW = 960 * scaling;
                double dialogH = 660 * scaling; // estimate — window uses SizeToContent="Height"

                var x = owner.Position.X + (owner.Bounds.Width * scaling - dialogW) / 2;
                var y = owner.Position.Y + (owner.Bounds.Height * scaling - dialogH) / 2;

                this.Position = new PixelPoint((int)Math.Max(0, x), (int)Math.Max(0, y));
            }

            _gpuService = PlatformServiceFactory.CreateGpuDetectionService();
            _gamepadService = PlatformServiceFactory.CreateGamepadDetectionService();
            _ignoreGamepadInputUntilUtc = DateTime.UtcNow.AddMilliseconds(350);
            _compatSidebarScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _compatSidebarScrollTimer.Tick += CompatSidebarScrollTimer_Tick;

            SetupUI();
            InitializeGamepadNavigation();

            // Start already in whatever mode the owner window was in, instead
            // of always defaulting to mouse mode until the user presses
            // something inside this new dialog — see
            // gamepad_implementation_log.md, section 25.
            if (owner is IGamepadInputHost ownerHost)
                SetControllerModeActive(ownerHost.IsGamepadModeActive);

            this.Closed += ManageGameWindow_Closed;
            this.AddHandler(InputElement.PointerMovedEvent, ManageGameWindow_PointerMoved, handledEventsToo: true);

            // Re-bind TitleBar dragging and Close button
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

            _ = LoadVersionsAsync();
        }

        private void InitializeGamepadNavigation()
        {
            if (_gamepadService == null) return;

            _gamepadService.GamepadInputReceived += OnGamepadInputReceived;
            _gamepadService.GamepadConnectionChanged += OnGamepadConnectionChanged;
            _gamepadService.StartListening();
        }

        private void ManageGameWindow_Closed(object? sender, EventArgs e)
        {
            _isClosed = true;
            StopWaitingForCompatibilityRefresh();
            this.RemoveHandler(InputElement.PointerMovedEvent, ManageGameWindow_PointerMoved);
            StopWikiFetchingAnimation();

            if (_gamepadService == null) return;

            _gamepadService.GamepadInputReceived -= OnGamepadInputReceived;
            _gamepadService.GamepadConnectionChanged -= OnGamepadConnectionChanged;
            _gamepadService.StopListening();
            _gamepadService = null;
        }

        private void OnGamepadConnectionChanged(object? sender, bool isConnected)
        {
            if (!isConnected) return;

            // Avoid processing the same held button that opened this dialog.
            _ignoreGamepadInputUntilUtc = DateTime.UtcNow.AddMilliseconds(200);
        }

        private void OnGamepadInputReceived(object? sender, GamepadEventArgs e)
        {
            // Intercepted before the IsPressed-only filter below: right-stick scroll needs both
            // press AND release to track the "held" state and stop scrolling when released.
            if (e.Button == GamepadButton.ThumbRightUp || e.Button == GamepadButton.ThumbRightDown)
            {
                Dispatcher.UIThread.Post(() => HandleCompatSidebarRightStickInput(e));
                return;
            }

            if (!e.IsPressed) return;

            Dispatcher.UIThread.Post(() =>
            {
                if (!IsActive) return;
                if (DateTime.UtcNow < _ignoreGamepadInputUntilUtc) return;

                SetControllerModeActive(true);

                if (HandleOpenComboBoxInput(e.Button))
                    return;

                EnsureGamepadFocus();

                switch (e.Button)
                {
                    case GamepadButton.DPadUp:
                    case GamepadButton.ThumbLeftUp:
                        MoveFocusInActiveSurface(NavigationDirection.Up);
                        break;

                    case GamepadButton.DPadDown:
                    case GamepadButton.ThumbLeftDown:
                        MoveFocusInActiveSurface(NavigationDirection.Down);
                        break;

                    case GamepadButton.DPadLeft:
                    case GamepadButton.ThumbLeftLeft:
                        MoveFocusInActiveSurface(NavigationDirection.Left);
                        break;

                    case GamepadButton.DPadRight:
                    case GamepadButton.ThumbLeftRight:
                        MoveFocusInActiveSurface(NavigationDirection.Right);
                        break;

                    case GamepadButton.A:
                        ActivateFocusedElement();
                        break;

                    case GamepadButton.B:
                    case GamepadButton.ThumbRightLeft:
                        HandleBackAction();
                        break;
                }
            });
        }

        private void ManageGameWindow_PointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_isControllerModeActive) return;

            SetControllerModeActive(false);
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
        }

        private void SetControllerModeActive(bool active)
        {
            if (_isControllerModeActive == active) return;
            _isControllerModeActive = active;

            var txtX = this.FindControl<TextBlock>("TxtCloseIconX");
            var badgeB = this.FindControl<Border>("BadgeCloseGamepadB");
            if (txtX != null) txtX.IsVisible = !active;
            if (badgeB != null) badgeB.IsVisible = active;
        }

        private void EnsureGamepadFocus()
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (focused is Visual focusedVisual && IsInsideActiveSurface(focusedVisual))
                return;

            FocusFirstActiveElement();
        }

        private bool IsInsideActiveSurface(Visual focused)
        {
            var surface = GetActiveSurface();
            if (surface == null) return false;
            return focused == surface || focused.GetVisualAncestors().Contains(surface);
        }

        private Visual? GetActiveSurface()
        {
            var coverModal = this.FindControl<Grid>("BdCoverModal");
            if (coverModal?.IsVisible == true) return coverModal;

            var corruptModal = this.FindControl<Grid>("BdConfirmCorruptInstall");
            if (corruptModal?.IsVisible == true) return corruptModal;

            var cleanupModal = this.FindControl<Grid>("BdConfirmFolderCleanup");
            if (cleanupModal?.IsVisible == true) return cleanupModal;

            var uninstallModal = this.FindControl<Grid>("BdConfirmUninstall");
            if (uninstallModal?.IsVisible == true) return uninstallModal;

            return (Visual?)this.FindControl<Panel>("RootPanel") ?? this;
        }

        private enum NavigationDirection { Up, Down, Left, Right }

        private sealed class NavigationNode
        {
            public string Name { get; }
            public Control Control { get; }
            public int Row { get; }
            public int Col { get; }

            public NavigationNode(string name, Control control, int row, int col)
            {
                Name = name;
                Control = control;
                Row = row;
                Col = col;
            }
        }

        private bool MoveFocusInActiveSurface(NavigationDirection direction)
        {
            if (!IsAnyModalVisible())
                return MoveFocusInRootGrid(direction);

            return MoveFocusInVisualSurface(direction);
        }

        private bool MoveFocusInRootGrid(NavigationDirection direction)
        {
            var nodes = GetRootNavigationNodes();
            if (nodes.Count == 0) return false;

            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            var currentNode = ResolveFocusedNode(focused, nodes);
            if (currentNode == null)
            {
                var first = nodes
                    .OrderBy(n => n.Row)
                    .ThenBy(n => n.Col)
                    .First();
                FocusControl(first.Control);
                return true;
            }

            var target = FindGridDirectionTarget(currentNode, nodes, direction);
            if (target == null) return false;

            FocusControl(target.Control);
            return true;
        }

        private List<NavigationNode> GetRootNavigationNodes()
        {
            var nodes = new List<NavigationNode>();

            AddRootNode(nodes, "BtnEditImage", 1, 1);
            AddRootNode(nodes, "BtnOptiStable", 1, 2);
            AddRootNode(nodes, "BtnOptiBeta", 1, 3);
            AddRootNode(nodes, "BtnOptiNightly", 1, 4);
            AddRootNode(nodes, "BtnClose", 1, 5);

            AddRootNode(nodes, "BtnEditTitle", 2, 1);
            AddRootNode(nodes, "CmbOptiVersion", 2, 2);
            AddRootNode(nodes, "CmbExtrasVersion", 2, 4);
            AddRootNode(nodes, "CmbFakenvapiVersion", 2, 5);

            AddRootNode(nodes, "CmbInjectionMethod", 3, 3);
            AddRootNode(nodes, "CmbOptiPatcherVersion", 3, 4);
            AddRootNode(nodes, "CmbNukemFGVersion", 3, 5);

            AddRootNode(nodes, "CmbProfile", 4, 3);
            AddRootNode(nodes, "BtnFrameGeneration", 4, 4);
            AddRootNode(nodes, "CmbUpscalingQuality", 4, 5);
            AddRootNode(nodes, "BtnUninstall", 5, 5);

            AddRootNode(nodes, "BtnOpenFolder", 6, 1);
            AddRootNode(nodes, "BtnFolderCleanup", 6, 3);
            AddRootNode(nodes, "BtnInstallManual", 6, 4);
            AddRootNode(nodes, "BtnInstall", 6, 5);

            return nodes;
        }

        private void AddRootNode(List<NavigationNode> nodes, string controlName, int row, int col)
        {
            var control = this.FindControl<Control>(controlName);
            if (control == null || !control.IsVisible || !control.IsEnabled || !control.Focusable)
                return;

            nodes.Add(new NavigationNode(controlName, control, row, col));
        }

        private NavigationNode? ResolveFocusedNode(IInputElement? focused, List<NavigationNode> nodes)
        {
            if (focused is not Visual focusedVisual) return null;

            foreach (var node in nodes)
            {
                var candidate = node.Control;
                if (focusedVisual == candidate || focusedVisual.GetVisualAncestors().Contains(candidate))
                    return node;
            }

            return null;
        }

        private NavigationNode? FindGridDirectionTarget(NavigationNode current, List<NavigationNode> nodes, NavigationDirection direction)
        {
            var explicitCandidates = GetRootNeighborCandidates(current.Name, direction);
            foreach (var targetName in explicitCandidates)
            {
                var explicitTarget = nodes.FirstOrDefault(n => string.Equals(n.Name, targetName, StringComparison.Ordinal));
                if (explicitTarget != null)
                    return explicitTarget;
            }

            NavigationNode? best = null;
            int bestScore = int.MaxValue;

            foreach (var candidate in nodes)
            {
                if (ReferenceEquals(candidate.Control, current.Control))
                    continue;

                if (IsOptiTabButton(candidate.Name)
                    && !IsOptiTabButton(current.Name)
                    && !string.Equals(current.Name, "CmbOptiVersion", StringComparison.Ordinal))
                {
                    continue;
                }

                int rowDelta = candidate.Row - current.Row;
                int colDelta = candidate.Col - current.Col;

                int primary;
                int secondary;

                switch (direction)
                {
                    case NavigationDirection.Right:
                        primary = colDelta;
                        secondary = Math.Abs(rowDelta);
                        break;

                    case NavigationDirection.Left:
                        primary = -colDelta;
                        secondary = Math.Abs(rowDelta);
                        break;

                    case NavigationDirection.Down:
                        primary = rowDelta;
                        secondary = Math.Abs(colDelta);
                        break;

                    default:
                        primary = -rowDelta;
                        secondary = Math.Abs(colDelta);
                        break;
                }

                if (primary <= 0)
                    continue;

                int score = primary * 10 + secondary;
                if (score < bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            return best;
        }

        private static bool IsOptiTabButton(string controlName)
        {
            return string.Equals(controlName, "BtnOptiStable", StringComparison.Ordinal)
                   || string.Equals(controlName, "BtnOptiBeta", StringComparison.Ordinal)
                   || string.Equals(controlName, "BtnOptiNightly", StringComparison.Ordinal);
        }

        private IEnumerable<string> GetRootNeighborCandidates(string currentName, NavigationDirection direction)
        {
            if (string.Equals(currentName, "CmbOptiVersion", StringComparison.Ordinal)
                && direction == NavigationDirection.Up)
            {
                var preferred = GetPreferredOptiTabButtonName();
                if (!string.IsNullOrEmpty(preferred))
                    return new[] { preferred };
            }

            return (currentName, direction) switch
            {
                ("BtnEditTitle", NavigationDirection.Down) => new[] { "BtnOpenFolder" },

                ("CmbOptiVersion", NavigationDirection.Right) => new[] { "CmbExtrasVersion" },
                ("CmbOptiVersion", NavigationDirection.Left) => new[] { "BtnEditTitle" },
                ("CmbOptiVersion", NavigationDirection.Down) => new[] { "CmbInjectionMethod", "CmbProfile" },

                ("BtnOptiStable", NavigationDirection.Down) => new[] { "CmbOptiVersion" },
                ("BtnOptiStable", NavigationDirection.Right) => new[] { "BtnOptiBeta", "BtnOptiNightly", "CmbExtrasVersion" },

                ("BtnOptiBeta", NavigationDirection.Left) => new[] { "BtnOptiStable" },
                ("BtnOptiBeta", NavigationDirection.Right) => new[] { "BtnOptiNightly", "CmbExtrasVersion" },
                ("BtnOptiBeta", NavigationDirection.Down) => new[] { "CmbOptiVersion" },

                ("BtnOptiNightly", NavigationDirection.Left) => new[] { "BtnOptiBeta", "BtnOptiStable" },
                ("BtnOptiNightly", NavigationDirection.Right) => new[] { "CmbExtrasVersion" },
                ("BtnOptiNightly", NavigationDirection.Down) => new[] { "CmbOptiVersion" },

                ("CmbExtrasVersion", NavigationDirection.Left) => new[] { "CmbOptiVersion" },
                ("CmbExtrasVersion", NavigationDirection.Up) => new[] { "BtnOptiNightly", "BtnOptiBeta", "BtnOptiStable" },
                ("CmbExtrasVersion", NavigationDirection.Right) => new[] { "CmbFakenvapiVersion" },
                ("CmbExtrasVersion", NavigationDirection.Down) => new[] { "CmbOptiPatcherVersion" },

                ("CmbFakenvapiVersion", NavigationDirection.Left) => new[] { "CmbExtrasVersion" },
                ("CmbFakenvapiVersion", NavigationDirection.Down) => new[] { "CmbNukemFGVersion", "BtnInstall" },

                ("CmbInjectionMethod", NavigationDirection.Up) => new[] { "CmbOptiVersion" },
                ("CmbInjectionMethod", NavigationDirection.Down) => new[] { "CmbProfile", "BtnFolderCleanup" },
                ("CmbInjectionMethod", NavigationDirection.Right) => new[] { "CmbOptiPatcherVersion" },
                ("CmbInjectionMethod", NavigationDirection.Left) => new[] { "BtnEditTitle" },

                ("CmbOptiPatcherVersion", NavigationDirection.Left) => new[] { "CmbInjectionMethod" },
                ("CmbOptiPatcherVersion", NavigationDirection.Right) => new[] { "CmbNukemFGVersion" },
                ("CmbOptiPatcherVersion", NavigationDirection.Up) => new[] { "CmbExtrasVersion" },
                ("CmbOptiPatcherVersion", NavigationDirection.Down) => new[] { "BtnInstallManual", "BtnFolderCleanup" },

                ("CmbNukemFGVersion", NavigationDirection.Left) => new[] { "CmbOptiPatcherVersion" },
                ("CmbNukemFGVersion", NavigationDirection.Up) => new[] { "CmbFakenvapiVersion" },
                ("CmbNukemFGVersion", NavigationDirection.Down) => new[] { "BtnInstall", "BtnInstallManual" },

                ("CmbProfile", NavigationDirection.Up) => new[] { "CmbInjectionMethod" },
                ("CmbProfile", NavigationDirection.Down) => new[] { "BtnFolderCleanup" },
                ("CmbProfile", NavigationDirection.Right) => new[] { "BtnFrameGeneration" },
                ("CmbProfile", NavigationDirection.Left) => new[] { "BtnEditTitle" },

                ("BtnFrameGeneration", NavigationDirection.Left) => new[] { "CmbProfile" },
                ("BtnFrameGeneration", NavigationDirection.Right) => new[] { "CmbUpscalingQuality" },
                ("BtnFrameGeneration", NavigationDirection.Up) => new[] { "CmbOptiPatcherVersion", "CmbNukemFGVersion" },
                ("BtnFrameGeneration", NavigationDirection.Down) => new[] { "BtnInstallManual", "BtnInstall" },

                ("CmbUpscalingQuality", NavigationDirection.Left) => new[] { "BtnFrameGeneration" },
                ("CmbUpscalingQuality", NavigationDirection.Up) => new[] { "CmbNukemFGVersion", "CmbFakenvapiVersion" },
                ("CmbUpscalingQuality", NavigationDirection.Down) => new[] { "BtnUninstall", "BtnInstall" },

                ("BtnUninstall", NavigationDirection.Left) => new[] { "CmbUpscalingQuality", "BtnFrameGeneration", "CmbProfile" },
                ("BtnUninstall", NavigationDirection.Down) => new[] { "BtnInstall" },
                ("BtnUninstall", NavigationDirection.Up) => new[] { "BtnFrameGeneration", "CmbNukemFGVersion" },

                ("BtnOpenFolder", NavigationDirection.Up) => new[] { "BtnEditTitle" },
                ("BtnOpenFolder", NavigationDirection.Right) => new[] { "BtnFolderCleanup" },

                ("BtnFolderCleanup", NavigationDirection.Left) => new[] { "BtnOpenFolder" },
                ("BtnFolderCleanup", NavigationDirection.Right) => new[] { "BtnInstallManual" },
                ("BtnFolderCleanup", NavigationDirection.Up) => new[] { "CmbProfile", "CmbInjectionMethod" },

                ("BtnInstallManual", NavigationDirection.Left) => new[] { "BtnFolderCleanup" },
                ("BtnInstallManual", NavigationDirection.Right) => new[] { "BtnInstall" },
                ("BtnInstallManual", NavigationDirection.Up) => new[] { "CmbOptiPatcherVersion" },

                ("BtnInstall", NavigationDirection.Left) => new[] { "BtnInstallManual" },
                ("BtnInstall", NavigationDirection.Up) => new[] { "BtnUninstall", "CmbNukemFGVersion" },

                _ => Array.Empty<string>()
            };
        }

        private string GetPreferredOptiTabButtonName()
        {
            var stable = this.FindControl<Button>("BtnOptiStable");
            var beta = this.FindControl<Button>("BtnOptiBeta");
            var nightly = this.FindControl<Button>("BtnOptiNightly");

            if (nightly?.IsVisible == true && nightly.IsEnabled && nightly.Classes.Contains("BtnPrimary"))
                return "BtnOptiNightly";

            if (beta?.IsVisible == true && beta.IsEnabled && beta.Classes.Contains("BtnPrimary"))
                return "BtnOptiBeta";

            if (stable?.IsVisible == true && stable.IsEnabled)
                return "BtnOptiStable";

            if (beta?.IsVisible == true && beta.IsEnabled)
                return "BtnOptiBeta";

            return string.Empty;
        }

        private bool MoveFocusInVisualSurface(NavigationDirection direction)
        {
            var focusables = GetFocusableElementsInActiveSurface();
            if (focusables.Count == 0) return false;

            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();

            var current = ResolveFocusedControl(focused, focusables);
            if (current == null)
            {
                FocusControl(focusables[0]);
                return true;
            }

            var strict = FindDirectionalCandidate(current, focusables, direction, strictCone: true);
            var target = strict ?? FindDirectionalCandidate(current, focusables, direction, strictCone: false);
            if (target == null) return false;

            FocusControl(target);
            return true;
        }

        private Control? ResolveFocusedControl(IInputElement? focused, List<Control> focusables)
        {
            if (focused is not Visual focusedVisual) return null;

            foreach (var candidate in focusables)
            {
                if (focusedVisual == candidate || focusedVisual.GetVisualAncestors().Contains(candidate))
                    return candidate;
            }

            return null;
        }

        private Control? FindDirectionalCandidate(Control current, List<Control> focusables, NavigationDirection direction, bool strictCone)
        {
            var currentCenter = GetControlCenter(current);
            if (currentCenter == null) return null;

            Control? best = null;
            double bestScore = double.MaxValue;
            double coneRatio = strictCone ? 1.2 : 4.0;

            foreach (var candidate in focusables)
            {
                if (ReferenceEquals(candidate, current))
                    continue;

                var candidateCenter = GetControlCenter(candidate);
                if (candidateCenter == null)
                    continue;

                double dx = candidateCenter.Value.X - currentCenter.Value.X;
                double dy = candidateCenter.Value.Y - currentCenter.Value.Y;

                double primary;
                double secondary;

                switch (direction)
                {
                    case NavigationDirection.Right:
                        primary = dx;
                        secondary = Math.Abs(dy);
                        break;

                    case NavigationDirection.Left:
                        primary = -dx;
                        secondary = Math.Abs(dy);
                        break;

                    case NavigationDirection.Down:
                        primary = dy;
                        secondary = Math.Abs(dx);
                        break;

                    default:
                        primary = -dy;
                        secondary = Math.Abs(dx);
                        break;
                }

                if (primary <= 2)
                    continue;

                if (secondary > primary * coneRatio)
                    continue;

                // Strongly favor controls aligned with the requested axis.
                double score = (primary * 1.0) + (secondary * 4.0);
                if (score < bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            return best;
        }

        private Point? GetControlCenter(Control control)
        {
            var localCenter = new Point(control.Bounds.Width / 2.0, control.Bounds.Height / 2.0);
            return control.TranslatePoint(localCenter, this);
        }

        private void FocusFirstActiveElement()
        {
            var focusables = GetFocusableElementsInActiveSurface();
            if (focusables.Count == 0) return;
            FocusControl(focusables[0]);
        }

        private List<Control> GetFocusableElementsInActiveSurface()
        {
            var surface = GetActiveSurface();
            if (surface == null) return new List<Control>();

            return surface.GetVisualDescendants()
                .OfType<Control>()
                .Where(control => control.IsVisible
                                  && control.IsEnabled
                                  && control.Focusable
                                  && control is not ScrollViewer
                                  && control is not ScrollBar)
                .ToList();
        }

        private static void FocusControl(Control control)
        {
            control.Focus(NavigationMethod.Directional);
        }

        private bool HandleOpenComboBoxInput(GamepadButton button)
        {
            var openCombo = GetOpenedComboBox();

            if (openCombo == null) return false;

            switch (button)
            {
                case GamepadButton.DPadDown:
                case GamepadButton.ThumbLeftDown:
                    SimulateKey(Key.Down);
                    return true;

                case GamepadButton.DPadUp:
                case GamepadButton.ThumbLeftUp:
                    SimulateKey(Key.Up);
                    return true;

                case GamepadButton.A:
                    SimulateKey(Key.Enter);
                    return true;

                case GamepadButton.B:
                case GamepadButton.ThumbRightLeft:
                    openCombo.IsDropDownOpen = false;
                    SimulateKey(Key.Escape);
                    return true;
            }

            return true;
        }

        private ComboBox? GetOpenedComboBox()
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();

            if (focused is ComboBox focusedCombo
                && focusedCombo.IsVisible
                && focusedCombo.IsEnabled
                && focusedCombo.IsDropDownOpen)
            {
                return focusedCombo;
            }

            if (focused is Visual focusedVisual)
            {
                var ancestorCombo = focusedVisual.GetVisualAncestors()
                    .OfType<ComboBox>()
                    .FirstOrDefault(c => c.IsVisible && c.IsEnabled && c.IsDropDownOpen);
                if (ancestorCombo != null)
                    return ancestorCombo;
            }

            var comboNames = new[]
            {
                "CmbOptiVersion",
                "CmbExtrasVersion",
                "CmbFakenvapiVersion",
                "CmbInjectionMethod",
                "CmbOptiPatcherVersion",
                "CmbNukemFGVersion",
                "CmbProfile"
            };

            foreach (var name in comboNames)
            {
                var combo = this.FindControl<ComboBox>(name);
                if (combo?.IsVisible == true && combo.IsEnabled && combo.IsDropDownOpen)
                    return combo;
            }

            return null;
        }

        private void HandleBackAction()
        {
            if (!IsAnyModalVisible())
            {
                _ = CloseAnimated();
                return;
            }

            if (TryActivateButton("BtnCoverCancel")) return;
            if (TryActivateButton("BtnCorruptCancel")) return;
            if (TryActivateButton("BtnConfirmFolderCleanupNo")) return;
            if (TryActivateButton("BtnConfirmUninstallNo")) return;

            _ = CloseAnimated();
        }

        private bool IsAnyModalVisible()
        {
            return this.FindControl<Grid>("BdCoverModal")?.IsVisible == true
                   || this.FindControl<Grid>("BdConfirmCorruptInstall")?.IsVisible == true
                   || this.FindControl<Grid>("BdConfirmFolderCleanup")?.IsVisible == true
                   || this.FindControl<Grid>("BdConfirmUninstall")?.IsVisible == true;
        }

        private bool TryActivateButton(string name)
        {
            var button = this.FindControl<Button>(name);
            if (button == null || !button.IsVisible || !button.IsEnabled) return false;

            button.Focus(NavigationMethod.Directional);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return true;
        }

        private void ActivateFocusedElement()
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (focused == null) return;

            if (focused is ComboBox combo)
            {
                combo.IsDropDownOpen = !combo.IsDropDownOpen;
                return;
            }

            if (focused is Button button)
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return;
            }

            if (focused is Visual focusedVisual)
            {
                var ancestorButton = focusedVisual.GetVisualAncestors().OfType<Button>().FirstOrDefault();
                if (ancestorButton != null)
                {
                    ancestorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    return;
                }
            }

            SimulateKey(Key.Enter);
        }

        private void SimulateKey(Key key, KeyModifiers modifiers = KeyModifiers.None)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            var focused = topLevel?.FocusManager?.GetFocusedElement();
            var target = (focused as Interactive) ?? this;

            target.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = key,
                Source = target,
                KeyModifiers = modifiers
            });
        }

        private static ComboBoxItem BuildVersionItem(string ver, bool isBeta, bool isLatest)
        {
            var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new TextBlock { Text = ver, VerticalAlignment = VerticalAlignment.Center });

            if (isBeta)
            {
                var badge = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Color.Parse("#D4A017")),
                    Padding = new Thickness(5, 1),
                    Child = new TextBlock { Text = "BETA", FontSize = 10, Foreground = Brushes.White, FontWeight = Avalonia.Media.FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center }
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
                    Child = new TextBlock { Text = "LATEST", FontSize = 10, Foreground = Brushes.White, FontWeight = Avalonia.Media.FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center }
                };
                stack.Children.Add(badge);
            }

            return new ComboBoxItem { Content = stack, Tag = ver };
        }

        private async Task LoadVersionsAsync()
        {
            var componentService = new ComponentManagementService();

            // Load profiles (purely local/disk — always fast)
            var profileService = new ProfileManagementService();
            var profiles = profileService.GetAllProfiles();
            var defaultProfileName = componentService.Config.DefaultProfileName;
            _defaultProfileName = !string.IsNullOrWhiteSpace(defaultProfileName)
                && profiles.Any(p => p.Name.Equals(defaultProfileName, StringComparison.OrdinalIgnoreCase))
                    ? defaultProfileName
                    : profileService.GetDefaultProfile().Name;

            // Immediately populate ALL selectors from disk cache (no API wait).
            // This eliminates the ~1s "popup" delay when versions are already cached.
            PopulateProfileSelector(profileService, profiles, _lastSelectedProfileName ?? _defaultProfileName);
            PopulateVersionSelectors(componentService);

            // Wait for the GitHub API check (may block if startup check is in-flight,
            // which is intentional — the semaphore prevents concurrent fetches and ensures
            // we get fresh data before the second populate).
            // Always re-populate selectors afterwards, even if the check threw.
            try
            {
                await componentService.CheckForUpdatesAsync();
            }
            catch (GitHubRateLimitException) { /* rate limited — show whatever is cached */ }
            catch (Exception) { /* network error — show whatever is cached */ }
            finally
            {
                // Re-populate version selectors with updated data from API (or from cache if API was skipped/failed)
                PopulateVersionSelectors(componentService);
            }
        }

        /// <summary>
        /// Populates the OptiScaler version, Extras, and OptiPatcher combo boxes
        /// from whatever is currently in the ComponentManagementService's static cache.
        /// Safe to call multiple times — properly unregisters/re-registers event handlers.
        /// </summary>
        private void PopulateVersionSelectors(ComponentManagementService componentService)
        {
            _cachedComponentService = componentService;
            _betaVersions = componentService.BetaVersions;
            _nightlyVersions = componentService.NightlyVersions;
            _customVersions = componentService.CustomVersions;

            // Show/hide Custom tab based on whether custom versions exist
            var btnCustom = this.FindControl<Button>("BtnOptiCustom");
            var gridTabs = this.FindControl<Grid>("GridOptiTabs");
            bool hasCustom = _customVersions.Count > 0;
            if (btnCustom != null) btnCustom.IsVisible = hasCustom;
            if (gridTabs != null)
                gridTabs.ColumnDefinitions = hasCustom
                    ? new ColumnDefinitions("*,*,*,*")
                    : new ColumnDefinitions("*,*,*");

            // Determine initial tab only on the first load
            if (!_optiTabInitialized)
            {
                var configDefault = componentService.EffectiveDefaultOptiScalerVersion;
                _optiShowingBeta = !string.IsNullOrEmpty(configDefault) && _betaVersions.Contains(configDefault);
                _optiShowingNightly = !string.IsNullOrEmpty(configDefault) && _nightlyVersions.Contains(configDefault);
                _optiShowingCustom = !string.IsNullOrEmpty(configDefault) && _customVersions.Contains(configDefault);
                if (_optiShowingCustom || _optiShowingNightly) _optiShowingBeta = false;
                if (_optiShowingCustom) _optiShowingNightly = false;
                _optiTabInitialized = true;
            }

            UpdateOptiChannelButtons();
            PopulateOptiVersionCombo(componentService);

            // ── Populate FSR4 INT8 Extras selector ────────────────────────────
            PopulateExtrasComboBox(componentService);

            // ── Populate OptiPatcher selector ─────────────────────────────────
            PopulateOptiPatcherComboBox(componentService);

            // ── Populate NukemFG selector ─────────────────────────────────────
            PopulateNukemFGComboBox(componentService);

            // ── Populate Fakenvapi selector ───────────────────────────────────
            PopulateFakenvapiComboBox(componentService);
        }

        // ── OptiScaler tab selector ──────────────────────────────────────────

        private void PopulateOptiVersionCombo(ComponentManagementService componentService)
        {
            var allVersions = componentService.OptiScalerAvailableVersions;
            var betaVersions = componentService.BetaVersions;
            var nightlyVersions = componentService.NightlyVersions;
            var customVersions = _customVersions;
            var latestStable = componentService.LatestStableVersion;
            var latestBeta = componentService.LatestBetaVersion;
            var latestNightly = componentService.LatestNightlyVersion;

            string? latestInChannel = _optiShowingCustom ? null : _optiShowingNightly ? latestNightly : (_optiShowingBeta ? latestBeta : latestStable);
            string latestBadgeColor = _optiShowingNightly ? "#0EA5E9" : _optiShowingBeta ? "#D4A017" : "#7C3AED";

            var cmbOptiVersion = this.FindControl<ComboBox>("CmbOptiVersion");
            if (cmbOptiVersion == null) return;

            cmbOptiVersion.SelectionChanged -= CmbOptiVersion_SelectionChanged;
            cmbOptiVersion.Items.Clear();

            // "None" always comes first, in every channel — lets the user do a DLL-only swap
            // (see ExecuteDllSwapAsync) without installing OptiScaler at all. Never auto-selected
            // by the logic below; only reached if the user picks it manually.
            cmbOptiVersion.Items.Add(new ComboBoxItem { Content = GetResourceString("TxtOptiVersionNone", "None"), Tag = "none" });

            if (allVersions.Count == 0 && !_optiShowingCustom)
            {
                cmbOptiVersion.Items.Add(new ComboBoxItem { Content = GetResourceString("TxtNoOptiDetected", "No version detected"), IsEnabled = false });
                cmbOptiVersion.SelectedIndex = 0;
                cmbOptiVersion.IsEnabled = true;
                cmbOptiVersion.SelectionChanged += CmbOptiVersion_SelectionChanged;
                UpdateInstallButtonsForSwapState();
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
                cmbOptiVersion.Items.Add(new ComboBoxItem { Content = "No versions available", IsEnabled = false });
                cmbOptiVersion.SelectedIndex = 0;
                cmbOptiVersion.IsEnabled = true;
                cmbOptiVersion.SelectionChanged += CmbOptiVersion_SelectionChanged;
                UpdateInstallButtonsForSwapState();
                return;
            }

            cmbOptiVersion.IsEnabled = true;

            foreach (var ver in versionsToShow)
            {
                bool isLatest = string.Equals(ver, latestInChannel, StringComparison.OrdinalIgnoreCase);
                ComboBoxItem cbi;
                if (isLatest)
                {
                    var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                    stack.Children.Add(new TextBlock { Text = ver, VerticalAlignment = VerticalAlignment.Center });
                    stack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(4),
                        Background = new SolidColorBrush(Color.Parse(latestBadgeColor)),
                        Padding = new Thickness(5, 1),
                        Child = new TextBlock { Text = "LATEST", FontSize = 10, Foreground = Brushes.White, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center }
                    });
                    cbi = new ComboBoxItem { Content = stack, Tag = ver };
                }
                else
                {
                    cbi = new ComboBoxItem { Content = ver, Tag = ver };
                }
                cmbOptiVersion.Items.Add(cbi);
            }

            // Select version: try to match config default if it's in this channel, else select first
            // real version (latest). Index 0 is always "None" and is never picked here — it only
            // gets selected by explicit user action, per the DLL-swap feature's requirement that it
            // never becomes a silent default.
            int selectedIndex = 1;
            var configDefault = componentService.EffectiveDefaultOptiScalerVersion;
            bool defaultInChannel = !string.IsNullOrEmpty(configDefault) &&
                (_optiShowingCustom
                    ? customVersions.Contains(configDefault)
                    : !customVersions.Contains(configDefault) &&
                      nightlyVersions.Contains(configDefault) == _optiShowingNightly &&
                      betaVersions.Contains(configDefault) == _optiShowingBeta);
            if (defaultInChannel)
            {
                for (int i = 1; i < cmbOptiVersion.Items.Count; i++)
                {
                    if (cmbOptiVersion.Items[i] is ComboBoxItem ci &&
                        string.Equals(ci.Tag?.ToString(), configDefault, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedIndex = i;
                        break;
                    }
                }
            }

            cmbOptiVersion.SelectedIndex = selectedIndex;
            UpdateCheckboxStatesForVersion(cmbOptiVersion);
            cmbOptiVersion.SelectionChanged += CmbOptiVersion_SelectionChanged;
            // The handler is detached while rebuilding the list, so a programmatic switch
            // from None to the first version must update the install-state explicitly.
            UpdateInstallButtonsForSwapState();
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
            if (_cachedComponentService != null)
                PopulateOptiVersionCombo(_cachedComponentService);
        }

        private void BtnOptiBeta_Click(object? sender, RoutedEventArgs e)
        {
            if (_optiShowingBeta) return;
            _optiShowingBeta = true;
            _optiShowingNightly = false;
            _optiShowingCustom = false;
            UpdateOptiChannelButtons();
            if (_cachedComponentService != null)
                PopulateOptiVersionCombo(_cachedComponentService);
        }

        private void BtnOptiNightly_Click(object? sender, RoutedEventArgs e)
        {
            if (_optiShowingNightly) return;
            _optiShowingNightly = true;
            _optiShowingBeta = false;
            _optiShowingCustom = false;
            UpdateOptiChannelButtons();
            if (_cachedComponentService != null)
                PopulateOptiVersionCombo(_cachedComponentService);
        }

        private void BtnOptiCustom_Click(object? sender, RoutedEventArgs e)
        {
            if (_optiShowingCustom) return;
            _optiShowingCustom = true;
            _optiShowingBeta = false;
            _optiShowingNightly = false;
            UpdateOptiChannelButtons();
            if (_cachedComponentService != null)
                PopulateOptiVersionCombo(_cachedComponentService);
        }

        /// <summary>
        /// Populates CmbExtrasVersion with available Extras versions + a "None" option.
        /// Selects the default based on GPU generation: RDNA 4 → None, others → global default or latest.
        /// </summary>
        private void PopulateExtrasComboBox(ComponentManagementService componentService)
        {
            var cmb = this.FindControl<ComboBox>("CmbExtrasVersion");
            if (cmb == null) return;

            if (!_extrasTabInitialized)
            {
                var defaultVersion = componentService.Config.DefaultExtrasVersion;
                if (!string.IsNullOrWhiteSpace(defaultVersion) &&
                    !defaultVersion.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    _extrasVariant = componentService.GetExtrasDllVariant(defaultVersion);
                }
                _extrasTabInitialized = true;
            }

            UpdateExtrasVariantButtons();

            cmb.SelectionChanged -= CmbExtrasVersion_SelectionChanged;
            cmb.Items.Clear();

            var versions = componentService.ExtrasAvailableVersions
                .Where(version => componentService.GetExtrasDllVariant(version) == _extrasVariant)
                .ToList();
            var latestInVariant = versions.FirstOrDefault();
            if (versions.Count == 0)
            {
                cmb.Items.Add(new ComboBoxItem { Content = GetResourceString("TxtNoVersions", "No versions available"), Tag = "none" });
                cmb.SelectedIndex = 0;
                cmb.IsEnabled = false;
                cmb.SelectionChanged += CmbExtrasVersion_SelectionChanged;
                return;
            }
            cmb.IsEnabled = true;

            // Option 0: None
            cmb.Items.Add(new ComboBoxItem { Content = "None", Tag = "none" });

            foreach (var ver in versions)
            {
                var isLatest = string.Equals(ver, latestInVariant, StringComparison.OrdinalIgnoreCase);
                var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                stack.Children.Add(new TextBlock { Text = componentService.GetExtrasDllDisplayName(ver), VerticalAlignment = VerticalAlignment.Center });
                if (isLatest)
                {
                    stack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(4),
                        Background = new SolidColorBrush(Color.Parse("#7C3AED")),
                        Padding = new Thickness(5, 1),
                        Child = new TextBlock { Text = "LATEST", FontSize = 10, Foreground = Brushes.White, FontWeight = Avalonia.Media.FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center }
                    });
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
                    var gpu = GpuSelectionHelper.GetPreferredGpu(_gpuService, componentService.Config.DefaultGpuId);
                    isRdna4OrRdna3 = GpuSelectionHelper.IsRdna4(gpu) || GpuSelectionHelper.IsRdna3(gpu);
                    isRdna2 = GpuSelectionHelper.IsRdna2(gpu);
                }
                catch (Exception ex) { DebugWindow.Log($"[ManageGame] GPU detection failed: {ex.Message}"); }
            }

            // Determine target index
            int targetIndex = 0; // Default to None (index 0)
            var globalDefault = componentService.Config.DefaultExtrasVersion;

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
                                ? componentService.GetRdna2PreferredExtrasVersion()
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
                        ? componentService.GetRdna2PreferredExtrasVersion()
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
        }  // end PopulateExtrasComboBox

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
            if (_cachedComponentService != null)
                PopulateExtrasComboBox(_cachedComponentService);
        }

        private void BtnExtrasFp8_Click(object? sender, RoutedEventArgs e)
        {
            if (_extrasVariant == Fsr4DllVariant.Fp8) return;
            _extrasVariant = Fsr4DllVariant.Fp8;
            if (_cachedComponentService != null)
                PopulateExtrasComboBox(_cachedComponentService);
        }

        private static Border CreateFsr4VariantBadge(Fsr4DllVariant variant) => new()
        {
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.Parse(variant == Fsr4DllVariant.Fp8 ? "#2563EB" : "#16A34A")),
            Padding = new Thickness(5, 1),
            Child = new TextBlock { Text = variant == Fsr4DllVariant.Fp8 ? "FP8" : "INT8", FontSize = 10, Foreground = Brushes.White, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center }
        };

        /// <summary>
        /// Recomputes the Auto/Manual-Install vs. Auto/Manual-Swap-DLL button state whenever either
        /// CmbOptiVersion or CmbExtrasVersion changes. See the DLL-swap plan's state matrix:
        /// Opti=none &amp; Extras=none → disabled; Opti=none &amp; Extras=version → swap-mode labels;
        /// anything with a real Opti version → normal install labels (handled by UpdateStatus).
        /// </summary>
        private void CmbExtrasVersion_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            UpdateInstallButtonsForSwapState();
        }

        /// <summary>
        /// Populates CmbOptiPatcherVersion with available OptiPatcher versions + a "None" option.
        /// Respects the configured DefaultOptiPatcherVersion from settings.
        /// </summary>
        private void PopulateOptiPatcherComboBox(ComponentManagementService componentService)
        {
            var cmb = this.FindControl<ComboBox>("CmbOptiPatcherVersion");
            if (cmb == null) return;

            cmb.Items.Clear();

            // Option 0: None (default — opt-in)
            cmb.Items.Add(new ComboBoxItem { Content = "None", Tag = "none" });

            var versions = componentService.OptiPatcherAvailableVersions;
            foreach (var ver in versions)
            {
                var isLatest = ver == componentService.LatestOptiPatcherVersion;
                cmb.Items.Add(BuildVersionItem(ver, isBeta: false, isLatest: isLatest));
            }

            int targetIndex = 0;

            // The wiki's Compatibility List flags this game as needing OptiPatcher — auto-select
            // the latest version instead of falling back to the user's saved global default.
            var latestVersion = componentService.LatestOptiPatcherVersion;
            var wantsAutoLatest = _compatEntry != null && _compatEntry.OptiPatcherSupported && !string.IsNullOrEmpty(latestVersion);
            var targetVersion = wantsAutoLatest ? latestVersion : componentService.Config.DefaultOptiPatcherVersion;

            if (!string.IsNullOrEmpty(targetVersion) && !targetVersion.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 1; i < cmb.Items.Count; i++)
                {
                    if (cmb.Items[i] is ComboBoxItem ci &&
                        string.Equals(ci.Tag?.ToString(), targetVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        targetIndex = i;
                        break;
                    }
                }
            }

            cmb.SelectedIndex = targetIndex;
        }

        /// <summary>
        /// Populates CmbNukemFGVersion with cached NukemFG versions + "None" + "Manage versions…" option.
        /// </summary>
        private void PopulateNukemFGComboBox(ComponentManagementService componentService)
        {
            var cmb = this.FindControl<ComboBox>("CmbNukemFGVersion");
            if (cmb == null) return;

            cmb.Items.Clear();

            // Option 0: None (default — opt-in)
            cmb.Items.Add(new ComboBoxItem { Content = "None", Tag = "none" });

            var versions = componentService.GetDownloadedNukemFGVersions();
            foreach (var ver in versions)
            {
                cmb.Items.Add(new ComboBoxItem { Content = ver, Tag = ver });
            }

            // Last option: Manage versions...
            cmb.Items.Add(new ComboBoxItem { Content = "Manage versions…", Tag = "__manage__" });

            // Pre-select configured default
            var savedNukemFG = componentService.Config.DefaultNukemFGVersion;
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
                    // Reset selection to None
                    cmb.SelectedIndex = 0;
                    // Open CacheManagementWindow
                    var cacheWindow = new CacheManagementWindow("nukemfg");
                    cacheWindow.ShowDialog(this);
                }
            };
        }

        /// <summary>
        /// Populates CmbFakenvapiVersion with available Fakenvapi versions + "None" + "Manage versions…".
        /// Shows a "latest" badge on the latest version.
        /// </summary>
        private void PopulateFakenvapiComboBox(ComponentManagementService componentService)
        {
            var cmb = this.FindControl<ComboBox>("CmbFakenvapiVersion");
            if (cmb == null) return;

            cmb.Items.Clear();

            // Option 0: None (default — opt-in)
            cmb.Items.Add(new ComboBoxItem { Content = "None", Tag = "none" });

            var versions = componentService.FakenvapiAvailableVersions;
            foreach (var ver in versions)
            {
                var isLatest = ver == componentService.LatestFakenvapiVersion;
                cmb.Items.Add(BuildVersionItem(ver, isBeta: false, isLatest: isLatest));
            }

            // Last option: Manage versions…
            cmb.Items.Add(new ComboBoxItem { Content = "Manage versions…", Tag = "__manage__" });

            // Pre-select configured default
            var savedFakenvapi = componentService.Config.DefaultFakenvapiVersion;
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

        private void CheckIfAntiCheat()
        {
            const string anticheatName = "start_protected_game.exe";
            var anticheatPanel = this.FindControl<Border>("EasyAntiCheat");

            bool antiCheatFound = !string.IsNullOrEmpty(_game?.InstallPath) &&
                         File.Exists(System.IO.Path.Combine(_game.InstallPath, anticheatName));

            if (anticheatPanel != null)
            {
                anticheatPanel.IsVisible = antiCheatFound;
                anticheatPanel.IsEnabled = antiCheatFound;
            }
        }

        private void UpdateCheckboxStatesForVersion(ComboBox? cmb)
        {
            if (cmb == null) return;

            var selectedTag = (cmb?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            bool isBeta = !string.IsNullOrEmpty(selectedTag) && _betaVersions.Contains(selectedTag);
            bool isNightly = !string.IsNullOrEmpty(selectedTag) && _nightlyVersions.Contains(selectedTag);

            // Stable/Beta 0.9+ bundle both components. Nightly resolves Fakenvapi automatically
            // per game when fakenvapi.dll is absent, so its manual selector remains disabled.
            bool includedInPackage = !isNightly && IsVersionGreaterOrEqual(selectedTag, 0, 9);
            bool disableFakenvapi = isNightly || includedInPackage;
            bool disableNukemFG = isNightly || includedInPackage;

            var cmbFakenvapi = this.FindControl<ComboBox>("CmbFakenvapiVersion");
            var cmbNukemFG = this.FindControl<ComboBox>("CmbNukemFGVersion");
            var fakenvapiPanel = this.FindControl<StackPanel>("PanelFakenvapiVersion");
            var nukemFGPanel = this.FindControl<StackPanel>("PanelNukemFGVersion");
            var betaInfoPanel = this.FindControl<Border>("BetaInfoPanel");

            // Since OptiScaler 0.9 these components are included in the package; Nightly
            // obtains Fakenvapi automatically when it is required. Hide both manual selectors
            // instead of leaving disabled controls that take up space.
            if (fakenvapiPanel != null) fakenvapiPanel.IsVisible = !disableFakenvapi;
            if (nukemFGPanel != null) nukemFGPanel.IsVisible = !disableNukemFG;
            UpdateOptionsLayout(disableFakenvapi && disableNukemFG);

            // The existing info text applies only to releases that bundle their components.
            if (betaInfoPanel != null)
            {
                betaInfoPanel.IsVisible = isBeta || includedInPackage;
            }

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

            // Extract numeric prefix (e.g. "0.9.1" from "v0.9.1-beta" or "0.9.1-beta")
            var m = Regex.Match(ver, "^v?(\\d+(?:\\.\\d+)*)");
            if (!m.Success) return false;

            if (!Version.TryParse(m.Groups[1].Value, out var parsed)) return false;

            if (parsed.Major > targetMajor) return true;
            if (parsed.Major < targetMajor) return false;
            // Majors equal
            var minor = parsed.Minor;
            return minor >= targetMinor;
        }

        private void SetupUI()
        {
            var txtGameName = this.FindControl<TextBlock>("TxtGameName");
            var txtInstallPath = this.FindControl<TextBlock>("TxtInstallPath");
            var txtGameNameEdit = this.FindControl<TextBox>("TxtGameNameEdit");
            var imgGameCover = this.FindControl<Image>("ImgGameCover");

            if (txtGameName != null) txtGameName.Text = _game.Name;
            if (txtInstallPath != null) txtInstallPath.Text = _game.InstallPath;
            if (txtGameNameEdit != null) txtGameNameEdit.Text = _game.Name;
            TrySetCoverImage(imgGameCover, _game.CoverImageUrl);

            UpdateStatus();
            LoadComponents();
            ConfigureAdditionalComponents();
            CheckIfAntiCheat();
            PopulateCompatibilitySidebar();
            SetupFrameGenerationButton();
            SetupUpscalingQualitySelector();

        }

        /// <summary>
        /// Fills the "Recommended Config" sidebar from the locally cached Compatibility List
        /// (already refreshed at app startup by CompatibilityListService — this is a pure,
        /// synchronous, network-free local lookup, safe to call while building the window).
        /// </summary>
        private void PopulateCompatibilitySidebar()
        {
            var pnlFound = this.FindControl<StackPanel>("PnlCompatFound");
            var pnlNotFound = this.FindControl<StackPanel>("PnlCompatNotFound");
            var pnlFetching = this.FindControl<Border>("PnlCompatFetching");
            if (pnlFound == null || pnlNotFound == null) return;

            _wikiPageUrl = null;
            _injectionMethodAutoSelected = false;
            var pnlWikiDetails = this.FindControl<StackPanel>("PnlWikiDetailsSection");
            var pnlWikiFetching = this.FindControl<Border>("PnlWikiFetching");
            var btnGameWikiLink = this.FindControl<Button>("BtnGameWikiLink");
            if (pnlWikiDetails != null) pnlWikiDetails.IsVisible = false;
            if (pnlWikiFetching != null) pnlWikiFetching.IsVisible = false;
            if (btnGameWikiLink != null) btnGameWikiLink.IsVisible = false;
            if (pnlFetching != null) pnlFetching.IsVisible = false;
            StopWikiFetchingAnimation();

            var compatService = new CompatibilityListService();
            if (!compatService.TryGetForGame(_game.Name, out var entry) || entry == null)
            {
                pnlFound.IsVisible = false;
                _compatEntry = null;
                if (CompatibilityListService.IsRefreshInProgress)
                {
                    ShowCompatibilityListFetchingState();
                }
                else
                {
                    pnlNotFound.IsVisible = true;
                }
                return;
            }

            StopWaitingForCompatibilityRefresh();
            _compatEntry = entry;
            var hasWikiPage = !string.IsNullOrEmpty(entry.WikiPageSlug);

            // Show whatever's already cached immediately, even if stale — never make the user
            // wait on a network round-trip to see data they've already seen before. The cooldown
            // check inside PopulateWikiDetailsAsync silently refreshes it in the background and
            // updates the fields in place if anything changed. The "Fetching…" spinner is reserved
            // for the one case where there's nothing to show yet at all (first time for this page).
            var cachedWikiDetails = hasWikiPage ? compatService.GetCachedGameWikiDetails(entry) : null;
            if (cachedWikiDetails != null)
            {
                RenderWikiDetails(cachedWikiDetails);
                if (pnlWikiFetching != null) pnlWikiFetching.IsVisible = false;
            }
            else if (hasWikiPage)
            {
                if (pnlWikiFetching != null) pnlWikiFetching.IsVisible = true;
                StartWikiFetchingAnimation();
            }
            if (hasWikiPage) _ = PopulateWikiDetailsAsync(compatService, entry, hadCachedDetails: cachedWikiDetails != null);
            pnlNotFound.IsVisible = false;
            pnlFound.IsVisible = true;

            var ellipseStatus = this.FindControl<Ellipse>("EllipseCompatStatus");
            var txtStatus = this.FindControl<TextBlock>("TxtCompatStatus");
            if (ellipseStatus != null && txtStatus != null)
            {
                var (brushKey, textKey, fallback) = entry.Status switch
                {
                    CompatibilityStatus.Compatible => ("BrSuccess", "TxtCompatSidebarStatusCompatible", "Compatible with OptiScaler"),
                    CompatibilityStatus.NotCompatible => ("BrError", "TxtCompatSidebarStatusNotCompatible", "Not compatible"),
                    CompatibilityStatus.SingleOsOnly => ("BrWarning", "TxtCompatSidebarStatusSingleOs", "Compatible (single OS only)"),
                    _ => ("BrTextSecondary", "TxtCompatSidebarStatusUnconfirmed", "Unconfirmed")
                };
                var brush = this.FindResource(brushKey) as IBrush;
                ellipseStatus.Fill = brush;
                txtStatus.Foreground = brush;
                txtStatus.Text = GetResourceString(textKey, fallback);
            }

            var pnlUpscalerSection = this.FindControl<StackPanel>("PnlUpscalerInputsSection");
            var pnlUpscalerBadges = this.FindControl<WrapPanel>("PnlUpscalerInputsBadges");
            if (pnlUpscalerSection != null && pnlUpscalerBadges != null)
            {
                pnlUpscalerBadges.Children.Clear();
                var inputs = entry.UpscalerInputs
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(s => s.Length > 0)
                    .ToList();

                pnlUpscalerSection.IsVisible = inputs.Count > 0;
                foreach (var input in inputs)
                {
                    pnlUpscalerBadges.Children.Add(BuildUpscalerInputBadge(input));
                }
            }

            var txtOptiPatcherIcon = this.FindControl<TextBlock>("TxtOptiPatcherIcon");
            var txtOptiPatcherStatus = this.FindControl<TextBlock>("TxtOptiPatcherStatus");
            if (txtOptiPatcherIcon != null && txtOptiPatcherStatus != null)
            {
                if (entry.OptiPatcherSupported)
                {
                    txtOptiPatcherIcon.Text = ""; // ic_fluent_checkmark_circle_20_regular
                    txtOptiPatcherIcon.Foreground = this.FindResource("BrSuccess") as IBrush;
                    txtOptiPatcherStatus.Text = GetResourceString("TxtCompatSidebarOptiPatcherYes", "Supported");
                    txtOptiPatcherStatus.Foreground = this.FindResource("BrTextPrimary") as IBrush;
                }
                else
                {
                    txtOptiPatcherIcon.Text = "";
                    txtOptiPatcherStatus.Text = GetResourceString("TxtCompatSidebarOptiPatcherNo", "Not required");
                    txtOptiPatcherStatus.Foreground = this.FindResource("BrTextSecondary") as IBrush;
                }
            }

            var pnlNotesSection = this.FindControl<StackPanel>("PnlCompatNotesSection");
            var txtNotes = this.FindControl<TextBlock>("TxtCompatNotes");
            if (pnlNotesSection != null && txtNotes != null)
            {
                var hasNotes = !string.IsNullOrWhiteSpace(entry.Notes);
                pnlNotesSection.IsVisible = hasNotes;
                txtNotes.Text = entry.Notes;
            }
        }

        /// <summary>
        /// Packs the remaining selectors left-to-right when OptiScaler supplies the legacy
        /// components itself. Older versions retain the full three-column layout.
        /// </summary>
        private void UpdateOptionsLayout(bool useCompactLayout)
        {
            var opti = this.FindControl<StackPanel>("PanelOptiScalerVersion");
            var extras = this.FindControl<StackPanel>("PanelExtrasVersion");
            var injection = this.FindControl<StackPanel>("PanelInjectionMethod");
            var patcher = this.FindControl<StackPanel>("PanelOptiPatcherVersion");
            var profile = this.FindControl<StackPanel>("PanelProfile");
            var frameGeneration = this.FindControl<StackPanel>("PanelFrameGeneration");
            var upscalingQuality = this.FindControl<StackPanel>("PanelUpscalingQuality");
            var injectionLabel = this.FindControl<TextBlock>("LblInjectionMethod");

            if (opti == null || extras == null || injection == null || patcher == null
                || profile == null || frameGeneration == null || upscalingQuality == null)
                return;

            // Full: Opti / FSR4 / Fakenvapi, then injection / patcher / NukemFG.
            // Compact: Opti / FSR4 / injection, then patcher / profile / Frame Generation.
            Grid.SetRow(opti, 0); Grid.SetColumn(opti, 0);
            Grid.SetRow(extras, 0); Grid.SetColumn(extras, 1);
            Grid.SetRow(injection, useCompactLayout ? 0 : 1);
            Grid.SetColumn(injection, useCompactLayout ? 2 : 0);
            Grid.SetRow(patcher, useCompactLayout ? 1 : 1);
            Grid.SetColumn(patcher, useCompactLayout ? 0 : 1);
            Grid.SetRow(profile, useCompactLayout ? 1 : 2);
            Grid.SetColumn(profile, useCompactLayout ? 1 : 0);
            Grid.SetRow(frameGeneration, useCompactLayout ? 1 : 2);
            Grid.SetColumn(frameGeneration, useCompactLayout ? 2 : 1);
            Grid.SetRow(upscalingQuality, 2);
            Grid.SetColumn(upscalingQuality, useCompactLayout ? 0 : 2);

            if (injectionLabel != null)
                injectionLabel.Margin = useCompactLayout ? new Thickness(0, 32, 0, 0) : default;
        }

        private void SetupFrameGenerationButton()
        {
            if (_game.FrameGenerationSettings == null)
            {
                _game.FrameGenerationSettings = new GameFrameGenerationSettings
                {
                    Route = FrameGenerationRoute.Disabled,
                    Output = FrameGenerationOutput.Auto,
                    MultiFrameMode = MultiFrameGenerationMode.Auto
                };
            }
            UpdateFrameGenerationSummary();
        }

        private void SetupUpscalingQualitySelector()
        {
            _game.UpscalingQualitySettings ??= new GameUpscalingQualitySettings();
            PopulateUpscalingQualitySelector(_game.UpscalingQualitySettings.Preset);
        }

        private void PopulateUpscalingQualitySelector(UpscalingQualityPreset selected)
        {
            var combo = this.FindControl<ComboBox>("CmbUpscalingQuality");
            if (combo == null) return;

            _isUpdatingUpscalingQuality = true;
            try
            {
                combo.Items.Clear();
                AddUpscalingQualityItem(combo, GetResourceString("TxtQualityGameControlled", "Game controlled"), UpscalingQualityPreset.GameControlled);
                AddUpscalingQualityItem(combo, "Native", UpscalingQualityPreset.NativeAa);
                AddUpscalingQualityItem(combo, "Ultra Quality", UpscalingQualityPreset.UltraQuality);
                AddUpscalingQualityItem(combo, "Quality", UpscalingQualityPreset.Quality);
                AddUpscalingQualityItem(combo, "Balanced", UpscalingQualityPreset.Balanced);
                AddUpscalingQualityItem(combo, "Performance", UpscalingQualityPreset.Performance);
                AddUpscalingQualityItem(combo, "Ultra Performance", UpscalingQualityPreset.UltraPerformance);
                AddUpscalingQualityItem(combo, GetResourceString("TxtCustom", "Custom"), UpscalingQualityPreset.Custom);

                for (var index = 0; index < combo.Items.Count; index++)
                {
                    if (combo.Items[index] is ComboBoxItem item && item.Tag is UpscalingQualityPreset preset && preset == selected)
                    {
                        combo.SelectedIndex = index;
                        return;
                    }
                }
                combo.SelectedIndex = 0;
            }
            finally
            {
                _isUpdatingUpscalingQuality = false;
            }
        }

        private static void AddUpscalingQualityItem(ComboBox combo, string label, UpscalingQualityPreset preset)
            => combo.Items.Add(new ComboBoxItem { Content = label, Tag = preset });

        private void SelectUpscalingQualityPreset(UpscalingQualityPreset selected)
        {
            var combo = this.FindControl<ComboBox>("CmbUpscalingQuality");
            if (combo == null) return;

            _isUpdatingUpscalingQuality = true;
            try
            {
                for (var index = 0; index < combo.Items.Count; index++)
                {
                    if (combo.Items[index] is ComboBoxItem item
                        && item.Tag is UpscalingQualityPreset preset
                        && preset == selected)
                    {
                        combo.SelectedIndex = index;
                        return;
                    }
                }
            }
            finally
            {
                _isUpdatingUpscalingQuality = false;
            }
        }

        private async void CmbUpscalingQuality_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUpscalingQuality || sender is not ComboBox combo
                || combo.SelectedItem is not ComboBoxItem item
                || item.Tag is not UpscalingQualityPreset selected)
                return;

            if (selected == UpscalingQualityPreset.Custom)
                _qualityCustomHandledForOpen = true;
            await ApplyUpscalingQualitySelectionAsync(selected);
        }

        private void CmbUpscalingQuality_DropDownOpened(object? sender, EventArgs e)
            => _qualityCustomHandledForOpen = false;

        private async void CmbUpscalingQuality_DropDownClosed(object? sender, EventArgs e)
        {
            if (_isUpdatingUpscalingQuality || _qualityCustomHandledForOpen
                || sender is not ComboBox combo
                || combo.SelectedItem is not ComboBoxItem item
                || item.Tag is not UpscalingQualityPreset.Custom)
                return;

            _qualityCustomHandledForOpen = true;
            await ApplyUpscalingQualitySelectionAsync(UpscalingQualityPreset.Custom);
        }

        private async Task ApplyUpscalingQualitySelectionAsync(UpscalingQualityPreset selected)
        {

            var previous = _game.UpscalingQualitySettings ?? new GameUpscalingQualitySettings();
            var previousPreset = previous.Preset;
            var customRatio = previous.CustomRatio;

            if (selected == UpscalingQualityPreset.Custom)
            {
                var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
                var outputResolution = screen == null
                    ? new PixelSize(2560, 1440)
                    : new PixelSize(screen.Bounds.Width, screen.Bounds.Height);
                var dialog = new UpscalingQualityCustomWindow(this, customRatio, outputResolution);
                var result = await dialog.ShowDialog<double?>(this);
                if (result == null)
                {
                    SelectUpscalingQualityPreset(previousPreset);
                    return;
                }
                customRatio = result.Value;
            }

            _game.UpscalingQualitySettings = new GameUpscalingQualitySettings
            {
                Preset = selected,
                CustomRatio = customRatio,
                AppliedAtUtc = previous.AppliedAtUtc
            };

            if (!_game.IsOptiscalerInstalled) return;

            try
            {
                await Task.Run(() => new GameInstallationService().ApplyUpscalingQualitySettings(_game));
                NeedsScan = true;
            }
            catch (Exception ex)
            {
                await new ConfirmDialog(this,
                    GetResourceString("TxtUpscalingQuality", "Upscaling Quality"),
                    $"{GetResourceString("TxtUpscalingQualityApplyError", "Could not apply the upscaling quality configuration:")}\n{ex.Message}")
                    .ShowDialog<object>(this);
            }
        }

        private async void BtnFrameGeneration_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var componentService = new ComponentManagementService();
                var gpu = GpuSelectionHelper.GetPreferredGpu(_gpuService, componentService.Config.DefaultGpuId);
                var dialog = new FrameGenerationSettingsWindow(this, _game, gpu);
                var settings = await dialog.ShowDialog<GameFrameGenerationSettings?>(this);
                if (settings == null) return;

                _game.FrameGenerationSettings = settings;
                UpdateFrameGenerationSummary();

                if (_game.IsOptiscalerInstalled)
                {
                    await Task.Run(() => new GameInstallationService().ApplyFrameGenerationSettings(_game, gpu: gpu));
                    NeedsScan = true;
                }
            }
            catch (Exception ex)
            {
                await new ConfirmDialog(this, "Frame Generation", $"Could not apply frame generation configuration:\n{ex.Message}").ShowDialog<object>(this);
            }
        }

        private void UpdateFrameGenerationSummary()
        {
            var button = this.FindControl<Button>("BtnFrameGeneration");
            var selection = this.FindControl<TextBlock>("TxtFrameGenerationSelection");
            var settings = _game.FrameGenerationSettings;
            if (button == null || selection == null || settings == null) return;

            var route = settings.Route == FrameGenerationRoute.Auto
                ? GetResourceString("TxtFgRouteAuto", "Auto")
                : GetFrameGenerationRouteSummary(settings.Route);
            var output = GetFrameGenerationOutputSummary(settings.Output);
            var multiplier = settings.MultiFrameMode == MultiFrameGenerationMode.Auto
                ? "Auto"
                : settings.MultiFrameMode.ToString().Replace("X", "x");
            selection.Text = settings.Route == FrameGenerationRoute.Disabled ? route : output;
            ToolTip.SetTip(button, settings.Route == FrameGenerationRoute.Disabled
                ? route
                : $"{route} → {output} · {multiplier}");
        }

        private string GetFrameGenerationRouteSummary(FrameGenerationRoute route) => route switch
        {
            FrameGenerationRoute.Disabled => GetResourceString("TxtFgRouteDisabled", "Disabled"),
            FrameGenerationRoute.DlssGStreamline => GetResourceString("TxtFgRouteDlssStreamline", "DLSS-G via Streamline"),
            FrameGenerationRoute.Nukem => GetResourceString("TxtFgRouteNukem", "Nukem DLSS-G → FSR3"),
            FrameGenerationRoute.Fsr31Native => GetResourceString("TxtFgRouteFsr31", "Native FSR 3.1 FG"),
            FrameGenerationRoute.Fsr30Native => GetResourceString("TxtFgRouteFsr30", "Native FSR 3.0 FG"),
            FrameGenerationRoute.OptiFg => GetResourceString("TxtFgRouteOptiFg", "OptiFG (experimental)"),
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

        private void ShowCompatibilityListFetchingState()
        {
            this.FindControl<StackPanel>("PnlCompatFound")!.IsVisible = false;
            this.FindControl<StackPanel>("PnlCompatNotFound")!.IsVisible = false;
            this.FindControl<Border>("PnlCompatFetching")!.IsVisible = true;

            if (_isWaitingForCompatibilityRefresh) return;

            _isWaitingForCompatibilityRefresh = true;
            CompatibilityListService.RefreshCompleted += CompatibilityListService_RefreshCompleted;

            // The refresh can finish between the state check above and event subscription.
            // Re-run the lookup in that case instead of leaving the loading card visible.
            if (!CompatibilityListService.IsRefreshInProgress)
                CompatibilityListService_RefreshCompleted(null, EventArgs.Empty);
        }

        private void CompatibilityListService_RefreshCompleted(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_isClosed) return;
                StopWaitingForCompatibilityRefresh();
                PopulateCompatibilitySidebar();
            });
        }

        private void StopWaitingForCompatibilityRefresh()
        {
            if (!_isWaitingForCompatibilityRefresh) return;
            CompatibilityListService.RefreshCompleted -= CompatibilityListService_RefreshCompleted;
            _isWaitingForCompatibilityRefresh = false;
        }

        /// <summary>
        /// Lazily fetches the game's individual wiki page (only while this window is open, for
        /// this one game - never for the whole library) and (re)fills the fields from RenderWikiDetails.
        /// When <paramref name="hadCachedDetails"/> is true, the caller already rendered a cached
        /// result before calling this — GetGameWikiDetailsAsync's own 24h cooldown means this call
        /// silently returns that same cached value most of the time (no network, no visible change),
        /// and only re-renders with something new on the rare call where the cooldown had expired.
        /// The "Fetching…" spinner is only touched when there was nothing cached to show up front.
        /// </summary>
        private async Task PopulateWikiDetailsAsync(CompatibilityListService compatService, CompatibilityListEntry entry, bool hadCachedDetails)
        {
            if (string.IsNullOrEmpty(entry.WikiPageSlug)) return;

            try
            {
                var details = await compatService.GetGameWikiDetailsAsync(entry);

                // The user may have closed the window or navigated elsewhere while this awaited, or
                // (in theory) the compat entry could no longer be the one this fetch was started for.
                if (details == null || _compatEntry != entry) return;

                RenderWikiDetails(details);
            }
            finally
            {
                if (!hadCachedDetails)
                {
                    // Runs whether the fetch succeeded, found nothing, or failed - the "Fetching..."
                    // card and its animation must never get stuck on screen.
                    StopWikiFetchingAnimation();
                    var pnlWikiFetching = this.FindControl<Border>("PnlWikiFetching");
                    if (pnlWikiFetching != null) pnlWikiFetching.IsVisible = false;
                }
            }
        }

        /// <summary>
        /// Fills the wiki-details fields (injection method, FG Inputs, Known Issues count) from an
        /// already-resolved GameWikiDetails — either a cached value shown immediately, or a fresh
        /// one from PopulateWikiDetailsAsync's background check. Last Tested Version and Upscaler
        /// Inputs are parsed too (see CompatibilityListService.ParseGameWikiPage) but deliberately
        /// not shown here - the latter would just duplicate the Compatibility List's own Upscaler
        /// Inputs section above.
        /// </summary>
        private void RenderWikiDetails(GameWikiDetails details)
        {
            if (!_injectionMethodAutoSelected)
            {
                _injectionMethodAutoSelected = true;
                ApplySuggestedInjectionMethod(details.Filename);
            }

            _wikiPageUrl = string.IsNullOrEmpty(details.PageUrl) ? null : details.PageUrl;
            var btnGameWikiLink = this.FindControl<Button>("BtnGameWikiLink");
            if (btnGameWikiLink != null) btnGameWikiLink.IsVisible = _wikiPageUrl != null;

            var pnlWikiDetails = this.FindControl<StackPanel>("PnlWikiDetailsSection");
            if (pnlWikiDetails == null) return;

            SetWikiBadgeRow("RowWikiFilename", "PnlWikiFilenameBadges", details.Filename);
            SetWikiBadgeRow("RowWikiFgInputs", "PnlWikiFgInputsBadges", details.FgInputs);

            var txtKnownIssues = this.FindControl<TextBlock>("TxtWikiKnownIssues");
            if (txtKnownIssues != null)
            {
                txtKnownIssues.IsVisible = details.KnownIssuesCount > 0;
                if (details.KnownIssuesCount > 0)
                {
                    var format = GetResourceString("TxtCompatSidebarWikiKnownIssues", "⚠ {0} known issue(s) reported — see the wiki page for details.");
                    txtKnownIssues.Text = string.Format(format, details.KnownIssuesCount);
                }
            }

            bool hasAnyField = !string.IsNullOrWhiteSpace(details.Filename) || !string.IsNullOrWhiteSpace(details.FgInputs);
            pnlWikiDetails.IsVisible = hasAnyField || details.KnownIssuesCount > 0;
        }

        /// <summary>
        /// Pre-selects CmbInjectionMethod from the wiki page's "Filename" field: one name listed →
        /// use it; several → prefer dxgi.dll if it's among them, otherwise the first one listed;
        /// none listed, or the resolved name isn't one of CmbInjectionMethod's known options →
        /// fall back to dxgi.dll. Only ever called once per window (see _injectionMethodAutoSelected)
        /// so it never fights a selection the user has since made by hand.
        /// </summary>
        private void ApplySuggestedInjectionMethod(string wikiFilenameField)
        {
            var cmbInjectionMethod = this.FindControl<ComboBox>("CmbInjectionMethod");
            if (cmbInjectionMethod == null) return;

            var candidates = (wikiFilenameField ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s.Length > 0)
                .ToList();

            string? target = candidates.Count switch
            {
                0 => null,
                1 => candidates[0],
                _ => candidates.FirstOrDefault(c => string.Equals(c, "dxgi.dll", StringComparison.OrdinalIgnoreCase))
                     ?? candidates[0]
            };

            var resolved = target != null && KnownInjectionDllNames.Contains(target, StringComparer.OrdinalIgnoreCase)
                ? target
                : "dxgi.dll";

            for (int i = 0; i < cmbInjectionMethod.Items.Count; i++)
            {
                if (cmbInjectionMethod.Items[i] is ComboBoxItem item &&
                    string.Equals(item.Tag?.ToString(), resolved, StringComparison.OrdinalIgnoreCase))
                {
                    cmbInjectionMethod.SelectedIndex = i;
                    return;
                }
            }
        }

        private void StartWikiFetchingAnimation()
        {
            var dot1 = this.FindControl<Ellipse>("WikiFetchDot1")?.RenderTransform as TranslateTransform;
            var dot2 = this.FindControl<Ellipse>("WikiFetchDot2")?.RenderTransform as TranslateTransform;
            var dot3 = this.FindControl<Ellipse>("WikiFetchDot3")?.RenderTransform as TranslateTransform;
            if (dot1 == null || dot2 == null || dot3 == null) return;

            StopWikiFetchingAnimation();
            _wikiFetchDotsPhase = 0;
            _wikiFetchDotsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _wikiFetchDotsTimer.Tick += (s, e) =>
            {
                _wikiFetchDotsPhase += 0.25;
                const double amplitude = 5;
                const double phaseOffset = Math.PI * 2 / 3;
                dot1.Y = -amplitude * Math.Max(0, Math.Sin(_wikiFetchDotsPhase));
                dot2.Y = -amplitude * Math.Max(0, Math.Sin(_wikiFetchDotsPhase + phaseOffset));
                dot3.Y = -amplitude * Math.Max(0, Math.Sin(_wikiFetchDotsPhase + phaseOffset * 2));
            };
            _wikiFetchDotsTimer.Start();
        }

        private void StopWikiFetchingAnimation()
        {
            if (_wikiFetchDotsTimer == null) return;
            _wikiFetchDotsTimer.Stop();
            _wikiFetchDotsTimer = null;
        }

        private void SetWikiBadgeRow(string rowName, string badgesPanelName, string commaSeparatedValues)
        {
            var row = this.FindControl<StackPanel>(rowName);
            var badgesPanel = this.FindControl<WrapPanel>(badgesPanelName);
            if (row == null || badgesPanel == null) return;

            badgesPanel.Children.Clear();
            var values = commaSeparatedValues
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s.Length > 0)
                .ToList();

            row.IsVisible = values.Count > 0;
            foreach (var value in values)
                badgesPanel.Children.Add(BuildUpscalerInputBadge(value));
        }

        private void BtnGameWikiLink_Click(object sender, RoutedEventArgs e)
        {
            if (_wikiPageUrl == null) return;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = _wikiPageUrl, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[ManageGameWindow] Could not open game wiki page: {ex.Message}");
            }
        }

        private Border BuildUpscalerInputBadge(string text)
        {
            return new Border
            {
                Background = this.FindResource("BrBgSurface") as IBrush,
                BorderBrush = this.FindResource("BrBorderSubtle") as IBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = (CornerRadius)(this.FindResource("RadiusSmall") ?? new CornerRadius(4)),
                Padding = new Thickness(8, 3),
                Margin = new Thickness(0, 0, 6, 6),
                // MaxWidth + wrapping matters for the wiki-sourced badges (FG Inputs especially -
                // e.g. "DLSSG via Streamline (Use OptiPatcher to unlock DLSS and DLSS-FG inputs
                // without spoofing.)") which can be much longer free text than the main
                // Compatibility List's short tags ("DLSS", "FSR3.1") this was originally built for.
                // Without it, a long value just stretches the badge past the sidebar's edge.
                MaxWidth = 240,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = (double)(this.FindResource("FontSizeCaption") ?? 11.0),
                    FontWeight = FontWeight.SemiBold,
                    Foreground = this.FindResource("BrTextPrimary") as IBrush,
                    TextWrapping = TextWrapping.Wrap
                }
            };
        }

        private void BtnCompatSidebarLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    // Prefer this game's own wiki page (set once PopulateWikiDetailsAsync
                    // resolves one) over the generic Compatibility List page.
                    FileName = _wikiPageUrl ?? CompatibilityListService.WikiUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[ManageGameWindow] Could not open Compatibility List link: {ex.Message}");
            }
        }

        // ── Right-stick scroll for the compatibility sidebar ────────────────────
        // Mirrors the held-state + accelerating DispatcherTimer pattern already used in
        // Helpers/GamepadDialogNavigationHelper.cs, scoped to ScrollCompatSidebar only.

        private void HandleCompatSidebarRightStickInput(GamepadEventArgs e)
        {
            if (e.Button == GamepadButton.ThumbRightUp)
                _isRightStickUpHeld = e.IsPressed;
            else
                _isRightStickDownHeld = e.IsPressed;

            if (e.IsPressed)
                SetControllerModeActive(true);

            UpdateCompatSidebarScrollTimerState();
        }

        private void UpdateCompatSidebarScrollTimerState()
        {
            var scrollViewer = this.FindControl<ScrollViewer>("ScrollCompatSidebar");
            bool hasDirection = _isRightStickUpHeld ^ _isRightStickDownHeld;
            bool shouldScroll = hasDirection && scrollViewer != null && this.IsVisible;

            if (shouldScroll)
            {
                if (!_compatSidebarScrollTimer.IsEnabled)
                {
                    _compatSidebarScrollVelocity = 0;
                    _compatSidebarScrollTimer.Start();
                    ScrollCompatSidebarViewport(_isRightStickUpHeld ? -10.0 : 10.0);
                }
                return;
            }

            if (_compatSidebarScrollTimer.IsEnabled)
                _compatSidebarScrollTimer.Stop();

            _compatSidebarScrollVelocity = 0;
        }

        private void CompatSidebarScrollTimer_Tick(object? sender, EventArgs e)
        {
            if (_isRightStickUpHeld == _isRightStickDownHeld || !this.IsVisible)
            {
                UpdateCompatSidebarScrollTimerState();
                return;
            }

            _compatSidebarScrollVelocity = Math.Min(28.0, _compatSidebarScrollVelocity + 1.5);
            double delta = 6.0 + _compatSidebarScrollVelocity;

            if (_isRightStickUpHeld)
                delta = -delta;

            ScrollCompatSidebarViewport(delta);
        }

        private void ScrollCompatSidebarViewport(double deltaY)
        {
            var scrollViewer = this.FindControl<ScrollViewer>("ScrollCompatSidebar");
            if (scrollViewer == null || !scrollViewer.IsVisible) return;

            double currentY = scrollViewer.Offset.Y;
            double maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
            double targetY = Math.Clamp(currentY + deltaY, 0, maxY);

            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, targetY);
        }

        private void TrySetCoverImage(Image? image, string? coverPath)
        {
            if (image == null || string.IsNullOrWhiteSpace(coverPath)) return;

            try
            {
                if (File.Exists(coverPath))
                {
                    image.Source = new Bitmap(coverPath);
                }
            }
            catch
            {
                // Ignore invalid images to avoid breaking the dialog
            }
        }

        private void BtnEditImage_Click(object sender, RoutedEventArgs e)
        {
            ShowCoverModal();
        }

        private void ShowCoverModal()
        {
            var bdCoverModal = this.FindControl<Grid>("BdCoverModal");
            var imgPreview = this.FindControl<Image>("ImgCoverPreview");
            var txtCoverPath = this.FindControl<TextBlock>("TxtCoverPath");

            _pendingCoverPath = null;
            if (imgPreview != null) imgPreview.Source = null;
            var noImage = GetResourceString("TxtNoImageSelected", "No image selected");
            if (txtCoverPath != null) txtCoverPath.Text = noImage;

            if (bdCoverModal != null) bdCoverModal.IsVisible = true;
        }

        private void HideCoverModal()
        {
            var bdCoverModal = this.FindControl<Grid>("BdCoverModal");
            if (bdCoverModal != null) bdCoverModal.IsVisible = false;
        }

        private async void BtnCoverSelect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var files = await this.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions()
                {
                    Title = "Select Game Cover Image",
                    AllowMultiple = false,
                    FileTypeFilter = new List<FilePickerFileType>
                    {
                        new FilePickerFileType("Image Files")
                        {
                            Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" }
                        }
                    }
                });

                if (files == null || files.Count == 0) return;

                var path = files[0].Path.LocalPath;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

                _pendingCoverPath = path;

                var imgPreview = this.FindControl<Image>("ImgCoverPreview");
                if (imgPreview != null) imgPreview.Source = new Bitmap(path);

                var txtCoverPath = this.FindControl<TextBlock>("TxtCoverPath");
                if (txtCoverPath != null) txtCoverPath.Text = path;
            }
            catch (Exception ex)
            {
                _ = new ConfirmDialog(this, "Error", $"Could not load image:\n{ex.Message}").ShowDialog<object>(this);
            }
        }

        private void BtnCoverApply_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_pendingCoverPath) || !File.Exists(_pendingCoverPath))
            {
                HideCoverModal();
                return;
            }

            _game.CoverImageUrl = _pendingCoverPath;
            var imgGameCover = this.FindControl<Image>("ImgGameCover");
            if (imgGameCover != null) imgGameCover.Source = new Bitmap(_pendingCoverPath);

            HideCoverModal();
        }

        private void BtnCoverCancel_Click(object sender, RoutedEventArgs e)
        {
            HideCoverModal();
        }

        private async void BtnCoverReset_Click(object sender, RoutedEventArgs e)
        {
            _pendingCoverPath = null;
            _game.CoverImageUrl = null;

            string appIdKey = !string.IsNullOrWhiteSpace(_game.AppId) ? _game.AppId : _game.Name;
            try
            {
                var metadataService = new GameMetadataService();
                var defaultCover = await metadataService.FetchAndCacheCoverImageAsync(_game.Name, appIdKey);
                _game.CoverImageUrl = defaultCover;
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[ManageGame] Cover reset fetch failed: {ex.Message}");
                _game.CoverImageUrl = null;
            }

            var imgGameCover = this.FindControl<Image>("ImgGameCover");
            if (imgGameCover != null)
            {
                imgGameCover.Source = null;
                TrySetCoverImage(imgGameCover, _game.CoverImageUrl);
            }

            var imgPreview = this.FindControl<Image>("ImgCoverPreview");
            if (imgPreview != null)
            {
                imgPreview.Source = null;
                TrySetCoverImage(imgPreview, _game.CoverImageUrl);
            }

            var txtCoverPath = this.FindControl<TextBlock>("TxtCoverPath");
            var noImage2 = GetResourceString("TxtNoImageSelected", "No image selected");
            if (txtCoverPath != null) txtCoverPath.Text = string.IsNullOrWhiteSpace(_game.CoverImageUrl) ? noImage2 : _game.CoverImageUrl;

            HideCoverModal();
        }

        private void BtnEditTitle_Click(object sender, RoutedEventArgs e)
        {
            var txtGameName = this.FindControl<TextBlock>("TxtGameName");
            var txtGameNameEdit = this.FindControl<TextBox>("TxtGameNameEdit");
            if (txtGameName == null || txtGameNameEdit == null) return;

            if (!txtGameNameEdit.IsVisible)
            {
                txtGameNameEdit.Text = _game.Name;
                txtGameNameEdit.IsVisible = true;
                txtGameName.IsVisible = false;
                txtGameNameEdit.Focus();
                txtGameNameEdit.SelectAll();
                txtGameNameEdit.KeyDown -= TxtGameNameEdit_KeyDown;
                txtGameNameEdit.KeyDown += TxtGameNameEdit_KeyDown;
                txtGameNameEdit.LostFocus -= TxtGameNameEdit_LostFocus;
                txtGameNameEdit.LostFocus += TxtGameNameEdit_LostFocus;
            }
            else
            {
                CommitTitleEdit();
            }
        }

        private void TxtGameNameEdit_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitTitleEdit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelTitleEdit();
                e.Handled = true;
            }
        }

        private void TxtGameNameEdit_LostFocus(object? sender, RoutedEventArgs e)
        {
            CommitTitleEdit();
        }

        private void CommitTitleEdit()
        {
            var txtGameName = this.FindControl<TextBlock>("TxtGameName");
            var txtGameNameEdit = this.FindControl<TextBox>("TxtGameNameEdit");
            if (txtGameName == null || txtGameNameEdit == null) return;

            var newName = txtGameNameEdit.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(newName))
            {
                _game.Name = newName;
                txtGameName.Text = newName;
            }

            txtGameNameEdit.IsVisible = false;
            txtGameName.IsVisible = true;
        }

        private void CancelTitleEdit()
        {
            var txtGameName = this.FindControl<TextBlock>("TxtGameName");
            var txtGameNameEdit = this.FindControl<TextBox>("TxtGameNameEdit");
            if (txtGameName == null || txtGameNameEdit == null) return;

            txtGameNameEdit.IsVisible = false;
            txtGameName.IsVisible = true;
        }

        private bool _isAnimatingClose = false;

        private void BtnClose_Click(object sender, RoutedEventArgs e) => _ = CloseAnimated();

        private async Task CloseAnimated()
        {
            if (_isAnimatingClose) return;
            _isAnimatingClose = true;
            DialogDimHelper.HideDimNow(this);
            var rootPanel = this.FindControl<Panel>("RootPanel");
            if (rootPanel != null) rootPanel.Opacity = 0;
            await Task.Delay(220);
            this.Close();
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string? dirToOpen = null;
                var installService = new GameInstallationService();
                var determinedDir = installService.DetermineInstallDirectory(_game);

                if (!string.IsNullOrEmpty(determinedDir) && Directory.Exists(determinedDir))
                    dirToOpen = determinedDir;
                else if (!string.IsNullOrEmpty(_game.InstallPath) && Directory.Exists(_game.InstallPath))
                    dirToOpen = _game.InstallPath;
                else if (!string.IsNullOrEmpty(_game.ExecutablePath))
                    dirToOpen = System.IO.Path.GetDirectoryName(_game.ExecutablePath);

                if (string.IsNullOrEmpty(dirToOpen) || !Directory.Exists(dirToOpen))
                {
                    _ = new ConfirmDialog(this, "Error", "The installation directory could not be found.").ShowDialog<object>(this);
                    return;
                }

                PlatformServiceFactory.CreateShellService().OpenFolder(dirToOpen);
            }
            catch (Exception ex)
            {
                _ = new ConfirmDialog(this, "Error", $"Could not open folder:\n{ex.Message}").ShowDialog<object>(this);
            }
        }

        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            try { await ExecuteInstallAsync(false); }
            catch (Exception ex) { DebugWindow.Log($"[ManageGame] Install failed: {ex.Message}"); }
        }

        private async void BtnInstallManual_Click(object sender, RoutedEventArgs e)
        {
            try { await ExecuteInstallAsync(true); }
            catch (Exception ex) { DebugWindow.Log($"[ManageGame] Manual install failed: {ex.Message}"); }
        }

        private async Task ExecuteInstallAsync(bool isManualMode)
        {
            var btnInstall = this.FindControl<Button>("BtnInstall");
            var btnInstallManual = this.FindControl<Button>("BtnInstallManual");
            var btnUninstall = this.FindControl<Button>("BtnUninstall");
            var cmbOptiVersion = this.FindControl<ComboBox>("CmbOptiVersion");
            var cmbExtrasVersion = this.FindControl<ComboBox>("CmbExtrasVersion");
            var bdProgress = this.FindControl<Border>("BdProgress");
            var prgDownload = this.FindControl<ProgressBar>("PrgDownload");
            var txtProgressState = this.FindControl<TextBlock>("TxtProgressState");
            var cmbInjectionMethod = this.FindControl<ComboBox>("CmbInjectionMethod");

            // Read selected Fakenvapi version before any async work
            var cmbFakenvapiVersion = this.FindControl<ComboBox>("CmbFakenvapiVersion");
            var selectedFakenvapiItem = cmbFakenvapiVersion?.SelectedItem as ComboBoxItem;
            var selectedFakenvapiVersion = selectedFakenvapiItem?.Tag?.ToString();
            bool installFakenvapi = !string.IsNullOrEmpty(selectedFakenvapiVersion) &&
                                    !selectedFakenvapiVersion.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                                    selectedFakenvapiVersion != "__manage__";

            // Read selected NukemFG version before any async work
            var cmbNukemFGVersion = this.FindControl<ComboBox>("CmbNukemFGVersion");
            var selectedNukemFGItem = cmbNukemFGVersion?.SelectedItem as ComboBoxItem;
            var selectedNukemFGVersion = selectedNukemFGItem?.Tag?.ToString();
            bool installNukemFG = !string.IsNullOrEmpty(selectedNukemFGVersion) &&
                                  !selectedNukemFGVersion.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                                  selectedNukemFGVersion != "__manage__";

            // Read selected Extras (FSR4 INT8) version before any async work
            var extrasComponentService = new ComponentManagementService();
            var selectedExtrasItem = cmbExtrasVersion?.SelectedItem as ComboBoxItem;
            var selectedExtrasVersion = selectedExtrasItem?.Tag?.ToString();
            bool injectExtras = !string.IsNullOrEmpty(selectedExtrasVersion) &&
                                !selectedExtrasVersion.Equals("none", StringComparison.OrdinalIgnoreCase);
            bool selectedExtrasIsInt8 = injectExtras && extrasComponentService.GetExtrasDllVariant(selectedExtrasVersion!) == Fsr4DllVariant.Int8;

            // Read selected OptiPatcher version before any async work
            var cmbOptiPatcherVersion = this.FindControl<ComboBox>("CmbOptiPatcherVersion");
            var selectedOptiPatcherItem = cmbOptiPatcherVersion?.SelectedItem as ComboBoxItem;
            var selectedOptiPatcherVersion = selectedOptiPatcherItem?.Tag?.ToString();
            bool installOptiPatcher = !string.IsNullOrEmpty(selectedOptiPatcherVersion) &&
                                      !selectedOptiPatcherVersion.Equals("none", StringComparison.OrdinalIgnoreCase);

            // ── DLL-swap mode: OptiScaler version is "None" ─────────────────────────────
            // Ignores the normal install flow entirely (profile, injection method, Fakenvapi,
            // NukemFG, OptiPatcher — none of that applies to a bare DLL swap). See plan §3/§5.D.
            var earlyOptiTag = (cmbOptiVersion?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (string.Equals(earlyOptiTag, "none", StringComparison.OrdinalIgnoreCase))
            {
                if (!injectExtras)
                {
                    // Defense #2 — buttons should already be disabled for this combination
                    // (UpdateInstallButtonsForSwapState), this is the last-resort guard.
                    await new ConfirmDialog(this,
                        GetResourceString("TxtErrNoOptiOrExtrasTitle", "Nothing to install"),
                        GetResourceString("TxtErrNoOptiOrExtrasText", "Select an OptiScaler version or an FSR4 INT8 version before installing.")
                    ).ShowDialog<object>(this);
                    return;
                }

                await ExecuteDllSwapAsync(isManualMode, selectedExtrasVersion!);
                return;
            }

            try
            {
                var componentService = new ComponentManagementService();
                var installService = new GameInstallationService();

                var selectedVersionItem = cmbOptiVersion?.SelectedItem as ComboBoxItem;
                var optiscalerVersion = selectedVersionItem?.Tag?.ToString();

                if (string.IsNullOrEmpty(optiscalerVersion))
                {
                    await new ConfirmDialog(this, "Error", "No OptiScaler version selected.").ShowDialog<object>(this);
                    return;
                }

                if (ComponentManagementService.IsOptiScalerDownloadActive(optiscalerVersion))
                {
                    var inProgressFmt = GetResourceString("TxtDownloadInProgressFormat", "A download is already in progress for v{0}.");
                    await ShowToastAsync(string.Format(inProgressFmt, optiscalerVersion));
                    return;
                }

                string? overrideGameDir = null;
                if (isManualMode)
                {
                    var files = await this.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions()
                    {
                        Title = "Select Game Executable (Main .exe)",
                        AllowMultiple = false,
                        FileTypeFilter = new[]
                        {
                            new FilePickerFileType("Executable Files (*.exe)")
                            {
                                Patterns = new[] { "*.exe" }
                            },
                            new FilePickerFileType("All files")
                            {
                                Patterns = new[] { "*.*" }
                            }
                        }
                    });

                    if (files == null || !files.Any()) return; // User cancelled
                    overrideGameDir = System.IO.Path.GetDirectoryName(files[0].Path.LocalPath); 
                }

                // ── Pre-install corrupt artifact check (fresh installs only) ───────────────
                // For updates the manifest already tracks everything; only fresh installs need
                // this check because there is no manifest to tell us the state is clean.
                if (!_game.IsOptiscalerInstalled)
                {
                    var checkService = new GameInstallationService();
                    var checkDir = overrideGameDir ?? checkService.DetermineInstallDirectory(_game);
                    if (!string.IsNullOrEmpty(checkDir) && Directory.Exists(checkDir)
                        && GameInstallationService.HasCorruptArtifacts(checkDir))
                    {
                        var choice = await ShowCorruptInstallWarningAsync();
                        if (choice == "cancel")
                            return;

                        if (choice == "clean")
                        {
                            try
                            {
                                var filesToClean = _preInstallCleanupSelectedFiles;
                                _preInstallCleanupSelectedFiles = null;
                                await Task.Run(() => checkService.ForceFolderCleanup(_game, filesToClean));
                                NeedsScan = true;
                                UpdateStatus();
                            }
                            catch (Exception cleanEx)
                            {
                                _preInstallCleanupSelectedFiles = null;
                                var errTitle = GetResourceString("TxtError", "Error");
                                await new ConfirmDialog(this, errTitle,
                                    $"Cleanup before install failed:\n{cleanEx.Message}").ShowDialog<object>(this);
                                return;
                            }
                        }
                        // "continue" → fall through to normal install
                    }
                }

                if (btnInstall != null) btnInstall.IsEnabled = false;
                if (btnInstallManual != null) btnInstallManual.IsEnabled = false;
                if (btnUninstall != null) btnUninstall.IsEnabled = false;
                if (cmbOptiVersion != null) cmbOptiVersion.IsEnabled = false;

                bool retryDone = false;
            RetryFullInstall:

                bool isDownloadingOpti = true;
                var progress = new Progress<double>(p =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!isDownloadingOpti) return;

                        if (bdProgress != null && bdProgress.IsVisible != true)
                            bdProgress.IsVisible = true;

                        if (prgDownload != null) prgDownload.Value = p;
                        var formatInstalling = GetResourceString("TxtInstallingFormat", "Downloading OptiScaler v{0}... {1}%");
                        if (txtProgressState != null) txtProgressState.Text = string.Format(formatInstalling, optiscalerVersion, (int)p);
                    });
                });

                string optiCacheDir;
                try
                {
                    optiCacheDir = await componentService.DownloadOptiScalerAsync(optiscalerVersion, progress);
                    isDownloadingOpti = false;

                    // Hide after download finishes
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (bdProgress != null) bdProgress.IsVisible = false;
                    });
                }
                catch (VersionUnavailableException vex)
                {
                    isDownloadingOpti = false;
                    Dispatcher.UIThread.Post(() => { if (bdProgress != null) bdProgress.IsVisible = false; });
                    if (vex.Message.Contains("Download already in progress", StringComparison.OrdinalIgnoreCase))
                    {
                        var inProgressFmt2 = GetResourceString("TxtDownloadInProgressFormat", "A download is already in progress for v{0}.");
                        await ShowToastAsync(string.Format(inProgressFmt2, vex.Version));
                    }
                    else
                    {
                        var title = GetResourceString("TxtError", "Error");
                        var msg = GetResourceString(
                            "TxtVersionUnavailable",
                            "Cannot install OptiScaler v{0} right now.\n\nCheck your internet connection and try again later.");
                        await new ConfirmDialog(this, title, string.Format(msg, vex.Version)).ShowDialog<object>(this);
                    }
                    return;
                }
                catch (Exception ex)
                {
                    isDownloadingOpti = false;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (bdProgress != null) bdProgress.IsVisible = false;
                    });
                    var msgFormat = GetResourceString("TxtDownloadErrorPrefix", "Failed to download OptiScaler: {0}");
                    var title = GetResourceString("TxtError", "Error");
                    await new ConfirmDialog(this, title, string.Format(msgFormat, ex.Message)).ShowDialog<Object>(this);
                    return;
                }
                finally
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (btnInstall != null) btnInstall.IsEnabled = true;
                        if (btnInstallManual != null) btnInstallManual.IsEnabled = true;
                        if (btnUninstall != null) btnUninstall.IsEnabled = true;
                        if (cmbOptiVersion != null) cmbOptiVersion.IsEnabled = true;
                    });
                }

                var installStreamline = componentService.IsNightlyOptiScalerVersion(optiscalerVersion);
                var streamlineCacheDir = string.Empty;
                if (installStreamline)
                {
                    try
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (btnInstall != null) btnInstall.IsEnabled = false;
                            if (btnInstallManual != null) btnInstallManual.IsEnabled = false;
                            if (btnUninstall != null) btnUninstall.IsEnabled = false;
                            if (cmbOptiVersion != null) cmbOptiVersion.IsEnabled = false;
                            if (bdProgress != null) bdProgress.IsVisible = true;
                            if (prgDownload != null) prgDownload.IsIndeterminate = true;
                            if (txtProgressState != null)
                            {
                                var extractFormat = GetResourceString("TxtExtractingFormat", "Extracting and installing v{0}...");
                                txtProgressState.Text = string.Format(extractFormat, "Streamline");
                            }
                        });
                        streamlineCacheDir = await componentService.DownloadLatestStreamlineAsync();
                    }
                    catch (Exception ex)
                    {
                        var title = GetResourceString("TxtError", "Error");
                        await new ConfirmDialog(this, title, ex.Message).ShowDialog<object>(this);
                        return;
                    }
                    finally
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (prgDownload != null) prgDownload.IsIndeterminate = false;
                            if (bdProgress != null) bdProgress.IsVisible = false;
                            if (btnInstall != null) btnInstall.IsEnabled = true;
                            if (btnInstallManual != null) btnInstallManual.IsEnabled = true;
                            if (btnUninstall != null) btnUninstall.IsEnabled = true;
                            if (cmbOptiVersion != null) cmbOptiVersion.IsEnabled = true;
                        });
                    }
                }

                var fakeCacheDir = installFakenvapi
                    ? componentService.GetFakenvapiCachePath(selectedFakenvapiVersion!)
                    : componentService.GetFakenvapiCachePath();
                var nukemCacheDir = installNukemFG
                    ? componentService.GetNukemFGCachePath(selectedNukemFGVersion!)
                    : componentService.GetNukemFGCachePath();

                var selectedItem = cmbInjectionMethod?.SelectedItem as ComboBoxItem;
                var injectionMethod = selectedItem?.Tag?.ToString() ?? "dxgi.dll";

                // Nightly packages do not bundle Fakenvapi. Do not overwrite an existing game
                // copy; otherwise resolve the current release and include it in this install.
                var nightlyGameDir = overrideGameDir ?? installService.DetermineInstallDirectory(_game);
                if (installStreamline && (string.IsNullOrWhiteSpace(nightlyGameDir) ||
                    !File.Exists(System.IO.Path.Combine(nightlyGameDir, "fakenvapi.dll"))))
                {
                    try
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (bdProgress != null) bdProgress.IsVisible = true;
                            if (prgDownload != null) prgDownload.IsIndeterminate = true;
                        });
                        fakeCacheDir = await componentService.DownloadLatestFakenvapiAsync();
                        installFakenvapi = true;
                    }
                    catch (Exception ex)
                    {
                        var title = GetResourceString("TxtError", "Error");
                        await new ConfirmDialog(this, title, ex.Message).ShowDialog<object>(this);
                        return;
                    }
                    finally
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (prgDownload != null) prgDownload.IsIndeterminate = false;
                            if (bdProgress != null) bdProgress.IsVisible = false;
                        });
                    }
                }

                // Download Fakenvapi if not cached yet
                if (!installStreamline && installFakenvapi && !componentService.IsFakenvapiCached(selectedFakenvapiVersion!))
                {
                    try
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (btnInstall != null) btnInstall.IsEnabled = false;
                            if (btnInstallManual != null) btnInstallManual.IsEnabled = false;
                            if (btnUninstall != null) btnUninstall.IsEnabled = false;
                            if (cmbOptiVersion != null) cmbOptiVersion.IsEnabled = false;
                            if (bdProgress != null) bdProgress.IsVisible = true;
                            if (txtProgressState != null) txtProgressState.Text = $"Downloading Fakenvapi v{selectedFakenvapiVersion}...";
                            if (prgDownload != null) prgDownload.IsIndeterminate = false;
                        });

                        var fakeProgress = new Progress<double>(p =>
                            Dispatcher.UIThread.Post(() => { if (prgDownload != null) prgDownload.Value = p; }));

                        fakeCacheDir = await componentService.DownloadFakenvapiAsync(selectedFakenvapiVersion!, fakeProgress);
                    }
                    catch (Exception ex)
                    {
                        await new ConfirmDialog(this, "Error", $"Failed to download Fakenvapi: {ex.Message}").ShowDialog<object>(this);
                        return;
                    }
                    finally
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (prgDownload != null) prgDownload.IsIndeterminate = false;
                            if (bdProgress != null) bdProgress.IsVisible = false;
                            if (btnInstall != null) btnInstall.IsEnabled = true;
                            if (btnInstallManual != null) btnInstallManual.IsEnabled = true;
                            if (btnUninstall != null) btnUninstall.IsEnabled = true;
                            if (cmbOptiVersion != null) cmbOptiVersion.IsEnabled = true;
                        });
                    }
                }

                if (installNukemFG && (!Directory.Exists(nukemCacheDir) || !File.Exists(System.IO.Path.Combine(nukemCacheDir, "dlssg_to_fsr3_amd_is_better.dll"))))
                {
                    await new ConfirmDialog(this, "Error", $"NukemFG version '{selectedNukemFGVersion}' is not available in cache.\nPlease import it first via Manage versions.").ShowDialog<object>(this);
                    return;
                }

                // Show extraction status
                Dispatcher.UIThread.Post(() =>
                {
                    if (bdProgress != null) bdProgress.IsVisible = true;
                    if (txtProgressState != null)
                    {
                        var extractFormat = GetResourceString("TxtExtractingFormat", "Extracting and installing v{0}...");
                        txtProgressState.Text = string.Format(extractFormat, optiscalerVersion);
                    }
                    if (prgDownload != null) prgDownload.IsIndeterminate = true;
                });

                // Get selected profile
                OptiScalerProfile? selectedProfile = null;
                var cmbProfile = this.FindControl<ComboBox>("CmbProfile");
                if (cmbProfile?.SelectedItem is ComboBoxItem profileItem && profileItem.Tag is OptiScalerProfile profile)
                {
                    selectedProfile = profile;
                }

                var preferredGpuForFsr4 = GpuSelectionHelper.GetPreferredGpu(_gpuService, componentService.Config.DefaultGpuId);
                var isRdna4 = GpuSelectionHelper.IsRdna4(preferredGpuForFsr4);
                var isRdna2 = GpuSelectionHelper.IsRdna2(preferredGpuForFsr4);

                string? resolvedGameDir = null;
                try
                {
                    await Task.Run(() => {
                        resolvedGameDir = installService.InstallOptiScaler(_game, optiCacheDir, injectionMethod,
                                                        installFakenvapi, fakeCacheDir,
                                                        installNukemFG, nukemCacheDir,
                                                        optiscalerVersion: optiscalerVersion,
                                                        overrideGameDir: overrideGameDir,
                                                        profile: selectedProfile,
                                                        isRdna4: isRdna4, isRdna2: isRdna2,
                                                        installStreamline: installStreamline,
                                                        streamlineCachePath: streamlineCacheDir,
                                                        ensureFakenvapiIfMissing: installStreamline);
                    });
                }
                catch (Exception instEx) when ((instEx.Message.Contains("corrupt or incomplete") || instEx.Message.Contains("not found in the downloaded package")) && !retryDone)
                {
                    retryDone = true;
                    DebugWindow.Log($"[Install] Detected corrupt cache. Missing files. Triggering auto-retry...");

                    if (instEx.Message.Contains("Fakenvapi", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Directory.Exists(fakeCacheDir)) try { Directory.Delete(fakeCacheDir, true); } catch (Exception delEx) { DebugWindow.Log($"[Install] Failed to delete Fakenvapi cache: {delEx.Message}"); }
                    }
                    else if (instEx.Message.Contains("NukemFG", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Directory.Exists(nukemCacheDir)) try { Directory.Delete(nukemCacheDir, true); } catch (Exception delEx) { DebugWindow.Log($"[Install] Failed to delete NukemFG cache: {delEx.Message}"); }
                    }
                    else
                    {
                        if (Directory.Exists(optiCacheDir)) try { Directory.Delete(optiCacheDir, true); } catch (Exception delEx) { DebugWindow.Log($"[Install] Failed to delete OptiScaler cache: {delEx.Message}"); }
                    }

                    Dispatcher.UIThread.Post(() => { if (prgDownload != null) { prgDownload.Value = 0; prgDownload.IsIndeterminate = true; } });
                    goto RetryFullInstall;
                }

                var installedComponents = "OptiScaler";
                if (installFakenvapi) installedComponents += " + Fakenvapi";
                if (installNukemFG) installedComponents += " + NukemFG";

                // ── FSR4 INT8 DLL injection ────────────────────────────────────────
                if (injectExtras && !string.IsNullOrEmpty(selectedExtrasVersion))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (bdProgress != null) bdProgress.IsVisible = true;
                        if (txtProgressState != null) txtProgressState.Text = $"Downloading FSR4 INT8 v{selectedExtrasVersion}...";
                        if (prgDownload != null) prgDownload.IsIndeterminate = false;
                    });

                    string extrasDllPath;
                    try
                    {
                        var extrasProgress = new Progress<double>(p =>
                            Dispatcher.UIThread.Post(() => { if (prgDownload != null) prgDownload.Value = p; }));

                        extrasDllPath = await componentService.DownloadExtrasDllAsync(selectedExtrasVersion, extrasProgress);
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.UIThread.Post(() => { if (bdProgress != null) bdProgress.IsVisible = false; });
                        await new ConfirmDialog(this, "Warning",
                            $"FSR4 INT8 DLL download failed (OptiScaler was still installed):\n{ex.Message}").ShowDialog<object>(this);
                        goto SkipExtras;
                    }

                    // Copy DLL into the actual game install directory (overwrite the placeholder)
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (txtProgressState != null) txtProgressState.Text = "Injecting FSR4 INT8 DLL...";
                        if (prgDownload != null) { prgDownload.IsIndeterminate = true; }
                    });

                    try
                    {
                        await Task.Run(() =>
                        {
                            var installSvc = new GameInstallationService();
                            var gameDir = resolvedGameDir ?? installSvc.DetermineInstallDirectory(_game) ?? _game.InstallPath;
                            var destPath = System.IO.Path.Combine(gameDir, System.IO.Path.GetFileName(extrasDllPath));
                            if (!File.Exists(extrasDllPath))
                                throw new Exception("Installation failed because the FSR4 INT8 package is corrupt or incomplete.");
                            File.Copy(extrasDllPath, destPath, overwrite: true);
                            if (selectedExtrasIsInt8)
                            {
                                var customAmdxc64Path = componentService.GetCachedCustomAmdxc64Path(selectedExtrasVersion);
                                if (customAmdxc64Path != null)
                                    installSvc.InstallCustomAmdxc64(gameDir, customAmdxc64Path);
                                // The forcing keys are specific to the INT8 fallback path.
                                installSvc.ConfigureFsr4IntFallback(gameDir, isRdna4, isRdna2);
                            }
                            _game.Fsr4ExtraVersion = selectedExtrasVersion;
                            DebugWindow.Log($"[ExtrasInject] Copied DLL to {destPath} and set version to {selectedExtrasVersion}");
                        });
                    }
                    catch (Exception ex) when ((ex is FileNotFoundException || ex.Message.Contains("corrupt or incomplete")) && !retryDone)
                    {
                        retryDone = true;
                        DebugWindow.Log($"[Install] Detected corrupt FSR4 INT8 cache. Triggering auto-retry...");
                        try { if (File.Exists(extrasDllPath)) File.Delete(extrasDllPath); } catch (Exception delEx) { DebugWindow.Log($"[Install] Failed to delete FSR4 INT8 cache: {delEx.Message}"); }
                        Dispatcher.UIThread.Post(() => { if (prgDownload != null) { prgDownload.Value = 0; prgDownload.IsIndeterminate = true; } });
                        goto RetryFullInstall;
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (prgDownload != null) prgDownload.IsIndeterminate = false;
                        if (bdProgress != null) bdProgress.IsVisible = false;
                    });

                    installedComponents += " + FSR4 INT8";
                }
                else
                {
                    _game.Fsr4ExtraVersion = null;
                }
            SkipExtras:

                // ── OptiPatcher install ───────────────────────────────────────────
                if (installOptiPatcher && !string.IsNullOrEmpty(selectedOptiPatcherVersion))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (bdProgress != null) bdProgress.IsVisible = true;
                        if (txtProgressState != null) txtProgressState.Text = GetResourceString("TxtDownloadingOptiPatcher", "Downloading OptiPatcher...");
                        if (prgDownload != null) { prgDownload.IsIndeterminate = false; prgDownload.Value = 0; }
                    });

                    try
                    {
                        var optiPatcherProgress = new Progress<double>(p =>
                            Dispatcher.UIThread.Post(() => { if (prgDownload != null) prgDownload.Value = p; }));

                        var optiPatcherAsiPath = await componentService.DownloadOptiPatcherAsync(selectedOptiPatcherVersion, optiPatcherProgress);

                        Dispatcher.UIThread.Post(() =>
                        {
                            if (txtProgressState != null) txtProgressState.Text = GetResourceString("TxtInstallingOptiPatcher", "Installing OptiPatcher...");
                            if (prgDownload != null) prgDownload.IsIndeterminate = true;
                        });

                        await Task.Run(() =>
                        {
                            var installSvc = new GameInstallationService();
                            var gameDir = overrideGameDir ?? resolvedGameDir ?? installSvc.DetermineInstallDirectory(_game) ?? _game.InstallPath;

                            // Create plugins folder and copy the .asi file
                            var pluginsDir = System.IO.Path.Combine(gameDir, "plugins");
                            Directory.CreateDirectory(pluginsDir);
                            var destAsi = System.IO.Path.Combine(pluginsDir, "OptiPatcher.asi");
                            System.IO.File.Copy(optiPatcherAsiPath, destAsi, overwrite: true);
                            DebugWindow.Log($"[OptiPatcher] Installed to {destAsi}");

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
                                if (!found)
                                    lines.Add("LoadAsiPlugins=true");
                                System.IO.File.WriteAllLines(iniPath, lines);
                                DebugWindow.Log("[OptiPatcher] Patched OptiScaler.ini: LoadAsiPlugins=true");
                            }
                            else
                            {
                                DebugWindow.Log($"[OptiPatcher] OptiScaler.ini not found at {iniPath}, skipping patch");
                            }
                        });

                        installedComponents += " + OptiPatcher";
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.UIThread.Post(() => { if (bdProgress != null) bdProgress.IsVisible = false; });
                        await new ConfirmDialog(this, "Warning",
                            $"OptiPatcher installation failed (OptiScaler was still installed):\n{ex.Message}").ShowDialog<object>(this);
                    }
                    finally
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (prgDownload != null) prgDownload.IsIndeterminate = false;
                            if (bdProgress != null) bdProgress.IsVisible = false;
                        });
                    }
                }

                NeedsScan = true;
                UpdateStatus();
                LoadComponents();

                // Explicitly hide progress
                Dispatcher.UIThread.Post(() =>
                {
                    if (bdProgress != null) bdProgress.IsVisible = false;
                });

                var successFormat = GetResourceString("TxtInstallSuccessFormat", "{0} installed successfully!");
                await ShowToastAsync(string.Format(successFormat, installedComponents));
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (bdProgress != null) bdProgress.IsVisible = false;
                });
                await new ConfirmDialog(this, "Error", $"Installation failed: {ex.Message}"). ShowDialog<object>(this);
            }
        }

        private void BtnUninstall_Click(object sender, RoutedEventArgs e)
        {
            var bdConfirmUninstall = this.FindControl<Grid>("BdConfirmUninstall");
            if (bdConfirmUninstall != null) bdConfirmUninstall.IsVisible = true;

            // This same button/modal also handles "Restore original DLL" (bare swap, no OptiScaler
            // — see UpdateStatus). Swap the copy to match what's actually about to happen instead
            // of always talking about uninstalling OptiScaler.
            bool isRestoreDllOnly = !_game.IsOptiscalerInstalled && _game.IsFsr4DllSwapped;
            var txtTitle = this.FindControl<TextBlock>("TxtConfirmUninstallTitleBlock");
            var txtMsg = this.FindControl<TextBlock>("TxtConfirmUninstallMsgBlock");
            var btnYes = this.FindControl<Button>("BtnConfirmUninstallYes");
            if (isRestoreDllOnly)
            {
                if (txtTitle != null) txtTitle.Text = GetResourceString("TxtConfirmRestoreDllTitle", "Confirm Restore");
                if (txtMsg != null) txtMsg.Text = GetResourceString("TxtConfirmRestoreDllMsg", "Are you sure you want to restore the original DLL?\nThe swapped FSR4 INT8 DLL will be replaced back with the backed-up original.");
                if (btnYes != null) btnYes.Content = GetResourceString("TxtRestoreOriginalDll", "↺ Restore original DLL");
            }
            else
            {
                if (txtTitle != null) txtTitle.Text = GetResourceString("TxtConfirmUninstallTitle", "Confirm Uninstall");
                if (txtMsg != null) txtMsg.Text = GetResourceString("TxtConfirmUninstallMsg", "Are you sure you want to uninstall OptiScaler?\nOnly backed-up original files will be restored.");
                if (btnYes != null) btnYes.Content = GetResourceString("TxtUninstall", "✕ Uninstall");
            }

            var btnInstall = this.FindControl<Button>("BtnInstall");
            var btnInstallManual = this.FindControl<Button>("BtnInstallManual");
            var btnUninstall = this.FindControl<Button>("BtnUninstall");

            if (btnInstall != null) btnInstall.IsEnabled = false;
            if (btnInstallManual != null) btnInstallManual.IsEnabled = false;
            if (btnUninstall != null) btnUninstall.IsEnabled = false;
        }

        private void BtnFolderCleanup_Click(object sender, RoutedEventArgs e)
        {
            // Reset all sensitive checkboxes to unchecked every time the dialog opens.
            var sensitiveCheckboxNames = new[]
            {
                ("ChkSensitive_amd_fidelityfx_dx12",  "amd_fidelityfx_dx12.dll"),
                ("ChkSensitive_amd_fidelityfx_fg_dx12", "amd_fidelityfx_framegeneration_dx12.dll"),
                ("ChkSensitive_amd_fidelityfx_vk",    "amd_fidelityfx_vk.dll"),
                ("ChkSensitive_dxgi",                  "dxgi.dll"),
                ("ChkSensitive_libxell",               "libxell.dll"),
                ("ChkSensitive_libxess",               "libxess.dll"),
                ("ChkSensitive_libxess_dx11",          "libxess_dx11.dll"),
                ("ChkSensitive_libxess_fg",            "libxess_fg.dll"),
            };
            foreach (var (name, _) in sensitiveCheckboxNames)
            {
                var chk = this.FindControl<CheckBox>(name);
                if (chk != null) chk.IsChecked = false;
            }
            var chkAll = this.FindControl<CheckBox>("ChkSensitiveSelectAll");
            if (chkAll != null) chkAll.IsChecked = false;

            var bdConfirm = this.FindControl<Grid>("BdConfirmFolderCleanup");
            if (bdConfirm != null) bdConfirm.IsVisible = true;

            var btnInstall = this.FindControl<Button>("BtnInstall");
            var btnInstallManual = this.FindControl<Button>("BtnInstallManual");
            var btnUninstall = this.FindControl<Button>("BtnUninstall");
            var btnCleanup = this.FindControl<Button>("BtnFolderCleanup");

            if (btnInstall != null) btnInstall.IsEnabled = false;
            if (btnInstallManual != null) btnInstallManual.IsEnabled = false;
            if (btnUninstall != null) btnUninstall.IsEnabled = false;
            if (btnCleanup != null) btnCleanup.IsEnabled = false;
        }

        private void ChkSensitiveSelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox chkAll) return;
            bool check = chkAll.IsChecked == true;
            var names = new[]
            {
                "ChkSensitive_amd_fidelityfx_dx12",
                "ChkSensitive_amd_fidelityfx_fg_dx12",
                "ChkSensitive_amd_fidelityfx_vk",
                "ChkSensitive_dxgi",
                "ChkSensitive_libxell",
                "ChkSensitive_libxess",
                "ChkSensitive_libxess_dx11",
                "ChkSensitive_libxess_fg",
            };
            foreach (var name in names)
            {
                var chk = this.FindControl<CheckBox>(name);
                if (chk != null) chk.IsChecked = check;
            }
        }

        private void BtnConfirmFolderCleanupNo_Click(object sender, RoutedEventArgs e)
        {
            var bdConfirm = this.FindControl<Grid>("BdConfirmFolderCleanup");
            if (bdConfirm != null) bdConfirm.IsVisible = false;

            var btnInstall = this.FindControl<Button>("BtnInstall");
            var btnInstallManual = this.FindControl<Button>("BtnInstallManual");
            var btnUninstall = this.FindControl<Button>("BtnUninstall");
            var btnCleanup = this.FindControl<Button>("BtnFolderCleanup");

            if (btnInstall != null) btnInstall.IsEnabled = true;
            if (btnInstallManual != null) btnInstallManual.IsEnabled = true;
            if (btnUninstall != null) btnUninstall.IsEnabled = true;
            if (btnCleanup != null) btnCleanup.IsEnabled = true;

            // If we were shown from the corrupt-install flow, cancelling here cancels the install.
            if (_cleanupIsPreInstall)
            {
                _cleanupIsPreInstall = false;
                _preInstallCleanupSelectedFiles = null;
                _corruptInstallTcs?.TrySetResult("cancel");
                _corruptInstallTcs = null;
            }
        }

        private async void BtnConfirmFolderCleanupYes_Click(object sender, RoutedEventArgs e)
        {
            var bdConfirm = this.FindControl<Grid>("BdConfirmFolderCleanup");
            if (bdConfirm != null) bdConfirm.IsVisible = false;

            var btnInstall = this.FindControl<Button>("BtnInstall");
            var btnInstallManual = this.FindControl<Button>("BtnInstallManual");
            var btnUninstall = this.FindControl<Button>("BtnUninstall");
            var btnCleanup = this.FindControl<Button>("BtnFolderCleanup");

            if (btnInstall != null) btnInstall.IsEnabled = true;
            if (btnInstallManual != null) btnInstallManual.IsEnabled = true;
            if (btnUninstall != null) btnUninstall.IsEnabled = true;
            if (btnCleanup != null) btnCleanup.IsEnabled = true;

            // Collect which sensitive files the user opted to delete.
            var sensitiveMap = new[]
            {
                ("ChkSensitive_amd_fidelityfx_dx12",    "amd_fidelityfx_dx12.dll"),
                ("ChkSensitive_amd_fidelityfx_fg_dx12", "amd_fidelityfx_framegeneration_dx12.dll"),
                ("ChkSensitive_amd_fidelityfx_vk",      "amd_fidelityfx_vk.dll"),
                ("ChkSensitive_dxgi",                    "dxgi.dll"),
                ("ChkSensitive_libxell",                 "libxell.dll"),
                ("ChkSensitive_libxess",                 "libxess.dll"),
                ("ChkSensitive_libxess_dx11",            "libxess_dx11.dll"),
                ("ChkSensitive_libxess_fg",              "libxess_fg.dll"),
            };
            var selectedSensitive = sensitiveMap
                .Where(pair => this.FindControl<CheckBox>(pair.Item1)?.IsChecked == true)
                .Select(pair => pair.Item2)
                .ToList();

            // If opened from the corrupt-install flow, store the selection and hand control
            // back to ExecuteInstallAsync — it will run the cleanup then the install.
            if (_cleanupIsPreInstall)
            {
                _cleanupIsPreInstall = false;
                _preInstallCleanupSelectedFiles = selectedSensitive;
                _corruptInstallTcs?.TrySetResult("clean");
                _corruptInstallTcs = null;
                return;
            }

            try
            {
                var installService = new GameInstallationService();
                installService.ForceFolderCleanup(_game, selectedSensitive);

                NeedsScan = true;
                UpdateStatus();
                LoadComponents();

                var successMsg = GetResourceString("TxtFolderCleanupSuccess", "Folder cleanup completed.");
                await ShowToastAsync(successMsg);
            }
            catch (Exception ex)
            {
                var failFormat = GetResourceString("TxtFolderCleanupFail", "Folder cleanup failed: {0}");
                var titleMsg = GetResourceString("TxtError", "Error");
                await new ConfirmDialog(this, titleMsg, string.Format(failFormat, ex.Message)).ShowDialog<object>(this);
            }
        }

        /// <summary>
        /// The whole point of "Opti = None + Extras = version" mode: replace a single DLL already
        /// sitting in the game folder with the selected FSR4 INT8 build, without touching OptiScaler,
        /// the profile, injection method, or any other selected component. Backs the original up
        /// through the same external store InstallOptiScaler/UninstallOptiScaler use, so a later
        /// Uninstall/"Restore original DLL" reverts it — see context/plans/fsr4_dll_swap_plan.md.
        /// </summary>
        private async Task ExecuteDllSwapAsync(bool isManualMode, string extrasVersion)
        {
            var btnInstall = this.FindControl<Button>("BtnInstall");
            var btnInstallManual = this.FindControl<Button>("BtnInstallManual");
            var bdProgress = this.FindControl<Border>("BdProgress");
            var prgDownload = this.FindControl<ProgressBar>("PrgDownload");
            var txtProgressState = this.FindControl<TextBlock>("TxtProgressState");

            try
            {
                var componentService = new ComponentManagementService();
                var installService = new GameInstallationService();

                var gameDir = installService.DetermineInstallDirectory(_game);
                if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
                {
                    await new ConfirmDialog(this, GetResourceString("TxtError", "Error"),
                        "Could not automatically detect the game directory.").ShowDialog<object>(this);
                    return;
                }

                string targetPath;
                if (isManualMode)
                {
                    var files = await this.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions()
                    {
                        Title = "Select the original DLL to replace",
                        AllowMultiple = false,
                        SuggestedStartLocation = await this.StorageProvider.TryGetFolderFromPathAsync(gameDir),
                        FileTypeFilter = new[]
                        {
                            new FilePickerFileType("DLL Files (*.dll)") { Patterns = new[] { "*.dll" } }
                        }
                    });

                    if (files == null || !files.Any()) return; // User cancelled
                    targetPath = files[0].Path.LocalPath;

                    // Backups are stored by path relative to gameDir (BackupStoreService) — a file
                    // outside that tree has no sensible relative path to restore to later.
                    var fullGameDir = System.IO.Path.GetFullPath(gameDir).TrimEnd(System.IO.Path.DirectorySeparatorChar);
                    var fullTargetDir = System.IO.Path.GetFullPath(System.IO.Path.GetDirectoryName(targetPath) ?? "");
                    if (!fullTargetDir.StartsWith(fullGameDir, StringComparison.OrdinalIgnoreCase))
                    {
                        await new ConfirmDialog(this, GetResourceString("TxtError", "Error"),
                            GetResourceString("TxtSwapDllOutsideGameFolder", "The selected file must be inside the game folder.")).ShowDialog<object>(this);
                        return;
                    }
                }
                else
                {
                    var found = Fsr4Int8DllHelper.FindSwapTargetIn(gameDir,
                        componentService.GetExtrasDllVariant(extrasVersion) == Fsr4DllVariant.Int8);
                    if (found == null)
                    {
                        await new ConfirmDialog(this,
                            GetResourceString("TxtSwapDllNotFoundTitle", "No DLL found to replace"),
                            GetResourceString("TxtSwapDllNotFoundText",
                                "Could not find amd_fidelityfx_upscaler_dx12.dll, amdxcffx64.dll or amdxc64.dll in the game folder. Try Manual-Swap DLL instead.")
                        ).ShowDialog<object>(this);
                        return;
                    }
                    targetPath = found;
                }

                if (btnInstall != null) btnInstall.IsEnabled = false;
                if (btnInstallManual != null) btnInstallManual.IsEnabled = false;
                Dispatcher.UIThread.Post(() =>
                {
                    if (bdProgress != null) bdProgress.IsVisible = true;
                    if (txtProgressState != null) txtProgressState.Text = $"Downloading FSR4 INT8 v{extrasVersion}...";
                    if (prgDownload != null) prgDownload.IsIndeterminate = false;
                });

                // The RDNA2 companion (amdxc64.dll) has its own separate source and can be absent
                // for a given version — everything else (both names of the main DLL) comes from the
                // regular Extras package, regardless of how the target was found (auto or manual).
                var targetFileName = System.IO.Path.GetFileName(targetPath);
                string sourcePath;
                if (string.Equals(targetFileName, Fsr4Int8DllHelper.CustomRdna2FileName, StringComparison.OrdinalIgnoreCase))
                {
                    if (componentService.GetExtrasDllVariant(extrasVersion) != Fsr4DllVariant.Int8)
                        throw new InvalidOperationException("amdxc64.dll can only be replaced with an INT8 package.");
                    var rdna2Path = componentService.GetCachedCustomAmdxc64Path(extrasVersion);
                    if (rdna2Path == null)
                    {
                        Dispatcher.UIThread.Post(() => { if (bdProgress != null) bdProgress.IsVisible = false; });
                        await new ConfirmDialog(this, GetResourceString("TxtError", "Error"),
                            GetResourceString("TxtSwapDllNoRdna2Companion",
                                "This FSR4 INT8 version doesn't include a replacement for amdxc64.dll. Pick a different version or target file.")
                        ).ShowDialog<object>(this);
                        return;
                    }
                    sourcePath = rdna2Path;
                }
                else
                {
                    var extrasProgress = new Progress<double>(p =>
                        Dispatcher.UIThread.Post(() => { if (prgDownload != null) prgDownload.Value = p; }));
                    sourcePath = await componentService.DownloadExtrasDllAsync(extrasVersion, extrasProgress);
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (txtProgressState != null) txtProgressState.Text = "Swapping DLL...";
                    if (prgDownload != null) prgDownload.IsIndeterminate = true;
                });

                await Task.Run(() => installService.SwapFsr4Dll(_game, targetPath, sourcePath, extrasVersion));

                NeedsScan = true;
                UpdateStatus();
                LoadComponents();

                Dispatcher.UIThread.Post(() => { if (bdProgress != null) bdProgress.IsVisible = false; });

                var successFormat = GetResourceString("TxtSwapDllSuccessFormat", "FSR4 INT8 v{0} swapped into {1}.");
                await ShowToastAsync(string.Format(successFormat, extrasVersion, targetFileName));
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => { if (bdProgress != null) bdProgress.IsVisible = false; });
                await new ConfirmDialog(this, GetResourceString("TxtError", "Error"), $"DLL swap failed: {ex.Message}").ShowDialog<object>(this);
            }
            finally
            {
                UpdateInstallButtonsForSwapState();
            }
        }

        // ── Corrupt-install-detected modal handlers ───────────────────────────────────────────

        private Task<string> ShowCorruptInstallWarningAsync()
        {
            _corruptInstallTcs = new TaskCompletionSource<string>();
            var bd = this.FindControl<Grid>("BdConfirmCorruptInstall");
            if (bd != null) bd.IsVisible = true;
            return _corruptInstallTcs.Task;
        }

        private void BtnCorruptCancel_Click(object sender, RoutedEventArgs e)
        {
            var bd = this.FindControl<Grid>("BdConfirmCorruptInstall");
            if (bd != null) bd.IsVisible = false;
            _corruptInstallTcs?.TrySetResult("cancel");
            _corruptInstallTcs = null;
        }

        private void BtnCorruptClean_Click(object sender, RoutedEventArgs e)
        {
            // Close the corrupt-install modal and open the cleanup modal so the user can
            // choose which sensitive files to include. The TCS is NOT resolved yet —
            // BtnConfirmFolderCleanupYes/No_Click will resolve it once the user decides.
            var bd = this.FindControl<Grid>("BdConfirmCorruptInstall");
            if (bd != null) bd.IsVisible = false;

            _cleanupIsPreInstall = true;

            // Open the cleanup modal (same path as BtnFolderCleanup_Click).
            var sensitiveNames = new[]
            {
                "ChkSensitive_amd_fidelityfx_dx12", "ChkSensitive_amd_fidelityfx_fg_dx12",
                "ChkSensitive_amd_fidelityfx_vk",   "ChkSensitive_dxgi",
                "ChkSensitive_libxell",              "ChkSensitive_libxess",
                "ChkSensitive_libxess_dx11",         "ChkSensitive_libxess_fg",
            };
            foreach (var name in sensitiveNames)
            {
                var chk = this.FindControl<CheckBox>(name);
                if (chk != null) chk.IsChecked = false;
            }
            var chkAll = this.FindControl<CheckBox>("ChkSensitiveSelectAll");
            if (chkAll != null) chkAll.IsChecked = false;

            var bdCleanup = this.FindControl<Grid>("BdConfirmFolderCleanup");
            if (bdCleanup != null) bdCleanup.IsVisible = true;
        }

        private void BtnCorruptContinue_Click(object sender, RoutedEventArgs e)
        {
            var bd = this.FindControl<Grid>("BdConfirmCorruptInstall");
            if (bd != null) bd.IsVisible = false;
            _corruptInstallTcs?.TrySetResult("continue");
            _corruptInstallTcs = null;
        }

        private void BtnConfirmUninstallNo_Click(object sender, RoutedEventArgs e)
        {
            var bdConfirmUninstall = this.FindControl<Grid>("BdConfirmUninstall");
            if (bdConfirmUninstall != null) bdConfirmUninstall.IsVisible = false;

            var btnInstall = this.FindControl<Button>("BtnInstall");
            var btnInstallManual = this.FindControl<Button>("BtnInstallManual");
            var btnUninstall = this.FindControl<Button>("BtnUninstall");

            if (btnInstall != null) btnInstall.IsEnabled = true;
            if (btnInstallManual != null) btnInstallManual.IsEnabled = true;
            if (btnUninstall != null) btnUninstall.IsEnabled = true;
        }

        private async void BtnConfirmUninstallYes_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var bdConfirmUninstall = this.FindControl<Grid>("BdConfirmUninstall");
                if (bdConfirmUninstall != null) bdConfirmUninstall.IsVisible = false;

                var btnInstall = this.FindControl<Button>("BtnInstall");
                var btnInstallManual = this.FindControl<Button>("BtnInstallManual");
                var btnUninstall = this.FindControl<Button>("BtnUninstall");

                if (btnInstall != null) btnInstall.IsEnabled = true;
                if (btnInstallManual != null) btnInstallManual.IsEnabled = true;
                if (btnUninstall != null) btnUninstall.IsEnabled = true;

                // Capture before UninstallOptiScaler runs — it resets both flags on _game.
                bool isRestoreDllOnly = !_game.IsOptiscalerInstalled && _game.IsFsr4DllSwapped;

                var installService = new GameInstallationService();
                var result = installService.UninstallOptiScaler(_game);

                NeedsScan = true;
                UpdateStatus();
                LoadComponents();

                if (result.RemainingSensitiveFiles.Count > 0)
                {
                    // These files could be native to the game, so uninstall deliberately left
                    // them - but leaving that unexplained just looks like a broken uninstall.
                    var remainingTitle = isRestoreDllOnly
                        ? GetResourceString("TxtRestoreDllResidueTitle", "Original DLL Restored")
                        : GetResourceString("TxtOptiUninstallResidueTitle", "OptiScaler Uninstalled");
                    var remainingFormat = isRestoreDllOnly
                        ? GetResourceString("TxtRestoreDllResidueMsg",
                            "The original DLL was restored, but {0} file(s) that could belong to the game were left behind as a precaution:\n\n{1}\n\nIf you're sure the game didn't ship these, use \"Folder Cleanup\" to remove them.")
                        : GetResourceString("TxtOptiUninstallResidueMsg",
                            "OptiScaler was uninstalled, but {0} file(s) that could belong to the game were left behind as a precaution:\n\n{1}\n\nIf you're sure the game didn't ship these, use \"Folder Cleanup\" to remove them.");
                    var fileList = string.Join("\n", result.RemainingSensitiveFiles.Select(f => $"• {f}"));
                    var remainingMsg = string.Format(remainingFormat, result.RemainingSensitiveFiles.Count, fileList);
                    await new ConfirmDialog(this, remainingTitle, remainingMsg).ShowDialog<object>(this);
                }
                else
                {
                    var successMsg = isRestoreDllOnly
                        ? GetResourceString("TxtRestoreDllSuccess", "Original DLL restored successfully.")
                        : GetResourceString("TxtOptiUninstallSuccess", "OptiScaler uninstalled successfully.");
                    await ShowToastAsync(successMsg);
                }
            }
            catch (Exception ex)
            {
                var failFormat = GetResourceString("TxtOptiUninstallFail", "Uninstall failed: {0}");
                var titleMsg = GetResourceString("TxtError", "Error");
                await new ConfirmDialog(this, titleMsg, string.Format(failFormat, ex.Message)).ShowDialog<object>(this);
            }
        }

        private async Task ShowToastAsync(string message)
        {
            var txtToastMessage = this.FindControl<TextBlock>("TxtToastMessage");
            var bdToast = this.FindControl<Border>("BdToast");

            Dispatcher.UIThread.Post(() =>
            {
                if (txtToastMessage != null) txtToastMessage.Text = message;
                if (bdToast != null) bdToast.IsVisible = true;
            });

            await Task.Delay(3500);

            Dispatcher.UIThread.Post(() =>
            {
                if (bdToast != null) bdToast.IsVisible = false;
            });
        }

        private void UpdateStatus()
        {
            var txtStatus = this.FindControl<TextBlock>("TxtStatus");
            var statusIndicator = this.FindControl<Ellipse>("StatusIndicator");
            var txtVersion = this.FindControl<TextBlock>("TxtVersion");

            var btnInstall = this.FindControl<Button>("BtnInstall");
            var btnInstallManual = this.FindControl<Button>("BtnInstallManual");
            var btnUninstall = this.FindControl<Button>("BtnUninstall");
            var btnFolderCleanup = this.FindControl<Button>("BtnFolderCleanup");
            var installBtnGroup = this.FindControl<StackPanel>("InstallBtnGroup");
            var pnlInstallOptions = this.FindControl<StackPanel>("PnlInstallOptions");

            // Folder Cleanup is always available regardless of install state.
            if (btnFolderCleanup != null) { btnFolderCleanup.IsVisible = true; btnFolderCleanup.IsEnabled = true; }

            if (_game.IsOptiscalerInstalled)
            {
                if (txtStatus != null) txtStatus.Text = GetResourceString("TxtOptiInstalled", "OptiScaler Installed");
                if (statusIndicator != null) statusIndicator.Fill = new SolidColorBrush(Color.FromRgb(118, 185, 0));

                if (txtVersion != null)
                {
                    if (!string.IsNullOrEmpty(_game.OptiscalerVersion))
                        txtVersion.Text = $"v{_game.OptiscalerVersion}";
                    else
                        txtVersion.Text = "";
                }

                if (btnInstall != null)
                {
                    btnInstall.IsVisible = true;
                    btnInstall.Content = GetResourceString("TxtUpdateOpti", "Update / Reinstall");
                }
                if (btnInstallManual != null)
                {
                    btnInstallManual.IsVisible = true;
                    btnInstallManual.Content = GetResourceString("TxtUpdateOptiManual", "Manual Update");
                }

                if (installBtnGroup != null) installBtnGroup.IsVisible = true;
                if (pnlInstallOptions != null) pnlInstallOptions.IsVisible = true;
                // Uninstall reverts OptiScaler; if a DLL swap also lives in the same manifest,
                // the same click restores that too (UninstallOptiScaler handles both — see plan §1.4).
                if (btnUninstall != null)
                {
                    btnUninstall.IsVisible = true;
                    btnUninstall.Content = GetResourceString("TxtUninstall", "Uninstall");
                }
            }
            else
            {
                if (txtStatus != null) txtStatus.Text = GetResourceString("TxtOptiNotInstalled", "Not Installed");
                if (statusIndicator != null) statusIndicator.Fill = new SolidColorBrush(Colors.Gray);
                if (txtVersion != null) txtVersion.Text = "";

                if (btnInstall != null)
                {
                    btnInstall.IsVisible = true;
                    btnInstall.Content = GetResourceString("TxtInstallOpti", "✦ Auto Install");
                }
                if (btnInstallManual != null)
                {
                    btnInstallManual.IsVisible = true;
                    btnInstallManual.Content = GetResourceString("TxtBtnManualInstall", "✦ Manual Install");
                }

                if (installBtnGroup != null) installBtnGroup.IsVisible = true;
                if (pnlInstallOptions != null) pnlInstallOptions.IsVisible = true;

                // OptiScaler isn't installed, but a bare DLL swap might still be active for this
                // game — offer to revert just that (same UninstallOptiScaler call, see plan §1.4).
                if (btnUninstall != null)
                {
                    btnUninstall.IsVisible = _game.IsFsr4DllSwapped;
                    btnUninstall.Content = GetResourceString("TxtRestoreOriginalDll", "Restore original DLL");
                }
            }

            UpdateInstallButtonsForSwapState();
        }

        private sealed record ComponentEntry(string Text, bool ViaOptiscaler, bool IsSwapped, string? Tooltip);

        private ComponentEntry MakeUpscalerEntry(string label, bool viaOptiscaler, bool isSwapped = false)
        {
            var tooltip = isSwapped
                ? GetResourceString("TxtFsr4SwappedTip", "Swapped directly, without installing OptiScaler")
                : viaOptiscaler
                    ? GetResourceString("TxtUpscalerViaOptiscalerTip", "Added by OptiScaler - not native to this game")
                    : null;
            return new ComponentEntry(label, viaOptiscaler && !isSwapped, isSwapped, tooltip);
        }

        private void LoadComponents()
        {
            var components = new ObservableCollection<ComponentEntry>();

            if (!string.IsNullOrEmpty(_game.DlssVersion))
            {
                var dlssMap = GetDlssVersionMap();
                string dlssDisplay;
                if (TryLookupVersionMap(dlssMap, _game.DlssVersion, out var dlssNormal))
                    dlssDisplay = VersionDisplayEquals(dlssNormal, _game.DlssVersion)
                        ? $"NVIDIA DLSS: {dlssNormal}"
                        : $"NVIDIA DLSS: {dlssNormal} ({_game.DlssVersion})";
                else
                    dlssDisplay = $"NVIDIA DLSS: {_game.DlssVersion}";
                components.Add(MakeUpscalerEntry(dlssDisplay, _game.DlssViaOptiscaler));
            }

            if (!string.IsNullOrEmpty(_game.FsrVersion))
            {
                var fsrMap = GetFsrVersionMap();
                string fsrDisplay;
                if (TryLookupVersionMap(fsrMap, _game.FsrVersion, out var fsrNormal))
                    fsrDisplay = VersionDisplayEquals(fsrNormal, _game.FsrVersion)
                        ? $"AMD FSR: {fsrNormal}"
                        : $"AMD FSR: {fsrNormal} ({_game.FsrVersion})";
                else
                    fsrDisplay = $"AMD FSR: {_game.FsrVersion}";
                if (_game.FsrIsSwapped)
                    fsrDisplay += " (swapped)";
                components.Add(MakeUpscalerEntry(fsrDisplay, _game.FsrViaOptiscaler, _game.FsrIsSwapped));
            }

            if (!string.IsNullOrEmpty(_game.XessVersion))
            {
                var xessMap = GetXessVersionMap();
                string xessDisplay;
                if (TryLookupVersionMap(xessMap, _game.XessVersion, out var xessNormal))
                    xessDisplay = VersionDisplayEquals(xessNormal, _game.XessVersion)
                        ? $"Intel XeSS: {xessNormal}"
                        : $"Intel XeSS: {xessNormal} ({_game.XessVersion})";
                else
                    xessDisplay = $"Intel XeSS: {_game.XessVersion}";
                components.Add(MakeUpscalerEntry(xessDisplay, _game.XessViaOptiscaler));
            }

            if (_game.IsOptiscalerInstalled)
            {
                string[] keyFiles = { "OptiScaler.ini", "dxgi.dll", "version.dll", "winmm.dll", "optiscaler.log" };
                foreach (var file in keyFiles)
                {
                    if (File.Exists(System.IO.Path.Combine(_game.InstallPath, file)))
                    {
                        components.Add(new ComponentEntry($"Found: {file}", false, false, null));
                    }
                }

                if (File.Exists(System.IO.Path.Combine(_game.InstallPath, "fakenvapi.dll")))
                    components.Add(new ComponentEntry("Fakenvapi: installed", false, false, null));

                if (File.Exists(System.IO.Path.Combine(_game.InstallPath, "dlssg_to_fsr3_amd_is_better.dll")))
                    components.Add(new ComponentEntry("NukemFG: installed", false, false, null));

                // Not shown when IsFsr4DllSwapped — that gets its own distinct entry below instead
                // of double-reporting the same physical file as both "installed" and "swapped".
                bool fsr4DllExists = Fsr4Int8DllHelper.ExistsIn(_game.InstallPath);
                if (fsr4DllExists && !string.IsNullOrEmpty(_game.Fsr4ExtraVersion) && !_game.IsFsr4DllSwapped)
                {
                    components.Add(new ComponentEntry($"FSR 4 INT8 mod: {_game.Fsr4ExtraVersion}", false, false, null));
                }
            }

            var lstComponents = this.FindControl<ListBox>("LstComponents");
            if (lstComponents != null) lstComponents.ItemsSource = components;
        }

        private static Dictionary<string, string> GetFsrVersionMap()
        {
            if (_fsrVersionMap != null) return _fsrVersionMap;
            try
            {
                var path = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "configs", "fsr_version_map.json");
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    _fsrVersionMap = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                                     ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[ManageGame] Failed to load FSR version map: {ex.Message}");
            }
            return _fsrVersionMap ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, string> GetDlssVersionMap()
        {
            if (_dlssVersionMap != null) return _dlssVersionMap;
            try
            {
                var path = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "configs", "dlss_version_map.json");
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    _dlssVersionMap = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                                      ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[ManageGame] Failed to load DLSS version map: {ex.Message}");
            }
            return _dlssVersionMap ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, string> GetXessVersionMap()
        {
            if (_xessVersionMap != null) return _xessVersionMap;
            try
            {
                var path = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "configs", "xess_version_map.json");
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    _xessVersionMap = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                                      ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[ManageGame] Failed to load XeSS version map: {ex.Message}");
            }
            return _xessVersionMap ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns true when two version strings are display-equivalent:
        /// exact string match, or one is the other with trailing ".0" components stripped
        /// (e.g. "2.4.0" == "2.4.0.0").
        /// </summary>
        private static bool VersionDisplayEquals(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            static string Strip(string v)
            {
                while (v.EndsWith(".0")) v = v[..^2];
                return v;
            }
            return string.Equals(Strip(a), Strip(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Looks up a DLL version string in a version map.
        /// 1. Exact match.
        /// 2. Same-prefix match (all but last component), highest key ≤ dllVersion.
        /// 3. Global nearest-below: highest key in the whole map that is ≤ dllVersion,
        ///    only when the mapped value is the same as the nearest-above key (i.e. the
        ///    version falls between two entries that map to the same value).
        /// 4. Global nearest-below regardless of value (last resort).
        /// </summary>
        private static bool TryLookupVersionMap(Dictionary<string, string> map, string dllVersion, out string mappedVersion)
        {
            // 1. Exact match
            if (map.TryGetValue(dllVersion, out mappedVersion!))
                return true;

            if (!Version.TryParse(dllVersion, out var gameVer))
            {
                mappedVersion = null!;
                return false;
            }

            // Pre-parse all map keys into (Version, key, value) sorted ascending
            var parsed = map.Keys
                .Select(k => Version.TryParse(k, out var v) ? (ver: v, key: k) : default)
                .Where(t => t.ver != null)
                .OrderBy(t => t.ver)
                .ToList();

            // 2. Same-prefix approximate match
            var parts = dllVersion.Split('.');
            if (parts.Length >= 2)
            {
                var prefix = string.Join(".", parts, 0, parts.Length - 1) + ".";
                var prefixCandidates = parsed
                    .Where(t => t.key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (prefixCandidates.Count > 0)
                {
                    // Highest key <= gameVer
                    var best = prefixCandidates.LastOrDefault(t => t.ver <= gameVer);
                    if (best.key == null)
                        best = prefixCandidates.First(); // all are above — take smallest

                    if (map.TryGetValue(best.key, out mappedVersion!))
                        return true;
                }
            }

            // 3 & 4. Global nearest: find the highest key <= gameVer across the whole map.
            // Only extrapolate *between* known entries, never *past* the highest one — the raw
            // internal build numbers in this map (e.g. "2.2.0.1328") and the human-friendly FSR
            // versions some newer DLLs report directly as their file version (e.g. "4.0.3.0") are
            // two unrelated numbering schemes that merely look alike. Comparing them as .NET
            // Versions treats a higher-major raw build (4.x) as "above" every cataloged entry
            // (all major 1-2), so without this guard a version bigger than anything we know about
            // would silently snap to the map's topmost entry — e.g. reporting an already-friendly
            // "4.0.3.0" as "4.1" instead of leaving it as-is.
            if (parsed.Count == 0 || gameVer > parsed[^1].ver)
            {
                mappedVersion = null!;
                return false;
            }

            var below = parsed.LastOrDefault(t => t.ver <= gameVer);
            var above = parsed.FirstOrDefault(t => t.ver > gameVer);

            if (below.key != null)
            {
                // If the entries directly below and above map to the same value, it's safe
                // to use that value (the game version sits between two entries of the same range).
                if (above.key != null &&
                    map.TryGetValue(below.key, out var belowVal) &&
                    map.TryGetValue(above.key, out var aboveVal) &&
                    belowVal == aboveVal)
                {
                    mappedVersion = belowVal;
                    return true;
                }

                // Last resort: just use the nearest-below entry
                if (map.TryGetValue(below.key, out mappedVersion!))
                    return true;
            }

            mappedVersion = null!;
            return false;
        }

        private void CmbOptiVersion_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            var cmb = sender as ComboBox;
            UpdateCheckboxStatesForVersion(cmb);

            // Only configure additional components if not a beta version
            var selectedTag = (cmb?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            bool isBeta = !string.IsNullOrEmpty(selectedTag) && _betaVersions.Contains(selectedTag);
            bool isNightly = !string.IsNullOrEmpty(selectedTag) && _nightlyVersions.Contains(selectedTag);

            if (!isBeta && !isNightly)
            {
                ConfigureAdditionalComponents();
            }

            UpdateInstallButtonsForSwapState();
        }

        /// <summary>
        /// Implements the DLL-swap feature's button-state matrix (see context/plans/fsr4_dll_swap_plan.md §3):
        /// Opti=none &amp; Extras=none → nothing selected to install, buttons disabled (defense #1 —
        /// defense #2 is the guard at the top of ExecuteInstallAsync/ExecuteDllSwapAsync in case
        /// these ever get clicked anyway). Opti=none &amp; Extras=version → swap-only mode, buttons
        /// relabeled to Auto/Manual-Swap DLL. Any real Opti version selected → normal install labels,
        /// left to UpdateStatus (Install vs. Update/Reinstall depending on IsOptiscalerInstalled).
        /// </summary>
        private void UpdateInstallButtonsForSwapState()
        {
            var btnInstall = this.FindControl<Button>("BtnInstall");
            var btnInstallManual = this.FindControl<Button>("BtnInstallManual");
            var pnlNothingToInstall = this.FindControl<Border>("PnlNothingToInstallInfo");
            if (btnInstall == null || btnInstallManual == null) return;

            var cmbOptiVersion = this.FindControl<ComboBox>("CmbOptiVersion");
            var cmbExtrasVersion = this.FindControl<ComboBox>("CmbExtrasVersion");
            var optiTag = (cmbOptiVersion?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var extrasTag = (cmbExtrasVersion?.SelectedItem as ComboBoxItem)?.Tag?.ToString();

            bool optiIsNone = string.Equals(optiTag, "none", StringComparison.OrdinalIgnoreCase);
            bool extrasIsNone = string.IsNullOrEmpty(extrasTag) || string.Equals(extrasTag, "none", StringComparison.OrdinalIgnoreCase);
            bool nothingToInstall = optiIsNone && extrasIsNone;
            if (pnlNothingToInstall != null) pnlNothingToInstall.IsVisible = nothingToInstall;

            if (!optiIsNone)
            {
                // Normal install mode. Recompute the label instead of assuming UpdateStatus already
                // set it — this method is also called live on every combo change (not just window
                // load), so switching CmbOptiVersion away from "None" must overwrite whatever
                // swap-mode label a previous pass left behind (e.g. "Auto-Swap DLL").
                btnInstall.IsEnabled = true;
                btnInstallManual.IsEnabled = true;
                if (_game.IsOptiscalerInstalled)
                {
                    btnInstall.Content = GetResourceString("TxtUpdateOpti", "↑ Auto Update / Reinstall");
                    btnInstallManual.Content = GetResourceString("TxtUpdateOptiManual", "↑ Manual Update / Reinstall");
                }
                else
                {
                    btnInstall.Content = GetResourceString("TxtInstallOpti", "✦ Auto Install");
                    btnInstallManual.Content = GetResourceString("TxtBtnManualInstall", "✦ Manual Install");
                }
                return;
            }

            if (extrasIsNone)
            {
                // Nothing selected to install at all — grey out (defense #1) and show the info panel.
                btnInstall.IsEnabled = false;
                btnInstallManual.IsEnabled = false;
                btnInstall.Content = GetResourceString("TxtInstallOpti", "✦ Auto Install");
                btnInstallManual.Content = GetResourceString("TxtBtnManualInstall", "✦ Manual Install");
            }
            else
            {
                // Opti=None + a FSR4 INT8 version selected → swap-only mode.
                btnInstall.IsEnabled = true;
                btnInstallManual.IsEnabled = true;
                btnInstall.Content = GetResourceString("TxtBtnAutoSwapDll", "✦ Auto-Swap DLL");
                btnInstallManual.Content = GetResourceString("TxtBtnManualSwapDll", "✦ Manual-Swap DLL");
            }
        }

        private void ConfigureAdditionalComponents()
        {
            var componentService = new ComponentManagementService();
            GpuInfo? gpu = null;
            if (_gpuService != null)
            {
                gpu = GpuSelectionHelper.GetPreferredGpu(_gpuService, componentService.Config.DefaultGpuId);
            }
            var cmbFakenvapi = this.FindControl<ComboBox>("CmbFakenvapiVersion");
            var cmbNukemFG = this.FindControl<ComboBox>("CmbNukemFGVersion");

            // Do not re-enable these controls when the selected OptiScaler version already
            // bundles fakenvapi and nukemfg (>= 0.9). UpdateCheckboxStatesForVersion owns
            // the disabled state for those versions.
            var cmbOptiVersion = this.FindControl<ComboBox>("CmbOptiVersion");
            var selectedOptiTag = (cmbOptiVersion?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (IsVersionGreaterOrEqual(selectedOptiTag, 0, 9))
                return;

            if (gpu != null && gpu.Vendor == GpuVendor.NVIDIA)
            {
                if (cmbFakenvapi != null)
                {
                    cmbFakenvapi.IsEnabled = false;
                    cmbFakenvapi.SelectedIndex = 0; // Reset to "None"
                    ToolTip.SetTip(cmbFakenvapi, "Fakenvapi is not required for NVIDIA GPUs");
                }
            }
            else
            {
                if (cmbFakenvapi != null)
                {
                    cmbFakenvapi.IsEnabled = true;
                    ToolTip.SetTip(cmbFakenvapi, "Required for AMD/Intel GPUs to enable DLSS FG with Nukem mod");
                }
            }

            if (cmbNukemFG != null) cmbNukemFG.IsEnabled = true;
        }

        private string GetResourceString(string key, string fallback)
        {
            return Application.Current?.TryFindResource(key, out var res) == true && res is string str ? str : fallback;
        }
    }
}
