using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OptiscalerClient.Views;

namespace OptiscalerClient.Helpers
{
    public class SchemaSettingControlFactory
    {
        public static Dictionary<(string Section, string Key), SchemaSetting> LoadCanonicalSchemaLookup()
        {
            var lookup = new Dictionary<(string Section, string Key), SchemaSetting>(new CaseInsensitiveTupleComparer());
            try
            {
                using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://OptiscalerClient/assets/configs/profile_editor_schema.json"));
                using var reader = new System.IO.StreamReader(stream);
                var json = reader.ReadToEnd();
                var schema = JsonSerializer.Deserialize<SettingsSchema>(json);
                if (schema?.Sections != null)
                {
                    foreach (var section in schema.Sections)
                    {
                        if (section.Settings != null)
                        {
                            foreach (var setting in section.Settings)
                            {
                                lookup[(section.Name ?? "", setting.Key ?? "")] = setting;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading schema: {ex}");
            }
            return lookup;
        }

        private class CaseInsensitiveTupleComparer : IEqualityComparer<(string Section, string Key)>
        {
            public bool Equals((string Section, string Key) x, (string Section, string Key) y)
            {
                return string.Equals(x.Section, y.Section, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode((string Section, string Key) obj)
            {
                return HashCode.Combine(
                    obj.Section.ToLowerInvariant(),
                    obj.Key.ToLowerInvariant()
                );
            }
        }

        public static (Control Control, Func<string> ValueGetter, Action<string> ValueSetter) BuildControl(
            SchemaSetting? setting,
            string currentValue,
            KeybindCaptureController keybindController,
            Action? onChanged = null)
        {
            Control settingControl;
            Func<string> valueGetter;
            Action<string> valueSetter;

            string controlType = setting?.ControlType ?? "";
            
            if (string.IsNullOrEmpty(controlType) && (setting == null || setting.Options == null || setting.Options.Count == 0))
            {
                controlType = "text";
            }

            if (string.Equals(controlType, "keybind", StringComparison.OrdinalIgnoreCase))
            {
                var keybindButton = keybindController.BuildKeybindButton(currentValue, onChanged);
                settingControl = keybindButton;
                valueGetter = () => keybindButton.Tag?.ToString() ?? "auto";
                valueSetter = (val) => keybindController.SetKeybindValue(keybindButton, val);
            }
            else if (string.Equals(controlType, "folderpath", StringComparison.OrdinalIgnoreCase))
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
                
                if (onChanged != null)
                {
                    pathTextBox.TextChanged += (s, e) => onChanged();
                }

                var browseButton = new Button
                {
                    Content = "Browse...",
                    Padding = new Thickness(12, 6),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
                };

                browseButton.Click += async (s, e) =>
                {
                    var topLevel = TopLevel.GetTopLevel(browseButton);
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
                            onChanged?.Invoke();
                        }
                    }
                };

                pathPanel.Children.Add(pathTextBox);
                pathPanel.Children.Add(browseButton);

                settingControl = pathPanel;
                valueGetter = () => string.IsNullOrWhiteSpace(pathTextBox.Text) ? "auto" : pathTextBox.Text;
                valueSetter = (val) => pathTextBox.Text = (val == "auto" ? "" : val);
            }
            else if (string.Equals(controlType, "text", StringComparison.OrdinalIgnoreCase))
            {
                var textBox = new TextBox
                {
                    Text = currentValue == "auto" ? "" : currentValue,
                    Watermark = "auto",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
                };
                
                if (onChanged != null)
                {
                    textBox.TextChanged += (s, e) => onChanged();
                }

                settingControl = textBox;
                valueGetter = () => string.IsNullOrWhiteSpace(textBox.Text) ? "auto" : textBox.Text;
                valueSetter = (val) => textBox.Text = (val == "auto" ? "" : val);
            }
            else
            {
                var comboBox = new ComboBox
                {
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
                };

                var optionItems = BuildOptionItems(setting);
                var exactMatch = optionItems.FirstOrDefault(item => item.Value == currentValue);
                
                if (exactMatch == null)
                {
                    exactMatch = new OptionItem(currentValue, currentValue);
                    optionItems.Add(exactMatch);
                }

                foreach (var option in optionItems)
                {
                    comboBox.Items.Add(option);
                }

                comboBox.SelectedItem = exactMatch;
                
                if (onChanged != null)
                {
                    comboBox.SelectionChanged += (s, e) => onChanged();
                }

                settingControl = comboBox;
                valueGetter = () => comboBox.SelectedItem is OptionItem optionItem ? optionItem.Value : "auto";
                valueSetter = (val) =>
                {
                    var match = comboBox.Items.OfType<OptionItem>().FirstOrDefault(item => item.Value == val);
                    if (match == null)
                    {
                        match = new OptionItem(val, val);
                        comboBox.Items.Add(match);
                    }
                    comboBox.SelectedItem = match;
                };
            }

            return (settingControl, valueGetter, valueSetter);
        }

        private static List<OptionItem> BuildOptionItems(SchemaSetting? setting)
        {
            var options = setting?.Options ?? new List<OptionEntry>();
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
    }

    public class KeybindCaptureController
    {
        private Button? _keyCaptureButton;
        private string? _keyCapturePreviousValue;
        private Action? _currentOnChanged;

        public Button BuildKeybindButton(string value, Action? onChanged = null)
        {
            var button = new Button
            {
                Height = 32,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Content = FormatKeybindLabel(value),
                Tag = value
            };

            button.Click += (_, __) => BeginKeyCapture(button, onChanged);
            return button;
        }

        private void BeginKeyCapture(Button button, Action? onChanged)
        {
            _keyCaptureButton = button;
            _keyCapturePreviousValue = button.Tag?.ToString() ?? "auto";
            _currentOnChanged = onChanged;
            button.Content = "Press a key...";
        }

        public void HandleKeyDown(object? sender, KeyEventArgs e)
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
            if (newValue != _keyCapturePreviousValue)
            {
                _currentOnChanged?.Invoke();
            }
            EndKeyCapture();
        }

        private void EndKeyCapture()
        {
            _keyCaptureButton = null;
            _keyCapturePreviousValue = null;
            _currentOnChanged = null;
        }

        public void SetKeybindValue(Button button, string value)
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
            if (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase)) return "Auto";
            if (value == "-1") return "Disabled";
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && value.Length == 4)
            {
                try
                {
                    int code = Convert.ToInt32(value, 16);
                    if (code >= 0x41 && code <= 0x5A) return ((char)code).ToString();
                    if (code >= 0x30 && code <= 0x39) return ((char)code).ToString();
                    if (code >= 0x60 && code <= 0x69) return $"NumPad {code - 0x60}";
                    if (code >= 0x70 && code <= 0x7B) return $"F{code - 0x70 + 1}";
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
                        _ => value
                    };
                }
                catch { return value; }
            }
            return value;
        }
    }
}
