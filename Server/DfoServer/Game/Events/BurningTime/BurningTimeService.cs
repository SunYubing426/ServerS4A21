using System;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Events.BurningTime
{
    internal sealed class BurningTimeService
    {
        private readonly IGameDatabase _database;
        private readonly BurningTimeConfigProvider _configProvider;
        private readonly BurningTimeConfig _configOverride;
        private readonly BurningTimeRepository _repository;

        internal BurningTimeService(
            IGameDatabase database,
            BurningTimeConfigProvider configProvider = null,
            BurningTimeConfig config = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _configProvider = configProvider ?? BurningTimeConfigProvider.Instance;
            _configOverride = config;
            _repository = new BurningTimeRepository(_database);
        }

        private BurningTimeConfig CurrentConfig =>
            _configOverride ?? _configProvider.Current;

        internal void Initialize()
        {
            _repository.EnsureStaticConfigRows(CurrentConfig);
        }

        internal bool IsEnabled()
        {
            try
            {
                return _database.Read(connection =>
                    _repository.IsEnabled(connection, null));
            }
            catch (Exception ex)
            {
                FileLogger.Log("[BurningTime] enabled check failed: " + ex);
                return false;
            }
        }

        internal BurningTimeSnapshot GetSnapshot()
        {
            var config = CurrentConfig;
            return new BurningTimeSnapshot
            {
                EventId = BurningTimeConfig.EventId,
                SeasonId = config.SeasonId,
                EventEnabled = IsEnabled(),
                ActivePhaseIndex = 1,
            };
        }

        internal void ApplyActiveBuffs(int characterId)
        {
            if (!IsEnabled())
                return;

            FileLogger.Log(
                "[BurningTime] apply buffs requested for "
                + $"cid={characterId} (server-side buff application not implemented)");
        }
    }
}
