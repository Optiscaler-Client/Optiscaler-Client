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

using OptiscalerClient.Models;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OptiscalerClient.Services;

/// <summary>
/// Scans Lutris (Linux-only game manager for Wine/native titles) for installed games.
/// Lutris has no Windows client, so this scanner is a no-op on Windows.
/// </summary>
public class LutrisScanner : IGameScanner
{
    private class LutrisGameSection
    {
        public string? Exe { get; set; }
    }

    private class LutrisGameConfig
    {
        public LutrisGameSection? Game { get; set; }
    }

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public List<Game> Scan()
    {
        var games = new List<Game>();

        if (OperatingSystem.IsWindows())
            return games;

        foreach (var configDir in GetLutrisConfigDirectories())
        {
            var gamesDir = Path.Combine(configDir, "games");
            if (!Directory.Exists(gamesDir))
                continue;

            foreach (var yamlPath in Directory.GetFiles(gamesDir, "*.yml"))
            {
                try
                {
                    var game = ParseGameYaml(yamlPath);
                    if (game != null && Directory.Exists(game.InstallPath))
                        games.Add(game);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Lutris] Error parsing '{yamlPath}': {ex.Message}");
                }
            }
        }

        return games;
    }

    private Game? ParseGameYaml(string yamlPath)
    {
        var yaml = File.ReadAllText(yamlPath);
        var config = YamlDeserializer.Deserialize<LutrisGameConfig>(yaml);

        var exePath = config?.Game?.Exe;
        if (string.IsNullOrWhiteSpace(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null; // Native Linux titles don't go through OptiScaler.

        if (!File.Exists(exePath))
            return null;

        var slug = Path.GetFileNameWithoutExtension(yamlPath);

        return new Game
        {
            AppId = slug,
            Name = DeriveNameFromSlug(slug),
            InstallPath = Path.GetDirectoryName(exePath) ?? exePath,
            ExecutablePath = exePath,
            Platform = GamePlatform.Lutris
        };
    }

    /// <summary>
    /// Lutris config filenames follow "&lt;game-slug&gt;-&lt;numeric-id&gt;.yml"; Lutris keeps the
    /// human-readable title in its local SQLite database rather than in the per-game YAML, so the
    /// slug (with the trailing id stripped and words title-cased) is the closest name we can derive
    /// without pulling in a database dependency for a single field.
    /// </summary>
    private static string DeriveNameFromSlug(string slug)
    {
        var withoutId = Regex.Replace(slug, @"-\d+$", "");
        var words = withoutId.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]);

        var name = string.Join(" ", words);
        return string.IsNullOrWhiteSpace(name) ? slug : name;
    }

    private static IEnumerable<string> GetLutrisConfigDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            Path.Combine(home, ".local", "share", "lutris"),
            Path.Combine(home, ".var", "app", "net.lutris.Lutris", "data", "lutris"),
        };
    }
}
