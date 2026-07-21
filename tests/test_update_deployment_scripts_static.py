from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_control_plane_image_contains_runtime_streaming_guard_and_ci_starts_container():
    dockerfile = read("services/api-control-plane/Dockerfile")
    workflow = read(".github/workflows/api-control-plane-ci.yml")

    assert "runtime_streaming_guard.py" in dockerfile
    assert 'CMD ["python", "bootstrap.py"]' in dockerfile
    assert "Smoke test Ubuntu container startup" in workflow
    assert "docker run -d --name qianniu-api-control-plane-smoke" in workflow
    assert "curl -fsS http://127.0.0.1:18081/healthz" in workflow


def test_server_updater_preserves_data_uses_master_and_verifies_baota_domain():
    script = read("scripts/update-api-control-plane.sh")

    assert 'REPO_URL="${REPO_URL:-git@github.com:b8vipvip/qianniu-ai-bot.git}"' in script
    assert 'REPO_DIR="${REPO_DIR:-/opt/qianniu-ai-bot}"' in script
    assert 'LEGACY_DIR="${LEGACY_DIR:-/opt/qianniu-api-control-plane}"' in script
    assert 'BRANCH="${BRANCH:-master}"' in script
    assert "data.tar.gz" in script
    assert "old-git-commit.txt" in script
    assert "docker-compose.bt.yml" in script
    assert "bootstrap.py" in script
    assert "https://aboter.mv3.cn/healthz" in script
    assert "rollback" in script


def test_windows_updater_backs_up_persistent_data_and_refuses_source_repo_overwrite():
    script = read("scripts/update-bot.ps1")

    assert "Get-RunningBotInstallDir" in script
    assert "QianniuAiBot\\data" in script
    assert "Get-FileHash" in script
    assert "Expand-Archive" in script
    assert "Bin\\Bot.exe" in script
    assert "Join-Path $InstallDir '.git'" in script
    assert "自动回滚" in script


def test_update_manual_records_current_production_environment():
    manual = read("docs/UPDATE_AND_DEPLOY.md")

    assert "https://aboter.mv3.cn" in manual
    assert "/opt/qianniu-ai-bot" in manual
    assert "http://127.0.0.1:18081" in manual
    assert "scripts/update-api-control-plane.sh" in manual
    assert "scripts\\update-bot.ps1" in manual
    assert "%LocalAppData%\\QianniuAiBot\\data" in manual
