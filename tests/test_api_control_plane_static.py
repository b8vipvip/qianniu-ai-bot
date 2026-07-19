from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

# Keep the cleaned Windows client thin and the Ubuntu service authoritative.
def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_bot_api_page_only_exposes_control_plane_connection():
    xaml = text("src/Bot/Options/CtlRobotOptions.xaml")
    code = text("src/Bot/Options/CtlRobotOptions.ControlPlane.cs")
    assert "统一 API 服务" in xaml
    assert "客户端令牌" in xaml
    assert "打开 API 管理后台" in xaml
    assert "Visibility=\"Collapsed\"" in xaml
    assert "服务端控制面" in code
    assert "ControlPlaneClientToken" in code
    assert "AiEndpointStore.SaveEndpoints(new[] { endpoint })" in code


def test_control_plane_owns_upstream_testing_and_routing():
    app = text("services/api-control-plane/app.py")
    for value in [
        'impersonate="chrome"',
        '"responses_text"',
        '"chat_text"',
        '"legacy_text"',
        '"responses_vision"',
        '"chat_vision"',
        "backup_text_models_json",
        "auto_test_interval_hours",
        "protocol_candidates",
        "model_candidates",
        '@app.post("/v1/chat/completions")',
        '@app.post("/v1/responses")',
    ]:
        assert value in app


def test_upstream_keys_stay_on_server():
    bot = text("src/Bot/Options/CtlRobotOptions.ControlPlane.cs")
    server = text("services/api-control-plane/app.py")
    assert "上游供应商密钥不会保存在本机" in bot
    assert "api_key_cipher" in server
    assert "encrypt_secret" in server
    assert "decrypt_secret" in server
