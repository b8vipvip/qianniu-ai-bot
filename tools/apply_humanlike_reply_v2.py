from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]


def read_text(path):
    raw = path.read_bytes()
    return raw.decode("utf-8-sig"), raw.startswith(b"\xef\xbb\xbf")


def write_text(path, text, bom):
    path.write_bytes(text.encode("utf-8-sig" if bom else "utf-8"))


def replace_once(path_string, old, new):
    path = ROOT / path_string
    text, bom = read_text(path)
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path_string}: expected one exact match, got {count}")
    write_text(path, text.replace(old, new, 1), bom)


def regex_once(path_string, pattern, replacement):
    path = ROOT / path_string
    text, bom = read_text(path)
    updated, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count != 1:
        raise RuntimeError(f"{path_string}: expected one regex match, got {count}")
    write_text(path, updated, bom)


replace_once(
    "src/Directory.Build.targets",
    '''  <ItemGroup Condition="Exists('$(MSBuildProjectDirectory)\\ChromeNs\\ReplyDeduplicationService.cs')">
    <Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\ReplyDeduplicationService.cs" />
  </ItemGroup>
</Project>''',
    '''  <ItemGroup Condition="Exists('$(MSBuildProjectDirectory)\\ChromeNs\\ReplyDeduplicationService.cs')">
    <Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\ReplyDeduplicationService.cs" />
  </ItemGroup>
  <ItemGroup Condition="Exists('$(MSBuildProjectDirectory)\\ChromeNs\\BuyerMessageBurstCoordinator.cs')">
    <Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\BuyerMessageBurstCoordinator.cs" />
  </ItemGroup>
</Project>''')

replace_once(
    "src/Bot/ChromeNs/IncomingMessageSafety.cs",
    '''        public static string BuildMessageKey(QNChatMessage message, string messageText)
        {''',
    '''        public static string GetDisplayText(QNChatMessage message, string messageText)
        {
            if (ConversationContextStore.IsProductLink(message, messageText))
            {
                return string.IsNullOrWhiteSpace(messageText) ? "[商品链接]" : messageText.Trim();
            }
            var unsupportedType = DetectUnsupportedType(message, messageText);
            if (!string.IsNullOrWhiteSpace(unsupportedType)) return "[" + unsupportedType + "]";
            var text = (messageText ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(text) ? "[空白或未知消息]" : text;
        }

        public static bool IsMediaPlaceholder(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value == "[图片]"
                || value == "[视频]"
                || value == "[语音]"
                || value == "[表情]"
                || value == "[文件]"
                || value == "[位置]";
        }

        public static string BuildMessageKey(QNChatMessage message, string messageText)
        {''')

replace_once(
    "src/Bot/ChromeNs/IncomingMessageSafety.cs",
    '''            if (HasExtension(fileId, AudioExtensions) || HasExtension(url, AudioExtensions) || ContainsMarker(combined, "语音", "音频", "voice", "audio")) return "语音";
            if (ContainsMarker(combined, "位置", "定位", "location")) return "位置";''',
    '''            if (HasExtension(fileId, AudioExtensions) || HasExtension(url, AudioExtensions) || ContainsMarker(combined, "语音", "音频", "voice", "audio")) return "语音";
            if (ContainsMarker(combined, "表情", "emoji", "emotion", "face")
                || combined.Contains("发送了一个表情")
                || combined.Contains("动态表情")) return "表情";
            if (ContainsMarker(combined, "位置", "定位", "location")) return "位置";''')

replace_once(
    "src/Bot/ChromeNs/ConversationContextStore.cs",
    '''                if (!string.IsNullOrWhiteSpace(normalizedCurrent))
                {
                    for (var i = eligible.Count - 1; i >= 0; i--)
                    {
                        var item = eligible[i];
                        if (item.Role == "user" && NormalizeText(item.Text) == normalizedCurrent)
                        {
                            eligible.RemoveAt(i);
                            break;
                        }
                    }
                }''',
    '''                if (!string.IsNullOrWhiteSpace(normalizedCurrent))
                {
                    var currentParts = new HashSet<string>(
                        (currentQuestion ?? string.Empty)
                            .Split(new[] { '\\r', '\\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(NormalizeText)
                            .Where(x => x.Length > 0),
                        StringComparer.Ordinal);
                    for (var i = eligible.Count - 1; i >= 0; i--)
                    {
                        var item = eligible[i];
                        if (item.Role == "assistant") break;
                        var itemText = NormalizeText(item.Text);
                        if (item.Role == "user"
                            && (itemText == normalizedCurrent
                                || currentParts.Contains(itemText)
                                || (itemText.Length >= 2 && normalizedCurrent.Contains(itemText))))
                        {
                            eligible.RemoveAt(i);
                        }
                    }
                }''')

replace_once(
    "src/Bot/ChromeNs/ConversationContextStore.cs",
    '''            var cleanText = (text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cleanText))
            {
                if (IsProductLink(message, text)) cleanText = "[商品链接]";
                else return;
            }''',
    '''            var cleanText = IncomingMessageSafety.GetDisplayText(message, text);
            if (string.IsNullOrWhiteSpace(cleanText)
                || string.Equals(cleanText, "[空白或未知消息]", StringComparison.Ordinal))
            {
                return;
            }''')

replace_once(
    "src/Bot/ChromeNs/VisionReplyTask.cs",
    '''        public QNChatMessage Message { get; set; }
        public DateTime CreatedAt { get; set; }''',
    '''        public QNChatMessage Message { get; set; }
        public string CombinedQuestion { get; set; }
        public bool DeferLearningUntilDelivered { get; set; }
        public DateTime CreatedAt { get; set; }''')

replace_once(
    "src/Bot/ChromeNs/VisionRequestService.cs",
    '''            var timeline = ConversationContextStore.BuildTimelineText(task.SellerNick, task.BuyerNick, "[图片]", 16);
            var prompt = UserPrompt;
            if (!string.IsNullOrWhiteSpace(timeline))''',
    '''            var currentQuestion = string.IsNullOrWhiteSpace(task.CombinedQuestion)
                ? "[图片]"
                : task.CombinedQuestion.Trim();
            var timeline = ConversationContextStore.BuildTimelineText(task.SellerNick, task.BuyerNick, currentQuestion, 16);
            var prompt = UserPrompt;
            if (!string.Equals(currentQuestion, "[图片]", StringComparison.Ordinal))
            {
                prompt += "\\n\\n买家本轮连续发送的消息如下，换行代表先后顺序。图片和这些文字属于同一轮，请合并理解后只回复一次：\\n" + currentQuestion;
            }
            if (!string.IsNullOrWhiteSpace(timeline))''')

replace_once(
    "src/Bot/ChromeNs/VisionRequestService.cs",
    '''                            KnowledgeLearningService.RegisterAnswerSource(task.SellerNick, task.BuyerNick, "[图片]", result.Answer, "AI生成");
                            KnowledgeLearningService.QueueLearn("买家发送图片。" + (string.IsNullOrWhiteSpace(timeline) ? string.Empty : "\\n" + timeline), result.Answer, "视觉AI", task.SellerNick, task.BuyerNick);
                            return result;''',
    '''                            KnowledgeLearningService.RegisterAnswerSource(task.SellerNick, task.BuyerNick, currentQuestion, result.Answer, "AI生成");
                            if (!task.DeferLearningUntilDelivered)
                            {
                                KnowledgeLearningService.QueueLearn(
                                    "买家本轮消息：" + currentQuestion + (string.IsNullOrWhiteSpace(timeline) ? string.Empty : "\\n" + timeline),
                                    result.Answer,
                                    "视觉AI",
                                    task.SellerNick,
                                    task.BuyerNick);
                            }
                            return result;''')

replace_once(
    "src/Bot/ChromeNs/MyOpenAI.cs",
    '''        private static string BuildSystemPrompt(string prompt)
        {
            var basePrompt = string.IsNullOrWhiteSpace(prompt) ? DefaultSystemPrompt : prompt.Trim();
            if (basePrompt.Contains("固定回复规则")) return basePrompt + TimelineGuard;
            return basePrompt + ReplyStyleGuard + TimelineGuard;
        }''',
    '''        private static string HumanConversationGuard
        {
            get
            {
                return "\\n\\n真人客服式会话规则：买家可能把一句话拆成多条发送，换行表示同一轮消息的先后顺序。先判断这些消息是同一句拆分、补充信息、纠正前文、连续追问、重复催促、寒暄后提问、只回复数字/型号，还是多个相关问题；不要按每一行逐条作答，只发送一条合并后的自然回复。后一条明确纠正前文时，以后一条为准；同义重复和连续问号只回应一次；寒暄与实际问题同时出现时直接处理实际问题；买家说“好的、嗯、知道了、谢谢、解决了”时简短收尾，不重新讲方案；信息不足时只追问一个最关键的信息；多个相关问题可在一句到两句内合并处理，多个无关问题优先处理最新或最影响交易的一项，再自然询问另一项。生成答案后如果买家又发来新消息，旧答案应作废，结合新消息重新生成。看到[图片]、[视频]、[语音]、[表情]等占位符时，不得假装看懂未解析的内容。";
            }
        }

        private static string BuildSystemPrompt(string prompt)
        {
            var basePrompt = string.IsNullOrWhiteSpace(prompt) ? DefaultSystemPrompt : prompt.Trim();
            if (basePrompt.Contains("固定回复规则")) return basePrompt + TimelineGuard + HumanConversationGuard;
            return basePrompt + ReplyStyleGuard + TimelineGuard + HumanConversationGuard;
        }''')

replace_once(
    "src/Bot/ChromeNs/MyOpenAI.cs",
    '''        public static string GetAnswer(string seller, string buyer, string question)
        {
            try''',
    '''        public static string GetAnswer(string seller, string buyer, string question)
        {
            return GetAnswer(seller, buyer, question, false);
        }

        internal static string GetAnswer(
            string seller,
            string buyer,
            string question,
            bool deferLearningUntilDelivered)
        {
            try''')

replace_once(
    "src/Bot/ChromeNs/MyOpenAI.cs",
    '''                    var offHoursSource = manualDecision.UseAiReply ? "AI生成" : "本地";''',
    '''                    var offHoursSource = "转人工回复";''')

replace_once(
    "src/Bot/ChromeNs/MyOpenAI.cs",
    '''                        if (contextualKnowledge == null)
                        {
                            KnowledgeLearningService.QueueLearn(question, finalAnswer, "AI生成", seller, buyer);
                        }''',
    '''                        if (contextualKnowledge == null && !deferLearningUntilDelivered)
                        {
                            KnowledgeLearningService.QueueLearn(question, finalAnswer, "AI生成", seller, buyer);
                        }''')

replace_once(
    "src/Bot/ChromeNs/HandoffNotificationService.cs",
    '''                + "\\n原因：" + Safe(decision.Reason, 200)
                + "\\n问题：" + Safe(question, 500);''',
    '''                + "\\n原因：" + Safe(decision.Reason, 200)
                + "\\n买家消息：\\n" + SafeBuyerMessage(question, 1200);''')

replace_once(
    "src/Bot/ChromeNs/HandoffNotificationService.cs",
    '''        private static string Safe(string value, int max)
        {''',
    '''        private static string SafeBuyerMessage(string value, int max)
        {
            var lines = (value ?? string.Empty)
                .Replace("\\r", string.Empty)
                .Split(new[] { '\\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => Safe(x, 300))
                .Where(x => x.Length > 0)
                .Take(10)
                .ToList();
            var text = lines.Count == 0 ? "[空白或未知消息]" : string.Join("\\n", lines);
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }

        private static string Safe(string value, int max)
        {''')

replace_once(
    "services/api-control-plane/wecom_bridge.py",
    '''import os
import secrets''',
    '''import os
import re
import secrets''')

replace_once(
    "services/api-control-plane/wecom_bridge.py",
    '''def split_users(value: str) -> Tuple[str, ...]:''',
    '''def safe_buyer_message(value: Any, limit: int = 2000) -> str:
    text = str(value or "").replace("\\r", "")
    text = re.sub(r"(?<!\\d)1\\d{10}(?!\\d)", "[手机号]", text)
    text = re.sub(r"(?i)sk-[a-z0-9_-]{12,}", "[API_KEY]", text)
    lines = []
    for raw in text.split("\\n"):
        line = re.sub(r"[ \\t]+", " ", raw).strip()
        if line and line not in lines:
            lines.append(line[:500])
        if len(lines) >= 10:
            break
    result = "\\n".join(lines) if lines else "[空白或未知消息]"
    return result if len(result) <= limit else result[:limit] + "..."


def split_users(value: str) -> Tuple[str, ...]:''')

replace_once(
    "services/api-control-plane/wecom_bridge.py",
    '''                safe_text(data.question, 2000),''',
    '''                safe_buyer_message(data.question, 2000),''')

replace_once(
    "services/api-control-plane/wecom_bridge.py",
    '''        + "\\n原因：" + safe_text(data.reason or "测试企业微信应用消息双向链路", 200)
        + "\\n问题：" + safe_text(data.question or "这是一条测试通知", 500)
        + "\\n\\n回复格式：" + ticket_id + " 这里填写给买家的回复"''',
    '''        + "\\n原因：" + safe_text(data.reason or "测试企业微信应用消息双向链路", 200)
        + "\\n买家消息：\\n" + safe_buyer_message(data.question or "这是一条测试通知", 1600)
        + "\\n\\n回复格式：" + ticket_id + " 这里填写给买家的回复"''')

regex_once(
    "src/Bot/ChromeNs/QN.cs",
    r'''        private async Task ProcessIncomingMessageAsync\(QNChatMessage message\)\n        \{.*?\n        \}\n\n        private void AddSkippedConversation''',
    '''        private Task ProcessIncomingMessageAsync(QNChatMessage message)
        {
            if (message == null) return Task.CompletedTask;
            var messageText = GetMessageText(message);
            var messageKey = IncomingMessageSafety.BuildMessageKey(message, messageText);
            if (!_incomingMessageDeduplicator.TryAccept(messageKey))
            {
                Log.Info("重复消息已跳过: key=" + messageKey);
                return Task.CompletedTask;
            }

            ConversationContextStore.RefreshAndRecord(message, messageText);

            if (IsSellerMessage(message))
            {
                RecordSellerEcho(message.toid.nick, messageText);
                return Task.CompletedTask;
            }
            if (!IsBuyerMessage(message)) return Task.CompletedTask;

            var sellerNick = message.toid.nick;
            var buyerNick = message.fromid.nick;
            var decision = IncomingMessageSafety.Evaluate(message, messageText, _messageSafetyStartedAt);
            var displayQuestion = IncomingMessageSafety.GetDisplayText(message, messageText);
            var visionDecision = VisionMessageDecision.Decide(
                message,
                messageText,
                decision,
                AiEndpointStore.GetVisionEnabledEndpoints());

            if (!Params.Robot.CanUseRobotReal)
            {
                AddSkippedConversation(sellerNick, buyerNick, displayQuestion, "Bot已停用，未调用AI，也未发送给买家。");
                return Task.CompletedTask;
            }

            if (visionDecision.Kind == VisionDecisionKind.Skip
                && !IncomingMessageSafety.IsMediaPlaceholder(displayQuestion))
            {
                AddSkippedConversation(sellerNick, buyerNick, visionDecision.QuestionLabel, visionDecision.Note);
                Log.Info("买家消息安全跳过: buyer=" + buyerNick + ", reason=" + visionDecision.Note);
                return Task.CompletedTask;
            }

            _buyerMessageBurstCoordinator.Enqueue(new BuyerMessageBurstItem
            {
                SellerNick = sellerNick,
                BuyerNick = buyerNick,
                MessageKey = messageKey,
                DisplayText = displayQuestion,
                Message = message,
                SafetyDecision = decision,
                VisionDecision = visionDecision,
                SortValue = IncomingMessageSafety.GetSortValue(message),
                ReceivedAt = DateTime.Now
            });
            return Task.CompletedTask;
        }

        private async Task ProcessBuyerBurstAsync(BuyerMessageBurstLease lease)
        {
            var burst = lease == null ? null : lease.Burst;
            if (burst == null || burst.Items.Count < 1 || string.IsNullOrWhiteSpace(burst.CombinedQuestion)) return;

            if (!burst.HasReplyableItem)
            {
                if (!lease.IsCurrent) return;
                var note = "已合并收到买家的媒体消息，但当前未配置对应内容理解能力，未自动回复。";
                AddSkippedConversation(burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, note);
                Log.Info("买家媒体消息合并跳过: buyer=" + burst.BuyerNick + ", messages=" + burst.CombinedQuestion.Replace("\\n", " | "));
                return;
            }

            var visionItem = burst.LatestVisionItem;
            if (visionItem != null)
            {
                await ProcessVisionBurstAsync(lease, visionItem);
                return;
            }
            await ProcessTextBurstAsync(lease);
        }

        private async Task ProcessTextBurstAsync(BuyerMessageBurstLease lease)
        {
            var burst = lease.Burst;
            var autoSend = Params.Robot.GetIsAutoReply();
            var answer = await Task.Run(() => MyOpenAI.GetAnswer(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                true));

            if (!lease.IsCurrent)
            {
                Log.Info("买家在AI生成期间发送了新消息，旧文本草稿已作废。buyer=" + burst.BuyerNick);
                return;
            }

            var deduplication = ReplyDeduplicationService.EnsureDistinct(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                answer);
            answer = deduplication.Answer;

            if (!await lease.ConfirmStableAsync(450))
            {
                Log.Info("发送前发现买家补充了新消息，旧文本答案未展示也未发送。buyer=" + burst.BuyerNick);
                return;
            }

            var answerSource = KnowledgeLearningService.ResolveAnswerSource(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                answer);
            var conversationCtl = Desk.Inst == null
                ? null
                : Desk.Inst.AddConversation(
                    burst.SellerNick,
                    burst.BuyerNick,
                    burst.CombinedQuestion,
                    answer,
                    autoSend,
                    answerSource);
            if (!autoSend) return;

            if (string.IsNullOrWhiteSpace(answer) || answer.StartsWith("错误："))
            {
                if (conversationCtl != null) conversationCtl.SetSendResult(false, "未发送：AI错误");
                return;
            }

            if (!lease.IsCurrent)
            {
                if (conversationCtl != null) conversationCtl.SetSendResult(false, "未发送：买家刚刚补充了新消息，正在重新组织回复");
                return;
            }

            var sendOk = await SendTextWithRetryAsync(burst.BuyerNick, answer, 1);
            if (sendOk)
            {
                ReplyDeduplicationService.RememberDelivered(burst.SellerNick, burst.BuyerNick, answer);
                if (string.Equals(answerSource, "AI生成", StringComparison.Ordinal))
                {
                    KnowledgeLearningService.QueueLearn(
                        burst.CombinedQuestion,
                        answer,
                        "AI生成",
                        burst.SellerNick,
                        burst.BuyerNick);
                }
            }
            if (conversationCtl != null)
            {
                conversationCtl.SetSendResult(sendOk, sendOk ? "已发送（合并本轮买家消息）" : "发送失败：目标买家会话未确认或发送未完成");
            }
        }

        private async Task ProcessVisionBurstAsync(
            BuyerMessageBurstLease lease,
            BuyerMessageBurstItem visionItem)
        {
            var burst = lease.Burst;
            var autoSend = Params.Robot.GetIsAutoReply();
            var task = new VisionReplyTask
            {
                SellerNick = burst.SellerNick,
                BuyerNick = burst.BuyerNick,
                MessageKey = visionItem.MessageKey,
                Message = visionItem.Message,
                CombinedQuestion = burst.CombinedQuestion,
                DeferLearningUntilDelivered = true
            };
            var result = await _visionRequestService.ExecuteAsync(task, CancellationToken.None);
            if (!lease.IsCurrent)
            {
                Log.Info("买家在视觉AI生成期间发送了新消息，旧视觉草稿已作废。buyer=" + burst.BuyerNick);
                return;
            }

            if (!result.Success || string.IsNullOrWhiteSpace(result.Answer))
            {
                var note = "已跳过：" + (string.IsNullOrWhiteSpace(result.Error) ? "视觉识别失败" : result.Error) + "，未向买家发送消息。";
                AddSkippedConversation(burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, note);
                Log.Info("视觉消息跳过: seller=" + burst.SellerNick + ", buyer=" + burst.BuyerNick + ", messageId=" + visionItem.MessageKey + ", endpoint=" + result.EndpointName + ", model=" + result.VisionModel + ", latencyMs=" + result.LatencyMs + ", reason=" + result.Error);
                return;
            }

            var deduplication = ReplyDeduplicationService.EnsureDistinct(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                result.Answer);
            var answer = deduplication.Answer;
            if (!await lease.ConfirmStableAsync(450))
            {
                Log.Info("发送前发现买家补充了新消息，旧视觉答案未展示也未发送。buyer=" + burst.BuyerNick);
                return;
            }

            var source = deduplication.Regenerated && !string.IsNullOrWhiteSpace(deduplication.Source)
                ? deduplication.Source
                : KnowledgeLearningService.ResolveAnswerSource(
                    burst.SellerNick,
                    burst.BuyerNick,
                    burst.CombinedQuestion,
                    answer);
            if (string.IsNullOrWhiteSpace(source)) source = "AI生成";
            var ctl = Desk.Inst == null
                ? null
                : Desk.Inst.AddConversation(
                    burst.SellerNick,
                    burst.BuyerNick,
                    burst.CombinedQuestion,
                    answer,
                    autoSend,
                    source);
            if (!autoSend) return;
            if (!lease.IsCurrent)
            {
                if (ctl != null) ctl.SetSendResult(false, "未发送：买家刚刚补充了新消息，正在重新组织回复");
                return;
            }

            var sendOk = await SendTextWithRetryAsync(burst.BuyerNick, answer, 1);
            if (sendOk)
            {
                ReplyDeduplicationService.RememberDelivered(burst.SellerNick, burst.BuyerNick, answer);
                KnowledgeLearningService.QueueLearn(
                    burst.CombinedQuestion,
                    answer,
                    "视觉AI",
                    burst.SellerNick,
                    burst.BuyerNick);
            }
            if (ctl != null) ctl.SetSendResult(sendOk, sendOk ? "已发送（合并图片与本轮消息）" : "识别完成，但目标买家会话未确认，未发送。");
        }

        private void AddSkippedConversation''')

replace_once(
    "src/Bot/ChromeNs/QN.cs",
    '''        private readonly VisionRequestService _visionRequestService = new VisionRequestService();''',
    '''        private readonly VisionRequestService _visionRequestService = new VisionRequestService();
        private readonly BuyerMessageBurstCoordinator _buyerMessageBurstCoordinator;''')

replace_once(
    "src/Bot/ChromeNs/QN.cs",
    '''        public QN(LocalUser seller)
        {
            this._seller = seller;
            this.rpa = new QNRpa(this);
        }''',
    '''        public QN(LocalUser seller)
        {
            this._seller = seller;
            this.rpa = new QNRpa(this);
            this._buyerMessageBurstCoordinator = new BuyerMessageBurstCoordinator(ProcessBuyerBurstAsync);
        }''')

replace_once(
    "src/Bot/ChromeNs/QN.cs",
    '''                // GetNewMsg 有时一次返回同一买家的多条未读消息。只处理该批次最新一条，避免启动或网络恢复时连续轰炸买家。
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
                }''',
    '''                // 同一批次和随后几秒到达的消息全部进入按买家隔离的聚合器。
                // 聚合器只在买家停止输入后生成一次答案，不再丢弃较早的短片段。
                foreach (var message in messages)
                {
                    await ProcessIncomingMessageAsync(message);
                    await Task.Delay(30);
                }''')

static_test = ROOT / "tests/test_humanlike_burst_static.py"
static_test.write_text('''from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_buyer_message_burst_coordinator_is_in_build():
    targets = text("src/Directory.Build.targets")
    coordinator = text("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "BuyerMessageBurstCoordinator.cs" in targets
    assert "QuietDelayMilliseconds" in coordinator
    assert "ConfirmStableAsync" in coordinator
    assert "买家本轮连续消息" in coordinator


def test_qn_ingests_all_messages_and_invalidates_stale_drafts():
    qn = text("src/Bot/ChromeNs/QN.cs")
    assert "只处理该批次最新一条" not in qn
    assert "_buyerMessageBurstCoordinator.Enqueue" in qn
    assert "旧文本草稿已作废" in qn
    assert "旧视觉草稿已作废" in qn
    assert "ConfirmStableAsync(450)" in qn
    assert "deferLearningUntilDelivered" in text("src/Bot/ChromeNs/MyOpenAI.cs")


def test_media_placeholders_and_human_conversation_rules():
    safety = text("src/Bot/ChromeNs/IncomingMessageSafety.cs")
    prompt = text("src/Bot/ChromeNs/MyOpenAI.cs")
    assert "[图片]" in safety and "[视频]" in safety
    assert "[语音]" in safety and "[表情]" in safety
    assert "不要按每一行逐条作答" in prompt
    assert "后一条明确纠正前文" in prompt
    assert "旧答案应作废" in prompt


def test_wecom_notification_contains_buyer_message_text_only():
    bridge = text("services/api-control-plane/wecom_bridge.py")
    handoff = text("src/Bot/ChromeNs/HandoffNotificationService.cs")
    assert "买家消息：\\n" in bridge
    assert "safe_buyer_message" in bridge
    assert "买家消息：\\n" in handoff
    assert "SafeBuyerMessage" in handoff
''', encoding="utf-8")

wecom_test = ROOT / "services/api-control-plane/tests/test_wecom_message_content.py"
wecom_test.write_text('''from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

import wecom_bridge


def test_buyer_message_preserves_lines_and_media_markers():
    value = wecom_bridge.safe_buyer_message("什么时候发货\\n[图片]\\n什么时候发货")
    assert value == "什么时候发货\\n[图片]"


def test_handoff_message_labels_buyer_message():
    data = wecom_bridge.HandoffNotifyInput(
        seller="seller",
        buyer="buyer",
        question="第一段\\n[语音]\\n补充说明",
        reason="需要人工",
    )
    message = wecom_bridge.build_handoff_message("QN-A1B2C3D4", data)
    assert "买家消息：\\n第一段\\n[语音]\\n补充说明" in message
    assert "\\n问题：" not in message
''', encoding="utf-8")

print("human-like reply patch applied")
