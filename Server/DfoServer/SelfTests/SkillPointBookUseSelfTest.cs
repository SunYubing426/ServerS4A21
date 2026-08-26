using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class SkillPointBookUseSelfTest
    {
        private const int AccountId = 93601;
        private const int CharacterId = 93602;

        public static int Run()
        {
            Console.WriteLine("=== SKILL_POINT_BOOK_USE selftest ===");
            var failures = 0;
            VerifyDefinitions(ref failures);
            VerifyEventBookClientProjection(ref failures);
            VerifyIncreaseStatusSuccessAck(ref failures);
            VerifyAtomicUseAndPersistence(ref failures);

            Console.WriteLine(
                failures == 0
                    ? "SKILL_POINT_BOOK_USE selftest passed."
                    : $"SKILL_POINT_BOOK_USE selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyIncreaseStatusSuccessAck(ref int failures)
        {
            var ack = IncreaseStatusAckBuilder.BuildExperienceSuccess(0x1234);
            Check(
                "INCREASE_STATUS skill-book route returns the accepted A21 success body",
                ack != null
                && ack.Length == IncreaseStatusAckBuilder.SuccessBodyLength
                && ack[0] == 0x01
                && ack[1] == 0x34
                && ack[2] == 0x12,
                ref failures);
        }

        private static void VerifyEventBookClientProjection(ref int failures)
        {
            var eventBook = ItemCore.Create(
                ItemCore.KindConsumable,
                SkillPointBookUseService.EventSpPlusFiveItemTemplateId);
            eventBook.Count = 1;
            var writer = new GamePacketWriter();
            ItemListProtocolWriter.WriteCommonEntry84(writer, 6, eventBook);
            var entry = writer.ToArray();

            Check(
                "event SP+5 book keeps ID 80003 on the server but projects native ID 1031 to A21",
                eventBook.ItemId == SkillPointBookUseService.EventSpPlusFiveItemTemplateId
                && entry.Length == ItemListProtocolWriter.CommonEntrySize
                && BitConverter.ToInt32(entry, 2)
                    == SkillPointBookUseService.SpPlusFiveItemTemplateId,
                ref failures);
        }

        private static void VerifyDefinitions(ref int failures)
        {
            Check(
                "five requested books resolve to their SP/TP grants",
                HasGrant(SkillPointBookUseService.TpPlusOneItemTemplateId, 0, 1)
                && HasGrant(SkillPointBookUseService.TpPlusFiveItemTemplateId, 0, 5)
                && HasGrant(SkillPointBookUseService.SpPlusFiveItemTemplateId, 5, 0)
                && HasGrant(SkillPointBookUseService.SpPlusTwentyItemTemplateId, 20, 0)
                && HasGrant(SkillPointBookUseService.EventSpPlusFiveItemTemplateId, 5, 0),
                ref failures);
            Check(
                "unrelated stackable is not claimed by skill-point-book handler",
                !SkillPointBookUseService.TryResolve(999999, out _),
                ref failures);
        }

        private static void VerifyAtomicUseAndPersistence(ref int failures)
        {
            var dbPath = Path.Combine(
                Path.GetTempPath(),
                "s4a21-skill-point-book-" + Guid.NewGuid().ToString("N") + ".db");
            var sessionId = Guid.NewGuid();
            InventoryLease lease = null;
            try
            {
                var database = CreateDatabase(dbPath);
                lease = RegisterInventory(database, sessionId);
                var itemIds = new[]
                {
                    SkillPointBookUseService.TpPlusOneItemTemplateId,
                    SkillPointBookUseService.TpPlusFiveItemTemplateId,
                    SkillPointBookUseService.SpPlusFiveItemTemplateId,
                    SkillPointBookUseService.SpPlusTwentyItemTemplateId,
                    SkillPointBookUseService.EventSpPlusFiveItemTemplateId,
                };
                var allSucceeded = true;
                for (short index = 0; index < itemIds.Length; index++)
                {
                    var result = SkillPointBookUseService.TryCommitUse(
                        lease,
                        InventoryListType.Main,
                        (short)(InventoryService.MainSlotStart + index),
                        itemIds[index]);
                    allSucceeded &= result.Handled
                        && result.Success
                        && result.ItemTemplateId == itemIds[index];
                }

                Check(
                    "all five books atomically grant points and consume one item",
                    allSucceeded
                    && ReadBonus(database, "bonus_sp") == 30
                    && ReadBonus(database, "bonus_tp") == 6
                    && ReadRemainingBookCount(database) == 0,
                    ref failures);
            }
            finally
            {
                if (lease != null)
                    InventoryContext.Unregister(sessionId, CharacterId);
                TryDeleteDatabase(dbPath);
            }
        }

        private static bool HasGrant(int itemId, int expectedSp, int expectedTp)
        {
            return SkillPointBookUseService.TryResolve(itemId, out var grant)
                && grant.Sp == expectedSp
                && grant.Tp == expectedTp;
        }

        private static GameDatabase CreateDatabase(string dbPath)
        {
            var database = new GameDatabase(dbPath, ServerPaths.SchemaFilePath);
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, 'skill-point-book', '');
INSERT INTO characters (character_id, account_id, name, job, grow_type, level)
VALUES (@cid, @aid, 'skill-point-book', 0, 0, 85);";
                command.Parameters.AddWithValue("@aid", AccountId);
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.ExecuteNonQuery();
            }

            return database;
        }

        private static InventoryLease RegisterInventory(GameDatabase database, Guid sessionId)
        {
            InventoryService inventory;
            using (var connection = database.OpenConnection())
                inventory = InventoryService.LoadFromDb(
                    connection,
                    CharacterId,
                    AccountId,
                    database);

            var itemIds = new[] { 1204, 1205, 1031, 1038, 80003 };
            for (short index = 0; index < itemIds.Length; index++)
            {
                var item = ItemCore.Create(ItemCore.KindConsumable, itemIds[index]);
                item.Count = 1;
                inventory.SetItem(
                    InventoryListType.Main,
                    (short)(InventoryService.MainSlotStart + index),
                    item);
            }

            var lease = InventoryContext.Register(sessionId, CharacterId, inventory);
            if (!OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "selftest-seed-skill-point-books"))
            {
                throw new InvalidOperationException("failed to seed skill-point-book inventory");
            }

            return lease;
        }

        private static int ReadBonus(GameDatabase database, string column)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT " + column + " FROM characters WHERE character_id = @cid";
                command.Parameters.AddWithValue("@cid", CharacterId);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private static int ReadRemainingBookCount(GameDatabase database)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    CharacterId,
                    AccountId,
                    database);
                return inventory.CountMainItem(1204)
                    + inventory.CountMainItem(1205)
                    + inventory.CountMainItem(1031)
                    + inventory.CountMainItem(1038)
                    + inventory.CountMainItem(80003);
            }
        }

        private static void TryDeleteDatabase(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + "-wal")) File.Delete(path + "-wal");
                if (File.Exists(path + "-shm")) File.Delete(path + "-shm");
            }
            catch
            {
            }
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
