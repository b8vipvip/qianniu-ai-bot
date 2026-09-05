from pathlib import Path

path = Path('src/Bot/ChromeNs/KnowledgeLearningService.cs')
text = path.read_text(encoding='utf-8-sig')
old = '''                    keywords = arr == null
                        ? Convert.ToString(parsed["keywords"])
                        : string.Join(",", arr.Select(x => x.ToString().Trim()).Where(x => x.Length > 0));
                }
'''
new = '''                    keywords = arr == null
                        ? Convert.ToString(parsed["keywords"])
                        : string.Join(",", arr.Select(x => x.ToString().Trim()).Where(x => x.Length > 0));
                    // AI may enrich the reusable question/category/keywords, but an answer that was
                    // explicitly confirmed by a human is immutable provenance. Never let the
                    // organizer paraphrase or broaden the human-confirmed answer text.
                    if (KnowledgeV2AuthorityPolicy.IsExplicitHumanConfirmationSource(sourceType))
                        learnedAnswer = safeAnswer;
                }
'''
if text.count(old) != 1:
    raise SystemExit('KnowledgeLearningService.cs target block changed; refusing broad patch')
path.write_text(text.replace(old, new, 1), encoding='utf-8')

test = Path('tests/test_1272_knowledge_authority_and_turn_lifecycle_static.py')
t = test.read_text(encoding='utf-8-sig')
addition = '''\n\ndef test_explicit_human_answer_is_not_rewritten_by_ai_organizer():\n    learning = read("src/Bot/ChromeNs/KnowledgeLearningService.cs")\n    assert "KnowledgeV2AuthorityPolicy.IsExplicitHumanConfirmationSource(sourceType)" in learning\n    assert "learnedAnswer = safeAnswer;" in learning\n'''
if 'test_explicit_human_answer_is_not_rewritten_by_ai_organizer' not in t:
    test.write_text(t.rstrip() + addition + '\n', encoding='utf-8')
