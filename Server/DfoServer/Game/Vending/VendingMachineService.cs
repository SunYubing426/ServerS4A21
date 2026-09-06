using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;
using DfoServer.Game.Premium;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Vending
{
    internal sealed record VendingDrawRequest(uint MachineId, uint GroupId, short CoinSlot);

    internal sealed record VendingRewardSnapshot(byte ListType, short Slot, ItemCore Core, int ItemId, int Count);

    internal sealed class VendingMachineResult
    {
        internal short CoinSlot { get; init; }
        internal int CoinRemaining { get; init; }
        internal VendingPrize Prize { get; init; }
        internal IReadOnlyList<VendingRewardSnapshot> Rewards { get; init; }
        internal int PremiumType { get; init; }
        internal long PremiumRemaining { get; init; }
    }

    internal sealed class VendingMachineService
    {
        private readonly Func<int, int> _next;
        internal VendingMachineService(Func<int, int> next = null) => _next = next ?? ServerRandom.Next;

        internal bool TryDraw(InventoryLease lease, Guid sessionId, int accountId,
            VendingDrawRequest request, VendingMachineDefinition definition,
            out VendingMachineResult result, out string rejection)
        {
            result = null;
            rejection = "invalid-owner-or-request";
            if (lease == null || request == null || definition == null || accountId <= 0
                || lease.AccountId != accountId || !InventoryContext.IsCurrentLease(lease, sessionId, lease.CharacterId)
                || (request.MachineId != 1 && request.MachineId != 3)
                || request.MachineId != definition.MachineId || request.GroupId != definition.GroupId)
                return false;

            VendingMachineResult committed = null;
            var reason = "commit-failed";
            bool ok;
            lock (lease.SyncRoot)
            {
                // 保存已有脏数据后再尝试抽奖；本次失败重载不能丢掉此前尚未刷盘的奖励。
                if (!InventoryPersistenceService.SaveDirty(lease))
                {
                    rejection = "prior-inventory-save-failed";
                    return false;
                }
                ok = OnlineInventoryMutationCommitCoordinator.TryCommit(lease, "black-diamond-vending", (connection, transaction) =>
                {
                    if (!InventoryContext.IsCurrentLease(lease, sessionId, lease.CharacterId))
                    {
                        reason = "stale-lease";
                        return false;
                    }
                    if (!PremiumService.HasActiveBlackDiamond(connection, transaction, accountId))
                    {
                        reason = "black-diamond-required";
                        return false;
                    }
                    var inventory = lease.Inventory;
                    var coin = inventory.GetItem(InventoryListType.Main, request.CoinSlot);
                    if (coin == null || coin.ItemId != definition.CoinItemId || coin.Count < 1
                        || !InventoryStackRuleService.IsStackable(coin)
                        || InventoryItemLifecycleService.IsExpired(coin, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
                    {
                        reason = "wrong-missing-or-expired-coin";
                        return false;
                    }
                    var prize = definition.Pick(_next(definition.TotalWeight));
                    if (!InventoryDeleteService.TryUseStackableForClient(inventory, InventoryListType.Main,
                            request.CoinSlot, definition.CoinItemId, out var debit))
                    {
                        reason = "coin-not-consumable";
                        return false;
                    }
                    if (!InventoryRewardGrantService.TryCreateAndInsert(inventory, prize.ItemId,
                            ItemCreateReason.NpcShopPurchase, prize.Count, out var grant)
                        || grant.GrantedCount != prize.Count)
                    {
                        reason = grant?.InsertPlan?.Error == InventoryInsertError.NoEmptySlot
                            && ItemMetadataResolver.TryResolveItemKind(prize.ItemId, out var prizeKind)
                            ? $"reward-container-full:{prizeKind}"
                            : "reward-grant-failed";
                        return false;
                    }

                    var rewards = new List<VendingRewardSnapshot>();
                    var premiumType = 0;
                    var premiumRemaining = 0L;
                    if (grant.Kind == InventoryRewardGrantKind.Premium)
                    {
                        if (!PremiumService.TryActivateContractInTransaction(connection, transaction, accountId,
                                prize.ItemId, prize.Count, out premiumType, out premiumRemaining))
                        {
                            reason = "premium-grant-failed";
                            return false;
                        }
                        rewards.Add(new VendingRewardSnapshot(6, 0, null, prize.ItemId, prize.Count));
                    }
                    else if (grant.Kind == InventoryRewardGrantKind.InventoryItem
                        || grant.Kind == InventoryRewardGrantKind.MainVirtualCount)
                    {
                        foreach (var change in grant.Changes.Slots)
                        {
                            var core = inventory.GetItem(change.ListType, change.SlotIndex);
                            if (change.ListType == InventoryListType.Main && InventoryService.IsVirtualMainSlot(change.SlotIndex))
                            {
                                var wallet = inventory.GetMainVirtualCount(change.SlotIndex);
                                if (wallet == null) return false;
                                rewards.Add(new VendingRewardSnapshot((byte)change.ListType, change.SlotIndex,
                                    null, wallet.ItemId, wallet.Count));
                            }
                            else
                            {
                                if (core == null) return false;
                                rewards.Add(new VendingRewardSnapshot((byte)change.ListType, change.SlotIndex,
                                    core.Copy(), core.ItemId, core.Count));
                            }
                        }
                    }
                    else
                    {
                        // 未验证的奖励路径不能只展示中奖却漏持久化/通知。
                        reason = "unsupported-reward-kind";
                        return false;
                    }
                    if (rewards.Count == 0 || rewards.Count > ushort.MaxValue) return false;
                    committed = new VendingMachineResult
                    {
                        CoinSlot = request.CoinSlot,
                        CoinRemaining = debit.RemainingStackCount,
                        Prize = prize,
                        Rewards = rewards.AsReadOnly(),
                        PremiumType = premiumType,
                        PremiumRemaining = premiumRemaining,
                    };
                    return true;
                });
            }
            // Coordinator 失败时会回滚 DB 并重载在线背包，不能向客户端发送预提交结果。
            if (!ok) { rejection = reason; return false; }
            result = committed;
            rejection = null;
            return true;
        }
    }
}
