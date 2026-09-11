using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Pvp;
using System;

namespace DfoServer.SelfTests
{
    public static class A21PvpMapIndexProtocolSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== A21_PVP_MAP_INDEX_PROTOCOL selftest ===");
            var failures = 0;

            Check(
                "SET_PVP_MAP_INDEX opcode is 0x003B",
                (ushort)CmdPacketTypeA21.SET_PVP_MAP_INDEX == 0x003B
                && PvpRoomHandler.SetMapIndexCommandType == 0x003B,
                ref failures);

            Check(
                "parser accepts non-negative little-endian int16",
                SetPvpMapIndexRequest.TryParse(
                    BitConverter.GetBytes((short)123),
                    out var accepted)
                && accepted.MapIndex == 123,
                ref failures);
            Check(
                "parser rejects null and non-2-byte bodies",
                !SetPvpMapIndexRequest.TryParse(
                    null,
                    out _)
                && !SetPvpMapIndexRequest.TryParse(
                    new byte[] { 1 },
                    out _)
                && !SetPvpMapIndexRequest.TryParse(
                    new byte[] { 1, 0, 0 },
                    out _),
                ref failures);
            Check(
                "parser rejects negative map index",
                !SetPvpMapIndexRequest.TryParse(
                    BitConverter.GetBytes((short)-1),
                    out _),
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "A21_PVP_MAP_INDEX_PROTOCOL selftest passed."
                    : $"A21_PVP_MAP_INDEX_PROTOCOL selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine(
                $"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
