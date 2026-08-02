using Bot.Knowledge;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bot.ChromeNs
{
    internal sealed class ConversationProgressSnapshot
    {
        public bool HasOrderEvidence { get; set; }
        public bool HasBuyerImage { get; set; }
        public bool HasDeviceAccountEvidence { get; set; }
        public bool DeviceAccountConfirmed { get; set; }
        public bool HasPhoneNumber { get; set; }
        public bool HasVerificationCode { get; set; }
        public bool BuyerRefusedExchangeLink { get; set; }
        public bool ManualRechargeOffered { get; set; }
        public bool RechargeSubmitted { get; set; }
        public bool ExplicitCompatibilityQuestion { get; set; }
        public string CurrentInputKind { get; set; }
        public string Stage { get; set; }
        public string NextAction { get; set; }

        public ConversationProgressSnapshot()
        {
            CurrentInputKind = "text";
            Stage = "售前咨询";
            NextAction = string.Empty;
        }
    }

    /// <summary>
    /// Keeps a buyer conversation moving forward instead of repeatedly falling back to generic
    /// knowledge such as “please send the device screenshot” after that step was already completed.
    /// The service stores no phone number or verification code; it only derives boolean progress
    /// flags from the current seller+buyer timeline.
    /// </summary>
    internal static class ConversationProgressGuardService
    {
        private static readonly Regex PhoneRegex = new Regex(
            @"(?<!\d)1[3-9]\d{9}(?!\d)",
            RegexOptions.Compiled);

        private static readonly Regex VerificationCodeRegex = new Regex(
            @"^\s*\d{4,8}\s*$",
            RegexOptions.Compiled);

        private static readonly Regex OrderEvidenceRegex = new Regex(
            "已下单|已经下单|下单了|拍下了|已经拍下|订单号|订单[:：]|已付款|付款成功|支付成功|购买成功",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ImageRegex = new Regex(
            "\\[图片\\]|【图片】|图片消息|买家发送了图片",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex DeviceEvidenceRegex = new Regex(
            "酷狗账号|酷狗id|酷狗昵称|账号界面|绑定界面|会员卡片|开通会员|音乐vip|超级vip|电视端账号|设备账号",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex DeviceConfirmationRegex = new Regex(
            "已收到.{0,18}(?:截图|照片|图片|界面|账号)|(?:截图|照片|图片|界面).{0,18}(?:已收到|看到了|收到)|方便核对账号|已确认.{0,12}(?:账号|界面|设备)|这个界面.{0,12}(?:可以|支持)|账号信息.{0,12}(?:收到|确认)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex LinkRefusalRegex = new Regex(
            "不敢.{0,12}(?:点|打开|提交|操作)|怕.{0,8}(?:病毒|风险|被骗)|有病毒|不安全|有风险|不敢自点|不敢自己操作|担心.{0,12}(?:链接|病毒|安全)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ManualRechargeRegex = new Regex(
            "(?:发|提供).{0,8}手机号.{0,18}(?:手动|代充|帮您充|协助充值)|(?:手动|代充).{0,18}(?:手机号|充值)|可以.{0,10}手机号.{0,12}(?:手动|代充)|手机号发给我",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex RechargeSubmittedRegex = new Regex(
            "已提交(?:兑换|充值)|已经提交(?:兑换|充值)|正在充值|充值中|正在充|大概.{0,8}(?:分钟|到账)|稍后刷新|到账后",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex CompatibilityQuestionRegex = new Regex(
            "支持哪些设备|哪些设备支持|什么设备支持|我的.{0,12}(?:电视|车机|设备).{0,10}(?:支持|能用|可以用|能充)|(?:电视|车机|设备).{0,10}(?:支持吗|能用吗|可以用吗|能充吗)|是否支持|能不能用|可不可以用|兼容吗|能充值吗",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ScreenshotRequestRegex = new Regex(
            "请.{0,18}(?:拍照|拍张|截图|发图|发照片|发截图)|需要.{0,18}(?:拍照|截图|照片|图片)|提供.{0,18}(?:账号界面|绑定界面|截图|照片|图片)|把.{0,18}(?:界面|截图|照片|图片).{0,8}发",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex OrderInstructionRegex = new Regex(
            "在哪里下单|点击下单|直接下单|可以先下单|先下单|下单后联系客服|拍下后|购买链接",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static ConversationProgressSnapshot Analyze(
            string seller,
            string buyer,
            string currentQuestion,
            IList<ConversationContextTurn> turns)
        {
            var ordered = (turns ?? new List<ConversationContextTurn>())
                .Where(x => x != null && !x.Withdrawn && !string.IsNullOrWhiteSpace(x.Text))
                .OrderBy(x => x.Timestamp)
                .ToList();
            var buyerTexts = ordered
                .Where(x => x.Role == "user")
                .Select(x => x.Text ?? string.Empty)
                .ToList();
            var sellerTexts = ordered
                .Where(x => x.Role == "assistant")
                .Select(x => x.Text ?? string.Empty)
                .ToList();
            var buyerText = string.Join("\n", buyerTexts.Concat(new[] { currentQuestion ?? string.Empty }));
            var sellerText = string.Join("\n", sellerTexts);
            var allText = buyerText + "\n" + sellerText;
            var current = (currentQuestion ?? string.Empty).Trim();

            var latestCodeRequest = ordered
                .Where(x => x.Role == "assistant"
                    && Regex.IsMatch(x.Text ?? string.Empty, "验证码|校验码", RegexOptions.IgnoreCase))
                .OrderByDescending(x => x.Timestamp)
                .FirstOrDefault();
            var latestPhoneRequest = ordered
                .Where(x => x.Role == "assistant"
                    && Regex.IsMatch(x.Text ?? string.Empty, "手机号|手机号码|联系电话", RegexOptions.IgnoreCase))
                .OrderByDescending(x => x.Timestamp)
                .FirstOrDefault();

            var visual = string.Empty;
            try { visual = RecentVisualContextService.BuildPromptAddon(seller, buyer, currentQuestion); }
            catch { visual = string.Empty; }

            var progress = new ConversationProgressSnapshot
            {
                HasOrderEvidence = OrderEvidenceRegex.IsMatch(allText),
                HasBuyerImage = ImageRegex.IsMatch(buyerText),
                HasPhoneNumber = PhoneRegex.IsMatch(buyerText),
                BuyerRefusedExchangeLink = LinkRefusalRegex.IsMatch(buyerText),
                ManualRechargeOffered = ManualRechargeRegex.IsMatch(sellerText),
                RechargeSubmitted = RechargeSubmittedRegex.IsMatch(sellerText),
                ExplicitCompatibilityQuestion = CompatibilityQuestionRegex.IsMatch(current)
            };

            progress.HasDeviceAccountEvidence =
                (progress.HasBuyerImage && DeviceEvidenceRegex.IsMatch(allText))
                || DeviceEvidenceRegex.IsMatch(visual);
            progress.DeviceAccountConfirmed = progress.HasDeviceAccountEvidence
                && (DeviceConfirmationRegex.IsMatch(sellerText)
                    || Regex.IsMatch(visual, "酷狗账号|酷狗id|酷狗昵称|开通会员|会员卡片", RegexOptions.IgnoreCase));

            var currentIsPhone = PhoneRegex.IsMatch(current);
            var currentIsCode = VerificationCodeRegex.IsMatch(current)
                && latestCodeRequest != null;
            progress.HasVerificationCode = currentIsCode
                || HasStructuredReplyAfter(ordered, latestCodeRequest, VerificationCodeRegex);

            if (currentIsPhone)
                progress.CurrentInputKind = "phone_number";
            else if (currentIsCode)
                progress.CurrentInputKind = "verification_code";
            else if (ImageRegex.IsMatch(current))
                progress.CurrentInputKind = "image";
            else if (LinkRefusalRegex.IsMatch(current))
                progress.CurrentInputKind = "exchange_link_refusal";

            if (progress.RechargeSubmitted)
            {
                progress.Stage = "已下单-充值处理中";
                progress.NextAction = "等待充值结果并按状态继续处理，不得退回索要设备截图或要求重新下单。";
            }
            else if (progress.HasVerificationCode)
            {
                progress.Stage = "已下单-代充已收到验证码";
                progress.NextAction = "继续提交验证码并核对返回账号；不得再次索要手机号、验证码或设备截图。";
            }
            else if (progress.HasPhoneNumber
                && (progress.ManualRechargeOffered || progress.BuyerRefusedExchangeLink || latestPhoneRequest != null))
            {
                progress.Stage = "已下单-代充已收到手机号";
                progress.NextAction = "进入获取验证码步骤；自动代充未成功接管时转人工，不得重新询问设备是否支持。";
            }
            else if (progress.ManualRechargeOffered || progress.BuyerRefusedExchangeLink)
            {
                progress.Stage = "已下单-待收集代充手机号";
                progress.NextAction = "先接收买家手机号，再进入验证码步骤；不要重复索要已经提供的设备账号界面。";
            }
            else if (progress.HasOrderEvidence && progress.DeviceAccountConfirmed)
            {
                progress.Stage = "已下单-设备账号已确认";
                progress.NextAction = "继续当前兑换或代充步骤，不得再次要求拍摄同一设备账号界面。";
            }
            else if (progress.HasOrderEvidence)
            {
                progress.Stage = "已下单-待确认设备账号";
                progress.NextAction = "仅在尚无有效设备账号证据时按下单固定流程索要一次关键界面。";
            }
            else
            {
                progress.Stage = "售前咨询";
                progress.NextAction = progress.ExplicitCompatibilityQuestion
                    ? "买家明确询问设备兼容性时，才要求提供关键设备界面确认。"
                    : "一般售前咨询直接回答；不要主动强制买家先发设备截图，可提示先下单后联系客服，无法充值可退款。";
            }

            return progress;
        }

        public static void EnrichState(
            ConversationStateSnapshot state,
            string seller,
            string buyer,
            string currentQuestion,
            IList<ConversationContextTurn> turns)
        {
            if (state == null) return;
            var progress = Analyze(seller, buyer, currentQuestion, turns);
            state.Progress = progress;
            state.ConversationStage = progress.Stage;

            AddFact(state, progress.HasOrderEvidence, "买家已经下单或付款");
            AddFact(state, progress.HasDeviceAccountEvidence, "买家已经提供设备上的酷狗账号或会员界面证据");
            AddFact(state, progress.DeviceAccountConfirmed, "设备账号界面已经确认，不应重复索要同一截图");
            AddFact(state, progress.HasPhoneNumber, "买家已经提供手机号");
            AddFact(state, progress.HasVerificationCode, "买家已经提供验证码");
            AddFact(state, progress.BuyerRefusedExchangeLink, "买家担心兑换链接安全，不愿自行打开或提交");
            AddFact(state, progress.ManualRechargeOffered, "客服已经提出手动代充方案");
            AddFact(state, progress.RechargeSubmitted, "客服已经告知充值或兑换已提交");

            if (progress.CurrentInputKind == "phone_number")
                state.BuyerGoal = "提供代充手机号并继续上一轮手动充值流程";
            else if (progress.CurrentInputKind == "verification_code")
                state.BuyerGoal = "提供验证码并继续代充流程";
            else if (progress.CurrentInputKind == "exchange_link_refusal")
                state.BuyerGoal = "改由客服协助代充，避免自行打开兑换链接";
        }

        public static string BuildPromptAddon(ConversationStateSnapshot state)
        {
            var progress = state == null ? null : state.Progress;
            if (progress == null) return string.Empty;

            var sb = new StringBuilder();
            sb.Append("\n【会话流程进度｜最高优先级】\n")
                .Append("阶段：").Append(progress.Stage).Append("\n")
                .Append("下一步：").Append(progress.NextAction).Append("\n")
                .Append("必须沿当前流程向前推进，已经完成的步骤不得重新要求买家再做。")
                .Append("不能因为命中一条通用知识就忽略最近聊天、人工客服承诺、买家已发送的信息或图片。\n");

            if (progress.HasOrderEvidence)
                sb.Append("买家已经下单：禁止再提示去下单、拍下商品或下单后再联系。\n");
            if (progress.DeviceAccountConfirmed)
                sb.Append("设备账号/会员界面已经提供并确认：禁止再次要求发送同一类照片、截图或账号界面。\n");
            if (progress.HasPhoneNumber)
                sb.Append("手机号已经提供：禁止再次索要手机号，也不要退回设备支持确认步骤。回复中不得复述完整手机号。\n");
            if (progress.HasVerificationCode)
                sb.Append("验证码已经提供：禁止再次索要验证码；不得在回复中复述验证码。\n");
            if (progress.BuyerRefusedExchangeLink)
                sb.Append("买家明确担心链接安全：不要继续劝其点击链接。承接客服代充方案；自动化未接管或失败时立即转人工。\n");
            if (!progress.HasOrderEvidence && !progress.ExplicitCompatibilityQuestion)
                sb.Append("当前属于一般售前咨询，买家没有明确询问设备兼容性：不要主动强制先发设备截图。可自然提示先下单后联系客服，充不了可退款。\n");
            if (progress.CurrentInputKind == "phone_number")
                sb.Append("买家本轮消息是手机号，是对上一轮代充邀请的直接回答。应先确认收到并进入获取验证码/人工代充步骤，绝不能回复‘请再发设备截图’。\n");
            if (progress.CurrentInputKind == "verification_code")
                sb.Append("买家本轮消息是验证码，是对上一轮验证码请求的直接回答。应继续提交和账号核对步骤，绝不能重新索要手机号或设备截图。\n");

            return sb.ToString();
        }

        public static bool RequiresContextualHandling(ConversationStateSnapshot state)
        {
            var progress = state == null ? null : state.Progress;
            if (progress == null) return false;
            return progress.CurrentInputKind == "phone_number"
                || progress.CurrentInputKind == "verification_code"
                || progress.CurrentInputKind == "image"
                || progress.CurrentInputKind == "exchange_link_refusal"
                || progress.ManualRechargeOffered
                || progress.RechargeSubmitted;
        }

        public static bool AllowKnowledge(
            KnowledgeBaseEntry entry,
            ConversationStateSnapshot state,
            string currentQuestion)
        {
            if (entry == null) return false;
            var progress = state == null ? null : state.Progress;
            if (progress == null) return true;

            var title = entry.Title ?? string.Empty;
            var answer = entry.Answer ?? string.Empty;
            var combined = title + " " + (entry.Keywords ?? string.Empty) + " " + answer;

            if (progress.CurrentInputKind == "phone_number"
                || progress.CurrentInputKind == "verification_code")
            {
                if (RequestsScreenshot(answer)
                    || Regex.IsMatch(combined, "充值前需要提供|下单前需要提供|确认设备是否支持|购买前如何确认", RegexOptions.IgnoreCase))
                    return false;
                if (OrderInstructionRegex.IsMatch(combined)) return false;
            }

            if (progress.DeviceAccountConfirmed && RequestsScreenshot(answer)) return false;

            if (progress.HasOrderEvidence
                && OrderInstructionRegex.IsMatch(combined)
                && !Regex.IsMatch(currentQuestion ?? string.Empty, "哪里下单|怎么下单|购买链接", RegexOptions.IgnoreCase))
            {
                return false;
            }

            if (!progress.HasOrderEvidence
                && !progress.ExplicitCompatibilityQuestion
                && RequestsScreenshot(answer))
            {
                return false;
            }

            return true;
        }

        public static void AddValidationIssues(
            string question,
            string answer,
            ConversationStateSnapshot state,
            IList<ConversationContextTurn> turns,
            IList<string> issues)
        {
            if (issues == null || state == null || state.Progress == null) return;
            var progress = state.Progress;
            answer = answer ?? string.Empty;

            if (progress.DeviceAccountConfirmed && RequestsScreenshot(answer))
                AddIssue(issues, "买家已经提供并确认设备账号界面，回复却再次索要相同截图或照片");
            if (progress.HasOrderEvidence && OrderInstructionRegex.IsMatch(answer))
                AddIssue(issues, "买家已经下单，回复却再次要求下单或下单后再联系");
            if (progress.HasPhoneNumber
                && Regex.IsMatch(answer, "(?:请|需要|麻烦).{0,12}(?:提供|发送|发).{0,8}(?:手机号|手机号码|联系电话)", RegexOptions.IgnoreCase))
                AddIssue(issues, "买家已经提供手机号，回复却再次索要手机号");
            if (progress.HasVerificationCode
                && Regex.IsMatch(answer, "(?:请|需要|麻烦).{0,12}(?:提供|发送|发).{0,8}(?:验证码|校验码)", RegexOptions.IgnoreCase))
                AddIssue(issues, "买家已经提供验证码，回复却再次索要验证码");
            if (progress.BuyerRefusedExchangeLink
                && Regex.IsMatch(answer, "(?:打开|点击|进入).{0,10}(?:兑换链接|链接)|自行.{0,8}(?:兑换|提交)", RegexOptions.IgnoreCase))
                AddIssue(issues, "买家已经明确不敢打开兑换链接，回复仍要求其继续点击或自行提交");
            if (!progress.HasOrderEvidence
                && !progress.ExplicitCompatibilityQuestion
                && RequestsScreenshot(answer))
                AddIssue(issues, "一般售前咨询未询问设备兼容性，回复却强制要求先提供设备截图");
            if ((progress.CurrentInputKind == "phone_number" || progress.CurrentInputKind == "verification_code")
                && RequestsScreenshot(answer))
                AddIssue(issues, "当前消息是在承接代充手机号/验证码流程，回复却错误退回设备截图步骤");
        }

        internal static bool RequestsScreenshot(string answer)
        {
            return ScreenshotRequestRegex.IsMatch(answer ?? string.Empty);
        }

        private static bool HasStructuredReplyAfter(
            IList<ConversationContextTurn> turns,
            ConversationContextTurn request,
            Regex valueRegex)
        {
            if (request == null || turns == null) return false;
            return turns.Any(x => x != null
                && x.Role == "user"
                && !x.Withdrawn
                && (request.Timestamp == DateTime.MinValue
                    || x.Timestamp == DateTime.MinValue
                    || x.Timestamp >= request.Timestamp)
                && valueRegex.IsMatch((x.Text ?? string.Empty).Trim()));
        }

        private static void AddFact(ConversationStateSnapshot state, bool condition, string fact)
        {
            if (!condition || state == null || string.IsNullOrWhiteSpace(fact)) return;
            if (state.ConfirmedFacts == null) state.ConfirmedFacts = new List<string>();
            if (!state.ConfirmedFacts.Contains(fact)) state.ConfirmedFacts.Add(fact);
        }

        private static void AddIssue(IList<string> issues, string issue)
        {
            if (issues == null || string.IsNullOrWhiteSpace(issue)) return;
            if (!issues.Contains(issue)) issues.Add(issue);
        }
    }
}
