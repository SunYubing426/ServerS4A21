using System;
using System.Linq;
using DfoServer.Game.Events.BurningTime;

namespace DfoServer.Network.Builders.Events
{
    internal static class BurningTimePacketBuilder
    {
        internal const int StateBodyLength = 65;

        internal static byte[] BuildStateBody(BurningTimeSnapshot snapshot)
        {
            var body = new byte[StateBodyLength];
            WriteUInt32(body, 0, (uint)BurningTimeConfig.EventId);
            body[4] = (byte)((snapshot?.EventEnabled ?? false) ? 1 : 0);
            body[5] = (byte)Math.Max(0, snapshot?.ActivePhaseIndex ?? 0);
            return body;
        }

        internal static byte[] BuildStatePacket(BurningTimeSnapshot snapshot)
            => GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.BURNING_TIME_BUFF,
                BuildStateBody(snapshot));

        internal static byte[] BuildPhasePacket(BurningTimePhase phase)
        {
            var body = new byte[StateBodyLength];
            if (phase?.Values != null)
            {
                var offset = 0;
                foreach (var value in phase.Values.Take(StateBodyLength / 4))
                {
                    WriteUInt32(body, offset, (uint)value);
                    offset += 4;
                }
            }

            return GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.BURNING_TIME_BUFF,
                body);
        }

        private static void WriteUInt32(byte[] body, int offset, uint value)
        {
            var bytes = BitConverter.GetBytes(value);
            Buffer.BlockCopy(bytes, 0, body, offset, bytes.Length);
        }
    }
}
