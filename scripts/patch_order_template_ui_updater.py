from pathlib import Path


def replace_once(path: Path, old: str, new: str) -> None:
    text = path.read_text(encoding="utf-8-sig")
    if new in text:
        return
    if old not in text:
        raise SystemExit(f"expected block not found in {path}")
    path.write_text(text.replace(old, new, 1), encoding="utf-8-sig")


app = Path("src/Bot/App.xaml.cs")
replace_once(
    app,
    "            OrderAttentionSettings.Initialize();\n            SelectableSettingsText.Initialize();",
    "            OrderAttentionSettings.Initialize();\n"
    "            // Explicitly initialize order-template runtime/UI hooks. A never-read static field on a\n"
    "            // partial App type is not guaranteed to run because of beforefieldinit semantics.\n"
    "            OrderTemplateRequiredFieldsV2.InitializeForApp();\n"
    "            SelectableSettingsText.Initialize();",
)
replace_once(
    app,
    '''        private static readonly string[] OrderPlaceholders =\n        {\n            "{客服}",\n            "{买家}",\n            "{订单号}",\n            "{时间}",\n            "{商品}",\n            "{规格}",\n            "{数量}",\n            "{金额}",\n            "{实付}",\n            "{订单状态}"\n        };''',
    '''        private static readonly string[] OrderPlaceholders =\n        {\n            "{客服}",\n            "{买家}",\n            "{订单号}",\n            "{时间}",\n            "{商品}",\n            "{sku}",\n            "{数量}",\n            "{金额}",\n            "{实付}",\n            "{订单状态}",\n            "{买家备注}",\n            "{分段符}"\n        };''',
)
replace_once(
    app,
    '''        private static bool ShouldEnhance(TextBlock source)\n        {\n            if (source == null || string.IsNullOrWhiteSpace(source.Text)) return false;\n            if (PlaceholderRegex.IsMatch(source.Text)) return true;''',
    '''        private static bool ShouldEnhance(TextBlock source)\n        {\n            if (source == null || string.IsNullOrWhiteSpace(source.Text)) return false;\n            // The order-template hint is owned by OrderTemplateSkuUiMigration so its blue clickable\n            // placeholders can insert at the answer TextBox caret. Do not replace it with copy-only UI.\n            if (IsOrderTemplateHint(source.Text)) return false;\n            if (PlaceholderRegex.IsMatch(source.Text)) return true;''',
)
replace_once(
    app,
    '''        private static bool IsMutedColor(Color color)\n        {''',
    '''        private static bool IsOrderTemplateHint(string text)\n        {\n            text = text ?? string.Empty;\n            return text.IndexOf("支持 {客服}", StringComparison.Ordinal) >= 0\n                && text.IndexOf("接口失败", StringComparison.Ordinal) >= 0;\n        }\n\n        private static bool IsMutedColor(Color color)\n        {''',
)

updater = Path("src/Bot/Update/BotAutoUpdater.ps1")
replace_once(
    updater,
    'throw "Backup validation failed for $Label: entry count differs ($($sourceState.Count) != $($backupState.Count))."',
    'throw "Backup validation failed for ${Label}: entry count differs ($($sourceState.Count) != $($backupState.Count))."',
)

print("patched order-template UI bootstrap/conflict and Windows PowerShell 5.1 updater syntax")
