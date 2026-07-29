using Bot.ChromeNs;
using Bot.Options;
using Bot.UpdateNs;
using BotLib;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Bot
{
    /// <summary>
    /// App.xaml 的交互逻辑
    /// </summary>
    public partial class App : Application
    {
        public App()
        {
            QianniuWebSocketJsonCompatibility.Initialize();
            RuntimeBuildIdentityService.Initialize();
            LegacyAboutUpdateRedirect.Initialize();
            SlowResponseDiagnosticsUi.Initialize();
            ConversationSessionLearningUi.Initialize();
            ReplyQualityCenterUi.Initialize();
            OrderPlacedReplyDelaySettings.Initialize();
            OrderAttentionSettings.Initialize();
            DirectOrderEventBridge.Initialize();
            OrderPaymentNotificationFallback.Initialize();
            OrderNotificationTraceBridge.Start();
            BotUpdateService.Initialize();
            HandoffRuleRemoteConfigService.Initialize();
            BuyerIdentityAliasRuntimeBridge.Initialize();
            BuyerIdentityAliasUiBridge.Start();
            QnRuntimeSafetyMonitor.Start();
            Bot.Knowledge.KnowledgeOptimizationUi.Initialize();
            Bot.Knowledge.StorePromptProfileUi.Initialize();
            Bot.Knowledge.KnowledgePolicyProfileUi.Initialize();
            ConversationSessionLearningService.Initialize();
            ManualVisualReplyLearningService.Initialize();
            BuyerStreamingReplyPipeline.Initialize();
            VisionWithdrawalAwarePipeline.Initialize();
            VisionFollowUpContextPipeline.Initialize();
            Startup += App_Startup;
            SessionEnding += App_SessionEnding;
            Exit += App_Exit;
            DispatcherUnhandledException += App_DispatcherUnhandledException;
        }

        void App_Exit(object sender, ExitEventArgs e)
        {
            try { AdaptiveReplyTimingService.Flush(); } catch { }
            try { ReplyQualityMetricsService.Flush(); } catch { }
        }

        void App_SessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            try { AdaptiveReplyTimingService.Flush(); } catch { }
            try { ReplyQualityMetricsService.Flush(); } catch { }
        }

        void App_Startup(object sender, StartupEventArgs e)
        {

        }

        void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            if (e.Exception != null)
            {
                Log.Error("出现UnhandledException");
                Log.Exception(e.Exception);
            }
            e.Handled = true;
        }
    }
}
