using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Auction
{
    /// <summary>
    /// 拍卖行机器人定价模型（移植自台服端 auction 工具 V4.0 的策略层）。
    ///
    /// 标准价 P_std：
    ///   · 蓝/白装备（rarity 0/1）：P_std(n) = A(n) × shop_price + B(n)，n = 强化等级
    ///   · 紫/粉装备（rarity 2/3）：P_std(n) = P_base + BaseCost(n) × (1 + sqrt(P_base / 150000))
    ///   · 消耗品/材料（stackable）：P_std = NPC 价（BuyGold，缺省回退 SellGold×5 ≈ [value]）
    ///
    /// 数据源是 ItemMetadataResolver（活 PVF），不需要外部价格表。
    /// rarity ≥ 4（史诗/传说等）不定价、不回收、不补货（防经济风险）。
    /// </summary>
    public static class AuctionBotPricing
    {
        /// <summary>蓝白装 A(n), B(n) 系数（n = 强化等级，10~18）。</summary>
        private static readonly Dictionary<int, Tuple<double, double>> WhiteBlueCoeff =
            new Dictionary<int, Tuple<double, double>>
            {
                { 10, Tuple.Create(75.76, 127272.0) },
                { 11, Tuple.Create(111.41, 216577.0) },
                { 12, Tuple.Create(227.27, 381819.0) },
                { 13, Tuple.Create(445.63, 866311.0) },
                { 14, Tuple.Create(713.01, 1786097.0) },
                { 15, Tuple.Create(1693.40, 5491980.0) },
                { 16, Tuple.Create(4634.58, 6609626.0) },
                { 17, Tuple.Create(9803.92, 7058824.0) },
                { 18, Tuple.Create(19607.84, 14117648.0) },
            };

        /// <summary>紫粉装 BaseCost(n)（n = 强化等级，10~18）。</summary>
        private static readonly Dictionary<int, int> PurplePinkBaseCost = new Dictionary<int, int>
        {
            { 10, 150000 }, { 11, 250000 }, { 12, 450000 }, { 13, 1000000 },
            { 14, 2000000 }, { 15, 6000000 }, { 16, 8000000 }, { 17, 10000000 },
            { 18, 20000000 },
        };

        private const double PurplePinkScale = 150000.0;

        /// <summary>
        /// 该物品是否参与 bot 定价（元数据可解析 + rarity 在允许区间）。
        /// ★ round114：上限由 3 放宽到 4。rarity=4 在 86 版本里是「英雄袖珍罐」这一档
        ///   可交易罐子（8095~8104 / 10003102~10003109 / 10007249~51），玩家清单里明确
        ///   配了 50 万~100 万的参考价，卡在 3 会被整档静默跳过。
        ///   再往上 rarity=5（太阳神之臂章、抗魔石，2 件）与 rarity=6（故事簿等 518 件）
        ///   仍然不放行 —— 这些没有可靠锚定价，按商店价挂会离谱。
        /// </summary>
        private const int MaxPriceableRarity = 4;

        public static bool IsPriceable(ItemMetadata metadata)
        {
            if (metadata == null)
                return false;
            if (metadata.ItemKind != "equipment" && metadata.ItemKind != "stackable")
                return false;
            return metadata.Rarity >= 0 && metadata.Rarity <= MaxPriceableRarity;
        }

        /// <summary>
        /// 计算标准价 P_std。enhance 仅对装备有意义（材料/消耗品传 0）。
        /// referencePrice 是可选外部拍卖行参考单价，目前仅用于 stackable；
        /// 传入后覆盖 NPC 锚定价，使补货/回收都以国服 86 拍卖参考价为基准。
        /// 返回 0 表示无法定价（调用方应跳过）。
        /// </summary>
        public static double StandardPrice(ItemMetadata metadata, int enhance, int referencePrice = 0)
        {
            if (!IsPriceable(metadata))
                return 0;

            if (metadata.ItemKind == "stackable")
            {
                // 有外部拍卖参考价时优先使用；否则 NPC 购买价优先，
                // [price] 缺省时 [value]（≈ SellGold×5）做锚。
                var anchor = referencePrice > 0
                    ? referencePrice
                    : (metadata.BuyGold > 0 ? metadata.BuyGold : (long)metadata.SellGold * 5);
                return Math.Max(1, anchor);
            }

            var shopPrice = metadata.BuyGold > 0 ? metadata.BuyGold : Math.Max(1, metadata.SellGold * 20);
            if (metadata.Rarity <= 1)
            {
                // 蓝白：低强化直接取商店价，≥10 强化套 A(n)x+B(n)。
                if (enhance < 10)
                    return Math.Max(1, shopPrice);
                var n = Math.Min(18, enhance);
                var coeff = WhiteBlueCoeff[n];
                return coeff.Item1 * shopPrice + coeff.Item2;
            }

            // 紫粉：P_base + BaseCost(n) × (1 + sqrt(P_base / 150000))；低强化只有 P_base。
            var pBase = Math.Max(1, shopPrice);
            if (enhance < 10)
                return pBase;
            var baseCost = PurplePinkBaseCost[Math.Min(18, enhance)];
            return pBase + baseCost * (1.0 + Math.Sqrt(pBase / PurplePinkScale));
        }

        /// <summary>
        /// 回收购买概率（R = 玩家单价 / P_std，移植台服工具五段表）：
        ///   R &lt;= 0.5        → 100%
        ///   0.5 &lt; R &lt; 0.9   → 1 - 0.7 × (R - 0.5) / 0.4
        ///   0.9 &lt;= R &lt;= 1.1 → 50%
        ///   1.1 &lt; R &lt;= 1.5 → 50% × (1 - (R - 1.1) / 0.4)
        ///   R &gt; 1.5         → 0%
        /// </summary>
        public static double RecycleProbability(double r)
        {
            if (r <= 0)
                return 0;
            if (r <= 0.5)
                return 1.0;
            if (r < 0.9)
                return 1.0 - 0.7 * (r - 0.5) / 0.4;
            if (r <= 1.1)
                return 0.5;
            if (r <= 1.5)
                return 0.5 * (1.0 - (r - 1.1) / 0.4);
            return 0;
        }

        /// <summary>卖家心智：名称 / 概率 / 倍率区间（移植台服工具 3.1 节）。</summary>
        private static readonly Tuple<double, double, double>[] SellerMind =
        {
            // prob, multMin, multMax
            Tuple.Create(0.20, 0.50, 0.80),  // 急需
            Tuple.Create(0.60, 0.90, 1.10),  // 普通
            Tuple.Create(0.15, 1.10, 1.30),  // 奸商
            Tuple.Create(0.05, 1.30, 1.50),  // 小白
        };

        /// <summary>按卖家心智分布随机一个定价倍率。</summary>
        public static double SampleSellerMindMultiplier(Random random)
        {
            var roll = random.NextDouble();
            var cumulative = 0.0;
            foreach (var entry in SellerMind)
            {
                cumulative += entry.Item1;
                if (roll < cumulative)
                    return entry.Item2 + random.NextDouble() * (entry.Item3 - entry.Item2);
            }
            return 1.0;
        }

        /// <summary>
        /// 饱和度调价（移植台服工具 3.2 节）：在售 &lt; 5 → +10%，&gt; 20 → -10%。
        /// 返回乘性系数（1.10 / 1.00 / 0.90）。
        /// </summary>
        public static double SaturationMultiplier(int activeCount)
        {
            if (activeCount < 5)
                return 1.10;
            if (activeCount > 20)
                return 0.90;
            return 1.0;
        }

        /// <summary>强化等级分布（按稀有度，移植台服工具 5.3 节；0=白 1=蓝 2=紫 3=粉）。</summary>
        private static readonly Dictionary<int, Tuple<int, double>[]> EnhanceDist =
            new Dictionary<int, Tuple<int, double>[]>
            {
                {
                    0, new[] // 白
                    {
                        Tuple.Create(12, 0.638), Tuple.Create(13, 0.197), Tuple.Create(14, 0.098),
                        Tuple.Create(15, 0.050), Tuple.Create(16, 0.010), Tuple.Create(17, 0.005),
                        Tuple.Create(18, 0.002),
                    }
                },
                {
                    1, new[] // 蓝
                    {
                        Tuple.Create(11, 0.577), Tuple.Create(12, 0.240), Tuple.Create(13, 0.096),
                        Tuple.Create(14, 0.039), Tuple.Create(15, 0.040), Tuple.Create(16, 0.005),
                        Tuple.Create(17, 0.002), Tuple.Create(18, 0.001),
                    }
                },
                {
                    2, new[] // 紫
                    {
                        Tuple.Create(0, 0.41), Tuple.Create(10, 0.26), Tuple.Create(11, 0.13),
                        Tuple.Create(12, 0.08), Tuple.Create(13, 0.05), Tuple.Create(14, 0.04),
                        Tuple.Create(15, 0.03),
                    }
                },
                {
                    3, new[] // 粉
                    {
                        Tuple.Create(0, 0.45), Tuple.Create(10, 0.23), Tuple.Create(11, 0.12),
                        Tuple.Create(12, 0.08), Tuple.Create(13, 0.05), Tuple.Create(14, 0.04),
                        Tuple.Create(15, 0.03),
                    }
                },
            };

        /// <summary>按稀有度分布随机一个强化等级（材料/消耗品返回 0）。</summary>
        public static int SampleEnhanceLevel(ItemMetadata metadata, Random random)
        {
            if (metadata == null || metadata.ItemKind != "equipment")
                return 0;
            if (!EnhanceDist.TryGetValue(metadata.Rarity, out var dist))
                return 0;

            var roll = random.NextDouble();
            var cumulative = 0.0;
            foreach (var entry in dist)
            {
                cumulative += entry.Item2;
                if (roll < cumulative)
                    return entry.Item1;
            }
            return dist[0].Item1;
        }
    }
}
