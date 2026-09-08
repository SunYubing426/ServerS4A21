using DfoServer.Game.Inventory;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Guilds
{
    /// <summary>公会仓库快照(容量 + 全部格子物品)。</summary>
    internal sealed record GuildWarehouseSnapshot(
        int GuildId,
        int Capacity,
        IReadOnlyDictionary<short, ItemCore> Items);

    /// <summary>
    /// 公会仓库持久层(2026-09-07 对齐参考包迁移 067, SQLite 适配):
    /// - 容量存 `guilds.warehouse_capacity`(迁移 v33); 物品存 `guild_warehouse_items`,
    ///   每格一份 99B <see cref="ItemCore.ToBytes"/>(与角色背包同序列化, 非 wire 格式)。
    /// - 写路径全部经 (connection, transaction) 静态方法, 挂在
    ///   <see cref="OnlineInventoryMutationCommitCoordinator"/> 的提交事务上,
    ///   与背包变更同生共死; 读快照为尽力而为的独立连接读。
    /// </summary>
    internal static class GuildWarehouseRepository
    {
        private static string ConnStr => GuildSystem.Repository.ConnectionString;

        // ── 事务内方法(OnlineInventoryMutationCommitCoordinator 提交域) ──

        public static int Capacity(SqliteConnection connection, SqliteTransaction transaction, int guildId)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "SELECT warehouse_capacity FROM guilds WHERE guild_id = @gid;";
                cmd.Parameters.AddWithValue("@gid", guildId);
                var v = cmd.ExecuteScalar();
                return v == null || v is DBNull ? 0 : Convert.ToInt32(v);
            }
        }

        public static Dictionary<short, ItemCore> ReadItems(
            SqliteConnection connection, SqliteTransaction transaction, int guildId)
        {
            var items = new Dictionary<short, ItemCore>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText =
                    "SELECT slot_index, item_core FROM guild_warehouse_items " +
                    "WHERE guild_id = @gid ORDER BY slot_index;";
                cmd.Parameters.AddWithValue("@gid", guildId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var slot = Convert.ToInt16(reader.GetValue(0));
                        var blob = (byte[])reader.GetValue(1);
                        if (blob == null || blob.Length < ItemCore.Size)
                            continue;
                        var core = ItemCore.FromBytes(blob);
                        if (core != null && !core.IsEmpty)
                            items[slot] = core;
                    }
                }
            }
            return items;
        }

        /// <summary>core=null/IsEmpty = 删除该格。</summary>
        public static void Save(
            SqliteConnection connection, SqliteTransaction transaction,
            int guildId, short slot, ItemCore core)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.Parameters.AddWithValue("@gid", guildId);
                cmd.Parameters.AddWithValue("@slot", slot);
                if (core == null || core.IsEmpty)
                {
                    cmd.CommandText =
                        "DELETE FROM guild_warehouse_items WHERE guild_id = @gid AND slot_index = @slot;";
                }
                else
                {
                    cmd.CommandText = @"
INSERT INTO guild_warehouse_items (guild_id, slot_index, item_core)
VALUES (@gid, @slot, @core)
ON CONFLICT (guild_id, slot_index) DO UPDATE SET item_core = @core;";
                    cmd.Parameters.AddWithValue("@core", core.ToBytes());
                }
                cmd.ExecuteNonQuery();
            }
        }

        // ── 独立连接读(查询快照用, 尽力而为) ──

        /// <summary>读取公会仓库快照; 公会不存在返回 null。权限由调用方先验。</summary>
        public static GuildWarehouseSnapshot ReadSnapshot(int guildId)
        {
            using (var conn = new SqliteConnection(ConnStr))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    var capacity = Capacity(conn, tx, guildId);
                    var items = ReadItems(conn, tx, guildId);
                    tx.Commit();
                    return new GuildWarehouseSnapshot(guildId, capacity, items);
                }
            }
        }
    }
}
