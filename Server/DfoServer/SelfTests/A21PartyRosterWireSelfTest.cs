using DfoServer.Game.Party;
using DfoServer.Network;
using DfoServer.Network.Builders.Party;
using System;

namespace DfoServer.SelfTests
{
    // PARTY_INFO (NOTI 0x0009) roster wire format, matched to the target A21
    // client's unpacked runtime parser at VA 0x01172000..0x011726B9
    // (reverse-engineered in MR !22, confirmed by live captures: a solo-create
    // roster block with info0==0 totals 65 bytes; malformed bodies made the
    // client exit its process). Byte layout per block:
    //   u16 blockCount; { u16 partyId; u8 type }
    //   type 0/1: info0; info0==0 -> empty raw dstr (u32 0); info1..info11
    //   type 0/2: 8 slots of { u16 uid; u8; u8; u8 } + 3 tail bytes
    //             (tail[1] is stored by the parser as the manager slot)
    //   type <=2: u8 hasExtra == 0 (no u32-pair records follow)
    public static class A21PartyRosterWireSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== A21_PARTY_ROSTER_WIRE selftest ===");
            var failures = 0;

            Check(
                "PARTY_INFO/SET_PARTY_INFO opcodes come from PacketTypesA21",
                (ushort)NotiPacketTypeA21.PARTY_INFO == 0x0009
                && (ushort)CmdPacketTypeA21.SET_PARTY_INFO == 0x000C,
                ref failures);

            var party = new Party(7) { LeaderUserId = 5 };
            party.TryAddMember(new PartyMember { UserId = 5, Name = "leader" });
            party.TryAddMember(new PartyMember { UserId = 6, Name = "memberA" });
            party.TryAddMember(new PartyMember { UserId = 7, Name = "memberB" });

            var roster = PartyInfoNotiBuilder.Build(party, 0);
            Check(
                "type-0 roster is the fixed 65-byte A21 block (info0==0)",
                roster.Length == 65
                && BitConverter.ToUInt16(roster, 0) == 1        // blockCount
                && BitConverter.ToUInt16(roster, 2) == 7         // partyId
                && roster[4] == 0                                // type
                && roster[5] == 0                                // info0
                && BitConverter.ToUInt32(roster, 6) == 0         // empty raw dstr
                && roster[10] == 0 && roster[11] == 4            // info1..info11
                && roster[16] == 5 && roster[20] == 0xFF,        // default block tail
                ref failures);

            Check(
                "type-0 roster writes 8 fixed slots, empty slot uid = 0xFFFF",
                BitConverter.ToUInt16(roster, 21) == 5           // slot0 uid
                && BitConverter.ToUInt16(roster, 26) == 6        // slot1 uid
                && BitConverter.ToUInt16(roster, 31) == 7        // slot2 uid
                && BitConverter.ToUInt16(roster, 36) == 0xFFFF   // slot3 empty
                && BitConverter.ToUInt16(roster, 56) == 0xFFFF,  // slot7 empty
                ref failures);

            Check(
                "roster tail byte[1] carries the leader slot, hasExtra = 0",
                roster[61] == 0 && roster[62] == 0 && roster[63] == 0
                && roster[64] == 0,
                ref failures);

            // Leader in a non-zero slot: the parser keeps no dedicated leader
            // field, so the manager slot must follow the leader's slot index.
            var partyLateLeader = new Party(8) { LeaderUserId = 9 };
            partyLateLeader.TryAddMember(new PartyMember { UserId = 8 });
            partyLateLeader.TryAddMember(new PartyMember { UserId = 9 });
            var lateLeaderRoster = PartyInfoNotiBuilder.Build(partyLateLeader, 0);
            Check(
                "manager slot follows a leader sitting in slot 1",
                lateLeaderRoster.Length == 65
                && lateLeaderRoster[62] == 1,
                ref failures);

            var inPlaceReplace = PartyInfoNotiBuilder.Build(party, 2);
            Check(
                "type-2 in-place replace shares the 8-slot layout without info",
                inPlaceReplace.Length == 49
                && BitConverter.ToUInt16(inPlaceReplace, 0) == 1
                && BitConverter.ToUInt16(inPlaceReplace, 2) == 7
                && inPlaceReplace[4] == 2
                && BitConverter.ToUInt16(inPlaceReplace, 5) == 5
                && inPlaceReplace[45] == 0
                && inPlaceReplace[46] == 0
                && inPlaceReplace[47] == 0
                && inPlaceReplace[48] == 0,
                ref failures);

            var clearBlock = PartyInfoNotiBuilder.Build(new Party(7), 3);
            Check(
                "type-3 window clear is blockCount + partyId + type only",
                clearBlock.Length == 5
                && BitConverter.ToUInt16(clearBlock, 0) == 1
                && BitConverter.ToUInt16(clearBlock, 2) == 7
                && clearBlock[4] == 3,
                ref failures);

            var list = PartyInfoNotiBuilder.BuildList(
                new[] { party },
                new[] { 3 });
            Check(
                "list build orders removed(type-3) blocks before type-0 rosters",
                list.Length == 2 + 3 + 63
                && BitConverter.ToUInt16(list, 0) == 2
                && BitConverter.ToUInt16(list, 2) == 3
                && list[4] == 3
                && BitConverter.ToUInt16(list, 5) == 7
                && list[7] == 0,
                ref failures);

            Console.WriteLine(failures == 0
                ? "PASS"
                : $"FAIL: {failures} check(s) failed");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine($"[{(condition ? "OK" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
