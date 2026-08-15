using BotLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using BotLib.Extensions;
using Bot.Automation.ChatDeskNs;
using Bot.Common;
using System.Reflection;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Web;
using DbEntity;
using Newtonsoft.Json;
using BotLib.Misc;
using DbEntity.Response;
using SuperWebSocket;
using Bot.ChatRecord;

namespace Bot.ChromeNs
{
    public class CDPClient
    {
        public event EventHandler<BuyerSwitchedEventArgs> EvBuyerSwitched;
        public event EventHandler<SellerSwitchedEventArgs> EvSellerSwitched;
        public event EventHandler<MessageNotifyEventArgs> EvMessageNotity;
        public event EventHandler<RecieveNewMessageEventArgs> EvRecieveNewMessage;
        public event EventHandler<ShopRobotReceriveNewMessageEventArgs> EvShopRobotReceriveNewMessage;
        private ConcurrentQueue<ManualResetEventSlim> _requestWaitHandles = new ConcurrentQueue<ManualResetEventSlim>();
        private ConcurrentQueue<string> _responses = new ConcurrentQueue<string>();
        private readonly SemaphoreSlim _executeGate = new SemaphoreSlim(1, 1);
        private WebSocketSession _webSocketSession;
        private const int InvokeTimeoutMs = 8000;
        private int _sessionInvalidated;
        public string Nick { get; set; }

        internal string SessionId
        {
            get { return _webSocketSession == null ? string.Empty : _webSocketSession.SessionID; }
        }

        internal bool IsInvalidated
        {
            get { return Volatile.Read(ref _sessionInvalidated) != 0; }
        }

        public CDPClient(WebSocketSession session)
        {
            _webSocketSession = session;
            MyWebSocketServer.WSocketSvrInst.OnRecieveMessage -= OnWSocketRecieveMessage;
            MyWebSocketServer.WSocketSvrInst.OnRecieveMessage += OnWSocketRecieveMessage;
        }

        private void OnWSocketRecieveMessage(object sender, WSocketNewMessageEventArgs e)
        {
            var session = sender as WebSocketSession;
            if (session == null || _webSocketSession == null || session.SessionID != _webSocketSession.SessionID) return;

            var response = e.Value ?? string.Empty;

            // execute 响应即使为空也必须唤醒等待线程，否则非聊天 WebView 上的初始化会永久卡住。
            if (e.Type == "execute")
            {
                if (_requestWaitHandles.Count > 0)
                {
                    _responses.Enqueue(response);
                    ManualResetEventSlim requestMre;
                    _requestWaitHandles.TryDequeue(out requestMre);
                    if (requestMre != null) requestMre.Set();
                }
                return;
            }

            DispatchInboundEvent(e.Type, response);
        }

        internal void DispatchInboundEvent(string type, string response)
        {
            if (string.IsNullOrEmpty(response)) return;
            if (type == "receiveNewMsg")
            {
                RecieveNewMessage(response);
            }
            else if (type == "onConversationChange")
            {
                BuyerSwitched(response);
            }
            else if (type == "onShopRobotReceriveNewMsgs")
            {
                ShopRobotReceriveNewMessage(response);
            }
            else if (type == "onChatDlgActive")
            {
                SellerSwitched(response);
            }
            else if (type == "messageCenterNotify")
            {
                BenchMessageNotify(response);
            }
            else if (type == "qnbotStatus")
            {
                Log.Info("千牛注入状态: " + response);
            }
        }

        private async Task<string> SendExecuteAndWaitAsync(string cmd, string desc)
        {
            await _executeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (IsInvalidated || _webSocketSession == null)
                {
                    Log.Info("CDP调用已跳过：会话已失效，等待注入脚本重连。desc=" + desc + ", session=" + SessionId);
                    return string.Empty;
                }

                // 当前注入协议的 execute 响应没有请求 ID，只能保证同一 WebSocket 会话同时存在
                // 一个等待请求。旧实现允许多个异步调用并发，慢请求与快请求乱序返回后会互相
                // 领取错误响应；某次超时还可能让后续 GetCurrentConversationID 永久读到旧结果。
                string staleResponse;
                while (_responses.TryDequeue(out staleResponse))
                {
                    Log.Info("已丢弃CDP调用前残留响应: desc=" + desc + ", length=" + (staleResponse ?? string.Empty).Length);
                }
                ManualResetEventSlim staleWaiter;
                while (_requestWaitHandles.TryDequeue(out staleWaiter))
                {
                    if (staleWaiter != null) staleWaiter.Set();
                }

                var requestResetEvent = new ManualResetEventSlim(false);
                _requestWaitHandles.Enqueue(requestResetEvent);
                try
                {
                    _webSocketSession.Send(JsonConvert.SerializeObject(new { method = "execute", expression = cmd }));
                }
                catch (Exception ex)
                {
                    ManualResetEventSlim droppedOnSend;
                    _requestWaitHandles.TryDequeue(out droppedOnSend);
                    InvalidateSession("发送execute请求失败: " + ex.Message);
                    return string.Empty;
                }

                var response = string.Empty;
                var ok = await System.Threading.Tasks.Task.Run(() => requestResetEvent.Wait(InvokeTimeoutMs)).ConfigureAwait(false);
                if (!ok)
                {
                    ManualResetEventSlim dropped;
                    _requestWaitHandles.TryDequeue(out dropped);
                    Log.Error("CDP调用超时: " + desc + ", session=" + SessionId);
                    InvalidateSession("调用超时: " + desc);
                    return string.Empty;
                }
                _responses.TryDequeue(out response);
                return response ?? string.Empty;
            }
            finally
            {
                _executeGate.Release();
            }
        }

        internal Task<string> EvaluateExpressionAsync(string expression, string description)
        {
            if (string.IsNullOrWhiteSpace(expression)) return Task.FromResult(string.Empty);
            return SendExecuteAndWaitAsync(
                expression,
                string.IsNullOrWhiteSpace(description) ? "EvaluateExpression" : description);
        }

        private string SendExecuteAndWait(string cmd, string desc)
        {
            try
            {
                return SendExecuteAndWaitAsync(cmd, desc).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Info("CDP同步调用失败: desc=" + desc + ", error=" + ex.Message);
                return string.Empty;
            }
        }

        private void InvalidateSession(string reason)
        {
            if (Interlocked.Exchange(ref _sessionInvalidated, 1) != 0) return;
            Log.Error("CDP会话已失效并请求WebSocket重连: session=" + SessionId + ", reason=" + (reason ?? string.Empty));
            BotConnectionDiagnostics.RecordCdpStatus(false, "CDP会话已失效，等待注入重连", Nick, string.Empty);
            try
            {
                if (_webSocketSession != null) _webSocketSession.Close();
            }
            catch (Exception ex)
            {
                Log.Info("关闭失效CDP WebSocket会话失败: " + ex.Message);
            }
        }

        private void BenchMessageNotify(string response)
        {
            if (EvMessageNotity != null)
            {
                EvMessageNotity(this, new MessageNotifyEventArgs
                {
                    NotifyContent = response
                });
            }
        }
        private void ShopRobotReceriveNewMessage(string response)
        {
            var localUser = JsonConvert.DeserializeObject<ActiveLocalUser>(response);
            if (EvShopRobotReceriveNewMessage != null)
            {
                EvShopRobotReceriveNewMessage(this, new ShopRobotReceriveNewMessageEventArgs
                {
                    Buyer = localUser.Conversation,
                    Seller = localUser.LoginID
                });
            }
        }

        private void RecieveNewMessage(string msg)
        {
            if (EvRecieveNewMessage != null)
            {
                EvRecieveNewMessage(this, new RecieveNewMessageEventArgs
                {
                    Buyer = string.Empty,
                    Message = msg
                });
            }
        }

        private void BuyerSwitched(string response)
        {
            var localUser = JsonConvert.DeserializeObject<ActiveLocalUser>(response);

            if (EvBuyerSwitched != null)
            {
                EvBuyerSwitched(this, new BuyerSwitchedEventArgs
                {
                    Seller = localUser.LoginID,
                    Buyer = localUser.Conversation
                });
            }
        }

        private void SellerSwitched(string response)
        {
            var localUser = JsonConvert.DeserializeObject<ActiveLocalUser>(response);

            if (EvSellerSwitched != null)
            {
                EvSellerSwitched(this, new SellerSwitchedEventArgs
                {
                    Seller = localUser.LoginID,
                    Buyer = localUser.Conversation
                });
            }
        }

        public async Task<T> InvokeMTop<T>(string apiName, object param = null, string version = "1.0")
        {
            param = param ?? new object();
            var cmd = $@"
                imsdk.invoke('application.invokeMTopChannelService', 
                {{
                  method: '{apiName}',
                  param: {JsonConvert.SerializeObject(param)},
                  httpMethod: 'post',
                  version: '{version}',
                }})";
            var response = await SendExecuteAndWaitAsync(cmd, "InvokeMTop:" + apiName);
            if (string.IsNullOrEmpty(response)) return default(T);
            return JsonConvert.DeserializeObject<T>(response);
        }

        public void InvokeMTop(string apiName, object param = null, string version = "1.0")
        {
            param = param ?? new object();
            var cmd = $@"
                imsdk.invoke('application.invokeMTopChannelService', 
                {{
                  method: '{apiName}',
                  param: {JsonConvert.SerializeObject(param)},
                  httpMethod: 'post',
                  version: '{version}',
                }})";
            SendExecuteAndWait(cmd, "InvokeMTop:" + apiName);
        }

        public async Task<T> Invoke<T>(string apiName, object param = null)
        {
            param = param ?? new object();
            var cmd = $@"imsdk.invoke('{apiName}',{JsonConvert.SerializeObject(param)})";
            var response = await SendExecuteAndWaitAsync(cmd, "Invoke:" + apiName);
            if (string.IsNullOrEmpty(response)) return default(T);
            return JsonConvert.DeserializeObject<T>(response);
        }

        public void Invoke(string apiName, object param = null)
        {
            param = param ?? new object();
            var cmd = $@"imsdk.invoke('{apiName}',{JsonConvert.SerializeObject(param)})";
            SendExecuteAndWait(cmd, "Invoke:" + apiName);
        }

        public async Task<QnVersionResponse> GetVersion()
        {
            return await Invoke<QnVersionResponse>("application.getVersion");
        }

        public void SendTimiMsg(string userId, string smartTip)
        {
            Invoke("intelligentservice.SendSmartTipMsg",
                new
                {
                    userId,
                    smartTip
                });
        }

        public void TransferContact(string contactID, string targetID, string reason = "")
        {
            Invoke("application.transferContact",
                new
                {
                    contactID,
                    targetID,
                    reason
                });
        }

        public void LightOff(string ccode)
        {
            Invoke("im.singlemsg.SetConversationRead",
                new List<object> {
                    new
                    {
                        cid = new
                        {
                            ccode,
                            bizeType="11001"
                        }
                    }
                });
        }

        public void MarkRead(string ccode, string clientId, string messageId)
        {
            Invoke("im.singlemsg.SetFlagsPeerMsgReaded",
                new
                {
                    cid = new
                    {
                        ccode
                    },
                    mcodes = new List<object> { new { clientId, messageId } }
                });
        }

        public async Task<LocalUserResponse> GetCurrentUser()
        {
            var user = await Invoke<LocalUserResponse>("im.login.GetCurrentLoginID");
            if (user == null || user.Result == null || string.IsNullOrEmpty(user.Result.Nick))
            {
                throw new Exception("GetCurrentLoginID 返回为空，可能不是千牛聊天 WebView。session=" + SessionId);
            }
            Nick = user.Result.Nick;
            return user;
        }

        public void InsertText2Inputbox(string uid, string text)
        {
            Invoke("application.insertText2Inputbox", new { uid = "cntaobao" + uid, text });
        }

        public async Task<bool> IsInputboxEmpty()
        {
            var inputboxEmpty = await Invoke<InputboxEmptyResponse>("application.isInputboxEmpty");
            return inputboxEmpty != null && inputboxEmpty.isEmpty;
        }

        public void BrowserUrl(string url)
        {
            Invoke("application.browserUrl", new { url });
        }

        public void SendRemindPayCard(string encryptedBuyerId, string orderId)
        {
            InvokeMTop("mtop.taobao.customer.service.remind.pay.manual", new { encryptedBuyerId, orderId });
        }

        public void RecallMessage(string ccode, string clientId, string messageId)
        {
            Invoke("im.singlemsg.DoChatMsgWithdraw",
                new
                {
                    cid = new
                    {
                        ccode
                    },
                    mcodes = new List<object> { new { clientId, messageId } }
                });
        }

        public void OpenChat(string nick)
        {
            Invoke("application.openChat", new { nick = "cntaobao" + nick });
        }

        public void SendCoupon(string buyerNick, string activityId)
        {
            InvokeMTop("mtop.taobao.qianniu.airisland.coupon.send.card", new { activityId, buyerNick });
        }

        public void CloseChat(string contactID)
        {
            Invoke("application.closeChat", new { contactID });
        }

        public void GetRemoteHisMsg(string ccode)
        {
            Invoke("im.singlemsg.GetRemoteHisMsg", new
            {
                cid = new
                {
                    ccode,
                    type = 1
                },
                count = 100,
                gohistory = 1,
                msgid = "-1",
                msgtime = "-1",
            });
        }

        public async Task<AccountStatusResponse> GetAccountStatus()
        {
            return await InvokeMTop<AccountStatusResponse>("mtop.taobao.qianniu.cloudkefu.accountstatus.getbyid");
        }

        public async Task<ItemRecordResponse> GetItemRecords(string encryptId)
        {
            return await InvokeMTop<ItemRecordResponse>("mtop.taobao.qianniu.cs.item.record.query", new { encryptId });
        }

        public async Task<SearchUserResponse> SearchBuyerUser(string searchQuery)
        {
            return await InvokeMTop<SearchUserResponse>("mtop.taobao.qianniu.airisland.contact.search",
                new
                {
                    accessKey = "qianniu-pc",
                    accessSecret = "qianniu-pc-secret",
                    accountType = 3,
                    searchQuery
                });
        }

        public async Task<BuyerInfoResponse> GetBuyerInfo(string encryptId)
        {
            return await InvokeMTop<BuyerInfoResponse>("mtop.taobao.qianniu.cs.user.query", new { encryptId });
        }

        public async Task<ZnkfTradeQueryResponse> GetBuyerTrades(string securityBuyerUid, string bizOrderId)
        {
            return await InvokeMTop<ZnkfTradeQueryResponse>("mtop.taobao.qianniu.cs.trade.query", new
            {
                securityBuyerUid,
                bizOrderId
            });
        }

        public async Task<ConversationResponse> GetCurrentConversationID()
        {
            return await Invoke<ConversationResponse>("im.uiutil.GetCurrentConversationID");
        }
    }
}
