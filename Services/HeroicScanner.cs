using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using OptiscalerClient.Models;

namespace OptiscalerClient.Services;

public class HeroicScanner : IGameScanner
{
	private class HeroicGame
	{
	    [JsonPropertyName("appName")]
	    public string AppId { get; set; } = string.Empty;
	    [JsonPropertyName("app_name")]
		private string AppId2 { set { AppId = value; } }
	    [JsonPropertyName("title")]
	    public string? Title { get; set; }
	    [JsonPropertyName("install_path")]
	    public string InstallPath { get; set; } = string.Empty;
	    [JsonPropertyName("executable")]
	    public string Executable { get; set; } = string.Empty;
	    [JsonPropertyName("platform")]
	    public string OsPlatform { get; set; } = string.Empty;
	    [JsonPropertyName("is_dlc")]
	    public bool IsDlc { get; set; }
		public GamePlatform? GamePlatform { get; set; }
	}

	public List<Game> Scan()
	{
		var games = new List<Game>();

		try
		{
			var heroicGames = new List<HeroicGame>();

			var heroicDataPaths = GetHeroicDataPaths();
			foreach (var dataPath in heroicDataPaths)
			{
				if (!Path.Exists(dataPath)) continue;

				var heroicGogDataPath = Path.Combine(dataPath, "gog_store", "installed.json");
				var heroicEpicDataPath = Path.Combine(dataPath, "legendaryConfig", "legendary", "installed.json");

				// GOG
				if (Path.Exists(heroicGogDataPath))
				{
					var gogJson = File.ReadAllText(heroicGogDataPath);
					var gogGamesDict = JsonSerializer.Deserialize<Dictionary<string, List<HeroicGame>>>(gogJson)!;
					var gogGames = gogGamesDict.First().Value;
					foreach (var game in gogGames)
						game.GamePlatform = GamePlatform.GOG;
					heroicGames.AddRange(gogGames);
				}

				// Epic
				if (Path.Exists(heroicEpicDataPath))
				{
					var epicJson = File.ReadAllText(heroicEpicDataPath);
					var epicGamesDict = JsonSerializer.Deserialize<Dictionary<string, HeroicGame>>(epicJson)!;
					var epicGames = new List<HeroicGame>();
					foreach (var game in epicGamesDict)
						epicGames.Add(game.Value);
					foreach (var game in epicGames)
						game.GamePlatform = GamePlatform.Epic;
					heroicGames.AddRange(epicGames);
				}
			}

			foreach (var game in heroicGames)
			{
				if (game.IsDlc || game.GamePlatform == null
					|| game.OsPlatform.ToLower() != "windows")
						continue;

				var gameName = string.Empty;
				if (game.GamePlatform == GamePlatform.Epic)
					gameName = game.Title;
				else
					gameName = game.InstallPath.Split(Path.DirectorySeparatorChar).Last();

				if (Path.Exists(game.InstallPath) && !string.IsNullOrEmpty(gameName))
				{
					games.Add(new Game
					{
						AppId = game.AppId,
						Name = gameName,
						InstallPath = game.InstallPath,
						Platform = game.GamePlatform.Value
					});
				}
			}
		}
		catch (Exception ex)
		{
            System.Diagnostics.Debug.WriteLine($"[Heroic] Error scanning: {ex.Message}");
		}

        return games;
	}

	private string[] GetHeroicDataPaths()
	{
		if (OperatingSystem.IsWindows())
			return GetHeroicDataPathsWindows();
		return GetHeroicDataPathsLinux();
	}

	[SupportedOSPlatform("windows")]
	private string[] GetHeroicDataPathsWindows()
	{
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		var heroicDataPath = Path.Combine(appData, "heroic");
		return [heroicDataPath];
	}

	private string[] GetHeroicDataPathsLinux()
	{
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var dataPaths = new[]
		{
			Path.Combine(home, ".config", "heroic"),
		    Path.Combine(home, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic"),
		};
		return dataPaths;
	}
}
