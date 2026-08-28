from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SERVICE = ROOT / "services" / "api-control-plane"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def test_server_source_update_uses_github_ssh_443_with_retry_and_keepalive():
    script = read(ROOT / "scripts" / "update-api-control-plane.sh")
    assert 'GITHUB_SSH_HOST="${GITHUB_SSH_HOST:-ssh.github.com}"' in script
    assert 'GITHUB_SSH_PORT="${GITHUB_SSH_PORT:-443}"' in script
    assert 'GIT_FETCH_RETRIES="${GIT_FETCH_RETRIES:-5}"' in script
    assert "ServerAliveInterval" in script
    assert "ServerAliveCountMax" in script
    assert "github_fetch_with_retry" in script
    assert "timeout --foreground" in script
    assert "git -C \"$REPO_DIR\" fetch --prune origin \"$BRANCH\"" in script


def test_client_package_download_keeps_partial_and_resumes_with_range():
    progress = read(SERVICE / "bot_update_progress.py")
    assert 'headers["Range"] = f"bytes={existing}-"' in progress
    assert 'mode = "ab" if append else "wb"' in progress
    assert 'phase="retrying"' in progress
    assert 'phase="resuming" if existing > 0 else "connecting"' in progress
    assert "DOWNLOAD_MAX_ATTEMPTS" in progress
    assert "DOWNLOAD_RETRY_BASE_SECONDS" in progress
    assert "speed_bps" in progress
    assert "eta_seconds" in progress
    # A network exception must not unconditionally delete the partial file. Corrupt/oversized
    # content may be discarded, but retry handling records its size and resumes it.
    assert "partial_size = partial.stat().st_size if partial.is_file() else 0" in progress


def test_github_metadata_and_release_support_optional_proxy():
    progress = read(SERVICE / "bot_update_progress.py")
    admin = read(SERVICE / "version_update_admin.py")
    env = read(SERVICE / ".env.example")
    compose = read(SERVICE / "docker-compose.bt.yml")
    assert 'GITHUB_PROXY = os.getenv("BOT_UPDATE_GITHUB_PROXY", "").strip()' in progress
    assert '"proxies": {"http": GITHUB_PROXY, "https": GITHUB_PROXY}' in progress
    assert "bot_update_cache._fetch_json = _fetch_json_resilient" in progress
    assert "_request_proxy_kwargs()" in admin
    assert "BOT_UPDATE_GITHUB_PROXY=" in env
    assert "host.docker.internal:1080" in env
    assert '"host.docker.internal:host-gateway"' in compose


def test_version_update_console_exposes_retry_speed_eta_and_transport():
    js = read(SERVICE / "static" / "version-update.js")
    assert "Git SSH 443" in js
    assert "HTTP Range 断点续传" in js
    assert "网络重试中" in js
    assert "speed_bps" in js
    assert "eta_seconds" in js
    assert "retry_in_seconds" in js
    assert "HTTPS 代理已启用" in js


def test_host_agent_reports_git_network_transport():
    agent = read(ROOT / "scripts" / "api-control-plane-update-agent.sh")
    installer = read(ROOT / "scripts" / "install-api-control-plane-update-agent.sh")
    assert "'git_transport':'ssh-443'" in agent
    assert "'git_fetch_attempts':int(sys.argv[4])" in agent
    assert "Environment=GITHUB_SSH_HOST=" in installer
    assert "Environment=GITHUB_SSH_PORT=" in installer
    assert "Environment=GIT_FETCH_RETRIES=" in installer
    assert "flock timeout" in installer
