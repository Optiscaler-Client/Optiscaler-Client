using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace OptiscalerClient.Services;

public class OptiscalerManagementService
{
    private string _repoOwner = "cdozdil";
    private string _repoName = "OptiScaler";
    private readonly string _cacheDir;
    private readonly string _versionFile;
    private string _configFile;
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private readonly HttpClient _httpClient;

    public string? CurrentLocalVersion { get; private set; }
    public bool IsUpdateAvailable { get; private set; }
    public string? LatestRemoteVersion { get; private set; }

    public event Action? OnStatusChanged;

    public OptiscalerManagementService()
    {
        var baseDir = AppPaths.GetAppDataRoot();
        _cacheDir = Path.Combine(baseDir, "Cache");
        _versionFile = Path.Combine(baseDir, "version.json");
        _configFile = Path.Combine(baseDir, "config.json");

        Directory.CreateDirectory(_cacheDir);

        _httpClient = SharedHttpClient;

        LoadConfiguration();
        LoadLocalVersion();
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "OptiscalerClient");
        return client;
    }

    private void LoadConfiguration()
    {
        try
        {
            // Check local directory first (portable/dev friendly)
            var localConfig = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (File.Exists(localConfig))
            {
                _configFile = localConfig; // Use this as source
            }

            if (File.Exists(_configFile))
            {
                var json = File.ReadAllText(_configFile);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("RepoOwner", out var owner)) _repoOwner = owner.GetString() ?? _repoOwner;
                if (doc.RootElement.TryGetProperty("RepoName", out var name)) _repoName = name.GetString() ?? _repoName;
            }
            else
            {
                // Create default config in AppData if neither exists
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(new { RepoOwner = _repoOwner, RepoName = _repoName }, options);
                File.WriteAllText(_configFile, json);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OptiscalerMgmt] Config load error, using defaults: {ex.Message}");
        }
    }

    private void LoadLocalVersion()
    {
        if (File.Exists(_versionFile))
        {
            try
            {
                var json = File.ReadAllText(_versionFile);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("version", out var v))
                {
                    CurrentLocalVersion = v.GetString();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OptiscalerMgmt] Corrupt version file: {ex.Message}");
            }
        }
    }

    public Exception? LastError { get; private set; }

    public async Task CheckForUpdatesAsync()
    {
        LastError = null;
        try
        {
            // GitHub API Release (Latest Stable)
            var url = $"https://api.github.com/repos/{_repoOwner}/{_repoName}/releases/latest";
            var release = await _httpClient.GetFromJsonAsync<GitHubRelease>(url);

            if (release != null)
            {
                LatestRemoteVersion = release.tag_name;

                if (CurrentLocalVersion != LatestRemoteVersion)
                {
                    IsUpdateAvailable = true;
                }
                else
                {
                    IsUpdateAvailable = false;
                }

                OnStatusChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            LastError = ex;
            Console.WriteLine($"[OptiScaler Update ERROR] Check failed: {ex}");
        }
    }

    public async Task DownloadAndExtractUpdateAsync()
    {
        if (string.IsNullOrEmpty(LatestRemoteVersion)) await CheckForUpdatesAsync();
        if (string.IsNullOrEmpty(LatestRemoteVersion)) throw new Exception("Could not verify latest version.");

        // Fetch release asset again from list
        var url = $"https://api.github.com/repos/{_repoOwner}/{_repoName}/releases?per_page=1";
        var releases = await _httpClient.GetFromJsonAsync<GitHubRelease[]>(url);

        if (releases == null || releases.Length == 0) throw new Exception("No releases found.");
        var release = releases[0];

        if (release.assets == null || release.assets.Length == 0)
            throw new Exception("No assets found in release.");

        // Find the archive file (.7z preferred, then .zip)
        var asset = release.assets.FirstOrDefault(a => a.name != null && a.name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase));
        if (asset == null) asset = release.assets.FirstOrDefault(a => a.name != null && a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (asset == null) asset = release.assets[0]; // Fallback

        var archiveName = asset.name ?? "update.zip";
        var archivePath = Path.Combine(Path.GetTempPath(), $"OptiScalerLegacy_{Guid.NewGuid()}_{archiveName}");

        // Stream download with per-attempt timeout
        using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
        using (var dlResponse = await _httpClient.GetAsync(asset.browser_download_url, HttpCompletionOption.ResponseHeadersRead, cts.Token))
        {
            dlResponse.EnsureSuccessStatusCode();
            var totalBytes = dlResponse.Content.Headers.ContentLength ?? 20 * 1024 * 1024;
            long totalRead = 0;
            using var fs = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
            using var stream = await dlResponse.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new byte[65536];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), cts.Token)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                totalRead += read;
            }
        }

        try
        {
        // Clear old cache content ensures clean install
        foreach (var file in Directory.GetFiles(_cacheDir))
        {
            try { File.Delete(file); } catch { }
        }
        foreach (var dir in Directory.GetDirectories(_cacheDir))
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        // Extract with path traversal validation
        try
        {
            using (var archive = ArchiveFactory.OpenArchive(archivePath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (!entry.IsDirectory)
                    {
                        var entryKey = entry.Key ?? string.Empty;
                        var fullDest = Path.GetFullPath(Path.Combine(_cacheDir, entryKey));
                        var root = Path.GetFullPath(_cacheDir);
                        if (!fullDest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException($"Archive entry '{entryKey}' would extract outside cache directory.");
                        var destDir = Path.GetDirectoryName(fullDest);
                        if (destDir != null) Directory.CreateDirectory(destDir);
                        using var entryStream = entry.OpenEntryStream();
                        using var fileStream = File.Create(fullDest);
                        entryStream.CopyTo(fileStream, 81920);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Extraction failed: {ex.Message}");
        }
        }
        finally
        {
            try { if (File.Exists(archivePath)) File.Delete(archivePath); } catch { }
        }

        // Update stored version
        CurrentLocalVersion = LatestRemoteVersion;
        IsUpdateAvailable = false;

        var json = JsonSerializer.Serialize(new { version = CurrentLocalVersion });
        await File.WriteAllTextAsync(_versionFile, json);

        OnStatusChanged?.Invoke();
    }

    public string GetCacheDirectory()
    {
        return _cacheDir;
    }

    // DTOs
    private class GitHubRelease
    {
        public string? tag_name { get; set; }
        public GitHubAsset[]? assets { get; set; }
    }

    private class GitHubAsset
    {
        public string? name { get; set; }
        public string? browser_download_url { get; set; }
    }
}
