using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Inventory
{
    // 玩家交易的物品层执行服务: 报价校验(TryCreateOffer)与成交原子交换(TryExecuteAndPersist)。
    //   - TryCreateOffer 只读校验 + 快照: 金币走主栏 0 槽虚拟计数; 堆叠物品可部分数量;
    //     不可交易物(绑定/封装未拆/trade limit 耗尽/过期)在此拒绝。
    //   - TryExecuteAndPersist 在双方 lease 锁(按 cid 排序防死锁)内先在克隆背包上试算,
    //     试算通过才落到真实背包; 失败按逆序回滚, 持久化用单事务 SaveDirtyPair,
    //     事务失败同样回滚内存态, 保证双方要么都成、要么都不动。
    internal static class PlayerTradeRuntimeService
    {
        // 虚拟计数栏(itemspace 0x24): 堆叠材料的独立计数空间。
        internal const byte VirtualItemSpace = 36;

        internal static bool TryCreateOffer(
            InventoryService inventory,
            byte sourceList,
            short sourceSlot,
            int amount,
            out PlayerTradeOffer offer,
            out string failure)
        {
            offer = null;
            failure = null;
            if (inventory == null || amount <= 0)
                return Fail(out failure, "invalid amount");

            // 主栏 0 槽 = 金币(虚拟计数), 交易窗金币形态。
            if (sourceList == 0 && sourceSlot == 0)
            {
                if ((inventory.GetMainVirtualCount(sourceSlot)?.Count ?? 0) < amount)
                    return Fail(out failure, "insufficient gold");
                offer = new PlayerTradeOffer
                {
                    IsGold = true,
                    SourceList = sourceList,
                    SourceSlot = sourceSlot,
                    Amount = amount,
                };
                return true;
            }

            switch (sourceList)
            {
                case VirtualItemSpace:
                {
                    if (!inventory.TryGetMainVirtualCount(sourceSlot, out var item)
                        || item.Count < amount)
                        return Fail(out failure, "virtual source changed");
                    var core = ItemCore.Create(3, item.ItemId);
                    core.Count = amount;
                    offer = new PlayerTradeOffer
                    {
                        SourceList = sourceList,
                        SourceSlot = sourceSlot,
                        Amount = amount,
                        SourceSnapshot = core.Copy(),
                        Item = core,
                    };
                    return true;
                }
                case 0:
                    if (!InventoryService.IsVirtualMainSlot(sourceSlot))
                    {
                        var source = inventory.GetItem(
                            InventoryListType.Main,
                            sourceSlot)?.Copy();
                        if (source == null || source.IsEmpty)
                            return Fail(out failure, "source item missing");

                        var stackable = InventoryStackRuleService.IsStackable(source);
                        if ((!stackable && amount != 1)
                            || (stackable && source.Count < amount))
                            return Fail(out failure, "invalid item amount");
                        if (!IsTradable(source, out failure))
                            return false;

                        var transferred = source.Copy();
                        if (stackable)
                            transferred.Count = amount;
                        offer = new PlayerTradeOffer
                        {
                            SourceList = sourceList,
                            SourceSlot = sourceSlot,
                            Amount = amount,
                            SourceSnapshot = source,
                            Item = transferred,
                        };
                        return true;
                    }
                    goto default;
                default:
                    return Fail(out failure, "unsupported source list");
            }
        }

        internal static bool TryExecuteAndPersist(
            InventoryLease firstLease,
            IReadOnlyList<PlayerTradeOffer> firstOffers,
            InventoryLease secondLease,
            IReadOnlyList<PlayerTradeOffer> secondOffers,
            out PlayerTradeExecutionResult result)
        {
            result = new PlayerTradeExecutionResult();
            if (firstLease == null || secondLease == null
                || firstLease.Inventory == null || secondLease.Inventory == null
                || firstLease.CharacterId == secondLease.CharacterId)
            {
                result.Failure = "invalid inventory leases";
                return false;
            }

            // 固定加锁顺序(小 cid 先)防交叉死锁。
            var firstLock = firstLease.CharacterId < secondLease.CharacterId
                ? firstLease
                : secondLease;
            var secondLock = firstLock == firstLease ? secondLease : firstLease;
            lock (firstLock.SyncRoot)
            {
                lock (secondLock.SyncRoot)
                {
                    if (!TryApplyExchange(
                            firstLease.Inventory,
                            firstOffers,
                            secondLease.Inventory,
                            secondOffers,
                            out result))
                        return false;
                    if (!InventoryPersistenceService.SaveDirtyPair(
                            firstLease,
                            secondLease))
                    {
                        result.Failure = "inventory persistence rejected";
                        result.Rollback();
                        return false;
                    }
                    result.Commit();
                    return true;
                }
            }
        }

        // 先在克隆背包上完整试算一次, 成功才对真实背包执行(双保险: 试算能暴露
        // "扣完 A 放不进 B"这类容量问题, 而不必污染真实状态)。
        internal static bool TryApplyExchange(
            InventoryService firstInventory,
            IReadOnlyList<PlayerTradeOffer> firstOffers,
            InventoryService secondInventory,
            IReadOnlyList<PlayerTradeOffer> secondOffers,
            out PlayerTradeExecutionResult result)
        {
            result = new PlayerTradeExecutionResult();
            if (firstInventory == null || secondInventory == null)
            {
                result.Failure = "inventory missing";
                return false;
            }

            firstOffers = firstOffers ?? Array.Empty<PlayerTradeOffer>();
            secondOffers = secondOffers ?? Array.Empty<PlayerTradeOffer>();

            var firstPlanning =
                InventoryCompoundPlanning.CloneInventory(firstInventory);
            var secondPlanning =
                InventoryCompoundPlanning.CloneInventory(secondInventory);
            var planningResult = new PlayerTradeExecutionResult();
            if (!TryApplyExchangeCore(
                    firstPlanning,
                    firstOffers,
                    secondPlanning,
                    secondOffers,
                    planningResult))
            {
                result.Failure = planningResult.Failure;
                return false;
            }

            if (!TryApplyExchangeCore(
                    firstInventory,
                    firstOffers,
                    secondInventory,
                    secondOffers,
                    result))
            {
                result.Rollback();
                return false;
            }
            result.Success = true;
            return true;
        }

        private static bool TryApplyExchangeCore(
            InventoryService firstInventory,
            IReadOnlyList<PlayerTradeOffer> firstOffers,
            InventoryService secondInventory,
            IReadOnlyList<PlayerTradeOffer> secondOffers,
            PlayerTradeExecutionResult result)
        {
            if (!TryRemoveOffers(
                    firstInventory,
                    firstOffers,
                    result.FirstChanges,
                    result.RollbackActions,
                    out var failure)
                || !TryRemoveOffers(
                    secondInventory,
                    secondOffers,
                    result.SecondChanges,
                    result.RollbackActions,
                    out failure))
            {
                result.Failure = failure;
                return false;
            }

            var firstGold = SumGold(firstOffers);
            var secondGold = SumGold(secondOffers);
            if (!TryExchangeGold(
                    firstInventory,
                    firstGold,
                    secondGold,
                    result.FirstChanges,
                    result.RollbackActions,
                    out failure)
                || !TryExchangeGold(
                    secondInventory,
                    secondGold,
                    firstGold,
                    result.SecondChanges,
                    result.RollbackActions,
                    out failure))
            {
                result.Failure = failure;
                return false;
            }

            if (!TryInsertOffers(
                    firstInventory,
                    secondOffers,
                    result.FirstChanges,
                    result.RollbackActions,
                    out failure)
                || !TryInsertOffers(
                    secondInventory,
                    firstOffers,
                    result.SecondChanges,
                    result.RollbackActions,
                    out failure))
            {
                result.Failure = failure;
                return false;
            }
            return true;
        }

        private static bool TryRemoveOffers(
            InventoryService inventory,
            IEnumerable<PlayerTradeOffer> offers,
            InventoryMutationSet changes,
            ICollection<Action> rollback,
            out string failure)
        {
            failure = null;
            var seenSources = new HashSet<string>(StringComparer.Ordinal);
            foreach (var offer in offers.Where(
                         value => value != null && !value.IsGold))
            {
                var sourceKey = $"{offer.SourceList}:{offer.SourceSlot}";
                if (!seenSources.Add(sourceKey))
                    return Fail(out failure, "duplicate source item");

                var listType = InventoryListType.Main;
                InventoryMutationResult mutation;
                if (offer.SourceList == VirtualItemSpace)
                {
                    var before = inventory.GetMainVirtualCount(offer.SourceSlot);
                    if (before == null || offer.Item == null
                        || before.ItemId != offer.Item.ItemId
                        || before.Count < offer.Amount)
                        return Fail(out failure, "virtual source changed");
                    if (!InventoryDeleteService.TryDeleteForClient(
                            inventory,
                            listType,
                            offer.SourceSlot,
                            offer.Amount,
                            out mutation))
                        return Fail(out failure, "virtual source remove failed");
                    rollback.Add(() => inventory.SetMainVirtualCount(
                        offer.SourceSlot,
                        before.ItemId,
                        before.Count));
                    changes.AddSlot(InventoryListType.Main, offer.SourceSlot);
                    continue;
                }

                if (offer.SourceList != 0)
                    return Fail(out failure, "unsupported source list");

                var source = inventory.GetItem(listType, offer.SourceSlot)?.Copy();
                if (!MatchesSource(source, offer.SourceSnapshot, offer.Amount))
                    return Fail(out failure, "source item changed");
                if (!InventoryDeleteService.TryDeleteForClient(
                        inventory,
                        listType,
                        offer.SourceSlot,
                        offer.Amount,
                        out mutation))
                    return Fail(out failure, "source item remove failed");
                rollback.Add(() => inventory.SetItem(
                    listType,
                    offer.SourceSlot,
                    source.Copy()));
                changes.AddSlot(listType, offer.SourceSlot);
            }
            return true;
        }

        private static bool TryExchangeGold(
            InventoryService inventory,
            int outgoing,
            int incoming,
            InventoryMutationSet changes,
            ICollection<Action> rollback,
            out string failure)
        {
            failure = null;
            if (outgoing == 0 && incoming == 0)
                return true;

            const short goldSlot = 0;
            var before = inventory.GetMainVirtualCount(goldSlot)?.Count ?? 0;
            var balance = (long)before - outgoing + incoming;
            if (balance < 0 || balance > int.MaxValue)
                return Fail(out failure, "invalid gold balance");
            if (!inventory.SetMainVirtualCount(goldSlot, (int)balance))
                return Fail(out failure, "gold update failed");
            rollback.Add(() => inventory.SetMainVirtualCount(goldSlot, before));
            changes.AddSlot(InventoryListType.Main, goldSlot);
            return true;
        }

        private static bool TryInsertOffers(
            InventoryService inventory,
            IEnumerable<PlayerTradeOffer> offers,
            InventoryMutationSet changes,
            ICollection<Action> rollback,
            out string failure)
        {
            failure = null;
            foreach (var offer in offers.Where(
                         value => value != null && !value.IsGold))
            {
                if (offer.SourceList == VirtualItemSpace)
                {
                    var virtualBefore =
                        inventory.GetMainVirtualCount(offer.SourceSlot);
                    var currentCount = virtualBefore?.Count ?? 0;
                    var itemId = offer.Item?.ItemId ?? 0;
                    var total = (long)currentCount + offer.Amount;
                    if (virtualBefore == null
                        || itemId <= 0
                        || offer.Amount <= 0
                        || virtualBefore.ItemId != itemId
                        || total > int.MaxValue
                        || !inventory.SetMainVirtualCount(
                            offer.SourceSlot,
                            itemId,
                            (int)total))
                        return Fail(out failure, "recipient virtual item update failed");
                    rollback.Add(() => inventory.SetMainVirtualCount(
                        offer.SourceSlot,
                        virtualBefore.ItemId,
                        currentCount));
                    changes.AddSlot(InventoryListType.Main, offer.SourceSlot);
                    continue;
                }

                var transferred = PrepareTransferredItem(offer.Item);
                if (transferred == null)
                    return Fail(out failure, "recipient item missing");
                if (!InventoryInsertService.TryPlanInsertByDefaultRule(
                        inventory,
                        transferred,
                        offer.Amount,
                        out var plan)
                    || plan.RemainingCount != 0)
                    return Fail(out failure, $"recipient insert plan failed: {plan?.Error}");

                var before = inventory.GetItem(plan.ListType, plan.SlotIndex)?.Copy();
                if (!InventoryRewardGrantService.TryInsertExisting(
                        inventory,
                        transferred,
                        offer.Amount,
                        ItemCreateReason.PlayerTrade,
                        null,
                        out var result)
                    || !result.Success)
                    return Fail(out failure, "recipient insert failed");
                rollback.Add(() =>
                {
                    if (before == null)
                        inventory.RemoveItem(plan.ListType, plan.SlotIndex);
                    else
                        inventory.SetItem(
                            plan.ListType,
                            plan.SlotIndex,
                            before.Copy());
                });
                changes.AddRange(result.Changes);
            }
            return true;
        }

        private static ItemCore PrepareTransferredItem(ItemCore source)
        {
            if (source == null)
                return null;
            var transferred = source.Copy();
            // trade limit 类道具每次转移消耗一次可交易次数。
            if (IsTradeLimit(ItemMetadataResolver.Resolve(transferred.ItemId))
                && transferred.StackTradeCount > 0)
                transferred.StackTradeCount--;
            return transferred;
        }

        private static bool MatchesSource(
            ItemCore current,
            ItemCore snapshot,
            int amount)
        {
            if (current == null || snapshot == null
                || current.ItemId != snapshot.ItemId)
                return false;

            var stackable = InventoryStackRuleService.IsStackable(current);
            if ((!stackable && amount != 1)
                || (stackable && current.Count < amount))
                return false;

            // 数量之外的逐字段比对(防换位/替换)。
            var currentCopy = current.Copy();
            var snapshotCopy = snapshot.Copy();
            if (stackable)
            {
                currentCopy.Count = 0;
                snapshotCopy.Count = 0;
            }
            return currentCopy.ToBytes()
                .SequenceEqual(snapshotCopy.ToBytes());
        }

        private static bool IsTradable(ItemCore item, out string failure)
        {
            failure = null;
            if (item.TradeRestriction != 0)
                return Fail(out failure, "instance trade restriction");
            if (item.ExpireTime >= 1000000000
                && item.ExpireTime <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                return Fail(out failure, "item expired");

            var metadata = ItemMetadataResolver.Resolve(item.ItemId);
            var attachType = NormalizeAttachType(metadata?.AttachType);
            if (IsTradeLimit(metadata) && item.StackTradeCount == 0)
                return Fail(out failure, "trade limit exhausted");
            // 封装(sealing/seal)道具必须已拆封(SealFlag!=0)才可交易。
            if ((attachType == "sealing" || attachType == "seal")
                && item.SealFlag == 0)
                return Fail(out failure, "unsealed item");
            if (attachType.Contains("account")
                || attachType.Contains("character")
                || attachType.Contains("no trade")
                || attachType.Contains("not trade")
                || attachType.Contains("untrade")
                || attachType == "bind"
                || attachType == "bound")
                return Fail(out failure, "PVF attach restriction");
            return true;
        }

        private static bool IsTradeLimit(ItemMetadata metadata)
        {
            return NormalizeAttachType(metadata?.AttachType) == "trade limit";
        }

        private static string NormalizeAttachType(string value)
        {
            return (value ?? string.Empty)
                .Replace("`", string.Empty)
                .Replace("[", string.Empty)
                .Replace("]", string.Empty)
                .Trim()
                .ToLowerInvariant();
        }

        private static int SumGold(IEnumerable<PlayerTradeOffer> offers)
        {
            var total = offers.Where(value => value?.IsGold ?? false)
                .Sum(value => (long)value.Amount);
            return total <= int.MaxValue ? (int)Math.Max(0L, total) : int.MaxValue;
        }

        private static bool Fail(out string failure, string value)
        {
            failure = value;
            return false;
        }
    }
}
