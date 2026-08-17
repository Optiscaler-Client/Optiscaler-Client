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

using System.IO;
using System.Text.Json;
using OptiscalerClient.Models;

namespace OptiscalerClient.Services;

public class GamePersistenceService
{
    private readonly string _filePath;

    public GamePersistenceService()
    {
        var folder = AppPaths.GetAppDataRoot();
        _filePath = Path.Combine(folder, "games.json");
    }

    public void SaveGames(IEnumerable<Game> games)
    {
        var json = JsonSerializer.Serialize(games.ToList(), OptimizerContext.Default.ListGame);
        File.WriteAllText(_filePath, json);
    }

    public List<Game> LoadGames()
    {
        if (!File.Exists(_filePath))
        {
            return new List<Game>();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var games = JsonSerializer.Deserialize(json, OptimizerContext.Default.ListGame) ?? new List<Game>();
            MigrateMisclassifiedManualGames(games);
            return games;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GamePersistence] Failed to load games: {ex.Message}");
            return new List<Game>();
        }
    }

    // 1.0.6 inserted GamePlatform.Lutris before Manual in the enum, shifting its integer
    // value. Since Platform is persisted as a raw int, every game added manually under
    // 1.0.5 deserializes as Lutris instead of Manual after upgrading. Manually-added
    // games always get an AppId of "Manual_<guid>" (see BtnAddManual_Click), while real
    // Lutris scans always use the Lutris slug as AppId, so that prefix reliably tells
    // the two apart without risking a genuine Lutris game.
    private void MigrateMisclassifiedManualGames(List<Game> games)
    {
        bool migrated = false;

        foreach (var game in games)
        {
            if (game.Platform == GamePlatform.Lutris && game.AppId.StartsWith("Manual_", StringComparison.Ordinal))
            {
                game.Platform = GamePlatform.Manual;
                migrated = true;
            }
        }

        if (migrated)
        {
            System.Diagnostics.Debug.WriteLine("[GamePersistence] Migrated misclassified Manual games back from Lutris.");
            SaveGames(games);
        }
    }
}
