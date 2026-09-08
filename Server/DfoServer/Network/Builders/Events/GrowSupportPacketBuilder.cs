using System;
using DfoServer.Game.Events.GrowSupport;

namespace DfoServer.Network.Builders.Events
{
    internal static class GrowSupportPacketBuilder
    {
        internal const int StateBodyLength = 25;

        internal static byte[] BuildStateBody(GrowSupportSnapshot snapshot)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32((uint)GrowSupportConfig.EventId);
            writer.WriteUInt32(ToClientUInt32(snapshot?.CharacterLevel ?? 0));
            writer.WriteUInt32(ToClientUInt32(snapshot?.LevelRewardClaimMask ?? 0));
            writer.WriteUInt32(ToClientUInt32(snapshot?.DungeonClearCount ?? 0));
            writer.WriteUInt32(ToClientUInt32(snapshot?.DungeonRewardClaimMask ?? 0));
            writer.WriteUInt32(0);
            writer.WriteByte((byte)((snapshot?.EventEnabled ?? false) ? 1 : 0));
            return writer.ToArray();
        }

        internal static byte[] BuildStatePacket(GrowSupportSnapshot snapshot)
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
