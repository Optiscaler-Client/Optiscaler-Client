using System;
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
            return gpu != null && gpu.Vendor == GpuVendor.AMD &&
                   (gpu.Name.Contains(" 9", StringComparison.OrdinalIgnoreCase) ||
                    gpu.Name.Contains("RX 9", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>RDNA 3 desktop (Radeon RX 7000 series) is now also whitelisted by AMD's official
        /// driver for native FSR4 INT8, same as RDNA 4 — no need for this app to inject/force it.
        /// RDNA3 mobile/APU chips (e.g. 780M/760M) aren't covered by that whitelist, so they're
        /// intentionally excluded here and still get the INT8 fallback like older GPUs.</summary>
        public static bool IsRdna3(GpuInfo? gpu)
        {
            return gpu != null && gpu.Vendor == GpuVendor.AMD &&
                   gpu.Name.Contains("RX 7", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>RDNA 2 (Radeon RX 6000 series, plus RDNA2-based APUs/handhelds like Steam Deck's
        /// "Van Gogh" or the Ryzen 6000 mobile "660M"/"680M" iGPUs) needs its own custom amdxc64.dll
        /// loaded via OptiScaler's LoadCustomAmdxc64OnRdna2 to get FSR4 INT8 working.</summary>
        public static bool IsRdna2(GpuInfo? gpu)
        {
            if (gpu == null || gpu.Vendor != GpuVendor.AMD) return false;
            return gpu.Name.Contains("RX 6", StringComparison.OrdinalIgnoreCase) ||
                   gpu.Name.Contains("Van Gogh", StringComparison.OrdinalIgnoreCase) ||
                   gpu.Name.Contains("660M", StringComparison.OrdinalIgnoreCase) ||
                   gpu.Name.Contains("680M", StringComparison.OrdinalIgnoreCase);
        }
    }
}
