using System;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using DfoServer.Network;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Events.LoginReward
{
    internal sealed class LoginRewardService
    {
        private readonly IGameDatabase _database;
        private readonly MailboxService _mailbox;
        private readonly LoginRewardConfigProvider _configProvider;
        private readonly LoginRewardConfig _configOverride;
        private readonly LoginRewardRepository _repository;
        private readonly Func<DateTimeOffset> _nowProvider;

        internal LoginRewardService(
            IGameDatabase database,
            MailboxService mailbox,
            LoginRewardConfigProvider configProvider = null,
            LoginRewardConfig config = null,
            Func<DateTimeOffset> nowProvider = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
            _configProvider = configProvider ?? LoginRewardConfigProvider.Instance;
            _configOverride = config;
            _repository = new LoginRewardRepository(_database);
            _nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
        }

        private LoginRewardConfig CurrentConfig =>
            _configOverride ?? _configProvider.Current;

        internal void Initialize()
        {
            _repository.EnsureStaticConfigRows(CurrentConfig);
        }

        internal bool TryGetSnapshot(
            int accountId,
            int characterId,
            out LoginRewardSnapshot snapshot)
        {
            snapshot = null;
            if (accountId <= 0 || characterId <= 0)
                return false;

            try
            {
                var now = _nowProvider();
                var utcNow = NormalizeUtc(now);
                var dayId = DailyResetService.TodayId(utcNow);
                var today = CurrentConfig.FindRewardDay(utcNow.Date);
                LoginRewardSnapshot local = null;
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
                    var progress = _repository.LoadAccountProgress(
                        connection,
                        transaction,
                        accountId,
                        config);
                    var alreadyClaimed = progress.LastClaimDayId == dayId;
                    local = _repository.LoadSnapshot(
                        connection,
                        transaction,
                        accountId,
                        characterId,
                        config,
                        dayId,
                        today?.DayIndex ?? -1,
                        alreadyClaimed,
                        eventEnabled: true);
                });

                snapshot = local;
                return snapshot != null;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[LoginReward] snapshot failed "
                    + $"account_id={accountId} cid={characterId}: {ex}");
                return false;
            }
        }

        internal LoginRewardClaimResult ClaimTodayReward(
            int accountId,
            int characterId,
            string characterName,
            int characterLevel)
        {
            if (accountId <= 0 || characterId <= 0)
            {
                return new LoginRewardClaimResult
                {
                    Status = LoginRewardClaimStatus.CharacterUnavailable,
                };
            }

            try
            {
                var now = _nowProvider();
                var utcNow = NormalizeUtc(now);
                var dayId = DailyResetService.TodayId(utcNow);
                var today = CurrentConfig.FindRewardDay(utcNow.Date);
                var nowUnix = now.ToUnixTimeSeconds();
                LoginRewardClaimResult result = null;
                _database.Write((connection, transaction) =>
                {
                    var config = CurrentConfig;
                    if (!_repository.IsEnabled(connection, transaction))
                    {
                        result = new LoginRewardClaimResult
                        {
                            Status = LoginRewardClaimStatus.EventClosed,
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
                    if (today == null)
                    {
                        result = WithSnapshot(
                            connection,
                            transaction,
                            accountId,
                            characterId,
                            config,
                            dayId,
                            today?.DayIndex ?? -1,
                            nowUnix,
                            LoginRewardClaimStatus.NoRewardToday);
                        return;
                    }

                    if (!_repository.TryClaimToday(
                            connection,
                            transaction,
                            accountId,
                            config,
                            dayId,
                            today.DayIndex,
                            nowUnix))
                    {
                        result = WithSnapshot(
                            connection,
                            transaction,
                            accountId,
                            characterId,
                            config,
                            dayId,
                            today.DayIndex,
                            nowUnix,
                            LoginRewardClaimStatus.AlreadyClaimed);
                        return;
                    }

                    var mail = CreateRewardMail(
                        accountId,
                        characterId,
                        characterName,
                        characterLevel,
                        config.SeasonId,
                        dayId,
                        today);
                    var mailResult = _mailbox.SendSystemMails(
                        connection,
                        transaction,
                        new[] { mail });
                    if (!mailResult.Success)
                    {
                        throw new LoginRewardRollbackException(
                            WithSnapshot(
                                connection,
                                transaction,
                                accountId,
                                characterId,
                                config,
                                dayId,
                                today.DayIndex,
                                nowUnix,
                                LoginRewardClaimStatus.MailFailed));
                    }

                    result = WithSnapshot(
                        connection,
                        transaction,
                        accountId,
                        characterId,
                        config,
                        dayId,
                        today.DayIndex,
                        nowUnix,
                        LoginRewardClaimStatus.Claimed,
                        today,
                        mailDelivered: true);
                });

                return result ?? new LoginRewardClaimResult
                {
                    Status = LoginRewardClaimStatus.MailFailed,
                };
            }
            catch (LoginRewardRollbackException ex)
            {
                return ex.Result;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[LoginReward] claim failed "
                    + $"account_id={accountId} cid={characterId}: {ex}");
                return new LoginRewardClaimResult
                {
                    Status = LoginRewardClaimStatus.MailFailed,
                };
            }
        }

        private LoginRewardClaimResult WithSnapshot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int characterId,
            LoginRewardConfig config,
            int dayId,
            int todayDayIndex,
            long nowUnix,
            LoginRewardClaimStatus status,
            LoginRewardDay reward = null,
            bool mailDelivered = false)
        {
            return new LoginRewardClaimResult
            {
                Status = status,
                Snapshot = _repository.LoadSnapshot(
                    connection,
                    transaction,
                    accountId,
                    characterId,
                    config,
                    dayId,
                    todayDayIndex,
                    alreadyClaimed: status == LoginRewardClaimStatus.Claimed
                        || status == LoginRewardClaimStatus.AlreadyClaimed,
                    eventEnabled: true),
                MailDelivered = mailDelivered,
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
            LoginRewardDay reward)
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
                Title = "Login reward",
                Text = "Login reward has been delivered.",
                MailType = 1,
                SourceProtocol = (ushort)NotiPacketTypeA21.INTEGRATE_EVENT_DATA,
                Unlimited = true,
                IdempotencyKey =
                    $"event-login-reward:{seasonId}:{dayId}:{accountId}",
                AuditActor = "event-login-reward",
                AuditReason =
                    $"loginreward day {reward.DayIndex} item {reward.ItemId}",
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

        private sealed class LoginRewardRollbackException : Exception
        {
            internal LoginRewardRollbackException(LoginRewardClaimResult result)
                : base(result?.Status.ToString())
            {
                Result = result;
            }

            internal LoginRewardClaimResult Result { get; }
        }
    }
}
