using Bot.ChromeNs;
using DbEntity.Response;
using DbEntity;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bot.Automation.ChatDeskNs;
using Bot.ChatRecord;
using Newtonsoft.Json;
using BotLib;

namespace Bot.ChromeNs
{
    public class QN
    {
        public event EventHandler<BuyerSwitchedEventArgs> EvBuyerSwitched;
        public event EventHandler<SellerSwitchedEventArgs> EvSellerSwitched;
        public event EventHandler<MessageNotifyEventArgs> EvMessageNotity;
        public event EventHandler<RecieveNewMessageEventArgs> EvRecieveNewMessage;
        public event EventHandler<ShopRobotReceriveNewMessageEventArgs> EvShopRobotReceriveNewMessage;
        public static HashSet<QN> QNSet { get; set; }
        private static readonly object QNSetLock = new object();
        public string QnVersion { get; set; }

        private CDPClient cdp;
        public CDPClient CDP
        {
            get { return cdp; }
            set
            {
                // 千牛新版会同时打开多个 recent.html / iframe，多个 WebSocket session 会重复初始化。
                // 旧代码先把 cdp 字段替换成新对象，再 -= 事件，导致旧 CDP 事件没有真正解绑，
                // 后续同一个买家第二条消息可能由旧 session 触发、却用新 session 发送，表现为 Bot 右侧有答案但千牛未发送。
                if (cdp != null)
                {
                    cdp.EvBuyerSwitched -= Cdp_EvBuyerSwitched;
                    cdp.EvMessageNotity -= Cdp_EvMessageNotity;
                    cdp.EvRecieveNewMessage -= Cdp_EvRecieveNewMessage;
                    cdp.EvSellerSwitched -= Cdp_EvSellerSwitched;
                    cdp.EvShopRobotReceriveNewMessage -= Cdp_EvShopRobotReceriveNewMessage;
                }

                cdp = value;
                if (cdp == null) return;

                cdp.EvBuyerSwitched -= Cdp_EvBuyerSwitched;
                cdp.EvMessageNotity -= Cdp_EvMessageNotity;
                cdp.EvRecieveNewMessage -= Cdp_EvRecieveNewMessage;
                cdp.EvSellerSwitched -= Cdp_EvSellerSwitched;
                cdp.EvShopRobotReceriveNewMessage -= Cdp_EvShopRobotReceriveNewMessage;

                cdp.EvBuyerSwitched += Cdp_EvBuyerSwitched;
                cdp.EvMessageNotity += Cdp_EvMessageNotity;
                cdp.EvRecieveNewMessage += Cdp_EvRecieveNewMessage;
                cdp.EvSellerSwitched += Cdp_EvSellerSwitched;
                cdp.EvShopRobotReceriveNewMessage += Cdp_EvShopRobotReceriveNewMessage;
            }
        }

        private LocalUser _seller;
        public LocalUser Seller
        {
            get { return _seller; }
            set { _seller = value; }
        }

        public QNRpa rpa;
        public QNRpa Rpa { get { return rpa; } }
        public Conversation Buyer { get; set; }

        private readonly object _sellerEchoLock = new object();
        private string _lastSellerEchoBuyer = string.Empty;
        private string _lastSellerEchoText = string.Empty;
        private DateTime _lastSellerEchoTime = DateTime.MinValue;
        private readonly SemaphoreSlim _incomingMessageGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
        private readonly IncomingMessageDeduplicator _incomingMessageDeduplicator = new IncomingMessageDeduplicator(2000);
        private readonly DateTime _messageSafetyStartedAt = DateTime.Now;
        private readonly VisionRequestService _visionRequestService = new VisionRequestService();

        public static QN CurQN = null;

        static QN()
        {
            QNSet = new HashSet<QN>();
        }

        public QN(LocalUser seller)
        {
            this._seller = seller;
            this.rpa = new QNRpa(this);
        }

        public async Task<bool> SendTextAsync(string buyer, string text)
        {
            var comparison = String.Compare(QnVersion, "9.19.06N", StringComparison.OrdinalIgnoreCase);
            if (comparison < 0)
            {
                SendTimiMsg(buyer, text);
                BotConnectionDiagnostics.RecordSendAttempt(true, "旧版接口发送");
                return true;
            }
            else
            {
                return await rpa.SendTextAsync(buyer, text);
            }
        }

        public async Task<bool> SendTextWithRetryAsync(string buyer, string text, int retryCount = 1)
        {
            await _sendGate.WaitAsync();
            try
            {
                if (!await EnsureActiveBuyerForSendAsync(buyer))
                {
                    BotConnectionDiagnostics.RecordSendAttempt(false, "无法确认目标买家会话");
                    return false;
                }

                var ok = await SendTextAsync(buyer, text);
                var retry = Math.Max(0, retryCount);
                for (var i = 0; !ok && i < retry; i++)
                {
                    Log.Info("自动发送失败，准备重试第" + (i + 1) + "次。buyer=" + buyer + ", text=" + text);
                    await Task.Delay(1800);
                    if (!await EnsureActiveBuyerForSendAsync(buyer)) return false;
                    ok = await SendTextAsync(buyer, text);
                }
                return ok;
            }
            finally
            {
                _sendGate.Release();
            }
        }

        private async Task<bool> EnsureActiveBuyerForSendAsync(string buyer)
        {
            buyer = (buyer ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(buyer) || cdp == null) return false;

            for (var attempt = 0; attempt < 22; attempt++)
            {
                try
                {
                    var current = await GetCurrentConversationID();
                    var currentNick = current == null || current.Result == null ? string.Empty : (current.Result.Nick ?? string.Empty).Trim();
                    if (currentNick == buyer)
                    {
                        SetActiveConversationByNick(Seller == null ? string.Empty : Seller.Nick, buyer, "sendVerified");
                        return true;
                    }
                    if (attempt == 0)
                    {
                        Log.Info("发送前切换目标买家: target=" + buyer + ", current=" + currentNick);
                        OpenChat(buyer);
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("发送前确认买家会话失败: " + ex.Message);
                    if (attempt == 0) OpenChat(buyer);
                }
                await Task.Delay(250);
            }

            Log.Error("发送已阻止：无法确认当前会话为目标买家。target=" + buyer);
            return false;
        }

        public async void SendImageAsync(string buyer, string imagePath)
        {
            await rpa.SendImageAsync(buyer, imagePath);
        }

        private static string GetMessageText(QNChatMessage m)
        {
            if (m == null) return string.Empty;
            try
            {
                if (m.originalData != null)
                {
                    var text = m.originalData.text ?? string.Empty;
                    if (m.originalData.header != null)
                    {
                        text += m.originalData.header.summary ?? string.Empty;
                    }
                    if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                }
            }
            catch
            {
            }
            return (m.summary ?? string.Empty).Trim();
        }

        private static string NormalizeMessageText(string value)
        {
            return (value ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
        }

        private void RecordSellerEcho(string buyerNick, string text)
        {
            buyerNick = (buyerNick ?? string.Empty).Trim();
            text = NormalizeMessageText(text);
            if (string.IsNullOrWhiteSpace(buyerNick) || string.IsNullOrWhiteSpace(text)) return;

            lock (_sellerEchoLock)
            {
                _lastSellerEchoBuyer = buyerNick;
                _lastSellerEchoText = text;
                _lastSellerEchoTime = DateTime.Now;
            }
            Log.Info("已记录卖家消息回显: buyer=" + buyerNick + ", text=" + text);
        }

        public bool HasRecentSellerEcho(string buyerNick, string text, DateTime since)
        {
            buyerNick = (buyerNick ?? string.Empty).Trim();
            text = NormalizeMessageText(text);
            if (string.IsNullOrWhiteSpace(buyerNick) || string.IsNullOrWhiteSpace(text)) return false;

            lock (_sellerEchoLock)
            {
                if (_lastSellerEchoTime < since.AddMilliseconds(-500)) return false;
                if (_lastSellerEchoBuyer != buyerNick) return false;
                return _lastSellerEchoText == text;
            }
        }

        private bool IsBuyerMessage(QNChatMessage m)
        {
            return m != null && m.fromid != null && m.toid != null && _seller != null
                && m.fromid.nick != _seller.Nick && m.toid.nick == _seller.Nick;
        }

        private bool IsSellerMessage(QNChatMessage m)
        {
            return m != null && m.fromid != null && m.toid != null && _seller != null
                && m.fromid.nick == _seller.Nick && m.toid.nick != _seller.Nick;
        }

        public void SetActiveConversationByNick(string sellerNick, string buyerNick, string source)
        {
            try
            {
                sellerNick = (sellerNick ?? string.Empty).Trim();
                buyerNick = (buyerNick ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(sellerNick) && string.IsNullOrWhiteSpace(buyerNick)) return;

                if (!string.IsNullOrWhiteSpace(sellerNick) && (_seller == null || _seller.Nick != sellerNick))
                {
                    _seller = new LocalUser { Nick = sellerNick, Display = sellerNick };
                }

                if (!string.IsNullOrWhiteSpace(buyerNick) && (Buyer == null || Buyer.Nick != buyerNick))
                {
                    Buyer = new Conversation { Nick = buyerNick, Display = buyerNick };
                }

                CurQN = this;
                BotConnectionDiagnostics.RecordBuyerSeller(_seller == null ? sellerNick : _seller.Nick, Buyer == null ? buyerNick : Buyer.Nick);

                try
                {
                    if (Desk.Inst != null)
                    {
                        if (_seller != null && !string.IsNullOrWhiteSpace(_seller.Nick)) Desk.Inst.ChangeSeller(_seller.Nick);
                        if (Buyer != null && !string.IsNullOrWhiteSpace(Buyer.Nick)) Desk.Inst.ChangeBuyer(Buyer.Nick);
                    }
                }
                catch (Exception ex)
                {
                    Log.Exception(ex);
                }

                Log.Info("当前会话已更新: source=" + source + ", seller=" + (_seller == null ? string.Empty : _seller.Nick) + ", buyer=" + (Buyer == null ? string.Empty : Buyer.Nick));
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }

        private void Cdp_EvShopRobotReceriveNewMessage(object sender, ShopRobotReceriveNewMessageEventArgs e)
        {
            // 这是后台新消息通知，不等于千牛当前可见聊天已经切换。
            // 绝不能在这里修改 QN.Buyer 或提前打开聊天，否则后台买家的答案可能发到当前可见买家。
            if (e != null && e.Seller != null && e.Buyer != null)
            {
                Log.Info("收到后台买家消息通知: seller=" + e.Seller.Nick + ", buyer=" + e.Buyer.Nick);
            }
            if (EvShopRobotReceriveNewMessage != null)
            {
                EvShopRobotReceriveNewMessage(this, e);
            }
        }

        private void Cdp_EvSellerSwitched(object sender, SellerSwitchedEventArgs e)
        {
            if (e == null) return;
            Seller = e.Seller;
            Buyer = e.Buyer;
            CurQN = this;
            SetActiveConversationByNick(e.Seller == null ? string.Empty : e.Seller.Nick, e.Buyer == null ? string.Empty : e.Buyer.Nick, "sellerSwitched");

            if (EvSellerSwitched != null)
            {
                EvSellerSwitched(this, e);
            }
        }

        private async Task ProcessIncomingMessageAsync(QNChatMessage message)
        {
            if (message == null) return;
            var messageText = GetMessageText(message);
            var messageKey = IncomingMessageSafety.BuildMessageKey(message, messageText);
            if (!_incomingMessageDeduplicator.TryAccept(messageKey))
            {
                Log.Info("重复消息已跳过: key=" + messageKey);
                return;
            }

            if (IsSellerMessage(message))
            {
                RecordSellerEcho(message.toid.nick, messageText);
                return;
            }
            if (!IsBuyerMessage(message)) return;

            var sellerNick = message.toid.nick;
            var buyerNick = message.fromid.nick;
            var decision = IncomingMessageSafety.Evaluate(message, messageText, _messageSafetyStartedAt);
            var displayQuestion = string.IsNullOrWhiteSpace(messageText) ? decision.MessageLabel : messageText;

            var visionDecision = VisionMessageDecision.Decide(message, messageText, decision, AiEndpointStore.GetVisionEnabledEndpoints());

            if (visionDecision.Kind == VisionDecisionKind.Skip)
            {
                AddSkippedConversation(sellerNick, buyerNick, visionDecision.QuestionLabel, visionDecision.Note);
                Log.Info("买家消息安全跳过: buyer=" + buyerNick + ", reason=" + visionDecision.Note);
                return;
            }

            var botEnabled = Params.Robot.CanUseRobotReal;
            var autoSend = Params.Robot.GetIsAutoReply();
            if (!botEnabled)
            {
                AddSkippedConversation(sellerNick, buyerNick, displayQuestion, "Bot已停用，未调用AI，也未发送给买家。");
                return;
            }

            if (visionDecision.Kind == VisionDecisionKind.Vision)
            {
                await ProcessVisionMessageAsync(new VisionReplyTask { SellerNick = sellerNick, BuyerNick = buyerNick, MessageKey = messageKey, Message = message });
                return;
            }

            var answer = MyOpenAI.GetAnswer(sellerNick, buyerNick, messageText);
            var conversationCtl = Desk.Inst == null ? null : Desk.Inst.AddConversation(sellerNick, buyerNick, messageText, answer, autoSend);
            if (!autoSend) return;

            if (string.IsNullOrWhiteSpace(answer) || answer.StartsWith("错误："))
            {
                if (conversationCtl != null) conversationCtl.SetSendResult(false, "未发送：AI错误");
                return;
            }

            var sendOk = await SendTextWithRetryAsync(buyerNick, answer, 1);
            if (conversationCtl != null)
            {
                conversationCtl.SetSendResult(sendOk, sendOk ? "已发送" : "发送失败：目标买家会话未确认或发送未完成");
            }
        }

        private async Task ProcessVisionMessageAsync(VisionReplyTask task)
        {
            var autoSend = Params.Robot.GetIsAutoReply();
            var ctl = Desk.Inst == null ? null : Desk.Inst.AddConversation(task.SellerNick, task.BuyerNick, "[图片]", "正在识别图片...", autoSend);
            var result = await _visionRequestService.ExecuteAsync(task, CancellationToken.None);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Answer))
            {
                var note = "已跳过：" + (string.IsNullOrWhiteSpace(result.Error) ? "视觉识别失败" : result.Error) + "，未向买家发送消息。";
                if (ctl != null) { ctl.SetAnswer("未生成答案。"); ctl.SetSkipped(note); } else AddSkippedConversation(task.SellerNick, task.BuyerNick, "[图片]", note);
                Log.Info("视觉消息跳过: seller=" + task.SellerNick + ", buyer=" + task.BuyerNick + ", messageId=" + task.MessageKey + ", endpoint=" + result.EndpointName + ", model=" + result.VisionModel + ", latencyMs=" + result.LatencyMs + ", reason=" + result.Error);
                return;
            }
            if (ctl != null) ctl.SetAnswer(result.Answer);
            if (!autoSend) return;
            var sendOk = await SendTextWithRetryAsync(task.BuyerNick, result.Answer, 1);
            if (ctl != null) ctl.SetSendResult(sendOk, sendOk ? "已发送" : "识别完成，但目标买家会话未确认，未发送。");
        }

        private void AddSkippedConversation(string seller, string buyer, string question, string note)
        {
            if (Desk.Inst == null) return;
            var ctl = Desk.Inst.AddConversation(seller, buyer, question, note, false);
            if (ctl != null) ctl.SetSkipped(note);
        }

        private async void Cdp_EvRecieveNewMessage(object sender, RecieveNewMessageEventArgs e)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.Message)) return;
            await _incomingMessageGate.WaitAsync();
            try
            {
                if (EvRecieveNewMessage != null)
                {
                    EvRecieveNewMessage(this, e);
                }

                Log.Info("收到千牛新消息事件: " + e.Message);
                var chatRes = JsonConvert.DeserializeObject<ChatResponse>(e.Message);
                if (chatRes == null || chatRes.result == null)
                {
                    Log.Error("收到新消息但无法解析: " + e.Message);
                    return;
                }

                var messages = chatRes.result
                    .Where(m => m != null)
                    .OrderBy(IncomingMessageSafety.GetSortValue)
                    .ToList();

                // GetNewMsg 有时一次返回同一买家的多条未读消息。只处理该批次最新一条，避免启动或网络恢复时连续轰炸买家。
                var latestBuyerMessages = messages
                    .Where(IsBuyerMessage)
                    .GroupBy(m => (m.toid == null ? string.Empty : m.toid.nick) + "#" + (m.fromid == null ? string.Empty : m.fromid.nick))
                    .ToDictionary(g => g.Key, g => g.Last());

                foreach (var message in messages)
                {
                    if (IsBuyerMessage(message))
                    {
                        var buyerKey = message.toid.nick + "#" + message.fromid.nick;
                        QNChatMessage latest;
                        if (latestBuyerMessages.TryGetValue(buyerKey, out latest) && !object.ReferenceEquals(message, latest))
                        {
                            var oldKey = IncomingMessageSafety.BuildMessageKey(message, GetMessageText(message));
                            _incomingMessageDeduplicator.TryAccept(oldKey);
                            Log.Info("同批次较早买家消息已合并跳过: buyer=" + message.fromid.nick + ", key=" + oldKey);
                            continue;
                        }
                    }

                    await ProcessIncomingMessageAsync(message);
                    await Task.Delay(250);
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
            finally
            {
                _incomingMessageGate.Release();
            }
        }

        private void Cdp_EvMessageNotity(object sender, MessageNotifyEventArgs e)
        {
            if (EvMessageNotity != null)
            {
                EvMessageNotity(this, e);
            }
        }

        private void Cdp_EvBuyerSwitched(object sender, BuyerSwitchedEventArgs e)
        {
            if (e == null) return;
            Seller = e.Seller;
            Buyer = e.Buyer;
            CurQN = this;
            SetActiveConversationByNick(e.Seller == null ? string.Empty : e.Seller.Nick, e.Buyer == null ? string.Empty : e.Buyer.Nick, "buyerSwitched");
            if (EvBuyerSwitched != null)
            {
                EvBuyerSwitched(this, e);
            }
        }

        public static QN GetByNick(LocalUser seller)
        {
            if (seller == null) throw new ArgumentNullException("seller");
            lock (QNSetLock)
            {
                var qn = QNSet.FirstOrDefault(q => q._seller != null && (q._seller.Nick == seller.Nick || q._seller.Display == seller.Display));
                if (qn == null)
                {
                    qn = new QN(seller);
                    QNSet.Add(qn);
                }
                return qn;
            }
        }

        public static QN FindExistingBySellerNick(string sellerNick)
        {
            if (string.IsNullOrWhiteSpace(sellerNick)) return null;
            lock (QNSetLock)
            {
                return QNSet.FirstOrDefault(q => q._seller != null && (q._seller.Nick == sellerNick || q._seller.Display == sellerNick));
            }
        }

        public void SendTimiMsg(string userId, string smartTip)
        {
            cdp.SendTimiMsg(userId, smartTip);
        }

        public void TransferContact(string contactID, string targetID, string reason = "")
        {
            cdp.TransferContact(contactID, targetID, reason);
        }

        public void LightOff(string ccode)
        {
            cdp.LightOff(ccode);
        }

        public void MarkRead(string ccode, string clientId, string messageId)
        {
            cdp.MarkRead(ccode, clientId, messageId);
        }

        public async Task<LocalUserResponse> GetCurrentUser()
        {
            return await cdp.GetCurrentUser();
        }

        public void InsertText2Inputbox(string uid, string text)
        {
            cdp.InsertText2Inputbox(uid, text);
        }

        public async Task<bool> IsInputboxEmpty()
        {
            return await cdp.IsInputboxEmpty();
        }

        public void BrowserUrl(string url)
        {
            cdp.BrowserUrl(url);
        }

        public void SendRemindPayCard(string encryptedBuyerId, string orderId)
        {
            cdp.SendRemindPayCard(encryptedBuyerId, orderId);
        }

        public void RecallMessage(string ccode, string clientId, string messageId)
        {
            cdp.RecallMessage(ccode, clientId, messageId);
        }

        public void OpenChat(string nick)
        {
            cdp.OpenChat(nick);
        }

        public void SendCoupon(string buyerNick, string activityId)
        {
            cdp.SendCoupon(buyerNick, activityId);
        }

        public void CloseChat(string contactID)
        {
            cdp.CloseChat(contactID);
        }

        public void GetRemoteHisMsg(string ccode)
        {
            cdp.GetRemoteHisMsg(ccode);
        }

        public async Task<AccountStatusResponse> GetAccountStatus()
        {
            return await cdp.GetAccountStatus();
        }

        public async Task<ItemRecordResponse> GetItemRecords(string encryptId)
        {
            return await cdp.GetItemRecords(encryptId);
        }

        public async Task<SearchUserResponse> SearchBuyerUser(string searchQuery)
        {
            return await cdp.SearchBuyerUser(searchQuery);
        }

        public async Task<BuyerInfoResponse> GetBuyerInfo(string encryptId)
        {
            return await cdp.GetBuyerInfo(encryptId);
        }

        public async Task<ZnkfTradeQueryResponse> GetBuyerTrades(string securityBuyerUid, string bizOrderId)
        {
            return await cdp.GetBuyerTrades(securityBuyerUid, bizOrderId);
        }

        public async Task<ConversationResponse> GetCurrentConversationID()
        {
            return await cdp.GetCurrentConversationID();
        }
    }
}