using System;
using Microsoft.Win32;

namespace Painter.Helpers
{
    /// <summary>
    /// Windows 版本能力判断。统一在这里查询，避免各处重复写 6.1 / 6.2 这种魔法数字：
    /// 6.1 = Windows 7，6.2 = Windows 8，6.3 = Windows 8.1，10.0 = Windows 10/11。
    /// <para>
    /// 智能自适应：所有跟系统版本相关的能力都收敛为 SupportsXxx 属性，各模块只依赖这些属性，
    /// 不直接比较版本号。新增平台适配点一律在这里加开关，避免 Win7/Win11 分支散落各处。
    /// </para>
    /// </summary>
    internal static class OsSupport
    {
        public static Version Version => Environment.OSVersion.Version;

        public static bool IsWindows7 => Version.Major == 6 && Version.Minor == 1;

        public static bool IsWindows7OrOlder => Version.Major == 6 && Version.Minor <= 1;

        public static bool IsWindows8 => Version.Major == 6 && Version.Minor == 2;

        public static bool IsWindows81 => Version.Major == 6 && Version.Minor == 3;

        public static bool IsWindows8OrGreater =>
            Version.Major > 6 || (Version.Major == 6 && Version.Minor >= 2);

        public static bool IsWindows10OrGreater => Version.Major >= 10;

        /// <summary>
        /// Win11 的内核版本仍是 10.0，仅靠 Major.Minor 无法区分。真正的分界是 Build 22000，
        /// 且 CurrentBuild 字符串在 Win10 上通常是 10xxx / 19xxx。这里用「10.0 且 Build≥22000」判定。
        /// 老程序读到的版本号可能被 manifest 影响，故失败时安全回退为 false（当作 Win10 处理）。
        /// </summary>
        public static bool IsWindows11
        {
            get
            {
                try
                {
                    if (Version.Major == 10 && Version.Minor == 0 && Version.Build >= 22000)
                        return true;
                    // 兜底：部分环境下 OSVersion 被垫片限制，读注册表的 DisplayVersion/CurrentBuild。
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    {
                        object build = key?.GetValue("CurrentBuild");
                        if (build != null && int.TryParse(build.ToString(), out int b) && b >= 22000)
                            return true;
                    }
                }
                catch { }
                return false;
            }
        }

        /// <summary>DXGI Desktop Duplication 需要 Windows 8 及以上（Win7 上 D3D11CreateDevice 不接受 11_1 特性级别，DuplicateOutput 也不可用）。</summary>
        public static bool SupportsDesktopDuplication => IsWindows8OrGreater;

        /// <summary>HttpListener.AcceptWebSocketAsync 依赖 HTTP.sys 的 WebSocket 支持，Windows 8 才引入。</summary>
        public static bool SupportsHttpSysWebSocket => IsWindows8OrGreater;

        /// <summary>Mica/Acrylic 等 DWM 背景材质：Windows 10 1803 起才有 Acrylic，Win7/8 没有。</summary>
        public static bool SupportsBackdropMaterial => IsWindows10OrGreater;

        /// <summary>Per-Monitor V2 DPI 感知需要 Win10 1607 (build 14393)+；Win7 只能进程级 DPI 感知。</summary>
        public static bool SupportsPerMonitorV2Dpi => IsWindows10OrGreater && Version.Build >= 14393;

        /// <summary>WDA_EXCLUDEFROMCAPTURE（隐私黑窗防截屏）需要 Win10 2004 (build 19041)+。</summary>
        public static bool SupportsExcludeFromCapture => IsWindows10OrGreater && Version.Build >= 19041;

        /// <summary>Windows Ink 手势 API（ApplicationGesture）在 Win7  Tablet PC  Edition 起可用，这里保守视为 Win8+。</summary>
        public static bool SupportsInkGesture => IsWindows8OrGreater;

        public static string Describe()
        {
            string name = IsWindows11 ? "Windows 11" :
                          IsWindows10OrGreater ? "Windows 10" :
                          IsWindows81 ? "Windows 8.1" :
                          IsWindows8 ? "Windows 8" :
                          IsWindows7 ? "Windows 7" : "Windows";
            return name + " " + Version.Major + "." + Version.Minor + "." + Version.Build;
        }
    }
}
