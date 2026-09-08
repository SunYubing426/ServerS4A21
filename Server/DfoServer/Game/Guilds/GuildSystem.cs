using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Guilds
{
    /// <summary>
    /// 工会系统(Phase 2): 内存权威 + SQLite 持久化(guilds/guild_members, 迁移 v16)。
    /// 模式与 UnitedFriendSystem 一致: 静态单例 + lock + 懒加载。
    ///
    /// 支撑协议:
    ///   - 0x009C 重名检查(IsNameTaken)
    ///   - 0x0044 创建流程完成 → Create(master 落库)
    ///   - 0x02E7 按角色查会(GetGuildOfCharacter)
    ///   - 0x02E8/0x02F9 搜索/推荐列表(SearchGuilds / 推荐前 N 个)
    /// </summary>
    public static class GuildSystem
    {
        // 职位等级【2026-09-07 权威对齐 A21-公会修复源码包 GuildRankPolicy】:
        //   native grade: 1=会长 2=副会长(限5名) 3=普通会员 4=新入会员 5=优秀会员;
        //   客户端 UI 顺序为 1,2,5,3,4(非数值顺序)。
        //   ★ 旧映射 3=优秀/4=普通/5=新入 系误配(客户端 HasPermission 特判
        //     "职位==5" 即优秀会员专属), 迁移 v25 已重映射存量数据。
        //   成员条目职位字节(0x0043/0x008C b1)与 0x0046 职位字段(#12/#27)
        //   均直接输出此值(0x19DAFB0 纯拷贝, 无变换)。
        public const int GradeMaster = 1;
        public const int GradeViceMaster = 2;
        public const int GradeNormal = 3;
        public const int GradeNewbie = 4;
        public const int GradeElite = 5;

        /// <summary>职位 → 资历级别(1=最高): 会长1 副会长2 优秀3 普通4 新入5。
        /// native grade 数值 ≠ 资历(5=优秀比 3=普通资历高), 层级比较必须用它。</summary>
        public static int SeniorityOf(int grade)
        {
            switch (grade)
            {
                case GradeMaster: return 1;
                case GradeViceMaster: return 2;
                case GradeElite: return 3;
                case GradeNormal: return 4;
                case GradeNewbie: return 5;
                default: return 6;
            }
        }

        // 工会商店内容表【权威定案 2026-09-04 GLM 提取 Script.pvf `etc/(r)guild.etc`
        //   [guild contents list], 原件 analysis_output/(r)guild_etc_extract.txt】。
        // 列: id type param(buff/数量) days unk icon 0 price tag。
        //   id1  永久公会属性     type5 buff169 永久   36000
        //   id2  (1天)公会属性值  type3 buff167        200
        //   id5  (7天)公会属性值  type3 buff167        1400
        //   id8  (30天)公会属性值 type3 buff167        6000
        //   id3/6/9  公会经验值(+10%) type1 1/7/30天   200/1400/6000
        //   id4/7/10 公会支援兵       type2 1/7/30天   1000/7000/30000
        //   id11/12  公会仓库扩张16/8格 type4          10000/5000
        //   id13/14/15 公会组队传送10/100/1000次 type7 300/2850/27000
        //   id16  公会成员扩张券(500名) type6          100000
        //   id22  type20(疑公会大厅)                 10000000
        //   id23/24 公会之契约(300/500名 1天) type21   3000000/5000000
        // ★gf42 旧映射全错(1≠支援兵; 属性值≠10/11/13/15), 全部作废。
        private static readonly Dictionary<int, int> ContentPriceTable =
            new Dictionary<int, int>
            {
                { 1, 36000 },   // 永久公会属性
                { 2, 200 },     // (1天)公会属性值
                { 3, 200 },     // (1天)公会经验值
                { 4, 1000 },    // (1天)公会支援兵
                { 5, 1400 },    // (7天)公会属性值
                { 6, 1400 },    // (7天)公会经验值
                { 7, 7000 },    // (7天)公会支援兵
                { 8, 6000 },    // (30天)公会属性值
                { 9, 6000 },    // (30天)公会经验值
                { 10, 30000 },  // (30天)公会支援兵
                { 11, 10000 },  // 仓库扩张16格
                { 12, 5000 },   // 仓库扩张8格
                { 13, 300 },    // 组队传送10次
                { 14, 2850 },   // 组队传送100次
                { 15, 27000 },  // 组队传送1000次
                { 16, 100000 }, // 成员扩张券500名
                { 22, 10000000 },
                { 23, 3000000 },
                { 24, 5000000 },
            };

        /// <summary>
        /// content_id → type((r)guild.etc col2)。★0x0046 countA 条目的 u32 写 type
        /// 而非 id(2026-09-04 实证: 写 id=1 时客户端"购买的公会内容"按 type=1 查表
        /// 显示"公会经验值"; 客户端按 type 扫表首行渲染名称, 同 type 多行共享显示)。
        /// </summary>
        public static readonly Dictionary<int, int> ContentIdToType =
            new Dictionary<int, int>
            {
                { 1, 5 }, { 2, 3 }, { 3, 1 }, { 4, 2 },
                { 5, 3 }, { 6, 1 }, { 7, 2 }, { 8, 3 },
                { 9, 1 }, { 10, 2 }, { 11, 4 }, { 12, 4 },
                { 13, 7 }, { 14, 7 }, { 15, 7 }, { 16, 6 },
                { 22, 20 }, { 23, 21 }, { 24, 21 },
            };

        /// <summary>
        /// content_id → param((r)guild.etc col4)。0x0046 内容条目 WireValue 用:
        /// type7(组队传送)=剩余次数(次数未消费, 恒为购买全额); type6(扩张券)=扩张名额。
        /// </summary>
        public static readonly Dictionary<int, int> ContentIdToParam =
            new Dictionary<int, int>
            {
                { 13, 10 }, { 14, 100 }, { 15, 1000 }, { 16, 500 },
            };

        private static readonly int DefaultContentPrice = 1000;  // gf42: 未购商品默认 1000, 防止 0 金币漏洞

        /// <summary>
        /// 成员扩张券 content_id((r)guild.etc id16: type6 param=500, 价 100000)。
        /// 购买后把 guilds.member_limit 提升 <see cref="MemberExpansionAmount"/> 名;
        /// guild_contents 主键 (guild_id, content_id) 保证每会只能买一次 → 上限只加一次。
        /// </summary>
        public const int ContentMemberExpansion = 16;
        public const int MemberExpansionAmount = 500;

        /// <summary>公会仓库容量上限(PVF `etc/(r)guild.etc` [guild cargo size limit] = 56)。</summary>
        public const int WarehouseCapacityMax = 56;

        /// <summary>
        /// 副本通关给所属公会累加的里程(= 公会经验)。
        /// 权威值来自 PVF `etc/(r)guild.etc` [Guild Mileage for Dungeon Clear] = 10
        /// (原件 analysis_output/(r)guild_etc_extract.txt 第 25-27 行)。
        /// 落 guilds.exp(迁移 v22)。公会等级阈值 PVF 未给表, 故只累加里程不自动升级。
        /// </summary>
        public const int MileagePerDungeonClear = 10;

        // 管理文本输入限制(权威源码对齐 2026-09-06): 客户端 read 长度上限 0<len<256;
        // 输入单元上限(UTF-16)取自定案报告。公告可空(空串=显式清空), 宣传语不可空白。
        public const int MaxGuildTextWireBytes = 255;
        public const int MaxAnnouncementChars = 100;
        public const int MaxPromoChars = 40;

        public sealed class GuildInfo
        {
            public int GuildId;
            public string Name;
            public string Memo;
            // 公会公告(v24): 0x009A 编辑落库; 0x0046#14 与 0x008D 下发。空串=无公告。
            public string Announcement = string.Empty;
            public string MasterName;
            // 86 版业务字段(职位: 1=会长 2=副会长 3=优秀会员 4=普通会员 5=新入会员)
            public int Level = 1;
            public int Exp;
            public int Gold;
            public int MemberLimit = 300;
            // 公会图标(v27): 0x0046 #12 下发; 16 内置图标, 0x0312 更换。
            public int EmblemId;
            // 公会频道(会长经 CMD 0x033B 把当前所在频道设为公会频道, 2026-09-06)。
            // 0 = 未设置; >0 = 频道号(客户端 0x0046 读原语 #37 → ch%02d 显示)。
            public int RecommendChannelId;
            // 公会公开标志(迁移 v34, 参考包 guilds.public_flag): 0x0046 #11 投影, 默认 0。
            public int PublicFlag;
            public readonly List<(int ContentId, int Status, int Level, int Exp,
                string ExpiresAt)> ActivatedContents =
                new List<(int, int, int, int, string)>();
            public readonly List<(int CharacterId, string CharacterName, int Grade)> Members =
                new List<(int, string, int)>();
        }

        /// <summary>工会操作日志类型(guild_log.log_type, 迁移 v23)。纯审计, 不下发客户端。</summary>
        public static class GuildLogType
        {
            public const int Create = 1;
            public const int Join = 2;
            public const int Leave = 3;
            public const int Kick = 4;
            public const int Grade = 5;
            public const int Delegate = 6;
            public const int Donate = 7;
            public const int BuyContent = 8;
            public const int Dismiss = 9;
            public const int Mileage = 10;
        }

        /// <summary>工会成员上限初值(2026-09-07 参考包权威基准 = 300 人;
        /// 旧默认 30 已按迁移 v27 纠正, 扩张券 id16 → 500)。</summary>
        public const int DefaultMemberLimit = 300;

        /// <summary>
        /// 写工会操作日志(迁移 v23)。审计性质: 任何失败都只记 FileLogger,
        /// 绝不上抛影响主流程(工会主逻辑不得因日志表异常而回滚)。
        /// </summary>
        private static void WriteLog(
            int guildId, int logType,
            int actorCid, string actorName,
            int targetCid, string targetName, string detail)
        {
            try
            {
                Repository.InsertGuildLog(
                    guildId, logType, actorCid, actorName,
                    targetCid, targetName, detail);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Guild] 工会日志写入失败(已忽略) guild={guildId} type={logType}: {ex.Message}");
            }
        }

        /// <summary>取成员在会内登记的角色名(写日志用)。不在会内返回空串。</summary>
        private static string MemberName(GuildInfo g, int characterId)
        {
            if (g == null)
                return string.Empty;
            foreach (var m in g.Members)
                if (m.CharacterId == characterId)
                    return m.CharacterName ?? string.Empty;
            return string.Empty;
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<int, GuildInfo> GuildsById =
            new Dictionary<int, GuildInfo>();
        private static readonly Dictionary<string, GuildInfo> GuildsByName =
            new Dictionary<string, GuildInfo>(StringComparer.Ordinal);
        private static readonly Dictionary<int, GuildInfo> CharacterGuild =
            new Dictionary<int, GuildInfo>();
        private static int _nextGuildId = 1;
        private static bool _loaded;
        private static GuildRepository _repository;

        internal static GuildRepository Repository
        {
            get
            {
                if (_repository != null)
                    return _repository;

                _repository = new GuildRepository(
                    ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                return _repository;
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
                return;
            _loaded = true;

            try
            {
                foreach (var g in Repository.LoadGuilds())
                {
                    var info = new GuildInfo
                    {
                        GuildId = g.GuildId,
                        Name = g.Name,
                        Memo = g.Memo,
                        Announcement = g.Announcement ?? string.Empty,  // v24
                        MasterName = g.MasterName,
                        Gold = g.Gold,
                        Level = g.Level,                    // v22
                        Exp = g.Exp,                        // v22
                        MemberLimit = g.MemberLimit,        // v22
                        EmblemId = g.EmblemId,              // v27
                        PublicFlag = g.PublicFlag,          // v34
                    };
                    GuildsById[info.GuildId] = info;
                    GuildsByName[info.Name] = info;
                    if (info.GuildId >= _nextGuildId)
                        _nextGuildId = info.GuildId + 1;
                }
                foreach (var m in Repository.LoadMembers())
                {
                    if (!GuildsById.TryGetValue(m.GuildId, out var g))
                        continue;
                    g.Members.Add((m.CharacterId, m.CharacterName, m.Grade));
                    if (m.Grade == GradeMaster || !CharacterGuild.ContainsKey(m.CharacterId))
                        CharacterGuild[m.CharacterId] = g;
                }
                foreach (var c in Repository.LoadContents())
                {
                    if (!GuildsById.TryGetValue(c.GuildId, out var g))
                        continue;
                    g.ActivatedContents.Add((c.ContentId, c.Status, c.Level, c.Exp, c.ExpiresAt));
                }
                foreach (var a in Repository.LoadPendingApplications())
                {
                    Applications[(a.GuildId, a.CharacterId)] = new JoinApplication
                    {
                        GuildId = a.GuildId,
                        CharacterId = a.CharacterId,
                        CharacterName = a.CharacterName,
                        Message = a.Message,
                        Time = a.CreatedAt,
                    };
                }
                FileLogger.Log(
                    $"[Guild] 表加载完成: guilds={GuildsById.Count} members={CharacterGuild.Count} " +
                    $"nextId={_nextGuildId}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Guild] 工会表加载失败: {ex}");
            }
        }

        /// <summary>工会名是否已存在(0x009C 重名检查)。</summary>
        public static bool IsNameTaken(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            lock (Sync)
            {
                EnsureLoaded();
                return GuildsByName.ContainsKey(name);
            }
        }

        /// <summary>
        /// 创建工会(0x0044 流程完成时调用)。成功返回新会; 失败(重名/该角色已有会)返回 null。
        /// </summary>
        public static GuildInfo Create(
            string name, string memo, int masterCid, string masterName,
            int foundingCid = 0, string foundingName = null)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(masterName))
                return null;

            lock (Sync)
            {
                EnsureLoaded();
                if (GuildsByName.ContainsKey(name))
                    return null;
                if (CharacterGuild.ContainsKey(masterCid))
                    return null;

                var info = new GuildInfo
                {
                    GuildId = _nextGuildId++,
                    Name = name,
                    Memo = memo ?? string.Empty,
                    MasterName = masterName,
                };
                info.Members.Add((masterCid, masterName, GradeMaster));

                // ★ 认证角色 = 创始成员(2016-03-24 公会改版公告:"建立公会的
                //   成员将自动被加入")。新加入 → 新入会员(5), 之后可由管理升职。
                var hasFounding = foundingCid > 0
                    && foundingCid != masterCid
                    && !CharacterGuild.ContainsKey(foundingCid);
                if (hasFounding)
                    info.Members.Add((foundingCid, foundingName ?? "", GradeNewbie));

                GuildsById[info.GuildId] = info;
                GuildsByName[info.Name] = info;
                CharacterGuild[masterCid] = info;
                if (hasFounding)
                    CharacterGuild[foundingCid] = info;

                try
                {
                    Repository.InsertGuild(info.GuildId, info.Name, info.Memo, info.MasterName);
                    Repository.InsertMember(info.GuildId, masterCid, masterName, GradeMaster);
                    if (hasFounding)
                        Repository.InsertMember(info.GuildId, foundingCid, foundingName ?? "", GradeNewbie);
                    // v21: 建会播种官方默认职位权限矩阵(0x0046 168B 表数据源)
                    Repository.SeedGuildGradeConfig(info.GuildId);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[Guild] 创建落库失败(内存已生效): {ex}");
                }

                WriteLog(
                    info.GuildId, GuildLogType.Create,
                    masterCid, masterName, 0, string.Empty,
                    $"建会 name=\"{info.Name}\"" +
                    (hasFounding ? $" 认证成员={foundingName}(cid={foundingCid})" : ""));
                FileLogger.Log(
                    $"[Guild] 创建工会 id={info.GuildId} name=\"{info.Name}\" " +
                    $"memo=\"{info.Memo}\" master={masterName}(cid={masterCid})" +
                    (hasFounding ? $" 认证成员={foundingName}(cid={foundingCid})" : ""));
                return info;
            }
        }

        /// <summary>建会失败原因(<see cref="CreateCommitted"/>)。</summary>
        public enum GuildCreateError { None, InvalidRequest, NameTaken, AlreadyMember, InsufficientGold, PersistenceFailed }

        /// <summary>
        /// 创建工会(2026-09-07 参考包权威: 创建与扣款【同一提交事务】):
        /// 校验(重名/已在会) → 查金币槽 0 ≥ 30 万 → 插公会+会长(+认证创始成员)+
        /// 播种权限表 → 扣金币, 全部挂在 OnlineInventoryMutationCommitCoordinator
        /// 提交域; 任一步失败整体回滚(旧"先扣款→创建→失败退款"两阶段作废)。
        /// </summary>
        internal static GuildInfo CreateCommitted(
            Game.Inventory.InventoryLease lease, string name, string memo,
            int masterCid, string masterName, int foundingCid, string foundingName,
            out GuildCreateError error)
        {
            error = GuildCreateError.None;
            if (lease == null || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(masterName))
            {
                error = GuildCreateError.InvalidRequest;
                return null;
            }
            // 快速内存预检(避免无谓事务; 事务内仍会权威重查)
            lock (Sync)
            {
                EnsureLoaded();
                if (GuildsByName.ContainsKey(name))
                {
                    error = GuildCreateError.NameTaken;
                    return null;
                }
                if (CharacterGuild.ContainsKey(masterCid))
                {
                    error = GuildCreateError.AlreadyMember;
                    return null;
                }
            }
            var newGuildId = 0;
            var txError = GuildCreateError.None;
            lock (lease.SyncRoot)
            {
                if (!Game.Inventory.InventoryPersistenceService.SaveDirty(lease))
                {
                    error = GuildCreateError.PersistenceFailed;
                    return null;
                }
                var committed = Game.Inventory.OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease, "guild-create", (connection, transaction) =>
                    {
                        if (GuildRepository.NameTakenTx(connection, transaction, name))
                        {
                            txError = GuildCreateError.NameTaken;
                            return true;
                        }
                        if (GuildRepository.IsMemberTx(connection, transaction, masterCid))
                        {
                            txError = GuildCreateError.AlreadyMember;
                            return true;
                        }
                        var gold = lease.Inventory.GetMainVirtualCount(
                            Game.Inventory.InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
                        if (gold < CreateCostGold)
                        {
                            txError = GuildCreateError.InsufficientGold;
                            return true;
                        }
                        var gid = GuildRepository.NextGuildIdTx(connection, transaction);
                        var insertFounding = foundingCid > 0
                            && foundingCid != masterCid
                            && !GuildRepository.IsMemberTx(connection, transaction, foundingCid);
                        Repository.InsertGuildTx(connection, transaction, gid, name, memo, masterName);
                        Repository.InsertMemberTx(connection, transaction, gid, masterCid, masterName, GradeMaster);
                        if (insertFounding)
                            Repository.InsertMemberTx(connection, transaction, gid, foundingCid, foundingName ?? string.Empty, GradeNewbie);
                        Repository.SeedGuildGradeConfigTx(connection, transaction, gid);
                        if (!lease.Inventory.SetMainVirtualCount(
                                Game.Inventory.InventoryService.MainVirtualCurrencySlotStart, 0, gold - CreateCostGold))
                            return false;
                        newGuildId = gid;
                        return true;
                    });
                if (txError != GuildCreateError.None)
                {
                    error = txError;
                    return null;
                }
                if (!committed)
                {
                    error = GuildCreateError.PersistenceFailed;
                    return null;
                }
            }
            if (newGuildId <= 0)
                return null;   // 业务拒绝(error 已设置)
            // 提交成功 → 内存登记(与 DB 已提交事实对齐)
            lock (Sync)
            {
                EnsureLoaded();
                if (GuildsByName.ContainsKey(name) || CharacterGuild.ContainsKey(masterCid))
                {
                    // 极端竞态(DB 已提交而内存冲突): 以库为准, 重启恢复; 记严重日志。
                    FileLogger.Log(
                        $"[Guild] 建会内存登记冲突(DB 已提交 gid={newGuildId}): name=\"{name}\" cid={masterCid}");
                    error = GuildCreateError.NameTaken;
                    return null;
                }
                var info = new GuildInfo
                {
                    GuildId = newGuildId,
                    Name = name,
                    Memo = memo ?? string.Empty,
                    MasterName = masterName,
                };
                info.Members.Add((masterCid, masterName, GradeMaster));
                var hasFounding = foundingCid > 0
                    && foundingCid != masterCid
                    && !CharacterGuild.ContainsKey(foundingCid);
                if (hasFounding)
                    info.Members.Add((foundingCid, foundingName ?? string.Empty, GradeNewbie));
                GuildsById[info.GuildId] = info;
                GuildsByName[info.Name] = info;
                CharacterGuild[masterCid] = info;
                if (hasFounding)
                    CharacterGuild[foundingCid] = info;
                if (info.GuildId >= _nextGuildId)
                    _nextGuildId = info.GuildId + 1;
                WriteLog(
                    info.GuildId, GuildLogType.Create,
                    masterCid, masterName, 0, string.Empty,
                    $"建会 name=\"{info.Name}\"(事务扣款 {CreateCostGold})" +
                    (hasFounding ? $" 认证成员={foundingName}(cid={foundingCid})" : ""));
                FileLogger.Log(
                    $"[Guild] 创建工会(事务) id={info.GuildId} name=\"{info.Name}\" " +
                    $"master={masterName}(cid={masterCid}) 扣款={CreateCostGold}");
                return info;
            }
        }

        /// <summary>按 id 查工会(退会/解散后判存活用)。无返回 null。</summary>
        public static GuildInfo GetGuildById(int guildId)
        {
            lock (Sync)
            {
                EnsureLoaded();
                return GuildsById.TryGetValue(guildId, out var g) ? g : null;
            }
        }

        /// <summary>查角色的工会(0x02E7)。无返回 null。</summary>
        public static GuildInfo GetGuildOfCharacter(int characterId)
        {
            lock (Sync)
            {
                EnsureLoaded();
                return CharacterGuild.TryGetValue(characterId, out var g) ? g : null;
            }
        }

        /// <summary>按角色名查 cid(创建认证校验)。不存在/重名返回 -1。</summary>
        public static int FindCharacterIdByName(string name)
        {
            lock (Sync)
            {
                EnsureLoaded();
                return Repository.FindCharacterIdByName(name);
            }
        }

        /// <summary>按 cid 查 job(0x0043 成员列表职业列)。查不到返回 0。</summary>
        public static int GetJobOfCharacter(int characterId)
        {
            lock (Sync)
            {
                EnsureLoaded();
                return Repository.FindJobById(characterId);
            }
        }

        /// <summary>按 cid 查 level(0x0160 申请列表等级列)。查不到返回 0。</summary>
        public static int GetLevelOfCharacter(int characterId)
        {
            lock (Sync)
            {
                EnsureLoaded();
                return Repository.FindLevelById(characterId);
            }
        }

        /// <summary>
        /// 按 cid 查 grow_type(0x0043 成员行转职字节, 修复"看他人职业错误")。
        /// DB 值即客户端打包格式 (secondGrow&lt;&lt;4)|firstGrow。查不到返回 0。
        /// </summary>
        public static int GetGrowTypeOfCharacter(int characterId)
        {
            lock (Sync)
            {
                EnsureLoaded();
                return Repository.FindGrowTypeById(characterId);
            }
        }

        /// <summary>1000 金币 = 1 GM(2026-09-07 参考包权威, 与客户端捐赠提示一致)。</summary>
        public const int GoldPerMileage = 1000;

        /// <summary>
        /// 0x015A 捐赠: 事务性 "扣捐赠者金币 + 累加工会资金(GM)"。
        /// ★ 2026-09-07 参考包权威: 1000 金币 = 1 GM。不足 1000 拒绝;
        ///   按完整兑换单位扣费(mileage×1000), 余数留在玩家钱包;
        ///   工会资金(guilds.gold)单位 = GM(v25 已把存量 /1000 归一)。
        /// 成员须在会内; 金币不足请求额返回 false; 成功返回新工会资金(GM)。
        /// </summary>
        public static bool DonateGold(
            int characterId,
            int amount,
            out int newGuildGold,
            out int guildId)
        {
            newGuildGold = 0;
            guildId = 0;
            if (amount < GoldPerMileage)
                return false;
            var mileage = amount / GoldPerMileage;
            var cost = mileage * GoldPerMileage;
            lock (Sync)
            {
                EnsureLoaded();
                if (!CharacterGuild.TryGetValue(characterId, out var g) || g == null)
                    return false;
                if (!Repository.DonateGold(g.GuildId, characterId, cost, mileage, out newGuildGold))
                    return false;
                g.Gold = newGuildGold;              // 内存同步(GM)
                guildId = g.GuildId;
                WriteLog(
                    g.GuildId, GuildLogType.Donate,
                    characterId, MemberName(g, characterId), 0, string.Empty,
                    $"捐赠 {amount} 金币(扣 {cost}) → +{mileage} GM, 工会资金 {newGuildGold}");
                // ★ 贡献累计: 每 1000 金币 = 1 贡献点(参考包规则; 尽力而为不阻塞)
                GuildContributionService.RecordDonationContribution(
                    characterId, MemberName(g, characterId), g.GuildId, cost, mileage);
                return true;
            }
        }

        /// <summary>
        /// 设置工会等级/经验(迁移 v22)。客户端无对应写协议, 供 GM/运营与后续
        /// 疲劳经验接入调用。level 夹到 1..255。返回是否找到该会。
        /// </summary>
        public static bool SetGuildLevel(int guildId, int level, int exp)
        {
            lock (Sync)
            {
                EnsureLoaded();
                if (!GuildsById.TryGetValue(guildId, out var g))
                    return false;
                g.Level = Math.Max(1, Math.Min(level, 255));
                g.Exp = Math.Max(0, exp);
                try
                {
                    Repository.UpdateGuildProfile(guildId, g.Level, g.Exp, g.MemberLimit);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[Guild] 工会等级落库失败(内存已生效): {ex}");
                }
                return true;
            }
        }

        /// <summary>
        /// 副本通关给所属公会累加里程(公会经验)。
        /// 出处: PVF `etc/(r)guild.etc` [Guild Mileage for Dungeon Clear] = 10
        /// → 见 <see cref="MileagePerDungeonClear"/>。落 guilds.exp(迁移 v22)。
        /// 只累加不自动升级: PVF 未给出公会等级经验阈值表, 发明数值不如留白。
        /// 返回累加后的总里程; 不在会 / amount&lt;=0 → -1(不写库)。
        /// </summary>
        public static int AddGuildMileage(int characterId, int amount, int dungeonId = 0)
        {
            if (amount <= 0)
                return -1;
            lock (Sync)
            {
                EnsureLoaded();
                if (!CharacterGuild.TryGetValue(characterId, out var g) || g == null)
                    return -1;
                g.Exp = Math.Max(0, g.Exp + amount);
                try
                {
                    Repository.UpdateGuildProfile(g.GuildId, g.Level, g.Exp, g.MemberLimit);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[Guild] 工会里程落库失败(内存已生效): {ex}");
                }
                WriteLog(
                    g.GuildId, GuildLogType.Mileage,
                    characterId, MemberName(g, characterId), 0, string.Empty,
                    $"副本通关 dungeon={dungeonId} 里程 +{amount} → 总 {g.Exp}");
                return g.Exp;
            }
        }

        /// <summary>
        /// 调整工会成员上限(迁移 v22)。下限 = 当前成员数(不允许把已有成员挤成超限)。
        /// 返回是否成功(会不存在 / 上限低于现有人数 均 false)。
        /// </summary>
        public static bool SetMemberLimit(int guildId, int memberLimit)
        {
            lock (Sync)
            {
                EnsureLoaded();
                if (!GuildsById.TryGetValue(guildId, out var g))
                    return false;
                if (memberLimit < g.Members.Count)
                    return false;
                g.MemberLimit = Math.Max(1, memberLimit);
                try
                {
                    Repository.UpdateGuildProfile(guildId, g.Level, g.Exp, g.MemberLimit);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[Guild] 成员上限落库失败(内存已生效): {ex}");
                }
                FileLogger.Log($"[Guild] 成员上限调整 guild={guildId} → {g.MemberLimit}");
                return true;
            }
        }

        /// <summary>读工会最近 N 条操作日志(GM 排查用, 迁移 v23)。</summary>
        public static List<(int LogId, int LogType, int ActorCid, string ActorName,
                             int TargetCid, string TargetName, string Detail, string CreatedAt)>
            GetGuildLogs(int guildId, int maxCount)
        {
            lock (Sync)
            {
                EnsureLoaded();
                try
                {
                    return Repository.LoadGuildLogs(guildId, maxCount);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[Guild] 工会日志读取失败: {ex.Message}");
                    return new List<(int, int, int, string, int, string, string, string)>();
                }
            }
        }

        /// <summary>创建工会费用(2016 原版规则: 博肯处 30 万金币)。</summary>
        public const int CreateCostGold = 300000;

        /// <summary>创建工会扣款(0x0044 确认创建前调用)。余额不足返回 false。</summary>
        public static bool ChargeCreateCost(int characterId, int amount)
        {
            lock (Sync)
            {
                EnsureLoaded();
                return Repository.TrySpendGold(characterId, amount);
            }
        }

        /// <summary>创建失败(重名等竞态)时退回已扣费用。</summary>
        public static void RefundCreateCost(int characterId, int amount)
        {
            lock (Sync)
            {
                EnsureLoaded();
                Repository.RefundGold(characterId, amount);
            }
        }

        /// <summary>
        /// 0x02B3 宣传信息修改: 更新工会宣传语(内存权威 + 落库)。
        /// 仅允许在会成员操作(客户端侧另有职位权限门槛)。
        /// </summary>
        public static bool UpdateMemo(int characterId, string memo, out int guildId)
        {
            guildId = 0;
            // 宣传语不接受空白(权威源码对齐): 空串/纯空白一律拒绝。
            if (string.IsNullOrWhiteSpace(memo))
                return false;
            lock (Sync)
            {
                EnsureLoaded();
                if (!CharacterGuild.TryGetValue(characterId, out var g) || g == null)
                    return false;
                Repository.UpdateGuildMemo(g.GuildId, memo);
                g.Memo = memo;
                guildId = g.GuildId;
                return true;
            }
        }

        /// <summary>
        /// 公告修改(0x009A, 权威源码对齐 2026-09-06): 公告文本(可为空串=显式清空)
        /// 落库 guilds.announcement(v24) 并同步内存。长度/编码校验在 handler 层
        /// (100 UTF-16 单元 / 255 字节), 本方法不重复限制。
        /// </summary>
        public static bool UpdateAnnouncement(int characterId, string announcement, out int guildId)
        {
            guildId = 0;
            var text = announcement ?? string.Empty;
            lock (Sync)
            {
                EnsureLoaded();
                if (!CharacterGuild.TryGetValue(characterId, out var g) || g == null)
                    return false;
                Repository.UpdateGuildAnnouncement(g.GuildId, text);
                g.Announcement = text;
                guildId = g.GuildId;
                return true;
            }
        }

        /// <summary>
        /// 0x02F8 BUY_GUILD_CONTENTS: 购买工会商店内容(gf41)。
        ///   查价格表(硬编码, 待 PVF 逆向替换) → 事务性扣工会金币 + 激活内容 →
        ///   同步内存 ActivatedContents + Gold。已激活 / 金币不足 返回 false。
        /// </summary>
        public static bool BuyGuildContent(
            int characterId,
            int contentId,
            out int newGuildGold,
            out int guildId)
        {
            return BuyGuildContent(characterId, contentId, out newGuildGold, out guildId, out _);
        }

        /// <summary>带失败原因文案的版本(仓库扩张购买给用户明确提示用)。</summary>
        public static bool BuyGuildContent(
            int characterId,
            int contentId,
            out int newGuildGold,
            out int guildId,
            out string error)
        {
            newGuildGold = 0;
            guildId = 0;
            error = null;
            lock (Sync)
            {
                EnsureLoaded();
                if (!CharacterGuild.TryGetValue(characterId, out var g) || g == null)
                    return false;

                // ★ 金币商品(22 改名 / 23/24 契约)不走公会 GM 通道 —— 它们扣【角色金币】,
                //   由 BuyGoldContent 处理(2026-09-07 参考包权威: type20/21 UsesGold)。
                if (contentId is 22 or 23 or 24)
                    return false;

                // 查价格
                int price;
                if (!ContentPriceTable.TryGetValue(contentId, out price))
                    price = DefaultContentPrice;

                // ★ 仓库扩张(id12=+8 格/id11=+16 格, type4, 2026-09-07 参考包权威):
                //   顺序 0 格 → 8 格 → 每次 +16, 上限 56; 可重复购买, 不走"防重激活"。
                if (contentId is 11 or 12)
                {
                    var addSlots = contentId == 12 ? 8 : 16;
                    // 先给出可读的失败原因(仓库规则 2026-09-07 实测: 通用"请稍后再试"无指引)
                    var capNow = Repository.GetWarehouseCapacity(g.GuildId);
                    if ((capNow == 0) != (addSlots == 8))
                    {
                        error = capNow == 0
                            ? "请先在公会商店购买「仓库扩张（8格）」。"
                            : "仓库已开启，请购买「仓库扩张（16格）」。";
                        return false;
                    }
                    if (capNow + addSlots > WarehouseCapacityMax)
                    {
                        error = "公会仓库已达上限（56格）。 ";
                        return false;
                    }
                    if (g.Gold < price)
                    {
                        error = "公会资金（GM）不足。";
                        return false;
                    }
                    if (!Repository.BuyWarehouseExpansion(
                            g.GuildId, contentId, price, addSlots, WarehouseCapacityMax,
                            out newGuildGold, out var newCapacity))
                    {
                        error = "仓库扩张购买失败，请稍后再试。";
                        return false;
                    }
                    g.Gold = newGuildGold;
                    // 内存内容行只登记一次(0x0046 已购内容显示); 重复购买不重复加。
                    if (!g.ActivatedContents.Exists(c => c.ContentId == contentId))
                        g.ActivatedContents.Add((contentId, 1, 1, 0, null));
                    guildId = g.GuildId;
                    WriteLog(
                        g.GuildId, GuildLogType.BuyContent,
                        characterId, MemberName(g, characterId), 0, string.Empty,
                        $"仓库扩张 +{addSlots} 格 → {newCapacity} 格(花费 {price} GM → 工会金币 {newGuildGold})");
                    FileLogger.Log(
                        $"[Guild] 仓库扩张生效 guild={g.GuildId} +{addSlots} → {newCapacity} 格");
                    return true;
                }

                // 防重激活
                foreach (var c in g.ActivatedContents)
                    if (c.ContentId == contentId)
                        return false;

                if (!Repository.BuyGuildContent(g.GuildId, contentId, price, out newGuildGold))
                    return false;

                // 内存同步
                g.Gold = newGuildGold;
                g.ActivatedContents.Add((contentId, 1, 1, 0, null));
                guildId = g.GuildId;
                WriteLog(
                    g.GuildId, GuildLogType.BuyContent,
                    characterId, MemberName(g, characterId), 0, string.Empty,
                    $"购买内容 content_id={contentId} 花费 {price} → 工会金币 {newGuildGold}");

                // 成员扩张券(id16): 容量设为 500(2026-09-07 参考包权威: type6 effect=500
                //   绝对值, 非累加; 300 基准 → 500)。guild_contents 主键防重 → 每会一次。
                if (contentId == ContentMemberExpansion)
                {
                    var expanded = MemberExpansionAmount;
                    try
                    {
                        g.MemberLimit = expanded;
                        Repository.UpdateGuildProfile(
                            g.GuildId, g.Level, g.Exp, g.MemberLimit);
                        WriteLog(
                            g.GuildId, GuildLogType.BuyContent,
                            characterId, MemberName(g, characterId), 0, string.Empty,
                            $"成员扩张券生效: 上限→{expanded}");
                        FileLogger.Log(
                            $"[Guild] 成员扩张券生效 guild={g.GuildId} 上限→{expanded}");
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log($"[Guild] 成员上限落库失败(内存已生效): {ex}");
                    }
                }
                return true;
            }
        }

        // ===== 公会图标/改名/契约(2026-09-07 参考包权威规则适配, 迁移 v27) =====

        /// <summary>公会名校验(改名/建会共用): 非空、≤24 字符、≤255 UTF-8 字节、无控制字符。</summary>
        public static bool IsValidGuildName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 24)
                return false;
            if (System.Text.Encoding.UTF8.GetByteCount(name) > 255)
                return false;
            foreach (var ch in name)
                if (char.IsControl(ch))
                    return false;
            return true;
        }

        /// <summary>16 内置图标中免费个数(前 5 免费, 其余 2000 GM; 重复应用当前图标免费)。</summary>
        public const int FreeEmblemCount = 5;
        public const int EmblemMileageCost = 2000;
        public const int EmblemCount = 16;
        /// <summary>改名价格(角色金币, content_id=22 type20)。</summary>
        public const int RenameGoldCost = 10000000;
        /// <summary>契约(content_id=23/24 type21): 价格(角色金币)与匹配容量。</summary>
        public const int Contract300GoldCost = 3000000;
        public const int Contract500GoldCost = 5000000;
        public const int ContractMaxDays = 30;

        /// <summary>0x0312 CHANGE_GUILD_MARK: 更换公会图标。
        /// 前 5 免费, 其余 2000 GM(公会资金); 重复应用当前图标免费。</summary>
        public static bool ChangeEmblem(
            int characterId, int emblemId, out int guildId, out int newGuildGold)
        {
            guildId = 0;
            newGuildGold = 0;
            if (emblemId < 0 || emblemId >= EmblemCount)
                return false;
            lock (Sync)
            {
                EnsureLoaded();
                if (!CharacterGuild.TryGetValue(characterId, out var g) || g == null)
                    return false;
                if (g.EmblemId == emblemId)      // 重复应用当前图标: 免费成功
                {
                    guildId = g.GuildId;
                    newGuildGold = g.Gold;
                    return true;
                }
                var cost = emblemId < FreeEmblemCount ? 0 : EmblemMileageCost;
                if (!Repository.ChangeEmblem(g.GuildId, emblemId, cost, out newGuildGold))
                    return false;
                g.Gold = newGuildGold;
                g.EmblemId = emblemId;
                guildId = g.GuildId;
                WriteLog(
                    g.GuildId, GuildLogType.BuyContent,
                    characterId, MemberName(g, characterId), 0, string.Empty,
                    $"更换公会图标 emblem={emblemId} 花费 {cost} GM → 工会资金 {newGuildGold}");
                return true;
            }
        }

        /// <summary>公会改名(0x02F8 content_id=22; 调用方需先扣角色金币, 失败须退款)。
        /// 仅做重名校验 + 落库 + 内存同步。error: null=成功。</summary>
        public static bool RenameGuild(
            int characterId, string newName, out int guildId, out string error)
        {
            guildId = 0;
            error = null;
            lock (Sync)
            {
                EnsureLoaded();
                if (!CharacterGuild.TryGetValue(characterId, out var g) || g == null)
                {
                    error = "not-member";
                    return false;
                }
                if (!IsValidGuildName(newName))
                {
                    error = "invalid-name";
                    return false;
                }
                if (GuildsByName.TryGetValue(newName, out var existing) && existing.GuildId != g.GuildId)
                {
                    error = "name-taken";
                    return false;
                }
                try
                {
                    Repository.UpdateGuildName(g.GuildId, newName);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[Guild] 改名落库失败 gid={g.GuildId}: {ex.Message}");
                    error = "persistence";
                    return false;
                }
                GuildsByName.Remove(g.Name);
                g.Name = newName;
                GuildsByName[newName] = g;
                guildId = g.GuildId;
                WriteLog(
                    g.GuildId, GuildLogType.BuyContent,
                    characterId, MemberName(g, characterId), 0, string.Empty,
                    $"公会改名 → \"{newName}\"(花费 {RenameGoldCost} 角色金币)");
                return true;
            }
        }

        /// <summary>购买公会契约(0x02F8 content_id=23/24): 容量匹配校验 +
        /// 角色金币扣款 + 到期累计(最多 30 天)。error: null=成功。</summary>
        public static bool BuyGuildContract(
            int characterId, int contentId, out int guildId, out string error)
        {
            guildId = 0;
            error = null;
            int capacity, goldCost;
            if (contentId == 23) { capacity = 300; goldCost = Contract300GoldCost; }
            else if (contentId == 24) { capacity = 500; goldCost = Contract500GoldCost; }
            else { error = "invalid-content"; return false; }
            lock (Sync)
            {
                EnsureLoaded();
                if (!CharacterGuild.TryGetValue(characterId, out var g) || g == null)
                {
                    error = "not-member";
                    return false;
                }
                // 容量匹配: 300 契约需上限=300; 500 契约需先扩张到 500
                if (g.MemberLimit != capacity)
                {
                    error = "capacity-mismatch";
                    return false;
                }
                var now = DateTime.UtcNow;
                var from = now;
                var oldExpiryText = Repository.GetActiveContractExpiry(g.GuildId);
                if (!string.IsNullOrEmpty(oldExpiryText)
                    && DateTime.TryParseExact(
                        oldExpiryText, "yyyy-MM-dd HH:mm:ss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var oldExpiry)
                    && oldExpiry > now)
                {
                    from = oldExpiry;
                }
                var newExpiry = from.AddDays(1);
                if (newExpiry > now.AddDays(ContractMaxDays))
                {
                    error = "contract-limit";
                    return false;
                }
                if (!Repository.TrySpendGold(characterId, goldCost))
                {
                    error = "insufficient-gold";
                    return false;
                }
                var expiryText = newExpiry.ToString("yyyy-MM-dd HH:mm:ss");
                try
                {
                    Repository.UpsertGuildContract(
                        g.GuildId, contentId, capacity,
                        now.ToString("yyyy-MM-dd HH:mm:ss"), expiryText);
                }
                catch (Exception ex)
                {
                    Repository.RefundGold(characterId, goldCost);
                    FileLogger.Log($"[Guild] 契约落库失败(已退款) gid={g.GuildId}: {ex.Message}");
                    error = "persistence";
                    return false;
                }
                // 内存同步: 移除旧契约行(23/24 互换)再登记新行
                g.ActivatedContents.RemoveAll(c => c.ContentId is 23 or 24);
                g.ActivatedContents.Add((contentId, 1, capacity, 0, expiryText));
                guildId = g.GuildId;
                WriteLog(
                    g.GuildId, GuildLogType.BuyContent,
                    characterId, MemberName(g, characterId), 0, string.Empty,
                    $"购买 {capacity} 人契约 1 天(花费 {goldCost} 角色金币) → 到期 {expiryText}");
                return true;
            }
        }

        /// <summary>公会是否有生效中的契约(0x04C4 每日领取前置)。</summary>
        public static bool HasActiveContract(int guildId)
        {
            lock (Sync)
            {
                EnsureLoaded();
                if (!GuildsById.TryGetValue(guildId, out var g) || g == null)
                    return false;
                var now = DateTime.UtcNow;
                foreach (var c in g.ActivatedContents)
                {
                    if (c.ContentId is not (23 or 24) || string.IsNullOrEmpty(c.ExpiresAt))
                        continue;
                    if (DateTime.TryParseExact(
                            c.ExpiresAt, "yyyy-MM-dd HH:mm:ss",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal,
                            out var expiry)
                        && expiry > now)
                        return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 按 cid 查 account_id(0x0043/0x008C 成员条目第 2 个 u32 = 客户端
        /// "按账号分组"的 key)。查不到返回 0。
        /// </summary>
        public static int GetAccountIdOfCharacter(int characterId)
        {
            lock (Sync)
            {
                EnsureLoaded();
                return Repository.FindAccountIdByCharacterId(characterId);
            }
        }

        /// <summary>
        /// 按 cid 查 town_id(0x0043/0x008C 成员行 p3 = 悬浮"chXX 地名"的地名 id)。
        /// 查不到返回 0(客户端显示"未知")。
        /// </summary>
        public static int GetTownIdOfCharacter(int characterId)
        {
            lock (Sync)
            {
                EnsureLoaded();
                return Repository.FindTownIdById(characterId);
            }
        }

        /// <summary>
        /// 查同账号下、在同一工会的角色名列表(0x02E9 成员列表"+"展开)。
        /// 无工会时返回空列表。
        /// </summary>
        public static List<string> GetAccountMemberNames(int characterId)
        {
            lock (Sync)
            {
                EnsureLoaded();
                if (!CharacterGuild.TryGetValue(characterId, out var guild))
                    return new List<string>();
                var accountIds = Repository.FindAccountCharacterIds(characterId);
                return guild.Members
                    .Where(m => accountIds.Contains(m.CharacterId))
                    .Select(m => m.CharacterName)
                    .ToList();
            }
        }

        // ══════════ 职位权限(2016-04-07 打勾矩阵, v21 guild_grade_config) ══════════

        // 职位权限配置缓存(guild_id → grade→(位图,名)); 写操作(0x02EB/0x02EC)同步失效
        private static readonly Dictionary<int, Dictionary<int, (uint Bitmap, string Name)>>
            _gradeConfigCache = new Dictionary<int, Dictionary<int, (uint, string)>>();

        /// <summary>读工会职位权限表(0x0046 168B 块数据源)。缺行回退官方默认。</summary>
        public static Dictionary<int, (uint Bitmap, string Name)> GetGradeConfig(int guildId)
        {
            lock (Sync)
            {
                EnsureLoaded();
                if (!_gradeConfigCache.TryGetValue(guildId, out var cfg))
                {
                    cfg = Repository.LoadGuildGradeConfig(guildId);
                    _gradeConfigCache[guildId] = cfg;
                }
                return cfg;
            }
        }

        /// <summary>角色的工会职位(1..5); 无工会返回 0。</summary>
        public static int GetGradeOfCharacter(int characterId)
        {
            lock (Sync)
            {
                EnsureLoaded();
                if (!CharacterGuild.TryGetValue(characterId, out var g) || g == null)
                    return 0;
                var m = g.Members.FirstOrDefault(x => x.CharacterId == characterId);
                return m.CharacterId == characterId ? m.Grade : 0;
            }
        }

        /// <summary>
        /// 权限校验(镜像客户端 0x19EA3F0): 会长(grade1)恒 true;
        /// 其余查 guild_grade_config 位图 bit。无工会/无配置 → false。
        /// </summary>
        public static bool HasPermission(int characterId, int bit)
        {
            lock (Sync)
            {
                EnsureLoaded();
                if (!CharacterGuild.TryGetValue(characterId, out var g) || g == null)
                    return false;
                var m = g.Members.FirstOrDefault(x => x.CharacterId == characterId);
                if (m.CharacterId != characterId)
                    return false;
                if (m.Grade == GradeMaster)
                    return true;
                var safeGrade = GuildPermissions.IsValidGrade(m.Grade) ? m.Grade : GradeNormal;
                var cfg = GetGradeConfig(g.GuildId);
                if (!cfg.TryGetValue(safeGrade, out var entry))
                    entry = GuildPermissions.DefaultGradeTable[safeGrade];
                return (entry.Bitmap & GuildPermissions.Mask(bit)) != 0;
            }
        }

        /// <summary>保存单职位权限位图(0x02EB)。要求操作者有 Appoint(bit11) 权限。</summary>
        public static bool SetGradeBitmap(int operatorCid, int grade, uint bitmap)
        {
            if (!GuildPermissions.IsValidGrade(grade) || grade == GradeMaster)
                return false; // 会长权限固定全开, 不可改
            lock (Sync)
            {
                EnsureLoaded();
                if (!CharacterGuild.TryGetValue(operatorCid, out var g) || g == null)
                    return false;
                if (!HasPermission(operatorCid, GuildPermissions.Appoint))
                    return false;
                Repository.SetGuildGradeBitmap(g.GuildId, grade, bitmap);
                GetGradeConfig(g.GuildId)[grade] =
                    (bitmap, GetGradeConfig(g.GuildId)[grade].Name);
                FileLogger.Log(
                    $"[Guild] 职位权限保存 guild={g.GuildId} grade={grade} bitmap=0x{bitmap:X8} by cid={operatorCid}");
                return true;
            }
        }

        /// <summary>职级更名(0x02EC)。要求 Appoint(bit11) 权限; 会长名不可改。</summary>
        public static bool SetGradeName(int operatorCid, int grade, string name)
        {
            if (!GuildPermissions.IsValidGrade(grade) || grade == GradeMaster
                || string.IsNullOrWhiteSpace(name))
                return false;
            if (name.Length > 12)
                name = name.Substring(0, 12);
            lock (Sync)
            {
                EnsureLoaded();
                if (!CharacterGuild.TryGetValue(operatorCid, out var g) || g == null)
                    return false;
                if (!HasPermission(operatorCid, GuildPermissions.Appoint))
                    return false;
                Repository.SetGuildGradeName(g.GuildId, grade, name);
                GetGradeConfig(g.GuildId)[grade] =
                    (GetGradeConfig(g.GuildId)[grade].Bitmap, name);
                FileLogger.Log(
                    $"[Guild] 职级更名 guild={g.GuildId} grade={grade} name=\"{name}\" by cid={operatorCid}");
                return true;
            }
        }

        /// <summary>
        /// 调整成员职位(0x007E)。规则(官方+位语义):
        /// 目标 grade2 → 需 AppointVice(bit12); grade3..5 → 需 Appoint(bit11)。
        /// 非会长只能调整比自己职位低(数值大)的成员、且只能任命比自己低的职位;
        /// 不能调会长(走委任); 副会长限 5 名。返回 null=成功, 否则错误原因。
        /// </summary>
        public static string ChangeMemberGrade(int operatorCid, string targetName, int newGrade)
        {
            if (!GuildPermissions.IsValidGrade(newGrade) || newGrade == GradeMaster)
                return "目标职位无效";
            lock (Sync)
            {
                EnsureLoaded();
                if (!CharacterGuild.TryGetValue(operatorCid, out var g) || g == null)
                    return "你不在工会中";
                var opIdx = g.Members.FindIndex(x => x.CharacterId == operatorCid);
                if (opIdx < 0)
                    return "你不在工会中";
                var opGrade = g.Members[opIdx].Grade;
                var tgIdx = g.Members.FindIndex(
                    x => string.Equals(x.CharacterName, targetName, StringComparison.Ordinal));
                if (tgIdx < 0)
                    return "目标不在本工会";
                var target = g.Members[tgIdx];
                if (target.CharacterId == operatorCid)
                    return "不能调整自己的职位";
                if (target.Grade == GradeMaster)
                    return "不能调整会长职位(请使用委任)";
                if (target.Grade == newGrade)
                    return null; // 幂等
                // 层级: 非会长只能管比自己低的成员、任命比自己低的职位
                //   (按 SeniorityOf 资历比较, native grade 数值 ≠ 资历)
                if (opGrade != GradeMaster)
                {
                    if (SeniorityOf(target.Grade) <= SeniorityOf(opGrade))
                        return "只能调整比自己职位低的成员";
                    if (SeniorityOf(newGrade) <= SeniorityOf(opGrade))
                        return "只能任命比自己职位低的职位";
                }
                // 权限位: grade2 需 AppointVice, 其余需 Appoint
                var needBit = newGrade == GradeViceMaster
                    ? GuildPermissions.AppointVice
                    : GuildPermissions.Appoint;
                if (!HasPermission(operatorCid, needBit))
                    return "没有权限";
                if (newGrade == GradeViceMaster
                    && g.Members.Count(x => x.Grade == GradeViceMaster) >= 5)
                    return "副会长已达上限(5名)";
                g.Members[tgIdx] = (target.CharacterId, target.CharacterName, newGrade);
                try
                {
                    Repository.UpdateMemberGrade(g.GuildId, target.CharacterId, newGrade);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[Guild] 调级落库失败(内存已生效): {ex}");
                }
                WriteLog(
                    g.GuildId, GuildLogType.Grade,
                    operatorCid, MemberName(g, operatorCid),
                    target.CharacterId, targetName,
                    $"职位 {target.Grade}→{newGrade}");
                FileLogger.Log(
                    $"[Guild] 调级 guild={g.GuildId} \"{targetName}\"(cid={target.CharacterId}) " +
                    $"{target.Grade}→{newGrade} by cid={operatorCid}(grade={opGrade})");
                return null;
            }
        }

        /// <summary>
        /// 退会(0x0099)。会长: 只剩自己时退会=解散; 还有人 → 须先委任(0x0329/0x0079)。
        /// 返回 null=成功(out 原工会, 已离会), 否则错误原因。
        /// </summary>
        public static string LeaveGuild(int characterId, out GuildInfo guild)
        {
            guild = null;
            lock (Sync)
            {
                EnsureLoaded();
                if (!CharacterGuild.TryGetValue(characterId, out var g) || g == null)
                    return "你不在工会中";
                var m = g.Members.FirstOrDefault(x => x.CharacterId == characterId);
                if (m.CharacterId != characterId)
                    return "你不在工会中";
                if (m.Grade == GradeMaster && g.Members.Count > 1)
                    return "会长需先委任他人才能退会";

                if (m.Grade == GradeMaster && g.Members.Count == 1)
                {
                    // 光杆会长退会 = 解散
                    DissolveGuildLocked(g, characterId, m.CharacterName, "会长退会→解散");
                    guild = g;
                    FileLogger.Log($"[Guild] 会长退会→解散 guild={g.GuildId} name=\"{g.Name}\"");
                    return null;
                }

                g.Members.RemoveAll(x => x.CharacterId == characterId);
                CharacterGuild.Remove(characterId);
                try
                {
                    Repository.RemoveMember(g.GuildId, characterId);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[Guild] 退会落库失败(内存已生效): {ex}");
                }
                guild = g;
                WriteLog(
                    g.GuildId, GuildLogType.Leave,
                    characterId, m.CharacterName, 0, string.Empty,
                    $"退会(grade={m.Grade}) 剩 {g.Members.Count}");
                FileLogger.Log(
                    $"[Guild] 成员退会 guild={g.GuildId} \"{m.CharacterName}\"(cid={characterId}) 剩 {g.Members.Count}");
                return null;
            }
        }

        /// <summary>
        /// 踢人(强制退会)。权限规则(客户端文案定案 2026-09-04):
        ///   0x19C5: 会长/副会长/优秀成员(grade≤3)可踢;
        ///   0xB509: 只有会长才能踢副会长;
        ///   会长不可被踢(须走委任/解散)。
        /// 返回 null=成功(out 工会/out 被踢者), 否则错误原因。
        /// </summary>
        public static string KickMember(int operatorCid, string targetName,
            out GuildInfo guild, out (int CharacterId, string CharacterName, int Grade) target)
        {
            guild = null;
            target = default;
            lock (Sync)
            {
                EnsureLoaded();
                if (!CharacterGuild.TryGetValue(operatorCid, out var g) || g == null)
                    return "你不在工会中";
                var opIdx = g.Members.FindIndex(x => x.CharacterId == operatorCid);
                if (opIdx < 0)
                    return "你不在工会中";
                var opGrade = g.Members[opIdx].Grade;
                var tgIdx = g.Members.FindIndex(
                    x => string.Equals(x.CharacterName, targetName, StringComparison.Ordinal));
                if (tgIdx < 0)
                    return "目标不在本工会";
                target = g.Members[tgIdx];
                guild = g;
                if (target.CharacterId == operatorCid)
                    return "不能踢自己(请使用退会)";
                if (target.Grade == GradeMaster)
                    return "不能强制踢除会长";
                // 层级: 只有会长/副会长/优秀成员(资历 ≤ 优秀)才能踢
                //   (按 SeniorityOf 比较, native grade 数值 ≠ 资历)
                if (SeniorityOf(opGrade) > SeniorityOf(GradeElite))
                    return "只有会长、副会长或优秀成员才能强制踢除";
                // ★ 权限位: 开除 = bit10(2026-09-07 参考包权威: ExpelMember)。
                //   默认矩阵下 优秀 无 bit10 → 实际可踢者 = 会长/副会长;
                //   公会若在打勾矩阵给优秀加 bit10 则放行(与客户端 0x19C5 文案一致)。
                if (!HasPermission(operatorCid, GuildPermissions.ExpelMember))
                    return "没有开除权限";
                // 0xB509: 踢副会长仅会长
                if (target.Grade == GradeViceMaster && opGrade != GradeMaster)
                    return "只有公会会长才能强制踢除副会长";

                g.Members.RemoveAt(tgIdx);
                CharacterGuild.Remove(target.CharacterId);
                try
                {
                    Repository.RemoveMember(g.GuildId, target.CharacterId);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[Guild] 踢人落库失败(内存已生效): {ex}");
                }
                WriteLog(
                    g.GuildId, GuildLogType.Kick,
                    operatorCid, MemberName(g, operatorCid),
                    target.CharacterId, targetName,
                    $"踢出(操作者 grade={opGrade}) 剩 {g.Members.Count}");
                FileLogger.Log(
                    $"[Guild] 踢人 guild={g.GuildId} \"{targetName}\"(cid={target.CharacterId}) " +
                    $"by cid={operatorCid}(grade={opGrade}) 剩 {g.Members.Count}");
                return null;
            }
        }

        /// <summary>
        /// 委任会长(0x0079)。仅现任会长; 目标须为本会其他成员。
        /// 原会长降为普通会员(4)。返回 null=成功(out 新会长cid)。
        /// </summary>
        public static string DelegateMaster(int operatorCid, int targetCid, out int newMasterCid)
        {
            newMasterCid = 0;
            lock (Sync)
            {
                EnsureLoaded();
                if (!CharacterGuild.TryGetValue(operatorCid, out var g) || g == null)
                    return "你不在工会中";
                var opIdx = g.Members.FindIndex(x => x.CharacterId == operatorCid);
                if (opIdx < 0 || g.Members[opIdx].Grade != GradeMaster)
                    return "只有会长才能委任";
                var tgIdx = g.Members.FindIndex(x => x.CharacterId == targetCid);
                if (tgIdx < 0)
                    return "目标不在本工会";
                if (targetCid == operatorCid)
                    return "不能委任给自己";
                var target = g.Members[tgIdx];
                g.Members[tgIdx] = (target.CharacterId, target.CharacterName, GradeMaster);
                g.Members[opIdx] = (operatorCid, g.Members[opIdx].CharacterName, GradeNormal);
                g.MasterName = target.CharacterName;
                try
                {
                    Repository.UpdateMemberGrade(g.GuildId, target.CharacterId, GradeMaster);
                    Repository.UpdateMemberGrade(g.GuildId, operatorCid, GradeNormal);
                    Repository.UpdateGuildMasterName(g.GuildId, target.CharacterName);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[Guild] 委任落库失败(内存已生效): {ex}");
                }
                newMasterCid = target.CharacterId;
                WriteLog(
                    g.GuildId, GuildLogType.Delegate,
                    operatorCid, g.Members[opIdx].CharacterName,
                    targetCid, target.CharacterName,
                    "委任会长(原会长降为普通会员)");
                FileLogger.Log(
                    $"[Guild] 委任会长 guild={g.GuildId} \"{target.CharacterName}\"(cid={targetCid}) " +
                    $"原会长 cid={operatorCid}→普通会员");
                return null;
            }
        }

        /// <summary>解散工会(0x012F)。仅会长。返回 null=成功(out 已解散工会)。</summary>
        public static string BreakGuild(int operatorCid, out GuildInfo guild)
        {
            guild = null;
            lock (Sync)
            {
                EnsureLoaded();
                if (!CharacterGuild.TryGetValue(operatorCid, out var g) || g == null)
                    return "你不在工会中";
                var m = g.Members.FirstOrDefault(x => x.CharacterId == operatorCid);
                if (m.CharacterId != operatorCid || m.Grade != GradeMaster)
                    return "只有会长才能解散工会";
                DissolveGuildLocked(g, operatorCid, null, "会长主动解散");
                guild = g;
                FileLogger.Log($"[Guild] 解散工会 guild={g.GuildId} name=\"{g.Name}\" by cid={operatorCid}");
                return null;
            }
        }

        // 解散公共路径: 写解散日志 + 清内存索引 + 删库(会/成员/内容/职位配置) + 清配置缓存。
        // ★ guild_log 不随会删除(审计需要: 解散后仍能追溯该会历史)。
        private static void DissolveGuildLocked(
            GuildInfo g, int actorCid = 0, string actorName = null, string reason = null)
        {
            WriteLog(
                g.GuildId, GuildLogType.Dismiss,
                actorCid, actorName ?? MemberName(g, actorCid), 0, string.Empty,
                reason ?? "工会解散");
            foreach (var m in g.Members.ToList())
                CharacterGuild.Remove(m.CharacterId);
            GuildsById.Remove(g.GuildId);
            GuildsByName.Remove(g.Name);
            _gradeConfigCache.Remove(g.GuildId);
            try
            {
                Repository.DeleteGuild(g.GuildId);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Guild] 解散落库失败(内存已清): {ex}");
            }
        }

        // ══════════ 加入申请(0x015C/0x0160, 2026-08-31) ══════════

        /// <summary>一条待处理的入会申请。</summary>
        public const int ApplicationPending = 0;
        public const int ApplicationApproved = 1;
        public const int ApplicationRejected = 2;
        public const int ApplicationCancelled = 3;

        public sealed class JoinApplication
        {
            public int GuildId;
            public int CharacterId;
            public string CharacterName = "";
            public string Message = "";
            public DateTime Time = DateTime.Now;
        }

        private static readonly Dictionary<(int GuildId, int CharacterId), JoinApplication> Applications =
            new Dictionary<(int, int), JoinApplication>();

        /// <summary>提交入会申请的结果(2026-09-07 参考包权威: 一角色一单待审)。</summary>
        public enum AddApplicationResult
        {
            Added,              // 新申请已记录
            AlreadyPendingSame, // 同公会申请已在待审(不重写留言/时间)
            PendingElsewhere,   // 已有其他公会的待审申请
            Failed,             // 一般失败(公会不存在/已入会/参数无效)
        }

        public static bool AddApplication(int guildId, int cid, string name, string message)
            => AddApplicationEx(guildId, cid, name, message) == AddApplicationResult.Added;

        /// <summary>
        /// 记录入会申请(2026-09-07 参考包权威 SubmitApplication):
        /// 一角色同时只允许【一单待审】; 同公会重复提交幂等成功(不改写留言/时间),
        /// 投向他公会须先取消原申请。
        /// </summary>
        public static AddApplicationResult AddApplicationEx(int guildId, int cid, string name, string message)
        {
            if (guildId <= 0 || cid <= 0 || string.IsNullOrEmpty(name))
                return AddApplicationResult.Failed;
            lock (Sync)
            {
                EnsureLoaded();
                if (!GuildsById.ContainsKey(guildId) || CharacterGuild.ContainsKey(cid))
                    return AddApplicationResult.Failed;
                foreach (var a in Applications.Values)
                {
                    if (a.CharacterId != cid)
                        continue;
                    // 已入会角色的陈旧申请视为无效(兜底, 正常入会时已撤销)
                    if (CharacterGuild.ContainsKey(cid))
                        continue;
                    return a.GuildId == guildId
                        ? AddApplicationResult.AlreadyPendingSame
                        : AddApplicationResult.PendingElsewhere;
                }
                var app = new JoinApplication
                {
                    GuildId = guildId,
                    CharacterId = cid,
                    CharacterName = name,
                    Message = message ?? "",
                    Time = DateTime.Now,
                };
                Repository.UpsertApplication(guildId, cid, name, app.Message);
                Applications[(guildId, cid)] = app;
                return AddApplicationResult.Added;
            }
        }

        public static List<JoinApplication> GetApplications(int guildId)
        {
            lock (Sync)
            {
                EnsureLoaded();
                // 2026-09-04: 清掉"申请人已在他会"的陈旧申请(历史脏数据兜底; 正常路径已在
                // 入会时撤销)。先物化 cid 列表再逐个撤销，避免遍历中修改集合。
                var staleCids = Applications.Values
                    .Where(a => a.GuildId == guildId && CharacterGuild.ContainsKey(a.CharacterId))
                    .Select(a => a.CharacterId)
                    .Distinct()
                    .ToList();
                foreach (var staleCid in staleCids)
                    WithdrawAllApplications(staleCid);

                return Applications.Values
                    .Where(a => a.GuildId == guildId && !CharacterGuild.ContainsKey(a.CharacterId))
                    .OrderBy(a => a.Time)
                    .ToList();
            }
        }

        public static JoinApplication GetApplication(int guildId, int cid)
        {
            lock (Sync)
            {
                EnsureLoaded();
                Applications.TryGetValue((guildId, cid), out var app);
                return app;
            }
        }

        /// <summary>
        /// 2026-09-04: 查某角色投出的所有待审申请(0x016D"我的入会申请"状态回显用)。
        /// 已入会角色的申请视为无效(入会时已撤销, 此处兜底过滤)。
        /// 返回 (公会id, 公会名) 按申请时间升序。
        /// </summary>
        public static List<(int GuildId, string GuildName)> GetPendingApplicationsByCid(int cid)
        {
            lock (Sync)
            {
                EnsureLoaded();
                if (cid <= 0 || CharacterGuild.ContainsKey(cid))
                    return new List<(int, string)>();
                return Applications.Values
                    .Where(a => a.CharacterId == cid)
                    .OrderBy(a => a.Time)
                    .Select(a => (a.GuildId,
                        GuildsById.TryGetValue(a.GuildId, out var g) ? g.Name : ""))
                    .ToList();
            }
        }

        /// <summary>取出并移除某角色对某工会的申请。仅兼容旧调用，新审批应使用 ProcessApplication。</summary>
        public static JoinApplication TakeApplication(int guildId, int cid)
        {
            lock (Sync)
            {
                EnsureLoaded();
                if (!Applications.TryGetValue((guildId, cid), out var app))
                    return null;
                if (!Repository.SetApplicationStatus(guildId, cid, ApplicationCancelled, 0))
                    return null;
                Applications.Remove((guildId, cid));
                return app;
            }
        }

        /// <summary>
        /// 2026-09-04: 撤销某角色投给【所有工会】的待审申请。角色入会(批准/接受邀请/建会)后
        /// 必须调用，否则他会永久停留在别家工会的申请列表里；而批准他时 ProcessApplication
        /// 会因 CharacterGuild 命中而失败，会长端只看到"点了没反应"且无错误提示。
        /// 只动 status=0 的记录(标记为已取消保留审计)。
        /// 返回撤销条数。调用方需已持有 Sync 或未持锁(内部自行加锁，Monitor 可重入)。
        /// </summary>
        public static int WithdrawAllApplications(int cid)
        {
            if (cid <= 0)
                return 0;
            lock (Sync)
            {
                EnsureLoaded();
                var keys = Applications.Values
                    .Where(a => a.CharacterId == cid)
                    .Select(a => (a.GuildId, a.CharacterId))
                    .ToList();
                if (keys.Count == 0)
                    return 0;
                try
                {
                    Repository.WithdrawAllApplications(cid);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[Guild] 撤销待审申请落库失败(内存已生效): {ex}");
                }
                foreach (var key in keys)
                    Applications.Remove(key);
                FileLogger.Log(
                    $"[Guild] 撤销待审申请 cid={cid} 条数={keys.Count} " +
                    $"工会=[{string.Join(",", keys.Select(k => k.GuildId))}]");
                return keys.Count;
            }
        }

        public static bool ProcessApplication(
            int guildId, int cid, int operatorCid, bool accept, out JoinApplication application)
        {
            application = null;
            lock (Sync)
            {
                EnsureLoaded();
                if (!Applications.TryGetValue((guildId, cid), out var app))
                    return false;
                if (!GuildsById.TryGetValue(guildId, out var guild) ||
                    !guild.Members.Any(m => m.CharacterId == operatorCid && m.Grade <= GradeViceMaster))
                    return false;
                if (accept && (CharacterGuild.ContainsKey(cid) || guild.Members.Count >= guild.MemberLimit))
                    return false;
                var status = accept ? ApplicationApproved : ApplicationRejected;
                if (!Repository.CompleteApplication(
                    guildId, cid, app.CharacterName, GradeNewbie, status, operatorCid))
                    return false;
                if (accept)
                {
                    guild.Members.Add((cid, app.CharacterName, GradeNewbie));
                    CharacterGuild[cid] = guild;
                    // 入会即撤销其投给其他工会的待审申请(防永久挂在其他工会列表且批准必失败)
                    WithdrawAllApplications(cid);
                }
                Applications.Remove((guildId, cid));
                application = app;
                return true;
            }
        }

        /// <summary>
        /// 添加成员(0x015E 批准 / 0x0098 接受邀请时调用)。
        /// 已在工会/工会不存在返回 false。
        /// </summary>
        public static bool AddMember(int guildId, int cid, string name, int grade)
        {
            if (cid <= 0 || string.IsNullOrEmpty(name))
                return false;
            lock (Sync)
            {
                EnsureLoaded();
                if (!GuildsById.TryGetValue(guildId, out var guild))
                    return false;
                if (CharacterGuild.ContainsKey(cid))
                    return false;
                if (guild.Members.Any(m => m.CharacterId == cid))
                    return false;

                grade = grade >= GradeMaster && grade <= GradeNewbie ? grade : GradeNewbie;
                guild.Members.Add((cid, name, grade));
                CharacterGuild[cid] = guild;
                // 入会即撤销其投给其他工会的待审申请(接受邀请/建会等路径统一在此清理)
                WithdrawAllApplications(cid);
                try
                {
                    Repository.InsertMember(guildId, cid, name, grade);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[Guild] 成员落库失败(内存已生效): {ex}");
                }
                WriteLog(
                    guildId, GuildLogType.Join,
                    cid, name, 0, string.Empty,
                    $"入会 grade={grade} total={guild.Members.Count}/{guild.MemberLimit}");
                FileLogger.Log(
                    $"[Guild] 成员入会 guild=\"{guild.Name}\"(id={guildId}) " +
                    $"member={name}(cid={cid}) grade={grade} total={guild.Members.Count}");
                return true;
            }
        }

        /// <summary>按名字搜索(0x02E8, 前缀/包含匹配)。最多 maxCount 条。</summary>
        public static List<GuildInfo> SearchGuilds(string keyword, int maxCount)
        {
            lock (Sync)
            {
                EnsureLoaded();
                IEnumerable<GuildInfo> seq = GuildsById.Values;
                if (!string.IsNullOrEmpty(keyword))
                {
                    seq = seq.Where(g =>
                        g.Name != null &&
                        g.Name.IndexOf(keyword, StringComparison.Ordinal) >= 0);
                }
                return seq
                    .OrderBy(g => g.GuildId)
                    .Take(maxCount)
                    .ToList();
            }
        }

        /// <summary>推荐列表(0x02F9): 前 maxCount 个(按创建序)。</summary>
        public static List<GuildInfo> RecommendGuilds(int maxCount)
        {
            lock (Sync)
            {
                EnsureLoaded();
                return GuildsById.Values
                    .OrderBy(g => g.GuildId)
                    .Take(maxCount)
                    .ToList();
            }
        }
    }
}
