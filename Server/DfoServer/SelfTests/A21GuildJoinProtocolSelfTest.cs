using DfoServer.Network;
using DfoServer.Network.Handlers;
using System;

namespace DfoServer.SelfTests
{
    public static class A21GuildJoinProtocolSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== A21_GUILD_JOIN_PROTOCOL selftest ===");
            var failures = 0;

            Check(
                "JOIN_GUILD_INFO request is 0x016D",
                (ushort)CmdPacketTypeA21.JOIN_GUILD_INFO == 0x016D,
                ref failures);
            Check(
                "JOIN_GUILD_INFO response is NOTI 0x0134",
                (ushort)NotiPacketTypeA21.JOIN_GUILD_INFO == 0x0134
                && GuildHandler.JoinGuildInfoNotificationType == 0x0134
                && (ushort)NotiPacketTypeA21.GROUP_MEMBER_LIST == 0x016D,
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "A21_GUILD_JOIN_PROTOCOL selftest passed."
                    : $"A21_GUILD_JOIN_PROTOCOL selftest failed: {failures}");
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
