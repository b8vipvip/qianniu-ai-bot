from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def edit(path, old, new):
    p = ROOT / path
    s = p.read_text(encoding="utf-8-sig")
    if s.count(old) != 1:
        raise RuntimeError(f"{path}: expected one match, got {s.count(old)}")
    p.write_text(s.replace(old, new, 1), encoding="utf-8")

edit(
    "src/Bot/Knowledge/RulePolicyImportExportUi.cs",
    '''                if (root["enabled"] != null)\n                {\n                    bool enabled;\n                    if (bool.TryParse(Convert.ToString(root["enabled"]), out enabled))\n                        KnowledgePolicyProfileService.SetEnabled(ShopSettingsScope.Current, enabled);\n                }''',
    '''                if (root["enabled"] != null)\n                {\n                    bool enabled;\n                    var policyShop = GetField<ShopContext>(window, "_shop") ?? ShopSettingsScope.Current;\n                    if (policyShop != null && bool.TryParse(Convert.ToString(root["enabled"]), out enabled))\n                        KnowledgePolicyProfileService.SetEnabled(policyShop, enabled);\n                }''')

edit(
    "src/Bot/Knowledge/RulePolicyImportExportUi.cs",
    '''                KnowledgeLearningService.NotifyKnowledgeBaseChanged();\n                Log.Info("知识库完整包已导入: file="''',
    '''                Log.Info("知识库完整包已导入: file="''')

print("follow-up fixes applied")
