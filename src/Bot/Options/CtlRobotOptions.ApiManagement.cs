using Bot.ChromeNs;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Bot.Options
{
    public partial class CtlRobotOptions
    {
        private void btnAddEndpointAdvanced_Click(object sender, RoutedEventArgs e)
        {
            CommitGridEdit();
            SyncSelectedPrompt();

            var seed = new AiEndpointConfig
            {
                Name = "接口" + (_endpoints.Count + 1),
                Type = "OpenAI兼容",
                Enabled = true,
                Priority = _endpoints.Count + 1,
                Weight = 1,
                TimeoutSeconds = 35
            };

            var editor = new AiEndpointEditorWindow(seed, new string[0], "手动新增接口")
            {
                Owner = Window.GetWindow(this)
            };
            if (editor.ShowDialog() != true) return;

            var endpoint = editor.Result;
            endpoint.Priority = _endpoints.Count + 1;
            _endpoints.Add(endpoint);
            NormalizePriority();
            gridEndpoints.SelectedItem = endpoint;
            gridEndpoints.ScrollIntoView(endpoint);
            gridEndpoints.Items.Refresh();
            txtApiTestResult.Text = "已新增接口“" + endpoint.Name + "”，请点击窗口右下角【保存】。";
        }

        private void btnEditEndpointAdvanced_Click(object sender, RoutedEventArgs e)
        {
            CommitGridEdit();
            SyncSelectedPrompt();
            var selected = SelectedEndpoint();
            if (selected == null)
            {
                txtApiTestResult.Text = "请先选择一个接口。";
                return;
            }

            var editor = new AiEndpointEditorWindow(selected.Clone(), new[] { selected.Model }, "编辑接口配置")
            {
                Owner = Window.GetWindow(this)
            };
            if (editor.ShowDialog() != true) return;

            CopyEndpoint(editor.Result, selected, true);
            gridEndpoints.Items.Refresh();
            gridEndpoints.SelectedItem = selected;
            txtSystemPrompt.Text = selected.SystemPrompt ?? string.Empty;
            txtApiTestResult.Text = "接口“" + selected.Name + "”已更新，请点击【保存】。";
        }

        private void gridEndpoints_MouseDoubleClickAdvanced(object sender, MouseButtonEventArgs e)
        {
            if (SelectedEndpoint() != null)
            {
                btnEditEndpointAdvanced_Click(sender, new RoutedEventArgs());
            }
        }

        private void btnPasteRecognize_Click(object sender, RoutedEventArgs e)
        {
            var pasteWindow = new ApiConfigPasteWindow
            {
                Owner = Window.GetWindow(this)
            };
            if (pasteWindow.ShowDialog() != true) return;

            ApiConfigRecognitionResult recognition;
            try
            {
                recognition = ApiConfigRecognizer.Recognize(pasteWindow.RawText);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                txtApiTestResult.Text = "识别失败：" + ex.Message;
                return;
            }

            var seed = recognition.ToEndpoint(_endpoints.Count + 1);
            var editor = new AiEndpointEditorWindow(seed, recognition.Models, recognition.BuildSummary())
            {
                Owner = Window.GetWindow(this)
            };
            if (editor.ShowDialog() != true) return;

            var endpoint = editor.Result;
            endpoint.Priority = _endpoints.Count + 1;
            _endpoints.Add(endpoint);
            NormalizePriority();
            gridEndpoints.SelectedItem = endpoint;
            gridEndpoints.ScrollIntoView(endpoint);
            gridEndpoints.Items.Refresh();
            txtApiTestResult.Text = "识别并添加了接口“" + endpoint.Name + "”。密钥仅保存在本机配置中，请点击【保存】。";
        }

        private async void btnTestSelectedAdvanced_Click(object sender, RoutedEventArgs e)
        {
            CommitGridEdit();
            SyncSelectedPrompt();
            var endpoint = SelectedEndpoint();
            if (endpoint == null)
            {
                txtApiTestResult.Text = "请先选择一个接口。";
                return;
            }

            await TestOneEndpointAsync(endpoint);
        }

        private async void btnTestAllAdvanced_Click(object sender, RoutedEventArgs e)
        {
            CommitGridEdit();
            SyncSelectedPrompt();
            btnTestAll.IsEnabled = false;
            btnTestSelected.IsEnabled = false;
            try
            {
                var results = new List<string>();
                foreach (var endpoint in _endpoints)
                {
                    txtApiTestResult.Text = "正在测试“" + endpoint.Name + "”：验证 HTTP 通信、鉴权、请求发送、模型响应和回显内容...";
                    var result = await Task.Run(() => AiEndpointTester.Test(endpoint));
                    ApplyTestResult(endpoint, result);
                    results.Add(endpoint.Name + "：" + result.DisplayText);
                    gridEndpoints.Items.Refresh();
                }
                txtApiTestResult.Text = string.Join(Environment.NewLine + Environment.NewLine, results);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                txtApiTestResult.Text = "测试异常：" + ex.Message;
            }
            finally
            {
                btnTestAll.IsEnabled = true;
                btnTestSelected.IsEnabled = true;
            }
        }

        private async Task TestOneEndpointAsync(AiEndpointConfig endpoint)
        {
            btnTestSelected.IsEnabled = false;
            btnTestAll.IsEnabled = false;
            txtApiTestResult.Text = "正在测试“" + endpoint.Name + "”：验证 HTTP 通信、鉴权、请求发送、模型响应和回显内容...";
            try
            {
                var result = await Task.Run(() => AiEndpointTester.Test(endpoint));
                ApplyTestResult(endpoint, result);
                txtApiTestResult.Text = endpoint.Name + "：" + result.DisplayText;
                gridEndpoints.Items.Refresh();
                Log.Info("AI接口完整测试结果：endpoint=" + endpoint.Name + ", success=" + result.Success + ", roundTrip=" + result.RoundTripVerified + ", latencyMs=" + result.LatencyMs);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                txtApiTestResult.Text = "测试异常：" + ex.Message;
            }
            finally
            {
                btnTestSelected.IsEnabled = true;
                btnTestAll.IsEnabled = true;
            }
        }

        private static void ApplyTestResult(AiEndpointConfig endpoint, ApiEndpointTestResult result)
        {
            endpoint.LastLatencyMs = result.LatencyMs;
            endpoint.LastTestTime = DateTime.Now;
            if (!result.Success)
            {
                endpoint.LastStatus = "失败：" + result.ShortStatus;
            }
            else if (result.RoundTripVerified)
            {
                endpoint.LastStatus = "可用：收发验证通过";
            }
            else
            {
                endpoint.LastStatus = "可用：响应正常，回显未严格匹配";
            }
        }

        private static void CopyEndpoint(AiEndpointConfig source, AiEndpointConfig target, bool preserveId)
        {
            var id = target.Id;
            target.Name = source.Name;
            target.Type = source.Type;
            target.BaseUrl = source.BaseUrl;
            target.ApiKey = source.ApiKey;
            target.Model = source.Model;
            target.SystemPrompt = source.SystemPrompt;
            target.Enabled = source.Enabled;
            target.Priority = source.Priority;
            target.Weight = source.Weight;
            target.TimeoutSeconds = source.TimeoutSeconds;
            target.RetryCount = source.RetryCount;
            target.LastStatus = source.LastStatus;
            target.LastLatencyMs = source.LastLatencyMs;
            target.LastTestTime = source.LastTestTime;
            target.Id = preserveId && !string.IsNullOrWhiteSpace(id) ? id : source.Id;
        }
    }

    internal sealed class ApiConfigRecognitionResult
    {
        public string BaseUrl { get; set; }
        public string ApiKey { get; set; }
        public List<string> Models { get; set; }
        public string SuggestedName { get; set; }
        public List<string> Warnings { get; private set; }

        public ApiConfigRecognitionResult()
        {
            BaseUrl = string.Empty;
            ApiKey = string.Empty;
            SuggestedName = string.Empty;
            Models = new List<string>();
            Warnings = new List<string>();
        }

        public AiEndpointConfig ToEndpoint(int priority)
        {
            return new AiEndpointConfig
            {
                Name = string.IsNullOrWhiteSpace(SuggestedName) ? "识别接口" : SuggestedName,
                Type = "OpenAI兼容",
                BaseUrl = BaseUrl ?? string.Empty,
                ApiKey = ApiKey ?? string.Empty,
                Model = Models.FirstOrDefault() ?? string.Empty,
                Enabled = true,
                Priority = priority <= 0 ? 1 : priority,
                Weight = 1,
                TimeoutSeconds = 60,
                RetryCount = 0,
                LastStatus = "未测试"
            };
        }

        public string BuildSummary()
        {
            var keyStatus = string.IsNullOrWhiteSpace(ApiKey) ? "未识别" : MaskKey(ApiKey);
            var modelText = Models.Count < 1 ? "未识别" : string.Join("、", Models.Take(6));
            if (Models.Count > 6) modelText += " 等 " + Models.Count + " 个";
            var summary = "自动识别结果：BaseUrl=" + (string.IsNullOrWhiteSpace(BaseUrl) ? "未识别" : BaseUrl)
                + "；ApiKey=" + keyStatus + "；Model=" + modelText + "。";
            if (Warnings.Count > 0) summary += " 提示：" + string.Join("；", Warnings);
            return summary;
        }

        private static string MaskKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "未识别";
            if (key.Length <= 10) return "******";
            return key.Substring(0, Math.Min(6, key.Length)) + "..." + key.Substring(key.Length - 4);
        }
    }

    internal static class ApiConfigRecognizer
    {
        private static readonly string[] BaseUrlNames = { "baseurl", "base_url", "api_base", "apibase", "endpoint", "endpointurl" };
        private static readonly string[] ApiKeyNames = { "apikey", "api_key", "access_token", "accesstoken", "token" };

        public static ApiConfigRecognitionResult Recognize(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) throw new Exception("粘贴内容为空。请粘贴包含 BaseUrl、ApiKey 和 Model 的配置文本。");

            var text = RemoveCodeFence(raw);
            var result = new ApiConfigRecognitionResult();

            TryReadStrictJson(text, result);
            ReadLooseText(text, result);
            NormalizeResult(result);

            if (string.IsNullOrWhiteSpace(result.BaseUrl)) result.Warnings.Add("未识别到 BaseUrl，请在确认窗口补充");
            if (string.IsNullOrWhiteSpace(result.ApiKey)) result.Warnings.Add("未识别到 ApiKey，请在确认窗口补充");
            if (result.Models.Count < 1) result.Warnings.Add("未识别到 Model，请在确认窗口补充");

            return result;
        }

        private static string RemoveCodeFence(string text)
        {
            text = (text ?? string.Empty).Trim();
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var firstLine = text.IndexOf('\n');
                if (firstLine >= 0) text = text.Substring(firstLine + 1);
                var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (lastFence >= 0) text = text.Substring(0, lastFence);
            }
            return text.Trim();
        }

        private static void TryReadStrictJson(string text, ApiConfigRecognitionResult result)
        {
            try
            {
                var token = JToken.Parse(text);
                VisitToken(token, result);
            }
            catch
            {
            }
        }

        private static void VisitToken(JToken token, ApiConfigRecognitionResult result)
        {
            var obj = token as JObject;
            if (obj != null)
            {
                foreach (var property in obj.Properties())
                {
                    var normalizedName = NormalizeName(property.Name);
                    if (BaseUrlNames.Contains(normalizedName) && string.IsNullOrWhiteSpace(result.BaseUrl))
                    {
                        result.BaseUrl = ScalarText(property.Value);
                    }
                    else if (ApiKeyNames.Contains(normalizedName) && string.IsNullOrWhiteSpace(result.ApiKey))
                    {
                        result.ApiKey = ScalarText(property.Value);
                    }
                    else if ((normalizedName == "model" || normalizedName == "modelname") && property.Value.Type == JTokenType.String)
                    {
                        AddModel(result, property.Value.ToString());
                    }
                    else if (normalizedName == "models")
                    {
                        var modelObject = property.Value as JObject;
                        if (modelObject != null)
                        {
                            foreach (var modelProperty in modelObject.Properties()) AddModel(result, modelProperty.Name);
                        }
                        var modelArray = property.Value as JArray;
                        if (modelArray != null)
                        {
                            foreach (var modelToken in modelArray) AddModel(result, ScalarText(modelToken));
                        }
                    }
                    VisitToken(property.Value, result);
                }
                return;
            }

            var array = token as JArray;
            if (array != null)
            {
                foreach (var child in array) VisitToken(child, result);
            }
        }

        private static string ScalarText(JToken token)
        {
            if (token == null) return string.Empty;
            if (token.Type == JTokenType.String || token.Type == JTokenType.Integer || token.Type == JTokenType.Float) return token.ToString();
            return string.Empty;
        }

        private static string NormalizeName(string value)
        {
            return (value ?? string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
        }

        private static void ReadLooseText(string text, ApiConfigRecognitionResult result)
        {
            if (string.IsNullOrWhiteSpace(result.BaseUrl))
            {
                result.BaseUrl = FirstGroup(text, @"(?ix)(?:[""']?(?:baseurl|base_url|api_base|apibase|endpoint|endpointurl)[""']?\s*[:=]\s*[""']?)(?<value>https?://[^\s""',}\]]+)");
            }

            if (string.IsNullOrWhiteSpace(result.BaseUrl))
            {
                var urls = Regex.Matches(text, @"(?i)https?://[a-z0-9][a-z0-9.\-]+(?::\d+)?(?:/[a-z0-9._~:/?#\[\]@!$&'()*+,;=%\-]*)?")
                    .Cast<Match>()
                    .Select(m => CleanValue(m.Value))
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                result.BaseUrl = urls.FirstOrDefault(u => u.IndexOf("/v1", StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? urls.FirstOrDefault(u => u.IndexOf("api", StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? urls.FirstOrDefault()
                    ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(result.ApiKey))
            {
                result.ApiKey = FirstGroup(text, @"(?ix)(?:[""']?(?:apikey|api_key|access_token|accesstoken|token)[""']?\s*[:=]\s*[""']?)(?<value>sk-[a-z0-9_.\-]{12,}|[a-z0-9_.\-]{24,})");
            }
            if (string.IsNullOrWhiteSpace(result.ApiKey))
            {
                var keyMatch = Regex.Match(text, @"(?i)\bsk-[a-z0-9_.\-]{12,}\b");
                if (keyMatch.Success) result.ApiKey = keyMatch.Value;
            }

            foreach (Match match in Regex.Matches(text, @"(?ix)[""']?(?:model|modelname)[""']?\s*[:=]\s*[""'](?<value>[a-z0-9][a-z0-9._:/\-]{1,100})[""']"))
            {
                AddModel(result, match.Groups["value"].Value);
            }

            foreach (Match match in Regex.Matches(text, @"(?ix)[""'](?<value>(?:gpt|o[1-9]|claude|gemini|deepseek|qwen|glm|moonshot|yi)[a-z0-9._:/\-]*)[""']\s*:"))
            {
                AddModel(result, match.Groups["value"].Value);
            }
        }

        private static string FirstGroup(string text, string pattern)
        {
            var match = Regex.Match(text ?? string.Empty, pattern);
            return match.Success ? CleanValue(match.Groups["value"].Value) : string.Empty;
        }

        private static string CleanValue(string value)
        {
            return (value ?? string.Empty).Trim().Trim('"', '\'', ',', ';', '}', ']', ')');
        }

        private static void AddModel(ApiConfigRecognitionResult result, string model)
        {
            model = CleanValue(model);
            if (string.IsNullOrWhiteSpace(model) || model.Length > 120) return;
            if (!result.Models.Contains(model, StringComparer.OrdinalIgnoreCase)) result.Models.Add(model);
        }

        private static void NormalizeResult(ApiConfigRecognitionResult result)
        {
            result.BaseUrl = CleanValue(result.BaseUrl).TrimEnd('/');
            const string chatSuffix = "/chat/completions";
            if (result.BaseUrl.EndsWith(chatSuffix, StringComparison.OrdinalIgnoreCase))
            {
                result.BaseUrl = result.BaseUrl.Substring(0, result.BaseUrl.Length - chatSuffix.Length).TrimEnd('/');
            }
            result.ApiKey = CleanValue(result.ApiKey);
            result.Models = result.Models
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Uri uri;
            if (Uri.TryCreate(result.BaseUrl, UriKind.Absolute, out uri))
            {
                result.SuggestedName = uri.Host;
            }
            if (string.IsNullOrWhiteSpace(result.SuggestedName)) result.SuggestedName = "识别接口";
        }
    }

    internal sealed class ApiConfigPasteWindow : Window
    {
        private readonly TextBox _rawText;
        public string RawText { get { return _rawText.Text; } }

        public ApiConfigPasteWindow()
        {
            Title = "粘贴并识别 API 配置";
            Width = 760;
            Height = 560;
            MinWidth = 620;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 252));

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = "粘贴 JSON、残缺 JSON、代码片段或包含 BaseUrl / ApiKey / Model 的文本",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55)),
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(title, 0);
            root.Children.Add(title);

            _rawText = new TextBox
            {
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Padding = new Thickness(10)
            };
            Grid.SetRow(_rawText, 1);
            root.Children.Add(_rawText);

            var hint = new TextBlock
            {
                Text = "识别过程不会调用网络，也不会把粘贴内容写入日志。确认添加前可修改所有字段。",
                Foreground = new SolidColorBrush(Color.FromRgb(75, 85, 99)),
                Margin = new Thickness(0, 8, 0, 8),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(hint, 2);
            root.Children.Add(hint);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "取消", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var recognize = new Button { Content = "识别配置", MinWidth = 110, IsDefault = true };
            recognize.Click += delegate
            {
                if (string.IsNullOrWhiteSpace(_rawText.Text))
                {
                    MessageBox.Show(this, "请先粘贴配置文本。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                DialogResult = true;
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(recognize);
            Grid.SetRow(buttons, 3);
            root.Children.Add(buttons);

            Content = root;
            Loaded += delegate
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        var clipboardText = Clipboard.GetText();
                        if (!string.IsNullOrWhiteSpace(clipboardText)) _rawText.Text = clipboardText;
                    }
                }
                catch
                {
                }
                _rawText.Focus();
                _rawText.CaretIndex = _rawText.Text.Length;
            };
        }
    }

    internal sealed class AiEndpointEditorWindow : Window
    {
        private readonly TextBox _name;
        private readonly ComboBox _type;
        private readonly TextBox _baseUrl;
        private readonly TextBox _apiKey;
        private readonly ComboBox _model;
        private readonly TextBox _priority;
        private readonly TextBox _timeout;
        private readonly TextBox _retry;
        private readonly CheckBox _enabled;
        private readonly TextBox _systemPrompt;
        private readonly TextBox _testResult;
        private readonly AiEndpointConfig _source;

        public AiEndpointConfig Result { get; private set; }

        public AiEndpointEditorWindow(AiEndpointConfig source, IEnumerable<string> models, string summary)
        {
            _source = source == null ? new AiEndpointConfig() : source.Clone();
            Title = "确认 API 接口信息";
            Width = 700;
            Height = 690;
            MinWidth = 620;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 252));

            var outer = new Grid { Margin = new Thickness(16) };
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var summaryBox = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(summary) ? "请确认并修改接口信息。" : summary,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 64, 175)),
                Background = new SolidColorBrush(Color.FromRgb(234, 245, 255)),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(summaryBox, 0);
            outer.Children.Add(summaryBox);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var form = new Grid { Margin = new Thickness(2) };
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });

            _name = NewTextBox(_source.Name);
            _type = new ComboBox { IsEditable = true, Height = 28, Margin = new Thickness(0, 3, 8, 3) };
            _type.Items.Add("OpenAI兼容");
            _type.Items.Add("OpenAI官方");
            _type.Text = string.IsNullOrWhiteSpace(_source.Type) ? "OpenAI兼容" : _source.Type;
            AddField(form, 0, "名称", _name, 0, 1);
            AddField(form, 0, "类型", _type, 2, 1);

            _baseUrl = NewTextBox(_source.BaseUrl);
            _baseUrl.FontFamily = new FontFamily("Consolas");
            AddField(form, 1, "BaseUrl", _baseUrl, 0, 3);

            _apiKey = NewTextBox(_source.ApiKey);
            _apiKey.FontFamily = new FontFamily("Consolas");
            _apiKey.ToolTip = "密钥只保存在本机 data\\params.db；列表中只显示脱敏值。";
            AddField(form, 2, "ApiKey", _apiKey, 0, 3);

            _model = new ComboBox { IsEditable = true, Height = 28, Margin = new Thickness(0, 3, 8, 3) };
            foreach (var modelName in (models ?? new string[0]).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _model.Items.Add(modelName);
            }
            _model.Text = _source.Model ?? string.Empty;
            AddField(form, 3, "Model", _model, 0, 3);

            _priority = NewTextBox((_source.Priority <= 0 ? 1 : _source.Priority).ToString());
            _timeout = NewTextBox((_source.TimeoutSeconds <= 0 ? 60 : _source.TimeoutSeconds).ToString());
            AddField(form, 4, "优先级", _priority, 0, 1);
            AddField(form, 4, "超时秒", _timeout, 2, 1);

            _retry = NewTextBox(Math.Max(0, _source.RetryCount).ToString());
            _enabled = new CheckBox { Content = "启用此接口", IsChecked = _source.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 8, 3) };
            AddField(form, 5, "失败重试", _retry, 0, 1);
            EnsureRows(form, 6);
            Grid.SetRow(_enabled, 5);
            Grid.SetColumn(_enabled, 2);
            Grid.SetColumnSpan(_enabled, 2);
            form.Children.Add(_enabled);

            _systemPrompt = NewTextBox(_source.SystemPrompt);
            _systemPrompt.AcceptsReturn = true;
            _systemPrompt.TextWrapping = TextWrapping.Wrap;
            _systemPrompt.Height = 100;
            _systemPrompt.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            AddField(form, 6, "SystemPrompt", _systemPrompt, 0, 3);

            _testResult = NewTextBox("可先点击下方【测试当前填写】。测试会真实发送一条随机回显消息，并验证模型响应。");
            _testResult.IsReadOnly = true;
            _testResult.TextWrapping = TextWrapping.Wrap;
            _testResult.Height = 90;
            _testResult.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _testResult.Background = new SolidColorBrush(Color.FromRgb(234, 245, 255));
            AddField(form, 7, "测试结果", _testResult, 0, 3);

            scroll.Content = form;
            Grid.SetRow(scroll, 1);
            outer.Children.Add(scroll);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var test = new Button { Content = "测试当前填写", MinWidth = 120, Margin = new Thickness(0, 0, 16, 0) };
            var cancel = new Button { Content = "取消", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var ok = new Button { Content = "确认添加 / 保存", MinWidth = 130, IsDefault = true };
            test.Click += async delegate
            {
                AiEndpointConfig temp;
                string error;
                if (!TryBuildResult(out temp, out error))
                {
                    _testResult.Text = error;
                    return;
                }
                test.IsEnabled = false;
                _testResult.Text = "正在进行 HTTP 通信、鉴权和模型收发测试...";
                try
                {
                    var testResult = await Task.Run(() => AiEndpointTester.Test(temp));
                    _testResult.Text = testResult.DisplayText;
                }
                finally
                {
                    test.IsEnabled = true;
                }
            };
            ok.Click += delegate
            {
                AiEndpointConfig endpoint;
                string error;
                if (!TryBuildResult(out endpoint, out error))
                {
                    MessageBox.Show(this, error, "配置不完整", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Result = endpoint;
                DialogResult = true;
            };
            buttons.Children.Add(test);
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            Grid.SetRow(buttons, 2);
            outer.Children.Add(buttons);

            Content = outer;
        }

        private static TextBox NewTextBox(string text)
        {
            return new TextBox { Text = text ?? string.Empty, Height = 28, Margin = new Thickness(0, 3, 8, 3), Padding = new Thickness(5, 2, 5, 2) };
        }

        private static void EnsureRows(Grid grid, int count)
        {
            while (grid.RowDefinitions.Count < count)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
        }

        private static void AddField(Grid grid, int row, string label, Control control, int labelColumn, int controlColumnSpan)
        {
            EnsureRows(grid, row + 1);
            var labelBlock = new TextBlock
            {
                Text = label + "：",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(55, 65, 81)),
                Margin = new Thickness(0, 3, 6, 3)
            };
            Grid.SetRow(labelBlock, row);
            Grid.SetColumn(labelBlock, labelColumn);
            grid.Children.Add(labelBlock);

            Grid.SetRow(control, row);
            Grid.SetColumn(control, labelColumn + 1);
            Grid.SetColumnSpan(control, controlColumnSpan);
            grid.Children.Add(control);
        }

        private bool TryBuildResult(out AiEndpointConfig endpoint, out string error)
        {
            endpoint = null;
            error = string.Empty;
            var baseUrl = (_baseUrl.Text ?? string.Empty).Trim().TrimEnd('/');
            var apiKey = (_apiKey.Text ?? string.Empty).Trim();
            var model = (_model.Text ?? string.Empty).Trim();
            var name = (_name.Text ?? string.Empty).Trim();

            Uri uri;
            if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                error = "BaseUrl 无效，必须是以 http:// 或 https:// 开头的完整地址，例如 https://example.com/v1。";
                return false;
            }
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                error = "ApiKey 不能为空。";
                return false;
            }
            if (string.IsNullOrWhiteSpace(model))
            {
                error = "Model 不能为空。";
                return false;
            }

            int priority;
            int timeout;
            int retry;
            if (!int.TryParse(_priority.Text, out priority) || priority < 1) priority = 1;
            if (!int.TryParse(_timeout.Text, out timeout) || timeout < 5) timeout = 60;
            timeout = Math.Min(timeout, 300);
            if (!int.TryParse(_retry.Text, out retry) || retry < 0) retry = 0;
            retry = Math.Min(retry, 5);

            endpoint = _source.Clone();
            endpoint.Name = string.IsNullOrWhiteSpace(name) ? uri.Host : name;
            endpoint.Type = string.IsNullOrWhiteSpace(_type.Text) ? "OpenAI兼容" : _type.Text.Trim();
            endpoint.BaseUrl = baseUrl;
            endpoint.ApiKey = apiKey;
            endpoint.Model = model;
            endpoint.Priority = priority;
            endpoint.TimeoutSeconds = timeout;
            endpoint.RetryCount = retry;
            endpoint.Weight = endpoint.Weight <= 0 ? 1 : endpoint.Weight;
            endpoint.Enabled = _enabled.IsChecked == true;
            endpoint.SystemPrompt = _systemPrompt.Text ?? string.Empty;
            return true;
        }
    }

    internal sealed class ApiEndpointTestResult
    {
        public bool Success { get; set; }
        public bool RoundTripVerified { get; set; }
        public long LatencyMs { get; set; }
        public string ShortStatus { get; set; }
        public string DisplayText { get; set; }
    }

    internal static class AiEndpointTester
    {
        public static ApiEndpointTestResult Test(AiEndpointConfig endpoint)
        {
            var result = new ApiEndpointTestResult { Success = false, RoundTripVerified = false, ShortStatus = "未完成", DisplayText = "测试未完成。" };
            if (endpoint == null)
            {
                result.ShortStatus = "配置为空";
                result.DisplayText = "失败：接口配置为空。";
                return result;
            }

            string validationError;
            var url = BuildChatUrl(endpoint.BaseUrl, out validationError);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                result.ShortStatus = validationError;
                result.DisplayText = "失败：" + validationError;
                return result;
            }
            if (string.IsNullOrWhiteSpace(endpoint.ApiKey))
            {
                result.ShortStatus = "ApiKey为空";
                result.DisplayText = "失败：ApiKey 为空。";
                return result;
            }
            if (string.IsNullOrWhiteSpace(endpoint.Model))
            {
                result.ShortStatus = "Model为空";
                result.DisplayText = "失败：Model 为空。";
                return result;
            }

            var marker = "QN_API_TEST_" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
            var payload = new JObject
            {
                ["model"] = endpoint.Model.Trim(),
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "system", ["content"] = "你正在执行API收发测试，只按用户要求原样回复测试标记。" },
                    new JObject { ["role"] = "user", ["content"] = "请只回复以下文本，不要添加任何其它字符：" + marker }
                },
                ["temperature"] = 0,
                ["max_tokens"] = 50,
                ["stream"] = false
            };

            var stopwatch = Stopwatch.StartNew();
            try
            {
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(endpoint.TimeoutSeconds <= 0 ? 60 : Math.Max(5, endpoint.TimeoutSeconds));
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey.Trim());
                    http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
                    http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "qianniu-bot-api-tester/9.5.2");

                    using (var content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json"))
                    {
                        var response = http.PostAsync(url, content).GetAwaiter().GetResult();
                        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        stopwatch.Stop();
                        result.LatencyMs = stopwatch.ElapsedMilliseconds;

                        if (!response.IsSuccessStatusCode)
                        {
                            result.ShortStatus = ClassifyHttpStatus(response.StatusCode);
                            result.DisplayText = "失败：" + result.ShortStatus + "。HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase
                                + "；接口返回：" + SafeText(body) + "；耗时 " + result.LatencyMs + "ms。";
                            return result;
                        }

                        string answer;
                        string parseError;
                        if (!TryExtractAnswer(body, out answer, out parseError))
                        {
                            result.ShortStatus = "HTTP成功但响应格式不兼容";
                            result.DisplayText = "部分失败：HTTP 通信和鉴权成功，但" + parseError + "；原始返回：" + SafeText(body)
                                + "；耗时 " + result.LatencyMs + "ms。";
                            return result;
                        }

                        result.Success = true;
                        result.RoundTripVerified = answer.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
                        result.ShortStatus = result.RoundTripVerified ? "通信、鉴权和模型收发均正常" : "通信和模型响应正常，回显未严格匹配";
                        result.DisplayText = result.RoundTripVerified
                            ? "成功：HTTP 通信正常；ApiKey 鉴权通过；测试消息发送成功；模型响应解析成功；随机回显标记验证通过。模型=" + endpoint.Model + "，耗时 " + result.LatencyMs + "ms。"
                            : "可用但需注意：HTTP 通信、鉴权、消息发送和模型响应均成功，但模型没有严格回显测试标记。实际回复：" + SafeText(answer) + "；耗时 " + result.LatencyMs + "ms。";
                        return result;
                    }
                }
            }
            catch (TaskCanceledException)
            {
                stopwatch.Stop();
                result.LatencyMs = stopwatch.ElapsedMilliseconds;
                result.ShortStatus = "请求超时";
                result.DisplayText = "失败：请求超时。可能是中转站拥堵、上游模型排队或网络不可达；耗时 " + result.LatencyMs + "ms。";
                return result;
            }
            catch (HttpRequestException ex)
            {
                stopwatch.Stop();
                result.LatencyMs = stopwatch.ElapsedMilliseconds;
                result.ShortStatus = "网络/TLS连接失败";
                result.DisplayText = "失败：网络、DNS、代理或 TLS 连接异常：" + SafeText(ex.Message) + "；耗时 " + result.LatencyMs + "ms。";
                return result;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.LatencyMs = stopwatch.ElapsedMilliseconds;
                result.ShortStatus = "测试异常";
                result.DisplayText = "失败：测试异常：" + SafeText(ex.Message) + "；耗时 " + result.LatencyMs + "ms。";
                return result;
            }
        }

        private static string BuildChatUrl(string baseUrl, out string error)
        {
            error = string.Empty;
            baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            Uri uri;
            if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                error = "BaseUrl无效";
                return string.Empty;
            }
            if (baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return baseUrl;
            return baseUrl + "/chat/completions";
        }

        private static string ClassifyHttpStatus(HttpStatusCode statusCode)
        {
            var code = (int)statusCode;
            if (code == 400) return "请求格式或模型参数被接口拒绝";
            if (code == 401) return "ApiKey无效、过期或鉴权方式不兼容";
            if (code == 403) return "接口拒绝访问，可能是权限、IP白名单或地区限制";
            if (code == 404) return "接口路径不存在，请检查BaseUrl是否需要/v1";
            if (code == 408) return "服务端请求超时";
            if (code == 409) return "服务端状态冲突";
            if (code == 429) return "中转站限流、余额不足或上游繁忙";
            if (code >= 500 && code <= 504) return "中转站或其上游服务暂时不可用（服务端故障）";
            return "接口返回HTTP错误";
        }

        private static bool TryExtractAnswer(string body, out string answer, out string error)
        {
            answer = string.Empty;
            error = string.Empty;
            try
            {
                var json = JObject.Parse(body ?? string.Empty);
                var content = json["choices"]?[0]?["message"]?["content"];
                if (content == null)
                {
                    error = "未找到 choices[0].message.content";
                    return false;
                }
                if (content.Type == JTokenType.String)
                {
                    answer = content.ToString().Trim();
                }
                else if (content.Type == JTokenType.Array)
                {
                    var parts = new List<string>();
                    foreach (var item in (JArray)content)
                    {
                        var text = item["text"] ?? item["content"];
                        if (text != null) parts.Add(text.ToString());
                    }
                    answer = string.Join("", parts).Trim();
                }
                else
                {
                    answer = content.ToString(Formatting.None).Trim();
                }
                if (string.IsNullOrWhiteSpace(answer))
                {
                    error = "模型响应内容为空";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "响应JSON解析失败：" + SafeText(ex.Message);
                return false;
            }
        }

        private static string SafeText(string value)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= 400 ? value : value.Substring(0, 400) + "...";
        }
    }
}
