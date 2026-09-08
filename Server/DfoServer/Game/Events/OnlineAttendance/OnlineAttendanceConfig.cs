using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Events.OnlineAttendance
{
    internal sealed class OnlineAttendanceConfigProvider
    {
        private static readonly Lazy<OnlineAttendanceConfig> SharedConfig =
            new Lazy<OnlineAttendanceConfig>(LoadShared);

        internal static OnlineAttendanceConfigProvider Instance { get; } =
            new OnlineAttendanceConfigProvider();

        private OnlineAttendanceConfigProvider()
        {
        }

        internal OnlineAttendanceConfig Current => SharedConfig.Value;

        internal void Warmup()
        {
            _ = Current;
        }

        private static OnlineAttendanceConfig LoadShared()
        {
            try
            {
                var loaded = OnlineAttendanceConfigParser.Parse(
                    PvfArchiveAccessor.ReadText(OnlineAttendanceConfig.PvfPath));
                FileLogger.Log(
                    "[OnlineAttendance] loaded "
                    + $"{OnlineAttendanceConfig.PvfPath} "
                    + $"time={loaded.TimeRewards.Count} "
                    + $"sum={loaded.SumRewards.Count}");
                return loaded;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[OnlineAttendance] failed to load PVF config, "
                    + "using fallback: " + ex.Message);
                return OnlineAttendanceConfigParser.CreateFallback();
            }
        }
    }

    internal static class OnlineAttendanceConfigParser
    {
        internal static OnlineAttendanceConfig Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException(
                    "chn_attendanceevent.evt is empty.",
                    nameof(text));
            }

            var timeSeconds = ReadNumericValues(
                ReadClosedBlock(text, "process time seconds"));
            var timeItems = ReadNumericValues(
                ReadClosedBlock(text, "reward item"));
            var sumCounts = ReadNumericValues(
                ReadClosedBlock(text, "process seconds for max count"));
            var sumItems = ReadNumericValues(
                ReadClosedBlock(text, "reward item for sum"));

            var config = new OnlineAttendanceConfig
            {
                TimeRewards = PairTimeRewards(timeSeconds, timeItems),
                SumRewards = PairSumRewards(sumCounts, sumItems),
            };
            Validate(config);
            return config;
        }

        internal static OnlineAttendanceConfig CreateFallback()
        {
            return new OnlineAttendanceConfig
            {
                TimeRewards = new[]
                {
                    new OnlineAttendanceRewardStage
                    {
                        StageIndex = 1, RequiredSeconds = 1800,
                        ItemId = 490000339, ItemCount = 1,
                    },
                    new OnlineAttendanceRewardStage
                    {
                        StageIndex = 2, RequiredSeconds = 3600,
                        ItemId = 490000340, ItemCount = 1,
                    },
                    new OnlineAttendanceRewardStage
                    {
                        StageIndex = 3, RequiredSeconds = 7200,
                        ItemId = 490000341, ItemCount = 1,
                    },
                    new OnlineAttendanceRewardStage
                    {
                        StageIndex = 4, RequiredSeconds = 10800,
                        ItemId = 490000342, ItemCount = 1,
                    },
                },
                SumRewards = new[]
                {
                    new OnlineAttendanceRewardStage
                    {
                        StageIndex = 1, RequiredSeconds = 5,
                        ItemId = 490000343, ItemCount = 1,
                    },
                    new OnlineAttendanceRewardStage
                    {
                        StageIndex = 2, RequiredSeconds = 10,
                        ItemId = 490000344, ItemCount = 1,
                    },
                    new OnlineAttendanceRewardStage
                    {
                        StageIndex = 3, RequiredSeconds = 15,
                        ItemId = 490000345, ItemCount = 1,
                    },
                    new OnlineAttendanceRewardStage
                    {
                        StageIndex = 4, RequiredSeconds = 20,
                        ItemId = 490000346, ItemCount = 1,
                    },
                },
            };
        }

        private static IReadOnlyList<OnlineAttendanceRewardStage> PairTimeRewards(
            IReadOnlyList<int> seconds,
            IReadOnlyList<int> items)
        {
            var rewards = new List<OnlineAttendanceRewardStage>();
            long cumulative = 0;
            for (var i = 0; i < seconds.Count && i * 2 + 1 < items.Count; i++)
            {
                cumulative += Math.Max(0, seconds[i]);
                rewards.Add(new OnlineAttendanceRewardStage
                {
                    StageIndex = i + 1,
                    RequiredSeconds = cumulative,
                    ItemId = items[i * 2],
                    ItemCount = items[i * 2 + 1],
                });
            }

            return rewards;
        }

        private static IReadOnlyList<OnlineAttendanceRewardStage> PairSumRewards(
            IReadOnlyList<int> counts,
            IReadOnlyList<int> items)
        {
            var rewards = new List<OnlineAttendanceRewardStage>();
            long cumulative = 0;
            for (var i = 0; i < counts.Count && i * 2 + 1 < items.Count; i++)
            {
                cumulative += Math.Max(0, counts[i]);
                rewards.Add(new OnlineAttendanceRewardStage
                {
                    StageIndex = i + 1,
                    RequiredSeconds = cumulative,
                    ItemId = items[i * 2],
                    ItemCount = items[i * 2 + 1],
                });
            }

            return rewards;
        }

        private static IReadOnlyList<int> ReadNumericValues(string block)
        {
            if (string.IsNullOrWhiteSpace(block))
                return Array.Empty<int>();

            var values = new List<int>();
            foreach (var rawLine in block.Split(
                         new[] { '\r', '\n' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var line = StripComment(rawLine);
                foreach (Match match in Regex.Matches(line, @"-?\d+"))
                    values.Add(int.Parse(match.Value));
            }

            return values;
        }

        private static string ReadClosedBlock(string text, string tag)
        {
            var match = Regex.Match(
                text,
                @"\[" + Regex.Escape(tag) + @"\](?<body>.*?)\[/"
                + Regex.Escape(tag) + @"\]",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? match.Groups["body"].Value : string.Empty;
        }

        private static string StripComment(string line)
        {
            if (string.IsNullOrEmpty(line))
                return string.Empty;

            var index = line.IndexOf('#');
            return index >= 0 ? line.Substring(0, index) : line;
        }

        private static void Validate(OnlineAttendanceConfig config)
        {
            if (config.TimeRewards.Count == 0)
            {
                throw new FormatException(
                    "chn_attendanceevent.evt must define time rewards.");
            }

            foreach (var reward in config.TimeRewards)
            {
                if (reward.StageIndex <= 0
                    || reward.ItemId <= 0
                    || reward.ItemCount <= 0
                    || reward.RequiredSeconds <= 0)
                {
                    throw new FormatException(
                        "chn_attendanceevent.evt contains an invalid time reward.");
                }
            }

            for (var i = 1; i < config.TimeRewards.Count; i++)
            {
                if (config.TimeRewards[i].RequiredSeconds
                    < config.TimeRewards[i - 1].RequiredSeconds)
                {
                    throw new FormatException(
                        "chn_attendanceevent.evt time rewards must be ascending.");
                }
            }
        }
    }
}
