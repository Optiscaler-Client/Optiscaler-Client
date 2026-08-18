using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OptiscalerClient.Models;
using OptiscalerClient.Views;

namespace OptiscalerClient.Services
{
    public class AppUpdateService
    {
        private HttpClient _httpClient => NetworkService.GetHttpClient();
        private readonly ComponentManagementService _componentService;

        public string? LatestVersion { get; private set; }
        public string? ReleaseNotes { get; private set; }
        public bool IsError { get; private set; }

        public AppUpdateService(ComponentManagementService componentService)
        {
            _componentService = componentService;
        }

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

        /// <summary>
        /// Only checks whether a newer app version is published on GitHub - does not download
        /// or install anything. Self-updating (download + replace + restart) was removed: on
        /// PublishSingleFile builds the whole app is one .exe, which Windows keeps locked while
        /// the old process is shutting down, making an in-place overwrite unreliable (confirmed
        /// 2026-08-18 - the app kept relaunching the pre-update build after a "successful"
        /// update). Callers should point the user at the GitHub releases page instead.
        /// </summary>
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
    }
}
