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
    private List<Regex?> _regexes = new();

    public ExclusionService(string configPath)
    {
        _exclusions = Load(configPath);
    }

    /// <summary>Returns true if the game should be skipped during scanning.</summary>
    public bool IsExcluded(Game game)
    {
        for (var i = 0; i < _exclusions.Count; i++)
        {
            var rule = _exclusions[i];

            // Match by name
            if (!string.IsNullOrWhiteSpace(rule.Name) &&
                game.Name.Equals(rule.Name, StringComparison.OrdinalIgnoreCase))
                return true;

            // Match by path segment (contains, case-insensitive)
            if (!string.IsNullOrWhiteSpace(rule.PathSegment) &&
                game.InstallPath.Contains(rule.PathSegment, StringComparison.OrdinalIgnoreCase))
                return true;

            // Match by regex pattern (case-insensitive, optional), using the
            // compiled regex stored for this rule.
            var regex = _regexes[i];
            if (regex != null && regex.IsMatch(game.InstallPath))
                return true;
        }
        return false;
    }

    /// <summary>All loaded exclusion rules — exposed so UI can display them.</summary>
    public IReadOnlyList<ScanExclusion> Exclusions => _exclusions;

    // ─────────────────────────────────────────────────────────────────────
    private List<ScanExclusion> Load(string configPath)
    {
        List<ScanExclusion> list;
        try
        {
            if (!File.Exists(configPath))
            {
                list = DefaultExclusions();
            }
            else
            {
                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("ScanExclusions", out var arr) ||
                    arr.ValueKind != JsonValueKind.Array)
                {
                    list = DefaultExclusions();
                }
                else
                {
                    list = new List<ScanExclusion>();
                    foreach (var item in arr.EnumerateArray())
                    {
                        // PathRegex used to be dropped here, so a regex rule only ever worked in the
                        // built-in defaults. An invalid pattern is skipped rather than kept: IsExcluded
                        // would otherwise throw on every game and abort the whole scan.
                        var regexPattern = item.TryGetProperty("PathRegex", out var r) ? r.GetString() ?? "" : "";
                        if (!string.IsNullOrWhiteSpace(regexPattern))
                        {
                            try { _ = new Regex(regexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase); }
                            catch (ArgumentException) { regexPattern = ""; }
                        }

                        list.Add(new ScanExclusion
                        {
                            Name = item.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "",
                            PathSegment = item.TryGetProperty("PathSegment", out var p) ? p.GetString() ?? "" : "",
                            PathRegex = regexPattern
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ExclusionService] Failed to load exclusions: {ex.Message}");
            list = DefaultExclusions();
        }

        // Compile/store regex instances once, with the required options.
        _regexes = new List<Regex?>(list.Count);
        foreach (var rule in list)
        {
            if (string.IsNullOrWhiteSpace(rule.PathRegex))
            {
                _regexes.Add(null);
                continue;
            }

            try
            {
                _regexes.Add(new Regex(rule.PathRegex!, RegexOptions.Compiled | RegexOptions.IgnoreCase));
            }
            catch (ArgumentException)
            {
                _regexes.Add(null);
            }
        }

        return list;
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
