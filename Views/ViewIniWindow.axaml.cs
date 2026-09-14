using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml;
using OptiscalerClient.Helpers;

namespace OptiscalerClient.Views
{
    /// <summary>Read-only viewer for an installed OptiScaler.ini — shows how the file actually
    /// landed after install/profile merge, grouped by [Section] with a text filter. Never writes
    /// anything back; purely for inspection next to the Profile selector's "view ini" eye icon.</summary>
    public partial class ViewIniWindow : Window
    {
        private sealed class IniRow
        {
            public Border RowBorder { get; set; } = null!;
            public TextBlock KeyBlock { get; set; } = null!;
            public Control ValueControl { get; set; } = null!;
            public string Section { get; set; } = "";
            public string Key { get; set; } = "";
            public string OriginalValue { get; set; } = "";
            public Func<string> ValueGetter { get; set; } = null!;
            public Action<string> ValueSetter { get; set; } = null!;
            public Button RestoreRowButton { get; set; } = null!;
        }

        private sealed record IniSectionUi(Border Card, TextBlock Header, string SectionName, List<IniRow> Rows);

        private const double BaseFontSize = 14; // slightly larger than the app's usual 12px body text, at 100% zoom
        private const int MinZoomPercent = 50;
        private const int MaxZoomPercent = 200;
        private const int ZoomStepPercent = 10;

        private readonly List<IniSectionUi> _sections = new();
        private TextBlock? _plainDumpBlock;
        private int _zoomPercent = 100;
        private double _fontSize = BaseFontSize;
        private string _searchMode = "term";

        private Models.Game? _game;
        private Models.OptiScalerProfile? _currentProfile;
        private Services.ProfileManagementService? _profileService;
        private Action<Models.OptiScalerProfile>? _onProfilePersisted;

        private readonly Dictionary<(string, string), Views.SchemaSetting> _schemaLookup;
        private readonly Helpers.KeybindCaptureController _keybindController = new();

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        public ViewIniWindow() 
        {
            InitializeComponent(); 
            _schemaLookup = new Dictionary<(string, string), Views.SchemaSetting>();
        }

        public ViewIniWindow(string iniPath, Models.Game game, Models.OptiScalerProfile currentProfile, Services.ProfileManagementService profileService, Action<Models.OptiScalerProfile> onProfilePersisted)
        {
            InitializeComponent();
            _game = game;
            _currentProfile = currentProfile;
            _profileService = profileService;
            _onProfilePersisted = onProfilePersisted;
            
            _schemaLookup = SchemaSettingControlFactory.LoadCanonicalSchemaLookup();

            DialogDimHelper.Register(this);
            WindowScreenFitHelper.FitToScreen(this);
            SetupWindow(game.Name);
            PopulateSearchModeCombo();
            BuildContent(iniPath);
            SetupFooter();
        }

        private void SetupWindow(string gameName)
        {
            this.Opacity = 0;

            var subtitle = this.FindControl<TextBlock>("TxtSubtitle");
            if (subtitle != null) subtitle.Text = gameName;

            var titleBar = this.FindControl<Border>("TitleBar");
            if (titleBar != null) titleBar.PointerPressed += (s, e) => this.BeginMoveDrag(e);

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

            this.KeyDown += _keybindController.HandleKeyDown;
        }

        private void SetupFooter()
        {
            var cmbSave = this.FindControl<ComboBox>("CmbSave");
            if (cmbSave != null)
            {
                cmbSave.Items.Clear();
                
                var overrideItem = new ComboBoxItem 
                { 
                    Content = GetResourceString("TxtViewIniSaveOverride", "Override current profile"), 
                    Tag = "override" 
                };
                
                if (_currentProfile != null && _currentProfile.IsBuiltIn)
                {
                    overrideItem.IsEnabled = false;
                    ToolTip.SetTip(overrideItem, GetResourceString("TxtViewIniSaveOverrideDisabledTooltip", "Cannot overwrite built-in profile"));
                }
                
                cmbSave.Items.Add(overrideItem);
                cmbSave.Items.Add(new ComboBoxItem 
                { 
                    Content = GetResourceString("TxtViewIniSaveAsNew", "As new profile"), 
                    Tag = "as_new" 
                });
                
                cmbSave.SelectedIndex = -1;
            }
            
            UpdateSaveButtonState();
        }

        private void BtnClose_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

        private static IBrush ResBrush(string key, IBrush fallback)
            => Application.Current?.FindResource(key) as IBrush ?? fallback;

        private static string GetResourceString(string key, string fallback)
            => Application.Current?.TryFindResource(key, out var res) == true && res is string str ? str : fallback;

        private void PopulateSearchModeCombo()
        {
            var cmb = this.FindControl<ComboBox>("CmbSearchMode");
            if (cmb == null) return;

            cmb.Items.Add(new ComboBoxItem { Content = GetResourceString("TxtViewIniSearchByTerm", "By term"), Tag = "term" });
            cmb.Items.Add(new ComboBoxItem { Content = GetResourceString("TxtViewIniSearchBySection", "By section"), Tag = "section" });
            cmb.SelectedIndex = 0;
            cmb.SelectionChanged += CmbSearchMode_SelectionChanged;
        }

        private void BuildContent(string iniPath)
        {
            var panel = this.FindControl<StackPanel>("PanelSections");
            if (panel == null) return;

            string[] lines;
            try { lines = File.ReadAllLines(iniPath); }
            catch (Exception ex)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"Couldn't read {iniPath}:\n{ex.Message}",
                    Foreground = ResBrush("BrTextSecondary", Brushes.Gray),
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            var parsedSections = ParseIni(lines);
            if (parsedSections.Count == 0)
            {
                _plainDumpBlock = new TextBlock
                {
                    Text = string.Join("\n", lines),
                    FontFamily = new FontFamily("Consolas, Monospace"),
                    FontSize = _fontSize,
                    Foreground = ResBrush("BrTextPrimary", Brushes.White),
                    TextWrapping = TextWrapping.Wrap
                };
                panel.Children.Add(_plainDumpBlock);
                return;
            }

            foreach (var (section, rows) in parsedSections)
            {
                var sectionUi = BuildSectionCard(section, rows);
                panel.Children.Add(sectionUi.Card);
                _sections.Add(sectionUi);
            }
        }

        private static List<(string Section, List<(string Key, string Value)> Rows)> ParseIni(string[] lines)
        {
            var result = new List<(string, List<(string, string)>)>();
            var currentSection = "";
            List<(string, string)>? currentRows = null;

            void Flush()
            {
                if (currentRows is { Count: > 0 }) result.Add((currentSection, currentRows));
            }

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    Flush();
                    currentSection = line[1..^1];
                    currentRows = new List<(string, string)>();
                    continue;
                }

                var idx = line.IndexOf('=');
                if (idx < 0 || currentRows == null) continue;
                currentRows.Add((line[..idx].Trim(), line[(idx + 1)..].Trim()));
            }
            Flush();
            return result;
        }

        private void MarkRowDirty(IniRow row)
        {
            var currentVal = row.ValueGetter();
            if (currentVal != row.OriginalValue)
            {
                row.RestoreRowButton.IsVisible = true;
                row.RowBorder.Background = ResBrush("BrAccentSubtle", Brushes.DarkSlateBlue);
            }
            else
            {
                row.RestoreRowButton.IsVisible = false;
                row.RowBorder.Background = Brushes.Transparent;
            }
            UpdateSaveButtonState();
        }

        private void UpdateSaveButtonState()
        {
            bool anyDirty = _sections.SelectMany(s => s.Rows).Any(r => r.ValueGetter() != r.OriginalValue);
            var cmbSave = this.FindControl<ComboBox>("CmbSave");
            if (cmbSave != null)
            {
                cmbSave.IsEnabled = anyDirty;
            }
            var btnRestoreValues = this.FindControl<Button>("BtnRestoreValues");
            if (btnRestoreValues != null)
            {
                btnRestoreValues.IsEnabled = anyDirty;
            }
        }

        private void RestoreRow(IniRow row)
        {
            row.ValueSetter(row.OriginalValue);
            MarkRowDirty(row);
        }

        private IniSectionUi BuildSectionCard(string section, List<(string Key, string Value)> rows)
        {
            var rowsPanel = new StackPanel { Spacing = 4 };
            var rowsUi = new List<IniRow>(rows.Count);

            foreach (var (itemKey, originalDiskValue) in rows)
            {
                var itemValue = originalDiskValue;
                if (_currentProfile != null && _currentProfile.IniSettings.TryGetValue(section, out var sectionSettings) && sectionSettings.TryGetValue(itemKey, out var profileValue))
                {
                    itemValue = profileValue;
                }
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*,Auto") };
                var keyBlock = new TextBlock
                {
                    Text = itemKey,
                    FontFamily = new FontFamily("Consolas, Monospace"),
                    FontSize = _fontSize,
                    Foreground = ResBrush("BrTextSecondary", Brushes.Gray),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                };
                
                var rowObj = new IniRow
                {
                    Section = section,
                    Key = itemKey,
                    OriginalValue = itemValue,
                    KeyBlock = keyBlock
                };

                _schemaLookup.TryGetValue((section, itemKey), out var setting);

                var controlResult = SchemaSettingControlFactory.BuildControl(setting, itemValue, _keybindController, () => MarkRowDirty(rowObj));
                
                var valueControl = controlResult.Control;
                valueControl.VerticalAlignment = VerticalAlignment.Center;
                
                rowObj.ValueControl = valueControl;
                rowObj.ValueGetter = controlResult.ValueGetter;
                rowObj.ValueSetter = controlResult.ValueSetter;

                var restoreBtn = new Button
                {
                    Content = "",
                    FontFamily = (Application.Current?.FindResource("FontIcons") as FontFamily)!,
                    Classes = { "IconGhost" },
                    Margin = new Thickness(8, 0, 0, 0),
                    Width = 24, Height = 24, Padding = new Thickness(0),
                    FontSize = 14, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    IsVisible = false
                };
                ToolTip.SetTip(restoreBtn, GetResourceString("TxtViewIniRestoreRowTooltip", "Restore original value"));
                restoreBtn.Click += (_, __) => RestoreRow(rowObj);
                rowObj.RestoreRowButton = restoreBtn;

                Grid.SetColumn(keyBlock, 0);
                Grid.SetColumn(valueControl, 1);
                Grid.SetColumn(restoreBtn, 2);
                grid.Children.Add(keyBlock);
                grid.Children.Add(valueControl);
                grid.Children.Add(restoreBtn);

                var rowBorder = new Border { Child = grid, Padding = new Thickness(4, 4), CornerRadius = new CornerRadius(4) };
                rowObj.RowBorder = rowBorder;

                rowsPanel.Children.Add(rowBorder);
                rowsUi.Add(rowObj);
            }

            var header = new TextBlock
            {
                Text = $"[{section}]",
                FontWeight = FontWeight.SemiBold,
                FontSize = _fontSize + 1,
                Foreground = ResBrush("BrAccent", Brushes.MediumPurple),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var card = new Border
            {
                Background = ResBrush("BrBgElevated", Brushes.DimGray),
                BorderBrush = ResBrush("BrBorderSubtle", Brushes.Gray),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12),
                Child = new StackPanel { Children = { header, rowsPanel } }
            };

            return new IniSectionUi(card, header, section, rowsUi);
        }

        private void BtnRestoreValues_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            foreach (var section in _sections)
            {
                foreach (var row in section.Rows)
                {
                    if (row.ValueGetter() != row.OriginalValue)
                    {
                        RestoreRow(row);
                    }
                }
            }
        }

        private Dictionary<string, Dictionary<string, string>> GetCurrentIniSettings()
        {
            var dict = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var section in _sections)
            {
                if (!dict.ContainsKey(section.SectionName))
                    dict[section.SectionName] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                
                foreach (var row in section.Rows)
                {
                    dict[section.SectionName][row.Key] = row.ValueGetter();
                }
            }
            return dict;
        }

        private void ClearDirtyStates()
        {
            foreach (var section in _sections)
            {
                foreach (var row in section.Rows)
                {
                    row.OriginalValue = row.ValueGetter();
                    MarkRowDirty(row);
                }
            }
        }

        private async void CmbSave_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox cmb || cmb.SelectedItem is not ComboBoxItem item) return;
            var tag = item.Tag as string;
            cmb.SelectedIndex = -1; // Reset right away

            if (_game == null || _profileService == null || _currentProfile == null) return;

            if (tag == "override")
            {
                _currentProfile.IniSettings = GetCurrentIniSettings();
                _profileService.SaveProfile(_currentProfile, _currentProfile.IsBuiltIn);
                
                ClearDirtyStates();
                _onProfilePersisted?.Invoke(_currentProfile);
                Close();
            }
            else if (tag == "as_new")
            {
                var defaultName = _profileService.GetUniqueProfileName(_game.Name);
                var prompt = new PromptDialog(
                    GetResourceString("TxtPromptNewProfileTitle", "New Profile"),
                    GetResourceString("TxtPromptNewProfileLabel", "Profile Name:"),
                    defaultName
                );

                var newName = await prompt.ShowDialog<string?>(this);
                if (!string.IsNullOrWhiteSpace(newName))
                {
                    var newProfile = new Models.OptiScalerProfile
                    {
                        Name = newName.Trim(),
                        IsBuiltIn = false,
                        CreatedBy = "User",
                        CreatedDate = DateTime.Now,
                        Description = "",
                        IniSettings = GetCurrentIniSettings()
                    };

                    _profileService.SaveProfile(newProfile, false);
                    ClearDirtyStates();
                    _onProfilePersisted?.Invoke(newProfile);
                    Close();
                }
            }
        }

        private void BtnZoomIn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
            => ChangeZoom(ZoomStepPercent);

        private void BtnZoomOut_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
            => ChangeZoom(-ZoomStepPercent);

        private void ChangeZoom(int deltaPercent)
        {
            var next = Math.Clamp(_zoomPercent + deltaPercent, MinZoomPercent, MaxZoomPercent);
            if (next == _zoomPercent) return;
            _zoomPercent = next;
            _fontSize = BaseFontSize * _zoomPercent / 100.0;

            if (_plainDumpBlock != null) _plainDumpBlock.FontSize = _fontSize;
            foreach (var section in _sections)
            {
                section.Header.FontSize = _fontSize + 1;
                foreach (var row in section.Rows)
                {
                    row.KeyBlock.FontSize = _fontSize;
                }
            }

            var zoomLabel = this.FindControl<TextBlock>("TxtZoomLevel");
            if (zoomLabel != null) zoomLabel.Text = $"{_zoomPercent}%";
        }

        private void TxtSearch_TextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
            => ApplyFilter((sender as TextBox)?.Text?.Trim() ?? "");

        private void CmbSearchMode_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if ((sender as ComboBox)?.SelectedItem is not ComboBoxItem item) return;
            _searchMode = item.Tag as string ?? "term";
            ApplyFilter(this.FindControl<TextBox>("TxtSearch")?.Text?.Trim() ?? "");
        }

        /// <summary>"term" (default) filters individual rows by key/value, same as before. "section"
        /// instead matches the query against [Section] titles and shows/hides whole sections — e.g.
        /// searching "spoofing" shows the entire [Spoofing] section with all its keys, rather than only
        /// rows whose key/value happen to contain "spoofing".</summary>
        private void ApplyFilter(string query)
        {
            foreach (var section in _sections)
            {
                if (_searchMode == "section")
                {
                    var sectionMatch = query.Length == 0
                        || section.SectionName.Contains(query, StringComparison.OrdinalIgnoreCase);
                    foreach (var row in section.Rows) row.RowBorder.IsVisible = true;
                    section.Card.IsVisible = sectionMatch;
                    continue;
                }

                bool anyVisible = false;
                foreach (var row in section.Rows)
                {
                    var match = query.Length == 0
                        || row.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || row.ValueGetter().Contains(query, StringComparison.OrdinalIgnoreCase);
                    row.RowBorder.IsVisible = match;
                    anyVisible |= match;
                }
                section.Card.IsVisible = anyVisible;
            }
        }
    }
}
