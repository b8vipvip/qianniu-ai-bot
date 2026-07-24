using BotLib;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Bot.ChromeNs
{
    internal sealed class ReplyQualityDailyMetric
    {
        public string Date { get; set; }
        public int RoutePreset { get; set; }
        public int RouteDirect { get; set; }
        public int RouteContextual { get; set; }
        public int RouteGeneralAi { get; set; }
        public int RouteVision { get; set; }
        public int RouteManual { get; set; }
        public int SemanticApplied { get; set; }
        public int SemanticFallback { get; set; }
        public int ValidationPass { get; set; }
        public int ValidationRegenerate { get; set; }
        public int ValidationManual { get; set; }
        public int ValidationSecondAttempt { get; set; }
        public int RepairSuccess { get; set; }
        public int RepairFailure { get; set; }
        public int DuplicateRewrite { get; set; }
        public int AnswerReady { get; set; }
        public int DisplayOnly { get; set; }
        public int SendAttempt { get; set; }
        public int SendSuccess { get; set; }
        public int SendFailure { get; set; }
        public int Cancelled { get; set; }
        public int Superseded { get; set; }
        public int HumanAccepted { get; set; }
        public int HumanCorrection { get; set; }
        public int HumanWithdrawCorrection { get; set; }
        public Dictionary<string, int> ValidationIssueCounts { get; set; }
        public List<int> AnswerLatencyMs { get; set; }
        public List<int> AiLatencyMs { get; set; }
        public List<int> TotalSendLatencyMs { get; set; }
        public List<int> SemanticLatencyMs { get; set; }

        public ReplyQualityDailyMetric()
        {
            ValidationIssueCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            AnswerLatencyMs = new List<int>();
            AiLatencyMs = new List<int>();
            TotalSendLatencyMs = new List<int>();
            SemanticLatencyMs = new List<int>();
        }
    }

    internal sealed class ReplyQualitySummary
    {
        public int Days { get; set; }
        public int TotalRoutes { get; set; }
        public int RoutePreset { get; set; }
        public int RouteDirect { get; set; }
        public int RouteContextual { get; set; }
        public int RouteGeneralAi { get; set; }
        public int RouteVision { get; set; }
        public int RouteManual { get; set; }
        public int SemanticApplied { get; set; }
        public int SemanticFallback { get; set; }
        public int ValidationPass { get; set; }
        public int ValidationRegenerate { get; set; }
        public int ValidationManual { get; set; }
        public int RepairSuccess { get; set; }
        public int RepairFailure { get; set; }
        public int SendAttempt { get; set; }
        public int SendSuccess { get; set; }
        public int SendFailure { get; set; }
        public int Cancelled { get; set; }
        public int Superseded { get; set; }
        public int HumanAccepted { get; set; }
        public int HumanCorrection { get; set; }
        public int HumanWithdrawCorrection { get; set; }
        public double DirectRate { get; set; }
        public double ValidatorPassRate { get; set; }
        public double RepairSuccessRate { get; set; }
        public double SendSuccessRate { get; set; }
        public double HumanCorrectionRate { get; set; }
        public int AverageAnswerLatencyMs { get; set; }
        public int P95AnswerLatencyMs { get; set; }
        public int AverageAiLatencyMs { get; set; }
        public int P95AiLatencyMs { get; set; }
        public int AverageTotalSendLatencyMs { get; set; }
        public int P95TotalSendLatencyMs { get; set; }
        public int QualityScore { get; set; }
        public string Recommendation { get; set; }
        public List<ReplyQualityDailyView> Daily { get; set; }
        public List<KeyValuePair<string, int>> TopValidationIssues { get; set; }

        public ReplyQualitySummary()
        {
            Recommendation = string.Empty;
            Daily = new List<ReplyQualityDailyView>();
            TopValidationIssues = new List<KeyValuePair<string, int>>();
        }
    }

    internal sealed class ReplyQualityDailyView
    {
        public string Date { get; set; }
        public int TotalRoutes { get; set; }
        public int Direct { get; set; }
        public int Contextual { get; set; }
        public int GeneralAi { get; set; }
        public int Vision { get; set; }
        public int Manual { get; set; }
        public int ValidatorPass { get; set; }
        public int ValidatorRepair { get; set; }
        public int ValidatorManual { get; set; }
        public int SendSuccess { get; set; }
        public int SendFailure { get; set; }
        public int HumanCorrections { get; set; }
        public int AverageAnswerMs { get; set; }
        public int P95AnswerMs { get; set; }
    }

    internal static class ReplyQualityMetricsService
    {
        private sealed class MetricsFile
        {
            public int Version { get; set; }
            public List<ReplyQualityDailyMetric> Days { get; set; }

            public MetricsFile()
            {
                Version = 1;
                Days = new List<ReplyQualityDailyMetric>();
            }
        }

        private static readonly object Sync = new object();
        private static MetricsFile _cache;
        private static bool _dirty;
        private static int _pendingWrites;
        private static Timer _flushTimer;

        public static event Action MetricsChanged;

        public static string MetricsFilePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "QianniuAiBot",
                    "data",
                    "reply-quality-metrics.json");
            }
        }

        public static string MetricsDirectory
        {
            get { return Path.GetDirectoryName(MetricsFilePath); }
        }

        public static void RecordRoute(string route, bool semanticApplied, long semanticLatencyMs)
        {
            Mutate(day =>
            {
                switch ((route ?? string.Empty).Trim().ToUpperInvariant())
                {
                    case "PRESET": day.RoutePreset++; break;
                    case "DIRECT_KNOWLEDGE": day.RouteDirect++; break;
                    case "CONTEXTUAL_KNOWLEDGE": day.RouteContextual++; break;
                    case "VISION": day.RouteVision++; break;
                    case "MANUAL": day.RouteManual++; break;
                    default: day.RouteGeneralAi++; break;
                }
                if (semanticApplied)
                {
                    day.SemanticApplied++;
                    AddSample(day.SemanticLatencyMs, semanticLatencyMs);
                }
                else
                {
                    day.SemanticFallback++;
                }
            });
        }

        public static void RecordValidation(
            AnswerValidationAction action,
            IEnumerable<string> issues,
            bool secondAttempt)
        {
            Mutate(day =>
            {
                if (action == AnswerValidationAction.Pass) day.ValidationPass++;
                else if (action == AnswerValidationAction.Regenerate) day.ValidationRegenerate++;
                else day.ValidationManual++;
                if (secondAttempt) day.ValidationSecondAttempt++;
                foreach (var category in CategorizeIssues(issues))
                {
                    int current;
                    day.ValidationIssueCounts.TryGetValue(category, out current);
                    day.ValidationIssueCounts[category] = current + 1;
                }
            });
        }

        public static void RecordRepair(bool success)
        {
            Mutate(day =>
            {
                if (success) day.RepairSuccess++;
                else day.RepairFailure++;
            });
        }

        public static void RecordDuplicateRewrite()
        {
            Mutate(day => day.DuplicateRewrite++);
        }

        public static void RecordAnswerReady(long aiLatencyMs, long answerLatencyMs, bool autoSend)
        {
            Mutate(day =>
            {
                day.AnswerReady++;
                if (!autoSend) day.DisplayOnly++;
                AddSample(day.AiLatencyMs, aiLatencyMs);
                AddSample(day.AnswerLatencyMs, answerLatencyMs);
            });
        }

        public static void RecordSendResult(bool success, long totalLatencyMs)
        {
            Mutate(day =>
            {
                day.SendAttempt++;
                if (success) day.SendSuccess++;
                else day.SendFailure++;
                AddSample(day.TotalSendLatencyMs, totalLatencyMs);
            });
        }

        public static void RecordCancellation(bool superseded)
        {
            Mutate(day =>
            {
                day.Cancelled++;
                if (superseded) day.Superseded++;
            });
        }

        public static void RecordHumanEvidence(string evidenceType)
        {
            evidenceType = (evidenceType ?? string.Empty).Trim().ToLowerInvariant();
            Mutate(day =>
            {
                if (evidenceType == "withdrawn_bot_then_manual")
                    day.HumanWithdrawCorrection++;
                else if (evidenceType == "manual_correction")
                    day.HumanCorrection++;
                else if (evidenceType == "human_confirmed"
                    || evidenceType == "manual_reply"
                    || evidenceType == "repeated_human_pattern")
                    day.HumanAccepted++;
            });
        }

        public static ReplyQualitySummary GetSummary(int days)
        {
            days = Math.Max(1, Math.Min(90, days));
            lock (Sync)
            {
                EnsureLoaded();
                var start = DateTime.Today.AddDays(-(days - 1));
                var selected = _cache.Days
                    .Where(x => x != null && ParseDate(x.Date) >= start)
                    .OrderBy(x => x.Date)
                    .ToList();
                return BuildSummary(days, selected);
            }
        }

        public static string FormatSummary(ReplyQualitySummary summary)
        {
            if (summary == null) return "暂无回复质量数据。";
            var sb = new StringBuilder();
            sb.AppendLine("回复质量中心（最近 " + summary.Days + " 天）")
                .AppendLine("质量分：" + summary.QualityScore + "/100")
                .AppendLine("路由总数：" + summary.TotalRoutes
                    + "，本地直答 " + summary.RouteDirect
                    + "，知识上下文 " + summary.RouteContextual
                    + "，通用AI " + summary.RouteGeneralAi
                    + "，视觉 " + summary.RouteVision
                    + "，人工/阻止 " + summary.RouteManual)
                .AppendLine("发送成功率：" + Percent(summary.SendSuccessRate)
                    + "（成功 " + summary.SendSuccess + " / 尝试 " + summary.SendAttempt + "）")
                .AppendLine("校验通过率：" + Percent(summary.ValidatorPassRate)
                    + "，要求重答 " + summary.ValidationRegenerate
                    + "，人工阻止 " + summary.ValidationManual)
                .AppendLine("校验重答成功率：" + Percent(summary.RepairSuccessRate))
                .AppendLine("答案耗时：平均 " + summary.AverageAnswerLatencyMs
                    + "ms，P95 " + summary.P95AnswerLatencyMs + "ms")
                .AppendLine("AI耗时：平均 " + summary.AverageAiLatencyMs
                    + "ms，P95 " + summary.P95AiLatencyMs + "ms")
                .AppendLine("完整发送耗时：平均 " + summary.AverageTotalSendLatencyMs
                    + "ms，P95 " + summary.P95TotalSendLatencyMs + "ms")
                .AppendLine("人工证据：确认 " + summary.HumanAccepted
                    + "，修改 " + summary.HumanCorrection
                    + "，撤回后纠正 " + summary.HumanWithdrawCorrection)
                .AppendLine("建议：" + summary.Recommendation);
            if (summary.TopValidationIssues.Count > 0)
            {
                sb.AppendLine("主要校验问题：") ;
                foreach (var item in summary.TopValidationIssues)
                    sb.AppendLine("- " + item.Key + "：" + item.Value);
            }
            return sb.ToString().Trim();
        }

        public static void Flush()
        {
            lock (Sync)
            {
                EnsureLoaded();
                SaveInternal();
            }
        }

        private static void Mutate(Action<ReplyQualityDailyMetric> mutation)
        {
            if (mutation == null) return;
            lock (Sync)
            {
                EnsureLoaded();
                var today = DateTime.Today.ToString("yyyy-MM-dd");
                var day = _cache.Days.FirstOrDefault(x => x != null && x.Date == today);
                if (day == null)
                {
                    day = new ReplyQualityDailyMetric { Date = today };
                    _cache.Days.Add(day);
                }
                Normalize(day);
                mutation(day);
                _cache.Days = _cache.Days
                    .Where(x => x != null && ParseDate(x.Date) >= DateTime.Today.AddDays(-89))
                    .OrderBy(x => x.Date)
                    .ToList();
                _dirty = true;
                _pendingWrites++;
                EnsureFlushTimer();
                if (_pendingWrites >= 25) SaveInternal();
            }
            var changed = MetricsChanged;
            if (changed != null)
            {
                try { changed(); } catch { }
            }
        }

        private static ReplyQualitySummary BuildSummary(int days, List<ReplyQualityDailyMetric> selected)
        {
            var summary = new ReplyQualitySummary { Days = days };
            var answerSamples = new List<int>();
            var aiSamples = new List<int>();
            var totalSamples = new List<int>();
            var issues = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var day in selected)
            {
                Normalize(day);
                summary.RoutePreset += day.RoutePreset;
                summary.RouteDirect += day.RouteDirect;
                summary.RouteContextual += day.RouteContextual;
                summary.RouteGeneralAi += day.RouteGeneralAi;
                summary.RouteVision += day.RouteVision;
                summary.RouteManual += day.RouteManual;
                summary.SemanticApplied += day.SemanticApplied;
                summary.SemanticFallback += day.SemanticFallback;
                summary.ValidationPass += day.ValidationPass;
                summary.ValidationRegenerate += day.ValidationRegenerate;
                summary.ValidationManual += day.ValidationManual;
                summary.RepairSuccess += day.RepairSuccess;
                summary.RepairFailure += day.RepairFailure;
                summary.SendAttempt += day.SendAttempt;
                summary.SendSuccess += day.SendSuccess;
                summary.SendFailure += day.SendFailure;
                summary.Cancelled += day.Cancelled;
                summary.Superseded += day.Superseded;
                summary.HumanAccepted += day.HumanAccepted;
                summary.HumanCorrection += day.HumanCorrection;
                summary.HumanWithdrawCorrection += day.HumanWithdrawCorrection;
                answerSamples.AddRange(day.AnswerLatencyMs);
                aiSamples.AddRange(day.AiLatencyMs);
                totalSamples.AddRange(day.TotalSendLatencyMs);
                foreach (var issue in day.ValidationIssueCounts)
                {
                    int current;
                    issues.TryGetValue(issue.Key, out current);
                    issues[issue.Key] = current + issue.Value;
                }
                summary.Daily.Add(new ReplyQualityDailyView
                {
                    Date = day.Date,
                    TotalRoutes = RouteCount(day),
                    Direct = day.RouteDirect,
                    Contextual = day.RouteContextual,
                    GeneralAi = day.RouteGeneralAi,
                    Vision = day.RouteVision,
                    Manual = day.RouteManual,
                    ValidatorPass = day.ValidationPass,
                    ValidatorRepair = day.ValidationRegenerate,
                    ValidatorManual = day.ValidationManual,
                    SendSuccess = day.SendSuccess,
                    SendFailure = day.SendFailure,
                    HumanCorrections = day.HumanCorrection + day.HumanWithdrawCorrection,
                    AverageAnswerMs = Average(day.AnswerLatencyMs),
                    P95AnswerMs = Percentile(day.AnswerLatencyMs, 0.95)
                });
            }
            summary.Daily = summary.Daily.OrderByDescending(x => x.Date).ToList();
            summary.TotalRoutes = summary.RoutePreset + summary.RouteDirect + summary.RouteContextual
                + summary.RouteGeneralAi + summary.RouteVision + summary.RouteManual;
            var validations = summary.ValidationPass + summary.ValidationRegenerate + summary.ValidationManual;
            var repairs = summary.RepairSuccess + summary.RepairFailure;
            var humanOutcomes = summary.HumanAccepted + summary.HumanCorrection + summary.HumanWithdrawCorrection;
            summary.DirectRate = Ratio(summary.RouteDirect, summary.TotalRoutes);
            summary.ValidatorPassRate = Ratio(summary.ValidationPass, validations);
            summary.RepairSuccessRate = Ratio(summary.RepairSuccess, repairs);
            summary.SendSuccessRate = Ratio(summary.SendSuccess, summary.SendAttempt);
            summary.HumanCorrectionRate = Ratio(summary.HumanCorrection + summary.HumanWithdrawCorrection, humanOutcomes);
            summary.AverageAnswerLatencyMs = Average(answerSamples);
            summary.P95AnswerLatencyMs = Percentile(answerSamples, 0.95);
            summary.AverageAiLatencyMs = Average(aiSamples);
            summary.P95AiLatencyMs = Percentile(aiSamples, 0.95);
            summary.AverageTotalSendLatencyMs = Average(totalSamples);
            summary.P95TotalSendLatencyMs = Percentile(totalSamples, 0.95);
            summary.TopValidationIssues = issues.OrderByDescending(x => x.Value).Take(5).ToList();
            summary.QualityScore = CalculateQualityScore(summary, validations);
            summary.Recommendation = BuildRecommendation(summary, validations);
            return summary;
        }

        private static int CalculateQualityScore(ReplyQualitySummary summary, int validations)
        {
            if (summary.TotalRoutes == 0 && summary.SendAttempt == 0 && validations == 0) return 0;
            var score = 100.0;
            if (summary.SendAttempt > 0) score -= (1.0 - summary.SendSuccessRate) * 35.0;
            if (validations > 0)
            {
                score -= Ratio(summary.ValidationManual, validations) * 25.0;
                score -= Ratio(summary.ValidationRegenerate, validations) * 10.0;
            }
            score -= summary.HumanCorrectionRate * 25.0;
            if (summary.P95AnswerLatencyMs > 12000) score -= 15;
            else if (summary.P95AnswerLatencyMs > 8000) score -= 10;
            else if (summary.P95AnswerLatencyMs > 5000) score -= 5;
            return Math.Max(0, Math.Min(100, (int)Math.Round(score)));
        }

        private static string BuildRecommendation(ReplyQualitySummary summary, int validations)
        {
            var recommendations = new List<string>();
            if (summary.SendAttempt >= 10 && summary.SendSuccessRate < 0.98)
                recommendations.Add("真实发送失败率偏高，优先检查千牛输入框、发送按钮和会话确认日志");
            if (validations >= 10 && Ratio(summary.ValidationManual, validations) > 0.03)
                recommendations.Add("发送前人工阻止偏多，检查店铺固定提示词、知识适用条件和模型输出");
            if (summary.RepairFailure > 0 && summary.RepairSuccessRate < 0.80)
                recommendations.Add("校验重答成功率偏低，考虑加强知识事实边界或更换文本模型");
            if (summary.HumanCorrectionRate > 0.08)
                recommendations.Add("人工修改率偏高，重点检查低可靠度知识和上下文路由");
            if (summary.P95AnswerLatencyMs > 8000)
                recommendations.Add("P95答案延迟偏高，检查语义模型、主文本模型和控制面最近路由");
            if (summary.SemanticApplied == 0 && summary.TotalRoutes >= 20)
                recommendations.Add("尚未观测到语义检索，可确认是否配置了可用的Embedding模型");
            if (recommendations.Count == 0)
                recommendations.Add("当前指标未发现明显异常，继续积累真实接待数据后再调整路由阈值");
            return string.Join("；", recommendations);
        }

        private static IEnumerable<string> CategorizeIssues(IEnumerable<string> issues)
        {
            foreach (var issue in issues ?? Enumerable.Empty<string>())
            {
                var value = issue ?? string.Empty;
                if (value.Contains("具体数字") || value.Contains("时效")) yield return "无依据数字/时效";
                else if (value.Contains("绝对承诺")) yield return "无依据绝对承诺";
                else if (value.Contains("订单") || value.Contains("售后状态")) yield return "虚构订单/售后状态";
                else if (value.Contains("知识") || value.Contains("结论相反")) yield return "知识事实冲突";
                else if (value.Contains("机器化") || value.Contains("AI")) yield return "机器化措辞";
                else if (value.Contains("没有回答") || value.Contains("过于笼统")) yield return "未回答当前问题";
                else if (value.Contains("过长")) yield return "答案过长";
                else yield return "其他校验问题";
            }
        }

        private static void AddSample(List<int> samples, long value)
        {
            if (samples == null || value < 0) return;
            samples.Add((int)Math.Min(int.MaxValue, value));
            if (samples.Count > 240) samples.RemoveRange(0, samples.Count - 240);
        }

        private static int RouteCount(ReplyQualityDailyMetric day)
        {
            return day.RoutePreset + day.RouteDirect + day.RouteContextual
                + day.RouteGeneralAi + day.RouteVision + day.RouteManual;
        }

        private static int Average(IEnumerable<int> values)
        {
            var list = (values ?? Enumerable.Empty<int>()).Where(x => x >= 0).ToList();
            return list.Count == 0 ? 0 : (int)Math.Round(list.Average());
        }

        private static int Percentile(IEnumerable<int> values, double percentile)
        {
            var list = (values ?? Enumerable.Empty<int>()).Where(x => x >= 0).OrderBy(x => x).ToList();
            if (list.Count == 0) return 0;
            if (list.Count == 1) return list[0];
            var position = (list.Count - 1) * percentile;
            var lower = (int)Math.Floor(position);
            var upper = (int)Math.Ceiling(position);
            if (lower == upper) return list[lower];
            return (int)Math.Round(list[lower] + (list[upper] - list[lower]) * (position - lower));
        }

        private static double Ratio(int numerator, int denominator)
        {
            return denominator <= 0 ? 0 : Math.Max(0, Math.Min(1, numerator / (double)denominator));
        }

        private static string Percent(double value)
        {
            return (Math.Max(0, Math.Min(1, value)) * 100).ToString("0.0") + "%";
        }

        private static void EnsureLoaded()
        {
            if (_cache != null) return;
            try
            {
                if (!File.Exists(MetricsFilePath))
                {
                    _cache = new MetricsFile();
                    return;
                }
                _cache = JsonConvert.DeserializeObject<MetricsFile>(File.ReadAllText(MetricsFilePath, Encoding.UTF8))
                    ?? new MetricsFile();
                if (_cache.Days == null) _cache.Days = new List<ReplyQualityDailyMetric>();
                foreach (var day in _cache.Days) Normalize(day);
            }
            catch (Exception ex)
            {
                Log.Info("读取回复质量指标失败，使用空指标：" + ex.Message);
                _cache = new MetricsFile();
            }
        }

        private static void Normalize(ReplyQualityDailyMetric day)
        {
            if (day == null) return;
            if (day.ValidationIssueCounts == null)
                day.ValidationIssueCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (day.AnswerLatencyMs == null) day.AnswerLatencyMs = new List<int>();
            if (day.AiLatencyMs == null) day.AiLatencyMs = new List<int>();
            if (day.TotalSendLatencyMs == null) day.TotalSendLatencyMs = new List<int>();
            if (day.SemanticLatencyMs == null) day.SemanticLatencyMs = new List<int>();
        }

        private static void EnsureFlushTimer()
        {
            if (_flushTimer != null) return;
            _flushTimer = new Timer(_ =>
            {
                try { Flush(); } catch { }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        private static void SaveInternal()
        {
            if (!_dirty || _cache == null) return;
            try
            {
                var directory = MetricsDirectory;
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                var temp = MetricsFilePath + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(_cache, Formatting.Indented), new UTF8Encoding(false));
                if (File.Exists(MetricsFilePath)) File.Delete(MetricsFilePath);
                File.Move(temp, MetricsFilePath);
                _dirty = false;
                _pendingWrites = 0;
            }
            catch (Exception ex)
            {
                Log.Info("保存回复质量指标失败：" + ex.Message);
            }
        }

        private static DateTime ParseDate(string value)
        {
            DateTime parsed;
            return DateTime.TryParse(value, out parsed) ? parsed.Date : DateTime.MinValue;
        }
    }
}
