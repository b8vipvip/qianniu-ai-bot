from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def write(path, text):
    (ROOT / path).write_text(text, encoding="utf-8")


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one anchor, got {count}")
    return text.replace(old, new, 1)


# 1) Preserve structured conversation source metadata supplied by Qianniu.
path = "src/DbEntity/Response/LocalUser.cs"
text = read(path)
old = '''        [JsonProperty("targetId")]
        public string TargetId { get; set; }
    }

    public class ActiveLocalUser
'''
new = '''        [JsonProperty("targetId")]
        public string TargetId { get; set; }
        [JsonProperty("targetType")]
        public string TargetType { get; set; }
        [JsonProperty("type")]
        public string Type { get; set; }
        [JsonProperty("conversationType")]
        public string ConversationType { get; set; }
        [JsonProperty("scene")]
        public string Scene { get; set; }
        [JsonProperty("category")]
        public string Category { get; set; }
        [JsonProperty("source")]
        public string Source { get; set; }
        [JsonProperty("channel")]
        public string Channel { get; set; }
    }

    public class ActiveLocalUser
'''
text = replace_once(text, old, new, "conversation metadata")
write(path, text)

# 2) Shared source/identity guard. It deliberately never treats a URL itself as a blocker.
path = "src/Bot/ChromeNs/IncomingMessageSafety.cs"
text = read(path)
text = replace_once(text, "using Bot.ChatRecord;\n", "using Bot.ChatRecord;\nusing DbEntity;\n", "guard import")
anchor = "    internal static class IncomingMessageSafety\n"
guard = r'''    internal static class NonBuyerConversationGuard
    {
        private static readonly string[] StrongIdentityMarkers =
        {
            "(行业小二)", "（行业小二）", "行业小二",
            "(平台小二)", "（平台小二）", "平台小二",
            "(淘宝小二)", "（淘宝小二）", "淘宝小二",
            "(天猫小二)", "（天猫小二）", "天猫小二",
            "(阿里小二)", "（阿里小二）", "阿里小二",
            "(官方小二)", "（官方小二）", "官方小二",
            "(服务商)", "（服务商）", "官方服务商", "服务商消息",
            "1688消息", "1688官方",
            "群聊消息", "群消息",
            "淘宝系统", "千牛系统", "平台系统", "系统消息",
            "平台通知", "官方通知", "系统通知"
        };

        private static readonly string[] NonBuyerSourceTokens =
        {
            "cnalichn", "1688", "xiaoer", "serviceprovider", "service-provider",
            "service_provider", "group", "chatroom", "chat-room", "tribe", "qun",
            "official", "system", "platform-notify", "platform_notify"
        };

        public static bool ShouldBlockConversation(LocalUser seller, Conversation conversation, out string reason)
        {
            reason = string.Empty;
            if (conversation == null) return false;

            var sellerNick = seller == null ? string.Empty : Normalize(seller.Nick);
            var sellerTargetId = seller == null ? string.Empty : Normalize(seller.TargetId);
            var candidateNick = Normalize(conversation.Nick);
            var candidateTargetId = Normalize(conversation.TargetId);

            if (SameNonEmpty(sellerNick, candidateNick)
                || SameNonEmpty(sellerTargetId, candidateTargetId))
            {
                reason = "self_identity";
                return true;
            }

            if (ContainsStrongIdentityMarker(Join(conversation.Nick, conversation.Display)))
            {
                reason = "non_buyer_identity_marker";
                return true;
            }

            if (IsNonBuyerSourceToken(conversation.TargetType)
                || IsNonBuyerSourceToken(conversation.Type)
                || IsNonBuyerSourceToken(conversation.ConversationType)
                || IsNonBuyerSourceToken(conversation.Scene)
                || IsNonBuyerSourceToken(conversation.Category)
                || IsNonBuyerSourceToken(conversation.Source)
                || IsNonBuyerSourceToken(conversation.Channel))
            {
                reason = "non_buyer_conversation_source";
                return true;
            }
            return false;
        }

        public static bool ShouldBlockIdentity(string sellerNick, string candidateNick, out string reason)
        {
            reason = string.Empty;
            sellerNick = Normalize(sellerNick);
            candidateNick = Normalize(candidateNick);
            if (SameNonEmpty(sellerNick, candidateNick))
            {
                reason = "self_identity";
                return true;
            }
            if (ContainsStrongIdentityMarker(candidateNick))
            {
                reason = "non_buyer_identity_marker";
                return true;
            }
            return false;
        }

        public static bool ShouldBlockMessage(QNChatMessage message, string sellerNick, string messageText, out string reason)
        {
            reason = string.Empty;
            if (message == null) return false;

            sellerNick = Normalize(sellerNick);
            if (sellerNick.Length == 0 && message.loginid != null)
                sellerNick = Normalize(message.loginid.nick);

            var fromNick = message.fromid == null ? string.Empty : Normalize(message.fromid.nick);
            var fromDisplay = message.fromid == null ? string.Empty : Normalize(message.fromid.display);

            // Never classify seller echoes as non-buyer ingress; they are observational delivery/learning evidence.
            if (SameNonEmpty(sellerNick, fromNick)) return false;

            if (message.fromid != null && IsNonBuyerSourceToken(message.fromid.targetType))
            {
                reason = "non_buyer_sender_type";
                return true;
            }
            if (message.toid != null && IsGroupSourceToken(message.toid.targetType))
            {
                reason = "group_conversation";
                return true;
            }
            if (ContainsStrongIdentityMarker(Join(fromNick, fromDisplay)))
            {
                reason = "non_buyer_sender_identity";
                return true;
            }
            if (ConversationContextStore.IsPlatformSystemTip(message, messageText))
            {
                reason = "platform_system_tip";
                return true;
            }

            var headerTitle = message.originalData == null || message.originalData.header == null
                ? string.Empty
                : Normalize(message.originalData.header.title);
            if (IsStrongSystemHeader(headerTitle))
            {
                reason = "platform_system_card";
                return true;
            }
            return false;
        }

        private static bool IsStrongSystemHeader(string value)
        {
            value = Normalize(value);
            if (value.Length == 0) return false;
            return value == "系统通知"
                || value == "平台通知"
                || value == "官方通知"
                || value == "淘宝通知"
                || value == "千牛通知"
                || value == "小二通知"
                || value == "服务商通知"
                || value.StartsWith("系统消息：", StringComparison.Ordinal)
                || value.StartsWith("平台消息：", StringComparison.Ordinal);
        }

        private static bool IsGroupSourceToken(string value)
        {
            var token = NormalizeToken(value);
            return token.Contains("group")
                || token.Contains("chatroom")
                || token.Contains("tribe")
                || token == "qun"
                || token.Contains("群聊");
        }

        private static bool IsNonBuyerSourceToken(string value)
        {
            var token = NormalizeToken(value);
            if (token.Length == 0) return false;
            foreach (var marker in NonBuyerSourceTokens)
            {
                if (token.Contains(NormalizeToken(marker))) return true;
            }
            return token.Contains("群聊")
                || token.Contains("小二")
                || token.Contains("服务商")
                || token.Contains("系统通知")
                || token.Contains("平台通知");
        }

        private static bool ContainsStrongIdentityMarker(string value)
        {
            value = Normalize(value);
            if (value.Length == 0) return false;
            foreach (var marker in StrongIdentityMarkers)
            {
                if (value.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static string Join(params string[] values)
        {
            return string.Join("|", (values ?? new string[0]).Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        private static bool SameNonEmpty(string left, string right)
        {
            return left.Length > 0 && right.Length > 0
                && string.Equals(left, right, StringComparison.Ordinal);
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private static string NormalizeToken(string value)
        {
            return Normalize(value).ToLowerInvariant().Replace(" ", string.Empty);
        }
    }

'''
text = replace_once(text, anchor, guard + anchor, "insert non-buyer guard")
old = '''        public static IncomingMessageDecision Evaluate(QNChatMessage message, string messageText, DateTime safetyStartedAt)
        {
            // Refresh the same seller+buyer timeline before deciding how to answer. This loads
'''
new = '''        public static IncomingMessageDecision Evaluate(QNChatMessage message, string messageText, DateTime safetyStartedAt)
        {
            string nonBuyerReason;
            var sellerNick = message == null || message.loginid == null ? string.Empty : message.loginid.nick;
            if (NonBuyerConversationGuard.ShouldBlockMessage(message, sellerNick, messageText, out nonBuyerReason))
            {
                return Skip("[非买家消息]", "已跳过：检测到小二/服务商/1688/群聊或平台系统消息；未调用AI，也未发送回复。 reason=" + nonBuyerReason);
            }

            // Refresh the same seller+buyer timeline before deciding how to answer. This loads
'''
text = replace_once(text, old, new, "incoming safety defense")
write(path, text)

# 3) QN: refuse non-buyer identity before it can poison CurrentBuyer or enter order/AI paths.
path = "src/Bot/ChromeNs/QN.cs"
text = read(path)
old = '''                sellerNick = (sellerNick ?? string.Empty).Trim();
                buyerNick = (buyerNick ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(sellerNick) && string.IsNullOrWhiteSpace(buyerNick)) return;

                if (!string.IsNullOrWhiteSpace(sellerNick) && (_seller == null || _seller.Nick != sellerNick))
'''
new = '''                sellerNick = (sellerNick ?? string.Empty).Trim();
                buyerNick = (buyerNick ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(sellerNick) && string.IsNullOrWhiteSpace(buyerNick)) return;

                string nonBuyerReason;
                if (NonBuyerConversationGuard.ShouldBlockIdentity(sellerNick, buyerNick, out nonBuyerReason))
                {
                    Log.Info("非买家会话身份已拒绝，不更新当前buyer: source=" + source + ", reason=" + nonBuyerReason);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(sellerNick) && (_seller == null || _seller.Nick != sellerNick))
'''
text = replace_once(text, old, new, "active conversation identity guard")
old = '''        private void Cdp_EvShopRobotReceriveNewMessage(object sender, ShopRobotReceriveNewMessageEventArgs e)
        {
            // 这是后台新消息通知，不等于千牛当前可见聊天已经切换。
'''
new = '''        private void Cdp_EvShopRobotReceriveNewMessage(object sender, ShopRobotReceriveNewMessageEventArgs e)
        {
            string nonBuyerReason;
            if (e != null && e.Seller != null && e.Buyer != null
                && NonBuyerConversationGuard.ShouldBlockConversation(e.Seller, e.Buyer, out nonBuyerReason))
            {
                Log.Info("非买家后台消息通知已丢弃，不触发首问/补偿/学习: reason=" + nonBuyerReason);
                return;
            }

            // 这是后台新消息通知，不等于千牛当前可见聊天已经切换。
'''
text = replace_once(text, old, new, "background notification guard")
old = '''        private void Cdp_EvSellerSwitched(object sender, SellerSwitchedEventArgs e)
        {
            if (e == null) return;
            Seller = e.Seller;
            Buyer = e.Buyer;
            CurQN = this;
            SetActiveConversationByNick(e.Seller == null ? string.Empty : e.Seller.Nick, e.Buyer == null ? string.Empty : e.Buyer.Nick, "sellerSwitched");

            if (EvSellerSwitched != null)
'''
new = '''        private void Cdp_EvSellerSwitched(object sender, SellerSwitchedEventArgs e)
        {
            if (e == null) return;
            Seller = e.Seller;
            CurQN = this;
            string nonBuyerReason;
            if (e.Buyer != null && NonBuyerConversationGuard.ShouldBlockConversation(e.Seller, e.Buyer, out nonBuyerReason))
            {
                Log.Info("卖家切换事件携带非买家会话，保留当前真实buyer: reason=" + nonBuyerReason);
                return;
            }
            Buyer = e.Buyer;
            SetActiveConversationByNick(e.Seller == null ? string.Empty : e.Seller.Nick, e.Buyer == null ? string.Empty : e.Buyer.Nick, "sellerSwitched");

            if (EvSellerSwitched != null)
'''
text = replace_once(text, old, new, "seller switch guard")
old = '''        private Task ProcessIncomingMessageAsync(QNChatMessage message)
        {
            if (message == null) return Task.CompletedTask;
            BuyerIdentityAliasService.ObserveMessage(_seller == null ? string.Empty : _seller.Nick, message);
            var messageText = GetMessageText(message);
            var messageKey = IncomingMessageSafety.BuildMessageKey(message, messageText);
'''
new = '''        private Task ProcessIncomingMessageAsync(QNChatMessage message)
        {
            if (message == null) return Task.CompletedTask;
            var messageText = GetMessageText(message);
            string nonBuyerReason;
            if (NonBuyerConversationGuard.ShouldBlockMessage(
                message,
                _seller == null ? string.Empty : _seller.Nick,
                messageText,
                out nonBuyerReason))
            {
                Log.Info("非买家普通入站消息已丢弃，未进入订单/首问/商品链接/AI链: reason=" + nonBuyerReason);
                return Task.CompletedTask;
            }
            BuyerIdentityAliasService.ObserveMessage(_seller == null ? string.Empty : _seller.Nick, message);
            var messageKey = IncomingMessageSafety.BuildMessageKey(message, messageText);
'''
text = replace_once(text, old, new, "foreground message guard")
old = '''        private void Cdp_EvBuyerSwitched(object sender, BuyerSwitchedEventArgs e)
        {
            if (e == null) return;
            Seller = e.Seller;
            Buyer = e.Buyer;
            CurQN = this;
            SetActiveConversationByNick(e.Seller == null ? string.Empty : e.Seller.Nick, e.Buyer == null ? string.Empty : e.Buyer.Nick, "buyerSwitched");
            if (EvBuyerSwitched != null)
'''
new = '''        private void Cdp_EvBuyerSwitched(object sender, BuyerSwitchedEventArgs e)
        {
            if (e == null) return;
            Seller = e.Seller;
            CurQN = this;
            string nonBuyerReason;
            if (e.Buyer != null && NonBuyerConversationGuard.ShouldBlockConversation(e.Seller, e.Buyer, out nonBuyerReason))
            {
                Log.Info("非买家会话切换已拒绝，不污染当前buyer也不触发买家切换订阅: reason=" + nonBuyerReason);
                return;
            }
            Buyer = e.Buyer;
            SetActiveConversationByNick(e.Seller == null ? string.Empty : e.Seller.Nick, e.Buyer == null ? string.Empty : e.Buyer.Nick, "buyerSwitched");
            if (EvBuyerSwitched != null)
'''
text = replace_once(text, old, new, "buyer switch guard")
write(path, text)

# 4) Recovery chain: never auto-switch/replay non-buyer events or parse them as order cards.
path = "src/Bot/ChromeNs/QN.MessageRecovery.cs"
text = read(path)
old = '''        private void ScheduleBackgroundMessageRecovery(ShopRobotReceriveNewMessageEventArgs e)
        {
            if (e == null || e.Seller == null || e.Buyer == null) return;
            var seller = (e.Seller.Nick ?? string.Empty).Trim();
'''
new = '''        private void ScheduleBackgroundMessageRecovery(ShopRobotReceriveNewMessageEventArgs e)
        {
            if (e == null || e.Seller == null || e.Buyer == null) return;
            string nonBuyerReason;
            if (NonBuyerConversationGuard.ShouldBlockConversation(e.Seller, e.Buyer, out nonBuyerReason))
            {
                Log.Info("非买家后台补偿已拒绝，禁止自动切换会话: reason=" + nonBuyerReason);
                return;
            }
            var seller = (e.Seller.Nick ?? string.Empty).Trim();
'''
text = replace_once(text, old, new, "recovery schedule guard")
old = '''        {
            if (message == null) return;
            var text = GetMessageText(message);
            if (IsPotentialRecoveredOrderCard(message))
'''
new = '''        {
            if (message == null) return;
            var text = GetMessageText(message);
            string nonBuyerReason;
            if (NonBuyerConversationGuard.ShouldBlockMessage(message, seller, text, out nonBuyerReason))
            {
                Log.Info("后台补偿非买家消息已丢弃，未进入订单/回复链: reason=" + nonBuyerReason);
                return;
            }
            if (IsPotentialRecoveredOrderCard(message))
'''
text = replace_once(text, old, new, "recovered dispatcher guard")
old = '''        {
            if (message == null) return Task.CompletedTask;
            var messageText = GetMessageText(message);
            var messageKey = IncomingMessageSafety.BuildMessageKey(message, messageText);
'''
new = '''        {
            if (message == null) return Task.CompletedTask;
            var messageText = GetMessageText(message);
            string nonBuyerReason;
            if (NonBuyerConversationGuard.ShouldBlockMessage(message, sellerNick, messageText, out nonBuyerReason))
            {
                Log.Info("后台补偿非买家买家候选已丢弃，未进入首问/订单/AI链: reason=" + nonBuyerReason);
                return Task.CompletedTask;
            }
            var messageKey = IncomingMessageSafety.BuildMessageKey(message, messageText);
'''
text = replace_once(text, old, new, "recovered buyer guard")
old = '''        private static bool IsRecoveredBuyerMessageForTarget(QNChatMessage message, string seller, string buyer)
        {
            if (message == null || message.fromid == null) return false;
            // Remote history is fetched only after the target conversation itself has been verified.
'''
new = '''        private static bool IsRecoveredBuyerMessageForTarget(QNChatMessage message, string seller, string buyer)
        {
            if (message == null || message.fromid == null) return false;
            string nonBuyerReason;
            if (NonBuyerConversationGuard.ShouldBlockMessage(message, seller, GetMessageText(message), out nonBuyerReason)) return false;
            // Remote history is fetched only after the target conversation itself has been verified.
'''
text = replace_once(text, old, new, "recovered target guard")
write(path, text)

# 5) First-inquiry background ccode fast path.
path = "src/Bot/ChromeNs/FirstInquiryStreamingGuard.cs"
text = read(path)
old = '''                var active = JsonConvert.DeserializeObject<ActiveLocalUser>(e.Value);
                var seller = active == null || active.LoginID == null
'''
new = '''                var active = JsonConvert.DeserializeObject<ActiveLocalUser>(e.Value);
                string nonBuyerReason;
                if (active != null && active.LoginID != null && active.Conversation != null
                    && NonBuyerConversationGuard.ShouldBlockConversation(active.LoginID, active.Conversation, out nonBuyerReason))
                {
                    Log.Info("首条咨询后台通知快路径已拒绝非买家会话: reason=" + nonBuyerReason);
                    return;
                }
                var seller = active == null || active.LoginID == null
'''
text = replace_once(text, old, new, "first inquiry notification guard")
old = '''            seller = (seller ?? string.Empty).Trim();
            buyer = BuyerIdentityAliasService.ResolveInternalNick(seller, buyer);
            ccode = (ccode ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0 || ccode.Length == 0) return;
'''
new = '''            seller = (seller ?? string.Empty).Trim();
            buyer = BuyerIdentityAliasService.ResolveInternalNick(seller, buyer);
            ccode = (ccode ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0 || ccode.Length == 0) return;
            string nonBuyerReason;
            if (NonBuyerConversationGuard.ShouldBlockIdentity(seller, buyer, out nonBuyerReason))
            {
                Log.Info("首条咨询快路径已拒绝非买家身份: reason=" + nonBuyerReason);
                return;
            }
'''
text = replace_once(text, old, new, "first inquiry schedule identity guard")
old = '''            var recentBuyerMessages = (messages ?? new List<QNChatMessage>())
                .Where(m => m != null && m.fromid != null && m.toid != null)
                .Where(m => m.toid.nick == seller
                    && BuyerIdentityAliasService.AreEquivalent(seller, m.fromid.nick, buyer))
'''
new = '''            var recentBuyerMessages = (messages ?? new List<QNChatMessage>())
                .Where(m => IsReplyableFirstInquiryCandidate(m, seller, buyer))
'''
text = replace_once(text, old, new, "first inquiry history candidate guard")
old = '''            var first = recentBuyerMessages.FirstOrDefault(IsRealFirstInquiryMessage);
'''
new = '''            var first = recentBuyerMessages.FirstOrDefault(m => IsRealFirstInquiryMessage(m, seller));
'''
text = replace_once(text, old, new, "first inquiry predicate signature")
old = '''        private static bool IsRealFirstInquiryMessage(QNChatMessage message)
        {
            if (message == null) return false;
            var text = GetMessageText(message);
'''
new = '''        private static bool IsReplyableFirstInquiryCandidate(QNChatMessage message, string seller, string buyer)
        {
            if (message == null || message.fromid == null || message.toid == null) return false;
            string nonBuyerReason;
            if (NonBuyerConversationGuard.ShouldBlockMessage(message, seller, GetMessageText(message), out nonBuyerReason)) return false;
            return message.toid.nick == seller
                && BuyerIdentityAliasService.AreEquivalent(seller, message.fromid.nick, buyer);
        }

        private static bool IsRealFirstInquiryMessage(QNChatMessage message, string seller)
        {
            if (message == null) return false;
            var text = GetMessageText(message);
            string nonBuyerReason;
            if (NonBuyerConversationGuard.ShouldBlockMessage(message, seller, text, out nonBuyerReason)) return false;
'''
text = replace_once(text, old, new, "first inquiry real-message guard")
write(path, text)

# 6) Returning-buyer fast reply bridge.
path = "src/Bot/ChromeNs/ReturningBuyerFirstReplyBridge.Messages.cs"
text = read(path)
old = '''                    if (buyer.Length == 0 || to != seller || buyer == seller) continue;
                    var question = MessageText(m);
                    var prior = ConversationContextStore.GetRecentTurns(seller, buyer, question, 24)
'''
new = '''                    if (buyer.Length == 0 || to != seller || buyer == seller) continue;
                    var question = MessageText(m);
                    string nonBuyerReason;
                    if (NonBuyerConversationGuard.ShouldBlockMessage(m, seller, question, out nonBuyerReason))
                    {
                        Log.Info("回访首答已跳过非买家消息: reason=" + nonBuyerReason);
                        continue;
                    }
                    var prior = ConversationContextStore.GetRecentTurns(seller, buyer, question, 24)
'''
text = replace_once(text, old, new, "returning buyer guard")
write(path, text)

# 7) BuyerSessionAgent: non-buyer traffic must not become buyer learning evidence.
path = "src/Bot/ChromeNs/BuyerSessionAgentRuntimeBridge.cs"
text = read(path)
old = '''                    qn.EvShopRobotReceriveNewMessage += (sender, e) =>
                    {
                        if (e == null || e.Seller == null || e.Buyer == null) return;
                        var now = DateTime.Now;
'''
new = '''                    qn.EvShopRobotReceriveNewMessage += (sender, e) =>
                    {
                        if (e == null || e.Seller == null || e.Buyer == null) return;
                        string nonBuyerReason;
                        if (NonBuyerConversationGuard.ShouldBlockConversation(e.Seller, e.Buyer, out nonBuyerReason))
                        {
                            Log.Info("BuyerSessionAgent忽略非买家后台通知: reason=" + nonBuyerReason);
                            return;
                        }
                        var now = DateTime.Now;
'''
text = replace_once(text, old, new, "agent background guard")
old = '''            var sellerMessage = string.Equals(from, seller, StringComparison.Ordinal);
            var buyer = sellerMessage ? to : from;
            if (buyer.Length == 0 || string.Equals(buyer, seller, StringComparison.Ordinal)) return;

            var text = GetMessageText(message);
            var display = IncomingMessageSafety.GetDisplayText(message, text);
'''
new = '''            var sellerMessage = string.Equals(from, seller, StringComparison.Ordinal);
            var text = GetMessageText(message);
            string nonBuyerReason;
            if (!sellerMessage && NonBuyerConversationGuard.ShouldBlockMessage(message, seller, text, out nonBuyerReason))
            {
                Log.Info("BuyerSessionAgent忽略非买家原始消息，禁止污染学习时间线: reason=" + nonBuyerReason);
                return;
            }
            var buyer = sellerMessage ? to : from;
            if (buyer.Length == 0 || string.Equals(buyer, seller, StringComparison.Ordinal)) return;

            var display = IncomingMessageSafety.GetDisplayText(message, text);
'''
text = replace_once(text, old, new, "agent raw message guard")
write(path, text)

# 8) Focused static regression tests. These encode architectural order, not just output strings.
test = r'''from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_shared_non_buyer_guard_uses_identity_and_structured_source_not_urls():
    code = read("src/Bot/ChromeNs/IncomingMessageSafety.cs")
    guard = code[code.index("internal static class NonBuyerConversationGuard"):code.index("internal static class IncomingMessageSafety")]
    assert "self_identity" in guard
    assert "行业小二" in guard
    assert "服务商" in guard
    assert "cnalichn" in guard
    assert "1688" in guard
    assert "group" in guard
    assert "chatroom" in guard
    assert "platform_system_card" in guard
    assert "http://" not in guard
    assert "https://" not in guard
    assert "Regex" not in guard


def test_conversation_model_preserves_qianniu_source_metadata():
    code = read("src/DbEntity/Response/LocalUser.cs")
    for field in ("targetType", "conversationType", "scene", "category", "source", "channel"):
        assert f'JsonProperty("{field}")' in code


def test_foreground_guard_runs_before_alias_order_and_smart_reply():
    code = read("src/Bot/ChromeNs/QN.cs")
    start = code.index("private Task ProcessIncomingMessageAsync")
    end = code.index("private async Task ProcessBuyerBurstAsync")
    block = code[start:end]
    guard = block.index("NonBuyerConversationGuard.ShouldBlockMessage")
    alias = block.index("BuyerIdentityAliasService.ObserveMessage")
    order = block.index("OrderPlacedAutoReplyService.TryCreatePlan")
    safety = block.index("IncomingMessageSafety.Evaluate")
    assert guard < alias < order < safety


def test_conversation_switches_and_background_notifications_do_not_poison_current_buyer():
    code = read("src/Bot/ChromeNs/QN.cs")
    buyer_switch = code[code.index("private void Cdp_EvBuyerSwitched"):code.index("public static QN GetByNick")]
    assert buyer_switch.index("ShouldBlockConversation") < buyer_switch.index("Buyer = e.Buyer")
    seller_switch = code[code.index("private void Cdp_EvSellerSwitched"):code.index("private Task ProcessIncomingMessageAsync")]
    assert seller_switch.index("ShouldBlockConversation") < seller_switch.index("Buyer = e.Buyer")
    background = code[code.index("private void Cdp_EvShopRobotReceriveNewMessage"):code.index("private void Cdp_EvSellerSwitched")]
    assert background.index("ShouldBlockConversation") < background.index("ScheduleBackgroundMessageRecovery")
    active = code[code.index("public void SetActiveConversationByNick"):code.index("private void Cdp_EvShopRobotReceriveNewMessage")]
    assert "ShouldBlockIdentity" in active


def test_first_inquiry_and_returning_buyer_fast_paths_share_guard():
    fast = read("src/Bot/ChromeNs/FirstInquiryStreamingGuard.cs")
    assert "ShouldBlockConversation(active.LoginID, active.Conversation" in fast
    assert "ShouldBlockIdentity(seller, buyer" in fast
    assert "IsReplyableFirstInquiryCandidate" in fast
    assert "ShouldBlockMessage(message, seller" in fast
    returning = read("src/Bot/ChromeNs/ReturningBuyerFirstReplyBridge.Messages.cs")
    assert "NonBuyerConversationGuard.ShouldBlockMessage" in returning


def test_recovery_guard_runs_before_recovered_order_card_and_buyer_dedupe():
    code = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")
    dispatch = code[code.index("private async Task ProcessRecoveredMessageWithKnownBuyerAsync"):code.index("private Task ProcessRecoveredBuyerMessageAfterMissAsync")]
    assert dispatch.index("ShouldBlockMessage") < dispatch.index("IsPotentialRecoveredOrderCard")
    buyer = code[code.index("private Task ProcessRecoveredBuyerMessageAfterMissAsync"):code.index("private static bool IsPotentialRecoveredOrderCard")]
    assert buyer.index("ShouldBlockMessage") < buyer.index("_handledBuyerMessageDeduplicator.TryAccept")
    assert "ShouldBlockConversation(e.Seller, e.Buyer" in code


def test_non_buyer_events_do_not_pollute_buyer_session_learning():
    code = read("src/Bot/ChromeNs/BuyerSessionAgentRuntimeBridge.cs")
    assert "BuyerSessionAgent忽略非买家后台通知" in code
    observe = code[code.index("private static void ObserveMessage"):code.index("private static BuyerSessionEventKind ClassifyBuyerEvent")]
    assert observe.index("ShouldBlockMessage") < observe.index("OrderCardParser.TryParse")


def test_real_buyer_product_links_remain_supported_after_source_guard():
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    safety = read("src/Bot/ChromeNs/IncomingMessageSafety.cs")
    assert "ConversationContextStore.IsProductLink(message, messageText)" in safety
    assert "RegisterProductLinkReply" in safety
    assert "https?://" in context
'''
write("tests/test_non_buyer_conversation_guard_static.py", test)

print("non-buyer conversation guard patch applied")
