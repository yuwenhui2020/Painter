using System;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace Painter.Helpers
{
    /// <summary>
    /// 按机器档位选择墨迹保真度。
    /// 低配机（集显 / ≤2 核 / 小内存 / 远程桌面）上「模拟笔锋」要在每笔收笔后重建整条笔迹
    /// 并整条重绘，「墨迹识别」要在收笔时连跑数次 InkAnalyzer.Analyze，停顿远大于观感收益。
    /// 判定**只看可量化的硬件指标，不按操作系统版本一刀切**：Win7 上的高配机器与
    /// Win10/11 走完全相同档位，笔锋、墨迹识别、高质量图片缩放不会被仅因系统版本关掉。
    /// 用户仍可用 Settings.Canvas.AutoPerformance 手动关掉自动降级。
    /// </summary>
    internal static class PerformanceTuning
    {
        public static bool IsLowEnd { get; private set; }

        public static string ProfileName { get; private set; } = "均衡";

        public static string Details { get; private set; } = "";

        /// <summary>最近一次 Apply 实际调整的内容，用于日志。</summary>
        public static string LastAction { get; private set; } = "";

        public static void Detect()
        {
            int tier = RenderCapability.Tier >> 16;
            Version v = Environment.OSVersion.Version;
            int cores = Environment.ProcessorCount;
            long ramMb = TotalPhysicalMemoryMb();

            // tier 0/1 = 无硬件加速或只有部分加速（老集显、远程桌面、虚拟机）；
            // ≤2 核或 <4.5GB 内存基本都是教学一体机的低配型号。
            IsLowEnd = tier <= 1 || cores <= 2 || (ramMb > 0 && ramMb < 4500);
            ProfileName = IsLowEnd ? "流畅优先" : "均衡";
            Details = "GPU渲染层=" + tier + "，CPU核=" + cores + "，内存=" + ramMb + "MB，系统=" + v.Major + "." + v.Minor;
        }

        /// <summary>把低配档位套用到设置上。返回是否有改动（调用方据此决定是否落盘）。</summary>
        public static bool Apply(Settings settings)
        {
            if (settings?.Canvas == null || !settings.Canvas.AutoPerformance || !IsLowEnd)
            {
                return false;
            }
            // 只自动降级一次，之后用户在设置里手动开启笔锋/墨迹识别不再被覆盖。
            if (settings.Canvas.AutoPerformanceApplied)
            {
                return false;
            }
            settings.Canvas.AutoPerformanceApplied = true;
            string action = "";
            if (settings.Canvas.InkStyle == 0 || settings.Canvas.InkStyle == 1)
            {
                settings.Canvas.InkStyle = 2;
                action = "已关闭笔锋";
            }
            if (settings.InkToShape != null && settings.InkToShape.IsInkToShapeEnabled)
            {
                settings.InkToShape.IsInkToShapeEnabled = false;
                action = action.Length == 0 ? "已关闭墨迹识别" : action + "、已关闭墨迹识别";
            }
            LastAction = action.Length == 0 ? "保持当前设置" : action;
            return true;
        }

        private static long TotalPhysicalMemoryMb()
        {
            try
            {
                MEMORYSTATUSEX status = new MEMORYSTATUSEX();
                status.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref status))
                {
                    return (long)(status.ullTotalPhys / 1024 / 1024);
                }
            }
            catch
            {
            }
            return 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }
}
