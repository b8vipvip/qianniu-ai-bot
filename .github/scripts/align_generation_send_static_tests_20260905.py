from pathlib import Path


def patch(path, replacements):
    p = Path(path)
    text = p.read_text(encoding="utf-8-sig")
    for old, new in replacements:
        count = text.count(old)
        if count != 1:
            raise SystemExit(f"{path}: expected one match for {old!r}, got {count}")
        text = text.replace(old, new, 1)
    p.write_text(text, encoding="utf-8")


patch("tests/test_bot_echo_markerless_delivery_static.py", [
    ('assert "await qn.SendTextWithRetryAsync(item.BuyerNick, answer, 3)" in deterministic',
     'assert "await qn.SendTextWithRetryAsync(item.BuyerNick, answer, 3, generationToken)" in deterministic'),
    ('assert "await SendTextWithRetryAsync(buyer, segment, retryCount)" in qn',
     'assert "await SendTextWithRetryAsync(buyer, segment, retryCount, cancellationToken)" in qn'),
])

patch("tests/test_bot_message_suffix_static.py", [
    ('gate_pos = block.index("await _sendGate.WaitAsync()")',
     'gate_pos = block.index("await _sendGate.WaitAsync(cancellationToken)")'),
])

patch("tests/test_first_inquiry_streaming_guard_static.py", [
    ('sender = service.index("qn.SendTextWithRetryAsync(item.BuyerNick, answer, 3)")',
     'sender = service.index("qn.SendTextWithRetryAsync(item.BuyerNick, answer, 3, generationToken)")'),
])

patch("tests/test_order_auto_reply_delay_static.py", [
    ("assert 'await _sendGate.WaitAsync();' in qn", "assert 'await _sendGate.WaitAsync(cancellationToken);' in qn"),
])

patch("tests/test_order_reply_template_placeholders_v3_static.py", [
    ("assert 'await SendTextWithRetryAsync(buyer, segment, retryCount)' in QN",
     "assert 'await SendTextWithRetryAsync(buyer, segment, retryCount, cancellationToken)' in QN"),
    ("split('await _sendGate.WaitAsync();', 1)[0]",
     "split('await _sendGate.WaitAsync(cancellationToken);', 1)[0]"),
])

patch("tests/test_recent_visual_context_and_ai_concurrency_static.py", [
    ('assert "await _sendGate.WaitAsync();" in qn[send_method:send_method + 2500]',
     'assert "await _sendGate.WaitAsync(cancellationToken);" in qn[send_method:send_method + 2500]'),
])

patch("tests/test_reply_transcript_prefix_sanitizer_static.py", [
    ('send = source.index("SendTextWithRetryAsync(burst.BuyerNick, answer, 1)")',
     'send = source.index("burst.BuyerNick, answer, 1, lease.CancellationToken")'),
])

patch("tests/test_server_push_watchdog_immediate_rules_static.py", [
    ('assert "SendTextWithRetryAsync(item.BuyerNick, answer, 3)" in deterministic',
     'assert "SendTextWithRetryAsync(item.BuyerNick, answer, 3, generationToken)" in deterministic'),
])

print("aligned legacy static tests with cancellation-aware reliable send")
