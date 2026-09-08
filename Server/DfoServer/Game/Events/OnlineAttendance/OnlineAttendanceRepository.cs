using System;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Events.OnlineAttendance
{
    internal sealed class OnlineAttendanceRepository
    {
        private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

        private readonly IGameDatabase _database;

        internal OnlineAttendanceRepository(IGameDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        internal void EnsureStaticConfigRows(OnlineAttendanceConfig config)
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
            OnlineAttendanceConfig config)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT OR IGNORE INTO game_event_state(event_id, state)
VALUES(@eventId, 0);",
                ("@eventId", OnlineAttendanceConfig.EventId));

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
    1, 23
)
ON CONFLICT(event_id) DO NOTHING;",
                ("@eventId", OnlineAttendanceConfig.EventId),
                ("@startNotice", "Online attendance event started."),
                ("@endNotice", "Online attendance event ended."),
                ("@title", "onlineattendanceevent"),
                ("@shortName", "onlineattendanceevent"),
                ("@startUnixTime", window.StartUnixTime),
                ("@endUnixTime", window.EndUnixTime),
                ("@description", "Online attendance rewards."));
        }

        internal bool IsEnabled(
            SqliteConnection connection,
            SqliteTransaction transaction)
            => GameEventRepository.IsEnabled(
                connection,
                transaction,
                OnlineAttendanceConfig.EventId);

        internal void EnsureStateRows(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            OnlineAttendanceConfig config,
            int dayId,
            long nowUnix)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT OR IGNORE INTO event_online_attendance_account (
    account_id, event_id, season_id,
    sum_completed_count, sum_claim_mask, updated_at_unix
) VALUES (
    @accountId, @eventId, @seasonId,
    0, 0, @nowUnix
);",
                ("@accountId", accountId),
                ("@eventId", OnlineAttendanceConfig.EventId),
                ("@seasonId", config.SeasonId),
                ("@nowUnix", nowUnix));

            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT OR IGNORE INTO event_online_attendance_daily (
    account_id, event_id, season_id, day_id,
    online_seconds, time_claim_mask, updated_at_unix
) VALUES (
    @accountId, @eventId, @seasonId, @dayId,
    0, 0, @nowUnix
);",
                ("@accountId", accountId),
                ("@eventId", OnlineAttendanceConfig.EventId),
                ("@seasonId", config.SeasonId),
                ("@dayId", dayId),
                ("@nowUnix", nowUnix));
        }

        internal void AddOnlineSeconds(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            OnlineAttendanceConfig config,
            int dayId,
            long seconds,
            long nowUnix)
        {
            if (seconds <= 0)
                return;

            ExecuteNonQuery(
                connection,
                transaction,
                @"
UPDATE event_online_attendance_daily
SET online_seconds = MIN(@cap, online_seconds + @seconds),
    updated_at_unix = @nowUnix
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId
  AND day_id=@dayId;",
                ("@cap", config.TotalRequiredSeconds),
                ("@seconds", seconds),
                ("@nowUnix", nowUnix),
                ("@accountId", accountId),
                ("@eventId", OnlineAttendanceConfig.EventId),
                ("@seasonId", config.SeasonId),
                ("@dayId", dayId));
        }

        internal OnlineAttendanceDailyProgress LoadDailyProgress(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            OnlineAttendanceConfig config,
            int dayId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT online_seconds, time_claim_mask
FROM event_online_attendance_daily
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId
  AND day_id=@dayId;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue(
                    "@eventId",
                    OnlineAttendanceConfig.EventId);
                command.Parameters.AddWithValue("@seasonId", config.SeasonId);
                command.Parameters.AddWithValue("@dayId", dayId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return new OnlineAttendanceDailyProgress();

                    return new OnlineAttendanceDailyProgress
                    {
                        OnlineSeconds = Math.Max(0, reader.GetInt64(0)),
                        TimeClaimMask = reader.GetInt32(1),
                    };
                }
            }
        }

        internal OnlineAttendanceAccountProgress LoadAccountProgress(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            OnlineAttendanceConfig config)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT sum_completed_count, sum_claim_mask
FROM event_online_attendance_account
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue(
                    "@eventId",
                    OnlineAttendanceConfig.EventId);
                command.Parameters.AddWithValue("@seasonId", config.SeasonId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return new OnlineAttendanceAccountProgress();

                    return new OnlineAttendanceAccountProgress
                    {
                        SumCompletedCount = Math.Max(0, reader.GetInt32(0)),
                        SumClaimMask = reader.GetInt32(1),
                    };
                }
            }
        }

        internal bool TryClaimTimeReward(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            OnlineAttendanceConfig config,
            int dayId,
            int stageIndex,
            long requiredSeconds,
            long nowUnix)
        {
            var bit = 1 << (stageIndex - 1);
            return ExecuteNonQuery(
                connection,
                transaction,
                @"
UPDATE event_online_attendance_daily
SET time_claim_mask = time_claim_mask | @bit,
    updated_at_unix = @nowUnix
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId
  AND day_id=@dayId
  AND online_seconds >= @requiredSeconds
  AND (time_claim_mask & @bit) = 0;",
                ("@bit", bit),
                ("@requiredSeconds", requiredSeconds),
                ("@nowUnix", nowUnix),
                ("@accountId", accountId),
                ("@eventId", OnlineAttendanceConfig.EventId),
                ("@seasonId", config.SeasonId),
                ("@dayId", dayId)) == 1;
        }

        internal bool TryClaimSumReward(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            OnlineAttendanceConfig config,
            int stageIndex,
            int requiredCount,
            long nowUnix)
        {
            var bit = 1 << (stageIndex - 1);
            return ExecuteNonQuery(
                connection,
                transaction,
                @"
UPDATE event_online_attendance_account
SET sum_claim_mask = sum_claim_mask | @bit,
    updated_at_unix = @nowUnix
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId
  AND sum_completed_count >= @requiredCount
  AND (sum_claim_mask & @bit) = 0;",
                ("@bit", bit),
                ("@requiredCount", requiredCount),
                ("@nowUnix", nowUnix),
                ("@accountId", accountId),
                ("@eventId", OnlineAttendanceConfig.EventId),
                ("@seasonId", config.SeasonId)) == 1;
        }

        internal void IncrementSumCompletedCount(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            OnlineAttendanceConfig config,
            long nowUnix)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                @"
UPDATE event_online_attendance_account
SET sum_completed_count = sum_completed_count + 1,
    updated_at_unix = @nowUnix
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId;",
                ("@nowUnix", nowUnix),
                ("@accountId", accountId),
                ("@eventId", OnlineAttendanceConfig.EventId),
                ("@seasonId", config.SeasonId));
        }

        internal OnlineAttendanceSnapshot LoadSnapshot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int characterId,
            OnlineAttendanceConfig config,
            int dayId,
            bool eventEnabled)
        {
            EnsureStateRows(
                connection,
                transaction,
                accountId,
                config,
                dayId,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var daily = LoadDailyProgress(connection, transaction, accountId, config, dayId);
            var account = LoadAccountProgress(connection, transaction, accountId, config);
            return new OnlineAttendanceSnapshot
            {
                AccountId = accountId,
                CharacterId = characterId,
                EventId = OnlineAttendanceConfig.EventId,
                SeasonId = config.SeasonId,
                DayId = dayId,
                DailyOnlineSeconds = Math.Min(
                    daily.OnlineSeconds,
                    config.TotalRequiredSeconds),
                DailyClaimMask = daily.TimeClaimMask,
                SumClaimMask = account.SumClaimMask,
                SumCompletedCount = account.SumCompletedCount,
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

    internal sealed class OnlineAttendanceDailyProgress
    {
        public long OnlineSeconds { get; set; }

        public int TimeClaimMask { get; set; }
    }

    internal sealed class OnlineAttendanceAccountProgress
    {
        public int SumCompletedCount { get; set; }

        public int SumClaimMask { get; set; }
    }
}
