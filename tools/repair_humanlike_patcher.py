from pathlib import Path

path = Path(__file__).with_name("apply_humanlike_reply_v2.py")
text = path.read_text(encoding="utf-8")
old = """replace_once(
    "src/Directory.Build.targets",
    '''  <ItemGroup Condition="Exists('$(MSBuildProjectDirectory)\\ChromeNs\\ReplyDeduplicationService.cs')">
    <Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\ReplyDeduplicationService.cs" />
  </ItemGroup>
</Project>''',
    '''  <ItemGroup Condition="Exists('$(MSBuildProjectDirectory)\\ChromeNs\\ReplyDeduplicationService.cs')">
    <Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\ReplyDeduplicationService.cs" />
  </ItemGroup>
  <ItemGroup Condition="Exists('$(MSBuildProjectDirectory)\\ChromeNs\\BuyerMessageBurstCoordinator.cs')">
    <Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\BuyerMessageBurstCoordinator.cs" />
  </ItemGroup>
</Project>''')"""
new = """replace_once(
    "src/Directory.Build.targets",
    '''</Project>''',
    '''  <ItemGroup Condition="Exists('$(MSBuildProjectDirectory)\\ChromeNs\\BuyerMessageBurstCoordinator.cs')">
    <Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\BuyerMessageBurstCoordinator.cs" />
  </ItemGroup>
</Project>''')"""
count = text.count(old)
if count != 1:
    raise RuntimeError("expected one Directory.Build.targets patch block, got %d" % count)
path.write_text(text.replace(old, new, 1), encoding="utf-8")
print("patched Directory.Build.targets insertion anchor")
