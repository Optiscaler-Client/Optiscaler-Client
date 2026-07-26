using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SharpCompress.Common;
using SharpCompress.Archives;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using OptiscalerClient.Helpers;

namespace OptiscalerClient.Views
{
    public partial class ManualDownloadDialog : Window
    {
        private const string RequiredDllName = "dlssg_to_fsr3_amd_is_better.dll";

        private readonly string _componentName;
        private readonly string _requiredFileName;
        private readonly string _targetCachePath;
        private readonly bool _isUpdate;

        public bool WasSuccessful { get; private set; }
        public string? SelectedPath { get; private set; }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public ManualDownloadDialog()
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
            _componentName = "";
            _requiredFileName = "";
            _targetCachePath = "";
        }

        public ManualDownloadDialog(string componentName, string requiredFileName, string targetCachePath, bool isUpdate = false)
        {
            InitializeComponent();
            DialogDimHelper.Register(this);

            _componentName = componentName;
            _requiredFileName = requiredFileName;
            _targetCachePath = targetCachePath;
            _isUpdate = isUpdate;

            // 100% Flicker-free startup strategy:
            this.Opacity = 0;

            // Re-bind TitleBar dragging
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

            SetupText();
        }

        private void SetupText()
        {
            var txtComponentName = this.FindControl<TextBlock>("TxtComponentName");
            var txtRequiredFile = this.FindControl<TextBlock>("TxtRequiredFile");

            if (txtComponentName != null) txtComponentName.Text = _componentName;
            if (txtRequiredFile != null) txtRequiredFile.Text = _requiredFileName;

            var txtTitle = this.FindControl<TextBlock>("TxtTitle");
            var txtMainInstruction = this.FindControl<TextBlock>("TxtMainInstruction");
            var btnConfirm = this.FindControl<Button>("BtnConfirm");
            var btnSkip = this.FindControl<Button>("BtnSkip");

            if (_isUpdate)
            {
                if (txtTitle != null) txtTitle.Text = GetResourceString("TxtManualUpdateTitle", "🔄 Manual Update Available");
                if (txtMainInstruction != null) txtMainInstruction.Text = GetResourceString("TxtManualUpdateInst", "A new version of NukemFG is available on Nexus Mods.\nPlease download the updated file and select the DLL (or ZIP):");
                if (btnConfirm != null) btnConfirm.Content = GetResourceString("TxtBtnUpdate", "Update");
                if (btnSkip != null) btnSkip.Content = GetResourceString("TxtBtnLater", "Later");
            }
            else
            {
                if (txtTitle != null) txtTitle.Text = GetResourceString("TxtManualReqTitle", "⚠️ Manual File Required");
                if (txtMainInstruction != null) txtMainInstruction.Text = GetResourceString("TxtManualReqInst", "NukemFG cannot be downloaded automatically.\nPlease download the file from Nexus Mods and select the DLL (or ZIP) below:");
                if (btnConfirm != null) btnConfirm.Content = GetResourceString("TxtBtnConfirm", "Confirm");
                if (btnSkip != null) btnSkip.Content = GetResourceString("TxtBtnSkip", "Skip");
            }
        }

        private void BtnOpenLink_Click(object? sender, PointerPressedEventArgs? e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://www.nexusmods.com/site/mods/738?tab=files",
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ManualDownload] Failed to open NexusMods URL: {ex.Message}"); }
        }

        private async void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = $"Select {_requiredFileName}",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("NukemFG DLL") { Patterns = new[] { RequiredDllName } },
                        new FilePickerFileType("ZIP Archive") { Patterns = new[] { "*.zip" } },
                        new FilePickerFileType("All files") { Patterns = new[] { "*.*" } }
                    }
                });

                if (files != null && files.Count > 0)
                {
                    var selectedPath = files[0].Path.LocalPath;

                    if (ValidateSelection(selectedPath, out string? errorMsg))
                    {
                        var txtSelectedPath = this.FindControl<TextBox>("TxtSelectedPath");
                        if (txtSelectedPath != null) txtSelectedPath.Text = selectedPath;

                        SelectedPath = selectedPath;
                        var btnConfirm = this.FindControl<Button>("BtnConfirm");
                        if (btnConfirm != null) btnConfirm.IsEnabled = true;
                    }
                    else
                    {
                        var title = GetResourceString("TxtError", "Error");
                        await new ConfirmDialog(this, title, errorMsg ?? "Unknown error").ShowDialog<object>(this);

                        var btnConfirm = this.FindControl<Button>("BtnConfirm");
                        if (btnConfirm != null) btnConfirm.IsEnabled = false;
                    }
                }
            }
            catch (Exception ex) { DebugWindow.Log($"[ManualDownload] Browse failed: {ex.Message}"); }
        }

        private bool ValidateSelection(string path, out string? errorMsg)
        {
            errorMsg = null;
            try
            {
                if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    if (Path.GetFileName(path).Equals(RequiredDllName, StringComparison.OrdinalIgnoreCase))
                        return true;

                    errorMsg = "Invalid file selected.";
                    return false;
                }

                if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using var archive = ArchiveFactory.OpenArchive(path);
                    bool found = archive.Entries.Any(e =>
                        !e.IsDirectory &&
                        Path.GetFileName(e.Key ?? "").Equals(RequiredDllName, StringComparison.OrdinalIgnoreCase));

                    if (!found)
                        errorMsg = "Required file not found in ZIP.";

                    return found;
                }

                errorMsg = "Unrecognized file type.";
                return false;
            }
            catch (Exception ex)
            {
                errorMsg = $"Failed to read file: {ex.Message}";
                return false;
            }
        }

        private async void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(SelectedPath))
                return;

            try
            {
                if (!Directory.Exists(_targetCachePath))
                    Directory.CreateDirectory(_targetCachePath);

                var destDllPath = Path.Combine(_targetCachePath, RequiredDllName);

                if (SelectedPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(SelectedPath, destDllPath, overwrite: true);
                }
                else if (SelectedPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using var archive = ArchiveFactory.OpenArchive(SelectedPath);
                    var entry = archive.Entries.FirstOrDefault(e =>
                        !e.IsDirectory &&
                        Path.GetFileName(e.Key ?? "").Equals(RequiredDllName, StringComparison.OrdinalIgnoreCase));

                    if (entry == null)
                        throw new FileNotFoundException("Required file not found in ZIP.");

                    entry.WriteToFile(destDllPath, new ExtractionOptions { Overwrite = true });
                }
                else
                {
                    throw new InvalidOperationException("Unsupported file type.");
                }

                WasSuccessful = true;
                _ = CloseAnimated(true);
            }
            catch (Exception ex)
            {
                var title = GetResourceString("TxtError", "Error");
                await new ConfirmDialog(this, title, ex.Message).ShowDialog<object>(this);
            }
        }

        private bool _isAnimatingClose = false;

        private async Task CloseAnimated(bool result)
        {
            if (_isAnimatingClose) return;
            _isAnimatingClose = true;
            DialogDimHelper.HideDimNow(this);
            var rootPanel = this.FindControl<Panel>("RootPanel");
            if (rootPanel != null) rootPanel.Opacity = 0;
            await Task.Delay(220);
            Close(result);
        }

        private void BtnSkip_Click(object sender, RoutedEventArgs e)
        {
            WasSuccessful = false;
            _ = CloseAnimated(false);
        }

        private string GetResourceString(string key, string fallback)
        {
            return Application.Current?.TryFindResource(key, out var res) == true && res is string str ? str : fallback;
        }
    }
}
