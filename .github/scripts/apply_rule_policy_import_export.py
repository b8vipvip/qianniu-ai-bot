from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if old not in text:
        raise RuntimeError(f"patch marker not found: {label}")
    return text.replace(old, new, 1)


store_path = Path("src/Bot/Knowledge/StorePromptProfileUi.cs")
policy_path = Path("src/Bot/Knowledge/KnowledgePolicyProfileUi.cs")

store = store_path.read_text(encoding="utf-8-sig")
store = replace_once(
    store,
    "using Bot.ChromeNs;\nusing System;\nusing System.Linq;",
    "using Bot.ChromeNs;\nusing Microsoft.Win32;\nusing Newtonsoft.Json;\nusing Newtonsoft.Json.Linq;\nusing System;\nusing System.IO;\nusing System.Linq;\nusing System.Text;",
    "store imports",
)

store_buttons_marker = '''            _generate = new Button
            {
                Content = "AI生成结构化规则",
                Width = 150,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0)
            };'''
store_buttons_replacement = '''            var import = new Button
            {
                Content = "导入",
                Width = 72,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "从JSON文件导入店铺核心规则和场景规则卡；导入前自动备份当前配置。"
            };
            import.Click += (s, e) => ImportProfile();
            buttons.Children.Add(import);

            var export = new Button
            {
                Content = "导出",
                Width = 72,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "把当前编辑器中的原始资料、核心规则和场景规则卡导出为可迁移JSON。"
            };
            export.Click += (s, e) => ExportProfile();
            buttons.Children.Add(export);

            _generate = new Button
            {
                Content = "AI生成结构化规则",
                Width = 150,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0)
            };'''
store = replace_once(store, store_buttons_marker, store_buttons_replacement, "store buttons")

store_method_marker = '''        private void SaveAndClose()
        {
            try
            {
                var rules = StorePromptProfileService.ParseRulesJson(_rules.Text);
                StorePromptProfileService.SaveStructured(_raw.Text, _core.Text, rules);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存店铺规则失败：" + ex.Message, "店铺规则中心", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }'''
store_method_replacement = '''        private JObject BuildExportObject()
        {
            var rules = StorePromptProfileService.ParseRulesJson(_rules.Text);
            return new JObject
            {
                ["schema"] = "qianniu-ai-bot.store-rules",
                ["version"] = 1,
                ["exportedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["profile"] = new JObject
                {
                    ["schemaVersion"] = 2,
                    ["rawInput"] = _raw.Text ?? string.Empty,
                    ["corePrompt"] = _core.Text ?? string.Empty,
                    ["rules"] = JArray.FromObject(rules)
                }
            };
        }

        private void ExportProfile()
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Title = "导出店铺规则",
                    Filter = "JSON文件 (*.json)|*.json",
                    FileName = "qianniu-store-rules-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json",
                    AddExtension = true,
                    DefaultExt = ".json"
                };
                if (dialog.ShowDialog(this) != true) return;
                File.WriteAllText(
                    dialog.FileName,
                    BuildExportObject().ToString(Formatting.Indented),
                    new UTF8Encoding(false));
                _status.Text = "已导出：" + Path.GetFileName(dialog.FileName);
                _status.Foreground = Brushes.SeaGreen;
                MessageBox.Show("店铺规则已导出。", "店铺规则中心", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("导出店铺规则失败：" + ex.Message, "店铺规则中心", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string BackupCurrentProfile()
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "data",
                "backups");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory,
                "store-rules-before-import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
            File.WriteAllText(path, BuildExportObject().ToString(Formatting.Indented), new UTF8Encoding(false));
            return path;
        }

        private void ImportProfile()
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "导入店铺规则",
                    Filter = "JSON文件 (*.json)|*.json",
                    Multiselect = false
                };
                if (dialog.ShowDialog(this) != true) return;

                var root = JObject.Parse(File.ReadAllText(dialog.FileName, Encoding.UTF8));
                var schema = Convert.ToString(root["schema"]);
                if (!string.IsNullOrWhiteSpace(schema)
                    && !string.Equals(schema, "qianniu-ai-bot.store-rules", StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception("文件类型不匹配：" + schema);
                }

                var profile = root["profile"] as JObject ?? root;
                var raw = Convert.ToString(profile["rawInput"] ?? profile["RawInput"]);
                var core = Convert.ToString(
                    profile["corePrompt"] ?? profile["CorePrompt"]
                    ?? profile["standardPrompt"] ?? profile["StandardPrompt"]);
                var rulesToken = profile["rules"] ?? profile["Rules"];
                if (rulesToken == null) throw new Exception("文件中没有 rules 场景规则数组。");
                var rules = StorePromptProfileService.ParseRulesJson(rulesToken.ToString(Formatting.None));

                var confirm = MessageBox.Show(
                    "将导入核心规则和 " + rules.Count + " 条场景规则，并覆盖当前店铺规则配置。导入前会自动备份当前配置。是否继续？",
                    "确认导入店铺规则",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;

                var backup = BackupCurrentProfile();
                StorePromptProfileService.SaveStructured(raw, core, rules);
                LoadProfile();
                _status.Text = "导入成功 · 已自动备份原配置";
                _status.Foreground = Brushes.SeaGreen;
                MessageBox.Show(
                    "导入成功：" + rules.Count + " 条场景规则。\n原配置备份：" + backup,
                    "店铺规则中心",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (JsonException ex)
            {
                MessageBox.Show("导入文件不是有效JSON：" + ex.Message, "店铺规则中心", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show("导入店铺规则失败：" + ex.Message, "店铺规则中心", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveAndClose()
        {
            try
            {
                var rules = StorePromptProfileService.ParseRulesJson(_rules.Text);
                StorePromptProfileService.SaveStructured(_raw.Text, _core.Text, rules);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存店铺规则失败：" + ex.Message, "店铺规则中心", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }'''
store = replace_once(store, store_method_marker, store_method_replacement, "store methods")
store_path.write_text(store, encoding="utf-8-sig")

policy = policy_path.read_text(encoding="utf-8-sig")
policy = replace_once(
    policy,
    "using Bot.ChromeNs;\nusing System;",
    "using Bot.ChromeNs;\nusing Microsoft.Win32;\nusing Newtonsoft.Json;\nusing Newtonsoft.Json.Linq;\nusing System;\nusing System.IO;",
    "policy imports",
)
policy = replace_once(
    policy,
    "using System.Runtime.CompilerServices;\nusing System.Text.RegularExpressions;",
    "using System.Runtime.CompilerServices;\nusing System.Text;\nusing System.Text.RegularExpressions;",
    "policy encoding import",
)

policy_buttons_marker = '''            var clear = new Button
            {
                Content = "清空",
                Width = 62,
                Height = 28,
                Margin = new Thickness(8, 0, 0, 0)
            };
            DockPanel.SetDock(clear, Dock.Right);
            searchRow.Children.Add(clear);'''
policy_buttons_replacement = '''            var clear = new Button
            {
                Content = "清空",
                Width = 62,
                Height = 28,
                Margin = new Thickness(8, 0, 0, 0)
            };
            DockPanel.SetDock(clear, Dock.Right);
            searchRow.Children.Add(clear);

            var export = new Button
            {
                Content = "导出",
                Width = 62,
                Height = 28,
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = "导出全部知识策略配置；可靠度统计不写入迁移文件。"
            };
            export.Click += (s, e) => ExportPolicies();
            DockPanel.SetDock(export, Dock.Right);
            searchRow.Children.Add(export);

            var import = new Button
            {
                Content = "导入",
                Width = 62,
                Height = 28,
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = "按知识ID或问题文本合并导入策略；不会删除现有策略，导入前自动备份。"
            };
            import.Click += (s, e) => ImportPolicies();
            DockPanel.SetDock(import, Dock.Right);
            searchRow.Children.Add(import);'''
policy = replace_once(policy, policy_buttons_marker, policy_buttons_replacement, "policy buttons")

policy_methods_marker = '''        private void SaveSelected()
        {
            var profile = _grid.SelectedItem as KnowledgePolicyProfile;
            if (profile == null) return;
            var entry = FindKnowledge(profile);
            if (entry == null)
            {
                MessageBox.Show("未找到对应知识条目，请刷新知识库后重试。", "知识策略", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedMode = _mode.SelectedItem as ComboBoxItem;
            profile.AnswerMode = selectedMode == null ? KnowledgeAnswerModes.Auto : Convert.ToString(selectedMode.Tag);
            profile.Intent = _intent.Text;
            profile.Entities = _entities.Text;
            profile.ApplyWhen = _applyWhen.Text;
            profile.DoNotApplyWhen = _doNotApplyWhen.Text;
            profile.RequiredContext = _requiredContext.Text;
            KnowledgePolicyProfileService.SaveProfile(entry, profile);

            var selectedId = profile.KnowledgeId;
            _profiles = KnowledgePolicyProfileService.GetProfilesForKnowledge(_knowledge);
            ApplySearch();
            var current = (_grid.ItemsSource as IEnumerable<KnowledgePolicyProfile>)
                .FirstOrDefault(x => x.KnowledgeId == selectedId);
            if (current != null) _grid.SelectedItem = current;
            LoadSelected();
        }'''
policy_methods_replacement = '''        private JObject BuildPolicyExportObject()
        {
            var profiles = (_profiles ?? new List<KnowledgePolicyProfile>())
                .Where(x => x != null)
                .Select(x => new JObject
                {
                    ["knowledgeId"] = x.KnowledgeId ?? string.Empty,
                    ["questionSnapshot"] = x.QuestionSnapshot ?? string.Empty,
                    ["intent"] = x.Intent ?? string.Empty,
                    ["entities"] = x.Entities ?? string.Empty,
                    ["applyWhen"] = x.ApplyWhen ?? string.Empty,
                    ["doNotApplyWhen"] = x.DoNotApplyWhen ?? string.Empty,
                    ["requiredContext"] = x.RequiredContext ?? string.Empty,
                    ["answerMode"] = KnowledgeAnswerModes.Normalize(x.AnswerMode),
                    ["confidence"] = x.Confidence <= 0 ? 0.80 : x.Confidence
                });
            return new JObject
            {
                ["schema"] = "qianniu-ai-bot.knowledge-policies",
                ["version"] = 1,
                ["exportedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["profiles"] = new JArray(profiles)
            };
        }

        private void ExportPolicies()
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Title = "导出知识策略",
                    Filter = "JSON文件 (*.json)|*.json",
                    FileName = "qianniu-knowledge-policies-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json",
                    AddExtension = true,
                    DefaultExt = ".json"
                };
                if (dialog.ShowDialog(this) != true) return;
                File.WriteAllText(
                    dialog.FileName,
                    BuildPolicyExportObject().ToString(Formatting.Indented),
                    new UTF8Encoding(false));
                MessageBox.Show(
                    "已导出 " + (_profiles == null ? 0 : _profiles.Count) + " 条知识策略。\n可靠度统计属于本机学习数据，未写入迁移文件。",
                    "知识策略",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("导出知识策略失败：" + ex.Message, "知识策略", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string BackupCurrentPolicies()
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "data",
                "backups");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory,
                "knowledge-policies-before-import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
            File.WriteAllText(path, BuildPolicyExportObject().ToString(Formatting.Indented), new UTF8Encoding(false));
            return path;
        }

        private KnowledgeBaseEntry FindKnowledgeForImport(string knowledgeId, string question)
        {
            var byId = _knowledge.FirstOrDefault(x => x != null
                && !string.IsNullOrWhiteSpace(knowledgeId)
                && string.Equals(x.Id ?? string.Empty, knowledgeId, StringComparison.Ordinal));
            if (byId != null) return byId;
            var normalized = KnowledgeAiService.NormalizeQuestion(question);
            if (normalized.Length == 0) return null;
            return _knowledge.FirstOrDefault(x => x != null
                && KnowledgeAiService.NormalizeQuestion(x.Title) == normalized);
        }

        private void ImportPolicies()
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "导入知识策略",
                    Filter = "JSON文件 (*.json)|*.json",
                    Multiselect = false
                };
                if (dialog.ShowDialog(this) != true) return;

                var root = JObject.Parse(File.ReadAllText(dialog.FileName, Encoding.UTF8));
                var schema = Convert.ToString(root["schema"]);
                if (!string.IsNullOrWhiteSpace(schema)
                    && !string.Equals(schema, "qianniu-ai-bot.knowledge-policies", StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception("文件类型不匹配：" + schema);
                }
                var profiles = root["profiles"] as JArray ?? root["Profiles"] as JArray;
                if (profiles == null) throw new Exception("文件中没有 profiles 策略数组。");

                var confirm = MessageBox.Show(
                    "将按知识ID或问题文本合并导入 " + profiles.Count + " 条策略。不会删除现有策略，可靠度学习统计也不会被覆盖。导入前会自动备份。是否继续？",
                    "确认导入知识策略",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;

                var backup = BackupCurrentPolicies();
                var updated = 0;
                var skipped = 0;
                var invalid = 0;
                foreach (var token in profiles)
                {
                    var item = token as JObject;
                    if (item == null)
                    {
                        invalid++;
                        continue;
                    }
                    var knowledgeId = Convert.ToString(item["knowledgeId"] ?? item["KnowledgeId"]);
                    var question = Convert.ToString(item["questionSnapshot"] ?? item["QuestionSnapshot"]);
                    var entry = FindKnowledgeForImport(knowledgeId, question);
                    if (entry == null)
                    {
                        skipped++;
                        continue;
                    }
                    double confidence;
                    if (!double.TryParse(
                        Convert.ToString(item["confidence"] ?? item["Confidence"] ?? 0.80),
                        out confidence)) confidence = 0.80;
                    var imported = new KnowledgePolicyProfile
                    {
                        KnowledgeId = knowledgeId,
                        QuestionSnapshot = question,
                        Intent = Convert.ToString(item["intent"] ?? item["Intent"]),
                        Entities = Convert.ToString(item["entities"] ?? item["Entities"]),
                        ApplyWhen = Convert.ToString(item["applyWhen"] ?? item["ApplyWhen"]),
                        DoNotApplyWhen = Convert.ToString(item["doNotApplyWhen"] ?? item["DoNotApplyWhen"]),
                        RequiredContext = Convert.ToString(item["requiredContext"] ?? item["RequiredContext"]),
                        AnswerMode = Convert.ToString(item["answerMode"] ?? item["AnswerMode"]),
                        Confidence = confidence
                    };
                    KnowledgePolicyProfileService.SaveProfile(entry, imported);
                    updated++;
                }

                _profiles = KnowledgePolicyProfileService.GetProfilesForKnowledge(_knowledge);
                ApplySearch();
                MessageBox.Show(
                    "知识策略导入完成。\n成功更新：" + updated
                    + "\n未找到对应知识：" + skipped
                    + "\n无效记录：" + invalid
                    + "\n原配置备份：" + backup,
                    "知识策略",
                    MessageBoxButton.OK,
                    skipped > 0 || invalid > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (JsonException ex)
            {
                MessageBox.Show("导入文件不是有效JSON：" + ex.Message, "知识策略", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show("导入知识策略失败：" + ex.Message, "知识策略", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveSelected()
        {
            var profile = _grid.SelectedItem as KnowledgePolicyProfile;
            if (profile == null) return;
            var entry = FindKnowledge(profile);
            if (entry == null)
            {
                MessageBox.Show("未找到对应知识条目，请刷新知识库后重试。", "知识策略", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedMode = _mode.SelectedItem as ComboBoxItem;
            profile.AnswerMode = selectedMode == null ? KnowledgeAnswerModes.Auto : Convert.ToString(selectedMode.Tag);
            profile.Intent = _intent.Text;
            profile.Entities = _entities.Text;
            profile.ApplyWhen = _applyWhen.Text;
            profile.DoNotApplyWhen = _doNotApplyWhen.Text;
            profile.RequiredContext = _requiredContext.Text;
            KnowledgePolicyProfileService.SaveProfile(entry, profile);

            var selectedId = profile.KnowledgeId;
            _profiles = KnowledgePolicyProfileService.GetProfilesForKnowledge(_knowledge);
            ApplySearch();
            var current = (_grid.ItemsSource as IEnumerable<KnowledgePolicyProfile>)
                .FirstOrDefault(x => x.KnowledgeId == selectedId);
            if (current != null) _grid.SelectedItem = current;
            LoadSelected();
        }'''
policy = replace_once(policy, policy_methods_marker, policy_methods_replacement, "policy methods")
policy_path.write_text(policy, encoding="utf-8-sig")

Path("tests/test_rule_policy_import_export_static.py").write_text(
    r'''from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
STORE = ROOT / "src" / "Bot" / "Knowledge" / "StorePromptProfileUi.cs"
POLICY = ROOT / "src" / "Bot" / "Knowledge" / "KnowledgePolicyProfileUi.cs"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_store_rule_center_has_versioned_json_import_export():
    source = read(STORE)
    assert 'Content = "导入"' in source
    assert 'Content = "导出"' in source
    assert '"qianniu-ai-bot.store-rules"' in source
    assert 'BuildExportObject().ToString(Formatting.Indented)' in source
    assert 'StorePromptProfileService.SaveStructured(raw, core, rules)' in source


def test_store_import_backs_up_before_overwrite_and_validates_rules():
    source = read(STORE)
    backup = source.index("var backup = BackupCurrentProfile()")
    save = source.index("StorePromptProfileService.SaveStructured(raw, core, rules)", backup)
    assert backup < save
    assert "StorePromptProfileService.ParseRulesJson" in source
    assert "文件类型不匹配" in source


def test_knowledge_policy_export_excludes_learning_telemetry():
    source = read(POLICY)
    block = source[source.index("private JObject BuildPolicyExportObject"):source.index("private void ExportPolicies")]
    assert '"qianniu-ai-bot.knowledge-policies"' in block
    assert '"answerMode"' in block
    assert '"confidence"' in block
    assert "DirectSelectedCount" not in block
    assert "SellerCorrectionCount" not in block
    assert "SellerWithdrawCount" not in block


def test_knowledge_policy_import_is_merge_only_and_preserves_reliability():
    source = read(POLICY)
    assert "FindKnowledgeForImport" in source
    assert "KnowledgePolicyProfileService.SaveProfile(entry, imported)" in source
    assert "不会删除现有策略" in source
    assert "可靠度学习统计也不会被覆盖" in source
    assert "var backup = BackupCurrentPolicies()" in source


def test_import_reports_updated_skipped_and_invalid_counts():
    source = read(POLICY)
    assert '"\\n成功更新：" + updated' in source
    assert '"\\n未找到对应知识：" + skipped' in source
    assert '"\\n无效记录：" + invalid' in source
''',
    encoding="utf-8",
)
