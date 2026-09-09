// OptiscalerClient - A frontend for managing OptiScaler installations
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
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using OptiscalerClient.Views;

namespace OptiscalerClient.Services;

/// <summary>
/// Adds Windows Defender path exclusions for Setup NR's danielblnc mod — danielblnc's installer is
/// small and unsigned, so Defender sometimes flags/removes it, either during our own download+copy
/// into the game folder or later when the user double-clicks it themselves to run it (that second
/// case is outside this app's process, so we can't catch a failure from it — the only way to
/// actually prevent it is asking up front, before either happens). Exclusions are only ever added on
/// the user's explicit, per-use consent — never run silently. Requires administrator rights, so
/// adding one always triggers a UAC prompt the user can decline; a decline or any other failure
/// (non-Windows, a third-party AV replacing Defender's own PowerShell module, Home-edition quirks,
/// etc.) is treated as "could not add it" rather than an error.
///
/// There is deliberately no "read current exclusions" check anymore: Get-MpPreference now refuses
/// ExclusionPath to a non-admin process on current Windows builds (confirmed on this project's own
/// dev machine — it returns the string "N/A: Must be an administrator to view exclusions" with exit
/// code 0, not an error), so a plain unelevated read can never confirm an exclusion that's actually
/// there. Callers instead persist their own "did I add this" flag (see Game.DlssNrDefenderExclusionAdded)
/// once TryAddExclusionsAsync succeeds, or take the user's word for it via an explicit "I already did this"
/// choice in the UI.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsDefenderExclusionHelper
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED — user declined the UAC prompt

    // Add-MpPreference returns as soon as the preference is written, but Defender's real-time
    // protection engine (MsMpEng.exe) doesn't necessarily pick up the new exclusion instantly — a
    // download/stage/launch that happens immediately after can still get scanned (and the unsigned
    // installer flagged/blocked) under the old exclusion list. Observed in practice: the very next
    // Setup NR attempt right after adding the exclusion fails, but retrying moments later succeeds
    // with no other change. A short grace period after a freshly-added exclusion avoids that race
    // instead of leaving it to the user to notice and retry.
    private static readonly TimeSpan ExclusionPropagationDelay = TimeSpan.FromSeconds(2);

    /// <summary>Attempts to add the given folder paths as Windows Defender exclusions, elevating via
    /// a UAC prompt. Returns false (never throws) if the user declines the prompt, the command fails,
    /// or this isn't actually Windows. On success, waits briefly first (see ExclusionPropagationDelay)
    /// so the exclusion has actually taken effect by the time the caller uses it.</summary>
    public static async Task<bool> TryAddExclusionsAsync(params string[] paths)
    {
        if (!OperatingSystem.IsWindows() || paths.Length == 0) return false;

        try
        {
            var quoted = string.Join(",", paths.Select(p => "'" + p.Replace("'", "''") + "'"));
            var command = $"Add-MpPreference -ExclusionPath {quoted}";
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{command}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            if (!proc.WaitForExit(20000)) return false;
            if (proc.ExitCode != 0) return false;

            await Task.Delay(ExclusionPropagationDelay);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            DebugWindow.Log("[WindowsDefenderExclusion] User declined the elevation prompt.");
            return false;
        }
        catch (Exception ex)
        {
            DebugWindow.Log($"[WindowsDefenderExclusion] Could not add exclusion: {ex.Message}");
            return false;
        }
    }
}
