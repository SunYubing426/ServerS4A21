using System;
using System.Globalization;
using System.IO;
using DfoServer.Game.Characters;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.SelfTests
{
    public static class CharacterFatigueSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== CHARACTER_FATIGUE selftest ===");
            var failures = 0;

            VerifySchemaAndDefaults(ref failures);
            VerifyConsumePersistsAcrossReload(ref failures);
            VerifyPartyConsumeIsAtomic(ref failures);
            VerifyDailyResetDoesNotRestoreSameDay(ref failures);
            VerifySelectCharacterAckWritesPersistedUsed(ref failures);
            VerifyEntryCostRules(ref failures);
            VerifyRoomVisitRules(ref failures);
            VerifyV26ToCurrentMigration(ref failures);

            Console.WriteLine(
                failures == 0
                    ? "CHARACTER_FATIGUE selftest passed."
                    : $"CHARACTER_FATIGUE selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifySchemaAndDefaults(ref int failures)
        {
            var databasePath = TempDbPath("schema");
            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = database.OpenConnection())
                {
                    Check(
                        "new schema has character fatigue columns at current version",
                        ColumnExists(connection, "characters", "usedFatigue")
                        && ColumnExists(connection, "characters", "maxFatigue")
                        && ColumnExists(connection, "characters", "fatigue")
                        && SqliteMigrations.ReadVersion(connection)
                            == SqliteMigrations.CurrentVersion,
                        ref failures);
                }

                SeedAccount(database, 101, 1001);
                var service = new CharacterFatigueService(database);
                var loaded = service.Load(1001);
                Check(
                    "new character starts with unused default max fatigue",
                    loaded.Used == 0
                    && loaded.Max == CharacterFatigueService.DefaultMaxFatigue
                    && loaded.Remaining == CharacterFatigueService.DefaultMaxFatigue,
                    ref failures);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        private static void VerifyConsumePersistsAcrossReload(ref int failures)
        {
            var databasePath = TempDbPath("consume");
            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                SeedAccount(database, 201, 2001);
                var service = new CharacterFatigueService(database);

                Check(
                    "first dungeon consume stores used fatigue",
                    service.TryConsume(2001, 1, out var first)
                    && first.Allowed
                    && first.Used == 1
                    && first.Max == CharacterFatigueService.DefaultMaxFatigue,
                    ref failures);

                var reloaded = new CharacterFatigueService(database).Load(2001);
                Check(
                    "new service instance still sees consumed fatigue",
                    reloaded.Used == 1
                    && reloaded.Remaining
                        == CharacterFatigueService.DefaultMaxFatigue - 1,
                    ref failures);

                var remaining = CharacterFatigueService.DefaultMaxFatigue - 1;
                Check(
                    "exhausting remaining fatigue is allowed",
                    service.TryConsume(2001, remaining, out var last)
                    && last.Allowed
                    && last.Used == CharacterFatigueService.DefaultMaxFatigue,
                    ref failures);
                Check(
                    "further consume is rejected after fatigue is spent",
                    !service.TryConsume(2001, 1, out var rejected)
                    && !rejected.Allowed
                    && rejected.Reason == "insufficient_fatigue"
                    && new CharacterFatigueService(database).Load(2001).Used
                        == CharacterFatigueService.DefaultMaxFatigue,
                    ref failures);

                Check(
                    "zero-cost dungeon does not change used fatigue",
                    service.TryConsume(2001, 0, out var zero)
                    && zero.Allowed
                    && new CharacterFatigueService(database).Load(2001).Used
                        == CharacterFatigueService.DefaultMaxFatigue,
                    ref failures);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        private static void VerifyPartyConsumeIsAtomic(ref int failures)
        {
            var databasePath = TempDbPath("party");
            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                SeedAccount(database, 211, 2101);
                SeedCharacter(database, 211, 2102);
                var service = new CharacterFatigueService(database);
                Check(
                    "exhaust second party member",
                    service.TryConsume(
                        2102,
                        CharacterFatigueService.DefaultMaxFatigue,
                        out var exhausted)
                    && exhausted.Allowed,
                    ref failures);

                var targets = new[]
                {
                    new CharacterFatigueTarget(2101, 0),
                    new CharacterFatigueTarget(2102, 1),
                };
                Check(
                    "party consume rejects when any member lacks fatigue",
                    !service.TryConsumeMany(targets, 1, out var rejected)
                    && !rejected.Allowed
                    && rejected.MemberSlot == 1
                    && service.Load(2101).Used == 0
                    && service.Load(2102).Used
                        == CharacterFatigueService.DefaultMaxFatigue,
                    ref failures);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        private static void VerifyDailyResetDoesNotRestoreSameDay(ref int failures)
        {
            var databasePath = TempDbPath("reset");
            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                SeedAccount(database, 301, 3001);
                var fatigue = new CharacterFatigueService(database);
                var dailyReset = new DailyResetService(database);
                Check(
                    "seed consume before reset checks",
                    fatigue.TryConsume(3001, 40, out var consumed)
                    && consumed.Allowed
                    && consumed.Used == 40,
                    ref failures);

                var logoutUtc = new DateTime(2026, 8, 20, 20, 30, 0, DateTimeKind.Utc);
                var preBoundaryUtc = new DateTime(2026, 8, 20, 21, 30, 0, DateTimeKind.Utc);
                var afterBoundaryUtc = new DateTime(2026, 8, 20, 22, 30, 0, DateTimeKind.Utc);
                var sameDayReloginUtc = new DateTime(2026, 8, 20, 23, 10, 0, DateTimeKind.Utc);

                Check(
                    "record logout before daily boundary",
                    dailyReset.TryRecordAccountLogout(301, logoutUtc),
                    ref failures);

                Check(
                    "same-day login after consume does not restore fatigue",
                    RunAccountReset(
                        dailyReset,
                        fatigue,
                        database,
                        301,
                        preBoundaryUtc,
                        out var sameDayApplied)
                    && !sameDayApplied
                    && fatigue.Load(3001).Used == 40,
                    ref failures);

                Check(
                    "first login after 06:00 restores used fatigue",
                    RunAccountReset(
                        dailyReset,
                        fatigue,
                        database,
                        301,
                        afterBoundaryUtc,
                        out var applied)
                    && applied
                    && fatigue.Load(3001).Used == 0,
                    ref failures);

                Check(
                    "consume again after daily reset",
                    fatigue.TryConsume(3001, 12, out var afterReset)
                    && afterReset.Used == 12,
                    ref failures);
                Check(
                    "record logout after 06:00",
                    dailyReset.TryRecordAccountLogout(301, sameDayReloginUtc),
                    ref failures);
                Check(
                    "login later the same day still keeps used fatigue",
                    RunAccountReset(
                        dailyReset,
                        fatigue,
                        database,
                        301,
                        sameDayReloginUtc.AddHours(1),
                        out var secondApplied)
                    && !secondApplied
                    && fatigue.Load(3001).Used == 12,
                    ref failures);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        private static void VerifySelectCharacterAckWritesPersistedUsed(
            ref int failures)
        {
            var databasePath = TempDbPath("ack");
            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                SeedAccount(database, 401, 4001);
                var service = new CharacterFatigueService(database);
                Check(
                    "ack fixture consume",
                    service.TryConsume(4001, 33, out var consumed) && consumed.Allowed,
                    ref failures);

                var snapshot = new SelectCharacterDataSnapshot
                {
                    CharacterRecord = new CharacterRecord
                    {
                        CharacterId = 4001,
                        CreatedAt = DateTime.UtcNow,
                    },
                    InitializationSnapshot = new SelectCharacterInitializationSnapshot(),
                };
                Check(
                    "select-character ACK writes used, max, and used fatigue",
                    SelectCharacterAckBodyBuilder.TryBuild(
                        snapshot,
                        database.ConnectionString,
                        out var body)
                    && body != null
                    && BitConverter.ToInt16(body, 11) == 33
                    && BitConverter.ToInt16(body, 13)
                        == CharacterFatigueService.DefaultMaxFatigue
                    && BitConverter.ToInt16(body, 15) == 33,
                    ref failures);
                var notification = CharacterFatiguePacketBuilder.BuildNotification(
                    new CharacterFatigueSnapshot(33, CharacterFatigueService.DefaultMaxFatigue));
                Check(
                    "NOTI FATIGUE body matches ACK fatigue triple",
                    notification != null
                    && notification.Length == 6
                    && BitConverter.ToInt16(notification, 0) == 33
                    && BitConverter.ToInt16(notification, 2)
                        == CharacterFatigueService.DefaultMaxFatigue
                    && BitConverter.ToInt16(notification, 4) == 33,
                    ref failures);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        private static void VerifyEntryCostRules(ref int failures)
        {
            Check(
                "missing dungeon file defaults to one fatigue",
                CharacterFatigueService.ResolveEntryCost(null)
                    == CharacterFatigueService.DefaultEntryCost,
                ref failures);
            Check(
                "no-fatigue dungeons cost zero",
                CharacterFatigueService.ResolveEntryCost(
                    new DungeonFile { NoFatigue = true }) == 0,
                ref failures);
            Check(
                "enter-without-fatigue dungeons cost zero",
                CharacterFatigueService.ResolveEntryCost(
                    new DungeonFile { EnterWithoutFatigue = true }) == 0,
                ref failures);
            Check(
                "explicit DGN fatigue value is used",
                CharacterFatigueService.ResolveEntryCost(
                    new DungeonFile { Fatigue = 8 }) == 8,
                ref failures);
            Check(
                "unset DGN fatigue defaults to one",
                CharacterFatigueService.ResolveEntryCost(new DungeonFile())
                    == CharacterFatigueService.DefaultEntryCost,
                ref failures);
            Check(
                "default dungeon charges per visited room",
                CharacterFatigueService.ResolveChargeMode(null)
                    == CharacterFatigueChargeMode.PerVisitedRoom
                    && CharacterFatigueService.ResolveChargeMode(new DungeonFile())
                        == CharacterFatigueChargeMode.PerVisitedRoom,
                ref failures);
            Check(
                "no-fatigue dungeons do not charge rooms or select",
                CharacterFatigueService.ResolveChargeMode(
                    new DungeonFile { NoFatigue = true })
                    == CharacterFatigueChargeMode.None,
                ref failures);
            Check(
                "enter-without-fatigue dungeons do not charge rooms or select",
                CharacterFatigueService.ResolveChargeMode(
                    new DungeonFile { EnterWithoutFatigue = true })
                    == CharacterFatigueChargeMode.None,
                ref failures);
            Check(
                "use-fatigue-only-start-dungeon charges at select only",
                CharacterFatigueService.ChargesOnlyAtSelectDungeon(
                    new DungeonFile { UseFatigueOnlyStartDungeon = true })
                && CharacterFatigueService.ResolveChargeMode(
                    new DungeonFile { UseFatigueOnlyStartDungeon = true })
                    == CharacterFatigueChargeMode.OnlyStartDungeon,
                ref failures);
            Check(
                "no-fatigue wins over only-start-dungeon",
                CharacterFatigueService.ResolveChargeMode(
                    new DungeonFile
                    {
                        NoFatigue = true,
                        UseFatigueOnlyStartDungeon = true,
                    }) == CharacterFatigueChargeMode.None,
                ref failures);
        }

        private static void VerifyRoomVisitRules(ref int failures)
        {
            var databasePath = TempDbPath("room");
            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                SeedAccount(database, 501, 5001);
                SeedCharacter(database, 501, 5002);
                var service = new CharacterFatigueService(database);
                var run = new DungeonRun(1, 0);
                var solo = new[] { new CharacterFatigueTarget(5001, 0) };

                Check(
                    "first logical room visit consumes default fatigue",
                    service.TryConsumeRoomVisit(
                        run,
                        solo,
                        mazeIndex: 2,
                        cellX: 1,
                        cellY: 3,
                        dungeon: new DungeonFile(),
                        out var first)
                    && first.Allowed
                    && first.Cost == 1
                    && first.Used == 1
                    && service.Load(5001).Used == 1,
                    ref failures);
                Check(
                    "revisit of the same maze+x+y does not consume",
                    service.TryConsumeRoomVisit(
                        run,
                        solo,
                        mazeIndex: 2,
                        cellX: 1,
                        cellY: 3,
                        dungeon: new DungeonFile(),
                        out var revisit)
                    && revisit.Allowed
                    && revisit.Reason == "revisit"
                    && revisit.Cost == 0
                    && service.Load(5001).Used == 1,
                    ref failures);
                Check(
                    "a new cell consumes again",
                    service.TryConsumeRoomVisit(
                        run,
                        solo,
                        mazeIndex: 2,
                        cellX: 2,
                        cellY: 3,
                        dungeon: new DungeonFile(),
                        out var nextCell)
                    && nextCell.Allowed
                    && nextCell.Cost == 1
                    && service.Load(5001).Used == 2,
                    ref failures);
                Check(
                    "same x+y in another maze consumes",
                    service.TryConsumeRoomVisit(
                        run,
                        solo,
                        mazeIndex: 3,
                        cellX: 1,
                        cellY: 3,
                        dungeon: new DungeonFile(),
                        out var otherMaze)
                    && otherMaze.Allowed
                    && otherMaze.Cost == 1
                    && service.Load(5001).Used == 3,
                    ref failures);

                var onlyStartRun = new DungeonRun(1, 0);
                Check(
                    "only-start-dungeon room visits do not consume",
                    service.TryConsumeRoomVisit(
                        onlyStartRun,
                        solo,
                        mazeIndex: 0,
                        cellX: 0,
                        cellY: 0,
                        dungeon: new DungeonFile
                        {
                            UseFatigueOnlyStartDungeon = true,
                        },
                        out var onlyStart)
                    && onlyStart.Allowed
                    && onlyStart.Reason == "only_start_dungeon"
                    && onlyStart.Cost == 0
                    && service.Load(5001).Used == 3,
                    ref failures);

                var explicitRun = new DungeonRun(1, 0);
                Check(
                    "explicit DGN fatigue is charged per new cell",
                    service.TryConsumeRoomVisit(
                        explicitRun,
                        solo,
                        mazeIndex: 0,
                        cellX: 4,
                        cellY: 5,
                        dungeon: new DungeonFile { Fatigue = 3 },
                        out var explicitCost)
                    && explicitCost.Allowed
                    && explicitCost.Cost == 3
                    && service.Load(5001).Used == 6,
                    ref failures);

                var zeroRun = new DungeonRun(1, 0);
                Check(
                    "no-fatigue room visits stay at zero cost",
                    service.TryConsumeRoomVisit(
                        zeroRun,
                        solo,
                        mazeIndex: 0,
                        cellX: 1,
                        cellY: 1,
                        dungeon: new DungeonFile { NoFatigue = true },
                        out var none)
                    && none.Allowed
                    && none.Cost == 0
                    && service.Load(5001).Used == 6,
                    ref failures);

                var remaining = CharacterFatigueService.DefaultMaxFatigue - 6;
                Check(
                    "exhaust remaining fatigue before insufficient visit",
                    service.TryConsume(5001, remaining, out var drained)
                    && drained.Allowed
                    && service.Load(5001).Used
                        == CharacterFatigueService.DefaultMaxFatigue,
                    ref failures);
                var blockedRun = new DungeonRun(1, 0);
                Check(
                    "insufficient room visit is rejected and not marked",
                    !service.TryConsumeRoomVisit(
                        blockedRun,
                        solo,
                        mazeIndex: 0,
                        cellX: 7,
                        cellY: 8,
                        dungeon: new DungeonFile(),
                        out var blocked)
                    && !blocked.Allowed
                    && blocked.Reason == "insufficient_fatigue"
                    && service.Load(5001).Used
                        == CharacterFatigueService.DefaultMaxFatigue,
                    ref failures);
                Check(
                    "unmarked failed cell can be attempted again",
                    !service.TryConsumeRoomVisit(
                        blockedRun,
                        solo,
                        mazeIndex: 0,
                        cellX: 7,
                        cellY: 8,
                        dungeon: new DungeonFile(),
                        out var blockedAgain)
                    && blockedAgain.Reason == "insufficient_fatigue",
                    ref failures);

                SeedAccount(database, 511, 5101);
                SeedCharacter(database, 511, 5102);
                var partyService = new CharacterFatigueService(database);
                Check(
                    "exhaust second party member before room visit",
                    partyService.TryConsume(
                        5102,
                        CharacterFatigueService.DefaultMaxFatigue,
                        out var partyExhausted)
                    && partyExhausted.Allowed,
                    ref failures);
                var partyRun = new DungeonRun(1, 0);
                var partyTargets = new[]
                {
                    new CharacterFatigueTarget(5101, 0),
                    new CharacterFatigueTarget(5102, 1),
                };
                Check(
                    "party room visit rejects atomically when a member lacks fatigue",
                    !partyService.TryConsumeRoomVisit(
                        partyRun,
                        partyTargets,
                        mazeIndex: 0,
                        cellX: 1,
                        cellY: 1,
                        dungeon: new DungeonFile(),
                        out var partyRejected)
                    && !partyRejected.Allowed
                    && partyRejected.MemberSlot == 1
                    && partyService.Load(5101).Used == 0
                    && partyService.Load(5102).Used
                        == CharacterFatigueService.DefaultMaxFatigue,
                    ref failures);
                Check(
                    "failed party visit leaves the cell unmarked",
                    !partyService.TryConsumeRoomVisit(
                        partyRun,
                        partyTargets,
                        mazeIndex: 0,
                        cellX: 1,
                        cellY: 1,
                        dungeon: new DungeonFile(),
                        out var partyRetry)
                    && partyRetry.Reason == "insufficient_fatigue"
                    && partyService.Load(5101).Used == 0,
                    ref failures);

                var shared = new DungeonInstance(1, 0);
                var leaderRun = new DungeonRun(
                    shared,
                    11,
                    1,
                    DungeonRunState.Active);
                var memberRun = new DungeonRun(
                    shared,
                    12,
                    1,
                    DungeonRunState.Active);
                SeedAccount(database, 521, 5201);
                var sharedService = new CharacterFatigueService(database);
                var sharedTargets = new[] { new CharacterFatigueTarget(5201, 0) };
                Check(
                    "leader first visit consumes for a shared dungeon instance",
                    sharedService.TryConsumeRoomVisit(
                        leaderRun,
                        sharedTargets,
                        mazeIndex: 1,
                        cellX: 0,
                        cellY: 0,
                        dungeon: new DungeonFile(),
                        out var leaderVisit)
                    && leaderVisit.Cost == 1
                    && sharedService.Load(5201).Used == 1,
                    ref failures);
                Check(
                    "party follower START_MAP of the same cell does not double-charge",
                    sharedService.TryConsumeRoomVisit(
                        memberRun,
                        sharedTargets,
                        mazeIndex: 1,
                        cellX: 0,
                        cellY: 0,
                        dungeon: new DungeonFile(),
                        out var followerVisit)
                    && followerVisit.Reason == "revisit"
                    && followerVisit.Cost == 0
                    && sharedService.Load(5201).Used == 1,
                    ref failures);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        private static void VerifyV26ToCurrentMigration(ref int failures)
        {
            var databasePath = TempDbPath("migration");
            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = database.OpenConnection())
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
ALTER TABLE characters DROP COLUMN usedFatigue;
ALTER TABLE characters DROP COLUMN maxFatigue;
ALTER TABLE characters DROP COLUMN fatigue;
UPDATE schema_metadata SET schema_version = 26
WHERE singleton_id = 1;
PRAGMA user_version = 26;";
                        command.ExecuteNonQuery();
                    }

                    Check(
                        "v26 fixture dropped fatigue columns",
                        !ColumnExists(connection, "characters", "usedFatigue")
                        && SqliteMigrations.ReadVersion(connection) == 26,
                        ref failures);

                    SqliteMigrations.Apply(connection);
                    Check(
                        "schema v26 migrates fatigue columns to current",
                        SqliteMigrations.ReadVersion(connection)
                            == SqliteMigrations.CurrentVersion
                        && ColumnExists(connection, "characters", "usedFatigue")
                        && ColumnExists(connection, "characters", "maxFatigue")
                        && ColumnExists(connection, "characters", "fatigue"),
                        ref failures);
                }
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        private static bool RunAccountReset(
            DailyResetService dailyReset,
            CharacterFatigueService fatigue,
            GameDatabase database,
            int accountId,
            DateTime utcNow,
            out bool applied)
        {
            applied = false;
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var ok = dailyReset.TryRunAccountFirstLoginReset(
                    connection,
                    transaction,
                    accountId,
                    utcNow,
                    (conn, tx) => fatigue.ResetUsedForAccount(conn, tx, accountId),
                    out applied);
                if (ok)
                    transaction.Commit();
                else
                    transaction.Rollback();
                return ok;
            }
        }

        private static void SeedAccount(
            GameDatabase database,
            int accountId,
            int characterId)
        {
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                InsertAccount(connection, transaction, accountId);
                InsertCharacter(connection, transaction, accountId, characterId);
                transaction.Commit();
            }
        }

        private static void SeedCharacter(
            GameDatabase database,
            int accountId,
            int characterId)
        {
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                InsertCharacter(connection, transaction, accountId, characterId);
                transaction.Commit();
            }
        }

        private static void InsertAccount(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(@aid, @mid, '');";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue(
                    "@mid",
                    "fatigue-" + accountId.ToString(CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
            }
        }

        private static void InsertCharacter(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int characterId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO characters(character_id, account_id, name, job)
VALUES(@cid, @aid, @name, 0);";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue(
                    "@name",
                    "fatigue-" + characterId.ToString(CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
            }
        }

        private static bool ColumnExists(
            SqliteConnection connection,
            string tableName,
            string columnName)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA table_info({tableName});";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (string.Equals(
                                reader.GetString(1),
                                columnName,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static string TempDbPath(string purpose)
            => Path.Combine(
                Path.GetTempPath(),
                $"dfo_character_fatigue_{purpose}_{Guid.NewGuid():N}.db");

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                if (File.Exists(path + "-wal"))
                    File.Delete(path + "-wal");
                if (File.Exists(path + "-shm"))
                    File.Delete(path + "-shm");
            }
            catch
            {
            }
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
