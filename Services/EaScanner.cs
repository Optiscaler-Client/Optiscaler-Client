using Microsoft.Win32;
using OptiscalerClient.Models;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;

namespace OptiscalerClient.Services;

[SupportedOSPlatform("windows")]
public class EaScanner : IGameScanner
{
    private readonly string[] REGISTRY_PATHS = new[]
    {
        @"SOFTWARE\WOW6432Node\Electronic Arts\EA Games",
        @"SOFTWARE\Electronic Arts\EA Games"
    };

    public Task<List<Game>> ScanAsync(CancellationToken cancellationToken = default)
    => Task.Run(() => Scan(cancellationToken), cancellationToken);

    private List<Game> Scan(CancellationToken cancellationToken = default)
    {
        var games = new List<Game>();

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (var basePath in REGISTRY_PATHS)
            {
                using var eaGamesKey = baseKey.OpenSubKey(basePath);
                if (eaGamesKey == null) continue;

                foreach (var subKeyName in eaGamesKey.GetSubKeyNames())
                {
                    using var gameKey = eaGamesKey.OpenSubKey(subKeyName);
                    if (gameKey == null) continue;

                    var gameName = gameKey.GetValue("DisplayName") as string ?? subKeyName;
                    var path = gameKey.GetValue("Install Dir") as string;

                    if (!string.IsNullOrEmpty(gameName) && !string.IsNullOrEmpty(path))
                    {
                        if (Directory.Exists(path))
                        {
                            games.Add(new Game
                            {
                                AppId = subKeyName,
                                Name = gameName,
                                InstallPath = path,
                                Platform = GamePlatform.EA
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EA] Error scanning registry: {ex.Message}");
        }

        return games;
    }
}
