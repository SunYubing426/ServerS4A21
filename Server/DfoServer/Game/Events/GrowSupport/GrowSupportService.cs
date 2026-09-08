using System;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using DfoServer.Network;

namespace DfoServer.Game.Events.GrowSupport
{
    internal sealed class GrowSupportService
    {
        private readonly IGameDatabase _database;
        private readonly MailboxService _mailbox;
        private readonly GrowSupportConfigProvider _configProvider;
        private readonly GrowSupportConfig _configOverride;
        private readonly GrowSupportRepository _repository;
        private readonly Func<DateTimeOffset> _nowProvider;

        internal GrowSupportService(
            IGameDatabase database,
            MailboxService mailbox,
            GrowSupportConfigProvider configProvider = null,
            GrowSupportConfig config = null,
            Func<DateTimeOffset> nowProvider = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
            _configProvider = configProvider ?? GrowSupportConfigProvider.Instance;
            _configOverride = config;
            _repository = new GrowSupportRepository(_database);
            _nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
        }

        private GrowSupportConfig CurrentConfig =>
            _configOverride ?? _configProvider.Current;

        internal void Initialize()
        {
            _repository.EnsureStaticConfigRows(CurrentConfig);
        }

        internal bool TryGetSnapshot(
            int accountId,
            int characterId,
            int characterLevel,
            out GrowSupportSnapshot snapshot)
        {
            snapshot = null;
            if (accountId <= 0 || characterId <= 0)
                return false;

            try
            {
                var nowUnix = _nowProvider().ToUnixTimeSeconds();
                GrowSupportSnapshot local = null;
                _database.Write((connection, transaction) =>
                {
                    var config = CurrentConfig;
                    if (!_repository.IsEnabled(connection, transaction))
                        return;

                    local = _repository.LoadSnapshot(
                        connection,
                        transaction,
                        accountId,
                        characterId,
                        config,
                        characterLevel,
                        eventEnabled: true);
                });

                snapshot = local;
                return snapshot != null;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[GrowSupport] snapshot failed "
                    + $"account_id={accountId} cid={characterId}: {ex}");
                return false;
            }
        }

        internal GrowSupportClaimResult ClaimLevelUpReward(
            int accountId,
            int characterId,
            string characterName,
            int characterLevel)
        {
            if (accountId <= 0 || characterId <= 0)
            {
                return new GrowSupportClaimResult
                {
                    Status = GrowSupportClaimStatus.CharacterUnavailable,
                };
            }

            try
            {
                var nowUnix = _nowProvider().ToUnixTimeSeconds();
                var config = CurrentConfig;
                var reward = config.FindLevelReward(characterLevel);
                if (reward == null)
                {
                    return new GrowSupportClaimResult
                    {
                        Status = GrowSupportClaimStatus.InvalidRequest,
                    };
                }

                return ClaimCore(
                    accountId,
                    characterId,
                    characterName,
                    characterLevel,
                    reward,
                    (connection, transaction) => _repository.TryClaimLevelReward(
                        connection,
                        transaction,
                        accountId,
                        characterId,
                        config,
                        characterLevel,
                        nowUnix),
                    "levelup");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[GrowSupport] level reward failed "
                    + $"account_id={accountId} cid={characterId} level={characterLevel}: {ex}");
                return new GrowSupportClaimResult
                {
                    Status = GrowSupportClaimStatus.MailFailed,
                };
            }
        }

        internal GrowSupportClaimResult ClaimDungeonClearReward(
            int accountId,
            int characterId,
            string characterName,
            int characterLevel,
            int requiredClears)
        {
            if (accountId <= 0 || characterId <= 0)
            {
                return new GrowSupportClaimResult
                {
                    Status = GrowSupportClaimStatus.CharacterUnavailable,
                };
            }

            try
            {
                var nowUnix = _nowProvider().ToUnixTimeSeconds();
                var config = CurrentConfig;
                var reward = config.FindDungeonReward(requiredClears);
                if (reward == null)
                {
                    return new GrowSupportClaimResult
                    {
                        Status = GrowSupportClaimStatus.InvalidRequest,
                    };
                }

                return ClaimCore(
                    accountId,
                    characterId,
                    characterName,
                    characterLevel,
                    reward,
                    (connection, transaction) => _repository.TryClaimDungeonReward(
                        connection,
                        transaction,
                        accountId,
                        characterId,
                        config,
                        requiredClears,
                        nowUnix),
                    "dungeon");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[GrowSupport] dungeon reward failed "
                    + $"account_id={accountId} cid={characterId} clears={requiredClears}: {ex}");
                return new GrowSupportClaimResult
                {
                    Status = GrowSupportClaimStatus.MailFailed,
                };
            }
        }

        internal void OnDungeonCleared(
            int accountId,
            int characterId)
        {
            if (accountId <= 0 || characterId <= 0)
                return;

            try
            {
                var nowUnix = _nowProvider().ToUnixTimeSeconds();
                var config = CurrentConfig;
                _database.Write((connection, transaction) =>
                {
                    if (!_repository.IsEnabled(connection, transaction))
                        return;

                    _repository.EnsureStateRows(
                        connection,
                        transaction,
                        accountId,
                        characterId,
                        config,
                        nowUnix);
                    _repository.IncrementDungeonClearCount(
                        connection,
                        transaction,
                        accountId,
                        characterId,
                        config,
                        nowUnix);
                });
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[GrowSupport] dungeon clear tracking failed "
                    + $"account_id={accountId} cid={characterId}: {ex}");
            }
        }

        private GrowSupportClaimResult ClaimCore(
            int accountId,
            int characterId,
            string characterName,
            int characterLevel,
            object reward,
            Func<Microsoft.Data.Sqlite.SqliteConnection,
                Microsoft.Data.Sqlite.SqliteTransaction,
                bool> tryClaim,
            string kind)
        {
            GrowSupportClaimResult result = null;
            var nowUnix = _nowProvider().ToUnixTimeSeconds();
            var config = CurrentConfig;
            _database.Write((connection, transaction) =>
            {
                if (!_repository.IsEnabled(connection, transaction))
                {
                    result = new GrowSupportClaimResult
                    {
                        Status = GrowSupportClaimStatus.EventClosed,
                    };
                    return;
                }

                _repository.EnsureStateRows(
                    connection,
                    transaction,
                    accountId,
                    characterId,
                    config,
                    nowUnix);
                if (!tryClaim(connection, transaction))
                {
                    result = WithSnapshot(
                        connection,
                        transaction,
                        accountId,
                        characterId,
                        config,
                        characterLevel,
                        GrowSupportClaimStatus.AlreadyClaimed);
                    return;
                }

                int itemId;
                int itemCount;
                string audit;
                if (reward is GrowSupportLevelReward levelReward)
                {
                    itemId = levelReward.ItemId;
                    itemCount = levelReward.ItemCount;
                    audit = $"level {levelReward.Level}";
                }
                else if (reward is GrowSupportDungeonReward dungeonReward)
                {
                    itemId = dungeonReward.ItemId;
                    itemCount = dungeonReward.ItemCount;
                    audit = $"dungeon {dungeonReward.RequiredClears}";
                }
                else
                {
                    result = WithSnapshot(
                        connection,
                        transaction,
                        accountId,
                        characterId,
                        config,
                        characterLevel,
                        GrowSupportClaimStatus.InvalidRequest);
                    return;
                }

                var mail = CreateRewardMail(
                    accountId,
                    characterId,
                    characterName,
                    characterLevel,
                    config.SeasonId,
                    itemId,
                    itemCount,
                    audit);
                var mailResult = _mailbox.SendSystemMails(
                    connection,
                    transaction,
                    new[] { mail });
                if (!mailResult.Success)
                {
                    throw new GrowSupportRollbackException(
                        WithSnapshot(
                            connection,
                            transaction,
                            accountId,
                            characterId,
                            config,
                            characterLevel,
                            GrowSupportClaimStatus.MailFailed));
                }

                result = WithSnapshot(
                    connection,
                    transaction,
                    accountId,
                    characterId,
                    config,
                    characterLevel,
                    GrowSupportClaimStatus.Claimed,
                    itemId,
                    itemCount,
                    mailDelivered: true);
            });

            return result ?? new GrowSupportClaimResult
            {
                Status = GrowSupportClaimStatus.MailFailed,
            };
        }

        private GrowSupportClaimResult WithSnapshot(
            Microsoft.Data.Sqlite.SqliteConnection connection,
            Microsoft.Data.Sqlite.SqliteTransaction transaction,
            int accountId,
            int characterId,
            GrowSupportConfig config,
            int characterLevel,
            GrowSupportClaimStatus status,
            int itemId = 0,
            int itemCount = 0,
            bool mailDelivered = false)
        {
            return new GrowSupportClaimResult
            {
                Status = status,
                Snapshot = _repository.LoadSnapshot(
                    connection,
                    transaction,
                    accountId,
                    characterId,
                    config,
                    characterLevel,
                    eventEnabled: true),
                MailDelivered = mailDelivered,
                ItemId = itemId,
                ItemCount = itemCount,
            };
        }

        private MailboxSendRequest CreateRewardMail(
            int accountId,
            int characterId,
            string characterName,
            int characterLevel,
            int seasonId,
            int itemId,
            int itemCount,
            string auditReason)
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
                Title = "Grow support reward",
                Text = "Grow support reward has been delivered.",
                MailType = 1,
                SourceProtocol = (ushort)NotiPacketTypeA21.INTEGRATE_EVENT_DATA,
                Unlimited = true,
                IdempotencyKey =
                    $"event-grow-support:{seasonId}:{accountId}:{characterId}:{auditReason}",
                AuditActor = "event-grow-support",
                AuditReason = auditReason,
                Attachments = new[]
                {
                    new MailboxSendAttachmentRequest
                    {
                        ItemType = ResolveMailboxItemType(itemId),
                        ItemId = itemId,
                        ItemCount = itemCount,
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

        private sealed class GrowSupportRollbackException : Exception
        {
            internal GrowSupportRollbackException(GrowSupportClaimResult result)
                : base(result?.Status.ToString())
            {
                Result = result;
            }

            internal GrowSupportClaimResult Result { get; }
        }
    }
}
