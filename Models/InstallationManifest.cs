using System.Collections.Generic;

namespace OptiscalerClient.Models
{
    public class ManifestFileRecord
    {
        public string RelativePath { get; set; } = string.Empty;
        public string? BackupRelativePath { get; set; }
        public bool ExistedBefore { get; set; }
        public string? PreInstallSha256 { get; set; }
        public string? PostInstallSha256 { get; set; }
    }

    public class KeyFileSnapshot
    {
        public string RelativePath { get; set; } = string.Empty;
        public bool Existed { get; set; }
        public string? Sha256 { get; set; }
    }

    /// <summary>
    /// Manifest that tracks all files installed by OptiScaler for a specific game.
    /// This allows complete uninstallation without leaving residual files.
    /// </summary>
    public class InstallationManifest
    {
        /// <summary>
        /// Version of the manifest format (for future compatibility)
        /// </summary>
        public int ManifestVersion { get; set; } = 2;

        public string OperationId { get; set; } = string.Empty;
        public string OperationStatus { get; set; } = "committed";
        public string StartedAtUtc { get; set; } = string.Empty;
        public string FinishedAtUtc { get; set; } = string.Empty;

        /// <summary>
        /// OptiScaler version that was installed
        /// </summary>
        public string? OptiscalerVersion { get; set; }

        /// <summary>
        /// Injection method used (e.g., dxgi.dll, winmm.dll)
        /// </summary>
        public string InjectionMethod { get; set; } = string.Empty;

        /// <summary>
        /// Date and time of installation
        /// </summary>
        public string InstallDate { get; set; } = string.Empty;

        /// <summary>
        /// Absolute path of the directory where OptiScaler was physically installed.
        /// For UE5/Phoenix games this is the "Phoenix\Binaries\Win64" subdirectory,
        /// not the root InstallPath. Storing this avoids re-detection issues at uninstall time.
        /// </summary>
        public string? InstalledGameDirectory { get; set; }

        public bool IncludesOptiscaler { get; set; } = true;
        public bool IncludesFakenvapi { get; set; }
        public bool IncludesNukemFG { get; set; }
        public bool IncludesStreamline { get; set; }
        public bool IncludesDlssEnabler { get; set; }
        public bool IncludesExtras { get; set; }
        public bool IncludesOptiPatcher { get; set; }
        public string? FrameGenerationRouteApplied { get; set; }
        public string? FrameGenerationOutputApplied { get; set; }
        public string? MfgModeApplied { get; set; }
        public string? UpscalingQualityPresetApplied { get; set; }
        public double? UpscalingQualityRatioApplied { get; set; }
        public string? OutputUpscalerBackendApplied { get; set; }

        /// <summary>
        /// Name of the OptiScaler profile that was applied during installation
        /// </summary>
        public string? AppliedProfileName { get; set; }

        public List<string> ExpectedFinalMarkers { get; set; } = new();

        /// <summary>
        /// List of all files that were installed (relative paths from game directory)
        /// </summary>
        public List<string> InstalledFiles { get; set; } = new();

        /// <summary>
        /// List of files that were backed up (existed before installation)
        /// </summary>
        public List<string> BackedUpFiles { get; set; } = new();

        /// <summary>
        /// List of directories that were created during installation (relative paths from game directory)
        /// These will be deleted during uninstallation if they are empty
        /// </summary>
        public List<string> InstalledDirectories { get; set; } = new();

        public List<ManifestFileRecord> FilesCreated { get; set; } = new();
        public List<ManifestFileRecord> FilesOverwritten { get; set; } = new();
        public List<KeyFileSnapshot> PreInstallKeyFiles { get; set; } = new();

        /// <summary>
        /// Populated when this manifest was created by migrating a legacy in-folder backup.
        /// Null for fresh installations with v1.0.5+.
        /// </summary>
        public string? MigrationSource { get; set; }

        /// <summary>
        /// True when a FSR4 INT8 DLL was swapped directly into the game folder (independent of
        /// IncludesOptiscaler — a game can have either, or both, tracked in the same manifest).
        /// See GameInstallationService.SwapFsr4Dll.
        /// </summary>
        public bool IncludesDllSwap { get; set; }

        /// <summary>Name of the swapped-in file in the game root (one of Fsr4Int8DllHelper.SwapTargetFileNames).</summary>
        public string? DllSwapTargetFileName { get; set; }

        /// <summary>FSR4 INT8 version whose content was used for the swap.</summary>
        public string? DllSwapExtrasVersion { get; set; }
    }
}
