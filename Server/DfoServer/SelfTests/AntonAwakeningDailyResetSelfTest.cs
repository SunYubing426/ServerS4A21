using DfoServer.Game.CharacterData;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    /// <summary>
    /// Anton_Awakening 每日重置行为测试：
    /// 1) 通关黑色火山 (247) 后 243-247 全部被锁住（DELETE + limit 扣减）
    /// 2) 跨天 06:00 重置只清空 Anton_Awakening 行，不影响其他副本
    /// </summary>
    public static class AntonAwakeningDailyResetSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== ANTON_AWAKENING_DAILY_RESET selftest ===");
            var failures = 0;
            VerifyFinalDungeonLocksAll(ref failures);
            VerifyCrossDayResetClearsOnlyAntonAwakening(ref failures);
            VerifyLimitConsumeAfterClear(ref failures);
            VerifyClearLeavesOtherDungeonsIntact(ref failures);
            Console.WriteLine(
                failures == 0
                    ? "ANTON_AWAKENING_DAILY_RESET selftest passed."
                    : $"ANTON_AWAKENING_DAILY_RESET selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }

        private static void VerifyFinalDungeonLocksAll(ref int failures)
        {
            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"dfo_anton_awakening_{Guid.NewGuid():N}.db");
            try
            {
                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                var repository = new SqliteCharacterStateRepository(database);
                const int accountId = 57100;
                const int characterId = 57101;
                SeedAccount(database, accountId, "anton-awakening-a");
                SeedCharacter(database, characterId, accountId, "anton-awakening-c");

                // 前置：写入 243-246 的 unlocked + 247 completed
                repository.UpsertDungeonPermission(characterId, 243, 1);
                repository.UpsertDungeonPermission(characterId, 244, 1);
                repository.UpsertDungeonPermission(characterId, 245, 1);
                repository.UpsertDungeonPermission(characterId, 246, 1);
                repository.UpsertDungeonPermission(characterId, 247, 2);

                var preCount = repository.LoadDungeonPermissions(characterId)
                    .Count(e => e.DungeonId >= 243 && e.DungeonId <= 247);
                Check("pre: 243-247 all unlocked/completed", preCount == 5, ref failures);

                // 模拟通关 247：清空 243-247 行
                var deleted = repository.DeleteDungeonPermissions(
                    characterId,
                    new[] { 243, 244, 245, 246, 247 });
                Check("cleared exactly 5 rows", deleted == 5, ref failures);

                var postCount = repository.LoadDungeonPermissions(characterId)
                    .Count(e => e.DungeonId >= 243 && e.DungeonId <= 247);
                Check("post: 243-247 all cleared", postCount == 0, ref failures);
            }
            finally
            {
                TryDelete(tempDbPath);
                TryDelete(tempDbPath + "-wal");
                TryDelete(tempDbPath + "-shm");
            }
        }

        private static void VerifyCrossDayResetClearsOnlyAntonAwakening(ref int failures)
        {
            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"dfo_anton_awakening_cross_{Guid.NewGuid():N}.db");
            try
            {
                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                var repository = new SqliteCharacterStateRepository(database);
                var dailyReset = new DailyResetService(database);
                const int accountId = 57110;
                const int characterId = 57111;
                SeedAccount(database, accountId, "anton-awakening-cross-a");
                SeedCharacter(database, characterId, accountId, "anton-awakening-cross-c");

                // 写入 Anton_Awakening 全部 5 行
                foreach (var dungeonId in new[] { 243, 244, 245, 246, 247 })
                {
                    repository.UpsertDungeonPermission(characterId, dungeonId, 2);
                }
                // 写入其他副本（不应被重置）
                repository.UpsertDungeonPermission(characterId, 100, 1);
                repository.UpsertDungeonPermission(characterId, 500, 1);

                var preCount = repository.LoadDungeonPermissions(characterId).Count;
                Check("pre: 7 total permissions", preCount == 7, ref failures);

                // 模拟跨天：调用 DailyResetService.TryRunAccountFirstLoginReset
                using (var conn = new SqliteConnection(database.ConnectionString))
                {
                    conn.Open();
                    using (var tx = conn.BeginTransaction(deferred: false))
                    {
                        bool applied;
                        var ok = dailyReset.TryRunAccountFirstLoginReset(
                            conn,
                            tx,
                            accountId,
                            DateTime.UtcNow,
                            resetAction: (c, t) =>
                            {
                                using (var cmd = c.CreateCommand())
                                {
                                    cmd.Transaction = t;
                                    cmd.CommandText = @"
DELETE FROM character_dungeon_permissions
WHERE dungeon_id IN (243, 244, 245, 246, 247)
  AND character_id IN (
      SELECT character_id FROM characters WHERE account_id = @aid
  );";
                                    cmd.Parameters.AddWithValue("@aid", accountId);
                                    cmd.ExecuteNonQuery();
                                }
                                return true;
                            },
                            out applied);
                        Check("cross-day reset succeeded", ok, ref failures);
                        Check("reset action applied", applied, ref failures);
                        tx.Commit();
                    }
                }

                // 验证：243-247 被清空，100 和 500 保留
                var post = repository.LoadDungeonPermissions(characterId)
                    .Select(e => e.DungeonId)
                    .ToList();
                Check("post: 100 retained", post.Contains((ushort)100), ref failures);
                Check("post: 500 retained", post.Contains((ushort)500), ref failures);
                Check("post: 243 cleared", !post.Contains((ushort)243), ref failures);
                Check("post: 247 cleared", !post.Contains((ushort)247), ref failures);
                Check("post: only 2 permissions remain", post.Count == 2, ref failures);
            }
            finally
            {
                TryDelete(tempDbPath);
                TryDelete(tempDbPath + "-wal");
                TryDelete(tempDbPath + "-shm");
            }
        }

        private static void VerifyLimitConsumeAfterClear(ref int failures)
        {
            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"dfo_anton_awakening_limit_{Guid.NewGuid():N}.db");
            try
            {
                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                var entryLimits = new DungeonEntryLimitService(database);
                const int accountId = 57120;
                const int characterId = 57121;
                SeedAccount(database, accountId, "anton-awakening-limit-a");
                SeedCharacter(database, characterId, accountId, "anton-awakening-limit-c");

                // 验证前置：243 limit_count=1
                var preSnapshot = entryLimits.LoadSpecialDungeonLimits(accountId, characterId)
                    .FirstOrDefault(s => s.DungeonId == 243);
                Check("pre: limit config exists for 243", preSnapshot != null, ref failures);
                if (preSnapshot != null)
                {
                    Check("pre: limit count == 1", preSnapshot.LimitCount == 1, ref failures);
                    Check("pre: current count == 1", preSnapshot.CurrentCount == 1, ref failures);
                }

                // 模拟通关 247：扣减 243
                entryLimits.TryConsumeSpecialDungeonLimit(accountId, characterId, 243, 1, out var consumeResult);
                Check("243 limit consumed", consumeResult.IsLimited, ref failures);

                // 再尝试消费：应被拒绝
                entryLimits.TryCheckSpecialDungeonLimit(accountId, characterId, 243, 1, out var checkResult);
                Check("243 limit exhausted", !checkResult.Allowed, ref failures);
            }
            finally
            {
                TryDelete(tempDbPath);
                TryDelete(tempDbPath + "-wal");
                TryDelete(tempDbPath + "-shm");
            }
        }

        private static void VerifyClearLeavesOtherDungeonsIntact(ref int failures)
        {
            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"dfo_anton_awakening_other_{Guid.NewGuid():N}.db");
            try
            {
                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                var repository = new SqliteCharacterStateRepository(database);
                const int accountId = 57130;
                const int characterId = 57131;
                SeedAccount(database, accountId, "anton-awakening-other-a");
                SeedCharacter(database, characterId, accountId, "anton-awakening-other-c");

                // 写入 Anton_Awakening 和 其他副本
                repository.UpsertDungeonPermission(characterId, 247, 2);
                repository.UpsertDungeonPermission(characterId, 100, 1);
                repository.UpsertDungeonPermission(characterId, 225, 1);  // Anton_Normal
                repository.UpsertDungeonPermission(characterId, 234, 1);  // Anton_Quest

                // 只清空 Anton_Awakening
                var deleted = repository.DeleteDungeonPermissions(
                    characterId,
                    new[] { 243, 244, 245, 246, 247 });
                Check("only anton_awakening deleted", deleted == 1, ref failures);

                var post = repository.LoadDungeonPermissions(characterId)
                    .Select(e => e.DungeonId)
                    .ToHashSet();
                Check("post: 100 retained", post.Contains((ushort)100), ref failures);
                Check("post: 225 (Anton_Normal) retained", post.Contains((ushort)225), ref failures);
                Check("post: 234 (Anton_Quest) retained", post.Contains((ushort)234), ref failures);
                Check("post: 247 cleared", !post.Contains((ushort)247), ref failures);
            }
            finally
            {
                TryDelete(tempDbPath);
                TryDelete(tempDbPath + "-wal");
                TryDelete(tempDbPath + "-shm");
            }
        }

        private static void SeedAccount(IGameDatabase database, int accountId, string mid)
        {
            using (var connection = database.OpenConnection())
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @mid, '');";
                    command.Parameters.AddWithValue("@aid", accountId);
                    command.Parameters.AddWithValue("@mid", mid);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void SeedCharacter(IGameDatabase database, int characterId, int accountId, string name)
        {
            using (var connection = database.OpenConnection())
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO characters (character_id, account_id, name, job)
VALUES (@cid, @aid, @name, 0);";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@aid", accountId);
                    command.Parameters.AddWithValue("@name", name);
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
                // 忽略文件锁错误
            }
        }
    }
}