using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    // 一次玩家交易的执行结果: 双方各自的槽位变更(用于成交后的背包刷新),
    // 以及成交前任意一步失败时按逆序回滚的原子性保证。
    internal sealed class PlayerTradeExecutionResult
    {
        private readonly List<Action> _rollback = new List<Action>();

        public bool Success { get; set; }

        public string Failure { get; set; }

        public InventoryMutationSet FirstChanges { get; } =
            new InventoryMutationSet();

        public InventoryMutationSet SecondChanges { get; } =
            new InventoryMutationSet();

        internal List<Action> RollbackActions => _rollback;

        internal void Rollback()
        {
            for (var index = _rollback.Count - 1; index >= 0; index--)
                _rollback[index]();
            _rollback.Clear();
            Success = false;
        }

        internal void Commit()
        {
            _rollback.Clear();
        }
    }
}
