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