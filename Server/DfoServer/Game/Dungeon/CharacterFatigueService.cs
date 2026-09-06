using System;
using System.Collections.Generic;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.Game.Dungeon
{
    internal readonly struct CharacterFatigueTarget
    {
        internal CharacterFatigueTarget(int characterId, byte memberSlot)
        {
            CharacterId = characterId;
            MemberSlot = memberSlot;
        }

        internal int CharacterId { get; }

        internal byte MemberSlot { get; }
    }

    internal sealed class CharacterFatigueSnapshot
    {
        internal CharacterFatigueSnapshot(int used, int max)
        {
            Used = Math.Max(0, used);
            Max = max > 0 ? max : CharacterFatigueService.DefaultMaxFatigue;
        }

        internal int Used { get; }

        internal int Max { get; }

        internal int Remaining => Math.Max(0, Max - Used);
    }

    internal sealed class CharacterFatigueConsumeResult
    {
        private CharacterFatigueConsumeResult()
        {
        }

        internal bool Allowed { get; private set; }

        internal string Reason { get; private set; }

        internal byte MemberSlot { get; private set; }

        internal int Used { get; private set; }

        internal int Max { get; private set; }

        internal int Cost { get; private set; }

        internal static CharacterFatigueConsumeResult Allow(
            int cost,
            int used,
            int max,
            byte memberSlot = 0,
            string reason = null)
            => new CharacterFatigueConsumeResult
            {
                Allowed = true,
                Reason = reason
                    ?? (cost <= 0 ? "zero_cost" : "allowed"),
                MemberSlot = memberSlot,
                Used = used,
                Max = max,
                Cost = cost,
            };

        internal static CharacterFatigueConsumeResult Reject(
            string reason,
            byte memberSlot,
            int used = 0,
            int max = 0,
            int cost = 0)
            => new CharacterFatigueConsumeResult
            {
                Allowed = false,
                Reason = reason ?? "rejected",
                MemberSlot = memberSlot,
                Used = used,
                Max = max,
                Cost = cost,
            };
    }

    internal enum CharacterFatigueChargeMode
    {
        None = 0,
        PerVisitedRoom = 1,
        OnlyStartDungeon = 2,
    }

    internal readonly struct DungeonFatigueRoomCell : IEquatable<DungeonFatigueRoomCell>
    {
        internal DungeonFatigueRoomCell(int mazeIndex, int x, int y)
        {
            MazeIndex = mazeIndex;
            X = x;
            Y = y;
        }

        internal int MazeIndex { get; }

        internal int X { get; }

        internal int Y { get; }

        public bool Equals(DungeonFatigueRoomCell other)
            => MazeIndex == other.MazeIndex
                && X == other.X
                && Y == other.Y;

        public override bool Equals(object obj)
            => obj is DungeonFatigueRoomCell other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = MazeIndex * 397;
                hash = (hash ^ X) * 397;
                hash ^= Y;
                return hash;
            }
        }
    }

    internal sealed class CharacterFatigueService
    {
        internal const int DefaultMaxFatigue = 188;
        internal const int DefaultEntryCost = 1;

        private readonly string _connectionString;

        internal CharacterFatigueService(IGameDatabase database)
            : this(
                (database ?? throw new ArgumentNullException(nameof(database)))
                    .ConnectionString)
        {
        }

        internal CharacterFatigueService(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException(
                    "connectionString is empty",
                    nameof(connectionString));

            _connectionString = connectionString;
        }

        internal static int ResolveEntryCost(DungeonFile dungeon)
        {
            if (dungeon == null)
                return DefaultEntryCost;
            if (dungeon.NoFatigue || dungeon.EnterWithoutFatigue)
                return 0;
            if (dungeon.Fatigue >= 0)
                return dungeon.Fatigue;
            return DefaultEntryCost;
        }

        internal static CharacterFatigueChargeMode ResolveChargeMode(
            DungeonFile dungeon)
        {
            if (dungeon != null
                && (dungeon.NoFatigue || dungeon.EnterWithoutFatigue))
            {
                return CharacterFatigueChargeMode.None;
            }

            if (dungeon != null && dungeon.UseFatigueOnlyStartDungeon)
                return CharacterFatigueChargeMode.OnlyStartDungeon;

            return CharacterFatigueChargeMode.PerVisitedRoom;
        }

        internal static bool ChargesOnlyAtSelectDungeon(DungeonFile dungeon)
            => ResolveChargeMode(dungeon)
                == CharacterFatigueChargeMode.OnlyStartDungeon;

        internal static bool IsChargeableRoomCell(int cellX, int cellY)
            => cellX >= 0
                && cellY >= 0
                && (cellX != 0xFF || cellY != 0xFF);

        internal bool TryConsumeRoomVisit(
            DungeonRun run,
            IReadOnlyList<CharacterFatigueTarget> targets,
            int mazeIndex,
            int cellX,
            int cellY,
            DungeonFile dungeon,
            out CharacterFatigueConsumeResult result)
        {
            result = CharacterFatigueConsumeResult.Reject(
                "invalid_request",
                memberSlot: 0);
            if (run == null)
                return false;

            var mode = ResolveChargeMode(dungeon);
            var cost = ResolveEntryCost(dungeon);
            if (mode != CharacterFatigueChargeMode.PerVisitedRoom)
            {
                result = CharacterFatigueConsumeResult.Allow(
                    0,
                    0,
                    DefaultMaxFatigue,
                    reason: mode == CharacterFatigueChargeMode.OnlyStartDungeon
                        ? "only_start_dungeon"
                        : "zero_cost");
                return true;
            }

            if (!IsChargeableRoomCell(cellX, cellY))
            {
                result = CharacterFatigueConsumeResult.Allow(
                    0,
                    0,
                    DefaultMaxFatigue,
                    reason: "unchargeable_cell");
                return true;
            }

            if (!run.TryMarkFatigueRoomVisited(mazeIndex, cellX, cellY))
            {
                result = CharacterFatigueConsumeResult.Allow(
                    0,
                    0,
                    DefaultMaxFatigue,
                    reason: "revisit");
                return true;
            }

            if (!TryConsumeMany(targets, cost, out result))
            {
                run.TryUnmarkFatigueRoomVisited(mazeIndex, cellX, cellY);
                return false;
            }

            return true;
        }

        internal CharacterFatigueSnapshot Load(int characterId)
        {
            if (characterId <= 0)
                return new CharacterFatigueSnapshot(0, DefaultMaxFatigue);

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                return Load(connection, null, characterId);
            }
        }

        internal bool TryCheck(
            int characterId,
            int cost,
            out CharacterFatigueConsumeResult result)
            => TryCheckMany(
                new[] { new CharacterFatigueTarget(characterId, 0) },
                cost,
                out result);

        internal bool TryCheckMany(
            IReadOnlyList<CharacterFatigueTarget> targets,
            int cost,
            out CharacterFatigueConsumeResult result)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var ok = TryCheckMany(
                        connection,
                        transaction,
                        targets,
                        cost,
                        out result);
                    transaction.Commit();
                    return ok;
                }
            }
        }

        internal bool TryConsume(
            int characterId,
            int cost,
            out CharacterFatigueConsumeResult result)
            => TryConsumeMany(
                new[] { new CharacterFatigueTarget(characterId, 0) },
                cost,
                out result);

        internal bool TryConsumeMany(
            IReadOnlyList<CharacterFatigueTarget> targets,
            int cost,
            out CharacterFatigueConsumeResult result)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var ok = TryConsumeMany(
                        connection,
                        transaction,
                        targets,
                        cost,
                        out result);
                    if (ok)
                        transaction.Commit();
                    else
                        transaction.Rollback();
                    return ok;
                }
            }
        }

        internal bool ResetUsedForAccount(
            SqliteConnection conn,
            SqliteTransaction tx,
            int accountId)
        {
            if (conn == null || accountId <= 0)
                return false;

            using (var command = conn.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = @"
UPDATE characters
SET usedFatigue = 0,
    fatigue = CASE
        WHEN maxFatigue IS NULL OR maxFatigue <= 0 THEN @defaultMax
        ELSE maxFatigue
    END,
    updated_at = CURRENT_TIMESTAMP
WHERE account_id = @aid;";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@defaultMax", DefaultMaxFatigue);
                command.ExecuteNonQuery();
                return true;
            }
        }

        private static bool TryCheckMany(
            SqliteConnection connection,
            SqliteTransaction transaction,
            IReadOnlyList<CharacterFatigueTarget> targets,
            int cost,
            out CharacterFatigueConsumeResult result)
        {
            result = CharacterFatigueConsumeResult.Reject(
                "invalid_request",
                memberSlot: 0,
                cost: cost);
            if (targets == null || targets.Count == 0)
                return false;

            if (cost < 0)
            {
                result = CharacterFatigueConsumeResult.Reject(
                    "invalid_cost",
                    memberSlot: targets[0].MemberSlot,
                    cost: cost);
                return false;
            }

            CharacterFatigueSnapshot last = null;
            byte lastSlot = 0;
            foreach (var target in targets)
            {
                if (target.CharacterId <= 0)
                {
                    result = CharacterFatigueConsumeResult.Reject(
                        "invalid_request",
                        target.MemberSlot,
                        cost: cost);
                    return false;
                }

                var snapshot = Load(connection, transaction, target.CharacterId);
                last = snapshot;
                lastSlot = target.MemberSlot;
                if (cost > 0 && snapshot.Used + cost > snapshot.Max)
                {
                    result = CharacterFatigueConsumeResult.Reject(
                        "insufficient_fatigue",
                        target.MemberSlot,
                        snapshot.Used,
                        snapshot.Max,
                        cost);
                    return false;
                }
            }

            result = CharacterFatigueConsumeResult.Allow(
                cost,
                last?.Used ?? 0,
                last?.Max ?? DefaultMaxFatigue,
                lastSlot);
            return true;
        }

        private static bool TryConsumeMany(
            SqliteConnection connection,
            SqliteTransaction transaction,
            IReadOnlyList<CharacterFatigueTarget> targets,
            int cost,
            out CharacterFatigueConsumeResult result)
        {
            if (!TryCheckMany(
                    connection,
                    transaction,
                    targets,
                    cost,
                    out result))
            {
                return false;
            }

            if (cost == 0)
                return true;

            CharacterFatigueSnapshot last = null;
            byte lastSlot = 0;
            foreach (var target in targets)
            {
                if (!TryIncrementUsed(
                        connection,
                        transaction,
                        target.CharacterId,
                        cost))
                {
                    result = CharacterFatigueConsumeResult.Reject(
                        "insufficient_fatigue",
                        target.MemberSlot,
                        cost: cost);
                    return false;
                }

                last = Load(connection, transaction, target.CharacterId);
                lastSlot = target.MemberSlot;
            }

            result = CharacterFatigueConsumeResult.Allow(
                cost,
                last?.Used ?? 0,
                last?.Max ?? DefaultMaxFatigue,
                lastSlot);
            return true;
        }

        private static CharacterFatigueSnapshot Load(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT COALESCE(usedFatigue, 0), COALESCE(maxFatigue, @defaultMax)
FROM characters
WHERE character_id = @cid;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@defaultMax", DefaultMaxFatigue);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return new CharacterFatigueSnapshot(0, DefaultMaxFatigue);

                    return new CharacterFatigueSnapshot(
                        reader.GetInt32(0),
                        reader.GetInt32(1));
                }
            }
        }

        private static bool TryIncrementUsed(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int cost)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE characters
SET usedFatigue = usedFatigue + @cost,
    fatigue = CASE
        WHEN maxFatigue - (usedFatigue + @cost) < 0 THEN 0
        ELSE maxFatigue - (usedFatigue + @cost)
    END,
    updated_at = CURRENT_TIMESTAMP
WHERE character_id = @cid
  AND usedFatigue + @cost <= CASE
        WHEN maxFatigue IS NULL OR maxFatigue <= 0 THEN @defaultMax
        ELSE maxFatigue
      END;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@cost", cost);
                command.Parameters.AddWithValue("@defaultMax", DefaultMaxFatigue);
                return command.ExecuteNonQuery() == 1;
            }
        }
    }
}
