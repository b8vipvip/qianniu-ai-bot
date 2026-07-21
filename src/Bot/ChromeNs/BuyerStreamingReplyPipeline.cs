using Bot.Automation.ChatDeskNs;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal static class BuyerStreamingReplyPipeline
    {
        private static readonly ConcurrentDictionary<int, bool> PatchedCoordinators =
            new ConcurrentDictionary<int, bool>();
        private static Timer _patchTimer;
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            PatchExisting();
            _patchTimer = new Timer(_ => PatchExisting(), null, 100, 300);
            Log.Info("买家文本回复流式管线已启动：新消息将取消旧AI流，完整答案生成后才发送。" );
        }

        private static void PatchExisting()
        {
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
                    var key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(coordinator);
                    if (PatchedCoordinators.ContainsKey(key)) continue;

                    var original = handlerField.GetValue(coordinator) as Func<BuyerMessageBurstLease, Task>;
                    if (original == null) continue;
                    Func<BuyerMessageBurstLease, Task> wrapped = lease => HandleAsync(qn, original, lease);
                    handlerField.SetValue(coordinator, wrapped);
                    PatchedCoordinators[key] = true;
                    Log.Info("已为客服实例启用可取消流式回复管线: seller="
                        + (qn.Seller == null ? string.Empty : qn.Seller.Nick));
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("安装流式回复管线失败，将继续使用原回复流程：" + ex.Message, 10);
            }
        }

        private static async Task HandleAsync(
            QN qn,
            Func<BuyerMessageBurstLease, Task> original,
            BuyerMessageBurstLease lease)
        {
            var burst = lease == null ? null : lease.Burst;
            if (qn == null
                || burst == null
                || burst.Items.Count < 1
                || !burst.HasReplyableItem
                || burst.LatestVisionItem != null)
            {
                await original(lease);
                return;
            }

            await ProcessTextBurstStreamingAsync(qn, lease);
        }

        private static async Task ProcessTextBurstStreamingAsync(QN qn, BuyerMessageBurstLease lease)
        {
            var burst = lease.Burst;
            var detectedAt = burst.Items.Min(x => x.ReceivedAt);
            var autoSend = Params.Robot.GetIsAutoReply();
            var conversationCtl = ResponseProgressTracker.BeginAnswer(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                detectedAt);
            var aiStartedAt = DateTime.Now;
            var generationCts = new CancellationTokenSource();
            var monitorCts = new CancellationTokenSource();
            var monitor = MonitorLeaseAsync(lease, generationCts, monitorCts.Token);

            string answer;
            try
            {
                answer = await StreamingBuyerAnswerService.GetAnswerAsync(
                    burst.SellerNick,
                    burst.BuyerNick,
                    burst.CombinedQuestion,
                    generationCts.Token,
                    partial =>
                    {
                        if (conversationCtl == null || !lease.IsCurrent) return;
                        var preview = CompactPreview(partial, 110);
                        if (!string.IsNullOrWhiteSpace(preview))
                        {
                            conversationCtl.SetProcessing("正在流式生成答案：" + preview);
                        }
                    });
            }
            catch (OperationCanceledException)
            {
                if (conversationCtl != null)
                {
                    conversationCtl.SetProcessing(lease.IsCurrent
                        ? "AI请求已取消"
                        : "买家补充了新消息，旧AI流已取消");
                    conversationCtl.SetStatus(lease.IsCurrent
                        ? "AI请求已取消"
                        : "已转入买家最新一轮消息，旧答案不会发送", false);
                }
                Log.Info("文本AI流已取消: buyer=" + burst.BuyerNick
                    + ", superseded=" + (!lease.IsCurrent));
                return;
            }
            catch (Exception ex)
            {
                answer = "错误：流式AI调用失败：" + ex.Message;
                Log.Info("流式AI调用失败: buyer=" + burst.BuyerNick + ", error=" + ex.Message);
            }
            finally
            {
                monitorCts.Cancel();
                monitorCts.Dispose();
                generationCts.Dispose();
            }

            if (!lease.IsCurrent)
            {
                if (conversationCtl != null)
                {
                    conversationCtl.SetProcessing("买家补充了新消息，旧AI结果已丢弃");
                    conversationCtl.SetStatus("已转入买家最新一轮消息，旧答案不会发送", false);
                }
                return;
            }

            var deduplication = ReplyDeduplicationService.EnsureDistinct(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                answer);
            answer = deduplication.Answer;

            if (!await lease.ConfirmStableAsync(180))
            {
                if (conversationCtl != null)
                {
                    conversationCtl.SetStatus("发送前收到买家新消息，旧答案已取消", false);
                }
                return;
            }

            var answerReadyAt = DateTime.Now;
            var answerSource = KnowledgeLearningService.ResolveAnswerSource(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                answer);
            conversationCtl = ResponseProgressTracker.SetAnswerReady(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                answer,
                answerSource,
                detectedAt,
                answerReadyAt);
            BotRuntimeStats.RecordDisplayedAnswer(autoSend);
            Log.Info("流式文本答案已生成: buyer=" + burst.BuyerNick
                + ", aiMs=" + Math.Max(0, (long)(answerReadyAt - aiStartedAt).TotalMilliseconds)
                + ", totalToAnswerMs=" + Math.Max(0, (long)(answerReadyAt - detectedAt).TotalMilliseconds));

            if (!autoSend)
            {
                if (conversationCtl != null) conversationCtl.SetStatus("仅生成答案", true);
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick);
                return;
            }

            if (string.IsNullOrWhiteSpace(answer) || answer.StartsWith("错误：", StringComparison.Ordinal))
            {
                if (conversationCtl != null) conversationCtl.SetSendResult(false, "未发送：AI错误");
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick);
                return;
            }

            if (!lease.IsCurrent)
            {
                if (conversationCtl != null)
                {
                    conversationCtl.SetSendResult(false, "未发送：买家刚刚补充了新消息，正在重新组织回复");
                }
                return;
            }

            var sendOk = await qn.SendTextWithRetryAsync(burst.BuyerNick, answer, 1);
            if (sendOk)
            {
                ReplyDeduplicationService.RememberDelivered(burst.SellerNick, burst.BuyerNick, answer);
                if (string.Equals(answerSource, "AI生成", StringComparison.Ordinal))
                {
                    KnowledgeLearningService.QueueLearn(
                        burst.CombinedQuestion,
                        answer,
                        "AI生成",
                        burst.SellerNick,
                        burst.BuyerNick);
                }
            }
            if (conversationCtl != null)
            {
                conversationCtl.SetSendResult(
                    sendOk,
                    sendOk
                        ? "已发送（流式生成，合并本轮买家消息）"
                        : "发送失败：" + (qn.Rpa == null ? string.Empty : qn.Rpa.GetSendFailureReason()));
            }
            Log.Info("流式文本真实流程完成: buyer=" + burst.BuyerNick + ", success=" + sendOk
                + ", totalMs=" + Math.Max(0, (long)(DateTime.Now - detectedAt).TotalMilliseconds));
            ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick);
        }

        private static async Task MonitorLeaseAsync(
            BuyerMessageBurstLease lease,
            CancellationTokenSource generationCts,
            CancellationToken stopToken)
        {
            try
            {
                while (!stopToken.IsCancellationRequested && !generationCts.IsCancellationRequested)
                {
                    if (!lease.IsCurrent)
                    {
                        generationCts.Cancel();
                        return;
                    }
                    await Task.Delay(80, stopToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static string CompactPreview(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : "…" + value.Substring(value.Length - max);
        }
    }

    internal static class StreamingBuyerAnswerService
    {
        private sealed class StreamResult
        {
            public bool Success;
            public string Answer;
            public string Error;
            public long LatencyMs;
            public int InputTokens;
            public int OutputTokens;
        }

        private static readonly HttpClient Http = CreateHttpClient();
        private static readonly MethodInfo BuildSystemPromptMethod = typeof(MyOpenAI).GetMethod(
            "BuildSystemPrompt",
            BindingFlags.Static | BindingFlags.NonPublic);

        public static async Task<string> GetAnswerAsync(
            string seller,
            string buyer,
            string question,
            CancellationToken token,
            Action<string> partial)
        {
            token.ThrowIfCancellationRequested();

            string presetReply;
            if (ConversationContextStore.TryTakeProductLinkReply(seller, buyer, question, out presetReply))
            {
                if (ConversationContextStore.IsWithdrawnAnswer(seller, buyer, presetReply))
                {
                    return "错误：该预设回复已被客服撤回，未再次发送。";
                }
                KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, presetReply, "本地");
                return presetReply;
            }

            if (string.IsNullOrWhiteSpace(question)) return "错误：买家消息为空，未调用AI。";

            KnowledgeBaseEntry contextualKnowledge = null;
            ContextualKnowledgeDecision contextualDecision = null;
            double contextualScore = 0;
            KnowledgeBaseEntry localKnowledge;
            double localScore;
            if (KnowledgeLearningService.TryFindLocalAnswer(seller, buyer, question, out localKnowledge, out localScore))
            {
                var localAnswer = BotFeatureStore.ApplyOutputPolicy(localKnowledge.Answer);
                var contextDecision = KnowledgeContextualReplyService.Analyze(seller, buyer, question, localKnowledge);
                if (!contextDecision.IsFollowUp)
                {
                    KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, localAnswer, "本地");
                    return localAnswer;
                }
                contextualKnowledge = localKnowledge;
                contextualDecision = contextDecision;
                contextualScore = localScore;
            }

            var manualDecision = BotFeatureStore.EvaluateAutoReplyRule(question);
            if (manualDecision.Matched)
            {
                HandoffNotificationService.QueueNotify(seller, buyer, question, manualDecision);
                if (!manualDecision.AllowAutoReply)
                {
                    return "错误：命中人工确认规则，未自动回复。" + manualDecision.ReplyText + " 原因：" + manualDecision.Reason;
                }
                if (!manualDecision.UseAiReply)
                {
                    var fixedReply = BotFeatureStore.ApplyOutputPolicy(manualDecision.ReplyText);
                    KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, fixedReply, "转人工回复");
                    return fixedReply;
                }

                var handoffMessages = new JArray
                {
                    Message("system", "你是电商店铺的下班转人工助手。当前人工客服已下班。只能礼貌告知人工客服不在线、工作时间，以及问题已记录或建议买家在上班时间联系；不得回答退款、投诉、赔偿、隐私、订单核验等具体高风险结论。回复一句到两句，禁止编造。"),
                    Message("user", "人工客服工作时间：" + manualDecision.WorkHoursText
                        + "\n触发原因：" + manualDecision.Reason
                        + "\n买家问题：" + question)
                };
                var handoff = await StreamMessagesAsync(handoffMessages, token, partial);
                if (!string.IsNullOrWhiteSpace(handoff))
                {
                    handoff = BotFeatureStore.ApplyOutputPolicy(handoff);
                    KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, handoff, "转人工回复");
                    return handoff;
                }
                return BotFeatureStore.ApplyOutputPolicy(manualDecision.ReplyText);
            }

            var endpoints = AiEndpointStore.GetEnabledEndpoints();
            if (endpoints == null || endpoints.Count < 1)
            {
                if (contextualKnowledge != null)
                {
                    var offline = KnowledgeContextualReplyService.BuildOfflineFallback(contextualDecision, contextualKnowledge);
                    if (!string.IsNullOrWhiteSpace(offline))
                    {
                        offline = BotFeatureStore.ApplyOutputPolicy(offline);
                        KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, offline, "本地知识库上下文");
                        return offline;
                    }
                }
                return "错误：没有可用的AI接口，请在设置-API接口中启用至少一个接口。";
            }

            var primary = endpoints.First();
            var configuredPrompt = string.IsNullOrWhiteSpace(primary.SystemPrompt)
                ? Params.Robot.GetSystemPrompt()
                : primary.SystemPrompt;
            var dynamicSystemPrompt = BuildSystemPrompt(configuredPrompt);
            var turns = ConversationContextStore.GetRecentTurns(seller, buyer, question, 18);
            var contextForKnowledge = new StringBuilder(question);
            foreach (var turn in turns)
            {
                if (contextForKnowledge.Length > 3500) break;
                contextForKnowledge.Append(' ').Append(turn.Text);
            }
            dynamicSystemPrompt += BotFeatureStore.BuildPromptAddon(contextForKnowledge.ToString());
            if (contextualKnowledge != null)
            {
                dynamicSystemPrompt += KnowledgeContextualReplyService.BuildPromptAddon(contextualDecision, contextualKnowledge);
            }

            var messages = new JArray { Message("system", dynamicSystemPrompt) };
            foreach (var turn in turns)
            {
                var time = turn.Timestamp == DateTime.MinValue
                    ? "时间未知"
                    : turn.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
                var speaker = turn.Role == "assistant" ? "客服" : "买家";
                messages.Add(Message(turn.Role, "[" + time + " " + speaker + "] " + turn.Text));
            }
            messages.Add(Message("user", "[当前消息 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " 买家] " + question));

            var answer = await StreamMessagesAsync(messages, token, partial);
            if (string.IsNullOrWhiteSpace(answer))
            {
                return "错误：所有AI接口均未返回有效答案。";
            }
            answer = BotFeatureStore.ApplyOutputPolicy(answer);
            if (ConversationContextStore.IsWithdrawnAnswer(seller, buyer, answer))
            {
                return "错误：该回复已被客服撤回，已阻止再次发送。";
            }

            var source = contextualKnowledge == null ? "AI生成" : "本地知识库上下文";
            KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, answer, source);
            if (contextualKnowledge != null)
            {
                Log.Info("流式上下文知识回复生成成功。buyer=" + buyer
                    + ", knowledgeId=" + contextualKnowledge.Id
                    + ", score=" + contextualScore.ToString("0.00"));
            }
            return answer;
        }

        private static async Task<string> StreamMessagesAsync(
            JArray messages,
            CancellationToken token,
            Action<string> partial)
        {
            var endpoints = AiEndpointStore.GetEnabledEndpoints();
            var errors = new List<string>();
            foreach (var endpoint in endpoints)
            {
                token.ThrowIfCancellationRequested();
                var result = await StreamOneAsync(endpoint, messages, token, partial);
                BotRuntimeStats.RecordAiCall(
                    endpoint,
                    result.InputTokens,
                    result.OutputTokens,
                    result.Success,
                    result.LatencyMs,
                    result.Success ? "流式成功" : result.Error);
                endpoint.LastLatencyMs = result.LatencyMs;
                endpoint.LastStatus = result.Success ? "可用" : "失败：" + result.Error;
                if (result.Success && !string.IsNullOrWhiteSpace(result.Answer)) return result.Answer.Trim();
                errors.Add((endpoint.Name ?? "接口") + "：" + result.Error);
            }

            // 某些中转站不支持 stream=true，最后使用现有结构化非流式调用兜底；该调用仍接收取消令牌。
            try
            {
                var fallback = await Task.Run(
                    () => MyOpenAI.CallStructuredChat(messages, 220, 0.15, 30, token),
                    token);
                if (fallback != null && fallback.Success && !string.IsNullOrWhiteSpace(fallback.Answer))
                {
                    return fallback.Answer.Trim();
                }
                if (fallback != null && !string.IsNullOrWhiteSpace(fallback.Error)) errors.Add(fallback.Error);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }

            Log.Error("流式AI接口全部失败：" + string.Join("；", errors));
            return string.Empty;
        }

        private static async Task<StreamResult> StreamOneAsync(
            AiEndpointConfig endpoint,
            JArray messages,
            CancellationToken token,
            Action<string> partial)
        {
            var sw = Stopwatch.StartNew();
            var payload = new JObject
            {
                ["model"] = endpoint.TextModel,
                ["messages"] = messages,
                ["temperature"] = 0.15,
                ["max_tokens"] = 220,
                ["stream"] = true
            };
            var payloadText = payload.ToString(Newtonsoft.Json.Formatting.None);
            var timeoutSeconds = endpoint.TimeoutSeconds <= 0
                ? 30
                : Math.Max(8, Math.Min(35, endpoint.TimeoutSeconds));

            try
            {
                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token))
                using (var request = new HttpRequestMessage(HttpMethod.Post, NormalizeUrl(endpoint.BaseUrl)))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
                    request.Headers.TryAddWithoutValidation("Accept", "text/event-stream, application/json");
                    request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot/9.5.2");
                    request.Content = new StringContent(payloadText, Encoding.UTF8, "application/json");

                    using (var response = await Http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        linked.Token))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            var failedBody = await response.Content.ReadAsStringAsync();
                            return Fail(sw, payloadText, "HTTP " + (int)response.StatusCode + " " + Short(failedBody, 300));
                        }

                        var mediaType = response.Content.Headers.ContentType == null
                            ? string.Empty
                            : (response.Content.Headers.ContentType.MediaType ?? string.Empty);
                        if (mediaType.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            var body = await response.Content.ReadAsStringAsync();
                            var answer = ExtractNormalAnswer(body);
                            if (string.IsNullOrWhiteSpace(answer))
                            {
                                return Fail(sw, payloadText, "接口未返回可解析的流式或普通答案");
                            }
                            return Ok(sw, payloadText, answer);
                        }

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            var buffer = new StringBuilder();
                            Task<string> pendingRead = null;
                            var lastPreviewAt = DateTime.MinValue;
                            var lastPreviewLength = 0;

                            while (true)
                            {
                                linked.Token.ThrowIfCancellationRequested();
                                if (pendingRead == null) pendingRead = reader.ReadLineAsync();
                                var completed = await Task.WhenAny(
                                    pendingRead,
                                    Task.Delay(120, linked.Token));
                                if (completed != pendingRead)
                                {
                                    linked.Token.ThrowIfCancellationRequested();
                                    continue;
                                }

                                var line = await pendingRead;
                                pendingRead = null;
                                if (line == null) break;
                                line = line.Trim();
                                if (line.Length == 0 || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                                var data = line.Substring(5).Trim();
                                if (data == "[DONE]") break;

                                string delta;
                                if (!TryExtractDelta(data, out delta) || string.IsNullOrEmpty(delta)) continue;
                                buffer.Append(delta);
                                if (partial != null
                                    && (buffer.Length - lastPreviewLength >= 8
                                        || DateTime.Now - lastPreviewAt >= TimeSpan.FromMilliseconds(180)))
                                {
                                    lastPreviewAt = DateTime.Now;
                                    lastPreviewLength = buffer.Length;
                                    partial(buffer.ToString());
                                }
                            }

                            var answer = buffer.ToString().Trim();
                            if (string.IsNullOrWhiteSpace(answer))
                            {
                                return Fail(sw, payloadText, "流已结束但没有收到文本内容");
                            }
                            if (partial != null) partial(answer);
                            return Ok(sw, payloadText, answer);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested) throw;
                return Fail(sw, payloadText, "流式请求超时（" + timeoutSeconds + "秒）");
            }
            catch (Exception ex)
            {
                return Fail(sw, payloadText, ex.Message);
            }
        }

        private static bool TryExtractDelta(string data, out string delta)
        {
            delta = string.Empty;
            try
            {
                var json = JObject.Parse(data);
                var token = json["choices"]?[0]?["delta"]?["content"]
                    ?? json["choices"]?[0]?["text"]
                    ?? json["choices"]?[0]?["message"]?["content"];
                if (token == null) return false;
                delta = token.Type == JTokenType.String
                    ? token.ToString()
                    : token.ToString(Newtonsoft.Json.Formatting.None);
                return !string.IsNullOrEmpty(delta);
            }
            catch
            {
                return false;
            }
        }

        private static string ExtractNormalAnswer(string body)
        {
            try
            {
                var json = JObject.Parse(body ?? string.Empty);
                var token = json["choices"]?[0]?["message"]?["content"]
                    ?? json["choices"]?[0]?["text"];
                return token == null ? string.Empty : token.ToString().Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string BuildSystemPrompt(string configured)
        {
            try
            {
                if (BuildSystemPromptMethod != null)
                {
                    var value = BuildSystemPromptMethod.Invoke(null, new object[] { configured });
                    if (value != null) return Convert.ToString(value);
                }
            }
            catch
            {
            }
            return string.IsNullOrWhiteSpace(configured)
                ? "你是淘宝店铺客服助手。只回复买家当前问题，语气简短自然，不得编造价格、库存、物流、订单状态。"
                : configured.Trim();
        }

        private static JObject Message(string role, string content)
        {
            return new JObject
            {
                ["role"] = role,
                ["content"] = content ?? string.Empty
            };
        }

        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClient();
            http.Timeout = Timeout.InfiniteTimeSpan;
            return http;
        }

        private static string NormalizeUrl(string baseUrl)
        {
            baseUrl = (baseUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = "https://api.openai.com/v1";
            baseUrl = baseUrl.TrimEnd('/');
            return baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
                ? baseUrl
                : baseUrl + "/chat/completions";
        }

        private static StreamResult Ok(Stopwatch sw, string payload, string answer)
        {
            sw.Stop();
            return new StreamResult
            {
                Success = true,
                Answer = answer,
                LatencyMs = sw.ElapsedMilliseconds,
                InputTokens = EstimateTokens(payload),
                OutputTokens = EstimateTokens(answer)
            };
        }

        private static StreamResult Fail(Stopwatch sw, string payload, string error)
        {
            sw.Stop();
            return new StreamResult
            {
                Success = false,
                Error = Short(error, 500),
                LatencyMs = sw.ElapsedMilliseconds,
                InputTokens = EstimateTokens(payload)
            };
        }

        private static int EstimateTokens(string value)
        {
            return string.IsNullOrEmpty(value) ? 0 : Math.Max(1, value.Length / 2);
        }

        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
