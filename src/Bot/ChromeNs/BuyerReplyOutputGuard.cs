using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Final deterministic guard for text that is about to be written into the Qianniu editor.
    /// Models and relay providers occasionally return an internal planning sentence instead of the
    /// requested buyer-facing answer. Such text must never be shown to a buyer or learned as FAQ.
    /// </summary>
    internal static class BuyerReplyOutputGuard
    {
        private static readonly Regex InternalReasoningRegex = new Regex(
            @"(?:^|\s)(?:we\s+need|need\s+to\s+respond|need\s+respond|respond\s+(?:in\s+)?chinese|should\s+respond|likely\s+(?:say|reply)|one\s+sentence|the\s+user\s+(?:asks|said|says)|current\s*[""'“‘]|analysis\s*:|final\s+answer\s*:|assistant\s*:|system\s*:|developer\s*:|chain\s+of\s+thought|internal\s+reasoning|thinking\s*:)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex AllowedShortLatinTokenRegex = new Regex(
            @"\b(?:app|tv|vip|svip|ai|ok|ios|android|windows|pc|wifi|wi-fi|hdmi|usb|qq)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool TryNormalizeForBuyer(string value, out string safeText, out string reason)
        {
            safeText = ReplyTranscriptSanitizer.Sanitize(value);
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(safeText))
            {
                reason = "回复为空";
                return false;
            }
            if (safeText.StartsWith("错误：", StringComparison.Ordinal))
            {
                reason = "回复是内部错误状态";
                return false;
            }

            var body = StripAiMarker(safeText);
            if (InternalReasoningRegex.IsMatch(body))
            {
                reason = "检测到模型内部规划/推理文字";
                return false;
            }

            // Buyer replies are normally Chinese. Permit common product tokens such as APP/TV/VIP,
            // but reject an overwhelmingly English sentence with almost no Chinese content.
            var ratioText = AllowedShortLatinTokenRegex.Replace(body, string.Empty);
            var latin = ratioText.Count(ch => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'));
            var cjk = ratioText.Count(IsCjk);
            if (latin >= 18 && cjk <= 6 && latin > Math.Max(12, cjk * 3))
            {
                reason = "回复主体为异常英文文本，疑似模型内部说明";
                return false;
            }

            return true;
        }

        internal static bool LooksLikeInternalReasoning(string value)
        {
            string ignored;
            string reason;
            return !TryNormalizeForBuyer(value, out ignored, out reason)
                && (reason.IndexOf("内部", StringComparison.Ordinal) >= 0
                    || reason.IndexOf("异常英文", StringComparison.Ordinal) >= 0);
        }

        private static string StripAiMarker(string value)
        {
            value = (value ?? string.Empty).Trim();
            foreach (var suffix in new[] { "[AI]", "【AI】", "［AI］" })
            {
                if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return value.Substring(0, value.Length - suffix.Length).TrimEnd();
                }
            }
            return value;
        }

        private static bool IsCjk(char ch)
        {
            return (ch >= '\u3400' && ch <= '\u4DBF')
                || (ch >= '\u4E00' && ch <= '\u9FFF')
                || (ch >= '\uF900' && ch <= '\uFAFF');
        }
    }
}
