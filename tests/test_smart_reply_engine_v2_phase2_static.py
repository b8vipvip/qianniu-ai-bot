from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_embedding_gateway_is_installed_and_packaged():
    assert "runtime_embedding_guard.install(control_plane)" in read("services/api-control-plane/bootstrap.py")
    assert "runtime_embedding_guard.py" in read("services/api-control-plane/Dockerfile")
    guard = read("services/api-control-plane/runtime_embedding_guard.py")
    assert '/v1/embeddings' in guard
    assert 'embedding_upstream_exhausted' in guard


def test_optional_embedding_setting_and_cache_exist():
    assert 'txtControlPlaneEmbeddingModel' in read("src/Bot/Options/CtlRobotOptions.xaml")
    assert 'ControlPlaneEmbeddingModel' in read("src/Bot/Options/CtlRobotOptions.ControlPlane.cs")
    assert 'embeddingModel' in read("src/Bot/Options/CtlRobotOptions.ControlPlane.Portability.cs")
    service = read("src/Bot/ChromeNs/SemanticEmbeddingService.cs")
    assert 'knowledge-embeddings.json' in service
    assert 'RequestTimeoutMilliseconds = 2200' in service
    assert 'WarmupBatchSize = 24' in service
    assert 'Cosine' in service
    assert 'SemanticEmbeddingService.cs' in read("src/Directory.Build.targets")


def test_semantics_only_assist_retrieval_not_direct_fixed_reply():
    router = read("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    assert 'SemanticScore' in router
    assert 'SemanticEmbeddingService.TryScore' in router
    assert 'existing.FinalScore * 0.72 + existing.SemanticScore * 0.28' in router
    assert 'scored.Score * 0.78' in router
    assert 'best.RetrievalScore >= 0.88' in router
    assert 'best.ExactQuestionMatch && best.RetrievalScore >= 0.95' in router
