using Bot.Automation;
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
            // LogWriter rotates this active file into 1 MiB segments and retains only the last 24h.
            var logDir = Path.Combine(PathEx.UserDataRoot, "logs");
            Directory.CreateDirectory(logDir);
            Log.Initiate(Path.Combine(logDir, "运行日志.txt"), false, RuntimeLogSegmentBytes);
            Log.WriteEnvironmentString(Params.SystemInfo);
        }
    }
}
