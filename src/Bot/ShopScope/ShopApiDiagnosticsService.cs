using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ShopScope
{
    internal sealed class ShopApiDiagnosticReport
    {
        public bool Success { get; set; }
        public string Summary { get; set; }
        public string Details { get; set; }
    }

    internal static class ShopApiDiagnosticsService
    {
        private const int RequestTimeoutSeconds = 70;

        public static async Task<ShopApiDiagnosticReport> TestConnectionAsync(
            ShopContext shop,
            string serverUrl,
            string token,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ValidateInputs(shop, serverUrl, token);
            var baseUrl = ShopControlPlaneConnectionStore.NormalizeUrl(serverUrl);
            var watch = Stopwatch.StartNew();
            try
            {
                using (var http = CreateClient(shop, token))
                using (var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    baseUrl + "/api/runtime/v1/config"))
                using (var response = await http.SendAsync(request, cancellationToken))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    watch.Stop();
                    if (!response.IsSuccessStatusCode)
                    {
                        return Failure(
                            "API连接失败",
                            "阶段1/3 网络连接：已到达服务端\n"
                            + "阶段2/3 Token/ShopKey 鉴权：失败\n"
                            + "HTTP：" + (int)response.StatusCode + " " + response.ReasonPhrase + "\n"
                            + "服务端：" + baseUrl + "\n"
                            + "ShopKey：" + shop.ShopKey + "\n"
                            + "耗时：" + watch.ElapsedMilliseconds + " ms\n"
                            + "响应：" + Safe(body, 1000));
                    }

                    var root = ParseObject(body);
                    var clientName = Convert.ToString(root["client"] ?? string.Empty);
                    var textRoute = Convert.ToString(root["text_route"] ?? string.Empty);
                    var providers = root["providers"] as JArray;
                    var providerCount = providers == null ? 0 : providers.Count;
                    return Success(
                        "API连接正常",
                        "阶段1/3 网络连接：通过\n"
                        + "阶段2/3 Token/ShopKey 鉴权：通过\n"
                        + "阶段3/3 Control Plane 配置读取：通过\n"
                        + "服务端：" + baseUrl + "\n"
                        + "客户端：" + (string.IsNullOrWhiteSpace(clientName) ? "已鉴权" : clientName) + "\n"
                        + "ShopKey：" + shop.ShopKey + "\n"
                        + "文本路由：" + (string.IsNullOrWhiteSpace(textRoute) ? "text-default" : textRoute) + "\n"
                        + "启用供应商：" + providerCount + "\n"
                        + "总耗时：" + watch.ElapsedMilliseconds + " ms");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                watch.Stop();
                return Failure(
                    "API连接失败",
                    "阶段1/3 网络连接：失败\n"
                    + "服务端：" + baseUrl + "\n"
                    + "ShopKey：" + shop.ShopKey + "\n"
                    + "耗时：" + watch.ElapsedMilliseconds + " ms\n"
                    + "错误：" + Safe(ex.Message, 1200));
            }
        }

        public static async Task<ShopApiDiagnosticReport> TestAnswerChainAsync(
            ShopContext shop,
            string serverUrl,
            string token,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ValidateInputs(shop, serverUrl, token);
            var baseUrl = ShopControlPlaneConnectionStore.NormalizeUrl(serverUrl);
            var overall = Stopwatch.StartNew();

            var connection = await TestConnectionAsync(
                shop,
                baseUrl,
                token,
                cancellationToken);
            if (!connection.Success)
            {
                return Failure(
                    "AI回答链路失败：API连接未通过",
                    connection.Details);
            }

            var prompt =
                "这是千牛Bot设置中的诊断请求，不是买家消息，不要调用工具或外部接口。"
                + "请用一句简短中文回答，并明确说明AI回答链路正常。";
            var payload = new JObject
            {
                ["model"] = "text-default",
                ["messages"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = prompt
                    }
                },
                ["max_tokens"] = 96,
                ["temperature"] = 0.1,
                ["timeout_seconds"] = 45
            };

            var aiWatch = Stopwatch.StartNew();
            try
            {
                using (var http = CreateClient(shop, token))
                using (var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    baseUrl + "/v1/chat/completions"))
                {
                    request.Content = new StringContent(
                        payload.ToString(Newtonsoft.Json.Formatting.None),
                        Encoding.UTF8,
                        "application/json");
                    using (var response = await http.SendAsync(request, cancellationToken))
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        aiWatch.Stop();
                        overall.Stop();
                        if (!response.IsSuccessStatusCode)
                        {
                            return Failure(
                                "AI回答链路失败",
                                "阶段1/5 API网络：通过\n"
                                + "阶段2/5 Token/ShopKey：通过\n"
                                + "阶段3/5 Control Plane 路由：已进入\n"
                                + "阶段4/5 上游供应商/模型调用：失败\n"
                                + "HTTP：" + (int)response.StatusCode + " " + response.ReasonPhrase + "\n"
                                + "AI阶段耗时：" + aiWatch.ElapsedMilliseconds + " ms\n"
                                + "响应：" + Safe(body, 1800));
                        }

                        var root = ParseObject(body);
                        var choices = root["choices"] as JArray;
                        var first = choices != null && choices.Count > 0 ? choices[0] as JObject : null;
                        var message = first == null ? null : first["message"] as JObject;
                        var answer = Convert.ToString(message == null ? null : message["content"]);
                        if (string.IsNullOrWhiteSpace(answer))
                        {
                            return Failure(
                                "AI回答链路失败：未解析到AI文本",
                                "服务端已返回 HTTP 2xx，但 choices[0].message.content 为空。\n"
                                + "响应：" + Safe(body, 1800));
                        }

                        var routing = root["qianniu_routing"] as JObject;
                        var provider = Convert.ToString(routing == null ? null : routing["provider"]);
                        var protocol = Convert.ToString(routing == null ? null : routing["protocol"]);
                        var model = Convert.ToString(root["model"] ?? string.Empty);
                        var latency = Convert.ToString(routing == null ? null : routing["latency_ms"]);
                        var fallback = Convert.ToString(routing == null ? null : routing["fallback_attempts"]);

                        return Success(
                            "AI回答链路正常",
                            "阶段1/5 API网络：通过\n"
                            + "阶段2/5 Token/ShopKey：通过\n"
                            + "阶段3/5 Control Plane 路由：通过\n"
                            + "阶段4/5 上游供应商/模型/协议：通过\n"
                            + "阶段5/5 AI回复文本解析：通过\n"
                            + "供应商：" + Empty(provider, "未返回") + "\n"
                            + "模型：" + Empty(model, "未返回") + "\n"
                            + "协议：" + Empty(protocol, "未返回") + "\n"
                            + "上游耗时：" + Empty(latency, "-") + " ms\n"
                            + "回退次数：" + Empty(fallback, "0") + "\n"
                            + "链路总耗时：" + overall.ElapsedMilliseconds + " ms\n\n"
                            + "AI实际回复：\n" + answer.Trim());
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                aiWatch.Stop();
                overall.Stop();
                return Failure(
                    "AI回答链路失败",
                    "API与鉴权已通过，但调用AI路由时发生异常。\n"
                    + "AI阶段耗时：" + aiWatch.ElapsedMilliseconds + " ms\n"
                    + "错误：" + Safe(ex.Message, 1600));
            }
        }

        private static HttpClient CreateClient(ShopContext shop, string token)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var handler = new HttpClientHandler
            {
                UseProxy = true,
                Proxy = WebRequest.DefaultWebProxy
            };
            var http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds)
            };
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token.Trim());
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            http.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "qianniu-bot-shop-diagnostics/1.0");
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-Shop-Key", shop.ShopKey);
            return http;
        }

        private static void ValidateInputs(ShopContext shop, string serverUrl, string token)
        {
            if (shop == null) throw new ArgumentNullException(nameof(shop));
            if (string.IsNullOrWhiteSpace(serverUrl))
                throw new InvalidOperationException("程序没有配置 Bot 服务端地址。");
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("本店尚未保存 Bot 客户端令牌，请先保存令牌后再测试。");
        }

        private static JObject ParseObject(string value)
        {
            try
            {
                return JObject.Parse(value ?? string.Empty);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "服务端返回的不是有效 JSON：" + Safe(ex.Message, 300));
            }
        }

        private static ShopApiDiagnosticReport Success(string summary, string details)
        {
            return new ShopApiDiagnosticReport
            {
                Success = true,
                Summary = summary,
                Details = details
            };
        }

        private static ShopApiDiagnosticReport Failure(string summary, string details)
        {
            return new ShopApiDiagnosticReport
            {
                Success = false,
                Summary = summary,
                Details = details
            };
        }

        private static string Empty(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
