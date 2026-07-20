using Bot.AssistWindow.Widget.Robot;
using Bot.Automation.ChatDeskNs;
using System;
using System.Collections.Concurrent;

namespace Bot.ChromeNs
{
    internal static class ResponseProgressTracker
    {
        private sealed class Entry
        {
            public readonly object Sync = new object();
            public CtlConversation Control;
            public string Question = string.Empty;
            public DateTime DetectedAt = DateTime.MinValue;
        }

        private static readonly ConcurrentDictionary<string, Entry> Entries =
            new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);

        private static string Key(string seller, string buyer)
        {
            return (seller ?? string.Empty).Trim() + "#" + (buyer ?? string.Empty).Trim();
        }

        public static CtlConversation ObserveQuestion(
            string seller,
            string buyer,
            string question,
            DateTime detectedAt)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            question = (question ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer)) return null;

            var entry = Entries.GetOrAdd(Key(seller, buyer), _ => new Entry());
            lock (entry.Sync)
            {
                if (entry.DetectedAt == DateTime.MinValue || detectedAt < entry.DetectedAt)
                {
                    entry.DetectedAt = detectedAt == DateTime.MinValue ? DateTime.Now : detectedAt;
                }
                entry.Question = MergeQuestion(entry.Question, question);
                if (entry.Control == null && Desk.Inst != null)
                {
                    entry.Control = Desk.Inst.AddConversation(
                        seller,
                        buyer,
                        entry.Question,
                        "正在识别并等待买家本轮消息结束...",
                        false,
                        "处理中");
                }
                if (entry.Control != null)
                {
                    entry.Control.SetQuestion(entry.Question, entry.DetectedAt);
                    entry.Control.SetProcessing("已识别，等待合并本轮消息...");
                }
                return entry.Control;
            }
        }

        public static CtlConversation BeginAnswer(
            string seller,
            string buyer,
            string combinedQuestion,
            DateTime detectedAt)
        {
            var control = ObserveQuestion(seller, buyer, combinedQuestion, detectedAt);
            if (control != null) control.SetProcessing("正在获取答案...");
            return control;
        }

        public static CtlConversation SetAnswerReady(
            string seller,
            string buyer,
            string question,
            string answer,
            string source,
            DateTime detectedAt,
            DateTime answerReadyAt)
        {
            var control = ObserveQuestion(seller, buyer, question, detectedAt);
            if (control != null)
            {
                control.SetAnswer(answer, source, answerReadyAt);
                control.SetSendPending("答案已生成，准备发送...");
            }
            return control;
        }

        public static void Fail(string seller, string buyer, string detail)
        {
            Entry entry;
            if (!Entries.TryRemove(Key(seller, buyer), out entry) || entry == null) return;
            lock (entry.Sync)
            {
                if (entry.Control != null)
                {
                    entry.Control.SetAnswer(detail ?? string.Empty, "系统", DateTime.Now);
                    entry.Control.SetSkipped(detail);
                }
            }
        }

        public static void Complete(string seller, string buyer)
        {
            Entry ignored;
            Entries.TryRemove(Key(seller, buyer), out ignored);
        }

        private static string MergeQuestion(string existing, string latest)
        {
            existing = (existing ?? string.Empty).Trim();
            latest = (latest ?? string.Empty).Trim();
            if (latest.Length == 0) return existing;
            if (existing.Length == 0) return latest;
            if (string.Equals(existing, latest, StringComparison.Ordinal)) return existing;
            foreach (var line in existing.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(line.Trim(), latest, StringComparison.Ordinal)) return existing;
            }
            var merged = existing + "\n" + latest;
            return merged.Length <= 1600 ? merged : merged.Substring(merged.Length - 1600);
        }
    }
}
