using DfoServer.Game.Guilds;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Guilds;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    /// <summary>
    /// 公会仓库协议(2026-09-07 对齐参考包 GuildWarehouseHandler):
    /// - CMD 0x0105 GUILD_CARGO        查询(空 body) → ACK 快照 / [0,36]
    /// - CMD 0x00F7 GUILD_CARGO_PUSH_ITEM 存入(13B)
    /// - CMD 0x00F8 GUILD_CARGO_POP_ITEM  取出(11B)
    /// - CMD 0x00F9 GUILD_CARGO_MOVE_ITEM 移动(12B)
    /// - NOTI 0x00ED GUILD_CARGO       快照刷新(操作后本人 + 其他在线同会成员)
    /// 权限 = bit15(仓库); 副本内拒绝; 提交后其他成员重读快照(不用旧广播覆盖)。
    /// </summary>
    internal static class GuildWarehouseHandler
    {
        private const ushort CmdGuildCargo = 0x0105;
        private const ushort CmdGuildCargoPush = 0x00F7;
        private const ushort CmdGuildCargoPop = 0x00F8;
        private const ushort CmdGuildCargoMove = 0x00F9;

        private static InventoryRefreshSender _refresh;
        private static ISessionDirectory _sessions;

        internal static void Bind(InventoryRefreshSender refresh, ISessionDirectory sessions)
        {
            _refresh = refresh;
            _sessions = sessions;
        }

        private static byte[] Ack(bool success, byte error = 1)
            => success ? new byte[] { 1 } : new byte[] { 0, error };

        internal static async Task Handle(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            var aid = session?.Player?.UserId ?? 0;
            if (cid <= 0 || aid <= 0 || session?.Player == null)
                return;
            var type = (ushort)header.type;
            Task Reply(byte[] b) => session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(1, type, b));

            // 副本内拒绝(参考包同规则)
            if (session.Player.CurrentRun != null)
            {
                await Reply(Ack(false, 8));
                return;
            }

            if (type == CmdGuildCargo)
            {
                // 查询: 严格空请求, 非空尾部拒绝
                if (body != null && body.Length != 0)
                {
                    await Reply(Ack(false, 8));
                    return;
                }
                var snapshot = ReadSnapshotFor(cid);
                await Reply(snapshot == null
                    ? Ack(false, 36)
                    : GuildWarehousePacketBuilder.Snapshot(snapshot, true));
                return;
            }

            if (!TryParseRequest(type, body, out var request))
            {
                await Reply(Ack(false, 8));
                return;
            }
            if (!InventoryContext.TryGetOwnedLease(session.SessionId, cid, out var lease)
                || lease.AccountId != aid)
            {
                await Reply(Ack(false, 8));
                return;
            }

            GuildWarehouseResult result;
            try
            {
                result = GuildWarehouseService.Execute(lease, aid, request);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GuildWarehouse] cid={cid} op={request.Operation} failed: {ex.Message}");
                result = new GuildWarehouseResult(false, Message: "仓库操作失败，未转移物品。");
            }
            FileLogger.Log(
                $"[GuildWarehouse] cid={cid} op={request.Operation} ok={result.Success} " +
                $"src={request.Source} dst={request.Destination} count={request.Count} " +
                $"gid={result.GuildId} msg={result.Message ?? "-"}");

            // 提交结果已成事实; 套接字失败不能撤销或重放
            await Reply(result.Success
                ? GuildWarehousePacketBuilder.Mutation(request.Operation, result)
                : Ack(false, 8));
            if (!result.Success)
            {
                if (result.Message != null)
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0, (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE,
                        ServerNoticeMessageBuilder.Build(result.Message)));
                await RefreshAsync(session);
                return;
            }
            if (request.Operation != GuildWarehouseOperation.Move && _refresh != null)
                await _refresh.SendUpdateItemList(
                    session, InventoryListType.Main,
                    request.Operation == GuildWarehouseOperation.Push
                        ? result.Source
                        : result.Destination);
            await RefreshAsync(session);

            // 其他在线同会成员: 逐个重读快照后推送(读在发送前, 并发提交不会重放旧快照)
            if (_sessions == null)
                return;
            foreach (var target in _sessions.GetAllGameSessions())
            {
                var tCid = target?.Player?.CharacterId ?? 0;
                if (tCid <= 0 || tCid == cid)
                    continue;
                try
                {
                    await RefreshAsync(target, result.GuildId);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[GuildWarehouse] refresh cid={tCid}: {ex.Message}");
                }
            }
        }

        /// <summary>权限(bit15)校验后读快照; 无公会/无权限返回 null。</summary>
        private static GuildWarehouseSnapshot ReadSnapshotFor(int cid)
        {
            var guild = GuildSystem.GetGuildOfCharacter(cid);
            if (guild == null || !GuildSystem.HasPermission(cid, GuildPermissions.Warehouse))
                return null;
            return GuildWarehouseRepository.ReadSnapshot(guild.GuildId);
        }

        /// <summary>仓库扩容购买(0x02F8 id11/12)后向全会在线成员推送最新快照。</summary>
        internal static async Task BroadcastRefreshAsync(int guildId)
        {
            if (_sessions == null || guildId <= 0)
                return;
            foreach (var target in _sessions.GetAllGameSessions())
            {
                try
                {
                    await RefreshAsync(target, guildId);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[GuildWarehouse] broadcast gid={guildId}: {ex.Message}");
                }
            }
        }

        /// <summary>NOTI 0x00ED 快照刷新; requiredGuild 不匹配/无权限时不发。</summary>
        private static async Task RefreshAsync(EnhancedClientSession session, int requiredGuild = 0)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            if (cid <= 0)
                return;
            var snapshot = ReadSnapshotFor(cid);
            if (snapshot == null || (requiredGuild > 0 && snapshot.GuildId != requiredGuild))
                return;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0, (ushort)NotiPacketTypeA21.GUILD_CARGO,
                GuildWarehousePacketBuilder.Snapshot(snapshot, false)));
        }

        /// <summary>请求解析(参考包 GuildWarehouseRequestParser 的 native 合同, 严格长度)。</summary>
        private static bool TryParseRequest(ushort type, byte[] body, out GuildWarehouseRequest request)
        {
            request = null;
            if (type == CmdGuildCargoPush && body?.Length == 13 && body[0] == 0)
            {
                request = new GuildWarehouseRequest(
                    GuildWarehouseOperation.Push,
                    BitConverter.ToInt16(body, 1),
                    BitConverter.ToInt32(body, 3),
                    BitConverter.ToInt32(body, 7),
                    BitConverter.ToInt16(body, 11));
            }
            else if (type == CmdGuildCargoPop && body?.Length == 11 && body[10] == 0)
            {
                request = new GuildWarehouseRequest(
                    GuildWarehouseOperation.Pop,
                    BitConverter.ToInt16(body, 0),
                    BitConverter.ToInt32(body, 2),
                    BitConverter.ToInt32(body, 6));
            }
            else if (type == CmdGuildCargoMove && body?.Length == 12)
            {
                request = new GuildWarehouseRequest(
                    GuildWarehouseOperation.Move,
                    BitConverter.ToInt16(body, 0),
                    BitConverter.ToInt32(body, 2),
                    0,
                    BitConverter.ToInt16(body, 6),
                    BitConverter.ToInt32(body, 8));
            }
            return request != null
                && request.Source >= 0 && request.Destination >= 0 && request.ExpectedItemId > 0
                && (request.Operation == GuildWarehouseOperation.Move
                    ? request.ExpectedDestinationId >= 0
                    : request.Count > 0);
        }
    }
}
