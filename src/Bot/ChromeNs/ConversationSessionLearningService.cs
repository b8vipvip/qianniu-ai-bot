using Bot.ChatRecord;
using Bot.Common;
using BotLib;
using BotLib.Db.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Bot.ChromeNs
{
    internal sealed class ConversationSessionLearningReportEntity
    {
        [PrimaryKey]
        public string EntityId { get; set; }
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string CCode { get; set; }
        public string Status { get; set; }
        public string Summary { get; set; }
        public string StyleBefore { get; set; }
        public string StyleAfter { get; set; }
        public string SuggestionsJson { get; set; }
        public string Error { get; set; }
        public int AppliedCount { get; set; }
        public int SkippedCount { get; set; }
        public int RetryCount { get; set; }
        public long SessionStartedAtTicks { get; set; }
        public long LastBuyerAtTicks { get; set; }
        public long LastActivityAtTicks { get; set; }
        public long CompletedAtTicks { get; set; }
        public long CreatedAtTicks { get; set; }
        public long UpdatedAtTicks { get; set; }
    }

    internal sealed class StoreReplyStyleProfileEntity
    {
        [PrimaryKey]
        public string Seller { get; set; }
        public string Profile { get; set; }
        public int LearnedSessions { get; set; }
        public long UpdatedAtTicks { get; set; }
    }

    internal sealed class ConversationSessionLearningReportView
    {
        public string Id { get; set; }
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string Status { get; set; }
        public string Summary { get; set; }
        public string StyleBefore { get; set; }
        public string StyleAfter { get; set; }
        public string SuggestionsJson { get; set; }
        public string Error { get; set; }
        public int AppliedCount { get; set; }
        public int SkippedCount { get; set; }
        public DateTime SessionStartedAt { get; set; }
        public DateTime LastBuyerAt { get; set; }
        public DateTime CompletedAt { get; set; }

        public string CompletedAtText
        {
            get
            {
                var value = CompletedAt == DateTime.MinValue ? LastBuyerAt : CompletedAt;
                return value == DateTime.MinValue ? string.Empty : value.ToString("MM-dd HH:mm:ss");
            }
        }
    }

    internal static class ConversationSessionLearningService
    {
        public const int InactivityMinutes = 5;
        public const int MaxSuggestionsPerSession = 8;
        public const int ReportRetentionDays = 90;
        public const int MaxReports = 1000;

        private sealed class ActiveSession
        {
            public readonly object Sync = new object();
            public string EntityId;
            public string Seller;
            public string Buyer;
            public string CCode;
            public DateTime StartedAt;
            public DateTime LastBuyerAt;
            public DateTime LastActivityAt;
            public int RetryCount;
            public Timer Timer;
        }

        private sealed class ParsedSuggestion
        {
            public string Action;
            public string Question;
            public string Answer;
            public string OldAnswer;
            public string Category;
            public string Keywords;
            public string EvidenceType;
            public string Evidence;
            public string Reason;
            public double Confidence;
            public bool Applied;
            public string ApplyMessage;
        }

        private static readonly ConcurrentDictionary<string, ActiveSession> Active =
            new ConcurrentDictionary<string, ActiveSession>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, StoreReplyStyleProfileEntity> StyleCache =
            new ConcurrentDictionary<string, StoreReplyStyleProfileEntity>(StringComparer.Ordinal);
        private static readonly object DbSync = new object();
        private static int _initialized;
        private static int _schemaReady;

        public static event Action ReportsChanged;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            EnsureSchema();
            ConversationSessionLearningRuntimeBridge.Initialize();
            try
            {
                if (Application.Current != null)
                {
                    Application.Current.Exit += (s, e) => DisposeAllTimers();
                }
            }
            catch
            {
            }

            Task.Run(async () =>
            {
                await Task.Delay(2500);
                ResumePendingSessions();
                CleanupReports();
            });

            Log.Info("接待结束自动学习已启用：买家连续 " + InactivityMinutes
                + " 分钟无新消息后，自动复盘人工客服与Bot回复并迭代知识和表达风格。");
        }

        public static void ObserveLiveMessage(
            QNChatMessage message,
            string messageText,
            string seller,
            string buyer)
        {
            Initialize();
            if (message == null || string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer)) return;
            if (ConversationContextStore.IsPlatformSystemTip(message, messageText)) return;

            var from = message.fromid == null ? string.Empty : (message.fromid.nick ?? string.Empty).Trim();
            var to = message.toid == null ? string.Empty : (message.toid.nick ?? string.Empty).Trim();
            var isBuyer = from == buyer && to == seller;
            var isSeller = from == seller && to == buyer;
            var isWithdrawal = ConversationContextStore.IsWithdrawalNotice(message, messageText);
            if (!isBuyer && !isSeller && !isWithdrawal) return;

            var now = DateTime.Now;
            var ccode = message.cid == null ? string.Empty : (message.cid.ccode ?? string.Empty).Trim();
            var key = SessionKey(seller, buyer);

            if (isBuyer)
            {
                ActiveSession session;
                if (!Active.TryGetValue(key, out session))
                {
                    session = CreateSession(seller, buyer, ccode, now);
                    Active[key] = session;
                }

                lock (session.Sync)
                {
                    session.LastBuyerAt = now;
                    session.LastActivityAt = now;
                    if (!string.IsNullOrWhiteSpace(ccode)) session.CCode = ccode;
                    session.RetryCount = 0;
                    SaveSessionSnapshot(session, "等待接待结束");
                    Schedule(session, TimeSpan.FromMinutes(InactivityMinutes));
                }
                return;
            }

            ActiveSession existing;
            if (Active.TryGetValue(key, out existing))
            {
                lock (existing.Sync)
                {
                    existing.LastActivityAt = now;
                    if (!string.IsNullOrWhiteSpace(ccode)) existing.CCode = ccode;
                    SaveSessionSnapshot(existing, "等待接待结束");
                }
            }
        }

        public static string BuildReplyStylePromptAddon(string seller)
        {
            var profile = GetStyleProfile(seller);
            if (string.IsNullOrWhiteSpace(profile)) return string.Empty;
            return "\n\n本店真人客服表达风格（来自历史人工接待复盘，只学习措辞风格，不作为商品或售后事实来源）："
                + profile.Trim()
                + "\n在不违反当前事实、知识库和安全规则的前提下，优先使用这种简洁自然的表达习惯。";
        }

        public static List<ConversationSessionLearningReportView> GetReports(int maxCount)
        {
            Initialize();
            EnsureSchema();
            var take = Math.Max(1, Math.Min(MaxReports, maxCount <= 0 ? 200 : maxCount));
            try
            {
                lock (DbSync)
                {
                    var rows = DbHelper.Db.Select(
                        typeof(ConversationSessionLearningReportEntity),
                        "order by UpdatedAtTicks desc limit " + take);
                    return (rows ?? new List<object>())
                        .OfType<ConversationSessionLearningReportEntity>()
                        .Where(x => x != null)
                        .Select(ToView)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取接待自动学习记录失败：" + ex.Message, 10);
                return new List<ConversationSessionLearningReportView>();
            }
        }

        public static string FormatReport(ConversationSessionLearningReportView report)
        {
            if (report == null) return string.Empty;
            var sb = new StringBuilder();
            sb.AppendLine("接待自动学习报告");
            sb.AppendLine("报告ID：" + report.Id);
            sb.AppendLine("客服：" + report.Seller);
            sb.AppendLine("买家：" + report.Buyer);
            sb.AppendLine("接待开始：" + FormatTime(report.SessionStartedAt));
            sb.AppendLine("最后买家消息：" + FormatTime(report.LastBuyerAt));
            sb.AppendLine("完成时间：" + FormatTime(report.CompletedAt));
            sb.AppendLine("状态：" + report.Status);
            sb.AppendLine("应用知识：" + report.AppliedCount + " 条");
            sb.AppendLine("跳过建议：" + report.SkippedCount + " 条");
            if (!string.IsNullOrWhiteSpace(report.Summary))
            {
                sb.AppendLine();
                sb.AppendLine("本轮复盘摘要：");
                sb.AppendLine(report.Summary);
            }
            if (!string.IsNullOrWhiteSpace(report.StyleBefore)
                || !string.IsNullOrWhiteSpace(report.StyleAfter))
            {
                sb.AppendLine();
                sb.AppendLine("学习前表达风格：");
                sb.AppendLine(string.IsNullOrWhiteSpace(report.StyleBefore) ? "（暂无）" : report.StyleBefore);
                sb.AppendLine();
                sb.AppendLine("学习后表达风格：");
                sb.AppendLine(string.IsNullOrWhiteSpace(report.StyleAfter) ? "（未变化）" : report.StyleAfter);
            }
            if (!string.IsNullOrWhiteSpace(report.SuggestionsJson))
            {
                sb.AppendLine();
                sb.AppendLine("知识迭代建议：");
                try
                {
                    sb.AppendLine(JToken.Parse(report.SuggestionsJson).ToString(Formatting.Indented));
                }
                catch
                {
                    sb.AppendLine(report.SuggestionsJson);
                }
            }
            if (!string.IsNullOrWhiteSpace(report.Error))
            {
                sb.AppendLine();
                sb.AppendLine("异常：" + report.Error);
            }
            return sb.ToString().Trim();
        }

        private static ActiveSession CreateSession(string seller, string buyer, string ccode, DateTime now)
        {
            var session = new ActiveSession
            {
                EntityId = Guid.NewGuid().ToString("N"),
                Seller = (seller ?? string.Empty).Trim(),
                Buyer = (buyer ?? string.Empty).Trim(),
                CCode = (ccode ?? string.Empty).Trim(),
                StartedAt = now,
                LastBuyerAt = now,
                LastActivityAt = now,
                RetryCount = 0
            };
            session.Timer = new Timer(OnSessionDue, session, Timeout.Infinite, Timeout.Infinite);
            SaveSessionSnapshot(session, "等待接待结束");
            Log.Info("已开始新的接待学习轮次: seller=" + session.Seller
                + ", buyer=" + session.Buyer + ", session=" + session.EntityId);
            return session;
        }

        private static void Schedule(ActiveSession session, TimeSpan due)
        {
            if (session == null || session.Timer == null) return;
            var milliseconds = (long)Math.Max(1000, due.TotalMilliseconds);
            if (milliseconds > int.MaxValue) milliseconds = int.MaxValue;
            session.Timer.Change((int)milliseconds, Timeout.Infinite);
        }

        private static void OnSessionDue(object state)
        {
            var session = state as ActiveSession;
            if (session == null) return;
            Task.Run(() => FinalizeSessionAsync(session));
        }

        private static async Task FinalizeSessionAsync(ActiveSession session)
        {
            var key = SessionKey(session.Seller, session.Buyer);
            lock (session.Sync)
            {
                var remaining = TimeSpan.FromMinutes(InactivityMinutes) - (DateTime.Now - session.LastBuyerAt);
                if (remaining > TimeSpan.Zero)
                {
                    Schedule(session, remaining);
                    return;
                }
            }

            ActiveSession current;
            if (!Active.TryGetValue(key, out current) || !ReferenceEquals(current, session)) return;
            if (!Active.TryRemove(key, out current)) return;
            DisposeTimer(session);

            SaveSessionSnapshot(session, "正在复盘");
            Log.Info("接待已静默 " + InactivityMinutes + " 分钟，开始自动学习复盘: seller="
                + session.Seller + ", buyer=" + session.Buyer + ", session=" + session.EntityId);

            try
            {
                if (!string.IsNullOrWhiteSpace(session.CCode))
                {
                    await Task.Run(() => ConversationSessionLearningRuntimeBridge.RefreshRemoteHistory(
                        session.Seller,
                        session.Buyer,
                        session.CCode));
                    await Task.Delay(500);
                }

                var endAt = DateTime.Now;
                var turns = ConversationSessionLearningRuntimeBridge.GetTurnsBetween(
                    session.Seller,
                    session.Buyer,
                    session.StartedAt.AddMinutes(-1),
                    endAt,
                    120,
                    true);
                var botCards = BotConversationHistoryStore.LoadRange(
                    session.Seller,
                    session.Buyer,
                    session.StartedAt.AddMinutes(-1),
                    endAt,
                    120);

                var buyerTurns = turns.Count(x => x.Role == "user" && !x.Withdrawn);
                var sellerTurns = turns.Count(x => x.Role == "assistant");
                if (buyerTurns < 1 || sellerTurns < 1)
                {
                    if (session.RetryCount < 3)
                    {
                        session.RetryCount++;
                        RequeueForHistory(session, "等待聊天历史恢复后再次复盘");
                        return;
                    }
                    CompleteWithoutLearning(
                        session,
                        "本轮未读取到完整的买家与客服双方消息，未自动修改知识库。",
                        "聊天历史不足");
                    return;
                }

                var previousStyle = GetStyleProfile(session.Seller);
                var analysis = await AnalyzeSessionAsync(
                    session,
                    turns,
                    botCards,
                    previousStyle,
                    CancellationToken.None);

                var summary = Clean(Convert.ToString(analysis["summary"]), 1200);
                var nextStyle = Clean(Convert.ToString(analysis["reply_style_profile"]), 800);
                var humanSellerTurns = CountHumanSellerTurns(turns, botCards);
                if (humanSellerTurns >= 2 && !string.IsNullOrWhiteSpace(nextStyle))
                {
                    SaveStyleProfile(session.Seller, nextStyle);
                }
                else
                {
                    nextStyle = previousStyle;
                }

                var parsed = ParseSuggestions(analysis["suggestions"] as JArray);
                var applied = 0;
                var skipped = 0;
                foreach (var suggestion in parsed.Take(MaxSuggestionsPerSession))
                {
                    if (!ShouldApply(suggestion))
                    {
                        suggestion.Applied = false;
                        if (string.IsNullOrWhiteSpace(suggestion.ApplyMessage))
                        {
                            suggestion.ApplyMessage = "未满足自动学习安全条件";
                        }
                        skipped++;
                        continue;
                    }

                    var result = KnowledgeLearningService.ApplyReviewedKnowledge(
                        suggestion.Question,
                        suggestion.Answer,
                        suggestion.Category,
                        suggestion.Keywords,
                        "人工接待复盘",
                        suggestion.Confidence,
                        suggestion.EvidenceType);
                    suggestion.Applied = result != null && result.Success && (result.Added || result.Updated);
                    suggestion.ApplyMessage = result == null ? "知识写入结果为空" : result.Message;
                    if (suggestion.Applied) applied++;
                    else skipped++;
                }

                var report = LoadReport(session.EntityId) ?? CreateReport(session);
                report.Status = "学习完成";
                report.Summary = summary;
                report.StyleBefore = previousStyle;
                report.StyleAfter = nextStyle;
                report.SuggestionsJson = SerializeSuggestions(parsed.Take(MaxSuggestionsPerSession));
                report.AppliedCount = applied;
                report.SkippedCount = skipped;
                report.Error = string.Empty;
                report.CompletedAtTicks = DateTime.Now.Ticks;
                report.UpdatedAtTicks = DateTime.Now.Ticks;
                SaveReport(report);
                RaiseReportsChanged();
                Log.Info("接待自动学习完成: seller=" + session.Seller + ", buyer=" + session.Buyer
                    + ", applied=" + applied + ", skipped=" + skipped);
            }
            catch (Exception ex)
            {
                var report = LoadReport(session.EntityId) ?? CreateReport(session);
                report.Status = "学习失败";
                report.Error = Clean(ex.Message, 1500);
                report.CompletedAtTicks = DateTime.Now.Ticks;
                report.UpdatedAtTicks = DateTime.Now.Ticks;
                SaveReport(report);
                RaiseReportsChanged();
                Log.ErrorWithMaxCount("接待自动学习失败: seller=" + session.Seller
                    + ", buyer=" + session.Buyer + ", error=" + ex.Message, 20);
            }
        }

        private static async Task<JObject> AnalyzeSessionAsync(
            ActiveSession session,
            List<ConversationContextTurn> turns,
            List<BotConversationHistoryEntity> botCards,
            string previousStyle,
            CancellationToken token)
        {
            var messages = new JArray
            {
                new JObject
                {
                    ["role"] = "system",
                    ["content"] =
                        "你是电商客服接待复盘与知识迭代系统。请分析完整的一轮买家与客服聊天，并对比Bot原始候选答案和客服最终实际回复。"
                        + "只输出一个JSON对象，格式为：{\"summary\":\"本轮复盘摘要\",\"reply_style_profile\":\"稳定的本店真人客服表达风格\",\"suggestions\":[{\"action\":\"add|update|skip\",\"question\":\"可复用的通用问题\",\"answer\":\"可复用的最终答案\",\"old_answer\":\"需要被改进的旧答案，可空\",\"category\":\"分类\",\"keywords\":[\"关键词\"],\"confidence\":0.0,\"evidence_type\":\"manual_reply|manual_correction|withdrawn_bot_then_manual|repeated_human_pattern|bot_only|insufficient\",\"evidence\":\"依据\",\"reason\":\"为什么这样优化\"}]}。"
                        + "规则：1.Bot标记回复不能作为新增事实的唯一证据，禁止自我学习、自我强化；"
                        + "2.客服撤回的消息不是有效答案，但Bot被撤回后人工重新发送的内容属于强修正证据；"
                        + "3.人工客服最终有效回复优先于Bot旧答案；如果人工只是改善语气而没有改变事实，可优化表达但不得编造新事实；"
                        + "4.不得保留真实手机号、验证码、订单号、身份证、银行卡、买家账号等个人信息，必须通用化；"
                        + "5.退款、投诉、赔偿、差评、订单隐私、账号安全等高风险或一次性订单结论默认action=skip；"
                        + "6.reply_style_profile只总结称呼、句长、语气、常用连接方式、追问习惯等表达风格，不能写商品事实、价格、库存、售后政策；"
                        + "7.最多给出8条真正值得沉淀的建议；没有可靠人工证据时宁可skip；"
                        + "8.answer必须完整自然，不得带[AI]标识，不得保留被截断的半句话。"
                },
                new JObject
                {
                    ["role"] = "user",
                    ["content"] =
                        "客服账号：" + Safe(session.Seller)
                        + "\n买家标识：" + Safe(session.Buyer)
                        + "\n现有表达风格：" + (string.IsNullOrWhiteSpace(previousStyle) ? "（暂无）" : previousStyle)
                        + "\n\n本轮聊天时间线：\n" + BuildTranscript(turns, botCards)
                        + "\n\nBot问答卡片：\n" + BuildBotCards(botCards)
                }
            };

            var result = await Task.Run(() => MyOpenAI.CallStructuredChat(
                messages,
                2600,
                0.05,
                50,
                token), token);
            if (result == null || !result.Success || string.IsNullOrWhiteSpace(result.Answer))
            {
                throw new Exception(result == null ? "AI复盘调用结果为空" : result.Error);
            }
            return ParseObject(result.Answer);
        }

        private static string BuildTranscript(
            List<ConversationContextTurn> turns,
            List<BotConversationHistoryEntity> botCards)
        {
            var sb = new StringBuilder();
            foreach (var turn in turns.OrderBy(x => x.Timestamp))
            {
                if (turn == null || string.IsNullOrWhiteSpace(turn.Text)) continue;
                var time = turn.Timestamp == DateTime.MinValue
                    ? "时间未知"
                    : turn.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
                string role;
                if (turn.Role == "user") role = "买家";
                else if (turn.Withdrawn) role = "客服-已撤回";
                else if (IsBotTurn(turn, botCards)) role = "Bot";
                else role = "人工客服";
                sb.Append('[').Append(time).Append(' ').Append(role).Append("] ")
                    .AppendLine(Redact(turn.Text));
            }
            return sb.ToString().Trim();
        }

        private static string BuildBotCards(List<BotConversationHistoryEntity> cards)
        {
            if (cards == null || cards.Count < 1) return "（本轮无Bot问答卡片）";
            var sb = new StringBuilder();
            foreach (var card in cards.OrderBy(x => x.CreatedAtTicks).Take(80))
            {
                sb.Append("[问题] ").AppendLine(Redact(card.Question));
                sb.Append("[Bot原答案] ").AppendLine(Redact(StripAiMarker(card.Answer)));
                sb.Append("[来源] ").Append(card.AnswerSource)
                    .Append(" [状态] ").AppendLine(card.StatusText);
            }
            return sb.ToString().Trim();
        }

        private static int CountHumanSellerTurns(
            List<ConversationContextTurn> turns,
            List<BotConversationHistoryEntity> botCards)
        {
            return turns.Count(x => x != null
                && x.Role == "assistant"
                && !x.Withdrawn
                && !IsBotTurn(x, botCards));
        }

        private static bool IsBotTurn(
            ConversationContextTurn turn,
            List<BotConversationHistoryEntity> botCards)
        {
            var text = (turn == null ? string.Empty : turn.Text ?? string.Empty).Trim();
            if (text.EndsWith("[AI]", StringComparison.OrdinalIgnoreCase)) return true;
            var normalized = Normalize(StripAiMarker(text));
            if (normalized.Length == 0 || botCards == null) return false;
            return botCards.Any(x => Normalize(StripAiMarker(x.Answer)) == normalized);
        }

        private static List<ParsedSuggestion> ParseSuggestions(JArray array)
        {
            var list = new List<ParsedSuggestion>();
            if (array == null) return list;
            foreach (var item in array.OfType<JObject>())
            {
                var keywordsToken = item["keywords"];
                var keywords = keywordsToken is JArray
                    ? string.Join(",", ((JArray)keywordsToken).Select(x => Clean(x.ToString(), 80)).Where(x => x.Length > 0))
                    : Clean(Convert.ToString(keywordsToken), 500);
                double confidence;
                if (!double.TryParse(Convert.ToString(item["confidence"]), out confidence)) confidence = 0;
                list.Add(new ParsedSuggestion
                {
                    Action = Clean(Convert.ToString(item["action"]), 20).ToLowerInvariant(),
                    Question = Redact(Clean(Convert.ToString(item["question"]), 400)),
                    Answer = StripAiMarker(Redact(Clean(Convert.ToString(item["answer"]), 1200))),
                    OldAnswer = StripAiMarker(Redact(Clean(Convert.ToString(item["old_answer"]), 1200))),
                    Category = Clean(Convert.ToString(item["category"]), 80),
                    Keywords = keywords,
                    EvidenceType = Clean(Convert.ToString(item["evidence_type"]), 80).ToLowerInvariant(),
                    Evidence = Redact(Clean(Convert.ToString(item["evidence"]), 1200)),
                    Reason = Redact(Clean(Convert.ToString(item["reason"]), 1200)),
                    Confidence = Math.Max(0, Math.Min(1, confidence))
                });
            }
            return list;
        }

        private static bool ShouldApply(ParsedSuggestion suggestion)
        {
            if (suggestion == null) return false;
            if (suggestion.Action != "add" && suggestion.Action != "update")
            {
                suggestion.ApplyMessage = "AI建议跳过";
                return false;
            }
            if (suggestion.Confidence < 0.86)
            {
                suggestion.ApplyMessage = "置信度低于0.86";
                return false;
            }
            if (string.IsNullOrWhiteSpace(suggestion.Question)
                || string.IsNullOrWhiteSpace(suggestion.Answer))
            {
                suggestion.ApplyMessage = "问题或答案为空";
                return false;
            }
            if (suggestion.EvidenceType == "bot_only" || suggestion.EvidenceType == "insufficient")
            {
                suggestion.ApplyMessage = "缺少可靠人工证据，禁止Bot自我学习";
                return false;
            }
            if (!IsTrustedEvidence(suggestion.EvidenceType))
            {
                suggestion.ApplyMessage = "证据类型不属于可自动学习范围";
                return false;
            }
            if (ContainsHighRiskTopic(suggestion.Question + " " + suggestion.Answer))
            {
                suggestion.ApplyMessage = "涉及高风险或一次性业务结论，未自动写入知识库";
                return false;
            }
            return true;
        }

        private static bool IsTrustedEvidence(string value)
        {
            return value == "manual_reply"
                || value == "manual_correction"
                || value == "withdrawn_bot_then_manual"
                || value == "repeated_human_pattern";
        }

        private static bool ContainsHighRiskTopic(string value)
        {
            value = value ?? string.Empty;
            var terms = new[]
            {
                "退款", "退货", "赔偿", "投诉", "差评", "举报", "仲裁",
                "身份证", "银行卡", "验证码", "密码", "订单隐私", "订单号",
                "手机号", "账号安全", "封号", "解封", "法律", "报警"
            };
            return terms.Any(x => value.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string SerializeSuggestions(IEnumerable<ParsedSuggestion> suggestions)
        {
            var array = new JArray();
            foreach (var x in suggestions ?? Enumerable.Empty<ParsedSuggestion>())
            {
                array.Add(new JObject
                {
                    ["action"] = x.Action,
                    ["question"] = x.Question,
                    ["answer"] = x.Answer,
                    ["old_answer"] = x.OldAnswer,
                    ["category"] = x.Category,
                    ["keywords"] = x.Keywords,
                    ["confidence"] = x.Confidence,
                    ["evidence_type"] = x.EvidenceType,
                    ["evidence"] = x.Evidence,
                    ["reason"] = x.Reason,
                    ["applied"] = x.Applied,
                    ["apply_message"] = x.ApplyMessage
                });
            }
            return array.ToString(Formatting.None);
        }

        private static void RequeueForHistory(ActiveSession session, string status)
        {
            var key = SessionKey(session.Seller, session.Buyer);
            session.Timer = new Timer(OnSessionDue, session, Timeout.Infinite, Timeout.Infinite);
            Active[key] = session;
            SaveSessionSnapshot(session, status);
            Schedule(session, TimeSpan.FromMinutes(1));
            Log.Info("接待自动学习暂缓：等待聊天历史恢复。seller=" + session.Seller
                + ", buyer=" + session.Buyer + ", retry=" + session.RetryCount);
        }

        private static void CompleteWithoutLearning(ActiveSession session, string summary, string error)
        {
            var report = LoadReport(session.EntityId) ?? CreateReport(session);
            report.Status = "已跳过";
            report.Summary = summary;
            report.Error = error;
            report.CompletedAtTicks = DateTime.Now.Ticks;
            report.UpdatedAtTicks = DateTime.Now.Ticks;
            SaveReport(report);
            RaiseReportsChanged();
            Log.Info("接待自动学习已跳过: seller=" + session.Seller + ", buyer=" + session.Buyer
                + ", reason=" + error);
        }

        private static void SaveSessionSnapshot(ActiveSession session, string status)
        {
            var report = LoadReport(session.EntityId) ?? CreateReport(session);
            report.CCode = session.CCode;
            report.Status = status;
            report.LastBuyerAtTicks = session.LastBuyerAt.Ticks;
            report.LastActivityAtTicks = session.LastActivityAt.Ticks;
            report.RetryCount = session.RetryCount;
            report.UpdatedAtTicks = DateTime.Now.Ticks;
            SaveReport(report);
            RaiseReportsChanged();
        }

        private static ConversationSessionLearningReportEntity CreateReport(ActiveSession session)
        {
            var now = DateTime.Now.Ticks;
            return new ConversationSessionLearningReportEntity
            {
                EntityId = session.EntityId,
                Seller = session.Seller,
                Buyer = session.Buyer,
                CCode = session.CCode,
                Status = "等待接待结束",
                Summary = string.Empty,
                StyleBefore = string.Empty,
                StyleAfter = string.Empty,
                SuggestionsJson = string.Empty,
                Error = string.Empty,
                AppliedCount = 0,
                SkippedCount = 0,
                RetryCount = session.RetryCount,
                SessionStartedAtTicks = session.StartedAt.Ticks,
                LastBuyerAtTicks = session.LastBuyerAt.Ticks,
                LastActivityAtTicks = session.LastActivityAt.Ticks,
                CompletedAtTicks = 0,
                CreatedAtTicks = now,
                UpdatedAtTicks = now
            };
        }

        private static void SaveReport(ConversationSessionLearningReportEntity report)
        {
            if (report == null) return;
            EnsureSchema();
            try
            {
                lock (DbSync)
                {
                    DbHelper.Db.SaveRecordsInTransaction(new List<object> { report });
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("保存接待自动学习记录失败：" + ex.Message, 10);
            }
        }

        private static ConversationSessionLearningReportEntity LoadReport(string entityId)
        {
            if (string.IsNullOrWhiteSpace(entityId)) return null;
            EnsureSchema();
            try
            {
                lock (DbSync)
                {
                    var rows = DbHelper.Db.Select(
                        typeof(ConversationSessionLearningReportEntity),
                        "where EntityId = ? limit 1",
                        entityId);
                    return (rows ?? new List<object>())
                        .OfType<ConversationSessionLearningReportEntity>()
                        .FirstOrDefault();
                }
            }
            catch
            {
                return null;
            }
        }

        private static string GetStyleProfile(string seller)
        {
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0) return string.Empty;
            StoreReplyStyleProfileEntity cached;
            if (StyleCache.TryGetValue(seller, out cached) && cached != null)
            {
                return cached.Profile ?? string.Empty;
            }
            EnsureSchema();
            try
            {
                lock (DbSync)
                {
                    var rows = DbHelper.Db.Select(
                        typeof(StoreReplyStyleProfileEntity),
                        "where Seller = ? limit 1",
                        seller);
                    var entity = (rows ?? new List<object>())
                        .OfType<StoreReplyStyleProfileEntity>()
                        .FirstOrDefault();
                    if (entity != null)
                    {
                        StyleCache[seller] = entity;
                        return entity.Profile ?? string.Empty;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取店铺客服表达风格失败：" + ex.Message, 10);
            }
            return string.Empty;
        }

        private static void SaveStyleProfile(string seller, string profile)
        {
            seller = (seller ?? string.Empty).Trim();
            profile = Clean(profile, 800);
            if (seller.Length == 0 || profile.Length == 0) return;
            EnsureSchema();
            try
            {
                lock (DbSync)
                {
                    var rows = DbHelper.Db.Select(
                        typeof(StoreReplyStyleProfileEntity),
                        "where Seller = ? limit 1",
                        seller);
                    var entity = (rows ?? new List<object>())
                        .OfType<StoreReplyStyleProfileEntity>()
                        .FirstOrDefault()
                        ?? new StoreReplyStyleProfileEntity { Seller = seller };
                    entity.Profile = profile;
                    entity.LearnedSessions = Math.Max(0, entity.LearnedSessions) + 1;
                    entity.UpdatedAtTicks = DateTime.Now.Ticks;
                    DbHelper.Db.SaveRecordsInTransaction(new List<object> { entity });
                    StyleCache[seller] = entity;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("保存店铺客服表达风格失败：" + ex.Message, 10);
            }
        }

        private static void ResumePendingSessions()
        {
            EnsureSchema();
            try
            {
                var cutoff = DateTime.Now.AddDays(-1).Ticks;
                List<ConversationSessionLearningReportEntity> rows;
                lock (DbSync)
                {
                    rows = (DbHelper.Db.Select(
                        typeof(ConversationSessionLearningReportEntity),
                        "where CompletedAtTicks = 0 and LastBuyerAtTicks >= ? order by UpdatedAtTicks desc",
                        cutoff) ?? new List<object>())
                        .OfType<ConversationSessionLearningReportEntity>()
                        .ToList();
                }

                foreach (var report in rows
                    .GroupBy(x => SessionKey(x.Seller, x.Buyer))
                    .Select(g => g.OrderByDescending(x => x.UpdatedAtTicks).First()))
                {
                    var session = new ActiveSession
                    {
                        EntityId = report.EntityId,
                        Seller = report.Seller,
                        Buyer = report.Buyer,
                        CCode = report.CCode,
                        StartedAt = FromTicks(report.SessionStartedAtTicks),
                        LastBuyerAt = FromTicks(report.LastBuyerAtTicks),
                        LastActivityAt = FromTicks(report.LastActivityAtTicks),
                        RetryCount = report.RetryCount
                    };
                    if (session.StartedAt == DateTime.MinValue) session.StartedAt = DateTime.Now;
                    if (session.LastBuyerAt == DateTime.MinValue) session.LastBuyerAt = session.StartedAt;
                    if (session.LastActivityAt == DateTime.MinValue) session.LastActivityAt = session.LastBuyerAt;
                    session.Timer = new Timer(OnSessionDue, session, Timeout.Infinite, Timeout.Infinite);
                    var key = SessionKey(session.Seller, session.Buyer);
                    Active[key] = session;
                    var remaining = TimeSpan.FromMinutes(InactivityMinutes) - (DateTime.Now - session.LastBuyerAt);
                    Schedule(session, remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("恢复待学习接待轮次失败：" + ex.Message, 10);
            }
        }

        private static void CleanupReports()
        {
            EnsureSchema();
            try
            {
                lock (DbSync)
                {
                    DbHelper.Db.Execute(
                        "delete from ConversationSessionLearningReportEntity where CompletedAtTicks > 0 and UpdatedAtTicks < ?",
                        DateTime.Now.AddDays(-ReportRetentionDays).Ticks);
                    DbHelper.Db.Execute(
                        "delete from ConversationSessionLearningReportEntity where EntityId not in "
                        + "(select EntityId from ConversationSessionLearningReportEntity order by UpdatedAtTicks desc limit "
                        + MaxReports + ")");
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("清理接待自动学习记录失败：" + ex.Message, 10);
            }
        }

        private static void EnsureSchema()
        {
            if (Volatile.Read(ref _schemaReady) != 0) return;
            lock (DbSync)
            {
                if (_schemaReady != 0) return;
                DbHelper.Db.Execute(
                    "create table if not exists ConversationSessionLearningReportEntity ("
                    + "EntityId text primary key not null,"
                    + "Seller text, Buyer text, CCode text, Status text, Summary text,"
                    + "StyleBefore text, StyleAfter text, SuggestionsJson text, Error text,"
                    + "AppliedCount integer not null default 0, SkippedCount integer not null default 0, RetryCount integer not null default 0,"
                    + "SessionStartedAtTicks integer not null default 0, LastBuyerAtTicks integer not null default 0,"
                    + "LastActivityAtTicks integer not null default 0, CompletedAtTicks integer not null default 0,"
                    + "CreatedAtTicks integer not null default 0, UpdatedAtTicks integer not null default 0)"
                );
                DbHelper.Db.Execute(
                    "create index if not exists IX_ConversationSessionLearning_Updated "
                    + "on ConversationSessionLearningReportEntity(UpdatedAtTicks)"
                );
                DbHelper.Db.Execute(
                    "create table if not exists StoreReplyStyleProfileEntity ("
                    + "Seller text primary key not null, Profile text,"
                    + "LearnedSessions integer not null default 0, UpdatedAtTicks integer not null default 0)"
                );
                Volatile.Write(ref _schemaReady, 1);
            }
        }

        private static ConversationSessionLearningReportView ToView(ConversationSessionLearningReportEntity entity)
        {
            return new ConversationSessionLearningReportView
            {
                Id = entity.EntityId,
                Seller = entity.Seller,
                Buyer = entity.Buyer,
                Status = entity.Status,
                Summary = entity.Summary,
                StyleBefore = entity.StyleBefore,
                StyleAfter = entity.StyleAfter,
                SuggestionsJson = entity.SuggestionsJson,
                Error = entity.Error,
                AppliedCount = entity.AppliedCount,
                SkippedCount = entity.SkippedCount,
                SessionStartedAt = FromTicks(entity.SessionStartedAtTicks),
                LastBuyerAt = FromTicks(entity.LastBuyerAtTicks),
                CompletedAt = FromTicks(entity.CompletedAtTicks)
            };
        }

        private static JObject ParseObject(string text)
        {
            text = (text ?? string.Empty).Trim();
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start) throw new Exception("AI复盘结果中未找到JSON对象");
            return JObject.Parse(text.Substring(start, end - start + 1));
        }

        private static string StripAiMarker(string value)
        {
            value = (value ?? string.Empty).Trim();
            while (value.EndsWith("[AI]", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(0, value.Length - 4).TrimEnd();
            }
            return value;
        }

        private static string Redact(string value)
        {
            value = value ?? string.Empty;
            value = Regex.Replace(value, @"(?<!\d)1\d{10}(?!\d)", "[手机号]");
            value = Regex.Replace(value, @"(?<!\d)\d{15,19}(?!\d)", "[敏感编号]");
            value = Regex.Replace(value, @"(?i)sk-[a-z0-9_-]{12,}", "[API_KEY]");
            value = Regex.Replace(value, @"(?i)(验证码|校验码)[：:\s]*\d{4,8}", "$1：[已脱敏]");
            return Clean(value, 4000);
        }

        private static string Safe(string value)
        {
            return Redact(value).Replace("\r", " ").Replace("\n", " ");
        }

        private static string Clean(string value, int max)
        {
            value = (value ?? string.Empty).Trim();
            if (max > 0 && value.Length > max) value = value.Substring(0, max);
            return value;
        }

        private static string Normalize(string value)
        {
            return Regex.Replace(
                (value ?? string.Empty).Trim().ToLowerInvariant(),
                @"[\s\p{P}\p{S}]+",
                string.Empty);
        }

        private static string SessionKey(string seller, string buyer)
        {
            return (seller ?? string.Empty).Trim() + "#" + (buyer ?? string.Empty).Trim();
        }

        private static DateTime FromTicks(long ticks)
        {
            try
            {
                return ticks > 0 ? new DateTime(ticks) : DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private static string FormatTime(DateTime value)
        {
            return value == DateTime.MinValue ? "（未知）" : value.ToString("yyyy-MM-dd HH:mm:ss");
        }

        private static void DisposeTimer(ActiveSession session)
        {
            try
            {
                if (session != null && session.Timer != null)
                {
                    session.Timer.Change(Timeout.Infinite, Timeout.Infinite);
                    session.Timer.Dispose();
                    session.Timer = null;
                }
            }
            catch
            {
            }
        }

        private static void DisposeAllTimers()
        {
            foreach (var session in Active.Values) DisposeTimer(session);
            Active.Clear();
        }

        private static void RaiseReportsChanged()
        {
            var handler = ReportsChanged;
            if (handler != null) handler();
        }
    }
}
