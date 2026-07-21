using Bot.ChatRecord;
using BotLib;
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
                    var from = (message.fromid.nick ?? string.Empty).Trim();
                    var buyer = (message.toid.nick ?? string.Empty).Trim();
                    if (!string.Equals(from, seller, StringComparison.Ordinal) || buyer.Length == 0) continue;

                    var text = ExtractMessageText(message);
                    if (text.Length == 0) continue;

                    // 自动回复的卖家回显用于确认真实送达；不能把自己的Bot回显误判成人工客服介入。
                    if (SendDeliveryWatchdog.ConfirmDelivery(seller, buyer, text)
                        || SendDeliveryWatchdog.IsKnownBotAnswer(seller, buyer, text))
                    {
                        continue;
                    }

                    // 与正在生成中的买家问题相比，出现一条不同内容的卖家消息，说明人工客服已经接管。
                    // 让旧 lease 立即失效，AI即使稍后返回也不能再把过时答案发出去。
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

        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
