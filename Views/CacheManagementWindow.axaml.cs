using System;
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
        private string _currentSection = "opti-stable";
        private string? _selectedVersion;
        private Button? _setDefaultButton;
        private GamepadDialogNavigationHelper? _gamepadHelper;

        GamepadHelperBase? IGamepadInputHost.GamepadHelper => _gamepadHelper;

        public CacheManagementWindow()
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
            _componentService = new ComponentManagementService();
        }

        public CacheManagementWindow(Window owner)
            : this(owner, "opti-stable")
        {
        }

        public CacheManagementWindow(string initialSection)
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
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

        public CacheManagementWindow(Window owner, string initialSection = "opti-stable")
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
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

            // ── OptiScaler (expandable) ──────────────────────────────────────
            var optiContainer = new StackPanel();

            var optiButton = CreateCategoryButton("\uF2A3", "\uEB8C", "OptiScaler");
            var expandIconTb = (optiButton.Content as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault();

            var optiChildren = new StackPanel { Margin = new Thickness(20, 0, 0, 0), IsVisible = true };
            optiChildren.Children.Add(CreateSubButton("opti-stable", "Stable", "\uE78F"));
            optiChildren.Children.Add(CreateSubButton("opti-beta",   "Beta",   "\uE206"));
            optiChildren.Children.Add(CreateSubButton("opti-nightly", "Nightly", "\uE945"));
            optiChildren.Children.Add(CreateSubButton("opti-custom", "Custom", "\uF41D"));

            optiButton.Click += (s, e) =>
            {
                optiChildren.IsVisible = !optiChildren.IsVisible;
                if (expandIconTb != null)
                    expandIconTb.Text = optiChildren.IsVisible ? "\uF2A3" : "\uF2B6";
            };

            optiContainer.Children.Add(optiButton);
            optiContainer.Children.Add(optiChildren);
            sidebar.Children.Add(optiContainer);

            // ── OptiPatcher ──────────────────────────────────────────────────
            sidebar.Children.Add(CreateTopButton("optipatcher", "OptiPatcher", "\uE8D7"));

            // ── FSR 4 DLL ───────────────────────────────────────────────────
            sidebar.Children.Add(CreateTopButton("fsr4",      "FSR 4 DLL", "\uE726"));

            // ── fakenvapi ────────────────────────────────────────────────────
            sidebar.Children.Add(CreateTopButton("fakenvapi",  "fakenvapi", "\uF193"));

            // ── nukemfg ──────────────────────────────────────────────────────
            sidebar.Children.Add(CreateTopButton("nukemfg",   "nukemfg",   "\uE619"));

            ShowSection("opti-stable");
            UpdateSidebarSelection("opti-stable");
        }

        private Button CreateCategoryButton(string expandIcon, string icon, string label)
        {
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 10),
                Margin = new Thickness(0, 0, 0, 4),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand)
            };

            btn.Styles.Add(new Style(x => x.OfType<Button>().Class(":pointerover"))
            {
                Setters = { new Setter(Button.BackgroundProperty, Brushes.Transparent) }
            });
            btn.Styles.Add(new Style(x => x.OfType<Button>().Class(":pressed"))
            {
                Setters = { new Setter(Button.BackgroundProperty, Brushes.Transparent) }
            });

            var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

            stack.Children.Add(new TextBlock
            {
                Text = expandIcon,
                FontFamily = IconFont,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = this.FindResource("BrTextSecondary") as IBrush
            });
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
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = this.FindResource("BrTextSecondary") as IBrush
            });

            btn.Content = stack;
            return btn;
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

        private Button CreateSubButton(string sectionId, string label, string icon)
        {
            var btn = new Button
            {
                Tag = sectionId,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 8),
                Margin = new Thickness(0, 0, 0, 2),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6)
            };

            var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            stack.Children.Add(new TextBlock
            {
                Text = icon,
                FontFamily = IconFont,
                FontSize = 13,
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
                {
                    StyleBtn(topBtn);
                }
                else if (child is StackPanel cat)
                {
                    foreach (var catChild in cat.Children)
                    {
                        if (catChild is StackPanel subContainer)
                            foreach (var sub in subContainer.Children.OfType<Button>())
                                StyleBtn(sub);
                    }
                }
            }
        }

        // ── Content rendering ─────────────────────────────────────────────────

        private void ShowSection(string sectionId)
        {
            bool sectionChanged = _currentSection != sectionId;
            _currentSection = sectionId;
            if (sectionChanged) _selectedVersion = null;

            var content = this.FindControl<StackPanel>("CacheContentArea");
            if (content == null) return;

            content.Children.Clear();

            switch (sectionId)
            {
                case "opti-stable": RenderOptiScalerVersions(content, showBeta: false); break;
                case "opti-beta":   RenderOptiScalerVersions(content, showBeta: true);  break;
                case "opti-nightly":RenderOptiScalerVersions(content, showBeta: false, showNightly: true); break;
                case "opti-custom": RenderOptiScalerCustom(content); break;
                case "optipatcher": RenderOptiPatcher(content); break;
                case "fsr4":        RenderFsr4(content); break;
                case "fakenvapi":   RenderFakenvapi(content); break;
                case "nukemfg":     RenderNukemfg(content); break;
            }
        }

        private void RenderOptiScalerVersions(StackPanel content, bool showBeta, bool showNightly = false)
        {
            content.Children.Add(CreateSetDefaultRow());

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
            content.Children.Add(CreateSetDefaultRow());

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
            content.Children.Add(CreateSetDefaultRow());

            var versions = _componentService.GetDownloadedOptiPatcherVersions();

            if (versions.Count == 0)
            {
                content.Children.Add(MakeEmptyLabel("No versions cached."));
                return;
            }

            foreach (var ver in versions)
                content.Children.Add(CreateVersionCard(ver, isExtras: false, isOptiPatcher: true));
        }

        private void RenderFsr4(StackPanel content)
        {
            content.Children.Add(CreateSetDefaultRow());

            // Add DLL button row
            var importRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 16) };
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

            var versions = _componentService.GetDownloadedExtrasVersions();

            if (versions.Count == 0)
            {
                content.Children.Add(MakeEmptyLabel("No versions cached."));
                return;
            }

            foreach (var ver in versions)
                content.Children.Add(CreateVersionCard(ver, isExtras: true));
        }

        private void RenderFakenvapi(StackPanel content)
        {
            content.Children.Add(CreateSetDefaultRow());

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
            content.Children.Add(CreateSetDefaultRow());

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

        // ── Version card ──────────────────────────────────────────────────────

        private static bool IsOptiSection(string sectionId) =>
            sectionId is "opti-stable" or "opti-beta" or "opti-nightly" or "opti-custom";

        private string? GetCurrentDefault()
        {
            return _currentSection switch
            {
                "opti-stable" or "opti-beta" or "opti-nightly" or "opti-custom" => _componentService.Config.DefaultOptiScalerVersion,
                "optipatcher" => _componentService.Config.DefaultOptiPatcherVersion,
                "fsr4" => _componentService.Config.DefaultExtrasVersion,
                "fakenvapi" => _componentService.Config.DefaultFakenvapiVersion,
                "nukemfg" => _componentService.Config.DefaultNukemFGVersion,
                _ => null
            };
        }

        private Border CreateVersionCard(string version, bool isExtras, bool isDeletable = true, bool isOptiPatcher = false, bool isNukemFG = false, bool isFakenvapi = false)
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

            // Show DEFAULT badge if this version is the configured default
            // (suppressed for OptiScaler while auto-latest is active — the pinned value is ignored then)
            var currentDefault = GetCurrentDefault();
            bool autoLatestActive = IsOptiSection(_currentSection) && _componentService.Config.AutoLatestOptiScalerDefault;
            if (!autoLatestActive && !string.IsNullOrEmpty(currentDefault) &&
                currentDefault.Equals(version, StringComparison.OrdinalIgnoreCase))
            {
                stack.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Color.Parse("#7C3AED")),
                    Padding = new Thickness(5, 1),
                    Margin = new Thickness(0, 2, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock
                    {
                        Text = Application.Current?.FindResource("TxtDefaultBadge") as string ?? "DEFAULT",
                        FontSize = 9,
                        Foreground = Brushes.White,
                        FontWeight = FontWeight.Bold,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                });
            }

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
                    Tag = new VersionDeleteInfo { Version = version, IsExtras = isExtras, IsOptiPatcher = isOptiPatcher, IsNukemFG = isNukemFG, IsFakenvapi = isFakenvapi }
                };
                btnDelete.Classes.Add("BtnSecondary");
                btnDelete.Click += BtnDelete_Click;
                grid.Children.Add(btnDelete);
                Grid.SetColumn(btnDelete, 2);
            }

            var border = new Border
            {
                Background = this.FindResource("BrBgCard") as IBrush ?? Brushes.Transparent,
                BorderBrush = (_selectedVersion == version)
                    ? this.FindResource("BrAccent") as IBrush ?? Brushes.DeepSkyBlue
                    : this.FindResource("BrBorderSubtle") as IBrush ?? Brushes.DimGray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 10),
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = version,
                Child = grid
            };

            border.PointerPressed += (s, e) =>
            {
                _selectedVersion = version;
                if (_setDefaultButton != null) _setDefaultButton.IsEnabled = true;
                ShowSection(_currentSection); // re-render to update highlight
            };

            return border;
        }

        private static Border CreateFsr4VariantBadge(Fsr4DllVariant variant) => new()
        {
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.Parse(variant == Fsr4DllVariant.Fp8 ? "#2563EB" : "#16A34A")),
            Padding = new Thickness(5, 1),
            Child = new TextBlock
            {
                Text = variant == Fsr4DllVariant.Fp8 ? "FP8" : "INT8",
                FontSize = 9,
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold
            }
        };

        /// <summary>
        /// Creates the "Set Default" header block shown at the top of each section.
        /// For the OptiScaler sections, also includes the "always use latest available version" toggle.
        /// </summary>
        private Control CreateSetDefaultRow()
        {
            bool isOptiSection = IsOptiSection(_currentSection);
            bool autoLatest = isOptiSection && _componentService.Config.AutoLatestOptiScalerDefault;

            var container = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 12) };

            if (isOptiSection)
            {
                var toggleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*, Auto") };

                var toggleLabel = new TextBlock
                {
                    Text = Application.Current?.FindResource("TxtAutoLatestOptiDefault") as string
                           ?? "Always use latest available version as default",
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = this.FindResource("BrTextSecondary") as IBrush
                };
                toggleRow.Children.Add(toggleLabel);
                Grid.SetColumn(toggleLabel, 0);

                var tglAutoLatest = new ToggleSwitch
                {
                    OnContent = "",
                    OffContent = "",
                    IsChecked = autoLatest,
                    Focusable = false,
                    VerticalAlignment = VerticalAlignment.Center
                };
                tglAutoLatest.IsCheckedChanged += TglAutoLatestOpti_IsCheckedChanged;
                toggleRow.Children.Add(tglAutoLatest);
                Grid.SetColumn(tglAutoLatest, 1);

                container.Children.Add(toggleRow);
            }

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*, Auto") };

            var currentDefault = GetCurrentDefault();
            var defaultLabel = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = this.FindResource("BrTextSecondary") as IBrush
            };

            if (autoLatest)
            {
                var latest = _componentService.LatestStableVersion;
                var fmt = Application.Current?.FindResource("TxtAutoLatestCurrentFormat") as string ?? "Auto: latest available (v{0})";
                defaultLabel.Text = string.IsNullOrEmpty(latest)
                    ? (Application.Current?.FindResource("TxtNoDefaultSet") as string ?? "No default set")
                    : string.Format(fmt, latest);
            }
            else if (!string.IsNullOrEmpty(currentDefault) &&
                !currentDefault.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                var fmt = Application.Current?.FindResource("TxtCurrentDefaultFormat") as string ?? "Current default: {0}";
                defaultLabel.Text = string.Format(fmt, currentDefault);
            }
            else
            {
                defaultLabel.Text = Application.Current?.FindResource("TxtNoDefaultSet") as string ?? "No default set";
            }

            row.Children.Add(defaultLabel);
            Grid.SetColumn(defaultLabel, 0);

            var btnStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

            // Clear default button
            var btnClear = new Button
            {
                Content = Application.Current?.FindResource("TxtClearDefault") as string ?? "Clear",
                Padding = new Thickness(10, 5),
                FontSize = 11,
                IsEnabled = !autoLatest &&
                            !string.IsNullOrEmpty(currentDefault) &&
                            !currentDefault.Equals("none", StringComparison.OrdinalIgnoreCase)
            };
            btnClear.Classes.Add("BtnSecondary");
            btnClear.Click += BtnClearDefault_Click;
            btnStack.Children.Add(btnClear);

            // Set Default button
            var btnSetDefault = new Button
            {
                Content = Application.Current?.FindResource("TxtSetDefault") as string ?? "Set Default",
                Padding = new Thickness(12, 5),
                FontSize = 11,
                IsEnabled = !autoLatest && _selectedVersion != null
            };
            btnSetDefault.Classes.Add("BtnBase");
            btnSetDefault.Click += BtnSetDefault_Click;
            _setDefaultButton = btnSetDefault;
            btnStack.Children.Add(btnSetDefault);

            row.Children.Add(btnStack);
            Grid.SetColumn(btnStack, 1);

            container.Children.Add(row);
            return container;
        }

        private void TglAutoLatestOpti_IsCheckedChanged(object? sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch tgl)
            {
                _componentService.Config.AutoLatestOptiScalerDefault = tgl.IsChecked ?? true;
                _componentService.SaveConfiguration();
                ShowSection(_currentSection);
            }
        }

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
            int total       = versions.Count + extras.Count + optiPatcher.Count + nukemfg.Count + fakenvapi.Count;
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

        // ── Set Default ────────────────────────────────────────────────────────

        private void BtnSetDefault_Click(object? sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedVersion)) return;

            switch (_currentSection)
            {
                case "opti-stable":
                case "opti-beta":
                case "opti-nightly":
                case "opti-custom":
                    _componentService.Config.DefaultOptiScalerVersion = _selectedVersion;
                    break;
                case "optipatcher":
                    _componentService.Config.DefaultOptiPatcherVersion = _selectedVersion;
                    break;
                case "fsr4":
                    _componentService.Config.DefaultExtrasVersion = _selectedVersion;
                    break;
                case "fakenvapi":
                    _componentService.Config.DefaultFakenvapiVersion = _selectedVersion;
                    break;
                case "nukemfg":
                    _componentService.Config.DefaultNukemFGVersion = _selectedVersion;
                    break;
            }

            _componentService.SaveConfiguration();
            _selectedVersion = null;
            ShowSection(_currentSection);
        }

        private void BtnClearDefault_Click(object? sender, RoutedEventArgs e)
        {
            switch (_currentSection)
            {
                case "opti-stable":
                case "opti-beta":
                case "opti-nightly":
                case "opti-custom":
                    _componentService.Config.DefaultOptiScalerVersion = null;
                    break;
                case "optipatcher":
                    _componentService.Config.DefaultOptiPatcherVersion = null;
                    break;
                case "fsr4":
                    _componentService.Config.DefaultExtrasVersion = null;
                    break;
                case "fakenvapi":
                    _componentService.Config.DefaultFakenvapiVersion = null;
                    break;
                case "nukemfg":
                    _componentService.Config.DefaultNukemFGVersion = null;
                    break;
            }

            _componentService.SaveConfiguration();
            _selectedVersion = null;
            ShowSection(_currentSection);
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

                ShowSection("opti-custom");
                UpdateSidebarSelection("opti-custom");
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
