using Bot.ChromeNs;
using BotLib;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Text;
using System.Windows;

namespace Bot.Options
{
    public partial class CtlRobotOptions
    {
        private JObject BuildControlPlaneExportJson()
        {
            return new JObject
            {
                ["kind"] = "qianniu-control-plane-config",
                ["version"] = 1,
                ["exportedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["serverUrl"] = NormalizeServerUrl(txtControlPlaneUrl.Text),
                ["clientToken"] = (pwdControlPlaneToken.Password ?? string.Empty).Trim(),
                ["textRoute"] = (txtControlPlaneTextRoute.Text ?? string.Empty).Trim(),
                ["visionRoute"] = (txtControlPlaneVisionRoute.Text ?? string.Empty).Trim(),
                ["visionEnabled"] = chkControlPlaneVision.IsChecked == true,
                ["adminUrl"] = NormalizeServerUrl(txtControlPlaneAdminUrl.Text)
            };
        }

        private void btnExportControlPlane_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string error;
                if (BuildControlPlaneEndpoint(out error) == null)
                {
                    txtControlPlaneStatus.Text = error;
                    return;
                }
                if (MessageBox.Show(
                    "导出的 JSON 会包含 Bot 客户端令牌明文。请只保存到可信位置，不要发送给无关人员。是否继续？",
                    "导出统一 API 配置",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    return;
                }
                var dlg = new SaveFileDialog
                {
                    Title = "导出统一 API 服务配置",
                    FileName = "qianniu-control-plane-config.json",
                    Filter = "JSON配置文件 (*.json)|*.json|所有文件 (*.*)|*.*"
                };
                if (dlg.ShowDialog() != true) return;
                File.WriteAllText(
                    dlg.FileName,
                    BuildControlPlaneExportJson().ToString(Formatting.Indented),
                    new UTF8Encoding(true));
                txtControlPlaneStatus.Text = "统一 API 配置已导出：" + dlg.FileName
                    + "\n注意：文件包含客户端令牌明文。";
                Log.Info("统一API配置已导出：" + dlg.FileName);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                txtControlPlaneStatus.Text = "导出失败：" + SafeControlPlaneText(ex.Message);
            }
        }

        private void btnImportControlPlane_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = "导入统一 API 服务配置",
                    Filter = "JSON配置文件 (*.json)|*.json|所有文件 (*.*)|*.*"
                };
                if (dlg.ShowDialog() != true) return;
                var root = JObject.Parse(File.ReadAllText(dlg.FileName, Encoding.UTF8));
                var obj = root["config"] as JObject ?? root;
                var kind = (obj["kind"] ?? root["kind"] ?? string.Empty).ToString();
                if (!string.IsNullOrWhiteSpace(kind)
                    && !string.Equals(kind, "qianniu-control-plane-config", StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception("这不是统一 API 服务配置文件。");
                }

                txtControlPlaneUrl.Text = (obj["serverUrl"] ?? obj["url"] ?? string.Empty).ToString();
                pwdControlPlaneToken.Password = (obj["clientToken"] ?? obj["token"] ?? string.Empty).ToString();
                txtControlPlaneTextRoute.Text = (obj["textRoute"] ?? "text-default").ToString();
                txtControlPlaneVisionRoute.Text = (obj["visionRoute"] ?? "vision-default").ToString();
                bool visionEnabled;
                chkControlPlaneVision.IsChecked = bool.TryParse(
                    (obj["visionEnabled"] ?? "false").ToString(),
                    out visionEnabled) && visionEnabled;
                txtControlPlaneAdminUrl.Text = (obj["adminUrl"] ?? obj["serverUrl"] ?? string.Empty).ToString();

                string error;
                var endpoint = BuildControlPlaneEndpoint(out error);
                if (endpoint == null) throw new Exception(error);
                SaveControlPlaneConfiguration(endpoint);
                txtControlPlaneStatus.Text = "统一 API 配置已导入并保存。建议立即点击【测试连接与文本调用】。";
                Log.Info("统一API配置已导入：" + dlg.FileName);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                txtControlPlaneStatus.Text = "导入失败：" + SafeControlPlaneText(ex.Message);
            }
        }

        private async void btnTestBotFlow_Click(object sender, RoutedEventArgs e)
        {
            btnTestBotFlow.IsEnabled = false;
            try
            {
                txtControlPlaneStatus.Text = "正在选择一条真实买家消息作为测试问题...";
                var candidate = await BotFlowTestService.PickRandomCandidateAsync();
                if (candidate == null)
                {
                    txtControlPlaneStatus.Text = "没有找到可用的真实买家文本消息。请先让测试买家发一条普通文字消息，或打开一个有聊天记录的买家会话后重试。";
                    return;
                }

                var confirm = "将执行一次真实 Bot 流程：\n\n"
                    + "客服：" + candidate.Seller + "\n"
                    + "买家：" + candidate.Buyer + "\n"
                    + "问题来源：" + candidate.Source + "\n"
                    + "测试问题：" + candidate.Question + "\n\n"
                    + "系统会真实生成答案、切换到该买家、写入并发送带【Bot测试】前缀的消息。"
                    + "测试结束后请在千牛中手动撤回测试消息。\n\n确认继续？";
                if (MessageBox.Show(
                    confirm,
                    "测试 Bot 真实流程",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    txtControlPlaneStatus.Text = "已取消 Bot 真实流程测试。";
                    return;
                }

                txtControlPlaneStatus.Text = "正在执行真实流程：生成答案 → 定位买家 → 写入输入框 → 发送 → 确认...";
                var result = await BotFlowTestService.RunAsync(candidate);
                txtControlPlaneStatus.Text = (result.Success ? "成功：" : "失败：")
                    + result.Detail
                    + "\n客服：" + result.Seller
                    + "；买家：" + result.Buyer
                    + "\nAI耗时 " + result.AiLatencyMs + "ms"
                    + "；发送耗时 " + result.SendLatencyMs + "ms"
                    + "；总耗时 " + result.TotalLatencyMs + "ms";
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                txtControlPlaneStatus.Text = "Bot真实流程测试异常：" + SafeControlPlaneText(ex.Message);
            }
            finally
            {
                btnTestBotFlow.IsEnabled = true;
            }
        }
    }
}
