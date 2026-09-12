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
using System.Threading;
using System.Threading.Tasks;
using OptiscalerClient.Models;
using OptiscalerClient.Views;
using static OptiscalerClient.Helpers.HttpRetryHelper;

namespace OptiscalerClient.Services
{
    /// <summary>Outcome of <see cref="DlssNrLinuxWrapperService.RunAutoInstallAsync"/>. On failure,
    /// RawError is guentra's own "Error: ..." message from stderr (exit code 2) — surfaced to the
    /// user as-is since it's already a clear, specific diagnostic (anti-cheat detected, game still
    /// running, unsupported GPU, etc.), not something worth re-wording.</summary>
    public sealed record LinuxWrapperInstallResult(bool Success, string? CommandPrefix, string? LaunchOptions, string? GpuName, List<string> Warnings, string? RawError);

    /// <summary>One Wine/Proton runner candidate reported by `list-protons --json`.</summary>
    public sealed record LinuxWrapperRunner(string Path, bool Compatible, string? Reason);

    /// <summary>
    /// Lists, downloads, extracts and runs guentra/DLSS-NR-on-AMD-Linux — an unofficial,
    /// experimental third-party fork that lets danielblnc's DLSS-NR-on-AMD Neural Rendering mod
    /// actually run on Linux/Proton. Windows HIP calls from the mod's own DLL can never see a real
    /// GPU architecture through Wine (Wine only ever exposes a cosmetic wined3d GPU-description
    /// table to D3DKMT, not a real compute driver — see context/dlssnr-on-amd-linux-setup.md for the
    /// full investigation this conclusion is based on); guentra's fork instead bridges those HIP
    /// calls to a real native ROCm runtime via LD_PRELOAD, which is the only way this mod has been
    /// confirmed to work at all under Proton. This is the Linux counterpart to DlssNrOnAmdService
    /// (same repo-release/cache-once shape) — not affiliated with guentra or danielblnc, credited in
    /// the UI wherever this is offered, and never used on Windows (danielblnc's own installer is used
    /// there directly, see DlssNrOnAmdService).
    /// </summary>
    public class DlssNrLinuxWrapperService
    {
        private const string RepoOwner = "guentra";
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
            _releasesCacheFile = Path.Combine(baseDir, "dlssnr_linux_wrapper_releases_cache.json");

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

        // ── Headless auto-install ────────────────────────────────────────────────────────

        /// <summary>Runs guentra's installer fully non-interactively (`--json` forces
        /// interactive=False on its side, so it never blocks on stdin — unlike danielblnc's Windows
        /// installer, there are no prompts to answer here at all). Passes every confirmation flag up
        /// front (--confirm-runner, --accept-risk, --replace-existing, --install-rocm) so the only
        /// thing this ever needs from the user beforehand is nvngx_dlssnr.dll (already handled by the
        /// existing cache-once flow, see DlssNrOnAmdService.CachedNvngxDlssNrPath) and, if ambiguous,
        /// a Wine/Proton runner (see ListRunnersAsync). Deliberately passes --game-dir rather than a
        /// specific --exe: Game.ExecutablePath is only ever populated by the Lutris/generic scanners
        /// (Steam-scanned games leave it blank), so it isn't a reliable source of the actual exe here —
        /// guentra's own select_executable already does real PE-header parsing (x64 check, "-Shipping"
        /// preference, excludes crash-reporter/setup/launcher stubs) to find it from the directory
        /// alone, which is the same directory this app's own GameInstallationService.
        /// DetermineInstallDirectory resolves for every other Setup NR path.
        ///
        /// <paramref name="weightsPath"/> (already-converted dlssnr_on_amd_weights.bin — see
        /// DlssNrOnAmdService.CachedModeBWeightsPath) is preferred over <paramref name="nvidiaDllPath"/>
        /// whenever available: guentra's own weight conversion (triggered by --nvidia-dll) hash-checks
        /// the supplied DLL against one specific known-good "310.8.0.0" release and rejects any other
        /// legitimate distribution of that same version outright (confirmed directly — a real,
        /// correctly-versioned nvngx_dlssnr.dll from a different source than guentra's own reference
        /// copy was rejected with "Unrecognized NVIDIA DLL"), whereas already-converted weights skip
        /// that check entirely (only structurally validated). Exactly one of the two must be non-null.
        ///
        /// On success, CommandPrefix/LaunchOptions come straight from guentra's own JSON result —
        /// nothing is guessed on this side. On failure, RawError is the exact "Error: ..." line guentra
        /// printed to stderr (exit code 2) — already a specific, actionable message (anti-cheat
        /// detected, game still running, unsupported GPU, ambiguous executable, unrecognized DLL,
        /// etc.), so it's surfaced to the user as-is rather than re-worded.</summary>
        public async Task<LinuxWrapperInstallResult> RunAutoInstallAsync(string extractedDir, string gameDir, string? weightsPath, string? nvidiaDllPath, string runnerPath)
        {
            if (string.IsNullOrEmpty(weightsPath) == string.IsNullOrEmpty(nvidiaDllPath))
                throw new ArgumentException("Exactly one of weightsPath or nvidiaDllPath must be provided.");

            var args = new List<string>
            {
                "installer.py", "install",
                "--game-dir", gameDir,
            };
            if (!string.IsNullOrEmpty(weightsPath))
                args.AddRange(new[] { "--weights", weightsPath });
            else
                args.AddRange(new[] { "--nvidia-dll", nvidiaDllPath! });
            args.AddRange(new[]
            {
                "--runner", runnerPath,
                "--confirm-runner",
                "--install-rocm",
                "--replace-existing",
                "--accept-risk",
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

        /// <summary>Runs guentra's own `installer.py uninstall --yes --json` against this game
        /// directory — the fork keeps a transactional journal of everything it touched (DLLs it
        /// backed up or created, the INI, the weights file, its whole .dlssnr-linux state folder) in
        /// that same folder, so this is the only reliable way to reverse an install: this app has no
        /// visibility into what guentra actually did, unlike DlssNrOnAmdService's own manifest for
        /// danielblnc's Windows installer (which this app drives by hand and can snapshot itself).
        /// Never removes nvngx_dlssnr.dll (guentra never tracks it — it only ever reads it from
        /// wherever --nvidia-dll/--weights pointed, never copies it into the game folder) or this
        /// app's own extracted "dlssnr-linux-portable" installer folder — the caller removes that
        /// itself once this returns, since guentra has no reason to know about it.</summary>
        public async Task<(bool Success, string? RawError)> RunUninstallAsync(string extractedDir, string gameDir)
        {
            var args = new[] { "installer.py", "uninstall", "--game-dir", gameDir, "--yes", "--json" };
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

        /// <summary>Runs `python3 &lt;extractedDir&gt;/&lt;args[0]&gt;` (installer.py) with the given
        /// arguments, capturing stdout/stderr fully. guentra's tool never needs stdin — with --json it
        /// never prompts (see the "Error: ..." line handling in RunAutoInstallAsync) — so this is a
        /// plain run-to-completion, unlike DlssNrOnAmdService's character-buffered prompt-driving for
        /// danielblnc's interactive Windows installer.</summary>
        private static async Task<(int ExitCode, string Stdout, string Stderr)> RunPythonAsync(string workingDir, IReadOnlyList<string> pythonArgs, TimeSpan timeout)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "python3",
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
                DebugWindow.Log($"[DlssNrLinuxWrapper] Could not start python3: {ex.Message}");
                return (-1, "", $"Could not start python3 — is it installed and on PATH? ({ex.Message})");
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
