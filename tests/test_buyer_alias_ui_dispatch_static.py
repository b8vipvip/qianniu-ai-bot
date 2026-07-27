from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_buyer_alias_timer_never_updates_wpf_controls_directly():
    source = text("src/Bot/ChromeNs/BuyerIdentityAliasUiBridge.cs")

    assert "new Timer(_ => QueueRefreshOnUiThread()" in source
    assert "Application.Current" in source
    assert "Dispatcher.CheckAccess()" in source
    assert "Dispatcher.BeginInvoke" in source
    assert "DispatcherPriority.Background" in source
    assert "Interlocked.Exchange(ref _refreshQueued, 1)" in source
    assert "RefreshOnUiThread();" in source
    assert "ctl.ReShowAfterQNChange();" in source


def test_buyer_alias_ui_refresh_verifies_ctl_dispatcher_access():
    source = text("src/Bot/ChromeNs/BuyerIdentityAliasUiBridge.cs")

    assert "if (!ctl.Dispatcher.CheckAccess())" in source
    assert "买家昵称别名UI刷新未在CtlRobot所属Dispatcher执行" in source
