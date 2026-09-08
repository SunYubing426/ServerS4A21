using DfoServer.Game.SelectCharacter;
using System;

namespace DfoServer.Network.Builders
{
    /// <summary>
    /// 进号阶段的拍卖行服务开启通知（NOTI AUCTION_NOTIFY_AUCTION_SERVICE 0x00B7）。
    /// 包体布局与 Network/Builders/Auction/AuctionServiceNotificationBuilder 对齐：
    /// [serviceType][openState]。进号序列连发两遍（occurrence 0 → serviceType 0，
    /// occurrence 1 → serviceType 1），客户端据此启用拍卖行 UI。
    /// </summary>
    public sealed class AuctionServiceInitBodyBuilder : IInitPacketBuilder
    {
        private const byte OpenState = 0x01;

        public ushort NotiType => (ushort)NotiPacketTypeA21.AUCTION_NOTIFY_AUCTION_SERVICE;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var serviceType = occurrenceIndex <= 0 ? (byte)0x00 : (byte)0x01;
            body = new[] { serviceType, OpenState };
            return true;
        }
    }
}
