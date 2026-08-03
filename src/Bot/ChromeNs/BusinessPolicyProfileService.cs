using Bot.Options;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Client-side JSON business policy. Business phrases, workflow stages, prompt boundaries,
    /// answer validation messages and handoff exceptions live in business-policy.json instead of C#.
    /// </summary>
    internal static class BusinessPolicyProfileService
    {
        private const string Schema = "qianniu-ai-bot.business-policy";
        private static readonly object Sync = new object();
        private static readonly Regex NeverRegex = new Regex("(?!)", RegexOptions.Compiled);
        private static JObject _policy = new JObject();
        private static Dictionary<string, Regex> _regexes = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);
        private static DateTime _loadedWriteUtc = DateTime.MinValue;
        private static long _loadedLength = -1;
        private static DateTime _nextCheckUtc = DateTime.MinValue;
        private static bool _loaded;

        public static Regex GetRegex(string path)
        {
            EnsureLoaded();
            lock (Sync)
            {
                Regex value;
                if (_regexes.TryGetValue(path ?? string.Empty, out value)) return value;
                var pattern = ReadString(_policy, path);
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    _regexes[path ?? string.Empty] = NeverRegex;
                    return NeverRegex;
                }
                try
                {
                    value = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("运行策略正则无效: path=" + Safe(path) + ", error=" + Safe(ex.Message), 10);
                    value = NeverRegex;
                }
                _regexes[path ?? string.Empty] = value;
                return value;
            }
        }

        public static string GetString(string path)
        {
            EnsureLoaded();
            lock (Sync) return ReadString(_policy, path);
        }

        public static string GetJson()
        {
            EnsureLoaded();
            lock (Sync) return _policy.ToString(Formatting.Indented);
        }

        public static string GetDefaultJson()
        {
            var path = GetDefaultPath();
            if (!File.Exists(path)) throw new FileNotFoundException("安装包缺少默认运行策略文件。", path);
            var json = File.ReadAllText(path, Encoding.UTF8);
            Validate(json);
            return JObject.Parse(json).ToString(Formatting.Indented);
        }

        public static string SaveJson(string json)
        {
            var normalized = Validate(json).ToString(Formatting.Indented);
            var path = GetUserPath();
            var directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            var backup = string.Empty;
            if (File.Exists(path))
            {
                var backupDirectory = Path.Combine(directory, "backups");
                Directory.CreateDirectory(backupDirectory);
                backup = Path.Combine(backupDirectory, "business-policy-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                File.Copy(path, backup, true);
            }
            AtomicWrite(path, normalized);
            Invalidate();
            EnsureLoaded();
            Log.Info("客户端运行策略JSON已保存: file=" + Path.GetFileName(path)
                + (string.IsNullOrWhiteSpace(backup) ? string.Empty : ", backup=" + Path.GetFileName(backup)));
            return backup;
        }

        public static string RestoreDefault()
        {
            return SaveJson(GetDefaultJson());
        }

        public static string GetUserPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "data",
                "business-policy.json");
        }

        public static bool TryOverrideHandoff(
            string question,
            AutoReplyRuleDecision decision,
            out string detail)
        {
            detail = string.Empty;
            if (decision == null || !decision.Matched || string.IsNullOrWhiteSpace(decision.HitKeyword)) return false;
            EnsureLoaded();

            JArray rules;
            lock (Sync)
            {
                rules = _policy["handoffOverrides"] as JArray;
                if (rules == null) return false;
                rules = (JArray)rules.DeepClone();
            }

            foreach (var token in rules)
            {
                var rule = token as JObject;
                if (rule == null) continue;
                var keyword = Convert.ToString(rule["keyword"]);
                if (!Same(keyword, decision.HitKeyword)) continue;

                var text = question ?? string.Empty;
                var strong = CompileOptional(Convert.ToString(rule["strongRiskPattern"]));
                var allowAi = CompileOptional(Convert.ToString(rule["allowAiPattern"]));
                var fixedReply = CompileOptional(Convert.ToString(rule["fixedReplyPattern"]));

                if (strong.IsMatch(text))
                {
                    detail = "客户端JSON强风险语境成立，继续人工确认";
                    return false;
                }
                if (allowAi.IsMatch(text))
                {
                    decision.AllowAutoReply = true;
                    decision.UseAiReply = true;
                    decision.IsOffHours = false;
                    decision.ReplyText = string.Empty;
                    decision.Reason = "命中客户端JSON普通业务例外：" + keyword;
                    detail = "客户端JSON允许继续智能回答";
                    return true;
                }
                if (fixedReply.IsMatch(text))
                {
                    decision.AllowAutoReply = true;
                    decision.UseAiReply = false;
                    decision.IsOffHours = false;
                    decision.ReplyText = Convert.ToString(rule["fixedReply"]).Trim();
                    decision.Reason = "命中客户端JSON固定业务例外：" + keyword;
                    detail = "客户端JSON固定答复例外";
                    return true;
                }
                return false;
            }
            return false;
        }

        private static void EnsureLoaded()
        {
            lock (Sync)
            {
                var now = DateTime.UtcNow;
                if (_loaded && now < _nextCheckUtc) return;
                _nextCheckUtc = now.AddSeconds(2);

                var userPath = GetUserPath();
                EnsureUserFile(userPath);
                var info = new FileInfo(userPath);
                if (_loaded && info.Exists
                    && info.LastWriteTimeUtc == _loadedWriteUtc
                    && info.Length == _loadedLength) return;

                JObject loaded;
                try
                {
                    loaded = Validate(File.ReadAllText(userPath, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("读取客户端运行策略失败，回退安装包默认策略: " + Safe(ex.Message), 10);
                    loaded = Validate(GetDefaultJson());
                }
                _policy = loaded;
                _regexes = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);
                info.Refresh();
                _loadedWriteUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
                _loadedLength = info.Exists ? info.Length : -1;
                _loaded = true;
                Log.Info("客户端运行策略已加载: version=" + Convert.ToString(_policy["version"])
                    + ", file=" + Path.GetFileName(userPath));
            }
        }

        private static void EnsureUserFile(string userPath)
        {
            if (File.Exists(userPath)) return;
            var directory = Path.GetDirectoryName(userPath);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            var source = GetDefaultPath();
            if (!File.Exists(source)) throw new FileNotFoundException("安装包缺少默认运行策略文件。", source);
            AtomicWrite(userPath, File.ReadAllText(source, Encoding.UTF8));
        }

        private static JObject Validate(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new Exception("运行策略JSON不能为空。");
            JObject root;
            try { root = JObject.Parse(json); }
            catch (JsonException ex) { throw new Exception("运行策略JSON格式错误：" + ex.Message); }

            var schema = Convert.ToString(root["schema"]);
            if (!string.IsNullOrWhiteSpace(schema) && !Same(schema, Schema))
                throw new Exception("运行策略schema不匹配：" + schema);
            if (!(root["patterns"] is JObject)) throw new Exception("运行策略缺少 patterns 对象。");
            if (!(root["stages"] is JObject)) throw new Exception("运行策略缺少 stages 对象。");

            foreach (var property in ((JObject)root["patterns"]).Properties())
                CompileRequired(property.Name, Convert.ToString(property.Value));
            var overrides = root["handoffOverrides"] as JArray;
            if (overrides != null)
            {
                foreach (var item in overrides)
                {
                    var obj = item as JObject;
                    if (obj == null) throw new Exception("handoffOverrides 必须是对象数组。");
                    foreach (var name in new[] { "strongRiskPattern", "allowAiPattern", "fixedReplyPattern" })
                        CompileOptional(Convert.ToString(obj[name]));
                }
            }
            return root;
        }

        private static Regex CompileOptional(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return NeverRegex;
            try { return new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase); }
            catch (Exception ex) { throw new Exception("正则表达式无效：" + ex.Message); }
        }

        private static void CompileRequired(string name, string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) throw new Exception("patterns." + name + " 不能为空。");
            CompileOptional(pattern);
        }

        private static string ReadString(JObject root, string path)
        {
            JToken token = root;
            foreach (var part in (path ?? string.Empty).Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var obj = token as JObject;
                if (obj == null) return string.Empty;
                token = obj[part];
                if (token == null) return string.Empty;
            }
            return Convert.ToString(token) ?? string.Empty;
        }

        private static string GetDefaultPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "default-business-policy.json");
        }

        private static void AtomicWrite(string path, string content)
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private static void Invalidate()
        {
            lock (Sync)
            {
                _loaded = false;
                _nextCheckUtc = DateTime.MinValue;
                _loadedWriteUtc = DateTime.MinValue;
                _loadedLength = -1;
                _regexes.Clear();
            }
        }

        private static bool Same(string left, string right)
        {
            return string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string Safe(string value)
        {
            value = Regex.Replace((value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim(), @"\s+", " ");
            return value.Length <= 240 ? value : value.Substring(0, 240) + "...";
        }
    }
}
