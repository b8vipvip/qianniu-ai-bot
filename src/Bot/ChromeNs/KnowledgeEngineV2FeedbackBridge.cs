using Bot.Knowledge;
using BotLib;
using DbEntity.Response;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Bot.ChromeNs
{
    internal static class KnowledgeEngineV2FeedbackBridge
    {
        private static readonly ConcurrentDictionary<int, bool> Attached =
            new ConcurrentDictionary<int, bool>();
        private static readonly ConcurrentDictionary<string, DateTime> Seen =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly DateTime StartedAt = DateTime.Now;
        private static Timer _timer;
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            AttachExisting();
            _timer = new Timer(_ => AttachExisting(), null, 400, 700);
            Log.Info("Knowledge Engine V2反馈观察器已启动：发送成功、明确认可、人工纠正和撤回将进入本地质量闭环；发送失败仅作为传输指标。" );
        }

        private static void AttachExisting()
        {
            try
            {
                QN[] qns;
                try { qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray(); }
                catch { return; }
                foreach (var qn in qns)
                {
                    if (qn == null) continue;
                    var key = RuntimeHelpers.GetHashCode(qn);
                    if (!Attached.TryAdd(key, true)) continue;
                    var captured = qn;
                    captured.EvRecieveNewMessage += (s, e) => OnRawMessages(captured, e);
                }
                CleanupSeen();
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("安装Knowledge V2反馈观察器失败: " + ex.Message, 10);
            }
        }

        private static void OnRawMessages(QN qn, RecieveNewMessageEventArgs e)
        {
            if (qn == null || e == null || string.IsNullOrWhiteSpace(e.Message)) return;
            try
            {
                var response = JsonConvert.DeserializeObject<ChatResponse>(e.Message);
                if (response == null || response.result == null) return;
                foreach (var message in response.result.Where(x => x != null).OrderBy(IncomingMessageSafety.GetSortValue))
                    ObserveMessage(qn, message);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("解析Knowledge V2反馈消息失败: " + ex.Message, 10);
            }
        }

        private static void ObserveMessage(QN qn, QNChatMessage message)
        {
            if (message == null) return;
            var messageTime = GetMessageTime(message);
            if (messageTime != DateTime.MinValue && messageTime < StartedAt.AddSeconds(-8)) return;

            var text = ExtractMessageText(message);
            var messageKey = IncomingMessageSafety.BuildMessageKey(message, text);
            if (!string.IsNullOrWhiteSpace(messageKey) && !Seen.TryAdd("v2-feedback:" + messageKey, DateTime.Now)) return;

            var seller = qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(seller) && message.loginid != null)
                seller = (message.loginid.nick ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(seller)) return;

            var from = message.fromid == null ? string.Empty : (message.fromid.nick ?? string.Empty).Trim();
            var to = message.toid == null ? string.Empty : (message.toid.nick ?? string.Empty).Trim();
            var buyer = from == seller ? to : (to == seller ? from : string.Empty);
            if (string.IsNullOrWhiteSpace(buyer)) return;

            var isWithdrawal = ConversationContextStore.IsWithdrawalNotice(message, text);
            if (isWithdrawal)
            {
                KnowledgeEngineV2FeedbackService.ObserveWithdrawal(seller, buyer, text);
                return;
            }
            if (ConversationContextStore.IsPlatformSystemTip(message, text)) return;

            if (from == buyer && to == seller)
                KnowledgeEngineV2FeedbackService.ObserveBuyerMessage(seller, buyer, text);
            else if (from == seller && to == buyer)
                KnowledgeEngineV2FeedbackService.ObserveSellerMessage(seller, buyer, text);
        }

        private static string ExtractMessageText(QNChatMessage message)
        {
            if (message == null) return string.Empty;
            try
            {
                if (message.originalData != null)
                {
                    var text = message.originalData.text ?? string.Empty;
                    if (message.originalData.header != null)
                        text += message.originalData.header.summary ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                }
            }
            catch { }
            return (message.summary ?? string.Empty).Trim();
        }

        private static DateTime GetMessageTime(QNChatMessage message)
        {
            if (message == null) return DateTime.MinValue;
            DateTime result;
            if (TryParseTime(message.sendTime, out result)) return result;
            if (TryParseTime(message.sortTimeMicrosecond, out result)) return result;
            return DateTime.MinValue;
        }

        private static bool TryParseTime(string value, out DateTime result)
        {
            result = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(value)) return false;
            long raw;
            if (long.TryParse(value.Trim(), out raw))
            {
                try
                {
                    if (raw > 1000000000000000L) result = DateTimeOffset.FromUnixTimeMilliseconds(raw / 1000L).LocalDateTime;
                    else if (raw > 100000000000L) result = DateTimeOffset.FromUnixTimeMilliseconds(raw).LocalDateTime;
                    else if (raw > 1000000000L) result = DateTimeOffset.FromUnixTimeSeconds(raw).LocalDateTime;
                    if (result != DateTime.MinValue) return true;
                }
                catch { }
            }
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dto)
                || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dto))
            {
                result = dto.LocalDateTime;
                return true;
            }
            return false;
        }

        private static void CleanupSeen()
        {
            if (Seen.Count < 5000) return;
            var cutoff = DateTime.Now.AddHours(-2);
            foreach (var key in Seen.Where(x => x.Value < cutoff).Select(x => x.Key).Take(2500).ToList())
            {
                DateTime ignored;
                Seen.TryRemove(key, out ignored);
            }
        }
    }
}
