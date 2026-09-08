using System;
using System.Collections.Generic;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using DfoServer.Network;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Events.OnlineAttendance
{
    internal sealed class OnlineAttendanceService
    {
        private sealed class SessionOnlineTracker
        {
            public Guid SessionId;
            public int AccountId;
            public int CharacterId;
        }

        private sealed class AccountOnlineTracker
        {
            public int AccountId;
            public DateTime LastFlushUtc;
            public HashSet<Guid> SessionIds { get; } = new HashSet<Guid>();
        }

        private readonly IGameDatabase _database;
        private readonly MailboxService _mailbox;
        private readonly OnlineAttendanceConfigProvider _configProvider;
        private readonly OnlineAttendanceConfig _configOverride;
        private readonly OnlineAttendanceRepository _repository;
        private readonly Func<DateTimeOffset> _nowProvider;
        private readonly object _sync = new object();
        private readonly Dictionary<Guid, SessionOnlineTracker> _sessionsById =
            new Dictionary<Guid, SessionOnlineTracker>();
        private readonly Dictionary<int, AccountOnlineTracker> _trackersByAccount =
            new Dictionary<int, AccountOnlineTracker>();

        internal OnlineAttendanceService(
            IGameDatabase database,
            MailboxService mailbox,
            OnlineAttendanceConfigProvider configProvider = null,
            OnlineAttendanceConfig config = null,
            Func<DateTimeOffset> nowProvider = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
            _configProvider = configProvider ?? OnlineAttendanceConfigProvider.Instance;
            _configOverride = config;
            _repository = new OnlineAttendanceRepository(_database);
            _nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
        }

        private OnlineAttendanceConfig CurrentConfig =>
            _configOverride ?? _configProvider.Current;

        internal void Initialize()
        {
            _repository.EnsureStaticConfigRows(CurrentConfig);
        }

        internal void BeginSession(
            Guid sessionId,
            int accountId,
            int characterId)
        {
            if (sessionId == Guid.Empty || accountId <= 0 || characterId <= 0)
                return;

            lock (_sync)
            {
                if (_sessionsById.TryGetValue(sessionId, out var existing))
                {
                    if (existing.AccountId == accountId)
                    {
                        existing.CharacterId = characterId;
                        return;
                    }

                    RemoveSessionLocked(existing);
                }

                var nowUtc = NormalizeUtc(_nowProvider());
                if (!_trackersByAccount.TryGetValue(accountId, out var tracker))
                {
                    tracker = new AccountOnlineTracker
                    {
                        AccountId = accountId,
                        LastFlushUtc = nowUtc,
                    };
                    _trackersByAccount[accountId] = tracker;
                }

                tracker.SessionIds.Add(sessionId);
                _sessionsById[sessionId] = new SessionOnlineTracker
                {
                    SessionId = sessionId,
                    AccountId = accountId,
                    CharacterId = characterId,
                };
            }
        }

        internal void EndSession(Guid sessionId)
        {
            if (sessionId == Guid.Empty)
                return;

            lock (_sync)
            {
                if (!_sessionsById.TryGetValue(sessionId, out var tracker))
                    return;

                RemoveSessionLocked(tracker);
                if (!_trackersByAccount.TryGetValue(
                        tracker.AccountId,
                        out var accountTracker))
                {
                    return;
                }

                accountTracker.SessionIds.Remove(sessionId);
                if (accountTracker.SessionIds.Count > 0)
                    return;

                try
                {
                    var now = _nowProvider();
                    _database.Write((connection, transaction) =>
                    {
                        var config = CurrentConfig;
                        if (_repository.IsEnabled(connection, transaction))
                        {
                            FlushElapsedLocked(
                                connection,
                                transaction,
                                accountTracker,
                                config,
                                now);
                        }
                    });
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        "[OnlineAttendance] end-session flush failed "
                        + $"account_id={tracker.AccountId}: {ex}");
                }
                finally
                {
                    _trackersByAccount.Remove(tracker.AccountId);
                }
            }
        }

        internal bool TryGetSnapshot(
            int accountId,
            int characterId,
            out OnlineAttendanceSnapshot snapshot)
        {
            snapshot = null;
            if (accountId <= 0 || characterId <= 0)
                return false;

            try
            {
                var now = _nowProvider();
                var utcNow = NormalizeUtc(now);
                var dayId = DailyResetService.TodayId(utcNow);
                long flushedSeconds = 0;
                lock (_sync)
                {
                    if (_trackersByAccount.TryGetValue(accountId, out var tracker))
                    {
                        flushedSeconds = (long)(utcNow - tracker.LastFlushUtc)
                            .TotalSeconds;
                        if (flushedSeconds > 0)
                            tracker.LastFlushUtc = utcNow;
                    }
                }

                OnlineAttendanceSnapshot local = null;
                _database.Write((connection, transaction) =>
                {
                    var config = CurrentConfig;
                    if (!_repository.IsEnabled(connection, transaction))
                        return;

                    _repository.EnsureStateRows(
                        connection,
                        transaction,
                        accountId,
                        config,
                        dayId,
                        now.ToUnixTimeSeconds());
                    if (flushedSeconds > 0)
                    {
                        _repository.AddOnlineSeconds(
                            connection,
                            transaction,
                            accountId,
                            config,
                            dayId,
                            flushedSeconds,
                            now.ToUnixTimeSeconds());
                    }

                    local = _repository.LoadSnapshot(
                        connection,
                        transaction,
                        accountId,
                        characterId,
                        config,
                        dayId,
                        eventEnabled: true);
                });

                snapshot = local;
                return snapshot != null;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[OnlineAttendance] snapshot failed "
                    + $"account_id={accountId} cid={characterId}: {ex}");
                return false;
            }
        }

        internal OnlineAttendanceClaimResult ClaimTimeReward(
            int accountId,
            int characterId,
            string characterName,
            int characterLevel,
            int stageIndex)
        {
            if (accountId <= 0 || characterId <= 0)
            {
                return new OnlineAttendanceClaimResult
                {
                    Status = OnlineAttendanceClaimStatus.CharacterUnavailable,
                };
            }

            try
            {
                var now = _nowProvider();
                var utcNow = NormalizeUtc(now);
                var dayId = DailyResetService.TodayId(utcNow);
                var nowUnix = now.ToUnixTimeSeconds();
                OnlineAttendanceClaimResult result = null;
                _database.Write((connection, transaction) =>
                {
                    var config = CurrentConfig;
                    if (!_repository.IsEnabled(connection, transaction))
                    {
                        result = new OnlineAttendanceClaimResult
                        {
                            Status = OnlineAttendanceClaimStatus.EventClosed,
                        };
                        return;
                    }

                    _repository.EnsureStateRows(
                        connection,
                        transaction,
                        accountId,
                        config,
                        dayId,
                        nowUnix);
                    var reward = config.GetTimeReward(stageIndex);
                    if (reward == null)
                    {
                        result = WithSnapshot(
                            connection,
                            transaction,
                            accountId,
                            characterId,
                            config,
                            dayId,
                            nowUnix,
                            OnlineAttendanceClaimStatus.InvalidStage);
                        return;
                    }

                    var daily = _repository.LoadDailyProgress(
                        connection,
                        transaction,
                        accountId,
                        config,
                        dayId);
                    if (daily.OnlineSeconds < reward.RequiredSeconds)
                    {
                        result = WithSnapshot(
                            connection,
                            transaction,
                            accountId,
                            characterId,
                            config,
                            dayId,
                            nowUnix,
                            OnlineAttendanceClaimStatus.NotReady);
                        return;
                    }

                    if (!_repository.TryClaimTimeReward(
                            connection,
                            transaction,
                            accountId,
                            config,
                            dayId,
                            stageIndex,
                            reward.RequiredSeconds,
                            nowUnix))
                    {
                        result = WithSnapshot(
                            connection,
                            transaction,
                            accountId,
                            characterId,
                            config,
                            dayId,
                            nowUnix,
                            OnlineAttendanceClaimStatus.AlreadyClaimed);
                        return;
                    }

                    var mail = CreateRewardMail(
                        accountId,
                        characterId,
                        characterName,
                        characterLevel,
                        config.SeasonId,
                        dayId,
                        reward,
                        "time");
                    var mailResult = _mailbox.SendSystemMails(
                        connection,
                        transaction,
                        new[] { mail });
                    if (!mailResult.Success)
                    {
                        throw new OnlineAttendanceRollbackException(
                            WithSnapshot(
                                connection,
                                transaction,
                                accountId,
                                characterId,
                                config,
                                dayId,
                                nowUnix,
                                OnlineAttendanceClaimStatus.MailFailed,
                                reward));
                    }

                    result = WithSnapshot(
                        connection,
                        transaction,
                        accountId,
                        characterId,
                        config,
                        dayId,
                        nowUnix,
                        OnlineAttendanceClaimStatus.Success,
                        reward,
                        mailDelivered: true);
                });

                return result ?? new OnlineAttendanceClaimResult
                {
                    Status = OnlineAttendanceClaimStatus.MailFailed,
                };
            }
            catch (OnlineAttendanceRollbackException ex)
            {
                return ex.Result;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[OnlineAttendance] claim failed "
                    + $"account_id={accountId} cid={characterId} stage={stageIndex}: {ex}");
                return new OnlineAttendanceClaimResult
                {
                    Status = OnlineAttendanceClaimStatus.MailFailed,
                };
            }
        }

        internal OnlineAttendanceClaimResult ClaimSumReward(
            int accountId,
            int characterId,
            string characterName,
            int characterLevel,
            int stageIndex)
        {
            if (accountId <= 0 || characterId <= 0)
            {
                return new OnlineAttendanceClaimResult
                {
                    Status = OnlineAttendanceClaimStatus.CharacterUnavailable,
                };
            }

            try
            {
                var now = _nowProvider();
                var utcNow = NormalizeUtc(now);
                var dayId = DailyResetService.TodayId(utcNow);
                var nowUnix = now.ToUnixTimeSeconds();
                OnlineAttendanceClaimResult result = null;
                _database.Write((connection, transaction) =>
                {
                    var config = CurrentConfig;
                    if (!_repository.IsEnabled(connection, transaction))
                    {
                        result = new OnlineAttendanceClaimResult
                        {
                            Status = OnlineAttendanceClaimStatus.EventClosed,
                        };
                        return;
                    }

                    _repository.EnsureStateRows(
                        connection,
                        transaction,
                        accountId,
                        config,
                        dayId,
                        nowUnix);
                    var reward = config.GetSumReward(stageIndex);
                    if (reward == null)
                    {
                        result = WithSnapshot(
                            connection,
                            transaction,
                            accountId,
                            characterId,
                            config,
                            dayId,
                            nowUnix,
                            OnlineAttendanceClaimStatus.InvalidStage);
                        return;
                    }

                    var account = _repository.LoadAccountProgress(
                        connection,
                        transaction,
                        accountId,
                        config);
                    if (account.SumCompletedCount < reward.RequiredSeconds)
                    {
                        result = WithSnapshot(
                            connection,
                            transaction,
                            accountId,
                            characterId,
                            config,
                            dayId,
                            nowUnix,
                            OnlineAttendanceClaimStatus.NotReady);
                        return;
                    }

                    if (!_repository.TryClaimSumReward(
                            connection,
                            transaction,
                            accountId,
                            config,
                            stageIndex,
                            (int)reward.RequiredSeconds,
                            nowUnix))
                    {
                        result = WithSnapshot(
                            connection,
                            transaction,
                            accountId,
                            characterId,
                            config,
                            dayId,
                            nowUnix,
                            OnlineAttendanceClaimStatus.AlreadyClaimed);
                        return;
                    }

                    var mail = CreateRewardMail(
                        accountId,
                        characterId,
                        characterName,
                        characterLevel,
                        config.SeasonId,
                        dayId,
                        reward,
                        "sum");
                    var mailResult = _mailbox.SendSystemMails(
                        connection,
                        transaction,
                        new[] { mail });
                    if (!mailResult.Success)
                    {
                        throw new OnlineAttendanceRollbackException(
                            WithSnapshot(
                                connection,
                                transaction,
                                accountId,
                                characterId,
                                config,
                                dayId,
                                nowUnix,
                                OnlineAttendanceClaimStatus.MailFailed,
                                reward));
                    }

                    result = WithSnapshot(
                        connection,
                        transaction,
                        accountId,
                        characterId,
                        config,
                        dayId,
                        nowUnix,
                        OnlineAttendanceClaimStatus.Success,
                        reward,
                        mailDelivered: true);
                });

                return result ?? new OnlineAttendanceClaimResult
                {
                    Status = OnlineAttendanceClaimStatus.MailFailed,
                };
            }
            catch (OnlineAttendanceRollbackException ex)
            {
                return ex.Result;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[OnlineAttendance] sum claim failed "
                    + $"account_id={accountId} cid={characterId} stage={stageIndex}: {ex}");
                return new OnlineAttendanceClaimResult
                {
                    Status = OnlineAttendanceClaimStatus.MailFailed,
                };
            }
        }

        internal void OnDailyCycleCompleted(
            int accountId,
            int characterId,
            string characterName,
            int characterLevel)
        {
            if (accountId <= 0 || characterId <= 0)
                return;

            try
            {
                var now = _nowProvider();
                var nowUnix = now.ToUnixTimeSeconds();
                _database.Write((connection, transaction) =>
                {
                    var config = CurrentConfig;
                    if (!_repository.IsEnabled(connection, transaction))
                        return;

                    _repository.IncrementSumCompletedCount(
                        connection,
                        transaction,
                        accountId,
                        config,
                        nowUnix);
                });
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[OnlineAttendance] cycle completion failed "
                    + $"account_id={accountId} cid={characterId}: {ex}");
            }
        }

        private void RemoveSessionLocked(SessionOnlineTracker tracker)
        {
            _sessionsById.Remove(tracker.SessionId);
            if (_trackersByAccount.TryGetValue(tracker.AccountId, out var account))
                account.SessionIds.Remove(tracker.SessionId);
        }

        private void FlushElapsedLocked(
            SqliteConnection connection,
            SqliteTransaction transaction,
            AccountOnlineTracker tracker,
            OnlineAttendanceConfig config,
            DateTimeOffset now)
        {
            var utcNow = NormalizeUtc(now);
            var elapsed = (long)(utcNow - tracker.LastFlushUtc).TotalSeconds;
            if (elapsed <= 0)
                return;

            tracker.LastFlushUtc = utcNow;
            var dayId = DailyResetService.TodayId(utcNow);
            var nowUnix = now.ToUnixTimeSeconds();
            _repository.EnsureStateRows(
                connection,
                transaction,
                tracker.AccountId,
                config,
                dayId,
                nowUnix);
            _repository.AddOnlineSeconds(
                connection,
                transaction,
                tracker.AccountId,
                config,
                dayId,
                elapsed,
                nowUnix);
        }

        private OnlineAttendanceClaimResult WithSnapshot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int characterId,
            OnlineAttendanceConfig config,
            int dayId,
            long nowUnix,
            OnlineAttendanceClaimStatus status,
            OnlineAttendanceRewardStage reward = null,
            bool mailDelivered = false)
        {
            return new OnlineAttendanceClaimResult
            {
                Status = status,
                Snapshot = _repository.LoadSnapshot(
                    connection,
                    transaction,
                    accountId,
                    characterId,
                    config,
                    dayId,
                    eventEnabled: true),
                MailDelivered = mailDelivered,
                ClaimedStageIndex = reward?.StageIndex ?? -1,
                ItemId = reward?.ItemId ?? 0,
                ItemCount = reward?.ItemCount ?? 0,
            };
        }

        private static MailboxSendRequest CreateRewardMail(
            int accountId,
            int characterId,
            string characterName,
            int characterLevel,
            int seasonId,
            int dayId,
            OnlineAttendanceRewardStage reward,
            string kind)
        {
            return new MailboxSendRequest
            {
                SenderCharacterId = characterId,
                SenderAccountId = accountId,
                SenderName = "DNFadmin",
                ReceiverCharacterId = characterId,
                ReceiverAccountId = accountId,
                ReceiverName = characterName ?? string.Empty,
                SenderLevel = characterLevel,
                ReceiverLevel = characterLevel,
                Gold = 0,
                Title = "Online attendance reward",
                Text = "Online attendance reward has been delivered.",
                MailType = 1,
                SourceProtocol = (ushort)NotiPacketTypeA21.INTEGRATE_EVENT_DATA,
                Unlimited = true,
                IdempotencyKey =
                    $"event-online-attendance:{seasonId}:{dayId}:"
                    + $"{accountId}:{kind}:{reward.StageIndex}",
                AuditActor = "event-online-attendance",
                AuditReason =
                    $"onlineattendance {kind} reward {reward.StageIndex}",
                Attachments = new[]
                {
                    new MailboxSendAttachmentRequest
                    {
                        ItemType = ResolveMailboxItemType(reward.ItemId),
                        ItemId = reward.ItemId,
                        ItemCount = reward.ItemCount,
                    },
                },
            };
        }

        private static byte ResolveMailboxItemType(int itemId)
        {
            if (!ItemMetadataResolver.TryResolveItemKind(itemId, out var itemKind))
                return 0;

            switch (itemKind)
            {
                case ItemCore.KindAvatar:
                    return 1;
                case ItemCore.KindCreature:
                case ItemCore.KindCreatureEquipment:
                case ItemCore.KindCreatureConsumable:
                    return 3;
                default:
                    return 0;
            }
        }

        private static DateTime NormalizeUtc(DateTimeOffset time)
            => time.UtcDateTime;

        private sealed class OnlineAttendanceRollbackException : Exception
        {
            internal OnlineAttendanceRollbackException(
                OnlineAttendanceClaimResult result)
                : base(result?.Status.ToString())
            {
                Result = result;
            }

            internal OnlineAttendanceClaimResult Result { get; }
        }
    }
}
