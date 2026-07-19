using Bot.Options;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal static class HandoffNotificationService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly ConcurrentDictionary<string, DateTime> Recent = new ConcurrentDictionary<string, DateTime>();

        public static void QueueNotify(
            string seller,
            string buyer,
            string question,
            AutoReplyRuleDecision decision)
        {
            var cfg = BotFeatureStore.GetAutoReplyRules();
            if (cfg == null || !cfg.EnableHandoffNotification || decision == null || !decision.Matched) return;
            var key = Normalize(seller) + "#" + Normalize(buyer) + "#" + Normalize(question);
            var now = DateTime.Now;
            DateTime until;
            if (Recent.TryGetValue(key, out until) && until > now) return;
            Recent[key] = now.AddMinutes(Math.Max(1, cfg.NotificationCooldownMinutes));
            Task.Run(async () =>
            {
                try
                {
                    var result = await SendAsync(cfg, BuildMessage(seller, buyer, question, decision));
                    Log.Info("转人工通知结果：" + result);
                }
                catch (Exception ex)
                {
                    Log.Info("转人工通知异常：" + ex.Message);
                }
            });
        }

        public static async Task<string> TestAsync(AutoReplyRuleConfig cfg)
        {
            cfg = cfg ?? AutoReplyRuleConfig.Default();
            return await SendAsync(cfg,
                "【千牛Bot测试通知】\n时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                + "\n这是一条转人工通知通道测试消息。");
        }

        private static string BuildMessage(
            string seller,
            string buyer,
            string question,
            AutoReplyRuleDecision decision)
        {
            return "【千牛Bot转人工提醒】"
                + "\n时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                + "\n客服：" + Safe(seller, 80)
                + "\n买家：" + Safe(buyer, 80)
                + "\n状态：" + (decision.IsOffHours ? "人工客服下班" : "人工客服工作时间")
                + "\n原因：" + Safe(decision.Reason, 200)
                + "\n问题：" + Safe(question, 500);
        }

        private static async Task<string> SendAsync(AutoReplyRuleConfig cfg, string message)
        {
            var results = new List<string>();
            if (cfg.NotifyWeChat)
            {
                results.Add("微信=" + await PostJson(cfg.WeChatWebhook,
                    new JObject { ["msgtype"] = "text", ["text"] = new JObject { ["content"] = message } }));
            }
            if (cfg.NotifyQQ)
            {
                results.Add("QQ=" + await PostJson(cfg.QQWebhook,
                    new JObject { ["message"] = message, ["content"] = message, ["text"] = message }));
            }
            if (cfg.NotifyFeishu)
            {
                results.Add("飞书=" + await PostJson(cfg.FeishuWebhook,
                    new JObject { ["msg_type"] = "text", ["content"] = new JObject { ["text"] = message } }));
            }
            if (cfg.NotifyDingTalk)
            {
                results.Add("钉钉=" + await PostJson(cfg.DingTalkWebhook,
                    new JObject { ["msgtype"] = "text", ["text"] = new JObject { ["content"] = message } }));
            }
            if (cfg.NotifyEmail)
            {
                results.Add("邮箱=" + await SendEmail(cfg, message));
            }
            return results.Count == 0 ? "未选择任何通知渠道" : string.Join("；", results);
        }

        private static async Task<string> PostJson(string url, JObject payload)
        {
            Uri uri;
            if (!Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return "未配置有效Webhook";
            }
            try
            {
                using (var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json"))
                using (var response = await Http.PostAsync(uri, content))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        return "HTTP " + (int)response.StatusCode + " " + Short(body, 120);
                    }
                    return "成功";
                }
            }
            catch (Exception ex)
            {
                return "失败：" + Short(ex.Message, 120);
            }
        }

        private static Task<string> SendEmail(AutoReplyRuleConfig cfg, string message)
        {
            return Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(cfg.SmtpHost) || string.IsNullOrWhiteSpace(cfg.EmailTo))
                {
                    return "SMTP服务器或收件人未配置";
                }
                try
                {
                    var recipients = (cfg.EmailTo ?? string.Empty)
                        .Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim())
                        .Where(x => x.Length > 0)
                        .ToList();
                    if (recipients.Count == 0) return "收件人未配置";
                    using (var mail = new MailMessage())
                    using (var client = new SmtpClient(cfg.SmtpHost.Trim(), cfg.SmtpPort <= 0 ? 465 : cfg.SmtpPort))
                    {
                        mail.Subject = "千牛Bot转人工提醒";
                        mail.Body = message;
                        mail.BodyEncoding = Encoding.UTF8;
                        var sender = string.IsNullOrWhiteSpace(cfg.SmtpUser) ? recipients[0] : cfg.SmtpUser.Trim();
                        mail.From = new MailAddress(sender);
                        foreach (var recipient in recipients) mail.To.Add(recipient);
                        client.EnableSsl = cfg.SmtpEnableSsl;
                        if (!string.IsNullOrWhiteSpace(cfg.SmtpUser))
                        {
                            client.Credentials = new NetworkCredential(cfg.SmtpUser.Trim(), cfg.SmtpPassword ?? string.Empty);
                        }
                        client.Send(mail);
                    }
                    return "成功";
                }
                catch (Exception ex)
                {
                    return "失败：" + Short(ex.Message, 120);
                }
            });
        }

        private static string Normalize(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty);
        }

        private static string Safe(string value, int max)
        {
            value = value ?? string.Empty;
            value = Regex.Replace(value, @"(?<!\d)1\d{10}(?!\d)", "[手机号]");
            value = Regex.Replace(value, @"(?i)sk-[a-z0-9_-]{12,}", "[API_KEY]");
            return Short(value, max);
        }

        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
