using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Painter
{
    static class Hotkey
    {
        #region 系统api
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, HotkeyModifiers fsModifiers, uint vk);

        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        #endregion

        const int WM_HOTKEY = 0x312;

        // id 全进程唯一且只增不减；早期版本依赖 keyid==10 判断 hook 是否安装，
        // 第一次 Regist 失败（快捷键被占用）也会让 keyid++ 变成 11，
        // 于是 WndProc hook 永远装不上，后续热键回调静默失效。现在按 hwnd 管理 hook。
        static int keyid = 10;
        // 按窗口句柄记录已挂载的 WndProc hook：旧实现用全局 _hookInstalled，
        // 窗口重建后新 HwndSource 永远挂不上 hook，热键静默失效。
        static readonly Dictionary<IntPtr, HwndSource> _hookSources = new Dictionary<IntPtr, HwndSource>();
        static readonly object _sync = new object();
        static readonly Dictionary<int, HotKeyCallBackHanlder> keymap = new Dictionary<int, HotKeyCallBackHanlder>();
        static readonly Dictionary<int, IntPtr> idToHwnd = new Dictionary<int, IntPtr>();
        // (窗口句柄|组合键|虚拟键) -> id，保证同一快捷键重复注册时幂等返回
        static readonly Dictionary<string, int> registered = new Dictionary<string, int>();

        /// <summary>
        /// 注册快捷键
        /// </summary>
        public static bool Regist(Window window, HotkeyModifiers fsModifiers, Key key, HotKeyCallBackHanlder callBack)
        {
            if (window == null || callBack == null) return false;
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;            // 窗口句柄尚未创建，不能注册
            var hwndSource = HwndSource.FromHwnd(hwnd);
            if (hwndSource == null) return false;             // 拿不到消息源，装不了 hook

            var vk = KeyInterop.VirtualKeyFromKey(key);
            string regKey = hwnd.ToInt64() + "|" + (int)fsModifiers + "|" + vk;
            lock (_sync)
            {
                if (registered.ContainsKey(regKey)) return true;   // 幂等，避免重复注册泄漏 id
                if (!_hookSources.ContainsKey(hwnd))
                {
                    hwndSource.AddHook(WndProc);
                    _hookSources[hwnd] = hwndSource;
                }
                int id = keyid++;
                if (!RegisterHotKey(hwnd, id, fsModifiers, (uint)vk))
                {
                    MaybeRemoveHook(hwnd); // 注册失败且该窗口无其它热键时撤掉刚挂的 hook
                    return false;
                }
                keymap[id] = callBack;
                idToHwnd[id] = hwnd;
                registered[regKey] = id;
                return true;
            }
        }

        /// <summary> 快捷键消息处理 </summary>
        static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                HotKeyCallBackHanlder callback;
                lock (_sync) { keymap.TryGetValue(id, out callback); }
                if (callback != null)
                {
                    // 回调运行在窗口消息过程里，异常绝不能穿过非托管边界，否则 CLR failfast。
                    try { callback(); } catch { }
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>注销该窗口上注册的全部热键并清理映射，避免重复初始化时重复注册。</summary>
        public static void UnRegistWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            lock (_sync)
            {
                var toRemove = new List<int>();
                foreach (var kv in idToHwnd)
                    if (kv.Value == hWnd) toRemove.Add(kv.Key);
                foreach (var id in toRemove)
                {
                    try { UnregisterHotKey(hWnd, id); } catch { }
                    keymap.Remove(id);
                    idToHwnd.Remove(id);
                }
                RemoveRegistrationIds(toRemove);
                MaybeRemoveHook(hWnd);
            }
        }

        /// <summary> 按回调注销快捷键 </summary>
        public static void UnRegist(IntPtr hWnd, HotKeyCallBackHanlder callBack)
        {
            if (callBack == null) return;
            lock (_sync)
            {
                var toRemove = new List<int>();
                foreach (var kv in keymap)
                {
                    if (kv.Value != callBack) continue;
                    IntPtr h;
                    if (hWnd != IntPtr.Zero && (!idToHwnd.TryGetValue(kv.Key, out h) || h != hWnd)) continue;
                    toRemove.Add(kv.Key);
                }
                var affected = new HashSet<IntPtr>();
                foreach (var id in toRemove)
                {
                    IntPtr h;
                    if (idToHwnd.TryGetValue(id, out h))
                    {
                        affected.Add(h);
                        try { UnregisterHotKey(h, id); } catch { }
                    }
                    keymap.Remove(id);
                    idToHwnd.Remove(id);
                }
                RemoveRegistrationIds(toRemove);
                foreach (var h in affected) MaybeRemoveHook(h);
            }
        }

        /// <summary>该窗口已无任何热键注册时，卸载 hook，使窗口重建后能重新挂载。</summary>
        private static void MaybeRemoveHook(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            foreach (var kv in idToHwnd)
                if (kv.Value == hwnd) return;
            if (_hookSources.TryGetValue(hwnd, out HwndSource source))
            {
                try { source.RemoveHook(WndProc); } catch { }
                _hookSources.Remove(hwnd);
            }
        }

        private static void RemoveRegistrationIds(List<int> ids)
        {
            if (ids == null || ids.Count == 0) return;
            var keys = new List<string>();
            foreach (var kv in registered)
                if (ids.Contains(kv.Value)) keys.Add(kv.Key);
            foreach (var k in keys) registered.Remove(k);
        }

        public delegate void HotKeyCallBackHanlder();
    }

    enum HotkeyModifiers
    {
        MOD_ALT = 0x1,
        MOD_CONTROL = 0x2,
        MOD_SHIFT = 0x4,
        MOD_WIN = 0x8
    }

}
