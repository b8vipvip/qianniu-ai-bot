using System;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Keeps ChromeNs sources independent from per-file BotLib using directives.
    /// All calls are forwarded to the existing application logger.
    /// </summary>
    internal static class Log
    {
        public static void Assert(string message) { BotLib.Log.Assert(message); }
        public static void Clear() { BotLib.Log.Clear(); }
        public static void Error(string message) { BotLib.Log.Error(message); }
        public static void Error(string message, object data) { BotLib.Log.Error(message, data); }
        public static void ErrorWithMaxCount(string message, int maxCount = 5) { BotLib.Log.ErrorWithMaxCount(message, maxCount); }
        public static void Exception(Exception exception) { BotLib.Log.Exception(exception); }
        public static void Info(string message) { BotLib.Log.Info(message); }
        public static void Debug(string message) { BotLib.Log.Debug(message); }
        public static void Show() { BotLib.Log.Show(); }
        public static void TimeElapse(string title, DateTime start) { BotLib.Log.TimeElapse(title, start); }
        public static void WriteLine(string format, params object[] args) { BotLib.Log.WriteLine(format, args); }
        public static void StackTrace() { BotLib.Log.StackTrace(); }
        public static void Close(string reason = "") { BotLib.Log.Close(reason); }
        public static string CopyTo(string fileName) { return BotLib.Log.CopyTo(fileName); }
    }
}
