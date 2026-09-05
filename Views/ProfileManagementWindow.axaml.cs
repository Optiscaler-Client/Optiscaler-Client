using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using OptiscalerClient.Helpers;
using OptiscalerClient.Models;
using OptiscalerClient.Services;

namespace OptiscalerClient.Views
{
    public partial class ProfileManagementWindow : Window
    {
        private readonly ProfileManagementService _profileService;
        private readonly ComponentManagementService _componentService;
        private OptiScalerProfile? _selectedProfile;
        private string _defaultProfileName = OptiScalerProfile.BuiltInDefaultName;
        private string _searchText = string.Empty;

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public ProfileManagementWindow()
        {
            InitializeComponent();
            WindowScreenFitHelper.FitToScreen(this);
            _profileService = new ProfileManagementService();
            _componentService = new ComponentManagementService();
            _defaultProfileName = _componentService.Config.DefaultProfileName;

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
                    AnimationHelper.SetupPanelTransition(rootPanel);
                    rootPanel.Opacity = 1;
                }
            };

            LoadProfiles();
        }

        private void LoadProfiles()
        {
            var pnlProfiles = this.FindControl<StackPanel>("PnlProfiles");
            if (pnlProfiles == null) return;

            pnlProfiles.Children.Clear();

            var profiles = _profileService.GetAllProfiles(forceRefresh: true);
            var customProfiles = profiles.Where(p => !p.IsBuiltIn).ToList();
            _defaultProfileName = _componentService.Config.DefaultProfileName;
            if (string.IsNullOrWhiteSpace(_defaultProfileName)
                || !profiles.Any(p => p.Name.Equals(_defaultProfileName, StringComparison.OrdinalIgnoreCase)))
            {
                _defaultProfileName = _profileService.GetDefaultProfile().Name;
                _componentService.Config.DefaultProfileName = _defaultProfileName;
                _componentService.SaveConfiguration();
            }

            var txtProfileInfo = this.FindControl<TextBlock>("TxtProfileInfo");
            var filteredProfiles = profiles.Where(profile =>
                string.IsNullOrWhiteSpace(_searchText)
                || profile.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(profile.Description)
                    && profile.Description.Contains(_searchText, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (txtProfileInfo != null)
            {
                int totalCount = profiles.Count;
                int customCount = customProfiles.Count;
                if (string.IsNullOrWhiteSpace(_searchText))
                {
                    txtProfileInfo.Text = $"{totalCount} profile(s) total ({customCount} custom).";
                }
                else
                {
                    txtProfileInfo.Text = $"{filteredProfiles.Count} result(s) for '{_searchText}' ({totalCount} total).";
                }
            }

            // Show all profiles (Default + custom)
            if (!filteredProfiles.Any())
            {
                pnlProfiles.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(_searchText)
                        ? "No profiles found."
                        : "No matching profiles found.",
                    Foreground = Brushes.Gray,
                    FontSize = 11,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    Margin = new Thickness(0, 10)
                });

                if (_selectedProfile != null)
                {
                    var selectedName = _selectedProfile.Name;
                    _selectedProfile = filteredProfiles.FirstOrDefault(p =>
                        p.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
                }
            }
            else
            {
                foreach (var profile in filteredProfiles)
                {
                    var card = CreateProfileCard(profile);
                    pnlProfiles.Children.Add(card);
                }
            }

            if (_selectedProfile != null)
            {
                var selectedName = _selectedProfile.Name;
                _selectedProfile = filteredProfiles.FirstOrDefault(p =>
                    p.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
            }

            UpdateButtonStates();
            HighlightSelectedCard();
        }

        private void TxtProfileSearch_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                _searchText = textBox.Text?.Trim() ?? string.Empty;
                LoadProfiles();
            }
        }

        private void RootPanel_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.Source is not Visual visual)
                return;

            if (visual.FindAncestorOfType<TextBox>() != null)
                return;

            var focusManager = TopLevel.GetTopLevel(this)?.FocusManager;
            focusManager?.ClearFocus();
        }

        private Border CreateProfileCard(OptiScalerProfile profile)
        {
            var stack = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            var titleRow = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6
            };
            titleRow.Children.Add(new TextBlock
            {
                Text = profile.Name,
                FontWeight = FontWeight.Bold,
                Foreground = Application.Current?.FindResource("BrTextPrimary") as IBrush ?? Brushes.White
            });

            if (profile.Name.Equals(_defaultProfileName, StringComparison.OrdinalIgnoreCase))
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
                        Text = "Default",
                        FontSize = 9,
                        Foreground = Application.Current?.FindResource("BrTextSecondary") as IBrush ?? Brushes.Gray
                    }
                });
            }

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
                Cursor = new Cursor(StandardCursorType.Hand)
            };

            border.PointerPressed += ProfileCard_Click;
            return border;
        }

        private void ProfileCard_Click(object? sender, Avalonia.Input.PointerPressedEventArgs e)
        {
            if (sender is Border border && border.Tag is OptiScalerProfile profile)
            {
                _selectedProfile = profile;
                UpdateButtonStates();
                HighlightSelectedCard();
            }
        }

        private void HighlightSelectedCard()
        {
            var pnlProfiles = this.FindControl<StackPanel>("PnlProfiles");

            if (pnlProfiles != null)
            {
                foreach (var child in pnlProfiles.Children)
                {
                    if (child is Border border)
                    {
                        var isSelected = border.Tag == _selectedProfile;
                        border.Background = isSelected
                            ? Application.Current?.FindResource("BrBgElevated") as IBrush ?? Brushes.Transparent
                            : Application.Current?.FindResource("BrBgCard") as IBrush ?? Brushes.Transparent;
                        border.BorderThickness = isSelected ? new Thickness(2) : new Thickness(1);
                    }
                }
            }
        }

        private void UpdateButtonStates()
        {
            var btnEdit = this.FindControl<Button>("BtnEdit");
            var btnDuplicate = this.FindControl<Button>("BtnDuplicate");
            var btnDelete = this.FindControl<Button>("BtnDelete");
            var btnSetDefault = this.FindControl<Button>("BtnSetDefault");
            var btnExport = this.FindControl<Button>("BtnExport");

            if (_selectedProfile == null)
            {
                if (btnEdit != null) btnEdit.IsEnabled = false;
                if (btnDuplicate != null) btnDuplicate.IsEnabled = false;
                if (btnDelete != null) btnDelete.IsEnabled = false;
                if (btnSetDefault != null) btnSetDefault.IsEnabled = false;
                if (btnExport != null) btnExport.IsEnabled = false;
            }
            else
            {
                if (btnEdit != null) btnEdit.IsEnabled = !_selectedProfile.IsBuiltIn;
                if (btnDuplicate != null) btnDuplicate.IsEnabled = true;
                if (btnDelete != null) btnDelete.IsEnabled = !_selectedProfile.IsBuiltIn;
                if (btnExport != null) btnExport.IsEnabled = true;
                if (btnSetDefault != null)
                {
                    btnSetDefault.IsEnabled = !_selectedProfile.Name.Equals(_defaultProfileName, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        private async void BtnExportProfile_Click(object? sender, RoutedEventArgs e)
        {
            if (_selectedProfile == null) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "Export profile as OptiScaler.ini",
                SuggestedFileName = "OptiScaler.ini",
                DefaultExtension = "ini",
                FileTypeChoices = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("INI files") { Patterns = new[] { "*.ini" } }
                }
            });

            if (file == null) return;

            try
            {
                var templatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "OptiScaler_example.ini");
                var iniContent = _profileService.GenerateOptiScalerIni(_selectedProfile, templatePath);
                await using var stream = await file.OpenWriteAsync();
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(iniContent);
            }
            catch (Exception ex)
            {
                await new ConfirmDialog(this, "Export Error", $"Failed to export profile: {ex.Message}").ShowDialog<object>(this);
            }
        }

        private void BtnSetDefault_Click(object? sender, RoutedEventArgs e)
        {
            if (_selectedProfile == null) return;

            _componentService.Config.DefaultProfileName = _selectedProfile.Name;
            _componentService.SaveConfiguration();
            _defaultProfileName = _selectedProfile.Name;
            LoadProfiles();
        }

        private async void BtnNewProfile_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var newProfile = OptiScalerProfile.CreateEmpty();
                var editor = new ProfileEditorWindow(newProfile, isNewProfile: true);
                await editor.ShowDialog(this);

                if (editor.ProfileSaved)
                {
                    LoadProfiles();
                }
            }
            catch (Exception ex) { DebugWindow.Log($"[ProfileManagement] New profile failed: {ex.Message}"); }
        }

        private async void BtnEditProfile_Click(object? sender, RoutedEventArgs e)
        {
            if (_selectedProfile == null) return;
            try
            {
                var wasDefault = _selectedProfile.Name.Equals(_defaultProfileName, StringComparison.OrdinalIgnoreCase);

                var editor = new ProfileEditorWindow(_selectedProfile, isNewProfile: false);
                await editor.ShowDialog(this);

                if (editor.ProfileSaved)
                {
                    if (wasDefault)
                    {
                        _componentService.Config.DefaultProfileName = _selectedProfile.Name;
                        _componentService.SaveConfiguration();
                        _defaultProfileName = _selectedProfile.Name;
                    }
                    LoadProfiles();
                }
            }
            catch (Exception ex) { DebugWindow.Log($"[ProfileManagement] Edit profile failed: {ex.Message}"); }
        }

        private async void BtnDuplicateProfile_Click(object? sender, RoutedEventArgs e)
        {
            if (_selectedProfile == null) return;
            try
            {
                var duplicatedProfile = _selectedProfile.Clone();
                duplicatedProfile.Name = $"{_selectedProfile.Name} (Copy)";
                duplicatedProfile.IsBuiltIn = false;

                var editor = new ProfileEditorWindow(duplicatedProfile, isNewProfile: true);
                await editor.ShowDialog(this);

                if (editor.ProfileSaved)
                {
                    LoadProfiles();
                }
            }
            catch (Exception ex) { DebugWindow.Log($"[ProfileManagement] Duplicate profile failed: {ex.Message}"); }
        }

        private async void BtnDeleteProfile_Click(object? sender, RoutedEventArgs e)
        {
            if (_selectedProfile == null) return;

            var dialog = new ConfirmDialog(this, "Delete Profile",
                $"Are you sure you want to delete the profile '{_selectedProfile.Name}'?", false);
            var result = await dialog.ShowDialog<bool>(this);

            if (result)
            {
                try
                {
                    var wasDefault = _selectedProfile.Name.Equals(_defaultProfileName, StringComparison.OrdinalIgnoreCase);
                    _profileService.DeleteProfile(_selectedProfile);
                    if (wasDefault)
                    {
                        _componentService.Config.DefaultProfileName = OptiScalerProfile.BuiltInDefaultName;
                        _componentService.SaveConfiguration();
                        _defaultProfileName = _componentService.Config.DefaultProfileName;
                    }
                    _selectedProfile = null;
                    LoadProfiles();
                }
                catch (Exception ex)
                {
                    await new ConfirmDialog(this, "Error", $"Failed to delete profile: {ex.Message}").ShowDialog<object>(this);
                }
            }
        }

        private bool _isAnimatingClose = false;

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
