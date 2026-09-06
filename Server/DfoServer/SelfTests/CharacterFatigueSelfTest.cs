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
                    "select-character ACK writes persisted used and max fatigue",
                    SelectCharacterAckBodyBuilder.TryBuild(
                        snapshot,
                        database.ConnectionString,
                        out var body)
                    && body != null
                    && BitConverter.ToInt16(body, 11) == 0
                    && BitConverter.ToInt16(body, 13)
                        == CharacterFatigueService.DefaultMaxFatigue
                    && BitConverter.ToInt16(body, 15) == 33,
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
