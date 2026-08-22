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

        /// <summary>
        /// Custom amdxc64.dll needed by OptiScaler's LoadCustomAmdxc64OnRdna2 to work around AMD's
        /// driver-side GPU whitelist for the "current" build (amdxcffx64.dll) on RDNA2. Distinct from
        /// CurrentFileName despite the similar name — this one goes in OptiDllPath (".\OptiScaler\"),
        /// not next to the game's .exe, and is only picked up when an Extras release actually ships it.
        /// </summary>
        public const string CustomRdna2FileName = "amdxc64.dll";

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

        /// <summary>
        /// The 3 filenames the DLL-swap feature will look for/replace directly in a game's root
        /// folder: both names of the main FSR4 INT8 DLL, plus the RDNA2 companion. Deliberately a
        /// separate list from KnownFileNames — that one is used elsewhere to detect the Extras DLL
        /// as installed *through OptiScaler*, and must not start matching CustomRdna2FileName (which
        /// normally lives in ".\OptiScaler\", not the game root, and has a different source — see
        /// ComponentManagementService.GetCachedCustomAmdxc64Path vs. DownloadExtrasDllAsync).
        /// </summary>
        public static readonly string[] SwapTargetFileNames = { LegacyFileName, CurrentFileName, CustomRdna2FileName };

        /// <summary>Returns the full path to whichever swap-target name exists directly in the game's root, or null.</summary>
        public static string? FindSwapTargetIn(string gameDir)
        {
            foreach (var name in SwapTargetFileNames)
            {
                var path = Path.Combine(gameDir, name);
                if (File.Exists(path)) return path;
            }
            return null;
        }
    }
}
