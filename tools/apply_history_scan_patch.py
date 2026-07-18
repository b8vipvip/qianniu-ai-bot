from pathlib import Path
import base64
import gzip

ROOT = Path(__file__).resolve().parents[1]
PAYLOAD_DIR = ROOT / "tools" / "history_scan_payload"

FILES = {
    "ChatHistoryScanService_cs": "src/Bot/Knowledge/ChatHistoryScanService.cs",
    "ChatHistoryScanWindow_cs": "src/Bot/Knowledge/ChatHistoryScanWindow.cs",
    "KnowledgeManagerControl_cs": "src/Bot/Knowledge/KnowledgeManagerControl.cs",
    "test_history_scan_static_py": "tests/test_history_scan_static.py",
}

for prefix, relative in FILES.items():
    parts = sorted(PAYLOAD_DIR.glob(prefix + ".*.part"))
    if not parts:
        raise SystemExit("missing payload parts: " + prefix)
    payload = "".join(part.read_text(encoding="utf-8").strip() for part in parts)
    data = gzip.decompress(base64.b64decode(payload))
    path = ROOT / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)

project = ROOT / "src/Bot/Bot.csproj"
text = project.read_text(encoding="utf-8-sig")
needle = '    <Compile Include="Knowledge\\KnowledgeAiService.cs" />\n'
insert = (
    needle
    + '    <Compile Include="Knowledge\\ChatHistoryScanService.cs" />\n'
    + '    <Compile Include="Knowledge\\ChatHistoryScanWindow.cs" />\n'
)
if 'Knowledge\\ChatHistoryScanService.cs' not in text:
    if needle not in text:
        raise SystemExit("Bot.csproj knowledge compile anchor not found")
    text = text.replace(needle, insert, 1)
project.write_text(text, encoding="utf-8-sig")

print("history scan patch applied")
