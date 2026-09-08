using System;

namespace DfoServer.Network.Parsers.Events
{
    internal enum OnlineAttendanceRequestKind
    {
        Query,
        TimeReward,
        SumReward,
    }

    internal sealed class OnlineAttendanceRequest
    {
        public OnlineAttendanceRequestKind Kind { get; set; }

        public int StageIndex { get; set; }
    }

    internal static class OnlineAttendanceRequestParser
    {
        internal static bool TryParse(byte[] body, out OnlineAttendanceRequest request)
        {
            request = null;
            if (body == null || body.Length == 0)
            {
                request = new OnlineAttendanceRequest
                {
                    Kind = OnlineAttendanceRequestKind.Query,
                };
                return true;
            }

            try
            {
                request = new OnlineAttendanceRequest
                {
                    Kind = (OnlineAttendanceRequestKind)Math.Max(
                        0,
                        (int)body[0]),
                    StageIndex = body.Length > 1 ? body[1] : 0,
                };
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
