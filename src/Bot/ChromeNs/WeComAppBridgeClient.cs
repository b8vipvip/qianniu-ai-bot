using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal static class WeComAppBridgeClient
    {
        private const string ControlPlaneScope = "ai-control-plane";
        private const string ControlPlaneUrlKey = "ControlPlaneUrl";
        private const string ControlPlaneTokenKey = "ControlPlaneClientToken";
        private static int _started;

        public static void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            Task.Run(PollLoopAsync);
            Log.Info("企业微信应用人工回复桥接轮询已启动。");
        }

        public static bool IsConfigured()
        {
            string url;
            string token;
            ReadConnection(out url, out token);
            return !string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(token);
        }

        public static async Task<string> SendNotificationAsync(
            string seller,
            string buyer,
            string question,
            AutoReplyRuleDecision decision,
            bool test)
        {
            string serverUrl;
            string token;
            ReadConnection(out serverUrl, out token);
            if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(token))
            {
                return "未配置统一API服务";
            }

            var payload = new JObject
            {
                ["seller"] = seller ?? string.Empty,
                ["buyer"] = buyer ?? string.Empty,
                ["question"] = question ?? string.Empty,
                ["reason"] = decision == null ? "测试企业微信应用消息双向链路" : (decision.Reason ?? string.Empty),
                ["is_off_hours"] = decision != null && decision.IsOffHours,
                ["test"] = test
            };

            try
            {
                var result = await SendJsonAsync(
                    HttpMethod.Post,
                    serverUrl + "/api/runtime/v1/handoff/notify",
                    token,
                    payload);
                if (!result.Success) return result.Error;
                var json = JObject.Parse(result.Body);
                var ticket = Convert.ToString(json["ticket_id"]);
                return string.IsNullOrWhiteSpace(ticket)
                    ? "成功"
                    : "成功，工单=" + ticket;
            }
            catch (Exception ex)
            {
                return "失败：" + Safe(ex.Message);
            }
        }

        private static async Task PollLoopAsync()
        {
            while (true)
            {
                try
                {
                    if (IsConfigured())
                    {
                        await PollOnceAsync();
                        await Task.Delay(3000);
                    }
                    else
                    {
                        await Task.Delay(15000);
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("企业微信应用人工回复轮询异常：" + Safe(ex.Message));
                    await Task.Delay(5000);
                }
            }
        }

        private static async Task PollOnceAsync()
        {
            string serverUrl;
            string token;
            ReadConnection(out serverUrl, out token);
            if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(token)) return;

            var next = await SendJsonAsync(
                HttpMethod.Get,
                serverUrl + "/api/runtime/v1/handoff/replies/next",
                token,
                null);
            if (next.StatusCode == HttpStatusCode.NoContent) return;
            if (!next.Success)
            {
                if (next.StatusCode != HttpStatusCode.NotFound)
                {
                    Log.Info("领取企业微信人工回复失败：" + next.Error);
                }
                return;
            }

            var json = JObject.Parse(next.Body);
            var commandId = Convert.ToInt32(json["id"]);
            var ticketId = Convert.ToString(json["ticket_id"]);
            var seller = Convert.ToString(json["seller"]);
            var buyer = Convert.ToString(json["buyer"]);
            var question = Convert.ToString(json["question"]);
            var reply = Convert.ToString(json["reply_text"]);
            var claimToken = Convert.ToString(json["claim_token"]);

            if (commandId <= 0 || string.IsNullOrWhiteSpace(claimToken)) return;
            var qn = QN.FindExistingBySellerNick(seller);
            if (qn == null || qn.CDP == null)
            {
                Log.Info("企业微信人工回复等待对应千牛客服上线。ticket=" + ticketId + ", seller=" + seller);
                return;
            }

            var success = false;
            var error = string.Empty;
            try
            {
                if (string.IsNullOrWhiteSpace(buyer) || string.IsNullOrWhiteSpace(reply))
                {
                    throw new Exception("人工回复任务缺少买家或回复内容");
                }

                KnowledgeLearningService.AllowNextManualSend(seller, buyer, reply);
                success = await qn.SendTextWithRetryAsync(buyer, reply, 1);
                if (!success)
                {
                    error = "无法确认目标买家会话或千牛发送未完成";
                }
                else
                {
                    ReplyDeduplicationService.RememberDelivered(seller, buyer, reply);
                    KnowledgeLearningService.RegisterAnswerSource(
                        seller,
                        buyer,
                        question,
                        reply,
                        "人工回复-企业微信应用");
                    KnowledgeLearningService.QueueLearn(
                        question,
                        reply,
                        "人工回复-企业微信应用",
                        seller,
                        buyer);
                    Log.Info("企业微信人工回复已发送并进入知识学习队列。ticket="
                        + ticketId + ", seller=" + seller + ", buyer=" + buyer);
                }
            }
            catch (Exception ex)
            {
                error = Safe(ex.Message);
                Log.Info("企业微信人工回复发送失败。ticket=" + ticketId + ", error=" + error);
            }

            await CompleteAsync(serverUrl, token, commandId, claimToken, success, error);
        }

        private static async Task CompleteAsync(
            string serverUrl,
            string token,
            int commandId,
            string claimToken,
            bool success,
            string error)
        {
            var payload = new JObject
            {
                ["claim_token"] = claimToken,
                ["success"] = success,
                ["error"] = error ?? string.Empty
            };
            var result = await SendJsonAsync(
                HttpMethod.Post,
                serverUrl + "/api/runtime/v1/handoff/replies/" + commandId + "/complete",
                token,
                payload);
            if (!result.Success)
            {
                Log.Info("回报企业微信人工回复结果失败：" + result.Error);
            }
        }

        private static async Task<HttpResult> SendJsonAsync(
            HttpMethod method,
            string url,
            string token,
            JObject payload)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var handler = new HttpClientHandler
            {
                UseProxy = true,
                Proxy = WebRequest.DefaultWebProxy
            })
            using (var http = new HttpClient(handler))
            using (var request = new HttpRequestMessage(method, url))
            {
                http.Timeout = TimeSpan.FromSeconds(25);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot-wecom-bridge/1.0");
                if (payload != null)
                {
                    request.Content = new StringContent(
                        payload.ToString(Formatting.None),
                        Encoding.UTF8,
                        "application/json");
                }
                using (var response = await http.SendAsync(request))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    return new HttpResult
                    {
                        Success = response.IsSuccessStatusCode,
                        StatusCode = response.StatusCode,
                        Body = body,
                        Error = response.IsSuccessStatusCode
                            ? string.Empty
                            : "HTTP " + (int)response.StatusCode + " " + Safe(body)
                    };
                }
            }
        }

        private static void ReadConnection(out string serverUrl, out string token)
        {
            serverUrl = BotLib.Db.Sqlite.PersistentParams.GetParam2Key(
                ControlPlaneUrlKey,
                ControlPlaneScope,
                string.Empty);
            token = BotLib.Db.Sqlite.PersistentParams.GetParam2Key(
                ControlPlaneTokenKey,
                ControlPlaneScope,
                string.Empty);
            serverUrl = (serverUrl ?? string.Empty).Trim().TrimEnd('/');
            if (serverUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                serverUrl = serverUrl.Substring(0, serverUrl.Length - 3).TrimEnd('/');
            }
            token = (token ?? string.Empty).Trim();
        }

        private static string Safe(string value)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= 300 ? value : value.Substring(0, 300) + "...";
        }

        private sealed class HttpResult
        {
            public bool Success;
            public HttpStatusCode StatusCode;
            public string Body;
            public string Error;
        }
    }
}
