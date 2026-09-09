using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders.Raid
{
    public sealed class RaidMemberSnapshot
    {
        public ushort UserId { get; set; }
        public uint CharacterId { get; set; }
        public byte[] NameBytes { get; set; } = Array.Empty<byte>();
        public byte Job { get; set; }
        public byte GrowType { get; set; }
        public ushort PartyIndex { get; set; }
    }

    public sealed class RaidRewardEntry
    {
        public ushort UserId { get; set; }
        public byte CardType { get; set; }
        public uint Quantity { get; set; }
        public uint ItemId { get; set; }
        public uint Flags { get; set; }
    }

    public sealed class RaidEntryCostStatus
    {
        public ushort UserId { get; set; }
        public bool Ready { get; set; }
        public uint OwnedCount { get; set; }
    }

    public sealed class RaidBuffStatusEntry
    {
        public ushort PartyIndex { get; set; }
        public ushort UserId { get; set; }
        public uint ActiveUntilTimestamp { get; set; }
        public uint CooldownUntilTimestamp { get; set; }
    }

    public sealed class RaidBuffStatusGroup
    {
        public byte BuffType { get; set; }
        public IReadOnlyList<RaidBuffStatusEntry> Entries { get; set; } = Array.Empty<RaidBuffStatusEntry>();
    }

    public sealed class RaidMonsterStatusEntry
    {
        public ushort SituationIndex { get; set; }
        public IReadOnlyList<ushort> MemberIds { get; set; } = Array.Empty<ushort>();
        public uint UsedCoinCount { get; set; }
        public IReadOnlyList<uint> RuntimeValues { get; set; } = Array.Empty<uint>();
    }

    public sealed class RaidDirectoryEntry
    {
        public uint RaidId { get; set; }
        public byte[] TitleBytes { get; set; } = Array.Empty<byte>();
        public uint State { get; set; }
        public uint StateArgument { get; set; }
        public RaidMemberSnapshot Leader { get; set; }
        public int MemberCount { get; set; }
    }

    public static class RaidPacketBuilder
    {
        public static byte[] BuildPeerInvite(ushort inviterUserId, int peerId)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(inviterUserId);
            writer.WriteByte(0x0A);
            writer.WriteInt32(peerId);
            writer.WriteUInt16(0);
            writer.WriteUInt16(0);
            writer.WriteUInt16(0);
            return writer.ToArray();
        }

        public static byte[] BuildCreateAck(uint raidKey)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteUInt32(raidKey);
            return writer.ToArray();
        }

        public static byte[] BuildRaidModify(
            uint raidId,
            byte[] titleBytes,
            RaidMemberSnapshot leader)
        {
            return BuildRaidModify(raidId, titleBytes, 0, 0, leader, new[] { leader });
        }

        public static byte[] BuildRaidModify(
            uint raidId,
            byte[] titleBytes,
            uint state,
            uint stateArgument,
            RaidMemberSnapshot leader,
            IReadOnlyList<RaidMemberSnapshot> members)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32(raidId);
            writer.WriteUInt32(0); // create/full refresh operation
            WriteRaidObject(writer, raidId, titleBytes, state, stateArgument, leader);
            WriteMemberList(writer, members);
            return writer.ToArray();

        }

        public static byte[] BuildRaidCreate(
            uint raidId,
            byte[] titleBytes,
            uint state,
            uint stateArgument,
            RaidMemberSnapshot leader,
            IReadOnlyList<RaidMemberSnapshot> members)
        {
            return BuildRaidModify(raidId, titleBytes, state, stateArgument, leader, members);

        }
        // RAID_LIST (0x024F) is a list of complete RAID objects, without the
        // operation field used by RAID_MODIFY (0x0250).
        public static byte[] BuildRaidList(
            uint raidId,
            byte[] titleBytes,
            uint state,
            uint stateArgument,
            RaidMemberSnapshot leader,
            IReadOnlyList<RaidMemberSnapshot> members)
        {
            if (members == null)
                throw new ArgumentNullException(nameof(members));

            var writer = new GamePacketWriter();
            writer.WriteUInt32(1);
            WriteRaidObject(writer, raidId, titleBytes, state, stateArgument, leader);
            WriteMemberList(writer, members);
            return writer.ToArray();
        }

        public static byte[] BuildRaidInfoUpdate(
            uint raidId,
            byte[] titleBytes,
            uint state,
            uint stateArgument,
            RaidMemberSnapshot leader)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32(raidId);
            writer.WriteUInt32(2); // raid object update operation
            WriteRaidObject(writer, raidId, titleBytes, state, stateArgument, leader);
            return writer.ToArray();

        }

        public static byte[] BuildRaidMembersUpdate(
            uint raidId,
            IReadOnlyList<RaidMemberSnapshot> members)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32(raidId);
            writer.WriteUInt32(3); // member list update operation
            WriteMemberList(writer, members);
            return writer.ToArray();

        }

        private static void WriteRaidObject(
            GamePacketWriter writer,
            uint raidId,
            byte[] titleBytes,
            uint state,
            uint stateArgument,
            RaidMemberSnapshot leader)
        {
            if (state > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(state));
            if (stateArgument > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(stateArgument));
            writer.WriteUInt32(raidId);
            writer.WriteRawDstr(titleBytes);
            writer.WriteByte(0); // Anton raid type
            writer.WriteByte((byte)state);
            writer.WriteByte((byte)stateArgument);
            writer.WriteUInt32(0);
            WriteMember(writer, leader);
        }

        private static void WriteMemberList(
            GamePacketWriter writer,
            IReadOnlyList<RaidMemberSnapshot> members)
        {
            writer.WriteByte((byte)members.Count);
            foreach (var member in members)
                WriteMember(writer, member);
        }

        // NotiPacketTypeA21.RAID_LIST, client-verified layout (handler
        // 0x11834B0): [u32 count] + count x {complete raid object
        // (reader 0x11733E0) + [u8 memberCount]}. This is the raid directory
        // that feeds the client's "寻找攻坚队" query window.
        public static byte[] BuildRaidDirectory(IReadOnlyList<RaidDirectoryEntry> raids)
        {
            if (raids == null)
                throw new ArgumentNullException(nameof(raids));

            var writer = new GamePacketWriter();
            writer.WriteUInt32((uint)raids.Count);
            foreach (var raid in raids)
            {
                if (raid?.Leader == null)
                    throw new ArgumentException("Raid directory entries need a leader.", nameof(raids));
                WriteRaidObject(writer, raid.RaidId, raid.TitleBytes, raid.State, raid.StateArgument, raid.Leader);
                writer.WriteByte((byte)raid.MemberCount);
            }
            return writer.ToArray();
        }

        public static byte[] BuildRaidWaitingAck()
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            for (int i = 0; i < 5; i++)
                writer.WriteUInt32(0);
            return writer.ToArray();
        }

        public static byte[] BuildRaidWaitingWindowState335(uint total, uint b, uint c, uint d)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32(total);
            writer.WriteUInt32(b);
            writer.WriteUInt32(c);
            writer.WriteUInt32(d);
            return writer.ToArray();
        }

        public static byte[] BuildRaidWaitingWindowState336(uint a, uint b, uint c)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32(a);
            writer.WriteUInt32(b);
            writer.WriteUInt32(c);
            return writer.ToArray();
        }

        public static byte[] BuildRaidWaitingRow337(
            ushort characterId,
            uint field4,
            byte recordType,
            ushort level,
            ushort job,
            ushort fieldE,
            ushort field10,
            ushort field12,
            ushort field14,
            byte tail)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(characterId);
            writer.WriteUInt32(field4);
            writer.WriteByte(recordType);
            writer.WriteUInt16(level);
            writer.WriteUInt16(job);
            writer.WriteUInt16(fieldE);
            writer.WriteUInt16(field10);
            writer.WriteUInt16(field12);
            writer.WriteUInt16(field14);
            writer.WriteByte(tail);
            return writer.ToArray();
        }

        public static byte[] BuildRaidWaitingDone338() => new byte[] { 0x01 };

        public static byte[] BuildRaidWaitingKeys254(IReadOnlyList<(ushort characterId, byte channel, ushort level)> entries)
        {
            if (entries == null)
                throw new ArgumentNullException(nameof(entries));
            var writer = new GamePacketWriter();
            writer.WriteUInt32((uint)entries.Count);
            foreach (var entry in entries)
            {
                writer.WriteUInt16(entry.characterId);
                writer.WriteByte(entry.channel);
                writer.WriteUInt16(entry.level);
            }
            return writer.ToArray();
        }

        public static byte[] BuildRaidRemove(uint raidId)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32(raidId);
            writer.WriteUInt32(1); // remove operation
            return writer.ToArray();

        }

        public static byte[] BuildWaitingList(RaidMemberSnapshot member)
        {
            if (member == null)
                throw new ArgumentNullException(nameof(member));

            return BuildWaitingList(new[] { member });
        }

        public static byte[] BuildWaitingList(IReadOnlyList<RaidMemberSnapshot> members)
        {
            if (members == null)
                throw new ArgumentNullException(nameof(members));

            var writer = new GamePacketWriter();
            writer.WriteUInt32((uint)members.Count);
            foreach (var member in members)
            {
                // IDA sub_D0F3B0 reads u16 user id followed by u32 value.
                writer.WriteUInt16(member.UserId);
                writer.WriteUInt32(member.PartyIndex);
            }
            return writer.ToArray();
        }

        public static byte[] BuildRaidState(uint state, uint arg)
        {
            if (state > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(state));
            if (arg > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(arg));
            var writer = new GamePacketWriter();
            writer.WriteByte((byte)state);
            writer.WriteByte((byte)arg);
            return writer.ToArray();
        }

        public static byte[] BuildSetTimer(uint key0, uint key1, uint durationSeconds)
        {
            var endTimestamp = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + durationSeconds);
            return BuildSetTimer(key0, key1, durationSeconds, endTimestamp);
        }

        internal static byte[] BuildSetTimer(uint key0, uint key1, uint durationSeconds, uint endTimestamp)
        {
            if (key0 > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(key0));
            var writer = new GamePacketWriter();
            writer.WriteByte((byte)key0);
            writer.WriteUInt32(key1);
            writer.WriteUInt32(endTimestamp);
            writer.WriteUInt32(durationSeconds);
            return writer.ToArray();
        }

        public static byte[] BuildRemainTime(byte timerType, uint remainSeconds)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(timerType);
            writer.WriteUInt32(remainSeconds);
            return writer.ToArray();
        }

        public static byte[] BuildRaidResult(
            uint resultType,
            uint phaseIndex,
            uint clearTimeSeconds,
            uint deadCount,
            uint rank,
            byte rewardOption)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(checked((byte)resultType));
            writer.WriteByte(checked((byte)phaseIndex));
            writer.WriteUInt32(clearTimeSeconds);
            writer.WriteUInt16((ushort)Math.Min(deadCount, ushort.MaxValue));
            writer.WriteByte(checked((byte)rank));
            writer.WriteByte(rewardOption);
            return writer.ToArray();
        }

        public static byte[] BuildRaidMovieSkip(uint movieId, uint option)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32(movieId);
            writer.WriteUInt32(option);
            return writer.ToArray();
        }

        public static byte[] BuildRaidRewardList(
            uint rewardType,
            IReadOnlyList<RaidRewardEntry> rewards)
        {
            if (rewards == null)
                throw new ArgumentNullException(nameof(rewards));

            var writer = new GamePacketWriter();
            writer.WriteByte(checked((byte)rewardType));
            writer.WriteByte(checked((byte)rewards.Count));
            foreach (var reward in rewards)
            {
                if (reward == null)
                    throw new ArgumentException("Reward entries cannot contain null.", nameof(rewards));
                writer.WriteUInt16(reward.UserId);
                writer.WriteByte(reward.CardType);
                writer.WriteByte(checked((byte)reward.Flags));
                writer.WriteUInt32(reward.ItemId);
                writer.WriteUInt16(checked((ushort)reward.Quantity));
            }
            return writer.ToArray();
        }

        public static byte[] BuildSetSymbols(IReadOnlyList<KeyValuePair<uint, uint>> symbols)
        {
            if (symbols == null)
                throw new ArgumentNullException(nameof(symbols));

            var writer = new GamePacketWriter();
            writer.WriteByte(checked((byte)symbols.Count));
            foreach (var symbol in symbols)
            {
                writer.WriteUInt32(symbol.Key);
                writer.WriteUInt32(symbol.Value);
            }
            return writer.ToArray();
        }

        public static byte[] BuildSetSymbol(uint symbolId, uint value)
        {
            return BuildSetSymbols(new[] { new KeyValuePair<uint, uint>(symbolId, value) });
        }

        public static byte[] BuildDungeonState(
            uint dungeonId,
            uint state,
            uint infectionDungeonId = 0)
        {
            return BuildDungeonState(
                new[] { new KeyValuePair<uint, uint>(dungeonId, state) },
                infectionDungeonId);
        }

        public static byte[] BuildDungeonState(
            IReadOnlyList<KeyValuePair<uint, uint>> dungeonStates,
            uint infectionDungeonId = 0)
        {
            if (dungeonStates == null)
                throw new ArgumentNullException(nameof(dungeonStates));
            if (dungeonStates.Count > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(dungeonStates));

            var writer = new GamePacketWriter();
            writer.WriteByte(0);
            writer.WriteByte((byte)dungeonStates.Count);
            foreach (var dungeonState in dungeonStates)
            {
                if (dungeonState.Value > byte.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(dungeonStates));
                writer.WriteUInt32(dungeonState.Key);
                writer.WriteByte((byte)dungeonState.Value);
            }
            writer.WriteUInt32(infectionDungeonId);
            return writer.ToArray();
        }

        public static byte[] BuildChangeDungeonState(uint dungeonId, uint state)
        {
            if (state > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(state));
            var writer = new GamePacketWriter();
            writer.WriteUInt32(dungeonId);
            writer.WriteByte(0); // read by the client but unused
            writer.WriteByte((byte)state);
            return writer.ToArray();
        }

        public static byte[] BuildRaidDungeonParticipationInfo(
            uint targetId,
            uint op,
			IReadOnlyList<uint> memberUserIds)
        {
			if (memberUserIds == null)
				throw new ArgumentNullException(nameof(memberUserIds));

            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteUInt32(targetId);
			writer.WriteByte(checked((byte)op));
			writer.WriteByte(checked((byte)memberUserIds.Count));
			foreach (var memberUserId in memberUserIds)
				writer.WriteUInt16(checked((ushort)memberUserId));
            return writer.ToArray();
        }

        public static byte[] BuildRaidMemberState(ushort userId, byte state)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(userId);
            writer.WriteByte(state);
            return writer.ToArray();
        }

        public static byte[] BuildEntryCostInfo(IReadOnlyList<RaidEntryCostStatus> statuses)
        {
            if (statuses == null)
                throw new ArgumentNullException(nameof(statuses));

            var writer = new GamePacketWriter();
            writer.WriteUInt32((uint)statuses.Count);
            foreach (var status in statuses)
            {
                if (status == null)
                    throw new ArgumentException("Entry cost statuses cannot contain null.", nameof(statuses));
                writer.WriteUInt16(status.UserId);
                writer.WriteByte(status.Ready ? (byte)1 : (byte)0);
                writer.WriteUInt16((ushort)Math.Min(status.OwnedCount, ushort.MaxValue));
            }
            return writer.ToArray();
        }

        public static byte[] BuildRaidBuffSystem(IReadOnlyList<RaidBuffStatusGroup> groups)
        {
            if (groups == null)
                throw new ArgumentNullException(nameof(groups));
            if (groups.Count > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(groups));

            var writer = new GamePacketWriter();
            writer.WriteByte((byte)groups.Count);
            foreach (var group in groups)
            {
                if (group == null || group.Entries == null || group.Entries.Count > byte.MaxValue)
                    throw new ArgumentException("Invalid raid buff group.", nameof(groups));
                writer.WriteByte(group.BuffType);
                writer.WriteByte((byte)group.Entries.Count);
                foreach (var entry in group.Entries)
                {
                    if (entry == null)
                        throw new ArgumentException("Raid buff entries cannot contain null.", nameof(groups));
                    writer.WriteUInt16(entry.PartyIndex);
                    writer.WriteUInt16(entry.UserId);
                    writer.WriteUInt32(entry.ActiveUntilTimestamp);
                    writer.WriteUInt32(entry.CooldownUntilTimestamp);
                }
            }
            return writer.ToArray();
        }

        public static byte[] BuildRaidMonsterHp(IReadOnlyList<RaidMonsterStatusEntry> dungeons)
        {
            if (dungeons == null)
                throw new ArgumentNullException(nameof(dungeons));
            if (dungeons.Count > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(dungeons));

            var writer = new GamePacketWriter();
            writer.WriteByte((byte)dungeons.Count);
            foreach (var dungeon in dungeons)
            {
                if (dungeon == null || dungeon.MemberIds == null || dungeon.RuntimeValues == null
                    || dungeon.MemberIds.Count > byte.MaxValue || dungeon.RuntimeValues.Count > byte.MaxValue)
                    throw new ArgumentException("Invalid raid monster entry.", nameof(dungeons));
                writer.WriteUInt16(dungeon.SituationIndex);
                writer.WriteByte((byte)dungeon.MemberIds.Count);
                foreach (var memberId in dungeon.MemberIds)
                    writer.WriteUInt16(memberId);
                writer.WriteUInt32(dungeon.UsedCoinCount);
                writer.WriteByte((byte)dungeon.RuntimeValues.Count);
                foreach (var runtimeValue in dungeon.RuntimeValues)
                    writer.WriteUInt32(runtimeValue);
            }
            return writer.ToArray();
        }

        private static void WriteMember(GamePacketWriter writer, RaidMemberSnapshot member)
        {
            writer.WriteUInt16(member.UserId);
            writer.WriteByte(1);
            writer.WriteRawDstr(member.NameBytes);
            writer.WriteByte(member.Job);
            writer.WriteByte(member.GrowType);
            writer.WriteByte((byte)member.PartyIndex);
            writer.WriteByte(0);
            writer.WriteUInt32(member.CharacterId);
            writer.WriteByte(0);
            writer.WriteByte(0);
        }
    }
}
