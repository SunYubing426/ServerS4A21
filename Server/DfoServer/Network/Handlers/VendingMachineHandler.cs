using System;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Inventory;
using DfoServer.Game.Premium;
using DfoServer.Game.VendingMachine;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;

namespace DfoServer.Network.Handlers
{
    internal sealed class VendingMachineHandler
    {
        private readonly VendingMachineService _service;
        private readonly InventoryRefreshSender _refresh;
        private readonly IGameDatabase _database;
        internal VendingMachineHandler(VendingMachineService service, InventoryRefreshSender refresh, IGameDatabase database)
        {
            _service = service;
            _refresh = refresh;
            _database = database;
        }

        internal async Task HandleUse(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (characterId, accountId) = SessionOwnerResolver.Resolve(session);
            VendingMachineResult result = null;
            var parsed = VendingMachineUseRequest.TryParse(body, out var request);
            if (parsed && session.Player != null && session.Player.CurrentRun == null
                && InventoryContext.TryGetLease(characterId, out var lease))
                _service.TryUse(lease, session.SessionId, accountId, request.MachineId, request.GroupId, request.SourceSlot, out result);

            var response = VendingMachineResultBuilder.Build(result);
            // Log the committed outcome before socket I/O; disconnect cannot undo the durable reward.
            FileLogger.Log($"[VendingMachine] cid={characterId} account={accountId} machine={request?.MachineId} "
                + $"group={request?.GroupId} slot={request?.SourceSlot} success={result != null} "
                + $"reward={result?.Reward.ItemId}x{result?.Reward.Count} remaining={result?.RemainingTokens} "
                + $"mailbox={result?.DeliveredToMailbox} premium={result?.PremiumType} response={response.Length}B");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01,
                (ushort)CmdPacketTypeA21.USE_VENDING_MACHINE, response));
            if (result == null) return;
            foreach (var changes in result.Changes.Slots.GroupBy(s => s.ListType))
                await _refresh.SendUpdateItemList(session, changes.Key, changes.Select(s => s.SlotIndex));
            if (result.DeliveredToMailbox)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00,
                    (ushort)NotiPacketTypeA21.MAILBOX_ALARM, MailboxHandler.BuildMailboxAlarmNotification(1)));
            if (result.PremiumType > 0)
                await PremiumService.NotifyCommittedContract(session, accountId, result.PremiumType,
                    result.PremiumRemaining, _database);
        }
    }
}


