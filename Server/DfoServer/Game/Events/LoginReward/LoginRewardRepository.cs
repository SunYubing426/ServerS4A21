using System;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Events.LoginReward
{
    internal sealed class LoginRewardRepository
    {
        private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

        private readonly IGameDatabase _database;

        internal LoginRewardRepository(IGameDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        internal void EnsureStaticConfigRows(LoginRewardConfig config)
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
            LoginRewardConfig config)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT OR IGNORE INTO game_event_state(event_id, state)
VALUES(@eventId, 0);",
                ("@eventId", LoginRewardConfig.EventId));

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
    1, 24
)
ON CONFLICT(event_id) DO NOTHING;",
                ("@eventId", LoginRewardConfig.EventId),
                ("@startNotice", "Login reward event started."),
                ("@endNotice", "Login reward event ended."),
                ("@title", "loginrewardevent"),
                ("@shortName", "loginrewardevent"),
                ("@startUnixTime", window.StartUnixTime),
                ("@endUnixTime", window.EndUnixTime),
                ("@description", "Login rewards."));
        }

        internal bool IsEnabled(
            SqliteConnection connection,
            SqliteTransaction transaction)
            => GameEventRepository.IsEnabled(
                connection,
                transaction,
                LoginRewardConfig.EventId);

        internal void EnsureStateRows(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            LoginRewardConfig config,
            int dayId,
            long nowUnix)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT OR IGNORE INTO event_login_reward_account (
    account_id, event_id, season_id,
    last_claim_day_id, claimed_mask, updated_at_unix
) VALUES (
    @accountId, @eventId, @seasonId,
    0, 0, @nowUnix
);",
                ("@accountId", accountId),
                ("@eventId", LoginRewardConfig.EventId),
                ("@seasonId", config.SeasonId),
                ("@nowUnix", nowUnix));
        }

        internal LoginRewardAccountProgress LoadAccountProgress(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            LoginRewardConfig config)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT last_claim_day_id, claimed_mask
FROM event_login_reward_account
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue(
                    "@eventId",
                    LoginRewardConfig.EventId);
                command.Parameters.AddWithValue("@seasonId", config.SeasonId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return new LoginRewardAccountProgress();

                    return new LoginRewardAccountProgress
                    {
                        LastClaimDayId = reader.GetInt32(0),
                        ClaimedMask = reader.GetInt32(1),
                    };
                }
            }
        }

        internal bool TryClaimToday(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            LoginRewardConfig config,
            int dayId,
            int dayIndex,
            long nowUnix)
        {
            var bit = 1 << dayIndex;
            return ExecuteNonQuery(
                connection,
                transaction,
                @"
UPDATE event_login_reward_account
SET last_claim_day_id = @dayId,
    claimed_mask = claimed_mask | @bit,
    updated_at_unix = @nowUnix
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId
  AND last_claim_day_id < @dayId;",
                ("@dayId", dayId),
                ("@bit", bit),
                ("@nowUnix", nowUnix),
                ("@accountId", accountId),
                ("@eventId", LoginRewardConfig.EventId),
                ("@seasonId", config.SeasonId)) == 1;
        }

        internal LoginRewardSnapshot LoadSnapshot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int characterId,
            LoginRewardConfig config,
            int dayId,
            int todayDayIndex,
            bool alreadyClaimed,
            bool eventEnabled)
        {
            var progress = LoadAccountProgress(connection, transaction, accountId, config);
            return new LoginRewardSnapshot
            {
                AccountId = accountId,
                CharacterId = characterId,
                EventId = LoginRewardConfig.EventId,
                SeasonId = config.SeasonId,
                DayId = dayId,
                TodayDayIndex = todayDayIndex,
                ClaimedMask = progress.ClaimedMask,
                AlreadyClaimedToday = alreadyClaimed,
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

    internal sealed class LoginRewardAccountProgress
    {
        public int LastClaimDayId { get; set; }

        public int ClaimedMask { get; set; }
    }
}
