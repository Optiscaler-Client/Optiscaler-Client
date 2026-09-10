using OptiscalerClient.Models;
using System.IO;
using System.Text.Json;

namespace OptiscalerClient.Services;

public class EpicScanner : IGameScanner
{
    private const string MANIFESTS_REL_PATH = @"Epic\EpicGamesLauncher\Data\Manifests";

    public List<Game> Scan()
    {
        var games = new List<Game>();
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var manifestsPath = Path.Combine(programData, MANIFESTS_REL_PATH);

        if (!Directory.Exists(manifestsPath))
            return games;

        var itemFiles = Directory.GetFiles(manifestsPath, "*.item");

        foreach (var file in itemFiles)
        {
            try
            {
                var json = File.ReadAllText(file);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Check categories
                bool isGame = false;
                if (root.TryGetProperty("AppCategories", out var categoriesElement))
                {
                    foreach (var category in categoriesElement.EnumerateArray())
                    {
                        if (category.GetString() == "games")
                        {
                            isGame = true;
                            break;
                        }
                    }
                }

                if (!isGame) continue;

                // Check if main game app (not DLC)
                var appName = root.GetProperty("AppName").GetString();
                var mainGameAppName = root.GetProperty("MainGameAppName").GetString();

                if (appName != mainGameAppName) continue;

                var displayName = root.GetProperty("DisplayName").GetString();
                var installLocation = root.GetProperty("InstallLocation").GetString();
                var catalogItemId = root.GetProperty("CatalogItemId").GetString();

                if (string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(installLocation)) continue;

                if (Directory.Exists(installLocation))
                {
                    games.Add(new Game
                    {
                        Name = displayName,
                        InstallPath = installLocation,
                        Platform = GamePlatform.Epic,
                        AppId = catalogItemId ?? "Unknown"
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Epic] Error parsing manifest '{file}': {ex.Message}");
            }
        }

        return games;
    }
}
