using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OptiscalerClient.Models;
using OptiscalerClient.Views;

namespace OptiscalerClient.Services
{
    /// <summary>
    /// Thrown when the app can't self-update because its own install directory isn't
    /// writable - e.g. installed via a distro package (AUR) or the Nix store, where the
    /// package manager owns the binary and self-replacing it would be both impossible
    /// (permissions) and wrong (it'd fight the next `pacman -Syu` / `nix profile upgrade`).
    /// </summary>
    public class ManagedInstallException : Exception
    {
        public ManagedInstallException() : base("The install directory is not writable; this looks like a package-managed install.") { }
    }

    public class AppUpdateService
    {
        private HttpClient _httpClient => NetworkService.GetHttpClient();
        private readonly ComponentManagementService _componentService;

        public string? LatestVersion { get; private set; }
        public string? ReleaseNotes { get; private set; }
        public string? DownloadUrl { get; private set; }
        public bool IsError { get; private set; }

        public AppUpdateService(ComponentManagementService componentService)
        {
            _componentService = componentService;
        }

        // ── Download helpers ─────────────────────────────────────────────────────

        private static async Task<HttpResponseMessage> GetWithRetryAsync(
            HttpClient client, string url,
            int maxRetries = 3, int timeoutSeconds = 30,
            CancellationToken cancellationToken = default)
        {
            int[] backoff = { 1000, 3000, 7000 };
            Exception? lastEx = null;
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                try
                {
                    return await client.GetAsync(url, cts.Token);
                }
                catch (Exception ex) when (ex is HttpRequestException
                    || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    lastEx = ex is OperationCanceledException
                        ? new TimeoutException($"Request timed out after {timeoutSeconds}s (attempt {attempt + 1})")
                        : ex;
                    DebugWindow.Log($"[HTTP] Attempt {attempt + 1}/{maxRetries + 1} failed: {lastEx.Message}");
                }
                if (attempt < maxRetries)
                    await Task.Delay(backoff[Math.Min(attempt, backoff.Length - 1)], cancellationToken);
            }
            throw lastEx!;
        }

        private static string SafeDestinationPath(string destinationDir, string entryPath)
        {
            if (string.IsNullOrEmpty(entryPath))
                throw new InvalidOperationException("Archive entry has an empty path.");
            var fullDest = Path.GetFullPath(Path.Combine(destinationDir, entryPath));
            var root = Path.GetFullPath(destinationDir);
            if (!fullDest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(fullDest, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Archive entry '{entryPath}' would extract outside destination directory.");
            return fullDest;
        }

        private static async Task StreamToFileAsync(
            HttpClient client, string url, string destPath,
            IProgress<double>? progress = null, long estimatedBytes = 20 * 1024 * 1024,
            int maxRetries = 3, int timeoutSeconds = 120,
            CancellationToken cancellationToken = default)
        {
            int[] backoff = { 2000, 5000, 10000 };
            Exception? lastEx = null;
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    DebugWindow.Log($"[AppUpdate] Retry {attempt}/{maxRetries} for download");
                    try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
                }
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                try
                {
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    response.EnsureSuccessStatusCode();
                    var totalBytes = response.Content.Headers.ContentLength ?? estimatedBytes;
                    long totalRead = 0;
                    using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
                    using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                    var buffer = new byte[65536];
                    int read;
                    while ((read = await stream.ReadAsync(buffer.AsMemory(), cts.Token)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                        totalRead += read;
                        progress?.Report((double)totalRead / totalBytes * 100.0);
                    }
                    progress?.Report(100.0);
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException
                    || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    lastEx = ex is OperationCanceledException
                        ? new TimeoutException($"Download timed out after {timeoutSeconds}s (attempt {attempt + 1})")
                        : ex;
                    DebugWindow.Log($"[AppUpdate] Download attempt {attempt + 1}/{maxRetries + 1} failed: {lastEx.Message}");
                }
                if (attempt < maxRetries)
                    await Task.Delay(backoff[Math.Min(attempt, backoff.Length - 1)], cancellationToken);
            }
            throw lastEx!;
        }

        // ─────────────────────────────────────────────────────────────────────────

        public async Task<bool> CheckForAppUpdateAsync()
        {
            IsError = false;
            try
            {
                var repo = _componentService.Config.App;
                if (string.IsNullOrWhiteSpace(repo.RepoOwner) || string.IsNullOrWhiteSpace(repo.RepoName))
                    return false;

                var url = $"https://api.github.com/repos/{repo.RepoOwner}/{repo.RepoName}/releases/latest";
                DebugWindow.Log($"[AppUpdate] Fetching latest App version from: {url}");

                var response = await GetWithRetryAsync(_httpClient, url);
                if (!response.IsSuccessStatusCode)
                {
                    DebugWindow.Log($"[AppUpdate] API Error: {response.StatusCode} ({(int)response.StatusCode})");
                    IsError = true;
                    return false;
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("tag_name", out var tagProp))
                {
                    var versionTag = tagProp.GetString() ?? string.Empty;
                    LatestVersion = versionTag.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                        ? versionTag.Substring(1)
                        : versionTag;

                    if (doc.RootElement.TryGetProperty("body", out var bodyProp))
                        ReleaseNotes = bodyProp.GetString();

                    if (doc.RootElement.TryGetProperty("assets", out var assets))
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            if (asset.TryGetProperty("browser_download_url", out var downloadProp))
                            {
                                var assetUrl = downloadProp.GetString();
                                if (assetUrl != null && assetUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (assetUrl.Contains("OptiscalerClient_Portable.zip", StringComparison.OrdinalIgnoreCase))
                                    {
                                        DownloadUrl = assetUrl;
                                        break;
                                    }
                                    else if (DownloadUrl == null)
                                    {
                                        DownloadUrl = assetUrl; // Fallback just in case
                                    }
                                }
                            }
                        }
                    }

                    // More robust way to get current version
                    string currentVersionStr = typeof(AppUpdateService).Assembly
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                        .InformationalVersion ?? "0.0.0.0";

                    // Cleanup version string (remove common git suffixes like +...)
                    if (currentVersionStr.Contains("+")) currentVersionStr = currentVersionStr.Split('+')[0];
                    if (currentVersionStr.StartsWith("v", StringComparison.OrdinalIgnoreCase)) currentVersionStr = currentVersionStr.Substring(1);

                    if (string.IsNullOrEmpty(LatestVersion)) return false;

                    // Normalize LatestVersion too (remove prefixes like 'OptiscalerClient-' or 'v')
                    if (LatestVersion.StartsWith("OptiscalerClient-", StringComparison.OrdinalIgnoreCase))
                        LatestVersion = LatestVersion.Substring("OptiscalerClient-".Length);
                    if (LatestVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                        LatestVersion = LatestVersion.Substring(1);

                    // Support for comparison logs
                    var logMsg = $"[AppUpdate] Normalized: Current='{currentVersionStr}', Latest='{LatestVersion}'";
                    DebugWindow.Log(logMsg);

                    if (Version.TryParse(currentVersionStr, out var currentVer) && Version.TryParse(LatestVersion, out var newVer))
                    {
                        var parseMsg = $"[AppUpdate] Parsed versions: Current='{currentVer}', New='{newVer}'";
                        DebugWindow.Log(parseMsg);

                        if (newVer > currentVer)
                        {
                            var updateMsg = $"[AppUpdate] Detected UPDATE: {newVer} > {currentVer}";
                            DebugWindow.Log(updateMsg);
                            return true;
                        }
                    }
                    else
                    {
                        var fallbackMsg = $"[AppUpdate] Fallback (non-SEMVER) comparison: '{LatestVersion}' != '{currentVersionStr}'";
                        DebugWindow.Log(fallbackMsg);
                        if (LatestVersion != currentVersionStr)
                            return true;
                    }
                }
            }
            catch (Exception ex)
            {
                string errorMsg = $"[AppUpdate] FATAL ERROR: {ex.Message}";
                DebugWindow.Log(errorMsg);
            }
            return false;
        }

        /// <summary>
        /// Probes whether the app's own install directory can be written to. False under
        /// a package-managed install (AUR, Nix store, /usr, ...), where self-updating
        /// must be skipped in favor of the package manager.
        /// </summary>
        public static bool IsInstallDirWritable()
        {
            try
            {
                var probePath = Path.Combine(AppContext.BaseDirectory, $".write_test_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probePath, string.Empty);
                File.Delete(probePath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task DownloadAndPrepareUpdateAsync(IProgress<double>? progress = null)
        {
            if (string.IsNullOrEmpty(DownloadUrl))
                throw new Exception("No valid download URL found for the update.");

            if (!IsInstallDirWritable())
                throw new ManagedInstallException();

            var tempZip = Path.Combine(Path.GetTempPath(), $"OptiscalerClientUpdate_{Guid.NewGuid()}.zip");
            var updateFolder = Path.Combine(AppContext.BaseDirectory, "update_temp");

            try
            {
                // Stream download with retry and per-attempt timeout
                DebugWindow.Log($"[AppUpdate] Streaming download from {DownloadUrl}");
                await StreamToFileAsync(_httpClient, DownloadUrl, tempZip, progress);

                if (Directory.Exists(updateFolder))
                    Directory.Delete(updateFolder, true);
                Directory.CreateDirectory(updateFolder);

                // Extract with path traversal validation
                using (var zipArchive = ZipFile.OpenRead(tempZip))
                {
                    foreach (var entry in zipArchive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
                        var destPath = SafeDestinationPath(updateFolder, entry.FullName);
                        var destDir = Path.GetDirectoryName(destPath);
                        if (destDir != null) Directory.CreateDirectory(destDir);
                        entry.ExtractToFile(destPath, overwrite: true);
                    }
                }

                // Check if zip contains a single folder inside it, then move contents up
                var extractedDirs = Directory.GetDirectories(updateFolder);
                var extractedFiles = Directory.GetFiles(updateFolder);

                if (extractedDirs.Length == 1 && extractedFiles.Length == 0)
                {
                    var innerDir = extractedDirs[0];
                    foreach (var file in Directory.GetFiles(innerDir, "*.*", SearchOption.AllDirectories))
                    {
                        var destPath = file.Replace(innerDir, updateFolder);
                        var destDir = Path.GetDirectoryName(destPath);
                        if (destDir != null) Directory.CreateDirectory(destDir);
                        File.Move(file, destPath, overwrite: true);
                    }
                    Directory.Delete(innerDir, true);
                }

                // Create the update script (platform-specific)
                if (OperatingSystem.IsWindows())
                {
                    var basePath = AppContext.BaseDirectory.TrimEnd('\\');
                    var batPath = Path.Combine(basePath, "update.bat");
                    var batContent = $@"@echo off
echo Updating Optiscaler Client...
timeout /t 2 /nobreak > nul
cd /d ""{basePath}""
xcopy /Y /S ""{updateFolder}\*"" "".\""
rmdir /s /q ""{updateFolder}""
start """" ""OptiscalerClient.exe""
del ""%~f0""
";
                    File.WriteAllText(batPath, batContent);
                }
                else
                {
                    var basePath = AppContext.BaseDirectory.TrimEnd('/');
                    var shPath = Path.Combine(basePath, "update.sh");
                    var shContent = $@"#!/bin/sh
echo 'Updating Optiscaler Client...'
sleep 2
cp -rf ""{updateFolder}/""* ""{basePath}/""
rm -rf ""{updateFolder}""
""{basePath}/OptiscalerClient"" &
rm -- ""$0""
";
                    File.WriteAllText(shPath, shContent);
                    File.SetUnixFileMode(shPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
            }
            finally
            {
                if (File.Exists(tempZip))
                    File.Delete(tempZip);
            }
        }

        public void FinalizeAndRestart()
        {
            string scriptPath;
            ProcessStartInfo psi;

            if (OperatingSystem.IsWindows())
            {
                scriptPath = Path.Combine(AppContext.BaseDirectory, "update.bat");
                if (!File.Exists(scriptPath)) return;
                psi = new ProcessStartInfo
                {
                    FileName = scriptPath,
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
            }
            else
            {
                scriptPath = Path.Combine(AppContext.BaseDirectory, "update.sh");
                if (!File.Exists(scriptPath)) return;
                psi = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    Arguments = scriptPath,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }

            Process.Start(psi);
            // Avalonia UI TODO: System.Windows.Application.Current.Shutdown();
        }
    }
}
