using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Bot.ChromeNs
{
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
            if (string.IsNullOrWhiteSpace(safeText)) { reason = "回复为空"; return false; }
            if (safeText.StartsWith("错误：", StringComparison.Ordinal)) { reason = "回复是内部错误状态"; return false; }

            var body = StripAiMarker(safeText);
            if (InternalReasoningRegex.IsMatch(body)) { reason = "检测到模型内部规划/推理文字"; return false; }

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
            var configuredSuffix = BotMessageSuffixService.GetCurrentSuffix();
            if (!string.IsNullOrWhiteSpace(configuredSuffix)
                && value.EndsWith(configuredSuffix, StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(0, value.Length - configuredSuffix.Length).TrimEnd();
            }
            foreach (var suffix in new[] { "[AI]", "【AI】", "［AI］" })
            {
                if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return value.Substring(0, value.Length - suffix.Length).TrimEnd();
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

    /// <summary>
    /// Quote/escape-aware structured JSON recovery shared by diagnostic paths. It accepts raw JSON,
    /// Markdown fences, arrays, JSON-encoded strings and explanatory prose. When a provider wraps the
    /// real payload in an envelope, schema-shaped nested objects are preferred over the envelope.
    /// </summary>
    internal static class StructuredJsonObjectRecovery
    {
        private static readonly string[] SchemaKeys =
        {
            "severity", "summary", "likely_cause", "evidence", "recommendations",
            "question", "answer", "should_learn", "action"
        };

        internal static string RecoverObjectText(string text)
        {
            JObject value;
            return TryRecoverObject(text, out value)
                ? value.ToString(Formatting.None)
                : (text ?? string.Empty).Trim();
        }

        internal static bool TryRecoverObject(string text, out JObject result)
        {
            result = null;
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0) return false;
            if (TryCandidate(text, 0, out result)) return true;

            foreach (Match fence in Regex.Matches(text, @"```(?:json)?\s*(?<body>[\s\S]*?)```", RegexOptions.IgnoreCase))
            {
                if (TryCandidate(fence.Groups["body"].Value, 0, out result)) return true;
            }
            foreach (var candidate in ExtractBalancedObjects(text))
            {
                if (TryCandidate(candidate, 0, out result)) return true;
            }
            return false;
        }

        private static bool TryCandidate(string text, int depth, out JObject result)
        {
            result = null;
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0 || depth > 5) return false;
            try { return SelectObject(JToken.Parse(text), depth, out result); }
            catch { return false; }
        }

        private static bool SelectObject(JToken token, int depth, out JObject result)
        {
            result = null;
            if (token == null || depth > 5) return false;

            var obj = token as JObject;
            if (obj != null)
            {
                if (LooksLikeStructuredPayload(obj)) { result = obj; return true; }
                foreach (var property in obj.Properties())
                {
                    if (SelectObject(property.Value, depth + 1, out result)) return true;
                }
                // A plain object is still a valid final fallback when no known schema exists.
                result = obj;
                return true;
            }

            var array = token as JArray;
            if (array != null)
            {
                foreach (var child in array)
                {
                    if (SelectObject(child, depth + 1, out result)) return true;
                }
                return false;
            }

            if (token.Type != JTokenType.String) return false;
            var nested = Convert.ToString(((JValue)token).Value);
            if (TryCandidate(nested, depth + 1, out result)) return true;
            foreach (var candidate in ExtractBalancedObjects(nested))
            {
                if (TryCandidate(candidate, depth + 1, out result)) return true;
            }
            return false;
        }

        private static bool LooksLikeStructuredPayload(JObject obj)
        {
            return obj != null && SchemaKeys.Any(key => obj[key] != null);
        }

        private static IEnumerable<string> ExtractBalancedObjects(string text)
        {
            var result = new List<string>();
            text = text ?? string.Empty;
            var start = -1;
            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (inString)
                {
                    if (escaped) { escaped = false; continue; }
                    if (ch == '\\') { escaped = true; continue; }
                    if (ch == '"') inString = false;
                    continue;
                }
                if (ch == '"') { inString = true; continue; }
                if (ch == '{') { if (depth == 0) start = i; depth++; continue; }
                if (ch != '}' || depth <= 0) continue;
                depth--;
                if (depth == 0 && start >= 0)
                {
                    result.Add(text.Substring(start, i - start + 1));
                    start = -1;
                    if (result.Count >= 16) break;
                }
            }
            return result;
        }
    }
}
