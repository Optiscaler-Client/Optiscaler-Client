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
    // The FSR 4 Swap mod DLL is one of the files _fsrNames detects as "FSR" (GameAnalyzerService),
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

    // "Setup NR" experimental feature — danielblnc's standalone AMD DLSS Neural Rendering mod.
    // Detected independently of IsOptiscalerInstalled: Option A (mod-only) never touches OptiScaler
    // at all, so this can be true while OptiScaler is absent, and vice versa for Option B where the
    // mod's own proxy is a transient install-time artifact, not the steady-state marker.
    public bool IsDlssNrOnAmdInstalled { get; set; }
    public string? DlssNrOnAmdVersion { get; set; }

    // Which mode ("daniel-only" or "daniel-and-opti") IsDlssNrOnAmdInstalled actually reflects. Needed
    // because the two modes are otherwise indistinguishable after the fact from detection alone — mode
    // A never installs OptiScaler and mode B does, but a game can also have a completely unrelated
    // normal OptiScaler install, so IsOptiscalerInstalled alone can't be used to infer which mode ran.
    // Set alongside IsDlssNrOnAmdInstalled on a successful install, cleared on uninstall.
    public string? InstalledDlssNrOnAmdMode { get; set; }

    // Set when "Setup NR" was saved but the actual wizard (staging + running danielblnc's
    // interactive installer) hasn't run yet — that step is deferred to the main Install button.
    // "daniel-only" or "daniel-and-opti"; null when no Setup NR run is pending.
    public string? PendingDlssNrOnAmdMode { get; set; }
    public string? PendingDlssNrOnAmdVersion { get; set; }

    // Drives the game-card badge (MainWindow.axaml) — "mod only" is the case worth calling out there,
    // since it means OptiScaler itself isn't installed even though DLSS upscaling is active via the
    // mod. "daniel-and-opti" already shows as a normal OptiScaler install everywhere else.
    public bool IsDlssNrOnAmdModOnly => IsDlssNrOnAmdInstalled && InstalledDlssNrOnAmdMode == "daniel-only";

    // True once Setup NR believes this game's folder is excluded from Defender — either because
    // WindowsDefenderExclusionHelper.TryAddExclusions actually succeeded (see
    // DlssNrDefenderExclusionVerified below), or because the user clicked "Continue" on the offer
    // dialog, taking their own word for it (there's no reliable unprivileged way to confirm an
    // exclusion actually exists — Get-MpPreference refuses to report it to a non-admin process).
    public bool DlssNrDefenderExclusionAdded { get; set; }

    // True only when DlssNrDefenderExclusionAdded came from TryAddExclusions actually succeeding —
    // i.e. we know for a fact the exclusion exists, as opposed to just trusting the user's "Continue".
    // A failed Setup NR install resets DlssNrDefenderExclusionAdded to re-ask next time UNLESS this is
    // true, since a verified exclusion can't be the reason a later run failed the same way an unverified
    // "take my word for it" one plausibly could.
    public bool DlssNrDefenderExclusionVerified { get; set; }

    // True when a FSR 4 Swap DLL was swapped directly into the game folder without installing
    // OptiScaler (independent of IsOptiscalerInstalled — both can be true at once). Fsr4ExtraVersion
    // above doubles as "which version" for this too, whether injected via OptiScaler or swapped raw.
    public bool IsFsr4DllSwapped { get; set; }
    public string? Fsr4DllSwapTargetFileName { get; set; }

    /// <summary>Optional FG settings applied specifically to this game, never to a shared profile.</summary>
    public GameFrameGenerationSettings? FrameGenerationSettings { get; set; }

    /// <summary>Optional upscaling quality override applied specifically to this game.</summary>
    public GameUpscalingQualitySettings? UpscalingQualitySettings { get; set; }

    /// <summary>Optional output-upscaler backend override applied specifically to this game.</summary>
    public GameOutputUpscalerSettings? OutputUpscalerSettings { get; set; }
    public bool HasUpscaler => DlssVersion != null || DlssFrameGenVersion != null || FsrVersion != null || XessVersion != null || IsOptiscalerInstalled;

    // UI customization (not set by scanner)
    public bool IsHidden { get; set; } = false;
    public bool IsFavorite { get; set; } = false;
    public int DisplayOrder { get; set; } = 0;
}
