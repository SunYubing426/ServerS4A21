using System;
using System.Collections.Generic;

namespace DfoServer.Game.Events.BurningTime
{
    internal sealed class BurningTimePhase
    {
        public int PhaseIndex { get; set; }

        public IReadOnlyList<int> Values { get; set; }
            = Array.Empty<int>();
    }

    internal sealed class BurningTimeConfig
    {
        internal const int EventId = 2382;
        internal const int DefaultSeasonId = 1;
        internal const string PvfPath =
            "event/chn_event/chn_burningtimebuff.evt";

        public int SeasonId { get; set; } = DefaultSeasonId;

        public IReadOnlyList<BurningTimePhase> Phases { get; set; }
            = Array.Empty<BurningTimePhase>();
    }

    internal sealed class BurningTimeSnapshot
    {
        public int EventId { get; set; }

        public int SeasonId { get; set; }

        public bool EventEnabled { get; set; }

        public int ActivePhaseIndex { get; set; }
    }
}
