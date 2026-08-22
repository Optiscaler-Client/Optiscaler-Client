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
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using OptiscalerClient.Helpers;
using OptiscalerClient.Models;
using OptiscalerClient.Views;

namespace OptiscalerClient.Services
{
    public class GameInstallationService
    {
        private const string BackupFolderName = "OptiScalerBackup"; // kept for legacy uninstall fallback
        private const string ManifestFileName = "optiscaler_manifest.json";

        private readonly BackupStoreService _backupStore = new();
        private static readonly string[] KnownOptiscalerArtifacts =
        {
            // OptiScaler core
            "OptiScaler.ini", "OptiScaler.log", "OptiScaler.dll",
            "setup_linux.sh", "setup_windows.bat",
            @"D3D12_Optiscaler\", @"Licences\",
            "!! README_EXTRACT ALL FILES TO GAME FOLDER !!.txt",
            // "dxgi.dll", "winmm.dll", "d3d12.dll", "dbghelp.dll",
            // "version.dll", "wininet.dll", "winhttp.dll",
            // "nvngx.dll", "libxess.dll", "amdxcffx64.dll",
            // Fakenvapi
            "nvapi64.dll", "fakenvapi.ini", "fakenvapi.log", "fakenvapi.dll",
            // NukemFG
            "dlssg_to_fsr3_amd_is_better.dll",
            // FSR 4 INT8 mod (name changed between releases: legacy + current)
            Fsr4Int8DllHelper.LegacyFileName,
            Fsr4Int8DllHelper.CurrentFileName,
            // OptiPatcher
            @"plugins\OptiPatcher.asi"
        };

        private static readonly string[] KnownOptiscalerDirectories =
        {
            "D3D12_Optiscaler",
            "Licenses",
            "plugins",
        };

        // Sensitive files that OptiScaler may place in the game folder but that could also
        // be native game files. Exposed publicly so the UI can present them as opt-in checkboxes.
        public static readonly string[] SensitiveArtifacts =
        {
            "amd_fidelityfx_dx12.dll",
            "amd_fidelityfx_framegeneration_dx12.dll",
            "amd_fidelityfx_vk.dll",
            "dxgi.dll",
            "libxell.dll",
            "libxess.dll",
            "libxess_dx11.dll",
            "libxess_fg.dll",
        };

        // Files that we want to track specifically for backup purposes if they exist in the game folder
        // essentially anything that OptiScaler might replace.
        // We will backup ANYTHING we overwrite, but these are known criticals.
        private readonly string[] _criticalFiles = { "dxgi.dll", "version.dll", "winmm.dll", "nvngx.dll", "nvngx_dlssg.dll", "libxess.dll" };

        /// <summary>Installs OptiScaler and returns the resolved game directory the files were placed in,
        /// so callers installing additional components (FSR4 INT8, OptiPatcher) reuse the same directory
        /// instead of re-running directory detection independently.</summary>
        public string InstallOptiScaler(Game game, string cachePath, string injectionDllName = "dxgi.dll",
                                     bool installFakenvapi = false, string fakenvapiCachePath = "",
                                     bool installNukemFG = false, string nukemFGCachePath = "",
                                     string? optiscalerVersion = null,
                                     string? overrideGameDir = null,
                                     OptiScalerProfile? profile = null,
                                     bool isRdna4 = false, bool isRdna2 = false)
        {
            DebugWindow.Log($"[Install] Starting OptiScaler installation for game: {game.Name}");
            DebugWindow.Log($"[Install] Version: {optiscalerVersion}, Injection: {injectionDllName}");
            DebugWindow.Log($"[Install] Cache path: {cachePath}");

            if (!Directory.Exists(cachePath))
                throw new DirectoryNotFoundException("Updates cache directory not found. Please download OptiScaler first.");

            // Verify cache is not empty
            var cacheFiles = Directory.GetFiles(cachePath, "*.*", SearchOption.AllDirectories);
            if (cacheFiles.Length == 0)
                throw new Exception("Cache directory is empty. Download update again.");

            DebugWindow.Log($"[Install] Cache contains {cacheFiles.Length} files");

            // Determine game directory intelligently (rules for base exe, Phoenix override, or user modal)
            string? gameDir;
            if (overrideGameDir != null)
            {
                gameDir = overrideGameDir;
                DebugWindow.Log($"[Install] Using override game directory: {gameDir}");
            }
            else
            {
                gameDir = DetermineInstallDirectory(game);
                if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
                {
                    throw new Exception("Could not automatically detect the game directory. Please use Manual Install.");
                }
                DebugWindow.Log($"[Install] Detected game directory: {gameDir}");
            }

            if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
                throw new Exception("Installation cancelled or valid directory not found.");

            // storeKey is always the stable game root (game.InstallPath) so that lookup is
            // consistent after app restarts, regardless of which subdirectory was chosen as gameDir.
            var storeKey = game.InstallPath;

            DebugWindow.Log($"[Install] External backup store: {_backupStore.GetBackupRoot(storeKey)}");

            // ── Capture prior manifest BEFORE overwriting (critical for update scenarios) ─────────
            // When updating an existing install, SaveManifest will overwrite the committed manifest
            // before any files are processed. Without this capture, the BackupFile calls below would
            // overwrite original game file backups with OptiScaler's own DLLs, corrupting uninstall.
            // Read the manifest ONCE to avoid redundant file reads (HasValidBackup + LoadManifest).
            var priorManifest = _backupStore.LoadManifest(storeKey);
            bool hasValidBackup = priorManifest != null &&
                string.Equals(priorManifest.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase);
            if (!hasValidBackup) priorManifest = null; // only trust committed manifests

            // Files the game originally owned (backed up during first install) — must NOT be overwritten.
            var priorBackedUpOriginals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Files created by a previous OptiScaler install — must be deleted (not restored) on uninstall.
            var priorCreatedByOptiScaler = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (priorManifest != null)
            {
                foreach (var r in priorManifest.FilesOverwritten)
                    priorBackedUpOriginals.Add(r.RelativePath);
                foreach (var f in priorManifest.BackedUpFiles)
                    priorBackedUpOriginals.Add(f); // legacy v1 fallback
                foreach (var r in priorManifest.FilesCreated)
                    priorCreatedByOptiScaler.Add(r.RelativePath);
                // Legacy v1: files listed in InstalledFiles but not backed up were created by OptiScaler.
                foreach (var f in priorManifest.InstalledFiles)
                    if (!priorBackedUpOriginals.Contains(f))
                        priorCreatedByOptiScaler.Add(f);
                DebugWindow.Log($"[Install] Update mode — preserving {priorBackedUpOriginals.Count} original game file backup(s), tracking {priorCreatedByOptiScaler.Count} OptiScaler-created file(s)");
            }

            // ── Auto-preserve existing OptiScaler.ini settings across updates ─────────
            // Step 2 below copies the new version's OptiScaler.ini over the game folder
            // unconditionally: it's classified as "OptiScaler-created" (priorCreatedByOptiScaler),
            // so the normal backup-before-overwrite guard skips it — correct for DLLs (nothing to
            // restore), wrong for a hand-edited config file (silently destroys user settings with
            // no backup at all). If the caller didn't provide a profile with real overrides, snapshot
            // the current on-disk ini now, before it gets overwritten, so Step 2.5 can merge those
            // values back onto the new template instead of letting the fresh defaults win.
            var effectiveProfile = profile;
            if (priorManifest != null && (effectiveProfile == null || effectiveProfile.IniSettings.Count == 0))
            {
                var existingIniPath = Path.Combine(gameDir, "OptiScaler.ini");
                if (File.Exists(existingIniPath))
                {
                    try
                    {
                        var profileService = new ProfileManagementService();
                        var autoProfile = profileService.CreateProfileFromIni(existingIniPath, "__AutoPreservedOnUpdate");
                        if (autoProfile.IniSettings.Count > 0)
                        {
                            effectiveProfile = autoProfile;
                            DebugWindow.Log($"[Install] Auto-captured {autoProfile.IniSettings.Sum(s => s.Value.Count)} existing OptiScaler.ini setting(s) to preserve across update");
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugWindow.Log($"[Install] Could not auto-capture existing OptiScaler.ini before update: {ex.Message}");
                    }
                }
            }

            // ── Pre-install: detect and remove residues from previous dirty installs ──
            // If there is no valid external backup for this gameDir, any known OptiScaler
            // artifacts found in the game folder are residues (orphaned files from a previous
            // install that was never properly uninstalled). Back them up as original files
            // would corrupt the next uninstall, so we delete them first.
            if (!hasValidBackup)
            {
                var componentService = new ComponentManagementService();
                var cacheDirsForResidue = new List<string>();
                if (!string.IsNullOrEmpty(optiscalerVersion))
                {
                    var p = componentService.GetOptiScalerCachePath(optiscalerVersion);
                    if (Directory.Exists(p)) cacheDirsForResidue.Add(p);
                }
                var fakeDir = componentService.GetFakenvapiCachePath();
                if (Directory.Exists(fakeDir)) cacheDirsForResidue.Add(fakeDir);
                var nukemDir = componentService.GetNukemFGCachePath();
                if (Directory.Exists(nukemDir)) cacheDirsForResidue.Add(nukemDir);

                var residues = _backupStore.FindResiduesInGameDir(gameDir, KnownOptiscalerArtifacts, cacheDirsForResidue);
                foreach (var residue in residues)
                {
                    var residuePath = Path.Combine(gameDir, residue);
                    try
                    {
                        File.Delete(residuePath);
                        DebugWindow.Log($"[Install] Deleted residue from previous install: {residue}");
                    }
                    catch (Exception ex)
                    {
                        DebugWindow.Log($"[Install] Could not delete residue '{residue}': {ex.Message}");
                    }
                }
            }

            // Create installation manifest — OptiscalerVersion is the authoritative source for the UI
            var manifest = new InstallationManifest
            {
                OperationId = Guid.NewGuid().ToString("N"),
                OperationStatus = "in_progress",
                StartedAtUtc = DateTime.UtcNow.ToString("O"),
                InjectionMethod = injectionDllName,
                InstallDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                OptiscalerVersion = optiscalerVersion,
                IncludesOptiscaler = true,
                IncludesFakenvapi = installFakenvapi,
                IncludesNukemFG = installNukemFG,
                // Store the EXACT directory used (already resolved for Phoenix/UE5 games).
                // Uninstall will read this directly, avoiding re-detection issues.
                InstalledGameDirectory = gameDir
            };

            manifest.PreInstallKeyFiles = CapturePreInstallKeySnapshot(gameDir, injectionDllName);
            manifest.ExpectedFinalMarkers.Add(injectionDllName);
            manifest.AppliedProfileName = profile?.Name;

            // For updates, carry over directories installed by the prior run so they are removed
            // on uninstall even though they already existed when this install started.
            if (priorManifest != null)
            {
                foreach (var dir in priorManifest.InstalledDirectories)
                    if (!manifest.InstalledDirectories.Contains(dir))
                        manifest.InstalledDirectories.Add(dir);
            }

            // Persist immediately as in-progress so crashes can be recovered later.
            _backupStore.SaveManifest(storeKey, manifest);

            try
            {

            // Find the main OptiScaler DLL (OptiScaler.dll or nvngx.dll for older versions)
            string? optiscalerMainDll = null;
            foreach (var file in cacheFiles)
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("nvngx.dll", StringComparison.OrdinalIgnoreCase))
                {
                    optiscalerMainDll = file;
                    DebugWindow.Log($"[Install] Found main OptiScaler DLL: {fileName}");
                    break;
                }
            }

            if (optiscalerMainDll == null)
                throw new Exception("Installation failed because the downloaded package is corrupt or incomplete (missing OptiScaler.dll). Please go to Settings -> Manage Cache, delete this version, and try the installation again.");

            // Step 1: Install the main OptiScaler DLL with the selected injection method name
            var injectionDllPath = Path.Combine(gameDir, injectionDllName);
            DebugWindow.Log($"[Install] Installing main DLL as: {injectionDllName}");
            var injectionExisted = File.Exists(injectionDllPath);

            // Backup existing file if it exists (into external store).
            // During an update, skip re-backing-up files that already have an original backup
            // (priorBackedUpOriginals) or that were created by a previous OptiScaler install
            // (priorCreatedByOptiScaler) — doing so would overwrite the original game file.
            bool injIsOriginal = priorBackedUpOriginals.Contains(injectionDllName);
            bool injIsOptiCreated = priorCreatedByOptiScaler.Contains(injectionDllName);
            string? injectionPreHash = null;
            if (injectionExisted && !injIsOriginal && !injIsOptiCreated)
            {
                injectionPreHash = ComputeSha256(injectionDllPath); // only hash when we actually need it
                _backupStore.BackupFile(storeKey, gameDir, injectionDllName);
                manifest.BackedUpFiles.Add(injectionDllName);
                DebugWindow.Log($"[Install] Backed up existing file: {injectionDllName}");
            }

            // Copy OptiScaler.dll as the injection DLL
            File.Copy(optiscalerMainDll, injectionDllPath, true);
            manifest.InstalledFiles.Add(injectionDllName);
            // existedBefore=false for OptiScaler-created files forces them into FilesCreated (delete on uninstall).
            // existedBefore=true for game-original files keeps them in FilesOverwritten (restore on uninstall).
            TrackManifestFileMutation(
                manifest,
                relativePath: injectionDllName,
                existedBefore: injectionExisted && !injIsOptiCreated,
                preInstallHash: injectionPreHash,
                postInstallHash: ComputeSha256(injectionDllPath));
            DebugWindow.Log($"[Install] Installed main OptiScaler DLL");

            // Step 2: Copy all other files (configs, dependencies, etc.)
            DebugWindow.Log($"[Install] Copying additional files...");
            var additionalFileCount = 0;

            foreach (var sourcePath in cacheFiles)
            {
                var fileName = Path.GetFileName(sourcePath);

                // Skip the main OptiScaler DLL as we already handled it
                if (fileName.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("nvngx.dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(cachePath, sourcePath);
                var destPath = Path.Combine(gameDir, relativePath);
                var destDir = Path.GetDirectoryName(destPath);

                // Track created directories
                if (destDir != null && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                    DebugWindow.Log($"[Install] Created directory: {Path.GetRelativePath(gameDir, destDir)}");

                    // Add to manifest (relative to game directory)
                    var relativeDir = Path.GetRelativePath(gameDir, destDir);
                    if (!manifest.InstalledDirectories.Contains(relativeDir))
                    {
                        manifest.InstalledDirectories.Add(relativeDir);
                    }
                }

                // Backup existing file if needed (into external store).
                // During an update, skip files already protected by a prior install's backup.
                bool existedBefore = File.Exists(destPath);
                bool fileIsOriginal = priorBackedUpOriginals.Contains(relativePath);
                bool fileIsOptiCreated = priorCreatedByOptiScaler.Contains(relativePath);
                string? preHash = null;
                if (existedBefore && !fileIsOriginal && !fileIsOptiCreated)
                {
                    preHash = ComputeSha256(destPath); // only hash when we actually need it
                    _backupStore.BackupFile(storeKey, gameDir, relativePath);
                    manifest.BackedUpFiles.Add(relativePath);
                    DebugWindow.Log($"[Install] Backed up existing file: {relativePath}");
                }

                File.Copy(sourcePath, destPath, true);
                manifest.InstalledFiles.Add(relativePath);
                TrackManifestFileMutation(
                    manifest,
                    relativePath: relativePath,
                    existedBefore: existedBefore && !fileIsOptiCreated,
                    preInstallHash: (!fileIsOriginal && !fileIsOptiCreated) ? preHash : null,
                    postInstallHash: ComputeSha256(destPath));
                additionalFileCount++;
            }

            DebugWindow.Log($"[Install] Copied {additionalFileCount} additional files");

            // Step 2.5: Generate OptiScaler.ini from profile if provided (skip for Default profile)
            if (effectiveProfile != null && effectiveProfile.IniSettings.Count > 0)
            {
                try
                {
                    var profileService = new ProfileManagementService();
                    profileService.WriteOptiScalerIniToFile(gameDir, effectiveProfile);
                    DebugWindow.Log($"[Install] Generated OptiScaler.ini from profile: {effectiveProfile.Name}");
                }
                catch (Exception ex)
                {
                    DebugWindow.Log($"[Install] Warning: Failed to generate OptiScaler.ini from profile: {ex.Message}");
                }
            }
            else if (effectiveProfile != null && effectiveProfile.Name == "Default")
            {
                DebugWindow.Log($"[Install] Using Default profile - OptiScaler will use its default configuration");
            }

            // FSR 4.1.1+ (the "Current" amdxcffx64.dll build) does its own internal GPU whitelist check
            // that silently falls back to FSR3 unless ConfigureFsr4IntFallback's 4 keys are forced. Step
            // 2.5 above just (re)generated OptiScaler.ini from whichever profile was applied, which may
            // not carry those keys — so if this game already has that DLL sitting in gameDir from an
            // earlier install, re-force them here. This way switching/reconfiguring a profile can never
            // silently disable FSR4 INT8 for a game that already has it active.
            var existingExtrasDll = Fsr4Int8DllHelper.FindIn(gameDir);
            if (existingExtrasDll != null && !isRdna4 &&
                string.Equals(Path.GetFileName(existingExtrasDll), Fsr4Int8DllHelper.CurrentFileName, StringComparison.OrdinalIgnoreCase))
            {
                ConfigureFsr4IntFallback(gameDir, isRdna4, isRdna2);
                DebugWindow.Log($"[Install] Re-applied FSR4 INT8 forcing keys after profile write (Current extras DLL already present)");
            }

            // Step 3: Install Fakenvapi if requested (AMD/Intel only)
            if (installFakenvapi && !string.IsNullOrEmpty(fakenvapiCachePath) && Directory.Exists(fakenvapiCachePath))
            {
                DebugWindow.Log($"[Install] Installing Fakenvapi...");
                var fakeFiles = Directory.GetFiles(fakenvapiCachePath, "*.*", SearchOption.AllDirectories);
                var fakeFileCount = 0;

                foreach (var sourcePath in fakeFiles)
                {
                    var fileName = Path.GetFileName(sourcePath);

                    // Only copy nvapi64.dll and fakenvapi.ini
                    if (fileName.Equals("nvapi64.dll", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Equals("fakenvapi.ini", StringComparison.OrdinalIgnoreCase))
                    {
                        var destPath = Path.Combine(gameDir, fileName);
                        var existedBefore = File.Exists(destPath);

                        // Backup if exists (into external store).
                        // During an update, skip files already protected by a prior install's backup.
                        bool fakeIsOriginal = priorBackedUpOriginals.Contains(fileName);
                        bool fakeIsOptiCreated = priorCreatedByOptiScaler.Contains(fileName);
                        string? preHash = null;
                        if (existedBefore && !fakeIsOriginal && !fakeIsOptiCreated)
                        {
                            preHash = ComputeSha256(destPath); // only hash when we actually need it
                            _backupStore.BackupFile(storeKey, gameDir, fileName);
                            manifest.BackedUpFiles.Add(fileName);
                            DebugWindow.Log($"[Install] Backed up existing Fakenvapi file: {fileName}");
                        }

                        File.Copy(sourcePath, destPath, true);
                        manifest.InstalledFiles.Add(fileName);
                        TrackManifestFileMutation(
                            manifest,
                            relativePath: fileName,
                            existedBefore: existedBefore && !fakeIsOptiCreated,
                            preInstallHash: (!fakeIsOriginal && !fakeIsOptiCreated) ? preHash : null,
                            postInstallHash: ComputeSha256(destPath));
                        fakeFileCount++;
                        DebugWindow.Log($"[Install] Installed Fakenvapi file: {fileName}");
                    }
                }

                DebugWindow.Log($"[Install] Installed {fakeFileCount} Fakenvapi files");
                if (fakeFileCount > 0)
                {
                    manifest.IncludesFakenvapi = true;
                    manifest.ExpectedFinalMarkers.Add("nvapi64.dll");
                }
                else
                {
                    throw new Exception("Installation failed because the Fakenvapi package is corrupt or incomplete.");
                }
            }

            // Step 4: Install NukemFG if requested
            if (installNukemFG && !string.IsNullOrEmpty(nukemFGCachePath) && Directory.Exists(nukemFGCachePath))
            {
                DebugWindow.Log($"[Install] Installing NukemFG...");
                var nukemFiles = Directory.GetFiles(nukemFGCachePath, "*.*", SearchOption.AllDirectories);
                var nukemFileCount = 0;

                foreach (var sourcePath in nukemFiles)
                {
                    var fileName = Path.GetFileName(sourcePath);

                    // ONLY copy dlssg_to_fsr3_amd_is_better.dll
                    // DO NOT copy nvngx.dll (200kb) - it will break the mod!
                    if (fileName.Equals("dlssg_to_fsr3_amd_is_better.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        var destPath = Path.Combine(gameDir, fileName);
                        var existedBefore = File.Exists(destPath);

                        // Backup if exists (into external store).
                        // During an update, skip files already protected by a prior install's backup.
                        bool nukemIsOriginal = priorBackedUpOriginals.Contains(fileName);
                        bool nukemIsOptiCreated = priorCreatedByOptiScaler.Contains(fileName);
                        string? preHash = null;
                        if (existedBefore && !nukemIsOriginal && !nukemIsOptiCreated)
                        {
                            preHash = ComputeSha256(destPath); // only hash when we actually need it
                            _backupStore.BackupFile(storeKey, gameDir, fileName);
                            manifest.BackedUpFiles.Add(fileName);
                            DebugWindow.Log($"[Install] Backed up existing NukemFG file: {fileName}");
                        }

                        File.Copy(sourcePath, destPath, true);
                        manifest.InstalledFiles.Add(fileName);
                        TrackManifestFileMutation(
                            manifest,
                            relativePath: fileName,
                            existedBefore: existedBefore && !nukemIsOptiCreated,
                            preInstallHash: (!nukemIsOriginal && !nukemIsOptiCreated) ? preHash : null,
                            postInstallHash: ComputeSha256(destPath));
                        nukemFileCount++;
                        DebugWindow.Log($"[Install] Installed NukemFG file: {fileName}");

                        // Wire NukemFG as both the FG input and output in [FrameGen] and enable it —
                        // but only if the game actually ships native DLSS Frame Generation
                        // (nvngx_dlssg.dll, detected by GameAnalyzerService into game.DlssFrameGenVersion).
                        // Per OptiScaler.ini's own docs, FGInput=nukems "Requires DLSSG in the game": it
                        // works by intercepting the game's native DLSS-G call, not by generating frames
                        // on its own. Forcing it on a game that never calls DLSS-G (either because the
                        // game has no DLSS-G support at all, or hides the toggle for non-Nvidia GPUs)
                        // makes OptiScaler wait forever for a signal that will never come, surfacing an
                        // in-game "enable Frame Generation" prompt the user has no way to satisfy
                        // (reported 2026-08-17, e.g. Clair Obscur: Expedition 33 on AMD/Intel GPUs).
                        if (!string.IsNullOrEmpty(game.DlssFrameGenVersion))
                        {
                            ModifyOptiScalerIni(gameDir, "Enabled", "true", "FrameGen");
                            ModifyOptiScalerIni(gameDir, "FGInput", "nukems", "FrameGen");
                            ModifyOptiScalerIni(gameDir, "FGOutput", "nukems", "FrameGen");
                            DebugWindow.Log($"[Install] Modified OptiScaler.ini [FrameGen] for NukemFG");
                        }
                        else
                        {
                            DebugWindow.Log($"[Install] Skipped forcing [FrameGen] for NukemFG — no native DLSS Frame Generation (nvngx_dlssg.dll) detected in game folder, FGInput=nukems would never trigger");
                        }
                    }
                }

                DebugWindow.Log($"[Install] Installed {nukemFileCount} NukemFG files");
                if (nukemFileCount > 0)
                {
                    manifest.IncludesNukemFG = true;
                    manifest.ExpectedFinalMarkers.Add("dlssg_to_fsr3_amd_is_better.dll");
                }
                else
                {
                    throw new Exception("Installation failed because the NukemFG package is corrupt or incomplete.");
                }
            }

            // Save manifest to external store
            manifest.ExpectedFinalMarkers = manifest.ExpectedFinalMarkers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            manifest.OperationStatus = "committed";
            manifest.FinishedAtUtc = DateTime.UtcNow.ToString("O");
            _backupStore.SaveManifest(storeKey, manifest);
            DebugWindow.Log($"[Install] Saved installation manifest to external store");

            // Immediately update the game object so the UI reflects the correct state
            // without waiting for the next full scan/analysis cycle.
            game.IsOptiscalerInstalled = true;
            if (!string.IsNullOrEmpty(optiscalerVersion))
                game.OptiscalerVersion = optiscalerVersion;

            // Post-Install: Re-analyze to refresh DLSS/FSR/XeSS fields.
            // AnalyzeGame will also confirm OptiscalerVersion via the manifest.
            DebugWindow.Log($"[Install] Re-analyzing game to update component information...");
            var analyzer = new GameAnalyzerService();
            GameAnalyzerService.InvalidateCacheForPath(game.InstallPath);
            analyzer.AnalyzeGame(game, forceRefresh: true);
            GameAnalyzerService.FlushCacheToDisk();

            DebugWindow.Log($"[Install] OptiScaler installation completed successfully for {game.Name}");
            DebugWindow.Log($"[Install] Total files installed: {manifest.InstalledFiles.Count}");
            DebugWindow.Log($"[Install] Total files backed up: {manifest.BackedUpFiles.Count}");

            return gameDir;
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[Install] Installation failed. Starting rollback. Error: {ex.Message}");

                manifest.OperationStatus = "failed";
                manifest.FinishedAtUtc = DateTime.UtcNow.ToString("O");
                _backupStore.SaveManifest(storeKey, manifest);

                var backupFilesDir = _backupStore.GetFilesDir(storeKey);
                var rollbackSummary = RollbackFailedInstall(gameDir, backupFilesDir, manifest);
                DebugWindow.Log($"[Install] Rollback completed. Restored={rollbackSummary.Restored}, Deleted={rollbackSummary.Deleted}");

                // Clean up the external store entry for this failed install
                _backupStore.DeleteBackup(storeKey);

                throw new Exception($"{ex.Message}", ex);
            }
        }

        public sealed record DllSwapResult(string TargetFileName, string ExtrasVersion);

        /// <summary>
        /// Replaces targetPath (a FSR4 INT8-related DLL already present in the game's root, one of
        /// Fsr4Int8DllHelper.SwapTargetFileNames) with sourceContentPath's bytes, without touching
        /// OptiScaler or any other file. Backs up the original into the same external backup store
        /// (same storeKey = game.InstallPath) InstallOptiScaler/UninstallOptiScaler use, so a later
        /// UninstallOptiScaler restores it automatically — whether or not OptiScaler ever got
        /// installed on top of this in the meantime (both flags can coexist on one manifest).
        /// </summary>
        public DllSwapResult SwapFsr4Dll(Game game, string targetPath, string sourceContentPath, string extrasVersion)
        {
            if (!File.Exists(targetPath))
                throw new FileNotFoundException($"Target DLL not found: {targetPath}");
            if (!File.Exists(sourceContentPath))
                throw new FileNotFoundException("FSR4 INT8 replacement content not found (download/cache missing).");

            var gameDir = Path.GetDirectoryName(targetPath)!;
            var targetFileName = Path.GetFileName(targetPath);
            var storeKey = game.InstallPath;
            var relativePath = Path.GetRelativePath(gameDir, targetPath);

            // Reuse (don't clobber) an existing committed manifest — e.g. OptiScaler already
            // installed for this game, or a previous swap of a different target file — so this
            // operation only ever adds to the same per-game record instead of losing prior state.
            var manifest = _backupStore.LoadManifest(storeKey);
            bool hasCommittedManifest = manifest != null &&
                string.Equals(manifest.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase);
            if (!hasCommittedManifest)
            {
                manifest = new InstallationManifest
                {
                    OperationId = Guid.NewGuid().ToString("N"),
                    OperationStatus = "in_progress",
                    StartedAtUtc = DateTime.UtcNow.ToString("O"),
                    InstallDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    InstalledGameDirectory = gameDir,
                    IncludesOptiscaler = false
                };
            }

            // Don't re-backup if this exact file already has a tracked original — that original
            // (from the very first time this path was touched) must never be replaced by a backup
            // of already-swapped content, or a later restore would bring back the wrong bytes.
            bool alreadyBackedUp = manifest!.FilesOverwritten.Any(f => f.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase))
                                 || manifest.BackedUpFiles.Contains(relativePath, StringComparer.OrdinalIgnoreCase);

            string? preHash = null;
            if (!alreadyBackedUp)
            {
                preHash = ComputeSha256(targetPath);
                if (!_backupStore.BackupFile(storeKey, gameDir, relativePath))
                    throw new Exception($"Could not back up '{targetFileName}' before swapping.");
            }

            try
            {
                File.Copy(sourceContentPath, targetPath, overwrite: true);
            }
            catch (Exception ex)
            {
                if (!alreadyBackedUp)
                    _backupStore.RestoreFile(storeKey, gameDir, relativePath);
                throw new Exception($"Failed to write swapped DLL: {ex.Message}", ex);
            }

            TrackManifestFileMutation(manifest, relativePath, existedBefore: true, preHash, ComputeSha256(targetPath));
            manifest.IncludesDllSwap = true;
            manifest.DllSwapTargetFileName = targetFileName;
            manifest.DllSwapExtrasVersion = extrasVersion;
            manifest.OperationStatus = "committed";
            manifest.FinishedAtUtc = DateTime.UtcNow.ToString("O");
            _backupStore.SaveManifest(storeKey, manifest);

            game.IsFsr4DllSwapped = true;
            game.Fsr4DllSwapTargetFileName = targetFileName;
            game.Fsr4ExtraVersion = extrasVersion;

            // Re-analyze so the UI reflects the swap immediately, same as InstallOptiScaler does.
            var analyzer = new GameAnalyzerService();
            GameAnalyzerService.InvalidateCacheForPath(game.InstallPath);
            analyzer.AnalyzeGame(game, forceRefresh: true);
            GameAnalyzerService.FlushCacheToDisk();

            DebugWindow.Log($"[DllSwap] Swapped '{targetFileName}' for '{game.Name}' with FSR4 INT8 v{extrasVersion}");
            return new DllSwapResult(targetFileName, extrasVersion);
        }

        public sealed record UninstallResult(bool UsedLegacyFallback, IReadOnlyList<string> RemainingSensitiveFiles);

        public UninstallResult UninstallOptiScaler(Game game)
        {
            // ── Determine candidate root directory ───────────────────────────────
            // We need a starting point to search for the manifest.
            string? rootDir = null;

            if (!string.IsNullOrEmpty(game.ExecutablePath))
                rootDir = Path.GetDirectoryName(game.ExecutablePath);

            if (string.IsNullOrEmpty(rootDir) && !string.IsNullOrEmpty(game.InstallPath))
                rootDir = game.InstallPath;

            if (string.IsNullOrEmpty(rootDir) || !Directory.Exists(rootDir))
                throw new Exception($"Invalid game directory: ExecutablePath='{game.ExecutablePath}', InstallPath='{game.InstallPath}'");

            // ── Load manifest: prefer external backup store, fall back to legacy in-folder ──
            string? gameDir = null;
            string? storeKey = null; // slug key for backup store (= game.InstallPath for new installs)
            InstallationManifest? manifest = null;
            bool usingExternalStore = false;

            // Priority 1: Try to resolve gameDir from external backup store.
            var candidateDirs = new List<string>();
            if (!string.IsNullOrEmpty(game.ExecutablePath) && File.Exists(game.ExecutablePath))
                candidateDirs.Add(Path.GetDirectoryName(game.ExecutablePath)!);
            if (!string.IsNullOrEmpty(game.InstallPath) && Directory.Exists(game.InstallPath))
            {
                candidateDirs.Add(game.InstallPath);
                var phoenixCandidate = Path.Combine(game.InstallPath, "Phoenix", "Binaries", "Win64");
                if (Directory.Exists(phoenixCandidate))
                    candidateDirs.Add(phoenixCandidate);
            }

            foreach (var candidate in candidateDirs.Where(d => !string.IsNullOrEmpty(d)))
            {
                if (_backupStore.HasValidBackup(candidate!))
                {
                    storeKey = candidate;
                    manifest = _backupStore.LoadManifest(candidate!);
                    usingExternalStore = true;
                    // Resolve actual game directory from the manifest so file operations target
                    // the correct subdirectory (e.g. ..\Game\ for Elden Ring, not the root).
                    var installedDir = manifest?.InstalledGameDirectory;
                    gameDir = !string.IsNullOrEmpty(installedDir) && Directory.Exists(installedDir)
                        ? installedDir
                        : candidate;
                    DebugWindow.Log($"[Uninstall] Found external backup for '{game.Name}' at store key: {candidate}, game dir: {gameDir}");
                    break;
                }
            }

            // Priority 2: Fall back to searching for legacy in-folder manifest
            if (!usingExternalStore)
            {
                string? legacyManifestPath = null;
                try
                {
                    var searchOptions = new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        MatchCasing = MatchCasing.CaseInsensitive
                    };
                    var manifests = Directory.GetFiles(rootDir, ManifestFileName, searchOptions);
                    if (manifests.Length > 0)
                        legacyManifestPath = manifests[0];
                }
                catch (Exception ex)
                {
                    DebugWindow.Log($"[Uninstall] Legacy manifest search failed: {ex.Message}");
                }

                if (legacyManifestPath != null && File.Exists(legacyManifestPath))
                {
                    try
                    {
                        var manifestJson = File.ReadAllText(legacyManifestPath);
                        manifest = JsonSerializer.Deserialize(manifestJson, OptimizerContext.Default.InstallationManifest);
                    }
                    catch (Exception ex)
                    {
                        DebugWindow.Log($"[Uninstall] Corrupt legacy manifest at '{legacyManifestPath}': {ex.Message}");
                    }

                    if (manifest?.InstalledGameDirectory != null && Directory.Exists(manifest.InstalledGameDirectory))
                        gameDir = manifest.InstalledGameDirectory;
                    else if (legacyManifestPath != null)
                        gameDir = Path.GetDirectoryName(Path.GetDirectoryName(legacyManifestPath));
                }
            }

            // Priority 3: last-resort re-detection
            if (string.IsNullOrEmpty(gameDir))
                gameDir = DetectCorrectInstallDirectory(rootDir);

            if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
                throw new Exception($"Could not determine installation directory for '{game.Name}'.");

            var backupDir = _backupStore.GetFilesDir(storeKey ?? gameDir);
            var legacyBackupDir = Path.Combine(gameDir, BackupFolderName);

            // If legacy folder exists but no external backup, migrate now (in case startup migration was skipped)
            if (!usingExternalStore && Directory.Exists(legacyBackupDir))
            {
                var legacyMPath = Path.Combine(legacyBackupDir, ManifestFileName);
                if (File.Exists(legacyMPath))
                {
                    try
                    {
                        _backupStore.MigrateFromLegacy(legacyMPath);
                        if (_backupStore.HasValidBackup(gameDir))
                        {
                            manifest = _backupStore.LoadManifest(gameDir);
                            usingExternalStore = true;
                            storeKey = gameDir; // legacy migration uses gameDir as its slug key
                            backupDir = _backupStore.GetFilesDir(gameDir);
                            DebugWindow.Log($"[Uninstall] On-demand migration completed for '{game.Name}'");
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugWindow.Log($"[Uninstall] On-demand migration failed, using legacy folder directly: {ex.Message}");
                        backupDir = legacyBackupDir; // fall back to legacy folder as source
                    }
                }
            }

            bool usedLegacyFallback = manifest == null;

            if (manifest != null)
            {
                // ── Manifest-based uninstallation (precise) ───────────────────────

                // Step 1: Delete files created by install.
                // If v2 tracking is unavailable, fall back to legacy InstalledFiles list.
                var filesToDelete = manifest.FilesCreated.Count > 0
                    ? manifest.FilesCreated.Select(f => f.RelativePath)
                    : manifest.InstalledFiles;

                foreach (var installedFile in filesToDelete.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var filePath = Path.Combine(gameDir, installedFile);
                        if (File.Exists(filePath))
                            File.Delete(filePath);
                    }
                    catch (Exception ex) { DebugWindow.Log($"[Uninstall] Failed to delete '{installedFile}': {ex.Message}"); }
                }

                // Step 1b: Unconditionally remove known OptiScaler files not tracked in the manifest
                // (e.g. the FSR4 INT8 Extras DLL and OptiPatcher.asi, which are installed through
                // separate flows outside InstallOptiScaler's manifest tracking). Mirrors Step 3b below,
                // which does the same thing for known directories.
                foreach (var artifact in KnownOptiscalerArtifacts)
                {
                    var artifactPath = Path.Combine(gameDir, artifact);
                    try
                    {
                        if (File.Exists(artifactPath))
                        {
                            File.Delete(artifactPath);
                            DebugWindow.Log($"[Uninstall] Removed known OptiScaler file not tracked in manifest: {artifact}");
                        }
                    }
                    catch (Exception ex) { DebugWindow.Log($"[Uninstall] Failed to remove known file '{artifact}': {ex.Message}"); }
                }

                // Step 2: Restore overwritten files from backup (external store or legacy folder).
                // If v2 tracking is unavailable, fall back to legacy BackedUpFiles list.
                if (manifest.FilesOverwritten.Count > 0)
                {
                    foreach (var overwritten in manifest.FilesOverwritten)
                    {
                        try
                        {
                            if (!_backupStore.RestoreFile(storeKey ?? gameDir, gameDir, overwritten.RelativePath, overwritten.BackupRelativePath))
                            {
                                // Fallback: try legacy in-folder backup
                                var legacyBackupPath = Path.Combine(legacyBackupDir,
                                    overwritten.BackupRelativePath ?? overwritten.RelativePath);
                                if (File.Exists(legacyBackupPath))
                                    File.Copy(legacyBackupPath, Path.Combine(gameDir, overwritten.RelativePath), overwrite: true);
                            }
                        }
                        catch (Exception ex) { DebugWindow.Log($"[Uninstall] Failed to restore '{overwritten.RelativePath}': {ex.Message}"); }
                    }
                }
                else
                {
                    foreach (var backedUpFile in manifest.BackedUpFiles)
                    {
                        try
                        {
                            if (!_backupStore.RestoreFile(storeKey ?? gameDir, gameDir, backedUpFile))
                            {
                                // Fallback: try legacy in-folder backup
                                var legacyBackupPath = Path.Combine(legacyBackupDir, backedUpFile);
                                if (File.Exists(legacyBackupPath))
                                    File.Copy(legacyBackupPath, Path.Combine(gameDir, backedUpFile), overwrite: true);
                            }
                        }
                        catch (Exception ex) { DebugWindow.Log($"[Uninstall] Failed to restore backup '{backedUpFile}': {ex.Message}"); }
                    }
                }

                // Step 3: Remove installed (now-empty) subdirectories, deepest first
                foreach (var installedDir in manifest.InstalledDirectories.OrderByDescending(d => d.Length))
                {
                    try
                    {
                        var dirPath = Path.Combine(gameDir, installedDir);
                        if (Directory.Exists(dirPath) && !Directory.EnumerateFileSystemEntries(dirPath).Any())
                            Directory.Delete(dirPath, false);
                    }
                    catch (Exception ex) { DebugWindow.Log($"[Uninstall] Failed to remove directory '{installedDir}': {ex.Message}"); }
                }

                // Step 3b: Unconditionally remove known OptiScaler directories.
                // These may still exist if they contained files not tracked in the manifest
                // (e.g. after an update where the directory already existed pre-install).
                foreach (var knownDir in KnownOptiscalerDirectories)
                {
                    var dirPath = Path.Combine(gameDir, knownDir);
                    try
                    {
                        if (Directory.Exists(dirPath))
                        {
                            Directory.Delete(dirPath, true);
                            DebugWindow.Log($"[Uninstall] Removed known OptiScaler directory: {knownDir}");
                        }
                    }
                    catch (Exception ex) { DebugWindow.Log($"[Uninstall] Failed to remove known directory '{knownDir}': {ex.Message}"); }
                }
            }
            else
            {
                // ── Legacy fallback (no manifest present) ─────────────────────────
                // Covers installations created before the manifest system was introduced.

                // Collect all directories to scan: gameDir + Phoenix subdir if present
                var dirsToScan = new List<string> { gameDir };
                var phoenixDir = DetectCorrectInstallDirectory(gameDir);
                if (!phoenixDir.Equals(gameDir, StringComparison.OrdinalIgnoreCase))
                    dirsToScan.Add(phoenixDir);

                // Restore backed-up files first
                foreach (var dir in dirsToScan)
                {
                    var innerLegacyBackupDir = Path.Combine(dir, BackupFolderName);
                    if (Directory.Exists(innerLegacyBackupDir))
                    {
                        foreach (var backupFile in Directory.GetFiles(innerLegacyBackupDir, "*.*", SearchOption.AllDirectories))
                        {
                            try
                            {
                                var relativePath = Path.GetRelativePath(innerLegacyBackupDir, backupFile);
                                if (relativePath.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                var destPath = Path.Combine(dir, relativePath);
                                File.Copy(backupFile, destPath, overwrite: true);
                            }
                            catch (Exception ex) { DebugWindow.Log($"[Uninstall] Failed to restore legacy backup file: {ex.Message}"); }
                        }

                        try { Directory.Delete(innerLegacyBackupDir, true); }
                        catch (Exception ex) { DebugWindow.Log($"[Uninstall] Failed to delete legacy backup dir: {ex.Message}"); }
                    }
                }

                foreach (var dir in dirsToScan)
                {
                    var innerLegacyBackupDir2 = Path.Combine(dir, BackupFolderName);
                    foreach (var fileName in KnownOptiscalerArtifacts)
                    {
                        var filePath = Path.Combine(dir, fileName);
                        if (!File.Exists(filePath)) continue;

                        try
                        {
                            // Always delete OptiScaler config/log
                            if (fileName.StartsWith("OptiScaler", StringComparison.OrdinalIgnoreCase))
                            {
                                File.Delete(filePath);
                                continue;
                            }

                            // For DLLs: only delete if there was no original backup
                            // (backup dir was already deleted above, so !Directory.Exists is true
                            // when there was no backup — safe to delete)
                            var backupPath = Path.Combine(legacyBackupDir, fileName);
                            if (!File.Exists(backupPath) && !Directory.Exists(legacyBackupDir))
                                File.Delete(filePath);
                        }
                        catch (Exception ex) { DebugWindow.Log($"[Uninstall] Failed to clean legacy artifact '{fileName}': {ex.Message}"); }
                    }
                }
            }

            // Clean up runtime-generated files that no game would have
            // (these are created when the game runs with OptiScaler, not during install)
            foreach (var runtimeFile in new[] { "OptiScaler.log", "fakenvapi.log", "fakenvapi.ini" })
            {
                var runtimePath = Path.Combine(gameDir, runtimeFile);
                try { if (File.Exists(runtimePath)) File.Delete(runtimePath); }
                catch (Exception ex) { DebugWindow.Log($"[Uninstall] Could not delete runtime file '{runtimeFile}': {ex.Message}"); }
            }

            // Remove external backup store entry
            _backupStore.DeleteBackup(storeKey ?? gameDir);

            // Remove legacy OptiScalerBackup/ folder if it still exists
            // (first uninstall after migration from v1.0.4, or if migration was skipped)
            if (Directory.Exists(legacyBackupDir))
            {
                try { Directory.Delete(legacyBackupDir, true); }
                catch (Exception ex) { DebugWindow.Log($"[Uninstall] Could not remove legacy backup directory: {ex.Message}"); }
            }

            // Clear game state immediately so the UI reflects the uninstallation
            game.IsOptiscalerInstalled = false;
            game.OptiscalerVersion = null;
            game.Fsr4ExtraVersion = null;
            game.IsFsr4DllSwapped = false;
            game.Fsr4DllSwapTargetFileName = null;

            // Re-analyze to refresh DLSS/FSR/XeSS detection after files were removed/restored
            var analyzer = new GameAnalyzerService();
            GameAnalyzerService.InvalidateCacheForPath(game.InstallPath);
            analyzer.AnalyzeGame(game, forceRefresh: true);
            GameAnalyzerService.FlushCacheToDisk();

            // Files that could be native game files were left behind on purpose (we can't tell
            // without a manifest whether the game shipped them) - surface that to the caller
            // instead of silently pretending the folder is fully clean. Exclude anything the
            // manifest tracked as FilesOverwritten/BackedUpFiles — those aren't a mystery, we know
            // exactly what happened to them and Step 2 above already restored them correctly (e.g.
            // a game's own dxgi.dll used as the injection target, or a DLL-swap target that happens
            // to share a name with a SensitiveArtifacts entry — see SwapFsr4Dll).
            var knownRestoredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (manifest != null)
            {
                foreach (var f in manifest.FilesOverwritten) knownRestoredFiles.Add(f.RelativePath);
                foreach (var f in manifest.BackedUpFiles) knownRestoredFiles.Add(f);
            }
            var remainingSensitive = SensitiveArtifacts
                .Where(f => !knownRestoredFiles.Contains(f) && File.Exists(Path.Combine(gameDir, f)))
                .ToList();

            return new UninstallResult(usedLegacyFallback, remainingSensitive);
        }

        public bool RecoverIncompleteInstallIfNeeded(string installRoot)
        {
            if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
                return false;

            string? manifestPath = null;
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MatchCasing = MatchCasing.CaseInsensitive
                };

                manifestPath = Directory.GetFiles(installRoot, ManifestFileName, options).FirstOrDefault();
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrEmpty(manifestPath) || !File.Exists(manifestPath))
                return false;

            InstallationManifest? manifest;
            try
            {
                var manifestJson = File.ReadAllText(manifestPath);
                manifest = JsonSerializer.Deserialize(manifestJson, OptimizerContext.Default.InstallationManifest);
            }
            catch
            {
                return false;
            }

            if (manifest == null)
                return false;

            if (string.Equals(manifest.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase))
                return false;

            var gameDir = !string.IsNullOrWhiteSpace(manifest.InstalledGameDirectory) &&
                          Directory.Exists(manifest.InstalledGameDirectory)
                ? manifest.InstalledGameDirectory
                : Path.GetDirectoryName(Path.GetDirectoryName(manifestPath));

            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
                return false;

            var backupDir = Path.Combine(gameDir, BackupFolderName);
            DebugWindow.Log($"[Recovery] Found incomplete install manifest (status={manifest.OperationStatus}). Starting recovery for: {gameDir}");

            var rollbackSummary = RollbackFailedInstall(gameDir, backupDir, manifest);
            ValidateAndHealPostUninstall(gameDir, backupDir, manifest);

            DebugWindow.Log($"[Recovery] Completed. Restored={rollbackSummary.Restored}, Deleted={rollbackSummary.Deleted}");
            return true;
        }

        private List<KeyFileSnapshot> CapturePreInstallKeySnapshot(string gameDir, string injectionDllName)
        {
            var keys = new HashSet<string>(_criticalFiles, StringComparer.OrdinalIgnoreCase)
            {
                injectionDllName,
                "OptiScaler.ini",
                "nvapi64.dll",
                "dlssg_to_fsr3_amd_is_better.dll",
                Fsr4Int8DllHelper.LegacyFileName,
                Fsr4Int8DllHelper.CurrentFileName
            };

            var snapshots = new List<KeyFileSnapshot>();
            foreach (var relPath in keys)
            {
                var fullPath = Path.Combine(gameDir, relPath);
                var existed = File.Exists(fullPath);
                snapshots.Add(new KeyFileSnapshot
                {
                    RelativePath = relPath,
                    Existed = existed,
                    Sha256 = existed ? ComputeSha256(fullPath) : null
                });
            }

            return snapshots.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void TrackManifestFileMutation(
            InstallationManifest manifest,
            string relativePath,
            bool existedBefore,
            string? preInstallHash,
            string? postInstallHash)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return;

            if (existedBefore)
            {
                var record = manifest.FilesOverwritten.FirstOrDefault(
                    x => x.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));

                if (record == null)
                {
                    record = new ManifestFileRecord
                    {
                        RelativePath = relativePath,
                        BackupRelativePath = relativePath
                    };
                    manifest.FilesOverwritten.Add(record);
                }

                record.ExistedBefore = true;
                record.PreInstallSha256 = preInstallHash;
                record.PostInstallSha256 = postInstallHash;
            }
            else
            {
                var record = manifest.FilesCreated.FirstOrDefault(
                    x => x.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));

                if (record == null)
                {
                    record = new ManifestFileRecord
                    {
                        RelativePath = relativePath,
                        BackupRelativePath = null
                    };
                    manifest.FilesCreated.Add(record);
                }

                record.ExistedBefore = false;
                record.PreInstallSha256 = null;
                record.PostInstallSha256 = postInstallHash;
            }
        }

        private static string? ComputeSha256(string filePath)
        {
            try
            {
                using var sha = SHA256.Create();
                using var stream = File.OpenRead(filePath);
                var hash = sha.ComputeHash(stream);
                return Convert.ToHexString(hash);
            }
            catch
            {
                return null;
            }
        }

        private static void SaveManifest(string manifestPath, InstallationManifest manifest)
        {
            var manifestJson = JsonSerializer.Serialize(manifest, OptimizerContext.Default.InstallationManifest);
            File.WriteAllText(manifestPath, manifestJson);
        }

        private RollbackResult RollbackFailedInstall(string gameDir, string backupDir, InstallationManifest manifest)
        {
            var result = new RollbackResult();

            foreach (var record in manifest.FilesCreated)
            {
                var fullPath = Path.Combine(gameDir, record.RelativePath);
                if (TryDeleteFileIfExists(fullPath))
                    result.Deleted++;
            }

            foreach (var record in manifest.FilesOverwritten)
            {
                if (!record.ExistedBefore)
                    continue;

                if (TryRestoreFromBackup(gameDir, backupDir, record.RelativePath, record.BackupRelativePath))
                    result.Restored++;
            }

            if (manifest.FilesCreated.Count == 0)
            {
                foreach (var rel in manifest.InstalledFiles.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var fullPath = Path.Combine(gameDir, rel);
                    if (TryDeleteFileIfExists(fullPath))
                        result.Deleted++;
                }
            }

            if (manifest.FilesOverwritten.Count == 0)
            {
                foreach (var rel in manifest.BackedUpFiles.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (TryRestoreFromBackup(gameDir, backupDir, rel, rel))
                        result.Restored++;
                }
            }

            return result;
        }

        private void ValidateAndHealPostUninstall(string gameDir, string backupDir, InstallationManifest? manifest)
        {
            var preInstallState = BuildPreInstallStateMap(manifest);
            var deletedResidues = 0;
            var restoredFiles = 0;

            // 1) Ensure files created by install are removed.
            if (manifest != null)
            {
                foreach (var record in manifest.FilesCreated)
                {
                    var fullPath = Path.Combine(gameDir, record.RelativePath);
                    if (TryDeleteFileIfExists(fullPath))
                    {
                        deletedResidues++;
                        DebugWindow.Log($"[Uninstall][Validate] Removed residue created by install: {record.RelativePath}");
                    }
                }

                // 2) Ensure overwritten files were restored.
                foreach (var record in manifest.FilesOverwritten)
                {
                    if (!record.ExistedBefore)
                        continue;

                    var targetPath = Path.Combine(gameDir, record.RelativePath);
                    var currentHash = File.Exists(targetPath) ? ComputeSha256(targetPath) : null;
                    var hashMismatch = !string.IsNullOrEmpty(record.PreInstallSha256) &&
                                       !string.Equals(record.PreInstallSha256, currentHash, StringComparison.OrdinalIgnoreCase);

                    if (!File.Exists(targetPath) || hashMismatch)
                    {
                        if (TryRestoreFromBackup(gameDir, backupDir, record.RelativePath, record.BackupRelativePath))
                        {
                            restoredFiles++;
                            DebugWindow.Log($"[Uninstall][Validate] Restored overwritten file from backup: {record.RelativePath}");
                        }
                    }
                }
            }

            // 3) Fallback sweep over known artifacts.
            // Only delete if we can confirm OptiScaler created the file (it's in FilesCreated).
            // Never delete files that were backed up/restored (game-native) or files
            // that OptiScaler never touched (not in the manifest at all).
            var backedUpFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (manifest != null)
            {
                foreach (var f in manifest.FilesOverwritten)
                    backedUpFiles.Add(f.RelativePath);
                foreach (var f in manifest.BackedUpFiles)
                    backedUpFiles.Add(f);
            }

            foreach (var relativePath in KnownOptiscalerArtifacts)
            {
                var fullPath = Path.Combine(gameDir, relativePath);
                if (!File.Exists(fullPath))
                    continue;

                // Skip files that were backed up — they belong to the game
                if (backedUpFiles.Contains(relativePath))
                    continue;

                // If we have a pre-install snapshot showing the file existed, try to restore it
                if (preInstallState.TryGetValue(relativePath, out var snapshot) && snapshot.Existed)
                {
                    var currentHash = ComputeSha256(fullPath);
                    var expectedHash = snapshot.Sha256;
                    var mismatch = !string.IsNullOrEmpty(expectedHash) &&
                                   !string.Equals(expectedHash, currentHash, StringComparison.OrdinalIgnoreCase);

                    if (mismatch && TryRestoreFromBackup(gameDir, backupDir, relativePath, relativePath))
                    {
                        restoredFiles++;
                        DebugWindow.Log($"[Uninstall][Validate] Restored key file to pre-install state: {relativePath}");
                    }
                    continue;
                }

                // Only delete if this file was created by OptiScaler (not a game file)
                var wasCreatedByInstall = manifest?.FilesCreated
                    .Any(f => f.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase)) ?? false;

                if (wasCreatedByInstall)
                {
                    if (TryDeleteFileIfExists(fullPath))
                    {
                        deletedResidues++;
                        DebugWindow.Log($"[Uninstall][Validate] Removed known residue: {relativePath}");
                    }
                }
            }

            // NOTE: Backup directory cleanup is now handled by ForceRemoveAllArtifacts.

            DebugWindow.Log($"[Uninstall][Validate] Validation completed. Restored={restoredFiles}, ResiduesRemoved={deletedResidues}");
        }

        /// <summary>
        /// Public entry point for the "Folder Cleanup" feature.
        /// Unconditionally removes all known OptiScaler artifacts from the game directory,
        /// marks the game as uninstalled, and deletes the external backup store entry.
        /// Intended for recovering from corrupted or orphaned OptiScaler installations.
        /// </summary>
        /// <param name="game">The game to clean up.</param>
        /// <param name="selectedSensitiveFiles">
        /// Subset of <see cref="SensitiveArtifacts"/> the user opted to delete.
        /// Pass null or empty to skip all sensitive files.
        /// </param>
        public void ForceFolderCleanup(Game game, IEnumerable<string>? selectedSensitiveFiles = null)
        {
            string? gameDir = null;

            // Priority 1: manifest's InstalledGameDirectory (most accurate — set during install).
            var storeKey = game.InstallPath;
            var manifest = _backupStore.HasValidBackup(storeKey) ? _backupStore.LoadManifest(storeKey) : null;
            if (manifest?.InstalledGameDirectory != null && Directory.Exists(manifest.InstalledGameDirectory))
                gameDir = manifest.InstalledGameDirectory;

            // Priority 2: parent dir of the game executable (same as InstallOptiScaler step 1).
            if (string.IsNullOrEmpty(gameDir) && !string.IsNullOrEmpty(game.ExecutablePath) && File.Exists(game.ExecutablePath))
                gameDir = Path.GetDirectoryName(game.ExecutablePath);

            // Priority 3: DetermineInstallDirectory — mirrors the exact logic used during install
            // (detects Binaries/Win64, Phoenix subdirs, etc.).  This is the critical fallback that
            // was missing and caused cleanup to target the wrong root directory.
            if (string.IsNullOrEmpty(gameDir))
            {
                var detected = DetermineInstallDirectory(game);
                if (!string.IsNullOrEmpty(detected) && Directory.Exists(detected))
                    gameDir = detected;
            }

            // Priority 4: InstallPath root as last resort.
            if (string.IsNullOrEmpty(gameDir) && !string.IsNullOrEmpty(game.InstallPath) && Directory.Exists(game.InstallPath))
                gameDir = game.InstallPath;

            if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
                throw new Exception($"Could not determine game directory for '{game.Name}'.");

            DebugWindow.Log($"[FolderCleanup] Starting force cleanup for '{game.Name}' at: {gameDir}");

            ForceRemoveAllArtifacts(gameDir, selectedSensitiveFiles);

            // Delete the external backup store so future installs start fresh.
            _backupStore.DeleteBackup(storeKey);

            // Remove legacy OptiScalerBackup/ folder if still present.
            var legacyBackupDir = Path.Combine(gameDir, BackupFolderName);
            if (Directory.Exists(legacyBackupDir))
            {
                try { Directory.Delete(legacyBackupDir, true); }
                catch (Exception ex) { DebugWindow.Log($"[FolderCleanup] Could not remove legacy backup dir: {ex.Message}"); }
            }

            // Mark the game as uninstalled and re-run the analyser so that FSR/XeSS/DLSS
            // badge state is refreshed (e.g. if the user selected sensitive files for deletion
            // those DLLs are now gone and the corresponding badges should disappear).
            // We force IsOptiscalerInstalled = false AFTER AnalyzeGame so that any re-detection
            // of OptiScaler by the analyser (e.g. because dxgi.dll was intentionally kept) does
            // not create a "stuck-as-installed" loop.
            game.IsOptiscalerInstalled = false;
            game.OptiscalerVersion = null;
            game.Fsr4ExtraVersion = null;

            var analyzer = new GameAnalyzerService();
            GameAnalyzerService.InvalidateCacheForPath(game.InstallPath);
            analyzer.AnalyzeGame(game, forceRefresh: true);

            // Override any OptiScaler re-detection from the analyser — files that remain
            // are sensitive ones the user explicitly chose to keep, not a live installation.
            game.IsOptiscalerInstalled = false;
            game.OptiscalerVersion = null;
            game.Fsr4ExtraVersion = null;

            GameAnalyzerService.FlushCacheToDisk();

            DebugWindow.Log($"[FolderCleanup] Completed for '{game.Name}'.");
        }
        
        /// <summary>
        /// Returns true if the given directory contains files that indicate a leftover or
        /// corrupted OptiScaler installation (i.e. game is "not installed" but artifacts remain).
        /// Used by the UI to prompt a cleanup before a fresh install.
        /// </summary>
        public static bool HasCorruptArtifacts(string gameDir)
        {
            if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
                return false;

            // Core OptiScaler files that should never exist unless it was installed.
            var indicators = new[]
            {
                "OptiScaler.ini", "OptiScaler.log", "OptiScaler.dll",
                "setup_linux.sh", "setup_windows.bat",
                "fakenvapi.ini",  "fakenvapi.log",  "fakenvapi.dll",
                "dlssg_to_fsr3_amd_is_better.dll",
            };

            foreach (var file in indicators)
                if (File.Exists(Path.Combine(gameDir, file)))
                    return true;

            // OptiScaler's exclusive subdirectory
            if (Directory.Exists(Path.Combine(gameDir, "D3D12_Optiscaler")))
                return true;

            // OptiPatcher plugin
            if (File.Exists(Path.Combine(gameDir, "plugins", "OptiPatcher.asi")))
                return true;

            return false;
        }

        private void ForceRemoveAllArtifacts(string gameDir, IEnumerable<string>? extraFilesToDelete = null)
        {
            var dirsToScan = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { gameDir };
            var phoenixDir = DetectCorrectInstallDirectory(gameDir);
            if (!phoenixDir.Equals(gameDir, StringComparison.OrdinalIgnoreCase))
                dirsToScan.Add(phoenixDir);

            var deletedCount = 0;

            foreach (var dir in dirsToScan)
            {
                // Delete every known artifact file unconditionally
                foreach (var artifact in KnownOptiscalerArtifacts)
                {
                    var fullPath = Path.Combine(dir, artifact);
                    try
                    {
                        if (File.Exists(fullPath))
                        {
                            File.Delete(fullPath);
                            deletedCount++;
                            DebugWindow.Log($"[Uninstall][ForceClean] Deleted file: {artifact}");
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugWindow.Log($"[Uninstall][ForceClean] Could not delete '{artifact}': {ex.Message}");
                    }
                }

                // Delete every known OptiScaler directory unconditionally (including contents)
                foreach (var knownDir in KnownOptiscalerDirectories)
                {
                    var fullPath = Path.Combine(dir, knownDir);
                    try
                    {
                        if (Directory.Exists(fullPath))
                        {
                            Directory.Delete(fullPath, true);
                            DebugWindow.Log($"[Uninstall][ForceClean] Deleted directory: {knownDir}");
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugWindow.Log($"[Uninstall][ForceClean] Could not delete directory '{knownDir}': {ex.Message}");
                    }
                }

                // Remove legacy OptiScalerBackup directory
                var backupDir = Path.Combine(dir, BackupFolderName);
                try
                {
                    if (Directory.Exists(backupDir))
                    {
                        Directory.Delete(backupDir, true);
                        DebugWindow.Log("[Uninstall][ForceClean] Removed backup directory.");
                    }
                }
                catch (Exception ex)
                {
                    DebugWindow.Log($"[Uninstall][ForceClean] Could not remove backup directory: {ex.Message}");
                }

                // Delete user-selected sensitive files
                if (extraFilesToDelete != null)
                {
                    foreach (var sensitiveFile in extraFilesToDelete)
                    {
                        var fullPath = Path.Combine(dir, sensitiveFile);
                        try
                        {
                            if (File.Exists(fullPath))
                            {
                                File.Delete(fullPath);
                                deletedCount++;
                                DebugWindow.Log($"[Uninstall][ForceClean] Deleted sensitive file: {sensitiveFile}");
                            }
                        }
                        catch (Exception ex)
                        {
                            DebugWindow.Log($"[Uninstall][ForceClean] Could not delete sensitive file '{sensitiveFile}': {ex.Message}");
                        }
                    }
                }
            }

            DebugWindow.Log($"[Uninstall][ForceClean] Final sweep completed. Files removed: {deletedCount}");
        }

        /// <summary>
        /// Compares the actual files in the cached OptiScaler version (and component caches)
        /// against what remains in the game directory. Any file whose name matches a cached
        /// file is deleted unconditionally. This catches files not in KnownOptiscalerArtifacts
        /// (e.g. new DLLs added in future OptiScaler versions, setup scripts, readme files,
        /// subdirectories like D3D12_Optiscaler/, Licenses/, etc.).
        /// </summary>
        private void SweepResidualFilesFromCache(string gameDir, InstallationManifest? manifest)
        {
            var componentService = new ComponentManagementService();
            var cacheDirs = new List<string>();

            // Resolve the OptiScaler version cache directory
            var version = manifest?.OptiscalerVersion;
            if (!string.IsNullOrEmpty(version))
            {
                var optiCachePath = componentService.GetOptiScalerCachePath(version);
                if (Directory.Exists(optiCachePath))
                    cacheDirs.Add(optiCachePath);
            }

            // Also check Fakenvapi, NukemFG, Extras and OptiPatcher caches
            var fakenvapiCache = componentService.GetFakenvapiCachePath();
            if (Directory.Exists(fakenvapiCache))
                cacheDirs.Add(fakenvapiCache);

            var nukemCache = componentService.GetNukemFGCachePath();
            if (Directory.Exists(nukemCache))
                cacheDirs.Add(nukemCache);

            if (cacheDirs.Count == 0)
            {
                DebugWindow.Log("[Uninstall][CacheSweep] No cache directories found — skipping cache-based sweep.");
                return;
            }

            // Build the set of relative paths from all cache directories
            var cachedRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cacheDir in cacheDirs)
            {
                try
                {
                    foreach (var entry in Directory.GetFileSystemEntries(cacheDir, "*", SearchOption.AllDirectories))
                    {
                        var relativePath = Path.GetRelativePath(cacheDir, entry);
                        cachedRelativePaths.Add(relativePath);
                    }
                }
                catch (Exception ex)
                {
                    DebugWindow.Log($"[Uninstall][CacheSweep] Error enumerating cache dir '{cacheDir}': {ex.Message}");
                }
            }

            if (cachedRelativePaths.Count == 0)
            {
                DebugWindow.Log("[Uninstall][CacheSweep] Cache directories are empty — skipping.");
                return;
            }

            // Collect all game directories to scan (main + Phoenix/UE5 subdirs)
            var dirsToScan = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { gameDir };
            var phoenixDir = DetectCorrectInstallDirectory(gameDir);
            if (!phoenixDir.Equals(gameDir, StringComparison.OrdinalIgnoreCase))
                dirsToScan.Add(phoenixDir);

            var deletedCount = 0;

            foreach (var dir in dirsToScan)
            {
                foreach (var relativePath in cachedRelativePaths)
                {
                    var fullPath = Path.Combine(dir, relativePath);

                    // Delete files
                    try
                    {
                        if (File.Exists(fullPath))
                        {
                            File.Delete(fullPath);
                            deletedCount++;
                            DebugWindow.Log($"[Uninstall][CacheSweep] Deleted residual file: {relativePath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugWindow.Log($"[Uninstall][CacheSweep] Could not delete '{relativePath}': {ex.Message}");
                    }
                }

                // Delete directories that came from the cache (deepest first)
                var cachedDirs = cachedRelativePaths
                    .Select(p => Path.Combine(dir, p))
                    .Where(p => Directory.Exists(p))
                    .OrderByDescending(p => p.Length);

                foreach (var dirPath in cachedDirs)
                {
                    try
                    {
                        if (Directory.Exists(dirPath) && !Directory.EnumerateFileSystemEntries(dirPath).Any())
                        {
                            Directory.Delete(dirPath, false);
                            DebugWindow.Log($"[Uninstall][CacheSweep] Removed empty directory: {Path.GetRelativePath(dir, dirPath)}");
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugWindow.Log($"[Uninstall][CacheSweep] Could not remove directory: {ex.Message}");
                    }
                }
            }

            DebugWindow.Log($"[Uninstall][CacheSweep] Cache comparison sweep completed. Residual files removed: {deletedCount}");
        }

        private sealed class RollbackResult
        {
            public int Restored { get; set; }
            public int Deleted { get; set; }
        }

        private static Dictionary<string, KeyFileSnapshot> BuildPreInstallStateMap(InstallationManifest? manifest)
        {
            var map = new Dictionary<string, KeyFileSnapshot>(StringComparer.OrdinalIgnoreCase);
            if (manifest?.PreInstallKeyFiles == null)
                return map;

            foreach (var snapshot in manifest.PreInstallKeyFiles)
            {
                if (string.IsNullOrWhiteSpace(snapshot.RelativePath))
                    continue;

                map[snapshot.RelativePath] = snapshot;
            }

            return map;
        }

        private static bool TryRestoreFromBackup(string gameDir, string backupDir, string relativePath, string? backupRelativePath)
        {
            try
            {
                var effectiveBackupRelative = string.IsNullOrWhiteSpace(backupRelativePath)
                    ? relativePath
                    : backupRelativePath;

                var backupPath = Path.Combine(backupDir, effectiveBackupRelative);
                if (!File.Exists(backupPath))
                    return false;

                var destinationPath = Path.Combine(gameDir, relativePath);
                var destinationDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
                    Directory.CreateDirectory(destinationDir);

                File.Copy(backupPath, destinationPath, overwrite: true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryDeleteFileIfExists(string fullPath)
        {
            try
            {
                if (!File.Exists(fullPath))
                    return false;

                File.Delete(fullPath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Determines the correct installation directory for games based on user rules.
        /// </summary>
        /// <summary>
        /// Checks whether an executable sits directly under a "Binaries/Win64" folder (Unreal Engine's
        /// shipping layout), independent of the OS path separator. Directory.GetFiles returns '\' on
        /// Windows and '/' on Linux, so a literal @"Binaries\Win64" substring check never matches on
        /// Linux/SteamOS — comparing folder names instead keeps this working on both.
        /// </summary>
        private static bool IsUnderBinariesWin64(string exePath)
        {
            var win64Dir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(win64Dir) || !string.Equals(Path.GetFileName(win64Dir), "Win64", StringComparison.OrdinalIgnoreCase))
                return false;

            var binariesDir = Path.GetDirectoryName(win64Dir);
            return !string.IsNullOrEmpty(binariesDir) && string.Equals(Path.GetFileName(binariesDir), "Binaries", StringComparison.OrdinalIgnoreCase);
        }

        public string? DetermineInstallDirectory(Game game)
        {
            if (string.IsNullOrEmpty(game.InstallPath) || !Directory.Exists(game.InstallPath))
            {
                // If InstallPath is missing, try ExecutablePath
                if (!string.IsNullOrEmpty(game.ExecutablePath) && File.Exists(game.ExecutablePath))
                    return Path.GetDirectoryName(game.ExecutablePath);

                return null;
            }

            // Rule 1: Try to extract in the same folder as the main .exe, scan to find it.
            string[] allExes = Array.Empty<string>();
            try
            {
                allExes = Directory.GetFiles(game.InstallPath, "*.exe", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[Install] Could not enumerate executables in '{game.InstallPath}': {ex.Message}");
            }

            // Rule 2: Unreal Engine always ships its real executables under "<Project>\Binaries\Win64"
            // (including split-project layouts such as UE5's "Phoenix" template — Phoenix\Binaries\Win64
            // is just a special case of this same pattern). When such a folder exists, restrict the
            // candidate pool to only those executables — this is a structural, engine-mandated
            // convention, unlike the name/size/DLL heuristic below, which can be flipped by files that
            // come and go (e.g. a native upscaler DLL removed by Folder Cleanup) and is therefore not
            // reliable enough on its own to pick between a root launcher stub and the real shipping exe.
            var binariesWin64Exes = allExes
                .Where(IsUnderBinariesWin64)
                .ToArray();
            var candidateExes = binariesWin64Exes.Length > 0 ? binariesWin64Exes : allExes;

            string? bestMatchDir = null;

            if (candidateExes.Length > 0)
            {
                // Try to match by name or context
                int bestScore = -1;
                string? bestExe = null;

                var gameNameLetters = new string(game.Name.Where(char.IsLetterOrDigit).ToArray());

                foreach (var exePath in candidateExes)
                {
                    var fileName = Path.GetFileNameWithoutExtension(exePath);

                    // Filter out known non-game executables
                    if (fileName.Contains("Crash", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Contains("Redist", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Contains("Setup", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Contains("Launcher", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Contains("UnrealCEFSubProcess", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Contains("Prerequisites", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int score = 0;
                    var exeLetters = new string(fileName.Where(char.IsLetterOrDigit).ToArray());

                    if (!string.IsNullOrEmpty(exeLetters) && !string.IsNullOrEmpty(gameNameLetters))
                    {
                        if (exeLetters.Contains(gameNameLetters, StringComparison.OrdinalIgnoreCase) ||
                            gameNameLetters.Contains(exeLetters, StringComparison.OrdinalIgnoreCase))
                        {
                            score += 15;
                        }
                    }

                    if (IsUnderBinariesWin64(exePath))
                    {
                        score += 5;
                    }

                    try
                    {
                        // Main game executables are usually decently sized (> 5MB)
                        var fileInfo = new FileInfo(exePath);
                        if (fileInfo.Length > 5 * 1024 * 1024)
                        {
                            score += 10;
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugWindow.Log($"[Install] Could not read file info for '{exePath}': {ex.Message}");
                    }

                    var exeDir = Path.GetDirectoryName(exePath);
                    if (exeDir != null)
                    {
                        try
                        {
                            var dlls = Directory.GetFiles(exeDir, "*.dll", SearchOption.TopDirectoryOnly);
                            foreach (var dll in dlls)
                            {
                                var dllName = Path.GetFileName(dll).ToLowerInvariant();
                                if (dllName.Contains("amd") || dllName.Contains("fsr") || dllName.Contains("nvngx") || dllName.Contains("dlss") || dllName.Contains("sl.interposer") || dllName.Contains("xess"))
                                {
                                    score += 25; // High confidence if scaling DLLs are nearby
                                    break;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            DebugWindow.Log($"[Install] Could not enumerate DLLs in '{exeDir}': {ex.Message}");
                        }
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestExe = exePath;
                    }
                }

                if (bestExe != null)
                {
                    bestMatchDir = Path.GetDirectoryName(bestExe);
                }

                // Fallback: If no match by name, check known ExecutablePath
                if (bestMatchDir == null)
                {
                    if (!string.IsNullOrEmpty(game.ExecutablePath) && File.Exists(game.ExecutablePath))
                    {
                        bestMatchDir = Path.GetDirectoryName(game.ExecutablePath);
                    }
                    else if (binariesWin64Exes.Length == 1)
                    {
                        bestMatchDir = Path.GetDirectoryName(binariesWin64Exes[0]);
                    }
                }
            }
            else if (allExes.Length == 0 && !string.IsNullOrEmpty(game.ExecutablePath) && File.Exists(game.ExecutablePath))
            {
                // Fallback if Directory.GetFiles fails but we have an ExecutablePath
                bestMatchDir = Path.GetDirectoryName(game.ExecutablePath);
            }

            if (bestMatchDir != null && Directory.Exists(bestMatchDir))
            {
                return bestMatchDir;
            }

            // Fallback to the main install path, if nothing else works
            return game.InstallPath;
        }


        /// <summary>
        /// Detects the correct installation directory fallback for older uninstalls.
        /// </summary>
        private string DetectCorrectInstallDirectory(string baseDir)
        {
            // Check for UE5 Phoenix structure: Phoenix/Binaries/Win64
            var phoenixPath = Path.Combine(baseDir, "Phoenix", "Binaries", "Win64");
            if (Directory.Exists(phoenixPath))
            {
                return phoenixPath;
            }

            // Check for generic UE structure: GameName/Binaries/Win64
            var binariesPath = Path.Combine(baseDir, "Binaries", "Win64");
            if (Directory.Exists(binariesPath))
            {
                return binariesPath;
            }

            // Return original path if no special structure detected
            return baseDir;
        }

        /// <summary>
        /// Forces OptiScaler to use the FSR4 INT8 software fallback on GPUs that lack native FP8 hardware.
        /// RDNA4 (Radeon RX 9000) gets FSR4 automatically with the default Fsr4ForceModel=0 (no override),
        /// but every other GPU needs Fsr4ForceModel=2 set explicitly — otherwise OptiScaler silently falls
        /// back to FSR3 and the injected FSR4 INT8 DLL never activates (it won't even show up in-game).
        /// UpscalerIndex must also be forced to 0: on "auto" it only resolves to the FSR4 backend for
        /// RDNA4, and to FSR3 for everything else, so without it Fsr4ForceModel never even gets a FSR4
        /// backend to apply to.
        /// AMD's own amdxcffx64.dll (the official 4.1.1+ driver build) additionally does its own internal
        /// GPU validation and only whitelists RDNA4/RDNA3-desktop for INT8 — Fsr4ForceModel alone isn't
        /// enough on everything else (RDNA3 mobile/APU, RDNA2, Intel, Nvidia), so Fsr4ForceEnableInt8=true
        /// and Fsr4Update=true (updates FSR3.X to FSR4, only defaults to true on RDNA4) are also needed.
        ///
        /// LoadCustomAmdxc64OnRdna2 tells OptiScaler to load a custom amdxc64.dll from OptiDllPath
        /// (".\OptiScaler\amdxc64.dll" by default). Most Extras releases don't bundle that file — it
        /// isn't produced by the FSR4 INT8 build process, it has to be sourced separately (historically,
        /// hand-extracted from an old AMD Adrenalin driver package). Setting this key with no file behind
        /// it makes OptiScaler try to load a DLL that doesn't exist, which was suspected as a cause of
        /// games failing to launch after installing FSR4 INT8 on RDNA2 (2026-08-17) — so this only gets
        /// set when that file has actually been placed there (see the copy step in the call sites, which
        /// pulls it from ComponentManagementService.GetCachedCustomAmdxc64Path if the downloaded Extras
        /// archive included one). No file there → key stays unset, same as before.
        /// </summary>
        /// <summary>
        /// Copies a downloaded RDNA2 amdxc64.dll companion into OptiDllPath (".\OptiScaler\") so
        /// ConfigureFsr4IntFallback can find it and enable LoadCustomAmdxc64OnRdna2.
        /// </summary>
        public void InstallCustomAmdxc64(string gameDir, string sourcePath)
        {
            var optiScalerDir = Path.Combine(gameDir, "OptiScaler");
            Directory.CreateDirectory(optiScalerDir);
            File.Copy(sourcePath, Path.Combine(optiScalerDir, Fsr4Int8DllHelper.CustomRdna2FileName), overwrite: true);
        }

        public void ConfigureFsr4IntFallback(string gameDir, bool isRdna4, bool isRdna2)
        {
            if (isRdna4) return;

            ModifyOptiScalerIni(gameDir, "UpscalerIndex", "0", "FSR");
            ModifyOptiScalerIni(gameDir, "Fsr4ForceModel", "2", "FSR");
            ModifyOptiScalerIni(gameDir, "Fsr4ForceEnableInt8", "true", "FSR");
            ModifyOptiScalerIni(gameDir, "Fsr4Update", "true", "FSR");

            if (isRdna2 && File.Exists(Path.Combine(gameDir, "OptiScaler", Fsr4Int8DllHelper.CustomRdna2FileName)))
                ModifyOptiScalerIni(gameDir, "LoadCustomAmdxc64OnRdna2", "true", "Plugins");
        }

        /// <summary>
        /// Modifies a setting within the given section of OptiScaler.ini, inserting the section
        /// and/or key if either is missing.
        /// </summary>
        private void ModifyOptiScalerIni(string gameDir, string key, string value, string section = "General")
        {
            var iniPath = Path.Combine(gameDir, "OptiScaler.ini");
            var sectionHeader = $"[{section}]";

            if (!File.Exists(iniPath))
            {
                // Create a basic ini file if it doesn't exist
                File.WriteAllText(iniPath, $"{sectionHeader}\n{key}={value}\n");
                return;
            }

            try
            {
                var lines = File.ReadAllLines(iniPath).ToList();
                bool keyFound = false;
                bool inTargetSection = false;

                for (int i = 0; i < lines.Count; i++)
                {
                    var line = lines[i].Trim();

                    // Check if we're in the target section
                    if (line.Equals(sectionHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        inTargetSection = true;
                        continue;
                    }

                    // Check if we've moved to another section
                    if (line.StartsWith("[") && !line.Equals(sectionHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        if (inTargetSection && !keyFound)
                        {
                            // Insert the key before the next section
                            lines.Insert(i, $"{key}={value}");
                            keyFound = true;
                            break;
                        }
                        inTargetSection = false;
                    }

                    // If we're in the target section and found the key, update it
                    if (inTargetSection && line.StartsWith($"{key}=", StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = $"{key}={value}";
                        keyFound = true;
                        break;
                    }
                }

                // If key wasn't found, add it to the end of the target section or create it
                if (!keyFound)
                {
                    if (inTargetSection)
                    {
                        lines.Add($"{key}={value}");
                    }
                    else
                    {
                        // Add the section if it doesn't exist
                        lines.Add(sectionHeader);
                        lines.Add($"{key}={value}");
                    }
                }

                File.WriteAllLines(iniPath, lines);
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[Install] Failed to modify OptiScaler.ini, creating new: {ex.Message}");
                File.WriteAllText(iniPath, $"{sectionHeader}\n{key}={value}\n");
            }
        }
    }
}
