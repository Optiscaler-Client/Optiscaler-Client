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

using OptiscalerClient.Helpers;
using OptiscalerClient.Models;
using OptiscalerClient.Views;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace OptiscalerClient.Services;

public class GameAnalyzerService
{
    private static readonly object _cacheLock = new();
    private static readonly Dictionary<string, AnalysisCacheEntry> _analysisCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] _dlssNames = new[] { "nvngx_dlss.dll" };
    private static readonly string[] _dlssFrameGenNames = new[] { "nvngx_dlssg.dll" };
    private static readonly string[] _fsrNames = new[] {
        "amd_fidelityfx_dx12.dll",
        "amd_fidelityfx_vk.dll",
        Fsr4Int8DllHelper.LegacyFileName,
        Fsr4Int8DllHelper.CurrentFileName,
        "amd_fidelityfx_loader_dx12.dll",
        "ffx_fsr2_api_x64.dll",
        "ffx_fsr2_api_dx12_x64.dll",
        "ffx_fsr2_api_vk_x64.dll",
        "ffx_fsr3_api_x64.dll",
        "ffx_fsr3_api_dx12_x64.dll"
    };
    private static readonly string[] _xessNames = new[] { "libxess.dll" };

    private static readonly HashSet<string> _allTargetFileNames;
    private static readonly string _diskCachePath = Path.Combine(AppPaths.GetAppDataRoot(), "analysis_cache.json");
    private static volatile bool _diskCacheLoaded = false;

    static GameAnalyzerService()
    {
        _allTargetFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "optiscaler_manifest.json",
            "optiscaler.log",
            "OptiScaler.ini"
        };
        foreach (var n in _dlssNames) _allTargetFileNames.Add(n);
        foreach (var n in _dlssFrameGenNames) _allTargetFileNames.Add(n);
        foreach (var n in _fsrNames) _allTargetFileNames.Add(n);
        foreach (var n in _xessNames) _allTargetFileNames.Add(n);
    }

    public static void InvalidateCacheForPath(string? installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath))
            return;

        string normalized;
        try
        {
            normalized = Path.GetFullPath(installPath);
        }
        catch
        {
            normalized = installPath;
        }

        lock (_cacheLock)
        {
            _analysisCache.Remove(normalized);
        }
    }

    public void AnalyzeGame(Game game, bool forceRefresh = false)
    {
        if (string.IsNullOrEmpty(game.InstallPath) || !Directory.Exists(game.InstallPath))
            return;

        string normalizedInstallPath;
        DateTime directoryWriteStamp;

        try
        {
            normalizedInstallPath = Path.GetFullPath(game.InstallPath);
            directoryWriteStamp = Directory.GetLastWriteTimeUtc(normalizedInstallPath);
        }
        catch
        {
            normalizedInstallPath = game.InstallPath;
            directoryWriteStamp = DateTime.MinValue;
        }

        if (!forceRefresh && TryApplyCachedAnalysis(game, normalizedInstallPath, directoryWriteStamp))
            return;

        // Reset current versions before analysis
        game.DlssVersion = null;
        game.DlssPath = null;
        game.DlssViaOptiscaler = false;
        game.FsrVersion = null;
        game.FsrPath = null;
        game.FsrViaOptiscaler = false;
        game.XessVersion = null;
        game.XessPath = null;
        game.XessViaOptiscaler = false;
        game.IsOptiscalerInstalled = false;
        game.OptiscalerVersion = null; // Will be repopulated from manifest or log
        game.IsFsr4DllSwapped = false;
        game.Fsr4DllSwapTargetFileName = null;
        game.IsDlssEnablerInstalled = false;

        HashSet<string> ignoredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blockHeuristicFallbackDetection = false;

        try
        {
            // ── Single-pass file collection ──────────────────────────────────────────
            // Traverse the game directory once and classify all relevant files by name.
            var collectedFiles = CollectRelevantFiles(game.InstallPath);

            // ── Detect OptiScaler ──────────────────────────────────────────────────
            // Do this first so we can ignore its installed files when looking for native DLLs
            try
            {
                // ── Priority 0: external backup store ──────────────────────────────
                // A valid external backup means OptiScaler was installed (and tracked) by v1.0.5+.
                // Check candidate game dirs derived from InstallPath / ExecutablePath.
                {
                    var backupStore = new BackupStoreService();
                    var candidateDirs = new List<string?> { game.InstallPath };
                    if (!string.IsNullOrEmpty(game.ExecutablePath))
                        candidateDirs.Add(Path.GetDirectoryName(game.ExecutablePath));

                    foreach (var candidate in candidateDirs.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)))
                    {
                        if (backupStore.HasValidBackup(candidate!))
                        {
                            var extManifest = backupStore.LoadManifest(candidate!);
                            if (extManifest != null && string.Equals(extManifest.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase))
                            {
                                // DLL-swap tracking is independent of OptiScaler's own install status —
                                // a manifest can carry either flag, or both, at once (see
                                // GameInstallationService.SwapFsr4Dll).
                                if (extManifest.IncludesDllSwap)
                                {
                                    game.IsFsr4DllSwapped = true;
                                    game.Fsr4DllSwapTargetFileName = extManifest.DllSwapTargetFileName;
                                    if (!string.IsNullOrEmpty(extManifest.DllSwapExtrasVersion))
                                        game.Fsr4ExtraVersion = extManifest.DllSwapExtrasVersion;
                                }

                                if (extManifest.IncludesDlssEnabler)
                                    game.IsDlssEnablerInstalled = true;

                                if (extManifest.IncludesOptiscaler)
                                {
                                    game.IsOptiscalerInstalled = true;
                                    if (!string.IsNullOrEmpty(extManifest.OptiscalerVersion))
                                        game.OptiscalerVersion = extManifest.OptiscalerVersion;

                                    // The manifest's own recorded install dir is authoritative - candidate
                                    // (InstallPath/ExecutablePath's dir) can differ from where OptiScaler
                                    // actually landed (e.g. UE games installing into Binaries\Win64), which
                                    // would silently break every path comparison below.
                                    var extInstallDir = !string.IsNullOrEmpty(extManifest.InstalledGameDirectory) && Directory.Exists(extManifest.InstalledGameDirectory)
                                        ? extManifest.InstalledGameDirectory
                                        : candidate!;
                                    foreach (var f in extManifest.InstalledFiles)
                                        ignoredFiles.Add(Path.GetFullPath(Path.Combine(extInstallDir, f)));
                                    blockHeuristicFallbackDetection = true;
                                    DebugWindow.Log($"[Analyzer] Priority 0 (external store) detected OptiScaler {extManifest.OptiscalerVersion} for '{game.Name}'");
                                    goto detectOtherComponents;
                                }

                                // Manifest found and processed (swap-only, or neither flag set) —
                                // don't consider a second candidate dir for the same game, but do
                                // fall through to the normal OptiScaler detection below since this
                                // manifest didn't establish that status one way or the other.
                                break;
                            }
                        }
                    }
                }

                // ── Priority 1: manifest ────────────────────────────────────────────
                var manifestFiles = collectedFiles.TryGetValue("optiscaler_manifest.json", out var mf) ? mf.ToArray() : Array.Empty<string>();
                if (manifestFiles.Length > 0)
                {
                    try
                    {
                        var manifestJson = File.ReadAllText(manifestFiles[0]);
                        var manifest = System.Text.Json.JsonSerializer.Deserialize<Models.InstallationManifest>(manifestJson);
                        if (manifest != null)
                        {
                            // Only a committed manifest should be treated as a valid installation.
                            var isCommitted = string.Equals(
                                manifest.OperationStatus,
                                "committed",
                                StringComparison.OrdinalIgnoreCase);

                            if (!isCommitted)
                            {
                                blockHeuristicFallbackDetection = true;
                                try
                                {
                                    var installer = new GameInstallationService();
                                    installer.RecoverIncompleteInstallIfNeeded(game.InstallPath);
                                }
                                catch
                                {
                                    // Ignore recovery errors here; we'll just avoid false positives
                                    // from fallback detection for this analysis pass.
                                }
                            }

                            // Determine absolute game directory to validate expected markers and ignored files.
                            string originDir = string.IsNullOrEmpty(manifest.InstalledGameDirectory)
                                ? Path.GetDirectoryName(Path.GetDirectoryName(manifestFiles[0]))!
                                : manifest.InstalledGameDirectory;

                            var markersLookValid = true;
                            if (isCommitted && !string.IsNullOrEmpty(originDir))
                            {
                                var markers = manifest.ExpectedFinalMarkers ?? new List<string>();
                                if (markers.Count > 0)
                                {
                                    markersLookValid = markers.Any(rel =>
                                    {
                                        try
                                        {
                                            return File.Exists(Path.Combine(originDir, rel));
                                        }
                                        catch
                                        {
                                            return false;
                                        }
                                    });
                                }
                            }

                            if (isCommitted && markersLookValid)
                            {
                                game.IsOptiscalerInstalled = true;
                                if (!string.IsNullOrEmpty(manifest.OptiscalerVersion))
                                    game.OptiscalerVersion = manifest.OptiscalerVersion;

                                if (!string.IsNullOrEmpty(originDir))
                                {
                                    foreach (var relFile in manifest.InstalledFiles)
                                    {
                                        ignoredFiles.Add(Path.GetFullPath(Path.Combine(originDir, relFile)));
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugWindow.Log($"[Analyzer] Corrupt manifest in '{game.InstallPath}': {ex.Message}");
                    }
                }

                // ── Priority 2: runtime log (overrides if it has richer version info) ──
                if (!blockHeuristicFallbackDetection &&
                    (!game.IsOptiscalerInstalled || string.IsNullOrEmpty(game.OptiscalerVersion)))
                {
                    try
                    {
                        var logs = collectedFiles.TryGetValue("optiscaler.log", out var lf) ? lf.ToArray() : Array.Empty<string>();
                        if (logs.Length > 0)
                        {
                            // Example log line: "[2024-...] [Init] OptiScaler v0.7.0-rc1"
                            foreach (var line in File.ReadLines(logs[0]).Take(10))
                            {
                                if (line.Contains("OptiScaler v", StringComparison.OrdinalIgnoreCase))
                                {
                                    var idx = line.IndexOf("OptiScaler v", StringComparison.OrdinalIgnoreCase);
                                    if (idx != -1)
                                    {
                                        var verPart = line.Substring(idx + 12).Trim();
                                        var endIdx = verPart.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
                                        if (endIdx != -1) verPart = verPart.Substring(0, endIdx);
                                        if (!string.IsNullOrEmpty(verPart))
                                        {
                                            game.IsOptiscalerInstalled = true;
                                            game.OptiscalerVersion = verPart;
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugWindow.Log($"[Analyzer] Error reading OptiScaler log for '{game.Name}': {ex.Message}");
                    }
                }

                // ── Priority 3: OptiScaler.ini presence (no version — last resort) ──
                if (!blockHeuristicFallbackDetection && !game.IsOptiscalerInstalled)
                {
                    var iniFiles = collectedFiles.TryGetValue("OptiScaler.ini", out var inf) ? inf.ToArray() : Array.Empty<string>();
                    if (iniFiles.Length > 0)
                        game.IsOptiscalerInstalled = true;
                }
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[Analyzer] OptiScaler detection error for '{game.Name}': {ex.Message}");
            }
            detectOtherComponents:
            FindBestVersionFromCollected(game, collectedFiles, _dlssNames, ignoredFiles, (g, path, ver) =>
            {
                g.DlssPath = path;
                g.DlssVersion = ver;
            }, g => g.DlssViaOptiscaler = true);

            // DLSS Frame Gen
            FindBestVersionFromCollected(game, collectedFiles, _dlssFrameGenNames, ignoredFiles, (g, path, ver) => { g.DlssFrameGenPath = path; g.DlssFrameGenVersion = ver; });

            // FSR
            FindBestVersionFromCollected(game, collectedFiles, _fsrNames, ignoredFiles, (g, path, ver) => { g.FsrPath = path; g.FsrVersion = ver; }, g => g.FsrViaOptiscaler = true);

            // XeSS
            FindBestVersionFromCollected(game, collectedFiles, _xessNames, ignoredFiles, (g, path, ver) => { g.XessPath = path; g.XessVersion = ver; }, g => g.XessViaOptiscaler = true);

        }
        catch (Exception ex)
        {
            DebugWindow.Log($"[Analyzer] General analysis error for '{game.Name}': {ex.Message}");
        }

        SaveAnalysisCache(game, normalizedInstallPath, directoryWriteStamp);
    }

    private static bool TryApplyCachedAnalysis(Game game, string installPath, DateTime directoryWriteStamp)
    {
        lock (_cacheLock)
        {
            if (!_analysisCache.TryGetValue(installPath, out var cached))
                return false;

            if (cached.DirectoryWriteStampUtc != directoryWriteStamp)
                return false;

            cached.ApplyTo(game);
            return true;
        }
    }

    private static void SaveAnalysisCache(Game game, string installPath, DateTime directoryWriteStamp)
    {
        var snapshot = AnalysisCacheEntry.FromGame(game, directoryWriteStamp);
        lock (_cacheLock)
        {
            _analysisCache[installPath] = snapshot;
        }
    }

    private static void FindBestVersionFromCollected(Game game, Dictionary<string, List<string>> collectedFiles, string[] filePatterns, HashSet<string> ignoredFiles, Action<Game, string, string> updateAction, Action<Game>? markViaOptiscaler = null)
    {
        var highestVer = new Version(0, 0);
        string? bestPath = null;
        string? bestVerStr = null;

        // Best match among files OptiScaler itself installed - only used if no native match wins.
        var highestIgnoredVer = new Version(0, 0);
        string? bestIgnoredPath = null;
        string? bestIgnoredVerStr = null;

        foreach (var pattern in filePatterns)
        {
            if (!collectedFiles.TryGetValue(pattern, out var files)) continue;
            foreach (var file in files)
            {
                var versionStr = GetFileVersion(file);

                // Clean up version string if it contains "FSR ", e.g. "FSR 3.1.4"
                string parseableVerStr = versionStr;
                if (parseableVerStr.StartsWith("FSR ", StringComparison.OrdinalIgnoreCase))
                    parseableVerStr = parseableVerStr.Substring(4).Trim();

                // Also take only the first component if there are spaces, e.g. "3.1.0 (release)"
                parseableVerStr = parseableVerStr.Split(' ')[0];

                if (!Version.TryParse(parseableVerStr, out var currentVer)) continue;

                // "0.0.0.0" means GetFileVersion couldn't find real version info (missing/unreadable
                // resource), not that the DLL is actually version 0 — treat it as "no version" rather
                // than reporting a meaningless placeholder to the user.
                if (currentVer == new Version(0, 0, 0, 0)) continue;

                if (ignoredFiles.Contains(Path.GetFullPath(file)))
                {
                    if (currentVer > highestIgnoredVer)
                    {
                        highestIgnoredVer = currentVer;
                        bestIgnoredPath = file;
                        bestIgnoredVerStr = versionStr;
                    }
                    continue;
                }

                if (currentVer > highestVer)
                {
                    highestVer = currentVer;
                    bestPath = file;
                    bestVerStr = versionStr; // keep original string for display
                }
            }
        }

        if (bestPath != null && bestVerStr != null)
        {
            updateAction(game, bestPath, bestVerStr);
        }
        else if (bestIgnoredPath != null && bestIgnoredVerStr != null)
        {
            // No native install, but OptiScaler provides its own copy - still report the
            // version, just flagged as not native.
            updateAction(game, bestIgnoredPath, bestIgnoredVerStr);
            markViaOptiscaler?.Invoke(game);
        }
    }

    private static Dictionary<string, List<string>> CollectRelevantFiles(string path)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive
        };

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", options))
            {
                var name = Path.GetFileName(file);
                if (_allTargetFileNames.Contains(name))
                {
                    if (!result.TryGetValue(name, out var list))
                    {
                        list = new List<string>();
                        result[name] = list;
                    }
                    list.Add(file);
                }

            }
        }
        catch (Exception ex)
        {
            DebugWindow.Log($"[Analyzer] Error enumerating files in '{path}': {ex.Message}");
        }

        return result;
    }

    public static void LoadCacheFromDisk()
    {
        lock (_cacheLock)
        {
            if (_diskCacheLoaded) return;
            _diskCacheLoaded = true;

            try
            {
                if (!File.Exists(_diskCachePath)) return;
                var json = File.ReadAllText(_diskCachePath);
                var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, AnalysisCacheEntry>>(json);
                if (loaded == null) return;

                foreach (var kv in loaded)
                    _analysisCache[kv.Key] = kv.Value;

                DebugWindow.Log($"[Analyzer] Loaded {loaded.Count} cached entries from disk.");
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[Analyzer] Failed to load disk cache: {ex.Message}");
            }
        }
    }

    public static void FlushCacheToDisk()
    {
        try
        {
            Dictionary<string, AnalysisCacheEntry> snapshot;
            lock (_cacheLock)
            {
                snapshot = new Dictionary<string, AnalysisCacheEntry>(_analysisCache, StringComparer.OrdinalIgnoreCase);
            }
            var dir = Path.GetDirectoryName(_diskCachePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = System.Text.Json.JsonSerializer.Serialize(snapshot);
            File.WriteAllText(_diskCachePath, json);
            DebugWindow.Log($"[Analyzer] Flushed {snapshot.Count} cache entries to disk.");
        }
        catch (Exception ex)
        {
            DebugWindow.Log($"[Analyzer] Failed to flush cache to disk: {ex.Message}");
        }
    }

    private static string GetFileVersion(string filePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(filePath);

            // ProductVersion is usually more accurate for libraries like DLSS (e.g. "3.7.10.0")
            // FileVersion might be "1.0.0.0" wrapper.
            string? version = null;
            if (!string.IsNullOrEmpty(info.ProductVersion) && info.ProductVersion != "1.0.0.0" && !info.ProductVersion.StartsWith("1.0."))
                version = info.ProductVersion.Replace(',', '.').Split(' ')[0];
            else if (!string.IsNullOrEmpty(info.FileVersion))
                version = info.FileVersion.Replace(',', '.').Split(' ')[0];
            else
                version = $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}";

            // On Linux, FileVersionInfo cannot read Windows PE version resources — fall back to manual PE parsing
            if (version == "0.0.0.0" && !OperatingSystem.IsWindows())
                version = ReadPeFileVersion(filePath);

            return version;
        }
        catch
        {
            return OperatingSystem.IsWindows() ? "0.0.0.0" : ReadPeFileVersion(filePath);
        }
    }

    /// <summary>
    /// Reads the file version from a Windows PE binary by parsing the resource section directly.
    /// Used on Linux where FileVersionInfo cannot parse PE version resources.
    /// </summary>
    private static string ReadPeFileVersion(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(fs);

            // DOS header: MZ signature + e_lfanew at offset 0x3C
            if (reader.ReadUInt16() != 0x5A4D) return "0.0.0.0";
            fs.Seek(0x3C, SeekOrigin.Begin);
            var peOffset = reader.ReadUInt32();

            // PE signature
            fs.Seek(peOffset, SeekOrigin.Begin);
            if (reader.ReadUInt32() != 0x00004550) return "0.0.0.0";

            // COFF header
            reader.ReadUInt16(); // Machine
            var numSections = reader.ReadUInt16();
            reader.ReadBytes(12); // TimeDateStamp, PointerToSymbolTable, NumberOfSymbols
            var optHeaderSize = reader.ReadUInt16();
            reader.ReadUInt16(); // Characteristics

            // Optional header
            var optHeaderStart = fs.Position;
            var magic = reader.ReadUInt16();
            bool is64 = magic == 0x20B; // PE32+ vs PE32

            // DataDirectory[2] = Resource Table
            // PE32:  DataDirectory starts at offset 96 → resource at 96 + 2*8 = 112
            // PE32+: DataDirectory starts at offset 112 → resource at 112 + 2*8 = 128
            var resourceDirOffset = (is64 ? 112 : 96) + 16;
            fs.Seek(optHeaderStart + resourceDirOffset, SeekOrigin.Begin);
            var rsrcRVA = reader.ReadUInt32();
            var rsrcSize = reader.ReadUInt32();

            if (rsrcRVA == 0 || rsrcSize == 0) return "0.0.0.0";

            // Section headers: find the section containing the resource RVA
            fs.Seek(optHeaderStart + optHeaderSize, SeekOrigin.Begin);
            uint rsrcFileOffset = 0;
            for (int i = 0; i < numSections; i++)
            {
                reader.ReadBytes(8); // Name
                reader.ReadUInt32(); // VirtualSize
                var va = reader.ReadUInt32(); // VirtualAddress
                var rawSize = reader.ReadUInt32();
                var rawOffset = reader.ReadUInt32();
                reader.ReadBytes(16); // Rest of section header

                if (va <= rsrcRVA && rsrcRVA < va + rawSize)
                {
                    rsrcFileOffset = rawOffset + (rsrcRVA - va);
                    break;
                }
            }

            if (rsrcFileOffset == 0) return "0.0.0.0";

            // Read resource section (cap at 4 MB — version resources are tiny)
            fs.Seek(rsrcFileOffset, SeekOrigin.Begin);
            var rsrcData = reader.ReadBytes((int)Math.Min(rsrcSize, 4 * 1024 * 1024));

            // Search for VS_FIXEDFILEINFO magic: FEEF04BD
            for (int i = 0; i <= rsrcData.Length - 20; i++)
            {
                if (rsrcData[i] != 0xBD || rsrcData[i + 1] != 0x04 ||
                    rsrcData[i + 2] != 0xEF || rsrcData[i + 3] != 0xFE) continue;

                // Offset +4: dwStrucVersion must be 0x00010000 (version 1.0)
                var structVer = BitConverter.ToUInt32(rsrcData, i + 4);
                if (structVer != 0x00010000) continue;

                var ms = BitConverter.ToUInt32(rsrcData, i + 8);  // dwFileVersionMS
                var ls = BitConverter.ToUInt32(rsrcData, i + 12); // dwFileVersionLS

                if (ms == 0 && ls == 0) continue;

                return $"{ms >> 16}.{ms & 0xFFFF}.{ls >> 16}.{ls & 0xFFFF}";
            }
        }
        catch { }

        return "0.0.0.0";
    }

    private sealed class AnalysisCacheEntry
    {
        public DateTime DirectoryWriteStampUtc { get; set; }
        public string? DlssVersion { get; set; }
        public string? DlssPath { get; set; }
        public bool DlssViaOptiscaler { get; set; }
        public string? DlssFrameGenVersion { get; set; }
        public string? DlssFrameGenPath { get; set; }
        public string? FsrVersion { get; set; }
        public string? FsrPath { get; set; }
        public bool FsrViaOptiscaler { get; set; }
        public string? XessVersion { get; set; }
        public string? XessPath { get; set; }
        public bool XessViaOptiscaler { get; set; }
        public bool IsOptiscalerInstalled { get; set; }
        public string? OptiscalerVersion { get; set; }

        public static AnalysisCacheEntry FromGame(Game game, DateTime directoryWriteStampUtc)
        {
            return new AnalysisCacheEntry
            {
                DirectoryWriteStampUtc = directoryWriteStampUtc,
                DlssVersion = game.DlssVersion,
                DlssPath = game.DlssPath,
                DlssViaOptiscaler = game.DlssViaOptiscaler,
                DlssFrameGenVersion = game.DlssFrameGenVersion,
                DlssFrameGenPath = game.DlssFrameGenPath,
                FsrVersion = game.FsrVersion,
                FsrPath = game.FsrPath,
                FsrViaOptiscaler = game.FsrViaOptiscaler,
                XessVersion = game.XessVersion,
                XessPath = game.XessPath,
                XessViaOptiscaler = game.XessViaOptiscaler,
                IsOptiscalerInstalled = game.IsOptiscalerInstalled,
                OptiscalerVersion = game.OptiscalerVersion
            };
        }

        public void ApplyTo(Game game)
        {
            game.DlssVersion = DlssVersion;
            game.DlssPath = DlssPath;
            game.DlssViaOptiscaler = DlssViaOptiscaler;
            game.DlssFrameGenVersion = DlssFrameGenVersion;
            game.DlssFrameGenPath = DlssFrameGenPath;
            game.FsrVersion = FsrVersion;
            game.FsrPath = FsrPath;
            game.FsrViaOptiscaler = FsrViaOptiscaler;
            game.XessVersion = XessVersion;
            game.XessPath = XessPath;
            game.XessViaOptiscaler = XessViaOptiscaler;
            game.IsOptiscalerInstalled = IsOptiscalerInstalled;
            game.OptiscalerVersion = OptiscalerVersion;
        }
    }
}
