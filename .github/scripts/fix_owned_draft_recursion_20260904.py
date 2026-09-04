from pathlib import Path

q_path = Path("src/Bot/ChromeNs/QNRpa.cs")
q = q_path.read_text(encoding="utf-8-sig")
old = '''        private void ForgetOwnedDraft()
        {
            _lastOwnedDraftBuyer = string.Empty;
            _lastOwnedDraftText = string.Empty;
            _lastOwnedDraftAt = DateTime.MinValue;
            ForgetOwnedDraft();
        }
'''
new = '''        private void ForgetOwnedDraft()
        {
            _lastOwnedDraftBuyer = string.Empty;
            _lastOwnedDraftText = string.Empty;
            _lastOwnedDraftAt = DateTime.MinValue;
            LastSetPlainText = string.Empty;
            LatestSetTextTime = DateTime.MinValue;
        }
'''
if q.count(old) != 1:
    raise SystemExit(f"ForgetOwnedDraft recursion shape changed: {q.count(old)}")
q = q.replace(old, new, 1)
q_path.write_text(q, encoding="utf-8")

test_path = Path("tests/test_1196_followup_runtime_hardening_static.py")
t = test_path.read_text(encoding="utf-8-sig")
needle = '''    assert "OwnedDraftRetention = TimeSpan.FromMinutes(30)" in text
'''
# The ownership test lives in another file; append a focused source-level recursion guard here.
append = '''\n\ndef test_owned_draft_forget_helper_clears_state_without_self_recursion():
    q = read("src/Bot/ChromeNs/QNRpa.cs")
    helper = q[q.index("private void ForgetOwnedDraft()"):q.index("private bool IsOwnedDraftForBuyer", q.index("private void ForgetOwnedDraft()"))]
    assert helper.count("ForgetOwnedDraft();") == 0
    assert "LastSetPlainText = string.Empty;" in helper
    assert "LatestSetTextTime = DateTime.MinValue;" in helper
'''
if "test_owned_draft_forget_helper_clears_state_without_self_recursion" not in t:
    t += append
test_path.write_text(t, encoding="utf-8")
print("owned draft recursion corrected")
