using DfoServer.Game.Dungeon;

namespace DfoServer.Network.Builders
{
    // A21 CMD 0x0004 and NOTI FATIGUE share the same three i16s.
    // Live 86JP GameLog after ACK:
    //   "Fatigue {0}, FatigueMax {1}, usedFatigueMax {2}"
    // CipherPacket 0x0004 body[11..16] = remaining, max, used.
    // Sending remaining=0 with used>0 still draws a full 188 bar, so [11]
    // is the HUD remaining, not leftover/bonus.
    internal static class CharacterFatiguePacketBuilder
    {
        internal static void WriteSnapshot(
            GamePacketWriter writer,
            CharacterFatigueSnapshot snapshot)
        {
            var snap = snapshot ?? new CharacterFatigueSnapshot(
                0,
                CharacterFatigueService.DefaultMaxFatigue);
            writer.WriteInt16(ToInt16(snap.Remaining));
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
