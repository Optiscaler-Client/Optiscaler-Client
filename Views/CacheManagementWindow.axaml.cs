using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using OptiscalerClient.Helpers;
using OptiscalerClient.Models;
using OptiscalerClient.Services;

namespace OptiscalerClient.Views
{
    public partial class CacheManagementWindow : Window, IGamepadInputHost
    {
        private static readonly FontFamily IconFont = new("avares://OptiscalerClient/assets/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular");
        private readonly ComponentManagementService _componentService;
        private bool _isAnimatingClose;
        private string _currentSection = "opti";
        private string _currentOptiTab = "opti-stable";
        private string _currentFsr4Tab     = "fsr4-mirror"; // "fsr4-mirror" | "fsr4-custom"
        private string _currentFsr4Variant = "fsr4-int8";   // "fsr4-int8"   | "fsr4-fp8"
        private string _currentDlssTab     = "dlss-mirror"; // "dlss-mirror" | "dlss-custom"
        private GamepadDialogNavigationHelper? _gamepadHelper;

        GamepadHelperBase? IGamepadInputHost.GamepadHelper => _gamepadHelper;

        public CacheManagementWindow()
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
            _componentService = new ComponentManagementService();
        }

        public CacheManagementWindow(Window owner)
            : this(owner, "opti")
        {
        }

        public CacheManagementWindow(string initialSection)
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
            WindowScreenFitHelper.FitToScreen(this);
            _componentService = new ComponentManagementService();
            _currentSection = initialSection;

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
                    _gamepadHelper = new GamepadDialogNavigationHelper(this, this.FindControl<ScrollViewer>("CacheContentScrollViewer"));
                    _gamepadHelper.CustomNavigationHandler = OnGamepadNavigation;
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

            BuildSidebar();
            ShowSection(_currentSection);
            UpdateSidebarSelection(_currentSection);
            UpdateCacheInfo();
        }

        private void OnGamepadModeActiveChanged(object? sender, bool isGamepadModeActive)
        {
            var txtX = this.FindControl<TextBlock>("TxtCloseIconX");
            var badgeB = this.FindControl<Border>("BadgeCloseGamepadB");
            if (txtX != null) txtX.IsVisible = !isGamepadModeActive;
            if (badgeB != null) badgeB.IsVisible = isGamepadModeActive;
        }

        private bool OnGamepadNavigation(GamepadButton button)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            var focused = topLevel?.FocusManager?.GetFocusedElement() as Visual;
            if (focused == null) return false;

            var sidebar = this.FindControl<StackPanel>("CacheSidebar");
            var contentArea = this.FindControl<StackPanel>("CacheContentArea");

            bool isInSidebar = sidebar != null && focused.GetVisualAncestors().Contains(sidebar);
            bool isInContent = contentArea != null && focused.GetVisualAncestors().Contains(contentArea);

            if (button == GamepadButton.DPadRight || button == GamepadButton.ThumbLeftRight)
            {
                if (isInSidebar && contentArea != null)
                {
                    var firstFocusable = contentArea.GetVisualDescendants().OfType<InputElement>().FirstOrDefault(x => x.Focusable && x.IsEffectivelyVisible);
                    if (firstFocusable != null)
                    {
                        firstFocusable.Focus(NavigationMethod.Directional);
                        return true;
                    }
                }
            }
            else if (button == GamepadButton.DPadLeft || button == GamepadButton.ThumbLeftLeft)
            {
                if (isInContent && sidebar != null)
                {
                    // Focus the currently selected sidebar button, or just the first button
                    var selectedBtn = sidebar.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Tag as string == _currentSection);
                    if (selectedBtn != null)
                    {
                        selectedBtn.Focus(NavigationMethod.Directional);
                        return true;
                    }
                }
            }
            else if (button == GamepadButton.A)
            {
                if (isInSidebar && focused is Button btn)
                {
                    bool alreadyActive = btn.Tag as string == _currentSection;

                    btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                    // First 'A' on a section only switches to it, keeping
                    // focus in the sidebar (matches the rest of the app's
                    // sidebar convention). Only jump into content if this
                    // section was already the active one — same as DPadRight.
                    if (alreadyActive && contentArea != null)
                    {
                        var firstFocusable = contentArea.GetVisualDescendants().OfType<InputElement>().FirstOrDefault(x => x.Focusable && x.IsEffectivelyVisible);
                        firstFocusable?.Focus(NavigationMethod.Directional);
                    }
                    return true;
                }
            }

            return false;
        }

        public CacheManagementWindow(Window owner, string initialSection = "opti")
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
            WindowScreenFitHelper.FitToScreen(this);
            _componentService = new ComponentManagementService();
            _currentSection = initialSection;

            this.Opacity = 0;

            var titleBar = this.FindControl<Border>("TitleBar");
            if (titleBar != null)
                titleBar.PointerPressed += (s, e) => this.BeginMoveDrag(e);

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
                    _gamepadHelper = new GamepadDialogNavigationHelper(this, this.FindControl<ScrollViewer>("CacheContentScrollViewer"));
                    _gamepadHelper.CustomNavigationHandler = OnGamepadNavigation;
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

            BuildSidebar();
            ShowSection(_currentSection);
            UpdateSidebarSelection(_currentSection);
            UpdateCacheInfo();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        // ── Sidebar ──────────────────────────────────────────────────────────

        private void BuildSidebar()
        {
            var sidebar = this.FindControl<StackPanel>("CacheSidebar");
            if (sidebar == null) return;

            sidebar.Children.Clear();

            // ── OptiScaler (single entry — Stable/Beta/Nightly/Custom shown as tabs in content) ──
            sidebar.Children.Add(CreateTopButton("opti", "OptiScaler", "\uEB8C"));

            // ── OptiPatcher ──────────────────────────────────────────────────
            sidebar.Children.Add(CreateTopButton("optipatcher", "OptiPatcher", "\uE8D7"));

            // ── FSR 4 DLL ───────────────────────────────────────────────────
            sidebar.Children.Add(CreateTopButton("fsr4",      "FSR 4 DLL", "\uE726"));

            // ── fakenvapi ────────────────────────────────────────────────────
            sidebar.Children.Add(CreateTopButton("fakenvapi",  "fakenvapi", "\uF193"));

            // ── nukemfg ──────────────────────────────────────────────────────
            sidebar.Children.Add(CreateTopButton("nukemfg",   "nukemfg",   "\uE619"));

            // ── DLSS Enabler ─────────────────────────────────────────────────
            sidebar.Children.Add(CreateTopButton("dlss-enabler",
                Application.Current?.FindResource("TxtDlssEnabler") as string ?? "DLSS Enabler", "\uE9CE"));

            // ── Streamline (auto-downloaded, list + delete only) ─────────────
            sidebar.Children.Add(CreateTopButton("streamline",
                Application.Current?.FindResource("TxtStreamline") as string ?? "Streamline", "\uE945"));

            // Section rendering/selection is the caller's responsibility (each constructor calls
            // ShowSection(_currentSection)/UpdateSidebarSelection(_currentSection) right after
            // BuildSidebar()). Doing it here too used to clobber a requested initialSection other
            // than "opti", since ShowSection mutates _currentSection as a side effect.
        }



        private Button CreateTopButton(string sectionId, string label, string icon)
        {
            var btn = new Button
            {
                Tag = sectionId,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 10),
                Margin = new Thickness(0, 0, 0, 4),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6)
            };

            var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            stack.Children.Add(new TextBlock
            {
                Text = icon,
                FontFamily = IconFont,
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = this.FindResource("BrTextSecondary") as IBrush
            });
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = this.FindResource("BrTextSecondary") as IBrush
            });

            btn.Content = stack;
            btn.Click += (s, e) => { ShowSection(sectionId); UpdateSidebarSelection(sectionId); };
            return btn;
        }

        private void UpdateSidebarSelection(string sectionId)
        {
            _currentSection = sectionId;

            var sidebar = this.FindControl<StackPanel>("CacheSidebar");
            if (sidebar == null) return;

            var activeBg   = this.FindResource("BrBgCard")         as IBrush ?? Brushes.DimGray;
            var inactiveBg = Brushes.Transparent;
            var activeFg   = this.FindResource("BrTextPrimary")    as IBrush ?? Brushes.White;
            var inactiveFg = this.FindResource("BrTextSecondary")  as IBrush ?? Brushes.Gray;

            void StyleBtn(Button b)
            {
                bool active = b.Tag?.ToString() == sectionId;
                b.Background = active ? activeBg : inactiveBg;
                if (b.Content is StackPanel sp)
                    foreach (var tb in sp.Children.OfType<TextBlock>())
                        tb.Foreground = active ? activeFg : inactiveFg;
            }

            foreach (var child in sidebar.Children)
            {
                if (child is Button topBtn)
                    StyleBtn(topBtn);
            }
        }


        // ── Content rendering ─────────────────────────────────────────────────

        private void ShowSection(string sectionId)
        {
            _currentSection = sectionId;

            var content = this.FindControl<StackPanel>("CacheContentArea");
            if (content == null) return;

            content.Children.Clear();

            switch (sectionId)
            {
                case "opti":        RenderOptiScalerWithTabs(content); break;
                case "optipatcher": RenderOptiPatcher(content); break;
                case "fsr4":        RenderFsr4WithTabs(content); break;
                case "fakenvapi":   RenderFakenvapi(content); break;
                case "nukemfg":     RenderNukemfg(content); break;
                case "dlss-enabler": RenderDlssEnablerWithTabs(content); break;
                case "streamline":  RenderStreamline(content); break;
            }
        }

        // ── OptiScaler unified view with tabs ────────────────────────────────

        private void RenderOptiScalerWithTabs(StackPanel content)
        {
            // ── Tab bar ──────────────────────────────────────────────────────
            var tabGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*,*,*"),
                Margin = new Thickness(0, 0, 0, 16)
            };

            var tabs = new[] { "opti-stable", "opti-beta", "opti-nightly", "opti-custom" };
            var tabLabels = new[] { "Stable", "Beta", "Nightly", "Custom" };
            var tabButtons = new Button[tabs.Length];

            for (int i = 0; i < tabs.Length; i++)
            {
                int idx = i; // capture for lambda
                var btn = new Button
                {
                    Content = tabLabels[idx],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    FontSize = 11,
                    Padding = new Thickness(8, 5),
                    Margin = new Thickness(idx == 0 ? 0 : 2, 0, idx == tabs.Length - 1 ? 0 : 2, 0)
                };
                btn.Classes.Add(_currentOptiTab == tabs[idx] ? "BtnPrimary" : "BtnSecondary");
                tabButtons[idx] = btn;

                btn.Click += (s, e) =>
                {
                    _currentOptiTab = tabs[idx];
                    // Re-render the whole opti section so tabs update
                    ShowSection("opti");
                };

                Grid.SetColumn(btn, i);
                tabGrid.Children.Add(btn);
            }

            content.Children.Add(tabGrid);

            // ── Tab content ──────────────────────────────────────────────────
            switch (_currentOptiTab)
            {
                case "opti-stable":
                    RenderOptiScalerVersions(content, showBeta: false);
                    break;
                case "opti-beta":
                    RenderOptiScalerVersions(content, showBeta: true);
                    break;
                case "opti-nightly":
                    RenderOptiScalerVersions(content, showBeta: false, showNightly: true);
                    break;
                case "opti-custom":
                    RenderOptiScalerCustom(content);
                    break;
            }
        }

        private void RenderOptiScalerVersions(StackPanel content, bool showBeta, bool showNightly = false)
        {
            var allVersions = _componentService.GetDownloadedOptiScalerVersions();
            var betaSet     = _componentService.BetaVersions;
            var nightlySet  = _componentService.NightlyVersions;
            var customSet   = _componentService.CustomVersions;

            var filtered = allVersions.Where(v =>
            {
                if (customSet.Contains(v)) return false;
                return betaSet.Contains(v) == showBeta && nightlySet.Contains(v) == showNightly;
            }).ToList();

            if (filtered.Count == 0)
            {
                content.Children.Add(MakeEmptyLabel("No versions cached."));
                return;
            }

            foreach (var ver in filtered)
                content.Children.Add(CreateVersionCard(ver, isExtras: false));
        }


        private void RenderOptiScalerCustom(StackPanel content)
        {
            // Import button
            var importRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 16) };
            var txtStatus = new TextBlock
            {
                Name = "TxtImportStatus",
                FontSize = 11,
                Foreground = this.FindResource("BrAccent") as IBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            var btnImport = new Button
            {
                Name = "BtnImportCustom",
                Content = Application.Current?.FindResource("TxtImportArchive") as string ?? "Import Archive",
                Padding = new Thickness(12, 5),
                FontSize = 11
            };
            btnImport.Classes.Add("BtnBase");
            btnImport.Click += BtnImportCustom_Click;

            importRow.Children.Add(txtStatus);
            Grid.SetColumn(txtStatus, 1);
            importRow.Children.Add(btnImport);
            Grid.SetColumn(btnImport, 2);
            content.Children.Add(importRow);

            // Warning banner
            content.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#1AFF9800")),
                BorderBrush = new SolidColorBrush(Color.Parse("#FF9800")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8),
                Margin = new Thickness(0, 0, 0, 12),
                Child = new TextBlock
                {
                    Text = Application.Current?.FindResource("TxtCustomVersionWarning") as string ?? "",
                    Foreground = new SolidColorBrush(Color.Parse("#FF9800")),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                }
            });

            var customSet    = _componentService.CustomVersions;
            var allDownloaded = _componentService.GetDownloadedOptiScalerVersions();
            var filtered     = allDownloaded.Where(v => customSet.Contains(v)).ToList();

            if (filtered.Count == 0)
            {
                content.Children.Add(MakeEmptyLabel(
                    Application.Current?.FindResource("TxtNoCustomVersions") as string
                    ?? "No custom versions imported."));
                return;
            }

            foreach (var ver in filtered)
                content.Children.Add(CreateVersionCard(ver, isExtras: false));
        }

        private void RenderOptiPatcher(StackPanel content)
        {
            var versions = _componentService.GetDownloadedOptiPatcherVersions();

            if (versions.Count == 0)
            {
                content.Children.Add(MakeEmptyLabel("No versions cached."));
                return;
            }

            foreach (var ver in versions)
                content.Children.Add(CreateVersionCard(ver, isExtras: false, isOptiPatcher: true));
        }


        private void RenderFsr4WithTabs(StackPanel content)
        {
            // ── Tab bar row 1: Mirror | Custom ───────────────────────────────
            var sourceTabGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var sourceTabs   = new[] { "fsr4-mirror", "fsr4-custom" };
            var sourceLabels = new[]
            {
                Application.Current?.FindResource("TxtTabMirror") as string ?? "Mirror",
                Application.Current?.FindResource("TxtTabCustom") as string ?? "Custom"
            };

            for (int i = 0; i < sourceTabs.Length; i++)
            {
                int idx = i;
                var btn = new Button
                {
                    Content = sourceLabels[idx],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    FontSize = 11,
                    Padding = new Thickness(8, 5),
                    Margin = new Thickness(idx == 0 ? 0 : 2, 0, idx == sourceTabs.Length - 1 ? 0 : 2, 0)
                };
                btn.Classes.Add(_currentFsr4Tab == sourceTabs[idx] ? "BtnPrimary" : "BtnSecondary");
                btn.Click += (s, e) =>
                {
                    _currentFsr4Tab = sourceTabs[idx];
                    ShowSection("fsr4");
                };
                Grid.SetColumn(btn, i);
                sourceTabGrid.Children.Add(btn);
            }
            content.Children.Add(sourceTabGrid);

            // ── Tab bar row 2: INT8 | FP8 ────────────────────────────────────
            var variantTabGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                Margin = new Thickness(0, 0, 0, 16)
            };

            var variantTabs   = new[] { "fsr4-int8", "fsr4-fp8" };
            var variantLabels = new[] { "INT8", "FP8" };

            for (int i = 0; i < variantTabs.Length; i++)
            {
                int idx = i;
                var btn = new Button
                {
                    Content = variantLabels[idx],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    FontSize = 11,
                    Padding = new Thickness(8, 5),
                    Margin = new Thickness(idx == 0 ? 0 : 2, 0, idx == variantTabs.Length - 1 ? 0 : 2, 0)
                };
                btn.Classes.Add(_currentFsr4Variant == variantTabs[idx] ? "BtnPrimary" : "BtnSecondary");
                btn.Click += (s, e) =>
                {
                    _currentFsr4Variant = variantTabs[idx];
                    ShowSection("fsr4");
                };
                Grid.SetColumn(btn, i);
                variantTabGrid.Children.Add(btn);
            }
            content.Children.Add(variantTabGrid);

            // ── Custom-only controls ──────────────────────────────────────────
            if (_currentFsr4Tab == "fsr4-custom")
            {
                // Add DLL button row
                var importRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
                var btnImport = new Button
                {
                    Name = "BtnImportCustomExtras",
                    Content = Application.Current?.FindResource("TxtAddDll") as string ?? "Add DLL",
                    Padding = new Thickness(12, 5),
                    FontSize = 11
                };
                btnImport.Classes.Add("BtnBase");
                btnImport.Click += BtnImportCustomExtras_Click;

                importRow.Children.Add(btnImport);
                Grid.SetColumn(btnImport, 1);
                content.Children.Add(importRow);

                // Info banner
                content.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#1A42A5F5")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#42A5F5")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8),
                    Margin = new Thickness(0, 0, 0, 12),
                    Child = new TextBlock
                    {
                        Text = Application.Current?.FindResource("TxtAddFsr4DllDesc") as string
                            ?? "Add a .zip/.7z/.rar package (or a single .dll) and select whether it contains an INT8 or FP8 FSR 4 model.",
                        Foreground = new SolidColorBrush(Color.Parse("#42A5F5")),
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap
                    }
                });
            }

            // ── Version list ──────────────────────────────────────────────────
            var targetVariant = _currentFsr4Variant == "fsr4-fp8" ? Fsr4DllVariant.Fp8 : Fsr4DllVariant.Int8;
            var customSet     = _componentService.CustomExtrasVersions;
            var allVersions   = _componentService.GetDownloadedExtrasVersions();

            List<string> filtered;
            string emptyKey;
            if (_currentFsr4Tab == "fsr4-mirror")
            {
                filtered = allVersions
                    .Where(v => !customSet.Contains(v) && _componentService.GetExtrasDllVariant(v) == targetVariant)
                    .ToList();
                emptyKey = targetVariant == Fsr4DllVariant.Int8 ? "TxtFsr4NoMirrorInt8" : "TxtFsr4NoMirrorFp8";
            }
            else
            {
                filtered = allVersions
                    .Where(v => customSet.Contains(v) && _componentService.GetExtrasDllVariant(v) == targetVariant)
                    .ToList();
                emptyKey = targetVariant == Fsr4DllVariant.Int8 ? "TxtFsr4NoCustomInt8" : "TxtFsr4NoCustomFp8";
            }

            if (filtered.Count == 0)
            {
                var fallbackMap = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["TxtFsr4NoMirrorInt8"] = "No INT8 mirror versions cached.",
                    ["TxtFsr4NoMirrorFp8"]  = "No FP8 mirror versions cached.",
                    ["TxtFsr4NoCustomInt8"] = "No custom INT8 versions imported.",
                    ["TxtFsr4NoCustomFp8"]  = "No custom FP8 versions imported."
                };
                content.Children.Add(MakeEmptyLabel(
                    Application.Current?.FindResource(emptyKey) as string ?? fallbackMap[emptyKey]));
                return;
            }

            foreach (var ver in filtered)
                content.Children.Add(CreateVersionCard(ver, isExtras: true));
        }


        private void RenderFakenvapi(StackPanel content)
        {
            var downloadedVersions = _componentService.GetDownloadedFakenvapiVersions();

            if (downloadedVersions.Count == 0)
            {
                content.Children.Add(MakeEmptyLabel("No Fakenvapi versions cached."));
                return;
            }

            foreach (var ver in downloadedVersions)
                content.Children.Add(CreateVersionCard(ver, isExtras: false, isDeletable: true, isOptiPatcher: false, isNukemFG: false, isFakenvapi: true));
        }

        private void RenderNukemfg(StackPanel content)
        {
            // Import archive button row
            var importRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 16) };
            var btnImport = new Button
            {
                Name = "BtnImportNukemFG",
                Content = Application.Current?.FindResource("TxtImportArchive") as string ?? "Import Archive",
                Padding = new Thickness(12, 5),
                FontSize = 11
            };
            btnImport.Classes.Add("BtnBase");
            btnImport.Click += BtnImportNukemFG_Click;

            importRow.Children.Add(btnImport);
            Grid.SetColumn(btnImport, 1);
            content.Children.Add(importRow);

            // Info banner
            content.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#1A42A5F5")),
                BorderBrush = new SolidColorBrush(Color.Parse("#42A5F5")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8),
                Margin = new Thickness(0, 0, 0, 12),
                Child = new TextBlock
                {
                    Text = "NukemFG versions are imported from .zip archives containing dlssg_to_fsr3_amd_is_better.dll.",
                    Foreground = new SolidColorBrush(Color.Parse("#42A5F5")),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                }
            });

            // Version list
            var nukemVersions = _componentService.GetDownloadedNukemFGVersions();
            if (nukemVersions.Count == 0)
            {
                content.Children.Add(MakeEmptyLabel("No NukemFG versions cached."));
            }
            else
            {
                foreach (var ver in nukemVersions)
                    content.Children.Add(CreateVersionCard(ver, isExtras: false, isDeletable: true, isOptiPatcher: false, isNukemFG: true));
            }
        }

        private void RenderDlssEnablerWithTabs(StackPanel content)
        {
            // ── Tab bar: Mirror | Custom ──────────────────────────────────────
            var tabGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                Margin = new Thickness(0, 0, 0, 16)
            };

            var tabs   = new[] { "dlss-mirror", "dlss-custom" };
            var labels = new[]
            {
                Application.Current?.FindResource("TxtTabMirror") as string ?? "Mirror",
                Application.Current?.FindResource("TxtTabCustom") as string ?? "Custom"
            };

            for (int i = 0; i < tabs.Length; i++)
            {
                int idx = i;
                var btn = new Button
                {
                    Content = labels[idx],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    FontSize = 11,
                    Padding = new Thickness(8, 5),
                    Margin = new Thickness(idx == 0 ? 0 : 2, 0, idx == tabs.Length - 1 ? 0 : 2, 0)
                };
                btn.Classes.Add(_currentDlssTab == tabs[idx] ? "BtnPrimary" : "BtnSecondary");
                btn.Click += (s, e) =>
                {
                    _currentDlssTab = tabs[idx];
                    ShowSection("dlss-enabler");
                };
                Grid.SetColumn(btn, i);
                tabGrid.Children.Add(btn);
            }
            content.Children.Add(tabGrid);

            // ── Tab content ───────────────────────────────────────────────────
            if (_currentDlssTab == "dlss-mirror")
            {
                var mirrorVersions = _componentService.GetDownloadedDlssEnablerMirrorVersions();
                if (mirrorVersions.Count == 0)
                {
                    content.Children.Add(MakeEmptyLabel(
                        Application.Current?.FindResource("TxtDlssEnablerNoMirrorVersions") as string
                        ?? "No DLSS Enabler mirror versions cached."));
                }
                else
                {
                    foreach (var ver in mirrorVersions)
                        content.Children.Add(CreateVersionCard(ver, isExtras: false, isDeletable: true, isDlssEnablerMirror: true));
                }
            }
            else
            {
                // ── Custom: Import button + info banner + list ────────────────
                var importRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
                var btnImport = new Button
                {
                    Name = "BtnImportDlssEnabler",
                    Content = Application.Current?.FindResource("TxtImportArchive") as string ?? "Import Archive",
                    Padding = new Thickness(12, 5),
                    FontSize = 11
                };
                btnImport.Classes.Add("BtnBase");
                btnImport.Click += BtnImportDlssEnabler_Click;

                importRow.Children.Add(btnImport);
                Grid.SetColumn(btnImport, 1);
                content.Children.Add(importRow);

                content.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#1A42A5F5")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#42A5F5")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8),
                    Margin = new Thickness(0, 0, 0, 12),
                    Child = new TextBlock
                    {
                        Text = Application.Current?.FindResource("TxtDlssEnablerImportBanner") as string ??
                            "DLSS Enabler versions are imported from a .dll file or a .zip/.7z/.rar archive containing version.dll or dlss-enabler-headless.dll.",
                        Foreground = new SolidColorBrush(Color.Parse("#42A5F5")),
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap
                    }
                });

                var versions = _componentService.GetDownloadedDlssEnablerVersions();
                if (versions.Count == 0)
                {
                    content.Children.Add(MakeEmptyLabel(
                        Application.Current?.FindResource("TxtNoDlssEnablerVersions") as string
                        ?? "No DLSS Enabler versions cached."));
                }
                else
                {
                    foreach (var ver in versions)
                        content.Children.Add(CreateVersionCard(ver, isExtras: false, isDeletable: true, isDlssEnabler: true));
                }
            }
        }


        private void RenderStreamline(StackPanel content)
        {
            var versions = _componentService.GetDownloadedStreamlineVersions();
            if (versions.Count == 0)
            {
                content.Children.Add(MakeEmptyLabel(Application.Current?.FindResource("TxtNoStreamlineVersions") as string ?? "No Streamline SDK versions cached."));
                return;
            }

            foreach (var ver in versions)
                content.Children.Add(CreateVersionCard(ver, isExtras: false, isDeletable: true, isStreamline: true));
        }

        // ── Version card ──────────────────────────────────────────────────────

        private Border CreateVersionCard(string version, bool isExtras, bool isDeletable = true, bool isOptiPatcher = false, bool isNukemFG = false, bool isFakenvapi = false, bool isDlssEnabler = false, bool isStreamline = false, bool isDlssEnablerMirror = false)
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*, Auto, Auto"),
                VerticalAlignment = VerticalAlignment.Center
            };

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            titleRow.Children.Add(new TextBlock
            {
                Text = isExtras ? _componentService.GetExtrasDllDisplayName(version) : version,
                FontWeight = FontWeight.Bold,
                Foreground = this.FindResource("BrTextPrimary") as IBrush ?? Brushes.White
            });
            if (isExtras)
                titleRow.Children.Add(CreateFsr4VariantBadge(_componentService.GetExtrasDllVariant(version)));
            stack.Children.Add(titleRow);

            // Show "Currently selected" label if this is the installed OptiScaler version
            // if (!isExtras && !isOptiPatcher && !isNukemFG && !isFakenvapi && version == _componentService.OptiScalerVersion)
            // {
            //     stack.Children.Add(new TextBlock
            //     {
            //         Text = Application.Current?.FindResource("TxtCurrentSelection") as string ?? "Currently selected",
            //         FontSize = 10,
            //         Foreground = this.FindResource("BrAccent") as IBrush ?? Brushes.DeepSkyBlue
            //     });
            // }

            grid.Children.Add(stack);
            Grid.SetColumn(stack, 0);

            if (isDeletable)
            {
                var btnDelete = new Button
                {
                    Content = Application.Current?.FindResource("TxtDeletePlain") as string ?? "Delete",
                    Padding = new Thickness(12, 4),
                    FontSize = 11,
                    Margin = new Thickness(8, 0, 0, 0),
                    Tag = new VersionDeleteInfo { Version = version, IsExtras = isExtras, IsOptiPatcher = isOptiPatcher, IsNukemFG = isNukemFG, IsFakenvapi = isFakenvapi, IsDlssEnabler = isDlssEnabler, IsStreamline = isStreamline, IsDlssEnablerMirror = isDlssEnablerMirror }
                };
                btnDelete.Classes.Add("BtnSecondary");
                btnDelete.Click += BtnDelete_Click;
                grid.Children.Add(btnDelete);
                Grid.SetColumn(btnDelete, 2);
            }

            var border = new Border
            {
                Background = this.FindResource("BrBgCard") as IBrush ?? Brushes.Transparent,
                BorderBrush = this.FindResource("BrBorderSubtle") as IBrush ?? Brushes.DimGray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 10),
                Tag = version,
                Child = grid
            };

            return border;
        }

        private static Border CreateFsr4VariantBadge(Fsr4DllVariant variant) => new()
        {
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.Parse(variant == Fsr4DllVariant.Fp8 ? "#2563EB" : "#0EA5E9")),
            Padding = new Thickness(5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = variant == Fsr4DllVariant.Fp8 ? "FP8" : "INT8",
                FontSize = 9,
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center
            }
        };


        private TextBlock MakeEmptyLabel(string text) => new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = this.FindResource("BrTextSecondary") as IBrush,
            Margin = new Thickness(0, 8, 0, 0)
        };

        private void UpdateCacheInfo()
        {
            var txtCacheInfo = this.FindControl<TextBlock>("TxtCacheInfo");
            if (txtCacheInfo == null) return;

            var versions    = _componentService.GetDownloadedOptiScalerVersions();
            var extras      = _componentService.GetDownloadedExtrasVersions();
            var optiPatcher = _componentService.GetDownloadedOptiPatcherVersions();
            var nukemfg     = _componentService.GetDownloadedNukemFGVersions();
            var fakenvapi   = _componentService.GetDownloadedFakenvapiVersions();
            var dlssEnabler = _componentService.GetDownloadedDlssEnablerVersions();
            var streamline  = _componentService.GetDownloadedStreamlineVersions();
            int total       = versions.Count + extras.Count + optiPatcher.Count + nukemfg.Count + fakenvapi.Count + dlssEnabler.Count + streamline.Count;
            txtCacheInfo.Text = $"{total} items cached locally.";
        }

        // ── Delete ─────────────────────────────────────────────────────────────

        private class VersionDeleteInfo
        {
            public string Version { get; set; } = "";
            public bool IsExtras { get; set; }
            public bool IsOptiPatcher { get; set; }
            public bool IsNukemFG { get; set; }
            public bool IsFakenvapi { get; set; }
            public bool IsDlssEnabler { get; set; }
            public bool IsStreamline { get; set; }
            public bool IsDlssEnablerMirror { get; set; }
        }

        private async void BtnDelete_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is VersionDeleteInfo info)
            {
                string title, msg;
                if (info.IsFakenvapi)
                {
                    title = "Delete Fakenvapi Version";
                    msg = $"Are you sure you want to delete Fakenvapi '{info.Version}' from cache?";
                }
                else if (info.IsNukemFG)
                {
                    title = "Delete NukemFG Version";
                    msg = $"Are you sure you want to delete NukemFG '{info.Version}' from cache?";
                }
                else if (info.IsDlssEnablerMirror)
                {
                    title = Application.Current?.FindResource("TxtDeleteDlssEnablerTitle") as string ?? "Delete DLSS Enabler Version";
                    msg = string.Format(Application.Current?.FindResource("TxtDeleteDlssEnablerMsgFormat") as string ?? "Are you sure you want to delete DLSS Enabler '{0}' from cache?", info.Version);
                }
                else if (info.IsDlssEnabler)
                {
                    title = Application.Current?.FindResource("TxtDeleteDlssEnablerTitle") as string ?? "Delete DLSS Enabler Version";
                    msg = string.Format(Application.Current?.FindResource("TxtDeleteDlssEnablerMsgFormat") as string ?? "Are you sure you want to delete DLSS Enabler '{0}' from cache?", info.Version);
                }
                else if (info.IsStreamline)
                {
                    title = Application.Current?.FindResource("TxtDeleteStreamlineTitle") as string ?? "Delete Streamline Version";
                    msg = string.Format(Application.Current?.FindResource("TxtDeleteStreamlineMsgFormat") as string ?? "Are you sure you want to delete Streamline '{0}' from cache?", info.Version);
                }
                else if (info.IsExtras)
                {
                    title = "Delete FSR4 Extra";
                    msg = $"Are you sure you want to delete FSR4 INT8 Extra {info.Version}?";
                }
                else
                {
                    title = "Delete OptiScaler Version";
                    msg = $"Are you sure you want to delete OptiScaler {info.Version} from cache?";
                }

                var dialog = new ConfirmDialog(this, title, msg, false);
                var result = await dialog.ShowDialog<bool>(this);

                if (result)
                {
                    try
                    {
                        if (info.IsFakenvapi)
                            _componentService.DeleteFakenvapiCache(info.Version);
                        else if (info.IsNukemFG)
                            _componentService.DeleteNukemFGCache(info.Version);
                        else if (info.IsDlssEnablerMirror)
                            _componentService.DeleteDlssEnablerMirrorCache(info.Version);
                        else if (info.IsDlssEnabler)
                            _componentService.DeleteDlssEnablerCache(info.Version);
                        else if (info.IsStreamline)
                            _componentService.DeleteStreamlineCache(info.Version);
                        else if (info.IsExtras)
                            _componentService.DeleteExtrasCache(info.Version);
                        else if (info.IsOptiPatcher)
                            _componentService.DeleteOptiPatcherCache(info.Version);
                        else
                            _componentService.DeleteOptiScalerCache(info.Version);

                        ShowSection(_currentSection);
                        UpdateCacheInfo();
                    }
                    catch (Exception ex)
                    {
                        await new ConfirmDialog(this, "Error", $"Failed to delete version: {ex.Message}").ShowDialog<object>(this);
                    }
                }
            }
        }


        // ── Import ─────────────────────────────────────────────────────────────

        private async void BtnImportCustom_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select OptiScaler Archive",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Archives (7z, zip, rar)")
                        {
                            Patterns = new[] { "*.7z", "*.zip", "*.rar" }
                        }
                    }
                });

                if (files == null || files.Count == 0) return;

                var filePath = files[0].Path.IsAbsoluteUri
                    ? files[0].Path.LocalPath
                    : files[0].TryGetLocalPath();
                if (string.IsNullOrEmpty(filePath)) return;

                var overlay   = this.FindControl<Grid>("OverlayImporting");
                if (overlay != null) overlay.IsVisible = true;
                if (sender is Button btnSender) btnSender.IsEnabled = false;

                var versionName = await _componentService.ImportCustomOptiScalerVersionAsync(filePath);
                DebugWindow.Log($"[Cache] Custom version imported: {versionName}");

                if (overlay != null) overlay.IsVisible = false;
                if (sender is Button btnSender2) btnSender2.IsEnabled = true;

                _currentOptiTab = "opti-custom";
                ShowSection("opti");
                UpdateSidebarSelection("opti");
                UpdateCacheInfo();
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[Cache] Import custom version failed: {ex}");
                var overlay = this.FindControl<Grid>("OverlayImporting");
                if (overlay != null) overlay.IsVisible = false;
                if (sender is Button btnSender) btnSender.IsEnabled = true;
                var innerMsg = ex.InnerException != null ? $"\n{ex.InnerException.Message}" : "";
                await new ConfirmDialog(this, "Import Error",
                    $"Failed to import custom version:\n{ex.Message}{innerMsg}").ShowDialog<object>(this);
            }
        }

        // ── Add FSR 4 DLL ─────────────────────────────────────────────────────

        private async void BtnImportCustomExtras_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var addDialog = new AddFsr4DllWindow(this);
                if (await addDialog.ShowDialog<bool>(this) != true || string.IsNullOrEmpty(addDialog.SelectedFilePath)) return;

                var overlay = this.FindControl<Grid>("OverlayImporting");
                if (overlay != null) overlay.IsVisible = true;
                if (sender is Button btnSender) btnSender.IsEnabled = false;

                var versionName = await _componentService.ImportCustomExtrasArchiveAsync(addDialog.SelectedFilePath, addDialog.SelectedVariant);
                DebugWindow.Log($"[Cache] Custom FSR 4 {addDialog.SelectedVariant} version imported: {versionName}");

                if (overlay != null) overlay.IsVisible = false;
                if (sender is Button btnSender2) btnSender2.IsEnabled = true;

                _currentFsr4Tab     = "fsr4-custom";
                _currentFsr4Variant = addDialog.SelectedVariant == Fsr4DllVariant.Fp8 ? "fsr4-fp8" : "fsr4-int8";
                ShowSection("fsr4");
                UpdateSidebarSelection("fsr4");
                UpdateCacheInfo();
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[Cache] Add FSR 4 DLL failed: {ex}");
                var overlay = this.FindControl<Grid>("OverlayImporting");
                if (overlay != null) overlay.IsVisible = false;
                if (sender is Button btnSender) btnSender.IsEnabled = true;
                var innerMsg = ex.InnerException != null ? $"\n{ex.InnerException.Message}" : "";
                await new ConfirmDialog(this, "Import Error",
                    $"Failed to add FSR 4 DLL package:\n{ex.Message}{innerMsg}").ShowDialog<object>(this);
            }
        }

        // ── Import NukemFG ─────────────────────────────────────────────────

        private async void BtnImportNukemFG_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select NukemFG Archive",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Archives (zip, 7z, rar)")
                        {
                            Patterns = new[] { "*.zip", "*.7z", "*.rar" }
                        }
                    }
                });

                if (files == null || files.Count == 0) return;

                var filePath = files[0].Path.IsAbsoluteUri
                    ? files[0].Path.LocalPath
                    : files[0].TryGetLocalPath();
                if (string.IsNullOrEmpty(filePath)) return;

                var overlay = this.FindControl<Grid>("OverlayImporting");
                if (overlay != null) overlay.IsVisible = true;
                if (sender is Button btnSender) btnSender.IsEnabled = false;

                var versionName = await _componentService.ImportNukemFGArchiveAsync(filePath);
                DebugWindow.Log($"[Cache] NukemFG version imported: {versionName}");

                if (overlay != null) overlay.IsVisible = false;
                if (sender is Button btnSender2) btnSender2.IsEnabled = true;

                ShowSection("nukemfg");
                UpdateSidebarSelection("nukemfg");
                UpdateCacheInfo();
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[Cache] Import NukemFG failed: {ex}");
                var overlay = this.FindControl<Grid>("OverlayImporting");
                if (overlay != null) overlay.IsVisible = false;
                if (sender is Button btnSender) btnSender.IsEnabled = true;
                var innerMsg = ex.InnerException != null ? $"\n{ex.InnerException.Message}" : "";
                await new ConfirmDialog(this, "Import Error",
                    $"Failed to import NukemFG version:\n{ex.Message}{innerMsg}").ShowDialog<object>(this);
            }
        }

        // ── Import DLSS Enabler ──────────────────────────────────────────────

        private async void BtnImportDlssEnabler_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var addDialog = new AddDlssEnablerWindow(this);
                if (await addDialog.ShowDialog<bool>(this) != true || string.IsNullOrEmpty(addDialog.SelectedFilePath) || string.IsNullOrEmpty(addDialog.SelectedName))
                    return;

                var overlay = this.FindControl<Grid>("OverlayImporting");
                if (overlay != null) overlay.IsVisible = true;
                if (sender is Button btnSender) btnSender.IsEnabled = false;

                var versionName = await _componentService.ImportDlssEnablerAsync(addDialog.SelectedFilePath, addDialog.SelectedName);
                DebugWindow.Log($"[Cache] DLSS Enabler version imported: {versionName}");

                if (overlay != null) overlay.IsVisible = false;
                if (sender is Button btnSender2) btnSender2.IsEnabled = true;

                _currentDlssTab = "dlss-custom";
                ShowSection("dlss-enabler");
                UpdateSidebarSelection("dlss-enabler");
                UpdateCacheInfo();
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[Cache] Import DLSS Enabler failed: {ex}");
                var overlay = this.FindControl<Grid>("OverlayImporting");
                if (overlay != null) overlay.IsVisible = false;
                if (sender is Button btnSender) btnSender.IsEnabled = true;
                var innerMsg = ex.InnerException != null ? $"\n{ex.InnerException.Message}" : "";
                await new ConfirmDialog(this, "Import Error",
                    $"Failed to import DLSS Enabler version:\n{ex.Message}{innerMsg}").ShowDialog<object>(this);
            }
        }

        // ── Close ──────────────────────────────────────────────────────────────

        private void BtnClose_Click(object? sender, RoutedEventArgs e) => _ = CloseAnimated();

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
    }
}
