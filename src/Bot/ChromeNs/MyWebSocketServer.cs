using System;
using BotLib;
using SuperWebSocket;
using Newtonsoft.Json;
using Bot.ChromeNs;
using Bot.Automation;
using SuperSocket.SocketBase.Config;
using Bot.AssistWindow.NotifyIcon;

namespace Bot.ChromeNs
{
    public class MyWebSocketServer
    {
        public static MyWebSocketServer WSocketSvrInst = null;
        static MyWebSocketServer()
        {
            if (WSocketSvrInst == null) WSocketSvrInst = new MyWebSocketServer();
        }

        public EventHandler<WSocketNewMessageEventArgs> OnRecieveMessage;

        public void Start()
        {
            try
            {
                var webSocket = new WebSocketServer();
                webSocket.NewSessionConnected += async (session) =>
                {
                    try
                    {
                        Log.Info("千牛注入脚本已连接 Bot WebSocket: " + session.SessionID);
                        var cdp = new CDPClient(session);
                        var user = await cdp.GetCurrentUser();
                        var ver = await cdp.GetVersion();
                        QN qn = QN.GetByNick(user.Result);
                        qn.QnVersion = ver.version;
                        qn.CDP = cdp;
                        WndNotifyIcon.Inst.AddSellerMenuItem(qn.Seller.Nick);
                        Log.Info("千牛CDP初始化成功, seller=" + qn.Seller.Nick + ", version=" + qn.QnVersion);
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
                        if (wMsg.Type == "hi") return;

                        Log.Info("收到千牛WebSocket事件: type=" + wMsg.Type);
                        if (OnRecieveMessage != null)
                            OnRecieveMessage(session, new WSocketNewMessageEventArgs(wMsg.Type,wMsg.Response));
                    }
                    catch (Exception ex)
                    {
                        Log.Exception(ex);
                    }
                };
                webSocket.SessionClosed += (session, value) =>
                {
                    Log.Info("千牛注入脚本 WebSocket 已断开: " + session.SessionID + ", reason=" + value);
                };
                var config = new ServerConfig()
                {
                    MaxRequestLength = 5* 1024 * 1024,
                    Ip = "127.0.0.1",
                    Port = 41010
                };
                webSocket.Setup(config);//设置端口
                webSocket.Start();
                Log.Info("Bot WebSocket服务已启动: 127.0.0.1:41010");
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
            finally
            {

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