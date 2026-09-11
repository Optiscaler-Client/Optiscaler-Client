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
        private sealed record IniRow(Border RowBorder, TextBlock KeyBlock, TextBlock ValueBlock, string Key, string Value);
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

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        public ViewIniWindow() => InitializeComponent(); // designer-only

        public ViewIniWindow(string iniPath, string gameName)
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
            WindowScreenFitHelper.FitToScreen(this);
            SetupWindow(gameName);
            PopulateSearchModeCombo();
            BuildContent(iniPath);
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
        }

        private void BtnClose_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

        private static IBrush ResBrush(string key, IBrush fallback)
            => Application.Current?.FindResource(key) as IBrush ?? fallback;

        private static string GetResourceString(string key, string fallback)
            => Application.Current?.TryFindResource(key, out var res) == true && res is string str ? str : fallback;

        /// <summary>Built in code, like CmbSpoofing elsewhere in this app, instead of declaring
        /// &lt;ComboBoxItem&gt; children directly in XAML — the declarative form threw "Could not find
        /// parent name scope" here at runtime (XAML compiled fine; only failed when the window was
        /// actually opened).</summary>
        private void PopulateSearchModeCombo()
        {
            var cmb = this.FindControl<ComboBox>("CmbSearchMode");
            if (cmb == null) return;

            cmb.Items.Add(new ComboBoxItem { Content = GetResourceString("TxtViewIniSearchByTerm", "By term"), Tag = "term" });
            cmb.Items.Add(new ComboBoxItem { Content = GetResourceString("TxtViewIniSearchBySection", "By section"), Tag = "section" });
            cmb.SelectedIndex = 0;
            cmb.SelectionChanged += CmbSearchMode_SelectionChanged;
        }

        /// <summary>Parses the ini into [Section] cards of "Key  Value" rows. Unknown format (can't
        /// even find one "key=value" line) falls back to a plain monospace dump instead of an empty
        /// window, so a malformed/edited-by-hand ini is still inspectable.</summary>
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

        private IniSectionUi BuildSectionCard(string section, List<(string Key, string Value)> rows)
        {
            var rowsPanel = new StackPanel { Spacing = 4 };
            var rowsUi = new List<IniRow>(rows.Count);

            foreach (var (key, value) in rows)
            {
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*") };
                var keyBlock = new TextBlock
                {
                    Text = key,
                    FontFamily = new FontFamily("Consolas, Monospace"),
                    FontSize = _fontSize,
                    Foreground = ResBrush("BrTextSecondary", Brushes.Gray),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Top
                };
                var valueBlock = new TextBlock
                {
                    Text = value,
                    FontFamily = new FontFamily("Consolas, Monospace"),
                    FontSize = _fontSize,
                    Foreground = ResBrush("BrTextPrimary", Brushes.White),
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetColumn(keyBlock, 0);
                Grid.SetColumn(valueBlock, 1);
                grid.Children.Add(keyBlock);
                grid.Children.Add(valueBlock);

                var rowBorder = new Border { Child = grid, Padding = new Thickness(0, 2) };
                rowsPanel.Children.Add(rowBorder);
                rowsUi.Add(new IniRow(rowBorder, keyBlock, valueBlock, key, value));
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
                    row.ValueBlock.FontSize = _fontSize;
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
                        || row.Value.Contains(query, StringComparison.OrdinalIgnoreCase);
                    row.RowBorder.IsVisible = match;
                    anyVisible |= match;
                }
                section.Card.IsVisible = anyVisible;
            }
        }
    }
}
