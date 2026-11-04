using OptiscalerClient.Models;
using System.IO;
using System.Linq;

namespace OptiscalerClient.Services;

public class XboxScanner : IGameScanner
{
    public Task<List<Game>> ScanAsync(CancellationToken cancellationToken = default)
    => Task.Run(() => Scan(cancellationToken), cancellationToken);

    private List<Game> Scan(CancellationToken cancellationToken = default)
    {
        var games = new List<Game>();

        try
        {
            // Scan XboxGames on all available drives
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.IsReady && drive.DriveType == DriveType.Fixed)
                {
                    string xboxGamesPath = Path.Combine(drive.Name, "XboxGames");
                    if (Directory.Exists(xboxGamesPath))
                    {
                        var directories = Directory.GetDirectories(xboxGamesPath);
                        foreach (var dir in directories)
                        {
                            try
                            {
                                // Xbox Game Pass games typically have executable files or a Content folder structure.
                                // A folder inside an XboxGames directory is generally an installed game.
                                string gameName = Path.GetFileName(dir);

                                // Basic validation: skip empty folders
                                if (Directory.EnumerateFileSystemEntries(dir).Any())
                                {
                                    games.Add(new Game
                                    {
                                        AppId = gameName, // No straightforward specific ID for Game Pass like Steam's AppId
                                        Name = gameName,
                                        InstallPath = dir,
                                        Platform = GamePlatform.Xbox
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Xbox] Error scanning game folder '{dir}': {ex.Message}");
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Xbox] Error enumerating drives: {ex.Message}");
        }

        return games;
    }
}
