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

    /// <summary>One parsed row from the OptiScaler wiki Compatibility List's main table.</summary>
    public class CompatibilityListEntry
    {
        public string GameName { get; set; } = "";
        public CompatibilityStatus Status { get; set; } = CompatibilityStatus.Unconfirmed;
        public string UpscalerInputs { get; set; } = "";
        public bool OptiPatcherSupported { get; set; }
        public string Notes { get; set; } = "";
    }

    /// <summary>Local cache of the parsed Compatibility List, persisted to disk between runs.</summary>
    public class CompatibilityListCache
    {
        public List<CompatibilityListEntry> Entries { get; set; } = new();
        public DateTime LastUpdated { get; set; }
    }
}
