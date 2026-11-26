using System;
using System.Diagnostics;
using System.Threading.Tasks;

using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Windows.Forms;

namespace desk;

/// <summary>
/// PPT 放映控制。
///
/// 两种实现，自动择优：
///   ① PowerPoint COM 自动化（首选）—— 能拿到准确页码（当前页/总页数），控制也最可靠；
///   ② 快捷键注入（回退）—— 未运行 PowerPoint、COM 被权限拦截、或用的是 WPS 等兼容软件时，
///      退化为对当前前台窗口发 F5 / Esc / 方向键 / Home / End，控制依然可用，只是拿不到页码。
///
/// 采用后期绑定（ProgID + dynamic），无需在编译时引用 Office 主互操作程序集，
/// 没装 PowerPoint 的机器上也能正常编译与运行。
///
/// 重要：只有检测到 POWERPNT 进程确实在运行时才走 COM。否则 Activator.CreateInstance
/// 会去【启动】PowerPoint，耗时十几秒直接把 HTTP 处理线程堵死（表现为接口超时）。
/// </summary>
internal static class PptController
{
	public static Action<string>? Log;

	private const ushort VK_F5 = 0x74;
	private const ushort VK_ESCAPE = 0x1B;
	private const ushort VK_LEFT = 0x25;
	private const ushort VK_RIGHT = 0x27;
	private const ushort VK_HOME = 0x24;
	private const ushort VK_END = 0x23;

	private const int ComTimeoutMs = 3000;

	private static volatile bool _comDisabled;
	private static int _comFails;
	private static long _lastNoteTick;

	/// <summary>释放每次 COM 调用创建的 Application RCW。Activator.CreateInstance 每调用一次
	/// 都会产生一个新 RCW（引用计数 +1），不释放会随状态轮询持续累积，最终 PowerPoint
	/// 无法退出、本机句柄与内存缓涨。用 FinalReleaseComObject 一次性清干净。</summary>
	private static void ReleaseApp(dynamic? app)
	{
		try {
			if (app != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(app);
		} catch { }
	}

	/// <summary>释放链式 COM 访问产生的中间 RCW（集合/窗口/View/Presentation）。</summary>
	private static void ReleaseCom(params object[]? objs)
	{
		if (objs == null) return;
		foreach (object o in objs) {
			try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(o); } catch { }
		}
	}

	/// <summary>当前放映状态：current=当前页(0 表示未放映)，total=总页数，running=是否放映中。</summary>
	public static (int current, int total, bool running) GetStatus ()
	{
		if (_comDisabled || !IsPowerPointRunning ()) {
			return (0, 0, false);
		}
		try {
			Task<(int, int, bool)> task = Task.Run (ComStatus);
			if (!task.Wait (ComTimeoutMs)) {
				Note ("COM 查询超时，本次跳过");
				return (0, 0, false);
			}
			Interlocked.Exchange (ref _comFails, 0);
			return task.Result;
		} catch (Exception ex) {
			OnComFail (ex);
			return (0, 0, false);
		}
	}

	/// <summary>执行放映命令（start/stop/next/prev/first/last）。</summary>
	public static bool Run (string cmd)
	{
		bool ok = false;
		if (!_comDisabled && IsPowerPointRunning ()) {
			try {
				Task<bool> task = Task.Run (() => ComRunWrap (cmd));
				if (task.Wait (ComTimeoutMs) && task.Result) {
					Interlocked.Exchange (ref _comFails, 0);
					ok = true;
				}
			} catch (Exception ex) {
				OnComFail (ex);
			}
		}
		if (!ok) ok = KeyFallback (cmd);
		// 只有「开始放映」才把放映窗口聚焦并置顶；stop / next / prev 等逻辑完全不变。
		if (ok && string.Equals (cmd, "start", StringComparison.OrdinalIgnoreCase)) {
			PptWindowFocus.BringSlideShowToFrontAsync ();
		}
		return ok;
	}

	private static bool IsPowerPointRunning ()
	{
		try {
			Process[] procs = Process.GetProcessesByName ("POWERPNT");
			try { return procs.Length > 0; }
			finally { foreach (Process p in procs) { try { p.Dispose (); } catch { } } }
		} catch {
			return false;
		}
	}

	private static (int, int, bool) ComStatus ()
	{
		dynamic? app = GetApp ();
		if (app == null) {
			return (0, 0, false);
		}
		dynamic? windows = null;
		dynamic? window = null;
		dynamic? view = null;
		try {
			windows = app.SlideShowWindows;
			if (windows != null && (int)windows.Count > 0) {
				window = windows [1];
				view = window?.View;
				return ((view != null ? (int)view.CurrentShowPosition : 0), TotalSlides (app), true);
			}
			return (0, TotalSlides (app), false);
		} finally {
			ReleaseCom (view, window, windows);
			ReleaseApp (app);
		}
	}

	private static bool ComRunWrap (string cmd)
	{
		dynamic? app = GetApp ();
		if (app == null) {
			return false;
		}
		try {
			return ComRun (app, cmd);
		} finally {
			ReleaseApp (app);
		}
	}

	private static bool ComRun (dynamic app, string cmd)
	{
		int shows = (int)app.SlideShowWindows.Count;
		switch (cmd) {
		case "start":
			if ((int)app.Presentations.Count == 0) {
				return false;
			}
			if (shows > 0) {
				return true;   // 已在放映
			}
			app.ActivePresentation.SlideShowSettings.Run ();
			return true;
		case "stop":
			if (shows == 0) {
				return true;   // 本来就没在放映
			}
			app.SlideShowWindows [1].View.Exit ();
			return true;
		case "next":
			if (shows == 0) return false;
			app.SlideShowWindows [1].View.Next ();
			return true;
		case "prev":
			if (shows == 0) return false;
			app.SlideShowWindows [1].View.Previous ();
			return true;
		case "first":
			if (shows == 0) return false;
			app.SlideShowWindows [1].View.First ();
			return true;
		case "last":
			if (shows == 0) return false;
			app.SlideShowWindows [1].View.Last ();
			return true;
		}
		return false;
	}

	private static bool KeyFallback (string cmd)
	{
		ushort vk = cmd switch {
			"start" => VK_F5, 
			"stop" => VK_ESCAPE, 
			"next" => VK_RIGHT, 
			"prev" => VK_LEFT, 
			"first" => VK_HOME, 
			"last" => VK_END, 
			_ => (ushort)0, 
		};
		if (vk == 0) {
			return false;
		}
		try {
			InputInjector.Key (vk, down: true);
			InputInjector.Key (vk, down: false);
			return true;
		} catch (Exception ex) {
			Log?.Invoke ("[PPT] 快捷键注入失败：" + ex.Message);
			return false;
		}
	}

	private static int TotalSlides (dynamic app)
	{
		dynamic? windows = null, window = null, pres = null, slides = null;
		dynamic? presentations = null, activePres = null, activeSlides = null;
		try {
			windows = app.SlideShowWindows;
			if (windows != null && (int)windows.Count > 0) {
				window = windows [1];
				pres = window?.Presentation;
				slides = pres?.Slides;
				if (slides != null) return (int)slides.Count;
			}
			presentations = app.Presentations;
			if (presentations != null && (int)presentations.Count > 0) {
				activePres = app.ActivePresentation;
				activeSlides = activePres?.Slides;
				if (activeSlides != null) return (int)activeSlides.Count;
			}
		} catch {
		} finally {
			ReleaseCom (slides, pres, window, windows, activeSlides, activePres, presentations);
		}
		return 0;
	}

	private static dynamic? GetApp ()
	{
		Type? type = Type.GetTypeFromProgID ("PowerPoint.Application");
		if (type == null) {
			if (!_comDisabled) {
				_comDisabled = true;
				Log?.Invoke ("[PPT] 未检测到 PowerPoint，改用快捷键控制（兼容 WPS 等）。");
			}
			return null;
		}
		return Activator.CreateInstance (type);
	}

	private static void OnComFail (Exception ex)
	{
		int fails = Interlocked.Increment (ref _comFails);
		if (fails == 1 || fails == 3) {
			Log?.Invoke ($"[PPT] PowerPoint COM 调用失败（第 {fails} 次），本次改用快捷键：{ex.Message}");
		}
		if (fails >= 5 && !_comDisabled) {
			_comDisabled = true;
			Log?.Invoke ("[PPT] COM 连续失败，已停用，后续统一用快捷键控制。");
		}
	}

	private static void Note (string s)
	{
		long tick = NetFx.TickCount64;
		if (tick - Interlocked.Read (ref _lastNoteTick) < 10000) {
			return;   // 避免状态轮询每 5 秒刷屏
		}
		Interlocked.Exchange (ref _lastNoteTick, tick);
		Log?.Invoke ("[PPT] " + s);
	}
}
