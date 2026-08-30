using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using BotLib;
using SuperWebSocket;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Bot.ChromeNs;
using Bot.Automation;
using Bot.Automation.ChatDeskNs;
using SuperSocket.SocketBase.Config;
using Bot.AssistWindow.NotifyIcon;

namespace Bot.ChromeNs
{
    public class MyWebSocketServer
    {
        public static MyWebSocketServer WSocketSvrInst = null;
        private readonly ConcurrentDictionary<string, CDPClient> _clients = new ConcurrentDictionary<string, CDPClient>();
        private readonly ConcurrentDictionary<string, bool> _initialized = new ConcurrentDictionary<string, bool>();
        private readonly ConcurrentDictionary<string, bool> _initializing = new ConcurrentDictionary<string, bool>();
        private readonly ConcurrentDictionary<string, bool> _connectedSessions = new ConcurrentDictionary<string, bool>();
        private readonly ConcurrentDictionary<string, string> _lastStatusBindings = new ConcurrentDictionary<string, string>();
        private readonly ConcurrentDictionary<string, string> _sellerSessions = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _sessionSellers = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private readonly object _sellerSessionSync = new object();

        static MyWebSocketServer()
        {
            if (WSocketSvrInst == null) WSocketSvrInst = new MyWebSocketServer();
        }

        public EventHandler<WSocketNewMessageEventArgs> OnRecieveMessage;

        private CDPClient GetOrCreateClient(WebSocketSession session)
        {
            return _clients.GetOrAdd(session.SessionID, id => new CDPClient(session));
        }

        private static string ReadJsonString(JObject jo, string name)
        {
            if (jo == null) return string.Empty;
            var token = jo[name];
            return token == null ? string.Empty : token.ToString().Trim();
        }

        private static ulong StableLogHash(string value)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            unchecked
            {
                foreach (var ch in value ?? string.Empty)
                {
                    hash ^= (byte)(ch & 0xff);
                    hash *= prime;
                    hash ^= (byte)(ch >> 8);
                    hash *= prime;
                }
            }
            return hash;
        }

        private static string LogRef(string kind, string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0) return (kind ?? "id") + "#none";
            var hash = StableLogHash(value).ToString("x16");
            return (kind ?? "id") + "#" + hash.Substring(0, 10);
        }

        private static string PayloadSummary(string response)
        {
            response = response ?? string.Empty;
            return "length=" + response.Length + ", payloadRef=" + LogRef("payload", response);
        }

        private bool TryClaimSellerSession(string sellerNick, string sessionId)
        {
            sellerNick = (sellerNick ?? string.Empty).Trim();
            sessionId = (sessionId ?? string.Empty).Trim();
            if (sellerNick.Length == 0 || sessionId.Length == 0) return false;

            lock (_sellerSessionSync)
            {
                string owner;
                if (_sellerSessions.TryGetValue(sellerNick, out owner))
                {
                    if (string.Equals(owner, sessionId, StringComparison.Ordinal))
                    {
                        _sessionSellers[sessionId] = sellerNick;
                        return true;
                    }

                    if (!string.IsNullOrWhiteSpace(owner) && _connectedSessions.ContainsKey(owner))
                    {
                        return false;
                    }

                    string staleOwner;
                    _sellerSessions.TryRemove(sellerNick, out staleOwner);
                    if (!string.IsNullOrWhiteSpace(staleOwner))
                    {
                        string staleSeller;
                        _sessionSellers.TryRemove(staleOwner, out staleSeller);
                    }
                }

                _sellerSessions[sellerNick] = sessionId;
                _sessionSellers[sessionId] = sellerNick;
                Log.Info("已选定卖家权威千牛CDP会话: sellerRef=" + LogRef("seller", sellerNick)
                    + ", sessionRef=" + LogRef("session", sessionId));
                return true;
            }
        }

        private bool IsAuthoritativeSellerSession(string sellerNick, string sessionId)
        {
            sellerNick = (sellerNick ?? string.Empty).Trim();
            sessionId = (sessionId ?? string.Empty).Trim();
            if (sellerNick.Length == 0 || sessionId.Length == 0) return false;
            string owner;
            return _sellerSessions.TryGetValue(sellerNick, out owner)
                && string.Equals(owner, sessionId, StringComparison.Ordinal);
        }

        private void ReleaseSellerSession(string sessionId)
        {
            sessionId = (sessionId ?? string.Empty).Trim();
            if (sessionId.Length == 0) return;
            lock (_sellerSessionSync)
            {
                string sellerNick;
                if (!_sessionSellers.TryRemove(sessionId, out sellerNick)
                    || string.IsNullOrWhiteSpace(sellerNick)) return;

                string owner;
                if (_sellerSessions.TryGetValue(sellerNick, out owner)
                    && string.Equals(owner, sessionId, StringComparison.Ordinal))
                {
                    string removed;
                    _sellerSessions.TryRemove(sellerNick, out removed);
                    Log.Info("卖家权威千牛CDP会话已释放，等待在线页面自动接管: sellerRef="
                        + LogRef("seller", sellerNick) + ", sessionRef=" + LogRef("session", sessionId));
                }
            }
        }

        private bool ShouldRefreshStatusBinding(string sessionId, string loginNick, string conversationNick)
        {
            var key = (loginNick ?? string.Empty).Trim() + "\n" + (conversationNick ?? string.Empty).Trim();
            string previous;
            if (_lastStatusBindings.TryGetValue(sessionId, out previous)
                && string.Equals(previous, key, StringComparison.Ordinal))
            {
                return false;
            }
            _lastStatusBindings[sessionId] = key;
            return true;
        }

        private async Task TryBindStatusConversation(WebSocketSession session, string loginNick, string conversationNick)
        {
            if (session == null) return;
            loginNick = (loginNick ?? string.Empty).Trim();
            conversationNick = (conversationNick ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(loginNick) && string.IsNullOrWhiteSpace(conversationNick)) return;

            try
            {
                var cdp = GetOrCreateClient(session);
                QN qn = null;
                if (!string.IsNullOrWhiteSpace(loginNick))
                {
                    qn = QN.FindExistingBySellerNick(loginNick);
                }

                if (qn == null)
                {
                    try
                    {
                        var user = await cdp.GetCurrentUser();
                        if (user != null && user.Result != null && !string.IsNullOrWhiteSpace(user.Result.Nick))
                        {
                            qn = QN.GetByNick(user.Result);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Info("状态绑定当前会话时获取登录用户失败: " + ex.Message);
                    }
                }

                if (qn == null)
                {
                    BotConnectionDiagnostics.RecordBuyerSeller(loginNick, conversationNick);
                    return;
                }

                var sellerNick = qn.Seller == null ? loginNick : qn.Seller.Nick;
                if (!TryClaimSellerSession(sellerNick, session.SessionID))
                {
                    _initialized[session.SessionID] = true;
                    Log.Info("已忽略同一卖家的重复千牛CDP状态会话，避免重启后反复切换发送通道: sellerRef="
                        + LogRef("seller", sellerNick) + ", sessionRef=" + LogRef("session", session.SessionID));
                    return;
                }

                qn.CDP = cdp;
                qn.SetActiveConversationByNick(sellerNick, conversationNick, "qnbotStatus");
                BotConnectionDiagnostics.RecordCdpStatus(true, "已获取", sellerNick, conversationNick);
                _initialized[session.SessionID] = true;
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }

        private async Task TryInitSession(WebSocketSession session, string reason)
        {
            if (session == null) return;
            if (_initialized.ContainsKey(session.SessionID)) return;
            if (!_initializing.TryAdd(session.SessionID, true)) return;

            try
            {
                var cdp = GetOrCreateClient(session);
                Log.Info("开始初始化千牛CDP, reason=" + reason + ", sessionRef=" + LogRef("session", session.SessionID));
                var user = await cdp.GetCurrentUser();
                var ver = await cdp.GetVersion();
                if (user == null || user.Result == null || string.IsNullOrEmpty(user.Result.Nick))
                {
                    BotConnectionDiagnostics.RecordCdpStatus(false, "未获取登录用户", string.Empty, string.Empty);
                    Log.Error("千牛CDP初始化跳过：未获取到登录用户, sessionRef=" + LogRef("session", session.SessionID));
                    return;
                }

                var sellerNick = (user.Result.Nick ?? string.Empty).Trim();
                if (!TryClaimSellerSession(sellerNick, session.SessionID))
                {
                    _initialized[session.SessionID] = true;
                    Log.Info("重复千牛CDP会话已完成识别但不接管卖家运行通道: sellerRef="
                        + LogRef("seller", sellerNick) + ", sessionRef=" + LogRef("session", session.SessionID));
                    return;
                }

                QN qn = QN.GetByNick(user.Result);
                qn.QnVersion = ver != null ? ver.version : string.Empty;
                qn.CDP = cdp;
                _initialized[session.SessionID] = true;

                var buyerNick = string.Empty;
                try
                {
                    var conv = await cdp.GetCurrentConversationID();
                    if (conv != null && conv.Result != null && !string.IsNullOrWhiteSpace(conv.Result.Nick))
                    {
                        buyerNick = conv.Result.Nick;
                        qn.SetActiveConversationByNick(qn.Seller.Nick, buyerNick, "initConversation");
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("初始化时获取当前买家失败: " + ex.Message);
                }

                BotConnectionDiagnostics.RecordCdpStatus(true, "已获取", qn.Seller.Nick, buyerNick);
                WndNotifyIcon.Inst.AddSellerMenuItem(qn.Seller.Nick);
                Log.Info("千牛CDP初始化成功, sellerRef=" + LogRef("seller", qn.Seller.Nick)
                    + ", buyerRef=" + LogRef("buyer", buyerNick)
                    + ", version=" + qn.QnVersion + ", sessionRef=" + LogRef("session", session.SessionID));
            }
            catch (Exception ex)
            {
                BotConnectionDiagnostics.RecordCdpStatus(false, ex.Message, string.Empty, string.Empty);
                Log.Exception(ex);
            }
            finally
            {
                bool tmp;
                _initializing.TryRemove(session.SessionID, out tmp);
            }
        }

        public void Start()
        {
            try
            {
                var webSocket = new WebSocketServer();
                webSocket.NewSessionConnected += (session) =>
                {
                    try
                    {
                        _connectedSessions[session.SessionID] = true;
                        BotConnectionDiagnostics.RecordWebSocketConnect(session.SessionID);
                        Log.Info("千牛注入脚本已连接 Bot WebSocket: sessionRef=" + LogRef("session", session.SessionID));
                        GetOrCreateClient(session);
                    }
                    catch (Exception ex)
                    {
                        Log.Exception(ex);
                    }
                };
                webSocket.NewMessageReceived += (session, value) =>
                {
                    try
                    {
                        var wMsg = JsonConvert.DeserializeObject<WSocketMessage>(value);
                        if (wMsg == null || wMsg.Type == "hi") return;

                        Log.Info("收到千牛WebSocket事件: type=" + wMsg.Type);

                        if (wMsg.Type == "qnbotStatus")
                        {
                            try
                            {
                                var jo = JObject.Parse(wMsg.Response ?? "{}");
                                var hasLoginId = jo["hasLoginID"] != null && jo["hasLoginID"].Value<bool>();
                                var hasImsdk = jo["hasImsdk"] != null && jo["hasImsdk"].Value<bool>();
                                var hasQn = jo["hasQN"] != null && jo["hasQN"].Value<bool>();
                                var hasVs = jo["hasVs"] != null && jo["hasVs"].Value<bool>();
                                var loginNick = ReadJsonString(jo, "loginNick");
                                var conversationNick = ReadJsonString(jo, "conversationNick");
                                Log.Info("千牛注入状态: hasLoginID=" + hasLoginId
                                    + ", hasImsdk=" + hasImsdk
                                    + ", hasQN=" + hasQn
                                    + ", hasVs=" + hasVs
                                    + ", sellerRef=" + LogRef("seller", loginNick)
                                    + ", buyerRef=" + LogRef("buyer", conversationNick)
                                    + ", sessionRef=" + LogRef("session", session.SessionID));
                                BotConnectionDiagnostics.RecordInjectionStatus(true, hasImsdk, hasLoginId, hasQn, hasVs, wMsg.Response);
                                BotConnectionDiagnostics.RecordBuyerSeller(loginNick, conversationNick);
                                if (hasLoginId || hasImsdk)
                                {
                                    var authoritative = string.IsNullOrWhiteSpace(loginNick)
                                        || TryClaimSellerSession(loginNick, session.SessionID);
                                    if (!authoritative)
                                    {
                                        Log.Info("检测到卖家重复千牛WebSocket页面，保留已稳定的权威CDP会话: sellerRef="
                                            + LogRef("seller", loginNick)
                                            + ", ignoredSessionRef=" + LogRef("session", session.SessionID));
                                    }
                                    else if (!_initialized.ContainsKey(session.SessionID))
                                    {
                                        // Do not run TryInitSession and TryBindStatusConversation concurrently.
                                        // A single authoritative initialization already reads the current buyer.
                                        Task.Run(() => TryInitSession(session, "status"));
                                        ShouldRefreshStatusBinding(session.SessionID, loginNick, conversationNick);
                                    }
                                    else if (ShouldRefreshStatusBinding(session.SessionID, loginNick, conversationNick)
                                        && (!string.IsNullOrWhiteSpace(loginNick) || !string.IsNullOrWhiteSpace(conversationNick)))
                                    {
                                        Task.Run(() => TryBindStatusConversation(session, loginNick, conversationNick));
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                BotConnectionDiagnostics.RecordInjectionStatus(false, false, false, false, false, "解析注入状态失败：" + ex.Message);
                            }
                        }
                        else if (wMsg.Type == "imsdkApiScan")
                        {
                            Log.Info("IMSDK API扫描结果: " + PayloadSummary(wMsg.Response));
                        }
                        else if (wMsg.Type == "imsdkInvokeTrace")
                        {
                            Log.Info("IMSDK调用跟踪: " + PayloadSummary(wMsg.Response));
                        }
                        else if (wMsg.Type == "receiveNewMsg" || wMsg.Type == "onShopRobotReceriveNewMsgs" || wMsg.Type == "onChatDlgActive")
                        {
                            Task.Run(() => TryInitSession(session, "event:" + wMsg.Type));
                        }

                        if (OnRecieveMessage != null)
                            OnRecieveMessage(session, new WSocketNewMessageEventArgs(wMsg.Type, wMsg.Response));
                    }
                    catch (Exception ex)
                    {
                        Log.Exception(ex);
                    }
                };
                webSocket.SessionClosed += (session, value) =>
                {
                    _connectedSessions.TryRemove(session.SessionID, out _);
                    ReleaseSellerSession(session.SessionID);
                    BotConnectionDiagnostics.RecordWebSocketClose(session.SessionID);
                    Log.Info("千牛注入脚本 WebSocket 已断开: sessionRef=" + LogRef("session", session.SessionID)
                        + ", reason=" + value);
                    CDPClient removed;
                    bool b;
                    string statusBinding;
                    _clients.TryRemove(session.SessionID, out removed);
                    CDPClient.ReleaseClosedSession(session.SessionID, Convert.ToString(value), removed);
                    _initialized.TryRemove(session.SessionID, out b);
                    _initializing.TryRemove(session.SessionID, out b);
                    _lastStatusBindings.TryRemove(session.SessionID, out statusBinding);
                };
                var config = new ServerConfig()
                {
                    MaxRequestLength = 5 * 1024 * 1024,
                    Ip = "127.0.0.1",
                    Port = 41010
                };
                webSocket.Setup(config);
                webSocket.Start();
                BotConnectionDiagnostics.RecordWebSocketServerStarted();
                Log.Info("Bot WebSocket服务已启动: 127.0.0.1:41010");
            }
            catch (Exception ex)
            {
                BotConnectionDiagnostics.RecordWebSocketServerError(ex.Message);
                Log.Exception(ex);
            }
        }
    }
}