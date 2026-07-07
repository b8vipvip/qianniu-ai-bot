using Bot.ChromeNs;
using DbEntity.Response;
using DbEntity;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Bot.Automation.ChatDeskNs;
using Bot.ChatRecord;
using Newtonsoft.Json;
using BotLib;
using System.Diagnostics;
using System.Threading;
using Bot.AssistWindow.Widget.Robot;
using BotLib.Wpf.Extensions;
using OpenAI.Chat;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using static Bot.Params;

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
        public string QnVersion { get; set; }

        private static readonly ConcurrentDictionary<string, DateTime> ManualPauseUntil = new ConcurrentDictionary<string, DateTime>();
        private static readonly ConcurrentDictionary<string, BotSentInfo> RecentBotSent = new ConcurrentDictionary<string, BotSentInfo>();
        private const int ManualPauseMinutes = 10;

        private CDPClient cdp;
        public CDPClient CDP
        {
            get
            {
                return cdp;
            }
            set
            {
                cdp = value;
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
            get
            {
                return _seller;
            }
            set
            {
                _seller = value;
            }
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

        public async Task SendTextAsync(string buyer, string text)
        {
            var comparison = String.Compare(QnVersion, "9.19.06N", StringComparison.OrdinalIgnoreCase);
            if (comparison < 0)
            {
                SendTimiMsg(buyer, text);
            }
            else
            {
                await rpa.SendTextAsync(buyer, text);
            }
        }

        public async void SendImageAsync(string buyer, string imagePath)
        {
            await rpa.SendImageAsync(buyer, imagePath);
        }

        private static string GetKey(string seller, string buyer)
        {
            return string.Format("{0}#{1}", seller ?? string.Empty, buyer ?? string.Empty);
        }

        private static string NormalizeText(string text)
        {
            text = (text ?? string.Empty).Trim();
            text = Regex.Replace(text, "\\s+", "");
            return text;
        }

        private static bool IsTrivialBuyerText(string text)
        {
            var t = NormalizeText(text);
            if (string.IsNullOrEmpty(t)) return true;
            if (Regex.IsMatch(t, "^[0-9]+$")) return true;
            if (Regex.IsMatch(t, "^[，。,.!！?？~～…、；;：:\\-_=+\uD83D\uDE00-\uD83D\uDE4F]+$")) return true;
            var words = new HashSet<string> { "好", "好的", "嗯", "恩", "哦", "噢", "是", "是的", "对", "对的", "谢谢", "谢了", "收到", "知道了", "可以", "行" };
            return words.Contains(t);
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

        private static bool IsBotEcho(string seller, string buyer, string text)
        {
            BotSentInfo info;
            var key = GetKey(seller, buyer);
            if (!RecentBotSent.TryGetValue(key, out info)) return false;
            if ((DateTime.Now - info.Time).TotalSeconds > 30) return false;
            return NormalizeText(info.Text) == NormalizeText(text);
        }

        private static void MarkBotSent(string seller, string buyer, string text)
        {
            RecentBotSent[GetKey(seller, buyer)] = new BotSentInfo
            {
                Text = text ?? string.Empty,
                Time = DateTime.Now
            };
        }

        private static void MarkManualPause(string seller, string buyer, string text)
        {
            if (IsBotEcho(seller, buyer, text)) return;
            ManualPauseUntil[GetKey(seller, buyer)] = DateTime.Now.AddMinutes(ManualPauseMinutes);
            Log.Info("检测到人工回复，暂停该买家自动回复" + ManualPauseMinutes + "分钟，buyer=" + buyer);
        }

        private static bool IsManualPaused(string seller, string buyer, out int remainMinutes)
        {
            remainMinutes = 0;
            DateTime until;
            var key = GetKey(seller, buyer);
            if (!ManualPauseUntil.TryGetValue(key, out until)) return false;
            if (until <= DateTime.Now)
            {
                DateTime removed;
                ManualPauseUntil.TryRemove(key, out removed);
                return false;
            }
            remainMinutes = Math.Max(1, (int)Math.Ceiling((until - DateTime.Now).TotalMinutes));
            return true;
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

        private void Cdp_EvShopRobotReceriveNewMessage(object sender, ShopRobotReceriveNewMessageEventArgs e)
        {
            if (Params.Robot.GetIsAutoReply())
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
            Seller = e.Seller;
            Buyer = e.Buyer;
            CurQN = this;
            Desk.Inst.ChangeBuyer(e.Buyer.Nick);
            Desk.Inst.ChangeSeller(e.Seller.Nick);

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

                        if (IsSellerMessage(m))
                        {
                            MarkManualPause(m.fromid.nick, m.toid.nick, msgText);
                            return;
                        }

                        if (IsBuyerMessage(m))
                        {
                            var isAutoReply = Params.Robot.GetIsAutoReply();
                            var answer = string.Empty;
                            var shouldSend = isAutoReply;
                            int remainMinutes;

                            if (IsManualPaused(m.toid.nick, m.fromid.nick, out remainMinutes))
                            {
                                answer = "人工已接管，暂停自动回复" + remainMinutes + "分钟。";
                                shouldSend = false;
                            }
                            else if (IsTrivialBuyerText(msgText))
                            {
                                answer = "疑似无明确问题，未自动回复。";
                                shouldSend = false;
                            }
                            else if (isAutoReply)
                            {
                                answer = MyOpenAI.GetAnswer(m.toid.nick, m.fromid.nick, msgText);
                            }
                            else
                            {
                                answer = "自动回复已关闭，未调用AI。";
                            }

                            Desk.Inst.AddConversation(m.toid.nick, m.fromid.nick, msgText, answer, shouldSend);

                            if (shouldSend && !string.IsNullOrWhiteSpace(answer) && !answer.StartsWith("错误："))
                            {
                                MarkBotSent(m.toid.nick, m.fromid.nick, answer);
                                await SendTextAsync(m.fromid.nick, answer);
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
            Seller = e.Seller;
            Buyer = e.Buyer;
            CurQN = this;
            Desk.Inst.ChangeBuyer(e.Buyer.Nick);
            Desk.Inst.ChangeSeller(e.Seller.Nick);
            if (EvBuyerSwitched != null)
            {
                EvBuyerSwitched(this, e);
            }
        }

        public static QN GetByNick(LocalUser seller)
        {
            var qn = QNSet.FirstOrDefault(q => q._seller.Nick == seller.Nick || q._seller.Display == seller.Display);
            if (qn == null)
            {
                qn = new QN(seller);
                QNSet.Add(qn);
            }
            return qn;
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

        private class BotSentInfo
        {
            public string Text { get; set; }
            public DateTime Time { get; set; }
        }
    }
}