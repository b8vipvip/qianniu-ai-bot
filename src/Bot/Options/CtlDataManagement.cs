using BotLib.Extensions;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace Bot.Options
{
    public class CtlDataManagement : UserControl, IOptions
    {
        private readonly TextBlock _status;

        public CtlDataManagement()
        {
            var root = new StackPanel
            {
                Margin = new Thickness(18)
            };

            root.Children.Add(new TextBlock
            {
                Text = "数据管理",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 12)
            });

            root.Children.Add(new TextBlock
            {
                Text = "用户数据已与程序目录分离。以后更换或覆盖 Bot 程序文件，不会影响知识库、API 配置和本地数据库。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            });

            root.Children.Add(new TextBlock
            {
                Text = "当前永久数据目录：",
                FontWeight = FontWeights.SemiBold
            });

            root.Children.Add(new TextBox
            {
                Text = PathEx.DataDir,
                IsReadOnly = true,
                Margin = new Thickness(0, 6, 0, 14),
                Padding = new Thickness(8)
            });

            var buttons = new WrapPanel
            {
                Margin = new Thickness(0, 0, 0, 14)
            };

            buttons.Children.Add(CreateButton("打开数据目录", OpenDataDirectory_Click));
            buttons.Children.Add(CreateButton("安全备份数据", ScheduleBackup_Click));
            buttons.Children.Add(CreateButton("恢复备份", RestoreBackup_Click));
            buttons.Children.Add(CreateButton("从旧版导入", ImportLegacyData_Click));
            root.Children.Add(buttons);

            root.Children.Add(new Border
            {
                BorderBrush = System.Windows.Media.Brushes.LightGray,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12),
                Child = new TextBlock
                {
                    Text = "说明：为避免 SQLite 数据库正在读写时被覆盖，备份、恢复和旧版导入采用“下次启动前执行”的安全模式。安排任务后，关闭并重新启动 Bot 即可完成。",
                    TextWrapping = TextWrapping.Wrap
                }
            });

            _status = new TextBlock
            {
                Margin = new Thickness(0, 14, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            root.Children.Add(_status);

            Content = root;
        }

        public OptionEnum OptionType
        {
            get { return OptionEnum.DataManagement; }
        }

        public void Save(string seller)
        {
        }

        public void RestoreDefault()
        {
        }

        public void NavHelp()
        {
        }

        public void InitUI(string seller)
        {
        }

        private static Button CreateButton(string text, RoutedEventHandler handler)
        {
            var button = new Button
            {
                Content = text,
                Margin = new Thickness(0, 0, 10, 10),
                Padding = new Thickness(16, 7, 16, 7)
            };
            button.Click += handler;
            return button;
        }

        private void OpenDataDirectory_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(PathEx.DataDir);
            PathEx.OpenFolder(PathEx.DataDir);
        }

        private void ScheduleBackup_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "为保证数据库一致性，备份将在下次启动、数据库打开之前自动创建。\r\n\r\n是否安排安全备份？",
                Params.AppName,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            string error;
            if (UserDataMigrationManager.ScheduleBackup(out error))
            {
                _status.Text = "已安排安全备份。请关闭并重新启动 Bot，备份将保存到：\r\n" + UserDataMigrationManager.BackupsDirectory;
                MessageBox.Show(_status.Text, Params.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("安排备份失败：" + error, Params.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RestoreBackup_Click(object sender, RoutedEventArgs e)
        {
            SelectAndScheduleImport("请选择要恢复的备份文件夹", UserDataMigrationManager.BackupsDirectory);
        }

        private void ImportLegacyData_Click(object sender, RoutedEventArgs e)
        {
            SelectAndScheduleImport("请选择旧版 Bot 的 data 文件夹，或旧版 Bot 程序目录", PathEx.ParentOfExePath);
        }

        private void SelectAndScheduleImport(string description, string initialDirectory)
        {
            using (var dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = description;
                dialog.ShowNewFolderButton = false;
                if (Directory.Exists(initialDirectory))
                {
                    dialog.SelectedPath = initialDirectory;
                }

                if (dialog.ShowDialog() != Forms.DialogResult.OK)
                {
                    return;
                }

                string normalized;
                string error;
                if (!UserDataMigrationManager.ScheduleImport(dialog.SelectedPath, out normalized, out error))
                {
                    MessageBox.Show(error, Params.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _status.Text = "已安排下次启动导入：\r\n" + normalized + "\r\n\r\n当前数据会在导入前自动备份。请关闭并重新启动 Bot 完成操作。";
                MessageBox.Show(_status.Text, Params.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
