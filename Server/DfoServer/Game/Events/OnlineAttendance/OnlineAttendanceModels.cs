using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Events.OnlineAttendance
{
    internal sealed class OnlineAttendanceRewardStage
    {
        public int StageIndex { get; set; }

        public int ItemId { get; set; }

        public int ItemCount { get; set; }

        public long RequiredSeconds { get; set; }
    }

    internal sealed class OnlineAttendanceConfig
    {
        internal const int EventId = 2366;
        internal const int DefaultSeasonId = 1;
        internal const string PvfPath =
            "event/chn_event/chn_attendanceevent.evt";

        public int SeasonId { get; set; } = DefaultSeasonId;

        public IReadOnlyList<OnlineAttendanceRewardStage> TimeRewards { get; set; }
            = Array.Empty<OnlineAttendanceRewardStage>();

        public IReadOnlyList<OnlineAttendanceRewardStage> SumRewards { get; set; }
            = Array.Empty<OnlineAttendanceRewardStage>();

        public long TotalRequiredSeconds =>
            TimeRewards.Count == 0
                ? 0
                : Math.Max(0, TimeRewards[TimeRewards.Count - 1].RequiredSeconds);

        internal OnlineAttendanceRewardStage GetTimeReward(int stageIndex)
            => TimeRewards.FirstOrDefault(s => s.StageIndex == stageIndex);

        internal OnlineAttendanceRewardStage GetSumReward(int stageIndex)
            => SumRewards.FirstOrDefault(s => s.StageIndex == stageIndex);
    }

    internal sealed class OnlineAttendanceSnapshot
    {
        public int AccountId { get; set; }

        public int CharacterId { get; set; }

        public int EventId { get; set; }

        public int SeasonId { get; set; }

        public int DayId { get; set; }

        public long DailyOnlineSeconds { get; set; }

        public int DailyClaimMask { get; set; }

        public int SumClaimMask { get; set; }

        public int SumCompletedCount { get; set; }

        public bool EventEnabled { get; set; }
    }

    internal enum OnlineAttendanceClaimStatus
    {
        Success,
        EventClosed,
        CharacterUnavailable,
        NotReady,
        AlreadyClaimed,
        InvalidStage,
        MailFailed,
    }

    internal sealed class OnlineAttendanceClaimResult
    {
        public OnlineAttendanceClaimStatus Status { get; set; }

        public OnlineAttendanceSnapshot Snapshot { get; set; }

        public bool MailDelivered { get; set; }

        public int ClaimedStageIndex { get; set; } = -1;

        public int ItemId { get; set; }

        public int ItemCount { get; set; }

        public bool Success => Status == OnlineAttendanceClaimStatus.Success;
    }
}
