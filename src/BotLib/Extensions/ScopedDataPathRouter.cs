using System;

namespace BotLib.Extensions
{
    /// <summary>
    /// Optional bridge that lets the Windows Bot redirect legacy DataDir callers to the
    /// currently executing shop without introducing a Bot dependency into BotLib.
    /// </summary>
    public static class ScopedDataPathRouter
    {
        public delegate bool TryResolveHandler(out string dataRoot);

        private static readonly object Sync = new object();
        private static TryResolveHandler _resolver;

        public static void Configure(TryResolveHandler resolver)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            lock (Sync)
            {
                _resolver = resolver;
            }
        }

        public static bool TryResolve(out string dataRoot)
        {
            dataRoot = string.Empty;
            TryResolveHandler resolver;
            lock (Sync) resolver = _resolver;
            if (resolver == null) return false;
            try
            {
                if (!resolver(out dataRoot)) return false;
                dataRoot = (dataRoot ?? string.Empty).Trim();
                return dataRoot.Length > 0;
            }
            catch
            {
                dataRoot = string.Empty;
                return false;
            }
        }
    }
}
