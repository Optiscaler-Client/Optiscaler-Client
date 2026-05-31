// OptiScaler Client - A frontend for managing OptiScaler installations
// Copyright (C) 2026 Agustín Montaña (Agustinm28)
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;

namespace OptiscalerClient.Services;

/// <summary>
/// Central factory for platform-specific service implementations.
/// Callers obtain the correct implementation without scattering
/// <c>OperatingSystem.IsWindows()</c> guards throughout the codebase.
/// </summary>
public static class PlatformServiceFactory
{
    /// <summary>Returns the <see cref="IShellService"/> for the current OS.</summary>
    public static IShellService CreateShellService()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsShellService();
        return new XdgShellService();
    }

    /// <summary>Returns the <see cref="IGpuDetectionService"/> for the current OS,
    /// or <c>null</c> on unsupported platforms.</summary>
    public static IGpuDetectionService? CreateGpuDetectionService()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsGpuDetectionService();
        if (OperatingSystem.IsLinux())
            return new LinuxGpuDetectionService();
        return null;
    }

    /// <summary>Returns the <see cref="IGamepadDetectionService"/> for the current OS,
    /// or <c>null</c> on unsupported platforms.</summary>
    public static IGamepadDetectionService? CreateGamepadDetectionService()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsGamepadDetectionService();
        if (OperatingSystem.IsLinux())
            return new LinuxGamepadDetectionService();
        return null;
    }

    // ── Private implementations ────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private sealed class WindowsShellService : IShellService
    {
        public void OpenFolder(string path)
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
            {
                UseShellExecute = true
            });
        }

        public void OpenUrl(string url) =>
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private sealed class XdgShellService : IShellService
    {
        private static readonly string[] _candidates = ["xdg-open", "/usr/bin/xdg-open", "open"];

        public void OpenFolder(string path)
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            LaunchXdg(path);
        }

        public void OpenUrl(string url) => LaunchXdg(url);

        private static void LaunchXdg(string target)
        {
            foreach (var exe in _candidates)
            {
                try
                {
                    var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
                    psi.ArgumentList.Add(target);
                    Process.Start(psi);
                    return;
                }
                catch (System.ComponentModel.Win32Exception) { /* try next */ }
            }
            throw new System.InvalidOperationException(
                $"Could not find a suitable program to open '{target}'. " +
                $"Tried: {string.Join(", ", _candidates)}");
        }
    }
}
