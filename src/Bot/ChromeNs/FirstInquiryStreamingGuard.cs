using BotLib;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Bot
{
    public partial class App
    {
        // Field initialization intentionally starts before the App constructor. The guard then
        // keeps re-wrapping the live burst handler so later Smart Reply / vision wrappers cannot
        // bypass the first-inquiry fixed reply.
        private readonly object _firstInquiryStreamingGuardBootstrap =
            ChromeNs.FirstInquiryStreamingGuard.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    /// <summary>
    /// Ensures a prepared first-inquiry fixed reply is consumed before any downstream
    /// Smart Reply, text streaming, or vision AI handler. BuyerStreamingReplyPipeline
    /// replaces BuyerMessageBurstCoordinator._handler at runtime, so relying only on
    /// QN.ProcessTextBurstAsync is insufficient: ordinary text can otherwise jump
    /// directly into AI while the fixed reply remains merely "reserved".
    /// </summary>
    internal static class FirstInquiryStreamingGuard
    {
        private sealed class GuardState
        {
            public Func<BuyerMessageBurstLease, Task> Wrapped;
        }

        private static readonly ConcurrentDictionary<int, GuardState> Guards =
            new ConcurrentDictionary<int, GuardState>();
        private static Timer _timer;
        private static int _initialized;
        private static int _patching;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                PatchExisting();
                _timer = new Timer(_ => PatchExisting(), null, 80, 250);
                Log.Info(
                    "首条咨询流式旁路保护已启动：固定回复优先于Smart Reply/视觉AI，"
                    + "只有固定回复真实送达后才提交30分钟去重窗口。");
            }
            return new object();
        }

        private static void PatchExisting()
        {
            if (Interlocked.Exchange(ref _patching, 1) != 0) return;
            try
            {
                QN[] qns;
                try
                {
                    qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray();
                }
                catch
                {
                    return;
                }

                var coordinatorField = typeof(QN).GetField(
                    "_buyerMessageBurstCoordinator",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var handlerField = typeof(BuyerMessageBurstCoordinator).GetField(
                    "_handler",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (coordinatorField == null || handlerField == null) return;

                foreach (var qn in qns)
                {
                    if (qn == null) continue;
                    var coordinator = coordinatorField.GetValue(qn) as BuyerMessageBurstCoordinator;
                    if (coordinator == null) continue;

                    var current = handlerField.GetValue(coordinator)
                        as Func<BuyerMessageBurstLease, Task>;
                    if (current == null) continue;

                    var key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(coordinator);
                    GuardState state;
                    if (Guards.TryGetValue(key, out state)
                        && state != null
                        && ReferenceEquals(current, state.Wrapped))
                    {
                        continue;
                    }

                    // Capture the handler that is live right now. If Smart Reply or another
                    // runtime pipeline replaces it later, the timer notices the changed delegate
                    // and puts this guard back on the outside of the chain.
                    var downstream = current;
                    Func<BuyerMessageBurstLease, Task> wrapped =
                        lease => HandleAsync(qn, downstream, lease);
                    handlerField.SetValue(coordinator, wrapped);
                    Guards[key] = new GuardState { Wrapped = wrapped };
                    Log.Info(
                        "已把首条咨询固定回复保护置于当前消息处理链最外层: seller="
                        + (qn.Seller == null ? string.Empty : qn.Seller.Nick));
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount(
                    "安装首条咨询流式旁路保护失败，将在下一轮自动重试：" + ex.Message,
                    10);
            }
            finally
            {
                Interlocked.Exchange(ref _patching, 0);
            }
        }

        private static async Task HandleAsync(
            QN qn,
            Func<BuyerMessageBurstLease, Task> downstream,
            BuyerMessageBurstLease lease)
        {
            var burst = lease == null ? null : lease.Burst;
            if (qn == null
                || burst == null
                || burst.Items.Count < 1
                || string.IsNullOrWhiteSpace(burst.SellerNick)
                || string.IsNullOrWhiteSpace(burst.BuyerNick)
                || string.IsNullOrWhiteSpace(burst.CombinedQuestion)
                || !burst.HasReplyableItem)
            {
                await downstream(lease);
                return;
            }

            string answer;
            // Do not gate this with HasPending. If an older superseded burst just released its
            // reservation, the newest burst must be able to reconstruct and acquire the same
            // first-inquiry reply instead of falling through to Smart Reply AI.
            if (!FirstInquiryFixedReplyService.TryResolve(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                out answer))
            {
                await downstream(lease);
                return;
            }

            // From this point the first-inquiry reply owns this burst. Never fall through to AI;
            // otherwise a transient AI failure can suppress a reply that requires no AI at all.
            var delivered = false;
            var releaseReason = "首条咨询固定回复未完成真实发送";
            var detectedAt = burst.Items.Min(x => x.ReceivedAt);
            var autoSend = Params.Robot.GetIsAutoReply();
            var ctl = ResponseProgressTracker.BeginAnswer(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                detectedAt);

            try
            {
                answer = BotOutboundMessageFormatter.EnsureAiMarker(answer);
                KnowledgeLearningService.RegisterAnswerSource(
                    burst.SellerNick,
                    burst.BuyerNick,
                    burst.CombinedQuestion,
                    answer,
                    "首条咨询固定回复");

                if (!lease.IsCurrent || !await lease.ConfirmStableAsync(180))
                {
                    releaseReason = "买家补充新消息，释放旧首条咨询预留给最新一轮";
                    if (ctl != null)
                    {
                        ctl.SetStatus(
                            "买家补充了新消息，首条固定回复转交最新一轮处理",
                            false);
                    }
                    return;
                }

                var answerReadyAt = DateTime.Now;
                ctl = ResponseProgressTracker.SetAnswerReady(
                    burst.SellerNick,
                    burst.BuyerNick,
                    burst.CombinedQuestion,
                    answer,
                    "首条咨询固定回复",
                    detectedAt,
                    answerReadyAt);
                BotRuntimeStats.RecordDisplayedAnswer(autoSend);
                Log.Info(
                    "首条咨询固定回复已在AI路由前命中: seller=" + burst.SellerNick
                    + ", buyer=" + burst.BuyerNick
                    + ", media=" + (burst.LatestVisionItem != null));

                if (!autoSend)
                {
                    releaseReason = "当前未开启自动回复，仅展示首条固定答案";
                    if (ctl != null) ctl.SetStatus("仅生成答案", true);
                    return;
                }

                if (!lease.IsCurrent)
                {
                    releaseReason = "发送前买家补充新消息，释放首条咨询预留";
                    if (ctl != null)
                    {
                        ctl.SetSendResult(
                            false,
                            "未发送：买家刚刚补充了新消息，固定回复将由最新一轮处理");
                    }
                    return;
                }

                var sendOk = await qn.SendTextWithRetryAsync(
                    burst.BuyerNick,
                    answer,
                    1);
                if (sendOk)
                {
                    delivered = true;
                    FirstInquiryFixedReplyService.MarkDelivered(
                        burst.SellerNick,
                        burst.BuyerNick);
                    ReplyDeduplicationService.RememberDelivered(
                        burst.SellerNick,
                        burst.BuyerNick,
                        answer);
                }
                else
                {
                    releaseReason = qn.Rpa == null
                        ? "首条咨询固定回复发送失败"
                        : qn.Rpa.GetSendFailureReason();
                }

                if (ctl != null)
                {
                    ctl.SetSendResult(
                        sendOk,
                        sendOk
                            ? "已发送（首条咨询固定回复，未调用AI）"
                            : "发送失败：" + releaseReason);
                }
                Log.Info(
                    "首条咨询固定回复真实发送完成: seller=" + burst.SellerNick
                    + ", buyer=" + burst.BuyerNick
                    + ", success=" + sendOk);
            }
            catch (Exception ex)
            {
                releaseReason = "首条咨询固定回复异常：" + ex.Message;
                if (ctl != null)
                {
                    ctl.SetSendResult(false, "发送失败：" + ex.Message);
                }
                Log.ErrorWithMaxCount(
                    "首条咨询固定回复保护执行失败: seller=" + burst.SellerNick
                    + ", buyer=" + burst.BuyerNick
                    + ", error=" + ex.Message,
                    20);
            }
            finally
            {
                if (!delivered)
                {
                    FirstInquiryFixedReplyService.ReleaseReservation(
                        burst.SellerNick,
                        burst.BuyerNick,
                        releaseReason);
                }
                ResponseProgressTracker.Complete(
                    burst.SellerNick,
                    burst.BuyerNick);
            }
        }
    }
}
