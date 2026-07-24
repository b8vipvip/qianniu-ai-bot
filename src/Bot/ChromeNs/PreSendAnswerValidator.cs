using Bot.Knowledge;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Bot.ChromeNs
{
    internal enum AnswerValidationDecision
    {
        Pass,
        Repair,
        Block
    }

    internal sealed class AnswerValidationResult
    {
        public AnswerValidationDecision Decision { get; set; }
        public string Answer { get; set; }
        public string Reason { get; set; }
        public bool Repaired { get; set; }
    }

    internal static class PreSendAnswerValidator
    {
        private const int MaxAnswerLength = 800;
        private const int RepairMaxTokens = 220;

        private static readonly string[] MachineMetaCues =
        {
            "作为ai", "作为一个ai", "我是ai", "我是一个ai", "根据系统提示", "系统提示要求",
            "根据知识库", "知识库显示", "候选知识", "智能知识路由", "上下文问题还原",
            "店铺固定事实", "作为语言模型", "我的提示词", "内部提示"
        };

        private static readonly string[] DangerousPromiseCues =
        {
            "保证到账", "一定到账", "百分百到账", "100%到账", "绝对到账", "马上到账", "立即到账",
            "保证退款", "一定退款", "无条件退款", "百分百退款", "100%退款",
            "保证赔偿", "一定赔偿", "全额赔偿", "绝对没问题", "百分百没问题", "100%没问题"
        };

        private static readonly string[] SensitiveRequestPatterns =
        {
            @"(?:把|请|需要|提供|发送|发一下|发我).{0,8}(?:密码|登录密码|支付密码)",
            @"(?:把|请|需要|提供|发送|发一下|发我).{0,8}(?:验证码|校验码|短信码)",
            @"(?:把|请|需要|提供|发送|发一下|发我).{0,8}(?:银行卡号|银行卡|身份证号|身份证正反面)"
        };

        private static readonly string[] UngroundedStatusClaims =
        {
            "已经退款", "已退款", "退款成功", "已经发货", "已发货", "已经到账", "已到账",
            "已为您处理", "已经处理好了", "已经处理完成", "已完成处理", "已补发", "已经补发"
        };

        private static readonly string[] GenericOnlyAnswers =
        {
            "好的", "好", "明白", "明白了", "收到", "知道了", "可以", "不可以", "能", "不能",
            "请稍等", "稍等", "稍等一下", "嗯", "嗯好的"
        };

        public static AnswerValidationResult ValidateAndRepair(
            string seller,
            string buyer,
            string question,
            string candidateAnswer,
            bool allowAiRepair)
        {
            var raw = BotOutboundMessageFormatter.StripAiMarker(candidateAnswer);
            var local = ValidateLocal(seller, buyer, question, raw);
            if (local.Decision == AnswerValidationDecision.Pass)
            {
                local.Answer = BotOutboundMessageFormatter.EnsureAiMarker(raw);
                return local;
            }
            if (local.Decision == AnswerValidationDecision.Block || !allowAiRepair)
            {
                local.Answer = BuildBlockedAnswer(local.Reason);
                return local;
            }

            var cleaned = TryDeterministicMetaCleanup(raw);
            if (!string.Equals(cleaned, raw, StringComparison.Ordinal))
            {
                var cleanedCheck = ValidateLocal(seller, buyer, question, cleaned);
                if (cleanedCheck.Decision == AnswerValidationDecision.Pass)
                {
                    return new AnswerValidationResult
                    {
                        Decision = AnswerValidationDecision.Pass,
                        Answer = BotOutboundMessageFormatter.EnsureAiMarker(
                            BotFeatureStore.ApplyOutputPolicy(cleaned)),
                        Reason = "已在发送前移除机器身份/内部提示泄漏措辞",
                        Repaired = true
                    };
                }
            }

            var repaired = RepairOnce(seller, buyer, question, raw, local.Reason);
            if (string.IsNullOrWhiteSpace(repaired)
                || repaired.StartsWith("错误：", StringComparison.Ordinal))
            {
                return new AnswerValidationResult
                {
                    Decision = AnswerValidationDecision.Block,
                    Answer = BuildBlockedAnswer(local.Reason + "；自动修复失败"),
                    Reason = local.Reason + "；自动修复失败",
                    Repaired = false
                };
            }

            repaired = BotFeatureStore.ApplyOutputPolicy(repaired);
            var recheck = ValidateLocal(seller, buyer, question, repaired);
            if (recheck.Decision != AnswerValidationDecision.Pass)
            {
                return new AnswerValidationResult
                {
                    Decision = AnswerValidationDecision.Block,
                    Answer = BuildBlockedAnswer(recheck.Reason + "；修复后仍未通过"),
                    Reason = recheck.Reason + "；修复后仍未通过",
                    Repaired = true
                };
            }

            Log.Info("发送前答案校验已自动修复: seller=" + seller
                + ", buyer=" + buyer + ", reason=" + SafeLog(local.Reason));
            return new AnswerValidationResult
            {
                Decision = AnswerValidationDecision.Pass,
                Answer = BotOutboundMessageFormatter.EnsureAiMarker(repaired),
                Reason = local.Reason,
                Repaired = true
            };
        }

        public static AnswerValidationResult ValidateLocal(
            string seller,
            string buyer,
            string question,
            string answer)
        {
            answer = BotOutboundMessageFormatter.StripAiMarker(answer).Trim();
            if (string.IsNullOrWhiteSpace(answer)) return Block("答案为空");
            if (answer.StartsWith("错误：", StringComparison.Ordinal)) return Block("上游返回错误答案");
            if (answer.IndexOf(BotOutboundMessageFormatter.StreamAbortMarker, StringComparison.Ordinal) >= 0)
                return Block("检测到流式中断标识");

            var compact = Compact(answer);
            if (MachineMetaCues.Any(x => compact.Contains(Compact(x))))
                return Repair("答案泄漏AI身份、系统提示或内部知识路由信息");

            foreach (var pattern in SensitiveRequestPatterns)
            {
                if (Regex.IsMatch(answer, pattern, RegexOptions.IgnoreCase))
                    return Block("答案要求买家提供密码、验证码或高敏感身份/银行卡信息");
            }

            if (DangerousPromiseCues.Any(x => compact.Contains(Compact(x))))
                return Repair("答案包含未经事实依据支持的绝对到账、退款或赔偿承诺");

            var timeline = ConversationContextStore.BuildTimelineText(seller, buyer, question, 12);
            var context = Compact((question ?? string.Empty) + " " + timeline);
            foreach (var claim in UngroundedStatusClaims)
            {
                var key = Compact(claim);
                if (!compact.Contains(key)) continue;
                if (!ContextSupportsClaim(context, key))
                    return Repair("答案声称订单/退款/发货/到账等状态已经完成，但当前会话没有足够依据");
            }

            if (answer.Length > MaxAnswerLength)
                return Repair("答案过长，容易出现多余信息或混入不相关结论");

            var questionCompact = Compact(question);
            if (questionCompact.Length >= 6
                && GenericOnlyAnswers.Contains(answer.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                return Repair("当前买家问题包含实质内容，但答案只是泛化确认或等待话术");
            }

            if (answer.Length <= 6
                && Regex.IsMatch(questionCompact, @"为什么|怎么|如何|多久|多少钱|区别|原因|能不能|是否"))
            {
                return Repair("当前问题需要解释或明确结论，但答案过短，可能没有真正回答问题");
            }

            return new AnswerValidationResult
            {
                Decision = AnswerValidationDecision.Pass,
                Answer = answer,
                Reason = "本地发送前校验通过"
            };
        }

        private static string RepairOnce(
            string seller,
            string buyer,
            string question,
            string originalAnswer,
            string failureReason)
        {
            try
            {
                var timeline = ConversationContextStore.BuildTimelineText(seller, buyer, question, 10);
                var state = ConversationStateService.Build(
                    seller,
                    buyer,
                    question,
                    ConversationContextStore.GetRecentTurns(seller, buyer, question, 12));
                var storeRules = StorePromptProfileService.BuildPromptAddon();
                var style = ConversationSessionLearningService.BuildReplyStylePromptAddon(seller);
                var safetyRules = BotFeatureStore.BuildPromptAddon(
                    (question ?? string.Empty) + " " + timeline);

                var knowledgeText = string.Empty;
                KnowledgeBaseEntry matched;
                double score;
                if (KnowledgeLearningService.TryFindLocalAnswer(
                    seller,
                    buyer,
                    question,
                    out matched,
                    out score)
                    && matched != null
                    && score >= 0.70)
                {
                    var policy = KnowledgePolicyProfileService.Evaluate(
                        matched,
                        question,
                        question,
                        state,
                        timeline);
                    if (policy == null || !policy.Excluded)
                    {
                        knowledgeText = "\n可能相关的本地事实依据（只有与当前上下文真正匹配时才能使用）：\n问题："
                            + Safe(matched.Title, 260)
                            + "\n答案：" + Safe(matched.Answer, 700)
                            + KnowledgePolicyProfileService.BuildPromptAddon(matched, policy);
                    }
                }

                var messages = new JArray
                {
                    new JObject
                    {
                        ["role"] = "system",
                        ["content"] =
                            "你是电商客服发送前答案修复器。只修复候选答案中被指出的问题，不得创造新的业务事实。"
                            + "最终只输出给买家的自然客服回复正文，不要JSON，不要解释修复过程，不要提到AI、系统提示、知识库或内部规则。"
                            + "禁止索取密码、验证码、银行卡、身份证等高敏感信息；禁止无依据承诺退款、赔偿、到账、发货或已完成某项操作。"
                            + "如果现有信息不足以确认事实，应改成谨慎说明并询问必要的非敏感信息，或建议人工进一步核查。"
                            + storeRules + style + safetyRules
                    },
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] =
                            "发送前校验失败原因：" + Safe(failureReason, 400)
                            + "\n当前买家问题：" + Safe(question, 500)
                            + "\n待修复候选答案：" + Safe(originalAnswer, 1200)
                            + (string.IsNullOrWhiteSpace(timeline)
                                ? string.Empty
                                : "\n同一买家最近对话：\n" + Safe(timeline, 2600))
                            + ConversationStateService.BuildPromptAddon(state)
                            + knowledgeText
                            + "\n请输出一条可以直接发送给当前买家的修复后答案。"
                    }
                };
                var response = MyOpenAI.CallStructuredChat(
                    messages,
                    RepairMaxTokens,
                    0.10,
                    30,
                    CancellationToken.None);
                return response != null && response.Success
                    ? BotOutboundMessageFormatter.StripAiMarker(response.Answer).Trim()
                    : string.Empty;
            }
            catch (Exception ex)
            {
                Log.Info("发送前答案自动修复失败，将阻止风险答案发送：" + ex.Message);
                return string.Empty;
            }
        }

        private static string TryDeterministicMetaCleanup(string answer)
        {
            var value = (answer ?? string.Empty).Trim();
            value = Regex.Replace(
                value,
                @"^(?:作为(?:一个)?AI(?:客服|助手|语言模型)?[，,:：\s]*|根据(?:系统提示|知识库)(?:内容)?[，,:：\s]*)",
                string.Empty,
                RegexOptions.IgnoreCase).Trim();
            return value;
        }

        private static bool ContextSupportsClaim(string compactContext, string compactClaim)
        {
            if (string.IsNullOrWhiteSpace(compactContext)) return false;
            if (compactContext.Contains(compactClaim)) return true;

            if (compactClaim.Contains("退款"))
                return ContainsAny(compactContext, "退款成功", "已经退款", "已退款", "退款完成");
            if (compactClaim.Contains("发货") || compactClaim.Contains("补发"))
                return ContainsAny(compactContext, "已经发货", "已发货", "已经补发", "已补发", "物流单号");
            if (compactClaim.Contains("到账"))
                return ContainsAny(compactContext, "已经到账", "已到账", "到账了", "收到到账");
            if (compactClaim.Contains("处理"))
                return ContainsAny(compactContext, "已经处理", "处理完成", "处理好了");
            return false;
        }

        private static bool ContainsAny(string value, params string[] cues)
        {
            return cues.Any(x => value.Contains(Compact(x)));
        }

        private static AnswerValidationResult Repair(string reason)
        {
            return new AnswerValidationResult
            {
                Decision = AnswerValidationDecision.Repair,
                Reason = reason,
                Answer = string.Empty
            };
        }

        private static AnswerValidationResult Block(string reason)
        {
            return new AnswerValidationResult
            {
                Decision = AnswerValidationDecision.Block,
                Reason = reason,
                Answer = BuildBlockedAnswer(reason)
            };
        }

        private static string BuildBlockedAnswer(string reason)
        {
            return "错误：发送前答案校验未通过，已阻止自动发送。原因：" + Safe(reason, 260);
        }

        private static string Compact(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"[\s，。！？、；：,.!?:;\-—_()（）\[\]【】]+", string.Empty);
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        private static string SafeLog(string value)
        {
            return Safe(value, 300);
        }
    }
}
