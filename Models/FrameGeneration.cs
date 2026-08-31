// OptiScaler Client - Per-game frame generation configuration.
namespace OptiscalerClient.Models;

/// <summary>FG source/provider selected for one game. Values are persisted, append only.</summary>
public enum FrameGenerationRoute
{
    Disabled = 0,
    DlssGStreamline = 1,
    Nukem = 2,
    Fsr31Native = 3,
    Fsr30Native = 4,
    OptiFg = 5,
    Reserved6 = 6,
    /// <summary>Resolve the safest route independently for each game.</summary>
    Auto = 7
}

public enum FrameGenerationOutput
{
    Auto = 0,
    FsrFg = 1,
    XeFg = 2,
    Nukem = 3,
    DlssG = 4,
    DlssGWithNvngx = 5
}

/// <summary>Multiplier offered by a selected provider. The capability service determines valid values.</summary>
public enum MultiFrameGenerationMode
{
    Auto = 0,
    X2 = 1,
    X3 = 2,
    X4 = 3,
    X5 = 4,
    X6 = 5,
    Dynamic = 6
}

/// <summary>Persisted per-game choice. It deliberately does not modify a shared OptiScaler profile.</summary>
public sealed class GameFrameGenerationSettings
{
    public FrameGenerationRoute Route { get; set; } = FrameGenerationRoute.Disabled;
    public FrameGenerationOutput Output { get; set; } = FrameGenerationOutput.Auto;
    public MultiFrameGenerationMode MultiFrameMode { get; set; } = MultiFrameGenerationMode.Auto;
    public bool AdvancedMode { get; set; }
    public double? DynamicTargetFps { get; set; }
    public DateTime? AppliedAtUtc { get; set; }
}

public enum FrameGenerationRecommendationLevel
{
    Unavailable = 0,
    Experimental = 1,
    Recommended = 2
}

public sealed class FrameGenerationRecommendation
{
    public FrameGenerationRoute Route { get; init; }
    public FrameGenerationOutput Output { get; init; }
    public MultiFrameGenerationMode MultiFrameMode { get; init; }
    public FrameGenerationRecommendationLevel Level { get; init; }
    public string Reason { get; init; } = string.Empty;
}

/// <summary>Result of safe local capability discovery for a game and selected GPU.</summary>
public sealed class FrameGenerationCapabilities
{
    public bool IsDirectX12 { get; init; }
    public bool IsVulkan { get; init; }
    public bool HasNativeDlssG { get; init; }
    public bool HasNativeFsr3 { get; init; }
    public bool HasStreamline { get; init; }
    public bool HasXeFgDependencies { get; init; }
    public bool HasFsrFgDependencies { get; init; }
    public bool HasNukem { get; init; }
    public bool IsIntelArc { get; init; }
    public bool IsAntiCheatDetected { get; init; }
    public IReadOnlyList<FrameGenerationRoute> AvailableRoutes { get; init; } = Array.Empty<FrameGenerationRoute>();
    public IReadOnlyList<FrameGenerationOutput> AvailableOutputs { get; init; } = Array.Empty<FrameGenerationOutput>();
    public IReadOnlyList<MultiFrameGenerationMode> AvailableMfgModes { get; init; } = Array.Empty<MultiFrameGenerationMode>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
