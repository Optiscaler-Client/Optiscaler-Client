namespace OptiscalerClient.Models;

// Persisted as an integer in games.json. Keep existing values pinned and append new ones.
public enum OutputUpscalerBackend
{
    Default = 0,
    Fsr2 = 1,
    Fsr3 = 2,       // Forces FSR 3.1.5 (UpscalerIndex=1), never upgrades to FSR4 even on RDNA4.
    XeSS = 3,
    Dlss = 4,
    Fsr4 = 5        // Leaves UpscalerIndex on auto; OptiScaler resolves FSR4 vs FSR3.1 by GPU.
}

/// <summary>Per-game upscaler-backend override applied after the selected shared profile.</summary>
public sealed class GameOutputUpscalerSettings
{
    public OutputUpscalerBackend Backend { get; set; } = OutputUpscalerBackend.Default;
    public DateTime? AppliedAtUtc { get; set; }
}
