using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Text;
using DfoServer.Game.Currency;

namespace DfoServer.Game.Guilds
{
    /// <summary>
    /// 工会表(guilds / guild_members)数据访问。
    ///
    /// 建表: SqliteMigrations v16(旧库) — 与 united_friend_relations 同模式。
    /// 本仓储只做表 CRUD, 不运行时建表(AGENTS.md 硬性约定)。
    /// 内存图(GuildSystem)为运行期权威, 启动全量载入, 写操作同步落表。
    /// </summary>
    public sealed class GuildRepository
    {
        private readonly string _connectionString;

        public GuildRepository(string databasePath, string schemaFilePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("databasePath is empty", nameof(databasePath));
            if (string.IsNullOrWhiteSpace(schemaFilePath))
                throw new ArgumentException("schemaFilePath is empty", nameof(schemaFilePath));

            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
        }

        private SqliteConnection Open()
        {
            var conn = new SqliteConnection(_connectionString);
            conn.Open();
            return conn;
        }

        /// <summary>数据库连接串(GmStyleItemGrant 等独立连接发放通道用)。</summary>
        internal string ConnectionString => _connectionString;

        // ── 公会签到(guild_member_attendance, 迁移 v25) ──

        /// <summary>持久签到: 角色/公会/游戏日唯一, 重复调用幂等。返回是否首次签到。</summary>
        public bool RecordAttendance(int guildId, int characterId, string attendanceDate)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT OR IGNORE INTO guild_member_attendance
    (guild_id, character_id, attendance_date, attended_at)
VALUES (@gid, @cid, @day, CURRENT_TIMESTAMP);";
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@day", attendanceDate);
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        /// <summary>某日已签到人数(去重)。</summary>
        public int CountAttendanceOn(int guildId, string attendanceDate)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT COUNT(DISTINCT character_id) FROM guild_member_attendance
 WHERE guild_id = @gid AND attendance_date = @day;";
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@day", attendanceDate);
                return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            }
        }

        /// <summary>周活跃: [weekStart, today] 内签到过的在会角色去重数(参考包权威:
        /// 不用在线人数冒充; 只统计当前仍在会的成员)。</summary>
        public int CountWeeklyActive(int guildId, string weekStartDate, string todayDate)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT COUNT(DISTINCT a.character_id) FROM guild_member_attendance a
 JOIN guild_members m ON m.guild_id = a.guild_id AND m.character_id = a.character_id
 WHERE a.guild_id = @gid AND a.attendance_date BETWEEN @start AND @day;";
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@start", weekStartDate);
                cmd.Parameters.AddWithValue("@day", todayDate);
                return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            }
        }

        // ── 公会贡献币凭据(guild_coin_claims, 迁移 v25) ──

        /// <summary>原子领取贡献币凭据: 同 (cid, key) 只成功一次。返回是否首次。</summary>
        public bool TryClaimCoinOnce(int characterId, string claimKey, int source, int coins)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT OR IGNORE INTO guild_coin_claims
    (character_id, claim_key, source, coins, claimed_at)
VALUES (@cid, @key, @src, @coins, CURRENT_TIMESTAMP);";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@key", claimKey);
                cmd.Parameters.AddWithValue("@src", source);
                cmd.Parameters.AddWithValue("@coins", coins);
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        /// <summary>发放失败时回滚凭据(玩家整理背包后可重试)。</summary>
        public void DeleteCoinClaim(int characterId, string claimKey)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "DELETE FROM guild_coin_claims WHERE character_id = @cid AND claim_key = @key;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@key", claimKey);
                cmd.ExecuteNonQuery();
            }
        }

        // ── 公会地下城每日通关计数(guild_dungeon_clear_counts, 迁移 v32) ──

        /// <summary>
        /// 原子累加今日公会地下城通关次数并返回累加后的值(事务内 upsert+读回)。
        /// 达到每日目标(5)时由调用方发 +10 贡献币。
        /// </summary>
        public int IncrementGuildDungeonClearCount(int characterId, string dayKey)
        {
            using (var conn = Open())
            using (var tx = conn.BeginTransaction())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
INSERT INTO guild_dungeon_clear_counts (character_id, day_key, clear_count, updated_at)
VALUES (@cid, @day, 1, CURRENT_TIMESTAMP)
ON CONFLICT (character_id, day_key)
DO UPDATE SET clear_count = clear_count + 1, updated_at = CURRENT_TIMESTAMP;";
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@day", dayKey);
                    cmd.ExecuteNonQuery();
                }
                int count;
                using (var read = conn.CreateCommand())
                {
                    read.Transaction = tx;
                    read.CommandText =
                        "SELECT clear_count FROM guild_dungeon_clear_counts " +
                        "WHERE character_id = @cid AND day_key = @day;";
                    read.Parameters.AddWithValue("@cid", characterId);
                    read.Parameters.AddWithValue("@day", dayKey);
                    count = Convert.ToInt32(read.ExecuteScalar() ?? 0);
                }
                tx.Commit();
                return count;
            }
        }

        // ── 公会贡献(迁移 v26, 2026-09-07 参考包权威规则适配) ──

        /// <summary>记录贡献事件(source_key 幂等去重)。返回 true=首次记录;
        /// 已存在同 (cid, sourceKey) 事件时返回 false(不重复加分)。</summary>
        public bool TryInsertContributionEvent(
            int characterId, int guildId, string sourceKey, int sourceType,
            int points, int monthKey, string characterName,
            int? donationGold, int? donationMileage)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT OR IGNORE INTO guild_contribution_events
    (source_key, character_id, guild_id, month_key, source_type, points,
     occurred_at, character_name, donation_gold, donation_mileage)
VALUES (@key, @cid, @gid, @month, @src, @points,
     CURRENT_TIMESTAMP, @name, @dgold, @dmile);";
                cmd.Parameters.AddWithValue("@key", sourceKey);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@month", monthKey);
                cmd.Parameters.AddWithValue("@src", sourceType);
                cmd.Parameters.AddWithValue("@points", points);
                cmd.Parameters.AddWithValue("@name", characterName ?? string.Empty);
                cmd.Parameters.AddWithValue("@dgold", (object)donationGold ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@dmile", (object)donationMileage ?? DBNull.Value);
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        /// <summary>确保月期行存在(目标/奖励快照取自参数)。</summary>
        public void EnsureContributionPeriod(
            int guildId, int monthKey, string startsUtc, string endsUtc,
            int memberGoal, int memberItem, int memberCount,
            int guildGoal, int guildItem, int guildCount)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT OR IGNORE INTO guild_contribution_periods
    (guild_id, month_key, starts_at, ends_at, member_goal, member_item,
     member_count, guild_goal, guild_item, guild_count, points)
VALUES (@gid, @month, @start, @end, @mgoal, @mitem, @mcount, @ggoal, @gitem, @gcount, 0);";
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@month", monthKey);
                cmd.Parameters.AddWithValue("@start", startsUtc);
                cmd.Parameters.AddWithValue("@end", endsUtc);
                cmd.Parameters.AddWithValue("@mgoal", memberGoal);
                cmd.Parameters.AddWithValue("@mitem", memberItem);
                cmd.Parameters.AddWithValue("@mcount", memberCount);
                cmd.Parameters.AddWithValue("@ggoal", guildGoal);
                cmd.Parameters.AddWithValue("@gitem", guildItem);
                cmd.Parameters.AddWithValue("@gcount", guildCount);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>累加贡献点(成员月账 + 公会月期; 月期已结则跳过累计并记日志)。</summary>
        public void AccumulateContribution(int guildId, int monthKey, int characterId, int points)
        {
            if (points <= 0)
                return;
            using (var conn = Open())
            using (var tx = conn.BeginTransaction())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
INSERT INTO guild_member_contributions (guild_id, month_key, character_id, points)
VALUES (@gid, @month, @cid, @points)
ON CONFLICT(guild_id, month_key, character_id)
DO UPDATE SET points = points + @points;";
                    cmd.Parameters.AddWithValue("@gid", guildId);
                    cmd.Parameters.AddWithValue("@month", monthKey);
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@points", points);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
UPDATE guild_contribution_periods SET points = points + @points
 WHERE guild_id = @gid AND month_key = @month AND settled_at IS NULL;";
                    cmd.Parameters.AddWithValue("@gid", guildId);
                    cmd.Parameters.AddWithValue("@month", monthKey);
                    cmd.Parameters.AddWithValue("@points", points);
                    if (cmd.ExecuteNonQuery() == 0)
                    {
                        FileLogger.Log(
                            $"[GuildContribution] 月期已结, 跳过累计 gid={guildId} month={monthKey} cid={characterId}");
                    }
                }
                tx.Commit();
            }
        }

        /// <summary>贡献明细(最近 50 条 points>0, 新→旧)。</summary>
        public List<(string OccurredUtc, string Name, int Points, int SourceType)>
            GetContributionHistory(int guildId, int limit = 50)
        {
            var rows = new List<(string, string, int, int)>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT occurred_at, character_name, points, source_type
  FROM guild_contribution_events
 WHERE guild_id = @gid AND points > 0
 ORDER BY event_id DESC LIMIT @limit;";
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@limit", limit);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        rows.Add((
                            reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                            reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                            reader.GetInt32(2),
                            reader.GetInt32(3)));
                    }
                }
            }
            return rows;
        }

        /// <summary>捐献明细(source_type=3 最近 50 条, 新→旧)。</summary>
        public List<(string OccurredUtc, string Name, int Gold, int Mileage)>
            GetDonationHistory(int guildId, int limit = 50)
        {
            var rows = new List<(string, string, int, int)>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT occurred_at, character_name, donation_gold, donation_mileage
  FROM guild_contribution_events
 WHERE guild_id = @gid AND source_type = 3
   AND donation_gold IS NOT NULL AND donation_mileage IS NOT NULL
 ORDER BY event_id DESC LIMIT @limit;";
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@limit", limit);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        rows.Add((
                            reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                            reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                            reader.GetInt32(2),
                            reader.GetInt32(3)));
                    }
                }
            }
            return rows;
        }

        /// <summary>今日已签到成员名(0x02E9 签到明细; 按成员表顺序)。</summary>
        public List<string> GetTodayAttendeeNames(int guildId, string attendanceDate)
        {
            var names = new List<string>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT m.character_name FROM guild_member_attendance a
 JOIN guild_members m ON m.guild_id = a.guild_id AND m.character_id = a.character_id
 WHERE a.guild_id = @gid AND a.attendance_date = @day
 ORDER BY m.character_id;";
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@day", attendanceDate);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        names.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
                }
            }
            return names;
        }

        /// <summary>取已过月结时间且未结算的月期(月结扫描用)。</summary>
        public List<(int GuildId, int MonthKey, string EndsUtc, long Points,
            int MemberGoal, int MemberItem, int MemberCount,
            int GuildGoal, int GuildItem, int GuildCount)>
            GetOverdueContributionPeriods(string nowUtc)
        {
            var rows = new List<(int, int, string, long, int, int, int, int, int, int)>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT guild_id, month_key, ends_at, points, member_goal, member_item,
       member_count, guild_goal, guild_item, guild_count
  FROM guild_contribution_periods
 WHERE settled_at IS NULL AND ends_at <= @now
 ORDER BY guild_id, month_key;";
                cmd.Parameters.AddWithValue("@now", nowUtc);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        rows.Add((
                            reader.GetInt32(0), reader.GetInt32(1),
                            reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                            reader.GetInt64(3),
                            reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6),
                            reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9)));
                    }
                }
            }
            return rows;
        }

        /// <summary>取某月达标成员(个人月奖资格)。</summary>
        public List<int> GetQualifiedContributionMembers(int guildId, int monthKey, int goal)
        {
            var cids = new List<int>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT character_id FROM guild_member_contributions
 WHERE guild_id = @gid AND month_key = @month AND points >= @goal;";
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@month", monthKey);
                cmd.Parameters.AddWithValue("@goal", goal);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        cids.Add(reader.GetInt32(0));
                }
            }
            return cids;
        }

        /// <summary>登记月奖资格(UNIQUE 幂等)。返回 true=新登记(应发邮件)。</summary>
        public long EntitleContributionReward(
            int guildId, int monthKey, int kind, int characterId, int accountId,
            int itemId, int itemCount, int senderCharacterId, string entitledUtc)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT OR IGNORE INTO guild_contribution_rewards
    (guild_id, month_key, reward_kind, character_id, account_id,
     item_id, item_count, sender_character_id, entitled_at)
VALUES (@gid, @month, @kind, @cid, @aid, @item, @count, @sender, @at);";
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@month", monthKey);
                cmd.Parameters.AddWithValue("@kind", kind);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@aid", accountId);
                cmd.Parameters.AddWithValue("@item", itemId);
                cmd.Parameters.AddWithValue("@count", itemCount);
                cmd.Parameters.AddWithValue("@sender", senderCharacterId);
                cmd.Parameters.AddWithValue("@at", entitledUtc);
                var inserted = cmd.ExecuteNonQuery() > 0;
                if (!inserted)
                    return 0;
                using (var idCmd = conn.CreateCommand())
                {
                    idCmd.CommandText = "SELECT last_insert_rowid();";
                    return Convert.ToInt64(idCmd.ExecuteScalar() ?? 0);
                }
            }
        }

        /// <summary>标记月奖邮件已发。</summary>
        public void MarkContributionRewardMailed(long rewardId)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "UPDATE guild_contribution_rewards SET mailed_at = CURRENT_TIMESTAMP WHERE reward_id = @id;";
                cmd.Parameters.AddWithValue("@id", rewardId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>月期标记已结算。</summary>
        public void MarkContributionPeriodSettled(int guildId, int monthKey)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
UPDATE guild_contribution_periods SET settled_at = CURRENT_TIMESTAMP
 WHERE guild_id = @gid AND month_key = @month AND settled_at IS NULL;";
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@month", monthKey);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>取角色的账号 ID 与名字(月奖邮件收/发件人解析用)。</summary>
        public (int AccountId, string Name) GetCharacterAccountInfo(int characterId)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT account_id, name FROM characters WHERE character_id = @cid;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return (reader.GetInt32(0),
                            reader.IsDBNull(1) ? string.Empty : reader.GetString(1));
                    }
                }
            }
            return (0, string.Empty);
        }

        // ── 公会图标/改名/契约(迁移 v27, 2026-09-07 参考包权威规则适配) ──

        /// <summary>更新公会名(改名; 调用方已完成重名/权限/扣款)。</summary>
        public void UpdateGuildName(int guildId, string newName)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE guilds SET name = @name WHERE guild_id = @gid;";
                cmd.Parameters.AddWithValue("@name", newName);
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>更换公会图标: 事务性扣公会 GM(cost=0 跳过) + 写 emblem_id。
        /// 返回 true 且 newGold=扣后公会 GM; GM 不足返回 false。</summary>
        public bool ChangeEmblem(int guildId, int emblemId, int cost, out int newGold)
        {
            newGold = 0;
            using (var conn = Open())
            using (var tx = conn.BeginTransaction())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT gold FROM guilds WHERE guild_id = @gid;";
                    cmd.Parameters.AddWithValue("@gid", guildId);
                    var value = cmd.ExecuteScalar();
                    if (value == null || value == DBNull.Value)
                        return false;
                    var gold = Convert.ToInt32(value);
                    if (gold < cost)
                        return false;
                    newGold = gold - cost;
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "UPDATE guilds SET gold = @gold, emblem_id = @emblem WHERE guild_id = @gid;";
                    cmd.Parameters.AddWithValue("@gold", newGold);
                    cmd.Parameters.AddWithValue("@emblem", emblemId);
                    cmd.Parameters.AddWithValue("@gid", guildId);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
                return true;
            }
        }

        /// <summary>购买契约(type21): upsert guild_contents 行并写累计到期。
        /// expiryFrom = max(现有 expires_at, now); 调用方已算好 newExpiry 与上限。</summary>
        public void UpsertGuildContract(
            int guildId, int contentId, int capacity, string purchasedUtc, string newExpiryUtc)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO guild_contents (guild_id, content_id, status, level, exp, expires_at)
VALUES (@gid, @id, 1, @capacity, 0, @expiry)
ON CONFLICT(guild_id, content_id)
DO UPDATE SET status = 1, level = @capacity, expires_at = @expiry;";
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@id", contentId);
                cmd.Parameters.AddWithValue("@capacity", capacity);
                cmd.Parameters.AddWithValue("@expiry", newExpiryUtc);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>取公会当前契约(type21)到期文本; 无契约返回 null。</summary>
        public string GetActiveContractExpiry(int guildId)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT MAX(expires_at) FROM guild_contents
 WHERE guild_id = @gid AND content_id IN (23, 24) AND expires_at IS NOT NULL;";
                cmd.Parameters.AddWithValue("@gid", guildId);
                var value = cmd.ExecuteScalar();
                return value == null || value == DBNull.Value ? null : Convert.ToString(value);
            }
        }

        /// <summary>契约每日领取: 今日未领则登记并返回 true(角色×游戏日唯一)。</summary>
        public bool TryClaimGuildContract(int characterId, int guildId, string claimDate, int itemId)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT OR IGNORE INTO guild_contract_claims (character_id, claim_date, guild_id, item_id)
VALUES (@cid, @day, @gid, @item);";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@day", claimDate);
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@item", itemId);
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        /// <summary>发放失败回滚领取资格(背包满等; 玩家整理后可重试)。</summary>
        public void DeleteGuildContractClaim(int characterId, string claimDate)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "DELETE FROM guild_contract_claims WHERE character_id = @cid AND claim_date = @day;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@day", claimDate);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>贡献合计(0x0046 尾部四值): 公会累计/公会当月/个人累计/个人当月。
        /// 个人 = 该角色在【本公会】的逐月贡献(跨会不带入, 与月奖口径一致)。</summary>
        public (long GuildLifetime, long GuildMonth, long PersonalLifetime, long PersonalMonth)
            GetContributionTotals(int guildId, int characterId, int currentMonthKey)
        {
            using (var conn = Open())
            {
                long guildLife = 0, guildMonth = 0, personalLife = 0, personalMonth = 0;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT COALESCE(SUM(points),0),
       COALESCE(SUM(CASE WHEN month_key = @month THEN points ELSE 0 END),0)
  FROM guild_contribution_periods WHERE guild_id = @gid;";
                    cmd.Parameters.AddWithValue("@month", currentMonthKey);
                    cmd.Parameters.AddWithValue("@gid", guildId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            guildLife = reader.GetInt64(0);
                            guildMonth = reader.GetInt64(1);
                        }
                    }
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT COALESCE(SUM(points),0),
       COALESCE(SUM(CASE WHEN month_key = @month THEN points ELSE 0 END),0)
  FROM guild_member_contributions WHERE guild_id = @gid AND character_id = @cid;";
                    cmd.Parameters.AddWithValue("@month", currentMonthKey);
                    cmd.Parameters.AddWithValue("@gid", guildId);
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            personalLife = reader.GetInt64(0);
                            personalMonth = reader.GetInt64(1);
                        }
                    }
                }
                return (guildLife, guildMonth, personalLife, personalMonth);
            }
        }

        /// <summary>按角色名查 cid(认证用)。不存在/重名冲突时返回 -1。</summary>
        public int FindCharacterIdByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return -1;
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                // characters.name 历史上有三种存储形态: TEXT / UTF-8 BLOB(v11 迁移前的
                // 建号路径) / GBK BLOB(v11 ConvertCharacterNames)。SQLite 跨存储类
                // (TEXT vs BLOB)比较永假 → 必须 TEXT+UTF8+GBK 三重匹配, 否则
                // 认证/查名一律"角色不存在"(2026-08-31 实测: 哈哈哈/麦哲伦均被拒)。
                cmd.CommandText =
                    "SELECT character_id FROM characters "
                    + "WHERE (name = @n OR name = @utf8 OR name = @gbk) AND delete_flag = 0 LIMIT 2;";
                cmd.Parameters.AddWithValue("@n", name);
                cmd.Parameters.AddWithValue("@utf8", Encoding.UTF8.GetBytes(name));
                cmd.Parameters.AddWithValue("@gbk", ClientTextEncoding.GetBytes(name));
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read())
                        return -1;
                    var cid = Convert.ToInt32(reader["character_id"]);
                    // 重名视为无效(创建认证要求唯一角色)
                    return reader.Read() ? -1 : cid;
                }
            }
        }

        /// <summary>
        /// 查同账号(account_id)下所有角色 cid(含自己, 0x02E9 成员列表"+"展开用)。
        /// 查不到账号时返回仅含自己的列表。
        /// </summary>
        public List<int> FindAccountCharacterIds(int characterId)
        {
            var result = new List<int>();
            if (characterId <= 0)
                return result;
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT character_id FROM characters "
                    + "WHERE account_id = (SELECT account_id FROM characters WHERE character_id=@cid) "
                    + "AND delete_flag = 0;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        result.Add(reader.GetInt32(0));
                }
            }
            if (result.Count == 0)
                result.Add(characterId);
            return result;
        }

        /// <summary>按 cid 查 job(0x0043 成员列表职业列渲染用)。查不到返回 0。</summary>
        public int FindJobById(int characterId)
        {
            if (characterId <= 0)
                return 0;
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT job FROM characters WHERE character_id=@cid AND delete_flag=0;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                var value = cmd.ExecuteScalar();
                return value == null || value is DBNull
                    ? 0
                    : Convert.ToInt32(value);
            }
        }

        /// <summary>
        /// 按 cid 查 grow_type(0x0043 成员行转职字节)。DB 存储格式即客户端
        /// 打包格式 (secondGrow&lt;&lt;4)|firstGrow(见 GrowupChangeSelfTest 0x11/0x12/0x23)。
        /// 查不到返回 0。
        /// </summary>
        public int FindGrowTypeById(int characterId)
        {
            if (characterId <= 0)
                return 0;
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT grow_type FROM characters WHERE character_id=@cid AND delete_flag=0;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                var value = cmd.ExecuteScalar();
                return value == null || value is DBNull
                    ? 0
                    : Convert.ToInt32(value);
            }
        }

        /// <summary>
        /// 按 cid 查 account_id(0x0043/0x008C 成员条目第 2 个 u32 = 客户端按账号
        /// 分组的 key, handler 0x1130B60 存 record+0x15C → 0x19F0AC0 作哈希键)。
        /// 查不到返回 0。
        /// </summary>
        public int FindAccountIdByCharacterId(int characterId)
        {
            if (characterId <= 0)
                return 0;
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT account_id FROM characters WHERE character_id=@cid AND delete_flag=0;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                var value = cmd.ExecuteScalar();
                return value == null || value is DBNull
                    ? 0
                    : Convert.ToInt32(value);
            }
        }

        /// <summary>
        /// 按 cid 查 town_id(0x0043/0x008C 成员行 p3 → record+0x34, 与 +0x38 频道号
        /// 相邻构成"地区+频道"位置, 客户端悬浮"chXX 地名"的地名来源; 0 → "未知")。
        /// 查不到返回 0。
        /// </summary>
        public int FindTownIdById(int characterId)
        {
            if (characterId <= 0)
                return 0;
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT town_id FROM characters WHERE character_id=@cid AND delete_flag=0;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                var value = cmd.ExecuteScalar();
                return value == null || value is DBNull
                    ? 0
                    : Convert.ToInt32(value);
            }
        }

        /// <summary>按 cid 查 level(0x0160 申请列表等级列渲染用)。查不到返回 0。</summary>
        public int FindLevelById(int characterId)
        {
            if (characterId <= 0)
                return 0;
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT level FROM characters WHERE character_id=@cid AND delete_flag=0;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                var value = cmd.ExecuteScalar();
                return value == null || value is DBNull
                    ? 0
                    : Convert.ToInt32(value);
            }
        }

        /// <summary>
        /// 全量读取工会(不含成员) → 按 guild_id 升序。启动时载入内存。
        /// level/exp/member_limit 来自迁移 v22(老库缺列时回退 1/0/30)。
        /// </summary>
        public List<(int GuildId, string Name, string Memo, string MasterName, int Gold,
                     int Level, int Exp, int MemberLimit, string Announcement, int EmblemId,
                     int PublicFlag)> LoadGuilds()
        {
            var result = new List<(int, string, string, string, int, int, int, int, string, int, int)>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT guild_id, name, memo, master_name, gold, "
                    + "level, exp, member_limit, announcement, emblem_id, public_flag FROM guilds ORDER BY guild_id;";
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var limit = reader.IsDBNull(7) ? 30 : reader.GetInt32(7);
                        result.Add((
                            reader.GetInt32(0),
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.GetString(3),
                            reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                            reader.IsDBNull(5) ? 1 : reader.GetInt32(5),
                            reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                            limit <= 0 ? 30 : limit,
                            reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                            reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
                            reader.IsDBNull(10) ? 0 : reader.GetInt32(10)));
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 更新工会档案(等级/经验/成员上限, 迁移 v22)。三值一并写入, 内存已改则整行覆盖。
        /// </summary>
        public void UpdateGuildProfile(int guildId, int level, int exp, int memberLimit)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
UPDATE guilds SET level=@level, exp=@exp, member_limit=@limit
WHERE guild_id=@gid;";
                cmd.Parameters.AddWithValue("@level", level);
                cmd.Parameters.AddWithValue("@exp", exp);
                cmd.Parameters.AddWithValue("@limit", memberLimit);
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 更新工会公告(v24)。文本可为空串(=显式清空公告)。
        /// </summary>
        public void UpdateGuildAnnouncement(int guildId, string announcement)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "UPDATE guilds SET announcement=@ann WHERE guild_id=@gid;";
                cmd.Parameters.AddWithValue("@ann", announcement ?? string.Empty);
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 写工会操作日志(迁移 v23)。审计用, 失败不得影响主流程(调用方已 try/catch)。
        /// </summary>
        public void InsertGuildLog(
            int guildId, int logType,
            int actorCid, string actorName,
            int targetCid, string targetName, string detail)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO guild_log (
    guild_id, log_type, actor_cid, actor_name, target_cid, target_name, detail
) VALUES (@gid, @type, @acid, @aname, @tcid, @tname, @detail);";
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@type", logType);
                cmd.Parameters.AddWithValue("@acid", actorCid);
                cmd.Parameters.AddWithValue("@aname", actorName ?? string.Empty);
                cmd.Parameters.AddWithValue("@tcid", targetCid);
                cmd.Parameters.AddWithValue("@tname", targetName ?? string.Empty);
                cmd.Parameters.AddWithValue("@detail", detail ?? string.Empty);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>按工会读最近 N 条日志(GM 排查用)。无则返回空表。</summary>
        public List<(int LogId, int LogType, int ActorCid, string ActorName,
                     int TargetCid, string TargetName, string Detail, string CreatedAt)>
            LoadGuildLogs(int guildId, int maxCount)
        {
            var result = new List<(int, int, int, string, int, string, string, string)>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT log_id, log_type, actor_cid, actor_name, target_cid, target_name, detail, created_at
FROM guild_log WHERE guild_id=@gid ORDER BY log_id DESC LIMIT @n;";
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@n", maxCount > 0 ? maxCount : 50);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add((
                            reader.GetInt32(0),
                            reader.GetInt32(1),
                            reader.GetInt32(2),
                            reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                            reader.GetInt32(4),
                            reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                            reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                            reader.IsDBNull(7) ? string.Empty : reader.GetString(7)));
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 0x015A 捐赠: 事务性 "扣捐赠者金币(goldCost) + 累加工会资金 GM(mileage)"。
        /// ★ 2026-09-07 参考包权威: 1000 金币 = 1 GM, 工会资金(guilds.gold)单位 = GM
        ///   (迁移 v25 已把存量 gold/1000 归一), 商店价格表同为 GM。
        /// 成功返回 true 且 newGold=新工会资金(GM); 金币不足返回 false, 不改任何数据。
        /// 个人金币 = character_inventory_items 货币槽 0(CurrencyService.TrySpendGold)。
        /// </summary>
        public bool DonateGold(int guildId, int characterId, int goldCost, int mileage, out int newGold)
        {
            newGold = 0;
            if (goldCost <= 0 || mileage <= 0)
                return false;
            using (var conn = Open())
            using (var tx = conn.BeginTransaction())
            {
                if (!CurrencyService.TrySpendGold(conn, tx, characterId, goldCost))
                    return false;                       // 金币不足, 不累加工会资金
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "UPDATE guilds SET gold = gold + @amt WHERE guild_id = @gid;";
                    cmd.Parameters.AddWithValue("@amt", mileage);
                    cmd.Parameters.AddWithValue("@gid", guildId);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT gold FROM guilds WHERE guild_id = @gid;";
                    cmd.Parameters.AddWithValue("@gid", guildId);
                    var v = cmd.ExecuteScalar();
                    newGold = v == null || v is DBNull ? 0 : Convert.ToInt32(v);
                }
                tx.Commit();
                return true;
            }
        }

        /// <summary>
        /// 创建工会费用(2016 原版规则: 博肯处 30 万金币): 事务性扣角色金币。
        /// 余额不足返回 false, 不改任何数据。
        /// </summary>
        public bool TrySpendGold(int characterId, int amount)
        {
            if (amount <= 0)
                return false;
            using (var conn = Open())
            using (var tx = conn.BeginTransaction())
            {
                if (!CurrencyService.TrySpendGold(conn, tx, characterId, amount))
                    return false;
                tx.Commit();
                return true;
            }
        }

        /// <summary>创建失败时退回已扣费用。</summary>
        public void RefundGold(int characterId, int amount)
        {
            if (amount <= 0)
                return;
            using (var conn = Open())
            using (var tx = conn.BeginTransaction())
            {
                CurrencyService.GrantGold(conn, tx, characterId, amount);
                tx.Commit();
            }
        }

        /// <summary>更新工会宣传语(0x02B3 宣传信息修改)。</summary>
        public void UpdateGuildMemo(int guildId, string memo)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE guilds SET memo = @memo WHERE guild_id = @gid;";
                cmd.Parameters.AddWithValue("@memo", memo ?? string.Empty);
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 全量读取工会激活内容 → (guild_id, content_id, status, level, exp)。
        /// 0x0046 #18 countA×{u8 status, u32 content_id, u8 level, u32 exp} 从此读。
        /// </summary>
        public List<(int GuildId, int ContentId, int Status, int Level, int Exp,
                     string ExpiresAt)> LoadContents()
        {
            var result = new List<(int, int, int, int, int, string)>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT guild_id, content_id, status, level, exp, expires_at "
                    + "FROM guild_contents ORDER BY guild_id, content_id;";
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add((
                            reader.GetInt32(0),
                            reader.GetInt32(1),
                            reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                            reader.IsDBNull(3) ? 1 : reader.GetInt32(3),
                            reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                            reader.IsDBNull(5) ? null : reader.GetString(5)));
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 0x02F8 购买工会商店内容: 事务性扣工会金币 + 激活内容(INSERT OR IGNORE 幂等)。
        /// 成功返回 true 且 newGold=扣后工会金币; 金币不足 / 已激活 返回 false。
        /// </summary>
        public bool BuyGuildContent(int guildId, int contentId, int price, out int newGold)
        {
            newGold = 0;
            if (price <= 0 || contentId <= 0)
                return false;
            using (var conn = Open())
            using (var tx = conn.BeginTransaction())
            {
                // 1) 查当前金币
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT gold FROM guilds WHERE guild_id = @gid;";
                    cmd.Parameters.AddWithValue("@gid", guildId);
                    var v = cmd.ExecuteScalar();
                    var curGold = v == null || v is DBNull ? 0 : Convert.ToInt32(v);
                    if (curGold < price)
                        return false;                       // 金币不足, 不扣
                }
                // 2) 检查是否已激活
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "SELECT 1 FROM guild_contents "
                        + "WHERE guild_id = @gid AND content_id = @cid LIMIT 1;";
                    cmd.Parameters.AddWithValue("@gid", guildId);
                    cmd.Parameters.AddWithValue("@cid", contentId);
                    if (cmd.ExecuteScalar() != null)
                        return false;                       // 已激活, 不重复扣
                }
                // 3) 扣金币
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "UPDATE guilds SET gold = gold - @amt WHERE guild_id = @gid;";
                    cmd.Parameters.AddWithValue("@amt", price);
                    cmd.Parameters.AddWithValue("@gid", guildId);
                    cmd.ExecuteNonQuery();
                }
                // 4) 激活内容
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
INSERT INTO guild_contents (guild_id, content_id, status, level, exp)
VALUES (@gid, @cid, 1, 1, 0);";
                    cmd.Parameters.AddWithValue("@gid", guildId);
                    cmd.Parameters.AddWithValue("@cid", contentId);
                    cmd.ExecuteNonQuery();
                }
                // 5) 读回新金币
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT gold FROM guilds WHERE guild_id = @gid;";
                    cmd.Parameters.AddWithValue("@gid", guildId);
                    var v = cmd.ExecuteScalar();
                    newGold = v == null || v is DBNull ? 0 : Convert.ToInt32(v);
                }
                tx.Commit();
                return true;
            }
        }

        /// <summary>读公会仓库当前容量(格)。</summary>
        public int GetWarehouseCapacity(int guildId)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT warehouse_capacity FROM guilds WHERE guild_id = @gid;";
                cmd.Parameters.AddWithValue("@gid", guildId);
                var v = cmd.ExecuteScalar();
                return v == null || v is DBNull ? 0 : Convert.ToInt32(v);
            }
        }

        /// <summary>
        /// 仓库扩张购买(2026-09-07 参考包迁移 067/Commerce 规则):
        /// 顺序 0 格 → 8 格(id12) → 每次 +16(id11), 上限 maxCapacity(56);
        /// 可重复购买(guild_contents 行 upsert 刷新 bought_at), 同事务扣 GM + 加容量。
        /// </summary>
        public bool BuyWarehouseExpansion(
            int guildId, int contentId, int price, int addSlots, int maxCapacity,
            out int newGold, out int newCapacity)
        {
            newGold = 0;
            newCapacity = 0;
            if (price <= 0 || (addSlots != 8 && addSlots != 16))
                return false;
            using (var conn = Open())
            using (var tx = conn.BeginTransaction())
            {
                int curGold, capacity;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "SELECT gold, warehouse_capacity FROM guilds WHERE guild_id = @gid;";
                    cmd.Parameters.AddWithValue("@gid", guildId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read())
                            return false;
                        var g = reader.GetValue(0);
                        var c = reader.GetValue(1);
                        curGold = g == null || g is DBNull ? 0 : Convert.ToInt32(g);
                        capacity = c == null || c is DBNull ? 0 : Convert.ToInt32(c);
                    }
                }
                if (curGold < price)
                    return false;                                   // GM 不足
                if ((capacity == 0) != (addSlots == 8))
                    return false;                                   // 首购须 8 格, 之后须 +16
                if (capacity + addSlots > maxCapacity)
                    return false;                                   // 超上限
                newCapacity = capacity + addSlots;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "UPDATE guilds SET gold = gold - @amt, warehouse_capacity = @cap " +
                        "WHERE guild_id = @gid;";
                    cmd.Parameters.AddWithValue("@amt", price);
                    cmd.Parameters.AddWithValue("@cap", newCapacity);
                    cmd.Parameters.AddWithValue("@gid", guildId);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
INSERT INTO guild_contents (guild_id, content_id, status, level, exp, bought_at)
VALUES (@gid, @cid, 1, 1, 0, CURRENT_TIMESTAMP)
ON CONFLICT (guild_id, content_id)
DO UPDATE SET bought_at = CURRENT_TIMESTAMP;";
                    cmd.Parameters.AddWithValue("@gid", guildId);
                    cmd.Parameters.AddWithValue("@cid", contentId);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT gold FROM guilds WHERE guild_id = @gid;";
                    cmd.Parameters.AddWithValue("@gid", guildId);
                    var v = cmd.ExecuteScalar();
                    newGold = v == null || v is DBNull ? 0 : Convert.ToInt32(v);
                }
                tx.Commit();
                return true;
            }
        }

        /// <summary>全量读取成员 → (guild_id, character_id, character_name, grade)。</summary>
        public List<(int GuildId, int CharacterId, string CharacterName, int Grade)> LoadMembers()
        {
            var result = new List<(int, int, string, int)>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT guild_id, character_id, character_name, grade FROM guild_members "
                    + "ORDER BY guild_id, joined_at;";
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add((
                            reader.GetInt32(0),
                            reader.GetInt32(1),
                            reader.GetString(2),
                            reader.GetInt32(3)));
                    }
                }
            }
            return result;
        }

        /// <summary>插入工会。guild_id 由 GuildSystem 分配(内存 max+1)。</summary>
        public void InsertGuild(int guildId, string name, string memo, string masterName)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT OR IGNORE INTO guilds (guild_id, name, memo, master_name)
VALUES (@id, @name, @memo, @master);";
                cmd.Parameters.AddWithValue("@id", guildId);
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@memo", memo ?? string.Empty);
                cmd.Parameters.AddWithValue("@master", masterName);
                cmd.ExecuteNonQuery();
            }
        }

        // ── 建会事务变体(2026-09-07 参考包权威: 创建与扣款同一提交事务) ──

        public static bool NameTakenTx(SqliteConnection conn, SqliteTransaction tx, string name)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT 1 FROM guilds WHERE name = @name LIMIT 1;";
                cmd.Parameters.AddWithValue("@name", name);
                return cmd.ExecuteScalar() != null;
            }
        }

        public static bool IsMemberTx(SqliteConnection conn, SqliteTransaction tx, int characterId)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT 1 FROM guild_members WHERE character_id = @cid LIMIT 1;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                return cmd.ExecuteScalar() != null;
            }
        }

        public static int NextGuildIdTx(SqliteConnection conn, SqliteTransaction tx)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT COALESCE(MAX(guild_id), 0) + 1 FROM guilds;";
                return Convert.ToInt32(cmd.ExecuteScalar() ?? 1);
            }
        }

        /// <summary>事务内建会: 普通 INSERT(重名/主键冲突抛异常 → 协调器整体回滚)。</summary>
        public void InsertGuildTx(SqliteConnection conn, SqliteTransaction tx,
            int guildId, string name, string memo, string masterName)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO guilds (guild_id, name, memo, master_name) VALUES (@id, @name, @memo, @master);";
                cmd.Parameters.AddWithValue("@id", guildId);
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@memo", memo ?? string.Empty);
                cmd.Parameters.AddWithValue("@master", masterName);
                cmd.ExecuteNonQuery();
            }
        }

        public void InsertMemberTx(SqliteConnection conn, SqliteTransaction tx,
            int guildId, int characterId, string characterName, int grade)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO guild_members (guild_id, character_id, character_name, is_master, grade)
VALUES (@gid, @cid, @name, @master, @grade);";
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@name", characterName);
                cmd.Parameters.AddWithValue("@master", grade == GuildSystem.GradeMaster ? 1 : 0);
                cmd.Parameters.AddWithValue("@grade", grade);
                cmd.ExecuteNonQuery();
            }
        }

        public void SeedGuildGradeConfigTx(SqliteConnection conn, SqliteTransaction tx, int guildId)
        {
            for (var grade = 1; grade <= 5; grade++)
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT OR IGNORE INTO guild_grade_config (guild_id, grade, perm_bitmap, grade_name)
VALUES (@gid, @grade, @bitmap, @name);";
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@grade", grade);
                cmd.Parameters.AddWithValue("@bitmap", (long)GuildPermissions.DefaultGradeTable[grade].Bitmap);
                cmd.Parameters.AddWithValue("@name", GuildPermissions.DefaultGradeTable[grade].Name);
                cmd.ExecuteNonQuery();
            }
        }

        public List<(int GuildId, int CharacterId, string CharacterName, string Message, DateTime CreatedAt)> LoadPendingApplications()
        {
            var result = new List<(int, int, string, string, DateTime)>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT guild_id, character_id, character_name, message, created_at
FROM guild_applications
WHERE status = 0
ORDER BY application_id;";
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        DateTime createdAt;
                        DateTime.TryParse(reader.GetString(4), out createdAt);
                        result.Add((
                            reader.GetInt32(0),
                            reader.GetInt32(1),
                            reader.GetString(2),
                            reader.GetString(3),
                            createdAt));
                    }
                }
            }
            return result;
        }

        public void UpsertApplication(int guildId, int characterId, string characterName, string message)
        {
            using (var conn = Open())
            using (var tx = conn.BeginTransaction())
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
UPDATE guild_applications
SET character_name = @name, message = @message, created_at = CURRENT_TIMESTAMP,
    processed_at = NULL, processed_by = 0
WHERE guild_id = @gid AND character_id = @cid AND status = 0;
INSERT INTO guild_applications (guild_id, character_id, character_name, message)
SELECT @gid, @cid, @name, @message
WHERE changes() = 0;";
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@name", characterName ?? string.Empty);
                cmd.Parameters.AddWithValue("@message", message ?? string.Empty);
                cmd.ExecuteNonQuery();
                tx.Commit();
            }
        }

        public bool CompleteApplication(
            int guildId, int characterId, string characterName, int grade,
            int status, int processedBy)
        {
            using (var conn = Open())
            using (var tx = conn.BeginTransaction())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
UPDATE guild_applications
SET status = @status, processed_at = CURRENT_TIMESTAMP, processed_by = @by
WHERE guild_id = @gid AND character_id = @cid AND status = 0;";
                    cmd.Parameters.AddWithValue("@status", status);
                    cmd.Parameters.AddWithValue("@by", processedBy);
                    cmd.Parameters.AddWithValue("@gid", guildId);
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    if (cmd.ExecuteNonQuery() != 1)
                        return false;
                }
                if (status == GuildSystem.ApplicationApproved)
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = @"
INSERT INTO guild_members
    (guild_id, character_id, character_name, is_master, grade)
VALUES (@gid, @cid, @name, 0, @grade);";
                        cmd.Parameters.AddWithValue("@gid", guildId);
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.Parameters.AddWithValue("@name", characterName ?? string.Empty);
                        cmd.Parameters.AddWithValue("@grade", grade);
                        cmd.ExecuteNonQuery();
                    }
                }
                tx.Commit();
                return true;
            }
        }

        public bool SetApplicationStatus(
            int guildId, int characterId, int status, int processedBy)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
UPDATE guild_applications
SET status = @status, processed_at = CURRENT_TIMESTAMP, processed_by = @by
WHERE guild_id = @gid AND character_id = @cid AND status = 0;";
                cmd.Parameters.AddWithValue("@status", status);
                cmd.Parameters.AddWithValue("@by", processedBy);
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@cid", characterId);
                return cmd.ExecuteNonQuery() == 1;
            }
        }

        /// <summary>
        /// 2026-09-04: 撤销某角色的全部待审申请(该角色已入会时，他投给其他工会的 pending
        /// 申请必须清掉，否则会永久挂在别家工会的申请列表里，且批准时被 ProcessApplication
        /// 判失败却无任何提示)。只处理 status=0，标记为已取消以保留审计痕迹。
        /// 返回被撤销记录所属的工会 id 列表。
        /// </summary>
        public List<int> WithdrawAllApplications(int characterId)
        {
            var affected = new List<int>();
            using (var conn = Open())
            using (var tx = conn.BeginTransaction())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
SELECT guild_id FROM guild_applications
WHERE character_id = @cid AND status = 0;";
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            affected.Add(reader.GetInt32(0));
                    }
                }
                if (affected.Count > 0)
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = @"
UPDATE guild_applications
SET status = @status, processed_at = CURRENT_TIMESTAMP, processed_by = 0
WHERE character_id = @cid AND status = 0;";
                        cmd.Parameters.AddWithValue("@status", GuildSystem.ApplicationCancelled);
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }
            return affected;
        }

        /// <summary>插入成员(幂等)。grade: 1=会长 2=副会长 3=优秀 4=普通 5=新入。</summary>
        public void InsertMember(int guildId, int characterId, string characterName, int grade)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT OR IGNORE INTO guild_members
    (guild_id, character_id, character_name, is_master, grade)
VALUES (@gid, @cid, @name, @master, @grade);";
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@name", characterName);
                cmd.Parameters.AddWithValue("@master", grade == GuildSystem.GradeMaster ? 1 : 0);
                cmd.Parameters.AddWithValue("@grade", grade);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>更新成员职位(0x007E 调级/0x0079 委任)。is_master 同步维护。</summary>
        public void UpdateMemberGrade(int guildId, int characterId, int grade)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
UPDATE guild_members SET grade=@grade, is_master=@master
WHERE guild_id=@gid AND character_id=@cid;";
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@grade", grade);
                cmd.Parameters.AddWithValue("@master", grade == GuildSystem.GradeMaster ? 1 : 0);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>更新会长名(0x0079 委任后 guilds.master_name 同步)。</summary>
        public void UpdateGuildMasterName(int guildId, string masterName)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "UPDATE guilds SET master_name=@name WHERE guild_id=@gid;";
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@name", masterName ?? string.Empty);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>移除成员(0x0099 退会/踢人)。返回是否删到行。</summary>
        public bool RemoveMember(int guildId, int characterId)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "DELETE FROM guild_members WHERE guild_id=@gid AND character_id=@cid;";
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@cid", characterId);
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        /// <summary>解散工会(0x012F): 删会+成员+内容+职位配置。</summary>
        public void DeleteGuild(int guildId)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
DELETE FROM guilds WHERE guild_id=@gid;
DELETE FROM guild_members WHERE guild_id=@gid;
DELETE FROM guild_contents WHERE guild_id=@gid;
DELETE FROM guild_grade_config WHERE guild_id=@gid;";
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.ExecuteNonQuery();
            }
        }

        // ===== 职位权限配置(guild_grade_config, v21; 2016-04-07 打勾矩阵+职级更名) =====

        /// <summary>建会时按官方默认矩阵播种(INSERT OR IGNORE, 幂等)。</summary>
        public void SeedGuildGradeConfig(int guildId)
        {
            using (var conn = Open())
            {
                for (var grade = 1; grade <= 5; grade++)
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR IGNORE INTO guild_grade_config (guild_id, grade, perm_bitmap, grade_name)
VALUES (@gid, @grade, @bitmap, @name);";
                    cmd.Parameters.AddWithValue("@gid", guildId);
                    cmd.Parameters.AddWithValue("@grade", grade);
                    cmd.Parameters.AddWithValue("@bitmap",
                        (long)GuildPermissions.DefaultGradeTable[grade].Bitmap);
                    cmd.Parameters.AddWithValue("@name",
                        GuildPermissions.DefaultGradeTable[grade].Name);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>读工会职位权限表(0x0046 168B 块数据源)。返回 grade→(位图, 名)。
        /// 缺行(老会)回退官方默认。</summary>
        public Dictionary<int, (uint Bitmap, string Name)> LoadGuildGradeConfig(int guildId)
        {
            var result = new Dictionary<int, (uint, string)>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT grade, perm_bitmap, grade_name FROM guild_grade_config WHERE guild_id=@gid;";
                cmd.Parameters.AddWithValue("@gid", guildId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var grade = reader.GetInt32(0);
                        result[grade] = (
                            (uint)reader.GetInt64(1),
                            reader.IsDBNull(2) ? string.Empty : reader.GetString(2));
                    }
                }
            }
            // 回退默认(老会未播种/缺行)
            for (var grade = 1; grade <= 5; grade++)
            {
                if (!result.ContainsKey(grade))
                    result[grade] = GuildPermissions.DefaultGradeTable[grade];
            }
            return result;
        }

        /// <summary>保存单职位权限位图(0x02EB 打勾矩阵保存, 单职位一包)。</summary>
        public void SetGuildGradeBitmap(int guildId, int grade, uint bitmap)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO guild_grade_config (guild_id, grade, perm_bitmap, grade_name)
VALUES (@gid, @grade, @bitmap, @name)
ON CONFLICT(guild_id, grade) DO UPDATE SET perm_bitmap=@bitmap;";
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@grade", grade);
                cmd.Parameters.AddWithValue("@bitmap", (long)bitmap);
                cmd.Parameters.AddWithValue("@name",
                    GuildPermissions.DefaultGradeTable[grade].Name);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>保存职级名(0x02EC 职级更名)。</summary>
        public void SetGuildGradeName(int guildId, int grade, string name)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO guild_grade_config (guild_id, grade, perm_bitmap, grade_name)
VALUES (@gid, @grade, @bitmap, @name)
ON CONFLICT(guild_id, grade) DO UPDATE SET grade_name=@name;";
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@grade", grade);
                cmd.Parameters.AddWithValue("@bitmap",
                    (long)GuildPermissions.DefaultGradeTable[grade].Bitmap);
                cmd.Parameters.AddWithValue("@name", name ?? string.Empty);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>按名字查重(0x009C 重名检查)。存在返回 guild_id, 否则 null。</summary>
        public int? FindGuildIdByName(string name)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT guild_id FROM guilds WHERE name = @name;";
                cmd.Parameters.AddWithValue("@name", name);
                var v = cmd.ExecuteScalar();
                return v is long l ? (int)l : (int?)null;
            }
        }
    }
}
