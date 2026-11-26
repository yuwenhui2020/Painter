using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace desk;

/// <summary>
/// 启动耗时埋点。用于定位「刚运行程序卡住一小会儿」到底卡在哪一步。
/// <para>
/// 记录每个阶段相对**进程启动时刻**的耗时（而不是相对首次调用的耗时），
/// 这样才能看出从进程创建到界面可交互的总时长，以及其中哪一段占了大头。
/// </para>
/// <para>
/// 写入 exe 同目录下的 <c>startup_timing.log</c>。启动期约 30 次 Mark，
/// 原来每次都同步「打开-写入-关闭」一次文件，在慢盘 / 杀软实时扫描下会被
/// 放大成明显的启动卡顿；现在改为内存缓冲 + 200ms 合并落盘，进程退出前补写。
/// </para>
/// </summary>
public static class StartupTiming
{
	private static readonly object Sync = new object ();

	private static readonly string LogPath =
		Path.Combine (AppContext.BaseDirectory, "startup_timing.log");

	/// <summary>进程启动时刻（取自 OS）。取不到时退化为首次调用时刻。</summary>
	private static readonly DateTime ProcStart = ResolveProcessStart ();

	private static readonly List<string> Pending = new List<string> ();
	private static readonly Timer FlushTimer;

	static StartupTiming ()
	{
		FlushTimer = new Timer (Flush, null, Timeout.Infinite, Timeout.Infinite);
		// 正常退出时把最后几条埋点补写，避免缓冲区丢内容。
		AppDomain.CurrentDomain.ProcessExit += (s, e) => Flush (null);
	}

	private static DateTime ResolveProcessStart ()
	{
		try {
			using (Process p = Process.GetCurrentProcess ()) {
				return p.StartTime;
			}
		} catch {
			return DateTime.Now;
		}
	}

	/// <summary>记录一个启动阶段。thread 为 true 时额外标注线程（UI/后台）归属。</summary>
	public static void Mark (string stage, bool uiThread = false)
	{
		try {
			double ms = (DateTime.Now - ProcStart).TotalMilliseconds;
			string line = string.Format ("[{0,9:F1} ms] {1}{2}{3}",
				ms,
				uiThread ? "[UI] " : "[--] ",
				stage,
				Environment.NewLine);
			lock (Sync) {
				Pending.Add (line);
				// 只在队列由空变非空时排一次刷新，避免每个埋点都重置定时器。
				if (Pending.Count == 1) FlushTimer.Change (200, Timeout.Infinite);
			}
		} catch {
			// 埋点失败绝不能影响启动
		}
	}

	/// <summary>立即把缓冲区合并落盘（定时器回调 / 进程退出时调用）。</summary>
	private static void Flush (object state)
	{
		string[] lines;
		lock (Sync) {
			if (Pending.Count == 0) return;
			lines = Pending.ToArray ();
			Pending.Clear ();
		}
		try {
			File.AppendAllText (LogPath, string.Concat (lines), Encoding.UTF8);
		} catch {
			// 埋点失败绝不能影响启动 / 退出
		}
	}
}
