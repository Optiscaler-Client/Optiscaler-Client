using Microsoft.Win32;
using OptiscalerClient.Models;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;

namespace OptiscalerClient.Services;

[SupportedOSPlatform("windows")]

public class UbisoftScanner : IGameScanner
{
    private const string UNINSTALL_PATH = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    public Task<List<Game>> ScanAsync(CancellationToken cancellationToken = default)
    => Task.Run(() => Scan(cancellationToken), cancellationToken);

    private List<Game> Scan(CancellationToken cancellationToken = default)
    {
        var games = new List<Game>();

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var uninstallKey = baseKey.OpenSubKey(UNINSTALL_PATH);

            if (uninstallKey != null)
            {
                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    // Uplay games usually start with "Uplay Install "
                    if (subKeyName.StartsWith("Uplay Install ", StringComparison.OrdinalIgnoreCase))
                    {
                        using var appKey = uninstallKey.OpenSubKey(subKeyName);
                        if (appKey == null) continue;

                        var gameName = appKey.GetValue("DisplayName") as string;
                        var path = appKey.GetValue("InstallLocation") as string;

                        if (!string.IsNullOrEmpty(gameName) && !string.IsNullOrEmpty(path))
                        {
                            if (Directory.Exists(path))
                            {
                                games.Add(new Game
                                {
                                    AppId = subKeyName.Replace("Uplay Install ", "").Trim(),
                                    Name = gameName,
                                    InstallPath = path,
                                    Platform = GamePlatform.Ubisoft
                                });
                            }
                        }
                    }
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
