using DfoServer.Game.VendingMachine;

namespace DfoServer.Network.Builders
{
    internal static class VendingMachineResultBuilder
    {
        internal static byte[] Build(VendingMachineResult result)
        {
            if (result == null) return new byte[] { 0, 1 };
            // A21 live client CMD callback 0x011435F0 (registered at 0x01145396).
            // Primary icon is selected by itemId/count via 0x02346750, independently
            // of inventory entries. Two reserved dwords are read even with no entries.
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt16(result.SourceSlot);
            writer.WriteInt32(result.RemainingTokens);
            writer.WriteInt32(result.Reward.ItemId);
            writer.WriteInt32(result.Reward.Count);
            writer.WriteInt32(0);
            writer.WriteInt32(0);
            writer.WriteUInt16(checked((ushort)result.MainEntries.Count));
            foreach (var (slot, core) in result.MainEntries)
            {
                writer.WriteByte(0); // main inventory; client reads exactly 0x65 bytes next
                ItemListProtocolWriter.WriteCommonEntry84(writer, slot, core);
            }
            return writer.ToArray();
        }
    }
}


