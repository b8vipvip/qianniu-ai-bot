using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class StorePromptProfile
    {
        public string RawInput { get; set; }
        public string StandardPrompt { get; set; }
        public string UpdatedAt { get; set; }
    }

    internal static class StorePromptProfileService
    {
        private static readonly object Sync = new object();
        private static StorePromptProfile _cached;

        public static StorePromptProfile GetProfile()
        {
            lock (Sync)
            {
                if (_cached != null) return Clone(_cached);
                _cached = LoadInternal();
                return Clone(_cached);
            }
        }

        public static string GetStandardPrompt()
        {
            var profile = GetProfile();
            return (profile.StandardPrompt ?? string.Empty).Trim();
        }

        public static string BuildPromptAddon()
        {
            var prompt = GetStandardPrompt();
            if (string.IsNullOrWhiteSpace(prompt)) return string.Empty;
            return "\n\n【店铺固定事实与服务边界｜高优先级】\n"
                + prompt
                + "\n以上内容是本店长期稳定的事实和边界。回答时必须遵守；不得自行扩大链接服务范围、售后保障、适用设备、账号规则或其他承诺。"
                + "如果当前买家问题与这些信息无关，不要生硬复述。\n";
        }

        public static void Save(string rawInput, string standardPrompt)
        {
            var profile = new StorePromptProfile
            {
                RawInput = Clean(rawInput, 30000),
                StandardPrompt = Clean(standardPrompt, 12000),
                UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            var path = GetPath();
            var directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            var temp = path + ".tmp";
            var json = JsonConvert.SerializeObject(profile, Formatting.Indented);
            lock (Sync)
            {
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                _cached = profile;
            }
            Log.Info("店铺固定提示词已保存: chars=" + profile.StandardPrompt.Length);
        }

        public static async Task<string> GenerateStandardPromptAsync(
            string rawInput,
            CancellationToken token)
        {
            rawInput = Clean(rawInput, 30000);
            if (string.IsNullOrWhiteSpace(rawInput))
            {
                throw new Exception("请先填写店铺介绍、服务范围、链接用途、售后保障等原始信息。");
            }

            var messages = new JArray
            {
                new JObject
                {
                    ["role"] = "system",
                    ["content"] =
                        "你是电商客服系统提示词整理专家。请把商家提供的原始资料整理成一份可长期作为AI客服前置系统提示词的中文标准提示词。"
                        + "必须严格忠于原始资料，不得补充或猜测任何价格、库存、时效、售后、链接能力、账号规则或服务范围。"
                        + "输出纯提示词正文，不要JSON，不要代码围栏，不要解释。"
                        + "建议按以下结构组织：店铺定位；核心商品/服务；链接与服务范围；支持事项；明确不支持事项；购买/使用前提；售后保障；风险与人工确认边界；回复原则。"
                        + "把重复信息合并，把口语和零散说明整理成清晰、可执行、无歧义的规则。"
                        + "对于原始资料没有说明的事项，明确要求AI不得自行承诺，而不是替商家补全答案。"
                },
                new JObject
                {
                    ["role"] = "user",
                    ["content"] = "请根据以下本店原始资料生成标准前置提示词：\n\n" + rawInput
                }
            };

            var result = await Task.Run(
                () => MyOpenAI.CallStructuredChat(messages, 4000, 0.05, 240, token),
                token);
            if (result == null || !result.Success || string.IsNullOrWhiteSpace(result.Answer))
            {
                throw new Exception(result == null || string.IsNullOrWhiteSpace(result.Error)
                    ? "AI没有返回有效提示词。"
                    : result.Error);
            }

            var prompt = NormalizeGenerated(result.Answer);
            if (prompt.Length < 20) throw new Exception("AI生成的提示词内容过短，请检查输入资料后重试。");
            Save(rawInput, prompt);
            return prompt;
        }

        private static StorePromptProfile LoadInternal()
        {
            try
            {
                var path = GetPath();
                if (!File.Exists(path)) return NewProfile();
                var json = File.ReadAllText(path, Encoding.UTF8);
                return JsonConvert.DeserializeObject<StorePromptProfile>(json) ?? NewProfile();
            }
            catch (Exception ex)
            {
                Log.Info("读取店铺固定提示词失败，使用空配置：" + ex.Message);
                return NewProfile();
            }
        }

        private static string GetPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "data",
                "store-prompt-profile.json");
        }

        private static StorePromptProfile Clone(StorePromptProfile source)
        {
            source = source ?? NewProfile();
            return new StorePromptProfile
            {
                RawInput = source.RawInput ?? string.Empty,
                StandardPrompt = source.StandardPrompt ?? string.Empty,
                UpdatedAt = source.UpdatedAt ?? string.Empty
            };
        }

        private static StorePromptProfile NewProfile()
        {
            return new StorePromptProfile
            {
                RawInput = string.Empty,
                StandardPrompt = string.Empty,
                UpdatedAt = string.Empty
            };
        }

        private static string NormalizeGenerated(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.StartsWith("```", StringComparison.Ordinal))
            {
                var firstLine = value.IndexOf('\n');
                if (firstLine >= 0) value = value.Substring(firstLine + 1);
                var end = value.LastIndexOf("```", StringComparison.Ordinal);
                if (end >= 0) value = value.Substring(0, end);
            }
            return Clean(value, 12000);
        }

        private static string Clean(string value, int max)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length <= max ? value : value.Substring(0, max).Trim();
        }
    }
}
