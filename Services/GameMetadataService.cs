using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using OptiscalerClient.Views;
using OptiscalerClient.Models;

namespace OptiscalerClient.Services;

public class GameMetadataService
{
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private readonly HttpClient _httpClient;
    private readonly string _coversCachePath;
    private readonly ComponentManagementService? _componentService;

    public GameMetadataService(ComponentManagementService? componentService = null)
    {
        _httpClient = SharedHttpClient;
        _componentService = componentService;

        // Caching covers in AppData
        _coversCachePath = Path.Combine(AppPaths.GetAppDataRoot(), "Covers");
        Directory.CreateDirectory(_coversCachePath);
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("User-Agent", "OptiscalerClient/1.0");
        client.Timeout = TimeSpan.FromSeconds(10);
        return client;
    }

    /// <summary>
    /// Deletes the ".nocover" sentinel file for a game so it will be re-fetched on the next attempt.
    /// </summary>
    public void DeleteSentinel(string appIdKey)
    {
        try
        {
            var sentinelPath = Path.Combine(_coversCachePath, $"{SanitizeFileName(appIdKey)}.nocover");
            if (File.Exists(sentinelPath))
                File.Delete(sentinelPath);
        }
        catch (Exception ex)
        {
            DebugWindow.Log(() => $"[Cover] Failed to delete sentinel for '{appIdKey}': {ex.Message}");
        }
    }

    /// <summary>
    /// Searches for game cover art using multiple sources with fallback.
    /// Priority: 1) Cache, 2) Steam API (with AppId if available), 3) SteamGridDB
    /// </summary>
    public async Task<string?> FetchAndCacheCoverImageAsync(string gameName, string appIdKey)
    {
        string sanitized = SanitizeFileName(appIdKey);
        string localPath = Path.Combine(_coversCachePath, $"{sanitized}.jpg");
        string sentinelPath = Path.Combine(_coversCachePath, $"{sanitized}.nocover");

        // Already downloaded
        if (File.Exists(localPath))
        {
            DebugWindow.Log(() => $"[Cover] HIT cache: {gameName}");
            return localPath;
        }

        // Previously determined no cover exists — skip all network calls
        if (File.Exists(sentinelPath))
        {
            DebugWindow.Log(() => $"[Cover] HIT sentinel (no cover): {gameName}");
            return null;
        }

        var sw = Stopwatch.StartNew();
        DebugWindow.Log(() => $"[Cover] START fetching: \"{gameName}\" (key: {appIdKey})");

        string? result = null;
        int? triedSteamAppId = null;

        // Try 1: If appIdKey is a numeric Steam AppId, use it directly (fastest — 1 request)
        if (int.TryParse(appIdKey, out int steamAppId))
        {
            triedSteamAppId = steamAppId;
            DebugWindow.Log(() => $"[Cover]   [T+{sw.ElapsedMilliseconds}ms] Trying Steam AppId {steamAppId} directly...");
            result = await TryDownloadSteamCoverByAppId(steamAppId, localPath, gameName, sw);
            if (result != null)
            {
                DebugWindow.Log(() => $"[Cover] DONE in {sw.ElapsedMilliseconds}ms via AppId: \"{gameName}\"");
                return result;
            }
            DebugWindow.Log(() => $"[Cover]   [T+{sw.ElapsedMilliseconds}ms] AppId direct failed, falling back to search...");
        }

        // Try 2: Search Steam Store API by name (skip if it would just return the same AppId)
        DebugWindow.Log(() => $"[Cover]   [T+{sw.ElapsedMilliseconds}ms] Trying Steam name search...");
        result = await TryFetchFromSteamSearch(gameName, localPath, sw, skipAppId: triedSteamAppId);
        if (result != null)
        {
            DebugWindow.Log(() => $"[Cover] DONE in {sw.ElapsedMilliseconds}ms via Steam search: \"{gameName}\"");
            return result;
        }
        DebugWindow.Log(() => $"[Cover]   [T+{sw.ElapsedMilliseconds}ms] Steam search failed.");

        // Try 3: Fallback to SteamGridDB (only if API key configured)
        DebugWindow.Log(() => $"[Cover]   [T+{sw.ElapsedMilliseconds}ms] Trying SteamGridDB fallback...");
        result = await TryFetchFromSteamGridDB(gameName, localPath, sw);
        if (result != null)
        {
            DebugWindow.Log(() => $"[Cover] DONE in {sw.ElapsedMilliseconds}ms via SteamGridDB: \"{gameName}\"");
            return result;
        }

        DebugWindow.Log(() => $"[Cover] FAIL in {sw.ElapsedMilliseconds}ms — no cover found for: \"{gameName}\" — writing sentinel");
        try { await File.WriteAllBytesAsync(sentinelPath, Array.Empty<byte>()); }
        catch (Exception ex) { DebugWindow.Log(() => $"[Cover] Failed to write sentinel: {ex.Message}"); }

        return null;
    }

    // Ordered list of Steam image formats to try — standard-res first (smaller, faster)
    private static readonly string[] SteamImageTemplates = new[]
    {
        "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/library_600x900.jpg",
        "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/header.jpg",
        "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/capsule_616x353.jpg",
    };

    private async Task<string?> TryDownloadSteamCoverByAppId(int appId, string localPath, string gameName, Stopwatch? sw = null)
    {
        // Stage 1: try the best-quality URL first (single request — works for ~90% of Steam games)
        var primaryUrl = string.Format(SteamImageTemplates[0], appId);
        var primaryResult = await TryFetchImageBytesAsync(primaryUrl, sw, CancellationToken.None);
        if (primaryResult.bytes != null)
        {
            await File.WriteAllBytesAsync(localPath, primaryResult.bytes);
            DebugWindow.Log(() => $"[Cover]     OK {primaryResult.bytes.Length / 1024}KB — {primaryUrl}");
            return localPath;
        }

        // Stage 2: primary failed — fire remaining formats in parallel
        using var cts = new CancellationTokenSource();
        var fallbackTasks = SteamImageTemplates.Skip(1)
            .Select(template => TryFetchImageBytesAsync(string.Format(template, appId), sw, cts.Token))
            .ToList();

        while (fallbackTasks.Count > 0)
        {
            var completed = await Task.WhenAny(fallbackTasks);
            fallbackTasks.Remove(completed);

            (string url, byte[]? bytes) result;
            try { result = await completed; }
            catch (Exception ex)
            {
                DebugWindow.Log(() => $"[Cover] Fallback task failed: {ex.Message}");
                continue;
            }

            if (result.bytes != null)
            {
                cts.Cancel();
                await File.WriteAllBytesAsync(localPath, result.bytes);
                DebugWindow.Log(() => $"[Cover]     OK {result.bytes.Length / 1024}KB — {result.url}");
                return localPath;
            }
        }

        DebugWindow.Log(() => $"[Cover]     All CDN URLs failed for AppId {appId}");
        return null;
    }

    private async Task<(string url, byte[]? bytes)> TryFetchImageBytesAsync(string url, Stopwatch? sw, CancellationToken ct)
    {
        try
        {
            DebugWindow.Log(() => $"[Cover]     GET {url}");
            var t0 = sw?.ElapsedMilliseconds ?? 0;
            var response = await _httpClient.GetAsync(url, ct);
            var t1 = sw?.ElapsedMilliseconds ?? 0;

            if (!response.IsSuccessStatusCode)
            {
                DebugWindow.Log(() => $"[Cover]     {(int)response.StatusCode} in {t1 - t0}ms — {url}");
                return (url, null);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            var t2 = sw?.ElapsedMilliseconds ?? 0;

            if (bytes.Length < 5000)
            {
                DebugWindow.Log(() => $"[Cover]     200 but tiny ({bytes.Length}B) — skipping {url}");
                return (url, null);
            }

            DebugWindow.Log(() => $"[Cover]     {bytes.Length / 1024}KB in {t2 - t0}ms — {url}");
            return (url, bytes);
        }
        catch (OperationCanceledException)
        {
            return (url, null);
        }
        catch (Exception ex)
        {
            DebugWindow.Log(() => $"[Cover]     ERROR on {url}: {ex.GetType().Name}: {ex.Message}");
            return (url, null);
        }
    }

    private async Task<string?> TryFetchFromSteamSearch(string gameName, string localPath, Stopwatch? sw = null, int? skipAppId = null)
    {
        try
        {
            string cleanName = CleanGameName(gameName);
            string queryName = Uri.EscapeDataString(cleanName);
            string url = $"https://store.steampowered.com/api/storesearch/?term={queryName}&l=english&cc=US";

            DebugWindow.Log(() => $"[Cover]     GET {url}");
            var t0 = sw?.ElapsedMilliseconds ?? 0;
            var response = await _httpClient.GetAsync(url);
            var t1 = sw?.ElapsedMilliseconds ?? 0;

            if (!response.IsSuccessStatusCode)
            {
                DebugWindow.Log(() => $"[Cover]     Steam search {(int)response.StatusCode} in {t1 - t0}ms");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var t2 = sw?.ElapsedMilliseconds ?? 0;
            DebugWindow.Log(() => $"[Cover]     Steam search 200 in {t1 - t0}ms, body read in {t2 - t1}ms");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("total", out var totalEl) && totalEl.GetInt32() > 0)
            {
                if (root.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
                {
                    var bestMatch = FindBestMatch(items, cleanName);
                    if (bestMatch.HasValue && bestMatch.Value.TryGetProperty("id", out var idEl))
                    {
                        int actualAppId = idEl.GetInt32();
                        string matchedName = bestMatch.Value.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";

                        if (skipAppId.HasValue && actualAppId == skipAppId.Value)
                        {
                            DebugWindow.Log(() => $"[Cover]     Steam search matched same AppId {actualAppId} already tried — skipping CDN retry");
                            return null;
                        }

                        DebugWindow.Log(() => $"[Cover]     Steam search matched: \"{matchedName}\" (AppId {actualAppId})");
                        return await TryDownloadSteamCoverByAppId(actualAppId, localPath, matchedName, sw);
                    }
                }
            }
            else
            {
                DebugWindow.Log(() => $"[Cover]     Steam search returned 0 results for: \"{cleanName}\"");
            }
        }
        catch (Exception ex)
        {
            DebugWindow.Log(() => $"[Cover]     Steam search exception: {ex.GetType().Name}: {ex.Message}");
        }

        return null;
    }

    private async Task<string?> TryFetchFromSteamGridDB(string gameName, string localPath, Stopwatch? sw = null)
    {
        string? apiKey = _componentService?.Config?.SteamGridDBApiKey;

        if (string.IsNullOrEmpty(apiKey))
        {
            DebugWindow.Log(() => $"[Cover]   [T+{sw?.ElapsedMilliseconds ?? 0}ms] No SteamGridDB API key — skipping.");
            return null;
        }

        try
        {
            string cleanName = CleanGameName(gameName);
            string queryName = Uri.EscapeDataString(cleanName);
            string searchUrl = $"https://www.steamgriddb.com/api/v2/search/autocomplete/{queryName}";

            DebugWindow.Log(() => $"[Cover]     GET {searchUrl}");
            var t0 = sw?.ElapsedMilliseconds ?? 0;
            var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
            var response = await _httpClient.SendAsync(request);
            var t1 = sw?.ElapsedMilliseconds ?? 0;

            if (!response.IsSuccessStatusCode)
            {
                DebugWindow.Log(() => $"[Cover]     SteamGridDB search {(int)response.StatusCode} in {t1 - t0}ms");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            DebugWindow.Log(() => $"[Cover]     SteamGridDB search 200 in {t1 - t0}ms");
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
            {
                var firstGame = data[0];
                if (firstGame.TryGetProperty("id", out var gameId))
                {
                    int gridGameId = gameId.GetInt32();
                    // Request up to 10 static grids — we'll prefer JPEG over PNG client-side
                    string gridsUrl = $"https://www.steamgriddb.com/api/v2/grids/game/{gridGameId}?dimensions=600x900&types=static&limit=10";

                    DebugWindow.Log(() => $"[Cover]     GET {gridsUrl}");
                    var t2 = sw?.ElapsedMilliseconds ?? 0;
                    var gridsRequest = new HttpRequestMessage(HttpMethod.Get, gridsUrl);
                    gridsRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
                    var gridsResponse = await _httpClient.SendAsync(gridsRequest);
                    var t3 = sw?.ElapsedMilliseconds ?? 0;

                    if (gridsResponse.IsSuccessStatusCode)
                    {
                        var gridsJson = await gridsResponse.Content.ReadAsStringAsync();
                        DebugWindow.Log(() => $"[Cover]     SteamGridDB grids 200 in {t3 - t2}ms");
                        using var gridsDoc = JsonDocument.Parse(gridsJson);

                        if (gridsDoc.RootElement.TryGetProperty("data", out var grids) && grids.GetArrayLength() > 0)
                        {
                            // Prefer JPEG over PNG (smaller file size), then fall back to first result
                            var gridsList = grids.EnumerateArray().ToList();
                            var grid = gridsList
                                .Select(g => new
                                {
                                    el = g,
                                    url = g.TryGetProperty("url", out var u) ? u.GetString() ?? "" : ""
                                })
                                .OrderByDescending(x =>
                                    x.url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                    x.url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                                .First();

                            if (!string.IsNullOrEmpty(grid.url))
                            {
                                DebugWindow.Log(() => $"[Cover]     GET {grid.url}");
                                var t4 = sw?.ElapsedMilliseconds ?? 0;
                                var imgBytes = await _httpClient.GetByteArrayAsync(grid.url);
                                var t5 = sw?.ElapsedMilliseconds ?? 0;
                                await File.WriteAllBytesAsync(localPath, imgBytes);
                                DebugWindow.Log(() => $"[Cover]     OK {imgBytes.Length / 1024}KB in {t5 - t4}ms — SteamGridDB image ({Path.GetExtension(grid.url)})");
                                return localPath;
                            }
                        }
                    }
                    else
                    {
                        DebugWindow.Log(() => $"[Cover]     SteamGridDB grids {(int)gridsResponse.StatusCode} in {t3 - t2}ms");
                    }
                }
            }
            else
            {
                DebugWindow.Log(() => $"[Cover]     SteamGridDB returned 0 results for: \"{cleanName}\"");
            }
        }
        catch (Exception ex)
        {
            DebugWindow.Log(() => $"[Cover]     SteamGridDB exception: {ex.GetType().Name}: {ex.Message}");
        }

        return null;
    }

    private JsonElement? FindBestMatch(JsonElement items, string searchName)
    {
        var itemsList = items.EnumerateArray().ToList();

        // First try: exact match (case insensitive)
        foreach (var item in itemsList)
        {
            if (item.TryGetProperty("name", out var nameEl))
            {
                string itemName = nameEl.GetString() ?? "";
                if (itemName.Equals(searchName, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }
        }

        // Second try: starts with search name
        foreach (var item in itemsList)
        {
            if (item.TryGetProperty("name", out var nameEl))
            {
                string itemName = nameEl.GetString() ?? "";
                if (itemName.StartsWith(searchName, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }
        }

        // Fallback: return first item
        return itemsList.Count > 0 ? itemsList[0] : null;
    }

    private string CleanGameName(string gameName)
    {
        // Remove common suffixes and prefixes that might interfere with search
        var cleaned = gameName;

        // Remove year suffixes like "(2024)", "- 2024"
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s*[\(\-]\s*\d{4}\s*[\)]?\s*$", "");

        // Remove edition suffixes
        var editionPatterns = new[] { "Deluxe", "Ultimate", "Gold", "GOTY", "Complete", "Enhanced", "Remastered", "Definitive" };
        foreach (var pattern in editionPatterns)
        {
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, $@"\s*-?\s*{pattern}\s*(Edition)?\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return cleaned.Trim();
    }

    private string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", fileName.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
    }

    public async Task<string?> FetchCoverImageUrlAsync(string gameName)
    {
        // Legacy method if still used elsewhere
        try
        {
            string queryName = Uri.EscapeDataString(gameName);
            string url = $"https://store.steampowered.com/api/storesearch/?term={queryName}&l=english&cc=US";

            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var root = doc.RootElement;
            if (root.TryGetProperty("total", out var totalEl) && totalEl.GetInt32() > 0)
            {
                if (root.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
                {
                    var firstItem = items[0];
                    if (firstItem.TryGetProperty("id", out var idEl))
                    {
                        int appId = idEl.GetInt32();
                        return string.Format(SteamImageTemplates[0], appId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DebugWindow.Log(() => $"[Cover] SteamGridDB search error: {ex.Message}");
        }
        return null;
    }
}
