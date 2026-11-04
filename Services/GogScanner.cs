using Microsoft.Win32;
using OptiscalerClient.Models;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;

namespace OptiscalerClient.Services;

[SupportedOSPlatform("windows")]

public class GogScanner : IGameScanner
{
    private const string REGISTRY_PATH = @"SOFTWARE\WOW6432Node\GOG.com\Games";

    public Task<List<Game>> ScanAsync(CancellationToken cancellationToken = default)
    => Task.Run(() => Scan(cancellationToken), cancellationToken);

    private List<Game> Scan(CancellationToken cancellationToken = default)
    {
        var games = new List<Game>();

        if (!OperatingSystem.IsWindows())
            return games;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var gamesKey = baseKey.OpenSubKey(REGISTRY_PATH);

            if (gamesKey == null)
                return games;

            foreach (var subKeyName in gamesKey.GetSubKeyNames())
            {
                using var gameKey = gamesKey.OpenSubKey(subKeyName);
                if (gameKey == null) continue;

                var gameName = gameKey.GetValue("gameName") as string;
                var path = gameKey.GetValue("path") as string;
                var gameId = gameKey.GetValue("gameID") as string ?? subKeyName;

                if (!string.IsNullOrEmpty(gameName) && !string.IsNullOrEmpty(path))
                {
                    if (Directory.Exists(path))
                    {
                        games.Add(new Game
                        {
                            AppId = gameId,
                            Name = gameName,
                            InstallPath = path,
                            Platform = GamePlatform.GOG
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GOG] Error scanning registry: {ex.Message}");
        }

        return games;
    }
}
