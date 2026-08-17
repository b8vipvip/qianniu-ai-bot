using BotLib;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Handles deterministic replies before any AI configuration gate or upstream call.
    /// These replies must remain available when every AI provider is slow or unavailable.
    /// </summary>
    internal static class DeterministicAutoReplyService
    {
        private const string DefaultOffHoursReply =
            "亲，人工客服当前已下班，工作时间为每天 {工作时间}。您的问题已记录，请在上班时间联系或等待人工处理。";

        public static async Task<bool> TryHandleAsync(
            BuyerMessageBurst burst,
            BuyerMessageBurstLease lease)
        {
            if (burst == null
                || lease == null
                || burst.Items == null
                || burst.Items.Count < 1
                || !burst.HasReplyableItem
                || string.IsNullOrWhiteSpace(burst.SellerNick)
                || string.IsNullOrWhiteSpace(burst.BuyerNick)
                || string.IsNullOrWhiteSpace(burst.CombinedQuestion)
                || !Params.Robot.CanUseRobotReal)
            {
                return false;
            }

            string answer;
            string source;
            var firstInquiryReserved = false;
            var offHours = TryResolveOffHours(out answer);
            if (offHours)
            {
                source = "下班自动回复";

                // If this is also the first inquiry in the session, consume that reservation now.
                // A successful off-hours reply is already the first deterministic response, so the
                // later message must not receive a stale "在的，亲！" greeting as a second reply.
                string ignoredFirstReply;
                firstInquiryReserved = FirstInquiryFixedReplyService.TryResolve(
                    burst.SellerNick,
                    burst.BuyerNick,
                    burst.CombinedQuestion,
                    out ignoredFirstReply);
            }
            else
            {
                firstInquiryReserved = FirstInquiryFixedReplyService.TryResolve(
                    burst.SellerNick,
                    burst.BuyerNick,
                    burst.CombinedQuestion,
                    out answer);
                if (!firstInquiryReserved) return false;
                source = "首条咨询固定回复";
            }

            var qn = QN.FindExistingBySellerNick(burst.SellerNick);
            if (qn == null)
            {
                ReleaseFirstInquiryIfNeeded(
                    firstInquiryReserved,
                    burst,
                    source + "未找到当前店铺客服实例");
                Log.ErrorWithMaxCount(
                    source + "未发送：找不到客服运行实例。seller=" + burst.SellerNick
                    + ", buyer=" + burst.BuyerNick,
                    20);
                return true;
            }

            var detectedAt = burst.Items.Min(x => x == null ? DateTime.Now : x.ReceivedAt);
            var autoSend = Params.Robot.GetIsAutoReply();
            var ctl = ResponseProgressTracker.BeginAnswer(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                detectedAt);

            try
            {
                if (!lease.IsCurrent || !await lease.ConfirmStableAsync(160))
                {
                    ReleaseFirstInquiryIfNeeded(
                        firstInquiryReserved,
                        burst,
                        "买家补充新消息，固定回复交给最新一轮");
                    if (ctl != null)
                    {
                        ctl.SetStatus("买家补充了新消息，固定回复转交最新一轮处理", false);
                    }
                    return true;
                }

                answer = BotOutboundMessageFormatter.EnsureAiMarker((answer ?? string.Empty).Trim());
                if (string.IsNullOrWhiteSpace(answer))
                {
                    ReleaseFirstInquiryIfNeeded(firstInquiryReserved, burst, source + "内容为空");
                    if (ctl != null) ctl.SetSendResult(false, "未发送：固定回复内容为空");
                    return true;
                }

                KnowledgeLearningService.RegisterAnswerSource(
                    burst.SellerNick,
                    burst.BuyerNick,
                    burst.CombinedQuestion,
                    answer,
                    source);

                ctl = ResponseProgressTracker.SetAnswerReady(
                    burst.SellerNick,
                    burst.BuyerNick,
                    burst.CombinedQuestion,
                    answer,
                    source,
                    detectedAt,
                    DateTime.Now);
                BotRuntimeStats.RecordDisplayedAnswer(autoSend);

                Log.Info(source + "已在AI路由前命中，不检查AI接口: seller="
                    + burst.SellerNick + ", buyer=" + burst.BuyerNick);

                if (!autoSend)
                {
                    ReleaseFirstInquiryIfNeeded(
                        firstInquiryReserved,
                        burst,
                        "全局自动回复未开启，仅展示固定答案");
                    if (ctl != null) ctl.SetStatus("仅生成答案", true);
                    return true;
                }

                if (!lease.IsCurrent)
                {
                    ReleaseFirstInquiryIfNeeded(
                        firstInquiryReserved,
                        burst,
                        "发送前买家补充新消息");
                    if (ctl != null)
                    {
                        ctl.SetSendResult(false, "未发送：买家刚刚补充了新消息");
                    }
                    return true;
                }

                var sendOk = await qn.SendTextWithRetryAsync(burst.BuyerNick, answer, 1);
                if (sendOk)
                {
                    if (firstInquiryReserved)
                    {
                        FirstInquiryFixedReplyService.MarkDelivered(
                            burst.SellerNick,
                            burst.BuyerNick);
                    }
                    ReplyDeduplicationService.RememberDelivered(
                        burst.SellerNick,
                        burst.BuyerNick,
                        answer);
                }
                else
                {
                    ReleaseFirstInquiryIfNeeded(
                        firstInquiryReserved,
                        burst,
                        qn.Rpa == null ? source + "发送失败" : qn.Rpa.GetSendFailureReason());
                }

                if (ctl != null)
                {
                    ctl.SetSendResult(
                        sendOk,
                        sendOk
                            ? "已发送（" + source + "，未调用AI）"
                            : "发送失败：" + (qn.Rpa == null ? string.Empty : qn.Rpa.GetSendFailureReason()));
                }
                Log.Info(source + "真实发送完成: seller=" + burst.SellerNick
                    + ", buyer=" + burst.BuyerNick + ", success=" + sendOk);
                return true;
            }
            catch (Exception ex)
            {
                ReleaseFirstInquiryIfNeeded(
                    firstInquiryReserved,
                    burst,
                    source + "异常：" + ex.Message);
                if (ctl != null) ctl.SetSendResult(false, "发送失败：" + ex.Message);
                Log.ErrorWithMaxCount(
                    source + "处理失败: seller=" + burst.SellerNick
                    + ", buyer=" + burst.BuyerNick + ", error=" + ex.Message,
                    20);
                return true;
            }
            finally
            {
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick);
            }
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

        private static void ReleaseFirstInquiryIfNeeded(
            bool reserved,
            BuyerMessageBurst burst,
            string reason)
        {
            if (!reserved || burst == null) return;
            FirstInquiryFixedReplyService.ReleaseReservation(
                burst.SellerNick,
                burst.BuyerNick,
                reason);
        }
    }
}
