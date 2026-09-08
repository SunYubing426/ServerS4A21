using DfoServer.Game.Mailbox;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Progression
{
    internal sealed class CharacterLevelUpReward
    {
        internal byte Level { get; set; }
        internal int ItemId { get; set; }
        internal int ItemCount { get; set; }
        internal int Parameter1 { get; set; }
        internal int Parameter2 { get; set; }
    }

    internal sealed class CharacterLevelUpRewardDelivery
    {
        internal CharacterLevelUpReward Reward { get; set; }
        internal bool NotifyMailboxAlarm { get; set; }
    }

    internal static class CharacterLevelUpRewardService
    {
        private const string RewardFile = "etc/serverparameter_chn.etc";
        private static readonly object Sync = new object();
        private static IReadOnlyDictionary<byte, CharacterLevelUpReward> _rewards;

        internal static IReadOnlyDictionary<byte, CharacterLevelUpReward> LoadRewards()
        {
            if (_rewards != null)
                return _rewards;

            lock (Sync)
            {
                if (_rewards != null)
                    return _rewards;

                var parsed = ParseRewards(PvfArchiveAccessor.ReadText(RewardFile));
                _rewards = parsed;
                FileLogger.Log($"[Progression] level-up rewards loaded: {parsed.Count} entries");
                return _rewards;
            }
        }

        internal static IReadOnlyDictionary<byte, CharacterLevelUpReward> ParseRewards(string text)
        {
            var parsed = new Dictionary<byte, CharacterLevelUpReward>();
            text = text ?? string.Empty;
                var matches = Regex.Matches(
                    text,
                    @"\[level\s+up\s+reward\](?<body>.*?)\[/level\s+up\s+reward\]",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                foreach (Match match in matches)
                {
                    var tokens = Regex.Matches(match.Groups["body"].Value, @"-?\d+");
                    if (tokens.Count < 5
                        || !byte.TryParse(tokens[0].Value, out var level)
                        || !int.TryParse(tokens[1].Value, out var parameter1)
                        || !int.TryParse(tokens[2].Value, out var parameter2)
                        || !int.TryParse(tokens[3].Value, out var itemId)
                        || !int.TryParse(tokens[4].Value, out var itemCount)
                        || level == 0
                        || itemId <= 0
                        || itemCount <= 0)
                    {
                        continue;
                    }

                    parsed[level] = new CharacterLevelUpReward
                    {
                        Level = level,
                        ItemId = itemId,
                        ItemCount = itemCount,
                        Parameter1 = parameter1,
                        Parameter2 = parameter2,
                    };
                }

            return parsed;
        }

        internal static IReadOnlyList<CharacterLevelUpRewardDelivery> Deliver(
            MailboxService mailbox,
            int characterId,
            int accountId,
            string characterName,
            ExperienceGrantResult grant)
        {
            if (grant == null)
                return Array.Empty<CharacterLevelUpRewardDelivery>();

            return Deliver(mailbox, characterId, accountId, characterName, grant.PreviousLevel, grant.NewLevel);
        }

        internal static IReadOnlyList<CharacterLevelUpRewardDelivery> Deliver(
            MailboxService mailbox,
            int characterId,
            int accountId,
            string characterName,
            byte previousLevel,
            byte newLevel)
        {
            var delivered = new List<CharacterLevelUpRewardDelivery>();
            if (mailbox == null || newLevel <= previousLevel)
                return delivered;

            foreach (var reward in Plan(previousLevel, newLevel))
            {
                MailboxSystemMailDeliveryResult delivery;
                try
                {
                    delivery = mailbox.SendSystemMailWithAlarmDecision(
                        BuildMail(characterId, accountId, characterName, reward));
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[Progression] level-up reward mail threw "
                        + $"cid={characterId} level={reward.Level} error={ex}");
                    continue;
                }

                var send = delivery.SendResult;
                if (!IsNewDelivery(send))
                {
                    if (send == null || !send.Success)
                    {
                        FileLogger.Log(
                            $"[Progression] level-up reward mail failed "
                            + $"cid={characterId} level={reward.Level} "
                            + $"error={send?.Error}");
                    }
                    continue;
                }

                delivered.Add(new CharacterLevelUpRewardDelivery
                {
                    Reward = reward,
                    NotifyMailboxAlarm = delivery.NotifyMailboxAlarm,
                });
            }

            return delivered;
        }

        internal static bool IsNewDelivery(MailboxSendResult send)
        {
            return send != null && send.Success && !send.AlreadyExists;
        }

        // A level change is persisted before the mail operation. Rechecking all
        // configured rewards at character entry makes a transient mail failure
        // recoverable without a second persistence table. Mailbox idempotency
        // keys ensure existing rewards are not inserted or announced again.
        internal static IReadOnlyList<CharacterLevelUpRewardDelivery> Recover(
            MailboxService mailbox,
            int characterId,
            int accountId,
            string characterName,
            byte currentLevel)
        {
            return Deliver(mailbox, characterId, accountId, characterName, 0, currentLevel);
        }

        internal static IReadOnlyList<CharacterLevelUpReward> Plan(byte previousLevel, byte newLevel)
        {
            return PlanFrom(LoadRewards(), previousLevel, newLevel);
        }

        internal static IReadOnlyList<CharacterLevelUpReward> PlanFrom(
            IReadOnlyDictionary<byte, CharacterLevelUpReward> rewards,
            byte previousLevel,
            byte newLevel)
        {
            var result = new List<CharacterLevelUpReward>();
            if (rewards == null || newLevel <= previousLevel)
                return result;

            for (var level = previousLevel + 1; level <= newLevel; level++)
            {
                if (rewards.TryGetValue((byte)level, out var reward))
                    result.Add(reward);
            }

            return result;
        }

        internal static MailboxSendRequest BuildMail(
            int characterId,
            int accountId,
            string characterName,
            CharacterLevelUpReward reward)
        {
            if (characterId <= 0 || reward == null)
                throw new ArgumentException("Invalid level-up reward request.");

            var text = $"恭喜你达到{reward.Level}级，获得升级奖励";
            return new MailboxSendRequest
            {
                SenderCharacterId = characterId,
                SenderAccountId = accountId,
                SenderName = string.Empty,
                ReceiverCharacterId = characterId,
                ReceiverAccountId = accountId,
                ReceiverName = characterName ?? string.Empty,
                ReceiverLevel = reward.Level,
                Title = text,
                Text = text,
                // Non-zero mail types are rendered by A21 as official mail;
                // type 0 makes the client resolve the sender as a character.
                MailType = 1,
                Unlimited = false,
                ExpireAtUtc = DateTimeOffset.UtcNow.AddDays(30),
                AuditActor = "DNF管理员GM",
                AuditReason = $"character-level-up:{reward.Level}",
                IdempotencyKey = $"level-up-reward:{characterId}:{reward.Level}",
                Attachments = new[]
                {
                    new MailboxSendAttachmentRequest
                    {
                        ItemId = reward.ItemId,
                        ItemCount = reward.ItemCount,
                    },
                },
            };
        }

    }
}
