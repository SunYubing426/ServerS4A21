using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    // PartyHandler 的交易 partial: 0x000A/0x000B 邀请应答开窗后的交易会话全部状态。
    //   - 交易对登记: RegisterTradePair/ClearTradePair(_tradePeers 双向 uid 映射),
    //     断线(OnSessionEndingAsync)/取消/离场自动拆对。
    //   - 放物/取物: 客户端把交易窗当 itemspace=4, MOVE_ITEMSPACE(0x0013) 先经
    //     TryHandleTradeMoveAsync 拦截, 校验生成 PlayerTradeOffer, ack 给放物者,
    //     0x000F(CHANGE_ITEMTRADE_ITEM) 35B 定长推给对方(官方是双方广播)。
    //   - 状态机 0x0011(STATE_ITEMTRADE) [actorUid:u16][state:u8]:
    //     0=解锁(放物后自动回落) 1=锁定 2=取消 3=最终确认 5=添加完成。
    //     双方都 5 → 阶段开放(0x028F 双方广播); 双方都 3 → CompleteTradeAsync 成交。
    //   - 成交: PlayerTradeRuntimeService 原子交换 + SaveDirtyPair 单事务持久化,
    //     0x0012(FINISH) 双方 + 槽位级背包刷新; 失败发 0x0010(CANCEL)。
    public sealed partial class PartyHandler
    {
        // 交易对/报价/邀请的统一锁(uid 有序加锁防死锁的语义都在单一锁内)。
        private readonly object _tradePairsLock = new object();

        private InventoryRefreshSender _inventoryRefresh;

        internal void AttachInventoryRefresh(InventoryRefreshSender inventoryRefresh)
        {
            _inventoryRefresh = inventoryRefresh;
        }

        // 已开窗的交易对: uid → 对手 uid(双向登记)。
        private readonly Dictionary<ushort, ushort> _tradePeers =
            new Dictionary<ushort, ushort>();

        // 已开窗交易必须绑定双方当前 SessionId，旧连接不能操作重连后建立的新交易。
        private readonly Dictionary<ushort, Guid> _tradeSessionIds =
            new Dictionary<ushort, Guid>();

        // uid → tradeSlot → 报价(未成交前物品不真正移动, 只记账)。
        private readonly Dictionary<ushort, Dictionary<short, PlayerTradeOffer>>
            _tradeOffers =
                new Dictionary<ushort, Dictionary<short, PlayerTradeOffer>>();

        // 定向邀请登记: (inviter<<32)|accepter → 会话绑定信息。
        // 只允许"同一对会话"的应答消费, 防串扰/过期弹框重放。
        private readonly Dictionary<ulong, PendingTradeInvite>
            _pendingTradePeerIds =
                new Dictionary<ulong, PendingTradeInvite>();

        // 双方都按了"添加完成"(state=5)后的阶段开放标记。
        private readonly HashSet<ushort> _tradeOfferStageOpen =
            new HashSet<ushort>();

        // 单方按了"添加完成"(state=5)。
        private readonly HashSet<ushort> _tradeOfferReady =
            new HashSet<ushort>();

        // 双方都按了最终确认(state=3)。
        private readonly HashSet<ushort> _tradeFinalReady =
            new HashSet<ushort>();

        // RES_PEER(0x000B) 邀请登记条目: 应答时校验会话仍配对, 弹框过期即作废。
        private sealed class PendingTradeInvite
        {
            internal Guid InviterSessionId { get; }

            internal Guid AccepterSessionId { get; }

            internal int PeerInt { get; }

            internal PendingTradeInvite(
                Guid inviterSessionId,
                Guid accepterSessionId,
                int peerInt)
            {
                InviterSessionId = inviterSessionId;
                AccepterSessionId = accepterSessionId;
                PeerInt = peerInt;
            }
        }

        // CMD 0x0018 SET_ITEMTRADE_STATE: body=[state:u8]。
        public async Task Handle_SET_ITEMTRADE_STATE(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var uid = (session?.Player?.UserId).GetValueOrDefault();
            ushort peer;
            lock (_tradePairsLock)
            {
                TryGetTradePeerLocked(session, out peer);
            }
            FileLogger.Log(
                $"[{ProtocolName}] SET_ITEMTRADE_STATE uid={uid} peer={peer} " +
                $"body({(body != null ? body.Length : 0)}B): " +
                $"{(body == null ? "null" : BitConverter.ToString(body))}");
            if (uid == 0 || peer == 0 || body == null || body.Length != 1)
                return;

            var peerSession = FindSessionByUserId(peer);
            if (peerSession == null || !IsSameGameChannel(session, peerSession))
            {
                ClearTradePair(uid);
                return;
            }

            // state=3: 最终确认。双方都在"报价阶段开放"后才记入, 双双确认即成交。
            if (body[0] == 3)
            {
                bool bothConfirmed;
                var offerStageOpen = false;
                lock (_tradePairsLock)
                {
                    if (_tradePeers.TryGetValue(uid, out var registered)
                        && registered == peer)
                    {
                        offerStageOpen = _tradeOfferStageOpen.Contains(uid)
                                         && _tradeOfferStageOpen.Contains(peer);
                        if (offerStageOpen)
                        {
                            _tradeFinalReady.Add(uid);
                            bothConfirmed = _tradeFinalReady.Contains(peer);
                        }
                        else
                        {
                            bothConfirmed = false;
                        }
                    }
                    else
                    {
                        bothConfirmed = false;
                    }
                }
                FileLogger.Log(
                    $"[{ProtocolName}] TRADE FINAL_CONFIRM uid={uid} peer={peer} " +
                    $"offerReady={offerStageOpen} both={bothConfirmed}");
                if (bothConfirmed)
                    await CompleteTradeAsync(session, peerSession);
                return;
            }

            // state=2: 取消, 拆对 + 双方 0x0010。
            if (body[0] == 2)
            {
                ClearTradePair(uid);
                await SendTradeTerminalAsync(
                    session,
                    peerSession,
                    NotiPacketTypeA21.CANCEL_ITEMTRADE);
                return;
            }

            var state = body[0];
            var openOfferStage = false;
            lock (_tradePairsLock)
            {
                if (body[0] == 5)
                {
                    _tradeOfferReady.Add(uid);
                    if (_tradeOfferReady.Contains(peer)
                        && !_tradeOfferStageOpen.Contains(uid))
                    {
                        _tradeOfferStageOpen.Add(uid);
                        _tradeOfferStageOpen.Add(peer);
                        openOfferStage = true;
                    }
                }
                else if (state == 0)
                {
                    // 解锁回落: 清报价/最终确认(放新物品会触发)。
                    _tradeOfferReady.Remove(uid);
                    _tradeFinalReady.Remove(uid);
                }
            }

            // 其余状态(0/1)原样转发给对方(0x0011 [actorUid][state])。
            await SendTradeStateAsync(session, peerSession, uid, state);
            if (openOfferStage)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] TRADE OFFER_STAGE_OPEN first={uid} " +
                    $"second={peer} noti=0x028F");
                await SendTradeOfferStageOpenAsync(session, peerSession);
            }
        }

        // MOVE_ITEMSPACE(0x0013) 交易窗拦截: 源或目标是 itemspace 4(Trade)即属交易。
        // 返回 true 表示已消费(含错误 ack), 调用方不再走普通移动。
        public async Task<bool> TryHandleTradeMoveAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (session?.Player == null || body == null || body.Length < 14)
                return false;

            // body: [srcList:u8][srcSlot:i16][5B保留][moveValue:i32][dstList:u8][dstSlot:i16]
            var sourceList = (InventoryListType)body[0];
            var destinationList = (InventoryListType)body[11];
            if (sourceList != InventoryListType.Trade
                && destinationList != InventoryListType.Trade)
                return false;

            var uid = session.Player.UserId;
            ushort peer;
            lock (_tradePairsLock)
            {
                TryGetTradePeerLocked(session, out peer);
            }
            if (peer == 0)
            {
                await SendTradeMoveErrorAsync(
                    session,
                    header.type,
                    sourceList,
                    destinationList);
                return true;
            }

            var peerSession = FindSessionByUserId(peer);
            if (peerSession == null || !IsSameGameChannel(session, peerSession))
            {
                ClearTradePair(uid);
                await SendTradeMoveErrorAsync(
                    session,
                    header.type,
                    sourceList,
                    destinationList);
                return true;
            }

            var sourceSlot = BitConverter.ToInt16(body, 1);
            var moveValue = BitConverter.ToInt32(body, 7);
            var destinationSlot = BitConverter.ToInt16(body, 12);

            // 交易期间任何一方变动了报价, 双方的 ready 状态整体回落(返回需重发 0 的 uid)。
            var resetReadyUsers = Array.Empty<ushort>();
            byte[] peerChangeBody;
            if (destinationList == InventoryListType.Trade)
            {
                // 放入交易窗: 校验生成报价(金币/物品/虚拟计数), 槽位自分配(复用同源槽)。
                if (!InventoryContext.TryGetOwnedLease(
                        session.SessionId,
                        session.Player.CharacterId,
                        out var lease))
                {
                    await SendTradeMoveErrorAsync(
                        session,
                        header.type,
                        sourceList,
                        destinationList);
                    return true;
                }
                PlayerTradeOffer offer;
                string failure;
                lock (lease.SyncRoot)
                {
                    if (!PlayerTradeRuntimeService.TryCreateOffer(
                            lease.Inventory,
                            body[0],
                            sourceSlot,
                            moveValue,
                            out offer,
                            out failure))
                        offer = null;
                }
                if (offer == null)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] TRADE offer rejected uid={uid} " +
                        $"source={body[0]}:{sourceSlot} reason={failure}");
                    await SendTradeMoveErrorAsync(
                        session,
                        header.type,
                        sourceList,
                        destinationList);
                    return true;
                }

                lock (_tradePairsLock)
                {
                    if (!_tradePeers.TryGetValue(uid, out var registered)
                        || registered != peer)
                    {
                        peerChangeBody = null;
                    }
                    else
                    {
                        var offers = _tradeOffers[uid];
                        // 同一源再次放入(数量调整): 复用原 tradeSlot, 覆盖旧报价。
                        var existing = offers.Values.FirstOrDefault(
                            value => value.SourceList == offer.SourceList
                                     && value.SourceSlot == offer.SourceSlot);
                        if (existing != null)
                            offers.Remove(existing.TradeSlot);
                        destinationSlot = (short)(!offer.IsGold
                            ? (existing?.TradeSlot
                               ?? FindAvailableTradeSlot(offers))
                            : 0);
                        if (destinationSlot < 0)
                        {
                            peerChangeBody = null;
                        }
                        else
                        {
                            offer.TradeSlot = destinationSlot;
                            offers[destinationSlot] = offer;
                            resetReadyUsers = ResetTradeReadyLocked(uid, peer);
                            peerChangeBody = offer.IsGold
                                ? TradeItemChangeBodyBuilder.BuildGold(
                                    offer.TradeSlot,
                                    offer.Amount)
                                : TradeItemChangeBodyBuilder.BuildItem(
                                    offer.TradeSlot,
                                    offer.Item);
                        }
                    }
                }
            }
            else
            {
                // 从交易窗取回: 撤销对应报价, 对方收到清槽 0x000F。
                lock (_tradePairsLock)
                {
                    if (!_tradeOffers.TryGetValue(uid, out var offers)
                        || !offers.Remove(sourceSlot))
                    {
                        peerChangeBody = null;
                    }
                    else
                    {
                        resetReadyUsers = ResetTradeReadyLocked(uid, peer);
                        peerChangeBody =
                            TradeItemChangeBodyBuilder.BuildEmpty(sourceSlot);
                    }
                }
            }

            if (peerChangeBody == null)
            {
                await SendTradeMoveErrorAsync(
                    session,
                    header.type,
                    sourceList,
                    destinationList);
                return true;
            }

            // 放物者本地 ack(交易窗记账不落真实背包, Mutated=false)。
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                1,
                header.type,
                MoveItemSpaceAckBuilder.Build(new InventoryMoveResult
                {
                    SourceListType = sourceList,
                    SourceSlotIndex = sourceSlot,
                    MoveValue32 = moveValue,
                    DestinationListType = destinationList,
                    DestinationSlotIndex = destinationSlot,
                    Mutated = false,
                })));
            foreach (var actorUid in resetReadyUsers)
                await SendTradeStateAsync(session, peerSession, actorUid, 0);
            await peerSession.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0,
                (ushort)NotiPacketTypeA21.CHANGE_ITEMTRADE_ITEM,
                peerChangeBody));
            FileLogger.Log(
                $"[{ProtocolName}] TRADE MOVE uid={uid} peer={peer} " +
                $"src=({sourceList},{sourceSlot}) " +
                $"dst=({destinationList},{destinationSlot}) value={moveValue} " +
                $"change({peerChangeBody.Length}B)=" +
                $"{BitConverter.ToString(peerChangeBody)}");
            return true;
        }

        // 双方最终确认后的成交: 快照报价 → 原子交换 → 单事务持久化 → 收尾通知。
        private async Task CompleteTradeAsync(
            EnhancedClientSession firstSession,
            EnhancedClientSession secondSession)
        {
            var firstUid = firstSession.Player.UserId;
            var secondUid = secondSession.Player.UserId;
            List<PlayerTradeOffer> firstOffers;
            List<PlayerTradeOffer> secondOffers;
            lock (_tradePairsLock)
            {
                if (!_tradePeers.TryGetValue(firstUid, out var peer)
                    || peer != secondUid
                    || !_tradeSessionIds.TryGetValue(firstUid, out var firstSessionId)
                    || firstSessionId != firstSession.SessionId
                    || !_tradeSessionIds.TryGetValue(secondUid, out var secondSessionId)
                    || secondSessionId != secondSession.SessionId
                    || !_tradeFinalReady.Contains(firstUid)
                    || !_tradeFinalReady.Contains(secondUid))
                    return;
                firstOffers = _tradeOffers[firstUid].Values
                    .Select(offer => offer.Copy())
                    .ToList();
                secondOffers = _tradeOffers[secondUid].Values
                    .Select(offer => offer.Copy())
                    .ToList();
                ClearTradePairLocked(firstUid);
            }

            PlayerTradeExecutionResult result = null;
            var executed =
                InventoryContext.TryGetOwnedLease(
                    firstSession.SessionId,
                    firstSession.Player.CharacterId,
                    out var firstLease)
                && InventoryContext.TryGetOwnedLease(
                    secondSession.SessionId,
                    secondSession.Player.CharacterId,
                    out var secondLease)
                && PlayerTradeRuntimeService.TryExecuteAndPersist(
                    firstLease,
                    firstOffers,
                    secondLease,
                    secondOffers,
                    out result);
            if (result == null)
                result = new PlayerTradeExecutionResult
                {
                    Failure = "inventory lease missing",
                };
            FileLogger.Log(
                $"[{ProtocolName}] TRADE COMPLETE first={firstUid} " +
                $"second={secondUid} ok={executed} " +
                $"failure={result.Failure ?? "none"}");

            await SendTradeTerminalAsync(
                firstSession,
                secondSession,
                executed
                    ? NotiPacketTypeA21.FINISH_ITEMTRADE
                    : NotiPacketTypeA21.CANCEL_ITEMTRADE);
            if (executed && _inventoryRefresh != null)
            {
                await SendTradeInventoryChangesAsync(
                    firstSession,
                    result.FirstChanges);
                await SendTradeInventoryChangesAsync(
                    secondSession,
                    result.SecondChanges);
                await _inventoryRefresh.SendItemListRefresh(
                    firstSession,
                    default(InventoryListType));
                await _inventoryRefresh.SendItemListRefresh(
                    secondSession,
                    default(InventoryListType));
            }
        }

        private async Task SendTradeInventoryChangesAsync(
            EnhancedClientSession session,
            InventoryMutationSet changes)
        {
            foreach (var group in changes.Slots.GroupBy(value => value.ListType))
            {
                await _inventoryRefresh.SendUpdateItemList(
                    session,
                    group.Key,
                    group.Select(value => value.SlotIndex));
            }
        }

        // 交易终局(0x0010 取消 / 0x0012 完成): 双方 + 状态回落 + 空闲 USER_STATE。
        private static async Task SendTradeTerminalAsync(
            EnhancedClientSession first,
            EnhancedClientSession second,
            NotiPacketTypeA21 packetType)
        {
            var w = new GamePacketWriter();
            w.WriteUInt16(0);
            var data = GamePacketEnvelopeBuilder.Build(
                0,
                (ushort)packetType,
                w.ToArray());
            await Task.WhenAll(
                first.SendPacketAsync(data),
                second.SendPacketAsync(data));
            await SendTradeStateAsync(
                first,
                second,
                first.Player.UserId,
                0);
            await SendTradeStateAsync(
                first,
                second,
                second.Player.UserId,
                0);
            await SendTradeIdleUserStateAsync(
                first,
                second,
                first.Player.UserId,
                second.Player.UserId);
        }

        // 0x0011 [actorUid:u16][state:u8] → 双方(本人+对方都收, 客户端按 uid 区分)。
        private static Task SendTradeStateAsync(
            EnhancedClientSession first,
            EnhancedClientSession second,
            ushort actorUid,
            byte stateValue)
        {
            var w = new GamePacketWriter();
            w.WriteUInt16(actorUid);
            w.WriteByte(stateValue);
            var data = GamePacketEnvelopeBuilder.Build(
                0,
                (ushort)NotiPacketTypeA21.STATE_ITEMTRADE,
                w.ToArray());
            return Task.WhenAll(
                first.SendPacketAsync(data),
                second.SendPacketAsync(data));
        }

        // 0x028F ITEMTRADE_REG_ITEM_FINISH(空包体) → 双方: 报价阶段开放。
        private static Task SendTradeOfferStageOpenAsync(
            EnhancedClientSession first,
            EnhancedClientSession second)
        {
            var data = GamePacketEnvelopeBuilder.Build(
                0,
                (ushort)NotiPacketTypeA21.ITEMTRADE_REG_ITEM_FINISH,
                Array.Empty<byte>());
            return Task.WhenAll(
                first.SendPacketAsync(data),
                second.SendPacketAsync(data));
        }

        // 交易结束把双方 UserState 归 0 并广播(0x0003), 解除客户端的"交易中"锁。
        private static Task SendTradeIdleUserStateAsync(
            EnhancedClientSession first,
            EnhancedClientSession second,
            ushort firstUid,
            ushort secondUid)
        {
            first.Player.UserState = 0;
            second.Player.UserState = 0;
            var data = GamePacketEnvelopeBuilder.Build(
                0,
                (ushort)NotiPacketTypeA21.USER_STATE,
                EnterSelectDungeonStateBuilder.BuildUserState(
                    new[] { firstUid, secondUid },
                    0));
            return Task.WhenAll(
                first.SendPacketAsync(data),
                second.SendPacketAsync(data));
        }

        private static Task SendTradeMoveErrorAsync(
            EnhancedClientSession session,
            ushort packetType,
            InventoryListType sourceList,
            InventoryListType destinationList)
        {
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                1,
                packetType,
                MoveItemSpaceAckBuilder.BuildError(
                    4,
                    (byte)sourceList,
                    (byte)destinationList)));
        }

        // 建立交易对: 双向 peer 映射 + 双方空报价表 + ready 状态复位。
        private void RegisterTradePair(
            ushort first,
            Guid firstSessionId,
            ushort second,
            Guid secondSessionId)
        {
            lock (_tradePairsLock)
            {
                ClearTradePairLocked(first);
                ClearTradePairLocked(second);
                _tradePeers[first] = second;
                _tradePeers[second] = first;
                _tradeSessionIds[first] = firstSessionId;
                _tradeSessionIds[second] = secondSessionId;
                _tradeOffers[first] = new Dictionary<short, PlayerTradeOffer>();
                _tradeOffers[second] = new Dictionary<short, PlayerTradeOffer>();
                ResetTradeReadyLocked(first, second);
            }
        }

        // 拆对(单向入口): 连带清掉对手侧的记账与双方 pending 邀请。
        private void ClearTradePair(ushort userId)
        {
            lock (_tradePairsLock)
            {
                ClearTradePairLocked(userId);
            }
        }

        private void ClearTradePairLocked(ushort userId)
        {
            var staleInvites = _pendingTradePeerIds.Keys
                .Where(key => (ushort)(key >> 32) == userId
                              || (ushort)key == userId)
                .ToArray();
            foreach (var key in staleInvites)
                _pendingTradePeerIds.Remove(key);

            if (_tradePeers.TryGetValue(userId, out var peer))
            {
                _tradePeers.Remove(userId);
                _tradeSessionIds.Remove(userId);
                _tradeOffers.Remove(userId);
                _tradeFinalReady.Remove(userId);
                _tradeOfferReady.Remove(userId);
                _tradeOfferStageOpen.Remove(userId);
                if (_tradePeers.TryGetValue(peer, out var reverse)
                    && reverse == userId)
                {
                    _tradePeers.Remove(peer);
                    _tradeSessionIds.Remove(peer);
                    _tradeOffers.Remove(peer);
                    _tradeFinalReady.Remove(peer);
                    _tradeOfferReady.Remove(peer);
                    _tradeOfferStageOpen.Remove(peer);
                }
            }
        }

        private bool HasPendingTradeInviteLocked(ushort userId)
        {
            return _pendingTradePeerIds.Keys.Any(
                key => (ushort)(key >> 32) == userId || (ushort)key == userId);
        }

        private bool TryGetTradePeerLocked(
            EnhancedClientSession session,
            out ushort peer)
        {
            peer = 0;
            var uid = session?.Player?.UserId ?? (ushort)0;
            return uid != 0
                && _tradeSessionIds.TryGetValue(uid, out var sessionId)
                && sessionId == session.SessionId
                && _tradePeers.TryGetValue(uid, out peer)
                && _tradeSessionIds.ContainsKey(peer);
        }

        private void ClearTradeStateForSession(ushort userId, Guid sessionId)
        {
            lock (_tradePairsLock)
            {
                var staleInvites = _pendingTradePeerIds
                    .Where(pair =>
                        ((ushort)(pair.Key >> 32) == userId
                         && pair.Value.InviterSessionId == sessionId)
                        || ((ushort)pair.Key == userId
                            && pair.Value.AccepterSessionId == sessionId))
                    .Select(pair => pair.Key)
                    .ToArray();
                foreach (var key in staleInvites)
                    _pendingTradePeerIds.Remove(key);

                if (_tradeSessionIds.TryGetValue(userId, out var activeSessionId)
                    && activeSessionId == sessionId)
                    ClearTradePairLocked(userId);
            }
        }

        // 报价变动后 ready 状态整体回落; 返回需要向双方补发 state=0 的 uid 集合。
        private ushort[] ResetTradeReadyLocked(ushort first, ushort second)
        {
            var resetUsers = new List<ushort>(2);
            var anyAdvanced = _tradeOfferStageOpen.Contains(first)
                               || _tradeOfferStageOpen.Contains(second)
                               || _tradeFinalReady.Contains(first)
                               || _tradeFinalReady.Contains(second);
            if (_tradeOfferReady.Contains(first)
                || _tradeFinalReady.Contains(first))
                resetUsers.Add(first);
            // 只有阶段已经推进过, 对手侧的 ready 才需要显式回落
            // (双方同时放物的竞态下避免误清对方刚置的标记)。
            if (anyAdvanced
                && (_tradeOfferReady.Contains(second)
                    || _tradeFinalReady.Contains(second)))
                resetUsers.Add(second);

            _tradeOfferReady.Remove(first);
            if (anyAdvanced)
                _tradeOfferReady.Remove(second);
            _tradeOfferStageOpen.Remove(first);
            _tradeOfferStageOpen.Remove(second);
            _tradeFinalReady.Remove(first);
            _tradeFinalReady.Remove(second);
            return resetUsers.ToArray();
        }

        // 交易窗物品槽 3..26(0=金币位, 1/2 预留), 找第一个空槽。
        private static short FindAvailableTradeSlot(
            IDictionary<short, PlayerTradeOffer> offers)
        {
            for (short slot = 3; slot <= 26; slot++)
            {
                if (!offers.ContainsKey(slot))
                    return slot;
            }
            return -1;
        }

        private static ulong MakeDirectedTradeKey(ushort inviter, ushort accepter)
        {
            return ((ulong)inviter << 32) | accepter;
        }

        private bool TryTakeTradeInvite(
            ushort inviter,
            ushort accepter,
            Guid inviterSessionId,
            Guid accepterSessionId,
            out int peerInt)
        {
            peerInt = 0;
            lock (_tradePairsLock)
            {
                var key = MakeDirectedTradeKey(inviter, accepter);
                if (!_pendingTradePeerIds.TryGetValue(key, out var pending)
                    || pending.InviterSessionId != inviterSessionId
                    || pending.AccepterSessionId != accepterSessionId)
                    return false;
                _pendingTradePeerIds.Remove(key);
                peerInt = pending.PeerInt;
                return true;
            }
        }

        private bool TryClearTradeInvite(
            ushort inviter,
            ushort accepter,
            Guid inviterSessionId,
            Guid accepterSessionId)
        {
            lock (_tradePairsLock)
            {
                var key = MakeDirectedTradeKey(inviter, accepter);
                if (!_pendingTradePeerIds.TryGetValue(key, out var pending)
                    || pending.InviterSessionId != inviterSessionId
                    || pending.AccepterSessionId != accepterSessionId)
                    return false;
                return _pendingTradePeerIds.Remove(key);
            }
        }
    }
}
