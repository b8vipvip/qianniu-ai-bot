using Bot.Automation.ChatDeskNs;
using BotLib;
using FlaUI.UIA3;
using System;

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
            var desk = Desk.FindExistingBySellerNick(seller);
            if (desk != null) return desk;

            var desks = Desk.Snapshot();
            if (desks.Count == 1)
            {
                // Preserve historical single-shop behavior while refusing to guess when
                // more than one Qianniu Desk is present.
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
                if (Desk.HasMultipleDesks)
                {
                    Log.ErrorWithMaxCount(
                        "多店铺RPA绑定失败，未找到卖家对应千牛窗口，禁止猜测其他店铺: seller=" + seller,
                        20);
                }
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
                var desk = Desk.FindExistingBySellerNick(SellerNick);
                return desk != null
                    && automationApplication != null
                    && _sellerDeskProcessId == desk.ProcessId
                    && _sellerDeskHwnd == desk.Hwnd.Handle;
            }
        }
    }
}