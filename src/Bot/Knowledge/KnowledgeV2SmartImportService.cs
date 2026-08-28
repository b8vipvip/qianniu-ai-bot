using Bot.ChromeNs;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Knowledge
{
    internal sealed class KnowledgeV2SmartImportResult
    {
        public int TextChars { get; set; }
        public int ImageCount { get; set; }
        public int VideoSkipped { get; set; }
        public int AiGenerated { get; set; }
        public int Added { get; set; }
        public int DuplicateSkipped { get; set; }
        public int UnsupportedImageSkipped { get; set; }
        public string ImportId { get; set; }
        public List<KnowledgeV2Record> AddedItems { get; private set; }

        public KnowledgeV2SmartImportResult()
        {
            AddedItems = new List<KnowledgeV2Record>();
            ImportId = string.Empty;
        }
    }

    internal sealed class KnowledgeV2SmartImportService
    {
        private const int ImagesPerBatch = 5;
        private const string SystemPrompt =
            "你是电商客服知识库整理助手。只能根据输入资料生成答案，不允许编造价格、库存、发货时间、物流时效或售后承诺。" +
            "每批最多生成20到40条问答。只输出严格JSON，不要输出解释、Markdown代码围栏或额外说明。" +
            "输出结构必须是：{\"faqs\":[{\"category\":\"店铺规则\",\"question\":\"问题\",\"answer\":\"答案\",\"keywords\":[\"关键词\"]}]}。";

        private sealed class Batch
        {
            public string Text;
            public List<KnowledgeMediaItem> Images;
            public string EndpointName;
        }

        private sealed class AnalysisResult
        {
            public List<KnowledgeBaseEntry> Items = new List<KnowledgeBaseEntry>();
            public int UnsupportedImages;
        }

        public bool SupportsDirectVideo
        {
            get { return false; }
        }

        public async Task<KnowledgeV2SmartImportResult> ImportAsync(
            string seller,
            ClipboardKnowledgeData data,
            int timeoutSeconds,
            CancellationToken userToken,
            Func<SmartImportCancelSource> cancelSource,
            Action<string> progress)
        {
            seller = (seller ?? string.Empty).Trim();
            timeoutSeconds = KnowledgeAiService.ClampTimeout(timeoutSeconds);
            if (seller.Length == 0)
                throw new InvalidOperationException("无法识别当前店铺客服账号，不能写入新版知识库。");
            if (data == null || !data.HasAnalyzableContent)
                throw new InvalidOperationException("没有检测到可导入的文字、图片或媒体内容。");

            var endpoints = AiEndpointStore.GetEnabledEndpoints();
            if (endpoints.Count < 1)
                throw new InvalidOperationException("请先在【设置 → API接口】中配置并启用至少一个可用的 AI 接口。");

            var primary = endpoints.FirstOrDefault();
            var batches = BuildBatches(data,
                primary == null ? string.Empty : primary.Name);
            var importId = "v2-ai-import-" + DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var result = new KnowledgeV2SmartImportResult
            {
                ImportId = importId,
                TextChars = (data.Text ?? string.Empty).Length,
                ImageCount = data.Images == null ? 0 : data.Images.Count,
                VideoSkipped = data.Videos == null ? 0 : data.Videos.Count
            };

            var existing = KnowledgeEngineV2Repository.LoadAll(seller);
            var seen = new HashSet<string>(
                existing.Where(x => x != null)
                    .Select(x => KnowledgeAiService.ContentHash(x.Title, x.Answer)),
                StringComparer.Ordinal);

            try
            {
                for (var i = 0; i < batches.Count; i++)
                {
                    var batch = batches[i];
                    var stopwatch = Stopwatch.StartNew();
                    ReportProgress(progress, batch, i + 1, batches.Count, result, stopwatch.ElapsedMilliseconds);
                    AnalysisResult analyzed = null;
                    Exception lastError = null;

                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(userToken, timeoutCts.Token))
                        {
                            try
                            {
                                analyzed = await AnalyzeBatchAsync(batch.Text, batch.Images, timeoutSeconds, linked.Token);
                                break;
                            }
                            catch (OperationCanceledException)
                            {
                                var source = timeoutCts.IsCancellationRequested && !userToken.IsCancellationRequested
                                    ? SmartImportCancelSource.Timeout
                                    : (cancelSource == null ? SmartImportCancelSource.UserCancel : cancelSource());
                                var cancelError = BuildCancelException(source, i + 1, timeoutSeconds, batch.EndpointName);
                                if (source == SmartImportCancelSource.Timeout && attempt == 0)
                                {
                                    lastError = cancelError;
                                    if (progress != null) progress("第 " + (i + 1) + " 批请求超时，正在自动重试一次...");
                                    continue;
                                }
                                throw cancelError;
                            }
                            catch (Exception ex)
                            {
                                lastError = ex;
                                if (attempt == 0 && IsRetryable(ex.Message))
                                {
                                    if (progress != null) progress("第 " + (i + 1) + " 批遇到临时错误，正在自动重试一次...");
                                    continue;
                                }
                                throw;
                            }
                        }
                    }

                    if (analyzed == null)
                    {
                        if (lastError != null) throw lastError;
                        throw new InvalidOperationException("智能导入未返回有效结果，本批没有写入新版知识库。");
                    }

                    result.UnsupportedImageSkipped += analyzed.UnsupportedImages;
                    result.AiGenerated += analyzed.Items.Count;
                    foreach (var item in analyzed.Items)
                    {
                        if (item == null || string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.Answer))
                            continue;
                        var contentHash = KnowledgeAiService.ContentHash(item.Title, item.Answer);
                        if (!seen.Add(contentHash))
                        {
                            result.DuplicateSkipped++;
                            continue;
                        }

                        var record = KnowledgeEngineV2Semantics.FromLegacy(item, null);
                        if (record == null)
                        {
                            result.DuplicateSkipped++;
                            continue;
                        }
                        record.SourceType = "ai_smart_import";
                        record.SourceId = importId;
                        record.Status = "active";
                        record.Enabled = true;
                        KnowledgeEngineV2Repository.Save(seller, record);
                        result.AddedItems.Add(record);
                        result.Added++;
                    }

                    Log.Info(string.Format(
                        "KnowledgeV2 SmartImport batch ok seller={0} endpoint={1} batch={2}/{3} input_chars={4} elapsed_ms={5} generated={6} added_total={7} dup_total={8}",
                        seller, batch.EndpointName, i + 1, batches.Count, (batch.Text ?? string.Empty).Length,
                        stopwatch.ElapsedMilliseconds, analyzed.Items.Count, result.Added, result.DuplicateSkipped));
                    ReportProgress(progress, batch, i + 1, batches.Count, result, stopwatch.ElapsedMilliseconds);
                }

                AppendAudit(seller, result, "success", "AI智能导入完成");
                return result;
            }
            catch (Exception ex)
            {
                AppendAudit(seller, result, "failed", "AI智能导入中止：" + Safe(ex.Message, 500));
                throw;
            }
        }

        private static List<Batch> BuildBatches(ClipboardKnowledgeData data, string endpointName)
        {
            var chunks = KnowledgeAiService.SplitTextBatches(data == null ? string.Empty : data.Text);
            var images = data == null || data.Images == null
                ? new List<KnowledgeMediaItem>()
                : data.Images.Where(x => x != null && !string.IsNullOrWhiteSpace(x.AiUrl)).ToList();
            var count = Math.Max(chunks.Count, (int)Math.Ceiling(images.Count / (double)ImagesPerBatch));
            count = Math.Max(1, count);
            return Enumerable.Range(0, count).Select(i => new Batch
            {
                Text = i < chunks.Count ? chunks[i] : string.Empty,
                Images = images.Skip(i * ImagesPerBatch).Take(ImagesPerBatch).ToList(),
                EndpointName = endpointName ?? string.Empty
            }).ToList();
        }

        private static async Task<AnalysisResult> AnalyzeBatchAsync(
            string text, List<KnowledgeMediaItem> images, int timeoutSeconds, CancellationToken token)
        {
            try
            {
                return await AnalyzeBatchCoreAsync(text, images, timeoutSeconds, token);
            }
            catch (Exception ex)
            {
                if (images != null && images.Count > 0 && IsVisionUnsupported(ex.Message))
                {
                    if (string.IsNullOrWhiteSpace(text))
                        return new AnalysisResult { UnsupportedImages = images.Count };
                    var fallback = await AnalyzeBatchCoreAsync(text, new List<KnowledgeMediaItem>(), timeoutSeconds, token);
                    fallback.UnsupportedImages = images.Count;
                    return fallback;
                }
                throw;
            }
        }

        private static async Task<AnalysisResult> AnalyzeBatchCoreAsync(
            string text, List<KnowledgeMediaItem> images, int timeoutSeconds, CancellationToken token)
        {
            var userText = string.Format(
                "请整理以下资料为客服问答知识库。每批最多20到40条问答，max_tokens受限时优先保证JSON完整。资料文本：\n{0}",
                text ?? string.Empty);
            JToken content = userText;
            if (images != null && images.Count > 0)
            {
                var array = new JArray { new JObject { ["type"] = "text", ["text"] = userText } };
                foreach (var image in images)
                {
                    array.Add(new JObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JObject { ["url"] = image.AiUrl }
                    });
                }
                content = array;
            }

            var messages = new JArray
            {
                new JObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JObject { ["role"] = "user", ["content"] = content }
            };
            var raw = await Task.Run(
                () => MyOpenAI.CallStructuredChat(messages, 4000, 0.1, timeoutSeconds, token), token);
            if (!raw.Success) throw new InvalidOperationException(raw.Error);

            try
            {
                return new AnalysisResult { Items = KnowledgeAiService.ParseAiKnowledgeResult(raw.Answer) };
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("JSON解析失败：AI返回的数据格式异常，本批没有写入新版知识库。" + ex.Message);
            }
        }

        private static void ReportProgress(Action<string> progress, Batch batch, int batchIndex, int batchCount,
            KnowledgeV2SmartImportResult result, long elapsedMs)
        {
            if (progress == null) return;
            progress(string.Format(
                "正在分析第 {0}/{1} 批\n当前批次字符数：{2:N0}\n已写入新版知识库：{3} 条\n已跳过重复：{4} 条\n当前耗时：{5:mm\\:ss}\n当前接口：{6}",
                batchIndex, batchCount, (batch.Text ?? string.Empty).Length, result.Added,
                result.DuplicateSkipped, TimeSpan.FromMilliseconds(elapsedMs),
                string.IsNullOrWhiteSpace(batch.EndpointName) ? "-" : batch.EndpointName));
        }

        private static SmartImportException BuildCancelException(
            SmartImportCancelSource source, int batchIndex, int timeoutSeconds, string endpointName)
        {
            if (source == SmartImportCancelSource.Timeout)
                return new SmartImportException(string.Format(
                    "智能导入超时：接口“{0}”等待超过{1}秒，未收到完整响应。本批内容尚未导入，可以重试。",
                    string.IsNullOrWhiteSpace(endpointName) ? "当前接口" : endpointName, timeoutSeconds), source, batchIndex);
            if (source == SmartImportCancelSource.WindowClosed)
                return new SmartImportException("窗口已关闭，智能导入任务已停止。已完成批次会保留在新版知识库。", source, batchIndex);
            if (source == SmartImportCancelSource.ReplacedByNewTask)
                return new SmartImportException("新的智能导入任务已开始，旧任务已停止。已完成批次会保留在新版知识库。", source, batchIndex);
            return new SmartImportException("用户已取消智能导入。已完成批次会保留在新版知识库。",
                SmartImportCancelSource.UserCancel, batchIndex);
        }

        private static void AppendAudit(string seller, KnowledgeV2SmartImportResult result, string auditResult, string summary)
        {
            if (result == null) return;
            try
            {
                string ignored;
                KnowledgeEngineV2GovernanceAuditService.TryAppendAction(
                    seller,
                    "ai_smart_import",
                    "knowledge_import",
                    result.ImportId,
                    string.Empty,
                    "AI智能导入",
                    string.Empty,
                    string.Format("generated={0};added={1};duplicate_skipped={2};images={3};unsupported_images={4};video_skipped={5}",
                        result.AiGenerated, result.Added, result.DuplicateSkipped, result.ImageCount,
                        result.UnsupportedImageSkipped, result.VideoSkipped),
                    summary,
                    auditResult,
                    out ignored);
            }
            catch { }
        }

        private static bool IsVisionUnsupported(string error)
        {
            error = (error ?? string.Empty).ToLowerInvariant();
            return error.Contains("unsupported image") || error.Contains("vision not supported")
                || error.Contains("invalid content type") || error.Contains("image_url")
                || error.Contains("multimodal") || error.Contains("http 400");
        }

        private static bool IsRetryable(string error)
        {
            error = (error ?? string.Empty).ToLowerInvariant();
            return error.Contains("超时") || error.Contains("timeout") || error.Contains("temporarily")
                || error.Contains("connection") || error.Contains("http 429") || error.Contains("http 500")
                || error.Contains("http 502") || error.Contains("http 503") || error.Contains("http 504");
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}
