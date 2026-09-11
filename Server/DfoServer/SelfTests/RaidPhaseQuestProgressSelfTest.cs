using System;
using System.IO;
using System.Linq;
using DfoServer.Game.Quests;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    // 锁定 [raid phase clear] 的「阶段索引 → 触发器通道」映射，以及
    // 6742/6743/6744 的角色标志过滤。防止团本阶段完成再次记错通道。
    internal static class RaidPhaseQuestProgressSelfTest
    {
        private const int AccountId = 987701;
        private const int CharacterId = 987801;

        public static int Run()
        {
            Console.WriteLine("=== RAID_PHASE_QUEST_PROGRESS selftest ===");
            var failures = 0;
            var tempDirectory = Path.Combine(
                Path.GetTempPath(),
                $"dfo_raid_phase_quest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var databasePath = Path.Combine(tempDirectory, "raid_phase.db");
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                Seed(database.ConnectionString);

                // 12830: [0 5 -1 1 5 -1] → 阶段0=5, 阶段1=5
                // 8514 : [0 5 -1]        → 阶段0=5
                // 6742/6743/6744: [1 15 2/1/0] → 阶段1=15，带角色标志
                var quests = new[] { 12830, 8514, 6742, 6743, 6744 };
                SeedActiveQuests(database.ConnectionString, quests);

                Check(
                    "12830 init trigger packs both phases independently",
                    TriggerOf(database.ConnectionString, 12830) == 2565u,
                    ref failures);
                Check(
                    "8514 init trigger packs phase 0 only",
                    TriggerOf(database.ConnectionString, 8514) == 5u,
                    ref failures);
                // 6742/6743/6744 的 int data 是 [1 15 x]：次数必须落在通道 1
                Check(
                    "role-flagged raid quests pack their count into channel 1",
                    TriggerOf(database.ConnectionString, 6742) == (15u << 9)
                        && TriggerOf(database.ConnectionString, 6743) == (15u << 9)
                        && TriggerOf(database.ConnectionString, 6744) == (15u << 9),
                    ref failures);

                var service = new QuestService(database.ConnectionString);

                // 阶段 0 完成：只递减索引 0 那一组（ch0）
                service.SyncRaidPhaseClear(CharacterId, 0, -1);
                // 12830: ch0 5->4, ch1 保持 5 => 4 + (5<<9) = 2564
                Check(
                    "phase 0 clear decrements only channel 0 of 12830",
                    TriggerOf(database.ConnectionString, 12830) == 2564u,
                    ref failures);
                // 8514: ch0 5->4 => 4
                Check(
                    "phase 0 clear decrements 8514 channel 0",
                    TriggerOf(database.ConnectionString, 8514) == 4u,
                    ref failures);
                Check(
                    "phase 0 clear leaves role-flagged 6742 untouched",
                    TriggerOf(database.ConnectionString, 6742) == (15u << 9),
                    ref failures);

                // 阶段 1 完成且身份=小队长(1)：只递减 6743 与 12830 的 ch1
                service.SyncRaidPhaseClear(CharacterId, 1, 1);
                // 12830: ch0 保持 4, ch1 5->4 => 4 + (4<<9) = 2052
                Check(
                    "phase 1 clear decrements only channel 1 of 12830",
                    TriggerOf(database.ConnectionString, 12830) == 2052u,
                    ref failures);
                // 6743: ch0 15->14 => 14
                Check(
                    "phase 1 squad-leader clear decrements 6743",
                    TriggerOf(database.ConnectionString, 6743) == (14u << 9),
                    ref failures);
                Check(
                    "phase 1 squad-leader clear leaves 6742 (member) untouched",
                    TriggerOf(database.ConnectionString, 6742) == (15u << 9),
                    ref failures);
                Check(
                    "phase 1 squad-leader clear leaves 6744 (raid leader) untouched",
                    TriggerOf(database.ConnectionString, 6744) == (15u << 9),
                    ref failures);

                Console.WriteLine(failures == 0
                    ? "RAID_PHASE_QUEST_PROGRESS selftest passed"
                    : $"RAID_PHASE_QUEST_PROGRESS selftest failed: {failures}");
                return failures;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] selftest threw: {ex}");
                return 1;
            }
            finally
            {
                try { Directory.Delete(tempDirectory, true); } catch { }
            }
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            if (ok)
            {
                Console.WriteLine($"[PASS] {name}");
                return;
            }
            failures++;
            Console.WriteLine($"[FAIL] {name}");
        }

        // 调试用：把当前所有相关任务的触发器值打印出来（含通道分解）
        private static void DumpTriggers(string connectionString, string label)
        {
            Console.WriteLine($"  [dump] {label}");
            foreach (var questId in new[] { 12830, 8514, 6742, 6743, 6744 })
            {
                var v = TriggerOf(connectionString, questId);
                Console.WriteLine(
                    $"    quest={questId} raw={v} ch0={v & 0x1FF} ch1={(v >> 9) & 0x1FF}");
            }
        }

        private static uint TriggerOf(string connectionString, int questId)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT trigger_value FROM character_active_quests " +
                "WHERE character_id=@cid AND quest_id=@qid";
            command.Parameters.AddWithValue("@cid", CharacterId);
            command.Parameters.AddWithValue("@qid", questId);
            var value = command.ExecuteScalar();
            return value == null || value == DBNull.Value
                ? 0u
                : (uint)Convert.ToInt64(value);
        }

        private static void Seed(string connectionString)
        {
            using var connection = new SqliteConnection(connectionString);
            using var command = connection.CreateCommand();
            connection.Open();
            command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, 'raid-phase-quest-selftest', '');
INSERT INTO characters (character_id, account_id, name, level)
VALUES (@cid, @aid, 'raid-phase', 86);";
            command.Parameters.AddWithValue("@aid", AccountId);
            command.Parameters.AddWithValue("@cid", CharacterId);
            command.ExecuteNonQuery();
        }

        private static void SeedActiveQuests(
            string connectionString,
            int[] questIds)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            var slot = 0;
            foreach (var questId in questIds)
            {
                QuestRepository.InsertActiveQuest(
                    connection,
                    transaction,
                    CharacterId,
                    slot++,
                    (ushort)questId,
                    QuestData.GetInitTrigger(questId));
            }
            transaction.Commit();
        }
    }
}
