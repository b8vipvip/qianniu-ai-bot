using Bot.ChatRecord;
using BotLib;
using DbEntity;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SuperWebSocket;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot
{
    public partial class App
    {
        // Compatibility bootstrap retained so existing startup/build wiring does not change.
        // First-inquiry delivery lives before AI routing, and background notifications now have a
        // read-only fast path so the first real buyer message does not wait for full conversation
        // switching/history hydration when receiveNewMsg is missing.
        private readonly object _firstInquiryStreamingGuardBootstrap =
            ChromeNs.FirstInquiryStreamingGuard.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    /// <summary>
    /// Keeps first-inquiry handling outside the buyer-message merge/AI path.
    ///
    /// Normal receiveNewMsg traffic still flows directly into BuyerMessageBurstCoordinator, where
    /// deterministic replies are evaluated before the quiet-delay merge. When Qianniu only emits
    /// onShopRobotReceriveNewMsgs, this guard uses the notification's own ccode to read recent
    /// history immediately. It never switches the visible chat and never sends by itself; it only
    /// restores the earliest real buyer-authored message into the existing authoritative pipeline.
    /// </summary>
    internal static class FirstInquiryStreamingGuard
    {
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                MyWebSocketServer.WSocketSvrInst.OnRecieveMessage += OnWebSocketMessage;
                Log.Info(
                    "首条咨询固定回复保持协调器前置直发：不等待消息合并、不等待AI接口；"
                    + "已启用后台通知ccode首消息快路径。" );
            }
            return new object();
        }

        private static void OnWebSocketMessage(object sender, WSocketNewMessageEventArgs e)
        {
            if (e == null
                || !string.Equals(e.Type, "onShopRobotReceriveNewMsgs", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(e.Value))
            {
                return;
            }

            try
            {
                var active = JsonConvert.DeserializeObject<ActiveLocalUser>(e.Value);
                var seller = active == null || active.LoginID == null
                    ? string.Empty
                    : (active.LoginID.Nick ?? string.Empty).Trim();
                var buyer = active == null || active.Conversation == null
                    ? string.Empty
                    : (active.Conversation.Nick ?? string.Empty).Trim();
                var ccode = active == null || active.Conversation == null
                    ? string.Empty
                    : (active.Conversation.Ccode ?? string.Empty).Trim();
                if (seller.Length == 0 || buyer.Length == 0 || ccode.Length == 0) return;

                var qn = QN.FindExistingBySellerNick(seller);
                if (qn == null) return;
                qn.ScheduleFirstInquiryFastRecovery(seller, buyer, ccode, DateTime.Now);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("首条咨询后台通知快路径解析失败，保留原后台补偿: " + ex.Message, 20);
            }
        }
    }

    public partial class QN
    {
        private readonly ConcurrentDictionary<string, byte> _firstInquiryFastRecoveryActive =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTime> _firstInquiryFastRecoveryWindowStart =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        internal void ScheduleFirstInquiryFastRecovery(
            string seller,
            string buyer,
            string ccode,
            DateTime notificationAt)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = BuyerIdentityAliasService.ResolveInternalNick(seller, buyer);
            ccode = (ccode ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0 || ccode.Length == 0) return;
            if (!Params.Robot.CanUseRobotReal) return;

            var key = RecoveryKey(seller, buyer);
            DateTime windowStart;
            if (!_firstInquiryFastRecoveryWindowStart.TryGetValue(key, out windowStart)
                || notificationAt - windowStart > TimeSpan.FromSeconds(30))
            {
                windowStart = notificationAt;
                _firstInquiryFastRecoveryWindowStart[key] = windowStart;
            }

            // Later product/system notifications for the same buyer must not restart the first
            // recovery attempt. The earliest pending notification owns this short recovery window.
            if (!_firstInquiryFastRecoveryActive.TryAdd(key, 0))
            {
                Log.Info("首条咨询后台通知快路径已在执行，后续同买家通知已合并: seller="
                    + seller + ", buyer=" + buyer);
                return;
            }

            Task.Run(async () =>
            {
                var recovered = false;
                try
                {
                    recovered = await TryRecoverFirstInquiryFromNotificationAsync(
                        seller,
                        buyer,
                        ccode,
                        windowStart).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Info("首条咨询后台通知快路径失败，保留原后台补偿: seller=" + seller
                        + ", buyer=" + buyer + ", error=" + ex.Message);
                }
                finally
                {
                    byte ignored;
                    _firstInquiryFastRecoveryActive.TryRemove(key, out ignored);
                    if (recovered || DateTime.Now - windowStart > TimeSpan.FromSeconds(30))
                    {
                        DateTime ignoredStart;
                        _firstInquiryFastRecoveryWindowStart.TryRemove(key, out ignoredStart);
                    }
                }
            });
        }

        private async Task<bool> TryRecoverFirstInquiryFromNotificationAsync(
            string seller,
            string buyer,
            string ccode,
            DateTime windowStart)
        {
            var key = RecoveryKey(seller, buyer);

            // Give a normal detailed receiveNewMsg a very small head start. This is deliberately far
            // shorter than the ordinary 1s background-recovery delay and has no message-merge wait.
            await Task.Delay(80).ConfigureAwait(false);

            DateTime observedAt;
            if (_latestBuyerMessageObserved.TryGetValue(key, out observedAt)
                && observedAt >= windowStart.AddMilliseconds(-250))
            {
                return true;
            }
            if (cdp == null || cdp.IsInvalidated) return false;

            Log.Info("首条咨询后台通知快路径开始: seller=" + seller + ", buyer=" + buyer
                + ", directCcode=true, mergeWait=false");

            var history = await cdp.Invoke<JObject>("im.singlemsg.GetRemoteHisMsg", new
            {
                cid = new { ccode = ccode, type = 1 },
                count = 30,
                gohistory = 1,
                msgid = "-1",
                msgtime = "-1"
            }).ConfigureAwait(false);
            if (history == null) return false;

            var messages = history["result"]?["msgs"]?.ToObject<List<QNChatMessage>>();
            var threshold = windowStart.AddSeconds(-12).Ticks;
            var recentBuyerMessages = (messages ?? new List<QNChatMessage>())
                .Where(m => m != null && m.fromid != null && m.toid != null)
                .Where(m => m.toid.nick == seller
                    && BuyerIdentityAliasService.AreEquivalent(seller, m.fromid.nick, buyer))
                .Where(m =>
                {
                    var sort = IncomingMessageSafety.GetSortValue(m);
                    return sort > 0 && sort >= threshold;
                })
                .OrderBy(IncomingMessageSafety.GetSortValue)
                .ToList();

            // "First inquiry" means the earliest real buyer-authored content. Product-page metadata,
            // automatically injected item links/cards, platform tips and withdrawal notices may be
            // useful context later, but they are never allowed to own or delay the first-inquiry slot.
            var first = recentBuyerMessages.FirstOrDefault(IsRealFirstInquiryMessage);
            if (first == null)
            {
                Log.Info("首条咨询后台通知快路径未发现真实买家首消息，保留原后台补偿: seller="
                    + seller + ", buyer=" + buyer);
                return false;
            }

            await _incomingMessageGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_latestBuyerMessageObserved.TryGetValue(key, out observedAt)
                    && observedAt >= windowStart.AddMilliseconds(-250))
                {
                    return true;
                }

                // Claim the recovery only after a real buyer message is present. This cancels the
                // slower switch/hydration fallback without falsely suppressing it on an empty read.
                MarkBuyerMessageObserved(seller, buyer);
            }
            finally
            {
                _incomingMessageGate.Release();
            }

            var firstKey = IncomingMessageSafety.BuildMessageKey(first, GetMessageText(first));
            Log.Info("首条咨询后台通知快路径命中首个真实买家消息: seller=" + seller
                + ", buyer=" + buyer + ", key=" + firstKey
                + ", mergeWait=false, aiGate=false");

            // Feed the first real message alone into the existing recovered-message pipeline first.
            // BuyerMessageBurstCoordinator then executes DeterministicAutoReplyService before merge.
            await ProcessRecoveredBuyerMessageAfterMissAsync(first, seller, buyer).ConfigureAwait(false);

            // Let the first message acquire the per-buyer deterministic gate before replaying any
            // later item-link/system-context messages from the same remote-history batch.
            await Task.Delay(160).ConfigureAwait(false);

            foreach (var message in recentBuyerMessages)
            {
                if (ReferenceEquals(message, first)) continue;
                try
                {
                    await ProcessRecoveredBuyerMessageAfterMissAsync(message, seller, buyer).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Info("首条咨询快路径后续消息回放失败: seller=" + seller
                        + ", buyer=" + buyer + ", error=" + ex.Message);
                }
                await Task.Delay(20).ConfigureAwait(false);
            }
            return true;
        }

        private static bool IsRealFirstInquiryMessage(QNChatMessage message)
        {
            if (message == null) return false;
            var text = GetMessageText(message);
            if (ConversationContextStore.IsPlatformSystemTip(message, text)) return false;
            if (ConversationContextStore.IsProductLink(message, text)) return false;
            if (ConversationContextStore.IsWithdrawalNotice(message, text)) return false;

            var display = IncomingMessageSafety.GetDisplayText(message, text);
            return !string.IsNullOrWhiteSpace(display)
                && !string.Equals(display, "[空白或未知消息]", StringComparison.Ordinal);
        }
    }
}
