using System;
using System.IO;
using System.Diagnostics;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Text;

// 零外部依赖的引导程序：只使用 Win7 原生自带的 .NET 3.5 (CLR 2.0) BCL，
// 因此即使目标机“什么都没装”（没有 .NET 4.x / Office / VC++ 运行库）也能运行。
//
// 职责：
//   1. 检测 .NET Framework 4.6.2+（画板主程序的硬门槛）。
//   2. 缺失时从随包内置的 runtime\ 目录运行 .NET 4.6.2 完整离线包，
//      以管理员权限静默安装（/q /norestart），完成后再启动 painter.exe。
//   3. 已满足时直接启动 painter.exe，完全透传命令行参数。
//
// 注意：本文件必须用 CLR 2.0 的 csc / v2.0 引用程序集编译，禁止使用
//       C# 3.0+ 语法（var / lambda / 自动属性 / 字符串插值 / 对象初始化器）。
namespace PainterLauncher
{
    internal static class LauncherProgram
    {
        private const string RuntimeSubDir = "runtime";
        private const string NetFxPackage = "NDP462-KB3151800-x86-x64-AllOS-ENU.exe";
        private const string CoreExe = "painter.exe";
        private const string ElevateFlag = "--painter-install-dotnet";
        private const int MinRelease = 394802; // .NET Framework 4.6.2 完整版的 Release 值

        private const int HKEY_LOCAL_MACHINE_HANDLE = unchecked((int)0x80000002);
        private const int KEY_READ = 0x20019;
        private const int KEY_WOW64_64KEY = 0x0100;
        private const int KEY_WOW64_32KEY = 0x0200;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegOpenKeyEx(IntPtr hKey, string subKey, uint options, int samDesired, out IntPtr result);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegQueryValueEx(IntPtr hKey, string valueName, IntPtr reserved, out uint type, byte[] data, ref uint length);

        [DllImport("advapi32.dll")]
        private static extern int RegCloseKey(IntPtr hKey);

        private static string AppDir
        {
            get { return Path.GetDirectoryName(Application.ExecutablePath); }
        }

        private static string LogPath
        {
            get { return Path.Combine(AppDir, "launcher.log"); }
        }

        private static void Log(string message)
        {
            try
            {
                string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + Environment.NewLine;
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
            catch { }
        }

        private static void Info(string message, string title)
        {
            try { MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Information); }
            catch { }
        }

        private static void Error(string message, string title)
        {
            try { MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            catch { }
        }

        // 读取指定注册表视图里的 .NET 4.x Release 值；读不到返回 -1。
        private static int ReadRelease(int wow64Flag)
        {
            IntPtr hk = IntPtr.Zero;
            try
            {
                int err = RegOpenKeyEx(new IntPtr(HKEY_LOCAL_MACHINE_HANDLE),
                    @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full",
                    0, KEY_READ | wow64Flag, out hk);
                if (err != 0) return -1;

                byte[] data = new byte[4];
                uint length = 4;
                uint type = 0;
                err = RegQueryValueEx(hk, "Release", IntPtr.Zero, out type, data, ref length);
                if (err != 0 || length < 4) return -1;
                return BitConverter.ToInt32(data, 0);
            }
            catch { return -1; }
            finally
            {
                if (hk != IntPtr.Zero)
                {
                    try { RegCloseKey(hk); } catch { }
                }
            }
        }

        // 32/64 位视图都查一遍：32 位系统只有 32 位视图，64 位系统两个视图通常一致，
        // 但某些精简系统只写一个视图，取最大值最稳妥。
        private static bool IsDotNetReady()
        {
            int a = ReadRelease(KEY_WOW64_32KEY);
            int b = ReadRelease(KEY_WOW64_64KEY);
            int release = a > b ? a : b;
            Log("检测 .NET Release: 32位视图=" + a + " 64位视图=" + b);
            return release >= MinRelease;
        }

        // 从命令行参数中剥离提权标记，其余原样透传给主程序。
        private static string StripFlagAndJoin(string[] args)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], ElevateFlag, StringComparison.OrdinalIgnoreCase)) continue;
                if (sb.Length > 0) sb.Append(' ');
                // 含空格的参数补引号，保证 -web 9090 password 等参数完整透传。
                if (args[i].IndexOf(' ') >= 0)
                {
                    sb.Append('"').Append(args[i]).Append('"');
                }
                else
                {
                    sb.Append(args[i]);
                }
            }
            return sb.ToString();
        }

        private static string PackagePath
        {
            get { return Path.Combine(Path.Combine(AppDir, RuntimeSubDir), NetFxPackage); }
        }

        private static string CorePath
        {
            get { return Path.Combine(AppDir, CoreExe); }
        }

        // 启动画板主程序（工作目录设为 exe 所在目录，透传全部参数）。
        private static bool StartCore(string coreArgs)
        {
            if (!File.Exists(CorePath))
            {
                Error("找不到主程序：" + CorePath + "\n\n请确保启动器与 painter.exe 位于同一目录。", "启动失败");
                return false;
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = CorePath;
                psi.Arguments = coreArgs;
                psi.WorkingDirectory = AppDir;
                psi.UseShellExecute = false;
                Process.Start(psi);
                Log("已启动主程序：" + CorePath + " " + coreArgs);
                return true;
            }
            catch (Exception ex)
            {
                Log("启动主程序失败：" + ex);
                Error("无法启动画板主程序：\n" + ex.Message, "启动失败");
                return false;
            }
        }

        // 提权实例：真正执行 .NET 4.6.2 静默安装，装完启动主程序。
        private static int RunElevatedInstall(string coreArgs)
        {
            Log("=== 提权安装实例启动 ===");
            string package = PackagePath;
            if (!File.Exists(package))
            {
                Log("内置运行时包缺失：" + package);
                Error("内置的 .NET Framework 安装包缺失：\n" + package + "\n\n请确认完整解压了整个程序目录（含 runtime 子目录）。", "缺少运行时");
                return 2;
            }

            string installLog = Path.Combine(AppDir, "dotnet-install.log");
            try
            {
                Log("开始静默安装 .NET 4.6.2： " + package);
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = package;
                psi.Arguments = " /q /norestart /log \"" + installLog + "\"";
                psi.WorkingDirectory = AppDir;
                psi.UseShellExecute = false;
                using (Process p = Process.Start(psi))
                {
                    if (p != null)
                    {
                        p.WaitForExit();
                        int code = p.ExitCode;
                        Log("安装进程退出码：" + code + "（0=成功，3010=需重启也算成功）");
                        // 0 成功；3010 ERROR_SUCCESS_REBOOT_REQUIRED 也算成功，.NET 4.6.2 可立即使用。
                        if (code != 0 && code != 3010)
                        {
                            Error("安装 .NET Framework 4.6.2 失败（退出码 " + code + "）。\n\n详细日志：" + installLog, "安装失败");
                            return 3;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("安装异常：" + ex);
                Error("安装 .NET Framework 时出错：\n" + ex.Message + "\n\n详细日志：" + installLog, "安装失败");
                return 3;
            }

            if (!IsDotNetReady())
            {
                Error("安装完成但未检测到 .NET Framework 4.6.2。\n请重启电脑后重试，或手动安装：\n" + package, "安装未完成");
                return 4;
            }

            Log(".NET 就绪，安装后启动主程序");
            return StartCore(coreArgs) ? 0 : 5;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            if (args == null) args = new string[0];
            string coreArgs = StripFlagAndJoin(args);
            Log("=== 启动器开始，参数：" + coreArgs + " ===");

            bool elevated = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], ElevateFlag, StringComparison.OrdinalIgnoreCase)) elevated = true;
            }

            if (elevated)
            {
                try { return RunElevatedInstall(coreArgs); }
                catch (Exception ex)
                {
                    Log("提权安装实例异常：" + ex);
                    Error("引导程序异常：" + ex.Message, "启动失败");
                    return 9;
                }
            }

            // 已安装 .NET 4.6.2+：直接启动主程序，零等待、零弹窗。
            if (IsDotNetReady())
            {
                return StartCore(coreArgs) ? 0 : 1;
            }

            // 缺失：需要提权安装。检查内置包是否存在，给出明确提示。
            if (!File.Exists(PackagePath))
            {
                Log("缺少内置运行时包：" + PackagePath);
                Error("本程序需要 .NET Framework 4.6.2，且随包的离线安装包缺失：\n" +
                      PackagePath + "\n\n请完整解压程序目录（确保有 runtime 子目录）后重试。", "缺少运行时");
                return 2;
            }

            Log("未检测到 .NET 4.6.2，请求管理员权限执行静默安装");
            Info("本程序需要安装 .NET Framework 4.6.2（仅首次）。\n\n" +
                 "接下来会弹出“用户账户控制”窗口，请点击“是”允许安装。\n" +
                 "安装过程完全在后台进行，可能需要 1-3 分钟，请耐心等待。", "首次运行准备");

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Application.ExecutablePath;
                psi.Arguments = ElevateFlag + (coreArgs.Length > 0 ? " " + coreArgs : "");
                psi.WorkingDirectory = AppDir;
                psi.UseShellExecute = true;
                psi.Verb = "runas"; // 触发 UAC 提权
                using (Process p = Process.Start(psi))
                {
                    if (p != null) p.WaitForExit();
                    Log("提权安装实例退出码：" + (p != null ? p.ExitCode.ToString() : "未知"));
                }
                // 提权实例负责启动主程序；本实例直接退出，避免两个主程序实例。
                return 0;
            }
            catch (Exception ex)
            {
                // 用户在 UAC 弹窗点了“否”会抛 Win32Exception。
                Log("提权失败：" + ex);
                Error("需要管理员权限才能安装 .NET Framework。\n\n请右键本程序选择“以管理员身份运行”，或在 UAC 弹窗中点击“是”。\n\n" + ex.Message, "需要管理员权限");
                return 6;
            }
        }
    }
}
