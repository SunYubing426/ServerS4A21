using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Guilds
{
    /// <summary>
    /// 公会地下城目录(2026-09-07 对齐参考包 GuildCoinCatalog):
    /// 成员关系权威来源 = PVF `etc/guild/guilddungeon/guilddungeonstyle.lst`
    /// 及其每个 style 文件的 [dungeon] 块 [dungeon index]。
    /// 仅用于"每日第 5 次公会地下城通关 +10 贡献币"判定;
    /// 解析失败=空集(功能关闭), 绝不影响副本结算。
    /// </summary>
    internal static class GuildDungeonCatalog
    {
        private const string Directory = "etc/guild/guilddungeon/";

        private static readonly Lazy<HashSet<int>> Dungeons = new Lazy<HashSet<int>>(Load);

        /// <summary>副本 id 是否为公会地下城。解析失败时恒 false。</summary>
        internal static bool IsGuildDungeon(int dungeonId)
        {
            if (dungeonId <= 0)
                return false;
            try
            {
                return Dungeons.Value.Contains(dungeonId);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GuildDungeon] 目录查询失败 dungeon={dungeonId}: {ex.Message}");
                return false;
            }
        }

        private static HashSet<int> Load()
        {
            var result = new HashSet<int>();
            try
            {
                var list = PvfArchiveAccessor.ReadText(Directory + "guilddungeonstyle.lst");
                // 条目 = <id> `相对路径`
                var entries = Regex.Matches(list ?? string.Empty, @"(\d+)\s+`([^`]+)`");
                foreach (Match entry in entries)
                {
                    var path = entry.Groups[2].Value.Replace('\\', '/');
                    if (path.Contains("..", StringComparison.Ordinal)
                        || path.StartsWith('/')
                        || path.Contains(':'))
                        continue;
                    string text;
                    try
                    {
                        text = PvfArchiveAccessor.ReadText(Directory + path);
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log($"[GuildDungeon] style 读取失败 {path}: {ex.Message}");
                        continue;
                    }
                    var blocks = Regex.Matches(
                        text ?? string.Empty, @"\[dungeon\](.*?)\[/dungeon\]",
                        RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    foreach (Match block in blocks)
                    {
                        var ids = Regex.Matches(
                            block.Groups[1].Value, @"\[dungeon index\]\s*([^\[]+)",
                            RegexOptions.IgnoreCase);
                        if (ids.Count != 1)
                            continue;
                        if (int.TryParse(ids[0].Groups[1].Value.Trim(), out var id) && id > 0)
                            result.Add(id);
                    }
                }
                FileLogger.Log($"[GuildDungeon] 公会地下城目录加载完成: {result.Count} 个副本");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GuildDungeon] 目录加载失败(功能关闭): {ex.Message}");
            }
            return result;
        }
    }
}
