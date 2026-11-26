using System;
using System.Runtime.InteropServices;
using System.Windows;
using Painter.Helpers;

namespace Painter
{
    /// <summary>
    /// 唯一入口。painter 画板与托盘 / 远程桌面 / Web 远控 / PPT 控制模块
    /// 在同一个进程、同一个 exe 内运行：启动本 exe 即同时启动两者。
    /// <para>
    /// 入口放在这里而非使用 App.xaml 的自动生成入口，是为了在 <b>WPF 初始化之前</b>先行处理
    /// 模块的三种独立运行模式——它们由 UAC 支持与会话启动器以
    /// <c>painter.exe -service</c> / <c>-host</c> / <c>-web</c> 拉起，
    /// 其中 <c>-service</c> 运行在 session 0（无交互桌面，WPF 无法初始化）。
    /// </para>
    /// </summary>
    internal static class Program
    {
        // Win7 没有 Win10 的 Per-Monitor V2 DPI API，用 Vista 起就有的
        // SetProcessDPIAware 声明系统 DPI 感知即可（必须在任何窗口创建前调用）。
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        /// <summary>
        /// 启动前自检：运行时门槛已降到 4.6.2（Win10/11 自带的 4.8 已满足，无需安装 4.7.2）。
        /// 只装了 4.0/4.5 的机器即使能进 Main，后续也会因缺 API 抛 TypeLoadException；
        /// 与其闪退，不如在这里给出明确说明。读不到 Release 时不拦截，避免误判。
        /// </summary>
        private static bool EnsureRuntime()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    object release = key?.GetValue("Release");
                    if (release is int r && r > 0 && r < 394802)   // 394802 = .NET Framework 4.6.2
                    {
                        // 不要让用户自己去下载运行时：随包启动程序已内置 .NET 4.6.2 离线包，
                        // 双击它即可自动静默安装再启动本程序。这里只做引导。
                        System.Windows.Forms.MessageBox.Show(
                            "当前系统的 .NET Framework 版本过低（Release=" + r + "），无法直接启动画板。\n\n" +
                            "请关闭本窗口，改为双击程序目录里的【启动程序.exe】——\n" +
                            "它内置了 .NET Framework 4.6.2 离线包，会引导您确认后自动完成安装并启动画板。\n\n" +
                            "（Windows 10 / 11 已自带所需版本，可忽略此提示。）",
                            desk.AppIdentity.ProductName,
                            System.Windows.Forms.MessageBoxButtons.OK,
                            System.Windows.Forms.MessageBoxIcon.Information);
                        return false;
                    }
                }
            }
            catch { }
            return true;
        }

        [STAThread]
        static int Main(string[] args)
        {
            if (!EnsureRuntime()) return 1;
            desk.StartupTiming.Mark("Program.Main 进入", true);

            // ---- Windows 7 专属兼容初始化：必须早于任何窗口 / 网络请求 ----
            if (OsSupport.IsWindows7OrOlder)
            {
                // Win7 SP1 的 SChannel 默认不启用 TLS 1.2。按位或只「添加」协议，
                // 不覆盖系统默认；Win8–Win11 不走这里，TLS 1.3 等默认行为完全不变。
                try
                {
                    System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                }
                catch { }

                // app.manifest 的 dpiAware=true/pm 是 Win8.1+ 语法，Win7 会把它当作
                // 未声明；WPF + WinForms 混用时用 Win7 就有的 API 显式声明。
                try { SetProcessDPIAware(); } catch { }
            }

            // WinForms 全局初始化必须在创建任何 WinForms 控件（desk 的窗体/托盘）之前完成
            desk.DeskBootstrap.InitWinForms();
            desk.StartupTiming.Mark("InitWinForms 完成", true);

            // -service / -host / -web：desk 独立运行模式，接管后本进程直接结束，不加载 WPF
            if (desk.DeskBootstrap.TryRunStandaloneMode(args, out int standaloneExitCode))
            {
                return standaloneExitCode;
            }
            desk.StartupTiming.Mark("非独立模式，继续 WPF", true);

            try
            {
                App app = new App();
                desk.StartupTiming.Mark("new App() 完成", true);
                app.InitializeComponent();
                desk.StartupTiming.Mark("App.InitializeComponent() 完成（XAML 资源已加载）", true);
                int rc = app.Run();
                desk.StartupTiming.Mark("app.Run() 返回", true);
                return rc;
            }
            catch (Exception ex)
            {
                desk.DeskBootstrap.ReportFatal(ex);
                return 1;
            }
        }
    }
}
