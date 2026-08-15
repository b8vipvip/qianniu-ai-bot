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
        private const int BackgroundRecoveryInitialDelayMs = 1000;
        private const int BackgroundRecoverySendGateWaitMs = 1200;
        private const int BackgroundRecoveryGateWaitMs = 900;
        private const int BackgroundRecoveryMaxAttempts = 8;

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
                    // 正常 receiveNewMsg 一般会很快到达。只有详细事件缺失时才自动切到目标买家补抓，
                    // 避免每个后台通知都抢走人工客服正在查看的会话。
                    await Task.Delay(BackgroundRecoveryInitialDelayMs).ConfigureAwait(false);

                    for (var attempt = 1; attempt <= BackgroundRecoveryMaxAttempts; attempt++)
                    {
                        long latestVersion;
                        if (!_backgroundRecoveryVersions.TryGetValue(key, out latestVersion) || latestVersion != version) return;

                        DateTime observedAt;
                        if (_latestBuyerMessageObserved.TryGetValue(key, out observedAt)
                            && observedAt >= scheduledAt.AddMilliseconds(-250))
                        {
                            return;
                        }

                        Log.Info("后台消息补偿调度: seller=" + seller + ", buyer=" + buyer
                            + ", attempt=" + attempt + "/" + BackgroundRecoveryMaxAttempts);

                        if (await RecoverMissedBuyerMessagesAsync(seller, buyer, scheduledAt, version, attempt).ConfigureAwait(false))
                        {
                            return;
                        }

                        if (attempt < BackgroundRecoveryMaxAttempts)
                        {
                            var retryDelayMs = Math.Min(2000, 500 + attempt * 250);
                            Log.Info("后台消息补偿暂未取得安全切换机会，将重试: seller=" + seller
                                + ", buyer=" + buyer + ", delayMs=" + retryDelayMs);
                            await Task.Delay(retryDelayMs).ConfigureAwait(false);
                        }
                    }

                    Log.Info("后台消息补偿多次重试仍未完成，保留明确日志等待下一条后台通知重新触发: seller="
                        + seller + ", buyer=" + buyer);
                }
                catch (Exception ex)
                {
                    Log.Info("后台消息补偿异常: seller=" + seller + ", buyer=" + buyer + ", error=" + ex.Message);
                }
                finally
                {
                    long latestVersion;
                    if (_backgroundRecoveryVersions.TryGetValue(key, out latestVersion) && latestVersion == version)
                    {
                        long ignored;
                        _backgroundRecoveryVersions.TryRemove(key, out ignored);
                    }
                }
            });
        }

        private async Task<bool> RecoverMissedBuyerMessagesAsync(
            string seller,
            string buyer,
            DateTime scheduledAt,
            long version,
            int attempt)
        {
            buyer = BuyerIdentityAliasService.ResolveInternalNick(seller, buyer);
            var key = RecoveryKey(seller, buyer);
            var sendGateAcquired = false;
            var recoveryGateAcquired = false;
            List<QNChatMessage> recovered = null;

            try
            {
                // 先等发送锁，且必须有界。旧实现先无限占住 recovery gate 再无限等 send gate，
                // 一次卡住就会让后续所有后台通知都无法自动切换/补抓。
                var waitStartedAt = DateTime.Now;
                sendGateAcquired = await _sendGate.WaitAsync(BackgroundRecoverySendGateWaitMs).ConfigureAwait(false);
                if (!sendGateAcquired)
                {
                    Log.Info("后台消息补偿等待发送锁超时: seller=" + seller + ", buyer=" + buyer
                        + ", attempt=" + attempt + ", waitMs="
                        + (int)(DateTime.Now - waitStartedAt).TotalMilliseconds);
                    return false;
                }

                waitStartedAt = DateTime.Now;
                recoveryGateAcquired = await _backgroundRecoveryGate.WaitAsync(BackgroundRecoveryGateWaitMs).ConfigureAwait(false);
                if (!recoveryGateAcquired)
                {
                    Log.Info("后台消息补偿等待恢复锁超时: seller=" + seller + ", buyer=" + buyer
                        + ", attempt=" + attempt + ", waitMs="
                        + (int)(DateTime.Now - waitStartedAt).TotalMilliseconds);
                    return false;
                }

                using (BotActivityCoordinator.Begin("后台消息补偿", seller, buyer))
                {
                    long latestVersion;
                    if (!_backgroundRecoveryVersions.TryGetValue(key, out latestVersion) || latestVersion != version) return true;

                    DateTime observedAt;
                    if (_latestBuyerMessageObserved.TryGetValue(key, out observedAt)
                        && observedAt >= scheduledAt.AddMilliseconds(-250))
                    {
                        return true;
                    }

                    if (cdp == null)
                    {
                        Log.Info("后台消息补偿暂不可用：权威CDP尚未就绪。seller=" + seller + ", buyer=" + buyer);
                        return false;
                    }

                    Log.Info("详细新消息事件未到，Bot准备自动切换目标买家并补抓历史: seller="
                        + seller + ", buyer=" + buyer + ", attempt=" + attempt);
                    OpenChat(buyer);

                    DbEntity.Conversation current = null;
                    for (var switchAttempt = 0; switchAttempt < 16; switchAttempt++)
                    {
                        var response = await GetCurrentConversationID().ConfigureAwait(false);
                        current = response == null ? null : response.Result;
                        if (current != null
                            && BuyerIdentityAliasService.AreEquivalent(seller, current.Nick, buyer))
                        {
                            BuyerIdentityAliasService.Observe(seller, current.Nick, current.Display, current.TargetId);
                            break;
                        }
                        await Task.Delay(200).ConfigureAwait(false);
                    }

                    if (current == null || !BuyerIdentityAliasService.AreEquivalent(seller, current.Nick, buyer))
                    {
                        Log.Info("后台消息补偿自动切换失败：无法确认目标买家会话。target=" + buyer
                            + ", current=" + (current == null ? string.Empty : current.Nick)
                            + ", attempt=" + attempt + ", equivalent=false");
                        return false;
                    }

                    Log.Info("后台消息补偿已自动切换到目标买家: seller=" + seller
                        + ", buyer=" + buyer + ", current=" + current.Nick + ", attempt=" + attempt);

                    SetActiveConversationByNick(
                        seller,
                        BuyerIdentityAliasService.ResolveConversationKey(seller, current.Nick),
                        "backgroundRecoveryAutoSwitch");

                    var ccode = (current.Ccode ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(ccode))
                    {
                        Log.Info("后台消息补偿失败：当前会话没有 ccode。buyer=" + buyer + ", attempt=" + attempt);
                        return false;
                    }

                    var history = await cdp.Invoke<JObject>("im.singlemsg.GetRemoteHisMsg", new
                    {
                        cid = new { ccode = ccode, type = 1 },
                        count = 30,
                        gohistory = 1,
                        msgid = "-1",
                        msgtime = "-1"
                    }).ConfigureAwait(false);

                    if (history == null)
                    {
                        Log.Info("后台消息补偿远端历史返回为空，将重试: seller=" + seller
                            + ", buyer=" + buyer + ", attempt=" + attempt);
                        return false;
                    }

                    var messages = history["result"]?["msgs"]?.ToObject<List<QNChatMessage>>();
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
            }
            catch (Exception ex)
            {
                Log.Info("后台消息补偿失败: seller=" + seller + ", buyer=" + buyer
                    + ", attempt=" + attempt + ", error=" + ex.Message);
                return false;
            }
            finally
            {
                if (recoveryGateAcquired) _backgroundRecoveryGate.Release();
                if (sendGateAcquired) _sendGate.Release();
            }

            // 抓取完成后立即释放会话/发送锁，再处理消息和生成答案，避免 AI/规则处理继续占锁。
            if (recovered == null || recovered.Count < 1)
            {
                Log.Info("后台消息补偿完成，但没有发现最近买家消息或订单卡片。seller=" + seller + ", buyer=" + buyer);
                return true;
            }

            Log.Info("后台消息补偿找回 " + recovered.Count + " 条候选消息/订单卡片。seller=" + seller + ", buyer=" + buyer);
            foreach (var message in recovered)
            {
                try
                {
                    await ProcessRecoveredMessageWithKnownBuyerAsync(message, seller, buyer).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Info("后台补偿候选消息处理失败: seller=" + seller + ", buyer=" + buyer
                        + ", error=" + ex.Message);
                }
                await Task.Delay(30).ConfigureAwait(false);
            }
            return true;
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
                        await ProcessOrderPlacedReplyAsync(orderPlan).ConfigureAwait(false);
                    }
                    return;
                }
            }
            await ProcessIncomingMessageAsync(message).ConfigureAwait(false);
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
