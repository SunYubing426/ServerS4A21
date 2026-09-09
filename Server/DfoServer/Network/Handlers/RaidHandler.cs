using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Raid;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Raid;
using PvfLib;

namespace DfoServer.Network.Handlers;

public sealed partial class RaidHandler
{


	private readonly ICharacterRepository _characterRepository;

	private readonly ISessionDirectory _sessions;

	private readonly RaidManager _raids;
	internal Func<IReadOnlyList<RaidMember>, bool> PreparationPartiesReady { get; set; }
	internal Func<EnhancedClientSession, RaidSnapshot, ushort, ushort, Task<RaidSnapshot>> LivePartyAssignment { get; set; }
	internal bool CommitLivePartyAssignment(RaidSnapshot expected, ushort actor, Guid session,
		ushort target, ushort index, Func<IReadOnlyList<RaidMember>, bool> commit, out RaidSnapshot result)
		=> _raids.TryAssignLiveParty(expected, actor, session, target, index, commit, out result);

	internal bool TryCommitPreparationResponse(ushort leader, Guid leaderSession,
		ushort member, Guid memberSession, Func<IReadOnlyList<RaidMember>, bool> commit)
		=> _raids.TryCommitPreparationResponse(leader, leaderSession, member, memberSession, commit);

	private readonly ConcurrentDictionary<Guid, byte> _objectSent = new ConcurrentDictionary<Guid, byte>();

	private readonly ConcurrentDictionary<string, Guid> _timerVersions = new ConcurrentDictionary<string, Guid>();

	private readonly ConcurrentDictionary<(uint RaidId, uint SymbolId), uint> _symbolValues = new ConcurrentDictionary<(uint, uint), uint>();

	private readonly ConcurrentDictionary<uint, uint> _infectionDungeonByRaid = new ConcurrentDictionary<uint, uint>();

	private readonly ConcurrentDictionary<uint, byte> _blackVolcanoBarrierBroken = new ConcurrentDictionary<uint, byte>();

	private readonly ConcurrentDictionary<uint, object> _raidRuntimeLocks = new ConcurrentDictionary<uint, object>();

	private readonly ConcurrentDictionary<uint, PhaseRewardFlow> _phaseRewardFlows = new ConcurrentDictionary<uint, PhaseRewardFlow>();

	private readonly ConcurrentDictionary<(uint RaidId, byte BuffType), AntonRaidBuffActivation> _raidBuffActivations = new ConcurrentDictionary<(uint, byte), AntonRaidBuffActivation>();

	private readonly ConcurrentDictionary<(uint RaidId, ushort SituationIndex, uint SoloMemberKey, uint DungeonId), uint[]> _raidMonsterRuntimeValues = new ConcurrentDictionary<(uint, ushort, uint, uint), uint[]>();

	public RaidHandler(
		ICharacterRepository characterRepository,
		ISessionDirectory sessions,
		RaidManager raids)
	{
		_characterRepository = characterRepository
			?? throw new ArgumentNullException(nameof(characterRepository));
		_sessions = sessions
			?? throw new ArgumentNullException(nameof(sessions));
		_raids = raids ?? throw new ArgumentNullException(nameof(raids));
	}

	private static void RunInBackground(Task task, string operation)
	{
		_ = ObserveBackgroundTaskAsync(task, operation);
	}

	private static async Task ObserveBackgroundTaskAsync(
		Task task,
		string operation)
	{
		try
		{
			await task;
		}
		catch (Exception ex)
		{
			FileLogger.Log(
				$"[GameProtocol] RAID_BACKGROUND_TASK " +
				$"operation={operation} error={ex}");
		}
	}

	private static bool IsRaidSession(EnhancedClientSession session)
	{
		return session != null && GameNetworkConfig.IsRaidListener(session.ListenerPort);
	}

	public async Task HandleCreateRaid(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!IsRaidSession(session) || !TryBuildMember(session, out var member) || !TryReadTitle(body, out var titleBytes))
		{
			await SendAckAsync(session, header.type, success: false);
			return;
		}
		if (titleBytes.Length == 0)
		{
			titleBytes = BuildDefaultTitle(member.CharacterId);
		}
		int channelId = GameNetworkConfig.ResolveGameChannel(session.ListenerPort).ChannelId;
		RaidSnapshot raid = _raids.Create(titleBytes, member, channelId);
		await SendRaidObjectAsync(session, raid);
		IReadOnlyList<RaidMemberSnapshot> createMembers = ToPacketMembers(raid);
		byte[] createObjectPacket = GamePacketEnvelopeBuilder.Build(0, 592, RaidPacketBuilder.BuildRaidCreate(raid.RaidId, raid.TitleBytes, raid.State, raid.StateArgument, ToPacketMember(raid.Leader), createMembers));
		foreach (EnhancedClientSession channelSession in GetRaidChannelSessions(raid))
		{
			if (channelSession.SessionId != session.SessionId)
			{
				await channelSession.SendPacketAsync(createObjectPacket);
			}
		}
		_objectSent[session.SessionId] = 0;
		await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(1, header.type, RaidPacketBuilder.BuildCreateAck(raid.RaidId)));
		await BroadcastRaidDirectoryAsync(raid);
		FileLogger.Log($"[GameProtocol] CREATE_RAID channel={channelId} cid={member.CharacterId} user={member.UserId} raid={raid.RaidId} title={BitConverter.ToString(raid.TitleBytes)}");
	}

	internal static bool ShouldRejectRaidWaitingListRequest(uint raidState, byte stage)
	{
		// Stage 0 is also the attack status-window refresh request.
		return false;
	}


	// 0x029A body: [u16 targetUserId]. Self-leave sends the sender's own id;
	// the leader's "攻坚队员强退" sends the member's id (capture-verified:
	// [02 00] kicked uid 2). Kick removes only that member; the raid survives.
	public async Task HandleLeaveRaid(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!TryResolveUserId(session, out var userId))
		{
			await SendAckAsync(session, header.type, success: false);
			return;
		}
		ushort targetUserId = userId;
		if (body != null && body.Length >= 2)
		{
			ushort parsed = BitConverter.ToUInt16(body, 0);
			if (parsed != 0)
			{
				targetUserId = parsed;
			}
		}
		bool isKick = targetUserId != userId
			&& _raids.TryGetByUser(userId, out var senderRaid)
			&& senderRaid.Leader != null
			&& senderRaid.Leader.UserId == userId;
		if (targetUserId != userId && !isKick)
		{
			await SendAckAsync(session, header.type, success: false);
			FileLogger.Log($"[GameProtocol] LEAVE_RAID kick rejected: non-leader user={userId} target={targetUserId}");
			return;
		}
		RaidLeaveResult result = _raids.Leave(targetUserId);
		if (isKick && result.Ok)
		{
			await SendAckAsync(session, header.type, true);
			_objectSent.TryRemove(session.SessionId, out var _);
			byte[] kickRemove = GamePacketEnvelopeBuilder.Build(0, 592, RaidPacketBuilder.BuildRaidRemove(result.RaidId));
			RaidMember kicked = result.PreviousRaid?.Members?.FirstOrDefault(m => m.UserId == targetUserId);
			if (kicked != null
				&& _sessions.TryGet(checked((int)kicked.CharacterId), out var kickedSession)
				&& kickedSession != null)
			{
				_objectSent.TryRemove(kickedSession.SessionId, out var _);
				await kickedSession.SendPacketAsync(kickRemove);
				// cmd=0x01 type=0x0530 [u8 kicked]: official "membership ended"
				// noti (handler 0x1114150 = t1[0x0530]; the t2[0x03A5] alias on
				// cmd=0x00 passes arg2=0 and only toasts). Shows the official
				// 已经强制退出攻坚队 toast and clears the client's my-raid
				// state (mgr->0xc2be60) so the window falls back to apply UI.
				await kickedSession.SendPacketAsync(
					GamePacketEnvelopeBuilder.Build(1, 0x0530, new byte[] { 1 }));
			}
			if (!result.Disbanded && result.RemainingRaid != null)
			{
				await BroadcastRaidObjectAsync(result.RemainingRaid);
				await BroadcastRaidMembersAsync(result.RemainingRaid);
				await BroadcastRaidDirectoryAsync(result.RemainingRaid);
			}
			FileLogger.Log($"[GameProtocol] LEAVE_RAID kick by={userId} target={targetUserId} raid={result.RaidId} disbanded={result.Disbanded}");
			return;
		}
		await SendAckAsync(session, header.type, result.Ok);
		if (!result.Ok)
		{
			return;
		}
		_objectSent.TryRemove(session.SessionId, out var _);
		byte[] removePacket = GamePacketEnvelopeBuilder.Build(0, 592, RaidPacketBuilder.BuildRaidRemove(result.RaidId));
		await session.SendPacketAsync(removePacket);
		if (!result.Disbanded)
		{
			// Self-leave: reset the leaver's own my-raid state (cmd=1 0x0530).
			await session.SendPacketAsync(
				GamePacketEnvelopeBuilder.Build(1, 0x0530, new byte[] { 0 }));
			await BroadcastRaidObjectAsync(result.RemainingRaid);
			await BroadcastRaidMembersAsync(result.RemainingRaid);
			await BroadcastRaidDirectoryAsync(result.RemainingRaid);
		}
		else
		{
			CleanupRaidRuntimeState(result.RaidId);
			// Disband notifies the whole raid channel so every client's query
			// window drops the raid, not just former members.
			await BroadcastToRaidChannelAsync(result.PreviousRaid, removePacket);
			// Former members get the my-raid reset noti as well.
			if (result.PreviousRaid?.Members != null)
			{
				byte[] endedNoti = GamePacketEnvelopeBuilder.Build(1, 0x0530, new byte[] { 0 });
				foreach (RaidMember former in result.PreviousRaid.Members)
				{
					await _sessions.SendToAsync(checked((int)former.CharacterId), endedNoti);
				}
			}
			await BroadcastRaidDirectoryAsync(result.PreviousRaid);
		}
		FileLogger.Log($"[GameProtocol] LEAVE_RAID user={userId} raid={result.RaidId} disbanded={result.Disbanded}");
	}


	public async Task HandleDungeonAbortedAsync(EnhancedClientSession session, int dungeonId, string reason)
	{
		if (IsAntonRaidDungeon(dungeonId) && TryResolveUserId(session, out var userId) && _raids.TryAbandonDungeon(userId, (uint)dungeonId, out var raid, out var memberKeys))
		{
			ResetRaidMonsterRuntimeValues(raid, userId, (uint)dungeonId);
			await BroadcastRaidNotificationAsync(raid, NotiPacketType.RAID_DUNGEON_PARTICIPATION_INFO, RaidPacketBuilder.BuildRaidDungeonParticipationInfo((uint)dungeonId, 0u, memberKeys));
			await BroadcastRaidMonsterStatusAsync(raid);
			FileLogger.Log($"[GameProtocol] RAID_DUNGEON_ABORT raid={raid.RaidId} dungeon={dungeonId} reason={reason} memberKeys={string.Join(",", memberKeys)}");
		}
	}

	public async Task HandleRaidDoBehavior(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		ushort userId = 0;
		RaidSnapshot raid = null;
		bool ok = IsRaidDoBehaviorRequest(body) && TryResolveUserId(session, out userId) && _raids.TryGetByUser(userId, out raid) && raid.State == 2;
		await SendAckAsync(session, header.type, ok);
		if (!ok)
		{
			FileLogger.Log("[GameProtocol] RAID_DO_BEHAVIOR rejected body=" + BitConverter.ToString(body ?? Array.Empty<byte>()));
			return;
		}
		await BroadcastRaidNotificationAsync(raid, NotiPacketType.RAID_DO_BEHAVIOR, body);
		FileLogger.Log($"[GameProtocol] RAID_DO_BEHAVIOR relayed raid={raid.RaidId} user={userId} target={BitConverter.ToUInt32(body, 0)} behavior={BitConverter.ToUInt32(body, 4)}");
	}

	internal static bool IsRaidDoBehaviorRequest(byte[] body)
	{
		return body != null && body.Length == 8;
	}

	public async Task HandleRaidSetSymbol(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		ushort userId = 0;
		RaidSnapshot raid = null;
		uint symbolId;
		uint operand;
		byte operation;
		bool ok = TryReadRaidSetSymbolRequest(body, out symbolId, out operand, out operation) && TryResolveUserId(session, out userId) && _raids.TryGetByUser(userId, out raid) && raid.State == 2 && raid.PhaseIndex == 1 && symbolId == 110 && _symbolValues.ContainsKey((raid.RaidId, symbolId));
		await SendAckAsync(session, header.type, ok);
		if (!ok)
		{
			FileLogger.Log("[GameProtocol] RAID_SET_SYMBOL rejected body=" + BitConverter.ToString(body ?? Array.Empty<byte>()));
			return;
		}
		await ChangeBlackVolcanoBarrierAsync(raid, operand, operation, "pvf-symbol-request");
		FileLogger.Log($"[GameProtocol] RAID_SET_SYMBOL applied raid={raid.RaidId} user={userId} symbol={symbolId} operation={operation} operand={operand}");
	}

	internal static bool TryReadRaidSetSymbolRequest(byte[] body, out uint symbolId, out uint operand, out byte operation)
	{
		symbolId = 0u;
		operand = 0u;
		operation = byte.MaxValue;
		if (body == null || body.Length != 9)
		{
			return false;
		}
		symbolId = BitConverter.ToUInt32(body, 0);
		operand = BitConverter.ToUInt32(body, 4);
		operation = body[8];
		return operation <= 2;
	}

	internal static bool TryApplyRaidSymbolOperation(uint currentValue, uint operand, byte operation, out uint nextValue)
	{
		switch (operation)
		{
		case 0:
			nextValue = operand;
			return true;
		case 1:
			nextValue = ((operand > (uint)(-1 - (int)currentValue)) ? uint.MaxValue : (currentValue + operand));
			return true;
		case 2:
			nextValue = ((operand < currentValue) ? (currentValue - operand) : 0u);
			return true;
		default:
			nextValue = currentValue;
			return false;
		}
	}

	public async Task HandleRaidManagerWork(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (body == null || body.Length < 12 || !TryResolveUserId(session, out var actingUserId))
		{
			await SendAckAsync(session, header.type, success: false);
			return;
		}
		FileLogger.Log($"[GameProtocol] RAID_MANAGER_WORK_RAW user={actingUserId} body={BitConverter.ToString(body)}");
		uint op = BitConverter.ToUInt32(body, 0);
		ushort targetActorId = BitConverter.ToUInt16(body, 4);
		uint partyIndex = BitConverter.ToUInt32(body, 8);
		RaidSnapshot raid = null;
		bool ok = false;
		if (op == 1 && _raids.TryGetByUser(actingUserId, out _))
		{
			ok = _raids.TryTransferLeader(
				actingUserId,
				targetActorId,
				out raid);
		}
		else if (op == 0 && partyIndex <= 10 && _raids.TryGetByUser(actingUserId, out var currentRaid))
		{
			if ((currentRaid.State == 2 || currentRaid.State == 5) && LivePartyAssignment != null)
			{
				raid = await LivePartyAssignment(session, currentRaid, targetActorId, (ushort)partyIndex);
				ok = raid != null;
			}
			else if (currentRaid.State == 0)
				ok = _raids.TryAssignParty(actingUserId, targetActorId, partyIndex, out raid);
		}
		await SendAckAsync(session, header.type, ok);
		if (ok)
		{
			await BroadcastRaidObjectAsync(raid);
			await BroadcastRaidMembersAsync(raid);
			await BroadcastRaidMonsterStatusAsync(raid);
			FileLogger.Log(
				op == 1
					? $"[GameProtocol] RAID_TRANSFER_LEADER raid={raid.RaidId} " +
						$"from={actingUserId} to={targetActorId}"
					: $"[GameProtocol] RAID_MANAGER_WORK raid={raid.RaidId} " +
						$"user={actingUserId} actor={targetActorId} " +
						$"partyIndex={partyIndex}");
		}
	}

	public async Task<bool> HandleNormalPartyLeftAsync(ushort userId)
	{
		if (!_raids.TryGetByUser(userId, out var currentRaid))
			return false;

		var member = currentRaid.Members.FirstOrDefault(entry => entry.UserId == userId);
		if (member == null || member.PartyIndex == 0
			|| !_raids.TryAssignParty(userId, userId, 0, out var updatedRaid))
			return false;

		await BroadcastRaidObjectAsync(updatedRaid);
		await BroadcastRaidMembersAsync(updatedRaid);
		await BroadcastRaidMonsterStatusAsync(updatedRaid);
		FileLogger.Log($"[GameProtocol] RAID_PARTY_UNASSIGN raid={updatedRaid.RaidId} user={userId}");
		return true;
	}

	public async Task<bool> HandleNormalPartyJoinedAsync(
		IReadOnlyList<ushort> userIds)
	{
		if (userIds == null || userIds.Count == 0)
			return false;

		RaidSnapshot currentRaid = null;
		foreach (var userId in userIds)
		{
			if (_raids.TryGetByUser(userId, out currentRaid))
				break;
		}
		if (currentRaid == null)
			return false;

		var partyMembers = currentRaid.Members
			.Where(member => userIds.Contains(member.UserId))
			.ToArray();
		if (partyMembers.Length == 0)
			return false;

		var partyIndex = partyMembers
			.Select(member => member.PartyIndex)
			.FirstOrDefault(index => index != 0);
		if (partyIndex == 0)
		{
			for (ushort candidate = 1; candidate <= 10; candidate++)
			{
				if (currentRaid.Members.All(member => member.PartyIndex != candidate))
				{
					partyIndex = candidate;
					break;
				}
			}
		}
		if (partyIndex == 0)
			return false;

		RaidSnapshot updatedRaid = currentRaid;
		foreach (var member in partyMembers)
		{
			if (member.PartyIndex == partyIndex)
				continue;
			if (!_raids.TryAssignParty(
					member.UserId,
					member.UserId,
					partyIndex,
					out updatedRaid))
				return false;
		}

		if (updatedRaid.AssignmentVersion == currentRaid.AssignmentVersion)
			return true;

		await BroadcastRaidObjectAsync(updatedRaid);
		await BroadcastRaidMembersAsync(updatedRaid);
		await BroadcastRaidMonsterStatusAsync(updatedRaid);
		FileLogger.Log(
			$"[GameProtocol] RAID_PARTY_ASSIGN raid={updatedRaid.RaidId} " +
			$"partyIndex={partyIndex} users={string.Join(",", userIds)}");
		return true;
	}

	public async Task HandleModifyRaidInfo(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!TryResolveUserId(session, out var userId) || !TryReadTitle(body, out var titleBytes) || !_raids.TryUpdateTitle(userId, titleBytes, out var raid))
		{
			await SendAckAsync(session, header.type, success: false);
			return;
		}
		await SendAckAsync(session, header.type, success: true);
		await BroadcastRaidInfoAsync(raid);
		FileLogger.Log($"[GameProtocol] MODIFY_RAID_INFO raid={raid.RaidId} user={userId} title={BitConverter.ToString(titleBytes)}");
	}

	public Task<bool> TryHandleCreatePopupClose(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!IsRaidSession(session) || !IsCreatePopupCloseBody(body))
		{
			return Task.FromResult(result: false);
		}
		FileLogger.Log($"[GameProtocol] CLOSE_RAID_CREATE_POPUP session={session.SessionId} body={BitConverter.ToString(body)}");
		return Task.FromResult(result: true);
	}

	public static bool IsCreatePopupCloseBody(byte[] body)
	{
		return body != null && body.Length == 3 && body[0] == 1 && BitConverter.ToUInt16(body, 1) == 665;
	}

	public void ClearSession(Guid sessionId)
	{
		_objectSent.TryRemove(sessionId, out var _);
	}

	private Task SendRaidObjectAsync(EnhancedClientSession session, RaidSnapshot raid)
	{
		IReadOnlyList<RaidMemberSnapshot> members = ToPacketMembers(raid);
		return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, 592, RaidPacketBuilder.BuildRaidCreate(raid.RaidId, raid.TitleBytes, raid.State, raid.StateArgument, ToPacketMember(raid.Leader), members)));
	}

	private Task SendRaidStateValueAsync(EnhancedClientSession session, uint state, uint stateArgument)
	{
		return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketType.RAID_STATE, RaidPacketBuilder.BuildRaidState(state, stateArgument)));
	}

	private Task BroadcastRaidObjectAsync(RaidSnapshot raid)
	{
		IReadOnlyList<RaidMemberSnapshot> members = ToPacketMembers(raid);
		byte[] packet = GamePacketEnvelopeBuilder.Build(0, 592, RaidPacketBuilder.BuildRaidModify(raid.RaidId, raid.TitleBytes, raid.State, raid.StateArgument, ToPacketMember(raid.Leader), members));
		// Official 2016 behavior: every raid-channel client keeps all raids in
		// its local manager so the "寻找攻坚队" query window can list them.
		// Non-member clients register the object but never select it
		// (check_raid fails), so a channel-wide broadcast is safe.
		return BroadcastToRaidChannelAsync(raid, packet);
	}

	private IEnumerable<EnhancedClientSession> GetRaidChannelSessions(RaidSnapshot raid)
	{
		int channelId = unchecked((int)(raid.RaidId >> 16));
		foreach (EnhancedClientSession candidate in _sessions.GetAllGameSessions())
		{
			if (candidate != null
				&& GameNetworkConfig.IsRaidListener(candidate.ListenerPort)
				&& GameNetworkConfig.ResolveGameChannel(candidate.ListenerPort).ChannelId == channelId)
			{
				yield return candidate;
			}
		}
	}

	private Task BroadcastToRaidChannelAsync(RaidSnapshot raid, byte[] packet)
	{
		List<Task> tasks = new List<Task>();
		foreach (EnhancedClientSession candidate in GetRaidChannelSessions(raid))
		{
			tasks.Add(candidate.SendPacketAsync(packet));
		}
		return Task.WhenAll(tasks);
	}

	private Task BroadcastRaidStateAsync(RaidSnapshot raid)
	{
		byte[] packet = raid.State == 4 && raid.StateArgument == 1
			? BuildFailedRaidResultPacket(raid)
			: GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketType.RAID_STATE, RaidPacketBuilder.BuildRaidState(raid.State, raid.StateArgument));
		return _sessions.BroadcastToAsync(ToCharacterIds(raid), packet);
	}

	private Task BroadcastRaidNotificationAsync(RaidSnapshot raid, NotiPacketType type, byte[] body)
	{
		byte[] packet = GamePacketEnvelopeBuilder.Build(0, (ushort)type, body);
		return _sessions.BroadcastToAsync(ToCharacterIds(raid), packet);
	}

	private Task BroadcastRaidInfoAsync(RaidSnapshot raid)
	{
		byte[] packet = GamePacketEnvelopeBuilder.Build(0, 592, RaidPacketBuilder.BuildRaidInfoUpdate(raid.RaidId, raid.TitleBytes, raid.State, raid.StateArgument, ToPacketMember(raid.Leader)));
		return BroadcastToRaidChannelAsync(raid, packet);
	}

	private Task BroadcastRaidMembersAsync(RaidSnapshot raid)
	{
		// The compact RAID_MODIFY member records already include PartyIndex.
		// Current client 0x01164860 reads RAID_WAITING_LIST as a waiting-user
		// list (u16 count), NOT the upstream u32-count user-to-party mapping.
		// Do not send that incompatible mapping after the valid member refresh.
		byte[] memberPacket = GamePacketEnvelopeBuilder.Build(
			0,
			(ushort)NotiPacketType.RAID_MODIFY,
			RaidPacketBuilder.BuildRaidMembersUpdate(raid.RaidId, ToPacketMembers(raid)));
		return BroadcastToRaidChannelAsync(raid, memberPacket);
	}

	private Task BroadcastRaidDirectoryAsync(RaidSnapshot raid)
	{
		byte[] packet = BuildRaidDirectoryPacket(ToDirectoryEntries(_raids.ListRaidsByChannel(unchecked((int)(raid.RaidId >> 16)))));
		return BroadcastToRaidChannelAsync(raid, packet);
	}

	private Task SendRaidDirectoryAsync(EnhancedClientSession session, int channelId)
	{
		byte[] packet = BuildRaidDirectoryPacket(ToDirectoryEntries(_raids.ListRaidsByChannel(channelId)));
		return session.SendPacketAsync(packet);
	}

	internal static byte[] BuildRaidDirectoryPacket(IReadOnlyList<RaidDirectoryEntry> entries)
	{
		// Current A21 dispatch at 0x01188623 routes RAID_LIST to 0x011834B0.
		// RAID_WAITING_LIST instead consumes u16-count waiting-player records.
		return GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_LIST,
			RaidPacketBuilder.BuildRaidDirectory(entries));
	}

	private IEnumerable<EnhancedClientSession> GetRaidChannelSessions(int channelId)
	{
		foreach (EnhancedClientSession candidate in _sessions.GetAllGameSessions())
		{
			if (candidate != null
				&& GameNetworkConfig.IsRaidListener(candidate.ListenerPort)
				&& GameNetworkConfig.ResolveGameChannel(candidate.ListenerPort).ChannelId == channelId)
			{
				yield return candidate;
			}
		}
	}

	private static IReadOnlyList<RaidDirectoryEntry> ToDirectoryEntries(IReadOnlyList<RaidSnapshot> raids)
	{
		List<RaidDirectoryEntry> result = new List<RaidDirectoryEntry>(raids.Count);
		foreach (RaidSnapshot raid in raids)
		{
			RaidMember leader = raid.Leader;
			if (leader == null)
				continue;
			result.Add(new RaidDirectoryEntry
			{
				RaidId = raid.RaidId,
				TitleBytes = raid.TitleBytes,
				State = raid.State,
				StateArgument = raid.StateArgument,
				Leader = ToPacketMember(leader),
				MemberCount = raid.Members.Count,
			});
		}
		return result;
	}

	private static Task SendAckAsync(EnhancedClientSession session, ushort type, bool success)
	{
		return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(1, type, new byte[1] { (byte)(success ? 1 : 0) }));
	}

	private bool TryBuildMember(EnhancedClientSession session, out RaidMember member)
	{
		member = null;
		int characterId = SessionOwnerResolver.Resolve(session).characterId;
		if (characterId <= 0 || characterId > 65535)
		{
			return false;
		}
		ushort userId = ((session.Player != null && session.Player.UserId != 0) ? session.Player.UserId : ((ushort)characterId));
		CharacterRecord record = _characterRepository.GetById(characterId);
		member = new RaidMember
		{
			UserId = userId,
			CharacterId = (uint)characterId,
			SessionId = session.SessionId,
			NameBytes = (record?.Name ?? session.Player?.Name ?? Array.Empty<byte>()),
			Job = record?.Job ?? session.Player?.Job ?? 0,
			GrowType = record?.GrowType ?? session.Player?.GrowType ?? 0
		};
		return true;
	}

	private static bool TryResolveUserId(EnhancedClientSession session, out ushort userId)
	{
		if (session.Player != null && session.Player.UserId != 0)
		{
			userId = session.Player.UserId;
			return true;
		}
		int characterId = SessionOwnerResolver.Resolve(session).characterId;
		if (characterId > 0 && characterId <= 65535)
		{
			userId = (ushort)characterId;
			return true;
		}
		userId = 0;
		return false;
	}

	internal static bool TryReadTitle(byte[] body, out byte[] title)
	{
		title = Array.Empty<byte>();
		// A21 CREATE_RAID/MODIFY_RAID_INFO carries a one-byte mode/reserved
		// field followed by one standard dstr. The captured request
		// 00 03 00 00 00 31 32 33 is therefore mode=0, title="123".
		const int dstrOffset = 1;
		if (body == null || body.Length < dstrOffset + sizeof(int))
		{
			return false;
		}
		int length = BitConverter.ToInt32(body, dstrOffset);
		if (length < 0 || length > body.Length - dstrOffset - sizeof(int))
		{
			return false;
		}
		title = new byte[length];
		Buffer.BlockCopy(body, dstrOffset + sizeof(int), title, 0, length);
		return true;
	}

	private static byte[] BuildDefaultTitle(uint characterId)
	{
		return ClientTextEncoding.GetBytes($"Raid: {characterId}");
	}


	private static RaidMemberSnapshot ToPacketMember(RaidMember member)
	{
		return new RaidMemberSnapshot
		{
			UserId = member.UserId,
			CharacterId = member.CharacterId,
			NameBytes = member.NameBytes,
			Job = member.Job,
			GrowType = member.GrowType,
			PartyIndex = member.PartyIndex
		};
	}

	private static IReadOnlyList<RaidMemberSnapshot> ToPacketMembers(RaidSnapshot raid)
	{
		List<RaidMemberSnapshot> result = new List<RaidMemberSnapshot>(raid.Members.Count);
		foreach (RaidMember member in raid.Members)
		{
			result.Add(ToPacketMember(member));
		}
		return result;
	}

	private static IEnumerable<int> ToCharacterIds(RaidSnapshot raid)
	{
		foreach (RaidMember member in raid.Members)
		{
			yield return checked((int)member.CharacterId);
		}
	}
}
