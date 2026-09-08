using System;
using DfoServer.Game.Events.OnlineAttendance;

namespace DfoServer.Network.Builders.Events
{
    internal static class OnlineAttendancePacketBuilder
    {
        internal const int StateBodyLength = 29;

        internal static byte[] BuildStateBody(OnlineAttendanceSnapshot snapshot)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32((uint)OnlineAttendanceConfig.EventId);
            writer.WriteUInt32(ToClientUInt32(
                (int)(snapshot?.DailyOnlineSeconds ?? 0)));
            writer.WriteUInt32(ToClientUInt32(snapshot?.DailyClaimMask ?? 0));
            writer.WriteUInt32(ToClientUInt32(snapshot?.SumClaimMask ?? 0));
            writer.WriteUInt32(ToClientUInt32(
                snapshot?.SumCompletedCount ?? 0));
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            writer.WriteByte((byte)((snapshot?.EventEnabled ?? false) ? 1 : 0));
            return writer.ToArray();
        }

        internal static byte[] BuildStatePacket(OnlineAttendanceSnapshot snapshot)
            => GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.INTEGRATE_EVENT_DATA,
                BuildStateBody(snapshot));

        internal static byte[] BuildAckPacket(ushort commandType)
            => GamePacketEnvelopeBuilder.Build(
                0x01,
                commandType,
                Array.Empty<byte>());

        private static uint ToClientUInt32(int value)
            => (uint)Math.Max(0, value);
    }
}
