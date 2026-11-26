using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Painter.Helpers
{
    /// <summary>
    /// 智能教学工具：激光笔 / 聚光灯 / 放大镜。全部为独立的最顶层透明覆盖窗口，
    /// 鼠标事件穿透（WS_EX_TRANSPARENT），不干扰画板书写与 PPT 翻页。所有 API 都是
    /// Win7 SP1 起就有的标准 WPF/Win32，Win7-Win11 行为一致。
    /// <para>通过全局热键切换：Ctrl+Alt+L 激光笔、Ctrl+Alt+S 聚光灯、Ctrl+Alt+M 放大镜；再按一次同一热键关闭当前工具。</para>
    /// </summary>
    internal static class SmartTeachingTools
    {
        private static ToolOverlay _overlay;
        private static bool _hotkeysRegistered;
        private static Window _hotkeyOwner;
        private static Hotkey.HotKeyCallBackHanlder[] _hotkeyCallbacks;

        public enum ToolMode { None, Laser, Spotlight, Magnifier }

        public static ToolMode CurrentMode => _overlay == null ? ToolMode.None : _overlay.Mode;

        /// <summary>在主窗口句柄就绪后调用一次，注册全局热键并准备覆盖层。</summary>
        public static void Initialize(Window owner)
        {
            // 覆盖层含全屏几何体 + 两个定时器，构造不便宜；Toggle() 内已做懒创建，
            // 启动时只注册热键即可，真正按下 Ctrl+Alt+L/S/M 时才构建覆盖层。
            if (_hotkeysRegistered || owner == null) return;
            try
            {
                // Ctrl+Alt 组合，避开画板已有快捷键与 PPT 常用键。
                var laser = new Hotkey.HotKeyCallBackHanlder(() => Toggle(ToolMode.Laser));
                var spot = new Hotkey.HotKeyCallBackHanlder(() => Toggle(ToolMode.Spotlight));
                var mag = new Hotkey.HotKeyCallBackHanlder(() => Toggle(ToolMode.Magnifier));
                if (!Hotkey.Regist(owner, HotkeyModifiers.MOD_CONTROL | HotkeyModifiers.MOD_ALT, Key.L, laser) ||
                    !Hotkey.Regist(owner, HotkeyModifiers.MOD_CONTROL | HotkeyModifiers.MOD_ALT, Key.S, spot) ||
                    !Hotkey.Regist(owner, HotkeyModifiers.MOD_CONTROL | HotkeyModifiers.MOD_ALT, Key.M, mag))
                {
                    // 部分注册失败（快捷键被占用）时回滚，避免只挂上一半、下次又注册一遍。
                    Hotkey.UnRegist(new WindowInteropHelper(owner).Handle, laser);
                    Hotkey.UnRegist(new WindowInteropHelper(owner).Handle, spot);
                    Hotkey.UnRegist(new WindowInteropHelper(owner).Handle, mag);
                    return;
                }
                _hotkeyCallbacks = new[] { laser, spot, mag };
                _hotkeyOwner = owner;
                _hotkeysRegistered = true;
            }
            catch { }
        }

        public static void Toggle(ToolMode mode)
        {
            if (_overlay == null) _overlay = new ToolOverlay();
            _overlay.Toggle(mode);
        }

        public static void Shutdown()
        {
            if (_overlay != null)
            {
                _overlay.CloseOverlay();
                _overlay = null;
            }
            // 只置标志不注销会让窗口重建后重复注册，热键触发 N 次回调；必须真正退订。
            if (_hotkeysRegistered && _hotkeyOwner != null)
            {
                try
                {
                    var hwnd = new WindowInteropHelper(_hotkeyOwner).Handle;
                    if (_hotkeyCallbacks != null)
                        foreach (var cb in _hotkeyCallbacks)
                            if (cb != null) Hotkey.UnRegist(hwnd, cb);
                }
                catch { }
            }
            _hotkeyCallbacks = null;
            _hotkeyOwner = null;
            _hotkeysRegistered = false;
        }

        #region 覆盖窗口实现

        private sealed class ToolOverlay : Window
        {
            [StructLayout(LayoutKind.Sequential)]
            private struct POINT { public int X; public int Y; }

            [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
            [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
            [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
            private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
            [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
            private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
            [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
            private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
            [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
            private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

            private const int GWL_EXSTYLE = -20;
            private const int WS_EX_TRANSPARENT = 0x20;
            private const int WS_EX_LAYERED = 0x80000;
            private const int WS_EX_TOOLWINDOW = 0x80;
            private const int SM_XVIRTUALSCREEN = 76;
            private const int SM_YVIRTUALSCREEN = 77;
            private const int SM_CXVIRTUALSCREEN = 78;
            private const int SM_CYVIRTUALSCREEN = 79;

            private readonly DispatcherTimer _timer;
            private readonly Grid _root;
            private readonly Path _spotlightMask;
            private readonly EllipseGeometry _hole;
            private readonly Ellipse _laserDot;
            private readonly Border _magnifier;
            private readonly Image _magnifierImage;
            private readonly System.Windows.Forms.Timer _fadeTimer;

            public ToolMode Mode { get; private set; } = ToolMode.None;

            private int _vx, _vy, _vw, _vh;
            private double _laserRadius = 12;
            private DateTime _lastMove = DateTime.Now;
            private bool _pointerVisible;
            private int _lastMagX = int.MinValue, _lastMagY = int.MinValue;
            private DateTime _lastMagAt = DateTime.MinValue;

            public ToolOverlay()
            {
                WindowStyle = WindowStyle.None;
                AllowsTransparency = true;
                Background = Brushes.Transparent;
                Topmost = true;
                ShowInTaskbar = false;
                Focusable = false;
                IsHitTestVisible = false;
                Left = 0; Top = 0; Width = 1; Height = 1;

                _vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
                _vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
                _vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
                _vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
                if (_vw <= 0 || _vh <= 0)
                {
                    _vx = (int)SystemParameters.VirtualScreenLeft;
                    _vy = (int)SystemParameters.VirtualScreenTop;
                    _vw = (int)SystemParameters.VirtualScreenWidth;
                    _vh = (int)SystemParameters.VirtualScreenHeight;
                }

                // 读取智能配置里的激光笔半径
                try
                {
                    double r = MainWindow.Settings?.Smart?.LaserRadius ?? 12;
                    if (r >= 4 && r <= 80) _laserRadius = r;
                }
                catch { }

                _root = new Grid();

                // 聚光灯：整屏半透明黑 - 跟随鼠标的透明圆洞
                _hole = new EllipseGeometry(new Point(_vw / 2.0, _vh / 2.0), 140, 140);
                var rect = new RectangleGeometry(new Rect(0, 0, _vw, _vh));
                var combined = new CombinedGeometry(GeometryCombineMode.Exclude, rect, _hole);
                _spotlightMask = new Path
                {
                    Data = combined,
                    Fill = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)),
                    Visibility = Visibility.Collapsed
                };
                _root.Children.Add(_spotlightMask);

                // 激光笔：带径向光晕的红点
                var glow = new RadialGradientBrush
                {
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(255, 255, 60, 60), 0.0),
                        new GradientStop(Color.FromArgb(220, 255, 0, 0), 0.35),
                        new GradientStop(Color.FromArgb(0, 255, 0, 0), 1.0)
                    }
                };
                _laserDot = new Ellipse
                {
                    Width = _laserRadius * 4,
                    Height = _laserRadius * 4,
                    Fill = glow,
                    Visibility = Visibility.Collapsed
                };
                _root.Children.Add(_laserDot);

                // 放大镜：圆形裁剪边框 + 内部放大的屏幕图像
                _magnifierImage = new Image
                {
                    Stretch = Stretch.Fill,
                    RenderTransformOrigin = new Point(0.5, 0.5)
                };
                _magnifier = new Border
                {
                    Width = 240,
                    Height = 240,
                    BorderBrush = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                    BorderThickness = new Thickness(3),
                    Background = Brushes.Black,
                    Child = _magnifierImage,
                    Visibility = Visibility.Collapsed
                };
                _magnifier.Clip = new EllipseGeometry(new Point(120, 120), 120, 120);
                _root.Children.Add(_magnifier);

                Content = _root;

                _timer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(30)
                };
                _timer.Tick += Timer_Tick;

                // 激光点静止淡出用的 WinForms 计时器（不占用 WPF UI 线程消息优先级）
                _fadeTimer = new System.Windows.Forms.Timer { Interval = 250 };
                _fadeTimer.Tick += FadeTimer_Tick;
            }

            public void Toggle(ToolMode mode)
            {
                if (Mode == mode) mode = ToolMode.None;
                Mode = mode;

                if (Mode == ToolMode.None)
                {
                    _timer.Stop();
                    _fadeTimer.Stop();
                    Hide();
                    return;
                }

                Left = _vx; Top = _vy; Width = _vw; Height = _vh;
                _spotlightMask.Visibility = Mode == ToolMode.Spotlight ? Visibility.Visible : Visibility.Collapsed;
                _laserDot.Visibility = Mode == ToolMode.Laser ? Visibility.Visible : Visibility.Collapsed;
                _magnifier.Visibility = Mode == ToolMode.Magnifier ? Visibility.Visible : Visibility.Collapsed;

                try { if (!IsVisible) Show(); } catch { }
                ApplyClickThrough();
                _lastMove = DateTime.Now;
                _pointerVisible = true;
                _laserDot.Opacity = 1;
                _lastMagAt = DateTime.MinValue;  // 重新启用时强制抓一帧
                _timer.Start();
                if (Mode == ToolMode.Laser) _fadeTimer.Start(); else _fadeTimer.Stop();
            }

            public void CloseOverlay()
            {
                try
                {
                    _timer.Stop();
                    _fadeTimer.Stop();
                    _timer.Tick -= Timer_Tick;
                    _fadeTimer.Tick -= FadeTimer_Tick;
                    ((IDisposable)_timer).Dispose();
                    _fadeTimer.Dispose();
                    Close();
                }
                catch { }
            }

            protected override void OnSourceInitialized(EventArgs e)
            {
                base.OnSourceInitialized(e);
                ApplyClickThrough();
            }

            private void ApplyClickThrough()
            {
                try
                {
                    IntPtr hwnd = new WindowInteropHelper(this).Handle;
                    if (hwnd == IntPtr.Zero) return;
                    int exStyle = IntPtr.Size == 8
                        ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt32()
                        : GetWindowLong32(hwnd, GWL_EXSTYLE);
                    exStyle |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW;
                    if (IntPtr.Size == 8)
                        SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(exStyle));
                    else
                        SetWindowLong32(hwnd, GWL_EXSTYLE, exStyle);
                }
                catch { }
            }

            private void Timer_Tick(object sender, EventArgs e)
            {
                if (!GetCursorPos(out POINT p)) return;
                double x = p.X - _vx;
                double y = p.Y - _vy;

                switch (Mode)
                {
                    case ToolMode.Laser:
                        // 根容器是 Grid，用 Margin + 左/上对齐定位
                        _laserDot.Margin = new Thickness(x - _laserRadius * 2, y - _laserRadius * 2, 0, 0);
                        _laserDot.HorizontalAlignment = HorizontalAlignment.Left;
                        _laserDot.VerticalAlignment = VerticalAlignment.Top;
                        _lastMove = DateTime.Now;
                        break;

                    case ToolMode.Spotlight:
                        _hole.Center = new Point(x, y);
                        break;

                    case ToolMode.Magnifier:
                        // 光标静止时把抓屏降到约 2Hz：既避免 30ms 空转制造大量位图对象，
                        // 又能在 PPT 翻页/视频播放等屏幕内容变化时刷新，不会一直显示旧画面。
                        bool magMoved = p.X != _lastMagX || p.Y != _lastMagY;
                        if (!magMoved && (DateTime.Now - _lastMagAt).TotalMilliseconds < 500) break;
                        _lastMagX = p.X; _lastMagY = p.Y; _lastMagAt = DateTime.Now;
                        double mx = x - 120, my = y - 120;
                        // 避免放大镜盖住光标点本身：向右下偏移一点
                        mx = Math.Min(Math.Max(0, mx + 40), Math.Max(0, _vw - 240));
                        my = Math.Min(Math.Max(0, my + 40), Math.Max(0, _vh - 240));
                        _magnifier.Margin = new Thickness(mx, my, 0, 0);
                        _magnifier.HorizontalAlignment = HorizontalAlignment.Left;
                        _magnifier.VerticalAlignment = VerticalAlignment.Top;
                        CaptureMagnifier(p.X, p.Y);
                        break;
                }
            }

            private void FadeTimer_Tick(object sender, EventArgs e)
            {
                if (Mode != ToolMode.Laser) return;
                double idle = (DateTime.Now - _lastMove).TotalMilliseconds;
                double target = idle > 1200 ? 0.0 : 1.0;
                _laserDot.Opacity += (target - _laserDot.Opacity) * 0.25;
            }

            /// <summary>抓取光标周围一小块屏幕并放大显示（放大镜）。GDI 截屏，Win7 通用。</summary>
            private void CaptureMagnifier(int screenX, int screenY)
            {
                try
                {
                    const int src = 80; // 抓取 80x80 区域放大到 240x240（3 倍）
                    int sx = screenX - src / 2;
                    int sy = screenY - src / 2;
                    using (var bmp = new System.Drawing.Bitmap(src, src,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(sx, sy, 0, 0, new System.Drawing.Size(src, src));
                        IntPtr hBitmap = bmp.GetHbitmap();
                        try
                        {
                            var source = Imaging.CreateBitmapSourceFromHBitmap(
                                hBitmap, IntPtr.Zero, Int32Rect.Empty,
                                BitmapSizeOptions.FromEmptyOptions());
                            _magnifierImage.Source = source;
                        }
                        finally
                        {
                            DeleteObject(hBitmap);
                        }
                    }
                }
                catch { }
            }

            [DllImport("gdi32.dll")]
            private static extern bool DeleteObject(IntPtr hObject);
        }

        #endregion
    }
}
