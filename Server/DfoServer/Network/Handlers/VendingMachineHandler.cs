using System;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Inventory;
using DfoServer.Game.Premium;
using DfoServer.Game.Vending;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Vending;

namespace DfoServer.Network.Handlers
{
    internal sealed class VendingMachineHandler
    {
        internal const ushort CommandType = (ushort)CmdPacketTypeA21.USE_VENDING_MACHINE;
        private readonly InventoryRefreshSender _refresh;
        private readonly VendingMachineService _service = new();

        internal VendingMachineHandler(InventoryRefreshSender refresh)
            => _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));

        internal async Task HandleUse(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (session?.Player == null) return;
            var player = session.Player;
            var (characterId, accountId) = InventoryHandler.ResolveOwner(session);
            if (!VendingMachineRequest.TryParse(body, out var request)
                || characterId <= 0 || accountId <= 0 || player.CurrentRun != null
                || player.CurrentDungeonSelection != null
                || !InventoryContext.TryGetOwnedLease(session.SessionId, characterId, out var lease))
            {
                await SendFailure(session, "invalid-request-or-state");
                return;
            }
            VendingMachineDefinition definition;
            try
            {
                if (!VendingMachineCatalog.TryGet(request.MachineId, out definition))
                {
                    await SendFailure(session, "unsupported-machine");
                    return;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Vending] catalog error: {ex.Message}");
                await SendFailure(session, "catalog-unavailable");
                return;
            }
            // PVF 读取可能较慢，事务前重新核对 session / player / lease。
            if (!ReferenceEquals(player, session.Player) || player.CurrentRun != null
                || player.CurrentDungeonSelection != null)
            {
                await SendFailure(session, "state-changed");
                return;
            }
            if (!_service.TryDraw(lease, session.SessionId, accountId, request, definition,
                    out var result, out var rejection))
            {
                await SendFailure(session, rejection);
                return;
            }
            FileLogger.Log($"[Vending] committed account={accountId} char={characterId} machine={request.MachineId} group={request.GroupId} coin={definition.CoinItemId}@{request.CoinSlot} remaining={result.CoinRemaining} prize={result.Prize.ItemId}x{result.Prize.Count} records={result.Rewards.Count} premium={result.PremiumType}");
            if (!ReferenceEquals(player, session.Player)
                || !InventoryContext.IsCurrentLease(lease, session.SessionId, characterId)) return;

            // 客户端 114369C 会先查源硬币。必须先 ACK，不能先发 0x0E 删除源槽。
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, CommandType,
                VendingMachineAckBuilder.BuildSuccess(result)));
            if (result.PremiumType > 0)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00,
                    (ushort)NotiPacketTypeA21.CERA_SPECIALITEM,
                    PremiumService.BuildCeraSpecialItemNotification(result.PremiumType, result.PremiumRemaining)));
                await PremiumService.SendPremiumServiceRefresh(session, accountId, lease.Inventory.Database);
            }
            await _refresh.SendUpdateItemList(session, InventoryListType.Main, result.CoinSlot);
            foreach (var group in result.Rewards.Where(reward => reward.ListType != 6).GroupBy(reward => reward.ListType))
                await _refresh.SendUpdateItemList(session, (InventoryListType)group.Key, group.Select(reward => reward.Slot));
        }

        internal static async Task SendFailure(EnhancedClientSession session, string reason)
        {
            FileLogger.Log($"[Vending] rejected char={session.Player?.CharacterId ?? 0} reason={reason}; no reward committed");
            var inventoryFull = reason?.StartsWith("reward-container-full:", StringComparison.Ordinal) == true;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, CommandType,
                VendingMachineAckBuilder.BuildFailure(inventoryFull)));
        }
    }
}
