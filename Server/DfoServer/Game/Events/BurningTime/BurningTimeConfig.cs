using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Events.BurningTime
{
    internal sealed class BurningTimeConfigProvider
    {
        private static readonly Lazy<BurningTimeConfig> SharedConfig =
            new Lazy<BurningTimeConfig>(LoadShared);

        internal static BurningTimeConfigProvider Instance { get; } =
            new BurningTimeConfigProvider();

        private BurningTimeConfigProvider()
        {
        }

        internal BurningTimeConfig Current => SharedConfig.Value;

        internal void Warmup()
        {
            _ = Current;
        }

        private static BurningTimeConfig LoadShared()
        {
            try
            {
                var loaded = BurningTimeConfigParser.Parse(
                    PvfArchiveAccessor.ReadText(BurningTimeConfig.PvfPath));
                FileLogger.Log(
                    "[BurningTime] loaded "
                    + $"{BurningTimeConfig.PvfPath} "
                    + $"phases={loaded.Phases.Count}");
                return loaded;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[BurningTime] failed to load PVF config, "
                    + "using fallback: " + ex.Message);
                return BurningTimeConfigParser.CreateFallback();
            }
        }
    }

    internal static class BurningTimeConfigParser
    {
        internal static BurningTimeConfig Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException(
                    "chn_burningtimebuff.evt is empty.",
                    nameof(text));
            }

            var values = ReadNumericValues(
                ReadClosedBlock(text, "burning time event param"));
            return new BurningTimeConfig
            {
                Phases = ParsePhases(values),
            };
        }

        internal static BurningTimeConfig CreateFallback()
        {
            return new BurningTimeConfig
            {
                Phases = ParsePhases(new[]
                {
                    0, 1800, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 900, 0,
                    0, 30, 255, 255, 0, 25, 0, 0, 0, 2, 1500, 0, 0, 50, 255,
                    255, 0, 50, 3, 3, 3, 3, 1800, 0, 0, 100, 255, 255, 0, 75,
                    5, 5, 5, 4, 0, 0, 1, 120, 255, 255, 0, 100, 10, 10, 10,
                }),
            };
        }

        private static IReadOnlyList<BurningTimePhase> ParsePhases(
            IReadOnlyList<int> values)
        {
            const int PhaseSize = 15;
            var phases = new List<BurningTimePhase>();
            for (var offset = 0; offset + PhaseSize <= values.Count;
                 offset += PhaseSize)
            {
                phases.Add(new BurningTimePhase
                {
                    PhaseIndex = phases.Count + 1,
                    Values = values.Skip(offset).Take(PhaseSize).ToList(),
                });
            }

            return phases;
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
    }
}
