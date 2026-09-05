using Bot.ChromeNs;
using Bot.ShopScope;
using BotLib;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot
{
    public partial class App
    {
        private static readonly object RulePolicyImportExportBootstrap =
            Knowledge.RulePolicyImportExportUi.InitializeForApp();
    }
}

namespace Bot.Knowledge
{
    internal static class RulePolicyImportExportUi
    {
        private const string StoreSchema = "qnbot.store-rules";
        private const string PolicySchema = "qnbot.knowledge-policies";
        private const string KnowledgePackageSchema = "qnbot.knowledge-package";
        private const int ExportVersion = 2;

        private static readonly ConditionalWeakTable<Window, object> Attached =
            new ConditionalWeakTable<Window, object>();
        private static readonly object AttachedMarker = new object();
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                EventManager.RegisterClassHandler(
                    typeof(StorePromptProfileWindow),
                    FrameworkElement.LoadedEvent,
                    new RoutedEventHandler(OnStoreWindowLoaded),
                    true);
                EventManager.RegisterClassHandler(
                    typeof(KnowledgePolicyProfileWindow),
                    FrameworkElement.LoadedEvent,
                    new RoutedEventHandler(OnPolicyWindowLoaded),
                    true);
            }
            return new object();
        }

        private static void OnStoreWindowLoaded(object sender, RoutedEventArgs e)
        {
            var window = sender as StorePromptProfileWindow;
            if (window == null || IsAttached(window)) return;

            var save = GetField<Button>(window, "_save");
            var panel = save == null ? null : save.Parent as Panel;
            if (panel == null)
            {
                Log.Info("店铺规则导入导出按钮挂载失败：未找到保存按钮容器");
                return;
            }

            var import = CreateButton(
                "导入",
                72,
                "从JSON导入店铺原始资料、核心规则和场景规则卡；导入前自动备份当前配置。",
                "store-rules-import");
            import.Click += (s, args) => ImportStoreRules(window);

            var export = CreateButton(
                "导出",
                72,
                "把当前编辑器中的店铺规则导出为带版本信息的JSON文件。",
                "store-rules-export");
            export.Click += (s, args) => ExportStoreRules(window);

            var index = panel.Children.IndexOf(save);
            if (index < 0) index = 0;
            panel.Children.Insert(index, import);
            panel.Children.Insert(index + 1, export);
            MarkAttached(window);
        }

        private static void OnPolicyWindowLoaded(object sender, RoutedEventArgs e)
        {
            var window = sender as KnowledgePolicyProfileWindow;
            if (window == null || IsAttached(window)) return;
            if (FindButton(window, "导入全部") != null || FindButton(window, "导出全部") != null)
            {
                MarkAttached(window);
                return;
            }

            var save = FindButton(window, "保存策略");
            var panel = save == null ? null : save.Parent as Panel;
            if (panel == null)
            {
                Log.Info("知识策略导入导出按钮挂载失败：未找到保存按钮容器");
                return;
            }

            var import = CreateButton(
                "导入",
                72,
                "按知识ID或问题文本合并导入策略；不会删除现有策略，也不会覆盖可靠度学习统计。",
                "knowledge-policies-import");
            import.Click += (s, args) => ImportKnowledgePolicies(window);

            var export = CreateButton(
                "导出",
                72,
                "导出全部可编辑知识策略；不导出本机可靠度学习统计。",
                "knowledge-policies-export");
            export.Click += (s, args) => ExportKnowledgePolicies(window);

            var index = panel.Children.IndexOf(save);
            if (index < 0) index = 0;
            panel.Children.Insert(index, import);
            panel.Children.Insert(index + 1, export);
            MarkAttached(window);
        }

        private static Button CreateButton(string text, double width, string tooltip, string tag)
        {
            return new Button
            {
                Content = text,
                Width = width,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = tooltip,
                Tag = tag
            };
        }

        private static bool IsAttached(Window window)
        {
            object ignored;
            return window != null && Attached.TryGetValue(window, out ignored);
        }

        private static void MarkAttached(Window window)
        {
            if (window == null || IsAttached(window)) return;
            try { Attached.Add(window, AttachedMarker); } catch { }
        }

        private static void ExportStoreRules(StorePromptProfileWindow window)
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
                if (dialog.ShowDialog(window) != true) return;

                var payload = BuildStoreExportObject(window);
                WriteJson(dialog.FileName, payload);
                SetStoreStatus(window, "已导出：" + Path.GetFileName(dialog.FileName), Brushes.SeaGreen);
                Log.Info("店铺规则已导出: file=" + Path.GetFileName(dialog.FileName)
                    + ", rules=" + ((JArray)payload["profile"]["rules"]).Count);
                MessageBox.Show("店铺规则已导出。", "店铺规则中心", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("导出店铺规则失败：" + ex.Message, "店铺规则中心", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void ImportStoreRules(StorePromptProfileWindow window)
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "导入店铺规则",
                    Filter = "JSON文件 (*.json)|*.json",
                    Multiselect = false
                };
                if (dialog.ShowDialog(window) != true) return;

                var root = ReadObject(dialog.FileName);
                ValidateSchema(root, StoreSchema);
                var profile = root["profile"] as JObject ?? root;
                var raw = ReadString(profile, "rawInput", "RawInput");
                var core = ReadString(
                    profile,
                    "corePrompt",
                    "CorePrompt",
                    "standardPrompt",
                    "StandardPrompt");
                var rulesToken = profile["rules"] ?? profile["Rules"];
                if (rulesToken == null || rulesToken.Type != JTokenType.Array)
                    throw new Exception("文件中没有有效的 rules 场景规则数组。");

                var rules = StorePromptProfileService.ParseRulesJson(
                    rulesToken.ToString(Formatting.None));
                var confirm = MessageBox.Show(
                    "将导入核心规则和 " + rules.Count + " 条场景规则，并覆盖当前店铺规则配置。"
                    + "\n导入前会自动备份当前配置。是否继续？",
                    "确认导入店铺规则",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;

                var backup = BackupStoreRules(window);
                StorePromptProfileService.SaveStructured(raw, core, rules);
                Invoke(window, "LoadProfile");
                SetStoreStatus(window, "导入成功 · 已自动备份原配置", Brushes.SeaGreen);
                Log.Info("店铺规则已导入: file=" + Path.GetFileName(dialog.FileName)
                    + ", rules=" + rules.Count + ", backup=" + Path.GetFileName(backup));
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

        private static JObject BuildStoreExportObject(StorePromptProfileWindow window)
        {
            var raw = GetField<TextBox>(window, "_raw");
            var core = GetField<TextBox>(window, "_core");
            var rulesEditor = GetField<TextBox>(window, "_rules");
            if (raw == null || core == null || rulesEditor == null)
                throw new Exception("无法读取店铺规则编辑器，请重新打开窗口后重试。");

            var rules = StorePromptProfileService.ParseRulesJson(rulesEditor.Text);
            return new JObject
            {
                ["schema"] = StoreSchema,
                ["version"] = ExportVersion,
                ["exportedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["profile"] = new JObject
                {
                    ["schemaVersion"] = 2,
                    ["rawInput"] = raw.Text ?? string.Empty,
                    ["corePrompt"] = core.Text ?? string.Empty,
                    ["rules"] = JArray.FromObject(rules)
                }
            };
        }

        private static string BackupStoreRules(StorePromptProfileWindow window)
        {
            var path = BuildBackupPath("store-rules-before-import");
            WriteJson(path, BuildStoreExportObject(window));
            return path;
        }

        private static void SetStoreStatus(StorePromptProfileWindow window, string text, Brush brush)
        {
            var status = GetField<TextBlock>(window, "_status");
            if (status == null) return;
            status.Text = text;
            status.Foreground = brush;
        }

        internal static void ExportKnowledgePolicies(KnowledgePolicyProfileWindow window)
        {
            try
            {
                var profiles = GetPolicyProfiles(window);
                var dialog = new SaveFileDialog
                {
                    Title = "导出知识策略",
                    Filter = "JSON文件 (*.json)|*.json",
                    FileName = "qianniu-knowledge-policies-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json",
                    AddExtension = true,
                    DefaultExt = ".json"
                };
                if (dialog.ShowDialog(window) != true) return;

                WriteJson(dialog.FileName, BuildPolicyExportObject(profiles));
                Log.Info("知识策略已导出: file=" + Path.GetFileName(dialog.FileName)
                    + ", profiles=" + profiles.Count);
                MessageBox.Show(
                    "已导出 " + profiles.Count + " 条知识策略。"
                    + "\n已包含回答模式、条件、可靠度及全部学习统计。",
                    "知识策略",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("导出知识策略失败：" + ex.Message, "知识策略", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        internal static void ImportKnowledgePolicies(KnowledgePolicyProfileWindow window)
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "导入知识策略",
                    Filter = "JSON文件 (*.json)|*.json",
                    Multiselect = false
                };
                if (dialog.ShowDialog(window) != true) return;

                var root = ReadObject(dialog.FileName);
                ValidateSchema(root, PolicySchema);
                var profiles = root["profiles"] as JArray ?? root["Profiles"] as JArray;
                if (profiles == null) throw new Exception("文件中没有 profiles 策略数组。");
                if (root["enabled"] != null)
                {
                    bool enabled;
                    var policyShop = GetField<ShopContext>(window, "_shop") ?? ShopSettingsScope.Current;
                    if (policyShop != null && bool.TryParse(Convert.ToString(root["enabled"]), out enabled))
                        KnowledgePolicyProfileService.SetEnabled(policyShop, enabled);
                }

                var confirm = MessageBox.Show(
                    "将按知识ID或问题文本合并导入 " + profiles.Count + " 条策略。"
                    + "\n不会删除现有策略；匹配到的策略将完整恢复配置和可靠度学习统计。"
                    + "\n导入前会自动备份。是否继续？",
                    "确认导入知识策略",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;

                var knowledge = GetKnowledge(window);
                var backup = BackupKnowledgePolicies(window);
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

                    var knowledgeId = ReadString(item, "knowledgeId", "KnowledgeId");
                    var question = ReadString(item, "questionSnapshot", "QuestionSnapshot");
                    var entry = FindKnowledgeForImport(knowledge, knowledgeId, question);
                    if (entry == null)
                    {
                        skipped++;
                        continue;
                    }

                    var imported = new KnowledgePolicyProfile
                    {
                        KnowledgeId = knowledgeId,
                        QuestionSnapshot = question,
                        Intent = ReadString(item, "intent", "Intent"),
                        Entities = ReadString(item, "entities", "Entities"),
                        ApplyWhen = ReadString(item, "applyWhen", "ApplyWhen"),
                        DoNotApplyWhen = ReadString(item, "doNotApplyWhen", "DoNotApplyWhen"),
                        RequiredContext = ReadString(item, "requiredContext", "RequiredContext"),
                        AnswerMode = ReadString(item, "answerMode", "AnswerMode"),
                        Confidence = ReadDouble(item, 0.80, "confidence", "Confidence"),
                        DirectSelectedCount = ReadInt(item, 0, "directSelectedCount", "DirectSelectedCount"),
                        ContextualSelectedCount = ReadInt(item, 0, "contextualSelectedCount", "ContextualSelectedCount"),
                        AcceptedCount = ReadInt(item, 0, "acceptedCount", "AcceptedCount"),
                        SellerCorrectionCount = ReadInt(item, 0, "sellerCorrectionCount", "SellerCorrectionCount"),
                        SellerWithdrawCount = ReadInt(item, 0, "sellerWithdrawCount", "SellerWithdrawCount"),
                        LastEvidenceType = ReadString(item, "lastEvidenceType", "LastEvidenceType"),
                        UpdatedAt = ReadString(item, "updatedAt", "UpdatedAt")
                    };
                    KnowledgePolicyProfileService.ImportCompleteProfile(entry, imported);
                    updated++;
                }

                var refreshed = KnowledgePolicyProfileService.GetProfilesForKnowledge(knowledge);
                SetField(window, "_profiles", refreshed);
                Invoke(window, "ApplySearch");
                Log.Info("知识策略已导入: file=" + Path.GetFileName(dialog.FileName)
                    + ", updated=" + updated + ", skipped=" + skipped + ", invalid=" + invalid
                    + ", backup=" + Path.GetFileName(backup));
                MessageBox.Show(
                    "知识策略导入完成。"
                    + "\n成功更新：" + updated
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

        private static JObject BuildPolicyExportObject(IList<KnowledgePolicyProfile> profiles)
        {
            profiles = profiles ?? new List<KnowledgePolicyProfile>();
            var items = profiles
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
                    ["confidence"] = x.Confidence <= 0 ? 0.80 : x.Confidence,
                    ["directSelectedCount"] = x.DirectSelectedCount,
                    ["contextualSelectedCount"] = x.ContextualSelectedCount,
                    ["acceptedCount"] = x.AcceptedCount,
                    ["sellerCorrectionCount"] = x.SellerCorrectionCount,
                    ["sellerWithdrawCount"] = x.SellerWithdrawCount,
                    ["lastEvidenceType"] = x.LastEvidenceType ?? string.Empty,
                    ["updatedAt"] = x.UpdatedAt ?? string.Empty
                });
            return new JObject
            {
                ["schema"] = PolicySchema,
                ["version"] = ExportVersion,
                ["exportedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["enabled"] = KnowledgePolicyProfileService.IsEnabled(),
                ["profiles"] = new JArray(items)
            };
        }

        internal static void ExportKnowledgePackage(KnowledgeCenterWindow window)
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Title = "导出知识库完整包",
                    Filter = "知识库JSON包 (*.json)|*.json",
                    FileName = "qianniu-knowledge-package-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json",
                    AddExtension = true,
                    DefaultExt = ".json"
                };
                if (dialog.ShowDialog(window) != true) return;
                var payload = BuildKnowledgePackage(window);
                WriteJson(dialog.FileName, payload);
                Log.Info("知识库完整包已导出: file=" + Path.GetFileName(dialog.FileName)
                    + ", knowledge=" + ((JArray)payload["knowledge"]).Count);
                MessageBox.Show("知识库完整包已导出，包含全部问答、知识策略/可靠度统计和知识相关设置。",
                    "知识库", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("导出知识库完整包失败：" + ex.Message, "知识库", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        internal static bool ImportKnowledgePackage(KnowledgeCenterWindow window)
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "导入知识库完整包",
                    Filter = "知识库JSON包 (*.json)|*.json",
                    Multiselect = false
                };
                if (dialog.ShowDialog(window) != true) return false;
                var root = ReadObject(dialog.FileName);
                ValidateSchema(root, KnowledgePackageSchema);
                var knowledgeToken = root["knowledge"] as JArray;
                if (knowledgeToken == null) throw new Exception("文件中没有 knowledge 问答数据。");
                var importedKnowledge = knowledgeToken.ToObject<List<KnowledgeBaseEntry>>() ?? new List<KnowledgeBaseEntry>();
                var confirm = MessageBox.Show(
                    "将完整替换当前店铺知识库为 " + importedKnowledge.Count + " 条问答，并恢复知识策略/可靠度统计和知识相关设置。"
                    + "\n导入前会自动备份当前知识库完整包。是否继续？",
                    "确认导入知识库完整包", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return false;

                var backup = BuildBackupPath("knowledge-package-before-import");
                WriteJson(backup, BuildKnowledgePackage(window));
                BotFeatureStore.SaveKnowledgeBase(importedKnowledge);

                var policy = root["policy"] as JObject;
                if (policy != null)
                {
                    var enabledToken = policy["enabled"];
                    bool enabled;
                    var shop = ResolveShop(window);
                    if (enabledToken != null && bool.TryParse(Convert.ToString(enabledToken), out enabled) && shop != null)
                        KnowledgePolicyProfileService.SetEnabled(shop, enabled);
                    var profiles = policy["profiles"] as JArray;
                    if (profiles != null)
                    {
                        foreach (var token in profiles.OfType<JObject>())
                        {
                            var entry = FindKnowledgeForImport(
                                importedKnowledge,
                                ReadString(token, "knowledgeId", "KnowledgeId"),
                                ReadString(token, "questionSnapshot", "QuestionSnapshot"));
                            if (entry == null) continue;
                            KnowledgePolicyProfileService.ImportCompleteProfile(entry, ReadCompleteProfile(token));
                        }
                    }
                }

                var settings = root["settings"] as JObject;
                var resolvedShop = ResolveShop(window);
                if (settings != null && resolvedShop != null)
                {
                    var values = settings.Properties().ToDictionary(
                        x => x.Name, x => Convert.ToString(x.Value) ?? string.Empty, StringComparer.Ordinal);
                    new ShopScopedSettingsStore(resolvedShop, new ShopScopedPathProvider()).MergeValues(values, true);
                }

                Log.Info("知识库完整包已导入: file=" + Path.GetFileName(dialog.FileName)
                    + ", knowledge=" + importedKnowledge.Count + ", backup=" + Path.GetFileName(backup));
                MessageBox.Show("知识库完整包导入成功。\n原配置备份：" + backup,
                    "知识库", MessageBoxButton.OK, MessageBoxImage.Information);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("导入知识库完整包失败：" + ex.Message, "知识库", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private static JObject BuildKnowledgePackage(Window window)
        {
            var knowledge = BotFeatureStore.GetKnowledgeBase() ?? new List<KnowledgeBaseEntry>();
            var policy = BuildPolicyExportObject(KnowledgePolicyProfileService.GetProfilesForKnowledge(knowledge));
            var settings = new JObject();
            var shop = ResolveShop(window);
            if (shop != null)
            {
                var values = new ShopScopedSettingsStore(shop, new ShopScopedPathProvider()).ExportValues();
                foreach (var pair in values.Where(x =>
                    x.Key.StartsWith("knowledge.", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.Key, ReplyModeService.SettingsKey, StringComparison.Ordinal)))
                {
                    settings[pair.Key] = pair.Value ?? string.Empty;
                }
            }
            return new JObject
            {
                ["schema"] = KnowledgePackageSchema,
                ["version"] = ExportVersion,
                ["exportedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["shopKey"] = shop == null ? string.Empty : shop.ShopKey,
                ["knowledge"] = JArray.FromObject(knowledge),
                ["policy"] = policy,
                ["settings"] = settings
            };
        }

        private static KnowledgePolicyProfile ReadCompleteProfile(JObject item)
        {
            return new KnowledgePolicyProfile
            {
                KnowledgeId = ReadString(item, "knowledgeId", "KnowledgeId"),
                QuestionSnapshot = ReadString(item, "questionSnapshot", "QuestionSnapshot"),
                Intent = ReadString(item, "intent", "Intent"),
                Entities = ReadString(item, "entities", "Entities"),
                ApplyWhen = ReadString(item, "applyWhen", "ApplyWhen"),
                DoNotApplyWhen = ReadString(item, "doNotApplyWhen", "DoNotApplyWhen"),
                RequiredContext = ReadString(item, "requiredContext", "RequiredContext"),
                AnswerMode = ReadString(item, "answerMode", "AnswerMode"),
                Confidence = ReadDouble(item, 0.80, "confidence", "Confidence"),
                DirectSelectedCount = ReadInt(item, 0, "directSelectedCount", "DirectSelectedCount"),
                ContextualSelectedCount = ReadInt(item, 0, "contextualSelectedCount", "ContextualSelectedCount"),
                AcceptedCount = ReadInt(item, 0, "acceptedCount", "AcceptedCount"),
                SellerCorrectionCount = ReadInt(item, 0, "sellerCorrectionCount", "SellerCorrectionCount"),
                SellerWithdrawCount = ReadInt(item, 0, "sellerWithdrawCount", "SellerWithdrawCount"),
                LastEvidenceType = ReadString(item, "lastEvidenceType", "LastEvidenceType"),
                UpdatedAt = ReadString(item, "updatedAt", "UpdatedAt")
            };
        }

        private static ShopContext ResolveShop(Window window)
        {
            return ShopSettingsScope.Current
                ?? ShopScopedUiBridge.Get(window)
                ?? (window == null ? null : ShopScopedUiBridge.Get(window.Owner));
        }

        private static string BackupKnowledgePolicies(KnowledgePolicyProfileWindow window)
        {
            var path = BuildBackupPath("knowledge-policies-before-import");
            WriteJson(path, BuildPolicyExportObject(GetPolicyProfiles(window)));
            return path;
        }

        private static List<KnowledgePolicyProfile> GetPolicyProfiles(KnowledgePolicyProfileWindow window)
        {
            return GetField<List<KnowledgePolicyProfile>>(window, "_profiles")
                ?? KnowledgePolicyProfileService.GetProfilesForKnowledge(GetKnowledge(window));
        }

        private static List<KnowledgeBaseEntry> GetKnowledge(KnowledgePolicyProfileWindow window)
        {
            return GetField<List<KnowledgeBaseEntry>>(window, "_knowledge")
                ?? BotFeatureStore.GetKnowledgeBase()
                ?? new List<KnowledgeBaseEntry>();
        }

        private static KnowledgeBaseEntry FindKnowledgeForImport(
            IEnumerable<KnowledgeBaseEntry> knowledge,
            string knowledgeId,
            string question)
        {
            var list = (knowledge ?? Enumerable.Empty<KnowledgeBaseEntry>())
                .Where(x => x != null)
                .ToList();
            var byId = list.FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(knowledgeId)
                && string.Equals(x.Id ?? string.Empty, knowledgeId, StringComparison.Ordinal));
            if (byId != null) return byId;

            var normalized = KnowledgeAiService.NormalizeQuestion(question);
            if (normalized.Length == 0) return null;
            return list.FirstOrDefault(x =>
                KnowledgeAiService.NormalizeQuestion(x.Title) == normalized);
        }

        private static string BuildBackupPath(string prefix)
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "data",
                "backups");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory,
                prefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + ".json");
        }

        private static JObject ReadObject(string path)
        {
            return JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
        }

        private static void WriteJson(string path, JObject value)
        {
            File.WriteAllText(
                path,
                (value ?? new JObject()).ToString(Formatting.Indented),
                new UTF8Encoding(false));
        }

        private static bool SchemaMatches(string actual, string expected)
        {
            if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.IsNullOrWhiteSpace(expected)
                || !expected.StartsWith("qnbot.", StringComparison.OrdinalIgnoreCase)) return false;
            var legacyExpected = ("qianniu" + "-ai-bot") + expected.Substring("qnbot".Length);
            return string.Equals(actual, legacyExpected, StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateSchema(JObject root, string expected)
        {
            if (root == null) throw new Exception("导入文件为空。");
            var schema = Convert.ToString(root["schema"]);
            if (!string.IsNullOrWhiteSpace(schema)
                && !SchemaMatches(schema, expected))
            {
                throw new Exception("文件类型不匹配：" + schema);
            }

            int version;
            if (root["version"] != null
                && (!int.TryParse(Convert.ToString(root["version"]), out version)
                    || version < 1
                    || version > ExportVersion))
            {
                throw new Exception("不支持的导入文件版本：" + Convert.ToString(root["version"]));
            }
        }

        private static string ReadString(JObject value, params string[] names)
        {
            if (value == null) return string.Empty;
            foreach (var name in names ?? new string[0])
            {
                var token = value[name];
                if (token != null && token.Type != JTokenType.Null)
                    return Convert.ToString(token).Trim();
            }
            return string.Empty;
        }

        private static double ReadDouble(JObject value, double fallback, params string[] names)
        {
            double parsed;
            return double.TryParse(ReadString(value, names), out parsed) ? parsed : fallback;
        }

        private static int ReadInt(JObject value, int fallback, params string[] names)
        {
            int parsed;
            return int.TryParse(ReadString(value, names), out parsed) ? parsed : fallback;
        }

        private static T GetField<T>(object target, string name) where T : class
        {
            if (target == null) return null;
            var field = target.GetType().GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return field == null ? null : field.GetValue(target) as T;
        }

        private static void SetField(object target, string name, object value)
        {
            if (target == null) return;
            var field = target.GetType().GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null) throw new Exception("无法刷新窗口字段：" + name);
            field.SetValue(target, value);
        }

        private static object Invoke(object target, string name)
        {
            if (target == null) return null;
            var method = target.GetType().GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (method == null) throw new Exception("无法刷新窗口方法：" + name);
            return method.Invoke(target, null);
        }

        private static Button FindButton(DependencyObject root, string content)
        {
            if (root == null) return null;
            var button = root as Button;
            if (button != null
                && string.Equals(Convert.ToString(button.Content), content, StringComparison.Ordinal))
                return button;

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var found = FindButton(VisualTreeHelper.GetChild(root, i), content);
                if (found != null) return found;
            }
            if (root is ContentControl)
            {
                var found = FindButton(((ContentControl)root).Content as DependencyObject, content);
                if (found != null) return found;
            }
            return null;
        }
    }
}
