using Bot.ChatRecord;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SuperWebSocket;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Bot
{
    public partial class App
    {
        private readonly object _duplicateCdpInboundRecoveryBridgeBootstrap =
            ChromeNs.DuplicateCdpInboundRecoveryBridge.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    /// <summary>
    /// Qianniu can expose several recent.html/iframe WebSocket pages for one logged-in seller.
    /// MyWebSocketServer intentionally keeps exactly one authoritative CDP for commands/sending,
    /// but buyer notifications can arrive on a different page. This bridge accepts inbound-only
    /// notifications from those duplicate pages and replays them through the authoritative CDP
    /// without ever changing outbound ownership or the visible conversation.
    /// </summary>
    internal static class DuplicateCdpInboundRecoveryBridge
    {
        private sealed class PendingInboundEvent
        {
            public string Seller;
            public string SourceSession;
            public string Type;
            public string Response;
            public DateTime ReceivedAt;
        }

        private static readonly ConcurrentDictionary<string, string> SessionSellers =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private static readonly ConcurrentQueue<PendingInboundEvent> Pending =
            new ConcurrentQueue<PendingInboundEvent>();
        private static Timer _retryTimer;
        private static int _initialized;
        private static int _draining;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                MyWebSocketServer.WSocketSvrInst.OnRecieveMessage += OnWebSocketMessage;
                _retryTimer = new Timer(_ => DrainPending(), null, 250, 250);
                Log.Info("重复千牛CDP入站消息恢复桥已启动：发送通道仍保持单一权威会话。");
            }
            return new object();
        }

        private static void OnWebSocketMessage(object sender, WSocketNewMessageEventArgs e)
        {
            var session = sender as WebSocketSession;
            if (session == null || e == null) return;
            var sessionId = (session.SessionID ?? string.Empty).Trim();
            if (sessionId.Length == 0) return;

            if (string.Equals(e.Type, "qnbotStatus", StringComparison.Ordinal))
            {
                ObserveStatusSeller(sessionId, e.Value);
                return;
            }

            if (!IsRecoverableInboundType(e.Type)) return;
            var response = e.Value ?? string.Empty;
            if (response.Length == 0) return;

            var seller = ResolveSeller(sessionId, e.Type, response);
            if (seller.Length == 0)
            {
                Log.Info("重复CDP入站消息暂无法确定seller，等待正常通道处理: session="
                    + sessionId + ", type=" + e.Type);
                return;
            }

            SessionSellers[sessionId] = seller;
            var item = new PendingInboundEvent
            {
                Seller = seller,
                SourceSession = sessionId,
                Type = e.Type,
                Response = response,
                ReceivedAt = DateTime.Now
            };

            if (!TryDeliverLive(item))
            {
                Pending.Enqueue(item);
                Log.Info("千牛入站消息已暂存等待权威CDP就绪: seller=" + seller
                    + ", session=" + sessionId + ", type=" + e.Type);
            }
        }

        private static bool IsRecoverableInboundType(string type)
        {
            // onChatDlgActive/onConversationChange can mutate active UI state and must never be
            // replayed from a duplicate page. Only buyer-message notifications are bridged.
            return string.Equals(type, "receiveNewMsg", StringComparison.Ordinal)
                || string.Equals(type, "onShopRobotReceriveNewMsgs", StringComparison.Ordinal);
        }

        private static void ObserveStatusSeller(string sessionId, string response)
        {
            try
            {
                var jo = JObject.Parse(response ?? "{}");
                var seller = Convert.ToString(jo["loginNick"] ?? string.Empty).Trim();
                if (seller.Length > 0) SessionSellers[sessionId] = seller;
            }
            catch
            {
            }
        }

        private static string ResolveSeller(string sessionId, string type, string response)
        {
            string known;
            if (SessionSellers.TryGetValue(sessionId, out known)
                && !string.IsNullOrWhiteSpace(known))
            {
                return known.Trim();
            }

            if (string.Equals(type, "onShopRobotReceriveNewMsgs", StringComparison.Ordinal))
            {
                try
                {
                    var active = JsonConvert.DeserializeObject<ActiveLocalUser>(response);
                    var seller = active == null || active.LoginID == null
                        ? string.Empty
                        : (active.LoginID.Nick ?? string.Empty).Trim();
                    if (seller.Length > 0) return seller;
                }
                catch
                {
                }
            }

            if (string.Equals(type, "receiveNewMsg", StringComparison.Ordinal))
            {
                try
                {
                    var chat = JsonConvert.DeserializeObject<ChatResponse>(response);
                    var ids = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var message in chat == null || chat.result == null
                        ? new List<QNChatMessage>()
                        : chat.result.Where(x => x != null))
                    {
                        if (message.fromid != null && !string.IsNullOrWhiteSpace(message.fromid.nick))
                            ids.Add(message.fromid.nick.Trim());
                        if (message.toid != null && !string.IsNullOrWhiteSpace(message.toid.nick))
                            ids.Add(message.toid.nick.Trim());
                    }

                    var sellers = QN.GetRuntimeSafetySnapshot()
                        .Where(qn => qn != null && qn.Seller != null
                            && !string.IsNullOrWhiteSpace(qn.Seller.Nick)
                            && ids.Contains(qn.Seller.Nick.Trim()))
                        .Select(qn => qn.Seller.Nick.Trim())
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    if (sellers.Count == 1) return sellers[0];
                }
                catch
                {
                }
            }

            return string.Empty;
        }

        private static bool TryDeliverLive(PendingInboundEvent item)
        {
            var qn = QN.FindExistingBySellerNick(item.Seller);
            var target = qn == null ? null : qn.CDP;
            if (target == null || target.IsInvalidated) return false;

            // The authoritative page already receives its own live global event normally.
            // Only duplicate-page live events need explicit forwarding.
            if (string.Equals(target.SessionId, item.SourceSession, StringComparison.Ordinal))
                return true;

            target.DispatchInboundEvent(item.Type, item.Response);
            Log.Info("重复千牛CDP入站消息已转交权威会话: seller=" + item.Seller
                + ", fromSession=" + item.SourceSession
                + ", toSession=" + target.SessionId
                + ", type=" + item.Type);
            return true;
        }

        private static void DrainPending()
        {
            if (Interlocked.Exchange(ref _draining, 1) != 0) return;
            try
            {
                var count = Pending.Count;
                for (var i = 0; i < count; i++)
                {
                    PendingInboundEvent item;
                    if (!Pending.TryDequeue(out item) || item == null) continue;
                    if (DateTime.Now - item.ReceivedAt > TimeSpan.FromSeconds(15))
                    {
                        Log.Info("千牛入站暂存消息等待权威CDP超时，已放弃: seller=" + item.Seller
                            + ", session=" + item.SourceSession + ", type=" + item.Type);
                        continue;
                    }

                    var qn = QN.FindExistingBySellerNick(item.Seller);
                    var target = qn == null ? null : qn.CDP;
                    if (target == null || target.IsInvalidated)
                    {
                        Pending.Enqueue(item);
                        continue;
                    }

                    // This item was queued specifically because no QN event consumer was ready at
                    // arrival time. Replay even if that same source page later became authoritative.
                    target.DispatchInboundEvent(item.Type, item.Response);
                    Log.Info("已补发初始化期间暂存的千牛入站消息: seller=" + item.Seller
                        + ", fromSession=" + item.SourceSession
                        + ", toSession=" + target.SessionId
                        + ", type=" + item.Type);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("重复千牛CDP入站消息恢复失败: " + ex.Message, 10);
            }
            finally
            {
                Interlocked.Exchange(ref _draining, 0);
            }
        }
    }
}
