using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Premium;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Dungeon
{
    internal readonly struct DungeonFatigueSnapshot
    {
        internal DungeonFatigueSnapshot(ushort used, ushort limit)
        {
            Used = used;
            Limit = limit;
        }

        internal ushort Used { get; }
        internal ushort Limit { get; }
        internal ushort Remaining => (ushort)Math.Max(0, Limit - Used);
    }

    internal enum DungeonFatigueConsumeStatus
    {
        Consumed = 0,
        QuotaExhausted = 1,
        Failed = 2,
    }

    internal readonly struct DungeonFatigueConsumeResult
    {
        internal DungeonFatigueConsumeResult(
            DungeonFatigueConsumeStatus status,
            DungeonFatigueSnapshot state,
            bool mailDelivered = false)
        {
            Status = status;
            State = state;
            MailDelivered = mailDelivered;
        }

        internal DungeonFatigueConsumeStatus Status { get; }
        internal DungeonFatigueSnapshot State { get; }
        internal bool MailDelivered { get; }
        internal bool Consumed => Status == DungeonFatigueConsumeStatus.Consumed;
        internal bool Failed => Status == DungeonFatigueConsumeStatus.Failed;
    }

    // 普通副本每个首次访问的房间消耗 1 点。额度按角色记账；黑钻资格按账号判定。
    // 日界沿用全服统一的北京时间 06:00。
    // dungeon_fatigue_used 只记录真实进房消耗，只增不减。
    // dungeon_fatigue_recovered 记录药剂恢复；对外 Used = clamp(rawUsed - recovered, 0, limit)。
    internal sealed class DungeonFatigueService
    {
        internal const string DailyCounterKey = "dungeon_fatigue_used";
        internal const string RecoveredCounterKey = "dungeon_fatigue_recovered";
        internal const ushort StandardLimit = 156;
        internal const ushort BlackDiamondLimit = 188;

        private readonly string _connectionString;
        private readonly DailyResetService _dailyReset;
        private readonly BlackDiamondFatigueCoinService _fatigueCoins;
        private readonly Func<DateTime> _utcNow;

        internal DungeonFatigueService(IGameDatabase database)
            : this(database, utcNow: null)
        {
        }

        internal DungeonFatigueService(IGameDatabase database, Func<DateTime> utcNow)
            : this(
                database,
                new BlackDiamondFatigueCoinService(
                    new MailboxService(new MailboxRepository(database))),
                utcNow)
        {
        }

        internal DungeonFatigueService(
            IGameDatabase database,
            BlackDiamondFatigueCoinService fatigueCoins)
            : this(database, fatigueCoins, utcNow: null)
        {
        }

        internal DungeonFatigueService(
            IGameDatabase database,
            BlackDiamondFatigueCoinService fatigueCoins,
            Func<DateTime> utcNow)
        {
            var resolved = database ?? throw new ArgumentNullException(nameof(database));
            _connectionString = resolved.ConnectionString;
            _dailyReset = new DailyResetService(resolved);
            _fatigueCoins = fatigueCoins
                ?? throw new ArgumentNullException(nameof(fatigueCoins));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        internal DateTime UtcNow() => _utcNow();

        internal static ushort ResolveLimit(IEnumerable<AckPremiumEntrySnapshot> premiums)
            => premiums != null && premiums.Any(p =>
                    p != null && p.PremiumType == PremiumService.BlackDiamondPremiumType)
                ? BlackDiamondLimit
                : StandardLimit;

        internal ushort ResolveLimit(int accountId)
            => accountId > 0
                && PremiumService.HasActiveBlackDiamond(
                    _connectionString,
                    accountId)
                ? BlackDiamondLimit
                : StandardLimit;

        internal static ushort ProjectUsed(long rawUsed, long recovered, ushort limit)
        {
            if (rawUsed < 0)
                rawUsed = 0;
            if (recovered < 0)
                recovered = 0;
            var unclamped = rawUsed - recovered;
            if (unclamped < 0)
                unclamped = 0;
            if (unclamped > limit)
                unclamped = limit;
            return (ushort)unclamped;
        }

        internal DungeonFatigueSnapshot GetSnapshot(int characterId, ushort limit)
        {
            if (characterId <= 0)
                return new DungeonFatigueSnapshot(0, limit);

            var utcNow = _utcNow();
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var snapshot = ReadProjectedSnapshot(
                        connection,
                        transaction,
                        characterId,
                        limit,
                        utcNow);
                    transaction.Commit();
                    return snapshot;
                }
            }
        }

        internal DungeonFatigueSnapshot GetSnapshot(int characterId, int accountId)
            => GetSnapshot(characterId, ResolveLimit(accountId));

        internal bool CanEnter(int characterId, int accountId, out DungeonFatigueSnapshot state)
        {
            state = GetSnapshot(characterId, accountId);
            return state.Remaining > 0;
        }

        internal bool TryConsumeRoom(
            int characterId,
            int accountId,
            out DungeonFatigueSnapshot state)
        {
            var result = ConsumeRoom(characterId, accountId);
            state = result.State;
            return result.Consumed;
        }

        internal DungeonFatigueConsumeResult ConsumeRoom(
            int characterId,
            int accountId)
        {
            var capturedUsed = (ushort)0;
            var capturedLimit = StandardLimit;
            var capturedState = false;
            if (characterId <= 0 || accountId <= 0)
            {
                return new DungeonFatigueConsumeResult(
                    DungeonFatigueConsumeStatus.Failed,
                    new DungeonFatigueSnapshot(capturedUsed, capturedLimit));
            }

            try
            {
                var utcNow = _utcNow();
                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        if (!CharacterBelongsToAccount(
                                connection,
                                transaction,
                                characterId,
                                accountId))
                        {
                            return new DungeonFatigueConsumeResult(
                                DungeonFatigueConsumeStatus.Failed,
                                new DungeonFatigueSnapshot(capturedUsed, capturedLimit));
                        }

                        var limit = ResolveLimit(
                            connection,
                            transaction,
                            accountId,
                            utcNow);
                        capturedLimit = limit;
                        var rawUsedBefore = ReadNonNegativeCounter(
                            connection,
                            transaction,
                            characterId,
                            DailyCounterKey,
                            utcNow);
                        var recovered = ReadNonNegativeCounter(
                            connection,
                            transaction,
                            characterId,
                            RecoveredCounterKey,
                            utcNow);
                        capturedUsed = ProjectUsed(rawUsedBefore, recovered, limit);
                        capturedState = true;
                        if (capturedUsed >= limit)
                        {
                            transaction.Commit();
                            return new DungeonFatigueConsumeResult(
                                DungeonFatigueConsumeStatus.QuotaExhausted,
                                new DungeonFatigueSnapshot(capturedUsed, limit));
                        }

                        var cap = (long)limit + recovered;
                        var consumed = DailyResetService.TryAddCounterAtomic(
                            connection,
                            transaction,
                            characterId,
                            DailyCounterKey,
                            1,
                            cap,
                            DailyResetService.PeriodDay,
                            utcNow);
                        var rawUsedAfter = ReadNonNegativeCounter(
                            connection,
                            transaction,
                            characterId,
                            DailyCounterKey,
                            utcNow);
                        var mailDelivered = false;
                        if (consumed
                            && rawUsedBefore < BlackDiamondFatigueCoinService.FatigueThreshold
                            && rawUsedAfter >= BlackDiamondFatigueCoinService.FatigueThreshold)
                        {
                            var judged = _fatigueCoins.TryJudgeCrossing(
                                connection,
                                transaction,
                                _dailyReset,
                                characterId,
                                accountId,
                                utcNow);
                            if (!judged.Success)
                            {
                                return new DungeonFatigueConsumeResult(
                                    DungeonFatigueConsumeStatus.Failed,
                                    new DungeonFatigueSnapshot(capturedUsed, limit));
                            }

                            mailDelivered = judged.MailDelivered;
                        }

                        transaction.Commit();
                        var state = new DungeonFatigueSnapshot(
                            ProjectUsed(rawUsedAfter, recovered, limit),
                            limit);
                        return new DungeonFatigueConsumeResult(
                            consumed
                                ? DungeonFatigueConsumeStatus.Consumed
                                : DungeonFatigueConsumeStatus.QuotaExhausted,
                            state,
                            mailDelivered);
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[DungeonFatigueService] ConsumeRoom failed: " +
                    "cid=" + characterId +
                    " aid=" + accountId +
                    " error=" + ex.Message);
                return new DungeonFatigueConsumeResult(
                    DungeonFatigueConsumeStatus.Failed,
                    new DungeonFatigueSnapshot(
                        capturedState ? capturedUsed : (ushort)0,
                        capturedLimit));
            }
        }

        private DungeonFatigueSnapshot ReadProjectedSnapshot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            ushort limit,
            DateTime utcNow)
        {
            var rawUsed = ReadNonNegativeCounter(
                connection,
                transaction,
                characterId,
                DailyCounterKey,
                utcNow);
            var recovered = ReadNonNegativeCounter(
                connection,
                transaction,
                characterId,
                RecoveredCounterKey,
                utcNow);
            return new DungeonFatigueSnapshot(
                ProjectUsed(rawUsed, recovered, limit),
                limit);
        }

        private long ReadNonNegativeCounter(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            string key,
            DateTime utcNow)
        {
            var value = _dailyReset.GetCounter(
                connection,
                transaction,
                characterId,
                key,
                utcNow);
            return value < 0 ? 0 : value;
        }

        private static ushort ResolveLimit(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            DateTime utcNow)
            => accountId > 0
                && PremiumService.HasActiveBlackDiamond(
                    connection,
                    transaction,
                    accountId,
                    utcNow)
                ? BlackDiamondLimit
                : StandardLimit;

        private static bool CharacterBelongsToAccount(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT 1
FROM characters
WHERE character_id = @cid
  AND account_id = @aid
  AND delete_flag = 0
LIMIT 1;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@aid", accountId);
                return command.ExecuteScalar() != null;
            }
        }
    }
}
