using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.IO;

namespace BotLib
{
    public class Log
    {
        private static LogWriter _writer;
        private static ConcurrentDictionary<string, int> _errorWithMaxCountDict = new ConcurrentDictionary<string, int>();
        private const string ImsdkVerboseTraceEnvironmentKey = "QNBOT_IMSDK_VERBOSE_TRACE";
        private static readonly object DiagnosticNoiseSync = new object();
        private static readonly TimeSpan InjectionStatusRepeatWindow = TimeSpan.FromSeconds(30);
        private static string _lastInjectionStatusSummary = string.Empty;
        private static DateTime _lastInjectionStatusLoggedUtc = DateTime.MinValue;
        private static int _suppressedInjectionStatusCount;

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
            text = NormalizeProductionDiagnostic(text);
            if (string.IsNullOrWhiteSpace(text)) return;
            Writer.Info(text);
            ScopedLogRouter.TryWrite("Info", text);
        }

        /// <summary>
        /// Production diagnostics must not persist buyer/seller identities or high-volume protocol
        /// payloads by default. IMSDK raw discovery remains an explicit opt-in. Periodic Qianniu
        /// injection status is converted to a state-only summary and identical states are coalesced.
        /// </summary>
        internal static string NormalizeProductionDiagnostic(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            if (text.IndexOf("千牛注入状态:", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return NormalizeInjectionStatus(text);
            }

            var verbose = IsImsdkVerboseTraceEnabled();
            if (text.IndexOf("收到千牛WebSocket事件: type=imsdkInvokeTrace", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("收到千牛WebSocket事件: type=imsdkApiScan", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return verbose ? text : null;
            }

            var isApiScan = text.IndexOf("IMSDK API扫描结果", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("IMSDK API鎵", StringComparison.OrdinalIgnoreCase) >= 0;
            var isInvokeTrace = text.IndexOf("IMSDK调用追踪", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("IMSDK调用跟踪", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("IMSDK璋", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isApiScan && !isInvokeTrace) return text;

            var json = ExtractJsonObject(text);
            if (verbose)
            {
                if (isApiScan) return "IMSDK API扫描结果: " + json;
                return "IMSDK调用追踪: " + json;
            }

            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var payload = JObject.Parse(json);
                if (isApiScan)
                {
                    var version = (payload["version"] ?? string.Empty).ToString();
                    var scanKind = (payload["scanKind"] ?? string.Empty).ToString();
                    var objectCount = payload["objects"] is JArray objects ? objects.Count : 0;
                    var candidateCount = payload["result"]?["candidates"] is JArray candidates ? candidates.Count : 0;
                    return "IMSDK API扫描摘要: version=" + SafeToken(version)
                        + ", scanKind=" + SafeToken(scanKind)
                        + ", objects=" + objectCount
                        + ", candidates=" + candidateCount;
                }

                var method = (payload["method"] ?? string.Empty).ToString();
                var phase = (payload["phase"] ?? string.Empty).ToString();
                var elapsed = payload["elapsedMs"] == null ? 0L : payload["elapsedMs"].Value<long>();
                var error = (payload["error"] ?? string.Empty).ToString();
                if (string.IsNullOrWhiteSpace(error) && elapsed < 2000) return null;

                return "IMSDK调用追踪摘要: method=" + SafeToken(method)
                    + ", phase=" + SafeToken(phase)
                    + ", elapsedMs=" + elapsed
                    + ", success=" + string.IsNullOrWhiteSpace(error);
            }
            catch
            {
                // Malformed protocol discovery data is intentionally not written raw in production.
                return null;
            }
        }

        private static string NormalizeInjectionStatus(string text)
        {
            var json = ExtractJsonObject(text);
            string summary;
            if (string.IsNullOrWhiteSpace(json))
            {
                summary = "千牛注入状态摘要: parse=false, payloadLength=0";
            }
            else
            {
                try
                {
                    var payload = JObject.Parse(json);
                    var sellerPresent = !string.IsNullOrWhiteSpace((payload["loginNick"] ?? string.Empty).ToString());
                    var buyerPresent = !string.IsNullOrWhiteSpace((payload["conversationNick"] ?? string.Empty).ToString());
                    summary = "千牛注入状态摘要: hasLoginID=" + ReadBooleanStatus(payload, "hasLoginID")
                        + ", hasImsdk=" + ReadBooleanStatus(payload, "hasImsdk")
                        + ", hasQN=" + ReadBooleanStatus(payload, "hasQN")
                        + ", hasVs=" + ReadBooleanStatus(payload, "hasVs")
                        + ", sellerPresent=" + sellerPresent
                        + ", buyerPresent=" + buyerPresent
                        + ", payloadLength=" + json.Length;
                }
                catch
                {
                    // Never fall back to the raw status payload when parsing fails: it can contain
                    // seller/buyer nicknames. A parse marker is enough for production diagnostics.
                    summary = "千牛注入状态摘要: parse=false, payloadLength=" + json.Length;
                }
            }

            lock (DiagnosticNoiseSync)
            {
                var now = DateTime.UtcNow;
                if (string.Equals(summary, _lastInjectionStatusSummary, StringComparison.Ordinal)
                    && now - _lastInjectionStatusLoggedUtc < InjectionStatusRepeatWindow)
                {
                    _suppressedInjectionStatusCount++;
                    return null;
                }

                var suppressed = _suppressedInjectionStatusCount;
                _suppressedInjectionStatusCount = 0;
                _lastInjectionStatusSummary = summary;
                _lastInjectionStatusLoggedUtc = now;
                if (suppressed > 0) summary += ", repeatsSuppressed=" + suppressed;
                return summary;
            }
        }

        private static string ReadBooleanStatus(JObject payload, string name)
        {
            var token = payload == null ? null : payload[name];
            if (token == null || token.Type == JTokenType.Null) return "unknown";
            bool value;
            return bool.TryParse(token.ToString(), out value)
                ? (value ? "true" : "false")
                : "unknown";
        }

        private static bool IsImsdkVerboseTraceEnabled()
        {
            var value = (Environment.GetEnvironmentVariable(ImsdkVerboseTraceEnvironmentKey) ?? string.Empty).Trim();
            return value == "1"
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractJsonObject(string text)
        {
            var index = (text ?? string.Empty).IndexOf('{');
            return index >= 0 ? text.Substring(index).Trim() : string.Empty;
        }

        private static string SafeToken(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length > 160) value = value.Substring(0, 160);
            return value.Replace("\r", " ").Replace("\n", " ");
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
