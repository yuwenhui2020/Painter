using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace desk;

/// <summary>
/// Web 端点击「开始放映」后，把 PPT / WPS 的放映窗口强制聚焦并置于最前端。
///
/// HttpListener 的请求线程不是前台线程，直接调 SetForegroundWindow 常被 Windows 的
/// 前台窗口锁拒绝，因此组合使用：ALT 键解锁 + AttachThreadInput + TOPMOST 抖动。
/// 放映窗口由 SlideShowSettings.Run() 异步创建，故用后台任务短暂轮询等它出现，
/// 不会阻塞 Web 请求；退出放映等其它命令不经过这里，行为保持原样。
/// </summary>
internal static class PptWindowFocus
{
    private const int SW_RESTORE = 9;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const byte VK_MENU = 0x12;

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

    // PowerPoint 与 WPS 演示的进程名
    private static readonly string[] PptProcessNames = { "POWERPNT", "wpp" };

    private static int _pending;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    /// <summary>异步等待放映窗口出现后把它置前。同一时刻只跑一个任务。</summary>
    public static void BringSlideShowToFrontAsync()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try
            {
                IntPtr fallback = IntPtr.Zero;
                // PowerPoint 放映窗口类名固定为 screenClass；只有它出现才说明放映真正就绪。
                // 否则刚点开始时只能找到编辑窗口，会被误当成放映窗口置前。
                for (int i = 0; i < 20; i++)
                {
                    IntPtr show = FindWindow("screenClass", null);
                    if (show != IntPtr.Zero && IsWindowVisible(show) && IsPptWindow(show))
                    {
                        ForceForeground(show);
                        return;
                    }
                    IntPtr best = FindSlideShowWindow();
                    if (best != IntPtr.Zero) fallback = best;   // WPS / 无 screenClass 时的兜底
                    Thread.Sleep(100);
                }
                if (fallback != IntPtr.Zero) ForceForeground(fallback);
                else PptController.Log?.Invoke("[PPT] 未找到放映窗口，跳过置前");
            }
            catch (Exception ex)
            {
                PptController.Log?.Invoke("[PPT] 放映窗口置前失败：" + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _pending, 0);
            }
        });
    }

    private static bool IsPptWindow(IntPtr hWnd)
    {
        uint pid;
        GetWindowThreadProcessId(hWnd, out pid);
        if (pid == 0) return false;
        try
        {
            string name = Process.GetProcessById((int)pid).ProcessName;
            foreach (string n in PptProcessNames)
                if (string.Equals(name, n, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>枚举 PowerPoint / WPS 进程的可见顶层窗口，取面积最大者（放映窗口通常全屏）。</summary>
    private static IntPtr FindSlideShowWindow()
    {
        var pids = new HashSet<uint>();
        foreach (string name in PptProcessNames)
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName(name)) pids.Add((uint)p.Id);
            }
            catch { }
        }
        if (pids.Count == 0) return IntPtr.Zero;

        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (!pids.Contains(pid)) return true;
            RECT r;
            if (!GetWindowRect(hWnd, out r)) return true;
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea)
            {
                bestArea = area;
                best = hWnd;
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    private static void ForceForeground(IntPtr hWnd)
    {
        // 模拟一次 ALT 输入，让系统认为用户有操作，从而解除前台窗口锁
        try
        {
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch { }

        uint curThread = GetCurrentThreadId();
        IntPtr fg = GetForegroundWindow();
        uint dummy;
        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out dummy);
        bool attached = fgThread != 0 && fgThread != curThread && AttachThreadInput(curThread, fgThread, true);
        try
        {
            if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
            // 先提到 TOPMOST 再立刻取消，既能压到最前，又不会让窗口永久置顶
            SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached) AttachThreadInput(curThread, fgThread, false);
        }
    }
}
