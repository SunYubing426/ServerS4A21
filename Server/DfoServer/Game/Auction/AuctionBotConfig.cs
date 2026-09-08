using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.Game.Auction
{
    /// <summary>拍卖行机器人单条补货目标（auction_bot_items.txt 一行）。</summary>
    public sealed class AuctionBotItemTarget
    {
        public int ItemId { get; set; }
        /// <summary>该物品维持在架的 bot 挂牌件数目标。</summary>
        public int TargetCount { get; set; }
        /// <summary>外部拍卖行参考单价（金币/个）；0 = 继续使用 PVF/NPC 锚定价。</summary>
        public int ReferencePrice { get; set; }
    }

    /// <summary>
    /// 拍卖行机器人配置（Data/auction_bot_config.txt + Data/auction_bot_items.txt）。
    /// 与 auction_avg_reply.txt 同一惯例：key=value、# 注释、每个 tick 重新读盘（热更新）。
    /// </summary>
    public sealed class AuctionBotConfig
    {
        /// <summary>总开关。默认 false，必须显式 enabled=1 才会跑（安全第一）。</summary>
        public bool Enabled { get; set; }

        /// <summary>干跑模式：只输出计划日志，不写任何数据库。默认 true（安全第一）。</summary>
        public bool DryRun { get; set; } = true;

        /// <summary>tick 间隔秒数（挂在分钟 tick 上自节流）。</summary>
        public int IntervalSeconds { get; set; } = 600;

        /// <summary>每 tick 回收扫描的玩家挂牌上限。</summary>
        public int RecycleScanLimit { get; set; } = 200;

        /// <summary>每 tick 最多回收件数。</summary>
        public int MaxRecyclePerTick { get; set; } = 20;

        /// <summary>每 tick 最多补货件数。</summary>
        public int MaxRestockPerTick { get; set; } = 50;

        /// <summary>bot 挂牌时长（小时）。</summary>
        public int ListingHours { get; set; } = 48;

        /// <summary>卖出一件后该物品下次补货价上调百分比（默认 3 = +3%）。</summary>
        public double SaleUpPercent { get; set; } = 3.0;

        /// <summary>回收一件后该物品下次补货价下调百分比（默认 3 = -3%）。</summary>
        public double RecycleDownPercent { get; set; } = 3.0;

        /// <summary>±3% 累计倍率下限（防无限贬值）。</summary>
        public double PriceMultMin { get; set; } = 0.5;

        /// <summary>±3% 累计倍率上限（防无限通胀）。</summary>
        public double PriceMultMax { get; set; } = 2.0;

        /// <summary>固定随机种子（0=系统时间随机；>0 可复现测试）。</summary>
        public int RandomSeed { get; set; }

        /// <summary>补货目标清单（auction_bot_items.txt）。</summary>
        public List<AuctionBotItemTarget> Items { get; } = new List<AuctionBotItemTarget>();

        public static string ConfigPath =>
            Path.Combine(AppContext.BaseDirectory, "Data", "auction_bot_config.txt");

        public static string ItemsPath =>
            Path.Combine(AppContext.BaseDirectory, "Data", "auction_bot_items.txt");

        /// <summary>每 tick 调用，热更新。文件不存在时返回「禁用 + 干跑」的安全默认。</summary>
        public static AuctionBotConfig Load()
        {
            var config = new AuctionBotConfig();
            LoadMainConfig(config);
            LoadItems(config);
            return config;
        }

        private static void LoadMainConfig(AuctionBotConfig config)
        {
            try
            {
                if (!File.Exists(ConfigPath))
                    return;

                foreach (var raw in File.ReadAllText(ConfigPath).Split('\n'))
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
                    var v = kv[1].Trim();

                    switch (k)
                    {
                        case "enabled":
                            config.Enabled = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "dry_run":
                            config.DryRun = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "interval_seconds":
                            // ★ round114：下限原为 60，但分钟节拍实际落在整分后 0~1.5s，
                            //   配 60 会因为「59.x < 60」被跳过、退化成 2 分钟一跳；
                            //   且任何 <60 的值会被静默忽略、回落到默认 600 秒（10 分钟一跳）。
                            //   下限放宽到 5 秒，让 30 这类小于节拍间隔的值可用（等效每分一跳）。
                            if (int.TryParse(v, out var interval) && interval >= 5)
                                config.IntervalSeconds = interval;
                            break;
                        case "recycle_scan_limit":
                            if (int.TryParse(v, out var scanLimit) && scanLimit > 0)
                                config.RecycleScanLimit = scanLimit;
                            break;
                        case "max_recycle_per_tick":
                            if (int.TryParse(v, out var maxRecycle) && maxRecycle > 0)
                                config.MaxRecyclePerTick = maxRecycle;
                            break;
                        case "max_restock_per_tick":
                            if (int.TryParse(v, out var maxRestock) && maxRestock > 0)
                                config.MaxRestockPerTick = maxRestock;
                            break;
                        case "listing_hours":
                            if (int.TryParse(v, out var hours) && hours > 0 && hours <= 24 * 30)
                                config.ListingHours = hours;
                            break;
                        case "sale_up_percent":
                            if (double.TryParse(v, out var up) && up >= 0 && up <= 100)
                                config.SaleUpPercent = up;
                            break;
                        case "recycle_down_percent":
                            if (double.TryParse(v, out var down) && down >= 0 && down <= 100)
                                config.RecycleDownPercent = down;
                            break;
                        case "price_mult_min":
                            if (double.TryParse(v, out var multMin) && multMin > 0 && multMin <= 1)
                                config.PriceMultMin = multMin;
                            break;
                        case "price_mult_max":
                            if (double.TryParse(v, out var multMax) && multMax >= 1 && multMax <= 100)
                                config.PriceMultMax = multMax;
                            break;
                        case "random_seed":
                            if (int.TryParse(v, out var seed) && seed >= 0)
                                config.RandomSeed = seed;
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("[AuctionBot] config load failed: " + ex.Message);
            }
        }

        private static void LoadItems(AuctionBotConfig config)
        {
            try
            {
                if (!File.Exists(ItemsPath))
                    return;

                foreach (var raw in File.ReadAllText(ItemsPath).Split('\n'))
                {
                    var line = raw.Trim();
                    var hash = line.IndexOf('#');
                    if (hash >= 0)
                        line = line.Substring(0, hash).Trim();
                    if (line.Length == 0)
                        continue;

                    // 格式：itemId, targetCount[, referencePrice]
                    // targetCount    = 维持在架的 bot 挂牌件数
                    // referencePrice = 可选，外部拍卖行参考单价；省略/0 时使用 PVF/NPC 锚定价
                    var parts = line.Split(',');
                    if (parts.Length < 2)
                        continue;
                    if (!int.TryParse(parts[0].Trim(), out var itemId) || itemId <= 0)
                        continue;
                    if (!int.TryParse(parts[1].Trim(), out var target) || target <= 0)
                        continue;
                    var referencePrice = 0;
                    if (parts.Length >= 3)
                        int.TryParse(parts[2].Trim(), out referencePrice);
                    if (referencePrice < 0)
                        referencePrice = 0;

                    config.Items.Add(new AuctionBotItemTarget
                    {
                        ItemId = itemId,
                        TargetCount = target,
                        ReferencePrice = referencePrice,
                    });
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("[AuctionBot] items load failed: " + ex.Message);
            }
        }
    }
}
