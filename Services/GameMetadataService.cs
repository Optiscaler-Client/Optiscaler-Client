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
    private readonly string _iconsCachePath;
    private readonly ComponentManagementService? _componentService;
    private readonly GameIconCoverService _iconCovers = new();
    private readonly LauncherCoverLocator _launcherCovers = new();

    // Sentinels carry the version of the source chain that gave up on the game; bumping it whenever a
    // source is added retries those "no cover" verdicts once. Legacy sentinels are empty files.
    private static readonly byte[] SentinelVersion = [3];

    /// <summary>Suffix of the covers generated from the game's .exe icon when no real cover exists.</summary>
    public const string IconCoverSuffix = ".icon.png";

    /// <summary>True for a cover generated from the exe icon: shown, but still worth replacing with
    /// real cover art whenever covers are retried.</summary>
    public static bool IsIconCover(string? coverPath) =>
        coverPath?.EndsWith(IconCoverSuffix, StringComparison.OrdinalIgnoreCase) == true;

    public GameMetadataService(ComponentManagementService? componentService = null)
    {
        _httpClient = SharedHttpClient;
        _componentService = componentService;

        // Caching covers in AppData
        _coversCachePath = Path.Combine(AppPaths.GetAppDataRoot(), "Covers");
        Directory.CreateDirectory(_coversCachePath);
        _iconsCachePath = Path.Combine(AppPaths.GetAppDataRoot(), "Icons");
        Directory.CreateDirectory(_iconsCachePath);
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
    /// Checks if a ".nocover" sentinel file exists for a game.
    /// </summary>
    public bool HasSentinel(string appIdKey)
    {
        var sentinelPath = Path.Combine(_coversCachePath, $"{SanitizeFileName(appIdKey)}.nocover");
        return IsCurrentSentinel(sentinelPath);
    }

    private static bool IsCurrentSentinel(string sentinelPath)
    {
        try { return File.Exists(sentinelPath) && File.ReadAllBytes(sentinelPath).AsSpan().SequenceEqual(SentinelVersion); }
        catch { return false; }
    }

    /// <summary>
    /// True when the exe-icon fallback already ran for a game and found no usable icon.
    /// </summary>
    public bool HasNoIconMarker(string appIdKey) =>
        File.Exists(NoIconMarkerPath(SanitizeFileName(appIdKey)));

    private string IconPath(string sanitizedKey) => Path.Combine(_iconsCachePath, $"{sanitizedKey}.png");
    private string NoIconMarkerPath(string sanitizedKey) => Path.Combine(_iconsCachePath, $"{sanitizedKey}.noicon");

    /// <summary>
    /// Cached PNG of the icon embedded in the game's executable (extracted on first call), or null
    /// when the game has none. Used as the list-mode thumbnail.
    /// </summary>
    public Task<string?> GetOrExtractIconAsync(Game game)
    {
        var key = !string.IsNullOrEmpty(game.AppId) ? game.AppId : game.Name;
        if (string.IsNullOrEmpty(key)) return Task.FromResult<string?>(null);
        var sanitized = SanitizeFileName(key);
        return Task.Run(() => _iconCovers.GetOrExtractIcon(game, IconPath(sanitized), NoIconMarkerPath(sanitized)));
    }

    /// <summary>
    /// Deletes all ".nocover" sentinel files in the cache directory.
    /// </summary>
    public void DeleteAllSentinels()
    {
        try
        {
            var files = Directory.GetFiles(_coversCachePath, "*.nocover");
            foreach (var file in files)
            {
                File.Delete(file);
            }
            DebugWindow.Log(() => $"[Cover] Deleted {files.Length} sentinels.");
        }
        catch (Exception ex)
        {
            DebugWindow.Log(() => $"[Cover] Failed to delete sentinels: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes both the cached image and the sentinel file for a specific app key.
    /// </summary>
    public void DeleteCoverCache(string appIdKey)
    {
        try
        {
            var sanitized = SanitizeFileName(appIdKey);
            var imagePath = Path.Combine(_coversCachePath, $"{sanitized}.jpg");
            var sentinelPath = Path.Combine(_coversCachePath, $"{sanitized}.nocover");
            var iconCoverPath = Path.Combine(_coversCachePath, sanitized + IconCoverSuffix);
            var iconPath = IconPath(sanitized);
            var noIconPath = NoIconMarkerPath(sanitized);

            if (File.Exists(imagePath)) File.Delete(imagePath);
            if (File.Exists(sentinelPath)) File.Delete(sentinelPath);
            if (File.Exists(iconCoverPath)) File.Delete(iconCoverPath);
            if (File.Exists(iconPath)) File.Delete(iconPath);
            if (File.Exists(noIconPath)) File.Delete(noIconPath);
        }
        catch (Exception ex)
        {
            DebugWindow.Log(() => $"[Cover] Failed to delete cover cache for '{appIdKey}': {ex.Message}");
        }
    }

    /// <summary>
    /// Searches for game cover art using multiple sources with fallback.
    /// Priority: 1) Cache, 2) Launcher's local cover cache, 3) Steam CDN by AppId, 4) GOG GamesDB by
    /// store ID, 5) Steam/GOG/Lutris search by name (then fallback name), 6) SteamGridDB (API key only).
    /// When every source fails and <paramref name="game"/> is given, falls back to a cover generated
    /// from the game's executable icon (see <see cref="GameIconCoverService"/>).
    /// </summary>
    public async Task<string?> FetchAndCacheCoverImageAsync(string gameName, string appIdKey, string? fallbackName = null, Game? game = null)
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
        if (IsCurrentSentinel(sentinelPath))
        {
            DebugWindow.Log(() => $"[Cover] HIT sentinel (no cover): {gameName}");
            return await GetIconCoverAsync(game, sanitized);
        }

        var sw = Stopwatch.StartNew();
        DebugWindow.Log(() => $"[Cover] START fetching: \"{gameName}\" (key: {appIdKey})");

        string? result = null;
        int? triedSteamAppId = null;

        // Try 1: Cover the game's own launcher already cached locally (no API, mostly no network)
        if (game != null)
        {
            result = await TryFetchFromLauncherCache(game, localPath, sw);
            if (result != null)
            {
                DebugWindow.Log(() => $"[Cover] DONE in {sw.ElapsedMilliseconds}ms via launcher cache: \"{gameName}\"");
                return result;
            }
        }

        // Try 2: If appIdKey is a numeric Steam AppId, use it directly (fastest — 1 request)
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
            DebugWindow.Log(() => $"[Cover]   [T+{sw.ElapsedMilliseconds}ms] AppId direct failed, falling back...");
        }

        // Try 3: GOG GamesDB resolves the store's own ID (Steam/GOG/Epic/Ubisoft) to a portrait cover
        if (game != null && !string.IsNullOrWhiteSpace(game.AppId))
        {
            result = await TryFetchFromGamesDb(game.Platform, game.AppId, localPath, sw);
            if (result != null)
            {
                DebugWindow.Log(() => $"[Cover] DONE in {sw.ElapsedMilliseconds}ms via GamesDB: \"{gameName}\"");
                return result;
            }
        }

        var names = new[] { gameName, fallbackName }
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Try 4: Searches by name (Steam, GOG, then Lutris.net for other stores' exclusives), primary name first
        foreach (var name in names)
        {
            DebugWindow.Log(() => $"[Cover]   [T+{sw.ElapsedMilliseconds}ms] Trying store search for: \"{name}\"");
            result = await TryFetchFromSteamSearch(name, localPath, sw, skipAppId: triedSteamAppId)
                     ?? await TryFetchFromGogSearch(name, localPath, sw)
                     ?? await TryFetchFromLutrisSearch(name, localPath, sw);
            if (result != null)
            {
                DebugWindow.Log(() => $"[Cover] DONE in {sw.ElapsedMilliseconds}ms via store search: \"{name}\"");
                return result;
            }
        }

        // Try 5: SteamGridDB by name (only if API key configured)
        foreach (var name in names)
        {
            DebugWindow.Log(() => $"[Cover]   [T+{sw.ElapsedMilliseconds}ms] Trying SteamGridDB for: \"{name}\"");
            result = await TryFetchFromSteamGridDB(name, localPath, sw);
            if (result != null)
            {
                DebugWindow.Log(() => $"[Cover] DONE in {sw.ElapsedMilliseconds}ms via SteamGridDB: \"{name}\"");
                return result;
            }
        }

        DebugWindow.Log(() => $"[Cover] FAIL in {sw.ElapsedMilliseconds}ms — no cover found for: \"{gameName}\" — writing sentinel");
        try { await File.WriteAllBytesAsync(sentinelPath, SentinelVersion); }
        catch (Exception ex) { DebugWindow.Log(() => $"[Cover] Failed to write sentinel: {ex.Message}"); }

        return await GetIconCoverAsync(game, sanitized);
    }

    /// <summary>
    /// Cover generated from the game's executable icon (created on demand), or null when the game has
    /// no usable icon. Used as the placeholder after a cover is deleted.
    /// </summary>
    public Task<string?> GetOrCreateIconCoverAsync(Game game, string appIdKey) =>
        GetIconCoverAsync(game, SanitizeFileName(appIdKey));

    private Task<string?> GetIconCoverAsync(Game? game, string sanitizedKey)
    {
        if (game == null) return Task.FromResult<string?>(null);
        var outputPath = Path.Combine(_coversCachePath, sanitizedKey + IconCoverSuffix);
        return Task.Run(() => _iconCovers.GetOrCreateCover(game, outputPath, IconPath(sanitizedKey), NoIconMarkerPath(sanitizedKey)));
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

        // Stage 1b: newer apps only publish portrait art under hashed paths — ask the store for them
        foreach (var assetUrl in await GetSteamCapsuleUrlsAsync(appId, sw))
        {
            var assetResult = await TryFetchImageBytesAsync(assetUrl, sw, CancellationToken.None);
            if (assetResult.bytes != null)
            {
                await File.WriteAllBytesAsync(localPath, assetResult.bytes);
                DebugWindow.Log(() => $"[Cover]     OK {assetResult.bytes.Length / 1024}KB — {assetUrl}");
                return localPath;
            }
        }

        // Stage 2: no portrait art — fire remaining (landscape) formats in parallel
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

    /// <summary>
    /// Portrait capsule URLs (2x first) from Steam's keyless IStoreBrowseService, which knows the
    /// hashed asset paths that the fixed CDN templates miss for recently updated apps.
    /// </summary>
    private async Task<List<string>> GetSteamCapsuleUrlsAsync(int appId, Stopwatch? sw)
    {
        var urls = new List<string>();
        try
        {
            var input = $"{{\"ids\":[{{\"appid\":{appId}}}],\"context\":{{\"language\":\"english\",\"country_code\":\"US\"}},\"data_request\":{{\"include_assets\":true}}}}";
            var url = $"https://api.steampowered.com/IStoreBrowseService/GetItems/v1?input_json={Uri.EscapeDataString(input)}";
            DebugWindow.Log(() => $"[Cover]     GET Steam GetItems for AppId {appId}");
            var t0 = sw?.ElapsedMilliseconds ?? 0;
            using var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                DebugWindow.Log(() => $"[Cover]     Steam GetItems {(int)response.StatusCode}");
                return urls;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("response", out var resp)
                || !resp.TryGetProperty("store_items", out var items)
                || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0
                || !items[0].TryGetProperty("assets", out var assets)
                || !assets.TryGetProperty("asset_url_format", out var formatEl))
                return urls;

            var format = formatEl.GetString();
            if (string.IsNullOrEmpty(format)) return urls;

            foreach (var key in new[] { "library_capsule_2x", "library_capsule" })
            {
                if (assets.TryGetProperty(key, out var fileEl) && fileEl.GetString() is { Length: > 0 } file)
                    urls.Add("https://shared.akamai.steamstatic.com/store_item_assets/" + format.Replace("${FILENAME}", file));
            }
            DebugWindow.Log(() => $"[Cover]     Steam GetItems: {urls.Count} capsule(s) in {(sw?.ElapsedMilliseconds ?? 0) - t0}ms");
        }
        catch (Exception ex)
        {
            DebugWindow.Log(() => $"[Cover]     Steam GetItems exception: {ex.GetType().Name}: {ex.Message}");
        }
        return urls;
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

    private async Task<string?> TryFetchFromLauncherCache(Game game, string localPath, Stopwatch sw)
    {
        foreach (var candidate in _launcherCovers.FindCoverCandidates(game))
        {
            try
            {
                if (candidate.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    DebugWindow.Log(() => $"[Cover]   [T+{sw.ElapsedMilliseconds}ms] Launcher metadata cover URL found");
                    var (_, bytes) = await TryFetchImageBytesAsync(candidate, sw, CancellationToken.None);
                    if (bytes == null) continue;
                    await File.WriteAllBytesAsync(localPath, bytes);
                }
                else
                {
                    DebugWindow.Log(() => $"[Cover]   [T+{sw.ElapsedMilliseconds}ms] Launcher cache file: {candidate}");
                    File.Copy(candidate, localPath, overwrite: true);
                }
                return localPath;
            }
            catch (Exception ex)
            {
                DebugWindow.Log(() => $"[Cover]     Launcher cover '{candidate}' failed: {ex.Message}");
            }
        }
        return null;
    }

    // GamesDB (GOG Galaxy's cross-store database) platform ids for the stores whose scanners keep
    // the native store ID in Game.AppId.
    private static string? GamesDbPlatform(GamePlatform platform) => platform switch
    {
        GamePlatform.Steam => "steam",
        GamePlatform.GOG => "gog",
        GamePlatform.Epic => "epic",
        GamePlatform.Ubisoft => "uplay",
        _ => null
    };

    private async Task<string?> TryFetchFromGamesDb(GamePlatform platform, string storeId, string localPath, Stopwatch sw)
    {
        var platformId = GamesDbPlatform(platform);
        if (platformId == null) return null;

        try
        {
            var url = $"https://gamesdb.gog.com/platforms/{platformId}/external_releases/{Uri.EscapeDataString(storeId)}";
            DebugWindow.Log(() => $"[Cover]   [T+{sw.ElapsedMilliseconds}ms] GET {url}");
            using var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                DebugWindow.Log(() => $"[Cover]     GamesDB {(int)response.StatusCode}");
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("game", out var gameEl)
                || !gameEl.TryGetProperty("vertical_cover", out var cover)
                || !cover.TryGetProperty("url_format", out var formatEl)
                || formatEl.GetString() is not { Length: > 0 } format)
            {
                DebugWindow.Log(() => "[Cover]     GamesDB has no vertical cover");
                return null;
            }

            // Empty formatter = original resolution.
            var imageUrl = format.Replace("{formatter}", "").Replace("{ext}", "jpg");
            var (_, bytes) = await TryFetchImageBytesAsync(imageUrl, sw, CancellationToken.None);
            if (bytes == null) return null;

            await File.WriteAllBytesAsync(localPath, bytes);
            return localPath;
        }
        catch (Exception ex)
        {
            DebugWindow.Log(() => $"[Cover]     GamesDB exception: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> TryFetchFromGogSearch(string gameName, string localPath, Stopwatch? sw = null)
    {
        try
        {
            string cleanName = CleanGameName(gameName);
            string url = $"https://catalog.gog.com/v1/catalog?limit=20&query=like:{Uri.EscapeDataString(cleanName)}&productType=in:game,pack&order=desc:score";

            DebugWindow.Log(() => $"[Cover]     GET {url}");
            using var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                DebugWindow.Log(() => $"[Cover]     GOG search {(int)response.StatusCode}");
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("products", out var products) || products.GetArrayLength() == 0)
            {
                DebugWindow.Log(() => $"[Cover]     GOG search returned 0 results for: \"{cleanName}\"");
                return null;
            }

            var bestMatch = FindBestMatch(products, cleanName, "title");
            if (bestMatch == null
                || !bestMatch.Value.TryGetProperty("coverVertical", out var coverEl)
                || coverEl.GetString() is not { Length: > 0 } coverUrl)
            {
                DebugWindow.Log(() => $"[Cover]     GOG search: no matching title for \"{cleanName}\"");
                return null;
            }

            DebugWindow.Log(() => $"[Cover]     GOG search matched: \"{bestMatch.Value.GetProperty("title").GetString()}\"");
            var (_, bytes) = await TryFetchImageBytesAsync(coverUrl, sw, CancellationToken.None);
            if (bytes == null) return null;

            await File.WriteAllBytesAsync(localPath, bytes);
            return localPath;
        }
        catch (Exception ex)
        {
            DebugWindow.Log(() => $"[Cover]     GOG search exception: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Lutris.net's public game database mirrors IGDB covers for every store (Epic/EA/Ubisoft
    /// exclusives included), so it catches titles that neither the Steam nor GOG catalogs carry.
    /// </summary>
    private async Task<string?> TryFetchFromLutrisSearch(string gameName, string localPath, Stopwatch? sw = null)
    {
        try
        {
            string cleanName = CleanGameName(gameName);
            string url = $"https://lutris.net/api/games?search={Uri.EscapeDataString(cleanName)}";

            DebugWindow.Log(() => $"[Cover]     GET {url}");
            using var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                DebugWindow.Log(() => $"[Cover]     Lutris search {(int)response.StatusCode}");
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            {
                DebugWindow.Log(() => $"[Cover]     Lutris search returned 0 results for: \"{cleanName}\"");
                return null;
            }

            var bestMatch = FindBestMatch(results, cleanName);
            if (bestMatch == null
                || !bestMatch.Value.TryGetProperty("coverart", out var coverEl)
                || coverEl.ValueKind != JsonValueKind.String
                || coverEl.GetString() is not { Length: > 0 } coverUrl)
            {
                DebugWindow.Log(() => $"[Cover]     Lutris search: no matching title with cover for \"{cleanName}\"");
                return null;
            }

            DebugWindow.Log(() => $"[Cover]     Lutris search matched: \"{bestMatch.Value.GetProperty("name").GetString()}\"");

            // Lutris serves IGDB's 264x352 "cover_big"; IGDB itself has the 2x (528x704) variant.
            var igdb = System.Text.RegularExpressions.Regex.Match(coverUrl, @"/media/igdb/cover_big/(\w+\.jpg)$");
            if (igdb.Success)
            {
                var hiRes = await TryFetchImageBytesAsync($"https://images.igdb.com/igdb/image/upload/t_cover_big_2x/{igdb.Groups[1].Value}", sw, CancellationToken.None);
                if (hiRes.bytes != null)
                {
                    await File.WriteAllBytesAsync(localPath, hiRes.bytes);
                    return localPath;
                }
            }

            var (_, bytes) = await TryFetchImageBytesAsync(coverUrl, sw, CancellationToken.None);
            if (bytes == null) return null;

            await File.WriteAllBytesAsync(localPath, bytes);
            return localPath;
        }
        catch (Exception ex)
        {
            DebugWindow.Log(() => $"[Cover]     Lutris search exception: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
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

    private static JsonElement? FindBestMatch(JsonElement items, string searchName, string nameProperty = "name")
    {
        var target = NormalizeTitle(searchName);
        if (target.Length == 0) return null;

        var candidates = items.EnumerateArray()
            .Select(item => (item, name: item.TryGetProperty(nameProperty, out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? NormalizeTitle(nameEl.GetString() ?? "")
                : ""))
            .Where(c => c.name.Length > 0)
            .ToList();

        // Exact match, then "starts with" ("The Witcher 3" -> "The Witcher 3: Wild Hunt"),
        // then "contains" ("Wild Hunt" -> "The Witcher 3: Wild Hunt").
        foreach (var predicate in new Func<string, bool>[]
                 {
                     n => n == target,
                     n => n.StartsWith(target + " ", StringComparison.Ordinal),
                     n => n.Contains(target, StringComparison.Ordinal),
                 })
        {
            foreach (var (item, name) in candidates)
                if (predicate(name)) return item;
        }

        // Never fall back to the first result: store searches often return unrelated games when the
        // exact one is missing (e.g. "Alan Wake 2" returns a Beat Saber DLC); failing lets the
        // remaining sources try.
        return null;
    }

    /// <summary>
    /// Lowercases and strips trademark symbols, punctuation and accents so that "DOOM Eternal™",
    /// "Doom: Eternal" and "doom eternal" compare equal.
    /// </summary>
    private static string NormalizeTitle(string title)
    {
        var decomposed = title.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (ch == '\'' || ch == '\u2019') continue; // "Assassin's" == "Assassins"
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private string CleanGameName(string gameName)
    {
        // Remove common suffixes and prefixes that might interfere with search
        var cleaned = gameName;

        // Replace dots and underscores with spaces
        cleaned = cleaned.Replace(".", " ").Replace("_", " ");

        // Remove scene release group suffixes like "-InsaneRamZes", "-RUNE", "-FLT", "-TENOKE", "-DODI"
        // This regex removes a dash followed by a single alphanumeric word at the very end.
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"-[A-Za-z0-9]+$", "");

        // Remove year suffixes like "(2024)", "- 2024"
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s*[\(\-]\s*\d{4}\s*[\)]?\s*$", "");

        // Remove edition suffixes
        var editionPatterns = new[] { "Deluxe", "Ultimate", "Gold", "GOTY", "Complete", "Enhanced", "Remastered", "Definitive" };
        foreach (var pattern in editionPatterns)
        {
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, $@"\s*-?\s*{pattern}\s*(Edition)?\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // Clean up multiple spaces
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ");

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
