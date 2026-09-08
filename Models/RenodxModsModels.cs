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

namespace OptiscalerClient.Models
{
    /// <summary>One parsed row from the RenoDX wiki's Mods page (clshortfuse/renodx/wiki/Mods).</summary>
    public class RenodxModEntry
    {
        public string GameName { get; set; } = "";

        /// <summary>Direct download URL to the .addon64/.addon32 file, from the "Snapshot" badge
        /// link. Null if the row has no Snapshot link (Nexus-only or no link at all) — those
        /// entries can't be auto-downloaded, treated the same as "no match" by the install flow.</summary>
        public string? SnapshotUrl { get; set; }

        /// <summary>Reference only, not used by the automatic install flow.</summary>
        public string? NexusUrl { get; set; }
    }

    /// <summary>Local cache of the parsed RenoDX Mods list, persisted to disk between runs.</summary>
    public class RenodxModsCache
    {
        public List<RenodxModEntry> Entries { get; set; } = new();
        public DateTime LastUpdated { get; set; }
        public int SchemaVersion { get; set; }
    }

    /// <summary>
    /// One RenoDX addon cached locally, associated with a specific game (unlike every other
    /// component cache in this app, which is keyed by an arbitrary version name reusable across
    /// games). See ComponentManagementService.GetRenodxCachePath.
    /// </summary>
    public class RenodxCacheEntry
    {
        /// <summary>Sanitized key used for the cache folder name — either the RenoDX wiki's own
        /// matched game name, or the local Game.Name if added manually for a game the wiki
        /// doesn't list.</summary>
        public string GameKey { get; set; } = "";
        public string DisplayName { get; set; } = "";
        /// <summary>The real .addon64/.addon32 filename, preserved as-is from the source.</summary>
        public string FileName { get; set; } = "";
        public DateTime CachedUtc { get; set; }
    }

    public class RenodxCache
    {
        public List<RenodxCacheEntry> Entries { get; set; } = new();
    }
}
