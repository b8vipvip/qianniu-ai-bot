from pathlib import Path

path = Path(__file__).with_name("apply_humanlike_reply_v2.py")
text = path.read_text(encoding="utf-8")

start_marker = 'replace_once(\n    "src/Directory.Build.targets",'
start = text.find(start_marker)
if start < 0:
    raise RuntimeError("Directory.Build.targets patch start marker not found")
end = text.find("\n\nreplace_once(", start + len(start_marker))
if end < 0:
    raise RuntimeError("Directory.Build.targets patch end marker not found")
new_block = """replace_once(
    "src/Directory.Build.targets",
    '''</Project>''',
    '''  <ItemGroup Condition="Exists('$(MSBuildProjectDirectory)\\ChromeNs\\BuyerMessageBurstCoordinator.cs')">
    <Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\BuyerMessageBurstCoordinator.cs" />
  </ItemGroup>
</Project>''')"""
text = text[:start] + new_block + text[end:]

old_source = """            var source = deduplication.Regenerated && !string.IsNullOrWhiteSpace(deduplication.Source)
                ? deduplication.Source
                : KnowledgeLearningService.ResolveAnswerSource(
                    burst.SellerNick,
                    burst.BuyerNick,
                    burst.CombinedQuestion,
                    answer);
            if (string.IsNullOrWhiteSpace(source)) source = "AI生成";"""
new_source = """            var source = deduplication.Regenerated && !string.IsNullOrWhiteSpace(deduplication.Source)
                ? deduplication.Source
                : "AI生成";"""
count = text.count(old_source)
if count != 1:
    raise RuntimeError("expected one visual answer source block, got %d" % count)
text = text.replace(old_source, new_source, 1)

for target in (
    'assert "买家消息：\\\\n" in bridge',
    'assert "买家消息：\\\\n" in handoff',
):
    replacement = target.replace("\\\\n", "\\\\\\\\n")
    count = text.count(target)
    if count != 1:
        raise RuntimeError("expected one literal newline assertion %r, got %d" % (target, count))
    text = text.replace(target, replacement, 1)

path.write_text(text, encoding="utf-8")
print("patched build insertion, source badge and literal newline tests")
