using BotLib.Db.Sqlite;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bot.ChromeNs
{
    public class AiEndpointConfig
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public string BaseUrl { get; set; }
        public string ApiKey { get; set; }
        public string Model { get; set; }
        public string SystemPrompt { get; set; }
        public bool Enabled { get; set; }
        public int Priority { get; set; }
        public int Weight { get; set; }
        public int TimeoutSeconds { get; set; }
        public int RetryCount { get; set; }
        public string LastStatus { get; set; }
        public long LastLatencyMs { get; set; }
        public DateTime LastTestTime { get; set; }

        public AiEndpointConfig()
        {
            Id = Guid.NewGuid().ToString("N");
            Name = "默认接口";
            Type = "OpenAI兼容";
            BaseUrl = string.Empty;
            ApiKey = string.Empty;
            Model = string.Empty;
            SystemPrompt = string.Empty;
            Enabled = true;
            Priority = 1;
            Weight = 1;
            TimeoutSeconds = 35;
            RetryCount = 0;
            LastStatus = "未测试";
            LastLatencyMs = 0;
            LastTestTime = DateTime.MinValue;
        }

        [JsonIgnore]
        public string ApiKeyMasked
        {
            get
            {
                if (string.IsNullOrEmpty(ApiKey)) return string.Empty;
                if (ApiKey.Length <= 10) return "******";
                return ApiKey.Substring(0, 6) + "..." + ApiKey.Substring(ApiKey.Length - 4);
            }
        }

        public AiEndpointConfig Clone()
        {
            return new AiEndpointConfig
            {
                Id = string.IsNullOrEmpty(Id) ? Guid.NewGuid().ToString("N") : Id,
                Name = Name,
                Type = Type,
                BaseUrl = BaseUrl,
                ApiKey = ApiKey,
                Model = Model,
                SystemPrompt = SystemPrompt,
                Enabled = Enabled,
                Priority = Priority,
                Weight = Weight,
                TimeoutSeconds = TimeoutSeconds,
                RetryCount = RetryCount,
                LastStatus = LastStatus,
                LastLatencyMs = LastLatencyMs,
                LastTestTime = LastTestTime
            };
        }
    }

    public static class AiEndpointStore
    {
        private const string EndpointKey = "AiEndpointListJson";
        private const string StrategyKey = "AiDispatchStrategy";
        private const string StoreScope = "ai";

        public static string GetStrategy()
        {
            return PersistentParams.GetParam2Key(StrategyKey, StoreScope, "按优先级顺序调用");
        }

        public static void SetStrategy(string strategy)
        {
            PersistentParams.TrySaveParam2Key(StrategyKey, StoreScope, string.IsNullOrWhiteSpace(strategy) ? "按优先级顺序调用" : strategy.Trim());
        }

        public static string GetEndpointsJson()
        {
            return PersistentParams.GetParam2Key(EndpointKey, StoreScope, string.Empty);
        }

        public static void SaveEndpointsJson(string json)
        {
            PersistentParams.TrySaveParam2Key(EndpointKey, StoreScope, json ?? string.Empty);
        }

        public static List<AiEndpointConfig> GetEndpoints()
        {
            var json = GetEndpointsJson();
            var list = new List<AiEndpointConfig>();
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    list = JsonConvert.DeserializeObject<List<AiEndpointConfig>>(json) ?? new List<AiEndpointConfig>();
                }
                catch
                {
                    list = new List<AiEndpointConfig>();
                }
            }

            if (list.Count < 1)
            {
                list.Add(CreateLegacyDefaultEndpoint());
            }

            Normalize(list);
            return list;
        }

        public static List<AiEndpointConfig> GetEnabledEndpoints()
        {
            var list = GetEndpoints()
                .Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.ApiKey) && !string.IsNullOrWhiteSpace(e.Model))
                .OrderBy(e => e.Priority)
                .ThenBy(e => e.Name)
                .ToList();
            return list;
        }

        public static void SaveEndpoints(IEnumerable<AiEndpointConfig> endpoints)
        {
            var list = endpoints == null ? new List<AiEndpointConfig>() : endpoints.Select(e => e == null ? new AiEndpointConfig() : e.Clone()).ToList();
            if (list.Count < 1) list.Add(CreateLegacyDefaultEndpoint());
            Normalize(list);
            SaveEndpointsJson(JsonConvert.SerializeObject(list, Formatting.Indented));

            var primary = list.OrderBy(e => e.Priority).FirstOrDefault();
            if (primary != null)
            {
                Params.Robot.SetBaseUrl(primary.BaseUrl ?? string.Empty);
                Params.Robot.SetApiKey(primary.ApiKey ?? string.Empty);
                Params.Robot.SetModelName(primary.Model ?? string.Empty);
                Params.Robot.SetSystemPrompt(primary.SystemPrompt ?? string.Empty);
            }
        }

        public static AiEndpointConfig CreateLegacyDefaultEndpoint()
        {
            return new AiEndpointConfig
            {
                Name = "默认接口",
                Type = "OpenAI兼容",
                BaseUrl = Params.Robot.GetBaseUrl() ?? string.Empty,
                ApiKey = Params.Robot.GetApiKey() ?? string.Empty,
                Model = Params.Robot.GetModelName() ?? string.Empty,
                SystemPrompt = Params.Robot.GetSystemPrompt() ?? string.Empty,
                Enabled = true,
                Priority = 1,
                Weight = 1,
                TimeoutSeconds = 35,
                RetryCount = 0,
                LastStatus = "未测试"
            };
        }

        private static void Normalize(List<AiEndpointConfig> list)
        {
            var p = 1;
            foreach (var endpoint in list.OrderBy(e => e.Priority).ThenBy(e => e.Name).ToList())
            {
                if (string.IsNullOrWhiteSpace(endpoint.Id)) endpoint.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(endpoint.Name)) endpoint.Name = "接口" + p;
                if (string.IsNullOrWhiteSpace(endpoint.Type)) endpoint.Type = "OpenAI兼容";
                if (endpoint.Priority <= 0) endpoint.Priority = p;
                if (endpoint.Weight <= 0) endpoint.Weight = 1;
                if (endpoint.TimeoutSeconds <= 0) endpoint.TimeoutSeconds = 35;
                if (endpoint.RetryCount < 0) endpoint.RetryCount = 0;
                p++;
            }
        }
    }
}
