using DfoServer.Game.DailyReset;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Premium
{
    internal readonly struct BlackDiamondFatigueCoinJudgeResult
    {
        internal static BlackDiamondFatigueCoinJudgeResult Ok(bool mailDelivered)
            => new BlackDiamondFatigueCoinJudgeResult(true, mailDelivered);

        internal static BlackDiamondFatigueCoinJudgeResult Failed { get; } =
            new BlackDiamondFatigueCoinJudgeResult(false, false);

        private BlackDiamondFatigueCoinJudgeResult(bool success, bool mailDelivered)
        {
            Success = success;
            MailDelivered = mailDelivered;
        }

        internal bool Success { get; }
        internal bool MailDelivered { get; }
    }

    internal sealed class BlackDiamondFatigueCoinService
    {
        internal const int CoinItemId = 7454;
        internal const int FatigueThreshold = 150;
        internal const string DailyJudgeKey = "black_diamond_fatigue_coin_judged";
        internal const string TitleKey = "chn_game_server_msg_23";
        internal const string BodyKey = "chn_game_server_msg_24";

        private readonly MailboxService _mailbox;
        private readonly Func<
            SqliteConnection,
            SqliteTransaction,
            MailboxSendRequest,
            MailboxSendResult> _sendMail;

        internal BlackDiamondFatigueCoinService(MailboxService mailbox)
            : this(mailbox, sendMail: null)
        {
        }

        internal BlackDiamondFatigueCoinService(
            MailboxService mailbox,
            Func<
                SqliteConnection,
                SqliteTransaction,
                MailboxSendRequest,
                MailboxSendResult> sendMail)
        {
            _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
            _sendMail = sendMail;
        }

        internal BlackDiamondFatigueCoinJudgeResult TryJudgeCrossing(
            SqliteConnection connection,
            SqliteTransaction transaction,
            DailyResetService dailyReset,
            int characterId,
            int accountId,
            DateTime utcNow)
        {
            if (connection == null
                || transaction == null
                || dailyReset == null
                || characterId <= 0
                || accountId <= 0)
            {
                return BlackDiamondFatigueCoinJudgeResult.Failed;
            }

            if (dailyReset.GetCounter(
                    connection,
                    transaction,
                    characterId,
                    DailyJudgeKey,
                    utcNow) > 0)
            {
                return BlackDiamondFatigueCoinJudgeResult.Ok(false);
            }

            if (!dailyReset.TryClaimFlag(
                    connection,
                    transaction,
                    characterId,
                    DailyJudgeKey,
                    DailyResetService.PeriodDay,
                    utcNow))
            {
                return BlackDiamondFatigueCoinJudgeResult.Ok(false);
            }

            if (!PremiumService.HasActiveBlackDiamond(
                    connection,
                    transaction,
                    accountId,
                    utcNow))
            {
                return BlackDiamondFatigueCoinJudgeResult.Ok(false);
            }

            if (!HardcodedTextTagCatalog.TryGet(TitleKey, out var title)
                || !HardcodedTextTagCatalog.TryGet(BodyKey, out var body))
            {
                FileLogger.Log(
                    "[BlackDiamondFatigueCoin] missing PVF mail strings " +
                    TitleKey + "/" + BodyKey);
                return BlackDiamondFatigueCoinJudgeResult.Failed;
            }

            var dayId = DailyResetService.TodayId(utcNow);
            var request = new MailboxSendRequest
            {
                SenderCharacterId = characterId,
                SenderAccountId = accountId,
                SenderName = string.Empty,
                ReceiverCharacterId = characterId,
                ReceiverAccountId = accountId,
                Title = title,
                Text = body,
                MailType = 1,
                Unlimited = true,
                IdempotencyKey =
                    "black-diamond-fatigue-coin:" + characterId + ":" + dayId,
                AuditActor = "black-diamond-fatigue-coin",
                AuditReason = DailyJudgeKey,
                Attachments = new[]
                {
                    new MailboxSendAttachmentRequest
                    {
                        ItemId = CoinItemId,
                        ItemCount = 1,
                    },
                },
            };

            var send = _sendMail != null
                ? _sendMail(connection, transaction, request)
                : _mailbox.SendSystemMails(
                    connection,
                    transaction,
                    new[] { request });
            if (send == null || !send.Success)
            {
                FileLogger.Log(
                    "[BlackDiamondFatigueCoin] system mail failed: " +
                    "cid=" + characterId +
                    " error=" + (send?.Error.ToString() ?? "null"));
                return BlackDiamondFatigueCoinJudgeResult.Failed;
            }

            return BlackDiamondFatigueCoinJudgeResult.Ok(!send.IdempotencyReplay);
        }
    }
}
