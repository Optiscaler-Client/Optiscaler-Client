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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using OptiscalerClient.Helpers;
using OptiscalerClient.Models;
using OptiscalerClient.Views;
using static OptiscalerClient.Helpers.HttpRetryHelper;

namespace OptiscalerClient.Services
{
    public sealed record DlssNrOnAmdRelease(string Version, string DownloadUrl, string AssetName, string? Sha256);

    /// <summary>
    /// Lists, downloads and stages danielblnc/DLSS-NR-on-AMD's setup.exe (the standalone AMD Neural
    /// Rendering mod — see context gathered across this conversation for how it relates to the
    /// OptiScaler "Setup NR" experimental feature). Also owns the single, machine-wide cache of the
    /// user's own nvngx_dlssnr.dll: that file is only ever asked for once, reused for every future
    /// game, and never leaves the user's machine (local convenience caching, not redistribution —
    /// unlike a community build bundling pre-converted weights, see
    /// GameInstallationService.RedistributionRestrictedFileNames).
    /// </summary>
    public class DlssNrOnAmdService
    {
        private const string RepoOwner = "danielblnc";
        private const string RepoName = "DLSS-NR-on-AMD";
        private const string SetupExeName = "dlssnr_on_amd_setup.exe";
        public const string StagedExeFileName = SetupExeName;
        private const string CachedNvngxFileName = "nvngx_dlssnr.dll";

        // Written by the mod's DLL at game runtime (troubleshooting log), not by the setup.exe run —
        // it never exists yet when DlssNrOnAmdWizardWindow.SaveDanielModManifest snapshots the game
        // folder right after "I'm done", so it can never be captured as a "created" file the normal
        // way. Unambiguous name (nothing else in a game folder is called this), so RestoreFromManifest
        // always attempts to delete it directly instead of relying on the manifest for it.
        private const string RuntimeLogFileName = "dlssnr_on_amd.log";

        private readonly string _cacheDir;
        private readonly string _nvngxCacheDir;
        private readonly BackupStoreService _backupStore = new();
        private static HttpClient HttpClient => NetworkService.GetHttpClient();

        private static List<DlssNrOnAmdRelease>? _cachedReleases;

        public DlssNrOnAmdService()
        {
            var baseDir = AppPaths.GetAppDataRoot();
            _cacheDir = Path.Combine(baseDir, "Cache", "DlssNrOnAmd");
            _nvngxCacheDir = Path.Combine(baseDir, "Cache", "DlssNr");
        }

        // ── nvngx_dlssnr.dll — asked once, cached for every future install ──────────────

        public string CachedNvngxDlssNrPath => Path.Combine(_nvngxCacheDir, CachedNvngxFileName);

        public bool IsNvngxDlssNrCached() => File.Exists(CachedNvngxDlssNrPath);

        /// <summary>Copies the user-picked nvngx_dlssnr.dll into the shared cache. Call once, ever
        /// (per machine) — every subsequent "Setup NR" run reuses <see cref="CachedNvngxDlssNrPath"/>.</summary>
        public void ImportNvngxDlssNr(string sourcePath)
        {
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("Selected file does not exist.", sourcePath);

            Directory.CreateDirectory(_nvngxCacheDir);
            File.Copy(sourcePath, CachedNvngxDlssNrPath, overwrite: true);
            DebugWindow.Log($"[DlssNrOnAmd] Cached nvngx_dlssnr.dll ({new FileInfo(CachedNvngxDlssNrPath).Length} bytes)");
        }

        /// <summary>Removes the cached nvngx_dlssnr.dll (Manage Local Versions' dedicated page — see
        /// CacheManagementWindow.RenderNvngxDlssNr). Only affects the machine-wide cache; games that
        /// already installed the mod keep their own copy untouched (see RestoreFromManifest for
        /// uninstalling those).</summary>
        public void DeleteCachedNvngx()
        {
            if (File.Exists(CachedNvngxDlssNrPath)) File.Delete(CachedNvngxDlssNrPath);
        }

        /// <summary>File version reported by the cached nvngx_dlssnr.dll's own metadata (e.g.
        /// "310.8.0.0", matching what danielblnc's installer itself prints when it recognises the
        /// file) — null if nothing is cached or the file has no version resource.</summary>
        public string? GetCachedNvngxVersion()
        {
            if (!IsNvngxDlssNrCached()) return null;
            try
            {
                // FileVersion sometimes comes back comma-separated (e.g. "310,8,0,0") depending on how
                // the file's version resource was authored — normalize to the dotted form everyone
                // actually expects, same as GameAnalyzerService.GetFileVersion does elsewhere.
                var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(CachedNvngxDlssNrPath).FileVersion?.Replace(',', '.');
                return string.IsNullOrWhiteSpace(version) ? null : version;
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[DlssNrOnAmd] Could not read nvngx_dlssnr.dll version: {ex.Message}");
                return null;
            }
        }

        // ── Release listing (GitHub Releases API, newest-first as returned by GitHub) ──

        public async Task<List<DlssNrOnAmdRelease>> GetReleasesAsync(bool forceRefresh = false)
        {
            if (!forceRefresh && _cachedReleases != null)
                return _cachedReleases;

            var releases = new List<DlssNrOnAmdRelease>();
            try
            {
                var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=30";
                var response = await GetWithRetryAsync(() => HttpClient, url);
                DebugWindow.Log($"[DlssNrOnAmd] GET {url} -> HTTP {(int)response.StatusCode}");
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return releases;

                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (!element.TryGetProperty("tag_name", out var tagProp)) continue;
                    var version = tagProp.GetString();
                    if (string.IsNullOrEmpty(version)) continue;
                    if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                        version = version.Substring(1);

                    if (!element.TryGetProperty("assets", out var assets)) continue;

                    string? exeUrl = null, exeName = null, checksumsText = null;
                    foreach (var asset in assets.EnumerateArray())
                    {
                        if (!asset.TryGetProperty("name", out var nameProp) ||
                            !asset.TryGetProperty("browser_download_url", out var urlProp))
                            continue;
                        var name = nameProp.GetString() ?? "";
                        var assetUrl = urlProp.GetString();
                        if (assetUrl == null) continue;

                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            exeUrl = assetUrl;
                            exeName = name;
                        }
                        else if (name.Contains("sha256", StringComparison.OrdinalIgnoreCase) &&
                                 (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || !name.Contains('.')))
                        {
                            try
                            {
                                var resp = await GetWithRetryAsync(() => HttpClient, assetUrl, maxRetries: 1, timeoutSeconds: 15);
                                if (resp.IsSuccessStatusCode)
                                    checksumsText = await resp.Content.ReadAsStringAsync();
                            }
                            catch (Exception ex) { DebugWindow.Log($"[DlssNrOnAmd] Checksums fetch failed for {version}: {ex.Message}"); }
                        }
                    }

                    if (exeUrl == null || exeName == null) continue; // no usable asset — skip this release

                    var sha256 = ExtractSha256For(checksumsText, exeName);
                    releases.Add(new DlssNrOnAmdRelease(version, exeUrl, exeName, sha256));
                }

                DebugWindow.Log($"[DlssNrOnAmd] {RepoOwner}/{RepoName} -> {releases.Count} usable release(s)");
                _cachedReleases = releases;
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[DlssNrOnAmd] Release listing failed: {ex.Message}");
            }

            return releases;
        }

        /// <summary>Parses a "SHA256SUMS.txt"-style checksums file (one "&lt;hash&gt; *&lt;filename&gt;"
        /// or "&lt;hash&gt;  &lt;filename&gt;" line per file) for the hash matching the given asset name.</summary>
        private static string? ExtractSha256For(string? checksumsText, string assetName)
        {
            if (string.IsNullOrEmpty(checksumsText)) return null;
            foreach (var line in checksumsText.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                var parts = trimmed.Split(new[] { ' ', '*' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                if (parts[^1].EndsWith(assetName, StringComparison.OrdinalIgnoreCase))
                    return parts[0];
            }
            return null;
        }

        // ── Download + cache ─────────────────────────────────────────────────────────

        public string CacheRootPath => _cacheDir;
        public string GetCachePath(string version) => Path.Combine(_cacheDir, SanitizeVersionName(version));
        public string GetCachedExePath(string version) => Path.Combine(GetCachePath(version), SetupExeName);
        public bool IsCached(string version) => File.Exists(GetCachedExePath(version));

        public void DeleteCache(string version)
        {
            var dir = GetCachePath(version);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }

        public List<string> GetDownloadedVersions()
        {
            if (!Directory.Exists(_cacheDir)) return new List<string>();
            return Directory.GetDirectories(_cacheDir)
                .Where(d => File.Exists(Path.Combine(d, SetupExeName)))
                .Select(Path.GetFileName)
                .Where(n => n != null)
                .Select(n => n!)
                .ToList();
        }

        // Tracks an in-flight download per version so a caller that starts one in the background
        // (Setup NR's Save button) and a later caller that needs the finished file (the install
        // wizard, or Manage Local Versions polling status) share the same task instead of racing
        // two writers on the same temp file.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string>> _inFlightDownloads = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>True while a download for this version is in progress (started by any caller,
        /// e.g. Setup NR's background download) — used by Manage Local Versions to show status.</summary>
        public bool IsDownloading(string version) => _inFlightDownloads.ContainsKey(version);

        /// <summary>Downloads the setup.exe for the given version if not already cached. Verifies
        /// against the release's published SHA256 when one was found (integrity, not authenticity —
        /// see the "mini security check" notes in the Setup NR plan). Never overwrites an existing
        /// cached copy silently on a hash mismatch: throws instead, so a corrupted/tampered download
        /// is never staged into a game folder. Safe to call concurrently for the same version — e.g.
        /// once to kick off a background download and again later to await its completion — both
        /// calls share the same underlying task rather than downloading twice.</summary>
        public Task<string> DownloadAsync(string version, IProgress<double>? progress = null)
        {
            if (IsCached(version))
                return Task.FromResult(GetCachedExePath(version));

            return _inFlightDownloads.GetOrAdd(version, v => DownloadCoreAsync(v, progress));
        }

        private async Task<string> DownloadCoreAsync(string version, IProgress<double>? progress)
        {
            try
            {
                return await DownloadCoreInnerAsync(version, progress);
            }
            finally
            {
                _inFlightDownloads.TryRemove(version, out _);
            }
        }

        private async Task<string> DownloadCoreInnerAsync(string version, IProgress<double>? progress)
        {
            var releases = await GetReleasesAsync();
            var release = releases.FirstOrDefault(r => string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase))
                ?? throw new VersionUnavailableException(version, "Release not found or has no .exe asset.");

            var destDir = GetCachePath(version);
            Directory.CreateDirectory(destDir);
            var destPath = GetCachedExePath(version);
            var tempPath = destPath + ".download";

            try
            {
                DebugWindow.Log($"[DlssNrOnAmd] Downloading {release.DownloadUrl}");
                await StreamToFileAsync(release.DownloadUrl, tempPath, progress);

                if (!string.IsNullOrEmpty(release.Sha256))
                {
                    var actual = ComputeSha256(tempPath);
                    if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(tempPath);
                        throw new InvalidOperationException(
                            $"Downloaded file hash mismatch for {release.AssetName} — expected {release.Sha256}, got {actual}. Discarded.");
                    }
                    DebugWindow.Log($"[DlssNrOnAmd] SHA256 verified for {release.AssetName}");
                }
                else
                {
                    DebugWindow.Log($"[DlssNrOnAmd] No published checksum for {release.AssetName} — skipped verification.");
                }

                File.Move(tempPath, destPath, overwrite: true);
                return destPath;
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
                throw;
            }
        }

        private static async Task StreamToFileAsync(string url, string destPath, IProgress<double>? progress)
        {
            using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? -1L;

            await using var httpStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = File.Create(destPath);
            var buffer = new byte[81920];
            long readSoFar = 0;
            int read;
            while ((read = await httpStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read));
                readSoFar += read;
                if (totalBytes > 0)
                    progress?.Report((double)readSoFar / totalBytes);
            }
        }

        private static string ComputeSha256(string filePath)
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hash = sha256.ComputeHash(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static string SanitizeVersionName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(invalid.Contains(c) ? '_' : c);
            return sb.ToString();
        }

        // ── Staging (the interactive part is never automated, see Setup NR plan) ──

        /// <summary>Copies the cached setup.exe (and the cached nvngx_dlssnr.dll, if present) into
        /// the game folder and tags the exe with Mark-of-the-Web so Windows Defender/SmartScreen treat
        /// it exactly as a browser download would. Does NOT launch it — Process.Start from within this
        /// app tripped Defender's real-time AV check and got the file quarantined outright, with no
        /// recovery short of restoring it from quarantine; the user double-clicking it themselves from
        /// Explorer goes through the normal, recoverable "Windows protected your PC" flow instead.
        /// Returns the staged exe's path so the caller can point the user at it.</summary>
        public string Stage(string version, string gameDir)
        {
            var cachedExe = GetCachedExePath(version);
            if (!File.Exists(cachedExe))
                throw new FileNotFoundException("Setup.exe is not cached for this version. Download it first.", cachedExe);

            var stagedExe = Path.Combine(gameDir, SetupExeName);
            File.Copy(cachedExe, stagedExe, overwrite: true);
            ApplyMarkOfTheWeb(stagedExe);

            if (IsNvngxDlssNrCached())
            {
                var stagedNvngx = Path.Combine(gameDir, CachedNvngxFileName);
                if (!File.Exists(stagedNvngx))
                    File.Copy(CachedNvngxDlssNrPath, stagedNvngx, overwrite: false);
            }

            DebugWindow.Log($"[DlssNrOnAmd] Staged {stagedExe}");
            return stagedExe;
        }

        // ── Install session tracking (shared by the manual wizard and the headless auto-install) ──

        /// <summary>Opaque handle for one daniel-mod install attempt — the before/after snapshot and
        /// backup-key needed to later diff what changed and save an InstallationManifest. Local state
        /// (not fields on this service instance) so a single DlssNrOnAmdService instance can track
        /// multiple installs without them colliding.</summary>
        public sealed class InstallSession
        {
            internal readonly string GameDir;
            internal readonly string ManifestStoreKey;
            internal readonly Dictionary<string, (long Size, DateTime WriteUtc)> BeforeSnapshot;

            internal InstallSession(string gameDir, string manifestStoreKey, Dictionary<string, (long, DateTime)> beforeSnapshot)
            {
                GameDir = gameDir;
                ManifestStoreKey = manifestStoreKey;
                BeforeSnapshot = beforeSnapshot;
            }
        }

        /// <summary>Snapshots the game folder and backs up every pre-existing top-level DLL (so a proxy
        /// DLL choice that happens to collide with one of the game's own is restorable later) — call
        /// once right before the installer actually runs (after Stage()'s own copy), whether that run
        /// is driven by hand (the wizard) or automated (RunAutomatedInstallAsync).</summary>
        public InstallSession BeginInstallSession(string gameDir)
        {
            var manifestStoreKey = gameDir + "::dlssnr";
            var beforeSnapshot = SnapshotTopLevel(gameDir);
            foreach (var name in beforeSnapshot.Keys)
            {
                if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, CachedNvngxFileName, StringComparison.OrdinalIgnoreCase))
                    _backupStore.BackupFile(manifestStoreKey, gameDir, name);
            }
            return new InstallSession(gameDir, manifestStoreKey, beforeSnapshot);
        }

        /// <summary>Checks for the weights marker and, if present, saves the InstallationManifest and
        /// records the mod as installed on <paramref name="game"/>. Returns false (no error, this is
        /// the normal "not done yet" state) when the marker isn't there yet — safe to call repeatedly
        /// while polling.</summary>
        public bool TryFinishInstall(InstallSession session, Game game, string danielVersion, bool isModeB)
        {
            var markerPath = Path.Combine(session.GameDir, "dlssnr_on_amd_weights.bin");
            if (!File.Exists(markerPath)) return false;

            SaveDanielModManifest(session);

            // Resolve the pending mode regardless of outcome — the wizard step is "used up" either
            // way, a failed wrapper install shouldn't leave a stale pending flag prompting a repeat.
            game.PendingDlssNrOnAmdMode = null;
            game.PendingDlssNrOnAmdVersion = null;

            // Mode B (daniel-and-opti): the weights are generated — the caller (ManageGameWindow's
            // Install button) is expected to fall through to its own normal InstallOptiScaler
            // afterwards once it sees this return true — we only run danielblnc's setup here, not the
            // wrapper itself.
            game.IsDlssNrOnAmdInstalled = true;
            game.DlssNrOnAmdVersion = danielVersion;
            game.InstalledDlssNrOnAmdMode = isModeB ? "daniel-and-opti" : "daniel-only";
            return true;
        }

        /// <summary>Diffs the game folder against the session's baseline and saves an
        /// InstallationManifest recording what changed — new files (including our own staged exe/nvngx)
        /// as FilesCreated, and any top-level DLL that changed and was preemptively backed up as
        /// FilesOverwritten. A file that changed but wasn't backed up (not a top-level DLL) is logged
        /// only — there's nothing to restore it from later.</summary>
        private void SaveDanielModManifest(InstallSession session)
        {
            var after = SnapshotTopLevel(session.GameDir);
            var manifest = new InstallationManifest
            {
                OperationStatus = "committed",
                StartedAtUtc = DateTime.UtcNow.ToString("O"),
                FinishedAtUtc = DateTime.UtcNow.ToString("O"),
                IncludesOptiscaler = false,
                InstalledGameDirectory = session.GameDir,
            };

            var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { StagedExeFileName };
            if (File.Exists(Path.Combine(session.GameDir, CachedNvngxFileName))) created.Add(CachedNvngxFileName);

            foreach (var (name, info) in after)
            {
                if (!session.BeforeSnapshot.TryGetValue(name, out var before))
                {
                    created.Add(name);
                }
                else if (before.Size != info.Size || before.WriteUtc != info.WriteUtc)
                {
                    if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(name, CachedNvngxFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        manifest.FilesOverwritten.Add(new ManifestFileRecord
                        {
                            RelativePath = name,
                            BackupRelativePath = name,
                            ExistedBefore = true,
                        });
                    }
                    else
                    {
                        DebugWindow.Log($"[SetupNr] '{name}' changed during install but wasn't backed up (not a top-level .dll) — won't be restorable on uninstall.");
                    }
                }
            }

            foreach (var name in created)
                manifest.FilesCreated.Add(new ManifestFileRecord { RelativePath = name, ExistedBefore = false });

            _backupStore.SaveManifest(session.ManifestStoreKey, manifest);
        }

        private static Dictionary<string, (long Size, DateTime WriteUtc)> SnapshotTopLevel(string gameDir)
        {
            var result = new Dictionary<string, (long, DateTime)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var file in Directory.GetFiles(gameDir))
                {
                    var info = new FileInfo(file);
                    result[info.Name] = (info.Length, info.LastWriteTimeUtc);
                }
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[SetupNr] Could not snapshot '{gameDir}': {ex.Message}");
            }
            return result;
        }

        // ── Headless automated install ("Auto Install") ──────────────────────────────
        //
        // Drives danielblnc's installer through its two known interactive prompts (confirmed against a
        // real transcript) via a redirected, hidden process — no window, no visible console at all:
        //   1) "Use this folder? [Y/n]" → "y"
        //   2) "...Press Enter to accept, or type another name (...):" for the proxy DLL → blank/Enter
        //      for mode A (any name works), or "dbghelp" for mode B (OptiScaler will claim dxgi.dll for
        //      itself, so daniel's mod needs a different one — dbghelp is always in the offered list).
        // Everything after that (weight conversion, then a "Done... Press Enter to close." prompt) runs
        // unattended and needs no more input from us — success is determined purely by the weights
        // marker appearing on disk, so once it does, the process is killed outright rather than waited
        // on to close itself gracefully. An earlier version tried to also answer that final "Press
        // Enter to close" prompt for a clean exit, on a timeout/best-effort basis if it didn't see that
        // exact text in time — but any failure along that path (an exception, or the 15-minute grace
        // deadline simply lapsing) left the child running with nothing left to kill it, which held the
        // exe file locked indefinitely (only released when the whole app — and with it every handle to
        // the child — finally exited). Killing it the moment we know the mod is installed removes that
        // whole failure class instead of trying to enumerate every way "wait for a graceful exit" could
        // go wrong.
        //
        // Reads with ReadAsync into a char buffer rather than ReadLine — these prompts don't end in a
        // newline, so a line-based read would hang forever. Exactly one ReadAsync is ever in flight on
        // the stream at a time (a pending read is reused across poll ticks, never abandoned and
        // replaced) — StreamReader throws if a second call starts before the first completes, which
        // silently killed an earlier version of this loop the first time a real gap in the installer's
        // output (e.g. during "Converting weights...") exceeded the poll tick.
        private enum AutomationStage { WaitFolder, WaitDllName, Draining }

        /// <summary>RequiresElevation is its own outcome (not just Failed) because it isn't really a
        /// failure of the automation itself — the game folder (e.g. under Program Files) needs
        /// administrator rights, and a headless CreateProcess launch can never silently elevate the way
        /// a visible ShellExecute one can. The caller needs to know this specifically so it doesn't
        /// also reset an unrelated, just-confirmed Defender exclusion (see
        /// ManageGameWindow.RunDanielModAutoInstallAsync) — this has nothing to do with Defender at
        /// all.</summary>
        public enum AutomatedInstallResult { Success, Failed, RequiresElevation }

        /// <summary>Attempts a fully automated install. Returns Success once the weights marker is
        /// detected (the mod is installed and <paramref name="game"/>'s fields are updated — same
        /// outcome as a successful manual run; the child process is killed at that point rather than
        /// waited on to exit on its own, see the class notes above). Returns RequiresElevation if the
        /// staged exe's own manifest demands admin rights (ERROR_ELEVATION_REQUIRED — CreateProcess
        /// can't silently elevate, unlike a visible ShellExecute launch), or Failed if the known prompts
        /// couldn't be driven in time (installer changed, unexpected state) or the weights marker never
        /// showed up within the overall timeout — the process is killed in every non-Success case, never
        /// left running. Never shows any UI; the caller decides what to tell the user on failure (e.g.
        /// "use Manual Install instead").</summary>
        public async Task<AutomatedInstallResult> RunAutomatedInstallAsync(Game game, string gameDir, string danielVersion, bool isModeB)
        {
            var stagedExe = Path.Combine(gameDir, StagedExeFileName);
            var session = BeginInstallSession(gameDir);

            Process? proc;
            try
            {
                proc = Process.Start(new ProcessStartInfo
                {
                    FileName = stagedExe,
                    WorkingDirectory = gameDir,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                });
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740) // ERROR_ELEVATION_REQUIRED
            {
                DebugWindow.Log("[SetupNr] Automated install needs admin rights (the game folder requires elevation) — can't run headlessly.");
                return AutomatedInstallResult.RequiresElevation;
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[SetupNr] Could not start automated install: {ex.Message}");
                return AutomatedInstallResult.Failed;
            }
            if (proc == null) return AutomatedInstallResult.Failed;

            _ = DrainStderrAsync(proc);

            // Completed once both prompts are answered (true) or a failure is detected (false) —
            // DriveInstallerAsync itself keeps running past that point (see its own notes) purely to
            // keep draining stdout so the child never blocks on a full pipe buffer, all the way until
            // this method kills it below.
            var promptsAnswered = new TaskCompletionSource<bool>();
            var expectedExeFileName = Path.GetFileName(game.ExecutablePath);
            _ = DriveInstallerAsync(proc, isModeB, expectedExeFileName, promptsAnswered);

            bool success;
            if (!await promptsAnswered.Task)
            {
                success = false;
            }
            else
            {
                var markerPath = Path.Combine(gameDir, "dlssnr_on_amd_weights.bin");
                var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(15);
                success = false;
                while (DateTime.UtcNow < deadline)
                {
                    if (File.Exists(markerPath)) { success = true; break; }
                    if (proc.HasExited) { success = File.Exists(markerPath); break; }
                    await Task.Delay(500);
                }
            }

            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return success && TryFinishInstall(session, game, danielVersion, isModeB)
                ? AutomatedInstallResult.Success
                : AutomatedInstallResult.Failed;
        }

        /// <summary>Answers the known prompts, then just keeps draining stdout (see class notes —
        /// no more input is ever needed after the DLL-name prompt, but something still has to keep
        /// reading so the child doesn't block on a full pipe buffer once weight conversion starts
        /// printing progress). Runs until the process exits or RunAutomatedInstallAsync kills it.</summary>
        private static async Task DriveInstallerAsync(Process proc, bool isModeB, string? expectedExeFileName, TaskCompletionSource<bool> promptsAnswered)
        {
            var stage = AutomationStage.WaitFolder;
            var buffer = new StringBuilder();
            var charBuf = new char[512];
            var stageDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            Task<int>? pendingRead = null;

            void Fail()
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                promptsAnswered.TrySetResult(false);
            }

            try
            {
                while (stage != AutomationStage.Draining)
                {
                    if (proc.HasExited) { Fail(); return; }

                    pendingRead ??= proc.StandardOutput.ReadAsync(charBuf, 0, charBuf.Length);
                    if (await Task.WhenAny(pendingRead, Task.Delay(500)) == pendingRead)
                    {
                        int n = await pendingRead;
                        pendingRead = null;
                        if (n <= 0) { Fail(); return; }
                        var chunk = new string(charBuf, 0, n);
                        DebugWindow.Log($"[SetupNr] {chunk}");
                        buffer.Append(chunk);
                    }

                    switch (stage)
                    {
                        case AutomationStage.WaitFolder:
                            if (buffer.ToString().Contains("Use this folder?", StringComparison.OrdinalIgnoreCase))
                            {
                                await proc.StandardInput.WriteLineAsync("y");
                                await proc.StandardInput.FlushAsync();
                                buffer.Clear();
                                stage = AutomationStage.WaitDllName;
                                stageDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
                            }
                            else if (DateTime.UtcNow > stageDeadline) { Fail(); return; }
                            break;

                        case AutomationStage.WaitDllName:
                            var pending = buffer.ToString();
                            // Only asked when the folder has more than one executable in it (e.g.
                            // Cyberpunk 2077's bin\x64 also has REDEngineErrorReporter.exe) — never
                            // guaranteed to appear, so this must not be its own strict stage between
                            // WaitFolder and the DLL-name prompt, just an optional detour handled here.
                            if (pending.Contains("Which one is the game?", StringComparison.OrdinalIgnoreCase))
                            {
                                var choice = PickGameExecutableChoice(pending, expectedExeFileName);
                                DebugWindow.Log($"[SetupNr] Multiple executables found, choosing: '{choice}'");
                                await proc.StandardInput.WriteLineAsync(choice);
                                await proc.StandardInput.FlushAsync();
                                buffer.Clear();
                                stageDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
                            }
                            else if (pending.Contains("Press Enter to accept", StringComparison.OrdinalIgnoreCase))
                            {
                                await proc.StandardInput.WriteLineAsync(isModeB ? "dbghelp" : "");
                                await proc.StandardInput.FlushAsync();
                                promptsAnswered.TrySetResult(true);
                                stage = AutomationStage.Draining;
                            }
                            else if (DateTime.UtcNow > stageDeadline) { Fail(); return; }
                            break;
                    }
                }

                // No more prompts to answer — just keep the pipe from backing up until
                // RunAutomatedInstallAsync kills the process (weights found or overall timeout).
                while (!proc.HasExited)
                {
                    int n = await proc.StandardOutput.ReadAsync(charBuf, 0, charBuf.Length);
                    if (n <= 0) break;
                    DebugWindow.Log($"[SetupNr] {new string(charBuf, 0, n)}");
                }
            }
            catch
            {
                // Process was killed out from under this read (the normal way this loop ends once
                // RunAutomatedInstallAsync is satisfied) or exited/closed its pipes — nothing to do.
            }
        }

        /// <summary>Picks which numbered executable to answer "Which one is the game?" with, by
        /// elimination so this can never get stuck waiting on a choice with no clearly-right answer:
        /// (1) an exact filename match against the game's own known .exe (most reliable — we already
        /// know exactly which file the user picked as this game), (2) otherwise trust the installer's
        /// own guess (its "Press Enter for [N]" default, usually the one it also flagged "probably the
        /// game") by sending a blank line, (3) otherwise the first listed option, (4) otherwise just a
        /// blank line — always answers *something*.</summary>
        private static string PickGameExecutableChoice(string promptText, string? expectedExeFileName)
        {
            var options = Regex.Matches(promptText, @"^\s*\[(\d+)\]\s+(\S+)", RegexOptions.Multiline);

            if (!string.IsNullOrEmpty(expectedExeFileName))
            {
                foreach (Match m in options)
                    if (string.Equals(m.Groups[2].Value, expectedExeFileName, StringComparison.OrdinalIgnoreCase))
                        return m.Groups[1].Value;
            }

            if (Regex.IsMatch(promptText, @"Press Enter for \[\d+\]", RegexOptions.IgnoreCase)) return "";

            return options.Count > 0 ? options[0].Groups[1].Value : "";
        }

        /// <summary>Continuously drains stderr for the process's whole lifetime, logging each chunk —
        /// not for prompt-matching (the installer's prompts are all on stdout), just so the OS pipe
        /// buffer for the redirected stderr stream never fills up and blocks the child, which would
        /// otherwise be a real risk here since we redirect it but would never otherwise read it.</summary>
        private static async Task DrainStderrAsync(Process proc)
        {
            var buf = new char[512];
            try
            {
                while (!proc.HasExited)
                {
                    int n = await proc.StandardError.ReadAsync(buf, 0, buf.Length);
                    if (n <= 0) break;
                    DebugWindow.Log($"[SetupNr:err] {new string(buf, 0, n)}");
                }
            }
            catch { /* process likely exited/disposed */ }
        }

        /// <summary>Deletes every file SaveDanielModManifest (DlssNrOnAmdWizardWindow) recorded as
        /// created during the daniel-only install, and restores whatever it backed up as overwritten —
        /// the uninstall counterpart, reusing the same storeKey convention (gameDir + "::dlssnr") so it
        /// never collides with OptiScaler's own manifest for the same game folder. No-op if no manifest
        /// was ever saved (e.g. install never actually completed).</summary>
        public void RestoreFromManifest(string gameDir)
        {
            var storeKey = gameDir + "::dlssnr";
            var manifest = _backupStore.LoadManifest(storeKey);
            if (manifest == null) return;

            DebugWindow.Log($"[DlssNrOnAmd] Uninstalling from manifest: created=[{string.Join(", ", manifest.FilesCreated.Select(f => f.RelativePath))}], overwritten=[{string.Join(", ", manifest.FilesOverwritten.Select(f => f.RelativePath))}]");

            // OptiScaler's own manifest for this same game folder (plain gameDir key, no "::dlssnr"
            // suffix — see SaveDanielModManifest) — checked before deleting any file so "Mode B"
            // (daniel + OptiScaler) never rips out a proxy DLL OptiScaler currently depends on, even
            // if this manifest's own created/overwritten split ever got it wrong.
            var optiManifest = _backupStore.LoadManifest(gameDir);

            foreach (var f in manifest.FilesCreated)
            {
                if (optiManifest != null && optiManifest.InstalledFiles.Contains(f.RelativePath, StringComparer.OrdinalIgnoreCase))
                {
                    DebugWindow.Log($"[DlssNrOnAmd] Skipping delete of '{f.RelativePath}' — OptiScaler's own manifest claims it.");
                    continue;
                }
                var path = Path.Combine(gameDir, f.RelativePath);
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) { DebugWindow.Log($"[DlssNrOnAmd] Could not delete '{path}': {ex.Message}"); }
            }
            foreach (var f in manifest.FilesOverwritten)
            {
                try { _backupStore.RestoreFile(storeKey, gameDir, f.RelativePath, f.BackupRelativePath); }
                catch (Exception ex) { DebugWindow.Log($"[DlssNrOnAmd] Could not restore '{f.RelativePath}': {ex.Message}"); }
            }

            // Not manifest-tracked (see RuntimeLogFileName) — only ever this mod's, so always safe.
            try
            {
                var logPath = Path.Combine(gameDir, RuntimeLogFileName);
                if (File.Exists(logPath)) File.Delete(logPath);
            }
            catch (Exception ex) { DebugWindow.Log($"[DlssNrOnAmd] Could not delete '{RuntimeLogFileName}': {ex.Message}"); }

            _backupStore.DeleteBackup(storeKey);
        }

        /// <summary>Walks the exception (and any InnerException) looking for the ERROR_VIRUS_INFECTED
        /// signature — either the Win32 code itself or the message text, since callers wrap the
        /// original Win32Exception and the wrapping shape isn't guaranteed to preserve NativeErrorCode
        /// cleanly across every .NET/Windows version. Lives here (rather than in whichever window
        /// happens to trigger the download/copy) since it's about interpreting failures of this
        /// service's own operations.</summary>
        public static bool IsSmartScreenBlock(Exception? ex)
        {
            for (; ex != null; ex = ex.InnerException)
            {
                if (ex is System.ComponentModel.Win32Exception win32ex && win32ex.NativeErrorCode == 1260)
                    return true;
                if (ex.Message.Contains("virus", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("potentially unwanted software", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Writes the NTFS Zone.Identifier alternate data stream that marks a file as
        /// downloaded from the internet — the same marker a browser writes, which is what actually
        /// arms Windows Defender/SmartScreen's "downloaded file" checks on execution. A plain
        /// HttpClient download has no such marker and silently skips those checks otherwise.</summary>
        private static void ApplyMarkOfTheWeb(string filePath)
        {
            try
            {
                File.WriteAllText(filePath + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            }
            catch (Exception ex)
            {
                // Best effort — not all filesystems/configurations support ADS (e.g. non-NTFS volumes).
                DebugWindow.Log($"[DlssNrOnAmd] Could not apply Mark-of-the-Web to '{filePath}': {ex.Message}");
            }
        }
    }
}
