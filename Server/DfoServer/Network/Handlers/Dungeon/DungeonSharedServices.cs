using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.DailyAttendanceAnytime;
using DfoServer.Game.Events.RecommendedDungeons;
using DfoServer.Game.Events.TotalAttendance;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mercenary;
using DfoServer.Game.Progression;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using PvfLib;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    // Dungeon composition root. Runtime behavior belongs to the exposed
    // application services/projectors, not to this object.
    internal sealed class DungeonSharedServices
    {
        internal const string ProtocolLogName = "GameProtocol";

        internal string ConnectionString { get; }
        internal IGameDatabase Database { get; }
        internal SqliteSelectCharacterDataSource SelectCharacterDataSource { get; }
        internal IRentalTimeProvider RentalTimeProvider { get; }
        internal InventoryRefreshSender InventoryRefresh { get; }
        internal IMercenaryRestrictionService MercenaryRestrictions { get; }

        internal Game.ReviveCoin.ReviveCoinService ReviveCoin { get; }
        internal DeathTowerCoordinator DeathTower { get; }
        internal Game.Quests.QuestDropService QuestDrops { get; }
        internal Game.Quests.DailyChallengeService DailyChallenges { get; }
        internal RecommendDungeonClearStatsService RecommendDungeonClears { get; }
        internal DailyAttendanceAnytimeService DailyAttendanceAnytime { get; }
        internal TotalAttendanceService TotalAttendance { get; }
        internal Game.Dungeon.DungeonItemAcquisitionService ItemAcquisition { get; }
        internal DungeonPersistentMechanismCoordinator PersistentMechanisms { get; }
        internal SqliteCharacterRepository CharacterRepository { get; }
        internal SqliteSubtype1Repository Subtype1Repository { get; }
        internal SqliteCharacterStateRepository CharacterStateRepository { get; }
        internal Game.Dungeon.DungeonDifficultyPermissionService
            DungeonDifficultyPermissions { get; }
        internal SqliteCharacterProgressRepository ProgressRepository { get; }
        internal SqliteSubtype0FieldsRepository Subtype0FieldsRepository { get; }
        internal HonorLevelSyncService HonorLevel { get; }
        internal AccountExperienceProgressService AccountExperience { get; }
        internal GrowthCapsuleSyncService GrowthCapsuleSync { get; }
        internal CharacterExperienceService CharacterExperience { get; }
        internal Game.Dungeon.TowerOfDespairProgressService TowerOfDespairProgress { get; }
        internal Game.Party.PartyManager PartyManager { get; }
        internal Game.Raid.RaidManager RaidManager { get; }
        internal Game.Session.ISessionDirectory Sessions { get; }
        internal CardRewardCoordinator CardRewards { get; }
        internal Game.Dungeon.DropService Drops { get; }
        internal Game.Premium.DevilContractUsagePolicy DevilContracts { get; }
        internal Game.Dungeon.DungeonEntryAdmissionApplicationService
            EntryAdmission { get; }
        internal Game.Dungeon.DungeonEntryLimitService EntryLimits { get; }
        internal Game.Dungeon.CharacterFatigueService Fatigue { get; }
        internal DungeonAdmissionRejectSender AdmissionRejects { get; }
        internal DungeonProgressNotificationProjector ProgressNotifications { get; }
        internal DungeonTownReturnCoordinator TownReturn { get; }
        internal Game.Dungeon.DungeonPersistentEffectApplicationService PersistentEffects { get; }
        internal Game.Dungeon.DungeonInstanceRegistry InstanceRegistry { get; }
        internal Game.Dungeon.Tournament.TournamentDungeonApplicationService
            Tournaments { get; }
        internal Game.Dungeon.BloodAltar.BloodAltarDungeonApplicationService
            BloodAltars { get; }
        internal Game.Dungeon.BloodAltar.BloodAltarRewardPlanningService
            BloodAltarRewardPlanner { get; }
        internal Game.Dungeon.LicensedDungeonService LicensedDungeons { get; }

        internal DungeonSharedServices(
            Game.ReviveCoin.ReviveCoinService reviveCoin,
            SqliteCharacterRepository characterRepository,
            SqliteSelectCharacterDataSource selectCharacterDataSource,
            IRentalTimeProvider rentalTimeProvider,
            string connectionString,
            InventoryRefreshSender inventoryRefresh,
            Game.Party.PartyManager partyManager = null,
            Game.Session.ISessionDirectory sessions = null,
            Game.Quests.QuestDropService questDropService = null,
            AccountExperienceProgressService accountExperience = null,
            IMercenaryRestrictionService mercenaryRestrictions = null,
            RecommendDungeonClearStatsService recommendDungeonClears = null,
            DailyAttendanceAnytimeService dailyAttendanceAnytime = null,
            TotalAttendanceService totalAttendance = null,
            Game.Dungeon.DungeonPersistentEffectApplicationService persistentEffects = null,
            Game.Dungeon.DungeonInstanceRegistry instanceRegistry = null,
            Game.Raid.RaidManager raidManager = null,
            IGameDatabase database = null)
        {
            ReviveCoin = reviveCoin
                ?? throw new ArgumentNullException(nameof(reviveCoin));
            CharacterRepository = characterRepository
                ?? throw new ArgumentNullException(nameof(characterRepository));
            Database = database ?? GameDatabase.CreateDefault();
            ConnectionString = !string.IsNullOrWhiteSpace(connectionString)
                ? connectionString
                : Database.ConnectionString;
            PartyManager = partyManager;
            RaidManager = raidManager;
            Sessions = sessions;
            SelectCharacterDataSource = selectCharacterDataSource
                ?? throw new ArgumentNullException(nameof(selectCharacterDataSource));
            InventoryRefresh = inventoryRefresh;
            MercenaryRestrictions = mercenaryRestrictions;
            RentalTimeProvider = rentalTimeProvider
                ?? SystemRentalTimeProvider.Instance;

            Drops = new Game.Dungeon.DropService();
            ItemAcquisition = new Game.Dungeon.DungeonItemAcquisitionService(Drops);
            QuestDrops = questDropService ?? new Game.Quests.QuestDropService(
                inventoryRefresh,
                ConnectionString,
                rollDrop: null,
                itemAcquisition: ItemAcquisition,
                database: Database);
            DailyChallenges = new Game.Quests.DailyChallengeService(
                ConnectionString,
                new Game.DailyReset.DailyResetService(Database));
            DevilContracts = new Game.Premium.DevilContractUsagePolicy(
                Database);
            RecommendDungeonClears = recommendDungeonClears
                ?? new RecommendDungeonClearStatsService(Database);
            DailyAttendanceAnytime = dailyAttendanceAnytime;
            TotalAttendance = totalAttendance;
            Subtype1Repository = new SqliteSubtype1Repository(
                Database);
            CharacterStateRepository = new SqliteCharacterStateRepository(
                Database);
            DungeonDifficultyPermissions =
                new Game.Dungeon.DungeonDifficultyPermissionService(
                    Database);
            ProgressRepository = new SqliteCharacterProgressRepository(
                Database);
            Subtype0FieldsRepository = new SqliteSubtype0FieldsRepository(
                Database);
            HonorLevel = new HonorLevelSyncService(
                CharacterRepository,
                Database);
            AccountExperience = accountExperience
                ?? new AccountExperienceProgressService(
                    CharacterRepository,
                    Database);
            GrowthCapsuleSync = new GrowthCapsuleSyncService(
                CharacterRepository,
                Database);
            CharacterExperience = new CharacterExperienceService(
                AccountExperience,
                Database);
            ProgressNotifications = new DungeonProgressNotificationProjector(
                ConnectionString,
                CharacterRepository,
                Subtype1Repository,
                ProgressRepository,
                Subtype0FieldsRepository,
                HonorLevel,
                AccountExperience,
                Sessions);
            PersistentEffects = persistentEffects
                ?? new Game.Dungeon.DungeonPersistentEffectApplicationService(
                    ConnectionString,
                    database: Database);
            InstanceRegistry = instanceRegistry
                ?? new Game.Dungeon.DungeonInstanceRegistry(
                    ClockService.Instance);
            TownReturn = new DungeonTownReturnCoordinator(
                InstanceRegistry,
                ProgressNotifications,
                Sessions);
            var entryCost = new Game.Dungeon.DungeonEntryCostService(Database);
            EntryAdmission =
                new Game.Dungeon.DungeonEntryAdmissionApplicationService(
                    entryCost);
            EntryLimits = new Game.Dungeon.DungeonEntryLimitService(Database);
            Fatigue = new Game.Dungeon.CharacterFatigueService(Database);
            Tournaments =
                new Game.Dungeon.Tournament
                    .TournamentDungeonApplicationService();
            BloodAltars =
                new Game.Dungeon.BloodAltar
                    .BloodAltarDungeonApplicationService();
            BloodAltarRewardPlanner =
                new Game.Dungeon.BloodAltar
                    .BloodAltarRewardPlanningService();
            LicensedDungeons = new Game.Dungeon.LicensedDungeonService(Database);

            PersistentMechanisms = new DungeonPersistentMechanismCoordinator(
                CharacterStateRepository);
            DeathTower = new DeathTowerCoordinator(
                ConnectionString,
                sendExpGrantNotification: (session, settlement) =>
                    ProgressNotifications.SendExpGrantNotificationAsync(
                        session,
                        settlement?.ExperienceGrant,
                        "DEATH_TOWER_SETTLEMENT",
                        reloadMissingAccountProgress: true),
                accountExperience: AccountExperience,
                sendInDungeonLevelUpFollowups:
                    ProgressNotifications.SendInDungeonLevelUpFollowups,
                inventoryRefresh: inventoryRefresh,
                instanceRegistry: InstanceRegistry,
                townReturn: TownReturn,
                sessionDirectory: Sessions);
            TowerOfDespairProgress =
                new Game.Dungeon.TowerOfDespairProgressService(
                    new Game.Dungeon.TowerOfDespairProgressRepository(
                        Database));
            CardRewards = new CardRewardCoordinator(
                new Game.Dungeon.CardRewardService(PersistentEffects),
                database: Database);
            AdmissionRejects = new DungeonAdmissionRejectSender();
        }

        internal List<CharacterFatigueTarget> ResolveFatigueTargets(
            EnhancedClientSession session)
        {
            var targets = new List<CharacterFatigueTarget>();
            var party = session?.Player == null
                ? null
                : PartyManager?.GetPartyByUser(session.Player.UserId);
            if (party != null && party.Count > 1)
            {
                foreach (var member in party.Members)
                {
                    if (member.CharacterId > 0)
                    {
                        targets.Add(
                            new CharacterFatigueTarget(
                                member.CharacterId,
                                member.SlotIndex));
                    }
                }
            }

            if (targets.Count == 0)
            {
                var characterId = session?.Player?.CharacterId ?? 0;
                if (characterId > 0)
                {
                    var member = party?.GetMember(session.Player.UserId);
                    targets.Add(
                        new CharacterFatigueTarget(
                            characterId,
                            member?.SlotIndex ?? 0));
                }
            }

            return targets;
        }

        internal bool TryChargeFatigueRoomVisit(
            EnhancedClientSession session,
            DungeonRun run,
            int mazeIndex,
            int cellX,
            int cellY,
            string source,
            out CharacterFatigueConsumeResult result)
        {
            result = CharacterFatigueConsumeResult.Reject(
                "invalid_request",
                memberSlot: 0);
            var dungeonId = run?.DungeonId ?? 0;
            DungeonFile dungeon = null;
            if (dungeonId > 0)
            {
                try
                {
                    dungeon = DungeonData.GetDungeonFile(dungeonId);
                }
                catch
                {
                    dungeon = null;
                }
            }

            var targets = ResolveFatigueTargets(session);
            var ok = Fatigue.TryConsumeRoomVisit(
                run,
                targets,
                mazeIndex,
                cellX,
                cellY,
                dungeon,
                out result);
            if (!ok)
            {
                FileLogger.Log(
                    $"[{ProtocolLogName}] {source} fatigue rejected: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"dungeon={dungeonId} maze={mazeIndex} " +
                    $"cell=({cellX},{cellY}) cost={result?.Cost ?? 0} " +
                    $"slot={result?.MemberSlot ?? 0} " +
                    $"used={result?.Used ?? 0} max={result?.Max ?? 0} " +
                    $"reason={result?.Reason ?? "unknown"}; " +
                    "fail closed (no evidenced MOVE_MAP reject packet)");
                return false;
            }

            if (result != null && result.Cost > 0)
            {
                FileLogger.Log(
                    $"[{ProtocolLogName}] {source} fatigue consumed: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"dungeon={dungeonId} maze={mazeIndex} " +
                    $"cell=({cellX},{cellY}) cost={result.Cost} " +
                    $"members={targets.Count} used={result.Used} max={result.Max}");
            }

            return true;
        }

        internal async Task NotifyFatigueAsync(EnhancedClientSession session)
        {
            await NotifyFatigueAsync(ResolveFatigueTargets(session));
        }

        internal async Task NotifyFatigueAsync(
            IReadOnlyList<CharacterFatigueTarget> targets)
        {
            if (targets == null || targets.Count == 0 || Sessions == null)
                return;

            foreach (var target in targets)
            {
                var snapshot = Fatigue.Load(target.CharacterId);
                await Sessions.SendToAsync(
                    target.CharacterId,
                    GamePacketEnvelopeBuilder.Build(
                        0x00,
                        (ushort)NotiPacketTypeA21.FATIGUE,
                        CharacterFatiguePacketBuilder.BuildNotification(
                            snapshot)));
            }
        }
    }
}
