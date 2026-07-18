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
                        if (!image.Success) return Fail(image.Error);
                        var result = await CallVisionAsync(endpoint, image.ImageUrl, cts.Token);
                        result.LatencyMs = sw.ElapsedMilliseconds;
                        result.EndpointName = endpoint.Name;
                        result.VisionModel = endpoint.VisionModel;
                        if (result.Success) return result;
                        last = result;
                        if (!IsRetryable(result.Error)) break;
                    }
                }
                catch (TaskCanceledException) { last = Fail("视觉 API 超时"); last.EndpointName = endpoint.Name; }
                catch (Exception ex) { last = Fail("视觉 API 异常：" + SafeText(ex.Message)); last.EndpointName = endpoint.Name; }
                finally { sw.Stop(); }
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
                    new JObject { ["role"] = "user", ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = prompt }, new JObject { ["type"] = "image_url", ["image_url"] = new JObject { ["url"] = imageUrl } } } }
                },
                ["temperature"] = 0.1,
                ["max_tokens"] = 180,
                ["stream"] = false
            };
        }

        private async Task<VisionRequestResult> CallVisionAsync(AiEndpointConfig endpoint, string imageUrl, CancellationToken token)
        {
            var payload = BuildVisionPayload(endpoint, imageUrl, UserPrompt).ToString(Formatting.None);
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

        private static string BuildChatUrl(string baseUrl) { baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/'); return baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? baseUrl : baseUrl + "/chat/completions"; }
        private static string ExtractAnswer(string body) { var json = JObject.Parse(body); return json["choices"]?[0]?["message"]?["content"] == null ? string.Empty : json["choices"][0]["message"]["content"].ToString(); }
        private static bool IsRetryable(string e) { return (e ?? string.Empty).Contains("429") || (e ?? string.Empty).Contains("500") || (e ?? string.Empty).Contains("502") || (e ?? string.Empty).Contains("503") || (e ?? string.Empty).Contains("504") || (e ?? string.Empty).Contains("超时"); }
        private static string Classify(HttpStatusCode c) { var code=(int)c; if(code==401)return "鉴权失败"; if(code==404)return "模型或路径不存在"; if(code==400)return "请求格式错误或模型不支持图片"; if(code==413)return "请求过大"; if(code==429)return "限流"; if(code>=500&&code<=504)return "上游服务异常"; return "视觉请求失败"; }
        private static string SafeText(string t) { t=(t??string.Empty).Replace("\r"," ").Replace("\n"," "); return t.Length>300?t.Substring(0,300)+"...":t; }
        private static VisionRequestResult Fail(string error) { return new VisionRequestResult { Success = false, Error = error }; }
    }
}
