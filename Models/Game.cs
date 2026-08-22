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

// Values are persisted as raw integers in games.json (no string enum converter is
// registered), so they must stay pinned. Adding a new platform must always append a
// new explicit value at the end — inserting one in the middle silently reinterprets
// every previously-saved game's Platform as a different platform (see the Lutris
// insertion between 1.0.5 and 1.0.6, which turned old Manual games into Lutris ones).
public enum GamePlatform
{
    Steam = 0,
    Epic = 1,
    GOG = 2,
    Xbox = 3,
    EA = 4,
    BattleNet = 5,
    Ubisoft = 6,
    Lutris = 7,
    Manual = 8,
    Custom = 9
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

    // True when the corresponding *Version above came from a file OptiScaler itself installed
    // rather than one the game shipped natively. The version is still populated either way.
    public bool DlssViaOptiscaler { get; set; }
    public bool FsrViaOptiscaler { get; set; }
    public bool XessViaOptiscaler { get; set; }

    public bool DlssIsNative => DlssVersion != null && !DlssViaOptiscaler;
    // The FSR4 INT8 mod DLL is one of the files _fsrNames detects as "FSR" (GameAnalyzerService),
    // so a swap makes FsrVersion/FsrIsNative light up exactly like a game-native FSR install would
    // — misleading, since it's neither native nor a straightforward OptiScaler injection. IsSwapped
    // gets its own badge state (see MainWindow.axaml/ManageGameWindow "Detected Components") and is
    // carved out of "native" so the two don't both claim the same file.
    public bool FsrIsSwapped => FsrVersion != null && IsFsr4DllSwapped;
    // Raw FsrViaOptiscaler doesn't know about swaps (it's just "OptiScaler's manifest owns this
    // file"), so if OptiScaler was installed first and the DLL got swapped afterwards, both this
    // and FsrIsSwapped would otherwise be true for the same physical file — two badges for one
    // thing. FsrIsSwapped wins; this is what the "via OptiScaler" badge should actually bind to.
    public bool FsrIsViaOptiscalerOnly => FsrVersion != null && FsrViaOptiscaler && !IsFsr4DllSwapped;
    public bool FsrIsNative => FsrVersion != null && !FsrViaOptiscaler && !IsFsr4DllSwapped;
    public bool XessIsNative => XessVersion != null && !XessViaOptiscaler;

    public bool IsOptiscalerInstalled { get; set; }
    public string? OptiscalerVersion { get; set; }
    public string? Fsr4ExtraVersion { get; set; }

    // True when a FSR4 INT8 DLL was swapped directly into the game folder without installing
    // OptiScaler (independent of IsOptiscalerInstalled — both can be true at once). Fsr4ExtraVersion
    // above doubles as "which version" for this too, whether injected via OptiScaler or swapped raw.
    public bool IsFsr4DllSwapped { get; set; }
    public string? Fsr4DllSwapTargetFileName { get; set; }

    public bool HasUpscaler => DlssVersion != null || DlssFrameGenVersion != null || FsrVersion != null || XessVersion != null || IsOptiscalerInstalled;

    // UI customization (not set by scanner)
    public bool IsHidden { get; set; } = false;
    public bool IsFavorite { get; set; } = false;
    public int DisplayOrder { get; set; } = 0;
}
