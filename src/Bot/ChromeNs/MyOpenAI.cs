using BotLib;
using BotLib.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenAI.Chat;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Bot.ChromeNs
{
    public class MyOpenAI
    {
        // 保留该属性，避免旧代码或 XAML 绑定引用失败；实际调用已改为原生 HTTP，兼容多数 OpenAI 中转站。
        public static ChatClient ChatClient { get; set; }

        private static string systemPrompt;
        private static string lastApiKey;
        private static string lastBaseUrl;
        private static string lastModel;

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
                return "你是淘宝店铺客服助手。只回复买家当前问题，语气像真人客服，简短自然。不要编造库存、价格、物流、订单状态。遇到退款、投诉、差评、赔偿、订单隐私问题时，回复：亲，这个问题我帮您转人工客服确认一下。";
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
                var apikey = (Params.Robot.GetApiKey() ?? string.Empty).Trim();
                var baseUrl = (Params.Robot.GetBaseUrl() ?? string.Empty).Trim();
                var model = (Params.Robot.GetModelName() ?? string.Empty).Trim();
                var prompt = Params.Robot.GetSystemPrompt();
                systemPrompt = BuildSystemPrompt(prompt);

                if (string.IsNullOrEmpty(apikey) || string.IsNullOrEmpty(model))
                {
                    return false;
                }

                if (!string.IsNullOrEmpty(baseUrl)
                    && !baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Error("AI BaseUrl 格式错误：" + baseUrl);
                    return false;
                }

                if (apikey != lastApiKey || baseUrl != lastBaseUrl || model != lastModel)
                {
                    lastApiKey = apikey;
                    lastBaseUrl = baseUrl;
                    lastModel = model;
                    Log.Info("AI配置加载成功, model=" + model + ", baseUrl=" + baseUrl);
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

        private static ApiCallResult CallChatCompletions(string baseUrl, string apiKey, string model, JArray messages)
        {
            var url = NormalizeBaseUrl(baseUrl);
            var payload = new JObject
            {
                ["model"] = model,
                ["messages"] = messages,
                ["temperature"] = 0.15,
                ["max_tokens"] = 120
            };

            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(35);
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "qianniu-bot/9.5.2");

                using (var content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json"))
                {
                    var response = http.PostAsync(url, content).GetAwaiter().GetResult();
                    var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (!response.IsSuccessStatusCode)
                    {
                        return new ApiCallResult
                        {
                            Success = false,
                            Error = "HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + "，接口返回：" + SafeError(body)
                        };
                    }

                    var answer = CleanAnswer(ExtractAnswer(body));
                    if (string.IsNullOrWhiteSpace(answer))
                    {
                        return new ApiCallResult
                        {
                            Success = false,
                            Error = "HTTP 200，但未解析到 choices[0].message.content。原始返回：" + SafeError(body)
                        };
                    }

                    return new ApiCallResult
                    {
                        Success = true,
                        Answer = answer,
                        Raw = body
                    };
                }
            }
        }

        public static string TestConnection(string baseUrl, string apiKey, string model, string prompt)
        {
            try
            {
                baseUrl = (baseUrl ?? string.Empty).Trim();
                apiKey = (apiKey ?? string.Empty).Trim();
                model = (model ?? string.Empty).Trim();
                prompt = BuildSystemPrompt(prompt);

                if (string.IsNullOrEmpty(apiKey)) return "失败：ApiKey 为空。";
                if (string.IsNullOrEmpty(model)) return "失败：Model 为空。";
                if (!string.IsNullOrEmpty(baseUrl)
                    && !baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return "失败：BaseUrl 必须以 http:// 或 https:// 开头。";
                }

                var messages = new JArray
                {
                    CreateMessage("system", prompt),
                    CreateMessage("user", "请只回复：连接测试成功")
                };
                var result = CallChatCompletions(baseUrl, apiKey, model, messages);
                if (!result.Success)
                {
                    return "失败：" + result.Error;
                }
                return "成功：API 连接正常。模型回复：" + SafeError(result.Answer);
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

                if (!EnsureConfig())
                {
                    return "错误：AI配置不完整，请检查 BaseUrl / ApiKey / Model。";
                }

                var baseUrl = (Params.Robot.GetBaseUrl() ?? string.Empty).Trim();
                var apiKey = (Params.Robot.GetApiKey() ?? string.Empty).Trim();
                var model = (Params.Robot.GetModelName() ?? string.Empty).Trim();

                var key = string.Format("{0}#{1}", seller, buyer);
                var history = buyerChatMessages.xTryGetValue(key);
                if (history == null || history.Count < 1)
                {
                    history = new List<JObject>
                    {
                        CreateMessage("system", systemPrompt)
                    };
                }

                history.Add(CreateMessage("user", question));

                // 避免长会话无限增长，保留 system + 最近 18 条。
                if (history.Count > 20)
                {
                    var trimmed = new List<JObject>();
                    trimmed.Add(history[0]);
                    trimmed.AddRange(history.Skip(Math.Max(1, history.Count - 18)));
                    history = trimmed;
                }

                var messages = new JArray(history.Select(m => (JObject)m.DeepClone()));
                var result = CallChatCompletions(baseUrl, apiKey, model, messages);
                if (!result.Success)
                {
                    Log.Error("AI接口调用失败：" + result.Error);
                    return "错误：AI接口调用失败，未自动回复。" + result.Error;
                }

                history.Add(CreateMessage("assistant", result.Answer));
                buyerChatMessages.AddOrUpdate(key, id => history, (k, v) => history);
                return result.Answer;
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
        }
    }
}