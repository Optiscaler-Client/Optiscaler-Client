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
using System.Globalization;
using System.IO;
using System.Linq;
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

        // Bump whenever CompatibilityListEntry gains a field worth re-fetching for (see
        // CompatibilityListCache.SchemaVersion). A cache saved by an older build deserializes
        // with SchemaVersion 0, which CheckForUpdatesAsync treats as unusable so it refreshes
        // immediately instead of waiting out the normal 24h cooldown on stale/incomplete data.
        private const int CacheSchemaVersion = 2; // 2: added CompatibilityListEntry.WikiPageSlug

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

        // Individual game wiki pages are only ever fetched lazily (when the user opens Manage
        // Game for that specific title) rather than eagerly for the whole list - see
        // GetGameWikiDetailsAsync. There's no way to know from the outside when a given page was
        // last edited, so this uses the same 24h cadence as the main list's own cooldown rather
        // than guessing at a longer one.
        private static readonly TimeSpan GameWikiDetailsCooldown = TimeSpan.FromHours(CooldownHours);

        // Only these fields are automated - the rest of an individual page (OS, GPU, Reported By,
        // and especially the free-form Known Issues/Notes prose) varies too much between
        // contributors to parse reliably, and is left for the user to read on the actual wiki
        // page instead. Two spellings for FG are both common in practice (confirmed by sampling
        // the live wiki 2026-08-18: 88 pages use "FG Inputs", 107 use "FG-Settings").
        private static readonly Dictionary<string, string> WikiFieldAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Last Tested Version"] = "LastTestedVersion",
            ["Filename"] = "Filename",
            ["Upscaler Inputs"] = "UpscalerInputs",
            ["FG Inputs"] = "FgInputs",
            ["FG-Settings"] = "FgInputs",
            ["FG Settings"] = "FgInputs",
            ["Known Issues"] = "KnownIssues",
        };

        private readonly string _cacheFile;
        private readonly string _gameWikiCacheFile;
        private readonly ComponentManagementService _componentService;

        private static GameWikiDetailsCache _gameWikiCache = new();
        private static bool _gameWikiLoadedFromDisk;

        public CompatibilityListService()
        {
            _cacheFile = Path.Combine(AppPaths.GetAppDataRoot(), "compatibility_list_cache.json");
            _gameWikiCacheFile = Path.Combine(AppPaths.GetAppDataRoot(), "game_wiki_details_cache.json");
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
                if (!_gameWikiLoadedFromDisk)
                {
                    LoadGameWikiCache();
                    _gameWikiLoadedFromDisk = true;
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

            cleaned = StripApostrophes(cleaned);
            cleaned = SplitCamelCase(cleaned);
            cleaned = RemoveDiacritics(cleaned);

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
        /// The cooldown is ignored whenever there's no usable cached data yet (first run, a
        /// previous attempt that recorded the cooldown timestamp but never produced any entries,
        /// or a cache saved by an older build that predates the current CacheSchemaVersion) —
        /// otherwise a single failed attempt, or a schema upgrade, would leave the app sitting on
        /// stale/incomplete data for up to 24h with no way to recover short of the user manually
        /// clearing the cache file.
        /// </summary>
        public async Task CheckForUpdatesAsync()
        {
            var config = _componentService.Config;
            var lastCheck = config.LastCompatListCheckTime;
            bool hasUsableCache = _cache.Entries.Count > 0 && _cache.SchemaVersion >= CacheSchemaVersion;
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

                _cache = new CompatibilityListCache { Entries = entries, LastUpdated = DateTime.UtcNow, SchemaVersion = CacheSchemaVersion };
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

        /// <summary>
        /// Lazily fetches and parses a game's individual wiki page (only the handful of fields
        /// listed in <see cref="WikiFieldAliases"/> plus a Known Issues count - see GameWikiDetails).
        /// Only called on demand, from Manage Game for the one game being opened - never during
        /// the bulk daily refresh, so this can't flood the wiki with requests for the whole list.
        /// Individual pages are served from raw.githubusercontent.com, same as the main list, so
        /// this doesn't touch the GitHub REST API's rate limit either. Falls back to a previously
        /// cached (possibly stale) result rather than null on a transient failure, and never throws.
        /// </summary>
        public async Task<GameWikiDetails?> GetGameWikiDetailsAsync(CompatibilityListEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry?.WikiPageSlug)) return null;
            var slug = entry.WikiPageSlug;

            lock (_lock)
            {
                if (_gameWikiCache.BySlug.TryGetValue(slug, out var fresh)
                    && (DateTime.UtcNow - fresh.FetchedUtc) < GameWikiDetailsCooldown)
                {
                    return fresh.Details;
                }
            }

            var pageUrl = $"https://github.com/optiscaler/OptiScaler/wiki/{slug}";
            var rawUrl = $"https://raw.githubusercontent.com/wiki/optiscaler/OptiScaler/{slug}.asciidoc";

            try
            {
                var response = await GetWithRetryNoRateLimitAsync(() => NetworkService.GetHttpClient(), rawUrl, maxRetries: 1, timeoutSeconds: 15);
                if (!response.IsSuccessStatusCode)
                {
                    DebugWindow.Log($"[CompatList] No individual wiki page for '{slug}' (HTTP {(int)response.StatusCode}).");
                    return GetCachedGameWikiDetailsOrNull(slug);
                }

                var asciidoc = await response.Content.ReadAsStringAsync();
                var details = ParseGameWikiPage(asciidoc, pageUrl);
                if (details == null)
                {
                    DebugWindow.Log($"[CompatList] Could not parse individual wiki page for '{slug}'.");
                    return GetCachedGameWikiDetailsOrNull(slug);
                }

                lock (_lock)
                {
                    _gameWikiCache.BySlug[slug] = new GameWikiDetailsCacheEntry { Details = details, FetchedUtc = DateTime.UtcNow };
                    SaveGameWikiCache();
                }
                return details;
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[CompatList] Failed to fetch individual wiki page for '{slug}': {ex.Message}");
                return GetCachedGameWikiDetailsOrNull(slug);
            }
        }

        /// <summary>
        /// Returns whatever is cached for this game's wiki page right now — even if the cooldown
        /// has expired — without ever touching the network. Null only if this slug has never been
        /// fetched before. Lets callers show existing data immediately while GetGameWikiDetailsAsync
        /// silently checks for something newer in the background instead of blocking on it (see
        /// ManageGameWindow.PopulateCompatibilitySidebar/PopulateWikiDetailsAsync).
        /// </summary>
        public GameWikiDetails? GetCachedGameWikiDetails(CompatibilityListEntry? entry)
        {
            if (string.IsNullOrWhiteSpace(entry?.WikiPageSlug)) return null;
            return GetCachedGameWikiDetailsOrNull(entry.WikiPageSlug);
        }

        private static GameWikiDetails? GetCachedGameWikiDetailsOrNull(string slug)
        {
            lock (_lock)
            {
                return _gameWikiCache.BySlug.TryGetValue(slug, out var stale) ? stale.Details : null;
            }
        }

        /// <summary>
        /// Parses the small set of automated fields out of an individual wiki page's AsciiDoc
        /// table (format confirmed 2026-08-18 against CL-Template.asciidoc and several live pages).
        /// Cell values are found by locating each "|**Label**" header line and taking everything
        /// up to the next one; that block is either a single "|value" line or an "a|" block of
        /// "* item" bullets (nested "**" sub-bullets are skipped for these fields - real pages
        /// only use them under Known Issues/Notes, never under the short fields parsed here).
        /// Returns null only if the page doesn't look like this template at all (e.g. a 404's
        /// HTML error page slipped through) - a page that parses but has none of our fields still
        /// returns an (empty) GameWikiDetails, since that's a legitimate outcome for a sparse entry.
        /// </summary>
        private static GameWikiDetails? ParseGameWikiPage(string asciidoc, string pageUrl)
        {
            var normalized = asciidoc.Replace("\r\n", "\n");
            var labelMatches = Regex.Matches(normalized, @"^\|\*\*([^*]+)\*\*\s*$", RegexOptions.Multiline);
            if (labelMatches.Count == 0) return null;

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < labelMatches.Count; i++)
            {
                var label = labelMatches[i].Groups[1].Value.Trim();
                if (!WikiFieldAliases.TryGetValue(label, out var key)) continue;

                var start = labelMatches[i].Index + labelMatches[i].Length;
                var end = (i + 1 < labelMatches.Count) ? labelMatches[i + 1].Index : normalized.Length;
                var block = normalized.Substring(start, end - start);

                fields[key] = key == "KnownIssues"
                    ? CountTopLevelBullets(block).ToString(CultureInfo.InvariantCulture)
                    : ExtractSingleFieldValue(block);
            }

            fields.TryGetValue("KnownIssues", out var knownIssuesStr);
            int.TryParse(knownIssuesStr, out var knownIssuesCount);

            return new GameWikiDetails
            {
                LastTestedVersion = fields.GetValueOrDefault("LastTestedVersion", ""),
                Filename = fields.GetValueOrDefault("Filename", ""),
                UpscalerInputs = fields.GetValueOrDefault("UpscalerInputs", ""),
                FgInputs = fields.GetValueOrDefault("FgInputs", ""),
                KnownIssuesCount = knownIssuesCount,
                PageUrl = pageUrl
            };
        }

        private static string ExtractSingleFieldValue(string block)
        {
            var contentLines = new List<string>();
            foreach (var raw in block.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                {
                    // The block always starts with the newline that ended the "|**Label**" line
                    // itself, so the first blank line must be skipped rather than treated as the
                    // end of the cell - only a blank line AFTER real content ends it.
                    if (contentLines.Count > 0) break;
                    continue;
                }
                if (line == "|===") break;             // ran into the table's closing marker
                if (line == "a|") continue;            // block-content marker, carries no text itself
                if (line.StartsWith("**")) continue;   // nested sub-bullet, too detailed for these fields
                if (line.StartsWith("|")) line = line.Substring(1).Trim();
                else if (line.StartsWith("*")) line = line.Substring(1).Trim();
                if (line.Length > 0) contentLines.Add(line);
            }

            return CleanAsciiDocInline(string.Join(", ", contentLines));
        }

        private static int CountTopLevelBullets(string block)
        {
            int count = 0;
            foreach (var raw in block.Split('\n'))
            {
                var line = raw.Trim();
                if (line == "|===") break;
                if (line.StartsWith("* ") || line == "*") count++;
            }
            return count;
        }

        private static string CleanAsciiDocInline(string text)
        {
            // Deprecated/broken options are struck through rather than removed from the wiki
            // (e.g. "DLSS, XeSS, +++<s>FSR3</s>+++"). Dropping the whole segment, instead of just
            // unwrapping it to plain text, avoids presenting a crossed-out option as if it worked.
            text = Regex.Replace(text, @"\+\+\+\s*<s>.*?</s>\s*\+\+\+", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            // AsciiDoc external link: https://example.com[Label] -> Label
            text = Regex.Replace(text, @"https?://\S+?\[([^\]]*)\]", "$1");
            // Bold/italic/code markers - not meaningful in these short technical fields.
            text = text.Replace("*", "").Replace("_", "").Replace("`", "");
            // A dropped strikethrough segment can leave "a, , b" or a trailing/leading comma behind.
            text = Regex.Replace(text, @"\s*,\s*,\s*", ", ");
            text = Regex.Replace(text, @"^,\s*|,\s*$", "");
            return Regex.Replace(text, @"\s+", " ").Trim();
        }

        private void LoadGameWikiCache()
        {
            try
            {
                if (File.Exists(_gameWikiCacheFile))
                {
                    var json = File.ReadAllText(_gameWikiCacheFile);
                    _gameWikiCache = JsonSerializer.Deserialize(json, OptimizerContext.Default.GameWikiDetailsCache) ?? new();
                    DebugWindow.Log($"[CompatList] Loaded {_gameWikiCache.BySlug.Count} cached game wiki page(s) from disk.");
                }
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[CompatList] Failed to load game wiki details cache: {ex.Message}");
            }
        }

        private void SaveGameWikiCache()
        {
            try
            {
                var json = JsonSerializer.Serialize(_gameWikiCache, OptimizerContext.Default.GameWikiDetailsCache);
                File.WriteAllText(_gameWikiCacheFile, json);
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[CompatList] Failed to save game wiki details cache: {ex.Message}");
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
                    var (gameName, wikiSlug) = ParseGameCell(gameCell);
                    entries.Add(new CompatibilityListEntry
                    {
                        GameName = gameName,
                        WikiPageSlug = wikiSlug,
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

        /// <summary>
        /// Splits the Game column's "[Game Name](slug)" markdown link into display name and wiki
        /// page slug. Confirmed 2026-08-18 against the live wiki: 206 of 207 rows link with a bare
        /// relative slug (e.g. "Hogwarts-Legacy"); the one exception used a full URL to the same
        /// wiki instead, hence normalizing anything after "/wiki/" rather than assuming bare slugs
        /// only. A cell with no link at all yields an empty slug - that game just has no individual
        /// page to look up, which callers already treat as "nothing to fetch," not an error.
        /// </summary>
        private static (string Name, string Slug) ParseGameCell(string cell)
        {
            var match = Regex.Match(cell.Trim(), @"^\[([^\]]*)\]\(([^\)]*)\)$");
            if (!match.Success)
                return (cell.Trim(), "");

            var name = match.Groups[1].Value.Trim();
            var href = match.Groups[2].Value.Trim();

            const string wikiMarker = "/wiki/";
            var idx = href.IndexOf(wikiMarker, StringComparison.OrdinalIgnoreCase);
            var slug = idx >= 0 ? href.Substring(idx + wikiMarker.Length) : href;

            return (name, slug.Trim());
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
            normalized = StripApostrophes(normalized);
            normalized = SplitCamelCase(normalized);
            normalized = RemoveDiacritics(normalized);
            // ASCII hyphen-minus plus the Unicode hyphen/dash variants a wiki title might use
            // instead (e.g. "Spider‐Man") — turned into a space so a hyphenated wiki name
            // and an unpunctuated/spaced local one normalize to the same word sequence.
            normalized = Regex.Replace(normalized, "[-‐‑‒–—―−]", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            return normalized.ToLowerInvariant();
        }

        /// <summary>
        /// Strips accents/diacritics (e.g. "Ragnarök" -> "Ragnarok") via Unicode NFD decomposition
        /// + removing combining marks, so names that only differ by accented characters between
        /// the local game title and the wiki's still match (e.g. reported 2026-08-18: "God of War
        /// Ragnarök" wasn't found because the wiki lists it as "God of War Ragnarok"). True
        /// distinct letters that merely look similar (e.g. "ø", "æ") don't decompose this way and
        /// are intentionally left alone rather than guessed at.
        /// </summary>
        private static string RemoveDiacritics(string text)
        {
            var decomposed = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        // Straight, curly, and grave apostrophe/quote variants a title's possessive might use
        // (e.g. wiki "Marvel's Spider-Man 2" vs a locally typed "Marvels Spider-Man 2", reported
        // 2026-08-22). Removed entirely rather than treated as a word boundary, so "Marvel's" and
        // "Marvels" reduce to the same "marvels" instead of the apostrophe'd side leaving a
        // spurious extra "s" token that never matches anything on the other side.
        private static readonly char[] ApostropheChars = { '\'', '’', '‘', '`', '´' };

        private static string StripApostrophes(string text)
        {
            foreach (var ch in ApostropheChars)
                text = text.Replace(ch.ToString(), "");
            return text;
        }

        /// <summary>
        /// Inserts a space at each lowercase/digit -> uppercase transition (e.g. "SpiderMan" ->
        /// "Spider Man"), so a locally concatenated camelCase title normalizes/tokenizes the same
        /// as the wiki's hyphenated or spaced form of the same words (same 2026-08-22 report: local
        /// "Marvels SpiderMan 2" vs wiki "Marvel's Spider-Man 2" — the hyphen already acts as a
        /// token boundary on the wiki's side, "SpiderMan" needs this to split the same way on the
        /// local side). All-caps runs (acronyms like "NBA2K") have no such transition and are left
        /// alone rather than guessed at.
        /// </summary>
        private static string SplitCamelCase(string text)
        {
            return Regex.Replace(text, @"(?<=[\p{Ll}\p{Nd}])(?=\p{Lu})", " ");
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
