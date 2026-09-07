using DfoServer.Game.CharacterData;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DfoServer.Game.Admin
{
    /// <summary>
    /// 实机测试辅助: 直接在 inventory.db 上播种/触发 Anton_Awakening (243-247)
    /// 相关的状态, 用于在不真打图的情况下验证：
    ///   1) 247 通关后 243-247 全部被锁
    ///   2) 跨天 06:00 重置只清 Anton_Awakening 行
    ///   3) 243 limit_count=1 时被服务端拒入
    ///   4) 清 Anton_Awakening 不波及 Anton_Normal/Quest 等其他副本
    ///
    /// 使用方法（需先停服）：
    ///   DfoServer.exe --gm-anton-seed &lt;characterId|characterName&gt;
    ///                  [--gm-anton-mode &lt;seed-cleared|trigger-clear|trigger-crossday|exhaust-243&gt;]
    ///                  [--gm-anton-db &lt;inventory.db 绝对路径&gt;]
    /// 不传 --gm-anton-mode 时默认 seed-cleared。
    /// 退出码: 0=成功, 1=参数/查表失败, 2=数据库执行失败。
    /// </summary>
    public static class AntonGmSeedCommand
    {
        private const int BlackVolcanoDungeonId = 247;
        private static readonly int[] AntonAwakeningDungeonIds = { 243, 244, 245, 246, 247 };

        public enum Mode
        {
            SeedCleared,
            TriggerClear,
            TriggerCrossDay,
            Exhaust243,
        }

        public static int Run(string[] args)
        {
            try
            {
                var target = ParseValue(args, "--gm-anton-seed");
                if (string.IsNullOrWhiteSpace(target))
                {
                    Console.Error.WriteLine("[gm-anton] missing --gm-anton-seed <characterId|name>");
                    return 1;
                }

                var modeText = ParseValue(args, "--gm-anton-mode") ?? "seed-cleared";
                if (!TryParseMode(modeText, out var mode))
                {
                    Console.Error.WriteLine($"[gm-anton] unknown mode '{modeText}'. Valid: seed-cleared|trigger-clear|trigger-crossday|exhaust-243");
                    return 1;
                }

                var overrideDbPath = ParseValue(args, "--gm-anton-db");
                var dbPath = string.IsNullOrWhiteSpace(overrideDbPath)
                    ? ServerPaths.DatabasePath
                    : overrideDbPath;
                if (!File.Exists(dbPath))
                {
                    Console.Error.WriteLine($"[gm-anton] inventory.db not found at '{dbPath}'.");
                    return 1;
                }

                var schemaPath = ServerPaths.SchemaFilePath;
                if (!File.Exists(schemaPath))
                {
                    Console.Error.WriteLine($"[gm-anton] item_schema.sql not found at '{schemaPath}'.");
                    return 1;
                }

                Console.WriteLine($"[gm-anton] db={dbPath}");
                Console.WriteLine($"[gm-anton] mode={mode} target={target}");

                IGameDatabase database = new GameDatabase(dbPath, schemaPath);
                var repository = new SqliteCharacterStateRepository(database);
                var entryLimits = new DungeonEntryLimitService(database);
                var dailyReset = new DailyResetService(database);

                var resolved = ResolveTarget(database, target);
                if (resolved == null)
                {
                    Console.Error.WriteLine($"[gm-anton] target '{target}' not found in characters table.");
                    return 1;
                }
                Console.WriteLine($"[gm-anton] resolved character_id={resolved.CharacterId} account_id={resolved.AccountId} name={resolved.Name}");

                int exit;
                switch (mode)
                {
                    case Mode.SeedCleared:
                        exit = DoSeedCleared(repository, resolved);
                        break;
                    case Mode.TriggerClear:
                        exit = DoTriggerClear(repository, resolved);
                        break;
                    case Mode.TriggerCrossDay:
                        exit = DoTriggerCrossDay(database, dailyReset, resolved);
                        break;
                    case Mode.Exhaust243:
                        exit = DoExhaust243(entryLimits, resolved);
                        break;
                    default:
                        exit = 1;
                        break;
                }

                if (exit != 0)
                    Console.Error.WriteLine($"[gm-anton] mode {mode} failed (exit={exit}).");
                else
                    Console.WriteLine($"[gm-anton] mode {mode} OK.");
                return exit;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[gm-anton] unexpected error: {ex}");
                return 2;
            }
        }

        private static int DoSeedCleared(
            SqliteCharacterStateRepository repository,
            ResolvedTarget target)
        {
            // 与 AntonNormalConquestApplicationService 通关 Black Volcano 后的
            // 终态保持一致: 243-246 unlocked, 247 completed。
            foreach (var dungeonId in new[] { 243, 244, 245, 246 })
            {
                if (!repository.UpsertDungeonPermission(target.CharacterId, dungeonId, 1))
                    Console.WriteLine($"[gm-anton] upsert {dungeonId} state=1 returned false (no-op).");
            }
            if (!repository.UpsertDungeonPermission(target.CharacterId, BlackVolcanoDungeonId, 2))
                Console.WriteLine($"[gm-anton] upsert {BlackVolcanoDungeonId} state=2 returned false (no-op).");

            var rows = repository.LoadDungeonPermissions(target.CharacterId)
                .Where(e => e.DungeonId >= 243 && e.DungeonId <= 247)
                .OrderBy(e => e.DungeonId)
                .Select(e => $"{e.DungeonId}={e.ClearState}")
                .ToArray();
            Console.WriteLine($"[gm-anton] post-seed 243-247: {(rows.Length == 0 ? "<empty>" : string.Join(", ", rows))}");
            return 0;
        }

        private static int DoTriggerClear(
            SqliteCharacterStateRepository repository,
            ResolvedTarget target)
        {
            var deleted = repository.DeleteDungeonPermissions(
                target.CharacterId,
                AntonAwakeningDungeonIds);
            Console.WriteLine($"[gm-anton] DeleteDungeonPermissions(243..247) deleted={deleted}");
            return 0;
        }

        private static int DoTriggerCrossDay(
            IGameDatabase database,
            DailyResetService dailyReset,
            ResolvedTarget target)
        {
            // 与 DailyResetService.TryRunAccountFirstLoginReset 在跨天 06:00 后
            // 调用的 resetAction 保持一致: 只删该账号下 243-247 的 character_dungeon_permissions 行。
            using (var conn = new SqliteConnection(database.ConnectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    var ok = dailyReset.TryRunAccountFirstLoginReset(
                        conn,
                        tx,
                        target.AccountId,
                        DateTime.UtcNow,
                        resetAction: (c, t) =>
                        {
                            using (var cmd = c.CreateCommand())
                            {
                                cmd.Transaction = t;
                                cmd.CommandText = @"
DELETE FROM character_dungeon_permissions
WHERE dungeon_id IN (243, 244, 245, 246, 247)
  AND character_id IN (
      SELECT character_id FROM characters WHERE account_id = @aid
  );";
                                cmd.Parameters.AddWithValue("@aid", target.AccountId);
                                cmd.ExecuteNonQuery();
                            }
                            return true;
                        },
                        out var applied);
                    if (!ok)
                    {
                        Console.Error.WriteLine("[gm-anton] TryRunAccountFirstLoginReset returned false.");
                        return 2;
                    }
                    Console.WriteLine($"[gm-anton] cross-day reset applied={applied}");
                    tx.Commit();
                }
            }
            return 0;
        }

        private static int DoExhaust243(
            DungeonEntryLimitService entryLimits,
            ResolvedTarget target)
        {
            var preSnapshot = entryLimits.LoadSpecialDungeonLimits(target.AccountId, target.CharacterId)
                .FirstOrDefault(s => s.DungeonId == 243);
            if (preSnapshot == null)
            {
                Console.Error.WriteLine("[gm-anton] dungeon_limit_config has no row for dgn_id=243. Did the schema seed run?");
                return 2;
            }
            Console.WriteLine($"[gm-anton] pre: 243 limit_count={preSnapshot.LimitCount} current_count={preSnapshot.CurrentCount}");

            entryLimits.TryConsumeSpecialDungeonLimit(
                target.AccountId,
                target.CharacterId,
                243,
                consumeCount: 1,
                out var consumeResult);
            Console.WriteLine($"[gm-anton] consume 243 => allowed={consumeResult.Allowed} limited={consumeResult.IsLimited} reason={consumeResult.Reason}");

            var postSnapshot = entryLimits.LoadSpecialDungeonLimits(target.AccountId, target.CharacterId)
                .FirstOrDefault(s => s.DungeonId == 243);
            if (postSnapshot != null)
                Console.WriteLine($"[gm-anton] post: 243 limit_count={postSnapshot.LimitCount} current_count={postSnapshot.CurrentCount}");
            return 0;
        }

        private static bool TryParseMode(string text, out Mode mode)
        {
            switch (text.Trim().ToLowerInvariant())
            {
                case "seed-cleared":
                    mode = Mode.SeedCleared;
                    return true;
                case "trigger-clear":
                    mode = Mode.TriggerClear;
                    return true;
                case "trigger-crossday":
                    mode = Mode.TriggerCrossDay;
                    return true;
                case "exhaust-243":
                    mode = Mode.Exhaust243;
                    return true;
                default:
                    mode = default;
                    return false;
            }
        }

        private static string ParseValue(string[] args, string key)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], key, StringComparison.Ordinal))
                    return args[i + 1];
            }
            return null;
        }

        private sealed class ResolvedTarget
        {
            public int AccountId;
            public int CharacterId;
            public string Name;
        }

        private static ResolvedTarget ResolveTarget(IGameDatabase database, string target)
        {
            using (var conn = new SqliteConnection(database.ConnectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    if (int.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out var characterId))
                    {
                        cmd.CommandText = @"
SELECT character_id, account_id, CAST(name AS BLOB)
FROM characters
WHERE character_id = @cid AND delete_flag = 0
LIMIT 1;";
                        cmd.Parameters.AddWithValue("@cid", characterId);
                    }
                    else
                    {
                        // characters.name 存的是 GBK 字节(参见 SqliteMigrations.cs:508-510),
                        // 直接把入参编码为 GBK 字节,作为 Blob 比对,避开 CAST(name AS TEXT) 的
                        // UTF-8 解码错位问题。
                        cmd.CommandText = @"
SELECT character_id, account_id, CAST(name AS BLOB)
FROM characters
WHERE name = @nm AND delete_flag = 0
LIMIT 1;";
                        cmd.Parameters.Add("@nm", SqliteType.Blob).Value =
                            Infrastructure.ClientTextEncoding.GetBytes(target);
                    }
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read())
                            return null;
                        var nameBytes = reader.GetValue(2) as byte[];
                        return new ResolvedTarget
                        {
                            CharacterId = reader.GetInt32(0),
                            AccountId = reader.GetInt32(1),
                            Name = nameBytes == null
                                ? string.Empty
                                : Infrastructure.ClientTextEncoding.GetString(nameBytes),
                        };
                    }
                }
            }
        }
    }
}