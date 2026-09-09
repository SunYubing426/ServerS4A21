using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Raid
{
    public sealed class RaidLeaveResult
    {
        public bool Ok { get; init; }
        public bool Disbanded { get; init; }
        public uint RaidId { get; init; }
        public RaidSnapshot PreviousRaid { get; init; }
        public RaidSnapshot RemainingRaid { get; init; }
    }

    public sealed class RaidManager
    {
        private readonly object _lock = new object();
        private long _nextPreparationGeneration;
        private readonly Dictionary<uint, RaidAggregate> _raids = new Dictionary<uint, RaidAggregate>();
        private readonly Dictionary<ushort, uint> _userToRaid = new Dictionary<ushort, uint>();
        private readonly Dictionary<Guid, ushort> _sessionToUser = new Dictionary<Guid, ushort>();
        private readonly Dictionary<uint, List<RaidDungeonParticipation>> _dungeonParticipations =
            new Dictionary<uint, List<RaidDungeonParticipation>>();
        private readonly Dictionary<uint, Dictionary<uint, uint>> _dungeonClearCounts =
            new Dictionary<uint, Dictionary<uint, uint>>();
        private readonly Dictionary<uint, HashSet<ushort>> _clearParticipants =
            new Dictionary<uint, HashSet<ushort>>();
        private readonly Dictionary<int, List<RaidMember>> _channelWaiting =
            new Dictionary<int, List<RaidMember>>();
        private readonly Func<long> _clockMilliseconds;

        public RaidManager()
            : this(() => Environment.TickCount64)
        {
        }

        internal RaidManager(Func<long> clockMilliseconds)
        {
            _clockMilliseconds = clockMilliseconds ?? throw new ArgumentNullException(nameof(clockMilliseconds));
        }

        public RaidSnapshot Create(byte[] titleBytes, RaidMember leader, int channelId)
        {
            if (leader == null)
                throw new ArgumentNullException(nameof(leader));

            lock (_lock)
            {
                LeaveLocked(leader.UserId);
                // A21 client hard requirement (verified on-machine 2026-09-04):
                // the RAID_MODIFY handler rejects objects whose raid key high
                // word does not equal the raid channel id held in the client's
                // secure global (200/201). The key must be
                // (channelId << 16) | characterId.
                var raidId = AllocateRaidId(((uint)channelId << 16) | leader.CharacterId);
                var raid = new RaidAggregate(raidId, (byte[])(titleBytes ?? Array.Empty<byte>()).Clone(), leader.Clone());
                _raids.Add(raidId, raid);
                _userToRaid[leader.UserId] = raidId;
                _sessionToUser[leader.SessionId] = leader.UserId;
                return raid.Snapshot();
            }
        }

        public bool TryGetByUser(ushort userId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (_userToRaid.TryGetValue(userId, out var raidId) && _raids.TryGetValue(raidId, out var aggregate))
                {
                    raid = aggregate.Snapshot();
                    return true;
                }
                raid = null;
                return false;
            }
        }

        public bool TryFindRecruitingRaid(int channelId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                foreach (var aggregate in _raids.Values)
                {
                    if (aggregate.State == 0 && (int)(aggregate.RaidId >> 16) == channelId)
                    {
                        raid = aggregate.Snapshot();
                        return true;
                    }
                }
                raid = null;
                return false;
            }
        }

        public bool TryGetByMemberCharacterId(uint characterId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                foreach (var aggregate in _raids.Values)
                {
                    if (aggregate.Members.Any(member => member.CharacterId == characterId))
                    {
                        raid = aggregate.Snapshot();
                        return true;
                    }
                }
                raid = null;
                return false;
            }
        }

        public IReadOnlyList<RaidSnapshot> ListRaidsByChannel(int channelId)
        {
            lock (_lock)
            {
                var result = new List<RaidSnapshot>();
                foreach (var aggregate in _raids.Values)
                {
                    if ((int)(aggregate.RaidId >> 16) == channelId)
                        result.Add(aggregate.Snapshot());
                }
                return result;
            }
        }

        // Channel-level waiting pool ("待命目录"): players who clicked 待命/参加
        // but have not been invited into a raid yet.
        public bool TryAddWaiting(int channelId, RaidMember member)
        {
            if (member == null)
                throw new ArgumentNullException(nameof(member));

            lock (_lock)
            {
                RemoveWaitingLocked(member.UserId);
                if (!_channelWaiting.TryGetValue(channelId, out var list))
                {
                    list = new List<RaidMember>();
                    _channelWaiting.Add(channelId, list);
                }
                list.Add(member.Clone());
                return true;
            }
        }

        public bool TryRemoveWaiting(ushort userId)
        {
            lock (_lock)
            {
                return RemoveWaitingLocked(userId);
            }
        }

        private bool RemoveWaitingLocked(ushort userId)
        {
            var removed = false;
            foreach (var list in _channelWaiting.Values)
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i].UserId == userId)
                    {
                        list.RemoveAt(i);
                        removed = true;
                    }
                }
            }
            return removed;
        }

        public IReadOnlyList<RaidMember> GetWaitingList(int channelId)
        {
            lock (_lock)
            {
                if (!_channelWaiting.TryGetValue(channelId, out var list) || list.Count == 0)
                    return Array.Empty<RaidMember>();
                var result = new List<RaidMember>(list.Count);
                foreach (var member in list)
                    result.Add(member.Clone());
                return result;
            }
        }

        public bool TryAddMember(uint raidId, RaidMember member, out RaidSnapshot raid)
        {
            if (member == null)
                throw new ArgumentNullException(nameof(member));

            lock (_lock)
            {
                raid = null;
                if (!_raids.TryGetValue(raidId, out var aggregate)
                    || aggregate.State != 0
                    || aggregate.GetMember(member.UserId) != null)
                {
                    return false;
                }

                LeaveLocked(member.UserId);
                if (!aggregate.AddMember(member.Clone()))
                    return false;
                _userToRaid[member.UserId] = raidId;
                _sessionToUser[member.SessionId] = member.UserId;
                raid = aggregate.Snapshot();
                return true;
            }
        }

        public bool RebindSession(ushort userId, Guid sessionId)
        {
            lock (_lock)
            {
                if (!TryGetAggregate(userId, out var aggregate))
                    return false;
                var member = aggregate.GetMember(userId);
                if (member == null)
                    return false;
                _sessionToUser.Remove(member.SessionId);
                member.SessionId = sessionId;
                _sessionToUser[sessionId] = userId;
                return true;
            }
        }

        public bool TryUpdateTitle(ushort userId, byte[] titleBytes, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (!TryGetAggregate(userId, out var aggregate))
                {
                    raid = null;
                    return false;
                }
                aggregate.TitleBytes = (byte[])(titleBytes ?? Array.Empty<byte>()).Clone();
                raid = aggregate.Snapshot();
                return true;
            }
        }

        public bool TryAssignParty(ushort actingUserId, ushort targetUserId, uint partyIndex, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (partyIndex > ushort.MaxValue
                    || !TryGetAggregate(actingUserId, out var aggregate))
                {
                    raid = null;
                    return false;
                }

                if (actingUserId != targetUserId && aggregate.LeaderUserId != actingUserId)
                {
                    raid = null;
                    return false;
                }

                var member = aggregate.GetMember(targetUserId);
                if (member == null)
                {
                    raid = null;
                    return false;
                }
                if (member.PartyIndex != partyIndex) aggregate.AssignmentVersion = Guid.NewGuid();
                member.PartyIndex = (ushort)partyIndex;
                raid = aggregate.Snapshot();
                return true;
            }
        }

        internal bool TryAssignLiveParty(RaidSnapshot expected, ushort actor, Guid actorSession,
            ushort target, uint index, Func<IReadOnlyList<RaidMember>, bool> commit, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                raid = null;
                if (expected == null || commit == null || index > 10
                    || !TryGetAggregate(actor, out var current)
                    || current.InstanceId != expected.InstanceId || current.AssignmentVersion != expected.AssignmentVersion
                    || current.LeaderUserId != actor
                    || current.GetMember(actor)?.SessionId != actorSession || current.StartPending
                    || (current.State != 2 && current.State != 5)
                    || current.State != expected.State || current.PhaseIndex != expected.PhaseIndex
                    || current.Members.Count != expected.Members.Count
                    || !expected.Members.All(e => current.Members.Any(m => m.UserId == e.UserId
                        && m.SessionId == e.SessionId && m.PartyIndex == e.PartyIndex))) return false;
                var member = current.GetMember(target);
                if (member == null || (index != 0 && current.Members.Count(m => m.UserId != target && m.PartyIndex == index) >= 4))
                    return false;
                if (!commit(current.Snapshot().Members)) return false;
                if (member.PartyIndex != index) current.AssignmentVersion = Guid.NewGuid();
                member.PartyIndex = (ushort)index;
                raid = current.Snapshot();
                return true;
            }
        }

        public bool TryBeginStart(ushort userId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (!TryGetAggregate(userId, out var aggregate)
                    || aggregate.LeaderUserId != userId
                    || aggregate.State != 0
                    || aggregate.StartPending)
                {
                    raid = null;
                    return false;
                }

                BeginPreparationLocked(aggregate);
                raid = aggregate.Snapshot();
                return true;
            }
        }

        private static bool PreparationIsCurrent(RaidAggregate aggregate)
            => aggregate.StartPending && (aggregate.State == 0
                || (aggregate.State == 5 && aggregate.StateArgument == 0 && aggregate.PhaseIndex == 0))
               && aggregate.Members.Count == aggregate.PreparationMembers.Count
               && aggregate.PreparationMembers.All(saved => aggregate.Members.Any(current =>
                   current.UserId == saved.UserId && current.SessionId == saved.SessionId
                   && current.PartyIndex == saved.PartyIndex));

        // Called inside the existing character-pair transition guard. Lock order:
        // character transitions -> RaidManager -> PartyManager; no await or send.
        public bool TryCommitPreparationResponse(ushort leaderId, Guid leaderSession,
            ushort memberId, Guid memberSession, Func<IReadOnlyList<RaidMember>, bool> commit)
        {
            lock (_lock)
            {
                if (!TryGetAggregate(memberId, out var aggregate) || !PreparationIsCurrent(aggregate)
                    || aggregate.PreparationResponses.Contains(memberId)) return false;
                var member = aggregate.PreparationMembers.FirstOrDefault(x => x.UserId == memberId);
                if (member == null || member.SessionId != memberSession || member.PartyIndex == 0) return false;
                var group = aggregate.PreparationMembers.Where(x => x.PartyIndex == member.PartyIndex).ToArray();
                if (group.Length < 2 || group.Length > 4 || group[0].UserId != leaderId
                    || group[0].SessionId != leaderSession || leaderId == memberId) return false;
                // Consume once even if PartyManager rejects; require a fresh preparation.
                aggregate.PreparationResponses.Add(memberId);
                return commit(group);
            }
        }

        public bool IsPreparationReady(RaidSnapshot expected, Func<IReadOnlyList<RaidMember>, bool> partiesReady)
        {
            lock (_lock)
            {
                return _raids.TryGetValue(expected.RaidId, out var aggregate)
                    && aggregate.InstanceId == expected.InstanceId
                    && aggregate.PreparationGeneration == expected.PreparationGeneration
                    && PreparationIsCurrent(aggregate) && partiesReady(aggregate.PreparationMembers);
            }
        }

        public bool TryCancelPreparation(RaidSnapshot expected, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                raid = null;
                if (!_raids.TryGetValue(expected.RaidId, out var aggregate)
                    || aggregate.InstanceId != expected.InstanceId
                    || aggregate.PreparationGeneration != expected.PreparationGeneration
                    || !aggregate.StartPending || aggregate.State != expected.State)
                    return false;
                aggregate.StartPending = false;
                raid = aggregate.Snapshot();
                return true;
            }
        }

        public bool TryCompletePreparation(RaidSnapshot expected, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                raid = null;
                return _raids.TryGetValue(expected.RaidId, out var aggregate)
                    && aggregate.InstanceId == expected.InstanceId
                    && aggregate.PreparationGeneration == expected.PreparationGeneration
                    && PreparationIsCurrent(aggregate)
                    && TryCompleteStart(expected.RaidId, expected.LeaderUserId, out raid);
            }
        }

        public bool TryCompleteStart(uint raidId, ushort leaderUserId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (!_raids.TryGetValue(raidId, out var aggregate)
                    || aggregate.LeaderUserId != leaderUserId
                    || !aggregate.StartPending
                    || aggregate.State != 0)
                {
                    raid = null;
                    return false;
                }

                aggregate.StartPending = false;
                aggregate.State = 2;
                aggregate.StateArgument = 0;
                aggregate.PhaseIndex = 0;
                aggregate.PhaseStartedAtMilliseconds = _clockMilliseconds();
                aggregate.PhaseClearTimeSeconds = 0;
                aggregate.PhaseTimeExtensionSeconds = 0;
                aggregate.PhaseDeathCount = 0;
                raid = aggregate.Snapshot();
                return true;
            }
        }

        public bool TryRecordDeath(ushort userId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (!TryGetAggregate(userId, out var aggregate)
                    || aggregate.State != 2
                    || aggregate.GetMember(userId) == null)
                {
                    raid = null;
                    return false;
                }

                if (aggregate.PhaseDeathCount < uint.MaxValue)
                    aggregate.PhaseDeathCount++;
                raid = aggregate.Snapshot();
                return true;
            }
        }

        public bool TryRecordCoinUse(ushort userId, uint dungeonId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                raid = null;
                if (dungeonId == 0
                    || !TryGetAggregate(userId, out var aggregate)
                    || aggregate.State != 2
                    || !_dungeonParticipations.TryGetValue(aggregate.RaidId, out var entries))
                {
                    return false;
                }

                foreach (var participation in entries)
                {
                    if (participation.DungeonId != dungeonId
                        || !participation.MemberKeys.Contains(userId))
                    {
                        continue;
                    }

                    if (participation.UsedCoinCount < uint.MaxValue)
                        participation.UsedCoinCount++;
                    raid = aggregate.Snapshot();
                    return true;
                }

                return false;
            }
        }

        public bool TryGrantAdditionalCoinUses(ushort userId, uint additionalCount, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                raid = null;
                if (additionalCount == 0
                    || !TryGetAggregate(userId, out var aggregate)
                    || aggregate.State != 2
                    || !_dungeonParticipations.TryGetValue(aggregate.RaidId, out var entries))
                {
                    return false;
                }

                foreach (var participation in entries)
                {
                    if (!participation.MemberKeys.Contains(userId))
                        continue;

                    var usedBalance = participation.UsedCoinCount > participation.GrantedCoinCount
                        ? participation.UsedCoinCount - participation.GrantedCoinCount
                        : 0u;
                    var appliedCount = Math.Min(additionalCount, usedBalance);
                    if (appliedCount == 0)
                        return false;

                    participation.GrantedCoinCount += appliedCount;
                    raid = aggregate.Snapshot();
                    return true;
                }

                return false;
            }
        }

        public bool TryCancelStart(uint raidId, ushort leaderUserId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (!_raids.TryGetValue(raidId, out var aggregate)
                    || aggregate.LeaderUserId != leaderUserId
                    || !aggregate.StartPending
                    || aggregate.State != 0)
                {
                    raid = null;
                    return false;
                }

                aggregate.StartPending = false;
                raid = aggregate.Snapshot();
                return true;
            }
        }

        public bool TryEnterDungeon(
            ushort userId,
            uint dungeonId,
            out RaidSnapshot raid,
            out IReadOnlyList<uint> memberKeys)
        {
            lock (_lock)
            {
                raid = null;
                memberKeys = Array.Empty<uint>();
                if (dungeonId == 0
                    || !TryGetAggregate(userId, out var aggregate)
                    || aggregate.State != 2)
                {
                    return false;
                }

                if (!_dungeonParticipations.TryGetValue(aggregate.RaidId, out var entries))
                {
                    entries = new List<RaidDungeonParticipation>();
                    _dungeonParticipations.Add(aggregate.RaidId, entries);
                }

                foreach (var entry in entries)
                {
                    if (entry.MemberKeys.Contains(userId))
                        return false;
                }

                var enteringMember = aggregate.GetMember(userId);
                if (enteringMember == null)
                    return false;

                List<uint> memberKeysForParty = null;
                foreach (var group in BuildSituationGroups(aggregate.Members))
                {
                    foreach (var memberKey in group.MemberKeys)
                    {
                        if (memberKey != userId)
                            continue;

                        memberKeysForParty = new List<uint>(group.MemberKeys);
                        break;
                    }

                    if (memberKeysForParty != null)
                        break;
                }
                if (memberKeysForParty == null || memberKeysForParty.Count == 0)
                    return false;

                var participation = new RaidDungeonParticipation
                {
                    DungeonId = dungeonId,
                    MemberKeys = memberKeysForParty,
                };
                entries.Add(participation);
                raid = aggregate.Snapshot();
                memberKeys = participation.MemberKeys;
                return true;
            }
        }
        internal static IReadOnlyList<RaidSituationGroup> BuildSituationGroups(
            IReadOnlyList<RaidMember> members)
        {
            if (members == null)
                throw new ArgumentNullException(nameof(members));

            var partyGroupIndexes = new Dictionary<ushort, int>();
            var partyIndexes = new List<ushort>();
            var memberKeysByGroup = new List<List<uint>>();
            foreach (var member in members)
            {
                if (member == null)
                    continue;

                if (member.PartyIndex == 0)
                {
                    partyIndexes.Add(0);
                    memberKeysByGroup.Add(new List<uint> { member.UserId });
                    continue;
                }

                if (!partyGroupIndexes.TryGetValue(member.PartyIndex, out var groupIndex))
                {
                    groupIndex = memberKeysByGroup.Count;
                    partyGroupIndexes.Add(member.PartyIndex, groupIndex);
                    partyIndexes.Add(member.PartyIndex);
                    memberKeysByGroup.Add(new List<uint>());
                }
                memberKeysByGroup[groupIndex].Add(member.UserId);
            }

            var groups = new List<RaidSituationGroup>(memberKeysByGroup.Count);
            for (var index = 0; index < memberKeysByGroup.Count; index++)
            {
                groups.Add(new RaidSituationGroup
                {
                    SituationIndex = checked((ushort)index),
                    PartyIndex = partyIndexes[index],
                    MemberKeys = memberKeysByGroup[index].ToArray(),
                });
            }
            return groups;
        }

        internal static int GetSituationPageCount(IReadOnlyList<RaidMember> members)
        {
            const int groupsPerPage = 5;
            var groupCount = BuildSituationGroups(members).Count;
            return Math.Max(1, (groupCount + groupsPerPage - 1) / groupsPerPage);
        }


        public bool TryGetSituationGroups(
            uint raidId,
            out IReadOnlyList<RaidSituationGroup> situationGroups)
        {
            lock (_lock)
            {
                if (!_raids.TryGetValue(raidId, out var aggregate))
                {
                    situationGroups = Array.Empty<RaidSituationGroup>();
                    return false;
                }

                _dungeonParticipations.TryGetValue(raidId, out var participations);
                var groups = BuildSituationGroups(aggregate.Members);
                var result = new List<RaidSituationGroup>(groups.Count);
                foreach (var group in groups)
                {
                    RaidDungeonParticipation participation = null;
                    if (participations != null)
                    {
                        foreach (var candidate in participations)
                        {
                            if (!candidate.MemberKeys.Any(group.MemberKeys.Contains))
                                continue;
                            participation = candidate;
                            break;
                        }
                    }

                    result.Add(new RaidSituationGroup
                    {
                        SituationIndex = group.SituationIndex,
                        PartyIndex = group.PartyIndex,
                        MemberKeys = group.MemberKeys.ToArray(),
                        DungeonId = participation?.DungeonId ?? 0,
                        DungeonCleared = participation?.Cleared ?? false,
                        UsedCoinCount = participation?.UsedCoinCount ?? 0,
                        GrantedCoinCount = participation?.GrantedCoinCount ?? 0,
                    });
                }

                situationGroups = result;
                return true;
            }
        }
        public bool TryClearDungeon(
            ushort userId,
            uint dungeonId,
            uint maxClearCount,
            out RaidSnapshot raid,
            out IReadOnlyList<uint> memberKeys,
            out uint clearCount)
        {
            lock (_lock)
            {
                raid = null;
                memberKeys = Array.Empty<uint>();
                clearCount = 0;
                if (dungeonId == 0
                    || maxClearCount == 0
                    || !TryGetAggregate(userId, out var aggregate)
                    || aggregate.State != 2
                    || !_dungeonParticipations.TryGetValue(aggregate.RaidId, out var entries))
                {
                    return false;
                }

                RaidDungeonParticipation participation = null;
                foreach (var entry in entries)
                {
                    if (entry.DungeonId == dungeonId && entry.MemberKeys.Contains(userId))
                    {
                        participation = entry;
                        break;
                    }
                }
                if (participation == null)
                    return false;
                // Keep the participation visible until the party leaves the dungeon.
                if (participation.Cleared)
                    return false;
                participation.Cleared = true;
                if (!_dungeonClearCounts.TryGetValue(aggregate.RaidId, out var counts))
                {
                    counts = new Dictionary<uint, uint>();
                    _dungeonClearCounts.Add(aggregate.RaidId, counts);
                }

                counts.TryGetValue(dungeonId, out var previous);
                clearCount = Math.Min(maxClearCount, previous + 1);
                counts[dungeonId] = clearCount;
                if (!_clearParticipants.TryGetValue(aggregate.RaidId, out var clearParticipants))
                {
                    clearParticipants = new HashSet<ushort>();
                    _clearParticipants.Add(aggregate.RaidId, clearParticipants);
                }
                foreach (var memberKey in participation.MemberKeys)
                    clearParticipants.Add(checked((ushort)memberKey));
                raid = aggregate.Snapshot();
                memberKeys = participation.MemberKeys;
                return true;
            }
        }

        public bool TryAbandonDungeon(
            ushort userId,
            uint dungeonId,
            out RaidSnapshot raid,
            out IReadOnlyList<uint> memberKeys)
        {
            lock (_lock)
            {
                raid = null;
                memberKeys = Array.Empty<uint>();
                if (dungeonId == 0
                    || !TryGetAggregate(userId, out var aggregate)
                    || !_dungeonParticipations.TryGetValue(aggregate.RaidId, out var entries))
                    return false;

                RaidDungeonParticipation participation = null;
                foreach (var entry in entries)
                {
                    if (entry.DungeonId == dungeonId && entry.MemberKeys.Contains(userId))
                    {
                        participation = entry;
                        break;
                    }
                }
                if (participation == null)
                    return false;

                entries.Remove(participation);
                raid = aggregate.Snapshot();
                memberKeys = participation.MemberKeys;
                return true;
            }
        }

        public RaidLeaveResult Leave(ushort userId)
        {
            lock (_lock)
            {
                return LeaveLocked(userId) ?? new RaidLeaveResult { Ok = false };
            }
        }

        public RaidLeaveResult OnSessionDisconnected(Guid sessionId)
        {
            lock (_lock)
            {
                if (!_sessionToUser.TryGetValue(sessionId, out var userId))
                    return new RaidLeaveResult { Ok = false };
                return LeaveLocked(userId) ?? new RaidLeaveResult { Ok = false };
            }
        }

        public bool TryGetByRaidId(uint raidId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (_raids.TryGetValue(raidId, out var aggregate))
                {
                    raid = aggregate.Snapshot();
                    return true;
                }

                raid = null;
                return false;
            }
        }

        public bool TryGetClearCount(uint raidId, uint dungeonId, out uint clearCount)
        {
            lock (_lock)
            {
                clearCount = 0;
                return _dungeonClearCounts.TryGetValue(raidId, out var counts)
                    && counts.TryGetValue(dungeonId, out clearCount);
            }
        }

        public bool HasClearedDungeon(uint raidId, ushort userId)
        {
            lock (_lock)
            {
                return _clearParticipants.TryGetValue(raidId, out var participants)
                    && participants.Contains(userId);
            }
        }

        public bool TryExtendPhaseTime(
            uint raidId,
            uint baseDurationSeconds,
            uint additionalSeconds,
            out RaidSnapshot raid,
            out uint remainingSeconds)
        {
            lock (_lock)
            {
                remainingSeconds = 0;
                if (!_raids.TryGetValue(raidId, out var aggregate)
                    || aggregate.State != 2
                    || aggregate.PhaseStartedAtMilliseconds < 0)
                {
                    raid = null;
                    return false;
                }

                var elapsedSeconds = (ulong)(Math.Max(
                    0L,
                    _clockMilliseconds() - aggregate.PhaseStartedAtMilliseconds) / 1000L);
				// The timer may be restored up to 40 minutes remaining. Cap the
				// resulting remaining time rather than the total scheduled duration;
				// otherwise a phase that starts at 40 minutes can never use this buff.
                const uint maxPhaseDurationSeconds = 2400u;
				var totalSeconds = (ulong)baseDurationSeconds + aggregate.PhaseTimeExtensionSeconds;
				var currentRemaining = totalSeconds > elapsedSeconds
					? totalSeconds - elapsedSeconds
					: 0UL;
				var availableRoom = currentRemaining >= maxPhaseDurationSeconds
					? 0UL
					: maxPhaseDurationSeconds - currentRemaining;
				var appliedExtension = Math.Min((ulong)additionalSeconds, availableRoom);
				if (appliedExtension == 0)
				{
					raid = null;
					return false;
				}
				aggregate.PhaseTimeExtensionSeconds = checked(
					aggregate.PhaseTimeExtensionSeconds + (uint)appliedExtension);
				remainingSeconds = checked((uint)(currentRemaining + appliedExtension));
                raid = aggregate.Snapshot();
                return true;
            }
        }
        public bool TryEnterPhaseBreak(uint raidId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (!_raids.TryGetValue(raidId, out var aggregate) || aggregate.State != 2)
                {
                    raid = null;
                    return false;
                }

                var elapsedMilliseconds = aggregate.PhaseStartedAtMilliseconds >= 0
                    ? Math.Max(0, _clockMilliseconds() - aggregate.PhaseStartedAtMilliseconds)
                    : 0;
                aggregate.PhaseClearTimeSeconds = checked((uint)Math.Min(
                    (long)uint.MaxValue,
                    elapsedMilliseconds / 1000));
                aggregate.State = 3;
                aggregate.StateArgument = 0;
                raid = aggregate.Snapshot();
                return true;
            }
        }

        public bool TryFailPhase(uint raidId, uint phaseIndex, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (!_raids.TryGetValue(raidId, out var aggregate)
                    || aggregate.State != 2
                    || aggregate.PhaseIndex != phaseIndex)
                {
                    raid = null;
                    return false;
                }

                var elapsedMilliseconds = aggregate.PhaseStartedAtMilliseconds >= 0
                    ? Math.Max(0, _clockMilliseconds() - aggregate.PhaseStartedAtMilliseconds)
                    : 0;
                aggregate.PhaseClearTimeSeconds = checked((uint)Math.Min(
                    (long)uint.MaxValue,
                    elapsedMilliseconds / 1000));
                aggregate.StartPending = false;
                aggregate.State = 4;
                // Distinguish timeout failure from the successful final reward state.
                aggregate.StateArgument = 1;
                raid = aggregate.Snapshot();
                return true;
            }
        }

        internal bool TryEnterPhaseBreak(RaidSnapshot expected, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                raid = null;
                return _raids.TryGetValue(expected.RaidId, out var aggregate)
                    && aggregate.InstanceId == expected.InstanceId
                    && aggregate.PhaseIndex == expected.PhaseIndex
                    && TryEnterPhaseBreak(expected.RaidId, out raid);
            }
        }

        internal bool TryFailPhase(RaidSnapshot expected, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                raid = null;
                return _raids.TryGetValue(expected.RaidId, out var aggregate)
                    && aggregate.InstanceId == expected.InstanceId
                    && TryFailPhase(expected.RaidId, expected.PhaseIndex, out raid);
            }
        }

        public bool TryCompletePhase(uint raidId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (!_raids.TryGetValue(raidId, out var aggregate) || aggregate.State != 3)
                {
                    raid = null;
                    return false;
                }

                // State 5 is the between-phase standby UI. The final phase stays
                // in reward state 4 after its rewards have completed.
                aggregate.State = aggregate.PhaseIndex == 0 ? 5u : 4u;
                aggregate.StateArgument = 0;
                raid = aggregate.Snapshot();
                return true;
            }
        }

        public bool TryPrepareNextPhase(ushort leaderUserId, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (!TryGetAggregate(leaderUserId, out var aggregate)
                    || aggregate.LeaderUserId != leaderUserId)
                {
                    raid = null;
                    return false;
                }

                return TryPrepareNextPhaseLocked(aggregate, out raid);
            }
        }

        public bool TryPrepareNextPhaseAutomatically(RaidSnapshot expected, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (!_raids.TryGetValue(expected.RaidId, out var aggregate)
                    || aggregate.InstanceId != expected.InstanceId)
                {
                    raid = null;
                    return false;
                }

                return TryPrepareNextPhaseLocked(aggregate, out raid);
            }
        }

        public bool TryCompletePreparedNextPhase(RaidSnapshot expected,
            Func<IReadOnlyList<RaidMember>, bool> partiesReady, out RaidSnapshot raid)
        {
            lock (_lock)
            {
                if (!_raids.TryGetValue(expected.RaidId, out var aggregate)
                    || aggregate.InstanceId != expected.InstanceId
                    || aggregate.PreparationGeneration != expected.PreparationGeneration
                    || !PreparationIsCurrent(aggregate)
                    || aggregate.State != 5
                    || aggregate.StateArgument != 0
                    || aggregate.PhaseIndex != 0
                    || !aggregate.StartPending)
                {
                    raid = null;
                    return false;
                }

                // Recheck the actual Party owner at the transition boundary,
                // not only before the callback attempts completion.
                if (partiesReady == null || !partiesReady(aggregate.PreparationMembers))
                {
                    raid = null;
                    return false;
                }

                aggregate.StartPending = false;
                aggregate.State = 2;
                aggregate.StateArgument = 1;
                aggregate.PhaseIndex = 1;
                aggregate.PhaseStartedAtMilliseconds = _clockMilliseconds();
                aggregate.PhaseClearTimeSeconds = 0;
                aggregate.PhaseTimeExtensionSeconds = 0;
                aggregate.PhaseDeathCount = 0;
                _dungeonParticipations.Remove(aggregate.RaidId);
                _dungeonClearCounts.Remove(aggregate.RaidId);
                _clearParticipants.Remove(aggregate.RaidId);
                raid = aggregate.Snapshot();
                return true;
            }
        }
        public void ResetClearCounts(uint raidId, IEnumerable<uint> dungeonIds)
        {
            if (dungeonIds == null)
                return;

            lock (_lock)
            {
                if (!_dungeonClearCounts.TryGetValue(raidId, out var counts))
                    return;

                foreach (var dungeonId in dungeonIds)
                    counts.Remove(dungeonId);
            }
        }
        private RaidLeaveResult LeaveLocked(ushort userId)
        {
            RemoveWaitingLocked(userId);
            if (!TryGetAggregate(userId, out var raid))
                return null;

            var previous = raid.Snapshot();
            var member = raid.GetMember(userId);
            raid.RemoveMember(userId);
            _userToRaid.Remove(userId);
            if (member != null)
                _sessionToUser.Remove(member.SessionId);

            if (raid.Members.Count == 0 || raid.LeaderUserId == userId)
            {
                foreach (var remaining in raid.Members)
                {
                    _userToRaid.Remove(remaining.UserId);
                    _sessionToUser.Remove(remaining.SessionId);
                }
                _raids.Remove(raid.RaidId);
                _dungeonParticipations.Remove(raid.RaidId);
                _dungeonClearCounts.Remove(raid.RaidId);
                _clearParticipants.Remove(raid.RaidId);
                return new RaidLeaveResult
                {
                    Ok = true,
                    Disbanded = true,
                    RaidId = raid.RaidId,
                    PreviousRaid = previous,
                };
            }

            return new RaidLeaveResult
            {
                Ok = true,
                RaidId = raid.RaidId,
                PreviousRaid = previous,
                RemainingRaid = raid.Snapshot(),
            };
        }

        private bool TryGetAggregate(ushort userId, out RaidAggregate raid)
        {
            if (_userToRaid.TryGetValue(userId, out var raidId) && _raids.TryGetValue(raidId, out raid))
                return true;
            raid = null;
            return false;
        }

        private void BeginPreparationLocked(RaidAggregate aggregate)
        {
            aggregate.StartPending = true;
            aggregate.PreparationGeneration = ++_nextPreparationGeneration;
            aggregate.PreparationMembers = aggregate.Members.Select(member => member.Clone()).ToArray();
            aggregate.PreparationResponses.Clear();
        }

        private bool TryPrepareNextPhaseLocked(RaidAggregate aggregate, out RaidSnapshot raid)
        {
            if (aggregate.State != 5
                || aggregate.StateArgument != 0
                || aggregate.PhaseIndex != 0
                || aggregate.StartPending)
            {
                raid = null;
                return false;
            }

            BeginPreparationLocked(aggregate);
            raid = aggregate.Snapshot();
            return true;
        }
        private sealed class RaidDungeonParticipation
        {
            public uint DungeonId { get; init; }
            public List<uint> MemberKeys { get; init; } = new List<uint>();
            public bool Cleared { get; set; }
            public uint UsedCoinCount { get; set; }
            public uint GrantedCoinCount { get; set; }
        }

        private uint AllocateRaidId(uint preferred)
        {
            var candidate = preferred == 0 ? 1u : preferred;
            while (_raids.ContainsKey(candidate))
                candidate = candidate == uint.MaxValue ? 1u : candidate + 1u;
            return candidate;
        }
    }
}
