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

using OptiscalerClient.Models;
using OptiscalerClient.Views;
using System.Collections.Concurrent;
using System.IO;

namespace OptiscalerClient.Services;

public class GameScannerService
{
    private readonly IGameScanner _steamScanner;
    private readonly IGameScanner _heroicScanner;
    private readonly IGameScanner? _epicScanner;
    private readonly IGameScanner? _gogScanner;
    private readonly IGameScanner? _xboxScanner;
    private readonly IGameScanner? _eaScanner;
    private readonly IGameScanner? _battleNetScanner;
    private readonly IGameScanner? _ubisoftScanner;
    private readonly IGameScanner? _lutrisScanner;
    private readonly ExclusionService _exclusions;

    public GameScannerService()
    {
        _steamScanner = new SteamScanner();
        _heroicScanner = new HeroicScanner();

        if (OperatingSystem.IsWindows())
        {
            _epicScanner = new EpicScanner();
            _gogScanner = new GogScanner();
            _xboxScanner = new XboxScanner();
            _eaScanner = new EaScanner();
            _battleNetScanner = new BattleNetScanner();
            _ubisoftScanner = new UbisoftScanner();
        }
        else
        {
            // Lutris has no Windows client — only relevant when scanning a Linux game library.
            _lutrisScanner = new LutrisScanner();
        }

        // config.json lives next to the executable (copied by the build)
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        _exclusions = new ExclusionService(configPath);
    }

    public async Task<List<Game>> ScanAllGamesAsync(ScanSourcesConfig? scanConfig = null, IReadOnlyCollection<string>? allowedDriveRoots = null)
    {
        return await Task.Run(async () =>
        {
            DebugWindow.Log("[Scanner] Executing global game scan across all platforms...");
            GameAnalyzerService.LoadCacheFromDisk();

            if (scanConfig == null)
                scanConfig = new ScanSourcesConfig();

            bool IsDriveAllowed(Game game)
            {
                if (allowedDriveRoots == null || allowedDriveRoots.Count == 0)
                    return true;
                try
                {
                    var root = Path.GetPathRoot(game.InstallPath);
                    if (string.IsNullOrEmpty(root)) return false;
                    return allowedDriveRoots.Contains(root, StringComparer.OrdinalIgnoreCase);
                }
                catch { return false; }
            }

            // ── 1. Parallel platform scanner discovery ───────────────────────────
            var platformTasks = new List<Task<List<Game>>>();

            if (scanConfig.ScanSteam)
                platformTasks.Add(Task.Run(async () => { try { DebugWindow.Log("[Scanner] Scanning Steam library..."); return await _steamScanner.ScanAsync(); } catch (Exception ex) { DebugWindow.Log($"[Scanner] Steam scan error: {ex.Message}"); return new List<Game>(); } }));
            if (scanConfig.ScanHeroic)
                platformTasks.Add(Task.Run(async () => { try { DebugWindow.Log("[Scanner] Scanning Heroic library..."); return await _heroicScanner.ScanAsync(); } catch (Exception ex) { DebugWindow.Log($"[Scanner] Heroic scan error: {ex.Message}"); return new List<Game>(); } }));
            if (scanConfig.ScanEpic && _epicScanner != null)
                platformTasks.Add(Task.Run(async () => { try { DebugWindow.Log("[Scanner] Scanning Epic Games library..."); return await _epicScanner.ScanAsync(); } catch (Exception ex) { DebugWindow.Log($"[Scanner] Epic scan error: {ex.Message}"); return new List<Game>(); } }));
            if (scanConfig.ScanGOG && _gogScanner != null)
                platformTasks.Add(Task.Run(async () => { try { DebugWindow.Log("[Scanner] Scanning GOG library..."); return await _gogScanner.ScanAsync(); } catch (Exception ex) { DebugWindow.Log($"[Scanner] GOG scan error: {ex.Message}"); return new List<Game>(); } }));
            if (scanConfig.ScanXbox && _xboxScanner != null)
                platformTasks.Add(Task.Run(async () => { try { DebugWindow.Log("[Scanner] Scanning Xbox library (MS Store)..."); return await _xboxScanner.ScanAsync(); } catch (Exception ex) { DebugWindow.Log($"[Scanner] Xbox scan error: {ex.Message}"); return new List<Game>(); } }));
            if (scanConfig.ScanEA && _eaScanner != null)
                platformTasks.Add(Task.Run(async () => { try { DebugWindow.Log("[Scanner] Scanning EA App library..."); return await _eaScanner.ScanAsync(); } catch (Exception ex) { DebugWindow.Log($"[Scanner] EA scan error: {ex.Message}"); return new List<Game>(); } }));
            if (_battleNetScanner != null)
                platformTasks.Add(Task.Run(async () => { try { DebugWindow.Log("[Scanner] Scanning Battle.net library..."); return await _battleNetScanner.ScanAsync(); } catch (Exception ex) { DebugWindow.Log($"[Scanner] Battle.net scan error: {ex.Message}"); return new List<Game>(); } }));
            if (scanConfig.ScanUbisoft && _ubisoftScanner != null)
                platformTasks.Add(Task.Run(async () => { try { DebugWindow.Log("[Scanner] Scanning Ubisoft Connect library..."); return await _ubisoftScanner.ScanAsync(); } catch (Exception ex) { DebugWindow.Log($"[Scanner] Ubisoft scan error: {ex.Message}"); return new List<Game>(); } }));
            if (scanConfig.ScanLutris && _lutrisScanner != null)
                platformTasks.Add(Task.Run(async () => { try { DebugWindow.Log("[Scanner] Scanning Lutris library..."); return await _lutrisScanner.ScanAsync(); } catch (Exception ex) { DebugWindow.Log($"[Scanner] Lutris scan error: {ex.Message}"); return new List<Game>(); } }));

            var platformResults = await Task.WhenAll(platformTasks);

            // ── 2. Merge, filter, and add custom folder games ───────────────────
            var allScannedGames = platformResults
                .SelectMany(batch => batch)
                .Where(g => !_exclusions.IsExcluded(g) && IsDriveAllowed(g))
                .ToList();

            if (scanConfig.CustomFolders != null && scanConfig.CustomFolders.Count > 0)
            {
                DebugWindow.Log($"[Scanner] Scanning {scanConfig.CustomFolders.Count} custom folder(s)...");
                foreach (var customFolder in scanConfig.CustomFolders)
                {
                    try
                    {
                        var customGames = ScanCustomFolder(customFolder);
                        DebugWindow.Log($"[Scanner] Found {customGames.Count} games in '{customFolder}'");
                        allScannedGames.AddRange(customGames.Where(g => !_exclusions.IsExcluded(g) && IsDriveAllowed(g)));
                    }
                    catch (Exception ex)
                    {
                        DebugWindow.Log($"[Scanner] Error scanning custom folder '{customFolder}': {ex.Message}");
                    }
                }
            }

            // ── 3. Parallel game analysis (up to 4 concurrent) ─────────────────
            var analyzer = new GameAnalyzerService();
            var games = new Game[allScannedGames.Count];
            Parallel.For(0, allScannedGames.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
            {
                var game = allScannedGames[i];
                analyzer.AnalyzeGame(game);
                games[i] = game;
            });

            GameAnalyzerService.FlushCacheToDisk();

            var result = games.ToList();
            DebugWindow.Log($"[Scanner] Scan completed. Found {result.Count} games (UpscalerFilter={scanConfig.UpscalerFilter}).");
            return result;
        });
    }

    private List<Game> ScanCustomFolder(string rootFolder)
    {
        var games = new List<Game>();

        if (!Directory.Exists(rootFolder))
        {
            DebugWindow.Log($"[Scanner] Custom folder does not exist: {rootFolder}");
            return games;
        }

        try
        {
            // Get all subdirectories (game folders)
            var gameFolders = Directory.GetDirectories(rootFolder);

            foreach (var gameFolder in gameFolders)
            {
                try
                {
                    // Find all .exe files in this game folder (recursive, but limited depth)
                    var exeFiles = Directory.GetFiles(gameFolder, "*.exe", new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        MaxRecursionDepth = 3,
                        IgnoreInaccessible = true
                    });

                    foreach (var exePath in exeFiles)
                    {
                        // Use the cleaned game folder name as the display name
                        var rawFolderName = Path.GetFileName(gameFolder);
                        var gameName = CleanFolderName(rawFolderName);

                        // Skip common non-game executables
                        var exeName = Path.GetFileNameWithoutExtension(exePath).ToLower();
                        if (exeName.Contains("unins") || exeName.Contains("setup") ||
                            exeName.Contains("installer") || exeName.Contains("crash") ||
                            exeName.Contains("launcher") && !exeName.Contains("game"))
                        {
                            continue;
                        }

                        var game = new Game
                        {
                            Name = gameName,
                            ExecutablePath = exePath,
                            InstallPath = gameFolder,
                            Platform = GamePlatform.Custom,
                            // AppId uses raw folder name to uniquely identify the game (no double prefix)
                            AppId = rawFolderName
                        };

                        games.Add(game);
                        DebugWindow.Log($"[Scanner] Found custom game: {gameName} ({Path.GetFileName(exePath)})");

                        // Only take the first valid exe per game folder to avoid duplicates
                        break;
                    }
                }
                catch (Exception ex)
                {
                    DebugWindow.Log($"[Scanner] Error scanning game folder '{gameFolder}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            DebugWindow.Log($"[Scanner] Error accessing custom folder '{rootFolder}': {ex.Message}");
        }

        return games;
    }

    private static readonly System.Text.RegularExpressions.Regex SceneSuffixRegex =
        new(@"\s*-[A-Za-z0-9_.]+$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex YearSuffixRegex =
        new(@"\s*[\(\-]\s*\d{4}\s*[\)]?\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly string[] EditionNames = { "Deluxe", "Ultimate", "Gold", "GOTY", "Complete", "Enhanced", "Remastered", "Definitive" };
    private static readonly System.Text.RegularExpressions.Regex[] EditionRegexes =
        EditionNames.Select(p => new System.Text.RegularExpressions.Regex(
            $@"\s*-?\s*{p}\s*(Edition)?\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToArray();
    private static readonly System.Text.RegularExpressions.Regex PrefixRegex =
        new(@"^(Custom|Rune|Razor|PLAZA|CODEX|RELOADED|FLT|SKIDROW|CPY|PROPHET|HOODLUM|SiMPLEX)\s+",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Converts a raw scene/release folder name into a human-readable game display name.
    /// e.g. "Custom_Alan.Wake.2.Deluxe.Edition-InsaneRamZes" → "Alan Wake 2"
    /// </summary>
    public static string CleanFolderName(string folderName)
    {
        var cleaned = folderName;

        // Replace underscores and dots with spaces if used as word separators. Both are checked
        // against the original name: "Custom_Alan.Wake.2" mixes them, and replacing "_" first
        // used to add a space that then skipped the dots.
        if (!cleaned.Contains(' '))
            cleaned = cleaned.Replace('_', ' ').Replace('.', ' ');

        // Remove scene/release group suffixes like "-InsaneRamZes", "-CODEX", "-FLT", etc.
        cleaned = SceneSuffixRegex.Replace(cleaned, "");

        // Remove year suffixes like "(2024)", "- 2024"
        cleaned = YearSuffixRegex.Replace(cleaned, "");

        // Remove edition suffixes
        foreach (var editionRegex in EditionRegexes)
        {
            cleaned = editionRegex.Replace(cleaned, "");
        }

        // Remove scene/release group prefixes that appear before the real game name
        cleaned = PrefixRegex.Replace(cleaned, "");

        return cleaned.Trim();
    }
}
