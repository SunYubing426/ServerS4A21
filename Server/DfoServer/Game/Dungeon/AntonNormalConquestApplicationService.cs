using System;
using System.Collections.Generic;
using DfoServer.Game.CharacterData;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Dungeon
{
    internal sealed class AntonNormalClearApplicationResult
    {
        internal AntonNormalClearApplicationResult(
            AntonNormalSyncState state,
            IReadOnlyList<DungeonPermissionEntrySnapshot> changes)
        {
            State = state;
            Changes = changes ?? Array.Empty<DungeonPermissionEntrySnapshot>();
        }

        internal AntonNormalSyncState State { get; }
        internal IReadOnlyList<DungeonPermissionEntrySnapshot> Changes { get; }
    }

    internal sealed class AntonNormalConquestApplicationService
    {
        private const int LinkedChallengeRate = 100;
        private const int LinkedChallengeCondition = -1;
        private readonly SqliteCharacterStateRepository _repository;
        private readonly DungeonEntryLimitService _entryLimits;
        private readonly IGameDatabase _database;

        // Anton_Awakening 最后一个副本（黑色火山），通关后锁住 5 个副本
        private const int AntonAwakeningFinalDungeonId = 247;
        private static readonly int[] AntonAwakeningDungeonIds =
            { 243, 244, 245, 246, 247 };

        internal AntonNormalConquestApplicationService(
            SqliteCharacterStateRepository repository)
            : this(repository, null)
        {
        }

        internal AntonNormalConquestApplicationService(
            SqliteCharacterStateRepository repository,
            IGameDatabase database)
        {
            _repository = repository
                ?? throw new ArgumentNullException(nameof(repository));
            _database = database;
            _entryLimits = database != null
                ? new DungeonEntryLimitService(database)
                : null;
        }

        internal void ConfigureLinkedChallenge(DungeonRun run)
        {
            if (run == null || !AntonNormalConquest.TryGetSequence(run.DungeonId, out _))
                return;
            if (!AntonNormalConquest.TryResolveLinkedNext(
                    run.DungeonId,
                    out var nextDungeonId))
            {
                run.LinkedDungeonNextId = 0;
                run.LinkedDungeonNextRate = 0;
                run.LinkedDungeonNextCondition = 0;
                return;
            }

            run.LinkedDungeonNextId = nextDungeonId;
            run.LinkedDungeonNextRate = LinkedChallengeRate;
            run.LinkedDungeonNextCondition = LinkedChallengeCondition;
        }

        // CMD SEQUENTIAL_DUNGEON_INFO(0x035D) 应答: 只解析客户端询问的
        // configKey 对应的序列。
        internal bool TryRestore(
            int characterId,
            int configKey,
            out AntonNormalSyncState state)
        {
            state = null;
            if (characterId <= 0)
                return false;
            return AntonNormalConquest.TryResolveSyncState(
                configKey,
                _repository.LoadDungeonPermissions(characterId),
                out state);
        }

        internal bool TryApplyClear(
            int characterId,
            int accountId,
            int dungeonId,
            out AntonNormalClearApplicationResult result)
        {
            result = null;
            if (characterId <= 0
                || !AntonNormalConquest.TryResolveClearPlan(dungeonId, out var plan))
            {
                return false;
            }

            var updates = new List<DungeonPermissionEntrySnapshot>();
            AddPermissionUpdate(
                updates,
                dungeonId,
                plan.Sequence.Difficulty,
                completed: true);
            AddPermissionUpdate(
                updates,
                plan.NextDungeonId,
                plan.Sequence.Difficulty,
                completed: false);
            AddPreviewPermissionUpdate(
                updates,
                plan.PreviewDungeonId,
                plan.Sequence.Difficulty);

            var permissions = _repository.ApplyDungeonPermissionBatch(
                characterId,
                updates,
                out var changes);
            if (!AntonNormalConquest.TryResolveSyncState(
                    permissions,
                    out var state)
                || state.Sequence.IndexOf(dungeonId) < 0)
            {
                return false;
            }

            // 通关 Anton_Awakening 最后一个副本（黑色火山）后：
            //   1) 清空 character 243-247 权限行（让客户端 UI 不再显示通关状态）
            //   2) 扣减 243-247 全部 5 个 limit（让 dungeon_limit_records 今日耗尽）
            if (dungeonId == AntonAwakeningFinalDungeonId)
            {
                TryLockAntonAwakening(characterId, accountId);
            }

            result = new AntonNormalClearApplicationResult(state, changes);
            return true;
        }

        private static void AddPermissionUpdate(
            ICollection<DungeonPermissionEntrySnapshot> updates,
            int dungeonId,
            byte difficulty,
            bool completed)
        {
            if (dungeonId <= 0)
                return;
            var resolved = completed
                ? AntonNormalConquest.TryResolveCompletedState(
                    dungeonId,
                    difficulty,
                    out var clearState)
                : AntonNormalConquest.TryResolveUnlockedState(
                    dungeonId,
                    difficulty,
                    out clearState);
            if (resolved)
            {
                updates.Add(new DungeonPermissionEntrySnapshot
                {
                    DungeonId = (ushort)dungeonId,
                    ClearState = clearState,
                });
            }
        }

        private static void AddPreviewPermissionUpdate(
            ICollection<DungeonPermissionEntrySnapshot> updates,
            int dungeonId,
            byte difficulty)
        {
            if (dungeonId <= 0
                || !AntonNormalConquest.TryResolveUnlockedState(
                    dungeonId,
                    difficulty,
                    out var unlockedState))
            {
                return;
            }
            updates.Add(new DungeonPermissionEntrySnapshot
            {
                DungeonId = (ushort)dungeonId,
                ClearState = (byte)Math.Max(1, unlockedState - 1),
            });
        }

        private void TryLockAntonAwakening(int characterId, int accountId)
        {
            if (_repository == null)
                return;

            try
            {
                // 清空 243-247 权限行
                _repository.DeleteDungeonPermissions(
                    characterId,
                    AntonAwakeningDungeonIds);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[AntonAwakening] clear permissions failed " +
                    $"character={characterId} dungeon={AntonAwakeningFinalDungeonId}: {ex.Message}");
            }

            // 扣减 243-247 全部 5 个 limit
            if (_entryLimits == null || accountId <= 0)
                return;

            foreach (var dungeonId in AntonAwakeningDungeonIds)
            {
                try
                {
                    _entryLimits.TryConsumeSpecialDungeonLimit(
                        accountId,
                        characterId,
                        dungeonId,
                        consumeCount: 1,
                        out _);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[AntonAwakening] consume limit failed " +
                        $"character={characterId} dungeon={dungeonId}: {ex.Message}");
                }
            }
        }
    }
}
