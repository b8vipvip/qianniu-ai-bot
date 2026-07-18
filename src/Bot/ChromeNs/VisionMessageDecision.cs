using Bot.ChatRecord;
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

        public static VisionMessageDecision Decide(QNChatMessage message, string text, IncomingMessageDecision safetyDecision, IEnumerable<AiEndpointConfig> endpoints)
        {
            if (safetyDecision == null) return Skip("[未知消息]", "已跳过：消息安全检查失败，未调用AI，也未发送给买家。");
            if (safetyDecision.ShouldCallAi) return new VisionMessageDecision { Kind = VisionDecisionKind.Text, QuestionLabel = text, Note = string.Empty };
            if (!string.Equals(safetyDecision.MessageLabel, "[图片]", StringComparison.Ordinal)) return Skip(safetyDecision.MessageLabel, safetyDecision.Note);
            var usable = (endpoints ?? new AiEndpointConfig[0]).Any(e => e != null && e.Enabled && e.SupportsVision && !string.IsNullOrWhiteSpace(e.VisionModel) && !string.IsNullOrWhiteSpace(e.ApiKey) && !string.IsNullOrWhiteSpace(e.BaseUrl));
            if (!usable) return Skip("[图片]", "已跳过：未配置可用的视觉模型，未向买家发送消息。");
            return new VisionMessageDecision { Kind = VisionDecisionKind.Vision, QuestionLabel = "[图片]", Note = string.Empty };
        }

        private static VisionMessageDecision Skip(string label, string note)
        {
            return new VisionMessageDecision { Kind = VisionDecisionKind.Skip, QuestionLabel = label, Note = note };
        }
    }
}
