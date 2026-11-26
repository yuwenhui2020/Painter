using Painter.Helpers;
using iNKORE.UI.WPF.Modern.Controls;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;

namespace Painter
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private CanvasActivator _activator;

        /// <summary>控制面板窗体（托盘 / 被控端 / Web 远控 / PPT 控制），与画板同进程。</summary>
        // Desk STA 线程创建、WPF UI 线程 OnExit 读取，需保证跨线程可见
        private volatile desk.Form1 _deskForm;

        /// <summary>标记是否为用户主动发起的关闭（点击退出按钮、托盘菜单退出、欢迎向导重启等）。</summary>
        private static bool _isUserInitiatedShutdown = false;

        public static string[] StartArgs = null;
        // 不能用 GetEnvironmentVariable("APPDATA") + "\\painter\\"：服务账户/精简 Win7 下该变量
        // 可能缺失，结果变成当前驱动器根路径 "\painter\"（非空，导致后续空串检查失效、日志静默丢失）。
        public static string RootPath = BuildRootPath();

        private static string BuildRootPath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
                appData = Environment.GetEnvironmentVariable("APPDATA");
            if (string.IsNullOrWhiteSpace(appData))
                return AppDomain.CurrentDomain.SetupInformation.ApplicationBase;
            return Path.Combine(appData, "painter") + Path.DirectorySeparatorChar;
        }

        /// <summary>标记用户主动发起关闭，供 OnExit 判断是否为预期关闭。</summary>
        internal static void MarkUserInitiatedShutdown() => _isUserInitiatedShutdown = true;

        public App()
        {
            this.Startup += new StartupEventHandler(App_Startup);
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;

            // 后台线程（new Thread/Task）里逃逸的异常不会经过 DispatcherUnhandledException。
            // .NET 4.x 中 AppDomain.UnhandledException 无法阻止进程终止，但至少把现场写进
            // crash.log，让闪退有据可查；Task 的异常也标记已观察到，避免终结期二次抛出。
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    var ex = e.ExceptionObject as Exception;
                    desk.CrashDiagnostics.AppendCrashLine(
                        "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] 后台线程致命异常（进程将退出）: " + ex + Environment.NewLine);
                    LogHelper.NewLog(ex ?? new Exception("非 Exception 类型的致命错误"));
                }
                catch { }
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                try
                {
                    e.SetObserved();
                    desk.CrashDiagnostics.AppendCrashLine(
                        "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] 未观察的 Task 异常: " + e.Exception + Environment.NewLine);
                }
                catch { }
            };

            // 画板关闭只缩回后台（托盘/远控继续运行），仅显式退出时才真正结束进程
            this.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        private string _lastCrashSignature;
        private DateTime _lastCrashNotifyUtc;

        /// <summary>
        /// 画板可见性快照，供 Desk STA 线程的托盘菜单读取。
        /// volatile 保证跨线程可见，且读取时完全不做 Dispatcher.Invoke（避免双向同步等待死锁）。
        /// </summary>
        internal volatile bool CanvasVisible = true;

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            // 异常处理函数自身绝不能再抛异常：启动期 MainWindow 尚未创建、或日志写盘失败时，
            // 二次抛出会让整个进程直接退出。先置 Handled，再把整段处理兜住。
            e.Handled = true;
            try
            {
                Exception ex = e.Exception;
                LogHelper.NewLog(ex);
                try { desk.CrashDiagnostics.AppendCrashLine("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] UI 未处理异常（已被 Dispatcher 接管，未崩溃）: " + ex + Environment.NewLine); } catch { }

                // 同一处异常可能被高频输入事件反复触发（例如每次鼠标移动都失败）。
                // 重复的只记日志、不再连续弹提示，否则提示会盖住画板、看起来像“频繁报错”。
                string signature = ex.GetType().FullName + "|" + ex.StackTrace;
                DateTime now = DateTime.UtcNow;
                if (signature == _lastCrashSignature && (now - _lastCrashNotifyUtc).TotalSeconds < 30) return;
                _lastCrashSignature = signature;
                _lastCrashNotifyUtc = now;

                try
                {
                    Painter.MainWindow.ShowNewMessage(
                        "抱歉，出现未预期的异常，可能导致 " + desk.AppIdentity.ProductName + " 画板运行不稳定。\n建议保存墨迹后重启应用。", true);
                }
                catch { }
            }
            catch { }
        }

        /// <summary>
        /// 旧版（Ink Canvas / desk）数据目录一次性改名。仅在新目录不存在时整目录移动，
        /// 已存在则保持原样，绝不合并或覆盖用户数据。
        /// </summary>
        private static void MigrateLegacyDataFolders()
        {
            MoveFolderIfMissing(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Ink Canvas Strokes"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "painter Strokes"));
            MoveFolderIfMissing(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Ink Canvas Screenshots"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "painter Screenshots"));

            // 配置目录：%APPDATA%\InkCanvas（改名前的 AppIdentity.DataFolder）与更早的
            // %APPDATA%\desk 都要搬到新的 %APPDATA%\Painter，否则老用户的设置会全部丢失。
            // 只在目标不存在时整体移动，先到先得，绝不合并覆盖。
            string appData = Environment.GetEnvironmentVariable("APPDATA");
            if (!string.IsNullOrEmpty(appData))
            {
                string current = Path.Combine(appData, desk.AppIdentity.DataFolder);
                MoveFolderIfMissing(Path.Combine(appData, desk.AppIdentity.LegacyDataFolderInkCanvas), current);
                MoveFolderIfMissing(Path.Combine(appData, desk.AppIdentity.LegacyDataFolder), current);
            }
        }

        private static void MoveFolderIfMissing(string legacyPath, string currentPath)
        {
            try
            {
                if (!Directory.Exists(legacyPath) || Directory.Exists(currentPath)) return;
                Directory.Move(legacyPath, currentPath);
                LogHelper.NewLog("已迁移旧数据目录: " + legacyPath + " -> " + currentPath);
            }
            catch (Exception ex)
            {
                LogHelper.NewLog("迁移旧数据目录失败: " + legacyPath + " -> " + currentPath + "：" + ex.Message);
            }
        }

        void App_Startup(object sender, StartupEventArgs e)
        {
            if (!StoreHelper.IsStoreApp) RootPath = AppDomain.CurrentDomain.SetupInformation.ApplicationBase;

            LogHelper.NewLog(string.Format("painter Starting (Version: {0})", Assembly.GetExecutingAssembly().GetName().Version.ToString()));

            StartArgs = e.Args ?? new string[0];
            // UI 无响应（AppHang）不产生任何托管异常，原来的 crash.log 钩子永远一片空白。
            // 装上 UI 心跳看门狗后，UI 线程长时间不处理消息会把现场写进 crash.log。
            try { desk.CrashDiagnostics.InstallUiWatchdog(Dispatcher); } catch { }
            desk.StartupTiming.Mark("App_Startup 进入（App.xaml 已加载完 XAML）", true);

            // ---------- 单实例互唤：已有实例在跑，就把它的画板唤出来，然后本进程退出 ----------
            _activator = new CanvasActivator();
            desk.StartupTiming.Mark("CanvasActivator 完成 IsFirstInstance=" + _activator.IsFirstInstance, true);
            if (!_activator.IsFirstInstance && !StartArgs.Contains("-m")) //-m multiple
            {
                if (CanvasActivator.TryNotifyExistingInstance())
                {
                    LogHelper.NewLog("Detected existing instance, activated its canvas");
                }
                else
                {
                    LogHelper.NewLog("Detected existing instance");
                    try
                    {
                        MessageBox.Show("已有一个程序实例正在运行。\n\n若长时间无响应，请从任务管理器结束 " + desk.AppIdentity.ProductName + " 进程后重试。", desk.AppIdentity.ProductName);
                    }
                    catch { }
                }
                LogHelper.NewLog("painter automatically closed");
                MarkUserInitiatedShutdown();
                Shutdown();
                return;
            }

            // 管道监听线程不能在回调里同步等 UI 线程：退出时 OnExit 会 Join 该监听线程，
            // 同步 Invoke 会形成「UI 等监听 / 监听等 UI」的循环等待。
            _activator.StartListening(() => Dispatcher.BeginInvoke(new Action(ShowCanvas)));
            desk.StartupTiming.Mark("管道监听线程已启动", true);

            // ---------- 画板（关键路径：优先显示窗口，非关键初始化延后） ----------
            MainWindow = new MainWindow();
            // 使用 sender 而非捕获 MainWindow，避免关闭时 MainWindow 为 null 导致 NullReferenceException
            MainWindow.IsVisibleChanged += (s, ev) => { if (s is Window w) CanvasVisible = w.IsVisible; };
            desk.StartupTiming.Mark("new MainWindow() 完成", true);
            MainWindow.Show();
            CanvasVisible = MainWindow.IsVisible;
            if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
            MainWindow.Activate();
            MainWindow.Focus();
            desk.StartupTiming.Mark("MainWindow.Show() 完成（窗口已显示）", true);

            // ---------- 非关键初始化：异步延后执行，不阻塞 UI 线程 ----------
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    // 旧版数据目录迁移（IO 密集，放后台）
                    await Task.Run(() => MigrateLegacyDataFolders()).ConfigureAwait(false);
                    desk.StartupTiming.Mark("数据迁移完成", true);

                    // 托盘/远控模块在独立 STA 线程启动（已优化：DXGI/编码器延迟初始化）
                    // 必须放在同一个 try 内：这是 async void 委托，try 之外的任何异常都不会被捕获，
                    // 会变成 Dispatcher 未处理异常，甚至掩盖真正的启动失败原因。
                    StartDesk();
                    desk.StartupTiming.Mark("DeskModule 启动完成", true);
                }
                catch (Exception ex)
                {
                    try { desk.CrashDiagnostics.AppendCrashLine("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] 启动期后台初始化异常: " + ex + Environment.NewLine); } catch { }
                    LogHelper.NewLog("启动期后台初始化失败: " + ex.Message);
                }
            }), DispatcherPriority.ApplicationIdle);
        }

        /// <summary>
        /// 在当前进程内启动控制面板。窗体默认隐藏（见其 SetVisibleCore），只留任务栏托盘；
        /// 被控端与 Web 远控由其内部定时器自动拉起。
        /// 放在独立的 STA 线程并运行自己的 WinForms 消息循环，使其 300ms 定时器与
        /// AutoStartup（DXGI 抓屏 + 编码器初始化、Web、FTP，约 700ms+）全部在后台线程执行，
        /// 不占用 WPF UI 线程 —— 启动后主界面不再因远控模块初始化而卡顿。
        /// </summary>
        private void StartDesk()
        {
            try
            {
                Thread deskThread = new Thread(() =>
                {
                    try
                    {
                        System.Windows.Forms.Application.EnableVisualStyles();
                        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                        var form = new desk.Form1
                        {
                            Host = new HostBridge(this)
                        };
                        _deskForm = form;
                        System.Windows.Forms.Application.Run(form);
                    }
                    catch (Exception ex)
                    {
                        LogHelper.NewLog("远程桌面 / Web 远控模块线程异常: " + ex);
                    }
                });
                deskThread.SetApartmentState(ApartmentState.STA);
                deskThread.IsBackground = true;
                deskThread.Name = "DeskModule";
                deskThread.Start();
                LogHelper.NewLog("远程桌面 / Web 远控模块已在独立线程启动（托盘 + 远程控制）");
            }
            catch (Exception ex)
            {
                // 控制面板启动失败不应影响画板正常使用
                LogHelper.NewLog("远程桌面 / Web 远控模块启动失败: " + ex);
                try
                {
                    MessageBox.Show("远程桌面 / Web 远控模块启动失败，画板可继续正常使用。\n\n" + ex.Message,
                        desk.AppIdentity.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch
                {
                }
            }
        }

        #region 画板显隐（供托盘菜单与单实例互唤调用）

        /// <summary>显示并激活画板（画板已隐藏时对应托盘的”开启画板”）。</summary>
        public void ShowCanvas()
        {
            if (MainWindow == null) return;
            if (!MainWindow.IsVisible) MainWindow.Show();
            if (MainWindow.WindowState == WindowState.Minimized)
                MainWindow.WindowState = WindowState.Maximized;
            MainWindow.ShowInTaskbar = false;
            // 先置顶再取消，绕过 Windows 前台锁：被单实例互唤（重复启动 exe）唤起时，
            // 单纯 Activate() 可能被系统拒绝，导致窗口其实已显示却藏在别的窗口后面，
            // 看起来像”双击没反应 / 起不来”。
            MainWindow.Topmost = true;
            MainWindow.Topmost = false;
            MainWindow.BringIntoView();
            MainWindow.Activate();
            MainWindow.Focus();
        }

        /// <summary>隐藏画板，托盘 / 被控端继续在后台运行。</summary>
        public void HideCanvas()
        {
            if (MainWindow != null && MainWindow.IsVisible) MainWindow.Hide();
        }


        #endregion

        /// <summary>
        /// 进程退出时的确定性清理。缺了这里：控制面板的 NotifyIcon（托盘图标）、
        /// WinForms 窗体与内部网络监听不会释放；CanvasActivator 持有的
        /// Mutex / CancellationTokenSource 句柄也会泄漏。
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            // 记录退出调用堆栈，定位谁触发了 Shutdown（先记录，后刷新）
            try
            {
                var stack = new System.Diagnostics.StackTrace(true);
                LogHelper.NewLog($"[ShutdownTrace] Application.OnExit called from:\n{stack}");
            }
            catch { }

            // 记录是否为用户主动发起的关闭
            LogHelper.NewLog($"[ShutdownTrace] UserInitiated={_isUserInitiatedShutdown}");

            // 先停单实例监听：否则控制面板窗体被拆掉后监听线程还可能回调 ShowCanvas
            try
            {
                _activator?.Dispose();
                _activator = null;
            }
            catch
            {
            }
            // Form1 由独立的 Desk STA 线程创建并运行消息循环。这里（WPF UI 线程）直接
            // 调用 form.Close()/Dispose() 会触发 WinForms 跨线程同步 marshal；若 Desk 线程
            // 此刻正等 UI（托盘退出 / 菜单刷新），两边互等即永久挂起 —— 这正是事件日志里
            // AppHangB1「Top level window is idle」的成因。改为把清理整体异步投递回窗体
            // 所属线程，UI 线程绝不等待 Desk 线程。
            var form = _deskForm;
            if (form != null)
            {
                try { form.RequestShutdownCleanup(); } catch { }
                _deskForm = null;
            }
            // 配对启动时的 timeBeginPeriod(1)，避免退出后系统仍维持 1ms 定时器精度
            try { desk.DeskBootstrap.RestoreTimerResolution(); } catch { }
            LogHelper.NewLog("painter exiting");

            // 最后刷新日志队列，确保上述追踪条目写入磁盘
            try { LogHelper.Flush(); } catch { }

            base.OnExit(e);
        }

        private void ScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            try
            {
                if (System.Windows.Forms.SystemInformation.MouseWheelScrollLines == -1)
                    e.Handled = false;
                else
                    try
                    {
                        ScrollViewerEx SenderScrollViewer = (ScrollViewerEx)sender;
                        SenderScrollViewer.ScrollToVerticalOffset(SenderScrollViewer.VerticalOffset - e.Delta * 10 * System.Windows.Forms.SystemInformation.MouseWheelScrollLines / (double)120);
                        e.Handled = true;
                    }
                    catch
                    {
                    }
            }
            catch
            {
            }
        }
    }
}
