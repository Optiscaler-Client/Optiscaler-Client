using OptiscalerClient.Helpers;
using OptiscalerClient.Models;

namespace OptiscalerClient.Services;

public interface IFrameGenerationConfigurationService
{
    FrameGenerationCapabilities DetectCapabilities(Game game, GpuInfo? gpu = null);
    FrameGenerationRecommendation GetRecommendation(FrameGenerationCapabilities capabilities);
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> BuildIniSettings(GameFrameGenerationSettings settings, FrameGenerationCapabilities capabilities);
}

/// <summary>
/// Centralizes all FG validation and INI mappings so Views never need to know OptiScaler keys.
/// Discovery is deliberately file based and conservative: an unknown feature is never advertised as ready.
/// </summary>
public sealed class FrameGenerationConfigurationService : IFrameGenerationConfigurationService
{
    private static readonly string[] AntiCheatFiles = ["start_protected_game.exe", "EasyAntiCheat_EOS.exe", "BEService.exe", "BEClient_x64.dll"];

    public FrameGenerationCapabilities DetectCapabilities(Game game, GpuInfo? gpu = null)
    {
        var root = ResolveGameDirectory(game);
        var files = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool hasDlssG = !string.IsNullOrEmpty(game.DlssFrameGenVersion) || files.Contains("nvngx_dlssg.dll");
        bool hasFsr3 = files.Contains("ffx_fsr3_api_x64.dll") || files.Contains("ffx_fsr3_api_dx12_x64.dll") || files.Contains("amd_fidelityfx_framegeneration_dx12.dll");
        bool hasStreamline = files.Any(n => n!.StartsWith("sl.", StringComparison.OrdinalIgnoreCase) || n!.Contains("streamline", StringComparison.OrdinalIgnoreCase));
        bool hasXeFg = files.Contains("libxess_fg.dll") && files.Contains("libxell.dll");
        bool hasFsrFg = files.Contains("amd_fidelityfx_dx12.dll") ||
                        (files.Contains("amd_fidelityfx_loader_dx12.dll") && files.Contains("amd_fidelityfx_framegeneration_dx12.dll"));
        bool hasNukem = files.Contains("dlssg_to_fsr3_amd_is_better.dll");
        bool hasEnabler = game.IsDlssEnablerInstalled || files.Contains("dlss-enabler-headless.dll") || files.Contains("dlss-enabler.dll");
        bool dx12 = files.Contains("d3d12.dll") || files.Contains("d3d12core.dll") ||
                    !string.IsNullOrEmpty(game.DlssFrameGenVersion) || hasFsr3 || hasXeFg;
        bool vulkan = files.Contains("vulkan-1.dll") || files.Contains("amd_fidelityfx_vk.dll");
        bool antiCheat = files.Overlaps(AntiCheatFiles);
        bool arc = gpu?.Vendor == GpuVendor.Intel &&
                   (gpu.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase) || gpu.Name.Contains("Battlemage", StringComparison.OrdinalIgnoreCase));

        var routes = new List<FrameGenerationRoute> { FrameGenerationRoute.Auto, FrameGenerationRoute.Disabled };
        var outputs = new List<FrameGenerationOutput> { FrameGenerationOutput.Auto };
        var warnings = new List<string>();
        if (antiCheat) warnings.Add("Anti-cheat detected: frame-generation injection is disabled for safety.");

        if (!antiCheat)
        {
            if (hasDlssG && hasStreamline && dx12) routes.Add(FrameGenerationRoute.DlssGStreamline);
            if (hasDlssG && (hasNukem || !string.IsNullOrEmpty(game.DlssFrameGenVersion))) routes.Add(FrameGenerationRoute.Nukem);
            if (hasFsr3) { routes.Add(FrameGenerationRoute.Fsr31Native); routes.Add(FrameGenerationRoute.Fsr30Native); }
            if (dx12 && game.HasUpscaler) routes.Add(FrameGenerationRoute.OptiFg);
            if (dx12 && !string.IsNullOrEmpty(game.DlssVersion)) routes.Add(FrameGenerationRoute.DlssEnabler);
            if (hasFsrFg) outputs.Add(FrameGenerationOutput.FsrFg);
            if (hasXeFg) outputs.Add(FrameGenerationOutput.XeFg);
            if (hasNukem) outputs.Add(FrameGenerationOutput.Nukem);
            if (hasDlssG && hasStreamline) outputs.Add(FrameGenerationOutput.DlssG);
            if (hasDlssG && (hasNukem || hasEnabler)) outputs.Add(FrameGenerationOutput.DlssGWithNvngx);
        }

        if (!hasXeFg) warnings.Add("XeFG requires libxess_fg.dll and libxell.dll.");
        if (!hasFsrFg) warnings.Add("FSR FG dependencies were not detected in the game/OptiScaler package.");
        if (!hasDlssG) warnings.Add("No native DLSS Frame Generation DLL was detected.");
        if (!dx12) warnings.Add("OptiFG and XeFG require DirectX 12.");

        var mfg = new List<MultiFrameGenerationMode> { MultiFrameGenerationMode.Auto, MultiFrameGenerationMode.X2 };
        if (arc) { mfg.Add(MultiFrameGenerationMode.X3); mfg.Add(MultiFrameGenerationMode.X4); }
        if (hasEnabler && hasStreamline) { mfg.Add(MultiFrameGenerationMode.X5); mfg.Add(MultiFrameGenerationMode.X6); mfg.Add(MultiFrameGenerationMode.Dynamic); }

        return new FrameGenerationCapabilities
        {
            IsDirectX12 = dx12, IsVulkan = vulkan, HasNativeDlssG = hasDlssG, HasNativeFsr3 = hasFsr3,
            HasStreamline = hasStreamline, HasXeFgDependencies = hasXeFg, HasFsrFgDependencies = hasFsrFg,
            HasNukem = hasNukem, HasDlssEnabler = hasEnabler, IsIntelArc = arc, IsAntiCheatDetected = antiCheat,
            AvailableRoutes = routes, AvailableOutputs = outputs, AvailableMfgModes = mfg, Warnings = warnings
        };
    }

    public FrameGenerationRecommendation GetRecommendation(FrameGenerationCapabilities c)
    {
        if (c.IsAntiCheatDetected)
            return new() { Route = FrameGenerationRoute.Disabled, Output = FrameGenerationOutput.Auto, MultiFrameMode = MultiFrameGenerationMode.Auto, Level = FrameGenerationRecommendationLevel.Unavailable, Reason = "Anti-cheat detected." };
        if (c.HasNativeDlssG && c.HasStreamline && c.IsDirectX12)
            return new() { Route = FrameGenerationRoute.DlssGStreamline, Output = c.HasXeFgDependencies ? FrameGenerationOutput.XeFg : FrameGenerationOutput.FsrFg, MultiFrameMode = MultiFrameGenerationMode.X2, Level = FrameGenerationRecommendationLevel.Recommended, Reason = "Native DLSS-G via Streamline was detected." };
        if (c.HasNativeDlssG)
            return new() { Route = FrameGenerationRoute.Nukem, Output = FrameGenerationOutput.Nukem, MultiFrameMode = MultiFrameGenerationMode.X2, Level = FrameGenerationRecommendationLevel.Recommended, Reason = "Native DLSS-G legacy route detected." };
        if (c.HasNativeFsr3)
            return new() { Route = FrameGenerationRoute.Fsr31Native, Output = c.HasXeFgDependencies ? FrameGenerationOutput.XeFg : FrameGenerationOutput.FsrFg, MultiFrameMode = MultiFrameGenerationMode.X2, Level = FrameGenerationRecommendationLevel.Recommended, Reason = "Native FSR 3 frame-generation input was detected." };
        if (c.IsDirectX12)
            return new() { Route = FrameGenerationRoute.OptiFg, Output = c.HasXeFgDependencies ? FrameGenerationOutput.XeFg : FrameGenerationOutput.FsrFg, MultiFrameMode = MultiFrameGenerationMode.X2, Level = FrameGenerationRecommendationLevel.Experimental, Reason = "OptiFG is a last-resort experimental route." };
        return new() { Route = FrameGenerationRoute.Disabled, Output = FrameGenerationOutput.Auto, MultiFrameMode = MultiFrameGenerationMode.Auto, Level = FrameGenerationRecommendationLevel.Unavailable, Reason = "No safe frame-generation route was detected." };
    }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> BuildIniSettings(GameFrameGenerationSettings settings, FrameGenerationCapabilities c)
    {
        var recommendation = GetRecommendation(c);
        var effectiveRoute = settings.Route == FrameGenerationRoute.Auto ? recommendation.Route : settings.Route;
        var effectiveOutput = settings.Output == FrameGenerationOutput.Auto ? recommendation.Output : settings.Output;

        if (c.IsAntiCheatDetected && effectiveRoute != FrameGenerationRoute.Disabled)
            throw new InvalidOperationException("Frame generation cannot be applied while anti-cheat is detected.");
        if (!settings.AdvancedMode && !c.AvailableRoutes.Contains(effectiveRoute))
            throw new InvalidOperationException("Selected frame-generation route is not available for this game.");
        if (settings.MultiFrameMode != MultiFrameGenerationMode.Auto && !c.AvailableMfgModes.Contains(settings.MultiFrameMode))
            throw new InvalidOperationException("Selected multi-frame mode is not supported by the current GPU/provider.");

        var frameGen = new Dictionary<string, string> { ["Enabled"] = effectiveRoute == FrameGenerationRoute.Disabled ? "false" : "true" };
        if (effectiveRoute != FrameGenerationRoute.Disabled)
        {
            frameGen["FGInput"] = effectiveRoute switch
            {
                FrameGenerationRoute.DlssGStreamline => "dlssg",
                FrameGenerationRoute.Nukem => "nvngxfg",
                FrameGenerationRoute.Fsr31Native => "fsrfg",
                FrameGenerationRoute.Fsr30Native => "fsrfg30",
                FrameGenerationRoute.OptiFg => "upscaler",
                FrameGenerationRoute.DlssEnabler => "nvngxfg",
                _ => "nofg"
            };
            frameGen["FGOutput"] = effectiveOutput switch
            {
                FrameGenerationOutput.FsrFg => "fsrfg",
                FrameGenerationOutput.XeFg => "xefg",
                FrameGenerationOutput.Nukem => "nvngxfg",
                FrameGenerationOutput.DlssG => "dlssg",
                FrameGenerationOutput.DlssGWithNvngx => "dlssgwithnvngx",
                _ => "nofg"
            };
        }

        var result = new Dictionary<string, IReadOnlyDictionary<string, string>> { ["FrameGen"] = frameGen };
        if (effectiveOutput == FrameGenerationOutput.XeFg)
        {
            var count = settings.MultiFrameMode switch { MultiFrameGenerationMode.X2 => "1", MultiFrameGenerationMode.X3 => "2", MultiFrameGenerationMode.X4 => "3", _ => "auto" };
            result["XeFG"] = new Dictionary<string, string> { ["InterpolationCount"] = count };
        }
        if (effectiveRoute == FrameGenerationRoute.OptiFg && effectiveOutput == FrameGenerationOutput.FsrFg)
            result["HUDFix"] = new Dictionary<string, string> { ["HUDFix"] = "true" };
        if (settings.MultiFrameMode == MultiFrameGenerationMode.Dynamic)
            result["Nvngx"] = new Dictionary<string, string> { ["OverrideForceDMFG"] = "true", ["FramerateTargetDMFG"] = settings.DynamicTargetFps?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0.0" };
        return result;
    }

    private static string ResolveGameDirectory(Game game) => !string.IsNullOrWhiteSpace(game.ExecutablePath) ? Path.GetDirectoryName(game.ExecutablePath) ?? game.InstallPath : game.InstallPath;
}
