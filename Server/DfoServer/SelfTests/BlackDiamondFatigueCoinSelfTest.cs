using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Premium;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Dungeon;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    internal static class BlackDiamondFatigueCoinSelfTest
    {
        private const int OrdinaryAccount = 771001;
        private const int MemberAccount = 771002;
        private const int ExpiredAccount = 771003;
        private const int Type1Account = 771004;
        private const int Type17Account = 771005;
        private const int OrdinaryCharacter = 771101;
        private const int MemberCharacterA = 771102;
        private const int MemberCharacterB = 771103;
        private const int ExpiredCharacter = 771104;
        private const int Type1Character = 771105;
        private const int Type17Character = 771106;
        private const int FailCharacter = 771107;
        private const int ClockCharacter = 771108;
        private const int ReplayCharacter = 771109;
        private const int ThrowCharacter = 771110;

        public static int Run()
        {
            Console.WriteLine("=== BLACK_DIAMOND_FATIGUE_COIN selftest ===");
            var failures = 0;
            VerifyPvfMailKeys(ref failures);
            VerifyFatigueMailRules(ref failures);
            VerifyPremiumExpiryUsesTransactionClock(ref failures);
            VerifyDungeonMapHandler(ref failures);
            VerifyFailedDoesNotRereadSnapshot(ref failures);
            VerifyWorldDropAndOrdinaryPickup(ref failures);
            Console.WriteLine(
                failures == 0
                    ? "BLACK_DIAMOND_FATIGUE_COIN selftest PASS"
                    : "BLACK_DIAMOND_FATIGUE_COIN selftest FAIL: " + failures);
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyPvfMailKeys(ref int failures)
        {
            if (!RequirePvf(ref failures, "pvf mail keys"))
                return;

            var raw = ReadPvfMailPair();
            Check(
                "PVF contains chn_game_server_msg_23/24",
                !string.IsNullOrEmpty(raw.Title) && !string.IsNullOrEmpty(raw.Body),
                ref failures);
            Check(
                "catalog reads the same PVF keys",
                HardcodedTextTagCatalog.TryGet(
                    BlackDiamondFatigueCoinService.TitleKey,
                    out var title)
                && HardcodedTextTagCatalog.TryGet(
                    BlackDiamondFatigueCoinService.BodyKey,
                    out var body)
                && title == raw.Title
                && body == raw.Body,
                ref failures);
        }

        private static void VerifyFatigueMailRules(ref int failures)
        {
            if (!RequirePvf(ref failures, "fatigue mail"))
                return;

            var raw = ReadPvfMailPair();
            var tempDb = Path.Combine(
                Path.GetTempPath(),
                "dfo-bd-fatigue-coin-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    tempDb,
                    ServerPaths.SchemaFilePath);
                var database = new GameDatabase(tempDb, ServerPaths.SchemaFilePath);
                SeedAccounts(connectionString);
                var mailbox = new MailboxService(new MailboxRepository(database));
                var fatigue = new DungeonFatigueService(database);

                SeedUsed(connectionString, MemberCharacterA, 149);
                var first = fatigue.ConsumeRoom(MemberCharacterA, MemberAccount);
                Check(
                    "149->150 member consumes and grants one coin mail",
                    first.Consumed
                    && first.MailDelivered
                    && first.State.Used == 150
                    && first.State.Limit == 188
                    && CountMails(connectionString, MemberCharacterA) == 1
                    && CountCoinAttachments(connectionString, MemberCharacterA) == 1
                    && MailMatchesResource(connectionString, MemberCharacterA, raw)
                    && ReadCounter(
                        connectionString,
                        MemberCharacterA,
                        BlackDiamondFatigueCoinService.DailyJudgeKey) == 1,
                    ref failures);

                var sameDay = fatigue.ConsumeRoom(MemberCharacterA, MemberAccount);
                Check(
                    "151 same day does not send a second mail",
                    sameDay.Consumed
                    && !sameDay.MailDelivered
                    && sameDay.State.Used == 151
                    && CountMails(connectionString, MemberCharacterA) == 1
                    && CountCoinAttachments(connectionString, MemberCharacterA) == 1,
                    ref failures);

                SeedUsed(connectionString, OrdinaryCharacter, 149);
                var ordinary = fatigue.ConsumeRoom(OrdinaryCharacter, OrdinaryAccount);
                Check(
                    "non-member crossing records judge without mail",
                    ordinary.Consumed
                    && !ordinary.MailDelivered
                    && ordinary.State.Used == 150
                    && ordinary.State.Limit == 156
                    && CountMails(connectionString, OrdinaryCharacter) == 0
                    && ReadCounter(
                        connectionString,
                        OrdinaryCharacter,
                        BlackDiamondFatigueCoinService.DailyJudgeKey) == 1,
                    ref failures);
                Execute(
                    connectionString,
                    "INSERT INTO account_premiums(account_id,premium_type,end_time) VALUES (@aid,@type,@end);",
                    ("@aid", OrdinaryAccount),
                    ("@type", PremiumService.BlackDiamondPremiumType),
                    ("@end", DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()));
                var afterBuy = fatigue.ConsumeRoom(OrdinaryCharacter, OrdinaryAccount);
                Check(
                    "opening type56 after a non-member crossing does not backfill",
                    afterBuy.Consumed
                    && !afterBuy.MailDelivered
                    && afterBuy.State.Used == 151
                    && CountMails(connectionString, OrdinaryCharacter) == 0,
                    ref failures);

                SeedUsed(connectionString, ExpiredCharacter, 149);
                var expired = fatigue.ConsumeRoom(ExpiredCharacter, ExpiredAccount);
                SeedUsed(connectionString, Type1Character, 149);
                var type1 = fatigue.ConsumeRoom(Type1Character, Type1Account);
                SeedUsed(connectionString, Type17Character, 149);
                var type17 = fatigue.ConsumeRoom(Type17Character, Type17Account);
                Check(
                    "expired/type1/type17 do not grant the coin mail",
                    expired.Consumed
                    && type1.Consumed
                    && type17.Consumed
                    && CountMails(connectionString, ExpiredCharacter) == 0
                    && CountMails(connectionString, Type1Character) == 0
                    && CountMails(connectionString, Type17Character) == 0,
                    ref failures);

                SeedUsed(connectionString, MemberCharacterB, 149);
                var otherRole = fatigue.ConsumeRoom(MemberCharacterB, MemberAccount);
                Check(
                    "same account different character has its own daily mail",
                    otherRole.Consumed
                    && otherRole.MailDelivered
                    && CountMails(connectionString, MemberCharacterB) == 1
                    && CountMails(connectionString, MemberCharacterA) == 1,
                    ref failures);

                var beforeBoundary = new DateTime(2026, 8, 20, 21, 30, 0, DateTimeKind.Utc);
                var afterBoundary = new DateTime(2026, 8, 20, 22, 30, 0, DateTimeKind.Utc);
                Check(
                    "frozen 21:30 UTC is still game day 20260820",
                    DailyResetService.TodayId(beforeBoundary) == 20260820,
                    ref failures);
                Check(
                    "frozen 22:30 UTC crosses Beijing 06:00 into 20260821",
                    DailyResetService.TodayId(afterBoundary) == 20260821,
                    ref failures);
                var utcNow = beforeBoundary;
                var timed = new DungeonFatigueService(database, () => utcNow);
                SeedUsed(connectionString, ClockCharacter, 149, utcNow);
                var firstDay = timed.ConsumeRoom(ClockCharacter, MemberAccount);
                Check(
                    "frozen pre-06:00 crossing grants one mail",
                    firstDay.Consumed
                    && firstDay.MailDelivered
                    && firstDay.State.Used == 150
                    && CountMails(connectionString, ClockCharacter) == 1
                    && CountCoinAttachments(connectionString, ClockCharacter) == 1
                    && ReadCounter(
                        connectionString,
                        ClockCharacter,
                        BlackDiamondFatigueCoinService.DailyJudgeKey) == 1,
                    ref failures);
                utcNow = afterBoundary;
                var rolled = timed.ConsumeRoom(ClockCharacter, MemberAccount);
                Check(
                    "real 06:00 rollover starts used at 1 and keeps yesterday's mail",
                    rolled.Consumed
                    && !rolled.MailDelivered
                    && rolled.State.Used == 1
                    && ReadCounter(
                        connectionString,
                        ClockCharacter,
                        BlackDiamondFatigueCoinService.DailyJudgeKey) == 0
                    && CountMails(connectionString, ClockCharacter) == 1,
                    ref failures);
                SeedUsed(connectionString, ClockCharacter, 149, utcNow);
                var secondDay = timed.ConsumeRoom(ClockCharacter, MemberAccount);
                Check(
                    "new game day grants again without deleting yesterday's mail",
                    secondDay.Consumed
                    && secondDay.MailDelivered
                    && secondDay.State.Used == 150
                    && ReadCounter(
                        connectionString,
                        ClockCharacter,
                        BlackDiamondFatigueCoinService.DailyJudgeKey) == 1
                    && CountMails(connectionString, ClockCharacter) == 2
                    && CountCoinAttachments(connectionString, ClockCharacter) == 2,
                    ref failures);

                SeedUsed(connectionString, FailCharacter, 149);
                var failing = new DungeonFatigueService(
                    database,
                    new BlackDiamondFatigueCoinService(
                        mailbox,
                        (connection, transaction, request) =>
                            MailboxSendResult.Fail(MailboxSendError.ServerBusy)));
                var failed = failing.ConsumeRoom(FailCharacter, MemberAccount);
                Check(
                    "mail insert failure rolls back consume, judge, and attachment",
                    failed.Failed
                    && !failed.MailDelivered
                    && failed.State.Used == 149
                    && failed.State.Limit == 188
                    && ReadCounter(connectionString, FailCharacter, DungeonFatigueService.DailyCounterKey) == 149
                    && ReadCounter(
                        connectionString,
                        FailCharacter,
                        BlackDiamondFatigueCoinService.DailyJudgeKey) == 0
                    && CountMails(connectionString, FailCharacter) == 0
                    && CountCoinAttachments(connectionString, FailCharacter) == 0,
                    ref failures);
                var retried = fatigue.ConsumeRoom(FailCharacter, MemberAccount);
                Check(
                    "retry after mail failure grants once",
                    retried.Consumed
                    && retried.MailDelivered
                    && retried.State.Used == 150
                    && CountMails(connectionString, FailCharacter) == 1
                    && CountCoinAttachments(connectionString, FailCharacter) == 1,
                    ref failures);

                SeedUsed(connectionString, ThrowCharacter, 149);
                var throwing = new DungeonFatigueService(
                    database,
                    new BlackDiamondFatigueCoinService(
                        mailbox,
                        (connection, transaction, request) =>
                            throw new InvalidOperationException("forced mailbox exception")));
                var crashed = throwing.ConsumeRoom(ThrowCharacter, MemberAccount);
                Check(
                    "exception rollback State keeps usedBefore, not uncommitted +1",
                    crashed.Failed
                    && !crashed.MailDelivered
                    && crashed.State.Used == 149
                    && crashed.State.Limit == 188
                    && ReadCounter(
                        connectionString,
                        ThrowCharacter,
                        DungeonFatigueService.DailyCounterKey) == 149
                    && ReadCounter(
                        connectionString,
                        ThrowCharacter,
                        BlackDiamondFatigueCoinService.DailyJudgeKey) == 0
                    && CountMails(connectionString, ThrowCharacter) == 0
                    && CountCoinAttachments(connectionString, ThrowCharacter) == 0,
                    ref failures);

                SeedUsed(connectionString, ReplayCharacter, 149);
                var replayFirst = fatigue.ConsumeRoom(ReplayCharacter, MemberAccount);
                var replaySend = mailbox.SendSystemMails(
                    new[]
                    {
                        BuildFatigueCoinMailRequest(
                            ReplayCharacter,
                            MemberAccount,
                            DateTime.UtcNow,
                            raw),
                    });
                Check(
                    "real mailbox owner replay returns IdempotencyReplay",
                    replayFirst.Consumed
                    && replayFirst.MailDelivered
                    && replaySend != null
                    && replaySend.Success
                    && replaySend.IdempotencyReplay
                    && CountMails(connectionString, ReplayCharacter) == 1,
                    ref failures);
                ClearJudge(connectionString, ReplayCharacter);
                SeedUsed(connectionString, ReplayCharacter, 149);
                var replayConsume = fatigue.ConsumeRoom(ReplayCharacter, MemberAccount);
                Check(
                    "fatigue consume on mailbox IdempotencyReplay does not set MailDelivered",
                    replayConsume.Consumed
                    && !replayConsume.MailDelivered
                    && replayConsume.State.Used == 150
                    && CountMails(connectionString, ReplayCharacter) == 1
                    && CountCoinAttachments(connectionString, ReplayCharacter) == 1,
                    ref failures);

                var usedBeforeMismatch = ReadCounter(
                    connectionString,
                    MemberCharacterA,
                    DungeonFatigueService.DailyCounterKey);
                var mismatch = fatigue.ConsumeRoom(MemberCharacterA, OrdinaryAccount);
                Check(
                    "owned account mismatch fails without consuming",
                    mismatch.Failed
                    && !mismatch.MailDelivered
                    && ReadCounter(
                        connectionString,
                        MemberCharacterA,
                        DungeonFatigueService.DailyCounterKey) == usedBeforeMismatch,
                    ref failures);

                var usedBeforeRepeat = ReadCounter(
                    connectionString,
                    MemberCharacterB,
                    DungeonFatigueService.DailyCounterKey);
                var firstRoom = fatigue.ConsumeRoom(MemberCharacterB, MemberAccount);
                var secondRoom = fatigue.ConsumeRoom(MemberCharacterB, MemberAccount);
                Check(
                    "service counts each consume; room-level skip stays on the handler already-accounted set",
                    firstRoom.Consumed
                    && secondRoom.Consumed
                    && !firstRoom.MailDelivered
                    && !secondRoom.MailDelivered
                    && ReadCounter(
                        connectionString,
                        MemberCharacterB,
                        DungeonFatigueService.DailyCounterKey)
                        == usedBeforeRepeat + 2,
                    ref failures);

                var exhausted = fatigue.GetSnapshot(OrdinaryCharacter, OrdinaryAccount);
                while (exhausted.Remaining > 0)
                {
                    var step = fatigue.ConsumeRoom(OrdinaryCharacter, OrdinaryAccount);
                    if (!step.Consumed)
                        break;
                    exhausted = step.State;
                }
                var quota = fatigue.ConsumeRoom(OrdinaryCharacter, OrdinaryAccount);
                Check(
                    "quota exhaustion is not a write failure",
                    quota.Status == DungeonFatigueConsumeStatus.QuotaExhausted
                    && !quota.Failed
                    && !quota.Consumed
                    && !quota.MailDelivered,
                    ref failures);
            }
            finally
            {
                TryDelete(tempDb);
            }
        }

        private static void VerifyWorldDropAndOrdinaryPickup(ref int failures)
        {
            if (!RequirePvf(ref failures, "world drop"))
                return;

            var worldText = PvfArchiveAccessor.ReadText("Etc/WorldDrop.etc");
            var coinWeights = ReadWorldDropItemWeights(worldText, 7454);
            var advancedWeights = ReadWorldDropItemWeights(worldText, 2749933);
            Check(
                "WorldDrop lists 7454 with a positive weight",
                coinWeights.Count > 0 && coinWeights.Exists(weight => weight > 0),
                ref failures);
            var advancedZero = 0;
            var advanced150 = 0;
            foreach (var weight in advancedWeights)
            {
                if (weight == 0)
                    advancedZero++;
                else if (weight == 150)
                    advanced150++;
            }
            Check(
                "WorldDrop keeps 2749933 weight 0 and 133 rows of weight 150; this batch does not add a path or zero them",
                advancedWeights.Count > 0
                && advancedZero > 0
                && advanced150 == 133,
                ref failures);

            DropInfo generated = default;
            var found = false;
            for (var level = 1; level <= 199 && !found; level++)
            {
                for (uint seed = 1; seed <= 2048 && !found; seed++)
                {
                    ushort slot = 0;
                    var drops = WorldDropSystem.GenerateDrops(
                        level,
                        new DnfLcg(seed),
                        ref slot);
                    foreach (var drop in drops)
                    {
                        if (drop.TemplateId == 7454)
                        {
                            generated = drop;
                            found = true;
                            break;
                        }
                    }
                }
            }

            Check("real WorldDrop generation can emit 7454", found, ref failures);
            if (!found)
                return;

            var tempDb = Path.Combine(
                Path.GetTempPath(),
                "dfo-bd-worlddrop-" + Guid.NewGuid().ToString("N") + ".db");
            InventoryLease lease = null;
            try
            {
                SqliteDatabaseBootstrap.Initialize(tempDb, ServerPaths.SchemaFilePath);
                var database = new GameDatabase(tempDb, ServerPaths.SchemaFilePath);
                Execute(
                    database.ConnectionString,
                    "INSERT INTO accounts(account_id,m_id) VALUES (781001,'world-drop-ordinary');");
                Execute(
                    database.ConnectionString,
                    "INSERT INTO characters(character_id,account_id,name) VALUES (781101,781001,'world-drop-ordinary');");
                using (var connection = database.OpenConnection())
                {
                    var inventory = InventoryService.LoadFromDb(
                        connection,
                        781101,
                        781001,
                        database);
                    lease = InventoryContext.Register(Guid.NewGuid(), inventory);
                }

                var run = new DungeonRun();
                run.Drops[generated.SceneSlot] = generated;
                var pickup = new DropService().TryPickup(
                    run,
                    generated.SceneSlot,
                    lease);
                Check(
                    "ordinary account can pick up generated 7454 without type56",
                    pickup.Success
                    && pickup.PickedUpItemId == 7454
                    && lease.Inventory.CountMainItem(7454) == 1
                    && generated.TemplateId == 7454,
                    ref failures);
            }
            finally
            {
                if (lease != null)
                    InventoryContext.Unregister(lease.SessionId, lease.CharacterId);
                TryDelete(tempDb);
            }
        }

        private static bool RequirePvf(ref int failures, string name)
        {
            var path = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return true;
            Check("[SKIP] " + name + ": PVF_ARCHIVE_PATH is not set", false, ref failures);
            return false;
        }

        private static (string Title, string Body) ReadPvfMailPair()
        {
            foreach (var path in new[]
                     {
                         "etc/hardcodetexttag_chn.etc",
                         "etc/hardcodetexttag.etc",
                     })
            {
                string text;
                try
                {
                    text = PvfArchiveAccessor.ReadText(path);
                }
                catch
                {
                    continue;
                }

                var title = ExtractTaggedString(
                    text,
                    BlackDiamondFatigueCoinService.TitleKey);
                var body = ExtractTaggedString(
                    text,
                    BlackDiamondFatigueCoinService.BodyKey);
                if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(body))
                    return (title, body);
            }

            return (null, null);
        }

        private static string ExtractTaggedString(string text, string key)
        {
            var match = Regex.Match(
                text ?? string.Empty,
                "`" + Regex.Escape(key) + "`\\s*`([^`]*)`");
            return match.Success ? match.Groups[1].Value : null;
        }

        private static List<int> ReadWorldDropItemWeights(string text, int itemId)
        {
            var result = new List<int>();
            var match = Regex.Match(
                text ?? string.Empty,
                @"\[world drop\]\s*([\s\S]*?)\s*\[/world drop\]",
                RegexOptions.IgnoreCase);
            if (!match.Success)
                return result;

            var values = new List<int>();
            foreach (var token in Regex.Replace(match.Groups[1].Value, @"//.*$", string.Empty, RegexOptions.Multiline)
                         .Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    values.Add(value);
            }

            var index = 0;
            while (index + 1 < values.Count)
            {
                index += 2;
                while (index < values.Count)
                {
                    var parsedItemId = values[index++];
                    if (parsedItemId == -1)
                        break;
                    if (index >= values.Count)
                        break;
                    var weight = values[index++];
                    if (parsedItemId == itemId)
                        result.Add(weight);
                }
            }

            return result;
        }

        private static void SeedAccounts(string connectionString)
        {
            var expire = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
            Execute(
                connectionString,
                @"
INSERT INTO accounts(account_id,m_id) VALUES
 (771001,'bd-ordinary'),
 (771002,'bd-member'),
 (771003,'bd-expired'),
 (771004,'bd-type1'),
 (771005,'bd-type17');
INSERT INTO characters(character_id,account_id,name) VALUES
 (771101,771001,'bd-ordinary'),
 (771102,771002,'bd-member-a'),
 (771103,771002,'bd-member-b'),
 (771107,771002,'bd-fail'),
 (771108,771002,'bd-clock'),
 (771109,771002,'bd-replay'),
 (771110,771002,'bd-throw'),
 (771104,771003,'bd-expired'),
 (771105,771004,'bd-type1'),
 (771106,771005,'bd-type17');
INSERT INTO account_premiums(account_id,premium_type,end_time) VALUES
 (771002,56,@expire),
 (771003,56,1),
 (771004,1,@expire),
 (771005,17,@expire);",
                ("@expire", expire));
        }

        private static void SeedUsed(
            string connectionString,
            int characterId,
            long used,
            DateTime? utcNow = null)
        {
            var now = utcNow ?? DateTime.UtcNow;
            var today = DailyResetService.TodayId(now);
            var week = DailyResetService.WeekId(now);
            Execute(
                connectionString,
                @"
INSERT INTO character_daily_reset(character_id,day_id,week_id)
VALUES (@cid,@today,@week)
ON CONFLICT(character_id) DO UPDATE SET day_id=@today, week_id=@week;
INSERT INTO character_daily_counters(character_id,counter_key,period,value)
VALUES (@cid,'dungeon_fatigue_used','day',@used)
ON CONFLICT(character_id,counter_key) DO UPDATE SET period='day', value=@used;",
                ("@cid", characterId),
                ("@today", today),
                ("@week", week),
                ("@used", used));
        }

        private static long ReadCounter(string connectionString, int characterId, string key)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT COALESCE(value,0)
FROM character_daily_counters
WHERE character_id=@cid AND counter_key=@key;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@key", key);
                    return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
                }
            }
        }

        private static int CountMails(string connectionString, int characterId)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT COUNT(*)
FROM mailbox_messages
WHERE receiver_character_id=@cid;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
        }

        private static int CountCoinAttachments(string connectionString, int characterId)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT COUNT(*)
FROM mailbox_attachments a
JOIN mailbox_messages m ON m.message_id = a.message_id
WHERE m.receiver_character_id=@cid
  AND a.item_template_id=@item
  AND a.item_count=1;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue(
                        "@item",
                        BlackDiamondFatigueCoinService.CoinItemId);
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
        }

        private static bool MailMatchesResource(
            string connectionString,
            int characterId,
            (string Title, string Body) raw)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT title, body, sender_name, mail_type
FROM mailbox_messages
WHERE receiver_character_id=@cid
LIMIT 1;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = command.ExecuteReader())
                    {
                        if (!reader.Read())
                            return false;
                        return reader.GetString(0) == raw.Title
                            && reader.GetString(1) == raw.Body
                            && reader.GetString(2) == string.Empty
                            && reader.GetInt32(3) == 1;
                    }
                }
            }
        }

        private static void Execute(
            string connectionString,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    foreach (var parameter in parameters)
                        command.Parameters.AddWithValue(parameter.Name, parameter.Value);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private static void VerifyPremiumExpiryUsesTransactionClock(ref int failures)
        {
            var tempDb = Path.Combine(Path.GetTempPath(),
                "dfo-bd-premium-clock-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    tempDb, ServerPaths.SchemaFilePath);
                SeedAccounts(connectionString);
                var database = new GameDatabase(tempDb, ServerPaths.SchemaFilePath);
                var expiry = new DateTime(2020, 1, 2, 12, 0, 0, DateTimeKind.Utc);
                Execute(connectionString,
                    "UPDATE account_premiums SET end_time=@expiry WHERE account_id=@aid AND premium_type=56;",
                    ("@expiry", new DateTimeOffset(expiry).ToUnixTimeSeconds()),
                    ("@aid", MemberAccount));

                var now = expiry.AddSeconds(-1);
                var clockReads = 0;
                var fatigue = new DungeonFatigueService(database, () =>
                {
                    clockReads++;
                    return now;
                });
                SeedUsed(connectionString, MemberCharacterA, 149, now);
                var active = fatigue.ConsumeRoom(MemberCharacterA, MemberAccount);
                Check("membership, fatigue and mail share the captured transaction time",
                    active.Consumed && active.MailDelivered && active.State.Limit == 188
                    && CountMails(connectionString, MemberCharacterA) == 1
                    && clockReads == 1, ref failures);

                now = expiry;
                SeedUsed(connectionString, MemberCharacterB, 149, now);
                var expired = fatigue.ConsumeRoom(MemberCharacterB, MemberAccount);
                Check("exact expiry uses ordinary quota and does not grant a fatigue coin",
                    expired.Consumed && !expired.MailDelivered && expired.State.Limit == 156
                    && CountMails(connectionString, MemberCharacterB) == 0
                    && clockReads == 2, ref failures);
            }
            finally
            {
                TryDelete(tempDb);
            }
        }

        private static void VerifyDungeonMapHandler(ref int failures)
        {
            if (!RequirePvf(ref failures, "dungeon map handler"))
                return;

            if (!TryResolveHandlerRooms(
                    out var dungeonId,
                    out var startX,
                    out var startY,
                    out var nextX,
                    out var nextY))
            {
                Check(
                    "handler fixture resolved a real PVF dungeon with two rooms",
                    false,
                    ref failures);
                return;
            }

            var tempDb = Path.Combine(
                Path.GetTempPath(),
                "dfo-bd-fatigue-handler-" + Guid.NewGuid().ToString("N") + ".db");
            A21DungeonDropItemSelfTest.LoopbackPacketCapture capture = null;
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    tempDb,
                    ServerPaths.SchemaFilePath);
                SeedAccounts(connectionString);
                var database = new GameDatabase(tempDb, ServerPaths.SchemaFilePath);
                var shared = CreateDungeonSharedServices(database);
                var handler = new DungeonMapHandler(shared);
                capture = new A21DungeonDropItemSelfTest.LoopbackPacketCapture();
                var session = capture.Session;

                SeedUsed(connectionString, MemberCharacterA, 149);
                var run = AttachMazeRun(
                    session,
                    MemberCharacterA,
                    OrdinaryAccount,
                    dungeonId);
                var failedIdentity = handler.SendStartMapAsync(
                        session,
                        run,
                        startX,
                        startY,
                        0)
                    .GetAwaiter().GetResult();
                Check(
                    "handler Failed does not mark the room or consume fatigue",
                    failedIdentity == null
                    && run.Combat.FatigueAccountedRooms.Count == 0
                    && ReadCounter(
                        connectionString,
                        MemberCharacterA,
                        DungeonFatigueService.DailyCounterKey) == 149
                    && CountMails(connectionString, MemberCharacterA) == 0,
                    ref failures);
                CheckNoPackets(
                    capture,
                    session,
                    "handler Failed sends no FATIGUE/START_MAP/MAILBOX_ALARM",
                    ref failures);

                session.Account.AccountId = MemberAccount;
                var retryIdentity = handler.SendStartMapAsync(
                        session,
                        run,
                        startX,
                        startY,
                        0)
                    .GetAwaiter().GetResult();
                var retryPackets = capture.ReadPackets(3);
                Check(
                    "retry of the original room consumes once and sends FATIGUE plus MAILBOX_ALARM",
                    retryIdentity != null
                    && run.Combat.FatigueAccountedRooms.Count == 1
                    && ReadCounter(
                        connectionString,
                        MemberCharacterA,
                        DungeonFatigueService.DailyCounterKey) == 150
                    && CountMails(connectionString, MemberCharacterA) == 1
                    && CountCoinAttachments(connectionString, MemberCharacterA) == 1
                    && CountNoti(retryPackets, (ushort)NotiPacketTypeA21.FATIGUE) == 1
                    && CountNoti(retryPackets, (ushort)NotiPacketTypeA21.MAILBOX_ALARM) == 1
                    && CountNoti(retryPackets, (ushort)NotiPacketTypeA21.START_MAP) == 1
                    && FatigueBodyMatches(retryPackets, 150, 188)
                    && MailboxAlarmCount(retryPackets) == 1,
                    ref failures);

                var revisitIdentity = handler.SendStartMapAsync(
                        session,
                        run,
                        startX,
                        startY,
                        0)
                    .GetAwaiter().GetResult();
                var revisitPackets = capture.ReadPackets(1);
                Check(
                    "successful revisit does not consume or double-send fatigue mail alarm",
                    revisitIdentity != null
                    && run.Combat.FatigueAccountedRooms.Count == 1
                    && ReadCounter(
                        connectionString,
                        MemberCharacterA,
                        DungeonFatigueService.DailyCounterKey) == 150
                    && CountMails(connectionString, MemberCharacterA) == 1
                    && CountNoti(revisitPackets, (ushort)NotiPacketTypeA21.FATIGUE) == 0
                    && CountNoti(revisitPackets, (ushort)NotiPacketTypeA21.MAILBOX_ALARM) == 0
                    && CountNoti(revisitPackets, (ushort)NotiPacketTypeA21.START_MAP) == 1,
                    ref failures);

                var secondIdentity = handler.SendStartMapAsync(
                        session,
                        run,
                        nextX,
                        nextY,
                        0)
                    .GetAwaiter().GetResult();
                var secondPackets = capture.ReadPackets(2);
                Check(
                    "later room consume does not false-alarm mailbox",
                    secondIdentity != null
                    && run.Combat.FatigueAccountedRooms.Count == 2
                    && ReadCounter(
                        connectionString,
                        MemberCharacterA,
                        DungeonFatigueService.DailyCounterKey) == 151
                    && CountMails(connectionString, MemberCharacterA) == 1
                    && CountNoti(secondPackets, (ushort)NotiPacketTypeA21.FATIGUE) == 1
                    && CountNoti(secondPackets, (ushort)NotiPacketTypeA21.MAILBOX_ALARM) == 0
                    && CountNoti(secondPackets, (ushort)NotiPacketTypeA21.START_MAP) == 1,
                    ref failures);

                SeedUsed(connectionString, OrdinaryCharacter, 149);
                var ordinaryRun = AttachMazeRun(
                    session,
                    OrdinaryCharacter,
                    OrdinaryAccount,
                    dungeonId);
                var ordinaryIdentity = handler.SendStartMapAsync(
                        session,
                        ordinaryRun,
                        startX,
                        startY,
                        0)
                    .GetAwaiter().GetResult();
                var ordinaryPackets = capture.ReadPackets(2);
                Check(
                    "non-member crossing through handler does not send MAILBOX_ALARM",
                    ordinaryIdentity != null
                    && CountMails(connectionString, OrdinaryCharacter) == 0
                    && ReadCounter(
                        connectionString,
                        OrdinaryCharacter,
                        BlackDiamondFatigueCoinService.DailyJudgeKey) == 1
                    && CountNoti(ordinaryPackets, (ushort)NotiPacketTypeA21.FATIGUE) == 1
                    && CountNoti(ordinaryPackets, (ushort)NotiPacketTypeA21.MAILBOX_ALARM) == 0,
                    ref failures);

                var mailbox = new MailboxService(new MailboxRepository(database));
                ReplaceFatigue(
                    shared,
                    new DungeonFatigueService(
                        database,
                        new BlackDiamondFatigueCoinService(
                            mailbox,
                            (connection, transaction, request) =>
                                MailboxSendResult.Fail(MailboxSendError.ServerBusy))));
                SeedUsed(connectionString, FailCharacter, 149);
                var failRun = AttachMazeRun(
                    session,
                    FailCharacter,
                    MemberAccount,
                    dungeonId);
                var mailFailedIdentity = handler.SendStartMapAsync(
                        session,
                        failRun,
                        startX,
                        startY,
                        0)
                    .GetAwaiter().GetResult();
                Check(
                    "handler mail write failure does not mark the room",
                    mailFailedIdentity == null
                    && failRun.Combat.FatigueAccountedRooms.Count == 0
                    && ReadCounter(
                        connectionString,
                        FailCharacter,
                        DungeonFatigueService.DailyCounterKey) == 149
                    && CountMails(connectionString, FailCharacter) == 0,
                    ref failures);
                CheckNoPackets(
                    capture,
                    session,
                    "handler mail write failure sends no FATIGUE/START_MAP/MAILBOX_ALARM",
                    ref failures);

                ReplaceFatigue(shared, new DungeonFatigueService(database));
                var mailRetryIdentity = handler.SendStartMapAsync(
                        session,
                        failRun,
                        startX,
                        startY,
                        0)
                    .GetAwaiter().GetResult();
                var mailRetryPackets = capture.ReadPackets(3);
                Check(
                    "handler retry after mail failure consumes and alarms once",
                    mailRetryIdentity != null
                    && failRun.Combat.FatigueAccountedRooms.Count == 1
                    && ReadCounter(
                        connectionString,
                        FailCharacter,
                        DungeonFatigueService.DailyCounterKey) == 150
                    && CountMails(connectionString, FailCharacter) == 1
                    && CountNoti(mailRetryPackets, (ushort)NotiPacketTypeA21.FATIGUE) == 1
                    && CountNoti(mailRetryPackets, (ushort)NotiPacketTypeA21.MAILBOX_ALARM) == 1
                    && CountNoti(mailRetryPackets, (ushort)NotiPacketTypeA21.START_MAP) == 1,
                    ref failures);

                // Pause the real handler at its await, then invalidate its owner.
                // No sleep, mock handler or timing-dependent thread race is needed.
                for (var invalidation = 0; invalidation < 3; invalidation++)
                {
                    SeedUsed(connectionString, MemberCharacterA, 149);
                    SeedUsed(connectionString, MemberCharacterB, 149);
                    var staleRun = AttachMazeRun(session, MemberCharacterA,
                        MemberAccount, dungeonId);
                    staleRun.Combat.FatigueRoomGate.Wait();
                    var pending = handler.SendStartMapAsync(session, staleRun,
                        startX, startY, 0);
                    try
                    {
                        Check("handler waits at fatigue gate before owner invalidation",
                            !pending.IsCompleted, ref failures);
                        if (invalidation == 0)
                            session.Player.CurrentRun = null;
                        else if (invalidation == 1)
                            AttachMazeRun(session, MemberCharacterB, MemberAccount, dungeonId);
                        else
                            staleRun.RoomKey = new RoomKey(nextX, nextY, 0);
                    }
                    finally
                    {
                        staleRun.Combat.FatigueRoomGate.Release();
                    }
                    var staleIdentity = pending.GetAwaiter().GetResult();
                    Check("stale fatigue request does not charge either role or mark its room: " + invalidation,
                        staleIdentity == null && staleRun.Combat.FatigueAccountedRooms.Count == 0
                        && ReadCounter(connectionString, MemberCharacterA,
                            DungeonFatigueService.DailyCounterKey) == 149
                        && ReadCounter(connectionString, MemberCharacterB,
                            DungeonFatigueService.DailyCounterKey) == 149, ref failures);
                    CheckNoPackets(capture, session,
                        "stale fatigue request sends no packets: " + invalidation, ref failures);
                }
            }
            finally
            {
                capture?.Dispose();
                TryDelete(tempDb);
            }
        }

        private static void VerifyFailedDoesNotRereadSnapshot(ref int failures)
        {
            var tempDb = Path.Combine(
                Path.GetTempPath(),
                "dfo-bd-fatigue-failed-db-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                SqliteDatabaseBootstrap.Initialize(tempDb, ServerPaths.SchemaFilePath);
                var database = new GameDatabase(tempDb, ServerPaths.SchemaFilePath);
                var fatigue = new DungeonFatigueService(database);
                TryDelete(tempDb);
                TryDelete(tempDb + "-wal");
                TryDelete(tempDb + "-shm");
                DungeonFatigueConsumeResult result;
                try
                {
                    result = fatigue.ConsumeRoom(MemberCharacterA, MemberAccount);
                }
                catch (Exception ex)
                {
                    Check(
                        "Failed consume does not throw from snapshot re-read: " + ex.Message,
                        false,
                        ref failures);
                    return;
                }

                Check(
                    "Failed consume returns Failed without a second snapshot read",
                    result.Failed && !result.MailDelivered,
                    ref failures);
            }
            finally
            {
                TryDelete(tempDb);
                TryDelete(tempDb + "-wal");
                TryDelete(tempDb + "-shm");
            }
        }

        private static DungeonSharedServices CreateDungeonSharedServices(GameDatabase database)
        {
            var characters = new SqliteCharacterRepository(database);
            var source = new SqliteSelectCharacterDataSource(database, characters);
            return new DungeonSharedServices(
                new Game.ReviveCoin.ReviveCoinService(
                    new DailyResetService(database)),
                characters,
                source,
                null,
                database.ConnectionString,
                new InventoryRefreshSender(source, characters, database),
                database: database);
        }

        private static DungeonRun AttachMazeRun(
            EnhancedClientSession session,
            int characterId,
            int accountId,
            short dungeonId)
        {
            session.Account = new AccountRecord { AccountId = accountId };
            session.Player.CharacterId = characterId;
            var run = new DungeonRun(dungeonId, 0);
            session.Player.CurrentRun = run;
            return run;
        }

        private static bool TryResolveHandlerRooms(
            out short dungeonId,
            out int startX,
            out int startY,
            out int nextX,
            out int nextY)
        {
            dungeonId = 0;
            startX = 0;
            startY = 0;
            nextX = 0;
            nextY = 0;
            foreach (var candidate in new short[] { 1, 88, 93 })
            {
                try
                {
                    if (Dungeon.GetDungeonFile(candidate) == null)
                        continue;
                    var maze = Dungeon.GetDungeonDefaultMaze(candidate);
                    if (maze?.StartMap == null || maze.StartMap.Length < 2)
                        continue;
                    var sx = maze.StartMap[0];
                    var sy = maze.StartMap[1];
                    var start = Dungeon.GetDungeonMapMonsterSummaryInformation(
                        candidate,
                        sx,
                        sy);
                    if (start.Index <= 0)
                        continue;
                    if (!TryFindSecondRoom(candidate, maze, sx, sy, out var nx, out var ny))
                        continue;
                    dungeonId = candidate;
                    startX = sx;
                    startY = sy;
                    nextX = nx;
                    nextY = ny;
                    return true;
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool TryFindSecondRoom(
            short dungeonId,
            PvfLib.MazeInfo maze,
            int startX,
            int startY,
            out int nextX,
            out int nextY)
        {
            nextX = 0;
            nextY = 0;
            if (maze.MapSpecifications != null)
            {
                foreach (var spec in maze.MapSpecifications)
                {
                    if (spec == null || (spec.X == startX && spec.Y == startY))
                        continue;
                    var summary = Dungeon.GetDungeonMapMonsterSummaryInformation(
                        dungeonId,
                        spec.X,
                        spec.Y);
                    if (summary.Index <= 0)
                        continue;
                    nextX = spec.X;
                    nextY = spec.Y;
                    return true;
                }
            }

            var width = Math.Max(maze.Width, 1);
            var height = Math.Max(maze.Height, 1);
            for (var x = 0; x < width; x++)
            {
                for (var y = 0; y < height; y++)
                {
                    if (x == startX && y == startY)
                        continue;
                    var summary = Dungeon.GetDungeonMapMonsterSummaryInformation(
                        dungeonId,
                        x,
                        y);
                    if (summary.Index <= 0)
                        continue;
                    nextX = x;
                    nextY = y;
                    return true;
                }
            }

            return false;
        }

        private static MailboxSendRequest BuildFatigueCoinMailRequest(
            int characterId,
            int accountId,
            DateTime utcNow,
            (string Title, string Body) raw)
        {
            return new MailboxSendRequest
            {
                SenderCharacterId = characterId,
                SenderAccountId = accountId,
                SenderName = string.Empty,
                ReceiverCharacterId = characterId,
                ReceiverAccountId = accountId,
                Title = raw.Title,
                Text = raw.Body,
                MailType = 1,
                Unlimited = true,
                IdempotencyKey =
                    "black-diamond-fatigue-coin:" +
                    characterId +
                    ":" +
                    DailyResetService.TodayId(utcNow),
                AuditActor = "black-diamond-fatigue-coin",
                AuditReason = BlackDiamondFatigueCoinService.DailyJudgeKey,
                Attachments = new[]
                {
                    new MailboxSendAttachmentRequest
                    {
                        ItemId = BlackDiamondFatigueCoinService.CoinItemId,
                        ItemCount = 1,
                    },
                },
            };
        }

        private static void ClearJudge(string connectionString, int characterId)
        {
            Execute(
                connectionString,
                @"
DELETE FROM character_daily_counters
WHERE character_id=@cid AND counter_key=@key;",
                ("@cid", characterId),
                ("@key", BlackDiamondFatigueCoinService.DailyJudgeKey));
        }

        private static void ReplaceFatigue(
            DungeonSharedServices shared,
            DungeonFatigueService fatigue)
        {
            var property = typeof(DungeonSharedServices).GetProperty(
                "Fatigue",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite)
            {
                property.SetValue(shared, fatigue);
                return;
            }

            var backing = typeof(DungeonSharedServices).GetField(
                "<Fatigue>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (backing == null)
                throw new InvalidOperationException("DungeonSharedServices.Fatigue cannot be replaced");
            backing.SetValue(shared, fatigue);
        }

        private static void CheckNoPackets(
            A21DungeonDropItemSelfTest.LoopbackPacketCapture capture,
            EnhancedClientSession session,
            string label,
            ref int failures)
        {
            var marker = GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.USER_STATE,
                EnterSelectDungeonStateBuilder.BuildUserState(session.Player));
            session.SendPacketAsync(marker).GetAwaiter().GetResult();
            var packets = capture.ReadPackets(1);
            Check(
                label,
                packets.Count == 1 && packets[0].SequenceEqual(marker),
                ref failures);
        }

        private static bool IsNoti(byte[] packet, ushort opcode)
            => packet != null
                && packet.Length >= 3
                && packet[0] == 0
                && BitConverter.ToUInt16(packet, 1) == opcode;

        private static int CountNoti(IReadOnlyList<byte[]> packets, ushort opcode)
        {
            var count = 0;
            if (packets == null)
                return 0;
            foreach (var packet in packets)
            {
                if (IsNoti(packet, opcode))
                    count++;
            }

            return count;
        }

        private static bool FatigueBodyMatches(
            IReadOnlyList<byte[]> packets,
            ushort used,
            ushort limit)
        {
            if (packets == null)
                return false;
            foreach (var packet in packets)
            {
                if (!IsNoti(packet, (ushort)NotiPacketTypeA21.FATIGUE)
                    || packet.Length < 25)
                {
                    continue;
                }

                return BitConverter.ToUInt16(packet, 15) == used
                    && BitConverter.ToUInt16(packet, 17) == limit;
            }

            return false;
        }

        private static int MailboxAlarmCount(IReadOnlyList<byte[]> packets)
        {
            if (packets == null)
                return -1;
            foreach (var packet in packets)
            {
                if (!IsNoti(packet, (ushort)NotiPacketTypeA21.MAILBOX_ALARM)
                    || packet.Length < 17)
                {
                    continue;
                }

                return BitConverter.ToUInt16(packet, 15);
            }

            return -1;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine("[" + (ok ? "PASS" : "FAIL") + "] " + name);
            if (!ok)
                failures++;
        }
    }
}
