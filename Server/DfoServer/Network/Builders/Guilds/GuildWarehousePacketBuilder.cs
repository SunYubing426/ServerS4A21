using DfoServer.Game.Guilds;
using System.Linq;

namespace DfoServer.Network.Builders.Guilds
{
    /// <summary>
    /// 公会仓库包构建(2026-09-07 对齐参考包 GuildWarehousePacketBuilder):
    /// native 113AFB0(ACK)/1198360(NOTI): [u32 容量][u16 条数] + 101B 公共物品项
    /// (ItemListProtocolWriter.WriteCommonEntry84, 非 DB 99B Core);
    /// ACK 前缀通用成功字节 u8=1。
    /// </summary>
    internal static class GuildWarehousePacketBuilder
    {
        internal static byte[] Snapshot(GuildWarehouseSnapshot snapshot, bool acknowledgement)
        {
            var w = new GamePacketWriter();
            if (acknowledgement)
                w.WriteByte(1);
            w.WriteUInt32((uint)snapshot.Capacity);
            w.WriteUInt16(checked((ushort)snapshot.Items.Count));
            foreach (var row in snapshot.Items.OrderBy(p => p.Key))
                ItemListProtocolWriter.WriteCommonEntry84(w, row.Key, row.Value);
            return w.ToArray();
        }

        internal static byte[] Mutation(GuildWarehouseOperation operation, GuildWarehouseResult result)
        {
            var w = new GamePacketWriter();
            w.WriteByte(1);
            if (operation == GuildWarehouseOperation.Push)
            {
                w.WriteByte(0);
                w.WriteInt16(result.Source);
                w.WriteInt32(result.Count);
                w.WriteInt16(result.Destination);
            }
            else if (operation == GuildWarehouseOperation.Pop)
            {
                w.WriteInt16(result.Source);
                w.WriteByte(0);
                w.WriteInt16(result.Destination);
                w.WriteInt32(result.Count);
            }
            else
            {
                w.WriteInt16(result.Source);
                w.WriteInt16(result.Destination);
            }
            return w.ToArray();
        }
    }
}
