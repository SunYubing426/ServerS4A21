using System;
using System.Buffers.Binary;
using DfoServer.Game.Vending;

namespace DfoServer.Network.Parsers.Vending
{
    internal static class VendingMachineRequest
    {
        // A21 23495A0: machine-kind + 1, group 1, inventory coin slot.
        internal static bool TryParse(byte[] body, out VendingDrawRequest request)
        {
            request = null;
            if (body == null || body.Length != 10) return false;
            var machine = BinaryPrimitives.ReadUInt32LittleEndian(body);
            var group = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
            var slot = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(8));
            if ((machine != 1 && machine != 3) || group != 1 || slot < 0) return false;
            request = new VendingDrawRequest(machine, group, slot);
            return true;
        }
    }
}
