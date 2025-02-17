namespace K4System
{
	using CounterStrikeSharp.API;
	using CounterStrikeSharp.API.Core;
	using CounterStrikeSharp.API.Modules.Utils;

	using System.Reflection;
	using MySqlConnector;
	using Nexd.MySQL;
	using Microsoft.Extensions.Logging;
	using CounterStrikeSharp.API.Modules.Admin;

	public partial class ModuleRank : IModuleRank
	{
		public CCSGameRules GameRules()
		{
			if (globalGameRules is null)
				globalGameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First().GameRules!;

			return globalGameRules;
		}

		public bool IsPointsAllowed()
		{
			List<CCSPlayerController> players = Utilities.GetPlayers();
			int notBots = players.Count(player => !player.IsBot);

			return (!GameRules().WarmupPeriod || Config.RankSettings.WarmupPoints) && (Config.RankSettings.MinPlayers <= notBots);
		}

		public async Task LoadRankData(int slot, string name, string steamid)
		{
			string escapedName = MySqlHelper.EscapeString(name);

			await Database.ExecuteNonQueryAsync($@"
				INSERT INTO `{Config.DatabaseSettings.TablePrefix}k4ranks` (`name`, `steam_id`, `rank`)
				VALUES (
					'{escapedName}',
					'{steamid}',
					'{noneRank.Name}'
				)
				ON DUPLICATE KEY UPDATE
					`name` = '{escapedName}';
			");

			MySqlQueryResult result = await Database.ExecuteQueryAsync($@"
				SELECT `points`
				FROM `{Config.DatabaseSettings.TablePrefix}k4ranks`
				WHERE `steam_id` = '{steamid}';
			");

			int points = result.Rows > 0 ? result.Get<int>(0, "points") : 0;

			RankData playerData = new RankData
			{
				Points = points,
				Rank = GetPlayerRank(points),
				PlayedRound = false,
				RoundPoints = 0
			};

			rankCache[slot] = playerData;
		}

		public Rank GetPlayerRank(int points)
		{
			return rankDictionary.LastOrDefault(kv => points >= kv.Value.Point).Value ?? noneRank;
		}

		public void ModifyPlayerPoints(CCSPlayerController player, int amount, string reason)
		{
			if (!IsPointsAllowed())
				return;

			if (player is null || !player.IsValid || !player.PlayerPawn.IsValid)
			{
				Logger.LogWarning("ModifyPlayerPoints > Invalid player controller");
				return;
			}

			if (player.IsBot || player.IsHLTV)
			{
				Logger.LogWarning($"ModifyPlayerPoints > Player controller is BOT or HLTV");
				return;
			}

			if (!rankCache.ContainsPlayer(player))
			{
				Logger.LogWarning($"ModifyPlayerPoints > Player is not loaded to the cache ({player.PlayerName})");
				return;
			}

			RankData playerData = rankCache[player];

			if (amount == 0)
				return;

			if (amount > 0 && AdminManager.PlayerHasPermissions(player, "@k4system/vip/points-multiplier"))
			{
				amount = (int)Math.Round(amount * Config.RankSettings.VipMultiplier);
			}

			Plugin plugin = (this.PluginContext.Plugin as Plugin)!;

			int oldPoints = playerData.Points;
			Server.NextFrame(() =>
			{
				if (!Config.RankSettings.RoundEndPoints)
				{
					if (amount > 0)
					{
						player.PrintToChat($" {plugin.Localizer["k4.general.prefix"]} {plugin.Localizer["k4.ranks.points.gain", oldPoints, amount, plugin.Localizer[reason]]}");
					}
					else if (amount < 0)
					{
						player.PrintToChat($" {plugin.Localizer["k4.general.prefix"]} {plugin.Localizer["k4.ranks.points.loss", oldPoints, Math.Abs(amount), plugin.Localizer[reason]]}");
					}
				}
			});

			playerData.Points += amount;

			if (playerData.Points < 0)
				playerData.Points = 0;

			if (Config.RankSettings.ScoreboardScoreSync)
				player.Score = playerData.Points;

			playerData.RoundPoints += amount;

			Rank newRank = GetPlayerRank(playerData.Points);

			if (playerData.Rank.Name != newRank.Name)
			{
				player.PrintToChat($" {plugin.Localizer["k4.general.prefix"]} {plugin.Localizer[playerData.Rank.Point > newRank.Point ? "k4.ranks.demote" : "k4.ranks.promote", newRank.Color, newRank.Name]}");

				if (playerData.Rank.Permissions != null && playerData.Rank.Permissions.Count > 0)
				{
					foreach (Permission permission in playerData.Rank.Permissions)
					{
						AdminManager.RemovePlayerPermissions(Utilities.GetPlayerFromSlot(player.Slot), permission.PermissionName);
					}
				}

				if (newRank.Permissions != null)
				{
					foreach (Permission permission in newRank.Permissions)
					{
						AdminManager.AddPlayerPermissions(Utilities.GetPlayerFromSlot(player.Slot), permission.PermissionName);
					}
				}

				playerData.Rank = newRank;
			}

			if (Config.RankSettings.ScoreboardRanks)
				player.Clan = $"{playerData.Rank.Tag ?? $"[{playerData.Rank.Name}]"}";
		}

		public void SavePlayerRankCache(CCSPlayerController player, bool remove)
		{
			var savedSlot = player.Slot;
			var savedRank = rankCache[player];
			var savedName = player.PlayerName;
			var savedSteam = player.SteamID.ToString();

			Task.Run(async () =>
			{
				await SavePlayerRankCacheAsync(savedSlot, savedRank, savedName, savedSteam, remove);
			});
		}

		public async Task SavePlayerRankCacheAsync(int slot, RankData playerData, string name, string steamid, bool remove)
		{
			if (!rankCache.ContainsKey(slot))
			{
				Logger.LogWarning($"SavePlayerRankCache > Player is not loaded to the cache ({name})");
				return;
			}

			string escapedName = MySqlHelper.EscapeString(name);

			int setPoints = playerData.RoundPoints;

			await Database.ExecuteNonQueryAsync($@"
				INSERT INTO `{Config.DatabaseSettings.TablePrefix}k4ranks`
				(`steam_id`, `name`, `rank`, `points`)
				VALUES
				('{steamid}', '{escapedName}', '{playerData.Rank.Name}',
				CASE
					WHEN (`points` + {setPoints}) < 0 THEN 0
					ELSE (`points` + {setPoints})
				END)
				ON DUPLICATE KEY UPDATE
				`name` = '{escapedName}',
				`rank` = '{playerData.Rank.Name}',
				`points` =
				CASE
					WHEN (`points` + {setPoints}) < 0 THEN 0
					ELSE (`points` + {setPoints})
				END;
			");

			if (Config.GeneralSettings.LevelRanksCompatibility)
			{
				await Database.ExecuteNonQueryAsync($@"
					INSERT INTO `lvl_base`
					(`steam`, `name`, `rank`, `lastconnect`, `value`)
					VALUES
					('{steamid}', '{escapedName}', '{playerData.Rank.Name}', CURRENT_TIMESTAMP,
					CASE
						WHEN (`value` + {setPoints}) < 0 THEN 0
						ELSE (`value` + {setPoints})
					END)
					ON DUPLICATE KEY UPDATE
					`name` = '{escapedName}',
					`rank` = '{playerData.Rank.Name}',
					`lastconnect` = CURRENT_TIMESTAMP,
					`value` =
					CASE
						WHEN (`value` + {setPoints}) < 0 THEN 0
						ELSE (`value` + {setPoints})
					END;
				");
			}

			if (!remove)
			{
				MySqlQueryResult result = await Database.ExecuteQueryAsync($@"
					SELECT `points`
					FROM `{Config.DatabaseSettings.TablePrefix}k4ranks`
					WHERE `steam_id` = '{steamid}';
				");

				playerData.Points = result.Rows > 0 ? result.Get<int>(0, "points") : 0;

				playerData.RoundPoints -= setPoints;
				playerData.Rank = GetPlayerRank(playerData.Points);
			}
			else
			{
				rankCache.Remove(slot);
			}
		}

		public void SaveAllPlayerCache(bool clear)
		{
			List<CCSPlayerController> players = Utilities.GetPlayers();

			var saveTasks = players
				.Where(player => player != null && player.IsValid && player.PlayerPawn.IsValid && !player.IsBot && !player.IsHLTV && rankCache.ContainsPlayer(player))
				.Select(player => SavePlayerRankCacheAsync(player.Slot, rankCache[player], player.PlayerName, player.SteamID.ToString(), clear))
				.ToList();

			Task.Run(async () =>
			{
				await Task.WhenAll(saveTasks);
			});

			if (clear)
				rankCache.Clear();
		}

		public async Task<(int playerPlace, int totalPlayers)> GetPlayerPlaceAndCount(string steamID)
		{
			MySqlQueryResult result = await Database.Table($"{Config.DatabaseSettings.TablePrefix}k4ranks")
				.ExecuteQueryAsync($"SELECT (SELECT COUNT(*) FROM `{Config.DatabaseSettings.TablePrefix}k4ranks` WHERE `points` > (SELECT `points` FROM `{Config.DatabaseSettings.TablePrefix}k4ranks` WHERE `steam_id` = '{steamID}')) AS playerCount, COUNT(*) AS totalPlayers FROM `{Config.DatabaseSettings.TablePrefix}k4ranks`;")!;

			if (result.Count > 0)
			{
				int playersWithMorePoints = result.Get<int>(0, "playerCount");
				int totalPlayers = result.Get<int>(0, "totalPlayers");

				return (playersWithMorePoints + 1, totalPlayers);
			}

			return (0, 0);
		}

		public int CalculateDynamicPoints(CCSPlayerController modifyFor, CCSPlayerController modifyFrom, int amount)
		{
			if (!Config.RankSettings.DynamicDeathPoints || modifyFor.IsBot || modifyFrom.IsBot || rankCache[modifyFor].Points <= 0 || rankCache[modifyFrom].Points <= 0)
			{
				return amount;
			}

			double pointsRatio = Math.Clamp(rankCache[modifyFor].Points / rankCache[modifyFrom].Points, Config.RankSettings.DynamicDeathPointsMinMultiplier, Config.RankSettings.DynamicDeathPointsMaxMultiplier);
			double result = pointsRatio * amount;
			return (int)Math.Round(result);
		}
	}
}