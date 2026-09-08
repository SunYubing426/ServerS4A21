using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Auction
{
    /// <summary>
    /// 拍卖行数据层。上架/搜索/一口价/下架/到期全部走 SQLite 事务；
    /// 道具托管复用邮件系统的 ItemCore 快照结构，结算退回直接构造系统邮件附件。
    /// 约定（ClockService.md）：上架单必须跨进程重启存活，存绝对到期时间。
    /// </summary>
    public sealed class AuctionRepository
    {
        private readonly string _connectionString;

        /// <summary>
        /// 86JP 金币道具模板 id（0x28EB7D）。客户端「金币寄售」发 0x00B7 上架时，
        /// 请求体 [4..7] 的 itemId 就是这个值，与普通道具上架同一命令、同一布局。
        /// 唯一区别：金币存在虚拟槽 0（InventoryService.MainVirtualCurrencySlotStart），
        /// 不在 inventory.GetItems() 里，也没有 ItemCore，故走 RegisterGoldListing 旁路。
        /// </summary>
        public const int GoldTemplateId = 2681725;

        /// <summary>
        /// 金币券家族（86JP cash/chn_Trade_Gold/gold_Nm.stk）：itemId → 面额（金币）。
        /// 语义：金币寄售上架一个「面额为 face、数量为 count」的金币券挂牌，
        /// 托管金币 = face × count；一口价 BuyoutPrice 是【点券】(céra) 单价。
        /// 面额表来自 stackable.lst 逆向（round56 定案）：
        ///   gold_1m=100 万 … gold_10m=1000 万，gold_20m/30m/40m/50m/100m/150m/200m。
        ///   注：2681762=trade_point、2681917=trade_point_event 是代币券，【不属于】金币家族。
        /// </summary>
        private static readonly Dictionary<int, int> GoldDenominations = new()
        {
            { 2681725, 1_000_000 },   // gold_1m
            { 2681726, 2_000_000 },   // gold_2m
            { 2681727, 3_000_000 },   // gold_3m
            { 2681728, 4_000_000 },   // gold_4m
            { 2681729, 5_000_000 },   // gold_5m
            { 2681730, 6_000_000 },   // gold_6m
            { 2681731, 7_000_000 },   // gold_7m
            { 2681732, 8_000_000 },   // gold_8m
            { 2681733, 9_000_000 },   // gold_9m
            { 2681734, 10_000_000 },  // gold_10m
            { 2681735, 20_000_000 },  // gold_20m
            { 2681736, 30_000_000 },  // gold_30m
            { 2683069, 40_000_000 },  // gold_40m
            { 2683070, 50_000_000 },  // gold_50m
            { 2683071, 100_000_000 }, // gold_100m
            { 2683072, 150_000_000 }, // gold_150m
            { 2683073, 200_000_000 }, // gold_200m
        };

        /// <summary>itemId 是否属于金币券家族（金币寄售）。</summary>
        public static bool IsGoldItem(int itemId) => GoldDenominations.ContainsKey(itemId);

        /// <summary>取出金币券面额（每张券兑多少金币）。</summary>
        public static bool TryGetGoldValue(int itemId, out int faceValue)
            => GoldDenominations.TryGetValue(itemId, out faceValue);

        public AuctionRepository(IGameDatabase database)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
        }

        // ------------------------------------------------------------------
        // 上架：从在线卖家的背包租约中托管道具并创建上架单。
        // ------------------------------------------------------------------

        public AuctionRegisterResult RegisterListing(
            int sellerCharacterId,
            InventoryListType listType,
            short slotIndex,
            int buyoutPrice,
            int startingPrice,
            TimeSpan duration,
            int listingCount = 1)
        {
            // 起拍价/一口价至少一个 > 0；纯竞拍时一口价=0（客户端传 -1 已在 handler 归零）。
            var effectiveBuyout = Math.Max(0, buyoutPrice);
            var effectiveStarting = Math.Max(0, startingPrice);
            if (sellerCharacterId <= 0 || (effectiveBuyout <= 0 && effectiveStarting <= 0))
                return AuctionRegisterResult.Fail(AuctionError.InvalidRequest);
            // 起拍价不得超过一口价（两者都填时）。
            if (effectiveBuyout > 0 && effectiveStarting > 0 && effectiveStarting >= effectiveBuyout)
                return AuctionRegisterResult.Fail(AuctionError.PriceOutOfRange);
            if (!InventoryContext.TryGetLease(sellerCharacterId, out var lease)
                || lease.CharacterId != sellerCharacterId)
                return AuctionRegisterResult.Fail(AuctionError.ServerBusy);

            lock (lease.SyncRoot)
            {
                var inventory = lease.Inventory;

                // ★ round111（2026-09-06）：材料栏虚拟槽（魔方碎片 354~359 / 灵魂仓 360~364）上架支持。
                //   无色/金色小晶块、四色魔方碎片、灵魂仓道具不存 Main 物品数组，而在
                //   _mainVirtualCounts（账号级魔方/灵魂仓库计数）；TryGetItem / GetItems(Main)
                //   都看不见它们 → 原先一律「源槽位定位失败」回 0xd2，而客户端给 0xd2
                //   配的弹窗文本是「安全模式」（msg 0x11205），玩家误以为账号被封。
                //   这里把虚拟槽计数合成为 ItemCore 后走正常拆堆上架链路：
                //   扣减用 SetMainVirtualCount（脏槽由 SaveDirtyInTransaction 持久化到
                //   CurrencyService 魔方/灵魂仓库表）；退回/售出仍走系统邮件，领取时由
                //   InventoryRewardGrantService.TryResolveMainVirtualReward 自动路由回虚拟槽。
                var isVirtualCubeOrSoulSlot = listType == InventoryListType.Main
                    && slotIndex >= InventoryService.MainVirtualCubeSlotStart
                    && slotIndex <= InventoryService.MainVirtualSoulSlotEnd;

                ItemCore core;
                if (isVirtualCubeOrSoulSlot)
                {
                    var virtualSource = inventory.GetMainVirtualCount(slotIndex);
                    if (virtualSource == null || virtualSource.Count <= 0)
                        return AuctionRegisterResult.Fail(AuctionError.InventorySlotEmpty);
                    if (!InventoryCreateService.TryCreateCore(
                            virtualSource.ItemId, ItemCreateReason.Unknown, virtualSource.Count, out core)
                        || core == null)
                        return AuctionRegisterResult.Fail(AuctionError.InvalidRequest);
                }
                else
                {
                    if (!inventory.TryGetItem(listType, slotIndex, out core) || core == null)
                        return AuctionRegisterResult.Fail(AuctionError.InventorySlotEmpty);

                    // 货币虚拟槽（金币/复活币/点券）与保留槽不可上架。
                    if (listType == InventoryListType.Main
                        && (InventoryService.IsVirtualMainSlot(slotIndex)
                            || InventoryService.IsReservedMainSlot(slotIndex)))
                        return AuctionRegisterResult.Fail(AuctionError.ItemNotTradable);
                }

                // ★ round100 回归拆堆（撤销 round18 方案一的强制整组）：
                //   按客户端 0x00B7 请求体 [8..11] 的 listingCount 部分上架，
                //   listedCount = min(listingCount, stackTotal)，背包保留剩余 keepCount。
                //   原先强制整组是为绕过客户端清锁函数 0x232f610 的整堆假设；
                //   现客户端清锁补丁由用户侧制作，服务端恢复拆堆（规格同 round11）。
                var stackable = IsStackableKind(core);
                var stackTotal = stackable ? Math.Max(1, core.Count) : 1;
                var listedCount = ResolveListingCount(core, listingCount); // min(请求数, 整堆)
                var keepCount = stackTotal - listedCount; // >0 时背包保留剩余数量

                var snapshot = BuildSnapshot(listType, slotIndex, core, listedCount);
                var policyError = MailboxSendPolicy.ValidateAttachment(
                    new MailboxSendRequest { SenderCharacterId = sellerCharacterId },
                    core);
                if (policyError != MailboxSendError.None)
                    return AuctionRegisterResult.Fail(MapPolicyError(policyError));

                var activeCount = CountActiveListings(sellerCharacterId);
                if (activeCount >= AuctionPolicy.MaxActiveListingsPerCharacter)
                    return AuctionRegisterResult.Fail(AuctionError.ListingLimitReached);
                // 价格上限校验 + 手续费按「起拍价/一口价较大者」计（纯竞拍无一口价时用起拍价）。
                var feeBase = Math.Max(effectiveBuyout, effectiveStarting);
                if (feeBase > AuctionPolicy.MaxPrice)
                    return AuctionRegisterResult.Fail(AuctionError.PriceOutOfRange);

                var feeGold = AuctionPolicy.CalculateListingFee(feeBase);
                var currentGold =
                    inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
                if (currentGold < feeGold)
                    return AuctionRegisterResult.Fail(AuctionError.InsufficientGold);

                var inventoryMutated = false;
                AuctionRegisterResult FailWithRollback(AuctionError error)
                {
                    if (inventoryMutated)
                        ReloadInventoryAfterRollback(lease);
                    return AuctionRegisterResult.Fail(error);
                }

                try
                {
                    using (var connection = new SqliteConnection(_connectionString))
                    {
                        connection.Open();
                        using (var transaction = connection.BeginTransaction(deferred: false))
                        {
                            // 卖家在线：内存库存是权威，先扣手续费再移除道具，
                            // 与 MailboxRepository.SendMail 同一事务内持久化。
                            if (!inventory.SetMainVirtualCount(
                                    InventoryService.MainVirtualCurrencySlotStart,
                                    currentGold - feeGold))
                                return FailWithRollback(AuctionError.InsufficientGold);
                            inventoryMutated = true;

                            // 拆堆上架：背包保留剩余 keepCount 则写回该槽（MarkDirty），
                            // 整堆托管（keepCount<=0，含不可堆叠装备）才整槽移除。
                            if (isVirtualCubeOrSoulSlot)
                            {
                                // 虚拟槽（魔方碎片/灵魂仓）：整堆/拆堆都只需改写计数，
                                // 剩余 0 也保留条目（客户端材料栏对应格子显示为空），
                                // 脏槽随本次事务持久化到账号级魔方/灵魂仓库表。
                                if (!inventory.SetMainVirtualCount(slotIndex, keepCount))
                                {
                                    return FailWithRollback(AuctionError.InventorySlotEmpty);
                                }
                            }
                            else if (keepCount > 0)
                            {
                                core.Count = keepCount;
                                if (!inventory.SetItem(listType, slotIndex, core))
                                {
                                    return FailWithRollback(AuctionError.InventorySlotEmpty);
                                }
                            }
                            else if (!inventory.RemoveItem(listType, slotIndex))
                            {
                                return FailWithRollback(AuctionError.InventorySlotEmpty);
                            }

                            if (!InventoryPersistenceService.SaveDirtyInTransaction(
                                    connection, transaction, lease))
                                return FailWithRollback(AuctionError.ServerBusy);

                            var listingId = InsertListing(
                                connection,
                                transaction,
                                sellerCharacterId,
                                LoadCharacterName(connection, transaction, sellerCharacterId),
                                snapshot,
                                effectiveBuyout,
                                effectiveStarting,
                                duration);

                            transaction.Commit();
                            lease.Inventory.ClearDirtyState();

                            FileLogger.Log(
                                $"[AuctionRegist] cid={sellerCharacterId} 拆堆上架 listingId={listingId} " +
                                $"item={core.ItemId} kind={core.ItemKind} stackable={stackable} " +
                                $"requested={listingCount} stackTotal={stackTotal} " +
                                $"listed={listedCount} keepInBag={keepCount}");

                            return new AuctionRegisterResult
                            {
                                Success = true,
                                Error = AuctionError.None,
                                ListingId = listingId,
                                FeeGold = feeGold,
                                GoldAfter = currentGold - feeGold,
                            };
                        }
                    }
                }
                catch
                {
                    if (inventoryMutated)
                        ReloadInventoryAfterRollback(lease);
                    throw;
                }
            }
        }

        /// <summary>
        /// 金币寄售（0x00B7 且 itemId 属于金币券家族）的专用上架旁路。
        ///
        /// 与 RegisterListing 的区别：
        ///   · 金币是虚拟槽 0 的货币，不在 inventory.GetItems() 里，也无 ItemCore；
        ///   · 这里按「面额 × 数量」计算托管金币，绕过 TryResolveSourceSlot /
        ///     IsVirtualMainSlot 两道对虚拟槽的防线；
        ///   · 金币扣除 = 托管金币 + 手续费（round94 起手续费 = 固定 1 万金币/笔，
        ///     见 AuctionPolicy.GoldListingFlatFee；旧规则为面额 × 数量的 1%，已废）。
        ///
        /// ★ 语义（round56 定案，对齐官方「点券换金币」）：
        ///   itemId 决定面额（2681725=100 万 … 2683073=2 亿），count 是张数；
        ///   托管金币 goldAmount = 面额 × count；BuyoutPrice 是【点券】(céra) 单价，
        ///   一口价总额 = BuyoutPrice × count（买家付点券、卖家收点券扣税）。
        ///   入库快照用真实 itemId + Value=count（张数），而不是把金币数额塞进 ItemCount
        ///   ——否则购买/搜索会把 ItemCount 当数量×价格，产生「超限多金币」。
        /// </summary>
        public AuctionRegisterResult RegisterGoldListing(
            int sellerCharacterId,
            int itemId,
            int count,
            int buyoutPrice,
            TimeSpan duration)
        {
            var effectiveBuyout = Math.Max(0, buyoutPrice);
            // 金币寄售只做一口价（点券购买），不支持竞拍起拍价。
            if (sellerCharacterId <= 0 || count <= 0 || effectiveBuyout <= 0)
                return AuctionRegisterResult.Fail(AuctionError.InvalidRequest);
            if (!TryGetGoldValue(itemId, out var faceValue) || faceValue <= 0)
                return AuctionRegisterResult.Fail(AuctionError.InvalidRequest);

            // 托管金币 = 面额 × 张数（long 防溢出，再夹到 int 上限）。
            var goldAmountLong = (long)faceValue * count;
            if (goldAmountLong > int.MaxValue)
                return AuctionRegisterResult.Fail(AuctionError.GoldLimitExceeded);
            var goldAmount = (int)goldAmountLong;

            if (!InventoryContext.TryGetLease(sellerCharacterId, out var lease)
                || lease.CharacterId != sellerCharacterId)
                return AuctionRegisterResult.Fail(AuctionError.ServerBusy);

            lock (lease.SyncRoot)
            {
                var inventory = lease.Inventory;
                var currentGold = inventory.GetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;

                // 金币整组托管，上架数量不得超过现有金币。
                if (goldAmount > currentGold)
                    return AuctionRegisterResult.Fail(AuctionError.InsufficientGold);

                var activeCount = CountActiveListings(sellerCharacterId);
                if (activeCount >= AuctionPolicy.MaxActiveListingsPerCharacter)
                    return AuctionRegisterResult.Fail(AuctionError.ListingLimitReached);

                // ★ round94：金币寄售手续费改为【固定基础手续费 10,000 金币/笔】，
                //   去掉按托管金币 1% 的比例部分（用户拍板「去掉惩罚性手续费，只保留基础手续费」）。
                //   旧规则 fee=面额×张数×1%：1m→1万、50m→50万、100m→100万、200m→200万，
                //   大面额手续费随金币面额等比放大（而售价是点券，与金币面额无关），体感惩罚性。
                //   新规则与面额/张数无关，每笔固定 1 万金币；1m 面额费用与旧规则一致（1万），
                //   大面额不再放大。成交时卖家代币券 5% 税（SaleTaxRate）不在本次范围内，保持不变。
                var feeGold = AuctionPolicy.GoldListingFlatFee;
                if (currentGold < goldAmount + feeGold)
                    return AuctionRegisterResult.Fail(AuctionError.InsufficientGold);

                // 构造金币券 ItemCore：材料类 + 真实 itemId + 张数（Value/Count 同源 = count）。
                // 这样搜索/「我的上架」面板按「金币券 × 张数」展示，不再显示天量金币。
                var goldCore = new ItemCore
                {
                    ItemKind = ItemCore.KindMaterial,
                    ItemId = itemId,
                    Value = count,
                };
                var snapshot = BuildSnapshot(
                    InventoryListType.Main,
                    InventoryService.MainVirtualCurrencySlotStart,
                    goldCore,
                    count);

                var inventoryMutated = false;
                AuctionRegisterResult FailWithRollback(AuctionError error)
                {
                    if (inventoryMutated)
                        ReloadInventoryAfterRollback(lease);
                    return AuctionRegisterResult.Fail(error);
                }

                try
                {
                    using (var connection = new SqliteConnection(_connectionString))
                    {
                        connection.Open();
                        using (var transaction = connection.BeginTransaction(deferred: false))
                        {
                            // 虚拟槽 0 扣「托管金币 + 手续费」，与普通上架同事务持久化。
                            if (!inventory.SetMainVirtualCount(
                                    InventoryService.MainVirtualCurrencySlotStart,
                                    currentGold - goldAmount - feeGold))
                                return FailWithRollback(AuctionError.InsufficientGold);
                            inventoryMutated = true;

                            if (!InventoryPersistenceService.SaveDirtyInTransaction(
                                    connection, transaction, lease))
                                return FailWithRollback(AuctionError.ServerBusy);

                            var listingId = InsertListing(
                                connection,
                                transaction,
                                sellerCharacterId,
                                LoadCharacterName(connection, transaction, sellerCharacterId),
                                snapshot,
                                effectiveBuyout,
                                0, // 金币寄售无起拍价
                                duration);

                            transaction.Commit();
                            lease.Inventory.ClearDirtyState();

                            FileLogger.Log(
                                $"[AuctionGold] cid={sellerCharacterId} 金币上架 listingId={listingId} " +
                                $"itemId={itemId} face={faceValue} count={count} " +
                                $"goldAmount={goldAmount} buyoutCera={effectiveBuyout} " +
                                $"fee={feeGold} goldAfter={currentGold - goldAmount - feeGold}");

                            return new AuctionRegisterResult
                            {
                                Success = true,
                                Error = AuctionError.None,
                                ListingId = listingId,
                                FeeGold = feeGold,
                                GoldAfter = currentGold - goldAmount - feeGold,
                            };
                        }
                    }
                }
                catch
                {
                    if (inventoryMutated)
                        ReloadInventoryAfterRollback(lease);
                    throw;
                }
            }
        }

        // ------------------------------------------------------------------
        // 搜索 / 查询
        // ------------------------------------------------------------------

        public IReadOnlyList<AuctionListing> SearchActiveListings(
            int itemId,
            int limit,
            int offset,
            int excludeSellerCharacterId = 0,
            IReadOnlyList<byte> filterItemKinds = null,
            string filterGroup = null,
            bool filterGroupPrefix = false,
            string filterUsableJob = null,
            string filterEquipmentType = null,
            string filterExpertType = null)
            => SearchActiveListings(itemId, limit, offset, excludeSellerCharacterId, filterItemKinds,
                filterGroup, filterGroupPrefix, filterUsableJob, filterEquipmentType, filterExpertType, out _);

        /// <summary>
        /// 带匹配总数的搜索（round112 搜索分页）：total = 与过滤条件匹配的全部在售件数
        /// （不受 limit/offset 影响）。客户端搜索窗口按 ceil(total/10) 计算总页数
        /// （伪C FUN_02322b50：[win+0x4e4]=总件数、[win+0x4dc]=总页数）。
        /// </summary>
        public IReadOnlyList<AuctionListing> SearchActiveListings(
            int itemId,
            int limit,
            int offset,
            int excludeSellerCharacterId,
            IReadOnlyList<byte> filterItemKinds,
            string filterGroup,
            bool filterGroupPrefix,
            string filterUsableJob,
            string filterEquipmentType,
            string filterExpertType,
            out int total)
        {
            const int maxLimit = 64;
            if (limit <= 0 || limit > maxLimit)
                limit = 24;
            if (offset < 0)
                offset = 0;

            total = 0;
            var results = new List<AuctionListing>();
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    // 官方 DNF 规则：搜索结果不显示自己上架的物品（同一角色无法与自己交易）。
                    // excludeSellerCharacterId > 0 时附加 AND seller_character_id != @excludeCid 过滤。
                    var excludeClause = excludeSellerCharacterId > 0
                        ? " AND seller_character_id != @excludeCid"
                        : string.Empty;

                    // 分类过滤：filterItemKinds 非空时按 item_kind（数字字符串）过滤，null/空不过滤。
                    // ★ round110：一个分类可命中多个 kind（材料 0x32CA = 3/10/11），故用 IN 而非 = 。
                    var kindNames = new List<string>();
                    if (filterItemKinds != null)
                    {
                        for (var ki = 0; ki < filterItemKinds.Count; ki++)
                        {
                            var pName = "@filterKind" + ki;
                            kindNames.Add(pName);
                            command.Parameters.AddWithValue(pName, filterItemKinds[ki].ToString());
                        }
                    }
                    var kindClause = kindNames.Count == 1
                        ? " AND item_kind = " + kindNames[0]
                        : kindNames.Count > 1
                            ? " AND item_kind IN (" + string.Join(",", kindNames) + ")"
                            : string.Empty;

                    // ★ round89：普通拍卖行搜索必须排除金币券家族（gold_1m~gold_200m）。
                    //   金币券 kind=3(KindMaterial)，会泄漏进「材料 0x32CA」等普通分类；
                    //   客户端在普通拍卖行列表里把它们当普通道具，购买走 0x014E 普通一口价
                    //   路径（门 row[0x400] 物品对象指针从未被赋值 → 静默 return 不发包），
                    //   表现为 50m/100m 等面额「购买无响应」。金币寄售走专门的
                    //   SearchActiveGoldListings（0x9CA5 / 0x00BA itemId），购买走 0x00B9
                    //   金币路径（门 [0x4d4] 正常），故此处排除不影响金币寄售功能。
                    //   排除区间对齐客户端范围判断 0x145a5a1：2681725-2681736 / 2683069-2683073。
                    const string goldExcludeClause =
                        " AND item_template_id NOT BETWEEN 2681725 AND 2681736" +
                        " AND item_template_id NOT BETWEEN 2683069 AND 2683073";

                    // 装备细分过滤（filterGroup != null 或 filterUsableJob != null 或 filterEquipmentType != null
                    // 或 filterExpertType != null）依赖 PVF 字段，这些字段不在 auction_listings 表中，
                    // 需查出候选后按 item_template_id 反查内存过滤。因此这些过滤维度都不走 SQL 分页
                    // （查全部 active，内存过滤后再截断），避免「先 LIMIT 64 再过滤」导致结果不足一页/缺失。
                    var hasMemoryFilter = filterGroup != null || filterUsableJob != null
                        || filterEquipmentType != null || filterExpertType != null;
                    var pageClause = hasMemoryFilter
                        ? string.Empty
                        : " LIMIT @limit OFFSET @offset";

                    command.CommandText = itemId > 0
                        ? $@"SELECT listing_id, seller_character_id, seller_name, item_type,
                                  source_list_type, source_slot_index, item_template_id, item_kind,
                                  item_count, instance_value, durability, seal_flag, option_value,
                                  expire_time, marker16, pet_serial_or_handle, extra_json,
                                  item_core_data, detail_json, buyout_price, status,
                                  buyer_character_id, buyer_name, listed_at_unix, expires_at_unix,
                                  sold_at_unix, settle_mail_count,
                                  current_bid, current_bidder_id, current_bidder_name, bid_count, starting_price
                           FROM auction_listings
                           WHERE status = 0 AND item_template_id = @itemId{excludeClause}{kindClause}{goldExcludeClause}
                           ORDER BY buyout_price ASC, listed_at_unix ASC{pageClause}"
                        : $@"SELECT listing_id, seller_character_id, seller_name, item_type,
                                  source_list_type, source_slot_index, item_template_id, item_kind,
                                  item_count, instance_value, durability, seal_flag, option_value,
                                  expire_time, marker16, pet_serial_or_handle, extra_json,
                                  item_core_data, detail_json, buyout_price, status,
                                  buyer_character_id, buyer_name, listed_at_unix, expires_at_unix,
                                  sold_at_unix, settle_mail_count,
                                  current_bid, current_bidder_id, current_bidder_name, bid_count, starting_price
                           FROM auction_listings
                           WHERE status = 0{excludeClause}{kindClause}{goldExcludeClause}
                           ORDER BY listed_at_unix DESC{pageClause}";
                    command.Parameters.AddWithValue("@itemId", itemId);
                    if (excludeSellerCharacterId > 0)
                        command.Parameters.AddWithValue("@excludeCid", excludeSellerCharacterId);
                    if (!hasMemoryFilter)
                    {
                        command.Parameters.AddWithValue("@limit", limit);
                        command.Parameters.AddWithValue("@offset", offset);

                        // ★ round112：无内存过滤时匹配总数走 SQL COUNT(*)（与分页 SELECT 同 WHERE）。
                        //   客户端搜索窗口用它算总页数（ceil(total/10)），决定「下一页」是否可用。
                        using (var countCommand = connection.CreateCommand())
                        {
                            countCommand.CommandText = itemId > 0
                                ? $@"SELECT COUNT(*) FROM auction_listings
                                     WHERE status = 0 AND item_template_id = @itemId{excludeClause}{kindClause}{goldExcludeClause}"
                                : $@"SELECT COUNT(*) FROM auction_listings
                                     WHERE status = 0{excludeClause}{kindClause}{goldExcludeClause}";
                            countCommand.Parameters.AddWithValue("@itemId", itemId);
                            if (excludeSellerCharacterId > 0)
                                countCommand.Parameters.AddWithValue("@excludeCid", excludeSellerCharacterId);
                            if (filterItemKinds != null)
                            {
                                for (var ki = 0; ki < filterItemKinds.Count; ki++)
                                    countCommand.Parameters.AddWithValue("@filterKind" + ki, filterItemKinds[ki].ToString());
                            }
                            total = Convert.ToInt32(countCommand.ExecuteScalar());
                        }
                    }
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadListing(reader));
                    }
                }
            }

            // 装备细分过滤：按 item_template_id 反查 PVF 装备 [item group name] 做内存过滤，再分页截断。
            //   - 精确匹配（filterGroupPrefix=false）：武器叶子（如 "katana"）、防具「材质+部位」组合（如 "cl coat"）。
            //   - 前缀匹配（filterGroupPrefix=true）：防具材质节点（如 "cl"）→ 匹配该材质所有部位（cl coat/cl pants/…）。
            if (filterGroup != null)
            {
                // ★ round112：内存过滤路径的匹配总数 = Where 之后、Skip/Take 之前的件数。
                IEnumerable<AuctionListing> filtered = filterGroupPrefix
                    ? results.Where(l =>
                    {
                        var g = ItemMetadataResolver.ResolveEquipmentGroupName(l.Item?.ItemTemplateId ?? 0);
                        return g != null && g.StartsWith(filterGroup, StringComparison.OrdinalIgnoreCase);
                    })
                    : results.Where(l => string.Equals(
                        ItemMetadataResolver.ResolveEquipmentGroupName(l.Item?.ItemTemplateId ?? 0),
                        filterGroup, StringComparison.OrdinalIgnoreCase));
                total = filtered.Count();
                results = filtered.Skip(offset).Take(limit).ToList();
            }

            // 特殊装备按职业过滤：按 item_template_id 反查 PVF 装备 [usable job]（如 "[swordman]"）。
            // ★ [usable job] 可能是【多值】：如格斗家男的辅助装备写成 `[fighter]` `[at fighter]`
            //   （男/女都能用）。StripBacktick 后残留 `[fighter]` `[at fighter]`，精确 Equals 会漏掉。
            //   改用「包含匹配」：提取 job 串里所有 [xxx] 标签，只要含目标职业 或 [all] 即命中。
            //   filterUsableJob = "[all]" 时匹配所有。
            if (filterUsableJob != null)
            {
                var filtered = results
                    .Where(l =>
                    {
                        var j = ItemMetadataResolver.ResolveEquipmentUsableJob(l.Item?.ItemTemplateId ?? 0);
                        if (string.IsNullOrEmpty(j))
                            return false;
                        if (string.Equals(filterUsableJob, "[all]", StringComparison.OrdinalIgnoreCase))
                            return true;
                        return JobTagMatches(j, filterUsableJob)
                            || JobTagMatches(j, "[all]");
                    });
                total = filtered.Count();
                results = filtered.Skip(offset).Take(limit).ToList();
            }

            // 宠物装备按 [equipment type] 颜色过滤（artifact red/blue/green）：
            // 红/蓝/绿宠物装备的 item_kind 都是 KindCreatureEquipment，无法用 kind 区分，
            // 只能按 item_template_id 反查 PVF [equipment type] 字段精确匹配。
            if (filterEquipmentType != null)
            {
                // 宠物蛋哨兵：走 IsCreatureEgg（[creature] + [output index]），排除宠物本体。
                // 其余值：按 [equipment type] 精确匹配（artifact red/blue/green 等宠物装备颜色）。
                var isEggFilter = string.Equals(
                    filterEquipmentType,
                    AuctionHandler.CreatureEggFilterSentinel,
                    StringComparison.Ordinal);
                var filtered = results
                    .Where(l => isEggFilter
                        ? ItemMetadataResolver.IsCreatureEgg(l.Item?.ItemTemplateId ?? 0)
                        : string.Equals(
                            ItemMetadataResolver.ResolveEquipmentType(l.Item?.ItemTemplateId ?? 0),
                            filterEquipmentType, StringComparison.OrdinalIgnoreCase));
                total = filtered.Count();
                results = filtered.Skip(offset).Take(limit).ToList();
            }

            // 副职业按 [expert type] 过滤（[alchemist]/[doll_controller]/[enchanter]）：
            // 副职业 3 职业的产物 [stackable type] 都是 [waste] 系，item_kind 同为
            // KindConsumable，无法用 kind 或 stackable type 区分，只能按 item_template_id
            // 反查 PVF [expert type] 字段精确匹配。
            if (filterExpertType != null)
            {
                var filtered = results
                    .Where(l => string.Equals(
                        ItemMetadataResolver.ResolveStackableExpertType(l.Item?.ItemTemplateId ?? 0),
                        filterExpertType, StringComparison.OrdinalIgnoreCase));
                total = filtered.Count();
                results = filtered.Skip(offset).Take(limit).ToList();
            }

            return results;
        }

        /// <summary>
        /// 金币寄售搜索（分类 ID 0x9CA5 = "其他"，金币寄售面板根，含 17 个面额子节点）。
        ///   itemId > 0 且属于金币券家族：精确搜索该面额的 Active 挂牌。
        ///   itemId == 0：浏览全部 17 个面额（gold_1m~gold_200m）的 Active 挂牌（按面额/单价/上架时间排序）。
        /// 金币券无职业/装备/副职业维度，识别靠 itemId（IsGoldItem）+ 分类 ID 0x9CA5，
        /// 故不走分类维度过滤（参考 HandleSearch 中 isGoldBrowse 旁路）。
        /// </summary>
        public IReadOnlyList<AuctionListing> SearchActiveGoldListings(
            int itemId,
            int limit,
            int offset,
            int excludeSellerCharacterId = 0)
            => SearchActiveGoldListings(itemId, limit, offset, excludeSellerCharacterId, out _);

        /// <summary>
        /// 带匹配总数的金币寄售搜索（round112 搜索分页）：total 语义同 SearchActiveListings，
        /// 金币窗口（type8）按 ceil(total/10) 计算总页数（伪C FUN_0144a6e0）。
        /// </summary>
        public IReadOnlyList<AuctionListing> SearchActiveGoldListings(
            int itemId,
            int limit,
            int offset,
            int excludeSellerCharacterId,
            out int total)
        {
            const int maxLimit = 64;
            if (limit <= 0 || limit > maxLimit)
                limit = 24;
            if (offset < 0)
                offset = 0;

            total = 0;

            var results = new List<AuctionListing>();
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    // 官方 DNF 规则：搜索结果不显示自己上架的物品。
                    var excludeClause = excludeSellerCharacterId > 0
                        ? " AND seller_character_id != @excludeCid"
                        : string.Empty;

                    string itemFilter;
                    if (itemId > 0)
                    {
                        // 精确搜索该面额（客户端点"1百万"等子节点时发 0x00BA itemId=面额）。
                        itemFilter = " AND item_template_id = @itemId";
                        command.Parameters.AddWithValue("@itemId", itemId);
                    }
                    else
                    {
                        // 浏览全部 17 个金币面额（客户端点"其他"时发 0x00BB itemId=0）。
                        var paramNames = new List<string>(GoldDenominations.Count);
                        int idx = 0;
                        foreach (var goldId in GoldDenominations.Keys)
                        {
                            var pName = $"@gold{idx}";
                            paramNames.Add(pName);
                            command.Parameters.AddWithValue(pName, goldId);
                            idx++;
                        }
                        itemFilter = $" AND item_template_id IN ({string.Join(",", paramNames)})";
                    }

                    // 金币寄售挂单禁止竞价，starting_price / current_bid 必为 0；按面额升序、单价升序、上架时间升序排列。
                    command.CommandText = $@"SELECT listing_id, seller_character_id, seller_name, item_type,
                                              source_list_type, source_slot_index, item_template_id, item_kind,
                                              item_count, instance_value, durability, seal_flag, option_value,
                                              expire_time, marker16, pet_serial_or_handle, extra_json,
                                              item_core_data, detail_json, buyout_price, status,
                                              buyer_character_id, buyer_name, listed_at_unix, expires_at_unix,
                                              sold_at_unix, settle_mail_count,
                                              current_bid, current_bidder_id, current_bidder_name, bid_count, starting_price
                                       FROM auction_listings
                                       WHERE status = 0{itemFilter}{excludeClause}
                                       ORDER BY item_template_id ASC, buyout_price ASC, listed_at_unix ASC
                                       LIMIT @limit OFFSET @offset";
                    if (excludeSellerCharacterId > 0)
                        command.Parameters.AddWithValue("@excludeCid", excludeSellerCharacterId);
                    command.Parameters.AddWithValue("@limit", limit);
                    command.Parameters.AddWithValue("@offset", offset);

                    // ★ round112：匹配总数（客户端金币寄售窗口分页用，同 WHERE 的 COUNT(*)）。
                    using (var countCommand = connection.CreateCommand())
                    {
                        countCommand.CommandText = $@"SELECT COUNT(*) FROM auction_listings
                                                      WHERE status = 0{itemFilter}{excludeClause}";
                        if (itemId > 0)
                        {
                            countCommand.Parameters.AddWithValue("@itemId", itemId);
                        }
                        else
                        {
                            var goldIdx = 0;
                            foreach (var goldId in GoldDenominations.Keys)
                            {
                                countCommand.Parameters.AddWithValue($"@gold{goldIdx}", goldId);
                                goldIdx++;
                            }
                        }
                        if (excludeSellerCharacterId > 0)
                            countCommand.Parameters.AddWithValue("@excludeCid", excludeSellerCharacterId);
                        total = Convert.ToInt32(countCommand.ExecuteScalar());
                    }

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadListing(reader));
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// 判断装备的 [usable job] 字符串是否包含目标职业标签（如 "[fighter]"）。
        /// [usable job] 可能是多值（如 "`[fighter]` `[at fighter]`"），StripBacktick 后
        /// 残留 "`[fighter]` `[at fighter]`" 之类格式，精确 Equals 会漏掉男/女通用装备。
        /// 这里用正则提取所有 [xxx] 标签逐一比对，天然兼容单值/多值/残留反引号。
        /// </summary>
        private static bool JobTagMatches(string usableJob, string targetJob)
        {
            if (string.IsNullOrEmpty(usableJob) || string.IsNullOrEmpty(targetJob))
                return false;
            var matches = System.Text.RegularExpressions.Regex.Matches(
                usableJob, @"\[[^\]]+\]");
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                if (string.Equals(m.Value, targetJob, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public IReadOnlyList<AuctionListing> LoadListingsBySeller(
            int sellerCharacterId,
            AuctionListingStatus status)
        {
            var results = new List<AuctionListing>();
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT listing_id, seller_character_id, seller_name, item_type,
                                  source_list_type, source_slot_index, item_template_id, item_kind,
                                  item_count, instance_value, durability, seal_flag, option_value,
                                  expire_time, marker16, pet_serial_or_handle, extra_json,
                                  item_core_data, detail_json, buyout_price, status,
                                  buyer_character_id, buyer_name, listed_at_unix, expires_at_unix,
                                  sold_at_unix, settle_mail_count,
                                  current_bid, current_bidder_id, current_bidder_name, bid_count, starting_price
                           FROM auction_listings
                           WHERE seller_character_id = @cid AND status = @status
                           ORDER BY listed_at_unix DESC LIMIT 64";
                    command.Parameters.AddWithValue("@cid", sellerCharacterId);
                    command.Parameters.AddWithValue("@status", (int)status);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadListing(reader));
                    }
                }
            }
            return results;
        }

        /// <summary>
        /// round58：加载某卖家「所有状态」的历史挂牌，供「我的拍卖历史」0x00BE 展示。
        /// 与 LoadListingsBySeller（仅 Active）区别在于不限定 status —— 一并展示 Active / Sold /
        /// Cancelled / Expired / Settled。模型上沿用 AuctionListing（status 字段区分语义）。
        /// </summary>
        public IReadOnlyList<AuctionListing> LoadAllListingsBySeller(int sellerCharacterId)
        {
            var results = new List<AuctionListing>();
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT listing_id, seller_character_id, seller_name, item_type,
                                  source_list_type, source_slot_index, item_template_id, item_kind,
                                  item_count, instance_value, durability, seal_flag, option_value,
                                  expire_time, marker16, pet_serial_or_handle, extra_json,
                                  item_core_data, detail_json, buyout_price, status,
                                  buyer_character_id, buyer_name, listed_at_unix, expires_at_unix,
                                  sold_at_unix, settle_mail_count,
                                  current_bid, current_bidder_id, current_bidder_name, bid_count, starting_price
                           FROM auction_listings
                           WHERE seller_character_id = @cid
                           ORDER BY listed_at_unix DESC LIMIT 128";
                    command.Parameters.AddWithValue("@cid", sellerCharacterId);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadListing(reader));
                    }
                }
            }
            return results;
        }

        public AuctionListing GetListing(long listingId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT listing_id, seller_character_id, seller_name, item_type,
                                  source_list_type, source_slot_index, item_template_id, item_kind,
                                  item_count, instance_value, durability, seal_flag, option_value,
                                  expire_time, marker16, pet_serial_or_handle, extra_json,
                                  item_core_data, detail_json, buyout_price, status,
                                  buyer_character_id, buyer_name, listed_at_unix, expires_at_unix,
                                  sold_at_unix, settle_mail_count,
                                  current_bid, current_bidder_id, current_bidder_name, bid_count, starting_price
                           FROM auction_listings WHERE listing_id = @id";
                    command.Parameters.AddWithValue("@id", listingId);
                    using (var reader = command.ExecuteReader())
                        return reader.Read() ? ReadListing(reader) : null;
                }
            }
        }

        // ------------------------------------------------------------------
        // 一口价购买 / 下架（状态机由 UPDATE ... WHERE status=0 保护，天然防并发双买）
        // ------------------------------------------------------------------

        public AuctionBuyoutResult Buyout(long listingId, int buyerCharacterId)
        {
            if (listingId <= 0 || buyerCharacterId <= 0)
                return AuctionBuyoutResult.Fail(AuctionError.InvalidRequest);

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var listing = GetListing(connection, transaction, listingId);
                    if (listing == null)
                        return AuctionBuyoutResult.Fail(AuctionError.ListingNotFound);
                    if (listing.Status != AuctionListingStatus.Active)
                        return AuctionBuyoutResult.Fail(AuctionError.ListingNotActive);
                    if (listing.SellerCharacterId == buyerCharacterId)
                        return AuctionBuyoutResult.Fail(AuctionError.OwnListing);

                    // ★ 金币券挂牌：买家扣【点券】、得【金币】，卖家收【代币券】扣税，即时结算。
                    if (IsGoldItem(listing.Item.ItemTemplateId))
                        return BuyoutGold(connection, transaction, listing, buyerCharacterId);

                    // ★ 一口价扣款金额 = 单价 × 数量（一口价总额）。
                    //   BuyoutPrice 是「单价」（搜索应答 record[8]），客户端「一口价购买」
                    //   确认框显示的、买家认可的是「总额」row[13] = 单价 × 数量
                    //   （客户端 0x2322f93 把 row[13] 作为金额传进确认框；0x2322e59 用
                    //   row[13]/数量 反推单价）。之前只扣 BuyoutPrice（单价），数量>1 时
                    //   买家少付了「(数量-1)×单价」，正是「少扣金币」的根因。
                    var totalPrice = (long)listing.BuyoutPrice * Math.Max(1, listing.Item.ItemCount);
                    if (totalPrice > int.MaxValue)
                        totalPrice = int.MaxValue;
                    if (!CurrencyService.TrySpendGold(
                            connection, transaction, buyerCharacterId, (int)totalPrice))
                        return AuctionBuyoutResult.Fail(AuctionError.InsufficientGold);

                    var buyerName = LoadCharacterName(connection, transaction, buyerCharacterId);
                    if (!TransitionStatus(
                            connection, transaction, listingId,
                            AuctionListingStatus.Sold,
                            buyerCharacterId, buyerName))
                        return AuctionBuyoutResult.Fail(AuctionError.ListingNotActive);

                    // ★ 扣款后回读买家真实金币余额（同一事务内可见扣款结果），
                    //   供 handler 用带参 SendGoldUpdate 同步内存背包 + 下发客户端。
                    var wallet = CurrencyService.LoadWallet(connection, transaction, buyerCharacterId);

                    transaction.Commit();
                    return new AuctionBuyoutResult
                    {
                        Success = true,
                        Error = AuctionError.None,
                        ListingId = listingId,
                        GoldAfter = wallet.Gold,
                    };
                }
            }
        }

        /// <summary>
        /// 金币券挂牌的一口价成交（在 Buyout 的同一事务内执行）。
        /// 官方「点券换金币」语义：买家扣【点券】、得【金币】，卖家收【代币券】扣成交税。
        /// 事务内只结算【点券扣款 + 转 Sold + 卖家代币券入账】；买家金币改走【系统邮件】
        /// 由 AuctionService.SettleGoldSold 派发（邮件领取后金币才进虚拟槽），
        /// 故此处不 GrantGold、不 MarkSettled，让 settle_mail_count 保持 0 等待邮件结算。
        /// </summary>
        private static AuctionBuyoutResult BuyoutGold(
            SqliteConnection connection,
            SqliteTransaction transaction,
            AuctionListing listing,
            int buyerCharacterId)
        {
            if (!TryGetGoldValue(listing.Item.ItemTemplateId, out var faceValue) || faceValue <= 0)
                return AuctionBuyoutResult.Fail(AuctionError.ItemNotFound);

            var count = Math.Max(1, listing.Item.ItemCount);
            var goldAmountLong = (long)faceValue * count;
            if (goldAmountLong > int.MaxValue)
                return AuctionBuyoutResult.Fail(AuctionError.GoldLimitExceeded);
            var goldAmount = (int)goldAmountLong;

            // 一口价总额 = 点券单价 × 张数（金币券无起拍价，BuyoutPrice 即单价）。
            var totalCeraLong = (long)listing.BuyoutPrice * count;
            if (totalCeraLong > int.MaxValue)
                return AuctionBuyoutResult.Fail(AuctionError.PriceOutOfRange);
            var totalCera = (int)totalCeraLong;

            // 买家扣点券。
            if (!CurrencyService.TrySpendCera(connection, transaction, buyerCharacterId, totalCera))
                return AuctionBuyoutResult.Fail(AuctionError.InsufficientGold);

            var buyerName = LoadCharacterName(connection, transaction, buyerCharacterId);
            if (!TransitionStatus(
                    connection, transaction, listing.ListingId,
                    AuctionListingStatus.Sold, buyerCharacterId, buyerName))
                return AuctionBuyoutResult.Fail(AuctionError.ListingNotActive);

            // ★ 卖家收代币券（扣成交税 5%）。买家金币改走【系统邮件】派发（2026-09-04 定案：
            //   金币寄售成交应发金币邮件、而非直接写虚拟槽），由 AuctionService.SettleGoldSold
            //   在成交后同步/分钟 tick 补发。故此处【不 GrantGold】、【不 MarkSettled】，
            //   让 settle_mail_count 保持 0，等待邮件结算路径完成并标记 Settled。
            var sellerProceedsToken = AuctionPolicy.CalculateSellerProceeds(totalCera);
            if (sellerProceedsToken > 0)
                CurrencyService.GrantTokenCera(connection, transaction, listing.SellerCharacterId, sellerProceedsToken);

            // 回读买家钱包（点券已扣、金币待邮件到账），供 handler 记录/同步。
            var wallet = CurrencyService.LoadWallet(connection, transaction, buyerCharacterId);

            // ★ 成交即提交（BuyoutGold 自持事务提交，返回后 Buyout 的 using 会释放已提交事务）。
            transaction.Commit();

            FileLogger.Log(
                $"[AuctionGoldBuy] buyer={buyerCharacterId} listing={listing.ListingId} " +
                $"seller={listing.SellerCharacterId} face={faceValue} count={count} " +
                $"goldByMail={goldAmount} ceraSpent={totalCera} sellerTokenCera={sellerProceedsToken}");

            return new AuctionBuyoutResult
            {
                Success = true,
                Error = AuctionError.None,
                ListingId = listing.ListingId,
                GoldAfter = wallet.Gold,
                IsGold = true,
            };
        }

        // ------------------------------------------------------------------
        // 竞价：出价金币立即扣除托管（2026-09-04 引入）
        // ------------------------------------------------------------------

        /// <summary>
        /// 竞价出价（出价 &lt; 一口价总额）。金币【立即扣除托管】，被超价/流拍/成交后由
        /// 结算逻辑退回（系统邮件金币）。规则：
        ///   - 出价必须严格高于当前最高价（无当前价时须 &gt; 0）。
        ///   - 出价者不能是卖家自己。
        ///   - 同一事务内：TrySpendGold 扣托管 -> 记录新 bid -> 更新 listing 的
        ///     current_bid/current_bidder/bid_count -> 把上一任最高价者标为被超价。
        ///   - 返回被顶掉的上一任出价者（供 handler 即时退回其托管金币）。
        /// </summary>
        public AuctionBidResult PlaceBid(long listingId, int bidderCharacterId, int bidAmount)
        {
            if (listingId <= 0 || bidderCharacterId <= 0 || bidAmount <= 0)
                return AuctionBidResult.Fail(AuctionError.InvalidRequest);

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var listing = GetListing(connection, transaction, listingId);
                    if (listing == null)
                        return AuctionBidResult.Fail(AuctionError.ListingNotFound);
                    if (listing.Status != AuctionListingStatus.Active)
                        return AuctionBidResult.Fail(AuctionError.ListingNotActive);
                    if (listing.SellerCharacterId == bidderCharacterId)
                        return AuctionBidResult.Fail(AuctionError.OwnListing);

                    // ★ 金币券挂牌只做一口价（点券购买），不参与竞拍，禁止出价。
                    if (IsGoldItem(listing.Item.ItemTemplateId))
                        return AuctionBidResult.Fail(AuctionError.BidTooLow);

                    // 最低出价 = 当前最高价（有竞价）或起拍价（无竞价）；出价必须严格高于它。
                    var floorBid = listing.CurrentBid > 0 ? listing.CurrentBid : listing.StartingPrice;
                    if (bidAmount <= floorBid)
                        return AuctionBidResult.Fail(AuctionError.BidTooLow);

                    // 金币立即扣除托管（出价即冻结，退回走邮件）。
                    if (!CurrencyService.TrySpendGold(
                            connection, transaction, bidderCharacterId, bidAmount))
                        return AuctionBidResult.Fail(AuctionError.InsufficientGold);

                    var bidderName = LoadCharacterName(connection, transaction, bidderCharacterId);

                    // 记录本次出价（status=0 进行中/当前最高）。
                    InsertBid(connection, transaction, listingId, bidderCharacterId, bidderName, bidAmount);

                    // 上一任最高出价者（若有）标记为被超价，稍后退回其托管金币。
                    var outbidCid = 0;
                    var outbidAmount = 0;
                    var outbidBidId = 0L;
                    if (listing.CurrentBidderId > 0 && listing.CurrentBid > 0)
                    {
                        outbidCid = listing.CurrentBidderId;
                        outbidAmount = listing.CurrentBid;
                        // 先取出被顶掉的那条「进行中」出价记录 id（最新一条 status=0），
                        // 供 handler 用唯一幂等 key 即时退回托管金币。
                        outbidBidId = FindCurrentBidId(connection, transaction, listingId, outbidCid);
                        MarkBidsOutbid(connection, transaction, listingId, outbidCid);
                    }

                    // 更新 listing 竞价字段。
                    UpdateListingBid(connection, transaction, listingId, bidAmount, bidderCharacterId, bidderName);

                    var wallet = CurrencyService.LoadWallet(connection, transaction, bidderCharacterId);

                    transaction.Commit();
                    FileLogger.Log(
                        $"[AuctionBid] cid={bidderCharacterId} 竞价成功 listingId={listingId} " +
                        $"出价={bidAmount}（当前最高）托管扣除后金币={wallet.Gold} " +
                        $"outbidCid={outbidCid} outbidAmount={outbidAmount}");
                    return new AuctionBidResult
                    {
                        Success = true,
                        Error = AuctionError.None,
                        ListingId = listingId,
                        BidAmount = bidAmount,
                        GoldAfter = wallet.Gold,
                        OutbidCharacterId = outbidCid,
                        OutbidAmount = outbidAmount,
                        OutbidBidId = outbidBidId,
                    };
                }
            }
        }

        /// <summary>加载某角色的出价记录（round57 放宽：领先中 status=0 + 被超价 status=1）。</summary>
        /// <remarks>
        ///   round56 版 WHERE status=0 会让所有 listing 已 Settled 的领先 bid 也被丢掉——
        ///   实测发现 settlement 流程未必把 bid.status 从 0 改到 2（中标），导致用户在
        ///   「我的竞价」面板看不到任何历史。现在返回该角色最近 64 条 bid，由 Handler
        ///   内存过滤 bid.Status &lt;= 1（领先中 + 被超价）。同 listing 多条 bid 由 Handler
        ///   按 listing_id dedup 后只取最新一笔。
        /// </remarks>
        public IReadOnlyList<AuctionBidRecord> LoadMyActiveBids(int bidderCharacterId)
        {
            var results = new List<AuctionBidRecord>();
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT bid_id, listing_id, bidder_character_id, bid_amount, bid_at_unix, status
                           FROM auction_bids
                           WHERE bidder_character_id = @cid
                           ORDER BY bid_at_unix DESC LIMIT 64";
                    command.Parameters.AddWithValue("@cid", bidderCharacterId);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadBidRecord(reader));
                    }
                }
            }
            return results;
        }

        /// <summary>加载某挂牌的全部出价记录（状态不限），供结算/退回遍历。</summary>
        public IReadOnlyList<AuctionBidRecord> LoadBidsByListing(long listingId)
        {
            var results = new List<AuctionBidRecord>();
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT bid_id, listing_id, bidder_character_id, bid_amount, bid_at_unix
                           FROM auction_bids
                           WHERE listing_id = @id
                           ORDER BY bid_at_unix ASC";
                    command.Parameters.AddWithValue("@id", listingId);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadBidRecord(reader));
                    }
                }
            }
            return results;
        }

        /// <summary>取走所有「被超价但金币尚未退回」的出价（status=1, refund_mail_sent=0），供结算退回。</summary>
        public IReadOnlyList<AuctionBidRecord> LoadPendingOutbidRefunds(int limit)
        {
            var results = new List<AuctionBidRecord>();
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT bid_id, listing_id, bidder_character_id, bid_amount, bid_at_unix
                           FROM auction_bids
                           WHERE status = 1 AND refund_mail_sent = 0
                           ORDER BY bid_at_unix ASC LIMIT @limit";
                    command.Parameters.AddWithValue("@limit", limit);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadBidRecord(reader));
                    }
                }
            }
            return results;
        }

        /// <summary>标记某出价为「被超价」（status=1），等待金币退回。</summary>
        public void MarkBidOutbid(long bidId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"UPDATE auction_bids SET status = 1 WHERE bid_id = @id AND status = 0";
                    command.Parameters.AddWithValue("@id", bidId);
                    command.ExecuteNonQuery();
                }
            }
        }

        /// <summary>标记某挂牌的指定出价者所有「进行中」出价为被超价（PlaceBid 事务内调用）。</summary>
        private static void MarkBidsOutbid(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long listingId,
            int bidderCharacterId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"UPDATE auction_bids
                    SET status = 1
                    WHERE listing_id = @id AND bidder_character_id = @cid AND status = 0";
                command.Parameters.AddWithValue("@id", listingId);
                command.Parameters.AddWithValue("@cid", bidderCharacterId);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>取指定出价者在某挂牌上最新一条「进行中」出价记录 id（供唯一幂等退回用）。</summary>
        private static long FindCurrentBidId(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long listingId,
            int bidderCharacterId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"SELECT bid_id FROM auction_bids
                    WHERE listing_id = @id AND bidder_character_id = @cid AND status = 0
                    ORDER BY bid_id DESC LIMIT 1";
                command.Parameters.AddWithValue("@id", listingId);
                command.Parameters.AddWithValue("@cid", bidderCharacterId);
                var result = command.ExecuteScalar();
                return result is long id ? id : 0L;
            }
        }

        /// <summary>标记某出价金币已退回（refund_mail_sent=1），避免重复退回。</summary>
        public void MarkBidRefunded(long bidId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"UPDATE auction_bids SET refund_mail_sent = 1 WHERE bid_id = @id";
                    command.Parameters.AddWithValue("@id", bidId);
                    command.ExecuteNonQuery();
                }
            }
        }

        /// <summary>把某挂牌的全部「进行中」出价标记为流拍（status=3），等待金币退回。</summary>
        public void MarkBidsUnsuccessful(long listingId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"UPDATE auction_bids SET status = 3 WHERE listing_id = @id AND status = 0";
                    command.Parameters.AddWithValue("@id", listingId);
                    command.ExecuteNonQuery();
                }
            }
        }

        /// <summary>把某挂牌的「进行中」出价中，除中标者外的其余全部标记为被超价（status=1）。</summary>
        public void MarkBidsOutbidExcept(long listingId, int winningBidderId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"UPDATE auction_bids SET status = 1
                        WHERE listing_id = @id AND status = 0 AND bidder_character_id != @cid";
                    command.Parameters.AddWithValue("@id", listingId);
                    command.Parameters.AddWithValue("@cid", winningBidderId);
                    command.ExecuteNonQuery();
                }
            }
        }

        /// <summary>把某挂牌的中标出价标记为中标（status=2）。</summary>
        public void MarkWinningBid(long listingId, int winningBidderId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"UPDATE auction_bids SET status = 2
                        WHERE listing_id = @id AND bidder_character_id = @cid AND status = 0";
                    command.Parameters.AddWithValue("@id", listingId);
                    command.Parameters.AddWithValue("@cid", winningBidderId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static AuctionBidRecord ReadBidRecord(SqliteDataReader reader)
        {
            return new AuctionBidRecord
            {
                BidId = reader.GetInt64(0),
                ListingId = reader.GetInt64(1),
                BidderCharacterId = reader.GetInt32(2),
                BidAmount = reader.GetInt32(3),
                BidAtUnix = reader.GetInt64(4),
            };
        }

        private static void InsertBid(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long listingId,
            int bidderCharacterId,
            string bidderName,
            int bidAmount)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"INSERT INTO auction_bids (
                    listing_id, bidder_character_id, bidder_name, bid_amount, status, bid_at_unix)
                VALUES (@listingId, @cid, @name, @amount, 0, @at)";
                command.Parameters.AddWithValue("@listingId", listingId);
                command.Parameters.AddWithValue("@cid", bidderCharacterId);
                command.Parameters.AddWithValue("@name", bidderName ?? string.Empty);
                command.Parameters.AddWithValue("@amount", bidAmount);
                command.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                command.ExecuteNonQuery();
            }
        }

        private static void UpdateListingBid(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long listingId,
            int bidAmount,
            int bidderCharacterId,
            string bidderName)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"UPDATE auction_listings
                    SET current_bid = @bid,
                        current_bidder_id = @cid,
                        current_bidder_name = @name,
                        bid_count = bid_count + 1,
                        updated_at = CURRENT_TIMESTAMP
                    WHERE listing_id = @id";
                command.Parameters.AddWithValue("@bid", bidAmount);
                command.Parameters.AddWithValue("@cid", bidderCharacterId);
                command.Parameters.AddWithValue("@name", bidderName ?? string.Empty);
                command.Parameters.AddWithValue("@id", listingId);
                command.ExecuteNonQuery();
            }
        }

        public AuctionCancelResult Cancel(long listingId, int sellerCharacterId)
        {
            if (listingId <= 0 || sellerCharacterId <= 0)
                return AuctionCancelResult.Fail(AuctionError.InvalidRequest);

            // 卖家必须在线：金币券下架要把托管金币退回其内存背包虚拟槽（权威状态），
            // 离线无法回退；普通道具下架改走邮件退回，同样要求在线以保持入口一致。
            if (!InventoryContext.TryGetLease(sellerCharacterId, out var lease)
                || lease.CharacterId != sellerCharacterId)
                return AuctionCancelResult.Fail(AuctionError.ServerBusy);

            lock (lease.SyncRoot)
            {
                var inventory = lease.Inventory;
                if (inventory == null)
                    return AuctionCancelResult.Fail(AuctionError.ServerBusy);

                bool inventoryMutated = false;
                AuctionCancelResult FailWithRollback(AuctionError error)
                {
                    if (inventoryMutated)
                        ReloadInventoryAfterRollback(lease);
                    return AuctionCancelResult.Fail(error);
                }

                try
                {
                    using (var connection = new SqliteConnection(_connectionString))
                    {
                        connection.Open();
                        using (var transaction = connection.BeginTransaction(deferred: false))
                        {
                            var listing = GetListing(connection, transaction, listingId);
                            if (listing == null || listing.SellerCharacterId != sellerCharacterId)
                                return FailWithRollback(AuctionError.ListingNotFound);
                            if (listing.Status != AuctionListingStatus.Active)
                                return FailWithRollback(AuctionError.ListingNotActive);

                            // ★ 金币券挂牌：不退回道具，直接把托管金币退回虚拟槽 0，即时结算。
                            //   （金币券只是「点券换金币」的凭证，从未作为实体道具进背包，
                            //     下架必须退金币，否则玩家会「金币消失」且挂牌不消失。）
                            if (IsGoldItem(listing.Item.ItemTemplateId))
                            {
                                if (!TryGetGoldValue(listing.Item.ItemTemplateId, out var face) || face <= 0)
                                    return FailWithRollback(AuctionError.ItemNotFound);
                                var goldCount = Math.Max(1, listing.Item.ItemCount);
                                var refundLong = (long)face * goldCount;
                                if (refundLong > int.MaxValue)
                                    return FailWithRollback(AuctionError.GoldLimitExceeded);

                                var goldBefore = inventory.GetMainVirtualCount(
                                    InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
                                var goldAfter = (long)goldBefore + refundLong;
                                if (goldAfter > int.MaxValue)
                                    goldAfter = int.MaxValue;
                                if (!inventory.SetMainVirtualCount(
                                        InventoryService.MainVirtualCurrencySlotStart,
                                        (int)goldAfter))
                                    return FailWithRollback(AuctionError.ServerBusy);
                                inventoryMutated = true;

                                if (!InventoryPersistenceService.SaveDirtyInTransaction(
                                        connection, transaction, lease))
                                    return FailWithRollback(AuctionError.ServerBusy);

                                if (!TransitionStatus(
                                        connection, transaction, listingId,
                                        AuctionListingStatus.Cancelled, 0, string.Empty))
                                    return FailWithRollback(AuctionError.ListingNotActive);

                                // 金币已同事务退回，标记 Settled 防 tick 重复补发邮件（dupe）。
                                MarkSettledInTransaction(connection, transaction, listingId);

                                transaction.Commit();
                                lease.Inventory.ClearDirtyState();

                                FileLogger.Log(
                                    $"[AuctionCancel] cid={sellerCharacterId} 金币下架成功 listingId={listingId} " +
                                    $"itemId={listing.Item.ItemTemplateId} face={face} count={goldCount} " +
                                    $"退回金币={refundLong} goldAfter={goldAfter}");
                                return new AuctionCancelResult
                                {
                                    Success = true,
                                    Error = AuctionError.None,
                                    ListingId = listingId,
                                    IsGold = true,
                                };
                            }

                            // ★ 下架改走邮件退回（2026-09-05 用户拍板）：本事务内不动背包，
                            //   只把状态推进到 Cancelled 并保持 settle_mail_count=0，由
                            //   AuctionService.SettleCancelled（handler 即时调用，失败由分钟
                            //   tick 的 LoadPendingSettlements 补发）通过系统邮件把托管道具
                            //   退回卖家。道具块快照在 auction_listings.item_core_data（其
                            //   Value 字段是实际上架数量），邮件附件解码即得应退回数量，
                            //   拆堆上架场景同样正确。绝不能在此 MarkSettled，否则退回邮件
                            //   永远不会发出（道具丢失）。
                            if (!TransitionStatus(
                                    connection, transaction, listingId,
                                    AuctionListingStatus.Cancelled, 0, string.Empty))
                                return FailWithRollback(AuctionError.ListingNotActive);

                            transaction.Commit();
                            lease.Inventory.ClearDirtyState();

                            FileLogger.Log(
                                $"[AuctionCancel] cid={sellerCharacterId} 下架成功(邮件退回) " +
                                $"listingId={listingId} item={listing.Item.ItemTemplateId} " +
                                $"count={listing.Item.ItemCount}");
                            return new AuctionCancelResult
                            {
                                Success = true,
                                Error = AuctionError.None,
                                ListingId = listingId,
                            };
                        }
                    }
                }
                catch
                {
                    if (inventoryMutated)
                        ReloadInventoryAfterRollback(lease);
                    throw;
                }
            }
        }

        // ------------------------------------------------------------------
        // 到期与结算对账（分钟 tick）
        // ------------------------------------------------------------------

        /// <summary>把到期仍未售出的上架单标记为 Expired。</summary>
        public int MarkExpiredListings(long nowUnix)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"UPDATE auction_listings
                        SET status = @expired
                        WHERE status = @active AND expires_at_unix <= @now";
                    command.Parameters.AddWithValue("@expired", (int)AuctionListingStatus.Expired);
                    command.Parameters.AddWithValue("@active", (int)AuctionListingStatus.Active);
                    command.Parameters.AddWithValue("@now", nowUnix);
                    var affected = command.ExecuteNonQuery();
                    transaction.Commit();
                    return affected;
                }
            }
        }

        /// <summary>
        /// 把「到期且已有竞价（current_bid &gt; 0）」的上架单标记为 Sold（竞价成交），
        /// buyer = 当前最高出价者。必须在 MarkExpiredListings 之后、结算邮件之前调用，
        /// 否则这类单会被 MarkExpiredListings 先标成 Expired 再按「退回卖家」结算，把道具
        /// 错退给卖家而不是最高价者。返回转换数量。
        /// </summary>
        public int MarkExpiredWithBidsAsSold(long nowUnix)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    // 只转「仍未售出、已到期、且确实有人竞价」的 Active 单。
                    // 注意：MarkExpiredListings 已把「无竞价」的到期单标成 Expired，
                    // 有竞价的到期单在 MarkExpiredListings 里也会被标 Expired（status=3），
                    // 因此这里按「status=Expired 且 current_bid>0」来识别更稳妥。
                    command.CommandText = @"UPDATE auction_listings
                        SET status = @sold,
                            buyer_character_id = current_bidder_id,
                            buyer_name = current_bidder_name,
                            sold_at_unix = @now
                        WHERE status = @expired AND current_bid > 0 AND current_bidder_id > 0";
                    command.Parameters.AddWithValue("@sold", (int)AuctionListingStatus.Sold);
                    command.Parameters.AddWithValue("@expired", (int)AuctionListingStatus.Expired);
                    command.Parameters.AddWithValue("@now", nowUnix);
                    var affected = command.ExecuteNonQuery();
                    transaction.Commit();
                    return affected;
                }
            }
        }

        /// <summary>取走所有需要补发结算邮件的上架单（崩溃恢复对账）。</summary>
        public IReadOnlyList<AuctionListing> LoadPendingSettlements(int limit)
        {
            var results = new List<AuctionListing>();
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT listing_id, seller_character_id, seller_name, item_type,
                                  source_list_type, source_slot_index, item_template_id, item_kind,
                                  item_count, instance_value, durability, seal_flag, option_value,
                                  expire_time, marker16, pet_serial_or_handle, extra_json,
                                  item_core_data, detail_json, buyout_price, status,
                                  buyer_character_id, buyer_name, listed_at_unix, expires_at_unix,
                                  sold_at_unix, settle_mail_count,
                                  current_bid, current_bidder_id, current_bidder_name, bid_count, starting_price
                           FROM auction_listings
                           WHERE status IN (1, 2, 3) AND settle_mail_count = 0
                           ORDER BY listed_at_unix ASC LIMIT @limit";
                    command.Parameters.AddWithValue("@limit", limit);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadListing(reader));
                    }
                }
            }
            return results;
        }

        public void MarkSettled(long listingId, int mailCount)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"UPDATE auction_listings
                        SET settle_mail_count = @count, status = @settled
                        WHERE listing_id = @id";
                    command.Parameters.AddWithValue("@count", mailCount);
                    command.Parameters.AddWithValue("@settled", (int)AuctionListingStatus.Settled);
                    command.Parameters.AddWithValue("@id", listingId);
                    command.ExecuteNonQuery();
                }
            }
        }

        /// <summary>
        /// 金币券挂牌的到期流拍退款（分钟 tick 调用）：把托管金币直接退回卖家虚拟槽，
        /// 不走邮件（金币券从未作为实体道具入包）。幂等由 settle_mail_count / status 保证。
        /// </summary>
        public bool SettleGoldReturn(long listingId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var listing = GetListing(connection, transaction, listingId);
                    if (listing == null || !IsGoldItem(listing.Item.ItemTemplateId))
                        return false;
                    if (listing.Status != AuctionListingStatus.Expired
                        && listing.Status != AuctionListingStatus.Cancelled)
                        return false;
                    if (listing.SettleMailCount > 0)
                        return true; // 已结算，幂等跳过

                    if (!TryGetGoldValue(listing.Item.ItemTemplateId, out var face) || face <= 0)
                        return false;
                    var count = Math.Max(1, listing.Item.ItemCount);
                    var refundLong = (long)face * count;
                    if (refundLong > int.MaxValue)
                        refundLong = int.MaxValue;

                    CurrencyService.GrantGold(
                        connection, transaction, listing.SellerCharacterId, (int)refundLong);
                    MarkSettledInTransaction(connection, transaction, listingId);

                    transaction.Commit();

                    // 卖家若在线，重载其内存库存使退回金币即时可见（否则下次选角才刷新）。
                    if (InventoryContext.TryGetLease(listing.SellerCharacterId, out var lease)
                        && lease.CharacterId == listing.SellerCharacterId)
                    {
                        try { ReloadInventoryAfterRollback(lease); }
                        catch (Exception ex)
                        {
                            FileLogger.Log($"[Auction] gold return reload failed: {ex}");
                        }
                    }

                    FileLogger.Log(
                        $"[AuctionGoldExpire] listing={listingId} seller={listing.SellerCharacterId} " +
                        $"face={face} count={count} 退回金币={refundLong}");
                    return true;
                }
            }
        }

        public int CountActiveListings(int sellerCharacterId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT COUNT(*) FROM auction_listings
                        WHERE seller_character_id = @cid AND status = @active";
                    command.Parameters.AddWithValue("@cid", sellerCharacterId);
                    command.Parameters.AddWithValue("@active", (int)AuctionListingStatus.Active);
                    var result = command.ExecuteScalar();
                    return result is long value ? (int)value : 0;
                }
            }
        }

        // ------------------------------------------------------------------
        // 拍卖行机器人（AuctionBotService 专用）
        // ------------------------------------------------------------------

        /// <summary>
        /// 回收扫描：加载全部「非 bot 的、一口价&gt;0 的、无竞价」Active 挂牌。
        /// 金币券由调用方按 IsGoldItem 过滤（家族表在代码里，SQL 不硬编码 id 区间）。
        /// </summary>
        public IReadOnlyList<AuctionListing> LoadActiveListingsForBotScan(int botSellerId, int limit)
        {
            var result = new List<AuctionListing>();
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT listing_id, seller_character_id, seller_name, item_type,
                                  source_list_type, source_slot_index, item_template_id, item_kind,
                                  item_count, instance_value, durability, seal_flag, option_value,
                                  expire_time, marker16, pet_serial_or_handle, extra_json,
                                  item_core_data, detail_json, buyout_price, status,
                                  buyer_character_id, buyer_name, listed_at_unix, expires_at_unix,
                                  sold_at_unix, settle_mail_count,
                                  current_bid, current_bidder_id, current_bidder_name, bid_count, starting_price
                           FROM auction_listings
                           WHERE status = @active
                             AND seller_character_id != @bot
                             AND buyout_price > 0
                             AND current_bid = 0
                             AND bid_count = 0
                           ORDER BY listed_at_unix ASC
                           LIMIT @limit";
                    command.Parameters.AddWithValue("@active", (int)AuctionListingStatus.Active);
                    command.Parameters.AddWithValue("@bot", botSellerId);
                    command.Parameters.AddWithValue("@limit", Math.Max(1, limit));
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                            result.Add(ReadListing(reader));
                }
            }
            return result;
        }

        /// <summary>
        /// bot 回收购买：把 Active 挂牌转 Sold（买家 = bot）。不扣款（系统印钞回收，
        /// 卖家所得由结算路径发系统邮件）。成功返回成交前的挂牌快照，失败返回 null。
        /// 状态机 UPDATE ... WHERE status=0 保护与玩家 Buyout 同款，天然防并发双买。
        /// </summary>
        public AuctionListing BotPurchase(long listingId, int botBuyerId, string botBuyerName)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var listing = GetListing(connection, transaction, listingId);
                    if (listing == null || listing.Status != AuctionListingStatus.Active)
                        return null;
                    if (listing.SellerCharacterId == botBuyerId)
                        return null;
                    if (!TransitionStatus(
                            connection, transaction, listingId,
                            AuctionListingStatus.Sold, botBuyerId, botBuyerName))
                        return null;

                    transaction.Commit();
                    return listing;
                }
            }
        }

        /// <summary>bot 补货上架：直接插入挂牌（无背包托管，道具由系统生成）。返回 listingId。</summary>
        public long InsertBotListing(
            int botSellerId,
            string botSellerName,
            AuctionItemSnapshot snapshot,
            int unitPrice,
            TimeSpan duration)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                // ★ bot 卖家 id（-1000001）在 characters 表无对应行，而 auction_listings.
                //   seller_character_id 有 FOREIGN KEY 引用且本服务连接默认开启 FK 强制。
                //   SQLite 的 FK 强制是【按连接】的 pragma：这里显式关闭本连接的 FK 检查，
                //   避免为 bot 伪造 accounts/characters 行（那会侵入角色系统）。
                //   影响面仅限本方法这条短连接，其余路径的 FK 行为不变。
                using (var pragma = connection.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA foreign_keys = OFF;";
                    pragma.ExecuteNonQuery();
                }
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var listingId = InsertListing(
                        connection, transaction,
                        botSellerId, botSellerName, snapshot,
                        Math.Max(1, unitPrice), 0, duration);
                    transaction.Commit();
                    return listingId;
                }
            }
        }

        /// <summary>某卖家某物品的在架件数（bot 补货差额计算用）。</summary>
        public int CountActiveListingsByItem(int sellerCharacterId, int itemId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT COUNT(*) FROM auction_listings
                        WHERE seller_character_id = @cid AND item_template_id = @item AND status = @active";
                    command.Parameters.AddWithValue("@cid", sellerCharacterId);
                    command.Parameters.AddWithValue("@item", itemId);
                    command.Parameters.AddWithValue("@active", (int)AuctionListingStatus.Active);
                    var result = command.ExecuteScalar();
                    return result is long value ? (int)value : 0;
                }
            }
        }

        /// <summary>某物品全市场在架件数（含玩家 + bot，饱和度计算用）。</summary>
        public int CountActiveListingsOfItem(int itemId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT COUNT(*) FROM auction_listings
                        WHERE item_template_id = @item AND status = @active";
                    command.Parameters.AddWithValue("@item", itemId);
                    command.Parameters.AddWithValue("@active", (int)AuctionListingStatus.Active);
                    var result = command.ExecuteScalar();
                    return result is long value ? (int)value : 0;
                }
            }
        }

        /// <summary>读 bot 调价倍率（无记录 = 1.0）。</summary>
        public double GetBotPriceMultiplier(int itemId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT multiplier FROM auction_bot_price_adj
                        WHERE item_id = @item";
                    command.Parameters.AddWithValue("@item", itemId);
                    var result = command.ExecuteScalar();
                    if (result is double d)
                        return d;
                    if (result is long l)
                        return l;
                    return 1.0;
                }
            }
        }

        /// <summary>
        /// 应用一次 ±3% 调价（upsert，钳制 [minMult, maxMult]，累计 sold/recycled 计数）。
        /// factor：卖出 = 1.03，回收 = 0.97（由 AuctionBotService 按配置计算）。
        /// 返回应用后的倍率。
        /// </summary>
        public double ApplyBotPriceMultiplier(
            int itemId, double factor, bool isSale, double minMult, double maxMult)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var current = 1.0;
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"SELECT multiplier FROM auction_bot_price_adj
                            WHERE item_id = @item";
                        command.Parameters.AddWithValue("@item", itemId);
                        var result = command.ExecuteScalar();
                        if (result is double d)
                            current = d;
                        else if (result is long l)
                            current = l;
                    }

                    var next = Math.Max(minMult, Math.Min(maxMult, current * factor));
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"INSERT INTO auction_bot_price_adj
                                (item_id, multiplier, sold_count, recycled_count, updated_at_unix)
                            VALUES (@item, @mult, @sold, @recycled, @now)
                            ON CONFLICT(item_id) DO UPDATE SET
                                multiplier = @mult,
                                sold_count = sold_count + @sold,
                                recycled_count = recycled_count + @recycled,
                                updated_at_unix = @now";
                        command.Parameters.AddWithValue("@item", itemId);
                        command.Parameters.AddWithValue("@mult", next);
                        command.Parameters.AddWithValue("@sold", isSale ? 1 : 0);
                        command.Parameters.AddWithValue("@recycled", isSale ? 0 : 1);
                        command.Parameters.AddWithValue("@now",
                            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        command.ExecuteNonQuery();
                    }

                    transaction.Commit();
                    return next;
                }
            }
        }

        // ------------------------------------------------------------------
        // 内部实现
        // ------------------------------------------------------------------

        /// <summary>
        /// 推导上架托管的真实数量。
        ///
        /// ItemCore.Value（core[5..8]）是**联合字段**，语义由 ItemKind 决定
        /// （见 ItemCore.Count / AvatarUid / CreatureUid 三个别名属性都映射到 Value）：
        ///   - 装备(1) / 宠物(5) / 时装(8)：Value = 唯一实例 UID，道具不可堆叠，数量恒为 1；
        ///   - 其余种类（消耗品2 / 材料3 / 任务4 / 宠物装备6 / 宠物消耗7 / 徽章9 /
        ///     专家材料10 / 特殊材料11 / 公会勋章12 / 守护珠13 / 史诗碎片14）：
        ///     Value = 堆叠数量。
        ///
        /// ★ round100 回归拆堆：按客户端请求数量部分上架。
        ///   历史（round18 方案一）曾强制整组上架，原因是客户端清 0x20c 上架锁的
        ///   函数 0x232f610 假设「整堆上架、背包该堆彻底消失」，拆堆残留 keepCount
        ///   会使锁永不释放、二次上架锁死。现用户已在客户端侧制作清锁补丁
        ///   （上架即可清 0x20c），服务端随之回归 round11 的拆堆行为：
        ///   listedCount = min(requestedCount, stackTotal)，背包保留剩余 keepCount。
        ///   requestedCount<=0（异常包）时退化为整堆托管。
        ///
        /// 该值写入 auction_listings.item_count；同时**必须**同步改写
        /// item_core_data 的 Value 字段（core[5..8]），因为客户端网格的数量列读的是
        /// 道具块 Value 而不是头部——两者不一致会出现「DB 记 5 个、界面显示 11 个」。
        /// </summary>
        private static int ResolveListingCount(ItemCore core, int requestedCount)
        {
            if (core == null)
                return Math.Max(1, requestedCount);

            if (!IsStackableKind(core))
                return 1;

            var stackTotal = core.Count > 0 ? core.Count : 1;
            if (requestedCount <= 0)
                return stackTotal; // 异常数量包退化为整堆托管

            return Math.Min(requestedCount, stackTotal);
        }

        /// <summary>
        /// Value(core[5..8]) 是否承载「堆叠数量」而非唯一实例 UID。
        /// 装备(1)/宠物(5)/时装(8) 为 UID，不可拆堆；其余种类均按数量处理。
        /// </summary>
        private static bool IsStackableKind(ItemCore core)
        {
            if (core == null)
                return false;

            switch (core.ItemKind)
            {
                case ItemCore.KindEquipment:
                case ItemCore.KindCreature:
                case ItemCore.KindAvatar:
                    return false;
                default:
                    return true;
            }
        }

        private static AuctionItemSnapshot BuildSnapshot(
            InventoryListType listType,
            short slotIndex,
            ItemCore core,
            int listedCount)
        {
            var stackable = IsStackableKind(core);

            // ★ round52 定案：挂牌时对可堆叠道具**重算 item_kind**，不信任道具块里固化的旧 kind 字节。
            //   根因：背包道具的 kind 持久化在 item_core_data blob 第 0 字节（ItemKindOffset=0），
            //   玩家在服务端分类逻辑更新前就持有的道具，其 kind 仍是旧值（如 [throw] 飞盘存 "2" 消耗品）。
            //   ResolveStackableItemKind 的 throw/waste/misc 细分在挂牌链路里从未被调用，
            //   导致「投掷品被消耗品命中」。这里按 itemId 重新解析，保证 item_kind 列与最新归类一致，
            //   同时改写 blob[0] 让 item_core_data 里的 kind 字节同步（避免购买/结算时又读回旧值）。
            if (stackable
                && core.ItemId > 0
                && ItemMetadataResolver.TryResolveItemKind(core.ItemId, out var resolvedKind)
                && resolvedKind != core.ItemKind)
            {
                core.ItemKind = resolvedKind;
            }

            var blob = MailboxItemCoreCodec.Encode(core);

            // ★ 拆堆后道具块里的 Value 仍是原始整堆数量，必须改写成实际上架数量，
            //   否则 BuildMyListingBody 把这段原样搬进 wire[30..112]，
            //   客户端读道具块 Value 当数量 -> 界面显示的还是整堆数。
            if (stackable && blob != null && blob.Length > ItemCore.ValueOffset + 3)
            {
                blob[ItemCore.ValueOffset] = (byte)(listedCount & 0xFF);
                blob[ItemCore.ValueOffset + 1] = (byte)((listedCount >> 8) & 0xFF);
                blob[ItemCore.ValueOffset + 2] = (byte)((listedCount >> 16) & 0xFF);
                blob[ItemCore.ValueOffset + 3] = (byte)((listedCount >> 24) & 0xFF);
            }

            return new AuctionItemSnapshot
            {
                ItemType = 0,
                SourceListType = (int)listType,
                SourceSlotIndex = slotIndex,
                ItemTemplateId = core.ItemId,
                ItemKind = core.ItemKind.ToString(),
                ItemCount = listedCount,
                InstanceValue = stackable ? listedCount : core.Value,
                Durability = core.Durability,
                SealFlag = core.SealFlag,
                OptionValue = 0,
                ExpireTime = core.ExpireTime,
                Marker16 = core.Marker16,
                PetSerialOrHandle = 0,
                ItemCoreData = blob,
            };
        }

        private void ReloadInventoryAfterRollback(InventoryLease lease)
        {
            try
            {
                InventoryRollbackRecoveryService.ReloadOnlineInventory(
                    _connectionString, lease);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Auction] reload after rollback failed: {ex}");
            }
        }

        private static string LoadCharacterName(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT name FROM characters WHERE character_id = @cid";
                command.Parameters.AddWithValue("@cid", characterId);
                var result = command.ExecuteScalar();
                // characters.name 列存的是 GBK 字节（BLOB），旧代码 `result as string`
                // 对 BLOB 恒为 null，导致 seller_name 空串。这里按字节解码。
                if (result is byte[] bytes)
                    return ClientTextEncoding.GetString(bytes);
                if (result is string s)
                    return s;
                return string.Empty;
            }
        }

        private static long InsertListing(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int sellerCharacterId,
            string sellerName,
            AuctionItemSnapshot snapshot,
            int buyoutPrice,
            int startingPrice,
            TimeSpan duration)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var expires = now + (long)duration.TotalSeconds;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"INSERT INTO auction_listings (
                    seller_character_id, seller_name,
                    item_type, source_list_type, source_slot_index,
                    item_template_id, item_kind, item_count, instance_value,
                    durability, seal_flag, option_value, expire_time, marker16,
                    pet_serial_or_handle, extra_json, item_core_data, detail_json,
                    buyout_price, starting_price, status, listed_at_unix, expires_at_unix)
                VALUES (
                    @cid, @name,
                    @itemType, @srcList, @srcSlot,
                    @itemId, @itemKind, @itemCount, @instanceValue,
                    @durability, @sealFlag, @optionValue, @expireTime, @marker16,
                    @petHandle, @extraJson, @coreData, @detailJson,
                    @price, @startingPrice, 0, @listedAt, @expiresAt);
                SELECT last_insert_rowid();";
                command.Parameters.AddWithValue("@cid", sellerCharacterId);
                command.Parameters.AddWithValue("@name", sellerName ?? string.Empty);
                command.Parameters.AddWithValue("@itemType", snapshot.ItemType);
                command.Parameters.AddWithValue("@srcList", snapshot.SourceListType);
                command.Parameters.AddWithValue("@srcSlot", snapshot.SourceSlotIndex);
                command.Parameters.AddWithValue("@itemId", snapshot.ItemTemplateId);
                command.Parameters.AddWithValue("@itemKind", snapshot.ItemKind ?? "unknown");
                command.Parameters.AddWithValue("@itemCount", snapshot.ItemCount);
                command.Parameters.AddWithValue("@instanceValue", snapshot.InstanceValue);
                command.Parameters.AddWithValue("@durability", snapshot.Durability);
                command.Parameters.AddWithValue("@sealFlag", snapshot.SealFlag);
                command.Parameters.AddWithValue("@optionValue", snapshot.OptionValue);
                command.Parameters.AddWithValue("@expireTime", snapshot.ExpireTime);
                command.Parameters.AddWithValue("@marker16", snapshot.Marker16);
                command.Parameters.AddWithValue("@petHandle", snapshot.PetSerialOrHandle);
                command.Parameters.AddWithValue("@extraJson", snapshot.ExtraJson ?? "{}");
                command.Parameters.AddWithValue("@coreData", snapshot.ItemCoreData ?? Array.Empty<byte>());
                command.Parameters.AddWithValue("@detailJson", snapshot.DetailJson ?? string.Empty);
                command.Parameters.AddWithValue("@price", buyoutPrice);
                command.Parameters.AddWithValue("@startingPrice", startingPrice);
                command.Parameters.AddWithValue("@listedAt", now);
                command.Parameters.AddWithValue("@expiresAt", expires);
                var result = command.ExecuteScalar();
                return result is long value ? value : 0;
            }
        }

        private static bool TransitionStatus(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long listingId,
            AuctionListingStatus target,
            int buyerCharacterId,
            string buyerName)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"UPDATE auction_listings
                    SET status = @status,
                        buyer_character_id = @buyerCid,
                        buyer_name = @buyerName,
                        sold_at_unix = @soldAt
                    WHERE listing_id = @id AND status = @active";
                command.Parameters.AddWithValue("@status", (int)target);
                command.Parameters.AddWithValue("@buyerCid", buyerCharacterId);
                command.Parameters.AddWithValue("@buyerName", buyerName ?? string.Empty);
                command.Parameters.AddWithValue("@soldAt",
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                command.Parameters.AddWithValue("@id", listingId);
                command.Parameters.AddWithValue("@active", (int)AuctionListingStatus.Active);
                return command.ExecuteNonQuery() == 1;
            }
        }

        /// <summary>
        /// 事务内把挂牌标记为「已结算」（status=Settled + settle_mail_count=1）。
        /// 用于下架：道具已在同一事务内退回背包，直接标记结算完成，
        /// 使 LoadPendingSettlements 的 settle_mail_count=0 过滤不再命中，避免邮件重复补发。
        /// </summary>
        private static void MarkSettledInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long listingId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"UPDATE auction_listings
                    SET status = @settled, settle_mail_count = 1
                    WHERE listing_id = @id";
                command.Parameters.AddWithValue("@settled", (int)AuctionListingStatus.Settled);
                command.Parameters.AddWithValue("@id", listingId);
                command.ExecuteNonQuery();
            }
        }

        private static AuctionListing GetListing(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long listingId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"SELECT listing_id, seller_character_id, seller_name, item_type,
                                  source_list_type, source_slot_index, item_template_id, item_kind,
                                  item_count, instance_value, durability, seal_flag, option_value,
                                  expire_time, marker16, pet_serial_or_handle, extra_json,
                                  item_core_data, detail_json, buyout_price, status,
                                  buyer_character_id, buyer_name, listed_at_unix, expires_at_unix,
                                  sold_at_unix, settle_mail_count,
                                  current_bid, current_bidder_id, current_bidder_name, bid_count, starting_price
                           FROM auction_listings WHERE listing_id = @id";
                command.Parameters.AddWithValue("@id", listingId);
                using (var reader = command.ExecuteReader())
                    return reader.Read() ? ReadListing(reader) : null;
            }
        }

        private static AuctionListing ReadListing(SqliteDataReader reader)
        {
            return new AuctionListing
            {
                ListingId = reader.GetInt64(0),
                SellerCharacterId = reader.GetInt32(1),
                SellerName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Item = new AuctionItemSnapshot
                {
                    ItemType = reader.GetByte(3),
                    SourceListType = reader.GetInt32(4),
                    SourceSlotIndex = reader.GetInt32(5),
                    ItemTemplateId = reader.GetInt32(6),
                    ItemKind = reader.IsDBNull(7) ? "unknown" : reader.GetString(7),
                    ItemCount = reader.GetInt32(8),
                    InstanceValue = reader.GetInt32(9),
                    Durability = reader.GetInt32(10),
                    SealFlag = reader.GetInt32(11),
                    OptionValue = reader.GetInt32(12),
                    ExpireTime = reader.GetInt32(13),
                    Marker16 = reader.GetInt32(14),
                    PetSerialOrHandle = reader.GetInt32(15),
                    ExtraJson = reader.IsDBNull(16) ? "{}" : reader.GetString(16),
                    ItemCoreData = reader.IsDBNull(17) ? Array.Empty<byte>() : (byte[])reader.GetValue(17),
                    DetailJson = reader.IsDBNull(18) ? string.Empty : reader.GetString(18),
                },
                BuyoutPrice = reader.GetInt32(19),
                Status = (AuctionListingStatus)reader.GetInt32(20),
                BuyerCharacterId = reader.GetInt32(21),
                BuyerName = reader.IsDBNull(22) ? string.Empty : reader.GetString(22),
                ListedAtUnix = reader.GetInt64(23),
                ExpiresAtUnix = reader.GetInt64(24),
                SoldAtUnix = reader.IsDBNull(25) ? 0 : reader.GetInt64(25),
                SettleMailCount = reader.GetInt32(26),
                // ★ 竞价字段：序号 27~30 紧跟 settle_mail_count（所有 SELECT 已统一追加，
                //   见 SearchActiveListings / LoadListingsBySeller / GetListing /
                //   LoadPendingSettlements 的列清单）。
                CurrentBid = reader.GetInt32(27),
                CurrentBidderId = reader.GetInt32(28),
                CurrentBidderName = reader.IsDBNull(29) ? string.Empty : reader.GetString(29),
                BidCount = reader.GetInt32(30),
                // 起拍价：序号 31（SELECT 列表末尾，紧跟 bid_count）。
                StartingPrice = reader.IsDBNull(31) ? 0 : reader.GetInt32(31),
            };
        }

        private static AuctionError MapPolicyError(MailboxSendError error)
        {
            switch (error)
            {
                case MailboxSendError.ItemLocked:
                case MailboxSendError.NotTradable:
                case MailboxSendError.AccountBound:
                case MailboxSendError.TradeRestricted:
                    return AuctionError.ItemNotTradable;
                default:
                    return AuctionError.InvalidRequest;
            }
        }
    }

    /// <summary>拍卖行数值策略（MVP 一口价版，常量集中便于后续调参）。</summary>
    public static class AuctionPolicy
    {
        /// <summary>
        /// 同时上架上限。官方无拍卖券角色为 5；本端调大到 100（round97，2026-09-05）：
        /// 达到上限时服务端只能回通用失败码 0xd2，而客户端给 0xd2 配的弹窗文本是
        /// 「安全模式」提示（msg 0x11205），玩家会误以为账号进了安全模式。
        /// 私服测试场景 5 件太容易触顶（金币寄售 + 道具混挂），直接放宽避免误导弹窗。
        /// </summary>
        public const int MaxActiveListingsPerCharacter = 100;

        /// <summary>默认上架时长：48 小时。</summary>
        public static readonly TimeSpan DefaultListingDuration = TimeSpan.FromHours(48);

        /// <summary>上架手续费率（上架时收取，下架/流拍不退，防刷）。</summary>
        public const double ListingFeeRate = 0.01;

        /// <summary>成交时从卖家所得中抽取的成交税率。</summary>
        public const double SaleTaxRate = 0.05;

        /// <summary>
        /// 金币寄售上架基础手续费：固定 10,000 金币/笔（round94 用户拍板）。
        /// 与面额/张数无关；替代旧的「托管金币 1%」比例费（大面额等比放大，惩罚性）。
        /// </summary>
        public const int GoldListingFlatFee = 10_000;

        /// <summary>单件最高价：与拍卖行金币额度上限对齐。</summary>
        public const int MaxPrice = 400_000_000;

        /// <summary>
        /// 上架手续费 = 一口价 × 1%（ListingFeeRate），且封顶为一口价的 10%。
        /// 低价物品允许少收（无硬性下限）——用户 2026-09-03 拍板「允许少收」。
        /// </summary>
        public static int CalculateListingFee(int buyoutPrice)
        {
            var fee = (int)(buyoutPrice * ListingFeeRate);
            return Math.Min(fee, buyoutPrice / 10);
        }

        public static int CalculateSellerProceeds(int buyoutPrice)
        {
            var proceeds = (int)(buyoutPrice * (1.0 - SaleTaxRate));
            return Math.Max(0, Math.Min(proceeds, buyoutPrice));
        }
    }
}
