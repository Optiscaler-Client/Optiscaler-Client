using Microsoft.Win32;
using OptiscalerClient.Models;
using System.IO;
using System.Runtime.Versioning;

namespace OptiscalerClient.Services;

[SupportedOSPlatform("windows")]
public class BattleNetScanner : IGameScanner
{
    private static readonly string[] UNINSTALL_PATHS = new[]    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    };

    public Task<List<Game>> ScanAsync(CancellationToken cancellationToken = default)
    => Task.Run(() => Scan(cancellationToken), cancellationToken);

    private List<Game> Scan(CancellationToken cancellationToken = default)
    {
        var games = new List<Game>();

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (var uninstallPath in UNINSTALL_PATHS)
            {
                using var uninstallKey = baseKey.OpenSubKey(uninstallPath);
                if (uninstallKey == null) continue;

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    using var appKey = uninstallKey.OpenSubKey(subKeyName);
                    if (appKey == null) continue;

                    var publisher = appKey.GetValue("Publisher") as string;
                    if (string.IsNullOrEmpty(publisher) || !publisher.Contains("Blizzard Entertainment", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var gameName = appKey.GetValue("DisplayName") as string;
                    var path = appKey.GetValue("InstallLocation") as string;

                    // Some Battle.net games might be "Battle.net" launcher itself
                    if (gameName == "Battle.net" || gameName == "Blizzard Battle.net App") continue;

                    if (!string.IsNullOrEmpty(gameName) && !string.IsNullOrEmpty(path))
                    {
                        if (Directory.Exists(path))
                        {
                            games.Add(new Game
                            {
                                AppId = subKeyName,
                                Name = gameName.Replace(" (PTR)", ""),
                                InstallPath = path,
                                Platform = GamePlatform.BattleNet
                            });
                        }
                    }
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
