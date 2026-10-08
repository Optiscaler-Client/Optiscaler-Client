using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OptiscalerClient.Models;
using OptiscalerClient.Views;

namespace OptiscalerClient.Services;

/// <summary>
/// Finds cover art that the game's own launcher already has: files in its local image cache, or
/// cover URLs from its local library metadata. No API keys and (for files) no network involved.
/// Candidates are either absolute file paths or http(s) URLs, best first.
/// </summary>
public class LauncherCoverLocator
{
    // Epic launcher's catalog cache (Windows): base64-encoded JSON array of catalog items.
    private const string EpicCatalogCacheRelPath = @"Epic\EpicGamesLauncher\Data\Catalog\catcache.bin";

    private readonly Lazy<Dictionary<string, string>> _heroicCovers = new(LoadHeroicCovers);
    private readonly Lazy<Dictionary<string, string>> _epicCovers = new(LoadEpicCatalogCovers);
    private readonly Lazy<string?> _steamPath = new(SteamScanner.FindSteamInstallPath);

    public IEnumerable<string> FindCoverCandidates(Game game)
    {
        if (string.IsNullOrWhiteSpace(game.AppId))
            yield break;

        switch (game.Platform)
        {
            case GamePlatform.Steam:
                foreach (var file in FindSteamLibraryCacheCovers(game.AppId))
                    yield return file;
                break;

            case GamePlatform.Lutris:
                foreach (var file in FindLutrisCovers(game.AppId))
                    yield return file;
                break;

            case GamePlatform.Epic:
            case GamePlatform.GOG:
                // Heroic keys games by Legendary appName / GOG product id; the Epic launcher by catalogItemId.
                if (_heroicCovers.Value.TryGetValue(game.AppId, out var heroicUrl))
                    yield return heroicUrl;
                if (game.Platform == GamePlatform.Epic && _epicCovers.Value.TryGetValue(game.AppId, out var epicUrl))
                    yield return epicUrl;
                break;
        }
    }

    private IEnumerable<string> FindSteamLibraryCacheCovers(string appId)
    {
        var steamPath = _steamPath.Value;
        if (steamPath == null || !int.TryParse(appId, out _))
            yield break;

        var cacheDir = Path.Combine(steamPath, "appcache", "librarycache");

        // Current layout: librarycache/<appid>/[<hash>/]library_600x900[_2x].jpg
        var appDir = Path.Combine(cacheDir, appId);
        if (Directory.Exists(appDir))
        {
            string[] files;
            try { files = Directory.GetFiles(appDir, "library_600x900*.jpg", SearchOption.AllDirectories); }
            catch (Exception ex)
            {
                DebugWindow.Log(() => $"[Cover] Steam librarycache read failed for {appId}: {ex.Message}");
                files = [];
            }
            // Prefer the 2x variant (higher resolution) when present.
            foreach (var file in files.OrderByDescending(f => f.EndsWith("_2x.jpg", StringComparison.OrdinalIgnoreCase)))
                yield return file;
        }

        // Legacy flat layout: librarycache/<appid>_library_600x900.jpg
        var legacy = Path.Combine(cacheDir, $"{appId}_library_600x900.jpg");
        if (File.Exists(legacy))
            yield return legacy;
    }

    private static IEnumerable<string> FindLutrisCovers(string configSlug)
    {
        // Config files are "<slug>-<id>.yml" while cover art is stored as "<slug>.jpg".
        var slug = Regex.Replace(configSlug, @"-\d+$", "");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var coverDirs = LutrisScanner.GetLutrisConfigDirectories()
            .Select(d => Path.Combine(d, "coverart"))
            .Append(Path.Combine(home, ".cache", "lutris", "coverart"));

        foreach (var dir in coverDirs)
        {
            foreach (var ext in new[] { ".jpg", ".png" })
            {
                var file = Path.Combine(dir, slug + ext);
                if (File.Exists(file))
                    yield return file;
            }
        }
    }

    /// <summary>Maps Heroic app ids (Epic appName / GOG product id) to their portrait cover URL.</summary>
    private static Dictionary<string, string> LoadHeroicCovers()
    {
        var covers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] libraryFiles = ["gog_library.json", "legendary_library.json", "nile_library.json"];

        foreach (var dataPath in HeroicScanner.GetHeroicDataPaths())
        {
            foreach (var fileName in libraryFiles)
            {
                var path = Path.Combine(dataPath, "store_cache", fileName);
                if (!File.Exists(path)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    CollectHeroicCovers(doc.RootElement, covers);
                }
                catch (Exception ex)
                {
                    DebugWindow.Log(() => $"[Cover] Failed to read Heroic library '{path}': {ex.Message}");
                }
            }
        }
        return covers;
    }

    // The library files nest the game array under different keys depending on the store/version,
    // so walk the tree and pick any object carrying both an app name and art_square.
    private static void CollectHeroicCovers(JsonElement element, Dictionary<string, string> covers)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("art_square", out var art) && art.ValueKind == JsonValueKind.String
                    && (TryGetString(element, "app_name", out var appName) || TryGetString(element, "appName", out appName)))
                {
                    var url = art.GetString();
                    if (!string.IsNullOrWhiteSpace(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        covers.TryAdd(appName, url);
                    return;
                }
                foreach (var prop in element.EnumerateObject())
                    CollectHeroicCovers(prop.Value, covers);
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectHeroicCovers(item, covers);
                break;
        }
    }

    /// <summary>Maps Epic catalogItemIds to their "DieselGameBoxTall" (portrait) image URL.</summary>
    private static Dictionary<string, string> LoadEpicCatalogCovers()
    {
        var covers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
            return covers;

        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), EpicCatalogCacheRelPath);
        if (!File.Exists(path))
            return covers;

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(File.ReadAllText(path).Trim()));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return covers;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!TryGetString(item, "id", out var id) || !item.TryGetProperty("keyImages", out var images)
                    || images.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var image in images.EnumerateArray())
                {
                    if (TryGetString(image, "type", out var type) && type == "DieselGameBoxTall"
                        && TryGetString(image, "url", out var url))
                    {
                        covers.TryAdd(id, url);
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DebugWindow.Log(() => $"[Cover] Failed to read Epic catalog cache: {ex.Message}");
        }
        return covers;
    }

    private static bool TryGetString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.String)
            return false;
        value = el.GetString() ?? string.Empty;
        return value.Length > 0;
    }
}
