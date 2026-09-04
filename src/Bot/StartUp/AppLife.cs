using Bot.Automation;
using Bot.UpdateNs;
using BotLib;
using BotLib.Extensions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Bot
{
    public class AppLife
    {
        private const int RuntimeLogSegmentBytes = 1024 * 1024;

        public static void Init()
        {
            // Runtime logs are diagnostic user data, not application binaries. Keep them under the
            // persistent user-data root so an in-place/zip update cannot erase the previous session.
            // Normal restarts keep appending to the active file; LogWriter rotates only at 1 MiB.
            // A verified updater handoff may archive one undersized active file before Log opens it.
            var logDir = Path.Combine(PathEx.UserDataRoot, "logs");
            Directory.CreateDirectory(logDir);
            var updateLogRotation = RuntimeBuildIdentityService.PrepareRuntimeLogForStartup(PathEx.UserDataRoot);
            Log.Initiate(Path.Combine(logDir, "运行日志.txt"), false, RuntimeLogSegmentBytes);
            if (!string.IsNullOrWhiteSpace(updateLogRotation)) Log.Info(updateLogRotation);
            Log.WriteEnvironmentString(Params.SystemInfo);
        }
    }
}