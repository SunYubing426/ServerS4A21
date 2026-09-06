using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DfoServer.GameWorld
{
    internal static class HardcodedTextTagCatalog
    {
        private static readonly string[] SourcePaths =
        {
            "etc/hardcodetexttag.etc",
            "etc/hardcodetexttag_chn.etc",
        };

        private static readonly Regex EntryPattern = new Regex(
            "`([^`]*)`\\s*`([^`]*)`",
            RegexOptions.Compiled);

        private static readonly Lazy<IReadOnlyDictionary<string, string>> Table =
            new Lazy<IReadOnlyDictionary<string, string>>(Load);

        internal static bool TryGet(string key, out string value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(key))
                return false;
            return Table.Value.TryGetValue(key, out value)
                && !string.IsNullOrEmpty(value);
        }

        private static IReadOnlyDictionary<string, string> Load()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in SourcePaths)
            {
                string text;
                try
                {
                    text = PvfArchiveAccessor.ReadText(path);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (Match match in EntryPattern.Matches(text ?? string.Empty))
                {
                    var key = match.Groups[1].Value;
                    if (key.Length == 0)
                        continue;
                    map[key] = match.Groups[2].Value;
                }
            }

            return map;
        }
    }
}
