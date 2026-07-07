using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using Bot.AssistWindow;
using Bot.Common;
using Bot.Common.Windows;
using BotLib;
using BotLib.Misc;
using BotLib.Wpf.Extensions;
using BotLib.Extensions;
using DbEntity;
using static Bot.Params;
using System.Drawing;
using System.Windows.Media.Imaging;
using System.IO;
using Microsoft.Win32;
using Bot.Common.Trivial;
using Bot.Asset;
using Bot.Automation.ChatDeskNs;
using SuperSocket.Common;
using SuperSocket.SocketEngine.Configuration;
using Bot.ChromeNs;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bot.Options
{
    public partial class CtlRobotOptions : UserControl, IOptions
    {
        private string _seller;
        private string _sellerMain;

        private string _sendImagePath;
        private SynableImageHelper _imageHelper = new SynableImageHelper(TransferFileTypeEnum.RuleAnswerImage);

        public CtlRobotOptions(string seller)
        {
            InitializeComponent();
            InitUI(seller);
        }

        public OptionEnum OptionType
        {
            get
            {
                return OptionEnum.RemindPay;
            }
        }

        public void InitUI(string seller)
        {
            _seller = seller;
            _sellerMain = TbNickHelper.GetMainPart(seller);
            txtBaseUrl.Text = Params.Robot.GetBaseUrl();
            txtApiKey.Text = Params.Robot.GetApiKey();
            txtModelName.Text = Params.Robot.GetModelName();
            txtSystemPrompt.Text = Params.Robot.GetSystemPrompt();
            txtApiTestResult.Text = string.Empty;
        }

        public void NavHelp()
        {
            throw new NotImplementedException();
        }

        public void RestoreDefault()
        {
            Params.Robot.SetBaseUrl(string.Empty);
            Params.Robot.SetApiKey(string.Empty);
            Params.Robot.SetModelName(string.Empty);
            Params.Robot.SetSystemPrompt(string.Empty);
        }

        public void Save(string seller)
        {
            Params.Robot.SetBaseUrl(txtBaseUrl.Text.Trim());
            Params.Robot.SetApiKey(txtApiKey.Text.Trim());
            Params.Robot.SetModelName(txtModelName.Text.Trim());
            Params.Robot.SetSystemPrompt(txtSystemPrompt.Text.Trim());
        }

        private JObject BuildConfigJson()
        {
            return new JObject
            {
                ["baseUrl"] = txtBaseUrl.Text.Trim(),
                ["apiKey"] = txtApiKey.Text.Trim(),
                ["model"] = txtModelName.Text.Trim(),
                ["systemPrompt"] = txtSystemPrompt.Text.Trim(),
                ["exportedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
        }

        private string GetJsonString(JObject json, params string[] names)
        {
            if (json == null || names == null) return string.Empty;
            foreach (var name in names)
            {
                var token = json[name];
                if (token != null) return token.ToString();
            }
            return string.Empty;
        }

        private void ApplyConfigJson(JObject json)
        {
            txtBaseUrl.Text = GetJsonString(json, "baseUrl", "BaseUrl");
            txtApiKey.Text = GetJsonString(json, "apiKey", "ApiKey");
            txtModelName.Text = GetJsonString(json, "model", "Model", "modelName", "ModelName");
            txtSystemPrompt.Text = GetJsonString(json, "systemPrompt", "SystemPrompt");
            Save(_seller);
        }

        private async void btnTestApi_Click(object sender, RoutedEventArgs e)
        {
            btnTestApi.IsEnabled = false;
            txtApiTestResult.Text = "正在测试...";
            try
            {
                var baseUrl = txtBaseUrl.Text.Trim();
                var apiKey = txtApiKey.Text.Trim();
                var model = txtModelName.Text.Trim();
                var prompt = txtSystemPrompt.Text.Trim();
                var result = await Task.Run(() => MyOpenAI.TestConnection(baseUrl, apiKey, model, prompt));
                txtApiTestResult.Text = result;
                Log.Info("AI连接测试结果：" + result);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                txtApiTestResult.Text = "测试异常：" + ex.Message;
            }
            finally
            {
                btnTestApi.IsEnabled = true;
            }
        }

        private void btnExportConfig_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new SaveFileDialog
                {
                    Title = "导出AI配置",
                    FileName = "qianniu-ai-config.json",
                    Filter = "JSON配置文件 (*.json)|*.json|所有文件 (*.*)|*.*"
                };
                if (dlg.ShowDialog() != true) return;

                File.WriteAllText(dlg.FileName, BuildConfigJson().ToString(Formatting.Indented), System.Text.Encoding.UTF8);
                txtApiTestResult.Text = "配置已导出：" + dlg.FileName;
                Log.Info("AI配置已导出：" + dlg.FileName);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                txtApiTestResult.Text = "导出失败：" + ex.Message;
            }
        }

        private void btnImportConfig_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = "导入AI配置",
                    Filter = "JSON配置文件 (*.json)|*.json|所有文件 (*.*)|*.*"
                };
                if (dlg.ShowDialog() != true) return;

                var json = JObject.Parse(File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8));
                ApplyConfigJson(json);
                txtApiTestResult.Text = "配置已导入并保存。";
                Log.Info("AI配置已导入：" + dlg.FileName);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                txtApiTestResult.Text = "导入失败：" + ex.Message;
            }
        }

        private void btnHelp_Click(object sender, RoutedEventArgs e)
        {

        }
    }
}
