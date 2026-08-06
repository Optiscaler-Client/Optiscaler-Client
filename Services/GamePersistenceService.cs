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
            return JsonSerializer.Deserialize(json, OptimizerContext.Default.ListGame) ?? new List<Game>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GamePersistence] Failed to load games: {ex.Message}");
            return new List<Game>();
        }
    }
}
