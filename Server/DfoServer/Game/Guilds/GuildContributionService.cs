using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Guilds
{
    /// <summary>
    /// 公会贡献累计与月奖(2026-09-07 对齐 A21-公会修复源码包 GuildContribution*):
    /// - 捐献: 每 1000 金币 = 1 贡献点(与 GM 同额); 适级通关: 每次 +10。
    /// - 月界: 北京时间每月 1 日 06:00; 月奖:
    ///   公会月总贡献 ≥ 100000 → 全体仍在会成员 公会的威严(10153529)×1;
    ///   个人月贡献 ≥ 1100 → 达标且仍在会成员 公会能量药剂(10153528)×1;
    ///   发件人 = 月结时会长身份(不扣会长资产)。
    /// - 结算为惰性触发: 贡献写入/选角时补结到期月份; 资格 UNIQUE 幂等,
    ///   邮件 IdempotencyKey 去重, 失败可恢复。
    /// </summary>
    public static class GuildContributionService
    {
        public const int DonationGoldPerPoint = 1000;       // 每 1000 金币 = 1 贡献
        public const int ProperClearPoints = 10;            // 适级通关每次 +10
        public const int MemberGoal = 1100;                 // 个人月目标
        public const int MemberRewardItem = 10153528;       // 公会能量药剂
        public const int GuildGoal = 100000;                // 公会月目标
        public const int GuildRewardItem = 10153529;        // 公会的威严
        public const int RewardItemCount = 1;

        public const int SourceDonation = 3;
        public const int SourceClear = 4;

        // 公会地下城(2026-09-07 参考包 GuildCoinRules): 每日第 5 次公会地下城
        // 通关 +10 贡献币; 副本成员关系权威 = PVF guilddungeonstyle.lst。
        public const int GuildDungeonDailyTarget = 5;
        public const int GuildDungeonRewardCoins = 10;

        private static MailboxService _mailbox;

        private static MailboxService Mailbox
        {
            get
            {
                if (_mailbox == null)
                {
                    _mailbox = new MailboxService(
                        new MailboxRepository(
                            ServerPaths.DatabasePath, ServerPaths.SchemaFilePath));
                }
                return _mailbox;
            }
        }

        /// <summary>月键(yyyyMM)与月起止(UTC)。日界=北京 06:00 → UTC+2 对齐 1 日。</summary>
        public static (int Key, DateTime StartUtc, DateTime EndUtc) CurrentMonth(DateTime utcNow)
        {
            if (utcNow.Kind != DateTimeKind.Utc)
                utcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
            var business = utcNow.AddHours(2);
            var first = new DateTime(business.Year, business.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            return (first.Year * 100 + first.Month, first.AddHours(-2), first.AddMonths(1).AddHours(-2));
        }

        /// <summary>0x0046 尾部四值: 公会累计/公会当月/个人累计/个人当月(尽力而为, 异常归 0)。</summary>
        public static (uint GuildLifetime, uint GuildMonth, uint PersonalLifetime, uint PersonalMonth)
            GetTotalsForWire(int guildId, int characterId)
        {
            try
            {
                var month = CurrentMonth(DateTime.UtcNow);
                var t = GuildSystem.Repository.GetContributionTotals(guildId, characterId, month.Key);
                static uint Cap(long v) => (uint)Math.Clamp(v, 0, int.MaxValue);
                return (Cap(t.GuildLifetime), Cap(t.GuildMonth),
                    Cap(t.PersonalLifetime), Cap(t.PersonalMonth));
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GuildContribution] 合计查询失败 gid={guildId}: {ex.Message}");
                return (0, 0, 0, 0);
            }
        }

        /// <summary>捐献贡献(在 DonateGold 成功后调用): 点数 = 花费金币/1000。</summary>
        public static void RecordDonationContribution(
            int characterId, string characterName, int guildId, int goldSpent, int mileage)
        {
            if (guildId <= 0 || goldSpent <= 0 || mileage <= 0)
                return;
            var points = goldSpent / DonationGoldPerPoint;
            var month = CurrentMonth(DateTime.UtcNow);
            try
            {
                EnsurePeriod(guildId, month);
                var inserted = GuildSystem.Repository.TryInsertContributionEvent(
                    characterId, guildId,
                    "donate:" + Guid.NewGuid().ToString("N"),
                    SourceDonation, points, month.Key, characterName,
                    goldSpent, mileage);
                if (inserted)
                    GuildSystem.Repository.AccumulateContribution(
                        guildId, month.Key, characterId, points);
                SettleOverduePeriods();
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GuildContribution] 捐献贡献失败 cid={characterId}: {ex.Message}");
            }
        }

        /// <summary>通关贡献与贡献币(在通关结算挂钩调用, 尽力而为; 2026-09-07 晚适级定案):
        /// 适级通关 +10 贡献(<see cref="GameWorld.Dungeon.IsSuitableLevelDungeon"/>,
        /// ±5 级段重叠或异次元豁免); 不适级也保存 0 分收据, 防事后入会重放刷分;
        /// 适级 + 同公会成员组队通关 +1 贡献币(按通关事件幂等);
        /// 公会地下城每日第 5 次通关 +10 贡献币(不要求适级, 参考包 counted 不门控 Suitable)。</summary>
        public static void RecordClearContribution(
            int characterId, int accountId, string characterName,
            Guid clearEventId, bool sameGuildParty, int dungeonId = 0, int characterLevel = 0)
        {
            if (characterId <= 0 || clearEventId == Guid.Empty)
                return;
            var guild = GuildSystem.GetGuildOfCharacter(characterId);
            if (guild == null)
                return;
            // 适级判定(参考包规范: 复用 Dungeon.IsSuitableLevelDungeon 与规范结算链)
            bool suitable;
            try
            {
                suitable = dungeonId > 0
                    && GameWorld.Dungeon.IsSuitableLevelDungeon(dungeonId, characterLevel);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GuildContribution] 适级判定失败 dungeon={dungeonId}: {ex.Message}");
                suitable = false;
            }
            var points = suitable ? ProperClearPoints : 0;
            var month = CurrentMonth(DateTime.UtcNow);
            try
            {
                EnsurePeriod(guild.GuildId, month);
                // 不适级也保存 0 分收据(占用 source_key 幂等位, 防换会/入会重放)
                var inserted = GuildSystem.Repository.TryInsertContributionEvent(
                    characterId, guild.GuildId,
                    "clear:" + clearEventId.ToString("N"),
                    SourceClear, points, month.Key, characterName,
                    null, null);
                if (inserted)
                {
                    if (points > 0)
                        GuildSystem.Repository.AccumulateContribution(
                            guild.GuildId, month.Key, characterId, points);
                    // 同公会成员适级组队通关 +1 币(参考包: eligible && Suitable && SameGuildParty)
                    if (suitable && sameGuildParty)
                        AwardClearCoin(characterId, accountId, clearEventId);
                    // 公会地下城每日计数: 第 5 次 +10 币(参考包 GuildCoinRules
                    // DungeonTarget=5/DungeonReward=10; counted 不门控 Suitable)。
                    if (GuildDungeonCatalog.IsGuildDungeon(dungeonId))
                    {
                        var day = GuildActivityService.TodayString();
                        var nth = GuildSystem.Repository
                            .IncrementGuildDungeonClearCount(characterId, day);
                        if (nth == GuildDungeonDailyTarget)
                            AwardGuildDungeonCoin(characterId, accountId, day);
                    }
                }
                SettleOverduePeriods();
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GuildContribution] 通关贡献失败 cid={characterId}: {ex.Message}");
            }
        }

        /// <summary>公会地下城第 5 次通关奖励 +10 币(凭据 gdc5:day 幂等, 发放失败回滚可重试)。</summary>
        private static void AwardGuildDungeonCoin(int characterId, int accountId, string dayKey)
        {
            var key = "gdc5:" + dayKey;
            try
            {
                if (!GuildSystem.Repository.TryClaimCoinOnce(
                        characterId, key, 3, GuildDungeonRewardCoins))
                    return;
                if (Game.Inventory.GmStyleItemGrant.TryGrant(
                        GuildSystem.Repository.ConnectionString,
                        characterId, accountId,
                        GuildActivityService.CoinItemId, GuildDungeonRewardCoins))
                {
                    FileLogger.Log(
                        $"[GuildCoin] cid={characterId} source=guild-dungeon-5th +{GuildDungeonRewardCoins}");
                    return;
                }
                GuildSystem.Repository.DeleteCoinClaim(characterId, key);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GuildCoin] 公会地下城币失败 cid={characterId}: {ex.Message}");
            }
        }

        private static void AwardClearCoin(int characterId, int accountId, Guid clearEventId)
        {
            var key = "clear:" + clearEventId.ToString("N");
            try
            {
                if (!GuildSystem.Repository.TryClaimCoinOnce(characterId, key, 3, 1))
                    return;
                if (Game.Inventory.GmStyleItemGrant.TryGrant(
                        GuildSystem.Repository.ConnectionString,
                        characterId, accountId, GuildActivityService.CoinItemId, 1))
                {
                    FileLogger.Log($"[GuildCoin] cid={characterId} source=clear +1");
                    return;
                }
                GuildSystem.Repository.DeleteCoinClaim(characterId, key);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GuildCoin] 通关币失败 cid={characterId}: {ex.Message}");
            }
        }

        private static void EnsurePeriod(
            int guildId, (int Key, DateTime StartUtc, DateTime EndUtc) month)
        {
            GuildSystem.Repository.EnsureContributionPeriod(
                guildId, month.Key,
                month.StartUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                month.EndUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                MemberGoal, MemberRewardItem, RewardItemCount,
                GuildGoal, GuildRewardItem, RewardItemCount);
        }

        /// <summary>补结全部到期月份(惰性: 贡献写入/选角时调用, 内部异常自吞)。</summary>
        public static void SettleOverduePeriods()
        {
            try
            {
                var nowUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
                var overdue = GuildSystem.Repository.GetOverdueContributionPeriods(nowUtc);
                foreach (var p in overdue)
                    SettlePeriod(p);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GuildContribution] 月结扫描失败: {ex.Message}");
            }
        }

        private static void SettlePeriod(
            (int GuildId, int MonthKey, string EndsUtc, long Points,
             int MemberGoal, int MemberItem, int MemberCount,
             int GuildGoal, int GuildItem, int GuildCount) p)
        {
            var guild = GuildSystem.GetGuildById(p.GuildId);
            if (guild == null)
            {
                GuildSystem.Repository.MarkContributionPeriodSettled(p.GuildId, p.MonthKey);
                return;
            }
            // 发件人 = 月结时会长(仅身份, 不扣资产)
            var masterCid = 0;
            var masterAccount = 0;
            var masterName = guild.MasterName ?? string.Empty;
            foreach (var m in guild.Members)
            {
                if (m.Grade == GuildSystem.GradeMaster)
                {
                    masterCid = m.CharacterId;
                    break;
                }
            }
            if (masterCid > 0)
            {
                var info = GuildSystem.Repository.GetCharacterAccountInfo(masterCid);
                masterAccount = info.AccountId;
            }

            var currentMembers = new HashSet<int>();
            foreach (var m in guild.Members)
                currentMembers.Add(m.CharacterId);

            // 1) 个人月奖: 达标且仍在会
            var qualified = GuildSystem.Repository.GetQualifiedContributionMembers(
                p.GuildId, p.MonthKey, p.MemberGoal);
            foreach (var cid in qualified)
            {
                if (!currentMembers.Contains(cid))
                    continue;
                EntitleAndMail(p.GuildId, p.MonthKey, 1, cid,
                    p.MemberItem, p.MemberCount,
                    masterCid, masterAccount, masterName,
                    "公会个人贡献奖励", "你上月的公会个人贡献已达标, 请查收奖励。");
            }

            // 2) 公会月奖: 月总达标 → 全体仍在会成员(含零贡献)
            if (p.Points >= p.GuildGoal)
            {
                foreach (var cid in currentMembers)
                {
                    EntitleAndMail(p.GuildId, p.MonthKey, 2, cid,
                        p.GuildItem, p.GuildCount,
                        masterCid, masterAccount, masterName,
                        "公会贡献奖励", "你所在的公会上月总贡献已达标, 请查收奖励。");
                }
            }

            GuildSystem.Repository.MarkContributionPeriodSettled(p.GuildId, p.MonthKey);
            FileLogger.Log(
                $"[GuildContribution] 月结 gid={p.GuildId} month={p.MonthKey} " +
                $"points={p.Points} qualified={qualified.Count} members={currentMembers.Count}");
        }

        private static void EntitleAndMail(
            int guildId, int monthKey, int kind, int characterId,
            int itemId, int itemCount,
            int masterCid, int masterAccount, string masterName,
            string title, string text)
        {
            try
            {
                var receiver = GuildSystem.Repository.GetCharacterAccountInfo(characterId);
                if (receiver.AccountId <= 0)
                    return;
                var rewardId = GuildSystem.Repository.EntitleContributionReward(
                    guildId, monthKey, kind, characterId, receiver.AccountId,
                    itemId, itemCount, masterCid,
                    DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
                if (rewardId <= 0)
                    return;   // 已登记过(幂等)
                var send = Mailbox.SendSystemMail(new MailboxSendRequest
                {
                    SenderCharacterId = masterCid,
                    SenderAccountId = masterAccount,
                    SenderName = masterName,
                    ReceiverCharacterId = characterId,
                    ReceiverAccountId = receiver.AccountId,
                    ReceiverName = receiver.Name,
                    Gold = 0,
                    Title = title,
                    Text = text,
                    MailType = 1,
                    SourceProtocol = 0,
                    IdempotencyKey = $"guild-contrib:{guildId}:{monthKey}:{kind}:{characterId}",
                    AuditActor = "guild-contribution",
                    AuditReason = $"month close {monthKey} kind={kind}",
                    Attachments = new List<MailboxSendAttachmentRequest>
                    {
                        new MailboxSendAttachmentRequest
                        {
                            ItemId = itemId,
                            ItemCount = itemCount,
                        },
                    },
                });
                if (send.Success)
                    GuildSystem.Repository.MarkContributionRewardMailed(rewardId);
                else
                    FileLogger.Log(
                        $"[GuildContribution] 月奖邮件发送失败 reward={rewardId} cid={characterId}: {send.Error}");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[GuildContribution] 月奖发放异常 gid={guildId} cid={characterId}: {ex.Message}");
            }
        }
    }
}
