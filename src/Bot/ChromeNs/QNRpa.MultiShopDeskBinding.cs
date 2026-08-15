using Bot.Automation.ChatDeskNs;
using BotLib;
using FlaUI.UIA3;
using System;
using System.Linq;

namespace Bot.ChromeNs
{
    public partial class QNRpa
    {
        private readonly object _sellerDeskBindingSync = new object();
        private int _sellerDeskProcessId;
        private int _sellerDeskHwnd;

        internal Desk ResolveSellerDesk()
        {
            var seller = SellerNick;
            if (string.IsNullOrWhiteSpace(seller)) return null;
            var desk = DeskSellerBindingRegistry.FindSellerDesk(seller);
            if (desk != null) return desk;

            var desks = Desk.Snapshot();
            if (desks.Count == 1 && RuntimeSellerCount() <= 1)
            {
                // Historical single-shop compatibility only. If two authenticated sellers
                // exist, a single discovered HWND is ambiguous and must never be shared.
                return desks[0];
            }
            return null;
        }

        internal bool EnsureSellerDeskBinding(bool force = false)
        {
            var seller = SellerNick;
            if (string.IsNullOrWhiteSpace(seller)) return false;
            var desk = ResolveSellerDesk();

            if (desk == null)
            {
                if (Desk.HasMultipleDesks || RuntimeSellerCount() > 1)
                {
                    Log.ErrorWithMaxCount(
                        "多店铺RPA绑定失败，未找到卖家唯一对应千牛窗口，禁止共享或猜测其他店铺: seller=" + seller,
                        20);
                }
                return false;
            }

            // Even after resolution, verify the one-to-one registry. A seller-named legacy
            // Desk is remembered by FindSellerDesk; generic single-shop compatibility is only
            // accepted above when there is at most one authenticated seller.
            var boundSeller = DeskSellerBindingRegistry.GetSeller(desk);
            if (RuntimeSellerCount() > 1
                && !string.Equals(boundSeller, seller, StringComparison.Ordinal))
            {
                Log.ErrorWithMaxCount("多店铺RPA绑定已阻止：目标Desk尚未证明属于当前seller: seller="
                    + seller + ", hwnd=" + desk.Hwnd.Handle, 20);
                return false;
            }

            lock (_sellerDeskBindingSync)
            {
                if (!force
                    && automationApplication != null
                    && _sellerDeskProcessId == desk.ProcessId
                    && _sellerDeskHwnd == desk.Hwnd.Handle)
                {
                    return true;
                }

                try
                {
                    automationApplication = FlaUI.Core.Application.Attach(desk.ProcessId);
                    if (uia3Automation == null) uia3Automation = new UIA3Automation();
                    _messageInputTextArea = null;
                    _sendMessageButton = null;
                    _closeContactButton = null;
                    _sellerDeskProcessId = desk.ProcessId;
                    _sellerDeskHwnd = desk.Hwnd.Handle;
                    Log.Info("RPA已绑定卖家专属千牛窗口: seller=" + seller
                        + ", pid=" + desk.ProcessId + ", hwnd=" + desk.Hwnd.Handle);
                    return true;
                }
                catch (Exception ex)
                {
                    _sellerDeskProcessId = 0;
                    _sellerDeskHwnd = 0;
                    _messageInputTextArea = null;
                    _sendMessageButton = null;
                    Log.ErrorWithMaxCount(
                        "RPA绑定卖家专属千牛窗口失败: seller=" + seller + ", " + ex.Message,
                        20);
                    return false;
                }
            }
        }

        internal bool IsSellerDeskBindingReady
        {
            get
            {
                var desk = DeskSellerBindingRegistry.FindSellerDesk(SellerNick);
                return desk != null
                    && DeskSellerBindingRegistry.IsSellerForDesk(desk, SellerNick)
                    && automationApplication != null
                    && _sellerDeskProcessId == desk.ProcessId
                    && _sellerDeskHwnd == desk.Hwnd.Handle;
            }
        }

        internal bool TryClearCanceledBotDraft(string buyer, string reason)
        {
            buyer = BuyerIdentityAliasService.ResolveInternalNick(SellerNick, buyer);
            if (string.IsNullOrWhiteSpace(buyer) || _qn == null || _qn.Buyer == null) return false;

            var currentBuyer = BuyerIdentityAliasService.ResolveInternalNick(
                SellerNick,
                _qn.Buyer.Nick);
            if (!BuyerIdentityAliasService.AreEquivalent(SellerNick, currentBuyer, buyer)) return false;

            var expected = (LastSetPlainText ?? string.Empty).Trim();
            if (expected.Length == 0 || !HasExpectedDraft(expected)) return false;

            // Recheck the conversation immediately before touching the composer. A human reply can
            // arrive while Qianniu is switching conversations; only clear the draft when both buyer
            // identity and exact editor contents still prove that this composer belongs to the Bot.
            currentBuyer = _qn.Buyer == null
                ? string.Empty
                : BuyerIdentityAliasService.ResolveInternalNick(SellerNick, _qn.Buyer.Nick);
            if (!BuyerIdentityAliasService.AreEquivalent(SellerNick, currentBuyer, buyer)) return false;

            ClearExpectedDraft(expected,
                string.IsNullOrWhiteSpace(reason) ? "Bot草稿已取消" : reason);

            string remaining;
            var cleared = !TryGetEditorText(out remaining)
                || !EditorMatchesExpectedText(remaining, expected);
            if (cleared)
            {
                LatestSetTextTime = DateTime.MinValue;
                Log.Info("已清除已取消的Bot专属草稿: seller=" + SellerNick
                    + ", buyer=" + buyer + ", reason=" + (reason ?? string.Empty));
            }
            return cleared;
        }

        private static int RuntimeSellerCount()
        {
            try
            {
                return QN.GetRuntimeSafetySnapshot()
                    .Where(qn => qn != null && qn.Seller != null
                        && !string.IsNullOrWhiteSpace(qn.Seller.Nick))
                    .Select(qn => qn.Seller.Nick.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .Count();
            }
            catch
            {
                return 0;
            }
        }
    }
}