using System;
using System.Buffers.Binary;

namespace DfoServer.Network.Parsers
{
    internal sealed record VendingMachineUseRequest(int MachineId, int GroupId, short SourceSlot)
    {
        internal static bool TryParse(byte[] body, out VendingMachineUseRequest request)
        {
            request = null;
            // Captured A21 bodies: 03-00-00-00-01-00-00-00-7C-00 and
            // 01-00-00-00-01-00-00-00-7B-00. One material purchase per request.
            if (body == null || body.Length != 10) return false;
            var machine = BinaryPrimitives.ReadInt32LittleEndian(body);
            var group = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4));
            var slot = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(8));
            if ((machine != 1 && machine != 3) || group <= 0 || slot < 0) return false;
            request = new(machine, group, slot);
            return true;
        }
    }
}


