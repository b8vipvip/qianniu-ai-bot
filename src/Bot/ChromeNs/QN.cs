using Bot.ChromeNs;
using DbEntity.Response;
using DbEntity;
using System;
using System.Collections.Generic;
using System.Linq;
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
            var ok = await SendTextAsync(buyer, text);
            var retry = Math.Max(0, retryCount);
            for (var i = 0; !ok && i < retry; i++)
            {
                Log.Info("自动发送失败，准备重试第" + (i + 1) + "次。buyer=" + buyer + ", text=" + text);
                await Task.Delay(900);
                ok = await SendTextAsync(buyer, text);
            }
            return ok;
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
            if (e != null && e.Seller != null && e.Buyer != null)
            {
                SetActiveConversationByNick(e.Seller.Nick, e.Buyer.Nick, "shopRobotNewMsg");
            }
            if (Params.Robot.CanUseRobotReal && Params.Robot.GetIsAutoReply() && e != null && e.Buyer != null)
            {
                OpenChat(e.Buyer.Nick);
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

        private void Cdp_EvRecieveNewMessage(object sender, RecieveNewMessageEventArgs e)
        {
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

                var messages = chatRes.result;
                messages.ForEach(async m =>
                {
                    try
                    {
                        var msgText = GetMessageText(m);

                        // 卖家消息只记录为聊天流的一部分，不再自动触发“人工接管”。
                        if (IsSellerMessage(m))
                        {
                            SetActiveConversationByNick(m.fromid.nick, m.toid.nick, "sellerMessage");
                            return;
                        }

                        if (IsBuyerMessage(m))
                        {
                            SetActiveConversationByNick(m.toid.nick, m.fromid.nick, "buyerMessage");

                            var botEnabled = Params.Robot.CanUseRobotReal;
                            var autoSend = Params.Robot.GetIsAutoReply();
                            var answer = string.Empty;

                            if (!botEnabled)
                            {
                                answer = "Bot已停用，未调用AI。";
                                Desk.Inst.AddConversation(m.toid.nick, m.fromid.nick, msgText, answer, false);
                                Log.Info("Bot已停用，跳过买家消息: buyer=" + m.fromid.nick + ", msg=" + msgText);
                                return;
                            }

                            // 启用Bot后，所有买家消息都交给AI；自动回复只决定是否直接发送。
                            answer = MyOpenAI.GetAnswer(m.toid.nick, m.fromid.nick, msgText);
                            var conversationCtl = Desk.Inst.AddConversation(m.toid.nick, m.fromid.nick, msgText, answer, autoSend);

                            if (autoSend)
                            {
                                if (string.IsNullOrWhiteSpace(answer) || answer.StartsWith("错误："))
                                {
                                    if (conversationCtl != null) conversationCtl.SetSendResult(false, "未发送：AI错误");
                                }
                                else
                                {
                                    var sendOk = await SendTextWithRetryAsync(m.fromid.nick, answer, 1);
                                    if (conversationCtl != null)
                                    {
                                        conversationCtl.SetSendResult(sendOk, sendOk ? "已发送" : "发送失败，已重试1次");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Exception(ex);
                    }
                    await Task.Delay(2000);
                });
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
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