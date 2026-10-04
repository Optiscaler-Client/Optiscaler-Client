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
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Win32;
using OptiscalerClient.Helpers;
using OptiscalerClient.Models;
using OptiscalerClient.Views;

namespace OptiscalerClient.Services
{
    /// <summary>
    /// dlssg_for_sm86 (DLSS Frame Generation on RTX 20/30) as a standalone component: eligibility,
    /// install / update / uninstall through the backup store, and its INI. Windows only — obtain it
    /// through <see cref="PlatformServiceFactory.CreateDlssgSm86Service"/>, which returns null elsewhere.
    /// </summary>
    public interface IDlssgSm86Service
    {
        DlssgSm86PackageService Packages { get; }

        /// <summary>The committed install for this game, or null when it isn't installed.</summary>
        DlssgSm86InstallState? GetInstallState(Game game);

        /// <summary>Whether the mod can go into this game, where, and under which proxy names.
        /// <paramref name="targetDirectory"/> is a folder the user picked (Manual Install) or the one
        /// OptiScaler was just installed into; null resolves it automatically. Scans the game folder —
        /// call it off the UI thread.</summary>
        DlssgSm86Eligibility GetEligibility(Game game, string? targetDirectory = null);

        HagsState GetHagsState();

        /// <summary>What the next install does for this selection: install, update, apply only the
        /// multiplier, remove, or nothing. <paramref name="build"/> is null when "None" is selected. Cheap:
        /// no disk access.</summary>
        DlssgSm86ActionPlan GetPendingAction(DlssgSm86Eligibility eligibility, DlssgSm86InstallState? state,
            DlssgSm86BuildEntry? build, int maxGeneratedFrames);

        /// <summary>Downloads (if needed) and installs <paramref name="buildId"/> into
        /// <paramref name="targetDirectory"/> (null: resolved as in <see cref="GetEligibility"/>). Also
        /// performs an update or build switch when the mod is already installed, carrying the user's INI
        /// values over.</summary>
        Task<DlssgSm86InstallResult> InstallAsync(Game game, string buildId, int maxGeneratedFrames,
            IProgress<double>? progress = null, string? targetDirectory = null, CancellationToken cancellationToken = default);

        /// <summary>Rewrites MaxGeneratedFrames in the installed INI (clamped to the build's maximum).</summary>
        DlssgSm86InstallResult SetMaxGeneratedFrames(Game game, int maxGeneratedFrames);

        /// <summary>True when the installed dlssg_sm86.ini differs from what the client last wrote.</summary>
        bool IsIniModified(Game game);

        /// <summary>Removes the mod and restores anything it replaced. Files changed since install are left
        /// alone and reported; <paramref name="keepModifiedIni"/> decides that for the INI.</summary>
        DlssgSm86UninstallResult Uninstall(Game game, bool keepModifiedIni);
    }

    /// <summary>Thrown by <see cref="IDlssgSm86Service.InstallAsync"/> when the game turned out to be blocked
    /// on the re-check (the folder changed after the window last looked).</summary>
    public sealed class DlssgSm86BlockedException : Exception
    {
        public DlssgSm86Eligibility Eligibility { get; }

        public DlssgSm86BlockedException(DlssgSm86Eligibility eligibility)
            : base($"dlssg_for_sm86 cannot be installed: {eligibility.Blocker}.")
        {
            Eligibility = eligibility;
        }
    }

    /// <summary>Thrown when OptiScaler would be installed under a proxy name dlssg_for_sm86 already uses.</summary>
    public sealed class DlssgSm86ProxyCollisionException : Exception
    {
        public DlssgSm86ProxyCollisionException(string injectionDllName, IEnumerable<string> dlssgProxyNames)
            : base(string.Format(
                DlssgSm86Records.GetString("TxtDlssgSm86CollisionMsg",
                    "{0} is already used by DLSS FG for RTX 20/30 in this game folder ({1}). Pick another injection method, or set DLSS FG (RTX 20/30) to None."),
                injectionDllName, string.Join(", ", dlssgProxyNames)))
        {
        }
    }

    /// <summary>
    /// Platform-neutral access to the dlssg_for_sm86 backup-store record. Kept apart from the
    /// Windows-only service so OptiScaler's installer and the analyzer can check it on any OS.
    /// The record lives under its own store key (game root + "::dlssg_sm86", same idea as
    /// DlssNrOnAmdService's "::dlssnr"), so uninstalling OptiScaler — which deletes the whole
    /// per-game entry — never touches it, and the reverse.
    /// </summary>
    public static class DlssgSm86Records
    {
        public const string ComponentId = "dlssg_sm86";
        public const string IniFileName = "dlssg_sm86.ini";
        public const string LogsDirectoryName = "dlssg_sm86";

        /// <summary>Utility proxy names, in the order they're claimed. dxgi.dll / d3d12.dll are on the
        /// render hot path and load-order sensitive upstream, so they're never installed automatically.</summary>
        public static readonly string[] ProxyNames = { "version.dll", "winmm.dll", "dbghelp.dll", "dinput8.dll" };

        public static string StoreKey(Game game)
        {
            var root = !string.IsNullOrEmpty(game.InstallPath)
                ? game.InstallPath
                : Path.GetDirectoryName(game.ExecutablePath ?? string.Empty) ?? string.Empty;
            return root + "::" + ComponentId;
        }

        /// <summary>The record for this game regardless of status (an "in_progress" one is an interrupted
        /// install), or null.</summary>
        public static InstallationManifest? Load(BackupStoreService store, Game game)
        {
            var manifest = store.LoadManifest(StoreKey(game));
            return manifest != null && string.Equals(manifest.ComponentId, ComponentId, StringComparison.Ordinal)
                ? manifest
                : null;
        }

        public static InstallationManifest? LoadCommitted(BackupStoreService store, Game game)
        {
            var manifest = Load(store, game);
            return manifest != null && IsCommitted(manifest) ? manifest : null;
        }

        /// <summary>Proxy names dlssg_for_sm86 occupies in <paramref name="gameDir"/>, or empty.</summary>
        public static IReadOnlyList<string> GetInstalledProxyNames(Game game, string gameDir)
        {
            try
            {
                var manifest = LoadCommitted(new BackupStoreService(), game);
                if (manifest == null || !SameDirectory(manifest.InstalledGameDirectory, gameDir))
                    return Array.Empty<string>();
                return manifest.DlssgSm86ProxyNames;
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[DlssgSm86] Could not read install record: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        internal static bool IsCommitted(InstallationManifest manifest) =>
            string.Equals(manifest.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase);

        internal static bool SameDirectory(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        internal static string GetString(string key, string fallback) =>
            Application.Current?.TryFindResource(key, out var value) == true && value is string text ? text : fallback;
    }

    [SupportedOSPlatform("windows")]
    internal sealed class DlssgSm86Service : IDlssgSm86Service
    {
        private const string TempSuffix = ".dlssg_tmp";

        public DlssgSm86PackageService Packages { get; } = new();

        // ── State ────────────────────────────────────────────────────────────────

        public DlssgSm86InstallState? GetInstallState(Game game)
        {
            var record = DlssgSm86Records.LoadCommitted(new BackupStoreService(), game);
            if (record == null || string.IsNullOrEmpty(record.InstalledGameDirectory))
                return null;

            var iniPath = Path.Combine(record.InstalledGameDirectory, DlssgSm86Records.IniFileName);
            int? maxFrames = int.TryParse(DlssgSm86Ini.GetValue(iniPath, "FrameGeneration", "MaxGeneratedFrames"), out var v) ? v : null;
            return new DlssgSm86InstallState
            {
                Version = record.DlssgSm86Version ?? string.Empty,
                Build = record.DlssgSm86Build ?? string.Empty,
                TargetDirectory = record.InstalledGameDirectory,
                ProxyNames = record.DlssgSm86ProxyNames,
                MaxGeneratedFrames = maxFrames
            };
        }

        public HagsState GetHagsState()
        {
            try
            {
                // HwSchMode: 2 = hardware-accelerated GPU scheduling on, 1 = off. Absent on systems
                // that don't support it or were never configured.
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", null);
                return value is int mode ? (mode == 2 ? HagsState.On : HagsState.Off) : HagsState.Unknown;
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[DlssgSm86] Could not read HAGS state: {ex.Message}");
                return HagsState.Unknown;
            }
        }

        public DlssgSm86ActionPlan GetPendingAction(DlssgSm86Eligibility eligibility, DlssgSm86InstallState? state,
            DlssgSm86BuildEntry? build, int maxGeneratedFrames)
        {
            var modVersion = Packages.Manifest?.ModVersion;
            bool updateAvailable = state != null && !string.Equals(state.Version, modVersion, StringComparison.OrdinalIgnoreCase);

            if (build == null)
            {
                return state == null
                    ? new DlssgSm86ActionPlan(DlssgSm86PendingAction.None, updateAvailable, CanRun: false)
                    : new DlssgSm86ActionPlan(DlssgSm86PendingAction.Remove, updateAvailable, CanRun: true);
            }

            DlssgSm86PendingAction action;
            if (state == null)
                action = DlssgSm86PendingAction.Install;
            else if (updateAvailable || !string.Equals(state.Build, build.Id, StringComparison.OrdinalIgnoreCase))
                action = DlssgSm86PendingAction.Update;
            else if (state.MaxGeneratedFrames != DlssgSm86Multipliers.Clamp(maxGeneratedFrames, build))
                action = DlssgSm86PendingAction.ApplyMultiplier;
            else
                action = DlssgSm86PendingAction.None;

            return new DlssgSm86ActionPlan(action, updateAvailable,
                CanRun: eligibility.IsEligible && action != DlssgSm86PendingAction.None);
        }

        // ── Eligibility ──────────────────────────────────────────────────────────

        public DlssgSm86Eligibility GetEligibility(Game game, string? targetDirectory = null)
        {
            var store = new BackupStoreService();
            var ours = DlssgSm86Records.LoadCommitted(store, game);
            var opti = LoadOptiScalerManifest(store, game);

            var targetDir = targetDirectory != null
                ? (Directory.Exists(targetDirectory) ? targetDirectory : null)
                : ResolveTargetDirectory(game, ours, opti);
            if (targetDir == null)
                return new DlssgSm86Eligibility { Blocker = DlssgSm86Blocker.NoTargetDirectory };

            if (game.HasAntiCheat || AntiCheatHelper.IsPresent(game.InstallPath))
                return new DlssgSm86Eligibility { Blocker = DlssgSm86Blocker.AntiCheat, TargetDirectory = targetDir };

            var caps = new FrameGenerationConfigurationService().DetectCapabilities(game);
            // The mod hooks the game's own Streamline DLSS-G path; DetectCapabilities only looks for
            // nvngx_dlssg.dll, so also accept the Streamline plugin itself.
            bool hasStreamlineDlssG = caps.HasNativeDlssG || ContainsFile(game.InstallPath, "sl.dlss_g.dll");
            if (!hasStreamlineDlssG)
                return new DlssgSm86Eligibility { Blocker = DlssgSm86Blocker.NoDlssG, TargetDirectory = targetDir };
            if (!caps.IsDirectX12 && caps.IsVulkan)
                return new DlssgSm86Eligibility { Blocker = DlssgSm86Blocker.NotDx12, TargetDirectory = targetDir };

            var (free, occupied) = ResolveProxyNames(targetDir, ours, opti);

            // A hand-copied mod (often an older version) already in the folder: adding our proxies
            // next to it would put two builds in one process, so the user removes it first.
            var manual = occupied.Where(o => o.Owner == DlssgSm86ProxyOwner.ManualDlssgSm86).ToList();
            if (manual.Count > 0)
            {
                bool iniIsOurs = ours != null && FindRecord(ours, DlssgSm86Records.IniFileName) != null;
                if (!iniIsOurs && File.Exists(Path.Combine(targetDir, DlssgSm86Records.IniFileName)))
                    manual.Add(new DlssgSm86ProxyOccupant(DlssgSm86Records.IniFileName, DlssgSm86ProxyOwner.ManualDlssgSm86));
                return new DlssgSm86Eligibility { Blocker = DlssgSm86Blocker.ManualInstall, TargetDirectory = targetDir, Occupied = manual };
            }

            return new DlssgSm86Eligibility
            {
                Blocker = free.Count == 0 ? DlssgSm86Blocker.NoFreeProxyName : DlssgSm86Blocker.None,
                TargetDirectory = targetDir,
                FreeProxyNames = free,
                Occupied = occupied
            };
        }

        /// <summary>OptiScaler's own committed record for this game (also covers a FSR 4 swap-only record).</summary>
        private static InstallationManifest? LoadOptiScalerManifest(BackupStoreService store, Game game)
        {
            if (string.IsNullOrEmpty(game.InstallPath)) return null;
            var manifest = store.LoadManifest(game.InstallPath);
            return manifest != null && DlssgSm86Records.IsCommitted(manifest) && manifest.ComponentId == null ? manifest : null;
        }

        /// <summary>The rendering executable's folder, which is where the proxies must sit: our own
        /// record's folder when installed, else wherever OptiScaler went, else the same detection
        /// OptiScaler's installer uses (prefers Unreal's Binaries\Win64).</summary>
        private static string? ResolveTargetDirectory(Game game, InstallationManifest? ours, InstallationManifest? opti)
        {
            if (Directory.Exists(ours?.InstalledGameDirectory)) return ours!.InstalledGameDirectory;
            if (Directory.Exists(opti?.InstalledGameDirectory)) return opti!.InstalledGameDirectory;
            var detected = new GameInstallationService().DetermineInstallDirectory(game);
            return Directory.Exists(detected) ? detected : null;
        }

        private static (List<string> Free, List<DlssgSm86ProxyOccupant> Occupied) ResolveProxyNames(
            string targetDir, InstallationManifest? ours, InstallationManifest? opti)
        {
            var free = new List<string>();
            var occupied = new List<DlssgSm86ProxyOccupant>();

            var ourNames = ours != null && DlssgSm86Records.SameDirectory(ours.InstalledGameDirectory, targetDir)
                ? new HashSet<string>(ours.DlssgSm86ProxyNames, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var optiNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var swapNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (opti != null && DlssgSm86Records.SameDirectory(opti.InstalledGameDirectory, targetDir))
            {
                if (opti.IncludesOptiscaler && !string.IsNullOrEmpty(opti.InjectionMethod))
                    optiNames.Add(opti.InjectionMethod);
                foreach (var path in opti.FilesCreated.Select(f => f.RelativePath)
                             .Concat(opti.FilesOverwritten.Select(f => f.RelativePath))
                             .Concat(opti.InstalledFiles))
                    optiNames.Add(path);
                if (opti.IncludesDllSwap && !string.IsNullOrEmpty(opti.DllSwapTargetFileName))
                    foreach (var name in opti.DllSwapTargetFileName.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                        swapNames.Add(name);
            }

            foreach (var name in DlssgSm86Records.ProxyNames)
            {
                var path = Path.Combine(targetDir, name);
                if (ourNames.Contains(name))
                    free.Add(name);
                else if (swapNames.Contains(name))
                    occupied.Add(new DlssgSm86ProxyOccupant(name, DlssgSm86ProxyOwner.Fsr4Swap));
                else if (optiNames.Contains(name))
                    occupied.Add(new DlssgSm86ProxyOccupant(name, DlssgSm86ProxyOwner.OptiScaler));
                else if (File.Exists(path))
                    occupied.Add(new DlssgSm86ProxyOccupant(name, IsDlssgSm86Proxy(path) ? DlssgSm86ProxyOwner.ManualDlssgSm86 : GuessOwner(targetDir)));
                else
                    free.Add(name);
            }

            // dxgi.dll / d3d12.dll are never ours, but upstream lets users copy the mod in under those
            // names by hand — still a manual install that must not be mixed with ours.
            foreach (var name in ManualOnlyProxyNames)
            {
                var path = Path.Combine(targetDir, name);
                if (!optiNames.Contains(name) && !swapNames.Contains(name) && File.Exists(path) && IsDlssgSm86Proxy(path))
                    occupied.Add(new DlssgSm86ProxyOccupant(name, DlssgSm86ProxyOwner.ManualDlssgSm86));
            }

            return (free, occupied);
        }

        private static readonly string[] ManualOnlyProxyNames = { "dxgi.dll", "d3d12.dll" };

        // Every dlssg_for_sm86 proxy (0.2.x through 0.3.x, either runtime) carries the UTF-16 name of
        // the INI it reads; system DLLs and other loaders don't. Recognises copies of any version,
        // not just the hashes pinned in our manifest.
        private static readonly byte[] ProxySignature = Encoding.Unicode.GetBytes(DlssgSm86Records.IniFileName);

        /// <summary>True when <paramref name="path"/> is a dlssg_for_sm86 proxy DLL (any version).</summary>
        internal static bool IsDlssgSm86Proxy(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
                var buffer = new byte[1 << 20];
                int carried = 0;
                int read;
                while ((read = stream.Read(buffer, carried, buffer.Length - carried)) > 0)
                {
                    int length = carried + read;
                    if (buffer.AsSpan(0, length).IndexOf(ProxySignature) >= 0)
                        return true;
                    // Keep the tail so a signature split across two reads is still found.
                    carried = Math.Min(ProxySignature.Length - 1, length);
                    Buffer.BlockCopy(buffer, length - carried, buffer, 0, carried);
                }
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[DlssgSm86] Could not inspect '{path}': {ex.Message}");
            }
            return false;
        }

        /// <summary>Best guess at what put an unknown proxy DLL into the folder, for the blocker message.</summary>
        private static DlssgSm86ProxyOwner GuessOwner(string targetDir)
        {
            try
            {
                if (Directory.EnumerateFiles(targetDir, "*.asi").Any() ||
                    Directory.Exists(Path.Combine(targetDir, "scripts")) && Directory.EnumerateFiles(Path.Combine(targetDir, "scripts"), "*.asi").Any())
                    return DlssgSm86ProxyOwner.AsiLoader;
                if (Directory.EnumerateFiles(targetDir, "ReShade*.ini").Any() || Directory.Exists(Path.Combine(targetDir, "reshade-shaders")))
                    return DlssgSm86ProxyOwner.ReShade;
            }
            catch { /* unknown */ }
            return DlssgSm86ProxyOwner.Unknown;
        }

        private static bool ContainsFile(string? root, string fileName)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return false;
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
                return Directory.EnumerateFiles(root, fileName, options).Any();
            }
            catch { return false; }
        }

        // ── Install / update ────────────────────────────────────────────────────

        public async Task<DlssgSm86InstallResult> InstallAsync(Game game, string buildId, int maxGeneratedFrames,
            IProgress<double>? progress = null, string? targetDirectory = null, CancellationToken cancellationToken = default)
        {
            var manifest = Packages.Manifest ?? throw new InvalidOperationException("dlssg_for_sm86 manifest is missing.");
            var build = manifest.FindBuild(buildId) ?? throw new ArgumentException($"Unknown dlssg_for_sm86 build '{buildId}'.", nameof(buildId));

            // Re-checked here: what the window showed may be stale (another tool may have dropped a
            // proxy into the folder since).
            var eligibility = await Task.Run(() => GetEligibility(game, targetDirectory), cancellationToken);
            if (!eligibility.IsEligible || eligibility.TargetDirectory == null)
                throw new DlssgSm86BlockedException(eligibility);

            var files = DlssgSm86PackageService.SelectFiles(build, eligibility.FreeProxyNames);
            var cacheDir = await Packages.EnsureFilesAsync(build, files, progress, cancellationToken);

            return await Task.Run(() =>
            {
                var result = InstallFromCache(game, manifest, build, files, cacheDir, eligibility.TargetDirectory, maxGeneratedFrames);
                RefreshGame(game);
                return result;
            }, cancellationToken);
        }

        private DlssgSm86InstallResult InstallFromCache(Game game, DlssgSm86PackageManifest package, DlssgSm86BuildEntry build,
            IReadOnlyList<DlssgSm86FileEntry> files, string cacheDir, string targetDir, int maxGeneratedFrames)
        {
            var store = new BackupStoreService();
            var storeKey = DlssgSm86Records.StoreKey(game);
            var iniPath = Path.Combine(targetDir, DlssgSm86Records.IniFileName);

            // Update / build switch: remember what the user had in their INI, then take the old
            // install out completely (restoring anything it replaced) so the new one starts from the
            // same clean folder a fresh install would. The new files are already downloaded and
            // verified at this point, so what can still fail below is local disk I/O only. The old
            // install may sit in another folder (Manual Install picked a different exe).
            List<(string Section, string Key, string Value)>? carried = null;
            var prior = DlssgSm86Records.Load(store, game);
            if (prior != null)
            {
                var priorIniPath = Path.Combine(prior.InstalledGameDirectory ?? targetDir, DlssgSm86Records.IniFileName);
                if (DlssgSm86Records.IsCommitted(prior) && File.Exists(priorIniPath))
                    carried = DlssgSm86Ini.ReadAll(priorIniPath);
                UninstallRecord(store, storeKey, prior, keepModifiedIni: false);
            }

            var proxyNames = files.Where(f => f.IsProxy).Select(f => f.Name).ToList();
            var record = new InstallationManifest
            {
                OperationId = Guid.NewGuid().ToString("N"),
                OperationStatus = "in_progress",
                StartedAtUtc = DateTime.UtcNow.ToString("O"),
                InstallDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                InstalledGameDirectory = targetDir,
                IncludesOptiscaler = false,
                ComponentId = DlssgSm86Records.ComponentId,
                DlssgSm86Version = package.ModVersion,
                DlssgSm86Build = build.Id,
                DlssgSm86ProxyNames = proxyNames
            };
            // The mod writes its logs under <exeDir>\dlssg_sm86\ at runtime. Only a folder that
            // didn't exist yet (e.g. not left over from a manual install) is ours to remove later.
            if (!Directory.Exists(Path.Combine(targetDir, DlssgSm86Records.LogsDirectoryName)))
                record.InstalledDirectories.Add(DlssgSm86Records.LogsDirectoryName);
            // Persisted before touching the game folder, so an interrupted install can still be undone.
            store.SaveManifest(storeKey, record);

            var staged = new List<string>();
            var created = new List<string>();
            var replaced = new List<string>();
            try
            {
                // Stage everything first: a full disk or a locked folder fails here, before any game
                // file has been touched.
                foreach (var file in files)
                {
                    var tmp = Path.Combine(targetDir, file.Name + TempSuffix);
                    File.Copy(Path.Combine(cacheDir, file.Name), tmp, overwrite: true);
                    staged.Add(tmp);
                }

                foreach (var file in files)
                {
                    var dest = Path.Combine(targetDir, file.Name);
                    var tmp = dest + TempSuffix;
                    if (File.Exists(dest))
                    {
                        // Normally only a leftover INI from a manual install: taken proxy names were
                        // filtered out by eligibility.
                        var preHash = DlssgSm86PackageService.ComputeSha256(dest);
                        if (!store.BackupFile(storeKey, targetDir, file.Name))
                            throw new IOException(string.Format(DlssgSm86Records.GetString("TxtDlssgSm86BackupFailed",
                                "Could not back up {0} before installing."), file.Name));
                        File.Move(tmp, dest, overwrite: true);
                        replaced.Add(file.Name);
                        record.FilesOverwritten.Add(new ManifestFileRecord
                        {
                            RelativePath = file.Name,
                            BackupRelativePath = file.Name,
                            ExistedBefore = true,
                            PreInstallSha256 = preHash
                        });
                    }
                    else
                    {
                        File.Move(tmp, dest);
                        created.Add(file.Name);
                        record.FilesCreated.Add(new ManifestFileRecord { RelativePath = file.Name, ExistedBefore = false });
                    }
                    staged.Remove(tmp);
                    record.InstalledFiles.Add(file.Name);
                }

                if (carried != null)
                    foreach (var (section, key, value) in carried)
                        DlssgSm86Ini.SetValue(iniPath, section, key, value);

                var applied = DlssgSm86Multipliers.Clamp(maxGeneratedFrames, build);
                DlssgSm86Ini.SetValue(iniPath, "FrameGeneration", "MaxGeneratedFrames", applied.ToString());

                foreach (var entry in record.FilesCreated.Concat(record.FilesOverwritten))
                    entry.PostInstallSha256 = DlssgSm86PackageService.ComputeSha256(Path.Combine(targetDir, entry.RelativePath));

                record.OperationStatus = "committed";
                record.FinishedAtUtc = DateTime.UtcNow.ToString("O");
                store.SaveManifest(storeKey, record);

                DebugWindow.Log($"[DlssgSm86] Installed {package.ModVersion} ({build.Id}) into '{targetDir}' as [{string.Join(", ", proxyNames)}], MaxGeneratedFrames={applied}");
                return new DlssgSm86InstallResult(proxyNames, applied, applied != maxGeneratedFrames);
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[DlssgSm86] Install failed, rolling back: {ex.Message}");
                foreach (var path in staged.Concat(created.Select(name => Path.Combine(targetDir, name))))
                {
                    try { DeleteFileWithRetry(path); }
                    catch { /* logged; keep rolling back the rest */ }
                }
                foreach (var name in replaced)
                {
                    try { store.RestoreFile(storeKey, targetDir, name); }
                    catch (Exception restoreEx) { DebugWindow.Log($"[DlssgSm86] Rollback could not restore '{name}': {restoreEx.Message}"); }
                }
                store.DeleteBackup(storeKey);
                throw;
            }
        }

        public DlssgSm86InstallResult SetMaxGeneratedFrames(Game game, int maxGeneratedFrames)
        {
            var store = new BackupStoreService();
            var record = DlssgSm86Records.LoadCommitted(store, game) ?? throw new InvalidOperationException("dlssg_for_sm86 is not installed.");
            var build = Packages.Manifest?.FindBuild(record.DlssgSm86Build);
            var applied = build != null
                ? DlssgSm86Multipliers.Clamp(maxGeneratedFrames, build)
                : Math.Clamp(maxGeneratedFrames, 1, DlssgSm86Multipliers.Default);

            var iniPath = Path.Combine(record.InstalledGameDirectory!, DlssgSm86Records.IniFileName);
            var iniRecord = FindRecord(record, DlssgSm86Records.IniFileName);
            // Only re-baseline the hash when the INI was still exactly what we wrote: a hand-edited INI
            // stays flagged as edited, so uninstall still asks before deleting it.
            bool wasUnmodified = iniRecord != null && File.Exists(iniPath) &&
                                 string.Equals(DlssgSm86PackageService.ComputeSha256(iniPath), iniRecord.PostInstallSha256, StringComparison.OrdinalIgnoreCase);

            DlssgSm86Ini.SetValue(iniPath, "FrameGeneration", "MaxGeneratedFrames", applied.ToString());

            if (wasUnmodified)
            {
                iniRecord!.PostInstallSha256 = DlssgSm86PackageService.ComputeSha256(iniPath);
                store.SaveManifest(DlssgSm86Records.StoreKey(game), record);
            }
            return new DlssgSm86InstallResult(record.DlssgSm86ProxyNames, applied, applied != maxGeneratedFrames);
        }

        public bool IsIniModified(Game game)
        {
            var record = DlssgSm86Records.LoadCommitted(new BackupStoreService(), game);
            var iniRecord = record == null ? null : FindRecord(record, DlssgSm86Records.IniFileName);
            if (iniRecord == null || string.IsNullOrEmpty(record!.InstalledGameDirectory)) return false;
            var iniPath = Path.Combine(record.InstalledGameDirectory, DlssgSm86Records.IniFileName);
            return File.Exists(iniPath) &&
                   !string.Equals(DlssgSm86PackageService.ComputeSha256(iniPath), iniRecord.PostInstallSha256, StringComparison.OrdinalIgnoreCase);
        }

        // ── Uninstall ────────────────────────────────────────────────────────────

        public DlssgSm86UninstallResult Uninstall(Game game, bool keepModifiedIni)
        {
            var store = new BackupStoreService();
            var record = DlssgSm86Records.Load(store, game);
            if (record == null)
                return new DlssgSm86UninstallResult(Array.Empty<string>());

            var kept = UninstallRecord(store, DlssgSm86Records.StoreKey(game), record, keepModifiedIni);
            RefreshGame(game);
            return new DlssgSm86UninstallResult(kept);
        }

        /// <summary>
        /// Undoes one install record: deletes what it created and restores what it replaced, but only
        /// where the file is still exactly what the install wrote. A proxy something else has replaced
        /// since is left in place and reported. Also clears the mod's logs folder (when this install
        /// created it) and any staging
        /// leftovers, then drops the record. %LOCALAPPDATA%\DlssgSm86 (the mod's own runtime cache) is
        /// left alone, as upstream recommends.
        /// </summary>
        private static List<string> UninstallRecord(BackupStoreService store, string storeKey, InstallationManifest record, bool keepModifiedIni)
        {
            var kept = new List<string>();
            var targetDir = record.InstalledGameDirectory;
            if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
            {
                store.DeleteBackup(storeKey);
                return kept;
            }

            bool IsUnchanged(ManifestFileRecord entry, string path) =>
                string.IsNullOrEmpty(entry.PostInstallSha256) ||
                string.Equals(DlssgSm86PackageService.ComputeSha256(path), entry.PostInstallSha256, StringComparison.OrdinalIgnoreCase);

            bool ShouldRemove(ManifestFileRecord entry, string path)
            {
                if (IsUnchanged(entry, path)) return true;
                bool isIni = string.Equals(entry.RelativePath, DlssgSm86Records.IniFileName, StringComparison.OrdinalIgnoreCase);
                if (isIni && !keepModifiedIni) return true;
                kept.Add(entry.RelativePath);
                return false;
            }

            foreach (var entry in record.FilesCreated)
            {
                var path = Path.Combine(targetDir, entry.RelativePath);
                if (File.Exists(path) && ShouldRemove(entry, path))
                    DeleteFileWithRetry(path);
            }

            foreach (var entry in record.FilesOverwritten)
            {
                var path = Path.Combine(targetDir, entry.RelativePath);
                if (File.Exists(path) && !ShouldRemove(entry, path))
                    continue;
                try
                {
                    if (!store.RestoreFile(storeKey, targetDir, entry.RelativePath, entry.BackupRelativePath))
                        DebugWindow.Log($"[DlssgSm86] No backup to restore for '{entry.RelativePath}'.");
                }
                catch (Exception ex) { DebugWindow.Log($"[DlssgSm86] Could not restore '{entry.RelativePath}': {ex.Message}"); }
            }

            // An interrupted install can leave staged copies behind.
            foreach (var name in DlssgSm86Records.ProxyNames.Append(DlssgSm86Records.IniFileName))
            {
                var tmp = Path.Combine(targetDir, name + TempSuffix);
                if (File.Exists(tmp)) DeleteFileWithRetry(tmp);
            }

            // Runtime output only the mod writes (default [Logging] Directory=dlssg_sm86\logs), removed
            // only when the folder didn't exist before this install.
            if (record.InstalledDirectories.Contains(DlssgSm86Records.LogsDirectoryName, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var logsRoot = Path.Combine(targetDir, DlssgSm86Records.LogsDirectoryName);
                    var logs = Path.Combine(logsRoot, "logs");
                    if (Directory.Exists(logs)) Directory.Delete(logs, recursive: true);
                    if (Directory.Exists(logsRoot) && !Directory.EnumerateFileSystemEntries(logsRoot).Any())
                        Directory.Delete(logsRoot);
                }
                catch (Exception ex) { DebugWindow.Log($"[DlssgSm86] Could not remove the mod's logs: {ex.Message}"); }
            }

            store.DeleteBackup(storeKey);
            DebugWindow.Log($"[DlssgSm86] Uninstalled from '{targetDir}'" + (kept.Count > 0 ? $", kept modified: {string.Join(", ", kept)}" : ""));
            return kept;
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static ManifestFileRecord? FindRecord(InstallationManifest record, string name) =>
            record.FilesCreated.Concat(record.FilesOverwritten)
                .FirstOrDefault(f => string.Equals(f.RelativePath, name, StringComparison.OrdinalIgnoreCase));

        private static void RefreshGame(Game game)
        {
            try
            {
                GameAnalyzerService.InvalidateCacheForPath(game.InstallPath);
                new GameAnalyzerService().AnalyzeGame(game, forceRefresh: true);
                GameAnalyzerService.FlushCacheToDisk();
            }
            catch (Exception ex) { DebugWindow.Log($"[DlssgSm86] Re-analysis failed: {ex.Message}"); }
        }

        /// <summary>The game can hold its proxy for a moment after its window closes; retry briefly
        /// instead of silently leaving the file behind.</summary>
        private static void DeleteFileWithRetry(string path, int maxAttempts = 6, int delayMs = 150)
        {
            for (var attempt = 1; ; attempt++)
            {
                try { if (File.Exists(path)) File.Delete(path); return; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= maxAttempts)
                    {
                        DebugWindow.Log($"[DlssgSm86] Could not delete '{path}': {ex.Message}");
                        throw;
                    }
                    Thread.Sleep(delayMs);
                }
            }
        }
    }

    /// <summary>
    /// Minimal line-preserving INI editor for dlssg_sm86.ini: changes one value in place and keeps
    /// every comment, blank line and the file's line endings, so the documented factory INI stays
    /// readable for users who tune it by hand.
    /// </summary>
    internal static class DlssgSm86Ini
    {
        public static string? GetValue(string path, string section, string key)
        {
            if (!File.Exists(path)) return null;
            try
            {
                return ReadAll(path).FirstOrDefault(e =>
                    string.Equals(e.Section, section, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
            }
            catch { return null; }
        }

        public static List<(string Section, string Key, string Value)> ReadAll(string path)
        {
            var result = new List<(string, string, string)>();
            string section = string.Empty;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                result.Add((section, line[..eq].Trim(), line[(eq + 1)..].Trim()));
            }
            return result;
        }

        public static void SetValue(string path, string section, string key, string value)
        {
            // The factory INI is UTF-8 without a BOM; keep whichever form is on disk (an editor may
            // have added one).
            var bytes = File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
            bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var text = Encoding.UTF8.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Length == 0 ? new List<string>() : text.Split(newline).ToList();
            bool endsWithNewline = text.EndsWith(newline);
            if (endsWithNewline) lines.RemoveAt(lines.Count - 1);

            int sectionStart = -1, sectionEnd = lines.Count; // [start, end) = lines after the header
            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if (t.StartsWith('[') && t.EndsWith(']'))
                {
                    if (sectionStart >= 0) { sectionEnd = i; break; }
                    if (string.Equals(t[1..^1].Trim(), section, StringComparison.OrdinalIgnoreCase))
                        sectionStart = i + 1;
                }
            }

            if (sectionStart < 0)
            {
                if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add(string.Empty);
                lines.Add($"[{section}]");
                lines.Add($"{key}={value}");
            }
            else
            {
                int insertAt = sectionStart;
                bool replaced = false;
                for (int i = sectionStart; i < sectionEnd; i++)
                {
                    var t = lines[i].Trim();
                    if (t.Length == 0 || t[0] == ';' || t[0] == '#') continue;
                    insertAt = i + 1;
                    var eq = t.IndexOf('=');
                    if (eq > 0 && string.Equals(t[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = $"{key}={value}";
                        replaced = true;
                        break;
                    }
                }
                if (!replaced) lines.Insert(insertAt, $"{key}={value}");
            }

            var output = new StringBuilder(string.Join(newline, lines));
            if (endsWithNewline || text.Length == 0) output.Append(newline);
            File.WriteAllText(path, output.ToString(), new UTF8Encoding(hasBom));
        }
    }
}
