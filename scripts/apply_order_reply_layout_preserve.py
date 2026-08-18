from pathlib import Path

p = Path('src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs')
s = p.read_text(encoding='utf-8-sig')
old = '''                var answer = BotOutboundMessageFormatter.EnsureAiMarker(\n                    BotFeatureStore.ApplyOutputPolicy(resolution.Reply));'''
new = '''                var preserveTemplateLayout = !string.IsNullOrWhiteSpace(resolution.Source)\n                    && (resolution.Source.IndexOf("固定预设", StringComparison.Ordinal) >= 0\n                        || resolution.Source.IndexOf("接口失败兜底", StringComparison.Ordinal) >= 0);\n                var rawReply = resolution.Reply ?? string.Empty;\n                var answer = preserveTemplateLayout\n                    ? (Regex.IsMatch(rawReply, @"(?:\\[AI\\]|【AI】|［AI］)\\s*$", RegexOptions.IgnoreCase)\n                        ? rawReply\n                        : rawReply + " [AI]")\n                    : BotOutboundMessageFormatter.EnsureAiMarker(\n                        BotFeatureStore.ApplyOutputPolicy(rawReply));'''
if s.count(old) != 1:
    raise SystemExit(f'answer formatter block occurrence={s.count(old)}')
s = s.replace(old, new, 1)
p.write_text(s, encoding='utf-8-sig')

t = Path('tests/test_order_reply_template_placeholders_v3_static.py')
ts = t.read_text(encoding='utf-8')
append = '''\n\ndef test_fixed_template_bypasses_output_policy_to_preserve_layout():\n    assert 'var preserveTemplateLayout' in SERVICE\n    assert 'resolution.Source.IndexOf("固定预设", StringComparison.Ordinal)' in SERVICE\n    assert 'resolution.Source.IndexOf("接口失败兜底", StringComparison.Ordinal)' in SERVICE\n    preserve = SERVICE.split('var preserveTemplateLayout', 1)[1].split('string duplicateReason;', 1)[0]\n    assert 'rawReply + " [AI]"' in preserve\n'''
if 'test_fixed_template_bypasses_output_policy_to_preserve_layout' not in ts:
    ts += append
t.write_text(ts, encoding='utf-8')
print('layout-preserve patch applied')
