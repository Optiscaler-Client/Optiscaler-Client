using Microsoft.Win32;
using System.IO;
using System.Runtime.Versioning;

namespace OptiscalerClient.Services;

/// <summary>
/// Shared helper for reading the Windows "Uninstall" registry hive. Several scanners
/// (Battle.net, Ubisoft, ...) walk the same uninstall keys and read the same values;
/// centralising the enumeration keeps that logic in one place.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsUninstallRegistryHelper
{
    private static readonly string[] UninstallPaths =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    };

    public readonly record struct Entry(string SubKeyName, string? DisplayName, string? InstallLocation, string? Publisher);

    /// <summary>Enumerates every uninstall entry under the machine hive (64-bit view).</summary>
    public static List<Entry> GetEntries()
    {
        var entries = new List<Entry>();

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (var uninstallPath in UninstallPaths)
            {
                using var uninstallKey = baseKey.OpenSubKey(uninstallPath);
                if (uninstallKey == null) continue;

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    using var appKey = uninstallKey.OpenSubKey(subKeyName);
                    if (appKey == null) continue;

                    entries.Add(new Entry(
                        subKeyName,
                        appKey.GetValue("DisplayName") as string,
                        appKey.GetValue("InstallLocation") as string,
                        appKey.GetValue("Publisher") as string));
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WindowsUninstallRegistry] Error scanning registry: {ex.Message}");
        }

        return entries;
    }
}
