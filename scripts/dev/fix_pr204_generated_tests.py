from pathlib import Path

root = Path(__file__).resolve().parents[2]
path = root / "tests/test_order_preset_manual_segment_continuation_static.py"
text = path.read_text(encoding="utf-8-sig")
bad = '    assert "continue;" in source[source.index("result.SatisfiedSegments++"):source.index("Log.Info(\\"下单固定预设分段强制自动发送") ]\n'
good = '''    satisfied_at = source.index("result.SatisfiedSegments++")
    send_log_at = source.index('Log.Info("下单固定预设分段强制自动发送', satisfied_at)
    assert "continue;" in source[satisfied_at:send_log_at]
'''
if bad not in text:
    raise RuntimeError("generated manual-segment assertion shape not found")
path.write_text(text.replace(bad, good, 1), encoding="utf-8", newline="\n")
print("generated manual segment test repaired")
