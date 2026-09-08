using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    // 玩家交易(0x000A/0x000B/0x0013/0x0018 全链路)的物品层自测:
    //   - 物品/金币/虚拟计数交换的语义与落库原子性(失败回滚, 双方都不动)。
    //   - 过期报价(源在成交前变化)/重复源/受限物/金币不足的拒绝。
    //   - 0x000F 对方物品通知的 35B 定长字段布局。
    // 运行: DfoServer.exe --selftest-player-trade
    public static class PlayerTradeSelfTest
    {
        private const int FirstItemId = 10007331;
        private const int SecondItemId = 10007330;

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== PLAYER_TRADE selftest ===");
            TestItemAndGoldExchange();
            TestStaleOfferIsAtomic();
            TestDuplicateSourceIsAtomic();
            TestRestrictedItemRejected();
            TestInsufficientGoldRejected();
            TestVirtualItemExchange();
            TestPairPersistenceUsesNewInventory();
            TestPairPersistenceFailureIsAtomic();
            TestPeerItemNotificationLayout();
            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
            return _fail != 0 ? 1 : 0;
        }

        private static void TestItemAndGoldExchange()
        {
            var first = CreateInventory(980001, 980000, 1000);
            var second = CreateInventory(980002, 980003, 250);
            PutStack(first, 9, FirstItemId, 5);
            PutStack(second, 9, SecondItemId, 3);
            Check(
                "first item offer validates",
                PlayerTradeRuntimeService.TryCreateOffer(
                    first, 0, 9, 2, out var firstItem, out var failure));
            Check(
                "first gold offer validates",
                PlayerTradeRuntimeService.TryCreateOffer(
                    first, 0, 0, 300, out var firstGold, out failure));
            Check(
                "second item offer validates",
                PlayerTradeRuntimeService.TryCreateOffer(
                    second, 0, 9, 1, out var secondItem, out failure));
            Check(
                "second gold offer validates",
                PlayerTradeRuntimeService.TryCreateOffer(
                    second, 0, 0, 50, out var secondGold, out failure));
            var executed = PlayerTradeRuntimeService.TryApplyExchange(
                first,
                new[] { firstItem, firstGold },
                second,
                new[] { secondItem, secondGold },
                out var result);
            Check(
                "mixed item/gold exchange succeeds ("
                + (result.Failure ?? "none") + ")",
                executed && result.Success);
            Check(
                "first inventory receives item and net gold",
                first.CountMainItem(FirstItemId) == 3
                && first.CountMainItem(SecondItemId) == 1
                && Gold(first) == 750);
            Check(
                "second inventory receives item and net gold",
                second.CountMainItem(FirstItemId) == 2
                && second.CountMainItem(SecondItemId) == 2
                && Gold(second) == 500);
        }

        private static void TestStaleOfferIsAtomic()
        {
            var first = CreateInventory(980011, 980010, 100);
            var second = CreateInventory(980012, 980013, 200);
            PutStack(first, 9, FirstItemId, 4);
            Check(
                "stale-source fixture offer validates",
                PlayerTradeRuntimeService.TryCreateOffer(
                    first, 0, 9, 3, out var offer, out _));
            // 报价后源被第三方改小(4→2): 成交必须整体拒绝。
            PutStack(first, 9, FirstItemId, 2);
            var executed = PlayerTradeRuntimeService.TryApplyExchange(
                first,
                new[] { offer },
                second,
                Array.Empty<PlayerTradeOffer>(),
                out _);
            Check("stale source rejects whole exchange", !executed);
            Check(
                "stale rejection leaves both inventories unchanged",
                first.CountMainItem(FirstItemId) == 2
                && second.CountMainItem(FirstItemId) == 0
                && Gold(first) == 100
                && Gold(second) == 200);
        }

        private static void TestDuplicateSourceIsAtomic()
        {
            var first = CreateInventory(980021, 980020, 100);
            var second = CreateInventory(980022, 980023, 100);
            PutStack(first, 9, FirstItemId, 5);
            Check(
                "duplicate-source fixture offer validates",
                PlayerTradeRuntimeService.TryCreateOffer(
                    first, 0, 9, 1, out var offer, out _));
            if (offer != null)
            {
                var executed = PlayerTradeRuntimeService.TryApplyExchange(
                    first,
                    new[] { offer, offer.Copy() },
                    second,
                    Array.Empty<PlayerTradeOffer>(),
                    out _);
                Check("duplicate source rejects whole exchange", !executed);
                Check(
                    "duplicate rejection does not remove items",
                    first.CountMainItem(FirstItemId) == 5
                    && second.CountMainItem(FirstItemId) == 0);
            }
        }

        private static void TestRestrictedItemRejected()
        {
            var inventory = CreateInventory(980031, 980030, 0);
            var core = ItemCore.Create(2, FirstItemId);
            core.Count = 1;
            core.TradeRestriction = 1;
            inventory.AttachItem(InventoryListType.Main, 9, core);
            Check(
                "instance trade restriction rejects offer",
                !PlayerTradeRuntimeService.TryCreateOffer(
                    inventory, 0, 9, 1, out _, out _));
        }

        private static void TestInsufficientGoldRejected()
        {
            var inventory = CreateInventory(980041, 980040, 99);
            Check(
                "gold offer cannot exceed live balance",
                !PlayerTradeRuntimeService.TryCreateOffer(
                    inventory, 0, 0, 100, out _, out _));
        }

        private static void TestVirtualItemExchange()
        {
            var first = CreateInventory(980051, 980050, 0);
            var second = CreateInventory(980052, 980053, 0);
            first.AttachMainVirtualCount(354, 3037, 3000);
            second.AttachMainVirtualCount(354, 3037, 40);
            Check(
                "virtual cube offer validates",
                PlayerTradeRuntimeService.TryCreateOffer(
                    first, PlayerTradeRuntimeService.VirtualItemSpace,
                    354, 500, out var offer, out _));
            var executed = PlayerTradeRuntimeService.TryApplyExchange(
                first,
                new[] { offer },
                second,
                Array.Empty<PlayerTradeOffer>(),
                out var result);
            Check(
                "virtual cube exchange succeeds ("
                + (result.Failure ?? "none") + ")",
                executed && result.Success);
            var firstCount = first.GetMainVirtualCount(354);
            var secondCount = second.GetMainVirtualCount(354);
            Check(
                "virtual cube counts move between matching virtual slots",
                firstCount != null && firstCount.Count == 2500
                && secondCount != null && secondCount.Count == 540);
            Check(
                "virtual cube exchange records targeted inventory changes",
                result.FirstChanges.Slots.Count == 1
                && result.FirstChanges.Slots[0].SlotIndex == 354
                && result.SecondChanges.Slots.Count == 1
                && result.SecondChanges.Slots[0].SlotIndex == 354);
        }

        private static void TestPairPersistenceUsesNewInventory()
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "dfo-player-trade-" + Guid.NewGuid().ToString("N") + ".db");
            var previousPath = Environment.GetEnvironmentVariable(
                "INVENTORY_DATABASE_PATH");
            Environment.SetEnvironmentVariable(
                "INVENTORY_DATABASE_PATH",
                databasePath);
            try
            {
                SeedIdentity(
                    databasePath,
                    980061, 980060,
                    980062, 980063);
                var first = CreateInventory(980061, 980060, 1000);
                var second = CreateInventory(980062, 980063, 250);
                PutStack(first, 9, FirstItemId, 2);
                PutStack(second, 9, SecondItemId, 1);
                first.SetMainVirtualCount(0, 1001);
                first.SetMainVirtualCount(0, 1000);
                second.SetMainVirtualCount(0, 251);
                second.SetMainVirtualCount(0, 250);
                PlayerTradeRuntimeService.TryCreateOffer(
                    first, 0, 9, 1, out var firstItem, out _);
                PlayerTradeRuntimeService.TryCreateOffer(
                    first, 0, 0, 300, out var firstGold, out _);
                PlayerTradeRuntimeService.TryCreateOffer(
                    second, 0, 9, 1, out var secondItem, out _);
                var executed = PlayerTradeRuntimeService.TryExecuteAndPersist(
                    new InventoryLease(Guid.NewGuid(), 980061, first, 1L),
                    new[] { firstItem, firstGold },
                    new InventoryLease(Guid.NewGuid(), 980062, second, 2L),
                    new[] { secondItem },
                    out var result);
                var firstReloaded = LoadInventory(
                    databasePath, 980061, 980060);
                var secondReloaded = LoadInventory(
                    databasePath, 980062, 980063);
                Check(
                    "trade persists both leases through the current inventory tables",
                    executed && result.Success
                    && firstReloaded.CountMainItem(FirstItemId) == 1
                    && firstReloaded.CountMainItem(SecondItemId) == 1
                    && Gold(firstReloaded) == 700
                    && secondReloaded.CountMainItem(FirstItemId) == 1
                    && secondReloaded.CountMainItem(SecondItemId) == 0
                    && Gold(secondReloaded) == 550);
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    previousPath);
                SqliteConnection.ClearAllPools();
                DeleteDatabaseFiles(databasePath);
            }
        }

        private static void TestPairPersistenceFailureIsAtomic()
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "dfo-player-trade-failure-" + Guid.NewGuid().ToString("N") + ".db");
            var previousPath = Environment.GetEnvironmentVariable(
                "INVENTORY_DATABASE_PATH");
            Environment.SetEnvironmentVariable(
                "INVENTORY_DATABASE_PATH",
                databasePath);
            try
            {
                SeedIdentity(
                    databasePath,
                    980071, 980070,
                    980072, 980073);
                var first = CreateInventory(980071, 980070, 1000);
                var second = CreateInventory(980072, 980073, 250);
                PutStack(first, 9, FirstItemId, 1);
                PutStack(second, 9, SecondItemId, 1);
                var firstLease =
                    new InventoryLease(Guid.NewGuid(), 980071, first, 1L);
                var secondLease =
                    new InventoryLease(Guid.NewGuid(), 980072, second, 2L);
                Check(
                    "trade failure fixture persists initial inventories",
                    InventoryPersistenceService.SaveDirtyPair(
                        firstLease,
                        secondLease));
                PlayerTradeRuntimeService.TryCreateOffer(
                    first, 0, 9, 1, out var firstItem, out _);
                PlayerTradeRuntimeService.TryCreateOffer(
                    second, 0, 9, 1, out var secondItem, out _);
                // 用触发器让第二方任何物品写库都失败: 验证双方都回滚。
                CreateSecondOwnerSaveFailure(databasePath, 980072);
                var executed = PlayerTradeRuntimeService.TryExecuteAndPersist(
                    firstLease,
                    new[] { firstItem },
                    secondLease,
                    new[] { secondItem },
                    out _);
                var firstReloaded = LoadInventory(
                    databasePath, 980071, 980070);
                var secondReloaded = LoadInventory(
                    databasePath, 980072, 980073);
                Check(
                    $"pair persistence failure rolls back both database owners executed={executed} " +
                    $"db1={firstReloaded.CountMainItem(FirstItemId)}/{firstReloaded.CountMainItem(SecondItemId)} " +
                    $"db2={secondReloaded.CountMainItem(FirstItemId)}/{secondReloaded.CountMainItem(SecondItemId)}",
                    !executed
                    && firstReloaded.CountMainItem(FirstItemId) == 1
                    && firstReloaded.CountMainItem(SecondItemId) == 0
                    && secondReloaded.CountMainItem(FirstItemId) == 0
                    && secondReloaded.CountMainItem(SecondItemId) == 1);
                Check(
                    $"pair persistence failure restores both online inventories " +
                    $"live1={first.CountMainItem(FirstItemId)}/{first.CountMainItem(SecondItemId)} " +
                    $"live2={second.CountMainItem(FirstItemId)}/{second.CountMainItem(SecondItemId)}",
                    first.CountMainItem(FirstItemId) == 1
                    && first.CountMainItem(SecondItemId) == 0
                    && second.CountMainItem(FirstItemId) == 0
                    && second.CountMainItem(SecondItemId) == 1);
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    previousPath);
                SqliteConnection.ClearAllPools();
                DeleteDatabaseFiles(databasePath);
            }
        }

        private static void CreateSecondOwnerSaveFailure(
            string databasePath,
            int secondCharacterId)
        {
            using var connection = new SqliteConnection(
                SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $@"
CREATE TRIGGER player_trade_second_owner_update_abort
BEFORE UPDATE ON character_inventory_items
WHEN OLD.character_id = {secondCharacterId}
BEGIN
    SELECT RAISE(ABORT, 'player trade second owner save failure');
END;
CREATE TRIGGER player_trade_second_owner_insert_abort
BEFORE INSERT ON character_inventory_items
WHEN NEW.character_id = {secondCharacterId}
BEGIN
    SELECT RAISE(ABORT, 'player trade second owner save failure');
END;
CREATE TRIGGER player_trade_second_owner_delete_abort
BEFORE DELETE ON character_inventory_items
WHEN OLD.character_id = {secondCharacterId}
BEGIN
    SELECT RAISE(ABORT, 'player trade second owner save failure');
END;";
            command.ExecuteNonQuery();
        }

        private static void SeedIdentity(
            string databasePath,
            int firstCharacterId,
            int firstAccountId,
            int secondCharacterId,
            int secondAccountId)
        {
            using var connection = new SqliteConnection(
                SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@firstAccount, 'player-trade-first', ''),
       (@secondAccount, 'player-trade-second', '');
INSERT INTO characters (character_id, account_id, name)
VALUES (@firstCharacter, @firstAccount, 'player-trade-first'),
       (@secondCharacter, @secondAccount, 'player-trade-second');";
            command.Parameters.AddWithValue("@firstCharacter", firstCharacterId);
            command.Parameters.AddWithValue("@firstAccount", firstAccountId);
            command.Parameters.AddWithValue("@secondCharacter", secondCharacterId);
            command.Parameters.AddWithValue("@secondAccount", secondAccountId);
            command.ExecuteNonQuery();
        }

        private static InventoryService LoadInventory(
            string databasePath,
            int characterId,
            int accountId)
        {
            using var connection = new SqliteConnection(
                SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath));
            connection.Open();
            return InventoryService.LoadFromDb(
                connection,
                characterId,
                accountId);
        }

        private static void DeleteDatabaseFiles(string databasePath)
        {
            foreach (var path in new[]
            {
                databasePath,
                databasePath + "-wal",
                databasePath + "-shm",
            })
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        private static void TestPeerItemNotificationLayout()
        {
            var core = ItemCore.Create(1, 287454020);
            core.Value = 1432778632;
            core.Attr = 33;
            core.Durability = 12867;
            core.SealFlag = 4;
            core.EnchantCardId = 16909060;
            core.EnchantUpgradeCount = 5;
            core.AmplifyType = 6;
            core.AmplifyValue = 1800;
            core.ExpireTime = 270544960;
            core.GenuineUpgrade = 9;
            core.EmancipateEquipmentLevel = 10;
            core.TradeRestriction = 11;
            core.TailUnknown0 = 4627;
            core.TailUnknown1 = 14;
            core.TailUnknown2 = 15;
            core.TailUnknown3 = 16;
            core.RemainUseCount = 17;
            core.SortLockFlag = 1;
            var body = TradeItemChangeBodyBuilder.BuildItem(3, core);
            Check(
                "peer-item notification uses the compact 86JP trade entry",
                body.Length
                    == TradeItemChangeBodyBuilder.EmptyOrPlainItemLength);
            Check(
                "peer-item notification keeps verified common fields",
                BitConverter.ToInt16(body, 0) == 3
                && BitConverter.ToInt32(body, 2) == core.ItemId
                && BitConverter.ToInt32(body, 6) == core.Value
                && body[10] == core.Attr
                && BitConverter.ToUInt16(body, 11) == core.Durability
                && BitConverter.ToInt32(body, 13) == core.EnchantCardId
                && body[17] == core.EnchantUpgradeCount
                && body[18] == core.AmplifyType
                && BitConverter.ToUInt16(body, 19) == core.AmplifyValue
                && BitConverter.ToInt32(body, 21) == core.ExpireTime
                && body[25] == core.GenuineUpgrade
                && body[26] == core.EmancipateEquipmentLevel
                && body[27] == core.TradeRestriction
                && BitConverter.ToUInt16(body, 28) == core.TailUnknown0
                && body[30] == core.TailUnknown1
                && body[31] == core.TailUnknown2
                && body[32] == core.TailUnknown3
                && body[33] == core.RemainUseCount
                && body[34] == 1);
            var goldBody = TradeItemChangeBodyBuilder.BuildGold(0, 123456);
            var emptyBody = TradeItemChangeBodyBuilder.BuildEmpty(3);
            Check(
                "gold and removal notifications keep the same fixed tail",
                goldBody.Length
                    == TradeItemChangeBodyBuilder.EmptyOrPlainItemLength
                && BitConverter.ToInt32(goldBody, 2) == 0
                && BitConverter.ToInt32(goldBody, 6) == 123456
                && emptyBody.Length
                    == TradeItemChangeBodyBuilder.EmptyOrPlainItemLength
                && BitConverter.ToInt32(emptyBody, 2) == -1);
        }

        private static InventoryService CreateInventory(
            int characterId,
            int accountId,
            int gold)
        {
            var inventory = new InventoryService(characterId, accountId);
            inventory.SetListParam16(InventoryListType.Main, 24);
            inventory.AttachMainVirtualCount(0, 0, gold);
            inventory.ClearDirtyState();
            return inventory;
        }

        private static void PutStack(
            InventoryService inventory,
            short slot,
            int itemId,
            int count)
        {
            var core = ItemCore.Create(2, itemId);
            core.Count = count;
            inventory.SetItem(InventoryListType.Main, slot, core);
        }

        private static int Gold(InventoryService inventory)
        {
            return inventory.GetMainVirtualCount(0)?.Count ?? 0;
        }

        private static void Check(string name, bool ok)
        {
            if (ok)
            {
                _pass++;
                Console.WriteLine("[PASS] " + name);
            }
            else
            {
                _fail++;
                Console.WriteLine("[FAIL] " + name);
            }
        }
    }
}
