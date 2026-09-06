using DfoServer.Game.Inventory;
using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.SecretShop
{
    internal readonly struct SecretShopSlotRefresh
    {
        internal SecretShopSlotRefresh(InventoryListType listType, short slot)
        {
            ListType = listType;
            Slot = slot;
        }

        internal InventoryListType ListType { get; }
        internal short Slot { get; }
    }

    internal sealed class SecretShopPurchaseResult
    {
        internal int ItemId { get; init; }
        internal int ItemCount { get; init; }
        internal int GoldCost { get; init; }
        internal int RequiredItemId { get; init; }
        internal int RequiredItemCount { get; init; }
        internal short CostItemSlot { get; init; }
        internal short AssignedSlot { get; init; }
        internal int ItemValue { get; init; }
        internal byte ExtData0 { get; init; }
        internal ushort Durability { get; init; }
        internal int UpdatedGold { get; init; }
        internal int CostItemRemainingCount { get; init; }
        internal int OfferRemainingCount { get; init; }
        internal IReadOnlyList<SecretShopSlotRefresh> SlotRefreshes { get; init; }
            = Array.Empty<SecretShopSlotRefresh>();
    }

    internal sealed class SecretShopPurchaseService
    {
        internal SecretShopPurchaseService()
        {
        }

        internal bool TryPurchase(
            InventoryLease lease,
            SecretShopOffer offer,
            int itemId,
            int requestedCount,
            out SecretShopPurchaseResult result)
        {
            result = null;
            if (lease?.Inventory == null
                || offer == null
                || !offer.IsSecretShop
                || itemId <= 0)
            {
                return false;
            }

            InventoryMutationResult mutation = null;
            List<InventoryMutationResult> mutations = null;
            try
            {
                if (!offer.TryCompletePurchase(
                        itemId,
                        requestedCount,
                        (item, purchaseCount) =>
                        {
                            if (item.Count <= 0 || item.Price < 0 || item.RawFlag is not (0 or 1))
                                return false;

                            var usesItemCurrency = item.RawFlag == 1;
                            // Validate the complete purchase before any mutation is committed.
                            _ = checked(item.Price * purchaseCount);
                            var independentCores = RequiresIndependentCores(item.ItemId);
                            var grantUnits = independentCores ? purchaseCount : 1;
                            var perGrantCount = independentCores ? 1 : purchaseCount;
                            return TryCommitPurchase(
                                lease,
                                "secret-shop-buy",
                                (connection, transaction) =>
                                {
                                    var applied = new List<InventoryMutationResult>(grantUnits);
                                    for (var i = 0; i < grantUnits; i++)
                                    {
                                        var unitCost = checked(item.Price * perGrantCount);
                                        if (!InventoryShopRuntimeService.TryBuySecretShopItem(
                                                lease.Inventory,
                                                item.ItemId,
                                                perGrantCount,
                                                usesItemCurrency ? 0 : unitCost,
                                                usesItemCurrency ? item.RequiredItemId : 0,
                                                usesItemCurrency ? unitCost : 0,
                                                out var granted)
                                            || granted == null)
                                        {
                                            return false;
                                        }

                                        applied.Add(granted);
                                    }

                                    if (applied.Count != grantUnits)
                                        return false;

                                    mutations = applied;
                                    mutation = applied[applied.Count - 1];
                                    return true;
                                });
                        },
                        out var purchased,
                        out var purchasedCount,
                        out var remainingCount))
                    return false;

                if (mutation == null || mutations == null || mutations.Count == 0)
                    return false;

                var totalCost = checked(purchased.Price * purchasedCount);
                result = new SecretShopPurchaseResult
                {
                    ItemId = purchased.ItemId,
                    ItemCount = purchasedCount,
                    GoldCost = purchased.RawFlag == 0 ? totalCost : 0,
                    RequiredItemId = purchased.RawFlag == 1 ? purchased.RequiredItemId : 0,
                    RequiredItemCount = purchased.RawFlag == 1 ? totalCost : 0,
                    CostItemSlot = mutation.CostItemSlotIndex,
                    AssignedSlot = mutation.SlotIndex,
                    ItemValue = mutation.InstanceValue,
                    ExtData0 = mutation.ExtData0,
                    Durability = mutation.Durability,
                    UpdatedGold = mutation.UpdatedGold,
                    CostItemRemainingCount = mutation.CostItemRemainingCount,
                    OfferRemainingCount = remainingCount,
                    SlotRefreshes = CollectSlotRefreshes(mutations),
                };
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[SecretShop] purchase transaction failed: char={lease?.CharacterId ?? 0} item={itemId} error={ex.Message}");
                result = null;
                return false;
            }
        }

        private static bool TryCommitPurchase(
            InventoryLease lease,
            string operation,
            Func<SqliteConnection, SqliteTransaction, bool> apply)
        {
            lock (lease.SyncRoot)
            {
                // A failed multi-core purchase reloads the persisted inventory.
                // Preserve unrelated rewards earned before this purchase first.
                if (!InventoryPersistenceService.SaveDirty(lease))
                    return false;
                return OnlineInventoryMutationCommitCoordinator.TryCommit(lease, operation, apply);
            }
        }

        private static bool RequiresIndependentCores(int itemId)
        {
            if (InventoryService.TryResolveMainVirtualSlotByItemId(itemId, out _, out _))
                return false;

            var metadata = ItemMetadataResolver.Resolve(itemId);
            return metadata != null && !metadata.IsStackable;
        }

        internal static IReadOnlyList<SecretShopSlotRefresh> CollectSlotRefreshes(
            IReadOnlyList<InventoryMutationResult> mutations)
        {
            var slots = new List<SecretShopSlotRefresh>();
            if (mutations == null)
                return slots;

            foreach (var mutation in mutations)
                AppendMutationSlots(slots, mutation);

            return slots;
        }

        private static void AppendMutationSlots(
            List<SecretShopSlotRefresh> slots,
            InventoryMutationResult mutation)
        {
            if (mutation == null || slots == null)
                return;

            AddSlot(slots, mutation.ListType, mutation.SlotIndex);
            if (mutation.CostItemTemplateId > 0)
                AddSlot(slots, InventoryListType.Main, mutation.CostItemSlotIndex);

            if (mutation.ExtraResults == null)
                return;

            foreach (var extra in mutation.ExtraResults)
                AppendMutationSlots(slots, extra);
        }

        private static void AddSlot(
            List<SecretShopSlotRefresh> slots,
            InventoryListType listType,
            short slot)
        {
            if (slot < 0)
                return;

            for (var i = 0; i < slots.Count; i++)
            {
                if (slots[i].ListType == listType && slots[i].Slot == slot)
                    return;
            }

            slots.Add(new SecretShopSlotRefresh(listType, slot));
        }
    }
}
