using DfoServer.Game.Progression;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using System;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class CharacterLevelUpRewardSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== CHARACTER_LEVEL_UP_REWARD selftest ===");
            var failures = 0;
            var rewards = CharacterLevelUpRewardService.ParseRewards(@"
[level up reward]
70 1 2 3330 250
[/level up reward]
[level up reward]
75 1161 1162 3330 300
[/level up reward]
");

            Check("PVF reward parser reads level 70", rewards.ContainsKey(70), ref failures);
            Check("PVF reward parser keeps item and count", rewards[70].ItemId == 3330 && rewards[70].ItemCount == 250, ref failures);

            var plan = CharacterLevelUpRewardService.PlanFrom(rewards, 69, 75);
            Check("level jump plans both configured reward levels", plan.Count == 2 && plan[0].Level == 70 && plan[1].Level == 75, ref failures);

            var recoveryPlan = CharacterLevelUpRewardService.PlanFrom(rewards, 0, 75);
            Check("login recovery plans every configured reward through current level", recoveryPlan.Count == 2 && recoveryPlan[0].Level == 70 && recoveryPlan[1].Level == 75, ref failures);

            var mail = CharacterLevelUpRewardService.BuildMail(1001, 2001, "Test", rewards[70]);
            Check("mail text contains reached level", mail.Title == "恭喜你达到70级，获得升级奖励" && mail.Text == mail.Title, ref failures);
            Check("mail uses official system type", mail.MailType != 0, ref failures);
            Check("mail uses character-level idempotency", mail.IdempotencyKey == "level-up-reward:1001:70", ref failures);
            Check("mail attaches configured invitation letters", mail.Attachments.Count == 1 && mail.Attachments[0].ItemId == 3330 && mail.Attachments[0].ItemCount == 250, ref failures);
            var replay = new DfoServer.Game.Mailbox.MailboxSendResult { Success = true, AlreadyExists = true };
            Check("idempotent replay is distinguishable from a newly inserted mail", replay.Success && replay.AlreadyExists, ref failures);
            Check("idempotent replay does not project reward UI", !CharacterLevelUpRewardService.IsNewDelivery(replay), ref failures);
            Check("newly inserted mail projects reward UI", CharacterLevelUpRewardService.IsNewDelivery(
                new DfoServer.Game.Mailbox.MailboxSendResult { Success = true }), ref failures);

            var notice = ServerBroadcastTimeMessageBuilder.Build("获得70级升级奖励，请到邮件查收");
            Check("personal reward notice writes 5 second duration", BitConverter.ToInt32(notice, 0) == 5000, ref failures);
            var expectedNoticeText = ClientTextEncoding.GetBytes("获得70级升级奖励，请到邮件查收");
            Check("personal reward notice uses A21 client-encoded DString text", notice.Length > 4, ref failures);
            Check("personal reward notice contains GBK text bytes", ContainsBytes(notice, expectedNoticeText), ref failures);
            Check("personal reward notice uses A21 0x0343", (ushort)NotiPacketTypeA21.SERVER_BROADCAST_TIME_MESSAGE == 0x0343, ref failures);
            Check("new-mail reminder uses A21 mailbox alarm type", (ushort)NotiPacketTypeA21.MAILBOX_ALARM == 0x0063, ref failures);

            Console.WriteLine(failures == 0
                ? "CHARACTER_LEVEL_UP_REWARD selftest passed."
                : $"CHARACTER_LEVEL_UP_REWARD selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }

        private static bool ContainsBytes(byte[] haystack, byte[] needle)
        {
            if (haystack == null || needle == null || needle.Length == 0 || needle.Length > haystack.Length)
                return false;
            for (var i = 0; i <= haystack.Length - needle.Length; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    return true;
            }
            return false;
        }
    }
}
