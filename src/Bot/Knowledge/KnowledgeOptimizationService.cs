using Bot.ChromeNs;
using BotLib.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Knowledge
{
    internal sealed class KnowledgeOptimizationProgress
    {
        public int BatchIndex { get; set; }
        public int BatchCount { get; set; }
        public int Processed { get; set; }
        public int Changed { get; set; }
        public int Kept { get; set; }
        public int Failed { get; set; }
        public string Message { get; set; }
    }

    internal sealed class KnowledgeOptimizationResult
    {
        public int Total { get; set; }
        public int Changed { get; set; }
        public int Kept { get; set; }
        public int Failed { get; set; }
        public string BackupPath { get; set; }
    }

    internal static class KnowledgeOptimizationService
    {
        private const int BatchSize = 12;

        private const string SystemPrompt =
            "你是电商客服知识库质量审校助手。任务是修复已有问答，而不是重新编造知识。" +
            "重点检查：答案是否明显截断、句子是否不完整、问题与答案是否答非所问、表达是否含糊、重复或不自然。" +
            "只能依据当前问答已有事实进行改写；不得新增价格、库存、发货时效、到账时间、售后承诺、账号规则或操作步骤。" +
            "遇到明显截断时，只允许把已有语义补成不增加新事实的完整表达；无法确定缺失内容时应保守改写或保持原文。" +
            "人工维护来源的内容除非有明显错字、截断或语病，否则保持不变。" +
            "只输出严格JSON：{\"items\":[{\"id\":\"原ID\",\"action\":\"keep或update\",\"question\":\"问题\",\"answer\":\"答案\",\"category\":\"分类\",\"keywords\":[\"关键词\"],\"reason\":\"简短原因\"}]}。";

        public static bool IsAiManagedSource(KnowledgeBaseEntry item)
        {
            if (item == null) return false;
            if (item.AiGenerated) return true;
            var source = (item.SourceType ?? string.Empty).Trim();
            return source.IndexOf("智能", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("扫描", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("AI", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("自动学习", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static async Task<KnowledgeOptimizationResult> OptimizeAsync(
            IList<KnowledgeBaseEntry> selected,
            Action<KnowledgeOptimizationProgress> progress,
            CancellationToken token)
        {
            var all = BotFeatureStore.GetKnowledgeBase() ?? new List<KnowledgeBaseEntry>();
            var selectedIds = new HashSet<string>(
                (selected ?? new List<KnowledgeBaseEntry>())
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id))
                    .Select(x => x.Id),
                StringComparer.Ordinal);
            var targets = all
                .Where(x => x != null
                    && x.Enabled
                    && !string.IsNullOrWhiteSpace(x.Title)
                    && !string.IsNullOrWhiteSpace(x.Answer)
                    && (selectedIds.Count == 0 || selectedIds.Contains(x.Id)))
                .ToList();

            if (targets.Count == 0)
            {
                return new KnowledgeOptimizationResult { Total = 0 };
            }

            var backupPath = Backup(all);
            var result = new KnowledgeOptimizationResult
            {
                Total = targets.Count,
                BackupPath = backupPath
            };
            var batches = targets
                .Select((item, index) => new { item, index })
                .GroupBy(x => x.index / BatchSize)
                .Select(x => x.Select(y => y.item).ToList())
                .ToList();

            for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                token.ThrowIfCancellationRequested();
                var batch = batches[batchIndex];
                Report(progress, batchIndex + 1, batches.Count, result, "正在调用AI审校本批问答...");

                try
                {
                    var changes = await OptimizeBatchAsync(batch, token);
                    foreach (var item in batch)
                    {
                        token.ThrowIfCancellationRequested();
                        JObject change;
                        if (!changes.TryGetValue(item.Id ?? string.Empty, out change))
                        {
                            result.Kept++;
                            continue;
                        }

                        var action = Convert.ToString(change["action"]).Trim();
                        if (!string.Equals(action, "update", StringComparison.OrdinalIgnoreCase))
                        {
                            result.Kept++;
                            continue;
                        }

                        var answer = NormalizeDisplay(Convert.ToString(change["answer"]));
                        var question = NormalizeDisplay(Convert.ToString(change["question"]));
                        var category = NormalizeDisplay(Convert.ToString(change["category"]));
                        var keywords = ReadKeywords(change["keywords"]);

                        if (!IsSafeUpdate(item, question, answer))
                        {
                            result.Kept++;
                            continue;
                        }

                        item.Answer = answer;
                        if (!string.IsNullOrWhiteSpace(question)) item.Title = question;
                        if (!string.IsNullOrWhiteSpace(category)) item.Category = category;
                        if (!string.IsNullOrWhiteSpace(keywords)) item.Keywords = keywords;
                        item.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        result.Changed++;
                    }

                    BotFeatureStore.SaveKnowledgeBase(all);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result.Failed += batch.Count;
                    BotLib.Log.Info("批量优化知识库失败，保留本批原内容：batch=" + (batchIndex + 1) + ", error=" + ex.Message);
                }

                Report(progress, batchIndex + 1, batches.Count, result, "本批处理完成");
            }

            BotFeatureStore.SaveKnowledgeBase(all);
            return result;
        }

        private static async Task<Dictionary<string, JObject>> OptimizeBatchAsync(
            IList<KnowledgeBaseEntry> batch,
            CancellationToken token)
        {
            var payload = new JArray();
            foreach (var item in batch)
            {
                payload.Add(new JObject
                {
                    ["id"] = item.Id ?? string.Empty,
                    ["source"] = item.SourceType ?? string.Empty,
                    ["aiGenerated"] = item.AiGenerated,
                    ["category"] = item.Category ?? string.Empty,
                    ["question"] = item.Title ?? string.Empty,
                    ["answer"] = item.Answer ?? string.Empty,
                    ["keywords"] = item.Keywords ?? string.Empty
                });
            }

            var messages = new JArray
            {
                new JObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JObject
                {
                    ["role"] = "user",
                    ["content"] = "请逐条审校以下已有知识。不要因为可以润色就强制修改；只有确有质量问题才返回update。\n" +
                        payload.ToString(Formatting.None)
                }
            };

            var response = await Task.Run(
                () => MyOpenAI.CallStructuredChat(messages, 5000, 0.05, 150, token),
                token);
            if (response == null || !response.Success)
            {
                throw new Exception(response == null ? "AI未返回结果" : response.Error);
            }

            var text = KnowledgeAiService.StripFence(response.Answer);
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start) throw new Exception("AI优化结果不是完整JSON");
            var root = JObject.Parse(text.Substring(start, end - start + 1));
            var items = root["items"] as JArray;
            if (items == null) throw new Exception("AI优化结果缺少items数组");

            return items.OfType<JObject>()
                .Where(x => !string.IsNullOrWhiteSpace(Convert.ToString(x["id"])))
                .GroupBy(x => Convert.ToString(x["id"]), StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Last(), StringComparer.Ordinal);
        }

        private static bool IsSafeUpdate(KnowledgeBaseEntry original, string question, string answer)
        {
            if (original == null || string.IsNullOrWhiteSpace(answer)) return false;
            if (answer.StartsWith("错误：", StringComparison.Ordinal)) return false;
            if (answer.Length > Math.Max(1200, (original.Answer ?? string.Empty).Length * 4 + 160)) return false;
            if (!string.IsNullOrWhiteSpace(question) && question.Length > 300) return false;

            // AI审校不得偷偷加入原答案完全没有出现的价格/金额表达。
            var oldHasMoney = Regex.IsMatch(original.Answer ?? string.Empty, @"(?:￥|¥|\d+(?:\.\d+)?\s*元)");
            var newHasMoney = Regex.IsMatch(answer, @"(?:￥|¥|\d+(?:\.\d+)?\s*元)");
            if (!oldHasMoney && newHasMoney) return false;
            return true;
        }

        private static string ReadKeywords(JToken token)
        {
            if (token == null) return string.Empty;
            var array = token as JArray;
            if (array != null)
            {
                return string.Join(",", array
                    .Select(x => NormalizeDisplay(Convert.ToString(x)))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            }
            return NormalizeDisplay(Convert.ToString(token));
        }

        private static string NormalizeDisplay(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");
        }

        private static string Backup(IList<KnowledgeBaseEntry> all)
        {
            try
            {
                var dir = Path.Combine(PathEx.DataDir, "knowledge-backups");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "knowledge-before-optimize-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                File.WriteAllText(path, JsonConvert.SerializeObject(all, Formatting.Indented), System.Text.Encoding.UTF8);
                return path;
            }
            catch (Exception ex)
            {
                BotLib.Log.Info("优化知识库前自动备份失败，继续使用数据库现有数据：" + ex.Message);
                return string.Empty;
            }
        }

        private static void Report(
            Action<KnowledgeOptimizationProgress> progress,
            int batchIndex,
            int batchCount,
            KnowledgeOptimizationResult result,
            string message)
        {
            if (progress == null) return;
            progress(new KnowledgeOptimizationProgress
            {
                BatchIndex = batchIndex,
                BatchCount = batchCount,
                Processed = Math.Min(result.Total, result.Changed + result.Kept + result.Failed),
                Changed = result.Changed,
                Kept = result.Kept,
                Failed = result.Failed,
                Message = message ?? string.Empty
            });
        }
    }
}
