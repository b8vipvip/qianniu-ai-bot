from pathlib import Path

root = Path(__file__).resolve().parents[1]
qn_path = root / "src/Bot/ChromeNs/QN.cs"
text = qn_path.read_text(encoding="utf-8-sig")
old = 'burst.CombinedQuestion.Replace("\n", " | ")'
new = 'burst.CombinedQuestion.Replace("\\n", " | ")'
count = text.count(old)
if count != 1:
    raise RuntimeError("expected one broken multiline Replace expression, got %d" % count)
qn_path.write_text(text.replace(old, new, 1), encoding="utf-8-sig")
print("fixed QN burst log newline expression")
