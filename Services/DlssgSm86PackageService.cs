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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OptiscalerClient.Models;
using OptiscalerClient.Views;

namespace OptiscalerClient.Services
{
    /// <summary>Thrown when a downloaded or cached dlssg_for_sm86 file doesn't match the pinned
    /// manifest. The offending file has already been deleted when this is thrown.</summary>
    public sealed class DlssgSm86VerificationException : Exception
    {
        public string FileName { get; }

        public DlssgSm86VerificationException(string fileName, string reason)
            : base($"{fileName}: {reason}")
        {
            FileName = fileName;
        }
    }

    /// <summary>
    /// Downloads and verifies dlssg_for_sm86 builds (https://github.com/sdli1995/dlssg_for_sm86).
    /// Trust comes only from the pinned manifest shipped with the app: each file is fetched from the
    /// upstream repository at the pinned commit (never mirrored — the proxies embed NVIDIA's DLSS-G
    /// runtime) and must match its recorded size, SHA-256 and signer thumbprint before it's used.
    /// Cache layout: {AppData}/Cache/DlssgSm86/{modVersion}/{buildId}/{fileName}.
    /// </summary>
    public sealed class DlssgSm86PackageService
    {
        public const string ManifestFileName = "dlssg_sm86_manifest.json";

        // One download at a time across every window: two Manage windows asking for the same
        // ~120 MB build must not race on the same .part files.
        private static readonly SemaphoreSlim _downloadLock = new(1, 1);
        private static DlssgSm86PackageManifest? _manifest;
        private static bool _manifestLoaded;

        public string CacheRoot { get; } = Path.Combine(AppPaths.GetAppDataRoot(), "Cache", "DlssgSm86");

        /// <summary>The pinned manifest, or null if it's missing or unreadable (feature unavailable).</summary>
        public DlssgSm86PackageManifest? Manifest
        {
            get
            {
                if (_manifestLoaded) return _manifest;
                _manifest = LoadManifest();
                _manifestLoaded = true;
                return _manifest;
            }
        }

        private static DlssgSm86PackageManifest? LoadManifest()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "assets", "configs", ManifestFileName);
            try
            {
                Stream stream = File.Exists(path)
                    ? File.OpenRead(path)
                    : Avalonia.Platform.AssetLoader.Open(new Uri($"avares://OptiscalerClient/assets/configs/{ManifestFileName}"));
                using (stream)
                {
                    var manifest = JsonSerializer.Deserialize(stream, DlssgSm86JsonContext.Default.DlssgSm86PackageManifest);
                    if (manifest == null || manifest.Builds.Count == 0 || string.IsNullOrEmpty(manifest.Commit))
                        return null;
                    return manifest;
                }
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[DlssgSm86] Could not load pinned manifest: {ex.Message}");
                return null;
            }
        }

        public string GetBuildCacheDir(string modVersion, string buildId) => Path.Combine(CacheRoot, modVersion, buildId);

        /// <summary>The files an install of <paramref name="proxyNames"/> needs from a build: those
        /// proxies plus the INI.</summary>
        public static IReadOnlyList<DlssgSm86FileEntry> SelectFiles(DlssgSm86BuildEntry build, IEnumerable<string> proxyNames)
        {
            var names = new HashSet<string>(proxyNames, StringComparer.OrdinalIgnoreCase);
            return build.Files.Where(f => !f.IsProxy || names.Contains(f.Name)).ToList();
        }

        /// <summary>Bytes still to download for these files (0 when all are cached at the right size).
        /// Size only — the hash is checked again by <see cref="EnsureFilesAsync"/> before use.</summary>
        public long GetMissingBytes(DlssgSm86BuildEntry build, IEnumerable<DlssgSm86FileEntry> files)
        {
            var manifest = Manifest;
            if (manifest == null) return 0;
            var dir = GetBuildCacheDir(manifest.ModVersion, build.Id);
            return files.Where(f =>
            {
                var path = Path.Combine(dir, f.Name);
                return !File.Exists(path) || new FileInfo(path).Length != f.Size;
            }).Sum(f => f.Size);
        }

        /// <summary>
        /// Makes sure every file in <paramref name="files"/> is in the cache and verified, downloading
        /// what's missing. Returns the build's cache directory. Any file that fails verification is
        /// deleted and a <see cref="DlssgSm86VerificationException"/> is thrown.
        /// </summary>
        public async Task<string> EnsureFilesAsync(DlssgSm86BuildEntry build, IReadOnlyList<DlssgSm86FileEntry> files,
            IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            var manifest = Manifest ?? throw new InvalidOperationException("dlssg_for_sm86 manifest is missing.");
            var dir = GetBuildCacheDir(manifest.ModVersion, build.Id);

            await _downloadLock.WaitAsync(cancellationToken);
            try
            {
                Directory.CreateDirectory(dir);
                long totalBytes = Math.Max(1, files.Sum(f => f.Size));
                long doneBytes = 0;

                foreach (var file in files)
                {
                    var path = Path.Combine(dir, file.Name);
                    if (File.Exists(path))
                    {
                        // Re-verify on every use: a cache file is just a file on disk anyone could replace.
                        try
                        {
                            await Task.Run(() => VerifyFile(path, file, manifest.SignerThumbprint), cancellationToken);
                            doneBytes += file.Size;
                            progress?.Report(doneBytes * 100.0 / totalBytes);
                            continue;
                        }
                        catch (DlssgSm86VerificationException ex)
                        {
                            DebugWindow.Log($"[DlssgSm86] Cached file failed verification, downloading again: {ex.Message}");
                        }
                    }

                    var partPath = path + ".part";
                    var url = $"https://raw.githubusercontent.com/{manifest.Repo}/{manifest.Commit}/{file.Path}";
                    DebugWindow.Log($"[DlssgSm86] Downloading {url}");
                    var baseBytes = doneBytes;
                    var fileProgress = progress == null ? null : new Progress<double>(p =>
                        progress.Report((baseBytes + file.Size * Math.Clamp(p, 0, 100) / 100.0) * 100.0 / totalBytes));
                    try
                    {
                        await ComponentManagementService.StreamToFileAsync(NetworkService.GetHttpClient, url, partPath,
                            fileProgress, file.Size, timeoutSeconds: 600, cancellationToken: cancellationToken);
                    }
                    catch
                    {
                        TryDelete(partPath);
                        throw;
                    }

                    await Task.Run(() => VerifyFile(partPath, file, manifest.SignerThumbprint), cancellationToken);
                    File.Move(partPath, path, overwrite: true);
                    doneBytes += file.Size;
                    progress?.Report(doneBytes * 100.0 / totalBytes);
                }

                return dir;
            }
            finally
            {
                _downloadLock.Release();
            }
        }

        /// <summary>Checks size, SHA-256 and (for proxy DLLs) the Authenticode signer thumbprint against
        /// the pinned entry. Deletes the file and throws on the first mismatch.</summary>
        public static void VerifyFile(string path, DlssgSm86FileEntry entry, string signerThumbprint)
        {
            string? failure = null;
            try
            {
                var length = new FileInfo(path).Length;
                if (length != entry.Size)
                    failure = string.Format(DlssgSm86Records.GetString("TxtDlssgSm86VerifySize",
                        "size is {0} bytes, expected {1}"), length, entry.Size);
                else if (!string.Equals(ComputeSha256(path), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    failure = DlssgSm86Records.GetString("TxtDlssgSm86VerifyHash", "SHA-256 does not match the pinned value");
                else if (entry.IsProxy && OperatingSystem.IsWindows())
                {
                    var thumbprint = GetSignerThumbprint(path);
                    if (!string.Equals(thumbprint, signerThumbprint, StringComparison.OrdinalIgnoreCase))
                        failure = thumbprint == null
                            ? DlssgSm86Records.GetString("TxtDlssgSm86VerifyUnsigned", "the file is not signed")
                            : string.Format(DlssgSm86Records.GetString("TxtDlssgSm86VerifySigner", "signed by an unexpected certificate ({0})"), thumbprint);
                }
            }
            catch (Exception ex)
            {
                failure = string.Format(DlssgSm86Records.GetString("TxtDlssgSm86VerifyError", "could not be checked ({0})"), ex.Message);
            }

            if (failure == null) return;
            TryDelete(path);
            throw new DlssgSm86VerificationException(entry.Name, failure);
        }

        /// <summary>Thumbprint of the certificate that signed <paramref name="path"/>, without asking
        /// Windows whether it trusts it: the mod is self-signed, so WinVerifyTrust always fails, and the
        /// pinned thumbprint is what we compare against instead.</summary>
        [SupportedOSPlatform("windows")]
        private static string? GetSignerThumbprint(string path)
        {
            try
            {
#pragma warning disable SYSLIB0057 // No X509CertificateLoader equivalent reads an Authenticode signature from a PE file.
                using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
                return cert.Thumbprint;
            }
            catch (CryptographicException)
            {
                return null;
            }
        }

        public static string ComputeSha256(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        // ── Cache management ────────────────────────────────────────────────────

        public sealed record CachedBuild(string ModVersion, string BuildId, string Directory, long SizeBytes);

        public List<CachedBuild> ListCached()
        {
            var result = new List<CachedBuild>();
            if (!Directory.Exists(CacheRoot)) return result;
            foreach (var versionDir in Directory.GetDirectories(CacheRoot))
            {
                foreach (var buildDir in Directory.GetDirectories(versionDir))
                {
                    long size = 0;
                    try { size = new DirectoryInfo(buildDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
                    catch { /* report 0 */ }
                    if (size == 0) continue;
                    result.Add(new CachedBuild(Path.GetFileName(versionDir), Path.GetFileName(buildDir), buildDir, size));
                }
            }
            return result.OrderByDescending(c => c.ModVersion).ThenByDescending(c => c.BuildId).ToList();
        }

        public void DeleteCached(string modVersion, string buildId)
        {
            var dir = GetBuildCacheDir(modVersion, buildId);
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (Exception ex) { DebugWindow.Log($"[DlssgSm86] Could not delete cache '{dir}': {ex.Message}"); }

            var versionDir = Path.GetDirectoryName(dir);
            try
            {
                if (versionDir != null && Directory.Exists(versionDir) && !Directory.EnumerateFileSystemEntries(versionDir).Any())
                    Directory.Delete(versionDir);
            }
            catch { /* leave an empty folder behind */ }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { DebugWindow.Log($"[DlssgSm86] Could not delete '{path}': {ex.Message}"); }
        }
    }
}
