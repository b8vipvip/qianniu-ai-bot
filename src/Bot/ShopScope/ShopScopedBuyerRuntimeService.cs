using Bot.ChromeNs;
using BotLib;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Bot
{
    public partial class App
    {
        private readonly object _shopScopedBuyerRuntimeBootstrap =
            ShopScope.ShopScopedBuyerRuntimeService.InitializeForApp();
    }
}

namespace Bot.ShopScope
{
    /// <summary>
    /// Carries an explicit ShopContext through the existing buyer-burst pipeline without using
    /// QN.CurQN. The temporary gate prevents the legacy MyOpenAI static prompt/config cache from
    /// being overwritten by another shop while one reply is being generated.
    /// </summary>
    internal static class ShopScopedBuyerRuntimeService
    {
        private static readonly ConcurrentDictionary<int, Func<BuyerMessageBurstLease, Task>> InstalledWrappers =
            new ConcurrentDictionary<int, Func<BuyerMessageBurstLease, Task>>();
        private static readonly ConcurrentDictionary<int, byte> ReplacementWarnings =
            new ConcurrentDictionary<int, byte>();
        private static readonly SemaphoreSlim LegacyAiConfigurationGate = new SemaphoreSlim(1, 1);

        private static Timer _patchTimer;
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                PatchExisting();
                _patchTimer = new Timer(_ => PatchExisting(), null, 400, 900);
                Log.Info("多店铺 AI 配置作用域已启动：买家回复按 QN 实例绑定 ShopContext。" );
            }
            return new object();
        }

        private static void PatchExisting()
        {
            try
            {
                QN[] qns;
                try { qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray(); }
                catch { return; }

                var coordinatorField = typeof(QN).GetField(
                    "_buyerMessageBurstCoordinator",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var handlerField = typeof(BuyerMessageBurstCoordinator).GetField(
                    "_handler",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (coordinatorField == null || handlerField == null) return;

                foreach (var qn in qns)
                {
                    if (qn == null) continue;
                    var coordinator = coordinatorField.GetValue(qn) as BuyerMessageBurstCoordinator;
                    if (coordinator == null) continue;
                    var key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(coordinator);
                    var current = handlerField.GetValue(coordinator) as Func<BuyerMessageBurstLease, Task>;
                    if (current == null) continue;

                    Func<BuyerMessageBurstLease, Task> installed;
                    if (InstalledWrappers.TryGetValue(key, out installed))
                    {
                        if (!ReferenceEquals(current, installed)
                            && ReplacementWarnings.TryAdd(key, 0))
                        {
                            Log.Info("多店铺作用域处理器已被其他模块继续包装；保持单次安装，避免闭包链增长。" );
                        }
                        continue;
                    }

                    var capturedQn = qn;
                    var next = current;
                    Func<BuyerMessageBurstLease, Task> wrapped =
                        lease => HandleScopedAsync(capturedQn, next, lease);
                    handlerField.SetValue(coordinator, wrapped);
                    InstalledWrappers[key] = wrapped;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("安装多店铺 AI 配置作用域失败：" + Safe(ex.Message, 260), 10);
            }
        }

        private static async Task HandleScopedAsync(
            QN qn,
            Func<BuyerMessageBurstLease, Task> next,
            BuyerMessageBurstLease lease)
        {
            if (next == null) return;

            ShopContext shop = null;
            try
            {
                if (qn != null && qn.Seller != null)
                    shop = ShopIdentityResolver.Resolve(qn.Seller);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("买家回复未能解析店铺身份，使用旧全局 AI 配置兼容模式："
                    + Safe(ex.Message, 220), 20);
            }

            if (shop == null)
            {
                await next(lease);
                return;
            }

            await LegacyAiConfigurationGate.WaitAsync();
            try
            {
                using (ShopSettingsScope.Enter(shop))
                {
                    await next(lease);
                }
            }
            finally
            {
                LegacyAiConfigurationGate.Release();
            }
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
