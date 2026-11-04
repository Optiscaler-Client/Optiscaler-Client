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
using OptiscalerClient.Models;
using OptiscalerClient.Views;

namespace OptiscalerClient.Services
{
    /// <summary>
    /// Runs once per app version on startup to migrate legacy in-folder backups
    /// ({gameDir}/OptiScalerBackup/) to the external backup store under %APPDATA%.
    /// The migration is idempotent and non-destructive: the legacy folder is NOT
    /// removed here — it is cleaned up on the first uninstall with v1.0.5+.
    /// </summary>
    public class StartupMigrationService
    {
        private const string LegacyBackupFolderName = "OptiScalerBackup";
        private const string LegacyManifestFileName = "optiscaler_manifest.json";

        private readonly BackupStoreService _backupStore;
        private readonly ComponentManagementService _componentService;

        public StartupMigrationService(BackupStoreService backupStore, ComponentManagementService componentService)
        {
            _backupStore = backupStore;
            _componentService = componentService;
        }

        /// <summary>
        /// Runs the migration pass if it hasn't been completed for the current app version.
        /// Updates AppConfiguration.LastMigratedAppVersion on success.
        /// </summary>
        public void RunIfNeeded(IEnumerable<Game> persistedGames)
        {
            var config = _componentService.Config;

            // Skip if already migrated for this version
            if (config.LastMigratedAppVersion == App.AppVersion)
                return;

            DebugWindow.Log($"[Migration] Starting backup migration pass for app version {App.AppVersion}...");

            var migratedCount = 0;
            var failedCount = 0;

            foreach (var game in persistedGames)
            {
                if (!game.IsOptiscalerInstalled)
                    continue;

                try
                {
                    if (TryMigrateGame(game))
                        migratedCount++;
                }
                catch (Exception ex)
                {
                    failedCount++;
                    DebugWindow.Log($"[Migration] Failed to migrate '{game.Name}': {ex.Message}");
                    // Non-fatal: a single game failing does not block migration of others.
                }
            }

            // Record completion regardless of individual failures.
            // Games that failed will be retried on the next app version bump,
            // or can fall back to the legacy path for uninstall.
            config.LastMigratedAppVersion = App.AppVersion;
            _componentService.SaveConfiguration();

            DebugWindow.Log($"[Migration] Pass complete. Migrated={migratedCount}, Skipped/Failed={failedCount}.");
        }

        // ── Private ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Attempts to find and migrate the legacy backup for a single game.
        /// Returns true if a migration was performed or if none was needed.
        /// </summary>
        private bool TryMigrateGame(Game game)
        {
            // Search for the legacy manifest recursively from the game's root directory,
            // mirroring the existing UninstallOptiScaler() search logic.
            var searchRoot = string.IsNullOrEmpty(game.ExecutablePath)
                ? game.InstallPath
                : Path.GetDirectoryName(game.ExecutablePath) ?? game.InstallPath;

            if (string.IsNullOrEmpty(searchRoot) || !Directory.Exists(searchRoot))
                return false;

            // Early-skip: if an external backup already exists for this root, no migration is needed.
            if (_backupStore.HasValidBackup(searchRoot))
                return true;

            string? legacyManifestPath = null;
            try
            {
                // Bounded recursive enumeration: cap depth and stop at the first match (early-skip)
                // instead of materialising every file under potentially huge game trees.
                var opts = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MatchCasing = MatchCasing.CaseInsensitive,
                    MaxRecursionDepth = 8,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
                };
                legacyManifestPath = Directory.EnumerateFiles(searchRoot, LegacyManifestFileName, opts).FirstOrDefault();
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[Migration] Could not search for legacy manifest in '{searchRoot}': {ex.Message}");
                return false;
            }

            if (legacyManifestPath == null)
                return false; // No legacy backup — nothing to migrate

            return _backupStore.MigrateFromLegacy(legacyManifestPath);
        }
    }
}
