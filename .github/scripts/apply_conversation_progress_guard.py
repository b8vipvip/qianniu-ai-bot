from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def replace_once(path: str, old: str, new: str) -> None:
    target = ROOT / path
    text = target.read_text(encoding="utf-8-sig")
    if old not in text:
        raise SystemExit(f"missing patch anchor in {path}: {old[:120]!r}")
    text = text.replace(old, new, 1)
    target.write_text(text, encoding="utf-8")


def insert_before_last(path: str, marker: str, block: str) -> None:
    target = ROOT / path
    text = target.read_text(encoding="utf-8-sig")
    index = text.rfind(marker)
    if index < 0:
        raise SystemExit(f"missing final marker in {path}: {marker!r}")
    text = text[:index] + block + text[index:]
    target.write_text(text, encoding="utf-8")


replace_once(
    "src/Bot/App.xaml.cs",
    "            Bot.Knowledge.KnowledgePolicyProfileUi.Initialize();\n",
    "            Bot.Knowledge.KnowledgePolicyProfileUi.Initialize();\n"
    "            // Explicit constructor call is required. A never-read static field on a beforefieldinit\n"
    "            // partial App type is not guaranteed to run, which made the import/export buttons disappear.\n"
    "            Bot.Knowledge.RulePolicyImportExportUi.InitializeForApp();\n",
)

replace_once(
    "src/Bot/ChromeNs/ConversationStateService.cs",
    "        public List<string> ConfirmedFacts { get; set; }\n        public List<string> Entities { get; set; }\n",
    "        public List<string> ConfirmedFacts { get; set; }\n"
    "        public List<string> Entities { get; set; }\n"
    "        public ConversationProgressSnapshot Progress { get; set; }\n",
)

replace_once(
    "src/Bot/ChromeNs/ConversationStateService.cs",
    "            return state;\n        }\n\n        public static string BuildPromptAddon",
    "            ConversationProgressGuardService.EnrichState(\n"
    "                state, seller, buyer, currentQuestion, ordered);\n"
    "            return state;\n"
    "        }\n\n"
    "        public static string BuildPromptAddon",
)

replace_once(
    "src/Bot/ChromeNs/ConversationStateService.cs",
    "            // 详细店铺规则不再作为每次都携带的固定提示词；这里按当前会话状态本地选择Top 3。\n            sb.Append(StorePromptProfileService.BuildTextRulesAddon(state));\n",
    "            sb.Append(ConversationProgressGuardService.BuildPromptAddon(state));\n\n"
    "            // 详细店铺规则不再作为每次都携带的固定提示词；这里按当前会话状态本地选择Top 3。\n"
    "            sb.Append(StorePromptProfileService.BuildTextRulesAddon(state));\n",
)

replace_once(
    "src/Bot/ChromeNs/SmartReplyRouterService.cs",
    "            var best = plan.BestCandidate;\n            if (best == null) return plan;\n\n            var second = plan.Candidates.Count > 1 ? plan.Candidates[1] : null;\n",
    "            var best = plan.BestCandidate;\n"
    "            if (ConversationProgressGuardService.RequiresContextualHandling(state))\n"
    "            {\n"
    "                plan.Route = best == null\n"
    "                    ? SmartReplyRouteKind.AiGeneral\n"
    "                    : SmartReplyRouteKind.ContextualKnowledge;\n"
    "                plan.Reason = \"当前消息属于订单/代充流程中的结构化承接，必须结合已完成步骤继续处理，禁止固定知识直答\";\n"
    "                if (best != null) KnowledgePolicyProfileService.RecordRouteSelection(best.Entry, false);\n"
    "                return plan;\n"
    "            }\n"
    "            if (best == null) return plan;\n\n"
    "            var second = plan.Candidates.Count > 1 ? plan.Candidates[1] : null;\n",
)

replace_once(
    "src/Bot/ChromeNs/SmartReplyRouterService.cs",
    "                .Where(x => x.PolicyEvaluation == null || !x.PolicyEvaluation.Excluded)\n                .Where(x => x.RetrievalScore >= 0.20\n",
    "                .Where(x => x.PolicyEvaluation == null || !x.PolicyEvaluation.Excluded)\n"
    "                .Where(x => ConversationProgressGuardService.AllowKnowledge(x.Entry, state, question))\n"
    "                .Where(x => x.RetrievalScore >= 0.20\n",
)

replace_once(
    "src/Bot/ChromeNs/PreSendAnswerValidator.cs",
    "            AddIntentCoverageIssue(question, answer, state, result.Issues);\n\n            var unsupportedNumbers",
    "            AddIntentCoverageIssue(question, answer, state, result.Issues);\n"
    "            var recentTurns = ConversationContextStore.GetRecentTurns(seller, buyer, question, 16);\n"
    "            ConversationProgressGuardService.AddValidationIssues(\n"
    "                question, answer, state, recentTurns, result.Issues);\n\n"
    "            var unsupportedNumbers",
)

replace_once(
    "src/Directory.Build.targets",
    "  <ItemGroup Condition=\"Exists('$(MSBuildProjectDirectory)\\ChromeNs\\ConversationStateService.cs')\">\n    <Compile Include=\"$(MSBuildProjectDirectory)\\ChromeNs\\ConversationStateService.cs\" />\n  </ItemGroup>\n",
    "  <ItemGroup Condition=\"Exists('$(MSBuildProjectDirectory)\\ChromeNs\\ConversationStateService.cs')\">\n"
    "    <Compile Include=\"$(MSBuildProjectDirectory)\\ChromeNs\\ConversationStateService.cs\" />\n"
    "  </ItemGroup>\n"
    "  <ItemGroup Condition=\"Exists('$(MSBuildProjectDirectory)\\ChromeNs\\ConversationProgressGuardService.cs')\">\n"
    "    <Compile Include=\"$(MSBuildProjectDirectory)\\ChromeNs\\ConversationProgressGuardService.cs\" />\n"
    "  </ItemGroup>\n",
)

# Add a focused static regression suite without touching existing tests.
test_path = ROOT / "tests/test_conversation_progress_guard_static.py"
test_path.write_text(
    '''from pathlib import Path\n\nROOT = Path(__file__).resolve().parents[1]\n\n\ndef read(path: str) -> str:\n    return (ROOT / path).read_text(encoding="utf-8-sig")\n\n\ndef test_import_export_bootstrap_is_explicitly_initialized():\n    app = read("src/Bot/App.xaml.cs")\n    assert "RulePolicyImportExportUi.InitializeForApp();" in app\n    assert app.index("KnowledgePolicyProfileUi.Initialize();") < app.index("RulePolicyImportExportUi.InitializeForApp();")\n\n\ndef test_progress_guard_tracks_completed_order_steps_without_sensitive_storage():\n    source = read("src/Bot/ChromeNs/ConversationProgressGuardService.cs")\n    assert "HasOrderEvidence" in source\n    assert "DeviceAccountConfirmed" in source\n    assert "HasPhoneNumber" in source\n    assert "HasVerificationCode" in source\n    assert "CurrentInputKind = \\\"phone_number\\\"" in source\n    assert "CurrentInputKind = \\\"verification_code\\\"" in source\n    assert "回复中不得复述完整手机号" in source\n    assert "不得在回复中复述验证码" in source\n\n\ndef test_router_filters_regressive_knowledge_and_forces_contextual_flow():\n    router = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")\n    assert "ConversationProgressGuardService.AllowKnowledge" in router\n    assert "ConversationProgressGuardService.RequiresContextualHandling" in router\n    assert "禁止固定知识直答" in router\n\n\ndef test_validator_blocks_repeated_screenshot_and_order_requests():\n    validator = read("src/Bot/ChromeNs/PreSendAnswerValidator.cs")\n    guard = read("src/Bot/ChromeNs/ConversationProgressGuardService.cs")\n    assert "ConversationProgressGuardService.AddValidationIssues" in validator\n    assert "回复却再次索要相同截图或照片" in guard\n    assert "买家已经下单，回复却再次要求下单" in guard\n    assert "当前消息是在承接代充手机号/验证码流程" in guard\n\n\ndef test_preorder_general_question_does_not_force_device_photo():\n    guard = read("src/Bot/ChromeNs/ConversationProgressGuardService.cs")\n    assert "一般售前咨询未询问设备兼容性" in guard\n    assert "不要主动强制买家先发设备截图" in guard\n    assert "可提示先下单后联系客服，无法充值可退款" in guard\n\n\ndef test_progress_guard_is_in_shared_build_targets():\n    targets = read("src/Directory.Build.targets")\n    assert "ConversationProgressGuardService.cs" in targets\n''',
    encoding="utf-8",
)
