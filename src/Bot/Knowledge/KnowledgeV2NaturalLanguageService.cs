using Bot.ChromeNs;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Knowledge
{
    internal static class KnowledgeV2NaturalLanguageService
    {
        private const string SystemPrompt =
            "你是电商客服 Knowledge Center V2 的结构化知识录入助手。" +
            "用户会用一句自然语言描述一条要新增的客服知识。你必须只依据这句话整理，不得补造价格、库存、时效、售后承诺、账号、密码或商品属性。" +
            "只输出一个严格 JSON 对象，不要 Markdown，不要解释。" +
            "字段固定为：title,type,intent,subject,predicate,entities,aliases,answer,short_answer,conditions,exclusions,required_context,product_ids,risk_level,confidence,authority,status。" +
            "type 只能是 business_fact/procedure/presale/order_rule/after_sale/safety_rule/fixed_reply/product_knowledge/learning_candidate/temporary；" +
            "risk_level 只能 normal/high；status 只能 active/candidate。" +
            "entities/aliases/conditions/exclusions/required_context/product_ids 必须是 JSON 字符串数组。" +
            "title 是便于管理的短标题；answer 是可直接给买家的标准答案；aliases 生成常见同义问法；" +
            "无法从用户原句确定的事实不要猜测，对可选字段使用空字符串或空数组。confidence/authority 范围 0~1。";

        public static async Task<KnowledgeV2Record> GenerateAsync(string naturalLanguage, KnowledgeV2RecordsPageMode mode, CancellationToken token)
        {
            naturalLanguage = (naturalLanguage ?? string.Empty).Trim();
            if (naturalLanguage.Length == 0) throw new ArgumentException("请输入一句要新增的知识。", "naturalLanguage");
            var modeHint = mode == KnowledgeV2RecordsPageMode.Product ? "当前页面=商品知识，优先使用 product_knowledge。"
                : mode == KnowledgeV2RecordsPageMode.Process ? "当前页面=流程，优先使用 procedure/order_rule。"
                : mode == KnowledgeV2RecordsPageMode.Learning ? "当前页面=学习候选，type 使用 learning_candidate，status 使用 candidate。"
                : "当前页面=全部知识，请按语义选择最合适的 type。";
            var messages = new JArray
            {
                new JObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JObject { ["role"] = "user", ["content"] = modeHint + "\n人工一句话：" + naturalLanguage }
            };
            var raw = await Task.Run(() => MyOpenAI.CallStructuredChat(messages, 2600, 0.1, 180, token), token).ConfigureAwait(false);
            if (!raw.Success) throw new InvalidOperationException(raw.Error);
            JObject obj;
            try { obj = ParseObject(raw.Answer); }
            catch (Exception ex) { throw new InvalidOperationException("AI 返回的知识字段格式无效：" + ex.Message); }
            var now = DateTime.Now;
            var record = new KnowledgeV2Record
            {
                Id = Guid.NewGuid().ToString("N"), Title = Text(obj, "title"), Type = Text(obj, "type"), Intent = Text(obj, "intent"),
                Subject = Text(obj, "subject"), Predicate = Text(obj, "predicate"), Entities = List(obj, "entities"), Aliases = List(obj, "aliases"),
                Answer = Text(obj, "answer"), ShortAnswer = Text(obj, "short_answer"), Conditions = List(obj, "conditions"), Exclusions = List(obj, "exclusions"),
                RequiredContext = List(obj, "required_context"), ProductIds = List(obj, "product_ids"), RiskLevel = Text(obj, "risk_level"),
                Confidence = Number(obj, "confidence", 0.86), Authority = Number(obj, "authority", 0.90), Enabled = true, Status = Text(obj, "status"),
                SourceType = "manual_ai_generated", SourceId = "manual:" + now.ToString("yyyyMMddHHmmssfff"), CreatedAt = now, UpdatedAt = now, LastVerifiedAt = now
            };
            ApplySafeDefaults(record, mode, naturalLanguage);
            return record;
        }

        private static void ApplySafeDefaults(KnowledgeV2Record record, KnowledgeV2RecordsPageMode mode, string original)
        {
            if (string.IsNullOrWhiteSpace(record.Title)) record.Title = original.Length <= 60 ? original : original.Substring(0, 60);
            if (string.IsNullOrWhiteSpace(record.Answer)) record.Answer = original;
            if (string.IsNullOrWhiteSpace(record.Type)) record.Type = mode == KnowledgeV2RecordsPageMode.Product ? "product_knowledge" : mode == KnowledgeV2RecordsPageMode.Process ? "procedure" : mode == KnowledgeV2RecordsPageMode.Learning ? "learning_candidate" : "business_fact";
            if (mode == KnowledgeV2RecordsPageMode.Product && record.Type == "business_fact") record.Type = "product_knowledge";
            if (mode == KnowledgeV2RecordsPageMode.Learning) record.Type = "learning_candidate";
            if (string.IsNullOrWhiteSpace(record.Intent)) record.Intent = "general";
            if (string.IsNullOrWhiteSpace(record.Predicate)) record.Predicate = "general";
            record.RiskLevel = string.Equals(record.RiskLevel, "high", StringComparison.OrdinalIgnoreCase) ? "high" : "normal";
            record.Status = mode == KnowledgeV2RecordsPageMode.Learning ? "candidate" : string.Equals(record.Status, "candidate", StringComparison.OrdinalIgnoreCase) ? "candidate" : "active";
            record.Confidence = Clamp(record.Confidence <= 0 ? 0.86 : record.Confidence);
            record.Authority = Clamp(record.Authority <= 0 ? 0.90 : record.Authority);
        }

        private static JObject ParseObject(string raw)
        {
            raw = (raw ?? string.Empty).Trim(); var start = raw.IndexOf('{'); var end = raw.LastIndexOf('}');
            if (start < 0 || end <= start) throw new Exception("未找到 JSON 对象");
            return JObject.Parse(raw.Substring(start, end - start + 1));
        }
        private static string Text(JObject obj, string name) { var token = obj == null ? null : obj[name]; return token == null ? string.Empty : token.ToString().Trim(); }
        private static List<string> List(JObject obj, string name)
        {
            var array = obj == null ? null : obj[name] as JArray; if (array == null) return new List<string>();
            return array.Select(x => (x == null ? string.Empty : x.ToString()).Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(48).ToList();
        }
        private static double Number(JObject obj, string name, double fallback) { double value; return double.TryParse(Text(obj, name), out value) ? Clamp(value) : fallback; }
        private static double Clamp(double value) { return Math.Max(0, Math.Min(1, value)); }
    }
}
