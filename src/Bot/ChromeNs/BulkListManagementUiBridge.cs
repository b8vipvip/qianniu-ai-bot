using log4net;
using log4net.Appender;
using log4net.Filter;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Bot.ChromeNs
{
    // Keeps startup calls beside the handoff service while the actual list
    // management implementation lives with the knowledge UI types.
    internal static class BulkListManagementUi
    {
        public static void Initialize()
        {
            // The local policy has already been initialized at this point. Run
            // the authenticated legacy import in the background and replace it
            // through SaveRules only when the old server snapshot is available.
            HandoffPolicyLegacyMigrationService.StartOnce();
            Bot.Knowledge.BulkListManagementUi.Initialize();
        }
    }

    // The first implementation of RuntimeLogNoiseFilterBootstrap was intentionally
    // conservative but filtered two signals too broadly. Repair that exact filter in
    // place after startup: SendForGetText must remain visible and only stable
    // qnbotStatus/extra=loop injection status should be hidden.
    internal static class RuntimeLogNoiseSafetyOverride
    {
        private static Timer _timer;
        private static int _started;
        private static int _reported;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
                _timer = new Timer(_ => Apply(), null, 4000, 10000);
            return new object();
        }

        private static void Apply()
        {
            try
            {
                var pattern = BuildPattern();
                foreach (var appender in LogManager.GetRepository().GetAppenders().OfType<AppenderSkeleton>())
                {
                    for (var filter = appender.FilterHead; filter != null; filter = filter.Next)
                    {
                        var regex = filter as RegexFilter;
                        if (regex == null) continue;
                        var current = regex.RegexToMatch ?? string.Empty;
                        if (current.IndexOf("设置界面已将“人工客服工作时间与下班回复”迁移", StringComparison.Ordinal) < 0)
                            continue;
                        if (current.IndexOf("SendForGetText", StringComparison.Ordinal) < 0
                            && current.IndexOf("千牛注入状态:", StringComparison.Ordinal) < 0)
                            continue;
                        regex.RegexToMatch = pattern;
                        regex.ActivateOptions();
                    }
                }
                if (Interlocked.Exchange(ref _reported, 1) == 0)
                    Log.Info("运行日志降噪安全边界已校正：SendForGetText与异常注入状态继续保留，仅过滤稳定成功探测。");
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _reported, 1) == 0)
                    Log.ErrorWithMaxCount("运行日志降噪安全边界校正失败：" + ex.Message, 3);
            }
        }

        private static string BuildPattern()
        {
            var phrases = new[]
            {
                "设置界面已将“人工客服工作时间与下班回复”迁移",
                "设置界面已在构造阶段将“启用转人工规则”",
                "设置界面已直接构造“转人工策略”页面并迁移转人工规则",
                "UIA控件刷新成功:",
                "收到千牛WebSocket事件: type=qnbotStatus",
                "检测到卖家重复千牛WebSocket页面，保留已稳定的权威CDP会话",
                "RPA已绑定卖家专属千牛窗口:",
                "IMSDK璋冪敤璺熻釜:",
                "后台订单面板延迟兜底订单已由其他通道处理/去重"
            };
            return string.Join("|", phrases.Select(Regex.Escape))
                + "|千牛注入状态:.*" + Regex.Escape("\"extra\":\"loop\"");
        }
    }
}

namespace Bot
{
    public partial class App
    {
        private readonly object _runtimeLogNoiseSafetyOverrideBootstrap =
            ChromeNs.RuntimeLogNoiseSafetyOverride.InitializeForApp();
    }
}
