using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Auction
{
    /// <summary>
    /// 拍卖行机器人：自动回收 + 自动补货 + ±3% 动态调价。
    ///
    /// 灵感来自台服端 auction 工具 V4.0（直写 MySQL 的外部程序），本实现按本服架构
    /// 重写为 DfoServer 内嵌后台服务，复用拍卖行/邮件既有链路：
    ///
    ///   · 回收：扫描玩家一口价挂牌（跳过金币券/有竞价/纯竞拍），按 R=单价/P_std
    ///     五段概率买走。买家=bot → SettleSoldListing 跳过买家邮件（道具销毁），
    ///     卖家照常收 95% 金币邮件（系统印钞回收，工具同款语义）。
    ///   · 补货：按 Data/auction_bot_items.txt 目标件数，伪造完整 ItemCore
    ///     （含强化位域）直接 INSERT 挂牌，卖家=bot（显示名「系统商会」）。
    ///     玩家购买走正常 Buyout → 买家收道具邮件；SettleSoldListing 检测到
    ///     卖家是 bot → 跳过卖家金币邮件 + 触发 +3% 调价钩子。
    ///   · ±3% 动态调价（用户 2026-09-06 拍板）：每卖出一件该物品下次补货价 ×1.03，
    ///     每回收一件 ×0.97；倍率按 item_id 持久化在 auction_bot_price_adj 表
    ///     （migration v30），钳制 [price_mult_min, price_mult_max]，跨重启存活。
    ///   · bot 挂牌到期流拍：SettleReturnListing 检测到卖家是 bot → 直接标记结算，
    ///     道具销毁，不发退回邮件（bot 无角色邮箱，发了必失败刷屏）。
    ///
    /// 安全默认：enabled=0（必须显式开启）+ dry_run=1（先干跑验证）。
    /// 配置 Data/auction_bot_config.txt 每 tick 热重载（同 auction_avg_reply.txt 惯例）。
    /// </summary>
    public sealed class AuctionBotService
    {
        /// <summary>bot 的角色 id（负值，永不与真实角色冲突；characters 表无此行的 FK 由 SQLite 默认不强制）。</summary>
        public const int BotCharacterId = -1000001;

        /// <summary>bot 挂牌的卖家显示名。</summary>
        public const string BotDisplayName = "系统商会";

        private readonly AuctionRepository _repository;
        private readonly AuctionService _auction;
        private readonly object _clockSync = new object();
        private bool _clockRegistered;
        private DateTime _lastRunUtc = DateTime.MinValue;

        /// <summary>
        /// 补货扫描游标（跨 tick 递进）。清单规模从几十条扩到 2 万条后，
        /// 每 tick 若从头部扫起，清单尾部的物品永远轮不到补货（MaxRestockPerTick 总被头部吃满）。
        /// 用「滑动窗口 + 递进游标」轮转，保证所有物品都能被覆盖。
        /// </summary>
        private int _restockCursor;

        public AuctionBotService(AuctionRepository repository, AuctionService auction)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _auction = auction ?? throw new ArgumentNullException(nameof(auction));
        }

        /// <summary>注册分钟级 tick（幂等）；按 config.interval_seconds 自节流。</summary>
        public void RegisterClock(ClockService clock)
        {
            if (clock == null)
                return;

            lock (_clockSync)
            {
                if (_clockRegistered)
                    return;
                _clockRegistered = true;
                clock.RegisterMinuteTick(
                    "auction:bot",
                    utcNow =>
                    {
                        try
                        {
                            Tick(utcNow);
                        }
                        catch (Exception ex)
                        {
                            FileLogger.Log("[AuctionBot] tick failed: " + ex);
                        }
                    });
            }
        }

        /// <summary>bot 补货成交回调（由 AuctionService.BotSaleSettledHook 指向本方法）：卖出 → 补货价 ×(1+up%)。</summary>
        internal void NotifyBotSaleSettled(int itemTemplateId)
        {
            if (itemTemplateId <= 0)
                return;
            try
            {
                var config = AuctionBotConfig.Load();
                var factor = 1.0 + config.SaleUpPercent / 100.0;
                var next = _repository.ApplyBotPriceMultiplier(
                    itemTemplateId, factor, isSale: true,
                    config.PriceMultMin, config.PriceMultMax);
                FileLogger.Log(
                    $"[AuctionBot] 卖出成交 item={itemTemplateId} → 补货价上调 {config.SaleUpPercent}% " +
                    $"(倍率 → {next:F4})");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AuctionBot] sale adjust failed item={itemTemplateId}: {ex.Message}");
            }
        }

        private void Tick(DateTime utcNow)
        {
            var config = AuctionBotConfig.Load();
            if (!config.Enabled)
                return;
            if ((utcNow - _lastRunUtc).TotalSeconds < config.IntervalSeconds)
                return;
            _lastRunUtc = utcNow;

            var random = config.RandomSeed > 0 ? new Random(config.RandomSeed) : new Random();
            var recycled = RunRecycle(config, random);
            var restocked = RunRestock(config, random);

            if (recycled > 0 || restocked > 0 || config.DryRun)
            {
                FileLogger.Log(
                    $"[AuctionBot] tick 完成 recycled={recycled} restocked={restocked} " +
                    $"dryRun={config.DryRun}");
            }
        }

        // ------------------------------------------------------------------
        // 回收：按 R 值概率买走玩家低价挂牌
        // ------------------------------------------------------------------

        private int RunRecycle(AuctionBotConfig config, Random random)
        {
            var listings = _repository.LoadActiveListingsForBotScan(
                BotCharacterId, config.RecycleScanLimit);
            if (listings.Count == 0)
                return 0;

            // ★ round110：清单已扩到 2 万条，原来每条挂牌都线性扫一遍 config.Items
            //   （O(挂牌数) × O(清单数)）会成为热点，改成每 tick 建一次字典。
            var refPrices = BuildReferencePriceMap(config);

            var recycled = 0;
            foreach (var listing in listings)
            {
                if (recycled >= config.MaxRecyclePerTick)
                    break;

                var itemId = listing.Item.ItemTemplateId;
                if (AuctionRepository.IsGoldItem(itemId))
                    continue; // 金币寄售不参与回收

                var metadata = ItemMetadataResolver.Resolve(itemId);
                if (!AuctionBotPricing.IsPriceable(metadata))
                    continue;

                var enhance = ReadEnhance(listing);
                var referencePrice = refPrices.TryGetValue(itemId, out var rp) ? rp : 0;
                var pStd = AuctionBotPricing.StandardPrice(metadata, enhance, referencePrice);
                if (pStd <= 0)
                    continue;

                var r = listing.BuyoutPrice / pStd;
                var probability = AuctionBotPricing.RecycleProbability(r);
                if (probability <= 0)
                    continue;
                if (random.NextDouble() >= probability)
                    continue;

                if (config.DryRun)
                {
                    FileLogger.Log(
                        $"[AuctionBot] [dry-run] 回收 listing={listing.ListingId} item={itemId} " +
                        $"seller={listing.SellerCharacterId} 单价={listing.BuyoutPrice} " +
                        $"P_std={pStd:F0} R={r:F2} p={probability:F2}");
                    recycled++;
                    continue;
                }

                var purchased = _repository.BotPurchase(listing.ListingId, BotCharacterId, BotDisplayName);
                if (purchased == null)
                    continue; // 被玩家抢先买走/下架，正常

                // 结算：卖家收 95% 金币邮件（买家是 bot → 道具销毁不发买家邮件）。
                try
                {
                    _auction.SettleSold(listing.ListingId);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[AuctionBot] 回收结算失败 listing={listing.ListingId}: {ex.Message}（分钟 tick 会补发）");
                }

                // ★ 用户规则（2026-09-06）：每回收一件，该物品下次补货价下调。
                var factor = 1.0 - config.RecycleDownPercent / 100.0;
                var next = _repository.ApplyBotPriceMultiplier(
                    itemId, factor, isSale: false,
                    config.PriceMultMin, config.PriceMultMax);

                FileLogger.Log(
                    $"[AuctionBot] 回收 listing={listing.ListingId} item={itemId} " +
                    $"seller={listing.SellerCharacterId} 单价={listing.BuyoutPrice} " +
                    $"R={r:F2} → 补货价下调 {config.RecycleDownPercent}% (倍率 → {next:F4})");
                recycled++;
            }
            return recycled;
        }

        // ------------------------------------------------------------------
        // 补货：按目标件数上架 bot 挂牌
        // ------------------------------------------------------------------

        private int RunRestock(AuctionBotConfig config, Random random)
        {
            var total = config.Items.Count;
            if (total == 0)
                return 0;

            var restocked = 0;
            // ★ round110：清单 2 万条时不能每 tick 全量查库（每条都要 Resolve + 2 次 COUNT）。
            //   改为每 tick 只扫描一个滑动窗口，游标跨 tick 递进，轮转覆盖全清单；
            //   MaxRestockPerTick 用满时游标停在未处理项，下一 tick 从这里续上。
            var window = Math.Min(total, Math.Max(config.MaxRestockPerTick * 20, 500));
            var start = _restockCursor % total;
            var scanned = 0;
            for (; scanned < window; scanned++)
            {
                if (restocked >= config.MaxRestockPerTick)
                    break;

                var target = config.Items[(start + scanned) % total];
                var itemId = target.ItemId;
                if (AuctionRepository.IsGoldItem(itemId))
                {
                    FileLogger.Log($"[AuctionBot] 跳过金币券 item={itemId}（寄售家族不参与补货）");
                    continue;
                }

                var metadata = ItemMetadataResolver.Resolve(itemId);
                if (!AuctionBotPricing.IsPriceable(metadata))
                {
                    FileLogger.Log(
                        $"[AuctionBot] 跳过 item={itemId}（元数据不可定价: " +
                        $"kind={metadata?.ItemKind} rarity={metadata?.Rarity}）");
                    continue;
                }

                var current = _repository.CountActiveListingsByItem(BotCharacterId, itemId);
                var deficit = target.TargetCount - current;
                if (deficit <= 0)
                    continue;

                var saturation = _repository.CountActiveListingsOfItem(itemId);
                var saturationMult = AuctionBotPricing.SaturationMultiplier(saturation);
                var adjMult = _repository.GetBotPriceMultiplier(itemId);

                for (var i = 0; i < deficit && restocked < config.MaxRestockPerTick; i++)
                {
                    var enhance = AuctionBotPricing.SampleEnhanceLevel(metadata, random);
                    var pStd = AuctionBotPricing.StandardPrice(metadata, enhance, target.ReferencePrice);
                    if (pStd <= 0)
                        break;

                    var mindMult = AuctionBotPricing.SampleSellerMindMultiplier(random);
                    var price = pStd * mindMult * saturationMult * adjMult;
                    // 地板价 P_std×0.10（工具同款 min_price_ratio），天花板对齐拍卖行上限。
                    price = Math.Max(pStd * 0.10, Math.Min(AuctionPolicy.MaxPrice, price));
                    var unitPrice = (int)Math.Round(price);
                    if (unitPrice < 1)
                        continue;

                    var count = SampleStackCount(metadata, random);

                    if (config.DryRun)
                    {
                        FileLogger.Log(
                            $"[AuctionBot] [dry-run] 补货 item={itemId} +{enhance} x{count} " +
                            $"单价={unitPrice} (P_std={pStd:F0} 心智={mindMult:F2} " +
                            $"饱和={saturationMult:F2} 调价={adjMult:F4})");
                        restocked++;
                        continue;
                    }

                    var snapshot = BuildBotSnapshot(metadata, itemId, enhance, count, random);
                    var listingId = _repository.InsertBotListing(
                        BotCharacterId, BotDisplayName, snapshot, unitPrice,
                        TimeSpan.FromHours(config.ListingHours));
                    if (listingId <= 0)
                    {
                        FileLogger.Log($"[AuctionBot] 补货 INSERT 失败 item={itemId}");
                        continue;
                    }

                    FileLogger.Log(
                        $"[AuctionBot] 补货 listing={listingId} item={itemId} +{enhance} x{count} " +
                        $"单价={unitPrice} (P_std={pStd:F0} 心智={mindMult:F2} " +
                        $"饱和={saturationMult:F2} 调价={adjMult:F4})");
                    restocked++;
                }
            }
            _restockCursor = (start + scanned) % total;
            return restocked;
        }

        // ------------------------------------------------------------------
        // 内部实现
        // ------------------------------------------------------------------

        /// <summary>
        /// 按 itemId 建外部拍卖参考价索引（每 tick 一次）。
        /// 取代原来的 FindReferencePrice 线性扫描——清单扩到 2 万条后，
        /// 每个挂牌候选都 O(n) 扫一遍会拖垮 tick。同 id 重复时后者覆盖前者。
        /// </summary>
        private static Dictionary<int, int> BuildReferencePriceMap(AuctionBotConfig config)
        {
            var map = new Dictionary<int, int>(config.Items.Count);
            foreach (var target in config.Items)
            {
                if (target.ItemId <= 0)
                    continue;
                map[target.ItemId] = target.ReferencePrice > 0 ? target.ReferencePrice : 0;
            }
            return map;
        }

        /// <summary>从挂牌快照的 ItemCoreData 读强化等级（Attr 低 5 位）；无数据返回 0。</summary>
        private static int ReadEnhance(AuctionListing listing)
        {
            var core = listing?.Item?.ItemCoreData;
            if (core == null || core.Length <= ItemCore.AttrOffset)
                return 0;
            return core[ItemCore.AttrOffset] & 0x1F;
        }

        /// <summary>可堆叠道具的补货堆叠数：1 ~ min(堆叠上限, 100)。</summary>
        private static int SampleStackCount(ItemMetadata metadata, Random random)
        {
            if (metadata.ItemKind != "stackable")
                return 1;
            var cap = metadata.StackLimit > 0 ? Math.Min(metadata.StackLimit, 100) : 100;
            return random.Next(1, cap + 1);
        }

        /// <summary>
        /// 伪造 bot 挂牌的道具快照（完整 99B ItemCore：搜索网格直接读道具块渲染，
        /// 成交后邮件附件也从 ItemCoreData 重建道具，强化/数量/耐久全部保真）。
        /// </summary>
        private static AuctionItemSnapshot BuildBotSnapshot(
            ItemMetadata metadata, int itemId, int enhance, int count, Random random)
        {
            byte kind;
            if (metadata.ItemKind == "equipment")
            {
                kind = ItemCore.KindEquipment;
            }
            else if (!ItemMetadataResolver.TryResolveItemKind(itemId, out kind))
            {
                kind = ItemCore.KindConsumable;
            }

            var core = new ItemCore { ItemKind = kind, ItemId = itemId };
            if (kind == ItemCore.KindEquipment)
            {
                core.Upgrade = (byte)Math.Max(0, Math.Min(31, enhance));
                core.Durability = metadata.Durability;
                // 装备 Value 是唯一实例 UID：随机分配避免不同 bot 挂牌撞 UID。
                core.Value = random.Next(1, int.MaxValue);
            }
            else
            {
                core.Count = Math.Max(1, count);
            }

            return new AuctionItemSnapshot
            {
                ItemType = 0,
                SourceListType = 0,
                SourceSlotIndex = -1,
                ItemTemplateId = itemId,
                ItemKind = metadata.ItemKind, // "equipment" / "stackable"
                ItemCount = kind == ItemCore.KindEquipment ? 1 : Math.Max(1, count),
                InstanceValue = core.Value,
                Durability = metadata.Durability,
                SealFlag = 0,
                OptionValue = 0,
                ExpireTime = 0,
                Marker16 = ItemCore.Marker16Default,
                PetSerialOrHandle = 0,
                ExtraJson = "{}",
                ItemCoreData = core.ToBytes(),
                DetailJson = string.Empty,
            };
        }
    }
}
