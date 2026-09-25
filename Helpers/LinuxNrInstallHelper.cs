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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using OptiscalerClient.Models;
using OptiscalerClient.Services;
using OptiscalerClient.Views;

namespace OptiscalerClient.Helpers;

/// <summary>
/// Installs danielblnc's NR mod on Linux through bulacha3's fork (DlssNrLinuxWrapperService) and
/// handles the dialogs around it — missing host tools, closing Steam, the game's Steam launch
/// options. Shared by Manage, Quick Install and Bulk Install so all three behave the same.
/// </summary>
public static class LinuxNrInstallHelper
{
    private static string Str(string key, string fallback) =>
        Application.Current?.FindResource(key) as string ?? fallback;

    /// <summary>Installs the mod into <paramref name="gameDir"/> (auto-detected when null) and
    /// records it on <paramref name="game"/>. <paramref name="version"/> null/empty means the latest
    /// fork release (or an already-downloaded one offline). Asks only for what can't be avoided:
    /// nvngx_dlssnr.dll the first time ever, and a runner when none is compatible. Every failure is
    /// shown to the user; the return value says whether it installed and in which folder.</summary>
    public static async Task<(bool Success, string? GameDir)> InstallAsync(Window owner, Game game, string? version,
        bool withOptiScaler, string? gameDir = null, string? gameExe = null, Action<string>? status = null)
    {
        var fork = new DlssNrLinuxWrapperService();
        var mod = new DlssNrOnAmdService();

        // Offline / GitHub rate limit leaves nothing picked — fall back to the latest listed or
        // already-downloaded release, and stop with a clear message instead of installing "v".
        if (string.IsNullOrEmpty(version))
        {
            version = (await fork.GetReleasesAsync(forceRefresh: true)).FirstOrDefault()?.Version
                ?? fork.GetDownloadedVersions().OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (string.IsNullOrEmpty(version))
            {
                await Alert(owner, Str("TxtError", "Error"), Str("TxtSetupNrLinuxWrapperNoRelease",
                    "Could not get the list of Linux fork versions from GitHub. You may be offline or have reached GitHub's hourly request limit — try again in a while."));
                return (false, null);
            }
        }

        if (!mod.IsNvngxDlssNrCached())
        {
            var picker = new DlssNrOnAmdWizardWindow(owner, game, version, withOptiScaler, nvngxPickerOnly: true);
            await picker.ShowDialog<bool>(owner);
            if (!picker.Succeeded) return (false, null);
        }

        var installService = new GameInstallationService();
        if (gameDir == null)
        {
            gameDir = installService.DetermineInstallDirectory(game);
            // Same main-exe choice as the folder above; handed to the fork as --exe (its own guess
            // refuses folders with several executables).
            gameExe = installService.DetermineMainExecutable(game);
            if (gameExe != null && !string.Equals(Path.GetDirectoryName(gameExe), gameDir, StringComparison.Ordinal))
                gameExe = null;
        }
        if (gameDir == null)
        {
            await Alert(owner, Str("TxtError", "Error"), Str("TxtSetupNrCannotResolveDir", "Could not resolve the game folder."));
            return (false, null);
        }

        // The fork's installer itself needs Python 3.11+ — check before downloading anything.
        if (await DlssNrLinuxWrapperService.ResolvePythonAsync() == null)
        {
            await ShowRequirementAsync(owner, LinuxForkRequirement.Python311);
            return (false, null);
        }

        string extractedDir;
        try
        {
            status?.Invoke(Str("TxtSetupNrLinuxWrapperDownloading", "Downloading the Linux fork..."));
            await fork.DownloadAsync(version);
            status?.Invoke(Str("TxtSetupNrLinuxWrapperExtracting", "Extracting the fork into the game folder..."));
            extractedDir = fork.ExtractToGameDir(version, gameDir);
        }
        catch (Exception ex)
        {
            DebugWindow.Log($"[SetupNr] Could not download/extract the Linux fork: {ex.Message}");
            await Alert(owner, Str("TxtError", "Error"), string.Format(
                Str("TxtSetupNrLinuxWrapperDownloadFailedFormat", "Could not download the Linux fork: {0}"), ex.Message));
            return (false, null);
        }

        // Never asks: the game's Steam compat tool, Steam's default, Proton-CachyOS on CachyOS, or
        // the first compatible one (see PickDefaultRunner). Resolved every time so a Proton change
        // in the game's Steam properties is picked up.
        status?.Invoke(Str("TxtSetupNrLinuxWrapperDetectingRunner", "Detecting your Proton/Wine runner..."));
        var runners = await fork.ListRunnersAsync(extractedDir);
        var chosen = DlssNrLinuxWrapperService.PickDefaultRunner(runners, game.Platform == GamePlatform.Steam ? game.AppId : null);
        string runnerPath;
        if (chosen != null)
        {
            runnerPath = chosen.Path;
            DebugWindow.Log($"[SetupNr] Using Proton/Wine runner '{runnerPath}' for the Linux fork.");
        }
        else
        {
            // Nothing compatible — the picker only shows why each runner was rejected.
            var runnerPicker = new DlssNrLinuxWrapperRunnerPickerWindow(owner, runners);
            var picked = await runnerPicker.ShowDialog<bool>(owner);
            if (!picked || string.IsNullOrEmpty(runnerPicker.SelectedPath)) return (false, null);
            runnerPath = runnerPicker.SelectedPath;
        }
        game.DlssNrLinuxWrapperRunnerPath = runnerPath;

        // Both model inputs when available — the service picks the raw DLL or the converted weights
        // (see DlssNrLinuxWrapperService.ChooseModelInput).
        status?.Invoke(Str("TxtSetupNrLinuxWrapperInstalling", "Installing (Linux fork)..."));
        var result = await fork.RunAutoInstallAsync(extractedDir, gameDir,
            weightsPath: mod.IsModeBOutputCached() ? mod.CachedModeBWeightsPath : null,
            nvidiaDllPath: mod.IsNvngxDlssNrCached() ? mod.CachedNvngxDlssNrPath : null,
            runnerPath: runnerPath,
            exePath: gameExe);
        if (!result.Success)
        {
            // A missing gcc/g++ (optimized RDNA4 backend) or Python gets a copyable install command
            // instead of the installer's raw message.
            var missing = DlssNrLinuxWrapperService.DetectMissingRequirement(result.RawError);
            if (missing != null)
            {
                await ShowRequirementAsync(owner, missing.Value);
                return (false, null);
            }
            await Alert(owner, Str("TxtError", "Error"), string.Format(
                Str("TxtSetupNrLinuxWrapperFailedFormat", "The Linux fork installer failed: {0}"), result.RawError ?? "unknown error"));
            return (false, null);
        }

        game.PendingDlssNrOnAmdMode = null;
        game.PendingDlssNrOnAmdVersion = null;
        game.IsDlssNrOnAmdInstalled = true;
        game.DlssNrOnAmdVersion = version;
        game.InstalledDlssNrOnAmdMode = withOptiScaler ? AmdNrBridgeService.BridgeMode : "daniel-only";
        game.DlssNrLinuxWrapperLaunchCommand = result.LaunchOptions
            ?? (string.IsNullOrEmpty(result.CommandPrefix) ? null : result.CommandPrefix + " %command%");
        return (true, gameDir);
    }

    /// <summary>After OptiScaler was installed next to the mod on Linux: its OptiScaler.ini values
    /// (AmdNrBridgeService.LinuxModSettings), the game recorded as "Mod + OptiScaler", and the Steam
    /// launch options with the swapchain-queue variable and a native override for
    /// <paramref name="optiScalerDll"/>. No-op when the mod isn't installed.</summary>
    public static async Task FinishWithOptiScalerAsync(Window owner, Game game, string gameDir, string optiScalerDll)
    {
        if (!game.IsDlssNrOnAmdInstalled) return;
        game.InstalledDlssNrOnAmdMode = AmdNrBridgeService.BridgeMode;
        AmdNrBridgeService.ApplyLinuxModSettings(gameDir);
        await ApplyLaunchOptionsAsync(owner, game, optiScalerDll);
    }

    /// <summary>Tells the user which host tool the Linux fork is missing and, when the
    /// distribution is recognised, gives the exact command to paste in a terminal.</summary>
    public static async Task ShowRequirementAsync(Window owner, LinuxForkRequirement requirement)
    {
        var reason = requirement == LinuxForkRequirement.BuildTools
            ? Str("TxtSetupNrLinuxReqBuildTools", "The Linux fork needs gcc and g++ to build its optimized backend for your GPU.")
            : Str("TxtSetupNrLinuxReqPython", "The Linux fork needs Python 3.11 or newer.");
        var command = DlssNrLinuxWrapperService.GetInstallCommand(requirement);
        var next = command != null
            ? Str("TxtSetupNrLinuxReqRunCommand", "Paste this command in a terminal, then run the install again:")
            : Str("TxtSetupNrLinuxReqNoCommand", "Install it with your distribution's package manager, then run the install again.");
        DebugWindow.Log($"[SetupNr] Linux fork requirement missing: {requirement} (suggested command: {command ?? "none"}).");
        await new ConfirmDialog(owner, Str("TxtSetupNrLinuxReqTitle", "Missing requirements"),
            $"{reason}\n\n{next}", isAlert: true, copyableText: command).ShowDialog<object>(owner);
    }

    /// <summary>Waits for Steam to be closed (it rewrites localconfig.vdf on exit, undoing any edit
    /// made while it runs). False when the user chose to do it by hand instead — the dialog itself
    /// shows the line to copy.</summary>
    private static async Task<bool> WaitForSteamClosedAsync(Window owner, string message, string? copyable)
    {
        while (SteamLaunchOptionsService.IsSteamRunning())
        {
            var retry = await new ConfirmDialog(owner,
                Str("TxtSetupNrSteamCloseTitle", "Close Steam"),
                message,
                confirmText: Str("TxtSetupNrSteamCloseContinueBtn", "Steam is closed, continue"),
                copyableText: copyable).ShowDialog<bool>(owner);
            if (!retry) return false;
        }
        return true;
    }

    /// <summary>Adds the fork's launch wrapper to the game's Steam launch options (plus the
    /// OptiScaler environment when <paramref name="optiScalerDll"/> is given), keeping whatever the
    /// user already has there. Falls back to the manual "paste this into Steam" dialog for non-Steam
    /// games or when Steam's config can't be updated.</summary>
    public static async Task ApplyLaunchOptionsAsync(Window owner, Game game, string? optiScalerDll = null)
    {
        if (string.IsNullOrEmpty(game.DlssNrLinuxWrapperLaunchCommand)) return;
        var launcher = SteamLaunchOptionsService.ExtractLauncher(game.DlssNrLinuxWrapperLaunchCommand);
        var command = launcher != null
            ? SteamLaunchOptionsService.AddLauncher("", launcher, optiScalerDll)
            : game.DlssNrLinuxWrapperLaunchCommand;

        if (game.Platform == GamePlatform.Steam && !string.IsNullOrEmpty(game.AppId) && launcher != null)
        {
            var closed = await WaitForSteamClosedAsync(owner, Str("TxtSetupNrSteamCloseAddMsg",
                "The mod runs through a launch script that has to be added to this game's Steam launch options. Close Steam completely so it can be added automatically (your current launch options are kept) — or copy the line below and add it yourself."),
                command);
            if (!closed) return;
            if (SteamLaunchOptionsService.TryAddLauncher(game.AppId, launcher, optiScalerDll, out var final))
            {
                game.DlssNrLinuxWrapperLaunchCommand = final;
                DebugWindow.Log($"[SetupNr] Steam launch options for {game.Name}: {final}");
                return;
            }
        }

        await new ConfirmDialog(owner, Str("TxtSetupNrLinuxWrapperLaunchCommandTitle", "Paste this into Steam"),
            Str("TxtSetupNrLinuxWrapperLaunchCommandBody",
                "The mod runs through a small launch script generated by the Linux fork. If you're using Steam, paste this into this game's launch options (Properties → General → Launch Options):"),
            isAlert: true, copyableText: command).ShowDialog<object>(owner);
    }

    /// <summary>Removes the fork's wrapper (and the environment added with it) from the game's Steam
    /// launch options, keeping the rest. False when it couldn't (non-Steam game, user declined to
    /// close Steam, config not found) — the caller then tells the user to clear it by hand.</summary>
    public static async Task<bool> RemoveLaunchOptionsAsync(Window owner, Game game)
    {
        if (game.Platform != GamePlatform.Steam || string.IsNullOrEmpty(game.AppId)) return false;
        var closed = await WaitForSteamClosedAsync(owner, Str("TxtSetupNrSteamCloseRemoveMsg",
            "The mod's launch script has to be removed from this game's Steam launch options. Close Steam completely so it can be removed automatically (your other launch options are kept)."),
            null);
        if (!closed) return false;
        return SteamLaunchOptionsService.TryRemoveLauncher(game.AppId, out _);
    }

    /// <summary>Uninstalls the mod from <paramref name="gameDir"/> through the fork and removes its
    /// wrapper from the game's Steam launch options (or tells the user to). False when the fork's own
    /// uninstall failed — the mod is still there then and must stay recorded as installed.</summary>
    public static async Task<bool> UninstallAsync(Window owner, Game game, string gameDir)
    {
        var hadLaunchCommand = !string.IsNullOrEmpty(game.DlssNrLinuxWrapperLaunchCommand);
        if (!await UninstallForkAsync(owner, game, gameDir)) return false;

        // Launch options left pointing at the deleted launch.sh make the game fail to start with
        // nothing on screen explaining why.
        if (hadLaunchCommand && !await RemoveLaunchOptionsAsync(owner, game))
        {
            await Alert(owner, Str("TxtSetupNrLinuxForkRemoveLaunchTitle", "Clear the game's launch options"),
                Str("TxtSetupNrLinuxForkRemoveLaunchBody",
                    "The mod is uninstalled. Remove the launch options you pasted for it (Steam: Properties → General → Launch Options) — they still point at the launch script that was just deleted, and the game won't start until you clear them."));
        }
        game.DlssNrLinuxWrapperLaunchCommand = null;
        return true;
    }

    /// <summary>Runs the fork's own uninstall against <paramref name="gameDir"/> (its journal is
    /// authoritative for what to restore), then removes this app's extracted
    /// "dlssnr-linux-portable" installer folder. Re-extracts the cached tar.gz first when that folder
    /// is gone. On failure nothing is removed and the error is shown — sweeping the mod's ini/weights
    /// anyway used to leave a half-removed install neither this app nor the fork could clean up.</summary>
    public static async Task<bool> UninstallForkAsync(Window owner, Game game, string gameDir)
    {
        var fork = new DlssNrLinuxWrapperService();
        var extractedDir = Path.Combine(gameDir, DlssNrLinuxWrapperService.ExtractedFolderName);
        var succeeded = false;
        try
        {
            if (!Directory.Exists(extractedDir))
            {
                var version = game.DlssNrOnAmdVersion;
                if (string.IsNullOrEmpty(version) || !fork.IsCached(version))
                {
                    DebugWindow.Log("[SetupNr] Linux fork uninstall: no extracted installer and no cached version to re-extract — nothing to do.");
                    return true;
                }
                extractedDir = fork.ExtractToGameDir(version, gameDir);
            }

            var (success, rawError) = await fork.RunUninstallAsync(extractedDir, gameDir);
            if (!success)
            {
                DebugWindow.Log($"[SetupNr] Linux fork uninstall failed, nothing removed: {rawError}");
                await Alert(owner, Str("TxtError", "Error"), string.Format(
                    Str("TxtSetupNrLinuxWrapperUninstallFailedFormat",
                        "The mod could not be uninstalled: {0}\n\nClose the game completely (including Steam's \"Stop\" if it still shows as running) and try again."),
                    rawError ?? "unknown error"));
                return false;
            }

            // Runtime-only logs/pass shaders the fork's journal never tracks.
            DlssNrOnAmdService.SweepRuntimeArtifacts(gameDir);
            succeeded = true;
            return true;
        }
        catch (Exception ex)
        {
            DebugWindow.Log($"[SetupNr] Linux fork uninstall failed: {ex.Message}");
            return false;
        }
        finally
        {
            // Keep the extracted installer after a failure — it's what a retry runs.
            if (succeeded)
            {
                try { if (Directory.Exists(extractedDir)) Directory.Delete(extractedDir, recursive: true); }
                catch (Exception ex) { DebugWindow.Log($"[SetupNr] Could not remove '{extractedDir}': {ex.Message}"); }
            }
        }
    }

    private static Task Alert(Window owner, string title, string message) =>
        new ConfirmDialog(owner, title, message, isAlert: true).ShowDialog<object>(owner);
}
