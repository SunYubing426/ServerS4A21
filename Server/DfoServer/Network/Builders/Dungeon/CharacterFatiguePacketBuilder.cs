using DfoServer.Game.Dungeon;

namespace DfoServer.Network.Builders
{
    // A21 CMD 0x0004 and NOTI FATIGUE share the same three i16s.
    // Live 86JP GameLog after ACK:
    //   "Fatigue {0}, FatigueMax {1}, usedFatigueMax {2}"
    // CipherPacket 0x0004 body[11..16] and NOTI 0x0024 body = used, max, used.
    // HUD remaining = FatigueMax - Fatigue([11]). Live:
    //   old ACK [11]=0 used=1 max=188 → HUD 188
    //   remaining ACK [11]=187 used=1 max=188 → HUD 1
    //   enter NOTI [11]=186 used=2 max=188 → HUD 2
    // [11] is the spent amount the HUD subtracts, not leftover remaining.
    internal static class CharacterFatiguePacketBuilder
    {
        internal static void WriteSnapshot(
            GamePacketWriter writer,
            CharacterFatigueSnapshot snapshot)
        {
            var snap = snapshot ?? new CharacterFatigueSnapshot(
                0,
                CharacterFatigueService.DefaultMaxFatigue);
            writer.WriteInt16(ToInt16(snap.Used));
            writer.WriteInt16(ToInt16(snap.Max));
            writer.WriteInt16(ToInt16(snap.Used));
        }

        internal static byte[] BuildNotification(CharacterFatigueSnapshot snapshot)
        {
            var writer = new GamePacketWriter();
            WriteSnapshot(writer, snapshot);
            return writer.ToArray();
        }

        private static short ToInt16(int value)
        {
            if (value < short.MinValue)
                return short.MinValue;
            if (value > short.MaxValue)
                return short.MaxValue;
            return (short)value;
        }
    }
}
