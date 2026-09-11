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
using System.Collections.Generic;

namespace OptiscalerClient.Models
{
    /// <summary>
    /// Controls how the scanner handles games that have no detectable upscaler DLLs.
    /// </summary>
    public enum UpscalerFilterMode
    {
        /// <summary>Scan and show all games normally.</summary>
        ShowAll = 0,
        /// <summary>Scan all games, but automatically hide those without detectable upscalers.
        /// Hidden games remain in the list and can be un-hidden from Organize mode.</summary>
        HideWithoutUpscaler = 1,
        /// <summary>Do not add games that have no detectable DLSS / FSR / XeSS DLLs to the list.
        /// Warning: some games expose their upscaler DLLs outside the standard detection path
        /// and will not appear even if they do support upscaling.</summary>
        SkipWithoutUpscaler = 2,
    }

    /// <summary>
    /// Network and proxy configuration.
    /// </summary>
    public class NetworkConfig
    {
        /// <summary>When true (default), the OS proxy / HTTP_PROXY env vars are used. When false, the explicit settings below apply.</summary>
        public bool UseSystemProxy { get; set; } = true;
        /// <summary>Proxy protocol: "HTTPS" (HTTP CONNECT tunneling) or "SOCKS5".</summary>
        public string ProxyType { get; set; } = "HTTPS";
        /// <summary>Proxy server hostname or IP address.</summary>
        public string? ProxyHost { get; set; } = null;
        /// <summary>Proxy server port number.</summary>
        public int? ProxyPort { get; set; } = null;
        /// <summary>Whether the proxy requires authentication credentials.</summary>
        public bool ProxyRequiresAuth { get; set; } = false;
        /// <summary>Username for authenticated proxies.</summary>
        public string? ProxyUsername { get; set; } = null;
        /// <summary>Password for authenticated proxies. Stored in plaintext; use OS keyring in a future iteration.</summary>
        public string? ProxyPassword { get; set; } = null;
    }

    /// <summary>
    /// Configuration for GitHub repositories
    /// </summary>
    public class RepositoryConfig
    {
        public string RepoOwner { get; set; } = string.Empty;
        public string RepoName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Configuration for scan sources
    /// </summary>
    public class ScanSourcesConfig
    {
        public bool ScanSteam { get; set; } = true;
        public bool ScanHeroic { get; set; } = true;
        public bool ScanEpic { get; set; } = true;
        public bool ScanGOG { get; set; } = true;
        public bool ScanXbox { get; set; } = true;
        public bool ScanEA { get; set; } = true;
        public bool ScanUbisoft { get; set; } = true;
        public bool ScanLutris { get; set; } = true;
        public List<string> CustomFolders { get; set; } = new();
        public UpscalerFilterMode UpscalerFilter { get; set; } = UpscalerFilterMode.ShowAll;
    }

    /// <summary>
    /// Root configuration containing all repository configurations
    /// </summary>
    public class AppConfiguration
    {
        public RepositoryConfig App { get; set; } = new();
        public RepositoryConfig OptiScaler { get; set; } = new();
        public RepositoryConfig OptiScalerBetas { get; set; } = new();
        public RepositoryConfig OptiScalerNightly { get; set; } = new();
        public RepositoryConfig Streamline { get; set; } = new();
        public RepositoryConfig OptiScalerExtras { get; set; } = new();
        public RepositoryConfig OptiScalerExtrasFp8 { get; set; } = new();
        public RepositoryConfig Fakenvapi { get; set; } = new();
        public RepositoryConfig NukemFG { get; set; } = new();
        public RepositoryConfig OptiPatcher { get; set; } = new();
        public RepositoryConfig DlssEnablerMirror { get; set; } = new();
        public string Language { get; set; } = "en";
        public bool Debug { get; set; } = false;
        public string DefaultProfileName { get; set; } = OptiScalerProfile.BuiltInDefaultName;
        public bool AutoScan { get; set; } = true;
        public bool AnimationsEnabled { get; set; } = true;
        public bool PreferGridView { get; set; } = true;

        // Live game-list filters (main view, persisted across sessions)
        public bool HideGamesWithoutUpscaler { get; set; } = false;
        public bool ShowOnlyInstalled { get; set; } = false;
        public bool ShowOnlyFavorites { get; set; } = false;
        public string? DefaultGpuId { get; set; } = null;
        public bool HasShownInitialScanPrompt { get; set; } = false;
        public bool HasCompletedInitialScan { get; set; } = false;
        public List<string> ScanDriveRoots { get; set; } = new();

        // Window state persistence
        public double WindowWidth { get; set; } = 1200;
        public double WindowHeight { get; set; } = 720;
        public bool WindowMaximized { get; set; } = false;
        public double WindowLeft { get; set; } = double.NaN;
        public double WindowTop { get; set; } = double.NaN;
        /// <summary>
        /// The default FSR 4 DLL version to pre-select in ManageGameWindow.
        /// Null or "none" means "do not inject".
        /// </summary>
        public string? DefaultExtrasVersion { get; set; } = null;
        /// <summary>
        /// The default OptiScaler version to pre-select in ManageGameWindow / Quick Install.
        /// Null or "auto" means let the app choose the recommended/latest version automatically.
        /// </summary>
        public string? DefaultOptiScalerVersion { get; set; } = null;
        /// <summary>
        /// When true (default), the OptiScaler version pre-selected everywhere always tracks the latest
        /// available release (even if not downloaded yet) instead of a pinned DefaultOptiScalerVersion.
        /// </summary>
        public bool AutoLatestOptiScalerDefault { get; set; } = true;
        /// <summary>
        /// Which channel AutoLatestOptiScalerDefault tracks: "stable" (default), "beta", or "nightly".
        /// Set from whichever tab was showing in Manage Default Versions when "Latest version
        /// available" was saved — without this, "auto" always meant latest STABLE regardless of which
        /// tab the user had open, silently discarding an explicit Beta/Nightly choice.
        /// </summary>
        public string DefaultOptiScalerChannel { get; set; } = "stable";
        /// <summary>
        /// The default OptiPatcher version to pre-select in ManageGameWindow / Quick Install.
        /// Null or "none" means "do not install".
        /// </summary>
        public string? DefaultOptiPatcherVersion { get; set; } = null;
        /// <summary>
        /// The default Fakenvapi version to pre-select in ManageGameWindow / Quick Install.
        /// Null or "none" means "do not install".
        /// </summary>
        public string? DefaultFakenvapiVersion { get; set; } = null;
        /// <summary>
        /// The default NukemFG version to pre-select in ManageGameWindow / Quick Install.
        /// Null or "none" means "do not install".
        /// </summary>
        public string? DefaultNukemFGVersion { get; set; } = null;
        /// <summary>
        /// Shows the RenoDX selector in ManageGameWindow and the "renodx" section in
        /// CacheManagementWindow. Purely a visibility switch — does not install, enable, or
        /// configure anything by itself.
        /// </summary>
        public bool ShowExperimentalFeatures { get; set; } = false;
        /// <summary>
        /// The last RenoDX selection made in ManageGameWindow ("none", "auto", or a cached addon's
        /// full file path), pre-selected the next time any game's Manage window opens. Global like
        /// DefaultFakenvapiVersion/DefaultNukemFGVersion above rather than per-game — a saved file
        /// path only ever matches a combo item when the addon happens to be cached for that same
        /// game, so per-game correctness falls out naturally without a per-game dictionary.
        /// </summary>
        public string? DefaultRenodxVersion { get; set; } = null;
        /// <summary>
        /// The default injection DLL name to pre-select in ManageGameWindow. Null means "auto"
        /// (resolved per-game from the compatibility list, falling back to dxgi.dll).
        /// </summary>
        public string? DefaultInjectionMethod { get; set; } = null;
        /// <summary>
        /// Default GPU spoofing override ("true"/"false"), applied to OptiScaler's Dxgi,
        /// StreamlineSpoofing and VulkanExtensionSpoofing keys together — see
        /// GameInstallationService.InstallOptiScaler's dxgiSpoofing parameter. Null/"auto" means
        /// OptiScaler's own per-vendor default, used by Quick Install when the user hasn't pinned one
        /// in "Default Versions &amp; Quick Install Settings".
        /// </summary>
        public string? DefaultDxgiSpoofing { get; set; } = null;
        /// <summary>
        /// Default AMD DLSS Neural Rendering ("Setup NR") mode: "none" (default), "daniel-only", or
        /// "daniel-and-opti". Pre-selects ManageGameWindow's Setup NR combo for a never-touched game,
        /// and drives whether Quick Install / Bulk Install install the mod automatically — see
        /// DlssNrOnAmdService.InstallForQuickPathAsync. Only ever offered in Settings when the
        /// configured default GPU is AMD AND ShowExperimentalFeatures is on, so every consumer of
        /// this field must also re-check ShowExperimentalFeatures itself — turning the experimental
        /// switch off must disable this default's effect immediately, not just hide its UI.
        /// </summary>
        public string DefaultDlssNrOnAmdMode { get; set; } = "none";
        /// <summary>Pinned danielblnc/DLSS-NR-on-AMD release for DefaultDlssNrOnAmdMode. Null/not found
        /// in the current release list means "use the latest available" at install time.</summary>
        public string? DefaultDlssNrOnAmdDanielVersion { get; set; } = null;
        /// <summary>Pinned MatheusGViana/dlss-5-amd-project (the "Modded" OptiScaler wrapper) release,
        /// only meaningful when DefaultDlssNrOnAmdMode is "daniel-and-opti". Null/not found means "use
        /// the latest available" at install time.</summary>
        public string? DefaultDlssNrOnAmdWrapperVersion { get; set; } = null;
        /// <summary>
        /// The default upscaling quality preset to pre-select in ManageGameWindow. Null means
        /// "Game controlled" (no override).
        /// </summary>
        public UpscalingQualityPreset? DefaultUpscalingQualityPreset { get; set; } = null;
        /// <summary>Custom render-scale ratio, only used when DefaultUpscalingQualityPreset is Custom.</summary>
        public double? DefaultUpscalingCustomRatio { get; set; } = null;
        /// <summary>
        /// The default output upscaler backend to pre-select in ManageGameWindow. Null means "Default"
        /// (no override).
        /// </summary>
        public OutputUpscalerBackend? DefaultOutputUpscalerBackend { get; set; } = null;
        /// <summary>
        /// The default Frame Generation configuration to pre-select in ManageGameWindow for a game
        /// with no saved FG settings of its own. Null means the built-in defaults (Disabled/Auto/Auto).
        /// </summary>
        public GameFrameGenerationSettings? DefaultFrameGenerationSettings { get; set; } = null;

        /// <summary>True (default) = the FSR4 DLL swap always prompts which packaged files to
        /// copy/replace. False = use Fsr4SwapDefaultFileKeys silently instead — see Fsr4SwapOptionsWindow.</summary>
        public bool Fsr4SwapAskEveryTime { get; set; } = true;

        /// <summary>Fsr4Int8DllHelper.LogicalFileKeys the user pre-selected when Fsr4SwapAskEveryTime
        /// is false. Empty means "never configured" and is treated as "all" by FilterCandidatesByDefaultKeys.</summary>
        public List<string> Fsr4SwapDefaultFileKeys { get; set; } = new();
        public ScanSourcesConfig ScanSources { get; set; } = new();
        public string SteamGridDBApiKey { get; set; } = string.Empty;
        public List<ScanExclusion> ScanExclusions { get; set; } = new();
        /// <summary>
        /// Names/labels of custom OptiScaler versions imported by the user.
        /// Each entry corresponds to a subdirectory under Cache/OptiScaler/.
        /// </summary>
        public List<string> CustomOptiScalerVersions { get; set; } = new();

        /// <summary>
        /// Names/labels of custom FSR 4 DLL packages imported by the user.
        /// Each entry corresponds to a subdirectory under Cache/Extras/.
        /// </summary>
        public List<string> CustomExtrasVersions { get; set; } = new();

        /// <summary>
        /// Variant selected when each custom FSR 4 DLL package was imported. Old custom packages
        /// intentionally default to INT8 when this metadata is absent, preserving their behavior.
        /// </summary>
        public Dictionary<string, Fsr4DllVariant> CustomExtrasVariants { get; set; } = new();

        /// <summary>Network and proxy settings.</summary>
        public NetworkConfig Network { get; set; } = new();

        /// <summary>
        /// Version of the app on which the last startup migration pass completed.
        /// When this matches the current AppVersion, the migration step is skipped entirely.
        /// </summary>
        public string? LastMigratedAppVersion { get; set; } = null;

        /// <summary>
        /// Version of the app that was running when the user last saw the welcome/changelog popup.
        /// When this differs from the current AppVersion, the welcome window is shown again.
        /// </summary>
        public string? LastSeenAppVersion { get; set; } = null;

        /// <summary>
        /// UTC timestamp of the last successful GitHub API check.
        /// Persisted so that the 15-minute cooldown survives app restarts.
        /// </summary>
        public DateTime? LastApiCheckTime { get; set; } = null;

        /// <summary>
        /// UTC timestamp of the last Compatibility List refresh attempt.
        /// Persisted so that the 24-hour cooldown survives app restarts. Recorded before the
        /// request is made, so a failed attempt doesn't retry on every app launch.
        /// </summary>
        public DateTime? LastCompatListCheckTime { get; set; } = null;

        /// <summary>UTC timestamp of the last RenoDX Mods wiki refresh attempt (see
        /// RenodxModsService) — same 24h cooldown convention as LastCompatListCheckTime.</summary>
        public DateTime? LastRenodxModsCheckTime { get; set; } = null;

        /// <summary>
        /// Latest GitHub release version the "Update Available" popup has already notified
        /// the user about. When a newer version than this is found, the popup is shown again.
        /// </summary>
        public string? LastNotifiedUpdateVersion { get; set; } = null;

        /// <summary>
        /// Whether the "Update Available" popup for <see cref="LastNotifiedUpdateVersion"/> has
        /// already been shown and dismissed. If false (e.g. the app closed before it was seen),
        /// the popup is shown again on next startup even if the latest version hasn't changed.
        /// </summary>
        public bool UpdateNotificationDismissed { get; set; } = true;

        /// <summary>
        /// User's rendering backend preference: "hardware" (default) uses hardware acceleration
        /// normally but automatically falls back to software rendering after repeated crashes on
        /// startup (see RenderingSafetyNet) — this is how a GPU driver crash that bypasses normal
        /// .NET exception handling gets self-healed without the user having to find a setting.
        /// "software" pins the choice regardless of crash history. There is deliberately no
        /// "hardware, never self-heal" option — see RenderingSafetyNet's class remarks.
        /// </summary>
        public string RenderingModePreference { get; set; } = "hardware";

        /// <summary>
        /// True once the app has already fallen back to software rendering because of repeated
        /// unclean shutdowns. Kept sticky (never auto-reverted) so a user whose driver issue is
        /// later fixed has to explicitly switch back via RenderingModePreference, rather than the
        /// app silently re-exposing them to the same crash on some future launch.
        /// </summary>
        public bool ForcedSoftwareRenderingActive { get; set; } = false;

        public bool NotifiedAboutSoftwareRendering { get; set; } = false;

        /// <summary>
        /// Consecutive app launches that did not reach a clean shutdown (see RunInProgress).
        /// Reset to 0 on any clean exit. Two in a row (not one, to avoid misfiring on a single
        /// Task Manager kill or power loss) triggers the automatic software-rendering fallback.
        /// </summary>
        public int UncleanShutdownStreak { get; set; } = 0;

        /// <summary>
        /// Set to true at every startup and cleared only on a clean shutdown (AppDomain.ProcessExit
        /// in Program.cs). Finding this still true at the next startup means the previous run
        /// never got there — most likely a native crash (e.g. a GPU driver crashing the rendering
        /// thread), since both normal exits and handled managed exceptions still reach ProcessExit.
        /// </summary>
        public bool RunInProgress { get; set; } = false;

        /// <summary>
        /// Caps the Avalonia compositor's render loop (frames per second). The UI doesn't need
        /// to redraw faster than this even when idle, and an uncapped loop on high-refresh
        /// monitors can fight with VRR (visible flicker as the panel's refresh window jitters).
        /// </summary>
        public int RenderFpsLimit { get; set; } = 60;
    }

    /// <summary>
    /// Version information for all components
    /// </summary>
    public class ComponentVersions
    {
        public string? OptiScalerVersion { get; set; }
        public string? FakenvapiVersion { get; set; }
        public string? NukemFGVersion { get; set; }
    }

    /// <summary>
    /// A single OptiScaler release entry stored in the local releases cache.
    /// Only metadata is stored — no binaries are downloaded at this stage.
    /// </summary>
    public class OptiScalerReleaseEntry
    {
        public string Version { get; set; } = string.Empty;
        public string? DownloadUrl { get; set; }
        public bool IsBeta { get; set; }
        /// <summary>Nightly builds are intentionally separate from beta prereleases.</summary>
        public bool IsNightly { get; set; }
        public bool IsLatestStable { get; set; }
        public bool IsLatestBeta { get; set; }
        public bool IsLatestNightly { get; set; }
    }

    /// <summary>
    /// Local cache of OptiScaler release metadata fetched from GitHub.
    /// Updated on each successful API call and merged with existing entries.
    /// </summary>
    public class OptiScalerReleasesCache
    {
        public DateTime LastUpdated { get; set; } = DateTime.MinValue;
        public List<OptiScalerReleaseEntry> Releases { get; set; } = new();
    }

    /// <summary>
    /// FSR 4 model variant represented by a DLL package.
    /// </summary>
    public enum Fsr4DllVariant
    {
        Int8 = 0,
        Fp8 = 1
    }

    /// <summary>
    /// A single OptiScaler Extras (FSR 4 DLL) release entry stored in the local cache.
    /// </summary>
    public class ExtrasReleaseEntry
    {
        public string Version { get; set; } = string.Empty;
        public string? DownloadUrl { get; set; }
        public bool IsLatest { get; set; }
        public Fsr4DllVariant Variant { get; set; } = Fsr4DllVariant.Int8;
    }

    /// <summary>
    /// Local cache of OptiScaler Extras release metadata.
    /// </summary>
    public class ExtrasReleasesCache
    {
        public DateTime LastUpdated { get; set; } = DateTime.MinValue;
        public List<ExtrasReleaseEntry> Releases { get; set; } = new();
    }

    /// <summary>
    /// A single OptiPatcher release entry stored in the local cache.
    /// </summary>
    public class OptiPatcherReleaseEntry
    {
        public string Version { get; set; } = string.Empty;
        public string? DownloadUrl { get; set; }
        public bool IsLatest { get; set; }
    }

    /// <summary>
    /// Local cache of OptiPatcher release metadata.
    /// </summary>
    public class OptiPatcherReleasesCache
    {
        public DateTime LastUpdated { get; set; } = DateTime.MinValue;
        public List<OptiPatcherReleaseEntry> Releases { get; set; } = new();
    }

    /// <summary>
    /// A single Fakenvapi release entry stored in the local cache.
    /// </summary>
    public class FakenvapiReleaseEntry
    {
        public string Version { get; set; } = string.Empty;
        public string? DownloadUrl { get; set; }
        public bool IsLatest { get; set; }
    }

    /// <summary>
    /// Local cache of Fakenvapi release metadata.
    /// </summary>
    public class FakenvapiReleasesCache
    {
        public DateTime LastUpdated { get; set; } = DateTime.MinValue;
        public List<FakenvapiReleaseEntry> Releases { get; set; } = new();
    }

    /// <summary>
    /// A single DLSS Enabler mirror release entry stored in the local cache.
    /// Sourced from the Optiscaler-Client/OptiScaler-DlssEnabler unofficial mirror repo.
    /// </summary>
    public class DlssEnablerMirrorReleaseEntry
    {
        public string Version { get; set; } = string.Empty;
        public string? DownloadUrl { get; set; }
        public bool IsLatest { get; set; }
    }

    /// <summary>
    /// Local cache of DLSS Enabler mirror release metadata.
    /// </summary>
    public class DlssEnablerMirrorReleasesCache
    {
        public DateTime LastUpdated { get; set; } = DateTime.MinValue;
        public List<DlssEnablerMirrorReleaseEntry> Releases { get; set; } = new();
    }

    /// <summary>
    /// A single NVIDIA Streamline SDK release entry stored in the local cache.
    /// </summary>
    public class StreamlineReleaseEntry
    {
        public string Version { get; set; } = string.Empty;
        public string? DownloadUrl { get; set; }
        public bool IsLatest { get; set; }
    }

    /// <summary>
    /// Local cache of Streamline SDK release metadata.
    /// </summary>
    public class StreamlineReleasesCache
    {
        public DateTime LastUpdated { get; set; } = DateTime.MinValue;
        public List<StreamlineReleaseEntry> Releases { get; set; } = new();
    }
}
