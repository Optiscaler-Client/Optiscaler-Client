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

namespace OptiscalerClient.Models;

/// <summary>One OptiScaler.ini value set by AmdNrBridgeService.ApplyAsync. Original is the value
/// before (null when the key was absent) — what AmdNrBridgeService.Remove puts back. Persisted on
/// Game.AmdNrBridgeIniChanges so an uninstall after an app restart can still restore them.</summary>
public class AmdNrBridgeIniChange
{
    public string Section { get; set; } = "";
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Original { get; set; }

    public AmdNrBridgeIniChange() { }

    public AmdNrBridgeIniChange(string section, string key, string value, string? original)
    {
        Section = section;
        Key = key;
        Value = value;
        Original = original;
    }
}
