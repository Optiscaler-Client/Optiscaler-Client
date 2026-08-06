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

namespace OptiscalerClient.Services;

/// <summary>
/// Resolves the app's per-user data directory (config, games.json, backups, cache, logs).
/// On Windows this is %AppData%\OptiscalerClient. On Linux, SpecialFolder.ApplicationData
/// already maps to $XDG_CONFIG_HOME (or ~/.config as fallback) via the .NET runtime, but if
/// neither HOME nor XDG_CONFIG_HOME is set (e.g. a minimal/headless environment) it can come
/// back empty, silently turning the path relative to the current directory. This centralizes
/// that resolution with an explicit fallback instead of every caller repeating the same logic.
/// </summary>
public static class AppPaths
{
    private static string? _cachedRoot;

    public static string GetAppDataRoot()
    {
        if (_cachedRoot != null) return _cachedRoot;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (string.IsNullOrEmpty(appData))
        {
            var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var home = Environment.GetEnvironmentVariable("HOME");

            appData = !string.IsNullOrEmpty(xdgConfigHome) ? xdgConfigHome
                    : !string.IsNullOrEmpty(home) ? Path.Combine(home, ".config")
                    : AppContext.BaseDirectory;
        }

        var dir = Path.Combine(appData, "OptiscalerClient");
        Directory.CreateDirectory(dir);

        _cachedRoot = dir;
        return dir;
    }
}
