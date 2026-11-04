using Microsoft.Win32;
using OptiscalerClient.Models;
using System.IO;
using System.Runtime.Versioning;

namespace OptiscalerClient.Services;

[SupportedOSPlatform("windows")]
public class BattleNetScanner : IGameScanner
{
    public Task<List<Game>> ScanAsync(CancellationToken cancellationToken = default)
    => Task.Run(() => Scan(cancellationToken), cancellationToken);

    private List<Game> Scan(CancellationToken cancellationToken = default)
    {
        var games = new List<Game>();

        try
        {
            foreach (var entry in WindowsUninstallRegistryHelper.GetEntries())
            {
                if (string.IsNullOrEmpty(entry.Publisher) ||
                    !entry.Publisher.Contains("Blizzard Entertainment", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var gameName = entry.DisplayName;
                var path = entry.InstallLocation;

                // Some Battle.net games might be "Battle.net" launcher itself
                if (gameName == "Battle.net" || gameName == "Blizzard Battle.net App") continue;

                if (!string.IsNullOrEmpty(gameName) && !string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    games.Add(new Game
                    {
                        AppId = entry.SubKeyName,
                        Name = gameName.Replace(" (PTR)", ""),
                        InstallPath = path,
                        Platform = GamePlatform.BattleNet
                    });
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BattleNet] Error scanning registry: {ex.Message}");
        }

        return games;
    }
}
