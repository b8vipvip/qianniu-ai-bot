using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Bot.ChromeNs;
using Bot.Options;
using BotLib;
using Newtonsoft.Json;
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

    public class KnowledgeAiService
    {
        private const int TextBatchChars = 12000;
        private const int ImagesPerBatch = 5;
        public bool SupportsDirectVideo { get { return false; } }

        private const string SystemPrompt = "你是电商客服知识库整理助手。你的任务是根据用户提供的店铺资料、商品资料、售后规则、产品介绍、聊天记录和图片内容，整理成可以供客服机器人直接使用的问答知识库。严格要求：1.只能根据输入资料生成答案；2.不允许编造资料中没有的信息；3.不知道的信息不要猜测；4.不要编造价格；5.不要编造库存；6.不要编造发货时间；7.不要编造物流时效；8.不要编造售后承诺；9.自动判断合理分类；10.自动生成客户真正可能询问的问题；11.一个知识点可以生成多个不同问法，但避免重复；12.答案要准确、清晰、适合客服直接使用；13.自动提取适合搜索匹配的关键词；14.只输出JSON，不要输出Markdown解释。输出结构必须是：{\"faqs\":[{\"category\":\"店铺规则\",\"question\":\"问题\",\"answer\":\"答案\",\"keywords\":[\"关键词\"]}]}。";

        public async Task<KnowledgeImportResult> ImportAsync(ClipboardKnowledgeData data, Action<string> progress)
        {
            if (data == null || !data.HasAnalyzableContent) throw new Exception("没有检测到可导入的文字、图片或媒体内容。");
            if (AiEndpointStore.GetEnabledEndpoints().Count < 1) throw new Exception("请先在【设置 → API接口】中配置并启用至少一个可用的 AI 接口。");
            if (progress != null) progress("正在整理资料批次...");
            var batches = BuildBatches(data);
            var all = new List<KnowledgeBaseEntry>();
            var unsupportedImages = 0;
            for (var i = 0; i < batches.Count; i++)
            {
                if (progress != null) progress("正在分析第 " + (i + 1) + "/" + batches.Count + " 批资料...");
                var batch = batches[i];
                var parsed = await AnalyzeBatchAsync(batch.Text, batch.Images, progress);
                unsupportedImages += parsed.UnsupportedImages;
                all.AddRange(parsed.Items);
            }
            if (progress != null) progress("正在去除重复知识...");
            var result = SaveDeduped(all, data);
            result.UnsupportedImageSkipped = unsupportedImages;
            return result;
        }

        private class Batch { public string Text; public List<KnowledgeMediaItem> Images; }
        private class ParseResult { public List<KnowledgeBaseEntry> Items = new List<KnowledgeBaseEntry>(); public int UnsupportedImages; }

        private List<Batch> BuildBatches(ClipboardKnowledgeData data)
        {
            var text = data.Text ?? string.Empty;
            var chunks = new List<string>();
            if (text.Length == 0) chunks.Add(string.Empty);
            for (var i = 0; i < text.Length; i += TextBatchChars) chunks.Add(text.Substring(i, Math.Min(TextBatchChars, text.Length - i)));
            var images = data.Images.Where(x => x != null && !string.IsNullOrWhiteSpace(x.AiUrl)).ToList();
            var count = Math.Max(chunks.Count, (int)Math.Ceiling(images.Count / (double)ImagesPerBatch));
            if (count < 1) count = 1;
            var batches = new List<Batch>();
            for (var i = 0; i < count; i++)
            {
                batches.Add(new Batch { Text = i < chunks.Count ? chunks[i] : string.Empty, Images = images.Skip(i * ImagesPerBatch).Take(ImagesPerBatch).ToList() });
            }
            return batches;
        }

        private async Task<ParseResult> AnalyzeBatchAsync(string text, List<KnowledgeMediaItem> images, Action<string> progress)
        {
            var withImages = images != null && images.Count > 0;
            try { return await AnalyzeBatchCoreAsync(text, images); }
            catch (Exception ex)
            {
                if (withImages && IsVisionUnsupported(ex.Message))
                {
                    if (progress != null) progress("当前AI接口不支持图片理解，正在改用纯文本分析...");
                    var fallback = await AnalyzeBatchCoreAsync(text, new List<KnowledgeMediaItem>());
                    fallback.UnsupportedImages = images.Count;
                    return fallback;
                }
                throw;
            }
        }

        private async Task<ParseResult> AnalyzeBatchCoreAsync(string text, List<KnowledgeMediaItem> images)
        {
            var userText = "请整理以下资料为客服问答知识库。资料文本：\n" + (text ?? string.Empty);
            JToken content;
            if (images != null && images.Count > 0)
            {
                var arr = new JArray { new JObject { ["type"] = "text", ["text"] = userText } };
                foreach (var img in images) arr.Add(new JObject { ["type"] = "image_url", ["image_url"] = new JObject { ["url"] = img.AiUrl } });
                content = arr;
            }
            else content = userText;
            var messages = new JArray { Msg("system", SystemPrompt), new JObject { ["role"] = "user", ["content"] = content } };
            var raw = await Task.Run(() => MyOpenAI.CallStructuredChat(messages, 2400, 0.1));
            if (!raw.Success) throw new Exception(raw.Error);
            var json = raw.Answer;
            try { return new ParseResult { Items = ParseAiKnowledgeResult(json) }; }
            catch
            {
                var fixMessages = new JArray { Msg("system", "请把用户提供的内容修复成规定JSON格式，只输出JSON。格式：{\"faqs\":[{\"category\":\"\",\"question\":\"\",\"answer\":\"\",\"keywords\":[\"\"]}]}"), Msg("user", json) };
                var fixedRaw = await Task.Run(() => MyOpenAI.CallStructuredChat(fixMessages, 2400, 0));
                if (!fixedRaw.Success) throw new Exception(fixedRaw.Error);
                try { return new ParseResult { Items = ParseAiKnowledgeResult(fixedRaw.Answer) }; }
                catch { throw new Exception("AI返回的数据格式异常，本次没有写入知识库，请重试。"); }
            }
        }

        private JObject Msg(string role, string text) { return new JObject { ["role"] = role, ["content"] = text ?? string.Empty }; }

        public static List<KnowledgeBaseEntry> ParseAiKnowledgeResult(string text)
        {
            text = (text ?? string.Empty).Trim();
            if (text.StartsWith("```"))
            {
                text = text.Trim('`').Trim();
                if (text.StartsWith("json", StringComparison.OrdinalIgnoreCase)) text = text.Substring(4).Trim();
            }
            var s = text.IndexOf('{'); var e = text.LastIndexOf('}');
            if (s < 0 || e <= s) throw new Exception("未找到JSON对象");
            var obj = JObject.Parse(text.Substring(s, e - s + 1));
            var faqs = obj["faqs"] as JArray;
            if (faqs == null) throw new Exception("缺少faqs数组");
            var list = new List<KnowledgeBaseEntry>();
            foreach (var f in faqs.OfType<JObject>())
            {
                var q = (f["question"] ?? f["title"] ?? string.Empty).ToString().Trim();
                var a = (f["answer"] ?? string.Empty).ToString().Trim();
                if (string.IsNullOrWhiteSpace(q) || string.IsNullOrWhiteSpace(a)) continue;
                var kws = f["keywords"] is JArray ? string.Join(",", ((JArray)f["keywords"]).Select(x => x.ToString().Trim()).Where(x => x.Length > 0).Distinct()) : (f["keywords"] ?? string.Empty).ToString();
                list.Add(new KnowledgeBaseEntry { Enabled = true, Category = string.IsNullOrWhiteSpace((f["category"] ?? string.Empty).ToString()) ? "通用" : f["category"].ToString().Trim(), Title = q, Answer = a, Keywords = kws, UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Id = Guid.NewGuid().ToString("N"), CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), AiGenerated = true, SourceType = "智能导入" });
            }
            if (list.Count < 1) throw new Exception("没有有效问答");
            return list;
        }

        private KnowledgeImportResult SaveDeduped(List<KnowledgeBaseEntry> generated, ClipboardKnowledgeData data)
        {
            var existing = BotFeatureStore.GetKnowledgeBase();
            var oldCats = existing.Select(x => x.Category ?? string.Empty).Where(x => x.Length > 0).Distinct().ToList();
            var seen = new HashSet<string>(existing.Select(x => NormalizeQuestion(x.Title)));
            var inner = new HashSet<string>();
            var add = new List<KnowledgeBaseEntry>();
            var dup = 0;
            foreach (var item in generated)
            {
                var key = NormalizeQuestion(item.Title);
                if (string.IsNullOrWhiteSpace(key) || seen.Contains(key) || inner.Contains(key)) { dup++; continue; }
                inner.Add(key); add.Add(item);
            }
            existing.AddRange(add);
            BotFeatureStore.SaveKnowledgeBase(existing);
            return new KnowledgeImportResult { TextChars = (data.Text ?? string.Empty).Length, ImageCount = data.Images.Count, VideoSkipped = data.Videos.Count, AiGenerated = generated.Count, Added = add.Count, DuplicateSkipped = dup, NewCategoryCount = add.Select(x => x.Category ?? string.Empty).Where(x => x.Length > 0 && !oldCats.Contains(x)).Distinct().Count(), AddedItems = add };
        }

        public static string NormalizeQuestion(string question)
        {
            var chars = (question ?? string.Empty).Trim().ToLowerInvariant().Where(c => !char.IsWhiteSpace(c) && ",，.。?？!！;；:：、'\"“”‘’()（）[]【】{}《》<>-—_~`".IndexOf(c) < 0);
            return new string(chars.ToArray());
        }

        private bool IsVisionUnsupported(string error)
        {
            error = (error ?? string.Empty).ToLowerInvariant();
            return error.Contains("unsupported image") || error.Contains("vision not supported") || error.Contains("invalid content type") || error.Contains("image_url") || error.Contains("multimodal") || error.Contains("http 400");
        }
    }
}
