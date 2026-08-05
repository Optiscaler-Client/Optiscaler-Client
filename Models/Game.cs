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

public enum GamePlatform
{
    Steam,
    Epic,
    GOG,
    Xbox,
    EA,
    BattleNet,
    Ubisoft,
    Lutris,
    Manual,
    Custom
}

public class Game
{
    public string Name { get; set; } = string.Empty;
    public string InstallPath { get; set; } = string.Empty;
    public GamePlatform Platform { get; set; }
    public bool IsManual => Platform == GamePlatform.Manual;
    public string AppId { get; set; } = string.Empty; // Steam AppId or Epic ItemId
    public string ExecutablePath { get; set; } = string.Empty; // Path to main .exe (if detectable)

    public string? CoverImageUrl { get; set; }

    // Detected Technologies
    public string? DlssVersion { get; set; }
    public string? DlssPath { get; set; }

    public string? DlssFrameGenVersion { get; set; }
    public string? DlssFrameGenPath { get; set; }

    public string? FsrVersion { get; set; }
    public string? FsrPath { get; set; }

    public string? XessVersion { get; set; }
    public string? XessPath { get; set; }

    public bool IsOptiscalerInstalled { get; set; }
    public string? OptiscalerVersion { get; set; }
    public string? Fsr4ExtraVersion { get; set; }

    public bool HasUpscaler => DlssVersion != null || DlssFrameGenVersion != null || FsrVersion != null || XessVersion != null || IsOptiscalerInstalled;

    // UI customization (not set by scanner)
    public bool IsHidden { get; set; } = false;
    public bool IsFavorite { get; set; } = false;
    public int DisplayOrder { get; set; } = 0;
}
