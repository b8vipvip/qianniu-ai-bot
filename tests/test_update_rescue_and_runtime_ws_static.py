from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_release_publishes_version_bound_rescue_updater():
    rescue = read("scripts/qianniu-bot-rescue-update.ps1")
    workflow = read(".github/workflows/publish-bot-auto-update-release.yml")

    assert "__QIANNIU_TARGET_VERSION__" in rescue
    assert "__QIANNIU_PACKAGE_URL__" in rescue
    assert "__QIANNIU_SHA256__" in rescue
    assert "BotAutoUpdater.ps1" in rescue
    assert "PowerShell 5.1" in rescue
    assert "Get-FileHash" in rescue
    assert "release-info.json" in rescue
    assert "Refusing" not in rescue  # user-facing rescue messages stay Chinese
    assert "qianniu-bot-rescue-update.ps1" in workflow
    assert "__QIANNIU_TARGET_VERSION__" in workflow
    assert "__QIANNIU_PACKAGE_URL__" in workflow
    assert "__QIANNIU_SHA256__" in workflow


def test_runtime_injection_uses_owned_websocket_namespace():
    inject = read("src/Bin/inject.js")
    qn_inject = read("src/Bot/Common/QNInject.cs")

    marker = "20260819-runtime-ws-v10"
    assert marker in inject
    assert marker in qn_inject
    assert "window.__qnbotWebSocket" in inject
    assert "socket.__qnbotOwned = true" in inject
    assert "function qnbotSocket()" in inject
    assert "var old = qnbotSocket();" in inject
    assert "if (window.__qnbotWebSocket === socket) window.__qnbotWebSocket = null;" in inject
    assert "function socketOpen() { return window.chatWebsocket" not in inject


def test_bootstrap_awaits_injection_and_starts_bounded_listener_self_heal():
    bootstrap = read("src/Bot/StartUp/BootStrap.cs")
    targets = read("src/Directory.Build.targets")
    repair = read("src/Bot/ChromeNs/QnStartupConnectionSelfHeal.cs")

    assert "await QNInject.StartInject();" in bootstrap
    assert "QnStartupConnectionSelfHeal.Start();" in bootstrap
    assert "ChromeNs\\QnStartupConnectionSelfHeal.cs" in targets
    assert "GetActiveTcpListeners" in repair
    assert "MyWebSocketServer.WSocketSvrInst.Start();" in repair
    assert "WebSocketSessionCount > 0" in repair
    assert "未自动重启千牛" in repair
    assert "QianniuRecoveryManager.RequestRecover" not in repair
