from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_shop_binding_exposes_api_and_ai_answer_chain_diagnostics():
    ui = read("src/Bot/Options/ShopBindingOptionsControl.cs")
    assert 'MakeButton("测试 API 连接"' in ui
    assert 'MakeButton("测试 AI 回答链路"' in ui
    assert "RunDiagnosticAsync(false)" in ui
    assert "RunDiagnosticAsync(true)" in ui
    assert "GetSavedTokenForDiagnostics" in ui
    assert "尚未保存的内容" in ui
    assert "不会向买家发送消息" in ui
    assert "ShopApiDiagnosticsService.TestConnectionAsync" in ui
    assert "ShopApiDiagnosticsService.TestAnswerChainAsync" in ui


def test_diagnostics_use_shop_auth_and_the_real_text_default_gateway():
    service = read("src/Bot/ShopScope/ShopApiDiagnosticsService.cs")
    assert '"/api/runtime/v1/config"' in service
    assert '"/v1/chat/completions"' in service
    assert 'new AuthenticationHeaderValue("Bearer", token.Trim())' in service
    assert '"X-Shop-Key", shop.ShopKey' in service
    assert '["model"] = "text-default"' in service
    assert 'root["qianniu_routing"]' in service
    assert 'root["choices"]' in service
    assert 'message["content"]' in service
    assert "供应商/模型/协议" in service
    assert "AI实际回复" in service


def test_diagnostics_are_dry_run_and_do_not_send_to_buyers():
    service = read("src/Bot/ShopScope/ShopApiDiagnosticsService.cs")
    ui = read("src/Bot/Options/ShopBindingOptionsControl.cs")
    assert "QNRpa" not in service
    assert "ReliableSend" not in service
    assert "send_text" not in service
    assert "/api/bot-web/messages/send" not in service
    assert "只做取答案诊断" in ui


def test_diagnostics_service_is_compiled_for_wpf_temp_projects():
    props = read("src/Bot/Directory.Build.props")
    assert "ShopScope\\ShopApiDiagnosticsService.cs" in props
