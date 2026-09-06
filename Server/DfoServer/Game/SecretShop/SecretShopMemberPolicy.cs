using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Game.SecretShop
{
    /// <summary>
    /// 本服黑钻会员对加百利秘密商店的增强口径。
    /// 不是官方精确概率还原：当前 PVF 没有独立会员出现倍率或会员 cash 归属证据。
    /// </summary>
    internal readonly struct SecretShopMemberPolicy
    {
        internal static SecretShopMemberPolicy None { get; } = new(false);
        internal static SecretShopMemberPolicy BlackDiamond { get; } = new(true);

        internal static SecretShopMemberPolicy FromBlackDiamond(bool member)
            => member ? BlackDiamond : None;

        private SecretShopMemberPolicy(bool isMember)
        {
            IsMember = isMember;
        }

        internal bool IsMember { get; }
        internal bool ExtraEncounterRoll => IsMember;
        internal bool DeduplicateItemIds => IsMember;
        internal bool BoostRareArtifactWeights => IsMember;
        internal bool DoubleSelectionCount => IsMember;
        internal bool DoubleItemCount => IsMember;

        internal int ResolveSelectionCount(int pvfSelectionCount, int uniquePositiveCandidateCount)
        {
            if (pvfSelectionCount <= 0)
                return 0;
            if (!DoubleSelectionCount)
                return pvfSelectionCount;

            var target = checked(pvfSelectionCount * 2);
            var unique = uniquePositiveCandidateCount < 0 ? 0 : uniquePositiveCandidateCount;
            return Math.Min(target, unique);
        }

        internal int ResolveOfferCount(int pvfCount)
        {
            if (!DoubleItemCount || pvfCount <= 0)
                return pvfCount;
            return checked(pvfCount * 2);
        }

        internal int ResolveEffectiveWeight(SecretShopItemCandidate candidate)
        {
            if (candidate == null || candidate.Weight <= 0)
                return 0;
            if (!BoostRareArtifactWeights)
                return candidate.Weight;
            if (!SecretShopEquipmentRarity.TryGet(candidate.ItemId, out var rarity)
                || rarity is not (2 or 3))
            {
                return candidate.Weight;
            }

            return checked(candidate.Weight * 2);
        }

        internal SecretShopItemCandidate MaterializeOfferCandidate(SecretShopItemCandidate source)
        {
            if (source == null)
                return null;
            var count = ResolveOfferCount(source.Count);
            if (count == source.Count && !IsMember)
                return source;

            return new SecretShopItemCandidate
            {
                ItemId = source.ItemId,
                RawFlag = source.RawFlag,
                Price = source.Price,
                RequiredItemId = source.RequiredItemId,
                Count = count,
                Weight = source.Weight,
            };
        }
    }

    internal static class SecretShopEquipmentRarity
    {
        internal static bool TryGet(int itemId, out int rarity)
        {
            rarity = 0;
            if (itemId <= 0)
                return false;

            var metadata = ItemMetadataResolver.Resolve(itemId);
            if (metadata == null
                || !string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal))
            {
                return false;
            }

            rarity = metadata.Rarity;
            return true;
        }
    }
}
