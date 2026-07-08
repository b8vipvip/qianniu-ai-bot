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

                qn.CDP = cdp;
                var sellerNick = qn.Seller == null ? loginNick : qn.Seller.Nick;
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
                Log.Info("千牛CDP初始化成功, seller=" + qn.Seller.Nick + ", buyer=" + buyerNick + ", version=" + qn.QnVersion + ", session=" + session.SessionID);
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
                                var loginNick = ReadJsonString(jo, "loginNick");
                                var conversationNick = ReadJsonString(jo, "conversationNick");
                                BotConnectionDiagnostics.RecordInjectionStatus(true, hasImsdk, hasLoginId, hasQn, hasVs, wMsg.Response);
                                BotConnectionDiagnostics.RecordBuyerSeller(loginNick, conversationNick);
                                if (hasLoginId || hasImsdk)
                                {
                                    Task.Run(() => TryInitSession(session, "status"));
                                    if (!string.IsNullOrWhiteSpace(loginNick) || !string.IsNullOrWhiteSpace(conversationNick))
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

        private static bool LastSendStatusIsOk()
        {
            if (string.IsNullOrWhiteSpace(lastSendStatus)) return true;
            if (lastSendStatus == "未测试") return true;
            return lastSendStatus.StartsWith("成功", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildSummary(bool wsOk, bool injectionOk, bool qnOk, bool sellerOk, bool uiOk, bool buttonOk, bool inputOk, bool sendOk)
        {
            if (wsOk && injectionOk && qnOk && sellerOk && uiOk && buttonOk && inputOk && sendOk) return "连接正常";
            if (!wsStarted) return string.IsNullOrWhiteSpace(lastWsError) ? "WS服务未启动" : "WS服务异常";
            if (!wsOk) return "WS未连接";
            if (!injectionOk) return "注入未连接";
            if (!qnOk) return "千牛参数未获取";
            if (!sellerOk) return "客服ID未识别";
            if (!uiOk || !buttonOk || !inputOk) return "无障碍/按钮未就绪";
            if (!sendOk) return "最近发送失败";
            return "检测中/需检查";
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
                var sellerOk = !string.IsNullOrWhiteSpace(seller);
                var qnStatus = cdpReady ? "已获取" : cdpMessage;
                if (cdpReady) qnStatus += sellerOk ? "｜客服=" + seller : "｜客服未识别";
                var access = uiAccessible ? "可用" : (inputFound || sendButtonFound ? "部分可用" : "未确认");
                var btn = sendButtonFound ? "已识别发送按钮" : "未识别发送按钮";
                if (!inputFound) btn += "｜输入框未识别";

                var wsOk = wsStarted && wsSessionCount > 0;
                var injectionOk = injectionConnected;
                var qnOk = cdpReady;
                var uiOk = uiAccessible;
                var buttonOk = sendButtonFound;
                var inputOk = inputFound;
                var sendOk = LastSendStatusIsOk();
                var summary = BuildSummary(wsOk, injectionOk, qnOk, sellerOk, uiOk, buttonOk, inputOk, sendOk);

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
                    Summary = summary,
                    Seller = seller,
                    Buyer = buyer,
                    LastUpdateTime = lastUpdate
                };
            }
        }
    }

    public static class QianniuRecoveryManager
    {
        private static readonly object SyncObj = new object();
        private static bool recovering;
        private static DateTime lastRecoverTime = DateTime.MinValue;

        public static void RequestRecover(string reason)
        {
            lock (SyncObj)
            {
                if (recovering) return;
                if ((DateTime.Now - lastRecoverTime).TotalMinutes < 3) return;
                recovering = true;
                lastRecoverTime = DateTime.Now;
            }

            Task.Run(async () =>
            {
                try
                {
                    await RecoverAsync(reason);
                }
                catch (Exception ex)
                {
                    Log.Exception(ex);
                }
                finally
                {
                    lock (SyncObj)
                    {
                        recovering = false;
                    }
                }
            });
        }

        private static async Task RecoverAsync(string reason)
        {
            var lastBuyer = string.Empty;
            try
            {
                var snapshot = BotConnectionDiagnostics.GetSnapshot();
                if (snapshot != null) lastBuyer = snapshot.Buyer;
            }
            catch
            {
            }

            Log.Error("触发千牛自动恢复：" + reason);
            KillQianniuProcesses();
            await Task.Delay(3000);

            var exe = FindLaunchFile();
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                Log.Error("千牛自动恢复失败：未找到 AliWorkbench.exe/wwcmd.exe");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(exe)
                {
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = true
                });
                Log.Info("已启动千牛：" + exe);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                return;
            }

            var started = await WaitForProcessAsync("AliWorkbench", 60);
            if (!started)
            {
                Log.Error("千牛自动恢复失败：启动后60秒内未检测到 AliWorkbench 进程");
                return;
            }

            Log.Info("千牛进程已启动，等待接待窗口和客服参数恢复...");
            await WaitForChatDeskAndOpenLastBuyerAsync(lastBuyer, 75);
        }

        private static void KillQianniuProcesses()
        {
            foreach (var name in new[] { "AliWorkbench", "AliRender", "wwcmd", "wangwang" })
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        p.Kill();
                    }
                    catch (Exception ex)
                    {
                        Log.Exception(ex);
                    }
                }
            }
        }

        private static async Task<bool> WaitForProcessAsync(string processName, int seconds)
        {
            var end = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < end)
            {
                try
                {
                    if (Process.GetProcessesByName(processName).Length > 0) return true;
                }
                catch
                {
                }
                await Task.Delay(1000);
            }
            return false;
        }

        private static async Task WaitForChatDeskAndOpenLastBuyerAsync(string lastBuyer, int seconds)
        {
            var end = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < end)
            {
                try
                {
                    if (Desk.Inst != null)
                    {
                        Desk.Inst.Show();
                        Desk.Inst.BringTop();
                    }

                    if (QN.CurQN != null && QN.CurQN.Seller != null && !string.IsNullOrWhiteSpace(QN.CurQN.Seller.Nick))
                    {
                        Log.Info("千牛自动恢复成功：客服=" + QN.CurQN.Seller.Nick);
                        if (!string.IsNullOrWhiteSpace(lastBuyer))
                        {
                            try
                            {
                                QN.CurQN.OpenChat(lastBuyer);
                                Log.Info("已尝试恢复打开最近买家会话：" + lastBuyer);
                            }
                            catch (Exception ex)
                            {
                                Log.Exception(ex);
                            }
                        }
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log.Exception(ex);
                }
                await Task.Delay(1000);
            }
            Log.Error("千牛已启动，但未能在限定时间内识别到接待窗口/客服ID。请确认千牛设置中已开启无障碍模式，并至少打开一次客服接待窗口。");
        }

        private static string FindLaunchFile()
        {
            var installPath = FindInstallPathFromRegistry();
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(installPath))
            {
                candidates.Add(Path.Combine(installPath, "AliWorkbench.exe"));
                candidates.Add(Path.Combine(installPath, "wwcmd.exe"));
            }
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AliWorkbench", "AliWorkbench.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "AliWorkbench", "AliWorkbench.exe"));

            foreach (var path in candidates)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
                }
                catch
                {
                }
            }
            return string.Empty;
        }

        private static string FindInstallPathFromRegistry()
        {
            try
            {
                using (var key = Registry.ClassesRoot.OpenSubKey(@"aliim\Shell\Open\Command"))
                {
                    if (key == null) return string.Empty;
                    var raw = (key.GetValue("") ?? string.Empty).ToString();
                    var exe = ExtractExePath(raw);
                    if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return string.Empty;
                    var dir = Directory.GetParent(exe);
                    if (dir == null) return string.Empty;
                    var parent = dir.Parent;
                    return parent == null ? dir.FullName : parent.FullName;
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                return string.Empty;
            }
        }

        private static string ExtractExePath(string command)
        {
            command = (command ?? string.Empty).Trim();
            if (command.Length < 1) return string.Empty;
            if (command.StartsWith("\""))
            {
                var end = command.IndexOf('"', 1);
                return end > 1 ? command.Substring(1, end - 1) : string.Empty;
            }
            var idx = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return idx >= 0 ? command.Substring(0, idx + 4).Trim() : command;
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