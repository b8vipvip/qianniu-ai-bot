from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_recent_visual_semantics_are_injected_into_later_text_ai_context():
    helper = text("src/Bot/ChromeNs/RecentVisualContextService.cs")
    openai = text("src/Bot/ChromeNs/MyOpenAI.cs")
    targets = text("src/Directory.Build.targets")

    assert "BuildPromptAddon(string seller, string buyer, string currentQuestion)" in helper
    assert "VisualKnowledgeObservationEntity" in helper
    assert "最近图片理解缓存" in helper
    assert "不代表订单、付款、账号、充值等实时状态" in helper
    assert "RecentVisualContextService.BuildPromptAddon(seller, buyer, question)" in openai
    assert "RecentVisualContextService.cs" in targets


def test_post_order_photo_preset_is_suppressed_by_recent_official_kugou_app_visual_evidence():
    helper = text("src/Bot/ChromeNs/RecentVisualContextService.cs")
    guard = text("src/Bot/ChromeNs/OrderGuidanceDeliveryGuard.cs")

    assert "TrySatisfyOrderPhotoRequirement" in helper
    assert "LooksLikeKugouPhotoGuidance" in helper
    assert "酷狗官方" in helper
    assert "官方app" in helper
    assert "酷狗音乐app" in helper
    assert "电视自带" in helper
    assert "系统内置" in helper
    assert "Login state is deliberately not part of this decision" in helper

    visual_check = guard.index("RecentVisualContextService.TrySatisfyOrderPhotoRequirement")
    old_message_check = guard.index("FindEquivalentSellerReply", visual_check)
    assert visual_check < old_message_check
    assert "下单前图片已满足确认要求" in guard
    assert "买家下单前已发送可确认的酷狗官方APP界面图片" in guard
    assert "if (!plan.IsBuyerFollowUp)" in guard


def test_visual_analysis_continues_after_reply_lease_becomes_stale_and_persists_semantics():
    qn = text("src/Bot/ChromeNs/QN.cs")
    vision = text("src/Bot/ChromeNs/VisionRequestService.cs")

    vision_method = qn.index("private async Task ProcessVisionBurstAsync")
    execute = qn.index("_visionRequestService.ExecuteAsync(task, CancellationToken.None)", vision_method)
    stale_check = qn.index("if (!lease.IsCurrent)", execute)
    assert execute < stale_check

    # The caller deliberately does not pass the burst lease as a cancellation token, so a
    # newer buyer task or manual intervention can suppress sending without killing analysis.
    assert "CancellationToken.None" in qn[vision_method:stale_check]

    record = vision.index("VisualKnowledgeLearningService.RecordVisionAnalysis")
    successful_return = vision.index("return result;", record)
    assert record < successful_return
    assert "result.VisualSummary" in vision[record - 500:successful_return]


def test_multiple_buyers_get_independent_ai_workers_while_real_sends_remain_serialized():
    burst = text("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    qn = text("src/Bot/ChromeNs/QN.cs")
    openai = text("src/Bot/ChromeNs/MyOpenAI.cs")

    assert "ConcurrentDictionary<string, BurstState> _states" in burst
    assert "var key = Key(item.SellerNick, item.BuyerNick);" in burst
    assert 'return (seller ?? string.Empty).Trim() + "#" + (buyer ?? string.Empty).Trim();' in burst
    assert "if (startWorker) Task.Run(() => RunAsync(key, state));" in burst

    text_method = qn.index("private async Task ProcessTextBurstAsync")
    vision_method = qn.index("private async Task ProcessVisionBurstAsync")
    assert "Task.Run(() => MyOpenAI.GetAnswer" in qn[text_method:vision_method]

    # Text calls use a shared HttpClient but there is no process-wide AI semaphore/lock.
    assert "private static readonly HttpClient SharedHttp" in openai
    assert "SemaphoreSlim" not in openai

    # Only the actual Qianniu UI send is serialized, which is required to prevent cross-buyer sends.
    assert "private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);" in qn
    send_method = qn.index("public async Task<bool> SendTextWithRetryAsync")
    assert "await _sendGate.WaitAsync();" in qn[send_method:send_method + 2500]
