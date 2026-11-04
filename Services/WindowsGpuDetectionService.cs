using System.Management;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace OptiscalerClient.Services
{
    /// <summary>
    /// Service to detect GPU information using WMI
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class WindowsGpuDetectionService : IGpuDetectionService
    {
        private static readonly object _cacheLock = new();
        private static GpuInfo[]? _cachedGpus;
        private static DateTime _cacheTimestampUtc;
        // Detection results are effectively immutable for the process lifetime, but allow a
        // periodic refresh so a transient WMI/registry failure can recover without a restart.
        private static readonly TimeSpan _cacheLifetime = TimeSpan.FromMinutes(5);

        private static List<RegistryVramEntry>? _cachedRegistryEntries;

        private readonly record struct RegistryVramEntry(string? AdapterString, string? DriverDesc, ulong Bytes);

        /// <summary>
        /// Detects all GPUs in the system
        /// </summary>
        public GpuInfo[] DetectGPUs()
        {
            lock (_cacheLock)
            {
                if (_cachedGpus != null && DateTime.UtcNow - _cacheTimestampUtc < _cacheLifetime)
                {
                    return _cachedGpus;
                }
            }

            var gpus = DetectGpusCore();

            lock (_cacheLock)
            {
                _cachedGpus = gpus;
                _cacheTimestampUtc = DateTime.UtcNow;
                return _cachedGpus;
            }
        }

        private GpuInfo[] DetectGpusCore()
        {
            try
            {
                var gpus = new System.Collections.Generic.List<GpuInfo>();

                using (var searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion, AdapterRAM FROM Win32_VideoController"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject obj in results)
                    {
                        using (obj)
                        {
                            var gpu = new GpuInfo();

                            // Get GPU name
                            gpu.Name = obj["Name"]?.ToString() ?? "Unknown GPU";

                            // Detect vendor from name
                            gpu.Vendor = DetectVendorFromName(gpu.Name);

                            // Get driver version
                            gpu.DriverVersion = obj["DriverVersion"]?.ToString() ?? "Unknown";

                            // Get video memory (in bytes) using WMI + registry fallback.
                            gpu.VideoMemoryBytes = GetBestVideoMemoryBytes(obj, gpu.Name);

                            gpus.Add(gpu);
                        }
                    }
                }

                return gpus.ToArray();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GPU] WMI detection failed: {ex.Message}");
                return Array.Empty<GpuInfo>();
            }
        }

        /// <summary>
        /// Gets the primary (first) GPU
        /// </summary>
        public GpuInfo? GetPrimaryGPU()
        {
            var gpus = DetectGPUs();
            return gpus.Length > 0 ? gpus[0] : null;
        }

        /// <summary>
        /// Gets the most powerful discrete GPU (prefers NVIDIA > AMD > Intel)
        /// </summary>
        public GpuInfo? GetDiscreteGPU()
        {
            var gpus = DetectGPUs();

            // Filter out integrated GPUs (usually have less VRAM)
            var discreteGpus = gpus.Where(g => g.VideoMemoryBytes > 2L * 1024 * 1024 * 1024).ToArray(); // > 2GB

            if (discreteGpus.Length == 0)
                return gpus.Length > 0 ? gpus[0] : null;

            // Prefer NVIDIA, then AMD, then Intel
            var nvidia = discreteGpus.FirstOrDefault(g => g.Vendor == GpuVendor.NVIDIA);
            if (nvidia != null) return nvidia;

            var amd = discreteGpus.FirstOrDefault(g => g.Vendor == GpuVendor.AMD);
            if (amd != null) return amd;

            var intel = discreteGpus.FirstOrDefault(g => g.Vendor == GpuVendor.Intel);
            if (intel != null) return intel;

            return discreteGpus[0];
        }

        /// <summary>
        /// Detects GPU vendor from the GPU name string
        /// </summary>
        private GpuVendor DetectVendorFromName(string gpuName)
        {
            if (string.IsNullOrEmpty(gpuName))
                return GpuVendor.Unknown;

            var nameLower = gpuName.ToLowerInvariant();

            // NVIDIA detection
            if (nameLower.Contains("nvidia") ||
                nameLower.Contains("geforce") ||
                nameLower.Contains("quadro") ||
                nameLower.Contains("tesla") ||
                nameLower.Contains("rtx") ||
                nameLower.Contains("gtx"))
            {
                return GpuVendor.NVIDIA;
            }

            // AMD detection
            if (nameLower.Contains("amd") ||
                nameLower.Contains("radeon") ||
                nameLower.Contains("rx ") ||
                nameLower.Contains("vega") ||
                nameLower.Contains("navi"))
            {
                return GpuVendor.AMD;
            }

            // Intel detection
            if (nameLower.Contains("intel") ||
                nameLower.Contains("iris") ||
                nameLower.Contains("uhd graphics") ||
                nameLower.Contains("hd graphics") ||
                nameLower.Contains("arc"))
            {
                return GpuVendor.Intel;
            }

            return GpuVendor.Unknown;
        }

        /// <summary>
        /// Checks if the system has a specific GPU vendor
        /// </summary>
        public bool HasGPU(GpuVendor vendor)
        {
            var gpus = DetectGPUs();
            return gpus.Any(g => g.Vendor == vendor);
        }

        /// <summary>
        /// Gets a user-friendly description of the GPU setup
        /// </summary>
        public string GetGPUDescription()
        {
            var gpus = DetectGPUs();

            if (gpus.Length == 0)
                return "No GPU detected";

            if (gpus.Length == 1)
                return $"{GetVendorIcon(gpus[0].Vendor)} {gpus[0].Name}";

            // Multiple GPUs
            var discrete = GetDiscreteGPU();
            if (discrete != null)
                return $"{GetVendorIcon(discrete.Vendor)} {discrete.Name} (+{gpus.Length - 1} more)";

            return $"{gpus.Length} GPUs detected";
        }

        /// <summary>
        /// Gets the best available video memory in bytes for a given GPU
        /// </summary>
        private ulong GetBestVideoMemoryBytes(ManagementObject gpuObject, string gpuName)
        {
            var adapterRamBytes = TryGetAdapterRam(gpuObject);
            var registryBytes = TryGetRegistryVram(gpuName);

            if (adapterRamBytes == 0)
                return registryBytes;

            if (registryBytes == 0)
                return adapterRamBytes;

            // Some systems cap AdapterRAM around ~4 GB for larger cards.
            return Math.Max(adapterRamBytes, registryBytes);
        }

        private ulong TryGetAdapterRam(ManagementObject gpuObject)
        {
            try
            {
                if (gpuObject["AdapterRAM"] == null)
                    return 0;

                return Convert.ToUInt64(gpuObject["AdapterRAM"]);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GPU] WMI AdapterRAM read failed: {ex.Message}");
                return 0;
            }
        }

        private ulong TryGetRegistryVram(string gpuName)
        {
            foreach (var entry in GetCachedRegistryEntries())
            {
                if (IsGpuNameMatch(gpuName, entry.AdapterString, entry.DriverDesc))
                {
                    return entry.Bytes;
                }
            }

            return 0;
        }

        private List<RegistryVramEntry> GetCachedRegistryEntries()
        {
            lock (_cacheLock)
            {
                if (_cachedRegistryEntries != null)
                {
                    return _cachedRegistryEntries;
                }
            }

            var entries = ScanRegistryEntries();

            lock (_cacheLock)
            {
                if (_cachedRegistryEntries == null)
                {
                    _cachedRegistryEntries = entries;
                }

                return _cachedRegistryEntries;
            }
        }

        private static List<RegistryVramEntry> ScanRegistryEntries()
        {
            var entries = new List<RegistryVramEntry>();

            try
            {
                using var videoRoot = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video");
                if (videoRoot == null)
                    return entries;

                foreach (var adapterGuid in videoRoot.GetSubKeyNames())
                {
                    using var adapterKey = videoRoot.OpenSubKey(adapterGuid);
                    if (adapterKey == null)
                        continue;

                    foreach (var childName in adapterKey.GetSubKeyNames())
                    {
                        if (!childName.StartsWith("0", StringComparison.Ordinal))
                            continue;

                        using var settingsKey = adapterKey.OpenSubKey(childName);
                        if (settingsKey == null)
                            continue;

                        var adapterString = settingsKey.GetValue("HardwareInformation.AdapterString")?.ToString();
                        var driverDesc = settingsKey.GetValue("DriverDesc")?.ToString();

                        var value = settingsKey.GetValue("HardwareInformation.qwMemorySize");
                        var bytes = TryConvertRegistryMemoryValue(value);
                        if (bytes > 0)
                        {
                            entries.Add(new RegistryVramEntry(adapterString, driverDesc, bytes));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GPU] Registry VRAM lookup failed: {ex.Message}");
            }

            return entries;
        }

        private bool IsGpuNameMatch(string gpuName, string? adapterString, string? driverDesc)
        {
            if (string.IsNullOrWhiteSpace(gpuName))
                return true;

            return ContainsIgnoreCase(gpuName, adapterString)
                || ContainsIgnoreCase(gpuName, driverDesc)
                || ContainsIgnoreCase(adapterString, gpuName)
                || ContainsIgnoreCase(driverDesc, gpuName);
        }

        private bool ContainsIgnoreCase(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            return left.IndexOf(right, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static ulong TryConvertRegistryMemoryValue(object? value)
        {
            try
            {
                return value switch
                {
                    ulong u => u,
                    long l when l > 0 => (ulong)l,
                    uint ui => ui,
                    int i when i > 0 => (ulong)i,
                    byte[] bytes when bytes.Length >= 8 => BitConverter.ToUInt64(bytes, 0),
                    _ => 0
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GPU] Registry memory conversion failed: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Gets an emoji/icon for the GPU vendor
        /// </summary>
        private string GetVendorIcon(GpuVendor vendor)
        {
            return vendor switch
            {
                GpuVendor.NVIDIA => "🟢",
                GpuVendor.AMD => "🔴",
                GpuVendor.Intel => "🔵",
                _ => "⚪"
            };
        }
    }
}
