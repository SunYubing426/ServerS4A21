using DfoServer.Game.Auction;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    /// <summary>
    /// 拍卖行 CMD 处理器（MVP 一口价版）。
    ///
    /// 业务层（AuctionRepository/AuctionService）已完成上架/搜索/一口价/下架/结算全链路。
    /// 客户端 9 个 AUCTION CMD 的包体布局逐个逆向接入，当前进度：
    ///   ✔ 0x00B6 均价查询   —— 双应答（mode=0 播种价格 + mode=1 开窗），已解锁价格输入框
    ///   ✔ 0x00B7 上架       —— 包体已逆向（构造点 0x232f519），已接 RegisterListing 落库
    ///   ✔ 0x031B UI 刷新    —— 已回应答
///   ✔ 0x00BC 我的上架   —— 已接 LoadListingsBySeller，返回 flag+mode+count(1B)+151B/条（布局迭代中）
///   ✔ 0x00BD 我的竞价   —— 已接 LoadMyActiveBids，返回 flag+mode+count(2B WORD)+159B/条（恒等映射）
    ///   ✔ 0x00B8 下架       —— 已接 Cancel 落库 + 道具退回背包（请求体 8B：ListingId@0 + 到期@4）
    ///   ⧗ 0x014E 一口价购买 —— 已接 Buyout 落库（请求体挂牌id 偏移探测中，见 HandleBuyItemApiece）
    ///   ✔ 0x00BA 按道具搜索 —— 已接 SearchActiveListings，168B 恒等拷贝记录（道具块在 wire[43]）
    ///   ✔ 0x00BB 分类搜索   —— 同上（itemId=0 搜索全部）
    ///   ✔ 0x00B9 竞价/一口价 —— 已接（86JP「一口价购买」按钮实际发 0x00B9，出价=一口价→Buyout 成交）
    ///   ⧗ 0x00BE 拍卖历史   —— 仍是抓包桩
    /// 未接入的 CMD 保持"只抓包不回业务应答"，便于继续采集真实封包。
    /// </summary>
    public sealed class AuctionHandler
    {
        private readonly AuctionService _auction;
        private readonly InventoryRefreshSender _refresh;

        public AuctionHandler(AuctionService auction, InventoryRefreshSender refresh = null)
        {
            _auction = auction ?? throw new ArgumentNullException(nameof(auction));
            _refresh = refresh;
        }

        // ------------------------------------------------------------------
        // 阶段 1：抓包桩。所有包体原样落日志（FileLogger + PacketFileLogger 双通道）。
        // ------------------------------------------------------------------

        /// <summary>0x00B7 请求体主体长度（客户端在其后还会补 9 字节 0，实测 len=37）。</summary>
        private const int RegistBodySize = 28;

        /// <summary>
        /// 通用上架失败码。客户端只对少数几个码有分支，未列入的会被当成成功，
        /// 因此所有失败都必须落在白名单内（详见 HandleRegistItem 的应答说明）。
        /// </summary>
        private const byte RegistErrGeneric = 0xD2;

        /// <summary>
        /// 上架允许的来源列表：背包 / 个人仓库 / 账号金库。数组顺序即回退扫描的优先级。
        /// </summary>
        private static readonly InventoryListType[] RegistSourceLists =
        {
            InventoryListType.Main,
            InventoryListType.PersonalCargo,
            InventoryListType.AccountCargo,
        };

        /// <summary>
        /// CMD 0x00B7：上架请求（点「开始拍卖」时客户端发出）。
        ///
        /// ★ 请求体布局来自客户端真正构造点 0x232f519。
        ///   注意不是 0x1456fe0——那处是同形的死分支，实测它 push ebx 时 ebx=0，
        ///   与抓包里 [4..7] 的物品 key 对不上；全转储中 "push 0xB7 + call 0x274ca50"
        ///   只有 0x1456fe4 / 0x232f51b 两处，用抓包值即可排除前者。
        ///   写入宽度由 [ecx+0x2bcc2c] 的自增量确认：
        ///     0x274d2e0 -> 1 字节    0x274d310 -> 2 字节    0x274d340 -> 4 字节
        ///
        ///   偏移      来源                                实测      含义
        ///   [0]       push 0                              00        固定 0
        ///   [1]       byte [esi+0x188]                    00        源列表类型（推断）
        ///   [2..3]    word [esi+0x184]                    11        源槽位（推断）
        ///   [4..7]    [esi+0x1d8].vt[0x28]() + 0x14       31300     物品模板 id
        ///   [8..11]   [esi+0x1d8].vt[0x48]()              1         数量
        ///   [12..15]  ebx <- strtol(ctl[0x1ac] 文本)      -1        竞拍价（空 -> -1）
        ///   [16..19]  edi <- strtol(ctl[0x1b4] 文本)      8012800   一口价
        ///   [20..23]  [ebp-4]（edi 副本）                 8012800   一口价回显
        ///   [24..27]  [esi+0x220]                         24        上架时长（小时）
        ///
        ///   价格无任何编码：0x2c65a29 是 strtol(str, NULL, 10) 的跳板，
        ///   即两个价格就是输入框文本的十进制值。
        ///
        ///   ⚠ [1] / [2..3] 的语义是推断（byte 列表类型 + word 槽位，与库存协议其余部分
        ///     一致），所以落库前一定会用 [4..7] 的 itemId 交叉校验；校验不过就改走
        ///     "按 itemId 扫描来源列表" 的回退路径，避免推断错误托管错道具。
        ///
        /// ★ 应答（回调 0x1126cd0，走 CMD 路径 ⇒ 框架先吃掉 1 字节 flag）：
        ///     flag != 0 -> 读第 2 字节 mode：1 -> GetWindow(8) + 0x1458960
        ///                                    0 -> GetWindow(7) + 0x2345cb0
        ///                                    其它 -> 无动作
        ///     flag == 0 -> 按 errcode（= [ebp+0x10]）分派：
        ///       0xd2 -> 弹 msg 0x11205（窗口 0x29e）后收尾
        ///       0x69 -> 弹 msg 0x112eb（文本内嵌一个客户端本地查询的数字，窗口 0x209）后收尾
        ///       0x72 -> 弹 msg 0x89b3（窗口 0x209），且【继续 fall-through 去读第 2 字节】
        ///       0x7e / 0xce / 0x7b / 0x89 -> 静默收尾（只刷窗口，不弹框）
        ///       其它 -> 直接跳到成功路径 ⇒ 未知错误码会被客户端当成"上架成功"！
        ///     ⇒ 失败必须回白名单里的码，否则界面会假装成功而道具还在包里。
        ///     ⇒ 应答统一发 2 字节（flag + errcode/mode），兼容 0x72 的 fall-through 读取。
        /// </summary>
        public async Task HandleRegistItem(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            var len = body?.Length ?? 0;
            FileLogger.Log(
                $"[AuctionCap] cid={cid} AUCTION_REGIST_ITEM(0x00B7) 上架 " +
                $"len={len} hex={BitConverter.ToString(body ?? Array.Empty<byte>())}");

            var config = ReadReplyConfig();
            if (session == null)
                return;

            RegistRequest request = null;
            if (body != null && body.Length >= RegistBodySize)
            {
                request = RegistRequest.Parse(body);
                FileLogger.Log($"[AuctionRegist] cid={cid} {request}");
            }
            else
            {
                FileLogger.Log(
                    $"[AuctionRegist] cid={cid} 请求体 {len}B，短于 {RegistBodySize}B，未解析");
            }

            // registlive = 1（默认）：真正落库。registlive = 0：退回阶段 1 的抓包桩。
            if (config.RegistLive && request != null && cid > 0)
            {
                // 金币寄售：itemId 属于金币券家族（2681725~2683073）走虚拟槽旁路，绕过普通道具两道防线。
                if (AuctionRepository.IsGoldItem(request.ItemId))
                    await ExecuteGoldRegist(session, cid, request, config);
                else
                    await ExecuteRegist(session, cid, request, config);
                return;
            }

            var reply = (config.RegistHex != null && config.RegistHex.Length > 0)
                ? config.RegistHex
                : new[] { config.RegistFlag, config.RegistMode };
            await SendRegistReply(session, cid, config, reply, "stub");
        }

        /// <summary>
        /// 解析后的 0x00B7 请求 -> AuctionRepository.RegisterListing -> 真实应答。
        /// 成功后必须清空源槽并刷金币，否则客户端会留下幽灵图标 / 旧金币数。
        /// </summary>
        private async Task ExecuteRegist(
            EnhancedClientSession session,
            int cid,
            RegistRequest request,
            AveragePriceReplyConfig config)
        {
            // 一口价：主字段为空时用回显字段兜底（实测两者相同，互为校验）。
            // 竞拍价（起拍价）：纯竞拍上架时一口价=-1/0，起拍价在 request.BidPrice。
            var buyout = request.BuyoutPrice > 0 ? request.BuyoutPrice : request.BuyoutPriceEcho;
            var starting = request.BidPrice > 0 ? request.BidPrice : 0;
            var duration = NormalizeDuration(request.DurationHours);

            if (!TryResolveSourceSlot(cid, request, out var listType, out var slotIndex, out var note))
            {
                FileLogger.Log(
                    $"[AuctionRegist] cid={cid} 源槽位定位失败（{note}） " +
                    $"hint={request.ListTypeHint}/{request.SlotHint} itemId={request.ItemId}");
                await SendRegistFail(session, cid, config, AuctionError.ItemNotFound);
                return;
            }

            // 起拍价/一口价至少一个 > 0 才合法（纯竞拍/纯一口价/两者都填均可）。
            if (buyout <= 0 && starting <= 0)
            {
                FileLogger.Log(
                    $"[AuctionRegist] cid={cid} 价格非法（一口价={buyout} 起拍价={starting}），拒绝上架 " +
                    $"list={listType}/{slotIndex}");
                await SendRegistFail(session, cid, config, AuctionError.PriceOutOfRange);
                return;
            }

            AuctionRegisterResult result;
            try
            {
                result = _auction.Repository.RegisterListing(
                    cid, listType, slotIndex, buyout, starting, duration, request.Count);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionRegist] cid={cid} RegisterListing 抛异常: {ex}");
                result = AuctionRegisterResult.Fail(AuctionError.ServerBusy);
            }

            if (!result.Success)
            {
                FileLogger.Log(
                    $"[AuctionRegist] cid={cid} 上架被拒 error={result.Error} " +
                    $"list={listType}/{slotIndex}({note}) itemId={request.ItemId} " +
                    $"buyout={buyout} duration={duration.TotalHours}h");
                await SendRegistFail(session, cid, config, result.Error);
                return;
            }

            FileLogger.Log(
                $"[AuctionRegist] cid={cid} ★上架成功 listingId={result.ListingId} " +
                $"list={listType}/{slotIndex}({note}) itemId={request.ItemId} " +
                $"count={request.Count} buyout={buyout} fee={result.FeeGold} " +
                $"goldAfter={result.GoldAfter} duration={duration.TotalHours}h");

            if (_refresh != null)
            {
                try
                {
                    // ★ 道具已被拍卖行托管：刷新源槽为"实际内存状态"。
                    //   不能用 SendEmptyUpdateItemList 硬刷空槽 —— 拆堆（部分上架）时源槽
                    //   还残留 keepCount 个道具，硬刷空会导致客户端把残留道具当成幽灵/锁死。
                    //   SendUpdateItemList 会读内存权威状态：整堆上架时槽已空 -> 自动写空条目；
                    //   拆堆时槽还有剩余 -> 写真实剩余堆叠。两种情况都正确。
                    await _refresh.SendUpdateItemList(session, listType, slotIndex);
                    // 手续费已在事务内从内存库存扣除，这里只需把当前值下发。
                    await _refresh.SendGoldUpdate(session);

                    // ★★ 背包锁死修复（round13）：单槽 0x000E 刷新不足以解除客户端
                    //   上架窗口对源槽的"拖入锁定"——上架成功后客户端 UI 仍把该槽视为
                    //   被上架窗口持有，导致背包不可操作、无法再次拖入新物品上架
                    //   （症状=需重选角色才恢复，而重选角色会触发整列背包刷新）。
                    //   因此上架成功后【额外补发整列背包刷新 0x000D】，强制客户端
                    //   重建整个背包 UI，解除任何残留的槽位锁/幽灵状态。
                    //   与 CeraShop/ExpertJobCompound 等"背包大变动"场景一致。
                    await _refresh.SendItemListRefresh(session, listType);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[AuctionRegist] cid={cid} 库存刷新失败: {ex.Message}");
                }
            }

            // ★ round15 方案（已证明不足，见下 round17 纠正）：上架成功后主动补发
            //   0x00B6 应答（mode=1, key=-1）触发 0x1456910(-1) -> 0x1456240(-1)
            //   清空待上架物品字段 0x1a4。但它【只清 0x1a4，不清 0x20c 上架锁】。
            //
            // ★★★ round17 定案（真正根因）：客户端「上架进行中锁」字段 0x20c 才是
            //   「二次上架发不出请求 + 背包锁死」的元凶——
            //     · 发 0x00B7 时置 1：0x232f602 `mov byte[esi+0x20c],1`
            //     · 再点「上架」先判：0x232f3c6 `cmp byte[esi+0x20c],0`，非 0 直接
            //       return 不发请求（0x232f3b0 入口）。
            //     · 清锁唯一路径 = 0x00B7 应答【mode=0】-> 浏览回调 0x2345cb0 ->
            //       0x232f610 -> 0x232f6ef `mov byte[esi+0x20c],0`。
            //   round15 补发的 0x00B6 key=-1 走 0x112CBB0 -> 0x1456910 -> 0x1456240(-1)，
            //   只清 0x1a4，【完全不碰 0x20c】，所以锁死依旧。
            //
            //   ⇒ 修复：成功应答改回 mode=0（配置 registmode=0），让客户端走 0x2345cb0
            //     清 0x20c。本段「主动补发 0x00B6 key=-1」保留用于清 0x1a4（待上架
            //     物品字段），二者互补：mode=0 清锁、key=-1 清待上架物品。
            var successReply = (config.RegistHex != null && config.RegistHex.Length > 0)
                ? config.RegistHex
                : new[]
                {
                    // flag 必须非 0，否则客户端走失败分支。
                    config.RegistFlag != 0 ? config.RegistFlag : (byte)0x01,
                    config.RegistMode,
                };
            await SendRegistReply(
                session, cid, config, successReply, $"ok listingId={result.ListingId}");

            // ★ 上架成功后主动补发 0x00B6 应答（key=-1）清空待上架物品。
            //   复用 BuildReplyBodyWithKey 构造 mode=1/key=-1 的应答体，与正常查价应答同构。
            try
            {
                var clearBody = BuildReplyBodyWithKey(Array.Empty<byte>(), config, -1);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    (ushort)CmdPacketTypeA21.AUCTION_ASK_AVERAGE_PRICE,
                    clearBody));
                FileLogger.Log(
                    $"[AuctionRegist] cid={cid} 上架成功后主动补发 0x00B6 key=-1 清空待上架物品 " +
                    $"len={clearBody.Length} hex={BitConverter.ToString(clearBody)}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionRegist] cid={cid} 主动补发 0x00B6 key=-1 失败: {ex.Message}");
            }

            // ★ 不再置位 _pendingKeyClear 兜底标记（round15 修正）：
            //   旧方案"置位等待下次 0x00B6"有两个缺陷——
            //     ① 上架成功后客户端不自动发 0x00B6，标记长期挂着，0x1a4 迟迟不清空；
            //     ② 标记残留会误伤"上架后重新拖新物品查价"的 0x00B6（把新物品名也清掉）。
            //   现在改为主动补发 0x00B6 key=-1（见上），即时清空且无副作用。
        }

        /// <summary>
        /// 金币寄售（0x00B7 且 itemId 属于金币券家族）的旁路。
        /// 语义：itemId 决定面额（gold_Nm → N×100 万金币），Count 是张数；
        /// 托管金币 = 面额 × 张数，一口价 = 点券(céra) 单价。
        /// 绕过 TryResolveSourceSlot / IsVirtualMainSlot 对虚拟槽的两道防线，
        /// 直接走 AuctionRepository.RegisterGoldListing 扣金币 + 入库。
        /// </summary>
        private async Task ExecuteGoldRegist(
            EnhancedClientSession session,
            int cid,
            RegistRequest request,
            AveragePriceReplyConfig config)
        {
            var itemId = request.ItemId;
            var buyout = request.BuyoutPrice > 0 ? request.BuyoutPrice : request.BuyoutPriceEcho;
            var count = request.Count > 0 ? request.Count : 1;
            var duration = NormalizeDuration(request.DurationHours);

            // 金币寄售只做一口价（点券购买），必须有正的一口价。
            if (buyout <= 0)
            {
                FileLogger.Log(
                    $"[AuctionGold] cid={cid} 金币一口价非法（buyout={buyout}），拒绝");
                await SendRegistFail(session, cid, config, AuctionError.PriceOutOfRange);
                return;
            }

            AuctionRegisterResult result;
            try
            {
                result = _auction.Repository.RegisterGoldListing(
                    cid, itemId, count, buyout, duration);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionGold] cid={cid} RegisterGoldListing 抛异常: {ex}");
                result = AuctionRegisterResult.Fail(AuctionError.ServerBusy);
            }

            if (!result.Success)
            {
                FileLogger.Log(
                    $"[AuctionGold] cid={cid} 金币上架被拒 error={result.Error} " +
                    $"itemId={itemId} count={count} buyout={buyout} duration={duration.TotalHours}h");
                await SendRegistFail(session, cid, config, result.Error);
                return;
            }

            FileLogger.Log(
                $"[AuctionGold] cid={cid} ★金币上架成功 listingId={result.ListingId} " +
                $"itemId={itemId} count={count} buyout={buyout} fee={result.FeeGold} " +
                $"goldAfter={result.GoldAfter} duration={duration.TotalHours}h");

            if (_refresh != null)
            {
                try
                {
                    // 金币在虚拟槽 0，扣款后刷新金币显示（0x000E 单槽更新 slot=0）。
                    await _refresh.SendGoldUpdate(session);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[AuctionGold] cid={cid} 金币刷新失败: {ex.Message}");
                }
            }

            // ★ 成功应答：2 字节 [flag][action]，action=1 → type8 金币寄售窗口（0x117）刷新。
            //   ★★ round83 定案（推翻 round82 的 action=2）：
            //      框架 0x163bf26 只固定吃 1 字节 flag，只有 flag==0 才再吃 errcode。flag!=0 时
            //      回调 READ_BYTES(1) 读到的 action = 第 2 字节。之前 3 字节 [01,00,XX] 会让
            //      action 读到中间的 0x00 → 走 type7 浏览窗口（0x2345cb0，刷新 [esi+0x13c]）弹
            //      「请放入需要拍卖的物品」——正是用户 round81 截图现象（普通拍卖行提示）。
            //   逆向 0x1126cd0 action 分派：
            //     action=1 → type8 0x1457ae0：弹 0xeab3 公告（金币寄售窗口的「上架成功」）
            //                + 刷新金币寄售面板 [esi+0x12c] + 0x1457980。★ 这才是「金币寄售
            //                自己的成功提示 + 刷新窗口」，窗口 ID 0x117（非普通拍卖行 0x114）。
            //     action=0 → type7 0x2345cb0：普通拍卖行浏览窗口（0x114）+ 清 0x20c 锁。
            //     action>=2 → 直接 return，不弹任何窗口、不刷新（round82 误用，用户反馈「无提示不刷新」）。
            //   用户诉求：金币寄售成功有它自己的成功提示并刷新窗口（非普通拍卖行）→ 必须 action=1。
            var successReply = (config.RegistHex != null && config.RegistHex.Length > 0)
                ? config.RegistHex
                : new byte[] { 0x01, 0x01 };
            await SendRegistReply(
                session, cid, config, successReply, $"gold-ok listingId={result.ListingId}");

            // 上架成功后主动补发 0x00B6 key=-1 清空待上架物品（同普通上架，清 0x1a4）。
            try
            {
                var clearBody = BuildReplyBodyWithKey(Array.Empty<byte>(), config, -1);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    (ushort)CmdPacketTypeA21.AUCTION_ASK_AVERAGE_PRICE,
                    clearBody));
                FileLogger.Log(
                    $"[AuctionGold] cid={cid} 上架成功后主动补发 0x00B6 key=-1 清空待上架物品 " +
                    $"len={clearBody.Length}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionGold] cid={cid} 主动补发 0x00B6 key=-1 失败: {ex.Message}");
            }
        }

        /// <summary>回失败应答：flag=0 + 客户端认识的 errcode。</summary>
        private static Task SendRegistFail(
            EnhancedClientSession session,
            int cid,
            AveragePriceReplyConfig config,
            AuctionError error)
        {
            // 第 2 字节是给 errcode=0x72 的 fall-through 读取兜底的，永远补上。
            var reply = new byte[] { 0x00, MapErrorToClientCode(error, config) };
            return SendRegistReply(session, cid, config, reply, $"fail {error}");
        }

        /// <summary>
        /// 服务端 AuctionError -> 客户端 errcode。
        ///
        /// 客户端 0x1126cd0 的失败分派只认 0xd2 / 0x69 / 0x72 / 0x7e / 0xce / 0x7b / 0x89，
        /// 其余值会 fall-through 到成功路径（界面假装上架成功）。各码对应的本地化文本
        /// 尚未逐条实测，所以这里默认一律回 0xd2（通用提示框，弹完即收尾，最安全）；
        /// 需要试其它码时用配置 registerrcode 覆盖（支持 0x 前缀）。
        /// </summary>
        private static byte MapErrorToClientCode(AuctionError error, AveragePriceReplyConfig config)
        {
            if (config.RegistErrCode != 0)
                return config.RegistErrCode;

            switch (error)
            {
                // 目前全部走通用提示框；细分文本待实测各 msgid 后再拆。
                default:
                    return RegistErrGeneric;
            }
        }

        private static async Task SendRegistReply(
            EnhancedClientSession session,
            int cid,
            AveragePriceReplyConfig config,
            byte[] reply,
            string note)
        {
            if (!config.RegistSend)
                return;

            try
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    config.RegistCmd,
                    (ushort)CmdPacketTypeA21.AUCTION_REGIST_ITEM,
                    reply));
                FileLogger.Log(
                    $"[AuctionRegistReply] cid={cid} cmd=0x{config.RegistCmd:X2} " +
                    $"body={BitConverter.ToString(reply)} ({note})");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionRegistReply] cid={cid} send failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 上架时长白名单（官方下拉：6 / 12 / 24 / 48 小时）。
        /// [24..27] 直接给小时数；白名单外的值一律回落到策略默认时长，
        /// 防止改包写出超长挂单。
        /// </summary>
        private static TimeSpan NormalizeDuration(int hours)
        {
            switch (hours)
            {
                case 6:
                case 12:
                case 24:
                case 48:
                    return TimeSpan.FromHours(hours);
                default:
                    return AuctionPolicy.DefaultListingDuration;
            }
        }

        /// <summary>
        /// 定位要托管的源槽位。
        ///   ① 优先采信包体里的 [1]=listType / [2..3]=slotIndex，但必须与 [4..7] 的
        ///      itemId 对得上——这两个字段的语义是逆向推断的，交叉校验是防止托管错
        ///      道具的唯一保险。
        ///   ② 校验不过则按 itemId 扫描来源列表，取第一个可上架的槽位。
        /// </summary>
        private static bool TryResolveSourceSlot(
            int cid,
            RegistRequest request,
            out InventoryListType listType,
            out short slotIndex,
            out string note)
        {
            listType = InventoryListType.Main;
            slotIndex = -1;
            note = "no-lease";

            if (!InventoryContext.TryGetLease(cid, out var lease) || lease == null)
                return false;

            lock (lease.SyncRoot)
            {
                var inventory = lease.Inventory;
                if (inventory == null)
                    return false;

                var hinted = (InventoryListType)request.ListTypeHint;
                if (Array.IndexOf(RegistSourceLists, hinted) >= 0
                    && request.SlotHint >= 0
                    && !IsBlockedMainSlot(hinted, request.SlotHint)
                    && inventory.TryGetItem(hinted, request.SlotHint, out var hintedCore)
                    && hintedCore != null
                    && (request.ItemId <= 0 || hintedCore.ItemId == request.ItemId))
                {
                    listType = hinted;
                    slotIndex = request.SlotHint;
                    note = "hint";
                    return true;
                }

                // ★ round111（2026-09-06）：材料栏（魔方碎片/灵魂仓）在客户端是独立 UI
                //   列表（实测 hint list=36 slot=358，list id 与服务端 ListType 无关），
                //   服务端存于 Main 虚拟槽 354~364。hint 列表无法识别、但槽位落在
                //   魔方/灵魂虚拟区间且 itemId 与槽位固定模板一致时，按 Main 虚拟槽采信。
                if (Array.IndexOf(RegistSourceLists, hinted) < 0
                    && request.SlotHint >= InventoryService.MainVirtualCubeSlotStart
                    && request.SlotHint <= InventoryService.MainVirtualSoulSlotEnd
                    && inventory.TryGetMainVirtualCount(request.SlotHint, out var hintedVirtual)
                    && hintedVirtual != null
                    && hintedVirtual.Count > 0
                    && (request.ItemId <= 0 || hintedVirtual.ItemId == request.ItemId))
                {
                    listType = InventoryListType.Main;
                    slotIndex = request.SlotHint;
                    note = "virtual-hint";
                    return true;
                }

                if (request.ItemId > 0)
                {
                    foreach (var candidate in RegistSourceLists)
                    {
                        foreach (var pair in inventory.GetItems(candidate))
                        {
                            if (pair.Value == null || pair.Value.ItemId != request.ItemId)
                                continue;
                            if (IsBlockedMainSlot(candidate, pair.Key))
                                continue;

                            listType = candidate;
                            slotIndex = pair.Key;
                            note = "scan";
                            return true;
                        }
                    }

                    // ★ round111：常规列表扫不到时，补扫 Main 虚拟槽的魔方碎片/灵魂仓
                    //   计数（材料栏道具不在物品数组里）。货币槽（0~2）不在此区间，
                    //   天然排除。
                    foreach (var virtualCount in inventory.GetMainVirtualCounts())
                    {
                        if (virtualCount == null
                            || virtualCount.Count <= 0
                            || virtualCount.ItemId != request.ItemId)
                            continue;
                        if (virtualCount.SlotIndex < InventoryService.MainVirtualCubeSlotStart
                            || virtualCount.SlotIndex > InventoryService.MainVirtualSoulSlotEnd)
                            continue;

                        listType = InventoryListType.Main;
                        slotIndex = virtualCount.SlotIndex;
                        note = "virtual-scan";
                        return true;
                    }
                }

                note = "not-found";
                return false;
            }
        }

        /// <summary>背包里的虚拟货币槽与保留槽不可上架。</summary>
        private static bool IsBlockedMainSlot(InventoryListType listType, short slotIndex)
        {
            return listType == InventoryListType.Main
                && (InventoryService.IsVirtualMainSlot(slotIndex)
                    || InventoryService.IsReservedMainSlot(slotIndex));
        }

        /// <summary>CMD 0x00B7 请求体（布局见 HandleRegistItem 的注释）。</summary>
        private sealed class RegistRequest
        {
            public byte Leading;         // [0]      固定 0
            public byte ListTypeHint;    // [1]      源列表类型（推断）
            public short SlotHint;       // [2..3]   源槽位（推断）
            public int ItemId;           // [4..7]   物品模板 id
            public int Count;            // [8..11]  数量
            public int BidPrice;         // [12..15] 竞拍价，-1 = 未填（MVP 只做一口价）
            public int BuyoutPrice;      // [16..19] 一口价
            public int BuyoutPriceEcho;  // [20..23] 一口价回显
            public int DurationHours;    // [24..27] 上架时长（小时）

            public static RegistRequest Parse(byte[] body)
            {
                return new RegistRequest
                {
                    Leading = body[0],
                    ListTypeHint = body[1],
                    SlotHint = BitConverter.ToInt16(body, 2),
                    ItemId = BitConverter.ToInt32(body, 4),
                    Count = BitConverter.ToInt32(body, 8),
                    BidPrice = BitConverter.ToInt32(body, 12),
                    BuyoutPrice = BitConverter.ToInt32(body, 16),
                    BuyoutPriceEcho = BitConverter.ToInt32(body, 20),
                    DurationHours = BitConverter.ToInt32(body, 24),
                };
            }

            public override string ToString()
            {
                return $"lead={Leading} list={ListTypeHint} slot={SlotHint} " +
                    $"itemId={ItemId} count={Count} bid={BidPrice} " +
                    $"buyout={BuyoutPrice}/{BuyoutPriceEcho} hours={DurationHours}";
            }
        }

        /// <summary>CMD 0x00B8：下架（取消自己的上架单）。</summary>
        ///
        /// ★ 请求体（2026-09-03 抓包实测定案）：**9 字节** =
        ///     [0] 前导 0（与 0x00B7 上架 Leading 同款）
        ///     [1..4] int32 挂牌ID（小端，主键）
        ///     [5..8] int32 到期时间（辅助，不参与定位）
        ///   实测 hex = `00-05-00-00-00-0A-D4-99-6A` -> listingId=5、到期=0x6A99D40A。
        ///   结构对齐 0x00B7 上架（Leading@0 + 字段从 @1 起），故 listingId 读 body[1..4]。
        ///
        /// ★ 应答（客户端回调 0x1126E50，CMD 路径 ⇒ 框架先吃掉 1 字节 flag）：
        ///     flag != 0 -> 只读 1 字节，成功：call 0x23009b0(type=8) -> 0x1456970 刷新「我的上架」面板
        ///     flag == 0 -> 按 errcode（=[ebp+0x10]）分派：0xd2/0x69/0x7e/0xce/0x7b/0x89
        ///                 ⇒ 失败必须回白名单里的码，否则界面会假装下架成功而道具已退回。
        ///   所以成功回 1 字节 [0x01]；失败回 2 字节 [0x00, errcode]。
        /// </summary>
        public async Task HandleRegistCancel(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            var hex = BitConverter.ToString(body ?? Array.Empty<byte>());
            FileLogger.Log(
                $"[AuctionCap] cid={cid} AUCTION_REGIST_CANCEL(0x00B8) 下架 " +
                $"len={body?.Length ?? 0} hex={hex}");

            if (session == null || cid <= 0)
                return;

            // 解析挂牌 ID：请求体 [0]=前导 0，[1..4]=int32 listingId（小端），[5..8]=到期时间。
            long listingId = 0;
            if (body != null && body.Length >= 5)
                listingId = (long)BitConverter.ToInt32(body, 1);

            if (listingId <= 0)
            {
                FileLogger.Log($"[AuctionCancel] cid={cid} 请求体无有效挂牌ID，不下架");
                await SendCancelFail(session, cid, AuctionError.InvalidRequest);
                return;
            }

            AuctionCancelResult result;
            try
            {
                result = _auction.Repository.Cancel(listingId, cid);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionCancel] cid={cid} Cancel 抛异常: {ex}");
                result = AuctionCancelResult.Fail(AuctionError.ServerBusy);
            }

            if (!result.Success)
            {
                FileLogger.Log(
                    $"[AuctionCancel] cid={cid} 下架被拒 error={result.Error} listingId={listingId}");
                await SendCancelFail(session, cid, result.Error);
                return;
            }

            FileLogger.Log($"[AuctionCancel] cid={cid} ★下架成功 listingId={listingId}");

            // ★ 金币券下架：托管金币已退回虚拟槽 0，刷新金币显示（无道具槽位，不刷道具列表）。
            if (result.IsGold)
            {
                if (_refresh != null)
                {
                    try
                    {
                        await _refresh.SendGoldUpdate(session);
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log($"[AuctionCancel] cid={cid} 金币刷新失败: {ex.Message}");
                    }
                }

                // ★ round82 修正：金币下架成功应答 = 2 字节 [flag][action]，action=1 → type8 窗口刷新
                //   （金币寄售是独立 type8 窗口）。之前 3 字节 [01,00,01] 会让 action 读到第 2 字节
                //   0x00 → 刷 type7 浏览窗口（错误窗口）。框架只吃 1 字节 flag，flag!=0 不再吃 errcode。
                await SendCancelReply(session, cid, new byte[] { 0x01, 0x01 }, $"gold-ok listingId={listingId}");
                return;
            }

            // ★ 下架改走邮件退回（2026-09-05）：道具不再直接回背包，立刻触发结算邮件，
            //   玩家即时收到退回邮件，无需刷新背包槽位。发送失败时留待分钟 tick 对账补发，
            //   幂等由 settle_mail_count + 邮件幂等键 auction:return:{listingId} 保证。
            try
            {
                if (!_auction.SettleCancelled(listingId))
                {
                    FileLogger.Log(
                        $"[AuctionCancel] cid={cid} 退回邮件暂未发出，留待分钟 tick 补发 " +
                        $"listingId={listingId}");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionCancel] cid={cid} 退回邮件触发异常: {ex.Message}");
            }

            // ★ 成功应答：2 字节 = flag(1) + mode(1)。
            //   客户端 0x1126E50 成功分支（0x1126f35）在框架吃掉 flag 后，还会
            //   0x1126f3e call 0x27c5230 再读【1 字节 mode】：
            //     mode==1 -> CreateWindow(8) + 0x1456970 刷新 type8 面板（[ecx+0x120]）
            //     mode==0 -> CreateWindow(7) + 0x2345e60 刷新 type7 面板（[ecx+0x130]）
            //   只发 1 字节 [0x01] 会让 mode 读超 -> cmp al,1 失败 -> 落到 mode==0
            //   分支刷新 type7 面板，而用户看的是 type7「我的上架」-> 应发 mode=0。
            //   （实测「我的上架」标签 0x233793c 发 0x00BC 时请求体首字节=0 -> type7。）
            await SendCancelReply(session, cid, new byte[] { 0x01, 0x00 }, $"ok listingId={listingId}");
        }

        private static Task SendCancelFail(EnhancedClientSession session, int cid, AuctionError error)
        {
            // 失败回 2 字节 [0x00, errcode]；errcode 必须落在客户端白名单，否则会被当成成功。
            var reply = new byte[] { 0x00, MapCancelErrorToClientCode(error) };
            return SendCancelReply(session, cid, reply, $"fail {error}");
        }

        /// <summary>
        /// 服务端 AuctionError -> 客户端 errcode（0x1126E50 失败分派只认
        /// 0xd2/0x69/0x7e/0xce/0x7b/0x89，其余值会 fall-through 到成功路径）。
        /// 下架失败默认回 0xd2（通用提示框，最安全）；已下架/不存在走 0x7e 静默收尾。
        /// </summary>
        private static byte MapCancelErrorToClientCode(AuctionError error)
        {
            switch (error)
            {
                case AuctionError.ListingNotFound:
                case AuctionError.ListingNotActive:
                    return 0x7e;
                default:
                    return 0xD2;
            }
        }

        private static async Task SendCancelReply(
            EnhancedClientSession session, int cid, byte[] reply, string note)
        {
            try
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01, (ushort)CmdPacketTypeA21.AUCTION_REGIST_CANCEL, reply));
                FileLogger.Log(
                    $"[AuctionCancelReply] cid={cid} cmd=0x01 " +
                    $"body={BitConverter.ToString(reply)} ({note})");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionCancelReply] cid={cid} send failed: {ex.Message}");
            }
        }

        /// <summary>
        /// CMD 0x00B9：竞价 / 一口价购买。
        ///
        /// ★ 关键（2026-09-03 实测定案）：86JP 客户端「一口价购买」按钮实际发的是
        ///   0x00B9 竞价命令，出价金额 = 一口价；0x014E 是另一入口（当前版本未走）。
        ///   实测请求体 26 字节：
        ///     [0]      前导 0
        ///     [1..4]   出价金额（int32，小端）—— 点「一口价」时恒等于该挂牌 buyout
        ///     [5..8]   listingId（int32，主键）
        ///     [9..12]  到期时间（int32，辅助，不参与定位）
        ///     [13..25] 13 字节（道具标识/字符串，全 0，不参与定位）
        ///   实测 hex = 00-2B-02-00-00-1F-00-00-00-51-1A-9A-6A-... → 出价555 / listingId31 / 到期0x6A9A1A51，
        ///   与 listing 31（生锈铁片 buyout=555）完全吻合。
        ///
        /// 应答（回调 0x1126FB0）＝ 1B flag（同 0x00B7/0x00B8）：成功回 [0x01]，失败回 [0x00, errcode]。
        /// </summary>
        public async Task HandleBidding(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            var hex = BitConverter.ToString(body ?? Array.Empty<byte>());
            FileLogger.Log(
                $"[AuctionCap] cid={cid} AUCTION_BIDDING(0x00B9) 竞价 " +
                $"len={body?.Length ?? 0} hex={hex}");

            if (session == null || cid <= 0)
                return;

            // 解析：出价金额 @1、listingId @5。
            int bidAmount = 0;
            long listingId = 0;
            if (body != null && body.Length >= 9)
            {
                bidAmount = BitConverter.ToInt32(body, 1);
                listingId = BitConverter.ToInt32(body, 5);
            }

            if (listingId <= 0)
            {
                FileLogger.Log($"[AuctionBid] cid={cid} 请求体无有效挂牌ID，拒绝");
                await SendBidReply(session, cid, new byte[] { 0x00, 0xD2 }, "no-listing-id");
                return;
            }

            // ★ 一口价语义：出价 ≥ 挂牌一口价总额即成交。MVP 无竞价系统，低于总额一律拒绝。
            //   注意：0x00B9 请求体 [1..4] 是「一口价总额」（单价 × 数量），不是单价。
            //   客户端购买确认框显示的金额 = row[13] = 总额（0x2322f93 逆向定案）。
            var listing = _auction.Repository.GetListing(listingId);
            if (listing == null)
            {
                FileLogger.Log($"[AuctionBid] cid={cid} 挂牌不存在 listingId={listingId}");
                await SendBidReply(session, cid, new byte[] { 0x00, 0x7E }, "listing-not-found");
                return;
            }
            var buyoutTotal = (long)listing.BuyoutPrice * Math.Max(1, listing.Item.ItemCount);
            // ★ 一口价成交判定：仅当「有一口价」且「出价 ≥ 一口价总额」才走成交。
            //   纯竞拍（BuyoutPrice=0）时 buyoutTotal=0，任何出价都不得误走成交，改走竞价。
            var isBuyout = listing.BuyoutPrice > 0 && bidAmount >= buyoutTotal;
            if (!isBuyout)
            {
                // ★ 出价低于一口价总额 → 真实竞价（2026-09-04 引入）。
                //   出价金币立即扣除托管；被超价者由结算逻辑（分钟 tick + 成交时）退回。
                AuctionBidResult bidResult;
                try
                {
                    bidResult = _auction.Repository.PlaceBid(listingId, cid, bidAmount);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[AuctionBid] cid={cid} PlaceBid 抛异常: {ex}");
                    bidResult = AuctionBidResult.Fail(AuctionError.ServerBusy);
                }

                if (!bidResult.Success)
                {
                    FileLogger.Log(
                        $"[AuctionBid] cid={cid} 竞价被拒 error={bidResult.Error} listingId={listingId}");
                    await SendBidReply(session, cid, new byte[] { 0x00, MapBuyErrorToClientCode(bidResult.Error) }, $"bid-fail {bidResult.Error}");
                    return;
                }

                FileLogger.Log(
                    $"[AuctionBid] cid={cid} ★竞价成功 listingId={listingId} 出价={bidAmount} 托管后金币={bidResult.GoldAfter}");

                // 被超价的上一任出价者：立即退回其托管金币（系统邮件）。
                if (bidResult.OutbidCharacterId > 0 && bidResult.OutbidAmount > 0)
                {
                    try
                    {
                        if (_auction.RefundOutbidGold(
                                bidResult.OutbidCharacterId, bidResult.OutbidAmount,
                                listingId, bidResult.OutbidBidId))
                        {
                            // 退回成功即标记 refunded，避免分钟 tick 重复退回同一笔出价。
                            if (bidResult.OutbidBidId > 0)
                                _auction.Repository.MarkBidRefunded(bidResult.OutbidBidId);
                        }
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log(
                            $"[AuctionBid] cid={cid} 退回被超价金币失败 outbid={bidResult.OutbidCharacterId}: {ex.Message}");
                    }
                }

                // 竞价成交后【不发金币刷新包】，只同步内存金币值（同一口价成交约定）。
                if (_refresh != null)
                {
                    try
                    {
                        _refresh.SyncGoldToMemory(session, bidResult.GoldAfter);
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log($"[AuctionBid] cid={cid} 金币内存同步失败: {ex.Message}");
                    }
                }

                // 成功应答：同成交路径，回 [0x01, 0x00]（mode=0 刷新浏览窗口）。
                await SendBidReply(session, cid, new byte[] { 0x01, 0x00 }, $"bid-ok listingId={listingId}");
                return;
            }

            // 出价 ≥ 一口价总额 → 走一口价成交。
            AuctionBuyoutResult result;
            try
            {
                result = _auction.Repository.Buyout(listingId, cid);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionBid] cid={cid} Buyout 抛异常: {ex}");
                result = AuctionBuyoutResult.Fail(AuctionError.ServerBusy);
            }

            if (!result.Success)
            {
                FileLogger.Log(
                    $"[AuctionBid] cid={cid} 一口价成交被拒 error={result.Error} listingId={listingId}");
                await SendBidReply(session, cid, new byte[] { 0x00, MapBuyErrorToClientCode(result.Error) }, $"fail {result.Error}");
                return;
            }

            FileLogger.Log($"[AuctionBid] cid={cid} ★一口价成交成功 listingId={listingId} 总额={buyoutTotal}（单价={listing.BuyoutPrice}×数量={Math.Max(1, listing.Item.ItemCount)}）");

            // ★ 金币券成交：买家金币走【系统邮件】派发（2026-09-04 定案：金币寄售成交
            //   应发金币邮件、而非直接写虚拟槽）。Repository.BuyoutGold 已扣点券、发卖家
            //   代币券、转 Sold；这里同步调用 SettleSold 发买家金币邮件（失败由分钟 tick 补发）。
            if (result.IsGold)
            {
                try
                {
                    _auction.SettleSold(listingId);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[AuctionBid] cid={cid} 金币邮件结算失败 listingId={listingId}: {ex.Message}");
                }

                // ★ round82 修正：金币购买成功应答 = 2 字节 [flag][action]，action=1 → type8 窗口刷新
                //   （金币寄售是独立 type8 窗口，购买后应刷新该窗口移除已购条目）。之前 3 字节
                //   [01,00,01] 会让 action 读到第 2 字节 0x00 → 刷 type7 浏览窗口（错误窗口），
                //   这才是「购买无响应/窗口不刷新」的根因之一。框架只吃 1 字节 flag，flag!=0 不再吃 errcode。
                await SendBidReply(session, cid, new byte[] { 0x01, 0x01 }, $"gold-ok listingId={listingId}");
                return;
            }

            // ★ 成交后立即结算（买家收道具邮件 + 卖家收金币邮件）。
            //   之前此处漏调 SettleSold，结算完全依赖分钟级 Maintain() tick 补发，
            //   导致连续购买时第一封邮件凑巧被 tick 扫到、后续邮件要等下一分钟才到。
            //   这里同步结算，让每笔成交的道具/金币邮件都「秒到」。
            try
            {
                _auction.SettleSold(listingId);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionBid] cid={cid} 成交结算失败 listingId={listingId}: {ex.Message}");
            }

            // ★ 成交后【不发任何金币刷新包】，只发 0x00B9 成功应答（2026-09-03 round39 定案）。
            //   根因：客户端 0x00B9 成交回调自身会「本地扣一次金币」（乐观扣款）。
            //   服务端若再补发 0x000D/0x000E 金币刷新包，会被客户端叠加成「再扣一次」，
            //   余额不足扣第二次时变负 → 客户端显示「余额清空」。
            //   故这里只同步内存背包金币值（供后续重新选角/其他刷新路径读到正确值），
            //   绝不发 0x000D/0x000E。
            if (_refresh != null)
            {
                try
                {
                    _refresh.SyncGoldToMemory(session, result.GoldAfter);
                    FileLogger.Log($"[AuctionBid] cid={cid} 成交后不发金币刷新包，仅同步内存金币={result.GoldAfter}");
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[AuctionBid] cid={cid} 金币内存同步失败: {ex.Message}");
                }
            }

            // 成功应答：2 字节 = flag(0x01) + mode(0x00)。
            // ★ 0x00B9 回调 0x1126FB0（2026-09-03 逆向定案）成功分支在 flag 之后还会
            //   读 1 字节 mode：mode==1 → 刷新上架窗口(type 8)，mode==0 → 刷新浏览窗口(type 7)。
            //   之前只发 [0x01] 缺 mode 字节，客户端刷新逻辑读不到 mode → 成交后列表不刷新、
            //   已购物品仍留在列表里。用户成交后在看浏览窗口，故发 mode=0 触发 type 7 刷新。
            await SendBidReply(session, cid, new byte[] { 0x01, 0x00 }, $"ok listingId={listingId}");
        }

        /// <summary>
        /// CMD 0x00BA：按道具搜索。请求体布局（round112 伪C定案：发包构造 FUN_02340230/FUN_02340500）：
        ///   [0]      mode（0=普通窗口 / 1=金币寄售窗口）
        ///   [1..4]   ★ 翻页 offset（int32 记录偏移）：首批=0，之后按 60/120/… 递增。
        ///            客户端批次=60 条（每页 10 条 × 6 页），翻到 page%6==1 且本地列表不足时
        ///            用 offset=批次起点 重发本包拉下一批（FUN_023404c0 / FUN_02340680）。
        ///   [5]      00
        ///   [6..7]   1F 01（[6]=0x1F 固定标识、[7]=itemId 个数，实测恒 1）
        ///   [8..9]   分类 ID（uint16，little-endian）：0x9CB7=全部、0x32CA=材料
        ///   [10..13] itemId（int32）★ 核心：要搜的物品模板 id，0=搜索全部
        ///   [14..20] 00×7
        ///   [21]     08
        /// 实测：搜「生锈的铁片」(itemId=3027=0x0BD3) 时 body[10..13]=D3 0B 00 00。
        /// 应答走 168B 恒等拷贝记录（详见 BuildSearchBody）。
        /// </summary>
        public async Task HandleSearchByItemKey(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => await HandleSearch(session, body, "AUCTION_SEARCH_BY_ITEMKEY(0x00BA) 按道具搜索",
                CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY);

        /// <summary>
        /// CMD 0x00BB：分类搜索（无 itemId）。请求体布局（round112 伪C定案：发包构造 FUN_023403b0）：
        ///   [0]      mode（同上）
        ///   [1..4]   ★ 翻页 offset（int32 记录偏移，语义与 0x00BA 完全一致）
        ///   [5..6]   分类 ID（uint16，little-endian）：0x32CA=材料
        ///   [7]      07
        ///   [8..9]   00 1F
        ///   [10..18] 00×9
        ///   [19]     08
        /// 与 0x00BA 结构不同（短 2 字节），分类 ID 偏移在 body[5..6] 而非 body[8..9]。
        /// </summary>
        public async Task HandleSearchByNoItemKey(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => await HandleSearch(session, body, "AUCTION_SEARCH_BY_NOITEMKEY(0x00BB) 分类搜索",
                CmdPacketTypeA21.AUCTION_SEARCH_BY_NOITEMKEY);

        /// <summary>
        /// 0x00BA / 0x00BB 通用搜索处理：解析 itemId + 分类 ID -> SearchActiveListings -> 168B 记录应答。
        /// </summary>
        private async Task HandleSearch(
            EnhancedClientSession session, byte[] body, string label, CmdPacketTypeA21 cmd)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            var hex = BitConverter.ToString(body ?? Array.Empty<byte>());
            FileLogger.Log(
                $"[AuctionCap] cid={cid} {label} len={body?.Length ?? 0} hex={hex}");

            if (session == null || cid <= 0)
                return;

            var isNoItemKey = cmd == CmdPacketTypeA21.AUCTION_SEARCH_BY_NOITEMKEY;

            // ★ 翻页 offset（round112 伪C定案）：0x00BA/0x00BB 请求 body[1..4] = int32 记录偏移，
            //   两种包布局一致。首批=0；客户端批次=60 条（每页 10 条 × 6 页），翻到 page%6==1
            //   且本地列表不足时发 offset=60/120/… 拉下一批（FUN_023404c0 → FUN_02340230/FUN_023403b0）。
            //   之前硬编码 0：即使客户端发出翻页请求也永远返回同一批。
            var reqOffset = 0;
            if (body != null && body.Length >= 5)
            {
                reqOffset = BitConverter.ToInt32(body, 1);
                if (reqOffset < 0) reqOffset = 0;
                if (reqOffset > 1000000) reqOffset = 1000000;
            }

            // ★ itemId 在 body[10..13]（0x00BA），0x00BB 无 itemId 字段。
            int itemId = 0;
            if (!isNoItemKey && body != null && body.Length >= 14)
                itemId = BitConverter.ToInt32(body, 10);

            // ★ 分类 ID（uint16）：0x00BA 在 body[8..9]，0x00BB 在 body[5..6]。
            int categoryId = -1;
            if (body != null)
            {
                var catOff = isNoItemKey ? 5 : 8;
                if (body.Length >= catOff + 2)
                    categoryId = body[catOff] | (body[catOff + 1] << 8);
            }

            // ★ 金币寄售旁路（抓包 + 桌面"金币寄售结构图.png"双确认）：
            //   分类 ID 0x9CA5 = 金币寄售面板"其他"根节点；17 个面额子节点的 categoryId
            //   从 0x9CA5 起【连续递增】（round91 抓包实证：1m=0x9CA5、100m=0x9CB3，
            //   即 idx=catId-0x9CA5 对应客户端 itemId 表 0x32a1ef8 的 17 项，
            //   服务端 itemId 2681725~2681736 + 2683069~2683073）。
            //   客户端请求：
            //     - 0x00BA itemId=面额 itemId、categoryId=0x9CA5+idx → 点具体面额子节点
            //     - 0x00BB itemId=0、categoryId=0x9CA5            → 点"其他"浏览全部 17 个面额
            //   0x9CA5 段不在 ResolveCategoryFilter 映射表（独立于主分类树的专门系统）；
            //   金币券无职业/装备/副职业维度，识别靠 itemId（IsGoldItem）+ 分类 ID 区间。
            //   故命中金币寄售时绕过分类解析，统一走 SearchActiveGoldListings。
            //   ★ round91：必须区间判断——仅判 ==0x9CA5 会让其它 16 个面额子叶漏判，
            //   导致 mode 被强制 0 渲染到普通窗口（"其它面额被普通拍卖命中、金币寄售不命中"）。
            var isGoldCategory = categoryId >= 0x9CA5 && categoryId <= 0x9CB5;
            var isGoldItemSearch = itemId > 0 && AuctionRepository.IsGoldItem(itemId);
            // ★ round92：金币券【只能】在金币寄售面板（0x9CA5~0x9CB5）出现。
            //   普通窗口搜金币券 itemId（isGoldItemSearch && !isGoldCategory）一律返回空——
            //   type8 购买已实测跑通（round91），普通窗口的 type7 金币通道完成历史使命。
            var isGoldBrowse = isGoldCategory || isGoldItemSearch;

            // 分类 ID -> 过滤维度（filterKinds + filterGroup + filterGroupPrefix + filterJob + filterEquipType + filterExpertType，null=不过滤=全部）。
            // ★ filterKinds 是数组：一个分类可命中多个 item_kind（如材料 0x32CA = 3/10/11）。
            byte[] filterKinds = null;
            string filterGroup = null, filterJob = null, filterEquipType = null, filterExpertType = null;
            bool filterGroupPrefix = false;
            if (!isGoldBrowse)
                (filterKinds, filterGroup, filterGroupPrefix, filterJob, filterEquipType, filterExpertType) = ResolveCategoryFilter(categoryId);

            // 请求体首字节作 mode（搜索/浏览窗口路由）：普通拍卖行发 0 -> type7，金币寄售发 1 -> type8。
            var reqMode = body != null && body.Length >= 1 ? body[0] : (byte)0;

            // ★ round90/round91/round92 定案（取代 round85 方案 A 的全量强制 mode=0）：
            //   round85 把所有金币搜索应答 mode 强制 0（type7 渲染），代价是金币面板（type8）
            //   永远收不到列表填充 → 面板搜索显示空白（服务端有数据但客户端不显示）。
            //   round90 静态重审 type8 购买链路：门控 [0x450] 的全部写入点均写 1，
            //   round85「不置位」结论系误判；type8 购买入口 0x1451f20 发送的 0x00B9 包体
            //   与 type7（0x23253f0）偏移完全一致，HandleBidding 无需区分。
            //   round91 实测：金币面板搜索（mode=1）+ 面板内购买（0x00B9 → BuyoutGold）全链路跑通。
            //   round92：普通窗口搜金币券 itemId 直接返回空（见下方分支），mode 不再强制。
            //   金币面板搜索（categoryId 0x9CA5~0x9CB5，客户端 reqMode=1）→ 透传 mode=1，
            //   type8 填充金币面板列表，购买走 type8 入口发 0x00B9 → BuyoutGold。

            // ★ round112：批次大小 60 = 客户端分页协议的批次（伪C FUN_02340680：offset 步进 0x3C=60，
            //   每页 10 条 × 6 页）。原 64 会让客户端批次错位（第 2 批 offset=60 与首批 64 条重叠 4 条）。
            const int SearchBatchSize = 60;
            var total = 0;

            IReadOnlyList<AuctionListing> listings;
            try
            {
                if (isGoldCategory)
                {
                    // 金币寄售面板：精确面额（itemId>0）或浏览全部 17 个面额（itemId==0）。
                    listings = _auction.Repository.SearchActiveGoldListings(itemId, SearchBatchSize, reqOffset, cid, out total);
                }
                else if (isGoldItemSearch)
                {
                    // ★ round92：普通窗口搜金币券 itemId → 返回空。
                    //   金币券只能在金币寄售面板（0x9CA5~0x9CB5）搜索/购买。
                    listings = Array.Empty<AuctionListing>();
                }
                else if (filterGroup == UnmappedCategoryGroup)
                {
                    // 未映射的分类 ID：返回空列表，避免串类显示其它武器。
                    // （若返回「全部」会让巨剑/匕首等未映射分类错误显示短剑+太刀等其它武器）
                    listings = Array.Empty<AuctionListing>();
                }
                else
                {
                    // 排除自己上架的物品（官方 DNF 规则：搜索结果不显示自己挂的）。
                    listings = _auction.Repository.SearchActiveListings(itemId, SearchBatchSize, reqOffset, cid, filterKinds, filterGroup, filterGroupPrefix, filterJob, filterEquipType, filterExpertType, out total);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionSearch] cid={cid} SearchActiveListings 抛异常: {ex}");
                listings = Array.Empty<AuctionListing>();
                total = 0;
            }

            FileLogger.Log(
                $"[AuctionSearch] cid={cid} {label} itemId={itemId} categoryId=0x{categoryId:X4} " +
                $"filterKinds={(filterKinds == null ? "(null)" : string.Join("/", filterKinds))} filterGroup={filterGroup ?? "(null)"} filterJob={filterJob ?? "(null)"} filterEquipType={filterEquipType ?? "(null)"} filterExpertType={filterExpertType ?? "(null)"} mode={reqMode} offset={reqOffset} 本批 {listings.Count} 条 / 匹配总数 {total}");

            // ★ round73：诊断「剩余时间」用。客户端竞拍分支 0x1de341d 只在 kind<=11 时
            //   执行 0x1126720（wire[156] → [item+0x12e8]）；kind>11（装备）走字符串日期分支。
            //   打印每条挂牌的 kind / 到期 / 剩余秒数，便于判断该试整数方案还是日期串方案。
            if (listings.Count > 0)
            {
                var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                foreach (var l in listings)
                {
                    FileLogger.Log(
                        $"[AuctionSearchRow] listing={l.ListingId} kind={l.Item.ItemKind} " +
                        $"template={l.Item.ItemTemplateId} expires={l.ExpiresAtUnix} " +
                        $"remain={(l.ExpiresAtUnix > 0 ? l.ExpiresAtUnix - nowUnix : 0)}s");
                }
            }

            var replyBody = BuildSearchBody(listings, reqMode, total);

            // ★ 0x00BB 分类搜索的应答必须用 0x00BA 的 opcode 发出（2026-09-03 逆向定案）。
            //   逆向活跃游戏服表确认：客户端根本没有为 0x00BB 注册回调（注册序列
            //   0xB6→B7→B8→B9→14E→BA→BC→BD，0xBA 与 0xBC 之间 0xBB 缺席）。
            //   所以服务端若按 0xBB 回包，客户端查无回调 → 列表不刷新 + 走异常分支（卡顿）。
            //   0x00BA 回调 0x111F970 能正常渲染 168B 恒等拷贝记录，故把 0xBB 请求的
            //   结果复用 0xBA 通道回给客户端，分类过滤在服务端按 categoryId 完成。
            var replyCmd = isNoItemKey
                ? CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY   // 0xBB 请求 → 0xBA 应答
                : cmd;

            try
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, (ushort)replyCmd, replyBody));
                FileLogger.Log(
                    $"[AuctionSearchReply] cid={cid} reqCmd=0x{(ushort)cmd:X4} replyCmd=0x{(ushort)replyCmd:X4} " +
                    $"count={listings.Count} mode={reqMode} len={replyBody.Length}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionSearchReply] cid={cid} send failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 未映射分类的哨兵值：当 ResolveCategoryFilter 返回此 group 时，HandleSearch 返回空列表
        /// （不串类显示其它武器）。用不可能与真实 PVF group 名冲突的字符串。
        /// </summary>
        private const string UnmappedCategoryGroup = "\u0000__unmapped_category__";

        /// <summary>
        /// 「材料 0x32CA」分类命中的 item_kind 集合。
        /// ★ round110：原本只按 KindMaterial(3) 过滤，与 AuctionHandler 注释里写的
        ///   「KindMaterial / KindExpertJobMaterial / KindSpecialMaterial」不一致，导致
        ///   无色小晶块(3037)、金色小晶块(3262)、黑/白/红/蓝魔方碎片(3033~3036)、
        ///   灵魂仓库 5 件（10100115/10100116/10099773~10099775）被 ResolveStackableItemKind
        ///   判成 KindSpecialMaterial(11) 后在「材料」分类下搜不到。改为三个 kind 全收。
        ///   ⚠️ 副职材料（KindExpertJobMaterial=10）归 0x80EC 子分类，[material expert job]
        ///   的 primary family 是 "material" 但 ResolveStackableItemKind 提前分支判 10，
        ///   不会落到 kind=3，所以这里带上 10 只是兜底，不会造成 0x32CA 与 0x80EC 重复。
        /// </summary>
        private static readonly byte[] MaterialCategoryKinds =
        {
            ItemCore.KindMaterial,           // 3
            ItemCore.KindExpertJobMaterial,  // 10
            ItemCore.KindSpecialMaterial,    // 11
        };

        /// <summary>
        /// 宠物蛋过滤哨兵：宠物大类 0x36B0 用它作为 filterEquipType 传入 SearchActiveListings，
        /// 内存层据此走 ItemMetadataResolver.IsCreatureEgg 判断（而非 [equipment type] 精确匹配）。
        /// 用不可能与真实 [equipment type] 值冲突的控制字符前缀。
        /// </summary>
        internal const string CreatureEggFilterSentinel = "\u0001__creature_egg__";

        /// <summary>
        /// 拍卖行搜索「分类 ID」→ 过滤维度映射，返回 (kinds, filterGroup, filterGroupPrefix)。
        ///   - kinds=null 且 filterGroup=null：不过滤（全部）。
        ///   - kinds!=null：按 ItemCore 种类集合过滤（数组，走 SQL item_kind IN (…)；
        ///     如消耗品=2、材料=3/10/11、副职材料=10）。
        ///   - filterGroup!=null 且 filterGroupPrefix=false：按 PVF 装备 [item group name] 精确匹配（武器叶子）。
        ///   - filterGroup!=null 且 filterGroupPrefix=true：按 group 前缀匹配（防具材质节点，返回该材质所有部位）。
        ///
        /// 分类 ID 是客户端拍卖行 UI 分类树的节点 id，硬编码在客户端函数 0x233bc42 附近
        /// （立即数序列 push parent; push child; push has_child; mov ecx,esi; call addNode）。
        /// 2026-09-03 逆向转储完整提取 157 节点分类树，结合 PVF 实测定案如下。
        ///
        /// ★ 武器细分依赖 group（PVF 装备 [item group name] 字段），不是 [sub type]（那是职业武器
        ///   槽位索引，同值横跨多职业不同武器）。武器叶子顺序 = 职业内 group 的 subType 升序
        ///   （鬼剑士 0x2776~0x277A = ssword/katana/club/lswd/beamswd 已实证）。
        ///
        /// 武器大类 0x2710 下 7 个职业节点：
        ///   0x2775 鬼剑士(5)  0x2776~0x277A = ssword/katana/club/lswd/beamswd
        ///   0x27D9 格斗家(5)  0x27DA~0x27DE = knuckle/gauntlet/claw/bglove/tonfa
        ///   0x283D 神枪手(5)  0x283E~0x2842 = revolver/automatic/musket/hcannon/bowgun
        ///   0x28A1 魔法师(5)  0x28A2~0x28A6 = spear/pole/rod/staff/broom
        ///   0x2905 圣职者(5)  0x2906~0x290A = cross/rosary/totem/scythe/axe
        ///   0x2969 暗夜使者(4) 0x296A~0x296D = dagger/twinswd/wand/chakraweapon
        ///   0x2A31 魔枪士(2)  0x2A32~0x2A33 = pike/halberd
        ///
        /// 防具大类 0x2AF8 下 5 个材质节点（各 5 部位叶子，group=「材质+部位」组合）：
        ///   0x2AF9 布甲 cl(subType0) / 0x2B5C 皮甲 lt(1) / 0x2BC0 轻甲 la(2) /
        ///   0x2C24 重甲 ha(3) / 0x2C88 板甲 mt(4)
        ///   部位 = coat/pants/shoulder/waist/shoes（group 如 "cl coat"/"ha pants"）。
        ///   ★ 材质节点用前缀匹配（如 "cl" 匹配 cl coat/cl pants/… 全部 5 部位）。
        ///   ⚠️ 部位叶子（0x2AFA~0x2AFE 等）的精确顺序尚未抓包定案，暂按未映射处理。
        ///
        /// 首饰大类 0x2EE0（group = amulet/ring/wrist/magic stone/support/charm）：
        ///   0x2EE1/0x2EE2/0x2EE3 直接叶子 + 0x2EE4(12 子) 具体顺序尚未抓包定案，暂按未映射处理。
        ///
        /// 其余大类：0x7918（4 子类 17 叶子，含设计图子类，当前 PVF 无设计图数据已作废）。
        /// ★ 0x80E8（副职业，4 子）已映射（见 switch 内副职业 case）。
        /// ★ 0x84D0（其它，单节点）定案置空（2026-09-04 用户拍板）：它是分类树兜底叶子，
        ///   语义=「不属于任何已分类大类的剩余道具」。客户端上架时按类型自动归类，归不进去
        ///   的才落「其它」；且 86JP PVF 里这类零散道具极少，故服务端直接返回空列表，
        ///   与 default 分支的 UnmappedCategoryGroup 哨兵（HandleSearch 检测后返回空）一致。
        ///
        /// ★ 消耗品大类 0x32C8 已映射 6 个子分类 0x32C9~0x32CE（见 switch 内注释）。
        /// ★ 宠物大类 0x36B0 已映射（2026-09-04）：宠物蛋（filterKind=KindCreature + 蛋哨兵），
        ///   3 子分类 0x36B1/0x36B2/0x36B3 = 红/蓝/绿宠物装备（按 [equipment type] 精确匹配）。
        /// </summary>
        private static (byte[] kinds, string group, bool groupPrefix, string filterJob, string filterEquipType, string filterExpertType) ResolveCategoryFilter(int categoryId)
        {
            switch (categoryId)
            {
                // ---- 顶层 / 大类 ----
                case 0x9CB7: // 全部
                    return (null, null, false, null, null, null);

                // ---- 消耗品大类 0x32C8（下辖 6 个子分类 0x32C9~0x32CE）----
                //   ★ 归类逻辑不在客户端：分类树叶子只发 cat_id，服务端按 stackable type 定义归类。
                //   0x32C9 消耗品   = 药水/buff/瞬移等普通消耗品（KindConsumable）
                //   0x32CA 材料     = [material] 系（KindMaterial / KindExpertJobMaterial / KindSpecialMaterial）
                //   0x32CB 投掷设置 = [throw]/[set]（飞盘/燃烧瓶/爆弹/地雷，KindThrow）
                //   0x32CC 抽取     = [booster selection]/[random reward item] 等开箱（KindGacha）
                //   0x32CD 任务     = [quest] 系（KindQuest）
                //   0x32CE 其它     = [waste]/[etc]/[dye]/[contract] 等杂项（KindMisc）
                case 0x32C9: // 消耗品（普通药水/buff 类）
                    return (new[] { ItemCore.KindConsumable }, null, false, null, null, null);
                case 0x32CA: // 材料（KindMaterial / KindExpertJobMaterial / KindSpecialMaterial）
                    return (MaterialCategoryKinds, null, false, null, null, null);
                case 0x32CB: // 投掷/设置（飞盘、燃烧瓶、爆弹、地雷等）
                    return (new[] { ItemCore.KindThrow }, null, false, null, null, null);
                case 0x32CC: // 抽取（抽奖开箱类）
                    return (new[] { ItemCore.KindGacha }, null, false, null, null, null);
                case 0x32CD: // 任务
                    return (new[] { ItemCore.KindQuest }, null, false, null, null, null);
                case 0x32CE: // 其它（废物/染料/契约等杂项）
                    return (new[] { ItemCore.KindMisc }, null, false, null, null, null);

                // ---- 宠物大类 0x36B0（下辖 3 个子分类 0x36B1~0x36B3）----
                //   ★ 2026-09-04 抓包 + 用户确认定案：0x36B0 是「宠物」大类（非旧注释的「任务品」）。
                //   0x36B0 宠物大类   = 宠物蛋（[creature] + [output index]，未孵化），kind=5。
                //   0x36B1 红色宠物装备 = PVF [equipment type]=[artifact red]（KindCreatureEquipment=6）
                //   0x36B2 蓝色宠物装备 = [artifact blue]
                //   0x36B3 绿色宠物装备 = [artifact green]
                //   ★ 宠物蛋与宠物本体同为 [creature]/kind=5，靠 [output index] 区分：
                //     蛋有 output index（孵化产物）、本体无。故大类用 filterKind=5 + filterEquipType
                //     哨兵（CreatureEggFilterSentinel）走内存层 IsCreatureEgg 排除本体。
                //   红/蓝/绿子分类的 item_kind 都是 6，无法用 kind 区分，只能按 [equipment type] 字段
                //   精确匹配（走 filterEquipType 内存过滤维度）。
                case 0x36B0: // 宠物大类（宠物蛋）
                    return (new[] { ItemCore.KindCreature }, null, false, null, CreatureEggFilterSentinel, null);
                case 0x36B1: // 红色宠物装备
                    return (null, null, false, null, "[artifact red]", null);
                case 0x36B2: // 蓝色宠物装备
                    return (null, null, false, null, "[artifact blue]", null);
                case 0x36B3: // 绿色宠物装备
                    return (null, null, false, null, "[artifact green]", null);

                // ---- 武器：鬼剑士 0x2775 ----
                case 0x2776: return (null, "ssword", false, null, null, null);   // 短剑
                case 0x2777: return (null, "katana", false, null, null, null);   // 太刀
                case 0x2778: return (null, "club", false, null, null, null);     // 钝器
                case 0x2779: return (null, "lswd", false, null, null, null);     // 巨剑（目录名 hsword，字段值 lswd）
                case 0x277A: return (null, "beamswd", false, null, null, null);  // 光剑

                // ---- 武器：格斗家 0x27D9 ----
                case 0x27DA: return (null, "knuckle", false, null, null, null);  // 手套
                case 0x27DB: return (null, "gauntlet", false, null, null, null); // 臂铠
                case 0x27DC: return (null, "claw", false, null, null, null);     // 爪
                case 0x27DD: return (null, "bglove", false, null, null, null);   // 拳套
                case 0x27DE: return (null, "tonfa", false, null, null, null);    // 东方棍

                // ---- 武器：神枪手 0x283D ----
                case 0x283E: return (null, "revolver", false, null, null, null); // 左轮
                case 0x283F: return (null, "automatic", false, null, null, null);// 自动手枪
                case 0x2840: return (null, "musket", false, null, null, null);   // 步枪
                case 0x2841: return (null, "hcannon", false, null, null, null);  // 手炮
                case 0x2842: return (null, "bowgun", false, null, null, null);   // 手弩

                // ---- 武器：魔法师 0x28A1 ----
                case 0x28A2: return (null, "spear", false, null, null, null);    // 长矛
                case 0x28A3: return (null, "pole", false, null, null, null);     // 棍棒
                case 0x28A4: return (null, "rod", false, null, null, null);      // 魔杖
                case 0x28A5: return (null, "staff", false, null, null, null);    // 法杖
                case 0x28A6: return (null, "broom", false, null, null, null);    // 扫把

                // ---- 武器：圣职者 0x2905 ----
                case 0x2906: return (null, "cross", false, null, null, null);    // 十字架
                case 0x2907: return (null, "rosary", false, null, null, null);   // 念珠
                case 0x2908: return (null, "totem", false, null, null, null);    // 图腾
                case 0x2909: return (null, "scythe", false, null, null, null);   // 镰刀
                case 0x290A: return (null, "axe", false, null, null, null);      // 战斧

                // ---- 武器：暗夜使者 0x2969 ----
                case 0x296A: return (null, "dagger", false, null, null, null);   // 匕首
                case 0x296B: return (null, "twinswd", false, null, null, null);  // 双剑
                case 0x296C: return (null, "wand", false, null, null, null);     // 权杖
                case 0x296D: return (null, "chakraweapon", false, null, null, null); // 苦无

                // ---- 武器：魔枪士 0x2A31 ----
                case 0x2A32: return (null, "pike", false, null, null, null);     // 长枪
                case 0x2A33: return (null, "halberd", false, null, null, null);  // 战戟

                // ---- 防具材质节点（前缀匹配，返回该材质全部 5 部位）----
                case 0x2AF9: return (null, "cl", true, null, null, null);  // 布甲
                case 0x2B5C: return (null, "lt", true, null, null, null);  // 皮甲
                case 0x2BC0: return (null, "la", true, null, null, null);  // 轻甲
                case 0x2C24: return (null, "ha", true, null, null, null);  // 重甲
                case 0x2C88: return (null, "mt", true, null, null, null);  // 板甲

                // ---- 防具部位叶子（精确匹配「材质+部位」，顺序 上衣coat→头肩shoulder→下装pants→鞋shoes→腰带waist）----
                // 布甲 0x2AF9
                case 0x2AFA: return (null, "cl coat", false, null, null, null);     // 上衣
                case 0x2AFB: return (null, "cl shoulder", false, null, null, null); // 头肩
                case 0x2AFC: return (null, "cl pants", false, null, null, null);    // 下装
                case 0x2AFD: return (null, "cl shoes", false, null, null, null);    // 鞋
                case 0x2AFE: return (null, "cl waist", false, null, null, null);    // 腰带
                // 皮甲 0x2B5C
                case 0x2B5D: return (null, "lt coat", false, null, null, null);     // 上衣
                case 0x2B5E: return (null, "lt shoulder", false, null, null, null); // 头肩
                case 0x2B5F: return (null, "lt pants", false, null, null, null);    // 下装
                case 0x2B60: return (null, "lt shoes", false, null, null, null);    // 鞋
                case 0x2B61: return (null, "lt waist", false, null, null, null);    // 腰带
                // 轻甲 0x2BC0
                case 0x2BC1: return (null, "la coat", false, null, null, null);     // 上衣
                case 0x2BC2: return (null, "la shoulder", false, null, null, null); // 头肩
                case 0x2BC3: return (null, "la pants", false, null, null, null);    // 下装
                case 0x2BC4: return (null, "la shoes", false, null, null, null);    // 鞋
                case 0x2BC5: return (null, "la waist", false, null, null, null);    // 腰带
                // 重甲 0x2C24
                case 0x2C25: return (null, "ha coat", false, null, null, null);     // 上衣
                case 0x2C26: return (null, "ha shoulder", false, null, null, null); // 头肩
                case 0x2C27: return (null, "ha pants", false, null, null, null);    // 下装
                case 0x2C28: return (null, "ha shoes", false, null, null, null);    // 鞋
                case 0x2C29: return (null, "ha waist", false, null, null, null);    // 腰带
                // 板甲 0x2C88
                case 0x2C89: return (null, "mt coat", false, null, null, null);     // 上衣
                case 0x2C8A: return (null, "mt shoulder", false, null, null, null); // 头肩
                case 0x2C8B: return (null, "mt pants", false, null, null, null);    // 下装
                case 0x2C8C: return (null, "mt shoes", false, null, null, null);    // 鞋
                case 0x2C8D: return (null, "mt waist", false, null, null, null);    // 腰带

                // ---- 首饰（group 精确匹配：amulet 项链 / ring 戒指 / wrist 手镯）----
                case 0x2EE1: return (null, "amulet", false, null, null, null);  // 项链
                case 0x2EE2: return (null, "ring", false, null, null, null);    // 戒指
                case 0x2EE3: return (null, "wrist", false, null, null, null);   // 手镯
                // 称号（0x2EE4，下辖 12 职业子叶 0x2EE5~0x2EF0）。
                // ★ 称号无 [item group name] 字段（group=null），不能用 group 匹配；
                //   识别维度是 [equipment type]=[title name]（1814 个称号全部统一）。
                //   用 filterEquipType 精确匹配。
                // ★ 2026-09-04 抓包定案：客户端点「称号」只发父节点 0x2EE4，
                //   12 职业子叶 0x2EE5~0x2EF0 是空壳占位（不会触发搜索，与「设计图死框架」同理）。
                //   且 PVF 称号 [usable job] 仅 [all](1808)+[at swordman](6)，无 12 职业系统性区分。
                //   故统一指向 [title name] 返回全部称号即可，子叶 case 保留作兜底。
                case 0x2EE4:
                case 0x2EE5: case 0x2EE6: case 0x2EE7: case 0x2EE8:
                case 0x2EE9: case 0x2EEA: case 0x2EEB: case 0x2EEC:
                case 0x2EED: case 0x2EEE: case 0x2EEF: case 0x2EF0:
                    return (null, null, false, null, "[title name]", null);

                // ---- 副职业 0x80E8（下辖 4 子分类 0x80E9~0x80EC）----
                // ★ 2026-09-04 用户定案顺序：0x80E9 炼金术师 / 0x80EA 控偶师 / 0x80EB 附魔师 / 0x80EC 副职材料。
                //   副职业产物都是 stackable（可堆叠），归类维度如下：
                //     炼金术师 = [expert type]=[alchemist]（药剂，[stackable type]=[waste]）
                //     控偶师   = [expert type]=[doll_controller]（人偶，[stackable type]=[waste]）
                //     附魔师   = [expert type]=[enchanter]（宝珠，[stackable type]=[enchant waste]）
                //     副职材料 = [stackable type]=[material expert job]（kind=KindExpertJobMaterial）
                //   ★ 前三者 [stackable type] 都是 [waste] 系，item_kind 同为 KindConsumable，
                //     无法用 kind 或 stackable type 区分，只能按 [expert type] 字段内存过滤
                //     （filterExpertType 维度）。副职材料则直接按 kind=10 过滤。
                //   ★ 分解师(disjointer)无固定配方产物（只产出分解材料），客户端分类树也无其子叶。
                case 0x80E9: // 炼金术师
                    return (null, null, false, null, null, "[alchemist]");
                case 0x80EA: // 控偶师（人偶师）
                    return (null, null, false, null, null, "[doll_controller]");
                case 0x80EB: // 附魔师
                    return (null, null, false, null, null, "[enchanter]");
                case 0x80EC: // 副职材料
                    return (new[] { ItemCore.KindExpertJobMaterial }, null, false, null, null, null);

                // ---- 其它 0x84D0（单节点，兜底）----
                // ★ 2026-09-04 用户拍板「置空」：不映射，落入 default 返回 UnmappedCategoryGroup
                //   （HandleSearch 检测后返回空列表）。语义见文件头部注释：它是分类树兜底叶子，
                //   只承接「无法归入武器/防具/首饰/称号/特殊装备/消耗品/宠物/副职业」的零散道具，
                //   86JP PVF 里此类道具极少，置空最稳妥（避免把已分类道具串进「其它」）。

                // ---- 特殊装备 0x7D00：辅助装备 0x7D01(14 职业) + 魔法石 0x7D64(14 职业) ----
                // 按 PVF [usable job] 过滤（filterJob）。职业顺序 = 客户端分类树 addNode 顺序：
                //   1鬼剑士男 2鬼剑士女 3黑暗武士 4格斗家男 5格斗家女 6神枪手男 7神枪手女
                //   8魔法师男 9魔法师女 10缔造者 11圣职者 12暗夜使者 13守护者 14魔枪士
                // ★ 匹配 = 该职业限定 + [all] 通用（SearchActiveListings 已实现 OR 逻辑）。
                // 辅助装备 0x7D01 的 14 职业叶子：
                case 0x7D02: return (null, "support", false, "[swordman]", null, null);         // 鬼剑士(男)
                case 0x7D03: return (null, "support", false, "[at swordman]", null, null);      // 鬼剑士(女)
                case 0x7D04: return (null, "support", false, "[demonic swordman]", null, null); // 黑暗武士
                case 0x7D0D: return (null, "support", false, "[at fighter]", null, null);       // 格斗家(男)
                case 0x7D05: return (null, "support", false, "[fighter]", null, null);          // 格斗家(女)
                case 0x7D06: return (null, "support", false, "[gunner]", null, null);           // 神枪手(男)
                case 0x7D0B: return (null, "support", false, "[at gunner]", null, null);        // 神枪手(女)
                case 0x7D09: return (null, "support", false, "[at mage]", null, null);          // 魔法师(男)
                case 0x7D07: return (null, "support", false, "[mage]", null, null);             // 魔法师(女)
                case 0x7D08: return (null, "support", false, "[creator mage]", null, null);     // 缔造者
                case 0x7D0A: return (null, "support", false, "[priest]", null, null);           // 圣职者
                case 0x7D0C: return (null, "support", false, "[thief]", null, null);            // 暗夜使者
                case 0x7D0E: return (null, "support", false, "[knight]", null, null);           // 守护者
                case 0x7D0F: return (null, "support", false, "[demonic lancer]", null, null);   // 魔枪士
                // 魔法石 0x7D64 的 14 职业叶子：
                case 0x7D65: return (null, "magic stone", false, "[swordman]", null, null);         // 鬼剑士(男)
                case 0x7D66: return (null, "magic stone", false, "[at swordman]", null, null);      // 鬼剑士(女)
                case 0x7D67: return (null, "magic stone", false, "[demonic swordman]", null, null); // 黑暗武士
                case 0x7D70: return (null, "magic stone", false, "[at fighter]", null, null);       // 格斗家(男)
                case 0x7D68: return (null, "magic stone", false, "[fighter]", null, null);          // 格斗家(女)
                case 0x7D69: return (null, "magic stone", false, "[gunner]", null, null);           // 神枪手(男)
                case 0x7D6E: return (null, "magic stone", false, "[at gunner]", null, null);        // 神枪手(女)
                case 0x7D6C: return (null, "magic stone", false, "[at mage]", null, null);          // 魔法师(男)
                case 0x7D6A: return (null, "magic stone", false, "[mage]", null, null);             // 魔法师(女)
                case 0x7D6B: return (null, "magic stone", false, "[creator mage]", null, null);     // 缔造者
                case 0x7D6D: return (null, "magic stone", false, "[priest]", null, null);           // 圣职者
                case 0x7D6F: return (null, "magic stone", false, "[thief]", null, null);            // 暗夜使者
                case 0x7D71: return (null, "magic stone", false, "[knight]", null, null);           // 守护者
                case 0x7D72: return (null, "magic stone", false, "[demonic lancer]", null, null);   // 魔枪士

                default:
                    FileLogger.Log($"[AuctionSearch] 未映射的分类 ID=0x{categoryId:X4}，返回空列表");
                    return (null, UnmappedCategoryGroup, false, null, null, null);
            }
        }

        // ====================================================================
        // 「剩余时间」字段探针（热重载）
        //
        // 逆向定案（2026-09-04）：
        //   渲染 0x1de04c1 有两条互斥路径——
        //     竞拍类  vtable[0x2c](2) → 0x113a510（返回 当前时间 + vtable[0x17c]()）
        //     一口价类 vtable[0x2c](1) → 0x1fb9280（= [道具对象+0x45c]，绝对到期）
        //   格式化 0x1ddffd0 统一做「到期 - 当前」，≤0 就显示「0 小时」。
        //
        //   行解析 0x1de32a0 的两条分支写入不同的地方：
        //     一口价分支 0x1de35dc：读道具块[10](天数×86400+0x44a53c70) 或 块[54](绝对到期)
        //                          → 0x1193330 写 [道具对象+0x45c]   ← round71 只覆盖了这条
        //     竞拍分支   0x1de3536：读 row[0x3f4]（= wire[156]）
        //                          → 0x1126720 写 [道具对象+0x12e8]
        //     且竞拍分支只在 kind<=11 时执行（0x1de342d call 0x1125f50 判定），
        //     kind>11（装备）走 0x113a510 的复杂分支（读 std::string 形式的日期）。
        //
        //   行对象构造函数 0x1dda440 显示 row+0x3f4 是「子对象」起点（0x24f91d0 构造），
        //   而 168B 记录只到 wire[167]，即该子对象只有前 12 字节过线 —— 它到底是
        //   int32 剩余秒数、绝对到期、还是 12 字节日期串，静态无法定死（vtable 被 VMP 加密）。
        //
        //   故提供热重载探针：改 Data/auction_expire_probe.txt 后直接再搜一次即可对比，
        //   不需要重新编译、不需要重启服务端。
        // ====================================================================

        /// <summary>一条探针写入规则。</summary>
        private sealed class ProbeWrite
        {
            public string Kind;      // w=wire 绝对偏移 / b=道具块内偏移 / s=ASCII 日期串
            public int Off;
            public string Val;
            public int ListingId = -1;  // >=0 时只对指定挂牌生效（A/B 对照用）
        }

        /// <summary>
        /// 一次「合成行扫描」：在真实搜索结果后面追加若干行克隆记录，
        /// 每行给同一个字段写不同的值，用来一次搜索就反推出客户端的当前时间基准。
        /// </summary>
        private sealed class SweepSpec
        {
            public string Kind;                       // w / b
            public int Off;
            public int ForceItemKind = -1;            // >=0：克隆后强制改写 块[0]（item kind）
            public readonly List<string> Vals = new List<string>();
        }

        /// <summary>剩余时间探针配置。</summary>
        private sealed class ExpireProbeConfig
        {
            public int Mode;
            public bool Dump;
            public bool Dumped;              // 只 dump 第一行
            public int DumpListingId = -1;   // >=0 = 只 dump 指定挂牌
            public bool SweepOnly;           // ★ round75：只发合成行，丢弃全部真实行
            public readonly List<ProbeWrite> Writes = new List<ProbeWrite>();
            public readonly List<SweepSpec> Sweeps = new List<SweepSpec>();
            // ★ round76：给【每一条】合成行统一设置的字段（不单独成行）。
            //   用来验证「必须某个开关字段为真，时间才会渲染」这类前置门闸。
            public readonly List<SweepSpec> SweepSets = new List<SweepSpec>();

            public bool Any => Mode > 0 || Writes.Count > 0 || Sweeps.Count > 0 || Dump;
        }

        /// <summary>
        /// 解析一条 sweep / sweepset 配置。
        /// 语法：&lt;kind&gt;&lt;off&gt;[@强制item kind]:&lt;值1&gt;,&lt;值2&gt;,...
        ///   kind: w = wire 绝对偏移（int32 写）
        ///         b = 道具块内偏移（int32 写，块基址 = record[43]）
        ///         k = item 块内偏移（只写 1 字节，不破坏相邻字段）
        ///              k   = block[0]  （item kind）
        ///              k12 = block[12] （任意 1 字节）
        ///   @N  ：把克隆行的块[0]（item kind）强制改写成 N
        /// </summary>
        private static bool TryParseSweep(string v, out SweepSpec spec)
        {
            spec = null;
            var swColon = v.IndexOf(':');
            if (swColon < 2 || v.Length <= swColon + 1)
                return false;

            var head = v.Substring(0, swColon);
            var swKind = head.Substring(0, 1);

            // 剥离可选的 @N
            var forceKind = -1;
            var at = head.IndexOf('@');
            var numPart = head;
            if (at > 1)
            {
                int.TryParse(head.Substring(at + 1), out forceKind);
                numPart = head.Substring(0, at);
            }

            // ★ round79：k 语法升级 —— 不光支持 block[0]，可写任意 k<off>。
            //   之前只支持 k = 写 block[0] 这 1 字节。扩展到 k<off>，off=块内偏移，
            //   只写 1 字节（不污染 block[N+1..N+3]）。原 `k` 形式仍可工作（off=0）。
            int soff = 0;
            if (swKind == "k")
            {
                var kNum = numPart.Substring(1);
                if (kNum.Length > 0)
                    int.TryParse(kNum, out soff);
            }
            else if ((swKind != "w" && swKind != "b")
                || !int.TryParse(numPart.Substring(1), out soff))
                return false;

            spec = new SweepSpec
            {
                Kind = swKind,
                Off = soff,
                ForceItemKind = forceKind,
            };
            foreach (var part in v.Substring(swColon + 1).Split(','))
            {
                var p = part.Trim();
                if (p.Length > 0)
                    spec.Vals.Add(p);
            }
            return spec.Vals.Count > 0;
        }

        /// <summary>
        /// 每次搜索请求重读 Data/auction_expire_probe.txt（热重载）。
        /// 语法（# 之后为行内注释）：
        ///   mode = N         预设方案（见 ApplyExpireProbe 的 switch）
        ///   w156 = remain    覆盖 wire[156..159]（int32）
        ///   b41  = abs       覆盖 道具块[41..44]（int32，块基址 = record[43]）
        ///   s156 = str12     覆盖 wire[156..]（12 字节 ASCII 日期串）
        /// 取值：off | remain | abs | days | raw:N | str8 | str12 | str14
        /// </summary>
        private static ExpireProbeConfig ReadExpireProbe()
        {
            var cfg = new ExpireProbeConfig();
            var path = System.IO.Path.Combine(
                AppContext.BaseDirectory, "Data", "auction_expire_probe.txt");
            try
            {
                if (!System.IO.File.Exists(path))
                    return cfg;

                foreach (var raw in System.IO.File.ReadAllText(path).Split('\n'))
                {
                    var line = raw.Trim();
                    var hash = line.IndexOf('#');
                    if (hash >= 0)
                        line = line.Substring(0, hash).Trim();
                    if (line.Length == 0)
                        continue;

                    var kv = line.Split(new[] { '=' }, 2);
                    if (kv.Length != 2)
                        continue;

                    var k = kv[0].Trim().ToLowerInvariant();
                    var v = kv[1].Trim().ToLowerInvariant();

                    if (k == "mode")
                    {
                        int.TryParse(v, out var m);
                        cfg.Mode = m;
                        continue;
                    }
                    if (k == "dump")
                    {
                        cfg.Dump = v == "1" || v == "on" || v == "true" || v == "yes";
                        continue;
                    }
                    if (k == "dumplisting")
                    {
                        int.TryParse(v, out var dl);
                        cfg.DumpListingId = dl;
                        continue;
                    }
                    // ★ round75：sweeponly = 1 时丢弃全部真实行，只发合成扫描行。
                    //   拍卖行 UI 可能有分页/每页条数上限，真实行会白白占掉可见名额。
                    if (k == "sweeponly")
                    {
                        cfg.SweepOnly = v == "1" || v == "on" || v == "true" || v == "yes";
                        continue;
                    }

                    // ★ 合成行扫描：sweep = <kind><off>:<v1>,<v2>,...
                    //   kind: w = wire 绝对偏移 / b = 道具块内偏移
                    //   值：十进制整数，或 now / now+N / now-N / max
                    //   例：sweep = b54:0,1700000000,1785647157,2147483647
                    //
                    // ★ round76 新增 sweepset：语法同 sweep，但【不产生新行】，
                    //   而是把该值写进【每一条】合成行。用于验证前置门闸字段
                    //   （例：sweepset = b41:now+7200 让「有到期时间」标志为真）。
                    if (k == "sweep" || k == "sweepset")
                    {
                        //   目标语法：<kind><off>[@<强制item kind>]
                        //     @N 会把克隆行的 块[0] 改写成 N。因为客户端按 item kind 走
                        //     不同分支（行解析 0x1de3413 / 渲染 0x1de04df 的 vtable[0x2c](x)），
                        //     合成行若不指定 kind，就全部继承模板的 kind，只能测到一条路径。
                        SweepSpec spec;
                        if (TryParseSweep(v, out spec))
                        {
                            if (k == "sweepset")
                                cfg.SweepSets.Add(spec);
                            else
                                cfg.Sweeps.Add(spec);
                        }
                        continue;
                    }

                    // ★ 支持按挂牌 ID 限定：  l<挂牌id>:<kind><off> = <值>
                    //   用于一次搜索做 A/B 对照（不同挂牌写不同值，看哪一行的显示变了）。
                    var key = k;
                    var onlyListing = -1;
                    var colon = key.IndexOf(':');
                    if (colon > 0)
                    {
                        var head = key.Substring(0, colon);
                        if (head.Length >= 2 && head[0] == 'l'
                            && int.TryParse(head.Substring(1), out var lid))
                        {
                            onlyListing = lid;
                            key = key.Substring(colon + 1);
                        }
                    }

                    if (key.Length < 2)
                        continue;

                    var kind = key.Substring(0, 1);
                    if ((kind != "w" && kind != "b" && kind != "s")
                        || !int.TryParse(key.Substring(1), out var off))
                        continue;

                    cfg.Writes.Add(new ProbeWrite
                    {
                        Kind = kind,
                        Off = off,
                        Val = v,
                        ListingId = onlyListing,
                    });
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionExpireProbe] config read failed: {ex.Message}");
            }

            return cfg;
        }

        /// <summary>
        /// 解析探针取值表达式。
        ///   off/none/0   -> 不写
        ///   remain/abs/days/max
        ///   h:N          -> N 小时对应的秒数（相对值）
        ///   abs:Nh       -> 当前时间 + N 小时的绝对 Unix 秒
        ///   now / now+N / now-N
        ///   raw:N / 纯十进制
        /// </summary>
        private static bool TryResolveProbeNumber(
            string val, long now, long expires, long remain, out long num)
        {
            switch (val)
            {
                case "off":
                case "none":
                case "0":
                    num = 0;
                    return false;
                case "remain":
                    num = remain;
                    return true;
                case "abs":
                    num = expires;
                    return true;
                // ★ 0x7FFFFFFF = 2038-01-19，int32 最大值。
                //   用它做「当前时间函数 0x1105710() 是否正常」的判别
                case "max":
                    num = int.MaxValue;
                    return true;
                case "days":
                    num = (remain + 86399) / 86400;
                    return true;
                case "now":
                    num = now;
                    return true;
            }

            if (val.StartsWith("h:") && int.TryParse(val.Substring(2), out var hh))
            {
                num = (long)hh * 3600;
                return true;
            }
            if (val.StartsWith("abs:") && val.EndsWith("h")
                && int.TryParse(val.Substring(4, val.Length - 5), out var ah))
            {
                num = now + (long)ah * 3600;
                return true;
            }
            if (val.StartsWith("raw:") && long.TryParse(val.Substring(4), out var r))
            {
                num = r;
                return true;
            }
            // now+3600 / now-3600
            if (val.StartsWith("now+") && long.TryParse(val.Substring(4), out var add))
            {
                num = now + add;
                return true;
            }
            if (val.StartsWith("now-") && long.TryParse(val.Substring(4), out var sub))
            {
                num = now - sub;
                return true;
            }
            if (long.TryParse(val, out var plain))
            {
                num = plain;
                return true;
            }

            num = 0;
            return false;
        }

        /// <summary>把探针值写进 168B 搜索记录。</summary>
        private static void ApplyExpireProbe(
            byte[] record, int itemOffset, AuctionListing listing, ExpireProbeConfig cfg)
        {
            if (cfg == null || !cfg.Any)
                return;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var expires = listing.ExpiresAtUnix;
            var remain = expires - now;
            if (remain < 0)
                remain = 0;

            // ---- 预设方案 ----
            switch (cfg.Mode)
            {
                case 1: // wire[156] = 剩余秒数
                    cfg.Writes.Add(new ProbeWrite { Kind = "w", Off = 156, Val = "remain" });
                    break;
                case 2: // wire[156] = 绝对到期
                    cfg.Writes.Add(new ProbeWrite { Kind = "w", Off = 156, Val = "abs" });
                    break;
                case 3: // wire[156..167] = 12 字节日期串 yyyyMMddHHmm
                    cfg.Writes.Add(new ProbeWrite { Kind = "s", Off = 156, Val = "str12" });
                    break;
                case 4: // 道具块[41] = 绝对到期（round67 方案，留档复测）
                    cfg.Writes.Add(new ProbeWrite { Kind = "b", Off = 41, Val = "abs" });
                    break;
                case 5: // wire[156] 剩余秒 + 块[41] 绝对到期
                    cfg.Writes.Add(new ProbeWrite { Kind = "w", Off = 156, Val = "remain" });
                    cfg.Writes.Add(new ProbeWrite { Kind = "b", Off = 41, Val = "abs" });
                    break;
                case 6: // 日期串 + 块[41] 绝对到期
                    cfg.Writes.Add(new ProbeWrite { Kind = "s", Off = 156, Val = "str12" });
                    cfg.Writes.Add(new ProbeWrite { Kind = "b", Off = 41, Val = "abs" });
                    break;
                case 7: // wire[156] = 剩余天数（配 0x1de35dc 的 天数×86400 路径）
                    cfg.Writes.Add(new ProbeWrite { Kind = "w", Off = 156, Val = "days" });
                    break;
                case 8: // wire[156] 剩余秒 + wire[160] 绝对到期（防 0x12e8 是 8 字节）
                    cfg.Writes.Add(new ProbeWrite { Kind = "w", Off = 156, Val = "remain" });
                    cfg.Writes.Add(new ProbeWrite { Kind = "w", Off = 160, Val = "abs" });
                    break;
                case 9: // wire[156..167] = yyyyMMddHHmmss 的前 12 字节
                    cfg.Writes.Add(new ProbeWrite { Kind = "s", Off = 156, Val = "str14" });
                    break;
                case 10: // ★ 整数霰弹：一次给 5 个候选偏移写入互不相同的小时数，
                         //   看列表显示「约N小时」里的 N 是哪个，就知道命中哪个字段。
                         //   3h->wire[156]  7h->wire[160]  11h->wire[164]
                         //   17h->wire[150] 23h->wire[126]
                    cfg.Writes.Add(new ProbeWrite { Kind = "w", Off = 156, Val = "h:3" });
                    cfg.Writes.Add(new ProbeWrite { Kind = "w", Off = 160, Val = "h:7" });
                    cfg.Writes.Add(new ProbeWrite { Kind = "w", Off = 164, Val = "h:11" });
                    cfg.Writes.Add(new ProbeWrite { Kind = "w", Off = 150, Val = "h:17" });
                    cfg.Writes.Add(new ProbeWrite { Kind = "w", Off = 126, Val = "h:23" });
                    break;
                case 11: // ★ 绝对时间霰弹：同上，但填的是「当前时间 + N 小时」的绝对 Unix 秒
                    cfg.Writes.Add(new ProbeWrite { Kind = "w", Off = 156, Val = "abs:3h" });
                    cfg.Writes.Add(new ProbeWrite { Kind = "w", Off = 160, Val = "abs:7h" });
                    cfg.Writes.Add(new ProbeWrite { Kind = "w", Off = 164, Val = "abs:11h" });
                    cfg.Writes.Add(new ProbeWrite { Kind = "w", Off = 150, Val = "abs:17h" });
                    cfg.Writes.Add(new ProbeWrite { Kind = "w", Off = 126, Val = "abs:23h" });
                    break;
                default:
                    break;
            }

            var applied = new List<string>();

            foreach (var w in cfg.Writes)
            {
                // 按挂牌 ID 限定（A/B 对照）
                if (w.ListingId >= 0 && w.ListingId != listing.ListingId)
                    continue;

                var kind = w.Kind;
                var off = w.Off;
                var val = w.Val;

                // 计算目标基址
                int baseOff;
                if (kind == "b")
                    baseOff = itemOffset + off;
                else
                    baseOff = off;

                // 整数语义（取值表达式统一由 TryResolveProbeNumber 解析）
                long num;
                bool isNum = TryResolveProbeNumber(val, now, expires, remain, out num);

                if (isNum)
                {
                    if (baseOff + 4 <= record.Length)
                    {
                        WriteInt32(record, baseOff, (int)num);
                        applied.Add($"{kind}{off}={num}");
                    }
                    continue;
                }

                // 日期串语义
                string text;
                switch (val)
                {
                    case "str8":
                        text = expireLocal(expires).ToString("MMddHHmm");
                        break;
                    case "str14":
                        text = expireLocal(expires).ToString("yyyyMMddHHmmss");
                        break;
                    default: // str12
                        text = expireLocal(expires).ToString("yyyyMMddHHmm");
                        break;
                }

                var bytes = System.Text.Encoding.ASCII.GetBytes(text);
                var copy = Math.Min(bytes.Length, record.Length - baseOff);
                if (copy > 0)
                {
                    Buffer.BlockCopy(bytes, 0, record, baseOff, copy);
                    applied.Add($"{kind}{off}=\"{text}\"");
                }
            }

            if (cfg.Dump && !cfg.Dumped
                && (cfg.DumpListingId < 0 || listing.ListingId == cfg.DumpListingId))
            {
                cfg.Dumped = true;
                FileLogger.Log(
                    $"[AuctionExpireProbe] DUMP listing={listing.ListingId} len={record.Length}\n" +
                    $"  wire[0..63]   = {BitConverter.ToString(record, 0, 64)}\n" +
                    $"  wire[64..127] = {BitConverter.ToString(record, 64, 64)}\n" +
                    $"  wire[128..167]= {BitConverter.ToString(record, 128, Math.Min(40, record.Length - 128))}");
            }

            if (applied.Count > 0)
            {
                FileLogger.Log(
                    $"[AuctionExpireProbe] mode={cfg.Mode} listing={listing.ListingId} " +
                    $"now={now} expires={expires} remain={remain} applied=[{string.Join(", ", applied)}]");
            }
        }

        /// <summary>把 Unix 秒转成客户端时区（UTC+9，86JP 服务端基准）的本地时间。</summary>
        private static DateTime expireLocal(long unixSeconds)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
                    .ToOffset(TimeSpan.FromHours(9)).DateTime;
            }
            catch
            {
                return new DateTime(1970, 1, 1).AddSeconds(unixSeconds);
            }
        }

        /// <summary>
        /// 构造 0x00BA / 0x00BB 搜索应答体：flag(1B) + mode(1B) + dword(4B) + count(2B WORD) + N×168B。
        ///
        /// ★ 与 0x00BC（151B 逐字段重映射）【完全不同】：
        ///   0x00BA 回调 0x111F970 走「恒等拷贝」路径（0x111fb4b `rep movsd 0x2a`，168B=42 dword），
        ///   row[k] = wire[k]，不做任何字段重排。道具块在 wire[43]（`lea esi,[ebx+0x7e];
        ///   lea ecx,[esi-0x53]` = record+43），不是 0x00BC 的 wire[30]。
        ///
        /// 168B 恒等映射字段布局（row 基址 = 0x358，各字段 = 0x358 + 偏移）：
        ///   wire[0..3]   挂牌 id 低位（row[0]，购买请求回传定位用）
        ///   wire[4..7]   挂牌 id 高位 / 到期时间（row[4]）
        ///   wire[8..11]  （row[8]，未使用，保持 0）
        ///   wire[9..12]  ★ 单价（row[9]=0x361，网格价格列）
        ///   wire[13..16] ★ 一口价总额（row[13]=0x365）
        ///   wire[17..20] 平均市价（row[17]=0x369，保持 0 跳过「高于市价2倍」提示）
        ///   wire[21..33] 13B null 结尾字符串（row[21]=0x36d，保持 0=空串）
        ///   wire[42]     byte 标志（row[42]）
        ///   wire[43..125] 83B 道具块（row[43]=0x383，块内 [5]=Attr [6..9]=Value 需反排）
        ///   wire[126..] 子对象（保持 0）
        ///
        /// 头部 dword（第 3 字段）= ★ 匹配总件数（round112 伪C定案，取代「语义未定、回 0 即可」）：
        ///   普通窗口 FUN_02338850(param_1=dword) → FUN_02322b50：[win+0x4e4]=总件数、
        ///   [win+0x4dc]=(dword-1)/10+1=总页数（每页 10 条）；
        ///   金币窗口 FUN_01458970 → FUN_0144a6e0 同构（[win+0x460]/[win+0x458]）。
        ///   总页数决定「下一页」按钮是否可用（[0x4d8] 当前页 != [0x4dc] 总页数时才亮）；
        ///   dword=0 → FUN_02322b50 不更新总页数（保持初始 1）→ 下一页恒灰。
        ///   客户端翻到 page%6==1 且本地列表不足时，用请求 body[1..4]=offset（批次 60 条）
        ///   重发 0x00BA/0x00BB 拉下一批，故 total 必须是不受 limit/offset 影响的匹配总数。
        /// </summary>
        private static byte[] BuildSearchBody(IReadOnlyList<AuctionListing> listings, byte reqMode, int total)
        {
            const int RecordSize = 168;
            const int ItemOffset = 43;   // ★ 168B 恒等拷贝路径的道具块偏移（非 0x00BC 的 30）
            const int ItemSize = 83;

            var writer = new GamePacketWriter();
            writer.WriteByte(1);       // flag（框架预读，必须≠0）
            writer.WriteByte(reqMode); // mode（0 -> type7 浏览窗口 / 1 -> type8）
            writer.WriteInt32(total);  // ★ 匹配总件数（客户端分页：总页数=ceil(total/10)，round112）

            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // 「剩余时间」探针：每次请求重读，改 Data/auction_expire_probe.txt 后直接再搜一次即可，
            // 不需要重启服务端。默认文件不存在 / mode=0 时完全不改字段（保持基线行为）。
            var expireProbe = ReadExpireProbe();

            var records = new List<byte[]>();

            foreach (var listing in listings)
            {
                var record = new byte[RecordSize];

                var unitPrice = listing.BuyoutPrice;
                var stackCount = Math.Max(1, listing.Item.ItemCount);

                // ★ 当前竞价列（row[9]）：有竞价显示最高出价，无竞价显示起拍价。
                //   之前误填 BuyoutPrice（一口价单价），导致「当前竞价=一口价」、客户端
                //   用 row[9] 做最低出价校验，玩家无法出低于一口价的价格（round63 修复）。
                var currentBidDisplay = listing.CurrentBid > 0
                    ? listing.CurrentBid
                    : listing.StartingPrice;

                // ★ 一口价总额（row[13]）：纯竞拍单（无一口价）必须填 -1，而非 0。
                //   反汇编定论（0x1ddb89f）：客户端用 `cmp [row+0x365], -1` 判断「有无一口价」，
                //   -1 = 纯竞拍单（跳过「比一口价高」校验），≥0 = 有一口价。
                //   之前纯竞拍单发 0，客户端 `0x1ddb8c8 cmp edi,[row+0x365]; setg` 把
                //   「出价(10) > 一口价(0)」判真 -> 弹「比一口价高」（0xeb0b），纯竞拍无法出价。
                var totalPrice = unitPrice > 0
                    ? (int)Math.Min((long)unitPrice * stackCount, int.MaxValue)
                    : -1;

                // 168B 恒等映射：字段直接落在对应 row 偏移（与 0x00BC 重映射不同，无需错位）。
                WriteInt32(record, 0, (int)listing.ListingId);        // wire[0..3] 挂牌 id 低位
                WriteInt32(record, 4, (int)listing.ExpiresAtUnix);    // wire[4..7] 挂牌 id 高位/到期
                // wire[8..11] 保持 0（row[8] 未使用）
                WriteInt32(record, 9, currentBidDisplay);             // wire[9..12] ★ 当前竞价（起拍价/最高价）
                WriteInt32(record, 13, totalPrice);                   // wire[13..16] ★ 一口价总额（纯竞拍=-1）
                // wire[17..20] 平均市价保持 0
                // wire[21..33] 13B 字符串保持 0
                // wire[34..41] 保持 0（round66 实验证明 row[34] 不是「剩余时间」字段，已回退）
                // wire[42] 标志保持 0

                // ---- 83B 道具结构 @record[43] ----
                var coreBytes = BuildSafeItemBlock(listing);
                {
                    var copy = Math.Min(coreBytes.Length, ItemSize);
                    Buffer.BlockCopy(coreBytes, 0, record, ItemOffset, copy);

                    // 拍卖行 wire 道具块 [5]=Attr、[6..9]=Value，与 ItemCore 的 [5..8]=Value/[9]=Attr 相反。
                    if (copy > ItemCore.AttrOffset)
                    {
                        var value = coreBytes[ItemCore.ValueOffset]
                                    | (coreBytes[ItemCore.ValueOffset + 1] << 8)
                                    | (coreBytes[ItemCore.ValueOffset + 2] << 16)
                                    | (coreBytes[ItemCore.ValueOffset + 3] << 24);
                        var attr = coreBytes[ItemCore.AttrOffset];

                        record[ItemOffset + 5] = attr;             // 块[5]    = 强化位域
                        WriteInt32(record, ItemOffset + 6, value); // 块[6..9] = 数量/实例UID
                    }
                }

                // ---- ★★★ 剩余时间（round74 定案：装备类走 wire[156]，与块[54] 是两条互斥路）----
                //   客户端行解析 0x1de3413 处二选一，两条路【互斥】：
                //     vtable[0x2c](itemObj, 2) == true  (装备类)
                //        -> 0x1de341d：call 0x1125f50 判 kind<=11 -> 0x1de3536
                //           mov eax,[ebx+0x3f4]   ; row+0x3f4 = wire[156]
                //           call 0x1126720         ; => [itemObj+0x12e8] = wire[156]
                //     vtable[0x2c](itemObj, 1) == true  (非装备/材料类)
                //        -> 0x1de35bb：块[10](word,剩余天数)!=0 ? 块[10]*86400+0x44a53c70
                //                                                : 块[54](int32,绝对到期)
                //           call 0x1193330         ; => [itemObj+0x45c]
                //       ⚠ 装备类 vtable[0x2c](1) 为 false，0x1de35ce `je 0x1de3606` 直接跳过，
                //         [itemObj+0x45c] 永远是构造时的 0。
                //
                //   渲染 0x1de04c1 同样二选一：
                //     装备类 -> 0x113a510：kind<=11 时 eax = vtable[0x17c](itemObj)（读 [item+0x12e8]）
                //               return 0x1105710() + eax  ⇒ getter 返回的是【剩余秒数】
                //     非装备 -> 0x1fb9280：return [item+0x45c]（【绝对到期】）
                //   格式化 0x1ddffd0：remain = 到期 - 0x1105710()；
                //     <=0 →「0小时」；>86400 →「X天」；否则「约X小时」
                //
                //   ⇒ 装备类根本不写 [item+0x45c]，round71 只改块[54] 对装备类完全无效
                //     —— 这就是「剩余时间恒为 0」的根因。装备类必须写 wire[156]，
                //     语义是【剩余秒数】（不是绝对时间，因为渲染端还要 +now 再 -now 抵消）。
                //   两个字段都写，装备类/非装备类都能正确显示。
                //
                //   ⚠⚠⚠ round76 更正（round75 的结论是错的，勿信）：
                //      round75 说「渲染 0x1de04c1 是死代码」——**错的，是我搜错了地址**。
                //      真正的入口是 0x1de04c0（序言 55 8B EC 56 8B 75 08 57），
                //      round75 搜的是 0x1de04c1，偏了 1 字节，所以 findcall=0 是假阴性。
                //      正确验证：findcall 1de04c0 → 0x1de1311 / 0x1de13cd 两处调用者。
                //      ⇒ 0x1de04c0 是【活的】，而且它就是真正的剩余时间渲染函数，
                //        它调用了格式化 0x1ddffd0（findcall 1ddffd0 只有 0x1de0503/0x1de053b）。
                //
                //   ★★★ round76 真正的渲染函数全文（0x1de04c0，两条路各有一个前置门闸）：
                //     0x01de04df: call edx        ; vtable[0x2c](itemObj, 2)   装备类?
                //     0x01de04e5: je  0x1de0510   ; false → 非装备分支
                //     0x01de04ef: call edx        ; ★★ vtable[0xd0](itemObj)   ← 第二个门闸！
                //     0x01de04f3: je  0x1de04ce   ; ★ false → return false（时间不渲染）
                //     0x01de04fb: call 0x113a510  ; 装备 getter → now + [item+0x12e8]
                //     0x01de0503: call 0x1ddffd0  ; 格式化
                //     0x01de0517: call eax        ; vtable[0x2c](itemObj, 1)   非装备?
                //     0x01de0527: call eax        ; ★★ vtable[0xd0](itemObj)   同一个门闸
                //     0x01de0533: call 0x1fb9280  ; 非装备 getter → [item+0x45c]
                //     0x01de053b: call 0x1ddffd0  ; 格式化
                //     ⇒ 【两条路都先过 vtable[0xd0]()】，返回 false 就 return false。
                //       这解释了为什么「改什么都还是 0」：到期值写得再对，
                //       只要这个门闸为假，时间根本不会渲染。
                //       该门闸极可能是「该道具是否有到期时间」，
                //       对应 ItemCore.ExpireTimeOffset = 41（库里在售 5 条的块[41] 全是 0）。
                //       用 sweepset = b41:... 验证（见 AppendSweepRows）。
                WriteRemainSeconds(record, 156, listing, nowUnix);

                // ---- 剩余时间探针（热重载，默认不启用）----
                ApplyExpireProbe(record, ItemOffset, listing, expireProbe);

                records.Add(record);
            }

            // ---- ★ 合成行扫描（round75）----
            //   客户端的「剩余时间」= 到期 - 0x1105710()，而 0x1105710() 的返回值
            //   我们无法静态确定（它 = 全局串 0x3a5b014 + (clock - 0x3a5b024)，转储里
            //   这两个串是空的）。与其猜，不如直接扫：
            //   克隆第一条真实记录，逐行写不同的到期值，一次搜索就能从客户端显示
            //   反推出 0x1105710() 的真实大小，并同时判定「装备路径 / 非装备路径」
            //   到底哪一条是活的。
            //
            //   配置：sweep = <kind><off>:<v1>,<v2>,...
            //     sweep = b54:0,1700000000,now,now+7200,max
            //     sweep = w156:0,3600,7200,86400,172800
            AppendSweepRows(records, expireProbe, ItemOffset, nowUnix);

            writer.WriteUInt16((ushort)Math.Min(records.Count, ushort.MaxValue));
            foreach (var record in records)
                writer.WriteBytes(record);

            return writer.ToArray();
        }

        /// <summary>
        /// 在搜索结果后面追加合成扫描行（探针用，默认不启用）。
        /// 每行克隆第一条真实记录，只改指定的一个字段 + 挂牌 ID（保证唯一，避免被去重）。
        /// </summary>
        private static void AppendSweepRows(
            List<byte[]> records, ExpireProbeConfig cfg, int itemOffset, long nowUnix)
        {
            if (cfg == null || cfg.Sweeps.Count == 0 || records.Count == 0)
                return;

            var template = records[0];
            var log = new System.Text.StringBuilder();

            // ★ round75：拍卖行 UI 可能有「每页条数」上限，真实行会占掉可见名额。
            //   sweeponly=1 时把真实行全部丢弃，只留合成行（模板已提前取出）。
            if (cfg.SweepOnly)
                records.Clear();

            // ★ round76：把响应时刻的 nowUnix 打进日志。
            //   本机时钟观测到过【瞬间跳变 10 年】（2026-09-04 07:30 变 2016-05-04，
            //   07:31:16 被 NTP time.windows.com 拉回）。若搜索那一刻时钟是塌的，
            //   所有 now±N 的相对值都会失真，测试结论就废了。
            //   ⇒ 探针配置请一律用 raw:<绝对Unix秒>，别用 now+N。
            log.Append("[AuctionExpireProbe] SWEEP rows (nowUnix=")
               .Append(nowUnix).Append(", 1-based")
               .Append(cfg.SweepOnly ? ", SWEEPONLY:" : ", after ")
               .Append(cfg.SweepOnly ? " " : " ")
               .Append(records.Count)
               .Append(cfg.SweepOnly ? " rows total" : " real rows")
               .Append("):");

            if (cfg.SweepSets.Count > 0)
            {
                log.Append("\n  sweepset (轮转，按行号取模):");
                foreach (var set in cfg.SweepSets)
                {
                    var setOff = set.Kind == "b" ? itemOffset + set.Off : set.Off;
                    log.Append($"\n    {set.Kind}{set.Off} (wire off {setOff}) =");
                    for (var vi = 0; vi < set.Vals.Count; vi++)
                    {
                        long setNum;
                        var ok = TryResolveProbeNumber(set.Vals[vi], nowUnix, 0, 0, out setNum);
                        log.Append($" [行号%{set.Vals.Count}=={vi}] {set.Vals[vi]}"
                                 + (ok ? $"={setNum}" : " SKIP"));
                    }
                }
            }

            // ★ 轮转排序：先每个 sweep 组各出第 1 个值，再各出第 2 个值……
            //   这样即使客户端只显示前 N 行（分页），也能保证每一组的第一个值
            //   （最有信息量的那一行）都落在可见范围内，而不是被同组挤到后面。
            var maxVals = 0;
            foreach (var s in cfg.Sweeps)
                if (s.Vals.Count > maxVals) maxVals = s.Vals.Count;

            var rowIdx = 0;   // sweepset 多值时按行号轮转
            for (var i = 0; i < maxVals; i++)
            {
                foreach (var spec in cfg.Sweeps)
                {
                    if (i >= spec.Vals.Count)
                        continue;

                    // ★ round79：k<off> 语法只写 1 字节，off 是块内偏移（k 不带 off=0 即 block[0]）。
                    var isKind = spec.Kind == "k";
                    var baseOff = isKind ? itemOffset + spec.Off
                                : (spec.Kind == "b" ? itemOffset + spec.Off : spec.Off);
                    var writeSize = isKind ? 1 : 4;
                    if (baseOff + writeSize > template.Length)
                    {
                        log.Append($"\n  !! {spec.Kind}{spec.Off} out of range (base={baseOff})");
                        continue;
                    }

                    long num;
                    if (!TryResolveProbeNumber(spec.Vals[i], nowUnix, 0, 0, out num))
                    {
                        log.Append($"\n  row {records.Count + 1}: {spec.Kind}{spec.Off}"
                                 + $" '{spec.Vals[i]}' -> skip");
                        continue;
                    }

                    var row = (byte[])template.Clone();

                    // 合成行必须先把「另一条路」的字段清零，否则两条路互相干扰，
                    // 看不出到底是哪条路生效。
                    //   非装备路：块[54]（绝对到期）
                    //   装备路  ：wire[156]（剩余秒数）
                    //   另有    ：wire[4]（我们本来也填了绝对到期，一并隔离）
                    if (!(spec.Kind == "b" && spec.Off == 54))
                        WriteInt32(row, itemOffset + 54, 0);
                    if (!(spec.Kind == "w" && spec.Off == 156))
                        WriteInt32(row, 156, 0);
                    if (!(spec.Kind == "w" && spec.Off == 4))
                        WriteInt32(row, 4, 0);

                    // 强制 item kind（决定客户端走哪条分支）
                    if (spec.ForceItemKind >= 0 && itemOffset < row.Length)
                        row[itemOffset] = (byte)spec.ForceItemKind;

                    // ★ round76：sweepset —— 给每条合成行统一设置的「背景字段」。
                    //   先写 sweepset 再写被扫字段，保证两者撞车时被扫字段生效。
                    //   多个值时按行号轮转，配合重复的 sweep 组即可做全因子 A/B 对照。
                    foreach (var set in cfg.SweepSets)
                    {
                        var setOff = set.Kind == "b" ? itemOffset + set.Off : set.Off;
                        long setNum;
                        var setVal = set.Vals[rowIdx % set.Vals.Count];
                        if (setOff + 4 <= row.Length
                            && TryResolveProbeNumber(setVal, nowUnix, 0, 0, out setNum))
                            WriteInt32(row, setOff, (int)setNum);
                    }

                    // ★ round79：k<off> 只写 1 字节，避免误伤 block[N+1..N+3]
                    if (isKind)
                        row[baseOff] = (byte)(num & 0xFF);
                    else
                        WriteInt32(row, baseOff, (int)num);

                    // 挂牌 ID 唯一化（900001, 900002, ...），避免客户端按 ID 去重
                    WriteInt32(row, 0, 900000 + records.Count + 1);

                    records.Add(row);
                    rowIdx++;
                    log.Append($"\n  row {records.Count}: {spec.Kind}{spec.Off}");
                    if (spec.ForceItemKind >= 0)
                        log.Append($"@kind{spec.ForceItemKind}");
                    log.Append($" = {spec.Vals[i]} ({num})");
                }
            }

            FileLogger.Log(log.ToString());
        }

        /// <summary>
        /// 把挂牌的剩余秒数写进记录（拍卖行「剩余时间」列的装备类字段）。
        /// 溢出/已过期一律夹到 0，避免客户端算出负数（负数同样显示「0小时」，但会产生误导性日志）。
        /// </summary>
        private static void WriteRemainSeconds(byte[] record, int offset, AuctionListing listing, long nowUnix)
        {
            long remain = listing.ExpiresAtUnix - nowUnix;
            if (remain < 0)
                remain = 0;
            if (remain > int.MaxValue)
                remain = int.MaxValue;

            if (offset + 4 <= record.Length)
                WriteInt32(record, offset, (int)remain);
        }

        /// <summary>CMD 0x00BC：我的上架列表。</summary>
        public async Task HandleMyRegistedItemInfo(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            // ★ 请求体第 1 字节 = 客户端窗口 mode。应答必须回显，否则数据被灌进另一个窗口类。
            //   客户端有两处发 0x00BC：
            //     0x1457241 (push 1) -> mode=1 -> 回调建 window type8，填 [window+0x120] (fill 0x1458c50)
            //     0x2337954 (push 0) -> mode=0 -> 回调建 window type7，填 [window+0x130] (fill 0x2338d50)
            //   实测「我的拍卖品 → 上架的物品」标签走 0x2337954，请求体为 [00]。
            //   旧代码硬编码回 mode=1 -> 4 条记录被填到用户没在看的 window8 面板 -> 可见网格空白。
            var reqMode = ResolveListMode(body);
            FileLogger.Log(
                $"[AuctionMyListings] cid={cid} 0x00BC 我的上架请求 len={body?.Length ?? 0} reqMode={reqMode}");

            try
            {
                var listings = _auction.Repository.LoadListingsBySeller(cid, AuctionListingStatus.Active);
                // ★ round93：「我的上架」按窗口归属过滤金币券（用户要求）：
                //   mode=1（金币寄售面板）→ 只返回金币券；
                //   mode=0（普通拍卖行）  → 排除金币券。
                //   金币券的上架/搜索/购买全部归属金币寄售面板（round92 定案），
                //   普通拍卖行的上架列表不应出现金币券。
                var filtered = new List<AuctionListing>(listings.Count);
                foreach (var l in listings)
                {
                    var isGold = AuctionRepository.IsGoldItem(l.Item?.ItemTemplateId ?? 0);
                    if (reqMode == 1 ? isGold : !isGold)
                        filtered.Add(l);
                }
                listings = filtered;
                var replyBody = BuildMyListingBody(listings, reqMode);

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    (ushort)CmdPacketTypeA21.AUCTION_MY_REGISTED_ITEM_INFO,
                    replyBody));

                FileLogger.Log(
                    $"[AuctionMyListingsReply] cid={cid} count={listings.Count} mode={reqMode} " +
                    $"len={replyBody.Length} hex={BitConverter.ToString(replyBody)}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionMyListingsError] cid={cid} {ex}");
                // 出错时回空列表，避免客户端卡死
                await SendEmptyMyListings(session, reqMode);
            }
        }

        /// <summary>
        /// CMD 0x00BD：我的竞价列表（round56 实施 + round57/round60 修正过滤逻辑）。
        /// 反汇编确认（2026-09-04）：0x111FE70 读取 flag(byte0,≠0) → mode(byte1)
        ///   → count(WORD, byte2-3) → N×0x9F(159) 记录，wire 与客户端 168B 行结构恒等映射
        ///   （路径A 0x232dc41：sub esp,0xa8 + rep movsd 0x2a，row[k]=wire[k]）。
        ///   mode==1 → window type8 (0x1458d20)；mode==0 → window type7。
        ///   159B 比 168B 行结构少 9 字节，客户端按 168B 渲染时尾部越界读几字节，按
        ///   0x00BC 同款经验这部分通常无副作用（行对象尾部剩余字段多用于重绘对齐）。
        ///
        /// 数据范围：round60 改为按 bid.Status 过滤（不再依赖 listing.status）：
        ///   0=领先中 ✓ 显示   1=被超价 ✓ 显示   2=中标 ✗ 不在面板范围   3=流拍 ✗ 不在面板范围
        ///   历史成交/流拍应进「我的拍卖历史」类面板，本面板仅展示「当前在跟踪的」竞价。
        ///   wire[29] 状态位写 bid.Status，让客户端可按 0/1 渲染不同视觉（如划线/灰显）。
        ///   round97 补充：bid.Status 过滤之上再要求 listing.Status == Active——
        ///   卖家下架/他人成交不会回写 bid.Status，仅靠 bid 侧过滤会把已下架物品留在面板里。
        ///
        /// 与 0x00BC 字段对照（两协议头部语义接近，单位/位置可能略不同）：
        ///   0x00BC (MyRegistedItem) 走路径B「逐字段重映射」151B→168B，
        ///                           wire[8..11] 单价 → row[9..12] 价格列；
        ///   0x00BD (MyBiddingInfo) 走路径A「恒等映射」wire==row，
        ///                           wire[8..11] 直接写到 row[8..11]。
        ///   ★ row[8..11] 是「网格价格列」这一字段语义在两套面板都成立；语义虽一致，
        ///     但面板含义不同：0x00BC 是「单价」，0x00BD 是「我的出价」。
        /// </summary>
        public async Task HandleMyBiddingInfo(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            var reqMode = ResolveListMode(body);
            FileLogger.Log(
                $"[AuctionMyBids] cid={cid} 0x00BD 我的竞价请求 len={body?.Length ?? 0} reqMode={reqMode}");

            if (cid <= 0)
            {
                await SendEmptyMyBids(session, reqMode);
                return;
            }

            try
            {
                var bids = _auction.Repository.LoadMyActiveBids(cid);

                // ★ round60 修正：按 bid.Status 过滤（领先中 + 被超价），不再依赖 listing.status
                //   之前用 listing.Status != Active 跳过会把所有 Settled 的 bid 全部漏掉。
                var rawEntries = new List<MyBidEntry>(bids.Count);
                foreach (var bid in bids)
                {
                    // bid.Status: 0=领先中 1=被超价 2=中标 3=流拍
                    if (bid.Status > 1)
                    {
                        FileLogger.Log(
                            $"[AuctionMyBids] cid={cid} 跳过 bid_id={bid.BidId} listing_id={bid.ListingId} " +
                            $"(bid.Status={bid.Status} 中标/流拍，不在跟踪面板范围)");
                        continue;
                    }

                    var listing = _auction.Repository.GetListing(bid.ListingId);
                    if (listing == null)
                    {
                        FileLogger.Log(
                            $"[AuctionMyBids] cid={cid} 跳过 bid_id={bid.BidId} listing_id={bid.ListingId} " +
                            $"(listing 已不存在)");
                        continue;
                    }

                    // ★ round97 修正（2026-09-05）：挂牌已不活跃（卖家下架 Cancelled / 已售 Sold /
                    //   流拍 Expired / 结算完 Settled）时不应再出现在「我的竞价」跟踪面板。
                    //   之前只看 bid.Status<=1，而卖家下架/他人买走都不会回写 bid.Status，
                    //   导致其它角色已下架/已成交的物品仍挂在我的竞价列表里。
                    //   round57 放弃 listing.Status 过滤是因为当时测试库全是 Settled 导致列表空，
                    //   那是脏测试数据问题而非语义问题——语义上本面板就是「仍在进行中的竞价」。
                    if (listing.Status != AuctionListingStatus.Active)
                    {
                        FileLogger.Log(
                            $"[AuctionMyBids] cid={cid} 跳过 bid_id={bid.BidId} listing_id={bid.ListingId} " +
                            $"(listing.Status={listing.Status} 已下架/成交/流拍，不在跟踪面板范围)");
                        continue;
                    }
                    rawEntries.Add(new MyBidEntry(bid, listing));
                }

                // ★ round60：按 listing_id dedup，同 listing 多次出价只保留最新一笔
                //   Repository 已 ORDER BY bid_at_unix DESC，所以同 listing 的第一条即最新。
                //   该最新 bid 的 status 即「当前状态」（0=还在领先，1=已被顶）。
                var seenListingIds = new HashSet<long>();
                var entries = new List<MyBidEntry>(rawEntries.Count);
                foreach (var entry in rawEntries)
                {
                    if (seenListingIds.Add(entry.Bid.ListingId))
                        entries.Add(entry);
                }

                var replyBody = BuildMyBidBody(entries, reqMode);

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    (ushort)CmdPacketTypeA21.AUCTION_MY_BIDDING_INFO,
                    replyBody));

                FileLogger.Log(
                    $"[AuctionMyBidsReply] cid={cid} raw={bids.Count} afterFilter={rawEntries.Count} deduped={entries.Count} mode={reqMode} " +
                    $"len={replyBody.Length}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionMyBidsError] cid={cid} {ex}");
                await SendEmptyMyBids(session, reqMode);
            }
        }

        /// <summary>我的竞价记录 (bid + listing 详情) 元组，构造 159B 记录时用。</summary>
        private readonly struct MyBidEntry
        {
            public readonly AuctionBidRecord Bid;
            public readonly AuctionListing Listing;
            public MyBidEntry(AuctionBidRecord bid, AuctionListing listing)
            {
                Bid = bid;
                Listing = listing;
            }
        }

        /// <summary>
        /// 构造 0x00BD 应答体：flag(1B) + mode(1B, 回显) + count(WORD, 2B) + N×159B 记录。
        /// ★ 0x00BD 是【紧凑 159B 布局】，不是 0x00BA 的 168B 恒等映射！客户端回调
        ///   0x111FE70 读 N×159B，渲染器 0x232e073（mode2）把 159B 记录逐字段重映射到
        ///   168B 行结构（row[k]）再交给共享渲染器 0x232d2a0。反汇编定案的重映射表：
        ///   record[0..3]   → row[0..3]   listing.ListingId（挂牌标识低位）
        ///   record[4..7]   → row[4..7]   listing.ExpiresAtUnix（到期时间）
        ///   record[8..11]  → row[9..12]  ★ bid.BidAmount（我的出价）
        ///   record[12..15] → row[13..16] 一口价总额（=BuyoutPrice × ItemCount）
        ///   record[16..28] → row[21..33] 13B null 结尾字符串（保持 0）
        ///   record[29..36] → row[34..41] 保持 0
        ///   record[37]     → row[42]     byte 状态标志（bid.Status：0=领先中 1=被超价）
        ///   record[38..120]→ row[43..125] 83B 道具结构（块内 [5..9] 需反排）
        ///   record[121..150]→ row[126..155] 子对象A 占位（保持 0）
        ///   record[151..154]→ row[156..159] ★ 装备类剩余秒数（row[156] 语义）
        ///   record[155..158]→ row[17..20]  平均市价位（保持 0 跳过「高于市价2倍」提示）
        /// 与 0x00BA 的差异：头部去掉了 row[8] 未使用字节 + row[17..20] 平均市价字段共 5 字节，
        /// 故道具块从 43 前移到 38；尾部子对象B 从 12B 缩到 8B（其中前 4B=剩余秒数，后 4B=平均市价）。
        /// </summary>
        private static byte[] BuildMyBidBody(IReadOnlyList<MyBidEntry> entries, byte reqMode)
        {
            const int RecordSize = 159;
            const int ItemOffset = 38;      // ★ 0x00BD 道具块偏移 = 38（非 0x00BA 的 43、非 0x00BC 的 30）
            const int ItemSize = 83;
            const int RemainSecondsOffset = 151; // ★ 装备类剩余秒数 → record[151..154]（渲染重映射到 row[156]）

            var writer = new GamePacketWriter();
            writer.WriteByte(1);             // flag（框架预读，必须≠0）
            writer.WriteByte(reqMode);       // mode 回显（0 → window7 / 1 → window8）
            // WORD count：防御性限幅到 1000 条（实际活跃 bid 远低于此）。
            var sendCount = entries.Count > 1000 ? 1000 : entries.Count;
            writer.WriteUInt16((ushort)sendCount);

            var myNowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            for (int i = 0; i < sendCount; i++)
            {
                var entry = entries[i];
                var listing = entry.Listing;
                var bid = entry.Bid;
                var record = new byte[RecordSize];

                // ---- 头部（159B 紧凑布局，渲染时由 0x232e073 重映射到 168B 行结构）----
                WriteInt32(record, 0, (int)listing.ListingId);     // record[0..3] 挂牌标识低位
                WriteInt32(record, 4, (int)listing.ExpiresAtUnix); // record[4..7] 挂牌标识高位 / 到期
                WriteInt32(record, 8, bid.BidAmount);              // record[8..11] ★ 我的出价（→row[9]）
                // 一口价总额（= BuyoutPrice × ItemCount）
                var stackCount = Math.Max(1, listing.Item.ItemCount);
                var totalRaw = (long)listing.BuyoutPrice * stackCount;
                var totalPrice = totalRaw > int.MaxValue ? int.MaxValue : (int)totalRaw;
                WriteInt32(record, 12, totalPrice);                // record[12..15] 一口价总额（→row[13]）
                // record[16..28] 13B 字符串 / record[29..36] 均保持 0
                record[37] = (byte)Math.Min(byte.MaxValue, Math.Max(0, bid.Status)); // record[37] 状态（→row[42]）

                // ---- 83B 道具结构 @record[38]（→row[43]，块内 [5..9] 反排）----
                // 复用 BuildSafeItemBlock + wire[5..9] 反排：
                //   ItemCore [5]=Attr(强化位域), [6..9]=Value(数量/实例UID)
                //   wire 顺序反过来：wire[5]=Attr, wire[6..9]=Value
                // 详见 BuildMyListingBody 注释。
                var coreBytes = BuildSafeItemBlock(listing);
                var copy = Math.Min(coreBytes.Length, ItemSize);
                Buffer.BlockCopy(coreBytes, 0, record, ItemOffset, copy);
                if (copy > ItemCore.AttrOffset)
                {
                    var value = coreBytes[ItemCore.ValueOffset]
                                | (coreBytes[ItemCore.ValueOffset + 1] << 8)
                                | (coreBytes[ItemCore.ValueOffset + 2] << 16)
                                | (coreBytes[ItemCore.ValueOffset + 3] << 24);
                    var attr = coreBytes[ItemCore.AttrOffset];
                    record[ItemOffset + 5] = attr;             // 块[5]    = 强化位域
                    WriteInt32(record, ItemOffset + 6, value); // 块[6..9] = 数量 / 实例UID
                }

                // ---- 剩余时间（与 0x00BA / 0x00BC 同语义，统一走两条互斥路径）----
                // 非装备类：由 BuildSafeItemBlock 写道具块偏移54（绝对到期 Unix），此处无需再写。
                // 装备类：写 record[151..154]（渲染时 0x232e073 重映射到 row[156]=装备剩余秒数）。
                WriteRemainSeconds(record, RemainSecondsOffset, listing, myNowUnix);

                // record[121..150] 子对象A / record[155..158] 平均市价位 均保持 0
                writer.WriteBytes(record);
            }

            return writer.ToArray();
        }

        /// <summary>失败/无 cid 时回 0x00BD 空列表。与 SendEmptyMyListings 风格对称，但 count 是 WORD。</summary>
        private static async Task SendEmptyMyBids(EnhancedClientSession session, byte reqMode)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(1);       // flag（框架预读，必须≠0）
            writer.WriteByte(reqMode); // mode 回显
            writer.WriteUInt16(0);     // count=0（WORD，2 字节）
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketTypeA21.AUCTION_MY_BIDDING_INFO,
                writer.ToArray()));
        }

        /// <summary>
        /// 解析 0x00BC / 0x00BD 请求体中的窗口 mode 标志（请求体首字节）。
        /// 客户端两条发送路径：0x2337954 写 0（window type7，实测「上架的物品」标签）、
        /// 0x1457241 写 1（window type8）。应答必须回显该值，否则回调会把记录填进
        /// 另一个窗口类的面板，导致用户可见的网格始终为空。
        /// </summary>
        private static byte ResolveListMode(byte[] body)
        {
            if (body == null || body.Length == 0)
            {
                // 理论上不会出现（实测请求体恒为 1 字节）；保守回退到旧行为。
                FileLogger.Log("[AuctionListMode] 请求体为空，mode 回退为 1");
                return 1;
            }

            return body[0];
        }

        private static async Task SendEmptyMyListings(EnhancedClientSession session, byte reqMode)
        {
            // 0x00BC 回调 0x111FC90：flag(1B) + mode(1B) + count(1B) + N×151。
            var writer = new GamePacketWriter();
            writer.WriteByte(1);        // flag（框架预读，必须≠0）
            writer.WriteByte(reqMode);  // mode 回显（决定填哪个窗口类的面板）
            writer.WriteByte(0);        // count=0
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketTypeA21.AUCTION_MY_REGISTED_ITEM_INFO,
                writer.ToArray()));
        }

        /// <summary>
        /// 构造 0x00BC 应答体：flag(1B) + mode(1B) + count(1B) + N×151B。
        /// 布局来自客户端回调 0x111FC90 重新反汇编（2026-09-02）：
        ///   flag(byte0, 框架预读, 必须≠0) -> 0x27c5230 读 1B mode(byte1)
        ///     -> 0x27c5230 读 1B count(byte2) -> N×0x97(151) 记录
        ///   mode==1 -> 建 type8 窗口，填 [window+0x120]，fill=0x1458c50，重绘=0x1453490
        ///   mode==0 -> 建 type7 窗口，填 [window+0x130]，fill=0x2338d50，重绘=0x232da80
        ///   两个 fill 函数同构（节点 ctor 0x1457100、步长 0x97=151），记录格式通用，
        ///   因此只需把应答 mode 回显为客户端请求值即可路由到正确窗口。
        ///
        /// 151B 记录内部切分（2026-09-03 逐字段反汇编 + 线上实测双重定案）：
        ///   [0..3]     挂牌标识低位（购买请求回传）
        ///   [4..7]     挂牌标识高位 / 到期时间
        ///   [8..11]    ★ 单价 —— 网格价格列显示的字段
        ///   [12..15]   ★ 一口价总额
        ///   [16..28]   13B null 结尾字符串
        ///   [29]       byte 标志
        ///   [30..112]  83B 道具结构（ItemCore 前 83 字节，块内 [5..9] 需反排）★ 关键
        ///   [113..146] 两个子对象
        ///   [147..150] 平均市价
        ///
        /// ⚠⚠ 历史误判警示：曾一度认定道具块偏移 = 43，并据此改坏过一版。
        ///   错因是把「行结构偏移」当成了「wire 偏移」——
        ///   0x232d2a0 里的 `lea esi,[ebx+0x383]`（0x383-0x358 = 43）指的是
        ///   **客户端 168B 行结构内**的道具块位置，不是 wire 记录内的位置。
        ///   0x00BC 的 151B wire 记录与 168B 行结构长度不同，中间隔着一层
        ///   逐字段重映射（0x232ddc4 分支，`lea esi,[edx+0x1e]` = wire[30] 才是
        ///   道具块的真实 wire 偏移）。实测已闭环：道具块写 30 时装备/材料全部
        ///   正确渲染，写 43 时整行变金币且悬停崩溃。
        ///
        /// ★ 两条渲染路径必须区分（同一个渲染器 0x232d2a0，喂参方式不同）：
        ///   路径A 0x232dc41：`sub esp,0xa8` + `rep movsd 0x2a` 从 wire 记录
        ///     **原样拷 168B**（恒等映射 row[k]=wire[k]）。这是给 0x00BD 我的竞价的
        ///     159B 记录用的（159 接近 168，仅尾部越界读几字节）。
        ///   路径B 0x232ddc4：**逐字段重映射**，151B wire → 168B 行结构。
        ///     0x00BC 走这条。`lea edx,[ebx+8]` 钉死 edx = wire 基址，
        ///     栈上行结构基址 = ebp-0xac，据此得出完整映射表（见 BuildMyListingBody 内注释）。
        ///   ⛔ 切勿把路径A 的恒等映射套用到 0x00BC，否则所有头部字段整体错位。
        ///
        /// 装备 / 材料的差异化展示机制（"装备有数值信息无数量，材料有数量无多余信息"）：
        ///   客户端拿到道具块后走的是与背包完全相同的 ItemCore 解释路径 ——
        ///   先用 core[0]=ItemKind 判定种类，再据此解释 core[5..8]=Value 这个**联合字段**：
        ///     ItemKind=1(装备)/5(宠物)/8(时装) -> Value 是唯一实例 UID，
        ///         道具不可堆叠，UI 不绘制数量，改绘 Attr&0x1F(强化)/
        ///         EnchantUpgradeCount(锻造)/AmplifyType+AmplifyValue(增幅)；
        ///     其余种类(2消耗品/3材料/4任务/9徽章/10-14各类材料) -> Value 是堆叠数量，
        ///         UI 绘制数量数字，且因这些种类不存在强化/锻造/增幅槽位而不绘制这些信息。
        ///   所以服务端**只要把道具块整体落在 record[30]**（且块内 [5..9] 按拍卖 wire
        ///   顺序反排），装备与材料就会各按自己的种类正确渲染；反之若偏移错位，
        ///   ItemKind/Value 全部读到错误字节，就会出现"材料显示强化锻造增幅"这类串台现象。
        ///   这也意味着服务端不需要（也不应该）按种类裁剪字段 —— 裁剪只会破坏
        ///   客户端的种类判定。
        ///   ★ 数量不需要写头部：材料数量来自道具块 Value(块[6..9])，
        ///     这就是为什么头部价格字段写错时材料数量依然显示正常。
        ///
        /// ⚠ 已推翻的旧注释：曾写"record[17..20] 会被 0x232d34a 读走，必须保持 0"。
        ///   实际 0x232d34a 读的是 `[ebx+0x369]` = **行结构** row[17]，
        ///   而 row[17] 的来源是 wire[147..150]（平均市价），与 wire[17] 无关。
        ///   wire[16..28] 是一段 13B null 结尾字符串，不是道具构造器参数。
        /// </summary>
        private static byte[] BuildMyListingBody(IReadOnlyList<AuctionListing> listings, byte reqMode)
        {
            const int RecordSize = 151;
            // ★ 道具块偏移：客户端 0x00BC 回调 0x111FC90 对每个记录做
            //     lea esi,[ebx+0x71]            ; 记录基址 = ebx+113
            //     lea ecx,[esi-0x53]            ; 0x53=83 -> ecx = ebx+30
            //     call 0x29190c0               ; 道具构造器，喂入 record[30] 起的 83 字节
            //   即道具块必须落在 record[30]（非 43）。此前误改为 43 导致整块错位 13 字节，
            //   客户端读到错误道具块 -> 全部渲染成金币、悬停崩溃。
            const int ItemOffset = 30;
            const int ItemSize = 83;

            var writer = new GamePacketWriter();
            writer.WriteByte(1);       // flag（框架预读，必须≠0）
            writer.WriteByte(reqMode); // mode 回显（0 -> window7 / 1 -> window8）
            writer.WriteByte((byte)Math.Min(listings.Count, 255)); // count（1 字节）

            var myNowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (var listing in listings)
            {
                var record = new byte[RecordSize];

                // ---- 30B 头部 ----
                // ★★★ wire → 客户端 168B 行结构 的字段映射（2026-09-03 逐条反汇编定案）
                //   证据：0x00BC 走「逐字段重映射」路径 0x232ddc4（151B ≠ 168B 必须重排），
                //   其中 `lea edx,[ebx+8]` 钉死 edx = wire 记录基址，栈上行结构基址 = ebp-0xac：
                //     wire[0..3]    -> row[0]        挂牌标识低位（购买请求 0x2323080 回传）
                //     wire[4..7]    -> row[4]        挂牌标识高位（同上回传）
                //     wire[8..11]   -> row[9]   ★★  网格价格列显示的就是这个字段
                //     wire[12..15]  -> row[13]  ★★  一口价总额
                //     wire[16..28]  -> row[21..33]   13B null 结尾字符串
                //     wire[29]      -> row[42]       byte 标志
                //     wire[30..112] -> row[43..125]  83B 道具块
                //     wire[113..142]-> row[126..155] / wire[143..146] -> row[156]  两个子对象
                //     wire[147..150]-> row[17]       平均市价
                //   ⚠ 另一条路径 0x232dc41（`rep movsd 0x2a` 从 wire 原样拷 168B，恒等映射）
                //     是给 0x00BD 我的竞价的 159B 记录用的，勿混用。0x00BC 道具块在 wire[30]
                //     而非 wire[43] 已由实测闭环证实 -> 确认 0x00BC 走的是重映射路径。
                //
                // 跳过判定 0x232da80 @0x232dc1a：edx=wire[0]; or edx,wire[4]; je skip
                //   -> wire[0] 与 wire[4] 同时为 0 时整行被隐藏，必须至少一个非 0。
                //
                // ⛔ 历史 bug（本次修复）：原先把 listing.Item.ItemCount 写在 wire[8]，
                //   而 wire[8] 是价格字段 -> 网格「一口价」列显示成数量 11/14/1/1。
                //   数量根本不需要写头部：它由道具块 Value(块[6..9]) 提供，
                //   所以材料数量一直显示正常。
                //   row[9] 是价格的独立佐证：0x232576a `cmp edi,[eax+0x361]` + `setl`
                //   -> 输入值低于 row[9] 时报错 0xeb09（出价低于最低价）；
                //   row[13] 是一口价总额的佐证：0x2322f93 把 row[13] 作为金额传进
                //   购买确认框 0x2304c70(msg 0x234)，且 0x2322e59 用 row[13]/数量 得单价，
                //   0x2322f14 用 row[17]×数量×2 与 row[13] 比较（高于市价 2 倍提示 msg 0x115）。
                var unitPrice = listing.BuyoutPrice;
                var stackCount = Math.Max(1, listing.Item.ItemCount);
                var totalRaw = (long)unitPrice * stackCount;
                var totalPrice = totalRaw > int.MaxValue ? int.MaxValue : (int)totalRaw;

                WriteInt32(record, 0, (int)listing.ListingId);        // 挂牌标识低位（唯一，供购买回传定位）
                WriteInt32(record, 4, (int)listing.ExpiresAtUnix);    // 挂牌标识高位 / 到期时间
                WriteInt32(record, 8, unitPrice);                     // ★ 网格价格列 = 单价
                WriteInt32(record, 12, totalPrice);                   // ★ 一口价总额（单价×数量）
                // wire[16..28] 是 13B null 结尾字符串（-> row[21..33]），保持全 0 = 空串。
                // ⛔ 不可再往 wire[24]/wire[28] 写挂牌ID/状态：那两处落在字符串区内，
                //    会被当作字符串内容渲染（原代码的隐性错误，本次一并移除）。
                // wire[29] 标志位保持 0。
                // wire[147..150] 平均市价保持 0 -> 客户端 0x2322ed6/0x2322edb 判 -1/0 直接跳过
                //    「价格高于市价 2 倍」提示，避免误弹窗。

                // ---- 83B 道具结构 @record[30] ----
                var coreBytes = BuildSafeItemBlock(listing);
                {
                    var copy = Math.Min(coreBytes.Length, ItemSize);
                    Buffer.BlockCopy(coreBytes, 0, record, ItemOffset, copy);

                    // ★★★ 拍卖行 wire 的道具块在 [5..9] 区间与 ItemCore 的字段顺序相反：
                    //     ItemCore  : [5..8] = Value(数量/实例UID)   [9]    = Attr(强化位域)
                    //     拍卖 wire : [5]    = Attr(强化位域)        [6..9] = Value(数量/实例UID)
                    //   offset 10 之后两者完全对齐（[10]耐久 [12]封印 [17]锻造 [18]增幅 [54]符文 …）。
                    //
                    //   反汇编依据（0x232d2a0 逐字段提取道具块传给上层渲染）：
                    //     mov al,  byte  [ebp-0x343]  ; 块[5]     -> and al,0x1f  = 强化等级
                    //     mov edx, dword [ebp-0x342]  ; 块[6..9]  = 数量 / 实例UID
                    //
                    //   不重排时的实测症状（用户反馈，已复现闭环）：
                    //     材料 Value=11 写在 [5..8] -> 块[5]=11 被读成「强化 +11」；
                    //     而数量取自块[6..9] = 11>>8 = 0 -> 数量显示 0。
                    //     装备 UID=0x76F8B35C -> 块[5]=0x5C, &0x1F=28 -> 装备出现「强化 +28」。
                    if (copy > ItemCore.AttrOffset)
                    {
                        var value = coreBytes[ItemCore.ValueOffset]
                                    | (coreBytes[ItemCore.ValueOffset + 1] << 8)
                                    | (coreBytes[ItemCore.ValueOffset + 2] << 16)
                                    | (coreBytes[ItemCore.ValueOffset + 3] << 24);
                        var attr = coreBytes[ItemCore.AttrOffset];

                        record[ItemOffset + 5] = attr;            // 块[5]    = 强化位域
                        WriteInt32(record, ItemOffset + 6, value); // 块[6..9] = 数量 / 实例UID
                    }
                }

                // ---- ★★★ 剩余时间（round74）：装备类字段 row[156] ----
                //   0x00BC 是「逐字段重映射」路径（151B → 168B 行结构），
                //   wire[143..146] -> row[156]，与搜索路径的 wire[156] 不是同一个位置。
                //   语义同样是【剩余秒数】，依据同 BuildSearchBody 里的注释。
                WriteRemainSeconds(record, 143, listing, myNowUnix);

                // ---- 尾部 record[113..150] 38B 已自动为 0 ----
                writer.WriteBytes(record);
            }

            return writer.ToArray();
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        /// <summary>
        /// 构造一条记录的道具块（83B）。对空/过短的 ItemCoreData 做兜底，
        /// 用 ItemTemplateId + ItemCount + ItemKind 生成最小合法道具块，避免客户端
        /// 把全 0 块读成金币类（ItemKind=0）而渲染成金币、甚至越界读取闪退。
        ///
        /// ⚠ 根因（2026-09-03 用户反馈闭环）：早期测试上架的 listing 23~30 的
        ///    item_core_data 为 NULL，读取后成空数组，原代码留全 0 -> 客户端把
        ///    ItemKind 读成 0（金币），整页渲染金币且切页崩溃。
        /// </summary>
        private static byte[] BuildSafeItemBlock(AuctionListing listing)
        {
            var coreBytes = listing.Item.ItemCoreData;
            var usable = coreBytes != null && coreBytes.Length >= ItemCore.AttrOffset + 1;

            var block = new byte[ItemBlockSize];
            if (usable)
            {
                var copy = Math.Min(coreBytes.Length, ItemBlockSize);
                Buffer.BlockCopy(coreBytes, 0, block, 0, copy);
            }
            else
            {
                // 最小合法道具块：Kind(1B) + ItemId(4B) + Value(4B，数量) + Attr(1B=0)。
                block[ItemCore.ItemKindOffset] = ItemCore.KindMaterial; // 材料（最常见，避免种类判定异常）
                WriteInt32(block, ItemCore.ItemIdOffset, listing.Item.ItemTemplateId);
                WriteInt32(block, ItemCore.ValueOffset, Math.Max(1, listing.Item.ItemCount));
            }

            // ★★★ 剩余时间（round70 定案，推翻 round67 的「偏移41」结论）：
            //   客户端行解析 0x1de32a0 的 0x1de35dc 分支读「剩余时间」的真相：
            //     mov ax, word [ebp-0x33e]   ; 道具块偏移10(word) = 「剩余天数」
            //     cmp dx, ax / jae 0x1de35fa ; 偏移10==0 时跳到下面读偏移54
            //     movzx eax, ax
            //     imul eax, eax, 0x15180     ; ×86400
            //     add  eax, 0x44a53c70       ; + 0x44a53c70(1151679600 = 2006-06-30 Unix秒)
            //     jmp  0x1de3600
            //   0x1de35fa: mov eax, [ebp-0x312]  ; 偏移10==0 → 读道具块偏移54(int32) = 绝对到期时间
            //   0x1de3600: push eax / call 0x1193330 → 0x1193362 mov [esi+0x45c],eax（行对象到期字段）
            //
            //   格式化函数 0x1ddffd0：剩余秒 = 到期时间 - 0x1105710()(当前Unix秒)，
            //     ≤0 →「0小时」；>86400 →「X天」；否则「约X小时」。
            //
            //   因此正确写法是【偏移54 写绝对到期 Unix 秒 + 偏移10 置 0】：
            //     - 偏移54（RuneOffset）客户端当 int32 绝对到期时间读，与 ExpiresAtUnix 同语义；
            //     - 偏移10（DurabilityOffset）必须为 0，否则客户端把它当「剩余天数」算成错误时间。
            //   ⚠⚠ round74 重要补充：以上只对【非装备类】有效！
            //     0x1de35bb 这条分支的前置门闸是 `vtable[0x2c](itemObj, 1)`：
            //     装备类该调用返回 false -> 0x1de35ce `je 0x1de3606` 直接跳过整段，
            //     [item+0x45c] 永远保持构造时的 0 -> 渲染出「0小时」。
            //     装备类的剩余时间走另一条【互斥】的路：
            //       wire[156] -> 0x1126720 -> [item+0x12e8]，语义是「剩余秒数」，
            //     见 BuildSearchBody / BuildMyListingBody 里的 WriteRemainSeconds 调用。
            //     （用户反馈「改了偏移54 仍然是 0 小时」即由此而来 —— 在售的全是装备。）
            //
            //   ⚠ 另外注意「偏移10」的风险（用户提出的「负数/超上限」猜测，成立）：
            //     偏移10 非 0 时到期 = 偏移10×86400 + 1151679600，
            //       · 小值（如装备耐久 35）-> 约 2007 年 -> 早已过期 -> 负数 ->「0小时」
            //       · ≥11508 -> 超过 int32 上限溢出成负数 -> 同样「0小时」
            //     所以「偏移10 置 0」这步是必须的。
            if (listing.ExpiresAtUnix > 0)
            {
                // 偏移10 置 0：强制客户端走「偏移54 绝对到期时间」分支
                if (ItemCore.DurabilityOffset + 2 <= block.Length)
                {
                    block[ItemCore.DurabilityOffset] = 0;
                    block[ItemCore.DurabilityOffset + 1] = 0;
                }
                // 偏移54 写绝对到期时间（int32 绝对 Unix 秒）
                if (ItemCore.RuneOffset + 4 <= block.Length)
                    WriteInt32(block, ItemCore.RuneOffset, (int)listing.ExpiresAtUnix);
            }

            return block;
        }

        /// <summary>
        /// CMD 0x00BE：我的拍卖历史（round58 实施）。
        /// 与 0x00BC「我的上架」（仅 Active）相对，本面板展示卖家全部状态挂牌
        /// （Active / Sold / Cancelled / Expired / Settled）。wire 协议格式暂按
        /// 0x00BC 同款 151B 镜像处理：flag(1) + mode(1, 回显) + count(1B BYTE) + N × 151B。
        /// 若实测发现客户端 0x00BE 用 159B 或 count 是 WORD，再按需调整为 A/B 对照。
        /// </summary>
        public async Task HandleMyAuctionHistory(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            var reqMode = ResolveListMode(body);
            FileLogger.Log(
                $"[AuctionMyHistory] cid={cid} 0x00BE 拍卖历史请求 len={body?.Length ?? 0} reqMode={reqMode}");

            if (cid <= 0)
            {
                await SendEmptyMyHistory(session, reqMode);
                return;
            }

            try
            {
                var listings = _auction.Repository.LoadAllListingsBySeller(cid);
                var replyBody = BuildMyListingBody(listings, reqMode);

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    (ushort)CmdPacketTypeA21.AUCTION_MY_AUCTION_HISTORY,
                    replyBody));

                FileLogger.Log(
                    $"[AuctionMyHistoryReply] cid={cid} count={listings.Count} mode={reqMode} len={replyBody.Length}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionMyHistoryError] cid={cid} {ex}");
                await SendEmptyMyHistory(session, reqMode);
            }
        }

        /// <summary>0x00BE 失败/无 cid 时的空列表回退（与 SendEmptyMyListings 对称）。</summary>
        private static async Task SendEmptyMyHistory(EnhancedClientSession session, byte reqMode)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteByte(reqMode);
            writer.WriteByte(0);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketTypeA21.AUCTION_MY_AUCTION_HISTORY,
                writer.ToArray()));
        }

        private const int ItemBlockSize = 83;

        /// <summary>
        /// 均价查询应答（CMD 0x00B6）+ 统计完成通知（NOTI 0x030D）。
        ///
        /// 布局来自对客户端 DNF.exe 的静态逆向（86JP，S4A21 客户端）：
        ///
        /// 1) opcode 注册表由 0x163c4a0(manager, opcode, callback, userarg) 建立；
        ///    处理游戏服连接的初始化函数位于 0x11442b0，其中
        ///      0x00B6 -> 0x112CBB0    0x030D -> 0x111EE20
        ///    （另有 0x115f1d0 / 0x117fe50 等几张表服务于其它连接，不是本服路径；
        ///      判定依据：只有 0x11442b0 注册了 0x030D，而实测 OVERFLOW_INFO
        ///      正是 0x030D 触发的。）
        ///
        /// 2) 回调签名 cb(userarg, flag, errcode)：flag != 0 才是成功分支。
        ///
        /// 3) 0x112CBB0 成功分支的读取序列（0x27c5230=读n字节 / 0x27c5310=读dword）：
        ///       byte  mode      0 -> 建 type 7 窗口；1 -> 建 type 8 窗口
        ///       dword key      传给窗口工厂 0x23009b0(global,type,key,&v1,&v2)
        ///       dword unused   读进局部变量但后续未使用，必须占位
        ///       6 x dword      vector1（0x112cc94 处循环，edi=6）
        ///       6 x dword      vector2（0x112cd04 处循环）
        ///    合计 1 + 4 + 4 + 24 + 24 = 57 字节。
        ///
        /// 4) 客户端发送 0x00B6 请求处（0xa96bd0）只写入两个值 [this+0x250]、
        ///    [this+0x254]，因此应答的 key / unused 直接回显请求的 8 字节即可。
        ///
        /// 5) 0x111EE20（NOTI 0x030D）成功分支【一个字节都不读】：直接向
        ///    0x3a5c9b8 投递 UI 消息 0x209（串 0x7a3e），再调
        ///    a9b8e0(1) / aaa0a0() / 9f6890(1,1) —— 即"结束『正在统计拍卖行
        ///    价格…』等待态、解锁竞拍价/一口价输入框"的信号。
        ///
        ///    但实测纠正：回调不读 ≠ 可以发空包体。发包日志对照——
        ///      NOTI 0x039B body=1B 正常 / NOTI 0x00B7 body=2B 正常
        ///      NOTI 0x030D body=0B -> OVERFLOW_INFO(00-0D-03)
        ///    说明框架在调用回调前会先消费 1 字节作为 flag，因此 NOTI 包体
        ///    【至少 1 字节】，且第 1 字节非 0 才会走成功分支。
        ///    回调零读取，所以多发字节不会溢出（只有发少了才会）。
        ///    默认发 1 字节 0x01，可用 notihex 覆盖。
        /// </summary>
        public async Task HandleAskAveragePrice(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            FileLogger.Log(
                $"[AuctionCap] cid={cid} AUCTION_ASK_AVERAGE_PRICE(0x00B6) 均价查询 " +
                $"len={body?.Length ?? 0} hex={BitConverter.ToString(body ?? Array.Empty<byte>())}");

            var config = ReadReplyConfig();

            // ★ 阶段四：按物品动态推荐价（keyminprice=1）。
            //   开启后从请求体读出物品模板 id，查该物品当前拍卖行最低一口价，
            //   用它覆盖全局固定 keyprice，使不同物品推送不同参考价。
            //   无在售挂牌时回落到 keyprice 兜底（保持旧行为不变）。
            var itemId = 0;
            if (body != null && body.Length >= config.KeyOffset + 4)
                itemId = BitConverter.ToInt32(body, config.KeyOffset);

            // ★ round95 修订（2026-09-05 用户拍板）：金币寄售差价费率「有参考价才显示」。
            //   客户端费率函数 0x1455900 按 ratio = 输入价 / [panel+0x1a4] 分档：
            //     ratio<0.6 -> -1(价格过低)   0.6~1.5 -> 基础 1%/5%
            //     1.5~1.6 -> 30%  1.6~1.7 -> 40%  1.7~1.8 -> 50%  >=1.8 -> 95%（红字）
            //   [+0x1a4]==0 时走官方「无均价数据」分支，恒基础 1%/5% 且无红字。
            //   参考价由 mode=1 应答 key 经 0x112CBB0->0x1456910->SetRefPrice(0x1456240)
            //   写入（key>0 时预填输入框；key<=0 跳过预填）。
            //   用户需求 = 无参考价时上架不出现差价红字（不要固定费率），因此：
            //     · 有同面额在售挂牌 -> 下发真实最低一口价作参考价（官方行为：
            //       预填参考价、费率随真实差价走）；
            //     · 无任何在售挂牌（无参考价）-> 下发 key=0，客户端走无均价分支，
            //       恒基础费率、不出现差价红字，也不做低价拦截。
            //   只影响 mode=1 开窗应答；mode=0 播种应答（type7 价格库）保持不变。
            var isGoldAvgQuery = AuctionRepository.IsGoldItem(itemId);

            byte[] replyBody;
            if (isGoldAvgQuery)
            {
                var goldRefPrice = ResolveMinGoldBuyoutPrice(itemId, cid);
                if (goldRefPrice > 0)
                {
                    config.KeyPrice = goldRefPrice;
                    replyBody = BuildReplyBody(body, config);
                    FileLogger.Log(
                        $"[AuctionGoldFee] cid={cid} itemId={itemId} " +
                        $"金币寄售均价查询：参考价={goldRefPrice}（在售最低价）");
                }
                else
                {
                    replyBody = BuildReplyBodyWithKey(body, config, 0);
                    FileLogger.Log(
                        $"[AuctionGoldFee] cid={cid} itemId={itemId} " +
                        $"金币寄售均价查询：无在售挂牌，key=0（无参考价，不显示差价红字）");
                }
            }
            else
            {
                if (config.KeyMinPrice && itemId > 0)
                {
                    // 排除自己上架的（官方 DNF 不让自己买/参考自己的挂牌价）。
                    config.KeyPrice = ResolveMinBuyoutPrice(itemId, config.KeyPrice, cid);
                }
                replyBody = BuildReplyBody(body, config);
            }

            var notiLen = -1;
            try
            {
                // 实验开关：某些情况下只想验证 NOTI 的解锁效果。
                if (config.SendCmd)
                {
                    // ★ 双应答（根因修复，2026-09-02）
                    //
                    // 客户端 0x112CBB0 按应答第 1 字节 mode 二选一分派：
                    //   mode=0 -> CreateWindow(7) -> 0x2335f20 -> 0x23317d0
                    //             ◇ 唯一的价格入库点 ◇
                    //             把 v1/v2 写进浏览窗口 0x114 对象并置 [+0x224]=1
                    //   mode=1 -> CreateWindow(8) -> 0x1456910 -> 0x1456240
                    //             只填物品名，12 个价格 int 【整个丢弃】
                    //
                    // 只发 mode=1 ⇒ [+0x224] 恒为 0 ⇒ "正在统计拍卖行价格……"常驻、
                    // 竞拍价/一口价输入框（ctl2 +0x18c / ctl3 +0x194）永远 disabled。
                    // 因此必须先补发一条 mode=0 把价格"播种"进客户端，再发 mode=1 开窗。
                    if (config.Dual)
                    {
                        var seedBody = BuildReplyBodyWithMode(body, config, 0);
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                            0x01,
                            (ushort)CmdPacketTypeA21.AUCTION_ASK_AVERAGE_PRICE,
                            seedBody));

                        FileLogger.Log(
                            $"[AuctionDual] cid={cid} step1/2 mode=0 (seed price) " +
                            $"len={seedBody.Length} hex={BitConverter.ToString(seedBody)}");

                        if (config.DualDelayMs > 0)
                            await Task.Delay(config.DualDelayMs);
                    }

                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x01,
                        (ushort)CmdPacketTypeA21.AUCTION_ASK_AVERAGE_PRICE,
                        replyBody));

                    if (config.Dual)
                    {
                        FileLogger.Log(
                            $"[AuctionDual] cid={cid} step2/2 mode={config.Mode} (open regist window) " +
                            $"len={replyBody.Length} hex={BitConverter.ToString(replyBody)}");
                    }
                }

                if (config.NotiMode != AveragePriceNotiMode.Off)
                {
                    byte[] notiBody;
                    if (config.NotiBody != null && config.NotiBody.Length > 0)
                        notiBody = config.NotiBody;
                    else if (config.NotiMode == AveragePriceNotiMode.Empty)
                        notiBody = Array.Empty<byte>();
                    else
                        notiBody = replyBody;

                    notiLen = notiBody.Length;

                    // A/B 对照：同一个 0x030D 分别用 cmd=0x00(NOTI) 与 cmd=0x01(CMD) 发出去。
                    // 客户端回的 OVERFLOW_INFO 包体是出问题包的头 3 字节，
                    // 所以 [00 0D 03] = NOTI 那条出错，[01 0D 03] = CMD 那条出错。
                    foreach (var notiCmd in config.NotiCmds)
                    {
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                            notiCmd,
                            (ushort)NotiPacketTypeA21.ITEM_AVERAGE_PRICE_LIST,
                            notiBody));
                    }
                }

                // 长度阶梯：一次请求发多个不同长度的 0x030D，用客户端回的
                // OVERFLOW_INFO 条数反推"到底要几字节"，省掉反复重启/登录。仅调试用。
                // 包体 = 0x01（flag 非 0 -> 成功分支） + 其余补 0。
                if (config.NotiLadder != null && config.NotiLadder.Count > 0)
                {
                    var lens = new List<int>();
                    foreach (var notiCmd in config.NotiCmds)
                    {
                        foreach (var len in config.NotiLadder)
                        {
                            if (len <= 0)
                                continue;
                            var buf = new byte[len];
                            buf[0] = 1;
                            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                                notiCmd,
                                (ushort)NotiPacketTypeA21.ITEM_AVERAGE_PRICE_LIST,
                                buf));
                            lens.Add(len);
                        }
                    }
                    FileLogger.Log("[AuctionNotiLadder] cid=" + cid +
                        " cmds=" + string.Join("/", config.NotiCmds) +
                        " sent=" + string.Join(",", lens));
                }

                FileLogger.Log(
                    $"[AuctionAvgReply] cid={cid} fmt={config.Format} prefix={config.Prefix} " +
                    $"mode={config.Mode} key={config.Key} " +
                    $"noti={config.NotiMode} notiCmd={string.Join("/", config.NotiCmds)} " +
                    $"cmdLen={replyBody.Length} notiLen={notiLen} " +
                    $"hex={BitConverter.ToString(replyBody)}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionAvgReply] cid={cid} send failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 阶段四：按物品查当前拍卖行「最低一口价」作为推荐价。
        /// 复用 SearchActiveListings(itemId,1,0, excludeCid)（已 ORDER BY buyout_price ASC），
        /// 取第 1 条即最低价。排除自己上架的，避免推荐「自己挂的价」（官方 DNF 不让自己买自己）。
        /// 无在售挂牌时返回 fallback（即全局 keyprice）。
        /// </summary>
        private int ResolveMinBuyoutPrice(int itemId, int fallback, int excludeCid = 0)
        {
            if (itemId <= 0)
                return fallback;

            try
            {
                var listings = _auction.Repository.SearchActiveListings(itemId, 1, 0, excludeCid);
                if (listings != null && listings.Count > 0 && listings[0].BuyoutPrice > 0)
                {
                    FileLogger.Log(
                        $"[AuctionMinPrice] itemId={itemId} 命中最低一口价 " +
                        $"{listings[0].BuyoutPrice}（挂牌 {listings.Count} 条以上）");
                    return listings[0].BuyoutPrice;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionMinPrice] itemId={itemId} 查询失败: {ex.Message}");
            }

            FileLogger.Log($"[AuctionMinPrice] itemId={itemId} 无在售挂牌，回落推荐价 {fallback}");
            return fallback;
        }

        /// <summary>
        /// 金币寄售版参考价（round95）：查该面额当前在售最低一口价（排除自己）。
        /// SearchActiveGoldListings 已 ORDER BY buyout_price ASC，取第 1 条即最低价。
        /// 与普通物品版的关键区别：无在售挂牌时返回 0（不是 fallback）——
        /// 金币场景 0 是有效信号，表示「无参考价」，调用方据此下发 key=0，
        /// 客户端走无均价分支（恒基础费率、不显示差价红字）。
        /// </summary>
        private int ResolveMinGoldBuyoutPrice(int itemId, int excludeCid = 0)
        {
            if (itemId <= 0)
                return 0;

            try
            {
                var listings = _auction.Repository.SearchActiveGoldListings(itemId, 1, 0, excludeCid);
                if (listings != null && listings.Count > 0 && listings[0].BuyoutPrice > 0)
                    return listings[0].BuyoutPrice;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionGoldFee] itemId={itemId} 参考价查询失败: {ex.Message}");
            }

            return 0;
        }

        /// <summary>NOTI 0x030D 的发送方式。</summary>
        private enum AveragePriceNotiMode
        {
            /// <summary>空包体（逆向结论：客户端成功分支不读字节）。</summary>
            Empty,
            /// <summary>与 CMD 应答同体（调试用）。</summary>
            Body,
            /// <summary>不发。</summary>
            Off,
            /// <summary>用配置里的 notihex 原始字节（调试用）。</summary>
            Custom,
        }

        private sealed class AveragePriceReplyConfig
        {
            public int Format = 20;
            public byte[] RawHex;
            public byte Mode;          // 0 -> 窗口 type 7；1 -> 窗口 type 8
            public int Key;            // 请求不足 8 字节时使用的 key
            public int Unused;         // 第 3 个 dword（占位）
            public int Price = 1000;   // vector1 的 6 个值
            public int Count = 1;      // vector2 的 6 个值
            public int Prefix;         // 在 57 字节布局前额外插入的状态字节数（调试用）
            /// <summary>
            /// 从请求体第几字节读 key（int32）后回显。
            ///
            /// 【必须是 1】2026-09-02 抓包定案：0x00B6 请求体恒为 8 字节，
            /// 布局是 `byte 0x00 + int32 物品模板id + 3 字节 0`：
            ///   00-AC-F0-99-00-00-00-00 -> 10088620（精灵气息）
            ///   00-AD-F0-99-00-00-00-00 -> 10088621（水晶碎片）
            ///   00-D3-0B-00-00-00-00-00 -> 3027
            ///   00-A0-28-00-00-00-00-00 -> 10400（赛利亚的旧拖鞋）
            /// 全部命中背包里的真实模板 id。
            ///
            /// 曾经误配成 0，等于把 key 读早 1 字节 = `模板id << 8`，再原样回显，
            /// 客户端把它当参考单价预填进一口价输入框 ⇒ 材料（id 高位字节 0x99）
            /// 变负数、小 id 装备变几百万。这就是"一口价自动填入异常"的根因。
            /// </summary>
            public int KeyOffset = 1;
            public bool SendCmd = true;

            // ---- 一口价默认值修复（2026-09-02）----
            //
            // 【实测根因】应答第 2 个字段（我们一直当"key"原样回显请求值）并不是纯窗口
            // 标识，客户端会把它当【参考单价】预填进上架窗口的一口价输入框，且**放大
            // 256 倍**（等价于左移 8 位）。
            //
            // 我们回显的是【物品模板 id】，于是：
            //   模板 31300      -> 31300<<8      = 8012800      「几百万」（与抓包实测值一致）
            //   模板 10400      -> 10400<<8      = 2662400      「几百万」
            //   模板 10088620   -> 10088620<<8   = -1712280576  「负数」（材料 id 高位字节 0x99，
            //   模板 10088621   -> 10088621<<8   = -1712280320   左移后符号位为 1）
            // 完美复现玩家反馈的"拖动材料到上架框，一口价有时负数、有时几百万"。
            //
            // KeyPrice > 0 时改为下发 KeyPrice / KeyDiv，使输入框预填 ≈ KeyPrice。
            // KeyEcho=false 则完全不回显请求值，直接用 Key。
            // 注意：该字段还兼作"有价格数据"的非零门（客户端 0x1455900 判 [+0x1a4]!=0），
            // 所以下发值会被夹到至少 1。
            public bool KeyEcho = true;
            public int KeyPrice;       // >0：期望预填进一口价输入框的参考价
            public int KeyDiv = 256;   // 客户端放大倍数补偿（<<8）
            /// <summary>
            /// 阶段四：keyminprice=1 时，从请求体读 itemId 查该物品当前拍卖最低一口价，
            /// 用它覆盖 KeyPrice（不同物品不同推荐价）。无在售挂牌时回落 KeyPrice。
            /// </summary>
            public bool KeyMinPrice;
            public AveragePriceNotiMode NotiMode = AveragePriceNotiMode.Empty;
            /// <summary>
            /// NOTI 0x030D 的独立包体（十六进制）。非空时优先于 NotiMode。
            /// 实测：NOTI 包至少要 1 字节——框架会先消费 1 字节作为回调的 flag，
            /// 发 0 字节会直接触发 OVERFLOW_INFO(00-0D-03)。
            /// </summary>
            public byte[] NotiBody;

            /// <summary>
            /// 发 0x030D 时使用的 cmd 字节列表（逗号分隔）。
            /// 0 = NOTI 路径（0x16377e0，回调参数 [opcode, arg]，框架不预读 flag）
            /// 1 = CMD  路径（0x1637680，回调参数 [opcode, flag, errcode, arg]，框架预读 1 字节 flag）
            /// 填 "0,1" 就是 A/B 对照：客户端回的 OVERFLOW_INFO 包体为 [00 0D 03] 说明
            /// NOTI 那条出错，[01 0D 03] 说明 CMD 那条出错。
            /// </summary>
            public List<byte> NotiCmds = new List<byte> { 0x00 };

            /// <summary>
            /// 长度阶梯：非空时，对同一次请求依次发送这些长度的 NOTI 包体。
            /// 每个包体 = 0x01（flag） + 其余补 0。
            /// 客户端对"读超"的包会各回一条 OVERFLOW_INFO，据此可一次测出所需长度，
            /// 省掉反复重启 / 重新登录的成本。
            /// </summary>
            public List<int> NotiLadder;

            /// <summary>
            /// ★ 双应答（根因修复，2026-09-02）。
            ///
            /// 逆向结论：客户端 0x112CBB0 收到 CMD 0x00B6 后按 mode 分派——
            ///   mode=0 -> CreateWindow(7) -> 0x2335f20(key,&v1,&v2)
            ///             -> 0x23317d0 把价格写进【浏览窗口 0x114 对象】
            ///                [+0x224]=1(价格就绪标志) [+0x228]=v1 [+0x23c]=v2
            ///   mode=1 -> CreateWindow(8) -> 0x1456910(key,&v1,&v2)
            ///             -> 0x1456240(key) 只填物品名，【12 个价格 int 被整个丢弃】
            ///
            /// 也就是说：只发 mode=1 时，价格永远进不了客户端的全局价格表，
            /// [+0x224] 恒为 0 —— 这正是"正在统计拍卖行价格……"常驻、
            /// 竞拍价/一口价输入框始终 disabled 的唯一原因。
            ///
            /// 修法：一次请求连发两条应答——
            ///   第 1 条 mode=0：让 type 7 路径把价格写进 +0x228/+0x23c 并置 +0x224=1
            ///   第 2 条 mode=1：让 type 8 路径填物品名并打开上架窗口
            /// 顺序不能颠倒（必须先入库再开窗）。
            /// </summary>
            public bool Dual = true;
            /// <summary>两条应答之间的延时（毫秒）。0 = 不延时。</summary>
            public int DualDelayMs = 30;

            // ---- CMD 0x031B（拍卖行 UI 数据刷新）应答 ----
            /// <summary>收到 0x031B 请求后是否回应答。</summary>
            public bool ListSend = true;
            /// <summary>0x031B 应答用的 cmd 字节。1 = CMD 路径（与 0x030D 实测一致）。</summary>
            public byte ListCmd = 0x01;
            /// <summary>0x031B 应答末尾的 3 个单字节。</summary>
            public byte ListB0;
            public byte ListB1;
            public byte ListB2;
            /// <summary>直接指定 0x031B 应答的原始十六进制，非空时优先于 ListStr/B0/B1/B2。</summary>
            public byte[] ListHex;
            /// <summary>0x031B 应答里的字符串（UTF-8 写入，会被裁到 31 字节以内）。</summary>
            public string ListStr = "";

            // ---- CMD 0x00B7（上架）应答 ----
            /// <summary>
            /// 收到 0x00B7 上架请求后是否回应答。
            /// 逆向（0x1126cd0）：应答 2 字节 = flag + mode。
            ///   flag != 0 时读第 2 字节：==1 -> 建 type 8 窗口 + 0x1458960；
            ///                           ==0 -> 建 type 7 窗口 + 0x2345cb0；
            ///                           其它 -> 无动作。
            ///   flag == 0 时按 errcode 分派弹错误提示框（0xd2/0x69/0x72/0x7e…）。
            /// 默认 01 01 = 成功 + type 8。
            /// </summary>
            public bool RegistSend = true;
            /// <summary>0x00B7 应答用的 cmd 字节（1 = CMD 路径）。</summary>
            public byte RegistCmd = 0x01;
            /// <summary>0x00B7 应答原始十六进制，非空时优先于 RegistFlag/RegistMode。</summary>
            public byte[] RegistHex;
            /// <summary>应答第 1 字节 flag（0 -> 走失败分支弹错误框）。</summary>
            public byte RegistFlag = 0x01;
            /// <summary>
            /// 应答第 2 字节 mode（1 -> type 8 成功回调 0x1458960；0 -> type 7 浏览回调 0x2345cb0）。
            ///
            /// ★★★ round17 修复：默认从 0x01 改为 0x00。
            ///   mode=1 走 0x1458960，只填物品名/重置输入框，【不清 0x20c 上架锁】；
            ///   mode=0 走 0x2345cb0 -> 0x232f610 -> 清 0x20c（详见 ExecuteRegist 注释）。
            ///   否则客户端「上架锁」0x20c 永远残留 -> 二次上架发不出请求 + 背包锁死。
            /// </summary>
            public byte RegistMode = 0x00;
            /// <summary>
            /// 1（默认）= 真正落库：解析包体 -> AuctionRepository.RegisterListing。
            /// 0 = 退回阶段 1 的抓包桩，只回固定应答、完全不动库存（排查协议时用）。
            /// </summary>
            public bool RegistLive = true;
            /// <summary>
            /// 非 0 时强制用该 errcode 回上架失败（调试用，支持 0x 前缀）。
            /// 客户端只认 0xd2/0x69/0x72/0x7e/0xce/0x7b/0x89，
            /// 填其它值会被当成成功——界面假装上架成功而道具还在包里。
            /// </summary>
            public byte RegistErrCode;
        }

        /// <summary>
        /// CMD 0x031B：拍卖行 UI 数据刷新请求。
        ///
        /// 触发链：
        ///   CMD 0x00B6 均价查询应答
        ///     -> 服务端发 0x030D 统计完成通知
        ///     -> 0x030D 回调 0x111EE20 在【成功与失败两个分支】都会调用 0x9f6890(1,1)
        ///        发出本请求。
        ///   ⇒ 本请求是"刷新 UI 数据"，【不是】0x030D 成功的标志；
        ///     但它能证明 0x030D 的回调确实被调到了（NOTI 表里没注册的 opcode 不会触发）。
        ///
        /// 客户端回调 0x1164630（注册点 0x11895db）的读取序列
        /// （全部 cdecl：0x1164778 一条 add esp,0x24 清掉 4 次调用的参数）：
        ///   0x274fcb0(buf, 0x20, 0)  长度前缀字符串
        ///   0x27c5230(p, 1) x 3      b0 / b1 / b2
        ///   0x553190(str, b0, b1, b2)  存进单例（3 个值放 +0/+4/+8，字符串拷到 +0x494）
        ///
        /// ★ 长度前缀的容错（0x274fc10）：
        ///     len == 0 或 len >= 0x20  -> 跳 0x274fc6e
        ///     该分支在 flag==0 时只做 buf[0]=0; return 0
        ///     ⇒ 长度填错【不会】触发 OVERFLOW_INFO，只会静默得到空串。
        ///   真正会溢出的只有末尾那 3 次 1 字节读取。
        ///
        /// 走 CMD 表（0x163c069 -> 0x1637680），框架进回调前吃掉 1 字节 flag
        /// ⇒ 包体 = flag(1) + len(4) + 字符串(len) + b0 + b1 + b2
        /// </summary>
        public async Task HandleAveragePriceList(
            EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            FileLogger.Log(
                $"[AuctionCap] cid={cid} AUCTION_AVERAGE_PRICE_LIST(0x031B) 均价列表刷新 " +
                $"len={body?.Length ?? 0} hex={BitConverter.ToString(body ?? Array.Empty<byte>())}");

            var config = ReadReplyConfig();
            if (!config.ListSend)
                return;

            var reply = BuildPriceListBody(config);
            try
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    config.ListCmd, 0x031B, reply));
                FileLogger.Log(
                    $"[AuctionPriceList] cid={cid} cmd=0x{config.ListCmd:X2} len={reply.Length} " +
                    $"hex={BitConverter.ToString(reply)}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionPriceList] cid={cid} send failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 构造 CMD 0x031B 的应答包体：
        ///   flag(1) + len(4) + 字符串(len) + b0 + b1 + b2
        /// flag 由分发框架在进回调前吃掉，作为 [ebp+0xC] 传给 0x1164630。
        /// </summary>
        private static byte[] BuildPriceListBody(AveragePriceReplyConfig config)
        {
            if (config.ListHex != null && config.ListHex.Length > 0)
                return config.ListHex;

            var strBytes = string.IsNullOrEmpty(config.ListStr)
                ? Array.Empty<byte>()
                : System.Text.Encoding.UTF8.GetBytes(config.ListStr);
            // 0x274fc10 校验：len 必须 > 0 且 <= 0x1F，否则静默降级为空串（不溢出）
            if (strBytes.Length >= 0x20)
                Array.Resize(ref strBytes, 0x1F);

            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);                 // flag：非 0 = 成功分支
            writer.WriteInt32(strBytes.Length);     // 长度前缀（dword）
            if (strBytes.Length > 0)
                writer.WriteBytes(strBytes);
            writer.WriteByte(config.ListB0);
            writer.WriteByte(config.ListB1);
            writer.WriteByte(config.ListB2);
            return writer.ToArray();
        }

        /// <summary>
        /// 构造 CMD 0x00B6 的应答包体。默认走 format=20（逆向得出的 57 字节布局）。
        /// </summary>
        private static byte[] BuildReplyBody(byte[] requestBody, AveragePriceReplyConfig config)
        {
            if (config.RawHex != null && config.RawHex.Length > 0)
                return config.RawHex;

            ResolveKeyAndUnused(requestBody, config, out var key, out var unused);
            return BuildAveragePriceReply(key, unused, config, null);
        }

        /// <summary>
        /// 解析应答里的第 2 / 第 3 个 dword。
        ///
        /// 第 2 个 dword 历史上被当作"回显请求的 key"，但实测它会被客户端当
        /// 【参考单价】预填进上架窗口的一口价输入框（放大 256 倍）。回显物品模板 id
        /// 会导致预填值变成负数或几百万，详见 AveragePriceReplyConfig 上的注释。
        /// </summary>
        private static void ResolveKeyAndUnused(
            byte[] requestBody, AveragePriceReplyConfig config, out int key, out int unused)
        {
            key = config.Key;
            unused = config.Unused;

            var off = config.KeyOffset;
            if (config.KeyEcho)
            {
                if (requestBody != null && requestBody.Length >= off + 4)
                    key = BitConverter.ToInt32(requestBody, off);
            }
            if (requestBody != null && requestBody.Length >= off + 8)
                unused = BitConverter.ToInt32(requestBody, off + 4);

            // KeyPrice > 0：按"希望输入框预填 KeyPrice"反算下发值。
            if (config.KeyPrice > 0)
            {
                var div = config.KeyDiv > 0 ? config.KeyDiv : 1;
                key = config.KeyPrice / div;
            }

            // 该字段兼作"价格数据就绪"的非零门（客户端 0x1455900 判 [+0x1a4]!=0），
            // 因此不能下发 0；同时夹掉负数避免再次出现负价预填。
            if (config.KeyPrice > 0 || !config.KeyEcho)
                key = Math.Max(1, key);
        }

        /// <summary>
        /// 同 BuildReplyBody，但允许覆盖 mode（双应答用：先 mode=0 入库，再 mode=1 开窗）。
        /// </summary>
        private static byte[] BuildReplyBodyWithMode(
            byte[] requestBody, AveragePriceReplyConfig config, byte mode)
        {
            if (config.RawHex != null && config.RawHex.Length > 0)
                return config.RawHex;

            ResolveKeyAndUnused(requestBody, config, out var key, out var unused);
            return BuildAveragePriceReply(key, unused, config, mode);
        }

        /// <summary>
        /// 同 BuildReplyBody，但强制指定 key（round14：上架成功后回 key=-1 清空待上架物品）。
        /// unused 仍按正常逻辑解析，mode 用配置值。
        /// </summary>
        private static byte[] BuildReplyBodyWithKey(
            byte[] requestBody, AveragePriceReplyConfig config, int forcedKey)
        {
            if (config.RawHex != null && config.RawHex.Length > 0)
                return config.RawHex;

            ResolveKeyAndUnused(requestBody, config, out var _, out var unused);
            return BuildAveragePriceReply(forcedKey, unused, config, null);
        }

        private static AveragePriceReplyConfig ReadReplyConfig()
        {
            var config = new AveragePriceReplyConfig();
            var configPath = System.IO.Path.Combine(
                AppContext.BaseDirectory, "Data", "auction_avg_reply.txt");
            try
            {
                if (!System.IO.File.Exists(configPath))
                    return config;

                foreach (var raw in System.IO.File.ReadAllText(configPath).Split('\n'))
                {
                    var line = raw.Trim();

                    // 剥离行内注释：允许写成 "key = value  # 说明"。
                    // 不剥离的话 "liststr =   # 留空" 会把注释整串当成字符串值。
                    var hash = line.IndexOf('#');
                    if (hash >= 0)
                        line = line.Substring(0, hash).Trim();

                    if (line.Length == 0)
                        continue;

                    var kv = line.Split(new[] { '=' }, 2);
                    if (kv.Length != 2)
                        continue;
                    var k = kv[0].Trim().ToLowerInvariant();
                    var v = kv[1].Trim();

                    // noticmd = 0,1  发 0x030D 时用的 cmd 列表（0=NOTI 路径，1=CMD 路径）。
                    if (k == "noticmd")
                    {
                        var list = new List<byte>();
                        foreach (var part in v.Split(','))
                            if (byte.TryParse(part.Trim(), out var cb) && cb <= 1)
                                list.Add(cb);
                        if (list.Count > 0)
                            config.NotiCmds = list;
                        continue;
                    }

                    // notiladder = 1,2,4,8,...  长度阶梯，逗号分隔的整数列表。
                    if (k == "notiladder")
                    {
                        var list = new List<int>();
                        foreach (var part in v.Split(','))
                            if (int.TryParse(part.Trim(), out var ln) && ln > 0)
                                list.Add(ln);
                        if (list.Count > 0)
                            config.NotiLadder = list;
                        continue;
                    }

                    // liststr = xxx   CMD 0x031B 应答里的字符串（原样取值，不参与整数解析）。
                    if (k == "liststr")
                    {
                        config.ListStr = v;
                        continue;
                    }

                    // hex / notihex / listhex 取原始十六进制串，不走整数解析。
                    // 坑：配置里写的是 "notihex = 01"（等号两侧带空格），
                    // 必须 split 之后再比 key，用 StartsWith("notihex=") 永远匹配不上。
                    if (k == "hex" || k == "notihex" || k == "listhex" || k == "registhex")
                    {
                        var bytes = ParseHex(v);
                        if (bytes != null)
                        {
                            if (k == "hex")
                                config.RawHex = bytes;
                            else if (k == "listhex")
                                config.ListHex = bytes;
                            else if (k == "registhex")
                                config.RegistHex = bytes;
                            else
                            {
                                config.NotiBody = bytes;
                                config.NotiMode = AveragePriceNotiMode.Custom;
                            }
                        }
                        continue;
                    }

                    // 允许十六进制写法（如 registerrcode = 0xd2），便于直接抄逆向里的码。
                    int n;
                    if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!int.TryParse(
                                v.Substring(2),
                                System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out n))
                            continue;
                    }
                    else if (!int.TryParse(v, out n))
                    {
                        continue;
                    }

                    switch (k)
                    {
                        case "format": config.Format = n; break;
                        case "mode": config.Mode = (byte)n; break;
                        case "key": config.Key = n; break;
                        case "unused": config.Unused = n; break;
                        case "price": config.Price = n; break;
                        case "count": config.Count = n; break;
                        case "prefix": config.Prefix = n; break;
                        case "keyoffset": config.KeyOffset = n; break;
                        // ---- 一口价预填值修复：见 AveragePriceReplyConfig 注释 ----
                        case "keyecho": config.KeyEcho = n != 0; break;
                        case "keyprice": config.KeyPrice = n; break;
                        case "keydiv": config.KeyDiv = n; break;
                        case "keyminprice": config.KeyMinPrice = n != 0; break;
                        case "sendcmd": config.SendCmd = n != 0; break;
                        // ---- 双应答（根因修复）：dual=1 时先发 mode=0 播种价格 ----
                        case "dual": config.Dual = n != 0; break;
                        case "dualdelay": config.DualDelayMs = n; break;
                        case "listsend": config.ListSend = n != 0; break;
                        case "listcmd": config.ListCmd = (byte)n; break;
                        case "listb0": config.ListB0 = (byte)n; break;
                        case "listb1": config.ListB1 = (byte)n; break;
                        case "listb2": config.ListB2 = (byte)n; break;
                        // ---- CMD 0x00B7（上架）应答 ----
                        case "registsend": config.RegistSend = n != 0; break;
                        case "registcmd": config.RegistCmd = (byte)n; break;
                        case "registflag": config.RegistFlag = (byte)n; break;
                        case "registmode": config.RegistMode = (byte)n; break;
                        case "registlive": config.RegistLive = n != 0; break;
                        case "registerrcode": config.RegistErrCode = (byte)n; break;
                        case "noti":
                            config.NotiMode = n == 0 ? AveragePriceNotiMode.Off
                                : (n == 1 ? AveragePriceNotiMode.Empty
                                : (n == 2 ? AveragePriceNotiMode.Body
                                : AveragePriceNotiMode.Custom));
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionAvgReply] config read failed: {ex.Message}");
            }
            return config;
        }

        /// <summary>解析十六进制串（忽略空格/0x 前缀等），长度非偶数时返回 null。</summary>
        private static byte[] ParseHex(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            var hex = System.Text.RegularExpressions.Regex.Replace(
                text, @"[^0-9A-Fa-f]", "");
            if (hex.Length == 0 || hex.Length % 2 != 0)
                return null;
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        private static byte[] BuildAveragePriceReply(
            int key, int unused, AveragePriceReplyConfig config, byte? modeOverride = null)
        {
            // modeOverride 非空时用它（双应答），否则用配置里的 config.Mode。
            var mode = modeOverride ?? config.Mode;
            var writer = new GamePacketWriter();

            // 调试用：验证"框架是否在回调前预读状态字节"的假设。
            // prefix=1 -> 前插 1 字节 0x01；prefix=2 -> 再插 1 字节 0x00。
            for (var i = 0; i < config.Prefix; i++)
                writer.WriteByte(i == 0 ? (byte)1 : (byte)0);

            switch (config.Format)
            {
                case 10:
                    // 回退档：旧版 37 字节。已由 packet_log.txt 实测——客户端对 0x00B6
                    // 未上报 OVERFLOW_INFO，即 37 字节不会读超，可安全发送。
                    // 布局：byte mode(0) + int32 key + int32 unused
                    //     + [price, 0] x 3 + int32 1
                    writer.WriteByte(0);
                    writer.WriteInt32(key);
                    writer.WriteInt32(unused);
                    for (var i = 0; i < 3; i++)
                    {
                        writer.WriteInt32(config.Price);
                        writer.WriteInt32(0);
                    }
                    writer.WriteInt32(1);
                    break;

                case 20:
                    // 逆向布局（默认）：byte mode + int32 key + int32 unused
                    //                 + 6 x int32(vector1) + 6 x int32(vector2) = 57 字节
                    writer.WriteByte(mode);
                    writer.WriteInt32(key);
                    writer.WriteInt32(unused);
                    for (var i = 0; i < 6; i++) writer.WriteInt32(config.Price);
                    for (var i = 0; i < 6; i++) writer.WriteInt32(config.Count);
                    break;

                case 21:
                    // 变体：只有 vector1（6 个 dword），不带 vector2 = 33 字节
                    writer.WriteByte(mode);
                    writer.WriteInt32(key);
                    writer.WriteInt32(unused);
                    for (var i = 0; i < 6; i++) writer.WriteInt32(config.Price);
                    break;

                case 22:
                    // 变体：vector1 全为 price，vector2 全为 0
                    writer.WriteByte(mode);
                    writer.WriteInt32(key);
                    writer.WriteInt32(unused);
                    for (var i = 0; i < 6; i++) writer.WriteInt32(config.Price);
                    for (var i = 0; i < 6; i++) writer.WriteInt32(0);
                    break;

                case 13:
                    // 纯成功 ACK（1 字节）
                    writer.WriteByte(0);
                    break;

                default:
                    // 未知 format 回落到布局 20
                    goto case 20;
            }

            return writer.ToArray();
        }

        /// <summary>
        /// CMD 0x014E：一口价购买（点「一口价购买」按钮时客户端发出）。
        ///
        /// ★ 请求体构造点 0x2325510（调用点 0x256a6d1，参数=购买数量）。
        ///   第二次 flush（0x2325640）之前的写入序列即 0x014E 封包 body：
        ///     前导 1 字节 + 总价 dword(单价×数量) + 数量 dword + 8 字节挂牌id(低+高)
        ///     + 13 字节字符串(@[ecx+0x36d]) + 4 字节道具标识(@[ecx+0x383])。
        ///   挂牌 id 低位 = [ecx+0x358]、高位 = [ecx+0x35c]（行结构 row[0]/row[4]）。
        ///   ⚠ 残栈传参（push 值 → getter 0x274ca50 ret0 → writer 非标准 ret 宽度）
        ///     使「前导字节」和「挂牌 id 精确偏移」无法 100% 静态确定，故本实现：
        ///       ① 打印完整请求体 hex + 多候选偏移读值（便于实测一锤定音）；
        ///       ② 用 bestOffset 执行真实业务闭环（默认 9 = 总价4+数量4 之后）。
        ///
        /// ★ 应答（回调 0x1127130，CMD 路径 ⇒ 框架先吃掉 1 字节 flag）：
        ///     flag != 0 -> 成功：0x23009b0(7,eax,ebx) + 0x2345f50 刷新浏览窗口
        ///     flag == 0 -> 失败：按 errcode 分派
        ///       0xd2 / 0x69 -> 弹框；0x7e/0xce/0x7b/0x89 -> 静默刷新；其它 -> 落到成功分支
        ///   ⇒ 成功回 1 字节 [0x01]；失败回 2 字节 [0x00, errcode]（errcode 白名单见下）。
        /// </summary>
        public async Task HandleBuyItemApiece(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var cid = session?.Player?.CharacterId ?? 0;
            var hex = BitConverter.ToString(body ?? Array.Empty<byte>());
            FileLogger.Log(
                $"[AuctionCap] cid={cid} AUCTION_BUY_ITEM_APIECE(0x014E) 一口价购买 " +
                $"len={body?.Length ?? 0} hex={hex}");

            if (session == null || cid <= 0)
                return;

            // 探测：打印多候选偏移的 int32 读值，实测后据此锁定 listingId 精确偏移。
            if (body != null && body.Length >= 4)
            {
                var probes = new int[16];
                for (var off = 0; off < 16 && off + 4 <= body.Length; off++)
                    probes[off] = BitConverter.ToInt32(body, off);
                FileLogger.Log(
                    $"[AuctionBuyProbe] cid={cid} " +
                    $"off0..15={string.Join(",", probes)}");
            }

            // 解析挂牌 id（默认 offset 9 = 总价4 + 数量4 之后，8 字节低 4 位）。
            long listingId = 0;
            if (body != null && body.Length >= 13)
                listingId = (long)BitConverter.ToInt32(body, 9);

            if (listingId <= 0)
            {
                FileLogger.Log(
                    $"[AuctionBuy] cid={cid} 请求体无有效挂牌ID（len={body?.Length ?? 0}），拒绝购买");
                await SendBuyReply(session, cid, new byte[] { 0x00, 0xD2 }, "no-listing-id");
                return;
            }

            AuctionBuyoutResult result;
            try
            {
                result = _auction.Repository.Buyout(listingId, cid);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionBuy] cid={cid} Buyout 抛异常: {ex}");
                result = AuctionBuyoutResult.Fail(AuctionError.ServerBusy);
            }

            if (!result.Success)
            {
                FileLogger.Log(
                    $"[AuctionBuy] cid={cid} 一口价购买被拒 error={result.Error} listingId={listingId}");
                await SendBuyReply(session, cid, new byte[] { 0x00, MapBuyErrorToClientCode(result.Error) }, $"fail {result.Error}");
                return;
            }

            FileLogger.Log($"[AuctionBuy] cid={cid} ★一口价购买成功 listingId={listingId}");

            // ★ 成交后立即结算（买家收道具邮件 + 卖家收金币邮件）。
            //   与 HandleBidding 同理：之前漏调 SettleSold，邮件全靠分钟级 tick 补发，
            //   导致延迟。这里同步结算让邮件即时到达。
            try
            {
                _auction.SettleSold(listingId);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionBuy] cid={cid} 成交结算失败 listingId={listingId}: {ex.Message}");
            }

            // ★ 成交后【不发任何金币刷新包】，仅同步内存背包金币（同 HandleBidding，round39 定案）：
            //   客户端成交回调自身会本地扣款，服务端补发刷新包会导致「多扣一次/余额清空」。
            if (_refresh != null)
            {
                try
                {
                    _refresh.SyncGoldToMemory(session, result.GoldAfter);
                    FileLogger.Log($"[AuctionBuy] cid={cid} 成交后不发金币刷新包，仅同步内存金币={result.GoldAfter}");
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[AuctionBuy] cid={cid} 金币内存同步失败: {ex.Message}");
                }
            }

            // 成功应答：1 字节 flag=0x01（客户端吃 flag 后走 0x2345f50 刷新浏览窗口）。
            await SendBuyReply(session, cid, new byte[] { 0x01 }, $"ok listingId={listingId}");
        }

        /// <summary>
        /// 服务端 AuctionError -> 客户端 errcode（0x1127130 失败分派只认
        /// 0xd2/0x69/0x7e/0xce/0x7b/0x89，其余值会 fall-through 到成功路径）。
        /// 一口价购买失败默认回 0xd2（通用提示框，最安全）；已售出/不存在走 0x7e 静默收尾。
        /// </summary>
        private static byte MapBuyErrorToClientCode(AuctionError error)
        {
            switch (error)
            {
                case AuctionError.ListingNotFound:
                case AuctionError.ListingNotActive:
                    return 0x7e;
                case AuctionError.OwnListing:
                    return 0x69; // 不能买/竞拍自己的挂牌：弹本地化提示框
                case AuctionError.InsufficientGold:
                case AuctionError.BidTooLow:
                    return 0xd2; // 通用提示框（余额不足 / 出价未超过当前最高价）
                default:
                    return 0xD2;
            }
        }

        private static async Task SendBuyReply(
            EnhancedClientSession session, int cid, byte[] reply, string note)
        {
            try
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01, (ushort)CmdPacketTypeA21.AUCTION_BUY_ITEM_APIECE, reply));
                FileLogger.Log(
                    $"[AuctionBuyReply] cid={cid} cmd=0x01 " +
                    $"body={BitConverter.ToString(reply)} ({note})");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionBuyReply] cid={cid} send failed: {ex.Message}");
            }
        }

        private static async Task SendBidReply(
            EnhancedClientSession session, int cid, byte[] reply, string note)
        {
            try
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01, (ushort)CmdPacketTypeA21.AUCTION_BIDDING, reply));
                FileLogger.Log(
                    $"[AuctionBidReply] cid={cid} cmd=0x01 " +
                    $"body={BitConverter.ToString(reply)} ({note})");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionBidReply] cid={cid} send failed: {ex.Message}");
            }
        }

        private static Task Capture(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body,
            string label)
        {
            var hex = BitConverter.ToString(body ?? Array.Empty<byte>());
            FileLogger.Log(
                $"[AuctionCap] cid={session?.Player?.CharacterId ?? 0} {label} " +
                $"len={body?.Length ?? 0} hex={hex}");
            return Task.CompletedTask;
        }

        // ------------------------------------------------------------------
        // 阶段 2 接入点：包体格式确认后，以下解析桩替换为真实 GamePacketReader 解析，
        // 再调用 _auction.Repository / Settle* 完成业务闭环（业务层已就绪，无需改动）。
        // 参考实现（上架）：
        //
        // var request = AuctionRegistItemRequest.Parse(body);  // 待逆向
        // var result = _auction.Repository.RegisterListing(
        //     session.Player.CharacterId,
        //     request.ListType, request.SlotIndex,
        //     request.BuyoutPrice, AuctionPolicy.DefaultListingDuration, request.Count);
        // if (result.Success)
        // {
        //     if (_refresh != null)
        //         await _refresh.SendUpdateItemList(session, request.ListType, (short)request.SlotIndex);
        //     ... // 回 AUCTION_REGIST_ITEM 应答包（格式待逆向）
        // }
        // ------------------------------------------------------------------
    }
}
