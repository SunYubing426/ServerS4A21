using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Quests
{
    internal sealed class QuestProgressApplicationService
    {
        private const int MaxCasAttempts = 4;
        private readonly string _connectionString;

        internal QuestProgressApplicationService(string connectionString)
        {
            _connectionString = connectionString
                ?? throw new ArgumentNullException(nameof(connectionString));
        }

        internal QuestProgressApplicationResult Apply(
            QuestProgressApplicationRequest request,
            Func<ushort, int, int, bool> clearMapMatcher = null)
        {
            var validation = Validate(request);
            if (validation != null)
                return validation;

            for (var attempt = 0; attempt < MaxCasAttempts; attempt++)
            {
                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction(deferred: false))
                    {
                        var result = ApplyInTransactionCore(
                            connection,
                            transaction,
                            request,
                            clearMapMatcher);
                        if (result.RetryRequired)
                        {
                            transaction.Rollback();
                            continue;
                        }
                        if (!result.Success)
                        {
                            transaction.Rollback();
                            return result;
                        }

                        transaction.Commit();
                        return result;
                    }
                }
            }

            return Failed("quest progress CAS retry exhausted");
        }

        internal QuestProgressApplicationResult ApplyInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            QuestProgressApplicationRequest request,
            Func<ushort, int, int, bool> clearMapMatcher = null)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));

            var validation = Validate(request);
            return validation ?? ApplyInTransactionCore(
                connection,
                transaction,
                request,
                clearMapMatcher);
        }

        private static QuestProgressApplicationResult ApplyInTransactionCore(
            SqliteConnection connection,
            SqliteTransaction transaction,
            QuestProgressApplicationRequest request,
            Func<ushort, int, int, bool> clearMapMatcher)
        {
            var active = QuestRepository.LoadActiveQuests(
                connection,
                transaction,
                request.CharacterId);
            var result = new QuestProgressApplicationResult();
            var eligible = request.EligibleQuestIds == null
                ? null
                : new HashSet<ushort>(request.EligibleQuestIds);
            var eligibleActivations = request.EligibleQuestActivations;
            var foundClientQuest = false;
            var clientActivationChanged = false;

            foreach (var quest in active)
            {
                if (quest == null)
                    continue;
                if (eligibleActivations != null)
                {
                    if (!eligibleActivations.TryGetValue(
                            quest.QuestId,
                            out var expectedActivation)
                        || !expectedActivation.Equals(quest.ActivationId))
                    {
                        if (request.Operation == QuestProgressOperation.ClientTrigger
                            && quest.QuestId == request.QuestId)
                        {
                            clientActivationChanged = true;
                        }
                        continue;
                    }
                }
                else if (eligible != null && !eligible.Contains(quest.QuestId))
                {
                    continue;
                }
                if (request.Operation == QuestProgressOperation.ClientTrigger
                    && quest.QuestId != request.QuestId)
                {
                    continue;
                }

                // 团本阶段完成：只作用于 [raid phase clear]，且按请求给的阶段索引
                // 直接选通道，不经过客户端触发位，也不套用其它类型的评估规则。
                if (request.Operation == QuestProgressOperation.RaidPhaseClear)
                {
                    if (!TryEvaluateRaidPhaseClear(
                            quest,
                            request,
                            out var raidEvaluation))
                    {
                        continue;
                    }

                    if (raidEvaluation.Trigger.PackedValue == quest.TriggerValue)
                    {
                        result.AddChanges(raidEvaluation.Changes);
                        continue;
                    }
                    if (!QuestRepository.TryUpdateTriggerValueCas(
                            connection,
                            transaction,
                            request.CharacterId,
                            quest.QuestId,
                            quest.ActivationId,
                            quest.Version,
                            quest.TriggerValue,
                            raidEvaluation.Trigger.PackedValue))
                    {
                        return Retry(
                            $"raid phase CAS conflict quest={quest.QuestId}");
                    }

                    result.MatchedObjective = true;
                    result.AddChanges(raidEvaluation.Changes);
                    continue;
                }

                if (request.Operation == QuestProgressOperation.ClientTrigger)
                    foundClientQuest = true;

                if (request.SourceEventId != Guid.Empty
                    && !quest.ActivationId.IsValid)
                {
                    return Failed(
                        $"quest={quest.QuestId} has invalid activation identity");
                }

                QuestProgressEvaluation evaluation;
                try
                {
                    evaluation = QuestObjectiveEvaluator.Evaluate(
                        quest,
                        request,
                        clearMapMatcher);
                }
                catch (Exception ex)
                {
                    return Failed(
                        $"objective evaluation failed quest={quest.QuestId}: {ex.Message}");
                }

                if (!evaluation.Matched)
                    continue;
                result.MatchedObjective = true;
                if (request.SourceEventId != Guid.Empty
                    && !QuestRepository.TryInsertProgressEvent(
                        connection,
                        transaction,
                        request.CharacterId,
                        quest.ActivationId,
                        request.SourceEventId,
                        request.EventKind))
                {
                    result.DuplicateEvent = true;
                    continue;
                }
                if (evaluation.Trigger.PackedValue == quest.TriggerValue)
                {
                    result.AddChanges(evaluation.Changes);
                    continue;
                }

                if (!QuestRepository.TryUpdateTriggerValueCas(
                        connection,
                        transaction,
                        request.CharacterId,
                        quest.QuestId,
                        quest.ActivationId,
                        quest.Version,
                        quest.TriggerValue,
                        evaluation.Trigger.PackedValue))
                {
                    return Retry(
                        $"quest progress CAS conflict quest={quest.QuestId}");
                }

                result.AddChanges(evaluation.Changes);
            }

            if (request.Operation == QuestProgressOperation.ClientTrigger
                && !foundClientQuest)
            {
                result.QuestNotActive = true;
                result.ActivationChanged = clientActivationChanged;
            }
            if (request.Operation == QuestProgressOperation.ClientTrigger
                && (!request.CommandOwner.HasValue
                    || !request.CommandOwner.Value.IsCurrentInventoryOwner()))
            {
                return Failed(
                    "client quest trigger session owner changed before commit");
            }

            result.Success = true;
            return result;
        }

        // [raid phase clear] 的 int data 每 3 个一组：(阶段索引, 次数, 角色标志)。
        // 只递减与本次阶段索引匹配的那一组对应的通道。
        // 角色标志：0=攻坚队长 / 1=小队长 / 2=队员 / 其余或缺失=不限角色。
        private static bool TryEvaluateRaidPhaseClear(
            ActiveQuest quest,
            QuestProgressApplicationRequest request,
            out QuestProgressEvaluation evaluation)
        {
            evaluation = null;
            if (quest == null || request == null)
                return false;

            var qst = GameWorld.QuestData.GetQuestFile(quest.QuestId);
            if (qst == null
                || GameWorld.QuestData.NormalizeQuestTag(qst.Type)
                    != "raid phase clear")
            {
                return false;
            }

            var values = GameWorld.QuestData.ParseIntList(qst.IntData);
            for (var i = 0; i + 3 <= values.Count; i += 3)
            {
                if (values[i] != request.RaidPhaseIndex)
                    continue;

                var required = values[i + 1];
                if (required <= 0)
                    return false;
                // required 只是该阶段的"总需求次数"，初始触发器已是该值；
                // 每完成一次阶段递减 1（通道存的是"还差多少次"）。
                _ = required;

                var roleFlag = values[i + 2];
                var roleScoped = roleFlag >= 0 && roleFlag <= 2;
                if (roleScoped
                    && request.RaidRoleFlag >= 0
                    && roleFlag != request.RaidRoleFlag)
                {
                    return false;
                }

                var current = new QuestTrigger(quest.TriggerValue);
                var currentChannel = current.GetChannel(request.RaidPhaseIndex);
                // 通道存的是「还差多少次」：阶段完成一次就递减，
                // 未完成/回滚(Increment=false)则回升。
                var channelDelta = request.Increment ? -1 : 1;
                var nextChannel = Math.Min(
                    0x1FF,
                    Math.Max(0, currentChannel + channelDelta));
                var next = current.ReplaceChannel(
                    request.RaidPhaseIndex,
                    nextChannel);
                if (next.PackedValue == current.PackedValue)
                    return false;
                evaluation = new QuestProgressEvaluation
                {
                    Matched = true,
                    Trigger = next,
                };
                evaluation.AddChange(quest.QuestId, current, next);
                return true;
            }

            return false;
        }

        private static QuestProgressApplicationResult Validate(
            QuestProgressApplicationRequest request)
        {
            if (request == null || request.CharacterId <= 0)
                return Failed("invalid quest progress request");
            if (request.Operation == QuestProgressOperation.ClientTrigger
                && (!request.CommandOwner.HasValue
                    || !request.CommandOwner.Value.IsCurrentInventoryOwner()))
            {
                return Failed("client quest trigger has no current session owner");
            }
            if (request.Operation == QuestProgressOperation.RaidPhaseClear
                && (request.RaidPhaseIndex < 0 || request.RaidPhaseIndex > 2))
            {
                return Failed(
                    $"invalid raid phase index {request.RaidPhaseIndex}");
            }
            return null;
        }

        private static QuestProgressApplicationResult Retry(string error)
            => new QuestProgressApplicationResult
            {
                Success = false,
                RetryRequired = true,
                Error = error ?? string.Empty,
            };

        private static QuestProgressApplicationResult Failed(string error)
            => new QuestProgressApplicationResult
            {
                Success = false,
                Error = error ?? string.Empty,
            };
    }

}
