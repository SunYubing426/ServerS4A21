using DfoServer.Game.ReviveCoin;
using DfoServer.Network.Handlers;
using System;

namespace DfoServer.Infrastructure
{
    // 城镇与地下城共享同一在线世界状态和复活币服务。
    internal sealed class GameProtocolTownDungeonHandlers
    {
        internal GameProtocolTownDungeonHandlers(
            ReviveCoinService reviveCoin,
            TownHandler town,
            DungeonHandler dungeon)
        {
            ReviveCoin = reviveCoin
                ?? throw new ArgumentNullException(nameof(reviveCoin));
            Town = town ?? throw new ArgumentNullException(nameof(town));
            Dungeon = dungeon ?? throw new ArgumentNullException(nameof(dungeon));
            // 副本回城(结算/跟随退出)后的城镇同屏投影接线(最小移植自 MR !22):
            // 不接时回城者只收到自己的 0x0017/0x0018, 不进城镇在场名单,
            // 队友互相看不见且组队进本提示"不在附近"。
            Dungeon.ConfigureTownPresenceProjection(
                Town.ProjectDungeonTownPresenceAsync);
        }

        internal ReviveCoinService ReviveCoin { get; }

        internal TownHandler Town { get; }

        internal DungeonHandler Dungeon { get; }
    }
}
