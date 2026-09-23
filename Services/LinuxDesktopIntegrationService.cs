using System;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Platform;
using OptiscalerClient.Views;

namespace OptiscalerClient.Services;

/// <summary>
/// Linux only. Desktops like GNOME 45+ ignore the icon a window sets on itself and only show an app
/// icon when the window's WM_CLASS matches an installed .desktop entry (StartupWMClass). Packaged
/// builds (AUR, AppImage, Flatpak) ship that entry; portable/tarball and dev runs don't, so this
/// registers a per-user one in ~/.local/share/applications pointing at the running executable.
/// It uses the same desktop id as the packaged entry, so it overrides an older package's entry
/// instead of adding a duplicate, and it removes itself once a package provides a proper entry.
/// </summary>
public sealed class LinuxDesktopIntegrationService
{
    private const string DesktopId = "optiscaler-client.desktop";
    private const string WmClass = "OptiscalerClient";
    private const string GeneratedMarker = "X-OptiscalerClient-Generated=true";

    public void EnsureRegistered()
    {
        if (!OperatingSystem.IsLinux()) return;

        try
        {
            // Flatpak exports its own entry and the shell matches sandboxed apps by app id.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLATPAK_ID"))) return;

            var userDesktopPath = Path.Combine(GetDataHome(), "applications", DesktopId);

            if (SystemEntryMatchesWmClass())
            {
                RemoveIfGenerated(userDesktopPath);
                return;
            }

            // An AppImage runs from a temporary mount; launch the .AppImage file itself.
            var exePath = Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage
                ? appImage
                : Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            // Never overwrite an entry the user (or something else) wrote by hand.
            if (File.Exists(userDesktopPath) && !File.ReadAllText(userDesktopPath).Contains(GeneratedMarker)) return;

            var iconPath = EnsureIcon();
            var content = BuildDesktopEntry(exePath, iconPath);
            if (File.Exists(userDesktopPath) && File.ReadAllText(userDesktopPath) == content) return;

            Directory.CreateDirectory(Path.GetDirectoryName(userDesktopPath)!);
            File.WriteAllText(userDesktopPath, content);
            DebugWindow.Log($"[DesktopIntegration] Registered {userDesktopPath} -> {exePath}");
        }
        catch (Exception ex)
        {
            DebugWindow.Log($"[DesktopIntegration] Failed to register desktop entry: {ex.Message}");
        }
    }

    private static string BuildDesktopEntry(string exePath, string? iconPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Desktop Entry]");
        sb.AppendLine("Type=Application");
        sb.AppendLine("Name=Optiscaler Client");
        sb.AppendLine("GenericName=Optiscaler Client");
        sb.AppendLine("Comment=A modern manager for OptiScaler");
        sb.AppendLine($"Exec={QuoteExec(exePath)}");
        if (iconPath != null) sb.AppendLine($"Icon={iconPath}");
        sb.AppendLine("Categories=Utility;");
        sb.AppendLine("StartupNotify=true");
        sb.AppendLine($"StartupWMClass={WmClass}");
        sb.AppendLine("Terminal=false");
        sb.AppendLine(GeneratedMarker);
        return sb.ToString();
    }

    /// <summary>Desktop Entry spec quoting: wrap in double quotes, escape " ` $ and \.</summary>
    private static string QuoteExec(string path)
    {
        var escaped = new StringBuilder();
        foreach (var c in path)
        {
            if (c is '"' or '`' or '$' or '\\') escaped.Append('\\');
            escaped.Append(c);
        }
        return $"\"{escaped}\"";
    }

    /// <summary>Copies the bundled app icon next to the app data, so the entry works without an
    /// icon theme install.</summary>
    private static string? EnsureIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppPaths.GetAppDataRoot(), "optiscaler-client.png");
            using var source = AssetLoader.Open(new Uri("avares://OptiscalerClient/assets/icon.png"));
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            var bytes = buffer.ToArray();

            if (!File.Exists(iconPath) || new FileInfo(iconPath).Length != bytes.Length)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
                File.WriteAllBytes(iconPath, bytes);
            }
            return iconPath;
        }
        catch (Exception ex)
        {
            DebugWindow.Log($"[DesktopIntegration] Failed to write icon: {ex.Message}");
            return null;
        }
    }

    /// <summary>True when a system-wide entry (a package) already maps our WM_CLASS.</summary>
    private static bool SystemEntryMatchesWmClass()
    {
        var dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } dirs
            ? dirs.Split(':', StringSplitOptions.RemoveEmptyEntries)
            : new[] { "/usr/local/share", "/usr/share" };

        return dataDirs
            .Select(dir => Path.Combine(dir, "applications"))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.desktop"))
            .Any(file =>
            {
                try { return File.ReadLines(file).Any(l => l.Trim() == $"StartupWMClass={WmClass}"); }
                catch { return false; }
            });
    }

    private static void RemoveIfGenerated(string path)
    {
        if (File.Exists(path) && File.ReadAllText(path).Contains(GeneratedMarker))
        {
            File.Delete(path);
            DebugWindow.Log($"[DesktopIntegration] Removed {path}: a packaged entry now provides it.");
        }
    }

    private static string GetDataHome() =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } dataHome
            ? dataHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
}
