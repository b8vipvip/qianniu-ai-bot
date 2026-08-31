from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TARGETS = ROOT / "src/Directory.Build.targets"
DIRECT_TEST = ROOT / "tests/test_direct_order_and_visual_learning_static.py"
CDP_TEST = ROOT / "tests/test_long_running_cdp_runtime_static.py"
RUNTIME_TEST = ROOT / "tests/test_runtime_stability_1077_static.py"


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, got {count}")
    return text.replace(old, new, 1)


# Legacy Bot.csproj uses explicit Compile items through Directory.Build.targets. The new
# partial must be included for both the real project and WPF-generated temporary projects.
t = TARGETS.read_text(encoding="utf-8-sig")
t = replace_once(
    t,
    '''  <ItemGroup Condition="Exists('$(MSBuildProjectDirectory)\\ChromeNs\\QN.MessageRecovery.cs')">\n    <Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\QN.MessageRecovery.cs" />\n  </ItemGroup>''',
    '''  <ItemGroup Condition="Exists('$(MSBuildProjectDirectory)\\ChromeNs\\QN.MessageRecovery.cs')">\n    <Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\QN.MessageRecovery.cs" />\n  </ItemGroup>\n  <ItemGroup Condition="Exists('$(MSBuildProjectDirectory)\\ChromeNs\\QN.DeliveryVerification.cs')">\n    <Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\QN.DeliveryVerification.cs" />\n  </ItemGroup>''',
    "include delivery verification partial")
TARGETS.write_text(t, encoding="utf-8")

# Recovery now uses an alias-aware helper instead of repeating the older literal role check.
t = DIRECT_TEST.read_text(encoding="utf-8-sig")
t = replace_once(
    t,
    '''    assert "IsBuyerMessage(m)" in recovery\n    assert "|| IsPotentialRecoveredOrderCard(m)" in recovery''',
    '''    assert "IsRecoveredBuyerMessageForTarget(m, seller, buyer)" in recovery\n    assert "BuyerIdentityAliasService.AreEquivalent" in recovery\n    assert "|| IsPotentialRecoveredOrderCard(m)" in recovery''',
    "refresh recovery regression expectation")
DIRECT_TEST.write_text(t, encoding="utf-8")

# The old CDP regression asserted that the physical source of a forwarded duplicate page
# became the runtime command session. That is exactly the behavior this stability fix removes:
# duplicate pages are ingress-only and the authoritative QN-owned session remains command owner.
t = CDP_TEST.read_text(encoding="utf-8-sig")
t = replace_once(
    t,
    '''def test_precise_conversation_change_selects_runtime_command_webview():\n    source = read("src/Bot/ChromeNs/CDPClient.cs")\n\n    assert 'PreferRuntimeSession(sellerNick, physicalSourceSession, buyerNick, "onConversationChange")' in source\n    assert "ResolvePreferredRuntimeClient" in source\n    assert 'desc + "@runtime-active-session"' in source\n    assert "活动CDP会话失效，已撤销会话偏好并回退权威通道" in source''',
    '''def test_precise_conversation_change_keeps_authoritative_runtime_command_webview():\n    source = read("src/Bot/ChromeNs/CDPClient.cs")\n\n    assert 'PreferRuntimeSession(sellerNick, SessionId, buyerNick, "onConversationChange")' in source\n    assert 'PreferRuntimeSession(sellerNick, physicalSourceSession, buyerNick, "onConversationChange")' not in source\n    assert "ResolvePreferredRuntimeClient" in source\n    assert 'desc + "@runtime-active-session"' in source\n    assert "活动CDP会话失效，已撤销会话偏好并回退权威通道" in source''',
    "authoritative runtime session regression")
t = replace_once(
    t,
    '''def test_forwarded_conversation_change_preserves_physical_source_without_rebinding_qn():\n    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")\n    client = read("src/Bot/ChromeNs/CDPClient.cs")\n\n    assert "CDPClient.BeginForwardedInbound(item.SourceSession)" in bridge\n    assert "target.DispatchInboundEvent(item.Type, item.Response);" in bridge\n    assert "SetActiveConversationByNick" not in bridge\n    assert "qn.CDP =" not in bridge\n    assert "ForwardedInboundSourceSession" in client\n    assert "physicalSourceSession = (ForwardedInboundSourceSession.Value" in client''',
    '''def test_forwarded_conversation_change_is_ingress_only_and_cannot_rebind_runtime_qn():\n    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")\n    client = read("src/Bot/ChromeNs/CDPClient.cs")\n\n    assert "CDPClient.BeginForwardedInbound(item.SourceSession)" in bridge\n    assert "target.DispatchInboundEvent(item.Type, item.Response);" in bridge\n    assert "SetActiveConversationByNick" not in bridge\n    assert "qn.CDP =" not in bridge\n    assert "ForwardedInboundSourceSession" in client\n    assert "physicalSourceSession = (ForwardedInboundSourceSession.Value" not in client\n    assert 'PreferRuntimeSession(sellerNick, SessionId, buyerNick, "onConversationChange")' in client''',
    "forwarded duplicate ingress-only regression")
CDP_TEST.write_text(t, encoding="utf-8")

r = RUNTIME_TEST.read_text(encoding="utf-8-sig")n