using BotLib;
using System.Threading;

namespace Bot
{
    public partial class App
    {
        // Compatibility bootstrap retained so existing startup/build wiring does not change.
        // First-inquiry delivery now lives inside BuyerMessageBurstCoordinator before AI routing.
        private readonly object _firstInquiryStreamingGuardBootstrap =
            ChromeNs.FirstInquiryStreamingGuard.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    /// <summary>
    /// Compatibility marker for the former reflection-based streaming guard.
    ///
    /// Version 1.1.749 repeatedly rewrote BuyerMessageBurstCoordinator._handler in order to stay
    /// outside Smart Reply / vision wrappers. Under repeated Qianniu CDP initialization that could
    /// grow/churn the delegate chain and produced a large amount of runtime work. Deterministic
    /// replies are now executed directly by BuyerMessageBurstCoordinator before any AI gate, so
    /// no timer, reflection, or handler replacement is necessary.
    /// </summary>
    internal static class FirstInquiryStreamingGuard
    {
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                Log.Info(
                    "首条咨询固定回复已切换为协调器前置直发：不再动态重包消息handler，"
                    + "固定回复不等待AI接口。" );
            }
            return new object();
        }
    }
}
