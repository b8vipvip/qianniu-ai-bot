using System;

namespace BotLib.Db.Sqlite
{
    /// <summary>
    /// Optional bridge used by the Windows client to route selected two-key parameters into
    /// an explicit shop scope. BotLib stays independent from the Bot project and knows nothing
    /// about ShopContext or filesystem layout.
    /// </summary>
    public static class ScopedParamRouter
    {
        public delegate bool TryReadHandler(string masterKey, string subKey, out string value);
        public delegate bool TryWriteHandler(string masterKey, string subKey, string value);

        private static readonly object Sync = new object();
        private static TryReadHandler _reader;
        private static TryWriteHandler _writer;

        public static void Configure(TryReadHandler reader, TryWriteHandler writer)
        {
            lock (Sync)
            {
                _reader = reader;
                _writer = writer;
            }
        }

        internal static bool TryRead(string masterKey, string subKey, out string value)
        {
            TryReadHandler reader;
            lock (Sync) reader = _reader;
            value = null;
            return reader != null && reader(masterKey, subKey, out value);
        }

        internal static bool TryWrite(string masterKey, string subKey, string value)
        {
            TryWriteHandler writer;
            lock (Sync) writer = _writer;
            return writer != null && writer(masterKey, subKey, value);
        }
    }
}
