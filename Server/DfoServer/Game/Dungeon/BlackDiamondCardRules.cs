using System;
using DfoServer.Game.Premium;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Game.Dungeon
{
    // Frozen extra free-card prize for one participant run.
    // Native NPK images map CLEAR kind2 (not kind1) to the black-diamond
    // card; kind1 is GOLD and stays empty. 25F970F/25F9751 bind the decoded
    // pcroompremiumreward.img to obj+50. Frozen=false means no roll (old JSON / no card
    // flow). A miss is Frozen=true with no item so CLEAR/select cannot reroll.
    // Coin chance and pcroom-default item generation are separate rolls.
    internal readonly struct BlackDiamondCardReward
    {
        internal BlackDiamondCardReward(bool frozen, int itemId, int stackCount)
        {
            Frozen = frozen;
            ItemId = itemId;
            StackCount = stackCount;
        }

        internal bool Frozen { get; }
        internal int ItemId { get; }
        internal int StackCount { get; }
        internal bool HasItem => Frozen && ItemId > 0 && StackCount > 0;

        internal static BlackDiamondCardReward Miss { get; } =
            new BlackDiamondCardReward(true, 0, 0);

        internal static BlackDiamondCardReward Hit(int itemId, int stackCount)
        {
            if (itemId <= 0 || stackCount <= 0)
                return Miss;
            return new BlackDiamondCardReward(true, itemId, stackCount);
        }
    }

    internal static class BlackDiamondCardRules
    {
        // Server-chosen policy. Not an official rate and not PVF 50's denominator.
        internal const int CardCoinChancePercent = 5;

        // Current CLEAR builder remaps this player's free cards onto seat 0
        // and writes dummy seats 1-7. Extra kind2 follows that local seat.
        internal const int LocalSeatIndex = 0;

        internal static bool AllowsPlay(
            int dungeonId,
            DungeonClearPresentationKind presentationKind)
        {
            if (presentationKind != DungeonClearPresentationKind.Standard)
                return false;
            if (dungeonId <= 0)
                return false;
            if (LicensedDungeonCatalog.TryGetDefinition(dungeonId, out _))
                return false;
            if (DungeonData.TryGetTowerOfDespairFloor(dungeonId, out _))
                return false;
            if (DungeonData.IsDimensionDungeon(dungeonId))
                return false;
            return true;
        }

        internal static bool HasEligiblePremium(
            string connectionString,
            int accountId)
        {
            if (accountId <= 0 || string.IsNullOrWhiteSpace(connectionString))
                return false;
            try
            {
                return PremiumService.HasActiveBlackDiamond(
                    connectionString,
                    accountId);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[BlackDiamondCard] premium lookup failed closed: " +
                    ex.Message);
                return false;
            }
        }

        internal static bool IsEligible(
            int dungeonId,
            DungeonClearPresentationKind presentationKind,
            int accountId,
            string connectionString)
            => AllowsPlay(dungeonId, presentationKind)
               && HasEligiblePremium(connectionString, accountId);

        internal static BlackDiamondCardReward Roll(
            bool eligible,
            Func<int, int> nextExclusive,
            ClearRewardDefinition definition)
            => Roll(
                eligible,
                nextExclusive,
                default,
                lcg: null,
                definition);

        internal static BlackDiamondCardReward Roll(
            bool eligible,
            Func<int, int> nextExclusive,
            ClearRewardGenerationContext context,
            DnfLcg lcg,
            ClearRewardDefinition definition = null)
        {
            if (!eligible)
                return BlackDiamondCardReward.Miss;
            if (nextExclusive == null)
            {
                FileLogger.Log(
                    "[BlackDiamondCard] missing random source; extra card disabled");
                return BlackDiamondCardReward.Miss;
            }

            int chanceRoll;
            try
            {
                chanceRoll = nextExclusive(100);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[BlackDiamondCard] chance roll failed closed: " +
                    ex.Message);
                return BlackDiamondCardReward.Miss;
            }

            if (chanceRoll < 0 || chanceRoll >= 100)
            {
                FileLogger.Log(
                    "[BlackDiamondCard] chance roll out of range; extra card disabled");
                return BlackDiamondCardReward.Miss;
            }

            if (chanceRoll < CardCoinChancePercent)
                return RollCoin(nextExclusive, definition);

            return RollPcRoomDefaultItem(context, lcg, definition);
        }

        private static BlackDiamondCardReward RollCoin(
            Func<int, int> nextExclusive,
            ClearRewardDefinition definition)
        {
            if (!ClearRewardGenerator.TryPickPcRoomCardBlankItem(
                    definition ?? ClearRewardDefinitionCatalog.Current,
                    nextExclusive,
                    out var reward)
                || reward.ItemId <= 0
                || reward.StackCount <= 0)
            {
                FileLogger.Log(
                    "[BlackDiamondCard] blank pool produced no item after hit");
                return BlackDiamondCardReward.Miss;
            }

            FileLogger.Log(
                "[BlackDiamondCard] coin hit item=" +
                reward.ItemId +
                " count=" +
                reward.StackCount);
            return BlackDiamondCardReward.Hit(reward.ItemId, reward.StackCount);
        }

        private static BlackDiamondCardReward RollPcRoomDefaultItem(
            ClearRewardGenerationContext context,
            DnfLcg lcg,
            ClearRewardDefinition definition)
        {
            if (lcg == null)
            {
                FileLogger.Log(
                    "[BlackDiamondCard] missing item random source; extra item disabled");
                return BlackDiamondCardReward.Miss;
            }

            var generated = ClearRewardGenerator.GenerateBlackDiamondItemCard(
                context,
                lcg,
                definition);
            if (generated.ItemId <= 0 || generated.StackCount <= 0)
            {
                FileLogger.Log(
                    "[BlackDiamondCard] pcroom default produced no extra item");
                return BlackDiamondCardReward.Miss;
            }

            FileLogger.Log(
                "[BlackDiamondCard] extra item=" +
                generated.ItemId +
                " count=" +
                generated.StackCount);
            return BlackDiamondCardReward.Hit(
                generated.ItemId,
                generated.StackCount);
        }
    }
}
