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
path.write_text(text[:start] + new_block + text[end:], encoding="utf-8")
print("patched Directory.Build.targets insertion anchor")
