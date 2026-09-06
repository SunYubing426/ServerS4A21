using System;
using System.IO;
using System.Linq;
using DfoServer.Game.Inventory;
using DfoServer.Game.Premium;
using DfoServer.Game.Vending;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Vending;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    internal static class BlackDiamondVendingSelfTest
    {
        internal static int Run()
        {
            var failures = 0;
            void Check(string name, bool ok)
            {
                Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] vending: {name}");
                if (!ok) failures++;
            }
            var databasePath = Path.Combine(Path.GetTempPath(), "dfo-vending-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var captured = new byte[] { 1, 0, 0, 0, 1, 0, 0, 0, 128, 0 };
                Check("captured 10B request", VendingMachineRequest.TryParse(captured, out var request)
                    && request.MachineId == 1 && request.GroupId == 1 && request.CoinSlot == 128);
                Check("reject short/extended/unsupported/group/negative-slot requests",
                    !VendingMachineRequest.TryParse(captured[..9], out _)
                    && !VendingMachineRequest.TryParse(captured.Concat(new byte[] { 0 }).ToArray(), out _)
                    && !VendingMachineRequest.TryParse(new byte[] { 2, 0, 0, 0, 1, 0, 0, 0, 128, 0 }, out _)
                    && !VendingMachineRequest.TryParse(new byte[] { 1, 0, 0, 0, 2, 0, 0, 0, 128, 0 }, out _)
                    && !VendingMachineRequest.TryParse(new byte[] { 1, 0, 0, 0, 1, 0, 0, 0, 255, 255 }, out _));
                Check("native failure codes carry no success payload",
                    VendingMachineAckBuilder.BuildFailure().SequenceEqual(new byte[] { 0, 1 })
                    && VendingMachineAckBuilder.BuildFailure(inventoryFull: true).SequenceEqual(new byte[] { 0, 4 }));
                Check("real PVF normal/high machine use distinct coins",
                    VendingMachineCatalog.TryGet(1, out var normal) && normal.CoinItemId == 7454
                    && VendingMachineCatalog.TryGet(3, out var advanced) && advanced.CoinItemId == 2749933);
                VendingMachineCatalog.TryGet(1, out normal);
                VendingMachineCatalog.TryGet(3, out advanced);
                var zero = normal.Prizes.First(prize => prize.Weight == 0);
                Check("weights keep duplicate entries, skip disabled entries and include last boundary",
                    normal.Pick(0) == normal.Prizes[0]
                    && normal.Pick(normal.TotalWeight - 1) == normal.Prizes.Last(prize => prize.Weight > 0)
                    && Enumerable.Range(0, normal.Prizes.Count).Where(i => normal.Prizes[i].Weight > 0)
                        .All(i => normal.Pick(normal.Prizes.Take(i).Sum(p => p.Weight)) != zero));
                try
                {
                    VendingMachineCatalog.Parse(1, "[item group][group num]1[material]7454 1[output]1 10 1[/output][/item group]");
                    Check("malformed prize quadruple rejected", false);
                }
                catch (InvalidDataException) { Check("malformed prize quadruple rejected", true); }

                var database = new GameDatabase(databasePath, ServerPaths.SchemaFilePath);
                Execute(database, "INSERT INTO accounts(account_id,m_id) VALUES (81,'vending-test'); INSERT INTO account_premiums(account_id,premium_type,end_time) VALUES (81,56,2000000000);");
                var nextCharacter = 8100;
                InventoryLease NewLease(int coinId = 7454, int count = 10)
                {
                    var characterId = ++nextCharacter;
                    Execute(database, $"INSERT INTO characters(character_id,account_id,name) VALUES ({characterId},81,CAST('vending{characterId}' AS BLOB));");
                    using var connection = database.OpenConnection();
                    var inventory = InventoryService.LoadFromDb(connection, characterId, 81, database);
                    if (!InventoryCreateService.TryCreateCore(coinId, ItemCreateReason.NpcShopPurchase, count, out var coin))
                        throw new InvalidOperationException("Cannot create PVF coin");
                    inventory.SetItem(InventoryListType.Main, 128, coin);
                    var lease = InventoryContext.Register(Guid.NewGuid(), inventory);
                    if (!InventoryPersistenceService.SaveDirty(lease)) throw new InvalidOperationException("Cannot seed coin");
                    return lease;
                }
                bool Draw(InventoryLease lease, VendingMachineDefinition definition, int ticket,
                    out VendingMachineResult result, out string reason)
                    => new VendingMachineService(_ => ticket).TryDraw(lease, lease.SessionId, 81,
                        new VendingDrawRequest(definition.MachineId, definition.GroupId, 128), definition, out result, out reason);
                int CoinCount(InventoryLease lease) => lease.Inventory.CountMainItem(7454) + lease.Inventory.CountMainItem(2749933);
                long PremiumEnd(int type) => Scalar(database, $"SELECT COALESCE(MAX(end_time),0) FROM account_premiums WHERE account_id=81 AND premium_type={type}");
                int Ticket(VendingMachineDefinition definition, int item)
                    => definition.Prizes.TakeWhile(prize => prize.ItemId != item || prize.Weight <= 0).Sum(prize => prize.Weight);

                var fixture = NewLease();
                try
                {
                    Check("ordinary item draw commits debit and reward", Draw(fixture, normal, 0, out var result, out var reason)
                        && CoinCount(fixture) == 9 && result.Prize.ItemId == 7463 && result.Prize.Count == 4);
                    if (result != null)
                    {
                        var ack = VendingMachineAckBuilder.BuildSuccess(result);
                        Check("ACK follows independent client read offsets and exact 101B item record",
                            ack.Length == 127 && ack[0] == 1 && BitConverter.ToInt16(ack, 1) == 128
                            && BitConverter.ToInt32(ack, 3) == 9 && BitConverter.ToInt32(ack, 7) == 7463
                            && BitConverter.ToInt32(ack, 11) == 4 && BitConverter.ToInt64(ack, 15) == 0
                            && BitConverter.ToUInt16(ack, 23) == 1 && ack[25] == 0
                            && BitConverter.ToInt32(ack, 28) == 7463 && BitConverter.ToInt32(ack, 32) == 4);
                    }
                    Check("repeat request pays again and sends absolute stack count", Draw(fixture, normal, 0, out result, out reason)
                        && CoinCount(fixture) == 8 && result.Rewards.Single().Core.Count == 8);
                    var revived = fixture.Inventory.GetMainVirtualCount(1)?.Count ?? 0;
                    Check("revive coins use persisted virtual wallet", Draw(fixture, normal, Ticket(normal, 1), out result, out reason)
                        && fixture.Inventory.GetMainVirtualCount(1).Count == revived + 1
                        && result.Rewards.Single().Slot == 1 && result.Rewards.Single().Core == null);
                    var oldEnd = PremiumEnd(22);
                    Check("contract grants native listType6 plus account duration in same transaction",
                        Draw(fixture, normal, Ticket(normal, 30), out result, out reason)
                        && PremiumEnd(22) >= Math.Max(oldEnd, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 2) + 86400
                        && result.PremiumType == 22 && result.PremiumRemaining >= 86398
                        && result.Rewards.Single().ListType == 6);
                    if (result != null)
                    {
                        var ack = VendingMachineAckBuilder.BuildSuccess(result);
                        Check("contract ACK is 34B not a fabricated inventory item", ack.Length == 34
                            && ack[25] == 6 && BitConverter.ToInt32(ack, 26) == 30 && BitConverter.ToInt32(ack, 30) == 1);
                    }
                    using (var connection = database.OpenConnection())
                    {
                        var loaded = InventoryService.LoadFromDb(connection, fixture.CharacterId, 81, database);
                        Check("relogin reads committed coin/item/wallet totals", loaded.GetItem(InventoryListType.Main, 128).Count == CoinCount(fixture)
                            && loaded.CountMainItem(7463) == 8 && loaded.GetMainVirtualCount(1).Count == revived + 1);
                    }
                    var before = CoinCount(fixture);
                    Check("wrong account/session cannot mutate inventory",
                        !new VendingMachineService(_ => 0).TryDraw(fixture, Guid.NewGuid(), 81, request, normal, out _, out _)
                        && !new VendingMachineService(_ => 0).TryDraw(fixture, fixture.SessionId, 82, request, normal, out _, out _)
                        && CoinCount(fixture) == before);
                    Check("advanced machine rejects ordinary coin", !Draw(fixture, advanced, 0, out result, out reason)
                        && CoinCount(fixture) == before && reason == "wrong-missing-or-expired-coin");
                    Execute(database, "UPDATE account_premiums SET premium_type=1 WHERE account_id=81 AND premium_type=56;");
                    Check("type1 is not black diamond and rejection does not debit", !Draw(fixture, normal, 0, out result, out reason)
                        && reason == "black-diamond-required" && result == null && CoinCount(fixture) == before);
                    Execute(database, "UPDATE account_premiums SET premium_type=56,end_time=1 WHERE account_id=81 AND premium_type=1;");
                    Check("expired black diamond is rejected", !Draw(fixture, normal, 0, out _, out reason) && reason == "black-diamond-required");
                    Execute(database, "UPDATE account_premiums SET end_time=2000000000 WHERE account_id=81 AND premium_type=56;");
                    var coin = fixture.Inventory.GetItem(InventoryListType.Main, 128).Copy();
                    coin.ExpireTime = 1;
                    fixture.Inventory.SetItem(InventoryListType.Main, 128, coin);
                    Check("expired coin is not consumed", !Draw(fixture, normal, 0, out _, out reason)
                        && reason == "wrong-missing-or-expired-coin" && CoinCount(fixture) == before);
                    coin.ExpireTime = 0;
                    fixture.Inventory.SetItem(InventoryListType.Main, 128, coin);
                    InventoryPersistenceService.SaveDirty(fixture);
                    var previousPremium = PremiumEnd(22);
                    Execute(database, "CREATE TRIGGER selftest_fail_vending BEFORE UPDATE ON character_inventory_items BEGIN SELECT RAISE(ABORT,'injected vending persistence failure'); END;");
                    Check("persistence failure rolls back coin and premium grant; result discarded",
                        !Draw(fixture, normal, Ticket(normal, 30), out result, out _)
                        && result == null && CoinCount(fixture) == before && PremiumEnd(22) == previousPremium);
                    Check("persistence failure rolls back newly inserted item and coin",
                        !Draw(fixture, normal, Ticket(normal, 7464), out result, out _)
                        && result == null && CoinCount(fixture) == before && fixture.Inventory.CountMainItem(7464) == 0);
                    Execute(database, "DROP TRIGGER selftest_fail_vending;");
                    var stale = fixture;
                    fixture = InventoryContext.Register(Guid.NewGuid(), fixture.Inventory);
                    Check("old lease cannot draw after replacement", !Draw(stale, normal, 0, out _, out _) && CoinCount(fixture) == before);
                }
                finally { InventoryContext.Unregister(fixture.SessionId); }

                var full = NewLease();
                try
                {
                    if (!InventoryCreateService.TryCreateCore(7464, ItemCreateReason.NpcShopPurchase, 1, out var filler)
                        || !ItemSlotBoundService.TryGetSlotRange(filler.ItemKind,
                            full.Inventory.GetListParam16(InventoryListType.Main), out var list, out var range))
                        throw new InvalidOperationException("Cannot resolve consumable slots");
                    for (var slot = range.Start; slot <= range.End; slot++)
                        full.Inventory.SetItem(list, (short)slot, filler.Copy());
                    for (var slot = ItemSlotBoundService.MainQuickSlotStart; slot <= ItemSlotBoundService.MainQuickSlotEnd; slot++)
                        full.Inventory.SetItem(list, (short)slot, filler.Copy());
                    var slotsFilled = range.End - range.Start + 1
                        + ItemSlotBoundService.MainQuickSlotEnd - ItemSlotBoundService.MainQuickSlotStart + 1;
                    Check("full reward container rolls back debit and preserves pre-existing dirty items",
                        !Draw(full, normal, 0, out var noResult, out var reason) && noResult == null
                        && reason == "reward-container-full:2" && CoinCount(full) == 10
                        && full.Inventory.CountMainItem(7464) == slotsFilled);
                    using (var capture = new A21DungeonDropItemSelfTest.LoopbackPacketCapture())
                    {
                        VendingMachineHandler.SendFailure(capture.Session, reason).GetAwaiter().GetResult();
                        var packets = capture.ReadPackets(1);
                        Check("capacity failure emits only the native vending ACK; no text notice or unrelated command",
                            packets.Count == 1 && packets[0].Length == 17 && packets[0][0] == 1
                            && BitConverter.ToUInt16(packets[0], 1) == VendingMachineHandler.CommandType
                            && packets[0][15] == 0 && packets[0][16] == 4);
                        VendingMachineHandler.SendFailure(capture.Session, "reward-container-full:3").GetAwaiter().GetResult();
                        var materialPackets = capture.ReadPackets(1);
                        Check("material capacity rejection also emits only the native vending failure",
                            materialPackets.Count == 1 && materialPackets[0].Length == 17
                            && materialPackets[0][0] == 1
                            && BitConverter.ToUInt16(materialPackets[0], 1) == VendingMachineHandler.CommandType
                            && materialPackets[0][15] == 0 && materialPackets[0][16] == 4);
                        VendingMachineHandler.SendFailure(capture.Session, "reward-grant-failed").GetAwaiter().GetResult();
                        packets = capture.ReadPackets(1);
                        Check("unclassified grant failure uses native generic error, not a fabricated full-bag cause",
                            packets.Count == 1 && packets[0].Length == 17
                            && packets[0][15] == 0 && packets[0][16] == 1);
                    }
                    Check("full container still accepts a stackable prize without discarding probability entries",
                        Draw(full, normal, Ticket(normal, 7464), out var stacked, out _)
                        && CoinCount(full) == 9
                        && full.Inventory.CountMainItem(7464) == slotsFilled + stacked.Prize.Count);
                    full.Inventory.SetItem(list, (short)range.Start, null);
                    Check("retry after clearing one consumable slot commits exactly one coin and actual prize",
                        Draw(full, normal, 0, out var retried, out _)
                        && CoinCount(full) == 8 && retried.Prize.ItemId == 7463
                        && full.Inventory.CountMainItem(7463) == retried.Prize.Count
                        && VendingMachineAckBuilder.BuildSuccess(retried)[0] == 1);
                    using var connection = database.OpenConnection();
                    var reloaded = InventoryService.LoadFromDb(connection, full.CharacterId, 81, database);
                    Check("capacity failure then successful retry survives relogin",
                        reloaded.CountMainItem(7454) == 8
                        && reloaded.CountMainItem(7463) == retried.Prize.Count);
                }
                finally { InventoryContext.Unregister(full.SessionId); }

                // 每个实际 PVF 正权重条目强制抽一次，覆盖契约、消耗品、材料、宠物蛋等真实创建路径。
                foreach (var definition in new[] { normal, advanced })
                {
                    var ticket = 0;
                    foreach (var prize in definition.Prizes)
                    {
                        if (prize.Weight == 0) continue;
                        var lease = NewLease(definition.CoinItemId, 1);
                        try
                        {
                            Check($"PVF machine={definition.MachineId} ticket={ticket} prize={prize.ItemId}x{prize.Count}",
                                Draw(lease, definition, ticket, out var result, out var reason)
                                && CoinCount(lease) == 0 && result.Prize == prize
                                && VendingMachineAckBuilder.BuildSuccess(result).Length >= 34);
                            if (reason != null) Console.WriteLine("  reason=" + reason);
                        }
                        finally { InventoryContext.Unregister(lease.SessionId); }
                        ticket += prize.Weight;
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("[FAIL] vending: " + ex); failures++; }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(databasePath + suffix)) File.Delete(databasePath + suffix);
            }
            Console.WriteLine($"=== BLACK_DIAMOND_VENDING {(failures == 0 ? "PASS" : "FAIL")} failures={failures} ===");
            return failures == 0 ? 0 : 1;
        }

        private static void Execute(IGameDatabase database, string sql)
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static long Scalar(IGameDatabase database, string sql)
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }
}
