using DfoServer.Game.Dungeon;

namespace DfoServer.Network.Builders
{
    internal static class DungeonFatigueStateBodyBuilder
    {
        // A21 NOTI FATIGUE(0x0024): used, limit, actorAux, displayUsed, actorExtra.
        internal static byte[] Build(DungeonFatigueSnapshot state)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(state.Used);
            writer.WriteUInt16(state.Limit);
            writer.WriteUInt16(0);
            writer.WriteUInt16(state.Used);
            writer.WriteUInt16(0);
            return writer.ToArray();
        }
    }
}
