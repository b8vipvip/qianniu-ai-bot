using Bot.ChromeNs;
using System;

namespace Bot.AssistWindow.Widget.Robot
{
    public partial class CtlRobot
    {
        private string _multiShopSessionSeller = string.Empty;
        private string _multiShopSessionBuyer = string.Empty;

        /// <summary>
        /// Refreshes this control from the QN belonging to its own Desk. It intentionally
        /// does not record reception statistics and does not request or send any message.
        /// </summary>
        internal void SynchronizeSellerSession(QN qn)
        {
            if (qn == null || qn.Seller == null) return;
            var seller = (qn.Seller.Nick ?? string.Empty).Trim();
            if (seller.Length == 0) return;

            // A CtlRobot attached to a Desk must never adopt another seller.
            if (_desk != null
                && !string.Equals((_desk.WndTitle ?? string.Empty).Trim(), seller, StringComparison.Ordinal))
            {
                return;
            }

            var buyer = qn.Buyer == null ? string.Empty : (qn.Buyer.Nick ?? string.Empty).Trim();
            var sellerChanged = !string.Equals(_multiShopSessionSeller, seller, StringComparison.Ordinal);
            var buyerChanged = !string.Equals(_multiShopSessionBuyer, buyer, StringComparison.Ordinal);
            if (!sellerChanged && !buyerChanged && ReferenceEquals(_preQN, qn)) return;

            _multiShopSessionSeller = seller;
            _multiShopSessionBuyer = buyer;
            _preQN = qn;
            txtSeller.Text = seller;
            txtBuyer.Text = buyer.Length == 0 ? "..." : buyer;
            RefreshConversations();
            RefreshRunStatus();
            RefreshStats();
            if (buyerChanged && buyer.Length > 0)
            {
                RefreshItems();
            }
        }
    }
}