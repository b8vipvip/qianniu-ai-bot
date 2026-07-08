using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using BotLib;
using SuperWebSocket;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Bot.ChromeNs;
using Bot.Automation;
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

        static MyWebSocketServer()
        {
            if (WSocketSvrInst == null) WSocketSvrInst = new MyWebSocketServer();
        }

        public EventHandler<WSocketNewMessageEventArgs> OnRecieveMessage;

        private CDPClient GetOrCreateClient(WebSocketSession session)
        {
            return _clients.GetOrAdd(session.SessionID, id => new CDPClient(session));
        }

        private async Task TryInitSession(WebSocketSession session, string reason)
        {
            if (session == null) return;
            if (_initialized.ContainsKey(session.SessionID)) return;
            if (!_initializing.TryAdd(session.SessionID, true)) return;

            try
            {
                var cdp = GetOrCreateClient(session);
                Log.Info("开始初始化千牛CDP, reason=" + reason + ", session=" + session.SessionID);
                var user = await cdp.GetCurrentUser();
                var ver = await cdp.GetVersion();
                if (user == null || user.Result == null || string.IsNullOrEmpty(user.Result.Nick))
                {
                    BotConnectionDiagnostics.RecordCdpStatus(false, "未获取登录用户", string.Empty, string.Empty);
                    Log.Error("千牛CDP初始化跳过：未获取到登录用户, session=" + session.SessionID);
                    return;
                }

                QN qn = QN.GetByNick(user.Result);
                qn.QnVersion = ver != null ? ver.version : string.Empty;
                qn.CDP = cdp;
                _initialized[session.SessionID] = true;
                BotConnectionDiagnostics.RecordCdpStatus(true, "已获取", qn.Seller.Nick, string.Empty);
                WndNotifyIcon.Inst.AddSellerMenuItem(qn.Seller.Nick);
                Log.Info("千牛CDP初始化成功, seller=" + qn.Seller.Nick + ", version=" + qn.QnVersion + ", session=" + session.SessionID);
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
                        BotConnectionDiagnostics.RecordWebSocketConnect(session.SessionID);
                        Log.Info("千牛注入脚本已连接 Bot WebSocket: " + session.SessionID);
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
                            Log.Info("千牛注入状态: " + wMsg.Response);
                            try
                            {
                                var jo = JObject.Parse(wMsg.Response ?? "{}");
                                var hasLoginId = jo["hasLoginID"] != null && jo["hasLoginID"].Value<bool>();
                                var hasImsdk = jo["hasImsdk"] != null && jo["hasImsdk"].Value<bool>();
                                var hasQn = jo["hasQN"] != null && jo["hasQN"].Value<bool>();
                                var hasVs = jo["hasVs"] != null && jo["hasVs"].Value<bool>();
                                BotConnectionDiagnostics.RecordInjectionStatus(true, hasImsdk, hasLoginId, hasQn, hasVs, wMsg.Response);
                                if (hasLoginId || hasImsdk)
                                {
                                    Task.Run(() => TryInitSession(session, "status"));
                                }
                            }
                            catch (Exception ex)
                            {
                                BotConnectionDiagnostics.RecordInjectionStatus(false, false, false, false, false, "解析注入状态失败：" + ex.Message);
                            }
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
                    BotConnectionDiagnostics.RecordWebSocketClose(session.SessionID);
                    Log.Info("千牛注入脚本 WebSocket 已断开: " + session.SessionID + ", reason=" + value);
                    CDPClient removed;
                    bool b;
                    _clients.TryRemove(session.SessionID, out removed);
                    _initialized.TryRemove(session.SessionID, out b);
                    _initializing.TryRemove(session.SessionID, out b);
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

    public class ConnectionDiagnosticsSnapshot
    {
        public bool WebSocketServerStarted { get; set; }
        public int WebSocketSessionCount { get; set; }
        public string WebSocketStatus { get; set; }
        public string InjectionStatus { get; set; }
        public string QnParamStatus { get; set; }
        public string AccessibilityStatus { get; set; }
        public string ButtonStatus { get; set; }
        public string SendStatus { get; set; }
        public string Summary { get; set; }
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public DateTime LastUpdateTime { get; set; }
    }

    public static class BotConnectionDiagnostics
    {
        private static readonly object SyncObj = new object();
        private static bool wsStarted;
        private static int wsSessionCount;
        private static bool injectionConnected;
        private static bool hasImsdk;
        private static bool hasLoginId;
        private static bool hasQn;
        private static bool hasVs;
        private static bool cdpReady;
        private static string cdpMessage = "未获取";
        private static string seller = string.Empty;
        private static string buyer = string.Empty;
        private static bool uiAccessible;
        private static bool sendButtonFound;
        private static bool inputFound;
        private static string lastSendStatus = "未测试";
        private static string lastWsError = string.Empty;
        private static DateTime lastUpdate = DateTime.MinValue;

        public static void RecordWebSocketServerStarted()
        {
            lock (SyncObj)
            {
                wsStarted = true;
                lastWsError = string.Empty;
                lastUpdate = DateTime.Now;
            }
        }

        public static void RecordWebSocketServerError(string error)
        {
            lock (SyncObj)
            {
                wsStarted = false;
                lastWsError = error ?? string.Empty;
                lastUpdate = DateTime.Now;
            }
        }

        public static void RecordWebSocketConnect(string sessionId)
        {
            lock (SyncObj)
            {
                wsSessionCount++;
                injectionConnected = true;
                lastUpdate = DateTime.Now;
            }
        }

        public static void RecordWebSocketClose(string sessionId)
        {
            lock (SyncObj)
            {
                if (wsSessionCount > 0) wsSessionCount--;
                injectionConnected = wsSessionCount > 0;
                lastUpdate = DateTime.Now;
            }
        }

        public static void RecordInjectionStatus(bool connected, bool imsdk, bool loginId, bool qn, bool vs, string raw)
        {
            lock (SyncObj)
            {
                injectionConnected = connected;
                hasImsdk = imsdk;
                hasLoginId = loginId;
                hasQn = qn;
                hasVs = vs;
                lastUpdate = DateTime.Now;
            }
        }

        public static void RecordCdpStatus(bool ready, string message, string sellerNick, string buyerNick)
        {
            lock (SyncObj)
            {
                cdpReady = ready;
                cdpMessage = message ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(sellerNick)) seller = sellerNick;
                if (!string.IsNullOrWhiteSpace(buyerNick)) buyer = buyerNick;
                lastUpdate = DateTime.Now;
            }
        }

        public static void RecordBuyerSeller(string sellerNick, string buyerNick)
        {
            lock (SyncObj)
            {
                if (!string.IsNullOrWhiteSpace(sellerNick)) seller = sellerNick;
                if (!string.IsNullOrWhiteSpace(buyerNick)) buyer = buyerNick;
                lastUpdate = DateTime.Now;
            }
        }

        public static void RecordRpaScan(bool buttonFound, bool textInputFound, string note)
        {
            lock (SyncObj)
            {
                sendButtonFound = buttonFound;
                inputFound = textInputFound;
                uiAccessible = buttonFound && textInputFound;
                lastUpdate = DateTime.Now;
            }
        }

        public static void RecordSendAttempt(bool success, string message)
        {
            lock (SyncObj)
            {
                lastSendStatus = success ? "成功" : "失败";
                if (!string.IsNullOrWhiteSpace(message)) lastSendStatus += "：" + message;
                lastUpdate = DateTime.Now;
            }
        }

        public static ConnectionDiagnosticsSnapshot GetSnapshot()
        {
            lock (SyncObj)
            {
                var ws = wsStarted ? (wsSessionCount > 0 ? "已连接" + wsSessionCount + "个" : "已监听") : (string.IsNullOrWhiteSpace(lastWsError) ? "未启动" : "异常：" + lastWsError);
                var inject = injectionConnected ? "已连接" : "未连接";
                if (injectionConnected)
                {
                    inject += "｜imsdk=" + (hasImsdk ? "是" : "否") + "｜login=" + (hasLoginId ? "是" : "否");
                }
                var qnStatus = cdpReady ? "已获取" : cdpMessage;
                if (cdpReady && !string.IsNullOrWhiteSpace(seller)) qnStatus += "｜客服=" + seller;
                var access = uiAccessible ? "可用" : (inputFound || sendButtonFound ? "部分可用" : "未确认");
                var btn = sendButtonFound ? "已识别发送按钮" : "未识别发送按钮";
                if (!inputFound) btn += "｜输入框未识别";
                var allOk = wsStarted && injectionConnected && cdpReady && uiAccessible;
                return new ConnectionDiagnosticsSnapshot
                {
                    WebSocketServerStarted = wsStarted,
                    WebSocketSessionCount = wsSessionCount,
                    WebSocketStatus = ws,
                    InjectionStatus = inject,
                    QnParamStatus = qnStatus,
                    AccessibilityStatus = access,
                    ButtonStatus = btn,
                    SendStatus = lastSendStatus,
                    Summary = allOk ? "连接正常" : "检测中/需检查",
                    Seller = seller,
                    Buyer = buyer,
                    LastUpdateTime = lastUpdate
                };
            }
        }
    }

    public class WSocketMessage
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("response")]
        public string Response { get; set; }
    }

    public class WSocketNewMessageEventArgs : EventArgs
    {
        public string Type { get; private set; }
        public string Value { get; private set; }

        public WSocketNewMessageEventArgs(string type, string value)
        {
            Type = type;
            Value = value;
        }
    }
}