using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Raid;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Raid;

namespace DfoServer.Network.Handlers;

public sealed partial class RaidHandler
{
	private readonly ConcurrentDictionary<uint, ConcurrentDictionary<uint, uint>>
		_raidDungeonStates =
			new ConcurrentDictionary<uint, ConcurrentDictionary<uint, uint>>();

	private async Task StartRecoveryTimerAsync(RaidSnapshot raid, uint dungeonId, uint recovery, uint active, Func<RaidSnapshot, Task> timeout)
	{
		var version = AdvanceTimer(raid.RaidId, 2u, dungeonId);
		await SendTimerAsync(raid, 2u, dungeonId, recovery);
		RunInBackground(RunRecoveryTimerAsync(raid, dungeonId, recovery, active, version, timeout), "dungeon-recovery");
	}

	private async Task RunRecoveryTimerAsync(RaidSnapshot raid, uint dungeonId, uint recovery, uint active, Guid version, Func<RaidSnapshot, Task> timeout)
	{
		try
		{
			await Task.Delay((int)(recovery * 1000));
			if (TimerCurrent(raid.RaidId, 2u, dungeonId, version) && TryGetCurrentRaid(raid, out var current))
			{
				await SetDungeonStateAsync(current, dungeonId, 0u);
				_raids.ResetClearCounts(current.RaidId, new uint[1] { dungeonId });
				await SetSymbolAsync(current, GetAntonHpSymbolId(dungeonId), 0u);
				switch (dungeonId)
				{
				case 211u:
					await StartBlackFogPassiveTimerAsync(current);
					break;
				case 216u:
					await SetSymbolAsync(current, 123u, 1u);
					await StartNavalCannonMeteoTimerAsync(current);
					break;
				}
				await StartActiveTimerAsync(current, dungeonId, active, timeout);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			FileLogger.Log($"[GameProtocol] RAID_RECOVERY_TIMER failed raid={raid?.RaidId} dungeon={dungeonId} error={ex2.Message}");
		}
	}

	private async Task StartBlackFogPassiveTimerAsync(RaidSnapshot raid)
	{
		var version = AdvanceTimer(raid.RaidId, 3u, 211u);
		await SendTimerAsync(raid, 3u, 211u, 240u);
		RunInBackground(RunBlackFogPassiveTimerAsync(raid, version), "black-fog-passive");
	}

	private async Task RunBlackFogPassiveTimerAsync(RaidSnapshot raid, Guid version)
	{
		try
		{
			await Task.Delay(240000);
			if (TimerCurrent(raid.RaidId, 3u, 211u, version) && TryGetCurrentRaid(raid, out var current))
			{
				await SetSymbolAsync(current, 1u, 1u);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			FileLogger.Log($"[GameProtocol] RAID_BLACK_FOG_PASSIVE_TIMER failed raid={raid?.RaidId} error={ex2.Message}");
		}
	}

	private async Task StartNavalCannonMeteoTimerAsync(RaidSnapshot raid)
	{
		var version = AdvanceTimer(raid.RaidId, 3u, 216u);
		await SendTimerAsync(raid, 3u, 216u, 120u);
		RunInBackground(RunNavalCannonMeteoTimerAsync(raid, version), "naval-cannon-meteo");
	}

	private async Task RunNavalCannonMeteoTimerAsync(RaidSnapshot raid, Guid version)
	{
		try
		{
			await Task.Delay(120000);
			if (TimerCurrent(raid.RaidId, 3u, 216u, version) && TryGetCurrentRaid(raid, out var current))
			{
				await SetSymbolAsync(current, 2u, 1u);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			FileLogger.Log($"[GameProtocol] RAID_NAVAL_METEO_TIMER failed raid={raid?.RaidId} error={ex2.Message}");
		}
	}

	private async Task StartActiveTimerAsync(RaidSnapshot raid, uint dungeonId, uint seconds, Func<RaidSnapshot, Task> timeout)
	{
		var version = AdvanceTimer(raid.RaidId, 1u, dungeonId);
		await SendTimerAsync(raid, 1u, dungeonId, seconds);
		RunInBackground(RunActiveTimerAsync(raid, dungeonId, seconds, version, timeout), "dungeon-active");
	}

	private async Task RunActiveTimerAsync(RaidSnapshot raid, uint dungeonId, uint seconds, Guid version, Func<RaidSnapshot, Task> timeout)
	{
		try
		{
			await Task.Delay((int)(seconds * 1000));
			if (TimerCurrent(raid.RaidId, 1u, dungeonId, version) && TryGetCurrentRaid(raid, out var current))
			{
				await timeout(current);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			FileLogger.Log($"[GameProtocol] RAID_ACTIVE_TIMER failed raid={raid?.RaidId} dungeon={dungeonId} error={ex2.Message}");
		}
	}

	private Task SendTimerAsync(RaidSnapshot raid, uint type, uint dungeonId, uint seconds)
	{
		return BroadcastRaidNotificationAsync(raid, NotiPacketType.RAID_SET_TIMER, RaidPacketBuilder.BuildSetTimer(type, dungeonId, seconds));
	}

	private Task SetDungeonStateAsync(RaidSnapshot raid, uint dungeonId, uint state)
	{
		return SendRaidDungeonStateAsync(
			raid,
			new[] { new KeyValuePair<uint, uint>(dungeonId, state) });
	}

	private Task SendRaidDungeonStateAsync(
		RaidSnapshot raid,
		IReadOnlyList<KeyValuePair<uint, uint>> dungeonStates,
		uint infectionDungeonId = 0)
	{
		if (raid == null || dungeonStates == null)
			throw new ArgumentNullException(
				raid == null ? nameof(raid) : nameof(dungeonStates));

		var cache = _raidDungeonStates.GetOrAdd(
			raid.RaidId,
			_ => new ConcurrentDictionary<uint, uint>());
		foreach (var dungeonState in dungeonStates)
			cache[dungeonState.Key] = dungeonState.Value;
		if (infectionDungeonId != 0)
			_symbolValues[(raid.RaidId, AntonInfectionDungeonIndexSymbolId)] =
				infectionDungeonId;

		return BroadcastRaidNotificationAsync(
			raid,
			NotiPacketType.RAID_DUNGEON_STATE,
			RaidPacketBuilder.BuildDungeonState(
				dungeonStates,
				infectionDungeonId));
	}

	private Task SetSymbolAsync(RaidSnapshot raid, uint symbolId, uint value)
	{
		_symbolValues[(raid.RaidId, symbolId)] = value;
		return BroadcastRaidNotificationAsync(raid, NotiPacketType.RAID_SET_SYMBOL, RaidPacketBuilder.BuildSetSymbol(symbolId, value));
	}

	private Task SetSymbolsAsync(RaidSnapshot raid, IReadOnlyList<KeyValuePair<uint, uint>> values)
	{
		foreach (KeyValuePair<uint, uint> value in values)
		{
			_symbolValues[(raid.RaidId, value.Key)] = value.Value;
		}
		return BroadcastRaidNotificationAsync(raid, NotiPacketType.RAID_SET_SYMBOL, RaidPacketBuilder.BuildSetSymbols(values));
	}

	private bool TryGetCurrentRaid(RaidSnapshot raid, out RaidSnapshot current)
	{
		current = null;
		return raid != null && _raids.TryGetByRaidId(raid.RaidId, out current)
			&& current.InstanceId == raid.InstanceId && current.State == 2 && current.PhaseIndex == raid.PhaseIndex;
	}

	private void StartAttackTimeoutTimer(RaidSnapshot raid, uint remainingSeconds)
	{
		var version = AdvanceTimer(raid.RaidId, AttackTimerType, AttackTimerDungeonId);
		DfoServer.Infrastructure.ClockService.Instance.ScheduleOneShotAfterAsync(
			$"raid-attack:{raid.InstanceId}", TimeSpan.FromSeconds(remainingSeconds),
			_ => RunAttackTimeoutAsync(raid, version));
	}

	private async Task RunAttackTimeoutAsync(
		RaidSnapshot expected,
		Guid version)
	{
		var raidId = expected.RaidId;
		var phaseIndex = expected.PhaseIndex;
		try
		{
			if (!TimerCurrent(raidId, AttackTimerType, AttackTimerDungeonId, version)
				|| !_raids.TryFailPhase(expected, out var failed))
			{
				return;
			}

			if (phaseIndex == 0)
				CancelAllPhaseOneTimers(raidId);
			else
				CancelAllPhaseTwoTimers(raidId);

			uint[] dungeonIds = phaseIndex == 0
				? AntonFirstPhaseDungeonIds
				: AntonSecondPhaseDungeonIds;
			foreach (uint dungeonId in dungeonIds)
				await SetDungeonStateAsync(failed, dungeonId, 2u);

			if (phaseIndex == 0)
				await SetSymbolAsync(failed, AntonPhaseOneFailSymbolId, 1u);

			await BroadcastRaidNotificationAsync(
				failed,
				NotiPacketType.RAID_REMAIN_TIME,
				RaidPacketBuilder.BuildRemainTime(0, 0u));
			foreach (RaidMember member in failed.Members)
			{
				if (_sessions.TryGet(checked((int)member.CharacterId), out var memberSession)
					&& memberSession.SessionId == member.SessionId)
				{
					await memberSession.SendPacketAsync(BuildFailedRaidResultPacket(failed));
				}
			}
			// RAID_STATE=4 enters the client card-selection window even on failure.
			// Keep the domain terminal state, but present failure through RAID_RESULT.
			await EnablePhaseOneDungeonReturnAsync(failed);
			CleanupRaidRuntimeState(raidId);
			FileLogger.Log(
				$"[GameProtocol] RAID_ATTACK_TIMEOUT raid={raidId} phase={phaseIndex} " +
				$"elapsed={failed.PhaseClearTimeSeconds} deaths={failed.PhaseDeathCount}");
		}
		catch (Exception ex)
		{
			FileLogger.Log(
				$"[GameProtocol] RAID_ATTACK_TIMEOUT failed raid={raidId} phase={phaseIndex} error={ex.Message}");
		}
	}

	internal static byte[] BuildFailedRaidResultPacket(RaidSnapshot raid)
	{
		if (raid.State != 4 || raid.StateArgument != 1)
			throw new ArgumentException("Raid is not in the failed terminal state.", nameof(raid));
		return GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_RESULT,
			RaidPacketBuilder.BuildRaidResult(1, raid.PhaseIndex,
				raid.PhaseClearTimeSeconds, raid.PhaseDeathCount, 0, 1));
	}

	internal Guid AdvanceTimer(uint raidId, uint type, uint dungeonId)
	{
		// Tokens must not repeat after Cleanup removes the key and a new raid
		// reuses the same wire id. A per-key counter restarting at one is unsafe.
		return _timerVersions.AddOrUpdate(TimerKey(raidId, type, dungeonId), Guid.NewGuid(), (_, _) => Guid.NewGuid());
	}

	private void CancelTimer(uint raidId, uint type, uint dungeonId)
	{
		AdvanceTimer(raidId, type, dungeonId);
	}

	internal bool TimerCurrent(uint raidId, uint type, uint dungeonId, Guid version)
	{
		Guid current;
		return _timerVersions.TryGetValue(TimerKey(raidId, type, dungeonId), out current) && current == version;
	}

	private void CancelAllPhaseOneTimers(uint raidId)
	{
		uint[] antonFirstPhaseDungeonIds = AntonFirstPhaseDungeonIds;
		foreach (uint dungeonId in antonFirstPhaseDungeonIds)
		{
			for (uint type = 1u; type <= 3; type++)
			{
				CancelTimer(raidId, type, dungeonId);
			}
		}
	}

	private void CancelAllPhaseTwoTimers(uint raidId)
	{
		uint[] antonSecondPhaseDungeonIds = AntonSecondPhaseDungeonIds;
		foreach (uint dungeonId in antonSecondPhaseDungeonIds)
		{
			for (uint type = 1u; type <= 3; type++)
			{
				CancelTimer(raidId, type, dungeonId);
			}
		}
		CancelTimer(raidId, 4u, 219u);
	}

	internal void CleanupRaidRuntimeState(uint raidId)
	{
		_phaseRewardFlows.TryRemove(raidId, out var _);
		_infectionDungeonByRaid.TryRemove(raidId, out var value2);
		_blackVolcanoBarrierBroken.TryRemove(raidId, out var _);
		_raidDungeonStates.TryRemove(raidId, out var _);
		_raidRuntimeLocks.TryRemove(raidId, out var _);
		foreach (var key in _raidBuffActivations.Keys)
		{
			if (key.RaidId == raidId)
			{
				_raidBuffActivations.TryRemove(key, out var _);
			}
		}
		foreach (var key2 in _raidMonsterRuntimeValues.Keys)
		{
			if (key2.RaidId == raidId)
			{
				_raidMonsterRuntimeValues.TryRemove(key2, out var _);
			}
		}
		foreach (var key3 in _symbolValues.Keys)
		{
			if (key3.RaidId == raidId)
			{
				_symbolValues.TryRemove(key3, out value2);
			}
		}
		string timerPrefix = raidId + ":";
		foreach (string key4 in _timerVersions.Keys)
		{
			if (key4.StartsWith(timerPrefix, StringComparison.Ordinal))
			{
				_timerVersions.TryRemove(key4, out var _);
			}
		}
	}

	private static string TimerKey(uint raidId, uint type, uint dungeonId)
	{
		return raidId + ":" + type + ":" + dungeonId;
	}

	internal static bool IsAntonRaidDungeon(int dungeonId)
	{
		return IsAntonFirstPhaseDungeon(dungeonId) || (dungeonId >= 218 && dungeonId <= 224);
	}

	internal static bool IsAntonFirstPhaseDungeon(int dungeonId)
	{
		return dungeonId >= 210 && dungeonId <= 216;
	}

	internal static bool IsAntonDungeonForPhase(uint phaseIndex, int dungeonId)
	{
		return (phaseIndex == 0) ? IsAntonFirstPhaseDungeon(dungeonId) : (phaseIndex == 1 && dungeonId >= 218 && dungeonId <= 224);
	}

	private static uint GetAntonRequiredClears(uint dungeonId)
	{
		switch (dungeonId)
		{
		case 210u:
			return 4u;
		default:
			if (dungeonId != 215)
			{
				if (dungeonId == 220)
				{
					return 5u;
				}
				return 1u;
			}
			goto case 213u;
		case 213u:
			return 2u;
		}
	}

	internal static uint GetAntonHpSymbolId(uint dungeonId)
	{
		return (dungeonId <= 216) ? (dungeonId - 160) : (dungeonId - 161);
	}
}
