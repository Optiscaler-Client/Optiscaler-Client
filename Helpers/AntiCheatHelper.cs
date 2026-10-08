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
using System.IO;
using System.Linq;

namespace OptiscalerClient.Helpers
{
    /// <summary>
    /// Single source of truth for anti-cheat detection, shared by the analyzer (Games view badge),
    /// the Manage warning, Quick/Bulk Install confirmations and Frame Generation's safety gate.
    /// </summary>
    public static class AntiCheatHelper
    {
        public static readonly string[] Files =
        [
            // EasyAntiCheat / BattlEye
            "start_protected_game.exe", "EasyAntiCheat_EOS.exe", "EasyAntiCheat_x64.dll",
            "BEService.exe", "BEService_x64.exe", "BEClient_x64.dll",
            // Denuvo Anti-Cheat / Elytra (THE FINALS)
            "AntiCheatInstaller.exe", "Elytra-Setup.msi"
        ];

        public static bool IsAntiCheatFile(string fileName) =>
            Files.Contains(fileName, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Root Steam install scripts (*.vdf) declare any anti-cheat Steam must install (THE FINALS:
        /// "Install Denuvo Anti-Cheat") - catches vendors not listed in Files.
        /// </summary>
        public static bool DeclaredInSteamScript(string root)
        {
            try
            {
                return Directory.EnumerateFiles(root, "*.vdf").Any(vdf =>
                {
                    var text = File.ReadAllText(vdf);
                    return text.Contains("anticheat", StringComparison.OrdinalIgnoreCase) ||
                           text.Contains("anti-cheat", StringComparison.OrdinalIgnoreCase) ||
                           text.Contains("battleye", StringComparison.OrdinalIgnoreCase);
                });
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Recursive: anti-cheat files often sit in subfolders (e.g. EasyAntiCheat\, Installers\).</summary>
        public static bool IsPresent(string? root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return false;
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                return Directory.EnumerateFiles(root, "*", options).Any(f => IsAntiCheatFile(Path.GetFileName(f)))
                       || DeclaredInSteamScript(root);
            }
            catch
            {
                return false;
            }
        }
    }
}
