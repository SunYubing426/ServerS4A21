using System;
using DfoServer.Game.Events.LoginReward;

namespace DfoServer.Network.Builders.Events
{
    internal static class LoginRewardPacketBuilder
    {
        internal const int StateBodyLength = 21;

        internal static byte[] BuildStateBody(LoginRewardSnapshot snapshot)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32((uint)LoginRewardConfig.EventId);
            writer.WriteInt32(snapshot?.TodayDayIndex ?? -1);
            writer.WriteUInt32(ToClientUInt32(snapshot?.ClaimedMask ?? 0));
            writer.WriteByte((byte)((snapshot?.AlreadyClaimedToday ?? false)
                ? 1
                : 0));
            writer.WriteByte((byte)((snapshot?.EventEnabled ?? false) ? 1 : 0));
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            return writer.ToArray();
        }

        internal static byte[] BuildStatePacket(LoginRewardSnapshot snapshot)
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
