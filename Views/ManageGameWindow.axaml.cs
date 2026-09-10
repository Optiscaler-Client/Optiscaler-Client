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
using Avalonia.Media.Transformation;
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
        private bool _optiShowingModded;
        private string? _optiVersionBeforeAutoNightlySwitch;
        private bool _optiBetaBeforeAutoNightlySwitch;
        private bool _optiCustomBeforeAutoNightlySwitch;
        private bool _optiTabInitialized;
        private bool _renodxHandlerAttached;
        private Fsr4DllVariant _extrasVariant = Fsr4DllVariant.Int8;
        private bool _extrasTabInitialized;
        private ComponentManagementService? _cachedComponentService;
        private readonly DlssNrOnAmdService _dlssNrService = new();

        // Setup NR — Matheus wrapper ("Modded" channel) background download, started as soon as a
        // version is picked in CmbOptiVersion so it's likely already on disk by the time Install runs.
        // No service-level dedup exists for this download (unlike danielblnc's own, which has one in
        // DlssNrOnAmdService), so it's tracked here per-window instead — only one Modded selection is
        // ever "current" at a time for a single ManageGameWindow, so this is enough.
        private Task<string>? _pendingModdedDownloadTask;
        private string? _pendingModdedDownloadVersion;

        // CmbOptiVersion's Tag for a Modded item is the *registered* Custom version name
        // ("custom-amd-presr-{version}", matching ComponentManagementService.DownloadAndImportAmdWrapperVersionAsync)
        // so the normal install pipeline — which reads CmbOptiVersion's Tag directly as optiscalerVersion —
        // can find it like any other Custom version. This maps that registered name back to the raw
        // GitHub release version DownloadAndImportAmdWrapperVersionAsync actually needs as input.
        private readonly Dictionary<string, string> _moddedVersionRawByRegisteredName = new(StringComparer.OrdinalIgnoreCase);
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
        private bool _isUpdatingOutputUpscaler;
        private bool _compatSidebarCollapsed;
        private const double CompatSidebarExpandedWidth = 320;
        private const double CompatSidebarCollapsedWidth = 76; // fits margins + the combined icon/arrow toggle button

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

        // TaskCompletionSource for the "which FSR4 files to swap/copy" modal — resolved with the
        // checked filenames on confirm, or null on cancel.
        private TaskCompletionSource<List<string>?>? _fsr4SwapSelectionTcs;

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

        // True once an explicit (non-"Auto") Config.DefaultInjectionMethod pin has been applied in
        // LoadVersionsAsync. Unlike _injectionMethodAutoSelected (reset on every compat-entry render),
        // this never resets for the life of the window, so a global pin always outranks the
        // per-game wiki suggestion instead of being silently overwritten by it.
        private bool _injectionMethodPinnedByConfig;

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

            cmbProfile.Items.Add(ComboActionItemHelper.Build(this, "New Profile", NewProfileTag));

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

        /// <summary>
        /// Populates CmbSpoofing (DXGI Spoofing, sitting next to Profile) with Auto/Enabled/Disabled,
        /// preselecting whatever this game's OptiScaler.ini already has set — unlike RenoDX/other
        /// combos, there's no "last choice" to remember here: the ini itself is the source of truth,
        /// so reopening Manage Game always reflects the game's actual current state instead of a
        /// separately-tracked preference. No SelectionChanged handler is needed either, since nothing
        /// needs to persist on change — ExecuteInstallAsync reads the combo directly at install time.
        /// </summary>
        private void PopulateSpoofingComboBox()
        {
            var cmb = this.FindControl<ComboBox>("CmbSpoofing");
            if (cmb == null) return;

            cmb.Items.Clear();
            cmb.Items.Add(new ComboBoxItem { Content = "Auto", Tag = "auto", Classes = { "SentinelOption" } });
            cmb.Items.Add(new ComboBoxItem { Content = "Enabled", Tag = "true" });
            cmb.Items.Add(new ComboBoxItem { Content = "Disabled", Tag = "false" });

            var currentValue = ReadCurrentDxgiSpoofingValue();
            var targetIndex = 0; // Auto, default
            if (!string.IsNullOrEmpty(currentValue))
            {
                for (int i = 0; i < cmb.Items.Count; i++)
                {
                    if (string.Equals((cmb.Items[i] as ComboBoxItem)?.Tag?.ToString(), currentValue, StringComparison.OrdinalIgnoreCase))
                    {
                        targetIndex = i;
                        break;
                    }
                }
            }
            cmb.SelectedIndex = targetIndex;
        }

        /// <summary>Reads the "Dxgi" key from OptiScaler.ini's [Spoofing] section for this game, if
        /// the game is already installed and the file exists. Null if not found/not installed —
        /// PopulateSpoofingComboBox then falls back to "Auto".</summary>
        private string? ReadCurrentDxgiSpoofingValue()
        {
            try
            {
                var installService = new GameInstallationService();
                var gameDir = installService.DetermineInstallDirectory(_game);
                if (string.IsNullOrWhiteSpace(gameDir)) return null;

                var iniPath = System.IO.Path.Combine(gameDir, "OptiScaler.ini");
                if (!System.IO.File.Exists(iniPath)) return null;

                var inSpoofingSection = false;
                foreach (var raw in System.IO.File.ReadAllLines(iniPath))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        inSpoofingSection = line.Equals("[Spoofing]", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!inSpoofingSection) continue;

                    var idx = line.IndexOf('=');
                    if (idx < 0) continue;
                    var key = line[..idx].Trim();
                    if (!key.Equals("Dxgi", StringComparison.OrdinalIgnoreCase)) continue;
                    return line[(idx + 1)..].Trim();
                }
                return null;
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[Spoofing] Failed to read current Dxgi value: {ex.Message}");
                return null;
            }
        }

        /// <summary>Whether ReShade looks installed for this game — used to skip the "ReShade not
        /// detected" warning before installing RenoDX. Checks gameDir first (ReShade.ini /
        /// reshade-shaders next to the executable, same as the official Windows installer), then
        /// falls back to the community reshade-linux.sh convention on non-Windows: that script keeps
        /// ReShade's config/shaders in a separate location entirely (~/.reshade by default,
        /// overridable via the MAIN_PATH env var it also reads), not inside the game folder — so the
        /// Windows-style check alone always misses a correctly-installed Linux/Proton setup.</summary>
        private static bool IsReshadeInstalledForGame(string? gameDir)
        {
            if (!string.IsNullOrWhiteSpace(gameDir) &&
                (File.Exists(System.IO.Path.Combine(gameDir, "ReShade.ini")) ||
                 Directory.Exists(System.IO.Path.Combine(gameDir, "reshade-shaders"))))
                return true;

            if (!OperatingSystem.IsWindows())
            {
                var altPath = Environment.GetEnvironmentVariable("MAIN_PATH");
                if (string.IsNullOrWhiteSpace(altPath))
                {
                    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (!string.IsNullOrWhiteSpace(home))
                        altPath = System.IO.Path.Combine(home, ".reshade");
                }
                if (!string.IsNullOrWhiteSpace(altPath) && Directory.Exists(altPath))
                    return true;
            }

            return false;
        }

        private void CmbProfile_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingProfiles) return;
            if (sender is not ComboBox cmbProfile) return;
            if (cmbProfile.SelectedItem is not ComboBoxItem item) return;

            if (item.Tag is OptiScalerProfile profile)
            {
                _lastSelectedProfileName = profile.Name;
                RefreshInstallActionAvailability();
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
            WindowScreenFitHelper.FitToScreen(this);
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
                WindowDragHelper.EnableDrag(this, titleBar);
            }

            SetupCompatSidebarToggle();

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

            // The settings column this cap is measured against keeps changing after Opened —
            // versions load asynchronously, panels show and hide per game — so re-evaluate it on
            // every layout pass rather than only once.
            this.LayoutUpdated += (_, _) => ApplyCompatSidebarCap();

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

        internal static ComboBoxItem BuildVersionItem(string ver, bool isBeta, bool isLatest, string? tag = null)
        {
            // Foreground must be explicit: this TextBlock is built in code with no XAML ancestor to
            // inherit from, so it falls back to the framework's default (black) — invisible against
            // the popup's dark background. Reused everywhere BuildVersionItem is called (main
            // OptiScaler/Extras/OptiPatcher selectors, Setup NR's danielblnc/wrapper selectors, Bulk
            // Install, Manage Default Versions), so the fix applies uniformly.
            var textFg = Application.Current?.TryFindResource("BrTextPrimary", out var fgRes) == true && fgRes is IBrush fgBrush
                ? fgBrush
                : Brushes.White;
            var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new TextBlock { Text = ver, Foreground = textFg, VerticalAlignment = VerticalAlignment.Center });

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

            return new ComboBoxItem { Content = stack, Tag = tag ?? ver };
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

            // Default injection DLL: an explicit pin from Manage Default Versions wins outright and
            // is never overridden by the wiki's per-game suggestion (see _injectionMethodPinnedByConfig).
            // "Auto"/unset leaves the XAML's dxgi.dll default in place for that suggestion to resolve.
            var defaultInjectionMethod = componentService.Config.DefaultInjectionMethod;
            if (!string.IsNullOrEmpty(defaultInjectionMethod) && !defaultInjectionMethod.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var cmbInjection = this.FindControl<ComboBox>("CmbInjectionMethod");
                if (cmbInjection != null)
                {
                    for (int i = 0; i < cmbInjection.Items.Count; i++)
                    {
                        if ((cmbInjection.Items[i] as ComboBoxItem)?.Tag?.ToString() == defaultInjectionMethod)
                        {
                            cmbInjection.SelectedIndex = i;
                            _injectionMethodPinnedByConfig = true;
                            break;
                        }
                    }
                }
            }

            // Immediately populate ALL selectors from disk cache (no API wait).
            // This eliminates the ~1s "popup" delay when versions are already cached.
            PopulateProfileSelector(profileService, profiles, _lastSelectedProfileName ?? _defaultProfileName);
            PopulateSpoofingComboBox();
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

            // Skipped while the "Modded" channel is active: this method (and PopulateVersionSelectors
            // as a whole) runs twice per LoadVersionsAsync, and on the second pass CmbSetupNr's tag is
            // already selected so SelectCmbSetupNrTag below is a no-op that won't re-trigger
            // PopulateModdedVersionComboAsync — populating the Stable/Beta/Nightly/Custom list here
            // unconditionally would silently stomp the Modded list it had just set.
            if (!_optiShowingModded)
            {
                UpdateOptiChannelButtons();
                PopulateOptiVersionCombo(componentService);
            }

            // ── Populate FSR 4 Swap Extras selector ────────────────────────────
            PopulateExtrasComboBox(componentService);

            // ── Populate OptiPatcher selector ─────────────────────────────────
            PopulateOptiPatcherComboBox(componentService);

            // ── Populate NukemFG selector ─────────────────────────────────────
            PopulateNukemFGComboBox(componentService);

            // ── Populate Fakenvapi selector ───────────────────────────────────
            PopulateFakenvapiComboBox(componentService);

            // ── Populate RenoDX selector (experimental, opt-in) ───────────────
            var showExperimental = componentService.Config.ShowExperimentalFeatures;
            var experimentalZone = this.FindControl<Control>("BorderExperimentalZone");
            if (experimentalZone != null) experimentalZone.IsVisible = showExperimental;
            var experimentalChip = this.FindControl<Control>("BorderExperimentalChip");
            if (experimentalChip != null) experimentalChip.IsVisible = showExperimental;
            if (showExperimental)
                PopulateRenodxComboBox(componentService);

            // "Setup NR" — Windows-only, danielblnc's mod only targets DXGI/D3D12.
            var dlssNrPanel = this.FindControl<Control>("PanelDlssNrOnAmd");
            if (dlssNrPanel != null) dlssNrPanel.IsVisible = showExperimental && OperatingSystem.IsWindows();
            var dlssNrDanielPanel = this.FindControl<Control>("PanelDlssNrDanielVersion");
            if (dlssNrDanielPanel != null) dlssNrDanielPanel.IsVisible = showExperimental && OperatingSystem.IsWindows();

            // The mod itself only targets AMD GPUs — stays visible (rather than hidden) so an already
            // pending/installed selection isn't yanked out from under the user (e.g. after swapping to
            // a different GPU), but locked so a non-AMD user can't start a doomed install. See
            // IsSetupNrGpuAllowed for why "unknown vendor" is treated as allowed, not locked.
            var setupNrGpuOk = IsSetupNrGpuAllowed();
            var cmbSetupNrGate = this.FindControl<ComboBox>("CmbSetupNr");
            if (cmbSetupNrGate != null)
            {
                cmbSetupNrGate.IsEnabled = setupNrGpuOk;
                ToolTip.SetTip(cmbSetupNrGate, setupNrGpuOk ? null : GetResourceString("TxtSetupNrRequiresAmdTooltip", "danielblnc's mod requires an AMD GPU."));
            }
            // Restoring the selection re-derives locking/tabs/Modded-or-Daniel version lists via
            // CmbSetupNr_SelectionChanged (only actually fires on a real value change, so this is a
            // no-op on the second of LoadVersionsAsync's two PopulateVersionSelectors calls). While
            // actually installed, the mode shown is the one that's installed (InstalledDlssNrOnAmdMode)
            // rather than "none" — selecting "none" is what triggers the uninstall wizard, so it must
            // never be the state a re-open silently lands on for an installed mod.
            // A never-touched game (both flags unset) pre-selects the configured Settings default
            // instead of always "none" — see ManageDefaultVersionsWindow's AMD DLSS Neural Rendering
            // section. Still just a preselection: the user has to click Install like any other mode.
            // Gated on showExperimental too: turning that switch off must stop the default from
            // having any effect, not just hide the UI that configured it.
            var targetSetupNrTag = _game.IsDlssNrOnAmdInstalled
                ? (_game.InstalledDlssNrOnAmdMode ?? "daniel-only")
                : (_game.PendingDlssNrOnAmdMode
                    ?? (setupNrGpuOk && showExperimental ? componentService.Config.DefaultDlssNrOnAmdMode : null)
                    ?? "none");
            SelectCmbSetupNrTag(targetSetupNrTag);

            // This is the point where all five "hard" combos (OptiVersion/Extras/OptiPatcher/
            // NukemFG/Fakenvapi) have real selections for the first time — LoadVersionsAsync runs
            // fire-and-forget from the constructor, so UpdateStatus's own baseline capture (which
            // runs synchronously before this) sees empty combos and captures a null baseline. This
            // capture is what actually makes "Update config only" show up after window load.
            CaptureConfigOnlyBaseline();
            RefreshInstallActionAvailability();
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
                RefreshInstallActionAvailability();
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
                RefreshInstallActionAvailability();
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
            RefreshInstallActionAvailability();
        }

        private void SelectOptiVersion(string version)
        {
            var cmb = this.FindControl<ComboBox>("CmbOptiVersion");
            if (cmb == null) return;
            for (int i = 0; i < cmb.Items.Count; i++)
            {
                if (cmb.Items[i] is ComboBoxItem item && string.Equals(item.Tag?.ToString(), version, StringComparison.OrdinalIgnoreCase))
                {
                    cmb.SelectedIndex = i;
                    break;
                }
            }
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

        private async void BtnOptiStable_Click(object? sender, RoutedEventArgs e)
        {
            if (!_optiShowingBeta && !_optiShowingNightly && !_optiShowingCustom) return;
            _optiShowingBeta = false;
            _optiShowingNightly = false;
            _optiShowingCustom = false;
            UpdateOptiChannelButtons();
            if (_cachedComponentService != null)
                PopulateOptiVersionCombo(_cachedComponentService);
            await WarnIfMfgEnablerNeedsNightlyAsync();
        }

        private async void BtnOptiBeta_Click(object? sender, RoutedEventArgs e)
        {
            if (_optiShowingBeta) return;
            _optiShowingBeta = true;
            _optiShowingNightly = false;
            _optiShowingCustom = false;
            UpdateOptiChannelButtons();
            if (_cachedComponentService != null)
                PopulateOptiVersionCombo(_cachedComponentService);
            await WarnIfMfgEnablerNeedsNightlyAsync();
        }

        /// <summary>Warns when the user switches to an OptiScaler version that doesn't ship
        /// FGNvngxReplacement while this game has MFG with DLSS Enabler configured. Known-old
        /// stable/beta versions (&lt;= 0.9.5) always warn, same as before; a newer version is only
        /// warned about if it's not already confirmed (via its own cached OptiScaler.ini) to
        /// support it — see FrameGenerationConfigurationService.UsesNightlyFrameGenerationSchema.</summary>
        private async Task WarnIfMfgEnablerNeedsNightlyAsync()
        {
            var settings = _game.FrameGenerationSettings;
            var mfgWithEnabler = settings?.Route != FrameGenerationRoute.Disabled &&
                settings?.Output == FrameGenerationOutput.DlssG &&
                settings?.NvngxReplacement is FrameGenerationNvngxReplacement.Arturs or FrameGenerationNvngxReplacement.Combo;
            if (mfgWithEnabler && !CurrentlySelectedOptiScalerVersionSupportsNvngxReplacement())
            {
                await ShowToastAsync(GetResourceString("TxtMfgWrongChannelToast",
                    "MFG may not work with the selected OptiScaler version — a Nightly version is recommended."));
            }
        }

        /// <summary>True when the OptiScaler version currently selected in CmbOptiVersion is known
        /// (by channel, by version number, or by probing its cached OptiScaler.ini) to ship
        /// FGNvngxReplacement/[DLSSG]. See FrameGenerationConfigurationService.UsesNightlyFrameGenerationSchema
        /// for how unverified future versions are resolved without needing a client update.</summary>
        private bool CurrentlySelectedOptiScalerVersionSupportsNvngxReplacement()
        {
            var cmbOptiVersion = this.FindControl<ComboBox>("CmbOptiVersion");
            var selectedVersion = (cmbOptiVersion?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (string.IsNullOrEmpty(selectedVersion) || selectedVersion.Equals("none", StringComparison.OrdinalIgnoreCase))
                return false;
            return FrameGenerationConfigurationService.UsesNightlyFrameGenerationSchema(selectedVersion);
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
            cmb.Items.Add(new ComboBoxItem { Content = "None", Tag = "none", Classes = { "SentinelOption" } });

            var customExtrasVersions = componentService.CustomExtrasVersions;
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
                if (customExtrasVersions.Contains(ver))
                {
                    stack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(4),
                        Background = new SolidColorBrush(Color.Parse("#6B7280")),
                        Padding = new Thickness(5, 1),
                        Child = new TextBlock { Text = "CUSTOM", FontSize = 10, Foreground = Brushes.White, FontWeight = Avalonia.Media.FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center }
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

            if (globalDefault == ComponentManagementService.LatestAvailableTag)
            {
                // "Latest version available" (set from Manage Default Versions) always means the
                // newest release in whichever variant tab is showing — no GPU gating, unlike the
                // "no preference configured" fallback below.
                targetIndex = versions.Count > 0 ? 1 : 0;
            }
            else if (!string.IsNullOrEmpty(globalDefault))
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
            RefreshInstallActionAvailability();
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
            cmb.Items.Add(new ComboBoxItem { Content = "None", Tag = "none", Classes = { "SentinelOption" } });

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
            // "Auto" (or unset) is the default from Manage Default Versions — only then does the
            // compatibility list get to auto-pick the latest version. An explicitly pinned default
            // is now respected instead of always being overridden.
            var configuredPatcherDefault = componentService.Config.DefaultOptiPatcherVersion;
            var patcherIsAuto = string.IsNullOrEmpty(configuredPatcherDefault) ||
                                 configuredPatcherDefault.Equals("auto", StringComparison.OrdinalIgnoreCase);
            var wantsAutoLatest = patcherIsAuto && _compatEntry != null && _compatEntry.OptiPatcherSupported && !string.IsNullOrEmpty(latestVersion);
            var targetVersion = wantsAutoLatest ? latestVersion : configuredPatcherDefault;

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
            cmb.Items.Add(new ComboBoxItem { Content = "None", Tag = "none", Classes = { "SentinelOption" } });

            var versions = componentService.GetDownloadedNukemFGVersions();
            foreach (var ver in versions)
            {
                cmb.Items.Add(new ComboBoxItem { Content = ver, Tag = ver });
            }

            // Last option: Manage versions...
            cmb.Items.Add(ComboActionItemHelper.Build(this, "Manage versions…", "__manage__"));

            // Pre-select configured default
            var savedNukemFG = componentService.Config.DefaultNukemFGVersion;
            if (savedNukemFG == ComponentManagementService.LatestAvailableTag)
                savedNukemFG = versions.FirstOrDefault();
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
            cmb.Items.Add(new ComboBoxItem { Content = "None", Tag = "none", Classes = { "SentinelOption" } });

            var versions = componentService.FakenvapiAvailableVersions;
            foreach (var ver in versions)
            {
                var isLatest = ver == componentService.LatestFakenvapiVersion;
                cmb.Items.Add(BuildVersionItem(ver, isBeta: false, isLatest: isLatest));
            }

            // Last option: Manage versions…
            cmb.Items.Add(ComboActionItemHelper.Build(this, "Manage versions…", "__manage__"));

            // Pre-select configured default
            var savedFakenvapi = componentService.Config.DefaultFakenvapiVersion;
            if (savedFakenvapi == ComponentManagementService.LatestAvailableTag)
                savedFakenvapi = componentService.LatestFakenvapiVersion;
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

        /// <summary>
        /// Resolves the cache key RenoDX uses for this game: the compatibility-list wiki's own game
        /// name if there's a fuzzy match, otherwise the local Game.Name as-is — so a manually-added
        /// addon (for a game the wiki doesn't list) still has something stable to key off of. See
        /// RenodxModsService.TryGetForGame / ComponentManagementService.GetRenodxCachePath.
        /// </summary>
        private string ResolveRenodxGameKey()
        {
            return new RenodxModsService().TryGetForGame(_game.Name, out var entry) && entry != null
                ? entry.GameName
                : _game.Name;
        }

        /// <summary>
        /// Populates CmbRenodxVersion with: "None" (default — never tries to fetch/install
        /// anything), "Auto" (resolves the addon automatically at install time, see
        /// ExecuteInstallAsync), the specific addon already cached for this game (only if one
        /// exists), and "Add addon…" (opens CacheManagementWindow's "renodx" section). Defaulting to
        /// "None" rather than "Auto" is deliberate — this is opt-in, so clicking Install should
        /// never attempt a network fetch the user didn't ask for. Experimental — only called when
        /// Config.ShowExperimentalFeatures is on (see PopulateVersionSelectors).
        /// </summary>
        private void PopulateRenodxComboBox(ComponentManagementService componentService)
        {
            var cmb = this.FindControl<ComboBox>("CmbRenodxVersion");
            if (cmb == null) return;

            RebuildRenodxItems(cmb, componentService);

            // PopulateVersionSelectors (and so this method) runs twice per window open — once
            // synchronously from cache, once again after the update check — so guard against
            // attaching a second handler on the second call. Without this, the handler fires
            // twice per real selection (harmless on its own), but worse: RebuildRenodxItems'
            // intermediate SelectedIndex assignments during that second call would already fire
            // the first handler and persist a transient value before the real one is restored
            // (fixed below too, but this guard is the other half — belt and suspenders).
            if (_renodxHandlerAttached) return;
            _renodxHandlerAttached = true;

            cmb.SelectionChanged += async (s, e) =>
            {
                if (cmb.SelectedItem is ComboBoxItem item && item.Tag?.ToString() == "__manage__")
                {
                    cmb.SelectedIndex = 0;
                    var cacheWindow = new CacheManagementWindow("renodx");
                    await cacheWindow.ShowDialog(this);
                    // Addons added/removed while the dialog was open (including a fresh auto-download,
                    // which also goes through the same cache) wouldn't otherwise show up until the
                    // whole Manage Game window was reopened. Rebuild items only here (not via
                    // PopulateRenodxComboBox) so this handler isn't re-registered on every reopen.
                    RebuildRenodxItems(cmb, componentService);
                }
                else if (cmb.SelectedItem is ComboBoxItem realItem)
                {
                    // Remembers the last real choice (None/Auto/a specific cached addon) so it's
                    // pre-selected again next time any Manage Game window opens — matches how
                    // Fakenvapi/NukemFG/OptiPatcher persist via Config.DefaultXVersion instead of
                    // always resetting to "None".
                    componentService.Config.DefaultRenodxVersion = realItem.Tag?.ToString();
                    componentService.SaveConfiguration();
                }
            };
        }

        private void RebuildRenodxItems(ComboBox cmb, ComponentManagementService componentService)
        {
            // Read BEFORE touching Items/SelectedIndex below — those assignments fire
            // SelectionChanged synchronously on whatever handler is already attached, which would
            // otherwise persist a transient "None" as the new "last selection" and clobber the
            // real saved value out from under this read (see the comment in PopulateRenodxComboBox).
            var savedRenodx = componentService.Config.DefaultRenodxVersion;

            cmb.Items.Clear();
            cmb.Items.Add(new ComboBoxItem { Content = "None", Tag = "none", Classes = { "SentinelOption" } });
            cmb.Items.Add(new ComboBoxItem { Content = "Auto", Tag = "auto", Classes = { "SentinelOption" } });

            var gameKey = ResolveRenodxGameKey();
            var cachedPath = componentService.GetCachedRenodxAddonPath(gameKey);
            if (!string.IsNullOrEmpty(cachedPath))
                cmb.Items.Add(new ComboBoxItem { Content = System.IO.Path.GetFileName(cachedPath), Tag = cachedPath });

            cmb.Items.Add(ComboActionItemHelper.Build(this, "Add addon…", "__manage__"));

            // Single assignment at the end (instead of "0, then maybe overwrite") so any already-
            // attached handler observes the final, correct value if it fires at all.
            var targetIndex = 0; // None, default
            if (!string.IsNullOrEmpty(savedRenodx) && !savedRenodx.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 1; i < cmb.Items.Count; i++)
                {
                    if ((cmb.Items[i] as ComboBoxItem)?.Tag?.ToString() == savedRenodx)
                    {
                        targetIndex = i;
                        break;
                    }
                }
            }
            cmb.SelectedIndex = targetIndex;
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
            bool isNone = string.Equals(selectedTag, "none", StringComparison.OrdinalIgnoreCase);

            // Stable/Beta 0.9+ bundle both components. Nightly resolves Fakenvapi automatically
            // per game when fakenvapi.dll is absent, so its manual selector remains disabled.
            // "None" means no OptiScaler install at all, so neither component applies either.
            // Modded ("Mod + OptiScaler") always bundles its own dependencies too — its version tag
            // ("custom-amd-presr-...") never parses as >= 0.9, so without this it fell through to
            // the manual-selector case, the same as an actual pre-0.9/Custom build.
            bool includedInPackage = _optiShowingModded || (!isNightly && IsVersionGreaterOrEqual(selectedTag, 0, 9));
            bool disableFakenvapi = isNightly || includedInPackage || isNone;
            bool disableNukemFG = isNightly || includedInPackage || isNone;

            UpdateLockedOptionsForNoneSelection(isNone);

            var cmbFakenvapi = this.FindControl<ComboBox>("CmbFakenvapiVersion");
            var cmbNukemFG = this.FindControl<ComboBox>("CmbNukemFGVersion");
            var fakenvapiPanel = this.FindControl<StackPanel>("PanelFakenvapiVersion");
            var nukemFGPanel = this.FindControl<StackPanel>("PanelNukemFGVersion");
            var betaInfoPanel = this.FindControl<Border>("BetaInfoPanel");
            var moddedWarningPanel = this.FindControl<Border>("PanelModdedWarning");

            // Since OptiScaler 0.9 these components are included in the package; Nightly
            // obtains Fakenvapi automatically when it is required. Hide both manual selectors
            // instead of leaving disabled controls that take up space.
            if (fakenvapiPanel != null) fakenvapiPanel.IsVisible = !disableFakenvapi;
            if (nukemFGPanel != null) nukemFGPanel.IsVisible = !disableNukemFG;
            UpdateOptionsLayout(disableFakenvapi && disableNukemFG);

            // The "already includes X/Y" info only applies to real OptiScaler releases — while the
            // "Modded" channel is selected, show the unofficial-build risk warning instead. Neither
            // applies while danielblnc's mod-only mode is pending/installed: OptiScaler itself is
            // locked out entirely then, so whatever CmbOptiVersion happens to still have selected
            // (e.g. a leftover Stable/Beta pick, or just PopulateOptiVersionCombo running again on a
            // window reopen — see CmbSetupNr_SelectionChanged's own explicit hide, which this call can
            // otherwise re-undo since it isn't gated on the same real-value-change guard) is moot.
            bool danielModOnlyInstalledForPanels = _game.IsDlssNrOnAmdInstalled && _game.InstalledDlssNrOnAmdMode == "daniel-only";
            bool danielOnlyPendingForPanels = !danielModOnlyInstalledForPanels && _game.PendingDlssNrOnAmdMode == "daniel-only";
            bool danielOnlyActive = danielModOnlyInstalledForPanels || danielOnlyPendingForPanels;
            if (betaInfoPanel != null) betaInfoPanel.IsVisible = !danielOnlyActive && !_optiShowingModded && (isBeta || includedInPackage);
            if (moddedWarningPanel != null) moddedWarningPanel.IsVisible = !danielOnlyActive && _optiShowingModded;

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

        /// <summary>
        /// OptiScaler = "None" means there's nothing installed to configure, so lock every option
        /// that only makes sense alongside an actual OptiScaler install (a bare FSR4 DLL swap
        /// doesn't touch any of these).
        /// </summary>
        private void UpdateLockedOptionsForNoneSelection(bool isNone)
        {
            var cmbInjection = this.FindControl<ComboBox>("CmbInjectionMethod");
            var cmbOptiPatcher = this.FindControl<ComboBox>("CmbOptiPatcherVersion");
            var cmbProfile = this.FindControl<ComboBox>("CmbProfile");
            var btnFrameGeneration = this.FindControl<Button>("BtnFrameGeneration");
            var cmbUpscalingQuality = this.FindControl<ComboBox>("CmbUpscalingQuality");
            var cmbOutputUpscaler = this.FindControl<ComboBox>("CmbOutputUpscaler");

            bool enabled = !isNone;
            if (cmbInjection != null) cmbInjection.IsEnabled = enabled;
            if (cmbOptiPatcher != null) cmbOptiPatcher.IsEnabled = enabled;
            if (cmbProfile != null) cmbProfile.IsEnabled = enabled;
            if (btnFrameGeneration != null) btnFrameGeneration.IsEnabled = enabled;
            if (cmbUpscalingQuality != null) cmbUpscalingQuality.IsEnabled = enabled;
            if (cmbOutputUpscaler != null) cmbOutputUpscaler.IsEnabled = enabled;
        }

        internal static bool IsVersionGreaterOrEqual(string? ver, int targetMajor, int targetMinor)
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

            // Tooltip carries the full name for the titles too long for the two lines the
            // TextBlock wraps to before it ellipsizes.
            if (txtGameName != null)
            {
                txtGameName.Text = _game.Name;
                ToolTip.SetTip(txtGameName, _game.Name);
            }
            if (txtInstallPath != null) txtInstallPath.Text = _game.InstallPath;
            if (txtGameNameEdit != null) txtGameNameEdit.Text = _game.Name;
            TrySetCoverImage(imgGameCover, _game.CoverImageUrl);

            PopulateInstallActionCombos();
            SetupFrameGenerationButton();
            SetupUpscalingQualitySelector();
            SetupOutputUpscalerSelector();
            UpdateStatus();
            LoadComponents();
            ConfigureAdditionalComponents();
            CheckIfAntiCheat();
            PopulateCompatibilitySidebar();
            SetupFrameGenerationButton();
            SetupUpscalingQualitySelector();
            SetupOutputUpscalerSelector();

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
            var outputUpscaler = this.FindControl<StackPanel>("PanelOutputUpscaler");
            var spoofingHost = this.FindControl<StackPanel>("PanelSpoofingHost");
            var injectionLabel = this.FindControl<TextBlock>("LblInjectionMethod");

            if (opti == null || extras == null || injection == null || patcher == null
                || profile == null || frameGeneration == null || upscalingQuality == null
                || outputUpscaler == null || spoofingHost == null)
                return;

            if (useCompactLayout)
            {
                // Opti / FSR4 / injection, then patcher / Output Upscaler / Quality, then Frame
                // Generation / Profile / Spoofing — exactly 9 panels, fits the 3 fixed option rows.
                Grid.SetRow(opti, 0); Grid.SetColumn(opti, 0);
                Grid.SetRow(extras, 0); Grid.SetColumn(extras, 1);
                Grid.SetRow(injection, 0); Grid.SetColumn(injection, 2);
                Grid.SetRow(patcher, 1); Grid.SetColumn(patcher, 0);
                Grid.SetRow(outputUpscaler, 1); Grid.SetColumn(outputUpscaler, 1);
                Grid.SetRow(upscalingQuality, 1); Grid.SetColumn(upscalingQuality, 2);
                Grid.SetRow(frameGeneration, 2); Grid.SetColumn(frameGeneration, 0);
                Grid.SetRow(profile, 2); Grid.SetColumn(profile, 1);
                Grid.SetRow(spoofingHost, 2); Grid.SetColumn(spoofingHost, 2);
            }
            else
            {
                // Fakenvapi and/or NukemFG selectors are visible (pre-0.9/Custom OptiScaler builds),
                // which is one or two extra panels than the compact case has room for in 3 rows (up
                // to 11 total incl. Spoofing). Lay them out in a fixed logical order instead of
                // hand-picking (row, col) per panel — wrap to a new row every 3 — so it can never
                // again silently run out of cells the way it did when Output Upscaler landed on top
                // of the Uninstall row and Spoofing got squeezed into Profile's column. A 4th options
                // row is reserved in the grid (see the .axaml RowDefinitions) specifically for this;
                // ceil(11/3) = 4 rows, so it always fits.
                var fakenvapiPanel = this.FindControl<StackPanel>("PanelFakenvapiVersion");
                var nukemFGPanel = this.FindControl<StackPanel>("PanelNukemFGVersion");
                var ordered = new List<StackPanel> { opti, extras };
                if (fakenvapiPanel is { IsVisible: true }) ordered.Add(fakenvapiPanel);
                ordered.Add(injection);
                ordered.Add(patcher);
                if (nukemFGPanel is { IsVisible: true }) ordered.Add(nukemFGPanel);
                ordered.Add(profile);
                ordered.Add(frameGeneration);
                ordered.Add(upscalingQuality);
                ordered.Add(outputUpscaler);
                ordered.Add(spoofingHost);

                for (var i = 0; i < ordered.Count; i++)
                {
                    Grid.SetRow(ordered[i], i / 3);
                    Grid.SetColumn(ordered[i], i % 3);
                }
            }

            if (injectionLabel != null)
                injectionLabel.Margin = useCompactLayout ? new Thickness(0, 32, 0, 0) : default;
        }

        /// <summary>The sidebar Border's own top+bottom margin (its XAML Margin="0,10,16,12"),
        /// which sits outside its MaxHeight but still counts towards the row height it shares with
        /// the other columns.</summary>
        private const double CompatSidebarOuterMargin = 22;

        /// <summary>Everything between the window's height and that shared row: RootPanel's
        /// Margin="12" top and bottom, plus the card Border's 1px edges.</summary>
        private const double RootPanelChrome = 26;

        private double _appliedCompatSidebarCap = double.NaN;

        /// <summary>
        /// Caps the Recommended Config sidebar so the window's height is decided by the settings
        /// column — which ends at the Folder Cleanup / Install action row — instead of by whichever
        /// game happens to have the most compat notes. The root layout is a single-row Grid, so the
        /// SizeToContent height is simply the tallest of the three columns; without this cap the
        /// sidebar wins that contest and stretches the whole window. Capped, it scrolls internally
        /// instead (its ScrollViewer already has VerticalScrollBarVisibility="Auto").
        ///
        /// Reads DesiredSize, not Bounds: DesiredSize is the measure-phase result, i.e. the column's
        /// own natural height, and does not depend on the sidebar. Bounds is the arrange result,
        /// stretched to the shared row height, which would make this circular.
        /// </summary>
        private void ApplyCompatSidebarCap()
        {
            var sidebar = this.FindControl<Border>("PnlCompatSidebar");
            var mainContent = this.FindControl<Grid>("GridMainContent");
            if (sidebar == null || mainContent == null) return;

            var mainNatural = mainContent.DesiredSize.Height;
            if (mainNatural <= 0) return; // no layout pass yet

            // MinHeight decides the window's height on its own for a game with little to show, and
            // the columns stretch to fill it — so the sidebar gets that height too, rather than
            // scrolling with empty window beside it.
            var available = Math.Max(mainNatural, MinHeight - RootPanelChrome);

            // MaxHeight is what WindowScreenFitHelper set from the screen this window opened on.
            if (!double.IsNaN(MaxHeight) && !double.IsInfinity(MaxHeight))
                available = Math.Min(available, MaxHeight - RootPanelChrome);

            var cap = available - CompatSidebarOuterMargin;

            // Also the loop guard: assigning MaxHeight schedules another layout pass, which calls
            // straight back in here.
            if (Math.Abs(cap - _appliedCompatSidebarCap) < 1) return;
            _appliedCompatSidebarCap = cap;
            sidebar.MaxHeight = cap;
        }

        // ── Recommended Config sidebar collapse ──────────────────────────────────
        // The sidebar's Width drives the outer Grid's Auto-sized 3rd column directly (see XAML
        // comment), so animating it reclaims the space instead of just hiding content behind a
        // fixed-width column. Starts expanded every time the window opens — no persisted state,
        // matches the ask ("por defecto desplegada") without over-building a settings entry
        // nobody asked for.
        private void SetupCompatSidebarToggle()
        {
            var sidebar = this.FindControl<Border>("PnlCompatSidebar");
            var scrollViewer = this.FindControl<ScrollViewer>("ScrollCompatSidebar");
            var titleText = this.FindControl<TextBlock>("TxtCompatSidebarTitleText");
            var collapsedLinks = this.FindControl<StackPanel>("PnlCompatSidebarCollapsedLinks");
            if (sidebar == null) return;

            var duration = AnimationHelper.GetPanelAnimationDuration();
            sidebar.Transitions = new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.DoubleTransition
                {
                    Property = Border.WidthProperty,
                    Duration = duration,
                    Easing = new Avalonia.Animation.Easings.CubicEaseOut()
                }
            };

            // The content (wrapped notes, badge WrapPanels) reflows on every frame while the
            // sidebar's width animates, which looks like a squished, re-shuffling mess mid-expand.
            // Fading it in — kept invisible via Opacity until the width settles, then faded — hides
            // that reflow entirely instead of trying to prevent it.
            if (scrollViewer != null)
            {
                scrollViewer.Transitions = new Avalonia.Animation.Transitions
                {
                    new Avalonia.Animation.DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = duration,
                        Easing = new Avalonia.Animation.Easings.CubicEaseOut()
                    }
                };
            }

            if (titleText != null)
            {
                titleText.Transitions = new Avalonia.Animation.Transitions
                {
                    new Avalonia.Animation.DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = duration,
                        Easing = new Avalonia.Animation.Easings.CubicEaseOut()
                    }
                };
            }

            if (collapsedLinks != null)
            {
                collapsedLinks.Transitions = new Avalonia.Animation.Transitions
                {
                    new Avalonia.Animation.DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = duration,
                        Easing = new Avalonia.Animation.Easings.CubicEaseOut()
                    }
                };
            }

            // The clipboard icon "flies" between sitting beside the title (expanded) and merging
            // into the toggle button next to the arrow (collapsed) — fade + slide + scale on both
            // copies, timed together so it reads as one icon travelling rather than two swapping.
            var titleIcon = this.FindControl<TextBlock>("TxtCompatSidebarClipboardIcon");
            var btnIcon = this.FindControl<TextBlock>("TxtCompatSidebarBtnClipboardIcon");
            foreach (var icon in new[] { titleIcon, btnIcon })
            {
                if (icon == null) continue;
                icon.Transitions = new Avalonia.Animation.Transitions
                {
                    new Avalonia.Animation.DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = duration,
                        Easing = new Avalonia.Animation.Easings.CubicEaseOut()
                    },
                    new Avalonia.Animation.TransformOperationsTransition
                    {
                        Property = Visual.RenderTransformProperty,
                        Duration = duration,
                        Easing = new Avalonia.Animation.Easings.CubicEaseOut()
                    }
                };
            }
        }

        private async void BtnToggleCompatSidebar_Click(object? sender, RoutedEventArgs e)
        {
            var sidebar = this.FindControl<Border>("PnlCompatSidebar");
            var header = this.FindControl<Grid>("PnlCompatSidebarHeader");
            var titleText = this.FindControl<TextBlock>("TxtCompatSidebarTitleText");
            var scrollViewer = this.FindControl<ScrollViewer>("ScrollCompatSidebar");
            var toggleBtn = this.FindControl<Button>("BtnToggleCompatSidebar");
            var toggleIcon = this.FindControl<TextBlock>("TxtCompatSidebarToggleIcon");
            var titleIcon = this.FindControl<TextBlock>("TxtCompatSidebarClipboardIcon");
            var btnIcon = this.FindControl<TextBlock>("TxtCompatSidebarBtnClipboardIcon");
            var collapsedLinks = this.FindControl<StackPanel>("PnlCompatSidebarCollapsedLinks");
            if (sidebar == null) return;

            _compatSidebarCollapsed = !_compatSidebarCollapsed;

            if (toggleIcon != null) toggleIcon.Text = _compatSidebarCollapsed ? "›" : "‹";
            if (toggleBtn != null)
                ToolTip.SetTip(toggleBtn, GetResourceString(
                    _compatSidebarCollapsed ? "TxtCompatSidebarExpand" : "TxtCompatSidebarCollapse",
                    _compatSidebarCollapsed ? "Expand sidebar" : "Collapse sidebar"));

            if (_compatSidebarCollapsed)
            {
                // Hide content immediately — no fade needed on the way out, only the reflow while
                // expanding looks bad.
                if (titleText != null) { titleText.Opacity = 0; titleText.IsVisible = false; }
                if (scrollViewer != null) { scrollViewer.Opacity = 0; scrollViewer.IsVisible = false; }
                sidebar.Width = CompatSidebarCollapsedWidth;

                // The title column ("*") still claims its layout share even with the title
                // hidden, so the Auto-sized button column sits pinned to the right edge — make
                // the button span both columns and center itself so it lands mid-rail instead.
                if (header != null) header.Margin = new Thickness(0, 14, 0, 10);
                if (toggleBtn != null)
                {
                    Grid.SetColumn(toggleBtn, 0);
                    Grid.SetColumnSpan(toggleBtn, 2);
                    toggleBtn.HorizontalAlignment = HorizontalAlignment.Center;
                }

                // Merge: the title's clipboard icon shrinks/fades toward the button while the
                // button's own copy grows/fades in to take its place, reading as one icon
                // travelling into the button rather than two icons swapping.
                if (titleIcon != null)
                {
                    titleIcon.Opacity = 0;
                    titleIcon.RenderTransform = TransformOperations.Parse("translateX(14px) scale(0.5)");
                }
                if (btnIcon != null)
                {
                    btnIcon.IsVisible = true;
                    btnIcon.Opacity = 1;
                    btnIcon.RenderTransform = TransformOperations.Parse("translateX(0px) scale(1)");
                }

                // Same reveal-after-settle treatment as the title text on expand: the collapsed
                // rail's own quick-link icons only get their fade-in once the width has actually
                // reached CompatSidebarCollapsedWidth, instead of getting wipe-revealed mid-shrink.
                if (collapsedLinks != null)
                {
                    await Task.Delay(AnimationHelper.GetPanelAnimationDuration());
                    collapsedLinks.IsVisible = true;
                    collapsedLinks.Opacity = 1;
                }
            }
            else
            {
                if (collapsedLinks != null) { collapsedLinks.Opacity = 0; collapsedLinks.IsVisible = false; }
                sidebar.Width = CompatSidebarExpandedWidth;

                if (header != null) header.Margin = new Thickness(16, 14, 10, 10);
                if (toggleBtn != null)
                {
                    Grid.SetColumn(toggleBtn, 1);
                    Grid.SetColumnSpan(toggleBtn, 1);
                    toggleBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
                }

                // Reverse the merge: the button's icon shrinks/fades back out while the title's
                // copy grows/fades back in beside the title text.
                if (titleIcon != null)
                {
                    titleIcon.Opacity = 1;
                    titleIcon.RenderTransform = TransformOperations.Parse("translateX(0px) scale(1)");
                }
                if (btnIcon != null)
                {
                    btnIcon.Opacity = 0;
                    btnIcon.RenderTransform = TransformOperations.Parse("translateX(-14px) scale(0.5)");
                }

                // Keep the content out of layout (IsVisible=false, not just Opacity=0) for the
                // whole width animation — a narrow in-between width forces the wrapped text/badges
                // to reflow much taller, and since it's still SizeToContent="Height", the whole
                // window would visibly stretch and snap back as that transient reflow happens.
                // Only bringing it into layout once the width has actually settled avoids that.
                if (scrollViewer != null || btnIcon != null || titleText != null)
                {
                    await Task.Delay(AnimationHelper.GetPanelAnimationDuration());
                    if (scrollViewer != null)
                    {
                        scrollViewer.IsVisible = true;
                        scrollViewer.Opacity = 1;
                    }
                    // Only reveal the title once the rail has reached full width — showing it
                    // earlier let it get wipe-revealed by the still-animating clip, looking like
                    // it slid in from the right over the button instead of fading in cleanly.
                    if (titleText != null)
                    {
                        titleText.IsVisible = true;
                        titleText.Opacity = 1;
                    }
                    // Only pull it out of layout once it's fully faded out, so the button
                    // smoothly shrinks back down to just the arrow instead of snapping.
                    if (btnIcon != null) btnIcon.IsVisible = false;
                }
            }
        }

        private void SetupFrameGenerationButton()
        {
            // AppliedAtUtc is only ever stamped by GameInstallationService.ApplyFrameGenerationSettings,
            // i.e. once this game's .ini has actually been patched with it. Until then, this is just a
            // preview of "what would install" and must keep tracking the current app-wide default —
            // otherwise merely opening Manage once (with no default configured yet) permanently freezes
            // the game on the built-in Disabled/Auto, ignoring any default set afterwards. Also re-derive
            // whenever OptiScaler itself isn't installed any more: the .ini that stamp was written to is
            // gone (uninstalled since), so a stale AppliedAtUtc from a previous install must not keep
            // freezing the preview on whatever was configured back then.
            if (_game.FrameGenerationSettings == null || _game.FrameGenerationSettings.AppliedAtUtc == null || !_game.IsOptiscalerInstalled)
            {
                // A game that has never had FG actually applied starts from the app-wide default set in
                // Manage Default Versions (cloned, so this window stamping AppliedAtUtc on it later
                // doesn't mutate the shared default object), falling back to the built-in Disabled/Auto.
                var defaultFg = new ComponentManagementService().Config.DefaultFrameGenerationSettings;
                _game.FrameGenerationSettings = defaultFg != null
                    ? new GameFrameGenerationSettings
                    {
                        Route = defaultFg.Route,
                        Output = defaultFg.Output,
                        MultiFrameMode = defaultFg.MultiFrameMode,
                        AdvancedMode = defaultFg.AdvancedMode,
                        DynamicTargetFps = defaultFg.DynamicTargetFps,
                        NvngxReplacement = defaultFg.NvngxReplacement,
                        DlssEnablerVersion = defaultFg.DlssEnablerVersion
                    }
                    : new GameFrameGenerationSettings
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
            // Same AppliedAtUtc reasoning as SetupFrameGenerationButton (including the
            // !IsOptiscalerInstalled re-derive): keep tracking the current default until this game has
            // actually had a quality override written to its .ini.
            if (_game.UpscalingQualitySettings == null || _game.UpscalingQualitySettings.AppliedAtUtc == null || !_game.IsOptiscalerInstalled)
            {
                var config = new ComponentManagementService().Config;
                _game.UpscalingQualitySettings = new GameUpscalingQualitySettings
                {
                    Preset = config.DefaultUpscalingQualityPreset ?? UpscalingQualityPreset.GameControlled,
                    CustomRatio = config.DefaultUpscalingCustomRatio ?? 1.5
                };
            }
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
                AddUpscalingQualityItem(combo, GetResourceString("TxtQualityGameControlled", "Game controlled"), UpscalingQualityPreset.GameControlled, isSentinel: true);
                AddUpscalingQualityItem(combo, "Native AA", UpscalingQualityPreset.NativeAa);
                AddUpscalingQualityItem(combo, "Ultra Quality", UpscalingQualityPreset.UltraQuality);
                AddUpscalingQualityItem(combo, "Quality", UpscalingQualityPreset.Quality);
                AddUpscalingQualityItem(combo, "Balanced", UpscalingQualityPreset.Balanced);
                AddUpscalingQualityItem(combo, "Performance", UpscalingQualityPreset.Performance);
                AddUpscalingQualityItem(combo, "Ultra Performance", UpscalingQualityPreset.UltraPerformance);
                var fontIcons = this.FindResource("FontIcons") as FontFamily;
                combo.Items.Add(ComboActionItemHelper.Build(this, GetResourceString("TxtCustom", "Custom"),
                    UpscalingQualityPreset.Custom, glyph: "", glyphFontFamily: fontIcons));

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

        internal static void AddUpscalingQualityItem(ComboBox combo, string label, UpscalingQualityPreset preset, bool isSentinel = false)
        {
            var item = new ComboBoxItem { Content = label, Tag = preset };
            if (isSentinel) item.Classes.Add("SentinelOption");
            combo.Items.Add(item);
        }

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

            // Staged only — applied to disk when the user explicitly hits Install/Update.
            _game.UpscalingQualitySettings = new GameUpscalingQualitySettings
            {
                Preset = selected,
                CustomRatio = customRatio,
                AppliedAtUtc = previous.AppliedAtUtc
            };

            RefreshInstallActionAvailability();
        }

        private void SetupOutputUpscalerSelector()
        {
            // Same AppliedAtUtc reasoning as SetupFrameGenerationButton (including the
            // !IsOptiscalerInstalled re-derive): keep tracking the current default until this game has
            // actually had an output-upscaler override written to its .ini.
            if (_game.OutputUpscalerSettings == null || _game.OutputUpscalerSettings.AppliedAtUtc == null || !_game.IsOptiscalerInstalled)
            {
                var backend = new ComponentManagementService().Config.DefaultOutputUpscalerBackend ?? OutputUpscalerBackend.Default;
                _game.OutputUpscalerSettings = new GameOutputUpscalerSettings { Backend = backend };
            }
            PopulateOutputUpscalerSelector(_game.OutputUpscalerSettings.Backend);
        }

        private void PopulateOutputUpscalerSelector(OutputUpscalerBackend selected)
        {
            var combo = this.FindControl<ComboBox>("CmbOutputUpscaler");
            if (combo == null) return;

            _isUpdatingOutputUpscaler = true;
            try
            {
                combo.Items.Clear();
                AddOutputUpscalerItem(combo, GetResourceString("TxtOutputUpscalerDefault", "Default"), OutputUpscalerBackend.Default, isSentinel: true);
                AddOutputUpscalerItem(combo, "FSR 2", OutputUpscalerBackend.Fsr2);
                AddOutputUpscalerItem(combo, "FSR 3", OutputUpscalerBackend.Fsr3);
                AddOutputUpscalerItem(combo, "FSR 4", OutputUpscalerBackend.Fsr4);
                AddOutputUpscalerItem(combo, "XeSS", OutputUpscalerBackend.XeSS);
                AddOutputUpscalerItem(combo, "DLSS", OutputUpscalerBackend.Dlss);

                for (var index = 0; index < combo.Items.Count; index++)
                {
                    if (combo.Items[index] is ComboBoxItem item && item.Tag is OutputUpscalerBackend backend && backend == selected)
                    {
                        combo.SelectedIndex = index;
                        return;
                    }
                }
                combo.SelectedIndex = 0;
            }
            finally
            {
                _isUpdatingOutputUpscaler = false;
            }
        }

        internal static void AddOutputUpscalerItem(ComboBox combo, string label, OutputUpscalerBackend backend, bool isSentinel = false)
        {
            var item = new ComboBoxItem { Content = label, Tag = backend };
            if (isSentinel) item.Classes.Add("SentinelOption");
            combo.Items.Add(item);
        }

        private void CmbOutputUpscaler_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingOutputUpscaler || sender is not ComboBox combo
                || combo.SelectedItem is not ComboBoxItem item
                || item.Tag is not OutputUpscalerBackend selected)
                return;

            // Staged only — applied to disk when the user explicitly hits Install/Update.
            _game.OutputUpscalerSettings = new GameOutputUpscalerSettings
            {
                Backend = selected,
                AppliedAtUtc = _game.OutputUpscalerSettings?.AppliedAtUtc
            };

            RefreshInstallActionAvailability();
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

                var needsNightly = settings.Route != FrameGenerationRoute.Disabled &&
                    settings.Output == FrameGenerationOutput.DlssG &&
                    settings.NvngxReplacement is FrameGenerationNvngxReplacement.Arturs or FrameGenerationNvngxReplacement.Combo;
                // Skipped entirely in "Modded" mode (Setup NR's Mod + OptiScaler) — auto-switching to
                // the official Nightly channel would silently undo the user's NR selector choice, and
                // the Matheus wrapper isn't part of this channel machinery to switch back into anyway.
                if (!_optiShowingModded && needsNightly && !CurrentlySelectedOptiScalerVersionSupportsNvngxReplacement())
                {
                    if (string.IsNullOrEmpty(_optiVersionBeforeAutoNightlySwitch))
                    {
                        _optiVersionBeforeAutoNightlySwitch = (this.FindControl<ComboBox>("CmbOptiVersion")?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                        _optiBetaBeforeAutoNightlySwitch = _optiShowingBeta;
                        _optiCustomBeforeAutoNightlySwitch = _optiShowingCustom;
                    }
                    _optiShowingNightly = true;
                    _optiShowingBeta = false;
                    _optiShowingCustom = false;
                    UpdateOptiChannelButtons();
                    if (_cachedComponentService != null)
                        PopulateOptiVersionCombo(_cachedComponentService);
                    await ShowToastAsync(GetResourceString("TxtMfgRequiresNightlyToast",
                        "MFG with DLSS Enabler requires a Nightly OptiScaler version — switched automatically."));
                }
                else if (!_optiShowingModded && !needsNightly && !string.IsNullOrEmpty(_optiVersionBeforeAutoNightlySwitch))
                {
                    _optiShowingBeta = _optiBetaBeforeAutoNightlySwitch;
                    _optiShowingCustom = _optiCustomBeforeAutoNightlySwitch;
                    _optiShowingNightly = !_optiShowingBeta && !_optiShowingCustom;
                    UpdateOptiChannelButtons();
                    if (_cachedComponentService != null)
                        PopulateOptiVersionCombo(_cachedComponentService);
                    SelectOptiVersion(_optiVersionBeforeAutoNightlySwitch);
                    _optiVersionBeforeAutoNightlySwitch = null;
                }

                // Staged only — applied to disk when the user explicitly hits Install/Update.
                RefreshInstallActionAvailability();
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
            selection.Text = settings.Route == FrameGenerationRoute.Disabled ? route : $"{output} {multiplier}";
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
            if (!_injectionMethodAutoSelected && !_injectionMethodPinnedByConfig)
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
                ToolTip.SetTip(txtGameName, newName);
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

        /// <summary>Same folder-resolution OptiScaler's own install uses (GameInstallationService.
        /// DetermineInstallDirectory — handles Unreal's Binaries/Win64 and Phoenix layouts, not just
        /// InstallPath's root) — kept in sync so "is the mod's exe staged here" checks (see
        /// IsDanielModStagedButIncomplete) look in the same place the wizard actually staged it.</summary>
        private string? ResolveDanielModGameDir() => new GameInstallationService().DetermineInstallDirectory(_game);

        /// <summary>Downloads (if not already cached) and stages danielblnc's installer into the game
        /// folder, showing progress on the same bar used for OptiScaler downloads — done here, before
        /// the wizard modal opens, so the modal itself no longer needs its own download step. Returns
        /// the game folder on success, or null (after showing an error) on failure.</summary>
        /// <summary>Offers to add a Windows Defender exclusion for the game folder and the mod's
        /// cache folder before danielblnc's installer ever lands there — that installer is small and
        /// unsigned, so Defender sometimes flags it when the user later runs it manually themselves
        /// (an action this app has no visibility into, so there's no failure to react to at that
        /// point — see WindowsDefenderExclusionHelper). Skips the prompt entirely once
        /// Game.DlssNrDefenderExclusionAdded is already true. There is deliberately no unprivileged
        /// "is it already excluded?" probe here anymore — Get-MpPreference now refuses to report
        /// ExclusionPath to a non-admin process on current Windows builds (it returns an "N/A: Must be
        /// an administrator..." string with exit code 0 instead of throwing, which used to get
        /// misread as a literal excluded path and never matched anything — silently useless, so it
        /// always re-prompted for an exclusion the user had actually already added by hand). The
        /// user's own word (via the dialog's "Continue" button) is the only reliable signal available
        /// without elevating just to check. Returns false if the user cancelled outright — the caller
        /// must not proceed to download/stage the installer in that case.</summary>
        private async Task<bool> OfferDanielModDefenderExclusionAsync(string gameDir)
        {
            if (!OperatingSystem.IsWindows()) return true;
            if (_game.DlssNrDefenderExclusionAdded) return true;

            var dialog = new ConfirmDialog(this,
                GetResourceString("TxtSetupNrDefenderExclusionOfferTitle", "Windows Defender exclusion"),
                GetResourceString("TxtSetupNrDefenderExclusionOfferMsg",
                    "danielblnc's installer is unsigned, so Defender may flag it. If you trust it, click \"Add exclusion\" (needs admin permission). Otherwise, add the exclusion yourself and press \"Continue\"."),
                confirmText: GetResourceString("TxtSetupNrAddExclusionOnlyBtn", "Add exclusion"),
                thirdButtonText: GetResourceString("TxtSetupNrDefenderExclusionContinueBtn", "Continue")
            );
            var addException = await dialog.ShowDialog<bool>(this);

            if (dialog.ThirdButtonClicked)
            {
                // The user says they've already handled it (manually, or it was excluded before) —
                // take their word for it and stop asking for this game. Left unverified: if a later
                // install fails, CancelStagedDanielModInstallAsync resets this so we ask again instead
                // of trusting an unconfirmed claim forever.
                _game.DlssNrDefenderExclusionAdded = true;
                _game.DlssNrDefenderExclusionVerified = false;
                return true;
            }

            if (addException)
            {
                _game.DlssNrDefenderExclusionAdded = await WindowsDefenderExclusionHelper.TryAddExclusionsAsync(gameDir, _dlssNrService.CacheRootPath);
                _game.DlssNrDefenderExclusionVerified = _game.DlssNrDefenderExclusionAdded;
                return true;
            }

            return false; // Cancel (or the titlebar X) — don't proceed with the install.
        }

        private async Task<string?> DownloadAndStageDanielModAsync(string version)
        {
            var gameDir = ResolveDanielModGameDir();
            if (gameDir == null)
            {
                await new ConfirmDialog(this, GetResourceString("TxtError", "Error"),
                    GetResourceString("TxtSetupNrCannotResolveDir", "Could not resolve the game folder.")).ShowDialog<object>(this);
                return null;
            }

            // The actual block happens when the user later double-clicks the installer themselves
            // (outside this app's process — see WindowsDefenderExclusionHelper), not during our own
            // download/copy, so catching a failure here would be too late. Ask up front instead,
            // every time, unless it's already excluded — skips the prompt once it's been added once.
            if (!await OfferDanielModDefenderExclusionAsync(gameDir)) return null;

            var bdProgress = this.FindControl<Border>("BdProgress");
            var prgDownload = this.FindControl<ProgressBar>("PrgDownload");
            var txtProgressState = this.FindControl<TextBlock>("TxtProgressState");
            var downloadingFmt = GetResourceString("TxtSetupNrDownloadingFormat", "Downloading {0} v{1}...{2}");

            try
            {
                if (bdProgress != null) bdProgress.IsVisible = true;
                var progress = new Progress<double>(p => Dispatcher.UIThread.Post(() =>
                {
                    if (prgDownload != null) prgDownload.Value = p;
                    if (txtProgressState != null) txtProgressState.Text = string.Format(downloadingFmt, "danielblnc", version, $" {p:P0}");
                }));

                try
                {
                    await _dlssNrService.DownloadAsync(version, progress);
                    _dlssNrService.Stage(version, gameDir);
                    return gameDir;
                }
                catch (Exception ex) when (DlssNrOnAmdService.IsSmartScreenBlock(ex))
                {
                    // Ask before touching Defender settings — never add the exclusion silently. Only
                    // offered here (reactively, once we actually hit a block) rather than up front on
                    // every install, since most downloads never get flagged in the first place.
                    bool exclusionAdded = false;
                    if (OperatingSystem.IsWindows())
                    {
                        var addException = await new ConfirmDialog(this,
                            GetResourceString("TxtSetupNrSmartScreenBlockedTitle", "Windows Defender blocked this file"),
                            string.Format(GetResourceString("TxtSetupNrSmartScreenBlockedOffer",
                                "Windows Defender flagged {0} as a threat and removed it — common for small, unsigned tools like this one. We can add an exclusion for the game folder and retry the download. This needs administrator permission — Windows will ask you to confirm."),
                                DlssNrOnAmdService.StagedExeFileName),
                            confirmText: GetResourceString("TxtSetupNrAddExclusionBtn", "Add exclusion & retry")
                        ).ShowDialog<bool>(this);

                        if (addException)
                        {
                            exclusionAdded = await WindowsDefenderExclusionHelper.TryAddExclusionsAsync(gameDir, _dlssNrService.CacheRootPath);
                            _game.DlssNrDefenderExclusionAdded = exclusionAdded;
                            _game.DlssNrDefenderExclusionVerified = exclusionAdded;
                        }
                    }

                    if (exclusionAdded)
                    {
                        try
                        {
                            await _dlssNrService.DownloadAsync(version, progress);
                            _dlssNrService.Stage(version, gameDir);
                            return gameDir;
                        }
                        catch (Exception ex2)
                        {
                            await new ConfirmDialog(this, GetResourceString("TxtError", "Error"), string.Format(
                                GetResourceString("TxtSetupNrLaunchError", "Could not launch the installer: {0}"), ex2.Message)).ShowDialog<object>(this);
                            return null;
                        }
                    }

                    // Declined, or the elevated command itself failed/was cancelled — same manual
                    // fallback instructions either way.
                    await new ConfirmDialog(this, GetResourceString("TxtError", "Error"), string.Format(
                        GetResourceString("TxtSetupNrSmartScreenBlocked",
                            "Windows Defender flagged {0} as a threat and removed it — common for small, unsigned tools. Open Windows Security → Virus & threat protection → Protection history to restore it if you trust danielblnc's official GitHub release, then add an exclusion for the game folder and for \"{1}\" so it isn't removed again, and try again."),
                        DlssNrOnAmdService.StagedExeFileName, _dlssNrService.CacheRootPath)).ShowDialog<object>(this);
                    return null;
                }
            }
            catch (Exception ex)
            {
                await new ConfirmDialog(this, GetResourceString("TxtError", "Error"),
                    string.Format(GetResourceString("TxtSetupNrLaunchError", "Could not launch the installer: {0}"), ex.Message)).ShowDialog<object>(this);
                return null;
            }
            finally
            {
                if (bdProgress != null) bdProgress.IsVisible = false;
            }
        }

        /// <summary>True when danielblnc's installer is sitting in the game folder from a previous
        /// Setup NR run that was interrupted before "I'm done" ever ran (window closed via the X, or
        /// the installer/weights step never finished) — nothing clears PendingDlssNrOnAmdMode or sets
        /// IsDlssNrOnAmdInstalled until DlssNrOnAmdWizardWindow.FinishAsync actually succeeds, so this
        /// state is fully derived from what's already on Game/disk rather than a new flag.</summary>
        private bool IsDanielModStagedButIncomplete(out string? gameDir)
        {
            gameDir = ResolveDanielModGameDir();
            if (gameDir == null || string.IsNullOrEmpty(_game.PendingDlssNrOnAmdMode) || _game.IsDlssNrOnAmdInstalled)
                return false;
            return File.Exists(System.IO.Path.Combine(gameDir, DlssNrOnAmdService.StagedExeFileName));
        }

        /// <summary>Deletes danielblnc's staged installer (and nvngx_dlssnr.dll, if present) and drops
        /// the pending Setup NR selection — called right after either Install path (manual or auto)
        /// detects its own failure (see ExecuteInstallAsync/RunDanielModAutoInstallAsync): staging the
        /// exe never means it's installed, so a run that doesn't finish must leave the game folder
        /// exactly as if Setup NR was never touched. Shows an error dialog if the delete itself fails
        /// (e.g. file in use), unlike the silent CleanupOrphanedDanielModStage below. No-op if
        /// IsDanielModStagedButIncomplete is already false (nothing staged to cancel).
        ///
        /// Also un-trusts a merely-claimed Defender exclusion on failure — but never a verified one
        /// (Game.DlssNrDefenderExclusionVerified): DlssNrDefenderExclusionAdded can be true just because
        /// the user clicked "Continue" on the offer dialog (their word, never actually confirmed — see
        /// OfferDanielModDefenderExclusionAsync), and a failed run is exactly the situation where that
        /// word might have been wrong. A verified exclusion (TryAddExclusions actually succeeded) can't
        /// be the reason install failed the same way, so it's left alone regardless of why this run
        /// failed — otherwise Auto Install would re-ask for an exclusion we already know exists every
        /// time something unrelated (e.g. <paramref name="requiresElevation"/>) goes wrong.</summary>
        /// <param name="requiresElevation">True when the caller knows this failure was
        /// DlssNrOnAmdService.AutomatedInstallResult.RequiresElevation — has nothing to do with
        /// Defender at all, so even an unverified "Continue" claim is left alone rather than re-asked
        /// for no reason.</param>
        private async Task CancelStagedDanielModInstallAsync(bool requiresElevation = false)
        {
            if (!IsDanielModStagedButIncomplete(out var danielGameDir) || danielGameDir == null) return;

            try
            {
                var stagedExe = System.IO.Path.Combine(danielGameDir, DlssNrOnAmdService.StagedExeFileName);
                var stagedNvngx = System.IO.Path.Combine(danielGameDir, "nvngx_dlssnr.dll");
                if (File.Exists(stagedExe)) File.Delete(stagedExe);
                if (File.Exists(stagedNvngx)) File.Delete(stagedNvngx);

                _game.PendingDlssNrOnAmdMode = null;
                _game.PendingDlssNrOnAmdVersion = null;
                if (!requiresElevation && !_game.DlssNrDefenderExclusionVerified)
                    _game.DlssNrDefenderExclusionAdded = false;
                SelectCmbSetupNrTag("none");
                UpdateStatus();
            }
            catch (Exception ex)
            {
                await new ConfirmDialog(this, GetResourceString("TxtError", "Error"),
                    $"Could not delete the staged files: {ex.Message}").ShowDialog<object>(this);
            }
        }

        /// <summary>Same cleanup as CancelStagedDanielModInstallAsync, but silent and synchronous — for
        /// UpdateStatus (see below) to self-heal a staged install orphaned by something neither Install
        /// path's own failure handling could see (e.g. the app being killed mid-install), with no
        /// dedicated UI or user confirmation needed for a state the user never intentionally caused.</summary>
        private void CleanupOrphanedDanielModStage()
        {
            if (!IsDanielModStagedButIncomplete(out var danielGameDir) || danielGameDir == null) return;

            try
            {
                var stagedExe = System.IO.Path.Combine(danielGameDir, DlssNrOnAmdService.StagedExeFileName);
                var stagedNvngx = System.IO.Path.Combine(danielGameDir, "nvngx_dlssnr.dll");
                if (File.Exists(stagedExe)) File.Delete(stagedExe);
                if (File.Exists(stagedNvngx)) File.Delete(stagedNvngx);
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[SetupNr] Could not clean up orphaned staged install: {ex.Message}");
            }

            _game.PendingDlssNrOnAmdMode = null;
            _game.PendingDlssNrOnAmdVersion = null;
        }

        /// <summary>"Auto Install" for Setup NR — tries the fully headless/silent install
        /// (DlssNrOnAmdService.RunAutomatedInstallAsync) without ever showing the Setup NR wizard.
        /// Still shows whichever of its two prerequisite dialogs is actually needed: the Defender
        /// exclusion offer (via DownloadAndStageDanielModAsync, unchanged) if it isn't confirmed yet,
        /// and/or the one-time nvngx_dlssnr.dll picker (reusing the wizard's nvngxPickerOnly mode,
        /// since that machine-wide cache has to exist before Stage() can copy it into the game folder)
        /// if it isn't cached yet. On failure, shows the "try Manual Install instead" dialog and
        /// returns false — no fallback attempted here, since Manual Install is now always available as
        /// its own separate button rather than something this method needs to degrade into.</summary>
        private async Task<bool> RunDanielModAutoInstallAsync(bool isModeB)
        {
            var version = _game.PendingDlssNrOnAmdVersion ?? "";

            if (!_dlssNrService.IsNvngxDlssNrCached())
            {
                var picker = new DlssNrOnAmdWizardWindow(this, _game, version, isModeB, nvngxPickerOnly: true);
                await picker.ShowDialog<bool>(this);
                if (!picker.Succeeded) return false;
            }

            // Shown before DownloadAndStageDanielModAsync (not just around RunAutomatedInstallAsync
            // below) because that call already includes its own invisible wait the user could easily
            // read as "did nothing": the Defender exclusion offer's TryAddExclusionsAsync deliberately
            // pauses a couple seconds after adding a fresh exclusion so Defender's real-time protection
            // actually picks it up before the install proceeds (see WindowsDefenderExclusionHelper).
            // The finally block's UpdateStatus() restores whatever the real state actually is
            // afterwards, whether that's success, failure, or the user having cancelled in between.
            ShowDanielModAutoInstallingStatus();
            try
            {
                var gameDir = await DownloadAndStageDanielModAsync(version);
                if (gameDir == null) return false;

                var result = await _dlssNrService.RunAutomatedInstallAsync(_game, gameDir, version, isModeB);
                if (result == DlssNrOnAmdService.AutomatedInstallResult.Success) return true;

                // Same as the manual path's failure handling: the staged exe never means it's
                // installed, so don't leave it (or nvngx_dlssnr.dll) sitting in the game folder. Needing
                // elevation has nothing to do with Defender, so don't un-trust an exclusion that may
                // well have just been verified this same run (see CancelStagedDanielModInstallAsync).
                bool requiresElevation = result == DlssNrOnAmdService.AutomatedInstallResult.RequiresElevation;
                await CancelStagedDanielModInstallAsync(requiresElevation);

                await new ConfirmDialog(this,
                    GetResourceString("TxtSetupNrAutomationFailedTitle", "Manual install needed"),
                    requiresElevation
                        ? GetResourceString("TxtSetupNrAutomationNeedsAdminMsg", "This game's folder needs administrator rights, so Auto Install can't run the installer silently. Use Manual Install instead — Windows will ask you to confirm.")
                        : GetResourceString("TxtSetupNrAutomationFailedMsg", "We couldn't finish the automated install. Try Manual Install instead."),
                    isAlert: true).ShowDialog<object>(this);
                return false;
            }
            finally
            {
                UpdateStatus();
            }
        }

        /// <summary>Visual-only "installing..." state for the status area while Auto Install's headless
        /// process runs in the background — there's no window/console for the user to look at
        /// otherwise, so without this the click looks like it did nothing until it finishes or fails.
        /// Whatever this sets is overwritten by the next UpdateStatus() call (see caller's finally
        /// block), so nothing here needs to be reverted explicitly.</summary>
        private void ShowDanielModAutoInstallingStatus()
        {
            var txtStatus = this.FindControl<TextBlock>("TxtStatus");
            var statusIndicator = this.FindControl<Ellipse>("StatusIndicator");
            var btnInstall = this.FindControl<Button>("BtnInstall");
            var btnInstallManual = this.FindControl<Button>("BtnInstallManual");
            if (txtStatus != null) txtStatus.Text = GetResourceString("TxtSetupNrAutoInstalling", "Installing danielblnc's mod...");
            if (statusIndicator != null) statusIndicator.Fill = new SolidColorBrush(Color.FromRgb(0xD4, 0xA0, 0x17));
            if (btnInstall != null) btnInstall.IsEnabled = false;
            if (btnInstallManual != null) btnInstallManual.IsEnabled = false;
        }

        /// <summary>Deletes whatever the original daniel-only install created and restores whatever it
        /// overwrote, straight from the manifest saved during that install (see
        /// DlssNrOnAmdService.TryFinishInstall/SaveDanielModManifest — shared by both the manual wizard
        /// and Auto Install) — same pattern GameInstallationService uses for OptiScaler's own uninstall.
        /// No re-download/re-run of danielblnc's installer needed:
        /// everything it touched is already tracked. Shared by CmbSetupNr's "none" case and the
        /// dedicated "Uninstall mod" button (see BtnUninstall_Click), which just re-selects that tag to
        /// reach the same case rather than duplicating this.</summary>
        private void UninstallDanielModOnly()
        {
            var gameDir = ResolveDanielModGameDir();
            if (gameDir != null) _dlssNrService.RestoreFromManifest(gameDir);
        }

        private async Task ExecuteInstallAsync(bool isManualMode)
        {
            // Mode B ("daniel-and-opti") commits the daniel-mod files (and Game state) as soon as
            // that step succeeds, well before the OptiScaler half below even starts — cancelling or
            // failing anywhere after that (the manual-install folder picker, a corrupt-artifact
            // prompt, a wrapper-download error, InstallOptiScaler itself throwing) used to leave that
            // half-done state sitting in the game folder with nothing to clean it up. These three
            // track whether this call needs to undo that half if the OptiScaler half never finishes —
            // see RollbackFreshDanielModIfNeeded and its call sites below.
            bool danielFreshThisRun = false;
            string? danielFreshGameDir = null;
            bool moddedInstallSucceeded = false;
            void RollbackFreshDanielModIfNeeded()
            {
                if (!danielFreshThisRun || moddedInstallSucceeded || danielFreshGameDir == null) return;
                try
                {
                    _dlssNrService.RestoreFromManifest(danielFreshGameDir);
                    _game.IsDlssNrOnAmdInstalled = false;
                    _game.DlssNrOnAmdVersion = null;
                    _game.InstalledDlssNrOnAmdMode = null;
                    DebugWindow.Log("[SetupNr] OptiScaler half of \"Mod + OptiScaler\" didn't complete — rolled back the daniel mod files placed moments ago.");
                }
                catch (Exception rbEx) { DebugWindow.Log($"[SetupNr] Rollback of daniel mod files failed: {rbEx.Message}"); }
            }

            // A saved "Setup NR" run is pending for this game (see CmbSetupNr_SelectionChanged) — the
            // interactive part (staging + running danielblnc's console installer) happens here,
            // triggered by this same Install button rather than a separate one in that dialog.
            // Mode A (danielblnc only) stops here; Mode B falls through to the normal install below,
            // which targets the wrapper Custom version already auto-selected when Setup NR was saved.
            if (!string.IsNullOrEmpty(_game.PendingDlssNrOnAmdMode))
            {
                var isModeB = _game.PendingDlssNrOnAmdMode == "daniel-and-opti";
                bool danielSucceeded;

                // Weights (+ whatever else Mode B's installer produces) are machine-invariant once
                // generated once — see DlssNrOnAmdService's Mode B output cache notes. Skip staging and
                // running danielblnc's installer altogether when we already have that output cached.
                var cachedModeBGameDir = isModeB ? ResolveDanielModGameDir() : null;
                if (isModeB && cachedModeBGameDir != null && _dlssNrService.IsModeBOutputCached())
                {
                    // Outside this method's own try/catch (that one only wraps the shared OptiScaler
                    // install further below) — a locked target file (e.g. the game is currently
                    // running and has last install's dbghelp.dll loaded) would otherwise throw straight
                    // out of this button click with no visible error at all.
                    try
                    {
                        danielSucceeded = _dlssNrService.TryUseCachedModeBOutput(_game, cachedModeBGameDir, _game.PendingDlssNrOnAmdVersion ?? "");
                    }
                    catch (Exception ex)
                    {
                        DebugWindow.Log($"[SetupNr] Could not reuse cached Mode B output: {ex.Message}");
                        await new ConfirmDialog(this, GetResourceString("TxtError", "Error"),
                            string.Format(GetResourceString("TxtSetupNrReuseWeightsCacheFailedFormat",
                                "Could not reuse the cached weights: {0}\n\nIs the game currently running? Close it and try again."), ex.Message),
                            isAlert: true).ShowDialog<object>(this);
                        danielSucceeded = false;
                    }
                }
                else if (isManualMode)
                {
                    if (await DownloadAndStageDanielModAsync(_game.PendingDlssNrOnAmdVersion ?? "") == null) return;
                    var wizard = new DlssNrOnAmdWizardWindow(this, _game, _game.PendingDlssNrOnAmdVersion ?? "", isModeB);
                    await wizard.ShowDialog<bool>(this);
                    danielSucceeded = wizard.Succeeded;
                    if (!danielSucceeded)
                    {
                        // Closed (X, or the weights marker never showed up) without finishing — the
                        // staged exe never means it's installed, so clean it up now instead of leaving
                        // it for the user to notice and clear manually via "Cancel Setup NR install".
                        await CancelStagedDanielModInstallAsync();
                    }
                }
                else
                {
                    danielSucceeded = await RunDanielModAutoInstallAsync(isModeB);
                }

                // Re-derive the lock from whatever's left instead of assuming success unlocked things
                // (both paths above clear PendingDlssNrOnAmdMode on success, either outcome).
                SetOptiScalerControlsLocked(_game.PendingDlssNrOnAmdMode == "daniel-only");
                if (!danielSucceeded) return;

                if (!isModeB)
                {
                    UpdateStatus();
                    await ShowToastAsync(GetResourceString("TxtSetupNrDanielInstalledDone", "danielblnc's mod installed successfully."));
                    return;
                }

                // Reached only for Mode B, past its own success check above — everything from here on
                // needs the rollback guard declared at the top of this method.
                danielFreshThisRun = true;
                danielFreshGameDir = cachedModeBGameDir;

                // Mode B: weights are generated — make sure the selected Matheus build is actually on
                // disk before falling through to the normal install below, which reads CmbOptiVersion's
                // Tag directly as optiscalerVersion. StartModdedVersionDownload already kicked this off
                // in the background when the version was picked; just await that same task instead of
                // starting a second, colliding download. Falls back to downloading it directly if it was
                // never triggered this session (e.g. the combo defaulted to the only available item
                // without the user touching it) and isn't already registered.
                var selectedModdedTag = (this.FindControl<ComboBox>("CmbOptiVersion")?.SelectedItem as ComboBoxItem)?.Tag as string;
                if (!string.IsNullOrEmpty(selectedModdedTag) &&
                    _moddedVersionRawByRegisteredName.TryGetValue(selectedModdedTag, out var selectedModdedRawVersion))
                {
                    try
                    {
                        if (string.Equals(_pendingModdedDownloadVersion, selectedModdedRawVersion, StringComparison.OrdinalIgnoreCase) &&
                            _pendingModdedDownloadTask != null)
                            await _pendingModdedDownloadTask;
                        else if (_cachedComponentService != null && !_customVersions.Contains(selectedModdedTag))
                            await _cachedComponentService.DownloadAndImportAmdWrapperVersionAsync(selectedModdedRawVersion);
                    }
                    catch (Exception ex)
                    {
                        DebugWindow.Log($"[SetupNr] Modded wrapper download failed: {ex.Message}");
                        await new ConfirmDialog(this, "Error", $"Could not download the OptiScaler wrapper build: {ex.Message}").ShowDialog<object>(this);
                        RollbackFreshDanielModIfNeeded();
                        return;
                    }
                }
            }

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

            // Read selected Extras (FSR 4 Swap) version before any async work
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

            // Read selected DXGI Spoofing option before any async work
            var cmbSpoofing = this.FindControl<ComboBox>("CmbSpoofing");
            var selectedSpoofing = (cmbSpoofing?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";

            // Read selected RenoDX option before any async work (experimental, opt-in). The actual
            // resolution (Auto lookup/download, ReShade-presence check, failure modals) needs
            // network + dialogs, so it happens later in this method, right before the Task.Run —
            // same shape as the Fakenvapi-nightly/DLSS-Enabler-Mirror pre-install resolution below.
            // Tag is "none" (default — never tries to fetch anything), "auto", a full cached file
            // path, or "__manage__" (never actually installed, the combo snaps back to "none" on
            // selection).
            var cmbRenodxVersion = this.FindControl<ComboBox>("CmbRenodxVersion");
            var selectedRenodxTag = (cmbRenodxVersion?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            bool renodxRequested = extrasComponentService.Config.ShowExperimentalFeatures &&
                                    !string.IsNullOrEmpty(selectedRenodxTag) &&
                                    selectedRenodxTag != "none" &&
                                    selectedRenodxTag != "__manage__";

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
                        GetResourceString("TxtErrNoOptiOrExtrasText", "Select an OptiScaler version or an FSR 4 Swap version before installing.")
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

                // Streamline is no longer tied to "is this a Nightly version" — it's tied to
                // whether the game's (Auto-resolved) FG configuration actually needs it: either
                // FGInput=dlssg or FGOutput=dlssg (with or without DLSS Enabler as the NVNGX
                // replacement). isNightlyChannel is kept separately below only for the
                // Fakenvapi-bundling quirk of Nightly packages.
                var fgConfigService = new FrameGenerationConfigurationService();
                var installGpu = GpuSelectionHelper.GetPreferredGpu(_gpuService, componentService.Config.DefaultGpuId);
                var isNightlyChannel = componentService.IsNightlyOptiScalerVersion(optiscalerVersion);
                var installStreamline = _game.FrameGenerationSettings != null &&
                    fgConfigService.RequiresStreamline(_game.FrameGenerationSettings, fgConfigService.DetectCapabilities(_game, installGpu), optiscalerVersion);
                var mfgWithEnabler = _game.FrameGenerationSettings?.Route != FrameGenerationRoute.Disabled &&
                    _game.FrameGenerationSettings?.Output == FrameGenerationOutput.DlssG &&
                    _game.FrameGenerationSettings?.NvngxReplacement is FrameGenerationNvngxReplacement.Arturs or FrameGenerationNvngxReplacement.Combo;
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
                        var streamlineVersion = _game.FrameGenerationSettings?.StreamlineVersion;
                        if (string.IsNullOrEmpty(streamlineVersion))
                            streamlineVersion = componentService.LatestStreamlineVersion;
                        streamlineCacheDir = await componentService.DownloadStreamlineAsync(streamlineVersion ?? "");
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
                if (isNightlyChannel && (string.IsNullOrWhiteSpace(nightlyGameDir) ||
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
                if (!isNightlyChannel && installFakenvapi && !componentService.IsFakenvapiCached(selectedFakenvapiVersion!))
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

                // RenoDX (experimental, opt-in): resolve the actual addon file now — Auto tries the
                // per-game cache first, then a wiki lookup + direct download; a specific cached path
                // (the combo's non-"auto" tag IS the full file path, see PopulateRenodxComboBox)
                // just uses that directly, no network. Two failure paths need a modal right here —
                // InstallOptiScaler has no UI to show one: nothing could be resolved at all, or
                // ReShade wasn't detected in the game folder (RenoDX requires it).
                string? renodxAddonPath = null;
                bool installRenodx = false;
                if (renodxRequested)
                {
                    var renodxGameKey = ResolveRenodxGameKey();
                    if (selectedRenodxTag == "auto")
                    {
                        renodxAddonPath = componentService.GetCachedRenodxAddonPath(renodxGameKey);
                        if (string.IsNullOrEmpty(renodxAddonPath))
                        {
                            var renodxModsService = new RenodxModsService();
                            if (renodxModsService.TryGetForGame(_game.Name, out var renodxEntry) &&
                                !string.IsNullOrEmpty(renodxEntry?.SnapshotUrl))
                            {
                                try
                                {
                                    Dispatcher.UIThread.Post(() =>
                                    {
                                        if (bdProgress != null) bdProgress.IsVisible = true;
                                        if (prgDownload != null) prgDownload.IsIndeterminate = true;
                                        if (txtProgressState != null) txtProgressState.Text = "Downloading RenoDX addon...";
                                    });
                                    renodxAddonPath = await componentService.DownloadRenodxAddonAsync(
                                        renodxEntry!.SnapshotUrl!, renodxGameKey, renodxEntry.GameName);
                                }
                                catch (Exception ex)
                                {
                                    DebugWindow.Log($"[Install] RenoDX auto-download failed: {ex.Message}");
                                    renodxAddonPath = null;
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
                        }
                    }
                    else
                    {
                        renodxAddonPath = selectedRenodxTag;
                    }

                    if (string.IsNullOrEmpty(renodxAddonPath))
                    {
                        // Doesn't proceed with the install at all — same precedent as the NukemFG
                        // cache-miss check just above (return instead of silently dropping the
                        // component and continuing), since the user explicitly asked for RenoDX
                        // (selected something other than "None") and it couldn't be honored.
                        await new ConfirmDialog(this,
                            GetResourceString("TxtRenodxNotFoundTitle", "Couldn't get the RenoDX addon automatically"),
                            GetResourceString("TxtRenodxNotFoundMsg", "Download it manually from the RenoDX Mods wiki and add it from Manage Local Versions if you'd like to use it."),
                            isAlert: true,
                            linkUrl: RenodxModsService.WikiUrl,
                            linkText: GetResourceString("TxtRenodxWikiLinkText", "Open the RenoDX Mods wiki")
                        ).ShowDialog<object>(this);
                        return;
                    }
                    else
                    {
                        var renodxGameDir = overrideGameDir ?? installService.DetermineInstallDirectory(_game);
                        var reshadeDetected = IsReshadeInstalledForGame(renodxGameDir);

                        if (reshadeDetected)
                        {
                            installRenodx = true;
                        }
                        else
                        {
                            var proceedAnyway = await new ConfirmDialog(this,
                                GetResourceString("TxtRenodxReshadeMissingTitle", "ReShade not detected"),
                                GetResourceString("TxtRenodxReshadeMissingMsg", "RenoDX requires ReShade to already be installed in this game's folder, and it wasn't detected. Install it first from reshade.me, or continue anyway if you already have it under a different setup."),
                                confirmText: GetResourceString("TxtBtnProceedAnyway", "Proceed anyway"),
                                linkUrl: "https://reshade.me",
                                linkText: GetResourceString("TxtRenodxOpenReshadeLinkText", "Open reshade.me")
                            ).ShowDialog<bool>(this);

                            // Cancel aborts the whole install, same as the "not found" case above —
                            // it doesn't silently fall back to installing everything except RenoDX.
                            if (!proceedAnyway) return;
                            installRenodx = true;
                        }
                    }
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

                var dlssEnablerCacheDir = string.Empty;
                if (mfgWithEnabler)
                {
                    var enablerVersion = _game.FrameGenerationSettings!.DlssEnablerVersion;
                    if (string.IsNullOrEmpty(enablerVersion))
                    {
                        var title = GetResourceString("TxtError", "Error");
                        await new ConfirmDialog(this, title,
                            GetResourceString("TxtNoDlssEnablerVersionSelected", "No DLSS Enabler version selected. Configure Frame Generation for this game first.")
                        ).ShowDialog<object>(this);
                        return;
                    }

                    if (ComponentManagementService.IsDlssEnablerMirrorTag(enablerVersion))
                    {
                        var mirrorVersion = ComponentManagementService.StripDlssEnablerMirrorTag(enablerVersion);
                        dlssEnablerCacheDir = componentService.GetDlssEnablerMirrorCachePath(mirrorVersion);
                        if (!componentService.IsDlssEnablerMirrorCached(mirrorVersion))
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
                                    if (prgDownload != null) prgDownload.IsIndeterminate = false;
                                    if (txtProgressState != null) txtProgressState.Text = $"Downloading DLSS Enabler v{mirrorVersion}...";
                                });
                                var dlssEnablerProgress = new Progress<double>(p =>
                                    Dispatcher.UIThread.Post(() => { if (prgDownload != null) prgDownload.Value = p; }));
                                dlssEnablerCacheDir = await componentService.DownloadDlssEnablerMirrorAsync(mirrorVersion, dlssEnablerProgress);
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
                    }
                    else
                    {
                        dlssEnablerCacheDir = componentService.GetDlssEnablerCachePath(enablerVersion);
                    }
                }

                // Re-show progress here: the DLSS Enabler Mirror download above (if it ran) hides
                // bdProgress and re-enables the buttons in its own finally block, which otherwise
                // leaves the UI looking idle while the file-copy/hash install step below still runs.
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
                        txtProgressState.Text = string.Format(extractFormat, optiscalerVersion);
                    }
                });

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
                                                        ensureFakenvapiIfMissing: isNightlyChannel,
                                                        installDlssEnabler: mfgWithEnabler,
                                                        dlssEnablerCachePath: dlssEnablerCacheDir,
                                                        installRenodx: installRenodx,
                                                        renodxAddonCachePath: renodxAddonPath ?? "",
                                                        gpu: preferredGpuForFsr4,
                                                        dxgiSpoofing: selectedSpoofing);
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
                if (installRenodx) installedComponents += " + RenoDX";

                // ── FSR 4 Swap DLL injection ────────────────────────────────────────
                if (injectExtras && !string.IsNullOrEmpty(selectedExtrasVersion))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (bdProgress != null) bdProgress.IsVisible = true;
                        if (txtProgressState != null) txtProgressState.Text = $"Downloading FSR 4 Swap v{selectedExtrasVersion}...";
                        if (prgDownload != null) prgDownload.IsIndeterminate = false;
                    });

                    string extrasDllPath;
                    List<(string TargetPath, string SourceContentPath)> filesToInject = new();
                    try
                    {
                        var extrasProgress = new Progress<double>(p =>
                            Dispatcher.UIThread.Post(() => { if (prgDownload != null) prgDownload.Value = p; }));

                        extrasDllPath = await componentService.DownloadExtrasDllAsync(selectedExtrasVersion, extrasProgress);

                        var packagedFiles = await componentService.GetExtrasPackagedFileNamesAsync(selectedExtrasVersion, extrasProgress);
                        var gameDirForSwap = resolvedGameDir ?? installService.DetermineInstallDirectory(_game) ?? _game.InstallPath;

                        if (packagedFiles.Count > 0)
                        {
                            var cacheDir = componentService.GetExtrasDllCachePath(selectedExtrasVersion);
                            var candidates = Fsr4Int8DllHelper.BuildSwapCandidates(gameDirForSwap, cacheDir, packagedFiles);

                            if (candidates.Count > 1 && componentService.Config.Fsr4SwapAskEveryTime)
                            {
                                Dispatcher.UIThread.Post(() => { if (bdProgress != null) bdProgress.IsVisible = false; });
                                var options = candidates.Select(c => (
                                    FileName: System.IO.Path.GetFileName(c.TargetPath),
                                    EffectLabel: Fsr4Int8DllHelper.GetEffectDisplayName(System.IO.Path.GetFileName(c.SourceContentPath)),
                                    ExistsAtDestination: File.Exists(c.TargetPath)
                                )).ToList();

                                var chosen = await ShowFsr4SwapSelectionAsync(options);
                                if (chosen == null || chosen.Count == 0) goto SkipExtras; // Cancelled

                                candidates = candidates
                                    .Where(c => chosen.Contains(System.IO.Path.GetFileName(c.TargetPath), StringComparer.OrdinalIgnoreCase))
                                    .ToList();

                                Dispatcher.UIThread.Post(() =>
                                {
                                    if (bdProgress != null) bdProgress.IsVisible = true;
                                    if (prgDownload != null) prgDownload.IsIndeterminate = true;
                                });
                            }
                            else if (candidates.Count > 1)
                            {
                                candidates = Fsr4Int8DllHelper.FilterCandidatesByDefaultKeys(candidates, componentService.Config.Fsr4SwapDefaultFileKeys);
                                if (candidates.Count == 0)
                                {
                                    Dispatcher.UIThread.Post(() => { if (bdProgress != null) bdProgress.IsVisible = false; });
                                    await new ConfirmDialog(this, GetResourceString("TxtError", "Error"),
                                        GetResourceString("TxtFsr4SwapNoDefaultsConfigured",
                                            "No files are selected in FSR 4 Swap Options (Settings). Check at least one file there, or switch it back to \"Ask me every time\".")
                                    ).ShowDialog<object>(this);
                                    goto SkipExtras;
                                }
                            }

                            filesToInject = candidates;
                        }
                        else
                        {
                            var destPath = System.IO.Path.Combine(gameDirForSwap, System.IO.Path.GetFileName(extrasDllPath));
                            filesToInject.Add((destPath, extrasDllPath));
                        }
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.UIThread.Post(() => { if (bdProgress != null) bdProgress.IsVisible = false; });
                        await new ConfirmDialog(this, "Warning",
                            $"FSR 4 Swap DLL download failed (OptiScaler was still installed):\n{ex.Message}").ShowDialog<object>(this);
                        goto SkipExtras;
                    }

                    // Copy DLL into the actual game install directory (overwrite the placeholder)
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (txtProgressState != null) txtProgressState.Text = "Injecting FSR 4 Swap DLL...";
                        if (prgDownload != null) { prgDownload.IsIndeterminate = true; }
                    });

                    try
                    {
                        await Task.Run(() =>
                        {
                            var installSvc = new GameInstallationService();
                            var gameDir = resolvedGameDir ?? installSvc.DetermineInstallDirectory(_game) ?? _game.InstallPath;
                            
                            foreach (var file in filesToInject)
                            {
                                if (!File.Exists(file.SourceContentPath))
                                    throw new Exception("Installation failed because the FSR 4 Swap package is corrupt or incomplete.");
                                installSvc.InjectExtrasDll(_game, file.TargetPath, file.SourceContentPath);
                                DebugWindow.Log($"[ExtrasInject] Copied DLL to {file.TargetPath} and set version to {selectedExtrasVersion}");
                            }

                            if (selectedExtrasIsInt8)
                            {
                                var customAmdxc64Path = componentService.GetCachedCustomAmdxc64Path(selectedExtrasVersion);
                                if (customAmdxc64Path != null)
                                    installSvc.InstallCustomAmdxc64(gameDir, customAmdxc64Path);
                                // The forcing keys are specific to the INT8 fallback path.
                                installSvc.ConfigureFsr4IntFallback(gameDir, isRdna4, isRdna2);
                            }
                            _game.Fsr4ExtraVersion = selectedExtrasVersion;
                        });
                    }
                    catch (Exception ex) when ((ex is FileNotFoundException || ex.Message.Contains("corrupt or incomplete")) && !retryDone)
                    {
                        retryDone = true;
                        DebugWindow.Log($"[Install] Detected corrupt FSR 4 Swap cache. Triggering auto-retry...");
                        try { if (File.Exists(extrasDllPath)) File.Delete(extrasDllPath); } catch (Exception delEx) { DebugWindow.Log($"[Install] Failed to delete FSR 4 Swap cache: {delEx.Message}"); }
                        Dispatcher.UIThread.Post(() => { if (prgDownload != null) { prgDownload.Value = 0; prgDownload.IsIndeterminate = true; } });
                        goto RetryFullInstall;
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (prgDownload != null) prgDownload.IsIndeterminate = false;
                        if (bdProgress != null) bdProgress.IsVisible = false;
                    });

                    installedComponents += " + FSR 4 Swap";
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

                moddedInstallSucceeded = true;
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
                    if (btnInstall != null) btnInstall.IsEnabled = true;
                    if (btnInstallManual != null) btnInstallManual.IsEnabled = true;
                    if (btnUninstall != null) btnUninstall.IsEnabled = true;
                    if (cmbOptiVersion != null) cmbOptiVersion.IsEnabled = true;
                });
                await new ConfirmDialog(this, "Error", $"Installation failed: {ex.Message}"). ShowDialog<object>(this);
            }
            finally
            {
                // Covers every early "return" between here and the try above (manual-install folder
                // picker cancelled, corrupt-artifact prompt cancelled, "no version selected", etc.) as
                // well as any exception already handled by the catch — one guard instead of patching
                // each exit point by hand.
                RollbackFreshDanielModIfNeeded();
            }
        }

        /// <summary>Drives the whole "Setup NR" mode switch. Also fires on programmatic selection (e.g.
        /// PopulateVersionSelectors restoring state on window reopen), which is deliberate — it means
        /// reopening the window re-derives locking/tabs/version lists from Game state for free instead
        /// of needing separate restore logic.</summary>
        private async void CmbSetupNr_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            var cmb = this.FindControl<ComboBox>("CmbSetupNr");
            var tag = (cmb?.SelectedItem as ComboBoxItem)?.Tag as string;
            if (tag == null) return;

            var cmbDanielVersion = this.FindControl<ComboBox>("CmbDlssNrDanielVersion");
            var btnInstallManual = this.FindControl<Button>("BtnInstallManual");

            switch (tag)
            {
                case "none":
                    // Reached either by explicit user choice (including the dedicated "Uninstall mod"
                    // button, which just re-selects this tag — see BtnUninstall_Click) or as the
                    // natural default when nothing is pending/installed — PopulateVersionSelectors
                    // only ever pre-selects "none" when Game.IsDlssNrOnAmdInstalled is false, so an
                    // installed mod always starts pre-selected on its actual mode instead, and this
                    // branch only runs an uninstall on a real, deliberate switch away from it.
                    bool wasInstalled = _game.IsDlssNrOnAmdInstalled;
                    if (wasInstalled)
                    {
                        UninstallDanielModOnly();
                        _game.IsDlssNrOnAmdInstalled = false;
                        _game.DlssNrOnAmdVersion = null;
                        _game.InstalledDlssNrOnAmdMode = null;
                    }
                    _game.PendingDlssNrOnAmdMode = null;
                    _game.PendingDlssNrOnAmdVersion = null;
                    SetOptiScalerControlsLocked(false);
                    SetOptiTabsForModdedMode(false);
                    if (_cachedComponentService != null)
                    {
                        UpdateOptiChannelButtons();
                        PopulateOptiVersionCombo(_cachedComponentService);
                    }
                    if (cmbDanielVersion != null) { cmbDanielVersion.IsEnabled = false; cmbDanielVersion.Items.Clear(); }
                    if (btnInstallManual != null) btnInstallManual.IsVisible = true;
                    // Fire-and-forget: ShowToastAsync runs its own multi-second fade loop before
                    // returning, and awaiting it here would delay UpdateStatus() below (which is what
                    // actually hides the "Uninstall mod" button) until the toast finishes animating —
                    // the button stayed visible for the toast's whole duration instead of updating
                    // immediately once the uninstall itself is done.
                    if (wasInstalled) _ = ShowToastAsync(GetResourceString("TxtSetupNrUninstalledDone", "Mod uninstalled."));
                    break;

                case "daniel-only":
                    _game.PendingDlssNrOnAmdMode = "daniel-only";
                    SetOptiScalerControlsLocked(true);
                    SetOptiTabsForModdedMode(false);
                    // Both Install buttons are shown for this mode (see UpdateStatus's danielOnlyPending
                    // block below, which runs right after this switch and is authoritative for their
                    // visibility/labels) — nothing to set here.
                    // BetaInfoPanel/PanelModdedWarning live outside PanelOptiScalerVersion (so
                    // SetOptiScalerControlsLocked doesn't hide them) and PopulateOptiVersionCombo
                    // isn't called for this mode — hide both explicitly instead of leaving whichever
                    // was showing before this switch.
                    var betaInfoPanelDanielOnly = this.FindControl<Border>("BetaInfoPanel");
                    var moddedWarningPanelDanielOnly = this.FindControl<Border>("PanelModdedWarning");
                    if (betaInfoPanelDanielOnly != null) betaInfoPanelDanielOnly.IsVisible = false;
                    if (moddedWarningPanelDanielOnly != null) moddedWarningPanelDanielOnly.IsVisible = false;
                    _ = PopulateDlssNrDanielVersionComboAsync();
                    break;

                case "daniel-and-opti":
                    _game.PendingDlssNrOnAmdMode = "daniel-and-opti";
                    SetOptiScalerControlsLocked(false);
                    SetOptiTabsForModdedMode(true);
                    if (btnInstallManual != null) btnInstallManual.IsVisible = true;
                    _ = PopulateDlssNrDanielVersionComboAsync();
                    _ = PopulateModdedVersionComboAsync();
                    break;
            }

            UpdateStatus();
        }

        private void SelectCmbSetupNrTag(string tag)
        {
            var cmb = this.FindControl<ComboBox>("CmbSetupNr");
            if (cmb?.Items == null) return;
            foreach (var item in cmb.Items)
            {
                if (item is ComboBoxItem cbi && string.Equals(cbi.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
                {
                    cmb.SelectedItem = cbi;
                    return;
                }
            }
        }

        /// <summary>Locks (or unlocks) every OptiScaler-related option when danielblnc's mod alone
        /// ("daniel-only" Setup NR mode) is pending or installed — that mode is standalone and
        /// directly incompatible with OptiScaler, so nothing OptiScaler-related should be touchable.
        /// Targets whole option *panels* rather than individual tab buttons/combos: Avalonia computes
        /// IsEnabled effectively through the visual ancestor chain, so disabling the panel keeps every
        /// descendant genuinely non-interactive even when something else (e.g. a tab click handler
        /// re-populating its combo) later sets a child control's own IsEnabled back to true — which is
        /// exactly what let CmbOptiVersion stay pickable and the INT8/FP8 tabs re-unlock CmbExtrasVersion
        /// under the old per-leaf-control list.</summary>
        private static readonly string[] OptiScalerOptionPanelNames =
        {
            "PanelOptiScalerVersion", "PanelInjectionMethod", "PanelExtrasVersion", "PanelOptiPatcherVersion",
            "PanelProfile", "PanelFrameGeneration", "PanelUpscalingQuality", "PanelSpoofingHost",
            "PanelOutputUpscaler", "PanelFakenvapiVersion", "PanelNukemFGVersion", "PanelRenodxVersion",
        };

        private void SetOptiScalerControlsLocked(bool locked)
        {
            var enabled = !locked;
            foreach (var name in OptiScalerOptionPanelNames)
            {
                var control = this.FindControl<Control>(name);
                if (control != null) control.IsEnabled = enabled;
            }
        }

        /// <summary>Disables (stays visible, greyed out) every OptiScaler option panel, the
        /// RenoDX (PanelRenodxVersion, already one of OptiScalerOptionPanelNames). Deliberately does NOT
        /// touch BorderExperimentalZone itself: CmbSetupNr and CmbDlssNrDanielVersion live directly
        /// inside it (not under any locked child panel) and must stay interactive — CmbSetupNr is the
        /// only way out of daniel-only mode, and CmbDlssNrDanielVersion lets the version still be
        /// changed while it's the only thing installed. Also deliberately does NOT touch
        /// InstallBtnGroup: while daniel-only is pending, Install must stay enabled (see UpdateStatus's
        /// single-button override) so the user can actually run the pending install; once daniel-only
        /// is fully installed, its buttons are hidden outright instead (also UpdateStatus), making their
        /// enabled state moot.</summary>
        private void SetInstallOptionsEnabled(bool enabled)
        {
            foreach (var name in OptiScalerOptionPanelNames)
            {
                var control = this.FindControl<Control>(name);
                if (control != null) control.IsEnabled = enabled;
            }
        }

        /// <summary>Swaps the OptiScaler version tab bar between the normal Stable/Beta/Nightly/Custom
        /// set and the single "Modded" indicator used for "Mod + OptiScaler" — does not touch
        /// _optiShowingBeta/Nightly/Custom, so whatever channel was active before entering Modded mode
        /// is exactly what's restored on the way back out.</summary>
        private void SetOptiTabsForModdedMode(bool modded)
        {
            _optiShowingModded = modded;
            var btnStable = this.FindControl<Button>("BtnOptiStable");
            var btnBeta = this.FindControl<Button>("BtnOptiBeta");
            var btnNightly = this.FindControl<Button>("BtnOptiNightly");
            var btnCustom = this.FindControl<Button>("BtnOptiCustom");
            var btnModded = this.FindControl<Button>("BtnOptiModded");
            if (btnStable != null) btnStable.IsVisible = !modded;
            if (btnBeta != null) btnBeta.IsVisible = !modded;
            if (btnNightly != null) btnNightly.IsVisible = !modded;
            if (btnCustom != null) btnCustom.IsVisible = !modded && _customVersions.Count > 0;
            if (btnModded != null) btnModded.IsVisible = modded;
        }

        /// <summary>Populates CmbOptiVersion with MatheusGViana/dlss-5-amd-project's releases (the
        /// "Modded" channel) — kept entirely separate from PopulateOptiVersionCombo's Stable/Beta/
        /// Nightly/Custom filtering rather than wedged in as a 5th branch there, since that logic is
        /// synchronous and driven by ComponentManagementService's pre-fetched cached lists, while this
        /// needs its own live GitHub call.</summary>
        private async Task PopulateModdedVersionComboAsync()
        {
            var cmbOptiVersion = this.FindControl<ComboBox>("CmbOptiVersion");
            if (cmbOptiVersion == null || _cachedComponentService == null) return;
            var componentService = _cachedComponentService;

            cmbOptiVersion.SelectionChanged -= CmbOptiVersion_SelectionChanged;
            cmbOptiVersion.Items.Clear();
            cmbOptiVersion.IsEnabled = false;

            List<DlssNrOnAmdRelease> releases;
            try
            {
                releases = await componentService.GetAmdWrapperReleasesAsync();
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[SetupNr] Could not list Matheus wrapper releases: {ex.Message}");
                releases = new List<DlssNrOnAmdRelease>();
            }

            _moddedVersionRawByRegisteredName.Clear();

            if (releases.Count == 0)
            {
                cmbOptiVersion.Items.Add(new ComboBoxItem { Content = GetResourceString("TxtNoOptiDetected", "No version detected"), IsEnabled = false });
                cmbOptiVersion.SelectedIndex = 0;
            }
            else
            {
                for (int i = 0; i < releases.Count; i++)
                {
                    var registeredName = "custom-amd-presr-" + ComponentManagementService.SanitizeVersionName(releases[i].Version);
                    _moddedVersionRawByRegisteredName[registeredName] = releases[i].Version;
                    cmbOptiVersion.Items.Add(BuildVersionItem(releases[i].Version, isBeta: false, isLatest: i == 0, tag: registeredName));
                }
                cmbOptiVersion.SelectedIndex = 0;
                StartModdedVersionDownload(releases[0].Version);
            }

            // SelectedIndex above was set while the handler was detached (to avoid a redundant
            // download kick-off), so drive the checkbox/warning-panel visibility update manually.
            UpdateCheckboxStatesForVersion(cmbOptiVersion);

            cmbOptiVersion.IsEnabled = true;
            cmbOptiVersion.SelectionChanged += CmbOptiVersion_SelectionChanged;
        }

        /// <summary>Downloads and registers a Matheus wrapper version as soon as it's picked in
        /// CmbOptiVersion, in the background — same idea as danielblnc's own dedup-safe background
        /// download, but tracked per-window here since ComponentManagementService has no equivalent
        /// in-flight-download dedup for this one. ExecuteInstallAsync awaits this same task (if it
        /// still matches the current selection) instead of starting a second, colliding download.</summary>
        private void StartModdedVersionDownload(string version)
        {
            if (_cachedComponentService == null) return;
            if (string.Equals(_pendingModdedDownloadVersion, version, StringComparison.OrdinalIgnoreCase) &&
                _pendingModdedDownloadTask?.IsCompleted == false)
                return;

            var componentService = _cachedComponentService;
            _pendingModdedDownloadVersion = version;
            _pendingModdedDownloadTask = componentService.DownloadAndImportAmdWrapperVersionAsync(version);
            _pendingModdedDownloadTask.ContinueWith(t =>
            {
                if (t.IsFaulted)
                    DebugWindow.Log($"[SetupNr] Background download of Matheus wrapper v{version} failed: {t.Exception?.GetBaseException().Message}");
            }, TaskScheduler.Default);
        }

        /// <summary>Populates CmbDlssNrDanielVersion (danielblnc's own mod version) — shared by both NR
        /// modes, since both eventually run its installer via DlssNrOnAmdWizardWindow to generate the
        /// weights.</summary>
        private async Task PopulateDlssNrDanielVersionComboAsync()
        {
            var cmb = this.FindControl<ComboBox>("CmbDlssNrDanielVersion");
            if (cmb == null) return;

            cmb.SelectionChanged -= CmbDlssNrDanielVersion_SelectionChanged;
            cmb.Items.Clear();
            cmb.IsEnabled = false;

            List<DlssNrOnAmdRelease> releases;
            try
            {
                releases = await _dlssNrService.GetReleasesAsync();
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[SetupNr] Could not list danielblnc releases: {ex.Message}");
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
                    cmb.Items.Add(BuildVersionItem(releases[i].Version, isBeta: false, isLatest: i == 0));

                int selectedIndex = 0;
                if (!string.IsNullOrEmpty(_game.PendingDlssNrOnAmdVersion))
                {
                    for (int i = 0; i < cmb.Items.Count; i++)
                    {
                        if (cmb.Items[i] is ComboBoxItem cbi && string.Equals(cbi.Tag as string, _game.PendingDlssNrOnAmdVersion, StringComparison.OrdinalIgnoreCase))
                        {
                            selectedIndex = i;
                            break;
                        }
                    }
                }
                cmb.SelectedIndex = selectedIndex;
                ApplyDlssNrDanielVersionSelection(releases[selectedIndex].Version);
            }

            // Same AMD gate as CmbSetupNr (see IsSetupNrGpuAllowed) — this combo is repopulated (and
            // would otherwise unconditionally re-enable itself) every time "daniel-only"/"daniel-and-
            // opti" is (re)selected, including programmatically on window reopen, so the gate has to be
            // re-applied here rather than relying on a one-time check elsewhere.
            var danielVersionGpuOk = IsSetupNrGpuAllowed();
            cmb.IsEnabled = danielVersionGpuOk;
            ToolTip.SetTip(cmb, danielVersionGpuOk ? null : GetResourceString("TxtSetupNrRequiresAmdTooltip", "danielblnc's mod requires an AMD GPU."));
            cmb.SelectionChanged += CmbDlssNrDanielVersion_SelectionChanged;
        }

        /// <summary>danielblnc's mod only targets AMD GPUs — used to lock (not hide) CmbSetupNr and
        /// CmbDlssNrDanielVersion for everyone else, even with experimental features on. Permissive
        /// when detection itself is inconclusive (gpu == null): only a GPU we're sure isn't AMD locks
        /// these, matching how the rest of this window treats an unknown vendor elsewhere (e.g.
        /// ConfigureAdditionalComponents' NVIDIA/fakenvapi check defaults to enabled when unsure).</summary>
        private bool IsSetupNrGpuAllowed()
        {
            if (_gpuService == null) return true;
            var gpu = GpuSelectionHelper.GetPreferredGpu(_gpuService, new ComponentManagementService().Config.DefaultGpuId);
            return gpu == null || gpu.Vendor == GpuVendor.AMD;
        }

        private void CmbDlssNrDanielVersion_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            var version = ((sender as ComboBox)?.SelectedItem as ComboBoxItem)?.Tag as string;
            if (!string.IsNullOrEmpty(version)) ApplyDlssNrDanielVersionSelection(version);
        }

        private void ApplyDlssNrDanielVersionSelection(string version)
        {
            _game.PendingDlssNrOnAmdVersion = version;
            _ = _dlssNrService.DownloadAsync(version).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    DebugWindow.Log($"[SetupNr] Background download of danielblnc v{version} failed: {t.Exception?.GetBaseException().Message}");
            }, TaskScheduler.Default);
        }

        private void BtnUninstall_Click(object sender, RoutedEventArgs e)
        {
            var bdConfirmUninstall = this.FindControl<Grid>("BdConfirmUninstall");
            if (bdConfirmUninstall != null) bdConfirmUninstall.IsVisible = true;

            // This same button/modal also handles "Restore original DLL" (bare swap, no OptiScaler
            // — see UpdateStatus) and uninstalling danielblnc's mod-only install. Swap the copy to
            // match what's actually about to happen instead of always talking about uninstalling
            // OptiScaler. (An interrupted Setup NR run never reaches here — UpdateStatus's
            // CleanupOrphanedDanielModStage clears it silently before this button is ever clickable.)
            bool isDanielModOnlyInstalled = _game.IsDlssNrOnAmdInstalled && _game.InstalledDlssNrOnAmdMode == "daniel-only";
            bool isRestoreDllOnly = !isDanielModOnlyInstalled && !_game.IsOptiscalerInstalled && _game.IsFsr4DllSwapped;
            var txtTitle = this.FindControl<TextBlock>("TxtConfirmUninstallTitleBlock");
            var txtMsg = this.FindControl<TextBlock>("TxtConfirmUninstallMsgBlock");
            var btnYes = this.FindControl<Button>("BtnConfirmUninstallYes");
            if (isDanielModOnlyInstalled)
            {
                if (txtTitle != null) txtTitle.Text = GetResourceString("TxtSetupNrUninstallModConfirmTitle", "Uninstall mod?");
                if (txtMsg != null) txtMsg.Text = GetResourceString("TxtSetupNrUninstallModConfirmMsg", "Are you sure you want to uninstall danielblnc's mod?\nOnly backed-up original files will be restored.");
                if (btnYes != null) btnYes.Content = GetResourceString("TxtSetupNrUninstallModConfirmBtn", "✕ Uninstall mod");
            }
            else if (isRestoreDllOnly)
            {
                if (txtTitle != null) txtTitle.Text = GetResourceString("TxtConfirmRestoreDllTitle", "Confirm Restore");
                if (txtMsg != null) txtMsg.Text = GetResourceString("TxtConfirmRestoreDllMsg", "Are you sure you want to restore the original DLL?\nThe swapped FSR 4 Swap DLL will be replaced back with the backed-up original.");
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
        /// Shows the "which files to swap/copy" overlay when a package has more than one recognized
        /// file, and awaits the user's checked selection (Tag = target file name). Returns null if
        /// the user cancels, or the list of chosen target file names on confirm (never empty on
        /// confirm — the button only enables once at least one box is checked... actually it's left
        /// enabled and an empty confirm is treated as an implicit cancel by the caller).
        /// </summary>
        private Task<List<string>?> ShowFsr4SwapSelectionAsync(List<(string FileName, string EffectLabel, bool ExistsAtDestination)> options)
        {
            var panel = this.FindControl<StackPanel>("PnlFsr4SwapFileOptions");
            var overlay = this.FindControl<Grid>("BdFsr4SwapSelection");
            if (panel == null || overlay == null)
                return Task.FromResult<List<string>?>(options.Select(o => o.FileName).ToList());

            var textPrimary = Application.Current?.FindResource("BrTextPrimary") as IBrush ?? Brushes.White;
            var textSecondary = Application.Current?.FindResource("BrTextSecondary") as IBrush ?? Brushes.Gray;

            panel.Children.Clear();
            foreach (var opt in options)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(new TextBlock { Text = opt.EffectLabel, FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = textPrimary, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new TextBlock { Text = opt.FileName, FontSize = 11, Foreground = textSecondary, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Color.Parse(opt.ExistsAtDestination ? "#D97706" : "#16A34A")),
                    Padding = new Thickness(5, 1),
                    Child = new TextBlock
                    {
                        Text = opt.ExistsAtDestination ? "REPLACE" : "COPY",
                        FontSize = 10,
                        Foreground = Brushes.White,
                        FontWeight = Avalonia.Media.FontWeight.Bold,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                });

                panel.Children.Add(new CheckBox { IsChecked = true, Content = row, Tag = opt.FileName });
            }

            overlay.IsVisible = true;
            _fsr4SwapSelectionTcs = new TaskCompletionSource<List<string>?>();
            return _fsr4SwapSelectionTcs.Task;
        }

        private async void BtnFsr4SwapOpenSettings_Click(object? sender, PointerPressedEventArgs e)
        {
            try
            {
                var dialog = new ManageDefaultVersionsWindow(this, new ComponentManagementService());
                await dialog.ShowDialog<bool?>(this);
            }
            catch (Exception ex) { DebugWindow.Log($"[ManageGame] FSR 4 Swap Options dialog failed: {ex.Message}"); }
        }

        private void BtnFsr4SwapSelectionCancel_Click(object sender, RoutedEventArgs e)
        {
            var overlay = this.FindControl<Grid>("BdFsr4SwapSelection");
            if (overlay != null) overlay.IsVisible = false;
            _fsr4SwapSelectionTcs?.TrySetResult(null);
            _fsr4SwapSelectionTcs = null;
        }

        private void BtnFsr4SwapSelectionConfirm_Click(object sender, RoutedEventArgs e)
        {
            var overlay = this.FindControl<Grid>("BdFsr4SwapSelection");
            var panel = this.FindControl<StackPanel>("PnlFsr4SwapFileOptions");
            if (overlay != null) overlay.IsVisible = false;

            var selected = panel?.Children.OfType<CheckBox>()
                .Where(c => c.IsChecked == true)
                .Select(c => c.Tag as string)
                .Where(s => s != null)
                .Select(s => s!)
                .ToList() ?? new List<string>();

            _fsr4SwapSelectionTcs?.TrySetResult(selected);
            _fsr4SwapSelectionTcs = null;
        }

        /// <summary>
        /// The whole point of "Opti = None + Extras = version" mode: replace/copy the FSR4 file(s)
        /// already sitting in (or missing from) the game folder with the selected build, without
        /// touching OptiScaler, the profile, injection method, or any other selected component. Backs
        /// originals up through the same external store InstallOptiScaler/UninstallOptiScaler use, so a
        /// later Uninstall/"Restore original DLL" reverts it — see context/plans/fsr4_dll_swap_plan.md.
        /// A package containing more than one recognized file (FidelityFX SDK 2.0+ split-effect DLLs)
        /// prompts the user to choose which of them to swap/copy.
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

                // Null only in auto mode when nothing was found to replace — filled in below with
                // the downloaded package's own filename once we know it, so we never rename a DLL
                // to some other known name; we just place it under whatever name it actually has.
                string? targetPath;
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
                    // If none of the known names exist yet, there's nothing to "swap" against — the
                    // copy still happens below, under whatever filename the downloaded package itself
                    // uses (never a forced/renamed one), tracked as a created file so uninstall
                    // deletes it instead of trying to "restore" an original that never existed.
                    targetPath = Fsr4Int8DllHelper.FindSwapTargetIn(gameDir,
                        componentService.GetExtrasDllVariant(extrasVersion) == Fsr4DllVariant.Int8);
                }

                if (btnInstall != null) btnInstall.IsEnabled = false;
                if (btnInstallManual != null) btnInstallManual.IsEnabled = false;
                Dispatcher.UIThread.Post(() =>
                {
                    if (bdProgress != null) bdProgress.IsVisible = true;
                    if (txtProgressState != null) txtProgressState.Text = $"Downloading FSR4 v{extrasVersion}...";
                    if (prgDownload != null) prgDownload.IsIndeterminate = false;
                });

                // The RDNA2 companion (amdxc64.dll) has its own separate source and can be absent
                // for a given version — everything else comes from the regular Extras package,
                // regardless of how the target was found (auto or manual). targetPath is only null
                // here for "nothing found" auto mode, which is never the RDNA2 companion case (that
                // requires an existing amdxc64.dll to have been found).
                var targetFileName = targetPath != null ? System.IO.Path.GetFileName(targetPath) : null;
                List<(string TargetPath, string SourceContentPath)> filesToSwap;

                if (targetFileName != null && string.Equals(targetFileName, Fsr4Int8DllHelper.CustomRdna2FileName, StringComparison.OrdinalIgnoreCase))
                {
                    if (componentService.GetExtrasDllVariant(extrasVersion) != Fsr4DllVariant.Int8)
                        throw new InvalidOperationException("amdxc64.dll can only be replaced with an INT8 package.");
                    var rdna2Path = componentService.GetCachedCustomAmdxc64Path(extrasVersion);
                    if (rdna2Path == null)
                    {
                        Dispatcher.UIThread.Post(() => { if (bdProgress != null) bdProgress.IsVisible = false; });
                        await new ConfirmDialog(this, GetResourceString("TxtError", "Error"),
                            GetResourceString("TxtSwapDllNoRdna2Companion",
                                "This FSR 4 Swap version doesn't include a replacement for amdxc64.dll. Pick a different version or target file.")
                        ).ShowDialog<object>(this);
                        return;
                    }
                    filesToSwap = new() { (targetPath!, rdna2Path) };
                }
                else if (isManualMode)
                {
                    // Manual mode is about forcing ANY arbitrary existing file (any name) to be
                    // replaced with the package's content — not about picking among several packaged
                    // files by name — so it keeps its original single-file behavior unchanged.
                    var extrasProgress = new Progress<double>(p =>
                        Dispatcher.UIThread.Post(() => { if (prgDownload != null) prgDownload.Value = p; }));
                    var sourcePath = await componentService.DownloadExtrasDllAsync(extrasVersion, extrasProgress);
                    filesToSwap = new() { (targetPath!, sourcePath) };
                }
                else
                {
                    var extrasProgress = new Progress<double>(p =>
                        Dispatcher.UIThread.Post(() => { if (prgDownload != null) prgDownload.Value = p; }));
                    var packagedFiles = await componentService.GetExtrasPackagedFileNamesAsync(extrasVersion, extrasProgress);
                    if (packagedFiles.Count == 0)
                        throw new InvalidOperationException("This FSR4 version doesn't contain any recognized file.");

                    var cacheDir = componentService.GetExtrasDllCachePath(extrasVersion);
                    var candidates = Fsr4Int8DllHelper.BuildSwapCandidates(gameDir, cacheDir, packagedFiles);

                    if (candidates.Count > 1 && componentService.Config.Fsr4SwapAskEveryTime)
                    {
                        Dispatcher.UIThread.Post(() => { if (bdProgress != null) bdProgress.IsVisible = false; });
                        var options = candidates.Select(c => (
                            FileName: System.IO.Path.GetFileName(c.TargetPath),
                            EffectLabel: Fsr4Int8DllHelper.GetEffectDisplayName(System.IO.Path.GetFileName(c.SourceContentPath)),
                            ExistsAtDestination: File.Exists(c.TargetPath)
                        )).ToList();

                        var chosen = await ShowFsr4SwapSelectionAsync(options);
                        if (chosen == null || chosen.Count == 0) return; // Cancelled, or nothing checked

                        candidates = candidates
                            .Where(c => chosen.Contains(System.IO.Path.GetFileName(c.TargetPath), StringComparer.OrdinalIgnoreCase))
                            .ToList();
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (bdProgress != null) bdProgress.IsVisible = true;
                            if (prgDownload != null) prgDownload.IsIndeterminate = true;
                        });
                    }
                    else if (candidates.Count > 1)
                    {
                        // "Choose default values" configured in FSR 4 Swap Options — apply the
                        // pre-selected files silently instead of asking every time.
                        candidates = Fsr4Int8DllHelper.FilterCandidatesByDefaultKeys(candidates, componentService.Config.Fsr4SwapDefaultFileKeys);
                        if (candidates.Count == 0)
                        {
                            Dispatcher.UIThread.Post(() => { if (bdProgress != null) bdProgress.IsVisible = false; });
                            await new ConfirmDialog(this, GetResourceString("TxtError", "Error"),
                                GetResourceString("TxtFsr4SwapNoDefaultsConfigured",
                                    "No files are selected in FSR 4 Swap Options (Settings). Check at least one file there, or switch it back to \"Ask me every time\".")
                            ).ShowDialog<object>(this);
                            return;
                        }
                    }

                    filesToSwap = candidates;
                    targetFileName = string.Join(", ", candidates.Select(c => System.IO.Path.GetFileName(c.TargetPath)));
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (txtProgressState != null) txtProgressState.Text = "Swapping DLL...";
                    if (prgDownload != null) prgDownload.IsIndeterminate = true;
                });

                var swapResult = await Task.Run(() => installService.SwapFsr4Dll(_game, gameDir, filesToSwap, extrasVersion));

                NeedsScan = true;
                UpdateStatus();
                LoadComponents();

                Dispatcher.UIThread.Post(() => { if (bdProgress != null) bdProgress.IsVisible = false; });

                var successFormat = GetResourceString("TxtSwapDllSuccessFormat", "FSR4 v{0} swapped into {1}.");
                await ShowToastAsync(string.Format(successFormat, extrasVersion, string.Join(", ", swapResult.TargetFileNames)));
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

                // danielblnc's mod-only install: re-selecting "none" on CmbSetupNr is exactly the
                // existing uninstall flow (see CmbSetupNr_SelectionChanged's "none" case), so route
                // there instead of running the OptiScaler-specific UninstallOptiScaler below.
                if (_game.IsDlssNrOnAmdInstalled && _game.InstalledDlssNrOnAmdMode == "daniel-only")
                {
                    SelectCmbSetupNrTag("none");
                    return;
                }

                // Mode B (daniel-and-opti): the mod has its own manifest (gameDir + "::dlssnr", never
                // touched by UninstallOptiScaler below) — restore it first, while OptiScaler's manifest
                // still exists, so RestoreFromManifest's shared-file check (see DlssNrOnAmdService) can
                // still see it. Otherwise the mod's setup.exe/nvngx/weights/proxy DLL/logs are left
                // behind and IsDlssNrOnAmdInstalled never clears.
                if (_game.IsDlssNrOnAmdInstalled && _game.InstalledDlssNrOnAmdMode == "daniel-and-opti")
                {
                    var danielGameDir = ResolveDanielModGameDir();
                    if (danielGameDir != null) _dlssNrService.RestoreFromManifest(danielGameDir);
                    _game.IsDlssNrOnAmdInstalled = false;
                    _game.DlssNrOnAmdVersion = null;
                    _game.InstalledDlssNrOnAmdMode = null;
                }

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

        private static readonly TimeSpan ToastDuration = TimeSpan.FromMilliseconds(3500);
        private int _toastCallId;

        // Ticks PrgToastDuration.Value down by hand instead of relying on Avalonia's
        // property Transitions: repeatedly re-triggering a Transition on the same
        // property (this toast can fire several times per FG-settings session) left the
        // animation stuck on a later call even though the reset/hide logic itself was fine.
        // _toastCallId also supersedes any earlier still-running call so its tail end can't
        // fight over PrgToastDuration/BdToast with a newer toast.
        private async Task ShowToastAsync(string message)
        {
            var txtToastMessage = this.FindControl<TextBlock>("TxtToastMessage");
            var bdToast = this.FindControl<Border>("BdToast");
            var prgToastDuration = this.FindControl<ProgressBar>("PrgToastDuration");

            var myToastId = ++_toastCallId;

            Dispatcher.UIThread.Post(() =>
            {
                if (txtToastMessage != null) txtToastMessage.Text = message;
                if (bdToast != null) bdToast.IsVisible = true;
                if (prgToastDuration != null) prgToastDuration.Value = 100;
            });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < ToastDuration)
            {
                await Task.Delay(30);
                if (myToastId != _toastCallId) return;

                var remaining = 100.0 * (1 - sw.Elapsed.TotalMilliseconds / ToastDuration.TotalMilliseconds);
                Dispatcher.UIThread.Post(() =>
                {
                    if (prgToastDuration != null) prgToastDuration.Value = Math.Max(0, remaining);
                });
            }

            if (myToastId != _toastCallId) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (bdToast != null) bdToast.IsVisible = false;
            });
        }

        private void BtnToastClose_Click(object? sender, RoutedEventArgs e)
        {
            _toastCallId++; // invalidate the running tick loop so it doesn't re-show/hide over this
            var bdToast = this.FindControl<Border>("BdToast");
            if (bdToast != null) bdToast.IsVisible = false;
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
                    btnUninstall.IsEnabled = true;
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
                    btnUninstall.IsEnabled = true;
                    btnUninstall.Content = GetResourceString("TxtRestoreOriginalDll", "Restore original DLL");
                }
            }

            // Everything below overrides whichever labels/visibility the branches above (and these
            // three calls) just set — called here, before the daniel-mod-specific overrides, so those
            // always get the final say instead of being clobbered by RefreshInstallActionAvailability
            // re-showing a button a daniel-mod state had just hidden.
            UpdateInstallButtonsForSwapState();
            CaptureConfigOnlyBaseline();
            RefreshInstallActionAvailability();

            // danielblnc's installer is still sitting in the game folder from a Setup NR run that got
            // interrupted somewhere neither install path's own failure cleanup could see (e.g. the app
            // was killed mid-install) — both Install paths already clean up after themselves the moment
            // they detect failure (see ExecuteInstallAsync/RunDanielModAutoInstallAsync), so this is
            // only ever reached for state orphaned that way. Silently finish that cleanup here instead
            // of surfacing a dedicated "Cancel Setup NR install" button for the user to notice and
            // click — there's nothing to resume (the exe never means it's installed) and no reason to
            // ask, so recovery just happens automatically the next time this window opens.
            CleanupOrphanedDanielModStage();

            // danielblnc's mod-only mode locks every OptiScaler option control the moment it's selected
            // (see CmbSetupNr_SelectionChanged's "daniel-only" case), not just once fully installed —
            // it's directly incompatible with OptiScaler either way. Re-derive that same locked state
            // here (rather than only checking "fully installed") since UpdateStatus runs on every change
            // and would otherwise silently re-enable everything a still-pending selection had just
            // locked. Negated flag runs on every pass so it also restores everything once neither
            // pending nor installed anymore (e.g. right after uninstalling).
            bool danielModOnlyInstalled = _game.IsDlssNrOnAmdInstalled && _game.InstalledDlssNrOnAmdMode == "daniel-only";
            bool danielOnlyPending = !danielModOnlyInstalled && _game.PendingDlssNrOnAmdMode == "daniel-only";
            SetInstallOptionsEnabled(!(danielModOnlyInstalled || danielOnlyPending));

            if (danielOnlyPending)
            {
                // Not yet installed — Auto Install tries the headless/silent path first (see
                // ExecuteInstallAsync -> RunDanielModAutoInstallAsync); Manual Install always opens the
                // wizard the user finishes by hand. Reuse the same generic labels/resources the normal
                // OptiScaler install uses for these two buttons, for a consistent look.
                if (btnInstall != null)
                {
                    btnInstall.IsVisible = true;
                    btnInstall.IsEnabled = true;
                    btnInstall.Content = GetResourceString("TxtInstallOpti", "✦ Auto Install");
                }
                if (btnInstallManual != null)
                {
                    btnInstallManual.IsVisible = true;
                    btnInstallManual.IsEnabled = true;
                    btnInstallManual.Content = GetResourceString("TxtBtnManualInstall", "✦ Manual Install");
                }
                var cmbInstallActionDanielOnly = this.FindControl<Control>("CmbInstallAction");
                if (cmbInstallActionDanielOnly != null) cmbInstallActionDanielOnly.IsVisible = false;
                var cmbInstallActionManualDanielOnly = this.FindControl<Control>("CmbInstallActionManual");
                if (cmbInstallActionManualDanielOnly != null) cmbInstallActionManualDanielOnly.IsVisible = false;
            }

            if (danielModOnlyInstalled)
            {
                if (txtStatus != null) txtStatus.Text = GetResourceString("TxtSetupNrDanielModInstalled", "DLSS Neural Rendering (Mod) Installed");
                if (statusIndicator != null) statusIndicator.Fill = new SolidColorBrush(Color.FromRgb(118, 185, 0));
                if (txtVersion != null) txtVersion.Text = !string.IsNullOrEmpty(_game.DlssNrOnAmdVersion) ? $"v{_game.DlssNrOnAmdVersion}" : "";
                if (btnInstall != null) btnInstall.IsVisible = false;
                if (btnInstallManual != null) btnInstallManual.IsVisible = false;
                if (btnUninstall != null)
                {
                    btnUninstall.IsVisible = true;
                    btnUninstall.IsEnabled = true;
                    btnUninstall.Content = GetResourceString("TxtSetupNrUninstallModBtn", "Uninstall mod");
                }
            }
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

            if (_game.IsDlssNrOnAmdInstalled)
            {
                var nrLabel = GetResourceString("TxtDlssNrBadge", "DLSS Neural Rendering (AMD, experimental)");
                var nrModeSuffix = _game.InstalledDlssNrOnAmdMode == "daniel-only"
                    ? GetResourceString("TxtSetupNrModeDanielOnlySuffix", " (mod only)")
                    : GetResourceString("TxtSetupNrModeDanielAndOptiSuffix", " (+ OptiScaler)");
                var nrDisplay = (string.IsNullOrEmpty(_game.DlssNrOnAmdVersion)
                    ? nrLabel
                    : $"{nrLabel}: {_game.DlssNrOnAmdVersion}") + nrModeSuffix;
                components.Add(new ComponentEntry(nrDisplay, false, false,
                    GetResourceString("TxtDlssNrBadgeTooltip", "danielblnc/DLSS-NR-on-AMD via \"Setup NR\" — unofficial, experimental, at your own risk")));
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
                    components.Add(new ComponentEntry($"FSR 4 Swap: {_game.Fsr4ExtraVersion}", false, false, null));
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

            // "Modded" channel (Setup NR's Mod + OptiScaler mode) — start downloading/registering
            // whichever Matheus wrapper build was just picked, same idea as switching OptiScaler tabs
            // but resolving the registered Tag back to the raw version DownloadAndImportAmdWrapperVersionAsync needs.
            if (_optiShowingModded && !string.IsNullOrEmpty(selectedTag) &&
                _moddedVersionRawByRegisteredName.TryGetValue(selectedTag, out var rawModdedVersion))
            {
                StartModdedVersionDownload(rawModdedVersion);
            }

            if (!isBeta && !isNightly)
            {
                ConfigureAdditionalComponents();
            }

            UpdateInstallButtonsForSwapState();
            RefreshInstallActionAvailability();
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

            // Same reasoning as the identical guard in RefreshInstallActionAvailability: this method is
            // also called directly from every OptiVersion/Extras combo-change handler, not just through
            // UpdateStatus, and it derives Auto/Manual's IsEnabled/content from those two combos —
            // completely irrelevant while daniel-only is pending/installed (OptiScaler is locked out
            // entirely then), and would otherwise wrongly grey out or relabel the two Setup NR install
            // buttons. Defer to UpdateStatus's daniel-only block for their content/visibility instead.
            bool danielModOnlyInstalled = _game.IsDlssNrOnAmdInstalled && _game.InstalledDlssNrOnAmdMode == "daniel-only";
            bool danielOnlyPending = !danielModOnlyInstalled && _game.PendingDlssNrOnAmdMode == "daniel-only";
            if (danielModOnlyInstalled || danielOnlyPending) return;

            var cmbOptiVersion = this.FindControl<ComboBox>("CmbOptiVersion");
            var cmbExtrasVersion = this.FindControl<ComboBox>("CmbExtrasVersion");

            // This is the resync point called after every install/uninstall/swap completes (via
            // UpdateStatus) as well as on every combo change — ExecuteInstallAsync disables the
            // combo before its "extracting and installing" step but only re-enables it in the
            // download-phase finally blocks, not after that final step, so this is what actually
            // clears it back to usable once the operation is done.
            if (cmbOptiVersion != null) cmbOptiVersion.IsEnabled = true;
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
                // Opti=None + a FSR 4 Swap version selected → swap-only mode.
                btnInstall.IsEnabled = true;
                btnInstallManual.IsEnabled = true;
                btnInstall.Content = GetResourceString("TxtBtnAutoSwapDll", "✦ Auto-Swap DLL");
                btnInstallManual.Content = GetResourceString("TxtBtnManualSwapDll", "✦ Manual-Swap DLL");
            }
        }

        /// <summary>
        /// The six "hard" combo selections whose default pre-selection does NOT necessarily reflect
        /// what's actually installed for this game (e.g. CmbOptiVersion may default to "latest in
        /// channel" even when an older version is what's really on disk) — so eligibility for
        /// "Update config only" compares these against a session baseline (captured in
        /// CaptureConfigOnlyBaseline, right after the window last reflected a real install) instead of
        /// against the manifest. ExtrasVersion is normalized to null for "none"/empty.
        /// </summary>
        private sealed record HardInstallSelection(string OptiscalerVersion, string InjectionMethod,
            bool InstallFakenvapi, bool InstallNukemFG, bool InstallOptiPatcher, string? ExtrasVersion);

        /// <summary>Session baseline for HardInstallSelection — see CaptureConfigOnlyBaseline.</summary>
        private HardInstallSelection? _installedHardSelectionBaseline;

        private const string InstallActionSentinelTag = "sentinel";
        private const string InstallActionReinstallTag = "reinstall";
        private const string InstallActionUpdateConfigTag = "update_config";

        /// <summary>Null when in DLL-swap-only mode (CmbOptiVersion = "none") — not a real install.</summary>
        private HardInstallSelection? ReadCurrentHardInstallSelection()
        {
            var optiscalerVersion = (this.FindControl<ComboBox>("CmbOptiVersion")?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (string.IsNullOrEmpty(optiscalerVersion) || optiscalerVersion.Equals("none", StringComparison.OrdinalIgnoreCase))
                return null;

            var injectionMethod = (this.FindControl<ComboBox>("CmbInjectionMethod")?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "dxgi.dll";

            var fakenvapiTag = (this.FindControl<ComboBox>("CmbFakenvapiVersion")?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            bool installFakenvapi = !string.IsNullOrEmpty(fakenvapiTag) &&
                !fakenvapiTag.Equals("none", StringComparison.OrdinalIgnoreCase) && fakenvapiTag != "__manage__";

            var nukemTag = (this.FindControl<ComboBox>("CmbNukemFGVersion")?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            bool installNukemFG = !string.IsNullOrEmpty(nukemTag) &&
                !nukemTag.Equals("none", StringComparison.OrdinalIgnoreCase) && nukemTag != "__manage__";

            var optiPatcherTag = (this.FindControl<ComboBox>("CmbOptiPatcherVersion")?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            bool installOptiPatcher = !string.IsNullOrEmpty(optiPatcherTag) &&
                !optiPatcherTag.Equals("none", StringComparison.OrdinalIgnoreCase);

            var extrasTag = (this.FindControl<ComboBox>("CmbExtrasVersion")?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var normalizedExtras = string.IsNullOrEmpty(extrasTag) || extrasTag.Equals("none", StringComparison.OrdinalIgnoreCase)
                ? null : extrasTag;

            // RenoDX (CmbRenodxVersion) deliberately isn't part of this record: the combo only ever
            // rests on "Auto" (or the one specific cached file, which resolves to the same addon
            // Auto would anyway) — there's no real "changed selection" for it to miss the way there
            // is for a multi-version combo, so comparing it here would never contribute anything.

            return new HardInstallSelection(optiscalerVersion, injectionMethod, installFakenvapi, installNukemFG,
                installOptiPatcher, normalizedExtras);
        }

        /// <summary>
        /// Snapshots the six HardInstallSelection combos as "what's actually installed right now" —
        /// call this only from UpdateStatus (window load, and right after a real install/uninstall/swap
        /// completes), never from a combo's own SelectionChanged, or the baseline would always equal
        /// the current selection and defeat the whole point.
        /// </summary>
        private void CaptureConfigOnlyBaseline()
        {
            _installedHardSelectionBaseline = _game.IsOptiscalerInstalled ? ReadCurrentHardInstallSelection() : null;
            _installedSoftSelectionBaseline = _installedHardSelectionBaseline != null
                ? ReadCurrentSoftInstallSelection(_installedHardSelectionBaseline.OptiscalerVersion)
                : null;
        }

        /// <summary>
        /// The four config-only-patchable fields, plus the two real components a Frame Generation
        /// change can newly require (Streamline, DLSS Enabler — both need actual files on disk, not
        /// just an INI patch). Compared against a session baseline (_installedSoftSelectionBaseline),
        /// NOT the manifest — see CanApplyConfigOnly's remarks for why: AppliedAtUtc on these settings
        /// isn't persisted across app restarts, so on a fresh window open they can get silently reset to
        /// config defaults by Setup*Selector, which would almost never match the manifest's real values.
        /// </summary>
        private sealed record SoftInstallSelection(string? ProfileName, string? UpscalingQualityPreset,
            double? UpscalingQualityRatio, string? OutputUpscalerBackend, string? FrameGenRoute,
            string? FrameGenOutput, string? FrameGenMfgMode, bool InstallStreamline, bool InstallDlssEnabler);

        private SoftInstallSelection ReadCurrentSoftInstallSelection(string optiscalerVersion)
        {
            var profileName = (this.FindControl<ComboBox>("CmbProfile")?.SelectedItem as ComboBoxItem)?.Tag is OptiScalerProfile profile
                ? profile.Name : null;

            var qualitySettings = _game.UpscalingQualitySettings;
            var qualityEnabled = qualitySettings != null && qualitySettings.Preset != UpscalingQualityPreset.GameControlled;
            var upscalingQualityPreset = qualitySettings?.Preset.ToString();
            double? upscalingQualityRatio = qualityEnabled ? qualitySettings!.Ratio : null;
            var outputUpscalerBackend = _game.OutputUpscalerSettings?.Backend.ToString();
            var frameGenRoute = _game.FrameGenerationSettings?.Route.ToString();
            var frameGenOutput = _game.FrameGenerationSettings?.Output.ToString();
            var frameGenMfgMode = _game.FrameGenerationSettings?.MultiFrameMode.ToString();

            var componentService = new ComponentManagementService();
            var fgConfigService = new FrameGenerationConfigurationService();
            var gpu = GpuSelectionHelper.GetPreferredGpu(_gpuService, componentService.Config.DefaultGpuId);
            bool installStreamline = _game.FrameGenerationSettings != null &&
                _game.FrameGenerationSettings.Route != FrameGenerationRoute.Disabled &&
                fgConfigService.RequiresStreamline(_game.FrameGenerationSettings, fgConfigService.DetectCapabilities(_game, gpu), optiscalerVersion);
            bool installDlssEnabler = _game.FrameGenerationSettings?.Route != FrameGenerationRoute.Disabled &&
                _game.FrameGenerationSettings?.Output == FrameGenerationOutput.DlssG &&
                _game.FrameGenerationSettings?.NvngxReplacement is FrameGenerationNvngxReplacement.Arturs or FrameGenerationNvngxReplacement.Combo;

            return new SoftInstallSelection(profileName, upscalingQualityPreset, upscalingQualityRatio,
                outputUpscalerBackend, frameGenRoute, frameGenOutput, frameGenMfgMode, installStreamline, installDlssEnabler);
        }

        /// <summary>Session baseline for SoftInstallSelection — see CaptureConfigOnlyBaseline.</summary>
        private SoftInstallSelection? _installedSoftSelectionBaseline;

        /// <summary>
        /// True when Profile/Upscaling Quality/Output Upscaler/Frame Generation actually changed since
        /// the session baseline, and nothing else did — so a full reinstall would just redo work
        /// already on disk. Three things must hold: the six HardInstallSelection combos still match
        /// _installedHardSelectionBaseline; the Streamline/DLSS Enabler requirements (real components,
        /// not narrow INI patches) still match what the baseline had, so a Frame Generation change that
        /// newly needs one of them forces a full reinstall instead; and at least one of the four
        /// config-only fields actually differs from _installedSoftSelectionBaseline — if nothing changed
        /// at all, there's nothing to offer, and the button must behave like a normal Install/Reinstall.
        /// </summary>
        private bool ComputeConfigOnlyEligible()
        {
            if (_installedHardSelectionBaseline == null || _installedSoftSelectionBaseline == null) return false;

            var currentHard = ReadCurrentHardInstallSelection();
            if (currentHard == null || currentHard != _installedHardSelectionBaseline) return false;

            var currentSoft = ReadCurrentSoftInstallSelection(currentHard.OptiscalerVersion);
            var baseline = _installedSoftSelectionBaseline;

            if (currentSoft.InstallStreamline != baseline.InstallStreamline
                || currentSoft.InstallDlssEnabler != baseline.InstallDlssEnabler)
                return false;

            bool anythingChanged =
                !string.Equals(currentSoft.ProfileName, baseline.ProfileName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(currentSoft.UpscalingQualityPreset, baseline.UpscalingQualityPreset, StringComparison.OrdinalIgnoreCase)
                || currentSoft.UpscalingQualityRatio != baseline.UpscalingQualityRatio
                || !string.Equals(currentSoft.OutputUpscalerBackend, baseline.OutputUpscalerBackend, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(currentSoft.FrameGenRoute, baseline.FrameGenRoute, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(currentSoft.FrameGenOutput, baseline.FrameGenOutput, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(currentSoft.FrameGenMfgMode, baseline.FrameGenMfgMode, StringComparison.OrdinalIgnoreCase);

            if (!anythingChanged) return false;

            return new GameInstallationService().CanApplyConfigOnly(_game);
        }

        private void PopulateInstallActionCombos()
        {
            PopulateInstallActionCombo(this.FindControl<ComboBox>("CmbInstallAction"),
                GetResourceString("TxtUpdateOpti", "↑ Auto Update / Reinstall"));
            PopulateInstallActionCombo(this.FindControl<ComboBox>("CmbInstallActionManual"),
                GetResourceString("TxtUpdateOptiManual", "↑ Manual Update / Reinstall"));
        }

        private void PopulateInstallActionCombo(ComboBox? combo, string sentinelLabel)
        {
            if (combo == null || combo.Items.Count > 0) return;

            // No sentinel item — the label is shown via PlaceholderText (XAML) when nothing
            // is selected (SelectedIndex = -1). The two real action items follow directly.
            // Plain ComboBoxItems (no ActionOption class / badge) so they read as normal
            // options rather than styled "action buttons".
            combo.Items.Add(new ComboBoxItem
            {
                Content = GetResourceString("TxtReinstallMenuOption", "↻ Reinstall"),
                Tag = InstallActionReinstallTag
            });
            combo.Items.Add(new ComboBoxItem
            {
                Content = GetResourceString("TxtUpdateConfigOnly", "⚙ Update config only"),
                Tag = InstallActionUpdateConfigTag
            });
            combo.SelectedIndex = -1;
        }

        /// <summary>
        /// Swaps BtnInstall/BtnInstallManual for CmbInstallAction/CmbInstallActionManual (and back)
        /// depending on eligibility. Called after every Profile/Upscaling Quality/Output
        /// Upscaler/Frame Generation staging change, plus the OptiScaler-version and Extras combos (the
        /// two "hard" selectors that already had a SelectionChanged hook). The combo handlers recheck
        /// eligibility themselves before applying anything, so this being stale (e.g. from touching
        /// Fakenvapi/NukemFG/OptiPatcher/Injection Method, which aren't hooked here) can never cause a
        /// wrong partial update — worst case it shows an error and points at Reinstall instead.
        /// </summary>
        private void RefreshInstallActionAvailability()
        {
            // danielblnc's mod-only mode has its own Install/Uninstall button state (single collapsed
            // "Install" button while pending, hidden entirely once installed — see UpdateStatus). This
            // function is also called on its own after the window's combos repopulate (e.g.
            // PopulateVersionSelectors), which used to run after UpdateStatus's daniel-only override
            // and re-split the collapsed button back into Auto/Manual. Bail out here instead so every
            // caller defers to UpdateStatus's daniel-only handling regardless of call order.
            bool danielModOnlyInstalled = _game.IsDlssNrOnAmdInstalled && _game.InstalledDlssNrOnAmdMode == "daniel-only";
            bool danielOnlyPending = !danielModOnlyInstalled && _game.PendingDlssNrOnAmdMode == "daniel-only";
            if (danielModOnlyInstalled || danielOnlyPending) return;

            var btnInstall = this.FindControl<Button>("BtnInstall");
            var btnInstallManual = this.FindControl<Button>("BtnInstallManual");
            var cmbInstallAction = this.FindControl<ComboBox>("CmbInstallAction");
            var cmbInstallActionManual = this.FindControl<ComboBox>("CmbInstallActionManual");

            var eligible = ComputeConfigOnlyEligible();

            if (btnInstall != null) btnInstall.IsVisible = !eligible;
            if (btnInstallManual != null) btnInstallManual.IsVisible = !eligible;

            if (cmbInstallAction != null)
            {
                cmbInstallAction.IsVisible = eligible;
                if (eligible) cmbInstallAction.SelectedIndex = -1;
            }
            if (cmbInstallActionManual != null)
            {
                cmbInstallActionManual.IsVisible = eligible;
                if (eligible) cmbInstallActionManual.SelectedIndex = -1;
            }
        }

        private async void CmbInstallAction_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox combo || combo.SelectedItem is not ComboBoxItem item) return;
            await HandleInstallActionSelectionAsync(combo, item.Tag as string, isManualMode: false);
        }

        private async void CmbInstallActionManual_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox combo || combo.SelectedItem is not ComboBoxItem item) return;
            await HandleInstallActionSelectionAsync(combo, item.Tag as string, isManualMode: true);
        }

        private async Task HandleInstallActionSelectionAsync(ComboBox combo, string? tag, bool isManualMode)
        {
            if (tag != InstallActionReinstallTag && tag != InstallActionUpdateConfigTag)
                return; // the sentinel placeholder itself — not a real choice

            // Snap back to the placeholder immediately so the combo doesn't sit showing "Reinstall" or
            // "Update config only" as if that were now the persisted selection while the action runs.
            combo.SelectedIndex = -1;
            combo.IsEnabled = false;
            try
            {
                if (tag == InstallActionReinstallTag)
                {
                    try { await ExecuteInstallAsync(isManualMode); }
                    catch (Exception ex) { DebugWindow.Log($"[ManageGame] Install failed: {ex.Message}"); }
                }
                else
                {
                    await ExecuteConfigOnlyUpdateAsync();
                }
            }
            finally
            {
                combo.IsEnabled = true;
            }
        }

        private async Task ExecuteConfigOnlyUpdateAsync()
        {
            if (!ComputeConfigOnlyEligible())
            {
                RefreshInstallActionAvailability();
                await ShowToastAsync(GetResourceString("TxtConfigOnlyNoLongerAvailable",
                    "Something else changed — use Install/Reinstall instead."));
                return;
            }

            try
            {
                var installService = new GameInstallationService();
                var componentService = new ComponentManagementService();
                var gpu = GpuSelectionHelper.GetPreferredGpu(_gpuService, componentService.Config.DefaultGpuId);

                var cmbProfile = this.FindControl<ComboBox>("CmbProfile");
                if (cmbProfile?.SelectedItem is ComboBoxItem profileItem && profileItem.Tag is OptiScalerProfile profile)
                    await Task.Run(() => installService.ApplyProfileSettings(_game, profile));

                if (_game.FrameGenerationSettings != null)
                    await Task.Run(() => installService.ApplyFrameGenerationSettings(_game, gpu: gpu));
                if (_game.UpscalingQualitySettings != null)
                    await Task.Run(() => installService.ApplyUpscalingQualitySettings(_game));
                if (_game.OutputUpscalerSettings != null)
                    await Task.Run(() => installService.ApplyOutputUpscalerSettings(_game));

                NeedsScan = true;
                UpdateStatus();
                await ShowToastAsync(GetResourceString("TxtConfigOnlyUpdateApplied", "Configuration updated."));
            }
            catch (Exception ex)
            {
                await new ConfirmDialog(this, GetResourceString("TxtError", "Error"),
                    $"{GetResourceString("TxtConfigOnlyUpdateError", "Could not update the configuration:")}\n{ex.Message}")
                    .ShowDialog<object>(this);
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
            if (IsVersionGreaterOrEqual(selectedOptiTag, 0, 9) ||
                string.Equals(selectedOptiTag, "none", StringComparison.OrdinalIgnoreCase))
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
