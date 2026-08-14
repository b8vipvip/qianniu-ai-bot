from pathlib import Path
import importlib.util


ROOT = Path(__file__).resolve().parents[1]
PROBE = ROOT / "tools" / "qn_discovery_lab" / "imsdk_send_discovery_v2.js"
ANALYZER = ROOT / "tools" / "qn_discovery_lab" / "analyze_imsdk_send_trace.py"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def load_analyzer():
    spec = importlib.util.spec_from_file_location("analyze_imsdk_send_trace", ANALYZER)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


def test_send_discovery_is_passive_and_recurses_high_value_roots():
    source = read(PROBE)
    assert "candidateInvocationDisabled: true" in source
    assert "passiveOnly: true" in source
    assert "window.QN.wangwang" in source
    assert "window.QN.intelligentservice" in source
    assert "window.QN.component" in source
    assert "window.QN.app" in source
    assert "window.QN.gateway" in source
    assert "window._vs.SDK" in source
    assert "window.imsdk" in source
    assert "Object.getOwnPropertyNames" in source
    assert "__qnbotRunImsdkSendDiscoveryV2" in source
    assert "imsdkSendDiscoveryV2" in source

    # Candidate APIs are metadata only. The probe must not dispatch through known invoke entrypoints.
    code = "\n".join(line for line in source.splitlines() if not line.lstrip().startswith("*"))
    assert "imsdk.invoke(" not in code
    assert ".invoke({" not in code
    assert "QN.application.invoke(" not in code


def test_send_discovery_ranks_normal_chat_names_above_generic_im_names():
    source = read(PROBE)
    assert "sendmsg" in source
    assert "sendmessage" in source
    assert "wangwang" in source
    assert "singlemsg" in source
    assert "smarttip" in source
    assert "scoreName" in source


def test_trace_analyzer_marks_smart_tip_as_text_capable_but_not_canonical():
    analyzer = load_analyzer()
    sample = (
        'Info: IMSDK调用跟踪: {"method":"intelligentservice.SendSmartTipMsg",'
        '"param":{"userId":"cntaobaoBuyer","smartTip":"hello"}}\n'
        'Info: receiveNewMsg LocalSendMsg=SendByThisDev\n'
    )
    report = analyzer.analyze_text(sample)
    assert report["send_smart_tip_observed"] is True
    assert report["canonical_direct_send_confirmed"] is False
    row = next(item for item in report["methods"] if item["method"] == "intelligentservice.SendSmartTipMsg")
    assert row["canonical_normal_chat"] == "not-confirmed"
    assert "smart-tip" in row["note"]


def test_trace_analyzer_accepts_recursive_discovery_candidates_without_invoking_them():
    analyzer = load_analyzer()
    sample = (
        'Info: {"type":"imsdkSendDiscoveryV2","payload":{"candidates":['
        '{"path":"window.QN.wangwang.sendMsg","kind":"function","score":300},'
        '{"path":"window._vs.SDK.im.singlemsg.SendMessage","kind":"function","score":250}'
        ']}}\n'
    )
    report = analyzer.analyze_text(sample)
    paths = {item["path"] for item in report["discovery_paths"]}
    assert "window.QN.wangwang.sendMsg" in paths
    assert "window._vs.SDK.im.singlemsg.SendMessage" in paths
    assert report["candidate_invocation"] is False
