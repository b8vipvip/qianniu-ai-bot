from pathlib import Path

path = Path("src/Bot/Automation/ChatDeskNs/Desk.cs")
text = path.read_text(encoding="utf-8-sig")
old = '''        public CtlConversation AddConversation(string seller, string buyer, string question, string answer, bool isAutoReply)
        {
            CtlConversation ctlConversation = null;
            DispatcherEx.xInvoke(new Action(() =>
            {
                if (CtlRobot == null)
                {
                    CtlRobot = inst.AssistWindow.ctlRightPanel.GetTabItem(Bot.AssistWindow.Widget.RightPanel.TabTypeEnum.Robot).Content as CtlRobot;
                }
                ctlConversation = CtlRobot.AddConversation(seller, buyer, question, answer, isAutoReply);
            }));
            return ctlConversation;
        }
'''
new = '''        public CtlConversation AddConversation(
            string seller,
            string buyer,
            string question,
            string answer,
            bool isAutoReply,
            string answerSource = "")
        {
            CtlConversation ctlConversation = null;
            DispatcherEx.xInvoke(new Action(() =>
            {
                if (CtlRobot == null)
                {
                    CtlRobot = inst.AssistWindow.ctlRightPanel.GetTabItem(Bot.AssistWindow.Widget.RightPanel.TabTypeEnum.Robot).Content as CtlRobot;
                }
                ctlConversation = CtlRobot.AddConversation(seller, buyer, question, answer, isAutoReply, answerSource);
            }));
            return ctlConversation;
        }
'''
if text.count(old) != 1:
    raise SystemExit("Desk.AddConversation patch anchor count=" + str(text.count(old)))
path.write_text(text.replace(old, new, 1), encoding="utf-8-sig")
print("PATCH_RESULT=PASS")
