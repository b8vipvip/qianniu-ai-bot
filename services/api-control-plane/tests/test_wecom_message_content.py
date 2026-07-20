from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

import wecom_bridge


def test_buyer_message_preserves_lines_and_media_markers():
    value = wecom_bridge.safe_buyer_message("什么时候发货\n[图片]\n什么时候发货")
    assert value == "什么时候发货\n[图片]"


def test_handoff_message_labels_buyer_message():
    data = wecom_bridge.HandoffNotifyInput(
        seller="seller",
        buyer="buyer",
        question="第一段\n[语音]\n补充说明",
        reason="需要人工",
    )
    message = wecom_bridge.build_handoff_message("QN-A1B2C3D4", data)
    assert "买家消息：\n第一段\n[语音]\n补充说明" in message
    assert "\n问题：" not in message
