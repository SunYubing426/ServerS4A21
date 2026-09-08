using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders
{
    // 交易窗物品变更(S2C 0x000F NOTI 交易形态)包体, 35 字节定长:
    //   [tradeSlot:i16][itemId:i32][value:i32][attr:u8][durability:u16]
    //   [enchantCardId:i32][enchantUpgradeCount:u8][amplifyType:u8]
    //   [amplifyValue:u16][expireTime:i32][genuineUpgrade:u8]
    //   [emancipateEquipmentLevel:u8][tradeRestriction:u8][tailUnknown0:u16]
    //   [tailUnknown1..3:u8×3][remainUseCount:u8][sortLockFlag:u8]
    // 金币形态 itemId=0、value=金额; 清槽形态 itemId=-1。core 为 null 时
    // 物品字段全 0(金币/清槽), 与客户端 0x000F handler 的定长读取对齐。
    internal static class TradeItemChangeBodyBuilder
    {
        internal const int EmptyOrPlainItemLength = 35;

        internal static byte[] BuildItem(short tradeSlot, ItemCore core)
        {
            return Build(tradeSlot, core, core?.ItemId ?? -1, core?.Value ?? 0);
        }

        internal static byte[] BuildGold(short tradeSlot, int amount)
        {
            return Build(tradeSlot, null, 0, amount);
        }

        internal static byte[] BuildEmpty(short tradeSlot)
        {
            return Build(tradeSlot, null, -1, 0);
        }

        private static byte[] Build(
            short tradeSlot,
            ItemCore core,
            int itemId,
            int value)
        {
            var w = new GamePacketWriter();
            w.WriteInt16(tradeSlot);
            w.WriteInt32(itemId);
            w.WriteInt32(value);
            w.WriteByte(core?.Attr ?? 0);
            w.WriteUInt16(core?.Durability ?? 0);
            w.WriteInt32(core?.EnchantCardId ?? 0);
            w.WriteByte(core?.EnchantUpgradeCount ?? 0);
            w.WriteByte(core?.AmplifyType ?? 0);
            w.WriteUInt16(core?.AmplifyValue ?? 0);
            w.WriteInt32(core?.ExpireTime ?? 0);
            w.WriteByte(core?.GenuineUpgrade ?? 0);
            w.WriteByte(core?.EmancipateEquipmentLevel ?? 0);
            w.WriteByte(core?.TradeRestriction ?? 0);
            w.WriteUInt16(core?.TailUnknown0 ?? 0);
            w.WriteByte(core?.TailUnknown1 ?? 0);
            w.WriteByte(core?.TailUnknown2 ?? 0);
            w.WriteByte(core?.TailUnknown3 ?? 0);
            w.WriteByte(core?.RemainUseCount ?? 0);
            w.WriteByte((byte)(core?.SortLockFlag == 1 ? 1 : 0));
            return w.ToArray();
        }
    }
}
