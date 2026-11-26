using System;
using System.Windows;

namespace Painter
{
    /// <summary>
    /// 把 WPF 画板的能力暴露给远程控制模块（托盘）。
    /// 远程模块（desk 命名空间）不引用 WPF，通过 desk.IHostBridge 与画板通信，
    /// 二者同进程但互相解耦。
    /// 所有调用都切回 WPF 线程（托盘菜单回调发生在 WinForms 的 UI 上下文中）。
    /// </summary>
    internal sealed class HostBridge : desk.IHostBridge
    {
        private readonly App _app;

        public HostBridge(App app)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
        }

        /// <summary>
        /// 画板可见性由 WPF 侧通过 IsVisibleChanged 维护到一个 volatile 字段。
        /// 绝不能用 Dispatcher.Invoke 同步读取：托盘菜单 Opening 事件发生在 Desk STA 线程，
        /// 一旦 WPF UI 线程此刻正等 Desk 线程（退出清理 / COM 调用），两边互等即永久挂起。
        /// </summary>
        public bool IsCanvasVisible => _app.CanvasVisible;

        public void SetCanvasVisible(bool visible)
        {
            try
            {
                _app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (visible) _app.ShowCanvas();
                    else _app.HideCanvas();
                }));
            }
            catch { }
        }

        public void Shutdown()
        {
            // 必须异步投递。同步 Invoke 会让 Desk STA 线程阻塞在这里等 WPF UI 线程，
            // 而 UI 线程执行 App.Shutdown → OnExit → 跨线程 form.Close() 又要等 Desk 线程，
            // 形成 AB-BA 死锁，表现为 Windows 事件日志里的 AppHangB1（窗口无响应）。
            // 若 Dispatcher 已经关闭则直接抛给调用方，由 Form1 的 Environment.Exit 兜底。
            _app.Dispatcher.BeginInvoke(new Action(() =>
            {
                // 允许画板真正关闭，且跳过”三次确认”——从托盘点”退出”已经是明确意图了
                MainWindow.AllowRealClose = true;
                MainWindow.CloseIsFromButton = true;
                App.MarkUserInitiatedShutdown();
                _app.Shutdown();
            }));
        }
    }
}
