using Painter.Helpers;
using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Helpers;
using IWshRuntimeLibrary;
using Microsoft.Office.Interop.PowerPoint;
using Microsoft.VisualBasic;
using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Input.StylusPlugIns;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using Application = System.Windows.Application;
using File = System.IO.File;
using MessageBox = System.Windows.MessageBox;
using Path = System.IO.Path;
using Point = System.Windows.Point;
using Timer = System.Timers.Timer;

namespace Painter
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        #region Window Initialization

        public MainWindow()
        {
            InitializeComponent();

            // 系统失焦/捕获被抢会取消触摸但不会触发 PreviewTouchUp，导致 dec 里残留设备 ID。
            // 只监听 LostTouchCapture（真正的捕获丢失）；不能用 TouchLeave —— 手指在画板内
            // 多指手势中滑出边界再滑回是正常操作，TouchLeave 清理会误伤活动触点。
            painterCanvas.LostTouchCapture += (s, e) => RemoveTouchState(e.TouchDevice.Id);
            GridPainterCanvasSelectionCover.LostTouchCapture += (s, e) => RemoveTouchState(e.TouchDevice.Id);

            BorderSettings.Opacity = 0;
            BorderSettings.Visibility = Visibility.Collapsed;
            StackPanelToolButtons.Visibility = Visibility.Collapsed;
            BorderDrawShape.Visibility = Visibility.Collapsed;
            GridPainterCanvasSelectionCover.Visibility = Visibility.Collapsed;

            if (App.StartArgs.Contains("-b")) //-b border
            {
                AllowsTransparency = false;
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                Background = new SolidColorBrush(StringToColor("#FFF2F2F2"));
                Topmost = false;
            }

            if (!App.StartArgs.Contains("-o")) //-old ui
            {
                GroupBoxAppearance.Visibility = Visibility.Collapsed;
                ViewBoxStackPanelMain.Visibility = Visibility.Collapsed;
                ViewBoxStackPanelShapes.Visibility = Visibility.Collapsed;
                HideSubPanels();

                ViewboxFloatingBar.Margin = new Thickness((SystemParameters.WorkArea.Width - 284) / 2, SystemParameters.WorkArea.Height - 80, -2000, -200);
            }
            else
            {
                GroupBoxAppearanceNewUI.Visibility = Visibility.Collapsed;
                ViewboxFloatingBar.Visibility = Visibility.Collapsed;
                GridForRecoverOldUI.Visibility = Visibility.Collapsed;
            }

            InitTimers();
            timeMachine.OnRedoStateChanged += TimeMachine_OnRedoStateChanged;
            timeMachine.OnUndoStateChanged += TimeMachine_OnUndoStateChanged;
            painterCanvas.Strokes.StrokesChanged += StrokesOnStrokesChanged;

            Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;

            // 图片放映：订阅 desk 端状态，把图片渲染到 painterCanvas 之下
            desk.ShowController.StateChanged += ShowOnState;
            // Web 上传时由主窗口提供「当前是否处于 PPT 放映、当前页码、墨迹自动保存目录」。
            // 该委托会在 HttpListener 工作线程上执行，因此只读下面的纯字段，绝不触碰控件。
            desk.ShowController.ContextProvider = GetShareContext;
            // 墨迹识别开关状态由 HTTP 线程直接读取设置字段，不触碰控件。
            desk.CanvasControl.ShapeStateProvider =
                () => Settings.InkToShape != null && Settings.InkToShape.IsInkToShapeEnabled;
            // Web 端画板工具按钮。HTTP 线程只投递命令，所有控件访问都 BeginInvoke 回 UI 线程，
            // 立即返回，避免自动保存/截图之类的同步 IO 把 Web 请求卡住。
            desk.CanvasControl.Apply = (cmd, arg) =>
            {
                if (cmd != "color" && cmd != "eraser" && cmd != "clear"
                    && cmd != "mode" && cmd != "whiteboard"
                    && cmd != "replay" && cmd != "shape") return false;
                // shape 是开关：在 HTTP 线程同步翻转设置字段，本次响应即可带回新状态；
                // 控件 IsOn 与设置落盘仍交给 UI 线程，HTTP 线程绝不触碰控件。
                bool shapeTarget = false;
                try
                {
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher == null || dispatcher.HasShutdownStarted) return false;
                    if (cmd == "shape")
                    {
                        if (Settings.InkToShape == null) Settings.InkToShape = new InkToShape();
                        shapeTarget = !Settings.InkToShape.IsInkToShapeEnabled;
                        Settings.InkToShape.IsInkToShapeEnabled = shapeTarget;
                    }
                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            switch (cmd)
                            {
                                case "color":
                                    switch (arg)
                                    {
                                        case "black": BtnColorBlack_Click(null, null); break;
                                        case "red": BtnColorRed_Click(null, null); break;
                                        case "green": BtnColorGreen_Click(null, null); break;
                                        case "blue": BtnColorBlue_Click(null, null); break;
                                        case "yellow": BtnColorYellow_Click(null, null); break;
                                        case "white": BorderPenColorWhite_MouseUp(null, null); break;
                                    }
                                    break;
                                case "eraser":
                                    // 完全等同点击桌面端「橡皮」按钮：BtnErase_Click 自身会在
                                    // 点擦/笔画擦之间来回 toggle，Web 侧不做任何幂等保护。
                                    BtnErase_Click(BtnErase, null);
                                    break;
                                case "clear": BtnClear_Click(null, null); break;
                                case "mode": BtnHidePainterCanvas_Click(null, null); break;
                                case "whiteboard":
                                    // 与「画笔/鼠标」一样是 toggle：已在白板就退回屏幕模式。
                                    // BtnSwitch_Click 在画板可见且 currentMode==1 时会走到 case 0（屏幕模式），
                                    // 完整处理笔迹存取、主题、PPT 按钮与遮罩；若用户此前切成了鼠标穿透，
                                    // 先恢复画板背景，否则它会误走「进入」分支。
                                    if (currentMode == 1)
                                    {
                                        if (Main_Grid.Background == Brushes.Transparent)
                                            Main_Grid.Background = new SolidColorBrush(StringToColor("#01FFFFFF"));
                                        BtnSwitch_Click(null, null);
                                    }
                                    else
                                    {
                                        EnsureWhiteboardMode();
                                    }
                                    break;
                                case "replay":
                                    {
                                        // 模拟浮动条「墨迹重绘」的按下 + 抬起：先写 lastBorderMouseDownObject
                                        // 才能通过 GridInkReplayButton_MouseUp 的守卫，回放逻辑全部沿用原方法。
                                        var replaySender = lastBorderMouseDownObject ?? new object();
                                        lastBorderMouseDownObject = replaySender;
                                        GridInkReplayButton_MouseUp(replaySender, null);
                                        break;
                                    }
                                case "shape":
                                    // 同步设置面板里的墨迹识别开关，沿用原 Toggled 处理保存设置。
                                    // 程序化赋值不一定触发 Toggled，因此显式再走一次原处理。
                                    ToggleSwitchEnableInkToShape.IsOn = shapeTarget;
                                    ToggleSwitchEnableInkToShape_Toggled(ToggleSwitchEnableInkToShape, null);
                                    break;
                            }
                        }
                        catch { }
                    }));
                    return true;
                }
                catch { return false; }
            };

            // 生态白板笔迹共享：把当前画布笔迹导出/导入为 ISF(.icstk) 字节。
            // 导出需要同步拿到字节，用 Dispatcher.Invoke；导入只投递，用 BeginInvoke 立即返回。
            desk.CanvasControl.ExportStrokes = () =>
            {
                try
                {
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher == null || dispatcher.HasShutdownStarted) return null;
                    byte[] result = null;
                    dispatcher.Invoke(new Action(() =>
                    {
                        try
                        {
                            using (var ms = new System.IO.MemoryStream())
                            {
                                painterCanvas.Strokes.Save(ms);
                                result = ms.ToArray();
                            }
                        }
                        catch { }
                    }));
                    return result;
                }
                catch { return null; }
            };
            desk.CanvasControl.ImportStrokes = (bytes) =>
            {
                try
                {
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher == null || dispatcher.HasShutdownStarted || bytes == null || bytes.Length == 0) return false;
                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            EnsureWhiteboardMode();
                            var incoming = new System.Windows.Ink.StrokeCollection(new System.IO.MemoryStream(bytes));
                            painterCanvas.Strokes.Add(incoming);
                        }
                        catch (Exception ex) { ShowNotification("导入白板笔迹失败：" + ex.Message); }
                    }));
                    return true;
                }
                catch { return false; }
            };
            PreviewMouseWheel += MainWindow_PreviewMouseWheel;
            PreviewMouseLeftButtonDown += MainWindow_PreviewMouseLeftButtonDown;
            PreviewMouseMove += MainWindow_PreviewMouseMove;
            PreviewMouseLeftButtonUp += MainWindow_PreviewMouseLeftButtonUp;
            PreviewMouseDown += MainWindow_PreviewMouseDown;
            SizeChanged += MainWindow_SizeChanged;
        }

        #endregion

        #region Timer - Consolidated Timer System

        // 统一的低频轮询定时器：替代原有的 timerCheckPPT(1s) + _sharePptPollTimer(4s)
        // 合并为单一 2s 轮询，减少 Timer 实例与线程池占用，避免重入竞态
        private DispatcherTimer _unifiedPollTimer;
        private int _pollTickCounter; // 用于实现不同频率：每 1 tick = 2s，PPT 检测每 2 tick (4s)，进程检测每 1 tick (2s)

        private void InitTimers()
        {
            _unifiedPollTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(2) // 统一 2 秒基准周期
            };
            _unifiedPollTimer.Tick += UnifiedPollTimer_Tick;
        }

        private bool _pollBusy;
        private void UnifiedPollTimer_Tick(object sender, EventArgs e)
        {
            if (_pollBusy) return;
            _pollBusy = true;
            _pollTickCounter++;
            try
            {
                // 每 2s (每 tick)：进程存活检测（比原 1s 略降频，足够及时）
                CheckPptProcessAlive();

                // 每 4s (每 2 tick)：PPT 放映页码轮询兜底
                if (_pollTickCounter % 2 == 0)
                {
                    PollPptSlidePosition();
                }

                // 每 10s (每 5 tick)：WPS 模式存活检测
                if (_pollTickCounter % 5 == 0)
                {
                    CheckWpsModeAlive();
                }
            }
            catch { }
            finally { _pollBusy = false; }
        }

        private void CheckPptProcessAlive()
        {
            if (_shareIsPpt) return; // 已在放映中，由 COM 事件驱动
            bool pptAlive = IsProcessRunning("POWERPNT");
            bool wppAlive = isWPSSupportOn && IsProcessRunning("wpp");
            if (!pptAlive && !wppAlive) return;

            // 发现进程：在后台线程获取 COM 对象，避免阻塞 UI
            Task.Run(() => AttachToPptApplication(pptAlive, wppAlive)).ConfigureAwait(false);
        }

        private void PollPptSlidePosition()
        {
            if (!_shareIsPpt) return;

            if (_isWpsMode && _wpsApp != null)
            {
                try
                {
                    int count = Convert.ToInt32(_wpsApp.SlideShowWindows.Count);
                    if (count < 1) { ExitWpsPresentationMode(); return; }
                    int pos = Convert.ToInt32(_wpsApp.SlideShowWindows[1].View.CurrentShowPosition);
                    if (pos > 0 && pos != _shareSlide)
                    {
                        _wpsSlide = pos;
                        Dispatcher.BeginInvoke(new Action(() => SetSharePptContext(true, _sharePresentation, _shareSlidesCount, pos)));
                    }
                }
                catch { }
                return;
            }

            if (pptApplication == null || pptApplication.SlideShowWindows == null || pptApplication.SlideShowWindows.Count < 1)
            {
                Dispatcher.BeginInvoke(new Action(() => SetSharePptContext(false, _sharePresentation, _shareSlidesCount, 0)));
                return;
            }

            try
            {
                int pos2 = pptApplication.SlideShowWindows[1].View.CurrentShowPosition;
                if (pos2 > 0 && pos2 != _shareSlide)
                    Dispatcher.BeginInvoke(new Action(() => SetSharePptContext(true, _sharePresentation, _shareSlidesCount, pos2)));
            }
            catch { }
        }

        private void CheckWpsModeAlive()
        {
            if (!_isWpsMode || _wpsApp == null) return;
            try
            {
                if (Convert.ToInt32(_wpsApp.SlideShowWindows.Count) < 1)
                    Dispatcher.BeginInvoke(new Action(ExitWpsPresentationMode));
            }
            catch { }
        }

        // 将原 TimerCheckPPT_Elapsed 的逻辑拆分：AttachToPptApplication 移至后台线程
        // 使用 COM 对象包装器提供重试、熔断、自动释放
        private readonly ComObjectWrapper<Microsoft.Office.Interop.PowerPoint.Application> _pptComWrapper =
            new ComObjectWrapper<Microsoft.Office.Interop.PowerPoint.Application>("PowerPoint.Application");

        private void AttachToPptApplication(bool pptAlive, bool wppAlive)
        {
            try
            {
                if (pptAlive)
                {
                    ReleaseOldPptApplication();
                    var app = _pptComWrapper.GetOrCreate();
                    if (app != null)
                    {
                        Dispatcher.BeginInvoke(new Action(() => InitializePptApplication(app)));
                    }
                    else
                    {
                        // COM 获取失败，重置熔断器以便下次重试
                        _pptComWrapper.ResetCircuitBreaker();
                    }
                }
                else if (wppAlive)
                {
                    var wpsApp = GetWpsApplication();
                    if (wpsApp != null)
                    {
                        Dispatcher.BeginInvoke(new Action(() => EnterWpsPresentationMode()));
                    }
                }
            }
            catch { }
        }

        private void InitializePptApplication(Microsoft.Office.Interop.PowerPoint.Application app)
        {
            try
            {
                pptApplication = app;
                presentation = pptApplication.ActivePresentation;
                pptApplication.PresentationClose += PptApplication_PresentationClose;
                pptApplication.SlideShowBegin += PptApplication_SlideShowBegin;
                pptApplication.SlideShowNextSlide += PptApplication_SlideShowNextSlide;
                pptApplication.SlideShowEnd += PptApplication_SlideShowEnd;
                slides = presentation.Slides;
                slidescount = slides.Count;
                ReinitMemoryStreams(slidescount + 2);
                slide = slides[pptApplication.ActiveWindow.Selection.SlideRange.SlideNumber];
            }
            catch
            {
                try { slide = pptApplication.SlideShowWindows[1].View.Slide; } catch { }
            }
        }

        /// <summary>
        /// 判断进程是否存在。Process 是 IDisposable，必须逐个释放。
        /// （后台查杀功能已迁移到 desk 的 ProcessKiller，此处仅保留通用辅助方法，
        /// 仍被 WPS 检测等逻辑复用。）
        /// </summary>
        private static bool IsProcessRunning(string processName)
        {
            bool running;
            Process[] processes = null;
            try
            {
                processes = Process.GetProcessesByName(processName);
                running = processes != null && processes.Length > 0;
            }
            catch
            {
                running = false;
            }
            finally
            {
                if (processes != null)
                {
                    foreach (Process process in processes)
                    {
                        try
                        {
                            process.Dispose();
                        }
                        catch
                        {
                        }
                    }
                }
            }
            return running;
        }

        /// <summary>
        /// COM 对象健壮性包装器：提供重试、超时、自动释放、熔断器功能。
        /// 解决：PPT/WPS COM 间歇性失效、RPC_E_DISCONNECTED、0x80010108 (RPC_E_SERVER_DIED) 等问题。
        /// </summary>
        private sealed class ComObjectWrapper<T> : IDisposable where T : class
        {
            private T _obj;
            private readonly string _progId;
            private readonly int _maxRetries;
            private readonly TimeSpan _retryDelay;
            private int _failureCount;
            private readonly int _circuitBreakerThreshold;
            private DateTime _lastFailureTime;
            private readonly object _lock = new object();

            public ComObjectWrapper(string progId, int maxRetries = 3, TimeSpan? retryDelay = null, int circuitBreakerThreshold = 5)
            {
                _progId = progId;
                _maxRetries = maxRetries;
                _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(200);
                _circuitBreakerThreshold = Math.Max(1, circuitBreakerThreshold);
            }

            /// <summary>获取或创建 COM 对象，带重试和熔断器。</summary>
            public T GetOrCreate()
            {
                lock (_lock)
                {
                    // 熔断器检查：连续失败过多且最近失败，直接返回 null 避免雪崩
                    if (_failureCount >= _circuitBreakerThreshold &&
                        DateTime.Now - _lastFailureTime < TimeSpan.FromMinutes(1))
                    {
                        return null;
                    }

                    if (_obj != null)
                    {
                        // 验证对象是否仍有效
                        if (IsValid(_obj)) return _obj;
                        Release();
                    }

                    for (int attempt = 0; attempt < _maxRetries; attempt++)
                    {
                        try
                        {
                            _obj = (T)Marshal.GetActiveObject(_progId);
                            if (_obj != null && IsValid(_obj))
                            {
                                _failureCount = 0; // 成功重置计数
                                return _obj;
                            }
                        }
                        catch (COMException ex) when (ex.ErrorCode == unchecked((int)0x80010108) || // RPC_E_DISCONNECTED
                                                      ex.ErrorCode == unchecked((int)0x800401E3) || // MK_E_UNAVAILABLE
                                                      ex.ErrorCode == unchecked((int)0x80080005))   // CO_E_SERVER_EXEC_FAILURE
                        {
                            // 服务器已死/不可用，等待后重试
                            if (attempt < _maxRetries - 1)
                                Thread.Sleep(_retryDelay);
                        }
                        catch
                        {
                            // 其他异常直接记录并重试
                            if (attempt < _maxRetries - 1)
                                Thread.Sleep(_retryDelay);
                        }
                    }

                    // 所有重试失败
                    _failureCount++;
                    _lastFailureTime = DateTime.Now;
                    Release();
                    return null;
                }
            }

            /// <summary>检查 COM 对象是否仍有效（通过调用简单属性）。</summary>
            private bool IsValid(T obj)
            {
                try
                {
                    // 尝试访问一个简单的属性来验证连接
                    var type = obj.GetType();
                    type.InvokeMember("Version", System.Reflection.BindingFlags.GetProperty, null, obj, null);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            /// <summary>释放 COM 对象引用。</summary>
            public void Release()
            {
                lock (_lock)
                {
                    if (_obj != null)
                    {
                        try { Marshal.FinalReleaseComObject(_obj); } catch { }
                        _obj = null;
                    }
                }
            }

            public void Dispose() => Release();

            /// <summary>重置熔断器状态（用于手动恢复）。</summary>
            public void ResetCircuitBreaker()
            {
                lock (_lock)
                {
                    _failureCount = 0;
                    _lastFailureTime = DateTime.MinValue;
                }
            }
        }

        #endregion Timer

        #region painter Functions

        DrawingAttributes drawingAttributes;
        private void loadPenCanvas()
        {
            SetColors();
            try
            {
                //drawingAttributes = new DrawingAttributes();
                drawingAttributes = painterCanvas.DefaultDrawingAttributes;
                drawingAttributes.Color = ((SolidColorBrush)BtnColorRed.Background).Color;

                drawingAttributes.Height = 2.5;
                drawingAttributes.Width = 2.5;

                painterCanvas.EditingMode = InkCanvasEditingMode.Ink;
            }
            catch { }
        }

        private bool _inkGestureHooked;

        private void StackPanelPPTControls_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            UpdateInkGestureSubscription();
        }

        private void UpdateInkGestureSubscription()
        {
            bool need = StackPanelPPTControls != null && StackPanelPPTControls.Visibility == Visibility.Visible;
            if (need == _inkGestureHooked) return;
            _inkGestureHooked = need;
            if (need)
            {
                painterCanvas.Gesture += PainterCanvas_Gesture;
            }
            else
            {
                painterCanvas.Gesture -= PainterCanvas_Gesture;
            }
        }
        //ApplicationGesture lastApplicationGesture = ApplicationGesture.AllGestures;
        DateTime lastGestureTime = DateTime.Now;
        private void PainterCanvas_Gesture(object sender, InkCanvasGestureEventArgs e)
        {
            try
            {
                // Win7 精简版可能未安装 Tablet PC 手势识别组件，GetGestureRecognitionResults()
                // 会抛异常；原本在 try 外会冒泡成 Dispatcher 未处理异常，这里一并纳入保护。
                ReadOnlyCollection<GestureRecognitionResult> gestures = e.GetGestureRecognitionResults();
                if (gestures == null) return;
                foreach (GestureRecognitionResult gest in gestures)
                {
                    //Trace.WriteLine(string.Format("Gesture: {0}, Confidence: {1}", gest.ApplicationGesture, gest.RecognitionConfidence));
                    if (StackPanelPPTControls.Visibility == Visibility.Visible)
                    {
                        if (gest.ApplicationGesture == ApplicationGesture.Left)
                        {
                            BtnPPTSlidesDown_Click(BtnPPTSlidesDown, null);
                        }
                        if (gest.ApplicationGesture == ApplicationGesture.Right)
                        {
                            BtnPPTSlidesUp_Click(BtnPPTSlidesUp, null);
                        }
                    }
                }
            }
            catch { }
        }

        private void painterCanvas_EditingModeChanged(object sender, RoutedEventArgs e)
        {
            var painterCanvas1 = sender as InkCanvas;
            if (painterCanvas1 == null) return;
            if (Settings.Canvas.IsShowCursor)
            {
                if (painterCanvas1.EditingMode == InkCanvasEditingMode.Ink || drawingShapeMode != 0)
                {
                    painterCanvas1.ForceCursor = true;
                }
                else
                {
                    painterCanvas1.ForceCursor = false;
                }
            }
            else
            {
                painterCanvas1.ForceCursor = false;
            }
            if (painterCanvas1.EditingMode == InkCanvasEditingMode.Ink) forcePointEraser = !forcePointEraser;

            if (painterCanvas.EditingMode == InkCanvasEditingMode.Select)
            {
                SetSelectIconBrush(Color.FromRgb(0, 136, 255));
            }
            else
            {
                SetSelectIconBrush(FloatBarForegroundColor);
            }
        }

        // 编辑模式在触摸屏上切换频繁（每次落指都会走一遍设置）。原先每次都 new 两个
        // SolidColorBrush 赋给同一个图标：既产生垃圾，又因引用变化让图标无条件重绘。
        // 按颜色缓存画笔，颜色不变时重复赋同一个实例不会触发重绘。
        private SolidColorBrush _selectIconBrush;
        private Color _selectIconBrushColor;

        private void SetSelectIconBrush(Color color)
        {
            if (_selectIconBrush == null || _selectIconBrushColor != color)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                _selectIconBrush = brush;
                _selectIconBrushColor = color;
            }
            SymbolIconSelect.Foreground = _selectIconBrush;
        }

        #endregion painter

        #region Hotkeys

        private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (StackPanelPPTControls.Visibility != Visibility.Visible || currentMode != 0) return;
            if (e.Delta >= 120)
            {
                BtnPPTSlidesUp_Click(BtnPPTSlidesUp, null);
            }
            else if (e.Delta <= -120)
            {
                BtnPPTSlidesDown_Click(BtnPPTSlidesDown, null);
            }
        }

        private void Main_Grid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (StackPanelPPTControls.Visibility != Visibility.Visible || currentMode != 0) return;

            if (e.Key == Key.Down || e.Key == Key.PageDown || e.Key == Key.Right || e.Key == Key.N || e.Key == Key.Space)
            {
                BtnPPTSlidesDown_Click(BtnPPTSlidesDown, null);
            }
            if (e.Key == Key.Up || e.Key == Key.PageUp || e.Key == Key.Left || e.Key == Key.P)
            {
                BtnPPTSlidesUp_Click(BtnPPTSlidesUp, null);
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                KeyExit(null, null);
            }
        }

        private void CommandBinding_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = true;
        }

        private void back_HotKey(object sender, ExecutedRoutedEventArgs e)
        {
            try
            {
                painterCanvas.Strokes.Remove(painterCanvas.Strokes[painterCanvas.Strokes.Count - 1]);
            }
            catch { }
        }

        private void KeyExit(object sender, ExecutedRoutedEventArgs e)
        {
            BtnPPTSlideShowEnd_Click(BtnPPTSlideShowEnd, null);
        }

        private void KeyChangeToDrawTool(object sender, ExecutedRoutedEventArgs e)
        {
            // 画板开关的真实状态是 Main_Grid 的背景色，不是 painterCanvas 的可见性：
            // 鼠标模式下若没有开启“选择时隐藏笔迹”，painterCanvas 依旧是 Visible，
            // 用可见性判断会让 Alt+D 在鼠标模式下按了没反应。
            if (Main_Grid.Background == Brushes.Transparent)
            {
                BtnHidePainterCanvas_Click(sender, e);
            }
        }

        private void KeyChangeToSelect(object sender, ExecutedRoutedEventArgs e)
        {
            if (Main_Grid.Background != Brushes.Transparent)
            {
                BtnHidePainterCanvas_Click(sender, e);
            }
        }

        private void KeyChangeToEraser(object sender, ExecutedRoutedEventArgs e)
        {
            // 鼠标模式下画板是穿透的，先切回画笔模式，否则按了橡皮热键也擦不到东西
            if (Main_Grid.Background == Brushes.Transparent)
            {
                BtnHidePainterCanvas_Click(sender, e);
            }
            if (ImageEraserMask.Visibility == Visibility.Visible)
            {
                BorderPenColorRed_MouseUp(null, null);
            }
            else
            {
                BtnErase_Click(sender, e);
            }
        }

        private void KeyCapture(object sender, ExecutedRoutedEventArgs e)
        {
            BtnScreenshot_Click(sender, e);
        }

        private void KeyDrawLine(object sender, ExecutedRoutedEventArgs e)
        {
            SetColorByIndex();
            BtnDrawLine_Click(lastMouseDownSender, e);
        }

        // Alt+1 ~ Alt+5：切换预设画笔颜色（黑/红/绿/蓝/黄）。
        // 这些命令在 XAML 的 InputBindings 里早已声明，但之前缺少 CommandBinding 与
        // Executed 处理器，导致按键按下后没有任何反应；这里补齐最小接线。
        // 颜色按钮处理器同时会清除强制橡皮状态，因此也可用它从橡皮快速切回书写。
        private void KeyChangeToPen1(object sender, ExecutedRoutedEventArgs e) { BtnColorBlack_Click(null, null); }
        private void KeyChangeToPen2(object sender, ExecutedRoutedEventArgs e) { BtnColorRed_Click(null, null); }
        private void KeyChangeToPen3(object sender, ExecutedRoutedEventArgs e) { BtnColorGreen_Click(null, null); }
        private void KeyChangeToPen4(object sender, ExecutedRoutedEventArgs e) { BtnColorBlue_Click(null, null); }
        private void KeyChangeToPen5(object sender, ExecutedRoutedEventArgs e) { BtnColorYellow_Click(null, null); }

        private void KeyHide(object sender, ExecutedRoutedEventArgs e)
        {
            SymbolIconEmoji_MouseUp(sender, null);
        }

        #endregion Hotkeys

        #region TimeMachine

        private enum CommitReason
        {
            UserInput,
            CodeInput,
            ShapeDrawing,
            ShapeRecognition,
            ClearingCanvas,
            Manipulation
        }

        private CommitReason _currentCommitType = CommitReason.UserInput;
        private bool IsEraseByPoint => painterCanvas.EditingMode == InkCanvasEditingMode.EraseByPoint;
        private StrokeCollection ReplacedStroke;
        private StrokeCollection AddedStroke;
        private StrokeCollection CuboidStrokeCollection;
        private Dictionary<Stroke, Tuple<StylusPointCollection, StylusPointCollection>> StrokeManipulationHistory;
        private Dictionary<Stroke, StylusPointCollection> StrokeInitialHistory = new Dictionary<Stroke, StylusPointCollection>();
        private Dictionary<Stroke, Tuple<DrawingAttributes, DrawingAttributes>> DrawingAttributesHistory = new Dictionary<Stroke, Tuple<DrawingAttributes, DrawingAttributes>>();
        private Dictionary<Guid, List<Stroke>> DrawingAttributesHistoryFlag = new Dictionary<Guid, List<Stroke>>()
        {
            { DrawingAttributeIds.Color, new List<Stroke>() },
            { DrawingAttributeIds.DrawingFlags, new List<Stroke>() },
            { DrawingAttributeIds.IsHighlighter, new List<Stroke>() },
            { DrawingAttributeIds.StylusHeight, new List<Stroke>() },
            { DrawingAttributeIds.StylusTip, new List<Stroke>() },
            { DrawingAttributeIds.StylusTipTransform, new List<Stroke>() },
            { DrawingAttributeIds.StylusWidth, new List<Stroke>() }
        };
        private TimeMachine timeMachine = new TimeMachine();

        private void ApplyHistoryToCanvas(TimeMachineHistory item)
        {
            _currentCommitType = CommitReason.CodeInput;
            var currentStrokes = item.GetCurrentStroke();
            var replacedStrokes = item.GetReplacedStroke();

            if (item.CommitType == TimeMachineHistoryType.UserInput)
            {
                if (!item.StrokeHasBeenCleared)
                {
                    foreach (var stroke in currentStrokes)
                        if (!painterCanvas.Strokes.Contains(stroke)) painterCanvas.Strokes.Add(stroke);
                }
                else
                {
                    foreach (var stroke in currentStrokes)
                        if (painterCanvas.Strokes.Contains(stroke)) painterCanvas.Strokes.Remove(stroke);
                }
            }
            else if (item.CommitType == TimeMachineHistoryType.ShapeRecognition)
            {
                if (item.StrokeHasBeenCleared)
                {
                    foreach (var stroke in currentStrokes)
                        if (painterCanvas.Strokes.Contains(stroke)) painterCanvas.Strokes.Remove(stroke);
                    foreach (var stroke in replacedStrokes)
                        if (!painterCanvas.Strokes.Contains(stroke)) painterCanvas.Strokes.Add(stroke);
                }
                else
                {
                    foreach (var stroke in currentStrokes)
                        if (!painterCanvas.Strokes.Contains(stroke)) painterCanvas.Strokes.Add(stroke);
                    foreach (var stroke in replacedStrokes)
                        if (painterCanvas.Strokes.Contains(stroke)) painterCanvas.Strokes.Remove(stroke);
                }
            }
            else if (item.CommitType == TimeMachineHistoryType.Manipulation)
            {
                if (!item.StrokeHasBeenCleared)
                {
                    foreach (var currentStroke in item.StylusPointDictionary)
                        if (painterCanvas.Strokes.Contains(currentStroke.Key))
                            currentStroke.Key.StylusPoints = currentStroke.Value.Item2;
                }
                else
                {
                    foreach (var currentStroke in item.StylusPointDictionary)
                        if (painterCanvas.Strokes.Contains(currentStroke.Key))
                            currentStroke.Key.StylusPoints = currentStroke.Value.Item1;
                }
            }
            else if (item.CommitType == TimeMachineHistoryType.DrawingAttributes)
            {
                if (!item.StrokeHasBeenCleared)
                {
                    foreach (var currentStroke in item.DrawingAttributes)
                        if (painterCanvas.Strokes.Contains(currentStroke.Key))
                            currentStroke.Key.DrawingAttributes = currentStroke.Value.Item2;
                }
                else
                {
                    foreach (var currentStroke in item.DrawingAttributes)
                        if (painterCanvas.Strokes.Contains(currentStroke.Key))
                            currentStroke.Key.DrawingAttributes = currentStroke.Value.Item1;
                }
            }
            else if (item.CommitType == TimeMachineHistoryType.Clear)
            {
                if (!item.StrokeHasBeenCleared)
                {
                    foreach (var stroke in currentStrokes)
                        if (!painterCanvas.Strokes.Contains(stroke)) painterCanvas.Strokes.Add(stroke);
                    foreach (var stroke in replacedStrokes)
                        if (painterCanvas.Strokes.Contains(stroke)) painterCanvas.Strokes.Remove(stroke);
                }
                else
                {
                    foreach (var stroke in replacedStrokes)
                        if (!painterCanvas.Strokes.Contains(stroke)) painterCanvas.Strokes.Add(stroke);
                    foreach (var stroke in currentStrokes)
                        if (painterCanvas.Strokes.Contains(stroke)) painterCanvas.Strokes.Remove(stroke);
                }
            }
            _currentCommitType = CommitReason.UserInput;
        }

        private void TimeMachine_OnUndoStateChanged(bool status)
        {
            var result = status ? Visibility.Visible : Visibility.Collapsed;
            BtnUndo.Visibility = result;
            BtnUndo.IsEnabled = status;
        }

        private void TimeMachine_OnRedoStateChanged(bool status)
        {
            var result = status ? Visibility.Visible : Visibility.Collapsed;
            BtnRedo.Visibility = result;
            BtnRedo.IsEnabled = status;
        }

        private void StrokesOnStrokesChanged(object sender, StrokeCollectionChangedEventArgs e)
        {
            // 逐点擦除每移动一次就会把被擦到的笔迹切成新碎片，碎片要等抬手提交擦除历史时
            // 才有用处。若在这里立刻克隆点集，擦一条长笔迹会随移动次数反复整段拷贝；
            // 基线推迟到 painterCanvas_MouseUp 提交时统一记录。
            bool deferInitialSnapshot = IsEraseByPoint
                && _currentCommitType != CommitReason.CodeInput
                && _currentCommitType != CommitReason.ShapeDrawing;

            foreach (var stroke in e?.Removed)
            {
                stroke.StylusPointsChanged -= Stroke_StylusPointsChanged;
                stroke.StylusPointsReplaced -= Stroke_StylusPointsReplaced;
                stroke.DrawingAttributesChanged -= Stroke_DrawingAttributesChanged;
                StrokeInitialHistory.Remove(stroke);
            }
            foreach (var stroke in e?.Added)
            {
                stroke.StylusPointsChanged += Stroke_StylusPointsChanged;
                stroke.StylusPointsReplaced += Stroke_StylusPointsReplaced;
                stroke.DrawingAttributesChanged += Stroke_DrawingAttributesChanged;
                if (!deferInitialSnapshot) StrokeInitialHistory[stroke] = stroke.StylusPoints.Clone();
            }
            if (_currentCommitType == CommitReason.CodeInput || _currentCommitType == CommitReason.ShapeDrawing) return;
            if ((e.Added.Count != 0 || e.Removed.Count != 0) && IsEraseByPoint)
            {
                if (AddedStroke == null) AddedStroke = new StrokeCollection();
                if (ReplacedStroke == null) ReplacedStroke = new StrokeCollection();
                AddedStroke.Add(e.Added);
                ReplacedStroke.Add(e.Removed);
                return;
            }
            if (e.Added.Count != 0)
            {
                if (_currentCommitType == CommitReason.ShapeRecognition)
                {
                    timeMachine.CommitStrokeShapeHistory(ReplacedStroke, e.Added);
                    ReplacedStroke = null;
                    return;
                }
                else
                {
                    timeMachine.CommitStrokeUserInputHistory(e.Added);
                    return;
                }
            }

            if (e.Removed.Count != 0)
            {
                if (_currentCommitType == CommitReason.ShapeRecognition)
                {
                    ReplacedStroke = e.Removed;
                    return;
                }
                else if (!IsEraseByPoint || _currentCommitType == CommitReason.ClearingCanvas)
                {
                    timeMachine.CommitStrokeEraseHistory(e.Removed);
                    return;
                }
            }
        }

        private void Stroke_DrawingAttributesChanged(object sender, PropertyDataChangedEventArgs e)
        {
            var key = sender as Stroke;
            if (key == null) return;
            var currentValue = key.DrawingAttributes.Clone();
            DrawingAttributesHistory.TryGetValue(key, out var previousTuple);
            var previousValue = previousTuple?.Item1 ?? currentValue.Clone();
            // 非标准 DrawingAttributeIds（如第三方触控驱动写入的私有属性）不在预置字典里，
            // 用索引器会抛 KeyNotFoundException 并从 InkCanvas 事件冒泡出来。按需补建即可。
            if (!DrawingAttributesHistoryFlag.TryGetValue(e.PropertyGuid, out var flagList))
            {
                flagList = new List<Stroke>();
                DrawingAttributesHistoryFlag[e.PropertyGuid] = flagList;
            }
            var needUpdateValue = !flagList.Contains(key);
            if (needUpdateValue)
            {
                flagList.Add(key);
                if (e.PreviousValue != null) Debug.Write(e.PreviousValue.ToString());
            }
            if (e.PropertyGuid == DrawingAttributeIds.Color && needUpdateValue)
            {
                previousValue.Color = (Color)e.PreviousValue;
            }
            if (e.PropertyGuid == DrawingAttributeIds.IsHighlighter && needUpdateValue)
            {
                previousValue.IsHighlighter = (bool)e.PreviousValue;

            }
            if (e.PropertyGuid == DrawingAttributeIds.StylusHeight && needUpdateValue)
            {
                previousValue.Height = (double)e.PreviousValue;
            }
            if (e.PropertyGuid == DrawingAttributeIds.StylusWidth && needUpdateValue)
            {
                previousValue.Width = (double)e.PreviousValue;
            }
            if (e.PropertyGuid == DrawingAttributeIds.StylusTip && needUpdateValue)
            {
                previousValue.StylusTip = (StylusTip)e.PreviousValue;
            }
            if (e.PropertyGuid == DrawingAttributeIds.StylusTipTransform && needUpdateValue)
            {
                previousValue.StylusTipTransform = (Matrix)e.PreviousValue;
            }
            if (e.PropertyGuid == DrawingAttributeIds.DrawingFlags && needUpdateValue)
            {
                previousValue.IgnorePressure = (bool)e.PreviousValue;
            }
            DrawingAttributesHistory[key] = new Tuple<DrawingAttributes, DrawingAttributes>(previousValue, currentValue);
        }

        private void Stroke_StylusPointsReplaced(object sender, StylusPointsReplacedEventArgs e)
        {
            StrokeInitialHistory[sender as Stroke] = e.NewStylusPoints.Clone();
        }

        private void Stroke_StylusPointsChanged(object sender, EventArgs e)
        {
            var stroke = sender as Stroke;
            if (stroke == null) return;
            var selectedStrokes = painterCanvas.GetSelectedStrokes();
            var count = selectedStrokes.Count;
            if (count == 0) count = painterCanvas.Strokes.Count;
            if (StrokeManipulationHistory == null)
            {
                StrokeManipulationHistory = new Dictionary<Stroke, Tuple<StylusPointCollection, StylusPointCollection>>();
            }
            // 逐点擦除产生的碎片基线推迟到抬手才写入（见 StrokesOnStrokesChanged）；
            // 若在那之前有点变更，用当前点集兜底，避免字典取不到键直接抛异常。
            if (!StrokeInitialHistory.TryGetValue(stroke, out var initialPoints))
            {
                initialPoints = stroke.StylusPoints.Clone();
            }
            StrokeManipulationHistory[stroke] =
                new Tuple<StylusPointCollection, StylusPointCollection>(initialPoints, stroke.StylusPoints.Clone());
            if ((StrokeManipulationHistory.Count == count || sender == null) && dec.Count == 0)
            {
                timeMachine.CommitStrokeManipulationHistory(StrokeManipulationHistory);
                foreach (var item in StrokeManipulationHistory)
                {
                    StrokeInitialHistory[item.Key] = item.Value.Item2;
                }
                StrokeManipulationHistory = null;
            }
        }
        #endregion

        #region Definations and Loading

        public static Settings Settings = new Settings();
        public static string settingsFileName = "Settings.json";
        bool isLoaded = false;
        // 配置损坏时只备份、不写回，避免用默认值静默覆盖原文件（由后台加载线程发布）
        private volatile bool _canPersistSettings = true;
        //bool isAutoUpdateEnabled = false;

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // 关键路径：最快显示画笔，非关键全部延后
            loadPenCanvas();
            StackPanelPPTControls.IsVisibleChanged += StackPanelPPTControls_IsVisibleChanged;
            UpdateInkGestureSubscription();

            // 仅同步做极轻量的主题应用，设置加载完全异步化
            ThemeManager.Current.ApplicationTheme = ApplicationTheme.Light;
            if (Environment.Is64BitProcess) GroupBoxInkRecognition.Visibility = Visibility.Collapsed;

            LogHelper.WriteLogToFile("painter Loaded", LogHelper.LogType.Event);

            // 设置加载 + 所有非关键初始化：后台线程完成后再回 UI 线程应用
            Task.Run(() =>
            {
                try { LoadSettingsBackground(); } catch { }
            }).ContinueWith(_ =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ApplyLoadedSettings();
                    isLoaded = true;
                }), DispatcherPriority.ApplicationIdle);
            }, TaskScheduler.Default);

            // 版本记录（IO）完全后台，不等待
            Task.Run(() =>
            {
                try
                {
                    var version = Assembly.GetExecutingAssembly().GetName().Version;
                    var path = App.RootPath + "Versions.ini";
                    var last = File.Exists(path) ? File.ReadAllText(path) : "";
                    if (!last.Contains(version.ToString()))
                        File.WriteAllText(path, (last + "\n" + version).Trim());
                }
                catch { }
            });

            // 低配机优化 + 希沃检测 + 查杀回调 + 教学工具：延后到 ApplicationIdle
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (PerformanceTuning.IsLowEnd)
                    RenderOptions.SetBitmapScalingMode(ShowImage, BitmapScalingMode.Linear);

                SeewoTuning.Detect();
                SeewoTuning.Apply(this, painterCanvas);
                LogHelper.WriteLogToFile("触控优化：" + SeewoTuning.Describe + "；" + SeewoTuning.LastAction, LogHelper.LogType.Event);
                _suppressSeewoAutoToggle = true;
                try { ApplySeewoScreenCompatibility(); }
                finally { _suppressSeewoAutoToggle = false; }

                desk.ProcessKiller.Killed = OnProcessesKilled;
                if (Settings.Smart?.TeachingToolsEnabled == true)
                    try { SmartTeachingTools.Initialize(this); } catch { }
            }), DispatcherPriority.ApplicationIdle);
        }

        // 后台加载设置（不阻塞 UI 线程）
        private void LoadSettingsBackground()
        {
            string settingsPath = App.RootPath + settingsFileName;
            bool fileExists = File.Exists(settingsPath);
            bool loadedOk = false;
            if (fileExists)
            {
                string text = null;
                try
                {
                    text = File.ReadAllText(settingsPath);
                    var options = new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                    };
                    Settings loaded = System.Text.Json.JsonSerializer.Deserialize<Settings>(text, options);
                    if (loaded != null)
                    {
                        Settings = loaded;
                        loadedOk = true;
                    }
                }
                catch
                {
                    try
                    {
                        string badPath = settingsPath + ".bad." + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                        if (text != null) File.WriteAllText(badPath, text);
                        else File.Copy(settingsPath, badPath, true);
                        LogHelper.WriteLogToFile("settings corrupted, backed up to " + badPath, LogHelper.LogType.Event);
                    }
                    catch { }
                }
            }
            bool canPersistSettings = !fileExists || loadedOk;
            _canPersistSettings = canPersistSettings;

            Settings.Advanced = Settings.Advanced ?? new Advanced();
            Settings.Appearance = Settings.Appearance ?? new Appearance();
            Settings.Automation = Settings.Automation ?? new Automation();
            Settings.PowerPointSettings = Settings.PowerPointSettings ?? new PowerPointSettings();
            Settings.Canvas = Settings.Canvas ?? new Canvas();
            Settings.Gesture = Settings.Gesture ?? new Gesture();
            Settings.InkToShape = Settings.InkToShape ?? new InkToShape();
            Settings.Startup = Settings.Startup ?? new Startup();
            Settings.Smart = Settings.Smart ?? new Smart();
            Settings.Roster = Settings.Roster ?? new Roster();

            if (!Settings.PowerPointSettings.HiddenPageNoticeMigrated)
            {
                Settings.PowerPointSettings.HiddenPageNoticeMigrated = true;
                Settings.PowerPointSettings.IsNotifyHiddenPage = false;
            }
            if (!Settings.Automation.AutoKillPptServiceMigrated)
            {
                Settings.Automation.AutoKillPptServiceMigrated = true;
                Settings.Automation.IsAutoKillPptService = true;
            }

            PerformanceTuning.Detect();
            PerformanceTuning.Apply(Settings);
        }

        // 在 UI 线程应用已加载的设置
        private void ApplyLoadedSettings()
        {
            // 用后台加载阶段计算的结果：配置损坏（已备份）时绝不能写回覆盖原文件
            bool canPersist = _canPersistSettings;

            if (canPersist && (Settings.PowerPointSettings.HiddenPageNoticeMigrated || Settings.Automation.AutoKillPptServiceMigrated))
                Task.Run(() => FlushSettings());

            if (Settings.Startup.IsAutoEnterModeFinger)
            {
                ToggleSwitchModeFinger.IsOn = true;
                ToggleSwitchAutoEnterModeFinger.IsOn = true;
            }
            else ToggleSwitchAutoEnterModeFinger.IsOn = false;

            if (Settings.Startup.IsAutoHideCanvas)
            {
                if (!isLoaded) BtnHidePainterCanvas_Click(BtnHidePainterCanvas, null);
                ToggleSwitchAutoHideCanvas.IsOn = true;
            }
            else
            {
                if (!isLoaded) { BtnHidePainterCanvas_Click(BtnHidePainterCanvas, null); BtnHidePainterCanvas_Click(BtnHidePainterCanvas, null); }
                ToggleSwitchAutoHideCanvas.IsOn = false;
            }

            BtnErase.Visibility = Settings.Appearance.IsShowEraserButton ? Visibility.Visible : Visibility.Collapsed;
            ToggleSwitchShowButtonEraser.IsOn = Settings.Appearance.IsShowEraserButton;
            BtnExit.Visibility = Settings.Appearance.IsShowExitButton ? Visibility.Visible : Visibility.Collapsed;
            ToggleSwitchShowButtonExit.IsOn = Settings.Appearance.IsShowExitButton;

            PptNavigationBtn.Visibility = Settings.PowerPointSettings.IsShowPPTNavigation ? Visibility.Visible : Visibility.Collapsed;
            ToggleSwitchShowButtonPPTNavigation.IsOn = Settings.PowerPointSettings.IsShowPPTNavigation;
            ToggleSwitchPptThumbnail.IsOn = Settings.PowerPointSettings.PptThumbnailEnabled;
            ApplyPptNavButtonPosition();

            ApplyThemeSegmentSelection();

            BtnHideControl.Visibility = Settings.Appearance.IsShowHideControlButton ? Visibility.Visible : Visibility.Collapsed;
            ToggleSwitchShowButtonHideControl.IsOn = Settings.Appearance.IsShowHideControlButton;

            BtnSwitchSide.Visibility = Settings.Appearance.IsShowLRSwitchButton ? Visibility.Visible : Visibility.Collapsed;
            ToggleSwitchShowButtonLRSwitch.IsOn = Settings.Appearance.IsShowLRSwitchButton;

            StackPanelModeFinger.Visibility = Settings.Appearance.IsShowModeFingerToggleSwitch ? Visibility.Visible : Visibility.Collapsed;
            ToggleSwitchShowButtonModeFinger.IsOn = Settings.Appearance.IsShowModeFingerToggleSwitch;

            ToggleSwitchTransparentButtonBackground.IsOn = Settings.Appearance.IsTransparentButtonBackground;
            if (Settings.Appearance.IsTransparentButtonBackground)
                BtnExit.Background = new SolidColorBrush(StringToColor("#7F909090"));
            else
                BtnExit.Background = new SolidColorBrush(BtnSwitchTheme.Content.ToString() == "深色" ? StringToColor("#FFCCCCCC") : StringToColor("#FF555555"));

            ToggleSwitchSupportPowerPoint.IsOn = Settings.PowerPointSettings.PowerPointSupport;
            if (Settings.PowerPointSettings.PowerPointSupport) TryStartPptTimer(); else TryStopPptTimer();

            ToggleSwitchShowCanvasAtNewSlideShow.IsOn = Settings.PowerPointSettings.IsShowCanvasAtNewSlideShow;

            ToggleSwitchDisableLockSmithByDefault.IsOn = Settings.Gesture.IsDisableLockSmithByDefault;
            _lockSmith = !Settings.Gesture.IsDisableLockSmithByDefault;
            LockSmithSymbol.Glyph = Settings.Gesture.IsDisableLockSmithByDefault ? "Pin" : "UnPin";

            ToggleSwitchEnableTwoFingerZoom.IsOn = Settings.Gesture.IsEnableTwoFingerZoom;
            ToggleSwitchEnableTwoFingerTranslate.IsOn = Settings.Gesture.IsEnableTwoFingerTranslate;
            ToggleSwitchEnableTwoFingerRotation.IsOn = Settings.Gesture.IsEnableTwoFingerRotation;
            ToggleSwitchEnableTwoFingerRotationOnSelection.IsOn = Settings.Gesture.IsEnableTwoFingerRotationOnSelection;
            ToggleSwitchEnableTwoFingerGestureInPresentationMode.IsOn = Settings.PowerPointSettings.IsEnableTwoFingerGestureInPresentationMode;
            ToggleSwitchEnableFingerGestureSlideShowControl.IsOn = Settings.PowerPointSettings.IsEnableFingerGestureSlideShowControl;

            ToggleSwitchRunAtStartup.IsOn = File.Exists(Environment.GetFolderPath(Environment.SpecialFolder.Startup) + "\\InkCanvas" + ".lnk");

            if (Settings.Canvas != null)
            {
                drawingAttributes.Height = Settings.Canvas.InkWidth;
                drawingAttributes.Width = Settings.Canvas.InkWidth;
                InkWidthSlider.Value = Settings.Canvas.InkWidth * 2;

                ToggleSwitchShowCursor.IsOn = Settings.Canvas.IsShowCursor;
                painterCanvas.ForceCursor = Settings.Canvas.IsShowCursor;

                ComboBoxPenStyle.SelectedIndex = Settings.Canvas.InkStyle;
                ComboBoxEraserSize.SelectedIndex = Settings.Canvas.EraserSize;
                ComboBoxHyperbolaAsymptoteOption.SelectedIndex = (int)Settings.Canvas.HyperbolaAsymptoteOption;
            }

            if (Settings.Automation != null)
            {
                ToggleSwitchClearExitingWritingMode.IsOn = Settings.Automation.IsAutoClearWhenExitingWritingMode;
                ToggleSwitchAutoSaveStrokesAtClear.IsOn = Settings.Automation.IsAutoSaveStrokesAtClear;
                ToggleSwitchAutoSaveStrokesAtScreenshot.IsOn = Settings.Automation.IsAutoSaveStrokesAtScreenshot;
                ToggleSwitchAutoSaveStrokesAtClear.Header = Settings.Automation.IsAutoSaveStrokesAtScreenshot ? "清屏时自动截图并保存墨迹" : "清屏时自动截图";
                ToggleSwitchAutoKillPptService.IsOn = Settings.Automation.IsAutoKillPptService;
                ToggleSwitchAutoKillEasiNote.IsOn = Settings.Automation.IsAutoKillEasiNote;
                ApplySeewoAutoKillSettings();

                ToggleSwitchAutoSaveStrokesInPowerPoint.IsOn = Settings.PowerPointSettings.IsAutoSaveStrokesInPowerPoint;
                ToggleSwitchNotifyPreviousPage.IsOn = Settings.PowerPointSettings.IsNotifyPreviousPage;
                ToggleSwitchNotifyHiddenPage.IsOn = Settings.PowerPointSettings.IsNotifyHiddenPage;
                ToggleSwitchNoStrokeClearInPowerPoint.IsOn = Settings.PowerPointSettings.IsNoClearStrokeOnSelectWhenInPowerPoint;
                ToggleSwitchShowStrokeOnSelectInPowerPoint.IsOn = Settings.PowerPointSettings.IsShowStrokeOnSelectInPowerPoint;
                ToggleSwitchSupportWPS.IsOn = Settings.PowerPointSettings.IsSupportWPS;

                SideControlMinimumAutomationSlider.Value = Settings.Automation.MinimumAutomationStrokeNumber;

                ToggleSwitchHideStrokeWhenSelecting.IsOn = Settings.Canvas.HideStrokeWhenSelecting;
                ToggleSwitchUsingWhiteboard.IsOn = Settings.Canvas.UsingWhiteboard;
                if (Settings.Canvas.UsingWhiteboard)
                {
                    BtnSwitchTheme.Content = "深色";
                    BtnSwitchTheme_Click(null, null);
                }
                ToggleSwitchAutoPerformance.IsOn = Settings.Canvas.AutoPerformance;

                forcePointEraser = Settings.Canvas.EraserType == 1;
                ComboBoxEraserType.SelectedIndex = Settings.Canvas.EraserType;

                ToggleSwitchAutoSaveScreenShotInPowerPoint.IsOn = Settings.PowerPointSettings.IsAutoSaveScreenShotInPowerPoint;
            }

            if (Settings.Advanced != null)
            {
                TouchMultiplierSlider.Value = Settings.Advanced.TouchMultiplier;
                EraserTouchThresholdSlider.Value = Settings.Advanced.EraserTouchThreshold;
                RefreshBoundsWidth();
                ToggleSwitchIsLogEnabled.IsOn = Settings.Advanced.IsLogEnabled;
                ToggleSwitchEraserBindTouchMultiplier.IsOn = Settings.Advanced.EraserBindTouchMultiplier;
                ToggleSwitchIsEnableTouchAreaEraser.IsOn = Settings.Advanced.IsEnableTouchAreaEraser;
                ToggleSwitchIsSpecialScreen.IsOn = Settings.Advanced.IsSpecialScreen;
                TouchMultiplierSlider.Visibility = ToggleSwitchIsSpecialScreen.IsOn ? Visibility.Visible : Visibility.Collapsed;
                ToggleSwitchIsQuadIR.IsOn = Settings.Advanced.IsQuadIR;
            }

            if (Settings.InkToShape != null)
                ToggleSwitchEnableInkToShape.IsOn = Settings.InkToShape.IsInkToShapeEnabled;

            SystemEvents_UserPreferenceChanged(null, null);

            string smartState = "几何识别:" + (Settings.Smart.GeometryShapeEnabled ? "开" : "关")
                 + "｜笔迹平滑:" + (Settings.Smart.StrokeSmoothingEnabled ? "开" : "关")
                 + "｜教学工具:" + (Settings.Smart.TeachingToolsEnabled ? "开" : "关");
            LogHelper.WriteLogToFile(
                "系统能力：" + OsSupport.Describe()
                + "｜DXGI 桌面复制：" + (OsSupport.SupportsDesktopDuplication ? "支持" : "回退 GDI 抓屏")
                + "｜HTTP.sys WebSocket：" + (OsSupport.SupportsHttpSysWebSocket ? "支持" : "使用兼容端口")
                + "｜窗口材质：" + (OsSupport.SupportsBackdropMaterial ? "支持" : "纯色背景")
                + "｜每显示器DPI：" + (OsSupport.SupportsPerMonitorV2Dpi ? "支持" : "进程级")
                + "｜智能画板[" + smartState + "]",
                LogHelper.LogType.Event);
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            LogHelper.WriteLogToFile("painter closing", LogHelper.LogType.Event);

            // 合并后的单一进程：关闭画板 = 缩回后台，托盘/被控端继续运行（不弹三次确认）。
            // 托盘菜单会在下一次打开时把该项文案切换为“开启画板”。
            if (!AllowRealClose)
            {
                e.Cancel = true;
                Hide();
                LogHelper.WriteLogToFile("painter canvas hidden, still running in tray", LogHelper.LogType.Event);
                return;
            }

            if (!CloseIsFromButton)
            {
                e.Cancel = true;
                if (MessageBox.Show("是否继续关闭 painter 画板，这将丢失当前未保存的工作。", "painter 画板", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
                {
                    if (MessageBox.Show("真的狠心关闭 painter 画板吗？", "painter 画板", MessageBoxButton.OKCancel, MessageBoxImage.Error) == MessageBoxResult.OK)
                    {
                        if (MessageBox.Show("是否取消关闭 painter 画板？", "painter 画板", MessageBoxButton.OKCancel, MessageBoxImage.Error) != MessageBoxResult.OK)
                        {
                            e.Cancel = false;
                        }
                    }
                }
            }
            if (e.Cancel)
            {
                LogHelper.WriteLogToFile("painter closing cancelled", LogHelper.LogType.Event);
            }
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            FlushSettings();
            // 关闭智能教学工具覆盖窗口，避免置顶透明窗残留
            try { SmartTeachingTools.Shutdown(); } catch { }
            SeewoTuning.Release();
            // 查杀回调是静态委托且捕获了 MainWindow，进程退出前解绑，避免整棵可视化树被一直引用
            desk.ProcessKiller.Killed = null;
            // ShowController 是静态类：不退订的话退出序列中手机端仍会向已关闭窗口广播，
            // ShowOnState 异步排到 UI 队列的动作会访问已释放控件；ContextProvider 还会
            // 把 MainWindow 实例一直挂在静态字段上。
            try { desk.ShowController.StateChanged -= ShowOnState; } catch { }
            try { desk.ShowController.ContextProvider = null; } catch { }
            // 静态委托捕获了 MainWindow，退出时必须清掉，否则整棵可视化树无法回收
            try { desk.CanvasControl.Apply = null; } catch { }
            try { desk.CanvasControl.ShapeStateProvider = null; } catch { }
            // SystemEvents.UserPreferenceChanged 是静态事件，不退订会一直持有 MainWindow
            // 强引用，导致整个可视化树（含全部笔迹）永远无法被 GC 回收。
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
            timeMachine.OnRedoStateChanged -= TimeMachine_OnRedoStateChanged;
            timeMachine.OnUndoStateChanged -= TimeMachine_OnUndoStateChanged;
            painterCanvas.Strokes.StrokesChanged -= StrokesOnStrokesChanged;

            // 必须先置 disposed 闸门再解绑 COM 事件、释放 Timer：这些处理器运行在 PowerPoint 的
            // RPC 线程上，若仍挂着，用户在 PPT 里翻页/关闭演示文稿会进入回调去
            // Start() 一个已 Dispose 的 Timer → ObjectDisposedException，且无人捕获 → 进程终止。
            _pptTimerDisposed = true;
            try { UnhookPptEvents(); } catch { }
            try { pptApplication = null; } catch { }
            try { presentation = null; } catch { }

            // 统一轮询定时器清理
            try { if (_unifiedPollTimer != null) { _unifiedPollTimer.Stop(); _unifiedPollTimer.Tick -= UnifiedPollTimer_Tick; _unifiedPollTimer = null; } } catch { }
            _pptTimerDisposed = true;

            DisposeAllMemoryStreams();

            LogHelper.WriteLogToFile("painter closed", LogHelper.LogType.Event);
        }

        private static void PreloadIALibrary()
        {
            GC.KeepAlive(typeof(InkAnalyzer));
            GC.KeepAlive(typeof(AnalysisAlternate));
            GC.KeepAlive(typeof(InkDrawingNode));
            // InkAnalyzer 是 IDisposable 且持有非托管识别上下文，必须释放
            using (InkAnalyzer analyzer = new InkAnalyzer())
            {
                analyzer.AddStrokes(new StrokeCollection() {
                    new Stroke(new StylusPointCollection() {
                        new StylusPoint(114,514),
                        new StylusPoint(191,9810),
                        new StylusPoint(7,21),
                        new StylusPoint(123,789),
                    })
                });
                analyzer.Analyze();
            }
        }

        private void LoadSettings(bool isStartup = true)
        {
            string settingsPath = App.RootPath + settingsFileName;
            bool fileExists = File.Exists(settingsPath);
            bool loadedOk = false;
            if (fileExists)
            {
                string text = null;
                try
                {
                    text = File.ReadAllText(settingsPath);
                    var options = new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                    };
                    Settings loaded = System.Text.Json.JsonSerializer.Deserialize<Settings>(text, options);
                    // 手改过的配置里可能出现 "null" 或把某个子节点写成 null，
                    // 直接套用会让后面所有 Settings.Xxx.Yyy 变成空引用。
                    if (loaded != null)
                    {
                        Settings = loaded;
                        loadedOk = true;
                    }
                }
                catch
                {
                    // 配置损坏时绝不能让下面的迁移逻辑用默认配置静默覆盖原文件，
                    // 否则用户的全部设置永久丢失且无从恢复。先备份损坏文件供人工恢复，
                    // 并阻止本次启动期间的迁移写盘，保留原文件原样。
                    try
                    {
                        string badPath = settingsPath + ".bad." + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                        if (text != null) File.WriteAllText(badPath, text);
                        else File.Copy(settingsPath, badPath, true);
                        LogHelper.WriteLogToFile("settings corrupted, backed up to " + badPath, LogHelper.LogType.Event);
                    }
                    catch { }
                }
            }
            // 文件不存在（首次运行）或成功加载时才允许迁移落盘；配置损坏时保留原文件。
            bool canPersistSettings = !fileExists || loadedOk;

            Settings.Advanced = Settings.Advanced ?? new Advanced();
            Settings.Appearance = Settings.Appearance ?? new Appearance();
            Settings.Automation = Settings.Automation ?? new Automation();
            Settings.PowerPointSettings = Settings.PowerPointSettings ?? new PowerPointSettings();
            Settings.Canvas = Settings.Canvas ?? new Canvas();
            Settings.Gesture = Settings.Gesture ?? new Gesture();
            Settings.InkToShape = Settings.InkToShape ?? new InkToShape();
            Settings.Startup = Settings.Startup ?? new Startup();
            // 智能画板配置是后加分组，旧版 Settings.json 没有 smart 节点时补齐默认开启。
            Settings.Smart = Settings.Smart ?? new Smart();
            // Roster 是后加字段：旧配置 / 手改配置里写成 "roster": null 时，
            // 点名器 ShowRosterInfo 会直接空引用；这里和其它子对象一样补齐。
            Settings.Roster = Settings.Roster ?? new Roster();

            // 旧版本默认开启「提示隐藏幻灯片」并把 true 写进了配置，导致每次打开 PPT
            // 都弹「是否取消隐藏」。现在默认无需提醒，对历史配置做一次性归零并落盘；
            // 迁移之后用户仍可在设置页自行开启该开关，不会被反复重置。
            if (!Settings.PowerPointSettings.HiddenPageNoticeMigrated)
            {
                Settings.PowerPointSettings.HiddenPageNoticeMigrated = true;
                Settings.PowerPointSettings.IsNotifyHiddenPage = false;
                if (canPersistSettings) FlushSettings();
            }

            // 旧版本「自动查杀 PPT 小工具」默认关闭并把 false 写进了配置。
            // 现在改为默认开启，对历史配置一次性归位并落盘；迁移之后用户
            // 仍可在设置页自行关闭该开关，不会被每次启动反复重置。
            if (!Settings.Automation.AutoKillPptServiceMigrated)
            {
                Settings.Automation.AutoKillPptServiceMigrated = true;
                Settings.Automation.IsAutoKillPptService = true;
                if (canPersistSettings) FlushSettings();
            }

            if (Settings.Startup.IsAutoEnterModeFinger)
            {
                ToggleSwitchModeFinger.IsOn = true;
                ToggleSwitchAutoEnterModeFinger.IsOn = true;
            }
            else
            {
                ToggleSwitchAutoEnterModeFinger.IsOn = false;
            }
            if (Settings.Startup.IsAutoHideCanvas)
            {
                if (isStartup)
                {
                    BtnHidePainterCanvas_Click(BtnHidePainterCanvas, null);
                }
                ToggleSwitchAutoHideCanvas.IsOn = true;
            }
            else
            {
                if (isStartup)
                {
                    BtnHidePainterCanvas_Click(BtnHidePainterCanvas, null);
                    BtnHidePainterCanvas_Click(BtnHidePainterCanvas, null);
                }
                ToggleSwitchAutoHideCanvas.IsOn = false;
            }

            if (Settings.Appearance.IsShowEraserButton)
            {
                BtnErase.Visibility = Visibility.Visible;
                ToggleSwitchShowButtonEraser.IsOn = true;
            }
            else
            {
                BtnErase.Visibility = Visibility.Collapsed;
                ToggleSwitchShowButtonEraser.IsOn = false;
            }
            if (Settings.Appearance.IsShowExitButton)
            {
                BtnExit.Visibility = Visibility.Visible;
                ToggleSwitchShowButtonExit.IsOn = true;
            }
            else
            {
                BtnExit.Visibility = Visibility.Collapsed;
                ToggleSwitchShowButtonExit.IsOn = false;
            }

            PptNavigationBtn.Visibility =
                Settings.PowerPointSettings.IsShowPPTNavigation ? Visibility.Visible : Visibility.Collapsed;
            ToggleSwitchShowButtonPPTNavigation.IsOn = Settings.PowerPointSettings.IsShowPPTNavigation;
            ToggleSwitchPptThumbnail.IsOn = Settings.PowerPointSettings.PptThumbnailEnabled;
            ApplyPptNavButtonPosition();

            ApplyThemeSegmentSelection();
            if (Settings.Appearance.IsShowHideControlButton)
            {
                BtnHideControl.Visibility = Visibility.Visible;
                ToggleSwitchShowButtonHideControl.IsOn = true;
            }
            else
            {
                BtnHideControl.Visibility = Visibility.Collapsed;
                ToggleSwitchShowButtonHideControl.IsOn = false;
            }
            if (Settings.Appearance.IsShowLRSwitchButton)
            {
                BtnSwitchSide.Visibility = Visibility.Visible;
                ToggleSwitchShowButtonLRSwitch.IsOn = true;
            }
            else
            {
                BtnSwitchSide.Visibility = Visibility.Collapsed;
                ToggleSwitchShowButtonLRSwitch.IsOn = false;
            }
            if (Settings.Appearance.IsShowModeFingerToggleSwitch)
            {
                StackPanelModeFinger.Visibility = Visibility.Visible;
                ToggleSwitchShowButtonModeFinger.IsOn = true;
            }
            else
            {
                StackPanelModeFinger.Visibility = Visibility.Collapsed;
                ToggleSwitchShowButtonModeFinger.IsOn = false;
            }
            // 开关状态必须从设置读回 UI，否则重启后开关显示"开"而实际背景不透明
            ToggleSwitchTransparentButtonBackground.IsOn = Settings.Appearance.IsTransparentButtonBackground;
            if (Settings.Appearance.IsTransparentButtonBackground)
            {
                BtnExit.Background = new SolidColorBrush(StringToColor("#7F909090"));
            }
            else
            {
                if (BtnSwitchTheme.Content.ToString() == "深色")
                {
                    //Light
                    BtnExit.Background = new SolidColorBrush(StringToColor("#FFCCCCCC"));
                }
                else
                {
                    //Dark
                    BtnExit.Background = new SolidColorBrush(StringToColor("#FF555555"));
                }
            }

            if (Settings.PowerPointSettings.PowerPointSupport)
            {
                ToggleSwitchSupportPowerPoint.IsOn = true;
                TryStartPptTimer();
            }
            else
            {
                ToggleSwitchSupportPowerPoint.IsOn = false;
                TryStopPptTimer();
            }
            if (Settings.PowerPointSettings.IsShowCanvasAtNewSlideShow)
            {
                ToggleSwitchShowCanvasAtNewSlideShow.IsOn = true;
            }
            else
            {
                ToggleSwitchShowCanvasAtNewSlideShow.IsOn = false;
            }

            if (Settings.Gesture == null)
            {
                Settings.Gesture = new Gesture();
            }
            if (Settings.Gesture.IsDisableLockSmithByDefault)
            {
                ToggleSwitchDisableLockSmithByDefault.IsOn = true;
                _lockSmith = false;
                LockSmithSymbol.Glyph = "Pin";
            }
            else
            {
                ToggleSwitchDisableLockSmithByDefault.IsOn = false;
                _lockSmith = true;
                LockSmithSymbol.Glyph = "UnPin";
            }
            if (Settings.Gesture.IsEnableTwoFingerZoom)
            {
                ToggleSwitchEnableTwoFingerZoom.IsOn = true;
            }
            else
            {
                ToggleSwitchEnableTwoFingerZoom.IsOn = false;
            }
            if (Settings.Gesture.IsEnableTwoFingerTranslate)
            {
                ToggleSwitchEnableTwoFingerTranslate.IsOn = true;
            }
            else
            {
                ToggleSwitchEnableTwoFingerTranslate.IsOn = false;
            }
            if (Settings.Gesture.IsEnableTwoFingerRotation)
            {
                ToggleSwitchEnableTwoFingerRotation.IsOn = true;
            }
            else
            {
                ToggleSwitchEnableTwoFingerRotation.IsOn = false;
            }
            if (Settings.Gesture.IsEnableTwoFingerRotationOnSelection)
            {
                ToggleSwitchEnableTwoFingerRotationOnSelection.IsOn = true;
            }
            else
            {
                ToggleSwitchEnableTwoFingerRotationOnSelection.IsOn = false;
            }
            if (Settings.PowerPointSettings.IsEnableTwoFingerGestureInPresentationMode)
            {
                ToggleSwitchEnableTwoFingerGestureInPresentationMode.IsOn = true;
            }
            else
            {
                ToggleSwitchEnableTwoFingerGestureInPresentationMode.IsOn = false;
            }
            if (Settings.PowerPointSettings.IsEnableFingerGestureSlideShowControl)
            {
                ToggleSwitchEnableFingerGestureSlideShowControl.IsOn = true;
            }
            else
            {
                ToggleSwitchEnableFingerGestureSlideShowControl.IsOn = false;
            }

            if (File.Exists(Environment.GetFolderPath(Environment.SpecialFolder.Startup) + "\\InkCanvas" + ".lnk"))
            {
                ToggleSwitchRunAtStartup.IsOn = true;
            }

            // 低配一体机 / Win7：先把档位套到设置上，后面读 ComboBox 时自然就是降级后的值
            PerformanceTuning.Detect();
            if (PerformanceTuning.Apply(Settings))
            {
                LogHelper.WriteLogToFile(
                    "性能档位：" + PerformanceTuning.ProfileName + "（" + PerformanceTuning.Details + "），" + PerformanceTuning.LastAction,
                    LogHelper.LogType.Event);
            }
            string smartState = Settings.Smart == null ? "未启用" :
                ("几何识别:" + (Settings.Smart.GeometryShapeEnabled ? "开" : "关")
                 + "｜笔迹平滑:" + (Settings.Smart.StrokeSmoothingEnabled ? "开" : "关")
                 + "｜教学工具:" + (Settings.Smart.TeachingToolsEnabled ? "开" : "关"));
            LogHelper.WriteLogToFile(
                "系统能力：" + OsSupport.Describe()
                + "｜DXGI 桌面复制：" + (OsSupport.SupportsDesktopDuplication ? "支持" : "回退 GDI 抓屏")
                + "｜HTTP.sys WebSocket：" + (OsSupport.SupportsHttpSysWebSocket ? "支持" : "使用兼容端口")
                + "｜窗口材质：" + (OsSupport.SupportsBackdropMaterial ? "支持" : "纯色背景")
                + "｜每显示器DPI：" + (OsSupport.SupportsPerMonitorV2Dpi ? "支持" : "进程级")
                + "｜智能画板[" + smartState + "]",
                LogHelper.LogType.Event);

            if (Settings.Canvas != null)
            {
                drawingAttributes.Height = Settings.Canvas.InkWidth;
                drawingAttributes.Width = Settings.Canvas.InkWidth;

                InkWidthSlider.Value = Settings.Canvas.InkWidth * 2;

                if (Settings.Canvas.IsShowCursor)
                {
                    ToggleSwitchShowCursor.IsOn = true;
                    painterCanvas.ForceCursor = true;
                }
                else
                {
                    ToggleSwitchShowCursor.IsOn = false;
                    painterCanvas.ForceCursor = false;
                }

                ComboBoxPenStyle.SelectedIndex = Settings.Canvas.InkStyle;

                ComboBoxEraserSize.SelectedIndex = Settings.Canvas.EraserSize;

                ComboBoxHyperbolaAsymptoteOption.SelectedIndex = (int)Settings.Canvas.HyperbolaAsymptoteOption;
            }
            else
            {
                Settings.Canvas = new Canvas();
            }

            if (Settings.Automation != null)
            {
                if (Settings.Automation.IsAutoClearWhenExitingWritingMode)
                {
                    ToggleSwitchClearExitingWritingMode.IsOn = true;
                }
                else
                {
                    ToggleSwitchClearExitingWritingMode.IsOn = false;
                }


                if (Settings.Automation.IsAutoSaveStrokesAtClear)
                {
                    ToggleSwitchAutoSaveStrokesAtClear.IsOn = true;
                }
                else
                {
                    ToggleSwitchAutoSaveStrokesAtClear.IsOn = false;
                }



                if (Settings.Automation.IsAutoSaveStrokesAtScreenshot)
                {
                    ToggleSwitchAutoSaveStrokesAtScreenshot.IsOn = true;
                }
                else
                {
                    ToggleSwitchAutoSaveStrokesAtScreenshot.IsOn = false;
                }
                // Toggled 处理器会在切换时改写相邻开关文案；启动时按设置恢复，避免文案与状态不符
                ToggleSwitchAutoSaveStrokesAtClear.Header =
                    Settings.Automation.IsAutoSaveStrokesAtScreenshot ? "清屏时自动截图并保存墨迹" : "清屏时自动截图";

                ToggleSwitchAutoKillPptService.IsOn = Settings.Automation.IsAutoKillPptService;
                ToggleSwitchAutoKillEasiNote.IsOn = Settings.Automation.IsAutoKillEasiNote;
                ApplySeewoAutoKillSettings();

                if (Settings.PowerPointSettings.IsAutoSaveStrokesInPowerPoint)
                {
                    ToggleSwitchAutoSaveStrokesInPowerPoint.IsOn = true;
                }
                else
                {
                    ToggleSwitchAutoSaveStrokesInPowerPoint.IsOn = false;
                }

                if (Settings.PowerPointSettings.IsNotifyPreviousPage)
                {
                    ToggleSwitchNotifyPreviousPage.IsOn = true;
                }
                else
                {
                    ToggleSwitchNotifyPreviousPage.IsOn = false;
                }

                if (Settings.PowerPointSettings.IsNotifyHiddenPage)
                {
                    ToggleSwitchNotifyHiddenPage.IsOn = true;
                }
                else
                {
                    ToggleSwitchNotifyHiddenPage.IsOn = false;
                }

                if (Settings.PowerPointSettings.IsNoClearStrokeOnSelectWhenInPowerPoint)
                {
                    ToggleSwitchNoStrokeClearInPowerPoint.IsOn = true;
                }
                else
                {
                    ToggleSwitchNoStrokeClearInPowerPoint.IsOn = false;
                }

                if (Settings.PowerPointSettings.IsShowStrokeOnSelectInPowerPoint)
                {
                    ToggleSwitchShowStrokeOnSelectInPowerPoint.IsOn = true;
                }
                else
                {
                    ToggleSwitchShowStrokeOnSelectInPowerPoint.IsOn = false;
                }

                if (Settings.PowerPointSettings.IsSupportWPS)
                {
                    ToggleSwitchSupportWPS.IsOn = true;
                }
                else
                {
                    ToggleSwitchSupportWPS.IsOn = false;
                }

                SideControlMinimumAutomationSlider.Value = Settings.Automation.MinimumAutomationStrokeNumber;

                if (Settings.Canvas.HideStrokeWhenSelecting)
                {
                    ToggleSwitchHideStrokeWhenSelecting.IsOn = true;
                }
                else
                {
                    ToggleSwitchHideStrokeWhenSelecting.IsOn = false;
                }

                if (Settings.Canvas.UsingWhiteboard)
                {
                    ToggleSwitchUsingWhiteboard.IsOn = true;
                }
                else
                {
                    ToggleSwitchUsingWhiteboard.IsOn = false;
                }
                if (Settings.Canvas.UsingWhiteboard)
                {
                    BtnSwitchTheme.Content = "深色";
                    BtnSwitchTheme_Click(null, null);
                }

                ToggleSwitchAutoPerformance.IsOn = Settings.Canvas.AutoPerformance;

                switch (Settings.Canvas.EraserType)
                {
                    case 1:
                        forcePointEraser = true;
                        break;
                    case 2:
                        forcePointEraser = false;
                        break;
                }

                ComboBoxEraserType.SelectedIndex = Settings.Canvas.EraserType;

                if (Settings.PowerPointSettings.IsAutoSaveScreenShotInPowerPoint)
                {
                    ToggleSwitchAutoSaveScreenShotInPowerPoint.IsOn = true;
                }
                else
                {
                    ToggleSwitchAutoSaveScreenShotInPowerPoint.IsOn = false;
                }
            }
            else
            {
                Settings.Automation = new Automation();
            }

            if (Settings.Advanced != null)
            {
                TouchMultiplierSlider.Value = Settings.Advanced.TouchMultiplier;
                EraserTouchThresholdSlider.Value = Settings.Advanced.EraserTouchThreshold;
                RefreshBoundsWidth();
                if (Settings.Advanced.IsLogEnabled)
                {
                    ToggleSwitchIsLogEnabled.IsOn = true;
                }
                else
                {
                    ToggleSwitchIsLogEnabled.IsOn = false;
                }
                if (Settings.Advanced.EraserBindTouchMultiplier)
                {
                    ToggleSwitchEraserBindTouchMultiplier.IsOn = true;
                }
                else
                {
                    ToggleSwitchEraserBindTouchMultiplier.IsOn = false;
                }
                ToggleSwitchIsEnableTouchAreaEraser.IsOn = Settings.Advanced.IsEnableTouchAreaEraser;

                if (Settings.Advanced.IsSpecialScreen)
                {
                    ToggleSwitchIsSpecialScreen.IsOn = true;
                }
                else
                {
                    ToggleSwitchIsSpecialScreen.IsOn = false;
                }
                TouchMultiplierSlider.Visibility = ToggleSwitchIsSpecialScreen.IsOn ? Visibility.Visible : Visibility.Collapsed;

                ToggleSwitchIsQuadIR.IsOn = Settings.Advanced.IsQuadIR;
            }
            else
            {
                Settings.Advanced = new Advanced();
            }

            if (Settings.InkToShape != null)
            {
                if (Settings.InkToShape.IsInkToShapeEnabled)
                {
                    ToggleSwitchEnableInkToShape.IsOn = true;
                }
                else
                {
                    ToggleSwitchEnableInkToShape.IsOn = false;
                }
            }
            else
            {
                Settings.InkToShape = new InkToShape();
            }
        }

        #endregion Definations and Loading

        #region Right Side Panel

        public static bool CloseIsFromButton = false;

        /// <summary>
        /// 合并成单一进程后，画板默认“关闭即缩回后台”，托盘/被控端继续运行；
        /// 只有显式退出（托盘“退出”、画板内退出按钮、-m 重启）时才置为 true 允许真正关闭。
        /// </summary>
        public static bool AllowRealClose = false;

        private void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            FlushSettings();
            // 画板内的退出按钮 = 退出整个应用（画板 + 远控模块 + 托盘）
            CloseIsFromButton = true;
            AllowRealClose = true;
            App.MarkUserInitiatedShutdown();
            Close();
            // App.ShutdownMode = OnExplicitShutdown：关闭最后一个窗口不会自动 Shutdown，
            // 少了这一句进程会残留（托盘/被控端仍在跑）而画板已被销毁，
            // 此后从托盘"开启画板"会在 Show() 上抛 InvalidOperationException。
            Application.Current.Shutdown();
        }

        private void BtnRestart_Click(object sender, RoutedEventArgs e)
        {
            FlushSettings();
            Process.Start(System.Windows.Forms.Application.ExecutablePath, "-m");

            CloseIsFromButton = true;
            AllowRealClose = true;
            App.MarkUserInitiatedShutdown();
            Application.Current.Shutdown();
        }

        // 用序号区分连点：原先快速连点会叠加多个挂起的延续，旧延续可能把面板
        // 重新隐藏/显示；同时把 150ms 淡入缩短，让开关「一点就有反应」。
        private int _settingsToggleSeq;
        private async void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            int seq = ++_settingsToggleSeq;
            bool show = !(BorderSettings.Tag as Visibility? == Visibility.Visible);
            BorderSettings.Tag = show ? Visibility.Visible : Visibility.Collapsed;
            BorderSettings.Visibility = Visibility.Visible;
            BorderSettings.BeginAnimation(OpacityProperty,
                new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 60 : 40)));
            await Task.Delay(show ? 70 : 50);
            if (seq != _settingsToggleSeq) return;   // 已被后续点击取代
            BorderSettings.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnThickness_Click(object sender, RoutedEventArgs e)
        {

        }

        bool forceEraser = false;

        private void BtnErase_Click(object sender, RoutedEventArgs e)
        {
            forceEraser = true;
            forcePointEraser = !forcePointEraser;
            switch (Settings.Canvas.EraserType)
            {
                case 1:
                    forcePointEraser = true;
                    break;
                case 2:
                    forcePointEraser = false;
                    break;
            }
            painterCanvas.EraserShape = GetEraserShape(forcePointEraser ? 50 : 5);
            painterCanvas.EditingMode =
                forcePointEraser ? InkCanvasEditingMode.EraseByPoint : InkCanvasEditingMode.EraseByStroke;
            drawingShapeMode = 0;
            GeometryDrawingEraser.Brush = forcePointEraser
                ? new SolidColorBrush(Color.FromRgb(0x23, 0xA9, 0xF2))
                : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            ImageEraser.Visibility = Visibility.Collapsed;
            painterCanvas_EditingModeChanged(painterCanvas, null);
            CancelSingleFingerDragMode();
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            forceEraser = false;
            PruneCircles();
            BorderClearInDelete.Visibility = Visibility.Collapsed;

            if (currentMode == 0)
            {
                BorderPenColorRed_MouseUp(BorderPenColorRed, null);
            }
            else
            {
                if (Settings.Canvas.UsingWhiteboard)
                {
                    BorderPenColorBlack_MouseUp(BorderPenColorBlack, null);
                }
                else
                {
                    BorderPenColorWhite_MouseUp(BorderPenColorWhite, null);
                }
            }
            if (painterCanvas.Strokes.Count != 0)
            {
                int whiteboardIndex = CurrentWhiteboardIndex;
                if (currentMode == 0)
                {
                    whiteboardIndex = 0;
                }
                strokeCollections[whiteboardIndex] = painterCanvas.Strokes.Clone();

            }

            ClearStrokes(false);
            painterCanvas.Children.Clear();

            CancelSingleFingerDragMode();
        }

        private void BtnClear_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            BtnHidePainterCanvas_Click(BtnHidePainterCanvas, null);
        }

        private void CancelSingleFingerDragMode()
        {
            if (ToggleSwitchDrawShapeBorderAutoHide.IsOn)
            {
                BorderDrawShape.Visibility = Visibility.Collapsed;
            }
            GridPainterCanvasSelectionCover.Visibility = Visibility.Collapsed;
            //Label.Content = "isSingleFingerDragMode=" + isSingleFingerDragMode.ToString();
            if (isSingleFingerDragMode)
            {
                BtnFingerDragMode_Click(BtnFingerDragMode, null);
            }
            isLongPressSelected = false;
        }

        private void BtnHideControl_Click(object sender, RoutedEventArgs e)
        {
            if (StackPanelControl.Visibility == Visibility.Visible)
            {
                StackPanelControl.Visibility = Visibility.Hidden;
            }
            else
            {
                StackPanelControl.Visibility = Visibility.Visible;
            }
        }

        int currentMode = 0;



        private void BtnSwitch_Click(object sender, RoutedEventArgs e)
        {
            if (Main_Grid.Background == Brushes.Transparent)
            {
                if (currentMode == 0)
                {
                    currentMode++;
                    GridBackgroundCover.Visibility = Visibility.Collapsed;

                    SaveStrokes(true);
                    ClearStrokes(true);
                    RestoreStrokes();

                    if (BtnSwitchTheme.Content.ToString() == "浅色")
                    {
                        BtnSwitch.Content = "黑板";
                        BtnExit.Foreground = Brushes.White;
                    }
                    else
                    {
                        BtnSwitch.Content = "白板";
                        if (isPresentationHaveBlackSpace)
                        {
                            BtnExit.Foreground = Brushes.White;
                            SymbolIconBtnColorBlackContent.Foreground = Brushes.White;
                            ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark;
                        }
                        else
                        {
                            BtnExit.Foreground = Brushes.Black;
                            SymbolIconBtnColorBlackContent.Foreground = Brushes.White;
                            ThemeManager.Current.ApplicationTheme = ApplicationTheme.Light;
                        }
                    }
                    StackPanelPPTButtons.Visibility = Visibility.Visible;
                }
                Topmost = true;
                BtnHidePainterCanvas_Click(BtnHidePainterCanvas, e);
            }
            else
            {
                switch ((++currentMode) % 2)
                {
                    case 0: //屏幕模式
                        currentMode = 0;
                        GridBackgroundCover.Visibility = Visibility.Collapsed;

                        SaveStrokes();
                        ClearStrokes(true);
                        RestoreStrokes(true);

                        if (BtnSwitchTheme.Content.ToString() == "浅色")
                        {
                            BtnSwitch.Content = "黑板";
                            BtnExit.Foreground = Brushes.White;
                            SymbolIconBtnColorBlackContent.Foreground = Brushes.Black;
                            ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark;
                        }
                        else
                        {
                            BtnSwitch.Content = "白板";
                            if (isPresentationHaveBlackSpace)
                            {
                                BtnExit.Foreground = Brushes.White;
                                SymbolIconBtnColorBlackContent.Foreground = Brushes.White;
                                ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark;
                            }
                            else
                            {
                                BtnExit.Foreground = Brushes.Black;
                                SymbolIconBtnColorBlackContent.Foreground = Brushes.White;
                                ThemeManager.Current.ApplicationTheme = ApplicationTheme.Light;
                            }
                        }

                        StackPanelPPTButtons.Visibility = Visibility.Visible;
                        Topmost = true;
                        break;
                    case 1: //黑板或白板模式
                        currentMode = 1;
                        GridBackgroundCover.Visibility = Visibility.Visible;

                        SaveStrokes(true);
                        ClearStrokes(true);
                        RestoreStrokes();

                        BtnSwitch.Content = "屏幕";
                        if (BtnSwitchTheme.Content.ToString() == "浅色")
                        {
                            BtnExit.Foreground = Brushes.White;
                            SymbolIconBtnColorBlackContent.Foreground = Brushes.Black;
                            ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark;
                        }
                        else
                        {
                            BtnExit.Foreground = Brushes.Black;
                            SymbolIconBtnColorBlackContent.Foreground = Brushes.White;
                            ThemeManager.Current.ApplicationTheme = ApplicationTheme.Light;
                        }

                        StackPanelPPTButtons.Visibility = Visibility.Collapsed;
                        Topmost = false;
                        break;
                }
            }

            // 切换屏幕 / 白板模式后，让图片背景层随之显示 / 隐藏
            ApplyWbPageImage();

        }

        private void BtnSwitchTheme_Click(object sender, RoutedEventArgs e)
        {
            if (BtnSwitchTheme.Content.ToString() == "深色")
            {
                BtnSwitchTheme.Content = "浅色";
                if (BtnSwitch.Content.ToString() != "屏幕")
                {
                    BtnSwitch.Content = "黑板";
                }
                BtnExit.Foreground = Brushes.White;
                GridBackgroundCover.Background = new SolidColorBrush(StringToColor("#FFF2F2F2"));
                ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark;
            }
            else
            {
                BtnSwitchTheme.Content = "深色";
                if (BtnSwitch.Content.ToString() != "屏幕")
                {
                    BtnSwitch.Content = "白板";
                }
                BtnExit.Foreground = Brushes.Black;
                GridBackgroundCover.Background = new SolidColorBrush(StringToColor("#FF1A1A1A"));
                ThemeManager.Current.ApplicationTheme = ApplicationTheme.Light;
            }
            SetColorByIndex();
            if (!Settings.Appearance.IsTransparentButtonBackground)
            {
                ToggleSwitchTransparentButtonBackground_Toggled(ToggleSwitchTransparentButtonBackground, null);
            }
        }
        private void SetColorByIndex()
        {
            if (currentMode != 0 || GridPainterCanvasSelectionCover.Visibility != Visibility.Collapsed)
                if (inkColor == 0)
                {
                    BtnColorBlack_Click(null, null);
                }
                else if (inkColor == 1)
                {
                    BtnColorRed_Click(null, null);
                }
                else if (inkColor == 2)
                {
                    BtnColorGreen_Click(null, null);
                }
                else if (inkColor == 3)
                {
                    BtnColorBlue_Click(null, null);
                }
                else if (inkColor == 4)
                {
                    BtnColorYellow_Click(null, null);
                }
                else if (inkColor == 5)
                {
                    BorderPenColorWhite_MouseUp(null, null);
                }
        }

        // 橡皮擦识别阈值：以设置里的接触面积阈值为基准，手指模式放宽到 3 倍。
        // 原实现写死 5/15，无法调整且默认偏小，笔尖按重一点就被判成手掌擦。
        int BoundsWidth = 12;
        private void RefreshBoundsWidth()
        {
            double t = Settings.Advanced.EraserTouchThreshold;
            if (t < 2) t = 2;
            BoundsWidth = (int)Math.Round(ToggleSwitchModeFinger.IsOn ? t * 3 : t);
        }
        private void ToggleSwitchModeFinger_Toggled(object sender, RoutedEventArgs e)
        {
            ToggleSwitchAutoEnterModeFinger.IsOn = ToggleSwitchModeFinger.IsOn;
            RefreshBoundsWidth();
        }

        private void BtnHidePainterCanvas_Click(object sender, RoutedEventArgs e)
        {
            if (Main_Grid.Background == Brushes.Transparent)
            {
                Main_Grid.Background = new SolidColorBrush(StringToColor("#01FFFFFF"));
                if (Settings.Canvas.HideStrokeWhenSelecting)
                {
                    painterCanvas.Visibility = Visibility.Visible;
                    painterCanvas.IsHitTestVisible = true;
                }
                else
                {
                    painterCanvas.IsHitTestVisible = true;
                    painterCanvas.Visibility = Visibility.Visible;
                }
                GridBackgroundCoverHolder.Visibility = Visibility.Visible;
                GridPainterCanvasSelectionCover.Visibility = Visibility.Collapsed;

                if (ImageEraserMask.Visibility == Visibility.Visible)
                    BtnColorRed_Click(sender, null);

                if (GridBackgroundCover.Visibility == Visibility.Collapsed)
                {
                    if (BtnSwitchTheme.Content.ToString() == "浅色")
                    {
                        BtnSwitch.Content = "黑板";
                    }
                    else
                    {
                        BtnSwitch.Content = "白板";
                    }
                    StackPanelPPTButtons.Visibility = Visibility.Visible;
                }
                else
                {
                    BtnSwitch.Content = "屏幕";
                    StackPanelPPTButtons.Visibility = Visibility.Collapsed;
                }

                BtnHidePainterCanvas.Content = "隐藏\n画板";
            }
            else
            {


                // Auto-clear Strokes
                // 很烦, 要重新来, 要等待截图完成再清理笔迹
                if (BtnPPTSlideShowEnd.Visibility != Visibility.Visible)
                {
                    if (isLoaded && Settings.Automation.IsAutoClearWhenExitingWritingMode)
                    {
                        if (painterCanvas.Strokes.Count > 0)
                        {
                            if (Settings.Automation.IsAutoSaveStrokesAtClear && painterCanvas.Strokes.Count >
                                Settings.Automation.MinimumAutomationStrokeNumber)
                            {
                                SaveScreenShot(true);
                            }

                            BtnClear_Click(BtnClear, null);
                        }
                    }
                    if (Settings.Canvas.HideStrokeWhenSelecting)
                        painterCanvas.Visibility = Visibility.Collapsed;
                    else
                    {
                        painterCanvas.IsHitTestVisible = false;
                        painterCanvas.Visibility = Visibility.Visible;
                    }
                }
                else
                {
                    if (isLoaded && Settings.Automation.IsAutoClearWhenExitingWritingMode && !Settings.PowerPointSettings.IsNoClearStrokeOnSelectWhenInPowerPoint)
                    {
                        if (painterCanvas.Strokes.Count > 0)
                        {
                            if (Settings.Automation.IsAutoSaveStrokesAtClear && painterCanvas.Strokes.Count >
                                Settings.Automation.MinimumAutomationStrokeNumber)
                            {
                                SaveScreenShot(true);
                            }

                            BtnClear_Click(BtnClear, null);
                        }
                    }


                    if (Settings.PowerPointSettings.IsShowStrokeOnSelectInPowerPoint)
                    {
                        painterCanvas.Visibility = Visibility.Visible;
                        painterCanvas.IsHitTestVisible = true;
                    }
                    else
                    {
                        if (Settings.Canvas.HideStrokeWhenSelecting)
                            painterCanvas.Visibility = Visibility.Collapsed;
                        else
                        {
                            painterCanvas.IsHitTestVisible = false;
                            painterCanvas.Visibility = Visibility.Visible;
                        }
                    }
                }



                Main_Grid.Background = Brushes.Transparent;


                GridBackgroundCoverHolder.Visibility = Visibility.Collapsed;
                if (currentMode != 0)
                {
                    SaveStrokes();
                    RestoreStrokes(true);
                }

                if (BtnSwitchTheme.Content.ToString() == "浅色")
                {
                    BtnSwitch.Content = "黑板";
                }
                else
                {
                    BtnSwitch.Content = "白板";
                }

                StackPanelPPTButtons.Visibility = Visibility.Visible;
                BtnHidePainterCanvas.Content = "显示\n画板";
            }

            if (Main_Grid.Background == Brushes.Transparent)
            {
                StackPanelCanvasControls.Visibility = Visibility.Collapsed;
                StackPanelCanvacMain.Visibility = Visibility.Visible;
            }
            else
            {
                StackPanelCanvasControls.Visibility = Visibility.Visible;
                StackPanelCanvacMain.Visibility = Visibility.Collapsed;
            }

            // 隐藏 / 显示画板（鼠标模式）后，同步图片背景可见性
            ApplyWbPageImage();

        }

        private void BtnSwitchSide_Click(object sender, RoutedEventArgs e)
        {
            if (ViewBoxStackPanelMain.HorizontalAlignment == HorizontalAlignment.Right)
            {
                ViewBoxStackPanelMain.HorizontalAlignment = HorizontalAlignment.Left;
                ViewBoxStackPanelShapes.HorizontalAlignment = HorizontalAlignment.Right;
            }
            else
            {
                ViewBoxStackPanelMain.HorizontalAlignment = HorizontalAlignment.Right;
                ViewBoxStackPanelShapes.HorizontalAlignment = HorizontalAlignment.Left;
            }
        }


        private void StackPanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (((StackPanel)sender).Visibility == Visibility.Visible)
            {
                GridForLeftSideReservedSpace.Visibility = Visibility.Collapsed;
            }
            else
            {
                GridForLeftSideReservedSpace.Visibility = Visibility.Visible;
            }
        }

        #endregion

        #region Right Side Panel (Buttons - Color)

        int inkColor = 1;

        /// <summary>
        /// 颜色选中提示：直接设 Opacity，并跳过已是目标值的元素。
        /// 这 6 个 Viewbox 位于 ViewBoxStackPanelMain 的 BitmapCache 子树内，
        /// 用 DoubleAnimation 会让整块缓存在动画期间每帧重建（一次点击约 9 帧），
        /// 是「点颜色 / 切工具掉帧」的主因。直接赋值只触发一次失效，重复点击则完全不失效。
        /// selected &lt; 0 表示全部隐藏（选择工具 / 橡皮）。
        /// </summary>
        private void SetColorSelection(int selected)
        {
            UIElement[] targets = {
                ViewboxBtnColorBlackContent, ViewboxBtnColorRedContent, ViewboxBtnColorGreenContent,
                ViewboxBtnColorBlueContent, ViewboxBtnColorYellowContent, ViewboxBtnColorWhiteContent
            };
            for (int i = 0; i < targets.Length; i++)
            {
                double want = (i == selected) ? 1.0 : 0.0;
                UIElement el = targets[i];
                if (Math.Abs(el.Opacity - want) < 0.001) continue;
                el.BeginAnimation(OpacityProperty, null);   // 清除可能残留的动画，确保下面的赋值生效
                el.Opacity = want;
            }
        }

        private void ColorSwitchCheck()
        {
            //EraserContainer.Background = null;
            ImageEraser.Visibility = Visibility.Visible;
            if (Main_Grid.Background == Brushes.Transparent)
            {
                if (currentMode == 1)
                {
                    currentMode = 0;
                    GridBackgroundCover.Visibility = Visibility.Collapsed;
                }
                BtnHidePainterCanvas_Click(BtnHidePainterCanvas, null);
            }

            StrokeCollection strokes = painterCanvas.GetSelectedStrokes();
            if (strokes.Count != 0)
            {
                foreach (Stroke stroke in strokes)
                {
                    try
                    {
                        stroke.DrawingAttributes.Color = painterCanvas.DefaultDrawingAttributes.Color;
                    }
                    catch { }
                }
            }
            if (DrawingAttributesHistory.Count > 0)
            {
                timeMachine.CommitStrokeDrawingAttributesHistory(DrawingAttributesHistory);
                DrawingAttributesHistory = new Dictionary<Stroke, Tuple<DrawingAttributes, DrawingAttributes>>();
                foreach (var item in DrawingAttributesHistoryFlag)
                {
                    item.Value.Clear();
                }
            }
            else
            {
                painterCanvas.IsManipulationEnabled = true;
                drawingShapeMode = 0;
                painterCanvas.EditingMode = InkCanvasEditingMode.Ink;
                CancelSingleFingerDragMode();
                forceEraser = false;

                // 改变选中提示（一次状态切换，缓存只失效一次）
                SetColorSelection(inkColor);
            }

            isLongPressSelected = false;
        }

        private void BtnColorBlack_Click(object sender, RoutedEventArgs e)
        {
            inkColor = 0;
            forceEraser = false;
            painterCanvas.DefaultDrawingAttributes.Color = Colors.Black;

            ColorSwitchCheck();
        }

        private void BtnColorRed_Click(object sender, RoutedEventArgs e)
        {
            inkColor = 1;
            forceEraser = false;
            painterCanvas.DefaultDrawingAttributes.Color = ((SolidColorBrush)BtnColorRed.Background).Color;
            ColorSwitchCheck();
        }

        private void BtnColorGreen_Click(object sender, RoutedEventArgs e)
        {
            inkColor = 2;
            forceEraser = false;
            painterCanvas.DefaultDrawingAttributes.Color = ((SolidColorBrush)BtnColorGreen.Background).Color;
            ColorSwitchCheck();
        }

        private void BtnColorBlue_Click(object sender, RoutedEventArgs e)
        {
            inkColor = 3;
            forceEraser = false;
            painterCanvas.DefaultDrawingAttributes.Color = ((SolidColorBrush)BtnColorBlue.Background).Color;
            ColorSwitchCheck();
        }

        private void BtnColorYellow_Click(object sender, RoutedEventArgs e)
        {
            inkColor = 4;
            forceEraser = false;
            painterCanvas.DefaultDrawingAttributes.Color = ((SolidColorBrush)BtnColorYellow.Background).Color;
            ColorSwitchCheck();
        }

        private Color StringToColor(string colorStr)
        {
            Byte[] argb = new Byte[4];
            for (int i = 0; i < 4; i++)
            {
                char[] charArray = colorStr.Substring(i * 2 + 1, 2).ToCharArray();
                //string str = "11";
                Byte b1 = toByte(charArray[0]);
                Byte b2 = toByte(charArray[1]);
                argb[i] = (Byte)(b2 | (b1 << 4));
            }
            return Color.FromArgb(argb[0], argb[1], argb[2], argb[3]);//#FFFFFFFF
        }

        private static byte toByte(char c)
        {
            byte b = (byte)"0123456789ABCDEF".IndexOf(c);
            return b;
        }

        #endregion

        #region Touch Events

        #region Multi-Touch

        bool isInMultiTouchMode = false;
        private void BorderMultiTouchMode_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (isInMultiTouchMode)
            {
                painterCanvas.StylusDown -= MainWindow_StylusDown;
                painterCanvas.StylusMove -= MainWindow_StylusMove;
                painterCanvas.StylusUp -= MainWindow_StylusUp;
                painterCanvas.TouchDown -= MainWindow_TouchDown;
                painterCanvas.TouchDown += Main_Grid_TouchDown;
                painterCanvas.EditingMode = InkCanvasEditingMode.Ink;
                painterCanvas.Children.Clear();
                isInMultiTouchMode = false;
                SymbolIconMultiTouchMode.Glyph = "People";
            }
            else
            {
                painterCanvas.StylusDown += MainWindow_StylusDown;
                painterCanvas.StylusMove += MainWindow_StylusMove;
                painterCanvas.StylusUp += MainWindow_StylusUp;
                painterCanvas.TouchDown -= Main_Grid_TouchDown;
                painterCanvas.TouchDown += MainWindow_TouchDown;
                painterCanvas.EditingMode = InkCanvasEditingMode.None;
                painterCanvas.Children.Clear();
                isInMultiTouchMode = true;
                SymbolIconMultiTouchMode.Glyph = "Contact";
            }
        }

        private void MainWindow_TouchDown(object sender, TouchEventArgs e)
        {
            // 多指模式原先直接读 Bounds.Width，绕开了「特殊屏幕 / 四边红外 / 触摸倍数」
            // 校正，导致希沃大屏上多指书写依旧把笔尖误判成橡皮。改用统一入口。
            double boundWidth = GetTouchBoundWidth(e);
            // 原实现写死 20，与单指模式的 BoundsWidth 无联动；现在统一由接触面积阈值派生。
            // 「识别触摸面积为橡皮擦」默认关闭：关闭时多指/触摸模式不再因接触面积大而自动切橡皮。
            if (Settings.Advanced.IsEnableTouchAreaEraser && boundWidth > BoundsWidth * 4)
            {
                painterCanvas.EraserShape = GetEraserShape(boundWidth);
                TouchDownPointsList[e.TouchDevice.Id] = InkCanvasEditingMode.EraseByPoint;
                painterCanvas.EditingMode = InkCanvasEditingMode.EraseByPoint;
            }
            else
            {
                TouchDownPointsList[e.TouchDevice.Id] = InkCanvasEditingMode.None;
                painterCanvas.EditingMode = InkCanvasEditingMode.None;
            }
        }

        private void MainWindow_StylusDown(object sender, StylusDownEventArgs e)
        {
            TouchDownPointsList[e.StylusDevice.Id] = InkCanvasEditingMode.None;
        }

        private void MainWindow_StylusUp(object sender, StylusEventArgs e)
        {
            int id = e.StylusDevice.Id;
            try
            {
                // 手指点按、橡皮抬手都没有 Move 包，也就没有 Stroke。原先无条件调
                // GetStrokeVisual 会现造一个空可视层再扔掉，还会让 Strokes.Add(null)
                // 抛异常——异常又被写进隐藏的 Label，这一笔就静默丢了。
                if (StrokeVisualList.TryGetValue(id, out var strokeVisual))
                {
                    var visualCanvas = GetVisualCanvas(id);
                    if (visualCanvas != null) painterCanvas.Children.Remove(visualCanvas);

                    if (strokeVisual.Stroke != null)
                    {
                        painterCanvas.Strokes.Add(strokeVisual.Stroke);
                        painterCanvas_StrokeCollected(painterCanvas, new InkCanvasStrokeCollectedEventArgs(strokeVisual.Stroke));
                    }
                }
            }
            catch (Exception ex)
            {
                LogMultiTouchError("提交笔迹", ex);
            }
            try
            {
                StrokeVisualList.Remove(id);
                VisualCanvasList.Remove(id);
                TouchDownPointsList.Remove(id);
                ClearMultiTouchVisualsIfIdle();
            }
            catch { }
        }

        private void MainWindow_StylusMove(object sender, StylusEventArgs e)
        {
            try
            {
                if (GetTouchDownPointsList(e.StylusDevice.Id) != InkCanvasEditingMode.None) return;
                try
                {
                    if (e.StylusDevice.StylusButtons[1].StylusButtonState == StylusButtonState.Down) return;
                }
                catch { }
                var strokeVisual = GetStrokeVisual(e.StylusDevice.Id);
                var stylusPointCollection = e.GetStylusPoints(this);
                foreach (var stylusPoint in stylusPointCollection)
                {
                    strokeVisual.Add(new StylusPoint(stylusPoint.X, stylusPoint.Y, stylusPoint.PressureFactor));
                }

                strokeVisual.Redraw();
            }
            catch (Exception ex)
            {
                LogMultiTouchError("绘制笔迹", ex);
            }
        }

        /// <summary>
        /// 所有笔/手指都抬起后才清理画板上的临时可视层。
        /// 必须三个字典同时为空：任意一个为空就清会把另一支笔正在写的墨迹一起删掉。
        /// </summary>
        private void ClearMultiTouchVisualsIfIdle()
        {
            if (!isInMultiTouchMode) return;
            if (StrokeVisualList.Count != 0 || VisualCanvasList.Count != 0 || TouchDownPointsList.Count != 0) return;
            painterCanvas.Children.Clear();
        }

        private DateTime _lastMultiTouchErrorLogUtc = DateTime.MinValue;

        /// <summary>
        /// 书写热路径上的异常会被高频触发（每一包触摸点都可能失败），
        /// 全量写日志反而会加重卡顿，这里 5 秒内只记录一次。
        /// </summary>
        private void LogMultiTouchError(string action, Exception ex)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastMultiTouchErrorLogUtc).TotalSeconds < 5) return;
            _lastMultiTouchErrorLogUtc = now;
            LogHelper.NewLog("多指书写" + action + "失败: " + ex);
        }

        private StrokeVisual GetStrokeVisual(int id)
        {
            if (StrokeVisualList.TryGetValue(id, out var visual))
            {
                return visual;
            }

            var strokeVisual = new StrokeVisual(painterCanvas.DefaultDrawingAttributes.Clone());
            StrokeVisualList[id] = strokeVisual;
            var visualCanvas = new VisualCanvas(strokeVisual);
            VisualCanvasList[id] = visualCanvas;
            painterCanvas.Children.Add(visualCanvas);

            return strokeVisual;
        }

        private VisualCanvas GetVisualCanvas(int id)
        {
            if (VisualCanvasList.TryGetValue(id, out var visualCanvas))
            {
                return visualCanvas;
            }
            return null;
        }

        private InkCanvasEditingMode GetTouchDownPointsList(int id)
        {
            if (TouchDownPointsList.TryGetValue(id, out var painterCanvasEditingMode))
            {
                return painterCanvasEditingMode;
            }
            return painterCanvas.EditingMode;
        }

        private Dictionary<int, InkCanvasEditingMode> TouchDownPointsList { get; } = new Dictionary<int, InkCanvasEditingMode>();
        private Dictionary<int, StrokeVisual> StrokeVisualList { get; } = new Dictionary<int, StrokeVisual>();
        private Dictionary<int, VisualCanvas> VisualCanvasList { get; } = new Dictionary<int, VisualCanvas>();

        #endregion

        int lastTouchDownTime = 0, lastTouchUpTime = 0;

        Point iniP = new Point(0, 0);
        bool isLastTouchEraser = false;
        private bool forcePointEraser = true;
        private bool _lockSmith = false; //临时停用双指手势

        // 橡皮形状按直径缓存。每次 new EllipseStylusShape 再赋给 EraserShape 都会让 InkCanvas
        // 重建橡皮的命中几何并刷新墨迹层——触摸屏上手指频繁按下时这是可见的掉帧来源。
        // StylusShape 不可变，同尺寸复用同一实例即可（DP 写入同引用时不会触发变更通知）。
        private readonly Dictionary<double, EllipseStylusShape> _eraserShapeCache = new Dictionary<double, EllipseStylusShape>();

        private EllipseStylusShape GetEraserShape(double diameter)
        {
            diameter = Math.Round(diameter, 1);
            if (diameter < 1) diameter = 1;
            if (!_eraserShapeCache.TryGetValue(diameter, out var shape))
            {
                if (_eraserShapeCache.Count >= 24) _eraserShapeCache.Clear();
                shape = new EllipseStylusShape(diameter, diameter);
                _eraserShapeCache[diameter] = shape;
            }
            return shape;
        }

        private void Main_Grid_TouchDown(object sender, TouchEventArgs e)
        {
            BorderClearInDelete.Visibility = Visibility.Collapsed;
            if (ToggleSwitchDrawShapeBorderAutoHide.IsOn)
            {
                BorderDrawShape.Visibility = Visibility.Collapsed;
            }

            if (NeedUpdateIniP())
            {
                iniP = e.GetTouchPoint(painterCanvas).Position;
            }
            if (drawingShapeMode == 9 && isFirstTouchCuboid == false)
            {
                MouseTouchMove(iniP);
            }
            painterCanvas.Opacity = 1;
            double boundsWidth = GetTouchBoundWidth(e);
            var eraserMultiplier = 1d;
            if (!Settings.Advanced.EraserBindTouchMultiplier && Settings.Advanced.IsSpecialScreen)
            {
                double tm = Settings.Advanced.TouchMultiplier;
                // 「触摸大小倍数」滑块下界是 0，直接取倒数会得到 Infinity，
                // 传给 EllipseStylusShape 会抛异常并中断落笔；这里做下界保护。
                if (tm > 0.01) eraserMultiplier = 1 / tm;
            }
            // 「识别触摸面积为橡皮擦」默认关闭：关闭时接触面积再大也只按画笔处理，
            // 只有橡皮按钮 / 笔尾按键（forceEraser）才会真正擦除。
            if (Settings.Advanced.IsEnableTouchAreaEraser && boundsWidth > BoundsWidth)
            {
                isLastTouchEraser = true;
                if (drawingShapeMode == 0 && forceEraser) return;
                if (boundsWidth > BoundsWidth * 2.5)
                {
                    double k = 1;
                    switch (Settings.Canvas.EraserSize)
                    {
                        case 0:
                            k = 0.5;
                            break;
                        case 1:
                            k = 0.8;
                            break;
                        case 3:
                            k = 1.25;
                            break;
                        case 4:
                            k = 1.8;
                            break;
                    }
                    painterCanvas.EraserShape = GetEraserShape(boundsWidth * 1.5 * k * eraserMultiplier);
                    painterCanvas.EditingMode = InkCanvasEditingMode.EraseByPoint;
                }
                else
                {
                    if (StackPanelPPTControls.Visibility == Visibility.Visible && painterCanvas.Strokes.Count == 0 && Settings.PowerPointSettings.IsEnableFingerGestureSlideShowControl)
                    {
                        isLastTouchEraser = false;
                        painterCanvas.EditingMode = InkCanvasEditingMode.GestureOnly;
                        painterCanvas.Opacity = 0.1;
                    }
                    else
                    {
                        //painterCanvas.EraserShape = new RectangleStylusShape(8, 8); //old old
                        //painterCanvas.EraserShape = forcePointEraser ? new EllipseStylusShape(50, 50) : new EllipseStylusShape(5, 5); //last
                        //painterCanvas.EraserShape = new EllipseStylusShape(boundsWidth * 1.5, boundsWidth * 1.5); //old old
                        //painterCanvas.EditingMode = forcePointEraser ? InkCanvasEditingMode.EraseByPoint : InkCanvasEditingMode.EraseByStroke; //last
                        painterCanvas.EraserShape = GetEraserShape(5);
                        painterCanvas.EditingMode = InkCanvasEditingMode.EraseByStroke;
                    }
                }
            }
            else
            {
                isLastTouchEraser = false;
                painterCanvas.EraserShape = GetEraserShape(forcePointEraser ? 50 : 5);
                if (forceEraser) return;
                painterCanvas.EditingMode = InkCanvasEditingMode.Ink;
            }
        }

        public double GetTouchBoundWidth(TouchEventArgs e)
        {
            var args = e.GetTouchPoint(null).Bounds;
            double value;
            if (!Settings.Advanced.IsQuadIR) value = args.Width;
            else value = Math.Sqrt(args.Width * args.Height); //四边红外
            if (Settings.Advanced.IsSpecialScreen) value *= Settings.Advanced.TouchMultiplier;
            return value;
        }

        //记录触摸设备ID
        private List<int> dec = new List<int>();
        //中心点
        System.Windows.Point centerPoint;
        InkCanvasEditingMode lastPainterCanvasEditingMode = InkCanvasEditingMode.Ink;
        bool isSingleFingerDragMode = false;

        // 双指 / 单指拖拽手势：手势期间只累加矩阵并整体套 painterCanvas.RenderTransform（不再每帧逐条 Transform 全部笔画），
        // 手势结束再一次性 bake 到笔画点，避免“笔画多时每帧 O(笔画×点数)”的卡顿。
        private Matrix _gestureMatrix = Matrix.Identity;
        private double _gestureScaleX = 1, _gestureScaleY = 1;
        private bool _gestureActive;
        // 手势期间复用同一个 MatrixTransform：每帧 new 一个会让 InkCanvas 反复摘挂 Freezable，白板笔画多时更明显
        private readonly MatrixTransform _gestureRenderTransform = new MatrixTransform(Matrix.Identity);

        //防止衣服误触造成的墨迹消失

        /// <summary>触摸被系统取消/丢失捕获时清理按下状态，避免 dec 残留导致手势历史不提交、单指被误判为多指。</summary>
        private void RemoveTouchState(int touchId)
        {
            dec.Remove(touchId);
            TouchDownPointsList.Remove(touchId);
            if (dec.Count == 0) isWaitUntilNextTouchDown = false;
        }

        private void painterCanvas_PreviewTouchDown(object sender, TouchEventArgs e)
        {
            dec.Add(e.TouchDevice.Id);
            //设备1个的时候，记录中心点
            if (dec.Count == 1)
            {
                TouchPoint touchPoint = e.GetTouchPoint(painterCanvas);
                centerPoint = touchPoint.Position;

                //记录第一根手指点击时的笔画数（只记数量，避免整表 Clone 的 O(n) 拷贝）
                _touchDownStrokeCount = painterCanvas.Strokes.Count;
            }
            //设备两个及两个以上，将画笔功能关闭
            if (dec.Count > 1 || isSingleFingerDragMode || !Settings.Gesture.IsEnableTwoFingerGesture)
            {
                if (isInMultiTouchMode || !Settings.Gesture.IsEnableTwoFingerGesture) return;
                if (painterCanvas.EditingMode != InkCanvasEditingMode.None && painterCanvas.EditingMode != InkCanvasEditingMode.Select)
                {
                    lastPainterCanvasEditingMode = painterCanvas.EditingMode;
                    painterCanvas.EditingMode = InkCanvasEditingMode.None;
                }
            }
        }

        private void painterCanvas_PreviewTouchUp(object sender, TouchEventArgs e)
        {
            //手势完成后切回之前的状态
            if (dec.Count > 1)
            {
                if (painterCanvas.EditingMode == InkCanvasEditingMode.None)
                {
                    painterCanvas.EditingMode = lastPainterCanvasEditingMode;
                }
            }
            dec.Remove(e.TouchDevice.Id);
            // 与 MainWindow_TouchDown 写入的键保持一致：触摸设备 ID 与笔设备 ID 不是同一套编号，
            // 只在这里移除触摸键，否则多指模式下每根手指都会在字典里留下一条永不释放的记录。
            TouchDownPointsList.Remove(e.TouchDevice.Id);
            ClearMultiTouchVisualsIfIdle();
            painterCanvas.Opacity = 1;
            if (dec.Count == 0)
            {
                if (_touchDownStrokeCount != painterCanvas.Strokes.Count &&
                    !(drawingShapeMode == 9 && !isFirstTouchCuboid))
                {
                    int whiteboardIndex = CurrentWhiteboardIndex;
                    if (currentMode == 0)
                    {
                        whiteboardIndex = 0;
                    }
                    strokeCollections[whiteboardIndex] = lastTouchDownStrokeCollection;
                }
            }
        }
        private void painterCanvas_ManipulationStarting(object sender, ManipulationStartingEventArgs e)
        {
            e.Mode = ManipulationModes.All;
        }

        private void painterCanvas_ManipulationInertiaStarting(object sender, ManipulationInertiaStartingEventArgs e)
        {

        }

        private void Main_Grid_ManipulationCompleted(object sender, ManipulationCompletedEventArgs e)
        {
            if (e.Manipulators.Count() == 0)
            {
                BakeGestureTransform();   // 把手势期间累积的 RenderTransform 一次性 bake 进笔画点并复位
                if (forceEraser) return;
                painterCanvas.EditingMode = InkCanvasEditingMode.Ink;
            }
        }

        /// <summary>
        /// 把笔迹层变换复位成默认值。必须用 Transform.Identity 而不是 null：
        /// WPF 的笔迹游标（MS.Internal.Ink.EditingBehavior.GetElementTransformMatrix）
        /// 直接解引用 LayoutTransform / RenderTransform，置 null 后每次鼠标移动都会
        /// 在 InkCanvas._OnQueryCursor 里抛 NullReferenceException。
        /// </summary>
        private void ResetPainterCanvasTransform()
        {
            painterCanvas.RenderTransform = Transform.Identity;
        }

        /// <summary>手势结束：把累积的 RenderTransform 一次性 bake 进笔画点（含粗细缩放与压感圆更新），再复位。</summary>
        private void BakeGestureTransform()
        {
            if (!_gestureActive) return;   // 白板手势未发生（如图片态由 ShowRender 管理 RenderTransform），不要动
            _gestureActive = false;

            var m = _gestureMatrix;
            bool hasScale = Settings.Gesture.IsEnableTwoFingerZoom;
            StrokeCollection selected = painterCanvas.GetSelectedStrokes();
            if (!m.IsIdentity)
            {
                if (selected.Count != 0)
                {
                    foreach (Stroke stroke in selected) GestureTransformOne(stroke, m, hasScale);
                }
                else
                {
                    foreach (Stroke stroke in painterCanvas.Strokes) GestureTransformOne(stroke, m, hasScale);
                }
            }

            ResetPainterCanvasTransform();
            _gestureRenderTransform.Matrix = Matrix.Identity;
            _gestureMatrix = Matrix.Identity;
            _gestureScaleX = 1;
            _gestureScaleY = 1;
        }

        private void GestureTransformOne(Stroke stroke, Matrix m, bool hasScale)
        {
            stroke.Transform(m, false);
            if (hasScale)
            {
                try
                {
                    stroke.DrawingAttributes.Width *= _gestureScaleX;
                    stroke.DrawingAttributes.Height *= _gestureScaleY;
                }
                catch { }
            }
            foreach (Circle circle in circles)
            {
                if (stroke == circle.Stroke)
                {
                    circle.R = GetDistance(circle.Stroke.StylusPoints[0].ToPoint(), circle.Stroke.StylusPoints[circle.Stroke.StylusPoints.Count / 2].ToPoint()) / 2;
                    circle.Centroid = new Point((circle.Stroke.StylusPoints[0].X + circle.Stroke.StylusPoints[circle.Stroke.StylusPoints.Count / 2].X) / 2,
                                                (circle.Stroke.StylusPoints[0].Y + circle.Stroke.StylusPoints[circle.Stroke.StylusPoints.Count / 2].Y) / 2);
                    break;
                }
            }
        }

        private void Main_Grid_ManipulationDelta(object sender, ManipulationDeltaEventArgs e)
        {
            if (isInMultiTouchMode || (!Settings.Gesture.IsEnableTwoFingerGesture && !IsWbImagePage) || _lockSmith) return;
            if ((dec.Count >= 2 && (Settings.PowerPointSettings.IsEnableTwoFingerGestureInPresentationMode || StackPanelPPTControls.Visibility != Visibility.Visible || StackPanelPPTButtons.Visibility == Visibility.Collapsed || IsWbImagePage)) || isSingleFingerDragMode)
            {
                // 放映模式：把双指手势转接给图片视图（平移 / 以双指中心为锚点缩放），复用画板原有手势
                if (IsWbImagePage) { ShowApplyManipulation(e); return; }

                ManipulationDelta md = e.DeltaManipulation;
                Vector trans = md.Translation;  // 获得位移矢量
                double rotate = md.Rotation;  // 获得旋转角度
                Vector scale = md.Scale;  // 获得缩放倍数

                Matrix m = new Matrix();

                // Find center of element and then transform to get current location of center
                FrameworkElement fe = e.Source as FrameworkElement;
                Point center = new Point(fe.ActualWidth / 2, fe.ActualHeight / 2);
                center = m.Transform(center);  // 转换为矩阵缩放和旋转的中心点

                // Update matrix to reflect translation/rotation
                if (Settings.Gesture.IsEnableTwoFingerTranslate)
                    m.Translate(trans.X, trans.Y);  // 移动
                if (Settings.Gesture.IsEnableTwoFingerRotation)
                    m.RotateAt(rotate, center.X, center.Y);  // 旋转
                if (Settings.Gesture.IsEnableTwoFingerZoom)
                    m.ScaleAt(scale.X, scale.Y, center.X, center.Y);  // 缩放

                StrokeCollection strokes = painterCanvas.GetSelectedStrokes();
                if (strokes.Count != 0)
                {
                    foreach (Stroke stroke in strokes)
                    {
                        stroke.Transform(m, false);

                        foreach (Circle circle in circles)
                        {
                            if (stroke == circle.Stroke)
                            {
                                circle.R = GetDistance(circle.Stroke.StylusPoints[0].ToPoint(), circle.Stroke.StylusPoints[circle.Stroke.StylusPoints.Count / 2].ToPoint()) / 2;
                                circle.Centroid = new Point((circle.Stroke.StylusPoints[0].X + circle.Stroke.StylusPoints[circle.Stroke.StylusPoints.Count / 2].X) / 2,
                                                            (circle.Stroke.StylusPoints[0].Y + circle.Stroke.StylusPoints[circle.Stroke.StylusPoints.Count / 2].Y) / 2);
                                break;
                            }
                        }

                        if (Settings.Gesture.IsEnableTwoFingerZoom)
                        {
                            try
                            {
                                stroke.DrawingAttributes.Width *= md.Scale.X;
                                stroke.DrawingAttributes.Height *= md.Scale.Y;
                            }
                            catch { }
                        }
                    }
                }
                else
                {
                    // 无选中笔画：把本帧增量累加进手势矩阵，整体套一次 RenderTransform（O(1)），不再逐条 Transform 全部笔画
                    _gestureActive = true;
                    _gestureMatrix = m * _gestureMatrix;   // 预乘，等价于“逐帧依次套用”
                    if (Settings.Gesture.IsEnableTwoFingerZoom && md.Scale.X > 0 && md.Scale.Y > 0)
                    {
                        _gestureScaleX *= md.Scale.X;
                        _gestureScaleY *= md.Scale.Y;
                    }
                    _gestureRenderTransform.Matrix = _gestureMatrix;
                    if (!ReferenceEquals(painterCanvas.RenderTransform, _gestureRenderTransform))
                    {
                        painterCanvas.RenderTransform = _gestureRenderTransform;
                    }
                }
            }
        }

        #endregion Touch Events

        #region PowerPoint

        public static Microsoft.Office.Interop.PowerPoint.Application pptApplication = null;
        public static Microsoft.Office.Interop.PowerPoint.Presentation presentation = null;
        public static Microsoft.Office.Interop.PowerPoint.Slides slides = null;
        public static Microsoft.Office.Interop.PowerPoint.Slide slide = null;
        public static int slidescount = 0;
        private void BtnCheckPPT_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 覆盖旧 RCW 前先退订并释放，避免重复点击按钮累积 COM 事件订阅/RCW。
                ReleaseOldPptApplication();
                pptApplication = (Microsoft.Office.Interop.PowerPoint.Application)Marshal.GetActiveObject("PowerPoint.Application");
                //pptApplication.SlideShowWindows[1].View.Next();
                if (pptApplication != null)
                {
                    //获得演示文稿对象
                    presentation = pptApplication.ActivePresentation;
                    // 已在 ReleaseOldPptApplication 中对旧对象退订；这里对新对象只订阅一次。
                    pptApplication.PresentationClose += PptApplication_PresentationClose;
                    pptApplication.SlideShowBegin += PptApplication_SlideShowBegin;
                    pptApplication.SlideShowNextSlide += PptApplication_SlideShowNextSlide;
                    pptApplication.SlideShowEnd += PptApplication_SlideShowEnd;
                    // 获得幻灯片对象集合
                    slides = presentation.Slides;
                    // 获得幻灯片的数量
                    slidescount = slides.Count;
                    ReinitMemoryStreams(slidescount + 2);
                    // 获得当前选中的幻灯片
                    try
                    {
                        // 在普通视图下这种方式可以获得当前选中的幻灯片对象
                        // 然而在阅读模式下，这种方式会出现异常
                        slide = slides[pptApplication.ActiveWindow.Selection.SlideRange.SlideNumber];
                    }
                    catch
                    {
                        // 在阅读模式下出现异常时，通过下面的方式来获得当前选中的幻灯片对象
                        slide = pptApplication.SlideShowWindows[1].View.Slide;
                    }
                }

                if (pptApplication == null) throw new Exception();
                //BtnCheckPPT.Visibility = Visibility.Collapsed;
                StackPanelPPTControls.Visibility = Visibility.Visible;
            }
            catch
            {
                //BtnCheckPPT.Visibility = Visibility.Visible;
                StackPanelPPTControls.Visibility = Visibility.Collapsed;
                MessageBox.Show("未找到幻灯片");
            }
        }
        private void ToggleSwitchSupportWPS_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;

            Settings.PowerPointSettings.IsSupportWPS = ToggleSwitchSupportWPS.IsOn;
            SaveSettingsToFile();
        }

        public static bool isWPSSupportOn => Settings.PowerPointSettings.IsSupportWPS;

        // WPS 演示：其 COM 对象无法转换为 PowerPoint 互操作类型，改用晚绑定的动态对象驱动最小集。
        private static bool _isWpsMode;
        private static dynamic _wpsApp;
        private static int _wpsSlide;
        private static int _wpsSlidesCount;

        /// <summary>
        /// 取当前正在放映的 WPS 演示文稿（晚绑定）。WPS 的 COM 对象无法转换为 PowerPoint 互操作类型，
        /// 因此只用于读取幻灯片数与当前页，其余控制走 PptController 的快捷键。返回 null 表示未放映。
        /// 使用 COM 包装器提供重试、熔断、自动释放。
        /// </summary>
        private static dynamic GetWpsApplication()
        {
            foreach (string progid in new[] { "KWPP.Application", "wpp.Application", "Kwps.Application", "wps.Application" })
            {
                try
                {
                    // 使用包装器获取（带重试和熔断）
                    var wrapper = new ComObjectWrapper<object>(progid, maxRetries: 2, circuitBreakerThreshold: 3);
                    var app = wrapper.GetOrCreate();
                    if (app != null)
                    {
                        // 晚绑定访问 SlideShowWindows
                        dynamic dynApp = app;
                        if (Convert.ToInt32(dynApp.SlideShowWindows.Count) >= 1)
                            return dynApp;
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>进入 WPS 放映模式：显示 PPT 控件、画板叠加，并记录上下文 / 读回已保存墨迹。</summary>
        private void EnterWpsPresentationMode()
        {
            try
            {
                int count = 0, pos = 0; string name = "";
                try { count = Convert.ToInt32(_wpsApp.Slides.Count); } catch { }
                try { pos = Convert.ToInt32(_wpsApp.SlideShowWindows[1].View.CurrentShowPosition); } catch { }
                try { name = Convert.ToString(_wpsApp.ActivePresentation.Name); } catch { }
                if (count <= 0) count = _wpsSlidesCount > 0 ? _wpsSlidesCount : 100;
                slidescount = count;
                _wpsSlide = pos > 0 ? pos : 1;
                _wpsSlidesCount = count;
                pptName = name;
                ReinitMemoryStreams(slidescount + 2);
                SetSharePptContext(true, name, count, _wpsSlide);
                try { desk.ShowController.LoadPptImages(GetShareFolderPath()); } catch { }
            }
            catch { }
            Dispatcher.BeginInvoke(new Action(() => { StackPanelPPTControls.Visibility = Visibility.Visible; }));
        }

        /// <summary>退出 WPS 放映模式：隐藏 PPT 控件、关闭画板叠加层。</summary>
        private void ExitWpsPresentationMode()
        {
            _isWpsMode = false;
            _wpsApp = null;
            _wpsSlide = 0;
            SetSharePptContext(false, _sharePresentation, _shareSlidesCount, 0);
            try { if (_unifiedPollTimer != null && _unifiedPollTimer.IsEnabled) _unifiedPollTimer.Stop(); } catch { }
            Dispatcher.BeginInvoke(new Action(() => { StackPanelPPTControls.Visibility = Visibility.Collapsed; }));
        }

        public static bool IsShowingRestoreHiddenSlidesWindow = false;

        public static bool IsNotifyPreviousPageWindowShown = false;

        // System.Timers.Timer 的 Elapsed 在线程池上触发且不保证不重入：
        // 回调里的 COM 调用（Marshal.GetActiveObject + Dispatcher.Invoke）很容易超过 1 秒的 Interval，
        // 两个线程会并发进入，导致重复订阅 PPT 事件、状态错乱。用 0/1 闸门挡住。
        private int _pptCheckBusy;

        // 窗口关闭后 Timer 会被 Dispose；Elapsed 跑在线程池上，关闭瞬间正在执行的回调
        // 仍可能走到 Start()。用 disposed 闸门 + try/catch 双重保护，杜绝已释放对象被重启
        // 抛出的 ObjectDisposedException 穿透到线程池顶部导致进程终止。
        private volatile bool _pptTimerDisposed;

        private void TryStartPptTimer()
        {
            if (_pptTimerDisposed) return;
            try { _unifiedPollTimer?.Start(); } catch { }
        }

        private void TryStopPptTimer()
        {
            try { _unifiedPollTimer?.Stop(); } catch { }
        }

        private void TimerCheckPPT_Elapsed(object sender, ElapsedEventArgs e)
        {
            if (IsShowingRestoreHiddenSlidesWindow) return;
            if (System.Threading.Interlocked.Exchange(ref _pptCheckBusy, 1) == 1) return;
            try
            {
                // PowerPoint / WPS 都没运行时，Marshal.GetActiveObject 必然抛 COMException，
                // 还会被全局 FirstChanceException 钩子过一遍；1 秒一次是规律性卡顿的主要来源。
                // 先用便宜的进程名检查挡住，只有确实存在宿主进程时才查 COM ROT。
                bool pptProcessAlive = IsProcessRunning("POWERPNT");
                bool wppProcessAlive = isWPSSupportOn && IsProcessRunning("wpp");
                if (!pptProcessAlive && !wppProcessAlive) return;

                //使用下方提前创建 PowerPoint 实例，将导致 PowerPoint 不再有启动界面
                //pptApplication = (Microsoft.Office.Interop.PowerPoint.Application)Activator.CreateInstance(Marshal.GetTypeFromCLSID(new Guid("91493441-5A91-11CF-8700-00AA0060263B")));
                //new ComAwareEventInfo(typeof(EApplication_Event), "SlideShowBegin").AddEventHandler(pptApplication, new EApplication_SlideShowBeginEventHandler(this.PptApplication_SlideShowBegin));
                //new ComAwareEventInfo(typeof(EApplication_Event), "SlideShowEnd").AddEventHandler(pptApplication, new EApplication_SlideShowEndEventHandler(this.PptApplication_SlideShowEnd));
                //new ComAwareEventInfo(typeof(EApplication_Event), "SlideShowNextSlide").AddEventHandler(pptApplication, new EApplication_SlideShowNextSlideEventHandler(this.PptApplication_SlideShowNextSlide));
                //ConfigHelper.Instance.IsInitApplicationSuccessful = true;

                // 真实 PowerPoint 优先。覆盖旧 RCW 前先退订并释放旧对象，
                // 否则旧 RCW 上的 4 个事件订阅会随每次检测泄漏。
                ReleaseOldPptApplication();
                try
                {
                    pptApplication = pptProcessAlive
                        ? (Microsoft.Office.Interop.PowerPoint.Application)Marshal.GetActiveObject("PowerPoint.Application")
                        : null;
                }
                catch
                {
                    pptApplication = null;
                }

                if (pptApplication != null)
                {
                    TryStopPptTimer();
                    //获得演示文稿对象
                    presentation = pptApplication.ActivePresentation;
                    // 旧对象已在 ReleaseOldPptApplication 中退订；这里对新对象只订阅一次。
                    pptApplication.PresentationClose += PptApplication_PresentationClose;
                    pptApplication.SlideShowBegin += PptApplication_SlideShowBegin;
                    pptApplication.SlideShowNextSlide += PptApplication_SlideShowNextSlide;
                    pptApplication.SlideShowEnd += PptApplication_SlideShowEnd;
                    // 获得幻灯片对象集合
                    slides = presentation.Slides;

                    // 获得幻灯片的数量
                    slidescount = slides.Count;
                    ReinitMemoryStreams(slidescount + 2);
                    // 获得当前选中的幻灯片
                    try
                    {
                        // 在普通视图下这种方式可以获得当前选中的幻灯片对象
                        // 然而在阅读模式下，这种方式会出现异常
                        slide = slides[pptApplication.ActiveWindow.Selection.SlideRange.SlideNumber];
                    }
                    catch
                    {
                        // 在阅读模式下出现异常时，通过下面的方式来获得当前选中的幻灯片对象
                        slide = pptApplication.SlideShowWindows[1].View.Slide;
                    }
                }
                else if (wppProcessAlive)
                {
                    // WPS 演示：其 COM 对象无法转换为 PowerPoint 互操作类型，改用晚绑定动态对象。
                    // 进入“WPS 放映模式”：画板叠加 + 快捷键控翻页（PptController 已兼容 WPS）。
                    _wpsApp = GetWpsApplication();
                    if (_wpsApp != null)
                    {
                        _isWpsMode = true;
                        TryStopPptTimer();
                        EnterWpsPresentationMode();
                        return;
                    }
                }

                if (pptApplication == null) return;
                //BtnCheckPPT.Visibility = Visibility.Collapsed;

                // 跳转到上次播放页
                if (Settings.PowerPointSettings.IsNotifyPreviousPage)
                    Application.Current.Dispatcher.BeginInvoke(() =>
                        {
                          try {
                            string defaultFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) +
                                                       @"\painter Strokes\Auto Saved\Presentations\";
                            string folderPath = defaultFolderPath + presentation.Name + "_" + presentation.Slides.Count;
                            if (File.Exists(folderPath + "/Position") && !IsNotifyPreviousPageWindowShown) //判断是否已存在NotifyPreviousPage窗口
                            {
                                if (int.TryParse(File.ReadAllText(folderPath + "/Position"), out var page))
                                {
                                    IsNotifyPreviousPageWindowShown= true;
                                    if (page <= 0) return;
                                    new YesOrNoNotificationWindow($"上次播放到了第 {page} 页, 是否立即跳转", () =>
                                    {
                                        if (pptApplication.SlideShowWindows.Count >= 1)
                                        {
                                            // 如果已经播放了的话, 跳转
                                            presentation.SlideShowWindow.View.GotoSlide(page);
                                        }
                                        else
                                        {
                                            presentation.Windows[1].View.GotoSlide(page);
                                        }
                                    }).ShowDialog();
                                }
                            }
                          } catch (Exception ex)
                          {
                              LogHelper.WriteLogToFile("NotifyPreviousPage failed: " + ex, LogHelper.LogType.Error);
                          }
                        }, DispatcherPriority.Normal);


                // 放映按钮必须无条件显示。旧实现把它塞在「隐藏幻灯片提醒」的条件块里，
                // 而该提醒现在默认关闭 → 检测到 PPT 后放映入口一直不出现，PPT 控制整体失效。
                SafeUiInvoke(() => { BtnPPTSlideShow.Visibility = Visibility.Visible; });

                //检查是否有隐藏幻灯片（默认不提醒；仅用户显式打开「提示隐藏幻灯片」时才询问）
                if (Settings.PowerPointSettings.IsNotifyHiddenPage)
                {
                    bool isHaveHiddenSlide = false;
                    foreach (Slide slide in slides)
                    {
                        if (slide.SlideShowTransition.Hidden == Microsoft.Office.Core.MsoTriState.msoTrue)
                        {
                            isHaveHiddenSlide = true;
                            break;
                        }
                    }

                    if (isHaveHiddenSlide && !IsShowingRestoreHiddenSlidesWindow)
                    {
                        IsShowingRestoreHiddenSlidesWindow = true;
                        SafeUiInvoke(() =>
                        {
                            new YesOrNoNotificationWindow("检测到此演示文稿包含隐藏的幻灯片，是否取消隐藏？",
                                clearHiddenSlidesFlag: true, yesAction: () =>
                                {
                                    foreach (Slide slide in slides)
                                    {
                                        if (slide.SlideShowTransition.Hidden ==
                                            Microsoft.Office.Core.MsoTriState.msoTrue)
                                        {
                                            slide.SlideShowTransition.Hidden =
                                                Microsoft.Office.Core.MsoTriState.msoFalse;
                                        }
                                    }
                                }).ShowDialog();
                        });
                    }
                }

                //如果检测到已经开始放映，则立即进入画板模式
                if (pptApplication.SlideShowWindows.Count >= 1)
                {
                    PptApplication_SlideShowBegin(pptApplication.SlideShowWindows[1]);
                }
            }
            catch
            {
                //StackPanelPPTControls.Visibility = Visibility.Collapsed;
                try
                {
                    SafeUiInvoke(() =>
                    {
                        BtnPPTSlideShow.Visibility = Visibility.Collapsed;
                    });
                }
                catch
                {
                }
                TryStartPptTimer();
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _pptCheckBusy, 0);
            }
        }

        /// <summary>
        /// 幂等解绑 PowerPoint COM 事件。Delegate.Combine 不会自动去重，
        /// 重复 += 之后单次 -= 只能解绑一个引用，必须在订阅前先无条件解绑一遍。
        /// </summary>
        private void UnhookPptEvents()
        {
            UnhookPptEventsFrom(pptApplication);
        }

        /// <summary>从指定的 RCW 退订事件。覆盖 pptApplication 字段之前必须用【旧对象】调用，
        /// 否则退订的是新 RCW，真正挂了事件的旧 RCW 连同 4 个订阅一起泄漏。</summary>
        private void UnhookPptEventsFrom(Microsoft.Office.Interop.PowerPoint.Application app)
        {
            if (app == null) return;
            try
            {
                app.PresentationClose -= PptApplication_PresentationClose;
                app.SlideShowBegin -= PptApplication_SlideShowBegin;
                app.SlideShowNextSlide -= PptApplication_SlideShowNextSlide;
                app.SlideShowEnd -= PptApplication_SlideShowEnd;
            }
            catch
            {
                // COM 对象可能已失效，忽略
            }
            finally
            {
                // 确保释放 COM 引用
                try { Marshal.FinalReleaseComObject(app); } catch { }
            }
        }

        /// <summary>替换 pptApplication 前调用：退订并释放旧 RCW，避免每次重连泄漏一组 COM 事件订阅。</summary>
        private void ReleaseOldPptApplication()
        {
            Microsoft.Office.Interop.PowerPoint.Application old = pptApplication;
            if (old == null) return;
            UnhookPptEventsFrom(old);
            try { Marshal.FinalReleaseComObject(old); } catch { }
            pptApplication = null;
        }

        private void PptApplication_PresentationClose(Presentation Pres)
        {
            // 退订并释放当前 RCW，再置空，避免文稿关闭后 RCW 残留。
            ReleaseOldPptApplication();
            TryStartPptTimer();
            try
            {
                SafeUiInvoke(() =>
                {
                    //BtnPPTSlideShow.Visibility = Visibility.Collapsed;
                    BtnPPTSlideShowEnd.Visibility = Visibility.Collapsed;

                    // 兜底：直接关闭 PowerPoint（未必触发 SlideShowEnd）时也要恢复消息图标
                });
            }
            catch
            {
                // Dispatcher 已关闭（应用退出中）
            }
        }

        bool isPresentationHaveBlackSpace = false;


        private string pptName = null;
        int currentShowPosition = -1;
        //bool isButtonBackgroundTransparent = true; //此变量仅用于保存用于幻灯片放映时的优化
        private void PptApplication_SlideShowBegin(SlideShowWindow Wn)
        {
            LogHelper.WriteLogToFile("PowerPoint Application Slide Show Begin", LogHelper.LogType.Event);
            SafeUiInvoke(() =>
            {
                // 新放映开始：强制清空上一份文稿的缩略图状态（含缓存身份），
                // 防止「换了 PPT 再放映仍看到上一份缩略图」。
                ClosePptThumbnailOverview();

                if (currentMode == 1)
                {
                    // 退出画板模式
                    BtnSwitch_Click(null, null);
                }

                //调整颜色
                // 希沃大屏常见 16:10 / 4:3 / 21:9，与 16:9 的 PPT 放映之间会出现黑边，
                // 需要把工具条前景色调亮。原条件 Math.Abs(...) <= -0.01 恒为 false
                //（绝对值不可能为负），这段适配从未生效；改为「明显偏离 16:9」才进入。
                double screenRatio = SystemParameters.PrimaryScreenWidth / SystemParameters.PrimaryScreenHeight;
                if (Math.Abs(screenRatio - 16.0 / 9) > 0.01)
                {
                    if (Wn.Presentation.PageSetup.SlideWidth / Wn.Presentation.PageSetup.SlideHeight < 1.65)
                    {
                        isPresentationHaveBlackSpace = true;
                        //isButtonBackgroundTransparent = ToggleSwitchTransparentButtonBackground.IsOn;

                        if (BtnSwitchTheme.Content.ToString() == "深色")
                        {
                            //Light
                            BtnExit.Foreground = Brushes.White;
                            SymbolIconBtnColorBlackContent.Foreground = Brushes.White;
                            ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark;
                            //BtnExit.Background = new SolidColorBrush(StringToColor("#AACCCCCC"));
                        }
                        else
                        {
                            //Dark
                            //BtnExit.Background = new SolidColorBrush(StringToColor("#AA555555"));
                        }
                    }
                }
                slidescount = Wn.Presentation.Slides.Count;
                previousSlideID = 0;
                ReinitMemoryStreams(slidescount + 2);

                pptName = Wn.Presentation.Name;
                LogHelper.NewLog("Name: " + Wn.Presentation.Name);
                LogHelper.NewLog("Slides Count: " + slidescount.ToString());

                // 进入放映：记录 Web 上传所需的上下文，并读回上次自动保存的叠加图片
                SetSharePptContext(true, Wn.Presentation.Name, Wn.Presentation.Slides.Count, Wn.View.CurrentShowPosition);
                try { desk.ShowController.LoadPptImages(GetShareFolderPath()); } catch { }

                //检查是否有已有墨迹，并加载
                if (Settings.PowerPointSettings.IsAutoSaveStrokesInPowerPoint)
                {
                    string defaultFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + @"\painter Strokes\Auto Saved\Presentations\";
                    if (Directory.Exists(defaultFolderPath + Wn.Presentation.Name + "_" + Wn.Presentation.Slides.Count))
                    {
                        LogHelper.WriteLogToFile("Found saved strokes", LogHelper.LogType.Trace);
                        FileInfo[] files = new DirectoryInfo(defaultFolderPath + Wn.Presentation.Name + "_" + Wn.Presentation.Slides.Count).GetFiles();
                        int count = 0;
                        foreach (FileInfo file in files)
                        {
                            // 叠加图片 / 清单不是墨迹文件，跳过（否则 int.Parse 会抛异常刷错误日志）
                            string stem = System.IO.Path.GetFileNameWithoutExtension(file.Name);
                            if (!int.TryParse(stem, out int parsedSlide)) continue;
                            {
                                int i = parsedSlide;
                                // 自动保存目录可能残留更大文稿编号的文件，越界直接跳过（否则每页都刷错误日志）
                                if (i < 0 || i >= memoryStreams.Length) continue;
                                try
                                {
                                    //var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read);
                                    //MemoryStream ms = new MemoryStream(File.ReadAllBytes(file.FullName));
                                    //new StrokeCollection(fs).Save(ms);
                                    //ms.Position = 0;
                                    memoryStreams[i] = new MemoryStream(File.ReadAllBytes(file.FullName));
                                    memoryStreams[i].Position = 0;
                                    count++;
                                }
                                catch (Exception ex)
                                {
                                    LogHelper.WriteLogToFile(string.Format("Failed to load strokes on Slide {0}\n{1}", i, ex.ToString()), LogHelper.LogType.Error);
                                }
                            }
                        }
                        LogHelper.WriteLogToFile(string.Format("Loaded {0} saved strokes", count.ToString()));
                    }
                }

                pointDesktop = new Point(ViewboxFloatingBar.Margin.Left, ViewboxFloatingBar.Margin.Top);
                pointPPT = new Point(-1, -1);

                StackPanelPPTControls.Visibility = Visibility.Visible;
                BtnPPTSlideShow.Visibility = Visibility.Collapsed;
                BtnPPTSlideShowEnd.Visibility = Visibility.Visible;
                ViewBoxStackPanelMain.Margin = new Thickness(10, 10, 10, 10);

                if (Settings.PowerPointSettings.IsShowCanvasAtNewSlideShow && Main_Grid.Background == Brushes.Transparent)
                {
                    if (currentMode != 0)
                    {
                        currentMode = 0;
                        GridBackgroundCover.Visibility = Visibility.Collapsed;

                        //SaveStrokes();
                        ClearStrokes(true);

                        if (BtnSwitchTheme.Content.ToString() == "浅色")
                        {
                            BtnSwitch.Content = "黑板";
                        }
                        else
                        {
                            BtnSwitch.Content = "白板";
                        }
                        StackPanelPPTButtons.Visibility = Visibility.Visible;
                    }
                    BtnHidePainterCanvas_Click(BtnHidePainterCanvas, null);
                }
                //if (GridBackgroundCover.Visibility == Visibility.Visible)
                //{
                //    SaveStrokes();
                //    currentMode = 0;
                //    GridBackgroundCover.Visibility = Visibility.Hidden;
                //}

                ClearStrokes(true);

                SetBorderFloatingBarMainControlsVisibility(true, false);
                BorderPenColorRed_MouseUp(BorderPenColorRed, null);

                if (Settings.PowerPointSettings.IsShowCanvasAtNewSlideShow == false)
                {
                    BtnHidePainterCanvas_Click(BtnHidePainterCanvas, null);
                }

                isEnteredSlideShowEndEvent = false;
                PptNavigationTextBlock.Text = $"{Wn.View.CurrentShowPosition}/{Wn.Presentation.Slides.Count}";
                LogHelper.NewLog("PowerPoint Slide Show Loading process complete");

                ScheduleUiAction(100, () =>
                {
                    ViewboxFloatingBar.Margin = new Thickness((SystemParameters.PrimaryScreenWidth - ViewboxFloatingBar.ActualWidth) / 2, SystemParameters.PrimaryScreenHeight - 60, -2000, -200);
                });
            });

            // 放映模式：隐藏右下角消息图标（PowerPoint 与 WPS 同一套事件路径）
            //previousSlideID = Wn.View.CurrentShowPosition;
            ////检查是否有已有墨迹，并加载当前页
            //if (Settings.Automation.IsAutoSaveStrokesInPowerPoint)
            //{
            //    try
            //    {
            //        if (memoryStreams[Wn.View.CurrentShowPosition].Length > 0)
            //        {
            //            SafeUiInvoke(() =>
            //            {
            //                painterCanvas.Strokes = new System.Windows.Ink.StrokeCollection(memoryStreams[Wn.View.CurrentShowPosition]);
            //            });
            //        }
            //    }
            //    catch (Exception ex)
            //    {
            //        LogHelper.WriteLogToFile(string.Format("Failed to load strokes for current slide (Slide {0})\n{1}", Wn.View.CurrentShowPosition, ex.ToString()), LogHelper.LogType.Error);
            //    }
            //}
        }

        bool isEnteredSlideShowEndEvent = false; //防止重复调用本函数导致墨迹保存失效
        private void PptApplication_SlideShowEnd(Presentation Pres)
        {
            IsNotifyPreviousPageWindowShown = false;
            LogHelper.WriteLogToFile(string.Format("PowerPoint Slide Show End"), LogHelper.LogType.Event);
            if (isEnteredSlideShowEndEvent)
            {
                LogHelper.WriteLogToFile("Detected previous entrance, returning");
                return;
            }
            isEnteredSlideShowEndEvent = true;
            // 本方法由 PowerPoint/WPS 的 COM 事件在 RPC 线程上调用；任何未捕获异常
            // 都会穿透非托管边界直接终止进程。整个方法体统一兜底。
            try
            {
                SlideShowEndCore(Pres);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile("SlideShowEnd failed: " + ex, LogHelper.LogType.Error);
            }
        }

        private void SlideShowEndCore(Presentation Pres)
        {
            // 退出放映：关闭屏幕叠加图片层，后续上传重新走内置白板
            SetSharePptContext(false, Pres?.Name ?? "", Pres?.Slides?.Count ?? 0, 0);
            int slideCount = 0;
            try { slideCount = Pres == null ? 0 : Pres.Slides.Count; } catch { }
            if (Settings.PowerPointSettings.IsAutoSaveStrokesInPowerPoint && slideCount > 0)
            {
                string defaultFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + @"\painter Strokes\Auto Saved\Presentations\";
                string folderPath = defaultFolderPath + (Pres?.Name ?? "Untitled") + "_" + slideCount;
                if (!Directory.Exists(folderPath))
                {
                    Directory.CreateDirectory(folderPath);
                }
                try
                {
                    File.WriteAllText(folderPath + "/Position", previousSlideID.ToString());
                }
                catch { }
                SafeUiInvoke(() =>
                {
                    try
                    {
                        MemoryStream ms = new MemoryStream();
                        painterCanvas.Strokes.Save(ms);
                        ms.Position = 0;
                        // 放映期间未翻页时 currentShowPosition 仍是 -1，或上次放映页数更多导致越界；
                        // 回退到第一页，避免 currentShowPosition 越界被 catch 吞掉而静默丢墨迹。
                        int pos = currentShowPosition;
                        if (pos < 1 || pos >= memoryStreams.Length) pos = 1;
                        memoryStreams[pos] = ms;
                    }
                    catch { }
                });
                for (int i = 1; i <= Math.Min(slideCount, memoryStreams.Length - 1); i++)
                {
                    if (memoryStreams[i] != null)
                    {
                        try
                        {
                            if (memoryStreams[i].Length > 8)
                            {
                                byte[] srcBuf = memoryStreams[i].ToArray(); // ToArray 不受 Position 影响，避免写出全 0 的坏墨迹文件
                                //MessageBox.Show(memoryStreams[i].Length.ToString());
                                int byteLength = srcBuf.Length;
                                File.WriteAllBytes(folderPath + @"\" + i.ToString("0000") + ".icstk", srcBuf);
                                LogHelper.WriteLogToFile(string.Format("Saved strokes for Slide {0}, size={1}, byteLength={2}", i.ToString(), memoryStreams[i].Length, byteLength));
                            }
                            else
                            {
                                File.Delete(folderPath + @"\" + i.ToString("0000") + ".icstk");
                            }
                        }
                        catch (Exception ex)
                        {
                            LogHelper.WriteLogToFile(string.Format("Failed to save strokes for Slide {0}\n{1}", i, ex.ToString()), LogHelper.LogType.Error);
                            // catch 块内抛出的异常不会被同一个 try 捕获，会穿透出
                            // PptApplication_SlideShowEnd（运行在 PowerPoint 的 RPC 线程上）→ 进程崩溃
                            try
                            {
                                File.Delete(folderPath + @"\" + i.ToString("0000") + ".icstk");
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }

            SafeUiInvoke(() =>
            {
                // 退出放映：关闭并清空缩略图侧栏，避免下次放映残留上一份 PPT 的缩略图。
                ClosePptThumbnailOverview();
                isPresentationHaveBlackSpace = false;

                if (BtnSwitchTheme.Content.ToString() == "深色")
                {
                    //Light
                    BtnExit.Foreground = Brushes.Black;
                    SymbolIconBtnColorBlackContent.Foreground = Brushes.White;
                    ThemeManager.Current.ApplicationTheme = ApplicationTheme.Light;
                }
                else
                {
                    //Dark
                }

                BtnPPTSlideShow.Visibility = Visibility.Visible;
                BtnPPTSlideShowEnd.Visibility = Visibility.Collapsed;
                StackPanelPPTControls.Visibility = Visibility.Collapsed;
                ViewBoxStackPanelMain.Margin = new Thickness(10, 10, 10, 55);

                if (currentMode != 0)
                {
                    currentMode = 0;
                    GridBackgroundCover.Visibility = Visibility.Collapsed;

                    //SaveStrokes();
                    ClearStrokes(true);
                    //RestoreStrokes(true);

                    if (BtnSwitchTheme.Content.ToString() == "浅色")
                    {
                        BtnSwitch.Content = "黑板";
                    }
                    else
                    {
                        BtnSwitch.Content = "白板";
                    }
                    StackPanelPPTButtons.Visibility = Visibility.Visible;
                }
                //if (GridBackgroundCover.Visibility == Visibility.Visible)
                //{
                //    SaveStrokes();
                //}


                ClearStrokes(true);

                if (Main_Grid.Background != Brushes.Transparent)
                {
                    BtnHidePainterCanvas_Click(BtnHidePainterCanvas, null);
                }

                if (pointDesktop != new Point(-1, -1))
                {
                    ViewboxFloatingBar.Margin = new Thickness(pointDesktop.X, pointDesktop.Y, -2000, -200);
                }

                // 退出放映：恢复右下角消息图标（若随后进入了全屏标注，则仍然保持隐藏）
            });
        }

        int previousSlideID = 0;
        MemoryStream[] memoryStreams = new MemoryStream[50];

        /// <summary>
        /// 重建 PPT 放映笔迹缓存数组。MemoryStream 是 IDisposable，原实现直接
        /// <c>new MemoryStream[n]</c> 覆盖，旧数组里的流既不释放也无人引用，
        /// 反复"开始放映→结束放映"会持续堆积未释放的流及其内部 byte[]。
        /// </summary>
        private void ReinitMemoryStreams(int size)
        {
            DisposeAllMemoryStreams();
            memoryStreams = new MemoryStream[size];
        }

        private void DisposeAllMemoryStreams()
        {
            if (memoryStreams == null) return;
            for (int i = 0; i < memoryStreams.Length; i++)
            {
                try
                {
                    memoryStreams[i]?.Dispose();
                }
                catch
                {
                }
                memoryStreams[i] = null;
            }
        }

        private void PptApplication_SlideShowNextSlide(SlideShowWindow Wn)
        {
            LogHelper.WriteLogToFile(string.Format("PowerPoint Next Slide (Slide {0})", Wn.View.CurrentShowPosition), LogHelper.LogType.Event);
            if (Wn.View.CurrentShowPosition != previousSlideID)
            {
                // 换页：更新页码上下文 → 叠加层自动隐藏其它页的图片；翻回来时又重新显示
                SetSharePptContext(true, Wn.Presentation.Name, Wn.Presentation.Slides.Count, Wn.View.CurrentShowPosition);
                SafeUiInvoke(() =>
                {
                    MemoryStream ms = new MemoryStream();
                    painterCanvas.Strokes.Save(ms);
                    ms.Position = 0;
                    // 页码来自 COM（1 基）；切换文稿后页数变小或旧事件晚到时，
                    // previousSlideID 可能越界，写入前必须校验，否则异常被吞导致笔迹静默丢失。
                    if (previousSlideID >= 0 && previousSlideID < memoryStreams.Length)
                    {
                        memoryStreams[previousSlideID]?.Dispose();
                        memoryStreams[previousSlideID] = ms;
                    }
                    else { ms.Dispose(); }

                    if (painterCanvas.Strokes.Count > Settings.Automation.MinimumAutomationStrokeNumber && Settings.PowerPointSettings.IsAutoSaveScreenShotInPowerPoint && !_isPptClickingBtnTurned)
                        SaveScreenShot(true, Wn.Presentation.Name + "/" + Wn.View.CurrentShowPosition);
                    _isPptClickingBtnTurned = false;

                    ClearStrokes(true);
                    timeMachine.ClearStrokeHistory();

                    try
                    {
                        int pos = Wn.View.CurrentShowPosition;
                        if (pos >= 0 && pos < memoryStreams.Length && memoryStreams[pos] != null && memoryStreams[pos].Length > 0)
                        {
                            memoryStreams[pos].Position = 0;
                            painterCanvas.Strokes.Add(new StrokeCollection(memoryStreams[pos]));
                        }
                        currentShowPosition = pos;
                    }
                    catch
                    { }

                    PptNavigationTextBlock.Text = $"{Wn.View.CurrentShowPosition}/{Wn.Presentation.Slides.Count}";
                });
                previousSlideID = Wn.View.CurrentShowPosition;

            }
        }

        private bool _isPptClickingBtnTurned = false;

        /// <summary>
        /// 在后台任务执行 PowerPoint COM 调用。使用 Task.Run 避免线程创建开销，
        /// 复用线程池，并正确处理异常与取消。
        /// </summary>
        private void RunPptComAction(Action comAction, string wpsCommand = null)
        {
            Task.Run(async () =>
            {
                try
                {
                    if (_isWpsMode && pptApplication == null)
                    {
                        if (!string.IsNullOrEmpty(wpsCommand))
                            desk.PptController.Run(wpsCommand);
                        return;
                    }
                    await Task.Yield(); // 确保在线程池线程执行
                    comAction();
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile("PowerPoint COM call failed: " + ex, LogHelper.LogType.Error);
                    try
                    {
                        if (_isWpsMode && !string.IsNullOrEmpty(wpsCommand))
                        {
                            desk.PptController.Run(wpsCommand);
                        }
                        else
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                StackPanelPPTControls.Visibility = Visibility.Collapsed;
                            }));
                        }
                    }
                    catch { }
                }
            });
        }

        /// <summary>安全读取当前放映的演示文稿名与页码，放映未激活时返回 false。同时兼容 WPS（晚绑定）。</summary>
        private bool TryGetSlideShowNameAndPosition(out string presentationName, out int position)
        {
            presentationName = null;
            position = 0;
            try
            {
                if (pptApplication != null)
                {
                    // PowerPoint COM 集合是 1-based：Count == 1 时 [1] 有效，< 1 才是无放映窗口
                    if (pptApplication.SlideShowWindows == null || pptApplication.SlideShowWindows.Count < 1) return false;
                    presentationName = pptApplication.SlideShowWindows[1].Presentation.Name;
                    position = pptApplication.SlideShowWindows[1].View.CurrentShowPosition;
                    return !string.IsNullOrEmpty(presentationName);
                }
                if (_isWpsMode && _wpsApp != null)
                {
                    presentationName = Convert.ToString(_wpsApp.ActivePresentation.Name);
                    position = _wpsSlide > 0 ? _wpsSlide : Convert.ToInt32(_wpsApp.SlideShowWindows[1].View.CurrentShowPosition);
                    return !string.IsNullOrEmpty(presentationName);
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile("Failed to read slideshow info: " + ex, LogHelper.LogType.Error);
            }
            return false;
        }

        private void BtnPPTSlidesUp_Click(object sender, RoutedEventArgs e)
        {
            if (currentMode == 1)
            {
                GridBackgroundCover.Visibility = Visibility.Collapsed;
                currentMode = 0;
            }

            _isPptClickingBtnTurned = true;
            if (painterCanvas.Strokes.Count > Settings.Automation.MinimumAutomationStrokeNumber &&
                Settings.PowerPointSettings.IsAutoSaveScreenShotInPowerPoint &&
                TryGetSlideShowNameAndPosition(out string shotFolder, out int shotPosition))
                SaveScreenShot(true, shotFolder + "/" + shotPosition);
            RunPptComAction(() =>
            {
                pptApplication.SlideShowWindows[1].Activate();
                pptApplication.SlideShowWindows[1].View.Previous();
            }, "prev");
        }

        private void BtnPPTSlidesDown_Click(object sender, RoutedEventArgs e)
        {
            if (currentMode == 1)
            {
                GridBackgroundCover.Visibility = Visibility.Collapsed;
                currentMode = 0;
            }
            _isPptClickingBtnTurned = true;
            if (painterCanvas.Strokes.Count > Settings.Automation.MinimumAutomationStrokeNumber &&
                Settings.PowerPointSettings.IsAutoSaveScreenShotInPowerPoint &&
                TryGetSlideShowNameAndPosition(out string shotFolder, out int shotPosition))
                SaveScreenShot(true, shotFolder + "/" + shotPosition);
            RunPptComAction(() =>
            {
                pptApplication.SlideShowWindows[1].Activate();
                pptApplication.SlideShowWindows[1].View.Next();
            }, "next");
        }


        private async void PPTNavigationBtn_Click(object sender, MouseButtonEventArgs e)
        {
            // 注意：不要在这里切换画笔/屏幕模式（原 BtnHidePainterCanvas_Click 会退出画笔模式，
            // 而退出后不会自动恢复，导致用户看完缩略图回来画笔不见了）。
            // 只负责展开/收起缩略图，保持当前模式原样。

            // 点击页码：优先展开本程序自带的幻灯片缩略图侧栏（可点击跳页）。
            // 点在右侧控制栏就在右侧显示，点在左侧就在左侧显示。
            bool onRight = IsDescendantOf(sender as DependencyObject, ViewboxPPTSidesControlRight);
            if (Settings.PowerPointSettings.PptThumbnailEnabled && OpenPptThumbnailOverview(onRight))
            {
                // 缩略图总览已展开
            }
            else
            {
                // SlideShowWindows 是 1-based COM 集合：没有放映窗口时 Count 为 0，
                // 直接取 [1] 会抛异常；async void 又无法把异常交回调用方，只能就地兜住。
                try
                {
                    if (pptApplication != null && pptApplication.SlideShowWindows.Count > 0)
                        pptApplication.SlideShowWindows[1].SlideNavigation.Visible = true;
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile("PPT SlideNavigation failed: " + ex, LogHelper.LogType.Error);
                }
            }

            // 控制居中
            if (BtnPPTSlideShowEnd.Visibility == Visibility.Visible)
            {
                if (ViewboxFloatingBar.Margin == new Thickness((SystemParameters.PrimaryScreenWidth - ViewboxFloatingBar.ActualWidth) / 2, SystemParameters.PrimaryScreenHeight - 60, -2000, -200))
                {
                    await Task.Delay(100);
                    ViewboxFloatingBar.Margin = new Thickness((SystemParameters.PrimaryScreenWidth - ViewboxFloatingBar.ActualWidth) / 2, SystemParameters.PrimaryScreenHeight - 60, -2000, -200);
                }
            }
        }

        /// <summary>判断 element 是否位于 ancestor 的可视树内（含自身）。用于区分左右页码按钮。</summary>
        private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
        {
            if (element == null || ancestor == null) return false;
            for (DependencyObject cur = element; cur != null; cur = VisualTreeHelper.GetParent(cur))
            {
                if (ReferenceEquals(cur, ancestor)) return true;
            }
            return false;
        }

        /// <summary>LRU 位图缓存：限制最大条目数，自动淘汰最久未用，防止内存无限增长</summary>
        private sealed class LruBitmapCache
        {
            private readonly int _capacity;
            private readonly Dictionary<byte[], LinkedListNode<CacheEntry>> _map = new Dictionary<byte[], LinkedListNode<CacheEntry>>(ByteArrayComparer.Instance);
            private readonly LinkedList<CacheEntry> _lru = new LinkedList<CacheEntry>();

            public LruBitmapCache(int capacity = 50) { _capacity = capacity; }

            public bool TryGet(byte[] key, out BitmapImage value)
            {
                lock (_map)
                {
                    if (_map.TryGetValue(key, out var node))
                    {
                        _lru.Remove(node);
                        _lru.AddFirst(node);
                        value = node.Value.Image;
                        return true;
                    }
                    value = null;
                    return false;
                }
            }

            public void Add(byte[] key, BitmapImage image)
            {
                lock (_map)
                {
                    if (_map.TryGetValue(key, out var existing))
                    {
                        _lru.Remove(existing);
                        existing.Value.Image = image;
                        _lru.AddFirst(existing);
                        return;
                    }
                    if (_map.Count >= _capacity)
                    {
                        var oldest = _lru.Last;
                        if (oldest != null)
                        {
                            _map.Remove(oldest.Value.Key);
                            _lru.RemoveLast();
                        }
                    }
                    var node = new LinkedListNode<CacheEntry>(new CacheEntry { Key = key, Image = image });
                    _lru.AddFirst(node);
                    _map[key] = node;
                }
            }

            public void Clear()
            {
                lock (_map) { _map.Clear(); _lru.Clear(); }
            }

            private sealed class CacheEntry { public byte[] Key; public BitmapImage Image; }
            private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
            {
                public static readonly ByteArrayComparer Instance = new ByteArrayComparer();
                public bool Equals(byte[] x, byte[] y) => StructuralComparisons.StructuralEqualityComparer.Equals(x, y);
                public int GetHashCode(byte[] obj) => obj != null ? StructuralComparisons.StructuralEqualityComparer.GetHashCode(obj) : 0;
            }
        }

        private void BtnPPTSlideShow_Click(object sender, RoutedEventArgs e)
        {
            if (_isWpsMode && pptApplication == null)
            {
                desk.PptController.Run("start");
                return;
            }
            // PowerPoint COM 是 STA 组件：必须显式设为 STA，否则 MTA 线程调用会触发
            // RPC_E_WRONGTHREAD / 跨套间编组失败，异常被吞后表现为「开始放映无反应」。
            Thread worker = new Thread(new ThreadStart(() =>
            {
                try
                {
                    presentation.SlideShowSettings.Run();
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile("PPT 开始放映失败: " + ex, LogHelper.LogType.Error);
                }
            }));
            worker.SetApartmentState(ApartmentState.STA);
            worker.IsBackground = true;
            worker.Start();
        }

        private void BtnPPTSlideShowEnd_Click(object sender, RoutedEventArgs e)
        {
            // 兜底：某些情况下 SlideShowEnd COM 事件不会触发，这里主动关闭叠加层
            SetSharePptContext(false, _sharePresentation, _shareSlidesCount, 0);
            SafeUiInvoke(() =>
            {
                // 与 SlideShowEnd 一致：点退出也收起并清空缩略图。
                ClosePptThumbnailOverview();
                try
                {
                    MemoryStream ms = new MemoryStream();
                    painterCanvas.Strokes.Save(ms);
                    ms.Position = 0;
                    int idx = 0;
                    if (_isWpsMode && pptApplication == null) idx = _wpsSlide;
                    else if (pptApplication != null && pptApplication.SlideShowWindows.Count >= 1)
                        idx = pptApplication.SlideShowWindows[1].View.CurrentShowPosition;
                    if (idx >= 0 && idx < memoryStreams.Length) memoryStreams[idx] = ms;
                    else memoryStreams[0] = ms;
                    timeMachine.ClearStrokeHistory();
                    IsNotifyPreviousPageWindowShown = false;
                }
                catch { }
            });
            if (_isWpsMode && pptApplication == null)
            {
                desk.PptController.Run("stop");
                ExitWpsPresentationMode();
                return;
            }
            // 与放映线程同理：Office COM 需用 STA 线程调用，MTA 下 Exit() 会静默失败。
            Thread worker = new Thread(new ThreadStart(() =>
            {
                try
                {
                    pptApplication.SlideShowWindows[1].View.Exit();
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile("PPT 结束放映失败: " + ex, LogHelper.LogType.Error);
                }
            }));
            worker.SetApartmentState(ApartmentState.STA);
            worker.IsBackground = true;
            worker.Start();
        }

        #region Show Mode (图片放映)

        // ---- PPT 放映 / Web 上传上下文（纯字段，供跨线程的 ContextProvider 读取）----
        private volatile bool _shareIsPpt;
        private volatile int _shareSlide;
        private volatile int _shareSlidesCount;
        private string _sharePresentation = "";
        private readonly LruBitmapCache _pptImageCache = new LruBitmapCache(50);

        /// <summary>墨迹自动保存的同一文件夹：文档\painter Strokes\Auto Saved\Presentations\{名称}_{页数}</summary>
        private string GetShareFolderPath()
        {
            // 文稿名来自 COM，可能含 Windows 非法字符（\ / : * ? " < > |）或过长导致 Win7 MAX_PATH 超限。
            // 保存与读回都调用本函数，净化规则一致即可稳定命中同一目录。
            string safeName = SanitizeFileNamePart(_sharePresentation);
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "painter Strokes", "Auto Saved", "Presentations",
                safeName + "_" + _shareSlidesCount);
        }

        /// <summary>把文稿名净化为合法的 Windows 目录名片段：替换非法字符并截断长度。</summary>
        private static string SanitizeFileNamePart(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Untitled";
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            string result = sb.ToString().Trim(' ', '.');
            if (result.Length == 0) result = "Untitled";
            // Win7 MAX_PATH=260，给 "_页数" 和路径前缀留余量
            if (result.Length > 80) result = result.Substring(0, 80);
            return result;
        }

        /// <summary>上传发生时的运行环境快照。只读取上面的纯字段，可在 HTTP 线程上安全调用。</summary>
        private desk.ShareContext GetShareContext()
        {
            string name = _sharePresentation ?? "";
            int count = _shareSlidesCount;
            bool isPpt = _shareIsPpt;
            return new desk.ShareContext {
                IsPpt = isPpt,
                PresentationName = name,
                SlidesCount = count,
                Slide = _shareSlide,
                FolderPath = isPpt ? GetShareFolderPath() : ""
            };
        }

        /// <summary>由 PPT COM 事件调用（可能在 RPC 线程），先更新纯字段，再回 UI 线程刷新叠加层。</summary>
        private void SetSharePptContext(bool inPpt, string name, int slidesCount, int slide)
        {
            _sharePresentation = name ?? "";
            _shareSlidesCount = slidesCount;
            _shareSlide = slide;
            _shareIsPpt = inPpt;
            try {
                Application.Current?.Dispatcher?.BeginInvoke(new Action(() => {
                    // 统一轮询定时器：放映期间运行，非放映期间停止
                    if (_unifiedPollTimer != null) {
                        if (inPpt) { if (!_unifiedPollTimer.IsEnabled) _unifiedPollTimer.Start(); }
                        else if (_unifiedPollTimer.IsEnabled) _unifiedPollTimer.Stop();
                    }
                    UpdatePptShareLayer();
                    desk.ShowController.Refresh();
                }));
            } catch { }
        }

        /// <summary>
        /// PPT 放映模式：把「当前幻灯片页 + 网页端设为可见」的上传图片直接叠加显示在当前屏幕上，
        /// 不进入内置白板。换页时其它页图片自动消失，翻回来又重新出现（也可能来自自动保存的读回）。
        /// 优化：缓存 UI 元素，仅在图片集合真正变化时重建。
        /// </summary>
        private void UpdatePptShareLayer()
        {
            if (PptShareLayer == null) return;
            if (!desk.ShowController.IsPptMode) {
                PptShareLayer.Visibility = Visibility.Collapsed;
                PptShareLayer.Children.Clear();
                return;
            }
            ShowImage.Visibility = Visibility.Collapsed;
            int slide = desk.ShowController.PptSlide;
            var imgs = desk.ShowController.GetVisiblePptImages(slide);

            // 快速检测：如果图片集合未变化，直接返回
            if (_pptLayerSlide == slide && _pptLayerCount == imgs.Count && _pptLayerKeys.SequenceEqual(imgs.Select(i => i.data)))
                return;

            _pptLayerSlide = slide;
            _pptLayerCount = imgs.Count;
            _pptLayerKeys = imgs.Select(i => i.data).ToArray();

            PptShareLayer.Children.Clear();
            PptShareLayer.RowDefinitions.Clear();
            PptShareLayer.ColumnDefinitions.Clear();
            if (imgs.Count == 0) {
                PptShareLayer.Visibility = Visibility.Collapsed;
                return;
            }
            int cols = (int)Math.Ceiling(Math.Sqrt(imgs.Count));
            int rows = (int)Math.Ceiling((double)imgs.Count / cols);
            for (int r = 0; r < rows; r++)
                PptShareLayer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            for (int c = 0; c < cols; c++)
                PptShareLayer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int k = 0; k < imgs.Count; k++) {
                var data = imgs[k].data;
                if (!_pptImageCache.TryGet(data, out var bmp)) {
                    bmp = ShowDecode(data);
                    if (bmp != null) _pptImageCache.Add(data, bmp);
                }
                if (bmp == null) continue;
                var img = new System.Windows.Controls.Image {
                    Source = bmp,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(12),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                Grid.SetRow(img, k / cols);
                Grid.SetColumn(img, k % cols);
                PptShareLayer.Children.Add(img);
            }
            PptShareLayer.Visibility = Visibility.Visible;
        }

        private int _pptLayerSlide = -1;
        private int _pptLayerCount = -1;
        private byte[][] _pptLayerKeys = Array.Empty<byte[]>();

        // “图片进白板”：手机图片直接成为内置白板的一页（背景），翻白板页即翻图，
        // 用白板自带的页存储（TimeMachineHistories）保存每页标注，因此与白板的上/下/加/删完全融合。
        private Point? _showPanLast;                  // 鼠标拖拽平移的上一位置

        // 每页白板背景图（索引 = 白板页号，1 基）。null 表示空白页。手机图片按“页号-1”映射到 ShowController 中的图片。
        private readonly BitmapImage[] _wbPageImages = new BitmapImage[101];
        private int _imageCount;                      // 缓存的手机图片数量（避免每次输入都去抢 ShowController 的锁）
        private int _lastShowCount = -1;              // 上一次同步过的图片数量，用于检测增删
        private int _lastRenderedImageIndex = -1;     // 上一次渲染到的图片序号，避免无关状态变化把用户弹回图片页
        private bool _wbEntered;                      // 本次图片会话是否已强制切到白板（只强制一次，之后尊重用户手动切换模式）
        private (int count, int page) _decodeKey = (-1, -1); // 后台解码去重：同一页只起一个解码任务

        // 放映渲染合并：把一帧内的多次平移 / 缩放合并为一次实际重绘，避免鼠标狂移时
        // 每个鼠标消息都触发整屏重画而”卡顿”。最新状态缓存于 _showRenderState，
        // 由 CompositionTarget.Rendering（每帧一次）在需要时统一绘制。
        private bool _showRenderPending;
        private desk.ShowState _showRenderState;
        private bool _showRenderSubscribed;
        private readonly MatrixTransform _sharedRenderTransform = new MatrixTransform(); // 复用避免每帧分配

        /// <summary>当前是否处于”白板模式下的图片页”（图片可见、可缩放）。退出白板 / 鼠标模式下为 false。纯字段判断，不加锁。</summary>
        private bool IsWbImagePage
        {
            get
            {
                if (currentMode != 1 || GridBackgroundCover.Visibility != Visibility.Visible || Main_Grid.Background == Brushes.Transparent)
                    return false;
                int p = CurrentWhiteboardIndex;
                if (p < 1 || p >= _wbPageImages.Length) return false;
                int imgIdx = p - 1;
                return _wbPageImages[p] != null || imgIdx < _imageCount;
            }
        }

        private void ShowOnState(desk.ShowState s)
        {
            // 状态可能在 desk 的 WS / 上传线程上触发，必须切回 UI 线程
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<desk.ShowState>(ShowOnState), s);
                return;
            }

            // 放映模式下先用屏幕叠加层刷新 PPT 图片（换页显隐 / 网页端控制显隐都会走这里）。
            UpdatePptShareLayer();
            if (desk.ShowController.IsPptMode)
            {
                // PPT 放映模式：上传的图片直接显示在当前屏幕上，绝不强制切到内置白板；
                // 同时不要改写 PptNavigationTextBlock，保持放映模式原有的翻页等功能正常。
                // 顺带复位白板图片留下的共享变换，避免放映笔迹被旧矩阵偏移 / 旋转。
                HideShowImageLayer();
                return;
            }

            // 图片进白板：按“当前白板窗口”实际尺寸适配（非铺满物理屏幕），任意窗口下居中可见。
            var screenW = (int)Math.Max(Main_Grid.ActualWidth, 1);
            var screenH = (int)Math.Max(Main_Grid.ActualHeight, 1);
            desk.ShowController.SetScreenSize(screenW, screenH);
            s = desk.ShowController.GetState();

            // 以“是否还有图片”决定是否显示图片层，而非手机推送状态（open）：
            // 关闭手机推送后，图片仍作为白板页保留，可继续浏览 / 缩放 / 标注。
            _imageCount = s.count;
            if (_imageCount <= 0)
            {
                HideShowImageLayer();
                for (int p = 0; p < _wbPageImages.Length; p++) _wbPageImages[p] = null;
                _lastShowCount = -1;
                _lastRenderedImageIndex = -1;
                _wbEntered = false;
                return;
            }

            // 图片数量变化（增 / 删）：刷新白板页总数，并清空背景缓存按新索引重新解码
            if (_lastShowCount != s.count)
            {
                SyncWbCountToImages(s.count);
                _lastShowCount = s.count;
            }

            // 仅在首次进入图片会话时强制切到白板；之后尊重用户手动切换屏幕 / 白板模式
            if (!_wbEntered)
            {
                EnsureWhiteboardMode();
                _wbEntered = true;
            }

            // 仅当手机端真正切换了图片序号时才把白板跳到对应页；
            // 缩放 / 平移等无关变化不跳页，避免把用户从手动新建的空白页弹回图片页。
            if (s.index != _lastRenderedImageIndex)
            {
                _lastRenderedImageIndex = s.index;
                int targetPage = s.index + 1;
                if (targetPage < 1) targetPage = 1;
                if (targetPage > WhiteboardTotalCount) targetPage = WhiteboardTotalCount;
                if (targetPage != CurrentWhiteboardIndex)
                {
                    SaveStrokes();
                    ClearStrokes(true);
                    CurrentWhiteboardIndex = targetPage;
                    RestoreStrokes();
                    UpdateIndexInfoDisplay();
                }
            }

            ApplyWbPageImage();
            PptNavigationTextBlock.Text = $"{s.index + 1}/{s.count}";
        }

        /// <summary>把图片数量同步到白板页数：保证至少有 count 页，并清理越界页的背景图。</summary>
        private void SyncWbCountToImages(int count)
        {
            // 数量变化时清空所有背景缓存，按新索引重新懒解码（避免手机增删图片后索引前移导致图 / 页错位）
            for (int p = 0; p < _wbPageImages.Length; p++) _wbPageImages[p] = null;
            if (WhiteboardTotalCount < count) WhiteboardTotalCount = Math.Min(count, 99);
            if (CurrentWhiteboardIndex > WhiteboardTotalCount) CurrentWhiteboardIndex = WhiteboardTotalCount;
            UpdateIndexInfoDisplay();
        }

        /// <summary>确保白板可见（幂等）：唤出画板、切到白板模式，使白板页控件可见。不劫持“背景”按钮的切换能力。</summary>
        private void EnsureWhiteboardMode()
        {
            // 鼠标模式下先唤出画板（恢复画板背景，否则白板与页控件都不可见）
            if (Main_Grid.Background == Brushes.Transparent)
                Main_Grid.Background = new SolidColorBrush(StringToColor("#01FFFFFF"));
            if (painterCanvas.Visibility != Visibility.Visible)
                painterCanvas.Visibility = Visibility.Visible;

            if (currentMode != 1)
            {
                currentMode = 1;
                SaveStrokes(true);
                ClearStrokes(true);
                RestoreStrokes();
                StackPanelPPTButtons.Visibility = Visibility.Collapsed;
                Topmost = false;
                painterCanvas.EditingMode = InkCanvasEditingMode.Ink;
            }

            // 白板页控件受“遮罩持有者 + 遮罩”双重可见性门控，两者都要可见
            GridBackgroundCoverHolder.Visibility = Visibility.Visible;
            GridBackgroundCover.Visibility = Visibility.Visible;

            UpdateIndexInfoDisplay();
        }

        /// <summary>把当前白板页的背景图显示 / 隐藏，并按该页图片自身的缩放套用渲染变换（图片与笔迹一起缩放）。</summary>
        private void ApplyWbPageImage()
        {
            // 仅白板模式（画板可见 + 遮罩可见）显示图片背景；退出白板 / 鼠标模式则隐藏
            if (currentMode != 1 || GridBackgroundCover.Visibility != Visibility.Visible || Main_Grid.Background == Brushes.Transparent)
            {
                ShowImage.Visibility = Visibility.Collapsed;
                ResetPainterCanvasTransform();
                return;
            }
            int page = CurrentWhiteboardIndex;
            if (page < 1 || page >= _wbPageImages.Length)
            {
                ShowImage.Visibility = Visibility.Collapsed;
                ResetPainterCanvasTransform();
                return;
            }
            int imgIdx = page - 1;
            if (imgIdx >= _imageCount)
            {
                // 该页对应的手机图片已被移除：清缓存并按空白页处理，避免按 1:1 像素错误显示
                _wbPageImages[page] = null;
                ShowImage.Source = null;
                ShowImage.Visibility = Visibility.Collapsed;
                ResetPainterCanvasTransform();
                return;
            }
            var bmp = _wbPageImages[page];
            if (bmp == null)
            {
                // 该页对应手机图片但还没解码：后台解码（BeginInvoke 异步回 UI，避免同步等待放大卡顿）
                var key = (_imageCount, page);
                if (!_decodeKey.Equals(key))
                {
                    _decodeKey = key;
                    var data = desk.ShowController.GetImageInfo(imgIdx).data;
                    if (data != null)
                    {
                        var capturedPage = page;
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                var decoded = ShowDecode(data);
                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    // 图片字节流不会因翻页而变，直接按页缓存；再渲染“当前页”
                                    if (decoded != null) _wbPageImages[capturedPage] = decoded;
                                    _decodeKey = (-1, -1);
                                    ApplyWbPageImage();
                                }));
                            }
                            catch { }
                        });
                    }
                }
                ShowImage.Visibility = Visibility.Collapsed;
                ResetPainterCanvasTransform();
                return;
            }
            ShowImage.Source = bmp;
            ShowImage.Width = bmp.PixelWidth;
            ShowImage.Height = bmp.PixelHeight;
            ShowImage.Visibility = Visibility.Visible;
            ShowScheduleRender(desk.ShowController.GetStateForIndex(imgIdx));
        }

        /// <summary>白板页翻动后，让手机端当前图片序号与白板页保持同步（仅在确实是图片页时）。</summary>
        private void SyncShowControllerIndex()
        {
            int imgIdx = CurrentWhiteboardIndex - 1;
            if (imgIdx >= 0 && imgIdx < desk.ShowController.Count) desk.ShowController.Goto(imgIdx);
        }

        /// <summary>隐藏图片背景层并复位笔迹变换，但不清除 _wbPageImages（图片仍作为白板页保留，翻回即现）。</summary>
        private void HideShowImageLayer()
        {
            if (_showRenderSubscribed)
            {
                CompositionTarget.Rendering -= ShowOnRenderFrame;
                _showRenderSubscribed = false;
            }
            _showRenderPending = false;
            _showRenderState = null;
            ShowImage.Visibility = Visibility.Collapsed;
            ResetPainterCanvasTransform();
        }

        // 图片作为“内置白板”的一页（背景），不再有独立的进入 / 退出层逻辑：
        // 图片的进入即“新增白板页背景”，退出图片会话只是结束手机推送，图片仍作为白板页保留可继续标注。

        private void ShowRender(desk.ShowState s)
        {
            var m = ShowComputeMatrix(s.imgW, s.imgH, s.zoom, s.offX, s.offY, s.angle, s.screenW, s.screenH);
            _sharedRenderTransform.Matrix = m;
            // 复用同一个 MatrixTransform 实例，避免每帧分配
            ShowImage.RenderTransform = _sharedRenderTransform;
            painterCanvas.RenderTransform = _sharedRenderTransform;
        }

        // 放映时把画板原有的双指手势转接到图片视图：平移 / 以双指中心为锚点缩放。
        // 与鼠标滚轮 / 拖拽走同一套 ShowController 状态，因此图片与笔迹一起平移、缩放。
        private void ShowApplyManipulation(ManipulationDeltaEventArgs e)
        {
            var md = e.DeltaManipulation;
            if (Settings.Gesture.IsEnableTwoFingerTranslate)
                desk.ShowController.Pan((int)Math.Round(md.Translation.X), (int)Math.Round(md.Translation.Y));
            if (Settings.Gesture.IsEnableTwoFingerZoom && md.Scale.X > 0 && md.Scale.Y > 0)
            {
                double factor = (md.Scale.X + md.Scale.Y) / 2.0;
                var o = e.ManipulationOrigin;
                double cx = Main_Grid.ActualWidth > 0 ? o.X / Main_Grid.ActualWidth : 0.5;
                double cy = Main_Grid.ActualHeight > 0 ? o.Y / Main_Grid.ActualHeight : 0.5;
                desk.ShowController.Zoom(factor, cx, cy);
            }
            // 双指旋转：把角度累加进与墨迹共用的矩阵，图片与标注同步旋转。
            // 图片页的手势被这里接管（不再逐条改笔画点），所以旋转不会破坏图片与墨迹的相对位置。
            if (Math.Abs(md.Rotation) > 0.0001)
                desk.ShowController.Rotate(md.Rotation);
            e.Handled = true;
        }

        // 把最新状态缓存起来，交给每帧一次的渲染回调统一绘制（同一帧内的多次 Pan/Zoom 只画一次）。
        private void ShowScheduleRender(desk.ShowState s)
        {
            _showRenderState = s;
            _showRenderPending = true;
            if (!_showRenderSubscribed)
            {
                _showRenderSubscribed = true;
                CompositionTarget.Rendering += ShowOnRenderFrame;
            }
        }

        // 每帧最多重绘一次：鼠标在相邻两帧之间产生的大量平移 / 缩放，只取最新状态绘制一帧。
        private void ShowOnRenderFrame(object sender, EventArgs e)
        {
            if (!_showRenderPending) return;
            _showRenderPending = false;
            if (_showRenderState != null) ShowRender(_showRenderState);
        }

        private static Matrix ShowComputeMatrix(int imgW, int imgH, double zoom, int offX, int offY, double angle, int sw, int sh)
        {
            if (imgW <= 0 || imgH <= 0 || sw <= 0 || sh <= 0) return Matrix.Identity;
            double fit = Math.Min((double)sw / imgW, (double)sh / imgH);
            double scale = fit * zoom;
            double tx = sw / 2.0 + offX - imgW * scale / 2.0;
            double ty = sh / 2.0 + offY - imgH * scale / 2.0;
            var m = new Matrix();
            m.Scale(scale, scale);
            m.Translate(tx, ty);
            if (Math.Abs(angle) > 0.0001) m.RotateAt(angle, sw / 2.0, sh / 2.0);
            return m;
        }

        private static BitmapImage ShowDecode(byte[] data)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(data, false);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private void BtnShowPrev_Click(object sender, RoutedEventArgs e) => desk.ShowController.Prev();
        private void BtnShowNext_Click(object sender, RoutedEventArgs e) => desk.ShowController.Next();
        private void BtnShowExit_Click(object sender, RoutedEventArgs e) => desk.ShowController.Close();

        // 电脑端：滚轮以光标为锚点缩放；鼠标拖拽平移（笔 / 触控仍用于标注）
        private void MainWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!IsWbImagePage) return;
            double factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
            var p = e.GetPosition(this);
            double cx = ActualWidth > 0 ? p.X / ActualWidth : 0.5;
            double cy = ActualHeight > 0 ? p.Y / ActualHeight : 0.5;
            desk.ShowController.Zoom(factor, cx, cy);
            e.Handled = true;
        }

        private void MainWindow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsWbImagePage || e.StylusDevice != null) return; // 仅鼠标拖拽平移，保留笔迹标注
            // 落在控件（浮动工具条 / PPT 按钮等）上的鼠标不触发平移，避免拖动控件栏时图片跟着动
            var src = e.OriginalSource as FrameworkElement;
            if (src != painterCanvas && src != ShowImage && src != GridBackgroundCover) return;
            _showPanLast = e.GetPosition(this);
        }

        private void MainWindow_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!IsWbImagePage || !_showPanLast.HasValue || e.StylusDevice != null) return;
            var p = e.GetPosition(this);
            double dx = p.X - _showPanLast.Value.X;
            double dy = p.Y - _showPanLast.Value.Y;
            if (dx != 0 || dy != 0)
            {
                desk.ShowController.Pan((int)Math.Round(dx), (int)Math.Round(dy));
                _showPanLast = p;
            }
        }

        private void MainWindow_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _showPanLast = null;
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (IsWbImagePage)
            {
                desk.ShowController.SetScreenSize((int)Math.Round(ActualWidth), (int)Math.Round(ActualHeight));
                ShowOnState(desk.ShowController.GetState());
            }
        }

        #endregion

        #endregion

        #region Settings

        #region Behavior

        private void ToggleSwitchRunAtStartup_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            if (ToggleSwitchRunAtStartup.IsOn)
            {
                StartAutomaticallyCreate("Painter");
            }
            else
            {
                StartAutomaticallyDel("Painter");
            }
        }

        private void ToggleSwitchSupportPowerPoint_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;

            Settings.PowerPointSettings.PowerPointSupport = ToggleSwitchSupportPowerPoint.IsOn;
            SaveSettingsToFile();

            if (Settings.PowerPointSettings.PowerPointSupport)
            {
                TryStartPptTimer();
            }
            else
            {
                TryStopPptTimer();
            }
        }

        private void ToggleSwitchShowCanvasAtNewSlideShow_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;

            Settings.PowerPointSettings.IsShowCanvasAtNewSlideShow = ToggleSwitchShowCanvasAtNewSlideShow.IsOn;
            SaveSettingsToFile();
        }

        #endregion

        #region Startup

        private void ToggleSwitchAutoHideCanvas_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;

            Settings.Startup.IsAutoHideCanvas = ToggleSwitchAutoHideCanvas.IsOn;
            SaveSettingsToFile();
        }

        private void ToggleSwitchAutoEnterModeFinger_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;

            Settings.Startup.IsAutoEnterModeFinger = ToggleSwitchAutoEnterModeFinger.IsOn;
            SaveSettingsToFile();
        }

        #endregion

        #region Appearance


        private void SideControlOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {

        }

        private void ToggleSwitchShowButtonExit_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;

            Settings.Appearance.IsShowExitButton = ToggleSwitchShowButtonExit.IsOn;
            SaveSettingsToFile();

            if (ToggleSwitchShowButtonExit.IsOn)
            {
                BtnExit.Visibility = Visibility.Visible;
            }
            else
            {
                BtnExit.Visibility = Visibility.Collapsed;
            }
        }

        private void ToggleSwitchShowButtonEraser_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;

            Settings.Appearance.IsShowEraserButton = ToggleSwitchShowButtonEraser.IsOn;
            SaveSettingsToFile();

            if (ToggleSwitchShowButtonEraser.IsOn)
            {
                BtnErase.Visibility = Visibility.Visible;
            }
            else
            {
                BtnErase.Visibility = Visibility.Collapsed;
            }
        }
        private void ToggleSwitchShowButtonPPTNavigation_OnToggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;

            Settings.PowerPointSettings.IsShowPPTNavigation = ToggleSwitchShowButtonPPTNavigation.IsOn;
            SaveSettingsToFile();

            PptNavigationBtn.Visibility =
                Settings.PowerPointSettings.IsShowPPTNavigation ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool isApplyingThemeSegment;

        /// <summary>把 Settings.Appearance.Theme（0 浅色 / 1 深色 / 2 跟随系统）反映到主题分段控件上。</summary>
        private void ApplyThemeSegmentSelection()
        {
            isApplyingThemeSegment = true;
            try
            {
                switch (Settings.Appearance.Theme)
                {
                    case 1: RadioButtonThemeDark.IsChecked = true; break;
                    case 2: RadioButtonThemeSystem.IsChecked = true; break;
                    default: RadioButtonThemeLight.IsChecked = true; break;
                }
            }
            finally
            {
                isApplyingThemeSegment = false;
            }
        }

        private void RadioButtonThemeSegment_Checked(object sender, RoutedEventArgs e)
        {
            if (!isLoaded || isApplyingThemeSegment) return;

            Settings.Appearance.Theme = int.Parse((string)((RadioButton)sender).Tag);
            SystemEvents_UserPreferenceChanged(null, null);
            SaveSettingsToFile();
        }

        private bool isApplyingPptNavPos;

        /// <summary>把翻页按钮位置设置（0=两侧底部 / 1=两侧中部）反映到分段控件与左右两个 Viewbox。</summary>
        private void ApplyPptNavButtonPosition()
        {
            isApplyingPptNavPos = true;
            try
            {
                int pos = Settings.PowerPointSettings.PptNavButtonPosition;
                if (pos == 1)
                {
                    // 两侧居中：按钮竖着排，一行一个。
                    RadioButtonPptNavPosMiddle.IsChecked = true;
                    ViewboxPPTSidesControl.VerticalAlignment = VerticalAlignment.Center;
                    ViewboxPPTSidesControlRight.VerticalAlignment = VerticalAlignment.Center;
                    PptNavStackLeft.Orientation = Orientation.Vertical;
                    PptNavStackRight.Orientation = Orientation.Vertical;
                    // Viewbox 固定 Height=50 会把竖排的三个按钮压扁，放宽到三项高度。
                    ViewboxPPTSidesControl.Height = 132;
                    ViewboxPPTSidesControlRight.Height = 132;
                }
                else
                {
                    // 两侧底部：保持原有横排，一行显示完所有按钮。
                    RadioButtonPptNavPosBottom.IsChecked = true;
                    ViewboxPPTSidesControl.VerticalAlignment = VerticalAlignment.Bottom;
                    ViewboxPPTSidesControlRight.VerticalAlignment = VerticalAlignment.Bottom;
                    PptNavStackLeft.Orientation = Orientation.Horizontal;
                    PptNavStackRight.Orientation = Orientation.Horizontal;
                    ViewboxPPTSidesControl.Height = 50;
                    ViewboxPPTSidesControlRight.Height = 50;
                }
            }
            finally
            {
                isApplyingPptNavPos = false;
            }
        }

        private void RadioButtonPptNavPosSegment_Checked(object sender, RoutedEventArgs e)
        {
            if (!isLoaded || isApplyingPptNavPos) return;

            Settings.PowerPointSettings.PptNavButtonPosition = int.Parse((string)((RadioButton)sender).Tag);
            ApplyPptNavButtonPosition();
            SaveSettingsToFile();
        }

        private void ToggleSwitchPptThumbnail_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;

            Settings.PowerPointSettings.PptThumbnailEnabled = ToggleSwitchPptThumbnail.IsOn;
            SaveSettingsToFile();
            // 关闭时若缩略图总览正展开则收起
            if (!ToggleSwitchPptThumbnail.IsOn) ClosePptThumbnailOverview();
        }

        #region PPT 幻灯片缩略图总览

        private const string PptThumbCacheFolder = "painter-ppt-thumbs";

        // 缩略图侧栏当前停靠侧：true=右侧、false=左侧（由点击的页码按钮决定）。
        private bool _pptThumbOnRight = false;

        // 当前缓存对应的文稿身份键。切换 PPT 后键会变化，从而彻底隔离不同文稿的缩略图；
        // 键由「文稿名 + 页数 + 完整路径 + 文件修改时间」哈希而成，同一文稿重开仍命中磁盘缓存。
        private string _pptThumbDeckKey = "";
        // 每次展开/收起自增；后台导出线程据此丢弃过期结果，避免切文稿/收起后旧图回填。
        private volatile int _pptThumbGeneration;
        private int _pptThumbCurrentPage;
        private readonly Dictionary<int, Border> _pptThumbCards = new Dictionary<int, Border>();

        private string GetPptThumbFolder()
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), PptThumbCacheFolder);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
            catch { return null; }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "deck";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        /// <summary>
        /// 读取当前「正在放映」的文稿信息。始终优先取实时放映窗口，
        /// 避免切换 PPT 后仍使用旧的 presentation 字段而导出上一个文稿的画面。
        /// </summary>
        private bool TryGetLivePptDeck(out bool isWps, out int count, out string name, out string fullPath)
        {
            isWps = _isWpsMode && pptApplication == null;
            count = 0; name = "deck"; fullPath = "";
            try
            {
                if (isWps)
                {
                    dynamic wps = _wpsApp ?? GetWpsApplication();
                    if (wps != null)
                    {
                        count = Convert.ToInt32(wps.Slides.Count);
                        if (wps.ActivePresentation != null)
                        {
                            name = Convert.ToString(wps.ActivePresentation.Name);
                            try { fullPath = Convert.ToString(wps.ActivePresentation.FullName); } catch { }
                        }
                    }
                }
                else if (pptApplication != null && pptApplication.SlideShowWindows.Count > 0)
                {
                    var pres = pptApplication.SlideShowWindows[1].Presentation;
                    count = pres.Slides.Count;
                    name = pres.Name;
                    try { fullPath = pres.FullName; } catch { }
                }
                else if (presentation != null)
                {
                    count = presentation.Slides.Count;
                    name = presentation.Name;
                    try { fullPath = presentation.FullName; } catch { }
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile("PptThumb deck: " + ex, LogHelper.LogType.Error);
            }
            return count > 0;
        }

        /// <summary>由文稿名/页数/路径/修改时间生成稳定的缓存目录名：不同文稿互不复用，同一文稿重开仍命中缓存。</summary>
        private static string BuildPptDeckKey(string name, string fullPath, int count)
        {
            string seed = (name ?? "deck") + "|" + count + "|" + (fullPath ?? "");
            if (!string.IsNullOrEmpty(fullPath))
            {
                try
                {
                    var fi = new FileInfo(fullPath);
                    if (fi.Exists) seed += "|" + fi.LastWriteTimeUtc.Ticks;
                }
                catch { }
            }
            int h = 17;
            unchecked { foreach (char c in seed) h = (h * 31) + c; }
            return SanitizeFileName(name) + "_" + count + "_" + ((uint)h).ToString("X8");
        }

        private void PptThumbnailOverviewBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == sender) ClosePptThumbnailOverview();
        }

        private void PptThumbnailOverviewClose_Click(object sender, RoutedEventArgs e)
        {
            ClosePptThumbnailOverview();
        }

        private void ClosePptThumbnailOverview()
        {
            // 收起即作废当前批次，并清空内容，避免切到别的 PPT 后残留上一份的缩略图。
            _pptThumbGeneration++;
            _pptThumbDeckKey = "";
            _pptThumbCards.Clear();
            PptThumbnailOverview.Visibility = Visibility.Collapsed;
            PptThumbPanelLeft.Visibility = Visibility.Collapsed;
            PptThumbPanelRight.Visibility = Visibility.Collapsed;
            PptThumbnailWrapPanel.Children.Clear();
            PptThumbnailWrapPanelRight.Children.Clear();
        }

        /// <summary>
        /// 在指定侧展开幻灯片缩略图侧栏。返回 false 表示当前没有可读取的放映文稿，
        /// 调用方据此回退到 PowerPoint 原生导航窗格。
        /// </summary>
        private bool OpenPptThumbnailOverview(bool onRight)
        {
            // 同一侧再次点击则收起；换侧点击则把上一侧的内容清空后在新侧重开。
            if (PptThumbnailOverview.Visibility == Visibility.Visible && _pptThumbOnRight == onRight)
            {
                ClosePptThumbnailOverview();
                return true;
            }
            if (PptThumbnailOverview.Visibility == Visibility.Visible)
                ClosePptThumbnailOverview();

            // 始终读取「正在放映」的文稿，避免用旧的 presentation 字段导出上一个 PPT 的画面。
            bool isWps;
            int count;
            string deckName, fullPath;
            if (!TryGetLivePptDeck(out isWps, out count, out deckName, out fullPath)) return false;

            // COM/WPS 晚绑定属性理论上可能返回异常大的页数，一次性建上万张卡片会冻结 UI
            // 甚至耗尽内存。设置合理上限：正常课件远小于此，超出则只渲染前面部分。
            const int MaxThumbPages = 2000;
            if (count <= 0) return false;
            if (count > MaxThumbPages) count = MaxThumbPages;

            string deckKey = BuildPptDeckKey(deckName, fullPath, count);
            string folder = Path.Combine(GetPptThumbFolder() ?? Path.GetTempPath(), deckKey);
            try { if (!Directory.Exists(folder)) Directory.CreateDirectory(folder); }
            catch { folder = null; }

            int currentPage = GetCurrentPptPage(isWps);
            int gen = ++_pptThumbGeneration;
            _pptThumbCurrentPage = currentPage;
            _pptThumbDeckKey = deckKey;
            _pptThumbOnRight = onRight;

            PptThumbnailOverview.Visibility = Visibility.Visible;
            PptThumbPanelLeft.Visibility = onRight ? Visibility.Collapsed : Visibility.Visible;
            PptThumbPanelRight.Visibility = onRight ? Visibility.Visible : Visibility.Collapsed;

            var targetPanel = onRight ? PptThumbnailWrapPanelRight : PptThumbnailWrapPanel;
            var scroll = onRight ? PptThumbScrollRight : PptThumbScrollLeft;
            targetPanel.Children.Clear();
            _pptThumbCards.Clear();

            // 立即铺好全部占位卡片（只有页码），侧栏瞬间可见；图片随后逐张填充。
            // 这是「第一次打开极慢」的核心优化：不再等所有页都 COM 导出完才显示界面。
            Border currentCard = null;
            for (int i = 1; i <= count; i++)
            {
                Border card = BuildPptThumbCard(i, i == currentPage);
                if (i == currentPage) currentCard = card;
                _pptThumbCards[i] = card;
                targetPanel.Children.Add(card);
            }

            if (currentCard != null)
            {
                // 布局尚未完成时 TransformToVisual 会抛异常；缩略图定位属于锦上添花，
                // 失败只跳过滚动，绝不能让它冒泡把整段打开流程打断。
                try
                {
                    scroll.UpdateLayout();
                    targetPanel.UpdateLayout();
                    double cardTop = currentCard.TransformToVisual(targetPanel).Transform(new Point(0, 0)).Y;
                    double target = Math.Max(0, cardTop + currentCard.ActualHeight / 2 - scroll.ViewportHeight / 2);
                    scroll.ScrollToVerticalOffset(target);
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile("PptThumb scroll: " + ex.Message, LogHelper.LogType.Error);
                }
            }

            // 导出顺序：当前页优先，再向两侧扩展，让用户最先看到的页先出图。
            var order = new List<int>();
            for (int d = 0; d < count; d++)
            {
                int p = currentPage + d;
                if (p >= 1 && p <= count) order.Add(p);
                if (d > 0)
                {
                    int q = currentPage - d;
                    if (q >= 1 && q <= count) order.Add(q);
                }
            }

            bool targetRight = onRight;
            bool wpsMode = isWps;
            Thread worker = new Thread(() =>
            {
                try
                {
                    foreach (int i in order)
                    {
                        if (gen != _pptThumbGeneration) return; // 已收起/切换文稿，作废本批次
                        string path = folder == null ? null : Path.Combine(folder, "slide_" + i + ".png");
                        bool ok = path != null && File.Exists(path) && new FileInfo(path).Length > 0;
                        if (!ok && path != null)
                        {
                            try
                            {
                                if (wpsMode)
                                {
                                    dynamic wpsApp = _wpsApp ?? GetWpsApplication();
                                    if (wpsApp != null && wpsApp.ActivePresentation != null
                                        && Convert.ToInt32(wpsApp.ActivePresentation.Slides.Count) >= i)
                                        wpsApp.ActivePresentation.Slides[i].Export(path, "PNG", 240, 135);
                                }
                                else
                                {
                                    // 优先用实时放映窗口的文稿；旧 presentation 字段可能属于上一个 PPT。
                                    Microsoft.Office.Interop.PowerPoint.Slide s = null;
                                    if (pptApplication != null && pptApplication.SlideShowWindows.Count > 0)
                                        s = pptApplication.SlideShowWindows[1].Presentation.Slides[i];
                                    else if (presentation != null) s = presentation.Slides[i];
                                    if (s != null) s.Export(path, "PNG", 240, 135);
                                }
                                ok = File.Exists(path) && new FileInfo(path).Length > 0;
                            }
                            catch (Exception ex)
                            {
                                LogHelper.WriteLogToFile("PptThumb export " + i + ": " + ex, LogHelper.LogType.Error);
                            }
                        }
                        if (ok && gen == _pptThumbGeneration)
                        {
                            string captured = path;
                            // 用异步回填，避免持有 COM/STA 的工作线程等 UI 线程、而 UI 线程
                            // 又在做 COM 调用时形成双向死锁（界面卡死）。
                            SafeUiInvokeAsync(() =>
                            {
                                if (gen == _pptThumbGeneration) FillPptThumbCard(i, captured, targetRight);
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile("PptThumb worker: " + ex, LogHelper.LogType.Error);
                }
            });
            worker.SetApartmentState(ApartmentState.STA);
            worker.IsBackground = true;
            worker.Start();
            return true;
        }

        /// <summary>取当前放映页码，用于高亮/居中/优先导出。始终读实时放映窗口。</summary>
        private int GetCurrentPptPage(bool isWps)
        {
            try
            {
                if (isWps)
                {
                    dynamic wpsApp = _wpsApp ?? GetWpsApplication();
                    if (wpsApp != null && wpsApp.SlideShowWindows.Count > 0)
                        return Convert.ToInt32(wpsApp.SlideShowWindows[1].View.CurrentShowPosition);
                }
                else if (pptApplication != null && pptApplication.SlideShowWindows.Count > 0)
                {
                    return pptApplication.SlideShowWindows[1].View.CurrentShowPosition;
                }
            }
            catch { }
            return 1;
        }

        /// <summary>构造一张只有页码的占位卡片（图片稍后异步填充）。</summary>
        private Border BuildPptThumbCard(int page, bool isCurrent)
        {
            Border card = new Border
            {
                Width = 176, Height = 104,
                Margin = new Thickness(4, 5, 4, 5),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(isCurrent ? Color.FromArgb(255, 38, 88, 150)
                                                          : Color.FromArgb(255, 30, 30, 30)),
                BorderBrush = new SolidColorBrush(isCurrent ? Color.FromArgb(255, 90, 170, 255)
                                                           : Color.FromArgb(255, 90, 90, 90)),
                BorderThickness = new Thickness(isCurrent ? 2 : 1),
                Cursor = Cursors.Hand,
                Tag = page,
                ToolTip = "跳转到第 " + page + " 页"
            };
            Grid content = new Grid();
            content.Children.Add(new TextBlock
            {
                Text = "第 " + page + " 页",
                Foreground = Brushes.White,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            content.Children.Add(BuildPptThumbBadge(page));
            card.Child = content;
            card.MouseUp += PptThumbnailItem_MouseUp;
            return card;
        }

        private static Border BuildPptThumbBadge(int page)
        {
            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)),
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(4),
                Padding = new Thickness(5, 1, 5, 1),
                Child = new TextBlock { Text = page.ToString(), Foreground = Brushes.White, FontSize = 12 }
            };
        }

        /// <summary>把已导出的图片填入对应页码的卡片（保留页码角标）。</summary>
        private void FillPptThumbCard(int page, string imagePath, bool onRight)
        {
            Border card;
            if (!_pptThumbCards.TryGetValue(page, out card)) return;
            var targetPanel = onRight ? PptThumbnailWrapPanelRight : PptThumbnailWrapPanel;
            if (!targetPanel.Children.Contains(card)) return;
            Grid content = card.Child as Grid;
            if (content == null) return;
            try
            {
                BitmapImage bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad; // 立即解码，释放文件句柄
                bmp.UriSource = new Uri(imagePath, UriKind.Absolute);
                bmp.EndInit();
                content.Children.Clear();
                content.Children.Add(new System.Windows.Controls.Image
                {
                    Source = bmp,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(2)
                });
                content.Children.Add(BuildPptThumbBadge(page));
            }
            catch { }
        }

        private void PptThumbnailItem_MouseUp(object sender, MouseButtonEventArgs e)
        {
            Border card = sender as Border;
            if (card == null || card.Tag == null) return;
            int page;
            if (!int.TryParse(card.Tag.ToString(), out page) || page < 1) return;
            ClosePptThumbnailOverview();
            PptGotoSlide(page);
        }

        private void PptGotoSlide(int page)
        {
            bool isWps = _isWpsMode && pptApplication == null;
            Thread worker = new Thread(() =>
            {
                try
                {
                    if (isWps)
                    {
                        dynamic app = _wpsApp ?? GetWpsApplication();
                        if (app != null) app.SlideShowWindows[1].View.GotoSlide(page);
                    }
                    else if (pptApplication != null && pptApplication.SlideShowWindows.Count > 0)
                    {
                        pptApplication.SlideShowWindows[1].View.GotoSlide(page);
                    }
                    else if (presentation != null)
                    {
                        presentation.SlideShowWindow.View.GotoSlide(page);
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile("PptGotoSlide: " + ex, LogHelper.LogType.Error);
                }
            });
            worker.SetApartmentState(ApartmentState.STA);
            worker.IsBackground = true;
            worker.Start();
        }

        #endregion


        private void ToggleSwitchShowButtonHideControl_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;

            Settings.Appearance.IsShowHideControlButton = ToggleSwitchShowButtonHideControl.IsOn;
            SaveSettingsToFile();

            if (ToggleSwitchShowButtonHideControl.IsOn)
            {
                BtnHideControl.Visibility = Visibility.Visible;
            }
            else
            {
                BtnHideControl.Visibility = Visibility.Collapsed;
            }
        }

        private void ToggleSwitchShowButtonLRSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;

            Settings.Appearance.IsShowLRSwitchButton = ToggleSwitchShowButtonLRSwitch.IsOn;
            SaveSettingsToFile();

            if (ToggleSwitchShowButtonLRSwitch.IsOn)
            {
                BtnSwitchSide.Visibility = Visibility.Visible;
            }
            else
            {
                BtnSwitchSide.Visibility = Visibility.Collapsed;
            }
        }

        private void ToggleSwitchShowButtonModeFinger_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;

            Settings.Appearance.IsShowModeFingerToggleSwitch = ToggleSwitchShowButtonModeFinger.IsOn;
            SaveSettingsToFile();

            if (ToggleSwitchShowButtonModeFinger.IsOn)
            {
                StackPanelModeFinger.Visibility = Visibility.Visible;
            }
            else
            {
                StackPanelModeFinger.Visibility = Visibility.Collapsed;
            }
        }

        private void ToggleSwitchTransparentButtonBackground_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;

            Settings.Appearance.IsTransparentButtonBackground = ToggleSwitchTransparentButtonBackground.IsOn;
            if (Settings.Appearance.IsTransparentButtonBackground)
            {
                BtnExit.Background = new SolidColorBrush(StringToColor("#7F909090"));
            }
            else
            {
                if (BtnSwitchTheme.Content.ToString() == "深色")
                {
                    //Light
                    BtnExit.Background = new SolidColorBrush(StringToColor("#FFCCCCCC"));
                }
                else
                {
                    //Dark
                    BtnExit.Background = new SolidColorBrush(StringToColor("#FF555555"));
                }
            }

            SaveSettingsToFile();
        }

        private void ToggleSwitchShowCursor_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;

            Settings.Canvas.IsShowCursor = ToggleSwitchShowCursor.IsOn;
            painterCanvas_EditingModeChanged(painterCanvas, null);

            SaveSettingsToFile();
        }

        #endregion

        #region Canvas

        private void ComboBoxPenStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.Canvas.InkStyle = ComboBoxPenStyle.SelectedIndex;
            SaveSettingsToFile();
        }

        private void ComboBoxEraserSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.Canvas.EraserSize = ComboBoxEraserSize.SelectedIndex;
            SaveSettingsToFile();
        }


        private void ComboBoxEraserType_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.Canvas.EraserType = ComboBoxEraserType.SelectedIndex;
            SaveSettingsToFile();
        }

        private void InkWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!isLoaded) return;

            drawingAttributes.Height = ((Slider)sender).Value / 2;
            drawingAttributes.Width = ((Slider)sender).Value / 2;

            Settings.Canvas.InkWidth = ((Slider)sender).Value / 2;

            SaveSettingsToFile();
        }

        private void ComboBoxHyperbolaAsymptoteOption_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.Canvas.HyperbolaAsymptoteOption = (OptionalOperation)ComboBoxHyperbolaAsymptoteOption.SelectedIndex;
            SaveSettingsToFile();
        }

        #endregion

        #region Automation

        private void ToggleSwitchAutoSaveStrokesAtScreenshot_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.Automation.IsAutoSaveStrokesAtScreenshot = ToggleSwitchAutoSaveStrokesAtScreenshot.IsOn;
            ToggleSwitchAutoSaveStrokesAtClear.Header =
                ToggleSwitchAutoSaveStrokesAtScreenshot.IsOn ? "清屏时自动截图并保存墨迹" : "清屏时自动截图";
            SaveSettingsToFile();
        }

        private void ToggleSwitchAutoSaveStrokesAtClear_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.Automation.IsAutoSaveStrokesAtClear = ToggleSwitchAutoSaveStrokesAtClear.IsOn;
            SaveSettingsToFile();
        }


        private void ToggleSwitchExitingWritingMode_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.Automation.IsAutoClearWhenExitingWritingMode = ToggleSwitchClearExitingWritingMode.IsOn;
            SaveSettingsToFile();
        }

        // 移植自原版 Painter 的「自动查杀希沃」：两个开关分别对应
        // PPT 小工具组（PPTService / SeewoIwbAssistant / Sia.Guard）与希沃白板 5（EasiNote）。
        // 复用了 desk.ProcessKiller 的单一 5 秒定时器，不再另起一套轮询。
        private void ToggleSwitchAutoKillPptService_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.Automation.IsAutoKillPptService = ToggleSwitchAutoKillPptService.IsOn;
            ApplySeewoAutoKillSettings();
            SaveSettingsToFile();
        }

        private void ToggleSwitchAutoKillEasiNote_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.Automation.IsAutoKillEasiNote = ToggleSwitchAutoKillEasiNote.IsOn;
            ApplySeewoAutoKillSettings();
            SaveSettingsToFile();
        }

        private void ApplySeewoAutoKillSettings()
        {
            try
            {
                desk.ProcessKiller.SetSeewoOptions(
                    Settings.Automation.IsAutoKillPptService,
                    Settings.Automation.IsAutoKillEasiNote);
            }
            catch { }
        }

        /// <summary>
        /// 查杀成功回调（运行在 ProcessKiller 的线程池定时器上）。
        /// 原版在每次命中 EasiNote 时都弹 MessageBox 并切换画板模式，5 秒一次会持续打扰；
        /// 这里由 ProcessKiller 保证同一批名单只回调一次，并改用轻量通知。
        /// </summary>
        private void OnProcessesKilled(string[] names)
        {
            if (names == null || !names.Contains("EasiNote")) return;
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (currentMode == 0) BtnSwitch_Click(null, null);
                    ShowNotification("“希沃白板 5”已自动关闭");
                }));
            }
            catch { }
        }

        /// <summary>
        /// 希沃红外触摸框上报的 TouchPoint.Bounds 普遍偏大，笔尖常被误判成橡皮。
        /// 原版要求用户自己去「高级」里打开「特殊屏幕模式」，这里检测到希沃硬件就自动打开。
        /// 用户手动改过该开关后（IsAutoDetectSeewo=false）不再自动覆盖，倍率设置也完全保留。
        /// </summary>
        private void ApplySeewoScreenCompatibility()
        {
            if (!SeewoTuning.IsSeewo || !Settings.Advanced.IsAutoDetectSeewo) return;
            bool changed = false;
            if (!Settings.Advanced.IsSpecialScreen)
            {
                Settings.Advanced.IsSpecialScreen = true;
                // isLoaded 此时仍为 false，Toggled 会直接返回，不会把 IsAutoDetectSeewo 置 false
                ToggleSwitchIsSpecialScreen.IsOn = true;
                TouchMultiplierSlider.Visibility = Visibility.Visible;
                changed = true;
            }
            // 希沃一体机几乎都是四边红外触摸框：接触面在 WPF 里是个椭圆，
            // 用几何均值 sqrt(W*H) 估计“接触大小”比单边 Bounds.Width 更准，
            // 能进一步减少“笔尖被误判成橡皮”的误触。检测到希沃触摸设备就一并自动打开。
            if (SeewoTuning.HasSeewoTouch && !Settings.Advanced.IsQuadIR)
            {
                Settings.Advanced.IsQuadIR = true;
                ToggleSwitchIsQuadIR.IsOn = true;
                changed = true;
            }
            if (changed)
            {
                SaveSettingsToFile();
                LogHelper.WriteLogToFile("希沃兼容：已自动启用特殊屏幕触摸校正"
                    + (SeewoTuning.HasSeewoTouch ? "（含四边红外模式）" : ""), LogHelper.LogType.Event);
            }
        }

        private void ToggleSwitchHideStrokeWhenSelecting_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.Canvas.HideStrokeWhenSelecting = ToggleSwitchHideStrokeWhenSelecting.IsOn;
            SaveSettingsToFile();
        }

        private void ToggleSwitchUsingWhiteboard_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.Canvas.UsingWhiteboard = ToggleSwitchUsingWhiteboard.IsOn;
            if (!Settings.Canvas.UsingWhiteboard)
            {
                BtnSwitchTheme.Content = "浅色";
            }
            else
            {
                BtnSwitchTheme.Content = "深色";
            }
            BtnSwitchTheme_Click(sender, e);
            SaveSettingsToFile();
        }

        private void ToggleSwitchAutoSaveStrokesInPowerPoint_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.PowerPointSettings.IsAutoSaveStrokesInPowerPoint = ToggleSwitchAutoSaveStrokesInPowerPoint.IsOn;
            SaveSettingsToFile();
        }

        private void ToggleSwitchNotifyPreviousPage_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.PowerPointSettings.IsNotifyPreviousPage = ToggleSwitchNotifyPreviousPage.IsOn;
            SaveSettingsToFile();
        }

        private void ToggleSwitchNotifyHiddenPage_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.PowerPointSettings.IsNotifyHiddenPage = ToggleSwitchNotifyHiddenPage.IsOn;
            SaveSettingsToFile();
        }

        private void ToggleSwitchNoStrokeClearInPowerPoint_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.PowerPointSettings.IsNoClearStrokeOnSelectWhenInPowerPoint = ToggleSwitchNoStrokeClearInPowerPoint.IsOn;
            SaveSettingsToFile();
        }


        private void ToggleSwitchShowStrokeOnSelectInPowerPoint_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.PowerPointSettings.IsShowStrokeOnSelectInPowerPoint = ToggleSwitchShowStrokeOnSelectInPowerPoint.IsOn;
            SaveSettingsToFile();
        }

        private void SideControlMinimumAutomationSlider_ValueChanged(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.Automation.MinimumAutomationStrokeNumber = (int)SideControlMinimumAutomationSlider.Value;
            SaveSettingsToFile();
        }

        private void ToggleSwitchAutoSaveScreenShotInPowerPoint_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.PowerPointSettings.IsAutoSaveScreenShotInPowerPoint = ToggleSwitchAutoSaveScreenShotInPowerPoint.IsOn;
            SaveSettingsToFile();
        }

        #endregion

        #region Gesture


        private void ToggleSwitchEnableFingerGestureSlideShowControl_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;

            Settings.PowerPointSettings.IsEnableFingerGestureSlideShowControl = ToggleSwitchEnableFingerGestureSlideShowControl.IsOn;

            SaveSettingsToFile();
        }

        private void ToggleSwitchDisableLockSmithByDefault_Toggled(object sender, RoutedEventArgs e) {
            if (!isLoaded) return;

            Settings.Gesture.IsDisableLockSmithByDefault = ToggleSwitchDisableLockSmithByDefault.IsOn;

            SaveSettingsToFile();
        }

        private void ToggleSwitchEnableTwoFingerZoom_Toggled(object sender, RoutedEventArgs e) {
            if (!isLoaded) return;

            Settings.Gesture.IsEnableTwoFingerZoom = ToggleSwitchEnableTwoFingerZoom.IsOn;

            SaveSettingsToFile();
        }

        private void ToggleSwitchEnableTwoFingerTranslate_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;

            Settings.Gesture.IsEnableTwoFingerTranslate = ToggleSwitchEnableTwoFingerTranslate.IsOn;

            SaveSettingsToFile();
        }

        private void ToggleSwitchEnableTwoFingerRotation_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;

            Settings.Gesture.IsEnableTwoFingerRotation = ToggleSwitchEnableTwoFingerRotation.IsOn;
            Settings.Gesture.IsEnableTwoFingerRotationOnSelection = ToggleSwitchEnableTwoFingerRotationOnSelection.IsOn;

            SaveSettingsToFile();
        }

        private void ToggleSwitchEnableTwoFingerGestureInPresentationMode_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;

            Settings.PowerPointSettings.IsEnableTwoFingerGestureInPresentationMode = ToggleSwitchEnableTwoFingerGestureInPresentationMode.IsOn;

            SaveSettingsToFile();
        }

        #endregion

        #region Reset

        public static void SetSettingsToRecommendation()
        {
            Settings = new Settings();
            Settings.Appearance.IsShowEraserButton = false;
            Settings.Appearance.IsShowExitButton = false;
            Settings.Startup.IsAutoHideCanvas = true;
            Settings.Canvas.InkWidth = 2.5;
        }

        private void BtnResetToSuggestion_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                isLoaded = false;
                SetSettingsToRecommendation();
                // 必须先同步落盘再 LoadSettings：SaveSettingsToFile 是 400ms 防抖，
                // 旧实现紧接着读到的仍是磁盘上的旧配置，会把刚设好的推荐值全部覆盖回去。
                FlushSettings();
                LoadSettings(false);
                isLoaded = true;

                if (ToggleSwitchRunAtStartup.IsOn == false)
                {
                    ToggleSwitchRunAtStartup.IsOn = true;
                }
            }
            catch { }
            SymbolIconResetSuggestionComplete.Visibility = Visibility.Visible;
            ScheduleUiAction(5000, () => SymbolIconResetSuggestionComplete.Visibility = Visibility.Collapsed);
        }

        /// <summary>
        /// 后台线程安全地切回 UI 线程执行动作。应用退出期间 Dispatcher 已关闭或
        /// Application.Current 为空时，裸 Dispatcher.Invoke 会抛异常；若线程体没有 try/catch，
        /// .NET Framework 下未处理的后台线程异常会直接终止整个进程。
        /// </summary>
        private static void SafeUiInvoke(Action action)
        {
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.HasShutdownStarted) return;
                dispatcher.Invoke(() => { try { action(); } catch { } });
            }
            catch { }
        }

        /// <summary>
        /// 异步版本：后台线程（尤其是持有 COM/STA 引用的线程）回填 UI 时用 BeginInvoke，
        /// 不阻塞工作线程，避免 UI 线程正做 COM 调用时双方互等导致界面卡死。
        /// </summary>
        private static void SafeUiInvokeAsync(Action action)
        {
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.HasShutdownStarted) return;
                dispatcher.BeginInvoke(new Action(() => { try { action(); } catch { } }));
            }
            catch { }
        }

        /// <summary>
        /// 延迟 delayMs 毫秒后在 UI 线程执行 action。
        /// 原实现是裸 new Thread + Thread.Sleep + Dispatcher.Invoke：若期间应用退出
        /// （Dispatcher 已关闭），Invoke 抛 InvalidOperationException 且线程体无 try/catch，
        /// 未处理的后台线程异常会直接终止进程。前台线程还会额外吊住退出 5 秒。
        /// </summary>
        private static void ScheduleUiAction(int delayMs, Action action)
        {
            Thread thread = new Thread(new ThreadStart(() =>
            {
                try
                {
                    Thread.Sleep(delayMs);
                    SafeUiInvoke(() =>
                    {
                        try
                        {
                            action();
                        }
                        catch
                        {
                        }
                    });
                }
                catch
                {
                    // Dispatcher 已关闭（应用退出中），忽略
                }
            }))
            {
                IsBackground = true
            };
            thread.Start();
        }

        private void BtnResetToDefault_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                isLoaded = false;
                // 真实配置在 App.RootPath 下，旧实现删的是进程工作目录下的 "settings.json"，
                // 真文件没删掉，随后 LoadSettings 又读回旧配置 → 重置完全无效。
                try { File.Delete(App.RootPath + settingsFileName); } catch { }
                Settings = new Settings();
                LoadSettings(false);
                FlushSettings();
                isLoaded = true;
            }
            catch { }
            SymbolIconResetDefaultComplete.Visibility = Visibility.Visible;
            ScheduleUiAction(5000, () => SymbolIconResetDefaultComplete.Visibility = Visibility.Collapsed);
        }
        #endregion

        #region Ink To Shape

        private void ToggleSwitchEnableInkToShape_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.InkToShape.IsInkToShapeEnabled = ToggleSwitchEnableInkToShape.IsOn;
            SaveSettingsToFile();
        }

        private void ToggleSwitchAutoPerformance_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.Canvas.AutoPerformance = ToggleSwitchAutoPerformance.IsOn;
            SaveSettingsToFile();
        }

        #endregion

        #region Advanced

        private volatile bool _suppressSeewoAutoToggle;

        private void ToggleSwitchIsSpecialScreen_OnToggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded || _suppressSeewoAutoToggle) return;
            Settings.Advanced.IsSpecialScreen = ToggleSwitchIsSpecialScreen.IsOn;
            // 用户已显式决定，之后启动时不再由希沃硬件检测自动覆盖这个开关
            Settings.Advanced.IsAutoDetectSeewo = false;
            TouchMultiplierSlider.Visibility = ToggleSwitchIsSpecialScreen.IsOn ? Visibility.Visible : Visibility.Collapsed;
            SaveSettingsToFile();
        }

        private void TouchMultiplierSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!isLoaded) return;
            Settings.Advanced.TouchMultiplier = e.NewValue;
            SaveSettingsToFile();
        }

        private void EraserTouchThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!isLoaded) return;
            Settings.Advanced.EraserTouchThreshold = e.NewValue;
            RefreshBoundsWidth();
            SaveSettingsToFile();
        }

        private void BorderCalculateMultiplier_TouchDown(object sender, TouchEventArgs e)
        {
            var args = e.GetTouchPoint(null).Bounds;
            double value;
            if (!Settings.Advanced.IsQuadIR) value = args.Width;
            else value = Math.Sqrt(args.Width * args.Height); //四边红外

            // 用笔尖点一下即可一键套用估算的“触摸大小倍数”，省得老师在 0~1 的滑块上反复试。
            if (value > 0.001)
            {
                double recommended = 5 / (value * 1.1);
                // 滑块范围 0~1，且 0 会让取倒数得到 Infinity 传给橡皮形状；须夹紧下界。
                if (recommended > 1) recommended = 1;
                if (recommended < 0.05) recommended = 0.05;
                TouchMultiplierSlider.Value = recommended;   // 触发 ValueChanged 持久化到 Settings
                Settings.Advanced.TouchMultiplier = recommended;
                SaveSettingsToFile();
                TextBlockShowCalculatedMultiplier.Text = "已应用：" + recommended.ToString("F2");
            }
            else
            {
                TextBlockShowCalculatedMultiplier.Text = "未采集到触摸尺寸，请重试";
            }
        }

        private void ToggleSwitchEraserBindTouchMultiplier_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.Advanced.EraserBindTouchMultiplier = ToggleSwitchEraserBindTouchMultiplier.IsOn;
            SaveSettingsToFile();
        }

        private void ToggleSwitchIsEnableTouchAreaEraser_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.Advanced.IsEnableTouchAreaEraser = ToggleSwitchIsEnableTouchAreaEraser.IsOn;
            SaveSettingsToFile();
        }

        private void ToggleSwitchIsQuadIR_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded || _suppressSeewoAutoToggle) return;
            Settings.Advanced.IsQuadIR = ToggleSwitchIsQuadIR.IsOn;
            // 用户已显式决定，之后启动时不再由希沃硬件检测自动覆盖这个开关
            Settings.Advanced.IsAutoDetectSeewo = false;
            SaveSettingsToFile();
        }

        private void ToggleSwitchIsLogEnabled_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.Advanced.IsLogEnabled = ToggleSwitchIsLogEnabled.IsOn;
            SaveSettingsToFile();
        }

        #endregion

        private static DispatcherTimer _settingsSaveTimer;

        public static void SaveSettingsToFile()
        {
            // 拖动画笔粗细滑块时每个 tick 都会调用一次；旧实现每次都同步序列化整棵设置树
            // 并重写文件，在低配一体机上直接表现为拖动卡顿。这里合并为 400ms 后的单次写入。
            Dispatcher dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                WriteSettingsFileNow();
                return;
            }
            if (_settingsSaveTimer == null)
            {
                _settingsSaveTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(400)
                };
                _settingsSaveTimer.Tick += (s, e) =>
                {
                    _settingsSaveTimer.Stop();
                    WriteSettingsFileNow();
                };
            }
            _settingsSaveTimer.Stop();
            _settingsSaveTimer.Start();
        }

        /// <summary>立即落盘。退出/重启前必须调用，否则防抖窗口内的改动会丢失。</summary>
        public static void FlushSettings()
        {
            _settingsSaveTimer?.Stop();
            // 退出 / 欢迎向导重启前必须真正落盘，不能只排进后台队列
            WriteSettingsFileNow(true);
        }

        private static void WriteSettingsFileNow(bool synchronous = false)
        {
            try
            {
                // 序列化在 UI 线程完成（Settings 由 UI 线程改动，保证快照一致），
                // 平时的文件写盘放到后台线程，避免 Settings.json 落盘阻塞界面。
                // System.Text.Json 比 Newtonsoft.Json 快 2-3 倍，内存占用更低
                var options = new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                };
                string json = System.Text.Json.JsonSerializer.Serialize(Settings, options);
                string path = App.RootPath + settingsFileName;
                if (synchronous)
                {
                    try { File.WriteAllText(path, json); } catch { }
                    return;
                }
                Task.Run(() =>
                {
                    try
                    {
                        File.WriteAllText(path, json);
                    }
                    catch { }
                });
            }
            catch { }
        }

        private void SCManipulationBoundaryFeedback(object sender, ManipulationBoundaryFeedbackEventArgs e)
        {
            e.Handled = true;
        }

        /// <summary>「开源参考项目列表」等处超链接的点击处理：交给系统浏览器打开。</summary>
        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch { }
            e.Handled = true;
        }

        #endregion

        #region Left Side Panel

        #region Other Controls

        private void BtnPenWidthDecrease_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                InkWidthSlider.Value -= 1;
            }
            catch { }
        }

        private void BtnPenWidthIncrease_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                InkWidthSlider.Value += 1;
            }
            catch { }
        }


        private void BtnFingerDragMode_Click(object sender, RoutedEventArgs e)
        {
            if (isSingleFingerDragMode)
            {
                isSingleFingerDragMode = false;
                BtnFingerDragMode.Content = "单指\n拖动";
            }
            else
            {
                isSingleFingerDragMode = true;
                BtnFingerDragMode.Content = "多指\n拖动";
            }
        }

        private void BtnUndo_Click(object sender, RoutedEventArgs e)
        {
            if (painterCanvas.GetSelectedStrokes().Count != 0)
            {
                GridPainterCanvasSelectionCover.Visibility = Visibility.Collapsed;
                painterCanvas.Select(new StrokeCollection());
            }
            var item = timeMachine.Undo();
            if (item != null) ApplyHistoryToCanvas(item);
        }
        private void BtnRedo_Click(object sender, RoutedEventArgs e)
        {
            if (painterCanvas.GetSelectedStrokes().Count != 0)
            {
                GridPainterCanvasSelectionCover.Visibility = Visibility.Collapsed;
                painterCanvas.Select(new StrokeCollection());
            }
            var item = timeMachine.Redo();
            if (item != null) ApplyHistoryToCanvas(item);
        }
        private void Btn_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!isLoaded) return;
            try
            {
                if (((Button)sender).IsEnabled)
                {
                    ((UIElement)((Button)sender).Content).Opacity = 1;
                }
                else
                {
                    ((UIElement)((Button)sender).Content).Opacity = 0.25;
                }
            }
            catch { }
        }
        #endregion Other Controls

        #endregion Left Side Panel

        #region Selection Gestures

        #region Floating Control

        object lastBorderMouseDownObject;

        private void Border_MouseDown(object sender, MouseButtonEventArgs e)
        {
            lastBorderMouseDownObject = sender;
        }

        bool isStrokeSelectionCloneOn = false;
        private void BorderStrokeSelectionClone_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (lastBorderMouseDownObject != sender) return;

            if (isStrokeSelectionCloneOn)
            {
                BorderStrokeSelectionClone.Background = Brushes.Transparent;

                isStrokeSelectionCloneOn = false;
            }
            else
            {
                BorderStrokeSelectionClone.Background = new SolidColorBrush(StringToColor("#FF1ED760"));

                isStrokeSelectionCloneOn = true;
            }
        }

        private void BorderStrokeSelectionCloneToNewBoard_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (lastBorderMouseDownObject != sender) return;

            var strokes = painterCanvas.GetSelectedStrokes();
            painterCanvas.Select(new StrokeCollection());
            strokes = strokes.Clone();
            BtnWhiteBoardAdd_Click(null, null);
            painterCanvas.Strokes.Add(strokes);
        }

        private void BorderStrokeSelectionDelete_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (lastBorderMouseDownObject == sender)
            {
                SymbolIconDelete_MouseUp(sender, e);
            }
        }

        private void GridPenWidthDecrease_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (lastBorderMouseDownObject != sender) return;

            ChangeStrokeThickness(0.8);
        }

        private void GridPenWidthIncrease_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (lastBorderMouseDownObject != sender) return;

            ChangeStrokeThickness(1.25);
        }

        private void ChangeStrokeThickness(double multipler)
        {
            foreach (Stroke stroke in painterCanvas.GetSelectedStrokes())
            {
                //stroke.DrawingAttributes.Width *= 1.25;
                //stroke.DrawingAttributes.Height *= 1.25;

                var newWidth = stroke.DrawingAttributes.Width * multipler;
                var newHeight = stroke.DrawingAttributes.Height * multipler;

                if (newWidth >= DrawingAttributes.MinWidth && newWidth <= DrawingAttributes.MaxWidth
                    && newHeight >= DrawingAttributes.MinHeight && newHeight <= DrawingAttributes.MaxHeight)
                {
                    stroke.DrawingAttributes.Width = newWidth;
                    stroke.DrawingAttributes.Height = newHeight;
                }
            }
            if (DrawingAttributesHistory.Count > 0)
            {

                timeMachine.CommitStrokeDrawingAttributesHistory(DrawingAttributesHistory);
                DrawingAttributesHistory = new Dictionary<Stroke, Tuple<DrawingAttributes, DrawingAttributes>>();
                foreach (var item in DrawingAttributesHistoryFlag)
                {
                    item.Value.Clear();
                }
            }
        }

        private void GridPenWidthRestore_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (lastBorderMouseDownObject != sender) return;

            foreach (Stroke stroke in painterCanvas.GetSelectedStrokes())
            {
                stroke.DrawingAttributes.Width = painterCanvas.DefaultDrawingAttributes.Width;
                stroke.DrawingAttributes.Height = painterCanvas.DefaultDrawingAttributes.Height;
            }
        }

        private void ImageFlipHorizontal_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (lastBorderMouseDownObject != sender) return;

            Matrix m = new Matrix();

            // Find center of element and then transform to get current location of center
            FrameworkElement fe = e.Source as FrameworkElement;
            Point center = new Point(fe.ActualWidth / 2, fe.ActualHeight / 2);
            center = new Point(painterCanvas.GetSelectionBounds().Left + painterCanvas.GetSelectionBounds().Width / 2,
                painterCanvas.GetSelectionBounds().Top + painterCanvas.GetSelectionBounds().Height / 2);
            center = m.Transform(center);  // 转换为矩阵缩放和旋转的中心点

            // Update matrix to reflect translation/rotation
            m.ScaleAt(-1, 1, center.X, center.Y);  // 缩放

            StrokeCollection targetStrokes = painterCanvas.GetSelectedStrokes();
            foreach (Stroke stroke in targetStrokes)
            {
                stroke.Transform(m, false);
            }
            if (DrawingAttributesHistory.Count > 0)
            {
                var collecion = new StrokeCollection();
                foreach (var item in DrawingAttributesHistory)
                {
                    collecion.Add(item.Key);
                }
                timeMachine.CommitStrokeDrawingAttributesHistory(DrawingAttributesHistory);
                DrawingAttributesHistory = new Dictionary<Stroke, Tuple<DrawingAttributes, DrawingAttributes>>();
                foreach (var item in DrawingAttributesHistoryFlag)
                {
                    item.Value.Clear();
                }
            }
            //updateBorderStrokeSelectionControlLocation();
        }

        private void ImageFlipVertical_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (lastBorderMouseDownObject != sender) return;

            Matrix m = new Matrix();

            // Find center of element and then transform to get current location of center
            FrameworkElement fe = e.Source as FrameworkElement;
            Point center = new Point(fe.ActualWidth / 2, fe.ActualHeight / 2);
            center = new Point(painterCanvas.GetSelectionBounds().Left + painterCanvas.GetSelectionBounds().Width / 2,
                painterCanvas.GetSelectionBounds().Top + painterCanvas.GetSelectionBounds().Height / 2);
            center = m.Transform(center);  // 转换为矩阵缩放和旋转的中心点

            // Update matrix to reflect translation/rotation
            m.ScaleAt(1, -1, center.X, center.Y);  // 缩放

            StrokeCollection targetStrokes = painterCanvas.GetSelectedStrokes();
            foreach (Stroke stroke in targetStrokes)
            {
                stroke.Transform(m, false);
            }
            if (DrawingAttributesHistory.Count > 0)
            {
                timeMachine.CommitStrokeDrawingAttributesHistory(DrawingAttributesHistory);
                DrawingAttributesHistory = new Dictionary<Stroke, Tuple<DrawingAttributes, DrawingAttributes>>();
                foreach (var item in DrawingAttributesHistoryFlag)
                {
                    item.Value.Clear();
                }
            }
        }

        private void ImageRotate45_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (lastBorderMouseDownObject != sender) return;

            Matrix m = new Matrix();

            // Find center of element and then transform to get current location of center
            FrameworkElement fe = e.Source as FrameworkElement;
            Point center = new Point(fe.ActualWidth / 2, fe.ActualHeight / 2);
            center = new Point(painterCanvas.GetSelectionBounds().Left + painterCanvas.GetSelectionBounds().Width / 2,
                painterCanvas.GetSelectionBounds().Top + painterCanvas.GetSelectionBounds().Height / 2);
            center = m.Transform(center);  // 转换为矩阵缩放和旋转的中心点

            // Update matrix to reflect translation/rotation
            m.RotateAt(45, center.X, center.Y);  // 旋转

            StrokeCollection targetStrokes = painterCanvas.GetSelectedStrokes();
            foreach (Stroke stroke in targetStrokes)
            {
                stroke.Transform(m, false);
            }
            if (DrawingAttributesHistory.Count > 0)
            {
                timeMachine.CommitStrokeDrawingAttributesHistory(DrawingAttributesHistory);
                DrawingAttributesHistory = new Dictionary<Stroke, Tuple<DrawingAttributes, DrawingAttributes>>();
                foreach (var item in DrawingAttributesHistoryFlag)
                {
                    item.Value.Clear();
                }
            }
        }

        private void ImageRotate90_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (lastBorderMouseDownObject != sender) return;

            Matrix m = new Matrix();

            // Find center of element and then transform to get current location of center
            FrameworkElement fe = e.Source as FrameworkElement;
            Point center = new Point(fe.ActualWidth / 2, fe.ActualHeight / 2);
            center = new Point(painterCanvas.GetSelectionBounds().Left + painterCanvas.GetSelectionBounds().Width / 2,
                painterCanvas.GetSelectionBounds().Top + painterCanvas.GetSelectionBounds().Height / 2);
            center = m.Transform(center);  // 转换为矩阵缩放和旋转的中心点

            // Update matrix to reflect translation/rotation
            m.RotateAt(90, center.X, center.Y);  // 旋转

            StrokeCollection targetStrokes = painterCanvas.GetSelectedStrokes();
            foreach (Stroke stroke in targetStrokes)
            {
                stroke.Transform(m, false);
            }
            if (DrawingAttributesHistory.Count > 0)
            {
                var collecion = new StrokeCollection();
                foreach (var item in DrawingAttributesHistory)
                {
                    collecion.Add(item.Key);
                }
                timeMachine.CommitStrokeDrawingAttributesHistory(DrawingAttributesHistory);
                DrawingAttributesHistory = new Dictionary<Stroke, Tuple<DrawingAttributes, DrawingAttributes>>();
                foreach (var item in DrawingAttributesHistoryFlag)
                {
                    item.Value.Clear();
                }
            }
        }

        #endregion


        bool isGridPainterCanvasSelectionCoverMouseDown = false;
        StrokeCollection StrokesSelectionClone = new StrokeCollection();

        private void GridPainterCanvasSelectionCover_MouseDown(object sender, MouseButtonEventArgs e)
        {
            isGridPainterCanvasSelectionCoverMouseDown = true;
        }

        private void GridPainterCanvasSelectionCover_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (isGridPainterCanvasSelectionCoverMouseDown)
            {
                isGridPainterCanvasSelectionCoverMouseDown = false;
                GridPainterCanvasSelectionCover.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnSelect_Click(object sender, RoutedEventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 0;
            painterCanvas.IsManipulationEnabled = false;
            if (painterCanvas.EditingMode == InkCanvasEditingMode.Select)
            {
                if (painterCanvas.GetSelectedStrokes().Count == painterCanvas.Strokes.Count)
                {
                    painterCanvas.EditingMode = InkCanvasEditingMode.Ink;
                    painterCanvas.IsManipulationEnabled = true;
                }
                else
                {
                    //painterCanvas.Select(painterCanvas.Strokes);
                    // Fixed bug: 当通过如鼠标点击等某些方式创建没有高度或长度的笔画时，全选功能不能使用克隆、旋转、翻转、调整笔画粗细、删除功能
                    StrokeCollection selectedStrokes = new StrokeCollection();
                    foreach (Stroke stroke in painterCanvas.Strokes)
                    {
                        if (stroke.GetBounds().Width > 0 && stroke.GetBounds().Height > 0)
                        {
                            selectedStrokes.Add(stroke);
                        }
                    }
                    painterCanvas.Select(selectedStrokes);
                }
            }
            else
            {
                painterCanvas.EditingMode = InkCanvasEditingMode.Select;

            }
        }

        double BorderStrokeSelectionControlWidth = 490.0;
        double BorderStrokeSelectionControlHeight = 80.0;
        bool isProgramChangeStrokeSelection = false;

        private void painterCanvas_SelectionChanged(object sender, EventArgs e)
        {
            if (isProgramChangeStrokeSelection) return;
            StrokeCollection selectedStrokes = painterCanvas.GetSelectedStrokes();
            if (selectedStrokes.Count == 0)
            {
                GridPainterCanvasSelectionCover.Visibility = Visibility.Collapsed;
            }
            else
            {
                GridPainterCanvasSelectionCover.Visibility = Visibility.Visible;
                BorderStrokeSelectionClone.Background = Brushes.Transparent;
                isStrokeSelectionCloneOn = false;
                // 物理工具仅在“恰好选中一条圆形笔迹”时出现：点选圆后可填充磁场点/叉、插入物理符号
                bool isCircle = selectedStrokes.Count == 1
                    && PhysicsShapeTools.TryGetCircleBounds(selectedStrokes[0], out _, out _, out _);
                Visibility physVis = isCircle ? Visibility.Visible : Visibility.Collapsed;
                BorderPhysicsTools.Visibility = physVis;
                BorderPhysicsSeparator.Visibility = physVis;
                updateBorderStrokeSelectionControlLocation();
            }
        }

        // ---- 物理教学工具：磁场填充 / 物理符号（选中圆时可用） ----
        private Stroke GetSelectedCircle()
        {
            StrokeCollection sel = painterCanvas.GetSelectedStrokes();
            if (sel.Count != 1) return null;
            Point c; double rx, ry;
            return PhysicsShapeTools.TryGetCircleBounds(sel[0], out c, out rx, out ry) ? sel[0] : null;
        }

        // 程序化增删笔迹时用 CodeInput 抑制 StrokesOnStrokesChanged 的自动提交，避免重复历史；
        // 合并为一次显式提交，撤销/重做才能一步还原。
        private void PhysicsAddStrokes(StrokeCollection toAdd)
        {
            CommitReason prev = _currentCommitType;
            _currentCommitType = CommitReason.CodeInput;
            try { painterCanvas.Strokes.Add(toAdd); }
            finally { _currentCommitType = prev; }
        }

        private void PhysicsRemoveStrokes(StrokeCollection toRemove)
        {
            CommitReason prev = _currentCommitType;
            _currentCommitType = CommitReason.CodeInput;
            try { painterCanvas.Strokes.Remove(toRemove); }
            finally { _currentCommitType = prev; }
        }

        private void PhysicsApplyFill(string kind)
        {
            Stroke circle = GetSelectedCircle();
            if (circle == null) return;
            StrokeCollection generated = PhysicsShapeTools.GenerateFieldFill(
                circle, kind, painterCanvas.DefaultDrawingAttributes);
            if (generated.Count == 0) return;

            List<Stroke> oldFills = PhysicsShapeTools.FindFills(painterCanvas.Strokes, circle);
            StrokeCollection old = new StrokeCollection();
            foreach (Stroke s in oldFills) old.Add(s);

            if (old.Count > 0)
            {
                // 切换类型（点↔叉）：一次提交，撤销可还原为之前的填充
                PhysicsRemoveStrokes(old);
                PhysicsAddStrokes(generated);
                timeMachine.CommitStrokeShapeHistory(old, generated);
            }
            else
            {
                PhysicsAddStrokes(generated);
                timeMachine.CommitStrokeUserInputHistory(generated);
            }
        }

        private void PhysicsFillDot_MouseUp(object sender, MouseButtonEventArgs e)
        {
            PhysicsApplyFill(PhysicsShapeTools.FillKindDot);
        }

        private void PhysicsFillCross_MouseUp(object sender, MouseButtonEventArgs e)
        {
            PhysicsApplyFill(PhysicsShapeTools.FillKindCross);
        }

        private void PhysicsFillClear_MouseUp(object sender, MouseButtonEventArgs e)
        {
            Stroke circle = GetSelectedCircle();
            if (circle == null) return;
            List<Stroke> fills = PhysicsShapeTools.FindFills(painterCanvas.Strokes, circle);
            if (fills.Count == 0) return;
            StrokeCollection old = new StrokeCollection();
            foreach (Stroke s in fills) old.Add(s);
            PhysicsRemoveStrokes(old);
            timeMachine.CommitStrokeEraseHistory(old);
        }

        private void PhysicsSymbol_MouseUp(object sender, MouseButtonEventArgs e)
        {
            Stroke circle = GetSelectedCircle();
            if (circle == null) return;
            Border tagBorder = sender as Border;
            string tag = tagBorder != null && tagBorder.Tag != null ? tagBorder.Tag.ToString() : null;
            PhysicsShapeTools.PhysicsSymbol kind;
            if (string.IsNullOrEmpty(tag) || !Enum.TryParse(tag, true, out kind)) return;

            Point c; double rx, ry;
            if (!PhysicsShapeTools.TryGetCircleBounds(circle, out c, out rx, out ry)) return;
            double size = Math.Max(24.0, Math.Min(rx, ry) * 1.2);
            // 插入在圆的右侧，避免遮挡所填充的磁场符号
            double cx = c.X + rx + size * 0.65 + 8;
            double maxX = painterCanvas.ActualWidth - size * 0.6;
            if (maxX > c.X && cx > maxX) cx = maxX;
            StrokeCollection symbol = PhysicsShapeTools.GenerateSymbol(
                kind, new Point(cx, c.Y), size, painterCanvas.DefaultDrawingAttributes);
            if (symbol.Count == 0) return;
            PhysicsAddStrokes(symbol);
            timeMachine.CommitStrokeUserInputHistory(symbol);
        }

        private void updateBorderStrokeSelectionControlLocation()
        {
            // GetSelectionBounds 会遍历全部选中笔迹做包围盒计算，旧实现一次调用三次，
            // 选择框拖动时每帧都多算两遍，这里只取一次。
            Rect bounds = painterCanvas.GetSelectionBounds();
            double borderLeft = (bounds.Left + bounds.Right - BorderStrokeSelectionControlWidth) / 2;
            double borderTop = bounds.Bottom + 15;
            if (borderLeft < 0) borderLeft = 0;
            if (borderTop < 0) borderTop = 0;
            if (Width - borderLeft < BorderStrokeSelectionControlWidth || double.IsNaN(borderLeft)) borderLeft = Width - BorderStrokeSelectionControlWidth;
            if (Height - borderTop < BorderStrokeSelectionControlHeight || double.IsNaN(borderTop)) borderTop = Height - BorderStrokeSelectionControlHeight;
            BorderStrokeSelectionControl.Margin = new Thickness(borderLeft, borderTop, 0, 0);
        }

        private void GridPainterCanvasSelectionCover_ManipulationStarting(object sender, ManipulationStartingEventArgs e)
        {
            e.Mode = ManipulationModes.All;
        }

        private void GridPainterCanvasSelectionCover_ManipulationCompleted(object sender, ManipulationCompletedEventArgs e)
        {
            if (StrokeManipulationHistory?.Count > 0)
            {
                timeMachine.CommitStrokeManipulationHistory(StrokeManipulationHistory);
                foreach (var item in StrokeManipulationHistory)
                {
                    StrokeInitialHistory[item.Key] = item.Value.Item2;
                }
                StrokeManipulationHistory = null;
            }
            if (DrawingAttributesHistory.Count > 0)
            {
                timeMachine.CommitStrokeDrawingAttributesHistory(DrawingAttributesHistory);
                DrawingAttributesHistory = new Dictionary<Stroke, Tuple<DrawingAttributes, DrawingAttributes>>();
                foreach (var item in DrawingAttributesHistoryFlag)
                {
                    item.Value.Clear();
                }
            }
        }

        private void GridPainterCanvasSelectionCover_ManipulationDelta(object sender, ManipulationDeltaEventArgs e)
        {
            try
            {
                if (dec.Count >= 1)
                {
                    ManipulationDelta md = e.DeltaManipulation;
                    Vector trans = md.Translation;  // 获得位移矢量
                    double rotate = md.Rotation;  // 获得旋转角度
                    Vector scale = md.Scale;  // 获得缩放倍数

                    Matrix m = new Matrix();

                    // GetSelectionBounds 每次调用都要遍历全部选中笔画求包围盒，每帧只取一次
                    Rect selectionBounds = painterCanvas.GetSelectionBounds();
                    Point center = new Point(selectionBounds.Left + selectionBounds.Width / 2,
                        selectionBounds.Top + selectionBounds.Height / 2);
                    center = m.Transform(center);  // 转换为矩阵缩放和旋转的中心点

                    // Update matrix to reflect translation/rotation
                    m.Translate(trans.X, trans.Y);  // 移动
                    m.ScaleAt(scale.X, scale.Y, center.X, center.Y);  // 缩放

                    StrokeCollection strokes = painterCanvas.GetSelectedStrokes();
                    if (StrokesSelectionClone.Count != 0)
                    {
                        strokes = StrokesSelectionClone;
                    }
                    else if (Settings.Gesture.IsEnableTwoFingerRotationOnSelection)
                    {
                        m.RotateAt(rotate, center.X, center.Y);  // 旋转
                    }
                    foreach (Stroke stroke in strokes)
                    {
                        stroke.Transform(m, false);

                        try
                        {
                            stroke.DrawingAttributes.Width *= md.Scale.X;
                            stroke.DrawingAttributes.Height *= md.Scale.Y;
                        }
                        catch { }
                    }
                    updateBorderStrokeSelectionControlLocation();
                }
            }
            catch { }
        }

        private void GridPainterCanvasSelectionCover_TouchDown(object sender, TouchEventArgs e)
        {
        }

        private void GridPainterCanvasSelectionCover_TouchUp(object sender, TouchEventArgs e)
        {
        }

        Point lastTouchPointOnGridPainterCanvasCover = new Point(0, 0);
        private void GridPainterCanvasSelectionCover_PreviewTouchDown(object sender, TouchEventArgs e)
        {
            dec.Add(e.TouchDevice.Id);
            //设备1个的时候，记录中心点
            if (dec.Count == 1)
            {
                TouchPoint touchPoint = e.GetTouchPoint(null);
                centerPoint = touchPoint.Position;
                lastTouchPointOnGridPainterCanvasCover = touchPoint.Position;

                if (isStrokeSelectionCloneOn)
                {
                    StrokeCollection strokes = painterCanvas.GetSelectedStrokes();
                    isProgramChangeStrokeSelection = true;
                    painterCanvas.Select(new StrokeCollection());
                    StrokesSelectionClone = strokes.Clone();
                    painterCanvas.Select(strokes);
                    isProgramChangeStrokeSelection = false;
                    painterCanvas.Strokes.Add(StrokesSelectionClone);
                }
            }
        }

        private void GridPainterCanvasSelectionCover_PreviewTouchUp(object sender, TouchEventArgs e)
        {
            dec.Remove(e.TouchDevice.Id);
            if (dec.Count >= 1) return;
            isProgramChangeStrokeSelection = false;
            if (lastTouchPointOnGridPainterCanvasCover == e.GetTouchPoint(null).Position)
            {
                if (lastTouchPointOnGridPainterCanvasCover.X < painterCanvas.GetSelectionBounds().Left ||
                    lastTouchPointOnGridPainterCanvasCover.Y < painterCanvas.GetSelectionBounds().Top ||
                    lastTouchPointOnGridPainterCanvasCover.X > painterCanvas.GetSelectionBounds().Right ||
                    lastTouchPointOnGridPainterCanvasCover.Y > painterCanvas.GetSelectionBounds().Bottom)
                {
                    painterCanvas.Select(new StrokeCollection());
                    StrokesSelectionClone = new StrokeCollection();
                }
            }
            else if (painterCanvas.GetSelectedStrokes().Count == 0)
            {
                GridPainterCanvasSelectionCover.Visibility = Visibility.Collapsed;
                StrokesSelectionClone = new StrokeCollection();
            }
            else
            {
                GridPainterCanvasSelectionCover.Visibility = Visibility.Visible;
                StrokesSelectionClone = new StrokeCollection();
            }
        }

        #endregion Selection Gestures

        #region Shape Drawing

        #region Floating Bar Control

        private void ImageDrawShape_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (BorderDrawShape.Visibility == Visibility.Visible)
            {
                BorderDrawShape.Visibility = Visibility.Collapsed;
            }
            else
            {
                BorderDrawShape.Visibility = Visibility.Visible;
            }
        }

        #endregion Floating Bar Control

        int drawingShapeMode = 0;
        bool isLongPressSelected = false; // 用于存是否是“选中”状态，便于后期抬笔后不做切换到笔的处理

        #region Buttons

        private void SymbolIconPinBorderDrawShape_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (lastBorderMouseDownObject != sender) return;

            ToggleSwitchDrawShapeBorderAutoHide.IsOn = !ToggleSwitchDrawShapeBorderAutoHide.IsOn;

            if (ToggleSwitchDrawShapeBorderAutoHide.IsOn)
            {
                ((AppIcon)sender).Glyph = "Pin";
            }
            else
            {
                ((AppIcon)sender).Glyph = "UnPin";
            }
        }

        object lastMouseDownSender = null;
        DateTime lastMouseDownTime = DateTime.MinValue;

        private async void Image_MouseDown(object sender, MouseButtonEventArgs e)
        {
            lastMouseDownSender = sender;
            lastMouseDownTime = DateTime.Now;

            await Task.Delay(500);

            if (lastMouseDownSender == sender)
            {
                lastMouseDownSender = null;
                var dA = new DoubleAnimation(1, 0.3, new Duration(TimeSpan.FromMilliseconds(100)));
                ((UIElement)sender).BeginAnimation(OpacityProperty, dA);

                forceEraser = true;
                painterCanvas.EditingMode = InkCanvasEditingMode.None;
                painterCanvas.IsManipulationEnabled = true;
                if (sender == ImageDrawLine)
                {
                    drawingShapeMode = 1;
                }
                else if (sender == ImageDrawDashedLine)
                {
                    drawingShapeMode = 8;
                }
                else if (sender == ImageDrawDotLine)
                {
                    drawingShapeMode = 18;
                }
                else if (sender == ImageDrawArrow)
                {
                    drawingShapeMode = 2;
                }
                else if (sender == ImageDrawParallelLine)
                {
                    drawingShapeMode = 15;
                }
                isLongPressSelected = true;
                if (isSingleFingerDragMode)
                {
                    BtnFingerDragMode_Click(BtnFingerDragMode, null);
                }
            }
        }

        private void BtnPen_Click(object sender, RoutedEventArgs e)
        {
            forceEraser = false;
            // 注意：不能在切工具时清 _currentCommitType / lastTempStroke —— 单笔图形的 MouseUp
            // 正是在调用本方法之后才用这些字段把笔迹提交进撤销历史，清掉会导致图形画得出却撤不掉。
            drawingShapeMode = 0;
            painterCanvas.EditingMode = InkCanvasEditingMode.Ink;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
            isLongPressSelected = false;
        }

        private void BtnDrawLine_Click(object sender, EventArgs e)
        {
            if (lastMouseDownSender == sender)
            {
                forceEraser = true;
                drawingShapeMode = 1;
                painterCanvas.EditingMode = InkCanvasEditingMode.None;
                painterCanvas.IsManipulationEnabled = true;
                CancelSingleFingerDragMode();
            }
            lastMouseDownSender = null;
            if (isLongPressSelected)
            {
                if (ToggleSwitchDrawShapeBorderAutoHide.IsOn)
                {
                    BorderDrawShape.Visibility = Visibility.Collapsed;
                }
                var dA = new DoubleAnimation(1, 1, new Duration(TimeSpan.FromMilliseconds(0)));
                ImageDrawLine.BeginAnimation(OpacityProperty, dA);
            }
        }

        private void BtnDrawDashedLine_Click(object sender, EventArgs e)
        {
            if (lastMouseDownSender == sender)
            {
                forceEraser = true;
                drawingShapeMode = 8;
                painterCanvas.EditingMode = InkCanvasEditingMode.None;
                painterCanvas.IsManipulationEnabled = true;
                CancelSingleFingerDragMode();
            }
            lastMouseDownSender = null;
            if (isLongPressSelected)
            {
                if (ToggleSwitchDrawShapeBorderAutoHide.IsOn)
                {
                    BorderDrawShape.Visibility = Visibility.Collapsed;
                }
                var dA = new DoubleAnimation(1, 1, new Duration(TimeSpan.FromMilliseconds(0)));
                ImageDrawDashedLine.BeginAnimation(OpacityProperty, dA);
            }
        }

        private void BtnDrawDotLine_Click(object sender, EventArgs e)
        {
            if (lastMouseDownSender == sender)
            {
                forceEraser = true;
                drawingShapeMode = 18;
                painterCanvas.EditingMode = InkCanvasEditingMode.None;
                painterCanvas.IsManipulationEnabled = true;
                CancelSingleFingerDragMode();
            }
            lastMouseDownSender = null;
            if (isLongPressSelected)
            {
                if (ToggleSwitchDrawShapeBorderAutoHide.IsOn)
                {
                    BorderDrawShape.Visibility = Visibility.Collapsed;
                }
                var dA = new DoubleAnimation(1, 1, new Duration(TimeSpan.FromMilliseconds(0)));
                ImageDrawDotLine.BeginAnimation(OpacityProperty, dA);
            }
        }

        private void BtnDrawArrow_Click(object sender, EventArgs e)
        {
            if (lastMouseDownSender == sender)
            {
                forceEraser = true;
                drawingShapeMode = 2;
                painterCanvas.EditingMode = InkCanvasEditingMode.None;
                painterCanvas.IsManipulationEnabled = true;
                CancelSingleFingerDragMode();
            }
            lastMouseDownSender = null;
            if (isLongPressSelected)
            {
                if (ToggleSwitchDrawShapeBorderAutoHide.IsOn)
                {
                    BorderDrawShape.Visibility = Visibility.Collapsed;
                }
                var dA = new DoubleAnimation(1, 1, new Duration(TimeSpan.FromMilliseconds(0)));
                ImageDrawArrow.BeginAnimation(OpacityProperty, dA);
            }
        }

        private void BtnDrawParallelLine_Click(object sender, EventArgs e)
        {
            if (lastMouseDownSender == sender)
            {
                forceEraser = true;
                drawingShapeMode = 15;
                painterCanvas.EditingMode = InkCanvasEditingMode.None;
                painterCanvas.IsManipulationEnabled = true;
                CancelSingleFingerDragMode();
            }
            lastMouseDownSender = null;
            if (isLongPressSelected)
            {
                if (ToggleSwitchDrawShapeBorderAutoHide.IsOn)
                {
                    BorderDrawShape.Visibility = Visibility.Collapsed;
                }
                var dA = new DoubleAnimation(1, 1, new Duration(TimeSpan.FromMilliseconds(0)));
                ImageDrawParallelLine.BeginAnimation(OpacityProperty, dA);
            }
        }

        private void BtnDrawCoordinate1_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 11;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawCoordinate2_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 12;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawCoordinate3_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 13;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawCoordinate4_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 14;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawCoordinate5_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 17;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawRectangle_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 3;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawRectangleCenter_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 19;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawEllipse_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 4;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawCircle_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 5;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawCenterEllipse_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 16;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawCenterEllipseWithFocalPoint_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 23;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawDashedCircle_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 10;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawHyperbola_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 24;
            drawMultiStepShapeCurrentStep = 0;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawHyperbolaWithFocalPoint_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 25;
            drawMultiStepShapeCurrentStep = 0;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawParabola1_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 20;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawParabolaWithFocalPoint_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 22;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawParabola2_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 21;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawCylinder_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 6;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawCone_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 7;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawCuboid_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 9;
            isFirstTouchCuboid = true;
            CuboidFrontRectIniP = new Point();
            CuboidFrontRectEndP = new Point();
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawPlaneMirror_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 26;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawPlaneMirrorHorizontal_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 32; // 水平平面镜：由竖直平面镜旋转 90° 而来
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawConcaveMirror_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 27;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawConvexMirror_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 28;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawConvexLens_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 29;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawConcaveLens_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 30;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        private void BtnDrawPrism_Click(object sender, EventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 31;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        // ---- 物理电学 / 波形图元（mode 33~40，生成器见 Helpers/PhysicsDrawingShapes.cs） ----
        private void BtnDrawBattery_Click(object sender, EventArgs e) => SetPhysicsDrawMode(PhysicsDrawingShapes.Battery);
        private void BtnDrawResistor_Click(object sender, EventArgs e) => SetPhysicsDrawMode(PhysicsDrawingShapes.Resistor);
        private void BtnDrawSwitch_Click(object sender, EventArgs e) => SetPhysicsDrawMode(PhysicsDrawingShapes.Switch);
        private void BtnDrawBulb_Click(object sender, EventArgs e) => SetPhysicsDrawMode(PhysicsDrawingShapes.Bulb);
        private void BtnDrawSineWave_Click(object sender, EventArgs e) => SetPhysicsDrawMode(PhysicsDrawingShapes.SineWave);
        private void BtnDrawSpring_Click(object sender, EventArgs e) => SetPhysicsDrawMode(PhysicsDrawingShapes.Spring);
        private void BtnDrawPositiveCharge_Click(object sender, EventArgs e) => SetPhysicsDrawMode(PhysicsDrawingShapes.PositiveCharge);
        private void BtnDrawNegativeCharge_Click(object sender, EventArgs e) => SetPhysicsDrawMode(PhysicsDrawingShapes.NegativeCharge);
        private void BtnDrawMagneticFieldOut_Click(object sender, EventArgs e) => SetPhysicsDrawMode(PhysicsDrawingShapes.MagneticFieldOut);
        private void BtnDrawMagneticFieldIn_Click(object sender, EventArgs e) => SetPhysicsDrawMode(PhysicsDrawingShapes.MagneticFieldIn);

        private void SetPhysicsDrawMode(int mode)
        {
            forceEraser = true;
            drawingShapeMode = mode;
            painterCanvas.EditingMode = InkCanvasEditingMode.None;
            painterCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
        }

        #endregion

        private void painterCanvas_TouchMove(object sender, TouchEventArgs e)
        {
            if (isSingleFingerDragMode) return;
            // 普通笔迹（drawingShapeMode == 0）完全由 InkCanvas 自己绘制/显示，
            // 这里既没有图形要预览，也不需要每帧取触摸点；提前返回省掉这次命中测试。
            if (drawingShapeMode == 0) return;
            if (isLastTouchEraser)
            {
                return;
            }
            //EraserContainer.Background = null;
            ImageEraser.Visibility = Visibility.Visible;
            if (isWaitUntilNextTouchDown) return;
            if (dec.Count > 1)
            {
                isWaitUntilNextTouchDown = true;
                try
                {
                    painterCanvas.Strokes.Remove(lastTempStroke);
                    painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                }
                catch { }
                // 预览已取消：必须清空临时字段，否则后续 MouseUp 会把这份已不在画布上的
                // 集合提交进历史，Redo 时凭空"复活"被取消的图形。
                lastTempStroke = null;
                lastTempStrokeCollection = null;
                _currentCommitType = CommitReason.UserInput;
                return;
            }
            if (painterCanvas.EditingMode != InkCanvasEditingMode.None)
            {
                painterCanvas.EditingMode = InkCanvasEditingMode.None;
            }
            MouseTouchMove(e.GetTouchPoint(painterCanvas).Position);
        }

        int drawMultiStepShapeCurrentStep = 0; //多笔完成的图形 当前所处在的笔画
        StrokeCollection drawMultiStepShapeSpecialStrokeCollection = new StrokeCollection(); //多笔完成的图形 当前所处在的笔画
        //double drawMultiStepShapeSpecialParameter1 = 0.0; //多笔完成的图形 特殊参数 通常用于表示a
        //double drawMultiStepShapeSpecialParameter2 = 0.0; //多笔完成的图形 特殊参数 通常用于表示b
        double drawMultiStepShapeSpecialParameter3 = 0.0; //多笔完成的图形 特殊参数 通常用于表示k

        private void MouseTouchMove(Point endP)
        {
            // 笔模式下没有任何图形预览要做，调用方的每次移动都只会落到空 switch 上
            if (drawingShapeMode == 0) return;
            List<System.Windows.Point> pointList;
            StylusPointCollection point;
            Stroke stroke;
            StrokeCollection strokes = new StrokeCollection();
            Point newIniP = iniP;
            switch (drawingShapeMode)
            {
                case 1:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    pointList = new List<System.Windows.Point>{
                        new System.Windows.Point(iniP.X, iniP.Y),
                        new System.Windows.Point(endP.X, endP.Y)
                    };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStroke);
                    }
                    catch { }
                    lastTempStroke = stroke;
                    painterCanvas.Strokes.Add(stroke);
                    break;
                case 8:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    strokes.Add(GenerateDashedLineStrokeCollection(iniP, endP));
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    painterCanvas.Strokes.Add(strokes);
                    break;
                case 18:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    strokes.Add(GenerateDotLineStrokeCollection(iniP, endP));
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    painterCanvas.Strokes.Add(strokes);
                    break;
                case 2:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    double w = 30, h = 10;
                    double theta = Math.Atan2(iniP.Y - endP.Y, iniP.X - endP.X);
                    double sint = Math.Sin(theta);
                    double cost = Math.Cos(theta);

                    pointList = new List<Point>
                    {
                        new Point(iniP.X, iniP.Y),
                        new Point(endP.X , endP.Y),
                        new Point(endP.X + (w * cost - h * sint), endP.Y + (w * sint + h * cost)),
                        new Point(endP.X,endP.Y),
                        new Point(endP.X + (w * cost + h * sint), endP.Y - (h * cost - w * sint))
                    };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStroke);
                    }
                    catch { }
                    lastTempStroke = stroke;
                    painterCanvas.Strokes.Add(stroke);
                    break;
                case 15:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    double d = GetDistance(iniP, endP);
                    if (d == 0) return;
                    double sinTheta = (iniP.Y - endP.Y) / d;
                    double cosTheta = (endP.X - iniP.X) / d;
                    double tanTheta = Math.Abs(sinTheta / cosTheta);
                    double x = 25;
                    if (Math.Abs(tanTheta) < 1.0 / 12)
                    {
                        sinTheta = 0;
                        cosTheta = 1;
                        endP.Y = iniP.Y;
                    }
                    if (tanTheta < 0.63 && tanTheta > 0.52) //30
                    {
                        sinTheta = sinTheta / Math.Abs(sinTheta) * 0.5;
                        cosTheta = cosTheta / Math.Abs(cosTheta) * 0.866;
                        endP.Y = iniP.Y - d * sinTheta;
                        endP.X = iniP.X + d * cosTheta;
                    }
                    if (tanTheta < 1.08 && tanTheta > 0.92) //45
                    {
                        sinTheta = sinTheta / Math.Abs(sinTheta) * 0.707;
                        cosTheta = cosTheta / Math.Abs(cosTheta) * 0.707;
                        endP.Y = iniP.Y - d * sinTheta;
                        endP.X = iniP.X + d * cosTheta;
                    }
                    if (tanTheta < 1.95 && tanTheta > 1.63) //60
                    {
                        sinTheta = sinTheta / Math.Abs(sinTheta) * 0.866;
                        cosTheta = cosTheta / Math.Abs(cosTheta) * 0.5;
                        endP.Y = iniP.Y - d * sinTheta;
                        endP.X = iniP.X + d * cosTheta;
                    }
                    if (Math.Abs(cosTheta / sinTheta) < 1.0 / 12)
                    {
                        endP.X = iniP.X;
                        sinTheta = 1;
                        cosTheta = 0;
                    }
                    strokes.Add(GenerateLineStroke(new Point(iniP.X - 3 * x * sinTheta, iniP.Y - 3 * x * cosTheta), new Point(endP.X - 3 * x * sinTheta, endP.Y - 3 * x * cosTheta)));
                    strokes.Add(GenerateLineStroke(new Point(iniP.X - x * sinTheta, iniP.Y - x * cosTheta), new Point(endP.X - x * sinTheta, endP.Y - x * cosTheta)));
                    strokes.Add(GenerateLineStroke(new Point(iniP.X + x * sinTheta, iniP.Y + x * cosTheta), new Point(endP.X + x * sinTheta, endP.Y + x * cosTheta)));
                    strokes.Add(GenerateLineStroke(new Point(iniP.X + 3 * x * sinTheta, iniP.Y + 3 * x * cosTheta), new Point(endP.X + 3 * x * sinTheta, endP.Y + 3 * x * cosTheta)));
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    painterCanvas.Strokes.Add(strokes);
                    break;
                case 11:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    strokes.Add(GenerateArrowLineStroke(new Point(2 * iniP.X - (endP.X - 20), iniP.Y), new Point(endP.X, iniP.Y)));
                    strokes.Add(GenerateArrowLineStroke(new Point(iniP.X, 2 * iniP.Y - (endP.Y + 20)), new Point(iniP.X, endP.Y)));
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    painterCanvas.Strokes.Add(strokes);
                    break;
                case 12:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    if (Math.Abs(iniP.X - endP.X) < 0.01) return;
                    strokes.Add(GenerateArrowLineStroke(new Point(iniP.X + (iniP.X - endP.X) / Math.Abs(iniP.X - endP.X) * 25, iniP.Y), new Point(endP.X, iniP.Y)));
                    strokes.Add(GenerateArrowLineStroke(new Point(iniP.X, 2 * iniP.Y - (endP.Y + 20)), new Point(iniP.X, endP.Y)));
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    painterCanvas.Strokes.Add(strokes);
                    break;
                case 13:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    if (Math.Abs(iniP.Y - endP.Y) < 0.01) return;
                    strokes.Add(GenerateArrowLineStroke(new Point(2 * iniP.X - (endP.X - 20), iniP.Y), new Point(endP.X, iniP.Y)));
                    strokes.Add(GenerateArrowLineStroke(new Point(iniP.X, iniP.Y + (iniP.Y - endP.Y) / Math.Abs(iniP.Y - endP.Y) * 25), new Point(iniP.X, endP.Y)));
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    painterCanvas.Strokes.Add(strokes);
                    break;
                case 14:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    if (Math.Abs(iniP.X - endP.X) < 0.01 || Math.Abs(iniP.Y - endP.Y) < 0.01) return;
                    strokes.Add(GenerateArrowLineStroke(new Point(iniP.X + (iniP.X - endP.X) / Math.Abs(iniP.X - endP.X) * 25, iniP.Y), new Point(endP.X, iniP.Y)));
                    strokes.Add(GenerateArrowLineStroke(new Point(iniP.X, iniP.Y + (iniP.Y - endP.Y) / Math.Abs(iniP.Y - endP.Y) * 25), new Point(iniP.X, endP.Y)));
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    painterCanvas.Strokes.Add(strokes);
                    break;
                case 17:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    strokes.Add(GenerateArrowLineStroke(new Point(iniP.X, iniP.Y), new Point(iniP.X + Math.Abs(endP.X - iniP.X), iniP.Y)));
                    strokes.Add(GenerateArrowLineStroke(new Point(iniP.X, iniP.Y), new Point(iniP.X, iniP.Y - Math.Abs(endP.Y - iniP.Y))));
                    d = (Math.Abs(iniP.X - endP.X) + Math.Abs(iniP.Y - endP.Y)) / 2;
                    strokes.Add(GenerateArrowLineStroke(new Point(iniP.X, iniP.Y), new Point(iniP.X - d / 1.76, iniP.Y + d / 1.76)));
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    painterCanvas.Strokes.Add(strokes);
                    break;
                case 3:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    pointList = new List<System.Windows.Point>{
                        new System.Windows.Point(iniP.X, iniP.Y),
                        new System.Windows.Point(iniP.X, endP.Y),
                        new System.Windows.Point(endP.X, endP.Y),
                        new System.Windows.Point(endP.X, iniP.Y),
                        new System.Windows.Point(iniP.X, iniP.Y)
                    };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStroke);
                    }
                    catch { }
                    lastTempStroke = stroke;
                    painterCanvas.Strokes.Add(stroke);
                    break;
                case 19:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    double a = iniP.X - endP.X;
                    double b = iniP.Y - endP.Y;
                    pointList = new List<System.Windows.Point>{
                        new System.Windows.Point(iniP.X - a, iniP.Y - b),
                        new System.Windows.Point(iniP.X - a, iniP.Y + b),
                        new System.Windows.Point(iniP.X + a, iniP.Y + b),
                        new System.Windows.Point(iniP.X + a, iniP.Y - b),
                        new System.Windows.Point(iniP.X - a, iniP.Y - b)
                    };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStroke);
                    }
                    catch { }
                    lastTempStroke = stroke;
                    painterCanvas.Strokes.Add(stroke);
                    break;
                case 4:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    pointList = GenerateEllipseGeometry(iniP, endP);
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStroke);
                    }
                    catch { }
                    lastTempStroke = stroke;
                    painterCanvas.Strokes.Add(stroke);
                    break;
                case 5:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    double R = GetDistance(iniP, endP);
                    pointList = GenerateEllipseGeometry(new Point(iniP.X - R, iniP.Y - R), new Point(iniP.X + R, iniP.Y + R));
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStroke);
                    }
                    catch { }
                    lastTempStroke = stroke;
                    painterCanvas.Strokes.Add(stroke);
                    break;
                case 16:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    double halfA = endP.X - iniP.X;
                    double halfB = endP.Y - iniP.Y;
                    pointList = GenerateEllipseGeometry(new Point(iniP.X - halfA, iniP.Y - halfB), new Point(iniP.X + halfA, iniP.Y + halfB));
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStroke);
                    }
                    catch { }
                    lastTempStroke = stroke;
                    painterCanvas.Strokes.Add(stroke);
                    break;
                case 23:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    a = Math.Abs(endP.X - iniP.X);
                    b = Math.Abs(endP.Y - iniP.Y);
                    pointList = GenerateEllipseGeometry(new Point(iniP.X - a, iniP.Y - b), new Point(iniP.X + a, iniP.Y + b));
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke);
                    double c = Math.Sqrt(Math.Abs(a * a - b * b));
                    StylusPoint stylusPoint;
                    if (a > b)
                    {
                        stylusPoint = new StylusPoint(iniP.X + c, iniP.Y, (float)1.0);
                        point = new StylusPointCollection();
                        point.Add(stylusPoint);
                        stroke = new Stroke(point)
                        {
                            DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                        };
                        strokes.Add(stroke.Clone());
                        stylusPoint = new StylusPoint(iniP.X - c, iniP.Y, (float)1.0);
                        point = new StylusPointCollection();
                        point.Add(stylusPoint);
                        stroke = new Stroke(point)
                        {
                            DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                        };
                        strokes.Add(stroke.Clone());
                    }
                    else if (a < b)
                    {
                        stylusPoint = new StylusPoint(iniP.X, iniP.Y - c, (float)1.0);
                        point = new StylusPointCollection();
                        point.Add(stylusPoint);
                        stroke = new Stroke(point)
                        {
                            DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                        };
                        strokes.Add(stroke.Clone());
                        stylusPoint = new StylusPoint(iniP.X, iniP.Y + c, (float)1.0);
                        point = new StylusPointCollection();
                        point.Add(stylusPoint);
                        stroke = new Stroke(point)
                        {
                            DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                        };
                        strokes.Add(stroke.Clone());
                    }
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    painterCanvas.Strokes.Add(strokes);
                    break;
                case 10:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    R = GetDistance(iniP, endP);
                    strokes = GenerateDashedLineEllipseStrokeCollection(new Point(iniP.X - R, iniP.Y - R), new Point(iniP.X + R, iniP.Y + R));
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    painterCanvas.Strokes.Add(strokes);
                    break;
                case 24:
                case 25:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    //双曲线 x^2/a^2 - y^2/b^2 = 1
                    if (Math.Abs(iniP.X - endP.X) < 0.01 || Math.Abs(iniP.Y - endP.Y) < 0.01) return;
                    var pointList2 = new List<Point>();
                    var pointList3 = new List<Point>();
                    var pointList4 = new List<Point>();
                    if (drawMultiStepShapeCurrentStep == 0)
                    {
                        //第一笔：画渐近线
                        double k = Math.Abs((endP.Y - iniP.Y) / (endP.X - iniP.X));
                        strokes.Add(GenerateDashedLineStrokeCollection(new Point(2 * iniP.X - endP.X, 2 * iniP.Y - endP.Y), endP));
                        strokes.Add(GenerateDashedLineStrokeCollection(new Point(2 * iniP.X - endP.X, endP.Y), new Point(endP.X, 2 * iniP.Y - endP.Y)));
                        drawMultiStepShapeSpecialParameter3 = k;
                        drawMultiStepShapeSpecialStrokeCollection = strokes;
                    }
                    else
                    {
                        //第二笔：画双曲线
                        double k = drawMultiStepShapeSpecialParameter3;
                        bool isHyperbolaFocalPointOnXAxis = Math.Abs((endP.Y - iniP.Y) / (endP.X - iniP.X)) < k;
                        if (isHyperbolaFocalPointOnXAxis)
                        { // 焦点在 x 轴上
                            a = Math.Sqrt(Math.Abs((endP.X - iniP.X) * (endP.X - iniP.X) - (endP.Y - iniP.Y) * (endP.Y - iniP.Y) / (k * k)));
                            b = a * k;
                            pointList = new List<Point>();
                            for (double i = a; i <= Math.Abs(endP.X - iniP.X); i += 0.5)
                            {
                                double rY = Math.Sqrt(Math.Abs(k * k * i * i - b * b));
                                pointList.Add(new Point(iniP.X + i, iniP.Y - rY));
                                pointList2.Add(new Point(iniP.X + i, iniP.Y + rY));
                                pointList3.Add(new Point(iniP.X - i, iniP.Y - rY));
                                pointList4.Add(new Point(iniP.X - i, iniP.Y + rY));
                            }
                        }
                        else
                        { // 焦点在 y 轴上
                            a = Math.Sqrt(Math.Abs((endP.Y - iniP.Y) * (endP.Y - iniP.Y) - (endP.X - iniP.X) * (endP.X - iniP.X) * (k * k)));
                            b = a / k;
                            pointList = new List<Point>();
                            for (double i = a; i <= Math.Abs(endP.Y - iniP.Y); i += 0.5)
                            {
                                double rX = Math.Sqrt(Math.Abs(i * i / k / k - b * b));
                                pointList.Add(new Point(iniP.X - rX, iniP.Y + i));
                                pointList2.Add(new Point(iniP.X + rX, iniP.Y + i));
                                pointList3.Add(new Point(iniP.X - rX, iniP.Y - i));
                                pointList4.Add(new Point(iniP.X + rX, iniP.Y - i));
                            }
                        }
                        try
                        {
                            point = new StylusPointCollection(pointList);
                            stroke = new Stroke(point) { DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone() };
                            strokes.Add(stroke.Clone());
                            point = new StylusPointCollection(pointList2);
                            stroke = new Stroke(point) { DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone() };
                            strokes.Add(stroke.Clone());
                            point = new StylusPointCollection(pointList3);
                            stroke = new Stroke(point) { DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone() };
                            strokes.Add(stroke.Clone());
                            point = new StylusPointCollection(pointList4);
                            stroke = new Stroke(point) { DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone() };
                            strokes.Add(stroke.Clone());
                            if (drawingShapeMode == 25)
                            {
                                //画焦点
                                c = Math.Sqrt(a * a + b * b);
                                stylusPoint = isHyperbolaFocalPointOnXAxis ? new StylusPoint(iniP.X + c, iniP.Y, (float)1.0) : new StylusPoint(iniP.X, iniP.Y + c, (float)1.0);
                                point = new StylusPointCollection();
                                point.Add(stylusPoint);
                                stroke = new Stroke(point) { DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone() };
                                strokes.Add(stroke.Clone());
                                stylusPoint = isHyperbolaFocalPointOnXAxis ? new StylusPoint(iniP.X - c, iniP.Y, (float)1.0) : new StylusPoint(iniP.X, iniP.Y - c, (float)1.0);
                                point = new StylusPointCollection();
                                point.Add(stylusPoint);
                                stroke = new Stroke(point) { DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone() };
                                strokes.Add(stroke.Clone());
                            }
                        }
                        catch
                        {
                            return;
                        }
                    }
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    painterCanvas.Strokes.Add(strokes);
                    break;
                case 20:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    //抛物线 y=ax^2
                    if (Math.Abs(iniP.X - endP.X) < 0.01 || Math.Abs(iniP.Y - endP.Y) < 0.01) return;
                    a = (iniP.Y - endP.Y) / ((iniP.X - endP.X) * (iniP.X - endP.X));
                    pointList = new List<Point>();
                    pointList2 = new List<Point>();
                    for (double i = 0.0; i <= Math.Abs(endP.X - iniP.X); i += 0.5)
                    {
                        pointList.Add(new Point(iniP.X + i, iniP.Y - a * i * i));
                        pointList2.Add(new Point(iniP.X - i, iniP.Y - a * i * i));
                    }
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    point = new StylusPointCollection(pointList2);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    painterCanvas.Strokes.Add(strokes);
                    break;
                case 21:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    //抛物线 y^2=ax
                    if (Math.Abs(iniP.X - endP.X) < 0.01 || Math.Abs(iniP.Y - endP.Y) < 0.01) return;
                    a = (iniP.X - endP.X) / ((iniP.Y - endP.Y) * (iniP.Y - endP.Y));
                    pointList = new List<Point>();
                    pointList2 = new List<Point>();
                    for (double i = 0.0; i <= Math.Abs(endP.Y - iniP.Y); i += 0.5)
                    {
                        pointList.Add(new Point(iniP.X - a * i * i, iniP.Y + i));
                        pointList2.Add(new Point(iniP.X - a * i * i, iniP.Y - i));
                    }
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    point = new StylusPointCollection(pointList2);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    painterCanvas.Strokes.Add(strokes);
                    break;
                case 22:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    //抛物线 y^2=ax, 含焦点
                    if (Math.Abs(iniP.X - endP.X) < 0.01 || Math.Abs(iniP.Y - endP.Y) < 0.01) return;
                    double p = (iniP.Y - endP.Y) * (iniP.Y - endP.Y) / (2 * (iniP.X - endP.X));
                    a = 0.5 / p;
                    pointList = new List<Point>();
                    pointList2 = new List<Point>();
                    for (double i = 0.0; i <= Math.Abs(endP.Y - iniP.Y); i += 0.5)
                    {
                        pointList.Add(new Point(iniP.X - a * i * i, iniP.Y + i));
                        pointList2.Add(new Point(iniP.X - a * i * i, iniP.Y - i));
                    }
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    point = new StylusPointCollection(pointList2);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    stylusPoint = new StylusPoint(iniP.X - p / 2, iniP.Y, (float)1.0);
                    point = new StylusPointCollection();
                    point.Add(stylusPoint);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    painterCanvas.Strokes.Add(strokes);
                    break;
                case 6:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    newIniP = iniP;
                    if (iniP.Y > endP.Y)
                    {
                        newIniP = new Point(iniP.X, endP.Y);
                        endP = new Point(endP.X, iniP.Y);
                    }
                    double topA = Math.Abs(newIniP.X - endP.X);
                    double topB = topA / 2.646;
                    //顶部椭圆
                    pointList = GenerateEllipseGeometry(new Point(newIniP.X, newIniP.Y - topB / 2), new Point(endP.X, newIniP.Y + topB / 2));
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    //底部椭圆
                    pointList = GenerateEllipseGeometry(new Point(newIniP.X, endP.Y - topB / 2), new Point(endP.X, endP.Y + topB / 2), false, true);
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    strokes.Add(GenerateDashedLineEllipseStrokeCollection(new Point(newIniP.X, endP.Y - topB / 2), new Point(endP.X, endP.Y + topB / 2), true, false));
                    //左侧
                    pointList = new List<System.Windows.Point>{
                        new System.Windows.Point(newIniP.X, newIniP.Y),
                        new System.Windows.Point(newIniP.X, endP.Y)
                    };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    //右侧
                    pointList = new List<System.Windows.Point>{
                        new System.Windows.Point(endP.X, newIniP.Y),
                        new System.Windows.Point(endP.X, endP.Y)
                    };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    painterCanvas.Strokes.Add(strokes);
                    break;
                case 7:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    if (iniP.Y > endP.Y)
                    {
                        newIniP = new Point(iniP.X, endP.Y);
                        endP = new Point(endP.X, iniP.Y);
                    }
                    double bottomA = Math.Abs(newIniP.X - endP.X);
                    double bottomB = bottomA / 2.646;
                    //底部椭圆
                    pointList = GenerateEllipseGeometry(new Point(newIniP.X, endP.Y - bottomB / 2), new Point(endP.X, endP.Y + bottomB / 2), false, true);
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    strokes.Add(GenerateDashedLineEllipseStrokeCollection(new Point(newIniP.X, endP.Y - bottomB / 2), new Point(endP.X, endP.Y + bottomB / 2), true, false));
                    //左侧
                    pointList = new List<System.Windows.Point>{
                        new System.Windows.Point((newIniP.X + endP.X) / 2, newIniP.Y),
                        new System.Windows.Point(newIniP.X, endP.Y)
                    };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    //右侧
                    pointList = new List<System.Windows.Point>{
                        new System.Windows.Point((newIniP.X + endP.X) / 2, newIniP.Y),
                        new System.Windows.Point(endP.X, endP.Y)
                    };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    painterCanvas.Strokes.Add(strokes);
                    break;
                case 9:
                    _currentCommitType = CommitReason.ShapeDrawing;
                    if (isFirstTouchCuboid)
                    {
                        //分开画线条方便后期单独擦除某一条棱
                        strokes.Add(GenerateLineStroke(new Point(iniP.X, iniP.Y), new Point(iniP.X, endP.Y)));
                        strokes.Add(GenerateLineStroke(new Point(iniP.X, endP.Y), new Point(endP.X, endP.Y)));
                        strokes.Add(GenerateLineStroke(new Point(endP.X, endP.Y), new Point(endP.X, iniP.Y)));
                        strokes.Add(GenerateLineStroke(new Point(iniP.X, iniP.Y), new Point(endP.X, iniP.Y)));
                        try
                        {
                            painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                        }
                        catch { }
                        lastTempStrokeCollection = strokes;
                        painterCanvas.Strokes.Add(strokes);
                        CuboidFrontRectIniP = iniP;
                        CuboidFrontRectEndP = endP;
                    }
                    else
                    {
                        d = CuboidFrontRectIniP.Y - endP.Y;
                        if (d < 0) d = -d; //就是懒不想做反向的，不要让我去做，想做自己做好之后 Pull Request
                        a = CuboidFrontRectEndP.X - CuboidFrontRectIniP.X; //正面矩形长
                        b = CuboidFrontRectEndP.Y - CuboidFrontRectIniP.Y; //正面矩形宽

                        //横上
                        Point newLineIniP = new Point(CuboidFrontRectIniP.X + d, CuboidFrontRectIniP.Y - d);
                        Point newLineEndP = new Point(CuboidFrontRectEndP.X + d, CuboidFrontRectIniP.Y - d);
                        pointList = new List<System.Windows.Point> { newLineIniP, newLineEndP };
                        point = new StylusPointCollection(pointList);
                        stroke = new Stroke(point) { DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone() };
                        strokes.Add(stroke.Clone());
                        //横下 (虚线)
                        newLineIniP = new Point(CuboidFrontRectIniP.X + d, CuboidFrontRectEndP.Y - d);
                        newLineEndP = new Point(CuboidFrontRectEndP.X + d, CuboidFrontRectEndP.Y - d);
                        strokes.Add(GenerateDashedLineStrokeCollection(newLineIniP, newLineEndP));
                        //斜左上
                        newLineIniP = new Point(CuboidFrontRectIniP.X, CuboidFrontRectIniP.Y);
                        newLineEndP = new Point(CuboidFrontRectIniP.X + d, CuboidFrontRectIniP.Y - d);
                        pointList = new List<System.Windows.Point> { newLineIniP, newLineEndP };
                        point = new StylusPointCollection(pointList);
                        stroke = new Stroke(point) { DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone() };
                        strokes.Add(stroke.Clone());
                        //斜右上
                        newLineIniP = new Point(CuboidFrontRectEndP.X, CuboidFrontRectIniP.Y);
                        newLineEndP = new Point(CuboidFrontRectEndP.X + d, CuboidFrontRectIniP.Y - d);
                        pointList = new List<System.Windows.Point> { newLineIniP, newLineEndP };
                        point = new StylusPointCollection(pointList);
                        stroke = new Stroke(point) { DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone() };
                        strokes.Add(stroke.Clone());
                        //斜左下 (虚线)
                        newLineIniP = new Point(CuboidFrontRectIniP.X, CuboidFrontRectEndP.Y);
                        newLineEndP = new Point(CuboidFrontRectIniP.X + d, CuboidFrontRectEndP.Y - d);
                        strokes.Add(GenerateDashedLineStrokeCollection(newLineIniP, newLineEndP));
                        //斜右下
                        newLineIniP = new Point(CuboidFrontRectEndP.X, CuboidFrontRectEndP.Y);
                        newLineEndP = new Point(CuboidFrontRectEndP.X + d, CuboidFrontRectEndP.Y - d);
                        pointList = new List<System.Windows.Point> { newLineIniP, newLineEndP };
                        point = new StylusPointCollection(pointList);
                        stroke = new Stroke(point) { DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone() };
                        strokes.Add(stroke.Clone());
                        //竖左 (虚线)
                        newLineIniP = new Point(CuboidFrontRectIniP.X + d, CuboidFrontRectIniP.Y - d);
                        newLineEndP = new Point(CuboidFrontRectIniP.X + d, CuboidFrontRectEndP.Y - d);
                        strokes.Add(GenerateDashedLineStrokeCollection(newLineIniP, newLineEndP));
                        //竖右
                        newLineIniP = new Point(CuboidFrontRectEndP.X + d, CuboidFrontRectIniP.Y - d);
                        newLineEndP = new Point(CuboidFrontRectEndP.X + d, CuboidFrontRectEndP.Y - d);
                        pointList = new List<System.Windows.Point> { newLineIniP, newLineEndP };
                        point = new StylusPointCollection(pointList);
                        stroke = new Stroke(point) { DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone() };
                        strokes.Add(stroke.Clone());

                        try
                        {
                            painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                        }
                        catch { }
                        lastTempStrokeCollection = strokes;
                        painterCanvas.Strokes.Add(strokes);
                    }
                    break;
                case 26: // 平面镜
                {
                    _currentCommitType = CommitReason.ShapeDrawing;
                    double optLeft = Math.Min(iniP.X, endP.X);
                    double optRight = Math.Max(iniP.X, endP.X);
                    double optTop = Math.Min(iniP.Y, endP.Y);
                    double optBottom = Math.Max(iniP.Y, endP.Y);
                    if (optRight - optLeft < 6 || optBottom - optTop < 6) return;
                    List<Point> surface = new List<Point> { new Point(optRight, optTop), new Point(optRight, optBottom) };
                    strokes.Add(GenerateLineStroke(surface[0], surface[1]));
                    double hatch = Math.Min((optRight - optLeft) * 0.55, (optBottom - optTop) / 8);
                    AddHatchStrokes(strokes, surface, hatch, -1, -0.65);
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    painterCanvas.Strokes.Add(strokes);
                    break;
                }
                case 32: // 水平平面镜（镜面横线在上方、背面斜线在下方）= case 26 旋转 90°
                {
                    _currentCommitType = CommitReason.ShapeDrawing;
                    double optLeft = Math.Min(iniP.X, endP.X);
                    double optRight = Math.Max(iniP.X, endP.X);
                    double optTop = Math.Min(iniP.Y, endP.Y);
                    double optBottom = Math.Max(iniP.Y, endP.Y);
                    if (optRight - optLeft < 6 || optBottom - optTop < 6) return;
                    // 镜面：上方横线（由竖直版的右侧竖线旋转 90° 而来）
                    List<System.Windows.Point> surface = new List<System.Windows.Point> { new System.Windows.Point(optLeft, optTop), new System.Windows.Point(optRight, optTop) };
                    strokes.Add(GenerateLineStroke(surface[0], surface[1]));
                    // 背面斜线：从横线向下方倾斜（dirY>0 指向下方），长度按高度自适应
                    double hatch = Math.Min((optBottom - optTop) * 0.55, (optRight - optLeft) / 8);
                    AddHatchStrokes(strokes, surface, hatch, 0.65, 1);
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    painterCanvas.Strokes.Add(strokes);
                    break;
                }
                case 27: // 凹面镜（反射面朝左，弧向右鼓）
                {
                    _currentCommitType = CommitReason.ShapeDrawing;
                    double optLeft = Math.Min(iniP.X, endP.X);
                    double optRight = Math.Max(iniP.X, endP.X);
                    double optTop = Math.Min(iniP.Y, endP.Y);
                    double optBottom = Math.Max(iniP.Y, endP.Y);
                    if (optBottom - optTop < 6) return;
                    double sag = Math.Max(6, (optRight - optLeft) * 0.85);
                    List<Point> arc = GenerateVerticalArcPointList(optLeft, optTop, optBottom, sag, true);
                    if (arc.Count < 2) return;
                    strokes.Add(GeneratePolylineStroke(arc));
                    AddHatchStrokes(strokes, arc, Math.Min(sag * 0.6, (optBottom - optTop) / 8), 1, 0.65);
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    painterCanvas.Strokes.Add(strokes);
                    break;
                }
                case 28: // 凸面镜（反射面朝左，弧向左鼓）
                {
                    _currentCommitType = CommitReason.ShapeDrawing;
                    double optLeft = Math.Min(iniP.X, endP.X);
                    double optRight = Math.Max(iniP.X, endP.X);
                    double optTop = Math.Min(iniP.Y, endP.Y);
                    double optBottom = Math.Max(iniP.Y, endP.Y);
                    if (optBottom - optTop < 6) return;
                    double sag = Math.Max(6, (optRight - optLeft) * 0.85);
                    List<Point> arc = GenerateVerticalArcPointList(optRight, optTop, optBottom, sag, false);
                    if (arc.Count < 2) return;
                    strokes.Add(GeneratePolylineStroke(arc));
                    AddHatchStrokes(strokes, arc, Math.Min(sag * 0.6, (optBottom - optTop) / 8), -1, 0.65);
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    painterCanvas.Strokes.Add(strokes);
                    break;
                }
                case 29: // 凸透镜（双凸）
                {
                    _currentCommitType = CommitReason.ShapeDrawing;
                    double optLeft = Math.Min(iniP.X, endP.X);
                    double optRight = Math.Max(iniP.X, endP.X);
                    double optTop = Math.Min(iniP.Y, endP.Y);
                    double optBottom = Math.Max(iniP.Y, endP.Y);
                    if (optRight - optLeft < 6 || optBottom - optTop < 6) return;
                    double chordX = (optLeft + optRight) / 2;
                    double sag = (optRight - optLeft) / 2;
                    List<Point> leftArc = GenerateVerticalArcPointList(chordX, optTop, optBottom, sag, false);
                    List<Point> rightArc = GenerateVerticalArcPointList(chordX, optTop, optBottom, sag, true);
                    if (leftArc.Count < 2 || rightArc.Count < 2) return;
                    strokes.Add(GeneratePolylineStroke(leftArc));
                    strokes.Add(GeneratePolylineStroke(rightArc));
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    painterCanvas.Strokes.Add(strokes);
                    break;
                }
                case 30: // 凹透镜（双凹，上下为平边）
                {
                    _currentCommitType = CommitReason.ShapeDrawing;
                    double optLeft = Math.Min(iniP.X, endP.X);
                    double optRight = Math.Max(iniP.X, endP.X);
                    double optTop = Math.Min(iniP.Y, endP.Y);
                    double optBottom = Math.Max(iniP.Y, endP.Y);
                    if (optRight - optLeft < 6 || optBottom - optTop < 6) return;
                    double sag = (optRight - optLeft) * 0.32;
                    List<Point> leftSide = GenerateVerticalArcPointList(optLeft, optTop, optBottom, sag, true);
                    List<Point> rightSide = GenerateVerticalArcPointList(optRight, optTop, optBottom, sag, false);
                    if (leftSide.Count < 2 || rightSide.Count < 2) return;
                    strokes.Add(GenerateLineStroke(new Point(optLeft, optTop), new Point(optRight, optTop)));
                    strokes.Add(GenerateLineStroke(new Point(optLeft, optBottom), new Point(optRight, optBottom)));
                    strokes.Add(GeneratePolylineStroke(leftSide));
                    strokes.Add(GeneratePolylineStroke(rightSide));
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    painterCanvas.Strokes.Add(strokes);
                    break;
                }
                case 31: // 三棱镜
                {
                    _currentCommitType = CommitReason.ShapeDrawing;
                    double optLeft = Math.Min(iniP.X, endP.X);
                    double optRight = Math.Max(iniP.X, endP.X);
                    double optTop = Math.Min(iniP.Y, endP.Y);
                    double optBottom = Math.Max(iniP.Y, endP.Y);
                    if (optRight - optLeft < 6 || optBottom - optTop < 6) return;
                    Point apex = new Point((optLeft + optRight) / 2, optTop);
                    strokes.Add(GenerateLineStroke(apex, new Point(optLeft, optBottom)));
                    strokes.Add(GenerateLineStroke(new Point(optLeft, optBottom), new Point(optRight, optBottom)));
                    strokes.Add(GenerateLineStroke(new Point(optRight, optBottom), apex));
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    painterCanvas.Strokes.Add(strokes);
                    break;
                }
                case 33: // 电源/电池
                case 34: // 定值电阻
                case 35: // 开关
                case 36: // 小灯泡
                case 37: // 正弦波形
                case 38: // 弹簧
                case 39: // 正点电荷
                case 40: // 负点电荷
                case 41: // 磁场向外 ⊙
                case 42: // 磁场向里 ⊗
                {
                    _currentCommitType = CommitReason.ShapeDrawing;
                    StrokeCollection generated = PhysicsDrawingShapes.Generate(
                        drawingShapeMode, iniP, endP, painterCanvas.DefaultDrawingAttributes);
                    if (generated.Count == 0) return;
                    strokes.Add(generated);
                    try
                    {
                        painterCanvas.Strokes.Remove(lastTempStrokeCollection);
                    }
                    catch { }
                    lastTempStrokeCollection = strokes;
                    painterCanvas.Strokes.Add(strokes);
                    break;
                }
            }
        }

        bool isFirstTouchCuboid = true;
        Point CuboidFrontRectIniP = new Point();
        Point CuboidFrontRectEndP = new Point();

        private void Main_Grid_TouchUp(object sender, TouchEventArgs e)
        {
            painterCanvas_MouseUp(sender, null);
            if (dec.Count == 0)
            {
                isWaitUntilNextTouchDown = false;
            }
        }
        Stroke lastTempStroke = null;
        StrokeCollection lastTempStrokeCollection = new StrokeCollection();
        bool isWaitUntilNextTouchDown = false;
        private List<System.Windows.Point> GenerateEllipseGeometry(System.Windows.Point st, System.Windows.Point ed, bool isDrawTop = true, bool isDrawBottom = true)
        {
            double a = 0.5 * (ed.X - st.X);
            double b = 0.5 * (ed.Y - st.Y);
            // 丝滑度：步长按圆周长自适应。原来固定 0.01 弧度=约 628 点，预览每帧要克隆/重建
            // 这么多点，画大圆时明显掉帧。按“约每 5 像素一个采样点”取步长，并限制在
            // [0.008, 0.06] 之间：小圆仍足够精细，大圆点数从 628 降到约 120~180，肉眼无差别。
            double rx = Math.Abs(a), ry = Math.Abs(b);
            double perimeter = 2 * Math.PI * Math.Sqrt((rx * rx + ry * ry) / 2.0); // Ramanujan 近似
            double step = perimeter / 5.0 > 0 ? (2 * Math.PI) / Math.Max(32.0, perimeter / 5.0) : 0.02;
            if (step < 0.008) step = 0.008;
            if (step > 0.06) step = 0.06;
            List<System.Windows.Point> pointList = new List<System.Windows.Point>();
            if (isDrawTop && isDrawBottom)
            {
                for (double r = 0; r < 2 * Math.PI; r += step)
                {
                    pointList.Add(new System.Windows.Point(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
                }
                // 保证闭合：补上终点（r=2π），避免自适应步长后首尾留缝
                pointList.Add(new System.Windows.Point(0.5 * (st.X + ed.X) + a, 0.5 * (st.Y + ed.Y)));
            }
            else
            {
                if (isDrawBottom)
                {
                    for (double r = 0; r <= Math.PI + 1e-9; r += step)
                    {
                        pointList.Add(new System.Windows.Point(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
                    }
                }
                if (isDrawTop)
                {
                    for (double r = Math.PI; r < 2 * Math.PI; r += step)
                    {
                        pointList.Add(new System.Windows.Point(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
                    }
                    pointList.Add(new System.Windows.Point(0.5 * (st.X + ed.X) + a, 0.5 * (st.Y + ed.Y)));
                }
            }
            return pointList;
        }

        private StrokeCollection GenerateDashedLineEllipseStrokeCollection(System.Windows.Point st, System.Windows.Point ed, bool isDrawTop = true, bool isDrawBottom = true)
        {
            double a = 0.5 * (ed.X - st.X);
            double b = 0.5 * (ed.Y - st.Y);
            double step = 0.05;
            List<System.Windows.Point> pointList = new List<System.Windows.Point>();
            StylusPointCollection point;
            Stroke stroke;
            StrokeCollection strokes = new StrokeCollection();
            if (isDrawBottom)
            {
                for (double i = 0.0; i < 1.0; i += step * 1.66)
                {
                    pointList = new List<Point>();
                    for (double r = Math.PI * i; r <= Math.PI * (i + step); r = r + 0.01)
                    {
                        pointList.Add(new System.Windows.Point(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
                    }
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                }
            }
            if (isDrawTop)
            {
                for (double i = 1.0; i < 2.0; i += step * 1.66)
                {
                    pointList = new List<Point>();
                    for (double r = Math.PI * i; r <= Math.PI * (i + step); r = r + 0.01)
                    {
                        pointList.Add(new System.Windows.Point(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
                    }
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                }
            }
            return strokes;
        }

        private Stroke GenerateLineStroke(System.Windows.Point st, System.Windows.Point ed)
        {
            List<System.Windows.Point> pointList = new List<System.Windows.Point>();
            StylusPointCollection point;
            Stroke stroke;
            pointList = new List<System.Windows.Point>{
                new System.Windows.Point(st.X, st.Y),
                new System.Windows.Point(ed.X, ed.Y)
            };
            point = new StylusPointCollection(pointList);
            stroke = new Stroke(point)
            {
                DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
            };
            return stroke;
        }

        private Stroke GenerateArrowLineStroke(System.Windows.Point st, System.Windows.Point ed)
        {
            List<System.Windows.Point> pointList = new List<System.Windows.Point>();
            StylusPointCollection point;
            Stroke stroke;

            double w = 20, h = 7;
            double theta = Math.Atan2(st.Y - ed.Y, st.X - ed.X);
            double sint = Math.Sin(theta);
            double cost = Math.Cos(theta);

            pointList = new List<Point>
            {
                new Point(st.X, st.Y),
                new Point(ed.X , ed.Y),
                new Point(ed.X + (w * cost - h * sint), ed.Y + (w * sint + h * cost)),
                new Point(ed.X,ed.Y),
                new Point(ed.X + (w * cost + h * sint), ed.Y - (h * cost - w * sint))
            };
            point = new StylusPointCollection(pointList);
            stroke = new Stroke(point)
            {
                DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
            };
            return stroke;
        }

        private StrokeCollection GenerateDashedLineStrokeCollection(System.Windows.Point st, System.Windows.Point ed)
        {
            double step = 5;
            List<System.Windows.Point> pointList = new List<System.Windows.Point>();
            StylusPointCollection point;
            Stroke stroke;
            StrokeCollection strokes = new StrokeCollection();
            double d = GetDistance(st, ed);
            double sinTheta = (ed.Y - st.Y) / d;
            double cosTheta = (ed.X - st.X) / d;
            for (double i = 0.0; i < d; i += step * 2.76)
            {
                pointList = new List<System.Windows.Point>{
                    new System.Windows.Point(st.X + i * cosTheta, st.Y + i * sinTheta),
                    new System.Windows.Point(st.X + Math.Min(i + step, d) * cosTheta, st.Y + Math.Min(i + step, d) * sinTheta)
                };
                point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                };
                strokes.Add(stroke.Clone());
            }
            return strokes;
        }

        private StrokeCollection GenerateDotLineStrokeCollection(System.Windows.Point st, System.Windows.Point ed)
        {
            double step = 3;
            List<System.Windows.Point> pointList = new List<System.Windows.Point>();
            StylusPointCollection point;
            Stroke stroke;
            StrokeCollection strokes = new StrokeCollection();
            double d = GetDistance(st, ed);
            double sinTheta = (ed.Y - st.Y) / d;
            double cosTheta = (ed.X - st.X) / d;
            for (double i = 0.0; i < d; i += step * 2.76)
            {
                var stylusPoint = new StylusPoint(st.X + i * cosTheta, st.Y + i * sinTheta, (float)0.8);
                point = new StylusPointCollection();
                point.Add(stylusPoint);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                };
                strokes.Add(stroke.Clone());
            }
            return strokes;
        }

        private Stroke GeneratePolylineStroke(List<System.Windows.Point> pts)
        {
            StylusPointCollection point = new StylusPointCollection(pts);
            return new Stroke(point)
            {
                DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
            };
        }

        /// <summary>
        /// 生成竖直方向的圆弧点列：弦位于 chordX（从 top 到 bottom），向左右鼓出 sagitta。
        /// 用于平面镜/凹面镜/凸面镜/透镜的轮廓。
        /// </summary>
        private List<System.Windows.Point> GenerateVerticalArcPointList(double chordX, double top, double bottom, double sagitta, bool bulgeRight)
        {
            List<System.Windows.Point> list = new List<System.Windows.Point>();
            double height = bottom - top;
            if (height < 0.001) return list;
            double h = height / 2;
            double d = Math.Max(0.01, Math.Abs(sagitta));
            double r = (h * h + d * d) / (2 * d);
            double centerY = top + h;
            double centerX = bulgeRight ? chordX - (r - d) : chordX + (r - d);
            int segments = Math.Max(16, (int)(height / 8));
            for (int i = 0; i <= segments; i++)
            {
                double y = top + height * i / segments;
                double dy = y - centerY;
                double dx = Math.Sqrt(Math.Max(0, r * r - dy * dy));
                list.Add(new System.Windows.Point(bulgeRight ? centerX + dx : centerX - dx, y));
            }
            return list;
        }

        /// <summary>沿轮廓按弧长均匀铺短斜线，模拟镜面背面的阴影线。</summary>
        private void AddHatchStrokes(StrokeCollection strokes, List<System.Windows.Point> surface, double hatchLength, double dirX, double dirY)
        {
            if (surface == null || surface.Count < 2 || hatchLength <= 1) return;
            double norm = Math.Sqrt(dirX * dirX + dirY * dirY);
            if (norm < 0.001) return;
            dirX /= norm;
            dirY /= norm;
            double step = Math.Max(12, hatchLength * 0.9);
            double carry = step * 0.5;
            for (int i = 1; i < surface.Count; i++)
            {
                System.Windows.Point p0 = surface[i - 1];
                System.Windows.Point p1 = surface[i];
                double dx = p1.X - p0.X;
                double dy = p1.Y - p0.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 0.001) continue;
                for (double t = carry; t < len; t += step)
                {
                    System.Windows.Point sp = new System.Windows.Point(p0.X + dx * t / len, p0.Y + dy * t / len);
                    strokes.Add(GenerateLineStroke(sp, new System.Windows.Point(sp.X + dirX * hatchLength, sp.Y + dirY * hatchLength)));
                }
                carry = carry + Math.Ceiling((len - carry) / step) * step - len;
            }
        }

        bool isMouseDown = false;
        private void painterCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            isMouseDown = true;
            if (NeedUpdateIniP())
            {
                iniP = e.GetPosition(painterCanvas);
            }
        }

        private void painterCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            // 只有图形工具需要在移动时重画预览；笔迹由 InkCanvas 自己绘制
            if (isMouseDown && drawingShapeMode != 0)
            {
                MouseTouchMove(e.GetPosition(painterCanvas));
            }
        }

        private void painterCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            // circles 里每个 Circle 都强引用一个 Stroke，而清理逻辑原先只存在于
            // InkToShapeProcess 内（该分支在 64 位进程下根本不执行），
            // 导致 circles 随画圆次数单调增长、已擦除的笔迹也无法被 GC 回收。
            PruneCircles();
            if (drawingShapeMode == 5 && lastTempStroke != null && lastTempStroke.StylusPoints.Count >= 2)
            {
                Circle circle = new Circle(new Point(), 0, lastTempStroke);
                circle.R = GetDistance(circle.Stroke.StylusPoints[0].ToPoint(), circle.Stroke.StylusPoints[circle.Stroke.StylusPoints.Count / 2].ToPoint()) / 2;
                circle.Centroid = new Point((circle.Stroke.StylusPoints[0].X + circle.Stroke.StylusPoints[circle.Stroke.StylusPoints.Count / 2].X) / 2,
                                            (circle.Stroke.StylusPoints[0].Y + circle.Stroke.StylusPoints[circle.Stroke.StylusPoints.Count / 2].Y) / 2);
                circles.Add(circle);
            }
            if (drawingShapeMode != 9 && drawingShapeMode != 0 && drawingShapeMode != 24 && drawingShapeMode != 25)
            {
                if (isLongPressSelected)
                {

                }
                else
                {
                    BtnPen_Click(null, null); //画完一次还原到笔模式
                }
            }
            if (drawingShapeMode == 9)
            {
                if (isFirstTouchCuboid)
                {
                    if (CuboidStrokeCollection == null) CuboidStrokeCollection = new StrokeCollection();
                    isFirstTouchCuboid = false;
                    Point newIniP = new Point(Math.Min(CuboidFrontRectIniP.X, CuboidFrontRectEndP.X), Math.Min(CuboidFrontRectIniP.Y, CuboidFrontRectEndP.Y));
                    Point newEndP = new Point(Math.Max(CuboidFrontRectIniP.X, CuboidFrontRectEndP.X), Math.Max(CuboidFrontRectIniP.Y, CuboidFrontRectEndP.Y));
                    CuboidFrontRectIniP = newIniP;
                    CuboidFrontRectEndP = newEndP;
                    CuboidStrokeCollection.Add(lastTempStrokeCollection);
                }
                else
                {
                    BtnPen_Click(null, null); //画完还原到笔模式
                    if (_currentCommitType == CommitReason.ShapeDrawing)
                    {
                        CuboidStrokeCollection.Add(lastTempStrokeCollection);
                        _currentCommitType = CommitReason.UserInput;
                        timeMachine.CommitStrokeUserInputHistory(CuboidStrokeCollection);
                        CuboidStrokeCollection = null;
                    }
                }
            }
            if (drawingShapeMode == 24 || drawingShapeMode == 25)
            {
                if (drawMultiStepShapeCurrentStep == 0)
                {
                    drawMultiStepShapeCurrentStep = 1;
                }
                else
                {
                    drawMultiStepShapeCurrentStep = 0;
                    if (drawMultiStepShapeSpecialStrokeCollection != null)
                    {
                        bool opFlag = false;
                        switch (Settings.Canvas.HyperbolaAsymptoteOption)
                        {
                            case OptionalOperation.Yes:
                                opFlag = true;
                                break;
                            case OptionalOperation.No:
                                opFlag = false;
                                break;
                            case OptionalOperation.Ask:
                                opFlag = MessageBox.Show("是否移除渐近线？", desk.AppIdentity.ProductName, MessageBoxButton.YesNo) != MessageBoxResult.Yes;
                                break;
                        };
                        if (!opFlag)
                        {
                            painterCanvas.Strokes.Remove(drawMultiStepShapeSpecialStrokeCollection);
                        }
                    }
                    BtnPen_Click(null, null); //画完还原到笔模式
                }
            }
            isMouseDown = false;
            if (ReplacedStroke != null || AddedStroke != null)
            {
                // 逐点擦除碎片的基线平时不记录（见 StrokesOnStrokesChanged），这里碎片
                // 已经固定，只对仍在画布上的碎片各克隆一次，供后续操作撤销使用。
                // 擦除期间碎片会被反复切分，AddedStroke 里混有大量已移出画布的旧碎片，
                // 逐个 Contains 是线性查找（O(碎片数×笔迹数)）；直接遍历画布并按字典缺键判断
                // 是 O(笔迹数)，结果等价。
                if (AddedStroke != null)
                {
                    foreach (var stroke in painterCanvas.Strokes)
                    {
                        if (!StrokeInitialHistory.ContainsKey(stroke))
                        {
                            StrokeInitialHistory[stroke] = stroke.StylusPoints.Clone();
                        }
                    }
                }
                timeMachine.CommitStrokeEraseHistory(ReplacedStroke, AddedStroke);
                AddedStroke = null;
                ReplacedStroke = null;
            }
            if (_currentCommitType == CommitReason.ShapeDrawing && drawingShapeMode != 9)
            {
                _currentCommitType = CommitReason.UserInput;
                StrokeCollection collection = null;
                if (lastTempStrokeCollection != null && lastTempStrokeCollection.Count > 0)
                {
                    collection = lastTempStrokeCollection;
                }
                else if (lastTempStroke != null)
                {
                    collection = new StrokeCollection() { lastTempStroke };
                }
                if (collection != null)
                {
                    timeMachine.CommitStrokeUserInputHistory(collection);
                }
            }
            lastTempStroke = null;
            lastTempStrokeCollection = null;
            if (StrokeManipulationHistory?.Count > 0)
            {
                timeMachine.CommitStrokeManipulationHistory(StrokeManipulationHistory);
                foreach (var item in StrokeManipulationHistory)
                {
                    StrokeInitialHistory[item.Key] = item.Value.Item2;
                }
                StrokeManipulationHistory = null;
            }
            if (DrawingAttributesHistory.Count > 0)
            {
                timeMachine.CommitStrokeDrawingAttributesHistory(DrawingAttributesHistory);
                DrawingAttributesHistory = new Dictionary<Stroke, Tuple<DrawingAttributes, DrawingAttributes>>();
                foreach (var item in DrawingAttributesHistoryFlag)
                {
                    item.Value.Clear();
                }
            }
        }

        private bool NeedUpdateIniP()
        {
            if (drawingShapeMode == 24 || drawingShapeMode == 25)
            {
                if (drawMultiStepShapeCurrentStep == 1) return false;
            }
            return true;
        }

        #endregion Shape Drawing

        #region Whiteboard Controls

        StrokeCollection[] strokeCollections = new StrokeCollection[101];
        bool[] whiteboadLastModeIsRedo = new bool[101];
        StrokeCollection lastTouchDownStrokeCollection = new StrokeCollection();
        private int _touchDownStrokeCount = -1;   // 落笔时的笔画数快照（替代整表 Clone，避免每次落笔 O(n) 拷贝）

        int CurrentWhiteboardIndex = 1;
        int WhiteboardTotalCount = 1;
        TimeMachineHistory[][] TimeMachineHistories = new TimeMachineHistory[101][]; //最多99页，0用来存储非白板时的墨迹以便还原

        private void SaveStrokes(bool isBackupMain = false)
        {
            if (isBackupMain)
            {
                var timeMachineHistory = timeMachine.ExportTimeMachineHistory();
                TimeMachineHistories[0] = timeMachineHistory;
                timeMachine.ClearStrokeHistory();

            }
            else
            {
                var timeMachineHistory = timeMachine.ExportTimeMachineHistory();
                TimeMachineHistories[CurrentWhiteboardIndex] = timeMachineHistory;
                timeMachine.ClearStrokeHistory();
            }
        }

        private void ClearStrokes(bool isErasedByCode)
        {

            _currentCommitType = CommitReason.ClearingCanvas;
            if (isErasedByCode) _currentCommitType = CommitReason.CodeInput;
            painterCanvas.Strokes.Clear();
            _currentCommitType = CommitReason.UserInput;
        }

        private void RestoreStrokes(bool isBackupMain = false)
        {
            try
            {
                if (TimeMachineHistories[CurrentWhiteboardIndex] == null) return; //防止白板打开后不居中
                if (isBackupMain)
                {
                    timeMachine.ImportTimeMachineHistory(TimeMachineHistories[0]);
                    foreach (var item in TimeMachineHistories[0])
                    {
                        ApplyHistoryToCanvas(item);
                    }
                }
                else
                {
                    timeMachine.ImportTimeMachineHistory(TimeMachineHistories[CurrentWhiteboardIndex]);
                    foreach (var item in TimeMachineHistories[CurrentWhiteboardIndex])
                    {
                        ApplyHistoryToCanvas(item);
                    }
                }
            }
            catch { }
        }

        private void BtnWhiteBoardSwitchPrevious_Click(object sender, EventArgs e)
        {
            if (CurrentWhiteboardIndex <= 1) return;

            SaveStrokes();

            ClearStrokes(true);
            CurrentWhiteboardIndex--;

            RestoreStrokes();

            UpdateIndexInfoDisplay();

            ApplyWbPageImage();
            SyncShowControllerIndex();
        }

        private void BtnWhiteBoardSwitchNext_Click(object sender, EventArgs e)
        {
            if (CurrentWhiteboardIndex >= WhiteboardTotalCount)
            {
                BtnWhiteBoardAdd_Click(sender, e);
                return;
            }
            if (Settings.Automation.IsAutoSaveStrokesAtClear && painterCanvas.Strokes.Count > Settings.Automation.MinimumAutomationStrokeNumber)
            {
                SaveScreenShot(true);
                if (Settings.Automation.IsAutoSaveStrokesAtScreenshot) SavePainterCanvasStrokes(false);
            }
            SaveStrokes();


            ClearStrokes(true);
            CurrentWhiteboardIndex++;

            RestoreStrokes();

            UpdateIndexInfoDisplay();

            ApplyWbPageImage();
            SyncShowControllerIndex();
        }

        private void BtnWhiteBoardAdd_Click(object sender, EventArgs e)
        {
            if (WhiteboardTotalCount >= 99) return;
            if (Settings.Automation.IsAutoSaveStrokesAtClear && painterCanvas.Strokes.Count > Settings.Automation.MinimumAutomationStrokeNumber)
            {
                SaveScreenShot(true);
                if (Settings.Automation.IsAutoSaveStrokesAtScreenshot) SavePainterCanvasStrokes(false);
            }
            SaveStrokes();
            ClearStrokes(true);

            WhiteboardTotalCount++;
            CurrentWhiteboardIndex++;

            if (CurrentWhiteboardIndex != WhiteboardTotalCount)
            {
                for (int i = WhiteboardTotalCount; i > CurrentWhiteboardIndex; i--)
                {
                    TimeMachineHistories[i] = TimeMachineHistories[i - 1];
                    _wbPageImages[i] = _wbPageImages[i - 1];
                }
            }

            UpdateIndexInfoDisplay();

            if (WhiteboardTotalCount >= 99) BtnWhiteBoardAdd.IsEnabled = false;

            ApplyWbPageImage();
            SyncShowControllerIndex();
        }

        private void BtnWhiteBoardDelete_Click(object sender, RoutedEventArgs e)
        {
            ClearStrokes(true);

            if (CurrentWhiteboardIndex != WhiteboardTotalCount)
            {
                for (int i = CurrentWhiteboardIndex; i <= WhiteboardTotalCount; i++)
                {
                    TimeMachineHistories[i] = TimeMachineHistories[i + 1];
                    _wbPageImages[i] = _wbPageImages[i + 1];
                }
            }
            else
            {
                CurrentWhiteboardIndex--;
            }

            WhiteboardTotalCount--;
            if (WhiteboardTotalCount + 1 < _wbPageImages.Length) _wbPageImages[WhiteboardTotalCount + 1] = null;
            if (WhiteboardTotalCount < 1) { WhiteboardTotalCount = 1; CurrentWhiteboardIndex = 1; }

            RestoreStrokes();

            UpdateIndexInfoDisplay();

            if (WhiteboardTotalCount < 99) BtnWhiteBoardAdd.IsEnabled = true;

            ApplyWbPageImage();
            SyncShowControllerIndex();
        }

        private void UpdateIndexInfoDisplay()
        {
            TextBlockWhiteBoardIndexInfo.Text = string.Format("{0} / {1}", CurrentWhiteboardIndex, WhiteboardTotalCount);

            if (CurrentWhiteboardIndex == 1)
            {
                BtnWhiteBoardSwitchPrevious.IsEnabled = false;
            }
            else
            {
                BtnWhiteBoardSwitchPrevious.IsEnabled = true;
            }

            if (CurrentWhiteboardIndex == WhiteboardTotalCount)
            {
                BtnWhiteBoardSwitchNext.IsEnabled = false;
            }
            else
            {
                BtnWhiteBoardSwitchNext.IsEnabled = true;
            }

            if (WhiteboardTotalCount == 1)
            {
                BtnWhiteBoardDelete.IsEnabled = false;
            }
            else
            {
                BtnWhiteBoardDelete.IsEnabled = true;
            }
        }

        private void SetColors()
        {
            if (currentMode != 0 && !Settings.Canvas.UsingWhiteboard)
            {
                if (File.Exists(App.RootPath + "Colors\\Light.ini"))
                {
                    try
                    {
                        string[] lightColors = File.ReadAllLines(App.RootPath + "Colors\\Light.ini");
                        // 少于 4 行时整体回退默认值，避免只设置部分按钮、配色不一致
                        if (lightColors.Length < 4) throw new FormatException("亮色配置文件不足 4 行");
                        BtnColorRed.Background = new SolidColorBrush(StringToColor(lightColors[0]));
                        BtnColorGreen.Background = new SolidColorBrush(StringToColor(lightColors[1]));
                        BtnColorBlue.Background = new SolidColorBrush(StringToColor(lightColors[2]));
                        BtnColorYellow.Background = new SolidColorBrush(StringToColor(lightColors[3]));
                    }
                    catch (Exception) { ShowNotification("读取亮色画笔颜色配置文件时遇到问题"); }
                }
                else
                {
                    BtnColorRed.Background = new SolidColorBrush(StringToColor("#FFFF3333"));
                    BtnColorGreen.Background = new SolidColorBrush(StringToColor("#FF1ED760"));
                    BtnColorBlue.Background = new SolidColorBrush(StringToColor("#FF239AD6"));
                    BtnColorYellow.Background = new SolidColorBrush(StringToColor("#FFFFC000"));
                }
            }
            else
            {
                if (File.Exists(App.RootPath + "Colors\\Dark.ini"))
                {
                    try
                    {
                        string[] darkColors = File.ReadAllLines(App.RootPath + "Colors\\Dark.ini");
                        if (darkColors.Length < 4) throw new FormatException("深色配置文件不足 4 行");
                        BtnColorRed.Background = new SolidColorBrush(StringToColor(darkColors[0]));
                        BtnColorGreen.Background = new SolidColorBrush(StringToColor(darkColors[1]));
                        BtnColorBlue.Background = new SolidColorBrush(StringToColor(darkColors[2]));
                        BtnColorYellow.Background = new SolidColorBrush(StringToColor(darkColors[3]));
                    }
                    catch (Exception) { ShowNotification("读取深色画笔颜色配置文件时遇到问题"); }
                }
                else
                {
                    BtnColorRed.Background = new SolidColorBrush(Colors.Red);
                    BtnColorGreen.Background = new SolidColorBrush(StringToColor("#FF169141"));
                    BtnColorBlue.Background = new SolidColorBrush(StringToColor("#FF239AD6"));
                    BtnColorYellow.Background = new SolidColorBrush(StringToColor("#FFF38B00"));
                }
            }
        }

        #endregion Whiteboard Controls

        #region Simulate Pen Pressure & Ink To Shape

        StrokeCollection newStrokes = new StrokeCollection();
        List<Circle> circles = new List<Circle>();

        /// <summary>
        /// 移除 circles 中已不在画布上的条目。必须无条件调用：每个 Circle 强引用 Stroke，
        /// 不清理会让已擦除的笔迹对象也无法回收。倒序遍历以免 RemoveAt 后漏删。
        /// </summary>
        private void PruneCircles()
        {
            for (int i = circles.Count - 1; i >= 0; i--)
            {
                if (circles[i] == null || circles[i].Stroke == null || !painterCanvas.Strokes.Contains(circles[i].Stroke))
                {
                    circles.RemoveAt(i);
                }
            }
        }

        // 每笔都 new Random() 既浪费，在 .NET Framework 上还可能因时间种子相同拿到同一序列；
        // 这里共用一个实例。
        private static readonly Random _shapeRandom = new Random();

        /// <summary>
        /// 智能几何识别：把单笔手绘规整为标准形状并替换上屏。成功返回 true。
        /// 复用现有撤销（SetNewBackupOfStroke）与提交类型机制，规整结果可正常撤销。
        /// </summary>
        private bool TryGeometryShape(Stroke stroke)
        {
            try
            {
                GeoShapeResult result = GeometryShapeRecognizer.Recognize(
                    stroke, Settings.Smart.ShapeMinSpan, Settings.Smart.ShapeTolerance);
                if (result == null || result.SubPaths.Count == 0 || result.Confidence < 0.5)
                    return false;

                // 构造规整后的笔画集合：每条子路径一条 Stroke，沿用当前画笔样式。
                var shaped = new StrokeCollection();
                foreach (List<Point> sub in result.SubPaths)
                {
                    if (sub == null || sub.Count < 2) continue;
                    var pts = new StylusPointCollection(sub);
                    var s = new Stroke(pts)
                    {
                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                    };
                    shaped.Add(s);
                }
                if (shaped.Count == 0) return false;

                SetNewBackupOfStroke();
                _currentCommitType = CommitReason.ShapeRecognition;
                painterCanvas.Strokes.Remove(stroke);
                painterCanvas.Strokes.Add(shaped);
                _currentCommitType = CommitReason.UserInput;
                GridPainterCanvasSelectionCover.Visibility = Visibility.Collapsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        //此函数中的所有代码版权所有 WXRIW，在其他项目中使用前必须提前联系（wxriw@outlook.com），谢谢！
        private void painterCanvas_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
        {
            try
            {
                painterCanvas.Opacity = 1;

                // ===== 智能笔迹平滑（去抖）=====
                // 只对「非压感笔」且点数足够的笔画生效；压感笔本身精度高，保持原样。
                // 放在形状识别之前，让几何识别拿到更干净的点序列。低强度 O(n)，低配无感。
                if (Settings.Smart != null && Settings.Smart.StrokeSmoothingEnabled &&
                    !StrokeSmoother.IsPressureSensitive(e.Stroke.StylusPoints))
                {
                    try
                    {
                        var smoothed = StrokeSmoother.Smooth(e.Stroke.StylusPoints,
                            Settings.Smart.StrokeSmoothingStrength);
                        if (smoothed != null && smoothed.Count > 4)
                            e.Stroke.StylusPoints = smoothed;
                    }
                    catch { }
                }

                // ===== 智能几何形状识别（优先）=====
                // 纯 C# 几何算法，无 x86 原生依赖，Win7-Win11 一致且快。识别成功时直接把
                // 规整后的笔画替换上屏并返回；未识别/关闭时再回退到下面的旧 InkAnalyzer 流程。
                // 关键：几何识别（直线/箭头等）必须同样受「墨迹识别」总开关控制——
                // 该开关关闭后不得有任何笔迹被转化成形状，否则用户写着写着就冒出箭头。
                bool inkRecognitionOn = Settings.InkToShape != null && Settings.InkToShape.IsInkToShapeEnabled;
                if (inkRecognitionOn && Settings.Smart != null && Settings.Smart.GeometryShapeEnabled &&
                    TryGeometryShape(e.Stroke))
                {
                    return;
                }

                if (inkRecognitionOn && !Environment.Is64BitProcess)
                {
                    void InkToShapeProcess()
                    {
                        try
                        {
                            newStrokes.Add(e.Stroke);
                            if (newStrokes.Count > 4) newStrokes.RemoveAt(0);
                            for (int i = 0; i < newStrokes.Count; i++)
                            {
                                if (!painterCanvas.Strokes.Contains(newStrokes[i])) newStrokes.RemoveAt(i--);
                            }
                            for (int i = circles.Count - 1; i >= 0; i--)
                            {
                                if (!painterCanvas.Strokes.Contains(circles[i].Stroke)) circles.RemoveAt(i);
                            }
                            var result = InkRecognizeHelper.RecognizeShape(newStrokes);
                            // 单笔时下面循环里的 strokeReco 与 newStrokes 完全相同，
                            // 再跑一次 InkAnalyzer.Analyze() 纯属重复（这是最热的非托管调用）。
                            // 只有多笔才需要「从最后一笔往前累加」寻找能构成圆/椭圆的子集。
                            if (newStrokes.Count > 1)
                            {
                                var strokeReco = new StrokeCollection();
                                for (int i = newStrokes.Count - 1; i >= 0; i--)
                                {
                                    strokeReco.Add(newStrokes[i]);
                                    var newResult = InkRecognizeHelper.RecognizeShape(strokeReco);
                                    if (newResult?.InkDrawingNode != null &&
                                        (newResult.InkDrawingNode.GetShapeName() == "Circle" || newResult.InkDrawingNode.GetShapeName() == "Ellipse"))
                                    {
                                        result = newResult;
                                        break;
                                    }
                                }
                            }
                            // RecognizeShape 识别失败返回 default(null)，必须先判空再用
                            if (result?.InkDrawingNode == null) return;
                            if (result.InkDrawingNode.GetShapeName() == "Circle")
                            {
                                var shape = result.InkDrawingNode.GetShape();
                                if (shape.Width > 75)
                                {
                                    foreach (Circle circle in circles)
                                    {
                                        //判断是否画同心圆
                                        if (Math.Abs(result.Centroid.X - circle.Centroid.X) / shape.Width < 0.12 &&
                                            Math.Abs(result.Centroid.Y - circle.Centroid.Y) / shape.Width < 0.12)
                                        {
                                            result.Centroid = circle.Centroid;
                                            break;
                                        }
                                        else
                                        {
                                            double d = (result.Centroid.X - circle.Centroid.X) * (result.Centroid.X - circle.Centroid.X) +
                                               (result.Centroid.Y - circle.Centroid.Y) * (result.Centroid.Y - circle.Centroid.Y);
                                            d = Math.Sqrt(d);
                                            //判断是否画外切圆
                                            double x = shape.Width / 2.0 + circle.R - d;
                                            if (Math.Abs(x) / shape.Width < 0.1)
                                            {
                                                double sinTheta = (result.Centroid.Y - circle.Centroid.Y) / d;
                                                double cosTheta = (result.Centroid.X - circle.Centroid.X) / d;
                                                double newX = result.Centroid.X + x * cosTheta;
                                                double newY = result.Centroid.Y + x * sinTheta;
                                                result.Centroid = new Point(newX, newY);
                                            }
                                            //判断是否画外切圆
                                            x = Math.Abs(circle.R - shape.Width / 2.0) - d;
                                            if (Math.Abs(x) / shape.Width < 0.1)
                                            {
                                                double sinTheta = (result.Centroid.Y - circle.Centroid.Y) / d;
                                                double cosTheta = (result.Centroid.X - circle.Centroid.X) / d;
                                                double newX = result.Centroid.X + x * cosTheta;
                                                double newY = result.Centroid.Y + x * sinTheta;
                                                result.Centroid = new Point(newX, newY);
                                            }
                                        }
                                    }

                                    Point iniP = new Point(result.Centroid.X - shape.Width / 2, result.Centroid.Y - shape.Height / 2);
                                    Point endP = new Point(result.Centroid.X + shape.Width / 2, result.Centroid.Y + shape.Height / 2);
                                    var pointList = GenerateEllipseGeometry(iniP, endP);
                                    var point = new StylusPointCollection(pointList);
                                    var stroke = new Stroke(point)
                                    {
                                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                                    };
                                    circles.Add(new Circle(result.Centroid, shape.Width / 2.0, stroke));
                                    SetNewBackupOfStroke();
                                    _currentCommitType = CommitReason.ShapeRecognition;
                                    painterCanvas.Strokes.Remove(result.InkDrawingNode.Strokes);
                                    painterCanvas.Strokes.Add(stroke);
                                    _currentCommitType = CommitReason.UserInput;
                                    newStrokes = new StrokeCollection();
                                }
                            }
                            else if (result.InkDrawingNode.GetShapeName().Contains("Ellipse"))
                            {
                                var shape = result.InkDrawingNode.GetShape();
                                //var shape1 = result.InkDrawingNode.GetShape();
                                //shape1.Fill = Brushes.Gray;
                                //Canvas.Children.Add(shape1);
                                var p = result.InkDrawingNode.HotPoints;
                                // 识别器可能返回点数不足的热点集，取 p[0..3] 前必须校验
                                if (p == null || p.Count < 4) return;
                                double a = GetDistance(p[0], p[2]) / 2; //长半轴
                                double b = GetDistance(p[1], p[3]) / 2; //短半轴
                                if (a < b)
                                {
                                    double t = a;
                                    a = b;
                                    b = t;
                                }

                                result.Centroid = new Point((p[0].X + p[2].X) / 2, (p[0].Y + p[2].Y) / 2);
                                bool needRotation = true;

                                if ((shape.Width > 75 || shape.Height > 75) && p.Count == 4)
                                {
                                    Point iniP = new Point(result.Centroid.X - shape.Width / 2, result.Centroid.Y - shape.Height / 2);
                                    Point endP = new Point(result.Centroid.X + shape.Width / 2, result.Centroid.Y + shape.Height / 2);

                                    foreach (Circle circle in circles)
                                    {
                                        //判断是否画同心椭圆
                                        if (Math.Abs(result.Centroid.X - circle.Centroid.X) / a < 0.2 &&
                                            Math.Abs(result.Centroid.Y - circle.Centroid.Y) / a < 0.2)
                                        {
                                            result.Centroid = circle.Centroid;
                                            iniP = new Point(result.Centroid.X - shape.Width / 2, result.Centroid.Y - shape.Height / 2);
                                            endP = new Point(result.Centroid.X + shape.Width / 2, result.Centroid.Y + shape.Height / 2);

                                            //再判断是否与圆相切
                                            if (Math.Abs(a - circle.R) / a < 0.2)
                                            {
                                                if (shape.Width >= shape.Height)
                                                {
                                                    iniP.X = result.Centroid.X - circle.R;
                                                    endP.X = result.Centroid.X + circle.R;
                                                    iniP.Y = result.Centroid.Y - b;
                                                    endP.Y = result.Centroid.Y + b;
                                                }
                                                else
                                                {
                                                    iniP.Y = result.Centroid.Y - circle.R;
                                                    endP.Y = result.Centroid.Y + circle.R;
                                                    iniP.X = result.Centroid.X - a;
                                                    endP.X = result.Centroid.X + a;
                                                }
                                            }
                                            break;
                                        }
                                        else if (Math.Abs(result.Centroid.X - circle.Centroid.X) / a < 0.2)
                                        {
                                            double sinTheta = Math.Abs(circle.Centroid.Y - result.Centroid.Y) / circle.R;
                                            double cosTheta = Math.Sqrt(1 - sinTheta * sinTheta);
                                            double newA = circle.R * cosTheta;
                                            if (circle.R * sinTheta / circle.R < 0.9 && a / b > 2 && Math.Abs(newA - a) / newA < 0.3)
                                            {
                                                iniP.X = circle.Centroid.X - newA;
                                                endP.X = circle.Centroid.X + newA;
                                                iniP.Y = result.Centroid.Y - newA / 5;
                                                endP.Y = result.Centroid.Y + newA / 5;

                                                double topB = endP.Y - iniP.Y;

                                                SetNewBackupOfStroke();
                                                _currentCommitType = CommitReason.ShapeRecognition;
                                                painterCanvas.Strokes.Remove(result.InkDrawingNode.Strokes);
                                                newStrokes = new StrokeCollection();

                                                var _pointList = GenerateEllipseGeometry(iniP, endP, false, true);
                                                var _point = new StylusPointCollection(_pointList);
                                                var _stroke = new Stroke(_point)
                                                {
                                                    DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                                                };
                                                var _dashedLineStroke = GenerateDashedLineEllipseStrokeCollection(iniP, endP, true, false);
                                                StrokeCollection strokes = new StrokeCollection()
                                                {
                                                    _stroke,
                                                    _dashedLineStroke
                                                };
                                                painterCanvas.Strokes.Add(strokes);
                                                _currentCommitType = CommitReason.UserInput;
                                                return;
                                            }
                                        }
                                        else if (Math.Abs(result.Centroid.Y - circle.Centroid.Y) / a < 0.2)
                                        {
                                            double cosTheta = Math.Abs(circle.Centroid.X - result.Centroid.X) / circle.R;
                                            double sinTheta = Math.Sqrt(1 - cosTheta * cosTheta);
                                            double newA = circle.R * sinTheta;
                                            if (circle.R * sinTheta / circle.R < 0.9 && a / b > 2 && Math.Abs(newA - a) / newA < 0.3)
                                            {
                                                iniP.X = result.Centroid.X - newA / 5;
                                                endP.X = result.Centroid.X + newA / 5;
                                                iniP.Y = circle.Centroid.Y - newA;
                                                endP.Y = circle.Centroid.Y + newA;
                                                needRotation = false;
                                            }
                                        }
                                    }

                                    //纠正垂直与水平关系
                                    var newPoints = FixPointsDirection(p[0], p[2]);
                                    p[0] = newPoints[0];
                                    p[2] = newPoints[1];
                                    newPoints = FixPointsDirection(p[1], p[3]);
                                    p[1] = newPoints[0];
                                    p[3] = newPoints[1];

                                    var pointList = GenerateEllipseGeometry(iniP, endP);
                                    var point = new StylusPointCollection(pointList);
                                    var stroke = new Stroke(point)
                                    {
                                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                                    };

                                    if (needRotation)
                                    {
                                        Matrix m = new Matrix();
                                        FrameworkElement fe = e.Source as FrameworkElement;
                                        double tanTheta = (p[2].Y - p[0].Y) / (p[2].X - p[0].X);
                                        double theta = Math.Atan(tanTheta);
                                        m.RotateAt(theta * 180.0 / Math.PI, result.Centroid.X, result.Centroid.Y);
                                        stroke.Transform(m, false);
                                    }

                                    SetNewBackupOfStroke();
                                    _currentCommitType = CommitReason.ShapeRecognition;
                                    painterCanvas.Strokes.Remove(result.InkDrawingNode.Strokes);
                                    painterCanvas.Strokes.Add(stroke);
                                    _currentCommitType = CommitReason.UserInput;
                                    GridPainterCanvasSelectionCover.Visibility = Visibility.Collapsed;
                                    newStrokes = new StrokeCollection();
                                }
                            }
                            else if (result.InkDrawingNode.GetShapeName().Contains("Triangle"))
                            {
                                var shape = result.InkDrawingNode.GetShape();
                                var p = result.InkDrawingNode.HotPoints;
                                if ((Math.Max(Math.Max(p[0].X, p[1].X), p[2].X) - Math.Min(Math.Min(p[0].X, p[1].X), p[2].X) >= 100 ||
                                    Math.Max(Math.Max(p[0].Y, p[1].Y), p[2].Y) - Math.Min(Math.Min(p[0].Y, p[1].Y), p[2].Y) >= 100) && result.InkDrawingNode.HotPoints.Count == 3)
                                {
                                    //纠正垂直与水平关系
                                    var newPoints = FixPointsDirection(p[0], p[1]);
                                    p[0] = newPoints[0];
                                    p[1] = newPoints[1];
                                    newPoints = FixPointsDirection(p[0], p[2]);
                                    p[0] = newPoints[0];
                                    p[2] = newPoints[1];
                                    newPoints = FixPointsDirection(p[1], p[2]);
                                    p[1] = newPoints[0];
                                    p[2] = newPoints[1];

                                    var pointList = p.ToList();
                                    //pointList.Add(p[0]);
                                    var point = new StylusPointCollection(pointList);
                                    var stroke = new Stroke(GenerateFakePressureTriangle(point))
                                    {
                                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                                    };
                                    SetNewBackupOfStroke();
                                    _currentCommitType = CommitReason.ShapeRecognition;
                                    painterCanvas.Strokes.Remove(result.InkDrawingNode.Strokes);
                                    painterCanvas.Strokes.Add(stroke);
                                    _currentCommitType = CommitReason.UserInput;
                                    GridPainterCanvasSelectionCover.Visibility = Visibility.Collapsed;
                                    newStrokes = new StrokeCollection();
                                }
                            }
                            else if (result.InkDrawingNode.GetShapeName().Contains("Rectangle") ||
                                     result.InkDrawingNode.GetShapeName().Contains("Diamond") ||
                                     result.InkDrawingNode.GetShapeName().Contains("Parallelogram") ||
                                     result.InkDrawingNode.GetShapeName().Contains("Square"))
                            {
                                var shape = result.InkDrawingNode.GetShape();
                                var p = result.InkDrawingNode.HotPoints;
                                if ((Math.Max(Math.Max(Math.Max(p[0].X, p[1].X), p[2].X), p[3].X) - Math.Min(Math.Min(Math.Min(p[0].X, p[1].X), p[2].X), p[3].X) >= 100 ||
                                    Math.Max(Math.Max(Math.Max(p[0].Y, p[1].Y), p[2].Y), p[3].Y) - Math.Min(Math.Min(Math.Min(p[0].Y, p[1].Y), p[2].Y), p[3].Y) >= 100) && result.InkDrawingNode.HotPoints.Count == 4)
                                {
                                    //纠正垂直与水平关系
                                    var newPoints = FixPointsDirection(p[0], p[1]);
                                    p[0] = newPoints[0];
                                    p[1] = newPoints[1];
                                    newPoints = FixPointsDirection(p[1], p[2]);
                                    p[1] = newPoints[0];
                                    p[2] = newPoints[1];
                                    newPoints = FixPointsDirection(p[2], p[3]);
                                    p[2] = newPoints[0];
                                    p[3] = newPoints[1];
                                    newPoints = FixPointsDirection(p[3], p[0]);
                                    p[3] = newPoints[0];
                                    p[0] = newPoints[1];

                                    var pointList = p.ToList();
                                    pointList.Add(p[0]);
                                    var point = new StylusPointCollection(pointList);
                                    var stroke = new Stroke(GenerateFakePressureRectangle(point))
                                    {
                                        DrawingAttributes = painterCanvas.DefaultDrawingAttributes.Clone()
                                    };
                                    SetNewBackupOfStroke();
                                    _currentCommitType = CommitReason.ShapeRecognition;
                                    painterCanvas.Strokes.Remove(result.InkDrawingNode.Strokes);
                                    painterCanvas.Strokes.Add(stroke);
                                    _currentCommitType = CommitReason.UserInput;
                                    GridPainterCanvasSelectionCover.Visibility = Visibility.Collapsed;
                                    newStrokes = new StrokeCollection();
                                }
                            }
                        }
                        catch { }
                    }
                    InkToShapeProcess();
                }

                // 检查是否是压感笔书写
                foreach (StylusPoint stylusPoint in e.Stroke.StylusPoints)
                {
                    if (stylusPoint.PressureFactor != 0.5 && stylusPoint.PressureFactor != 0)
                    {
                        return;
                    }
                }


                try
                {
                    if (e.Stroke.StylusPoints.Count > 3)
                    {
                        Random random = _shapeRandom;
                        double _speed = GetPointSpeed(e.Stroke.StylusPoints[random.Next(0, e.Stroke.StylusPoints.Count - 1)].ToPoint(), e.Stroke.StylusPoints[random.Next(0, e.Stroke.StylusPoints.Count - 1)].ToPoint(), e.Stroke.StylusPoints[random.Next(0, e.Stroke.StylusPoints.Count - 1)].ToPoint());

                        RandWindow.randSeed = (int)(_speed * 100000 * 1000);
                    }
                }
                catch { }

                switch (Settings.Canvas.InkStyle)
                {
                    case 1:
                        try
                        {
                            StylusPointCollection stylusPoints = new StylusPointCollection();
                            int n = e.Stroke.StylusPoints.Count - 1;

                            for (int i = 0; i <= n; i++)
                            {
                                double speed = GetPointSpeed(e.Stroke.StylusPoints[Math.Max(i - 1, 0)].ToPoint(), e.Stroke.StylusPoints[i].ToPoint(), e.Stroke.StylusPoints[Math.Min(i + 1, n)].ToPoint());
                                StylusPoint point = new StylusPoint();
                                if (speed >= 0.25)
                                {
                                    point.PressureFactor = (float)(0.5 - 0.3 * (Math.Min(speed, 1.5) - 0.3) / 1.2);
                                }
                                else if (speed >= 0.05)
                                {
                                    point.PressureFactor = (float)0.5;
                                }
                                else
                                {
                                    point.PressureFactor = (float)(0.5 + 0.4 * (0.05 - speed) / 0.05);
                                }
                                point.X = e.Stroke.StylusPoints[i].X;
                                point.Y = e.Stroke.StylusPoints[i].Y;
                                stylusPoints.Add(point);
                            }
                            //Label.Visibility = Visibility.Visible;
                            //Label.Content = s;
                            e.Stroke.StylusPoints = stylusPoints;
                        }
                        catch
                        {

                        }
                        break;
                    case 0:
                        try
                        {
                            StylusPointCollection stylusPoints = new StylusPointCollection();
                            int n = e.Stroke.StylusPoints.Count - 1;
                            double pressure = 0.1;
                            int x = 10;
                            if (n == 1) return;
                            if (n >= x)
                            {
                                for (int i = 0; i < n - x; i++)
                                {
                                    StylusPoint point = new StylusPoint();

                                    point.PressureFactor = (float)0.5;
                                    point.X = e.Stroke.StylusPoints[i].X;
                                    point.Y = e.Stroke.StylusPoints[i].Y;
                                    stylusPoints.Add(point);
                                }
                                for (int i = n - x; i <= n; i++)
                                {
                                    StylusPoint point = new StylusPoint();

                                    point.PressureFactor = (float)((0.5 - pressure) * (n - i) / x + pressure);
                                    point.X = e.Stroke.StylusPoints[i].X;
                                    point.Y = e.Stroke.StylusPoints[i].Y;
                                    stylusPoints.Add(point);
                                }
                            }
                            else
                            {
                                for (int i = 0; i <= n; i++)
                                {
                                    StylusPoint point = new StylusPoint();

                                    point.PressureFactor = (float)(0.4 * (n - i) / n + pressure);
                                    point.X = e.Stroke.StylusPoints[i].X;
                                    point.Y = e.Stroke.StylusPoints[i].Y;
                                    stylusPoints.Add(point);
                                }
                            }
                            e.Stroke.StylusPoints = stylusPoints;
                        }
                        catch
                        {

                        }
                        break;
                    case 3: //根据 mode == 0 改写，目前暂未完成
                        try
                        {
                            StylusPointCollection stylusPoints = new StylusPointCollection();
                            int n = e.Stroke.StylusPoints.Count - 1;
                            double pressure = 0.1;
                            int x = 8;
                            if (lastTouchDownTime < lastTouchUpTime)
                            {
                                double k = (lastTouchUpTime - lastTouchDownTime) / (n + 1); // 每个点之间间隔 k 毫秒
                                x = (int)(1000 / k); // 取 1000 ms 内的点
                            }

                            if (n >= x)
                            {
                                for (int i = 0; i < n - x; i++)
                                {
                                    StylusPoint point = new StylusPoint();

                                    point.PressureFactor = (float)0.5;
                                    point.X = e.Stroke.StylusPoints[i].X;
                                    point.Y = e.Stroke.StylusPoints[i].Y;
                                    stylusPoints.Add(point);
                                }
                                for (int i = n - x; i <= n; i++)
                                {
                                    StylusPoint point = new StylusPoint();

                                    point.PressureFactor = (float)((0.5 - pressure) * (n - i) / x + pressure);
                                    point.X = e.Stroke.StylusPoints[i].X;
                                    point.Y = e.Stroke.StylusPoints[i].Y;
                                    stylusPoints.Add(point);
                                }
                            }
                            else
                            {
                                for (int i = 0; i <= n; i++)
                                {
                                    StylusPoint point = new StylusPoint();

                                    point.PressureFactor = (float)(0.4 * (n - i) / n + pressure);
                                    point.X = e.Stroke.StylusPoints[i].X;
                                    point.Y = e.Stroke.StylusPoints[i].Y;
                                    stylusPoints.Add(point);
                                }
                            }
                            e.Stroke.StylusPoints = stylusPoints;
                        }
                        catch
                        {

                        }
                        break;
                }
            }
            catch { }
        }

        private void SetNewBackupOfStroke()
        {
            lastTouchDownStrokeCollection = painterCanvas.Strokes.Clone();
            int whiteboardIndex = CurrentWhiteboardIndex;
            if (currentMode == 0)
            {
                whiteboardIndex = 0;
            }
            strokeCollections[whiteboardIndex] = lastTouchDownStrokeCollection;
        }

        public double GetDistance(Point point1, Point point2)
        {
            return Math.Sqrt((point1.X - point2.X) * (point1.X - point2.X) + (point1.Y - point2.Y) * (point1.Y - point2.Y));
        }

        public double GetPointSpeed(Point point1, Point point2, Point point3)
        {
            return (Math.Sqrt((point1.X - point2.X) * (point1.X - point2.X) + (point1.Y - point2.Y) * (point1.Y - point2.Y))
                + Math.Sqrt((point3.X - point2.X) * (point3.X - point2.X) + (point3.Y - point2.Y) * (point3.Y - point2.Y)))
                / 20;
        }

        public Point[] FixPointsDirection(Point p1, Point p2)
        {
            double dx = Math.Abs(p1.X - p2.X);
            double dy = Math.Abs(p1.Y - p2.Y);
            // 完全重合（或亚像素级接近）的点无需纠正；显式判零避免除零产生 NaN/Infinity
            // 传到 Stroke 几何里，那会让 InkCanvas 在后续命中测试/渲染时抛异常。
            if (dx < 1e-9 && dy < 1e-9) return new Point[2] { p1, p2 };

            if (dy < 1e-9 || dx / dy > 8)
            {
                //水平
                double x = dy / 2;
                if (p1.Y > p2.Y)
                {
                    p1.Y -= x;
                    p2.Y += x;
                }
                else
                {
                    p1.Y += x;
                    p2.Y -= x;
                }
            }
            else if (dx < 1e-9 || dy / dx > 8)
            {
                //垂直
                double x = dx / 2;
                if (p1.X > p2.X)
                {
                    p1.X -= x;
                    p2.X += x;
                }
                else
                {
                    p1.X += x;
                    p2.X -= x;
                }
            }

            return new Point[2] { p1, p2 };
        }

        public StylusPointCollection GenerateFakePressureTriangle(StylusPointCollection points)
        {
            var newPoint = new StylusPointCollection();
            newPoint.Add(new StylusPoint(points[0].X, points[0].Y, (float)0.4));
            var cPoint = GetCenterPoint(points[0], points[1]);
            newPoint.Add(new StylusPoint(cPoint.X, cPoint.Y, (float)0.8));
            newPoint.Add(new StylusPoint(points[1].X, points[1].Y, (float)0.4));
            newPoint.Add(new StylusPoint(points[1].X, points[1].Y, (float)0.4));
            cPoint = GetCenterPoint(points[1], points[2]);
            newPoint.Add(new StylusPoint(cPoint.X, cPoint.Y, (float)0.8));
            newPoint.Add(new StylusPoint(points[2].X, points[2].Y, (float)0.4));
            newPoint.Add(new StylusPoint(points[2].X, points[2].Y, (float)0.4));
            cPoint = GetCenterPoint(points[2], points[0]);
            newPoint.Add(new StylusPoint(cPoint.X, cPoint.Y, (float)0.8));
            newPoint.Add(new StylusPoint(points[0].X, points[0].Y, (float)0.4));
            return newPoint;
        }

        public StylusPointCollection GenerateFakePressureRectangle(StylusPointCollection points)
        {
            var newPoint = new StylusPointCollection();
            newPoint.Add(new StylusPoint(points[0].X, points[0].Y, (float)0.4));
            var cPoint = GetCenterPoint(points[0], points[1]);
            newPoint.Add(new StylusPoint(cPoint.X, cPoint.Y, (float)0.8));
            newPoint.Add(new StylusPoint(points[1].X, points[1].Y, (float)0.4));
            newPoint.Add(new StylusPoint(points[1].X, points[1].Y, (float)0.4));
            cPoint = GetCenterPoint(points[1], points[2]);
            newPoint.Add(new StylusPoint(cPoint.X, cPoint.Y, (float)0.8));
            newPoint.Add(new StylusPoint(points[2].X, points[2].Y, (float)0.4));
            newPoint.Add(new StylusPoint(points[2].X, points[2].Y, (float)0.4));
            cPoint = GetCenterPoint(points[2], points[3]);
            newPoint.Add(new StylusPoint(cPoint.X, cPoint.Y, (float)0.8));
            newPoint.Add(new StylusPoint(points[3].X, points[3].Y, (float)0.4));
            newPoint.Add(new StylusPoint(points[3].X, points[3].Y, (float)0.4));
            cPoint = GetCenterPoint(points[3], points[0]);
            newPoint.Add(new StylusPoint(cPoint.X, cPoint.Y, (float)0.8));
            newPoint.Add(new StylusPoint(points[0].X, points[0].Y, (float)0.4));
            return newPoint;
        }

        public Point GetCenterPoint(Point point1, Point point2)
        {
            return new Point((point1.X + point2.X) / 2, (point1.Y + point2.Y) / 2);
        }

        public StylusPoint GetCenterPoint(StylusPoint point1, StylusPoint point2)
        {
            return new StylusPoint((point1.X + point2.X) / 2, (point1.Y + point2.Y) / 2);
        }

        #endregion

        #region Functions

        /// <summary>
        /// 传入域名返回对应的IP 
        /// </summary>
        /// <param name="domainName">域名</param>
        /// <returns></returns>
        public static string GetIp(string domainName)
        {
            domainName = domainName.Replace("http://", "").Replace("https://", "");
            IPHostEntry hostEntry = Dns.GetHostEntry(domainName);
            IPEndPoint ipEndPoint = new IPEndPoint(hostEntry.AddressList[0], 0);
            return ipEndPoint.Address.ToString();
        }

        #region 开机自启
        /// <summary>
        /// 开机自启创建
        /// </summary>
        /// <param name="exeName">程序名称</param>
        /// <returns></returns>
        public static bool StartAutomaticallyCreate(string exeName)
        {
            try
            {
                WshShell shell = new WshShell();
                IWshShortcut shortcut = (IWshShortcut)shell.CreateShortcut(Environment.GetFolderPath(Environment.SpecialFolder.Startup) + "\\" + exeName + ".lnk");
                //设置快捷方式的目标所在的位置(源程序完整路径)
                shortcut.TargetPath = System.Windows.Forms.Application.ExecutablePath;
                //应用程序的工作目录
                //当用户没有指定一个具体的目录时，快捷方式的目标应用程序将使用该属性所指定的目录来装载或保存文件。
                shortcut.WorkingDirectory = System.Environment.CurrentDirectory;
                //目标应用程序窗口类型(1.Normal window普通窗口,3.Maximized最大化窗口,7.Minimized最小化)
                shortcut.WindowStyle = 1;
                //快捷方式的描述
                shortcut.Description = exeName + "_Ink";
                //设置快捷键(如果有必要的话.)
                //shortcut.Hotkey = "CTRL+ALT+D";
                shortcut.Save();
                return true;
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// 开机自启删除
        /// </summary>
        /// <param name="exeName">程序名称</param>
        /// <returns></returns>
        public static bool StartAutomaticallyDel(string exeName)
        {
            try
            {
                System.IO.File.Delete(Environment.GetFolderPath(Environment.SpecialFolder.Startup) + "\\" + exeName + ".lnk");
                return true;
            }
            catch (Exception) { }
            return false;
        }
        #endregion

        #region Auto Theme

        Color FloatBarForegroundColor = Color.FromRgb(102, 102, 102);

        // SetTheme 每次调用都会往 Application.Resources.MergedDictionaries 追加 4 个字典且从不移除，
        // 每切换一次主题/系统主题变更一次就永久多出 4 份（内含全部图片资源）。
        // 用这个列表跟踪本方法添加的字典，下次调用前先移除，避免无界增长。
        // （ResourceDictionary 没有 Tag 属性，无法像 FrameworkElement 那样打标记。）
        private static readonly List<ResourceDictionary> ThemeDictionaries = new List<ResourceDictionary>();
        // App.xaml 默认就合并了 Light + 三份图片字典。首次 SetTheme("Light") 时它们已在位，
        // 无需移除重建（那会在首帧前重复解析 ~128KB XAML，是启动最明显的卡顿之一）。
        private static bool _themeFirstApply = true;

        private void SetTheme(string theme)
        {
            // 无论从哪个线程进来（SystemEvents 回调在专用线程上），统一切回 UI 线程
            if (!Dispatcher.CheckAccess())
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(() => SetTheme(theme)));
                }
                catch
                {
                }
                return;
            }

            // 首次应用且目标是 Light（App.xaml 的默认主题）：字典已在位，跳过整段移除/重建，
            // 只做后续的控件级着色。切换到 Dark 或后续系统主题变更仍走完整路径。
            if (_themeFirstApply && theme == "Light" && ThemeDictionaries.Count == 0)
            {
                _themeFirstApply = false;
                try { ThemeManager.SetRequestedTheme(window, ElementTheme.Light); } catch { }
                FloatBarForegroundColor = (Color)Application.Current.FindResource("FloatBarForegroundColor");
                SymbolIconSelect.Foreground = new SolidColorBrush(FloatBarForegroundColor);
                SymbolIconDelete.Foreground = new SolidColorBrush(FloatBarForegroundColor);
                return;
            }
            _themeFirstApply = false;

            // 先移除上一次由本方法追加的字典（只移除自己跟踪的，不动 App.xaml 里的）
            foreach (ResourceDictionary stale in ThemeDictionaries)
            {
                try
                {
                    Application.Current.Resources.MergedDictionaries.Remove(stale);
                }
                catch
                {
                }
            }
            ThemeDictionaries.Clear();

            // 本地主题字典与 iNKORE 的 SetRequestedTheme 都是纯托管资源切换，
            // 不依赖 Win10 专有 API：Win7 到 Win11 全平台都执行，亮/暗主题观感一致。
            // 仍保留 try/catch，若某个控件版本不支持也只是跳过控件级主题，不影响画板。
            if (theme == "Light")
            {
                AddThemeDictionary("Resources/Styles/Light.xaml");
                try { ThemeManager.SetRequestedTheme(window, ElementTheme.Light); } catch { }
            }
            else if (theme == "Dark")
            {
                AddThemeDictionary("Resources/Styles/Dark.xaml");
                try { ThemeManager.SetRequestedTheme(window, ElementTheme.Dark); } catch { }
            }
            else
            {
                return;
            }

            // 三份图片字典与主题无关，每次重建一份即可（上面的清理已移除旧副本）
            AddThemeDictionary("Resources/DrawShapeImageDictionary.xaml");
            AddThemeDictionary("Resources/SeewoImageDictionary.xaml");
            AddThemeDictionary("Resources/IconImageDictionary.xaml");

            FloatBarForegroundColor = (Color)Application.Current.FindResource("FloatBarForegroundColor");

            SymbolIconSelect.Foreground = new SolidColorBrush(FloatBarForegroundColor);
            SymbolIconDelete.Foreground = new SolidColorBrush(FloatBarForegroundColor);
        }

        private void AddThemeDictionary(string relativeSource)
        {
            ResourceDictionary dict = new ResourceDictionary()
            {
                Source = new Uri(relativeSource, UriKind.Relative)
            };
            Application.Current.Resources.MergedDictionaries.Add(dict);
            ThemeDictionaries.Add(dict);
        }

        private void SystemEvents_UserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            // SetTheme 内部已切回 UI 线程，这里只负责判断走哪个主题
            switch (Settings.Appearance.Theme)
            {
                case 0:
                    SetTheme("Light");
                    break;
                case 1:
                    SetTheme("Dark");
                    break;
                case 2:
                    if (IsSystemThemeLight()) SetTheme("Light");
                    else SetTheme("Dark");
                    break;
            }
        }

        private bool IsSystemThemeLight()
        {
            // Win7 没有 Themes\Personalize\SystemUsesLightTheme 键。以前缺键恒返回 false，
            // 导致「跟随系统主题」在 Win7 永远切成深色；Win10/11 默认亮色，因此缺键时
            // 按默认亮色处理，两端观感一致。
            // 注意 Registry.CurrentUser 是共享句柄，不能 Dispose；OpenSubKey 的句柄必须释放。
            bool light = true;
            try
            {
                using (RegistryKey themeKey = Registry.CurrentUser.OpenSubKey(@"software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (themeKey != null)
                    {
                        object value = themeKey.GetValue("SystemUsesLightTheme");
                        if (value != null)
                        {
                            try { light = Convert.ToInt32(value) == 1; }
                            catch { light = true; }
                        }
                    }
                }
            }
            catch { }
            return light;
        }
        #endregion

        #endregion Functions

        #region Screenshot

        private void BtnScreenshot_Click(object sender, RoutedEventArgs e)
        {
            bool isHideNotification = false;
            if (sender is bool) isHideNotification = (bool)sender;

            GridNotifications.Visibility = Visibility.Collapsed;

            new Thread(new ThreadStart(() =>
            {
                // 后台线程的任何未捕获异常都会直接终止进程，这里整体兜底
                try
                {
                    Thread.Sleep(20);
                    try
                    {
                        SafeUiInvoke(() =>
                        {
                            if (BtnPPTSlideShowEnd.Visibility == Visibility.Visible)
                                SaveScreenShot(isHideNotification, $"{pptName}/{previousSlideID}_{DateTime.Now:HH-mm-ss}");
                            else
                                SaveScreenShot(isHideNotification);
                        });
                    }
                    catch
                    {
                        // ShowNotification 内部已能自行切回 UI 线程
                        if (!isHideNotification)
                        {
                            ShowNotification("截图保存失败");
                        }
                    }

                    try
                    {
                        SafeUiInvoke(() =>
                        {
                            if (painterCanvas.Visibility != Visibility.Visible || painterCanvas.Strokes.Count == 0 || !Settings.Automation.IsAutoSaveStrokesAtScreenshot) return;
                            SavePainterCanvasStrokes(false);
                        });
                    }
                    catch
                    {
                    }

                    if (isHideNotification)
                    {
                        SafeUiInvoke(() =>
                        {
                            BtnClear_Click(BtnClear, null);
                        });
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile("Screenshot task failed: " + ex, LogHelper.LogType.Error);
                }
            }))
            {
                IsBackground = true
            }.Start();
        }

        // 放映翻页 / 连续截图会写同一个「第N页墨迹.png」，后台串行化避免并发写冲突
        private static readonly object _screenshotSaveLock = new object();

        private void SaveScreenShot(bool isHideNotification, string fileName = null)
        {
            var size = System.Windows.Forms.SystemInformation.PrimaryMonitorSize;
            var rc = new System.Drawing.Rectangle(new System.Drawing.Point(0, 0), new System.Drawing.Size(size.Width, size.Height));

            // 抓屏必须在当前帧同步完成（否则拍到的是已经翻页后的画面）；
            // 最耗时的 PNG 编码 + 写盘移到后台，避免 PPT 翻页 / 点截图时 UI 卡住数百毫秒。
            System.Drawing.Bitmap snapshot;
            using (var bitmap = new System.Drawing.Bitmap(rc.Width, rc.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (System.Drawing.Graphics memoryGrahics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    memoryGrahics.CopyFromScreen(rc.X, rc.Y, 0, 0, rc.Size, System.Drawing.CopyPixelOperation.SourceCopy);
                }
                // 复制一份独立位图交给后台，原 bitmap 在此释放（4K 约 33MB，一次 memcpy）
                snapshot = new System.Drawing.Bitmap(bitmap);
            }

            // 统一保存到桌面“笔迹截图”文件夹
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "笔迹截图");

            string savePath;
            if (!string.IsNullOrWhiteSpace(fileName) && fileName.Contains("/"))
            {
                // 放映模式：fileName 形如 “PPT名称/页码” 或 “PPT名称/页码_时间”
                var parts = fileName.Split(new[] { '/' }, 2);
                var pptName = SanitizeForPath(parts[0]);
                var tail = parts.Length > 1 ? parts[1] : "";
                // 取页码（tail 可能为 “3” 或 “3_17-24-59”）
                var pageToken = tail.IndexOf('_') >= 0 ? tail.Substring(0, tail.IndexOf('_')) : tail;
                var pptDir = Path.Combine(baseDir, pptName);
                if (!Directory.Exists(pptDir)) Directory.CreateDirectory(pptDir);
                savePath = Path.Combine(pptDir, $"第{pageToken}页墨迹.png");
            }
            else
            {
                // 桌面（屏幕标记）：保存到 “笔迹截图/屏幕标记”
                var screenDir = Path.Combine(baseDir, "屏幕标记");
                if (!Directory.Exists(screenDir)) Directory.CreateDirectory(screenDir);
                // 文件名使用全角冒号：半角冒号 “:” 在 Windows 文件名中非法
                var stamp = DateTime.Now.ToString("yyyy-MM-dd HH：mm：ss");
                savePath = Path.Combine(screenDir, stamp + ".png");
            }

            string path = savePath;
            _ = Task.Run(() => {
                try
                {
                    using (snapshot)
                    {
                        lock (_screenshotSaveLock) snapshot.Save(path, ImageFormat.Png);
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile("截图保存失败：" + ex, LogHelper.LogType.Error);
                }
            });

            if (!isHideNotification)
            {
                ShowNotification("截图成功保存至 " + savePath);
            }
        }

        private static string SanitizeForPath(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "未命名";
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray()).TrimEnd('.');
            return cleaned.Length == 0 ? "未命名" : cleaned;
        }

        #endregion

        #region Notification

        // 通知条自动隐藏的计时基准。用 Stopwatch（单调、不回绕）而非 Environment.TickCount：
        // 后者每约 24.9 天回绕一次，int 减法一旦跨越回绕点会得到负数，导致通知条永久不消失。
        private static readonly Stopwatch NotificationClock = Stopwatch.StartNew();
        private static long lastNotificationShowTime;
        private const long notificationShowTime = 2500;

        public static void ShowNewMessage(string notice, bool isShowImmediately = true)
        {
            (Application.Current?.Windows.Cast<Window>().FirstOrDefault(window => window is MainWindow) as MainWindow)?.ShowNotification(notice, isShowImmediately);
        }

        public void ShowNotification(string notice, bool isShowImmediately = true)
        {
            // 该方法会被后台线程调用（截图线程 / PowerPoint COM 回调），统一在这里切回 UI 线程。
            // 原实现没有线程判断，后台线程直接写 GridNotifications 会抛跨线程异常；
            // 而调用点多在 catch 块或裸线程里，异常会逃逸并终止进程。
            if (!Dispatcher.CheckAccess())
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(() => ShowNotification(notice, isShowImmediately)));
                }
                catch
                {
                }
                return;
            }

            Interlocked.Exchange(ref lastNotificationShowTime, NotificationClock.ElapsedMilliseconds);

            GridNotifications.Visibility = Visibility.Visible;
            //GridNotifications.Opacity = 1;
            TextBlockNotice.Text = notice;

            // 用线程池的延时任务替代"每条通知 new Thread + Sleep"：高频截图/COM 回调
            // 下不再堆积数百个休眠线程。时间戳校验保证旧延时不会误隐藏新通知。
            int delayMs = (int)notificationShowTime + 200;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs);
                    if (NotificationClock.ElapsedMilliseconds - Interlocked.Read(ref lastNotificationShowTime) >= notificationShowTime)
                    {
                        SafeUiInvoke(() =>
                        {
                            GridNotifications.Visibility = Visibility.Collapsed;
                        });
                    }
                }
                catch
                {
                    // 应用退出中 / Dispatcher 已关闭，忽略
                }
            });
        }

        private void AppendNotification(string notice)
        {
            TextBlockNotice.Text = TextBlockNotice.Text + Environment.NewLine + notice;
        }

        #endregion

        #region Tools

        private void BtnTools_Click(object sender, RoutedEventArgs e)
        {
            if (StackPanelToolButtons.Visibility == Visibility.Visible)
            {
                StackPanelToolButtons.Visibility = Visibility.Collapsed;
            }
            else
            {
                StackPanelToolButtons.Visibility = Visibility.Visible;
            }
        }

        // 复用同一个点名器窗口实例：首次创建后，后续唤起只做 Show/Hide，
        // 免去每次 new 都重新解析 XAML 与读盘名单的开销，做到“一点就启动”。
        private static RandWindow _randWindow;

        private void BtnRand_Click(object sender, RoutedEventArgs e)
        {
            StackPanelToolButtons.Visibility = Visibility.Collapsed;
            if (_randWindow == null)
            {
                _randWindow = new RandWindow();
            }
            if (_randWindow.IsVisible)
            {
                _randWindow.Activate();
                return;
            }
            _randWindow.Show();
            _randWindow.Activate();
        }

        #endregion Tools

        #region Float Bar

        private void HideSubPanels()
        {
            BorderClearInDelete.Visibility = Visibility.Collapsed;
            PopupTools.IsOpen = false;
        }


        private void BorderPenColorBlack_MouseUp(object sender, MouseButtonEventArgs e)
        {
            BtnColorBlack_Click(BtnColorBlack, null);
            HideSubPanels();
        }

        private void BorderPenColorRed_MouseUp(object sender, MouseButtonEventArgs e)
        {
            BtnColorRed_Click(BtnColorRed, null);
            HideSubPanels();
        }

        private void BorderPenColorGreen_MouseUp(object sender, MouseButtonEventArgs e)
        {
            BtnColorGreen_Click(BtnColorGreen, null);
            HideSubPanels();
        }

        private void BorderPenColorBlue_MouseUp(object sender, MouseButtonEventArgs e)
        {
            BtnColorBlue_Click(BtnColorBlue, null);
            HideSubPanels();
        }

        private void BorderPenColorYellow_MouseUp(object sender, MouseButtonEventArgs e)
        {
            BtnColorYellow_Click(BtnColorYellow, null);
            HideSubPanels();
        }

        private void BorderPenColorWhite_MouseUp(object sender, MouseButtonEventArgs e)
        {
            painterCanvas.DefaultDrawingAttributes.Color = StringToColor("#FFFEFEFE");
            inkColor = 5;
            ColorSwitchCheck();
            HideSubPanels();
        }

        private void SymbolIconUndo_MouseUp(object sender, MouseButtonEventArgs e)
        {
            BtnUndo_Click(BtnUndo, null);
            HideSubPanels();
        }

        private void SymbolIconRedo_MouseUp(object sender, MouseButtonEventArgs e)
        {
            BtnRedo_Click(BtnRedo, null);
            HideSubPanels();
        }

        private async void SymbolIconCursor_Click(object sender, RoutedEventArgs e)
        {
            if (currentMode != 0)
            {
                ImageBlackboard_MouseUp(null, null);
            }
            else
            {
                BtnHidePainterCanvas_Click(BtnHidePainterCanvas, null);

                if (BtnPPTSlideShowEnd.Visibility == Visibility.Visible)
                {
                    if (ViewboxFloatingBar.Margin == new Thickness((SystemParameters.PrimaryScreenWidth - ViewboxFloatingBar.ActualWidth) / 2, SystemParameters.PrimaryScreenHeight - 60, -2000, -200))
                    {
                        await Task.Delay(100);
                        ViewboxFloatingBar.Margin = new Thickness((SystemParameters.PrimaryScreenWidth - ViewboxFloatingBar.ActualWidth) / 2, SystemParameters.PrimaryScreenHeight - 60, -2000, -200);
                    }
                }
            }

            // 画笔 ⇄ 鼠标 切换：切到画笔（白板可写字）时隐藏右下角消息图标，切回鼠标时恢复

            SetColors();
        }

        private void SymbolIconDelete_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (sender != lastBorderMouseDownObject) return;
            if (painterCanvas.GetSelectedStrokes().Count > 0)
            {
                painterCanvas.Strokes.Remove(painterCanvas.GetSelectedStrokes());
                GridPainterCanvasSelectionCover.Visibility = Visibility.Collapsed;
            }
            else if (painterCanvas.Strokes.Count > 0)
            {
                if (Settings.Automation.IsAutoSaveStrokesAtClear && painterCanvas.Strokes.Count > Settings.Automation.MinimumAutomationStrokeNumber)
                {
                    if (BtnPPTSlideShowEnd.Visibility == Visibility.Visible)
                        SaveScreenShot(true, $"{pptName}/{previousSlideID}_{DateTime.Now:HH-mm-ss}");
                    else
                        SaveScreenShot(true);
                }
                BtnClear_Click(BtnClear, null);
            }
            else
            {
                if (currentMode == 0 && BtnPPTSlideShowEnd.Visibility != Visibility.Visible)
                {
                    BtnHidePainterCanvas_Click(BtnHidePainterCanvas, null);
                }
            }
        }

        // 挂在 AppIcon.MouseUp 上，签名必须是 MouseButtonEventArgs：
        // BAML 运行时用 Delegate.CreateDelegate 绑定自定义控件的事件，不支持参数逆变。
        private void SymbolIconSettings_Click(object sender, MouseButtonEventArgs e)
        {
            BtnSettings_Click(this, null);
            HideSubPanels();
        }

        private void SymbolIconSelect_MouseUp(object sender, MouseButtonEventArgs e)
        {
            BtnSelect_Click(BtnSelect, null);

            ImageEraser.Visibility = Visibility.Visible;
            SetColorSelection(-1);   // 选择工具：隐藏全部颜色选中提示

            HideSubPanels();
        }

        private void SymbolIconScreenshot_MouseUp(object sender, MouseButtonEventArgs e)
        {
            BtnScreenshot_Click(this, null);
        }

        Point pointDesktop = new Point(-1, -1); //用于记录上次进入PPT或白板时的坐标
        Point pointPPT = new Point(-1, -1); //用于记录上次在PPT中打开白板时的坐标

        private void ImageBlackboard_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (currentMode == 0)
            {
                //进入黑板
                if (BtnPPTSlideShowEnd.Visibility == Visibility.Collapsed)
                {
                    pointDesktop = new Point(ViewboxFloatingBar.Margin.Left, ViewboxFloatingBar.Margin.Top);
                }
                else
                {
                    pointPPT = new Point(ViewboxFloatingBar.Margin.Left, ViewboxFloatingBar.Margin.Top);
                }
                //ViewboxFloatingBar.Margin = new Thickness(10, SystemParameters.PrimaryScreenHeight - 60, -2000, -200);

                ScheduleUiAction(100, () =>
                {
                    ViewboxFloatingBar.Margin = new Thickness((SystemParameters.PrimaryScreenWidth - ViewboxFloatingBar.ActualWidth) / 2, SystemParameters.PrimaryScreenHeight - 60, -2000, -200);
                });
                if (Settings.Canvas.UsingWhiteboard)
                {
                    BorderPenColorBlack_MouseUp(BorderPenColorBlack, null);
                }
                else
                {
                    BorderPenColorWhite_MouseUp(BorderPenColorWhite, null);
                }
            }
            else
            {
                //关闭黑板
                if (isInMultiTouchMode) BorderMultiTouchMode_MouseUp(null, null);

                if (BtnPPTSlideShowEnd.Visibility == Visibility.Collapsed)
                {
                    if (pointDesktop != new Point(-1, -1))
                    {
                        ViewboxFloatingBar.Margin = new Thickness(pointDesktop.X, pointDesktop.Y, -2000, -200);
                        pointDesktop = new Point(-1, -1);
                    }
                }
                else
                {
                    ScheduleUiAction(100, () =>
                    {
                        ViewboxFloatingBar.Margin = new Thickness((SystemParameters.PrimaryScreenWidth - ViewboxFloatingBar.ActualWidth) / 2, SystemParameters.PrimaryScreenHeight - 60, -2000, -200);
                    });
                }
                BorderPenColorRed_MouseUp(BorderPenColorRed, null);
            }
            BtnSwitch_Click(BtnSwitch, null);

            BtnExit.Foreground = Brushes.White;
            ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark;
            SetColors();
            SetColorByIndex();
            if (currentMode == 0 && painterCanvas.Strokes.Count == 0 && BtnPPTSlideShowEnd.Visibility != Visibility.Visible)
            {
                BtnHidePainterCanvas_Click(BtnHidePainterCanvas, null);
            }
        }

        private void ImageEraser_MouseUp(object sender, MouseButtonEventArgs e)
        {
            BtnErase_Click(BtnErase, e);

            SetColorSelection(-1);   // 橡皮：隐藏全部颜色选中提示

            HideSubPanels();
        }

        private void SymbolIconRand_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (lastBorderMouseDownObject != sender) return;

            PopupTools.IsOpen = false;
            BtnRand_Click(BtnRand, null);
        }

        private void SymbolIconRandOne_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (lastBorderMouseDownObject != sender) return;

            PopupTools.IsOpen = false;
            new RandWindow(true).ShowDialog();
        }

        private void GridInkReplayButton_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (lastBorderMouseDownObject != sender) return;

            PopupTools.IsOpen = false;
            BorderDrawShape.Visibility = Visibility.Collapsed;

            PainterCanvasForInkReplay.Visibility = Visibility.Visible;
            painterCanvas.Visibility = Visibility.Collapsed;
            isStopInkReplay = false;
            PainterCanvasForInkReplay.Strokes.Clear();
            StrokeCollection strokes = painterCanvas.Strokes.Clone();
            if (painterCanvas.GetSelectedStrokes().Count != 0)
            {
                strokes = painterCanvas.GetSelectedStrokes().Clone();
            }
            int k = 1, i = 0;
            new Thread(new ThreadStart(() =>
            {
                // 后台线程必须整体兜底：异常逃逸线程入口会直接终止进程（全局处理器也拦不住）。
                try
                {
                    foreach (Stroke stroke in strokes)
                    {
                        // 复用一条 Stroke 只追加新点（O(1)/点），替代原来“每点 new Stroke(整表 Clone)”的 O(n²)
                        Stroke s = null;
                        int step = stroke.StylusPoints.Count == 629 ? 50 : k; // 圆/椭圆(629点)用更小步进
                        if (step < 1) step = 1; // 防止极端情况下取模/步进为 0
                        foreach (StylusPoint stylusPoint in stroke.StylusPoints)
                        {
                            if (i++ >= step)
                            {
                                i = 0;
                                Thread.Sleep(10);
                                if (isStopInkReplay) return;
                            }
                            var pt = stylusPoint;
                            var owningAttributes = stroke.DrawingAttributes;
                            SafeUiInvoke(() =>
                            {
                                if (s == null)
                                {
                                    s = new Stroke(new StylusPointCollection { pt }) { DrawingAttributes = owningAttributes };
                                    PainterCanvasForInkReplay.Strokes.Add(s);
                                }
                                else
                                {
                                    s.StylusPoints.Add(pt);
                                }
                            });
                        }
                    }
                    Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile("[InkReplay] 回放线程异常：" + ex.Message, LogHelper.LogType.Error);
                }
                finally
                {
                    // 无论正常结束还是异常，都恢复画板可见，避免停留在空回放画布上。
                    SafeUiInvoke(() =>
                    {
                        PainterCanvasForInkReplay.Visibility = Visibility.Collapsed;
                        painterCanvas.Visibility = Visibility.Visible;
                    });
                }
            }))
            {
                IsBackground = true
            }.Start();
        }
        volatile bool isStopInkReplay = false;
        private void PainterCanvasForInkReplay_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                PainterCanvasForInkReplay.Visibility = Visibility.Collapsed;
                painterCanvas.Visibility = Visibility.Visible;
                isStopInkReplay = true;
            }
        }

        private bool _toolsPopupWasOpen;
        private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // 记录按下时弹层是否开着：用于区分“点扳手关闭”还是“点扳手打开”，
            // 避免 StaysOpen=False 在扳手点击时把弹层自动关掉后又被 MouseUp 重新打开。
            _toolsPopupWasOpen = PopupTools.IsOpen;
        }

        private void SymbolIconTools_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_toolsPopupWasOpen)
            {
                _toolsPopupWasOpen = false;
                return; // 本次点击本意是关闭（按下时弹层开着，已被 StaysOpen 自动关闭），不要再重新打开
            }
            // 浮动条在屏幕下半区 → 菜单向上展开；在上半区（或被拖到顶部）→ 向下展开，
            // 避免溢出屏幕外而“点不开”。Popup 自身在最顶层渲染，不会被浮动条 Viewbox 裁切。
            bool barAtBottom = ViewboxFloatingBar.Margin.Top > SystemParameters.PrimaryScreenHeight / 2;
            PopupTools.Placement = barAtBottom ? PlacementMode.Top : PlacementMode.Bottom;
            PopupTools.IsOpen = true;
            RefreshWebInfo();
        }

        // ---- 扳手快捷设置底部：Web 控制台开关 / IP / 端口 ----
        private StackPanel? _webInfoUrl;
        private TextBlock? _webInfoState;
        private bool _webSwitchReady;
        private volatile bool _webSwitchBusy;

        private void WebInfoUrl_Loaded(object sender, RoutedEventArgs e)
        {
            _webInfoUrl = sender as StackPanel;
            RefreshWebInfo();
        }

        private void WebInfoState_Loaded(object sender, RoutedEventArgs e)
        {
            _webInfoState = sender as TextBlock;
            RefreshWebInfo();
        }

        private void WebConsoleSwitch_Loaded(object sender, RoutedEventArgs e)
        {
            // 代码设置 IsOn 会触发 Toggled，用标志挡住，避免刚展开弹窗就误启动 Web。
            _webSwitchReady = false;
            try { WebConsoleSwitch.IsOn = desk.WebStatus.IsRunning; } catch { }
            _webSwitchReady = true;
        }

        /// <summary>把真实运行状态同步回开关；代码赋值期间不触发启停动作。</summary>
        private void SyncWebSwitch(bool running)
        {
            try
            {
                if (WebConsoleSwitch.IsOn == running) return;
                _webSwitchReady = false;
                WebConsoleSwitch.IsOn = running;
            }
            catch { }
            finally { _webSwitchReady = true; }
        }

        private void WebConsoleSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_webSwitchReady) return;
            bool want = WebConsoleSwitch.IsOn;
            var dispatcher = Dispatcher;
            _webSwitchBusy = true;
            if (_webInfoState != null)
                _webInfoState.Text = want ? "· 正在启动…" : "· 正在停止…";

            _ = Task.Run(() =>
            {
                try
                {
                    string msg = desk.WebControl.SetEnabled(want);
                    if (!string.IsNullOrEmpty(msg)) LogHelper.NewLog("[Web] " + msg);
                    // 启停在 desk 的后台线程完成（可能要等 UAC / DXGI 初始化），
                    // 轮询等状态稳定，避免立刻刷新把开关又拨回去。
                    for (int i = 0; i < 30; i++)
                    {
                        System.Threading.Thread.Sleep(500);
                        if (!desk.WebControl.IsAvailable) break;
                        if (desk.WebStatus.IsRunning == want) break;
                    }
                }
                catch { }
                finally
                {
                    try
                    {
                        dispatcher.BeginInvoke(new Action(() =>
                        {
                            _webSwitchBusy = false;
                            RefreshWebInfo();
                        }));
                    }
                    catch { _webSwitchBusy = false; }
                }
            });
        }

        /// <summary>刷新扳手里的 Web 地址：列出本机所有可用 IPv4 + 端口，逐条可点击打开浏览器。</summary>
        private void RefreshWebInfo()
        {
            if (_webSwitchBusy) return;   // 启停进行中，保持「正在启动/停止」提示
            try
            {
                int port = desk.WebStatus.Port;
                bool running = desk.WebStatus.IsRunning;
                bool loopback = desk.WebStatus.IsLoopbackOnly;
                var dispatcher = Dispatcher;   // 先在 UI 线程取好，避免后台线程触碰 DependencyObject
                string state = running
                    ? (loopback ? "· 已启动 · 仅本机可访问" : "· 已启动 · 监听 0.0.0.0")
                    : "· 未启动";
                if (_webInfoState != null)
                    _webInfoState.Text = state + " · 端口 " + port;
                SyncWebSwitch(running);
                if (_webInfoUrl != null)
                {
                    _webInfoUrl.Children.Clear();
                    _webInfoUrl.Children.Add(new TextBlock
                    {
                        Text = "读取中…",
                        FontSize = 12,
                        Foreground = WebInfoForeground()
                    });
                }

                _ = Task.Run(() =>
                {
                    List<string> urls;
                    try { urls = desk.WebStatus.GetAllUrls(); }
                    catch { urls = new List<string> { "http://127.0.0.1:" + port + "/" }; }
                    try
                    {
                        dispatcher.BeginInvoke(new Action(() =>
                        {
                            try { PopulateWebInfoUrls(urls); } catch { }
                        }));
                    }
                    catch { }
                });
            }
            catch { }
        }

        private Brush WebInfoForeground()
        {
            return TryFindResource("FloatBarForeground") as Brush ?? Brushes.White;
        }

        /// <summary>把所有地址渲染成可点击的行：左键在默认浏览器打开，快速验证 Web 是否真的启动成功。</summary>
        private void PopulateWebInfoUrls(List<string> urls)
        {
            var panel = _webInfoUrl;
            if (panel == null) return;
            panel.Children.Clear();
            Brush fg = WebInfoForeground();
            var font = new FontFamily("Consolas, Microsoft YaHei");

            if (urls == null || urls.Count == 0)
                urls = new List<string> { "http://127.0.0.1:" + desk.WebStatus.Port + "/" };

            foreach (string url in urls)
            {
                string captured = url;
                var row = new TextBlock
                {
                    Text = url,
                    FontSize = 12,
                    FontFamily = font,
                    Foreground = fg,
                    Cursor = Cursors.Hand,
                    TextDecorations = TextDecorations.Underline,
                    ToolTip = "点击用浏览器打开：" + captured,
                    Margin = new Thickness(0, 0, 0, 2),
                    TextWrapping = TextWrapping.Wrap
                };
                row.MouseUp += (s, e) => OpenWebBrowserUrl(captured);
                panel.Children.Add(row);
            }

            // 保留复制能力：多个地址一次性复制，方便粘到手机浏览器。
            var copy = new TextBlock
            {
                Text = "复制全部地址",
                FontSize = 10,
                Foreground = fg,
                Opacity = 0.7,
                Cursor = Cursors.Hand,
                TextDecorations = TextDecorations.Underline,
                ToolTip = "复制所有地址到剪贴板",
                Margin = new Thickness(0, 2, 0, 0)
            };
            copy.MouseUp += (s, e) =>
            {
                try { Clipboard.SetText(string.Join(Environment.NewLine, urls)); } catch { }
                copy.ToolTip = "已复制全部地址";
            };
            panel.Children.Add(copy);
        }

        /// <summary>用系统默认浏览器打开 Web 控制台地址。</summary>
        private static void OpenWebBrowserUrl(string url)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url)) return;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile("[Web] 打开浏览器失败：" + ex.Message, LogHelper.LogType.Error);
            }
        }

        /// <summary>手动重新申请防火墙放行（后台提权，绝不阻塞 UI）。</summary>
        private void WebInfoFirewall_MouseUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                int port = desk.WebStatus.Port;
                var link = sender as TextBlock;
                var dispatcher = Dispatcher;
                if (link != null) link.Text = "正在申请放行，请在弹出的 UAC 窗口中确认…";

                _ = Task.Run(() =>
                {
                    bool ok = false;
                    try
                    {
                        ok = desk.FirewallSetup.EnsureNetworkAccess(port, 0, 0, 0, true,
                            s => LogHelper.NewLog("[Web] " + s));
                    }
                    catch { }
                    try
                    {
                        dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                if (link != null)
                                    link.Text = ok
                                        ? "已放行；请停止并重新启动 Web 远控后局域网生效"
                                        : "未获得管理员授权，点击重试";
                                RefreshWebInfo();
                            }
                            catch { }
                        }));
                    }
                    catch { }
                });
            }
            catch { }
        }


        #region Drag

        bool isDragDropInEffect = false;
        Point pos = new Point();
        Point downPos = new Point();

        void Element_MouseMove(object sender, MouseEventArgs e)
        {
            if (isDragDropInEffect)
            {
                FrameworkElement currEle = sender as FrameworkElement;
                double xPos = e.GetPosition(null).X - pos.X + currEle.Margin.Left;
                double yPos = e.GetPosition(null).Y - pos.Y + currEle.Margin.Top;
                currEle.Margin = new Thickness(xPos, yPos, 0, 0);
                pos = e.GetPosition(null);
            }
        }

        void Element_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {

            FrameworkElement fEle = sender as FrameworkElement;
            isDragDropInEffect = true;
            pos = e.GetPosition(null);
            fEle.CaptureMouse();
            fEle.Cursor = Cursors.Hand;
        }

        void Element_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (isDragDropInEffect)
            {
                FrameworkElement ele = sender as FrameworkElement;
                isDragDropInEffect = false;
                ele.ReleaseMouseCapture();
            }
        }


        // 拖拽期间的累计位移（不写 Margin，避免每个 MouseMove 触发整条浮动栏重新布局）
        private double _dragStartX, _dragStartY, _dragOffsetX, _dragOffsetY;

        void SymbolIconEmoji_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isDragDropInEffect) return;
            Point p = e.GetPosition(null);
            _dragOffsetX += p.X - pos.X;
            _dragOffsetY += p.Y - pos.Y;
            pos = p;
            // 纯合成层位移：只改一个 DP，零 Measure/Arrange
            ViewboxFloatingBarDragTransform.X = _dragOffsetX;
            ViewboxFloatingBarDragTransform.Y = _dragOffsetY;
        }

        void SymbolIconEmoji_MouseDown(object sender, MouseButtonEventArgs e)
        {
            isDragDropInEffect = true;
            pos = e.GetPosition(null);
            downPos = e.GetPosition(null);
            _dragStartX = ViewboxFloatingBar.Margin.Left;
            _dragStartY = ViewboxFloatingBar.Margin.Top;
            _dragOffsetX = 0;
            _dragOffsetY = 0;
            ViewboxFloatingBarDragTransform.X = 0;
            ViewboxFloatingBarDragTransform.Y = 0;
            GridForFloatingBarDraging.Visibility = Visibility.Visible;

            SymbolIconEmoji.Glyph = "Emoji";
        }

        void SymbolIconEmoji_MouseUp(object sender, MouseButtonEventArgs e)
        {
            isDragDropInEffect = false;

            if (e is null || (downPos.X == e.GetPosition(null).X && downPos.Y == e.GetPosition(null).Y))
            {
                SetBorderFloatingBarMainControlsVisibility(!borderFloatingBarMainControlsVisibility);
            }

            // 松手时把累计位移一次性提交到 Margin（只触发一次布局），随后复位变换
            if (Math.Abs(_dragOffsetX) > 0.001 || Math.Abs(_dragOffsetY) > 0.001)
            {
                ViewboxFloatingBar.Margin = new Thickness(
                    _dragStartX + _dragOffsetX, _dragStartY + _dragOffsetY, -2000, -200);
                ViewboxFloatingBarDragTransform.X = 0;
                ViewboxFloatingBarDragTransform.Y = 0;
                _dragOffsetX = 0;
                _dragOffsetY = 0;
            }

            GridForFloatingBarDraging.Visibility = Visibility.Collapsed;
            SymbolIconEmoji.Glyph = "Emoji2";
        }

        bool borderFloatingBarMainControlsVisibility = true;
        void SetBorderFloatingBarMainControlsVisibility(bool isVisible, bool isAnimated = true)
        {
            borderFloatingBarMainControlsVisibility = isVisible;
            // 缩短显隐动画时长，让浮动工具条“一点就出现”，减少唤起时的等待感
            if (!isVisible)
            {
                BorderFloatingBarMainControls.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(isAnimated ? 40 : 0))
                {
                    EasingFunction = new PowerEase() { Power = 4, EasingMode = EasingMode.EaseOut },
                });
                BorderFloatingBarMainControls.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(isAnimated ? 40 : 0)));
            }
            else
            {
                BorderFloatingBarMainControls.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(isAnimated ? 80 : 0))
                {
                    EasingFunction = new PowerEase() { Power = 4, EasingMode = EasingMode.EaseOut },
                });
                BorderFloatingBarMainControls.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(isAnimated ? 80 : 0)));
            }
        }

        #endregion


        private void GridPPTControlPrevious_MouseUp(object sender, MouseButtonEventArgs e)
        {
            BtnPPTSlidesUp_Click(BtnPPTSlidesUp, null);
        }

        private void GridPPTControlNext_MouseUp(object sender, MouseButtonEventArgs e)
        {
            BtnPPTSlidesDown_Click(BtnPPTSlidesDown, null);
        }

        private void ImagePPTControlEnd_MouseUp(object sender, MouseButtonEventArgs e)
        {
            BtnPPTSlideShowEnd_Click(BtnPPTSlideShowEnd, null);
        }

        #endregion

        #region Save & Open

        private void SavePainterCanvasStrokes(bool newNotice = true)
        {
            // 文件名精度到毫秒：原先用 DateTime.Now.ToString("u")（秒级），
            // 1 秒内多次保存会互相覆盖同一个文件
            string fileName = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss-fff") + ".icstk";
            string saveDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + @"\painter Strokes\User Saved";
            string savePath = Path.Combine(saveDir, fileName);
            try
            {
                if (!Directory.Exists(saveDir))
                {
                    Directory.CreateDirectory(saveDir);
                }

                // FileStream 必须释放：默认 FileShare.None，不关闭的话
                // 保存后的 .icstk 会被进程独占锁定，直到 GC 终结器回收
                using (FileStream fs = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    painterCanvas.Strokes.Save(fs);
                }

                if (newNotice)
                {
                    ShowNotification("墨迹成功保存至 " + savePath);
                }
                else
                {
                    AppendNotification("墨迹成功保存至 " + savePath);
                }
            }
            catch (Exception ex)
            {
                ShowNotification($"墨迹保存失败：{ex.Message}");
            }
        }

        private void SymbolIconPin_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _lockSmith = !_lockSmith;
            if (_lockSmith) LockSmithSymbol.Glyph = "UnPin";
            else LockSmithSymbol.Glyph = "Pin";
        }

        #endregion

        #region Multi-finger Inking


        #endregion

        #region Test for pen
    // A StylusPlugin that renders ink with a linear gradient brush effect.
    class CustomDynamicRenderer : DynamicRenderer
    {
        [ThreadStatic]
        static private Brush brush = null;

        [ThreadStatic]
        static private Pen pen = null;

        private Point prevPoint;

        protected override void OnStylusDown(RawStylusInput rawStylusInput)
        {
            // Allocate memory to store the previous point to draw from.
            prevPoint = new Point(double.NegativeInfinity, double.NegativeInfinity);
            base.OnStylusDown(rawStylusInput);
        }
        //protected override void OnDraw(System.Windows.Media.DrawingContext drawingContext, System.Windows.Input.StylusPointCollection stylusPoints, System.Windows.Media.Geometry geometry, System.Windows.Media.Brush fillBrush)
        //{


        //    ImageSource img = new BitmapImage(new Uri("pack://application:,,,/Resources/maobi.png"));

        //    //前一个点的绘制。
        //    Point prevPoint = new Point(double.NegativeInfinity,
        //                                double.NegativeInfinity);


        //    var w = Global.StrokeWidth + 15;    //输出时笔刷的实际大小


        //    Point pt = new Point(0, 0);
        //    Vector v = new Vector();            //前一个点与当前点的距离
        //    var subtractY = 0d;                 //当前点处前一点的Y偏移
        //    var subtractX = 0d;                 //当前点处前一点的X偏移
        //    var pointWidth = Global.StrokeWidth;
        //    double x = 0, y = 0;
        //    for (int i = 0; i < stylusPoints.Count; i++)
        //    {
        //        pt = (Point)stylusPoints[i];
        //        v = Point.Subtract(prevPoint, pt);

        //        Debug.WriteLine("X " + pt.X + "\t" + pt.Y);

        //        subtractY = (pt.Y - prevPoint.Y) / v.Length;    //设置stylusPoints两个点之间需要填充的XY偏移
        //        subtractX = (pt.X - prevPoint.X) / v.Length;

        //        if (w - v.Length < Global.StrokeWidth)          //控制笔刷大小
        //        {
        //            pointWidth = Global.StrokeWidth;
        //        }
        //        else
        //        {
        //            pointWidth = w - v.Length;                  //在两个点距离越大的时候，笔刷所展示的大小越小
        //        }


        //        for (double j = 0; j < v.Length; j = j + 1d)    //填充stylusPoints两个点之间
        //        {
        //            x = 0; y = 0;

        //            if (prevPoint.X == double.NegativeInfinity || prevPoint.Y == double.NegativeInfinity || double.PositiveInfinity == prevPoint.X || double.PositiveInfinity == prevPoint.Y)
        //            {
        //                y = pt.Y;
        //                x = pt.X;
        //            }
        //            else
        //            {
        //                y = prevPoint.Y + subtractY;
        //                x = prevPoint.X + subtractX;
        //            }

        //            drawingContext.DrawImage(img, new Rect(x - pointWidth / 2, y - pointWidth / 2, pointWidth, pointWidth));    //在当前点画笔刷图片
        //            prevPoint = new Point(x, y);


        //            if (double.IsNegativeInfinity(v.Length) || double.IsPositiveInfinity(v.Length))
        //            { break; }
        //        }
        //    }
        //    stylusPoints = null;
        //}
        protected override void OnDraw(DrawingContext drawingContext,
                                       StylusPointCollection stylusPoints,
                                       Geometry geometry, Brush fillBrush)
        {
            // Create a new Brush, if necessary.
            //brush ??= new LinearGradientBrush(Colors.Red, Colors.Blue, 20d);

            // Create a new Pen, if necessary.
            //pen ??= new Pen(brush, 2d);

            // Draw linear gradient ellipses between 
            // all the StylusPoints that have come in.
            for (int i = 0; i < stylusPoints.Count; i++)
            {
                Point pt = (Point)stylusPoints[i];
                Vector v = Point.Subtract(prevPoint, pt);

                // Only draw if we are at least 4 units away 
                // from the end of the last ellipse. Otherwise, 
                // we're just redrawing and wasting cycles.
                if (v.Length > 4)
                {
                    // Set the thickness of the stroke based 
                    // on how hard the user pressed.
                    double radius = stylusPoints[i].PressureFactor * 10d;
                    drawingContext.DrawEllipse(brush, pen, pt, radius, radius);
                    prevPoint = pt;
                }
            }
        }
    }
    public class Global
    {
        public static double StrokeWidth = 2.5;
    }
    public class CustomRenderingPainterCanvas : InkCanvas
    {
        CustomDynamicRenderer customRenderer = new CustomDynamicRenderer();

        public CustomRenderingPainterCanvas() : base()
        {
            // Use the custom dynamic renderer on the
            // custom InkCanvas.
            this.DynamicRenderer = customRenderer;
        }

        protected override void OnStrokeCollected(InkCanvasStrokeCollectedEventArgs e)
        {
            // Remove the original stroke and add a custom stroke.
            this.Strokes.Remove(e.Stroke);
            CustomStroke customStroke = new CustomStroke(e.Stroke.StylusPoints);
            this.Strokes.Add(customStroke);

            // Pass the custom stroke to base class' OnStrokeCollected method.
            InkCanvasStrokeCollectedEventArgs args =
                new InkCanvasStrokeCollectedEventArgs(customStroke);
            base.OnStrokeCollected(args);
        }
    }// A class for rendering custom strokes
    class CustomStroke : Stroke
    {
        Brush brush;
        Pen pen;

        public CustomStroke(StylusPointCollection stylusPoints)
            : base(stylusPoints)
        {
            // Create the Brush and Pen used for drawing.
            brush = new LinearGradientBrush(Colors.Red, Colors.Blue, 20d);
            pen = new Pen(brush, 2d);
        }
        //protected override void DrawCore(DrawingContext drawingContext, DrawingAttributes drawingAttributes)
        //{


        //            ImageSource img = new BitmapImage(new Uri("pack://application:,,,/Resources/maobi.png"));

        //    //前一个点的绘制。
        //    Point prevPoint = new Point(double.NegativeInfinity,
        //                                double.NegativeInfinity);


        //    var w = Global.StrokeWidth + 15;    //输出时笔刷的实际大小


        //    Point pt = new Point(0, 0);
        //    Vector v = new Vector();            //前一个点与当前点的距离
        //    var subtractY = 0d;                 //当前点处前一点的Y偏移
        //    var subtractX = 0d;                 //当前点处前一点的X偏移
        //    var pointWidth = Global.StrokeWidth;
        //    double x = 0, y = 0;
        //    for (int i = 0; i < stylusPoints.Count; i++)
        //    {
        //        pt = (Point)stylusPoints[i];
        //        v = Point.Subtract(prevPoint, pt);

        //        Debug.WriteLine("X " + pt.X + "\t" + pt.Y);

        //        subtractY = (pt.Y - prevPoint.Y) / v.Length;    //设置stylusPoints两个点之间需要填充的XY偏移
        //        subtractX = (pt.X - prevPoint.X) / v.Length;

        //        if (w - v.Length < Global.StrokeWidth)          //控制笔刷大小
        //        {
        //            pointWidth = Global.StrokeWidth;
        //        }
        //        else
        //        {
        //            pointWidth = w - v.Length;                  //在两个点距离越大的时候，笔刷所展示的大小越小
        //        }


        //        for (double j = 0; j < v.Length; j = j + 1d)    //填充stylusPoints两个点之间
        //        {
        //            x = 0; y = 0;

        //            if (prevPoint.X == double.NegativeInfinity || prevPoint.Y == double.NegativeInfinity || double.PositiveInfinity == prevPoint.X || double.PositiveInfinity == prevPoint.Y)
        //            {
        //                y = pt.Y;
        //                x = pt.X;
        //            }
        //            else
        //            {
        //                y = prevPoint.Y + subtractY;
        //                x = prevPoint.X + subtractX;
        //            }

        //            drawingContext.DrawImage(img, new Rect(x - pointWidth / 2, y - pointWidth / 2, pointWidth, pointWidth));    //在当前点画笔刷图片
        //            prevPoint = new Point(x, y);


        //            if (double.IsNegativeInfinity(v.Length) || double.IsPositiveInfinity(v.Length))
        //            { break; }
        //        }
        //    }
        //    stylusPoints = null;
        //}
        protected override void DrawCore(DrawingContext drawingContext,
                                         DrawingAttributes drawingAttributes)
        {
            // Allocate memory to store the previous point to draw from.
            Point prevPoint = new Point(double.NegativeInfinity,
                                        double.NegativeInfinity);

            // Draw linear gradient ellipses between
            // all the StylusPoints in the Stroke.
            for (int i = 0; i < this.StylusPoints.Count; i++)
            {
                Point pt = (Point)this.StylusPoints[i];
                Vector v = Point.Subtract(prevPoint, pt);

                // Only draw if we are at least 4 units away
                // from the end of the last ellipse. Otherwise,
                // we're just redrawing and wasting cycles.
                if (v.Length > 4)
                {
                    // Set the thickness of the stroke
                    // based on how hard the user pressed.
                    double radius = this.StylusPoints[i].PressureFactor * 10d;
                    drawingContext.DrawEllipse(brush, pen, pt, radius, radius);
                    prevPoint = pt;
                }
            }
        }
    }
    #endregion
}
}
