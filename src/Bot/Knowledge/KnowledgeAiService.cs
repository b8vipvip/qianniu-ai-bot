using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Bot.ChromeNs;
using Bot.Options;
using BotLib;
using Newtonsoft.Json.Linq;

namespace Bot.Knowledge
{
    public class KnowledgeImportResult
    {
        public int TextChars { get; set; }
        public int ImageCount { get; set; }
        public int VideoSkipped { get; set; }
        public int AiGenerated { get; set; }
        public int Added { get; set; }
        public int DuplicateSkipped { get; set; }
        public int NewCategoryCount { get; set; }
        public int UnsupportedImageSkipped { get; set; }
        public List<KnowledgeBaseEntry> AddedItems { get; set; }
        public KnowledgeImportResult() { AddedItems = new List<KnowledgeBaseEntry>(); }
    }

    public enum SmartImportCancelSource { None, UserCancel, Timeout, WindowClosed, ReplacedByNewTask }
    public class SmartImportException : Exception
    {
        public SmartImportCancelSource Source { get; private set; }
        public int BatchIndex { get; private set; }
        public SmartImportException(string message, SmartImportCancelSource source, int batchIndex) : base(message) { Source = source; BatchIndex = batchIndex; }
    }
    public class SmartImportProgress
    {
        public int BatchIndex, BatchCount, BatchChars, Added, DuplicateSkipped, TimeoutSeconds; public long ElapsedMs; public string EndpointName;
        public override string ToString() { return string.Format("正在分析第 {0}/{1} 批\n当前批次字符数：{2:N0}\n已导入：{3}条\n已跳过重复：{4}条\n当前耗时：{5:mm\\:ss}\n当前接口：{6}", BatchIndex, BatchCount, BatchChars, Added, DuplicateSkipped, TimeSpan.FromMilliseconds(ElapsedMs), string.IsNullOrWhiteSpace(EndpointName) ? "-" : EndpointName); }
    }

    public class KnowledgeAiService
    {
        private const int TargetMinChars = 3000, TargetMaxChars = 5000, MaxBatchChars = 6000, ImagesPerBatch = 5;
        public bool SupportsDirectVideo { get { return false; } }
        private const string SystemPrompt = "你是电商客服知识库整理助手。只能根据输入资料生成答案，不允许编造价格、库存、发货时间、物流时效或售后承诺。每批最多生成20到40条问答。只输出严格JSON，不要输出解释、Markdown代码围栏或额外说明。输出结构必须是：{\"faqs\":[{\"category\":\"店铺规则\",\"question\":\"问题\",\"answer\":\"答案\",\"keywords\":[\"关键词\"]}]}。";

        public async Task<KnowledgeImportResult> ImportAsync(ClipboardKnowledgeData data, Action<string> progress) { return await ImportAsync(data, BotFeatureStore.GetSmartImportTimeoutSeconds(), CancellationToken.None, () => SmartImportCancelSource.None, progress); }
        public async Task<KnowledgeImportResult> ImportAsync(ClipboardKnowledgeData data, int timeoutSeconds, CancellationToken userToken, Func<SmartImportCancelSource> cancelSource, Action<string> progress)
        {
            timeoutSeconds = ClampTimeout(timeoutSeconds);
            if (data == null || !data.HasAnalyzableContent) throw new Exception("没有检测到可导入的文字、图片或媒体内容。");
            if (AiEndpointStore.GetEnabledEndpoints().Count < 1) throw new Exception("请先在【设置 → API接口】中配置并启用至少一个可用的 AI 接口。");
            var batches = BuildBatches(data); var total = new KnowledgeImportResult { TextChars = (data.Text ?? string.Empty).Length, ImageCount = data.Images.Count, VideoSkipped = data.Videos.Count };
            var existingCats = BotFeatureStore.GetKnowledgeBase().Select(x => x.Category ?? string.Empty).Distinct().ToList(); var importedCats = new HashSet<string>();
            for (var i = 0; i < batches.Count; i++)
            {
                var batch = batches[i]; var bh = Hash(batch.Text ?? string.Empty); var sw = Stopwatch.StartNew();
                if (progress != null) progress(new SmartImportProgress { BatchIndex = i + 1, BatchCount = batches.Count, BatchChars = (batch.Text ?? "").Length, Added = total.Added, DuplicateSkipped = total.DuplicateSkipped, ElapsedMs = 0, TimeoutSeconds = timeoutSeconds }.ToString());
                ParseResult parsed = null; Exception last = null;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(userToken, timeoutCts.Token))
                    {
                        try { parsed = await AnalyzeBatchAsync(batch.Text, batch.Images, timeoutSeconds, linked.Token, p => { if (progress != null) progress(p); }, i + 1, batches.Count, total.Added, total.DuplicateSkipped, sw); break; }
                        catch (OperationCanceledException) { var src = timeoutCts.IsCancellationRequested && !userToken.IsCancellationRequested ? SmartImportCancelSource.Timeout : (cancelSource == null ? SmartImportCancelSource.UserCancel : cancelSource()); throw BuildCancelException(src, i + 1, timeoutSeconds, batch.EndpointName); }
                        catch (Exception ex) { last = ex; if (attempt == 0 && IsRetryable(ex.Message)) continue; throw; }
                    }
                }
                if (parsed == null && last != null) throw last;
                total.UnsupportedImageSkipped += parsed.UnsupportedImages; total.AiGenerated += parsed.Items.Count;
                var save = SaveDeduped(parsed.Items); total.Added += save.Added; total.DuplicateSkipped += save.DuplicateSkipped; total.AddedItems.AddRange(save.AddedItems);
                foreach (var c in save.AddedItems.Select(x => x.Category ?? string.Empty).Where(x => x.Length > 0 && !existingCats.Contains(x))) importedCats.Add(c);
                total.NewCategoryCount = importedCats.Count;
                Log.Info(string.Format("SmartImport batch ok endpoint={0} model={1} batch={2}/{3} input_chars={4} batch_hash={5} elapsed_ms={6} timeout_seconds={7} parsed_items={8} added={9} dup={10}", batch.EndpointName, batch.Model, i + 1, batches.Count, (batch.Text ?? "").Length, bh, sw.ElapsedMilliseconds, timeoutSeconds, parsed.Items.Count, save.Added, save.DuplicateSkipped));
                if (progress != null) progress(new SmartImportProgress { BatchIndex = i + 1, BatchCount = batches.Count, BatchChars = (batch.Text ?? "").Length, Added = total.Added, DuplicateSkipped = total.DuplicateSkipped, ElapsedMs = sw.ElapsedMilliseconds, EndpointName = batch.EndpointName, TimeoutSeconds = timeoutSeconds }.ToString());
            }
            return total;
        }
        public static int ClampTimeout(int seconds) { return Math.Max(120, Math.Min(1800, seconds <= 0 ? 600 : seconds)); }
        private SmartImportException BuildCancelException(SmartImportCancelSource src, int batch, int timeout, string endpoint) { if (src == SmartImportCancelSource.Timeout) return new SmartImportException(string.Format("智能导入超时：接口“{0}”等待超过{1}秒，未收到完整响应。本批内容尚未导入，可以重试本批。", string.IsNullOrWhiteSpace(endpoint) ? "当前接口" : endpoint, timeout), src, batch); if (src == SmartImportCancelSource.WindowClosed) return new SmartImportException("窗口已关闭，智能导入任务已停止。已完成批次会保留。", src, batch); if (src == SmartImportCancelSource.ReplacedByNewTask) return new SmartImportException("新的智能导入任务已开始，旧任务已停止。已完成批次会保留。", src, batch); return new SmartImportException("用户已取消智能导入。已完成批次会保留。", SmartImportCancelSource.UserCancel, batch); }
        private class Batch { public string Text; public List<KnowledgeMediaItem> Images; public string EndpointName; public string Model; }
        private class ParseResult { public List<KnowledgeBaseEntry> Items = new List<KnowledgeBaseEntry>(); public int UnsupportedImages; }
        public static List<string> SplitTextBatches(string text)
        {
            text = text ?? string.Empty; var paras = Regex.Split(text.Replace("\r\n", "\n"), "\n{2,}").Where(x => x.Trim().Length > 0).ToList(); if (paras.Count == 0) return new List<string> { string.Empty };
            var parts = new List<string>(); var sb = new StringBuilder(); Action flush = () => { if (sb.Length > 0) { parts.Add(sb.ToString().Trim()); sb.Length = 0; } };
            foreach (var p0 in paras) foreach (var p in SplitLongParagraph(p0)) { if (sb.Length > 0 && sb.Length + p.Length + 2 > TargetMaxChars) flush(); if (p.Length > MaxBatchChars) { flush(); parts.Add(p.Trim()); } else { if (sb.Length > 0) sb.Append("\n\n"); sb.Append(p.Trim()); if (sb.Length >= TargetMinChars) flush(); } }
            flush(); return parts;
        }
        private static IEnumerable<string> SplitLongParagraph(string p)
        { if ((p ?? "").Length <= MaxBatchChars) { yield return p; yield break; } var segs = Regex.Split(p, "(?<=[。！？?？!；;])"); var sb = new StringBuilder(); foreach (var s in segs.Where(x => x.Length > 0)) { if (sb.Length > 0 && sb.Length + s.Length > MaxBatchChars) { yield return sb.ToString(); sb.Length = 0; } sb.Append(s); } if (sb.Length > 0) yield return sb.ToString(); }
        private List<Batch> BuildBatches(ClipboardKnowledgeData data) { var chunks = SplitTextBatches(data.Text); var images = data.Images.Where(x => x != null && !string.IsNullOrWhiteSpace(x.AiUrl)).ToList(); var count = Math.Max(chunks.Count, (int)Math.Ceiling(images.Count / (double)ImagesPerBatch)); return Enumerable.Range(0, Math.Max(1, count)).Select(i => new Batch { Text = i < chunks.Count ? chunks[i] : string.Empty, Images = images.Skip(i * ImagesPerBatch).Take(ImagesPerBatch).ToList() }).ToList(); }
        private async Task<ParseResult> AnalyzeBatchAsync(string text, List<KnowledgeMediaItem> images, int timeout, CancellationToken token, Action<string> progress, int bi, int bc, int added, int dup, Stopwatch sw)
        { try { return await AnalyzeBatchCoreAsync(text, images, timeout, token, progress, bi, bc, added, dup, sw); } catch (Exception ex) { if (images != null && images.Count > 0 && IsVisionUnsupported(ex.Message)) { if (progress != null) progress("当前AI接口不支持图片理解，正在改用纯文本分析..."); var f = await AnalyzeBatchCoreAsync(text, new List<KnowledgeMediaItem>(), timeout, token, progress, bi, bc, added, dup, sw); f.UnsupportedImages = images.Count; return f; } throw; } }
        private async Task<ParseResult> AnalyzeBatchCoreAsync(string text, List<KnowledgeMediaItem> images, int timeout, CancellationToken token, Action<string> progress, int bi, int bc, int added, int dup, Stopwatch sw)
        {
            var userText = string.Format("请整理以下资料为客服问答知识库。每批最多20到40条问答，max_tokens受限时优先保证JSON完整。资料文本：\n{0}", text ?? string.Empty); JToken content = userText;
            if (images != null && images.Count > 0) { var arr = new JArray { new JObject { ["type"] = "text", ["text"] = userText } }; foreach (var img in images) arr.Add(new JObject { ["type"] = "image_url", ["image_url"] = new JObject { ["url"] = img.AiUrl } }); content = arr; }
            var messages = new JArray { Msg("system", SystemPrompt), new JObject { ["role"] = "user", ["content"] = content } };
            var raw = await Task.Run(() => MyOpenAI.CallStructuredChat(messages, 4000, 0.1, timeout, token));
            if (!raw.Success) throw new Exception(raw.Error); var json = raw.Answer;
            try { return new ParseResult { Items = ParseAiKnowledgeResult(json) }; } catch (Exception ex) { throw new Exception("JSON解析失败：AI返回的数据格式异常，本批没有写入知识库。" + ex.Message); }
        }
        private JObject Msg(string role, string text) { return new JObject { ["role"] = role, ["content"] = text ?? string.Empty }; }
        public static List<KnowledgeBaseEntry> ParseAiKnowledgeResult(string text)
        { text = StripFence(text); var s = text.IndexOf('{'); var e = text.LastIndexOf('}'); if (s < 0 || e <= s) throw new Exception("未找到JSON对象，可能是响应被截断"); var obj = JObject.Parse(text.Substring(s, e - s + 1)); var faqs = obj["faqs"] as JArray; if (faqs == null) throw new Exception("缺少faqs数组"); var list = new List<KnowledgeBaseEntry>(); foreach (var f in faqs.OfType<JObject>().Take(40)) { var q = (f["question"] ?? f["title"] ?? string.Empty).ToString().Trim(); var a = (f["answer"] ?? string.Empty).ToString().Trim(); if (string.IsNullOrWhiteSpace(q) || string.IsNullOrWhiteSpace(a)) continue; var kws = f["keywords"] is JArray ? string.Join(",", ((JArray)f["keywords"]).Select(x => x.ToString().Trim()).Where(x => x.Length > 0).Distinct()) : (f["keywords"] ?? string.Empty).ToString(); list.Add(new KnowledgeBaseEntry { Enabled = true, Category = string.IsNullOrWhiteSpace((f["category"] ?? string.Empty).ToString()) ? "通用" : f["category"].ToString().Trim(), Title = NormalizeDisplay(q), Answer = NormalizeDisplay(a), Keywords = NormalizeDisplay(kws), UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Id = Guid.NewGuid().ToString("N"), CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), AiGenerated = true, SourceType = "智能导入" }); } if (list.Count < 1) throw new Exception("没有有效问答"); return list; }
        public static string StripFence(string text) { text = (text ?? string.Empty).Trim(); if (text.StartsWith("```")) { text = Regex.Replace(text, "^```[a-zA-Z]*", "").Trim(); if (text.EndsWith("```")) text = text.Substring(0, text.Length - 3).Trim(); } return text; }
        private KnowledgeImportResult SaveDeduped(List<KnowledgeBaseEntry> generated) { var existing = BotFeatureStore.GetKnowledgeBase(); var oldCats = existing.Select(x => x.Category ?? string.Empty).Where(x => x.Length > 0).Distinct().ToList(); var seen = new HashSet<string>(existing.Select(x => ContentHash(x.Title, x.Answer))); var add = new List<KnowledgeBaseEntry>(); var dup = 0; foreach (var item in generated) { var key = ContentHash(item.Title, item.Answer); if (string.IsNullOrWhiteSpace(NormalizeQuestion(item.Title)) || seen.Contains(key)) { dup++; continue; } seen.Add(key); add.Add(item); } existing.AddRange(add); BotFeatureStore.SaveKnowledgeBase(existing); return new KnowledgeImportResult { AiGenerated = generated.Count, Added = add.Count, DuplicateSkipped = dup, NewCategoryCount = add.Select(x => x.Category ?? string.Empty).Where(x => x.Length > 0 && !oldCats.Contains(x)).Distinct().Count(), AddedItems = add }; }
        public static string NormalizeQuestion(string question) { var chars = (question ?? string.Empty).Trim().ToLowerInvariant().Where(c => !char.IsWhiteSpace(c) && ",，.。?？!！;；:：、'\"“”‘’()（）[]【】{}《》<>-—_~`".IndexOf(c) < 0); return new string(chars.ToArray()); }
        private static string NormalizeDisplay(string s) { return Regex.Replace((s ?? string.Empty).Trim(), "\\s+", " "); }
        public static string ContentHash(string q, string a) { return Hash(NormalizeQuestion(q) + "\n" + NormalizeDisplay(a).ToLowerInvariant()); }
        private static string Hash(string s) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""))).Replace("-", "").ToLowerInvariant(); }
        private bool IsVisionUnsupported(string error) { error = (error ?? string.Empty).ToLowerInvariant(); return error.Contains("unsupported image") || error.Contains("vision not supported") || error.Contains("invalid content type") || error.Contains("image_url") || error.Contains("multimodal") || error.Contains("http 400"); }
        private bool IsRetryable(string error) { error = (error ?? string.Empty).ToLowerInvariant(); return error.Contains("超时") || error.Contains("timeout") || error.Contains("http 429") || error.Contains("http 500") || error.Contains("http 501") || error.Contains("http 502") || error.Contains("http 503") || error.Contains("http 504") || error.Contains("network") || error.Contains("连接") || error.Contains("断开"); }
    }
}
