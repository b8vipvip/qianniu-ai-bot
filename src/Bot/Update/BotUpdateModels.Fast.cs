using BotLib;
using BotLib.Db.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.UpdateNs
{
    internal sealed class BotUpdateSettings
    {
        // Compatibility name retained in persisted JSON. It now means “receive server push”,
        // not “periodically check version from the client”.
        public bool AutoCheck { get; set; }
        public bool NotifyPopup { get; set; }
        public bool AutoDownload { get; set; }
        public bool AutoInstall { get; set; }
        public int CheckIntervalHours { get; set; }
        public string SkippedVersion { get; set; }
        public string LastNotifiedVersion { get; set; }
        public string LastNotifiedAt { get; set; }
        public string LastCheckAt { get; set; }

        public BotUpdateSettings()
        {
            AutoCheck = true;
            NotifyPopup = true;
            AutoDownload = false;
            AutoInstall = false;
            CheckIntervalHours = 6;
            SkippedVersion = string.Empty;
            LastNotifiedVersion = string.Empty;
            LastNotifiedAt = string.Empty;
            LastCheckAt = string.Empty;
        }
    }

    internal sealed class BotReleaseInfo
    {
        public string Version { get; set; }
        public string Tag { get; set; }
        public string Name { get; set; }
        public string Notes { get; set; }
        public string HtmlUrl { get; set; }
        public string PackageUrl { get; set; }
        public string MirrorUrl { get; set; }
        public string ManifestUrl { get; set; }
        public string Sha256 { get; set; }
        public long PackageSize { get; set; }
        public DateTime PublishedAt { get; set; }
        public string Commit { get; set; }
        public string Source { get; set; }

        public BotReleaseInfo()
        {
            Version = string.Empty;
            Tag = string.Empty;
            Name = string.Empty;
            Notes = string.Empty;
            HtmlUrl = string.Empty;
            PackageUrl = string.Empty;
            MirrorUrl = string.Empty;
            ManifestUrl = string.Empty;
            Sha256 = string.Empty;
            Commit = string.Empty;
            Source = string.Empty;
        }
    }

    internal sealed class BotUpdateCheckResult
    {
        public bool Success { get; set; }
        public bool UpdateAvailable { get; set; }
        public bool InstallStarted { get; set; }
        public string CurrentVersion { get; set; }
        public string Message { get; set; }
        public BotReleaseInfo Release { get; set; }
        public string DownloadChannel { get; set; }
        public int DownloadPercent { get; set; }
        public long DownloadedBytes { get; set; }
        public long TotalBytes { get; set; }

        public BotUpdateCheckResult()
        {
            CurrentVersion = string.Empty;
            Message = string.Empty;
            DownloadChannel = string.Empty;
            DownloadPercent = -1;
        }
    }
}
