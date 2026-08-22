using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using OptiscalerClient.Helpers;
using OptiscalerClient.Models;
using OptiscalerClient.Services;

namespace OptiscalerClient.Views
{
    public partial class ProfileEditorWindow : Window
    {
        private OptiScalerProfile _profile;
        private bool _isNewProfile;
        private Dictionary<string, Dictionary<string, SettingControlRef>> _settingControls = new();
        private SettingsSchema? _schema;
        private LayoutSettings _layout = new();
        private WrapPanel? _sectionsWrap;
        private Button? _keyCaptureButton;
        private string? _keyCapturePreviousValue;
        private string _searchText = string.Empty;
        private StackPanel? _sidebarNav;
        private Dictionary<string, Border> _sectionBorders = new();
        private bool _isEasyMode = true;

        public bool ProfileSaved { get; private set; }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public ProfileEditorWindow()
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
            _profile = OptiScalerProfile.CreateEmpty();
            _isNewProfile = true;
            SetupWindow();
        }

        public ProfileEditorWindow(OptiScalerProfile profile, bool isNewProfile = false)
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
            _profile = profile;
            _isNewProfile = isNewProfile;
            SetupWindow();
            LoadProfileData();
            BuildSettingsUI();
            UpdateModeButtons();
        }

        private void SetupWindow()
        {
            // Flicker-free startup strategy
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
                    Helpers.AnimationHelper.SetupPanelTransition(rootPanel);
                    rootPanel.Opacity = 1;
                }

                UpdateWrapLayout();
            };

            this.SizeChanged += (_, __) => UpdateWrapLayout();
            this.KeyDown += HandleKeyCapture;
        }

        private void TxtSettingsSearch_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                FlushControlValues();
                _searchText = textBox.Text?.Trim() ?? string.Empty;
                BuildSettingsUI();
            }
        }

        private void RootPanel_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.Source is not Visual visual)
                return;

            // Check if the clicked element or any of its parents is a TextBox
            var current = visual;
            while (current != null)
            {
                if (current is TextBox)
                    return;
                current = current.Parent as Visual;
            }

            var focusManager = TopLevel.GetTopLevel(this)?.FocusManager;
            focusManager?.ClearFocus();
        }

        private async void NavButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button button && button.Tag is string sectionName && _sectionBorders.TryGetValue(sectionName, out var sectionBorder))
                {
                    var scrollViewer = this.FindControl<ScrollViewer>("SettingsScrollViewer");
                    if (scrollViewer != null && _sectionsWrap != null)
                    {
                        // Wait for layout to be updated
                        await System.Threading.Tasks.Task.Delay(10);

                        // Force layout update
                        scrollViewer.InvalidateMeasure();
                        scrollViewer.InvalidateArrange();
                        _sectionsWrap.InvalidateMeasure();
                        _sectionsWrap.InvalidateArrange();

                        // Wait a bit more for the layout to settle
                        await System.Threading.Tasks.Task.Delay(50);

                        // Calculate the position of the section relative to the scroll viewer's content
                        var transform = sectionBorder.TransformToVisual(_sectionsWrap);
                        if (transform.HasValue)
                        {
                            var position = transform.Value.Transform(new Point(0, 0));

                            // Get current scroll offset and add the section's position
                            var targetOffset = position.Y - 20; // 20px padding from top

                            // Scroll to the section
                            scrollViewer.Offset = new Vector(0, Math.Max(0, targetOffset));
                        }
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ProfileEditor] Nav scroll failed: {ex.Message}"); }
        }

        private void LoadProfileData()
        {
            var txtProfileName = this.FindControl<TextBox>("TxtProfileName");
            var txtDescription = this.FindControl<TextBox>("TxtDescription");
            var btnCancel = this.FindControl<Button>("BtnCancel");
            var btnSave = this.FindControl<Button>("BtnSave");

            if (txtProfileName != null)
            {
                txtProfileName.Text = _profile.Name;
                txtProfileName.IsReadOnly = _profile.IsBuiltIn;
            }

            if (txtDescription != null)
            {
                txtDescription.Text = _profile.Description;
            }

            if (btnCancel != null)
            {
                btnCancel.Click += BtnCancel_Click;
            }

            if (btnSave != null)
            {
                btnSave.Click += BtnSave_Click;
            }

            // Populate easy-mode virtual sections from canonical sections before building the UI
            if (_isEasyMode)
                SyncSchemaValues(fromEasyToAdvanced: false);

            BuildSettingsUI();
            UpdateModeButtons();
        }

        private void BuildSettingsUI()
        {
            var sectionsWrap = this.FindControl<WrapPanel>("SectionsWrap");
            var sidebarNav = this.FindControl<StackPanel>("SidebarNav");
            if (sectionsWrap == null || sidebarNav == null) return;

            _sectionsWrap = sectionsWrap;
            _sidebarNav = sidebarNav;
            sectionsWrap.Children.Clear();

            // Clear existing navigation buttons (except the title)
            while (sidebarNav.Children.Count > 1)
            {
                sidebarNav.Children.RemoveAt(1);
            }

            _sectionBorders.Clear();

            string schemaFileName = _isEasyMode ? "easy_profile_editor_schema.json" : "profile_editor_schema.json";
            string schemaPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "configs", schemaFileName);
            if (!File.Exists(schemaPath))
            {
                sectionsWrap.Children.Add(new TextBlock { Text = $"Error: Settings schema file not found: {schemaFileName}" });
                return;
            }

            try
            {
                var json = File.ReadAllText(schemaPath);
                _schema = JsonSerializer.Deserialize<SettingsSchema>(json);
            }
            catch (Exception ex)
            {
                sectionsWrap.Children.Add(new TextBlock { Text = $"Error parsing schema: {ex.Message}" });
                return;
            }

            if (_schema?.Sections == null) return;
            _layout = _schema.Layout ?? new LayoutSettings();

            foreach (var section in _schema.Sections)
            {
                var sectionName = section.Name;
                if (string.IsNullOrEmpty(sectionName)) continue;

                if (!_settingControls.ContainsKey(sectionName))
                {
                    _settingControls[sectionName] = new Dictionary<string, SettingControlRef>();
                }

                if (!_profile.IniSettings.ContainsKey(sectionName))
                {
                    _profile.IniSettings[sectionName] = new Dictionary<string, string>();
                }

                var sectionCard = BuildSectionCard(sectionName, section);
                if (sectionCard != null)
                {
                    sectionsWrap.Children.Add(sectionCard);
                    _sectionBorders[sectionName] = sectionCard;

                    // Add navigation button
                    var navButton = new Button
                    {
                        Content = sectionName,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                        Padding = new Thickness(12, 8),
                        FontSize = 12,
                        Tag = sectionName,
                        Margin = new Thickness(0, 0, 8, 0) // Add right margin to avoid scrollbar overlap
                    };
                    navButton.Classes.Add("BtnSecondary");
                    navButton.Click += NavButton_Click;
                    sidebarNav.Children.Add(navButton);
                }
            }

            UpdateWrapLayout();
        }

        private Border BuildSectionCard(string sectionName, SchemaSection section)
        {
            var cardContent = new StackPanel { Spacing = 10 };

            var header = new TextBlock
            {
                Text = sectionName,
                FontSize = 15,
                FontWeight = Avalonia.Media.FontWeight.Bold,
                Foreground = Application.Current?.FindResource("BrTextPrimary") as Avalonia.Media.IBrush
                    ?? Avalonia.Media.Brushes.White
            };
            cardContent.Children.Add(header);

            var columns = Math.Max(1, section.Columns);
            var rows = Math.Max(1, section.Rows);

            var sectionGrid = new Grid();
            for (int i = 0; i < columns; i++)
                sectionGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            for (int i = 0; i < rows; i++)
                sectionGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var visibleSettingsCount = 0;

            if (section.Settings != null)
            {
                foreach (var setting in section.Settings)
                {
                    if (string.IsNullOrEmpty(setting.Key)) continue;

                    // Apply search filter
                    if (!string.IsNullOrWhiteSpace(_searchText))
                    {
                        var label = (setting.Label ?? setting.Key).ToLowerInvariant();
                        var tooltip = (setting.Tooltip ?? "").ToLowerInvariant();
                        var searchLower = _searchText.ToLowerInvariant();
                        if (!label.Contains(searchLower) && !tooltip.Contains(searchLower))
                        {
                            continue; // Skip this setting if it doesn't match search
                        }
                    }

                    visibleSettingsCount++;

                    var settingPanel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 12, 12) };
                    var labelRow = new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 6
                    };
                    var labelBlock = new TextBlock
                    {
                        Text = setting.Label ?? setting.Key,
                        FontSize = 12,
                        Foreground = Application.Current?.FindResource("BrTextSecondary") as Avalonia.Media.IBrush
                            ?? Avalonia.Media.Brushes.Gray
                    };
                    labelRow.Children.Add(labelBlock);

                    if (!string.IsNullOrWhiteSpace(setting.Tooltip))
                    {
                        var tooltipIcon = new Border
                        {
                            Width = 16,
                            Height = 16,
                            CornerRadius = new CornerRadius(8),
                            BorderThickness = new Thickness(1),
                            BorderBrush = Application.Current?.FindResource("BrBorderSubtle") as Avalonia.Media.IBrush
                                ?? Avalonia.Media.Brushes.DimGray,
                            Background = Application.Current?.FindResource("BrBgCard") as Avalonia.Media.IBrush
                                ?? Avalonia.Media.Brushes.Transparent,
                            Child = new TextBlock
                            {
                                Text = "?",
                                FontSize = 10,
                                FontWeight = Avalonia.Media.FontWeight.Bold,
                                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                                Foreground = Application.Current?.FindResource("BrTextSecondary") as Avalonia.Media.IBrush
                                    ?? Avalonia.Media.Brushes.Gray
                            }
                        };
                        ToolTip.SetTip(tooltipIcon, setting.Tooltip);
                        labelRow.Children.Add(tooltipIcon);
                    }

                    settingPanel.Children.Add(labelRow);

                    Control settingControl;
                    SettingControlRef settingRef;
                    var hasValue = _profile.IniSettings[sectionName].TryGetValue(setting.Key, out var currentValue);
                    if (!hasValue || string.IsNullOrWhiteSpace(currentValue))
                    {
                        currentValue = string.Equals(setting.Key, "ShortcutKey", StringComparison.OrdinalIgnoreCase)
                            ? "0x2D"
                            : "auto";
                    }

                    if (string.Equals(setting.ControlType, "keybind", StringComparison.OrdinalIgnoreCase))
                    {
                        var keybindButton = BuildKeybindButton(currentValue);
                        settingControl = keybindButton;
                        settingRef = new SettingControlRef(settingControl, () => keybindButton.Tag?.ToString() ?? "auto", setting.AppliesTo);
                    }
                    else if (string.Equals(setting.ControlType, "text", StringComparison.OrdinalIgnoreCase))
                    {
                        var textBox = new TextBox
                        {
                            Text = currentValue == "auto" ? "" : currentValue,
                            Watermark = "auto",
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
                        };
                        settingControl = textBox;
                        settingRef = new SettingControlRef(settingControl, () =>
                            string.IsNullOrWhiteSpace(textBox.Text) ? "auto" : textBox.Text, setting.AppliesTo);
                    }
                    else if (string.Equals(setting.ControlType, "folderpath", StringComparison.OrdinalIgnoreCase))
                    {
                        var pathPanel = new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            Spacing = 8
                        };

                        var pathTextBox = new TextBox
                        {
                            Text = currentValue == "auto" ? "" : currentValue,
                            Watermark = "auto",
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                            IsReadOnly = false,
                            MinWidth = 180
                        };

                        var browseButton = new Button
                        {
                            Content = "Browse...",
                            Padding = new Thickness(12, 6),
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
                        };

                        browseButton.Click += async (s, e) =>
                        {
                            var topLevel = TopLevel.GetTopLevel(this);
                            if (topLevel != null)
                            {
                                var folderPicker = await topLevel.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
                                {
                                    Title = "Select Folder",
                                    AllowMultiple = false
                                });

                                if (folderPicker.Count > 0)
                                {
                                    pathTextBox.Text = folderPicker[0].Path.LocalPath;
                                }
                            }
                        };

                        pathPanel.Children.Add(pathTextBox);
                        pathPanel.Children.Add(browseButton);

                        settingControl = pathPanel;
                        settingRef = new SettingControlRef(settingControl, () =>
                            string.IsNullOrWhiteSpace(pathTextBox.Text) ? "auto" : pathTextBox.Text, setting.AppliesTo);
                    }
                    else
                    {
                        var comboBox = new ComboBox
                        {
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
                        };

                        var optionItems = BuildOptionItems(setting);
                        foreach (var option in optionItems)
                        {
                            comboBox.Items.Add(option);
                        }

                        var selectedItem = optionItems.FirstOrDefault(item => item.Value == currentValue)
                            ?? optionItems.FirstOrDefault();
                        comboBox.SelectedItem = selectedItem;

                        settingControl = comboBox;
                        settingRef = new SettingControlRef(settingControl, () =>
                            comboBox.SelectedItem is OptionItem optionItem ? optionItem.Value : "auto", setting.AppliesTo);
                    }

                    _settingControls[sectionName][setting.Key] = settingRef;
                    settingPanel.Children.Add(settingControl);

                    var safeRow = Math.Clamp(setting.Row, 0, rows - 1);
                    var safeColumn = Math.Clamp(setting.Column, 0, columns - 1);
                    Grid.SetRow(settingPanel, safeRow);
                    Grid.SetColumn(settingPanel, safeColumn);
                    sectionGrid.Children.Add(settingPanel);
                }
            }

            // Only add the grid to the card if there are visible settings
            if (visibleSettingsCount > 0)
            {
                cardContent.Children.Add(sectionGrid);
            }
            else
            {
                // If no settings are visible and there's a search, don't show this section at all
                if (!string.IsNullOrWhiteSpace(_searchText))
                {
                    return null!;
                }
                // If no search is active, show empty section (for initial load)
                cardContent.Children.Add(sectionGrid);
            }

            return new Border
            {
                Background = Application.Current?.FindResource("BrBgCard") as Avalonia.Media.IBrush
                    ?? Avalonia.Media.Brushes.Transparent,
                BorderBrush = Application.Current?.FindResource("BrBorderSubtle") as Avalonia.Media.IBrush
                    ?? Avalonia.Media.Brushes.DimGray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 0, _layout.ColumnGap, _layout.RowGap),
                Child = cardContent
            };
        }

        private void UpdateWrapLayout()
        {
            if (_sectionsWrap == null) return;

            var availableWidth = _sectionsWrap.Bounds.Width;
            if (availableWidth <= 0)
            {
                availableWidth = Math.Max(0, Bounds.Width - 120);
            }

            var columns = CalculateColumns(_layout, availableWidth);
            var gap = _layout.ColumnGap;
            var cardWidth = (availableWidth - (columns - 1) * gap) / columns;
            cardWidth = Math.Clamp(cardWidth, _layout.CardMinWidth, _layout.CardMaxWidth);

            _sectionsWrap.ItemWidth = cardWidth;
        }

        private List<OptionItem> BuildOptionItems(SchemaSetting setting)
        {
            var options = setting.Options ?? new List<OptionEntry>();
            var items = new List<OptionItem>();

            foreach (var option in options)
            {
                var value = option.Value ?? string.Empty;
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var label = string.IsNullOrWhiteSpace(option.Label) ? value : option.Label;
                items.Add(new OptionItem(value, label));
            }

            return items;
        }

        private Button BuildKeybindButton(string value)
        {
            var button = new Button
            {
                Height = 32,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Content = FormatKeybindLabel(value),
                Tag = value
            };

            button.Click += (_, __) => BeginKeyCapture(button);
            return button;
        }

        private void BeginKeyCapture(Button button)
        {
            _keyCaptureButton = button;
            _keyCapturePreviousValue = button.Tag?.ToString() ?? "auto";
            button.Content = "Press a key...";
        }

        private void HandleKeyCapture(object? sender, KeyEventArgs e)
        {
            if (_keyCaptureButton == null) return;
            e.Handled = true;

            var key = e.Key;
            if (key == Key.Escape)
            {
                SetKeybindValue(_keyCaptureButton, _keyCapturePreviousValue ?? "auto");
                EndKeyCapture();
                return;
            }

            string? newValue = key switch
            {
                Key.Back => "auto",
                Key.Delete => "-1",
                _ => TryMapKeyToVirtualKey(key)
            };

            if (string.IsNullOrWhiteSpace(newValue))
            {
                SetKeybindValue(_keyCaptureButton, _keyCapturePreviousValue ?? "auto");
                EndKeyCapture();
                return;
            }

            SetKeybindValue(_keyCaptureButton, newValue);
            EndKeyCapture();
        }

        private void EndKeyCapture()
        {
            _keyCaptureButton = null;
            _keyCapturePreviousValue = null;
        }

        private void SetKeybindValue(Button button, string value)
        {
            button.Tag = value;
            button.Content = FormatKeybindLabel(value);
        }

        private static string? TryMapKeyToVirtualKey(Key key)
        {
            if (key >= Key.A && key <= Key.Z)
            {
                var code = 0x41 + (key - Key.A);
                return $"0x{code:X2}";
            }

            if (key >= Key.D0 && key <= Key.D9)
            {
                var code = 0x30 + (key - Key.D0);
                return $"0x{code:X2}";
            }

            if (key >= Key.NumPad0 && key <= Key.NumPad9)
            {
                var code = 0x60 + (key - Key.NumPad0);
                return $"0x{code:X2}";
            }

            if (key >= Key.F1 && key <= Key.F12)
            {
                var code = 0x70 + (key - Key.F1);
                return $"0x{code:X2}";
            }

            return key switch
            {
                Key.Insert => "0x2D",
                Key.Home => "0x24",
                Key.End => "0x23",
                Key.PageUp => "0x21",
                Key.PageDown => "0x22",
                Key.Back => "0x08",
                Key.Tab => "0x09",
                Key.Enter => "0x0D",
                Key.Space => "0x20",
                Key.Left => "0x25",
                Key.Up => "0x26",
                Key.Right => "0x27",
                Key.Down => "0x28",
                Key.Delete => "0x2E",
                Key.Escape => "0x1B",
                Key.LeftShift or Key.RightShift => "0x10",
                Key.LeftCtrl or Key.RightCtrl => "0x11",
                Key.LeftAlt or Key.RightAlt => "0x12",
                Key.CapsLock => "0x14",
                Key.PrintScreen => "0x2C",
                Key.Pause => "0x13",
                _ => null
            };
        }

        private static string FormatKeybindLabel(string value)
        {
            if (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return "Auto";
            }

            if (value == "-1")
            {
                return "Disabled";
            }

            var normalized = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? value[2..]
                : value;

            if (int.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
            {
                return GetVirtualKeyLabel(code);
            }

            return value;
        }

        private static string GetVirtualKeyLabel(int code)
        {
            if (code >= 0x41 && code <= 0x5A)
            {
                return ((char)code).ToString();
            }

            if (code >= 0x30 && code <= 0x39)
            {
                return ((char)code).ToString();
            }

            if (code >= 0x70 && code <= 0x7B)
            {
                return $"F{code - 0x6F}";
            }

            return code switch
            {
                0x2D => "Insert",
                0x24 => "Home",
                0x23 => "End",
                0x21 => "Page Up",
                0x22 => "Page Down",
                0x08 => "Backspace",
                0x09 => "Tab",
                0x0D => "Enter",
                0x20 => "Space",
                0x25 => "Left",
                0x26 => "Up",
                0x27 => "Right",
                0x28 => "Down",
                0x2E => "Delete",
                0x1B => "Escape",
                0x10 => "Shift",
                0x11 => "Ctrl",
                0x12 => "Alt",
                0x14 => "Caps Lock",
                0x2C => "Print Screen",
                0x13 => "Pause",
                _ => $"Key {code:X2}"
            };
        }

        private int CalculateColumns(LayoutSettings layout, double width)
        {
            int columns = 1;
            if (layout.Breakpoints != null && layout.Breakpoints.Count > 0)
            {
                var sorted = layout.Breakpoints.OrderBy(bp => bp.MinWidth).ToList();
                foreach (var breakpoint in sorted)
                {
                    if (width >= breakpoint.MinWidth)
                    {
                        columns = breakpoint.Columns;
                    }
                }
            }

            columns = Math.Min(layout.MaxColumns, Math.Max(1, columns));
            while (columns > 1)
            {
                var cardWidth = (width - (columns - 1) * layout.ColumnGap) / columns;
                if (cardWidth >= layout.CardMinWidth)
                {
                    break;
                }
                columns--;
            }

            return Math.Max(1, columns);
        }

        private void BtnEasyMode_Click(object? sender, RoutedEventArgs e)
        {
            if (_isEasyMode) return; // Already in Easy mode
            FlushControlValues();
            SyncSchemaValues(fromEasyToAdvanced: false);

            _isEasyMode = true;
            UpdateModeButtons();
            BuildSettingsUI();
        }

        private void BtnAdvancedMode_Click(object? sender, RoutedEventArgs e)
        {
            if (!_isEasyMode) return; // Already in Advanced mode
            FlushControlValues();
            SyncSchemaValues(fromEasyToAdvanced: true);

            _isEasyMode = false;
            UpdateModeButtons();
            BuildSettingsUI();
        }

        private void FlushControlValues()
        {
            foreach (var section in _settingControls)
            {
                if (!_profile.IniSettings.ContainsKey(section.Key))
                    _profile.IniSettings[section.Key] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var setting in section.Value)
                {
                    var value = setting.Value.ValueGetter?.Invoke() ?? "auto";
                    _profile.IniSettings[section.Key][setting.Key] = value;
                }
            }
        }

        private void SyncSchemaValues(bool fromEasyToAdvanced)
        {
            try
            {
                var easySchemaPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "configs", "easy_profile_editor_schema.json");
                var masterSchemaPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "configs", "profile_editor_schema.json");
                if (!File.Exists(easySchemaPath) || !File.Exists(masterSchemaPath)) return;

                var easyJson = File.ReadAllText(easySchemaPath);
                var masterJson = File.ReadAllText(masterSchemaPath);
                var easySchema = JsonSerializer.Deserialize<SettingsSchema>(easyJson);
                var masterSchema = JsonSerializer.Deserialize<SettingsSchema>(masterJson);
                if (easySchema?.Sections == null || masterSchema?.Sections == null) return;

                // Build map targetKey -> canonical section name
                var keyToSection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var sec in masterSchema.Sections)
                {
                    if (sec?.Settings == null || string.IsNullOrWhiteSpace(sec.Name)) continue;
                    foreach (var s in sec.Settings)
                    {
                        if (string.IsNullOrWhiteSpace(s.Key)) continue;
                        if (!keyToSection.ContainsKey(s.Key))
                            keyToSection[s.Key] = sec.Name!;
                    }
                }

                foreach (var easySection in easySchema.Sections)
                {
                    if (easySection?.Settings == null || string.IsNullOrWhiteSpace(easySection.Name)) continue;
                    foreach (var s in easySection.Settings)
                    {
                        if (s == null || string.IsNullOrWhiteSpace(s.Key)) continue;

                        // Ensure easy section dict exists
                        if (!_profile.IniSettings.ContainsKey(easySection.Name))
                            _profile.IniSettings[easySection.Name] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                        if (s.AppliesTo == null || s.AppliesTo.Count == 0)
                        {
                            // Passthrough key: sync directly with its canonical section
                            if (keyToSection.TryGetValue(s.Key, out var passCanonSec))
                            {
                                if (fromEasyToAdvanced)
                                {
                                    if (_profile.IniSettings[easySection.Name].TryGetValue(s.Key, out var pEasyVal) && !string.IsNullOrWhiteSpace(pEasyVal))
                                    {
                                        if (!_profile.IniSettings.ContainsKey(passCanonSec))
                                            _profile.IniSettings[passCanonSec] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                        _profile.IniSettings[passCanonSec][s.Key] = pEasyVal;
                                    }
                                }
                                else
                                {
                                    if (_profile.IniSettings.ContainsKey(passCanonSec) &&
                                        _profile.IniSettings[passCanonSec].TryGetValue(s.Key, out var pCanVal) &&
                                        !string.IsNullOrWhiteSpace(pCanVal))
                                    {
                                        _profile.IniSettings[easySection.Name][s.Key] = pCanVal;
                                    }
                                }
                            }
                            continue;
                        }

                        if (fromEasyToAdvanced)
                        {
                            // Copy easy value into each canonical target key/section
                            if (_profile.IniSettings[easySection.Name].TryGetValue(s.Key, out var easyVal) && !string.IsNullOrWhiteSpace(easyVal))
                            {
                                foreach (var targetKey in s.AppliesTo)
                                {
                                    if (string.IsNullOrWhiteSpace(targetKey)) continue;
                                    if (keyToSection.TryGetValue(targetKey, out var canonicalSection))
                                    {
                                        if (!_profile.IniSettings.ContainsKey(canonicalSection))
                                            _profile.IniSettings[canonicalSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                        _profile.IniSettings[canonicalSection][targetKey] = easyVal;
                                    }
                                }
                            }
                        }
                        else
                        {
                            // Copy from first available canonical target into the easy setting
                            foreach (var targetKey in s.AppliesTo)
                            {
                                if (string.IsNullOrWhiteSpace(targetKey)) continue;
                                if (keyToSection.TryGetValue(targetKey, out var canonicalSection) &&
                                    _profile.IniSettings.ContainsKey(canonicalSection) &&
                                    _profile.IniSettings[canonicalSection].TryGetValue(targetKey, out var canVal) &&
                                    !string.IsNullOrWhiteSpace(canVal))
                                {
                                    _profile.IniSettings[easySection.Name][s.Key] = canVal;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // ignore sync failures
            }
        }

        private void UpdateModeButtons()
        {
            var btnEasy = this.FindControl<Button>("BtnEasyMode");
            var btnAdvanced = this.FindControl<Button>("BtnAdvancedMode");

            if (btnEasy != null && btnAdvanced != null)
            {
                if (_isEasyMode)
                {
                    btnEasy.Classes.Remove("BtnSecondary");
                    btnEasy.Classes.Add("BtnPrimary");
                    btnAdvanced.Classes.Remove("BtnPrimary");
                    btnAdvanced.Classes.Add("BtnSecondary");
                }
                else
                {
                    btnEasy.Classes.Remove("BtnPrimary");
                    btnEasy.Classes.Add("BtnSecondary");
                    btnAdvanced.Classes.Remove("BtnSecondary");
                    btnAdvanced.Classes.Add("BtnPrimary");
                }
            }
        }

        private void BtnCancel_Click(object? sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void BtnSave_Click(object? sender, RoutedEventArgs e)
        {
            var txtProfileName = this.FindControl<TextBox>("TxtProfileName");
            var txtDescription = this.FindControl<TextBox>("TxtDescription");

            if (txtProfileName == null || string.IsNullOrWhiteSpace(txtProfileName.Text))
            {
                return;
            }

            // Update profile properties
            if (!_profile.IsBuiltIn)
            {
                _profile.Name = txtProfileName.Text.Trim();
            }
            _profile.Description = txtDescription?.Text?.Trim() ?? "";

            // Update settings from controls
            // Build a map of canonical key -> section from the advanced schema (if available)
            var keyToSection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var masterSchemaPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "configs", "profile_editor_schema.json");
                if (File.Exists(masterSchemaPath))
                {
                    var masterJson = File.ReadAllText(masterSchemaPath);
                    var masterSchema = JsonSerializer.Deserialize<SettingsSchema>(masterJson);
                    if (masterSchema?.Sections != null)
                    {
                        foreach (var sec in masterSchema.Sections)
                        {
                            if (sec?.Settings == null || string.IsNullOrWhiteSpace(sec.Name)) continue;
                            foreach (var s in sec.Settings)
                            {
                                if (string.IsNullOrWhiteSpace(s.Key)) continue;
                                if (!keyToSection.ContainsKey(s.Key))
                                    keyToSection[s.Key] = sec.Name!;
                            }
                        }
                    }
                }
            }
            catch
            {
                // If anything fails, fall back to previous behavior (no mapping)
            }

            foreach (var section in _settingControls)
            {
                if (!_profile.IniSettings.ContainsKey(section.Key))
                {
                    _profile.IniSettings[section.Key] = new Dictionary<string, string>();
                }

                foreach (var setting in section.Value)
                {
                    var value = setting.Value.ValueGetter?.Invoke() ?? "auto";
                    _profile.IniSettings[section.Key][setting.Key] = value;

                    // In Easy mode, also write this key to its own canonical section if it's a canonical key
                    if (_isEasyMode && !string.IsNullOrWhiteSpace(setting.Key) && keyToSection.TryGetValue(setting.Key, out var selfCanonicalSection))
                    {
                        if (!_profile.IniSettings.ContainsKey(selfCanonicalSection))
                            _profile.IniSettings[selfCanonicalSection] = new Dictionary<string, string>();
                        _profile.IniSettings[selfCanonicalSection][setting.Key] = value;
                    }

                    // In Easy mode, if this setting has AppliesTo, apply value to all target keys
                    if (_isEasyMode && setting.Value.AppliesTo != null && setting.Value.AppliesTo.Count > 0)
                    {
                        foreach (var targetKey in setting.Value.AppliesTo)
                        {
                            // If we know the canonical section for this key from the master schema, write there
                            if (!string.IsNullOrWhiteSpace(targetKey) && keyToSection.TryGetValue(targetKey, out var canonicalSection))
                            {
                                if (!_profile.IniSettings.ContainsKey(canonicalSection))
                                {
                                    _profile.IniSettings[canonicalSection] = new Dictionary<string, string>();
                                }
                                _profile.IniSettings[canonicalSection][targetKey] = value;
                            }
                            else
                            {
                                // Fallback: write into the current section (legacy behavior)
                                _profile.IniSettings[section.Key][targetKey] = value;
                            }
                        }
                    }
                }
            }

            // Save profile
            try
            {
                var profileService = new ProfileManagementService();
                profileService.SaveProfile(_profile, isBuiltIn: false);
                ProfileSaved = true;
                Close();
            }
            catch (Exception ex)
            {
                var dialog = new ConfirmDialog(this, "Error", $"Failed to save profile: {ex.Message}");
                _ = dialog.ShowDialog(this);
            }
        }
    }

    public class SettingsSchema
    {
        public LayoutSettings? Layout { get; set; }
        public System.Collections.Generic.List<SchemaSection>? Sections { get; set; }
    }

    public class LayoutSettings
    {
        public int MaxColumns { get; set; } = 3;
        public double ColumnGap { get; set; } = 16;
        public double RowGap { get; set; } = 16;
        public double CardMinWidth { get; set; } = 260;
        public double CardMaxWidth { get; set; } = 420;
        public System.Collections.Generic.List<LayoutBreakpoint>? Breakpoints { get; set; } = new();
    }

    public class LayoutBreakpoint
    {
        public double MinWidth { get; set; }
        public int Columns { get; set; } = 1;
    }

    public class SchemaSection
    {
        public string? Name { get; set; }
        public int Rows { get; set; } = 1;
        public int Columns { get; set; } = 1;
        public System.Collections.Generic.List<SchemaSetting>? Settings { get; set; }
    }

    public class SchemaSetting
    {
        public string? Key { get; set; }
        public string? Label { get; set; }
        public string? Tooltip { get; set; }
        public string? ControlType { get; set; }
        public System.Collections.Generic.List<OptionEntry>? Options { get; set; }
        public int Row { get; set; } = 0;
        public int Column { get; set; } = 0;
        public System.Collections.Generic.List<string>? AppliesTo { get; set; }
    }

    public class SettingControlRef
    {
        public SettingControlRef(Control control, Func<string> valueGetter, System.Collections.Generic.List<string>? appliesTo = null)
        {
            Control = control;
            ValueGetter = valueGetter;
            AppliesTo = appliesTo;
        }

        public Control Control { get; }
        public Func<string> ValueGetter { get; }
        public System.Collections.Generic.List<string>? AppliesTo { get; }
    }

    [JsonConverter(typeof(OptionEntryConverter))]
    public class OptionEntry
    {
        public string? Value { get; set; }
        public string? Label { get; set; }
    }

    public class OptionEntryConverter : JsonConverter<OptionEntry>
    {
        public override OptionEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                return new OptionEntry { Value = reader.GetString() };
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                using var doc = JsonDocument.ParseValue(ref reader);
                var root = doc.RootElement;
                string? value = null;
                if (root.TryGetProperty("Value", out var valueProp))
                {
                    value = valueProp.GetString();
                }
                else if (root.TryGetProperty("Key", out var keyProp))
                {
                    value = keyProp.GetString();
                }

                string? label = null;
                if (root.TryGetProperty("Label", out var labelProp))
                {
                    label = labelProp.GetString();
                }

                return new OptionEntry { Value = value, Label = label };
            }

            throw new JsonException("Invalid option entry format.");
        }

        public override void Write(Utf8JsonWriter writer, OptionEntry value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("Value", value.Value);
            if (!string.IsNullOrWhiteSpace(value.Label))
            {
                writer.WriteString("Label", value.Label);
            }
            writer.WriteEndObject();
        }
    }

    public class OptionItem
    {
        public OptionItem(string value, string label)
        {
            Value = value;
            Label = label;
        }

        public string Value { get; }
        public string Label { get; }

        public override string ToString() => Label;
    }
}
