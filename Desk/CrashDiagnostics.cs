using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace desk
{
	/// <summary>
	/// 崩溃诊断：把致命原生异常（AccessViolation 等）的现场写入 crash.log。
	/// 这类异常在事件日志里通常只留下"错误模块 unknown"，堆栈也无法还原，事后几乎无法定位。
	/// </summary>
	public static class CrashDiagnostics
	{
		private static readonly object WriteLock = new object();
		private static volatile bool _installed;
		private static readonly DateTime StartUtc = DateTime.UtcNow;
		private static readonly int ProcessId = GetPid();
		// 单实例互唤、-m 多开时会有多个进程写同一份 crash.log，
		// 进程内的 WriteLock 挡不住跨进程交错，需要一把命名互斥锁。
		private const string LogMutexName = @"Local\Painter_CrashLog";
		private static int _fatalCount;      // 致命 first-chance 次数
		private static int _handledCount;    // 被吞异常留痕次数
		private static int _uiHangCount;     // UI 挂起次数

		private static int GetPid()
		{
			try { using (Process p = Process.GetCurrentProcess()) return p.Id; }
			catch { return 0; }
		}

		public static void Install()
		{
			if (_installed) return;
			_installed = true;
			try { AppDomain.CurrentDomain.FirstChanceException += OnFirstChance; }
			catch (Exception ex) { TryWrite("install hook failed: " + ex); }
			// 正常退出也写一条收尾记录：只有 baseline 而没有 exit，就说明进程是被
			// 强杀 / 崩溃 / 挂死终止的 —— 这本身就是 crash.log 最有价值的信号。
			try { AppDomain.CurrentDomain.ProcessExit += OnProcessExit; }
			catch { }
			WriteBaseline();
		}

		private static void OnProcessExit(object sender, EventArgs e)
		{
			try
			{
				long seconds = (long)(DateTime.UtcNow - StartUtc).TotalSeconds;
				// 一行给出本次运行的健康状态，不必逐条翻日志；
				// 缺少这行（只有 baseline）就说明进程是被强杀 / 崩溃 / 挂死终止的。
				TryWrite("==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
					+ " normal exit [uptime " + seconds + "s"
					+ " | ui-hang " + _uiHangCount
					+ " | handled " + _handledCount
					+ " | fatal " + _fatalCount + "] ====");
			}
			catch { }
		}

		/// <summary>
		/// 记录被业务代码 try/catch 吞掉、但值得留痕的异常。同一来源 60 秒内只记一次，
		/// 避免网络断开等高频异常把 crash.log 刷爆。
		/// </summary>
		public static void NoteHandled(string context, Exception ex)
		{
			if (ex == null) return;
			try
			{
				string key = (context ?? "?") + "|" + ex.GetType().FullName;
				DateTime now = DateTime.UtcNow;
				lock (NoteLock)
				{
					DateTime last;
					if (LastNote.TryGetValue(key, out last) && (now - last).TotalSeconds < 60) return;
					LastNote[key] = now;
				}
				TryWrite("==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " handled exception (suppressed in app) ===="
					+ Environment.NewLine + "context : " + context
					+ Environment.NewLine + "type    : " + ex.GetType().FullName
					+ Environment.NewLine + "message : " + ex.Message
					+ Environment.NewLine + "stack   : " + ex.StackTrace
					+ Environment.NewLine + "recent  :" + Environment.NewLine + Tail());
				Interlocked.Increment(ref _handledCount);
			}
			catch { }
		}

		private static readonly object NoteLock = new object();
		private static readonly Dictionary<string, DateTime> LastNote = new Dictionary<string, DateTime>();

		/// <summary>读出系统实际安装的 .NET Framework 版本，便于判断目标机运行时是否达标。</summary>
		private static string DescribeFramework()
		{
			try
			{
				using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
					@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
				{
					object release = key?.GetValue("Release");
					int r = release is int i ? i : 0;
					string name = r >= 528040 ? "4.8+" : r >= 461808 ? "4.7.2" : r >= 461308 ? "4.7.1"
						: r >= 460798 ? "4.7" : r >= 394802 ? "4.6.2" : r > 0 ? "4.6.1 或更早" : "未知";
					return ".NET Framework " + name + " (Release=" + r + ")";
				}
			}
			catch { return "读取失败"; }
		}

		// ===== UI 无响应（AppHang）看门狗 =====
		// 异常钩子只能记录「抛了异常」的故障；界面被同步调用/死锁卡住时什么都不抛，
		// WER 的 LocalDumps 也只对崩溃生成 dump、对 AppHang 无效 —— 这正是过去
		// crash.log 只有启动基线却一片空白的原因。用 UI Dispatcher 心跳补上这块盲区。
		private static readonly object WatchdogLock = new object();
		private const int HangThresholdMs = 10000;
		private static volatile int _lastUiTick;
		private static volatile bool _hangReported;
		private static System.Windows.Threading.DispatcherTimer _uiTimer;
		private static System.Threading.Timer _watchdog;

		/// <summary>在 WPF UI 线程装 1 秒心跳，并用独立线程池定时器监测心跳是否停摆。</summary>
		public static void InstallUiWatchdog(System.Windows.Threading.Dispatcher dispatcher)
		{
			if (dispatcher == null) return;
			lock (WatchdogLock)
			{
				if (_watchdog != null) return;
				_lastUiTick = Environment.TickCount;
				try
				{
					// 用 Normal 而不是 Background：后台优先级在 UI 繁忙时容易被饿死，
					// 会把「界面还活着但很忙」误判成挂起。
					_uiTimer = new System.Windows.Threading.DispatcherTimer(
						System.Windows.Threading.DispatcherPriority.Normal, dispatcher);
					_uiTimer.Interval = TimeSpan.FromSeconds(1);
					_uiTimer.Tick += delegate { _lastUiTick = Environment.TickCount; };
					_uiTimer.Start();
				}
				catch (Exception ex)
				{
					TryWrite("InstallUiWatchdog failed (crash.log 仍只覆盖异常): " + ex);
					return;
				}
				_watchdog = new System.Threading.Timer(OnWatchdogTick, null, 1000, 1000);
			}
		}

		private static void OnWatchdogTick(object state)
		{
			try
			{
				// 退出过程中 Dispatcher 已停止，心跳自然停摆，属正常现象，不记录。
				System.Windows.Application app = System.Windows.Application.Current;
				if (app == null || app.Dispatcher.HasShutdownStarted) return;

				int elapsed = unchecked(Environment.TickCount - _lastUiTick);
				if (elapsed < HangThresholdMs)
				{
					if (elapsed < 2000) _hangReported = false;   // UI 已恢复，允许下次再报
					return;
				}
				if (_hangReported) return;
				_hangReported = true;
				ReportUiHang(elapsed);
			}
			catch { }
		}

		private static void ReportUiHang(int elapsed)
		{
			Interlocked.Increment(ref _uiHangCount);
			try
			{
				StringBuilder sb = new StringBuilder();
				sb.AppendLine("==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " UI HANG (dispatcher heartbeat lost) ====");
				sb.AppendLine("silent    : " + elapsed + " ms (threshold " + HangThresholdMs + " ms)");
				sb.AppendLine("module    : " + GetModulePath());
				sb.AppendLine("memws     : " + Mem());
				sb.AppendLine("hint      : UI 线程长时间未处理 Dispatcher 队列。优先排查托盘退出 / 单实例互唤 / PPT COM 调用中的跨线程同步 Invoke 与锁等待。");
				try
				{
					using (Process p = Process.GetCurrentProcess())
					{
						foreach (ProcessThread t in p.Threads)
						{
							try
							{
								sb.AppendLine("  thread " + t.Id + " state=" + t.ThreadState
									+ " wait=" + t.WaitReason + " total=" + t.TotalProcessorTime);
							}
							catch { }
						}
					}
				}
				catch { }
				sb.AppendLine("recent  :" + Environment.NewLine + Tail());
				TryWrite(sb.ToString());
			}
			catch { }
		}

		/// <summary>
		/// 为当前 exe 注册 WER 本地转储。硬原生崩溃（0xC0000005 / 错误模块 unknown）不会
		/// 走托管异常钩子，进程直接被终止，只有 minidump 能还原现场。
		/// </summary>
		public static void EnableLocalDumps()
		{
			try
			{
				string exe = GetModulePath();
				if (string.IsNullOrEmpty(exe) || exe == "?") return;
				// WER 的 LocalDumps 子键名就是 exe 文件名（不含路径、不含扩展名）。
				// 之前误用了完整路径当键名，注册表里被截成了 "C:"，转储实际上从未生效。
				string imageName = Path.GetFileNameWithoutExtension (exe);
				if (string.IsNullOrEmpty (imageName)) return;
				using (Microsoft.Win32.RegistryKey root = Microsoft.Win32.Registry.CurrentUser)
				using (Microsoft.Win32.RegistryKey dumps = root.CreateSubKey(@"Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\" + imageName))
				{
					string dumpDir = Path.Combine (AppContext.BaseDirectory, "dumps");
					try { Directory.CreateDirectory (dumpDir); } catch { }
					dumps.SetValue ("DumpFolder", dumpDir, Microsoft.Win32.RegistryValueKind.String);
					// 1 = mini dump（含线程栈与模块列表，足够定位原生崩溃）。
					// 原先用 2 = full dump：x86 进程 150MB+ 工作集，单个转储几百 MB，
					// DumpCount=5 会在程序目录堆出数 GB 文件。
					dumps.SetValue ("DumpType", 1, Microsoft.Win32.RegistryValueKind.DWord);
					dumps.SetValue ("DumpCount", 5, Microsoft.Win32.RegistryValueKind.DWord);
					dumps.SetValue ("DumpLogComment", "painter crash dump", Microsoft.Win32.RegistryValueKind.String);
				}
				TryWrite ("LocalDumps enabled for image=" + imageName + " (mini dump) dir=" + Path.Combine (AppContext.BaseDirectory, "dumps"));
			}
			catch (Exception ex)
			{
				TryWrite ("EnableLocalDumps failed: " + ex);
			}
		}

		/// <summary>记录启动现场：进程路径、CLR 版本、已加载的原生模块。用于确认钩子是否生效。</summary>
		public static void WriteBaseline()
		{
			try
			{
				StringBuilder sb = new StringBuilder();
				sb.AppendLine("==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " startup baseline ====");
				sb.AppendLine("pid     : " + ProcessId);
				sb.AppendLine("version : " + EntryVersion());
				sb.AppendLine("module  : " + GetModulePath());
				sb.AppendLine("clr     : " + Environment.Version + " / " + (Environment.Is64BitProcess ? "x64" : "x86"));
				sb.AppendLine("runtime : " + DescribeFramework());
				sb.AppendLine("os      : " + Environment.OSVersion + " / " + (Environment.Is64BitOperatingSystem ? "x64 OS" : "x86 OS"));
				sb.AppendLine("baseDir : " + AppContext.BaseDirectory);
				sb.AppendLine("cmdline : " + Environment.CommandLine);
				int total = 0, external = 0;
				using (Process proc = System.Diagnostics.Process.GetCurrentProcess())
				{
					foreach (ProcessModule m in proc.Modules)
					{
						string fn = m.FileName;
						if (string.IsNullOrEmpty(fn)) continue;
						total++;
						// 启动极早期业务 DLL 尚未加载；只列 Windows 目录之外的模块。
						// 旧过滤只排除了 system32，而 .NET/WPF 模块在 Windows\assembly
						// 与 Windows\Microsoft.NET 下，结果整个日志被系统模块刷满。
						if (fn.ToLowerInvariant().Contains(@"\windows\")) continue;
						external++;
						sb.AppendLine("  mod " + m.ModuleName + "  <- " + fn);
					}
				}
				sb.AppendLine("modules : " + total + " loaded, " + external + " outside Windows dir");
				TryWrite(sb.ToString());
			}
			catch (Exception ex)
			{
				TryWrite("WriteBaseline failed: " + ex);
			}
		}

		/// <summary>
		/// 自检：抛出一个真正的 AccessViolationException 实例（不是真的踩内存），
		/// 用来验证 first-chance 钩子链路是否通。对应命令行 -diagtest。
		/// </summary>
		/// <summary>
		/// 供 DeskBootstrap.ReportFatal 复用：必须与 OnFirstChance 共用同一把锁与同一个文件，
		/// 否则两处并发 AppendAllText 会因文件占用抛 IOException 被 catch 吞掉，崩溃现场丢失。
		/// </summary>
		public static void AppendCrashLine(string text)
		{
			TryWrite(text);
		}

		public static void SelfTest()
		{
			TryWrite("---- SelfTest: 抛合成 AccessViolationException 验证钩子 ----");
			try
			{
				throw new AccessViolationException("SELF-TEST: synthetic, not a real memory fault");
			}
			catch (Exception ex)
			{
				TryWrite("SelfTest caught (expected): " + ex.GetType().FullName);
			}
		}

		private static string GetModulePath()
		{
			try { using Process p = System.Diagnostics.Process.GetCurrentProcess(); ProcessModule m = p.MainModule; return m == null ? "?" : m.FileName; }
			catch { return "?"; }
		}

		private static string EntryVersion()
		{
			try
			{
				System.Reflection.Assembly a = System.Reflection.Assembly.GetEntryAssembly();
				return a == null ? "?" : (a.GetName().Version?.ToString() ?? "?");
			}
			catch { return "?"; }
		}

		private static void OnFirstChance(object s, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
		{
			Exception ex = e.Exception;
			if (ex == null || !IsFatal(ex)) return;
			Interlocked.Increment(ref _fatalCount);
			try
			{
				StringBuilder sb = new StringBuilder();
				sb.AppendLine("==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " fatal first-chance ====");
				sb.AppendLine("type    : " + ex.GetType().FullName);
				sb.AppendLine("HResult : 0x" + ex.HResult.ToString("X8"));
				sb.AppendLine("message : " + ex.Message);
				sb.AppendLine("thread  : " + Thread.CurrentThread.ManagedThreadId + (Thread.CurrentThread.IsBackground ? " (bg)" : " (fg)"));
				sb.AppendLine("module  : " + Module());
				sb.AppendLine("memws   : " + Mem());
				sb.AppendLine("stack   :" + Environment.NewLine + ex.StackTrace);
				if (ex.InnerException != null) sb.AppendLine("inner   : " + ex.InnerException);
				sb.AppendLine("recent  :" + Environment.NewLine + Tail());
				TryWrite(sb.ToString());
			}
			catch { }
		}

		/// <summary>只记录最可能致命的几类，避免正常业务异常刷屏。</summary>
		private static bool IsFatal(Exception ex)
		{
			return ex is AccessViolationException
				|| ex is System.Runtime.InteropServices.SEHException
				|| ex is StackOverflowException
				|| ex is OutOfMemoryException
				|| ex is InvalidProgramException
				|| ex is BadImageFormatException
				|| ex is TypeInitializationException;
		}

		private static string Module()
		{
			return GetModulePath();
		}

		private static string Mem()
		{
			try { using Process p = Process.GetCurrentProcess(); p.Refresh(); return (p.WorkingSet64 / 1048576) + "MB ws, " + p.HandleCount + " handles"; }
			catch { return "?"; }
		}

		private static string Tail()
		{
			try
			{
				string p = AppLog.FilePath;
				if (!File.Exists(p)) return "(no " + AppIdentity.LogFileName + ")";
				// 只读末尾 16KB：AppLog 会轮转到 4MB，每次 fatal/note 都 ReadAllLines
				// 整份日志会在异常路径上造成明显卡顿，且日志是以写共享方式打开的。
				using (FileStream fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
				{
					long len = fs.Length;
					int take = (int)Math.Min(len, 16 * 1024);
					fs.Seek(len - take, SeekOrigin.Begin);
					byte[] buf = new byte[take];
					int read = fs.Read(buf, 0, take);
					string[] lines = Encoding.UTF8.GetString(buf, 0, read).Split('\n');
					StringBuilder sb = new StringBuilder();
					int start = Math.Max(0, lines.Length - 15);
					for (int i = start; i < lines.Length; i++)
					{
						// 从文件中间切进来时第一行可能是被截断的半行，跳过。
						if (i == start && take < len) continue;
						sb.AppendLine("  " + lines[i].TrimEnd('\r'));
					}
					return sb.ToString();
				}
			}
			catch { return "(read failed)"; }
		}

		private const long MaxLogBytes = 1024L * 1024L;

		private static void TryWrite(string text)
		{
			System.Threading.Mutex crossProcess = null;
			bool held = false;
			try
			{
				// 单实例互唤 / -m 多开时会有多个进程写同一份日志，进程内的
				// WriteLock 挡不住跨进程交错。用命名 Mutex 保证整条记录原子写入；
				// 等不到锁也继续写，宁可偶发交错也不能阻塞退出或异常处理路径。
				try
				{
					crossProcess = new System.Threading.Mutex(false, LogMutexName);
					try { held = crossProcess.WaitOne(1000); }
					catch (System.Threading.AbandonedMutexException) { held = true; }
				}
				catch { }
				lock (WriteLock)
				{
					string path = Path.Combine(AppContext.BaseDirectory, "crash.log");
					// 每条记录都带 pid，baseline 与 normal exit 才能正确配对；
					// 日志被中途清空或截断时也不会把不同进程的记录混为一谈。
					string record = text ?? "";
					record = record.StartsWith("====")
						? "==== [pid " + ProcessId + "]" + record.Substring(4)
						: "[pid " + ProcessId + "] " + record;
					bool exists = File.Exists(path);
					bool needBom = !exists || new FileInfo(path).Length == 0;
					// 超过 1MB 归档成 crash.log.old，避免长期运行堆成巨型文件。
					try
					{
						if (exists && new FileInfo(path).Length > MaxLogBytes)
						{
							string old = path + ".old";
							try { File.Delete(old); } catch { }
							try { File.Move(path, old); } catch { }
							// 只有确实移走了才重建；Move 失败时继续追加，绝不覆盖已有现场。
							if (!File.Exists(path)) needBom = true;
						}
					}
					catch { }
					// 首次写入带 UTF-8 BOM：Win7 记事本默认按 ANSI 解析，
					// 无 BOM 时中文会显示成乱码。
					if (needBom) File.WriteAllText(path, "\uFEFF", new UTF8Encoding(false));
					File.AppendAllText(path, record + Environment.NewLine, new UTF8Encoding(false));
				}
			}
			catch { }
			finally
			{
				if (held) { try { crossProcess.ReleaseMutex(); } catch { } }
				if (crossProcess != null) { try { crossProcess.Dispose(); } catch { } }
			}
		}
	}
}