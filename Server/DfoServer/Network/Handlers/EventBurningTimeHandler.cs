using System;
using System.Threading.Tasks;
using DfoServer.Game.Events.BurningTime;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Events;

namespace DfoServer.Network.Handlers
{
    internal sealed class EventBurningTimeHandler
    {
        private readonly BurningTimeService _service;

        internal EventBurningTimeHandler(BurningTimeService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        internal Task NotifyStateOnLoginAsync(EnhancedClientSession session)
        {
            if (session?.Player?.CharacterId <= 0)
                return Task.CompletedTask;

            var snapshot = _service.GetSnapshot();
            if (!snapshot.EventEnabled)
                return Task.CompletedTask;

            var packet = BurningTimePacketBuilder.BuildStatePacket(snapshot);
            _service.ApplyActiveBuffs(session.Player.CharacterId);
            return SessionDirectory.TrySendBestEffortAsync(
                cancellationToken =>
                    session.SendPacketAsync(packet, cancellationToken),
                $"burningtime state login cid={session.Player.CharacterId}");
        }
    }
}
