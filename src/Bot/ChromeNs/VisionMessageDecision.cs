using Bot.ChatRecord;
using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bot.ChromeNs
{
    internal enum VisionDecisionKind { Text, Vision, Skip }

    internal sealed class VisionMessageDecision
    {
        public VisionDecisionKind Kind { get; set; }
        public string QuestionLabel { get; set; }
        public string Note { get; set; }

        public static VisionMessageDecision Decide(
            QNChatMessage message,
            string text,
            IncomingMessageDecision safetyDecision,
            IEnumerable<AiEndpointConfig> endpoints)
        {
            if (safetyDecision == null)
                return Skip("[未知消息]", "已跳过：消息安全检查失败，未调用AI，也未发送给买家。");

            // The first-inquiry fixed reply sits in front of the ordinary content-type router.
            // Therefore text, images, files, emoji, withdrawal/platform tips and other fresh
            // buyer-side events can all trigger the same configured greeting. Historical startup
            // messages remain excluded by FirstInquiryFixedReplyService.TryPrepare.
            var seller = message == null || message.toid == null
                ? string.Empty
                : (message.toid.nick ?? string.Empty).Trim();
            var buyer = message == null || message.fromid == null
                ? string.Empty
                : (message.fromid.nick ?? string.Empty).Trim();
            var firstQuestion = IncomingMessageSafety.GetDisplayText(message, text);
            string fixedAnswer;
            if (FirstInquiryFixedReplyService.TryPrepare(
                seller,
                buyer,
                firstQuestion,
                safetyDecision,
                out fixedAnswer))
            {
                return new VisionMessageDecision
                {
                    Kind = VisionDecisionKind.Text,
                    QuestionLabel = firstQuestion,
                    Note = "本轮首条消息使用固定回复，不调用AI或视觉模型。"
                };
            }

            if (safetyDecision.ShouldCallAi)
                return new VisionMessageDecision
                {
                    Kind = VisionDecisionKind.Text,
                    QuestionLabel = text,
                    Note = string.Empty
                };
            if (!string.Equals(safetyDecision.MessageLabel, "[图片]", StringComparison.Ordinal))
                return Skip(safetyDecision.MessageLabel, safetyDecision.Note);

            var selectedEndpoints = ResolveShopVisionEndpoints(message, endpoints);
            var usable = (selectedEndpoints ?? new AiEndpointConfig[0]).Any(
                e => e != null
                    && e.Enabled
                    && e.SupportsVision
                    && !string.IsNullOrWhiteSpace(e.VisionModel)
                    && !string.IsNullOrWhiteSpace(e.ApiKey)
                    && !string.IsNullOrWhiteSpace(e.BaseUrl));
            if (!usable)
                return Skip("[图片]", "已跳过：本店未配置可用的视觉模型，未向买家发送消息。");
            return new VisionMessageDecision
            {
                Kind = VisionDecisionKind.Vision,
                QuestionLabel = "[图片]",
                Note = string.Empty
            };
        }

        private static IEnumerable<AiEndpointConfig> ResolveShopVisionEndpoints(
            QNChatMessage message,
            IEnumerable<AiEndpointConfig> fallback)
        {
            try
            {
                var sellerNick = message == null || message.toid == null
                    ? string.Empty
                    : (message.toid.nick ?? string.Empty).Trim();
                if (sellerNick.Length == 0) return fallback;

                var shop = ShopContextLocator.ResolveRuntimeBySellerNick(sellerNick);
                using (ShopSettingsScope.Enter(shop))
                {
                    return AiEndpointStore.GetVisionEnabledEndpoints();
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount(
                    "视觉消息未能解析店铺 AI 配置，使用旧全局配置兼容模式：" + Safe(ex.Message, 220),
                    20);
                return fallback;
            }
        }

        private static VisionMessageDecision Skip(string label, string note)
        {
            return new VisionMessageDecision
            {
                Kind = VisionDecisionKind.Skip,
                QuestionLabel = label,
                Note = note
            };
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
