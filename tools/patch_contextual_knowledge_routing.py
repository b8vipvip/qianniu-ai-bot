from pathlib import Path

path = Path("src/Bot/ChromeNs/MyOpenAI.cs")
text = path.read_text(encoding="utf-8-sig")

old = '''                KnowledgeBaseEntry localKnowledge;
                double localScore;
                if (KnowledgeLearningService.TryFindLocalAnswer(seller, buyer, question, out localKnowledge, out localScore))
                {
                    var localAnswer = BotFeatureStore.ApplyOutputPolicy(localKnowledge.Answer);
                    KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, localAnswer, "本地");
                    Log.Info("命中本地知识库，未调用AI。buyer=" + buyer + ", knowledgeId=" + localKnowledge.Id + ", score=" + localScore.ToString("0.00"));
                    return localAnswer;
                }
'''
new = '''                KnowledgeBaseEntry contextualKnowledge = null;
                ContextualKnowledgeDecision contextualDecision = null;
                double contextualKnowledgeScore = 0;
                KnowledgeBaseEntry localKnowledge;
                double localScore;
                if (KnowledgeLearningService.TryFindLocalAnswer(seller, buyer, question, out localKnowledge, out localScore))
                {
                    var localAnswer = BotFeatureStore.ApplyOutputPolicy(localKnowledge.Answer);
                    var contextDecision = KnowledgeContextualReplyService.Analyze(seller, buyer, question, localKnowledge);
                    if (!contextDecision.IsFollowUp)
                    {
                        KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, localAnswer, "本地");
                        Log.Info("命中本地知识库，未调用AI。buyer=" + buyer + ", knowledgeId=" + localKnowledge.Id + ", score=" + localScore.ToString("0.00"));
                        return localAnswer;
                    }

                    contextualKnowledge = localKnowledge;
                    contextualDecision = contextDecision;
                    contextualKnowledgeScore = localScore;
                    Log.Info("命中本地知识库，但当前消息属于上下文续答，将基于知识库事实进行衔接改写。buyer="
                        + buyer + ", knowledgeId=" + localKnowledge.Id + ", score=" + localScore.ToString("0.00")
                        + ", reason=" + contextDecision.Reason);
                }
'''
if old not in text:
    raise SystemExit("local knowledge block not found")
text = text.replace(old, new, 1)

old = '''                if (!EnsureConfig()) return "错误：AI配置不完整，请检查 API接口 列表中的 BaseUrl / ApiKey / Model。";
'''
new = '''                if (!EnsureConfig())
                {
                    if (contextualKnowledge != null)
                    {
                        var fallback = KnowledgeContextualReplyService.BuildOfflineFallback(contextualDecision, contextualKnowledge);
                        if (!string.IsNullOrWhiteSpace(fallback))
                        {
                            fallback = BotFeatureStore.ApplyOutputPolicy(fallback);
                            KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, fallback, "本地知识库上下文");
                            Log.Info("上下文知识回复使用本地安全兜底。buyer=" + buyer + ", answer=" + fallback);
                            return fallback;
                        }
                    }
                    return "错误：AI配置不完整，请检查 API接口 列表中的 BaseUrl / ApiKey / Model。";
                }
'''
if old not in text:
    raise SystemExit("EnsureConfig block not found")
text = text.replace(old, new, 1)

old = '''                var dynamicSystemPrompt = systemPrompt + BotFeatureStore.BuildPromptAddon(contextForKnowledge.ToString());
                var messages = new JArray { CreateMessage("system", dynamicSystemPrompt) };
'''
new = '''                var dynamicSystemPrompt = systemPrompt + BotFeatureStore.BuildPromptAddon(contextForKnowledge.ToString());
                if (contextualKnowledge != null)
                {
                    dynamicSystemPrompt += KnowledgeContextualReplyService.BuildPromptAddon(contextualDecision, contextualKnowledge);
                }
                var messages = new JArray { CreateMessage("system", dynamicSystemPrompt) };
'''
if old not in text:
    raise SystemExit("dynamic prompt block not found")
text = text.replace(old, new, 1)

old = '''                        KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, finalAnswer, "AI生成");
                        KnowledgeLearningService.QueueLearn(question, finalAnswer, "AI生成", seller, buyer);
                        return finalAnswer;
'''
new = '''                        var answerSource = contextualKnowledge == null ? "AI生成" : "本地知识库上下文";
                        KnowledgeLearningService.RegisterAnswerSource(seller, buyer, question, finalAnswer, answerSource);
                        if (contextualKnowledge == null)
                        {
                            KnowledgeLearningService.QueueLearn(question, finalAnswer, "AI生成", seller, buyer);
                        }
                        else
                        {
                            Log.Info("上下文知识回复生成成功。buyer=" + buyer
                                + ", knowledgeId=" + contextualKnowledge.Id
                                + ", score=" + contextualKnowledgeScore.ToString("0.00")
                                + ", answer=" + finalAnswer);
                        }
                        return finalAnswer;
'''
if old not in text:
    raise SystemExit("answer source block not found")
text = text.replace(old, new, 1)

path.write_text(text, encoding="utf-8-sig")
