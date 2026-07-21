using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal static class SendFailureAnomalyService
    {
        public static void Queue(
            string seller,
            string buyer,
            string question,
            string answer,
            string source,
            string failureReason,
            DateTime detectedAt,
            DateTime answerReadyAt,
            DateTime failureDetectedAt)
        {
            var failureAt = failureDetectedAt == DateTime.MinValue ? DateTime.Now : failureDetectedAt;
            var readyAt = answerReadyAt == DateTime.MinValue ? failureAt : answerReadyAt;
            var detected = detectedAt == DateTime.MinValue ? readyAt : detectedAt;
            var report = new SlowResponseAnomalyReport
            {
                Id = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.Now,
                Seller = (seller ?? string.Empty).Trim(),
                Buyer = (buyer ?? string.Empty).Trim(),
                Question = Limit(question, 2400),
                Answer = Limit(answer, 2400),
                AnswerSource = "发送异常 / " + Limit(source, 160),
                DetectedAt = detected,
                AnswerStartedAt = readyAt,
                AnswerReadyAt = failureAt,
                TotalMilliseconds = Math.Max(0, (long)(failureAt - detected).TotalMilliseconds),
                QueueMilliseconds = Math.Max(0, (long)(readyAt - detected).TotalMilliseconds),
                GenerationMilliseconds = Math.Max(0, (long)(failureAt - readyAt).TotalMilliseconds),
                Severity = "高（发送失败）",
                AnalysisStatus = "等待AI分析",
                Summary = "答案已经获取，但未确认真实发送给买家。已触发发送链路异常分析。",
                LikelyCause = Limit(failureReason, 2000),
                Evidence = "发送监控在等待卖家消息回显后仍未发现与目标答案一致的实际发送记录。",
                Recommendations = "检查是否误走 SendSmartTipMsg 智能提示接口、目标会话是否正确、输入框是否真正写入，以及发送后是否收到卖家消息回显。",
                NotificationStatus = "发送异常已写入异常报告列表"
            };

            SaveReport(report);
            Log.Error("[发送失败异常报告] seller=" + report.Seller
                + ", buyer=" + report.Buyer + ", reportId=" + report.Id
                + ", reason=" + Limit(failureReason, 500));

            Task.Run(() => AnalyzeAsync(report, failureReason));
        }

        private static void AnalyzeAsync(SlowResponseAnomalyReport report, string failureReason)
        {
            try
            {
                var messages = new JArray
                {
                    new JObject
                    {
                        ["role"] = "system",
                        ["content"] = "你是千牛AI客服Bot的高级故障诊断工程师。根据一次‘答案已生成但没有确认真实发送’事件和运行日志，定位最可能的技术根因。必须区分：智能提示接口被误当发送成功、错误千牛会话、UIA输入框或发送按钮失败、消息回显缺失、并发会话覆盖、人工介入。只输出JSON：severity、summary、likely_cause、evidence、recommendations。不要编造日志里没有的事实。"
                    },
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = BuildPrompt(report, failureReason)
                    }
                };

                var result = MyOpenAI.CallStructuredChat(messages, 1400, 0.1, 35, CancellationToken.None);
                if (!result.Success || string.IsNullOrWhiteSpace(result.Answer))
                {
                    report.AnalysisStatus = "AI分析失败，已保留规则诊断";
                    report.Summary = "发送失败已确认；诊断AI调用失败：" + Limit(result.Error, 500);
                    report.RawAnalysis = Limit(result.Raw, 4000);
                    SaveReport(report);
                    return;
                }

                report.RawAnalysis = Limit(result.Answer, 8000);
                JObject json;
                try
                {
                    json = JObject.Parse(ExtractJsonObject(result.Answer));
                }
                catch
                {
                    json = null;
                }
                if (json != null)
                {
                    report.Severity = Text(json["severity"], report.Severity, 100);
                    report.Summary = Text(json["summary"], report.Summary, 1200);
                    report.LikelyCause = Text(json["likely_cause"], report.LikelyCause, 2400);
                    report.Evidence = Text(json["evidence"], report.Evidence, 3000);
                    report.Recommendations = Text(json["recommendations"], report.Recommendations, 3000);
                }
                else
                {
                    report.Summary = Limit(result.Answer, 1200);
                }
                report.AnalysisStatus = "AI分析完成";
                SaveReport(report);
                Log.Info("发送失败AI分析完成: reportId=" + report.Id
                    + ", cause=" + Limit(report.LikelyCause, 500));
            }
            catch (Exception ex)
            {
                report.AnalysisStatus = "AI分析任务异常，已保留规则诊断";
                report.Summary = "发送失败异常已记录，但AI分析任务异常：" + Limit(ex.Message, 500);
                SaveReport(report);
                Log.Exception(ex);
            }
        }

        private static string BuildPrompt(SlowResponseAnomalyReport report, string failureReason)
        {
            var sb = new StringBuilder();
            sb.AppendLine("客服：" + report.Seller);
            sb.AppendLine("买家：" + report.Buyer);
            sb.AppendLine("买家问题：" + report.Question);
            sb.AppendLine("待发送答案：" + report.Answer);
            sb.AppendLine("答案来源：" + report.AnswerSource);
            sb.AppendLine("监控失败原因：" + Limit(failureReason, 1800));
            sb.AppendLine("答案就绪到判定未送达：" + report.GenerationSeconds.ToString("0.000") + "秒");
            sb.AppendLine();
            sb.AppendLine("最近运行日志：");
            sb.AppendLine(ReadRecentLogTail(16000));
            return sb.ToString();
        }

        private static string ReadRecentLogTail(int maxChars)
        {
            try
            {
                Log.Flush();
                var file = Log.CurrentFileName;
                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return "[没有可读取的运行日志]";
                var bytes = File.ReadAllBytes(file);
                var encoding = Encoding.GetEncoding(936);
                var text = encoding.GetString(bytes);
                if (text.Length > maxChars) text = text.Substring(text.Length - maxChars);
                return Limit(text, maxChars);
            }
            catch (Exception ex)
            {
                return "[读取日志失败：" + Limit(ex.Message, 300) + "]";
            }
        }

        private static void SaveReport(SlowResponseAnomalyReport report)
        {
            try
            {
                var method = typeof(SlowResponseAnomalyService).GetMethod(
                    "SaveOrUpdate",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (method == null) throw new MissingMethodException("SlowResponseAnomalyService.SaveOrUpdate");
                method.Invoke(null, new object[] { report });
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }

        private static string ExtractJsonObject(string value)
        {
            value = (value ?? string.Empty).Trim();
            var start = value.IndexOf('{');
            var end = value.LastIndexOf('}');
            return start >= 0 && end > start ? value.Substring(start, end - start + 1) : value;
        }

        private static string Text(JToken token, string fallback, int max)
        {
            if (token == null) return fallback ?? string.Empty;
            var value = token.Type == JTokenType.Array
                ? string.Join("\n", token.Values<string>().Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => "- " + x.Trim()))
                : token.ToString();
            value = Limit(value, max);
            return string.IsNullOrWhiteSpace(value) ? (fallback ?? string.Empty) : value;
        }

        private static string Limit(string value, int max)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
