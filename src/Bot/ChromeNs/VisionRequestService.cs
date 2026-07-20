using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class VisionRequestResult
    {
        public bool Success { get; set; }
        public string Answer { get; set; }
        public string Error { get; set; }
        public string EndpointName { get; set; }
        public string VisionModel { get; set; }
        public long LatencyMs { get; set; }
    }

    internal sealed class VisionRequestService
    {
        private readonly VisionImageResolver _resolver = new VisionImageResolver();
        private const string UserPrompt = "你是淘宝/千牛客服助手。请理解买家当前发送的图片，并结合当前会话上下文生成客服回复。只描述图片中能够确认的内容。不要猜测模糊、遮挡或无法识别的信息。不要声称已经核实订单、账号、付款、充值或售后状态，除非上下文提供了明确数据。回复应简洁、自然，并直接解决买家的当前问题。";

        public async Task<VisionRequestResult> ExecuteAsync(VisionReplyTask task, CancellationToken cancellationToken)
        {
            var endpoints = AiEndpointStore.GetVisionEnabledEndpoints();
            if (endpoints.Count < 1) return Fail("未配置可用的视觉模型");

            var currentQuestion = string.IsNullOrWhiteSpace(task.CombinedQuestion)
                ? "[图片]"
                : task.CombinedQuestion.Trim();
            var timeline = ConversationContextStore.BuildTimelineText(task.SellerNick, task.BuyerNick, currentQuestion, 16);
            var prompt = UserPrompt;
            if (!string.Equals(currentQuestion, "[图片]", StringComparison.Ordinal))
            {
                prompt += "\n\n买家本轮连续发送的消息如下，换行代表先后顺序。图片和这些文字属于同一轮，请合并理解后只回复一次：\n" + currentQuestion;
            }
            if (!string.IsNullOrWhiteSpace(timeline))
            {
                prompt += "\n\n以下是同一客服与同一买家按时间排序的最近对话。买家发送的图片可能是在回答最近一条客服问题，请结合时间线理解；不得混入其他买家信息：\n" + timeline;
            }

            VisionRequestResult last = null;
            foreach (var endpoint in endpoints)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        cts.CancelAfter(TimeSpan.FromSeconds(endpoint.VisionTimeoutSeconds));
                        var image = await _resolver.ResolveAsync(task.Message, endpoint, cts.Token);
                        if (!image.Success)
                        {
                            last = Fail(image.Error);
                            last.EndpointName = endpoint.Name;
                            last.VisionModel = endpoint.VisionModel;
                            if (!IsRetryable(image.Error)) break;
                            continue;
                        }

                        var result = await CallVisionAsync(endpoint, image.ImageUrl, prompt, cts.Token);
                        result.LatencyMs = sw.ElapsedMilliseconds;
                        result.EndpointName = endpoint.Name;
                        result.VisionModel = endpoint.VisionModel;
                        if (result.Success)
                        {
                            if (ConversationContextStore.IsWithdrawnAnswer(task.SellerNick, task.BuyerNick, result.Answer))
                            {
                                return new VisionRequestResult
                                {
                                    Success = false,
                                    Error = "该回复已被客服撤回，已阻止再次发送",
                                    EndpointName = endpoint.Name,
                                    VisionModel = endpoint.VisionModel,
                                    LatencyMs = result.LatencyMs
                                };
                            }
                            KnowledgeLearningService.RegisterAnswerSource(task.SellerNick, task.BuyerNick, currentQuestion, result.Answer, "AI生成");
                            if (!task.DeferLearningUntilDelivered)
                            {
                                KnowledgeLearningService.QueueLearn(
                                    "买家本轮消息：" + currentQuestion + (string.IsNullOrWhiteSpace(timeline) ? string.Empty : "\n" + timeline),
                                    result.Answer,
                                    "视觉AI",
                                    task.SellerNick,
                                    task.BuyerNick);
                            }
                            return result;
                        }
                        last = result;
                        if (!IsRetryable(result.Error)) break;
                    }
                }
                catch (TaskCanceledException)
                {
                    last = Fail("视觉 API 超时");
                    last.EndpointName = endpoint.Name;
                    last.VisionModel = endpoint.VisionModel;
                }
                catch (Exception ex)
                {
                    last = Fail("视觉 API 异常：" + SafeText(ex.Message));
                    last.EndpointName = endpoint.Name;
                    last.VisionModel = endpoint.VisionModel;
                }
                finally
                {
                    sw.Stop();
                }
            }
            return last ?? Fail("所有视觉接口失败");
        }

        public static JObject BuildVisionPayload(AiEndpointConfig endpoint, string imageUrl, string prompt)
        {
            return new JObject
            {
                ["model"] = endpoint.VisionModel,
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "system", ["content"] = string.IsNullOrWhiteSpace(endpoint.SystemPrompt) ? "你是淘宝店铺客服助手。" : endpoint.SystemPrompt },
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = new JArray
                        {
                            new JObject { ["type"] = "text", ["text"] = prompt },
                            new JObject { ["type"] = "image_url", ["image_url"] = new JObject { ["url"] = imageUrl } }
                        }
                    }
                },
                ["temperature"] = 0.1,
                ["max_tokens"] = 180,
                ["stream"] = false
            };
        }

        private async Task<VisionRequestResult> CallVisionAsync(AiEndpointConfig endpoint, string imageUrl, string prompt, CancellationToken token)
        {
            var payload = BuildVisionPayload(endpoint, imageUrl, prompt).ToString(Formatting.None);
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(endpoint.VisionTimeoutSeconds);
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
                http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
                using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
                {
                    var response = await http.PostAsync(BuildChatUrl(endpoint.BaseUrl), content, token);
                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode) return Fail("HTTP " + (int)response.StatusCode + " " + Classify(response.StatusCode) + "：" + SafeText(body));
                    var answer = ExtractAnswer(body).Trim();
                    if (string.IsNullOrWhiteSpace(answer)) return Fail("返回内容为空");
                    return new VisionRequestResult { Success = true, Answer = answer };
                }
            }
        }

        private static string BuildChatUrl(string baseUrl)
        {
            baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            return baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? baseUrl : baseUrl + "/chat/completions";
        }

        private static string ExtractAnswer(string body)
        {
            var json = JObject.Parse(body);
            return json["choices"]?[0]?["message"]?["content"] == null ? string.Empty : json["choices"][0]["message"]["content"].ToString();
        }

        private static bool IsRetryable(string error)
        {
            error = error ?? string.Empty;
            return error.Contains("429") || error.Contains("500") || error.Contains("502") || error.Contains("503") || error.Contains("504") || error.Contains("超时") || error.Contains("下载失败");
        }

        private static string Classify(HttpStatusCode code)
        {
            var value = (int)code;
            if (value == 401) return "鉴权失败";
            if (value == 404) return "模型或路径不存在";
            if (value == 400) return "请求格式错误或模型不支持图片";
            if (value == 413) return "请求过大";
            if (value == 429) return "限流";
            if (value >= 500 && value <= 504) return "上游服务异常";
            return "视觉请求失败";
        }

        private static string SafeText(string text)
        {
            text = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            return text.Length > 300 ? text.Substring(0, 300) + "..." : text;
        }

        private static VisionRequestResult Fail(string error)
        {
            return new VisionRequestResult { Success = false, Error = error };
        }
    }
}
