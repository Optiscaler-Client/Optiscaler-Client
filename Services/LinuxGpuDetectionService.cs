using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace OptiscalerClient.Services;

[SupportedOSPlatform("linux")]
public class LinuxGpuDetectionService : IGpuDetectionService
{
    public GpuInfo[] DetectGPUs()
    {
        try
        {
            var gpus = new List<GpuInfo>();
            const string drmPath = "/sys/class/drm";

            if (!Directory.Exists(drmPath))
                return Array.Empty<GpuInfo>();

            foreach (var cardDir in Directory.GetDirectories(drmPath, "card*"))
            {
                // Only process top-level card entries (card0, card1, ...) not sub-connectors
                if (!Regex.IsMatch(Path.GetFileName(cardDir), @"^card\d+$"))
                    continue;

                var devicePath = Path.Combine(cardDir, "device");
                if (!Directory.Exists(devicePath)) continue;

                var vendorFile = Path.Combine(devicePath, "vendor");
                if (!File.Exists(vendorFile)) continue;

                var vendorId = File.ReadAllText(vendorFile).Trim().ToLowerInvariant();
                var vendor = vendorId switch
                {
                    "0x10de" => GpuVendor.NVIDIA,
                    "0x1002" => GpuVendor.AMD,
                    "0x8086" => GpuVendor.Intel,
                    _ => GpuVendor.Unknown
                };

                if (vendor == GpuVendor.Unknown) continue;

                var gpu = new GpuInfo
                {
                    Vendor = vendor,
                    Name = GetGpuName(devicePath, vendor),
                    VideoMemoryBytes = GetVram(devicePath, vendor),
                    DriverVersion = GetDriverVersion(vendor)
                };

                gpus.Add(gpu);
            }

            return gpus.ToArray();
        }
        catch
        {
            return Array.Empty<GpuInfo>();
        }
    }

    private string GetGpuName(string devicePath, GpuVendor vendor)
    {
        try
        {
            var vendorIdRaw = File.ReadAllText(Path.Combine(devicePath, "vendor")).Trim();
            var deviceIdRaw = File.Exists(Path.Combine(devicePath, "device"))
                ? File.ReadAllText(Path.Combine(devicePath, "device")).Trim()
                : "";

            var shortVendor = vendorIdRaw.Replace("0x", "").PadLeft(4, '0').ToLowerInvariant();
            var shortDevice = deviceIdRaw.Replace("0x", "").PadLeft(4, '0').ToLowerInvariant();

            // 0. Vendor-specific databases/tools give the exact retail model, which the generic PCI
            //    tables cannot (they share one entry across a whole chip family, e.g.
            //    "Radeon RX 9070/9070 XT/9070 GRE" for every Navi 48 board).
            var exactName = vendor switch
            {
                GpuVendor.AMD => LookupAmdgpuIds(shortDevice, GetPciRevision(devicePath)),
                GpuVendor.NVIDIA => QueryNvidiaSmiName(devicePath),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(exactName))
                return exactName;

            // 1. Try lspci (fastest, most accurate)
            var lspciOutput = RunProcess("lspci", $"-d {shortVendor}:{shortDevice} -mm", timeoutMs: 2000);
            if (!string.IsNullOrWhiteSpace(lspciOutput))
            {
                var parts = Regex.Matches(lspciOutput, "\"([^\"]*)\"");
                if (parts.Count >= 3)
                    return BuildCleanGpuName(vendor, parts[2].Groups[1].Value);
            }

            // 2. Fallback: parse pci.ids directly (works without lspci installed)
            var (idsVendorName, idsDeviceName) = LookupPciIds(shortVendor, shortDevice);
            if (!string.IsNullOrEmpty(idsDeviceName))
                return BuildCleanGpuName(vendor, idsDeviceName);
            if (!string.IsNullOrEmpty(idsVendorName))
                return idsVendorName;
        }
        catch { }

        return vendor switch
        {
            GpuVendor.NVIDIA => "NVIDIA GPU",
            GpuVendor.AMD => "AMD GPU",
            GpuVendor.Intel => "Intel GPU",
            _ => "Unknown GPU"
        };
    }

    /// <summary>
    /// pci.ids / lspci -mm device fields carry the chip codename plus the marketing name in a
    /// trailing bracket, e.g. "Navi 48 [Radeon RX 9070/9070 XT/9070 GRE]". Keep only that bracketed
    /// marketing name and prefix a short vendor label, instead of the raw codename + full legal
    /// vendor string (e.g. "Advanced Micro Devices, Inc. [AMD/ATI] Navi 48 [Radeon RX ...]"), which
    /// is what fastfetch/neofetch-style tools show and what fits in a UI badge.
    /// </summary>
    private static string BuildCleanGpuName(GpuVendor vendor, string deviceField)
    {
        var marketingName = ExtractBracketedMarketingName(deviceField);
        var vendorLabel = vendor switch
        {
            GpuVendor.NVIDIA => "NVIDIA",
            GpuVendor.AMD => "AMD",
            GpuVendor.Intel => "Intel",
            _ => ""
        };

        return string.IsNullOrEmpty(vendorLabel) ? marketingName : $"{vendorLabel} {marketingName}".Trim();
    }

    private static string ExtractBracketedMarketingName(string deviceField)
    {
        var match = Regex.Match(deviceField, @"\[([^\[\]]+)\]\s*$");
        return match.Success ? match.Groups[1].Value.Trim() : deviceField.Trim();
    }

    private static readonly string[] _amdgpuIdsPaths =
    [
        "/usr/share/libdrm/amdgpu.ids",
        "/usr/local/share/libdrm/amdgpu.ids",
    ];

    /// <summary>
    /// Reads the PCI revision of the device as an uppercase hex string without the "0x" prefix
    /// (sysfs exposes e.g. "0xc0" -> "C0"), which is the form amdgpu.ids uses.
    /// </summary>
    private static string? GetPciRevision(string devicePath)
    {
        try
        {
            var revFile = Path.Combine(devicePath, "revision");
            if (!File.Exists(revFile)) return null;

            var rev = File.ReadAllText(revFile).Trim();
            if (rev.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                rev = rev.Substring(2);

            return string.IsNullOrEmpty(rev) ? null : rev.PadLeft(2, '0').ToUpperInvariant();
        }
        catch { return null; }
    }

    /// <summary>
    /// Looks up the exact AMD marketing name in libdrm's amdgpu.ids database, which keys on
    /// device id + PCI revision and therefore distinguishes boards sharing a device id
    /// (7550/C0 = "AMD Radeon RX 9070 XT", 7550/C3 = "AMD Radeon RX 9070", ...).
    /// This is the same source tools like LACT use.
    /// Format: "device_id,\trevision_id,\tproduct_name".
    /// </summary>
    private static string? LookupAmdgpuIds(string deviceId, string? revisionId)
    {
        if (string.IsNullOrEmpty(revisionId)) return null;

        try
        {
            var idsFile = _amdgpuIdsPaths.FirstOrDefault(File.Exists);
            if (idsFile == null) return null;

            foreach (var line in File.ReadLines(idsFile))
            {
                if (line.Length == 0 || line.StartsWith('#')) continue;

                var parts = line.Split(',');
                if (parts.Length < 3) continue;

                if (!parts[0].Trim().Equals(deviceId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!parts[1].Trim().Equals(revisionId, StringComparison.OrdinalIgnoreCase)) continue;

                var name = string.Join(',', parts.Skip(2)).Trim();
                if (!string.IsNullOrEmpty(name))
                    return name;
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Asks the proprietary driver for the exact model name, matching by PCI bus id so multi-GPU
    /// systems map each sysfs card to its own entry.
    /// </summary>
    private string? QueryNvidiaSmiName(string devicePath)
    {
        try
        {
            var busId = GetPciBusId(devicePath);
            var output = RunProcess("nvidia-smi", "--query-gpu=pci.bus_id,name --format=csv,noheader", timeoutMs: 3000, readAllLines: true);
            if (string.IsNullOrWhiteSpace(output)) return null;

            string? firstName = null;
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var sep = line.IndexOf(',');
                if (sep < 0) continue;

                var lineBus = line.Substring(0, sep).Trim();
                var name = line.Substring(sep + 1).Trim();
                if (string.IsNullOrEmpty(name)) continue;

                firstName ??= name;

                // nvidia-smi pads the domain ("00000000:03:00.0" vs sysfs "0000:03:00.0")
                if (busId != null && lineBus.EndsWith(busId, StringComparison.OrdinalIgnoreCase))
                    return name;
            }

            // Single-GPU systems: no bus id match needed
            return busId == null ? firstName : null;
        }
        catch { return null; }
    }

    private static string? GetPciBusId(string devicePath)
    {
        try
        {
            var resolved = Directory.ResolveLinkTarget(devicePath, returnFinalTarget: true)?.FullName ?? devicePath;
            var busId = Path.GetFileName(resolved.TrimEnd(Path.DirectorySeparatorChar));
            return Regex.IsMatch(busId, @"^[0-9a-fA-F]{4}:[0-9a-fA-F]{2}:[0-9a-fA-F]{2}\.\d$") ? busId : null;
        }
        catch { return null; }
    }

    private static readonly string[] _pciIdsPaths =
    [
        "/usr/share/hwdata/pci.ids",
        "/usr/share/misc/pci.ids",
        "/usr/share/pci.ids",
    ];

    /// <summary>
    /// Looks up vendor + device names from a local pci.ids database file.
    /// Format:
    ///   vendorid  Vendor Name
    ///   \tdeviceid  Device Name
    /// </summary>
    private static (string? VendorName, string? DeviceName) LookupPciIds(string vendorId, string deviceId)
    {
        try
        {
            var idsFile = _pciIdsPaths.FirstOrDefault(File.Exists);
            if (idsFile == null) return (null, null);

            string? vendorName = null;
            string? deviceName = null;
            bool inVendor = false;

            foreach (var line in File.ReadLines(idsFile))
            {
                if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line))
                    continue;

                if (!line.StartsWith('\t'))
                {
                    // vendor line: "10de  NVIDIA Corporation"
                    inVendor = line.StartsWith(vendorId, StringComparison.OrdinalIgnoreCase);
                    if (inVendor)
                        vendorName = line.Substring(4).Trim();
                    else if (vendorName != null)
                        break; // past our vendor section
                }
                else if (inVendor && line.StartsWith('\t') && !line.StartsWith("\t\t"))
                {
                    // device line: "\t687f  Vega 10 XL/XT [Radeon RX Vega 56/64]"
                    var stripped = line.TrimStart('\t');
                    if (stripped.StartsWith(deviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        deviceName = stripped.Substring(4).Trim();
                        break;
                    }
                }
            }

            return (vendorName, deviceName);
        }
        catch { }

        return (null, null);
    }

    private ulong GetVram(string devicePath, GpuVendor vendor)
    {
        // AMD exposes VRAM size directly via sysfs
        if (vendor == GpuVendor.AMD)
        {
            var vramFile = Path.Combine(devicePath, "mem_info_vram_total");
            if (File.Exists(vramFile) && ulong.TryParse(File.ReadAllText(vramFile).Trim(), out var vram))
                return vram;
        }

        // NVIDIA: query via nvidia-smi (returns MiB)
        if (vendor == GpuVendor.NVIDIA)
        {
            var output = RunProcess("nvidia-smi", "--query-gpu=memory.total --format=csv,noheader,nounits", timeoutMs: 3000);
            if (ulong.TryParse(output?.Trim(), out var mb))
                return mb * 1024 * 1024;
        }

        return 0;
    }

    private string GetDriverVersion(GpuVendor vendor)
    {
        if (vendor == GpuVendor.NVIDIA)
        {
            var output = RunProcess("nvidia-smi", "--query-gpu=driver_version --format=csv,noheader", timeoutMs: 3000);
            if (!string.IsNullOrWhiteSpace(output))
                return output.Trim();
        }

        var modulePath = vendor switch
        {
            GpuVendor.AMD => "/sys/module/amdgpu/version",
            GpuVendor.Intel => "/sys/module/i915/version",
            _ => null
        };

        if (modulePath != null && File.Exists(modulePath))
            return File.ReadAllText(modulePath).Trim();

        return "Unknown";
    }

    private string? RunProcess(string fileName, string args, int timeoutMs, bool readAllLines = false)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, args)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = readAllLines ? proc.StandardOutput.ReadToEnd() : proc.StandardOutput.ReadLine();
            proc.WaitForExit(timeoutMs);
            return output;
        }
        catch
        {
            return null;
        }
    }

    public GpuInfo? GetPrimaryGPU()
    {
        var gpus = DetectGPUs();
        return gpus.Length > 0 ? gpus[0] : null;
    }

    public GpuInfo? GetDiscreteGPU()
    {
        var gpus = DetectGPUs();
        var discreteGpus = gpus.Where(g => g.VideoMemoryBytes > 2L * 1024 * 1024 * 1024).ToArray();

        if (discreteGpus.Length == 0)
            return gpus.Length > 0 ? gpus[0] : null;

        return discreteGpus.FirstOrDefault(g => g.Vendor == GpuVendor.NVIDIA)
            ?? discreteGpus.FirstOrDefault(g => g.Vendor == GpuVendor.AMD)
            ?? discreteGpus[0];
    }

    public bool HasGPU(GpuVendor vendor)
    {
        return DetectGPUs().Any(g => g.Vendor == vendor);
    }

    public string GetGPUDescription()
    {
        var gpus = DetectGPUs();

        if (gpus.Length == 0)
            return "No GPU detected";

        if (gpus.Length == 1)
            return $"{GetVendorIcon(gpus[0].Vendor)} {gpus[0].Name}";

        var discrete = GetDiscreteGPU();
        if (discrete != null)
            return $"{GetVendorIcon(discrete.Vendor)} {discrete.Name} (+{gpus.Length - 1} more)";

        return $"{gpus.Length} GPUs detected";
    }

    private static string GetVendorIcon(GpuVendor vendor) => vendor switch
    {
        GpuVendor.NVIDIA => "🟢",
        GpuVendor.AMD => "🔴",
        GpuVendor.Intel => "🔵",
        _ => "⚪"
    };
}
