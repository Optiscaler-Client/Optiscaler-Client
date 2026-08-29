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
    /// <summary>
    /// Mirrors the "Compatibility" column emoji used in the OptiScaler wiki's Compatibility List.
    /// Unconfirmed is the safe fallback for any symbol the parser doesn't recognize.
    /// </summary>
    public enum CompatibilityStatus
    {
        Unconfirmed,
        Compatible,
        NotCompatible,
        SingleOsOnly
    }

    /// <summary>One parsed row from either table on the OptiScaler wiki Compatibility List.</summary>
    public class CompatibilityListEntry
    {
        public string GameName { get; set; } = "";
        public CompatibilityStatus Status { get; set; } = CompatibilityStatus.Unconfirmed;
        public string UpscalerInputs { get; set; } = "";
        public bool OptiPatcherSupported { get; set; }
        public string Notes { get; set; } = "";

        /// <summary>
        /// True when the game originates from the Compatibility List's "Luma Unreal Engine"
        /// table. Luma entries remain part of the same unified compatibility list, while this
        /// flag preserves their engine/mod context for future recommendations.
        /// </summary>
        public bool IsLumaUnrealEngine { get; set; }

        /// <summary>
        /// The game's individual wiki page slug (e.g. "Hogwarts-Legacy"), taken from the main
        /// table's own link to it. Empty if the row had no link or used a format this couldn't
        /// parse - callers must treat that as "no per-game page available", not retry-worthy.
        /// </summary>
        public string WikiPageSlug { get; set; } = "";
    }

    /// <summary>Local cache of the parsed Compatibility List, persisted to disk between runs.</summary>
    public class CompatibilityListCache
    {
        public List<CompatibilityListEntry> Entries { get; set; } = new();
        public DateTime LastUpdated { get; set; }

        /// <summary>
        /// Bumped whenever CompatibilityListEntry gains a field that's worth re-fetching for
        /// (e.g. WikiPageSlug). A file on disk from before that bump deserializes with this at 0
        /// (the type's default, since the property didn't exist yet to serialize), which
        /// CompatibilityListService.CheckForUpdatesAsync treats as "not usable" so it force-refreshes
        /// once on next launch instead of silently sitting on stale/incomplete data for up to 24h.
        /// </summary>
        public int SchemaVersion { get; set; }
    }

    /// <summary>
    /// The handful of fields worth showing from a game's individual wiki page (source format is
    /// AsciiDoc, not Markdown - see CompatibilityListService.ParseGameWikiPage). Everything else
    /// on that page (Known Issues/Notes prose, OS, GPU, Reported By) is too free-form to parse
    /// reliably and is left for the user to read on the actual wiki page instead.
    /// </summary>
    public class GameWikiDetails
    {
        public string LastTestedVersion { get; set; } = "";
        public string Filename { get; set; } = "";
        public string UpscalerInputs { get; set; } = "";
        public string FgInputs { get; set; } = "";
        public int KnownIssuesCount { get; set; }
        public string PageUrl { get; set; } = "";
    }

    /// <summary>One cached, timestamped fetch of a game's individual wiki page.</summary>
    public class GameWikiDetailsCacheEntry
    {
        public GameWikiDetails Details { get; set; } = new();
        public DateTime FetchedUtc { get; set; }
    }

    /// <summary>
    /// Local cache of per-game wiki page details, keyed by wiki page slug. Populated lazily (only
    /// for games the user has actually opened Manage Game for), unlike the eagerly-refreshed main
    /// CompatibilityListCache - see CompatibilityListService.GetGameWikiDetailsAsync.
    /// </summary>
    public class GameWikiDetailsCache
    {
        public Dictionary<string, GameWikiDetailsCacheEntry> BySlug { get; set; } = new();
    }
}
