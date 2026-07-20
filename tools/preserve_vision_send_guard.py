from pathlib import Path


path = Path("src/Bot/ChromeNs/QN.cs")
raw = path.read_bytes()
bom = raw.startswith(b"\xef\xbb\xbf")
text = raw.decode("utf-8-sig")
old = 'if (ctl != null) ctl.SetSendResult(sendOk, sendOk ? "已发送（合并图片与本轮消息）" : "发送失败：" + rpa.GetSendFailureReason());'
new = 'if (ctl != null) ctl.SetSendResult(sendOk, sendOk ? "已发送（合并图片与本轮消息）" : "识别完成，但目标买家会话未确认，未发送。原因：" + rpa.GetSendFailureReason());'
count = text.count(old)
if count != 1:
    raise RuntimeError("vision guard patch expected one match, got " + str(count))
text = text.replace(old, new, 1)
path.write_bytes(text.encode("utf-8-sig" if bom else "utf-8"))
print("VISION_GUARD_OK")
