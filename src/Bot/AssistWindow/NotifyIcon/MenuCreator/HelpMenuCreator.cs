using Bot.Common;
using Bot.Common.Db;
using Bot.Common.Windows;
using Bot.Options;
using BotLib;
using BotLib.Extensions;
using BotLib.Wpf.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Bot.AssistWindow.NotifyIcon.MenuCreator
{
    public class HelpMenuCreator
    {
        public static void Create(CtlNotifyIcon notifyIcon)
        {
            var helpRootMenu = notifyIcon.GetRootMenu(0);
            helpRootMenu.DropDownItems.Add(notifyIcon.CreateItem("打开安装目录", OnOpenAppCatalogClicked));
            helpRootMenu.DropDownItems.Add(notifyIcon.CreateItem("打开data目录", OnOpenDataCatalogClicked));
            helpRootMenu.DropDownItems.Add(notifyIcon.CreateSeparator());
            helpRootMenu.DropDownItems.Add(notifyIcon.CreateItem("打开日志", OnShowLogClicked));
            helpRootMenu.DropDownItems.Add(notifyIcon.CreateItem("清空日志", OnClearLogClicked));
            helpRootMenu.DropDownItems.Add(notifyIcon.CreateSeparator());
            helpRootMenu.DropDownItems.Add(notifyIcon.CreateItem("关于与版本更新", OnAboutClicked));
        }

        private static void OnComeNoodles(object sender, EventArgs e)
        {
            WndNoodles.MyShow();
        }

        private static void OnComeRedBull(object sender, EventArgs e)
        {
            WndRedBull.MyShow();
        }

        private static void OnClearLogClicked(object sender, EventArgs e)
        {
            Log.Clear();
        }

        private static void OnShowLogClicked(object sender, EventArgs e)
        {
            Log.Show();
        }

        private static void OnAboutClicked(object sender, EventArgs e)
        {
            BotAboutUpdateLauncher.Show();
        }

        private static void OnOpenDataCatalogClicked(object sender, EventArgs e)
        {
            try
            {
                Process.Start(PathEx.DataDir);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }

        private static void OnOpenAppCatalogClicked(object sender, EventArgs e)
        {
            try
            {
                Process.Start(PathEx.StartUpPathOfExe);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }
    }
}
