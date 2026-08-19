using BotLib;
using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Repairs the narrow startup case where the Bot process is healthy but the local
    /// 127.0.0.1:41010 listener failed to bind/start during the first bootstrap pass.
    /// It never restarts Qianniu and never changes the active buyer conversation.
    /// </summary>
    internal static class QnStartupConnectionSelfHeal
    {
        private const int WebSocketPort = 41010;
        private static int _started;

        public static void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            Task.Run(() => RunAsync());
        }

        private static async Task RunAsync()
        {
            var delays = new[] { 2000, 3500, 5500, 8000, 11000 };
            for (var attempt = 0; attempt < delays.Length; attempt++)
            {
                await Task.Delay(delays[attempt]);

                try
                {
                    var snapshot = BotConnectionDiagnostics.GetSnapshot();
                    if (snapshot != null && snapshot.WebSocketSessionCount > 0)
                    {
                        Log.Info("千牛启动连接自恢复完成：注入WebSocket已连接，attempt=" + (attempt + 1));
                        return;
                    }

                    if (!IsLoopbackListenerActive())
                    {
                        Log.Error("千牛启动连接自恢复：127.0.0.1:41010 未监听，重新启动Bot WebSocket服务，attempt="
                            + (attempt + 1));
                        MyWebSocketServer.WSocketSvrInst.Start();
                        continue;
                    }

                    if (attempt == delays.Length - 1)
                    {
                        Log.Error("千牛启动连接自恢复结束：Bot WebSocket端口已监听但注入脚本仍未连接；"
                            + "未自动重启千牛，避免破坏登录态。请检查注入页面运行时状态。"
                            + " ws=" + (snapshot == null ? string.Empty : snapshot.WebSocketStatus)
                            + ", injection=" + (snapshot == null ? string.Empty : snapshot.InjectionStatus));
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("千牛启动连接自恢复检查失败：" + ex.Message, 5);
                }
            }
        }

        private static bool IsLoopbackListenerActive()
        {
            try
            {
                return IPGlobalProperties.GetIPGlobalProperties()
                    .GetActiveTcpListeners()
                    .Any(endpoint => endpoint.Port == WebSocketPort
                        && (IPAddress.IsLoopback(endpoint.Address)
                            || endpoint.Address.Equals(IPAddress.Any)
                            || endpoint.Address.Equals(IPAddress.IPv6Any)));
            }
            catch
            {
                return false;
            }
        }
    }
}
