using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Events.LoginReward
{
    internal sealed class LoginRewardConfigProvider
    {
        private static readonly Lazy<LoginRewardConfig> SharedConfig =
            new Lazy<LoginRewardConfig>(LoadShared);

        internal static LoginRewardConfigProvider Instance { get; } =
            new LoginRewardConfigProvider();

        private LoginRewardConfigProvider()
        {
        }

        internal LoginRewardConfig Current => SharedConfig.Value;

        internal void Warmup()
        {
            _ = Current;
        }

        private static LoginRewardConfig LoadShared()
        {
            try
            {
                var loaded = LoginRewardConfigParser.Parse(
                    PvfArchiveAccessor.ReadText(LoginRewardConfig.PvfPath));
                FileLogger.Log(
                    "[LoginReward] loaded "
                    + $"{LoginRewardConfig.PvfPath} "
                    + $"tracks={loaded.Tracks.Count}");
                return loaded;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[LoginReward] failed to load PVF config, "
                    + "using fallback: " + ex.Message);
                return LoginRewardConfigParser.CreateFallback();
            }
        }
    }

    internal static class LoginRewardConfigParser
    {
        private static readonly Regex BlockRegex = new Regex(
            @"\[reward item\](?<body>.*?)\[/reward item\]",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex TokenRegex = new Regex(
            @"`(?<date>[^`]+)`\s*(?<itemId>-?\d+)\s*(?<count>-?\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        internal static LoginRewardConfig Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException(
                    "chn_login_reward.evt is empty.",
                    nameof(text));
            }

            var tracks = new List<LoginRewardTrack>();
            var trackIndex = 1;
            foreach (Match blockMatch in BlockRegex.Matches(text))
            {
                var days = new List<LoginRewardDay>();
                var dayIndex = 0;
                foreach (Match tokenMatch in TokenRegex.Matches(blockMatch.Groups["body"].Value))
                {
                    if (!DateTime.TryParseExact(
                            tokenMatch.Groups["date"].Value.Trim('`', ' '),
                            "yyyy-MM-dd HH:mm:ss",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out var deadline))
                    {
                        continue;
                    }

                    days.Add(new LoginRewardDay
                    {
                        DayIndex = dayIndex,
                        Deadline = deadline,
                        ItemId = int.Parse(tokenMatch.Groups["itemId"].Value),
                        ItemCount = int.Parse(tokenMatch.Groups["count"].Value),
                    });
                    dayIndex++;
                }

                tracks.Add(new LoginRewardTrack
                {
                    TrackIndex = trackIndex++,
                    Days = days,
                });
            }

            return new LoginRewardConfig { Tracks = tracks };
        }

        internal static LoginRewardConfig CreateFallback()
        {
            var days = new List<LoginRewardDay>();
            var baseDate = new DateTime(2026, 1, 1);
            for (var i = 0; i < 28; i++)
            {
                days.Add(new LoginRewardDay
                {
                    DayIndex = i,
                    Deadline = baseDate.AddDays(i),
                    ItemId = 490003342 + (i % 7),
                    ItemCount = 1,
                });
            }

            return new LoginRewardConfig
            {
                Tracks = new[]
                {
                    new LoginRewardTrack
                    {
                        TrackIndex = 1,
                        Days = days,
                    },
                },
            };
        }
    }
}
