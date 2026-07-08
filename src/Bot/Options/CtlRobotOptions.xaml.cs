using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Bot.ChromeNs;
using BotLib;

namespace Bot.Options
{
    public partial class CtlRobotOptions : UserControl, IOptions
    {
        private string _seller;
        private ObservableCollection<AiEndpointConfig> _endpoints;
        private bool _loadingPrompt;

        public CtlRobotOptions(string seller)
        {
            InitializeComponent();
            InitUI(seller);
        }

        public OptionEnum OptionType
        {
            get { return OptionEnum.Robot; }
        }

        public void InitUI(string seller)
        {
            _seller = seller;
            _endpoints = new ObservableCollection<AiEndpointConfig>(AiEndpointStore.GetEndpoints());
            gridEndpoints.ItemsSource = _endpoints;
            SelectStrategy(AiEndpointStore.GetStrategy());
            if (_endpoints.Count > 0)
            {
                gridEndpoints.SelectedIndex = 0;
            }
            txtApiTestResult.Text = "提示：编辑表格后点击保存；新增/导入/测试后也建议保存一次。";
        }

        private void SelectStrategy(string strategy)
        {
            if (string.IsNullOrWhiteSpace(strategy)) strategy = "按优先级顺序调用";
            foreach (ComboBoxItem item in cmbStrategy.Items)
            {
                if ((item.Content ?? string.Empty).ToString() == strategy)
                {
                    cmbStrategy.SelectedItem = item;
                    return;
                }
            }
            cmbStrategy.SelectedIndex = 0;
        }

        private string CurrentStrategy()
        {
            var item = cmbStrategy.SelectedItem as ComboBoxItem;
            return item == null ? "按优先级顺序调用" : (item.Content ?? string.Empty).ToString();
        }

        public void NavHelp()
        {
            MessageBox.Show("API接口支持 OpenAI 官方和 OpenAI 兼容中转站。建议至少配置一个可用接口，并点击测试选中确认连接正常。", "帮助", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public void RestoreDefault()
        {
            _endpoints.Clear();
            _endpoints.Add(new AiEndpointConfig());
            gridEndpoints.SelectedIndex = 0;
            SelectStrategy("按优先级顺序调用");
            txtApiTestResult.Text = "已恢复默认，请保存。";
        }

        public void Save(string seller)
        {
            CommitGridEdit();
            SyncSelectedPrompt();
            NormalizePriority();
            AiEndpointStore.SetStrategy(CurrentStrategy());
            AiEndpointStore.SaveEndpoints(_endpoints);
            txtApiTestResult.Text = "配置已保存。";
        }

        private void CommitGridEdit()
        {
            try
            {
                gridEndpoints.CommitEdit(DataGridEditingUnit.Cell, true);
                gridEndpoints.CommitEdit(DataGridEditingUnit.Row, true);
            }
            catch
            {
            }
        }

        private AiEndpointConfig SelectedEndpoint()
        {
            return gridEndpoints.SelectedItem as AiEndpointConfig;
        }

        private void NormalizePriority()
        {
            var i = 1;
            foreach (var ep in _endpoints)
            {
                if (string.IsNullOrWhiteSpace(ep.Id)) ep.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(ep.Name)) ep.Name = "接口" + i;
                if (string.IsNullOrWhiteSpace(ep.Type)) ep.Type = "OpenAI兼容";
                if (ep.Weight <= 0) ep.Weight = 1;
                if (ep.TimeoutSeconds <= 0) ep.TimeoutSeconds = 35;
                if (ep.Priority <= 0) ep.Priority = i;
                i++;
            }
        }

        private void SyncSelectedPrompt()
        {
            var ep = SelectedEndpoint();
            if (ep != null && !_loadingPrompt)
            {
                ep.SystemPrompt = txtSystemPrompt.Text;
            }
        }

        private void gridEndpoints_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _loadingPrompt = true;
            try
            {
                var ep = SelectedEndpoint();
                txtSystemPrompt.Text = ep == null ? string.Empty : (ep.SystemPrompt ?? string.Empty);
            }
            finally
            {
                _loadingPrompt = false;
            }
        }

        private void txtSystemPrompt_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loadingPrompt) return;
            var ep = SelectedEndpoint();
            if (ep != null)
            {
                ep.SystemPrompt = txtSystemPrompt.Text;
            }
        }

        private void btnAddEndpoint_Click(object sender, RoutedEventArgs e)
        {
            CommitGridEdit();
            var ep = new AiEndpointConfig
            {
                Name = "接口" + (_endpoints.Count + 1),
                Priority = _endpoints.Count + 1,
                Type = "OpenAI兼容",
                Enabled = true,
                TimeoutSeconds = 35,
                Weight = 1
            };
            _endpoints.Add(ep);
            gridEndpoints.SelectedItem = ep;
            txtApiTestResult.Text = "已新增接口，请填写 BaseUrl / ApiKey / Model。";
        }

        private void btnDeleteEndpoint_Click(object sender, RoutedEventArgs e)
        {
            var ep = SelectedEndpoint();
            if (ep == null) return;
            if (_endpoints.Count <= 1)
            {
                txtApiTestResult.Text = "至少保留一个接口。";
                return;
            }
            var idx = _endpoints.IndexOf(ep);
            _endpoints.Remove(ep);
            NormalizePriority();
            gridEndpoints.SelectedIndex = Math.Min(idx, _endpoints.Count - 1);
            txtApiTestResult.Text = "已删除接口，请保存。";
        }

        private void MoveSelected(int offset)
        {
            var ep = SelectedEndpoint();
            if (ep == null) return;
            var oldIndex = _endpoints.IndexOf(ep);
            var newIndex = oldIndex + offset;
            if (newIndex < 0 || newIndex >= _endpoints.Count) return;
            _endpoints.Move(oldIndex, newIndex);
            var p = 1;
            foreach (var item in _endpoints)
            {
                item.Priority = p++;
            }
            gridEndpoints.SelectedItem = ep;
            gridEndpoints.Items.Refresh();
        }

        private void btnMoveUp_Click(object sender, RoutedEventArgs e)
        {
            MoveSelected(-1);
        }

        private void btnMoveDown_Click(object sender, RoutedEventArgs e)
        {
            MoveSelected(1);
        }

        private async void btnTestSelected_Click(object sender, RoutedEventArgs e)
        {
            CommitGridEdit();
            SyncSelectedPrompt();
            var ep = SelectedEndpoint();
            if (ep == null) return;
            btnTestSelected.IsEnabled = false;
            txtApiTestResult.Text = "正在测试 " + ep.Name + " ...";
            try
            {
                var result = await Task.Run(() => MyOpenAI.TestConnection(ep));
                txtApiTestResult.Text = ep.Name + "：" + result;
                gridEndpoints.Items.Refresh();
                Log.Info("AI连接测试结果：" + txtApiTestResult.Text);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                txtApiTestResult.Text = "测试异常：" + ex.Message;
            }
            finally
            {
                btnTestSelected.IsEnabled = true;
            }
        }

        private async void btnTestAll_Click(object sender, RoutedEventArgs e)
        {
            CommitGridEdit();
            SyncSelectedPrompt();
            btnTestAll.IsEnabled = false;
            try
            {
                var results = new List<string>();
                foreach (var ep in _endpoints)
                {
                    txtApiTestResult.Text = "正在测试 " + ep.Name + " ...";
                    var result = await Task.Run(() => MyOpenAI.TestConnection(ep));
                    results.Add(ep.Name + "：" + result);
                    gridEndpoints.Items.Refresh();
                }
                txtApiTestResult.Text = string.Join("\n", results);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                txtApiTestResult.Text = "测试异常：" + ex.Message;
            }
            finally
            {
                btnTestAll.IsEnabled = true;
            }
        }

        private JObject BuildConfigJson()
        {
            CommitGridEdit();
            SyncSelectedPrompt();
            return new JObject
            {
                ["version"] = 2,
                ["strategy"] = CurrentStrategy(),
                ["endpoints"] = JArray.FromObject(_endpoints.ToList()),
                ["exportedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
        }

        private void ApplyConfigJson(string text)
        {
            var token = JToken.Parse(text);
            List<AiEndpointConfig> list;
            if (token.Type == JTokenType.Array)
            {
                list = token.ToObject<List<AiEndpointConfig>>();
            }
            else
            {
                var obj = (JObject)token;
                if (obj["endpoints"] != null)
                {
                    list = obj["endpoints"].ToObject<List<AiEndpointConfig>>();
                    SelectStrategy((obj["strategy"] ?? string.Empty).ToString());
                }
                else
                {
                    list = new List<AiEndpointConfig>
                    {
                        new AiEndpointConfig
                        {
                            Name = "导入接口",
                            BaseUrl = (obj["baseUrl"] ?? obj["BaseUrl"] ?? string.Empty).ToString(),
                            ApiKey = (obj["apiKey"] ?? obj["ApiKey"] ?? string.Empty).ToString(),
                            Model = (obj["model"] ?? obj["Model"] ?? obj["modelName"] ?? string.Empty).ToString(),
                            SystemPrompt = (obj["systemPrompt"] ?? obj["SystemPrompt"] ?? string.Empty).ToString(),
                            Enabled = true,
                            Priority = 1,
                            Type = "OpenAI兼容",
                            TimeoutSeconds = 35,
                            Weight = 1
                        }
                    };
                }
            }

            if (list == null || list.Count < 1) throw new Exception("配置文件中没有接口。 ");
            _endpoints.Clear();
            foreach (var ep in list)
            {
                _endpoints.Add(ep ?? new AiEndpointConfig());
            }
            NormalizePriority();
            gridEndpoints.SelectedIndex = 0;
        }

        private void btnExportConfig_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new SaveFileDialog
                {
                    Title = "导出AI配置",
                    FileName = "qianniu-ai-config.json",
                    Filter = "JSON配置文件 (*.json)|*.json|所有文件 (*.*)|*.*"
                };
                if (dlg.ShowDialog() != true) return;

                File.WriteAllText(dlg.FileName, BuildConfigJson().ToString(Formatting.Indented), System.Text.Encoding.UTF8);
                txtApiTestResult.Text = "配置已导出：" + dlg.FileName;
                Log.Info("AI配置已导出：" + dlg.FileName);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                txtApiTestResult.Text = "导出失败：" + ex.Message;
            }
        }

        private void btnImportConfig_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = "导入AI配置",
                    Filter = "JSON配置文件 (*.json)|*.json|所有文件 (*.*)|*.*"
                };
                if (dlg.ShowDialog() != true) return;

                ApplyConfigJson(File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8));
                txtApiTestResult.Text = "配置已导入，请点击保存。";
                Log.Info("AI配置已导入：" + dlg.FileName);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                txtApiTestResult.Text = "导入失败：" + ex.Message;
            }
        }
    }
}

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
            return BotLib.Db.Sqlite.PersistentParams.GetParam2Key(StrategyKey, StoreScope, "按优先级顺序调用");
        }

        public static void SetStrategy(string strategy)
        {
            BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(StrategyKey, StoreScope, string.IsNullOrWhiteSpace(strategy) ? "按优先级顺序调用" : strategy.Trim());
        }

        public static string GetEndpointsJson()
        {
            return BotLib.Db.Sqlite.PersistentParams.GetParam2Key(EndpointKey, StoreScope, string.Empty);
        }

        public static void SaveEndpointsJson(string json)
        {
            BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(EndpointKey, StoreScope, json ?? string.Empty);
        }

        public static List<AiEndpointConfig> GetEndpoints()
        {
            var json = GetEndpointsJson();
            var list = new List<AiEndpointConfig>();
            if (!string.IsNullOrWhiteSpace(json))
            {
                try { list = JsonConvert.DeserializeObject<List<AiEndpointConfig>>(json) ?? new List<AiEndpointConfig>(); }
                catch { list = new List<AiEndpointConfig>(); }
            }
            if (list.Count < 1) list.Add(CreateLegacyDefaultEndpoint());
            Normalize(list);
            return list;
        }

        public static List<AiEndpointConfig> GetEnabledEndpoints()
        {
            return GetEndpoints()
                .Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.ApiKey) && !string.IsNullOrWhiteSpace(e.Model))
                .OrderBy(e => e.Priority)
                .ThenBy(e => e.Name)
                .ToList();
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

    public class ApiUsageSnapshot
    {
        public string EndpointId { get; set; }
        public string EndpointName { get; set; }
        public long TotalCalls { get; set; }
        public long TodayCalls { get; set; }
        public long TotalTokens { get; set; }
        public long TodayTokens { get; set; }
        public long FailedCalls { get; set; }
        public long TodayFailedCalls { get; set; }
        public long AvgLatencyMs { get; set; }
        public string LastStatus { get; set; }
    }

    public class RuntimeStatsSnapshot
    {
        public long TotalReceptionCount { get; set; }
        public long TodayReceptionCount { get; set; }
        public long TotalAutoReplies { get; set; }
        public long TodayAutoReplies { get; set; }
        public long TotalAiCalls { get; set; }
        public long TodayAiCalls { get; set; }
        public long TotalAiFailedCalls { get; set; }
        public long TodayAiFailedCalls { get; set; }
        public long TotalTokens { get; set; }
        public long TodayTokens { get; set; }
        public long AvgLatencyMs { get; set; }
        public string LastError { get; set; }
        public List<ApiUsageSnapshot> ApiUsages { get; set; }
    }

    public static class BotRuntimeStats
    {
        private class ApiUsageCounter
        {
            public string EndpointId;
            public string EndpointName;
            public long TotalCalls;
            public long TodayCalls;
            public long TotalTokens;
            public long TodayTokens;
            public long FailedCalls;
            public long TodayFailedCalls;
            public long LatencyTotalMs;
            public long LatencyCount;
            public string LastStatus;
        }

        private static readonly object SyncObj = new object();
        private static readonly Dictionary<string, ApiUsageCounter> ApiCounters = new Dictionary<string, ApiUsageCounter>();
        private static DateTime Today = DateTime.Today;
        private static long totalReceptionCount;
        private static long todayReceptionCount;
        private static long totalAutoReplies;
        private static long todayAutoReplies;
        private static long totalAiCalls;
        private static long todayAiCalls;
        private static long totalAiFailedCalls;
        private static long todayAiFailedCalls;
        private static long totalTokens;
        private static long todayTokens;
        private static long latencyTotalMs;
        private static long latencyCount;
        private static string lastError = string.Empty;

        private static void EnsureToday()
        {
            if (Today == DateTime.Today) return;
            Today = DateTime.Today;
            todayReceptionCount = 0;
            todayAutoReplies = 0;
            todayAiCalls = 0;
            todayAiFailedCalls = 0;
            todayTokens = 0;
            foreach (var item in ApiCounters.Values)
            {
                item.TodayCalls = 0;
                item.TodayTokens = 0;
                item.TodayFailedCalls = 0;
            }
        }

        public static void RecordReception()
        {
            lock (SyncObj)
            {
                EnsureToday();
                totalReceptionCount++;
                todayReceptionCount++;
            }
        }

        public static void RecordDisplayedAnswer(bool autoReply)
        {
            if (!autoReply) return;
            lock (SyncObj)
            {
                EnsureToday();
                totalAutoReplies++;
                todayAutoReplies++;
            }
        }

        public static void RecordSendResult(bool success)
        {
            if (success) return;
            lock (SyncObj)
            {
                EnsureToday();
                lastError = "发送失败";
            }
        }

        public static void RecordAiCall(AiEndpointConfig endpoint, int inputTokens, int outputTokens, bool success, long latencyMs, string status)
        {
            lock (SyncObj)
            {
                EnsureToday();
                var endpointId = endpoint == null ? "default" : (endpoint.Id ?? "default");
                var endpointName = endpoint == null ? "默认接口" : (endpoint.Name ?? "默认接口");
                ApiUsageCounter counter;
                if (!ApiCounters.TryGetValue(endpointId, out counter))
                {
                    counter = new ApiUsageCounter { EndpointId = endpointId, EndpointName = endpointName, LastStatus = string.Empty };
                    ApiCounters[endpointId] = counter;
                }
                var tokens = Math.Max(0, inputTokens) + Math.Max(0, outputTokens);
                counter.EndpointName = endpointName;
                counter.TotalCalls++;
                counter.TodayCalls++;
                counter.TotalTokens += tokens;
                counter.TodayTokens += tokens;
                counter.LatencyTotalMs += Math.Max(0, latencyMs);
                counter.LatencyCount++;
                counter.LastStatus = status ?? string.Empty;
                if (!success)
                {
                    counter.FailedCalls++;
                    counter.TodayFailedCalls++;
                    lastError = status ?? string.Empty;
                }

                totalAiCalls++;
                todayAiCalls++;
                totalTokens += tokens;
                todayTokens += tokens;
                latencyTotalMs += Math.Max(0, latencyMs);
                latencyCount++;
                if (!success)
                {
                    totalAiFailedCalls++;
                    todayAiFailedCalls++;
                }
            }
        }

        public static RuntimeStatsSnapshot GetSnapshot()
        {
            lock (SyncObj)
            {
                EnsureToday();
                var apiUsages = ApiCounters.Values
                    .OrderByDescending(a => a.TodayCalls)
                    .ThenBy(a => a.EndpointName)
                    .Select(a => new ApiUsageSnapshot
                    {
                        EndpointId = a.EndpointId,
                        EndpointName = a.EndpointName,
                        TotalCalls = a.TotalCalls,
                        TodayCalls = a.TodayCalls,
                        TotalTokens = a.TotalTokens,
                        TodayTokens = a.TodayTokens,
                        FailedCalls = a.FailedCalls,
                        TodayFailedCalls = a.TodayFailedCalls,
                        AvgLatencyMs = a.LatencyCount <= 0 ? 0 : a.LatencyTotalMs / a.LatencyCount,
                        LastStatus = a.LastStatus ?? string.Empty
                    }).ToList();

                return new RuntimeStatsSnapshot
                {
                    TotalReceptionCount = totalReceptionCount,
                    TodayReceptionCount = todayReceptionCount,
                    TotalAutoReplies = totalAutoReplies,
                    TodayAutoReplies = todayAutoReplies,
                    TotalAiCalls = totalAiCalls,
                    TodayAiCalls = todayAiCalls,
                    TotalAiFailedCalls = totalAiFailedCalls,
                    TodayAiFailedCalls = todayAiFailedCalls,
                    TotalTokens = totalTokens,
                    TodayTokens = todayTokens,
                    AvgLatencyMs = latencyCount <= 0 ? 0 : latencyTotalMs / latencyCount,
                    LastError = lastError ?? string.Empty,
                    ApiUsages = apiUsages
                };
            }
        }
    }
}