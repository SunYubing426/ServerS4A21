using System;
using System.Threading.Tasks;
using DfoServer.Game.Events.LoginReward;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Events;

namespace DfoServer.Network.Handlers
{
    internal sealed class EventLoginRewardHandler
    {
        private readonly LoginRewardService _service;

        internal EventLoginRewardHandler(LoginRewardService service)
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
                    out var snapshot))
            {
                return;
            }

            await SendStateAsync(session, snapshot, "login");
            if (!snapshot.AlreadyClaimedToday && snapshot.TodayDayIndex >= 0)
            {
                var result = _service.ClaimTodayReward(
                    accountId,
                    characterId,
                    DecodeCharacterName(session),
                    session.Player?.Level ?? 0);
                if (result.MailDelivered)
                    await SendMailboxAlarmAsync(session);
                if (result.Snapshot != null)
                    await SendStateAsync(session, result.Snapshot, "auto-claim");
            }
        }

        internal async Task HandleClaimAsync(
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
                FileLogger.Log(
                    "[LoginReward] rejected claim without active character");
                return;
            }

            var result = _service.ClaimTodayReward(
                accountId,
                characterId,
                DecodeCharacterName(session),
                session.Player?.Level ?? 0);

            await SendAckAsync(session, header.type);
            if (result.MailDelivered)
                await SendMailboxAlarmAsync(session);
            if (result.Snapshot != null)
                await SendStateAsync(session, result.Snapshot, "claim");

            if (!result.Success)
            {
                FileLogger.Log(
                    "[LoginReward] claim skipped "
                    + $"account_id={accountId} cid={characterId} "
                    + $"status={result.Status}");
            }
        }

        private static Task SendAckAsync(
            EnhancedClientSession session,
            ushort type)
        {
            if (session == null)
                return Task.CompletedTask;

            return session.SendPacketAsync(
                LoginRewardPacketBuilder.BuildAckPacket(type));
        }

        private static Task<bool> SendStateAsync(
            EnhancedClientSession session,
            LoginRewardSnapshot snapshot,
            string reason)
        {
            if (session == null
                || snapshot == null
                || snapshot.CharacterId <= 0
                || session.Player?.CharacterId != snapshot.CharacterId)
            {
                return Task.FromResult(false);
            }

            var packet = LoginRewardPacketBuilder.BuildStatePacket(snapshot);
            return SessionDirectory.TrySendBestEffortAsync(
                cancellationToken =>
                    session.SendPacketAsync(packet, cancellationToken),
                $"loginreward state {reason ?? "unknown"} "
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
                $"loginreward mailbox alarm cid={session.Player.CharacterId}");
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
