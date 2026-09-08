namespace DfoServer.Game.Inventory
{
    // 玩家交易(0x000A/0x000B 开窗 + 0x0018 状态机)中, 一方放入交易窗的单项报价。
    // IsGold=金币形态(源固定为主栏 0 槽虚拟计数), 否则为物品形态(Item 为转移用副本,
    // SourceSnapshot 为放入时的源快照, 成交前校验源未变, 防复制/错乱)。
    internal sealed class PlayerTradeOffer
    {
        public short TradeSlot { get; set; }

        public byte SourceList { get; set; }

        public short SourceSlot { get; set; }

        public int Amount { get; set; }

        public bool IsGold { get; set; }

        public ItemCore SourceSnapshot { get; set; }

        public ItemCore Item { get; set; }

        public PlayerTradeOffer Copy()
        {
            return new PlayerTradeOffer
            {
                TradeSlot = TradeSlot,
                SourceList = SourceList,
                SourceSlot = SourceSlot,
                Amount = Amount,
                IsGold = IsGold,
                SourceSnapshot = SourceSnapshot?.Copy(),
                Item = Item?.Copy(),
            };
        }
    }
}
