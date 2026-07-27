using Bot.ChatRecord;
using BotLib;
using DbEntity;
using DbEntity.Response;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace Bot.ChromeNs
{
    internal static class QnRuntimeSafetyMonitor
    {
        private static readonly ConcurrentDictionary<QN, byte> Subscribed =
            new ConcurrentDictionary<QN, byte>();
        private static readonly ConcurrentDictionary<QN, byte> VersionGuardLogged =
            new ConcurrentDictionary<QN, byte>();
        private static Timer _timer;
        private static int _started;

        public static void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            BuyerIdentityAliasUiBridge.Start();
            OrderNotificationTraceBridge.Start();
            _timer = new Timer(_ => Refresh(), null, 0, 500);
            Log.Info("千牛发送与人工介入安全监控已启动。");
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
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("刷新千牛运行时安全监控失败：" + ex.Message, 5);
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
