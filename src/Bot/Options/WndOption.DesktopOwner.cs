using Bot.Common.Windows;
using BotLib;
using System.Windows;

namespace Bot.Options
{
    public partial class WndOption
    {
        /// <summary>
        /// Standalone-window overload. Existing WndAssist overload remains unchanged
        /// for all legacy attached-panel callers.
        /// </summary>
        public static void MyShow(string seller, Window owner)
        {
            Util.Assert(!string.IsNullOrEmpty(seller));
            ShowSameNickOneInstance<WndOption>(seller, delegate
            {
                return new WndOption(seller);
            }, owner, true);
        }
    }
}
