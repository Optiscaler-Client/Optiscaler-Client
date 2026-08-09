// OptiScaler Client - A frontend for managing OptiScaler installations
// Copyright (C) 2026 Agustín Montaña (Agustinm28)
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OptiscalerClient.Services;
using OptiscalerClient.Views;

namespace OptiscalerClient.Helpers
{
    /// <summary>
    /// Shared HTTP GET-with-retry helper. Extracted from ComponentManagementService so any
    /// service hitting a remote host (GitHub API, raw.githubusercontent.com, etc.) reuses the
    /// same exponential-backoff/timeout behavior instead of duplicating it.
    /// </summary>
    public static class HttpRetryHelper
    {
        /// <summary>
        /// Executes an HTTP GET with per-attempt timeout and exponential-backoff retries on
        /// transient network errors. Does NOT retry on HTTP error status codes (e.g. 404).
        /// A 403 is treated as a GitHub REST API rate limit (throws <see cref="GitHubRateLimitException"/>
        /// immediately, no retry) — only correct for api.github.com; callers hitting other hosts
        /// (e.g. raw.githubusercontent.com) should check the status code themselves instead.
        /// </summary>
        public static async Task<HttpResponseMessage> GetWithRetryAsync(
            Func<HttpClient> getClient, string url,
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
                    var resp = await getClient().GetAsync(url, cts.Token);
                    if ((int)resp.StatusCode == 403)
                        throw new GitHubRateLimitException();
                    return resp;
                }
                catch (GitHubRateLimitException)
                {
                    throw; // propagate immediately, no retry
                }
                catch (Exception ex) when (ex is HttpRequestException
                    || ex is ObjectDisposedException  // HttpClient replaced mid-flight; retry picks up the new client
                    || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    lastEx = ex is OperationCanceledException
                        ? new TimeoutException($"Request timed out after {timeoutSeconds}s (attempt {attempt + 1})")
                        : ex;
                    DebugWindow.Log($"[HTTP] Attempt {attempt + 1}/{maxRetries + 1} failed for {url}: {lastEx.Message}");
                }
                if (attempt < maxRetries)
                    await Task.Delay(backoff[Math.Min(attempt, backoff.Length - 1)], cancellationToken);
            }
            throw lastEx!;
        }

        /// <summary>
        /// Same retry/backoff behavior as <see cref="GetWithRetryAsync"/> but without the
        /// api.github.com-specific 403-as-rate-limit assumption — any non-success status code is
        /// simply returned to the caller to inspect. Use this for hosts other than api.github.com
        /// (e.g. raw.githubusercontent.com), where a 403 doesn't necessarily mean rate limiting.
        /// </summary>
        public static async Task<HttpResponseMessage> GetWithRetryNoRateLimitAsync(
            Func<HttpClient> getClient, string url,
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
                    return await getClient().GetAsync(url, cts.Token);
                }
                catch (Exception ex) when (ex is HttpRequestException
                    || ex is ObjectDisposedException
                    || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    lastEx = ex is OperationCanceledException
                        ? new TimeoutException($"Request timed out after {timeoutSeconds}s (attempt {attempt + 1})")
                        : ex;
                    DebugWindow.Log($"[HTTP] Attempt {attempt + 1}/{maxRetries + 1} failed for {url}: {lastEx.Message}");
                }
                if (attempt < maxRetries)
                    await Task.Delay(backoff[Math.Min(attempt, backoff.Length - 1)], cancellationToken);
            }
            throw lastEx!;
        }
    }
}
