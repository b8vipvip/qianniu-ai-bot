using Bot.ChatRecord;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    public partial class QN
    {
        private readonly SemaphoreSlim _backgroundRecoveryGate = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, DateTime> _latestBuyerMessageObserved =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> _backgroundRecoveryVersions =
            new ConcurrentDictionary<string, long>(StringComparer.Ordinal);

        private static string RecoveryKey(string seller, string buyer)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = BuyerIdentityAliasService.ResolveInternalNick(seller, buyer);
            return seller + "#" + buyer;
        }

        private void MarkBuyerMessageObserved(string seller, string buyer)
        {
            var key = RecoveryKey(seller, buyer);
            if (key == "#") return;
            _latestBuyerMessageObserved[key] = DateTime.Now;
            long ignored;
            _backgroundRecoveryVersions.TryRemove(key, out ignored);
        }

        private void ScheduleBackgroundMessageRecovery(ShopRobotReceriveNewMessageEventArgs e)
        {
            if (e == null || e.Seller == null || e.Buyer == null) return;
            var seller = (e.Seller.Nick ?? string.Empty).Trim();
            BuyerIdentityAliasService.Observe(seller, e.Buyer.Nick, e.Buyer.Display, e.Buyer.TargetId);
            var buyer = BuyerIdentityAliasService.ResolveInternalNick(seller, e.Buyer.Nick);
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer)) return;
            if (!Params.Robot.CanUseRobotReal) return;

            var key = RecoveryKey(seller, buyer);
            var scheduledAt = DateTime.Now;
            var version = DateTime.UtcNow.Ticks;
            _backgroundRecoveryVersions[key] = version;

            Task.Run(async () =>
            {
                try
                {
                    // 正常 receiveNewMsg 一般会先到。仅当详细买家消息事件缺失时才打开会话补抓。
                    // 订单系统卡片可能不是 buyer->seller 消息，因此即使详细事件已到，也不会调用
                    // MarkBuyerMessageObserved；这时该补偿仍会继续，并从目标买家的远端历史中识别订单卡片。
                    await Task.Delay(1800);
                    long latestVersion;
                    if (!_backgroundRecoveryVersions.TryGetValue(key, out latestVersion) || latestVersion != version) return;
                    DateTime observedAt;
                    if (_latestBuyerMessageObserved.TryGetValue(key, out observedAt)
                        && observedAt >= scheduledAt.AddMilliseconds(-250))
                    {
                        return;
                    }

                    await RecoverMissedBuyerMessagesAsync(seller, buyer, scheduledAt, version);
                }
                catch (Exception ex)
                {
                    Log.Info("后台消息补偿异常: seller=" + seller + ", buyer=" + buyer + ", error=" + ex.Message);
                }
            });
        }

        private async Task RecoverMissedBuyerMessagesAsync(
            string seller,
            string buyer,
            DateTime scheduledAt,
            long version)
        {
            buyer = BuyerIdentityAliasService.ResolveInternalNick(seller, buyer);
            await _backgroundRecoveryGate.WaitAsync();
            using (BotActivityCoordinator.Begin("后台消息补偿", seller, buyer))
            {
                List<QNChatMessage> recovered = null;
                try
                {
                    var key = RecoveryKey(seller, buyer);
                    long latestVersion;
                    if (!_backgroundRecoveryVersions.TryGetValue(key, out latestVersion) || latestVersion != version) return;
                    DateTime observedAt;
                    if (_latestBuyerMessageObserved.TryGetValue(key, out observedAt)
                        && observedAt >= scheduledAt.AddMilliseconds(-250))
                    {
                        return;
                    }
                    if (cdp == null) return;

                    // 与自动发送共用会话切换互斥锁。这里只抓消息，不在持锁期间生成或发送答案。
                    await _sendGate.WaitAsync();
                    try
                    {
                        Log.Info("详细新消息事件未到或可能为系统订单卡片，开始安全补偿: seller=" + seller + ", buyer=" + buyer);
                        OpenChat(buyer);

                        DbEntity.Conversation current = null;
                        for (var attempt = 0; attempt < 24; attempt++)
                        {
                            var response = await GetCurrentConversationID();
                            current = response == null ? null : response.Result;
                            if (current != null
                                && BuyerIdentityAliasService.AreEquivalent(seller, current.Nick, buyer))
                            {
                                BuyerIdentityAliasService.Observe(seller, current.Nick, current.Display, current.TargetId);
                                break;
                            }
                            await Task.Delay(250);
                        }

                        if (current == null || !BuyerIdentityAliasService.AreEquivalent(seller, current.Nick, buyer))
                        {
                            Log.Info("后台消息补偿失败：无法确认目标买家会话。target=" + buyer
                                + ", current=" + (current == null ? string.Empty : current.Nick)
                                + ", equivalent=false");
                            return;
                        }

                        SetActiveConversationByNick(
                            seller,
                            BuyerIdentityAliasService.ResolveConversationKey(seller, current.Nick),
                            "backgroundRecovery");
                        var ccode = (current.Ccode ?? string.Empty).Trim();
                        if (string.IsNullOrWhiteSpace(ccode))
                        {
                            Log.Info("后台消息补偿失败：当前会话没有 ccode。buyer=" + buyer);
                            return;
                        }

                        var history = await cdp.Invoke<JObject>("im.singlemsg.GetRemoteHisMsg", new
                        {
                            cid = new { ccode = ccode, type = 1 },
                            count = 30,
                            gohistory = 1,
                            msgid = "-1",
                            msgtime = "-1"
                        });
                        var messages = history == null
                            ? null
                            : history["result"]?["msgs"]?.ToObject<List<QNChatMessage>>();
                        var threshold = scheduledAt.AddMinutes(-2).Ticks;
                        recovered = (messages ?? new List<QNChatMessage>())
                            .Where(m => m != null)
                            .Where(m =>
                                (IsBuyerMessage(m)
                                    && m.fromid != null
                                    && BuyerIdentityAliasService.AreEquivalent(seller, m.fromid.nick, buyer))
                                || IsPotentialRecoveredOrderCard(m))
                            .Where(m =>
                            {
                                var sort = IncomingMessageSafety.GetSortValue(m);
                                return sort <= 0 || sort >= threshold;
                            })
                            .OrderBy(IncomingMessageSafety.GetSortValue)
                            .ToList();
                    }
                    finally
                    {
                        _sendGate.Release();
                    }

                    if (recovered == null || recovered.Count < 1)
                    {
                        Log.Info("后台消息补偿完成，但没有发现最近买家消息或订单卡片。seller=" + seller + ", buyer=" + buyer);
                        return;
                    }

                    Log.Info("后台消息补偿找回 " + recovered.Count + " 条候选消息/订单卡片。seller=" + seller + ", buyer=" + buyer);
                    foreach (var message in recovered)
                    {
                        await ProcessRecoveredMessageWithKnownBuyerAsync(message, seller, buyer);
                        await Task.Delay(30);
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("后台消息补偿失败: seller=" + seller + ", buyer=" + buyer + ", error=" + ex.Message);
                }
                finally
                {
                    long ignored;
                    _backgroundRecoveryVersions.TryRemove(RecoveryKey(seller, buyer), out ignored);
                    _backgroundRecoveryGate.Release();
                }
            }
        }

        private async Task ProcessRecoveredMessageWithKnownBuyerAsync(
            QNChatMessage message,
            string seller,
            string buyer)
        {
            if (message == null) return;
            var text = GetMessageText(message);
            if (IsPotentialRecoveredOrderCard(message))
            {
                OrderPlacedReplyPlan orderPlan;
                if (OrderPlacedAutoReplyService.TryCreatePlan(
                    message,
                    text,
                    seller,
                    buyer,
                    _messageSafetyStartedAt,
                    out orderPlan))
                {
                    if (orderPlan != null)
                    {
                        Log.Info("后台补偿识别到直接下单订单卡片: seller=" + seller
                            + ", buyer=" + buyer + ", orderId=" + orderPlan.OrderId);
                        await ProcessOrderPlacedReplyAsync(orderPlan);
                    }
                    return;
                }
            }
            await ProcessIncomingMessageAsync(message);
        }

        private static bool IsPotentialRecoveredOrderCard(QNChatMessage message)
        {
            if (message == null) return false;
            OrderSnapshot snapshot;
            return OrderCardParser.TryParse(
                message,
                GetMessageText(message),
                string.Empty,
                string.Empty,
                "千牛远端历史订单卡片",
                out snapshot);
        }
    }
}
