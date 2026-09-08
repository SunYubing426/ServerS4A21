using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Events
{
    internal sealed class EventDefaultsApplier
    {
        private const string DefaultsFileName = "event_defaults.json";
        private const string SkipEnvironmentVariable = "DFO_EVENT_DEFAULTS_SKIP";

        private readonly IGameDatabase _database;

        internal EventDefaultsApplier(IGameDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        internal void Apply()
        {
            if (IsSkipRequested())
                return;

            var path = Path.Combine(
                AppContext.BaseDirectory,
                "Data",
                DefaultsFileName);
            if (!File.Exists(path))
            {
                Console.WriteLine(
                    $"[EventDefaults] Skip: defaults file not found: {path}");
                return;
            }

            EventDefaultsConfig config;
            try
            {
                var json = File.ReadAllText(path);
                config = JsonSerializer.Deserialize<EventDefaultsConfig>(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[EventDefaults] Failed to read defaults: {ex.Message}");
                return;
            }

            if (config?.Events == null || config.Events.Count == 0)
                return;

            using (var connection = _database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var entry in config.Events)
                {
                    ApplyEvent(connection, transaction, entry);
                }

                transaction.Commit();
            }

            Console.WriteLine(
                $"[EventDefaults] Applied {config.Events.Count} event default(s).");
        }

        private static bool IsSkipRequested()
        {
            var value = Environment.GetEnvironmentVariable(SkipEnvironmentVariable);
            return !string.IsNullOrWhiteSpace(value)
                && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
        }

        private static void ApplyEvent(
            SqliteConnection connection,
            SqliteTransaction transaction,
            EventDefaultEntry entry)
        {
            var eventId = entry.EventId;
            var state = ResolveState(eventId, entry.State);

            UpsertState(connection, transaction, eventId, state);

            if (entry.Details != null)
            {
                var details = entry.Details.Clone();
                details.StartUnixTime = ResolveUnixTime(
                    eventId,
                    "START",
                    details.StartUnixTime);
                details.EndUnixTime = ResolveUnixTime(
                    eventId,
                    "END",
                    details.EndUnixTime);
                UpsertDetails(connection, transaction, eventId, details);
            }

            if (entry.Extra != null)
            {
                UpsertExtra(connection, transaction, eventId, entry.Extra);
            }
        }

        private static int ResolveState(int eventId, int defaultState)
        {
            var env = Environment.GetEnvironmentVariable(
                $"DFO_EVENT_{eventId}_ENABLED");
            if (string.IsNullOrWhiteSpace(env))
                return defaultState;

            if (env == "1" || env.Equals("true", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (env == "0" || env.Equals("false", StringComparison.OrdinalIgnoreCase))
                return 0;

            Console.WriteLine(
                $"[EventDefaults] Ignored invalid DFO_EVENT_{eventId}_ENABLED={env}");
            return defaultState;
        }

        private static long ResolveUnixTime(
            int eventId,
            string suffix,
            long defaultValue)
        {
            var env = Environment.GetEnvironmentVariable(
                $"DFO_EVENT_{eventId}_{suffix}");
            if (string.IsNullOrWhiteSpace(env))
                return defaultValue;

            if (long.TryParse(env.Trim(), out var raw))
                return raw;

            if (DateTimeOffset.TryParse(env.Trim(), out var parsed))
                return parsed.ToUnixTimeSeconds();

            Console.WriteLine(
                $"[EventDefaults] Ignored invalid DFO_EVENT_{eventId}_{suffix}={env}");
            return defaultValue;
        }

        private static void UpsertState(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int eventId,
            int state)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO game_event_state (event_id, state)
VALUES (@eventId, @state)
ON CONFLICT(event_id) DO UPDATE SET
    state = excluded.state;";
                command.Parameters.AddWithValue("@eventId", eventId);
                command.Parameters.AddWithValue("@state", state);
                command.ExecuteNonQuery();
            }
        }

        private static void UpsertDetails(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int eventId,
            EventDetails details)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR REPLACE INTO game_event_info_details (
    event_id, unknown0, start_notice, end_notice, detail_flag,
    flag_a, flag_b, title, short_name, reserved_or_icon,
    start_unix_time, end_unix_time, link_key, description,
    detail_enabled, sort_order, updated_at
) VALUES (
    @eventId, @unknown0, @startNotice, @endNotice, @detailFlag,
    @flagA, @flagB, @title, @shortName, @reservedOrIcon,
    @startUnixTime, @endUnixTime, @linkKey, @description,
    @detailEnabled, @sortOrder, CURRENT_TIMESTAMP
);";
                AddParameter(command, "@eventId", eventId);
                AddParameter(command, "@unknown0", details.Unknown0);
                AddParameter(command, "@startNotice", details.StartNotice);
                AddParameter(command, "@endNotice", details.EndNotice);
                AddParameter(command, "@detailFlag", details.DetailFlag);
                AddParameter(command, "@flagA", details.FlagA);
                AddParameter(command, "@flagB", details.FlagB);
                AddParameter(command, "@title", details.Title);
                AddParameter(command, "@shortName", details.ShortName);
                AddParameter(command, "@reservedOrIcon", details.ReservedOrIcon);
                AddParameter(command, "@startUnixTime", details.StartUnixTime);
                AddParameter(command, "@endUnixTime", details.EndUnixTime);
                AddParameter(command, "@linkKey", details.LinkKey);
                AddParameter(command, "@description", details.Description);
                AddParameter(command, "@detailEnabled", details.DetailEnabled ? 1 : 0);
                AddParameter(command, "@sortOrder", details.SortOrder);
                command.ExecuteNonQuery();
            }
        }

        private static void UpsertExtra(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int eventId,
            EventExtra extra)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR REPLACE INTO game_event_info_extra (
    event_id, param0, param1, param2, param3, param4,
    param5, param6, param7, param8, param9, param10,
    param11, sort_order, updated_at
) VALUES (
    @eventId, @p0, @p1, @p2, @p3, @p4,
    @p5, @p6, @p7, @p8, @p9, @p10,
    @p11, @sortOrder, CURRENT_TIMESTAMP
);";
                AddParameter(command, "@eventId", eventId);
                AddParameter(command, "@p0", extra.Param0);
                AddParameter(command, "@p1", extra.Param1);
                AddParameter(command, "@p2", extra.Param2);
                AddParameter(command, "@p3", extra.Param3);
                AddParameter(command, "@p4", extra.Param4);
                AddParameter(command, "@p5", extra.Param5);
                AddParameter(command, "@p6", extra.Param6);
                AddParameter(command, "@p7", extra.Param7);
                AddParameter(command, "@p8", extra.Param8);
                AddParameter(command, "@p9", extra.Param9);
                AddParameter(command, "@p10", extra.Param10);
                AddParameter(command, "@p11", extra.Param11);
                AddParameter(command, "@sortOrder", extra.SortOrder);
                command.ExecuteNonQuery();
            }
        }

        private static void AddParameter(
            SqliteCommand command,
            string name,
            object value)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        private sealed class EventDefaultsConfig
        {
            [JsonPropertyName("events")]
            public List<EventDefaultEntry> Events { get; set; }
        }

        private sealed class EventDefaultEntry
        {
            [JsonPropertyName("eventId")]
            public int EventId { get; set; }

            [JsonPropertyName("state")]
            public int State { get; set; }

            [JsonPropertyName("details")]
            public EventDetails Details { get; set; }

            [JsonPropertyName("extra")]
            public EventExtra Extra { get; set; }
        }

        private sealed class EventDetails
        {
            [JsonPropertyName("unknown0")]
            public long Unknown0 { get; set; }

            [JsonPropertyName("startNotice")]
            public string StartNotice { get; set; }

            [JsonPropertyName("endNotice")]
            public string EndNotice { get; set; }

            [JsonPropertyName("detailFlag")]
            public int DetailFlag { get; set; }

            [JsonPropertyName("flagA")]
            public int FlagA { get; set; }

            [JsonPropertyName("flagB")]
            public int FlagB { get; set; }

            [JsonPropertyName("title")]
            public string Title { get; set; }

            [JsonPropertyName("shortName")]
            public string ShortName { get; set; }

            [JsonPropertyName("reservedOrIcon")]
            public string ReservedOrIcon { get; set; }

            [JsonPropertyName("startUnixTime")]
            public long StartUnixTime { get; set; }

            [JsonPropertyName("endUnixTime")]
            public long EndUnixTime { get; set; }

            [JsonPropertyName("linkKey")]
            public string LinkKey { get; set; }

            [JsonPropertyName("description")]
            public string Description { get; set; }

            [JsonPropertyName("detailEnabled")]
            public bool DetailEnabled { get; set; }

            [JsonPropertyName("sortOrder")]
            public int SortOrder { get; set; }

            public EventDetails Clone()
            {
                return (EventDetails)MemberwiseClone();
            }
        }

        private sealed class EventExtra
        {
            [JsonPropertyName("param0")]
            public long Param0 { get; set; }

            [JsonPropertyName("param1")]
            public long Param1 { get; set; }

            [JsonPropertyName("param2")]
            public long Param2 { get; set; }

            [JsonPropertyName("param3")]
            public long Param3 { get; set; }

            [JsonPropertyName("param4")]
            public long Param4 { get; set; }

            [JsonPropertyName("param5")]
            public long Param5 { get; set; }

            [JsonPropertyName("param6")]
            public long Param6 { get; set; }

            [JsonPropertyName("param7")]
            public long Param7 { get; set; }

            [JsonPropertyName("param8")]
            public long Param8 { get; set; }

            [JsonPropertyName("param9")]
            public long Param9 { get; set; }

            [JsonPropertyName("param10")]
            public long Param10 { get; set; }

            [JsonPropertyName("param11")]
            public long Param11 { get; set; }

            [JsonPropertyName("sortOrder")]
            public int SortOrder { get; set; }
        }
    }
}
