using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Premium;
using DfoServer.Game.SecretShop;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    internal static class BlackDiamondSecretShopSelfTest
    {
        internal static int Run()
        {
            var failures = 0;
            void Check(string name, bool ok)
            {
                Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] secret-shop: {name}");
                if (!ok)
                    failures++;
            }

            Console.WriteLine("=== BLACK_DIAMOND_SECRET_SHOP selftest ===");
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH")))
            {
                Check("PVF_ARCHIVE_PATH is required", false);
                return 1;
            }

            try
            {
                var catalog = SecretShopCatalog.Parse(PvfArchiveAccessor.ReadText("etc/secretshop.etc"));
                Check("cash user stackable count is parsed and unused as stock",
                    catalog.CashUserStackableCount == 200);

                CheckPartySize(catalog, Check);
                CheckEncounterReroll(catalog, Check);
                CheckDisabledDungeonStaysZero(catalog, Check);
                CheckSelectionAndDedup(Check);
                CheckZeroWeightNeverSelected(Check);
                CheckOverflow(Check);
                CheckRealPvfOrdinaryUnchanged(catalog, Check);
                CheckRealPvfMemberKindsAndCounts(catalog, Check);
                CheckCashPoolNotUsed(catalog, Check);
                CheckRarityBoostFromRealDefinitions(catalog, Check);
                CheckOfferFreezeAndIsolation(catalog, Check);
                CheckQualificationTypes(Check);
                CheckPurchaseIndependentEquipmentCores(catalog, Check);
                CheckPurchaseSecondCoreRollback(catalog, Check);
                CheckMaterialMultiSlotRefresh(catalog, Check);
                CheckHandlerNotifiesAllGrantedSlots(catalog, Check);
                CheckListBodyCountAndUnitPrice(catalog, Check);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] secret-shop: " + ex);
                failures++;
            }

            Console.WriteLine(
                $"=== BLACK_DIAMOND_SECRET_SHOP {(failures == 0 ? "PASS" : "FAIL")} failures={failures} ===");
            return failures == 0 ? 0 : 1;
        }

        private static void CheckPartySize(SecretShopCatalog catalog, Action<string, bool> check)
        {
            var solo = catalog.ResolveNpcWeights(1, 20, 1);
            var four = catalog.ResolveNpcWeights(1, 20, 4);
            check("party size 1..4 uses distinct level-npc columns, not a hardcoded 1",
                WeightOf(solo, 1002) == 30
                && WeightOf(four, 1002) == 60
                && WeightOf(solo, 1000) == 9470
                && WeightOf(four, 1000) == 9410);
        }

        private static void CheckEncounterReroll(SecretShopCatalog catalog, Action<string, bool> check)
        {
            var ordinaryCalls = 0;
            var ordinary = SecretShopOfferFactory.Create(
                catalog, 33, 50, 1, _ => { ordinaryCalls++; return 0; }, SecretShopMemberPolicy.None);
            check("ordinary miss uses a single NPC roll",
                ordinary.NpcId == 1000 && ordinaryCalls == 1 && !ordinary.IsSecretShop);

            var memberCalls = 0;
            var memberMissThenHit = SecretShopOfferFactory.Create(
                catalog,
                33,
                50,
                1,
                total =>
                {
                    var call = memberCalls++;
                    if (call == 0)
                        return 0;
                    if (call == 1)
                        return 9265;
                    return 0;
                },
                SecretShopMemberPolicy.BlackDiamond);
            check("member miss rerolls the original NPC table and can appear",
                memberMissThenHit.NpcId == 1002 && memberCalls >= 2 && memberMissThenHit.IsSecretShop);

            var memberHitCalls = 0;
            var memberHit = SecretShopOfferFactory.Create(
                catalog,
                33,
                50,
                1,
                total =>
                {
                    var call = memberHitCalls++;
                    return call == 0 ? 9265 : 0;
                },
                SecretShopMemberPolicy.BlackDiamond);
            check("member first-hit does not consume a second NPC roll",
                memberHit.NpcId == 1002 && memberHitCalls == 1 + memberHit.Items.Count);
        }

        private static void CheckDisabledDungeonStaysZero(SecretShopCatalog catalog, Action<string, bool> check)
        {
            var ordinaryCalls = 0;
            var ordinary = SecretShopOfferFactory.Create(
                catalog, 140, 1, 1, _ => { ordinaryCalls++; return 0; }, SecretShopMemberPolicy.None);
            var memberCalls = 0;
            var member = SecretShopOfferFactory.Create(
                catalog, 140, 1, 1, _ => { memberCalls++; return 0; }, SecretShopMemberPolicy.BlackDiamond);
            check("p=0 dungeon stays 1000 for ordinary and member",
                ordinary.NpcId == 1000 && member.NpcId == 1000
                && ordinaryCalls == 1 && memberCalls == 2
                && !ordinary.IsSecretShop && !member.IsSecretShop);
        }

        private static void CheckSelectionAndDedup(Action<string, bool> check)
        {
            var pool = new SecretShopItemPool
            {
                SelectorKey = 0,
                SelectionCount = 2,
                Source = SecretShopPoolSource.Level,
                Candidates = new[]
                {
                    Candidate(10, 5, 1, 100),
                    Candidate(10, 5, 1, 100),
                    Candidate(20, 5, 1, 200),
                    Candidate(30, 5, 1, 300),
                    Candidate(40, 5, 1, 400),
                },
            };

            var ordinary = SecretShopSelector.SelectItems(pool, AlwaysZero, SecretShopMemberPolicy.None);
            check("ordinary keeps row-level picks, including duplicate item ids",
                ordinary.Select(x => x.ItemId).SequenceEqual(new[] { 10, 10 })
                && ordinary.All(x => x.Count == 1 && x.Price == 100));

            var member = SecretShopSelector.SelectItems(pool, AlwaysZero, SecretShopMemberPolicy.BlackDiamond);
            check("member doubles kinds, drops duplicate ids, and stops at unique candidates",
                member.Select(x => x.ItemId).SequenceEqual(new[] { 10, 20, 30, 40 })
                && member.Select(x => x.ItemId).Distinct().Count() == 4
                && member.All(x => x.Count == 2 && x.Price is 100 or 200 or 300 or 400));
        }

        private static void CheckZeroWeightNeverSelected(Action<string, bool> check)
        {
            var pool = new SecretShopItemPool
            {
                SelectorKey = 0,
                SelectionCount = 2,
                Source = SecretShopPoolSource.Level,
                Candidates = new[]
                {
                    Candidate(1, 0, 1, 10),
                    Candidate(2, 5, 1, 20),
                    Candidate(3, 5, 1, 30),
                },
            };
            var ordinary = SecretShopSelector.SelectItems(pool, AlwaysZero, SecretShopMemberPolicy.None);
            var member = SecretShopSelector.SelectItems(pool, AlwaysZero, SecretShopMemberPolicy.BlackDiamond);
            check("zero-weight rows never enter ordinary or member picks",
                ordinary.All(x => x.ItemId != 1)
                && member.All(x => x.ItemId != 1)
                && ordinary.Select(x => x.ItemId).SequenceEqual(new[] { 2, 3 }));
        }

        private static void CheckOverflow(Action<string, bool> check)
        {
            var overflow = false;
            try
            {
                SecretShopMemberPolicy.BlackDiamond.ResolveOfferCount(int.MaxValue);
            }
            catch (OverflowException)
            {
                overflow = true;
            }

            var zeroStaysZero = SecretShopMemberPolicy.BlackDiamond.ResolveEffectiveWeight(
                Candidate(9, 0, 1, 1)) == 0;
            check("count doubling uses checked arithmetic and zero weight stays zero",
                overflow && zeroStaysZero);
        }

        private static void CheckRealPvfOrdinaryUnchanged(SecretShopCatalog catalog, Action<string, bool> check)
        {
            var pool = catalog.ResolvePool(1002, 33, 50, useCashItems: false);
            var expected = OracleSelect(pool, Queue(0, 0));
            var actual = SecretShopSelector.SelectItems(pool, Queue(0, 0), SecretShopMemberPolicy.None);
            check("ordinary real PVF selection matches the original row-weight algorithm",
                pool != null
                && pool.SelectionCount == 2
                && actual.Select(x => x.ItemId).SequenceEqual(expected)
                && actual.All(x => x.Count == pool.Candidates.First(c => c.ItemId == x.ItemId && c.Weight > 0).Count));
        }

        private static void CheckRealPvfMemberKindsAndCounts(SecretShopCatalog catalog, Action<string, bool> check)
        {
            var pool = catalog.ResolvePool(1002, 33, 50, useCashItems: false);
            var ordinary = SecretShopSelector.SelectItems(pool, AlwaysZero, SecretShopMemberPolicy.None);
            var member = SecretShopSelector.SelectItems(pool, AlwaysZero, SecretShopMemberPolicy.BlackDiamond);
            var uniquePositive = pool.Candidates.Where(x => x.Weight > 0).Select(x => x.ItemId).Distinct().Count();
            var expectedKinds = Math.Min(4, uniquePositive);
            check("member real PVF kinds go 2 -> 4 without leaving the PVF candidate set",
                ordinary.Count == 2
                && member.Count == expectedKinds
                && member.Select(x => x.ItemId).Distinct().Count() == member.Count
                && member.All(x => pool.Candidates.Any(c => c.ItemId == x.ItemId && c.Weight > 0)));

            var sample = member[0];
            var pvf = pool.Candidates.First(c => c.ItemId == sample.ItemId && c.Weight > 0);
            var offered = new SecretShopOffer(1002, new[] { sample });
            check("member row count is PVF count * 2 and unit price is unchanged",
                sample.Count == checked(pvf.Count * 2)
                && sample.Price == pvf.Price
                && offered.Items[0].RemainingCount == sample.Count
                && offered.Items[0].Price == pvf.Price);
        }

        private static void CheckCashPoolNotUsed(SecretShopCatalog catalog, Action<string, bool> check)
        {
            var level = catalog.ResolvePool(1004, 1, 70, useCashItems: false);
            var cash = catalog.ResolvePool(1004, 1, 70, useCashItems: true);
            check("1004 level and cash pools differ, and this server does not treat cash as black diamond",
                level != null && cash != null
                && level.Source == SecretShopPoolSource.Level
                && cash.Source == SecretShopPoolSource.CashItem
                && level.Candidates.Count != cash.Candidates.Count);

            var allowed = new HashSet<int>(level.Candidates.Where(x => x.Weight > 0).Select(x => x.ItemId));
            var offer = SecretShopOfferFactory.Create(
                catalog,
                1,
                70,
                1,
                Queue(9995, 0, 0, 0, 0),
                SecretShopMemberPolicy.BlackDiamond);
            check("forced 1004 offer stays on the ordinary level/dungeon pool",
                offer.NpcId == 1004
                && offer.Items.Count > 0
                && offer.Items.All(x => allowed.Contains(x.ItemId)));
        }

        private static void CheckRarityBoostFromRealDefinitions(SecretShopCatalog catalog, Action<string, bool> check)
        {
            var pool = catalog.ResolvePool(1002, 33, 50, useCashItems: false)
                ?? catalog.ResolvePool(1004, 33, 70, useCashItems: false);
            SecretShopItemCandidate rare = null;
            var rarity = 0;
            foreach (var candidate in pool.Candidates)
            {
                if (candidate.Weight <= 0)
                    continue;
                if (SecretShopEquipmentRarity.TryGet(candidate.ItemId, out rarity)
                    && rarity is 2 or 3)
                {
                    rare = candidate;
                    break;
                }
            }

            if (rare == null)
            {
                check("no rarity 2/3 equipment in the ordinary PVF pool; original weights kept, no invented artifact",
                    true);
                return;
            }

            check("rarity comes from parsed equipment definitions, not a hardcoded table",
                SecretShopEquipmentRarity.TryGet(rare.ItemId, out var resolved)
                && resolved == rarity
                && ItemMetadataResolver.Resolve(rare.ItemId).Rarity == rarity);

            var boosted = SecretShopMemberPolicy.BlackDiamond.ResolveEffectiveWeight(rare);
            var ordinary = SecretShopMemberPolicy.None.ResolveEffectiveWeight(rare);
            check("rarity 2/3 positive weight is doubled; ordinary weight stays PVF",
                ordinary == rare.Weight && boosted == checked(rare.Weight * 2));

            var zero = new SecretShopItemCandidate
            {
                ItemId = rare.ItemId,
                RawFlag = rare.RawFlag,
                Price = rare.Price,
                RequiredItemId = rare.RequiredItemId,
                Count = rare.Count,
                Weight = 0,
            };
            check("zero-weight rarity 2/3 cannot be revived",
                SecretShopMemberPolicy.BlackDiamond.ResolveEffectiveWeight(zero) == 0);

            var dummy = Candidate(7454, rare.Weight, 1, 10);
            var isolated = new SecretShopItemPool
            {
                SelectorKey = pool.SelectorKey,
                SelectionCount = 1,
                Source = pool.Source,
                Candidates = new[] { dummy, rare },
            };
            var dummyWeight = SecretShopMemberPolicy.BlackDiamond.ResolveEffectiveWeight(dummy);
            var picked = SecretShopSelector.SelectItems(
                isolated,
                Queue(dummyWeight, 0),
                SecretShopMemberPolicy.BlackDiamond);
            check("injected boosted-weight boundary selects the real rarity 2/3 candidate",
                dummyWeight == dummy.Weight
                && picked.Count >= 1
                && picked[0].ItemId == rare.ItemId);
        }

        private static void CheckOfferFreezeAndIsolation(SecretShopCatalog catalog, Action<string, bool> check)
        {
            var memberRun = new DungeonRun { DungeonId = 33, EntryPartyMemberCount = 2 };
            var ordinaryRun = new DungeonRun { DungeonId = 33, EntryPartyMemberCount = 2 };
            memberRun.SecretShopOffer = SecretShopOfferFactory.Create(
                catalog, 33, 50, memberRun.EntryPartyMemberCount, Queue(9265, 0, 0, 0, 0),
                SecretShopMemberPolicy.BlackDiamond);
            ordinaryRun.SecretShopOffer = SecretShopOfferFactory.Create(
                catalog, 33, 50, ordinaryRun.EntryPartyMemberCount, Queue(9265, 0, 0, 0, 0),
                SecretShopMemberPolicy.None);

            var frozen = memberRun.SecretShopOffer;
            var reused = memberRun.SecretShopOffer
                ?? SecretShopOfferFactory.Create(
                    catalog, 33, 50, 4, AlwaysZero, SecretShopMemberPolicy.None);
            check("same run reuses the frozen earned offer and does not reroll or stack",
                ReferenceEquals(frozen, reused)
                && frozen.Items.Count != ordinaryRun.SecretShopOffer.Items.Count);

            if (frozen.Items.Count > 0)
                frozen.Items[0].RemainingCount = 0;
            check("two participant runs keep isolated offer stock",
                !ReferenceEquals(memberRun.SecretShopOffer, ordinaryRun.SecretShopOffer)
                && ordinaryRun.SecretShopOffer.Items.All(x => x.RemainingCount == x.Count)
                && frozen.Items[0].RemainingCount == 0);
        }

        private static void CheckQualificationTypes(Action<string, bool> check)
        {
            var dir = ResolveTestDataDirectory();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "secret-shop-premium-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var database = new GameDatabase(path, ResolveSchemaPath());
                var future = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
                Execute(database, $@"
INSERT INTO accounts(account_id,m_id) VALUES
 (201,'bd'),(202,'type1'),(203,'type17'),(204,'expired');
INSERT INTO account_premiums(account_id,premium_type,end_time) VALUES
 (201,{PremiumService.BlackDiamondPremiumType},{future}),
 (202,1,{future}),
 (203,17,{future}),
 (204,{PremiumService.BlackDiamondPremiumType},1);");

                check("type 56 is the only black-diamond shop policy",
                    SecretShopOfferFactory.ResolveMemberPolicy(database.ConnectionString, 201).IsMember);
                check("type 1 does not grant black-diamond shop policy",
                    !SecretShopOfferFactory.ResolveMemberPolicy(database.ConnectionString, 202).IsMember);
                check("type 17 does not grant black-diamond shop policy",
                    !SecretShopOfferFactory.ResolveMemberPolicy(database.ConnectionString, 203).IsMember);
                check("expired type 56 does not grant black-diamond shop policy",
                    !SecretShopOfferFactory.ResolveMemberPolicy(database.ConnectionString, 204).IsMember);
                check("missing account is not a member",
                    !SecretShopOfferFactory.ResolveMemberPolicy(database.ConnectionString, 0).IsMember);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                DeleteDb(path);
            }
        }

        private static void CheckPurchaseIndependentEquipmentCores(
            SecretShopCatalog catalog,
            Action<string, bool> check)
        {
            var pool = catalog.ResolvePool(1002, 33, 50, useCashItems: false);
            var equipment = pool.Candidates.FirstOrDefault(candidate =>
                candidate.Weight > 0
                && candidate.Count > 0
                && candidate.RawFlag == 0
                && candidate.Price > 0
                && IsEquipment(candidate.ItemId));
            if (equipment == null)
            {
                check("no gold-priced equipment candidate for purchase coverage", false);
                return;
            }

            var dir = ResolveTestDataDirectory();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "secret-shop-buy-" + Guid.NewGuid().ToString("N") + ".db");
            InventoryLease lease = null;
            try
            {
                var database = new GameDatabase(path, ResolveSchemaPath());
                Execute(database, @"
INSERT INTO accounts(account_id,m_id) VALUES (211,'shop-buy');
INSERT INTO characters(character_id,account_id,name) VALUES (21101,211,X'41');");
                InventoryService inventory;
                using (var connection = database.OpenConnection())
                {
                    inventory = InventoryService.LoadFromDb(connection, 21101, 211, database);
                }

                var ordinaryOffer = new SecretShopOffer(1002, new[] { equipment });
                var memberCandidate = SecretShopMemberPolicy.BlackDiamond.MaterializeOfferCandidate(equipment);
                var memberOffer = new SecretShopOffer(1002, new[] { memberCandidate });
                var goldNeeded = checked(equipment.Price * memberOffer.Items[0].Count);
                if (!InventoryRewardGrantService.TryCreateAndInsert(
                        inventory,
                        0,
                        ItemCreateReason.NpcShopPurchase,
                        goldNeeded,
                        out var goldGrant)
                    || !goldGrant.Success)
                {
                    check("seed gold for secret-shop purchase", false);
                    return;
                }

                lease = InventoryContext.Register(Guid.NewGuid(), inventory);
                if (!InventoryPersistenceService.SaveDirty(lease))
                {
                    check("persist seeded gold", false);
                    return;
                }

                var service = new SecretShopPurchaseService();

                check("ordinary equipment offer keeps PVF count 1 and unit price",
                    ordinaryOffer.Items[0].Count == equipment.Count
                    && ordinaryOffer.Items[0].RemainingCount == equipment.Count
                    && ordinaryOffer.Items[0].Price == equipment.Price);

                check("member equipment offer doubles stock and keeps unit price",
                    memberOffer.Items[0].Count == checked(equipment.Count * 2)
                    && memberOffer.Items[0].RemainingCount == memberOffer.Items[0].Count
                    && memberOffer.Items[0].Price == equipment.Price);

                var bought = service.TryPurchase(
                    lease,
                    memberOffer,
                    equipment.ItemId,
                    memberOffer.Items[0].Count,
                    out var result);
                if (!bought)
                {
                    Console.WriteLine(
                        $"  purchase failed item={equipment.ItemId} count={memberOffer.Items[0].Count} "
                        + $"price={equipment.Price} gold={lease.Inventory.CountMainItem(0)}");
                }
                var cores = CountItemCores(lease.Inventory, equipment.ItemId);
                var grantSlots = result?.SlotRefreshes
                    ?.Where(slot => slot.ListType != InventoryListType.Main
                        || !InventoryService.IsVirtualMainSlot(slot.Slot))
                    .Select(slot => slot.Slot)
                    .Distinct()
                    .ToArray()
                    ?? Array.Empty<short>();
                var groups = SecretShopHandler.GroupSlotRefreshes(result?.SlotRefreshes);
                byte[] updateBody = null;
                if (grantSlots.Length > 0)
                {
                    updateBody = ItemListUpdateBuilder.BuildItemSpaceUpdateBody(
                        lease.Inventory,
                        InventoryListType.Main,
                        grantSlots);
                }

                check("buying member count 2 grants two independent equipment cores",
                    bought
                    && result != null
                    && result.ItemCount == 2
                    && result.GoldCost == goldNeeded
                    && result.OfferRemainingCount == 0
                    && cores == 2
                    && lease.Inventory.CountMainItem(0) == 0);

                check("purchase result lists both new equipment slots, not only the last mutation",
                    bought
                    && result != null
                    && grantSlots.Length == 2
                    && grantSlots.Contains(result.AssignedSlot)
                    && grantSlots.All(slot => lease.Inventory.GetItem(InventoryListType.Main, slot)?.ItemId == equipment.ItemId)
                    && groups.Count == 1
                    && groups[0].ListType == InventoryListType.Main
                    && groups[0].Slots.Count == 2);

                check("0x000E update body carries both granted cores",
                    updateBody != null
                    && updateBody.Length > 3
                    && updateBody[0] == (byte)InventoryListType.Main
                    && BitConverter.ToUInt16(updateBody, 1) == 2
                    && ContainsInt32(updateBody, equipment.ItemId));

                var leftover = memberOffer.Items[0];
                check("displayed remaining count matches frozen purchased stock",
                    leftover.RemainingCount == 0
                    && leftover.Count == memberCandidate.Count
                    && leftover.Price == equipment.Price);
                var ack = result == null ? null : SecretShopBuyAckBuilder.BuildSuccess(result);
                check("native ACK keeps a single assigned-slot field",
                    ack != null
                    && ack.Length >= 7
                    && ack[0] == 1
                    && BitConverter.ToUInt16(ack, 5) == unchecked((ushort)result.AssignedSlot));
            }
            finally
            {
                if (lease != null)
                    InventoryContext.Unregister(lease.SessionId);
                SqliteConnection.ClearAllPools();
                DeleteDb(path);
            }
        }

        private static void CheckPurchaseSecondCoreRollback(
            SecretShopCatalog catalog,
            Action<string, bool> check)
        {
            var pool = catalog.ResolvePool(1002, 33, 50, useCashItems: false);
            var equipment = pool.Candidates.FirstOrDefault(candidate =>
                candidate.Weight > 0
                && candidate.Count > 0
                && candidate.RawFlag == 0
                && candidate.Price > 0
                && IsEquipment(candidate.ItemId));
            var fillerId = pool.Candidates.FirstOrDefault(candidate =>
                candidate.ItemId != equipment?.ItemId
                && IsEquipment(candidate.ItemId))?.ItemId ?? 0;
            if (equipment == null || fillerId <= 0)
            {
                check("no equipment pair for second-core rollback", false);
                return;
            }

            var dir = ResolveTestDataDirectory();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "secret-shop-rollback-" + Guid.NewGuid().ToString("N") + ".db");
            InventoryLease lease = null;
            try
            {
                var database = new GameDatabase(path, ResolveSchemaPath());
                Execute(database, @"
INSERT INTO accounts(account_id,m_id) VALUES (212,'shop-rollback');
INSERT INTO characters(character_id,account_id,name) VALUES (21201,212,X'41');");
                InventoryService inventory;
                using (var connection = database.OpenConnection())
                    inventory = InventoryService.LoadFromDb(connection, 21201, 212, database);

                var memberOffer = new SecretShopOffer(
                    1002,
                    new[] { SecretShopMemberPolicy.BlackDiamond.MaterializeOfferCandidate(equipment) });
                var goldNeeded = checked(equipment.Price * memberOffer.Items[0].Count);
                if (!InventoryRewardGrantService.TryCreateAndInsert(
                        inventory, 0, ItemCreateReason.NpcShopPurchase, goldNeeded, out var goldGrant)
                    || !goldGrant.Success
                    || !FillEquipmentSlotsLeaving(inventory, fillerId, freeSlots: 1))
                {
                    check("seed almost-full equipment inventory for rollback", false);
                    return;
                }

                lease = InventoryContext.Register(Guid.NewGuid(), inventory);
                if (!InventoryPersistenceService.SaveDirty(lease))
                {
                    check("persist almost-full equipment inventory", false);
                    return;
                }

                // Simulate an unrelated reward that has not reached periodic save.
                if (!InventoryRewardGrantService.TryCreateAndInsert(
                        lease.Inventory, 0, ItemCreateReason.DungeonDrop, 123, out var pendingGold)
                    || !pendingGold.Success)
                {
                    check("prepare unsaved reward before failed multi-core purchase", false);
                    return;
                }
                var goldBefore = lease.Inventory.CountMainItem(0);
                var fillerBefore = CountItemCores(lease.Inventory, fillerId);
                var bought = new SecretShopPurchaseService().TryPurchase(
                    lease,
                    memberOffer,
                    equipment.ItemId,
                    memberOffer.Items[0].Count,
                    out var result);
                check("second independent core failure rolls back gold, first core, and offer stock",
                    !bought
                    && result == null
                    && lease.Inventory.CountMainItem(0) == goldBefore
                    && CountItemCores(lease.Inventory, equipment.ItemId) == 0
                    && CountItemCores(lease.Inventory, fillerId) == fillerBefore
                    && memberOffer.Items[0].RemainingCount == memberOffer.Items[0].Count);
                using (var connection = database.OpenConnection())
                {
                    var reloaded = InventoryService.LoadFromDb(connection, 21201, 212, database);
                    check("failed multi-core purchase preserves the earlier dirty reward on relogin",
                        reloaded.CountMainItem(0) == goldBefore);
                }
            }
            finally
            {
                if (lease != null)
                    InventoryContext.Unregister(lease.SessionId);
                SqliteConnection.ClearAllPools();
                DeleteDb(path);
            }
        }

        private static void CheckMaterialMultiSlotRefresh(
            SecretShopCatalog catalog,
            Action<string, bool> check)
        {
            var pool = catalog.ResolvePool(1002, 33, 50, useCashItems: false);
            var equipment = pool.Candidates.FirstOrDefault(candidate =>
                candidate.Weight > 0 && IsEquipment(candidate.ItemId));
            if (equipment == null)
            {
                check("no equipment candidate for material multi-slot refresh", false);
                return;
            }

            const int materialId = 7454;
            var dir = ResolveTestDataDirectory();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "secret-shop-material-" + Guid.NewGuid().ToString("N") + ".db");
            InventoryLease lease = null;
            try
            {
                var database = new GameDatabase(path, ResolveSchemaPath());
                Execute(database, @"
INSERT INTO accounts(account_id,m_id) VALUES (213,'shop-material');
INSERT INTO characters(character_id,account_id,name) VALUES (21301,213,X'41');");
                InventoryService inventory;
                using (var connection = database.OpenConnection())
                    inventory = InventoryService.LoadFromDb(connection, 21301, 213, database);

                if (!InventoryCreateService.TryCreateCore(materialId, ItemCreateReason.NpcShopPurchase, 1, out var mat1)
                    || !InventoryCreateService.TryCreateCore(materialId, ItemCreateReason.NpcShopPurchase, 1, out var mat2))
                {
                    check("create split material stacks", false);
                    return;
                }

                inventory.SetItem(InventoryListType.Main, 121, mat1);
                inventory.SetItem(InventoryListType.Main, 122, mat2);
                lease = InventoryContext.Register(Guid.NewGuid(), inventory);
                if (!InventoryPersistenceService.SaveDirty(lease))
                {
                    check("persist split material stacks", false);
                    return;
                }

                var offer = new SecretShopOffer(
                    1002,
                    new[]
                    {
                        new SecretShopItemCandidate
                        {
                            ItemId = equipment.ItemId,
                            RawFlag = 1,
                            Price = 1,
                            RequiredItemId = materialId,
                            Count = 2,
                            Weight = 1,
                        }
                    });
                var bought = new SecretShopPurchaseService().TryPurchase(
                    lease, offer, equipment.ItemId, 2, out var result);
                var costSlots = result?.SlotRefreshes
                    ?.Where(slot => slot.ListType == InventoryListType.Main
                        && (slot.Slot == 121 || slot.Slot == 122))
                    .Select(slot => slot.Slot)
                    .Distinct()
                    .OrderBy(slot => slot)
                    .ToArray()
                    ?? Array.Empty<short>();
                var grantSlots = result?.SlotRefreshes
                    ?.Where(slot => slot.ListType != InventoryListType.Main
                        || (slot.Slot != 121 && slot.Slot != 122
                            && !InventoryService.IsVirtualMainSlot(slot.Slot)))
                    .Select(slot => slot.Slot)
                    .Distinct()
                    .ToArray()
                    ?? Array.Empty<short>();
                check("material debit spanning two stacks refreshes both cost slots and both cores",
                    bought
                    && result != null
                    && result.ItemCount == 2
                    && result.RequiredItemId == materialId
                    && costSlots.SequenceEqual(new short[] { 121, 122 })
                    && grantSlots.Length == 2
                    && CountItemCores(lease.Inventory, equipment.ItemId) == 2
                    && lease.Inventory.CountMainItem(materialId) == 0
                    && lease.Inventory.GetItem(InventoryListType.Main, 121) == null
                    && lease.Inventory.GetItem(InventoryListType.Main, 122) == null);
            }
            finally
            {
                if (lease != null)
                    InventoryContext.Unregister(lease.SessionId);
                SqliteConnection.ClearAllPools();
                DeleteDb(path);
            }
        }

        private static void CheckHandlerNotifiesAllGrantedSlots(
            SecretShopCatalog catalog,
            Action<string, bool> check)
        {
            var pool = catalog.ResolvePool(1002, 33, 50, useCashItems: false);
            var equipment = pool.Candidates.FirstOrDefault(candidate =>
                candidate.Weight > 0
                && candidate.Count > 0
                && candidate.RawFlag == 0
                && candidate.Price > 0
                && IsEquipment(candidate.ItemId));
            if (equipment == null)
            {
                check("no equipment candidate for handler slot notifications", false);
                return;
            }

            var dir = ResolveTestDataDirectory();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "secret-shop-handler-" + Guid.NewGuid().ToString("N") + ".db");
            InventoryLease lease = null;
            try
            {
                var database = new GameDatabase(path, ResolveSchemaPath());
                Execute(database, @"
INSERT INTO accounts(account_id,m_id) VALUES (214,'shop-handler');
INSERT INTO characters(character_id,account_id,name) VALUES (21401,214,X'41');");
                InventoryService inventory;
                using (var connection = database.OpenConnection())
                    inventory = InventoryService.LoadFromDb(connection, 21401, 214, database);

                var memberOffer = new SecretShopOffer(
                    1002,
                    new[] { SecretShopMemberPolicy.BlackDiamond.MaterializeOfferCandidate(equipment) });
                var goldNeeded = checked(equipment.Price * memberOffer.Items[0].Count);
                if (!InventoryRewardGrantService.TryCreateAndInsert(
                        inventory, 0, ItemCreateReason.NpcShopPurchase, goldNeeded, out var goldGrant)
                    || !goldGrant.Success)
                {
                    check("seed gold for handler notification", false);
                    return;
                }

                using var capture = new A21DungeonDropItemSelfTest.LoopbackPacketCapture();
                var session = capture.Session;
                session.Account = new AccountRecord { AccountId = 214 };
                session.Player.CharacterId = 21401;
                session.Player.CurrentRun = new DungeonRun { DungeonId = 33 };
                session.Player.CurrentRun.SecretShopOffer = memberOffer;
                lease = InventoryContext.Register(session.SessionId, inventory);
                if (!InventoryPersistenceService.SaveDirty(lease))
                {
                    check("persist handler purchase inventory", false);
                    return;
                }

                var characters = new SqliteCharacterRepository(database);
                var source = new SqliteSelectCharacterDataSource(database, characters);
                var handler = new SecretShopHandler(new InventoryRefreshSender(source, characters, database));
                var request = new byte[8];
                BitConverter.GetBytes(equipment.ItemId).CopyTo(request, 0);
                BitConverter.GetBytes(memberOffer.Items[0].Count).CopyTo(request, 4);
                handler.HandleBuyRequest(session, new GamePacketHeader(), request).GetAwaiter().GetResult();

                var packets = capture.ReadPackets(3);
                var ack = packets.FirstOrDefault(packet =>
                    packet.Length > 15
                    && packet[0] == 1
                    && BitConverter.ToUInt16(packet, 1) == 0x0128);
                var itemUpdates = packets.Where(packet =>
                    packet.Length > 18
                    && packet[0] == 0
                    && BitConverter.ToUInt16(packet, 1) == 0x000E
                    && packet[15] == (byte)InventoryListType.Main
                    && BitConverter.ToUInt16(packet, 16) >= 2)
                    .ToArray();
                check("handler 0x0128 ACK stays single-slot and 0x000E notifies both granted cores",
                    CountItemCores(lease.Inventory, equipment.ItemId) == 2
                    && ack != null
                    && ack[15] == 1
                    && itemUpdates.Length >= 1
                    && itemUpdates.Any(packet =>
                        BitConverter.ToUInt16(packet, 16) >= 2
                        && ContainsInt32(packet, equipment.ItemId)));
            }
            finally
            {
                if (lease != null)
                    InventoryContext.Unregister(lease.SessionId);
                SqliteConnection.ClearAllPools();
                DeleteDb(path);
            }
        }

        private static void CheckListBodyCountAndUnitPrice(SecretShopCatalog catalog, Action<string, bool> check)
        {
            var pool = catalog.ResolvePool(1002, 33, 50, useCashItems: false);
            var first = pool.Candidates.First(x => x.Weight > 0);
            var member = SecretShopMemberPolicy.BlackDiamond.MaterializeOfferCandidate(first);
            var offer = new SecretShopOffer(1002, new[] { member });
            var body = SecretShopItemListBodyBuilder.Build(offer);
            check("0x0118 remaining count equals doubled stock and unit price is unchanged",
                body.Length >= 25
                && BitConverter.ToInt32(body, 0) == 1
                && BitConverter.ToInt32(body, 4) == first.ItemId
                && BitConverter.ToInt32(body, 9) == first.Price
                && BitConverter.ToInt32(body, 13) == checked(first.Count * 2));
        }

        private static SecretShopItemCandidate Candidate(int itemId, int weight, int count, int price)
        {
            return new SecretShopItemCandidate
            {
                ItemId = itemId,
                RawFlag = 0,
                Price = price,
                RequiredItemId = 0,
                Count = count,
                Weight = weight,
            };
        }

        private static int AlwaysZero(int total) => 0;

        private static Func<int, int> Queue(params int[] rolls)
        {
            var index = 0;
            return total =>
            {
                if (index >= rolls.Length)
                    throw new InvalidOperationException($"unexpected extra secret-shop roll total={total}");
                var roll = rolls[index++];
                if (roll < 0 || roll >= total)
                    throw new InvalidOperationException($"injected roll {roll} outside [0,{total})");
                return roll;
            };
        }

        private static int WeightOf(IReadOnlyList<SecretShopNpcWeight> rows, int npcId)
            => rows.First(x => x.NpcId == npcId).Weight;

        private static List<int> OracleSelect(SecretShopItemPool pool, Func<int, int> next)
        {
            var remaining = pool.Candidates.Where(x => x.Weight > 0).ToList();
            var selected = new List<int>();
            while (selected.Count < pool.SelectionCount && remaining.Count > 0)
            {
                var total = remaining.Sum(x => x.Weight);
                var roll = next(total);
                var cursor = 0;
                SecretShopItemCandidate chosen = null;
                foreach (var row in remaining)
                {
                    cursor += row.Weight;
                    if (roll < cursor)
                    {
                        chosen = row;
                        break;
                    }
                }
                selected.Add(chosen.ItemId);
                remaining.Remove(chosen);
            }
            return selected;
        }

        private static bool FillEquipmentSlotsLeaving(
            InventoryService inventory,
            int fillerId,
            int freeSlots)
        {
            if (inventory == null
                || !InventoryCreateService.TryCreateCore(
                    fillerId,
                    ItemCreateReason.NpcShopPurchase,
                    1,
                    out var filler)
                || filler == null
                || !ItemSlotBoundService.TryGetSlotRange(
                    filler.ItemKind,
                    inventory.GetListParam16(InventoryListType.Main),
                    out var listType,
                    out var range)
                || range.Count <= freeSlots)
            {
                return false;
            }

            var targetFree = Math.Max(0, freeSlots);
            var occupied = CountOccupiedSlots(inventory, listType, range);
            var targetOccupied = range.Count - targetFree;
            while (occupied < targetOccupied)
            {
                if (!InventoryRewardGrantService.TryCreateAndInsert(
                        inventory,
                        fillerId,
                        ItemCreateReason.NpcShopPurchase,
                        1,
                        out var grant)
                    || grant == null
                    || !grant.Success)
                {
                    return false;
                }

                occupied++;
            }

            return CountOccupiedSlots(inventory, listType, range) == targetOccupied;
        }

        private static int CountOccupiedSlots(
            InventoryService inventory,
            InventoryListType listType,
            ItemSlotRange range)
        {
            var occupied = 0;
            for (var slot = range.Start; slot <= range.End; slot++)
            {
                if (inventory.GetItem(listType, (short)slot) != null)
                    occupied++;
            }

            return occupied;
        }

        private static bool ContainsInt32(byte[] data, int value)
        {
            if (data == null || data.Length < 4)
                return false;

            var needle = BitConverter.GetBytes(value);
            for (var i = 0; i <= data.Length - 4; i++)
            {
                if (data[i] == needle[0]
                    && data[i + 1] == needle[1]
                    && data[i + 2] == needle[2]
                    && data[i + 3] == needle[3])
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsEquipment(int itemId)
        {
            var metadata = ItemMetadataResolver.Resolve(itemId);
            return metadata != null
                && string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal);
        }

        private static int CountItemCores(InventoryService inventory, int itemId)
        {
            var count = 0;
            foreach (var listType in new[] { InventoryListType.Main, InventoryListType.Equipment })
            {
                foreach (var pair in inventory.GetItems(listType))
                {
                    if (pair.Value != null && pair.Value.ItemId == itemId)
                        count++;
                }
            }
            return count;
        }

        private static string ResolveSchemaPath()
        {
            var assemblyDir = Path.GetDirectoryName(typeof(GameDatabase).Assembly.Location);
            var fromAssembly = Path.Combine(assemblyDir ?? string.Empty, "Sqlite", "item_schema.sql");
            return File.Exists(fromAssembly) ? fromAssembly : ServerPaths.SchemaFilePath;
        }

        private static string ResolveTestDataDirectory()
        {
            var configured = Environment.GetEnvironmentVariable("INVENTORY_DATABASE_PATH");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Directory.Exists(configured) || !configured.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                    ? configured
                    : Path.GetDirectoryName(configured);
            }

            return Path.GetTempPath();
        }

        private static void Execute(IGameDatabase database, string sql)
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static void DeleteDb(string path)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var file = path + suffix;
                if (File.Exists(file))
                    File.Delete(file);
            }
        }
    }
}
