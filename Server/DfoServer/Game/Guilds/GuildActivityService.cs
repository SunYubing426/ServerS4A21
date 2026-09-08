using DfoServer.Game.DailyReset;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using System;

namespace DfoServer.Game.Guilds
{
    /// <summary>
    /// 公会签到与公会贡献币(490003262)业务服务(2026-09-07 对齐
    /// A21-公会修复源码包 GuildActivityApplicationService / GuildCoinRules):
    /// - 签到: 角色/公会/游戏日唯一持久(日界=北京 06:00, DailyResetService.TodayId),
    ///   不因重登/换频道重复; 周活跃 = 本周(北京周一 06:00 起)签到过的当前在会角色去重数。
    /// - 贡献币: 每日首次有效登录 3 个、每日首次公会发言 2 个;
    ///   领取凭据(guild_coin_claims)与发放分离, 发放失败回滚凭据可重试。
    /// </summary>
    public static class GuildActivityService
    {
        public const int CoinItemId = 490003262;
        public const int LoginCoins = 3;
        public const int ChatCoins = 2;

        private const int CoinSourceLogin = 1;
        private const int CoinSourceChat = 2;

        /// <summary>今日游戏日字符串(YYYY-MM-DD, 北京 06:00 日界; 签到明细/计数用)。</summary>
        public static string TodayString()
        {
            var id = DailyResetService.TodayId();
            return $"{id / 10000:D4}-{id / 100 % 100:D2}-{id % 100:D2}";
        }

        private static string WeekStartString()
        {
            var id = DailyResetService.TodayId();
            var day = new DateTime(id / 10000, id / 100 % 100, id % 100);
            return day.AddDays(-(((int)day.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd");
        }

        /// <summary>持久签到(幂等)。返回是否首次签到; 不在会返回 false。</summary>
        public static bool RecordAttendance(int characterId)
        {
            var guild = GuildSystem.GetGuildOfCharacter(characterId);
            if (guild == null)
                return false;
            try
            {
                var first = GuildSystem.Repository.RecordAttendance(
                    guild.GuildId, characterId, TodayString());
                if (first)
                {
                    FileLogger.Log(
                        $"[Guild] 签到 guild={guild.GuildId} cid={characterId} day={TodayString()}");
                }
                return first;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Guild] 签到失败 cid={characterId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>今日已签到人数(0x04EC 签到分子)。</summary>
        public static int GetTodayAttendanceCount(int guildId)
        {
            try
            {
                return GuildSystem.Repository.CountAttendanceOn(guildId, TodayString());
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Guild] 签到计数失败 guild={guildId}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>周活跃(本周签到过的在会角色去重数; 搜索/推荐快照用)。</summary>
        public static int GetWeeklyActiveCount(int guildId)
        {
            try
            {
                return GuildSystem.Repository.CountWeeklyActive(
                    guildId, WeekStartString(), TodayString());
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Guild] 周活跃计数失败 guild={guildId}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>每日首次有效登录 3 个贡献币(参考包 GuildCoinRules.Login=3)。</summary>
        public static int AwardDailyLoginCoins(int characterId, int accountId)
            => AwardCoins(characterId, accountId, "login", CoinSourceLogin, LoginCoins);

        /// <summary>每日首次公会发言 2 个贡献币(参考包 GuildCoinRules.Chat=2)。</summary>
        public static int AwardDailyChatCoins(int characterId, int accountId)
            => AwardCoins(characterId, accountId, "chat", CoinSourceChat, ChatCoins);

        private static int AwardCoins(
            int characterId, int accountId, string sourceName, int source, int coins)
        {
            if (characterId <= 0 || accountId <= 0 || coins <= 0)
                return 0;
            if (GuildSystem.GetGuildOfCharacter(characterId) == null)
                return 0;
            var key = $"{sourceName}:{DailyResetService.TodayId()}";
            try
            {
                if (!GuildSystem.Repository.TryClaimCoinOnce(characterId, key, source, coins))
                    return 0;   // 今日已领
                if (GmStyleItemGrant.TryGrant(
                        GuildSystem.Repository.ConnectionString,
                        characterId, accountId, CoinItemId, coins))
                {
                    FileLogger.Log(
                        $"[GuildCoin] cid={characterId} source={sourceName} +{coins} (item {CoinItemId})");
                    return coins;
                }
                // 发放失败(背包满/未加载): 回滚凭据, 整理背包/重登后可再领
                GuildSystem.Repository.DeleteCoinClaim(characterId, key);
                FileLogger.Log(
                    $"[GuildCoin] cid={characterId} source={sourceName} 发放失败, 凭据已回滚");
                return 0;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[GuildCoin] cid={characterId} source={sourceName} 异常: {ex.Message}");
                return 0;
            }
        }
    }
}
