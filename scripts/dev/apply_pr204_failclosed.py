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

try_begin = r'''        internal static bool TryBeginExecution(OrderPlacedReplyPlan plan, out string reason)
        {
            reason = string.Empty;
            if (plan == null || string.IsNullOrWhiteSpace(plan.Seller)
                || string.IsNullOrWhiteSpace(plan.Buyer) || string.IsNullOrWhiteSpace(plan.OrderId))
            {
                reason = "invalid_plan";
                return false;
            }

            lock (ActionSync)
            {
                var path = GetActionStatePath();
                using (var lease = CrossProcessAtomicStateFile.Acquire(path, "OrderReplyActionState", 3000))
                {
                    if (!lease.Acquired)
                    {
                        reason = "action_state_lock_timeout";
                        Log.ErrorWithMaxCount("订单自动回复已阻止：无法取得跨进程动作状态锁，避免多实例重复发送。", 20);
                        return false;
                    }

                    string reloadError;
                    if (!ReloadAndMergeActionStateLocked(path, out reloadError))
                    {
                        reason = "action_state_unavailable";
                        Log.ErrorWithMaxCount("订单自动回复已阻止：动作级持久状态不可可靠读取，禁止把未知状态当空状态发送。 error="
                            + Short(reloadError, 220), 20);
                        return false;
                    }
                    var now = DateTime.Now;
                    ActiveActions.RemoveAll(x => x == null || x.Until <= now);
                    _actionState.Records.RemoveAll(x => x == null || x.Until <= now);

                    var canonical = FindCanonicalOrderIdLocked(plan.Seller, plan.Buyer, plan.OrderId);
                    if (!string.IsNullOrWhiteSpace(canonical)
                        && !string.Equals(canonical, plan.OrderId, StringComparison.Ordinal))
                    {
                        Log.Info("订单号精度别名已归一化: orderId=" + plan.OrderId + ", canonicalOrderId=" + canonical);
                        plan.OrderId = canonical;
                        if (plan.Snapshot != null) plan.Snapshot.OrderId = canonical;
                        plan.ReservationKey = BuildReservationKey(plan.Seller, plan.Buyer, canonical, plan.IsBuyerFollowUp);
                    }

                    if (IsSuspiciousRoundedOrderId(plan.OrderId)
                        && string.IsNullOrWhiteSpace(FindCanonicalOrderIdLocked(plan.Seller, plan.Buyer, plan.OrderId, true)))
                    {
                        reason = "precision_risk_order_id";
                        Log.ErrorWithMaxCount("订单自动回复已阻止：检测到疑似 JavaScript Number 精度损失的长订单号，等待精确字符串订单事件补偿。 orderId="
                            + plan.OrderId, 50);
                        return false;
                    }

                    if (ActiveActions.Any(x => SameAction(x, plan)))
                    {
                        reason = "action_inflight";
                        return false;
                    }
                    if (_actionState.Records.Any(x => x.Delivered && SameAction(x, plan)))
                    {
                        reason = "action_already_delivered";
                        return false;
                    }
                    if (_actionState.Records.Any(x => x.DeliveryUncertain && SameAction(x, plan)))
                    {
                        reason = "action_delivery_uncertain";
                        return false;
                    }
                    if (_actionState.Records.Any(x => x.InFlight && x.Until > now && SameAction(x, plan)))
                    {
                        reason = "action_inflight_cross_process";
                        return false;
                    }

                    var durable = _actionState.Records.FirstOrDefault(x => x != null && SameAction(x, plan));
                    if (durable == null)
                    {
                        durable = new OrderReplyActionRecord();
                        _actionState.Records.Add(durable);
                    }
                    durable.Seller = Normalize(plan.Seller);
                    durable.Buyer = NormalizeBuyer(plan.Seller, plan.Buyer);
                    durable.OrderId = plan.OrderId.Trim();
                    durable.FollowUp = plan.IsBuyerFollowUp;
                    durable.Until = now.AddMinutes(10);
                    durable.Delivered = false;
                    durable.DeliveryUncertain = false;
                    durable.InFlight = true;

                    ActiveActions.Add(new OrderReplyActionRecord
                    {
                        Seller = durable.Seller,
                        Buyer = durable.Buyer,
                        OrderId = durable.OrderId,
                        FollowUp = durable.FollowUp,
                        Until = durable.Until,
                        Delivered = false,
                        DeliveryUncertain = false,
                        InFlight = true
                    });

                    if (!SaveActionStateLocked(path))
                    {
                        ActiveActions.RemoveAll(x => x != null && SameAction(x, plan));
                        durable.InFlight = false;
                        reason = "action_state_persist_failed";
                        Log.ErrorWithMaxCount("订单自动回复已阻止：动作级in-flight状态无法原子持久化，避免多实例重复发送。", 20);
                        return false;
                    }
                    return true;
                }
            }
        }

'''
code = replace_between(
    code,
    "        internal static bool TryBeginExecution(OrderPlacedReplyPlan plan, out string reason)",
    "        internal static void MarkDeliveryUncertain(OrderPlacedReplyPlan plan, string reason)",
    try_begin,
    "TryBeginExecution")

mark_uncertain = r'''        internal static void MarkDeliveryUncertain(OrderPlacedReplyPlan plan, string reason)
        {
            if (plan == null) return;
            lock (ActionSync)
            {
                ActiveActions.RemoveAll(x => x != null && SameAction(x, plan));
                var path = GetActionStatePath();
                using (var lease = CrossProcessAtomicStateFile.Acquire(path, "OrderReplyActionState", 3000))
                {
                    if (!lease.Acquired)
                    {
                        Log.ErrorWithMaxCount("记录订单发送不确定状态时跨进程锁超时；保留既有durable in-flight窗口以防重复。", 20);
                        return;
                    }
                    string reloadError;
                    if (!ReloadAndMergeActionStateLocked(path, out reloadError))
                    {
                        Log.ErrorWithMaxCount("记录订单发送不确定状态时无法可靠读取动作ledger；保留磁盘中既有in-flight安全窗口。 error="
                            + Short(reloadError, 220), 20);
                        return;
                    }
                    var existing = _actionState.Records.FirstOrDefault(x => x != null && SameAction(x, plan));
                    if (existing == null)
                    {
                        existing = new OrderReplyActionRecord();
                        _actionState.Records.Add(existing);
                    }
                    existing.Seller = Normalize(plan.Seller);
                    existing.Buyer = NormalizeBuyer(plan.Seller, plan.Buyer);
                    existing.OrderId = (plan.OrderId ?? string.Empty).Trim();
                    existing.FollowUp = plan.IsBuyerFollowUp;
                    existing.Until = DateTime.Now.AddMinutes(10);
                    existing.Delivered = false;
                    existing.DeliveryUncertain = true;
                    existing.InFlight = false;
                    SaveActionStateLocked(path);
                }
            }
            Log.ErrorWithMaxCount(
                "订单发送状态不确定，10分钟内禁止自动重发以避免重复: seller=" + plan.Seller
                + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId
                + ", reason=" + (reason ?? string.Empty),
                20);
        }

'''
code = replace_between(
    code,
    "        internal static void MarkDeliveryUncertain(OrderPlacedReplyPlan plan, string reason)",
    "        internal static void FinishExecution(OrderPlacedReplyPlan plan, bool delivered, int sentSegments)",
    mark_uncertain,
    "MarkDeliveryUncertain")

finish = r'''        internal static void FinishExecution(OrderPlacedReplyPlan plan, bool delivered, int sentSegments)
        {
            if (plan == null) return;
            lock (ActionSync)
            {
                ActiveActions.RemoveAll(x => x != null && SameAction(x, plan));
                var path = GetActionStatePath();
                using (var lease = CrossProcessAtomicStateFile.Acquire(path, "OrderReplyActionState", 3000))
                {
                    if (!lease.Acquired)
                    {
                        Log.ErrorWithMaxCount("完成订单动作时跨进程锁超时；durable in-flight将按10分钟安全窗口自然过期。", 20);
                        return;
                    }
                    string reloadError;
                    if (!ReloadAndMergeActionStateLocked(path, out reloadError))
                    {
                        Log.ErrorWithMaxCount("完成订单动作时无法可靠读取动作ledger；不覆盖磁盘状态，让既有in-flight安全窗口自然过期。 error="
                            + Short(reloadError, 220), 20);
                        return;
                    }
                    var existing = _actionState.Records.FirstOrDefault(x => x != null && SameAction(x, plan));
                    if (existing != null) existing.InFlight = false;

                    if (delivered || sentSegments > 0)
                    {
                        var now = DateTime.Now;
                        var hours = plan.IsBuyerFollowUp
                            ? 720
                            : (plan.Config == null ? 24 : Math.Max(1, Math.Min(720, plan.Config.OrderPlacedDedupHours)));
                        var until = delivered ? now.AddHours(hours) : now.AddMinutes(10);
                        if (existing == null)
                        {
                            existing = new OrderReplyActionRecord();
                            _actionState.Records.Add(existing);
                        }
                        existing.Seller = Normalize(plan.Seller);
                        existing.Buyer = NormalizeBuyer(plan.Seller, plan.Buyer);
                        existing.OrderId = plan.OrderId.Trim();
                        existing.FollowUp = plan.IsBuyerFollowUp;
                        existing.Until = until;
                        existing.Delivered = delivered || sentSegments > 0;
                        existing.DeliveryUncertain = false;
                        existing.InFlight = false;
                    }
                    _actionState.Records.RemoveAll(x => x == null || x.Until <= DateTime.Now);
                    SaveActionStateLocked(path);
                }
            }
        }

'''
code = replace_between(
    code,
    "        internal static void FinishExecution(OrderPlacedReplyPlan plan, bool delivered, int sentSegments)",
    "        private static void ObserveCanonicalOrderId(string seller, string buyer, string orderId)",
    finish,
    "FinishExecution")

observe = r'''        private static void ObserveCanonicalOrderId(string seller, string buyer, string orderId)
        {
            orderId = (orderId ?? string.Empty).Trim();
            if (orderId.Length < 8 || IsSuspiciousRoundedOrderId(orderId)) return;
            lock (ActionSync)
            {
                var path = GetActionStatePath();
                using (var lease = CrossProcessAtomicStateFile.Acquire(path, "OrderReplyActionState", 3000))
                {
                    if (!lease.Acquired)
                    {
                        Log.ErrorWithMaxCount("记录精确订单号时跨进程动作状态锁超时，本次仅跳过持久化观察。 orderId=" + orderId, 10);
                        return;
                    }
                    string reloadError;
                    if (!ReloadAndMergeActionStateLocked(path, out reloadError))
                    {
                        Log.ErrorWithMaxCount("记录精确订单号时无法可靠读取动作ledger，本次跳过持久化观察。 error="
                            + Short(reloadError, 220), 10);
                        return;
                    }
                    var exists = _actionState.Records.Any(x => x != null
                        && !x.FollowUp
                        && Normalize(x.Seller) == Normalize(seller)
                        && NormalizeBuyer(x.Seller, x.Buyer) == NormalizeBuyer(seller, buyer)
                        && string.Equals(x.OrderId, orderId, StringComparison.Ordinal));
                    if (!exists)
                    {
                        _actionState.Records.Add(new OrderReplyActionRecord
                        {
                            Seller = Normalize(seller),
                            Buyer = NormalizeBuyer(seller, buyer),
                            OrderId = orderId,
                            FollowUp = false,
                            Until = DateTime.Now.AddHours(2),
                            Delivered = false,
                            DeliveryUncertain = false,
                            InFlight = false
                        });
                        SaveActionStateLocked(path);
                    }
                }
            }
        }

'''
code = replace_between(
    code,
    "        private static void ObserveCanonicalOrderId(string seller, string buyer, string orderId)",
    "        private static string FindCanonicalOrderIdLocked(string seller, string buyer, string orderId, bool requireExactCandidate = false)",
    observe,
    "ObserveCanonicalOrderId")

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
            // Compatibility-only observation loader. The actual send reservation path always calls
            // ReloadAndMergeActionStateLocked and fails closed on any read/parse uncertainty.
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
    "ledger read/reload helpers")

if "ReloadAndMergeActionStateLocked(path);" in code:
    raise RuntimeError("unsafe void reload call remains")
if 'reason = "action_state_unavailable";' not in code:
    raise RuntimeError("fail-closed action reservation reason missing")
write(ORDER, code)

test = read(TEST)
anchor = '    assert "action_state_persist_failed" in begin\n'
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
