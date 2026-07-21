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
        private static string _tmpPath;
        private static string _dataDir;
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
                    return DataDir;
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
            string text = string.IsNullOrEmpty(sub) ? DataDir : (DataDir + sub + "\\");
            Directory.CreateDirectory(text);
            return text;
        }

        public static string GetTmpFileName(string ext = "")
        {
            tmpFilenameOrder++;
            return TmpPath + tmpFilenameOrder + ext;
        }

        public static string TmpPath
        {
            get
            {
                if (_tmpPath == null)
                {
                    _tmpPath = GetSubDirOfData("tmp");
                }
                return _tmpPath;
            }
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

        public static string DataDir
        {
            get
            {
                if (string.IsNullOrEmpty(_dataDir))
                {
                    if (Params.IsServerLib)
                    {
                        _dataDir = GetAppSubDir("data");
                    }
                    else
                    {
                        _dataDir = EnsureTrailingSeparator(Path.Combine(UserDataRoot, "data"));
                        Directory.CreateDirectory(_dataDir);
                    }
                }
                return _dataDir;
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
