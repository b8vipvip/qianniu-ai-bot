from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def write(path, content):
    target = ROOT / path
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(content, encoding="utf-8", newline="\n")


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, got {count}")
    return text.replace(old, new, 1)


def replace_between(text, start, end, replacement, label):
    start_at = text.find(start)
    if start_at < 0:
        raise RuntimeError(f"{label}: start marker missing")
    end_at = text.find(end, start_at)
    if end_at < 0:
        raise RuntimeError(f"{label}: end marker missing")
    return text[:start_at] + replacement + text[end_at:]


cross_process_source = r'''using BotLib;
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Small crash-safe state-file primitive shared by runtime ledgers that must survive more than
    /// one Bot process. The named mutex covers the caller's whole read/modify/write transaction;
    /// writes always go to a unique sibling file, Flush(true), then atomic replace. The previous
    /// valid file is never deleted before the replacement succeeds.
    /// </summary>
    internal static class CrossProcessAtomicStateFile
    {
        internal sealed class Lease : IDisposable
        {
            private Mutex _mutex;
            public bool Acquired { get; private set; }

            internal Lease(Mutex mutex, bool acquired)
            {
                _mutex = mutex;
                Acquired = acquired;
            }

            public void Dispose()
            {
                if (_mutex == null) return;
                if (Acquired)
                {
                    try { _mutex.ReleaseMutex(); }
                    catch { }
                    Acquired = false;
                }
                try { _mutex.Dispose(); }
                catch { }
                _mutex = null;
            }
        }

        internal static Lease Acquire(string path, string scope, int waitMilliseconds)
        {
            Mutex mutex = null;
            var acquired = false;
            try
            {
                mutex = new Mutex(false, BuildMutexName(path, scope));
                try
                {
                    acquired = mutex.WaitOne(Math.Max(250, waitMilliseconds));
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                    Log.Info("检测到跨进程状态锁的旧持有进程已退出，已安全接管: scope=" + scope);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("获取跨进程状态锁失败: scope=" + scope + ", error=" + ex.Message, 10);
            }
            return new Lease(mutex, acquired);
        }

        internal static string ReadAllTextShared(
            string path,
            int retryCount,
            int retryDelayMilliseconds,
            out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return string.Empty;
            Exception last = null;
            for (var attempt = 1; attempt <= Math.Max(1, retryCount); attempt++)
            {
                try
                {
                    using (var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        return reader.ReadToEnd();
                    }
                }
                catch (IOException ex)
                {
                    last = ex;
                    if (attempt < retryCount) Thread.Sleep(Math.Max(10, retryDelayMilliseconds) * attempt);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return string.Empty;
                }
            }
            error = last == null ? "unknown read failure" : last.Message;
            return string.Empty;
        }

        internal static bool WriteAllTextAtomic(
            string path,
            string content,
            int retryCount,
            int retryDelayMilliseconds,
            out string error)
        {
            error = string.Empty;
            var directory = Path.GetDirectoryName(path);
            try
            {
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            var temp = path + "." + Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(content ?? string.Empty);
                using (var stream = new FileStream(
                    temp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    8192,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                Exception last = null;
                for (var attempt = 1; attempt <= Math.Max(1, retryCount); attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(temp, path, null, true);
                        else File.Move(temp, path);
                        return true;
                    }
                    catch (IOException ex)
                    {
                        last = ex;
                        if (attempt < retryCount) Thread.Sleep(Math.Max(10, retryDelayMilliseconds) * attempt);
                    }
                }
                error = last == null ? "unknown atomic replace failure" : last.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch { }
            }
        }

        private static string BuildMutexName(string path, string scope)
        {
            var normalized = string.Empty;
            try { normalized = Path.GetFullPath(path ?? string.Empty).Trim().ToLowerInvariant(); }
            catch { normalized = (path ?? string.Empty).Trim().ToLowerInvariant(); }
            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes((scope ?? "state") + "|" + normalized);
                var hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
                return @"Local\QianniuAiBot.State." + hash.Substring(0, Math.Min(32, hash.Length));
            }
        }
    }
}
'''
write("src/Bot/ChromeNs/CrossProcessAtomicStateFile.cs", cross_process_source)

platform_guard_source = r'''using BotLib;
using FlaUI.Core.AutomationElements;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    public partial class QNRpa
    {
        private const int PlatformSendBlockProbeTimeoutMs = 900;

        /// <summary>
        /// Detect Qianniu's service-attitude confirmation as a platform policy block. This method
        /// never clicks the dialog. It converts the send into a cancellation so the existing outer
        /// retry loop stops immediately instead of blindly sending the same text again.
        /// </summary>
        private async Task<bool> StopIfPlatformSendBlockedAsync(string buyer, string stage)
        {
            Task<string> probe;
            try
            {
                probe = Task.Run(() =>
                {
                    string detail;
                    return TryDetectServiceAttitudeBlock(out detail) ? detail : string.Empty;
                });
            }
            catch
            {
                return false;
            }

            var winner = await Task.WhenAny(probe, Task.Delay(PlatformSendBlockProbeTimeoutMs)).ConfigureAwait(false);
            if (winner != probe)
            {
                Log.Info("千牛平台发送拦截探测超时，保持原发送失败结果: seller=" + SellerNick
                    + ", buyer=" + buyer + ", stage=" + stage);
                return false;
            }

            string detailText;
            try { detailText = await probe.ConfigureAwait(false); }
            catch (Exception ex)
            {
                Log.Info("千牛平台发送拦截探测失败，保持原发送失败结果: " + ex.Message);
                return false;
            }
            if (string.IsNullOrWhiteSpace(detailText)) return false;

            SetSendCancellation("平台发送拦截", detailText);
            Log.ErrorWithMaxCount(
                "千牛服务态度提醒已阻止自动发送，Bot不会点击“继续发送”也不会盲目重试: seller="
                + SellerNick + ", buyer=" + buyer + ", stage=" + stage,
                50);
            return true;
        }

        private bool TryDetectServiceAttitudeBlock(out string detail)
        {
            detail = string.Empty;
            if (!EnsureSellerDeskBinding(false) || automationApplication == null || uia3Automation == null)
                return false;

            try
            {
                var roots = new List<AutomationElement>();
                var windows = automationApplication.GetAllTopLevelWindows(uia3Automation);
                if (windows != null) roots.AddRange(windows.Where(x => x != null));

                var desk = ResolveSellerDesk();
                if (desk != null && desk.Hwnd != null && desk.Hwnd.Handle > 0)
                {
                    try
                    {
                        var main = uia3Automation.FromHandle(new IntPtr(desk.Hwnd.Handle));
                        if (main != null && !roots.Any(x => x.Equals(main))) roots.Add(main);
                    }
                    catch { }
                }

                foreach (var root in roots)
                {
                    var names = new List<string>();
                    AddPlatformGuardName(names, root);
                    AutomationElement[] descendants;
                    try { descendants = root.FindAllDescendants(); }
                    catch { descendants = new AutomationElement[0]; }
                    foreach (var element in descendants) AddPlatformGuardName(names, element);

                    var combined = string.Join(" ", names);
                    if (combined.IndexOf("服务态度提醒", StringComparison.Ordinal) < 0) continue;
                    var hasContinue = names.Any(x => string.Equals(
                        RegexCompactPlatformGuardText(x),
                        "继续发送",
                        StringComparison.Ordinal));
                    detail = hasContinue
                        ? "检测到千牛“服务态度提醒”及“继续发送”按钮；该平台提示必须由人工判断，Bot禁止自动确认"
                        : "检测到千牛“服务态度提醒”；该平台提示必须由人工判断，Bot禁止自动确认";
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Info("扫描千牛服务态度提醒失败: " + ex.Message);
            }
            return false;
        }

        private static void AddPlatformGuardName(ICollection<string> names, AutomationElement element)
        {
            if (names == null || element == null) return;
            var name = SafeName(element);
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
        }

        private static string RegexCompactPlatformGuardText(string value)
        {
            return string.Concat((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c))).Trim();
        }
    }
}
'''
write("src/Bot/ChromeNs/QNRpa.PlatformSendGuard.cs", platform_guard_source)

# Order action ledger: make the cross-process state itself authoritative for in-flight execution.
order_path = "src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs"
order = read(order_path)
order = replace_once(
    order,
    "            public bool Delivered { get; set; }\n            public bool DeliveryUncertain { get; set; }",
    "            public bool Delivered { get; set; }\n            public bool DeliveryUncertain { get; set; }\n            public bool InFlight { get; set; }",
    "add durable inflight flag")

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

                    ReloadAndMergeActionStateLocked(path);
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
order = replace_between(
    order,
    "        internal static bool TryBeginExecution(OrderPlacedReplyPlan plan, out string reason)",
    "        internal static void MarkDeliveryUncertain(OrderPlacedReplyPlan plan, string reason)",
    try_begin,
    "replace TryBeginExecution")

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
                    ReloadAndMergeActionStateLocked(path);
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
order = replace_between(
    order,
    "        internal static void MarkDeliveryUncertain(OrderPlacedReplyPlan plan, string reason)",
    "        internal static void FinishExecution(OrderPlacedReplyPlan plan, bool delivered, int sentSegments)",
    mark_uncertain,
    "replace MarkDeliveryUncertain")

finish_execution = r'''        internal static void FinishExecution(OrderPlacedReplyPlan plan, bool delivered, int sentSegments)
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
                    ReloadAndMergeActionStateLocked(path);
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
order = replace_between(
    order,
    "        internal static void FinishExecution(OrderPlacedReplyPlan plan, bool delivered, int sentSegments)",
    "        private static void ObserveCanonicalOrderId(string seller, string buyer, string orderId)",
    finish_execution,
    "replace FinishExecution")

observe_canonical = r'''        private static void ObserveCanonicalOrderId(string seller, string buyer, string orderId)
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
                    ReloadAndMergeActionStateLocked(path);
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
order = replace_between(
    order,
    "        private static void ObserveCanonicalOrderId(string seller, string buyer, string orderId)",
    "        private static string FindCanonicalOrderIdLocked(string seller, string buyer, string orderId, bool requireExactCandidate = false)",
    observe_canonical,
    "replace ObserveCanonicalOrderId")

state_helpers = r'''        private static void EnsureActionStateLoadedLocked()
        {
            if (_actionState != null) return;
            _actionState = ReadActionStateFromDiskLocked(GetActionStatePath());
        }

        private static OrderReplyActionState ReadActionStateFromDiskLocked(string path)
        {
            string error;
            var raw = CrossProcessAtomicStateFile.ReadAllTextShared(path, 4, 60, out error);
            if (!string.IsNullOrWhiteSpace(error))
            {
                Log.ErrorWithMaxCount("读取订单自动回复动作幂等状态失败，保留可用内存状态：" + Short(error, 220), 10);
                return new OrderReplyActionState();
            }
            if (string.IsNullOrWhiteSpace(raw)) return new OrderReplyActionState();
            try
            {
                var state = JsonConvert.DeserializeObject<OrderReplyActionState>(raw) ?? new OrderReplyActionState();
                if (state.Records == null) state.Records = new List<OrderReplyActionRecord>();
                return state;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("解析订单自动回复动作幂等状态失败，保留可用内存状态：" + Short(ex.Message, 220), 10);
                return new OrderReplyActionState();
            }
        }

        private static void ReloadAndMergeActionStateLocked(string path)
        {
            var disk = ReadActionStateFromDiskLocked(path);
            if (_actionState == null || _actionState.Records == null || _actionState.Records.Count == 0)
            {
                _actionState = disk;
                return;
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
        }

        private static bool SameStoredAction(OrderReplyActionRecord left, OrderReplyActionRecord right)
        {
            if (left == null || right == null || left.FollowUp != right.FollowUp) return false;
            return Normalize(left.Seller) == Normalize(right.Seller)
                && Normalize(left.Buyer) == Normalize(right.Buyer)
                && (string.Equals((left.OrderId ?? string.Empty).Trim(), (right.OrderId ?? string.Empty).Trim(), StringComparison.Ordinal)
                    || ArePrecisionAliases(left.OrderId, right.OrderId));
        }

        private static bool SaveActionStateLocked(string path)
        {
            if (_actionState == null) return true;
            string error;
            var ok = CrossProcessAtomicStateFile.WriteAllTextAtomic(
                path,
                JsonConvert.SerializeObject(_actionState, Formatting.Indented),
                4,
                60,
                out error);
            if (!ok)
            {
                Log.ErrorWithMaxCount("保存订单自动回复动作幂等状态失败；旧有效文件已保留：" + Short(error, 220), 10);
            }
            return ok;
        }

'''
order = replace_between(
    order,
    "        private static void EnsureActionStateLoadedLocked()",
    "        private static string GetActionStatePath()",
    state_helpers,
    "replace action state IO")

order = replace_once(
    order,
    "            public int SentSegments { get; set; }\n        }",
    "            public int SentSegments { get; set; }\n            public int SatisfiedSegments { get; set; }\n        }",
    "add satisfied segment count")

segment_helper = r'''        private async Task<bool> IsOrderPresetSegmentAlreadySatisfiedAsync(OrderPlacedReplyPlan plan, string text)
        {
            if (plan == null || string.IsNullOrWhiteSpace(text)) return false;
            var expected = BotOutboundMessageFormatter.StripAiMarker(text).Trim();
            if (expected.Length == 0) return false;
            var since = plan.IsBuyerFollowUp && plan.TriggerTime != DateTime.MinValue
                ? plan.TriggerTime.AddSeconds(-5)
                : plan.EventTime.AddSeconds(-20);

            if (HasRecentSellerEcho(plan.Buyer, expected, since))
            {
                Log.Info("下单固定预设分段已由人工/现有卖家实时回显精确满足，跳过本段但继续后续分段: seller="
                    + plan.Seller + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId);
                return true;
            }

            var remote = await VerifySellerEchoInRemoteHistoryAsync(
                plan.Seller,
                plan.Buyer,
                expected,
                since).ConfigureAwait(false);
            if (remote == RemoteSellerEchoVerification.Delivered)
            {
                Log.Info("下单固定预设分段已由远端卖家历史精确满足，跳过本段但继续后续分段: seller="
                    + plan.Seller + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId);
                return true;
            }
            if (remote == RemoteSellerEchoVerification.Unavailable)
            {
                Log.Info("下单固定预设分段发送前远端历史不可用；没有精确已满足证据，继续执行配置的订单动作: seller="
                    + plan.Seller + ", buyer=" + plan.Buyer + ", orderId=" + plan.OrderId);
            }
            return false;
        }

'''
order = replace_once(
    order,
    "        private async Task<OrderPresetSendResult> SendOrderPresetAnswerAsync(OrderPlacedReplyPlan plan, string answer)\n",
    segment_helper + "        private async Task<OrderPresetSendResult> SendOrderPresetAnswerAsync(OrderPlacedReplyPlan plan, string answer)\n",
    "insert exact segment satisfaction helper")

old_loop = r'''            for (var i = 0; i < segments.Count; i++)
            {
                if (i > 0) await Task.Delay(220);
                Log.Info("下单固定预设分段强制自动发送: buyer=" + plan.Buyer
                    + ", segment=" + (i + 1) + "/" + segments.Count
                    + ", manualReplyDoesNotSuppress=true");
                if (!await SendMandatoryOrderTextAsync(plan, segments[i]))
                {
                    result.Success = false;
                    return result;
                }
                result.SentSegments++;
            }
            result.Success = true;
            return result;'''
new_loop = r'''            for (var i = 0; i < segments.Count; i++)
            {
                if (i > 0) await Task.Delay(220);
                if (await IsOrderPresetSegmentAlreadySatisfiedAsync(plan, segments[i]).ConfigureAwait(false))
                {
                    result.SatisfiedSegments++;
                    continue;
                }
                Log.Info("下单固定预设分段强制自动发送: buyer=" + plan.Buyer
                    + ", segment=" + (i + 1) + "/" + segments.Count
                    + ", manualReplyDoesNotSuppress=true, exactSellerEchoSatisfied=false");
                if (!await SendMandatoryOrderTextAsync(plan, segments[i]))
                {
                    result.Success = false;
                    return result;
                }
                result.SentSegments++;
            }
            result.Success = result.SentSegments + result.SatisfiedSegments == segments.Count;
            Log.Info("下单固定预设分段动作完成: buyer=" + plan.Buyer
                + ", orderId=" + plan.OrderId
                + ", botSentSegments=" + result.SentSegments
                + ", exactSellerEchoSatisfiedSegments=" + result.SatisfiedSegments
                + ", totalSegments=" + segments.Count);
            return result;'''
order = replace_once(order, old_loop, new_loop, "replace preset segment loop")

if "SaveActionStateLocked();" in order:
    raise RuntimeError("legacy non-atomic SaveActionStateLocked call remains")
if "File.Delete(path)" in order[order.find("private static void EnsureActionStateLoadedLocked"):order.find("private static string GetActionStatePath")]:
    raise RuntimeError("delete-then-move action state persistence remains")
write(order_path, order)

# Platform blocker: detect before any action, after each unconfirmed action, and reject clicks into sibling roots.
native_path = "src/Bot/ChromeNs/QNRpa.NativeSend.cs"
native = read(native_path)
native = replace_once(
    native,
    "            var domTriggered = await TryTriggerSendViaCdpDomAsync(buyer).ConfigureAwait(false);",
    "            if (await StopIfPlatformSendBlockedAsync(buyer, \"发送前\").ConfigureAwait(false)) return false;\n\n            var domTriggered = await TryTriggerSendViaCdpDomAsync(buyer).ConfigureAwait(false);",
    "platform guard before first send action")
native = replace_once(
    native,
    "                // A click may have reached Qianniu while the echo is late. Never perform another\n                // action unless the exact owned draft is still present.",
    "                if (await StopIfPlatformSendBlockedAsync(buyer, \"CDP页面发送按钮后\").ConfigureAwait(false)) return false;\n\n                // A click may have reached Qianniu while the echo is late. Never perform another\n                // action unless the exact owned draft is still present.",
    "platform guard after cdp action")
native = replace_once(
    native,
    "                if (!await HasExpectedDraftFastAsync(text, 800).ConfigureAwait(false))\n                {\n                    return await WaitForTextSendConfirmedAsync(\n                        buyer, text, sendStart, \"HWND安全消息延迟确认\", 2200).ConfigureAwait(false);\n                }\n                ResetSendFailure();",
    "                if (await StopIfPlatformSendBlockedAsync(buyer, \"HWND安全消息后\").ConfigureAwait(false)) return false;\n                if (!await HasExpectedDraftFastAsync(text, 800).ConfigureAwait(false))\n                {\n                    return await WaitForTextSendConfirmedAsync(\n                        buyer, text, sendStart, \"HWND安全消息延迟确认\", 2200).ConfigureAwait(false);\n                }\n                ResetSendFailure();",
    "platform guard after hwnd action")
native = replace_once(
    native,
    "            var uiResult = await TrySendTextViaUiaAsync(buyer, text, sendStart).ConfigureAwait(false);\n            if (!uiResult && _lastSendButtonCoordinateClickRejected)",
    "            var uiResult = await TrySendTextViaUiaAsync(buyer, text, sendStart).ConfigureAwait(false);\n            if (!uiResult && await StopIfPlatformSendBlockedAsync(buyer, \"UIA发送后\").ConfigureAwait(false)) return false;\n            if (!uiResult && _lastSendButtonCoordinateClickRejected)",
    "platform guard after uia action")
native = replace_once(
    native,
    "            if (root != expectedRoot)\n            {\n                Log.Info(\"HWND安全发送允许同一千牛进程的独立根窗口: seller=\" + SellerNick\n                    + \", pid=\" + targetPid + \", expectedRoot=\" + expectedRoot + \", actualRoot=\" + root);\n            }",
    "            if (root != expectedRoot)\n            {\n                SetSendFailure(\"HWND安全发送\", \"安全点被当前千牛进程的独立弹窗覆盖，拒绝向未知根窗口投递点击\");\n                Log.Info(\"HWND安全发送已阻止跨根窗口点击: seller=\" + SellerNick\n                    + \", pid=\" + targetPid + \", expectedRoot=\" + expectedRoot + \", actualRoot=\" + root);\n                return false;\n            }",
    "reject sibling root click")
write(native_path, native)

# Compile the two new helpers in the legacy non-SDK Bot project and WPF temporary projects.
targets_path = "src/Directory.Build.targets"
targets = read(targets_path)
anchor = r'''  <ItemGroup Condition="Exists('$(MSBuildProjectDirectory)\ChromeNs\QNRpa.ReliableSend.cs')">
    <Compile Include="$(MSBuildProjectDirectory)\ChromeNs\QNRpa.ReliableSend.cs" />
  </ItemGroup>
'''
addition = anchor + r'''  <ItemGroup Condition="Exists('$(MSBuildProjectDirectory)\ChromeNs\QNRpa.PlatformSendGuard.cs')">
    <Compile Include="$(MSBuildProjectDirectory)\ChromeNs\QNRpa.PlatformSendGuard.cs" />
  </ItemGroup>
  <ItemGroup Condition="Exists('$(MSBuildProjectDirectory)\ChromeNs\CrossProcessAtomicStateFile.cs')">
    <Compile Include="$(MSBuildProjectDirectory)\ChromeNs\CrossProcessAtomicStateFile.cs" />
  </ItemGroup>
'''
targets = replace_once(targets, anchor, addition, "wire runtime guard helpers")
write(targets_path, targets)

# Migrate the legacy test from 'manual can never satisfy any segment' to the new precise business rule.
manual_test_path = "tests/test_order_preset_manual_segment_continuation_static.py"
manual_test = read(manual_test_path)
manual_test = replace_once(
    manual_test,
    "def test_order_fixed_preset_sends_all_segments_even_after_manual_takeover():\n    source = text(\"src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs\")\n    assert \"SendOrderPresetAnswerAsync\" in source\n    assert \"SendMandatoryOrderTextAsync\" in source\n    assert \"KnowledgeLearningService.AllowNextManualSend(plan.Seller, plan.Buyer, text)\" in source\n    assert \"OrderPresetSegmentOutcome.CancelledByManual\" not in source\n    assert \"SatisfiedByManual\" not in source\n    assert \"停止本段及全部剩余分段\" not in source\n    assert \"manualReplyDoesNotSuppress=true\" in source\n",
    "def test_order_fixed_preset_continues_after_manual_takeover_but_skips_only_exactly_satisfied_segment():\n    source = text(\"src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs\")\n    assert \"SendOrderPresetAnswerAsync\" in source\n    assert \"SendMandatoryOrderTextAsync\" in source\n    assert \"KnowledgeLearningService.AllowNextManualSend(plan.Seller, plan.Buyer, text)\" in source\n    assert \"IsOrderPresetSegmentAlreadySatisfiedAsync\" in source\n    assert \"VerifySellerEchoInRemoteHistoryAsync\" in source\n    assert \"BotOutboundMessageFormatter.StripAiMarker(text)\" in source\n    assert \"result.SatisfiedSegments++\" in source\n    assert \"continue;\" in source[source.index(\"result.SatisfiedSegments++\"):source.index(\"Log.Info(\\\"下单固定预设分段强制自动发送\") ]\n    assert \"OrderPresetSegmentOutcome.CancelledByManual\" not in source\n    assert \"停止本段及全部剩余分段\" not in source\n    assert \"manualReplyDoesNotSuppress=true\" in source\n",
    "migrate manual segment semantics test")
write(manual_test_path, manual_test)

write("tests/test_order_action_cross_process_ledger_static.py", r'''from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_action_ledger_persists_cross_process_inflight_before_send():
    code = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    begin = code[code.index("internal static bool TryBeginExecution"):code.index("internal static void MarkDeliveryUncertain")]
    assert "public bool InFlight" in code
    assert 'CrossProcessAtomicStateFile.Acquire(path, "OrderReplyActionState", 3000)' in begin
    assert "ReloadAndMergeActionStateLocked(path)" in begin
    assert "action_inflight_cross_process" in begin
    assert "durable.InFlight = true" in begin
    assert "SaveActionStateLocked(path)" in begin
    assert "action_state_persist_failed" in begin


def test_action_ledger_write_is_atomic_and_old_valid_file_is_never_deleted_first():
    helper = read("src/Bot/ChromeNs/CrossProcessAtomicStateFile.cs")
    assert "Guid.NewGuid().ToString(\"N\")" in helper
    assert "FileMode.CreateNew" in helper
    assert "FileOptions.WriteThrough" in helper
    assert "stream.Flush(true)" in helper
    assert "File.Replace(temp, path, null, true)" in helper
    assert "File.Delete(path)" not in helper
    order = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    state = order[order.index("private static void EnsureActionStateLoadedLocked"):order.index("private static string GetActionStatePath")]
    assert "ReadAllTextShared" in state
    assert "WriteAllTextAtomic" in state
    assert "File.WriteAllText" not in state
    assert "File.Delete(path)" not in state


def test_finish_and_uncertain_paths_clear_durable_inflight_under_same_mutex():
    code = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    uncertain = code[code.index("internal static void MarkDeliveryUncertain"):code.index("internal static void FinishExecution")]
    finish = code[code.index("internal static void FinishExecution"):code.index("private static void ObserveCanonicalOrderId")]
    assert "existing.InFlight = false" in uncertain
    assert "existing.DeliveryUncertain = true" in uncertain
    assert "existing.InFlight = false" in finish
    assert 'CrossProcessAtomicStateFile.Acquire(path, "OrderReplyActionState", 3000)' in finish
''')

write("tests/test_qianniu_service_attitude_block_static.py", r'''from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_service_attitude_prompt_is_a_terminal_platform_block_not_an_auto_confirm():
    guard = read("src/Bot/ChromeNs/QNRpa.PlatformSendGuard.cs")
    assert "服务态度提醒" in guard
    assert "继续发送" in guard
    assert 'SetSendCancellation("平台发送拦截"' in guard
    assert "Bot禁止自动确认" in guard
    assert ".Click(" not in guard
    assert ".Invoke(" not in guard


def test_native_send_checks_platform_block_between_every_physical_send_fallback():
    native = read("src/Bot/ChromeNs/QNRpa.NativeSend.cs")
    first = native.index('StopIfPlatformSendBlockedAsync(buyer, "发送前")')
    cdp = native.index("TryTriggerSendViaCdpDomAsync", first)
    after_cdp = native.index('StopIfPlatformSendBlockedAsync(buyer, "CDP页面发送按钮后")', cdp)
    hwnd = native.index("TryPostSafeMainSendMouseMessage", after_cdp)
    after_hwnd = native.index('StopIfPlatformSendBlockedAsync(buyer, "HWND安全消息后")', hwnd)
    uia = native.index("TrySendTextViaUiaAsync", after_hwnd)
    after_uia = native.index('StopIfPlatformSendBlockedAsync(buyer, "UIA发送后")', uia)
    assert first < cdp < after_cdp < hwnd < after_hwnd < uia < after_uia


def test_hwnd_sender_never_clicks_a_modal_or_other_sibling_root_window():
    native = read("src/Bot/ChromeNs/QNRpa.NativeSend.cs")
    block = native[native.index("if (root != expectedRoot)"):][:700]
    assert "拒绝向未知根窗口投递点击" in block
    assert "return false;" in block
    assert "允许同一千牛进程的独立根窗口" not in block
    qn = read("src/Bot/ChromeNs/QN.cs")
    assert "if (!ok && rpa.LastSendWasCancelled)" in qn
    assert "禁止重试" in qn
''')

# Sanity checks on the final source shape before CI gets it.
assert "action_inflight_cross_process" in order
assert "IsOrderPresetSegmentAlreadySatisfiedAsync" in order
assert "StopIfPlatformSendBlockedAsync" in native
print("PR204 runtime fixes applied successfully")
