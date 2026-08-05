using System;
using System.Diagnostics;
using System.IO;
using System.Web.Hosting;

namespace BotLib.Extensions
{
    public static class PathEx
    {
        private const string ClientDataRootFolderName = "QianniuAiBot";
        private static string _parentPathOfExePath;
        private static int tmpFilenameOrder = 0;
        private static string _globalDataDir;
        private static string _legacyDataDir;
        private static string _userDataRoot;
        private static string _startUpPathOfExe;

        public static string AppendStringToFileName(string ori, string tail)
        {
            return string.Concat(Path.GetDirectoryName(ori), "\\", Path.GetFileNameWithoutExtension(ori), tail, Path.GetExtension(ori));
        }

        public static string AppendBackupTime(string ori)
        {
            return AppendStringToFileName(ori, string.Format("#备份时间-{0}#", DateTime.Now.xToString2()));
        }

        public static string ParentOfExePath
        {
            get
            {
                if (_parentPathOfExePath == null)
                {
                    DirectoryInfo directoryInfo = new DirectoryInfo(StartUpPathOfExe);
                    _parentPathOfExePath = directoryInfo.Parent.FullName + "\\";
                }
                return _parentPathOfExePath;
            }
        }

        /// <summary>
        /// 客户端永久用户数据根目录。程序升级或更换解压目录时保持不变。
        /// </summary>
        public static string UserDataRoot
        {
            get
            {
                if (Params.IsServerLib)
                {
                    return StartUpPathOfExe;
                }

                if (string.IsNullOrEmpty(_userDataRoot))
                {
                    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    _userDataRoot = EnsureTrailingSeparator(Path.Combine(localAppData, ClientDataRootFolderName));
                    Directory.CreateDirectory(_userDataRoot);
                }
                return _userDataRoot;
            }
        }

        /// <summary>
        /// 旧版客户端数据目录（Bot.exe 上一级目录下的 data）。仅用于升级迁移和安装包模板识别。
        /// </summary>
        public static string LegacyDataDir
        {
            get
            {
                if (Params.IsServerLib)
                {
                    return GlobalDataDir;
                }

                if (string.IsNullOrEmpty(_legacyDataDir))
                {
                    _legacyDataDir = EnsureTrailingSeparator(Path.Combine(ParentOfExePath, "data"));
                }
                return _legacyDataDir;
            }
        }

        public static void OpenFolder(string dir)
        {
            Process.Start("explorer.exe", dir);
        }

        public static void OpenParentFolderAndSelectFileOrDir(string dest)
        {
            Process.Start("explorer.exe", "/select," + dest);
        }

        public static string GetAncestorPathOfExe(int upLevels)
        {
            var directoryInfo = new DirectoryInfo(StartUpPathOfExe);
            while (upLevels > 0)
            {
                directoryInfo = directoryInfo.Parent;
                upLevels--;
            }
            return directoryInfo.FullName + "\\";
        }

        public static string GetParentSiblingDir(string sub)
        {
            string text = ParentOfExePath + sub + "\\";
            Directory.CreateDirectory(text);
            return text;
        }

        public static string GetAppSubDir(string sub)
        {
            string text = StartUpPathOfExe + sub + "\\";
            Directory.CreateDirectory(text);
            return text;
        }

        public static string GetSubDirOfData(string sub)
        {
            string text = string.IsNullOrEmpty(sub) ? DataDir : Path.Combine(DataDir, sub);
            text = EnsureTrailingSeparator(text);
            Directory.CreateDirectory(text);
            return text;
        }

        public static string GetTmpFileName(string ext = "")
        {
            var order = System.Threading.Interlocked.Increment(ref tmpFilenameOrder);
            return TmpPath + order + ext;
        }

        public static string TmpPath
        {
            get { return GetSubDirOfData("tmp"); }
        }

        public static string GetRightSectionOfPath(string path, bool removeHeadXiegan, int n = 1)
        {
            if (n < 0)
            {
                n = 1;
            }
            int separatorIdx = path.Length - 1;
            while (n > 0 && separatorIdx > 0)
            {
                separatorIdx = path.LastIndexOf('\\', separatorIdx - 1);
                n--;
            }
            string separator;
            if (separatorIdx >= 0)
            {
                separator = path.Substring(separatorIdx).Trim();
            }
            else
            {
                separator = path.Trim();
            }
            if (removeHeadXiegan && separator.StartsWith("\\"))
            {
                separator = separator.Substring(1);
            }
            return separator;
        }

        internal static string ConvertToRelativePath(string fullpath)
        {
            string path = fullpath;
            int num = fullpath.xLengthOfLeftEndSameString(StartUpPathOfExe);
            if (num > 0)
            {
                path = fullpath.Substring(num);
            }
            return path;
        }

        public static string GetFilenameUnderAppDataDir(string name)
        {
            return Path.Combine(DataDir, name);
        }

        public static string GetFileNameUnderExeDir(string name)
        {
            return Path.Combine(StartUpPathOfExe, name);
        }

        public static string TempTxtFileName
        {
            get
            {
                return StartUpPathOfExe + "tmp.txt";
            }
        }

        /// <summary>
        /// The original process-wide data folder. Migration and registry code must use this
        /// property so an ambient shop scope cannot accidentally change its source directory.
        /// </summary>
        public static string GlobalDataDir
        {
            get
            {
                if (string.IsNullOrEmpty(_globalDataDir))
                {
                    if (Params.IsServerLib)
                    {
                        _globalDataDir = GetAppSubDir("data");
                    }
                    else
                    {
                        _globalDataDir = EnsureTrailingSeparator(Path.Combine(UserDataRoot, "data"));
                        Directory.CreateDirectory(_globalDataDir);
                    }
                }
                return _globalDataDir;
            }
        }

        /// <summary>
        /// Compatibility data folder. In a ShopContext it resolves to that shop's state/data
        /// directory; outside a shop operation it remains the legacy process-wide folder.
        /// </summary>
        public static string DataDir
        {
            get
            {
                string scoped;
                if (!Params.IsServerLib && ScopedDataPathRouter.TryResolve(out scoped))
                {
                    scoped = EnsureTrailingSeparator(Path.GetFullPath(scoped));
                    Directory.CreateDirectory(scoped);
                    return scoped;
                }
                return GlobalDataDir;
            }
        }

        public static string StartUpPathOfExe
        {
            get
            {
                if (string.IsNullOrEmpty(_startUpPathOfExe))
                {
                    if (Params.IsServerLib)
                    {
                        _startUpPathOfExe = HostingEnvironment.MapPath("~");
                    }
                    else
                    {
                        string fileName = Process.GetCurrentProcess().MainModule.FileName;
                        _startUpPathOfExe = Path.GetDirectoryName(fileName);
                    }
                    if (!_startUpPathOfExe.EndsWith("\\"))
                    {
                        _startUpPathOfExe += "\\";
                    }
                }
                return _startUpPathOfExe;
            }
        }

        public static string DeskTopPath
        {
            get
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) + "\\";
            }
        }

        private static string EnsureTrailingSeparator(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }
            return path.EndsWith("\\") ? path : path + "\\";
        }
    }
}