using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace Painter.Helpers
{
    /// <summary>
    /// 希沃（seewo / CVTE 视源）一体机的触控侧优化。
    ///
    /// 希沃各代触摸框在 Windows 10/11 上由系统自带 HID 驱动直接接管，官网下载中心
    /// 只提供「触摸框测试程序」和调试菜单里的触摸复位，并没有能提升响应速度的通用
    /// 触控驱动，因此这里不安装任何第三方驱动，只做两件确实影响落笔手感的事：
    ///   1. 显式关闭 WPF 的触摸手势等待（按住、轻扫、触摸反馈），避免系统先等手势再出墨；
    ///   2. 在希沃一体机上抬高本进程的调度优先级，减少 UI 线程排队。
    /// 只有检测到希沃硬件才执行第 2 步，其他电脑只做无副作用的第 1 步。
    /// 定时器精度不在这里处理：DeskBootstrap.InitWinForms() 已在进程启动最早期
    /// 统一调用 timeBeginPeriod(1)，对全部机器生效；此处重复调用只会让引用计数与
    /// Release() 的 timeEndPeriod 互相干扰。
    /// </summary>
    internal static class SeewoTuning
    {
        private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";
        private const string OemKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation";

        public static bool IsSeewo { get; private set; }

        /// <summary>是否检测到希沃触摸设备（四边红外框）。仅当此为真时才自动打开“四边红外模式”。</summary>
        public static bool HasSeewoTouch { get; private set; }

        /// <summary>硬件 / 触摸设备的可读摘要，写入日志便于排查驱动问题。</summary>
        public static string DeviceSummary { get; private set; } = "";

        /// <summary>本次实际做过的调整，写入日志。</summary>
        public static string LastAction { get; private set; } = "";

        public static string Describe =>
            (IsSeewo ? "希沃硬件" : "非希沃硬件") +
            (DeviceSummary.Length == 0 ? "" : "（" + DeviceSummary + "）");

        public static void Detect()
        {
            var parts = new List<string>();
            string sysMan = ReadHklm(BiosKey, "SystemManufacturer");
            string boardMan = ReadHklm(BiosKey, "BaseBoardManufacturer");
            string product = ReadHklm(BiosKey, "SystemProductName");
            string oemMan = ReadHklm(OemKey, "Manufacturer");
            string oemModel = ReadHklm(OemKey, "Model");

            string vendor = sysMan.Length != 0 ? sysMan : oemMan;
            string model = product.Length != 0 ? product : oemModel;
            if (vendor.Length != 0) parts.Add("厂商=" + vendor);
            if (model.Length != 0) parts.Add("型号=" + model);
            if (boardMan.Length != 0) parts.Add("主板=" + boardMan);

            bool seewoSoftware = RegistryExists(@"SOFTWARE\Seewo")
                || RegistryExists(@"SOFTWARE\WOW6432Node\Seewo")
                || Directory.Exists(Path.Combine(
                       Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Seewo"));
            if (seewoSoftware) parts.Add("已安装希沃软件");

            bool seewoTablet = false;
            string tablets = DescribeTablets(out seewoTablet);

            // 只认硬件证据：BIOS / OEM 标识或希沃触摸设备。教室电脑上常单独安装
            // 希沃白板等软件，若把“装了希沃软件”也当硬件判据，普通笔记本会误入优化档。
            IsSeewo = ContainsSeewo(sysMan) || ContainsSeewo(boardMan) || ContainsSeewo(product)
                || ContainsSeewo(oemMan) || ContainsSeewo(oemModel) || seewoTablet;
            HasSeewoTouch = seewoTablet;

            if (tablets.Length != 0) parts.Add(tablets);
            else if (IsSeewo) parts.Add("未检测到触摸设备（若书写正常可忽略）");
            DeviceSummary = string.Join("；", parts);
        }

        /// <summary>把画板从系统的触摸手势等待里“摘”出来，落指即成墨。</summary>
        public static void Apply(Window window, DependencyObject inkSurface)
        {
            DisableTouchDelays(window);
            if (inkSurface != null) DisableTouchDelays(inkSurface);

            var actions = new List<string>();
            if (IsSeewo)
            {
                if (TryRaisePriority()) actions.Add("进程优先级=AboveNormal");
            }
            actions.Add("触摸手势等待=已关闭");

            LastAction = string.Join("，", actions)
                + (IsSeewo ? "" : "（非希沃硬件，未提权）");
        }

        /// <summary>
        /// 保留给 Window_Closed 调用。定时精度由启动层统一管理、随进程退出由系统回收，
        /// 这里不能再调用 timeEndPeriod，否则会抵消全局的 1ms 设置。
        /// </summary>
        public static void Release()
        {
        }

        private static void DisableTouchDelays(DependencyObject target)
        {
            if (target == null) return;
            // 窗口级设置会向子元素继承；这里对墨迹层再显式写一遍，
            // 避免以后重建 / 替换 painterCanvas 时漏设，第一笔又要等“按住”计时。
            Stylus.SetIsPressAndHoldEnabled(target, false);
            Stylus.SetIsFlicksEnabled(target, false);
            Stylus.SetIsTapFeedbackEnabled(target, false);
            Stylus.SetIsTouchFeedbackEnabled(target, false);
        }

        private static bool TryRaisePriority()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    ProcessPriorityClass current = process.PriorityClass;
                    // 枚举值是 Win32 标志位，不能比大小；只在后台/普通档位时上调。
                    if (current == ProcessPriorityClass.Normal ||
                        current == ProcessPriorityClass.BelowNormal ||
                        current == ProcessPriorityClass.Idle)
                    {
                        process.PriorityClass = ProcessPriorityClass.AboveNormal;
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private static string DescribeTablets(out bool hasSeewoTablet)
        {
            hasSeewoTablet = false;
            var parts = new List<string>();
            try
            {
                foreach (TabletDevice device in Tablet.TabletDevices)
                {
                    string name = string.IsNullOrWhiteSpace(device.Name) ? "(未命名)" : device.Name.Trim();
                    if (ContainsSeewo(name)) hasSeewoTablet = true;
                    string type = device.Type == TabletDeviceType.Touch ? "触摸" : "笔";
                    parts.Add(type + "=" + name + "[" + device.TabletHardwareCapabilities + "]");
                }
            }
            catch
            {
            }
            return string.Join("；", parts);
        }

        private static bool ContainsSeewo(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return value.IndexOf("seewo", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("希沃", StringComparison.Ordinal) >= 0
                || value.IndexOf("cvte", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("视源", StringComparison.Ordinal) >= 0;
        }

        private static string ReadHklm(string subKey, string valueName)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(subKey))
                {
                    return key?.GetValue(valueName) as string ?? "";
                }
            }
            catch
            {
                return "";
            }
        }

        private static bool RegistryExists(string subKey)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(subKey))
                {
                    return key != null;
                }
            }
            catch
            {
                return false;
            }
        }

    }
}
