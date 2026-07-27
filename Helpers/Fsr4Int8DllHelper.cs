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

using System.IO;

namespace OptiscalerClient.Helpers
{
    /// <summary>
    /// AMD renamed the FSR4 INT8 mod DLL between releases (amd_fidelityfx_upscaler_dx12.dll -> amdxcffx64.dll).
    /// Centralizes the known names so every consumer recognizes either one.
    /// </summary>
    public static class Fsr4Int8DllHelper
    {
        public const string LegacyFileName = "amd_fidelityfx_upscaler_dx12.dll";
        public const string CurrentFileName = "amdxcffx64.dll";

        public static readonly string[] KnownFileNames = { LegacyFileName, CurrentFileName };

        public static bool IsKnownFileName(string fileName)
        {
            foreach (var name in KnownFileNames)
            {
                if (string.Equals(fileName, name, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Returns the full path to whichever known DLL name exists in the directory, or null.</summary>
        public static string? FindIn(string directory)
        {
            foreach (var name in KnownFileNames)
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        public static bool ExistsIn(string directory) => FindIn(directory) != null;
    }
}
