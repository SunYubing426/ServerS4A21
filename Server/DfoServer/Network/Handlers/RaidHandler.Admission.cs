using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Raid;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Raid;

namespace DfoServer.Network.Handlers;

public sealed partial class RaidHandler
{

	private sealed class RaidEntryCostLease
	{
		public EnhancedClientSession Session { get; }

		public InventoryLease Lease { get; }

		public RaidEntryCostLease(EnhancedClientSession session, InventoryLease lease)
		{
			Session = session;
			Lease = lease;
		}
	}

	private sealed class RaidConsumedEntryCost
	{
		public EnhancedClientSession Session { get; }

		public short SlotIndex { get; }

		public RaidConsumedEntryCost(EnhancedClientSession session, short slotIndex)
		{
			Session = session;

			SlotIndex = slotIndex;
		}
	}

	public async Task HandleEntryCostInfo(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!TryResolveUserId(session, out var userId))
		{
			await SendAckAsync(session, header.type, success: false);
			return;
		}
		if (!_raids.TryGetByUser(userId, out var raid))
		{
			// Opening the raid status window is a read-only query. Do not create
			// a raid here: the client can send this request from town before the
			// explicit CREATE_RAID flow, which otherwise leaves a phantom raid.
			await SendAckAsync(session, header.type, success: false);
			return;
		}
		byte stage = (byte)((body != null && body.Length != 0) ? body[0] : 0);
		await EnsureRaidDungeonParticipationAsync(session, raid, userId);
		if (_raids.TryGetByUser(userId, out var refreshedRaid))
			raid = refreshedRaid;
		// Member cache refresh is limited to party edits and START_RAID_ATTACK.
		// Opening the status window is read-only; replaying operation=3 here can
		// race the client state handler and recreate the raid-start banner.
		if (!_objectSent.ContainsKey(session.SessionId))
		{
			await SendRaidObjectAsync(session, raid);
			_objectSent[session.SessionId] = 0;
		}
		await SendAckAsync(session, header.type, success: true);
		if (stage == 0)
		{
			await SendRaidBuffStatusAsync(session, raid.RaidId);
			await SendRaidMonsterStatusAsync(session, raid);
		}
		await RefreshEntryCostsAsync(session);
	}

	public Task RefreshEntryCostsAsync(EnhancedClientSession session)
	{
		if (!IsRaidSession(session) || !TryResolveUserId(session, out var userId)
			|| !_raids.TryGetByUser(userId, out var raid)
			|| !raid.Members.Any(member => member.UserId == userId && member.SessionId == session.SessionId))
			return Task.CompletedTask;
		var packet = GamePacketEnvelopeBuilder.Build(0,
			(ushort)NotiPacketTypeA21.RAID_ENTRY_COST_INFO,
			RaidPacketBuilder.BuildEntryCostInfo(BuildEntryCostStatuses(raid)));
		return _sessions.BroadcastToAsync(ToCharacterIds(raid), packet);
	}

	private static IReadOnlyList<RaidEntryCostStatus> BuildEntryCostStatuses(RaidSnapshot raid)
	{
		List<RaidEntryCostStatus> result = new List<RaidEntryCostStatus>(raid.Members.Count);
		checked
		{
			foreach (RaidMember member in raid.Members)
			{
				int ownedCount = 0;
				if (InventoryContext.TryGetLease((int)member.CharacterId, out var lease) && lease.IsOwnedBy(member.SessionId))
				{
					lock (lease.SyncRoot)
					{
						ownedCount = Math.Max(0, lease.Inventory.CountMainItem(10096296));
					}
				}
				result.Add(new RaidEntryCostStatus
				{
					UserId = member.UserId,
					Ready = (raid.State != 0 || ownedCount >= 1),
					OwnedCount = (uint)ownedCount
				});
			}
			return result;
		}
	}

	private static bool HasAllEntryCosts(RaidSnapshot raid)
	{
		if (raid == null || raid.State != 0 || raid.Members.Count == 0)
		{
			return false;
		}
		foreach (RaidMember member in raid.Members)
		{
			if (!InventoryContext.TryGetLease(checked((int)member.CharacterId), out var lease) || !lease.IsOwnedBy(member.SessionId))
			{
				return false;
			}
			lock (lease.SyncRoot)
			{
				if (lease.Inventory.CountMainItem(10096296) < 1)
				{
					return false;
				}
			}
		}
		return true;
	}

	// SET_RAID_WAITING (0x029C): enter the channel-level waiting pool
	// ("待命目录"). The leader later invites from the pool; joining a raid is
	// a separate step. The ack and the channel broadcast carry the pool in
	// the client-verified [flag][count][entries] layout.
	public async Task HandleSetRaidWaiting(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!IsRaidSession(session) || !TryBuildMember(session, out var member) || _raids.TryGetByUser(member.UserId, out _))
		{
			await SendAckAsync(session, header.type, success: false);
			return;
		}
		int channelId = GameNetworkConfig.ResolveGameChannel(session.ListenerPort).ChannelId;
		_raids.TryAddWaiting(channelId, member);
		// Official behavior: entering the waiting pool is silent. Leaders see
		// pooled players in the 等候组队列表 and choose whom to invite; no
		// leader notification is sent.
		await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(1, header.type, RaidPacketBuilder.BuildRaidWaitingAck()));
		FileLogger.Log($"[GameProtocol] RAID_WAITING_JOIN channel={channelId} user={member.UserId} cid={member.CharacterId} body={BitConverter.ToString(body ?? Array.Empty<byte>())}");
	}

	// 0x0334: the "等候组队列表" window's 最近攻坚 (record) tab request
	// (client sender 0xF9B1F0, fired on tab index 1). The t2 0x0335-0x0338
	// family drives the RECORD window's fields (floors/timer/exp), not the
	// waiting-player list, so only the plain t1 count ack is answered; the
	// record window stays empty instead of showing corrupted rows.
	public async Task HandleRaidWaitingListRequest(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!IsRaidSession(session))
		{
			return;
		}
		var writer = new GamePacketWriter();
		writer.WriteUInt32(0);
		await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(1, header.type, writer.ToArray()));
		FileLogger.Log($"[GameProtocol] RAID_RECENT_LIST_0334 body={BitConverter.ToString(body ?? Array.Empty<byte>())}");
	}

	// 等候组队列表 "待机人员" tab data, in official order: t2 0x0254 key
	// registration (the window drops 0x0337 rows for unregistered cids),
	// window state via t2 0x0336 (plain field sets, unlike 0x0335 which
	// flips the window into record mode), one t2 0x0337 row per waiting
	// player, t2 0x0338 done marker.
	private async Task SendWaitingPlayerListAsync(EnhancedClientSession session, int channelId)
	{
		IReadOnlyList<RaidMember> pool = _raids.GetWaitingList(channelId);
		uint total = (uint)pool.Count;
		List<(ushort characterId, byte channel, ushort level)> keys = new List<(ushort, byte, ushort)>(pool.Count);
		foreach (RaidMember pooled in pool)
		{
			CharacterRecord record = _characterRepository.GetById(checked((int)pooled.CharacterId));
			keys.Add(((ushort)pooled.CharacterId, (byte)channelId, (ushort)(record?.Level ?? 0)));
		}
		await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, 0x0254, RaidPacketBuilder.BuildRaidWaitingKeys254(keys)));
		await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, 0x0336, RaidPacketBuilder.BuildRaidWaitingWindowState336(total, 0, 0)));
		foreach (RaidMember pooled in pool)
		{
			CharacterRecord record = _characterRepository.GetById(checked((int)pooled.CharacterId));
			ushort level = (ushort)(record?.Level ?? 0);
			ushort job = (ushort)(record?.Job ?? 0);
			await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
				0,
				0x0337,
				RaidPacketBuilder.BuildRaidWaitingRow337(
					(ushort)pooled.CharacterId,
					0,
					0,
					level,
					job,
					0,
					0,
					0,
					0,
					0)));
		}
		await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, 0x0338, RaidPacketBuilder.BuildRaidWaitingDone338()));
	}

	// 0x000B reqType=0x0A accepted: the approved party joins the raid. Covers
	// both directions: invite (inviter has the raid, accepter joins) and
	// apply (accepter/leader has the raid, inviter joins).
	public async Task HandleRaidPeerAcceptAsync(EnhancedClientSession accepterSession, EnhancedClientSession inviterSession)
	{
		if (!IsRaidSession(accepterSession) || inviterSession == null || !IsRaidSession(inviterSession))
		{
			return;
		}
		EnhancedClientSession joinerSession = null;
		RaidSnapshot raid = null;
		TryResolveUserId(inviterSession, out var inviterUserId);
		TryResolveUserId(accepterSession, out var accepterUserId);
		if (_raids.TryGetByUser(inviterUserId, out var inviterRaid) && !_raids.TryGetByUser(accepterUserId, out _))
		{
			raid = inviterRaid;
			joinerSession = accepterSession;
		}
		else if (_raids.TryGetByUser(accepterUserId, out var accepterRaid) && !_raids.TryGetByUser(inviterUserId, out _))
		{
			raid = accepterRaid;
			joinerSession = inviterSession;
		}
		if (raid == null || joinerSession == null || !TryBuildMember(joinerSession, out var member))
		{
			FileLogger.Log("[GameProtocol] RAID_PEER_ACCEPT rejected: no joinable side");
			return;
		}
		RaidSnapshot joined = TryJoinRaidById(raid.RaidId, member);
		if (joined == null)
		{
			return;
		}
		_raids.TryRemoveWaiting(member.UserId);
		_objectSent[joinerSession.SessionId] = 0;
		await SendRaidObjectAsync(joinerSession, joined);
		await ResendRaidStateToSessionAsync(joinerSession, joined);
		await BroadcastRaidObjectAsync(joined);
		await BroadcastRaidMembersAsync(joined);
		await BroadcastRaidDirectoryAsync(joined);
		await joinerSession.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
			0,
			(ushort)NotiPacketType.SERVER_NOTICE_MESSAGE,
			ServerNoticeMessageBuilder.Build("您已加入攻坚队。")));
		string joinedName = ClientTextEncoding.GetString(member.NameBytes ?? Array.Empty<byte>());
		byte[] joinNotice = GamePacketEnvelopeBuilder.Build(
			0,
			(ushort)NotiPacketType.SERVER_NOTICE_MESSAGE,
			ServerNoticeMessageBuilder.Build($"玩家[{joinedName}]加入攻坚队"));
		foreach (RaidMember other in joined.Members)
		{
			if (other.UserId == member.UserId)
			{
				continue;
			}
			await _sessions.SendToAsync(checked((int)other.CharacterId), joinNotice);
		}
		FileLogger.Log($"[GameProtocol] RAID_PEER_ACCEPT raid={joined.RaidId} joiner={member.UserId} cid={member.CharacterId}");
	}

	private async Task NotifyRecruitingLeaderAsync(int channelId, RaidMember applicant, string action)
	{
		if (!_raids.TryFindRecruitingRaid(channelId, out var recruiting))
		{
			return;
		}
		RaidMember leader = recruiting.Leader;
		if (leader == null)
		{
			return;
		}
		string applicantName = ClientTextEncoding.GetString(applicant.NameBytes ?? Array.Empty<byte>());
		string message = $"玩家[{applicantName}]{action}";
		await _sessions.SendToAsync(
			checked((int)leader.CharacterId),
			GamePacketEnvelopeBuilder.Build(
				0,
				(ushort)NotiPacketType.SERVER_NOTICE_MESSAGE,
				ServerNoticeMessageBuilder.Build(message)));
	}

	// Cross-handler bridge (set at runtime build): forwards a raid invite /
	// apply prompt through PartyHandler's 0x000A(0x0A)/SC 0x0007 channel so
	// the other side must explicitly consent (0x000B).
	public Func<EnhancedClientSession, EnhancedClientSession, int, Task> RaidPeerRequestAsync { get; set; }

	// 0x0364: right-click "参加攻坚队"/"邀请攻坚队" on the raid channel.
	// Captured body: [u8 arg8][u16 targetCharacterId] (arg8=0xC8 on channel
	// 200). Both directions are consensual: the other side gets the raid
	// prompt and must accept (0x000B reqType=0x0A) before anyone joins.
	public async Task HandleRaidJoinRequest(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!IsRaidSession(session))
		{
			return;
		}
		uint targetCharacterId = 0;
		if (body != null && body.Length >= 3)
		{
			targetCharacterId = BitConverter.ToUInt16(body, 1);
		}
		if (targetCharacterId == 0 || targetCharacterId > 65535
			|| !_sessions.TryGet(checked((int)targetCharacterId), out var targetSession)
			|| !IsRaidSession(targetSession))
		{
			FileLogger.Log($"[GameProtocol] RAID_0364 rejected target={targetCharacterId} body={BitConverter.ToString(body ?? Array.Empty<byte>())}");
			return;
		}
		if (TryResolveUserId(session, out var senderId) && _raids.TryGetByUser(senderId, out var senderRaid))
		{
			// Invite path (leader -> target): target must consent.
			if (RaidPeerRequestAsync != null)
			{
				await RaidPeerRequestAsync(session, targetSession, 0);
			}
			FileLogger.Log($"[GameProtocol] RAID_INVITE_0364 raid={senderRaid.RaidId} inviter={senderId} target={targetCharacterId} prompted=True");
			return;
		}

		// Apply path (sender -> the raid's leader): leader must consent.
		int channelId = GameNetworkConfig.ResolveGameChannel(session.ListenerPort).ChannelId;
		if (TryBuildMember(session, out var member))
		{
			_raids.TryAddWaiting(channelId, member);
		}
		if (RaidPeerRequestAsync != null)
		{
			await RaidPeerRequestAsync(session, targetSession, 0);
		}
		FileLogger.Log($"[GameProtocol] RAID_APPLY_0364 channel={channelId} user={senderId} target={targetCharacterId} prompted=True");
	}

	// Shared join path: find the channel's recruiting raid, enforce the
	// official anti-double-box rule (2016-06: same account cannot raid twice),
	// then add the member as waiting (party 0).
	private RaidSnapshot TryJoinRecruitingRaid(EnhancedClientSession session, RaidMember member)
	{
		int channelId = GameNetworkConfig.ResolveGameChannel(session.ListenerPort).ChannelId;
		return _raids.TryFindRecruitingRaid(channelId, out var recruiting)
			? TryJoinRaidById(recruiting.RaidId, member)
			: null;
	}

	private RaidSnapshot TryJoinRaidById(uint raidId, RaidMember member)
	{
		if (!_raids.TryGetByRaidId(raidId, out var recruiting))
		{
			return null;
		}
		int joinerAccountId = _characterRepository.GetById(checked((int)member.CharacterId))?.AccountId ?? 0;
		if (joinerAccountId > 0)
		{
			bool sameAccountInside = false;
			foreach (RaidMember existing in recruiting.Members)
			{
				if ((_characterRepository.GetById(checked((int)existing.CharacterId))?.AccountId ?? 0) == joinerAccountId)
				{
					sameAccountInside = true;
					break;
				}
			}
			if (sameAccountInside)
			{
				FileLogger.Log($"[GameProtocol] RAID_JOIN rejected same-account raid={recruiting.RaidId} user={member.UserId} account={joinerAccountId}");
				return null;
			}
		}
		return _raids.TryAddMember(recruiting.RaidId, member, out var raid) ? raid : null;
	}

	// RAID_REQUEST_RAID_MEMBERS (0x0363): the raid window asks for a member
	// refresh. Official behavior is NOTI-only; no cmd=1 ack is sent.
	public async Task HandleRaidRequestMembers(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!IsRaidSession(session) || !TryResolveUserId(session, out var userId) || !_raids.TryGetByUser(userId, out var raid))
		{
			return;
		}
		_raids.RebindSession(userId, session.SessionId);
		if (!_objectSent.ContainsKey(session.SessionId))
		{
			_objectSent[session.SessionId] = 0;
			await SendRaidObjectAsync(session, raid);
		}
		await ResendRaidStateToSessionAsync(
			session,
			raid,
			includeState: false);
		await SendRaidTimerSnapshotAsync(session, raid);
		FileLogger.Log($"[GameProtocol] RAID_REQUEST_MEMBERS raid={raid.RaidId} user={userId} body={BitConverter.ToString(body ?? Array.Empty<byte>())}");
	}

	// REJOIN_RAID (0x029D): rebind the session and resend the raid object.
	// Silent by design: the client's ack handler pops an error message on
	// both flag paths, so no ack is sent.
	public async Task HandleRejoinRaid(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!IsRaidSession(session) || !TryResolveUserId(session, out var userId))
		{
			return;
		}
		_raids.RebindSession(userId, session.SessionId);
		if (!_raids.TryGetByUser(userId, out var raid))
		{
			return;
		}
		_objectSent[session.SessionId] = 0;
		await SendRaidObjectAsync(session, raid);
		await ResendRaidStateToSessionAsync(session, raid);
		FileLogger.Log($"[GameProtocol] REJOIN_RAID raid={raid.RaidId} user={userId} body={BitConverter.ToString(body ?? Array.Empty<byte>())}");
	}

	// Called after character selection completes on any game channel. On a
	// raid channel, rebinds the raid session and resends the raid object so
	// reconnecting players immediately see their existing raid.
	public async Task HandleRebindResyncAsync(EnhancedClientSession session)
	{
		if (!IsRaidSession(session) || !TryResolveUserId(session, out var userId))
		{
			return;
		}
		_raids.RebindSession(userId, session.SessionId);
		if (!_raids.TryGetByUser(userId, out var raid) || _objectSent.ContainsKey(session.SessionId))
		{
			return;
		}
		_objectSent[session.SessionId] = 0;
		await SendRaidObjectAsync(session, raid);
		await ResendRaidStateToSessionAsync(session, raid);
		FileLogger.Log($"[GameProtocol] RAID_REBIND_RESYNC raid={raid.RaidId} user={userId} session={session.SessionId}");
	}

	// Sends the raid directory ("寻找攻坚队" data) and the waiting pool to a
	// session that just entered the raid channel, regardless of membership.
	public async Task HandleRaidChannelWelcomeAsync(EnhancedClientSession session)
	{
		if (!IsRaidSession(session))
		{
			return;
		}
		int channelId = GameNetworkConfig.ResolveGameChannel(session.ListenerPort).ChannelId;
		await SendRaidDirectoryAsync(session, channelId);
	}

	// RAID_OTHER_CHANNEL_LIST (0x033C): data request of the raid-channel
	// search windows. Body [u8 windowKind]: 0 = "寻找攻坚队" (raid directory,
	// delivered through NotiPacketTypeA21.RAID_LIST); 1 retains the existing
	// waiting-player branch, whose separate window protocol is not verified
	// by the directory dispatch correction.
	public async Task HandleRaidOtherChannelList(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!IsRaidSession(session))
		{
			return;
		}
		int channelId = GameNetworkConfig.ResolveGameChannel(session.ListenerPort).ChannelId;
		if (body != null && body.Length >= 1 && body[0] == 1)
		{
			await SendWaitingPlayerListAsync(session, channelId);
			FileLogger.Log($"[GameProtocol] RAID_OTHER_CHANNEL_LIST_WAITING channel={channelId} total={_raids.GetWaitingList(channelId).Count} body={BitConverter.ToString(body)}");
			return;
		}
		await SendRaidDirectoryAsync(session, channelId);
		FileLogger.Log($"[GameProtocol] RAID_OTHER_CHANNEL_LIST channel={channelId} body={BitConverter.ToString(body ?? Array.Empty<byte>())}");
	}

	private async Task ResendRaidStateToSessionAsync(
		EnhancedClientSession session,
		RaidSnapshot raid,
		bool includeState = true)
	{
		IReadOnlyList<RaidMemberSnapshot> members = ToPacketMembers(raid);
		await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
			0,
			(ushort)NotiPacketType.RAID_MODIFY,
			RaidPacketBuilder.BuildRaidMembersUpdate(raid.RaidId, members)));
		// PartyIndex is already in the compact RAID_MODIFY member record.
		// Current A21 RAID_WAITING_LIST is not a party-assignment packet.
		await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, 599, RaidPacketBuilder.BuildEntryCostInfo(BuildEntryCostStatuses(raid))));
		if (includeState && raid.State != 0)
		{
			if (raid.State == 4 && raid.StateArgument == 1)
				await session.SendPacketAsync(BuildFailedRaidResultPacket(raid));
			else
				await SendRaidStateValueAsync(session, raid.State, raid.StateArgument);
			if (raid.State == 2)
			{
				if (_raidDungeonStates.TryGetValue(
						raid.RaidId,
						out var dungeonStateCache)
					&& !dungeonStateCache.IsEmpty)
				{
					var dungeonStates = dungeonStateCache
						.OrderBy(entry => entry.Key)
						.Select(entry => new KeyValuePair<uint, uint>(
							entry.Key,
							entry.Value))
						.ToArray();
					var infectionDungeonId =
						_symbolValues.TryGetValue(
							(raid.RaidId,
								AntonInfectionDungeonIndexSymbolId),
							out var infection)
							? infection
							: 0u;
					await session.SendPacketAsync(
						GamePacketEnvelopeBuilder.Build(
							0,
							(ushort)NotiPacketType.RAID_DUNGEON_STATE,
							RaidPacketBuilder.BuildDungeonState(
								dungeonStates,
								infectionDungeonId)));
				}
				else
				{
					var initialStates = raid.PhaseIndex == 0
						? AntonFirstPhaseInitialDungeonStates
						: AntonSecondPhaseInitialDungeonStates;
					await session.SendPacketAsync(
						GamePacketEnvelopeBuilder.Build(
							0,
							(ushort)NotiPacketType.RAID_DUNGEON_STATE,
							RaidPacketBuilder.BuildDungeonState(
								initialStates)));
				}
				var symbols = _symbolValues
					.Where(entry => entry.Key.RaidId == raid.RaidId)
					.OrderBy(entry => entry.Key.SymbolId)
					.Select(entry => new KeyValuePair<uint, uint>(
						entry.Key.SymbolId,
						entry.Value))
					.ToArray();
				if (symbols.Length > 0)
				{
					await session.SendPacketAsync(
						GamePacketEnvelopeBuilder.Build(
							0,
							(ushort)NotiPacketType.RAID_SET_SYMBOL,
							RaidPacketBuilder.BuildSetSymbols(symbols)));
				}
			}
			await SendRaidTimerSnapshotAsync(session, raid);
			await SendRaidBuffStatusAsync(session, raid.RaidId);
			await SendRaidMonsterStatusAsync(session, raid);
		}
	}

	private async Task SendRaidTimerSnapshotAsync(
		EnhancedClientSession session,
		RaidSnapshot raid)
	{
		if (raid.State == 2
			&& _raids.TryGetAttackRemainingSeconds(
				raid.RaidId,
				AttackSeconds,
				out var remainingSeconds))
		{
			await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
				0,
				(ushort)NotiPacketType.RAID_SET_TIMER,
				RaidPacketBuilder.BuildSetTimer(0u, 0u, remainingSeconds)));
			await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
				0,
				(ushort)NotiPacketType.RAID_REMAIN_TIME,
				RaidPacketBuilder.BuildRemainTime(0, remainingSeconds)));
		}
		else if (raid.State == 3)
		{
			var remainingBreakSeconds = GetAntonPhaseBreakRemainingSeconds();
			await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
				0,
				(ushort)NotiPacketType.RAID_SET_TIMER,
				RaidPacketBuilder.BuildSetTimer(0u, 0u, remainingBreakSeconds)));
			await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
				0,
				(ushort)NotiPacketType.RAID_REMAIN_TIME,
				RaidPacketBuilder.BuildRemainTime(1, remainingBreakSeconds)));
		}
	}

	private bool TryConsumeEntryCosts(RaidSnapshot raid, out List<RaidConsumedEntryCost> consumedCosts)
	{
		consumedCosts = new List<RaidConsumedEntryCost>();
		List<RaidEntryCostLease> leases = new List<RaidEntryCostLease>(raid.Members.Count);
		foreach (RaidMember member in raid.Members.OrderBy((RaidMember raidMember) => raidMember.CharacterId))
		{
			int characterId = checked((int)member.CharacterId);
			if (!_sessions.TryGet(characterId, out var memberSession) || memberSession.SessionId != member.SessionId || !InventoryContext.TryGetLease(characterId, out var lease) || !lease.IsOwnedBy(member.SessionId))
			{
				return false;
			}
			leases.Add(new RaidEntryCostLease(memberSession, lease));
		}

		if (!RaidEntryCostCommitService.TryConsume(
				leases.Select(entry => entry.Lease).ToArray(),
				out var mutations))
		{
			return false;
		}

		var sessionsByCharacterId = leases.ToDictionary(
			entry => entry.Lease.CharacterId,
			entry => entry.Session);
		foreach (var mutation in mutations)
		{
			if (!sessionsByCharacterId.TryGetValue(
					mutation.CharacterId,
					out var memberSession))
				return false;

			consumedCosts.Add(new RaidConsumedEntryCost(
				memberSession,
				mutation.SlotIndex));
		}

		return true;
	}

}
