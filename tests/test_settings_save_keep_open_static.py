from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def method_body(code: str, method_name: str, next_method_name: str) -> str:
    start = code.index(method_name)
    end = code.index(next_method_name, start)
    return code[start:end]


def test_save_persists_visited_pages_without_closing_settings_window():
    code = read("src/Bot/Options/WndOption.xaml.cs")
    save = method_body(code, "private void Save(string seller)", "private void btnCancel_Click")

    assert "RunInShopScope(delegate" in save
    assert "options.Save(seller);" in save
    assert "Hide();" not in save
    assert "Close();" not in save
    assert "DialogResult" not in save
    assert "设置已保存，保留设置窗口继续编辑" in save


def test_save_button_still_calls_save_and_explicit_cancel_still_closes():
    code = read("src/Bot/Options/WndOption.xaml.cs")
    click = method_body(code, "private void sbSave_Click", "private void Save(string seller)")
    cancel = method_body(code, "private void btnCancel_Click", "private void btnRestoreCurrentPageToDef_Click")

    assert "Save(Seller);" in click
    assert "Close();" in cancel


def test_save_failure_keeps_window_available_for_correction():
    code = read("src/Bot/Options/WndOption.xaml.cs")
    save = method_body(code, "private void Save(string seller)", "private void btnCancel_Click")

    assert "Show();" in save
    assert "Activate();" in save
    assert "窗口已保留，请修正后重试" in save
