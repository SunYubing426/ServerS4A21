using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Premium;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Dungeon;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    internal static class BlackDiamondCardCoinSelfTest
    {
        private const int OrdinaryAccount = 781001;
        private const int MemberAccount = 781002;
        private const int ExpiredAccount = 781003;
        private const int Type1Account = 781004;
        private const int Type17Account = 781005;
        private const int OrdinaryCharacter = 781101;
        private const int MemberCharacter = 781102;
        private const int MemberCharacterB = 781103;
        private const int ExpiredCharacter = 781104;
        private const int Type1Character = 781105;
        private const int Type17Character = 781106;
        private const int FullBagCharacter = 781107;
        private const int PaidCharacter = 781108;
        private const int ReplayCharacter = 781109;
        private const int PairCharacter = 781110;
        private const int ExtraItemCharacter = 781111;
        private const int TimerCharacter = 781201;
        private const int PaidFirstCharacter = 781202;
        private const int FreeFirstCharacter = 781203;
        private const int PaidWaitCharacter = 781204;
        private const int RetryFlowCharacter = 781205;
        private const int OrdinaryFlowCharacter = 781206;
        private const int StaleTimerCharacter = 781207;
        private const int DiamondCoinItemId = 7454;
        private const int SecondaryItemId = 2749933;

        public static int Run()
        {
            Console.WriteLine("=== BLACK_DIAMOND_CARD_COIN selftest ===");
            var failures = 0;
            VerifyPoolParsing(ref failures);
            VerifyChanceAndBlankData(ref failures);
            VerifyPcRoomDefaultItemBranch(ref failures);
            VerifyEligibility(ref failures);
            VerifyPlayExclusion(ref failures);
            VerifyClearPacketFamilies(ref failures);
            VerifyGrantAndIsolation(ref failures);
            VerifyCoordinatorFlows(ref failures);
            Console.WriteLine(
                failures == 0
                    ? "BLACK_DIAMOND_CARD_COIN selftest PASS"
                    : "BLACK_DIAMOND_CARD_COIN selftest FAIL: " + failures);
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyPoolParsing(ref int failures)
        {
            var parsed = ClearRewardDefinitionCatalog.Parse(
                "[pcroom card blank item]\n50 7454 1\n[/pcroom card blank item]");
            Check(
                "parser reads the current PVF triple shape",
                parsed.PcRoomCardBlankItems.Count == 1
                    && parsed.PcRoomCardBlankItems[0].Weight == 50
                    && parsed.PcRoomCardBlankItems[0].ItemId == DiamondCoinItemId
                    && parsed.PcRoomCardBlankItems[0].Count == 1
                    && parsed.PcRoomCardBlankItems[0].IsActivatable,
                ref failures);

            if (!RequirePvf(ref failures, "real pcroom blank pool"))
                return;

            ClearRewardGenerator.WarmUp();
            var live = ClearRewardDefinitionCatalog.Current.PcRoomCardBlankItems;
            Check(
                "current PVF pool is 50,7454,1",
                live.Count == 1
                    && live[0].Weight == 50
                    && live[0].ItemId == DiamondCoinItemId
                    && live[0].Count == 1,
                ref failures);
        }

        private static void VerifyChanceAndBlankData(ref int failures)
        {
            var live = ClearRewardDefinitionCatalog.Parse(
                "[pcroom card blank item]\n50 7454 1\n[/pcroom card blank item]");
            Check(
                "chance 4 is a hit and picks 7454x1",
                BlackDiamondCardRules.Roll(true, new ScriptedRandom(4, 0).Next, live).HasItem
                    && BlackDiamondCardRules.Roll(true, new ScriptedRandom(4, 0).Next, live).ItemId
                        == DiamondCoinItemId
                    && BlackDiamondCardRules.Roll(true, new ScriptedRandom(4, 0).Next, live).StackCount
                        == 1,
                ref failures);
            Check(
                "coin miss without an item generator stays a frozen empty extra",
                !BlackDiamondCardRules.Roll(true, new ScriptedRandom(5).Next, live).HasItem
                    && BlackDiamondCardRules.Roll(true, new ScriptedRandom(5).Next, live).Frozen,
                ref failures);
            Check(
                "ineligible accounts skip the random source",
                !BlackDiamondCardRules.Roll(
                        false,
                        _ => throw new InvalidOperationException("rolled"),
                        live)
                    .HasItem,
                ref failures);

            var zeroWeight = ClearRewardDefinitionCatalog.Parse(
                "[pcroom card blank item]\n0 7454 1\n[/pcroom card blank item]");
            Check(
                "zero weight does not activate after a 5% hit",
                zeroWeight.PcRoomCardBlankItems.Count == 1
                    && !zeroWeight.PcRoomCardBlankItems[0].IsActivatable
                    && !BlackDiamondCardRules.Roll(
                            true,
                            new ScriptedRandom(0).Next,
                            zeroWeight)
                        .HasItem,
                ref failures);

            var bad = ClearRewardDefinitionCatalog.Parse(
                "[pcroom card blank item]\n-1 7454 1 50 0 1 50 7454 0 50 7454\n[/pcroom card blank item]");
            Check(
                "negative id/count/weight and leftover tokens stay fail-closed",
                bad.PcRoomCardBlankItems.Count == 3
                    && !bad.PcRoomCardBlankItems[0].IsActivatable
                    && !bad.PcRoomCardBlankItems[1].IsActivatable
                    && !bad.PcRoomCardBlankItems[2].IsActivatable
                    && !BlackDiamondCardRules.Roll(
                            true,
                            new ScriptedRandom(0).Next,
                            bad)
                        .HasItem,
                ref failures);

            var overflow = ClearRewardDefinitionCatalog.Parse(
                "[pcroom card blank item]\n2147483647 7454 1 1 7455 1\n[/pcroom card blank item]");
            Check(
                "weight overflow does not invent a fallback item",
                !BlackDiamondCardRules.Roll(
                        true,
                        new ScriptedRandom(0).Next,
                        overflow)
                    .HasItem,
                ref failures);

            var missing = ClearRewardDefinitionCatalog.Parse(string.Empty);
            Check(
                "missing blank section is a safe no-reward",
                missing.PcRoomCardBlankItems.Count == 0
                    && !BlackDiamondCardRules.Roll(
                            true,
                            new ScriptedRandom(0).Next,
                            missing)
                        .HasItem,
                ref failures);
        }

        private static void VerifyPcRoomDefaultItemBranch(ref int failures)
        {
            var emptyProfile = ClearRewardDefinitionCatalog.Parse(
                "[drop prob]\n`pcroom default` 1 200 0\n[/drop prob]\n" +
                "[pcroom card blank item]\n50 7454 1\n[/pcroom card blank item]");
            var context = CreateRewardContext();
            Check(
                "coin miss with a zero pcroom-default profile writes no kind2 item",
                !BlackDiamondCardRules.Roll(
                        true,
                        new ScriptedRandom(5).Next,
                        context,
                        new DnfLcg(1),
                        emptyProfile)
                    .HasItem
                    && BlackDiamondCardRules.Roll(
                        true,
                        new ScriptedRandom(5).Next,
                        context,
                        new DnfLcg(1),
                        emptyProfile)
                    .Frozen,
                ref failures);
            Check(
                "death penalty 1.0 fail-closes the extra item without a placeholder",
                ClearRewardGenerator.GenerateBlackDiamondItemCard(
                    CreateRewardContext(deathDropPenalty: 1.0),
                    new DnfLcg(1))
                    .ItemId == 0,
                ref failures);

            if (!RequirePvf(ref failures, "pcroom default extra item"))
                return;

            ClearRewardGenerator.WarmUp();
            var live = ClearRewardDefinitionCatalog.Current;
            var allLevelsUseFullProfile = true;
            for (var level = 1; level <= 200; level++)
                allLevelsUseFullProfile &= live.GetDropProbability(ClearRewardDropProfile.PcRoomDefault, level) == 10000;
            Check(
                "current PVF pcroom default is 10000 for levels 1 through 200",
                allLevelsUseFullProfile,
                ref failures);
            Check(
                "current PVF type weights are equipment 9500 and avatar 500, with no eligible avatar candidates",
                live.GetItemTypeWeight(1) == 0
                    && live.GetItemTypeWeight(2) == 9500
                    && live.GetItemTypeWeight(3) == 0
                    && live.GetItemTypeWeight(4) == 500
                    && EquipmentDropPoolProvider.GetClearRewardPool(avatar: true).Count == 0,
                ref failures);

            var lcg = new DnfLcg(1);
            var generated = ClearRewardGenerator.GenerateBlackDiamondItemCard(
                context,
                lcg);
            Check(
                "pcroom default with mapRate 1 yields a real PVF item, not the coin",
                generated.ItemId > 0
                    && generated.StackCount > 0
                    && generated.ItemId != DiamondCoinItemId,
                ref failures);
            var rolled = BlackDiamondCardRules.Roll(
                true,
                new ScriptedRandom(5).Next,
                context,
                new DnfLcg(1),
                live);
            Check(
                "coin miss uses the same pcroom-default item as a direct generate",
                rolled.HasItem
                    && rolled.ItemId == generated.ItemId
                    && rolled.StackCount == generated.StackCount,
                ref failures);
            var clear = DungeonNotificationBuilder.BuildClearDungeonReward(
                clearBaseExp: 1786,
                scoreBonusExp: 535,
                paidCardCost: 580,
                extraCardItemId: generated.ItemId,
                extraCardItemCount: generated.StackCount);
            var families = ReadExtraFamilies(clear, objectEntryCount: 0, freeCardItemId: 0);
            var equipmentProjection = ProjectKind2Seat(families.Kind2[0]);
            Check(
                "CLEAR kind2 writes gold slot then the frozen pcroom-default item on local seat 0",
                families.Kind1Empty
                    && IsGoldThenItem(
                        families.Kind2[0],
                        generated.ItemId,
                        generated.StackCount)
                    && equipmentProjection.GoldAmount == 0
                    && equipmentProjection.DrawsItemIcon
                    && equipmentProjection.ItemId == generated.ItemId
                    && equipmentProjection.ItemCount == generated.StackCount
                    && SeatsEmpty(families.Kind2, skip: 0),
                ref failures);
            var coinLcg = new DnfLcg(1);
            var coin = BlackDiamondCardRules.Roll(
                true,
                new ScriptedRandom(4, 0).Next,
                context,
                coinLcg,
                live);
            Check(
                "coin hit still returns 7454 and does not consume the item LCG",
                coin.HasItem
                    && coin.ItemId == DiamondCoinItemId
                    && coin.StackCount == 1
                    && coinLcg.Seed == 1,
                ref failures);
        }

        private static void VerifyEligibility(ref int failures)
        {
            var tempDb = Path.Combine(
                Path.GetTempPath(),
                "dfo-bd-card-elig-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    tempDb,
                    ServerPaths.SchemaFilePath);
                SeedAccounts(connectionString);
                Check(
                    "active type56 is eligible",
                    BlackDiamondCardRules.HasEligiblePremium(
                        connectionString,
                        MemberAccount),
                    ref failures);
                Check(
                    "ordinary/expired/type1/type17 are not eligible",
                    !BlackDiamondCardRules.HasEligiblePremium(
                        connectionString,
                        OrdinaryAccount)
                        && !BlackDiamondCardRules.HasEligiblePremium(
                            connectionString,
                            ExpiredAccount)
                        && !BlackDiamondCardRules.HasEligiblePremium(
                            connectionString,
                            Type1Account)
                        && !BlackDiamondCardRules.HasEligiblePremium(
                            connectionString,
                            Type17Account),
                    ref failures);
            }
            finally
            {
                TryDelete(tempDb);
            }
        }

        private static void VerifyPlayExclusion(ref int failures)
        {
            Check(
                "blood altar and tournament presentations cannot freeze extra cards",
                !BlackDiamondCardRules.AllowsPlay(
                    1,
                    DungeonClearPresentationKind.BloodAltar)
                    && !BlackDiamondCardRules.AllowsPlay(
                        1,
                        DungeonClearPresentationKind.Tournament)
                    && !BlackDiamondCardRules.AllowsPlay(
                        1,
                        DungeonClearPresentationKind.LicensedDungeon),
                ref failures);

            if (!RequirePvf(ref failures, "play exclusion"))
                return;

            Check(
                "ordinary standard dungeons allow the extra roll",
                BlackDiamondCardRules.AllowsPlay(
                    1,
                    DungeonClearPresentationKind.Standard)
                    && DungeonSettlementHandler.ShouldScheduleCardRewardFlow(1),
                ref failures);
            Check(
                "licensed/tower/dimension dedicated cards stay disabled",
                !BlackDiamondCardRules.AllowsPlay(
                    5008,
                    DungeonClearPresentationKind.Standard)
                    && !DungeonSettlementHandler.ShouldScheduleCardRewardFlow(5008)
                    && !BlackDiamondCardRules.AllowsPlay(
                        62,
                        DungeonClearPresentationKind.Standard)
                    && DfoServer.GameWorld.Dungeon.IsDimensionDungeon(62),
                ref failures);
        }

        private static void VerifyClearPacketFamilies(ref int failures)
        {
            var empty = DungeonNotificationBuilder.BuildClearDungeonReward(
                clearBaseExp: 1786,
                scoreBonusExp: 535,
                paidCardCost: 580);
            var miss = DungeonNotificationBuilder.BuildClearDungeonReward(
                clearBaseExp: 1786,
                scoreBonusExp: 535,
                paidCardCost: 580,
                extraCardItemId: 0,
                extraCardItemCount: 0);
            var hit = DungeonNotificationBuilder.BuildClearDungeonReward(
                clearBaseExp: 1786,
                scoreBonusExp: 535,
                paidCardCost: 580,
                extraCardItemId: DiamondCoinItemId,
                extraCardItemCount: 1);
            var withFreeItem = DungeonNotificationBuilder.BuildClearDungeonReward(
                clearBaseExp: 100,
                freeCardGold: 40,
                freeCardItemId: 1234,
                freeCardItemCount: 2,
                paidCardCost: 580,
                extraCardItemId: DiamondCoinItemId,
                extraCardItemCount: 1);

            // A21TutorialProtocolSelfTest: 2 object entries + 115B card tail = 290.
            // Zero-entry packets are 159+115=274; do not force production to 290.
            var tutorialEntries = new[]
            {
                new DungeonObjectExperienceEntry(10004, 85),
                new DungeonObjectExperienceEntry(10003, 85),
            };
            var tutorialEmpty = DungeonNotificationBuilder.BuildClearDungeonReward(
                clearBaseExp: 1786,
                scoreBonusExp: 535,
                monsterExp: 999,
                bossExp: 135,
                championExp: 340,
                superChampionExp: 120,
                paidCardCost: 580,
                objectExperienceEntries: tutorialEntries);
            const int clearCardTailLength = 115;
            const int awardedKind2PairBytes = 16;
            Check(
                "empty extra keeps the existing A21 CLEAR length with two object entries",
                tutorialEmpty.Length == 290
                    && empty.Length == miss.Length
                    && empty.Length
                        == DungeonNotificationBuilder.ObjectExperienceEntriesOffset
                            + clearCardTailLength
                    && hit.Length == empty.Length + awardedKind2PairBytes,
                ref failures);

            var emptyFamilies = ReadExtraFamilies(empty, objectEntryCount: 0, freeCardItemId: 0);
            var hitFamilies = ReadExtraFamilies(hit, objectEntryCount: 0, freeCardItemId: 0);
            var freeItemFamilies = ReadExtraFamilies(
                withFreeItem,
                objectEntryCount: 0,
                freeCardItemId: 1234);
            Check(
                "kind1 stays empty and kind2 miss is eight zero counts",
                emptyFamilies.Kind1Empty
                    && emptyFamilies.Kind2Empty
                    && emptyFamilies.PaidCardCost == 580
                    && hitFamilies.Kind1Empty
                    && !hitFamilies.Kind2Empty,
                ref failures);
            var hitProjection = ProjectKind2Seat(hitFamilies.Kind2[0]);
            var freeItemProjection = ProjectKind2Seat(freeItemFamilies.Kind2[0]);
            Check(
                "kind2 hit writes gold slot then 7454x1 on local seat 0 only",
                IsGoldThenItem(hitFamilies.Kind2[0], DiamondCoinItemId, 1)
                    && hitProjection.GoldAmount == 0
                    && hitProjection.DrawsItemIcon
                    && hitProjection.ItemId == DiamondCoinItemId
                    && hitProjection.ItemCount == 1
                    && SeatsEmpty(hitFamilies.Kind2, skip: 0)
                    && SeatsEmpty(hitFamilies.Kind1, skip: -1),
                ref failures);
            Check(
                "free-item prefix does not shift kind2 onto another seat",
                freeItemFamilies.Kind1Empty
                    && IsGoldThenItem(
                        freeItemFamilies.Kind2[0],
                        DiamondCoinItemId,
                        1)
                    && freeItemProjection.GoldAmount == 0
                    && freeItemProjection.ItemId == DiamondCoinItemId
                    && SeatsEmpty(freeItemFamilies.Kind2, skip: 0),
                ref failures);
            var legacySingleSlot = ReadFamily(
                BuildLegacySingleSlotFamily(DiamondCoinItemId, 1),
                offset: 0)[0];
            var legacyProjection = ProjectKind2Seat(legacySingleSlot);
            Check(
                "legacy kind2 count=1 (item,1) projects as 1 gold with no item icon",
                legacySingleSlot.Count == 1
                    && PairAt(legacySingleSlot, 0).ItemId == DiamondCoinItemId
                    && PairAt(legacySingleSlot, 0).StackCount == 1
                    && legacyProjection.GoldAmount == 1
                    && !legacyProjection.DrawsItemIcon
                    && legacyProjection.ItemId == 0,
                ref failures);
            Check(
                "fixed kind2 no longer projects the extra item stack as gold",
                hitProjection.GoldAmount == 0
                    && hitProjection.DrawsItemIcon
                    && emptyFamilies.Kind2Empty
                    && ProjectKind2Seat(emptyFamilies.Kind2[0]).GoldAmount == 0
                    && !ProjectKind2Seat(emptyFamilies.Kind2[0]).DrawsItemIcon,
                ref failures);
            Check(
                "ordinary free/paid prefix through paidCardCost is unchanged on a hit",
                PrefixEquals(empty, hit, emptyFamilies.PaidCardCostOffset + 4),
                ref failures);
        }

        private static void VerifyGrantAndIsolation(ref int failures)
        {
            if (!RequirePvf(ref failures, "card grant"))
                return;

            var tempDb = Path.Combine(
                Path.GetTempPath(),
                "dfo-bd-card-grant-" + Guid.NewGuid().ToString("N") + ".db");
            InventoryLease memberLease = null;
            InventoryLease memberBLease = null;
            InventoryLease ordinaryLease = null;
            InventoryLease fullLease = null;
            InventoryLease paidLease = null;
            InventoryLease replayLease = null;
            InventoryLease pairLease = null;
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    tempDb,
                    ServerPaths.SchemaFilePath);
                var database = new GameDatabase(tempDb, ServerPaths.SchemaFilePath);
                SeedAccounts(connectionString);
                memberLease = OpenLease(database, MemberCharacter, MemberAccount);
                memberBLease = OpenLease(database, MemberCharacterB, MemberAccount);
                ordinaryLease = OpenLease(database, OrdinaryCharacter, OrdinaryAccount);
                fullLease = OpenLease(database, FullBagCharacter, MemberAccount);
                paidLease = OpenLease(database, PaidCharacter, MemberAccount);
                replayLease = OpenLease(database, ReplayCharacter, MemberAccount);
                pairLease = OpenLease(database, PairCharacter, MemberAccount);
                var persistent = new DungeonPersistentEffectApplicationService(
                    database.ConnectionString,
                    database: database);
                var service = new CardRewardService(persistent);
                var pool = ClearRewardDefinitionCatalog.Current;

                var frozenRun = CreateRevealedRun();
                var first = frozenRun.FreezeBlackDiamondCardReward(
                    true,
                    new ScriptedRandom(0, 0).Next,
                    CreateRewardContext(),
                    new DnfLcg(1));
                var second = frozenRun.FreezeBlackDiamondCardReward(
                    true,
                    _ => throw new InvalidOperationException("rerolled"),
                    CreateRewardContext(),
                    new DnfLcg(99));
                Check(
                    "the same run freezes once and ignores a later roll",
                    first.HasItem
                        && first.ItemId == DiamondCoinItemId
                        && ReferenceEqualsRunReward(first, second),
                    ref failures);

                var memberRun = CreateRevealedRun(
                    BlackDiamondCardRules.Roll(
                        true,
                        new ScriptedRandom(0, 0).Next,
                        pool));
                var ordinaryRun = CreateRevealedRun(
                    BlackDiamondCardRules.Roll(
                        false,
                        new ScriptedRandom(0, 0).Next,
                        pool));
                Check(
                    "member hit and ordinary miss stay isolated on two participant runs",
                    memberRun.BlackDiamondCardReward.HasItem
                        && !ordinaryRun.BlackDiamondCardReward.HasItem,
                    ref failures);

                var memberGoldBefore = CountGold(memberLease);
                Check(
                    "free select grants original gold plus the frozen coin",
                    DeliverFree(service, memberLease, memberRun)
                        && CountItem(memberLease, DiamondCoinItemId) == 1
                        && CountGold(memberLease) >= memberGoldBefore,
                    ref failures);
                var memberBRun = CreateRevealedRun(
                    BlackDiamondCardReward.Miss);
                Check(
                    "second participant freeze/miss stays isolated from the first",
                    !memberBRun.BlackDiamondCardReward.HasItem
                        && DeliverFree(service, memberBLease, memberBRun)
                        && CountItem(memberBLease, DiamondCoinItemId) == 0
                        && CountItem(memberLease, DiamondCoinItemId) == 1,
                    ref failures);
                Check(
                    "repeat free select does not reroll or double-grant",
                    !CardRewardRules.TrySelectCardSlot(memberRun, 0, 0)
                        && !service.Deliver(
                                memberLease.CharacterId,
                                memberLease,
                                memberRun,
                                CardRewardSide.Free)
                            .Committed
                        && CountItem(memberLease, DiamondCoinItemId) == 1,
                    ref failures);

                var replayRun = CreateRevealedRun(
                    BlackDiamondCardReward.Hit(DiamondCoinItemId, 1),
                    gold: 0);
                var replayCards = replayRun.CardRewards;
                var replayExtra = replayRun.BlackDiamondCardReward;
                Check(
                    "first free commit grants the coin",
                    DeliverFree(service, replayLease, replayRun)
                        && CountItem(replayLease, DiamondCoinItemId) == 1,
                    ref failures);
                var replayEffect = CardRewardRules.GetEffectId(
                    replayRun,
                    CardRewardSide.Free);
                var replayAgain = persistent.TryApplyCardReward(
                    replayEffect,
                    replayLease,
                    replayLease.SessionId,
                    CardRewardSide.Free,
                    0,
                    false,
                    replayCards,
                    replayExtra,
                    out var replayResult,
                    out _);
                Check(
                    "persistent recovery replay returns committed without a second coin",
                    replayAgain
                        && replayResult != null
                        && CountItem(replayLease, DiamondCoinItemId) == 1,
                    ref failures);

                FillMain(fullLease);
                var fullRun = CreateRevealedRun(
                    BlackDiamondCardReward.Hit(DiamondCoinItemId, 1),
                    gold: 0);
                var fullBefore = CountItem(fullLease, DiamondCoinItemId);
                Check(
                    "full bag fails closed and does not consume the free slot",
                    CardRewardRules.TrySelectCardSlot(fullRun, 0, 0)
                        && !service.Deliver(
                                fullLease.CharacterId,
                                fullLease,
                                fullRun,
                                CardRewardSide.Free)
                            .Committed
                        && fullRun.FreeCardSlots[0] == 0xFF
                        && CountItem(fullLease, DiamondCoinItemId) == fullBefore
                        && fullRun.BlackDiamondCardReward.HasItem,
                    ref failures);
                lock (fullLease.SyncRoot)
                {
                    fullLease.Inventory.RemoveItem(
                        InventoryListType.Main,
                        InventoryService.MainSlotStart);
                }
                var frozenCoin = fullRun.BlackDiamondCardReward.ItemId;
                Check(
                    "retry after a free slot uses the frozen coin and does not reroll",
                    frozenCoin == DiamondCoinItemId
                        && DeliverFree(service, fullLease, fullRun)
                        && CountItem(fullLease, DiamondCoinItemId) == fullBefore + 1,
                    ref failures);

                var paidRun = CreateRevealedRun(
                    BlackDiamondCardReward.Hit(DiamondCoinItemId, 1),
                    gold: 0,
                    freeItemId: 0,
                    paidItemId: SecondaryItemId);
                var paidBefore = CountItem(paidLease, DiamondCoinItemId);
                Check(
                    "paid select cannot steal the free-side extra coin",
                    CardRewardRules.TrySelectCardSlot(paidRun, 1, 0)
                        && service.Deliver(
                                paidLease.CharacterId,
                                paidLease,
                                paidRun,
                                CardRewardSide.Paid)
                            .Committed
                        && CountItem(paidLease, DiamondCoinItemId) == paidBefore
                        && CountItem(paidLease, SecondaryItemId) == 1,
                    ref failures);

                var pairRun = CreateRevealedRun(
                    BlackDiamondCardReward.Hit(DiamondCoinItemId, 1),
                    gold: 0,
                    freeItemId: SecondaryItemId);
                var pairResult = default(CardRewardDeliveryResult);
                Check(
                    "free item plus coin grant two main slots in one commit",
                    CardRewardRules.TrySelectCardSlot(pairRun, 0, 0)
                        && (pairResult = service.Deliver(
                                pairLease.CharacterId,
                                pairLease,
                                pairRun,
                                CardRewardSide.Free))
                            .Committed
                        && DistinctMainSlots(pairResult.Changes) >= 2
                        && CountItem(pairLease, DiamondCoinItemId) == 1
                        && CountItem(pairLease, SecondaryItemId) == 1,
                    ref failures);

                Check(
                    "ordinary participant still has no extra coin after the member grant",
                    CountItem(ordinaryLease, DiamondCoinItemId) == 0
                        && !ordinaryRun.BlackDiamondCardReward.HasItem,
                    ref failures);

                InventoryLease extraItemLease = null;
                try
                {
                    extraItemLease = OpenLease(
                        database,
                        ExtraItemCharacter,
                        MemberAccount);
                    var generatedExtra = ClearRewardGenerator
                        .GenerateBlackDiamondItemCard(
                            CreateRewardContext(),
                            new DnfLcg(1));
                    var extraItemRun = CreateRevealedRun(
                        BlackDiamondCardReward.Hit(
                            generatedExtra.ItemId,
                            generatedExtra.StackCount),
                        gold: 0);
                    Check(
                        "free grant of a pcroom-default extra item persists and uses template durability",
                        generatedExtra.ItemId > 0
                            && generatedExtra.ItemId != DiamondCoinItemId
                            && DeliverFree(service, extraItemLease, extraItemRun)
                            && CountItem(extraItemLease, generatedExtra.ItemId) == 1
                            && CountPersistedItem(
                                database,
                                ExtraItemCharacter,
                                MemberAccount,
                                generatedExtra.ItemId) == 1
                            && ExtraItemUsesTemplateDurability(
                                extraItemLease,
                                generatedExtra.ItemId),
                        ref failures);
                    var extraClear = DungeonNotificationBuilder.BuildClearDungeonReward(
                        clearBaseExp: 100,
                        extraCardItemId: generatedExtra.ItemId,
                        extraCardItemCount: generatedExtra.StackCount);
                    var extraFamilies = ReadExtraFamilies(
                        extraClear,
                        objectEntryCount: 0,
                        freeCardItemId: 0);
                    var extraProjection = ProjectKind2Seat(extraFamilies.Kind2[0]);
                    Check(
                        "CLEAR kind2 matches the persisted extra item",
                        IsGoldThenItem(
                            extraFamilies.Kind2[0],
                            generatedExtra.ItemId,
                            generatedExtra.StackCount)
                            && extraProjection.GoldAmount == 0
                            && extraProjection.DrawsItemIcon
                            && extraProjection.ItemId == generatedExtra.ItemId
                            && extraProjection.ItemCount == generatedExtra.StackCount
                            && CountPersistedItem(
                                database,
                                ExtraItemCharacter,
                                MemberAccount,
                                generatedExtra.ItemId) == 1,
                        ref failures);
                }
                finally
                {
                    Unregister(extraItemLease);
                }

                var oldPayload = JsonSerializer.Deserialize<CardRewardEffectPayload>(
                    "{\"characterId\":1,\"accountId\":1,\"side\":0,\"paidGoldCost\":0," +
                    "\"consumeGoldCardContractUse\":false,\"requestedGold\":0," +
                    "\"itemId\":0,\"stackCount\":0}",
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        PropertyNameCaseInsensitive = false,
                    });
                Check(
                    "old free payload JSON without extra fields stays empty extra",
                    oldPayload != null
                        && oldPayload.ExtraItemId == 0
                        && oldPayload.ExtraStackCount == 0,
                    ref failures);

                Check(
                    "old payload serialization stays byte-identical for outbox idempotency",
                    JsonSerializer.Serialize(oldPayload, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    }) == "{\"characterId\":1,\"accountId\":1,\"side\":0,\"paidGoldCost\":0," +
                        "\"consumeGoldCardContractUse\":false,\"requestedGold\":0," +
                        "\"itemId\":0,\"stackCount\":0}",
                    ref failures);

                var info = CardRewardNotificationSender.BuildCardInfoAck(memberRun);
                Check(
                    "0x0047 paid list does not carry the kind2 coin",
                    info != null
                        && info.Length > 4
                        && !PaidCardInfoContainsItem(info, DiamondCoinItemId),
                    ref failures);

                memberRun.CardRewards = null;
                Check(
                    "clearing card rewards also clears the extra freeze",
                    !memberRun.BlackDiamondCardReward.Frozen
                        && !memberRun.BlackDiamondCardReward.HasItem,
                    ref failures);
            }
            finally
            {
                Unregister(memberLease);
                Unregister(memberBLease);
                Unregister(ordinaryLease);
                Unregister(fullLease);
                Unregister(paidLease);
                Unregister(replayLease);
                Unregister(pairLease);
                TryDelete(tempDb);
            }
        }

        private static void VerifyCoordinatorFlows(ref int failures)
        {
            if (!RequirePvf(ref failures, "card coordinator flows"))
                return;

            ClearRewardGenerator.WarmUp();
            var generated = ClearRewardGenerator.GenerateBlackDiamondItemCard(
                CreateRewardContext(),
                new DnfLcg(1));
            if (generated.ItemId <= 0 || generated.ItemId == DiamondCoinItemId)
            {
                Check(
                    "pcroom default produced a real extra item for coordinator flows",
                    false,
                    ref failures);
                return;
            }

            var extra = BlackDiamondCardReward.Hit(
                generated.ItemId,
                generated.StackCount);
            var tempDb = Path.Combine(
                Path.GetTempPath(),
                "dfo-bd-card-coord-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    tempDb,
                    ServerPaths.SchemaFilePath);
                var database = new GameDatabase(tempDb, ServerPaths.SchemaFilePath);
                SeedAccounts(connectionString);
                var persistent = new DungeonPersistentEffectApplicationService(
                    database.ConnectionString,
                    database: database);
                var application = new CardRewardService(persistent);
                var sender = new CardRewardNotificationSender();
                var coordinator = new CardRewardCoordinator(
                    application,
                    sender,
                    database);

                RunCoordinatorCase(
                    "timer auto-flips free and grants extra once",
                    database,
                    coordinator,
                    TimerCharacter,
                    extra,
                    generated.ItemId,
                    (session, run) =>
                    {
                        coordinator.ScheduleAutoFlow(
                            session,
                            layoutDelayMs: 0,
                            autoFlipDelayMs: 0);
                        return PumpUntil(
                            () => CardRewardRules.IsCommitted(
                                run,
                                CardRewardSide.Free),
                            TimeSpan.FromSeconds(5));
                    },
                    expectExtra: true,
                    expectPaid: false,
                    failures: ref failures);

                RunCoordinatorCase(
                    "paid first then free still grants extra only on free",
                    database,
                    coordinator,
                    PaidFirstCharacter,
                    extra,
                    generated.ItemId,
                    (session, run) =>
                    {
                        coordinator.HandleSelectCard(
                            session,
                            new byte[] { 1, 0 })
                            .GetAwaiter()
                            .GetResult();
                        coordinator.HandleSelectCard(
                            session,
                            new byte[] { 0, 0 })
                            .GetAwaiter()
                            .GetResult();
                        return CardRewardRules.IsCommitted(
                            run,
                            CardRewardSide.Free)
                            && CardRewardRules.IsCommitted(
                                run,
                                CardRewardSide.Paid);
                    },
                    expectExtra: true,
                    expectPaid: true,
                    revealed: true,
                    paidItemId: SecondaryItemId,
                    failures: ref failures);

                RunCoordinatorCase(
                    "free first then paid does not double-grant extra",
                    database,
                    coordinator,
                    FreeFirstCharacter,
                    extra,
                    generated.ItemId,
                    (session, run) =>
                    {
                        coordinator.HandleSelectCard(
                            session,
                            new byte[] { 0, 0 })
                            .GetAwaiter()
                            .GetResult();
                        coordinator.HandleSelectCard(
                            session,
                            new byte[] { 1, 0 })
                            .GetAwaiter()
                            .GetResult();
                        return CardRewardRules.IsCommitted(
                            run,
                            CardRewardSide.Free)
                            && CardRewardRules.IsCommitted(
                                run,
                                CardRewardSide.Paid);
                    },
                    expectExtra: true,
                    expectPaid: true,
                    revealed: true,
                    paidItemId: SecondaryItemId,
                    failures: ref failures);

                RunCoordinatorCase(
                    "paid first then timer auto-flips remaining free extra",
                    database,
                    coordinator,
                    PaidWaitCharacter,
                    extra,
                    generated.ItemId,
                    (session, run) =>
                    {
                        coordinator.ScheduleAutoFlow(
                            session,
                            layoutDelayMs: 0,
                            autoFlipDelayMs: 60_000);
                        if (!PumpUntil(
                                () => run.SettlementState
                                    == DungeonSettlementState.CardsRevealed,
                                TimeSpan.FromSeconds(5)))
                        {
                            return false;
                        }

                        coordinator.HandleSelectCard(
                            session,
                            new byte[] { 1, 0 })
                            .GetAwaiter()
                            .GetResult();
                        ClockService.Instance.CheckOnce(
                            DateTime.UtcNow.AddMinutes(2));
                        return PumpUntil(
                            () => CardRewardRules.IsCommitted(
                                run,
                                CardRewardSide.Free),
                            TimeSpan.FromSeconds(5));
                    },
                    expectExtra: true,
                    expectPaid: true,
                    paidItemId: SecondaryItemId,
                    failures: ref failures);

                RunCoordinatorCase(
                    "non-member timer auto-flip does not invent extra",
                    database,
                    coordinator,
                    OrdinaryFlowCharacter,
                    BlackDiamondCardReward.Miss,
                    generated.ItemId,
                    (session, run) =>
                    {
                        coordinator.ScheduleAutoFlow(
                            session,
                            layoutDelayMs: 0,
                            autoFlipDelayMs: 0);
                        return PumpUntil(
                            () => CardRewardRules.IsCommitted(
                                run,
                                CardRewardSide.Free),
                            TimeSpan.FromSeconds(5));
                    },
                    expectExtra: false,
                    expectPaid: false,
                    accountId: OrdinaryAccount,
                    failures: ref failures);

                RunCoordinatorRetry(
                    database,
                    coordinator,
                    application,
                    extra,
                    generated.ItemId,
                    ref failures);
                RunCoordinatorStaleTimer(
                    database,
                    coordinator,
                    extra,
                    generated.ItemId,
                    ref failures);
                RunCoordinatorBackToBackFreeze(ref failures);
            }
            finally
            {
                ClockService.Instance.CancelOneShotsByPrefix("dungeon-card:");
                TryDelete(tempDb);
            }
        }

        private static void RunCoordinatorCase(
            string name,
            GameDatabase database,
            CardRewardCoordinator coordinator,
            int characterId,
            BlackDiamondCardReward extra,
            int extraItemId,
            Func<EnhancedClientSession, DungeonRun, bool> act,
            bool expectExtra,
            bool expectPaid,
            ref int failures,
            bool revealed = false,
            int paidItemId = 0,
            int accountId = MemberAccount)
        {
            using (var capture = new A21DungeonDropItemSelfTest.LoopbackPacketCapture())
            {
                var session = capture.Session;
                session.Account = new DfoServer.Game.Accounts.AccountRecord
                {
                    AccountId = accountId,
                };
                session.Player.CharacterId = characterId;
                var run = revealed
                    ? CreateRevealedRun(
                        extra,
                        gold: 0,
                        paidItemId: paidItemId)
                    : CreateResultShownRun(
                        extra,
                        gold: 0,
                        paidItemId: paidItemId);
                session.Player.CurrentRun = run;
                var lease = OpenLease(
                    database,
                    characterId,
                    accountId,
                    session.SessionId);
                using (StartPacketDrain(capture))
                {
                    try
                    {
                        var acted = act(session, run);
                        var extraCount = CountPersistedItem(
                            database,
                            characterId,
                            accountId,
                            extraItemId);
                        var paidCount = paidItemId > 0
                            ? CountPersistedItem(
                                database,
                                characterId,
                                accountId,
                                paidItemId)
                            : 0;
                        var info = CardRewardNotificationSender.BuildCardInfoAck(run);
                        Check(
                            name,
                            acted
                                && extraCount == (expectExtra ? 1 : 0)
                                && (!expectPaid || paidCount == 1)
                                && (paidItemId == 0
                                    || !PaidCardInfoContainsItem(info, extraItemId)),
                            ref failures);
                    }
                    finally
                    {
                        DungeonRunLifecycle.CancelAutoFlip(run);
                        Unregister(lease);
                    }
                }
            }
        }

        private static void RunCoordinatorRetry(
            GameDatabase database,
            CardRewardCoordinator coordinator,
            CardRewardService application,
            BlackDiamondCardReward extra,
            int extraItemId,
            ref int failures)
        {
            using (var capture = new A21DungeonDropItemSelfTest.LoopbackPacketCapture())
            {
                var session = capture.Session;
                session.Account = new DfoServer.Game.Accounts.AccountRecord
                {
                    AccountId = MemberAccount,
                };
                session.Player.CharacterId = RetryFlowCharacter;
                var run = CreateRevealedRun(extra, gold: 0);
                session.Player.CurrentRun = run;
                var lease = OpenLease(
                    database,
                    RetryFlowCharacter,
                    MemberAccount,
                    session.SessionId);
                FillMain(lease);
                using (StartPacketDrain(capture))
                {
                    try
                    {
                        var firstSelect = CardRewardRules.TrySelectCardSlot(
                            run,
                            0,
                            0);
                        var firstDeliver = !application.Deliver(
                                lease.CharacterId,
                                lease,
                                run,
                                CardRewardSide.Free)
                            .Committed;
                        var failedClosed = firstSelect
                            && firstDeliver
                            && run.FreeCardSlots[0] == 0xFF
                            && !CardRewardRules.IsCommitted(
                                run,
                                CardRewardSide.Free)
                            && CountItem(lease, extraItemId) == 0
                            && run.BlackDiamondCardReward.HasItem;
                        lock (lease.SyncRoot)
                        {
                            if (!ItemMetadataResolver.TryResolveItemKind(extraItemId, out var itemKind)
                                || !ItemSlotBoundService.TryGetSlotRange(
                                    itemKind,
                                    ItemSlotBoundService.MainExpandStageFull,
                                    out var listType,
                                    out var range)
                                || listType != InventoryListType.Main)
                            {
                                throw new InvalidOperationException("Retry fixture requires a real main-inventory reward range.");
                            }
                            lease.Inventory.RemoveItem(listType, range.Start);
                        }

                        DungeonRunLifecycle.CancelAutoFlip(run);
                        coordinator.HandleSelectCard(
                            session,
                            new byte[] { 0, 0 })
                            .GetAwaiter()
                            .GetResult();
                        Check(
                            "full-bag first extra deliver fails closed",
                            failedClosed,
                            ref failures);
                        Check(
                            "full-bag retry uses the frozen extra and grants once",
                            CardRewardRules.IsCommitted(
                                    run,
                                    CardRewardSide.Free)
                                && CountItem(lease, extraItemId) == 1
                                && CountPersistedItem(
                                    database,
                                    RetryFlowCharacter,
                                    MemberAccount,
                                    extraItemId) == 1
                                && extra.ItemId == extraItemId,
                            ref failures);
                    }
                    finally
                    {
                        DungeonRunLifecycle.CancelAutoFlip(run);
                        Unregister(lease);
                    }
                }
            }
        }

        private static void RunCoordinatorStaleTimer(
            GameDatabase database,
            CardRewardCoordinator coordinator,
            BlackDiamondCardReward extra,
            int extraItemId,
            ref int failures)
        {
            using (var capture = new A21DungeonDropItemSelfTest.LoopbackPacketCapture())
            {
                var session = capture.Session;
                session.Account = new DfoServer.Game.Accounts.AccountRecord
                {
                    AccountId = MemberAccount,
                };
                session.Player.CharacterId = StaleTimerCharacter;
                var stale = CreateResultShownRun(extra, gold: 0);
                session.Player.CurrentRun = stale;
                var lease = OpenLease(
                    database,
                    StaleTimerCharacter,
                    MemberAccount,
                    session.SessionId);
                using (StartPacketDrain(capture))
                {
                    try
                    {
                        coordinator.ScheduleAutoFlow(
                            session,
                            layoutDelayMs: 0,
                            autoFlipDelayMs: 0);
                        var replacement = CreateResultShownRun(extra, gold: 0);
                        session.Player.CurrentRun = replacement;
                        ClockService.Instance.CheckOnce(
                            DateTime.UtcNow.AddMinutes(1));
                        Thread.Sleep(50);
                        ClockService.Instance.CheckOnce(
                            DateTime.UtcNow.AddMinutes(1));
                        Check(
                            "old-run auto-flip does not grant extra after the session left",
                            !CardRewardRules.IsCommitted(
                                stale,
                                CardRewardSide.Free)
                                && CountPersistedItem(
                                    database,
                                    StaleTimerCharacter,
                                    MemberAccount,
                                    extraItemId) == 0,
                            ref failures);
                    }
                    finally
                    {
                        DungeonRunLifecycle.CancelAutoFlip(stale);
                        DungeonRunLifecycle.CancelAutoFlip(
                            session.Player.CurrentRun);
                        Unregister(lease);
                    }
                }
            }
        }

        private static void RunCoordinatorBackToBackFreeze(ref int failures)
        {
            var run = CreateResultShownRun(default, gold: 0);
            var first = run.FreezeBlackDiamondCardReward(
                true,
                new ScriptedRandom(5).Next,
                CreateRewardContext(),
                new DnfLcg(1));
            var second = run.FreezeBlackDiamondCardReward(
                true,
                new ScriptedRandom(4, 0).Next,
                CreateRewardContext(),
                new DnfLcg(2));
            Check(
                "back-to-back freeze on the same run keeps the first extra item",
                first.HasItem
                    && first.ItemId != DiamondCoinItemId
                    && ReferenceEqualsRunReward(first, second),
                ref failures);
        }

        private static DungeonRun CreateRevealedRun(
            BlackDiamondCardReward extra = default,
            int gold = 40,
            int freeItemId = 0,
            int paidItemId = 0)
        {
            var run = new DungeonRun(1, 0);
            if (run.RunId <= 0 || run.PartyDungeonInstanceId <= 0)
            {
                throw new InvalidOperationException(
                    "Card reward fixture requires a positive run/instance identity.");
            }

            var fact = new DungeonClearedFact(
                new DungeonClearIntent(
                    DungeonEventEnvelope.Create(run, 1, "black-diamond-card"),
                    "black-diamond-card",
                    1));
            if (!run.TryBeginClearCommit(fact)
                || !run.TryCompleteClearCommit(fact)
                || !run.TryBeginSettlementPreparation()
                || !run.TryMarkResultShown()
                || !run.TryMarkCardsRevealed())
            {
                throw new InvalidOperationException(
                    "Unable to walk the ordinary card settlement states.");
            }

            run.CardRewards = new List<ClearRewardGenerator.CardReward>
            {
                new ClearRewardGenerator.CardReward
                {
                    IsGold = true,
                    GoldAmount = gold,
                },
                freeItemId > 0
                    ? new ClearRewardGenerator.CardReward
                    {
                        ItemId = freeItemId,
                        StackCount = 1,
                    }
                    : default,
                default,
                default,
                new ClearRewardGenerator.CardReward
                {
                    IsGold = true,
                    GoldAmount = 0,
                },
                paidItemId > 0
                    ? new ClearRewardGenerator.CardReward
                    {
                        ItemId = paidItemId,
                        StackCount = 1,
                    }
                    : default,
                default,
                default,
            };
            run.BlackDiamondCardReward = extra;
            run.PaidCardCost = 0;
            run.FreeCardSlots = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
            run.PaidCardSlots = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
            return run;
        }

        private static DungeonRun CreateResultShownRun(
            BlackDiamondCardReward extra = default,
            int gold = 40,
            int freeItemId = 0,
            int paidItemId = 0)
        {
            var run = new DungeonRun(1, 0);
            if (run.RunId <= 0 || run.PartyDungeonInstanceId <= 0)
            {
                throw new InvalidOperationException(
                    "Card reward fixture requires a positive run/instance identity.");
            }

            var fact = new DungeonClearedFact(
                new DungeonClearIntent(
                    DungeonEventEnvelope.Create(run, 1, "black-diamond-card"),
                    "black-diamond-card",
                    1));
            if (!run.TryBeginClearCommit(fact)
                || !run.TryCompleteClearCommit(fact)
                || !run.TryBeginSettlementPreparation()
                || !run.TryMarkResultShown())
            {
                throw new InvalidOperationException(
                    "Unable to walk the ordinary result-shown settlement states.");
            }

            run.CardRewards = new List<ClearRewardGenerator.CardReward>
            {
                new ClearRewardGenerator.CardReward
                {
                    IsGold = true,
                    GoldAmount = gold,
                },
                freeItemId > 0
                    ? new ClearRewardGenerator.CardReward
                    {
                        ItemId = freeItemId,
                        StackCount = 1,
                    }
                    : default,
                default,
                default,
                new ClearRewardGenerator.CardReward
                {
                    IsGold = true,
                    GoldAmount = 0,
                },
                paidItemId > 0
                    ? new ClearRewardGenerator.CardReward
                    {
                        ItemId = paidItemId,
                        StackCount = 1,
                    }
                    : default,
                default,
                default,
            };
            run.BlackDiamondCardReward = extra;
            run.PaidCardCost = 0;
            run.FreeCardSlots = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
            run.PaidCardSlots = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
            return run;
        }

        private static ClearRewardGenerationContext CreateRewardContext(
            double deathDropPenalty = 0.0)
        {
            return new ClearRewardGenerationContext(
                dungeonLevel: 20,
                difficulty: 0,
                partyMemberCount: 1,
                rankBonusRate: 0.0f,
                normalKillCount: 1,
                championKillCount: 0,
                bossKillCount: 1,
                visitedRoomCount: 1,
                totalRoomCount: 10,
                deathDropPenalty: deathDropPenalty);
        }

        private static bool PumpUntil(Func<bool> done, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                ClockService.Instance.CheckOnce(DateTime.UtcNow.AddMinutes(1));
                if (done())
                    return true;
                Thread.Sleep(20);
            }

            ClockService.Instance.CheckOnce(DateTime.UtcNow.AddMinutes(1));
            return done();
        }

        private static IDisposable StartPacketDrain(
            A21DungeonDropItemSelfTest.LoopbackPacketCapture capture)
        {
            var cts = new CancellationTokenSource();
            var task = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        capture.ReadPackets(1);
                    }
                    catch (TimeoutException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            });
            return new DrainHandle(cts, task);
        }

        private sealed class DrainHandle : IDisposable
        {
            private readonly CancellationTokenSource _cts;
            private readonly Task _task;

            internal DrainHandle(CancellationTokenSource cts, Task task)
            {
                _cts = cts;
                _task = task;
            }

            public void Dispose()
            {
                _cts.Cancel();
                try
                {
                    _task.Wait(TimeSpan.FromSeconds(2));
                }
                catch
                {
                }

                _cts.Dispose();
            }
        }

        private static bool DeliverFree(
            CardRewardService service,
            InventoryLease lease,
            DungeonRun run)
        {
            return CardRewardRules.TrySelectCardSlot(run, 0, 0)
                && service.Deliver(
                    lease.CharacterId,
                    lease,
                    run,
                    CardRewardSide.Free)
                    .Committed;
        }

        private static InventoryLease OpenLease(
            GameDatabase database,
            int characterId,
            int accountId)
            => OpenLease(database, characterId, accountId, Guid.NewGuid());

        private static InventoryLease OpenLease(
            GameDatabase database,
            int characterId,
            int accountId,
            Guid sessionId)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    characterId,
                    accountId,
                    database);
                return InventoryContext.Register(sessionId, inventory);
            }
        }

        private static int CountPersistedItem(
            GameDatabase database,
            int characterId,
            int accountId,
            int itemId)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    characterId,
                    accountId,
                    database);
                return inventory.CountMainItem(itemId);
            }
        }

        private static bool ExtraItemUsesTemplateDurability(
            InventoryLease lease,
            int itemId)
        {
            var expected = ItemMetadataResolver.Resolve(itemId).Durability;
            lock (lease.SyncRoot)
            {
                for (var slot = InventoryService.MainSlotStart;
                    slot <= InventoryService.MainSlotEnd;
                    slot++)
                {
                    if (!lease.Inventory.TryGetItem(
                            InventoryListType.Main,
                            slot,
                            out var item)
                        || item == null
                        || item.ItemId != itemId)
                    {
                        continue;
                    }

                    return item.Durability == expected;
                }
            }

            return false;
        }

        private static void FillMain(InventoryLease lease)
        {
            lock (lease.SyncRoot)
            {
                for (var slot = InventoryService.MainSlotStart;
                    slot <= InventoryService.MainSlotEnd;
                    slot++)
                {
                    var filler = ItemCore.Create(
                        ItemCore.KindConsumable,
                        800000 + slot);
                    filler.Count = 1;
                    lease.Inventory.SetItem(InventoryListType.Main, slot, filler);
                }
            }
        }

        private static int CountItem(InventoryLease lease, int itemId)
        {
            lock (lease.SyncRoot)
                return lease.Inventory.CountMainItem(itemId);
        }

        private static int CountGold(InventoryLease lease)
        {
            lock (lease.SyncRoot)
            {
                return lease.Inventory.CountMainItem(
                    InventoryService.MainVirtualCurrencySlotStart);
            }
        }

        private static int DistinctMainSlots(
            IReadOnlyList<InventorySlotMutation> changes)
        {
            var slots = new HashSet<short>();
            if (changes == null)
                return 0;
            foreach (var change in changes)
            {
                if (change.ListType == InventoryListType.Main)
                    slots.Add(change.SlotIndex);
            }
            return slots.Count;
        }

        private static bool ReferenceEqualsRunReward(
            BlackDiamondCardReward first,
            BlackDiamondCardReward second)
            => first.Frozen == second.Frozen
               && first.ItemId == second.ItemId
               && first.StackCount == second.StackCount;

        private static ExtraFamilies ReadExtraFamilies(
            byte[] body,
            int objectEntryCount,
            int freeCardItemId)
        {
            var reserved = DungeonNotificationBuilder.ObjectExperienceEntriesOffset
                + objectEntryCount * 8;
            var freePairs = freeCardItemId > 0 ? 2 : 1;
            var paidOffset = reserved + 2 + freePairs * 8 + 7 * 9;
            var paidCost = BitConverter.ToInt32(body, paidOffset);
            var offset = paidOffset + 4;
            var kind1 = ReadFamily(body, ref offset);
            var kind2 = ReadFamily(body, ref offset);
            return new ExtraFamilies
            {
                PaidCardCostOffset = paidOffset,
                PaidCardCost = paidCost,
                Kind1 = kind1,
                Kind2 = kind2,
            };
        }

        private static ExtraSeat[] ReadFamily(byte[] body, int offset)
        {
            var local = offset;
            return ReadFamily(body, ref local);
        }

        private static ExtraSeat[] ReadFamily(byte[] body, ref int offset)
        {
            var seats = new ExtraSeat[8];
            for (var seat = 0; seat < 8; seat++)
            {
                var count = body[offset++];
                var pairs = count > 0 ? new ExtraPair[count] : Array.Empty<ExtraPair>();
                for (var i = 0; i < count; i++)
                {
                    var itemId = (int)BitConverter.ToUInt32(body, offset);
                    offset += 4;
                    var stack = (int)BitConverter.ToUInt32(body, offset);
                    offset += 4;
                    pairs[i] = new ExtraPair
                    {
                        ItemId = itemId,
                        StackCount = stack,
                    };
                }

                seats[seat] = new ExtraSeat
                {
                    Count = count,
                    Pairs = pairs,
                };
            }

            return seats;
        }

        // Current client 25F4000 / 25F412A / 25F478B / 25F4798:
        // gold is always vector[0] record+30 (first pair stack);
        // item icon is drawn only when pair-count > 1, from pair 1.
        private static ClientKind2Projection ProjectKind2Seat(ExtraSeat seat)
        {
            var gold = PairAt(seat, 0).StackCount;
            var drawsItem = seat.Count > 1;
            var item = drawsItem ? PairAt(seat, 1) : default;
            return new ClientKind2Projection
            {
                GoldAmount = gold,
                DrawsItemIcon = drawsItem,
                ItemId = item.ItemId,
                ItemCount = item.StackCount,
            };
        }

        private static bool IsGoldThenItem(ExtraSeat seat, int itemId, int stackCount)
            => seat.Count == 2
               && PairAt(seat, 0).ItemId == 0
               && PairAt(seat, 0).StackCount == 0
               && PairAt(seat, 1).ItemId == itemId
               && PairAt(seat, 1).StackCount == stackCount;

        private static ExtraPair PairAt(ExtraSeat seat, int index)
        {
            if (seat.Pairs == null
                || index < 0
                || index >= seat.Pairs.Length)
            {
                return default;
            }

            return seat.Pairs[index];
        }

        private static byte[] BuildLegacySingleSlotFamily(int itemId, int stackCount)
        {
            var body = new byte[9 + 7];
            var offset = 0;
            body[offset++] = 1;
            var idBytes = BitConverter.GetBytes((uint)itemId);
            Buffer.BlockCopy(idBytes, 0, body, offset, 4);
            offset += 4;
            var stackBytes = BitConverter.GetBytes((uint)stackCount);
            Buffer.BlockCopy(stackBytes, 0, body, offset, 4);
            return body;
        }

        private static bool SeatsEmpty(ExtraSeat[] seats, int skip)
        {
            for (var i = 0; i < seats.Length; i++)
            {
                if (i == skip)
                    continue;
                if (seats[i].Count != 0)
                    return false;
                if (seats[i].Pairs == null)
                    continue;
                for (var p = 0; p < seats[i].Pairs.Length; p++)
                {
                    if (seats[i].Pairs[p].ItemId != 0
                        || seats[i].Pairs[p].StackCount != 0)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool PrefixEquals(byte[] left, byte[] right, int length)
        {
            if (left == null || right == null || left.Length < length || right.Length < length)
                return false;
            for (var i = 0; i < length; i++)
            {
                if (left[i] != right[i])
                    return false;
            }

            return true;
        }

        private static bool PaidCardInfoContainsItem(byte[] body, int itemId)
        {
            if (body == null || body.Length < 6)
                return false;
            var offset = 1;
            for (var seat = 0; seat < 8 && offset + 3 < body.Length; seat++)
            {
                offset += 2;
                if (seat >= 4)
                {
                    offset += 2;
                    continue;
                }

                if (seat != 0)
                {
                    offset += 2;
                    continue;
                }

                var paidCount = body[offset++];
                for (var i = 0; i < paidCount && offset + 8 <= body.Length; i++)
                {
                    var id = BitConverter.ToInt32(body, offset);
                    if (id == itemId)
                        return true;
                    offset += 8;
                }

                if (offset < body.Length)
                    offset++;
            }

            return false;
        }

        private static void SeedAccounts(string connectionString)
        {
            var expire = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
            Execute(
                connectionString,
                @"
INSERT INTO accounts(account_id,m_id) VALUES
 (781001,'bd-card-ordinary'),
 (781002,'bd-card-member'),
 (781003,'bd-card-expired'),
 (781004,'bd-card-type1'),
 (781005,'bd-card-type17');
INSERT INTO characters(character_id,account_id,name) VALUES
 (781101,781001,'bd-card-ordinary'),
 (781102,781002,'bd-card-member'),
 (781103,781002,'bd-card-member-b'),
 (781104,781003,'bd-card-expired'),
 (781105,781004,'bd-card-type1'),
 (781106,781005,'bd-card-type17'),
 (781107,781002,'bd-card-full'),
 (781108,781002,'bd-card-paid'),
 (781109,781002,'bd-card-replay'),
 (781110,781002,'bd-card-pair'),
 (781111,781002,'bd-card-extra-item'),
 (781201,781002,'bd-card-timer'),
 (781202,781002,'bd-card-paid-first'),
 (781203,781002,'bd-card-free-first'),
 (781204,781002,'bd-card-paid-wait'),
 (781205,781002,'bd-card-retry'),
 (781206,781001,'bd-card-ordinary-flow'),
 (781207,781002,'bd-card-stale');
INSERT INTO account_premiums(account_id,premium_type,end_time) VALUES
 (781002,56,@expire),
 (781003,56,1),
 (781004,1,@expire),
 (781005,17,@expire);",
                ("@expire", expire));
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

        private static InventoryLease Unregister(InventoryLease lease)
        {
            if (lease != null)
                InventoryContext.Unregister(lease.SessionId, lease.CharacterId);
            return null;
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

        private static bool RequirePvf(ref int failures, string name)
        {
            var path = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return true;
            Check("[SKIP] " + name + ": PVF_ARCHIVE_PATH is not set", false, ref failures);
            return false;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            if (condition)
            {
                Console.WriteLine("[PASS] " + name);
                return;
            }

            failures++;
            Console.WriteLine("[FAIL] " + name);
        }

        private sealed class ScriptedRandom
        {
            private readonly Queue<int> _values;

            internal ScriptedRandom(params int[] values)
            {
                _values = new Queue<int>(values ?? Array.Empty<int>());
            }

            internal int Next(int maxValue)
            {
                if (_values.Count == 0)
                    throw new InvalidOperationException("no scripted rolls remain");
                return _values.Dequeue();
            }
        }

        private struct ExtraFamilies
        {
            internal int PaidCardCostOffset;
            internal int PaidCardCost;
            internal ExtraSeat[] Kind1;
            internal ExtraSeat[] Kind2;
            internal bool Kind1Empty => SeatsEmpty(Kind1, skip: -1);
            internal bool Kind2Empty => SeatsEmpty(Kind2, skip: -1);
        }

        private struct ExtraSeat
        {
            internal int Count;
            internal ExtraPair[] Pairs;
        }

        private struct ExtraPair
        {
            internal int ItemId;
            internal int StackCount;
        }

        private struct ClientKind2Projection
        {
            internal int GoldAmount;
            internal bool DrawsItemIcon;
            internal int ItemId;
            internal int ItemCount;
        }
    }
}
