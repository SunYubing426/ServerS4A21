using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DfoServer.Game.Events.OnlineAttendance;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Events;
using DfoServer.Network.Parsers.Events;

namespace DfoServer.Network.Handlers
{
    internal sealed class EventOnlineAttendanceHandler
    {
        private const string TimerPrefix = "event:online-attendance:";

        private readonly OnlineAttendanceService _service;
        private readonly ISessionDirectory _sessions;
        private ClockService _clock;
        private bool _clockRegistered;

        internal EventOnlineAttendanceHandler(
            OnlineAttendanceService service,
            ISessionDirectory sessions = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _sessions = sessions;
        }

        internal void RegisterClock(ClockService clock)
        {
            if (clock == null || _sessions == null)
                return;

            if (_clockRegistered)
                return;
            _clockRegistered = true;
            _clock = clock;

            clock.RegisterMinuteTick(
                "event:online-attendance:flush",
                utcNow => { _ = NotifyOnMinuteTickAsync(utcNow); });
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

            _service.BeginSession(session.SessionId, accountId, characterId);
            if (!_service.TryGetSnapshot(
                    accountId,
                    characterId,
                    out var snapshot))
            {
                return;
            }

            await SendStateAsync(session, snapshot, "login");
        }

        internal Task NotifySessionEndingAsync(
            EnhancedClientSession session,
            string reason)
        {
            if (session == null)
                return Task.CompletedTask;

            _service.EndSession(session.SessionId);
            _clock?.CancelOneShotsByPrefix(
                TimerPrefix + session.SessionId.ToString("N"));
            return Task.CompletedTask;
        }

        internal async Task HandleAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!OnlineAttendanceRequestParser.TryParse(body, out var command))
            {
                await SendAckAsync(session, header.type);
                FileLogger.Log(
                    "[OnlineAttendance] rejected request "
                    + $"bodyLength={body?.Length ?? 0}");
                return;
            }

            if (!TryGetIdentity(
                    session,
                    out var accountId,
                    out var characterId))
            {
                await SendAckAsync(session, header.type);
                FileLogger.Log(
                    "[OnlineAttendance] rejected request without active character");
                return;
            }

            _service.BeginSession(session.SessionId, accountId, characterId);
            OnlineAttendanceClaimResult result;
            if (command.Kind == OnlineAttendanceRequestKind.TimeReward)
            {
                result = _service.ClaimTimeReward(
                    accountId,
                    characterId,
                    DecodeCharacterName(session),
                    session.Player?.Level ?? 0,
                    command.StageIndex);
            }
            else if (command.Kind == OnlineAttendanceRequestKind.SumReward)
            {
                result = _service.ClaimSumReward(
                    accountId,
                    characterId,
                    DecodeCharacterName(session),
                    session.Player?.Level ?? 0,
                    command.StageIndex);
            }
            else
            {
                await SendStateAsync(session, null, "query");
                return;
            }

            await SendAckAsync(session, header.type);
            if (result.MailDelivered)
                await SendMailboxAlarmAsync(session);
            if (result.Snapshot != null)
                await SendStateAsync(session, result.Snapshot, "claim");

            if (!result.Success)
            {
                FileLogger.Log(
                    "[OnlineAttendance] claim skipped "
                    + $"account_id={accountId} cid={characterId} "
                    + $"kind={command.Kind} stage={command.StageIndex} "
                    + $"status={result.Status}");
            }
        }

        private async Task NotifyOnMinuteTickAsync(DateTime utcNow)
        {
            if (_sessions == null)
                return;

            var sessions = _sessions.GetAllGameSessions();
            if (sessions.Count == 0)
                return;

            var tasks = new List<Task>(sessions.Count);
            foreach (var session in sessions)
            {
                if (!TryGetIdentity(
                        session,
                        out var accountId,
                        out var characterId))
                {
                    continue;
                }

                _service.BeginSession(session.SessionId, accountId, characterId);
                if (!_service.TryGetSnapshot(
                        accountId,
                        characterId,
                        out var snapshot))
                {
                    continue;
                }

                tasks.Add(SendStateAsync(session, snapshot, "minute"));
            }

            if (tasks.Count > 0)
                await Task.WhenAll(tasks);
        }

        private static Task SendAckAsync(
            EnhancedClientSession session,
            ushort type)
        {
            if (session == null)
                return Task.CompletedTask;

            return session.SendPacketAsync(
                OnlineAttendancePacketBuilder.BuildAckPacket(type));
        }

        private static Task<bool> SendStateAsync(
            EnhancedClientSession session,
            OnlineAttendanceSnapshot snapshot,
            string reason)
        {
            if (session == null
                || snapshot == null
                || snapshot.CharacterId <= 0
                || session.Player?.CharacterId != snapshot.CharacterId)
            {
                return Task.FromResult(false);
            }

            var packet = OnlineAttendancePacketBuilder.BuildStatePacket(snapshot);
            return SessionDirectory.TrySendBestEffortAsync(
                cancellationToken =>
                    session.SendPacketAsync(packet, cancellationToken),
                $"onlineattendance state {reason ?? "unknown"} "
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
                $"onlineattendance mailbox alarm cid={session.Player.CharacterId}");
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
