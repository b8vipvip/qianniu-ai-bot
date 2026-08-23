using BotLib;
using BotLib.Extensions;
using Bot.ControllerNs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Bot.Common.Db;
using Bot.Common;
using BotLib.Wpf.Extensions;
using Bot.ChromeNs;
using ICSharpCode.SharpZipLib.Zip;

namespace Bot
{
    public class BootStrap
    {
        public static async Task Init()
        {
            ClearTmpPathFiles();
            var languageResult = await LanguageStartupSafetyGate.CheckAndRepairLanguageSafely();
            BotConnectionDiagnostics.RecordLanguageStatus(languageResult.IsOk, languageResult.StatusText, languageResult.Detail);
            DeskScanner.LoopScan();
            MyWebSocketServer.WSocketSvrInst.Start();
            await QNInject.StartInject();
            QnStartupConnectionSelfHeal.Start();
            WeComAppBridgeClient.Start();

            //var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"inject.js"));
            //IseiyaHttpProxy.StartProxy(script);
        }

        private static void ClearTmpPathFiles()
        {
            try
            {
                if (Directory.GetFiles(PathEx.TmpPath).Length > 0)
                {
                    DirectoryEx.Delete(PathEx.TmpPath, true);
                    Directory.CreateDirectory(PathEx.TmpPath);
                }
            }
            catch (Exception e)
            {
                Log.Exception(e);
            }
        }
    }

    /// <summary>
    /// Startup must never destroy a live Qianniu WebView just to re-apply files that are already
    /// current. The legacy repair service is still used when Qianniu is closed, but a running
    /// workbench is inspected read-only and either accepted or marked for deferred repair.
    /// </summary>
    internal static class LanguageStartupSafetyGate
    {
        private const string InjectVersionMarker = "20260714-zh-cn-v9";
        private const string LanguageVersionMarker = "20260713-hans-all-pages-v3";
        private const string WebuiRelativePath = @"Resources\newWebui\webui.zip";
        private static readonly string[] WorkbenchProcessNames =
        {
            "AliWorkbench", "new_AliWorkbench", "AliRender"
        };

        public static async Task<LanguageRepairResult> CheckAndRepairLanguageSafely()
        {
            if (!IsQianniuRunning())
            {
                return await LanguageRepairService.CheckAndRepairLanguage();
            }

            string activeZip;
            string discovery;
            if (TryGetActiveResourceZip(out activeZip, out discovery)
                && ActiveResourceHasCurrentMarkers(activeZip))
            {
                Log.Info("语言启动安全检查：运行中的千牛资源已是当前版本，跳过自动修复，不关闭WebView。 path=" + activeZip);
                return new LanguageRepairResult
                {
                    IsOk = true,
                    Repaired = false,
                    CurrentLanguage = "zh-CN",
                    StatusText = "语言：简体中文 ✓",
                    Detail = "运行中的千牛资源包含当前简体中文与注入标记；启动未修改WebView。"
                };
            }

            var detail = "检测到千牛正在运行，但当前活动 webui 资源尚未确认包含最新语言/注入标记；"
                + "为保护登录态，本次启动不关闭WebView、不清缓存、不覆盖资源，待千牛关闭后再安全修复。"
                + (string.IsNullOrWhiteSpace(discovery) ? string.Empty : " " + discovery);
            Log.ErrorWithMaxCount("语言启动安全检查已延后破坏性修复：" + detail, 5);
            return new LanguageRepairResult
            {
                IsOk = false,
                Repaired = false,
                CurrentLanguage = "zh-CN",
                StatusText = "语言：待安全修复",
                Detail = detail
            };
        }

        internal static bool IsQianniuRunning()
        {
            foreach (var name in WorkbenchProcessNames)
            {
                try
                {
                    if (Process.GetProcessesByName(name).Any()) return true;
                }
                catch
                {
                }
            }
            return false;
        }

        internal static bool TryGetActiveResourceZip(out string zipPath, out string detail)
        {
            zipPath = string.Empty;
            detail = string.Empty;

            foreach (var name in WorkbenchProcessNames)
            {
                Process[] processes;
                try
                {
                    processes = Process.GetProcessesByName(name);
                }
                catch
                {
                    continue;
                }

                foreach (var process in processes)
                {
                    try
                    {
                        var exe = process.MainModule == null ? string.Empty : process.MainModule.FileName;
                        var directory = string.IsNullOrWhiteSpace(exe) ? null : new FileInfo(exe).Directory;
                        for (var depth = 0; directory != null && depth < 5; depth++, directory = directory.Parent)
                        {
                            var direct = Path.Combine(directory.FullName, WebuiRelativePath);
                            if (File.Exists(direct))
                            {
                                zipPath = direct;
                                detail = "activeExe=" + exe;
                                return true;
                            }

                            string fromIni;
                            if (TryResolveVersionZipFromInstallRoot(directory.FullName, out fromIni))
                            {
                                zipPath = fromIni;
                                detail = "activeExe=" + exe;
                                return true;
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }

            foreach (var root in KnownInstallRoots())
            {
                string fromIni;
                if (TryResolveVersionZipFromInstallRoot(root, out fromIni))
                {
                    zipPath = fromIni;
                    detail = "installRoot=" + root;
                    return true;
                }
            }

            detail = "未定位到活动版本 webui.zip";
            return false;
        }

        private static IEnumerable<string> KnownInstallRoots()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AliWorkbench"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "AliWorkbench")
            };
            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate) && seen.Add(candidate))
                    yield return candidate;
            }
        }

        private static bool TryResolveVersionZipFromInstallRoot(string root, out string zipPath)
        {
            zipPath = string.Empty;
            try
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return false;
                var ini = Path.Combine(root, "AliWorkbench.ini");
                if (!File.Exists(ini)) return false;
                var versionLine = File.ReadLines(ini)
                    .Select(line => (line ?? string.Empty).Trim())
                    .FirstOrDefault(line => line.StartsWith("Version=", StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(versionLine)) return false;
                var version = versionLine.Substring(versionLine.IndexOf('=') + 1).Trim().Trim('"');
                if (version.Length == 0) return false;
                var candidate = Path.Combine(root, version, WebuiRelativePath);
                if (!File.Exists(candidate)) return false;
                zipPath = candidate;
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool ActiveResourceHasCurrentMarkers(string zipPath)
        {
            if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath)) return false;
            try
            {
                using (var stream = File.Open(zipPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var zip = new ZipFile(stream))
                {
                    return ZipContainsMarker(zip, "web_chat-packer/qnbot-inject.js", InjectVersionMarker)
                        && ZipContainsMarker(zip, "web_chat-packer/qnbot-language.js", LanguageVersionMarker);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("语言启动安全检查读取活动webui失败：" + ex.Message, 5);
                return false;
            }
        }

        private static bool ZipContainsMarker(ZipFile zip, string entryName, string marker)
        {
            var entry = zip.GetEntry(entryName);
            if (entry == null || !entry.IsFile) return false;
            using (var input = zip.GetInputStream(entry))
            using (var reader = new StreamReader(input, Encoding.UTF8, true))
            {
                var text = reader.ReadToEnd();
                return text.IndexOf(marker, StringComparison.Ordinal) >= 0;
            }
        }
    }
}
