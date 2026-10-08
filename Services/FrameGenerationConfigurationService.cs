using System.Globalization;
using OptiscalerClient.Helpers;
using OptiscalerClient.Models;

namespace OptiscalerClient.Services;

public interface IFrameGenerationConfigurationService
{
    FrameGenerationCapabilities DetectCapabilities(Game game, GpuInfo? gpu = null);
    FrameGenerationRecommendation GetRecommendation(FrameGenerationCapabilities capabilities);
    IReadOnlyList<MultiFrameGenerationMode> GetAvailableMfgModes(FrameGenerationRoute route, FrameGenerationOutput output, FrameGenerationCapabilities capabilities, FrameGenerationNvngxReplacement nvngxReplacement = FrameGenerationNvngxReplacement.None);
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> BuildIniSettings(GameFrameGenerationSettings settings, FrameGenerationCapabilities capabilities, string? optiscalerVersion = null);
    /// <summary>True when the effective (Auto-resolved) route or output needs the Streamline runtime DLLs in OptiScaler/streamline/.</summary>
    bool RequiresStreamline(GameFrameGenerationSettings settings, FrameGenerationCapabilities capabilities, string? optiscalerVersion = null);
    /// <summary>Resolves what Route=Auto (and the Nukem-output override) actually lands on. Shared with
    /// GameInstallationService.ApplySpoofingSettings, which needs to know whether the Nukem route is in
    /// effect without duplicating this resolution logic.</summary>
    FrameGenerationRoute ResolveEffectiveRoute(GameFrameGenerationSettings settings, FrameGenerationRecommendation recommendation, FrameGenerationCapabilities capabilities);
}

/// <summary>
/// Centralizes all FG validation and INI mappings so Views never need to know OptiScaler keys.
/// Discovery is deliberately file based and conservative: an unknown feature is never advertised as ready.
/// </summary>
public sealed class FrameGenerationConfigurationService : IFrameGenerationConfigurationService
{
    public FrameGenerationCapabilities DetectCapabilities(Game game, GpuInfo? gpu = null)
    {
        var root = ResolveGameDirectory(game);
        // Do not treat OptiScaler's own installed runtime files as native game capabilities.
        // Otherwise its dependencies make every manual FG route look safe before the user has
        // explicitly enabled Advanced routes.
        var files = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(file => !IsOptiScalerRuntimeFile(root, file))
                .Select(Path.GetFileName)
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool hasDlssG = !string.IsNullOrEmpty(game.DlssFrameGenVersion) || files.Contains("nvngx_dlssg.dll");
        bool hasFsr3 = files.Contains("ffx_fsr3_api_x64.dll") || files.Contains("ffx_fsr3_api_dx12_x64.dll") || files.Contains("amd_fidelityfx_framegeneration_dx12.dll");
        bool hasStreamline = files.Any(n => n!.StartsWith("sl.", StringComparison.OrdinalIgnoreCase) || n!.Contains("streamline", StringComparison.OrdinalIgnoreCase));
        bool hasXeFg = files.Contains("libxess_fg.dll") && files.Contains("libxell.dll");
        bool hasFsrFg = files.Contains("amd_fidelityfx_dx12.dll") ||
                        (files.Contains("amd_fidelityfx_loader_dx12.dll") && files.Contains("amd_fidelityfx_framegeneration_dx12.dll"));
        bool dx12 = files.Contains("d3d12.dll") || files.Contains("d3d12core.dll") ||
                    !string.IsNullOrEmpty(game.DlssFrameGenVersion) || hasFsr3 || hasXeFg;
        bool vulkan = files.Contains("vulkan-1.dll") || files.Contains("amd_fidelityfx_vk.dll");
        bool antiCheat = files.Overlaps(AntiCheatHelper.Files);
        bool arc = gpu?.Vendor == GpuVendor.Intel &&
                   (gpu.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase) || gpu.Name.Contains("Battlemage", StringComparison.OrdinalIgnoreCase));
        bool supportsDynamicMfg = gpu != null && gpu.Vendor switch
        {
            GpuVendor.NVIDIA => GpuSelectionHelper.IsBlackwell(gpu),
            GpuVendor.AMD => true,
            GpuVendor.Intel => true,
            _ => false
        };

        var routes = new List<FrameGenerationRoute> { FrameGenerationRoute.Auto, FrameGenerationRoute.Disabled };
        var outputs = new List<FrameGenerationOutput> { FrameGenerationOutput.Auto };
        var warnings = new List<string>();
        if (antiCheat) warnings.Add("Anti-cheat detected: frame-generation injection is disabled for safety.");

        if (!antiCheat)
        {
            // Streamline DLLs are not required to already be on disk to offer/recommend this
            // route: the install pipeline downloads and copies them into OptiScaler/streamline/
            // whenever the effective configuration needs them (see RequiresStreamline). Gating
            // on pre-existing files would make DLSS-G via Streamline unavailable for every fresh
            // install even when the game natively supports DLSS-G.
            if (hasDlssG && dx12) routes.Add(FrameGenerationRoute.DlssGStreamline);
            // NukemFG ships bundled with OptiScaler itself (installed alongside it, not a separate
            // user-provided component) — so this route only needs the game to have native DLSS-G,
            // same as DlssGStreamline above.
            if (hasDlssG) routes.Add(FrameGenerationRoute.Nukem);
            // FSR 3.1 is the safe automatic/native option. FSR 3.0 and OptiFG are
            // deliberately manual routes, exposed only through Advanced routes.
            if (hasFsr3) routes.Add(FrameGenerationRoute.Fsr31Native);
            if (hasFsrFg) outputs.Add(FrameGenerationOutput.FsrFg);
            if (hasXeFg) outputs.Add(FrameGenerationOutput.XeFg);
            // FGOutput=nvngxfg only ever works paired with FGInput=nvngxfg (Route.Nukem), which
            // per OptiScaler.ini itself "Requires DLSSG in the game" — offering this output without
            // hasDlssG would advertise a combo whose mandatory route is never in AvailableRoutes.
            if (hasDlssG) outputs.Add(FrameGenerationOutput.Nukem);
            // Unlike the route above, this isn't gated on hasDlssG: DLSS Enabler's replacement
            // provider (FGNvngxReplacement=Nukems/FFX/Arturs/Combo — see ApplyAutoNvngxReplacement)
            // exists specifically to fake DLSS-G output on games/GPUs with no native DLSS-G asset at
            // all, so this must stay selectable exactly like Auto/Disabled or that whole mechanism
            // becomes unreachable outside the permissive Default-settings capabilities.
            outputs.Add(FrameGenerationOutput.DlssG);
            if (hasDlssG) outputs.Add(FrameGenerationOutput.DlssGWithNvngx);
        }

        if (!hasXeFg) warnings.Add("XeFG requires libxess_fg.dll and libxell.dll.");
        if (!hasFsrFg) warnings.Add("FSR FG dependencies were not detected in the game/OptiScaler package.");
        if (!hasDlssG) warnings.Add("No native DLSS Frame Generation DLL was detected.");
        if (!dx12) warnings.Add("OptiFG and XeFG require DirectX 12.");

        var mfg = new List<MultiFrameGenerationMode> { MultiFrameGenerationMode.Auto, MultiFrameGenerationMode.X2 };
        if (arc) { mfg.Add(MultiFrameGenerationMode.X3); mfg.Add(MultiFrameGenerationMode.X4); }
        return new FrameGenerationCapabilities
        {
            IsDirectX12 = dx12, IsVulkan = vulkan, HasNativeDlssG = hasDlssG, HasNativeFsr3 = hasFsr3,
            HasStreamline = hasStreamline, HasXeFgDependencies = hasXeFg, HasFsrFgDependencies = hasFsrFg,
            // NukemFG ships bundled with OptiScaler, so its own availability now just mirrors
            // native DLSS-G — see the Route/Output.Nukem gating above.
            HasNukem = hasDlssG, IsIntelArc = arc, IsAntiCheatDetected = antiCheat, SupportsDynamicMfg = supportsDynamicMfg,
            AvailableRoutes = routes, AvailableOutputs = outputs, AvailableMfgModes = mfg, Warnings = warnings
        };
    }

    public FrameGenerationRecommendation GetRecommendation(FrameGenerationCapabilities c)
    {
        if (c.IsAntiCheatDetected)
            return new() { Route = FrameGenerationRoute.Disabled, Output = FrameGenerationOutput.Auto, MultiFrameMode = MultiFrameGenerationMode.Auto, Level = FrameGenerationRecommendationLevel.Unavailable, Reason = "Anti-cheat detected." };
        // Prioritized regardless of whether Streamline files already exist on disk: the install
        // pipeline supplies them whenever the effective route/output needs them.
        if (c.HasNativeDlssG && c.IsDirectX12)
            return new() { Route = FrameGenerationRoute.DlssGStreamline, Output = c.HasXeFgDependencies ? FrameGenerationOutput.XeFg : FrameGenerationOutput.FsrFg, MultiFrameMode = MultiFrameGenerationMode.X2, Level = FrameGenerationRecommendationLevel.Recommended, Reason = "Native DLSS-G via Streamline was detected." };
        if (c.HasNativeDlssG)
            return new() { Route = FrameGenerationRoute.Nukem, Output = FrameGenerationOutput.Nukem, MultiFrameMode = MultiFrameGenerationMode.X2, Level = FrameGenerationRecommendationLevel.Recommended, Reason = "Native DLSS-G legacy route detected." };
        if (c.HasNativeFsr3)
            return new() { Route = FrameGenerationRoute.Fsr31Native, Output = c.HasXeFgDependencies ? FrameGenerationOutput.XeFg : FrameGenerationOutput.FsrFg, MultiFrameMode = MultiFrameGenerationMode.X2, Level = FrameGenerationRecommendationLevel.Recommended, Reason = "Native FSR 3 frame-generation input was detected." };
        if (c.IsDirectX12)
            return new() { Route = FrameGenerationRoute.OptiFg, Output = c.HasXeFgDependencies ? FrameGenerationOutput.XeFg : FrameGenerationOutput.FsrFg, MultiFrameMode = MultiFrameGenerationMode.X2, Level = FrameGenerationRecommendationLevel.Experimental, Reason = "OptiFG is a last-resort experimental route." };
        return new() { Route = FrameGenerationRoute.Disabled, Output = FrameGenerationOutput.Auto, MultiFrameMode = MultiFrameGenerationMode.Auto, Level = FrameGenerationRecommendationLevel.Unavailable, Reason = "No safe frame-generation route was detected." };
    }

    public IReadOnlyList<MultiFrameGenerationMode> GetAvailableMfgModes(
        FrameGenerationRoute route,
        FrameGenerationOutput output,
        FrameGenerationCapabilities capabilities,
        FrameGenerationNvngxReplacement nvngxReplacement = FrameGenerationNvngxReplacement.None)
    {
        if (route == FrameGenerationRoute.Disabled)
            return [MultiFrameGenerationMode.Auto];

        if (output == FrameGenerationOutput.XeFg)
        {
            // x3-x6 need the XeFGUnlock.asi plugin (auto-installed at Install time when picked —
            // see ManageGameWindow's installXeFGUnlock), which works on any GPU, not just Intel
            // Arc's native x3/x4 cap. No GPU gate here on purpose.
            return
            [
                MultiFrameGenerationMode.Auto, MultiFrameGenerationMode.X2, MultiFrameGenerationMode.X3,
                MultiFrameGenerationMode.X4, MultiFrameGenerationMode.X5, MultiFrameGenerationMode.X6
            ];
        }

        // MFG beyond x2 for the DLSS-G output requires DLSS Enabler's DLL (Arturs/Combo).
        if (output == FrameGenerationOutput.DlssG &&
            nvngxReplacement is FrameGenerationNvngxReplacement.Arturs or FrameGenerationNvngxReplacement.Combo)
        {
            var modes = new List<MultiFrameGenerationMode>
            {
                MultiFrameGenerationMode.Auto, MultiFrameGenerationMode.X2, MultiFrameGenerationMode.X3,
                MultiFrameGenerationMode.X4, MultiFrameGenerationMode.X5, MultiFrameGenerationMode.X6
            };
            if (capabilities.SupportsDynamicMfg) modes.Add(MultiFrameGenerationMode.Dynamic);
            return modes;
        }

        return [MultiFrameGenerationMode.X2];
    }

    /// <summary>Resolves Route=Auto the same way everywhere: normally via <see cref="GetRecommendation"/>
    /// (native-capability detection), with two overrides:
    /// 1. Output explicitly pinned to DLSS-G through DLSS Enabler's replacement provider (Arturs/Combo)
    ///    — that path fakes DLSS-G output without any native DLSS-G asset, so a per-game recommendation
    ///    built from native detection would wrongly downgrade it (often to Disabled) on every game that
    ///    doesn't ship real DLSS-G files, silently discarding an explicit Output/multiplier choice.
    /// 2. Recommendation resolved to DlssGStreamline and Nukem is also available. DlssGStreamline hooks
    ///    the swapchain directly through Streamline's own interposer/proxy-factory upgrade
    ///    (DxgiFactoryHooks::HookToDLSSGFactory) — confirmed via OptiScaler.log to silently kill the game
    ///    mid-hook on this setup (no crash dump, no Defender flag — an external termination, not our
    ///    code throwing) on every game/OptiScaler version tried. Nukem reads the exact same native
    ///    DLSS-G signal through a self-contained converter DLL instead, without that interposer hook, and
    ///    was confirmed to start cleanly in the same scenario (manually pinned via Advanced routes).
    ///    Preferred over DlssGStreamline whenever available; DlssGStreamline remains the fallback when
    ///    Nukem isn't (native DLSS-G present but no converter DLL) — better than nothing.</summary>
    public FrameGenerationRoute ResolveEffectiveRoute(GameFrameGenerationSettings settings, FrameGenerationRecommendation recommendation, FrameGenerationCapabilities capabilities)
    {
        // "Nukem FSR3 FG" output (FGOutput=nvngxfg) only works paired with the Nukem route
        // (FGInput=nvngxfg) — any other route/output pairing silently fails to enable FG since
        // OptiScaler has no matching input signal for that output. Takes priority over an explicit
        // Route pick (mismatched saved/Advanced selections included) since the pairing is mandatory,
        // not a preference.
        if (settings.Output == FrameGenerationOutput.Nukem) return FrameGenerationRoute.Nukem;
        if (settings.Route != FrameGenerationRoute.Auto) return settings.Route;
        if (settings.Output == FrameGenerationOutput.DlssG &&
            settings.NvngxReplacement is FrameGenerationNvngxReplacement.Arturs or FrameGenerationNvngxReplacement.Combo)
            return FrameGenerationRoute.DlssGStreamline;
        // Nukem's FGInput=nvngxfg is documented as "Limited to FSR 3 FG" (OptiScaler_example.ini) —
        // it only pairs validly with FGOutput=nvngxfg (Nukem's own output, handled above). Swapping
        // the route here while a DIFFERENT explicit Output is selected (e.g. XeFg) produces a
        // silently-broken ini: input locked to Nukem's DLSS-G reader, the chosen output never
        // actually engages. Only take this crash-avoidance swap when Output is still Auto — an
        // explicit non-Nukem Output pick must keep whatever route actually supports it.
        if (settings.Output == FrameGenerationOutput.Auto &&
            recommendation.Route == FrameGenerationRoute.DlssGStreamline &&
            capabilities.AvailableRoutes.Contains(FrameGenerationRoute.Nukem))
            return FrameGenerationRoute.Nukem;
        return recommendation.Route;
    }

    public bool RequiresStreamline(GameFrameGenerationSettings settings, FrameGenerationCapabilities capabilities, string? optiscalerVersion = null)
    {
        if (settings.Route == FrameGenerationRoute.Disabled)
            return false;

        var recommendation = GetRecommendation(capabilities);
        var effectiveRoute = ResolveEffectiveRoute(settings, recommendation, capabilities);
        if (effectiveRoute == FrameGenerationRoute.Disabled)
            return false;

        var effectiveOutput = settings.Output == FrameGenerationOutput.Auto ? recommendation.Output : settings.Output;
        // The Nukem route's "Uses Streamline swapchain for pacing" (OptiScaler.ini) refers to the
        // GAME's own bundled native Streamline runtime (the same one that exposes native DLSS-G),
        // not files OptiScaler needs downloaded into OptiScaler/streamline/ — confirmed against
        // OptiScaler's own wiki (no Streamline-file requirement listed for Nukem's dlssg-to-fsr3)
        // and its changelog, which places the actual Streamline-file-requiring "DLSSG via Nvngx"
        // rework exclusively in Nightly/WIP v0.10, never in any stable release through v0.9.4.
        return effectiveRoute == FrameGenerationRoute.DlssGStreamline || effectiveOutput == FrameGenerationOutput.DlssG;
    }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> BuildIniSettings(
        GameFrameGenerationSettings settings, FrameGenerationCapabilities c, string? optiscalerVersion = null)
    {
        var usesNightlySchema = UsesNightlyFrameGenerationSchema(optiscalerVersion);
        var recommendation = GetRecommendation(c);
        var effectiveRoute = ResolveEffectiveRoute(settings, recommendation, c);
        var effectiveOutput = settings.Output == FrameGenerationOutput.Auto ? recommendation.Output : settings.Output;
        // DlssGStreamline resolved via the Arturs/Combo replacement (see ResolveEffectiveRoute) never
        // appears in a non-native game's AvailableRoutes — that's the whole point of the replacement,
        // so it must bypass this native-capability gate the same way AdvancedMode already does.
        var routeViaNvngxReplacement = effectiveRoute == FrameGenerationRoute.DlssGStreamline &&
            effectiveOutput == FrameGenerationOutput.DlssG &&
            settings.NvngxReplacement is FrameGenerationNvngxReplacement.Arturs or FrameGenerationNvngxReplacement.Combo;

        if (c.IsAntiCheatDetected && effectiveRoute != FrameGenerationRoute.Disabled)
            throw new InvalidOperationException("Frame generation cannot be applied while anti-cheat is detected.");
        if (!settings.AdvancedMode && !routeViaNvngxReplacement && !c.AvailableRoutes.Contains(effectiveRoute))
            throw new InvalidOperationException("Selected frame-generation route is not available for this game.");
        var availableMfgModes = GetAvailableMfgModes(effectiveRoute, effectiveOutput, c, settings.NvngxReplacement);
        // A multiplier is irrelevant while FG is disabled. Older saved game settings used
        // X2 as their default, so validating it here made a disabled FG configuration block
        // any OptiScaler install, including Nightly.
        if (effectiveRoute != FrameGenerationRoute.Disabled &&
            settings.MultiFrameMode != MultiFrameGenerationMode.Auto &&
            !availableMfgModes.Contains(settings.MultiFrameMode))
            throw new InvalidOperationException("Selected multi-frame mode is not supported by the current GPU/provider.");

        var frameGen = new Dictionary<string, string> { ["Enabled"] = effectiveRoute == FrameGenerationRoute.Disabled ? "false" : "true" };
        if (effectiveRoute != FrameGenerationRoute.Disabled)
        {
            frameGen["FGInput"] = effectiveRoute switch
            {
                FrameGenerationRoute.DlssGStreamline => "dlssg",
                FrameGenerationRoute.Nukem => usesNightlySchema ? "nvngxfg" : "nukems",
                FrameGenerationRoute.Fsr31Native => "fsrfg",
                FrameGenerationRoute.Fsr30Native => "fsrfg30",
                FrameGenerationRoute.OptiFg => "upscaler",
                _ => "nofg"
            };
            frameGen["FGOutput"] = effectiveOutput switch
            {
                FrameGenerationOutput.FsrFg => "fsrfg",
                FrameGenerationOutput.XeFg => "xefg",
                FrameGenerationOutput.Nukem => usesNightlySchema ? "nvngxfg" : "nukems",
                FrameGenerationOutput.DlssG => "dlssg",
                FrameGenerationOutput.DlssGWithNvngx => "dlssgwithnvngx",
                _ => "nofg"
            };
            // Nightly 0.10 moved the provider choice behind the NVNGX bridge. Do
            // not emit this unknown key for 0.9.x packages.
            if (usesNightlySchema && (effectiveRoute == FrameGenerationRoute.Nukem || effectiveOutput == FrameGenerationOutput.Nukem))
                frameGen["FGNvngxReplacement"] = "Nukems";
        }

        var result = new Dictionary<string, IReadOnlyDictionary<string, string>> { ["FrameGen"] = frameGen };
        if (effectiveOutput == FrameGenerationOutput.XeFg)
        {
            // X5/X6 need XeFGUnlock.asi (see GetAvailableMfgModes) — without this mapping they fell
            // through to "auto", silently never asking for more than native x2-x4 in the ini.
            var count = settings.MultiFrameMode switch
            {
                MultiFrameGenerationMode.X2 => "1",
                MultiFrameGenerationMode.X3 => "2",
                MultiFrameGenerationMode.X4 => "3",
                MultiFrameGenerationMode.X5 => "4",
                MultiFrameGenerationMode.X6 => "5",
                _ => "auto"
            };
            result["XeFG"] = new Dictionary<string, string> { ["InterpolationCount"] = count };
        }
        // FGNvngxReplacement only does anything when FGOutput=dlssg. None keeps the ini default.
        if (effectiveOutput == FrameGenerationOutput.DlssG && settings.NvngxReplacement != FrameGenerationNvngxReplacement.None)
        {
            frameGen["FGNvngxReplacement"] = settings.NvngxReplacement switch
            {
                FrameGenerationNvngxReplacement.Nukems => "Nukems",
                FrameGenerationNvngxReplacement.Ffx => "FFX",
                FrameGenerationNvngxReplacement.Arturs => "Arturs",
                FrameGenerationNvngxReplacement.Combo => "Combo",
                _ => "None"
            };

            // MFG multiplier beyond x2 is only meaningful for the DLSS Enabler providers.
            if (settings.NvngxReplacement is FrameGenerationNvngxReplacement.Arturs or FrameGenerationNvngxReplacement.Combo)
            {
                var dlssg = new Dictionary<string, string>();
                // ForceDMFG/FramerateTargetDMFG only exist in the nightly ini schema. Falling back to
                // "auto" on stable keeps this a safe no-op there instead of writing unknown keys.
                if (settings.MultiFrameMode == MultiFrameGenerationMode.Dynamic && usesNightlySchema)
                {
                    // Dynamic MFG picks the multiplier itself to hit a target framerate instead of a
                    // fixed count - ForceDMFG turns it on for OptiScaler's own DLSSG output, and a
                    // non-zero FramerateTargetDMFG disables manual multiplier control entirely (per
                    // the ini docs), so InterpolationCount is deliberately not written alongside it.
                    dlssg["ForceDMFG"] = "true";
                    dlssg["FramerateTargetDMFG"] = settings.DynamicTargetFps is > 0
                        ? settings.DynamicTargetFps.Value.ToString(CultureInfo.InvariantCulture)
                        : "auto";
                }
                else
                {
                    dlssg["InterpolationCount"] = settings.MultiFrameMode switch
                    {
                        MultiFrameGenerationMode.X2 => "1",
                        MultiFrameGenerationMode.X3 => "2",
                        MultiFrameGenerationMode.X4 => "3",
                        MultiFrameGenerationMode.X5 => "4",
                        MultiFrameGenerationMode.X6 => "5",
                        _ => "auto"
                    };
                }
                result["DLSSG"] = dlssg;
            }
        }
        if (effectiveRoute == FrameGenerationRoute.OptiFg &&
            (effectiveOutput == FrameGenerationOutput.FsrFg || effectiveOutput == FrameGenerationOutput.DlssG))
            result["HUDFix"] = new Dictionary<string, string> { ["HUDFix"] = "true" };
        return result;
    }

    /// <summary>Last stable/beta release confirmed (by inspecting its shipped OptiScaler.ini) to
    /// NOT carry FGNvngxReplacement/[DLSSG]. Versions at or below this keep the old, deterministic
    /// answer with no disk access, so already-verified behavior never changes. Versions above it are
    /// unverified by us — resolved by probing the real release instead of guessing from the number.</summary>
    private static readonly Version LastConfirmedUnsupportedVersion = new(0, 9, 5);

    /// <summary>0.10/nightly changed the NVNGX/Nukem INI vocabulary from 0.9.x.</summary>
    public static bool UsesNightlyFrameGenerationSchema(string? optiscalerVersion)
    {
        if (string.IsNullOrWhiteSpace(optiscalerVersion)) return false;
        if (optiscalerVersion.StartsWith("nightly-", StringComparison.OrdinalIgnoreCase)) return true;

        var numericPart = optiscalerVersion.TrimStart('v', 'V').Split('-', 2)[0];
        if (!Version.TryParse(numericPart, out var parsed)) return false;
        if (parsed <= LastConfirmedUnsupportedVersion) return false;

        // Newer than anything we've verified: don't guess from the version number a future
        // release might be numbered however the OptiScaler team decides. If we already have this
        // exact release cached, its own OptiScaler.ini is authoritative on whether it ships
        // FGNvngxReplacement — new releases are recognized automatically, no client update needed.
        if (TryProbeCachedIniForNvngxReplacement(optiscalerVersion, out var probedSupport))
            return probedSupport;

        // Not cached yet (nothing downloaded to inspect): fall back to the old >=0.10 assumption
        // as a best guess. Once the real release is downloaded, BuildIniSettings re-evaluates this
        // from the actual file, so an incorrect guess here only affects UI-level warnings/prompts,
        // never what actually gets written to the game's ini.
        return parsed >= new Version(0, 10);
    }

    /// <summary>Looks for the exact release's own OptiScaler.ini in the local download cache and
    /// checks whether it ships the FGNvngxReplacement key. Returns false (nothing to report) if the
    /// release hasn't been downloaded/cached yet.</summary>
    private static bool TryProbeCachedIniForNvngxReplacement(string optiscalerVersion, out bool supportsNvngxReplacement)
    {
        supportsNvngxReplacement = false;
        try
        {
            var cachedIniPath = Path.Combine(new ComponentManagementService().GetOptiScalerCachePath(optiscalerVersion), "OptiScaler.ini");
            if (!File.Exists(cachedIniPath))
                return false;

            supportsNvngxReplacement = File.ReadAllText(cachedIniPath).Contains("FGNvngxReplacement", StringComparison.OrdinalIgnoreCase);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsOptiScalerRuntimeFile(string gameRoot, string filePath)
    {
        var relativePath = Path.GetRelativePath(gameRoot, filePath);
        var firstSeparator = relativePath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        var topLevelDirectory = firstSeparator < 0 ? relativePath : relativePath[..firstSeparator];
        return topLevelDirectory.Equals("OptiScaler", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveGameDirectory(Game game) => !string.IsNullOrWhiteSpace(game.ExecutablePath) ? Path.GetDirectoryName(game.ExecutablePath) ?? game.InstallPath : game.InstallPath;
}
