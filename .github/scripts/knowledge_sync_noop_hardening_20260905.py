from pathlib import Path

bridge_path = Path('src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs')
text = bridge_path.read_text(encoding='utf-8-sig')
old = '''                    KnowledgeV2Record existing = null;
                    if (!string.IsNullOrWhiteSpace(entry.Id))
                        byId.TryGetValue(entry.Id, out existing);

                    KnowledgePolicyProfile profile = null;
'''
new = '''                    KnowledgeV2Record existing = null;
                    if (!string.IsNullOrWhiteSpace(entry.Id))
                        byId.TryGetValue(entry.Id, out existing);
                    if (existing != null
                        && Same(existing.Answer, entry.Answer)
                        && KnowledgeV2AuthorityPolicy.IsPersistedStateSynchronized(existing, entry))
                    {
                        continue;
                    }

                    KnowledgePolicyProfile profile = null;
'''
if text.count(old) != 1:
    raise SystemExit('bridge import target changed; refusing broad patch')
text = text.replace(old, new, 1)

anchor = '''        public static void ApplyImportedLegacyProvenance(KnowledgeV2Record record, KnowledgeBaseEntry entry)
        {
'''
method = '''        public static bool IsPersistedStateSynchronized(KnowledgeV2Record record, KnowledgeBaseEntry entry)
        {
            if (record == null || entry == null) return false;
            var expectedSource = string.IsNullOrWhiteSpace(entry.SourceType) ? "legacy_learning" : entry.SourceType.Trim();
            if (!string.Equals((record.SourceType ?? string.Empty).Trim(), expectedSource, StringComparison.OrdinalIgnoreCase))
                return false;
            if (record.Enabled != entry.Enabled) return false;

            if (IsExplicitHumanConfirmationSource(expectedSource))
            {
                return string.Equals(record.Status, "active", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(record.Type, "learning_candidate", StringComparison.OrdinalIgnoreCase)
                    && record.Authority >= 0.98
                    && record.Confidence >= 0.94;
            }

            return string.Equals(record.Status, "candidate", StringComparison.OrdinalIgnoreCase)
                && string.Equals(record.Type, "learning_candidate", StringComparison.OrdinalIgnoreCase);
        }

'''
if text.count(anchor) != 1:
    raise SystemExit('authority policy anchor changed; refusing broad patch')
text = text.replace(anchor, method + anchor, 1)
bridge_path.write_text(text, encoding='utf-8')

test_path = Path('tests/test_1272_knowledge_authority_and_turn_lifecycle_static.py')
t = test_path.read_text(encoding='utf-8-sig')
addition = '''\n\ndef test_v2_learning_bridge_skips_already_synchronized_unchanged_entries():\n    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs")\n    assert "IsPersistedStateSynchronized(existing, entry)" in bridge\n    assert "public static bool IsPersistedStateSynchronized" in bridge\n    assert 'record.Authority >= 0.98' in bridge\n    assert 'record.Confidence >= 0.94' in bridge\n'''
if 'test_v2_learning_bridge_skips_already_synchronized_unchanged_entries' not in t:
    test_path.write_text(t.rstrip() + addition + '\n', encoding='utf-8')
