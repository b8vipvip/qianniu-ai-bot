from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def test_update_agent_initializes_temp_after_path_assignment():
    text = (ROOT / "scripts" / "api-control-plane-update-agent.sh").read_text(encoding="utf-8")
    assert 'local path="$1" payload="$2"\n  local temp="${path}.tmp.$$"' in text
    assert 'local path="$1" payload="$2" temp="${path}.tmp.$$"' not in text
