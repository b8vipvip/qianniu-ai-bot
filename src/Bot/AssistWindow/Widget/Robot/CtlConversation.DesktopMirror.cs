namespace Bot.AssistWindow.Widget.Robot
{
    internal sealed class DesktopConversationSnapshot
    {
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string Question { get; set; }
        public string Answer { get; set; }
        public bool IsAutoReply { get; set; }
        public string AnswerSource { get; set; }
    }

    public partial class CtlConversation
    {
        /// <summary>
        /// Prevents the passive desktop observer from mirroring its own copy again.
        /// </summary>
        internal bool IsDesktopMirror { get; set; }

        internal DesktopConversationSnapshot GetDesktopSnapshot()
        {
            return new DesktopConversationSnapshot
            {
                Seller = _seller ?? string.Empty,
                Buyer = _buyer ?? string.Empty,
                Question = _question ?? string.Empty,
                Answer = _answer ?? string.Empty,
                IsAutoReply = txtStatus != null && (txtStatus.Text ?? string.Empty).StartsWith("正在发送"),
                AnswerSource = txtSource == null ? string.Empty : (txtSource.Text ?? string.Empty)
            };
        }
    }
}
