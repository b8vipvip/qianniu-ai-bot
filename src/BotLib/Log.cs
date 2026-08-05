using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.IO;

namespace BotLib
{
    public class Log
    {
        private static LogWriter _writer;
        private static ConcurrentDictionary<string, int> _errorWithMaxCountDict = new ConcurrentDictionary<string, int>();

        private static LogWriter Writer
        {
            get
            {
                if (_writer == null)
                {
                    Initiate("", false, 0, true);
                }
                return _writer;
            }
        }

        public static void WriteEnvironmentString(string tip)
        {
            Writer.WriteEnvironmentString(tip);
            ScopedLogRouter.TryWrite("Environment", tip);
        }

        public static void Initiate(string FileName = "", bool saveLogByDay = false, int maxByte = 0, bool limitSameStringWriteCount = true)
        {
            if (_writer != null)
            {
                _writer.Close("启动程序");
            }
            if (string.IsNullOrEmpty(FileName)) FileName = "txt";
            if (!FileName.ToLower().EndsWith(".txt")) FileName += ".txt";
            if (!FileName.Contains("\\"))
                FileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            _writer = new LogWriter(FileName, saveLogByDay, maxByte);
            _writer.LimitSameStringWriteCount = limitSameStringWriteCount;
        }

        public static void Assert(string msg)
        {
            Writer.Assert(msg);
            ScopedLogRouter.TryWrite("Assert", msg);
        }

        public static string CurrentFileName
        {
            get { return Writer.FileName; }
        }

        public static void Flush()
        {
            Writer.Flush();
        }

        public static void Clear()
        {
            Writer.Clear();
        }

        public static void Error(string msg, object o, [System.Runtime.CompilerServices.CallerMemberName] string caller = "", [System.Runtime.CompilerServices.CallerFilePath] string path = "", [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        {
            msg = msg + Environment.NewLine + "data=" + JsonConvert.SerializeObject(o);
            var text = GetDesc(msg, caller, path, line);
            Writer.Error(text);
            ScopedLogRouter.TryWrite("ERROR", text);
        }

        public static void Error(string msg, [System.Runtime.CompilerServices.CallerMemberName] string caller = "", [System.Runtime.CompilerServices.CallerFilePath] string path = "", [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        {
            var text = GetDesc(msg, caller, path, line);
            Writer.Error(text);
            ScopedLogRouter.TryWrite("ERROR", text);
        }

        public static void ErrorWithMaxCount(string msg, int maxCount = 5, [System.Runtime.CompilerServices.CallerMemberName] string caller = "", [System.Runtime.CompilerServices.CallerFilePath] string path = "", [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        {
            string key = caller + line + path;
            if (IsLogCountLessThanMaxCount(key, maxCount + 1))
            {
                var text = GetDesc(msg, caller, path, line);
                Writer.Error(text);
                ScopedLogRouter.TryWrite("ERROR", text);
            }
        }

        private static bool IsLogCountLessThanMaxCount(string key, int maxCount)
        {
            int cnt = _errorWithMaxCountDict.GetOrAdd(key, 0);
            cnt++;
            if (cnt < maxCount) _errorWithMaxCountDict[key] = cnt;
            return cnt < maxCount;
        }

        private static string GetDesc(string msg, string caller, string path, int line)
        {
            var idx = (path ?? string.Empty).LastIndexOf('\\');
            if (idx >= 0) path = path.Substring(idx);
            if (!msg.Contains(caller) && msg.Contains(path ?? string.Empty))
                msg = string.Format("{0}\r\ncaller={1}, file={2},line={3}", msg.Trim(), caller, path, line);
            return msg;
        }

        private static string GetDesc(Exception e, string caller, string path, int line)
        {
            var idx = (path ?? string.Empty).LastIndexOf('\\');
            if (idx >= 0) path = path.Substring(idx);
            string text = e.ToString();
            if (!text.Contains(caller) && text.Contains(path ?? string.Empty))
            {
                text = string.Format("Message={0}\r\nBreakPoint={3}\r\ncaller={1}, file={2},line={3}\r\nStackTrace={4}",
                    text.Trim(), caller, path, line, e.StackTrace);
            }
            return text;
        }

        public static void Exception(Exception e, [System.Runtime.CompilerServices.CallerMemberName] string caller = "", [System.Runtime.CompilerServices.CallerFilePath] string path = "", [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        {
            var text = GetDesc(e, caller, path, line);
            Writer.Exception(text);
            ScopedLogRouter.TryWrite("Exception", text);
        }

        public static void Info(string text)
        {
            Writer.Info(text);
            ScopedLogRouter.TryWrite("Info", text);
        }

        public static void Debug(string text)
        {
            Writer.Debug(text);
            ScopedLogRouter.TryWrite("Debug", text);
        }

        public static void Show()
        {
            Writer.Show();
        }

        public static void TimeElapse(string title, DateTime t0)
        {
            var text = title + ",ms=" + (DateTime.Now - t0).TotalMilliseconds;
            Writer.Info(text);
            ScopedLogRouter.TryWrite("Info", text);
        }

        public static void WriteLine(string format, params object[] args)
        {
            try
            {
                string msg = string.Format(format, args);
                Writer.WriteLine(msg);
                ScopedLogRouter.TryWrite("Line", msg);
            }
            catch (Exception e)
            {
                Exception(e);
            }
        }

        public static void StackTrace()
        {
            Writer.StackTrace();
        }

        public static void Close(string reason = "")
        {
            Writer.Close(reason);
        }

        public static string CopyTo(string fn)
        {
            string rt = null;
            try
            {
                Writer.CopyTo(fn);
                if (!File.Exists(fn)) Error("无法复制文件到:" + fn);
            }
            catch (Exception ex)
            {
                Exception(ex);
                Info("fn=" + fn);
                rt = ex.Message;
            }
            return rt;
        }
    }
}