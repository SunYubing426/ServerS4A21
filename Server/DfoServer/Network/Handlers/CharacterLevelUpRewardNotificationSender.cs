using DfoServer.Game.Progression;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal static class CharacterLevelUpRewardNotificationSender
    {
        internal static async Task SendAsync(
            EnhancedClientSession session,
            IReadOnlyList<CharacterLevelUpRewardDelivery> deliveredRewards)
        {
            if (session == null)
                return;

            await SendCoreAsync(
                (type, body) => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    type,
                    body)),
                deliveredRewards);
        }

        internal static async Task SendAsync(
            ISessionPacketSender session,
            IReadOnlyList<CharacterLevelUpRewardDelivery> deliveredRewards)
        {
            if (session == null)
                return;

            await SendCoreAsync(
                (type, body) => session.SendNotiAsync(type, body),
                deliveredRewards);
        }

        private static async Task SendCoreAsync(
            Func<ushort, byte[], Task> send,
            IReadOnlyList<CharacterLevelUpRewardDelivery> deliveredRewards)
        {
            if (send == null || deliveredRewards == null)
                return;

            var alarmCount = 0;
            foreach (var delivery in deliveredRewards)
            {
                if (delivery?.Reward != null && delivery.NotifyMailboxAlarm)
                    alarmCount++;
            }

            if (alarmCount > 0)
            {
                await send(
                    (ushort)NotiPacketTypeA21.MAILBOX_ALARM,
                    BuildMailboxAlarmBody(alarmCount));
            }

            foreach (var delivery in deliveredRewards)
            {
                var reward = delivery?.Reward;
                if (reward == null)
                    continue;

                await send(
                    (ushort)NotiPacketTypeA21.SERVER_BROADCAST_TIME_MESSAGE,
                    ServerBroadcastTimeMessageBuilder.Build(
                        $"获得{reward.Level}级升级奖励，请到邮件查收"));
            }

        }

        private static byte[] BuildMailboxAlarmBody(int mailCount)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16((ushort)Math.Min(ushort.MaxValue, mailCount));
            return writer.ToArray();
        }
    }
}
