using DfoServer.Game.SecretShop;
using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed class SecretShopHandler
    {
        private readonly SecretShopPurchaseService _purchaseService;
        private readonly InventoryRefreshSender _refresh;

        internal SecretShopHandler(InventoryRefreshSender refresh)
        {
            _purchaseService = new SecretShopPurchaseService();
            _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        }

        internal Task HandleOpenClose(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!SecretShopOpenCloseRequest.TryParse(body, out var open))
            {
                FileLogger.Log($"[SecretShop] malformed OPEN_CLOSE body({body?.Length ?? 0}): {(body != null ? BitConverter.ToString(body) : "null")}");
                return Task.CompletedTask;
            }

            var offer = session?.Player?.CurrentRun?.SecretShopOffer;
            FileLogger.Log($"[SecretShop] {(open ? "open" : "close")}: char={session?.Player?.CharacterId ?? 0} npc={offer?.NpcId ?? 0}");
            return Task.CompletedTask;
        }

        internal async Task HandleBuyRequest(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var offer = session?.Player?.CurrentRun?.SecretShopOffer;
            if (!SecretShopBuyRequest.TryParse(body, out var request) || offer == null || !offer.IsSecretShop)
            {
                FileLogger.Log(
                    $"[SecretShop] BUY reject malformed/expired: char={session?.Player?.CharacterId ?? 0} " +
                    $"body({body?.Length ?? 0})={(body != null ? BitConverter.ToString(body) : "null")}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x0128,
                    SecretShopBuyAckBuilder.BuildFailure()));
                return;
            }

            var characterId = session.Player.CharacterId;
            SecretShopPurchaseResult result;
            bool ok;
            if (InventoryContext.TryGetLease(characterId, out var lease) && lease.IsOwnedBy(session.SessionId))
            {
                ok = _purchaseService.TryPurchase(
                    lease,
                    offer,
                    request.ItemId,
                    request.RequestedCount,
                    out result);
            }
            else
            {
                ok = false;
                result = null;
            }

            if (!ok)
            {
                FileLogger.Log($"[SecretShop] BUY reject transaction: char={characterId} npc={offer.NpcId} item={request.ItemId}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x0128,
                    SecretShopBuyAckBuilder.BuildFailure()));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x0128,
                SecretShopBuyAckBuilder.BuildSuccess(result)));

            if (result.GoldCost > 0)
            {
                await _refresh.SendGoldUpdate(session);
            }

            foreach (var group in GroupSlotRefreshes(result.SlotRefreshes))
            {
                await _refresh.SendUpdateItemList(session, group.ListType, group.Slots);
            }

            FileLogger.Log(
                $"[SecretShop] BUY success: char={characterId} npc={offer.NpcId} item={result.ItemId} " +
                $"count={result.ItemCount} goldCost={result.GoldCost} required={result.RequiredItemId}:{result.RequiredItemCount} " +
                $"slot={result.AssignedSlot} refresh={result.SlotRefreshes.Count} gold={result.UpdatedGold}");
        }

        internal static IReadOnlyList<SecretShopRefreshGroup> GroupSlotRefreshes(
            IReadOnlyList<SecretShopSlotRefresh> slots)
        {
            if (slots == null || slots.Count == 0)
                return Array.Empty<SecretShopRefreshGroup>();

            return slots
                .GroupBy(slot => slot.ListType)
                .Select(group => new SecretShopRefreshGroup(
                    group.Key,
                    group.Select(slot => slot.Slot).Distinct().ToArray()))
                .ToArray();
        }
    }

    internal readonly struct SecretShopRefreshGroup
    {
        internal SecretShopRefreshGroup(InventoryListType listType, IReadOnlyList<short> slots)
        {
            ListType = listType;
            Slots = slots ?? Array.Empty<short>();
        }

        internal InventoryListType ListType { get; }
        internal IReadOnlyList<short> Slots { get; }
    }
}
