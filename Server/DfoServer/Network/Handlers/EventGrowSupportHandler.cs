using System;
using System.Threading.Tasks;
using DfoServer.Game.Events.GrowSupport;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Events;

namespace DfoServer.Network.Handlers
{
    internal sealed class EventGrowSupportHandler
    {
        private readonly GrowSupportService _service;

        internal EventGrowSupportHandler(GrowSupportService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        internal async Task NotifyStateOnLoginAsync(EnhancedClientSession session)
        {
            if (!TryGetIdentity(
                    session,
                    out var accountId,
                    out var characterId))
            {
                return;
            }

            if (!_service.TryGetSnapshot(
                    accountId,
                    characterId,
                    session.Player?.Level ?? 0,
                    out var snapshot))
            {
                return;
            }

            await SendStateAsync(session, snapshot, "login");
        }

        internal async Task HandleLevelUpRewardAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!TryGetIdentity(
                    session,
                    out var accountId,
                    out var characterId))
            {
                await SendAckAsync(session, header.type);
                return;
            }

            var result = _service.ClaimLevelUpReward(
                accountId,
                characterId,
                DecodeCharacterName(session),
                session.Player?.Level ?? 0);

            await SendAckAsync(session, header.type);
            if (result.MailDelivered)
                await SendMailboxAlarmAsync(session);
            if (result.Snapshot != null)
                await SendStateAsync(session, result.Snapshot, "levelup");
        }

        internal async Task HandleDungeonClearRewardAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!TryGetIdentity(
                    session,
                    out var accountId,
                    out var characterId))
            {
                await SendAckAsync(session, header.type);
                return;
            }

            var requiredClears = body.Length >= 4
                ? BitConverter.ToInt32(body, 0)
                : 0;
            var result = _service.ClaimDungeonClearReward(
                accountId,
                characterId,
                DecodeCharacterName(session),
                session.Player?.Level ?? 0,
                requiredClears);

            await SendAckAsync(session, header.type);
            if (result.MailDelivered)
                await SendMailboxAlarmAsync(session);
            if (result.Snapshot != null)
                await SendStateAsync(session, result.Snapshot, "dungeon");
        }

        private static Task SendAckAsync(
            EnhancedClientSession session,
            ushort type)
        {
            if (session == null)
                return Task.CompletedTask;

            return session.SendPacketAsync(
                GrowSupportPacketBuilder.BuildAckPacket(type));
        }

        private static Task<bool> SendStateAsync(
            EnhancedClientSession session,
            GrowSupportSnapshot snapshot,
            string reason)
        {
            if (session == null
                || snapshot == null
                || snapshot.CharacterId <= 0
                || session.Player?.CharacterId != snapshot.CharacterId)
            {
                return Task.FromResult(false);
            }

            var packet = GrowSupportPacketBuilder.BuildStatePacket(snapshot);
            return SessionDirectory.TrySendBestEffortAsync(
                cancellationToken =>
                    session.SendPacketAsync(packet, cancellationToken),
                $"growsupport state {reason ?? "unknown"} "
                + $"cid={snapshot.CharacterId}");
        }

        private static Task<bool> SendMailboxAlarmAsync(
            EnhancedClientSession session)
        {
            if (session?.Player?.CharacterId <= 0)
                return Task.FromResult(false);

            var packet = GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.MAILBOX_ALARM,
                MailboxHandler.BuildMailboxAlarmNotification(1));
            return SessionDirectory.TrySendBestEffortAsync(
                cancellationToken => session.SendPacketAsync(
                    packet,
                    cancellationToken),
                $"growsupport mailbox alarm cid={session.Player.CharacterId}");
        }

        private static bool TryGetIdentity(
            EnhancedClientSession session,
            out int accountId,
            out int characterId)
        {
            accountId = session?.Account?.AccountId ?? 0;
            characterId = session?.Player?.CharacterId ?? 0;
            return accountId > 0 && characterId > 0;
        }

        private static string DecodeCharacterName(EnhancedClientSession session)
        {
            try
            {
                return ClientTextEncoding.GetString(
                    session?.Player?.Name ?? Array.Empty<byte>());
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
