using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using ServiceProcess = System.ServiceProcess;

using System.Collections.Generic;
using System.Drawing;
using System.Net.Http;
using System.Threading;

namespace desk;

/// <summary>
/// desk 的启动宿主。desk 已并入 painter 单一 exe，正常启动由 WPF 画板承载
/// 托盘 / 远控；但 <c>-service</c> / <c>-host</c> / <c>-web</c> 三种模式
/// 必须在 WPF 初始化<b>之前</b>接管并直接结束进程：
/// <list type="bullet">
/// <item><c>-service</c> 以 LocalSystem 在 session 0 运行，无交互桌面，WPF 无法初始化</item>
/// <item><c>-host</c> / <c>-web</c> 需要独占 DXGI 桌面复制并阻塞主线程</item>
/// </list>
/// 这三种模式由 UAC 支持（<see cref="UacSupport"/>）与 <see cref="SessionLauncher"/>
/// 以 <c>NetFx.ProcessPath</c>（即合并后的 Painter.exe）拉起。
/// </summary>
public static class DeskBootstrap
{
	/// <summary>把系统定时器精度提升到 1ms，否则 60fps 会被 ~15.6ms 的时钟粒度限制在 30fps 左右。</summary>
	[DllImport("winmm.dll")]
	private static extern uint timeBeginPeriod(uint uPeriod);

	[DllImport("winmm.dll")]
	private static extern uint timeEndPeriod(uint uPeriod);

	/// <summary>退出时恢复系统定时器精度，配对 InitWinForms 里的 timeBeginPeriod(1)。
	/// 不配对会导致本进程退出后系统仍保持 1ms 精度、徒增整机功耗。</summary>
	public static void RestoreTimerResolution()
	{
		try { timeEndPeriod(1); } catch { }
	}

	/// <summary>WinForms 全局初始化。必须在任何 WinForms 控件创建前调用一次。</summary>
	public static void InitWinForms()
	{
		// 尽早挂钩致命异常诊断：AccessViolation 这类原生错误事后无法从事件日志还原
		try
		{
			CrashDiagnostics.EnableLocalDumps();
			CrashDiagnostics.Install();
		}
		catch
		{
		}

		Application.EnableVisualStyles();
		Application.SetCompatibleTextRenderingDefault(false);
		timeBeginPeriod(1);          // 提升定时精度，保证帧率上限 60 真正可达

		// 兜底：任何未处理异常都记录到 crash.log 并提示，而不是直接闪退
		Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
		Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e) {
			ReportFatal(e.Exception);
		};
		AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) {
			ReportFatal(e.ExceptionObject as Exception);
		};
		TaskScheduler.UnobservedTaskException += delegate(object sender, UnobservedTaskExceptionEventArgs e) {
			e.SetObserved();
			AppLog.Write("异常", e.Exception.ToString());
		};
	}

	/// <summary>
	/// 处理 desk 的独立运行模式。返回 <c>true</c> 表示已接管，调用方应立即结束进程，
	/// 退出码写入 <paramref name="exitCode"/>（启动失败返回非 0，便于 SessionLauncher / 服务识别）。
	/// </summary>
	public static bool TryRunStandaloneMode(string[] args, out int exitCode)
	{
		exitCode = 0;
		// 诊断开关：-diagtest 自检异常钩子，-diag 仅打印现场后退出
		if (HasArg(args, "-diagtest"))
		{
			CrashDiagnostics.SelfTest();
			ReportFatal(new Exception("SelfTest finished, crash.log should contain the hook record."));
			return true;
		}
		if (HasArg(args, "-diag"))
		{
			CrashDiagnostics.WriteBaseline();
			ReportFatal(new Exception("Diagnostic baseline written, crash.log should contain startup info."));
			return true;
		}
		// -h264probe：只跑一次 H.264 编码器/解码器探测后退出。
		// 该探测原先会在 mfplat.dll 内触发 0xC0000005，用它可单独验证修复是否生效，
		// 且不会启动被控端/端口监听。
		if (HasArg(args, "-h264probe"))
		{
			string probe;
			try
			{
				probe = H264Codec.Probe();
			}
			catch (Exception ex)
			{
				probe = "探测抛出异常：" + ex;
			}
			ReportFatal(new Exception("H264 probe => " + probe));
			return true;
		}


		if (HasArg(args, "-service"))
		{
			// -service：以 Windows 服务方式运行（SYSTEM，session 0），负责拉起会话内被控端
			ServiceProcess.ServiceBase.Run(new UacService());
			return true;
		}

		if (HasArg(args, "-host"))
		{
			// -host：系统级无界面被控端（由服务在用户会话内以 SYSTEM 令牌启动）
			if (!RunHeadlessHost()) { exitCode = 1; ReportFatal(new Exception("-host 被控端启动失败")); }
			return true;
		}

		if (HasArg(args, "-web"))
		{
			// -web <端口> [密码]：启动被控端 + HTTP/WebSocket 网关，浏览器即可远控
			int webPort = desk.WebStatus.DefaultPort;   // Web 端默认端口（统一 9090）
			if (args.Length >= 2)
			{
				if (!int.TryParse(args[1], out int p) || p <= 0 || p > 65535)
				{
					MessageBox.Show("端口无效，应为 1~65535 之间的整数。\n例如：Painter.exe -web 9090", "Web 远控", MessageBoxButtons.OK, MessageBoxIcon.Information);
					exitCode = 1;
					return true;
				}
				webPort = p;
			}
			string webPwd = args.Length > 2 ? args[2] : null;
			if (!RunWebMode(webPort, webPwd)) { exitCode = 1; ReportFatal(new Exception("-web 远控网关启动失败")); }
			return true;
		}

		return false;
	}

	/// <summary>无界面被控端：供 UAC/安全桌面支持使用（SYSTEM 权限、用户会话内）。成功进入阻塞时永不返回。</summary>
	private static bool RunHeadlessHost()
	{
		try
		{
			var (port, pwd) = Settings.LoadHostConfig();
			// 系统级(-host)进程只负责 UAC/安全桌面；常规桌面由主程序(S端)用 DXGI 抓取。
			// 桌面复制对同一显示输出全局只能有一个持有者——若本进程也抢 DXGI，会与主程序互相
			// 撞 E_INVALIDARG（"另一进程占用"）。故 -host 强制回退 GDI：安全桌面无 explorer，
			// GDI BitBlt 不会冻结，且把 DXGI 名额让给主程序。
			DxgiCapture.Enabled = false;
			var server = new RemoteDesktopServer(port)
			{
				ServerPassword = pwd,
				FollowInputDesktop = true,   // SYSTEM 级：跟随安全桌面，才能抓到 UAC
			};
			server.Log += s => HostLog(s);
			server.Start();
			HostLog($"系统级被控端已启动，端口 {port}（会话 {Process.GetCurrentProcess().SessionId}）");
			new System.Threading.ManualResetEventSlim(false).Wait();   // 阻塞主线程，服务端在后台线程运行
			return true;
		}
		catch (Exception ex)
		{
			HostLog("启动失败：" + ex.Message);
			return false;
		}
	}

	/// <summary>Web 远控模式：在本进程内启动被控端 + HTTP/WS 网关，
	/// 浏览器访问指定端口即可浏览并操作桌面（被控端与网关同进程，WebSocket 透明桥接到被控端 TCP）。</summary>
	private static bool RunWebMode(int httpPort, string password)
	{
		try
		{
			// 先放行防火墙 / 注册 URL ACL，再绑定 HTTP：否则首次换新端口 9090 时
			// 会退化成仅 127.0.0.1 可访问，局域网需重启才生效。
			try { FirewallSetup.EnsureNetworkAccess(httpPort, 0, 0, 0, false, WebLog); }
			catch (Exception ex) { WebLog("启动 Web 前申请网络访问失败（已忽略）：" + ex.Message); }

			// Web 远控默认走 DXGI Desktop Duplication（与桌面端一致，抓屏从 ~250ms/帧 降到 <15ms/帧）。
			// 本进程是唯一的桌面复制持有者（Global 互斥已防重复占用），不会与主程序冲突；
			// 若某环境仍触发异常，DxgiCapture.TryGetFrame 会自动降级回退 GDI（见 DxgiCapture.cs）。
			DxgiCapture.Enabled = true;
			var srv = new RemoteDesktopServer(0)   // 0 = 由系统分配空闲端口
			{
				ServerPassword = password ?? "",
				AudioEnabled = false,   // 声音无需传输到 Web 端
			};
			srv.Log += s => WebLog(s);
			srv.Start();
			int srvPort = srv.Port;
			// 访问密码必须同时交给网关：否则 CheckCodeAuth 因 _password 为空而恒放行，
			// 且网关用空密码去登录设了密码的被控端，浏览器永远拿不到画面。
			var web = new WebServer(httpPort, srvPort, password ?? "");
			web.Start();
			desk.WebStatus.MarkRunning(httpPort, web.IsLoopbackOnly);
			if (web.IsLoopbackOnly)
				WebLog("警告：当前只绑定了 127.0.0.1，URL ACL / 防火墙未放行，局域网无法访问；请放行后重启本进程。");
			WebLog($"Web 远控已就绪（监听 0.0.0.0:{httpPort}，全部网卡）：" + string.Join(" 或 ", desk.WebStatus.GetAllUrls()) + $"  （密码：{(string.IsNullOrEmpty(password) ? "空（无密码）" : password)}）");
			if (web.IsRawHttpMode)
				WebLog("当前使用纯 TCP 备选模式（不依赖系统 HTTP 组件）；图片/文件等扩展接口在此模式下不可用，其余控制台功能正常。");
			WebLog($"被控端 TCP 端口 {srvPort}｜Web 网关端口 {httpPort}");
			new System.Threading.ManualResetEventSlim(false).Wait();   // 阻塞主线程，后台线程提供服务
			return true;
		}
		catch (Exception ex)
		{
			WebLog("Web 模式启动失败：" + ex.Message);
			return false;
		}
	}

	private static bool HasArg(string[] args, string name)
	{
		return args != null && args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>未处理异常：写 crash.log 并弹框。注意 AppDomain.UnhandledException 在 .NET 4.x
	/// 触发后进程仍会终止，这里只保证崩溃有据可查，不能真正阻止退出。</summary>
	public static void ReportFatal(Exception ex)
	{
		if (ex == null) return;
		try
		{
			CrashDiagnostics.AppendCrashLine(
				$"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n");
		}
		catch { }
		try
		{
			MessageBox.Show(
				"程序出现异常，详细信息已写入程序目录下的 crash.log。\n\n" + ex.Message,
				"异常", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
		catch { }
	}

	private static void WebLog(string msg)
	{
		try
		{
			File.AppendAllText(Path.Combine(AppContext.BaseDirectory, AppIdentity.WebLogFileName),
				$"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\r\n");
		}
		catch { }
	}

	private static void HostLog(string msg)
	{
		try
		{
			File.AppendAllText(Path.Combine(Path.GetTempPath(), AppIdentity.HostLogFileName),
				$"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\r\n");
		}
		catch { }
	}
}
