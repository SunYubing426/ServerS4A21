using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Events.GrowSupport
{
    internal sealed class GrowSupportConfigProvider
    {
        private static readonly Lazy<GrowSupportConfig> SharedConfig =
            new Lazy<GrowSupportConfig>(LoadShared);

        internal static GrowSupportConfigProvider Instance { get; } =
            new GrowSupportConfigProvider();

        private GrowSupportConfigProvider()
        {
        }

        internal GrowSupportConfig Current => SharedConfig.Value;

        internal void Warmup()
        {
            _ = Current;
        }

        private static GrowSupportConfig LoadShared()
        {
            try
            {
                var loaded = GrowSupportConfigParser.Parse(
                    PvfArchiveAccessor.ReadText(GrowSupportConfig.PvfPath));
                FileLogger.Log(
                    "[GrowSupport] loaded "
                    + $"{GrowSupportConfig.PvfPath} "
                    + $"level={loaded.LevelRewards.Count} "
                    + $"dungeon={loaded.DungeonRewards.Count}");
                return loaded;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[GrowSupport] failed to load PVF config, "
                    + "using fallback: " + ex.Message);
                return GrowSupportConfigParser.CreateFallback();
            }
        }
    }

    internal static class GrowSupportConfigParser
    {
        internal static GrowSupportConfig Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException(
                    "chn_growsupport.evt is empty.",
                    nameof(text));
            }

            var startTimeBlock = ReadClosedBlock(text, "make character start time");
            var startTime = DateTime.TryParseExact(
                ExtractQuoted(startTimeBlock),
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed)
                ? parsed
                : new DateTime(2015, 4, 27);

            var postalValues = ReadNumericValues(
                ReadClosedBlock(text, "postal"));

            return new GrowSupportConfig
            {
                CharacterStartTime = startTime,
                LevelRewards = ParseLevelRewards(
                    ReadClosedBlock(text, "levelup reward")),
                DungeonRewards = ParseDungeonRewards(
                    ReadClosedBlock(text, "proper dungeon clear reward")),
                PostalTitleId = postalValues.Length > 0 ? postalValues[0] : 1295,
                PostalTextId = postalValues.Length > 1 ? postalValues[1] : 1296,
                PostalRetentionDays = postalValues.Length > 2 ? postalValues[2] : 15,
            };
        }

        internal static GrowSupportConfig CreateFallback()
        {
            return new GrowSupportConfig
            {
                CharacterStartTime = new DateTime(2015, 4, 27),
                LevelRewards = new[]
                {
                    new GrowSupportLevelReward { Level = 20, ItemId = 10005152, ItemCount = 1 },
                    new GrowSupportLevelReward { Level = 35, ItemId = 10005153, ItemCount = 1 },
                    new GrowSupportLevelReward { Level = 41, ItemId = 10005154, ItemCount = 1 },
                    new GrowSupportLevelReward { Level = 51, ItemId = 10005155, ItemCount = 1 },
                    new GrowSupportLevelReward { Level = 61, ItemId = 10005156, ItemCount = 1 },
                    new GrowSupportLevelReward { Level = 66, ItemId = 10005157, ItemCount = 1 },
                    new GrowSupportLevelReward { Level = 71, ItemId = 10005158, ItemCount = 1 },
                },
                DungeonRewards = new[]
                {
                    new GrowSupportDungeonReward { RequiredClears = 3, ItemId = 10005169, ItemCount = 1 },
                    new GrowSupportDungeonReward { RequiredClears = 6, ItemId = 10005169, ItemCount = 1 },
                    new GrowSupportDungeonReward { RequiredClears = 9, ItemId = 10005169, ItemCount = 1 },
                    new GrowSupportDungeonReward { RequiredClears = 12, ItemId = 10005169, ItemCount = 1 },
                    new GrowSupportDungeonReward { RequiredClears = 15, ItemId = 10005169, ItemCount = 1 },
                    new GrowSupportDungeonReward { RequiredClears = 99, ItemId = 10005170, ItemCount = 1 },
                },
                PostalTitleId = 1295,
                PostalTextId = 1296,
                PostalRetentionDays = 15,
            };
        }

        private static IReadOnlyList<GrowSupportLevelReward> ParseLevelRewards(
            string block)
        {
            var rewards = new List<GrowSupportLevelReward>();
            var values = ReadNumericValues(block);
            for (var offset = 0; offset + 2 < values.Length; offset += 3)
            {
                rewards.Add(new GrowSupportLevelReward
                {
                    Level = values[offset],
                    ItemId = values[offset + 1],
                    ItemCount = values[offset + 2],
                });
            }

            return rewards;
        }

        private static IReadOnlyList<GrowSupportDungeonReward> ParseDungeonRewards(
            string block)
        {
            var rewards = new List<GrowSupportDungeonReward>();
            var values = ReadNumericValues(block);
            for (var offset = 0; offset + 2 < values.Length; offset += 3)
            {
                rewards.Add(new GrowSupportDungeonReward
                {
                    RequiredClears = values[offset],
                    ItemId = values[offset + 1],
                    ItemCount = values[offset + 2],
                });
            }

            return rewards;
        }

        private static int[] ReadNumericValues(string block)
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

            return values.ToArray();
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

        private static string ExtractQuoted(string block)
        {
            if (string.IsNullOrWhiteSpace(block))
                return string.Empty;

            var match = Regex.Match(block, @"`(?<value>[^`]*)`");
            return match.Success ? match.Groups["value"].Value : string.Empty;
        }

        private static string StripComment(string line)
        {
            if (string.IsNullOrEmpty(line))
                return string.Empty;

            var index = line.IndexOf('#');
            return index >= 0 ? line.Substring(0, index) : line;
        }
    }
}
