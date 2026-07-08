using BotLib;
using BotLib.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenAI.Chat;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Bot.ChromeNs
{
    public class MyOpenAI
    {
        public static ChatClient ChatClient { get; set; }

        private static string systemPrompt;
        private static string lastConfigFingerprint;
        private static ConcurrentDictionary<string, List<JObject>> buyerChatMessages;

        static MyOpenAI()
        {
            buyerChatMessages = new ConcurrentDictionary<string, List<JObject>>();
            EnsureConfig();
        }

        private static string DefaultSystemPrompt
        {
            get
            {
                return "你是淘宝店铺客服助手。只回复买家当前问题，语气像真人客服，简短自然。不要编造库存、价格、物流、订单状态。遇到退款、投诉、差评、赔偿、订单隐私等高风险问题时，建议转人工客服确认。";
            }
        }

        private static string ReplyStyleGuard
        {
            get
            {
                return "\n\n固定回复规则：每次最多1句话，优先20到35个字，最多不超过60个字；不要连续说多个解决方案；不要反复感谢、不要过度客套、不要像机器人话术；不要重复称呼“亲”；买家只发数字、表情、嗯/好/好的/是的/谢谢这类无明确问题时，不要主动扩展营销。";
            }
        }

        private static string BuildSystemPrompt(string prompt)
        {
            var basePrompt = string.IsNullOrWhiteSpace(prompt) ? DefaultSystemPrompt : prompt.Trim();
            if (basePrompt.Contains("固定回复规则"))
            {
                return basePrompt;
            }
            return basePrompt + ReplyStyleGuard;
        }

        private static bool EnsureConfig()
        {
            try
            {
                var endpoints = AiEndpointStore.GetEnabledEndpoints();
                if (endpoints.Count < 1)
                {
                    return false;
                }

                var primary = endpoints.First();
                systemPrompt = BuildSystemPrompt(string.IsNullOrWhiteSpace(primary.SystemPrompt) ? Params.Robot.GetSystemPrompt() : primary.SystemPrompt);
                var featureFingerprint = BotFeatureStore.GetMessagePolicy().Tone + ":" + BotFeatureStore.GetKnowledgeBase().Count + ":" + BotFeatureStore.GetAutoReplyRules().Enabled;
                var fingerprint = string.Join("|", endpoints.Select(e => string.Format("{0}:{1}:{2}:{3}", e.Name, e.BaseUrl, e.Model, e.Enabled))) + "|" + featureFingerprint;
                if (fingerprint != lastConfigFingerprint)
                {
                    lastConfigFingerprint = fingerprint;
                    Log.Info("AI配置加载成功, endpointCount=" + endpoints.Count + ", primary=" + primary.Name + ", model=" + primary.Model + ", baseUrl=" + primary.BaseUrl);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                return false;
            }
        }

        private static string NormalizeBaseUrl(string baseUrl)
        {
            baseUrl = (baseUrl ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(baseUrl))
            {
                baseUrl = "https://api.openai.com/v1";
            }
            baseUrl = baseUrl.TrimEnd('/');
            if (baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                return baseUrl;
            }
            return baseUrl + "/chat/completions";
        }

        private static string SafeError(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return "未知错误";
            msg = msg.Replace("\r", " ").Replace("\n", " ").Trim();
            if (msg.Length > 500) msg = msg.Substring(0, 500) + "...";
            return msg;
        }

        private static JObject CreateMessage(string role, string content)
        {
            return new JObject
            {
                ["role"] = role,
                ["content"] = content ?? string.Empty
            };
        }

        private static string ExtractAnswer(string responseBody)
        {
            var json = JObject.Parse(responseBody);
            var content = json["choices"]?[0]?["message"]?["content"];
            if (content == null)
            {
                return string.Empty;
            }
            if (content.Type == JTokenType.String)
            {
                return content.ToString();
            }
            return content.ToString(Formatting.None);
        }

        private static int GetIntToken(JToken token)
        {
            if (token == null) return 0;
            int value;
            return int.TryParse(token.ToString(), out value) ? value : 0;
        }

        private static int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return Math.Max(1, text.Length / 2);
        }

        private static void FillUsage(ApiCallResult result, string payloadText, string answerText, string responseBody)
        {
            try
            {
                var json = JObject.Parse(responseBody);
                result.InputTokens = GetIntToken(json["usage"]?["prompt_tokens"]);
                result.OutputTokens = GetIntToken(json["usage"]?["completion_tokens"]);
                result.TotalTokens = GetIntToken(json["usage"]?["total_tokens"]);
            }
            catch
            {
            }

            if (result.InputTokens <= 0) result.InputTokens = EstimateTokens(payloadText);
            if (result.OutputTokens <= 0) result.OutputTokens = EstimateTokens(answerText);
            if (result.TotalTokens <= 0) result.TotalTokens = result.InputTokens + result.OutputTokens;
        }

        private static string CleanAnswer(string answer)
        {
            answer = (answer ?? string.Empty).Trim();
            answer = answer.Replace("\r", " ").Replace("\n", " ");
            while (answer.Contains("  ")) answer = answer.Replace("  ", " ");
            if (answer.Length > 80)
            {
                answer = answer.Substring(0, 80).TrimEnd('，', '。', '；', '、', ' ') + "。";
            }
            return answer;
        }

        private static ApiCallResult CallChatCompletions(AiEndpointConfig endpoint, JArray messages)
        {
            var sw = Stopwatch.StartNew();
            var url = NormalizeBaseUrl(endpoint.BaseUrl);
            var payload = new JObject
            {
                ["model"] = endpoint.Model,
                ["messages"] = messages,
                ["temperature"] = 0.15,
                ["max_tokens"] = 120
            };
            var payloadText = payload.ToString(Formatting.None);

            try
            {
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(endpoint.TimeoutSeconds <= 0 ? 35 : endpoint.TimeoutSeconds);
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
                    http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
                    http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "qianniu-bot/9.5.2");

                    using (var content = new StringContent(payloadText, Encoding.UTF8, "application/json"))
                    {
                        var response = http.PostAsync(url, content).GetAwaiter().GetResult();
                        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        sw.Stop();
                        if (!response.IsSuccessStatusCode)
                        {
                            var failed = new ApiCallResult
                            {
                                Success = false,
                                LatencyMs = sw.ElapsedMilliseconds,
                                Error = "HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + "，接口返回：" + SafeError(body)
                            };
                            failed.InputTokens = EstimateTokens(payloadText);
                            failed.TotalTokens = failed.InputTokens;
                            return failed;
                        }

                        var answer = CleanAnswer(ExtractAnswer(body));
                        if (string.IsNullOrWhiteSpace(answer))
                        {
                            var empty = new ApiCallResult
                            {
                                Success = false,
                                LatencyMs = sw.ElapsedMilliseconds,
                                Error = "HTTP 200，但未解析到 choices[0].message.content。原始返回：" + SafeError(body)
                            };
                            empty.InputTokens = EstimateTokens(payloadText);
                            empty.TotalTokens = empty.InputTokens;
                            return empty;
                        }

                        var ok = new ApiCallResult
                        {
                            Success = true,
                            Answer = answer,
                            Raw = body,
                            LatencyMs = sw.ElapsedMilliseconds
                        };
                        FillUsage(ok, payloadText, answer, body);
                        return ok;
                    }
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new ApiCallResult
                {
                    Success = false,
                    LatencyMs = sw.ElapsedMilliseconds,
                    Error = SafeError(ex.Message),
                    InputTokens = EstimateTokens(payloadText),
                    TotalTokens = EstimateTokens(payloadText)
                };
            }
        }

        public static string TestConnection(string baseUrl, string apiKey, string model, string prompt)
        {
            var endpoint = new AiEndpointConfig
            {
                Name = "测试接口",
                Type = "OpenAI兼容",
                BaseUrl = (baseUrl ?? string.Empty).Trim(),
                ApiKey = (apiKey ?? string.Empty).Trim(),
                Model = (model ?? string.Empty).Trim(),
                SystemPrompt = prompt ?? string.Empty,
                Enabled = true,
                Priority = 1,
                TimeoutSeconds = 35
            };
            return TestConnection(endpoint);
        }

        public static string TestConnection(AiEndpointConfig endpoint)
        {
            try
            {
                if (endpoint == null) return "失败：接口配置为空。";
                endpoint.BaseUrl = (endpoint.BaseUrl ?? string.Empty).Trim();
                endpoint.ApiKey = (endpoint.ApiKey ?? string.Empty).Trim();
                endpoint.Model = (endpoint.Model ?? string.Empty).Trim();

                if (string.IsNullOrEmpty(endpoint.ApiKey)) return "失败：ApiKey 为空。";
                if (string.IsNullOrEmpty(endpoint.Model)) return "失败：Model 为空。";
                if (!string.IsNullOrEmpty(endpoint.BaseUrl)
                    && !endpoint.BaseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !endpoint.BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return "失败：BaseUrl 必须以 http:// 或 https:// 开头。";
                }

                var prompt = BuildSystemPrompt(endpoint.SystemPrompt);
                var messages = new JArray
                {
                    CreateMessage("system", prompt),
                    CreateMessage("user", "请只回复：连接测试成功")
                };
                var result = CallChatCompletions(endpoint, messages);
                endpoint.LastLatencyMs = result.LatencyMs;
                endpoint.LastTestTime = DateTime.Now;
                endpoint.LastStatus = result.Success ? "可用" : "失败：" + result.Error;
                if (!result.Success)
                {
                    return "失败：" + result.Error;
                }
                return "成功：API 连接正常。模型回复：" + SafeError(result.Answer) + "，耗时 " + result.LatencyMs + "ms";
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                return "失败：" + SafeError(ex.Message);
            }
        }

        public static string GetAnswer(string seller, string buyer, string question)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(question))
                {
                    return "错误：买家消息为空，未调用AI。";
                }

                string manualAnswer;
                string manualReason;
                if (BotFeatureStore.TryMatchManualRule(question, out manualAnswer, out manualReason))
                {
                    return "错误：命中人工确认规则，未自动回复。" + manualAnswer + " 原因：" + manualReason;
                }

                if (!EnsureConfig())
                {
                    return "错误：AI配置不完整，请检查 API接口 列表中的 BaseUrl / ApiKey / Model。";
                }

                var endpoints = AiEndpointStore.GetEnabledEndpoints();
                if (endpoints.Count < 1)
                {
                    return "错误：没有可用的AI接口，请在设置-API接口中启用至少一个接口。";
                }

                var dynamicSystemPrompt = systemPrompt + BotFeatureStore.BuildPromptAddon(question);
                var key = string.Format("{0}#{1}", seller, buyer);
                var history = buyerChatMessages.xTryGetValue(key);
                if (history == null || history.Count < 1)
                {
                    history = new List<JObject>
                    {
                        CreateMessage("system", dynamicSystemPrompt)
                    };
                }
                else
                {
                    history[0] = CreateMessage("system", dynamicSystemPrompt);
                }

                history.Add(CreateMessage("user", question));

                if (history.Count > 20)
                {
                    var trimmed = new List<JObject>();
                    trimmed.Add(history[0]);
                    trimmed.AddRange(history.Skip(Math.Max(1, history.Count - 18)));
                    history = trimmed;
                }

                var messages = new JArray(history.Select(m => (JObject)m.DeepClone()));
                var errors = new List<string>();
                foreach (var endpoint in endpoints)
                {
                    var result = CallChatCompletions(endpoint, messages);
                    BotRuntimeStats.RecordAiCall(endpoint, result.InputTokens, result.OutputTokens, result.Success, result.LatencyMs, result.Success ? "成功" : result.Error);
                    endpoint.LastLatencyMs = result.LatencyMs;
                    endpoint.LastStatus = result.Success ? "可用" : "失败：" + result.Error;
                    if (result.Success)
                    {
                        var finalAnswer = BotFeatureStore.ApplyOutputPolicy(result.Answer);
                        history.Add(CreateMessage("assistant", finalAnswer));
                        buyerChatMessages.AddOrUpdate(key, id => history, (k, v) => history);
                        return finalAnswer;
                    }

                    var err = endpoint.Name + "：" + result.Error;
                    errors.Add(err);
                    Log.Error("AI接口调用失败：" + err);
                }

                return "错误：AI接口调用失败，未自动回复。" + string.Join("；", errors);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                return "错误：AI接口调用失败，未自动回复。" + SafeError(ex.Message);
            }
        }

        private class ApiCallResult
        {
            public bool Success { get; set; }
            public string Answer { get; set; }
            public string Error { get; set; }
            public string Raw { get; set; }
            public int InputTokens { get; set; }
            public int OutputTokens { get; set; }
            public int TotalTokens { get; set; }
            public long LatencyMs { get; set; }
        }
    }
}