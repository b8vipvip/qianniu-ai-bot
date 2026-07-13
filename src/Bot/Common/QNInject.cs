using IniParser.Model;
using IniParser;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;
using BotLib;
using System.Windows;

namespace Bot.Common
{
    public class QNInject
    {
        private static readonly string[] workbenchProcessNames = { "AliWorkbench", "new_AliWorkbench", "AliRender", "wwcmd", "wangwang" };
        private const string webuiResDir = "newWebui";
        private const string webuiFile = "webui.zip";
        private const string signFile = "sign.json";
        private const string chatRecentHtmlFile = @"web_chat-packer/recent.html";
        private const string injectedScriptFile = @"web_chat-packer/qnbot-inject.js";
        private const string injectedScriptSrc = "qnbot-inject.js";
        private const string imSupportUrl = @"https://iseiya.taobao.com/imsupport";
        private const string injectVersionMarker = "20260712-zh-cn-v3";
        // 旧版本使用外部 OSS 脚本，可能导致千牛接待台资源/语言异常。新版改为把本地 src\Bin\inject.js 写入 webui.zip。
        private const string oldRemoteOverwriteUrl = "https://worklink.oss-cn-hangzhou.aliyuncs.com/5CFB5E11D17E63CDD8CB37B52FA6ACFD.js";

        public static async Task StartInject()
        {
            try
            {
                var installPaths = FindInstallPaths();
                if (installPaths.Count < 1)
                {
                    MessageBox.Show("没有检测到安装的千牛!!");
                    return;
                }

                var resourcePaths = FindResourcePaths(installPaths);
                if (resourcePaths.Count < 1)
                {
                    MessageBox.Show("获取千牛资源目录失败!!");
                    return;
                }

                var needInjectPaths = resourcePaths.Where(p => !IsInjected(p)).ToList();
                if (needInjectPaths.Count < 1)
                {
                    return;
                }

                if (IsWorkbenchRunning())
                {
                    if (MessageBox.Show("检测到千牛正在运行。需要先退出千牛后注入插件，是否现在关闭千牛并继续？", "提示", MessageBoxButton.YesNo)
                        == MessageBoxResult.No)
                    {
                        return;
                    }
                    else
                    {
                        KillWorkbenchProcesses();
                    }
                    await Task.Delay(3000);
                }

                int success = 0;
                foreach (var resourcePath in needInjectPaths)
                {
                    try
                    {
                        if (InjectScript(resourcePath))
                        {
                            success++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Exception(ex, "InjectScript:" + resourcePath);
                    }
                }

                if (success == needInjectPaths.Count)
                {
                    MessageBox.Show("千牛插件注入成功，请重新启动千牛!!");
                }
                else if (success > 0)
                {
                    MessageBox.Show("千牛插件部分注入成功，请重新启动千牛后检查连接状态。");
                }
                else
                {
                    MessageBox.Show("千牛插件注入失败!!");
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }

        private static string FindInstallPath()
        {
            return FindInstallPaths().FirstOrDefault();
        }

        private static List<string> FindInstallPaths()
        {
            var paths = new List<string>();
            AddInstallPath(paths, FindInstallPathFromRegistry());
            foreach (var path in FindInstallPathsFromRunningProcesses())
            {
                AddInstallPath(paths, path);
            }
            AddInstallPath(paths, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AliWorkbench"));
            AddInstallPath(paths, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "AliWorkbench"));
            return paths
                .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void AddInstallPath(List<string> paths, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                path = Path.GetFullPath(path.Trim().Trim('"'));
                if (Directory.Exists(path) && !paths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                {
                    paths.Add(path);
                }
            }
            catch
            {
            }
        }

        private static string FindInstallPathFromRegistry()
        {
            try
            {
                using (var registryKey = Registry.ClassesRoot.OpenSubKey(@"aliim\Shell\Open\Command"))
                {
                    if (registryKey == null) return string.Empty;
                    var command = (registryKey.GetValue("") ?? string.Empty).ToString();
                    var exePath = ExtractExePath(command);
                    return InstallPathFromExe(exePath);
                }
            }
            catch (Exception e)
            {
                Log.Exception(e, "installPath");
                return string.Empty;
            }
        }

        private static IEnumerable<string> FindInstallPathsFromRunningProcesses()
        {
            foreach (var name in workbenchProcessNames)
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    string path = string.Empty;
                    try
                    {
                        path = process.MainModule == null ? string.Empty : process.MainModule.FileName;
                    }
                    catch
                    {
                    }
                    var installPath = InstallPathFromExe(path);
                    if (!string.IsNullOrWhiteSpace(installPath))
                    {
                        yield return installPath;
                    }
                }
            }
        }

        private static string ExtractExePath(string command)
        {
            command = (command ?? string.Empty).Trim();
            if (command.Length < 1) return string.Empty;
            if (command.StartsWith("\""))
            {
                var end = command.IndexOf('"', 1);
                return end > 1 ? command.Substring(1, end - 1) : string.Empty;
            }
            var idx = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return idx >= 0 ? command.Substring(0, idx + 4).Trim() : command;
        }

        private static string InstallPathFromExe(string exePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return string.Empty;
                var file = new FileInfo(exePath);
                var name = file.Name;
                if (name.Equals("AliRender.exe", StringComparison.OrdinalIgnoreCase))
                {
                    return file.Directory == null || file.Directory.Parent == null ? string.Empty : file.Directory.Parent.FullName;
                }
                if (name.Equals("wwcmd.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("wangwang.exe", StringComparison.OrdinalIgnoreCase))
                {
                    return file.Directory == null || file.Directory.Parent == null ? string.Empty : file.Directory.Parent.FullName;
                }
                return file.Directory == null ? string.Empty : file.Directory.FullName;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static List<string> FindResourcePaths(IEnumerable<string> installPaths)
        {
            var paths = new List<string>();
            foreach (var installPath in installPaths)
            {
                AddResourcePathFromIni(paths, installPath);
                AddAllVersionResourcePaths(paths, installPath);
            }
            return paths
                .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(Path.Combine(p, webuiResDir, webuiFile)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(p => File.GetLastWriteTime(Path.Combine(p, webuiResDir, webuiFile)))
                .ToList();
        }

        private static string FindResourcePath(string installPath)
        {
            return FindResourcePaths(new[] { installPath }).FirstOrDefault() ?? string.Empty;
        }

        private static void AddResourcePathFromIni(List<string> paths, string installPath)
        {
            try
            {
                var aliWorkbenchConfigPath = Path.Combine(installPath, "AliWorkbench.ini");
                if (!File.Exists(aliWorkbenchConfigPath)) return;
                var version = ReadAliConfigFile(aliWorkbenchConfigPath);
                AddResourcePath(paths, Path.Combine(installPath, version, "Resources"));
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "AddResourcePathFromIni");
            }
        }

        private static void AddAllVersionResourcePaths(List<string> paths, string installPath)
        {
            try
            {
                if (!Directory.Exists(installPath)) return;
                foreach (var dir in Directory.GetDirectories(installPath))
                {
                    AddResourcePath(paths, Path.Combine(dir, "Resources"));
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "AddAllVersionResourcePaths");
            }
        }

        private static void AddResourcePath(List<string> paths, string resourcePath)
        {
            try
            {
                if (File.Exists(Path.Combine(resourcePath, webuiResDir, webuiFile))
                    && !paths.Any(p => string.Equals(p, resourcePath, StringComparison.OrdinalIgnoreCase)))
                {
                    paths.Add(resourcePath);
                }
            }
            catch
            {
            }
        }

        private static bool IsWorkbenchRunning()
        {
            return workbenchProcessNames.Any(name => Process.GetProcessesByName(name).Length > 0);
        }

        private static void KillWorkbenchProcesses()
        {
            foreach (var name in workbenchProcessNames)
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        p.Kill();
                    }
                    catch (Exception ex)
                    {
                        Log.Exception(ex);
                    }
                }
            }
        }

        public static bool IsInjected(string resourcePath)
        {
            try
            {
                var webuiResPath = Path.Combine(resourcePath, webuiResDir, webuiFile);
                if (!File.Exists(webuiResPath)) return false;
                using (var zipFile = new ZipFile(webuiResPath))
                {
                    var entry = zipFile.GetEntry(chatRecentHtmlFile);
                    if (entry == null) return false;
                    using (var inputStream = zipFile.GetInputStream(entry))
                    using (var streamReader = new StreamReader(inputStream))
                    {
                        var chatRecentHtmlContent = streamReader.ReadToEnd();
                        if (!chatRecentHtmlContent.Contains(injectedScriptSrc) || chatRecentHtmlContent.Contains(oldRemoteOverwriteUrl))
                        {
                            return false;
                        }
                    }

                    var injectEntry = zipFile.GetEntry(injectedScriptFile);
                    if (injectEntry == null)
                    {
                        return false;
                    }

                    using (var inputStream = zipFile.GetInputStream(injectEntry))
                    using (var streamReader = new StreamReader(inputStream))
                    {
                        var injectContent = streamReader.ReadToEnd();
                        return injectContent.Contains(injectVersionMarker);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "IsInjected:" + resourcePath);
                return false;
            }
        }

        private static string ReadLocalInjectJs()
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "inject.js");
            if (!File.Exists(path))
            {
                Log.Error("没有找到本地 inject.js: " + path);
                return string.Empty;
            }
            return File.ReadAllText(path, Encoding.UTF8);
        }

        private static bool InjectScript(string resourcePath)
        {
            var webuiResPath = Path.Combine(resourcePath, webuiResDir, webuiFile);
            var signPath = Path.Combine(resourcePath, webuiResDir, signFile);
            var injectJsContent = ReadLocalInjectJs();
            if (string.IsNullOrWhiteSpace(injectJsContent)) return false;

            BackupWebuiZip(webuiResPath);
            using (var zipFile = new ZipFile(webuiResPath))
            {
                var entry = zipFile.GetEntry(chatRecentHtmlFile);
                if (entry == null)
                {
                    Log.Error("webui.zip中没有找到 " + chatRecentHtmlFile + ": " + webuiResPath);
                    return false;
                }
                using (var inputStream = zipFile.GetInputStream(entry))
                using (var streamReader = new StreamReader(inputStream))
                {
                    var chatRecentHtmlContent = streamReader.ReadToEnd();
                    chatRecentHtmlContent = PlaceInjectedScriptFirst(chatRecentHtmlContent);

                    zipFile.BeginUpdate();
                    zipFile.Add(new ZipStaticDataSource(chatRecentHtmlContent), chatRecentHtmlFile);
                    zipFile.Add(new ZipStaticDataSource(injectJsContent), injectedScriptFile);
                    zipFile.CommitUpdate();

                    if (File.Exists(signPath))
                    {
                        var signFi = new FileInfo(signPath);
                        if (signFi.Length > 0)
                        {
                            signFi.Delete();
                            File.Create(signPath).Dispose();
                        }
                    }
                    return true;
                }
            }
        }

        private static string PlaceInjectedScriptFirst(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return html;

            // Locale must be selected before Qianniu's application bundles start.
            // Remove old/late injection tags and place our bootstrap first in <head>.
            var scriptPatterns = new[]
            {
                @"<script\b[^>]*\bsrc\s*=\s*[\""'][^\""']*qnbot-inject\.js[^\""']*[\""'][^>]*>\s*</script\s*>",
                @"<script\b[^>]*\bsrc\s*=\s*[\""'][^\""']*iseiya\.taobao\.com/imsupport[^\""']*[\""'][^>]*>\s*</script\s*>",
                @"<script\b[^>]*\bsrc\s*=\s*[\""'][^\""']*5CFB5E11D17E63CDD8CB37B52FA6ACFD\.js[^\""']*[\""'][^>]*>\s*</script\s*>"
            };
            foreach (var pattern in scriptPatterns)
            {
                html = Regex.Replace(html, pattern, string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            }

            var tag = "<script src=\"" + injectedScriptSrc + "\"></script>";
            var head = Regex.Match(html, @"<head\b[^>]*>", RegexOptions.IgnoreCase);
            if (head.Success)
            {
                return html.Insert(head.Index + head.Length, tag);
            }
            return tag + html;
        }

        private static void BackupWebuiZip(string webuiResPath)
        {
            try
            {
                var backupPath = webuiResPath + ".bak-qnbot-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                if (!File.Exists(backupPath))
                {
                    File.Copy(webuiResPath, backupPath);
                    Log.Info("已备份千牛webui.zip: " + backupPath);
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "BackupWebuiZip");
            }
        }

        private static string ReadAliConfigFile(string path)
        {
            var parser = new FileIniDataParser();
            var data = parser.ReadFile(path);
            string version = data["Common"]["Version"];
            return version;
        }
    }

    public class WorkbenchResult
    {
        public WorkbenchStatus Status { get; set; }
        public string Message { get; set; }
    }

    public enum WorkbenchStatus
    {
        NotInstalled,
        ResourceNotFound,
        Running,
        Success,
        Failed
    }

    public class ZipStaticDataSource : IStaticDataSource
    {
        private string _content;
        public ZipStaticDataSource(string content)
        {
            _content = content;
        }

        public Stream GetSource()
        {
            byte[] bytes = Encoding.UTF8.GetBytes(_content);
            return new MemoryStream(bytes);
        }
    }
}
