namespace OptiscalerClient.Models;

// Persisted as integers in games.json. Keep existing values pinned and append new ones.
public enum UpscalingQualityPreset
{
    GameControlled = 0,
    NativeAa = 1,
    UltraQuality = 2,
    Quality = 3,
    Balanced = 4,
    Performance = 5,
    UltraPerformance = 6,
    Custom = 7
}

/// <summary>
/// Per-game quality-ratio override applied after the selected shared profile.
/// </summary>
public sealed class GameUpscalingQualitySettings
{
    public UpscalingQualityPreset Preset { get; set; } = UpscalingQualityPreset.GameControlled;
    public double CustomRatio { get; set; } = 1.5;
    public DateTime? AppliedAtUtc { get; set; }

    public double Ratio => Preset switch
    {
        UpscalingQualityPreset.NativeAa => 1.0,
        UpscalingQualityPreset.UltraQuality => 1.3,
        UpscalingQualityPreset.Quality => 1.5,
        UpscalingQualityPreset.Balanced => 1.7,
        UpscalingQualityPreset.Performance => 2.0,
        UpscalingQualityPreset.UltraPerformance => 3.0,
        UpscalingQualityPreset.Custom => Math.Clamp(CustomRatio, 1.0, 3.0),
        _ => 1.0
    };
}
