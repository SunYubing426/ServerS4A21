using DfoServer.Game.Guilds;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    // 工会(公会)协议 — Phase 1(协议探测) + Phase 2(数据库落库)。
    //
    // 客户端逆向结论(2026-08-30, dnf_hang.dmp / dnf_live*.dmp 两张 NOTI 订阅表 + handler 反汇编):
    //   table1(0x3A5BE68, cmd=1 应答族) / table2(0x3A5BE74, cmd=0 推送族)。
    //
    // ===== 创建流程实测进度(2026-08-30) =====
    //   0x009C 名称检查 → ack cmd=1 [1][2][名字][名字] ✅
    //   0x02E2 宣传语检查 → ack cmd=1 [1][2][文本][文本] ✅
    //   0x02E4 创建许可 → NOTI 0x02AE [1] ✅ ("认证完成")
    //   0x0044 确认创建 → NOTI 0x0044 [1][count][cid] + NOTI 0x0242(13B 窗口状态) ✅
    //      Phase 2: 此处真正建会(guilds/guild_members 落库, 迁移 v16)
    //   0x02E7 我的工会查询 → ack cmd=1 [count][dstr工会名][dstr角色名]
    //   0x02E8 搜索 / 0x02F9 推荐 → ack [count]×{[id][dstr名][dstr宣传语]}
    //   CMD 0x016D 加入信息 → NOTI 0x0134 JOIN_GUILD_INFO [u32 key][u8 count][dstr 名称...]
    //
    // ===== 暂缓(会崩溃) =====
    //   NOTI 0x0046 GUILD_INFO 详情推送: handler(0x011959B0) 含条件分支读取
    //   (0x1195E91 分支的 u8 受全局 0x3a3a6a8 标志控制), 静态复刻两轮均闪退。
    //   改推 NOTI 0x0047(创建成功通知, [dstr 工会名])。
    internal static class GuildHandler
    {
        private const string ProtocolName = "GameProtocol";
        internal const ushort JoinGuildInfoNotificationType =
            (ushort)NotiPacketTypeA21.JOIN_GUILD_INFO;

        // 0x02AE 结果码(客户端 handler 0x0118AE20 分派值)。
        private const byte PermitGranted = 1;
        private const byte PermitDenied = 0;
        private const byte PermitDeniedAlt = 8;
        private const byte NameDuplicated = 0x2D;

        // 认证通过待入会的角色(uid → (cid, 名)), 创建成功时作为创始成员入会
        private static readonly System.Collections.Concurrent.ConcurrentDictionary
            <int, (int Cid, string Name)> PendingPermits =
            new System.Collections.Concurrent.ConcurrentDictionary<int, (int, string)>();

        // 在线状态查询(签到在线标记/在线成员数用), GameProtocolHandler 注册时绑定
        internal static Game.Session.ISessionDirectory Sessions;
        internal static void BindSessions(Game.Session.ISessionDirectory sessions)
        {
            Sessions = sessions;
            // ★ 成员下线 → 广播 0x0043 刷新(SessionEnding 触发时该 cid 已从字典移除,
            //   故 IsMemberOnline 返回 false, 其他成员列表会正确显示其离线)。
            //   防多次 BindSessions 重复订阅。
            if (sessions != null && !_presenceHooked)
            {
                _presenceHooked = true;
                sessions.SessionEnding += async (characterId, endingSession) =>
                {
                    try { await BroadcastMemberListRefreshAsync(characterId); }
                    catch (Exception ex)
                    {
                        FileLogger.Log(
                            $"[{ProtocolName}] GUILD presence-broadcast(disconnect) " +
                            $"cid={characterId} failed: {ex.Message}");
                    }
                };
            }
        }
        private static bool _presenceHooked;

        private static bool IsMemberOnline(int memberCid)
            => Sessions != null && Sessions.TryGet(memberCid, out _);

        // 成员行频道号: 在线(含他人会话)→真实频道号, 离线→0xFF
        private static byte ResolveMemberChannel(int memberCid, int selfCid, int selfChannel)
        {
            if (memberCid == selfCid)
                return (byte)selfChannel;
            if (Sessions != null && Sessions.TryGet(memberCid, out var other))
                return (byte)GameNetworkConfig.ResolveGameChannel(
                    other.ListenerPort).ChannelId;
            return 0xFF;
        }

        // ===== Phase 2: 创建流程挂起状态(0x009C/0x02E2 积累, 0x0044 一起落库) =====
        // ★ 2026-09-07 参考包权威(GuildCreationDraft): 草稿绑定【角色+账号+背包会话代次
        //   +5 分钟过期】; 0x02E2 无有效草稿即丢弃; 开窗/取消清除; 创建与扣款同一事务。
        private sealed class CreateDraft
        {
            public string Name;
            public string Memo = string.Empty;
            public int AccountId;
            public long LeaseVersion;
            public DateTime CreatedUtc;
            public bool Ready => !string.IsNullOrEmpty(Name) && !string.IsNullOrEmpty(Memo);
            public bool Matches(int accountId, long leaseVersion, DateTime nowUtc)
                => AccountId == accountId && LeaseVersion == leaseVersion
                    && nowUtc < CreatedUtc.AddMinutes(5);
        }
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, CreateDraft>
            PendingCreations = new System.Collections.Concurrent.ConcurrentDictionary<int, CreateDraft>();

        /// <summary>取与当前会话/背包代次匹配且未过期的建会草稿; 不匹配则清除并返回 null。</summary>
        private static CreateDraft GetValidDraft(EnhancedClientSession session, int cid)
        {
            if (cid == 0 || !PendingCreations.TryGetValue(cid, out var draft))
                return null;
            var aid = session?.Player?.UserId ?? 0;
            long version = 0;
            var leaseOk = Game.Inventory.InventoryContext.TryGetOwnedLease(
                session?.SessionId ?? Guid.Empty, cid, out var lease);
            if (leaseOk)
                version = lease.Version;
            if (!leaseOk || aid <= 0 || !draft.Matches(aid, version, DateTime.UtcNow))
            {
                PendingCreations.TryRemove(cid, out _);
                return null;
            }
            return draft;
        }

        // ★ 改名草稿(2026-09-07 参考包权威): 0x009C 重名检查时若已在公会 →
        // 存草稿(5 分钟有效); 0x02F8 content_id=22 购买改名时消费。
        //   key=cid, value=(新名字, 草稿写入时间 UTC)。
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (string Name, DateTime DraftUtc)>
            PendingRenames = new System.Collections.Concurrent.ConcurrentDictionary<int, (string, DateTime)>();

        // 0x0097 邀请已发出待答复: 被邀请者 cid → (工会 id, 邀请者 cid)。
        // 0x0098(被邀请者答复)到达时据此定位目标工会。
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (int GuildId, int InviterCid)>
            PendingInvites = new System.Collections.Concurrent.ConcurrentDictionary<int, (int, int)>();

        // CMD 0x009C CHECK_GUILD_NAME_DOUBLE: body = [nameLen:u32][UTF-8 名字]。
        // 客户端发送按钮回调注册在 0x6fd760, 应答需 ≥2 个字符串元素启用创建窗口控件。
        // 固定应答 = cmd=1 [1][2][名字][名字](两轮真机验证)。
        // Phase 2: 记录挂起名称 + 查重。
        public static async Task Handle_CHECK_GUILD_NAME_DOUBLE(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var name = ParseNameBody(body, out var failure);
            FileLogger.Log(
                $"[{ProtocolName}] GUILD CHECK_NAME_DOUBLE name=\"{name ?? "?"}\" " +
                $"raw={FormatBody(body)} parseFailure={failure ?? "none"}");
            if (session == null)
                return;

            if (!string.IsNullOrEmpty(name))
            {
                var cid = session.Player?.CharacterId ?? 0;
                var taken = GuildSystem.IsNameTaken(name);
                // ★ 改名路径(2026-09-07 参考包权威): 已在公会 → 存改名草稿(需 BuyContent
                //   权限, 5 分钟有效), 不再进建会挂起; 0x02F8 content_id=22 消费。
                if (cid != 0 && GuildSystem.GetGuildOfCharacter(cid) != null)
                {
                    PendingCreations.TryRemove(cid, out _);
                    if (!taken && GuildSystem.HasPermission(cid, GuildPermissions.BuyContent))
                    {
                        PendingRenames[cid] = (name, DateTime.UtcNow);
                        FileLogger.Log(
                            $"[{ProtocolName}] GUILD rename-draft cid={cid} name=\"{name}\"");
                    }
                    else
                    {
                        PendingRenames.TryRemove(cid, out _);
                        FileLogger.Log(
                            $"[{ProtocolName}] GUILD rename-draft 拒绝 cid={cid} " +
                            $"taken={taken} perm={GuildSystem.HasPermission(cid, GuildPermissions.BuyContent)}");
                    }
                }
                else
                {
                    PendingRenames.TryRemove(cid, out _);
                    // ★ 建会草稿(参考包权威): 绑定角色+账号+当前背包代次, 5 分钟有效。
                    var aid = session.Player?.UserId ?? 0;
                    long version = 0;
                    var leaseOk = Game.Inventory.InventoryContext.TryGetOwnedLease(
                        session.SessionId, cid, out var draftLease);
                    if (leaseOk)
                        version = draftLease.Version;
                    if (leaseOk && aid > 0)
                    {
                        PendingCreations[cid] = new CreateDraft
                        {
                            Name = name,
                            Memo = string.Empty,
                            AccountId = aid,
                            LeaseVersion = version,
                            CreatedUtc = DateTime.UtcNow,
                        };
                    }
                    else
                    {
                        PendingCreations.TryRemove(cid, out _);
                        FileLogger.Log(
                            $"[{ProtocolName}] GUILD 建会草稿创建失败(无有效背包会话): cid={cid}");
                    }
                }
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD name-check pending cid={cid} name=\"{name}\" " +
                    $"taken={taken}");
            }
            await SendStringCheckSuccessAsync(session, 0x009C, body);
        }

        // CMD 0x02E2 CHECK_GUILD_CREATE_PROMOTE_MSG: body = [msgLen:u32][UTF-8 文本]。
        // 应答 cmd=1 [1][2][文本][文本]。Phase 2: 记录挂起宣传语。
        public static async Task Handle_CHECK_GUILD_CREATE_PROMOTE_MSG(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var text = ParseNameBody(body, out var failure);
            FileLogger.Log(
                $"[{ProtocolName}] GUILD CHECK_PROMOTE_MSG text=\"{text ?? "?"}\" " +
                $"raw={FormatBody(body)} parseFailure={failure ?? "none"}");
            if (session == null)
                return;

            if (!string.IsNullOrEmpty(text))
            {
                var cid = session.Player?.CharacterId ?? 0;
                // ★ 参考包权威: 宣传语必须落在【有效建会草稿】上; 无草稿/过期/代次不符 →
                //   丢弃草稿(后续 0x0044 会因草稿缺失拒绝创建, 防脱离会话上下文建会)。
                var draft = GetValidDraft(session, cid);
                if (draft != null)
                {
                    draft.Memo = text;
                    FileLogger.Log(
                        $"[{ProtocolName}] GUILD promote-msg draft cid={cid} text=\"{text}\"");
                }
                else
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] GUILD promote-msg 无有效建会草稿, 已丢弃: cid={cid} text=\"{text}\"");
                }
            }
            await SendStringCheckSuccessAsync(session, 0x02E2, body);
        }

        // CMD 0x02E3 MODIFY_GUILD_PROMOTE_MSG: 修改宣传语(应答同 0x02E2)。
        // 权威源码对齐(2026-09-06 他人 MD): 权限 bit5(EditPromo) + 落库 notice +
        // 重推 0x0046(宣传语 #8); 不接受空白, ≤40 UTF-16 单元 / ≤255 字节。
        // body: [len:u32][UTF-8 宣传语](同 0x02E2; 兼容 [u8 0] 前缀)。
        public static async Task Handle_MODIFY_GUILD_PROMOTE_MSG(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var text = ParseNameBodyFlexible(body, out var failure);
            FileLogger.Log(
                $"[{ProtocolName}] GUILD MODIFY_PROMOTE_MSG text=\"{text ?? "?"}\" " +
                $"raw={FormatBody(body)} parseFailure={failure ?? "none"}");
            if (session == null)
                return;
            var cid = session.Player?.CharacterId ?? 0;
            var valid = text != null
                && !string.IsNullOrWhiteSpace(text)
                && text.Length <= GuildSystem.MaxPromoChars
                && Encoding.UTF8.GetByteCount(text) <= GuildSystem.MaxGuildTextWireBytes
                && IsSafeText(text);
            if (valid && GuildSystem.HasPermission(cid, GuildPermissions.EditPromo)
                && GuildSystem.UpdateMemo(cid, text, out var guildId))
            {
                var guild = GuildSystem.GetGuildOfCharacter(cid);
                if (guild != null)
                    await SendGuildInfoAsync(session, guild);
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD MODIFY_PROMOTE_MSG 保存成功 " +
                    $"guild={guildId} text=\"{text}\" by cid={cid}");
            }
            else
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD MODIFY_PROMOTE_MSG 拒绝: cid={cid} " +
                    $"textNull={text == null} (空白/超限/无 bit5 权限)");
            }
            await SendStringCheckSuccessAsync(session, 0x02E3, body);
        }

        // 名称/宣传语检查应答(已验证形态): cmd=1 [1][2][文本][文本]。
        private static async Task SendStringCheckSuccessAsync(
            EnhancedClientSession session,
            ushort ackType,
            byte[] originalBody)
        {
            var textBytes = originalBody != null && originalBody.Length > 4
                ? originalBody
                : new byte[] { 0, 0, 0, 0 };
            var payload = Join(
                new byte[] { 1, 2 },
                textBytes,
                textBytes);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                1,
                ackType,
                payload));
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → ack 0x{ackType:X4} [1][2][text][text] " +
                $"len={payload.Length} uid={(session?.Player?.UserId).GetValueOrDefault()}");
        }

        private static byte[] Join(params byte[][] parts)
        {
            var total = 0;
            foreach (var p in parts)
                total += p.Length;
            var result = new byte[total];
            var offset = 0;
            foreach (var p in parts)
            {
                Buffer.BlockCopy(p, 0, result, offset, p.Length);
                offset += p.Length;
            }
            return result;
        }

        // NOTI 0x0242(13 字节创建窗口状态) 常量:
        //   b1 → 0x1F4 启用; b2 → [obj+0x1dc], ★必须=3(0x28d6dc0 读回比 3,
        //   ==3 时设 [会话+0x3be]=1 按钮启用标志); s0..s5 槽位; b9 存值; t0..t3 刷新。
        private const byte CreateWindowFlag = 1;
        private const byte CreateWindowValue2 = 3;
        private const byte CreateWindowSlotValue = 1;
        private const byte CreateWindowValue9 = 1;
        private const byte CreateWindowTailValue = 1;

        // CMD 0x0044 CALL_GUILD_CREATE_RIGHT (body=[01]): 确认创建。
        // 应答 NOTI 0x0044 [flag=1][count=1][cid] + NOTI 0x0242(13B)。
        // Phase 2: 用挂起的名称+宣传语真正建会, 会长=当前角色, 落库。
        public static async Task Handle_CALL_GUILD_CREATE_RIGHT(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            FileLogger.Log(
                $"[{ProtocolName}] GUILD CALL_CREATE_RIGHT cid={cid} " +
                $"raw={FormatBody(body)}");
            if (session == null)
                return;

            // ===== Phase 2: 真正创建(2026-09-07 参考包权威: 有效草稿 + 创建扣款同一事务) =====
            GuildSystem.GuildInfo created = null;
            var foundingCid = 0;
            // ★ 草稿必须: 绑定本角色+本账号+当前背包代次、5 分钟内、名称+宣传语齐备。
            var draft = GetValidDraft(session, cid);
            var masterName = "guildmaster";
            try
            {
                var nameBytes = session.Player?.Name;
                if (nameBytes != null && nameBytes.Length > 0)
                    masterName = ClientTextEncoding.GetString(nameBytes);
            }
            catch
            {
                // 保留回退名
            }

            string createFailReason = null;
            if (draft != null && draft.Ready
                && !string.IsNullOrEmpty(masterName)
                && GuildSystem.GetGuildOfCharacter(cid) == null)
            {
                if (!Game.Inventory.InventoryContext.TryGetOwnedLease(
                        session.SessionId, cid, out var createLease))
                {
                    createFailReason = "角色状态已变化，请重新打开公会面板。";
                    FileLogger.Log(
                        $"[{ProtocolName}] GUILD create aborted: cid={cid} 无有效背包会话");
                }
                else
                {
                    // ★ 认证角色作为创始成员一并入会(2016-03-24 改版语义, 本地保留特性)
                    var foundingName = (string)null;
                    var uid = session.Player?.UserId ?? 0;
                    if (PendingPermits.TryRemove(uid, out var permit))
                    {
                        foundingCid = permit.Cid;
                        foundingName = permit.Name;
                    }
                    created = GuildSystem.CreateCommitted(
                        createLease, draft.Name, draft.Memo, cid, masterName,
                        foundingCid, foundingName, out var createError);
                    if (created != null)
                    {
                        PendingCreations.TryRemove(cid, out _);
                    }
                    else
                    {
                        createFailReason = createError switch
                        {
                            GuildSystem.GuildCreateError.NameTaken => "该公会名称已被使用，请更换名称。",
                            GuildSystem.GuildCreateError.AlreadyMember => "你已加入公会，不能重复创建。",
                            GuildSystem.GuildCreateError.InsufficientGold =>
                                $"金币不足（创建公会需要 {GuildSystem.CreateCostGold} 金币）。",
                            _ => "公会创建保存失败，请稍后重试。",
                        };
                        FileLogger.Log(
                            $"[{ProtocolName}] GUILD create failed: cid={cid} error={createError}");
                    }
                }
            }
            else if (GuildSystem.GetGuildOfCharacter(cid) != null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD create skipped: cid={cid} 已在工会中");
            }
            else
            {
                createFailReason = "公会创建信息已过期，请重新输入公会名称与宣传语。";
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD create skipped: 草稿缺失/过期/未备齐 " +
                    $"draft={(draft == null ? "null" : $"name=\"{draft.Name}\" memo=\"{draft.Memo}\"")} " +
                    $"master=\"{masterName}\"");
            }

            var w = new GamePacketWriter();
            w.WriteByte(1);                 // flag: 有权限
            w.WriteUInt16(1);               // count
            w.WriteInt32(cid);              // 元素: 当前角色
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0044,
                w.ToArray()));
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → NOTI 0x0044 create-right cid={cid} " +
                $"uid={(session?.Player?.UserId).GetValueOrDefault()} " +
                $"created={(created != null ? $"id={created.GuildId} \"{created.Name}\"" : "no")}");

            // NOTI 0x0242: 创建窗口状态(13 字节)
            var w2 = new GamePacketWriter();
            w2.WriteByte(CreateWindowFlag);
            w2.WriteByte(CreateWindowValue2);
            for (var i = 0; i < 6; i++)
                w2.WriteByte(CreateWindowSlotValue);
            w2.WriteByte(CreateWindowValue9);
            for (var i = 0; i < 4; i++)
                w2.WriteByte(CreateWindowTailValue);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0242,
                w2.ToArray()));
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → NOTI 0x0242 window-state(13B) " +
                $"[b1={CreateWindowFlag} b2={CreateWindowValue2} slots=6×{CreateWindowSlotValue} " +
                $"b9={CreateWindowValue9} tail=4×{CreateWindowTailValue}] " +
                $"uid={(session?.Player?.UserId).GetValueOrDefault()}");

            // 创建失败 → 系统公告说明原因(此前静默, 玩家只见"没反应")
            if (created == null && createFailReason != null)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE,
                    ServerNoticeMessageBuilder.Build(createFailReason)));
            }

            // 创建成功 → NOTI 0x0047(客户端弹"创建成功"并刷新状态)
            if (created != null)
            {
                await SendGuildCreatedAsync(session, created);
                // ★ 2016-04 改版语义: 公会基地随公会创建自动开启, 向全会在线成员
                //   广播基地状态(仅 0x00BF, 不弹"已生成公会基地"公告)。
                await BroadcastAgitCreatedAsync(created);
            }

            // ★ 认证成员创建时在线 → 同步推送"已入会"状态(0x0047 置 my-guild-id
            //   + 0x0046 成员视图数据, 同选角恢复路径)。2026-09-03 实测: 不推则
            //   认证成员客户端停在"您尚未加入任何公会"搜索视图, 只能收到公告文本。
            if (created != null && foundingCid > 0 && foundingCid != cid
                && Sessions != null && Sessions.TryGet(foundingCid, out var foundingSession)
                && foundingSession != null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD → push founding member cid={foundingCid} " +
                    $"id={created.GuildId} name=\"{created.Name}\"");
                await SendGuildCreatedAsync(foundingSession, created);
                await SendGuildInfoAsync(foundingSession, created);
            }
        }

        // NOTI 0x0047 GUILD_CREATE(cmd=0, handler 0x0118E350 反汇编确认):
        //   [u32 工会id][dstr 工会名]。
        //   u32 存入工会管理器(+0x2c8 区域), dstr 拼进消息(0x4e3="公会已创建")。
        //   ★ 旧格式 [dstr 名] 让 u32 读到名字长度、dstr 长度读到 UTF-8 首字节
        //     (0xE5889CE6 巨数) → 失败 → 客户端"[无公会名]公会已创建"。
        internal static async Task SendGuildCreatedAsync(
            EnhancedClientSession session,
            GuildSystem.GuildInfo guild)
        {
            var w = new GamePacketWriter();
            w.WriteInt32(guild.GuildId);
            w.WriteClientDstr(guild.Name);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0047,
                w.ToArray()));
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → NOTI 0x0047 guild-created " +
                $"id={guild.GuildId} name=\"{guild.Name}\" " +
                $"uid={(session?.Player?.UserId).GetValueOrDefault()}");
        }

        // 公会基地状态下行(2026-09-07 参考包权威模型):
        //   0x00BF cmd=0 (handler 0x0118F230): 读 1B, 创建/更新本会基地缓存与 UI。
        // ★ 0x009C/0x00C1/0x00BD 全部废弃(参考包从不下行):
        //   0x009C handler 0x0118AC80 upsert 后无条件经 0x23060f0+0x78c7f0 弹
        //     0x53EC"已生成公会基地"公告(每发必弹); 0x00C1 handler 0x0118A0B0 直接
        //     弹同一文案。官方正常流程不触发该消息, 全路径不再发送。
        // ★ 客户端会话状态(基地缓存)随选角/断线被清 → 建会广播之外,
        //   每次选角恢复必须重放 0x00BF(同 COUPLE_ROOM 每次选角回放)。
        private static async Task BroadcastAgitCreatedAsync(GuildSystem.GuildInfo guild)
        {
            if (guild == null || Sessions == null)
                return;

            var targets = 0;
            foreach (var m in guild.Members)
            {
                if (!Sessions.TryGet(m.CharacterId, out var target) || target == null)
                    continue;

                try
                {
                    await SendAgitStateAsync(target, guild);
                    targets++;
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] GUILD agit-created push to " +
                        $"cid={m.CharacterId} failed: {ex.Message}");
                }
            }
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → broadcast NOTI 0x00BF agit-created " +
                $"guild=\"{guild.Name}\" gid={guild.GuildId} targets={targets}");
        }

        // 向单个会话下发基地状态(仅 0x00BF), 不弹提示。
        // 调用点: 建会广播(BroadcastAgitCreatedAsync)、选角恢复(CharacterSelectHandler)、
        //   切区域补推(TownHandler)。
        // ★ 2026-09-07 参考包权威定案: 只发 0x00BF。参考实现(A21-公会修复源码包)
        //   证实 0x00BF 的 handler sub_118F230 自身即"creates/updates the own-guild
        //   agit cache", 0x0046 #10(sub_11959B0)维持赛丽亚房间入口;
        //   0x009C(upsert 后无条件弹公告)/0x00C1(直接弹)/0x00BD(参考包从不下行)
        //   全部不再发送 — 与参考包逐字节对齐, 消除一切"已生成公会基地"弹窗。
        internal static async Task SendAgitStateAsync(
            EnhancedClientSession session,
            GuildSystem.GuildInfo guild)
        {
            if (session == null || guild == null)
                return;

            // 0x00BF GUILD_AGIT_INFO [u8 flag=1] (handler 0x0118F230):
            //   创建/更新本会基地缓存 → obj+0x30 为空则 0x19f5140 创建 UI 元素。
            //   ★ UI 元素(obj+0x30)随切图/加载被客户端销毁, 必须进图后重推重建,
            //     否则赛丽亚房间基地门失效(2026-09-06 实测: 选角推的门不开,
            //     开工会界面触发 0x0046 #10 重建 UI 元素后门即开)。
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x00BF,
                new byte[] { 1 }));
        }

        // CMD 0x02E4 REQUEST_GUILD_CREATE_PERMIT: body=[len:u32][UTF-8 认证角色名]。
        // 应答 NOTI 0x02AE [1]=认证完成 / [0]=失败。
        // ★ 2026-08-31 修复: 校验角色名真实存在(此前任意名字都能过认证)。
        public static async Task Handle_REQUEST_GUILD_CREATE_PERMIT(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var permitName = ParseNameBody(body, out var failure);
            FileLogger.Log(
                $"[{ProtocolName}] GUILD REQUEST_CREATE_PERMIT name=\"{permitName ?? "?"}\" " +
                $"raw={FormatBody(body)} parseFailure={failure ?? "none"}");
            if (session == null)
                return;

            var cid = permitName == null
                ? -1
                : GuildSystem.FindCharacterIdByName(permitName);
            if (cid <= 0)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD 认证失败: 角色不存在或重名 \"{permitName}\" → permitResult=0");
                await SendPermitResultAsync(session, PermitDenied);
                return;
            }

            // 同一角色不能重复认证多个工会(一个认证角色只能辅助创建一个工会)
            if (GuildSystem.GetGuildOfCharacter(cid) != null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD 认证失败: \"{permitName}\"(cid={cid}) 已属于其他工会 → permitResult=0");
                await SendPermitResultAsync(session, PermitDenied);
                return;
            }

            // 2016 原版规则: 认证角色必须来自其他账号(不能认证自己同账号角色)。
            // 2026-09-03 抓包确认: 同账号角色被接受 → 在此拦截。
            var requesterUid = session.Player?.UserId ?? 0;
            if (requesterUid > 0 &&
                GuildSystem.GetAccountIdOfCharacter(cid) == requesterUid)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD 认证失败: \"{permitName}\"(cid={cid}) 与创建者同账号 → permitResult=0");
                await SendPermitResultAsync(session, PermitDenied);
                return;
            }

            // ★ 记录待用的认证角色(创建成功时作为创始成员入会)
            var permitUid = session.Player?.UserId ?? 0;
            PendingPermits[permitUid] = (cid, permitName);
            FileLogger.Log(
                $"[{ProtocolName}] GUILD 认证通过: \"{permitName}\"(cid={cid}) 待创建时入会");
            await SendPermitResultAsync(session, PermitGranted);
        }

        // CMD 0x009E OPEN_GUILD_CREATE_WINDOW: 打开创建窗口(本地 UI, 记账吞掉)。
        // ★ 2026-09-07 参考包权威: 开窗即清旧建会草稿(新流程从 0x009C 重建)。
        public static async Task Handle_OPEN_GUILD_CREATE_WINDOW(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            if (cid != 0)
                PendingCreations.TryRemove(cid, out _);
            await LogAndConsumeAsync("OPEN_CREATE_WINDOW", session, body);
        }

        // CMD 0x02E5 REPLY_GUILD_CREATE_PERMIT: 玩家对创建确认框的回答(记账)。
        public static Task Handle_REPLY_GUILD_CREATE_PERMIT(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
            => LogAndConsumeAsync("REPLY_CREATE_PERMIT", session, body);

        // CMD 0x02E6 CANCEL_GUILD_CREATE: 取消创建(记账)。
        // ★ 2026-09-07 参考包权威: 取消即清建会草稿。
        public static async Task Handle_CANCEL_GUILD_CREATE(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            if (cid != 0)
                PendingCreations.TryRemove(cid, out _);
            await LogAndConsumeAsync("CANCEL_CREATE", session, body);
        }

        // CMD 0x02E7 REQ_GUILD_INFO_OF_MY_CHARS: 开工会界面必发。
        // table1 handler 0x011322B0: cmd=1 应答 [count:u8] × {[dstr 工会名][dstr 角色名]}。
        // Phase 2: 返回角色真实所在工会; 有工会 → 追加 NOTI 0x0047 刷新客户端状态。
        public static async Task Handle_REQ_GUILD_INFO_OF_MY_CHARS(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            FileLogger.Log(
                $"[{ProtocolName}] GUILD INFO_OF_MY_CHARS cid={cid} " +
                $"raw={FormatBody(body)}");
            if (session == null)
                return;

            var guild = GuildSystem.GetGuildOfCharacter(cid);
            var charName = "character";
            try
            {
                var nameBytes = session.Player?.Name;
                if (nameBytes != null && nameBytes.Length > 0)
                    charName = ClientTextEncoding.GetString(nameBytes);
            }
            catch
            {
                // 保留回退名
            }

            var w = new GamePacketWriter();
            w.WriteByte(1);                     // cmd=1 框架标志(派发器先吃 1 字节)
            if (guild != null)
            {
                w.WriteByte(1);                 // count=1
                // ★ 字段序对齐参考包(2026-09-07): sub_11322B0 把第二个 dstr 当
                //   工会名(UI/搜索字段), 第一个是角色名。旧序[会名,角色名]与之相反。
                w.WriteClientDstr(charName);
                w.WriteClientDstr(guild.Name);
            }
            else
            {
                w.WriteByte(0);                 // count=0
            }
            var myCharsBody = w.ToArray();
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                1,
                0x02E7,
                myCharsBody));
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → ack 0x02E7 " +
                $"[count={(guild != null ? 1 : 0)} guild=\"{guild?.Name ?? ""}\" char=\"{charName}\"] " +
                $"uid={(session?.Player?.UserId).GetValueOrDefault()}");

            // ⚠️ cmd=0 0x02E7 同体推送已回滚: 客户端处理它会延迟崩溃(2026-08-31 08:41 实测,
            //    在无 0x0047 状态下 4 秒后闪退, 崩溃栈为通用 UI 代码)。格式待逆向。

            // ★ 0x0047 已移至选角流程(CharacterSelectHandler, 每次选角必发以恢复
            //   my-guild-id 状态); 此处不再重复发。
            //   ★ 0x0046 不在这里推! 0x0046 handler 调 0x19ef4a0(1) 切缓存模式, 会干扰
            //     随后 0x02F9(mode=2) 的推荐表填充; 已移至 0x02F9 应答之后发送。
        }

        // CMD 0x02F9 REQ_RECOMMEND_GUILD: 推荐工会列表(开界面必发)。
        // table1 handler 0x01133180: cmd=1 应答 [count:u8] × {[u32][dstr 名][dstr 宣传语]}。
        public static async Task Handle_REQ_RECOMMEND_GUILD(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            FileLogger.Log(
                $"[{ProtocolName}] GUILD RECOMMEND_GUILD uid={(session?.Player?.UserId).GetValueOrDefault()} " +
                $"raw={FormatBody(body)}");
            if (session == null)
                return;

            var guilds = GuildSystem.RecommendGuilds(8);
            var w = new GamePacketWriter();
            w.WriteByte(1);                     // cmd=1 框架标志(派发器先吃 1 字节)
            w.WriteByte((byte)guilds.Count);
            foreach (var g in guilds)
            {
                w.WriteInt32(g.GuildId);
                w.WriteClientDstr(g.Name);
                // ★ 推荐表列 = 公会名|会长: 第二个 dstr 是会长名(非宣传语, 旧认知有误)。
                w.WriteClientDstr(g.MasterName ?? string.Empty);
            }
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                1,
                0x02F9,
                w.ToArray()));
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → ack 0x02F9 [count={guilds.Count}] " +
                $"guilds=[{string.Join(",", guilds.Select(g => $"#{g.GuildId}\"{g.Name}\""))}] " +
                $"uid={(session?.Player?.UserId).GetValueOrDefault()}");

            // ★ cmd=0 0x02F9 同体推送: ⚠️ 已回滚 — table2 的 0x02F9 handler 格式未知,
            //   实测推 [count][u32][dstr][dstr] 会让客户端在工会模块(DNF+0x1101AE0 系)闪退
            //   (2026-08-31 07:23 双端抓包定位)。待从堆对象订阅链挖出真 handler 再开。

            // ★ 2026-09-07 参考包对齐: 应答后不再补推 0x0046(参考包只 Reply 推荐表;
            //   0x0046 handler 0x011959B0 调 0x19ef4a0(1) 切缓存模式, 有干扰风险)。
            //   成员视图的 0x0046 由 0x0043 路径(成员视图打开必经)与选角推送覆盖。
        }

        // CMD 0x0043 GUILD_MEMBER_LIST: 成员视图打开时客户端发送(空包体)。
        // 应答 cmd=1 type=0x0043(handler 0x1130B60, 2026-08-31 反汇编; 同函数服务 0x43/0x8C/0x8D):
        //   [u32 工会id]  ← ★必须与客户端 my-guild-id(0x0047/cmd=0 0x02E7 写入)一致, 否则整包被忽略!
        //   [u32 ?=0]     (this+0x28, 疑似公会金币)
        //   [u16 成员上限] (客户端"N/300"的 300 即此字段)
        //   [u16 成员数 N] × {
        //     [u32 cid][dstr 角色名≤0x1F][dstr 留言≤0x15][u16 等级]
        //     [u8 职业][u8×6][u32 0][u32 0][u8 0][dstr 称号≤0x1F] }
        //   [u8×6] 第3字节 = 频道号(0x1130B60 反汇编 record+0x38, 0xFF=无效 →
        //   客户端 "chXX 地名" 悬浮提示由此渲染; 全 1 时显示 ch01 即此字节)
        //   [u8 职业] = characters.job 原值(0=鬼剑士 12=守护者; 2026-08-31 实测定案 —
        //   误写 1/4 时他人行显示气功师/圣职者)
        public static async Task Handle_GUILD_MEMBER_LIST(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            FileLogger.Log(
                $"[{ProtocolName}] GUILD MEMBER_LIST cid={cid} raw={FormatBody(body)}");
            if (session == null)
                return;

            var guild = GuildSystem.GetGuildOfCharacter(cid);
            var w = new GamePacketWriter();
            WriteMemberListBody(w, session, guild, cid);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                1,
                0x0043,
                w.ToArray()));
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → ack 0x0043 member-list " +
                $"id={guild?.GuildId ?? 0} name=\"{guild?.Name ?? ""}\" members={guild?.Members.Count ?? 0} " +
                $"uid={(session?.Player?.UserId).GetValueOrDefault()}");
        }

        // ★ 成员在线状态变化(上线/下线) → 向工会其他在线成员推 NOTI 0x0040
        //   GUILD_MEMBER_LOGIN_OUT(2026-09-04 GLM 定案, 替代 0x0043 全量广播)。
        //   包体: [u8 onlineFlag][u8 频道][dstr 角色名] → handler 0x0118E200:
        //   flag!=0 → 0x19ED150 置在线+频道; ==0 → 0x19ED1E0 置离线;
        //   再 0x19F1CA0(name,online) 刷成员列表 + 聊天行(msg 0x29E/0x29F 上/下线提示,
        //   属原版行为)。cid=状态变化成员; 下线时 SessionEnding 触发, 其会话已移除。
        public static async Task BroadcastMemberListRefreshAsync(int changedCid)
        {
            if (changedCid <= 0 || Sessions == null)
                return;
            var guild = GuildSystem.GetGuildOfCharacter(changedCid);
            if (guild == null || guild.Members.Count == 0)
                return;
            var changed = guild.Members.FirstOrDefault(x => x.CharacterId == changedCid);
            if (changed.CharacterId != changedCid)
                return;
            // 变化者在线状态与频道(上线=已注册取真实频道; 下线=已注销 → flag=0)
            var online = Sessions.TryGet(changedCid, out var changedSession) && changedSession != null;
            var channel = online
                ? (byte)GameNetworkConfig.ResolveGameChannel(changedSession.ListenerPort).ChannelId
                : (byte)0xFF;
            foreach (var m in guild.Members)
            {
                if (m.CharacterId == changedCid)
                    continue; // 变化者自己(登录态本端已知)
                if (!Sessions.TryGet(m.CharacterId, out var target) || target == null)
                    continue;
                try
                {
                    var w = new GamePacketWriter();
                    w.WriteByte(online ? (byte)1 : (byte)0);        // onlineFlag
                    w.WriteByte(channel);                           // 频道
                    w.WriteClientDstr(changed.CharacterName ?? string.Empty);
                    await target.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x0040,
                        w.ToArray()));
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] GUILD presence-broadcast to " +
                        $"cid={m.CharacterId} failed: {ex.Message}");
                }
            }
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → presence-broadcast(0x0040) " +
                $"guild=\"{guild.Name}\" changed=\"{changed.CharacterName}\"(cid={changedCid}) " +
                $"online={online} ch={(online ? channel.ToString() : "-")}");
        }

        // 0x0043/0x008C 共用成员列表包体(2026-09-01 handler 0x1130B60 全量反汇编定案,
        // 两 opcode 读序列完全一致; 0x8C 仅在成员循环后多读 1 个尾随 u32 @0x1130FD6):
        //   [u8 框架标志=1][u32 工会id(门禁)][dstr 公告][u32 ?][u16 成员数][u16 在线数]
        //   × { [u32 cid][dstr 角色名][dstr 留言][u16 等级][u8 职业 p1][u8 p2][u8 p3]
        //       [u8 频道][u8 p5][u8 p6][u8 p7][u32][u32=账号id][u8 p8][dstr 称号] }
        //   等级后共 7 个 u8 + 2 个 u32 + 1 个 u8(读序 #10~#19, 见 disasm_1130B60.txt):
        //     p1 → record+0x24 职业(转职表渲染, 12→13 映射, 2026-08-31 实测定案)
        //     p2 → record+0x28/0x2C 转职字节: firstGrow=p2&0xF, secondGrow=(p2>>4)&7
        //          (0x1130E8C 拆位 → 职业名查表 [job][grow]; ★此前误写 grade →
        //          "自己看自己对, 看他人职业错"根因, gf39 修复: 写 DB grow_type)
        //     p3 → record+0x34(语义未定), p4 → record+0x38 频道号(0xFF=离线),
        //     p5 → record+0x30, p6 → record+0x144, p7 → record+0x148, p8 → record+0x160
        //   [u32#18](第2个u32) = 账号ID(2026-09-01 定案: 0x1130B60 → record+0x15C,
        //           0x19F0AC0 以其为 key 插 singleton+0xBA0 分组表 → 客户端按账号
        //           分组/+展开; 全写 0 时所有成员同组 = "所有成员都在一个账号下")。
        //   ★ opcode 0x43/0x8C 进入时客户端先 CLR.list(0x19EF2F0)清空成员表 —
        //     空成员数应答 = 清屏("点+号成员内容消失"的根因)。
        // 职位字节归一化: DB 手改/异常值落回普通会员(4)。
        private static byte NormalizeGrade(int grade)
            => (byte)(grade >= GuildSystem.GradeMaster && grade <= GuildSystem.GradeNewbie
                ? grade
                : GuildSystem.GradeNormal);

        private static void WriteMemberListBody(
            GamePacketWriter w,
            EnhancedClientSession session,
            GuildSystem.GuildInfo guild,
            int cid)
        {
            w.WriteByte(1);                                          // 框架子标志
            w.WriteInt32(guild?.GuildId ?? 0);                       // 工会id(门禁值)
            w.WriteClientDstr(guild?.Announcement ?? string.Empty);  // 公告 dstr(v24: 0x009A 落库真值)
            w.WriteUInt32(0);                                        // +9 u32(语义未定, 0 安全)
            w.WriteUInt16((ushort)(guild?.Members.Count ?? 0));      // +D u16 成员总数(界面"N/300人"分子 → manager+0x4c)
            w.WriteUInt16((ushort)(guild?.Members.Count ?? 0));      // +F u16 ★包内记录数(0x1130B60 按它循环解析!
                                                                        //   2026-09-03 GLM 逆向实锤: 此前误写"在线数"=2,
                                                                        //   客户端只解析前 2 条, 第 3 条(麦哲伦大哥)根本没进池
                                                                        //   → "成员不显示"真根因; 在线数由 0x0046#16/0x04EC 承担)
            if (guild == null)
                return;
            // 频道号(与 RaidHandler 同款): 监听端口 → 频道目录解析
            var myChannelId = GameNetworkConfig.ResolveGameChannel(
                session.ListenerPort).ChannelId;
            // ★ 排序: 按账号分组, 组内在线成员排最前(2026-09-03 实测定案)。
            //   客户端每账号组只渲染一行(= u32#18 指向的 pool 下标行), 组内在线者
            //   排最前 → 锚定行在线、整组可见; 其余成员靠"+"展开(0x02E9)。
            var rows = guild.Members
                .Select(m => (
                    Member: m,
                    Account: GuildSystem.GetAccountIdOfCharacter(m.CharacterId),
                    Online: IsMemberOnline(m.CharacterId)))
                .OrderBy(r => r.Account)
                .ThenByDescending(r => r.Online)
                .ToList();
            // ★ u32#18 真语义(GLM 2026-09-03 逆向 0x19F0AC0/0x19F4740/0x79C30A 实锤):
            //   不是账号ID! 客户端以 #18 为 key 建 map<#18,vector<pool下标>>, 渲染时
            //   把 key 直接当 pool 下标取行(0x19EA160: pool+key*0x184) →
            //   #18 必须 = 该账号首条记录在本包数组中的 0 基下标。
            //   同账号成员共享同一下标 → 每账号只渲染一行(=该下标行, 即"账号代表"),
            //   这就是 2016 原版"按账号分组显示"的真正机制。
            var accountFirstIndex = new Dictionary<int, int>();
            for (var i = 0; i < rows.Count; i++)
            {
                if (!accountFirstIndex.ContainsKey(rows[i].Account))
                    accountFirstIndex[rows[i].Account] = i;
            }
            foreach (var row in rows)
            {
                var m = row.Member;
                w.WriteInt32(m.CharacterId);                          // cid
                w.WriteClientDstr(m.CharacterName ?? string.Empty);  // 角色名
                w.WriteClientDstr(string.Empty);                     // 留言
                w.WriteUInt16((ushort)Math.Max(0, Math.Min(
                    GuildSystem.GetLevelOfCharacter(m.CharacterId), 0xFFFF)));  // 真实等级
                // p1 = 职业(客户端转职表渲染, 2026-08-31 实测定案):
                //   0→鬼剑士 11→鬼剑士(女鬼) 12→精灵骑士 13→守护者(基础)。
                //   主职业表(角色信息用): 12=Knight守护者 → 转职表映射 12→13。
                var colJob = GuildSystem.GetJobOfCharacter(m.CharacterId);
                w.WriteByte(colJob == 12 ? (byte)13 : (byte)colJob);
                // p2 = 转职字节(2026-09-01 disasm 0x1130E8C 定案: firstGrow=p2&0xF,
                //   secondGrow=(p2>>4)&7 → 职业名查表 [job][grow])。
                //   DB grow_type 即打包格式 (second<<4)|first → 直接写(掩码 0x77 防越界)。
                //   ★ gf39 修复: 此前误写 grade → 他人职业名查错转职。
                var grow = GuildSystem.GetGrowTypeOfCharacter(m.CharacterId);
                w.WriteByte((byte)((((grow >> 4) & 0x7) << 4) | (grow & 0xF)));
                // 频道号: 在线成员(含他人, 经 SessionDirectory 查会话)→真实频道,
                //   离线 → 0xFF(客户端=-1 → "X小时之前")。
                var rowChannel = ResolveMemberChannel(m.CharacterId, cid, myChannelId);
                // p3 → record+0x34 = 地区(town)id, 与 +0x38 频道号相邻构成"地区+频道"
                //   位置; 客户端悬浮"chXX 地名"的地名来源(0 → "未知")。2026-09-03
                //   实测: 会长行硬编码 1 恰显示"银色村庄"(town 1), 普通成员写 0
                //   显示"未知" → 改写真实 town_id。
                var rowTown = (byte)Math.Max(0, Math.Min(
                    GuildSystem.GetTownIdOfCharacter(m.CharacterId), 0xFF));
                var grade = NormalizeGrade(m.Grade);
                if (grade == GuildSystem.GradeMaster)
                {
                    // 会长行维持原观察形态: p5/p6/p7=1(p2/p3 已改为真实转职字节/地区id)
                    w.WriteByte(rowTown);
                    w.WriteByte(rowChannel);
                    w.WriteByte(1); w.WriteByte(1); w.WriteByte(1);
                }
                else
                {
                    w.WriteByte(rowTown);                            // p3=地区(town)id
                    w.WriteByte(rowChannel);                         // p4=频道(0xFF=离线)
                    w.WriteZeroBytes(3);                             // p5/p6/p7(语义未定)
                }
                // 第 2 个 u32(#18) = 该账号首条记录的 0 基下标(见头部注释, GLM 实锤;
                //   2026-09-01 的"账号ID"结论系误读 — 客户端拿它当 pool 下标取行,
                //   同账号共享下标 → 每账号渲染一行代表, 账号分组效果由此产生)。
                w.WriteUInt32(0);                                    // u32#17(语义未定, 0 安全)
                w.WriteUInt32((uint)accountFirstIndex[row.Account]); // u32#18=账号首记录下标
                w.WriteByte(0);
                w.WriteClientDstr(string.Empty);                     // 称号 dstr(空)
            }
        }

        // CMD 0x02F4 / 0x02FB: 成员视图打开时的列表请求(2026-08-31 反汇编):
        //   0x02F4 应答 = [u8 count]×{[u32 id][dstr][dstr][u32]}  → 0x19f0150 插入(先 0x19ef590 清空)
        //   0x02FB 应答 = [u8 count]×{[u32][dstr][u16][u32]}      → 0x19f0200 插入(先 0x19ef5a0 清空)
        //   两者处理完都发 UI 事件 0x7A(列表刷新)。
        //   ⚠️ 20:1x 实测: 02F4 回一条全空条目, 同盟槽仍显示"麦哲伦", 且点开弹窗显示
        //   自己工会"月光酒馆"(空条目 id=0 解析回落到自己工会) → 空条目探针无效, 回退空列表。
        //   同盟槽的真实数据源未定位(疑另有同盟 NOTI 未实现)。
        public static Task Handle_GUILD_ALLY_LIST_02F4(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
            => SendEmptyListAckAsync(session, 0x02F4, "02F4");

        public static Task Handle_GUILD_ALLY_LIST_02FB(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
            => SendEmptyListAckAsync(session, 0x02FB, "02FB");

        private static async Task SendEmptyListAckAsync(
            EnhancedClientSession session,
            ushort type,
            string label)
        {
            if (session == null)
                return;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                1,
                type,
                new byte[] { 0 }));
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → ack 0x{type:X4} [count=0](空列表) uid={(session?.Player?.UserId).GetValueOrDefault()}");
        }

        // CMD 0x02E9: 成员列表同账号"+"展开(2026-08-31 22:42 实测: 点"+"触发, body 空)。
        //   应答 = [u16 count]×{dstr} → 0x19f0c40 插入, 完成后发 UI 事件 0x77。
        //   语义 = 查询当前玩家同账号下、在同一工会的角色名列表
        //   (DNF2016 改版成员按账号分组, 点"+"展开该账号其他角色;
        //    回空导致展开区无内容 — 客户端清空旧行后无数据可填)。
        public static async Task Handle_GUILD_SUB_02E9(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            var names = cid != 0 ? GuildSystem.GetAccountMemberNames(cid) : null;
            var w = new GamePacketWriter();
            w.WriteByte(1);                     // cmd=1 框架标志(派发器先吃 1 字节)
            var count = names?.Count ?? 0;
            w.WriteUInt16((ushort)count);
            if (names != null)
            {
                foreach (var n in names)
                    w.WriteClientDstr(n ?? string.Empty);
            }
            await SendRawAckAsync(session, 0x02E9, w.ToArray(),
                $"02E9 同账号成员展开[条目={count}: {string.Join(",", names ?? new List<string>())}]");
        }

        // CMD 0x015A DONATE_GUILD_FUND: 捐赠工会金币。
        //   请求 body = [u32 捐赠金额](2026-09-01 抓包: [E0 93 04 00]=300000)。
        //   应答: 客户端 handler 0x0111C770 不读应答体, 直接弹系统消息 0xb4e3 — 回空体。
        //   gf40 前 = 空 ACK 桩, 既不扣个人金币也不加工会金币("捐赠没涨工会金币"根因)。
        //   gf40: 事务性扣个人金币(CurrencyService.TrySpendGold, 货币槽0)+ 累加 guilds.gold
        //   (迁移 v18), 成功后重推 0x0046 刷新工会界面(金币位 #28, 待 minidump 实测定案)。
        public static async Task Handle_GUILD_DONATE_015A(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            var amount = body != null && body.Length >= 4
                ? (int)BitConverter.ToUInt32(body, 0)
                : 0;
            var newGold = 0;
            var gid = 0;
            var ok = amount > 0 && cid != 0
                && GuildSystem.DonateGold(cid, amount, out newGold, out gid);
            FileLogger.Log(
                $"[{ProtocolName}] GUILD 0x015A 捐赠 uid={(session?.Player?.UserId).GetValueOrDefault()} " +
                $"cid={cid} amount={amount} ok={ok} gid={gid} newGold={newGold} raw={FormatBody(body)}");
            if (ok)
            {
                var guild = GuildSystem.GetGuildOfCharacter(cid);
                if (guild != null)
                    await SendGuildInfoAsync(session, guild);   // 重推 0x0046 刷新金币
            }
            // [u8 框架标志=1]: 让 cmd=1 handler 运行(空体 flag=0 会被派发器跳过, 弹不出系统消息)
            await SendRawAckAsync(session, 0x015A, new byte[] { 1 },
                $"015A 捐赠[amt={amount} ok={ok} newGold={newGold}]");
        }

        // CMD 0x02F8 BUY_GUILD_CONTENTS: 工会商店/工会属性购买。
        //   gf41 实现: 请求 body=[u32 content_id] → 查硬编码价格表 → GuildSystem.BuyGuildContent
        //   (事务性扣 guilds.gold + 插入 guild_contents) → 重推 0x0046 刷新 UI。
        //   gf42 ACK body 从空改为 [u8 result(1=ok,0=fail)][u32 content_id][u8 status=1][u8 level=1]
        //   试图让客户端收到成功确认后自动刷新工会属性区域。
        public static async Task Handle_BUY_GUILD_CONTENTS_02F8(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            var contentId = body != null && body.Length >= 4
                ? (int)BitConverter.ToUInt32(body, 0)
                : 0;
            // ★ 权限校验(2026-09-04 GLM 位语义): 购买公会内容需 bit8(BuyContent)
            if (cid != 0 && !GuildSystem.HasPermission(cid, GuildPermissions.BuyContent))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD BUY_GUILD_CONTENTS 拒绝: cid={cid} " +
                    $"无公会内容购买权限(bit8) contentId={contentId}");
                // 仍回 ACK(result=0)保持管线一致
                var wDeny = new GamePacketWriter();
                wDeny.WriteByte(1);            // cmd=1 框架标志(派发器先吃 1 字节)
                wDeny.WriteByte(0);
                wDeny.WriteUInt32((uint)contentId);
                wDeny.WriteByte(0);
                wDeny.WriteByte(0);
                await SendRawAckAsync(session, 0x02F8, wDeny.ToArray(), "02F8 无权限(result=0)");
                return;
            }
            // ★ 金币商品分支(2026-09-07 参考包权威: type20 改名 / type21 契约,
            //   扣【角色金币】而非公会 GM; 旧版错走 BuyGuildContent 扣公会资金)。
            if (contentId is 22 or 23 or 24)
            {
                await HandleGoldContentPurchaseAsync(session, cid, contentId);
                return;
            }
            var newGold = 0;
            var gid = 0;
            string buyError = null;
            var ok = false;
            // ★ 2026-09-07 实测: DB 异常(如缺列)曾沿调用链炸断连接("网络连接中断"),
            //   购买是旁路操作, 必须兜底成 result=0 + 公告, 绝不能拖垮会话。
            try
            {
                ok = contentId > 0 && cid != 0
                    && GuildSystem.BuyGuildContent(cid, contentId, out newGold, out gid, out buyError);
            }
            catch (Exception ex)
            {
                buyError = "购买失败（服务器内部错误），请稍后重试。";
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD BUY_GUILD_CONTENTS 异常 cid={cid} contentId={contentId}: {ex}");
            }
            FileLogger.Log(
                $"[{ProtocolName}] GUILD BUY_GUILD_CONTENTS 0x02F8 " +
                $"uid={(session?.Player?.UserId).GetValueOrDefault()} cid={cid} " +
                $"contentId={contentId} ok={ok} gid={gid} newGold={newGold}");
            if (ok)
            {
                // 仓库扩张(id11/12)购买成功 → 全会在线成员推送最新仓库快照(容量变化)
                if (contentId is 11 or 12)
                    await GuildWarehouseHandler.BroadcastRefreshAsync(gid);
                var guild = GuildSystem.GetGuildOfCharacter(cid);
                if (guild != null)
                {
                    await SendGuildInfoAsync(session, guild);   // 购买者: 重推 0x0046 刷新金币+内容(末尾含 buff 推送)
                    // gf44: 工会属性对全体在线成员即时生效 — 遍历同工会在线成员
                    // 逐个推 buff(购买者已在上面推过, 跳过)。SendGuildBuffAsync
                    // 先 DEL 再 ADD, 重复推安全。其他成员重开界面/重登也会补。
                    foreach (var m in guild.Members)
                    {
                        if (m.CharacterId == cid)
                            continue;
                        if (Sessions != null
                            && Sessions.TryGet(m.CharacterId, out var ms)
                            && ms != null)
                        {
                            await SendGuildBuffAsync(ms, guild);
                        }
                    }
                }
            }
            // 非空 ACK body: [u8 框架标志=1] + result + content_id + status + level
            var w = new GamePacketWriter();
            w.WriteByte(1);                     // cmd=1 框架标志(派发器先吃 1 字节)
            w.WriteByte(ok ? (byte)1 : (byte)0);
            w.WriteUInt32((uint)contentId);
            w.WriteByte(ok ? (byte)1 : (byte)0);
            w.WriteByte(ok ? (byte)1 : (byte)0);
            await SendRawAckAsync(session, 0x02F8, w.ToArray(),
                $"02F8 BUY_GUILD_CONTENTS[contentId={contentId} ok={ok} newGold={newGold}]");
            // 仓库扩张购买失败 → 补一条系统公告说明原因(2026-09-07 实测:
            // 仅 result=0 时客户端只弹"请稍后再试", 无任何指引)。
            if (!ok && buyError != null)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE,
                    ServerNoticeMessageBuilder.Build(buyError)));
            }
        }

        public static async Task Handle_GUILD_CHECKIN_04EC(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            // 应答(2026-08-31 客户端 handler 0x00444480 反汇编实锤):
            //   [u16 A][u32 B][u32 C][u32 N] + N×40B 条目
            //   A → 签到widget+0x5c, B → +0x58, C → +0x60(签到计数/总数语义待对位);
            //   条目逐字节复制分两列表, [entry+0x27]==1 → "在线工会成员"列表。
            //   ★ 旧 [u8][u8][u32] 头把 count 读成巨数 → 垃圾条目 → 弹窗空白/同盟槽污染。
            var cid = session?.Player?.CharacterId ?? 0;
            var guild = GuildSystem.GetGuildOfCharacter(cid);
            var members = guild?.Members;
            var count = members?.Count ?? 0;
            var onlineCount = members?.Count(m => IsMemberOnline(m.CharacterId)) ?? 0;
            // ★ 签到分子 = 今日已签到人数(持久签到, 北京06:00日界; 查询即记一次签到,
            //   2026-09-07 参考包规则: 首次有效登录/查询完成持久签到)。
            if (guild != null)
                GuildActivityService.RecordAttendance(cid);
            var signedToday = guild != null
                ? GuildActivityService.GetTodayAttendanceCount(guild.GuildId)
                : 0;

            var w = new GamePacketWriter();
            w.WriteByte(1);                                      // cmd=1 框架标志(派发器先吃 1 字节)
            w.WriteUInt16((ushort)signedToday);                  // A: 今日已签到人数(持久签到)
            w.WriteUInt32((uint)count);                          // B: 成员总数
            w.WriteUInt32((uint)count);                          // C: 签到分母=成员总数(2026-09-03 实测:
                                                                 //   客户端"N/M"分母读 +0x60, 旧写 0 → 显示 1/0)
            w.WriteUInt32((uint)count);                          // N
            if (members != null)
            {
                foreach (var m in members)
                {
                    // ⚠️ 条目布局未定案 — 17:08 实测真实字节(宽字名@4)闪退, 全零良性。
                    //   本轮二分: 只加 +0x27 在线标记(驱动"N/2"进度与在线列表), 名字仍零。
                    var entry = new byte[40];
                    entry[39] = IsMemberOnline(m.CharacterId) ? (byte)1 : (byte)0;
                    w.WriteBytes(entry);
                }
            }
            // ★ 成员视图打开必经此请求(已入会客户端不发 0x02E7/0x02F9)→ 顺势补推
            //   0x0046 详情, 否则成员视图会话里公会名/宣传语/标题全空(12:24 实测)。
            if (guild != null)
                await SendGuildInfoAsync(session, guild);
            await SendRawAckAsync(session, 0x04EC, w.ToArray(),
                $"04EC 签到[条目={count} 在线={onlineCount}]");
        }

        // 0x04ED: ⚠️ 静默消费, 不应答! [u32 0] 虽被 table1(0x00444010) 精确消化, 但该类型
        //   同 0x02E8 存在"框架第二读取器", 会溢出并回 0x00D9 01-ED-04, 随后客户端发包
        //   管线错乱(发出 type=0x0000 畸形包, 工会窗口卡死, 2026-08-31 09:45 抓包实证)。
        //   2026-09-03: 注册 handler 吃掉请求消除 Unhandled 噪音, 业务应答待框架读取器
        //   完整字段逆向后再开。
        public static Task Handle_GUILD_CHECKIN_ONLINE_04ED(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            FileLogger.Log(
                $"[{ProtocolName}] GUILD 04ED 签到在线成员查询 " +
                $"uid={(session?.Player?.UserId).GetValueOrDefault()} raw={FormatBody(body)} " +
                $"(静默不应答: 框架第二读取器溢出风险)");
            return Task.CompletedTask;
        }

        // ===== 公会签到/捐献/贡献 三明细(2026-09-07 参考包权威格式) =====

        // CMD 0x02E9 TODAY_GUILD_ATTENDANCE_DETAIL_INFO: 签到明细(今日已签到成员名)。
        //   应答(参考包权威): [u8 1][u16 count]×{dstr 名字}, 随后补推 0x0046。
        //   ★ 冲突定案: 本端旧实现返回"同账号同会角色名"(成员列表点"+"展开,
        //     2026-08-31 实测触发但展示未实锤); 参考包与同枚举名
        //     (TODAY_GUILD_ATTENDANCE_DETAIL_INFO)均为签到明细, 同账号展开数据
        //     改由 0x02E7 提供。若"+"展开回退再评估。
        public static async Task Handle_TODAY_GUILD_ATTENDANCE_DETAIL_02E9(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            var guild = GuildSystem.GetGuildOfCharacter(cid);
            var names = guild != null
                ? GuildSystem.Repository.GetTodayAttendeeNames(
                    guild.GuildId, GuildActivityService.TodayString())
                : new List<string>();

            var w = new GamePacketWriter();
            w.WriteByte(1);                     // cmd=1 框架标志
            w.WriteUInt16((ushort)names.Count);
            foreach (var n in names)
                w.WriteClientDstr(n ?? string.Empty);
            if (guild != null)
                await SendGuildInfoAsync(session, guild);
            await SendRawAckAsync(session, 0x02E9, w.ToArray(),
                $"02E9 签到明细[今日已签到={names.Count}]");
        }

        // CMD 0x02EA REQ_GUILD_MILEAGE_HISTORY: 捐献(GM)明细。
        //   应答(参考包权威, native 0x1120600): [u8 1][u32 count] + count×44B
        //   条目 = [i32 unix秒][名字 30B UTF-8+NUL][u8 4][u8 0][u32 0][i32 GM]。
        public static async Task Handle_REQ_GUILD_MILEAGE_HISTORY_02EA(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            var guild = GuildSystem.GetGuildOfCharacter(cid);
            var rows = guild != null
                ? GuildSystem.Repository.GetDonationHistory(guild.GuildId)
                : new List<(string OccurredUtc, string Name, int Gold, int Mileage)>();

            var w = new GamePacketWriter();
            w.WriteByte(1);
            w.WriteUInt32((uint)rows.Count);
            foreach (var r in rows)
            {
                w.WriteInt32(ToUnixSeconds32(r.OccurredUtc));
                w.WriteBytes(FixedUtf8Name(r.Name, 30));
                w.WriteByte(4);
                w.WriteByte(0);
                w.WriteUInt32(0);
                w.WriteInt32(r.Mileage);
            }
            await SendRawAckAsync(session, 0x02EA, w.ToArray(),
                $"02EA 捐献明细[条目={rows.Count}]");
        }

        // CMD 0x0328 GUILD_CONTRIBUTE_HISTORY: 贡献明细。
        //   应答(参考包权威, native 0x111F180 固定 50 行): [u8 1] + 50×44B
        //   条目 = [u32 unix秒][名字 32B UTF-8+NUL][u32 贡献点][u32 来源(3=捐献 4=通关)]。
        public static async Task Handle_GUILD_CONTRIBUTE_HISTORY_0328(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            var guild = GuildSystem.GetGuildOfCharacter(cid);
            var rows = guild != null
                ? GuildSystem.Repository.GetContributionHistory(guild.GuildId)
                : new List<(string OccurredUtc, string Name, int Points, int SourceType)>();

            var w = new GamePacketWriter();
            w.WriteByte(1);
            for (var i = 0; i < 50; i++)
            {
                if (i >= rows.Count)
                {
                    w.WriteBytes(new byte[44]);
                    continue;
                }
                var r = rows[i];
                w.WriteUInt32(unchecked((uint)ToUnixSeconds32(r.OccurredUtc)));
                w.WriteBytes(FixedUtf8Name(r.Name, 32));
                w.WriteUInt32((uint)r.Points);
                w.WriteUInt32((uint)r.SourceType);
            }
            await SendRawAckAsync(session, 0x0328, w.ToArray(),
                $"0328 贡献明细[条目={rows.Count}]");
        }

        // occurred_at 文本(UTC "yyyy-MM-dd HH:mm:ss") → unix 秒。
        private static int ToUnixSeconds32(string occurredUtcText)
        {
            if (string.IsNullOrEmpty(occurredUtcText))
                return 0;
            if (!DateTime.TryParseExact(
                    occurredUtcText,
                    "yyyy-MM-dd HH:mm:ss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var dt))
                return 0;
            var seconds = new DateTimeOffset(
                DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToUnixTimeSeconds();
            return (int)Math.Clamp(seconds, 0, int.MaxValue);
        }

        // 定长名字字段: UTF-8 截断到 size-1 字节, NUL 填充。
        private static byte[] FixedUtf8Name(string name, int size)
        {
            var field = new byte[size];
            if (!string.IsNullOrEmpty(name))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(name);
                var take = Math.Min(bytes.Length, size - 1);
                Array.Copy(bytes, field, take);
            }
            return field;
        }

        // ===== 金币商品(2026-09-07 参考包权威): 改名(22) / 契约(23/24) =====

        /// <summary>0x02F8 content_id=22/23/24 统一处理: 扣角色金币, 不走公会 GM。</summary>
        private static async Task HandleGoldContentPurchaseAsync(
            EnhancedClientSession session, int cid, int contentId)
        {
            var ok = false;
            string error = "invalid";
            int gid = 0;
            string renamedTo = null;

            if (cid == 0)
            {
                error = "no-character";
            }
            else if (contentId == 22)
            {
                // 改名: 需要 5 分钟内的改名草稿(0x009C 已验重名/权限)
                if (!PendingRenames.TryGetValue(cid, out var draft)
                    || DateTime.UtcNow - draft.DraftUtc > TimeSpan.FromMinutes(5))
                {
                    error = "no-draft";
                }
                else if (!GuildSystem.ChargeCreateCost(cid, GuildSystem.RenameGoldCost))
                {
                    error = "insufficient-gold";
                }
                else
                {
                    if (GuildSystem.RenameGuild(cid, draft.Name, out gid, out error))
                    {
                        ok = true;
                        renamedTo = draft.Name;
                        PendingRenames.TryRemove(cid, out _);
                    }
                    else
                    {
                        GuildSystem.RefundCreateCost(cid, GuildSystem.RenameGoldCost);
                    }
                }
            }
            else
            {
                // 契约 23/24: 容量匹配 + 30 天累计 + 扣角色金币(方法内完成)
                ok = GuildSystem.BuyGuildContract(cid, contentId, out gid, out error);
            }

            FileLogger.Log(
                $"[{ProtocolName}] GUILD gold-content cid={cid} contentId={contentId} " +
                $"ok={ok} gid={gid} error={error} rename=\"{renamedTo ?? string.Empty}\"");
            if (ok)
            {
                var guild = GuildSystem.GetGuildOfCharacter(cid);
                if (guild != null)
                {
                    if (renamedTo != null)
                        await BroadcastGuildRenameAsync(guild, renamedTo);
                    await SendGuildInfoAsync(session, guild);
                }
            }
            // ACK 与 GM 购买同构: [u8 框架=1][u8 result][u32 content_id][u8][u8]
            var w = new GamePacketWriter();
            w.WriteByte(1);
            w.WriteByte(ok ? (byte)1 : (byte)0);
            w.WriteUInt32((uint)contentId);
            w.WriteByte(ok ? (byte)1 : (byte)0);
            w.WriteByte(ok ? (byte)1 : (byte)0);
            await SendRawAckAsync(session, 0x02F8, w.ToArray(),
                $"02F8 gold-content[id={contentId} ok={ok} error={error}]");
        }

        // NOTI 0x00DA CHANGE_GUILD_NAME_TO_GUILD_MEMBERS: 改名广播。
        //   包体 = 单个 dstr(新名字); native 0x118F430 读一个 UTF-8 DSTR 更新公会缓存。
        private static async Task BroadcastGuildRenameAsync(
            GuildSystem.GuildInfo guild, string newName)
        {
            if (guild == null || Sessions == null)
                return;
            var w = new GamePacketWriter();
            w.WriteClientDstr(newName ?? string.Empty);
            var payload = GamePacketEnvelopeBuilder.Build(0, 0x00DA, w.ToArray());
            foreach (var m in guild.Members)
            {
                if (Sessions.TryGet(m.CharacterId, out var ms) && ms != null)
                {
                    try { await ms.SendPacketAsync(payload); }
                    catch (Exception ex)
                    {
                        FileLogger.Log(
                            $"[{ProtocolName}] GUILD 0x00DA 广播失败 cid={m.CharacterId}: {ex.Message}");
                    }
                }
            }
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → NOTI 0x00DA 改名广播 gid={guild.GuildId} name=\"{newName}\"");
        }

        // ===== CMD 0x0312 CHANGE_GUILD_MARK: 更换公会图标(2026-09-07 参考包权威) =====
        //   请求 body = [u8 emblemId](0..15)。16 内置图标: 前 5 免费, 其余 2000 GM;
        //   重复应用当前图标免费。权限 bit7(ChangeEmblem)。
        //   应答 = [u8 框架=1][u8 result]; 成功后向在线成员重推 0x0046(#12 图标)。
        public static async Task Handle_CHANGE_GUILD_MARK_0312(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            var emblemId = body != null && body.Length >= 1 ? body[0] : (byte)0xFF;
            var gid = 0;
            var newGold = 0;
            var ok = false;
            if (cid != 0 && emblemId < GuildSystem.EmblemCount)
            {
                if (!GuildSystem.HasPermission(cid, GuildPermissions.MainBtn7))
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] GUILD CHANGE_MARK 拒绝: cid={cid} 无图标权限(bit7)");
                }
                else
                {
                    ok = GuildSystem.ChangeEmblem(cid, emblemId, out gid, out newGold);
                }
            }
            FileLogger.Log(
                $"[{ProtocolName}] GUILD 0x0312 CHANGE_MARK cid={cid} " +
                $"emblem={emblemId} ok={ok} gid={gid} newGold={newGold}");
            if (ok)
            {
                var guild = GuildSystem.GetGuildOfCharacter(cid);
                if (guild != null)
                {
                    await SendGuildInfoAsync(session, guild);
                    // 图标对全体在线成员可见: 逐个重推 0x0046(请求者已推, 跳过)
                    if (Sessions != null)
                    {
                        foreach (var m in guild.Members)
                        {
                            if (m.CharacterId == cid)
                                continue;
                            if (Sessions.TryGet(m.CharacterId, out var ms) && ms != null)
                            {
                                try { await SendGuildInfoAsync(ms, guild); }
                                catch (Exception ex)
                                {
                                    FileLogger.Log(
                                        $"[{ProtocolName}] GUILD 图标刷新失败 cid={m.CharacterId}: {ex.Message}");
                                }
                            }
                        }
                    }
                }
            }
            await SendRawAckAsync(session, 0x0312,
                new byte[] { 1, ok ? (byte)1 : (byte)0 },
                $"0312 CHANGE_MARK[emblem={emblemId} ok={ok}]");
        }

        // ===== CMD 0x04C4 CONTRACT_OF_GUILD: 契约每日领取(2026-09-07 参考包权威) =====
        //   请求 body = 18B, [u32 @14] = 21(内容类型)。每角色每游戏日限领 1 次,
        //   换公会不能重复领; 公会须有生效中的契约(type21)。
        //   应答 = [u8 框架=1][u8 result]。
        //   ★ 奖励物品 ID 取自 PVF (r)guild.etc [guild contracts] 段:
        //     `21 490001385 1` = type21 契约每日发 490001385(公会之契约礼盒)×1
        //     (2026-09-07 探针实证本端 Script.pvf 含该段与 chn_490001385.stk)。
        private const int ContractRewardItemId = 490001385;   // 公会之契约礼盒([booster random])
        private const int ContractRewardCount = 1;

        public static async Task Handle_CONTRACT_OF_GUILD_04C4(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            var contentType = body != null && body.Length == 18
                ? (int)BitConverter.ToUInt32(body, 14)
                : 0;
            var ok = false;
            var reason = "unknown";
            if (contentType != 21)
            {
                reason = "bad-type";
            }
            else if (cid == 0)
            {
                reason = "no-character";
            }
            else
            {
                var guild = GuildSystem.GetGuildOfCharacter(cid);
                if (guild == null)
                {
                    reason = "not-member";
                }
                else if (!GuildSystem.HasActiveContract(guild.GuildId))
                {
                    reason = "no-contract";
                }
                else
                {
                    var day = GuildActivityService.TodayString();
                    if (!GuildSystem.Repository.TryClaimGuildContract(
                            cid, guild.GuildId, day, ContractRewardItemId))
                    {
                        reason = "already-claimed";
                    }
                    else if (Game.Inventory.GmStyleItemGrant.TryGrant(
                            GuildSystem.Repository.ConnectionString,
                            cid, session.Player?.UserId ?? 0,
                            ContractRewardItemId, ContractRewardCount))
                    {
                        ok = true;
                        reason = "granted";
                    }
                    else
                    {
                        // 背包失败不消耗资格: 回滚今日领取记录, 整理背包后可重试
                        GuildSystem.Repository.DeleteGuildContractClaim(cid, day);
                        reason = "inventory-full";
                    }
                }
            }
            FileLogger.Log(
                $"[{ProtocolName}] GUILD 0x04C4 CONTRACT cid={cid} ok={ok} reason={reason} " +
                $"raw={FormatBody(body)}");
            await SendRawAckAsync(session, 0x04C4,
                new byte[] { 1, ok ? (byte)1 : (byte)0 },
                $"04C4 CONTRACT[ok={ok} reason={reason}]");
        }

        // CMD 0x008C GUILD_ALL_MEMBER_LIST: 成员列表"+"展开时客户端发送
        // (2026-08-31 23:16 抓包实证: 点"+"两次各触发一次, 均紧跟 0x00D9 01-8C-00 溢出回执)。
        // ★ 2026-09-01 handler 0x1130B60 全量反汇编定案(与 0x0043 共用同函数):
        //   - opcode 0x43/0x8C 进入时先 CLR.list(0x19EF2F0)清空客户端成员表 —
        //     旧"空探针"(成员数=0)把成员表清成 0 条 → "点+号成员内容消失,
        //     刷新才恢复"(刷新=客户端重发 0x0043, 真实列表重新填回)。
        //   - 读序列与 0x0043 完全一致, 仅 0x8C/0x8D 在成员循环后多读 1 个
        //     尾随 u32(@0x1130FD6), 不写则框架溢出回执 0x00D9。
        //   - 2016 改版语义: 成员列表按账号分组显示, "+"展开查同账号其他角色。
        //     2026-09-01 已接入: 成员条目第 2 个 u32 = 账号 ID(0x1130B60 存
        //     record+0x15C, 0x19F0AC0 作分组 key), WriteMemberListBody 现写真实
        //     account_id → 客户端按账号分组渲染; 0x02E9 应答当前玩家同账号同工会
        //     角色名(请求体空, 服务端只能按请求者账号应答)。
        public static async Task Handle_GUILD_ALLY_LIST_008C(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            var guild = GuildSystem.GetGuildOfCharacter(cid);
            var w = new GamePacketWriter();
            WriteMemberListBody(w, session, guild, cid);
            w.WriteUInt32(0);                                   // ★尾随 u32(0x8C/0x8D 专属读)
            await SendRawAckAsync(session, 0x008C, w.ToArray(),
                $"008C 全部成员列表 members={guild?.Members.Count ?? 0}");
        }

        // CMD 0x02B3: 公会管理-宣传信息修改(2026-08-31 抓包: 点"输入"按钮发送,
        // body=[u8 标志][dstr 新宣传语](2026-09-03 实测空输入仅 [00]))。
        // ★仍不发 cmd=1 ack: 全代码段 REG 扫描确认客户端无 0x02B3 的 cmd=1 handler —
        //   unhandled ack 与 0x04ED 同类风险(框架第二读取器溢出→发包管线错乱),
        //   三轮 N=1 闪退均发生在 ack 后 0.07s(10:54 日志定案)。
        //   2026-09-03 起实现业务: 解析新宣传语 → 落库 → 重推 0x0046 刷新界面
        //   (0x0046 是 cmd=0 推送, 客户端有 handler, 安全)。
        public static async Task Handle_GUILD_PROMO_MODIFY_02B3(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            // 解析 [u8 标志][dstr 新宣传语]; 空输入([00])无操作
            string newMemo = null;
            if (body != null && body.Length >= 5 && body[0] != 0)
            {
                var len = BitConverter.ToInt32(body, 1);
                if (len > 0 && len <= 200 && 5 + len <= body.Length)
                    newMemo = ClientTextEncoding.GetString(body, 5, len);
            }
            FileLogger.Log(
                $"[{ProtocolName}] GUILD PROMO_MODIFY uid={(session?.Player?.UserId).GetValueOrDefault()} " +
                $"cid={cid} raw={FormatBody(body)} newMemo=\"{newMemo ?? ""}\" (不 ack)");
            if (session == null || string.IsNullOrEmpty(newMemo))
                return;

            // ★ 权限校验(2026-09-04 GLM 位语义): 宣传语修改需 bit5(EditPromo)
            if (!GuildSystem.HasPermission(cid, GuildPermissions.EditPromo))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD PROMO_MODIFY 拒绝: cid={cid} 无宣传语权限(bit5)");
                return;
            }

            var gid = 0;
            if (!GuildSystem.UpdateMemo(cid, newMemo, out gid))
            {
                FileLogger.Log($"[{ProtocolName}] GUILD PROMO_MODIFY 失败: cid={cid} 不在会内");
                return;
            }
            FileLogger.Log($"[{ProtocolName}] GUILD 宣传语已更新 gid={gid} memo=\"{newMemo}\"");
            var guild = GuildSystem.GetGuildOfCharacter(cid);
            if (guild != null)
                await SendGuildInfoAsync(session, guild);   // 重推 0x0046 刷新宣传语
        }

        private static async Task SendRawAckAsync(
            EnhancedClientSession session,
            ushort type,
            byte[] payload,
            string label)
        {
            if (session == null)
                return;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(1, type, payload));
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → ack 0x{type:X4} {label} uid={(session?.Player?.UserId).GetValueOrDefault()}");
        }

        // ===== 工会管理: 职位/权限/退会/委任/解散(2026-09-04 GLM 逆向定案格式) =====

        // 向工会所有在线成员重推 0x0046+0x007F(职位/权限/宣传语变更后生效;
        //   0x0046 按接收者各自职位构建, 客户端无本地写位图路径, 必须靠它刷新)。
        private static async Task BroadcastGuildInfoAsync(GuildSystem.GuildInfo guild)
        {
            if (guild == null || Sessions == null)
                return;
            foreach (var m in guild.Members)
            {
                if (!Sessions.TryGet(m.CharacterId, out var target) || target == null)
                    continue;
                try
                {
                    await SendGuildInfoAsync(target, guild);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] GUILD broadcast-info to cid={m.CharacterId} failed: {ex.Message}");
                }
            }
        }

        // 向工会所有在线成员重推 NOTI 0x008C(CHANGE_GUILD_MEMBER_GRADE 定案:
        //   与 0x0043 共用 handler 0x1130B60 + 同一 wire 格式 + 尾随 u32) =
        //   调职/退会/解散后的全量成员列表刷新。
        private static async Task BroadcastMemberList008CAsync(GuildSystem.GuildInfo guild)
        {
            if (guild == null || Sessions == null)
                return;
            foreach (var m in guild.Members)
            {
                if (!Sessions.TryGet(m.CharacterId, out var target) || target == null)
                    continue;
                try
                {
                    var w = new GamePacketWriter();
                    WriteMemberListBody(w, target, guild, m.CharacterId);
                    w.WriteUInt32(0);                               // 尾随 u32(0x8C 专属读)
                    await target.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00, 0x008C, w.ToArray()));
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] GUILD broadcast-008C to cid={m.CharacterId} failed: {ex.Message}");
                }
            }
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → broadcast 0x008C member-list guild=\"{guild.Name}\" members={guild.Members.Count}");
        }

        // NOTI 0x0095 GUILD_MASTER_DELEGATE: 公会内广播"玩家[%s]已成为公会会长"。
        //   客户端字符串 0x1A2F; body 推断为 [dstr 新会长名](参考 0x0093/0x0094 同族格式)。
        //   cmd=0(table2) 无需框架标志。
        private static async Task SendMasterDelegateNotificationAsync(
            EnhancedClientSession session,
            string newMasterName)
        {
            if (session == null)
                return;
            var w = new GamePacketWriter();
            w.WriteClientDstr(newMasterName ?? string.Empty);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00, 0x0095, w.ToArray()));
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → NOTI 0x0095 master-delegate " +
                $"newMaster=\"{newMasterName ?? ""}\" " +
                $"uid={(session?.Player?.UserId).GetValueOrDefault()}");
        }

        // NOTI 0x0096 HAS_BEEN_GUILD_MASTER: 单独发给新会长本人。
        //   客户端字符串 0x19C6 "您已成为公会会长"; body 推断为空(无 %s 占位)。
        private static async Task SendYouAreMasterNotificationAsync(
            EnhancedClientSession session)
        {
            if (session == null)
                return;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00, 0x0096, Array.Empty<byte>()));
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → NOTI 0x0096 you-are-master " +
                $"uid={(session?.Player?.UserId).GetValueOrDefault()}");
        }

        // CMD 0x02EB CHANGE_GUILD_GRADE: 权限打勾矩阵保存。
        //   包体(GLM 0x77A10D 定案): [u8 标志][u32 grade 1..5][u32 位图], 单职位一包。
        //   校验: 操作者需 Appoint(bit11); 会长位图固定不可改。生效: 重推 0x0046+0x007F。
        public static async Task Handle_CHANGE_GUILD_GRADE_02EB(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            FileLogger.Log(
                $"[{ProtocolName}] GUILD CHANGE_GRADE(权限保存) cid={cid} raw={FormatBody(body)}");
            if (session == null || body == null || body.Length < 9)
                return;
            var grade = BitConverter.ToInt32(body, 1);
            var bitmap = BitConverter.ToUInt32(body, 5);
            if (!GuildSystem.SetGradeBitmap(cid, grade, bitmap))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD CHANGE_GRADE 拒绝: cid={cid} grade={grade} " +
                    $"(无 bit11 权限/职位非法/会长不可改)");
                return;
            }
            var guild = GuildSystem.GetGuildOfCharacter(cid);
            if (guild != null)
                await BroadcastGuildInfoAsync(guild);
        }

        // CMD 0x02EC CHANGE_GUILD_GRADE_NAME: 职级更名。
        //   包体(GLM 0x248D07A 定案): [u8 标志][u32 grade槽][dstr 新名]。
        //   校验: Appoint(bit11); 会长名不可改。生效: 成功回 cmd=1 ACK [01]
        //   (权威源码对齐 2026-09-06; table1 0x02EC handler=0x0111E280 存在, ack 安全)
        //   后重推全员 0x0046。
        public static async Task Handle_CHANGE_GUILD_GRADE_NAME_02EC(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            FileLogger.Log(
                $"[{ProtocolName}] GUILD CHANGE_GRADE_NAME cid={cid} raw={FormatBody(body)}");
            if (session == null || body == null || body.Length < 9)
                return;
            var grade = BitConverter.ToInt32(body, 1);
            var name = ParseDstrAt(body, 5, out _);
            if (!GuildSystem.SetGradeName(cid, grade, name))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD CHANGE_GRADE_NAME 拒绝: cid={cid} grade={grade} name=\"{name}\"");
                return;
            }
            await SendRawAckAsync(session, 0x02EC, new byte[] { 1 },
                $"02EC 职级更名 grade={grade} name=\"{name}\"");
            var guild = GuildSystem.GetGuildOfCharacter(cid);
            if (guild != null)
                await BroadcastGuildInfoAsync(guild);
        }

        // CMD 0x007E SET_SUB_GUILD_MASTER: 调整成员职位。
        //   包体(GLM 0x2595BE6 等 5 发包点定案): [u8 0][dstr 角色名][u32 grade∈{2,3,4,5}]。
        //   层级/权限规则见 GuildSystem.ChangeMemberGrade。生效: 0x008C 全量成员列表
        //   + 0x0046+0x007F(目标职位变化)。不发 cmd=1 ack(客户端无本地生效路径, 靠推送)。
        public static async Task Handle_SET_SUB_GUILD_MASTER_007E(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            FileLogger.Log(
                $"[{ProtocolName}] GUILD SET_GRADE(调级) cid={cid} raw={FormatBody(body)}");
            if (session == null || body == null || body.Length < 10)
                return;
            var targetName = ParseDstrAt(body, 1, out var off);
            var newGrade = off > 0 && off + 4 <= body.Length
                ? BitConverter.ToInt32(body, off)
                : -1;
            var error = GuildSystem.ChangeMemberGrade(cid, targetName, newGrade);
            if (error != null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD SET_GRADE 拒绝: cid={cid} target=\"{targetName}\" " +
                    $"grade={newGrade} 原因={error}");
                return;
            }
            FileLogger.Log(
                $"[{ProtocolName}] GUILD SET_GRADE 成功: \"{targetName}\" → grade={newGrade} by cid={cid}");
            var guild = GuildSystem.GetGuildOfCharacter(cid);
            if (guild != null)
            {
                await BroadcastMemberList008CAsync(guild);
                await BroadcastGuildInfoAsync(guild);
            }
        }

        // CMD 0x0329 SET_REPRESENTATIVE: 设置代表角色(退会流程前置, 2026-09-04 逆向定案)。
        //   客户端退会确认回调(0x023E4B75)先发 0x0329 [dstr 角色名] 再发 0x0099 退会;
        //   多角色账号须指定代表角色(错误 0x4C9C "退出公会时必须委任代表角色")。
        //   发包函数 0x019E9F20: begin(0x0329,0) + write_dstr(名字)。
        //   ack(table1 0x0329=0x01133BE0): 成功=[1](空体, 弹 0xBD2A 消息);
        //   失败=[0][errcode](0xBD4E "代表角色设置失败(%d)")。
        public static async Task Handle_SET_REPRESENTATIVE_0329(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            FileLogger.Log(
                $"[{ProtocolName}] GUILD SET_REPRESENTATIVE(代表角色) cid={cid} raw={FormatBody(body)}");
            if (session == null || body == null || body.Length < 4)
                return;
            // 防御解析: 跳过可能的 [u8 0] 前缀
            var name = ParseDstrAt(body, 0, out var off);
            if (off < 0 && body.Length >= 5 && body[0] == 0)
                name = ParseDstrAt(body, 1, out off);
            var guild = GuildSystem.GetGuildOfCharacter(cid);
            if (guild == null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD SET_REPRESENTATIVE 忽略: cid={cid} 不在任何工会");
                return;
            }
            // 单角色/简化实现: 名字为空或等于自己 → 接受; 否则查成员表确认
            var member = string.IsNullOrEmpty(name)
                ? guild.Members.FirstOrDefault(m => m.CharacterId == cid)
                : guild.Members.FirstOrDefault(m =>
                    m.CharacterId == cid || m.CharacterName == name);
            if (member.CharacterId == 0)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD SET_REPRESENTATIVE 拒绝: cid={cid} name=\"{name}\" 不是本工会成员");
                return;
            }
            // 成功 ack = [1](cmd=1 框架标志, 无后续字段)
            var ack = new GamePacketWriter();
            ack.WriteByte(1);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                1, (ushort)0x0329, ack.ToArray()));
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → ack 0x0329 ok 代表角色=\"{member.CharacterName}\" cid={cid}");
        }

        // CMD 0x0099 REQ_GUILD_SECEDE: 退会/踢人 复合命令(2026-09-04 抓包定案)。
        //   踢人形态: [dstr 目标名] (实测: 06-00-00-00-E6-B5-8B-E8-AF-95 = "测试",
        //     会长在成员列表右键"强制踢除"→确认框→点确定发送)。
        //   退会形态: [u8 0][u32 0] (GLM 0x23E4BD1 逆向定案, 无目标字段 → 只能退自己;
        //     退会流程先发 0x0329 设代表角色)。
        //   会长有人时退会须先委任; 光杆会长退会=解散。
        public static async Task Handle_REQ_GUILD_SECEDE_0099(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            FileLogger.Log(
                $"[{ProtocolName}] GUILD SECEDE/KICK(退会/踢人) cid={cid} raw={FormatBody(body)}");
            if (session == null || body == null || body.Length == 0)
                return;

            // ── 分流: [dstr 目标名] = 踢人 ──
            if (body.Length >= 8)
            {
                var nlen = BitConverter.ToInt32(body, 0);
                if (nlen > 0 && nlen <= 72 && 4 + nlen == body.Length)
                {
                    var targetName = ClientTextEncoding.GetString(body, 4, nlen);
                    await HandleKickAsync(session, cid, targetName);
                    return;
                }
            }

            // ── 退会(自己) ──
            var error = GuildSystem.LeaveGuild(cid, out var guild);
            if (error != null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD SECEDE 拒绝: cid={cid} 原因={error}");
                return;
            }
            FileLogger.Log(
                $"[{ProtocolName}] GUILD SECEDE 成功: cid={cid} 退出 \"{guild?.Name}\"");
            // 剩余成员: 全量成员列表刷新(解散则工会已空, 跳过)
            if (guild != null && GuildSystem.GetGuildById(guild.GuildId) != null)
            {
                await BroadcastMemberList008CAsync(guild);
                await BroadcastGuildInfoAsync(guild);
            }
            // TODO(待 GLM 补 0x003B 读序列): 给退会者本人发退会通知清 my-guild-id;
            // 当前其客户端下次选角时不再收 0x0047 自然回落搜索视图。
        }

        // 0x0099 踢人路径。权限规则(客户端文案 0x19C5/0xB509 定案):
        //   会长/副会长/优秀成员(grade≤3)可踢; 踢副会长仅会长; 会长不可被踢。
        private static async Task HandleKickAsync(
            EnhancedClientSession session, int cid, string targetName)
        {
            var error = GuildSystem.KickMember(cid, targetName,
                out var guild, out var target);
            if (error != null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD KICK 拒绝: cid={cid} target=\"{targetName}\" 原因={error}");
                return;
            }
            FileLogger.Log(
                $"[{ProtocolName}] GUILD KICK 成功: \"{targetName}\"(cid={target.CharacterId}) " +
                $"被 cid={cid} 踢出 \"{guild?.Name}\"");
            // 剩余在线成员: 成员列表 + 工会信息刷新
            if (guild != null && GuildSystem.GetGuildById(guild.GuildId) != null)
            {
                await BroadcastMemberList008CAsync(guild);
                await BroadcastGuildInfoAsync(guild);
            }
            // 被踢者若在线: 无专用 NOTI 通道(table2 0x0099 是成员状态更新, 格式不符),
            // 其客户端工会 UI 靠重新登录回落(服务端 CharacterGuild 已删)。
        }

        // CMD 0x009B GUILD_MASTER_DELEGATE: 委任会长(enum 155)。
        //   ★ opcode 纠偏(2026-09-04 启动崩溃定案): GLM 初判的 0x0079 实为组队
        //   CHANGE_HOST(与 party-chat 注册冲突) — 真委任命令 = 0x009B。
        //   包体未实测: 防御解析 — [u8 标志][dstr 角色名] 优先; 退化 [u8][u32 cid/槽位]。
        public static async Task Handle_GUILD_MASTER_DELEGATE_009B(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            FileLogger.Log(
                $"[{ProtocolName}] GUILD MASTER_DELEGATE(委任) cid={cid} raw={FormatBody(body)}");
            if (session == null || body == null || body.Length < 2)
                return;
            var guild = GuildSystem.GetGuildOfCharacter(cid);
            if (guild == null)
                return;
            // 解析目标: 实测 0x009B 请求体 = [dstr 角色名](06-00-00-00-E5-B0-8F-E4-B8-89=小三),
            // 兼容旧客户端 [u8 标志][dstr 名] / [u8][u32 cid] 写法。
            var targetCid = 0;
            int start = 0;
            if (body != null && body.Length >= 5 && body[0] == 0
                && ParseDstrAt(body, 1, out _) != null)
                start = 1;
            var name = ParseDstrAt(body, start, out _);
            if (!string.IsNullOrEmpty(name) && name.Length <= 24)
            {
                targetCid = guild.Members
                    .FirstOrDefault(m => string.Equals(
                        m.CharacterName, name, StringComparison.Ordinal))
                    .CharacterId;
            }
            if (targetCid == 0 && body != null && body.Length >= start + 5)
            {
                var raw = BitConverter.ToInt32(body, start);
                targetCid = guild.Members.Any(m => m.CharacterId == raw)
                    ? raw
                    : (raw >= 0 && raw < guild.Members.Count
                        ? guild.Members[raw].CharacterId
                        : 0);
            }
            if (targetCid == 0)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD MASTER_DELEGATE 无法解析目标(name=\"{name ?? "?"}\")");
                return;
            }
            var error = GuildSystem.DelegateMaster(cid, targetCid, out var newMasterCid);
            if (error != null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD MASTER_DELEGATE 拒绝: cid={cid} targetCid={targetCid} 原因={error}");
                return;
            }
            FileLogger.Log(
                $"[{ProtocolName}] GUILD MASTER_DELEGATE 成功: 新会长 cid={newMasterCid} by cid={cid}");
            // 双方职位都变: 全员重推(0x0046 #12 按接收者各自职位)
            await BroadcastMemberList008CAsync(guild);
            await BroadcastGuildInfoAsync(guild);

            // 委任结果通知: 新会长本人收 0x0096, 其他在线成员(含原会长)收 0x0095
            var newMasterMember = guild.Members.FirstOrDefault(m => m.CharacterId == newMasterCid);
            var newMasterName = newMasterMember.CharacterName;
            foreach (var m in guild.Members)
            {
                if (!Sessions.TryGet(m.CharacterId, out var target) || target == null)
                    continue;
                try
                {
                    if (m.CharacterId == newMasterCid)
                        await SendYouAreMasterNotificationAsync(target);
                    else
                        await SendMasterDelegateNotificationAsync(target, newMasterName);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] GUILD delegate-notify to cid={m.CharacterId} failed: {ex.Message}");
                }
            }
        }

        // CMD 0x012F BREAK_GUILD: 解散工会(仅会长)。包体未定案(日志记录 raw)。
        public static async Task Handle_BREAK_GUILD_012F(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            FileLogger.Log(
                $"[{ProtocolName}] GUILD BREAK(解散) cid={cid} raw={FormatBody(body)}");
            if (session == null)
                return;
            var error = GuildSystem.BreakGuild(cid, out var guild);
            if (error != null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD BREAK 拒绝: cid={cid} 原因={error}");
                return;
            }
            FileLogger.Log(
                $"[{ProtocolName}] GUILD BREAK 成功: \"{guild?.Name}\"(id={guild?.GuildId}) 已解散");
            // TODO(待 GLM 补 0x003D 读序列): 给原成员发解散通知清 my-guild-id;
            // 当前各客户端下次选角自然回落搜索视图。
        }


        // CMD 0x016D JOIN_GUILD_INFO: 未入会角色"我的入会申请"状态查询(空 body)。
        // 2026-09-04 二次定案(逆向 dnf_hang.dmp table2 handler 0x024EBDF0):
        //   客户端发送: cmd=1 空 body —— 开界面未入会时(0x007750D7, [wnd+0x1dc]==1 分支)
        //   与 0x015C 申请成功后(0x00776576)各发一次。
        //   应答必须走 cmd=0 NOTI 0x0134 JOIN_GUILD_INFO: table1(cmd=1) 无 0x016D 表项,
        //   回 cmd=1 会被 0x00D9 丢弃; NOTI 0x016D 是 GROUP_MEMBER_LIST，误用会被客户端
        //   当作聊天群成员包解析并打开私聊窗口。table2(cmd=0) handler 0x024EBDF0。
        //   读序: [u32 key][u8 count] + count×{[dstr 公会名]}。
        //     key   → 窗口+0x230(0x24EC420 setter; 0x24E92B0 以其为窗口查找键)
        //             → 取首个申请的公会 id(无申请=0)。
        //     count → 申请中的公会数; 每条 dstr 公会名(空串时客户端显示
        //             GetStr(0x29d)="没有名字" 占位)。
        //   UI: 列表显示 count 条"公会名+状态(GetStr(0x4b4f))", 发 0x3b 事件刷新。
        public static async Task Handle_JOIN_GUILD_INFO(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            var apps = GuildSystem.GetPendingApplicationsByCid(cid);

            var w = new GamePacketWriter();
            w.WriteUInt32((uint)(apps.Count > 0 ? apps[0].GuildId : 0));
            w.WriteByte((byte)apps.Count);
            foreach (var app in apps)
                w.WriteClientDstr(app.GuildName ?? string.Empty);

            if (session != null)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    JoinGuildInfoNotificationType,
                    w.ToArray()));
            }

            FileLogger.Log(
                $"[{ProtocolName}] GUILD JOIN_GUILD_INFO cid={cid} " +
                $"raw={FormatBody(body)} → NOTI 0x0134 key={apps.FirstOrDefault().GuildId} " +
                $"count={apps.Count} " +
                $"guilds=[{string.Join(",", apps.Select(a => $"#{a.GuildId}:{a.GuildName}"))}]");
        }

        // CMD 0x02E8 REQ_GUILD_SERCH_FOR_JOIN: body=[len:u32][UTF-8 名]。
        // table1 handler 0x01132600(2026-08-30 反汇编 dnf_live3.dmp):
        //   读取 [u32 id][dstr 公会名][u16 成员数][dstr 宣传语][u32 保留][u8 N][u8×N]。
        //   → 以 dstr 公会名为键, 在 0x02F9 应答建立的缓存([0x3a5c9a8]+0xfc, 0x40B/条)
        //     中查找(0x19ef4c0); 命中 → 发 UI 事件 0x22e(缓存条目+0x20 = 工会 id)。
        //   ★ 与 0x02F9 的 [count:u8]×{[u32][dstr][dstr]} 完全不同!
        //     旧格式把 06-00 当 u16 后错位, dstr 长度读到 0x9CE60000 → 0x00D9 溢出丢弃。
        //     未命中/无结果: 发全空记录(id=0/空串/N=0), 客户端查不到名字即无动作。
        public static async Task Handle_REQ_GUILD_SERCH_FOR_JOIN(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var name = ParseNameBody(body, out var failure);
            FileLogger.Log(
                $"[{ProtocolName}] GUILD SEARCH_FOR_JOIN name=\"{name ?? "?"}\" " +
                $"raw={FormatBody(body)} parseFailure={failure ?? "none"}");
            if (session == null)
                return;

            var guilds = GuildSystem.SearchGuilds(name, 8);
            var g = guilds.FirstOrDefault();

            // ★ 搜索流程不推 0x0046: 详情框数据来自本 ack(框架格式), 且 0x0046 首次
            //   处理会弹客户端公告(0x1a46), 用户不应在搜索时看到。

            // ★ 0x02E8 ack = 独立请求-应答框架格式(弹「公会信息」框的数据源):
            //   [u8 flag=1][u32 工会id][dstr 工会名][u16 成员数][dstr 宣传语][i32 周活跃]
            //   注意: table1 的 0x01132600([u32][dstr][u16][dstr][u32][u8][u8N])
            //   会对此格式溢出并回 0x00D9 01-E8-02 — 无害噪音, 框架照常弹框。
            //   (2026-08-30 双轮抓包+垃圾值复算实证; 周活跃缺省时读到尾部残渣
            //    → 旧包-1987/新包80846, 补 i32 后归 0)
            var w = new GamePacketWriter();
            w.WriteByte((byte)(g != null ? 1 : 0));
            w.WriteUInt32((uint)(g?.GuildId ?? 0));
            w.WriteClientDstr(g?.Name ?? string.Empty);
            w.WriteUInt16((ushort)(g?.Members.Count ?? 0));
            w.WriteClientDstr(g?.Memo ?? string.Empty);
            w.WriteInt32(0);            // 周公会活跃人数
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                1,
                0x02E8,
                w.ToArray()));
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → ack 0x02E8(框架格式) " +
                $"[flag={(g != null ? 1 : 0)} id={g?.GuildId ?? 0} name=\"{g?.Name ?? ""}\" " +
                $"members={g?.Members.Count ?? 0} memo=\"{g?.Memo ?? ""}\"] key=\"{name ?? ""}\" " +
                $"uid={(session?.Player?.UserId).GetValueOrDefault()}");
        }

        // ===== NOTI 0x0046 GUILD_INFO(cmd=0, handler 0x011959B0) =====
        // 2026-08-30 完整反汇编(dnf_live3.dmp, 1003 行):
        //   包体为【纯线性读序列】, 共 39 个读原语, 无任何值依赖分支。
        //   - 一堆 jmp [eax*4+表] 是 memcpy 对齐分派(指针&0xF), 与协议无关。
        //   - 0x3a3a6a8 "条件"仅控制首次弹公告消息(0x28c14c0(0x1a46)→0xa9af50),
        //     两条路径在 0x1195F62 汇合后无条件读 #12 u8 — 旧结论(条件读包)是
        //     两次崩溃的根源(流错位→dstr 巨长→memcpy 越界)。
        //   读序列:
        //     #1  dstr 工会名
        //     #2  u8  #3  u8  #4  u16 #5  u8  #6  u32 #7  u8
        //     #8  dstr 宣传语
        //     #9  u8  #10 u8  #11 u8  #12 u8(恒有) #13 u32
        //     #14 dstr 公告   #15 dstr    #16 u16  #17 dstr
        //     ★ raw 168B(0x27c5380 固定批量读) = 「激活的公会内容」4×42B
        //     #18 u8 countA ×{ #19 u8 #20 u32 #21 u8 #22 u32 }
        //     #23 u32
        //     #24 u8 countB ×{ #25 u32 cid #26 dstr 名 #27 u8 职位 }
        //     #28 u32 #29 u8 #30 u32 #31 u8 #32..#35 u32×4 #36..#39 u32×4
        internal static async Task SendGuildInfoAsync(
            EnhancedClientSession session,
            GuildSystem.GuildInfo g)
        {
            var selfCid = session?.Player?.CharacterId ?? 0;
            // ★ #12 = 请求者在工会中的职位(反汇编: 写入 my-guild 成员记录 0x19f5070)
            //   — 客户端会长视图/设置按钮的权限开关, 会长=1, 其余按 grade(2~5)。
            var selfMember = g.Members.FirstOrDefault(m => m.CharacterId == selfCid);
            var selfPosition = NormalizeGrade(selfMember.Grade);

            var w = new GamePacketWriter();
            w.WriteClientDstr(g.Name);                                  // #1
            // ★ #2 = 公会等级(2026-09-07 参考包权威: GuildPacketBuilder.Info 第 2 写
            //   = guild.Level; 旧"疑似会长/权限标记恒 1"解读作废)。
            w.WriteByte((byte)Math.Clamp(g.Level, 0, 255));             // #2 公会等级
            w.WriteByte(0);                                             // #3
            w.WriteUInt16((ushort)Math.Min(g.Members.Count, 65535));    // #4
            w.WriteByte(0);                                             // #5
            // ★ #6 = 公会经验/里程(参考包权威: guild.Experience = guilds.exp)。
            w.WriteUInt32((uint)Math.Max(0, g.Exp));                    // #6 公会经验
            w.WriteByte(0);                                             // #7
            w.WriteClientDstr(g.Memo ?? string.Empty);                  // #8
            w.WriteByte(0);                                             // #9  → record+0x338(语义未定)
            // ★ #10 = 「有无公会基地」标志(2026-09-06 dump 定案, handler 0x011959B0
            //   的 0x01195D38 分支): !=0 → 0x19f5140 创建基地对象 UI 元素(obj+0x30);
            //   ==0 → 0x19f5170 销毁。此前写 0 → 每次推 0x0046 客户端都销毁基地 UI
            //   元素, 赛丽亚房间基地门永远失效(“能跑通但不正确”的根因)。
            //   前提: 基地对象已存在(0x009C cmd=0 先到) → 选角推送顺序 0x009C→0x0046。
            w.WriteByte(1);                                             // #10 has-agit=1
            // ★ #11 = 公会公开标志(2026-09-07 参考包权威: GuildPacketBuilder.Info 第 11 写
            //   = guild.PublicFlag(guilds.public_flag, v34 落库, 默认 0); 旧"语义未定恒 0"对齐)。
            w.WriteByte((byte)(g.PublicFlag & 0xFF));                   // #11 public_flag
            // ★ #12 = 公会图标(2026-09-07 参考包权威定案: sub_11959B0→sub_19F5070→
            //   sub_19F5280 选 guildmark_cn.img; "This is a guild emblem, NOT the
            //   viewer's membership grade")。旧"请求者职位"解读作废 — 职位索引仍由
            //   0x007F 推送(SendGuildPositionAsync), 权限检查读 0x007F 的 singleton。
            w.WriteByte((byte)(g.EmblemId & 0xFF));                     // #12 公会图标
            w.WriteUInt32(0);                                           // #13
            w.WriteClientDstr(g.Announcement ?? string.Empty);          // #14 公告(v24: 0x009A 落库真值)
            // ★ #15 = 会长名(2026-09-07 参考包权威: GuildPacketBuilder.Info 第 15 写
            //   = Truncate(master.Name,16); 旧空串作废)。
            var masterName = g.MasterName ?? string.Empty;
            if (masterName.Length > 16) masterName = masterName.Substring(0, 16);
            w.WriteClientDstr(masterName);                              // #15 会长名
            // ★ #16 = 今日签到数(参考包权威: sub_11959B0→sub_19E9B40 存 guild+184,
            //   sub_7935D0/sub_7779C0 消费为"今日签到"分子, NOT 在线人数/技能点;
            //   与 0x04EC 签到分子同源)。旧 onlineCount 解读作废。
            var todayAttendance = GuildActivityService.GetTodayAttendanceCount(g.GuildId);
            w.WriteUInt16((ushort)Math.Min(todayAttendance, 65535));    // #16 今日签到数
            w.WriteClientDstr(string.Empty);                            // #17
            WriteGradePermissionTable(w, g.GuildId);                    // ★raw 168B(0x27c5380 固定读):
                                                                        //   2026-09-01 反汇编定案(0x1196180 循环
                                                                        //   esi=0..5, edi+=0x1c):
                                                                        //   = 职位权限表 6 职位 × 28B
                                                                        //   = [u32 权限位图][24B 窄字职位名],
                                                                        //   逐职位 SetGradeBitmap(idx, u32) +
                                                                        //   SetGradeName(idx, MultiByteToWideStr
                                                                        //   (名字)) 写入 singleton+0x198+idx*36。
                                                                        //   权限检查(0x19EA3F0) =
                                                                        //   test [singleton+grade*36+0x198] &
                                                                        //        (1 << (permId & 31))。
                                                                        //   全 0 = 所有职位无任何权限
                                                                        //   ("会长没权限"的根因)。
            // ★ 内容条目 = [u8 type][u32 expiry][u8 countMode][u32 value]
            //   (2026-09-07 参考包权威 GuildPacketBuilder.Info + GuildPurchasedContent):
            //   type=(r)guild.etc col2; expiry=绝对 unix 秒, 0x7FFFFFFF=永久;
            //   countMode=1 时末位 u32 显示为剩余次数(仅 type7 组队传送);
            //   value: type7=剩余次数 / type6=扩张名额 / 其余=0。
            //   旧布局 [u8 status][u32 id][u8 type][u32 count] 作废 — 客户端读序
            //   首字节即 type, status 恒 1 → 全部显示 type1"公会经验值"
            //   (即 §14 遗留"购买内容全显示公会经验值"根因)。
            var nowUtc = DateTime.UtcNow;
            var activeContents = new List<(int ContentId, byte Type, uint Expiry, byte CountMode, uint Value)>();
            foreach (var c in g.ActivatedContents)
            {
                var contentType = GuildSystem.ContentIdToType.TryGetValue(c.ContentId, out var t) ? t : 0;
                if (contentType <= 0)
                    continue;
                uint expiry = 0x7FFFFFFF;   // 无到期=永久
                if (!string.IsNullOrEmpty(c.ExpiresAt))
                {
                    if (!DateTime.TryParseExact(
                            c.ExpiresAt, "yyyy-MM-dd HH:mm:ss",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal,
                            out var expiryUtc))
                        continue;   // 到期文本损坏: 不下发, 避免客户端误渲染
                    if (expiryUtc <= nowUtc)
                        continue;   // 已到期: 参考包 IsActive 过滤
                    expiry = (uint)new DateTimeOffset(expiryUtc, TimeSpan.Zero).ToUnixTimeSeconds();
                }
                var value = contentType is 6 or 7
                    ? (uint)(GuildSystem.ContentIdToParam.TryGetValue(c.ContentId, out var p) ? p : 0)
                    : 0u;
                activeContents.Add((c.ContentId, (byte)contentType, expiry,
                    (byte)(contentType == 7 ? 1 : 0), value));
            }
            w.WriteByte((byte)Math.Min(activeContents.Count, 255));     // #18 countA 已激活工会内容数
            foreach (var c in activeContents)
            {
                w.WriteByte(c.Type);                                      // #19 u8 content TYPE
                w.WriteUInt32(c.Expiry);                                  // #20 u32 到期(unix 秒, 0x7FFFFFFF=永久)
                w.WriteByte(c.CountMode);                                 // #21 u8 countMode(1=按次数显示)
                w.WriteUInt32(c.Value);                                   // #22 u32 次数/名额
            }
            w.WriteUInt32(0);                                           // #23
            w.WriteByte((byte)Math.Min(g.Members.Count, 255));          // #24 countB
            foreach (var m in g.Members)
            {
                w.WriteInt32(m.CharacterId);                            // #25 cid
                w.WriteClientDstr(m.CharacterName ?? string.Empty);     // #26 名
                w.WriteByte(NormalizeGrade(m.Grade));                   // #27 职位(1=会长..5=新入)
            }
            w.WriteUInt32((uint)g.Gold);                             // #28 工会金币(gf40 接 guilds.gold; ★位待 minidump 实测定案, 若非金币则移至正位)
            w.WriteByte(0);                                             // #29
            w.WriteUInt32(0);                                           // #30
            w.WriteByte(0);                                             // #31
            // ★ #32-#35 = 贡献四值(2026-09-07 参考包权威定案: GUILD_INFO 尾部 =
            //   公会累计/公会当月/个人累计/个人当月; 本端旧版写 4×u32 0, 布局恰对齐)。
            var contrib = GuildContributionService.GetTotalsForWire(g.GuildId, selfCid);
            w.WriteUInt32(contrib.GuildLifetime);                       // #32 公会累计贡献
            w.WriteUInt32(contrib.GuildMonth);                          // #33 公会当月贡献
            w.WriteUInt32(contrib.PersonalLifetime);                    // #34 个人累计贡献
            w.WriteUInt32(contrib.PersonalMonth);                       // #35 个人当月贡献
            // ★ #36/#37 = 公会频道字段(2026-09-06 逆向定案): 0x0046 handler 0x011959B0
            //   收尾(0x011967C0~0x011967CE)把 #36→controller+0xbcc(频道分组), #37→
            //   controller+0xbd0(频道号), 公会频道行 ch%02d 的 %d 即 bd0(读原语见
            //   analysis_output/disasm_0046_full.txt; setter=0x19E9F80, getter=0x19E9FA0)。
            //   bcc=服务器组号(登录通知内 serverIndex=GameNetworkConfig.ChannelServerIndex=1;
            //   单服常=1), bd0=频道号。两者必须能命中客户端本地频道对象, 否则 UI 停留
            //   "没有设置公会频道"(0xbd33, 首次实测 bcc=0 不命中)。
            w.WriteUInt32((uint)GameNetworkConfig.ChannelServerIndex); // #36 bcc 服务器组号(=1)
            w.WriteUInt32((uint)Math.Max(0, g.RecommendChannelId)); // #37 公会频道号(ch%02d)
            w.WriteUInt32(0);                                           // #38
            w.WriteUInt32(0);                                           // #39
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0046,
                w.ToArray()));
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → NOTI 0x0046 guild-info " +
                $"id={g.GuildId} name=\"{g.Name}\" members={g.Members.Count} " +
                $"selfGrade={selfPosition} " +
                $"uid={(session?.Player?.UserId).GetValueOrDefault()}");

            // ★ 职位索引通知: 必须在 0x0046(权限表)之后发, 客户端 handler
            //   (0x0118EC10) 写 [0x3b3dfb8]+0x5c14 后立即调虚函数刷新 UI —
            //   此时权限位图已就位, 权限检查(test [singleton+grade*36+0x198])生效。
            await SendGuildPositionAsync(session, selfPosition);
            // ★ 工会属性 buff 推送: NOTI 0x01E8 CHARACTER_ADD_BUFF + 0x01EA 激活
            //   variousbufflist.etc 定案(2026-09-01): 工会属性 = buff_id 167(限时)/169(永久),
            //   不是 0x02C9(客户端不消费), 166 是攻城经验值 buff(勿用)。
            await SendGuildBuffAsync(session, g);
        }

        // ===== NOTI 0x007F GUILD_POSITION(cmd=0, handler 0x0118EC10) =====
        // 包体: [u8 职位索引] → 写 [0x3b3dfb8]+0x5c14(Set+0x5c14, 全客户端唯一写点),
        //   随后调该 singleton 虚函数 +0xa9c/+0xaa0 刷新 UI。
        //   未收到时客户端默认 6(越界) → 所有权限检查失败(显示"没权限")。
        private static async Task SendGuildPositionAsync(
            EnhancedClientSession session,
            byte grade)
        {
            var w = new GamePacketWriter();
            w.WriteByte(grade);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x007F,
                w.ToArray()));
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → NOTI 0x007F grade-index={grade} " +
                $"uid={(session?.Player?.UserId).GetValueOrDefault()}");
        }

        // ===== 工会属性 buff 推送(gf43): NOTI 0x01E8 ADD_BUFF + 0x01EA 激活 =====
        // ETC 定案(2026-09-01 variousbufflist.etc):
        //   buff_id=169 = Guild_Status1 (永久公会属性, physical_attack/defence+60, magical_attack/defence+60)
        //   buff_id=167 = Guild_Status  (限时公会属性, 同上)
        //   buff_id=166 不是工会属性(是攻城经验值 buff)!
        //   content_id → (buff_id, 持续秒数)【2026-09-04 (r)guild.etc 权威定案:
        //     param 列 = buff_id, 只对 type3/5 生效; gf43 旧映射 {10,11,13,15} 全错】:
        //     1 = 永久公会属性     → buff_id=169, duration=0(永久)
        //     2 = (1天)公会属性值  → buff_id=167, duration=86400
        //     5 = (7天)公会属性值  → buff_id=167, duration=604800
        //     8 = (30天)公会属性值 → buff_id=167, duration=2592000
        //   调用时机: SendGuildInfoAsync 末尾(重登/重开界面)、0x02F8 购买成功后。
        private static readonly Dictionary<int, (int buffId, int durationSec)> ContentIdToBuffId =
            new Dictionary<int, (int, int)>
            {
                { 1, (169, 0) },        // 永久公会属性 → Guild_Status1 (永久)
                { 2, (167, 86400) },    // (1天)公会属性值 → Guild_Status (1天)
                { 5, (167, 604800) },   // (7天)公会属性值 → Guild_Status (7天)
                { 8, (167, 2592000) },  // (30天)公会属性值 → Guild_Status (30天)
            };

        private static readonly HashSet<int> GuildBuffIds = new HashSet<int> { 167, 169 };

        private static async Task SendGuildBuffAsync(
            EnhancedClientSession session,
            GuildSystem.GuildInfo guild)
        {
            if (session == null || guild == null) return;

            // 收集需要推送的 (buff_id, duration) 组合(按 content_id 逐条, 不同限时分别推)
            var buffEntries = new List<(int buffId, int duration)>();
            foreach (var c in guild.ActivatedContents)
            {
                if (c.Status == 1 && ContentIdToBuffId.TryGetValue(c.ContentId, out var tuple))
                    buffEntries.Add(tuple);
            }

            if (buffEntries.Count == 0)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD → NO guild-buff (no attribute content) " +
                    $"guildId={guild.GuildId} uid={(session?.Player?.UserId).GetValueOrDefault()}");
                return;
            }

            // 先全量移除旧的 guild buff, 再重加(防止重复层数)
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.CHARACTER_DEL_BUFF,
                SpecialDungeonNotificationBuilder.BuildCharacterRemoveBuff(
                    GuildBuffIds.ToList())));

            // 发 NOTI 0x01E8 CHARACTER_ADD_BUFF: 每条 content_id 单独一条(不同限时不同 duration)
            var allBuffIds = new HashSet<int>();
            foreach (var (bid, dur) in buffEntries)
            {
                allBuffIds.Add(bid);
                var body = SpecialDungeonNotificationBuilder.BuildCharacterAddBuff(
                    bid,    // 正确的公会属性 buff_id
                    dur,    // field1 = expire_time 秒数(0=永久)
                    0,      // field2
                    0);     // field3
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketType.CHARACTER_ADD_BUFF,
                    body));
            }

            // 发 NOTI 0x01EA CHARACTER_BUFF_DUNGEON 激活 buff
            var activateBody = SpecialDungeonNotificationBuilder.BuildCharacterBuffDungeon(
                allBuffIds.ToList());
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.CHARACTER_BUFF_DUNGEON,
                activateBody));

            FileLogger.Log(
                $"[{ProtocolName}] GUILD → guild-buff 0x01E8+0x01EA entries=[{string.Join(",", buffEntries.Select(e => $"{e.buffId}/{e.duration}"))}] " +
                $"guildId={guild.GuildId} uid={(session?.Player?.UserId).GetValueOrDefault()}");
        }

        // 职位权限表(0x0046 raw 168B = 6 职位 × 28B)。
        //   块 idx = 职位值 grade(1=会长..5=新入会员), idx0 空位。
        //   位图位 = 权限 ID(客户端按 permId & 31 索引, 位语义 2026-09-04 GLM 定案,
        //   见 Game/Guilds/GuildPermissions.cs)。
        //   ★ v21: 数据源 = guild_grade_config 表(0x02EB 打勾矩阵/0x02EC 职级更名
        //   可改), 缺行回退官方默认矩阵(GuildPermissions.DefaultGradeTable)。
        //   名字区 24B 窄字符 null 终止(UTF-8, 客户端 MultiByteToWideChar)。
        private const int GradeNameFieldBytes = 24;

        private static void WriteGradePermissionTable(GamePacketWriter w, int guildId)
        {
            var cfg = GuildSystem.GetGradeConfig(guildId);
            for (var idx = 0; idx <= 5; idx++)
            {
                var (bitmap, name) = idx == 0
                    ? (0u, string.Empty)
                    : (cfg.TryGetValue(idx, out var e)
                        ? e
                        : GuildPermissions.DefaultGradeTable[idx]);
                w.WriteUInt32(bitmap);
                var nameBytes = ClientTextEncoding.GetBytes(name ?? string.Empty);
                if (nameBytes.Length > GradeNameFieldBytes - 1)
                    Array.Resize(ref nameBytes, GradeNameFieldBytes - 1);
                w.WriteBytes(nameBytes);
                w.WriteZeroBytes(GradeNameFieldBytes - nameBytes.Length);  // 含 null 终止
            }
        }

        // CMD 0x015C REQUEST_JOIN_GUILD: 申请加入。
        // 请求(2026-09-04 GLM dump 定案) = [u8 0][dstr 工会名][dstr 申请留言];
        //   兼容早期无前缀 [dstr 工会名][dstr 留言] 写法。
        // 应答(2026-09-04 三次逆向定案, 推翻"一律无应答"旧结论):
        //   cmd=1 handler 0x111C880 是【申请人 ack 读取器】:
        //     成功 = [1][u32 工会id][dstr 工会名][dstr ""(未用)][u32 0(未用)]
        //       → 0x19f5ec0(mgr, id, 名) 把公会写进申请人本地缓存(与 0x02F2 同盟
        //         列表 handler 同一 upsert 模式) + GetStr(0x4ba3)="已申请加入公会。"提示。
        //     失败 = [0][errcode:u8] → 错误弹窗(0x12=已加入该公会/0x15=不存在/
        //       0x5f=已满/0x17=无法加入/0x9f=留言含禁词/2=通用)。
        //   旧"回包崩溃"系当年发 1 字节 [0] 畸形包: flag=0 派发器要多吃 1 字节
        //   errcode → 包不够 → 游标溢出(与 0x0160 四次闪退同根因同签名
        //   DNF+0x023C5318); "会长通知读取器/坏记录"为当时误诊(错误文案全是
        //   申请人视角, 会长通知不会校验申请人留言禁词)。
        public static async Task Handle_REQUEST_JOIN_GUILD(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            FileLogger.Log(
                $"[{ProtocolName}] GUILD REQUEST_JOIN_GUILD uid={(session?.Player?.UserId).GetValueOrDefault()} " +
                $"raw={FormatBody(body)}");

            // 定位工会名 dstr 起点: 现行客户端带 [u8 0] 前缀, 旧版不带。
            int start = 0;
            if (body != null && body.Length >= 5 && body[0] == 0
                && ParseDstrAt(body, 1, out var offProbe) != null)
                start = 1;
            var guildName = ParseDstrAt(body, start, out var off1);
            var message = guildName == null
                ? null
                : ParseDstrAt(body, off1, out _);
            FileLogger.Log(
                $"[{ProtocolName}] GUILD REQUEST_JOIN_GUILD start={start} " +
                $"guild=\"{guildName ?? "?"}\" message=\"{message ?? "?"}\"");
            if (session == null || string.IsNullOrEmpty(guildName))
                return;

            // 错误码 ack = [0][errcode](2 字节, 派发器 flag=0 时多吃 1 字节 errcode)。
            async Task SendErrAsync(byte code)
            {
                var err = new GamePacketWriter();
                err.WriteByte(0);
                err.WriteByte(code);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    1, (ushort)0x015C, err.ToArray()));
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD → ack 0x015C err=0x{code:X2} " +
                    $"guild=\"{guildName}\" uid={(session?.Player?.UserId).GetValueOrDefault()}");
            }

            // 已加入某工会 → 0x12
            var myGuild = GuildSystem.GetGuildOfCharacter(cid);
            if (myGuild != null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD REQUEST_JOIN_GUILD 已在工会 \"{myGuild.Name}\", 拒绝申请");
                await SendErrAsync(0x12);
                return;
            }

            var guild = GuildSystem.SearchGuilds(guildName, 1)
                .FirstOrDefault(g => g.Name == guildName);
            if (guild == null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD REQUEST_JOIN_GUILD 目标工会不存在: \"{guildName}\"");
                await SendErrAsync(0x15);
                return;
            }

            // 目标工会满员 → 0x5f
            if (guild.Members.Count >= guild.MemberLimit)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD REQUEST_JOIN_GUILD 目标工会已满: \"{guildName}\"");
                await SendErrAsync(0x5f);
                return;
            }

            var nameBytes = session.Player?.Name;
            var charName = "character";
            try
            {
                if (nameBytes != null && nameBytes.Length > 0)
                    charName = ClientTextEncoding.GetString(nameBytes);
            }
            catch
            {
                // 保留回退名
            }

            // ★ 单待审申请(2026-09-07 参考包权威): 一角色同时一单待审;
            //   同会重复提交幂等成功, 投向他会先取消原申请(0x17)。
            var applyResult = GuildSystem.AddApplicationEx(guild.GuildId, cid, charName, message ?? "");
            if (applyResult == GuildSystem.AddApplicationResult.PendingElsewhere)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD 申请被拒(已有别会待审): cid={cid} \"{charName}\" → \"{guildName}\"");
                await SendErrAsync(0x17);   // 无法加入(已有其他公会待审申请)
                await SendNoticeAsync(session, "已有其他公会的待审申请，请先取消原申请。");
                return;
            }
            if (applyResult != GuildSystem.AddApplicationResult.Added
                && applyResult != GuildSystem.AddApplicationResult.AlreadyPendingSame)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD 申请未记录(DB ERROR): cid={cid} \"{charName}\" → " +
                    $"{guild.Name}(id={guild.GuildId})");
                await SendErrAsync(2);
                return;
            }
            FileLogger.Log(
                $"[{ProtocolName}] GUILD 申请已记录({applyResult}): cid={cid} \"{charName}\" → " +
                $"{guild.Name}(id={guild.GuildId}) message=\"{message}\"");

            // 成功 ack = [1][u32 工会id][dstr 工会名][dstr ""][u32 0]
            //   → 申请人客户端缓存该公会 + 弹"已申请加入公会。"提示。
            var ack = new GamePacketWriter();
            ack.WriteByte(1);
            ack.WriteInt32(guild.GuildId);
            ack.WriteClientDstr(guild.Name);
            ack.WriteClientDstr(string.Empty);
            ack.WriteInt32(0);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                1, (ushort)0x015C, ack.ToArray()));
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → ack 0x015C ok id={guild.GuildId} " +
                $"name=\"{guild.Name}\" cid={cid} " +
                $"uid={(session?.Player?.UserId).GetValueOrDefault()}");

            // 主动申请仅进入 guild_applications，等待管理员通过 0x0160 列表处理。
            // 0x0093/0x0098 只属于玩家邀请，不能用于主动申请。
            if (applyResult == GuildSystem.AddApplicationResult.AlreadyPendingSame)
            {
                await SendNoticeAsync(session, "该公会的申请已在等待审批，请勿重复提交。");
            }
            else
            {
                // ★ 新申请 → 通知在线审批人(重发申请列表 + 审批提示)
                await NotifyReviewersOfNewApplicationAsync(guild, charName);
            }
        }

        // CMD 0x015D CANCEL_JOIN_GUILD: 申请人取消入会申请(2026-09-04 逆向定案)。
        // 请求 = [u32 工会id](发送点 0x0078BE6C: mkpkt(0x15D)+pkt_u32([wnd+0x1c4]),
        //   发完客户端自己紧跟 0x016D 空 body 刷新状态列表)。
        // 应答 = 【无】! table1(cmd=1) 无 0x015D 表项(回 ack 必触发 0x00D9 溢出);
        //   table2(cmd=0) 0x015D handler 0x02231720 为空 ret(推了也无害但无意义)。
        //   客户端 UI 刷新完全依赖其自发 0x016D → 服务端静默删申请即可。
        public static async Task Handle_CANCEL_JOIN_GUILD_015D(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            await Task.CompletedTask;
            var cid = session?.Player?.CharacterId ?? 0;
            FileLogger.Log(
                $"[{ProtocolName}] GUILD CANCEL_JOIN_GUILD uid={(session?.Player?.UserId).GetValueOrDefault()} " +
                $"raw={FormatBody(body)}");

            // 请求体 = [u32 工会id]; 兼容 [u8 0] 前缀写法。
            int offset;
            if (body != null && body.Length >= 5 && body[0] == 0)
                offset = 1;
            else if (body != null && body.Length == sizeof(int))
                offset = 0;
            else
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD CANCEL_JOIN_GUILD 非法请求长度(body={FormatBody(body)}), 忽略");
                return;
            }
            var guildId = BitConverter.ToInt32(body, offset);
            if (guildId <= 0)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD CANCEL_JOIN_GUILD 无法解析工会id(body={FormatBody(body)}), 忽略");
                return;
            }

            var app = GuildSystem.TakeApplication(guildId, cid);
            if (app == null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD CANCEL_JOIN_GUILD 无此申请: cid={cid} guildId={guildId}");
                return;
            }
            FileLogger.Log(
                $"[{ProtocolName}] GUILD 取消申请: cid={cid} \"{app.CharacterName}\" " +
                $"× guildId={guildId}(静默不应答, 客户端自发 0x016D 刷新)");

            // ★ 取消成功 → 刷新该会在线审批人的申请列表(2026-09-07 参考包权威:
            //   取消同样用真实列表收敛, 防审批人端残留已取消行)。
            if (Sessions != null)
            {
                foreach (var target in Sessions.GetAllGameSessions())
                {
                    var tCid = target?.Player?.CharacterId ?? 0;
                    if (tCid <= 0)
                        continue;
                    var tg = GuildSystem.GetGuildOfCharacter(tCid);
                    if (tg == null || tg.GuildId != guildId)
                        continue;
                    if (!GuildSystem.HasPermission(tCid, GuildPermissions.Recruit))
                        continue;
                    try
                    {
                        await SendJoinListAsync(target);
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log($"[{ProtocolName}] GUILD 取消后刷新审批人列表失败 cid={tCid}: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>系统公告(SERVER_NOTICE_MESSAGE)下发助手。</summary>
        private static async Task SendNoticeAsync(EnhancedClientSession session, string message)
        {
            if (session == null || string.IsNullOrEmpty(message))
                return;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE,
                ServerNoticeMessageBuilder.Build(message)));
        }

        /// <summary>
        /// 新申请提交后通知在线审批人(2026-09-07 参考包权威 RefreshGuildApplications):
        /// 向目标公会内【有收人权限(bit1)】的在线成员重发 0x0160 申请列表 + 审批提示。
        /// </summary>
        private static async Task NotifyReviewersOfNewApplicationAsync(
            GuildSystem.GuildInfo guild, string applicantName)
        {
            if (Sessions == null || guild == null)
                return;
            foreach (var target in Sessions.GetAllGameSessions())
            {
                var tCid = target?.Player?.CharacterId ?? 0;
                if (tCid <= 0)
                    continue;
                var tg = GuildSystem.GetGuildOfCharacter(tCid);
                if (tg == null || tg.GuildId != guild.GuildId)
                    continue;
                if (!GuildSystem.HasPermission(tCid, GuildPermissions.Recruit))
                    continue;
                try
                {
                    await SendJoinListAsync(target);
                    await SendNoticeAsync(target, $"玩家[{applicantName}]申请加入公会，请在申请列表中审批。");
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[{ProtocolName}] GUILD 审批人通知失败 cid={tCid}: {ex.Message}");
                }
            }
        }

        // 从 body[offset] 解析一个 [len:u32][UTF-8] dstr, 返回字符串并让 offset 前进。
        private static string ParseDstrAt(byte[] body, int offset, out int nextOffset)
        {
            nextOffset = offset;
            if (body == null || body.Length < offset + 4)
                return null;
            var len = BitConverter.ToUInt32(body, offset);
            if (len == 0 || body.Length < offset + 4 + (int)len)
                return null;
            nextOffset = offset + 4 + (int)len;
            try
            {
                return ClientTextEncoding.GetString(body, offset + 4, (int)len);
            }
            catch
            {
                return null;
            }
        }

        // CMD 0x0153 REFRESH_GUILD_INFO: 客户端打开/刷新工会窗口时必发(空包体,
        // 工会 UI 打开函数 0x775A90 依次激活控件 0x153/0x160/0x2fb/0x2f4 对应各请求)。
        // 2026-09-01 前完全未处理(日志反复 Unhandled CMD 0x0153)。
        // 客户端 cmd=1 注册表无 0x0153 handler(全代码段扫描确认) → 无需回 ack 包,
        // 正确应答 = 直接重推工会信息 NOTI 0x0046(+0x007F 职位, SendGuildInfoAsync 内置)。
        public static async Task Handle_REFRESH_GUILD_INFO_0153(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            var guild = GuildSystem.GetGuildOfCharacter(cid);
            FileLogger.Log(
                $"[{ProtocolName}] GUILD REFRESH_GUILD_INFO_0153 uid={(session?.Player?.UserId).GetValueOrDefault()} " +
                $"guild=\"{guild?.Name ?? "-"}\"");
            if (guild != null)
                await SendGuildInfoAsync(session, guild);
        }

        // CMD 0x004A GUILD_INFO: 工会详细信息请求(gf42)。
        //   客户端打开工会商店/工会属性界面时发此包, 期望返回含工会属性激活状态的工会信息。
        //   应答 = NOTI 0x0046(含 #18 countA 已激活工会内容块) + NOTI 0x007F 职位索引。
        public static async Task Handle_GUILD_INFO_004A(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            var guild = GuildSystem.GetGuildOfCharacter(cid);
            FileLogger.Log(
                $"[{ProtocolName}] GUILD GUILD_INFO_004A uid={(session?.Player?.UserId).GetValueOrDefault()} " +
                $"guild=\"{guild?.Name ?? "-"}\" contents={guild?.ActivatedContents.Count ?? 0}");
            if (guild != null)
                await SendGuildInfoAsync(session, guild);
        }

        // CMD 0x0160 GUILD_JOIN_LIST: 会长端申请列表请求(空包体, 打开申请页发, 3 秒节流)。
        // 应答必须立即全量(handler 0x113F040 先清表再逐条插入 = 替换语义, 非增量)。
        // ★ 2026-09-04 GLM dump 逐字段核对定案:
        //   [u32 count]
        //   ×count: [u32 cid][dstr 角色名][u8 职业job][u8 grow打包][u8 等级][u8 字段6][dstr 申请留言][u32 时间]
        //   - count 是 u32(首读 call 0x27c5310 存 [ebp-0x86c]=循环界)。
        //     ★旧 WriteByte(u8) → 客户端按 u32 读把首条 cid 低 3 字节并进 N → 巨数 →
        //      疯狂循环读垃圾 → "1 申请多占位/花屏"根因。历史"改 u32 N 闪退"系当时条目
        //      字段错位所致, 与本行宽度无关; 按此布局逐字段对齐即安全。
        //   - grow 打包与 0x0043 一致: (secondGrow<<4)|firstGrow, 客户端 0x19dafb0 恒等后拆
        //     (b>>4)&7 与 b&0xF。
        //   - 字段6(record+0x28)/尾 u32(record+0x4C, 疑申请时间, 列表"X小时前"用, 写 unix 秒)
        //     语义不影响布局。
        //   - 无账号分组, 逐条生行, 1 申请 = 1 行。
        //   dstr 仍使用客户端全局文本编码。
        // 临时保底开关：0x0160 真实条目触发客户端闪退期间置 true(回空列表)。
        // 2026-09-04 根因已修(cmd=1 缺框架标志字节) → false 恢复真实列表。
        private static readonly bool ForceEmptyJoinListForStability = false;

        public static async Task Handle_GUILD_JOIN_LIST(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            await SendJoinListAsync(session);
        }

        /// <summary>发送本会申请列表(0x0160)。审批/取消/新申请后复用此刷新(参考包权威:
        ///   任何审批结果(含失败与重复点击)都用真实列表收敛, 不留幽灵行)。</summary>
        private static async Task SendJoinListAsync(EnhancedClientSession session)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            var guild = GuildSystem.GetGuildOfCharacter(cid);
            var apps = guild == null
                ? new System.Collections.Generic.List<GuildSystem.JoinApplication>()
                : GuildSystem.GetApplications(guild.GuildId);
            FileLogger.Log(
                $"[{ProtocolName}] GUILD JOIN_LIST uid={(session?.Player?.UserId).GetValueOrDefault()} " +
                $"guild=\"{guild?.Name ?? "-"}\" 申请人={apps.Count}");

            // ★调试后门: 工作目录存在 guild_probe.txt 时，本次 0x0160 应答改用文件内容。
            //   每行一个条目：applicantCid,fieldA,fieldB,fieldC,fieldD,name,message,value
            //   字段名暂不代表最终语义，仅用于单字段探针。申请者 CID 必须是真实 Pending 申请的角色 ID，
            //   不能复用打开列表的会长 CID，否则 0x015E/0x015F 无法定位条目。
            //   value 是最后 DWORD 的原始探针值；文件用后即焚(重命名 .done)，下次请求恢复正式数据库条目。
            var probePath = System.IO.Path.Combine(
                System.IO.Directory.GetCurrentDirectory(), "guild_probe.txt");
            var probeEntries = new System.Collections.Generic.List<(
                int ApplicantCid, byte FieldA, byte FieldB, byte FieldC, byte FieldD,
                string Name, string Message, uint Value)>();
            try
            {
                if (System.IO.File.Exists(probePath))
                {
                    foreach (var raw in System.IO.File.ReadAllLines(probePath))
                    {
                        if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith("#"))
                            continue;
                        var p = raw.Split(',');
                        if (p.Length != 8 ||
                            !int.TryParse(p[0], out var applicantCid) || applicantCid <= 0)
                        {
                            FileLogger.Log(
                                $"[{ProtocolName}] GUILD JOIN_LIST 忽略无效探针行: {raw}");
                            continue;
                        }
                        probeEntries.Add((
                            applicantCid,
                            byte.TryParse(p[1], out var fieldA) ? fieldA : (byte)0,
                            byte.TryParse(p[2], out var fieldB) ? fieldB : (byte)0,
                            byte.TryParse(p[3], out var fieldC) ? fieldC : (byte)0,
                            byte.TryParse(p[4], out var fieldD) ? fieldD : (byte)0,
                            p[5], p[6],
                            uint.TryParse(p[7], out var value) ? value : 0u));
                    }
                    System.IO.File.Move(probePath, probePath + ".done", true);
                    FileLogger.Log(
                        $"[{ProtocolName}] GUILD JOIN_LIST 调试覆盖: {probeEntries.Count} 条(来自 guild_probe.txt)");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] GUILD JOIN_LIST guild_probe.txt 读取失败: {ex.Message}");
            }

            // ★ cmd=1 框架标志(2026-09-04 GLM 崩溃定案)：cmd=1 派发器调 table1 handler
            //   前【无条件先从全局游标吃 1 字节】作 [ebp+0xc] 开关(=0 → handler 整体跳过)。
            //   故一切 cmd=1 下行 body 必须以标志字节开头(0x0043/0x02E8 本就有=“框架格式”)。
            //   缺它时本包 u32 count 低字节被吃 → handler 从 body+1 读 →
            //   count=(cid 低字节)<<24(cid=10 → 0x0A000000≈1.67 亿) → 巨循环越过 0x30
            //   护栏扫出会话缓冲 → read_u32(0x113F17C)撞未映射页 =
            //   4 次同签名闪退(DNF+0x023C5318)真根因；旧 u8 构建"1 申请多占位"同因
            //   (flag=1, count=cid=7 → 7 条垃圾行)；N=0 不崩(flag=0 跳过, 但列表也不清)。
            var forceEmptyJoinList = ForceEmptyJoinListForStability;
            var w = new GamePacketWriter();
            w.WriteByte(1);                                // cmd=1 框架标志
            if (probeEntries.Count > 0)   // 探针优先于保底开关：用于逐字段定位闪退诱因
            {
                var probeCount = Math.Min(probeEntries.Count, 200);
                w.WriteUInt32((uint)probeCount);   // ★ N 为 u32(2026-09-04 GLM 定案)
                foreach (var e in probeEntries.Take(probeCount))
                {
                    w.WriteInt32(e.ApplicantCid);            // 申请者 cid，供 0x015E/0x015F 定位
                    w.WriteClientDstr(e.Name);
                    w.WriteByte(e.FieldA);
                    w.WriteByte(e.FieldB);
                    w.WriteByte(e.FieldC);
                    w.WriteByte(e.FieldD);
                    w.WriteClientDstr(e.Message);
                    w.WriteUInt32(e.Value);
                    FileLogger.Log(
                        $"[{ProtocolName}] GUILD JOIN_LIST probe cid={e.ApplicantCid} " +
                        $"A={e.FieldA} B={e.FieldB} C={e.FieldC} D={e.FieldD} " +
                        $"name=\"{e.Name}\" value={e.Value}");
                }
            }
            else if (forceEmptyJoinList)
            {
                w.WriteUInt32(0);
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD JOIN_LIST 临时回空(防闪退, 实际申请人={apps.Count})");
            }
            else
            {
                var entryCount = Math.Min(apps.Count, 200);
                w.WriteUInt32((uint)entryCount);   // ★ N 为 u32(2026-09-04 GLM 定案);
                                                     //   旧 WriteByte(u8) → 客户端按 u32 读时
                                                     //   把首条 cid 低 3 字节并进 N → 巨数 →
                                                     //   "1 申请多占位"根因。
                foreach (var app in apps.Take(entryCount))
                {
                    var job = GuildSystem.GetJobOfCharacter(app.CharacterId);
                    var clientJob = job == 12 ? 13 : job;
                    var grow = GuildSystem.GetGrowTypeOfCharacter(app.CharacterId);
                    var level = Math.Max(1, Math.Min(
                        GuildSystem.GetLevelOfCharacter(app.CharacterId), byte.MaxValue));
                    var appliedSeconds = (uint)Math.Min(
                        Math.Max(1, (DateTime.Now - app.Time).TotalSeconds), uint.MaxValue);

                    w.WriteInt32(app.CharacterId);               // 申请者 cid，审批定位键
                    w.WriteClientDstr(app.CharacterName ?? "");
                    w.WriteByte((byte)Math.Max(0, Math.Min(clientJob, byte.MaxValue)));
                    w.WriteByte((byte)((((grow >> 4) & 0x7) << 4) | (grow & 0xF)));
                    w.WriteByte((byte)level);
                    w.WriteByte(0);                               // 第四字段，待探针确认
                    w.WriteClientDstr(app.Message ?? "");
                    w.WriteUInt32(appliedSeconds);                // 距申请的秒数
                    FileLogger.Log(
                        $"[{ProtocolName}] GUILD JOIN_LIST entry cid={app.CharacterId} " +
                        $"A={clientJob} B=0x{grow:X2} C={level} D=0 " +
                        $"value={appliedSeconds} name=\"{app.CharacterName}\"");
                }
            }
            var bodyBytes = w.ToArray();
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                1,
                0x0160,
                bodyBytes));
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → ack 0x0160 join-list N={(bodyBytes.Length >= 4 ? BitConverter.ToUInt32(bodyBytes, 0) : 0)} " +
                $"raw={BitConverter.ToString(bodyBytes)} " +
                $"uid={(session?.Player?.UserId).GetValueOrDefault()}");
        }

        // CMD 0x0097 GUILD_INVITE: 会长端"邀请加入"(2026-09-01 07:53 双端实测)。
        // 包体: [len:u32][UTF-8 对方角色名](实测 0F-00-00-00+"麦哲伦大哥")。
        // 服务端: 校验后给被邀请者会话推 NOTI 0x0093(邀请弹窗, table2 handler 0x01196CD0):
        //   读序列 dstr(0x32)/dstr(0x80)/dstr(0x100)/u32/u16/[u8 N][N bytes] →
        //   [dstr 邀请者名][dstr 工会名][dstr 备注][u32 工会id][u16 未知][u8 0]。
        //   弹窗字段槽位若与实测显示不符, 交换前两个 dstr 即可。
        // 失败 → 邀请者推 NOTI 0x0094 [u8 code](handler 0x0118EE40:
        //   code<2 时还会读 [dstr 名]; code=0 → GetStr(0x19ab) 格式化弹消息;
        //   code=1 静默; code≥2 → 错误弹窗 0x27→专属串/0x68→专属串/其他→通用串 0x346)。
        public static async Task Handle_GUILD_INVITE_0097(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var inviterCid = session?.Player?.CharacterId ?? 0;
            var inviterName = GetSessionCharacterName(session);
            var targetName = ParseNameBody(body, out var failure);
            FileLogger.Log(
                $"[{ProtocolName}] GUILD INVITE_0097 inviter=\"{inviterName}\"({inviterCid}) " +
                $"target=\"{targetName ?? "?"}\" raw={FormatBody(body)} " +
                $"parseFailure={failure ?? "none"}");
            if (session == null || string.IsNullOrEmpty(targetName))
                return;

            var guild = GuildSystem.GetGuildOfCharacter(inviterCid);
            if (guild == null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD INVITE_0097 拒绝: 邀请者不在工会 cid={inviterCid}");
                await SendInviteResultAsync(session, 2);
                return;
            }

            // ★ 权限校验(2026-09-04 GLM 位语义): 邀请需 bit1(Recruit 收人)
            if (!GuildSystem.HasPermission(inviterCid, GuildPermissions.Recruit))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD INVITE_0097 拒绝: cid={inviterCid} 无收人权限(bit1)");
                await SendInviteResultAsync(session, 2);
                return;
            }

            var targetCid = GuildSystem.FindCharacterIdByName(targetName);
            if (targetCid <= 0)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD INVITE_0097 拒绝: 目标角色不存在 \"{targetName}\"");
                await SendInviteResultAsync(session, 0x68);
                return;
            }

            if (GuildSystem.GetGuildOfCharacter(targetCid) != null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD INVITE_0097 拒绝: 目标已在工会 " +
                    $"\"{targetName}\"({targetCid})");
                await SendInviteResultAsync(session, 0x27);
                return;
            }

            if (Sessions == null || !Sessions.TryGet(targetCid, out var targetSession))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD INVITE_0097 拒绝: 目标不在线 " +
                    $"\"{targetName}\"({targetCid})");
                await SendInviteResultAsync(session, 0x68);
                return;
            }

            PendingInvites[targetCid] = (guild.GuildId, inviterCid);

            // 字段语义经 2026-09-01 双端实测校准(弹窗显示"公会名:哈哈哈/成员0人/活跃1人"后修正):
            // #1=公会名(显示在"公会名:") #2=邀请者名(顶部大字) #3=宣传信息
            // u32=周活跃人数(原发工会id=1被显示为"活跃1人") u16=成员数 N=激活内容数
            var w = new GamePacketWriter();
            w.WriteClientDstr(guild.Name);                        // #1 公会名
            w.WriteClientDstr(inviterName);                      // #2 邀请者名
            w.WriteClientDstr(guild.Memo ?? string.Empty);       // #3 宣传信息
            w.WriteUInt32(0);                                     // #4 周活跃人数
            w.WriteUInt16((ushort)guild.Members.Count);          // #5 成员数
            w.WriteByte(0);                                       // #6 N=0
            await targetSession.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0093,
                w.ToArray()));
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → NOTI 0x0093 invite " +
                $"guild=\"{guild.Name}\"(id={guild.GuildId}) inviter=\"{inviterName}\" " +
                $"target=\"{targetName}\"({targetCid})");
        }

        // NOTI 0x0094 邀请结果通知(发给邀请者)。code<2 时客户端会再读 [dstr 名]。
        private static async Task SendInviteResultAsync(
            EnhancedClientSession session,
            byte code,
            string name = null)
        {
            var w = new GamePacketWriter();
            w.WriteByte(code);
            if (code < 2)
                w.WriteClientDstr(name ?? string.Empty);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0094,
                w.ToArray()));
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → NOTI 0x0094 invite-result code={code} " +
                $"name=\"{name ?? ""}\" uid={(session?.Player?.UserId).GetValueOrDefault()}");
        }

        // CMD 0x009A MODIFY_GUILD_ANNOUNCEMENT(编辑公告)。
        // 权威源码对齐(2026-09-06 他人 MD §修复-编辑公告; 另一套 86A21 源码定案):
        //   body=[len:u32][UTF-8 公告文本], 权限 bit4(Announce), 落 guilds.announcement(v24);
        //   公告可显式清空(空串)。长度 ≤100 UTF-16 单元 / ≤255 字节, 拒绝非法控制字符。
        // 成功:
        //   1) ack cmd=1 [01](table1 0x009A handler 存在, ack 安全);
        //   2) 全会在线成员(含发送者)重推 NOTI 0x0046 GUILD_INFO(#14 公告);
        //   3) NOTI 0x008D 即时刷新 —— 恰好【单个 UTF-8 DSTR 公告文本】。
        //   ★ 0x0046 handler(0x011959B0)对公告的赋值受 byte_3A3A6A8 门控 = 同登录仅首次
        //     生效 → 只重推 0x0046 刷不动已打开的公告框; 0x008D(单 DSTR)才是即时刷新入口。
        //     旧"工会消息[发送者名][消息]双 DSTR"实现与权威源码冲突(客户端会把第一段
        //     DSTR 当公告读), 已废弃。
        public static async Task Handle_NOTIFY_MESSAGE_TO_GUILD_009A(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var senderCid = session?.Player?.CharacterId ?? 0;
            var senderName = GetSessionCharacterName(session);
            var message = ParseNameBodyFlexible(body, out var failure);
            FileLogger.Log(
                $"[{ProtocolName}] GUILD 009A(公告编辑) RECV sender=\"{senderName}\"({senderCid}) " +
                $"text=\"{message ?? "?"}\" raw={FormatBody(body)} len={body?.Length ?? 0} " +
                $"parseFailure={failure ?? "none"}");
            if (session == null)
                return;

            var guild = GuildSystem.GetGuildOfCharacter(senderCid);
            if (guild == null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD 009A 拒绝: 发送者不在工会 cid={senderCid}");
                return;
            }

            // 权限: 公告编辑需 bit4(Announce)。
            if (!GuildSystem.HasPermission(senderCid, GuildPermissions.RosterEdit))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD 009A 拒绝: 无 bit4(公告编辑)权限 cid={senderCid}");
                return;
            }

            // 公告允许显式清空(空文本 → 清空公告)。
            var announcement = message ?? string.Empty;
            if (announcement.Length > GuildSystem.MaxAnnouncementChars
                || Encoding.UTF8.GetByteCount(announcement) > GuildSystem.MaxGuildTextWireBytes
                || !IsSafeText(announcement))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD 009A 拒绝: 公告超限或含非法控制字符 " +
                    $"len={announcement.Length} " +
                    $"bytes={Encoding.UTF8.GetByteCount(announcement)}");
                return;
            }

            if (!GuildSystem.UpdateAnnouncement(senderCid, announcement, out var guildId))
                return;

            FileLogger.Log(
                $"[{ProtocolName}] GUILD 009A 公告保存成功 guild={guildId} " +
                $"ann=\"{announcement}\" by \"{senderName}\"({senderCid})");

            // 1) ack cmd=1 [01]
            await SendRawAckAsync(session, 0x009A, new byte[] { 1 },
                "009A 公告已保存");

            // 2) 全会在线成员(含发送者)重推 NOTI 0x0046(#14 公告)
            await BroadcastGuildInfoAsync(guild);

            // 3) NOTI 0x008D 单 DSTR 即时刷新已打开的公告框
            var notiW = new GamePacketWriter();
            notiW.WriteClientDstr(announcement);
            var notiBody = notiW.ToArray();
            int onlineCount = 0;
            foreach (var m in guild.Members)
            {
                if (Sessions != null && Sessions.TryGet(m.CharacterId, out var ms) && ms != null)
                {
                    try
                    {
                        await ms.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                            0x00,
                            0x008D,
                            notiBody));
                        onlineCount++;
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log(
                            $"[{ProtocolName}] GUILD 009A 0x008D 推送失败 " +
                            $"cid={m.CharacterId}: {ex.Message}");
                    }
                }
            }

            FileLogger.Log(
                $"[{ProtocolName}] GUILD 009A → ack[01] + 0x0046 全会 + 0x008D " +
                $"guild=\"{guild.Name}\"(id={guildId}) " +
                $"online={onlineCount}/{guild.Members.Count} ann=\"{announcement}\"");
        }

        // CMD 0x0098 GUILD_INVITE_REPLY: 被邀请者对 0x0093 弹窗的答复。
        // 包体(2026-09-01 08:42 实测): [u8 result](01=同意, 00=拒绝)。
        // 同意 → AddMember(grade=5 新入会员) + 邀请者 0x0094 code=1[名](客户端静默) +
        //        被邀请者 NOTI 0x0046/0x007F(客户端入会状态刷新);
        // 拒绝 → 邀请者 0x0094 code=0[名](弹"玩家%s拒绝了您的公会邀请")。
        // ★2026-09-01 活体反汇编 handler 0x0118EE40 定案修正(原实现反了):
        //   code=0 → GetStr(0x19ab)+名 格式化弹窗(实测"麦哲伦大哥拒绝了您的公会邀请")
        //   code=1 → 静默结束(同意时邀请者看到新成员入会即可)
        //   code≥2 → 错误弹窗(0x27/0x68 专属串, 其他通用串), 且不再读 [dstr 名]。
        public static async Task Handle_GUILD_INVITE_REPLY_0098(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            FileLogger.Log(
                $"[{ProtocolName}] GUILD INVITE_REPLY_0098 uid={(session?.Player?.UserId).GetValueOrDefault()} " +
                $"raw={FormatBody(body)}");
            if (session == null || body == null || body.Length < 1)
                return;
            var accepted = body[0] != 0;

            if (!PendingInvites.TryRemove(cid, out var invite))
            {
                // 0x0098 只接受由 0x0097 创建的玩家邀请答复。
                // 主动申请应由管理员在列表中发送 0x015E/0x015F 处理。
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD INVITE_REPLY_0098 无待处理玩家邀请, 忽略 cid={cid}");
                return;
            }

            if (!accepted)
            {
                // 拒绝 → 邀请者弹"玩家%s拒绝了您的公会邀请"
                if (Sessions != null && Sessions.TryGet(invite.InviterCid, out var inviterS))
                    await SendInviteResultAsync(inviterS, 0, GetSessionCharacterName(session));
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD 邀请被拒绝: cid={cid} → 工会id={invite.GuildId} " +
                    $"(已通知邀请者 cid={invite.InviterCid})");
                return;
            }

            var name = GetSessionCharacterName(session);
            var added = GuildSystem.AddMember(invite.GuildId, cid, name, GuildSystem.GradeNewbie);
            if (!added)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD INVITE_REPLY_0098 入会失败(已在工会/工会无效) " +
                    $"cid={cid} guildId={invite.GuildId}");
                if (Sessions != null && Sessions.TryGet(invite.InviterCid, out var inviterF))
                    await SendInviteResultAsync(inviterF, 0x68);
                return;
            }

            if (Sessions != null && Sessions.TryGet(invite.InviterCid, out var inviter))
                await SendInviteResultAsync(inviter, 1, name); // 同意 → code=1(静默)

            var guild = GuildSystem.GetGuildOfCharacter(cid);
            if (guild != null)
                await SendGuildInfoAsync(session, guild);
            FileLogger.Log(
                $"[{ProtocolName}] GUILD 邀请已接受入会: cid={cid} \"{name}\" → " +
                $"\"{guild?.Name}\"(id={invite.GuildId})");
        }

        // CMD 0x015E GUILD_ACCEPT_APPLY: 会长批准申请列表中的申请。
        // 请求 = [u8 0][u32 申请人cid](勾选多条时逐条各发一包)。见 HandleApplyDecision。
        // ack(handler 0x113B490 cmd=1): [u32 cid] — 会长端按 cid 移除该条。
        // 申请者入会链(2026-09-04 GLM 定案): 0x0047 → 0x0046 → 0x007F(勿再回
        //   NOTI 0x015E, 那是商店 NOTI 会被误解析)。
        public static async Task Handle_GUILD_ACCEPT_APPLY_015E(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            await HandleApplyDecision(session, body, accept: true);
        }

        // CMD 0x015F GUILD_REJECT_APPLY: 会长拒绝申请(格式与 0x015E 同源, 0x778B86 分支)。
        public static async Task Handle_GUILD_REJECT_APPLY_015F(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            await HandleApplyDecision(session, body, accept: false);
        }

        // 0x015E/0x015F 共用处理(2026-09-04 GLM 权威定案):
        //   请求包体 = [u8 0][u32 申请人cid](勾选多条时客户端逐条各发一包)。
        //   批准(0x015E): 服务端回 ack [u32 cid](handler 0x113B490 按 cid 从申请容器
        //     移除该条)→ 申请者端入会落地链 = NOTI 0x0047[置 my-guild-id] → 0x0046
        //     → 0x007F(职位)。工会成员侧重推 0x0043。
        //   拒绝(0x015F): 客户端【无 ack handler】, 发完自己本地删条目 →
        //     服务端【不回任何 0x015F 应答】; 申请者也【无注册通知】= 官方静默拒绝,
        //     服务端不发任何包。
        private static async Task HandleApplyDecision(
            EnhancedClientSession session,
            byte[] body,
            bool accept)
        {
            var label = accept ? "ACCEPT_APPLY_015E" : "REJECT_APPLY_015F";
            var cid = session?.Player?.CharacterId ?? 0;
            FileLogger.Log(
                $"[{ProtocolName}] GUILD {label} uid={(session?.Player?.UserId).GetValueOrDefault()} " +
                $"raw={FormatBody(body)}");
            if (session == null)
                return;

            // 请求体 = [u8 0][u32 申请人cid]; 兼容早期纯 [u32 cid](4B) 兜底。
            int offset;
            if (body != null && body.Length >= 5 && body[0] == 0)
                offset = 1;                       // 现行客户端带 [u8 0] 前缀
            else if (body != null && body.Length == sizeof(int))
                offset = 0;                       // 兼容旧格式
            else
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD {label} 非法请求长度(body={FormatBody(body)}), 本次仅记录");
                return;
            }
            var targetCid = BitConverter.ToInt32(body, offset);
            if (targetCid <= 0)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD {label} 无法解析申请人cid(body={FormatBody(body)}), 本次仅记录");
                return;
            }

            // ★ 权限校验(2026-09-04 GLM 位语义): 批准/拒绝申请需 bit1(Recruit 收人)
            if (!GuildSystem.HasPermission(cid, GuildPermissions.Recruit))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD {label} 拒绝: cid={cid} 无收人权限(bit1)");
                return;
            }

            var guild = GuildSystem.GetGuildOfCharacter(cid);
            if (guild == null)
            {
                FileLogger.Log($"[{ProtocolName}] GUILD {label} 拒绝: 操作者不在工会 cid={cid}");
                return;
            }

            var app = GuildSystem.GetApplication(guild.GuildId, targetCid);
            if (app == null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD {label} 无此申请记录: cid={targetCid} " +
                    $"guild=\"{guild.Name}\"(id={guild.GuildId})");
                return;
            }

            // 2026-09-04: 申请人已加入别的工会(陈旧申请) → 撤销该条再退出。
            //   官方此情形回 errcode 0x27「玩家[%s]已属于公会成员」(0x015E 失败 ack 读序
            //   待 GLM 定案, 暂只清理+明确日志; 否则会长点批准只看到"没反应")。
            var applicantGuild = accept ? GuildSystem.GetGuildOfCharacter(targetCid) : null;
            if (applicantGuild != null)
            {
                GuildSystem.WithdrawAllApplications(targetCid);
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD {label} 申请人已在别会, 已撤销其待审申请: " +
                    $"cid={targetCid} \"{app.CharacterName}\" 现属 " +
                    $"\"{applicantGuild.Name}\"(id={applicantGuild.GuildId})");
                // 列表已变化(陈旧申请被撤销) → 用真实列表收敛审批人界面
                await SendJoinListAsync(session);
                return;
            }

            var processed = GuildSystem.ProcessApplication(
                guild.GuildId, targetCid, cid, accept, out app);
            if (!processed)
            {
                var reason = accept
                    ? (guild.Members.Count >= guild.MemberLimit ? "工会已满员" : "未知")
                    : "拒绝流程失败";
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD {label} 处理失败({reason}): cid={targetCid} " +
                    $"guild=\"{guild.Name}\" 成员={guild.Members.Count}/{guild.MemberLimit}");
                // ★ 失败也用真实列表收敛(参考包权威: 含失败与重复点击, 防幽灵行)
                await SendJoinListAsync(session);
                return;
            }
            FileLogger.Log(
                $"[{ProtocolName}] GUILD {(accept ? "批准" : "拒绝")}申请: cid={targetCid} " +
                $"\"{app.CharacterName}\" {(accept ? "→" : "×")} \"{guild.Name}\"(id={guild.GuildId})");

            // 拒绝: 申请者无通知(官方静默); 但审批人端重发真实列表收敛
            //   (客户端本地乐观删行, 重复点击/失败会留幽灵行 — 参考包权威: 总是回真实列表)。
            if (!accept)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD → 静默拒绝(仅刷新审批人列表) cid={targetCid} " +
                    $"uid={(session?.Player?.UserId).GetValueOrDefault()}");
                await SendJoinListAsync(session);
                return;
            }

            // ── 批准路径 ──
            // 会长端 ack: [u8 框架标志=1][u32 cid](cmd=1, handler 0x113B490 按 cid 移除该条)。
            var ack = new GamePacketWriter();
            ack.WriteByte(1);        // cmd=1 框架标志(派发器先吃 1 字节, 见 0x0160 注释)
            ack.WriteInt32(targetCid);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                1, (ushort)0x015E, ack.ToArray()));

            // 申请者端入会落地链(2026-09-04 GLM 定案, 与创建时认证成员入会一致):
            //   0x0047 置 my-guild-id+弹提示 → 0x0046 工会信息 → 0x007F 职位索引。
            if (Sessions != null && Sessions.TryGet(targetCid, out var target))
            {
                var g = GuildSystem.GetGuildOfCharacter(targetCid);
                if (g != null)
                {
                    await SendGuildCreatedAsync(target, g);
                    await SendGuildInfoAsync(target, g);
                }
            }

            // 工会成员侧刷新成员列表(新成员入会): 会长/其他在线成员各推一次 0x0043。
            await PushMemberListToOnlineAsync(guild.GuildId, exceptCid: targetCid);

            // ★ 审批人端重发真实申请列表(参考包权威: 审批后用真实列表收敛;
            //   多条待审时客户端只按 cid 移除当前条, 其余行以服务端为准)。
            await SendJoinListAsync(session);

            FileLogger.Log(
                $"[{ProtocolName}] GUILD → ack 0x015E + 申请者入会链 + 0x0043 刷新 " +
                $"cid={targetCid} uid={(session?.Player?.UserId).GetValueOrDefault()}");
        }

        // 向工会全体在线成员推送一次 0x0043 成员列表(每次应答按接收者构建频道/职位)。
        // exceptCid: 跳过的成员(如刚被拉入会者, 其走 0x0047 独立入会链)。
        private static async Task PushMemberListToOnlineAsync(int guildId, int exceptCid)
        {
            var guild = GuildSystem.GetGuildById(guildId);
            if (guild == null || Sessions == null)
                return;
            foreach (var m in guild.Members)
            {
                if (m.CharacterId == exceptCid)
                    continue;
                if (!Sessions.TryGet(m.CharacterId, out var s) || s == null)
                    continue;
                try
                {
                    var w = new GamePacketWriter();
                    WriteMemberListBody(w, s, guild, m.CharacterId);
                    await s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00, (ushort)0x0043, w.ToArray()));
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] GUILD 0x0043-push to cid={m.CharacterId} failed: {ex.Message}");
                }
            }
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → 0x0043 member-list refresh guild=\"{guild.Name}\"(id={guildId})");
        }

        // 会话角色名(Player.Name 为 UTF-8 字节)。
        private static string GetSessionCharacterName(EnhancedClientSession session)
        {
            try
            {
                var nameBytes = session?.Player?.Name;
                if (nameBytes != null && nameBytes.Length > 0)
                    return ClientTextEncoding.GetString(nameBytes);
            }
            catch
            {
                // 回退名
            }
            return "character";
        }

        // ══════ 工会频道(已实现) / 公会基地(AGIT)抓包探针 (2026-09-06) ══════
        //
        // 工会频道 0x033B: 见 Handle_SET_GUILD_RECOMMAND_CHANNEL_033B(table1 handler
        //   0x0111F340 逆向定案 [u8 flag][u8 reason], 2026-09-06 正式化)。
        //
        // 公会基地 4 个 CMD 仍为探针(只记日志一律不回包, 猜协议回包 = 客户端闪退):
        //   opcode 取自 Network/Core/PacketTypes.cs(带权威值, 非猜测):
        //         CMD  CHECK_CREATE_GUILD_AGIT     = 0x00E3
        //         CMD  CREATE_GUILD_AGIT           = 0x00E4
        //         CMD  DELETE_GUILD_AGIT           = 0x00E5
        //         CMD  UPGRADE_GUILD_AGIT          = 0x00E7
        //         NOTI GUILD_AGIT_INFO             = 0x00BF(下行, 不注册)
        //
        // 客户端文案出处(analysis_output/zz_msg_guild.txt), 证明客户端确有这些功能:
        //   0xBD32 "公会频道 : ch%02d.%s"   0xBD34 "确定要移动至公会频道么？"
        //   0xBD36 "只有公会长才能设置"      0xBD42 "只能将普通区域的频道…设为公会频道"
        //   0x53EC "已生成公会基地, 可以从赛丽亚房间进入公会基地"
        //   0x53FE "当前公会基地 : %d阶段"   0x5402 "公会基地已经升级至%d阶段"
        private static Task ProbeLogAsync(
            string label,
            EnhancedClientSession session,
            byte[] body)
        {
            FileLogger.Log(
                $"[{ProtocolName}] GUILD PROBE {label} " +
                $"cid={(session?.Player?.CharacterId).GetValueOrDefault()} " +
                $"len={body?.Length ?? 0} raw={FormatBody(body)}");
            return Task.CompletedTask;
        }

        // CMD 0x033B SET_GUILD_RECOMMAND_CHANNEL: 会长把【当前所在频道】设为公会频道。
        //   2026-09-06 抓包: 请求 body 空(频道隐含于会话)。应答格式经 dnf_hang.dmp table1
        //   反汇编定案(handler 0x0111F340, obj=0x0CA1E390): body = [u8 flag][u8 reason]。
        //     flag!=0           → 静默返回(成功, 客户端不弹窗, 设置生效)
        //     flag==0,reason=7  → 弹 0xbd42 "只能将普通区域的频道设为公会频道"
        //     flag==0,reason=19 → 弹 0xbd36 "只有公会长才能设置"
        //     flag==0,其他     → 无弹窗
        //   ★ handler 存在于 cmd=1 应答表 → 应答安全(区别于 0x02B3 无 handler 不可 ack)。
        public static async Task Handle_SET_GUILD_RECOMMAND_CHANNEL_033B(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            var guild = GuildSystem.GetGuildOfCharacter(cid);
            if (guild == null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD 0x033B 忽略: cid={cid} 不在会内");
                return;
            }
            var member = guild.Members.FirstOrDefault(m => m.CharacterId == cid);
            if (member.CharacterId != cid || member.Grade != GuildSystem.GradeMaster)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD 0x033B 拒绝: cid={cid} 非会长(grade={member.Grade})");
                await SendRawAckAsync(session, 0x033B, new byte[] { 0, 0x13 },
                    $"033B 非会长设置[拒绝 reason=0x13]");
                return;
            }
            if (GameNetworkConfig.IsFreeDuelListener(session.ListenerPort))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GUILD 0x033B 拒绝: cid={cid} 当前在自由决斗频道");
                await SendRawAckAsync(session, 0x033B, new byte[] { 0, 0x07 },
                    $"033B 非普通频道[拒绝 reason=0x07]");
                return;
            }
            var myChannelId = GameNetworkConfig.ResolveGameChannel(
                session.ListenerPort).ChannelId;
            FileLogger.Log(
                $"[{ProtocolName}] GUILD 0x033B 设置公会频道 gid={guild.GuildId} " +
                $"\"{guild.Name}\" → ch{myChannelId}(port={session.ListenerPort}) " +
                $"by master cid={cid}");
            // 记录 → 广播 0x0046(带 #37=频道号, 全会成员 UI 刷新"公会频道"行) → 应答
            // flag=1(客户端静默接受)。(逆向: ack handler 0x0111F340 成功不弹窗;
            //  可见反馈全靠 0x0046 #36/#37 → controller +0xbcc/+0xbd0 驱动, 见
            //  SendGuildInfoAsync 注释与 analysis_output/disasm_0046_full.txt。)
            guild.RecommendChannelId = myChannelId;
            await BroadcastGuildInfoAsync(guild);
            await SendRawAckAsync(session, 0x033B, new byte[] { 1 },
                $"033B 已设 ch{myChannelId} 广播0x0046");
        }

        /// <summary>CMD 0x00E3 CHECK_CREATE_GUILD_AGIT: 创建公会基地前置检查(探针)。</summary>
        public static Task Handle_PROBE_CHECK_CREATE_AGIT_00E3(
            EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => ProbeLogAsync("0x00E3 CHECK_CREATE_GUILD_AGIT", session, body);

        /// <summary>CMD 0x00E4 CREATE_GUILD_AGIT: 创建公会基地(探针)。</summary>
        public static Task Handle_PROBE_CREATE_AGIT_00E4(
            EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => ProbeLogAsync("0x00E4 CREATE_GUILD_AGIT", session, body);

        /// <summary>CMD 0x00E5 DELETE_GUILD_AGIT: 删除公会基地(探针)。</summary>
        public static Task Handle_PROBE_DELETE_AGIT_00E5(
            EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => ProbeLogAsync("0x00E5 DELETE_GUILD_AGIT", session, body);

        /// <summary>CMD 0x00E7 UPGRADE_GUILD_AGIT: 升级公会基地阶段(探针)。</summary>
        public static Task Handle_PROBE_UPGRADE_AGIT_00E7(
            EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => ProbeLogAsync("0x00E7 UPGRADE_GUILD_AGIT", session, body);

        private static async Task LogAndConsumeAsync(
            string label,
            EnhancedClientSession session,
            byte[] body)
        {
            FileLogger.Log(
                $"[{ProtocolName}] GUILD {label} uid={(session?.Player?.UserId).GetValueOrDefault()} " +
                $"raw={FormatBody(body)}");
            await Task.CompletedTask;
        }

        // NOTI 0x02AE REPLY_GUILD_CREATE_PERMIT: [resultCode:u8](严格单字节)。
        private static async Task SendPermitResultAsync(
            EnhancedClientSession session,
            byte resultCode)
        {
            var w = new GamePacketWriter();
            w.WriteByte(resultCode);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.REPLY_GUILD_CREATE_PERMIT,
                w.ToArray()));
            FileLogger.Log(
                $"[{ProtocolName}] GUILD → NOTI 0x02AE permitResult={resultCode} " +
                $"uid={(session?.Player?.UserId).GetValueOrDefault()}");
        }

        // 公告/宣传语正文解析(0x009A/0x02E3): 标准 [len:u32][UTF-8],
        // 兼容部分管理命令带 [u8 0] 前缀的形态(前缀剥除后再试一次)。
        private static string ParseNameBodyFlexible(byte[] body, out string failure)
        {
            var text = ParseNameBody(body, out var firstFailure);
            if (text != null)
            {
                failure = null;
                return text;
            }
            if (body != null && body.Length >= 5 && body[0] == 0)
            {
                var stripped = new byte[body.Length - 1];
                Buffer.BlockCopy(body, 1, stripped, 0, stripped.Length);
                return ParseNameBody(stripped, out failure);
            }
            failure = firstFailure;
            return null;
        }

        // 文本安全校验(权威源码对齐): 拒绝非法/控制字符; \t \r \n 放行
        // (公告多行需保留 CR/LF)。
        private static bool IsSafeText(string text)
        {
            if (text == null)
                return true;
            foreach (var c in text)
            {
                if (c == '\r' || c == '\n' || c == '\t')
                    continue;
                if (c < 0x20 || c == 0x7F)
                    return false;
            }
            return true;
        }

        // 包体: [len:u32][UTF-8 字节]。
        private static string ParseNameBody(byte[] body, out string failure)
        {
            failure = null;
            if (body == null || body.Length < 4)
            {
                failure = "body-too-short";
                return null;
            }
            var nameLen = BitConverter.ToUInt32(body, 0);
            if (nameLen == 0 || body.Length < 4 + (int)nameLen)
            {
                failure = $"bad-length {nameLen}/{body.Length}";
                return null;
            }
            try
            {
                return ClientTextEncoding.GetString(body, 4, (int)nameLen);
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                return null;
            }
        }

        private static string FormatBody(byte[] body)
        {
            return body == null
                ? "null"
                : $"{body.Length}B:{BitConverter.ToString(body)}";
        }
    }
}
