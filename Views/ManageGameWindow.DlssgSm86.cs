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
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OptiscalerClient.Helpers;
using OptiscalerClient.Models;
using OptiscalerClient.Services;

namespace OptiscalerClient.Views
{
    /// <summary>
    /// "DLSS FG (RTX 20/30)" section of the Experimental zone: dlssg_for_sm86 as a standalone
    /// component with its own Install / Update / Uninstall buttons. All logic lives in
    /// <see cref="IDlssgSm86Service"/>; this file only shows its results and forwards clicks.
    /// </summary>
    public partial class ManageGameWindow
    {
        private IDlssgSm86Service? _dlssgSm86Service;
        private bool _dlssgSm86ServiceResolved;
        private DlssgSm86Eligibility? _dlssgSm86Eligibility;
        private DlssgSm86InstallState? _dlssgSm86State;
        private bool _isPopulatingDlssgSm86;
        private bool _dlssgSm86Busy;

        private IDlssgSm86Service? DlssgSm86Service
        {
            get
            {
                if (!_dlssgSm86ServiceResolved)
                {
                    _dlssgSm86Service = PlatformServiceFactory.CreateDlssgSm86Service();
                    _dlssgSm86ServiceResolved = true;
                }
                return _dlssgSm86Service;
            }
        }

        /// <summary>Shows the section only on Windows, with Experimental Features on, and with an RTX 20/30
        /// as the preferred GPU; otherwise it stays hidden (never shown disabled). Game-level blockers are
        /// shown with their reason instead.</summary>
        private async Task PopulateDlssgSm86Async(ComponentManagementService componentService)
        {
            var panel = this.FindControl<Control>("PanelDlssgSm86");
            if (panel == null) return;

            var service = DlssgSm86Service;
            if (service?.Packages.Manifest == null || !componentService.Config.ShowExperimentalFeatures)
            {
                panel.IsVisible = false;
                return;
            }

            var game = _game;
            var gpuService = _gpuService;
            var defaultGpuId = componentService.Config.DefaultGpuId;
            (bool Capable, DlssgSm86Eligibility? Eligibility, DlssgSm86InstallState? State, HagsState Hags) result;
            try
            {
                result = await Task.Run(() =>
                {
                    var gpu = GpuSelectionHelper.GetPreferredGpu(gpuService, defaultGpuId);
                    if (!GpuSelectionHelper.IsDlssgSm86Capable(gpu))
                        return (false, (DlssgSm86Eligibility?)null, (DlssgSm86InstallState?)null, HagsState.Unknown);
                    return (true, service.GetEligibility(game), service.GetInstallState(game), service.GetHagsState());
                });
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[DlssgSm86] Could not evaluate '{game.Name}': {ex.Message}");
                panel.IsVisible = false;
                return;
            }

            if (!result.Capable)
            {
                panel.IsVisible = false;
                return;
            }

            _dlssgSm86Eligibility = result.Eligibility;
            _dlssgSm86State = result.State;
            panel.IsVisible = true;

            var hagsPanel = this.FindControl<Control>("PanelDlssgSm86Hags");
            if (hagsPanel != null) hagsPanel.IsVisible = result.Hags == HagsState.Off;

            PopulateDlssgSm86Selectors();
            UpdateDlssgSm86Controls();
        }

        private void PopulateDlssgSm86Selectors()
        {
            var manifest = DlssgSm86Service?.Packages.Manifest;
            var cmbBuild = this.FindControl<ComboBox>("CmbDlssgSm86Build");
            if (manifest == null || cmbBuild == null) return;

            _isPopulatingDlssgSm86 = true;
            try
            {
                var previous = (cmbBuild.SelectedItem as ComboBoxItem)?.Tag as string;
                var itemFormat = GetResourceString("TxtDlssgSm86BuildItem", "Runtime {0} (up to {1})");
                cmbBuild.ItemsSource = manifest.Builds
                    .Select(b => new ComboBoxItem
                    {
                        Content = string.Format(itemFormat, b.Id, DlssgSm86Multipliers.Label(b.MaxGeneratedFrames)),
                        Tag = b.Id
                    })
                    .ToList();

                // Installed build first, then whatever the user picked before a refresh, then the
                // manifest's first entry (the newer runtime).
                var wanted = _dlssgSm86State?.Build ?? previous ?? manifest.Builds[0].Id;
                var items = cmbBuild.ItemsSource.Cast<ComboBoxItem>().ToList();
                cmbBuild.SelectedItem = items.FirstOrDefault(i => (string?)i.Tag == wanted) ?? items.FirstOrDefault();

                PopulateDlssgSm86Multipliers(_dlssgSm86State?.MaxGeneratedFrames);
            }
            finally
            {
                _isPopulatingDlssgSm86 = false;
            }
        }

        private void PopulateDlssgSm86Multipliers(int? preferred)
        {
            var cmbMultiplier = this.FindControl<ComboBox>("CmbDlssgSm86Multiplier");
            var build = SelectedDlssgSm86Build();
            if (cmbMultiplier == null || build == null) return;

            var current = preferred ?? SelectedDlssgSm86Multiplier() ?? DlssgSm86Multipliers.Default;
            var items = DlssgSm86Multipliers.For(build)
                .Select(m => new ComboBoxItem { Content = DlssgSm86Multipliers.Label(m), Tag = m })
                .ToList();
            cmbMultiplier.ItemsSource = items;
            var clamped = DlssgSm86Multipliers.Clamp(current, build);
            cmbMultiplier.SelectedItem = items.FirstOrDefault(i => (int)i.Tag! == clamped);
        }

        private DlssgSm86BuildEntry? SelectedDlssgSm86Build()
        {
            var id = (this.FindControl<ComboBox>("CmbDlssgSm86Build")?.SelectedItem as ComboBoxItem)?.Tag as string;
            return DlssgSm86Service?.Packages.Manifest?.FindBuild(id);
        }

        private int? SelectedDlssgSm86Multiplier() =>
            (this.FindControl<ComboBox>("CmbDlssgSm86Multiplier")?.SelectedItem as ComboBoxItem)?.Tag as int?;

        /// <summary>Asks the service what the action button does for the current selection, then sets the
        /// button, the status line and which controls are enabled. Cheap: no folder scan.</summary>
        private void UpdateDlssgSm86Controls()
        {
            var service = DlssgSm86Service;
            var manifest = service?.Packages.Manifest;
            var eligibility = _dlssgSm86Eligibility;
            var state = _dlssgSm86State;
            var build = SelectedDlssgSm86Build();
            var multiplier = SelectedDlssgSm86Multiplier() ?? DlssgSm86Multipliers.Default;

            var btnInstall = this.FindControl<Button>("BtnDlssgSm86Install");
            var btnUninstall = this.FindControl<Button>("BtnDlssgSm86Uninstall");
            var cmbBuild = this.FindControl<ComboBox>("CmbDlssgSm86Build");
            var cmbMultiplier = this.FindControl<ComboBox>("CmbDlssgSm86Multiplier");
            var txtStatus = this.FindControl<TextBlock>("TxtDlssgSm86Status");
            if (service == null || manifest == null || eligibility == null || build == null || btnInstall == null) return;

            bool installed = state != null;
            bool blocked = !eligibility.IsEligible;
            var plan = service.GetPendingAction(eligibility, state, build, multiplier);
            bool actionEnabled = !_dlssgSm86Busy && plan.CanRun;

            btnInstall.Content = plan.Action switch
            {
                DlssgSm86PendingAction.Install when plan.DownloadBytes > 0 =>
                    string.Format(GetResourceString("TxtDlssgSm86Install", "Install ({0} MB)"), Math.Ceiling(plan.DownloadBytes / 1048576.0)),
                DlssgSm86PendingAction.Install => GetResourceString("TxtDlssgSm86InstallCached", "Install"),
                DlssgSm86PendingAction.Update => GetResourceString("TxtDlssgSm86Update", "Update"),
                DlssgSm86PendingAction.ApplyMultiplier => GetResourceString("TxtDlssgSm86Apply", "Apply"),
                _ => GetResourceString("TxtDlssgSm86Installed", "Installed")
            };
            btnInstall.IsEnabled = actionEnabled;
            btnInstall.Classes.Set("BtnSuccess", actionEnabled);
            btnInstall.Classes.Set("BtnBase", !actionEnabled);
            if (btnUninstall != null)
            {
                btnUninstall.IsVisible = installed;
                btnUninstall.IsEnabled = !_dlssgSm86Busy;
            }
            if (cmbBuild != null) cmbBuild.IsEnabled = !_dlssgSm86Busy && !blocked;
            if (cmbMultiplier != null) cmbMultiplier.IsEnabled = !_dlssgSm86Busy && !blocked;

            if (txtStatus != null)
                txtStatus.Text = BuildDlssgSm86StatusText(manifest, eligibility, state, plan.UpdateAvailable);
        }

        private string BuildDlssgSm86StatusText(DlssgSm86PackageManifest manifest, DlssgSm86Eligibility eligibility,
            DlssgSm86InstallState? state, bool updateAvailable)
        {
            var lines = new List<string>();
            if (state != null)
            {
                lines.Add(string.Format(GetResourceString("TxtDlssgSm86StatusInstalled", "Installed: v{0} (runtime {1}) as {2} in {3}"),
                    state.Version, state.Build, string.Join(", ", state.ProxyNames), state.TargetDirectory));
                if (updateAvailable)
                    lines.Add(string.Format(GetResourceString("TxtDlssgSm86StatusUpdateAvailable", "Update available: v{0}"), manifest.ModVersion));
            }

            if (!eligibility.IsEligible)
                lines.Add(DescribeDlssgSm86Blocker(eligibility));
            else if (state == null)
            {
                lines.Add(string.Format(GetResourceString("TxtDlssgSm86StatusReady", "Will be installed as {0} in {1}"),
                    string.Join(", ", eligibility.FreeProxyNames), eligibility.TargetDirectory));
                if (eligibility.Occupied.Count > 0)
                    lines.Add(string.Format(GetResourceString("TxtDlssgSm86StatusSkipped", "Skipped (in use): {0}"),
                        FormatDlssgSm86Occupants(eligibility.Occupied)));
            }

            return string.Join(Environment.NewLine, lines);
        }

        private string DescribeDlssgSm86Blocker(DlssgSm86Eligibility eligibility) =>
            eligibility.Blocker switch
            {
                DlssgSm86Blocker.NoTargetDirectory => GetResourceString("TxtDlssgSm86BlockerNoTargetDirectory", "Could not find the folder of the game's executable."),
                DlssgSm86Blocker.AntiCheat => GetResourceString("TxtDlssgSm86BlockerAntiCheat", "Anti-cheat detected: DLSS FG for RTX 20/30 is disabled for this game."),
                DlssgSm86Blocker.NoDlssG => GetResourceString("TxtDlssgSm86BlockerNoDlssG", "This game doesn't ship DLSS Frame Generation (Streamline), so there is nothing for the mod to enable."),
                DlssgSm86Blocker.NotDx12 => GetResourceString("TxtDlssgSm86BlockerNotDx12", "DLSS FG for RTX 20/30 only works in DirectX 12 games."),
                DlssgSm86Blocker.ManualInstall => string.Format(
                    GetResourceString("TxtDlssgSm86BlockerManualInstall",
                        "dlssg_for_sm86 (manual install) is already in this folder: {0}. Remove those files first, then reopen this window."),
                    string.Join(", ", eligibility.Occupied.Select(o => o.FileName))),
                DlssgSm86Blocker.NoFreeProxyName => string.Format(
                    GetResourceString("TxtDlssgSm86BlockerNoFreeProxyName", "Every loader name the mod can use is already taken: {0}"),
                    FormatDlssgSm86Occupants(eligibility.Occupied)),
                _ => eligibility.Blocker.ToString()
            };

        private string FormatDlssgSm86Occupants(IEnumerable<DlssgSm86ProxyOccupant> occupants) =>
            string.Join(", ", occupants.Select(o => $"{o.FileName} ({o.Owner switch
            {
                DlssgSm86ProxyOwner.OptiScaler => "OptiScaler",
                DlssgSm86ProxyOwner.Fsr4Swap => GetResourceString("TxtDlssgSm86OwnerFsr4Swap", "FSR 4 Swap"),
                DlssgSm86ProxyOwner.AsiLoader => GetResourceString("TxtDlssgSm86OwnerAsiLoader", "ASI loader"),
                DlssgSm86ProxyOwner.ReShade => "ReShade",
                DlssgSm86ProxyOwner.ManualDlssgSm86 => GetResourceString("TxtDlssgSm86OwnerManual", "dlssg_for_sm86, manual install"),
                _ => GetResourceString("TxtDlssgSm86OwnerUnknown", "another file")
            }})"));

        // ── Event handlers ───────────────────────────────────────────────────────

        private void CmbDlssgSm86Build_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_isPopulatingDlssgSm86) return;
            _isPopulatingDlssgSm86 = true;
            try { PopulateDlssgSm86Multipliers(null); }
            finally { _isPopulatingDlssgSm86 = false; }
            UpdateDlssgSm86Controls();
        }

        private void CmbDlssgSm86Multiplier_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_isPopulatingDlssgSm86) return;
            UpdateDlssgSm86Controls();
        }

        private void BtnDlssgSm86HagsSettings_Click(object? sender, RoutedEventArgs e)
        {
            try { PlatformServiceFactory.CreateShellService().OpenUrl("ms-settings:display-advancedgraphics"); }
            catch (Exception ex) { DebugWindow.Log($"[DlssgSm86] Could not open graphics settings: {ex.Message}"); }
        }

        private async void BtnDlssgSm86Install_Click(object? sender, RoutedEventArgs e)
        {
            var service = DlssgSm86Service;
            var build = SelectedDlssgSm86Build();
            var eligibility = _dlssgSm86Eligibility;
            if (service == null || build == null || eligibility == null || _dlssgSm86Busy) return;
            var multiplier = SelectedDlssgSm86Multiplier() ?? DlssgSm86Multipliers.Default;
            var plan = service.GetPendingAction(eligibility, _dlssgSm86State, build, multiplier);
            if (!plan.CanRun) return;
            var game = _game;

            var bdProgress = this.FindControl<Border>("BdProgress");
            var prgDownload = this.FindControl<ProgressBar>("PrgDownload");
            var txtProgressState = this.FindControl<TextBlock>("TxtProgressState");
            var title = GetResourceString("TxtDlssgSm86Title", "DLSS FG (RTX 20/30)");

            SetDlssgSm86Busy(true);
            try
            {
                DlssgSm86InstallResult result;
                if (plan.Action == DlssgSm86PendingAction.ApplyMultiplier)
                {
                    result = await Task.Run(() => service.SetMaxGeneratedFrames(game, multiplier));
                    _ = ShowToastAsync(string.Format(GetResourceString("TxtDlssgSm86AppliedToast", "Max frame generation set to {0}."),
                        DlssgSm86Multipliers.Label(result.MaxGeneratedFrames)));
                }
                else
                {
                    if (bdProgress != null) bdProgress.IsVisible = true;
                    if (prgDownload != null) { prgDownload.IsIndeterminate = false; prgDownload.Value = 0; }
                    var downloadingFmt = GetResourceString("TxtDlssgSm86Downloading", "Downloading DLSS FG for RTX 20/30 ({0})... {1}%");
                    if (txtProgressState != null) txtProgressState.Text = string.Format(downloadingFmt, build.Id, 0);
                    var progress = new Progress<double>(p => Dispatcher.UIThread.Post(() =>
                    {
                        if (prgDownload != null) prgDownload.Value = p;
                        if (txtProgressState != null) txtProgressState.Text = string.Format(downloadingFmt, build.Id, (int)p);
                    }));

                    result = await service.InstallAsync(game, build.Id, multiplier, progress);
                    _ = ShowToastAsync(result.MultiplierClamped
                        ? string.Format(GetResourceString("TxtDlssgSm86ClampedToast",
                            "DLSS FG for RTX 20/30 installed. Runtime {0} supports up to {1}, so the maximum was lowered."),
                            build.Id, DlssgSm86Multipliers.Label(build.MaxGeneratedFrames))
                        : GetResourceString("TxtDlssgSm86InstalledToast", "DLSS FG for RTX 20/30 installed."));
                }
            }
            catch (DlssgSm86BlockedException ex)
            {
                await new ConfirmDialog(this, title, string.Format(
                    GetResourceString("TxtDlssgSm86InstallFailed", "Could not install DLSS FG for RTX 20/30: {0}"), DescribeDlssgSm86Blocker(ex.Eligibility)),
                    isAlert: true).ShowDialog<object>(this);
            }
            catch (DlssgSm86VerificationException ex)
            {
                await new ConfirmDialog(this, title, string.Format(
                    GetResourceString("TxtDlssgSm86VerifyFailed", "A downloaded file failed verification and was deleted: {0}"), ex.Message),
                    isAlert: true).ShowDialog<object>(this);
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[DlssgSm86] Install failed for '{game.Name}': {ex}");
                await new ConfirmDialog(this, title, string.Format(
                    GetResourceString("TxtDlssgSm86InstallFailed", "Could not install DLSS FG for RTX 20/30: {0}"), ex.Message),
                    isAlert: true).ShowDialog<object>(this);
            }
            finally
            {
                if (bdProgress != null) bdProgress.IsVisible = false;
                SetDlssgSm86Busy(false);
                await RefreshAfterDlssgSm86ChangeAsync();
            }
        }

        private async void BtnDlssgSm86Uninstall_Click(object? sender, RoutedEventArgs e)
        {
            var service = DlssgSm86Service;
            if (service == null || _dlssgSm86Busy) return;
            var game = _game;
            var title = GetResourceString("TxtDlssgSm86Title", "DLSS FG (RTX 20/30)");

            var confirmed = await new ConfirmDialog(this, title,
                GetResourceString("TxtDlssgSm86UninstallConfirm",
                    "Remove DLSS FG for RTX 20/30 from this game? Files it replaced are restored. OptiScaler is not affected."),
                confirmText: GetResourceString("TxtDlssgSm86Uninstall", "Uninstall")).ShowDialog<bool>(this);
            if (!confirmed) return;

            SetDlssgSm86Busy(true);
            try
            {
                bool keepIni = false;
                if (await Task.Run(() => service.IsIniModified(game)))
                {
                    var iniDialog = new ConfirmDialog(this, title,
                        GetResourceString("TxtDlssgSm86IniEditedPrompt",
                            "dlssg_sm86.ini was changed after it was installed. Delete it anyway, or keep your edited copy in the game folder?"),
                        confirmText: GetResourceString("TxtDlssgSm86DeleteIni", "Delete"),
                        thirdButtonText: GetResourceString("TxtDlssgSm86KeepIni", "Keep my INI"));
                    var delete = await iniDialog.ShowDialog<bool>(this);
                    if (iniDialog.ThirdButtonClicked) keepIni = true;
                    else if (!delete) return;
                }

                var result = await Task.Run(() => service.Uninstall(game, keepIni));
                if (result.KeptModifiedFiles.Count > 0 && !(keepIni && result.KeptModifiedFiles.Count == 1))
                {
                    await new ConfirmDialog(this, title, string.Format(
                        GetResourceString("TxtDlssgSm86KeptFiles", "These files were changed after installation and were left in place: {0}"),
                        string.Join(", ", result.KeptModifiedFiles)), isAlert: true).ShowDialog<object>(this);
                }
                _ = ShowToastAsync(GetResourceString("TxtDlssgSm86UninstalledToast", "DLSS FG for RTX 20/30 uninstalled."));
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[DlssgSm86] Uninstall failed for '{game.Name}': {ex}");
                await new ConfirmDialog(this, title, string.Format(
                    GetResourceString("TxtDlssgSm86UninstallFailed", "Could not uninstall DLSS FG for RTX 20/30: {0}"), ex.Message),
                    isAlert: true).ShowDialog<object>(this);
            }
            finally
            {
                SetDlssgSm86Busy(false);
                await RefreshAfterDlssgSm86ChangeAsync();
            }
        }

        private void SetDlssgSm86Busy(bool busy)
        {
            _dlssgSm86Busy = busy;
            UpdateDlssgSm86Controls();
        }

        private async Task RefreshAfterDlssgSm86ChangeAsync()
        {
            LoadComponents();
            await PopulateDlssgSm86Async(_cachedComponentService ?? new ComponentManagementService());
        }

        /// <summary>"Detected Components" entry for an installed dlssg_for_sm86.</summary>
        private void AppendDlssgSm86ComponentEntry(ObservableCollection<ComponentEntry> components)
        {
            if (!_game.IsDlssgSm86Installed) return;
            components.Add(new ComponentEntry(
                string.Format(GetResourceString("TxtDlssgSm86Badge", "DLSS FG for RTX 20/30: v{0} ({1})"), _game.DlssgSm86Version, _game.DlssgSm86Build),
                false, false,
                GetResourceString("TxtDlssgSm86BadgeTooltip", "sdli1995/dlssg_for_sm86 — unofficial, experimental, at your own risk")));
        }

        // ── Gamepad ──────────────────────────────────────────────────────────────

        /// <summary>Adds this section's controls to the root navigation grid, between the Experimental
        /// zone's first row (row 4) and the Uninstall/action rows. Explicit neighbours below decide the
        /// actual moves, so sharing row 5 with BtnUninstall doesn't matter.</summary>
        private void AddDlssgSm86NavigationNodes(List<NavigationNode> nodes)
        {
            if (this.FindControl<Control>("PanelDlssgSm86")?.IsVisible != true) return;
            AddRootNode(nodes, "CmbDlssgSm86Build", 5, 2);
            AddRootNode(nodes, "CmbDlssgSm86Multiplier", 5, 4);
            AddRootNode(nodes, "BtnDlssgSm86Install", 5, 5);
            AddRootNode(nodes, "BtnDlssgSm86Uninstall", 5, 6);
            AddRootNode(nodes, "BtnDlssgSm86HagsSettings", 5, 1);
        }

        /// <summary>Explicit gamepad neighbours for this section and for the controls around it, or null
        /// to use the regular map. Every list ends with the target the regular map would pick, so a
        /// hidden or disabled control here never strands focus.</summary>
        private string[]? GetDlssgSm86NeighborCandidates(string currentName, NavigationDirection direction)
        {
            if (this.FindControl<Control>("PanelDlssgSm86")?.IsVisible != true) return null;

            string[] rowAbove = { "CmbAmdNrBridgeVersion", "CmbDlssNrDanielVersion", "CmbSpoofing" };
            return (currentName, direction) switch
            {
                // Into this section from the Experimental row above
                ("CmbRenodxVersion", NavigationDirection.Down) => new[] { "CmbDlssgSm86Build", "BtnDlssgSm86HagsSettings", "BtnFolderCleanup" },
                ("CmbSetupNr", NavigationDirection.Down) => new[] { "CmbDlssgSm86Multiplier", "BtnInstallManual" },
                ("CmbDlssNrDanielVersion", NavigationDirection.Down) => new[] { "CmbAmdNrBridgeVersion", "BtnDlssgSm86Install", "BtnDlssgSm86Uninstall", "BtnUninstall", "BtnInstall" },
                ("CmbAmdNrBridgeVersion", NavigationDirection.Down) => new[] { "BtnDlssgSm86Install", "BtnDlssgSm86Uninstall", "BtnUninstall", "BtnInstall" },

                // Into this section from the action rows below
                ("BtnUninstall", NavigationDirection.Up) => new[] { "BtnDlssgSm86Uninstall", "BtnDlssgSm86Install", "CmbDlssgSm86Multiplier", "CmbAmdNrBridgeVersion", "CmbDlssNrDanielVersion" },
                ("BtnFolderCleanup", NavigationDirection.Up) => new[] { "BtnDlssgSm86HagsSettings", "CmbDlssgSm86Build", "CmbRenodxVersion" },
                ("BtnInstallManual", NavigationDirection.Up) => new[] { "CmbDlssgSm86Multiplier", "CmbSetupNr", "CmbProfile" },

                // The section itself
                ("CmbDlssgSm86Build", NavigationDirection.Up) => new[] { "CmbRenodxVersion", "BtnFrameGeneration" },
                ("CmbDlssgSm86Build", NavigationDirection.Left) => new[] { "BtnOpenFolder" },
                ("CmbDlssgSm86Build", NavigationDirection.Right) => new[] { "CmbDlssgSm86Multiplier", "BtnDlssgSm86Install" },
                ("CmbDlssgSm86Build", NavigationDirection.Down) => new[] { "BtnDlssgSm86HagsSettings", "BtnFolderCleanup" },

                ("CmbDlssgSm86Multiplier", NavigationDirection.Up) => new[] { "CmbSetupNr", "CmbDlssNrDanielVersion", "CmbProfile" },
                ("CmbDlssgSm86Multiplier", NavigationDirection.Left) => new[] { "CmbDlssgSm86Build", "BtnOpenFolder" },
                ("CmbDlssgSm86Multiplier", NavigationDirection.Right) => new[] { "BtnDlssgSm86Install", "BtnDlssgSm86Uninstall", "BtnUninstall" },
                ("CmbDlssgSm86Multiplier", NavigationDirection.Down) => new[] { "BtnDlssgSm86HagsSettings", "BtnInstallManual" },

                ("BtnDlssgSm86Install", NavigationDirection.Up) => rowAbove,
                ("BtnDlssgSm86Install", NavigationDirection.Left) => new[] { "CmbDlssgSm86Multiplier", "CmbDlssgSm86Build" },
                ("BtnDlssgSm86Install", NavigationDirection.Right) => new[] { "BtnDlssgSm86Uninstall" },
                ("BtnDlssgSm86Install", NavigationDirection.Down) => new[] { "BtnUninstall", "BtnInstall" },

                ("BtnDlssgSm86Uninstall", NavigationDirection.Up) => rowAbove,
                ("BtnDlssgSm86Uninstall", NavigationDirection.Left) => new[] { "BtnDlssgSm86Install", "CmbDlssgSm86Multiplier" },
                ("BtnDlssgSm86Uninstall", NavigationDirection.Down) => new[] { "BtnUninstall", "BtnInstall" },

                ("BtnDlssgSm86HagsSettings", NavigationDirection.Up) => new[] { "CmbDlssgSm86Build", "CmbDlssgSm86Multiplier" },
                ("BtnDlssgSm86HagsSettings", NavigationDirection.Left) => new[] { "BtnOpenFolder" },
                ("BtnDlssgSm86HagsSettings", NavigationDirection.Right) => new[] { "BtnUninstall", "BtnInstallManual" },
                ("BtnDlssgSm86HagsSettings", NavigationDirection.Down) => new[] { "BtnFolderCleanup", "BtnInstallManual" },

                _ => null
            };
        }
    }
}
