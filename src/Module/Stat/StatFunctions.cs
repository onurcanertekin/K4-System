namespace K4System
{
	using CounterStrikeSharp.API;
	using CounterStrikeSharp.API.Core;

	using MySqlConnector;
	using Nexd.MySQL;
	using Microsoft.Extensions.Logging;
	using System.Text;

	public partial class ModuleStat : IModuleStat
	{
		public CCSGameRules GameRules()
		{
			if (globalGameRules is null)
				globalGameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First().GameRules!;

			return globalGameRules;
		}

		public bool IsStatsAllowed()
		{
			List<CCSPlayerController> players = Utilities.GetPlayers();
			int notBots = players.Count(player => !player.IsBot);

			return (!GameRules().WarmupPeriod || Config.StatisticSettings.WarmupStats) && (Config.StatisticSettings.MinPlayers <= notBots);
		}

		public async Task LoadStatData(int slot, string name, string steamid)
		{
			string escapedName = MySqlHelper.EscapeString(name);

			await Database.ExecuteNonQueryAsync($@"
				INSERT INTO `{Config.DatabaseSettings.TablePrefix}k4stats` (`name`, `steam_id`, `lastseen`)
				VALUES (
					'{escapedName}',
					'{steamid}',
					CURRENT_TIMESTAMP
				)
				ON DUPLICATE KEY UPDATE
					`name` = '{escapedName}',
					`lastseen` = CURRENT_TIMESTAMP;
			");

			MySqlQueryResult result = await Database.ExecuteQueryAsync($@"
				SELECT *
				FROM `{Config.DatabaseSettings.TablePrefix}k4stats`
				WHERE `steam_id` = '{steamid}';
			");

			Dictionary<string, int> NewStatFields = new Dictionary<string, int>();

			string[] statFieldNames = { "kills", "shoots", "firstblood", "deaths", "hits_given", "hits_taken", "headshots", "grenades", "mvp", "round_win", "round_lose", "game_win", "game_lose", "assists" };

			foreach (string statField in statFieldNames)
			{
				NewStatFields[statField] = result.Rows > 0 ? result.Get<int>(0, statField) : 0;
			}

			StatData playerData = new StatData
			{
				StatFields = NewStatFields,
			};

			statCache[slot] = playerData;
		}

		public void SavePlayerStatCache(CCSPlayerController player, bool remove)
		{
			var savedSlot = player.Slot;
			var savedStat = statCache[player];
			var savedName = player.PlayerName;
			var savedSteam = player.SteamID.ToString();

			Task.Run(async () =>
			{
				await SavePlayerStatCacheAsync(savedSlot, savedStat, savedName, savedSteam, remove);
			});
		}

		public async Task SavePlayerStatCacheAsync(int slot, StatData playerData, string name, string steamid, bool remove)
		{
			if (!statCache.ContainsKey(slot))
			{
				Logger.LogWarning($"SavePlayerStatCache > Player is not loaded to the cache ({name})");
				return;
			}

			string escapedName = MySqlHelper.EscapeString(name);

			StringBuilder queryBuilder = new StringBuilder();
			queryBuilder.Append($@"
				INSERT INTO `{Config.DatabaseSettings.TablePrefix}k4stats` (`steam_id`, `name`, `lastseen`");

			foreach (var field in playerData.StatFields)
			{
				queryBuilder.Append($", `{field.Key}`");
			}

			queryBuilder.Append($@")
				VALUES ('{steamid}', '{escapedName}', CURRENT_TIMESTAMP");

			foreach (var field in playerData.StatFields)
			{
				queryBuilder.Append($", {field.Value}");
			}

			queryBuilder.Append($@")
				ON DUPLICATE KEY UPDATE");

			foreach (var field in playerData.StatFields)
			{
				queryBuilder.Append($"`{field.Key}` = VALUES(`{field.Key}`), ");
			}

			queryBuilder.Append($@"`lastseen` = CURRENT_TIMESTAMP;");

			if (!remove)
			{
				queryBuilder.Append($@"

				SELECT * FROM `{Config.DatabaseSettings.TablePrefix}k4stats`
				WHERE `steam_id` = '{steamid}';");
			}

			string insertOrUpdateQuery = queryBuilder.ToString();

			MySqlQueryResult result = await Database.ExecuteQueryAsync(insertOrUpdateQuery);

			if (Config.GeneralSettings.LevelRanksCompatibility)
			{
				await Database.ExecuteNonQueryAsync($@"
					INSERT INTO `lvl_base`
					(`steam`, `name`, `kills`, `deaths`, `shoots`, `hits`, `headshots`, `assists`, `round_win`, `round_lose`, `lastconnect`)
					VALUES
					('{steamid}', '{escapedName}', {playerData.StatFields["kills"]}, {playerData.StatFields["deaths"]}, {playerData.StatFields["shoots"]}, {playerData.StatFields["hits_given"]}, {playerData.StatFields["headshots"]}, {playerData.StatFields["assists"]}, {playerData.StatFields["round_win"]}, {playerData.StatFields["round_lose"]}, CURRENT_TIMESTAMP)
					ON DUPLICATE KEY UPDATE
					`name` = '{escapedName}',
					`kills` = {playerData.StatFields["kills"]},
					`deaths` = {playerData.StatFields["deaths"]},
					`shoots` = {playerData.StatFields["shoots"]},
					`hits` = {playerData.StatFields["hits_given"]},
					`headshots` = {playerData.StatFields["headshots"]},
					`assists` = {playerData.StatFields["assists"]},
					`round_win` = {playerData.StatFields["round_win"]},
					`round_lose` = {playerData.StatFields["round_lose"]},
					`lastconnect` = CURRENT_TIMESTAMP;
				");
			}

			if (!remove)
			{
				Dictionary<string, int> NewStatFields = new Dictionary<string, int>();

				var allKeys = playerData.StatFields.Keys.ToList();

				foreach (string statField in allKeys)
				{
					NewStatFields[statField] = result.Rows > 0 ? result.Get<int>(0, statField) : 0;
				}

				statCache[slot].StatFields = NewStatFields;
			}
			else
			{
				statCache.Remove(slot);
			}
		}

		public void SaveAllPlayerCache(bool clear)
		{
			List<CCSPlayerController> players = Utilities.GetPlayers();

			var saveTasks = players
				.Where(player => player != null && player.IsValid && player.PlayerPawn.IsValid && !player.IsBot && !player.IsHLTV && statCache.ContainsPlayer(player))
				.Select(player => SavePlayerStatCacheAsync(player.Slot, statCache[player], player.PlayerName, player.SteamID.ToString(), clear))
				.ToList();

			Task.Run(async () =>
			{
				await Task.WhenAll(saveTasks);
			});

			if (clear)
				statCache.Clear();
		}

		public void ModifyPlayerStats(CCSPlayerController player, string field, int amount)
		{
			if (player is null || !player.IsValid || !player.PlayerPawn.IsValid)
			{
				Logger.LogWarning("ModifyPlayerStats > Invalid player controller");
				return;
			}

			if (player.IsBot || player.IsHLTV)
			{
				Logger.LogWarning($"ModifyPlayerStats > Player controller is BOT or HLTV");
				return;
			}

			if (!statCache.ContainsPlayer(player))
			{
				Logger.LogWarning($"ModifyPlayerStats > Player is not loaded to the cache ({player.PlayerName})");
				return;
			}

			StatData playerData = statCache[player];
			playerData.StatFields[field] += amount;
		}
	}
}