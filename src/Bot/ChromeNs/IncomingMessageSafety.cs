using Bot.ChatRecord;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Bot.ChromeNs
{
    internal sealed class IncomingMessageDecision
    {
        public bool ShouldCallAi { get; set; }
        public string MessageLabel { get; set; }
        public string Note { get; set; }
    }

    internal sealed class IncomingMessageDeduplicator
    {
        private readonly object _sync = new object();
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> _order = new Queue<string>();
        private readonly int _capacity;

        public IncomingMessageDeduplicator(int capacity)
        {
            _capacity = Math.Max(100, capacity);
        }

        public bool TryAccept(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return true;
            lock (_sync)
            {
                if (_seen.Contains(key)) return false;
                _seen.Add(key);
                _order.Enqueue(key);
                while (_order.Count > _capacity)
                {
                    var old = _order.Dequeue();
                    _seen.Remove(old);
                }
                return true;
            }
        }
    }

    internal static class IncomingMessageSafety
    {
        private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic" };
        private static readonly string[] VideoExtensions = { ".mp4", ".mov", ".avi", ".mkv", ".webm", ".m4v" };
        private static readonly string[] AudioExtensions = { ".amr", ".mp3", ".wav", ".m4a", ".aac", ".ogg" };

        public static IncomingMessageDecision Evaluate(QNChatMessage message, string messageText, DateTime safetyStartedAt)
        {
            // Refresh the same seller+buyer timeline before deciding how to answer. This loads
            // recent manual-agent questions as well as buyer replies such as phone/model/account IDs.
            ConversationContextStore.RefreshAndRecord(message, messageText);

            DateTime messageTime;
            if (TryGetMessageTime(message, out messageTime) && messageTime < safetyStartedAt.AddSeconds(-8))
            {
                return Skip("历史消息", "已跳过：这是 Bot 启动前的历史或未读消息，未调用AI，也未发送给买家。");
            }

            if (ConversationContextStore.IsWithdrawalNotice(message, messageText))
            {
                return Skip("[撤回提示]", "已跳过：检测到消息撤回提示，未调用AI，也未发送给买家。");
            }

            if (ConversationContextStore.IsPlatformSystemTip(message, messageText))
            {
                return Skip("[淘宝系统提示]", "已跳过：这是淘宝/千牛自动生成的系统提示，未调用AI，也未发送给买家。");
            }

            // Product links and product cards use a local preset reply. ShouldCallAi remains true
            // only so the existing UI/send pipeline is reused; MyOpenAI returns before any HTTP call.
            if (ConversationContextStore.IsProductLink(message, messageText))
            {
                ConversationContextStore.RegisterProductLinkReply(message, messageText);
                return new IncomingMessageDecision
                {
                    ShouldCallAi = true,
                    MessageLabel = string.IsNullOrWhiteSpace(messageText) ? "[商品链接]" : messageText,
                    Note = "商品链接使用本地预设随机回复，不调用AI接口。"
                };
            }

            var unsupportedType = DetectUnsupportedType(message, messageText);
            if (!string.IsNullOrWhiteSpace(unsupportedType))
            {
                return Skip("[" + unsupportedType + "]", "已跳过：收到" + unsupportedType + "消息，当前版本未启用对应内容理解能力；未调用AI，也未发送给买家。");
            }

            if (string.IsNullOrWhiteSpace(messageText))
            {
                return Skip("[空白或未知消息]", "已跳过：消息内容为空或类型无法识别，未调用AI，也未发送给买家。");
            }

            return new IncomingMessageDecision
            {
                ShouldCallAi = true,
                MessageLabel = messageText,
                Note = string.Empty
            };
        }

        public static string GetDisplayText(QNChatMessage message, string messageText)
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
        {
            if (message == null) return string.Empty;
            if (message.mcode != null && (!string.IsNullOrWhiteSpace(message.mcode.clientId) || !string.IsNullOrWhiteSpace(message.mcode.messageId)))
            {
                return "mcode:" + (message.mcode.clientId ?? string.Empty) + ":" + (message.mcode.messageId ?? string.Empty);
            }
            if (message.ext != null && message.ext.ww_msgid != 0)
            {
                return "ww:" + message.ext.ww_msgid;
            }
            return string.Join("|", new[]
            {
                message.cid == null ? string.Empty : message.cid.ccode,
                message.fromid == null ? string.Empty : message.fromid.nick,
                message.toid == null ? string.Empty : message.toid.nick,
                message.sendTime ?? string.Empty,
                message.sortTimeMicrosecond ?? string.Empty,
                message.templateId.ToString(CultureInfo.InvariantCulture),
                messageText ?? string.Empty,
                message.originalData == null ? string.Empty : (message.originalData.fileId ?? string.Empty)
            });
        }

        public static long GetSortValue(QNChatMessage message)
        {
            if (message == null) return 0;
            DateTime time;
            if (TryGetMessageTime(message, out time)) return time.Ticks;
            long raw;
            if (long.TryParse(message.sortTimeMicrosecond, out raw)) return raw;
            return 0;
        }

        private static IncomingMessageDecision Skip(string label, string note)
        {
            return new IncomingMessageDecision { ShouldCallAi = false, MessageLabel = label, Note = note };
        }

        private static string DetectUnsupportedType(QNChatMessage message, string messageText)
        {
            if (message == null) return "未知类型";
            var original = message.originalData;
            var fileId = original == null ? string.Empty : (original.fileId ?? string.Empty);
            var url = original == null ? string.Empty : (original.url ?? string.Empty);
            var combined = ((messageText ?? string.Empty) + " " + (message.summary ?? string.Empty)).Trim().ToLowerInvariant();

            if (HasExtension(fileId, ImageExtensions) || HasExtension(url, ImageExtensions) || ContainsMarker(combined, "图片", "image", "photo")) return "图片";
            if (HasExtension(fileId, VideoExtensions) || HasExtension(url, VideoExtensions) || ContainsMarker(combined, "视频", "video")) return "视频";
            if (HasExtension(fileId, AudioExtensions) || HasExtension(url, AudioExtensions) || ContainsMarker(combined, "语音", "音频", "voice", "audio")) return "语音";
            if (ContainsMarker(combined, "表情", "emoji", "emotion", "face")
                || combined.Contains("发送了一个表情")
                || combined.Contains("动态表情")) return "表情";
            if (ContainsMarker(combined, "位置", "定位", "location")) return "位置";
            if (ContainsMarker(combined, "文件", "附件", "file")) return "文件";
            if (!string.IsNullOrWhiteSpace(fileId)) return "文件";
            return string.Empty;
        }

        private static bool ContainsMarker(string text, params string[] markers)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (var marker in markers)
            {
                if (text == "[" + marker + "]" || text == marker || text.Contains("[" + marker + "]")) return true;
            }
            return false;
        }

        private static bool HasExtension(string value, IEnumerable<string> extensions)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var clean = value.Split('?', '#')[0].ToLowerInvariant();
            return extensions.Any(clean.EndsWith);
        }

        private static bool TryGetMessageTime(QNChatMessage message, out DateTime localTime)
        {
            localTime = DateTime.MinValue;
            if (message == null) return false;
            if (TryParseTimeValue(message.sendTime, out localTime)) return true;
            return TryParseTimeValue(message.sortTimeMicrosecond, out localTime);
        }

        private static bool TryParseTimeValue(string value, out DateTime localTime)
        {
            localTime = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(value)) return false;
            long raw;
            if (long.TryParse(value.Trim(), out raw))
            {
                try
                {
                    if (raw > 1000000000000000L) localTime = DateTimeOffset.FromUnixTimeMilliseconds(raw / 1000L).LocalDateTime;
                    else if (raw > 100000000000L) localTime = DateTimeOffset.FromUnixTimeMilliseconds(raw).LocalDateTime;
                    else if (raw > 1000000000L) localTime = DateTimeOffset.FromUnixTimeSeconds(raw).LocalDateTime;
                    if (localTime != DateTime.MinValue) return true;
                }
                catch
                {
                }
            }

            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dto)
                || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dto))
            {
                localTime = dto.LocalDateTime;
                return true;
            }
            return false;
        }
    }
}
