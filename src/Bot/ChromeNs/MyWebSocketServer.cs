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
                    Log.Error("千牛CDP初始化跳过：未获取到登录用户, session=" + session.SessionID);
                    return;
                }

                QN qn = QN.GetByNick(user.Result);
                qn.QnVersion = ver != null ? ver.version : string.Empty;
                qn.CDP = cdp;
                _initialized[session.SessionID] = true;
                WndNotifyIcon.Inst.AddSellerMenuItem(qn.Seller.Nick);
                Log.Info("千牛CDP初始化成功, seller=" + qn.Seller.Nick + ", version=" + qn.QnVersion + ", session=" + session.SessionID);
            }
            catch (Exception ex)
            {
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
                        Log.Info("千牛注入脚本已连接 Bot WebSocket: " + session.SessionID);
                        GetOrCreateClient(session);
                        // 不在连接时立即初始化。千牛会有很多普通 WebView，例如 dx-h5，里面没有 imsdk/_vs。
                        // 等 qnbotStatus 报告 hasImsdk/hasLoginID 后再初始化，避免一直卡在“正在接待...”。
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
                                if (hasLoginId || hasImsdk)
                                {
                                    Task.Run(() => TryInitSession(session, "status"));
                                }
                            }
                            catch
                            {
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
                Log.Info("Bot WebSocket服务已启动: 127.0.0.1:41010");
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
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
