using System;

namespace BotLib
{
    /// <summary>
    /// Optional Bot-provided mirror for logs emitted while a ShopContext is active.
    /// The primary process log remains available for startup/crash diagnostics.
    /// </summary>
    public static class ScopedLogRouter
    {
        public delegate void WriteHandler(string tag, string text);

        private static readonly object Sync = new object();
        private static WriteHandler _writer;

        public static void Configure(WriteHandler writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            lock (Sync)
            {
                _writer = writer;
            }
        }

        public static void TryWrite(string tag, string text)
        {
            WriteHandler writer;
            lock (Sync) writer = _writer;
            if (writer == null) return;
            try { writer(tag ?? string.Empty, text ?? string.Empty); }
            catch { }
        }
    }
}
