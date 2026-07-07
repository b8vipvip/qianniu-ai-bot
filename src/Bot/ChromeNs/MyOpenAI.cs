using BotLib;
using BotLib.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenAI;
using OpenAI.Chat;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Interop;

namespace Bot.ChromeNs
{
    public class MyOpenAI
    {
        public static ChatClient ChatClient { get; set; }

        private static string systemPrompt;
        private static string lastApiKey;
        private static string lastBaseUrl;
        private static string lastModel;

        private static ConcurrentDictionary<string, List<ChatMessage>> buyerChatMessages;

        static MyOpenAI()
        {
            buyerChatMessages = new ConcurrentDictionary<string, List<ChatMessage>>();
            EnsureClient();
        }

        private static string DefaultSystemPrompt
        {
            get
            {
                return "你是淘宝店铺客服助手。请用简短、礼貌、自然的中文回复买家。不要编造库存、价格、物流、订单状态。遇到退款、投诉、差评、赔偿、订单隐私问题时，回复：亲，这个问题我帮您转人工客服确认一下。";
            }
        }

        private static bool EnsureClient()
        {
            try
            {
                var apikey = (Params.Robot.GetApiKey() ?? string.Empty).Trim();
                var baseUrl = (Params.Robot.GetBaseUrl() ?? string.Empty).Trim();
                var model = (Params.Robot.GetModelName() ?? string.Empty).Trim();
                var prompt = Params.Robot.GetSystemPrompt();
                systemPrompt = string.IsNullOrWhiteSpace(prompt) ? DefaultSystemPrompt : prompt;

                if (string.IsNullOrEmpty(apikey) || string.IsNullOrEmpty(model))
                {
                    ChatClient = null;
                    return false;
                }

                // 设置窗口里保存后，不重启程序也能重新加载新配置。
                if (ChatClient != null && apikey == lastApiKey && baseUrl == lastBaseUrl && model == lastModel)
                {
                    return true;
                }

                OpenAIClientOptions options = null;
                if (!string.IsNullOrEmpty(baseUrl))
                {
                    if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        && !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Error("AI BaseUrl 格式错误：" + baseUrl);
                        ChatClient = null;
                        return false;
                    }
                    options = new OpenAIClientOptions
                    {
                        Endpoint = new Uri(baseUrl.TrimEnd('/'))
                    };
                }

                ChatClient = new ChatClient(model: model, credential: new System.ClientModel.ApiKeyCredential(apikey), options: options);
                lastApiKey = apikey;
                lastBaseUrl = baseUrl;
                lastModel = model;
                Log.Info("AI客户端初始化成功, model=" + model + ", baseUrl=" + baseUrl);
                return true;
            }
            catch (Exception ex)
            {
                ChatClient = null;
                Log.Exception(ex);
                return false;
            }
        }

        private static string SafeError(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return "未知错误";
            msg = msg.Replace("\r", " ").Replace("\n", " ").Trim();
            if (msg.Length > 160) msg = msg.Substring(0, 160) + "...";
            return msg;
        }

        public static string GetAnswer(string seller, string buyer, string question)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(question))
                {
                    return "错误：买家消息为空，未调用AI。";
                }

                if (!EnsureClient() || ChatClient == null)
                {
                    return "错误：AI客户端未正确初始化，请检查 BaseUrl / ApiKey / Model。";
                }

                var key = string.Format("{0}#{1}", seller, buyer);
                var messages = buyerChatMessages.xTryGetValue(key);
                if (messages == null || messages.Count < 1)
                {
                    messages = new List<ChatMessage>() {
                        ChatMessage.CreateSystemMessage(systemPrompt),
                        ChatMessage.CreateUserMessage(question),
                    };
                }
                else
                {
                    messages.Add(ChatMessage.CreateUserMessage(question));
                }

                var completion = ChatClient.CompleteChat(messages);
                var completionContent = completion.GetRawResponse().Content.ToString();
                var answer = JObject.Parse(completionContent)["choices"][0]["message"]["content"].ToString();
                if (string.IsNullOrWhiteSpace(answer))
                {
                    return "错误：AI返回内容为空。";
                }

                messages.Add(ChatMessage.CreateAssistantMessage(answer));
                buyerChatMessages.AddOrUpdate(key, id => messages, (k, v) => messages);
                return answer;
            }
            catch (Exception ex)
            {
                // 原版本没有捕获 OpenAI.ClientResultException，导致买家一发消息程序直接闪退。
                Log.Exception(ex);
                return "错误：AI接口调用失败，未自动回复。" + SafeError(ex.Message);
            }
        }
    }
}