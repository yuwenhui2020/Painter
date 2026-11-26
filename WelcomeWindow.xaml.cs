using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using Painter.Helpers;

namespace Painter
{
    /// <summary>
    /// WelcomeWindow.xaml 的交互逻辑
    /// </summary>
    public partial class WelcomeWindow : Window
    {
        public WelcomeWindow()
        {
            InitializeComponent();
            WindowBackdrop.Apply(this);
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (CheckBoxSetAsRecommendation.IsChecked == true)
            {
                // 完成向导后先保存版本至 versions.ini，再强行重启，以读取设置
                MainWindow.SetSettingsToRecommendation();
            }

            if (CheckBoxRunAtStartup.IsChecked == true)
            {
                MainWindow.StartAutomaticallyCreate("Painter");
            }

            if (CheckBoxTouchPenOptimization.IsChecked == true)
            {
                MainWindow.Settings.Appearance.IsShowEraserButton = true;
                MainWindow.Settings.Startup.IsAutoEnterModeFinger = true;
            }

            // 原版 Painter 首次向导里的两个希沃查杀开关，写进画板 Settings.json 的 automation 段
            MainWindow.Settings.Automation.IsAutoKillPptService = CheckBoxAutoKillPptService.IsChecked == true;
            MainWindow.Settings.Automation.IsAutoKillEasiNote = CheckBoxAutoKillEasiNote.IsChecked == true;

            MainWindow.SaveSettingsToFile();
            // 向导结束后会立刻重启进程，防抖的 400ms 保存可能来不及落盘；
            // FlushSettings 走同步写盘，保证新实例能读到刚勾选的查杀设置。
            MainWindow.FlushSettings();

            string str = string.Empty;

            try
            {
                str = File.ReadAllText(System.AppDomain.CurrentDomain.SetupInformation.ApplicationBase + "Versions.ini");
            }
            catch { }

            str = (str + "\n" + Assembly.GetExecutingAssembly().GetName().Version.ToString() + "\n" + "NewWelcomeConfigured").Trim();
            try
            {
                // 必须用 exe 目录下的绝对路径：相对路径依赖进程工作目录，
                // 从其它目录启动时标记写到别处，MainWindow 永远读不到 → 每次都弹向导。
                File.WriteAllText(
                    System.AppDomain.CurrentDomain.SetupInformation.ApplicationBase + "Versions.ini", str);
            }
            catch { }
            // 必须先允许真正关闭：否则 MainWindow.Window_Closing 会命中"关闭即缩回后台"
            // 分支（e.Cancel = true + Hide），画板只是被隐藏、进程不退，
            // 新实例起来后旧实例又被唤醒 → 重启失效。
            MainWindow.CloseIsFromButton = true;
            MainWindow.AllowRealClose = true;
            App.MarkUserInitiatedShutdown();
            // 带 -m：新实例跳过单实例互唤。否则它会通过命名管道把"即将退出"的
            // 旧实例的画板又唤出来，用户看到的就是"重启后什么都没变"。
            Process.Start(System.Windows.Forms.Application.ExecutablePath, "-m");

            Application.Current.Shutdown();
        }
    }
}
