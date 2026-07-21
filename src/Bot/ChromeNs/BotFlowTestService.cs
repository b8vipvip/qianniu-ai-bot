using Bot.AssistWindow.Widget.Robot;
using Bot.ChatRecord;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class BotFlowTestCandidate
    {
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string Question { get; set; }
        public DateTime CapturedAt { get; set; }
        public string Source { get; set; }
    }

    internal sealed class BotFlowTestResult
    {
        public bool Success { get; set; }
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string Question { get; set; }
        public string Answer { get; set; }
        public string Detail { get; set; }
        public long AiLatencyMs { get; set; }
        public long SendLatencyMs { get; set; }
        public long TotalLatencyMs { get; set; }
    }

    internal static class BotFlowTestService
    {
        private static readonly object Sync = new object();
        private static readonly List<BotFlowTestCandidate> Recent = new List<BotFlowTestCandidate>();
        private static readonly Random Random = new Random();

        public static void RecordCandidate(
            string seller,
            string buyer,
            string question,
            DateTime capturedAt)
        {
            if (!IsEligible(question)) return;
            if (BotFeatureStore.EvaluateAutoReplyRule(question).Matched) return;
            var candidate = new BotFlowTestCandidate
            {
                Seller = (seller ?? string.Empty).Trim(),
                Buyer = (buyer ?? string.Empty).Trim(),
                Question = question.Trim(),
                CapturedAt = capturedAt == DateTime.MinValue ? DateTime.Now : capturedAt,
                Source = "本次运行已识别消息"
            };
            if (candidate.Seller.Length == 0 || candidate.Buyer.Length == 0) return;
            lock (Sync)
            {
                Recent.RemoveAll(x => x == null || x.CapturedAt < DateTime.Now.AddHours(-72));
                if (!Recent.Any(x => x.Seller == candidate.Seller
                    && x.Buyer == candidate.Buyer
                    && x.Question == candidate.Question))
                {
                    Recent.Add(candidate);
                }
                if (Recent.Count > 200) Recent.RemoveRange(0, Recent.Count - 200);
            }
        }

        public static async Task<BotFlowTestCandidate> PickRandomCandidateAsync()
        {
            lock (Sync)
            {
                var available = Recent
                    .Where(x => x != null
                        && x.CapturedAt >= DateTime.Now.AddHours(-72)
                        && QN.FindExistingBySellerNick(x.Seller) != null
                        && IsEligible(x.Question)
                        && !BotFeatureStore.EvaluateAutoReplyRule(x.Question).Matched)
                    .ToList();
                if (available.Count > 0)
                {
                    return available[Random.Next(available.Count)];
                }
            }
            return await LoadFromCurrentConversationAsync();
        }

        public static async Task<BotFlowTestResult> RunAsync(BotFlowTestCandidate candidate)
        {
            var total = Stopwatch.StartNew();
            if (candidate == null) return Failure("没有可用的测试问题。", total);
            if (!Params.Robot.CanUseRobotReal) return Failure("Bot 总开关已停用。", total, candidate);
            var qn = QN.FindExistingBySellerNick(candidate.Seller);
            if (qn == null || qn.CDP == null)
            {
                return Failure("目标客服账号当前没有可用的千牛消息连接，已阻止回退到其他店铺。", total, candidate);
            }
            if (qn.Seller == null
                || !string.Equals((qn.Seller.Nick ?? string.Empty).Trim(), (candidate.Seller ?? string.Empty).Trim(), StringComparison.Ordinal))
            {
                return Failure("目标客服账号校验失败，已阻止跨店铺执行测试。", total, candidate);
            }

            var detectedAt = DateTime.Now;
            var label = "[Bot真实流程测试] " + candidate.Question;
            var ctl = ResponseProgressTracker.ObserveQuestion(
                candidate.Seller,
                candidate.Buyer,
                label,
                detectedAt);
            if (ctl != null) ctl.SetProcessing("测试中：正在按真实规则生成答案...");

            var ai = Stopwatch.StartNew();
            var generated = await Task.Run(() => MyOpenAI.GetAnswer(
                candidate.Seller,
                candidate.Buyer,
                candidate.Question,
                true));
            ai.Stop();
            if (string.IsNullOrWhiteSpace(generated) || generated.StartsWith("错误：", StringComparison.Ordinal))
            {
                var error = string.IsNullOrWhiteSpace(generated) ? "AI 未返回答案。" : generated;
                ctl = ResponseProgressTracker.SetAnswerReady(
                    candidate.Seller,
                    candidate.Buyer,
                    label,
                    error,
                    "Bot真实流程测试",
                    detectedAt,
                    DateTime.Now);
                if (ctl != null) ctl.SetSendResult(false, "测试失败：答案生成阶段");
                ResponseProgressTracker.Complete(candidate.Seller, candidate.Buyer);
                return Failure(error, total, candidate, ai.ElapsedMilliseconds);
            }

            var outgoing = BotOutboundMessageFormatter.EnsureAiMarker(
                "【Bot测试】" + BotOutboundMessageFormatter.StripAiMarker(
                    BotFeatureStore.ApplyOutputPolicy(generated)));
            var answerReadyAt = DateTime.Now;
            ctl = ResponseProgressTracker.SetAnswerReady(
                candidate.Seller,
                candidate.Buyer,
                label,
                outgoing,
                "Bot真实流程测试",
                detectedAt,
                answerReadyAt);
            if (ctl != null) ctl.SetSendPending("测试中：正在定位买家、写入输入框并真实发送...");

            var send = Stopwatch.StartNew();
            var sent = await qn.SendTextWithRetryAsync(candidate.Buyer, outgoing, 1);
            send.Stop();
            total.Stop();
            var detail = sent
                ? "测试成功：真实消息已发送，请在千牛中手动撤回这条【Bot测试】消息。"
                : "测试失败：" + qn.Rpa.GetSendFailureReason();
            if (ctl != null) ctl.SetSendResult(sent, detail);
            ResponseProgressTracker.Complete(candidate.Seller, candidate.Buyer);
            Log.Info("Bot真实流程测试完成: seller=" + candidate.Seller
                + ", buyer=" + candidate.Buyer
                + ", success=" + sent
                + ", aiMs=" + ai.ElapsedMilliseconds
                + ", sendMs=" + send.ElapsedMilliseconds
                + ", totalMs=" + total.ElapsedMilliseconds
                + ", detail=" + detail);
            return new BotFlowTestResult
            {
                Success = sent,
                Seller = candidate.Seller,
                Buyer = candidate.Buyer,
                Question = candidate.Question,
                Answer = outgoing,
                Detail = detail,
                AiLatencyMs = ai.ElapsedMilliseconds,
                SendLatencyMs = send.ElapsedMilliseconds,
                TotalLatencyMs = total.ElapsedMilliseconds
            };
        }

        private static async Task<BotFlowTestCandidate> LoadFromCurrentConversationAsync()
        {
            try
            {
                var qn = QN.CurQN;
                if (qn == null || qn.CDP == null || qn.Seller == null) return null;
                var current = await qn.GetCurrentConversationID();
                var conversation = current == null ? null : current.Result;
                if (conversation == null
                    || string.IsNullOrWhiteSpace(conversation.Nick)
                    || string.IsNullOrWhiteSpace(conversation.Ccode))
                {
                    return null;
                }

                var history = await qn.CDP.Invoke<JObject>("im.singlemsg.GetRemoteHisMsg", new
                {
                    cid = new { ccode = conversation.Ccode, type = 1 },
                    count = 50,
                    gohistory = 1,
                    msgid = "-1",
                    msgtime = "-1"
                });
                var messages = history == null
                    ? null
                    : history["result"]?["msgs"]?.ToObject<List<QNChatMessage>>();
                var candidates = (messages ?? new List<QNChatMessage>())
                    .Where(x => x != null
                        && x.fromid != null
                        && x.toid != null
                        && string.Equals((x.fromid.nick ?? string.Empty).Trim(), conversation.Nick.Trim(), StringComparison.Ordinal)
                        && string.Equals((x.toid.nick ?? string.Empty).Trim(), qn.Seller.Nick.Trim(), StringComparison.Ordinal))
                    .Select(x => new
                    {
                        Message = x,
                        Text = IncomingMessageSafety.GetDisplayText(x, ExtractText(x))
                    })
                    .Where(x => IsEligible(x.Text) && !BotFeatureStore.EvaluateAutoReplyRule(x.Text).Matched)
                    .OrderByDescending(x => IncomingMessageSafety.GetSortValue(x.Message))
                    .Take(20)
                    .ToList();
                if (candidates.Count == 0) return null;
                int pickedIndex;
                lock (Sync)
                {
                    pickedIndex = Random.Next(candidates.Count);
                }
                var picked = candidates[pickedIndex];
                return new BotFlowTestCandidate
                {
                    Seller = qn.Seller.Nick,
                    Buyer = conversation.Nick,
                    Question = picked.Text,
                    CapturedAt = DateTime.Now,
                    Source = "当前会话最近消息"
                };
            }
            catch (Exception ex)
            {
                Log.Info("选择Bot真实流程测试问题失败：" + ex.Message);
                return null;
            }
        }

        private static string ExtractText(QNChatMessage message)
        {
            if (message == null) return string.Empty;
            var text = message.originalData == null ? string.Empty : (message.originalData.text ?? string.Empty);
            if (message.originalData != null && message.originalData.header != null)
            {
                text += message.originalData.header.summary ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(text)) text = message.summary ?? string.Empty;
            return text.Trim();
        }

        private static bool IsEligible(string question)
        {
            question = (question ?? string.Empty).Trim();
            if (question.Length < 1 || question.Length > 500) return false;
            if (question.StartsWith("[", StringComparison.Ordinal) && question.EndsWith("]", StringComparison.Ordinal)) return false;
            if (question.IndexOf("http://", StringComparison.OrdinalIgnoreCase) >= 0
                || question.IndexOf("https://", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }

        private static BotFlowTestResult Failure(
            string detail,
            Stopwatch total,
            BotFlowTestCandidate candidate = null,
            long aiMs = 0)
        {
            if (total.IsRunning) total.Stop();
            Log.Info("Bot真实流程测试未完成：" + detail);
            return new BotFlowTestResult
            {
                Success = false,
                Seller = candidate == null ? string.Empty : candidate.Seller,
                Buyer = candidate == null ? string.Empty : candidate.Buyer,
                Question = candidate == null ? string.Empty : candidate.Question,
                Detail = detail,
                AiLatencyMs = aiMs,
                TotalLatencyMs = total.ElapsedMilliseconds
            };
        }
    }
}
