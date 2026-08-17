using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Executes deterministic buyer replies before the burst/context merge window.
    /// Fixed replies never inspect or wait for AI endpoints. The strict order is:
    /// first-inquiry greeting -> off-hours reply -> ordinary burst/context/AI handling.
    /// </summary>
    internal static class DeterministicAutoReplyService
    {
        private const string DefaultOffHoursReply =
            "亲，人工客服当前已下班，工作时间为每天 {工作时间}。您的问题已记录，请在上班时间联系或等待人工处理。";

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> BuyerGates =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, DateTime> OffHoursDeliveredUntil =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        /// <summary>
        /// Returns true when the original buyer message should continue into the burst/context
        /// merge pipeline. Returns false when a higher-priority fixed rule owns this message.
        /// </summary>
        public static async Task<bool> HandleBeforeMergeAsync(BuyerMessageBurstItem item)
        {
            if (item == null
                || string.IsNullOrWhiteSpace(item.SellerNick)
                || string.IsNullOrWhiteSpace(item.BuyerNick)
                || string.IsNullOrWhiteSpace(item.DisplayText)
                || !Params.Robot.CanUseRobotReal)
            {
                return true;
            }

            if (item.SafetyDecision != null && !item.SafetyDecision.ShouldCallAi)
            {
                return true;
            }
            if (item.VisionDecision != null && item.VisionDecision.Kind == VisionDecisionKind.Skip)
            {
                return true;
            }

            var key = Key(item.SellerNick, item.BuyerNick);
            var gate = BuyerGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                ShopContext shop = null;
                try { shop = ShopContextLocator.ResolveRuntimeBySellerNick(item.SellerNick); }
                catch { shop = null; }

                if (shop != null)
                {
                    using (ShopSettingsScope.Enter(shop))
                    {
                        return await HandleScopedBeforeMergeAsync(item, key);
                    }
                }
                return await HandleScopedBeforeMergeAsync(item, key);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount(
                    "固定规则前置处理异常，继续普通消息链路: seller=" + item.SellerNick
                    + ", buyer=" + item.BuyerNick
                    + ", error=" + ex.Message,
                    20);
                return true;
            }
            finally
            {
                gate.Release();
            }
        }

        private static async Task<bool> HandleScopedBeforeMergeAsync(
            BuyerMessageBurstItem item,
            string buyerKey)
        {
            // The global auto-reply switch still controls all automatic sends. When it is off,
            // preserve the historical merge/display path without attempting a fixed send.
            if (!Params.Robot.GetIsAutoReply()) return true;

            var qn = QN.FindExistingBySellerNick(item.SellerNick);
            if (qn == null)
            {
                Log.ErrorWithMaxCount(
                    "固定规则前置未发送：找不到客服运行实例。seller=" + item.SellerNick
                    + ", buyer=" + item.BuyerNick,
                    20);
                return true;
            }

            var question = (item.DisplayText ?? string.Empty).Trim();

            // Priority 1: a real buyer-authored/replyable message must get the configured first
            // inquiry greeting before any quiet-delay, context merge, Smart Reply or AI decision.
            string firstReply;
            var firstReserved = FirstInquiryFixedReplyService.TryResolve(
                item.SellerNick,
                item.BuyerNick,
                question,
                out firstReply);
            if (firstReserved)
            {
                var firstOk = await SendFixedAsync(
                    qn,
                    item,
                    firstReply,
                    "首条咨询固定回复");
                if (firstOk)
                {
                    FirstInquiryFixedReplyService.MarkDelivered(
                        item.SellerNick,
                        item.BuyerNick);
                }
                else
                {
                    FirstInquiryFixedReplyService.ReleaseReservation(
                        item.SellerNick,
                        item.BuyerNick,
                        qn.Rpa == null
                            ? "首条咨询固定回复发送失败"
                            : qn.Rpa.GetSendFailureReason());
                    // Do not let an AI/context reply overtake a failed mandatory greeting.
                    return false;
                }
            }

            // Priority 2: after the first greeting has had its chance, off-hours is evaluated on
            // the same individual message, still before any burst merge. While closed, normal AI
            // handling is suppressed. The fixed off-hours text is deduplicated for 30 minutes.
            string offHoursReply;
            if (TryResolveOffHours(out offHoursReply))
            {
                DateTime until;
                if (!OffHoursDeliveredUntil.TryGetValue(buyerKey, out until) || until <= DateTime.Now)
                {
                    var offHoursOk = await SendFixedAsync(
                        qn,
                        item,
                        offHoursReply,
                        "下班自动回复");
                    if (offHoursOk)
                    {
                        OffHoursDeliveredUntil[buyerKey] = DateTime.Now.AddMinutes(30);
                    }
                }
                return false;
            }

            // A first greeting does not consume the buyer's actual question during work hours.
            // Only now may the original message enter the normal merge/context/AI pipeline.
            return true;
        }

        private static async Task<bool> SendFixedAsync(
            QN qn,
            BuyerMessageBurstItem item,
            string answer,
            string source)
        {
            answer = BotOutboundMessageFormatter.EnsureAiMarker((answer ?? string.Empty).Trim());
            if (string.IsNullOrWhiteSpace(answer))
            {
                Log.ErrorWithMaxCount(source + "内容为空，未发送。", 20);
                return false;
            }

            var detectedAt = item.ReceivedAt == DateTime.MinValue ? DateTime.Now : item.ReceivedAt;
            var ctl = ResponseProgressTracker.BeginAnswer(
                item.SellerNick,
                item.BuyerNick,
                item.DisplayText,
                detectedAt);
            try
            {
                KnowledgeLearningService.RegisterAnswerSource(
                    item.SellerNick,
                    item.BuyerNick,
                    item.DisplayText,
                    answer,
                    source);
                ctl = ResponseProgressTracker.SetAnswerReady(
                    item.SellerNick,
                    item.BuyerNick,
                    item.DisplayText,
                    answer,
                    source,
                    detectedAt,
                    DateTime.Now);
                BotRuntimeStats.RecordDisplayedAnswer(true);
                Log.Info(
                    source + "在消息合并前命中，不等待合并窗口、不检查AI接口: seller="
                    + item.SellerNick + ", buyer=" + item.BuyerNick);

                // Fixed business replies are small and deterministic. Give the existing reliable
                // sender three attempts so a short UIA/CDP refresh does not lose the mandatory reply.
                var ok = await qn.SendTextWithRetryAsync(item.BuyerNick, answer, 3);
                if (ok)
                {
                    ReplyDeduplicationService.RememberDelivered(
                        item.SellerNick,
                        item.BuyerNick,
                        answer);
                }
                if (ctl != null)
                {
                    ctl.SetSendResult(
                        ok,
                        ok
                            ? "已发送（" + source + "，先于消息合并，未调用AI）"
                            : "发送失败：" + (qn.Rpa == null ? string.Empty : qn.Rpa.GetSendFailureReason()));
                }
                Log.Info(
                    source + "前置真实发送完成: seller=" + item.SellerNick
                    + ", buyer=" + item.BuyerNick + ", success=" + ok);
                return ok;
            }
            catch (Exception ex)
            {
                if (ctl != null) ctl.SetSendResult(false, "发送失败：" + ex.Message);
                Log.ErrorWithMaxCount(
                    source + "前置发送异常: seller=" + item.SellerNick
                    + ", buyer=" + item.BuyerNick + ", error=" + ex.Message,
                    20);
                return false;
            }
            finally
            {
                ResponseProgressTracker.Complete(item.SellerNick, item.BuyerNick);
            }
        }

        // Kept for older call sites/tests. New buyer traffic must use HandleBeforeMergeAsync.
        public static async Task<bool> TryHandleAsync(
            BuyerMessageBurst burst,
            BuyerMessageBurstLease lease)
        {
            if (burst == null || burst.Items == null || burst.Items.Count < 1) return false;
            var item = burst.Items[0];
            return !await HandleBeforeMergeAsync(item);
        }

        private static bool TryResolveOffHours(out string answer)
        {
            answer = string.Empty;
            var cfg = BotFeatureStore.GetAutoReplyRules();
            if (cfg == null || !cfg.EnableWorkHours) return false;

            TimeSpan start;
            TimeSpan end;
            if (!TryParseClock(cfg.WorkStartTime, out start)) start = new TimeSpan(9, 0, 0);
            if (!TryParseClock(cfg.WorkEndTime, out end)) end = new TimeSpan(18, 0, 0);
            if (IsInsideWorkHours(DateTime.Now.TimeOfDay, start, end)) return false;

            var template = string.IsNullOrWhiteSpace(cfg.OffHoursFixedText)
                ? DefaultOffHoursReply
                : cfg.OffHoursFixedText.Trim();
            var workHours = FormatClock(start) + "-" + FormatClock(end);
            answer = template.Replace("{工作时间}", workHours);
            return !string.IsNullOrWhiteSpace(answer);
        }

        private static bool TryParseClock(string value, out TimeSpan time)
        {
            time = TimeSpan.Zero;
            value = (value ?? string.Empty).Trim();
            DateTime parsed;
            if (!DateTime.TryParseExact(
                value,
                new[] { "H:mm", "HH:mm" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out parsed))
            {
                return false;
            }
            time = parsed.TimeOfDay;
            return true;
        }

        private static bool IsInsideWorkHours(TimeSpan now, TimeSpan start, TimeSpan end)
        {
            if (start == end) return true;
            if (start < end) return now >= start && now < end;
            return now >= start || now < end;
        }

        private static string FormatClock(TimeSpan value)
        {
            return ((int)value.TotalHours).ToString("00") + ":" + value.Minutes.ToString("00");
        }

        private static string Key(string seller, string buyer)
        {
            return (seller ?? string.Empty).Trim().ToLowerInvariant()
                + "#" + (buyer ?? string.Empty).Trim().ToLowerInvariant();
        }
    }
}
