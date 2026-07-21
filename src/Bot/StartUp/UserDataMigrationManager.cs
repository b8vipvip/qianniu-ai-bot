using BotLib.Extensions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Bot
{
    /// <summary>
    /// 在任何 SQLite 数据库被初始化前，负责建立永久用户数据目录、迁移旧数据及执行待处理的数据维护任务。
    /// </summary>
    public static class UserDataMigrationManager
    {
        private const string MigrationMarkerFileName = "data-migration-v2.done";
        private const string PendingImportFileName = "pending-data-import.txt";
        private const string PendingBackupFileName = "pending-data-backup.txt";

        public static string BackupsDirectory
        {
            get
            {
                var path = Path.Combine(PathEx.UserDataRoot, "backups");
                Directory.CreateDirectory(path);
                return path;
            }
        }

        private static string MigrationMarkerPath
        {
            get { return Path.Combine(PathEx.UserDataRoot, MigrationMarkerFileName); }
        }

        private static string PendingImportPath
        {
            get { return Path.Combine(PathEx.UserDataRoot, PendingImportFileName); }
        }

        private static string PendingBackupPath
        {
            get { return Path.Combine(PathEx.UserDataRoot, PendingBackupFileName); }
        }

        public static bool PrepareBeforeAppStartup()
        {
            try
            {
                Directory.CreateDirectory(PathEx.UserDataRoot);

                if (!ApplyPendingBackup())
                {
                    return false;
                }

                if (!ApplyPendingImport())
                {
                    return false;
                }

                if (File.Exists(MigrationMarkerPath))
                {
                    Directory.CreateDirectory(PathEx.DataDir);
                    return true;
                }

                string reason;
                if (IsValidUserDataDirectory(PathEx.DataDir, out reason))
                {
                    WriteMigrationMarker("existing-persistent-data", PathEx.DataDir);
                    return true;
                }

                var candidate = FindBestLegacyDataCandidate();
                if (!string.IsNullOrEmpty(candidate))
                {
                    return PromptForLegacyData(candidate);
                }

                SeedFreshDataFromBundledTemplate();
                WriteMigrationMarker("fresh-install", string.Empty);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "初始化用户数据目录失败，程序将不会继续启动，以避免覆盖或损坏数据。\r\n\r\n" + ex.Message,
                    Params.AppName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }

        public static bool ScheduleBackup(out string error)
        {
            error = string.Empty;
            try
            {
                Directory.CreateDirectory(PathEx.UserDataRoot);
                File.WriteAllText(PendingBackupPath, DateTime.Now.ToString("O"), Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool ScheduleImport(string selectedDirectory, out string normalizedDataDirectory, out string error)
        {
            normalizedDataDirectory = NormalizeSelectedDataDirectory(selectedDirectory);
            error = string.Empty;

            string reason;
            if (!IsValidUserDataDirectory(normalizedDataDirectory, out reason))
            {
                error = reason;
                return false;
            }

            try
            {
                Directory.CreateDirectory(PathEx.UserDataRoot);
                File.WriteAllText(PendingImportPath, normalizedDataDirectory, Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool IsValidUserDataDirectory(string directory, out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                reason = "所选数据目录不存在。";
                return false;
            }

            var paramsDb = Path.Combine(directory, "params.db");
            if (!IsValidSqliteFile(paramsDb))
            {
                reason = "未找到有效的 params.db。请选择旧版 Bot 的 data 文件夹，而不是 Bin 文件夹。";
                return false;
            }

            var botDb = Path.Combine(directory, "bot.db");
            if (File.Exists(botDb) && !IsValidSqliteFile(botDb))
            {
                reason = "检测到 bot.db，但该文件不是有效的 SQLite 数据库。";
                return false;
            }

            return true;
        }

        public static string NormalizeSelectedDataDirectory(string selectedDirectory)
        {
            if (string.IsNullOrWhiteSpace(selectedDirectory))
            {
                return selectedDirectory;
            }

            var fullPath = Path.GetFullPath(selectedDirectory.Trim());
            string reason;
            if (IsValidUserDataDirectory(fullPath, out reason))
            {
                return fullPath;
            }

            var nestedData = Path.Combine(fullPath, "data");
            if (IsValidUserDataDirectory(nestedData, out reason))
            {
                return nestedData;
            }

            return fullPath;
        }

        private static bool PromptForLegacyData(string candidate)
        {
            while (true)
            {
                var result = MessageBox.Show(
                    "检测到旧版 Bot 用户数据：\r\n\r\n" +
                    candidate + "\r\n\r\n" +
                    DescribeDataDirectory(candidate) + "\r\n\r\n" +
                    "【是】使用并迁移这份旧数据（推荐）\r\n" +
                    "【否】全新启动，不使用旧数据\r\n" +
                    "【取消】选择其他旧版 data 目录",
                    "检测到旧版 Bot 数据",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    string error;
                    if (!MigrateDataDirectory(candidate, "automatic-upgrade", out error))
                    {
                        MessageBox.Show(
                            "旧数据迁移失败，程序已停止启动。原数据不会被删除。\r\n\r\n" + error,
                            Params.AppName,
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        return false;
                    }

                    WriteMigrationMarker("automatic-upgrade", candidate);
                    MessageBox.Show(
                        "旧版数据已安全迁移到新的永久数据目录：\r\n\r\n" + PathEx.DataDir,
                        Params.AppName,
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return true;
                }

                if (result == MessageBoxResult.No)
                {
                    SeedFreshDataFromBundledTemplate();
                    WriteMigrationMarker("fresh-install-user-selected", candidate);
                    return true;
                }

                using (var dialog = new Forms.FolderBrowserDialog())
                {
                    dialog.Description = "请选择旧版 Bot 的 data 文件夹，或包含 data 文件夹的旧版程序目录";
                    dialog.ShowNewFolderButton = false;
                    if (dialog.ShowDialog() != Forms.DialogResult.OK)
                    {
                        continue;
                    }

                    var selected = NormalizeSelectedDataDirectory(dialog.SelectedPath);
                    string reason;
                    if (!IsValidUserDataDirectory(selected, out reason))
                    {
                        MessageBox.Show(reason, Params.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
                        continue;
                    }

                    candidate = selected;
                }
            }
        }

        private static bool ApplyPendingBackup()
        {
            if (!File.Exists(PendingBackupPath))
            {
                return true;
            }

            try
            {
                if (Directory.Exists(PathEx.DataDir) && Directory.EnumerateFileSystemEntries(PathEx.DataDir).Any())
                {
                    CreateBackupInternal(PathEx.DataDir, "manual");
                }
                File.Delete(PendingBackupPath);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "创建启动前安全备份失败。为避免继续运行时数据发生变化，本次启动已取消。\r\n\r\n" + ex.Message,
                    Params.AppName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }

        private static bool ApplyPendingImport()
        {
            if (!File.Exists(PendingImportPath))
            {
                return true;
            }

            string source = string.Empty;
            try
            {
                source = File.ReadAllText(PendingImportPath, Encoding.UTF8).Trim();
                string error;
                if (!MigrateDataDirectory(source, "scheduled-import", out error))
                {
                    File.Delete(PendingImportPath);
                    MessageBox.Show(
                        "待导入的数据未能恢复，当前数据已保留。\r\n\r\n" + error,
                        Params.AppName,
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return false;
                }

                File.Delete(PendingImportPath);
                WriteMigrationMarker("scheduled-import", source);
                MessageBox.Show(
                    "数据导入完成。当前数据目录：\r\n\r\n" + PathEx.DataDir,
                    Params.AppName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    File.Delete(PendingImportPath);
                }
                catch
                {
                }

                MessageBox.Show(
                    "执行待导入任务失败。\r\n\r\n来源：" + source + "\r\n" + ex.Message,
                    Params.AppName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }

        private static bool MigrateDataDirectory(string sourceDirectory, string reason, out string error)
        {
            error = string.Empty;
            var source = NormalizeSelectedDataDirectory(sourceDirectory);
            string validationError;
            if (!IsValidUserDataDirectory(source, out validationError))
            {
                error = validationError;
                return false;
            }

            var target = Path.GetFullPath(PathEx.DataDir.TrimEnd('\\', '/'));
            source = Path.GetFullPath(source.TrimEnd('\\', '/'));
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var staging = Path.Combine(PathEx.UserDataRoot, ".migration-staging-" + Guid.NewGuid().ToString("N"));
            string rollbackBackup = null;

            try
            {
                CopyDirectory(source, staging);
                if (!IsValidUserDataDirectory(staging, out validationError))
                {
                    throw new InvalidDataException("迁移后的临时数据校验失败：" + validationError);
                }

                if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                {
                    rollbackBackup = CreateBackupInternal(target, "pre-" + reason);
                }

                if (Directory.Exists(target))
                {
                    Directory.Delete(target, true);
                }

                Directory.Move(staging, target);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try
                {
                    if (Directory.Exists(staging))
                    {
                        Directory.Delete(staging, true);
                    }

                    if (!string.IsNullOrEmpty(rollbackBackup) && Directory.Exists(rollbackBackup))
                    {
                        if (Directory.Exists(target))
                        {
                            Directory.Delete(target, true);
                        }
                        CopyDirectory(rollbackBackup, target);
                    }
                }
                catch (Exception rollbackEx)
                {
                    error += "\r\n回滚时又发生错误：" + rollbackEx.Message;
                }
                return false;
            }
        }

        private static string FindBestLegacyDataCandidate()
        {
            var candidates = new List<string>();
            AddCandidate(candidates, PathEx.LegacyDataDir);

            try
            {
                var currentRoot = new DirectoryInfo(PathEx.ParentOfExePath.TrimEnd('\\', '/'));
                var parent = currentRoot.Parent;
                if (parent != null)
                {
                    foreach (var sibling in parent.GetDirectories().Take(100))
                    {
                        if (string.Equals(sibling.FullName, currentRoot.FullName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var siblingData = Path.Combine(sibling.FullName, "data");
                        var siblingBot = Path.Combine(sibling.FullName, "Bin", "Bot.exe");
                        if (File.Exists(siblingBot))
                        {
                            AddCandidate(candidates, siblingData);
                        }
                    }
                }
            }
            catch
            {
                // 自动发现失败不影响启动；用户仍可在设置页手动选择旧目录。
            }

            return candidates
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(GetDataLastWriteTimeUtc)
                .FirstOrDefault();
        }

        private static void AddCandidate(List<string> candidates, string directory)
        {
            string reason;
            if (IsValidUserDataDirectory(directory, out reason))
            {
                candidates.Add(Path.GetFullPath(directory));
            }
        }

        private static void SeedFreshDataFromBundledTemplate()
        {
            Directory.CreateDirectory(PathEx.DataDir);
            if (Directory.EnumerateFileSystemEntries(PathEx.DataDir).Any())
            {
                return;
            }

            var bundled = PathEx.LegacyDataDir;
            if (!Directory.Exists(bundled))
            {
                return;
            }

            string ignored;
            if (IsValidUserDataDirectory(bundled, out ignored))
            {
                // 包含 params.db 的目录属于真实用户数据，用户既然选择“全新启动”，就不能把它当模板复制。
                return;
            }

            CopyDirectory(bundled, PathEx.DataDir, fileName =>
                !string.Equals(fileName, "params.db", StringComparison.OrdinalIgnoreCase));
        }

        private static string CreateBackupInternal(string sourceDirectory, string prefix)
        {
            Directory.CreateDirectory(BackupsDirectory);
            var safePrefix = string.IsNullOrWhiteSpace(prefix) ? "backup" : prefix.Replace(' ', '-');
            var backup = Path.Combine(BackupsDirectory, safePrefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            CopyDirectory(sourceDirectory, backup);
            return backup;
        }

        private static void CopyDirectory(string sourceDirectory, string destinationDirectory, Func<string, bool> includeFile = null)
        {
            var source = new DirectoryInfo(sourceDirectory);
            if (!source.Exists)
            {
                throw new DirectoryNotFoundException(sourceDirectory);
            }

            Directory.CreateDirectory(destinationDirectory);

            foreach (var file in source.GetFiles())
            {
                if (includeFile != null && !includeFile(file.Name))
                {
                    continue;
                }
                file.CopyTo(Path.Combine(destinationDirectory, file.Name), true);
            }

            foreach (var directory in source.GetDirectories())
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                {
                    continue;
                }
                CopyDirectory(directory.FullName, Path.Combine(destinationDirectory, directory.Name), includeFile);
            }
        }

        private static bool IsValidSqliteFile(string path)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length < 100)
                {
                    return false;
                }

                var expected = Encoding.ASCII.GetBytes("SQLite format 3\0");
                var actual = new byte[expected.Length];
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (stream.Read(actual, 0, actual.Length) != actual.Length)
                    {
                        return false;
                    }
                }

                for (var i = 0; i < expected.Length; i++)
                {
                    if (expected[i] != actual[i])
                    {
                        return false;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static DateTime GetDataLastWriteTimeUtc(string directory)
        {
            try
            {
                var files = new[] { "params.db", "bot.db" }
                    .Select(name => Path.Combine(directory, name))
                    .Where(File.Exists)
                    .Select(File.GetLastWriteTimeUtc)
                    .ToList();
                return files.Count == 0 ? DateTime.MinValue : files.Max();
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private static string DescribeDataDirectory(string directory)
        {
            var lines = new List<string>();
            foreach (var name in new[] { "params.db", "bot.db" })
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    lines.Add(string.Format("✓ {0}  {1:N0} KB  最后修改：{2:yyyy-MM-dd HH:mm}", name, Math.Max(1, info.Length / 1024), info.LastWriteTime));
                }
            }
            return string.Join("\r\n", lines);
        }

        private static void WriteMigrationMarker(string mode, string source)
        {
            var text = new StringBuilder()
                .AppendLine("version=2")
                .AppendLine("completedAt=" + DateTime.Now.ToString("O"))
                .AppendLine("mode=" + (mode ?? string.Empty))
                .AppendLine("source=" + (source ?? string.Empty))
                .AppendLine("dataDir=" + PathEx.DataDir)
                .ToString();
            File.WriteAllText(MigrationMarkerPath, text, Encoding.UTF8);
        }
    }
}
