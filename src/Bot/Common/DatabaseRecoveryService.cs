using System;
using System.Collections.Generic;
using System.IO;
using BotLib;
using BotLib.Db.Sqlite;

namespace Bot.Common
{
    internal static class DatabaseRecoveryService
    {
        internal static SQLiteHelper OpenOrRecover(string databasePath, List<Type> tableTypes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath));
            Log.Info("数据库健康检查开始: path=" + databasePath);

            try
            {
                var database = new SQLiteHelper(databasePath, tableTypes);
                var integrity = database.ExecuteScalar<string>("PRAGMA integrity_check");
                if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("SQLite integrity_check: " + (integrity ?? "<empty>"));
                }

                Log.Info("数据库完整性: OK");
                Log.Info("自动恢复: false");
                Log.Info("备份路径: <none>");
                return database;
            }
            catch (Exception ex)
            {
                Log.Error("数据库完整性: FAILED; reason=" + ex.Message);
                var backupPath = BackupCorruptDatabase(databasePath);
                Log.Info("自动恢复: true");
                Log.Info("备份路径: " + (backupPath ?? "<database did not exist>"));

                // Do not catch this second initialization failure. If a brand-new database cannot
                // be created (permissions/disk failure), continuing would only hide data loss.
                var recovered = new SQLiteHelper(databasePath, tableTypes);
                var recoveredIntegrity = recovered.ExecuteScalar<string>("PRAGMA integrity_check");
                if (!string.Equals(recoveredIntegrity, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("新数据库完整性检查失败: " + recoveredIntegrity, ex);
                }
                Log.Info("数据库恢复完成；新数据库完整性: OK");
                return recovered;
            }
        }

        private static string BackupCorruptDatabase(string databasePath)
        {
            if (!File.Exists(databasePath)) return null;

            var suffix = ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backupPath = databasePath + suffix;
            for (var sequence = 1; File.Exists(backupPath); sequence++)
            {
                backupPath = databasePath + suffix + "-" + sequence;
            }

            File.Move(databasePath, backupPath);
            MoveSidecarIfPresent(databasePath + "-wal", backupPath + "-wal");
            MoveSidecarIfPresent(databasePath + "-shm", backupPath + "-shm");
            return backupPath;
        }

        private static void MoveSidecarIfPresent(string source, string destination)
        {
            if (File.Exists(source)) File.Move(source, destination);
        }
    }
}
