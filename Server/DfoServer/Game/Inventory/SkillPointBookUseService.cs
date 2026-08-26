using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    // 这些道具在 PVF 中只是可使用堆叠物；实际的永久 SP/TP 奖励必须由服务端写入角色账本。
    internal static class SkillPointBookUseService
    {
        internal const int TpPlusOneItemTemplateId = 1204;
        internal const int TpPlusFiveItemTemplateId = 1205;
        internal const int SpPlusFiveItemTemplateId = 1031;
        internal const int SpPlusTwentyItemTemplateId = 1038;
        internal const int EventSpPlusFiveItemTemplateId = 80003;

        internal static int ResolveClientVisibleItemTemplateId(ItemCore core)
        {
            if (core?.ItemKind == ItemCore.KindConsumable
                && core.ItemId == EventSpPlusFiveItemTemplateId)
            {
                // The A21 client only emits INCREASE_STATUS for its built-in
                // skill-book IDs. Keep 80003 as the server/persistence identity,
                // but project the byte-identical SP+5 book as native ID 1031 so
                // the client sends the slot-only use request.
                return SpPlusFiveItemTemplateId;
            }

            return core?.ItemId ?? 0;
        }

        internal static bool TryResolve(int itemTemplateId, out SkillPointBookGrant grant)
        {
            switch (itemTemplateId)
            {
                case TpPlusOneItemTemplateId:
                    grant = new SkillPointBookGrant(0, 1);
                    return true;
                case TpPlusFiveItemTemplateId:
                    grant = new SkillPointBookGrant(0, 5);
                    return true;
                case SpPlusFiveItemTemplateId:
                case EventSpPlusFiveItemTemplateId:
                    grant = new SkillPointBookGrant(5, 0);
                    return true;
                case SpPlusTwentyItemTemplateId:
                    grant = new SkillPointBookGrant(20, 0);
                    return true;
                default:
                    grant = default;
                    return false;
            }
        }

        internal static SkillPointBookUseResult TryCommitUse(
            InventoryLease lease,
            InventoryListType listType,
            short slotIndex,
            int expectedItemTemplateId)
        {
            if (lease?.Inventory == null)
                return SkillPointBookUseResult.NotHandled;

            var resolvedItemTemplateId = 0;
            SkillPointBookGrant grant;
            lock (lease.SyncRoot)
            {
                if (!InventoryDeleteService.CanUseStackableForClient(
                        lease.Inventory,
                        listType,
                        slotIndex,
                        expectedItemTemplateId,
                        out resolvedItemTemplateId)
                    || !TryResolve(resolvedItemTemplateId, out grant))
                {
                    return SkillPointBookUseResult.NotHandled;
                }
            }

            var committed = InventoryDeleteCommitService.TryCommitStackableUseDetailed(
                lease,
                listType,
                slotIndex,
                resolvedItemTemplateId,
                (connection, transaction, itemTemplateId) =>
                {
                    SkillPointBookGrant resolvedGrant;
                    return TryResolve(itemTemplateId, out resolvedGrant)
                        && resolvedGrant.Equals(grant)
                        && TryApplyBonus(
                            connection,
                            transaction,
                            lease.CharacterId,
                            resolvedGrant);
                });

            return new SkillPointBookUseResult
            {
                Handled = true,
                Success = committed != null && committed.Consumed,
                ItemTemplateId = resolvedItemTemplateId,
                Grant = grant,
                Mutation = committed?.Mutation,
            };
        }

        private static bool TryApplyBonus(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            SkillPointBookGrant grant)
        {
            if (connection == null || transaction == null || characterId <= 0)
                return false;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE characters
SET bonus_sp = bonus_sp + @sp,
    bonus_tp = bonus_tp + @tp,
    updated_at = CURRENT_TIMESTAMP
WHERE character_id = @characterId
  AND delete_flag = 0
  AND bonus_sp <= @maxSp
  AND bonus_tp <= @maxTp;";
                command.Parameters.AddWithValue("@sp", grant.Sp);
                command.Parameters.AddWithValue("@tp", grant.Tp);
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@maxSp", int.MaxValue - grant.Sp);
                command.Parameters.AddWithValue("@maxTp", int.MaxValue - grant.Tp);
                return command.ExecuteNonQuery() == 1;
            }
        }
    }

    internal readonly struct SkillPointBookGrant
    {
        internal SkillPointBookGrant(int sp, int tp)
        {
            Sp = sp;
            Tp = tp;
        }

        internal int Sp { get; }

        internal int Tp { get; }
    }

    internal sealed class SkillPointBookUseResult
    {
        internal static readonly SkillPointBookUseResult NotHandled =
            new SkillPointBookUseResult();

        internal bool Handled { get; set; }

        internal bool Success { get; set; }

        internal int ItemTemplateId { get; set; }

        internal SkillPointBookGrant Grant { get; set; }

        internal InventoryMutationResult Mutation { get; set; }
    }
}
