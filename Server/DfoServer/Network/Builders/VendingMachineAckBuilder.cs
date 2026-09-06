using System;
using DfoServer.Game.Vending;

namespace DfoServer.Network.Builders
{
    internal static class VendingMachineAckBuilder
    {
        // A21 11435F0: status 后读源槽、剩余量、展示奖励、两个保留 i32、记录数。
        internal static byte[] BuildSuccess(VendingMachineResult result)
        {
            if (result?.Prize == null || result.Rewards == null)
                throw new ArgumentNullException(nameof(result));
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt16(result.CoinSlot);
            writer.WriteInt32(result.CoinRemaining);
            writer.WriteInt32(result.Prize.ItemId);
            writer.WriteInt32(result.Prize.Count);
            writer.WriteInt32(0);
            writer.WriteInt32(0);
            writer.WriteUInt16(checked((ushort)result.Rewards.Count));
            foreach (var reward in result.Rewards)
            {
                writer.WriteByte(reward.ListType);
                if (reward.ListType == 6)
                {
                    writer.WriteInt32(reward.ItemId);
                    writer.WriteInt32(reward.Count);
                }
                else if (reward.Core == null)
                    ItemListProtocolWriter.WriteVirtualCountEntry84(writer, reward.Slot, reward.ItemId, reward.Count);
                else
                    ItemListProtocolWriter.WriteCommonEntry84(writer, reward.Slot, reward.Core);
            }
            return writer.ToArray();
        }

        // A21 1143F6B: error 4 selects native string 0xFF (with its error prefix), error 1 selects 0xBBA.
        // 0x2EA is a different resource ID, not a vending failure code; do not substitute a text notice.
        internal static byte[] BuildFailure(bool inventoryFull = false)
            => new byte[] { 0, inventoryFull ? (byte)4 : (byte)1 };
    }
}
