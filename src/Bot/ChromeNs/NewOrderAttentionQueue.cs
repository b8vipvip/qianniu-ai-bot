using Bot.AssistWindow.Widget.Robot;
using Bot.Automation.ChatDeskNs;
using Bot.Options;
using BotLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class OrderAttentionItem
    {
        public OrderSnapshot Snapshot { get; set; }
        public DateTime EnqueuedAt { get; set; }
        public int Attempts { get; set; }
        public DateTime LastUiUpdateAt { get; set; }
    }

    internal static class OrderAttentionUiService
    {
        private static readonly ConcurrentDictionary<string, CtlConversation> Cards =
            new ConcurrentDictionary<string, CtlConversation>(StringComparer.Ordinal);

        public static void ShowPending(OrderSnapshot snapshot)
        {
            if (snapshot == null || Desk.Inst == null) return;
            var key = Key(snapshot);
            if (Cards.ContainsKey(key)) return;
            var question = "【新订单待处理】" + (snapshot.EventType == OrderEventType.Paid ? "已付款" : "新下单")
                + " · 订单号 " + snapshot.OrderId;
            var ctl = Desk.Inst.AddConversation(
                snapshot.Seller,
                snapshot.Buyer,
                question,
                snapshot.BuildSummary(),
                false,
                "新订单待处理");
            if (ctl == null) return;
            Cards[key] = ctl;
            ctl.SetProcessing(OrderAttentionSettings.IsEnabled()
                ? "订单已进入待处理队列，等待Bot空闲后自动切换到该买家..."
                : "订单已识别；空闲自动切换当前已关闭。"
            );
        }

        public static void SetDeferred(OrderSnapshot snapshot, string reason)
        {
            CtlConversation ctl;
            if (snapshot == null || !Cards.TryGetValue(Key(snapshot), out ctl) || ctl == null) return;
            ctl.SetProcessing("新订单等待中：" + (reason ?? "当前仍有任务"));
        }

        public static void SetFocused(OrderSnapshot snapshot)
        {
            CtlConversation ctl;
            if (snapshot == null || !Cards.TryGetValue(Key(snapshot), out ctl) || ctl == null) return;
            ctl.SetStatus("已自动切换到该买家会话，等待处理", true);
        }

        public static void SetFocusFailed(OrderSnapshot snapshot, string reason)
        {
            CtlConversation ctl;
            if (snapshot == null || !Cards.TryGetValue(Key(snapshot), out ctl) || ctl == null) return;
            ctl.SetStatus("未自动切换：" + (reason ?? "未知原因") + "，订单仍保留在右侧待处理", false);
        }

        public static void SetReplyResult(OrderSnapshot snapshot, bool delivered)
        {
            CtlConversation ctl;
            if (snapshot == null || !Cards.TryGetValue(Key(snapshot), out ctl) || ctl == null) return;
            ctl.SetStatus(
                delivered ? "已切换并发送下单自动消息" : "已识别订单，但下单自动消息未成功发送",
                delivered);
        }

        private static string Key(OrderSnapshot snapshot)
        {
            return (snapshot.Seller ?? string.Empty).Trim().ToLowerInvariant()
                + "#" + (snapshot.OrderId ?? string.Empty).Trim()
                + "#" + snapshot.EventType;
        }
    }

    public partial class QN
    {
        private readonly object _orderAttentionSync = new object();
        private readonly List<OrderAttentionItem> _orderAttentionItems = new List<OrderAttentionItem>();
        private int _orderAttentionWorkerRunning;
        private DateTime _orderAttentionLastAutoFocusAt = DateTime.MinValue;
        private string _orderAttentionLastAutoFocusedBuyer = string.Empty;
        private string _orderAttentionLastObservedBuyer = string.Empty;
        private DateTime _orderAttentionLastObservedBuyerAt = DateTime.MinValue;

        internal void EnqueueNewOrderAttention(OrderSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Buyer) || string.IsNullOrWhiteSpace(snapshot.OrderId)) return;
            OrderAttentionUiService.ShowPending(snapshot);
            if (!OrderAttentionSettings.IsEnabled()) return;

            lock (_orderAttentionSync)
            {
                var key = AttentionKey(snapshot);
                if (_orderAttentionItems.Any(x => x != null && x.Snapshot != null && AttentionKey(x.Snapshot) == key)) return;
                _orderAttentionItems.Add(new OrderAttentionItem
                {
                    Snapshot = snapshot,
                    EnqueuedAt = DateTime.Now,
                    Attempts = 0,
                    LastUiUpdateAt = DateTime.MinValue
                });
            }

            Log.Info("新订单已加入空闲自动切换队列: seller=" + snapshot.Seller
                + ", buyer=" + snapshot.Buyer
                + ", orderId=" + snapshot.OrderId
                + ", event=" + snapshot.EventType);
            StartOrderAttentionWorker();
        }

        private void StartOrderAttentionWorker()
        {
            if (Interlocked.CompareExchange(ref _orderAttentionWorkerRunning, 1, 0) != 0) return;
            Task.Run(RunOrderAttentionWorkerAsync);
        }

        private async Task RunOrderAttentionWorkerAsync()
        {
            try
            {
                while (true)
                {
                    var item = PeekNextOrderAttention();
                    if (item == null) return;
                    if (!OrderAttentionSettings.IsEnabled())
                    {
                        RemoveOrderAttention(item);
                        OrderAttentionUiService.SetFocusFailed(item.Snapshot, "设置中已关闭空闲自动切换");
                        continue;
                    }
                    if (DateTime.Now - item.EnqueuedAt > TimeSpan.FromMinutes(30))
                    {
                        RemoveOrderAttention(item);
                        OrderAttentionUiService.SetFocusFailed(item.Snapshot, "等待超过30分钟");
                        continue;
                    }

                    string reason;
                    if (!await CanAutoFocusOrderAsync(item.Snapshot, out reason))
                    {
                        item.Attempts++;
                        UpdateDeferredUi(item, reason);
                        await Task.Delay(700);
                        continue;
                    }

                    // 二次稳定确认，避免刚判定为空闲时恰好开始生成或人工输入。
                    await Task.Delay(650);
                    if (!await CanAutoFocusOrderAsync(item.Snapshot, out reason))
                    {
                        item.Attempts++;
                        UpdateDeferredUi(item, reason);
                        await Task.Delay(500);
                        continue;
                    }

                    var focused = await FocusOrderBuyerAsync(item.Snapshot);
                    if (focused)
                    {
                        RemoveOrderAttention(item);
                        OrderAttentionUiService.SetFocused(item.Snapshot);
                        await Task.Delay(TimeSpan.FromSeconds(OrderAttentionSettings.GetSwitchIntervalSeconds()));
                    }
                    else
                    {
                        item.Attempts++;
                        UpdateDeferredUi(item, "目标会话暂时无法确认，稍后重试");
                        if (item.Attempts >= 30)
                        {
                            RemoveOrderAttention(item);
                            OrderAttentionUiService.SetFocusFailed(item.Snapshot, "连续多次无法确认目标买家会话");
                        }
                        await Task.Delay(1200);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("新订单自动切换队列异常：" + ex.Message, 10);
            }
            finally
            {
                Interlocked.Exchange(ref _orderAttentionWorkerRunning, 0);
                lock (_orderAttentionSync)
                {
                    if (_orderAttentionItems.Count > 0 && OrderAttentionSettings.IsEnabled())
                    {
                        StartOrderAttentionWorker();
                    }
                }
            }
        }

        private OrderAttentionItem PeekNextOrderAttention()
        {
            lock (_orderAttentionSync)
            {
                return _orderAttentionItems
                    .Where(x => x != null && x.Snapshot != null)
                    .OrderByDescending(x => x.Snapshot.EventType == OrderEventType.Paid)
                    .ThenBy(x => x.Snapshot.EventTime)
                    .ThenBy(x => x.EnqueuedAt)
                    .FirstOrDefault();
            }
        }

        private void RemoveOrderAttention(OrderAttentionItem item)
        {
            lock (_orderAttentionSync)
            {
                _orderAttentionItems.Remove(item);
            }
        }

        private void UpdateDeferredUi(OrderAttentionItem item, string reason)
        {
            if (item == null) return;
            if (DateTime.Now - item.LastUiUpdateAt < TimeSpan.FromSeconds(2)) return;
            item.LastUiUpdateAt = DateTime.Now;
            OrderAttentionUiService.SetDeferred(item.Snapshot, reason);
        }

        private async Task<bool> CanAutoFocusOrderAsync(OrderSnapshot snapshot, out string reason)
        {
            reason = string.Empty;
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Buyer))
            {
                reason = "订单缺少买家信息";
                return false;
            }
            if (!Params.Robot.CanUseRobotReal)
            {
                reason = "Bot当前未启用";
                return false;
            }
            if (cdp == null)
            {
                reason = "千牛CDP尚未连接";
                return false;
            }
            if (_incomingMessageGate.CurrentCount < 1)
            {
                reason = "正在处理千牛新消息";
                return false;
            }
            if (_sendGate.CurrentCount < 1)
            {
                reason = "正在切换会话或发送消息";
                return false;
            }
            if (_backgroundRecoveryGate.CurrentCount < 1)
            {
                reason = "正在补抓后台消息";
                return false;
            }
            if (!BotActivityCoordinator.IsSafeToAutoFocus(snapshot.Seller, out reason)) return false;

            bool inputEmpty;
            if (!await TryGetInputboxEmptyAsync(out inputEmpty))
            {
                reason = "暂时无法确认输入框状态";
                return false;
            }
            if (!inputEmpty)
            {
                BotActivityCoordinator.MarkHumanInteraction(snapshot.Seller, "客服输入框中存在未发送内容");
                reason = "客服正在输入消息";
                return false;
            }

            var current = await TryGetCurrentBuyerAsync();
            ObserveVisibleBuyer(snapshot.Seller, current);
            if (!BotActivityCoordinator.IsSafeToAutoFocus(snapshot.Seller, out reason)) return false;

            var interval = OrderAttentionSettings.GetSwitchIntervalSeconds();
            if (_orderAttentionLastAutoFocusAt != DateTime.MinValue
                && DateTime.Now - _orderAttentionLastAutoFocusAt < TimeSpan.FromSeconds(interval))
            {
                reason = "等待最短自动切换间隔";
                return false;
            }
            return true;
        }

        private async Task<bool> FocusOrderBuyerAsync(OrderSnapshot snapshot)
        {
            await _sendGate.WaitAsync();
            using (BotActivityCoordinator.Begin("新订单自动切换", snapshot.Seller, snapshot.Buyer))
            {
                try
                {
                    bool inputEmpty;
                    if (!await TryGetInputboxEmptyAsync(out inputEmpty) || !inputEmpty)
                    {
                        if (!inputEmpty) BotActivityCoordinator.MarkHumanInteraction(snapshot.Seller, "自动切换前检测到客服输入内容");
                        return false;
                    }

                    var current = await TryGetCurrentBuyerAsync();
                    if (string.Equals(current, snapshot.Buyer, StringComparison.Ordinal))
                    {
                        SetActiveConversationByNick(snapshot.Seller, snapshot.Buyer, "newOrderAlreadyActive");
                        _orderAttentionLastAutoFocusAt = DateTime.Now;
                        _orderAttentionLastAutoFocusedBuyer = snapshot.Buyer;
                        return true;
                    }

                    Log.Info("Bot空闲，准备自动切换到新下单买家: seller=" + snapshot.Seller
                        + ", buyer=" + snapshot.Buyer
                        + ", orderId=" + snapshot.OrderId
                        + ", current=" + current);
                    OpenChat(snapshot.Buyer);
                    for (var attempt = 0; attempt < 24; attempt++)
                    {
                        current = await TryGetCurrentBuyerAsync();
                        if (string.Equals(current, snapshot.Buyer, StringComparison.Ordinal))
                        {
                            SetActiveConversationByNick(snapshot.Seller, snapshot.Buyer, "newOrderAutoFocus");
                            _orderAttentionLastAutoFocusAt = DateTime.Now;
                            _orderAttentionLastAutoFocusedBuyer = snapshot.Buyer;
                            _orderAttentionLastObservedBuyer = snapshot.Buyer;
                            _orderAttentionLastObservedBuyerAt = DateTime.Now;
                            Log.Info("新订单买家自动切换成功: buyer=" + snapshot.Buyer + ", orderId=" + snapshot.OrderId);
                            return true;
                        }
                        await Task.Delay(250);
                    }
                    Log.Info("新订单买家自动切换未确认: buyer=" + snapshot.Buyer + ", orderId=" + snapshot.OrderId);
                    return false;
                }
                catch (Exception ex)
                {
                    Log.Info("新订单买家自动切换异常: buyer=" + snapshot.Buyer + ", error=" + ex.Message);
                    return false;
                }
                finally
                {
                    _sendGate.Release();
                }
            }
        }

        private async Task<string> TryGetCurrentBuyerAsync()
        {
            try
            {
                var task = GetCurrentConversationID();
                var completed = await Task.WhenAny(task, Task.Delay(1200));
                if (completed != task) return string.Empty;
                var response = await task;
                return response == null || response.Result == null
                    ? string.Empty
                    : (response.Result.Nick ?? string.Empty).Trim();
            }
            catch { return string.Empty; }
        }

        private async Task<bool> TryGetInputboxEmptyAsync(out bool empty)
        {
            empty = true;
            try
            {
                var task = IsInputboxEmpty();
                var completed = await Task.WhenAny(task, Task.Delay(1200));
                if (completed != task) return false;
                empty = await task;
                return true;
            }
            catch { return false; }
        }

        private void ObserveVisibleBuyer(string seller, string currentBuyer)
        {
            currentBuyer = (currentBuyer ?? string.Empty).Trim();
            if (currentBuyer.Length == 0) return;
            if (_orderAttentionLastObservedBuyer.Length == 0)
            {
                _orderAttentionLastObservedBuyer = currentBuyer;
                _orderAttentionLastObservedBuyerAt = DateTime.Now;
                return;
            }
            if (string.Equals(_orderAttentionLastObservedBuyer, currentBuyer, StringComparison.Ordinal)) return;

            var wasOwnRecentSwitch = string.Equals(_orderAttentionLastAutoFocusedBuyer, currentBuyer, StringComparison.Ordinal)
                && _orderAttentionLastAutoFocusAt != DateTime.MinValue
                && DateTime.Now - _orderAttentionLastAutoFocusAt < TimeSpan.FromSeconds(3);
            _orderAttentionLastObservedBuyer = currentBuyer;
            _orderAttentionLastObservedBuyerAt = DateTime.Now;
            if (!wasOwnRecentSwitch)
            {
                BotActivityCoordinator.MarkHumanInteraction(seller, "客服或其他流程刚切换到买家 " + currentBuyer);
            }
        }

        private static string AttentionKey(OrderSnapshot snapshot)
        {
            return (snapshot.Seller ?? string.Empty).Trim().ToLowerInvariant()
                + "#" + (snapshot.OrderId ?? string.Empty).Trim()
                + "#" + snapshot.EventType;
        }
    }
}
