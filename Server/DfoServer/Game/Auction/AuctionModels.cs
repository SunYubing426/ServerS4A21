using System;

namespace DfoServer.Game.Auction
{
    /// <summary>拍卖行上架单状态机（ClockService.md 约定：跨重启存活，存绝对到期时间）。</summary>
    public enum AuctionListingStatus
    {
        /// <summary>上架中，可被购买/下架。</summary>
        Active = 0,
        /// <summary>已售出（等待/已完成结算邮件）。</summary>
        Sold = 1,
        /// <summary>卖家主动下架（等待/已完成退回邮件）。</summary>
        Cancelled = 2,
        /// <summary>到期流拍（等待/已完成退回邮件）。</summary>
        Expired = 3,
        /// <summary>结算完成（退回/成交邮件均已发出）。</summary>
        Settled = 4,
    }

    /// <summary>拍卖行业务错误码（与 MailboxSendError 风格一致，供 Handler 映射协议错误包）。</summary>
    public enum AuctionError
    {
        None,
        InvalidRequest,
        CharacterNotFound,
        ItemNotFound,
        ItemNotTradable,
        InventorySlotEmpty,
        PriceOutOfRange,
        ListingLimitReached,
        GoldLimitExceeded,
        InsufficientGold,
        ListingNotFound,
        ListingNotActive,
        OwnListing,
        ServerBusy,
        BidTooLow,
    }

    /// <summary>拍卖行上架道具快照（字段与 MailboxSendAttachmentRequest 对齐，成交后可直接构造系统邮件附件）。</summary>
    public sealed class AuctionItemSnapshot
    {
        public byte ItemType { get; set; }
        public int SourceListType { get; set; }
        public int SourceSlotIndex { get; set; }
        public int ItemTemplateId { get; set; }
        public string ItemKind { get; set; } = "unknown";
        public int ItemCount { get; set; }
        public int InstanceValue { get; set; }
        public int Durability { get; set; }
        public int SealFlag { get; set; }
        public int OptionValue { get; set; }
        public int ExpireTime { get; set; }
        public int Marker16 { get; set; }
        public int PetSerialOrHandle { get; set; }
        public string ExtraJson { get; set; } = "{}";
        public byte[] ItemCoreData { get; set; } = Array.Empty<byte>();
        public string DetailJson { get; set; } = string.Empty;
    }

    /// <summary>拍卖行上架单（数据库行投影）。</summary>
    public sealed class AuctionListing
    {
        public long ListingId { get; set; }
        public int SellerCharacterId { get; set; }
        public string SellerName { get; set; } = string.Empty;
        public AuctionItemSnapshot Item { get; set; } = new AuctionItemSnapshot();
        public int BuyoutPrice { get; set; }
        public AuctionListingStatus Status { get; set; }
        public int BuyerCharacterId { get; set; }
        public string BuyerName { get; set; } = string.Empty;
        public long ListedAtUnix { get; set; }
        public long ExpiresAtUnix { get; set; }
        public long SoldAtUnix { get; set; }
        public int SettleMailCount { get; set; }

        // ---- 起拍价（2026-09-04 引入，支持「竞拍价上架」）----
        /// <summary>起拍价（单价，纯竞拍上架时一口价可为 0）。0 = 未设起拍价（纯一口价）。</summary>
        public int StartingPrice { get; set; }

        // ---- 竞价字段（2026-09-04 引入，出价金币立即扣除托管）----
        /// <summary>当前最高出价（0 = 无人竞价）。</summary>
        public int CurrentBid { get; set; }
        /// <summary>当前最高出价者角色 ID（0 = 无）。</summary>
        public int CurrentBidderId { get; set; }
        /// <summary>当前最高出价者角色名。</summary>
        public string CurrentBidderName { get; set; } = string.Empty;
        /// <summary>累计出价次数。</summary>
        public int BidCount { get; set; }
    }

    /// <summary>一次竞价出价（auction_bids 表行投影，供「我的竞价」0x00BD 展示）。</summary>
    public sealed class AuctionBidRecord
    {
        public long BidId { get; set; }
        public long ListingId { get; set; }
        public int BidderCharacterId { get; set; }
        public int BidAmount { get; set; }
        public long BidAtUnix { get; set; }
        // ★ round57：补充 bid 状态字段（领先中 / 被超价 / 中标 / 流拍）。
        //   LoadMyActiveBids / 新 LoadMyBidsByBidder SQL 已 SELECT status，
        //   旧 LoadBidsByListing / LoadPendingOutbidRefunds SQL 只 5 列不读此处（ReadBidRecord 兼容）。
        public int Status { get; set; }
    }

    public sealed class AuctionRegisterResult
    {
        public bool Success { get; set; }
        public AuctionError Error { get; set; }
        public long ListingId { get; set; }
        public int FeeGold { get; set; }
        public int GoldAfter { get; set; }

        public static AuctionRegisterResult Fail(AuctionError error)
            => new AuctionRegisterResult { Success = false, Error = error };
    }

    public sealed class AuctionBuyoutResult
    {
        public bool Success { get; set; }
        public AuctionError Error { get; set; }
        public long ListingId { get; set; }
        public int GoldAfter { get; set; }

        /// <summary>true = 本次成交是金币券挂牌（扣点券/发金币，已即时结算，无需走邮件）。</summary>
        public bool IsGold { get; set; }

        public static AuctionBuyoutResult Fail(AuctionError error)
            => new AuctionBuyoutResult { Success = false, Error = error };
    }

    public sealed class AuctionCancelResult
    {
        public bool Success { get; set; }
        public AuctionError Error { get; set; }
        public long ListingId { get; set; }

        /// <summary>true = 本次下架是金币券挂牌（金币直接退回虚拟槽，无道具槽位）。</summary>
        public bool IsGold { get; set; }

        /// <summary>道具退回的背包列表类型（供 handler 刷新客户端背包 UI）。金币挂牌为 -1。</summary>
        public int ReturnedListType { get; set; } = -1;

        /// <summary>道具退回的槽位（供 handler 刷新客户端背包 UI）。金币挂牌为 -1。</summary>
        public int ReturnedSlotIndex { get; set; } = -1;

        public static AuctionCancelResult Fail(AuctionError error)
            => new AuctionCancelResult { Success = false, Error = error };
    }

    public sealed class AuctionExpirationResult
    {
        public int ExpiredCount { get; set; }
        public int SettledCount { get; set; }
        public int FailedCount { get; set; }
    }

    /// <summary>竞价出价结果（出价金币立即扣除托管）。</summary>
    public sealed class AuctionBidResult
    {
        public bool Success { get; set; }
        public AuctionError Error { get; set; }
        public long ListingId { get; set; }
        /// <summary>本次出价金额（已托管扣除）。</summary>
        public int BidAmount { get; set; }
        /// <summary>托管扣除后买家剩余金币（供 handler 同步内存背包）。</summary>
        public int GoldAfter { get; set; }
        /// <summary>被本次出价顶掉的上一任最高出价者（需退回其托管金币），0=无。</summary>
        public int OutbidCharacterId { get; set; }
        /// <summary>被顶掉的上任出价金额（退回金额）。</summary>
        public int OutbidAmount { get; set; }
        /// <summary>被顶掉的那条出价记录 id（供幂等退回用），0=无。</summary>
        public long OutbidBidId { get; set; }

        public static AuctionBidResult Fail(AuctionError error)
            => new AuctionBidResult { Success = false, Error = error };
    }
}
