from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_rescue_updater_bypasses_broken_legacy_client_bootstrap():
    rescue = read("scripts/qianniu-bot-rescue-update.ps1")

    assert "https://api.github.com/repos/b8vipvip/qianniu-ai-bot/releases/latest" in rescue
    assert "update.json" in rescue
    assert "qianniu-bot-x64.zip" in rescue
    assert "BotAutoUpdater.ps1" in rescue
    assert "Windows PowerShell 5.1" in rescue
    assert "Get-FileHash" in rescue
    assert "release-info.json" in rescue
    assert "绕过旧 Bot 更新器执行救援安装" in rescue
    assert "Start-Process -FilePath 'powershell.exe' -ArgumentList $args -PassThru -Wait" in rescue
    assert "ExpectedVersion" in rescue
    assert "ExpectedSha256" in rescue
    assert "-Verb RunAs" in rescue


def test_bootstrap_awaits_injection_and_starts_bounded_listener_self_heal():
    bootstrap = read("src/Bot/StartUp/BootStrap.cs")
    props = read("src/Bot/Directory.Build.props")
    repair = read("src/Bot/Update/BotUpdateStartupConnection.Fast.cs")

    assert "await QNInject.StartInject();" in bootstrap
    assert "QnStartupConnectionSelfHeal.Start();" in bootstrap
    assert "Update\\BotUpdate*.Fast.cs" in props
    assert "GetActiveTcpListeners" in repair
    assert "MyWebSocketServer.WSocketSvrInst.Start();" in repair
    assert "WebSocketSessionCount > 0" in repair
    assert "未自动重启千牛" in repair
    assert "QianniuRecoveryManager.RequestRecover" not in repair


def test_self_heal_checks_actual_listener_instead_of_trusting_injection_marker():
    repair = read("src/Bot/Update/BotUpdateStartupConnection.Fast.cs")
    qn_inject = read("src/Bot/Common/QNInject.cs")

    assert "IsLoopbackListenerActive" in repair
    assert "endpoint.Port == WebSocketPort" in repair
    assert "127.0.0.1:41010 未监听" in repair
    assert "WebSocketSessionCount > 0" in repair
    assert "needInjectPaths.Count < 1" in qn_inject
    assert "千牛注入已是最新版本" in qn_inject
