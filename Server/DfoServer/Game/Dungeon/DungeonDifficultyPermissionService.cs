using DfoServer.Game.DailyReset;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Dungeon
{
    internal enum DungeonPermissionPersistenceScope
    {
        Unknown = 0,
        None = 1,
        AccountDifficulty = 2,
        CharacterMechanism = 3,
    }

    internal static class DungeonPermissionScopePolicy
    {
        internal static DungeonPermissionPersistenceScope Resolve(int dungeonId)
        {
            if (dungeonId <= 0 || dungeonId > ushort.MaxValue)
                return DungeonPermissionPersistenceScope.Unknown;

            return ResolveUncached(dungeonId);
        }

        internal static bool IsAccountDifficulty(int dungeonId) =>
            Resolve(dungeonId)
                == DungeonPermissionPersistenceScope.AccountDifficulty;

        private static DungeonPermissionPersistenceScope ResolveUncached(
            int dungeonId)
        {
            try
            {
                if (!DungeonPermissionDefinitionResolver.TryResolve(
                        dungeonId,
                        out var definition,
                        out var failureReason))
                {
                    FileLogger.Log(
                        $"[DungeonDifficultyPermission] scope is unknown " +
                        $"dungeon={dungeonId}: {failureReason}");
                    return DungeonPermissionPersistenceScope.Unknown;
                }

                if (definition.IsTaskExclusive)
                    return DungeonPermissionPersistenceScope.None;

                // Anton uses the same 0x0005 rows for its character-specific
                // conquest chain. It is not an account difficulty unlock.
                if (AntonNormalConquest.TryGetSequence(dungeonId, out _))
                    return DungeonPermissionPersistenceScope.CharacterMechanism;

                if (!definition.HasWorldMapReference
                    || !definition.HasExplicitDifficultyConfiguration)
                {
                    FileLogger.Log(
                        $"[DungeonDifficultyPermission] scope is unknown " +
                        $"dungeon={dungeonId} path={definition.FilePath} " +
                        $"worldMap={definition.HasWorldMapReference} " +
                        $"difficulty={definition.HasExplicitDifficultyConfiguration}");
                    return DungeonPermissionPersistenceScope.Unknown;
                }

                return DungeonPermissionPersistenceScope.AccountDifficulty;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonDifficultyPermission] scope resolution failed closed " +
                    $"dungeon={dungeonId}: {ex.Message}");
                return DungeonPermissionPersistenceScope.Unknown;
            }
        }
    }

    internal sealed class AccountDungeonPermissionRepository
    {
        private readonly string _connectionString;

        internal AccountDungeonPermissionRepository(
            string databasePath,
            string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath))
        {
        }

        internal AccountDungeonPermissionRepository(IGameDatabase database)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
        }

        // 暴露连接字符串供登录时的跨天重置复用同一 Sqlite 数据库（同一进程同一文件）。
        internal string ConnectionStringForLoginReset => _connectionString;

        internal List<DungeonPermissionEntrySnapshot> Load(int accountId)
        {
            if (accountId <= 0)
                return new List<DungeonPermissionEntrySnapshot>();

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                return Load(connection, transaction: null, accountId);
            }
        }

        internal List<DungeonPermissionEntrySnapshot> LoadLegacyByAccount(
            int accountId)
        {
            var result = new List<DungeonPermissionEntrySnapshot>();
            if (accountId <= 0)
                return result;

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT permission.dungeon_id, MAX(permission.clear_state)
FROM character_dungeon_permissions AS permission
JOIN characters AS character
  ON character.character_id = permission.character_id
WHERE character.account_id = @accountId
GROUP BY permission.dungeon_id
ORDER BY permission.dungeon_id;";
                    command.Parameters.AddWithValue("@accountId", accountId);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var dungeonId = reader.GetInt32(0);
                            var clearState = reader.GetInt32(1);
                            if (dungeonId <= 0
                                || dungeonId > ushort.MaxValue
                                || clearState <= 0
                                || clearState > byte.MaxValue)
                            {
                                continue;
                            }

                            result.Add(new DungeonPermissionEntrySnapshot
                            {
                                DungeonId = (ushort)dungeonId,
                                ClearState = (byte)clearState,
                            });
                        }
                    }
                }
            }

            return result;
        }

        internal List<DungeonPermissionEntrySnapshot> ApplyBatch(
            int accountId,
            IReadOnlyCollection<DungeonPermissionEntrySnapshot> updates,
            out List<DungeonPermissionEntrySnapshot> changes)
        {
            if (accountId <= 0)
                throw new ArgumentOutOfRangeException(nameof(accountId));
            if (updates == null)
                throw new ArgumentNullException(nameof(updates));

            var normalized = Normalize(updates);
            changes = new List<DungeonPermissionEntrySnapshot>();
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    foreach (var update in normalized)
                    {
                        var currentState = LoadState(
                            connection,
                            transaction,
                            accountId,
                            update.DungeonId);
                        if (currentState >= update.ClearState)
                            continue;

                        using (var command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = @"
INSERT INTO account_dungeon_permissions(
    account_id,
    dungeon_id,
    clear_state,
    updated_at)
VALUES(
    @accountId,
    @dungeonId,
    @clearState,
    CURRENT_TIMESTAMP)
ON CONFLICT(account_id, dungeon_id) DO UPDATE SET
    clear_state = MAX(
        account_dungeon_permissions.clear_state,
        excluded.clear_state),
    updated_at = CASE
        WHEN excluded.clear_state
            > account_dungeon_permissions.clear_state
        THEN CURRENT_TIMESTAMP
        ELSE account_dungeon_permissions.updated_at
    END;";
                            command.Parameters.AddWithValue(
                                "@accountId",
                                accountId);
                            command.Parameters.AddWithValue(
                                "@dungeonId",
                                (int)update.DungeonId);
                            command.Parameters.AddWithValue(
                                "@clearState",
                                (int)update.ClearState);
                            command.ExecuteNonQuery();
                        }

                        changes.Add(new DungeonPermissionEntrySnapshot
                        {
                            DungeonId = update.DungeonId,
                            ClearState = update.ClearState,
                        });
                    }

                    var snapshot = Load(connection, transaction, accountId);
                    transaction.Commit();
                    return snapshot;
                }
            }
        }

        private static List<DungeonPermissionEntrySnapshot> Normalize(
            IReadOnlyCollection<DungeonPermissionEntrySnapshot> updates)
        {
            var states = new Dictionary<ushort, byte>();
            foreach (var update in updates)
            {
                if (update == null
                    || update.DungeonId == 0
                    || update.ClearState == 0)
                {
                    throw new ArgumentException(
                        "Dungeon permission updates require non-zero dungeon and state values.",
                        nameof(updates));
                }

                if (!states.TryGetValue(update.DungeonId, out var state)
                    || state < update.ClearState)
                {
                    states[update.DungeonId] = update.ClearState;
                }
            }

            return states
                .OrderBy(entry => entry.Key)
                .Select(entry => new DungeonPermissionEntrySnapshot
                {
                    DungeonId = entry.Key,
                    ClearState = entry.Value,
                })
                .ToList();
        }

        private static int LoadState(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int dungeonId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT clear_state
FROM account_dungeon_permissions
WHERE account_id = @accountId AND dungeon_id = @dungeonId;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@dungeonId", dungeonId);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value
                    ? 0
                    : Convert.ToInt32(value);
            }
        }

        private static List<DungeonPermissionEntrySnapshot> Load(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId)
        {
            var result = new List<DungeonPermissionEntrySnapshot>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT dungeon_id, clear_state
FROM account_dungeon_permissions
WHERE account_id = @accountId
ORDER BY dungeon_id;";
                command.Parameters.AddWithValue("@accountId", accountId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new DungeonPermissionEntrySnapshot
                        {
                            DungeonId = (ushort)reader.GetInt32(0),
                            ClearState = (byte)reader.GetInt32(1),
                        });
                    }
                }
            }

            return result;
        }
    }

    internal sealed class DungeonDifficultyPermissionService
    {
        // Anton_Awakening (暴走安徒恩) 副本 ID：黑雾之源/震颤的大地/擎天之柱/能量阻截战/黑色火山
        private static readonly int[] AntonAwakeningDungeonIds =
            { 243, 244, 245, 246, 247 };

        private readonly AccountDungeonPermissionRepository _repository;
        private readonly DailyResetService _dailyReset;

        internal DungeonDifficultyPermissionService(
            string databasePath,
            string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath), null)
        {
        }

        internal DungeonDifficultyPermissionService(IGameDatabase database)
            : this(database, null)
        {
        }

        internal DungeonDifficultyPermissionService(
            IGameDatabase database,
            DailyResetService dailyReset)
        {
            _repository = new AccountDungeonPermissionRepository(database);
            _dailyReset = dailyReset;
        }

        internal IReadOnlyList<DungeonPermissionEntrySnapshot>
            BuildLoginPermissions(
                int accountId,
                IReadOnlyCollection<DungeonPermissionEntrySnapshot>
                    characterPermissions)
        {
            var result = new List<DungeonPermissionEntrySnapshot>();

            // Anton_Awakening 跨天重置（登录时检测 06:00 切日）
            if (accountId > 0 && _dailyReset != null)
            {
                try
                {
                    using (var conn = new SqliteConnection(
                        _repository.ConnectionStringForLoginReset))
                    {
                        conn.Open();
                        using (var tx = conn.BeginTransaction(deferred: false))
                        {
                            TryResetAntonAwakeningForAccount(accountId, conn, tx);
                            tx.Commit();
                        }
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[AntonAwakening] login reset failed account={accountId}: {ex.Message}");
                }
            }

            result.AddRange(LoadAccountPermissions(accountId));

            if (characterPermissions != null)
            {
                result.AddRange(characterPermissions.Where(permission =>
                    permission != null
                    && DungeonPermissionScopePolicy.Resolve(
                        permission.DungeonId)
                        == DungeonPermissionPersistenceScope.CharacterMechanism));
            }

            return DungeonPermissionProjector.ProjectForClient(result);
        }

        /// <summary>
        /// 跨天时（北京时间 06:00 切日）清空 account 下所有 character 的
        /// Anton_Awakening (243-247) 权限行。dungeon_limit_records 由 day_id 自然失效。
        /// 不抛异常：失败时返回 false 并由 BuildLoginPermissions 走原流程。
        /// </summary>
        internal bool TryResetAntonAwakeningForAccount(
            int accountId,
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            if (accountId <= 0 || connection == null || transaction == null)
                return false;

            if (_dailyReset == null)
                return false;

            bool applied;
            if (!_dailyReset.TryRunAccountFirstLoginReset(
                    connection,
                    transaction,
                    accountId,
                    DateTime.UtcNow,
                    resetAction: (conn, tx) =>
                    {
                        using (var cmd = new SqliteCommand(
                            @"
DELETE FROM character_dungeon_permissions
WHERE dungeon_id IN (243, 244, 245, 246, 247)
  AND character_id IN (
      SELECT character_id FROM characters WHERE account_id = @aid
  );",
                            conn,
                            tx))
                        {
                            cmd.Parameters.AddWithValue("@aid", accountId);
                            cmd.ExecuteNonQuery();
                        }
                        return true;
                    },
                    out applied))
            {
                return false;
            }

            return applied;
        }

        internal IReadOnlyList<DungeonPermissionEntrySnapshot>
            LoadAccountPermissions(int accountId)
        {
            if (accountId <= 0)
                return Array.Empty<DungeonPermissionEntrySnapshot>();

            var legacy = _repository.LoadLegacyByAccount(accountId)
                .Where(permission =>
                    DungeonPermissionScopePolicy.IsAccountDifficulty(
                        permission.DungeonId))
                .ToArray();
            if (legacy.Length > 0)
                _repository.ApplyBatch(accountId, legacy, out _);

            return _repository.Load(accountId)
                .Where(permission =>
                    DungeonPermissionScopePolicy.IsAccountDifficulty(
                        permission.DungeonId))
                .ToArray();
        }

        internal DungeonPermissionProgressionPlan BuildProgressionPlan(
            int accountId,
            int dungeonId,
            byte requestedClearState)
        {
            return DungeonPermissionProjector.BuildProgressionPlan(
                LoadAccountPermissions(accountId),
                dungeonId,
                requestedClearState);
        }

        internal IReadOnlyList<DungeonPermissionEntrySnapshot> ApplyBatch(
            int accountId,
            IReadOnlyCollection<DungeonPermissionEntrySnapshot> updates,
            out List<DungeonPermissionEntrySnapshot> changes)
        {
            var accountUpdates = (updates
                    ?? Array.Empty<DungeonPermissionEntrySnapshot>())
                .Where(permission =>
                    permission != null
                    && DungeonPermissionScopePolicy.IsAccountDifficulty(
                        permission.DungeonId))
                .ToArray();
            return _repository.ApplyBatch(
                accountId,
                accountUpdates,
                out changes);
        }
    }
}
