using System;
using System.Text.RegularExpressions;
using OptiscalerClient.Services;

namespace OptiscalerClient.Helpers
{
    public static class GpuSelectionHelper
    {
        public static string BuildGpuId(GpuInfo gpu)
        {
            return $"{gpu.Vendor}|{gpu.Name}";
        }

        public static GpuInfo? GetPreferredGpu(IGpuDetectionService? gpuService, string? defaultGpuId)
        {
            if (gpuService == null) return null;

            var gpus = gpuService.DetectGPUs();
            if (gpus.Length == 0) return null;

            if (!string.IsNullOrWhiteSpace(defaultGpuId))
            {
                foreach (var gpu in gpus)
                {
                    if (string.Equals(BuildGpuId(gpu), defaultGpuId, StringComparison.OrdinalIgnoreCase))
                    {
                        return gpu;
                    }
                }
            }

            return gpuService.GetDiscreteGPU() ?? gpuService.GetPrimaryGPU() ?? gpus[0];
        }

        /// <summary>RDNA 4 (Radeon RX 9000 series) is the only generation with native FP8 hardware for FSR4 —
        /// every other AMD GPU needs the INT8 software fallback forced explicitly.</summary>
        public static bool IsRdna4(GpuInfo? gpu)
        {
            // "R9\d{3}": the Radeon AI PRO R9700 workstation card is RDNA 4 but has no " 9"/"RX 9".
            return gpu != null && gpu.Vendor == GpuVendor.AMD &&
                   (gpu.Name.Contains(" 9", StringComparison.OrdinalIgnoreCase) ||
                    gpu.Name.Contains("RX 9", StringComparison.OrdinalIgnoreCase) ||
                    Regex.IsMatch(gpu.Name, @"\bR9\d{3}\b", RegexOptions.IgnoreCase));
        }

        /// <summary>RDNA 3 desktop (Radeon RX 7000 series) is now also whitelisted by AMD's official
        /// driver for native FSR 4 Swap, same as RDNA 4 — no need for this app to inject/force it.
        /// RDNA3 mobile/APU chips (e.g. 780M/760M) aren't covered by that whitelist, so they're
        /// intentionally excluded here and still get the INT8 fallback like older GPUs.</summary>
        public static bool IsRdna3(GpuInfo? gpu)
        {
            return gpu != null && gpu.Vendor == GpuVendor.AMD &&
                   gpu.Name.Contains("RX 7", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>RDNA 2 (Radeon RX 6000 series, plus RDNA2-based APUs/handhelds like Steam Deck's
        /// "Van Gogh" or the Ryzen 6000 mobile "660M"/"680M" iGPUs) needs its own custom amdxc64.dll
        /// loaded via OptiScaler's LoadCustomAmdxc64OnRdna2 to get FSR 4 Swap working.</summary>
        public static bool IsRdna2(GpuInfo? gpu)
        {
            if (gpu == null || gpu.Vendor != GpuVendor.AMD) return false;
            // Steam Deck reports "AMD Custom GPU 0405" (LCD) / "0932" (OLED) on both Windows (WMI)
            // and Linux (amdgpu.ids) - the "Van Gogh" codename never appears in either name.
            return gpu.Name.Contains("RX 6", StringComparison.OrdinalIgnoreCase) ||
                   gpu.Name.Contains("Van Gogh", StringComparison.OrdinalIgnoreCase) ||
                   gpu.Name.Contains("Custom GPU 0405", StringComparison.OrdinalIgnoreCase) ||
                   gpu.Name.Contains("Custom GPU 0932", StringComparison.OrdinalIgnoreCase) ||
                   gpu.Name.Contains("660M", StringComparison.OrdinalIgnoreCase) ||
                   gpu.Name.Contains("680M", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>"Setup NR" (AMD DLSS Neural Rendering — danielblnc's mod, and guentra's Linux fork
        /// that runs it there) is gated to RDNA 3/4 specifically, not just "any AMD GPU": that's the
        /// same gfx1100/1101/1102 (RDNA3) and gfx1200/1201 (RDNA4) target range guentra's fork's
        /// bundled ROCm actually supports on Linux (see its README's Requirements section), and on
        /// Windows it's the hardware generation actually fast enough for the mod's extra render passes
        /// to be worthwhile. Older AMD GPUs (RDNA1/2, Vega, ...) are excluded even though they're
        /// still AMD.</summary>
        public static bool IsRdna3OrRdna4(GpuInfo? gpu) => IsRdna3(gpu) || IsRdna4(gpu);

        /// <summary>Nvidia Blackwell (GeForce RTX 50 series) is the only Nvidia generation with native
        /// Dynamic Multi Frame Generation support (OptiScaler's ForceDMFG/OverrideForceDMFG). Matches
        /// RTX 5050/5060/5070/5080/5090 (with optional Ti/Laptop suffixes) but not the RTX 5000 Ada
        /// workstation card, which is Ada Lovelace, not Blackwell.</summary>
        public static bool IsBlackwell(GpuInfo? gpu)
        {
            return gpu != null && gpu.Vendor == GpuVendor.NVIDIA &&
                   Regex.IsMatch(gpu.Name, @"RTX\s?50(50|60|70|80|90)", RegexOptions.IgnoreCase);
        }

        /// <summary>Nvidia Turing with tensor cores: GeForce RTX 20 series, TITAN RTX and Quadro RTX
        /// (with optional Super/Ti/Laptop/Max-Q suffixes). GTX 16xx and Quadro T-series are Turing too
        /// but have no tensor cores, so they don't match.</summary>
        public static bool IsTuringRtx(GpuInfo? gpu)
        {
            return gpu != null && gpu.Vendor == GpuVendor.NVIDIA &&
                   Regex.IsMatch(gpu.Name, @"RTX\s?20(60|70|80)|TITAN\s?RTX|Quadro\s?RTX\s?\d{4}", RegexOptions.IgnoreCase);
        }

        /// <summary>Nvidia Ampere: GeForce RTX 30 series (and the GA107-based RTX 2050 Laptop) plus the
        /// RTX A-series workstation cards (A500–A6000). The "RTX 2000/4000/… Ada" workstation cards are
        /// Ada Lovelace and don't match.</summary>
        public static bool IsAmpere(GpuInfo? gpu)
        {
            return gpu != null && gpu.Vendor == GpuVendor.NVIDIA &&
                   Regex.IsMatch(gpu.Name, @"RTX\s?(30(50|60|70|80|90)|2050)|RTX\s?A\d{3,4}(?![0-9])", RegexOptions.IgnoreCase);
        }

        /// <summary>GPUs dlssg_for_sm86 targets: RTX 20 (Turing) and RTX 30 (Ampere), which have
        /// tensor cores but no official DLSS Frame Generation.</summary>
        public static bool IsDlssgSm86Capable(GpuInfo? gpu) => IsTuringRtx(gpu) || IsAmpere(gpu);
    }
}
