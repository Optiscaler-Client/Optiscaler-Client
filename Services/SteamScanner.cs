using Microsoft.Win32;
using OptiscalerClient.Models;
using OptiscalerClient.Views;
using System.Runtime.Versioning;
using ValveKeyValue;

namespace OptiscalerClient.Services;

public class SteamScanner : IGameScanner
{
    private const string REGISTRY_PATH = @"SOFTWARE\Valve\Steam";

    private static readonly KVSerializerOptions _libraryFolderOptions = new()
    {
        HasEscapeSequences = true
    };

    private static readonly KVSerializer _kvSerializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);

    public List<Game> Scan()
    {
        var games = new List<Game>();
        var installPath = GetSteamInstallPath();

        if (string.IsNullOrEmpty(installPath))
            return games;

        var libraryFolders = GetLibraryFolders(installPath);

        foreach (var libraryPath in libraryFolders)
        {
            try
            {
                var steamappsPath = Path.Combine(libraryPath, "steamapps");
                DebugWindow.Log($"[Steam] Scanning library: {steamappsPath}");
                if (!Directory.Exists(steamappsPath)) continue;

                var manifestFiles = Directory.GetFiles(steamappsPath, "appmanifest_*.acf");
                foreach (var file in manifestFiles)
                {
                    var game = ParseManifest(file);
                    if (game != null)
                    {
                        // Verify install path exists
                        if (Directory.Exists(game.InstallPath))
                        {
                            games.Add(game);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[Steam] Error scanning library '{libraryPath}': {ex.Message}");
            }
        }

        return games;
    }

    private string? GetSteamInstallPath()
    {
        if (OperatingSystem.IsWindows())
            return GetSteamInstallPathWindows();
        return GetSteamInstallPathLinux();
    }

    [SupportedOSPlatform("windows")]
    private string? GetSteamInstallPathWindows()
    {
        try
        {
            // Try 32-bit registry view first (Steam is usually 32-bit app)
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = baseKey.OpenSubKey(REGISTRY_PATH);
            return key?.GetValue("InstallPath") as string;
        }
        catch (Exception ex)
        {
            DebugWindow.Log($"[Steam] Error reading registry: {ex.Message}");
            return null;
        }
    }

    private string? GetSteamInstallPathLinux()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
            Path.Combine(home, "snap", "steam", "common", ".steam", "steam"),
        };
        return candidates.FirstOrDefault(p => Directory.Exists(Path.Combine(p, "steamapps")));
    }

    private static string CanonicalPath(string path)
    {
        try { return new DirectoryInfo(path).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(path); }
        catch { return Path.GetFullPath(path); }
    }

    private List<string> GetLibraryFolders(string steamPath)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var folders = new List<string>();

        var canonical = CanonicalPath(steamPath);
        seen.Add(canonical);
        folders.Add(canonical);

        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath)) return folders;

        try
        {
            using var stream = File.OpenRead(vdfPath);
            var doc = _kvSerializer.Deserialize(stream, _libraryFolderOptions);

            foreach (var (_, value) in doc.Root.Children)
            {
                if (value.TryGetValue("path", out var pathValue))
                {
                    var path = pathValue.ToString();
                    if (!string.IsNullOrEmpty(path))
                    {
                        var canon = CanonicalPath(path);
                        if (seen.Add(canon))
                            folders.Add(canon);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DebugWindow.Log($"[Steam] Error reading libraryfolders.vdf: {ex.Message}");
        }

        return folders;
    }

    private Game? ParseManifest(string manifestPath)
    {
        try
        {
            using var stream = File.OpenRead(manifestPath);
            var appState = _kvSerializer.Deserialize<AppState>(stream);

            // Extract AppID
            var appId = !string.IsNullOrEmpty(appState.AppId)
                ? appState.AppId
                : Path.GetFileName(manifestPath).Replace("appmanifest_", "").Replace(".acf", "");

            // Extract Name
            var name = !string.IsNullOrEmpty(appState.Name)
                ? appState.Name
                : "Unknown Game";

            // Extract InstallDir
            var installDirName = appState.InstallDir;
            if (string.IsNullOrEmpty(installDirName)) return null;

            var steamappsPath = Path.GetDirectoryName(manifestPath); // .../steamapps
            if (steamappsPath == null) return null;

            var commonPath = Path.Combine(steamappsPath, "common");
            var fullInstallPath = Path.Combine(commonPath, installDirName);

            return new Game
            {
                AppId = appId,
                Name = name,
                InstallPath = fullInstallPath,
                Platform = GamePlatform.Steam
            };
        }
        catch (Exception ex)
        {
            DebugWindow.Log($"[Steam] Error parsing manifest '{manifestPath}': {ex.Message}");
            return null;
        }
    }

    private sealed class AppState
    {
        [KVProperty("appid")]
        public string? AppId { get; set; }

        [KVProperty("name")]
        public string? Name { get; set; }

        [KVProperty("installdir")]
        public string? InstallDir { get; set; }
    }
}
