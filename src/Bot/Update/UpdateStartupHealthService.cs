using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using Bot.Common;
using BotLib;

namespace Bot.UpdateNs
{
    internal static class UpdateStartupHealthService
    {
        private const string HealthFileEnvironmentVariable = "QIANNIU_BOT_UPDATE_HEALTH_FILE";

        internal static void ReportReady()
        {
            var path = Environment.GetEnvironmentVariable(HealthFileEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                // Reassert database readiness at the point where configuration and startup
                // services have also completed. The updater accepts only this explicit contract.
                DbHelper.EnsureInitialized();
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                var temporary = path + ".tmp";
                var payload = new
                {
                    status = "OK",
                    pid = Process.GetCurrentProcess().Id,
                    database_initialized = true,
                    configuration_loaded = true,
                    services_started = true,
                    created_at = DateTime.UtcNow.ToString("o")
                };
                File.WriteAllText(temporary, JsonConvert.SerializeObject(payload));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temporary, path);
                Log.Info("更新启动健康检查返回OK: " + path);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }
    }
}
