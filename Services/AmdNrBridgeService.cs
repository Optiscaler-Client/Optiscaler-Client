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
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OptiscalerClient.Helpers;
using OptiscalerClient.Models;
using OptiscalerClient.Views;
using static OptiscalerClient.Helpers.HttpRetryHelper;

namespace OptiscalerClient.Services
{
    /// <summary>
    /// Lists, downloads and applies GoldenNights/AMD-NR-bridge — an ASI plugin (loaded by
    /// OptiScaler from its plugins folder) plus a set of OptiScaler.ini values, which makes
    /// danielblnc's DLSS-NR-on-AMD work alongside an official OptiScaler build (stable/beta/nightly).
    /// Replaces the discontinued MatheusGViana/dlss-5-amd-project wrapper build in Setup NR's
    /// "Mod + OptiScaler" mode. The bridge's own PowerShell installer is not run — its steps are
    /// replicated here (see ApplyAsync), which also keeps them usable on Linux/Proton.
    ///
    /// Its licence forbids redistributing or bundling it, so it is never shipped with this app — it
    /// is downloaded from its own GitHub releases at install time (same model as danielblnc's setup
    /// exe, see DlssNrOnAmdService) and its plugin is copied unmodified.
    /// </summary>
    public class AmdNrBridgeService
    {
        private const string RepoOwner = "GoldenNights";
        private const string RepoName = "AMD-NR-bridge";
        // Releases before 0.4.0 were published as "DLSSNR-OPTI-bridge-AMD-*.zip" with a different
        // plugin name and settings — deliberately not listed, only this layout is supported.
        private const string AssetPrefix = "AMD-NR-bridge-";
        public const string BridgeAsiFileName = "AMD-NR-bridge.asi";

        // Same rationale as DlssNrOnAmdService.ReleasesCacheTtl.
        private static readonly TimeSpan ReleasesCacheTtl = TimeSpan.FromHours(12);

        private readonly string _cacheDir;
        private readonly string _releasesCacheFile;
        private static HttpClient HttpClient => NetworkService.GetHttpClient();

        private static List<DlssNrOnAmdRelease>? _cachedReleases;
        private static DateTime _cachedReleasesLastUpdated = DateTime.MinValue;
        private static bool _releasesLoadedFromDisk;
        private static readonly object _releasesCacheLock = new();

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string>> _inFlightDownloads = new(StringComparer.OrdinalIgnoreCase);

        public AmdNrBridgeService()
        {
            var baseDir = AppPaths.GetAppDataRoot();
            _cacheDir = Path.Combine(baseDir, "Cache", "AmdNrBridge");
            _releasesCacheFile = Path.Combine(baseDir, "amd_nr_bridge_releases_cache.json");

            lock (_releasesCacheLock)
            {
                if (_releasesLoadedFromDisk) return;
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
                        }
                    }
                }
                catch (Exception ex) { DebugWindow.Log($"[AmdNrBridge] Failed to load releases cache: {ex.Message}"); }
            }
        }

        // ── Release listing ──────────────────────────────────────────────────────────

        public async Task<List<DlssNrOnAmdRelease>> GetReleasesAsync(bool forceRefresh = false)
        {
            if (!forceRefresh && _cachedReleases != null && DateTime.UtcNow - _cachedReleasesLastUpdated < ReleasesCacheTtl)
                return _cachedReleases;

            try
            {
                var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=30";
                var response = await GetWithRetryAsync(() => HttpClient, url);
                DebugWindow.Log($"[AmdNrBridge] GET {url} -> HTTP {(int)response.StatusCode}");
                response.EnsureSuccessStatusCode();

                var releases = ParseReleases(await response.Content.ReadAsStringAsync());
                DebugWindow.Log($"[AmdNrBridge] {RepoOwner}/{RepoName} -> {releases.Count} usable release(s)");
                _cachedReleases = releases;
                if (releases.Count > 0)
                {
                    try
                    {
                        _cachedReleasesLastUpdated = DateTime.UtcNow;
                        var cache = new DlssNrOnAmdReleasesCache { LastUpdated = _cachedReleasesLastUpdated, Releases = releases };
                        File.WriteAllText(_releasesCacheFile, JsonSerializer.Serialize(cache, OptimizerContext.Default.DlssNrOnAmdReleasesCache));
                    }
                    catch (Exception ex) { DebugWindow.Log($"[AmdNrBridge] Failed to save releases cache: {ex.Message}"); }
                }
                return releases;
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[AmdNrBridge] Release listing failed: {ex.Message}");
                return _cachedReleases ?? new List<DlssNrOnAmdRelease>();
            }
        }

        /// <summary>Parses a GitHub "list releases" response into usable releases, newest-first as
        /// GitHub returns them. Drafts and releases without an "AMD-NR-bridge-*.zip" asset are
        /// skipped. The SHA-256 comes from the release notes — the bridge publishes no checksums
        /// file.</summary>
        internal static List<DlssNrOnAmdRelease> ParseReleases(string json)
        {
            var releases = new List<DlssNrOnAmdRelease>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return releases;

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
                if (!element.TryGetProperty("tag_name", out var tagProp)) continue;
                var version = tagProp.GetString();
                if (string.IsNullOrEmpty(version)) continue;
                if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                    version = version.Substring(1);

                if (!element.TryGetProperty("assets", out var assets)) continue;
                string? zipUrl = null, zipName = null;
                foreach (var asset in assets.EnumerateArray())
                {
                    if (!asset.TryGetProperty("name", out var nameProp) ||
                        !asset.TryGetProperty("browser_download_url", out var urlProp))
                        continue;
                    var name = nameProp.GetString() ?? "";
                    if (name.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        zipUrl = urlProp.GetString();
                        zipName = name;
                        break;
                    }
                }
                if (zipUrl == null || zipName == null) continue;

                var body = element.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() : null;
                releases.Add(new DlssNrOnAmdRelease(version, zipUrl, zipName, ExtractSha256FromBody(body, zipName)));
            }
            return releases;
        }

        /// <summary>Finds the SHA-256 published for <paramref name="assetName"/> in a release's notes
        /// ("SHA-256 of AMD-NR-bridge-0.4.0.zip: `1F32…`") — the first 64-hex token after the asset
        /// name. Null when the notes don't mention that asset, rather than guessing at an unrelated
        /// hash.</summary>
        internal static string? ExtractSha256FromBody(string? body, string assetName)
        {
            if (string.IsNullOrEmpty(body)) return null;
            var idx = body.IndexOf(assetName, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            var match = Regex.Match(body.Substring(idx + assetName.Length), @"\b[0-9A-Fa-f]{64}\b");
            return match.Success ? match.Value.ToLowerInvariant() : null;
        }

        // ── Download + cache ─────────────────────────────────────────────────────────

        public string CacheRootPath => _cacheDir;
        public string GetCachePath(string version) => Path.Combine(_cacheDir, SanitizeVersionName(version));
        public string GetCachedAsiPath(string version) => Path.Combine(GetCachePath(version), BridgeAsiFileName);
        public bool IsCached(string version) => File.Exists(GetCachedAsiPath(version));
        public bool IsDownloading(string version) => _inFlightDownloads.ContainsKey(version);

        public void DeleteCache(string version)
        {
            var dir = GetCachePath(version);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }

        public List<string> GetDownloadedVersions()
        {
            if (!Directory.Exists(_cacheDir)) return new List<string>();
            return Directory.GetDirectories(_cacheDir)
                .Where(d => File.Exists(Path.Combine(d, BridgeAsiFileName)))
                .Select(Path.GetFileName)
                .Where(n => n != null)
                .Select(n => n!)
                .ToList();
        }

        /// <summary>Downloads and extracts the given release if not already cached, verifying it
        /// against the SHA-256 in its release notes when one is published (a mismatch throws and
        /// nothing is kept). Returns the path of the cached plugin. Concurrent calls for the
        /// same version share one download.</summary>
        public Task<string> DownloadAsync(string version, IProgress<double>? progress = null)
        {
            if (IsCached(version))
                return Task.FromResult(GetCachedAsiPath(version));

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
                ?? throw new VersionUnavailableException(version, "Release not found or has no AMD-NR-bridge .zip asset.");

            var tempZip = Path.Combine(Path.GetTempPath(), $"AmdNrBridge_{Guid.NewGuid():N}.zip");
            var destDir = GetCachePath(version);
            try
            {
                DebugWindow.Log($"[AmdNrBridge] Downloading {release.DownloadUrl}");
                await StreamToFileAsync(release.DownloadUrl, tempZip, progress);

                if (!string.IsNullOrEmpty(release.Sha256))
                {
                    var actual = ComputeSha256(tempZip);
                    if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"Downloaded file hash mismatch for {release.AssetName} — expected {release.Sha256}, got {actual}. Discarded.");
                    DebugWindow.Log($"[AmdNrBridge] SHA256 verified for {release.AssetName}");
                }
                else
                {
                    DebugWindow.Log($"[AmdNrBridge] No published checksum for {release.AssetName} — skipped verification.");
                }

                if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
                await Task.Run(() => ExtractBridgeZip(tempZip, destDir));

                var asiPath = GetCachedAsiPath(version);
                if (!File.Exists(asiPath))
                    throw new InvalidOperationException(
                        $"{release.AssetName} does not contain {BridgeAsiFileName} — unsupported release layout.");
                return asiPath;
            }
            catch
            {
                try { if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true); } catch { /* best effort */ }
                throw;
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { /* best effort */ }
            }
        }

        /// <summary>Extracts the release zip flat into <paramref name="destDir"/>, dropping its single
        /// top-level folder ("AMD-NR-bridge\"). The zip is written with backslash separators, which
        /// ZipFile.ExtractToDirectory on Linux would turn into literal file names — so entries are
        /// normalized by hand, with a path-traversal guard.</summary>
        internal static void ExtractBridgeZip(string zipPath, string destDir)
        {
            Directory.CreateDirectory(destDir);
            var root = Path.GetFullPath(destDir) + Path.DirectorySeparatorChar;

            using var archive = ZipFile.OpenRead(zipPath);
            var entries = archive.Entries
                .Select(e => (Entry: e, Key: e.FullName.Replace('\\', '/').TrimStart('/')))
                .Where(e => e.Key.Length > 0 && !e.Key.EndsWith('/'))
                .ToList();

            var firstSegments = entries.Select(e => e.Key.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var stripPrefix = firstSegments.Count == 1 && entries.All(e => e.Key.Contains('/')) ? firstSegments[0] + "/" : "";

            foreach (var (entry, key) in entries)
            {
                var relative = key.Substring(stripPrefix.Length).Replace('/', Path.DirectorySeparatorChar);
                var destPath = Path.GetFullPath(Path.Combine(destDir, relative));
                if (!destPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                entry.ExtractToFile(destPath, overwrite: true);
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
            return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
        }

        private static string SanitizeVersionName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(invalid.Contains(c) ? '_' : c);
            return sb.ToString();
        }

        // ── Game-level helpers (used by Manage / Quick Install / Bulk Install) ────────

        /// <summary>Setup NR mode that installs the bridge (danielblnc's mod + official OptiScaler).</summary>
        public const string BridgeMode = "daniel-and-opti";

        /// <summary><paramref name="preferred"/> (a per-window pick or the version pinned in
        /// Settings) when it is still listed, otherwise the latest release. Offline (no release list)
        /// it falls back to an already-downloaded version. Null when none is available.</summary>
        public async Task<string?> ResolveVersionAsync(string? preferred)
        {
            var releases = await GetReleasesAsync();
            if (releases.Count == 0)
            {
                if (!string.IsNullOrEmpty(preferred) && IsCached(preferred)) return preferred;
                return GetDownloadedVersions().OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            }
            if (!string.IsNullOrEmpty(preferred) &&
                releases.Any(r => string.Equals(r.Version, preferred, StringComparison.OrdinalIgnoreCase)))
                return preferred;
            return releases[0].Version;
        }

        /// <summary>True for a "Mod + OptiScaler" game installed through the bridge — as opposed to
        /// a legacy install made with the discontinued MatheusGViana wrapper build, which has no
        /// bridge version recorded and is left exactly as it is.</summary>
        public static bool UsesBridge(Game game) =>
            game.InstalledDlssNrOnAmdMode == BridgeMode && !string.IsNullOrEmpty(game.AmdNrBridgeVersion);

        /// <summary>(Re-)applies <paramref name="game"/>'s bridge version in <paramref name="gameDir"/>
        /// — after a fresh "Mod + OptiScaler" install and after anything that rewrites OptiScaler.ini
        /// (reinstall/update, profile, quality, output upscaler, frame generation), which could undo
        /// the values the bridge needs. No-op for games not using the bridge. Merges the reported
        /// changes into Game.AmdNrBridgeIniChanges (the latest original per key wins: it is what the
        /// app itself last wanted there) and records them as expected OptiScaler.ini values so the
        /// post-install verification doesn't offer to "reapply" them away. Throws on failure.</summary>
        public async Task EnsureAppliedAsync(Game game, string gameDir)
        {
            if (!UsesBridge(game)) return;
            var version = game.AmdNrBridgeVersion!;

            await DownloadAsync(version);
            var result = await ApplyAsync(gameDir, version);

            var merged = (game.AmdNrBridgeIniChanges ?? new List<AmdNrBridgeIniChange>())
                .ToDictionary(c => $"{c.Section}|{c.Key}", StringComparer.OrdinalIgnoreCase);
            foreach (var change in result.IniChanges)
                merged[$"{change.Section}|{change.Key}"] = change;
            game.AmdNrBridgeIniChanges = merged.Values.ToList();
            game.IsAmdNrBridgeInstalled = true;

            GameInstallationService.RecordExternalIniValues(gameDir,
                result.IniChanges.Select(c => (c.Section, c.Key, c.Value)));
        }

        /// <summary>Removes the bridge from <paramref name="gameDir"/> (plugin + OptiScaler.ini
        /// values) and clears <paramref name="game"/>'s bridge fields. Safe to call for games
        /// without the bridge. Must run before OptiScaler itself is uninstalled.</summary>
        public static void RemoveFromGame(Game game, string gameDir)
        {
            if (game.AmdNrBridgeVersion != null || game.IsAmdNrBridgeInstalled || IsInstalledIn(gameDir))
            {
                try { Remove(gameDir, game.AmdNrBridgeIniChanges); }
                catch (Exception ex) { DebugWindow.Log($"[AmdNrBridge] Remove failed for '{gameDir}': {ex.Message}"); }
            }
            game.AmdNrBridgeVersion = null;
            game.AmdNrBridgeIniChanges = null;
            game.IsAmdNrBridgeInstalled = false;
        }

        // ── Applying the bridge ──────────────────────────────────────────────────────
        //
        // Replicates what the bridge's own installer (AMD-NR-bridge-setup.ps1) does for an official
        // OptiScaler build, instead of running it: that script needs Windows PowerShell (no Linux /
        // Proton path at all), fails rather than asking for administrator rights when run with -Yes,
        // and keeps its own restore file outside this app's rollback/manifest. The .asi itself is
        // still the unmodified file from the bridge's own release. Deliberately not replicated:
        //   - resolving OptiScaler and DLSS-NR-on-AMD installed under the same proxy name — this app
        //     picks both names itself (DLSS-NR-on-AMD always as dbghelp.dll in Mode B);
        //   - the NR-fork shim (nvngx.dll_dlssnr.dll, ffx-probe marker, [Libraries] NvngxPath) —
        //     only official OptiScaler builds are installed with the bridge;
        //   - [Plugins] Path — the plugin goes where OptiScaler already loads plugins from without it
        //     (ResolveExtrasRoot\plugins, same as OptiPatcher), so no absolute Windows path has to be
        //     written, which under Proton would have to be a Wine path. An existing non-"auto" Path
        //     is respected instead.
        // When a new bridge release changes its script, diff it against this list before raising
        // the supported/default version.

        public sealed record ApplyResult(bool GameHasOwnFsr, string PluginPath, IReadOnlyList<AmdNrBridgeIniChange> IniChanges);

        /// <summary>A setting the bridge needs. <c>Also</c> lists values that already do the same —
        /// newer OptiScaler builds rename fsr31 to ffx when they save their ini.</summary>
        internal sealed record RequiredSetting(string Section, string Key, string Value, params string[] Also);

        /// <summary>Settings for a game that doesn't call FidelityFX itself (OptiScaler turns its
        /// DLSS/XeSS/FSR2 inputs into FSR, where DLSS-NR-on-AMD hooks in).</summary>
        internal static readonly RequiredSetting[] DefaultSettings =
        {
            new("Plugins", "LoadAsiPlugins", "true"),
            // Otherwise OptiScaler calls FSR around DLSS-NR's hook.
            new("Inputs", "EnableFfxInputs", "false"),
            new("Upscalers", "Dx12Upscaler", "fsr31", "ffx"),
            // DX11: FSR on OptiScaler's interop D3D12 device, the only place DLSS-NR can run there.
            new("Upscalers", "Dx11Upscaler", "fsr31_12", "ffx_12"),
            // OptiScaler's factory hooks race DLSS-NR's startup.
            new("Spoofing", "DxgiFactoryWrapping", "true"),
            // End opens DLSS-NR's overlay; OptiScaler's FG shortcut uses End too.
            new("Menu", "FGShortcutKey", "-1"),
        };

        /// <summary>Settings for a game with its own FSR 3.1 / FSR 4: OptiScaler has to hook the
        /// game's FidelityFX calls, and must not add its Unreal colour barrier to the corrected colour
        /// DLSS-NR hands on (the game's command list fails to close).</summary>
        internal static readonly RequiredSetting[] OwnFsrSettings =
        {
            new("Plugins", "LoadAsiPlugins", "true"),
            new("Inputs", "EnableFfxInputs", "true"),
            new("Upscalers", "Dx12Upscaler", "fsr31", "ffx"),
            new("Upscalers", "Dx11Upscaler", "fsr31_12", "ffx_12"),
            new("Spoofing", "DxgiFactoryWrapping", "true"),
            new("Hotfix", "ColorResourceBarrier", "64"),
            new("Menu", "FGShortcutKey", "-1"),
        };

        /// <summary>OptiScaler.ini values for running it alongside the mod on Linux (the Linux fork's
        /// "DLSS input → OptiScaler → FSR" route, no bridge plugin): FSR as the upscaler the mod hooks,
        /// OptiScaler not wrapping FSR calls around it, End left to the mod's menu. Taken from the
        /// fork's validated game profiles and confirmed on Cyberpunk 2077 + Proton-CachyOS.
        /// Frame generation and spoofing are left to the user's own selections.</summary>
        internal static readonly RequiredSetting[] LinuxModSettings =
        {
            new("Inputs", "EnableFfxInputs", "false"),
            new("Upscalers", "Dx12Upscaler", "fsr31", "ffx"),
            new("FSR", "UpscalerIndex", "0"),
            new("FSR", "Fsr4Update", "false"),
            new("Menu", "OverlayMenu", "true"),
            new("Menu", "FGShortcutKey", "-1"),
        };

        /// <summary>Applies <see cref="LinuxModSettings"/> to the OptiScaler.ini in
        /// <paramref name="gameDir"/> and records them as expected values (so the post-install
        /// verification doesn't offer to undo them). No-op without an OptiScaler.ini.</summary>
        public static void ApplyLinuxModSettings(string gameDir)
        {
            var iniPath = GameInstallationService.ResolveOptiScalerIniPath(gameDir);
            if (!File.Exists(iniPath)) return;
            var (text, changes) = ApplySettings(File.ReadAllText(iniPath), LinuxModSettings);
            if (changes.Count > 0)
            {
                File.WriteAllText(iniPath, text, new UTF8Encoding(false));
                DebugWindow.Log($"[AmdNrBridge] Linux NR settings in '{iniPath}': {string.Join(", ", changes.Select(c => $"[{c.Section}] {c.Key}={c.Value}"))}");
            }
            GameInstallationService.RecordExternalIniValues(gameDir, changes.Select(c => (c.Section, c.Key, c.Value)));
        }

        private static readonly string[] FidelityFxNames = { "ffxCreateContext", "amd_fidelityfx_dx12", "amd_fidelityfx_upscaler_dx12", "amd_fidelityfx_loader_dx12" };
        // DLSS-NR-on-AMD's setup exe names the FidelityFX API too and is often left in the game
        // folder — an exe carrying this marker never counts as "the game has its own FSR".
        private const string DlssNrMarker = "dlssnr_amd";
        private const string DlssNrIniFileName = "dlssnr_on_amd.ini";

        /// <summary>Sets up the cached <paramref name="version"/> of the bridge in
        /// <paramref name="gameDir"/>: copies the plugin and sets the OptiScaler.ini values it needs.
        /// OptiScaler and DLSS-NR-on-AMD must already be installed there. Idempotent — values that are
        /// already right are left alone and not reported, so callers can re-apply after anything
        /// rewrites OptiScaler.ini. Callers keep the returned changes (only from the first apply that
        /// reported them) to hand to <see cref="Remove"/>.</summary>
        public async Task<ApplyResult> ApplyAsync(string gameDir, string version)
        {
            var asiSource = GetCachedAsiPath(version);
            if (!File.Exists(asiSource))
                throw new InvalidOperationException($"AMD-NR-bridge {version} is not downloaded.");

            var iniPath = GameInstallationService.ResolveOptiScalerIniPath(gameDir);
            if (!File.Exists(iniPath))
                throw new InvalidOperationException("OptiScaler is not installed in this folder (OptiScaler.ini not found).");
            if (!File.Exists(Path.Combine(gameDir, DlssNrIniFileName)))
                throw new InvalidOperationException("DLSS-NR-on-AMD is not installed in this folder (dlssnr_on_amd.ini not found).");

            var ownFsr = await Task.Run(() => GameHasOwnFsr(gameDir));
            var iniText = File.ReadAllText(iniPath);
            var (newText, changes) = ApplySettings(iniText, ownFsr ? OwnFsrSettings : DefaultSettings);
            if (changes.Count > 0)
                File.WriteAllText(iniPath, newText, new UTF8Encoding(false));

            var pluginDir = ResolvePluginDir(gameDir, newText);
            Directory.CreateDirectory(pluginDir);
            var asiTarget = Path.Combine(pluginDir, BridgeAsiFileName);
            File.Copy(asiSource, asiTarget, overwrite: true);

            DebugWindow.Log($"[AmdNrBridge] Applied {version} to '{gameDir}' (own FSR: {ownFsr}, plugin: {asiTarget}, " +
                $"ini: {(changes.Count == 0 ? "already set" : string.Join(", ", changes.Select(c => $"{c.Key}={c.Value}")))})");

            if (OperatingSystem.IsWindows())
                await ProbeDxgiAsync(asiTarget);

            return new ApplyResult(ownFsr, asiTarget, changes);
        }

        /// <summary>Removes the bridge's plugin from <paramref name="gameDir"/> and puts back the
        /// OptiScaler.ini values <paramref name="changes"/> recorded — only where the value is still
        /// the one the bridge set, so a later change by the user or a profile is kept. A key that was
        /// absent before goes back to "auto", as the bridge's own uninstaller does. Null changes (e.g.
        /// never recorded) only remove the plugin.</summary>
        public static void Remove(string gameDir, IEnumerable<AmdNrBridgeIniChange>? changes)
        {
            foreach (var dir in GetCandidatePluginDirs(gameDir).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var asi = Path.Combine(dir, BridgeAsiFileName);
                if (!File.Exists(asi)) continue;
                File.Delete(asi);
                DebugWindow.Log($"[AmdNrBridge] Removed {asi}");
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch (IOException) { }
            }

            var iniPath = GameInstallationService.ResolveOptiScalerIniPath(gameDir);
            if (changes == null || !File.Exists(iniPath)) return;

            var text = File.ReadAllText(iniPath);
            var restored = RestoreSettings(text, changes);
            if (!ReferenceEquals(restored, text))
            {
                File.WriteAllText(iniPath, restored, new UTF8Encoding(false));
                DebugWindow.Log($"[AmdNrBridge] Restored OptiScaler.ini values in '{gameDir}'");
            }
        }

        /// <summary>Sets every setting whose current value isn't already acceptable, returning the
        /// new text and one change per key actually touched.</summary>
        internal static (string Text, List<AmdNrBridgeIniChange> Changes) ApplySettings(string iniText, IEnumerable<RequiredSetting> settings)
        {
            var changes = new List<AmdNrBridgeIniChange>();
            foreach (var s in settings)
            {
                var current = GetIniValue(iniText, s.Section, s.Key);
                if (string.Equals(current, s.Value, StringComparison.OrdinalIgnoreCase) ||
                    (current != null && s.Also.Contains(current, StringComparer.OrdinalIgnoreCase)))
                    continue;
                iniText = SetIniValue(iniText, s.Section, s.Key, s.Value);
                changes.Add(new AmdNrBridgeIniChange(s.Section, s.Key, s.Value, current));
            }
            return (iniText, changes);
        }

        /// <summary>Returns the same string instance when nothing had to be restored.</summary>
        internal static string RestoreSettings(string iniText, IEnumerable<AmdNrBridgeIniChange> changes)
        {
            var result = iniText;
            foreach (var c in changes)
            {
                var current = GetIniValue(result, c.Section, c.Key);
                if (!string.Equals(current, c.Value, StringComparison.OrdinalIgnoreCase)) continue;
                result = SetIniValue(result, c.Section, c.Key, c.Original ?? "auto");
            }
            return result;
        }

        /// <summary>True when an exe in <paramref name="gameDir"/> names the FidelityFX API (ASCII or
        /// UTF-16), as a game with its own FSR 3.1 / FSR 4 does. Exes carrying DLSS-NR-on-AMD's
        /// marker are skipped. Streams each file — game exes can be hundreds of MB.</summary>
        internal static bool GameHasOwnFsr(string gameDir)
        {
            var ffxPatterns = FidelityFxNames.SelectMany(n => new[] { Encoding.ASCII.GetBytes(n), Encoding.Unicode.GetBytes(n) }).ToArray();
            var markerPattern = Encoding.ASCII.GetBytes(DlssNrMarker);

            foreach (var exe in Directory.EnumerateFiles(gameDir, "*.exe"))
            {
                try
                {
                    var found = FindPatterns(exe, ffxPatterns.Append(markerPattern).ToArray());
                    if (found[^1]) continue; // DLSS-NR-on-AMD's own setup
                    if (found.Take(found.Length - 1).Any(f => f)) return true;
                }
                catch (IOException ex) { DebugWindow.Log($"[AmdNrBridge] Could not scan {exe}: {ex.Message}"); }
                catch (UnauthorizedAccessException ex) { DebugWindow.Log($"[AmdNrBridge] Could not scan {exe}: {ex.Message}"); }
            }
            return false;
        }

        /// <summary>Which of <paramref name="patterns"/> occur in the file, read in chunks that
        /// overlap by the longest pattern so a match split across two reads isn't missed.</summary>
        private static bool[] FindPatterns(string path, byte[][] patterns)
        {
            var found = new bool[patterns.Length];
            var overlap = patterns.Max(p => p.Length) - 1;
            var buffer = new byte[1 << 20];
            var carried = 0;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
            int read;
            while ((read = stream.Read(buffer, carried, buffer.Length - carried)) > 0)
            {
                var window = buffer.AsSpan(0, carried + read);
                for (int i = 0; i < patterns.Length; i++)
                    if (!found[i] && window.IndexOf(patterns[i]) >= 0) found[i] = true;
                if (found.All(f => f)) break;

                carried = Math.Min(overlap, window.Length);
                window.Slice(window.Length - carried).CopyTo(buffer);
            }
            return found;
        }

        /// <summary>An explicit, non-"auto" [Plugins] Path is where OptiScaler loads plugins from, so
        /// the plugin goes there; otherwise the default next to OptiScaler's own files.</summary>
        private static string ResolvePluginDir(string gameDir, string iniText)
        {
            var configured = GetIniValue(iniText, "Plugins", "Path");
            if (!string.IsNullOrEmpty(configured) && !configured.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return Path.IsPathRooted(configured) ? configured : Path.Combine(gameDir, configured);
            return Path.Combine(GameInstallationService.ResolveExtrasRoot(gameDir), "plugins");
        }

        /// <summary>Lets the bridge record the DXGI vtable layout it hooks ahead of the first launch
        /// (the bridge's installer does the same). Best effort: if it fails, the bridge records it
        /// itself when the game starts.</summary>
        private static async Task ProbeDxgiAsync(string asiPath)
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "rundll32.exe"),
                    Arguments = $"\"{asiPath}\",ProbeDxgi",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (proc == null) return;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await proc.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException) { try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ } }
            }
            catch (Exception ex) { DebugWindow.Log($"[AmdNrBridge] ProbeDxgi skipped: {ex.Message}"); }
        }

        // ── OptiScaler.ini text editing ──────────────────────────────────────────────
        // Accepts both "Key=Value" and "Key = Value" (OptiScaler writes the latter when it saves its
        // ini from its menu) and only replaces the value, keeping each line's own spacing.

        internal static string? GetIniValue(string text, string section, string key)
        {
            var lines = text.Split('\n');
            var (start, end) = FindSection(lines, section);
            if (start < 0) return null;
            for (int i = start + 1; i < end; i++)
                if (TryParseKeyLine(lines[i], key, out var valueStart))
                    return lines[i].Substring(valueStart).Trim();
            return null;
        }

        internal static string SetIniValue(string text, string section, string key, string value)
        {
            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Split('\n').ToList();
            var (start, end) = FindSection(lines, section);
            if (start < 0)
                return text.TrimEnd() + newline + newline + $"[{section}]" + newline + $"{key}={value}" + newline;

            for (int i = start + 1; i < end; i++)
            {
                if (!TryParseKeyLine(lines[i], key, out var valueStart)) continue;
                var line = lines[i];
                var cr = line.EndsWith('\r') ? "\r" : "";
                var prefix = line.Substring(0, valueStart);
                var afterEq = line.Substring(valueStart).TrimEnd('\r');
                var leading = afterEq.Length - afterEq.TrimStart().Length;
                lines[i] = prefix + afterEq.Substring(0, leading) + value + cr;
                return string.Join('\n', lines);
            }

            lines.Insert(start + 1, $"{key}={value}" + (newline == "\r\n" ? "\r" : ""));
            return string.Join('\n', lines);
        }

        /// <summary>Header index of <paramref name="section"/> and the index where its body ends (the
        /// next header or the end of the file); (-1, -1) when absent.</summary>
        private static (int Start, int End) FindSection(IList<string> lines, string section)
        {
            var header = $"[{section}]";
            for (int i = 0; i < lines.Count; i++)
            {
                if (!lines[i].Trim().Equals(header, StringComparison.OrdinalIgnoreCase)) continue;
                var end = i + 1;
                while (end < lines.Count && !lines[end].TrimStart().StartsWith('[')) end++;
                return (i, end);
            }
            return (-1, -1);
        }

        private static bool TryParseKeyLine(string line, string key, out int valueStart)
        {
            valueStart = -1;
            var eq = line.IndexOf('=');
            if (eq <= 0) return false;
            var name = line.Substring(0, eq).Trim();
            if (name.StartsWith(';') || !name.Equals(key, StringComparison.OrdinalIgnoreCase)) return false;
            valueStart = eq + 1;
            return true;
        }

        // ── Detection ────────────────────────────────────────────────────────────────

        /// <summary>True when the bridge's plugin is in one of the places OptiScaler loads it from
        /// for <paramref name="gameDir"/>: the folder named by a non-"auto" [Plugins] Path in
        /// OptiScaler.ini, or either default plugins folder.</summary>
        public static bool IsInstalledIn(string gameDir)
        {
            foreach (var dir in GetCandidatePluginDirs(gameDir))
                if (File.Exists(Path.Combine(dir, BridgeAsiFileName))) return true;
            return false;
        }

        internal static IEnumerable<string> GetCandidatePluginDirs(string gameDir)
        {
            var iniPath = GameInstallationService.ResolveOptiScalerIniPath(gameDir);
            string? configured = null;
            try { if (File.Exists(iniPath)) configured = GetIniValue(File.ReadAllText(iniPath), "Plugins", "Path"); }
            catch (IOException) { }
            if (!string.IsNullOrEmpty(configured) && !configured.Equals("auto", StringComparison.OrdinalIgnoreCase))
                yield return Path.IsPathRooted(configured) ? configured : Path.Combine(gameDir, configured);
            yield return Path.Combine(gameDir, "plugins");
            yield return Path.Combine(gameDir, "OptiScaler", "plugins");
        }
    }
}
