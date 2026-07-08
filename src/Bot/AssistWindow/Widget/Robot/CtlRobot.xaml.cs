using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Markup;
using Bot.Automation.ChatDeskNs;
using DbEntity;
using BotLib.Db.Sqlite;
using BotLib.Wpf.Extensions;
using System.Security.Cryptography;
using Bot.ChromeNs;
using Bot.AssistWindow.Widget.Robot;
using System.Collections.Concurrent;
using System.Linq;
using OpenAI.Chat;
using BotLib.Extensions;
using SuperSocket.SocketEngine.Configuration;
using BotLib;
using System.Windows.Threading;
using System.Windows.Media;

namespace Bot.AssistWindow.Widget.Robot
{
    public partial class CtlRobot : UserControl
    {
        private Desk _desk;
        private RightPanel _rightPanel;
        private WndAssist _wndDontUse;
        private QN _preQN;
        private ConcurrentDictionary<string, List<CtlConversation>> buyerConversations;
        private DispatcherTimer _statsTimer;
        private bool _dashboardVisible;

        public CtlRobot(Desk desk, RightPanel rp)
        {
            InitializeComponent();
            _desk = desk;
            _rightPanel = rp;
            buyerConversations = new ConcurrentDictionary<string, List<CtlConversation>>();
            Loaded += CtlRobot_Loaded;
        }

        private WndAssist Wnd
        {
            get
            {
                if (_wndDontUse == null)
                {
                    _wndDontUse = (this.xFindParentWindow() as WndAssist);
                }
                return _wndDontUse;
            }
            set
            {
                _wndDontUse = value;
            }
        }

        private void CtlRobot_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshRunStatus();
            RefreshStats();
            _statsTimer = new DispatcherTimer();
            _statsTimer.Interval = TimeSpan.FromSeconds(3);
            _statsTimer.Tick += (s, args) => RefreshStats();
            _statsTimer.Start();
        }

        private void RefreshRunStatus()
        {
            if (!Params.Robot.CanUseRobotReal)
            {
                txtRunStatus.Text = "Bot已停用";
                return;
            }
            txtRunStatus.Text = Params.Robot.GetIsAutoReply() ? "自动回复中" : "仅生成答案";
        }

        public void RefreshSwitchState()
        {
            RefreshRunStatus();
        }

        public void ToggleDashboard()
        {
            ShowDashboard(!_dashboardVisible);
        }

        public void ShowDashboard(bool visible)
        {
            _dashboardVisible = visible;
            gridDashboard.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            colDashboard.Width = visible ? new GridLength(310) : new GridLength(0);
            if (visible)
            {
                RefreshStats();
            }
        }

        public void AddConversation(string seller, string buyer, string question, string answer,bool isAutoReply = false)
        {
            BotRuntimeStats.RecordDisplayedAnswer(isAutoReply);
            RefreshStats();

            var key = string.Format("{0}#{1}", seller, buyer);
            var ctlConversation = CtlConversation.Create(question, answer, isAutoReply);
            var conversations = buyerConversations.xTryGetValue(key);
            if (conversations == null || conversations.Count < 1)
            {
                conversations = new List<CtlConversation>() { ctlConversation };
            }
            else
            {
                conversations.Add(ctlConversation);
            }

            buyerConversations.AddOrUpdate(key, id => conversations, (k, v) => conversations);

            if (QN.CurQN != null && QN.CurQN.Seller != null && QN.CurQN.Buyer != null
                && QN.CurQN.Seller.Nick == seller && QN.CurQN.Buyer.Nick == buyer)
            {
                grdTipNoConv.Visibility = Visibility.Collapsed;
                stkDialog.Children.Add(ctlConversation);
            }
            scvBody.ScrollToEnd();
        }

        private void ShowGridTip(Border gd)
        {
            grdTipNoConv.xIsVisible(gd == grdTipNoConv);
            grdShowConv.xIsVisible(false);
        }

        public void ReShowAfterQNChange()
        {
            if (QN.CurQN != null && QN.CurQN.Seller != null && QN.CurQN.Buyer != null)
            {
                _preQN = QN.CurQN;
                RefreshItems();
                RefreshConversations();
                RefreshRunStatus();
            }
        }

        private void RefreshConversations()
        {
            if (_preQN == null || _preQN.Seller == null || _preQN.Buyer == null) return;
            var key = string.Format("{0}#{1}", _preQN.Seller.Nick, _preQN.Buyer.Nick);
            var conversations = buyerConversations.xTryGetValue(key);
            stkDialog.Children.Clear();
            if (conversations != null && conversations.Count > 0)
            {
                conversations.ForEach(conv => stkDialog.Children.Add(conv));
                scvBody.ScrollToEnd();
                grdTipNoConv.Visibility = Visibility.Collapsed;
            }
            else
            {
                ShowGridTip(grdTipNoConv);
                stkDialog.Children.Add(grdTipNoConv);
            }
        }

        private async void RefreshItems()
        {
            try
            {
                if (_preQN == null || _preQN.Buyer == null) return;
                pgDownGoods.Visibility = Visibility.Visible;
                RemoveCtlGoods();
                //咨询的商品
                var itemRecord = await _preQN.GetItemRecords(_preQN.Buyer.TargetId);
                if (itemRecord == null || itemRecord.data == null || itemRecord.data.underInquiryItemList == null)
                {
                    pgDownGoods.Visibility = Visibility.Collapsed;
                }
                else
                {
                    var inquiryItems = itemRecord.data.underInquiryItemList;
                    if (inquiryItems != null && inquiryItems.Count > 0)
                    {
                        foreach (var item in inquiryItems)
                        {
                            var ctlGoods = new CtlOneGoods(item);
                            panelGoods.Children.Add(ctlGoods);
                        }
                    }
                    pgDownGoods.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                pgDownGoods.Visibility = Visibility.Collapsed;
            }
        }

        private void RemoveCtlGoods()
        {
            var idx = 0;
            while (idx < panelGoods.Children.Count)
            {
                if (panelGoods.Children[idx] is CtlOneGoods)
                {
                    panelGoods.Children.RemoveAt(idx);
                }
                else
                {
                    idx++;
                }
            }
        }

        public void ChangeBuyer(string buyer)
        {
            txtBuyer.Text = string.IsNullOrWhiteSpace(buyer) ? "..." : buyer;
            BotRuntimeStats.RecordReception();
            ReShowAfterQNChange();
            RefreshStats();
        }

        public void ChangeSeller(string seller)
        {
            txtSeller.Text = string.IsNullOrWhiteSpace(seller) ? "..." : seller;
            RefreshStats();
        }

        private string FormatNum(long value)
        {
            if (value >= 1000000) return (value / 10000.0).ToString("0.0") + "万";
            return value.ToString();
        }

        private void RefreshStats()
        {
            try
            {
                var snapshot = BotRuntimeStats.GetSnapshot();
                txtTotalReception.Text = FormatNum(snapshot.TotalReceptionCount);
                txtTodayReception.Text = FormatNum(snapshot.TodayReceptionCount);
                txtTotalAutoReply.Text = FormatNum(snapshot.TotalAutoReplies);
                txtTodayAutoReply.Text = FormatNum(snapshot.TodayAutoReplies);
                txtTotalAiCall.Text = FormatNum(snapshot.TotalAiCalls);
                txtTodayAiCall.Text = FormatNum(snapshot.TodayAiCalls);
                txtTotalTokens.Text = FormatNum(snapshot.TotalTokens);
                txtTodayTokens.Text = FormatNum(snapshot.TodayTokens);
                txtAvgLatency.Text = "平均耗时：" + snapshot.AvgLatencyMs + "ms，失败：" + snapshot.TodayAiFailedCalls + "/今日";
                txtLastError.Text = string.IsNullOrWhiteSpace(snapshot.LastError) ? "" : "最近错误：" + snapshot.LastError;

                panelApiStats.Children.Clear();
                if (snapshot.ApiUsages == null || snapshot.ApiUsages.Count < 1)
                {
                    panelApiStats.Children.Add(new TextBlock
                    {
                        Text = "暂无API调用数据",
                        Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                        FontSize = 12
                    });
                    return;
                }

                foreach (var api in snapshot.ApiUsages)
                {
                    var border = new Border
                    {
                        Background = Brushes.White,
                        BorderBrush = new SolidColorBrush(Color.FromRgb(230, 236, 242)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(8),
                        Margin = new Thickness(0, 0, 0, 6)
                    };
                    var sp = new StackPanel();
                    sp.Children.Add(new TextBlock { Text = api.EndpointName, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55)) });
                    sp.Children.Add(new TextBlock { Text = "今日调用 " + api.TodayCalls + " 次｜今日Token " + api.TodayTokens, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)), Margin = new Thickness(0, 4, 0, 0) });
                    sp.Children.Add(new TextBlock { Text = "总Token " + api.TotalTokens + "｜失败 " + api.FailedCalls + "｜均耗时 " + api.AvgLatencyMs + "ms", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)) });
                    border.Child = sp;
                    panelApiStats.Children.Add(border);
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }
    }
}