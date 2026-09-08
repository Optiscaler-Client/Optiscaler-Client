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
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using OptiscalerClient.Helpers;
using OptiscalerClient.Models;
using OptiscalerClient.Views;
using static OptiscalerClient.Helpers.HttpRetryHelper;

namespace OptiscalerClient.Services
{
    /// <summary>
    /// Downloads, parses and caches the RenoDX project's "Mods" wiki page — a table listing every
    /// game with a RenoDX addon, and (when the maintainer publishes one) a "Snapshot" badge linking
    /// directly to the .addon64/.addon32 download. That link IS the machine-readable manifest this
    /// feature needs — no GitHub Releases API involved, and no fixed owner/repo: each maintainer
    /// hosts their own build (clshortfuse, mqhaji, steve161803, ...), confirmed 2026-09 via direct
    /// fetch of the page. Same lifecycle as CompatibilityListService: static in-memory cache + JSON
    /// on disk, 24h cooldown, background refresh that never throws into app startup.
    /// </summary>
    public class RenodxModsService
    {
        private const string RawMarkdownUrl = "https://raw.githubusercontent.com/wiki/clshortfuse/renodx/Mods.md";

        /// <summary>Human-facing wiki page — shown to the user when Auto couldn't resolve an addon
        /// automatically, so they can look it up and add it manually via Local Versions.</summary>
        public const string WikiUrl = "https://github.com/clshortfuse/renodx/wiki/Mods";

        private const int CooldownHours = 24;
        private const int CacheSchemaVersion = 1;

        private static RenodxModsCache _cache = new();
        private static Dictionary<string, RenodxModEntry> _byNormalizedName = new();
        private static List<(RenodxModEntry Entry, HashSet<string> Tokens)> _tokenizedEntries = new();
        private static bool _loadedFromDisk;
        private static readonly object _lock = new();
        private static int _refreshInProgress;

        public static bool IsRefreshInProgress => System.Threading.Volatile.Read(ref _refreshInProgress) != 0;
        public static event EventHandler? RefreshCompleted;

        private static readonly Regex SnapshotLinkRegex = new(@"\[!\[Snapshot\]\([^)]*\)\]\(([^)]+)\)", RegexOptions.Compiled);
        private static readonly Regex NexusLinkRegex = new(@"\[!\[Nexus Mods\]\([^)]*\)\]\(([^)]+)\)", RegexOptions.Compiled);

        private readonly string _cacheFile;
        private readonly ComponentManagementService _componentService;

        public RenodxModsService()
        {
            _cacheFile = Path.Combine(AppPaths.GetAppDataRoot(), "renodx_mods_cache.json");
            _componentService = new ComponentManagementService();

            lock (_lock)
            {
                if (!_loadedFromDisk)
                {
                    LoadCache();
                    _loadedFromDisk = true;
                }
            }
        }

        /// <summary>
        /// Looks up a game's RenoDX entry by name. Exact (normalized) match first, then the same
        /// fuzzy token-overlap matching CompatibilityListService uses (see GameNameMatcher) for
        /// near-identical names. Purely local — never touches the network.
        /// </summary>
        public bool TryGetForGame(string? gameName, out RenodxModEntry? entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(gameName)) return false;

            if (_byNormalizedName.TryGetValue(GameNameMatcher.Normalize(gameName), out entry))
                return true;

            if (GameNameMatcher.TryFuzzyMatch(gameName, _tokenizedEntries, out var fuzzyMatch) && fuzzyMatch != null)
            {
                entry = fuzzyMatch;
                DebugWindow.Log($"[RenodxMods] Fuzzy-matched '{gameName}' -> '{fuzzyMatch.GameName}'.");
                return true;
            }

            return false;
        }

        /// <summary>Refreshes the cache from the wiki if the cooldown has elapsed. Never throws —
        /// mirrors CompatibilityListService.CheckForUpdatesAsync exactly (see its comments for why
        /// the cooldown is bypassed on unusable/missing cache).</summary>
        public async Task CheckForUpdatesAsync()
        {
            var config = _componentService.Config;
            var lastCheck = config.LastRenodxModsCheckTime;
            bool hasUsableCache = _cache.Entries.Count > 0 && _cache.SchemaVersion >= CacheSchemaVersion;
            if (hasUsableCache && lastCheck.HasValue && (DateTime.UtcNow - lastCheck.Value).TotalHours < CooldownHours)
                return;

            if (System.Threading.Interlocked.CompareExchange(ref _refreshInProgress, 1, 0) != 0)
                return;

            try
            {
                config.LastRenodxModsCheckTime = DateTime.UtcNow;
                _componentService.SaveConfiguration();

                var response = await GetWithRetryNoRateLimitAsync(() => NetworkService.GetHttpClient(), RawMarkdownUrl, maxRetries: 2, timeoutSeconds: 20);
                if (!response.IsSuccessStatusCode)
                {
                    DebugWindow.Log($"[RenodxMods] Fetch failed with HTTP {(int)response.StatusCode} — keeping existing cache.");
                    return;
                }

                var markdown = await response.Content.ReadAsStringAsync();
                var entries = ParseMarkdownTable(markdown);

                if (entries.Count == 0)
                {
                    DebugWindow.Log("[RenodxMods] Parsed 0 entries — assuming a transient/format issue, keeping existing cache.");
                    return;
                }

                _cache = new RenodxModsCache { Entries = entries, LastUpdated = DateTime.UtcNow, SchemaVersion = CacheSchemaVersion };
                SaveCache();
                RebuildLookup();
                DebugWindow.Log($"[RenodxMods] Refreshed: {entries.Count} entries.");
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[RenodxMods] Refresh failed (will use existing cache): {ex.Message}");
            }
            finally
            {
                System.Threading.Volatile.Write(ref _refreshInProgress, 0);
                RefreshCompleted?.Invoke(null, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Parses the "| Game | Maintainer | Links | Status |" table. Confirmed 2026-09 via direct
        /// fetch: the Game column is plain text (no markdown link, unlike OptiScaler's own
        /// Compatibility List), and Links holds 0-2 badge links (Nexus and/or Snapshot, in either
        /// order, separated by "&middot;") — extracted by matching the exact alt text of each badge
        /// image rather than assuming a fixed owner/repo, since every maintainer hosts their own
        /// Snapshot build.
        /// </summary>
        private static List<RenodxModEntry> ParseMarkdownTable(string markdown)
        {
            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            var headerIndex = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                var cells = lines[i].Trim().Split('|');
                if (cells.Length < 4) continue;
                // Confirmed 2026-09 via direct fetch: the column is literally "Name", not "Game" —
                // an earlier research pass (via an AI web-summarizer, not a direct fetch) guessed
                // "Game" and was wrong, which silently emptied the whole parsed list (every row
                // failed to match, not just one game) since this header was never found.
                if (string.Equals(cells[1].Trim(), "Name", StringComparison.OrdinalIgnoreCase) &&
                    cells[2].Trim().StartsWith("Maintainer", StringComparison.OrdinalIgnoreCase))
                {
                    headerIndex = i;
                    break;
                }
            }
            if (headerIndex < 0) return new List<RenodxModEntry>();

            var entries = new List<RenodxModEntry>();
            for (int i = headerIndex + 2; i < lines.Length; i++) // +2 skips the header and its "|---|" separator row
            {
                var line = lines[i].Trim();
                if (!line.StartsWith("|")) break;

                var cells = line.Split('|');
                if (cells.Length < 4) continue;

                // Almost always plain text, but a handful of rows wrap the name in a markdown link
                // (e.g. "[Ori and the Blind Forest: Definitive Edition](https://github.com/.../discussions/223)")
                // — strip that down to just the visible text so it doesn't poison name matching.
                var gameName = Regex.Replace(cells[1].Trim(), @"\[([^\]]*)\]\([^)]*\)", "$1").Trim();
                if (gameName.Length == 0) continue;

                var linksCell = cells[3];
                var snapshotMatch = SnapshotLinkRegex.Match(linksCell);
                var nexusMatch = NexusLinkRegex.Match(linksCell);

                entries.Add(new RenodxModEntry
                {
                    GameName = gameName,
                    SnapshotUrl = snapshotMatch.Success ? snapshotMatch.Groups[1].Value.Trim() : null,
                    NexusUrl = nexusMatch.Success ? nexusMatch.Groups[1].Value.Trim() : null
                });
            }

            return entries;
        }

        private static void RebuildLookup()
        {
            var map = new Dictionary<string, RenodxModEntry>();
            var tokenized = new List<(RenodxModEntry, HashSet<string>)>();

            foreach (var entry in _cache.Entries)
            {
                var key = GameNameMatcher.Normalize(entry.GameName);
                if (key.Length == 0) continue;
                if (!map.ContainsKey(key)) map[key] = entry;
                tokenized.Add((entry, GameNameMatcher.Tokenize(entry.GameName)));
            }

            _byNormalizedName = map;
            _tokenizedEntries = tokenized;
        }

        private void LoadCache()
        {
            try
            {
                if (File.Exists(_cacheFile))
                {
                    var json = File.ReadAllText(_cacheFile);
                    _cache = JsonSerializer.Deserialize(json, OptimizerContext.Default.RenodxModsCache) ?? new();
                    RebuildLookup();
                    DebugWindow.Log($"[RenodxMods] Loaded {_cache.Entries.Count} entries from local cache.");
                }
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[RenodxMods] Failed to load local cache: {ex.Message}");
            }
        }

        private void SaveCache()
        {
            try
            {
                var json = JsonSerializer.Serialize(_cache, OptimizerContext.Default.RenodxModsCache);
                File.WriteAllText(_cacheFile, json);
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[RenodxMods] Failed to save local cache: {ex.Message}");
            }
        }
    }
}
