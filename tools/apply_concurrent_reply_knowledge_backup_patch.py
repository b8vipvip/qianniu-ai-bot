from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def write(path, text):
    (ROOT / path).write_text(text, encoding="utf-8")


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, found {count}")
    return text.replace(old, new, 1)


# 1) Knowledge reliability switch + complete profile restore.
path = "src/Bot/ChromeNs/KnowledgePolicyProfileService.cs"
s = read(path)
s = replace_once(s, "using Bot.Knowledge;\nusing BotLib;", "using Bot.Knowledge;\nusing Bot.ShopScope;\nusing BotLib;", "policy using")
s = replace_once(
    s,
    "        private static readonly object Sync = new object();\n        private static PolicyFile _cache;\n",
    "        private static readonly object Sync = new object();\n        private static PolicyFile _cache;\n        internal const string EnabledSettingsKey = \"knowledge.policy_reliability_enabled\";\n        private static readonly IShopScopedPathProvider SettingsPaths = new ShopScopedPathProvider();\n\n        public static bool IsEnabled(ShopContext shop = null)\n        {\n            shop = shop ?? ShopSettingsScope.Current;\n            if (shop == null) return true;\n            try\n            {\n                string value;\n                var store = new ShopScopedSettingsStore(shop, SettingsPaths);\n                if (!store.TryGetString(EnabledSettingsKey, out value)) return true;\n                value = (value ?? string.Empty).Trim();\n                return !(value == \"0\"\n                    || value.Equals(\"false\", StringComparison.OrdinalIgnoreCase)\n                    || value.Equals(\"off\", StringComparison.OrdinalIgnoreCase));\n            }\n            catch (Exception ex)\n            {\n                Log.ErrorWithMaxCount(\"读取知识策略与可靠度开关失败，按启用运行: \" + ex.Message, 10);\n                return true;\n            }\n        }\n\n        public static void SetEnabled(ShopContext shop, bool enabled)\n        {\n            shop = shop ?? ShopSettingsScope.Current;\n            if (shop == null) throw new InvalidOperationException(\"保存知识策略开关需要当前店铺上下文。\");\n            var store = new ShopScopedSettingsStore(shop, SettingsPaths);\n            store.SetString(EnabledSettingsKey, enabled ? \"1\" : \"0\");\n            Log.Info(\"知识策略与可靠度已\" + (enabled ? \"启用\" : \"关闭\") + \": shop=\" + shop.ShopKey);\n        }\n",
    "policy switch service")
insert_before = "        public static KnowledgePolicyEvaluation Evaluate(\n"
complete_method = '''        public static void ImportCompleteProfile(KnowledgeBaseEntry entry, KnowledgePolicyProfile imported)\n        {\n            if (entry == null || imported == null) return;\n            lock (Sync)\n            {\n                var file = LoadInternal();\n                var id = StableId(entry);\n                var existing = file.Profiles.FirstOrDefault(x => x != null\n                    && string.Equals(x.KnowledgeId, id, StringComparison.Ordinal));\n                if (existing == null)\n                {\n                    existing = NewProfile(entry);\n                    file.Profiles.Add(existing);\n                }\n                existing.KnowledgeId = id;\n                existing.QuestionSnapshot = Clean(entry.Title, 400);\n                existing.Intent = Clean(imported.Intent, 80);\n                existing.Entities = Clean(imported.Entities, 500);\n                existing.ApplyWhen = Clean(imported.ApplyWhen, 1000);\n                existing.DoNotApplyWhen = Clean(imported.DoNotApplyWhen, 1000);\n                existing.RequiredContext = Clean(imported.RequiredContext, 1000);\n                existing.AnswerMode = KnowledgeAnswerModes.Normalize(imported.AnswerMode);\n                existing.Confidence = Clamp(imported.Confidence <= 0 ? 0.80 : imported.Confidence);\n                existing.DirectSelectedCount = Math.Max(0, imported.DirectSelectedCount);\n                existing.ContextualSelectedCount = Math.Max(0, imported.ContextualSelectedCount);\n                existing.AcceptedCount = Math.Max(0, imported.AcceptedCount);\n                existing.SellerCorrectionCount = Math.Max(0, imported.SellerCorrectionCount);\n                existing.SellerWithdrawCount = Math.Max(0, imported.SellerWithdrawCount);\n                existing.LastEvidenceType = Clean(imported.LastEvidenceType, 120);\n                existing.UpdatedAt = string.IsNullOrWhiteSpace(imported.UpdatedAt)\n                    ? DateTime.Now.ToString(\"yyyy-MM-dd HH:mm:ss\")\n                    : Clean(imported.UpdatedAt, 40);\n                SaveInternal(file);\n            }\n        }\n\n'''
if s.count(insert_before) != 1:
    raise RuntimeError("policy import insertion point missing")
s = s.replace(insert_before, complete_method + insert_before, 1)
s = replace_once(
    s,
    "            var profile = GetProfile(entry);\n            var mode = KnowledgeAnswerModes.Normalize(profile.AnswerMode);",
    "            var profile = GetProfile(entry);\n            if (!IsEnabled())\n            {\n                return new KnowledgePolicyEvaluation\n                {\n                    Profile = profile,\n                    Excluded = false,\n                    ForceContextual = false,\n                    ConstraintOnly = false,\n                    AllowDirect = true,\n                    ScoreAdjustment = 0,\n                    Reason = \"知识策略与可靠度已关闭，仅保留基础相关度、上下文和高风险安全判断\"\n                };\n            }\n            var mode = KnowledgeAnswerModes.Normalize(profile.AnswerMode);",
    "policy disabled evaluation")
write(path, s)


# 2) Make demonstrative but otherwise explicit questions eligible for safe high-confidence local direct replies.
path = "src/Bot/ChromeNs/SmartReplyRouterService.cs"
s = read(path)
s = replace_once(
    s,
    "            if (best == null || best.Entry == null) return false;\n            if (dependency > 0.20) return false;\n            if (resolution != null && resolution.Rewritten) return false;\n            if (!PolicyAllowsDirect(best)) return false;\n            if (Compact(question).Length < 4) return false;\n            if (ContextCues.Any(x => Compact(question).Contains(Compact(x)))) return false;\n            if (IsUnsafeDirectAnswer(best.Entry.Answer)) return false;",
    "            if (best == null || best.Entry == null) return false;\n            var selfContainedDemonstrative = IsSelfContainedDemonstrativeQuestion(question, best);\n            if (dependency > 0.20 && !selfContainedDemonstrative) return false;\n            if (resolution != null && resolution.Rewritten && !selfContainedDemonstrative) return false;\n            if (!PolicyAllowsDirect(best)) return false;\n            if (Compact(question).Length < 4) return false;\n            if (ContextCues.Any(x => Compact(question).Contains(Compact(x))) && !selfContainedDemonstrative) return false;\n            if (IsUnsafeDirectAnswer(best.Entry.Answer)) return false;",
    "direct context gate")
s = replace_once(
    s,
    "            if (best.ExactQuestionMatch && best.RetrievalScore >= 0.95) return reliability >= 0.58;\n            if (mode == KnowledgeAnswerModes.Direct && reliability >= 0.78)",
    "            if (best.ExactQuestionMatch && best.RetrievalScore >= 0.95) return reliability >= 0.58;\n            if (selfContainedDemonstrative\n                && reliability >= 0.58\n                && best.FinalScore >= 0.88\n                && (best.RetrievalScore >= 0.80 || best.ResolvedQueryScore >= 0.84 || best.SemanticScore >= 0.86)\n                && margin >= 0.08) return true;\n            if (mode == KnowledgeAnswerModes.Direct && reliability >= 0.78)",
    "direct demonstrative threshold")
marker = "        private static bool PolicyAllowsDirect(SmartKnowledgeCandidate candidate)\n"
helper = '''        private static bool IsSelfContainedDemonstrativeQuestion(string question, SmartKnowledgeCandidate best)\n        {\n            if (best == null || best.Entry == null) return false;\n            var compact = Compact(question);\n            if (!Regex.IsMatch(compact, @\"^(这个|那个|这款|那款|这种|那种)\")) return false;\n            var stripped = Regex.Replace(compact, @\"^(这个|那个|这款|那款|这种|那种)\", string.Empty);\n            if (stripped.Length < 8) return false;\n            if (!Regex.IsMatch(stripped, @\"会员|电视|tv|手机|电脑|平板|酷狗|音乐|充值|账号|设备|软件|app|价格|退款|订单\", RegexOptions.IgnoreCase)) return false;\n            if (HighRiskTerms.Any(x => stripped.Contains(Compact(x)))) return false;\n            return best.FinalScore >= 0.84\n                && (best.RetrievalScore >= 0.72 || best.ResolvedQueryScore >= 0.78 || best.SemanticScore >= 0.82);\n        }\n\n'''
if s.count(marker) != 1:
    raise RuntimeError("router helper insertion point missing")
s = s.replace(marker, helper + marker, 1)
write(path, s)


# 3) Knowledge-policy window: explicit enable switch and explicit full import/export buttons.
path = "src/Bot/Knowledge/KnowledgePolicyProfileUi.cs"
s = read(path)
s = replace_once(s, "using Bot.ChromeNs;\nusing System;", "using Bot.ChromeNs;\nusing Bot.ShopScope;\nusing System;", "policy ui using")
s = replace_once(
    s,
    "                var window = new KnowledgePolicyProfileWindow\n                {\n                    Owner = Window.GetWindow(manager)\n                };\n                window.ShowDialog();",
    "                var owner = Window.GetWindow(manager);\n                var shop = ShopSettingsScope.Current ?? ShopScopedUiBridge.Get(owner);\n                var window = new KnowledgePolicyProfileWindow(shop)\n                {\n                    Owner = owner\n                };\n                if (shop != null) ShopScopedUiBridge.Attach(window, shop);\n                window.ShowDialog();",
    "policy ui scoped window")
s = replace_once(
    s,
    "        private List<KnowledgePolicyProfile> _profiles;\n        private bool _loading;\n\n        public KnowledgePolicyProfileWindow()\n        {\n            Title = \"知识策略与可靠度\";",
    "        private List<KnowledgePolicyProfile> _profiles;\n        private bool _loading;\n        private readonly ShopContext _shop;\n        private readonly CheckBox _enabled;\n\n        public KnowledgePolicyProfileWindow(ShopContext shop = null)\n        {\n            _shop = shop ?? ShopSettingsScope.Current;\n            Title = \"知识策略与可靠度\";",
    "policy ui fields ctor")
s = replace_once(
    s,
    "            var form = new StackPanel();\n            right.Content = form;\n\n            AddLabel(form, \"回答模式\");",
    "            var form = new StackPanel();\n            right.Content = form;\n\n            _enabled = new CheckBox\n            {\n                Content = \"启用知识策略与可靠度\",\n                IsChecked = KnowledgePolicyProfileService.IsEnabled(_shop),\n                FontWeight = FontWeights.SemiBold,\n                Margin = new Thickness(0, 0, 0, 6),\n                ToolTip = \"关闭后不再用每条知识的回答模式、适用/禁用/必要上下文和可靠度限制直答；仍保留基础相关度、上下文依赖与高风险安全判断。\"\n            };\n            _enabled.Click += (s, e) => KnowledgePolicyProfileService.SetEnabled(_shop, _enabled.IsChecked == true);\n            form.Children.Add(_enabled);\n            form.Children.Add(new TextBlock\n            {\n                Text = \"关闭仅绕过本页策略/可靠度约束，不会关闭知识库，也不会绕过订单流程、上下文或高风险安全校验。\",\n                TextWrapping = TextWrapping.Wrap,\n                Foreground = Brushes.DimGray,\n                Margin = new Thickness(0, 0, 0, 12)\n            });\n\n            AddLabel(form, \"回答模式\");",
    "policy ui switch")
s = replace_once(
    s,
    "            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };\n            var save = new Button { Content = \"保存策略\", Width = 90, Height = 30, Margin = new Thickness(0, 0, 8, 0) };",
    "            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };\n            var importAll = new Button { Content = \"导入全部\", Width = 82, Height = 30, Margin = new Thickness(0, 0, 8, 0), Tag = \"knowledge-policies-import-full\" };\n            importAll.Click += (s, e) => RulePolicyImportExportUi.ImportKnowledgePolicies(this);\n            buttons.Children.Add(importAll);\n            var exportAll = new Button { Content = \"导出全部\", Width = 82, Height = 30, Margin = new Thickness(0, 0, 8, 0), Tag = \"knowledge-policies-export-full\" };\n            exportAll.Click += (s, e) => RulePolicyImportExportUi.ExportKnowledgePolicies(this);\n            buttons.Children.Add(exportAll);\n            var save = new Button { Content = \"保存策略\", Width = 90, Height = 30, Margin = new Thickness(0, 0, 8, 0) };",
    "policy ui full buttons")
write(path, s)


# 4) Full policy import/export + whole knowledge package import/export.
path = "src/Bot/Knowledge/RulePolicyImportExportUi.cs"
s = read(path)
s = replace_once(s, "using Bot.ChromeNs;\nusing BotLib;", "using Bot.ChromeNs;\nusing Bot.ShopScope;\nusing BotLib;", "rule import using")
s = replace_once(
    s,
    "        private const string PolicySchema = \"qianniu-ai-bot.knowledge-policies\";\n        private const int ExportVersion = 1;",
    "        private const string PolicySchema = \"qianniu-ai-bot.knowledge-policies\";\n        private const string KnowledgePackageSchema = \"qianniu-ai-bot.knowledge-package\";\n        private const int ExportVersion = 2;",
    "package schema")
s = replace_once(
    s,
    "            var window = sender as KnowledgePolicyProfileWindow;\n            if (window == null || IsAttached(window)) return;\n\n            var save = FindButton(window, \"保存策略\");",
    "            var window = sender as KnowledgePolicyProfileWindow;\n            if (window == null || IsAttached(window)) return;\n            if (FindButton(window, \"导入全部\") != null || FindButton(window, \"导出全部\") != null)\n            {\n                MarkAttached(window);\n                return;\n            }\n\n            var save = FindButton(window, \"保存策略\");",
    "avoid duplicate policy buttons")
s = s.replace("        private static void ExportKnowledgePolicies(KnowledgePolicyProfileWindow window)", "        internal static void ExportKnowledgePolicies(KnowledgePolicyProfileWindow window)", 1)
s = s.replace("        private static void ImportKnowledgePolicies(KnowledgePolicyProfileWindow window)", "        internal static void ImportKnowledgePolicies(KnowledgePolicyProfileWindow window)", 1)
s = s.replace("可靠度统计属于本机学习数据，未写入迁移文件。", "已包含回答模式、条件、可靠度及全部学习统计。", 1)
s = s.replace("不会删除现有策略，可靠度学习统计也不会被覆盖。", "不会删除现有策略；匹配到的策略将完整恢复配置和可靠度学习统计。", 1)
s = replace_once(
    s,
    "                        AnswerMode = ReadString(item, \"answerMode\", \"AnswerMode\"),\n                        Confidence = ReadDouble(item, 0.80, \"confidence\", \"Confidence\")\n                    };\n                    KnowledgePolicyProfileService.SaveProfile(entry, imported);",
    "                        AnswerMode = ReadString(item, \"answerMode\", \"AnswerMode\"),\n                        Confidence = ReadDouble(item, 0.80, \"confidence\", \"Confidence\"),\n                        DirectSelectedCount = ReadInt(item, 0, \"directSelectedCount\", \"DirectSelectedCount\"),\n                        ContextualSelectedCount = ReadInt(item, 0, \"contextualSelectedCount\", \"ContextualSelectedCount\"),\n                        AcceptedCount = ReadInt(item, 0, \"acceptedCount\", \"AcceptedCount\"),\n                        SellerCorrectionCount = ReadInt(item, 0, \"sellerCorrectionCount\", \"SellerCorrectionCount\"),\n                        SellerWithdrawCount = ReadInt(item, 0, \"sellerWithdrawCount\", \"SellerWithdrawCount\"),\n                        LastEvidenceType = ReadString(item, \"lastEvidenceType\", \"LastEvidenceType\"),\n                        UpdatedAt = ReadString(item, \"updatedAt\", \"UpdatedAt\")\n                    };\n                    KnowledgePolicyProfileService.ImportCompleteProfile(entry, imported);",
    "policy full restore")
s = replace_once(
    s,
    "                    [\"answerMode\"] = KnowledgeAnswerModes.Normalize(x.AnswerMode),\n                    [\"confidence\"] = x.Confidence <= 0 ? 0.80 : x.Confidence\n                });",
    "                    [\"answerMode\"] = KnowledgeAnswerModes.Normalize(x.AnswerMode),\n                    [\"confidence\"] = x.Confidence <= 0 ? 0.80 : x.Confidence,\n                    [\"directSelectedCount\"] = x.DirectSelectedCount,\n                    [\"contextualSelectedCount\"] = x.ContextualSelectedCount,\n                    [\"acceptedCount\"] = x.AcceptedCount,\n                    [\"sellerCorrectionCount\"] = x.SellerCorrectionCount,\n                    [\"sellerWithdrawCount\"] = x.SellerWithdrawCount,\n                    [\"lastEvidenceType\"] = x.LastEvidenceType ?? string.Empty,\n                    [\"updatedAt\"] = x.UpdatedAt ?? string.Empty\n                });",
    "policy export stats")
s = replace_once(
    s,
    "                [\"exportedAt\"] = DateTime.Now.ToString(\"yyyy-MM-dd HH:mm:ss\"),\n                [\"profiles\"] = new JArray(items)",
    "                [\"exportedAt\"] = DateTime.Now.ToString(\"yyyy-MM-dd HH:mm:ss\"),\n                [\"enabled\"] = KnowledgePolicyProfileService.IsEnabled(),\n                [\"profiles\"] = new JArray(items)",
    "policy export enabled")
s = replace_once(
    s,
    "                var profiles = root[\"profiles\"] as JArray ?? root[\"Profiles\"] as JArray;\n                if (profiles == null) throw new Exception(\"文件中没有 profiles 策略数组。\");",
    "                var profiles = root[\"profiles\"] as JArray ?? root[\"Profiles\"] as JArray;\n                if (profiles == null) throw new Exception(\"文件中没有 profiles 策略数组。\");\n                if (root[\"enabled\"] != null)\n                {\n                    bool enabled;\n                    if (bool.TryParse(Convert.ToString(root[\"enabled\"]), out enabled))\n                        KnowledgePolicyProfileService.SetEnabled(ShopSettingsScope.Current, enabled);\n                }",
    "policy import enabled")
# Add complete knowledge-package methods before backup helpers.
marker = "        private static string BackupKnowledgePolicies(KnowledgePolicyProfileWindow window)\n"
package_methods = '''        internal static void ExportKnowledgePackage(KnowledgeCenterWindow window)\n        {\n            try\n            {\n                var dialog = new SaveFileDialog\n                {\n                    Title = \"导出知识库完整包\",\n                    Filter = \"知识库JSON包 (*.json)|*.json\",\n                    FileName = \"qianniu-knowledge-package-\" + DateTime.Now.ToString(\"yyyyMMdd-HHmmss\") + \".json\",\n                    AddExtension = true,\n                    DefaultExt = \".json\"\n                };\n                if (dialog.ShowDialog(window) != true) return;\n                var payload = BuildKnowledgePackage(window);\n                WriteJson(dialog.FileName, payload);\n                Log.Info(\"知识库完整包已导出: file=\" + Path.GetFileName(dialog.FileName)\n                    + \", knowledge=\" + ((JArray)payload[\"knowledge\"]).Count);\n                MessageBox.Show(\"知识库完整包已导出，包含全部问答、知识策略/可靠度统计和知识相关设置。\",\n                    \"知识库\", MessageBoxButton.OK, MessageBoxImage.Information);\n            }\n            catch (Exception ex)\n            {\n                MessageBox.Show(\"导出知识库完整包失败：\" + ex.Message, \"知识库\", MessageBoxButton.OK, MessageBoxImage.Error);\n            }\n        }\n\n        internal static bool ImportKnowledgePackage(KnowledgeCenterWindow window)\n        {\n            try\n            {\n                var dialog = new OpenFileDialog\n                {\n                    Title = \"导入知识库完整包\",\n                    Filter = \"知识库JSON包 (*.json)|*.json\",\n                    Multiselect = false\n                };\n                if (dialog.ShowDialog(window) != true) return false;\n                var root = ReadObject(dialog.FileName);\n                ValidateSchema(root, KnowledgePackageSchema);\n                var knowledgeToken = root[\"knowledge\"] as JArray;\n                if (knowledgeToken == null) throw new Exception(\"文件中没有 knowledge 问答数据。\");\n                var importedKnowledge = knowledgeToken.ToObject<List<KnowledgeBaseEntry>>() ?? new List<KnowledgeBaseEntry>();\n                var confirm = MessageBox.Show(\n                    \"将完整替换当前店铺知识库为 \" + importedKnowledge.Count + \" 条问答，并恢复知识策略/可靠度统计和知识相关设置。\"\n                    + \"\\n导入前会自动备份当前知识库完整包。是否继续？\",\n                    \"确认导入知识库完整包\", MessageBoxButton.YesNo, MessageBoxImage.Question);\n                if (confirm != MessageBoxResult.Yes) return false;\n\n                var backup = BuildBackupPath(\"knowledge-package-before-import\");\n                WriteJson(backup, BuildKnowledgePackage(window));\n                BotFeatureStore.SaveKnowledgeBase(importedKnowledge);\n\n                var policy = root[\"policy\"] as JObject;\n                if (policy != null)\n                {\n                    var enabledToken = policy[\"enabled\"];\n                    bool enabled;\n                    var shop = ResolveShop(window);\n                    if (enabledToken != null && bool.TryParse(Convert.ToString(enabledToken), out enabled) && shop != null)\n                        KnowledgePolicyProfileService.SetEnabled(shop, enabled);\n                    var profiles = policy[\"profiles\"] as JArray;\n                    if (profiles != null)\n                    {\n                        foreach (var token in profiles.OfType<JObject>())\n                        {\n                            var entry = FindKnowledgeForImport(\n                                importedKnowledge,\n                                ReadString(token, \"knowledgeId\", \"KnowledgeId\"),\n                                ReadString(token, \"questionSnapshot\", \"QuestionSnapshot\"));\n                            if (entry == null) continue;\n                            KnowledgePolicyProfileService.ImportCompleteProfile(entry, ReadCompleteProfile(token));\n                        }\n                    }\n                }\n\n                var settings = root[\"settings\"] as JObject;\n                var resolvedShop = ResolveShop(window);\n                if (settings != null && resolvedShop != null)\n                {\n                    var values = settings.Properties().ToDictionary(\n                        x => x.Name, x => Convert.ToString(x.Value) ?? string.Empty, StringComparer.Ordinal);\n                    new ShopScopedSettingsStore(resolvedShop, new ShopScopedPathProvider()).MergeValues(values, true);\n                }\n\n                KnowledgeLearningService.NotifyKnowledgeBaseChanged();\n                Log.Info(\"知识库完整包已导入: file=\" + Path.GetFileName(dialog.FileName)\n                    + \", knowledge=\" + importedKnowledge.Count + \", backup=\" + Path.GetFileName(backup));\n                MessageBox.Show(\"知识库完整包导入成功。\\n原配置备份：\" + backup,\n                    \"知识库\", MessageBoxButton.OK, MessageBoxImage.Information);\n                return true;\n            }\n            catch (Exception ex)\n            {\n                MessageBox.Show(\"导入知识库完整包失败：\" + ex.Message, \"知识库\", MessageBoxButton.OK, MessageBoxImage.Error);\n                return false;\n            }\n        }\n\n        private static JObject BuildKnowledgePackage(Window window)\n        {\n            var knowledge = BotFeatureStore.GetKnowledgeBase() ?? new List<KnowledgeBaseEntry>();\n            var policy = BuildPolicyExportObject(KnowledgePolicyProfileService.GetProfilesForKnowledge(knowledge));\n            var settings = new JObject();\n            var shop = ResolveShop(window);\n            if (shop != null)\n            {\n                var values = new ShopScopedSettingsStore(shop, new ShopScopedPathProvider()).ExportValues();\n                foreach (var pair in values.Where(x =>\n                    x.Key.StartsWith(\"knowledge.\", StringComparison.OrdinalIgnoreCase)\n                    || string.Equals(x.Key, ReplyModeService.SettingsKey, StringComparison.Ordinal)))\n                {\n                    settings[pair.Key] = pair.Value ?? string.Empty;\n                }\n            }\n            return new JObject\n            {\n                [\"schema\"] = KnowledgePackageSchema,\n                [\"version\"] = ExportVersion,\n                [\"exportedAt\"] = DateTime.Now.ToString(\"yyyy-MM-dd HH:mm:ss\"),\n                [\"shopKey\"] = shop == null ? string.Empty : shop.ShopKey,\n                [\"knowledge\"] = JArray.FromObject(knowledge),\n                [\"policy\"] = policy,\n                [\"settings\"] = settings\n            };\n        }\n\n        private static KnowledgePolicyProfile ReadCompleteProfile(JObject item)\n        {\n            return new KnowledgePolicyProfile\n            {\n                KnowledgeId = ReadString(item, \"knowledgeId\", \"KnowledgeId\"),\n                QuestionSnapshot = ReadString(item, \"questionSnapshot\", \"QuestionSnapshot\"),\n                Intent = ReadString(item, \"intent\", \"Intent\"),\n                Entities = ReadString(item, \"entities\", \"Entities\"),\n                ApplyWhen = ReadString(item, \"applyWhen\", \"ApplyWhen\"),\n                DoNotApplyWhen = ReadString(item, \"doNotApplyWhen\", \"DoNotApplyWhen\"),\n                RequiredContext = ReadString(item, \"requiredContext\", \"RequiredContext\"),\n                AnswerMode = ReadString(item, \"answerMode\", \"AnswerMode\"),\n                Confidence = ReadDouble(item, 0.80, \"confidence\", \"Confidence\"),\n                DirectSelectedCount = ReadInt(item, 0, \"directSelectedCount\", \"DirectSelectedCount\"),\n                ContextualSelectedCount = ReadInt(item, 0, \"contextualSelectedCount\", \"ContextualSelectedCount\"),\n                AcceptedCount = ReadInt(item, 0, \"acceptedCount\", \"AcceptedCount\"),\n                SellerCorrectionCount = ReadInt(item, 0, \"sellerCorrectionCount\", \"SellerCorrectionCount\"),\n                SellerWithdrawCount = ReadInt(item, 0, \"sellerWithdrawCount\", \"SellerWithdrawCount\"),\n                LastEvidenceType = ReadString(item, \"lastEvidenceType\", \"LastEvidenceType\"),\n                UpdatedAt = ReadString(item, \"updatedAt\", \"UpdatedAt\")\n            };\n        }\n\n        private static ShopContext ResolveShop(Window window)\n        {\n            return ShopSettingsScope.Current\n                ?? ShopScopedUiBridge.Get(window)\n                ?? (window == null ? null : ShopScopedUiBridge.Get(window.Owner));\n        }\n\n'''
if s.count(marker) != 1:
    raise RuntimeError("knowledge package insertion point missing")
s = s.replace(marker, package_methods + marker, 1)
s = replace_once(
    s,
    "        private static double ReadDouble(JObject value, double fallback, params string[] names)\n        {\n            double parsed;\n            return double.TryParse(ReadString(value, names), out parsed) ? parsed : fallback;\n        }",
    "        private static double ReadDouble(JObject value, double fallback, params string[] names)\n        {\n            double parsed;\n            return double.TryParse(ReadString(value, names), out parsed) ? parsed : fallback;\n        }\n\n        private static int ReadInt(JObject value, int fallback, params string[] names)\n        {\n            int parsed;\n            return int.TryParse(ReadString(value, names), out parsed) ? parsed : fallback;\n        }",
    "read int helper")
write(path, s)


# 5) Add prominent import/export to the Knowledge Center window itself.
path = "src/Bot/Knowledge/KnowledgeCenterWindow.cs"
s = read(path)
s = replace_once(
    s,
    "            _tabs = new TabControl();\n            Content = _tabs;\n            _manager = new KnowledgeManagerControl();",
    "            var root = new DockPanel();\n            Content = root;\n            var toolbar = new WrapPanel { Margin = new Thickness(10, 10, 10, 4) };\n            DockPanel.SetDock(toolbar, Dock.Top);\n            var importPackage = new Button { Content = \"导入知识库完整包\", Width = 140, Height = 30, Margin = new Thickness(0, 0, 8, 0) };\n            var exportPackage = new Button { Content = \"导出知识库完整包\", Width = 140, Height = 30, Margin = new Thickness(0, 0, 8, 0) };\n            toolbar.Children.Add(importPackage);\n            toolbar.Children.Add(exportPackage);\n            root.Children.Add(toolbar);\n            _tabs = new TabControl();\n            root.Children.Add(_tabs);\n            _manager = new KnowledgeManagerControl();",
    "knowledge center toolbar")
s = replace_once(
    s,
    "            _tabs.Items.Add(new TabItem { Header = \"AI优化记录\", Content = _optimizationHistory });\n        }",
    "            _tabs.Items.Add(new TabItem { Header = \"AI优化记录\", Content = _optimizationHistory });\n            importPackage.Click += (s, e) =>\n            {\n                if (RulePolicyImportExportUi.ImportKnowledgePackage(this))\n                {\n                    _manager.RefreshData();\n                    ShowManager();\n                }\n            };\n            exportPackage.Click += (s, e) => RulePolicyImportExportUi.ExportKnowledgePackage(this);\n        }",
    "knowledge center toolbar handlers")
write(path, s)


# 6) Buyer work: merge only before dispatch; once dispatched, later buyer messages no longer cancel the older task.
path = "src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs"
s = read(path)
s = replace_once(
    s,
    "            public bool WorkerRunning;\n            public int Version;",
    "            public bool WorkerRunning;\n            public int Version;\n            public int HardCancelVersion;",
    "burst hard cancel field")
s = replace_once(
    s,
    "            // A buyer message must invalidate an answer that has already been dispatched to the\n            // Smart Reply/AI handler before we spend time evaluating deterministic rules. This is\n            // what lets “好的” cancel the previous in-flight AI and immediately use a local reply.\n            InvalidateDispatchedAnswerOnArrival(item.SellerNick, item.BuyerNick);\n            var allowLocalShortReply = !HasPendingBuyerMessages(item.SellerNick, item.BuyerNick);",
    "            // New buyer messages may start another independent work item. Only messages still\n            // inside the short pre-dispatch merge window are merged; dispatched AI/vision work is\n            // not cancelled merely because the buyer continues typing.\n            var allowLocalShortReply = !HasPendingBuyerMessages(item.SellerNick, item.BuyerNick);",
    "remove latest-wins invalidation")
s = replace_once(
    s,
    "                state.Version++;\n                state.Items.Clear();",
    "                state.Version++;\n                state.HardCancelVersion++;\n                state.Items.Clear();",
    "manual hard cancel")
s = replace_once(
    s,
    "                CancellationToken token;\n                int capturedVersion;\n                int delayMilliseconds;",
    "                CancellationToken token;\n                int capturedVersion;\n                int capturedHardCancelVersion;\n                int delayMilliseconds;",
    "capture hard cancel declaration")
s = replace_once(
    s,
    "                    token = state.DelayCancellation.Token;\n                    capturedVersion = state.Version;\n                    delayMilliseconds = QuietDelayMilliseconds(state.Items, state.StartedAt);",
    "                    token = state.DelayCancellation.Token;\n                    capturedVersion = state.Version;\n                    capturedHardCancelVersion = state.HardCancelVersion;\n                    delayMilliseconds = QuietDelayMilliseconds(state.Items, state.StartedAt);",
    "capture hard cancel value")
s = replace_once(
    s,
    "                            return state.Version == capturedVersion;",
    "                            return state.HardCancelVersion == capturedHardCancelVersion;",
    "lease hard cancellation semantics")
write(path, s)


# 7) Streaming pipeline: do not describe buyer continuation as cancellation; suppress only explicit correction/cancel after dispatch.
path = "src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs"
s = read(path)
s = s.replace("新消息仍会取消旧AI流。", "新消息可并发处理；已派发AI仅在人工接管/显式失效时取消。", 1)
s = s.replace("已为客服实例启用可取消Smart Reply流式管线", "已为客服实例启用并发Smart Reply流式管线", 1)
s = s.replace("买家补充了新消息，旧AI流已取消", "当前任务已被人工接管或显式取消", 1)
s = s.replace("已转入买家最新一轮消息，旧答案不会发送", "当前任务已失效，答案不会发送", 1)
s = s.replace("买家补充了新消息，旧AI结果已丢弃", "当前任务已失效，AI结果已丢弃", 1)
s = s.replace("发送前收到买家新消息，旧答案已取消", "发送前任务已失效，答案已取消", 1)
s = s.replace("未发送：买家刚刚补充了新消息，正在重新组织回复", "未发送：任务已因人工接管或显式取消而失效", 1)
s = replace_once(
    s,
    "            var answerReadyAt = DateTime.Now;\n            var answerSource = KnowledgeLearningService.ResolveAnswerSource(",
    "            string relevanceReason;\n            if (!ParallelReplyRelevanceGate.ShouldSend(\n                burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, detectedAt, out relevanceReason))\n            {\n                if (conversationCtl != null) conversationCtl.SetStatus(\"并发旧答案已抑制：\" + relevanceReason, false);\n                Log.Info(\"并发旧答案已抑制: buyer=\" + burst.BuyerNick + \", reason=\" + relevanceReason);\n                return;\n            }\n\n            var answerReadyAt = DateTime.Now;\n            var answerSource = KnowledgeLearningService.ResolveAnswerSource(",
    "parallel relevance gate call")
marker = "    internal static class ReplyTranscriptSanitizer\n"
relevance_class = '''    internal static class ParallelReplyRelevanceGate\n    {\n        private static readonly Regex SupersedeRegex = new Regex(\n            @\"(?:^|\\s)(?:不是|不对|说错了|我说的是|改一下|改成|算了|不用了|不用|取消|撤回|别回|不要回复|前面错了)(?:$|[，。！？\\s])\",\n            RegexOptions.Compiled | RegexOptions.IgnoreCase);\n\n        public static bool ShouldSend(string seller, string buyer, string originalQuestion, DateTime dispatchedAt, out string reason)\n        {\n            reason = string.Empty;\n            try\n            {\n                var newerBuyer = ConversationContextStore.GetRecentTurns(seller, buyer, originalQuestion, 20)\n                    .Where(x => x != null\n                        && x.Role == \"user\"\n                        && !x.Withdrawn\n                        && x.Timestamp != DateTime.MinValue\n                        && x.Timestamp > dispatchedAt.AddMilliseconds(120)\n                        && !string.IsNullOrWhiteSpace(x.Text))\n                    .OrderBy(x => x.Timestamp)\n                    .ToList();\n                if (newerBuyer.Count == 0) return true;\n                var latest = newerBuyer[newerBuyer.Count - 1].Text ?? string.Empty;\n                if (SupersedeRegex.IsMatch(latest))\n                {\n                    reason = \"买家后续消息明确纠正/取消了前一问题\";\n                    return false;\n                }\n                reason = \"买家有后续消息，但未明确否定前一问题，允许作为补充答案发送\";\n                return true;\n            }\n            catch\n            {\n                return true;\n            }\n        }\n    }\n\n'''
if s.count(marker) != 1:
    raise RuntimeError("parallel relevance class insertion point missing")
s = s.replace(marker, relevance_class + marker, 1)
write(path, s)


# 8) Empty messageCenterNotify still creates an independent order-payment wake instead of being discarded.
path = "src/Bot/ChromeNs/OrderPaymentNotificationFallback.cs"
s = read(path)
s = replace_once(
    s,
    "            var qn = sender as QN;\n            var raw = e == null ? string.Empty : (e.NotifyContent ?? string.Empty).Trim();\n            if (qn == null || raw.Length == 0) return;",
    "            var qn = sender as QN;\n            var raw = e == null ? string.Empty : (e.NotifyContent ?? string.Empty).Trim();\n            if (qn == null) return;\n            if (raw.Length == 0)\n            {\n                OrderAutomationCoordinator.ObserveGenericPaymentSignal(qn, \"messageCenterNotify空载荷\");\n                Log.Info(\"付款通知收到空载荷，已转入独立订单补扫，不等待买家文本/图片处理。\");\n                return;\n            }",
    "empty order notify fallback")
write(path, s)


# 9) Static regression coverage.
test = r'''from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_policy_switch_is_shop_scoped_and_neutral_when_disabled():
    source = read("src/Bot/ChromeNs/KnowledgePolicyProfileService.cs")
    assert 'EnabledSettingsKey = "knowledge.policy_reliability_enabled"' in source
    assert 'ShopScopedSettingsStore' in source
    assert 'public static bool IsEnabled(ShopContext shop = null)' in source
    assert 'Reason = "知识策略与可靠度已关闭' in source
    assert 'AllowDirect = true' in source


def test_policy_window_has_explicit_switch_and_full_import_export():
    source = read("src/Bot/Knowledge/KnowledgePolicyProfileUi.cs")
    assert 'Content = "启用知识策略与可靠度"' in source
    assert 'Content = "导入全部"' in source
    assert 'Content = "导出全部"' in source
    assert 'ImportKnowledgePolicies(this)' in source
    assert 'ExportKnowledgePolicies(this)' in source


def test_policy_full_export_contains_reliability_stats():
    source = read("src/Bot/Knowledge/RulePolicyImportExportUi.cs")
    for field in ["directSelectedCount", "contextualSelectedCount", "acceptedCount", "sellerCorrectionCount", "sellerWithdrawCount", "lastEvidenceType"]:
        assert field in source
    assert 'KnowledgePolicyProfileService.ImportCompleteProfile' in source


def test_knowledge_center_has_complete_package_import_export():
    center = read("src/Bot/Knowledge/KnowledgeCenterWindow.cs")
    io = read("src/Bot/Knowledge/RulePolicyImportExportUi.cs")
    assert '导入知识库完整包' in center
    assert '导出知识库完整包' in center
    assert 'KnowledgePackageSchema = "qianniu-ai-bot.knowledge-package"' in io
    assert 'BotFeatureStore.SaveKnowledgeBase(importedKnowledge)' in io
    assert '["policy"] = policy' in io
    assert '["settings"] = settings' in io


def test_dispatched_buyer_work_is_not_cancelled_by_next_buyer_message():
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    enqueue = coordinator[coordinator.index("public void Enqueue(BuyerMessageBurstItem item)"):coordinator.index("private bool HasPendingBuyerMessages")]
    assert "InvalidateDispatchedAnswerOnArrival" not in enqueue
    assert "HardCancelVersion" in coordinator
    assert "state.HardCancelVersion == capturedHardCancelVersion" in coordinator
    assert "ParallelReplyRelevanceGate.ShouldSend" in pipeline
    assert "允许作为补充答案发送" in pipeline


def test_order_empty_message_center_event_triggers_independent_probe():
    source = read("src/Bot/ChromeNs/OrderPaymentNotificationFallback.cs")
    assert 'ObserveGenericPaymentSignal(qn, "messageCenterNotify空载荷")' in source
    assert '已转入独立订单补扫' in source


def test_demonstrative_question_can_still_use_high_confidence_local_direct():
    source = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    assert "IsSelfContainedDemonstrativeQuestion" in source
    assert "selfContainedDemonstrative" in source
'''
write("tests/test_concurrent_reply_knowledge_backup_static.py", test)

print("patch applied")
