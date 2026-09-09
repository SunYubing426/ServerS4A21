using DfoServer.Game.Inventory;
using DfoServer.Network;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public static class BuyItemAckBuilder
    {
        public static byte[] Build(InventoryMutationResult result, List<PurchaseCountUpdate> purchaseCountUpdates = null)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt32(result.UpdatedGold);
            writer.WriteInt32(result.UpdatedSp);
            writer.WriteInt32(0);
            writer.WriteInt32(result.UpdatedCoin);

            var protocolCount = ResolveProtocolCount(result, purchaseCountUpdates);
            if (result.CoreSnapshot != null && result.SlotIndex >= 0)
            {
                ItemListProtocolWriter.WriteCommonEntry84(
                    writer,
                    result.SlotIndex,
                    result.CoreSnapshot,
                    protocolCount);
            }
            else if (result.SlotIndex >= 0
                && result.ItemTemplateId > 0
                && result.RemainingStackCount > 0)
            {
                ItemListProtocolWriter.WriteVirtualCountEntry84(
                    writer,
                    result.SlotIndex,
                    result.ItemTemplateId,
                    protocolCount);
            }
            else
            {
                WriteLegacyItemSummary(writer, result);
            }

            var count = purchaseCountUpdates != null ? purchaseCountUpdates.Count : 0;
            writer.WriteByte((byte)count);
            if (purchaseCountUpdates != null)
            {
                foreach (var update in purchaseCountUpdates)
                {
                    writer.WriteInt32(update.ItemTemplateId);
                    writer.WriteInt32(update.RequestedCount);
                }
            }

            return writer.ToArray();
        }

        private static int ResolveProtocolCount(
            InventoryMutationResult result,
            List<PurchaseCountUpdate> purchaseCountUpdates)
        {
            if (result.RemainingStackCount <= 0)
                return result.RequestedCount;

            var purchaseCount = (int)result.RequestedCount;
            if (purchaseCountUpdates != null)
            {
                foreach (var update in purchaseCountUpdates)
                {
                    if (update.ItemTemplateId == result.ItemTemplateId)
                    {
                        purchaseCount = update.RequestedCount;
                        break;
                    }
                }
            }

            return Math.Max(0, result.RemainingStackCount - purchaseCount);
        }

        private static void WriteLegacyItemSummary(
            GamePacketWriter writer,
            InventoryMutationResult result)
        {
            writer.WriteInt16(result.SlotIndex);
            writer.WriteInt32(result.ItemTemplateId);
            writer.WriteInt32(result.InstanceValue);
            writer.WriteUInt16(result.Durability);
            writer.WriteByte(0);
            writer.WriteUInt16(0);
            writer.WriteInt32(result.ExpireTime);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteUInt16(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
        }

        public static byte[] BuildError(byte errorCode)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x00);
            writer.WriteByte(errorCode);
            return writer.ToArray();
        }
    }

    public sealed class PurchaseCountUpdate
    {
        public int ItemTemplateId { get; set; }

        public int RequestedCount { get; set; }
    }
}
