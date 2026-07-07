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
            Params.Robot.SetBaseUrl( txtBaseUrl.Text.Trim());
            Params.Robot.SetApiKey( txtApiKey.Text.Trim());
            Params.Robot.SetModelName(txtModelName.Text.Trim());
            Params.Robot.SetSystemPrompt(txtSystemPrompt.Text.Trim());
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

        private void btnHelp_Click(object sender, RoutedEventArgs e)
        {

        }
    }
}