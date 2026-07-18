from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_once(text, old, new, label):
    if old not in text:
        raise SystemExit(f"missing patch anchor: {label}")
    if text.count(old) != 1:
        raise SystemExit(f"patch anchor is not unique: {label} count={text.count(old)}")
    return text.replace(old, new, 1)


service_path = ROOT / "src/Bot/Knowledge/ChatHistoryScanService.cs"
service = service_path.read_text(encoding="utf-8-sig")

service = replace_once(
    service,
    '            sb.AppendLine("已读取消息：" + MessageCount);',
    '            sb.AppendLine("已保留有效消息：" + MessageCount);',
    "progress valid message label")

service = replace_once(
    service,
    '''        public bool MessageManagerOpened { get; set; }
        public int ContactCount { get; set; }''',
    '''        public bool MessageManagerOpened { get; set; }
        public int ChatBuyerListContactCount { get; set; }
        public int MessageManagerContactCount { get; set; }
        public int ApiContactCount { get; set; }
        public int ContactCount { get; set; }''',
    "contact source result properties")

old_scan = '''            var diagnostics = new StringBuilder();
            Report(progress, "正在打开千牛消息管理器...", 0, 0, string.Empty, 0, 0, 0);
            var managerOpened = await TryOpenMessageManagerAsync(diagnostics, token);

            Report(progress, "正在读取最近联系人...", 0, 0, string.Empty, 0, 0, 0);
            var contacts = new HashSet<string>(StringComparer.Ordinal);
            AddContact(contacts, qn.Buyer == null ? string.Empty : qn.Buyer.Nick, qn.Seller.Nick);
            foreach (var name in await ReadVisibleMessageManagerContactsAsync(diagnostics, token))
            {
                AddContact(contacts, name, qn.Seller.Nick);
            }
            foreach (var name in await ReadContactsFromApisAsync(qn, diagnostics, token))
            {
                AddContact(contacts, name, qn.Seller.Nick);
            }
'''
new_scan = '''            var diagnostics = new StringBuilder();
            var originalBuyer = qn.Buyer == null ? string.Empty : (qn.Buyer.Nick ?? string.Empty);

            Report(progress, "正在读取千牛左侧“全部买家”列表...", 0, 0, string.Empty, 0, 0, 0);
            var chatBuyerListContacts = await ReadVisibleChatBuyerListContactsAsync(diagnostics, token);

            var contacts = new HashSet<string>(StringComparer.Ordinal);
            AddContact(contacts, originalBuyer, qn.Seller.Nick);
            foreach (var name in chatBuyerListContacts)
            {
                AddContact(contacts, name, qn.Seller.Nick);
            }

            var managerOpened = false;
            var messageManagerContacts = new List<string>();
            if (contacts.Count <= 1)
            {
                Report(progress, "左侧买家列表未完整读取，正在尝试千牛消息管理器...", 0, 0, string.Empty, 0, 0, 0);
                managerOpened = await TryOpenMessageManagerAsync(diagnostics, token);
                if (managerOpened)
                {
                    messageManagerContacts = await ReadVisibleMessageManagerContactsAsync(diagnostics, token);
                    foreach (var name in messageManagerContacts)
                    {
                        AddContact(contacts, name, qn.Seller.Nick);
                    }
                }
            }
            else
            {
                diagnostics.AppendLine("已从聊天界面左侧“全部买家”列表读取联系人，无需打开独立消息管理器。");
            }

            Report(progress, "正在通过千牛接口补充联系人...", 0, 0, string.Empty, 0, 0, 0);
            var apiContacts = await ReadContactsFromApisAsync(qn, diagnostics, token);
            foreach (var name in apiContacts)
            {
                AddContact(contacts, name, qn.Seller.Nick);
            }

            diagnostics.AppendLine(string.Format(
                "联系人来源：全部买家列表 {0}，消息管理器 {1}，接口 {2}，合并去重后 {3}。",
                chatBuyerListContacts.Count,
                messageManagerContacts.Count,
                apiContacts.Count,
                contacts.Count));
'''
service = replace_once(service, old_scan, new_scan, "contact discovery flow")

service = replace_once(
    service,
    '            var originalBuyer = qn.Buyer == null ? string.Empty : (qn.Buyer.Nick ?? string.Empty);\n            var histories = new List<ContactHistory>();',
    '            var histories = new List<ContactHistory>();',
    "duplicate original buyer declaration")

service = replace_once(
    service,
    '''                MessageManagerOpened = managerOpened,
                ContactCount = orderedContacts.Count,''',
    '''                MessageManagerOpened = managerOpened,
                ChatBuyerListContactCount = chatBuyerListContacts.Count,
                MessageManagerContactCount = messageManagerContacts.Count,
                ApiContactCount = apiContacts.Count,
                ContactCount = orderedContacts.Count,''',
    "result contact source counts")

new_reader = r'''        private static async Task<List<string>> ReadVisibleChatBuyerListContactsAsync(StringBuilder diagnostics, CancellationToken token)
        {
            return await Task.Run(() =>
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    if (Desk.Inst == null)
                    {
                        diagnostics.AppendLine("无法读取“全部买家”：未找到千牛主窗口进程。");
                        return names.ToList();
                    }

                    var app = FlaUI.Core.Application.Attach(Desk.Inst.ProcessId);
                    using (var automation = new UIA3Automation())
                    {
                        Window chatWindow = null;
                        AutomationElement allBuyerHeader = null;
                        foreach (var window in app.GetAllTopLevelWindows(automation))
                        {
                            token.ThrowIfCancellationRequested();
                            var header = window.FindAllDescendants()
                                .FirstOrDefault(x => IsAllBuyerHeader(x.Name));
                            if (header == null) continue;
                            chatWindow = window;
                            allBuyerHeader = header;
                            break;
                        }

                        if (chatWindow == null || allBuyerHeader == null)
                        {
                            diagnostics.AppendLine("千牛聊天主窗口中未找到“全部买家”栏。");
                            return names.ToList();
                        }

                        TryBringToFront(chatWindow);
                        var windowBounds = chatWindow.BoundingRectangle;
                        var headerBounds = allBuyerHeader.BoundingRectangle;
                        var panelLeft = windowBounds.Left;
                        var panelRight = Math.Min(
                            windowBounds.Right,
                            windowBounds.Left + Math.Max(260, Math.Min(340, windowBounds.Width * 0.27)));
                        var panelTop = Math.Max(headerBounds.Bottom, windowBounds.Top + 250);
                        var panelBottom = windowBounds.Bottom - 28;
                        if (panelRight <= panelLeft || panelBottom <= panelTop)
                        {
                            diagnostics.AppendLine("“全部买家”列表区域尺寸无效。");
                            return names.ToList();
                        }

                        var focusPoint = new System.Drawing.Point(
                            (int)(panelLeft + Math.Min(150, (panelRight - panelLeft) * 0.55)),
                            (int)(panelTop + (panelBottom - panelTop) * 0.55));
                        var stableRounds = 0;

                        for (var round = 0; round < 180 && stableRounds < 10; round++)
                        {
                            token.ThrowIfCancellationRequested();
                            var before = names.Count;
                            var elements = chatWindow.FindAllDescendants()
                                .Where(x => IsInsideBuyerPanel(x, panelLeft, panelRight, panelTop, panelBottom))
                                .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                                .ToList();

                            var timeElements = elements
                                .Where(x => Regex.IsMatch((x.Name ?? string.Empty).Trim(), @"^\d{1,2}:\d{2}$"))
                                .Where(x => x.BoundingRectangle.Left > panelLeft + (panelRight - panelLeft) * 0.62)
                                .ToList();

                            foreach (var timeElement in timeElements)
                            {
                                var timeBounds = timeElement.BoundingRectangle;
                                var candidate = elements
                                    .Where(x => !object.ReferenceEquals(x, timeElement))
                                    .Where(x => x.BoundingRectangle.Left > panelLeft + 42)
                                    .Where(x => x.BoundingRectangle.Right < timeBounds.Left - 2)
                                    .Where(x => Math.Abs(x.BoundingRectangle.Top - timeBounds.Top) <= 11)
                                    .OrderBy(x => x.BoundingRectangle.Left)
                                    .FirstOrDefault();
                                AddBuyerListCandidate(names, candidate == null ? string.Empty : candidate.Name);
                            }

                            if (timeElements.Count == 0)
                            {
                                foreach (var group in elements
                                    .Where(x => x.BoundingRectangle.Left > panelLeft + 42)
                                    .Where(x => x.BoundingRectangle.Right < panelRight - 18)
                                    .Where(x => !Regex.IsMatch((x.Name ?? string.Empty).Trim(), @"^\d{1,2}:\d{2}$"))
                                    .GroupBy(x => (int)Math.Floor((x.BoundingRectangle.Top - panelTop) / 48.0)))
                                {
                                    var candidate = group
                                        .OrderBy(x => x.BoundingRectangle.Top)
                                        .ThenBy(x => x.BoundingRectangle.Left)
                                        .FirstOrDefault();
                                    AddBuyerListCandidate(names, candidate == null ? string.Empty : candidate.Name);
                                }
                            }

                            stableRounds = names.Count == before ? stableRounds + 1 : 0;
                            ClickPoint(focusPoint);
                            PressPageDown();
                            Thread.Sleep(160);
                        }
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.AppendLine("读取聊天界面“全部买家”列表失败：" + Safe(ex.Message));
                }

                diagnostics.AppendLine("聊天界面“全部买家”列表读取到 " + names.Count + " 个联系人。");
                return names.ToList();
            }, token);
        }

        private static bool IsAllBuyerHeader(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.StartsWith("全部买家", StringComparison.Ordinal);
        }

        private static bool IsInsideBuyerPanel(
            AutomationElement element,
            double left,
            double right,
            double top,
            double bottom)
        {
            if (element == null) return false;
            var rect = element.BoundingRectangle;
            if (rect.Width <= 0 || rect.Height <= 0) return false;
            var centerX = rect.Left + rect.Width / 2;
            var centerY = rect.Top + rect.Height / 2;
            return centerX >= left && centerX <= right && centerY >= top && centerY <= bottom;
        }

        private static void AddBuyerListCandidate(HashSet<string> names, string value)
        {
            var nick = CleanBuyerListNick(value);
            if (!IsPlausibleBuyerNick(nick) || IsBuyerListNoise(nick)) return;
            names.Add(nick);
        }

        private static string CleanBuyerListNick(string value)
        {
            value = (value ?? string.Empty).Trim();
            var lines = Regex.Split(value, @"[\r\n\t]+")
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();
            if (lines.Count > 0) value = lines[0];
            value = Regex.Replace(value, @"\s+\d{1,2}:\d{2}$", string.Empty).Trim();
            var columns = Regex.Split(value, @"\s{2,}")
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();
            if (columns.Count > 0) value = columns[0];
            return CleanNick(value);
        }

        private static bool IsBuyerListNoise(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value)) return true;
            var exact = new[]
            {
                "全部买家", "最近联系", "最近星标", "咨询未下单", "咨询未付款",
                "正在接待", "其他消息", "联系人", "搜索", "消息", "订单号", "聊天记录"
            };
            if (exact.Any(x => string.Equals(value, x, StringComparison.Ordinal))) return true;
            if (value.StartsWith("全部买家", StringComparison.Ordinal)) return true;
            if (value.StartsWith("最近星标", StringComparison.Ordinal)) return true;
            if (value.StartsWith("咨询未下单", StringComparison.Ordinal)) return true;
            if (value.StartsWith("咨询未付款", StringComparison.Ordinal)) return true;
            if (Regex.IsMatch(value, @"^\d{1,2}:\d{2}$")) return true;
            if (value.IndexOf("撤回了一条消息", StringComparison.Ordinal) >= 0) return true;
            if (value.IndexOf("系统消息", StringComparison.Ordinal) >= 0) return true;
            return false;
        }

'''
anchor = '        private static async Task<List<string>> ReadVisibleMessageManagerContactsAsync(StringBuilder diagnostics, CancellationToken token)\n'
service = replace_once(service, anchor, new_reader + anchor, "all buyers reader insertion")
service_path.write_text(service, encoding="utf-8-sig")

window_path = ROOT / "src/Bot/Knowledge/ChatHistoryScanWindow.cs"
window = window_path.read_text(encoding="utf-8-sig")
window = replace_once(
    window,
    '系统会尝试自动打开千牛“消息管理器”，读取“最近联系”中的买家，并通过千牛历史消息接口分页获取聊天记录。只整理买家提问后客服已经回答的轮次，不向买家发送任何消息。',
    '系统会优先读取千牛聊天界面左侧“全部买家”列表；只有列表读取不到时才尝试独立“消息管理器”，并通过千牛历史消息接口分页获取聊天记录。只整理买家提问后客服已经回答的轮次，不向买家发送任何消息。',
    "window intro")
window = replace_once(
    window,
    '• 联系人优先从消息管理器可见列表和千牛联系人接口读取。',
    '• 联系人优先从聊天界面左侧“全部买家”列表读取，消息管理器和千牛联系人接口作为补充。',
    "window contact source description")
window = replace_once(
    window,
    '                sb.Append("，消息 ").Append(result.MessageCount);',
    '                sb.Append("，有效聊天消息 ").Append(result.MessageCount);',
    "window effective message label")
old_result = '''                _progress.Text = sb + Environment.NewLine
                    + "消息管理器：" + (result.MessageManagerOpened ? "已自动打开" : "未找到入口，已使用后台接口继续扫描")
                    + (string.IsNullOrWhiteSpace(result.Diagnostics)
                        ? string.Empty
                        : Environment.NewLine + Environment.NewLine + "诊断信息：" + Environment.NewLine + result.Diagnostics);'''
new_result = '''                _progress.Text = sb + Environment.NewLine
                    + "联系人来源：全部买家列表 " + result.ChatBuyerListContactCount
                    + "，消息管理器 " + result.MessageManagerContactCount
                    + "，接口 " + result.ApiContactCount
                    + Environment.NewLine
                    + "消息管理器：" + (result.MessageManagerOpened
                        ? "已作为兜底自动打开"
                        : (result.ChatBuyerListContactCount > 0
                            ? "未打开（已从左侧全部买家列表读取）"
                            : "未找到入口，已使用接口或当前会话兜底"))
                    + (string.IsNullOrWhiteSpace(result.Diagnostics)
                        ? string.Empty
                        : Environment.NewLine + Environment.NewLine + "诊断信息：" + Environment.NewLine + result.Diagnostics);'''
window = replace_once(window, old_result, new_result, "window result diagnostics")
window_path.write_text(window, encoding="utf-8-sig")

test_path = ROOT / "tests/test_history_scan_static.py"
test = test_path.read_text(encoding="utf-8-sig")
test = replace_once(
    test,
    '    assert "ReadVisibleMessageManagerContactsAsync" in source\n',
    '    assert "ReadVisibleChatBuyerListContactsAsync" in source\n    assert "ChatBuyerListContactCount" in source\n    assert "ReadVisibleMessageManagerContactsAsync" in source\n',
    "static test all buyers assertions")
test_path.write_text(test, encoding="utf-8")
