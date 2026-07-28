using Bot.ChatRecord;
using BotLib;
using DbEntity;
using DbEntity.Response;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal static class QnRuntimeSafetyMonitor
    {
        private const int ConversationProbeIntervalMilliseconds = 2500;
        private const int HeartbeatIntervalSeconds = 60;

        private static readonly ConcurrentDictionary<QN, byte> Subscribed =
            new ConcurrentDictionary<QN, byte>();
        private static readonly ConcurrentDictionary<QN, byte> VersionGuardLogged =
            new ConcurrentDictionary<QN, byte>();
        private static readonly ConcurrentDictionary<QN, DateTime> NextConversationProbeAt =
            new ConcurrentDictionary<QN, DateTime>();
        private static readonly ConcurrentDictionary<QN, byte> ConversationProbeRunning =
            new ConcurrentDictionary<QN, byte>();
        private static readonly ConcurrentDictionary<QN, int> ConsecutiveProbeFailures =
            new ConcurrentDictionary<QN, int>();

        private static Timer _timer;
        private static int _started;
        private static long _lastHeartbeatUtcTicks;

        public static void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            BuyerIdentityAliasUiBridge.Start();
            OrderNotificationTraceBridge.Start();
            _timer = new Timer(_ => Refresh(), null, 0, 500);
            Log.Info("千牛发送、当前买家与人工介入安全监控已启动。");
        }

        private static void Refresh()
        {
            try
            {
                foreach (var qn in QN.GetRuntimeSafetySnapshot())
                {
                    if (qn == null) continue;
                    NormalizeUnknownVersion(qn);
                    if (Subscribed.TryAdd(qn, 0))
                    {
                        qn.EvRecieveNewMessage += Qn_EvRecieveNewMessage;
                    }
                    ScheduleConversationProbe(qn);
                }
                WriteHeartbeatIfDue();
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("刷新千牛运行时安全监控失败：" + ex.Message, 5);
            }
        }

        private static void ScheduleConversationProbe(QN qn)
        {
            if (qn == null || qn.CDP == null || qn.Seller == null
                || string.IsNullOrWhiteSpace(qn.Seller.Nick))
            {
                return;
            }

            var now = DateTime.UtcNow;
            DateTime next;
            if (NextConversationProbeAt.TryGetValue(qn, out next) && next > now) return;
            NextConversationProbeAt[qn] = now.AddMilliseconds(ConversationProbeIntervalMilliseconds);
            if (!ConversationProbeRunning.TryAdd(qn, 0)) return;

            Task.Run(async () =>
            {
                try
                {
                    await ProbeCurrentConversationAsync(qn);
                }
                catch (Exception ex)
                {
                    RecordProbeFailure(qn, ex.Message);
                }
                finally
                {
                    byte ignored;
                    ConversationProbeRunning.TryRemove(qn, out ignored);
                }
            });
        }

        private static async Task ProbeCurrentConversationAsync(QN qn)
        {
            var seller = qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
            if (seller.Length == 0 || qn.CDP == null) return;

            var first = await qn.GetCurrentConversationID();
            var firstNick = ReadConversationNick(first);
            if (firstNick.Length == 0)
            {
                RecordProbeFailure(qn, "im.uiutil.GetCurrentConversationID 返回空值");
                return;
            }

            var currentNick = qn.Buyer == null ? string.Empty : (qn.Buyer.Nick ?? string.Empty).Trim();
            if (AreSameBuyer(seller, currentNick, firstNick))
            {
                RecordProbeSuccess(qn, seller, firstNick, false);
                return;
            }

            // 千牛切换会话时内部对象会短暂经过空值或旧值。连续读取两次一致后才修正，
            // 避免一次瞬态结果改变 Bot 当前买家；真正发送仍会执行独立的严格会话确认。
            await Task.Delay(220);
            var second = await qn.GetCurrentConversationID();
            var secondNick = ReadConversationNick(second);
            if (secondNick.Length == 0 || !AreSameBuyer(seller, firstNick, secondNick))
            {
                RecordProbeFailure(qn, "当前会话连续两次读取不稳定: first=" + firstNick + ", second=" + secondNick);
                return;
            }

            var resolved = BuyerIdentityAliasService.ResolveInternalNick(seller, secondNick);
            qn.SetActiveConversationByNick(seller, resolved, "runtimeConversationProbe");
            Log.Info("当前买家由主动探测修正: seller=" + seller
                + ", previous=" + currentNick + ", current=" + resolved);
            RecordProbeSuccess(qn, seller, resolved, true);
        }

        private static string ReadConversationNick(ConversationResponse response)
        {
            return response == null || response.Result == null
                ? string.Empty
                : (response.Result.Nick ?? string.Empty).Trim();
        }

        private static bool AreSameBuyer(string seller, string left, string right)
        {
            left = (left ?? string.Empty).Trim();
            right = (right ?? string.Empty).Trim();
            if (left.Length == 0 || right.Length == 0) return false;
            if (string.Equals(left, right, StringComparison.Ordinal)) return true;
            return BuyerIdentityAliasService.AreEquivalent(seller, left, right);
        }

        private static void RecordProbeSuccess(QN qn, string seller, string buyer, bool corrected)
        {
            int failures;
            ConsecutiveProbeFailures.TryRemove(qn, out failures);
            BotConnectionDiagnostics.RecordCdpStatus(true,
                corrected ? "主动探测已修正当前买家" : "当前买家主动探测正常",
                seller,
                buyer);
            BotConnectionDiagnostics.RecordBuyerSeller(seller, buyer);
            if (failures > 0)
            {
                Log.Info("当前买家主动探测已恢复: seller=" + seller
                    + ", buyer=" + buyer + ", previousFailures=" + failures);
            }
        }

        private static void RecordProbeFailure(QN qn, string reason)
        {
            var failures = ConsecutiveProbeFailures.AddOrUpdate(qn, 1, (_, count) => count + 1);
            var seller = qn == null || qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
            var buyer = qn == null || qn.Buyer == null ? string.Empty : (qn.Buyer.Nick ?? string.Empty).Trim();
            if (failures >= 2)
            {
                BotConnectionDiagnostics.RecordCdpStatus(false,
                    "当前买家主动探测连续失败" + failures + "次，等待注入/CDP恢复",
                    seller,
                    buyer);
            }
            if (failures == 1 || failures == 3 || failures % 10 == 0)
            {
                Log.Error("当前买家主动探测失败: seller=" + seller
                    + ", cachedBuyer=" + buyer + ", failures=" + failures
                    + ", reason=" + (reason ?? string.Empty));
            }
        }

        private static void WriteHeartbeatIfDue()
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            var previous = Interlocked.Read(ref _lastHeartbeatUtcTicks);
            if (previous != 0
                && new TimeSpan(nowTicks - previous).TotalSeconds < HeartbeatIntervalSeconds)
            {
                return;
            }
            if (Interlocked.CompareExchange(ref _lastHeartbeatUtcTicks, nowTicks, previous) != previous) return;

            try
            {
                var snapshot = BotConnectionDiagnostics.GetSnapshot();
                var age = snapshot == null || snapshot.LastUpdateTime == DateTime.MinValue
                    ? -1
                    : Math.Max(0, (long)(DateTime.Now - snapshot.LastUpdateTime).TotalSeconds);
                Log.Info("Bot运行心跳: ws=" + (snapshot == null ? string.Empty : snapshot.WebSocketStatus)
                    + ", injection=" + (snapshot == null ? string.Empty : snapshot.InjectionStatus)
                    + ", qn=" + (snapshot == null ? string.Empty : snapshot.QnParamStatus)
                    + ", seller=" + (snapshot == null ? string.Empty : snapshot.Seller)
                    + ", buyer=" + (snapshot == null ? string.Empty : snapshot.Buyer)
                    + ", diagnosticsAgeSeconds=" + age);
            }
            catch (Exception ex)
            {
                Log.Info("Bot运行心跳写入失败: " + ex.Message);
            }
        }

        private static void NormalizeUnknownVersion(QN qn)
        {
            var value = (qn.QnVersion ?? string.Empty).Trim();
            Version parsed;
            var normalized = value.TrimEnd('N', 'n');
            if (!string.IsNullOrWhiteSpace(value) && Version.TryParse(normalized, out parsed)) return;

            // QN.SendTextAsync 的历史分支使用字符串比较。空版本会被错误判断成旧版，
            // 继而调用 intelligentservice.SendSmartTipMsg（只是智能提示，不是真实聊天发送）并直接返回成功。
            // 未识别版本必须走当前可靠 RPA 发送链路，不能再制造“已发送”的假阳性。
            qn.QnVersion = "999.999.999N";
            if (VersionGuardLogged.TryAdd(qn, 0))
            {
                Log.Info("千牛版本为空或无法解析，已强制启用可靠RPA发送链路，禁止误走SendSmartTipMsg。original=" + value);
            }
        }

        private static void Qn_EvRecieveNewMessage(object sender, RecieveNewMessageEventArgs e)
        {
            var qn = sender as QN;
            if (qn == null || e == null || string.IsNullOrWhiteSpace(e.Message)) return;
            try
            {
                var response = JsonConvert.DeserializeObject<ChatResponse>(e.Message);
                if (response == null || response.result == null) return;
                var seller = qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
                if (seller.Length == 0) return;

                foreach (var message in response.result.Where(x => x != null))
                {
                    if (message.fromid == null || message.toid == null) continue;
                    BuyerIdentityAliasService.ObserveMessage(seller, message);

                    var from = (message.fromid.nick ?? string.Empty).Trim();
                    var buyer = (message.toid.nick ?? string.Empty).Trim();
                    if (!string.Equals(from, seller, StringComparison.Ordinal) || buyer.Length == 0) continue;

                    var text = ExtractMessageText(message);
                    if (text.Length == 0) continue;

                    if (SendDeliveryWatchdog.ConfirmDelivery(seller, buyer, text)
                        || SendDeliveryWatchdog.IsKnownBotAnswer(seller, buyer, text))
                    {
                        continue;
                    }

                    // 卖家回显有时晚于买家的下一条消息到达。此时上一轮发送任务可能已被
                    // “新消息替代”流程移除，导致 watchdog 状态无法再匹配。Bot 的所有自动
                    // 对外回复都带结尾 [AI] 署名；该署名是独立于任务生命周期的作者证据，
                    // 不能把这种迟到的 Bot 回显误判为人工客服介入并取消当前问题的答案。
                    if (IsExplicitBotAuthoredReply(text))
                    {
                        ResponseProgressTracker.MarkDeliveryConfirmed(
                            seller,
                            buyer,
                            text,
                            "通过[AI]署名确认这是Bot卖家回显");
                        Log.Info("卖家消息带Bot署名标记，未判定人工介入: seller=" + seller
                            + ", buyer=" + buyer + ", reply=" + Short(text, 120));
                        continue;
                    }

                    qn.CancelActiveBuyerGeneration(seller, buyer, "检测到客服回复：" + Short(text, 120));
                    ResponseProgressTracker.MarkManualIntervention(seller, buyer, text);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("分析卖家消息以判断人工介入失败：" + ex.Message, 10);
            }
        }

        private static string ExtractMessageText(QNChatMessage message)
        {
            try
            {
                if (message.originalData != null && !string.IsNullOrWhiteSpace(message.originalData.text))
                {
                    return message.originalData.text.Trim();
                }
            }
            catch
            {
            }
            return (message.summary ?? string.Empty).Trim();
        }

        private static bool IsExplicitBotAuthoredReply(string value)
        {
            var compact = new string((value ?? string.Empty)
                .Where(ch => !char.IsWhiteSpace(ch))
                .ToArray());
            return compact.EndsWith("[AI]", StringComparison.OrdinalIgnoreCase)
                || compact.EndsWith("【AI】", StringComparison.OrdinalIgnoreCase)
                || compact.EndsWith("［AI］", StringComparison.OrdinalIgnoreCase);
        }

        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
