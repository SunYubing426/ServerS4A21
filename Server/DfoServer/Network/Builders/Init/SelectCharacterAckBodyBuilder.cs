using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Quests;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public static class SelectCharacterAckBodyBuilder
    {
        public static byte[] BuildRejected(byte errorCode)
        {
            return CommonPacketBodyBuilder.BuildCmdError(errorCode);
        }

        public static bool TryBuild(SelectCharacterDataSnapshot snapshot, out byte[] body)
        {
            return TryBuild(snapshot, connectionString: null, out body);
        }

        internal static bool TryBuild(
            SelectCharacterDataSnapshot snapshot,
            string connectionString,
            out byte[] body)
        {
            var initSnap = snapshot.InitializationSnapshot;
            var record = snapshot.CharacterRecord;

            if (record == null)
            {
                body = null;
                return false;
            }

            var writer = new GamePacketWriter();

            // [0] u8 resultCode
            writer.WriteByte(1);

            // [1] u32 accountRegTime — seed=0
            writer.WriteInt32(0);

            // [5] u32 characterCreatedTime
            if (record != null)
                writer.WriteInt32((int)((DateTimeOffset)record.CreatedAt).ToUnixTimeSeconds());
            else
                writer.WriteInt32(initSnap.AckCharCreatedTime);

            // [9] u16 uniqueId
            writer.WriteInt16(record != null ? (short)record.CharacterId : (short)initSnap.AckUniqueId);

            var fatigue = new CharacterFatigueSnapshot(
                0,
                CharacterFatigueService.DefaultMaxFatigue);
            if (record != null
                && record.CharacterId > 0
                && !string.IsNullOrWhiteSpace(connectionString))
            {
                try
                {
                    fatigue = new CharacterFatigueService(connectionString)
                        .Load(record.CharacterId);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[SelectCharacterAck] fatigue load failed " +
                        $"cid={record.CharacterId}: {ex.Message}");
                }
            }

            FileLogger.Log(
                $"[SelectCharacterAck] fatigue cid={record.CharacterId} " +
                $"remaining={fatigue.Remaining} used={fatigue.Used} " +
                $"max={fatigue.Max}");
            // [11] i16 Fatigue used (HUD remaining = max - this),
            // [13] FatigueMax, [15] usedFatigueMax (same used)
            CharacterFatiguePacketBuilder.WriteSnapshot(writer, fatigue);

            // [17] u8 premiumCount + N × (u8 type + u8[8] endTime)
            var premiums = initSnap.AckPremiums;
            writer.WriteByte((byte)premiums.Count);
            for (int i = 0; i < premiums.Count; i++)
            {
                writer.WriteByte(premiums[i].PremiumType);
                writer.WriteBytes(premiums[i].EndTime);
            }

            // u32 cera
            writer.WriteInt32(initSnap.AckCera);

            // 30 x (u16 questId + u32 triggerValue) - fixed active quest slots
            List<ActiveQuest> activeQuests = null;
            if (record != null && record.CharacterId > 0)
            {
                try
                {
                    var connStr = !string.IsNullOrWhiteSpace(connectionString)
                        ? connectionString
                        : Infrastructure.GameDatabase.CreateDefault().ConnectionString;
                    activeQuests = QuestService.LoadActiveQuests(connStr, record.CharacterId);
                }
                catch { }
            }
            var projectedActiveQuests = QuestDungeonPresentationPlanner
                .ProjectActiveQuests(activeQuests);
            var activeQuestSlots = QuestSlotLayout.ProjectFixedSlots(
                projectedActiveQuests);
            for (int i = 0; i < QuestSlotLayout.ActiveSlotCount; i++)
            {
                var activeQuest = activeQuestSlots[i];
                if (activeQuest != null)
                {
                    writer.WriteUInt16(activeQuest.QuestId);
                    writer.WriteUInt32(activeQuest.TriggerValue);
                }
                else
                {
                    writer.WriteUInt16(0xFFFF);
                    writer.WriteInt32(0);
                }
            }

            // Four fixed int32 slots restored by CMD 0x01FB SAVE_QUEST_NOTIFY.
            for (var index = 0; index < QuestNotifySelectionService.MaxSlots; index++)
            {
                writer.WriteInt32(index < initSnap.QuestNotifyIds.Count
                    ? initSnap.QuestNotifyIds[index]
                    : 0);
            }

            writer.WriteByte(initSnap.AckCharSlotIndex);

            // pad0 + flag_count + flags[count], then HUD 疲劳蓄电池 two u16s.
            // Always count=0: the old level>1 `01 4E` was a capture leftover
            // (HUD second number 78=0x4E). Official unused battery is 0/0;
            // seed 3073/513 were misaligned slices of this list, not an owner.
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteUInt16(initSnap.AckFatigueBattery);
            writer.WriteUInt16(initSnap.AckFatigueGrownUpBuff);
            writer.WriteByte(initSnap.AckTradePunishFlag);
            writer.WriteUInt16(initSnap.AckExtraField86JP);
            // reserved 8B: 客户端读取边界(264B)之后的尾巴, handler 不读(CMD_PACKET/4.md);
            // 旧列存的其实是抓包切片错位的教程标记残渣, 固定写零
            for (int j = 0; j < 8; j++)
                writer.WriteByte(0);
            writer.WriteByte(initSnap.AckTutorialSkipable);
            // ack_post_tutorial_u16: seed=0
            writer.WriteUInt16(0);
            // ack_unread_tail: seed=3B all-zero; original code defaulted to 22B when null
            for (int j = 0; j < 22; j++)
                writer.WriteByte(0);

            body = writer.ToArray();
            return true;
        }
    }
}
