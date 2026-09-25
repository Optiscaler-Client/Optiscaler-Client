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
using System.IO.Compression;
using System.Formats.Tar;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OptiscalerClient.Models;
using OptiscalerClient.Views;
using static OptiscalerClient.Helpers.HttpRetryHelper;

namespace OptiscalerClient.Services
{
    /// <summary>Outcome of <see cref="DlssNrLinuxWrapperService.RunAutoInstallAsync"/>. On failure,
    /// RawError is the fork's own "Error: ..." message from stderr (exit code 2) — surfaced to the
    /// user as-is since it's already a clear, specific diagnostic (anti-cheat detected, game still
    /// running, unsupported GPU, etc.), not something worth re-wording.</summary>
    public sealed record LinuxWrapperInstallResult(bool Success, string? CommandPrefix, string? LaunchOptions, string? GpuName, List<string> Warnings, string? RawError);

    /// <summary>One Wine/Proton runner candidate reported by `list-protons --json`.</summary>
    public sealed record LinuxWrapperRunner(string Path, bool Compatible, string? Reason);

    /// <summary>A host tool the Linux fork needs that can be missing on a fresh system: Python 3.11+
    /// for its installer itself, and gcc/g++ for its optimized lmxxf backend (RDNA4 / gfx1201).</summary>
    public enum LinuxForkRequirement { Python311, BuildTools }

    /// <summary>
    /// Lists, downloads, extracts and runs bulacha3/DLSS-NR-on-AMD-Linux — an unofficial,
    /// experimental third-party fork (based on guentra's original Linux port, which stopped at
    /// danielblnc v0.2.12; bulacha3's tracks v0.3.x and adds lmxxf's optimized RDNA4 backend, and
    /// is the one danielblnc recommends) that lets danielblnc's DLSS-NR-on-AMD Neural Rendering mod
    /// actually run on Linux/Proton. Windows HIP calls from the mod's own DLL can never see a real
    /// GPU architecture through Wine (Wine only ever exposes a cosmetic wined3d GPU-description
    /// table to D3DKMT, not a real compute driver — see context/dlssnr-on-amd-linux-setup.md for the
    /// full investigation this conclusion is based on); the fork instead bridges those HIP
    /// calls to a real native ROCm runtime via LD_PRELOAD, which is the only way this mod has been
    /// confirmed to work at all under Proton. This is the Linux counterpart to DlssNrOnAmdService
    /// (same repo-release/cache-once shape) — not affiliated with bulacha3, guentra or danielblnc, credited in
    /// the UI wherever this is offered, and never used on Windows (danielblnc's own installer is used
    /// there directly, see DlssNrOnAmdService).
    /// </summary>
    public class DlssNrLinuxWrapperService
    {
        private const string RepoOwner = "bulacha3";
        private const string RepoName = "DLSS-NR-on-AMD-Linux";
        private const string AssetName = "dlssnr-linux-portable.tar.gz";
        public const string ExtractedFolderName = "dlssnr-linux-portable";

        private readonly string _cacheDir;
        private readonly string _releasesCacheFile;
        private static HttpClient HttpClient => NetworkService.GetHttpClient();

        private static List<DlssNrOnAmdRelease>? _cachedReleases;
        private static DateTime _cachedReleasesLastUpdated = DateTime.MinValue;
        private static bool _releasesLoadedFromDisk;
        private static readonly object _releasesCacheLock = new();

        // Same rationale as DlssNrOnAmdService's own ReleasesCacheTtl: without an expiry, a release
        // fetched once would stay cached forever across every future app launch.
        private static readonly TimeSpan ReleasesCacheTtl = TimeSpan.FromHours(12);

        public DlssNrLinuxWrapperService()
        {
            var baseDir = AppPaths.GetAppDataRoot();
            _cacheDir = Path.Combine(baseDir, "Cache", "DlssNrLinuxWrapper");
            // Renamed with the switch from guentra's fork to bulacha3's, so guentra's cached release
            // list is never offered again.
            _releasesCacheFile = Path.Combine(baseDir, "dlssnr_linux_fork_releases_cache.json");

            lock (_releasesCacheLock)
            {
                if (!_releasesLoadedFromDisk)
                {
                    _releasesLoadedFromDisk = true;
                    try
                    {
                        if (File.Exists(_releasesCacheFile))
                        {
                            var loaded = JsonSerializer.Deserialize(File.ReadAllText(_releasesCacheFile), OptimizerContext.Default.DlssNrOnAmdReleasesCache);
                            if (loaded != null && loaded.Releases.Count > 0)
                            {
                                _cachedReleases = loaded.Releases;
                                _cachedReleasesLastUpdated = loaded.LastUpdated;
                                DebugWindow.Log($"[DlssNrLinuxWrapper] Loaded {loaded.Releases.Count} release(s) from local cache (last updated: {loaded.LastUpdated}).");
                            }
                        }
                    }
                    catch (Exception ex) { DebugWindow.Log($"[DlssNrLinuxWrapper] Failed to load releases cache: {ex.Message}"); }
                }
            }
        }

        // ── Release listing (GitHub Releases API) ──────────────────────────────────────

        public async Task<List<DlssNrOnAmdRelease>> GetReleasesAsync(bool forceRefresh = false)
        {
            if (!forceRefresh && _cachedReleases != null && DateTime.UtcNow - _cachedReleasesLastUpdated < ReleasesCacheTtl)
                return _cachedReleases;

            var releases = new List<DlssNrOnAmdRelease>();
            try
            {
                var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=30";
                var response = await GetWithRetryAsync(() => HttpClient, url);
                DebugWindow.Log($"[DlssNrLinuxWrapper] GET {url} -> HTTP {(int)response.StatusCode}");
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return releases;

                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (!element.TryGetProperty("tag_name", out var tagProp)) continue;
                    var version = tagProp.GetString();
                    if (string.IsNullOrEmpty(version)) continue;

                    if (!element.TryGetProperty("assets", out var assets)) continue;

                    // Checksum deliberately NOT fetched here — same reasoning as
                    // DlssNrOnAmdService.GetReleasesAsync: this list is refreshed on every app
                    // launch, so fetching every release's .sha256 sidecar here would be one extra
                    // request per release for a list nobody has opened yet. Deferred to download
                    // time (FetchChecksumForReleaseAsync), for the one version actually installed.
                    string? tarUrl = null, tarName = null;
                    foreach (var asset in assets.EnumerateArray())
                    {
                        if (!asset.TryGetProperty("name", out var nameProp) ||
                            !asset.TryGetProperty("browser_download_url", out var urlProp))
                            continue;
                        var name = nameProp.GetString() ?? "";
                        var assetUrl = urlProp.GetString();
                        if (assetUrl != null && string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
                        {
                            tarUrl = assetUrl;
                            tarName = name;
                            break;
                        }
                    }

                    if (tarUrl == null || tarName == null) continue; // no usable asset — skip this release

                    releases.Add(new DlssNrOnAmdRelease(version, tarUrl, tarName, null));
                }

                DebugWindow.Log($"[DlssNrLinuxWrapper] {RepoOwner}/{RepoName} -> {releases.Count} usable release(s)");
                _cachedReleases = releases;
                if (releases.Count > 0)
                {
                    try
                    {
                        _cachedReleasesLastUpdated = DateTime.UtcNow;
                        var cache = new DlssNrOnAmdReleasesCache { LastUpdated = _cachedReleasesLastUpdated, Releases = releases };
                        File.WriteAllText(_releasesCacheFile, JsonSerializer.Serialize(cache, OptimizerContext.Default.DlssNrOnAmdReleasesCache));
                    }
                    catch (Exception ex) { DebugWindow.Log($"[DlssNrLinuxWrapper] Failed to save releases cache: {ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[DlssNrLinuxWrapper] Release listing failed: {ex.Message}");
                // Offline / GitHub rate limit: keep offering the last known list, even if stale.
                if (_cachedReleases != null) return _cachedReleases;
            }

            return releases;
        }

        /// <summary>Fetches the SHA256 for one specific release's tar.gz asset by re-querying that
        /// tag's own sidecar ".sha256" asset — only called at download time for the version actually
        /// being installed (see GetReleasesAsync's own comment on why this isn't done for the list).</summary>
        private async Task<string?> FetchChecksumForReleaseAsync(string version, string tarAssetName)
        {
            try
            {
                var apiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/tags/{version}";
                var resp = await GetWithRetryAsync(() => HttpClient, apiUrl, maxRetries: 2, timeoutSeconds: 15);
                if (!resp.IsSuccessStatusCode) return null;

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (!doc.RootElement.TryGetProperty("assets", out var assets)) return null;

                foreach (var asset in assets.EnumerateArray())
                {
                    if (!asset.TryGetProperty("name", out var nameProp) ||
                        !asset.TryGetProperty("browser_download_url", out var urlProp))
                        continue;
                    var name = nameProp.GetString() ?? "";
                    var assetUrl = urlProp.GetString();
                    if (assetUrl == null || !string.Equals(name, tarAssetName + ".sha256", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var sumResp = await GetWithRetryAsync(() => HttpClient, assetUrl, maxRetries: 1, timeoutSeconds: 15);
                    if (!sumResp.IsSuccessStatusCode) continue;
                    var text = await sumResp.Content.ReadAsStringAsync();
                    // Sidecar format is "<hash>  <filename>" (verified against the real asset in this
                    // project's context/dlssnr-on-amd-linux-setup.md investigation).
                    return text.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                }
            }
            catch (Exception ex) { DebugWindow.Log($"[DlssNrLinuxWrapper] Checksum lookup for {version} failed: {ex.Message}"); }
            return null;
        }

        // ── Download + cache ────────────────────────────────────────────────────────────

        public string CacheRootPath => _cacheDir;
        public string GetCachePath(string version) => Path.Combine(_cacheDir, SanitizeVersionName(version));
        public string GetCachedTarPath(string version) => Path.Combine(GetCachePath(version), AssetName);
        public bool IsCached(string version) => File.Exists(GetCachedTarPath(version));

        public void DeleteCache(string version)
        {
            var dir = GetCachePath(version);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }

        public List<string> GetDownloadedVersions()
        {
            if (!Directory.Exists(_cacheDir)) return new List<string>();
            return Directory.GetDirectories(_cacheDir)
                .Where(d => File.Exists(Path.Combine(d, AssetName)))
                .Select(Path.GetFileName)
                .Where(n => n != null)
                .Select(n => n!)
                .ToList();
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string>> _inFlightDownloads = new(StringComparer.OrdinalIgnoreCase);

        public bool IsDownloading(string version) => _inFlightDownloads.ContainsKey(version);

        /// <summary>Downloads the tar.gz for the given version if not already cached, verifying its
        /// SHA256 against the release's own ".sha256" sidecar when available. Mirrors
        /// DlssNrOnAmdService.DownloadAsync's shape exactly (shared in-flight-task dictionary, never
        /// silently overwrites a cached file on a hash mismatch).</summary>
        public Task<string> DownloadAsync(string version, IProgress<double>? progress = null)
        {
            if (IsCached(version))
                return Task.FromResult(GetCachedTarPath(version));

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
                ?? throw new VersionUnavailableException(version, "Release not found or has no usable asset.");

            var destDir = GetCachePath(version);
            Directory.CreateDirectory(destDir);
            var destPath = GetCachedTarPath(version);
            var tempPath = destPath + ".download";

            try
            {
                DebugWindow.Log($"[DlssNrLinuxWrapper] Downloading {release.DownloadUrl}");
                await StreamToFileAsync(release.DownloadUrl, tempPath, progress);

                var expectedSha256 = release.Sha256 ?? await FetchChecksumForReleaseAsync(release.Version, release.AssetName);
                if (!string.IsNullOrEmpty(expectedSha256))
                {
                    var actual = ComputeSha256(tempPath);
                    if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(tempPath);
                        throw new InvalidOperationException(
                            $"Downloaded file hash mismatch for {release.AssetName} — expected {expectedSha256}, got {actual}. Discarded.");
                    }
                    DebugWindow.Log($"[DlssNrLinuxWrapper] SHA256 verified for {release.AssetName}");
                }
                else
                {
                    DebugWindow.Log($"[DlssNrLinuxWrapper] No published checksum for {release.AssetName} — skipped verification.");
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
            using var sha256 = System.Security.Cryptography.SHA256.Create();
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

        // ── Extraction ──────────────────────────────────────────────────────────────────

        /// <summary>Extracts the cached tar.gz into "&lt;gameDir&gt;/dlssnr-linux-portable/" (the same
        /// top-level folder name the archive itself uses). Idempotent — re-extracting (e.g. on
        /// update, or a retried install) replaces whatever was there.</summary>
        public string ExtractToGameDir(string version, string gameDir)
        {
            var tarPath = GetCachedTarPath(version);
            if (!File.Exists(tarPath))
                throw new FileNotFoundException("The fork's tar.gz is not cached for this version. Download it first.", tarPath);

            var destDir = Path.Combine(gameDir, ExtractedFolderName);
            if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
            Directory.CreateDirectory(destDir);

            using var fileStream = File.OpenRead(tarPath);
            using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzipStream, destDir, overwriteFiles: true);

            DebugWindow.Log($"[DlssNrLinuxWrapper] Extracted {tarPath} -> {destDir}");
            // The tarball's own top-level entries are already named "dlssnr-linux-portable/..." (see
            // this project's context/dlssnr-on-amd-linux-setup.md), so extracting it INTO that same
            // folder name nests it one level deeper than intended — flatten it back out.
            var nested = Path.Combine(destDir, ExtractedFolderName);
            if (Directory.Exists(nested))
            {
                foreach (var entry in Directory.GetFileSystemEntries(nested))
                {
                    var name = Path.GetFileName(entry);
                    var target = Path.Combine(destDir, name);
                    if (Directory.Exists(entry)) Directory.Move(entry, target);
                    else File.Move(entry, target, overwrite: true);
                }
                Directory.Delete(nested, recursive: true);
            }

            return destDir;
        }

        // ── Proton/Wine runner discovery ────────────────────────────────────────────────

        /// <summary>Runs `installer.py list-protons --json` to enumerate installed Wine/Proton
        /// runners. Read-only, installs nothing. Returns an empty list on any failure (caller falls
        /// back to asking the user for a folder directly).</summary>
        public async Task<List<LinuxWrapperRunner>> ListRunnersAsync(string extractedDir)
        {
            var result = new List<LinuxWrapperRunner>();
            try
            {
                var (exitCode, stdout, stderr) = await RunPythonAsync(extractedDir,
                    new[] { "installer.py", "list-protons", "--json" }, TimeSpan.FromSeconds(30));
                if (exitCode != 0)
                {
                    DebugWindow.Log($"[DlssNrLinuxWrapper] list-protons failed (exit {exitCode}): {stderr}");
                    return result;
                }

                using var doc = JsonDocument.Parse(stdout);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
                foreach (var row in doc.RootElement.EnumerateArray())
                {
                    var path = row.TryGetProperty("path", out var p) ? p.GetString() : null;
                    if (string.IsNullOrEmpty(path)) continue;
                    var compatible = row.TryGetProperty("compatible", out var c) && c.GetBoolean();
                    var reason = row.TryGetProperty("reason", out var r) ? r.GetString() : null;
                    result.Add(new LinuxWrapperRunner(path, compatible, reason));
                }
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[DlssNrLinuxWrapper] list-protons error: {ex.Message}");
            }
            return result;
        }

        // ── Automatic runner choice (never asks) ──────────────────────────────────────────

        /// <summary>Picks the Wine/Proton runner for <paramref name="steamAppId"/> without asking:
        /// the one Steam is set to use for that game, else Steam's global default, else (on CachyOS)
        /// a Proton-CachyOS build, else the first compatible one. Null when none is compatible.</summary>
        public static LinuxWrapperRunner? PickDefaultRunner(IReadOnlyList<LinuxWrapperRunner> runners, string? steamAppId)
        {
            var (perGame, global) = ReadSteamCompatToolNames(ReadSteamConfigVdf(), steamAppId);
            return PickDefaultRunner(runners, perGame, global, IsCachyOs());
        }

        internal static LinuxWrapperRunner? PickDefaultRunner(IReadOnlyList<LinuxWrapperRunner> runners,
            string? perGameToolName, string? globalToolName, bool isCachyOs)
        {
            var compatible = runners.Where(r => r.Compatible).ToList();
            if (compatible.Count == 0) return null;

            foreach (var toolName in new[] { perGameToolName, globalToolName })
            {
                if (string.IsNullOrEmpty(toolName)) continue;
                var match = compatible.FirstOrDefault(r => RunnerMatchesToolName(r.Path, toolName));
                if (match != null) return match;
            }

            if (isCachyOs)
            {
                var cachy = compatible
                    .Where(r => Path.GetFileName(r.Path.TrimEnd('/')).Contains("cachyos", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => Path.GetFileName(r.Path.TrimEnd('/')).Contains("latest", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
                if (cachy != null) return cachy;
            }

            return compatible[0];
        }

        /// <summary>Whether the runner folder is the compatibility tool Steam calls
        /// <paramref name="toolName"/>: the internal name in its compatibilitytool.vdf for custom
        /// tools (GE-Proton, Proton-CachyOS...), or Valve's naming for official Proton
        /// ("proton_experimental" → "Proton - Experimental", "proton_9" → "Proton 9.0").</summary>
        internal static bool RunnerMatchesToolName(string runnerPath, string toolName)
        {
            var folder = Path.GetFileName(runnerPath.TrimEnd('/'));
            if (string.Equals(folder, toolName, StringComparison.OrdinalIgnoreCase)) return true;

            try
            {
                var vdf = Path.Combine(runnerPath, "compatibilitytool.vdf");
                if (File.Exists(vdf))
                {
                    var m = Regex.Match(File.ReadAllText(vdf), "\"compat_tools\"\\s*\\{\\s*\"([^\"]+)\"");
                    if (m.Success) return string.Equals(m.Groups[1].Value, toolName, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            var official = Regex.Match(toolName, "^proton_(\\d+|experimental|hotfix)$", RegexOptions.IgnoreCase);
            if (!official.Success) return false;
            var suffix = official.Groups[1].Value;
            return char.IsDigit(suffix[0])
                ? folder.StartsWith($"Proton {suffix}", StringComparison.OrdinalIgnoreCase)
                : folder.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) && folder.Contains(suffix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Compat tool names from Steam's config.vdf CompatToolMapping: the one set for
        /// <paramref name="appId"/> (Properties → Compatibility) and the global default ("0").</summary>
        internal static (string? PerGame, string? Global) ReadSteamCompatToolNames(string? configVdf, string? appId)
        {
            if (string.IsNullOrEmpty(configVdf)) return (null, null);
            var start = configVdf.IndexOf("\"CompatToolMapping\"", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return (null, null);
            var open = configVdf.IndexOf('{', start);
            if (open < 0) return (null, null);
            int depth = 0, end = open;
            for (; end < configVdf.Length; end++)
            {
                if (configVdf[end] == '{') depth++;
                else if (configVdf[end] == '}' && --depth == 0) break;
            }
            var section = configVdf.Substring(open, Math.Min(end, configVdf.Length - 1) - open + 1);

            string? NameFor(string id)
            {
                var m = Regex.Match(section, "\"" + Regex.Escape(id) + "\"\\s*\\{[^{}]*?\"name\"\\s*\"([^\"]*)\"");
                return m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : null;
            }

            return (string.IsNullOrEmpty(appId) ? null : NameFor(appId), NameFor("0"));
        }

        private static string? ReadSteamConfigVdf()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var root in new[] { ".local/share/Steam", ".steam/steam", ".var/app/com.valvesoftware.Steam/.local/share/Steam" })
            {
                var path = Path.Combine(home, root, "config", "config.vdf");
                try { if (File.Exists(path)) return File.ReadAllText(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return null;
        }

        private static bool IsCachyOs()
        {
            try
            {
                return File.Exists("/etc/os-release") &&
                    File.ReadAllLines("/etc/os-release").Any(l => l.Trim().Equals("ID=cachyos", StringComparison.OrdinalIgnoreCase) ||
                                                                 l.Trim().Equals("ID=\"cachyos\"", StringComparison.OrdinalIgnoreCase));
            }
            catch (IOException) { return false; }
        }

        // ── Headless auto-install ────────────────────────────────────────────────────────

        /// <summary>Runs the fork's installer fully non-interactively (`--json` forces
        /// interactive=False on its side, so it never blocks on stdin — unlike danielblnc's Windows
        /// installer, there are no prompts to answer here at all). Passes every confirmation flag up
        /// front (--confirm-runner, --accept-risk, --replace-existing, --install-rocm, --confirm-fsr)
        /// so the only thing this ever needs from the user beforehand is nvngx_dlssnr.dll (already
        /// handled by the existing cache-once flow, see DlssNrOnAmdService.CachedNvngxDlssNrPath) and,
        /// if ambiguous, a Wine/Proton runner (see ListRunnersAsync). --confirm-fsr: the fork refuses
        /// to continue headlessly when it can't find FSR in the game's file/import names, which is
        /// exactly the case for games whose FSR comes from OptiScaler or is built into the exe — the
        /// user already chose this game for Setup NR. Falls back to --game-dir instead of a
        /// specific --exe only when <paramref name="exePath"/> is unknown (see TargetArgs) — callers
        /// pass GameInstallationService.DetermineMainExecutable, since the fork's own guess refuses
        /// folders with several executables.
        ///
        /// Model input: see <see cref="ChooseModelInput"/>. At least one of
        /// <paramref name="weightsPath"/> / <paramref name="nvidiaDllPath"/> must be non-null.
        ///
        /// On success, CommandPrefix/LaunchOptions come straight from the fork's own JSON result —
        /// nothing is guessed on this side. On failure, RawError is the exact "Error: ..." line it
        /// printed to stderr (exit code 2) — already a specific, actionable message (anti-cheat
        /// detected, game still running, unsupported GPU, missing gcc/g++, unrecognized DLL, etc.), so
        /// it's surfaced to the user as-is rather than re-worded.</summary>
        public async Task<LinuxWrapperInstallResult> RunAutoInstallAsync(string extractedDir, string gameDir, string? weightsPath, string? nvidiaDllPath, string runnerPath, string? exePath = null)
        {
            var (flag, modelPath) = ChooseModelInput(weightsPath, nvidiaDllPath);

            var args = new List<string> { "installer.py", "install" };
            args.AddRange(TargetArgs(gameDir, exePath));
            args.AddRange(new[] { flag, modelPath });
            args.AddRange(new[]
            {
                "--runner", runnerPath,
                "--confirm-runner",
                "--install-rocm",
                "--replace-existing",
                "--accept-risk",
                "--confirm-fsr",
                "--json",
            });

            // Generous timeout — a first-ever run downloads ~3 GiB of ROCm before it can install
            // anything; every later run on any other game reuses that cache and finishes in seconds
            // (both observed directly while validating this flow manually).
            var (exitCode, stdout, stderr) = await RunPythonAsync(extractedDir, args, TimeSpan.FromMinutes(20));

            if (exitCode != 0)
            {
                var rawError = stderr.Split('\n').FirstOrDefault(l => l.StartsWith("Error:", StringComparison.Ordinal))?.Trim()
                    ?? (string.IsNullOrWhiteSpace(stderr) ? $"Installer exited with code {exitCode}." : stderr.Trim());
                DebugWindow.Log($"[DlssNrLinuxWrapper] install failed (exit {exitCode}): {rawError}");
                return new LinuxWrapperInstallResult(false, null, null, null, new List<string>(), rawError);
            }

            try
            {
                using var doc = JsonDocument.Parse(stdout);
                var root = doc.RootElement;
                string? Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var gpuName = root.TryGetProperty("gpu", out var gpu) && gpu.TryGetProperty("name", out var n) ? n.GetString() : null;
                var warnings = new List<string>();
                if (root.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array)
                    foreach (var item in w.EnumerateArray())
                        if (item.GetString() is string s) warnings.Add(s);

                DebugWindow.Log("[DlssNrLinuxWrapper] install succeeded.");
                return new LinuxWrapperInstallResult(true, Get("command_prefix"), Get("launch_options"), gpuName, warnings, null);
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[DlssNrLinuxWrapper] Could not parse install JSON output: {ex.Message}\n{stdout}");
                return new LinuxWrapperInstallResult(false, null, null, null, new List<string>(), "Installer finished but its output could not be parsed.");
            }
        }

        /// <summary>The fork's target: --exe when the game's executable is known (the fork's own
        /// guess gives up on folders with several executables, e.g. Cyberpunk's bin\x64 with its
        /// crash reporters), --game-dir otherwise.</summary>
        internal static string[] TargetArgs(string gameDir, string? exePath) =>
            !string.IsNullOrEmpty(exePath) && File.Exists(exePath)
                ? new[] { "--exe", exePath }
                : new[] { "--game-dir", gameDir };

        // The only files the fork's own uninstall accepts in .dlssnr-linux/logs — it refuses to run
        // ("Unknown file in deployment store") when anything else is there.
        private static readonly HashSet<string> ForkOwnedLogs = new(StringComparer.Ordinal) { "hip.log", "vkd3d.log" };

        /// <summary>Deletes the reports bulacha3's optimized lmxxf backend writes at game runtime into
        /// .dlssnr-linux/logs (resultado-etapa4.txt, kernel-profile.txt) — the fork's uninstall only
        /// knows hip.log/vkd3d.log there and refuses to remove anything while other files exist.
        /// Only plain files directly inside that logs folder are touched.</summary>
        internal static void RemoveRuntimeReports(string gameDir)
        {
            var logs = Path.Combine(gameDir, ".dlssnr-linux", "logs");
            if (!Directory.Exists(logs)) return;
            try
            {
                foreach (var file in Directory.GetFiles(logs))
                {
                    if (ForkOwnedLogs.Contains(Path.GetFileName(file))) continue;
                    if (new FileInfo(file).LinkTarget != null) continue;
                    File.Delete(file);
                    DebugWindow.Log($"[DlssNrLinuxWrapper] Removed runtime report before uninstall: {file}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DebugWindow.Log($"[DlssNrLinuxWrapper] Could not clear runtime reports in '{logs}': {ex.Message}");
            }
        }

        /// <summary>The exe the fork recorded when it installed into <paramref name="gameDir"/>
        /// (.dlssnr-linux/manifest.json "exe") — what its uninstall must target.</summary>
        internal static string? InstalledExeFromManifest(string gameDir)
        {
            try
            {
                var manifest = Path.Combine(gameDir, ".dlssnr-linux", "manifest.json");
                if (!File.Exists(manifest)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                return doc.RootElement.TryGetProperty("exe", out var exe) && exe.ValueKind == JsonValueKind.String ? exe.GetString() : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>SHA-256 of the one nvngx_dlssnr.dll (310.8.0.0) the fork's converters accept
        /// (conversion.KNOWN_NVIDIA_SHA upstream) — any other copy is rejected outright.</summary>
        internal const string KnownNvidiaDllSha256 = "e16bcf15e16e13f527491cdf7845b2fe6521a738d8f7c9c721866a8496e1fc8e";

        /// <summary>Picks the installer's model input. The raw DLL (--nvidia-dll) when it is the exact
        /// copy the fork accepts: its optimized lmxxf backend (RDNA4 / gfx1201) can only prepare its
        /// own model data from that DLL and fails the whole install without it, even when converted
        /// weights are supplied. Otherwise the already-converted dlssnr_on_amd_weights.bin
        /// (--weights, only structurally validated) — it still installs everywhere the original
        /// backend is used, whereas the fork rejects any other distribution of the same DLL version
        /// outright. Falls back to the DLL when no weights are cached, letting the fork report the
        /// mismatch itself.</summary>
        internal static (string Flag, string Path) ChooseModelInput(string? weightsPath, string? nvidiaDllPath)
        {
            var dllUsable = !string.IsNullOrEmpty(nvidiaDllPath) && File.Exists(nvidiaDllPath);
            if (dllUsable && string.Equals(ComputeSha256(nvidiaDllPath!), KnownNvidiaDllSha256, StringComparison.OrdinalIgnoreCase))
                return ("--nvidia-dll", nvidiaDllPath!);
            if (!string.IsNullOrEmpty(weightsPath))
                return ("--weights", weightsPath);
            if (!string.IsNullOrEmpty(nvidiaDllPath))
                return ("--nvidia-dll", nvidiaDllPath);
            throw new ArgumentException("Either weightsPath or nvidiaDllPath must be provided.");
        }

        /// <summary>Runs the fork's own `installer.py uninstall --yes --json` against this game
        /// directory — the fork keeps a transactional journal of everything it touched (DLLs it
        /// backed up or created, the INI, the weights file, its whole .dlssnr-linux state folder) in
        /// that same folder, so this is the only reliable way to reverse an install: this app has no
        /// visibility into what the fork actually did, unlike DlssNrOnAmdService's own manifest for
        /// danielblnc's Windows installer (which this app drives by hand and can snapshot itself).
        /// Never removes nvngx_dlssnr.dll (the fork never tracks it — it only ever reads it from
        /// wherever --nvidia-dll/--weights pointed, never copies it into the game folder) or this
        /// app's own extracted "dlssnr-linux-portable" installer folder — the caller removes that
        /// itself once this returns, since the fork has no reason to know about it.</summary>
        public async Task<(bool Success, string? RawError)> RunUninstallAsync(string extractedDir, string gameDir)
        {
            RemoveRuntimeReports(gameDir);
            var args = new List<string> { "installer.py", "uninstall" };
            args.AddRange(TargetArgs(gameDir, InstalledExeFromManifest(gameDir)));
            args.AddRange(new[] { "--yes", "--json" });
            var (exitCode, _, stderr) = await RunPythonAsync(extractedDir, args, TimeSpan.FromMinutes(2));
            if (exitCode == 0)
            {
                DebugWindow.Log("[DlssNrLinuxWrapper] uninstall succeeded.");
                return (true, null);
            }

            var rawError = stderr.Split('\n').FirstOrDefault(l => l.StartsWith("Error:", StringComparison.Ordinal))?.Trim()
                ?? (string.IsNullOrWhiteSpace(stderr) ? $"Uninstaller exited with code {exitCode}." : stderr.Trim());
            DebugWindow.Log($"[DlssNrLinuxWrapper] uninstall failed (exit {exitCode}): {rawError}");
            return (false, rawError);
        }

        // ── Host requirements (Python 3.11+, gcc/g++) ────────────────────────────────────

        // Tried in order: a distro whose default python3 is older (e.g. Ubuntu 22.04) can still have a
        // newer interpreter installed side by side.
        private static readonly string[] PythonCandidates = { "python3", "python3.14", "python3.13", "python3.12", "python3.11" };
        private static string? _resolvedPython;

        /// <summary>First interpreter on PATH that is Python 3.11 or newer (what the fork's
        /// installer.py requires), or null when there is none. Cached once found.</summary>
        public static async Task<string?> ResolvePythonAsync()
        {
            if (_resolvedPython != null) return _resolvedPython;
            foreach (var candidate in PythonCandidates)
            {
                try
                {
                    using var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = candidate,
                        ArgumentList = { "-c", "import sys; sys.exit(0 if sys.version_info >= (3, 11) else 1)" },
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    });
                    if (proc == null) continue;
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await proc.WaitForExitAsync(cts.Token);
                    if (proc.ExitCode == 0)
                    {
                        DebugWindow.Log($"[DlssNrLinuxWrapper] Using {candidate} for the fork's installer.");
                        return _resolvedPython = candidate;
                    }
                }
                catch (Exception) { /* not installed / not runnable — try the next one */ }
            }
            DebugWindow.Log("[DlssNrLinuxWrapper] No Python 3.11+ interpreter found on PATH.");
            return null;
        }

        /// <summary>Recognises the fork's own messages for a missing requirement in its error output
        /// (installer.py's Python check, lmxxf.preflight's gcc/g++ and Python checks).</summary>
        public static LinuxForkRequirement? DetectMissingRequirement(string? rawError)
        {
            if (string.IsNullOrEmpty(rawError)) return null;
            if (rawError.Contains("gcc and g++", StringComparison.OrdinalIgnoreCase)) return LinuxForkRequirement.BuildTools;
            if (rawError.Contains("Python 3.11 or newer", StringComparison.OrdinalIgnoreCase)) return LinuxForkRequirement.Python311;
            return null;
        }

        /// <summary>Terminal command that installs <paramref name="requirement"/> on this machine's
        /// distribution (from /etc/os-release), or null when it can't be given safely (unknown
        /// distribution, SteamOS's read-only system).</summary>
        public static string? GetInstallCommand(LinuxForkRequirement requirement)
        {
            string osRelease = "";
            try { if (File.Exists("/etc/os-release")) osRelease = File.ReadAllText("/etc/os-release"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return GetInstallCommand(requirement, osRelease, File.Exists("/run/ostree-booted"));
        }

        internal static string? GetInstallCommand(LinuxForkRequirement requirement, string osRelease, bool isOstree)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in osRelease.Split('\n'))
            {
                var line = raw.Trim();
                foreach (var key in new[] { "ID=", "ID_LIKE=" })
                    if (line.StartsWith(key, StringComparison.Ordinal))
                        foreach (var id in line.Substring(key.Length).Trim('"', '\'').Split(' ', StringSplitOptions.RemoveEmptyEntries))
                            ids.Add(id);
            }

            var buildTools = requirement == LinuxForkRequirement.BuildTools;
            if (ids.Contains("steamos")) return null;
            if (isOstree) return buildTools ? "rpm-ostree install gcc gcc-c++" : "rpm-ostree install python3";
            if (ids.Contains("arch")) return buildTools ? "sudo pacman -S --needed gcc" : "sudo pacman -S --needed python";
            if (ids.Contains("debian") || ids.Contains("ubuntu")) return buildTools ? "sudo apt install build-essential" : "sudo apt install python3.11";
            if (ids.Contains("fedora") || ids.Contains("rhel") || ids.Contains("centos")) return buildTools ? "sudo dnf install gcc gcc-c++" : "sudo dnf install python3";
            if (ids.Contains("suse") || ids.Contains("opensuse")) return buildTools ? "sudo zypper install gcc gcc-c++" : "sudo zypper install python311";
            return null;
        }

        /// <summary>Runs `python3 &lt;extractedDir&gt;/&lt;args[0]&gt;` (installer.py) with the given
        /// arguments, capturing stdout/stderr fully. the fork's tool never needs stdin — with --json it
        /// never prompts (see the "Error: ..." line handling in RunAutoInstallAsync) — so this is a
        /// plain run-to-completion, unlike DlssNrOnAmdService's character-buffered prompt-driving for
        /// danielblnc's interactive Windows installer.</summary>
        private static async Task<(int ExitCode, string Stdout, string Stderr)> RunPythonAsync(string workingDir, IReadOnlyList<string> pythonArgs, TimeSpan timeout)
        {
            var python = await ResolvePythonAsync() ?? "python3";
            var psi = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in pythonArgs) psi.ArgumentList.Add(a);

            using var proc = new Process { StartInfo = psi };
            try
            {
                proc.Start();
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[DlssNrLinuxWrapper] Could not start {python}: {ex.Message}");
                return (-1, "", $"Could not start {python} — is it installed and on PATH? ({ex.Message})");
            }

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return (-1, await stdoutTask, "Timed out waiting for the installer.");
            }

            return (proc.ExitCode, await stdoutTask, await stderrTask);
        }
    }
}
