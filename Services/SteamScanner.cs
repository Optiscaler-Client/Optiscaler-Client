using Microsoft.Win32;
using OptiscalerClient.Models;
using OptiscalerClient.Views;
using System.Runtime.Versioning;
using ValveKeyValue;

namespace OptiscalerClient.Services;

public class SteamScanner : IGameScanner
{
    private const string REGISTRY_PATH = @"SOFTWARE\Valve\Steam";

    // Allows unit tests to inject a fake Steam path, bypassing registry/filesystem lookups.
    private readonly string? _overrideSteamPath;

    public SteamScanner() { }

    internal SteamScanner(string steamPath) => _overrideSteamPath = steamPath;

    private static readonly KVSerializerOptions _libraryFolderOptions = new()
    {
        HasEscapeSequences = true
    };

    // KVSerializer is not guaranteed thread-safe; keep one per SteamScanner instance and guard calls.
    private readonly KVSerializer _kvSerializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);
    private readonly object _kvLock = new();

    // Caches keyed by path (+ mtime) so repeated scans avoid filesystem resolution and VDF reparsing.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime MtimeUtc, string Canonical)> _canonicalPathCache =
        new(StringComparer.Ordinal);
    private sealed class CachedLibraryFolders
    {
        public DateTime MtimeUtc;
        public List<string> Folders = new();
    }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedLibraryFolders> _libraryFolderCache =
        new(StringComparer.Ordinal);

    public Task<List<Game>> ScanAsync(CancellationToken cancellationToken = default)
    => Task.Run(() => Scan(cancellationToken), cancellationToken);

    private List<Game> Scan(CancellationToken cancellationToken = default)
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

    private string? GetSteamInstallPath() => _overrideSteamPath ?? FindSteamInstallPath();

    /// <summary>Steam's install root on this machine (registry on Windows, known folders on Linux).</summary>
    internal static string? FindSteamInstallPath()
    {
        if (OperatingSystem.IsWindows())
            return GetSteamInstallPathWindows();
        return GetSteamInstallPathLinux();
    }

    [SupportedOSPlatform("windows")]
    private static string? GetSteamInstallPathWindows()
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

    private static string? GetSteamInstallPathLinux()
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
        DateTime mtime;
        try { mtime = File.GetLastWriteTimeUtc(path); }
        catch { mtime = DateTime.MinValue; }

        if (_canonicalPathCache.TryGetValue(path, out var cached) && cached.MtimeUtc == mtime)
            return cached.Canonical;

        string canonical;
        try { canonical = new DirectoryInfo(path).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(path); }
        catch { canonical = Path.GetFullPath(path); }

        _canonicalPathCache[path] = (mtime, canonical);
        return canonical;
    }

    private List<string> GetLibraryFolders(string steamPath)
    {
        var canonicalSteamPath = CanonicalPath(steamPath);

        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        var vdfMtime = File.Exists(vdfPath) ? File.GetLastWriteTimeUtc(vdfPath) : DateTime.MinValue;

        if (_libraryFolderCache.TryGetValue(canonicalSteamPath, out var cached) && cached.MtimeUtc == vdfMtime)
            return new List<string>(cached.Folders);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var folders = new List<string>();

        seen.Add(canonicalSteamPath);
        folders.Add(canonicalSteamPath);

        if (File.Exists(vdfPath))
        {
            try
            {
                lock (_kvLock)
                {
                    using var stream = File.OpenRead(vdfPath);
                    var doc = _kvSerializer.Deserialize(stream, _libraryFolderOptions);
                    if (doc != null)
                    {
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
                }
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[Steam] Error reading libraryfolders.vdf: {ex.Message}");
            }
        }

        _libraryFolderCache[canonicalSteamPath] = new CachedLibraryFolders
        {
            MtimeUtc = vdfMtime,
            Folders = new List<string>(folders)
        };

        return folders;
    }

    private Game? ParseManifest(string manifestPath)
    {
        try
        {
            AppState? appState;
            lock (_kvLock)
            {
                using var stream = File.OpenRead(manifestPath);
                appState = _kvSerializer.Deserialize<AppState>(stream);
            }

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
