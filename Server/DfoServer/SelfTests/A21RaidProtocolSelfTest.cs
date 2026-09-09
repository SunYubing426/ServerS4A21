using System;
using System.Linq;
using DfoServer.Game.Raid;
using DfoServer.Game.Party;
using DfoServer.Network;
using DfoServer.Network.Builders.Raid;
using DfoServer.Network.Handlers;

namespace DfoServer.SelfTests
{
    public static class A21RaidProtocolSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== A21_RAID_PROTOCOL selftest ===");
            var failures = 0;
            CheckSituationWire(ref failures);
            CheckRewardList(ref failures);
            Check("failure result has current-client ten-byte layout and disables rewards",
                RaidPacketBuilder.BuildRaidResult(1, 1, 2400, 4, 0, 1).SequenceEqual(
                    new byte[] { 1, 1, 0x60, 9, 0, 0, 4, 0, 0, 1 }), ref failures);
            Check("successful result preserves rank and reward eligibility",
                RaidPacketBuilder.BuildRaidResult(0, 0, 256, 513, 3, 0).SequenceEqual(
                    new byte[] { 0, 0, 0, 1, 0, 0, 1, 2, 3, 0 }), ref failures);
            Check("death display saturates without shifting reward flag",
                RaidPacketBuilder.BuildRaidResult(1, 0, 0, uint.MaxValue, 0, 1).SequenceEqual(
                    new byte[] { 1, 0, 0, 0, 0, 0, 255, 255, 0, 1 }), ref failures);
            CheckPreparation(ref failures);
            CheckLeaderLifecycle(ref failures);
            CheckPhaseIsolation(ref failures);
            CheckLivePartyAssignment(ref failures);
            var costs = RaidPacketBuilder.BuildEntryCostInfo(new[] {
                new RaidEntryCostStatus { UserId = 4, Ready = true, OwnedCount = 9 },
                new RaidEntryCostStatus { UserId = 5, Ready = true, OwnedCount = 1 },
                new RaidEntryCostStatus { UserId = 10, Ready = false, OwnedCount = 0 }
            });
            Check("three material rows preserve client five-byte record boundaries",
                costs.SequenceEqual(new byte[] { 3,0,0,0, 4,0,1,9,0, 5,0,1,1,0, 10,0,0,0,0 }), ref failures);
            Check("empty material list preserves u32 header",
                RaidPacketBuilder.BuildEntryCostInfo(Array.Empty<RaidEntryCostStatus>()).SequenceEqual(new byte[4]), ref failures);
            Check("material display count saturates without wrapping",
                RaidPacketBuilder.BuildEntryCostInfo(new[] {
                    new RaidEntryCostStatus { UserId = ushort.MaxValue, Ready = true, OwnedCount = uint.MaxValue }
                }).SequenceEqual(new byte[] { 1,0,0,0, 255,255,1,255,255 }), ref failures);
            var readyPacket = TownHandler.BuildRaidUserFinishLoadPacket();
            Check("raid finish-load notification has no body and uses the registered A21 opcode",
                readyPacket.Length == 15 && readyPacket[0] == 0
                    && BitConverter.ToUInt16(readyPacket, 1) == (ushort)NotiPacketTypeA21.RAID_USER_FINISH_LOAD
                    && BitConverter.ToUInt32(readyPacket, 3) == 15, ref failures);
            var arrival = new DfoServer.Game.Session.PlayerContext {
                CharacterId = 4, CurTownId = 19, UserState = 0, TownPresenceReady = true
            };
            Check("raid town arrival permits completion notification",
                TownHandler.ShouldNotifyRaidTownLoaded(10200, arrival), ref failures);
            Check("ordinary channel does not receive raid completion notification",
                !TownHandler.ShouldNotifyRaidTownLoaded(10010, arrival), ref failures);
            arrival.TownPresenceReady = false;
            Check("unready town state does not unlock raid search",
                !TownHandler.ShouldNotifyRaidTownLoaded(10200, arrival), ref failures);
            arrival.TownPresenceReady = true;
            arrival.UserState = 1;
            Check("non-town state does not unlock raid search",
                !TownHandler.ShouldNotifyRaidTownLoaded(10200, arrival), ref failures);
            arrival.UserState = 0;
            arrival.CharacterId = 0;
            Check("missing character does not unlock raid search",
                !TownHandler.ShouldNotifyRaidTownLoaded(10200, arrival), ref failures);
            var invite = RaidPacketBuilder.BuildPeerInvite(4, 2919);
            Check("raid invitation includes all three client-read tail fields",
                invite.SequenceEqual(new byte[] { 4, 0, 10, 0x67, 0x0B, 0, 0, 0, 0, 0, 0, 0, 0 }), ref failures);
            Check("raid invitation preserves full inviter and peer widths",
                RaidPacketBuilder.BuildPeerInvite(ushort.MaxValue, -1).SequenceEqual(
                    new byte[] { 255, 255, 10, 255, 255, 255, 255, 0, 0, 0, 0, 0, 0 }), ref failures);
            var invitePacket = GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.REQUEST_PEER, invite);
            Check("raid invitation uses notification envelope with 13-byte body",
                invitePacket.Length == 28 && invitePacket[0] == 0
                    && BitConverter.ToUInt16(invitePacket, 1) == (ushort)NotiPacketTypeA21.REQUEST_PEER
                    && BitConverter.ToUInt32(invitePacket, 3) == 28
                    && invitePacket.Skip(15).SequenceEqual(invite), ref failures);

            // Captured A21 CREATE_RAID request: dstr(3) + ASCII title "123".
            var captured = new byte[] { 0x00, 0x03, 0x00, 0x00, 0x00, 0x31, 0x32, 0x33 };
            Check(
                "CREATE_RAID accepts captured dstr body",
                RaidHandler.TryReadTitle(captured, out var capturedTitle)
                    && capturedTitle.SequenceEqual(new byte[] { 0x31, 0x32, 0x33 }),
                ref failures);

            var empty = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00 };
            Check(
                "CREATE_RAID accepts an empty title dstr",
                RaidHandler.TryReadTitle(empty, out var emptyTitle)
                    && emptyTitle.Length == 0,
                ref failures);

            Check(
                "CREATE_RAID rejects truncated dstr",
                !RaidHandler.TryReadTitle(new byte[] { 0x00, 0x03, 0x00, 0x00, 0x00, 0x31 }, out _),
                ref failures);

            Check(
                "CREATE_RAID rejects impossible dstr length",
                !RaidHandler.TryReadTitle(new byte[] { 0x00, 0x09, 0x00, 0x00, 0x00, 0x31, 0x32 }, out _),
                ref failures);

            var member = new RaidMemberSnapshot
            {
                UserId = 4,
                CharacterId = 4,
                NameBytes = new byte[] { 0x31 },
                Job = 0x23,
                GrowType = 0,
                PartyIndex = 0
            };
            var directoryEntry = new RaidDirectoryEntry
            {
                RaidId = 0x00C80004, TitleBytes = new byte[] { 0x52, 0x31 },
                State = 2, StateArgument = 1, Leader = member, MemberCount = 1
            };
            var directory = RaidPacketBuilder.BuildRaidDirectory(new[] { directoryEntry });
            Check("raid directory matches the complete-object reader and member count",
                directory.Length == 40 && BitConverter.ToUInt32(directory, 0) == 1
                    && BitConverter.ToUInt32(directory, 4) == 0x00C80004
                    && BitConverter.ToUInt32(directory, 8) == 2
                    && directory[12] == 0x52 && directory[13] == 0x31
                    && directory[14] == 0 && directory[15] == 2 && directory[16] == 1
                    && BitConverter.ToUInt16(directory, 21) == 4
                    && directory[39] == 1, ref failures);
            var directoryPacket = RaidHandler.BuildRaidDirectoryPacket(new[] { directoryEntry });
            Check("actual directory sender targets RAID_LIST, not RAID_WAITING_LIST",
                directoryPacket[0] == 0 && directoryPacket.Length == 55
                    && BitConverter.ToUInt16(directoryPacket, 1) == (ushort)NotiPacketTypeA21.RAID_LIST
                    && BitConverter.ToUInt32(directoryPacket, 3) == 55
                    && directoryPacket.Skip(15).SequenceEqual(directory), ref failures);
            var emptyDirectory = RaidHandler.BuildRaidDirectoryPacket(Array.Empty<RaidDirectoryEntry>());
            Check("empty directory uses the same dispatch and a u32 zero count",
                emptyDirectory.Length == 19
                    && BitConverter.ToUInt16(emptyDirectory, 1) == (ushort)NotiPacketTypeA21.RAID_LIST
                    && emptyDirectory.Skip(15).SequenceEqual(new byte[4]), ref failures);
            var repeatedDirectory = RaidHandler.BuildRaidDirectoryPacket(new[] { directoryEntry, directoryEntry });
            Check("multiple directory entries retain record boundaries",
                repeatedDirectory.Length == 91 && BitConverter.ToUInt32(repeatedDirectory, 15) == 2
                    && repeatedDirectory.Skip(19).Take(36).SequenceEqual(repeatedDirectory.Skip(55)), ref failures);
            var memberRefresh = RaidPacketBuilder.BuildRaidCreate(
                4,
                new byte[] { 0x52, 0x31 },
                0,
                0,
                member,
                new[] { member });
            Check(
                "RAID_MODIFY uses the A21 compact raid object and member layout",
                memberRefresh.Length == 62
                    && BitConverter.ToUInt32(memberRefresh, 0) == 4
                    && BitConverter.ToUInt32(memberRefresh, 4) == 0
                    && BitConverter.ToUInt32(memberRefresh, 8) == 4
                    && BitConverter.ToInt32(memberRefresh, 12) == 2
                    && memberRefresh[16] == 0x52
                    && memberRefresh[17] == 0x31
                    && BitConverter.ToUInt16(memberRefresh, 25) == 4
                    && memberRefresh[27] == 1
                    && BitConverter.ToUInt32(memberRefresh, 37) == 4
                    && memberRefresh[43] == 1
                    && BitConverter.ToUInt16(memberRefresh, 44) == 4
                    && memberRefresh[52] == 0x23,
                ref failures);

            var memberUpdate = RaidPacketBuilder.BuildRaidMembersUpdate(4, new[] { member });
            var activeObject = RaidPacketBuilder.BuildRaidCreate(
                4, new byte[] { 0x52, 0x31 }, 2, 1, member, new[] { member });
            Check("full raid refresh preserves Anton type and active phase",
                activeObject.Length == 62 && activeObject[18] == 0
                    && activeObject[19] == 2 && activeObject[20] == 1
                    && BitConverter.ToUInt32(activeObject, 21) == 0, ref failures);
            Check(
                "raid member refresh uses RAID_MODIFY operation 3 layout",
                memberUpdate.Length == 27
                    && BitConverter.ToUInt32(memberUpdate, 0) == 4
                    && BitConverter.ToUInt32(memberUpdate, 4) == 3
                    && memberUpdate[8] == 1
                    && BitConverter.ToUInt16(memberUpdate, 9) == 4
                    && memberUpdate[11] == 1
                    && BitConverter.ToUInt32(memberUpdate, 21) == 4,
                ref failures);

            member.PartyIndex = 1;
            var assignments = RaidPacketBuilder.BuildRaidMembersUpdate(4, new[] { member });
            Check(
                "RAID_MODIFY compact member record preserves party assignment",
                assignments.Length == 27 && assignments[19] == 1,
                ref failures);

            // Live A21 readers verified 2026-09-06: 0x0115E9A0,
            // 0x0114EFE0 and 0x01148140. These tests deliberately do not
            // reuse a server-side parser that could share the same width bug.
            Check("RAID_STATE first phase is exactly two bytes",
                RaidPacketBuilder.BuildRaidState(2, 0).SequenceEqual(new byte[] { 2, 0 }), ref failures);
            Check("RAID_STATE second phase retains its argument",
                RaidPacketBuilder.BuildRaidState(2, 1).SequenceEqual(new byte[] { 2, 1 }), ref failures);
            Check("RAID_STATE failure argument does not shift",
                RaidPacketBuilder.BuildRaidState(3, 1).SequenceEqual(new byte[] { 3, 1 }), ref failures);
            Check("RAID_STATE rejects overflowing state",
                RejectsRange(() => RaidPacketBuilder.BuildRaidState(256, 0)), ref failures);
            Check("RAID_STATE rejects overflowing argument",
                RejectsRange(() => RaidPacketBuilder.BuildRaidState(2, 256)), ref failures);

            var timer = RaidPacketBuilder.BuildSetTimer(1, 210, 2400, 0x65010203);
            Check("RAID_SET_TIMER follows current 13-byte client reader",
                timer.SequenceEqual(new byte[] { 1, 210, 0, 0, 0, 3, 2, 1, 0x65, 0x60, 9, 0, 0 }), ref failures);
            Check("RAID_SET_TIMER ready countdown is three seconds",
                RaidPacketBuilder.BuildSetTimer(0, 0, 3, 1003).SequenceEqual(
                    new byte[] { 0, 0, 0, 0, 0, 0xEB, 3, 0, 0, 3, 0, 0, 0 }), ref failures);
            Check("RAID_SET_TIMER rejects overflowing timer kind",
                RejectsRange(() => RaidPacketBuilder.BuildSetTimer(256, 0, 3, 1003)), ref failures);
            Check("RAID_REMAIN_TIME remains a byte and seconds",
                RaidPacketBuilder.BuildRemainTime(0, 2400).SequenceEqual(new byte[] { 0, 0x60, 9, 0, 0 }), ref failures);
            var timerPacket = GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_SET_TIMER, timer);
            Check("raid timer envelope retains A21 opcode and exact body length",
                timerPacket.Length == 28 && BitConverter.ToUInt16(timerPacket, 1) == (ushort)NotiPacketTypeA21.RAID_SET_TIMER
                    && BitConverter.ToUInt32(timerPacket, 3) == 28 && timerPacket.Skip(15).SequenceEqual(timer), ref failures);

            // Running A21 client readers 0x011480B0 / 0x01149520, verified
            // 2026-09-06. Counts and states are bytes; dungeon IDs stay u32.
            var firstPhase = RaidPacketBuilder.BuildDungeonState(RaidHandler.AntonFirstPhaseInitialDungeonStates);
            Check("first phase list exposes seven dungeons, not zero",
                firstPhase.SequenceEqual(new byte[] {
                    0, 7, 210, 0, 0, 0, 0, 211, 0, 0, 0, 2,
                    212, 0, 0, 0, 2, 213, 0, 0, 0, 2, 214, 0, 0, 0, 2,
                    215, 0, 0, 0, 2, 216, 0, 0, 0, 2, 0, 0, 0, 0 }), ref failures);
            var secondPhase = RaidPacketBuilder.BuildDungeonState(RaidHandler.AntonSecondPhaseInitialDungeonStates);
            Check("second phase list exposes its distinct dungeon IDs and states",
                secondPhase.SequenceEqual(new byte[] {
                    0, 7, 218, 0, 0, 0, 0, 219, 0, 0, 0, 0,
                    220, 0, 0, 0, 2, 221, 0, 0, 0, 2, 222, 0, 0, 0, 2,
                    223, 0, 0, 0, 2, 224, 0, 0, 0, 2, 0, 0, 0, 0 }), ref failures);
            Check("single dungeon list preserves trailing infection ID",
                RaidPacketBuilder.BuildDungeonState(220, 3, 224).SequenceEqual(
                    new byte[] { 0, 1, 220, 0, 0, 0, 3, 224, 0, 0, 0 }), ref failures);
            Check("dungeon state change uses six bytes and retains state",
                RaidPacketBuilder.BuildChangeDungeonState(212, 3).SequenceEqual(
                    new byte[] { 212, 0, 0, 0, 0, 3 }), ref failures);
            Check("empty dungeon list still carries the infection field",
                RaidPacketBuilder.BuildDungeonState(Array.Empty<System.Collections.Generic.KeyValuePair<uint, uint>>())
                    .SequenceEqual(new byte[6]), ref failures);
            var maxList = Enumerable.Repeat(new System.Collections.Generic.KeyValuePair<uint, uint>(210, 0), 255).ToArray();
            Check("maximum byte-sized dungeon list retains all records",
                RaidPacketBuilder.BuildDungeonState(maxList).Length == 1281
                    && RaidPacketBuilder.BuildDungeonState(maxList)[1] == 255, ref failures);
            Check("oversized dungeon list is rejected without truncation",
                RejectsRange(() => RaidPacketBuilder.BuildDungeonState(maxList.Concat(maxList.Take(1)).ToArray())), ref failures);
            Check("oversized initial dungeon state is rejected",
                RejectsRange(() => RaidPacketBuilder.BuildDungeonState(210, 256)), ref failures);
            Check("oversized changed dungeon state is rejected",
                RejectsRange(() => RaidPacketBuilder.BuildChangeDungeonState(210, 256)), ref failures);
            var listPacket = GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_DUNGEON_STATE, firstPhase);
            Check("dungeon list envelope carries the complete 41-byte body",
                listPacket.Length == 56 && BitConverter.ToUInt32(listPacket, 3) == 56
                    && BitConverter.ToUInt16(listPacket, 1) == (ushort)NotiPacketTypeA21.RAID_DUNGEON_STATE
                    && listPacket.Skip(15).SequenceEqual(firstPhase), ref failures);

            Console.WriteLine(
                failures == 0
                    ? "A21_RAID_PROTOCOL selftest passed."
                    : $"A21_RAID_PROTOCOL selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void CheckLivePartyAssignment(ref int failures)
        {
            var raids = new RaidManager();
            var parties = new PartyManager();
            var members = Enumerable.Range(61, 5).Select((id, i) => new RaidMember {
                UserId = (ushort)id, CharacterId = (uint)id, SessionId = Guid.NewGuid(),
                PartyIndex = (ushort)(i < 2 ? 1 : i < 4 ? 2 : 0)
            }).ToArray();
            PartyMember AsParty(RaidMember m) => new PartyMember {
                UserId = m.UserId, CharacterId = (int)m.CharacterId, SessionId = m.SessionId, Name = "live-raid"
            };
            var created = raids.Create(Array.Empty<byte>(), members[0], 200);
            foreach (var m in members.Skip(1)) raids.TryAddMember(created.RaidId, m, out _);
            foreach (var group in members.Where(m => m.PartyIndex != 0).GroupBy(m => m.PartyIndex))
            {
                var first = AsParty(group.First());
                var made = parties.CreateParty(first).Party;
                foreach (var m in group.Skip(1)) parties.Join(made.PartyId, AsParty(m));
                parties.UpdateSettings(first.UserId, first.SessionId, Array.Empty<byte>(), 4,
                    new byte[] { 0,0,4,255,255,255,255,5,0,2,0,0 });
            }
            raids.TryBeginStart(members[0].UserId, out var ready);
            raids.TryCompletePreparation(ready, out var active);
            var retired = new System.Collections.Generic.List<Party>();
            var formed = new System.Collections.Generic.List<Party>();
            bool Move(RaidSnapshot snapshot, int target, ushort index, out RaidSnapshot changed)
                => raids.TryAssignLiveParty(snapshot, members[0].UserId, members[0].SessionId,
                    members[target].UserId, index, roster => parties.ReassignLiveRaidMember(
                        roster, AsParty(members[target]), index, out retired, out formed), out changed);
            bool touched = false;
            Check("nonleader live assignment does not reach party owner",
                !raids.TryAssignLiveParty(active, members[1].UserId, members[1].SessionId, members[0].UserId, 2,
                    _ => touched = true, out _) && !touched, ref failures);
            Check("old leader session cannot assign live party",
                !raids.TryAssignLiveParty(active, members[0].UserId, Guid.NewGuid(), members[0].UserId, 2,
                    _ => touched = true, out _) && !touched, ref failures);
            Check("leader move updates both owners and replaces affected generations",
                Move(active, 0, 2, out var changed) && retired.Count == 2 && formed.Count == 2
                    && parties.GetPartyByUser(61).PartyId == parties.GetPartyByUser(63).PartyId
                    && parties.GetPartyByUser(62).Count == 1
                    && parties.ArePreparedRaidPartiesReady(changed.Members), ref failures);
            Check("stale roster request cannot undo a committed move", !Move(active, 0, 1, out _), ref failures);
            var stableId = parties.GetPartyByUser(61).PartyId;
            Check("duplicate assignment keeps actual party generation",
                Move(changed, 0, 2, out changed) && retired.Count == 0 && formed.Count == 0
                    && parties.GetPartyByUser(61).PartyId == stableId, ref failures);
            Check("unassigned raid member can join a live small party",
                Move(changed, 4, 2, out changed) && parties.GetPartyByUser(65).Count == 4, ref failures);
            var beforeFull = changed;
            Check("full destination rejects without changing either owner",
                !Move(changed, 1, 2, out _) && parties.GetPartyByUser(62).Count == 1, ref failures);
            Check("live unassignment clears actual membership",
                Move(beforeFull, 0, 0, out changed) && parties.GetPartyByUser(61) == null
                    && parties.ArePreparedRaidPartiesReady(changed.Members), ref failures);
            var unrelated = parties.CreateParty(AsParty(members[0])).Party;
            Check("live assignment cannot displace unrelated party",
                !Move(changed, 0, 1, out _) && parties.GetPartyByUser(61).PartyId == unrelated.PartyId, ref failures);
            parties.Leave(61);
            Check("live assignment can form a new singleton group",
                Move(changed, 0, 3, out changed) && parties.GetPartyByUser(61).Count == 1
                    && parties.ArePreparedRaidPartiesReady(changed.Members), ref failures);
            Check("invalid small party index is rejected", !Move(changed, 0, 11, out _), ref failures);
            var beforeRoundTrip = changed;
            Check("round trip assignment keeps old snapshots invalid",
                Move(changed, 0, 1, out changed) && Move(changed, 0, 3, out changed)
                    && !Move(beforeRoundTrip, 0, 2, out _), ref failures);
            Check("real-party commit rejection leaves raid assignment intact",
                !raids.TryAssignLiveParty(changed, 61, members[0].SessionId, 61, 2, _ => false, out _)
                    && raids.TryGetByRaidId(changed.RaidId, out var unchanged)
                    && unchanged.AssignmentVersion == changed.AssignmentVersion, ref failures);
            raids.TryFailPhase(changed, out var failed);
            Check("failed raid cannot change live party", !Move(failed, 0, 1, out _), ref failures);
        }

        private static void CheckPreparation(ref int failures)
        {
            var raids = new RaidManager();
            var parties = new PartyManager();
            var members = Enumerable.Range(41, 4).Select(id => new RaidMember {
                UserId = (ushort)id, CharacterId = (uint)id, SessionId = Guid.NewGuid(), PartyIndex = 1
            }).ToArray();
            PartyMember PartyMemberFor(RaidMember m) => new PartyMember {
                UserId = m.UserId, CharacterId = (int)m.CharacterId, SessionId = m.SessionId, Name = "raid-test"
            };
            var created = raids.Create(Array.Empty<byte>(), members[0], 200);
            foreach (var m in members.Skip(1))
                if (!raids.TryAddMember(created.RaidId, m, out _)) throw new InvalidOperationException("raid fixture");
            bool Respond(int index, ushort leader, Guid leaderSession, Guid memberSession)
                => raids.TryCommitPreparationResponse(leader, leaderSession, members[index].UserId, memberSession,
                    group => parties.AcceptPreparedRaidMember(PartyMemberFor(members[0]), PartyMemberFor(members[index]), group).Ok);
            bool Join(int index) => Respond(index, members[0].UserId, members[0].SessionId, members[index].SessionId);

            Check("ordinary acceptance still requires a pending invitation",
                !parties.AcceptInvite(members[1].UserId, members[1].SessionId, members[0].UserId,
                    members[0].SessionId, PartyMemberFor(members[0]), PartyMemberFor(members[1]), out _).Ok, ref failures);
            Check("raid auto-response is rejected before preparation", !Join(1), ref failures);
            Check("preparation captures a nonzero generation", raids.TryBeginStart(members[0].UserId, out var ready)
                && ready.PreparationGeneration > 0, ref failures);
            Check("missing real party cannot start", !raids.IsPreparationReady(ready, parties.ArePreparedRaidPartiesReady), ref failures);
            Check("wrong leader cannot authorize a raid party", !Respond(1, members[2].UserId, members[2].SessionId, members[1].SessionId), ref failures);
            Check("old leader session cannot authorize a raid party", !Respond(1, members[0].UserId, Guid.NewGuid(), members[1].SessionId), ref failures);
            Check("old member session cannot authorize a raid party", !Respond(1, members[0].UserId, members[0].SessionId, Guid.NewGuid()), ref failures);
            Check("first auto-response creates the real party", Join(1) && parties.GetPartyByUser(members[0].UserId)?.Count == 2, ref failures);
            Check("partial party cannot start", !raids.IsPreparationReady(ready, parties.ArePreparedRaidPartiesReady), ref failures);
            Check("duplicate auto-response is consumed only once", !Join(1) && parties.GetPartyByUser(members[0].UserId)?.Count == 2, ref failures);
            Check("later auto-responses join the newly created party", Join(2) && Join(3)
                && parties.GetPartyByUser(members[0].UserId)?.Count == 4, ref failures);
            Check("raid party settings match the current singleton client writer",
                parties.GetPartyByUser(members[0].UserId).PartyInfoBlock.SequenceEqual(new byte[] {0,0,4,255,255,255,255,5,0,2,0,0}), ref failures);
            Check("complete real party permits start", raids.IsPreparationReady(ready, parties.ArePreparedRaidPartiesReady), ref failures);
            Check("current preparation can cancel", raids.TryCancelPreparation(ready, out _), ref failures);
            Check("new preparation gets a different generation", raids.TryBeginStart(members[0].UserId, out var retry)
                && retry.PreparationGeneration != ready.PreparationGeneration, ref failures);
            Check("old continuation cannot cancel or complete a new preparation",
                !raids.IsPreparationReady(ready, parties.ArePreparedRaidPartiesReady)
                && !raids.TryCancelPreparation(ready, out _) && !raids.TryCompletePreparation(ready, out _), ref failures);
            Check("already formed matching raid party survives retry", raids.IsPreparationReady(retry, parties.ArePreparedRaidPartiesReady), ref failures);
            raids.RebindSession(members[1].UserId, Guid.NewGuid());
            Check("reconnection invalidates the frozen preparation",
                !raids.IsPreparationReady(retry, parties.ArePreparedRaidPartiesReady) && !Join(1)
                && !raids.TryCompletePreparation(retry, out _), ref failures);
            raids.TryCancelPreparation(retry, out _);
            raids.RebindSession(members[1].UserId, members[1].SessionId);
            raids.TryBeginStart(members[0].UserId, out var changed);
            raids.TryAssignParty(members[0].UserId, members[3].UserId, 2, out _);
            Check("assignment change invalidates preparation", !raids.IsPreparationReady(changed, parties.ArePreparedRaidPartiesReady) && !Join(1), ref failures);
            raids.TryCancelPreparation(changed, out _);
            raids.TryAssignParty(members[0].UserId, members[3].UserId, 1, out _);
            raids.TryBeginStart(members[0].UserId, out var final);
            Check("current complete preparation advances once", raids.IsPreparationReady(final, parties.ArePreparedRaidPartiesReady)
                && raids.TryCompletePreparation(final, out var started) && started.State == 2
                && !raids.TryCompletePreparation(final, out _) && !Join(1), ref failures);

            var conflict = new PartyManager();
            var outsider = new PartyMember { UserId = 99, CharacterId = 99, SessionId = Guid.NewGuid() };
            var occupied = conflict.CreateParty(outsider).Party;
            conflict.Join(occupied.PartyId, PartyMemberFor(members[1]));
            Check("preparation cannot displace an unrelated party",
                !conflict.AcceptPreparedRaidMember(PartyMemberFor(members[0]), PartyMemberFor(members[1]), members).Ok
                && conflict.GetPartyByUser(members[1].UserId)?.PartyId == occupied.PartyId && occupied.Count == 2, ref failures);
            var pending = new PartyManager();
            pending.RecordInvite(members[1].UserId, members[1].SessionId, outsider.UserId, outsider.SessionId, out _);
            Check("preparation cannot overwrite an ordinary invitation",
                !pending.AcceptPreparedRaidMember(PartyMemberFor(members[0]), PartyMemberFor(members[1]), members).Ok
                && pending.CancelInvite(members[1].UserId, members[1].SessionId, outsider.UserId, outsider.SessionId)
                && pending.PartyCount == 0, ref failures);
            var solo = new PartyManager();
            solo.CreateParty(PartyMemberFor(members[0]));
            Check("ordinary solo settings are not raid-ready", !solo.ArePreparedRaidPartiesReady(members.Take(1).ToArray()), ref failures);
            solo.UpdateSettings(members[0].UserId, members[0].SessionId, Array.Empty<byte>(), 4,
                new byte[] {0,0,4,255,255,255,255,5,0,2,0,0});
            Check("singleton SET_PARTY_INFO can complete preparation", solo.ArePreparedRaidPartiesReady(members.Take(1).ToArray()), ref failures);
            var existing = new PartyManager();
            var ordinary = existing.CreateParty(PartyMemberFor(members[0])).Party;
            foreach (var m in members.Skip(1)) existing.Join(ordinary.PartyId, PartyMemberFor(m));
            var reuse = existing.AcceptPreparedRaidMember(PartyMemberFor(members[0]), PartyMemberFor(members[1]), members);
            Check("matching full ordinary party is upgraded without removing members",
                reuse.Ok && reuse.MembershipUnchanged && reuse.Party.PartyId == ordinary.PartyId
                && existing.ArePreparedRaidPartiesReady(members) && existing.PartyCount == 1, ref failures);

            var multiple = new RaidManager();
            var multiParty = new PartyManager();
            var other = members.Select(m => m.Clone()).ToArray();
            other[0].PartyIndex = 2;
            other[3].PartyIndex = 2;
            var multi = multiple.Create(Array.Empty<byte>(), other[0], 200);
            foreach (var m in other.Skip(1)) multiple.TryAddMember(multi.RaidId, m, out _);
            multiple.TryBeginStart(other[0].UserId, out var multiReady);
            Check("raid leader cannot accept members assigned to another small party",
                !multiple.TryCommitPreparationResponse(other[0].UserId, other[0].SessionId, other[2].UserId, other[2].SessionId, _ => true), ref failures);
            Check("each small party uses its first listed member as leader",
                multiple.TryCommitPreparationResponse(other[1].UserId, other[1].SessionId, other[2].UserId, other[2].SessionId,
                    group => multiParty.AcceptPreparedRaidMember(PartyMemberFor(other[1]), PartyMemberFor(other[2]), group).Ok)
                && multiParty.GetPartyByUser(other[2].UserId)?.LeaderUserId == other[1].UserId, ref failures);
            Check("all assigned small parties must finish, not just one",
                !multiple.IsPreparationReady(multiReady, multiParty.ArePreparedRaidPartiesReady), ref failures);
            multiple.TryAddMember(multi.RaidId, new RaidMember { UserId = 100, CharacterId = 100, SessionId = Guid.NewGuid(), PartyIndex = 2 }, out _);
            Check("roster changes invalidate frozen preparation",
                !multiple.TryCommitPreparationResponse(other[0].UserId, other[0].SessionId, other[3].UserId, other[3].SessionId, _ => true), ref failures);
        }

        private static void CheckLeaderLifecycle(ref int failures)
        {
            var manager = new RaidManager();
            var leader = new RaidMember { UserId = 71, CharacterId = 71, SessionId = Guid.NewGuid(), PartyIndex = 1 };
            var member = new RaidMember { UserId = 72, CharacterId = 72, SessionId = Guid.NewGuid(), PartyIndex = 1 };
            var raid = manager.Create(Array.Empty<byte>(), leader, 200);
            manager.TryAddMember(raid.RaidId, member, out _);

            Check("nonleader cannot transfer raid leadership",
                !manager.TryTransferLeader(member.UserId, leader.UserId, out _), ref failures);
            Check("leader transfer promotes the selected raid member",
                manager.TryTransferLeader(leader.UserId, member.UserId, out var transferred)
                && transferred.LeaderUserId == member.UserId, ref failures);

            var left = manager.Leave(member.UserId);
            Check("leader leaving preserves the raid and promotes a remaining member",
                left.Ok && !left.Disbanded && left.RemainingRaid?.LeaderUserId == leader.UserId, ref failures);
        }

        public class UnusedDependency : System.Reflection.DispatchProxy
        {
            protected override object Invoke(System.Reflection.MethodInfo method, object[] args)
                => throw new InvalidOperationException("Unexpected dependency call: " + method.Name);
        }

        private static void CheckPhaseIsolation(ref int failures)
        {
            var manager = new RaidManager();
            var handler = new RaidHandler(
                System.Reflection.DispatchProxy.Create<DfoServer.Game.Characters.ICharacterRepository, UnusedDependency>(),
                System.Reflection.DispatchProxy.Create<DfoServer.Game.Session.ISessionDirectory, UnusedDependency>(), manager);
            var oldToken = handler.AdvanceTimer(42, 0, 0);
            Check("current timer token is accepted", handler.TimerCurrent(42, 0, 0, oldToken), ref failures);
            handler.CleanupRaidRuntimeState(42);
            var newToken = handler.AdvanceTimer(42, 0, 0);
            Check("recreated timer key cannot revive old callback", oldToken != newToken
                && !handler.TimerCurrent(42, 0, 0, oldToken) && handler.TimerCurrent(42, 0, 0, newToken), ref failures);
            handler.AdvanceTimer(42, 0, 0);
            Check("timer replacement invalidates its previous callback", !handler.TimerCurrent(42, 0, 0, newToken), ref failures);

            var leader = new RaidMember { UserId = 42, CharacterId = 42, SessionId = Guid.NewGuid(), PartyIndex = 1 };
            var member = new RaidMember { UserId = 43, CharacterId = 43, SessionId = Guid.NewGuid(), PartyIndex = 1 };
            RaidSnapshot ToBreak()
            {
                manager.Leave(leader.UserId);
                manager.Leave(member.UserId);
                var made = manager.Create(Array.Empty<byte>(), leader, 200);
                manager.TryAddMember(made.RaidId, member, out _);
                if (!manager.TryBeginStart(leader.UserId, out var start)
                    || !manager.TryCompletePreparation(start, out _)
                    || !manager.TryEnterPhaseBreak(made.RaidId, out _)
                    || !manager.TryCompletePhase(made.RaidId, out var waiting))
                    throw new InvalidOperationException("phase fixture");
                return waiting;
            }
            var first = ToBreak();
            Check("nonleader cannot prepare phase two", !manager.TryPrepareNextPhase(member.UserId, out _), ref failures);
            Check("phase break timer can prepare phase two", manager.TryPrepareNextPhaseAutomatically(first, out var prepared), ref failures);
            Check("duplicate phase-two start is rejected", !manager.TryPrepareNextPhase(leader.UserId, out _), ref failures);
            Check("phase two registers the normal small-party auto-response",
                manager.TryCommitPreparationResponse(leader.UserId, leader.SessionId, member.UserId, member.SessionId,
                    group => group.Count == 2 && group[0].UserId == leader.UserId), ref failures);
            Check("phase two does not bypass real-party readiness", !manager.IsPreparationReady(prepared, _ => false), ref failures);
            Check("failed phase-two preparation remains in standby", manager.TryCancelPreparation(prepared, out var cancelled)
                && cancelled.State == 5 && cancelled.PhaseIndex == 0, ref failures);
            manager.TryPrepareNextPhase(leader.UserId, out var retry);
            Check("old phase-two callback cannot complete or cancel retry",
                retry.PreparationGeneration != prepared.PreparationGeneration
                && !manager.TryCompletePreparedNextPhase(prepared, _ => true, out _) && !manager.TryCancelPreparation(prepared, out _), ref failures);
            manager.RebindSession(member.UserId, Guid.NewGuid());
            Check("phase-two session replacement invalidates readiness and completion",
                !manager.IsPreparationReady(retry, _ => true) && !manager.TryCompletePreparedNextPhase(retry, _ => true, out _), ref failures);
            manager.TryCancelPreparation(retry, out _);
            manager.RebindSession(member.UserId, member.SessionId);
            manager.TryPrepareNextPhase(leader.UserId, out var good);
            Check("phase transition rechecks real-party readiness", !manager.TryCompletePreparedNextPhase(good, _ => false, out _), ref failures);
            Check("current phase two starts exactly once", manager.TryCompletePreparedNextPhase(good, _ => true, out var active)
                && active.State == 2 && active.PhaseIndex == 1 && active.StateArgument == 1
                && !manager.TryCompletePreparedNextPhase(good, _ => true, out _), ref failures);
            Check("phase-one timeout cannot fail phase two", !manager.TryFailPhase(first, out _), ref failures);
            Check("current phase-two timeout can fail it", manager.TryFailPhase(active, out var failed)
                && failed.State == 4 && failed.StateArgument == 1, ref failures);
            var failurePacket = RaidHandler.BuildFailedRaidResultPacket(failed);
            Check("timeout and failed reconnect use result notification, not card-selection state",
                failurePacket.Length == 25 && failurePacket[0] == 0
                    && BitConverter.ToUInt16(failurePacket, 1) == (ushort)NotiPacketTypeA21.RAID_RESULT
                    && failurePacket[15] == 1 && failurePacket[16] == 1
                    && failurePacket[24] == 1, ref failures);
            Check("failed phase cannot time out twice", !manager.TryFailPhase(active, out _), ref failures);

            var replacement = ToBreak();
            Check("same wire raid id has a new runtime instance", replacement.RaidId == first.RaidId
                && replacement.InstanceId != first.InstanceId, ref failures);
            Check("old break timer cannot prepare replacement raid", !manager.TryPrepareNextPhaseAutomatically(first, out _), ref failures);
            manager.TryPrepareNextPhase(leader.UserId, out var replacementReady);
            Check("old ready callback cannot start or cancel replacement raid",
                !manager.TryCompletePreparedNextPhase(good, _ => true, out _) && !manager.TryCancelPreparation(good, out _), ref failures);
            manager.TryCompletePreparedNextPhase(replacementReady, _ => true, out var replacementActive);
            Check("old attack timeout cannot fail same-id replacement",
                !manager.TryFailPhase(active, out _) && manager.TryGetByRaidId(replacementActive.RaidId, out var stillActive)
                && stillActive.State == 2, ref failures);
            Check("old delayed clear cannot finish same-id replacement", !manager.TryEnterPhaseBreak(active, out _), ref failures);
        }

        private static void CheckSituationWire(ref int failures)
        {
            Check("symbol update uses byte count and intact key/value",
                RaidPacketBuilder.BuildSetSymbol(50, 2).SequenceEqual(new byte[] { 1,50,0,0,0,2,0,0,0 }), ref failures);
            var symbols = Enumerable.Range(0, 255).Select(i => new System.Collections.Generic.KeyValuePair<uint,uint>((uint)i, (uint)(i+1))).ToArray();
            var body = RaidPacketBuilder.BuildSetSymbols(symbols);
            Check("255 symbols retain all pair boundaries", body.Length == 2041 && body[0] == 255
                && Enumerable.Range(0,255).All(i => BitConverter.ToUInt32(body,1+8*i)==i && BitConverter.ToUInt32(body,5+8*i)==i+1), ref failures);
            Check("empty symbols have one-byte count", RaidPacketBuilder.BuildSetSymbols(Array.Empty<System.Collections.Generic.KeyValuePair<uint,uint>>()).SequenceEqual(new byte[] {0}), ref failures);
            foreach (uint operation in new uint[] {0,1,2,4})
                Check($"participation operation {operation} compact record", RaidPacketBuilder.BuildRaidDungeonParticipationInfo(210,operation,new uint[] {5,513})
                    .SequenceEqual(new byte[] {1,210,0,0,0,(byte)operation,2,5,0,1,2}), ref failures);
            Check("empty participation members preserve header", RaidPacketBuilder.BuildRaidDungeonParticipationInfo(210,0,Array.Empty<uint>()).Length==7, ref failures);
            Check("four member participation is fifteen bytes", RaidPacketBuilder.BuildRaidDungeonParticipationInfo(210,1,new uint[] {4,5,9,65535}).Length==15, ref failures);
            bool Overflow(Action action) { try { action(); return false; } catch (OverflowException) { return true; } }
            Check("symbol count overflow rejected", Overflow(() => RaidPacketBuilder.BuildSetSymbols(symbols.Concat(symbols.Take(1)).ToArray())), ref failures);
            Check("participation operation overflow rejected", Overflow(() => RaidPacketBuilder.BuildRaidDungeonParticipationInfo(210,256,new uint[] {5})), ref failures);
            Check("participation member overflow rejected", Overflow(() => RaidPacketBuilder.BuildRaidDungeonParticipationInfo(210,1,new uint[] {65536})), ref failures);
            Check("participation count overflow rejected", Overflow(() => RaidPacketBuilder.BuildRaidDungeonParticipationInfo(210,1,Enumerable.Repeat(5u,256).ToArray())), ref failures);
            var groups = new[] {
                new RaidSituationGroup {PartyIndex=1,DungeonId=210,MemberKeys=new uint[] {4,5}},
                new RaidSituationGroup {PartyIndex=2,DungeonId=213,MemberKeys=new uint[] {9},DungeonCleared=true},
                new RaidSituationGroup {PartyIndex=3,DungeonId=0,MemberKeys=new uint[] {10}},
                new RaidSituationGroup {PartyIndex=4,DungeonId=215,MemberKeys=Array.Empty<uint>()}
            };
            var packets = RaidHandler.BuildRaidParticipationRefreshPackets(groups);
            Check("refresh includes other parties, skips town/empty groups and restores cleared flag",
                packets.Count==5 && packets.Select(p=>p[20]).SequenceEqual(new byte[] {0,2,0,2,4})
                && packets.All(p=>BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.RAID_DUNGEON_PARTICIPATION_INFO)
                && BitConverter.ToUInt32(packets[0],16)==210 && BitConverter.ToUInt32(packets[2],16)==213, ref failures);
            Check("repeated refresh has deterministic remove-before-add sequence",
                packets.Zip(RaidHandler.BuildRaidParticipationRefreshPackets(groups),(a,b)=>a.SequenceEqual(b)).All(v=>v), ref failures);
            Check("empty raid refresh sends no fabricated occupancy", RaidHandler.BuildRaidParticipationRefreshPackets(Array.Empty<RaidSituationGroup>()).Count==0, ref failures);
        }

        private static void CheckRewardList(ref int failures)
        {
            var row = new RaidRewardEntry { UserId = 513, CardType = 3, Flags = 4,
                ItemId = 0x12345678, Quantity = 258 };
            foreach (uint type in new uint[] { 0, 1, 2, 3 })
            {
                var body = RaidPacketBuilder.BuildRaidRewardList(type, new[] { row });
                Check($"reward type {type} exact A21 layout", body.SequenceEqual(new byte[] {
                    (byte)type, 1, 1, 2, 3, 4, 0x78, 0x56, 0x34, 0x12, 2, 1 }), ref failures);
            }
            foreach (int count in new[] { 0, 4, 20, 255 })
            {
                var rows = Enumerable.Range(0, count).Select(i => new RaidRewardEntry {
                    UserId = (ushort)(i + 1), CardType = 1, ItemId = (uint)(440116 + i), Quantity = 1
                }).ToArray();
                var body = RaidPacketBuilder.BuildRaidRewardList(3, rows);
                Check($"squad reward {count} members preserves every record boundary",
                    body.Length == 2 + 10 * count && body[0] == 3 && body[1] == count
                    && Enumerable.Range(0, count).All(i =>
                        BitConverter.ToUInt16(body, 2 + 10 * i) == i + 1
                        && BitConverter.ToUInt32(body, 6 + 10 * i) == 440116 + i
                        && BitConverter.ToUInt16(body, 10 + 10 * i) == 1), ref failures);
            }
            var packet = GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_REWARD_LIST,
                RaidPacketBuilder.BuildRaidRewardList(3, new[] { row }));
            Check("reward notification uses A21 enum and compact body", packet.Length == 27
                && BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.RAID_REWARD_LIST
                && packet[15] == 3 && packet[16] == 1, ref failures);
            bool Overflow(Action build) { try { build(); return false; } catch (OverflowException) { return true; } }
            Check("reward type overflow rejected", Overflow(() => RaidPacketBuilder.BuildRaidRewardList(256, new[] { row })), ref failures);
            Check("reward count overflow rejected", Overflow(() => RaidPacketBuilder.BuildRaidRewardList(3, Enumerable.Repeat(row, 256).ToArray())), ref failures);
            row.Flags = 256;
            Check("reward flag overflow rejected", Overflow(() => RaidPacketBuilder.BuildRaidRewardList(3, new[] { row })), ref failures);
            row.Flags = 0; row.Quantity = 65536;
            Check("reward quantity overflow rejected", Overflow(() => RaidPacketBuilder.BuildRaidRewardList(3, new[] { row })), ref failures);
            row.Quantity = ushort.MaxValue;
            Check("maximum reward quantity preserved", BitConverter.ToUInt16(
                RaidPacketBuilder.BuildRaidRewardList(3, new[] { row }), 10) == ushort.MaxValue, ref failures);
            var gold = RaidHandler.BuildPhaseOnePartyCardRevealEntry(4, 0,
                RaidHandler.GetPhaseOnePartyCardDisplayItemId(0, 10094789, 0),
                RaidHandler.GetPhaseOnePartyCardDisplayCount(0, 120000));
            Check("120000 gold projects one container without truncating gold into u16",
                gold.ItemId == 10094789 && gold.Quantity == 1
                && RaidPacketBuilder.BuildRaidRewardList(0, new[] { gold }).Length == 12, ref failures);
            Check("party material reveal preserves actual quantity",
                RaidHandler.GetPhaseOnePartyCardDisplayCount(1, 120) == 120
                && RaidHandler.GetPhaseOnePartyCardDisplayItemId(1, 10094735, 3330) == 3330, ref failures);
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }

        private static bool RejectsRange(Func<byte[]> build)
        {
            try { build(); return false; }
            catch (ArgumentOutOfRangeException) { return true; }
        }

    }
}
