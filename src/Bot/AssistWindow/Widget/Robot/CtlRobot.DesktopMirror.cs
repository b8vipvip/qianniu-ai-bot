using Bot.ChromeNs;
using BotLib.Extensions;
using System.Collections.Generic;
using System.Windows;

namespace Bot.AssistWindow.Widget.Robot
{
    public partial class CtlRobot
    {
        /// <summary>
        /// Mirrors seller text into the standalone workbench without changing runtime state.
        /// </summary>
        internal void MirrorSeller(string seller)
        {
            txtSeller.Text = string.IsNullOrWhiteSpace(seller) ? "..." : seller;
            RefreshStats();
        }

        /// <summary>
        /// Mirrors the active buyer without recording another reception and without
        /// issuing the duplicate Qianniu goods query performed by ChangeBuyer().
        /// </summary>
        internal void MirrorBuyer(string buyer)
        {
            txtBuyer.Text = string.IsNullOrWhiteSpace(buyer) ? "..." : buyer;
            if (QN.CurQN != null && QN.CurQN.Seller != null && QN.CurQN.Buyer != null)
            {
                _preQN = QN.CurQN;
                RefreshConversations();
                RefreshRunStatus();
            }
            RefreshStats();
        }

        /// <summary>
        /// Mirrors an answer that has already been produced by the authoritative Desk
        /// UI chain. This intentionally does not call BotRuntimeStats.RecordDisplayedAnswer.
        /// </summary>
        internal void MirrorConversation(
            string seller,
            string buyer,
            string question,
            string answer,
            bool isAutoReply,
            string answerSource)
        {
            var key = string.Format("{0}#{1}", seller, buyer);
            var ctlConversation = CtlConversation.Create(
                seller,
                buyer,
                question,
                answer,
                isAutoReply,
                answerSource);
            ctlConversation.ResendRequested += CtlConversation_ResendRequested;
            ctlConversation.EditRequested += CtlConversation_EditRequested;

            var conversations = buyerConversations.xTryGetValue(key);
            if (conversations == null || conversations.Count < 1)
            {
                conversations = new List<CtlConversation> { ctlConversation };
            }
            else
            {
                conversations.Add(ctlConversation);
            }
            buyerConversations.AddOrUpdate(key, id => conversations, (k, v) => conversations);

            if (QN.CurQN != null
                && QN.CurQN.Seller != null
                && QN.CurQN.Buyer != null
                && QN.CurQN.Seller.Nick == seller
                && QN.CurQN.Buyer.Nick == buyer)
            {
                grdTipNoConv.Visibility = Visibility.Collapsed;
                stkDialog.Children.Add(ctlConversation);
            }
            scvBody.ScrollToEnd();
            RefreshStats();
        }
    }
}
