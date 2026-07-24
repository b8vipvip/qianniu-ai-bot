using Bot.ChromeNs;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace Bot.Options
{
    public partial class CtlRobotOptions
    {
        private const string ControlPlaneScope = "ai-control-plane";
        private const string ControlPlaneUrlKey = "ControlPlaneUrl";
        private const string ControlPlaneTokenKey = "ControlPlaneClientToken";
        private const string ControlPlaneTextRouteKey = "ControlPlaneTextRoute";
        private const string ControlPlaneVisionRouteKey = "ControlPlaneVisionRoute";
        private const string ControlPlaneVisionEnabledKey = "ControlPlaneVisionEnabled";
        private const string ControlPlaneEmbeddingModelKey = "ControlPlaneEmbeddingModel";
        private const string ControlPlaneAdminUrlKey = "ControlPlaneAdminUrl";
        private bool _controlPlaneLoaded;

        private static string ReadControlPlaneValue(string key, string defaultValue)
        {
            return BotLib.Db.Sqlite.PersistentParams.GetParam2Key(key, ControlPlaneScope, defaultValue ?? string.Empty);
        }

        private static void SaveControlPlaneValue(string key, string value)
        {
            BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(key, ControlPlaneScope, value ?? string.Empty);
        }

        private void ControlPlane_Loaded(object sender, RoutedEventArgs e)
        {
            if (_controlPlaneLoaded) return;
            _controlPlaneLoaded = true;
            try
            {
                var existing = _endpoints == null ? null : _endpoints.FirstOrDefault(x => x != null && x.Type == "服务端控制面");
                var storedUrl = ReadControlPlaneValue(ControlPlaneUrlKey, string.Empty);
                if (string.IsNullOrWhiteSpace(storedUrl) && existing != null)
                {
                    storedUrl = RemoveV1Suffix(existing.BaseUrl);
                }
                txtControlPlaneUrl.Text = storedUrl;
                pwdControlPlaneToken.Password = ReadControlPlaneValue(ControlPlaneTokenKey, existing == null ? string.Empty : existing.ApiKey);
                txtControlPlaneTextRoute.Text = ReadControlPlaneValue(ControlPlaneTextRouteKey, existing == null || string.IsNullOrWhiteSpace(existing.TextModel) ? "text-default" : existing.TextModel);
                txtControlPlaneVisionRoute.Text = ReadControlPlaneValue(ControlPlaneVisionRouteKey, existing == null || string.IsNullOrWhiteSpace(existing.VisionModel) ? "vision-default" : existing.VisionModel);
                chkControlPlaneVision.IsChecked = string.Equals(
                    ReadControlPlaneValue(ControlPlaneVisionEnabledKey, existing != null && existing.SupportsVision ? "true" : "false"),
                    "true",
                    StringComparison.OrdinalIgnoreCase);
                txtControlPlaneEmbeddingModel.Text = ReadControlPlaneValue(ControlPlaneEmbeddingModelKey, string.Empty);
                txtControlPlaneAdminUrl.Text = ReadControlPlaneValue(ControlPlaneAdminUrlKey, storedUrl);
                txtControlPlaneStatus.Text = string.IsNullOrWhiteSpace(storedUrl)
                    ? "尚未配置统一 API 服务。请先部署 Ubuntu 服务端，在后台创建供应商和 Bot 客户端令牌。"
                    : "已载入服务端连接配置。点击【测试连接与文本调用】验证健康检查、令牌和网关调用。";
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                txtControlPlaneStatus.Text = "载入服务端配置失败：" + SafeControlPlaneText(ex.Message);
            }
        }

        private void btnSaveControlPlane_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string error;
                var endpoint = BuildControlPlaneEndpoint(out error);
                if (endpoint == null)
                {
                    txtControlPlaneStatus.Text = error;
                    return;
                }

                SaveControlPlaneConfiguration(endpoint);
                txtControlPlaneStatus.Text = "服务端连接已保存。Bot 后续只调用统一网关，上游供应商密钥不会保存在本机。"
                    + (string.IsNullOrWhiteSpace(txtControlPlaneEmbeddingModel.Text)
                        ? " 语义向量检索未启用，将使用本地混合检索。"
                        : " 已启用可选 Embedding 语义检索；接口异常时会自动降级。 ");
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                txtControlPlaneStatus.Text = "保存失败：" + SafeControlPlaneText(ex.Message);
            }
        }

        private async void btnTestControlPlane_Click(object sender, RoutedEventArgs e)
        {
            string error;
            var endpoint = BuildControlPlaneEndpoint(out error);
            if (endpoint == null)
            {
                txtControlPlaneStatus.Text = error;
                return;
            }

            btnTestControlPlane.IsEnabled = false;
            txtControlPlaneStatus.Text = "正在检查服务端健康状态、客户端令牌、路由配置和真实文本调用...";
            try
            {
                var result = await Task.Run(() => TestControlPlane(endpoint));
                txtControlPlaneStatus.Text = result;
                if (result.StartsWith("成功：", StringComparison.Ordinal))
                {
                    SaveControlPlaneConfiguration(endpoint);
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                txtControlPlaneStatus.Text = "测试异常：" + SafeControlPlaneText(ex.Message);
            }
            finally
            {
                btnTestControlPlane.IsEnabled = true;
            }
        }

        private void btnOpenControlPlaneAdmin_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var url = NormalizeServerUrl(txtControlPlaneAdminUrl.Text);
                if (string.IsNullOrWhiteSpace(url)) url = NormalizeServerUrl(txtControlPlaneUrl.Text);
                if (string.IsNullOrWhiteSpace(url))
                {
                    txtControlPlaneStatus.Text = "请先填写管理后台地址或服务端地址。";
                    return;
                }
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                txtControlPlaneStatus.Text = "打开后台失败：" + SafeControlPlaneText(ex.Message);
            }
        }

        private AiEndpointConfig BuildControlPlaneEndpoint(out string error)
        {
            error = string.Empty;
            var serverUrl = NormalizeServerUrl(txtControlPlaneUrl.Text);
            var token = (pwdControlPlaneToken.Password ?? string.Empty).Trim();
            var textRoute = (txtControlPlaneTextRoute.Text ?? string.Empty).Trim();
            var visionRoute = (txtControlPlaneVisionRoute.Text ?? string.Empty).Trim();
            Uri uri;
            if (string.IsNullOrWhiteSpace(serverUrl) || !Uri.TryCreate(serverUrl, UriKind.Absolute, out uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                error = "服务端地址无效。请填写完整的 http:// 或 https:// 地址，例如 https://ai-api.example.com。";
                return null;
            }
            if (string.IsNullOrWhiteSpace(token))
            {
                error = "客户端令牌为空。请在服务端后台的【Bot 客户端】页面创建令牌。";
                return null;
            }
            if (string.IsNullOrWhiteSpace(textRoute)) textRoute = "text-default";
            if (string.IsNullOrWhiteSpace(visionRoute)) visionRoute = "vision-default";

            return new AiEndpointConfig
            {
                Id = "control-plane-gateway",
                Name = "统一 API 服务",
                Type = "服务端控制面",
                BaseUrl = serverUrl + "/v1",
                ApiKey = token,
                Model = textRoute,
                TextModel = textRoute,
                VisionModel = visionRoute,
                SupportsVision = chkControlPlaneVision.IsChecked == true,
                MaxImageSizeMb = 10,
                VisionTimeoutSeconds = 120,
                SystemPrompt = txtSystemPrompt == null ? string.Empty : (txtSystemPrompt.Text ?? string.Empty),
                Enabled = true,
                Priority = 1,
                Weight = 1,
                TimeoutSeconds = 120,
                RetryCount = 0,
                LastStatus = "未测试"
            };
        }

        private void SaveControlPlaneConfiguration(AiEndpointConfig endpoint)
        {
            var serverUrl = RemoveV1Suffix(endpoint.BaseUrl);
            SaveControlPlaneValue(ControlPlaneUrlKey, serverUrl);
            SaveControlPlaneValue(ControlPlaneTokenKey, endpoint.ApiKey);
            SaveControlPlaneValue(ControlPlaneTextRouteKey, endpoint.TextModel);
            SaveControlPlaneValue(ControlPlaneVisionRouteKey, endpoint.VisionModel);
            SaveControlPlaneValue(ControlPlaneVisionEnabledKey, endpoint.SupportsVision ? "true" : "false");
            SaveControlPlaneValue(ControlPlaneEmbeddingModelKey, (txtControlPlaneEmbeddingModel.Text ?? string.Empty).Trim());
            SaveControlPlaneValue(ControlPlaneAdminUrlKey, NormalizeServerUrl(txtControlPlaneAdminUrl.Text));

            if (_endpoints != null)
            {
                _endpoints.Clear();
                _endpoints.Add(endpoint);
                gridEndpoints.Items.Refresh();
            }
            AiEndpointStore.SetStrategy("按优先级顺序调用");
            AiEndpointStore.SaveEndpoints(new[] { endpoint });
        }

        private static string TestControlPlane(AiEndpointConfig endpoint)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var serverUrl = RemoveV1Suffix(endpoint.BaseUrl);
            var details = new StringBuilder();
            using (var handler = new HttpClientHandler { UseProxy = true, Proxy = WebRequest.DefaultWebProxy })
            using (var http = new HttpClient(handler))
            {
                http.Timeout = TimeSpan.FromSeconds(30);
                var health = http.GetAsync(serverUrl + "/healthz").GetAwaiter().GetResult();
                var healthBody = health.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!health.IsSuccessStatusCode)
                {
                    return "失败：服务端健康检查返回 HTTP " + (int)health.StatusCode + "；" + SafeControlPlaneText(healthBody);
                }
                details.Append("健康检查通过");

                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
                var config = http.GetAsync(serverUrl + "/api/runtime/v1/config").GetAwaiter().GetResult();
                var configBody = config.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!config.IsSuccessStatusCode)
                {
                    return "失败：客户端令牌验证未通过。HTTP " + (int)config.StatusCode + "；" + SafeControlPlaneText(configBody);
                }
                try
                {
                    var json = JObject.Parse(configBody);
                    var providers = json["providers"] as JArray;
                    details.Append("；令牌有效；服务端启用供应商 ").Append(providers == null ? 0 : providers.Count).Append(" 个");
                }
                catch
                {
                    details.Append("；令牌有效");
                }
            }

            var test = AiEndpointTester.Test(endpoint);
            endpoint.LastLatencyMs = test.LatencyMs;
            endpoint.LastTestTime = DateTime.Now;
            endpoint.LastStatus = test.Success ? "可用：统一网关测试通过" : "失败：" + test.ShortStatus;
            if (!test.Success)
            {
                return "部分成功：" + details + "；但真实模型调用失败。" + test.DisplayText;
            }
            return "成功：" + details + "；统一网关真实文本调用通过。" + test.DisplayText;
        }

        private static string NormalizeServerUrl(string value)
        {
            value = (value ?? string.Empty).Trim().TrimEnd('/');
            if (value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) value = value.Substring(0, value.Length - 3).TrimEnd('/');
            return value;
        }

        private static string RemoveV1Suffix(string value)
        {
            return NormalizeServerUrl(value);
        }

        private static string SafeControlPlaneText(string value)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= 500 ? value : value.Substring(0, 500) + "...";
        }
    }
}
