using Bot.ChatRecord;
using System;

namespace Bot.ChromeNs
{
    internal sealed class VisionReplyTask
    {
        public string SellerNick { get; set; }
        public string BuyerNick { get; set; }
        public string MessageKey { get; set; }
        public QNChatMessage Message { get; set; }
        public DateTime CreatedAt { get; set; }
        public string EndpointId { get; set; }

        public VisionReplyTask()
        {
            CreatedAt = DateTime.Now;
        }
    }
}
