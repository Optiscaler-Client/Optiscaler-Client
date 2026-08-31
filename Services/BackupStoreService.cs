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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OptiscalerClient.Models;
using OptiscalerClient.Views;

namespace OptiscalerClient.Services
{
    /// <summary>
    /// Manages the external backup store for OptiScaler installations.
    /// All backups live under %APPDATA%\OptiscalerClient\Backups\{slug}\
    /// rather than inside the game folder, preventing state corruption.
    /// </summary>
    public class BackupStoreService
    {
        private const string ManifestFileName = "manifest.json";
        private const string FilesDirName = "files";

        private readonly string _backupsRoot;

        // Names that are unambiguously OptiScaler's — no game ships these
        private static readonly HashSet<string> _unambiguousResidueNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "OptiScaler.dll", "OptiScaler.ini", "OptiScaler.log",
            "fakenvapi.dll", "fakenvapi.ini", "fakenvapi.log"
        };

        public BackupStoreService()
        {
            _backupsRoot = Path.Combine(AppPaths.GetAppDataRoot(), "Backups");
            Directory.CreateDirectory(_backupsRoot);
        }

        // ── Path helpers ─────────────────────────────────────────────────────────

        public string GetBackupRoot(string gameDir)
            => Path.Combine(_backupsRoot, ComputeGameSlug(gameDir));

        public string GetFilesDir(string gameDir)
            => Path.Combine(GetBackupRoot(gameDir), FilesDirName);

        public string GetManifestPath(string gameDir)
            => Path.Combine(GetBackupRoot(gameDir), ManifestFileName);

        // ── State queries ─────────────────────────────────────────────────────────

        public bool HasValidBackup(string gameDir)
        {
            var manifestPath = GetManifestPath(gameDir);
            if (!File.Exists(manifestPath))
                return false;

            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize(json, OptimizerContext.Default.InstallationManifest);
                return string.Equals(manifest?.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public InstallationManifest? LoadManifest(string gameDir)
        {
            var manifestPath = GetManifestPath(gameDir);
            if (!File.Exists(manifestPath))
                return null;

            try
            {
                var json = File.ReadAllText(manifestPath);
                return JsonSerializer.Deserialize(json, OptimizerContext.Default.InstallationManifest);
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[BackupStore] Failed to load manifest for '{gameDir}': {ex.Message}");
                return null;
            }
        }

        public void SaveManifest(string gameDir, InstallationManifest manifest)
        {
            var backupRoot = GetBackupRoot(gameDir);
            Directory.CreateDirectory(backupRoot);

            var manifestPath = GetManifestPath(gameDir);
            var tmpPath = manifestPath + ".tmp";

            var json = JsonSerializer.Serialize(manifest, OptimizerContext.Default.InstallationManifest);
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, manifestPath, overwrite: true);
        }

        // ── Backup / Restore ──────────────────────────────────────────────────────

        /// <summary>
        /// Copies {gameDir}/{relativePath} into the external backup store keyed by storeKey.
        /// storeKey is the game root (game.InstallPath) used for slug computation.
        /// gameDir is the actual directory where the file lives on disk.
        /// Returns true if the file was backed up, false if it didn't exist.
        /// </summary>
        public bool BackupFile(string storeKey, string gameDir, string relativePath)
        {
            var src = Path.Combine(gameDir, relativePath);
            if (!File.Exists(src))
                return false;

            var dst = Path.Combine(GetFilesDir(storeKey), relativePath);
            var dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir))
                Directory.CreateDirectory(dstDir);

            File.Copy(src, dst, overwrite: true);
            return true;
        }

        /// <summary>
        /// Restores a backed-up file back into the game directory.
        /// storeKey is the game root (game.InstallPath) used for slug computation.
        /// gameDir is the actual directory where the file should be restored to.
        /// Returns true if the restore succeeded.
        /// </summary>
        public bool RestoreFile(string storeKey, string gameDir, string relativePath, string? backupRelativePath = null)
        {
            var effectiveRelative = string.IsNullOrWhiteSpace(backupRelativePath) ? relativePath : backupRelativePath;
            var src = Path.Combine(GetFilesDir(storeKey), effectiveRelative);
            if (!File.Exists(src))
                return false;

            var dst = Path.Combine(gameDir, relativePath);
            var dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir) && !Directory.Exists(dstDir))
                Directory.CreateDirectory(dstDir);

            File.Copy(src, dst, overwrite: true);
            return true;
        }

        /// <summary>
        /// Deletes the entire backup store entry for the given game directory.
        /// </summary>
        public void DeleteBackup(string gameDir)
        {
            var backupRoot = GetBackupRoot(gameDir);
            if (Directory.Exists(backupRoot))
            {
                try { Directory.Delete(backupRoot, true); }
                catch (Exception ex) { DebugWindow.Log($"[BackupStore] Could not delete backup root '{backupRoot}': {ex.Message}"); }
            }
        }

        // ── Residue detection ─────────────────────────────────────────────────────

        /// <summary>
        /// Returns the list of relative paths in gameDir that are unambiguously OptiScaler
        /// residues from a previous install: files with known OptiScaler-only names, or whose
        /// SHA-256 matches a file in any of the provided cache directories.
        /// </summary>
        public IReadOnlyList<string> FindResiduesInGameDir(
            string gameDir,
            IEnumerable<string> knownArtifactNames,
            IEnumerable<string> cacheDirs)
        {
            var residues = new List<string>();
            var cacheHashes = BuildCacheHashSet(cacheDirs);

            foreach (var artifactName in knownArtifactNames)
            {
                // Handle sub-paths like plugins\OptiPatcher.asi
                var fullPath = Path.Combine(gameDir, artifactName);
                if (!File.Exists(fullPath))
                    continue;

                if (_unambiguousResidueNames.Contains(Path.GetFileName(artifactName)))
                {
                    residues.Add(artifactName);
                    continue;
                }

                // Hash-match against any cached version
                var hash = TryComputeSha256(fullPath);
                if (hash != null && cacheHashes.Contains(hash))
                {
                    residues.Add(artifactName);
                }
            }

            return residues;
        }

        // ── Legacy migration ──────────────────────────────────────────────────────

        /// <summary>
        /// Migrates a legacy in-folder backup ({gameDir}/OptiScalerBackup/) to the external
        /// backup store. Does NOT delete the legacy folder — that happens on the next uninstall.
        /// Idempotent: if an external backup already exists for this gameDir, returns true immediately.
        /// </summary>
        public bool MigrateFromLegacy(string legacyManifestPath)
        {
            if (!File.Exists(legacyManifestPath))
                return false;

            InstallationManifest? manifest;
            try
            {
                var json = File.ReadAllText(legacyManifestPath);
                manifest = JsonSerializer.Deserialize(json, OptimizerContext.Default.InstallationManifest);
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[BackupStore][Migration] Could not parse manifest '{legacyManifestPath}': {ex.Message}");
                return false;
            }

            if (manifest == null)
                return false;

            // Determine gameDir from the manifest (authoritative path)
            var legacyBackupDir = Path.GetDirectoryName(legacyManifestPath)!;
            var gameDir = manifest.InstalledGameDirectory
                ?? Path.GetDirectoryName(legacyBackupDir);

            if (string.IsNullOrEmpty(gameDir))
                return false;

            // Already migrated?
            if (HasValidBackup(gameDir))
            {
                DebugWindow.Log($"[BackupStore][Migration] External backup already exists for '{gameDir}', skipping.");
                return true;
            }

            // Collect all backup file relative paths (union of both v1 and v2 tracking)
            var filesToCopy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in manifest.BackedUpFiles)
                filesToCopy.Add(f);
            foreach (var f in manifest.FilesOverwritten)
                filesToCopy.Add(f.BackupRelativePath ?? f.RelativePath);

            var filesDir = GetFilesDir(gameDir);
            Directory.CreateDirectory(filesDir);

            var copiedCount = 0;
            foreach (var relativePath in filesToCopy)
            {
                var src = Path.Combine(legacyBackupDir, relativePath);
                if (!File.Exists(src))
                    continue;

                var dst = Path.Combine(filesDir, relativePath);
                var dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir))
                    Directory.CreateDirectory(dstDir);

                File.Copy(src, dst, overwrite: true);

                // Verify copy
                if (new FileInfo(dst).Length != new FileInfo(src).Length)
                    throw new IOException($"[BackupStore][Migration] Size mismatch after copying '{relativePath}'");

                copiedCount++;
            }

            // Stamp manifest with migration metadata and save to external store
            manifest.MigrationSource = "legacy_v1";
            SaveManifest(gameDir, manifest);

            DebugWindow.Log($"[BackupStore][Migration] Migrated '{gameDir}': {copiedCount} backup file(s) moved to external store. Legacy folder preserved until next uninstall.");
            return true;
        }

        // ── Slug computation ──────────────────────────────────────────────────────

        /// <summary>
        /// Computes a deterministic, filesystem-safe identifier for a game directory.
        /// Format: {readable_suffix}_{8-char-hash}
        /// Example: "bin_x64_3f7a2c1d"
        /// </summary>
        public static string ComputeGameSlug(string gameDir)
        {
            var normalized = Path.GetFullPath(gameDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToLowerInvariant();

            // 8-char hex hash for uniqueness
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            var hash = Convert.ToHexString(hashBytes)[..8].ToLowerInvariant();

            // Human-readable suffix: last 2 path segments, sanitized
            var parts = normalized.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            var readable = string.Join("_", parts.TakeLast(2)
                .Select(p => Regex.Replace(p, @"[^\w\-]", "_")));

            return $"{readable}_{hash}";
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private static HashSet<string> BuildCacheHashSet(IEnumerable<string> cacheDirs)
        {
            var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cacheDir in cacheDirs)
            {
                if (!Directory.Exists(cacheDir))
                    continue;

                try
                {
                    foreach (var file in Directory.GetFiles(cacheDir, "*.*", SearchOption.AllDirectories))
                    {
                        var hash = TryComputeSha256(file);
                        if (hash != null)
                            hashes.Add(hash);
                    }
                }
                catch (Exception ex)
                {
                    DebugWindow.Log($"[BackupStore] Could not hash files in cache dir '{cacheDir}': {ex.Message}");
                }
            }
            return hashes;
        }

        private static string? TryComputeSha256(string filePath)
        {
            try
            {
                using var sha = SHA256.Create();
                using var stream = File.OpenRead(filePath);
                return Convert.ToHexString(sha.ComputeHash(stream));
            }
            catch
            {
                return null;
            }
        }
    }
}
