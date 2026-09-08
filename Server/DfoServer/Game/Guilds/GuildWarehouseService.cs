using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using System;
using System.Linq;

namespace DfoServer.Game.Guilds
{
    internal enum GuildWarehouseOperation { Push, Pop, Move }

    /// <summary>
    /// 仓库请求(参考包 GuildWarehouseRequestParser 定案的 native 合同):
    /// - Push(0x00F7, 13B): [u8 0][i16 背包槽][i32 预期物品][i32 数量][i16 仓库槽]
    /// - Pop (0x00F8, 11B): [i16 仓库槽][i32 预期物品][i32 数量][u8 0]
    /// - Move(0x00F9, 12B): [i16 源仓槽][i32 预期物品][i16 目标仓槽][i32 预期目标物品]
    /// </summary>
    internal sealed record GuildWarehouseRequest(
        GuildWarehouseOperation Operation,
        short Source,
        int ExpectedItemId,
        int Count,
        short Destination = 0,
        int ExpectedDestinationId = 0);

    internal sealed record GuildWarehouseResult(
        bool Success,
        int GuildId = 0,
        short Source = 0,
        short Destination = 0,
        int Count = 0,
        string Message = null);

    /// <summary>
    /// 公会仓库业务(2026-09-07 对齐参考包 GuildWarehouseApplicationService):
    /// 权限 bit15 → 容量校验 → 背包与仓库格在同一提交事务内原子转移;
    /// 物品预期 ID 全部校验(并发防呆: 他人先动同一格则失败重刷)。
    /// </summary>
    internal static class GuildWarehouseService
    {
        /// <summary>
        /// 可入库判定: 拒绝锁定/不可交易/限时/不支持类别;
        /// 装备/消耗/材料/副职业材料四类, 附加类型须 free 或已封印(sealing + SealFlag)。
        /// </summary>
        internal static bool CanStore(ItemCore core)
        {
            if (core == null || core.ItemId <= 0
                || core.EquipmentLockId != 0 || core.SortLockFlag != 0
                || core.TradeRestriction != 0 || core.ExpireTime > 0
                || !(core.ItemKind is ItemCore.KindEquipment or ItemCore.KindConsumable
                    or ItemCore.KindMaterial or ItemCore.KindExpertJobMaterial))
                return false;
            ItemMetadata metadata;
            try
            {
                metadata = ItemMetadataResolver.Resolve(core.ItemId);
            }
            catch
            {
                return false;
            }
            if (metadata == null
                || !(metadata.ItemKind is "equipment" or "stackable")
                || metadata.TradeLimitMax > 0)
                return false;
            string Token(string s) => (s ?? string.Empty).Replace("`", string.Empty)
                .Trim('[', ']', ' ').ToLowerInvariant();
            if (metadata.ImpossibleContents != null
                && metadata.ImpossibleContents.Any(c => Token(c).Contains("guild")))
                return false;
            var attach = Token(metadata.AttachType);
            return attach == "free" || (attach is "sealing" or "seal") && core.SealFlag != 0;
        }

        internal static GuildWarehouseResult Execute(
            InventoryLease lease, int accountId, GuildWarehouseRequest request)
        {
            GuildWarehouseResult Fail(string m) => new GuildWarehouseResult(false, Message: m);
            bool Current() => lease != null && lease.AccountId == accountId && accountId > 0
                && InventoryContext.IsCurrentLease(lease, lease.SessionId, lease.CharacterId);
            if (!Current() || request == null
                || request.Source < 0 || request.Destination < 0 || request.ExpectedItemId <= 0
                || (request.Operation != GuildWarehouseOperation.Move && request.Count <= 0))
                return Fail("公会仓库请求无效。");
            lock (lease.SyncRoot)
            {
                if (!Current() || !InventoryPersistenceService.SaveDirty(lease))
                    return Fail("背包尚未保存，请稍后重试。");
                var result = Fail("公会仓库未提交，物品没有转移。");
                var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease, "guild-warehouse-" + request.Operation, (connection, transaction) =>
                    {
                        if (!Current())
                            return false;
                        var guild = GuildSystem.GetGuildOfCharacter(lease.CharacterId);
                        if (guild == null
                            || !GuildSystem.HasPermission(lease.CharacterId, GuildPermissions.Warehouse))
                        {
                            result = Fail("没有公会仓库使用权限。");
                            return true;
                        }
                        var gid = guild.GuildId;
                        var capacity = GuildWarehouseRepository.Capacity(connection, transaction, gid);
                        if (capacity <= 0)
                        {
                            result = Fail("请先购买公会仓库扩张。");
                            return true;
                        }
                        var items = GuildWarehouseRepository.ReadItems(connection, transaction, gid);
                        var inventory = lease.Inventory;
                        items.TryGetValue(request.Source, out var stored);
                        items.TryGetValue(request.Destination, out var destination);

                        if (request.Operation == GuildWarehouseOperation.Push)
                        {
                            var source = inventory.GetItem(InventoryListType.Main, request.Source);
                            if (request.Destination >= capacity
                                || source?.ItemId != request.ExpectedItemId
                                || !CanStore(source)
                                || request.Count > (InventoryStackRuleService.IsStackable(source) ? source.Count : 1))
                            {
                                result = Fail("物品已变化，或此物品不能存入公会仓库。");
                                return true;
                            }
                            var copy = source.Copy();
                            if (destination != null)
                            {
                                if (!CanStore(destination)
                                    || !InventoryStackRuleService.TryPlanMerge(
                                        source, destination, request.Count, out var plan, out _)
                                    || plan.RemainingCount != 0)
                                {
                                    result = Fail("目标仓库格不能合并该物品。");
                                    return true;
                                }
                                copy = destination.Copy();
                                copy.Count = checked(copy.Count + request.Count);
                            }
                            else if (InventoryStackRuleService.IsStackable(copy))
                            {
                                copy.Count = request.Count;
                            }
                            if (!InventoryDeleteService.TryDecreaseStack(
                                    inventory, InventoryListType.Main, request.Source, request.Count, out _))
                                return false;
                            GuildWarehouseRepository.Save(connection, transaction, gid, request.Destination, copy);
                            result = new GuildWarehouseResult(
                                true, gid, request.Source, request.Destination, request.Count);
                        }
                        else if (request.Operation == GuildWarehouseOperation.Pop)
                        {
                            if (request.Source >= capacity
                                || stored?.ItemId != request.ExpectedItemId
                                || !CanStore(stored)
                                || request.Count > (InventoryStackRuleService.IsStackable(stored) ? stored.Count : 1))
                            {
                                result = Fail("仓库物品已变化，请刷新后再试。");
                                return true;
                            }
                            if (!InventoryInsertService.TryPlanInsertByDefaultRule(
                                    inventory, stored, request.Count, out var plan)
                                || plan.ListType != InventoryListType.Main
                                || plan.RemainingCount != 0)
                            {
                                result = Fail("背包空间不足，无法取出完整数量。");
                                return true;
                            }
                            if (!InventoryInsertService.TryApplyInsertPlan(inventory, stored, plan, out _))
                                return false;
                            ItemCore remaining = null;
                            if (InventoryStackRuleService.IsStackable(stored) && stored.Count > request.Count)
                            {
                                remaining = stored.Copy();
                                remaining.Count -= request.Count;
                            }
                            GuildWarehouseRepository.Save(connection, transaction, gid, request.Source, remaining);
                            result = new GuildWarehouseResult(
                                true, gid, request.Source, plan.SlotIndex, request.Count);
                        }
                        else if (request.Operation == GuildWarehouseOperation.Move)
                        {
                            if (request.Source >= capacity || request.Destination >= capacity
                                || stored?.ItemId != request.ExpectedItemId
                                || (destination?.ItemId ?? 0) != request.ExpectedDestinationId)
                            {
                                result = Fail("仓库格已变化，请刷新后再试。");
                                return true;
                            }
                            if (request.Source != request.Destination)
                            {
                                GuildWarehouseRepository.Save(connection, transaction, gid, request.Source, destination);
                                GuildWarehouseRepository.Save(connection, transaction, gid, request.Destination, stored);
                            }
                            result = new GuildWarehouseResult(true, gid, request.Source, request.Destination);
                        }
                        else
                        {
                            return false;
                        }
                        return true;
                    });
                return committed ? result : Fail("公会仓库提交失败，已回滚物品转移。");
            }
        }
    }
}
