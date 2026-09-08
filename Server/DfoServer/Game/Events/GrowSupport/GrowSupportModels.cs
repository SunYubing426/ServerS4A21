using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Events.GrowSupport
{
    internal sealed class GrowSupportLevelReward
    {
        public int Level { get; set; }

        public int ItemId { get; set; }

        public int ItemCount { get; set; }
    }

    internal sealed class GrowSupportDungeonReward
    {
        public int RequiredClears { get; set; }

        public int ItemId { get; set; }

        public int ItemCount { get; set; }
    }

    internal sealed class GrowSupportConfig
    {
        internal const int EventId = 2381;
        internal const int DefaultSeasonId = 1;
        internal const string PvfPath =
            "event/chn_event/chn_growsupport.evt";

        public int SeasonId { get; set; } = DefaultSeasonId;

        public DateTime CharacterStartTime { get; set; }

        public IReadOnlyList<GrowSupportLevelReward> LevelRewards { get; set; }
            = Array.Empty<GrowSupportLevelReward>();

        public IReadOnlyList<GrowSupportDungeonReward> DungeonRewards { get; set; }
            = Array.Empty<GrowSupportDungeonReward>();

        public int PostalTitleId { get; set; }

        public int PostalTextId { get; set; }

        public int PostalRetentionDays { get; set; }

        internal GrowSupportLevelReward FindLevelReward(int level)
            => LevelRewards.FirstOrDefault(r => r.Level == level);

        internal GrowSupportDungeonReward FindDungeonReward(int clearCount)
            => DungeonRewards.FirstOrDefault(r => r.RequiredClears == clearCount);
    }

    internal sealed class GrowSupportSnapshot
    {
        public int AccountId { get; set; }

        public int CharacterId { get; set; }

        public int EventId { get; set; }

        public int SeasonId { get; set; }

        public int CharacterLevel { get; set; }

        public int LevelRewardClaimMask { get; set; }

        public int DungeonClearCount { get; set; }

        public int DungeonRewardClaimMask { get; set; }

        public bool EventEnabled { get; set; }
    }

    internal enum GrowSupportClaimStatus
    {
        Claimed,
        EventClosed,
        CharacterUnavailable,
        NotReady,
        AlreadyClaimed,
        InvalidRequest,
        MailFailed,
    }

    internal sealed class GrowSupportClaimResult
    {
        public GrowSupportClaimStatus Status { get; set; }

        public GrowSupportSnapshot Snapshot { get; set; }

        public bool MailDelivered { get; set; }

        public int ItemId { get; set; }

        public int ItemCount { get; set; }

        public bool Success => Status == GrowSupportClaimStatus.Claimed;
    }
}
