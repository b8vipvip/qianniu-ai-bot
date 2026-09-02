from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_embedding_http_path_is_true_async_with_linked_wall_clock_deadline():
    code = read("src/Bot/ChromeNs/SemanticEmbeddingService.cs")
    assert "RequestTimeoutMilliseconds = 2200" in code
    assert "TryScoreAsync(" in code
    assert "RequestEmbeddingsAsync(" in code
    assert "CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)" in code
    assert "linked.CancelAfter(RequestTimeoutMilliseconds)" in code
    assert "await Http.SendAsync(" in code
    assert "HttpCompletionOption.ResponseContentRead" in code

    request_region = code.split("private static async Task<List<float[]>> RequestEmbeddingsAsync", 1)[1]
    request_region = request_region.split("private static IEnumerable<DocumentDescriptor>", 1)[0]
    assert ".GetAwaiter().GetResult()" not in request_region


def test_foreground_router_awaits_embedding_and_propagates_total_budget_token():
    router = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    assert "public static async Task<SmartReplyPlan> BuildPlanAsync(" in router
    assert "CancellationToken cancellationToken" in router
    assert "await SemanticEmbeddingService.TryScoreAsync(" in router

    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    get_answer = pipeline.split("public static async Task<string> GetAnswerAsync", 1)[1]
    get_answer = get_answer.split("private static async Task<string> StreamMessagesAsync", 1)[0]
    assert "await SmartReplyRouterService.BuildPlanAsync(seller, buyer, question, token)" in get_answer
    assert "var plan = SmartReplyRouterService.BuildPlan(seller, buyer, question);" not in get_answer


def test_embedding_subdeadline_fails_open_but_outer_budget_cancellation_propagates():
    code = read("src/Bot/ChromeNs/SemanticEmbeddingService.cs")
    assert "if (cancellationToken.IsCancellationRequested) throw;" in code
    assert "已自动降级到本地混合检索" in code
    assert "Task.Run(async () =>" in code
