from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ORDER = ROOT / "src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs"
TEST = ROOT / "tests/test_order_action_cross_process_ledger_static.py"


def read(path):
    return path.read_text(encoding="utf-8-sig")


def write(path, content):
    path.write_text(content, encoding="utf-8", newline="\n")


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one match, got {count}")
    return text.replace(old, new, 1)


def replace_between(text, start, end, replacement, label):
    a = text.find(start)
    if a < 0:
        raise RuntimeError(f"{label}: start marker missing")
    b = text.find(end, a)
    if b < 0:
        raise RuntimeError(f"{label}: end marker missing")
    return text[:a] + replacement + text[b:]


code = read(ORDER)

code = replace_once(
    code,
    '                    ReloadAndMergeActionStateLocked(path);\n                    var now = DateTime.Now;',
    '''                    string reloadError;
                    if (!ReloadAndMergeActionStateLocked(path, out reloadError))
                    {
                        reason = "action_state_unavailable";
                        Log.ErrorWithMaxCount("订单自动回复已阻止：动作级持久状态不可可靠读取，禁止把未知状态当空状态发送。 error="
                            + Short(reloadError, 220), 20);
                        return false;
                    }
                    var now = DateTime.Now;''',
    "TryBegin fail-closed reload")

code = replace_once(
    code,
    '                    ReloadAndMergeActionStateLocked(path);\n                    var existing = _actionState.Records.FirstOrDefault(x => x != null && SameAction(x, plan));',
    '''                    string reloadError;
                    if (!ReloadAndMergeActionStateLocked(path, out reloadError))
                    {
                        Log.ErrorWithMaxCount("记录订单发送不确定状态时无法可靠读取动作ledger；保留磁盘中既有in-flight安全窗口。 error="
                            + Short(reloadError, 220), 20);
                        return;
                    }
                    var existing = _actionState.Records.FirstOrDefault(x => x != null && SameAction(x, plan));''',
    "MarkDeliveryUncertain fail-closed reload")

code = replace_once(
    code,
    '                    ReloadAndMergeActionStateLocked(path);\n                    var existing = _actionState.Records.FirstOrDefault(x => x != null && SameAction(x, plan));',
    '''                    string reloadError;
                    if (!ReloadAndMergeActionStateLocked(path, out reloadError))
                    {
                        Log.ErrorWithMaxCount("完成订单动作时无法可靠读取动作ledger；不覆盖磁盘状态，让既有in-flight安全窗口自然过期。 error="
                            + Short(reloadError, 220), 20);
                        return;
                    }
                    var existing = _actionState.Records.FirstOrDefault(x => x != null && SameAction(x, plan));''',
    "FinishExecution fail-closed reload")

code = replace_once(
    code,
    '                    ReloadAndMergeActionStateLocked(path);\n                    var exists = _actionState.Records.Any(x => x != null',
    '''                    string reloadError;
                    if (!ReloadAndMergeActionStateLocked(path, out reloadError))
                    {
                        Log.ErrorWithMaxCount("记录精确订单号时无法可靠读取动作ledger，本次跳过持久化观察。 error="
                            + Short(reloadError, 220), 10);
                        return;
                    }
                    var exists = _actionState.Records.Any(x => x != null''',
    "ObserveCanonical fail-closed reload")

state_block = r'''        private static void EnsureActionStateLoadedLocked()
        {
            if (_actionState != null) return;
            OrderReplyActionState disk;
            string error;
            if (TryReadActionStateFromDiskLocked(GetActionStatePath(), out disk, out error))
            {
                _actionState = disk;
                return;
            }
            // This compatibility loader is used only for non-send observations. The actual send
            // reservation path always calls ReloadAndMergeActionStateLocked and fails closed.
            Log.ErrorWithMaxCount("初始化订单自动回复动作状态失败；发送路径将继续保持fail-closed。 error="
                + Short(error, 220), 10);
            _actionState = new OrderReplyActionState();
        }

        private static bool TryReadActionStateFromDiskLocked(
            string path,
            out OrderReplyActionState state,
            out string error)
        {
            state = new OrderReplyActionState();
            error = string.Empty;
            string readError;
            var raw = CrossProcessAtomicStateFile.ReadAllTextShared(path, 4, 60, out readError);
            if (!string.IsNullOrWhiteSpace(readError))
            {
                error = "read_failed: " + readError;
                return false;
            }
            if (string.IsNullOrWhiteSpace(raw)) return true;
            try
            {
                state = JsonConvert.DeserializeObject<OrderReplyActionState>(raw) ?? new OrderReplyActionState();
                if (state.Records == null) state.Records = new List<OrderReplyActionRecord>();
                return true;
            }
            catch (Exception ex)
            {
                state = new OrderReplyActionState();
                error = "parse_failed: " + ex.Message;
                return false;
            }
        }

        private static bool ReloadAndMergeActionStateLocked(string path, out string error)
        {
            OrderReplyActionState disk;
            if (!TryReadActionStateFromDiskLocked(path, out disk, out error)) return false;
            if (_actionState == null || _actionState.Records == null || _actionState.Records.Count == 0)
            {
                _actionState = disk;
                return true;
            }
            if (disk.Records == null) disk.Records = new List<OrderReplyActionRecord>();
            foreach (var local in _actionState.Records.Where(x => x != null))
            {
                var existing = disk.Records.FirstOrDefault(x => SameStoredAction(x, local));
                if (existing == null)
                {
                    disk.Records.Add(local);
                    continue;
                }
                if (local.Until > existing.Until) existing.Until = local.Until;
                existing.Delivered = existing.Delivered || local.Delivered;
                existing.DeliveryUncertain = !existing.Delivered
                    && (existing.DeliveryUncertain || local.DeliveryUncertain);
                existing.InFlight = !existing.Delivered
                    && (existing.InFlight || local.InFlight);
                if (IsSuspiciousRoundedOrderId(existing.OrderId) && !IsSuspiciousRoundedOrderId(local.OrderId))
                    existing.OrderId = local.OrderId;
            }
            _actionState = disk;
            return true;
        }

'''
code = replace_between(
    code,
    "        private static void EnsureActionStateLoadedLocked()",
    "        private static bool SameStoredAction(OrderReplyActionRecord left, OrderReplyActionRecord right)",
    state_block,
    "replace ledger read/reload helpers")

if "ReloadAndMergeActionStateLocked(path);" in code:
    raise RuntimeError("unsafe void reload call remains")
if 'reason = "action_state_unavailable";' not in code:
    raise RuntimeError("fail-closed action reservation reason missing")
write(ORDER, code)

test = read(TEST)
anchor = '''    assert "action_state_persist_failed" in begin
'''
replacement = '''    assert "action_state_persist_failed" in begin
    assert "action_state_unavailable" in begin
    assert "ReloadAndMergeActionStateLocked(path, out reloadError)" in begin
'''
test = replace_once(test, anchor, replacement, "extend action reservation regression")
anchor2 = '''    assert "ReadAllTextShared" in state
    assert "WriteAllTextAtomic" in state
'''
replacement2 = '''    assert "ReadAllTextShared" in state
    assert "TryReadActionStateFromDiskLocked" in state
    assert "read_failed:" in state
    assert "parse_failed:" in state
    assert "WriteAllTextAtomic" in state
'''
test = replace_once(test, anchor2, replacement2, "extend ledger read failure regression")
write(TEST, test)

print("PR204 fail-closed ledger patch applied")
