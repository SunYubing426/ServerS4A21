using System;
using System.Collections.Generic;

namespace DfoServer.Game.Events.LoginReward
{
    internal sealed class LoginRewardDay
    {
        public int DayIndex { get; set; }

        public DateTime Deadline { get; set; }

        public int ItemId { get; set; }

        public int ItemCount { get; set; }
    }

    internal sealed class LoginRewardTrack
    {
        public int TrackIndex { get; set; }

        public IReadOnlyList<LoginRewardDay> Days { get; set; }
            = Array.Empty<LoginRewardDay>();
    }

    internal sealed class LoginRewardConfig
    {
        internal const int EventId = 2380;
        internal const int DefaultSeasonId = 1;
        internal const string PvfPath =
            "event/chn_event/chn_login_reward.evt";

        public int SeasonId { get; set; } = DefaultSeasonId;

        public IReadOnlyList<LoginRewardTrack> Tracks { get; set; }
            = Array.Empty<LoginRewardTrack>();

        internal LoginRewardDay FindRewardDay(DateTime date)
        {
            if (Tracks.Count == 0)
                return null;

            var track = Tracks[0];
            foreach (var day in track.Days)
            {
                if (day.Deadline.Date == date.Date)
                    return day;
            }

            return null;
        }
    }

    internal sealed class LoginRewardSnapshot
    {
        public int AccountId { get; set; }

        public int CharacterId { get; set; }

        public int EventId { get; set; }

        public int SeasonId { get; set; }

        public int DayId { get; set; }

        public int TodayDayIndex { get; set; } = -1;

        public int ClaimedMask { get; set; }

        public bool AlreadyClaimedToday { get; set; }

        public bool EventEnabled { get; set; }
    }

    internal enum LoginRewardClaimStatus
    {
        Claimed,
        EventClosed,
        CharacterUnavailable,
        NoRewardToday,
        AlreadyClaimed,
        MailFailed,
    }

    internal sealed class LoginRewardClaimResult
    {
        public LoginRewardClaimStatus Status { get; set; }

        public LoginRewardSnapshot Snapshot { get; set; }

        public bool MailDelivered { get; set; }

        public int ItemId { get; set; }

        public int ItemCount { get; set; }

        public bool Success => Status == LoginRewardClaimStatus.Claimed;
    }
}
