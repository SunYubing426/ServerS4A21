using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using DfoServer.GameWorld;

namespace DfoServer.Game.VendingMachine
{
    internal sealed record VendingReward(int ItemId, int Weight, int Count, int Notice);

    internal sealed class VendingMachineGroup
    {
        internal int MaterialId { get; }
        internal int MaterialCount { get; }
        internal IReadOnlyList<VendingReward> Rewards { get; }
        internal int TotalWeight { get; }

        internal VendingMachineGroup(int materialId, int materialCount, IReadOnlyList<VendingReward> rewards)
        {
            if (materialId <= 0 || materialCount <= 0 || rewards == null || rewards.Count == 0
                || rewards.Any(r => r.ItemId <= 0 || r.Weight < 0 || r.Count <= 0))
                throw new FormatException("Invalid vending material or reward");
            MaterialId = materialId;
            MaterialCount = materialCount;
            Rewards = rewards.ToArray();
            TotalWeight = checked((int)rewards.Sum(r => (long)r.Weight));
            if (TotalWeight <= 0) throw new FormatException("Empty vending probability distribution");
        }

        internal VendingReward Roll(int ticket)
        {
            if (ticket < 0 || ticket >= TotalWeight) throw new ArgumentOutOfRangeException(nameof(ticket));
            foreach (var reward in Rewards)
            {
                if (ticket < reward.Weight) return reward;
                ticket -= reward.Weight;
            }
            throw new InvalidOperationException("Invalid vending probability distribution");
        }
    }

    internal sealed class VendingMachineCatalog
    {
        private readonly Dictionary<(int machine, int group), VendingMachineGroup> _groups = new();
        private static readonly Lazy<VendingMachineCatalog> Current = new(() => new(PvfArchiveAccessor.ReadText));
        internal static VendingMachineCatalog Load() => Current.Value;

        internal VendingMachineCatalog(Func<string, string> readText)
        {
            // A21 vendingmachine.lst maps machine 1 to pcroom.vm and 3 to pcroom4.vm.
            var entries = Regex.Matches(readText("etc/vendingmachine.lst"), @"(\d+)\s+`([^`]+)`");
            foreach (Match entry in entries)
            {
                var machine = int.Parse(entry.Groups[1].Value, CultureInfo.InvariantCulture);
                if (machine != 1 && machine != 3) continue;
                var path = entry.Groups[2].Value.Replace('\\', '/');
                if (path.Contains("..") || path.StartsWith('/')) throw new FormatException("Invalid vending path");
                var text = readText("etc/" + path);
                foreach (Match block in Regex.Matches(text, @"\[item group\](.*?)\[/item group\]", RegexOptions.Singleline))
                {
                    var group = Values(block.Groups[1].Value, "group num");
                    var material = Values(block.Groups[1].Value, "material");
                    var output = Values(block.Groups[1].Value, "output");
                    if (group.Length != 1 || group[0] <= 0 || material.Length != 2 || output.Length % 4 != 0)
                        throw new FormatException("Invalid vending group layout");
                    var rewards = new List<VendingReward>();
                    for (var i = 0; i < output.Length; i += 4)
                        rewards.Add(new(output[i], output[i + 1], output[i + 2], output[i + 3]));
                    _groups.Add((machine, group[0]), new(material[0], material[1], rewards));
                }
            }
            if (!_groups.ContainsKey((1, 1)) || !_groups.ContainsKey((3, 1)))
                throw new FormatException("Missing black diamond vending definitions");
        }

        private static int[] Values(string block, string tag)
        {
            var match = Regex.Match(block, @"\[" + Regex.Escape(tag) + @"\]\s*([^\[]*)");
            if (!match.Success) throw new FormatException("Missing vending tag: " + tag);
            return match.Groups[1].Value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        }

        internal bool TryGet(int machine, int group, out VendingMachineGroup definition) =>
            _groups.TryGetValue((machine, group), out definition);
    }
}


