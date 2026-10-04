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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OptiscalerClient.Helpers;
using OptiscalerClient.Models;
using OptiscalerClient.Services;

namespace OptiscalerClient.Views
{
    /// <summary>
    /// "DLSS FG (RTX 20/30)" section of the Experimental zone: dlssg_for_sm86, selected here and installed
    /// by the main Auto/Manual Install buttons together with OptiScaler and the FSR 4 Swap (or on its own
    /// when both are "None"). The main Uninstall removes it too. All decisions live in
    /// <see cref="IDlssgSm86Service"/>; this file shows its results and runs the steps it plans.
    /// </summary>
    public partial class ManageGameWindow
    {
        private const string DlssgSm86NoneTag = "none";

        private IDlssgSm86Service? _dlssgSm86Service;
        private bool _dlssgSm86ServiceResolved;
        private DlssgSm86Eligibility? _dlssgSm86Eligibility;
        private DlssgSm86InstallState? _dlssgSm86State;
        private bool _isPopulatingDlssgSm86;

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
        /// as the preferred GPU; otherwise it stays hidden (never shown disabled) and the install buttons
        /// leave DLSS FG alone. Game-level blockers are shown with their reason instead.</summary>
        private async Task PopulateDlssgSm86Async(ComponentManagementService componentService)
        {
            var panel = this.FindControl<Control>("PanelDlssgSm86");
            if (panel == null) return;

            var service = DlssgSm86Service;
            if (service?.Packages.Manifest == null || !componentService.Config.ShowExperimentalFeatures)
            {
                HideDlssgSm86Panel(panel);
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
                HideDlssgSm86Panel(panel);
                return;
            }

            if (!result.Capable)
            {
                HideDlssgSm86Panel(panel);
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

        private void HideDlssgSm86Panel(Control panel)
        {
            panel.IsVisible = false;
            _dlssgSm86Eligibility = null;
            UpdateInstallButtonsForSwapState();
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
                var items = new List<ComboBoxItem>
                {
                    new() { Content = GetResourceString("TxtDlssgSm86None", "None"), Tag = DlssgSm86NoneTag, Classes = { "SentinelOption" } }
                };
                items.AddRange(manifest.Builds.Select(b => new ComboBoxItem
                {
                    Content = string.Format(itemFormat, b.Id, DlssgSm86Multipliers.Label(b.MaxGeneratedFrames)),
                    Tag = b.Id
                }));
                cmbBuild.ItemsSource = items;

                // The installed build first. Otherwise opt-in, like the other experimental components:
                // "None", unless the user already picked a build before this refresh. A blocked game
                // stays at "None".
                var wanted = _dlssgSm86State?.Build
                             ?? (_dlssgSm86Eligibility?.IsEligible == true ? previous : null)
                             ?? DlssgSm86NoneTag;
                cmbBuild.SelectedItem = items.FirstOrDefault(i => (string?)i.Tag == wanted) ?? items[0];

                PopulateDlssgSm86Multipliers(_dlssgSm86State?.MaxGeneratedFrames);
            }
            finally
            {
                _isPopulatingDlssgSm86 = false;
            }
        }

        /// <summary>Fills the multiplier combo for the selected build, or for the first one while "None" is
        /// selected (the combo is disabled then, but doesn't look empty).</summary>
        private void PopulateDlssgSm86Multipliers(int? preferred)
        {
            var cmbMultiplier = this.FindControl<ComboBox>("CmbDlssgSm86Multiplier");
            var build = SelectedDlssgSm86Build() ?? DlssgSm86Service?.Packages.Manifest?.Builds.FirstOrDefault();
            if (cmbMultiplier == null || build == null) return;

            var current = preferred ?? (cmbMultiplier.SelectedItem as ComboBoxItem)?.Tag as int? ?? DlssgSm86Multipliers.Default;
            var items = DlssgSm86Multipliers.For(build)
                .Select(m => new ComboBoxItem { Content = DlssgSm86Multipliers.Label(m), Tag = m })
                .ToList();
            cmbMultiplier.ItemsSource = items;
            var clamped = DlssgSm86Multipliers.Clamp(current, build);
            cmbMultiplier.SelectedItem = items.FirstOrDefault(i => (int)i.Tag! == clamped);
        }

        /// <summary>The selected build, or null for "None" (and while the section is hidden).</summary>
        private DlssgSm86BuildEntry? SelectedDlssgSm86Build()
        {
            if (this.FindControl<Control>("PanelDlssgSm86")?.IsVisible != true) return null;
            var id = (this.FindControl<ComboBox>("CmbDlssgSm86Build")?.SelectedItem as ComboBoxItem)?.Tag as string;
            return id == DlssgSm86NoneTag ? null : DlssgSm86Service?.Packages.Manifest?.FindBuild(id);
        }

        private int SelectedDlssgSm86Multiplier() =>
            (this.FindControl<ComboBox>("CmbDlssgSm86Multiplier")?.SelectedItem as ComboBoxItem)?.Tag as int?
            ?? DlssgSm86Multipliers.Default;

        /// <summary>What the next Auto/Manual Install does with DLSS FG, decided by the service. Null while
        /// the section is hidden: the install buttons then leave DLSS FG alone.</summary>
        private DlssgSm86ActionPlan? GetDlssgSm86Plan()
        {
            var service = DlssgSm86Service;
            var eligibility = _dlssgSm86Eligibility;
            if (service == null || eligibility == null || this.FindControl<Control>("PanelDlssgSm86")?.IsVisible != true)
                return null;
            return service.GetPendingAction(eligibility, _dlssgSm86State, SelectedDlssgSm86Build(), SelectedDlssgSm86Multiplier());
        }

        /// <summary>DLSS FG has something to install, update or apply. On its own, that's enough to enable
        /// the install buttons while OptiScaler and the FSR 4 Swap are both "None".</summary>
        private bool HasDlssgSm86PendingInstall() =>
            GetDlssgSm86Plan() is { CanRun: true } plan && plan.Action != DlssgSm86PendingAction.Remove;

        /// <summary>Any DLSS FG change, removal included. Rules out "Update config only", which doesn't
        /// touch DLSS FG.</summary>
        private bool HasDlssgSm86PendingChange() => GetDlssgSm86Plan()?.CanRun == true;

        /// <summary>Sets which selectors are enabled and the info boxes, then lets the main install buttons
        /// pick up the new selection. Cheap: no folder scan.</summary>
        private void UpdateDlssgSm86Controls()
        {
            var manifest = DlssgSm86Service?.Packages.Manifest;
            var eligibility = _dlssgSm86Eligibility;
            if (manifest == null || eligibility == null) return;

            bool blocked = !eligibility.IsEligible;
            var cmbBuild = this.FindControl<ComboBox>("CmbDlssgSm86Build");
            var cmbMultiplier = this.FindControl<ComboBox>("CmbDlssgSm86Multiplier");
            if (cmbBuild != null) cmbBuild.IsEnabled = !blocked;
            if (cmbMultiplier != null) cmbMultiplier.IsEnabled = !blocked && SelectedDlssgSm86Build() != null;

            var blockedPanel = this.FindControl<Control>("PanelDlssgSm86Blocked");
            var blockedText = this.FindControl<TextBlock>("TxtDlssgSm86BlockedText");
            if (blockedPanel != null) blockedPanel.IsVisible = blocked;
            if (blockedText != null) blockedText.Text = blocked ? DescribeDlssgSm86Blocker(eligibility) : string.Empty;

            var updatePanel = this.FindControl<Control>("PanelDlssgSm86Update");
            var updateText = this.FindControl<TextBlock>("TxtDlssgSm86UpdateText");
            bool updateAvailable = GetDlssgSm86Plan()?.UpdateAvailable == true;
            if (updatePanel != null) updatePanel.IsVisible = updateAvailable;
            if (updateText != null)
                updateText.Text = updateAvailable
                    ? string.Format(GetResourceString("TxtDlssgSm86StatusUpdateAvailable", "Update available: v{0}"), manifest.ModVersion)
                    : string.Empty;

            UpdateInstallButtonsForSwapState();
            RefreshInstallActionAvailability();
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

        // ── Install (run by ExecuteInstallAsync) ─────────────────────────────────

        /// <summary>The DLSS FG step of an Auto/Manual Install, after OptiScaler and the FSR 4 Swap, or on
        /// its own: install, update or apply the multiplier, as the service's plan says. Goes into
        /// <paramref name="targetDirectory"/> when given (the folder OptiScaler just went into, or the one
        /// picked for Manual Install). True when something changed. Failures are shown here and don't undo
        /// the other components.</summary>
        private async Task<bool> RunDlssgSm86InstallStepAsync(string? targetDirectory, bool showToast)
        {
            var service = DlssgSm86Service;
            var build = SelectedDlssgSm86Build();
            var plan = GetDlssgSm86Plan();
            if (service == null || build == null || plan is not { CanRun: true } || plan.Action == DlssgSm86PendingAction.Remove)
                return false;
            var multiplier = SelectedDlssgSm86Multiplier();
            var game = _game;

            var bdProgress = this.FindControl<Border>("BdProgress");
            var prgDownload = this.FindControl<ProgressBar>("PrgDownload");
            var txtProgressState = this.FindControl<TextBlock>("TxtProgressState");
            var title = GetResourceString("TxtDlssgSm86Title", "DLSS FG (RTX 20/30)");

            SetDlssgSm86Busy(true);
            try
            {
                if (plan.Action == DlssgSm86PendingAction.ApplyMultiplier)
                {
                    var applied = await Task.Run(() => service.SetMaxGeneratedFrames(game, multiplier));
                    if (showToast)
                        _ = ShowToastAsync(string.Format(GetResourceString("TxtDlssgSm86AppliedToast", "Max frame generation set to {0}."),
                            DlssgSm86Multipliers.Label(applied.MaxGeneratedFrames)));
                    return true;
                }

                if (bdProgress != null) bdProgress.IsVisible = true;
                if (prgDownload != null) { prgDownload.IsIndeterminate = false; prgDownload.Value = 0; }
                var downloadingFmt = GetResourceString("TxtDlssgSm86Downloading", "Downloading DLSS FG for RTX 20/30 ({0})... {1}%");
                if (txtProgressState != null) txtProgressState.Text = string.Format(downloadingFmt, build.Id, 0);
                var progress = new Progress<double>(p => Dispatcher.UIThread.Post(() =>
                {
                    if (prgDownload != null) prgDownload.Value = p;
                    if (txtProgressState != null) txtProgressState.Text = string.Format(downloadingFmt, build.Id, (int)p);
                }));

                var result = await service.InstallAsync(game, build.Id, multiplier, progress, targetDirectory);
                if (showToast)
                    _ = ShowToastAsync(result.MultiplierClamped
                        ? string.Format(GetResourceString("TxtDlssgSm86ClampedToast",
                            "DLSS FG for RTX 20/30 installed. Runtime {0} supports up to {1}, so the maximum was lowered."),
                            build.Id, DlssgSm86Multipliers.Label(build.MaxGeneratedFrames))
                        : GetResourceString("TxtDlssgSm86InstalledToast", "DLSS FG for RTX 20/30 installed."));
                return true;
            }
            catch (DlssgSm86BlockedException ex)
            {
                await ShowDlssgSm86ErrorAsync(title, string.Format(
                    GetResourceString("TxtDlssgSm86InstallFailed", "Could not install DLSS FG for RTX 20/30: {0}"), DescribeDlssgSm86Blocker(ex.Eligibility)));
                return false;
            }
            catch (DlssgSm86VerificationException ex)
            {
                await ShowDlssgSm86ErrorAsync(title, string.Format(
                    GetResourceString("TxtDlssgSm86VerifyFailed", "A downloaded file failed verification and was deleted: {0}"), ex.Message));
                return false;
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[DlssgSm86] Install failed for '{game.Name}': {ex}");
                await ShowDlssgSm86ErrorAsync(title, string.Format(
                    GetResourceString("TxtDlssgSm86InstallFailed", "Could not install DLSS FG for RTX 20/30: {0}"), ex.Message));
                return false;
            }
            finally
            {
                if (bdProgress != null) bdProgress.IsVisible = false;
                await PopulateDlssgSm86Async(_cachedComponentService ?? new ComponentManagementService());
            }
        }

        private async Task ShowDlssgSm86ErrorAsync(string title, string message)
        {
            var bdProgress = this.FindControl<Border>("BdProgress");
            if (bdProgress != null) bdProgress.IsVisible = false;
            await new ConfirmDialog(this, title, message, isAlert: true).ShowDialog<object>(this);
        }

        /// <summary>Auto/Manual Install with OptiScaler at "None": the DLSS FG step on its own (after the FSR 4
        /// Swap, if one is selected). Manual Install asks for the game's executable first, like OptiScaler's
        /// Manual Install, except when only the multiplier changes.</summary>
        private async Task RunDlssgSm86StandaloneInstallAsync(bool isManualMode)
        {
            string? targetDirectory = null;
            if (isManualMode && GetDlssgSm86Plan()?.Action != DlssgSm86PendingAction.ApplyMultiplier)
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = GetResourceString("TxtDlssgSm86PickExe", "Select the game's executable for DLSS FG (RTX 20/30)"),
                    AllowMultiple = false,
                    FileTypeFilter = new[] { new FilePickerFileType("*.exe") { Patterns = new[] { "*.exe" } } }
                });
                if (files == null || files.Count == 0) return; // cancelled
                targetDirectory = Path.GetDirectoryName(files[0].Path.LocalPath);
            }

            if (await RunDlssgSm86InstallStepAsync(targetDirectory, showToast: true))
                NeedsScan = true;
            UpdateStatus();
            LoadComponents();
        }

        /// <summary>"None" while DLSS FG is installed: an OptiScaler (re)install removes it, the same way it
        /// drops an FSR 4 Swap set to "None". Runs before OptiScaler goes in, so OptiScaler can take a
        /// proxy name DLSS FG held. False when the user cancelled or the removal failed (already shown),
        /// which stops the install.</summary>
        private async Task<bool> RemoveDeselectedDlssgSm86Async()
        {
            if (GetDlssgSm86Plan()?.Action != DlssgSm86PendingAction.Remove) return true;
            if (!await UninstallDlssgSm86Async()) return false;
            NeedsScan = true;
            return true;
        }

        // ── Uninstall (run by the main Uninstall button) ─────────────────────────

        /// <summary>Shows the main Uninstall button while DLSS FG is installed, also without OptiScaler.
        /// Called by UpdateStatus after it set the button up for OptiScaler / the FSR 4 Swap.</summary>
        private void ApplyDlssgSm86UninstallButton(Button? btnUninstall)
        {
            if (btnUninstall == null || !_game.IsDlssgSm86Installed || DlssgSm86Service == null) return;
            btnUninstall.IsVisible = true;
            btnUninstall.IsEnabled = true;
            if (!_game.IsOptiscalerInstalled)
                btnUninstall.Content = GetResourceString("TxtUninstall", "Uninstall");
        }

        /// <summary>Adapts the main Uninstall confirmation: DLSS FG alone gets its own text; otherwise the
        /// OptiScaler / Restore DLL text says DLSS FG goes too.</summary>
        private void AdjustDlssgSm86UninstallConfirm(TextBlock? txtTitle, TextBlock? txtMsg, Button? btnYes)
        {
            if (!_game.IsDlssgSm86Installed || DlssgSm86Service == null) return;
            if (!_game.IsOptiscalerInstalled && !_game.IsFsr4DllSwapped)
            {
                if (txtTitle != null) txtTitle.Text = GetResourceString("TxtConfirmUninstallTitle", "Confirm Uninstall");
                if (txtMsg != null) txtMsg.Text = GetResourceString("TxtDlssgSm86UninstallConfirm",
                    "Remove DLSS FG for RTX 20/30 from this game? Files it replaced are restored. OptiScaler is not affected.");
            }
            else if (txtMsg != null)
            {
                txtMsg.Text += Environment.NewLine + GetResourceString("TxtDlssgSm86AlsoRemoved", "DLSS FG for RTX 20/30 will be removed too.");
            }
            if (btnYes != null) btnYes.Content = GetResourceString("TxtUninstall", "✕ Uninstall");
        }

        /// <summary>The main Uninstall's DLSS FG part, run first: DLSS FG has its own record, which
        /// UninstallOptiScaler never touches. False when the caller should stop: the user cancelled, the
        /// removal failed (already shown), or DLSS FG was the only thing installed (finished here).</summary>
        private async Task<bool> UninstallDlssgSm86WithMainAsync()
        {
            if (!_game.IsDlssgSm86Installed || DlssgSm86Service == null) return true;
            if (!await UninstallDlssgSm86Async()) return false;
            if (_game.IsOptiscalerInstalled || _game.IsFsr4DllSwapped) return true;

            NeedsScan = true;
            UpdateStatus();
            LoadComponents();
            _ = ShowToastAsync(GetResourceString("TxtDlssgSm86UninstalledToast", "DLSS FG for RTX 20/30 uninstalled."));
            return false;
        }

        /// <summary>Removes DLSS FG, asking first whether to keep a hand-edited INI. False when the user
        /// cancelled or it failed (already shown).</summary>
        private async Task<bool> UninstallDlssgSm86Async()
        {
            var service = DlssgSm86Service;
            if (service == null) return false;
            var game = _game;
            var title = GetResourceString("TxtDlssgSm86Title", "DLSS FG (RTX 20/30)");

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
                    else if (!delete) return false;
                }

                var result = await Task.Run(() => service.Uninstall(game, keepIni));
                if (result.KeptModifiedFiles.Count > 0 && !(keepIni && result.KeptModifiedFiles.Count == 1))
                {
                    await new ConfirmDialog(this, title, string.Format(
                        GetResourceString("TxtDlssgSm86KeptFiles", "These files were changed after installation and were left in place: {0}"),
                        string.Join(", ", result.KeptModifiedFiles)), isAlert: true).ShowDialog<object>(this);
                }
                return true;
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[DlssgSm86] Uninstall failed for '{game.Name}': {ex}");
                await ShowDlssgSm86ErrorAsync(title, string.Format(
                    GetResourceString("TxtDlssgSm86UninstallFailed", "Could not uninstall DLSS FG for RTX 20/30: {0}"), ex.Message));
                return false;
            }
            finally
            {
                await PopulateDlssgSm86Async(_cachedComponentService ?? new ComponentManagementService());
            }
        }

        /// <summary>Locks the section's selectors while a DLSS FG step runs; the refresh afterwards unlocks
        /// them. The main buttons are handled by the install / uninstall flow around it.</summary>
        private void SetDlssgSm86Busy(bool busy)
        {
            var cmbBuild = this.FindControl<ComboBox>("CmbDlssgSm86Build");
            var cmbMultiplier = this.FindControl<ComboBox>("CmbDlssgSm86Multiplier");
            if (cmbBuild != null) cmbBuild.IsEnabled = !busy;
            if (cmbMultiplier != null) cmbMultiplier.IsEnabled = !busy;
        }

        /// <summary>"Detected Components" entry for an installed dlssg_for_sm86. It gets its own colour, like
        /// the ViaOptiscaler and Swapped entries, because it doesn't ship with the game.</summary>
        private void AppendDlssgSm86ComponentEntry(ObservableCollection<ComponentEntry> components)
        {
            if (!_game.IsDlssgSm86Installed) return;
            components.Add(new ComponentEntry(
                string.Format(GetResourceString("TxtDlssgSm86Badge", "DLSS FG for RTX 20/30: v{0} ({1})"), _game.DlssgSm86Version, _game.DlssgSm86Build),
                false, false,
                GetResourceString("TxtDlssgSm86BadgeTooltip", "sdli1995/dlssg_for_sm86 — unofficial, experimental, at your own risk"))
            {
                IsAddedMod = true
            });
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
            AddRootNode(nodes, "BtnDlssgSm86HagsSettings", 5, 1);
        }

        /// <summary>Explicit gamepad neighbours for this section and for the controls around it, or null
        /// to use the regular map. Every list ends with the target the regular map would pick, so a
        /// hidden or disabled control here never strands focus.</summary>
        private string[]? GetDlssgSm86NeighborCandidates(string currentName, NavigationDirection direction)
        {
            if (this.FindControl<Control>("PanelDlssgSm86")?.IsVisible != true) return null;

            return (currentName, direction) switch
            {
                // Into this section from the Experimental row above
                ("CmbRenodxVersion", NavigationDirection.Down) => new[] { "CmbDlssgSm86Build", "BtnDlssgSm86HagsSettings", "BtnFolderCleanup" },
                ("CmbSetupNr", NavigationDirection.Down) => new[] { "CmbDlssgSm86Multiplier", "BtnInstallManual" },
                ("CmbDlssNrDanielVersion", NavigationDirection.Down) => new[] { "CmbAmdNrBridgeVersion", "CmbDlssgSm86Multiplier", "BtnUninstall", "BtnInstall" },
                ("CmbAmdNrBridgeVersion", NavigationDirection.Down) => new[] { "CmbDlssgSm86Multiplier", "BtnUninstall", "BtnInstall" },

                // Into this section from the action rows below
                ("BtnUninstall", NavigationDirection.Up) => new[] { "CmbDlssgSm86Multiplier", "CmbAmdNrBridgeVersion", "CmbDlssNrDanielVersion" },
                ("BtnFolderCleanup", NavigationDirection.Up) => new[] { "BtnDlssgSm86HagsSettings", "CmbDlssgSm86Build", "CmbRenodxVersion" },
                ("BtnInstallManual", NavigationDirection.Up) => new[] { "CmbDlssgSm86Multiplier", "CmbSetupNr", "CmbProfile" },

                // The section itself
                ("CmbDlssgSm86Build", NavigationDirection.Up) => new[] { "CmbRenodxVersion", "BtnFrameGeneration" },
                ("CmbDlssgSm86Build", NavigationDirection.Left) => new[] { "BtnOpenFolder" },
                ("CmbDlssgSm86Build", NavigationDirection.Right) => new[] { "CmbDlssgSm86Multiplier", "BtnUninstall" },
                ("CmbDlssgSm86Build", NavigationDirection.Down) => new[] { "BtnDlssgSm86HagsSettings", "BtnFolderCleanup" },

                ("CmbDlssgSm86Multiplier", NavigationDirection.Up) => new[] { "CmbSetupNr", "CmbDlssNrDanielVersion", "CmbProfile" },
                ("CmbDlssgSm86Multiplier", NavigationDirection.Left) => new[] { "CmbDlssgSm86Build", "BtnOpenFolder" },
                ("CmbDlssgSm86Multiplier", NavigationDirection.Right) => new[] { "BtnUninstall", "BtnInstall" },
                ("CmbDlssgSm86Multiplier", NavigationDirection.Down) => new[] { "BtnDlssgSm86HagsSettings", "BtnInstallManual" },

                ("BtnDlssgSm86HagsSettings", NavigationDirection.Up) => new[] { "CmbDlssgSm86Build", "CmbDlssgSm86Multiplier" },
                ("BtnDlssgSm86HagsSettings", NavigationDirection.Left) => new[] { "BtnOpenFolder" },
                ("BtnDlssgSm86HagsSettings", NavigationDirection.Right) => new[] { "BtnUninstall", "BtnInstallManual" },
                ("BtnDlssgSm86HagsSettings", NavigationDirection.Down) => new[] { "BtnFolderCleanup", "BtnInstallManual" },

                _ => null
            };
        }
    }
}
