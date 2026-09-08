using System;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Events.GrowSupport
{
    internal sealed class GrowSupportRepository
    {
        private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

        private readonly IGameDatabase _database;

        internal GrowSupportRepository(IGameDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        internal void EnsureStaticConfigRows(GrowSupportConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            _database.Write((connection, transaction) =>
            {
                EnsureStaticConfigRows(connection, transaction, config);
            });
        }

        internal void EnsureStaticConfigRows(
            SqliteConnection connection,
            SqliteTransaction transaction,
            GrowSupportConfig config)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT OR IGNORE INTO game_event_state(event_id, state)
VALUES(@eventId, 0);",
                ("@eventId", GrowSupportConfig.EventId));

            var window = GetCalendarWindowUnix();
            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT INTO game_event_info_details (
    event_id, unknown0, start_notice, end_notice, detail_flag,
    flag_a, flag_b, title, short_name, reserved_or_icon,
    start_unix_time, end_unix_time, link_key, description,
    detail_enabled, sort_order
) VALUES (
    @eventId, 0, @startNotice, @endNotice, 1,
    0, 5, @title, @shortName, '',
    @startUnixTime, @endUnixTime, '', @description,
    1, 25
)
ON CONFLICT(event_id) DO NOTHING;",
                ("@eventId", GrowSupportConfig.EventId),
                ("@startNotice", "Grow support event started."),
                ("@endNotice", "Grow support event ended."),
                ("@title", "growsupportevent"),
                ("@shortName", "growsupportevent"),
                ("@startUnixTime", window.StartUnixTime),
                ("@endUnixTime", window.EndUnixTime),
                ("@description", "Grow support rewards."));
        }

        internal bool IsEnabled(
            SqliteConnection connection,
            SqliteTransaction transaction)
            => GameEventRepository.IsEnabled(
                connection,
                transaction,
                GrowSupportConfig.EventId);

        internal void EnsureStateRows(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int characterId,
            GrowSupportConfig config,
            long nowUnix)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT OR IGNORE INTO event_grow_support_character (
    account_id, character_id, event_id, season_id,
    level_reward_claim_mask, dungeon_clear_count,
    dungeon_reward_claim_mask, updated_at_unix
) VALUES (
    @accountId, @characterId, @eventId, @seasonId,
    0, 0, 0, @nowUnix
);",
                ("@accountId", accountId),
                ("@characterId", characterId),
                ("@eventId", GrowSupportConfig.EventId),
                ("@seasonId", config.SeasonId),
                ("@nowUnix", nowUnix));
        }

        internal GrowSupportCharacterProgress LoadCharacterProgress(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int characterId,
            GrowSupportConfig config)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT level_reward_claim_mask, dungeon_clear_count, dungeon_reward_claim_mask
FROM event_grow_support_character
WHERE account_id=@accountId
  AND character_id=@characterId
  AND event_id=@eventId
  AND season_id=@seasonId;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue(
                    "@eventId",
                    GrowSupportConfig.EventId);
                command.Parameters.AddWithValue("@seasonId", config.SeasonId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return new GrowSupportCharacterProgress();

                    return new GrowSupportCharacterProgress
                    {
                        LevelRewardClaimMask = reader.GetInt32(0),
                        DungeonClearCount = Math.Max(0, reader.GetInt32(1)),
                        DungeonRewardClaimMask = reader.GetInt32(2),
                    };
                }
            }
        }

        internal bool TryClaimLevelReward(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int characterId,
            GrowSupportConfig config,
            int level,
            long nowUnix)
        {
            var bit = 1 << (level % 32);
            return ExecuteNonQuery(
                connection,
                transaction,
                @"
UPDATE event_grow_support_character
SET level_reward_claim_mask = level_reward_claim_mask | @bit,
    updated_at_unix = @nowUnix
WHERE account_id=@accountId
  AND character_id=@characterId
  AND event_id=@eventId
  AND season_id=@seasonId
  AND (level_reward_claim_mask & @bit) = 0;",
                ("@bit", bit),
                ("@nowUnix", nowUnix),
                ("@accountId", accountId),
                ("@characterId", characterId),
                ("@eventId", GrowSupportConfig.EventId),
                ("@seasonId", config.SeasonId)) == 1;
        }

        internal bool TryClaimDungeonReward(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int characterId,
            GrowSupportConfig config,
            int requiredClears,
            long nowUnix)
        {
            var bit = 1 << (requiredClears % 32);
            return ExecuteNonQuery(
                connection,
                transaction,
                @"
UPDATE event_grow_support_character
SET dungeon_reward_claim_mask = dungeon_reward_claim_mask | @bit,
    updated_at_unix = @nowUnix
WHERE account_id=@accountId
  AND character_id=@characterId
  AND event_id=@eventId
  AND season_id=@seasonId
  AND dungeon_clear_count >= @requiredClears
  AND (dungeon_reward_claim_mask & @bit) = 0;",
                ("@bit", bit),
                ("@requiredClears", requiredClears),
                ("@nowUnix", nowUnix),
                ("@accountId", accountId),
                ("@characterId", characterId),
                ("@eventId", GrowSupportConfig.EventId),
                ("@seasonId", config.SeasonId)) == 1;
        }

        internal void IncrementDungeonClearCount(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int characterId,
            GrowSupportConfig config,
            long nowUnix)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                @"
UPDATE event_grow_support_character
SET dungeon_clear_count = dungeon_clear_count + 1,
    updated_at_unix = @nowUnix
WHERE account_id=@accountId
  AND character_id=@characterId
  AND event_id=@eventId
  AND season_id=@seasonId;",
                ("@nowUnix", nowUnix),
                ("@accountId", accountId),
                ("@characterId", characterId),
                ("@eventId", GrowSupportConfig.EventId),
                ("@seasonId", config.SeasonId));
        }

        internal GrowSupportSnapshot LoadSnapshot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int characterId,
            GrowSupportConfig config,
            int characterLevel,
            bool eventEnabled)
        {
            EnsureStateRows(
                connection,
                transaction,
                accountId,
                characterId,
                config,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var progress = LoadCharacterProgress(
                connection,
                transaction,
                accountId,
                characterId,
                config);
            return new GrowSupportSnapshot
            {
                AccountId = accountId,
                CharacterId = characterId,
                EventId = GrowSupportConfig.EventId,
                SeasonId = config.SeasonId,
                CharacterLevel = characterLevel,
                LevelRewardClaimMask = progress.LevelRewardClaimMask,
                DungeonClearCount = progress.DungeonClearCount,
                DungeonRewardClaimMask = progress.DungeonRewardClaimMask,
                EventEnabled = eventEnabled,
            };
        }

        private static (uint StartUnixTime, uint EndUnixTime)
            GetCalendarWindowUnix()
        {
            var now = DateTimeOffset.UtcNow.ToOffset(BeijingOffset);
            var start = new DateTimeOffset(
                now.Year,
                1,
                1,
                0,
                0,
                0,
                BeijingOffset);
            var end = new DateTimeOffset(
                now.Year,
                12,
                31,
                23,
                59,
                59,
                BeijingOffset);
            return (
                (uint)start.ToUnixTimeSeconds(),
                (uint)end.ToUnixTimeSeconds());
        }

        private static int ExecuteNonQuery(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                foreach (var parameter in parameters)
                    command.Parameters.AddWithValue(parameter.Name, parameter.Value);
                return command.ExecuteNonQuery();
            }
        }
    }

    internal sealed class GrowSupportCharacterProgress
    {
        public int LevelRewardClaimMask { get; set; }

        public int DungeonClearCount { get; set; }

        public int DungeonRewardClaimMask { get; set; }
    }
}
