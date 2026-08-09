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
    /// Downloads, parses and caches the main table of the OptiScaler wiki's Compatibility List
    /// (per-game recommended upscaler inputs / OptiPatcher support / notes), refreshed once a
    /// day at app startup — same pattern as <see cref="ComponentManagementService"/>'s version
    /// caches, but reading raw wiki markdown instead of the GitHub releases API.
    /// </summary>
    public class CompatibilityListService
    {
        // Raw markdown source — a GitHub wiki page's raw content is served straight from a
        // static CDN host (not api.github.com), so it doesn't count against the GitHub REST API
        // rate limit used elsewhere in the app.
        private const string RawMarkdownUrl = "https://raw.githubusercontent.com/wiki/optiscaler/OptiScaler/Compatibility-List.md";

        /// <summary>Human-facing wiki page, used for the "view more info" link in the UI.</summary>
        public const string WikiUrl = "https://github.com/optiscaler/OptiScaler/wiki/Compatibility-List";

        private const int CooldownHours = 24;

        // Static so the in-memory cache survives across `new CompatibilityListService()` calls,
        // same convention as ComponentManagementService's release caches.
        private static CompatibilityListCache _cache = new();
        private static Dictionary<string, CompatibilityListEntry> _byNormalizedName = new();
        private static List<(CompatibilityListEntry Entry, HashSet<string> Tokens)> _tokenizedEntries = new();
        private static bool _loadedFromDisk;
        private static readonly object _lock = new();

        // Words too common to be meaningful for similarity scoring.
        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "and"
        };

        // Edition/re-release suffixes stripped before tokenizing, so e.g. "Silent Hill 2 Remake"
        // and "Silent Hill 2" tokenize the same way. "Demo" is deliberately NOT in this list —
        // a demo is often its own separate wiki entry with different compatibility notes, so
        // treating it as noise could match a game to the wrong (full-release) entry.
        private static readonly string[] EditionSuffixes =
        {
            "Deluxe", "Ultimate", "Gold", "GOTY", "Complete", "Enhanced",
            "Remastered", "Remake", "Definitive", "Standard", "Digital"
        };

        // Minimum Jaccard token-overlap score to accept a fuzzy match, and the minimum lead the
        // best candidate must have over the second-best one. Both tuned against real near-miss
        // pairs: "Resident Evil 9 Requiem" vs the wiki's "Resident Evil Requiem" scores ~0.75 and
        // is accepted; "Grand Theft Auto V" vs "Grand Theft Auto IV" scores exactly 0.6 and must
        // be rejected — hence 0.7, not a rounder-looking 0.6 or 0.65.
        private const double FuzzyMinScore = 0.7;
        private const double FuzzyMinMargin = 0.15;

        private readonly string _cacheFile;
        private readonly ComponentManagementService _componentService;

        public CompatibilityListService()
        {
            _cacheFile = Path.Combine(AppPaths.GetAppDataRoot(), "compatibility_list_cache.json");
            // Reuses ComponentManagementService's already-loaded (and shared/static) AppConfiguration
            // instead of re-implementing config load/save just for one cooldown timestamp.
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
        /// Looks up recommended config for a game by name. Tries an exact (normalized) match
        /// first, then falls back to token-overlap fuzzy matching for near-identical names that
        /// differ slightly between the local game title and the wiki's (e.g. "Resident Evil 9
        /// Requiem" vs "Resident Evil Requiem"). Purely local — never touches the network, safe
        /// to call synchronously from any window.
        /// </summary>
        public bool TryGetForGame(string? gameName, out CompatibilityListEntry? entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(gameName)) return false;

            if (_byNormalizedName.TryGetValue(NormalizeName(gameName), out entry))
                return true;

            return TryFuzzyMatch(gameName, out entry);
        }

        private bool TryFuzzyMatch(string gameName, out CompatibilityListEntry? entry)
        {
            entry = null;
            var queryTokens = Tokenize(gameName);
            if (queryTokens.Count == 0) return false;

            CompatibilityListEntry? best = null;
            double bestScore = 0;
            double secondBestScore = 0;

            foreach (var (candidateEntry, candidateTokens) in _tokenizedEntries)
            {
                var score = JaccardSimilarity(queryTokens, candidateTokens);
                if (score > bestScore)
                {
                    secondBestScore = bestScore;
                    bestScore = score;
                    best = candidateEntry;
                }
                else if (score > secondBestScore)
                {
                    secondBestScore = score;
                }
            }

            if (best == null || bestScore < FuzzyMinScore || (bestScore - secondBestScore) < FuzzyMinMargin)
                return false;

            entry = best;
            DebugWindow.Log($"[CompatList] Fuzzy-matched '{gameName}' -> '{best.GameName}' (score={bestScore:F2}, margin={(bestScore - secondBestScore):F2}).");
            return true;
        }

        private static HashSet<string> Tokenize(string name)
        {
            var cleaned = name;
            foreach (var suffix in EditionSuffixes)
                cleaned = Regex.Replace(cleaned, $@"\b{suffix}\b\s*(Edition)?", "", RegexOptions.IgnoreCase);

            return Regex.Matches(cleaned, @"[\p{L}\p{Nd}]+")
                .Select(m => m.Value.ToLowerInvariant())
                .Where(w => !StopWords.Contains(w))
                .ToHashSet();
        }

        private static double JaccardSimilarity(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 || b.Count == 0) return 0;
            int intersection = a.Count(b.Contains);
            int union = a.Count + b.Count - intersection;
            return union == 0 ? 0 : (double)intersection / union;
        }

        /// <summary>
        /// Refreshes the cache from the wiki if the cooldown has elapsed. Never throws — network
        /// or parsing failures are logged and the existing cache (if any) is kept as-is, since
        /// this runs unattended during app startup and must not be intrusive to the user.
        /// The cooldown is ignored whenever there's no usable cached data yet (first run, or a
        /// previous attempt that recorded the cooldown timestamp but never produced any entries)
        /// — otherwise a single failed attempt would leave the app without data for 24h.
        /// </summary>
        public async Task CheckForUpdatesAsync()
        {
            var config = _componentService.Config;
            var lastCheck = config.LastCompatListCheckTime;
            bool hasUsableCache = _cache.Entries.Count > 0;
            if (hasUsableCache && lastCheck.HasValue && (DateTime.UtcNow - lastCheck.Value).TotalHours < CooldownHours)
            {
                DebugWindow.Log($"[CompatList] Skipping refresh, last check was {(DateTime.UtcNow - lastCheck.Value):hh\\:mm\\:ss} ago.");
                return;
            }

            // Recorded before the request so a failed attempt doesn't retry on every launch.
            config.LastCompatListCheckTime = DateTime.UtcNow;
            _componentService.SaveConfiguration();

            try
            {
                var response = await GetWithRetryNoRateLimitAsync(() => NetworkService.GetHttpClient(), RawMarkdownUrl, maxRetries: 2, timeoutSeconds: 20);
                if (!response.IsSuccessStatusCode)
                {
                    DebugWindow.Log($"[CompatList] Fetch failed with HTTP {(int)response.StatusCode} — keeping existing cache.");
                    return;
                }

                var markdown = await response.Content.ReadAsStringAsync();
                var entries = ParseMarkdownTable(markdown);

                if (entries.Count == 0)
                {
                    var preview = markdown.Length > 120 ? markdown.Substring(0, 120) : markdown;
                    preview = preview.Replace("\n", "\\n").Replace("\r", "");
                    DebugWindow.Log($"[CompatList] Parsed 0 entries — HTTP {(int)response.StatusCode}, {markdown.Length} chars received, starts with: \"{preview}\". Assuming a transient/format issue, keeping existing cache.");
                    return;
                }

                _cache = new CompatibilityListCache { Entries = entries, LastUpdated = DateTime.UtcNow };
                SaveCache();
                RebuildLookup();
                DebugWindow.Log($"[CompatList] Refreshed: {entries.Count} entries.");
            }
            catch (Exception ex)
            {
                // Deliberately swallowed: this is a background refresh the user never asked for,
                // so a network hiccup must never surface a dialog or break app startup.
                DebugWindow.Log($"[CompatList] Refresh failed (will use existing cache): {ex.Message}");
            }
        }

        private static List<CompatibilityListEntry> ParseMarkdownTable(string markdown)
        {
            var entries = new List<CompatibilityListEntry>();
            var lines = markdown.Replace("\r\n", "\n").Split('\n');

            int i = 0;
            // Find the main table's header row (6 columns: Game, Compatibility, Upscaler Inputs,
            // OptiPatcher Support, Notes, Images). The wiki page also has a second, differently
            // shaped table further down ("Luma Unreal Engine") which is intentionally not parsed.
            //
            // Matched on the first two cells being exactly "Game" and "Compatibility..." rather
            // than a loose prefix check — the page also has an HTML-commented row template
            // ("| GAME NAME | ... |") earlier in the file that a plain "starts with '| Game'"
            // check would false-positive on, since "GAME NAME" also starts with "Game".
            while (i < lines.Length && !IsMainTableHeaderRow(lines[i]))
                i++;
            if (i >= lines.Length) return entries;

            i += 2; // skip header row + the "| --- | --- |" separator row

            for (; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (!line.StartsWith("|")) break; // blank line or next heading ends the table

                var cells = line.Split('|');
                // cells[0] and cells[^1] are the empty strings before/after the leading/trailing "|"
                if (cells.Length < 6) continue;

                var gameCell = cells[1].Trim();
                if (gameCell.Length == 0) continue;

                try
                {
                    entries.Add(new CompatibilityListEntry
                    {
                        GameName = StripMarkdownLink(gameCell),
                        Status = ParseStatus(cells[2]),
                        UpscalerInputs = CleanText(cells[3]),
                        OptiPatcherSupported = cells[4].Trim().Length > 0,
                        Notes = CleanText(cells[5])
                    });
                }
                catch (Exception ex)
                {
                    // A single malformed row (hand-edited wiki) shouldn't abort the whole parse.
                    DebugWindow.Log($"[CompatList] Skipped malformed row: {ex.Message}");
                }
            }

            return entries;
        }

        private static bool IsMainTableHeaderRow(string line)
        {
            var cells = line.Trim().Split('|');
            if (cells.Length < 3) return false;

            return string.Equals(cells[1].Trim(), "Game", StringComparison.OrdinalIgnoreCase)
                && cells[2].Trim().StartsWith("Compatibility", StringComparison.OrdinalIgnoreCase);
        }

        private static CompatibilityStatus ParseStatus(string cell)
        {
            if (cell.Contains("✅")) return CompatibilityStatus.Compatible;
            if (cell.Contains("❌")) return CompatibilityStatus.NotCompatible;
            if (cell.Contains("💥")) return CompatibilityStatus.SingleOsOnly;
            return CompatibilityStatus.Unconfirmed;
        }

        private static string StripMarkdownLink(string cell)
        {
            // "[Game Name](slug)" -> "Game Name"; plain "Game Name" passes through unchanged.
            return Regex.Replace(cell.Trim(), @"\[([^\]]*)\]\([^\)]*\)", "$1").Trim();
        }

        private static string CleanText(string cell)
        {
            var text = cell.Trim();
            if (text.Length == 0) return "";

            text = Regex.Replace(text, @"\[([^\]]*)\]\([^\)]*\)", "$1"); // [text](url) -> text
            text = Regex.Replace(text, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
            text = text.Replace("**", "").Replace("`", "");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text;
        }

        private static string NormalizeName(string name)
        {
            var normalized = name.Trim().Replace("™", "").Replace("®", "").Replace("©", "");
            normalized = Regex.Replace(normalized, @"\s+", " ");
            return normalized.ToLowerInvariant();
        }

        private static void RebuildLookup()
        {
            var map = new Dictionary<string, CompatibilityListEntry>();
            var tokenized = new List<(CompatibilityListEntry, HashSet<string>)>();

            foreach (var entry in _cache.Entries)
            {
                var key = NormalizeName(entry.GameName);
                if (key.Length == 0) continue;

                if (!map.ContainsKey(key))
                {
                    map[key] = entry;
                }
                else
                {
                    DebugWindow.Log($"[CompatList] Duplicate normalized name '{key}' — keeping first occurrence.");
                }

                tokenized.Add((entry, Tokenize(entry.GameName)));
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
                    _cache = JsonSerializer.Deserialize(json, OptimizerContext.Default.CompatibilityListCache) ?? new();
                    RebuildLookup();
                    DebugWindow.Log($"[CompatList] Loaded {_cache.Entries.Count} entries from local cache.");
                }
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[CompatList] Failed to load local cache: {ex.Message}");
            }
        }

        private void SaveCache()
        {
            try
            {
                var json = JsonSerializer.Serialize(_cache, OptimizerContext.Default.CompatibilityListCache);
                File.WriteAllText(_cacheFile, json);
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[CompatList] Failed to save local cache: {ex.Message}");
            }
        }
    }
}
