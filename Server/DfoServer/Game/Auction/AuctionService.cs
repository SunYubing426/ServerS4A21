using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using System;

namespace DfoServer.Game.Auction
{
    /// <summary>
    /// 拍卖行业务编排：一口价成交/下架/流拍的结算邮件、到期扫描与崩溃对账。
    /// 结算统一走系统邮件（道具附件 + 金币），复用邮件系统已验证的领取入包路径。
    /// </summary>
    public sealed class AuctionService
    {
        private const string SystemSenderName = "拍卖行";
        private readonly AuctionRepository _repository;
        private readonly MailboxService _mailbox;
        private readonly object _clockSync = new object();
        private bool _clockRegistered;

        public AuctionService(AuctionRepository repository, MailboxService mailbox)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
        }

        public AuctionRepository Repository => _repository;

        /// <summary>
        /// bot 补货挂牌成交后的回调（参数 = itemTemplateId），由 AuctionBotService 注册，
        /// 用于「卖出一件 → 该物品下次补货价 +3%」的动态调价。null 时不触发。
        /// </summary>
        internal Action<int> BotSaleSettledHook { get; set; }

        /// <summary>注册分钟级到期扫描（幂等，ClockService 单例上只挂一次）。</summary>
        public void RegisterClock(ClockService clock)
        {
            if (clock == null)
                return;

            lock (_clockSync)
            {
                if (_clockRegistered)
                    return;
                _clockRegistered = true;
                clock.RegisterMinuteTick(
                    "auction:expiry",
                    utcNow =>
                    {
                        try
                        {
                            Maintain(utcNow);
                        }
                        catch (Exception ex)
                        {
                            FileLogger.Log("[Auction] minute tick failed: " + ex);
                        }
                    });
            }
        }

        /// <summary>
        /// 到期维护：先把过期单标记为 Expired，再补发所有待结算邮件。
        /// 崩溃恢复：结算邮件发送成功前 settle_mail_count=0，重启后由本方法补发。
        /// </summary>
        public AuctionExpirationResult Maintain(DateTime utcNow)
        {
            var result = new AuctionExpirationResult();
            var nowUnix = new DateTimeOffset(utcNow).ToUnixTimeSeconds();
            result.ExpiredCount = _repository.MarkExpiredListings(nowUnix);

            // ★ 竞价成交（2026-09-04）：有竞价的到期单要转为 Sold（buyer=最高价者），
            //   不能按「退回卖家」处理。必须在 LoadPendingSettlements 之前调用。
            var soldByBid = _repository.MarkExpiredWithBidsAsSold(nowUnix);
            if (soldByBid > 0)
                FileLogger.Log($"[Auction] 竞价成交 {soldByBid} 笔到期挂牌（buyer=最高出价者）");

            // ★ 退回所有「被超价但金币尚未退回」的出价（崩溃恢复对账）。
            RefundPendingOutbids();

            var pending = _repository.LoadPendingSettlements(32);
            foreach (var listing in pending)
            {
                try
                {
                    if (Settle(listing))
                        result.SettledCount++;
                    else
                        result.FailedCount++;
                }
                catch (Exception ex)
                {
                    result.FailedCount++;
                    FileLogger.Log($"[Auction] settle listing={listing.ListingId} failed: {ex}");
                }
            }
            return result;
        }

        /// <summary>退回所有「被超价但金币尚未退回」的出价（幂等，按 refund_mail_sent 去重）。</summary>
        private void RefundPendingOutbids()
        {
            var pending = _repository.LoadPendingOutbidRefunds(32);
            foreach (var bid in pending)
            {
                try
                {
                    if (RefundOutbidGold(bid.BidderCharacterId, bid.BidAmount, bid.ListingId, bid.BidId))
                        _repository.MarkBidRefunded(bid.BidId);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[Auction] refund outbid bid={bid.BidId} failed: {ex}");
                }
            }
        }

        /// <summary>成交后立即结算（Handler 同步调用）；失败时留给分钟 tick 对账补发。</summary>
        public bool SettleSold(long listingId)
        {
            var listing = _repository.GetListing(listingId);
            if (listing == null || listing.SettleMailCount > 0)
                return listing != null;
            return Settle(listing);
        }

        /// <summary>下架后立即退回道具；失败时留给分钟 tick 对账补发。</summary>
        public bool SettleCancelled(long listingId)
        {
            var listing = _repository.GetListing(listingId);
            if (listing == null || listing.SettleMailCount > 0)
                return listing != null;
            return Settle(listing);
        }

        /// <summary>
        /// 退回被超价者的托管金币（系统邮件金币）。竞价出价金币「立即扣除托管」，
        /// 被更高出价顶掉时走这里退回。幂等由调用方用 refund_mail_sent 去重。
        /// </summary>
        /// <param name="bidId">被顶掉的出价记录 id。&gt;0 时用作唯一幂等 key（推荐）；
        /// 0 时回退到 (listingId, bidder, amount) 组合 key。</param>
        public bool RefundOutbidGold(int bidderCharacterId, int amount, long listingId, long bidId = 0)
        {
            if (bidderCharacterId <= 0 || amount <= 0)
                return false;

            // ★ 幂等 key 必须唯一：同一 bidder 在同一 listing 可能被超价多次（每次出价金额
            //   不同），若 key 只用 (listingId, bidder) 会撞 key 且金额不同导致 hash 不匹配
            //   → SendSystemMail 返回 InvalidRequest。故优先用 bidId，回退时把金额并入 key。
            var idempotencyKey = bidId > 0
                ? $"auction:outbid:bid:{bidId}"
                : $"auction:outbid:{listingId}:{bidderCharacterId}:{amount}";

            var mail = BuildSystemMail(
                bidderCharacterId,
                "拍卖行竞价被超越",
                $"您的出价 {amount:N0} 金币已被更高的出价超越，托管金币已退回。",
                gold: amount,
                attachment: null,
                idempotencyKey: idempotencyKey);
            var result = _mailbox.SendSystemMail(mail);
            if (!result.Success)
            {
                FileLogger.Log(
                    $"[Auction] refund outbid gold failed listing={listingId} " +
                    $"bidder={bidderCharacterId} amount={amount} error={result.Error}");
                return false;
            }

            FileLogger.Log(
                $"[Auction] refunded outbid gold listing={listingId} bidder={bidderCharacterId} amount={amount}");
            return true;
        }

        private bool Settle(AuctionListing listing)
        {
            // ★ 金币券挂牌结算：成交（Sold）走【买家金币邮件】SettleGoldSold；
            //   下架/流拍（Cancelled/Expired）走 SettleGoldReturn 直接退金币。
            //   任何金币券都不能落入下面的「实体道具附件」邮件路径，否则会把
            //   「金币券」当道具附件发邮件，造成金币券凭空出现（dupe）。
            if (AuctionRepository.IsGoldItem(listing.Item.ItemTemplateId))
            {
                return listing.Status == AuctionListingStatus.Sold
                    ? SettleGoldSold(listing)
                    : _repository.SettleGoldReturn(listing.ListingId);
            }

            switch (listing.Status)
            {
                case AuctionListingStatus.Sold:
                    return SettleSoldListing(listing);
                case AuctionListingStatus.Cancelled:
                case AuctionListingStatus.Expired:
                    return SettleReturnListing(listing);
                default:
                    return false;
            }
        }

        // 金币券成交：买家收【金币邮件】（面额 × 张数）。卖家代币券已在 BuyoutGold
        // 事务内即时入账（邮件系统不支持代币券附件），这里只补发买家金币邮件。
        // 幂等靠 settle_mail_count（MarkSettled 后分钟 tick 不再捞）。
        private bool SettleGoldSold(AuctionListing listing)
        {
            if (!AuctionRepository.TryGetGoldValue(listing.Item.ItemTemplateId, out var face)
                || face <= 0)
                return false;

            var count = Math.Max(1, listing.Item.ItemCount);
            var goldLong = (long)face * count;
            if (goldLong > int.MaxValue)
                goldLong = int.MaxValue;
            var gold = (int)goldLong;

            var buyerMail = BuildSystemMail(
                listing.BuyerCharacterId,
                "金币寄售购买成功",
                $"您购买的金币已到账，金额 {gold:N0}。",
                gold: gold,
                attachment: null,
                idempotencyKey: $"auction:goldbuy:{listing.ListingId}");
            var buyerResult = _mailbox.SendSystemMail(buyerMail);
            if (!buyerResult.Success)
            {
                FileLogger.Log(
                    $"[Auction] settle gold-buyer mail failed listing={listing.ListingId} " +
                    $"error={buyerResult.Error}");
                return false;
            }

            _repository.MarkSettled(listing.ListingId, 1);
            FileLogger.Log(
                $"[Auction] settled gold listing={listing.ListingId} buyer={listing.BuyerCharacterId} gold={gold}");
            return true;
        }

        // 成交：买家收道具邮件，卖家收金币邮件（扣成交税）。
        // ★ bot 感知（AuctionBotService）：
        //   · 买家是 bot（回收玩家挂牌）→ 跳过买家道具邮件（道具由系统回收销毁），
        //     只发卖家金币邮件；
        //   · 卖家是 bot（补货被玩家买走）→ 跳过卖家金币邮件（bot 无钱包），
        //     结算成功后触发 BotSaleSettledHook（该物品下次补货价 +3%）。
        private bool SettleSoldListing(AuctionListing listing)
        {
            // ★ 成交价：竞价成交用 current_bid（最高出价），一口价用单价 × 数量。
            //   两种成交 BuyerCharacterId 都由结算前置逻辑填好（一口价=Buyout 里写，
            //   竞价=MarkExpiredWithBidsAsSold 写 current_bidder）。
            var dealPrice = listing.CurrentBid > 0
                ? (long)listing.CurrentBid
                : (long)listing.BuyoutPrice * Math.Max(1, listing.Item.ItemCount);
            if (dealPrice > int.MaxValue)
                dealPrice = int.MaxValue;

            var buyerIsBot = listing.BuyerCharacterId == AuctionBotService.BotCharacterId;
            var sellerIsBot = listing.SellerCharacterId == AuctionBotService.BotCharacterId;

            if (!buyerIsBot)
            {
                var buyerMail = BuildSystemMail(
                    listing.BuyerCharacterId,
                    "拍卖行购买成功",
                    $"您购买的物品已投递，成交价 {dealPrice:N0} 金币。",
                    gold: 0,
                    attachment: listing.Item,
                    idempotencyKey: $"auction:item:{listing.ListingId}");
                var buyerResult = _mailbox.SendSystemMail(buyerMail);
                if (!buyerResult.Success)
                {
                    FileLogger.Log(
                        $"[Auction] settle buyer mail failed listing={listing.ListingId} " +
                        $"error={buyerResult.Error}");
                    return false;
                }
            }

            var proceeds = sellerIsBot
                ? 0
                : AuctionPolicy.CalculateSellerProceeds((int)dealPrice);
            if (proceeds > 0)
            {
                var sellerMail = BuildSystemMail(
                    listing.SellerCharacterId,
                    "拍卖行物品售出",
                    $"您的物品已售出，扣除成交税后入账 {proceeds:N0} 金币。",
                    gold: proceeds,
                    attachment: null,
                    idempotencyKey: $"auction:gold:{listing.ListingId}");
                var sellerResult = _mailbox.SendSystemMail(sellerMail);
                if (!sellerResult.Success)
                {
                    // 买家邮件已发出；金币邮件失败时只重试金币部分，
                    // 用独立幂等键 + settle_mail_count=1 标记半完成态。
                    FileLogger.Log(
                        $"[Auction] settle seller gold mail failed listing={listing.ListingId} " +
                        $"error={sellerResult.Error}");
                    _repository.MarkSettled(listing.ListingId, 1);
                    return false;
                }
            }

            _repository.MarkSettled(listing.ListingId, buyerIsBot || sellerIsBot ? 1 : 2);

            // bot 补货成交：触发「卖出 → 下次补货价 +3%」调价（MarkSettled 之后，
            // 避免邮件失败重试时重复上调）。
            if (sellerIsBot)
            {
                try
                {
                    BotSaleSettledHook?.Invoke(listing.Item.ItemTemplateId);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[Auction] bot sale hook failed listing={listing.ListingId}: {ex.Message}");
                }
            }

            FileLogger.Log(
                $"[Auction] settled listing={listing.ListingId} seller={listing.SellerCharacterId} " +
                $"buyer={listing.BuyerCharacterId} price={dealPrice} proceeds={proceeds}");
            return true;
        }

        // 下架/流拍：道具退回卖家。
        // ★ bot 感知：bot 补货挂牌到期流拍时不发退回邮件（bot 无角色邮箱，
        //   发了必失败且每分钟重试刷屏），直接标记结算，道具由系统回收销毁。
        private bool SettleReturnListing(AuctionListing listing)
        {
            if (listing.SellerCharacterId == AuctionBotService.BotCharacterId)
            {
                _repository.MarkSettled(listing.ListingId, 1);
                FileLogger.Log(
                    $"[Auction] bot listing expired/cancelled, discarded listing={listing.ListingId} " +
                    $"item={listing.Item.ItemTemplateId}");
                return true;
            }

            var reason = listing.Status == AuctionListingStatus.Cancelled
                ? "您下架的物品已退回。"
                : "物品到期未售出，已退回。";
            var mail = BuildSystemMail(
                listing.SellerCharacterId,
                listing.Status == AuctionListingStatus.Cancelled ? "拍卖行物品退回" : "拍卖行流拍退回",
                reason,
                gold: 0,
                attachment: listing.Item,
                idempotencyKey: $"auction:return:{listing.ListingId}");
            var result = _mailbox.SendSystemMail(mail);
            if (!result.Success)
            {
                FileLogger.Log(
                    $"[Auction] return mail failed listing={listing.ListingId} error={result.Error}");
                return false;
            }

            _repository.MarkSettled(listing.ListingId, 1);
            FileLogger.Log($"[Auction] returned listing={listing.ListingId} to seller={listing.SellerCharacterId}");
            return true;
        }

        private static MailboxSendRequest BuildSystemMail(
            int receiverCharacterId,
            string title,
            string text,
            int gold,
            AuctionItemSnapshot attachment,
            string idempotencyKey)
        {
            var request = new MailboxSendRequest
            {
                SenderCharacterId = receiverCharacterId,
                SenderName = SystemSenderName,
                ReceiverCharacterId = receiverCharacterId,
                Gold = gold,
                Title = string.Empty,
                Text = title + "：" + text,
                MailType = 1,
                SourceProtocol = 0,
                IdempotencyKey = idempotencyKey,
                AuditActor = "auction-system",
                AuditReason = $"auction listing {idempotencyKey}",
            };

            if (attachment != null)
            {
                request.Attachments = new[]
                {
                    new MailboxSendAttachmentRequest
                    {
                        ItemType = attachment.ItemType,
                        ItemId = attachment.ItemTemplateId,
                        ItemCount = Math.Max(1, attachment.ItemCount),
                        InstanceValue = attachment.InstanceValue,
                        Durability = attachment.Durability,
                        SealFlag = attachment.SealFlag,
                        OptionValue = attachment.OptionValue,
                        ExpireTime = attachment.ExpireTime,
                        Marker16 = attachment.Marker16,
                        PetSerialOrHandle = attachment.PetSerialOrHandle,
                        ExtraJson = attachment.ExtraJson,
                        ItemCoreData = attachment.ItemCoreData,
                        DetailJson = attachment.DetailJson,
                    },
                };
            }
            return request;
        }
    }
}
