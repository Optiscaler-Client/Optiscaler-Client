using Microsoft.Win32;
using OptiscalerClient.Models;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;

namespace OptiscalerClient.Services;

[SupportedOSPlatform("windows")]

public class UbisoftScanner : IGameScanner
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
                // Uplay games usually start with "Uplay Install "
                if (!entry.SubKeyName.StartsWith("Uplay Install ", StringComparison.OrdinalIgnoreCase))
                    continue;

                var gameName = entry.DisplayName;
                var path = entry.InstallLocation;

                if (!string.IsNullOrEmpty(gameName) && !string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    games.Add(new Game
                    {
                        AppId = entry.SubKeyName.Replace("Uplay Install ", "").Trim(),
                        Name = gameName,
                        InstallPath = path,
                        Platform = GamePlatform.Ubisoft
                    });
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Ubisoft] Error scanning registry: {ex.Message}");
        }

        return games;
    }
}
