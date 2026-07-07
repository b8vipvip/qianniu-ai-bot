using IniParser.Model;
using IniParser;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;
using BotLib;
using System.Windows;

namespace Bot.Common
{
    public class QNInject
    {
        private const string processName = "AliWorkbench.exe";
        private const string webuiResDir = "newWebui";
        private const string webuiFile = "webui.zip";
        private const string signFile = "sign.json";
        private const string chatRecentHtmlFile = @"web_chat-packer/recent.html";
        private const string injectedScriptFile = @"web_chat-packer/qnbot-inject.js";
        private const string injectedScriptSrc = "qnbot-inject.js";
        private const string imSupportUrl = @"https://iseiya.taobao.com/imsupport";
        private const string injectVersionMarker = "20260707-zh-cn-v2";
        // 旧版本使用外部 OSS 脚本，可能导致千牛接待台资源/语言异常。新版改为把本地 src\Bin\inject.js 写入 webui.zip。
        private const string oldRemoteOverwriteUrl = "https://worklink.oss-cn-hangzhou.aliyuncs.com/5CFB5E11D17E63CDD8CB37B52FA6ACFD.js";

        public static async Task StartInject()
        {
            try
            {
                string installPath = FindInstallPath();
                if (string.IsNullOrEmpty(installPath))
                {
                    MessageBox.Show("没有检测到安装的千牛!!");
                }

                var resourcePath = FindResourcePath(installPath);
                if (string.IsNullOrEmpty(resourcePath))
                {
                    MessageBox.Show("获取千牛资源目录失败!!");
                    return;
                }

                if (IsInjected(resourcePath))
                {
                    return;
                }

                if (IsWorkbenchRunning())
                {
                    if (MessageBox.Show("请先退出千牛，在运行此程序!!", "提示", MessageBoxButton.YesNo)
                        == MessageBoxResult.No)
                    {
                        return;
                    }
                    else
                    {
                        foreach (var p in Process.GetProcessesByName(processName))
                        {
                            p.Kill();
                        }
                    }
                    await Task.Delay(2000);
                }

                if (InjectScript(resourcePath))
                {
                    MessageBox.Show("千牛插件注入成功，请重新启动千牛!!");
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
            string installPath = null;
            try
            {
                RegistryKey registryKey = Registry.ClassesRoot.OpenSubKey("aliim");
                registryKey = registryKey.OpenSubKey("Shell");
                registryKey = registryKey.OpenSubKey("Open");
                registryKey = registryKey.OpenSubKey("Command");
                installPath = registryKey.GetValue("").ToString();
                int idx = installPath.IndexOf("wwcmd.exe");
                installPath = installPath.Substring(1, idx + 8);
                installPath = Directory.GetParent(installPath).Parent.FullName;
            }
            catch (Exception e)
            {
                Log.Exception(e, "installPath");
            }
            return installPath;
        }

        private static string FindResourcePath(string installPath)
        {
            var aliWorkbenchConfigPath = Path.Combine(installPath, "AliWorkbench.ini");
            if (File.Exists(aliWorkbenchConfigPath))
            {
                var version = ReadAliConfigFile(aliWorkbenchConfigPath);
                return Path.Combine(installPath, version, "Resources");
            }
            return string.Empty;
        }

        private static bool IsWorkbenchRunning()
        {
            var processes = Process.GetProcessesByName(processName);
            return processes.Length > 0;
        }

        public static bool IsInjected(string resourcePath)
        {
            var webuiResPath = Path.Combine(resourcePath, webuiResDir, webuiFile);
            using (var zipFile = new ZipFile(webuiResPath))
            {
                var entry = zipFile.GetEntry(chatRecentHtmlFile);
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

            using (var zipFile = new ZipFile(webuiResPath))
            {
                var entry = zipFile.GetEntry(chatRecentHtmlFile);
                using (var inputStream = zipFile.GetInputStream(entry))
                using (var streamReader = new StreamReader(inputStream))
                {
                    var chatRecentHtmlContent = streamReader.ReadToEnd();
                    if (chatRecentHtmlContent.Contains(oldRemoteOverwriteUrl))
                    {
                        chatRecentHtmlContent = chatRecentHtmlContent.Replace(oldRemoteOverwriteUrl, injectedScriptSrc);
                    }
                    else if (chatRecentHtmlContent.Contains(imSupportUrl))
                    {
                        chatRecentHtmlContent = chatRecentHtmlContent.Replace(imSupportUrl, injectedScriptSrc);
                    }
                    else if (!chatRecentHtmlContent.Contains(injectedScriptSrc))
                    {
                        var tag = "<script src=\"" + injectedScriptSrc + "\"></script>";
                        if (chatRecentHtmlContent.IndexOf("</body>", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            chatRecentHtmlContent = chatRecentHtmlContent.Replace("</body>", tag + "</body>");
                        }
                        else
                        {
                            chatRecentHtmlContent += tag;
                        }
                    }

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