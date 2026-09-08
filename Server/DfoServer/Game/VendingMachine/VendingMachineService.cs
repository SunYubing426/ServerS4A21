using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;
using DfoServer.Game.Premium;
using DfoServer.Infrastructure;

namespace DfoServer.Game.VendingMachine
{
    internal sealed class VendingMachineResult
    {
        internal short SourceSlot { get; init; }
        internal int RemainingTokens { get; set; }
        internal VendingReward Reward { get; init; }
        internal bool DeliveredToMailbox { get; set; }
        internal int PremiumType { get; set; }
        internal long PremiumRemaining { get; set; }
        internal InventoryMutationSet Changes { get; } = new();
        internal List<(short slot, ItemCore core)> MainEntries { get; } = new();
    }

    internal sealed class VendingMachineService
    {
        private readonly IInventoryOverflowRewardSink _overflow;
        private readonly Func<VendingMachineCatalog> _catalog;
        private readonly Func<int, int> _random;

        internal VendingMachineService(IInventoryOverflowRewardSink overflow,
            Func<VendingMachineCatalog> catalog = null, Func<int, int> random = null)
        {
            _overflow = overflow ?? throw new ArgumentNullException(nameof(overflow));
            _catalog = catalog ?? VendingMachineCatalog.Load;
            _random = random ?? ServerRandom.Next;
        }

        internal bool TryUse(InventoryLease lease, Guid sessionId, int accountId,
            int machineId, int groupId, short sourceSlot, out VendingMachineResult result)
        {
            result = null;
            if (lease == null || !lease.IsOwnedBy(sessionId) || accountId <= 0
                || lease.Inventory.AccountId != accountId
                || !InventoryContext.IsCurrentLease(lease, sessionId, lease.CharacterId)) return false;

            try
            {
                if (!_catalog().TryGet(machineId, groupId, out var group)) return false;
                VendingMachineResult committedResult = null;
                // Preserve unrelated pending inventory changes before rollback can reload from DB.
                lock (lease.SyncRoot)
                {
                    if (!InventoryContext.IsCurrentLease(lease, sessionId, lease.CharacterId)
                        || !ValidMaterial(lease.Inventory, sourceSlot, group)
                        || !InventoryPersistenceService.SaveDirty(lease)) return false;

                    var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(lease, "vending-machine",
                        (connection, transaction) =>
                        {
                            var inventory = lease.Inventory;
                            if (!InventoryContext.IsCurrentLease(lease, sessionId, lease.CharacterId)
                                || !PremiumService.HasActiveBlackDiamond(connection, transaction, accountId)
                                || !ValidMaterial(inventory, sourceSlot, group)) return false;

                            var reward = group.Roll(_random(group.TotalWeight));
                            if (!InventoryRewardGrantService.TryCreateOnly(reward.ItemId, ItemCreateReason.PackageOpen,
                                reward.Count, out var created)) return false;
                            if (!InventoryDeleteService.TryConsumeFromSlot(inventory, InventoryListType.Main,
                                sourceSlot, group.MaterialId, group.MaterialCount, out var consumed)) return false;

                            var pending = new VendingMachineResult
                            {
                                SourceSlot = sourceSlot, RemainingTokens = consumed.RemainingCount, Reward = reward,
                            };
                            pending.Changes.AddRange(consumed.Changes);
                            var requests = new[] { created.Kind == InventoryRewardGrantKind.InventoryItem
                                ? InventoryRewardGrantRequest.Existing(created.Core, reward.Count, ItemCreateReason.PackageOpen)
                                : InventoryRewardGrantRequest.Create(reward.ItemId, reward.Count, ItemCreateReason.PackageOpen) };
                            if (!InventoryRewardGrantService.TryPlanBatch(inventory, requests, out var plan))
                            {
                                if (plan.Error != InventoryRewardGrantError.InsertPlanFailed
                                    || !new TransactionBoundInventoryOverflowRewardSink(connection, transaction, _overflow)
                                        .TryDeliver(inventory, requests, out _)) return false;
                                pending.DeliveredToMailbox = true;
                            }
                            else
                            {
                                if (!InventoryRewardGrantService.TryApplyPreparedBatch(inventory, plan, out var granted)) return false;
                                pending.Changes.AddRange(granted.Changes);
                                foreach (var change in granted.Changes.Slots)
                                {
                                    if (change.ListType != InventoryListType.Main) continue;
                                    var core = inventory.GetItem(change.ListType, change.SlotIndex);
                                    if (core != null) pending.MainEntries.Add((change.SlotIndex, core.Copy()));
                                }
                                if (created.Kind == InventoryRewardGrantKind.Premium)
                                {
                                    if (!PremiumService.TryActivateContractInTransaction(connection, transaction,
                                        accountId, reward.ItemId, reward.Count, out var type, out var remaining)) return false;
                                    pending.PremiumType = type;
                                    pending.PremiumRemaining = remaining;
                                }
                            }
                            committedResult = pending;
                            return true;
                        });
                    if (!committed) return false;
                }
                result = committedResult;
                return result != null;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[VendingMachine] rejected cid={lease.CharacterId} machine={machineId}: {ex.Message}");
                return false;
            }
        }

        private static bool ValidMaterial(InventoryService inventory, short slot, VendingMachineGroup group)
        {
            var source = inventory.GetItem(InventoryListType.Main, slot);
            return source != null && source.ItemId == group.MaterialId
                && InventoryStackRuleService.IsStackable(source) && source.Value >= group.MaterialCount
                && !InventoryItemLifecycleService.IsExpired(source, InventoryItemLifecycleService.UtcNowUnixSeconds());
        }
    }
}


