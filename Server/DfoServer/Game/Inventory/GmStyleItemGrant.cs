using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Inventory
{
    /// <summary>
    /// GM 工具同款的独立连接发放通道，直接把道具写入指定角色背包。
    /// </summary>
    internal static class GmStyleItemGrant
    {
        internal static bool TryGrant(
            string connectionString,
            int characterId,
            int accountId,
            int itemTemplateId,
            int count)
        {
            if (count <= 0)
                return false;

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                var inventory = InventoryService.LoadFromDb(connection, characterId, accountId);
                if (inventory == null)
                    return false;

                if (!InventoryRewardGrantService.TryCreateAndInsert(
                        inventory,
                        itemTemplateId,
                        ItemCreateReason.AdminGrant,
                        count,
                        out var grant)
                    || !grant.Success)
                {
                    return false;
                }

                var lease = new InventoryLease(Guid.NewGuid(), characterId, inventory, version: 1);
                using (var transaction = connection.BeginTransaction())
                {
                    if (!InventoryPersistenceService.SaveDirtyInTransaction(
                            connection,
                            transaction,
                            lease))
                        return false;
                    transaction.Commit();
                }
                return true;
            }
        }
    }
}
