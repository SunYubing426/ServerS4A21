using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DfoServer.GameWorld;

namespace DfoServer.Game.Vending
{
    internal sealed record VendingPrize(int ItemId, int Weight, int Count, int BroadcastFlag);

    internal sealed class VendingMachineDefinition
    {
        internal uint MachineId { get; init; }
        internal uint GroupId { get; init; }
        internal int CoinItemId { get; init; }
        internal IReadOnlyList<VendingPrize> Prizes { get; init; }
        internal int TotalWeight { get; init; }

        internal VendingPrize Pick(int ticket)
        {
            if (ticket < 0 || ticket >= TotalWeight)
                throw new ArgumentOutOfRangeException(nameof(ticket));
            foreach (var prize in Prizes)
            {
                if (ticket < prize.Weight) return prize;
                ticket -= prize.Weight;
            }
            throw new InvalidOperationException("Invalid vending weight total");
        }
    }

    internal static class VendingMachineCatalog
    {
        private static readonly Lazy<IReadOnlyDictionary<uint, VendingMachineDefinition>> Cached = new(Load);

        internal static bool TryGet(uint machineId, out VendingMachineDefinition definition)
            => Cached.Value.TryGetValue(machineId, out definition);

        private static IReadOnlyDictionary<uint, VendingMachineDefinition> Load()
        {
            var result = new Dictionary<uint, VendingMachineDefinition>();
            var list = PvfArchiveAccessor.ReadText("etc/vendingmachine.lst");
            foreach (Match match in Regex.Matches(list, @"(\d+)\s+`([^`]+)`"))
            {
                var id = uint.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                // 本批仅实现黑钻普通机与高级机；周年活动机不套用黑钻规则。
                if (id != 1 && id != 3) continue;
                result.Add(id, Parse(id, PvfArchiveAccessor.ReadText("etc/" + match.Groups[2].Value)));
            }
            if (result.Count != 2) throw new InvalidDataException("Missing black diamond vending definitions");
            return result;
        }

        internal static VendingMachineDefinition Parse(uint machineId, string text)
        {
            var groups = Regex.Matches(text ?? "", @"\[item group\](.*?)\[/item group\]", RegexOptions.Singleline);
            if (groups.Count != 1) throw new InvalidDataException("Unsupported vending item groups");
            var group = groups[0].Groups[1].Value;
            var groupMatch = Regex.Match(group, @"\[group num\]\s*(\d+)\s*\[material\]\s*(\d+)\s+(\d+)\s*\[output\](.*?)\[/output\]", RegexOptions.Singleline);
            if (!groupMatch.Success) throw new InvalidDataException("Malformed vending definition");
            var groupId = uint.Parse(groupMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            var coin = int.Parse(groupMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            var coinCount = int.Parse(groupMatch.Groups[3].Value, CultureInfo.InvariantCulture);
            if (groupId != 1 || coin <= 0 || coinCount != 1)
                throw new InvalidDataException("Unsupported vending material/group rule");
            var values = groupMatch.Groups[4].Value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            if (values.Length == 0 || values.Length % 4 != 0)
                throw new InvalidDataException("Vending output must contain complete quadruples");
            var prizes = new List<VendingPrize>();
            var total = 0;
            for (var i = 0; i < values.Length; i += 4)
            {
                var prize = new VendingPrize(values[i], values[i + 1], values[i + 2], values[i + 3]);
                if (prize.ItemId <= 0 || prize.Weight < 0 || prize.Count <= 0
                    || (prize.BroadcastFlag != 0 && prize.BroadcastFlag != 1))
                    throw new InvalidDataException("Invalid vending prize");
                total = checked(total + prize.Weight);
                prizes.Add(prize);
            }
            if (total <= 0) throw new InvalidDataException("Empty vending probability distribution");
            return new VendingMachineDefinition
            {
                MachineId = machineId, GroupId = groupId, CoinItemId = coin,
                Prizes = prizes.AsReadOnly(), TotalWeight = total,
            };
        }
    }
}
