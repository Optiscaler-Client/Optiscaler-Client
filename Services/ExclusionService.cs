using OptiscalerClient.Models;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OptiscalerClient.Services;

/// <summary>
/// Loads the scan exclusion list from config.json and exposes
/// a fast IsExcluded(Game) check used by the scanner.
///
/// Matching rules (OR logic — either condition is enough to exclude):
///   1. Game.Name  == exclusion.Name         (case-insensitive, exact)
///   2. Game.InstallPath contains exclusion.PathSegment  (case-insensitive)
///
/// Empty Name or empty PathSegment means that field is not used for matching.
/// </summary>
public class ExclusionService
{
    private readonly List<ScanExclusion> _exclusions;

    public ExclusionService(string configPath)
    {
        _exclusions = Load(configPath);
    }

    /// <summary>Returns true if the game should be skipped during scanning.</summary>
    public bool IsExcluded(Game game)
    {
        foreach (var rule in _exclusions)
        {
            // Match by name
            if (!string.IsNullOrWhiteSpace(rule.Name) &&
                game.Name.Equals(rule.Name, StringComparison.OrdinalIgnoreCase))
                return true;

            // Match by path segment (contains, case-insensitive)
            if (!string.IsNullOrWhiteSpace(rule.PathSegment) &&
                game.InstallPath.Contains(rule.PathSegment, StringComparison.OrdinalIgnoreCase))
                return true;

            // Match by regex pattern (case-insensitive, optional)
            if (!string.IsNullOrWhiteSpace(rule.PathRegex) &&
                Regex.IsMatch(game.InstallPath, rule.PathRegex, RegexOptions.IgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>All loaded exclusion rules — exposed so UI can display them.</summary>
    public IReadOnlyList<ScanExclusion> Exclusions => _exclusions;

    // ─────────────────────────────────────────────────────────────────────
    private static List<ScanExclusion> Load(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
                return DefaultExclusions();

            var json = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("ScanExclusions", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return DefaultExclusions();

            var list = new List<ScanExclusion>();
            foreach (var item in arr.EnumerateArray())
            {
                list.Add(new ScanExclusion
                {
                    Name = item.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "",
                    PathSegment = item.TryGetProperty("PathSegment", out var p) ? p.GetString() ?? "" : ""
                });
            }
            return list;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ExclusionService] Failed to load exclusions: {ex.Message}");
            return DefaultExclusions();
        }
    }

    /// <summary>
    /// Fallback list used when config.json is missing or malformed.
    /// Mirrors the defaults in config.json so the app always works out-of-the-box.
    /// </summary>
    private static List<ScanExclusion> DefaultExclusions() =>
    [
        new() { Name = "Wallpaper Engine",                   PathSegment = "wallpaper_engine"      },
        new() { Name = "Steamworks Common Redistributables", PathSegment = "Steamworks Shared"     },
        new() { Name = "Steam Linux Runtime",                PathSegment = "SteamLinuxRuntime"      },
        // All Proton variants: covers "Proton 8.0-5", "Proton Experimental", "Proton Hotfix",
        // "Proton EasyAntiCheat Runtime", "Proton BattlEye Runtime", and future names like
        // "Proton-9.0" that use a dash instead of a space.
        new() { Name = "Proton",                             PathRegex = @"[/\\]Proton[ \-]"      },
        new() { Name = "SteamVR",                            PathSegment = "SteamVR"               }
    ];
}
