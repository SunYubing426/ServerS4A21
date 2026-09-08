using System;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Events.BurningTime
{
    internal sealed class BurningTimeRepository
    {
        private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

        private readonly IGameDatabase _database;

        internal BurningTimeRepository(IGameDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        internal void EnsureStaticConfigRows(BurningTimeConfig config)
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
            BurningTimeConfig config)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT OR IGNORE INTO game_event_state(event_id, state)
VALUES(@eventId, 0);",
                ("@eventId", BurningTimeConfig.EventId));

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
    1, 26
)
ON CONFLICT(event_id) DO NOTHING;",
                ("@eventId", BurningTimeConfig.EventId),
                ("@startNotice", "Burning time event started."),
                ("@endNotice", "Burning time event ended."),
                ("@title", "burningtimeevent"),
                ("@shortName", "burningtimeevent"),
                ("@startUnixTime", window.StartUnixTime),
                ("@endUnixTime", window.EndUnixTime),
                ("@description", "Burning time buff event."));
        }

        internal bool IsEnabled(
            SqliteConnection connection,
            SqliteTransaction transaction)
            => GameEventRepository.IsEnabled(
                connection,
                transaction,
                BurningTimeConfig.EventId);

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
}
