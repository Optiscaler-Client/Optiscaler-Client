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

namespace OptiscalerClient.Services;

/// <summary>
/// Abstraction for OS-level shell operations (opening folders, URLs) so that
/// callers do not need inline <c>OperatingSystem.IsWindows()</c> checks.
/// </summary>
public interface IShellService
{
    /// <summary>Opens <paramref name="path"/> in the platform file manager.</summary>
    void OpenFolder(string path);

    /// <summary>Opens the containing folder of <paramref name="filePath"/> with that file selected/
    /// highlighted, where the platform supports it. Falls back to just opening the containing folder
    /// otherwise.</summary>
    void OpenFolderAndSelect(string filePath);

    /// <summary>Opens <paramref name="url"/> in the default browser.</summary>
    void OpenUrl(string url);
}
