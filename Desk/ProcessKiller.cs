using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Timers;
// 与 ViewerForm.cs 一致：desk 的全局 using 引入了 System.Windows.Forms，
// 为避免与 System.Timers.Timer 产生 “Timer” 歧义（CS0104），这里把简单名 Timer 固定为 Timers 版本。
using Timer = System.Timers.Timer;

using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace desk;

/// <summary>
/// 后台进程查杀器。
/// 名单与查杀参数从「外置侧载」文件加载（程序目录下的 killlist.json 或 killlist.txt），
/// 默认每 5 秒扫描一次，对列表中的进程执行 <c>taskkill /F /IM</c> 强制结束。
/// 总开关（是否启用）由任务栏托盘右键菜单控制，并持久化在 desk 的设置里。
/// </summary>
internal static class ProcessKiller
{
	private static readonly string BaseDir = AppContext.BaseDirectory;

	private static readonly string JsonPath = Path.Combine (BaseDir, "killlist.json");

	private static readonly string TxtPath = Path.Combine (BaseDir, "killlist.txt");

	private static readonly Timer _timer = new Timer ();

	// 三个互相独立的启用来源：托盘总开关（killlist.json 名单）+
	// 画板设置面板的两个希沃细粒度开关（原版 Painter 的「自动查杀」功能）。
	// 任一开启即运行，且全程只有这一个定时器，避免两套 5s 轮询并存。
	private static bool _trayEnabled;
	private static bool _seewoPptService;
	private static bool _seewoEasiNote;
	private static bool _running;

	public static bool Enabled
	{
		get => _trayEnabled;
		set
		{
			_trayEnabled = value;
			RefreshRunning ();
		}
	}

	/// <summary>
	/// 由画板「自动」设置面板驱动：分别对应 sample 版的
	/// IsAutoKillPptService / IsAutoKillEasiNote 两个开关。
	/// </summary>
	public static void SetSeewoOptions (bool killPptService, bool killEasiNote)
	{
		_seewoPptService = killPptService;
		_seewoEasiNote = killEasiNote;
		RefreshRunning ();
	}

	/// <summary>本轮确实结束了进程时回调（线程池线程）。参数是被结束的进程名数组。</summary>
	public static Action<string[]>? Killed;

	private static void RefreshRunning ()
	{
		bool want = _trayEnabled || _seewoPptService || _seewoEasiNote;
		_running = want;
		if (want) {
			_timer.Start ();
			// 开关刚变动就立刻跑一轮（RunOnce 自带重入闸门，重复排队无害），
			// 用户点开关或开机启用都不用空等第一个 5 秒周期。
			_ = Task.Run ((Action)RunOnce);
		} else {
			_timer.Stop ();
		}
	}

	static ProcessKiller ()
	{
		_timer.AutoReset = true;
		_timer.Interval = 5000;
		_timer.Elapsed += OnTick;
		EnsureSideLoadFile ();
	}

	/// <summary>触达静态构造，确保示例侧载文件在首次运行时被生成。</summary>
	public static void Init ()
	{
	}

	private static void OnTick (object? sender, ElapsedEventArgs e) => RunOnce ();

	// Timer 线程与「开关刚启用时立即执行」的后台任务可能撞在同一轮，用闸门串行化。
	private static int _tickBusy;

	private static void RunOnce ()
	{
		if (Interlocked.Exchange (ref _tickBusy, 1) == 1) {
			return;
		}
		try {
			KillConfig cfg = LoadConfig ();
			if (cfg.IntervalSeconds > 0) {
				double ms = cfg.IntervalSeconds * 1000.0;
				if (Math.Abs (_timer.Interval - ms) > 1) {
					_timer.Interval = ms;
				}
			}
			RunKill (cfg);
		} catch {
			// 单轮失败不影响后续轮次
		} finally {
			Interlocked.Exchange (ref _tickBusy, 0);
		}
	}

	// 侧载文件是用户手改的静态配置，原先每 5 秒都重新读盘 + JSON 解析纯属浪费。
	// 用「文件路径 + 最后修改时间」做缓存，只有文件真的变了才重新解析。
	private static KillConfig? _cachedConfig;
	private static string _cachedPath = "";
	private static long _cachedStamp;

	private static KillConfig LoadConfig ()
	{
		try {
			string path = File.Exists (JsonPath) ? JsonPath : (File.Exists (TxtPath) ? TxtPath : "");
			long stamp = path.Length != 0 ? File.GetLastWriteTimeUtc (path).Ticks : 0;
			if (_cachedConfig != null && path == _cachedPath && stamp == _cachedStamp) {
				return _cachedConfig;
			}
			KillConfig cfg = ParseConfig (path);
			_cachedConfig = cfg;
			_cachedPath = path;
			_cachedStamp = stamp;
			return cfg;
		} catch {
			return _cachedConfig ?? new KillConfig ();
		}
	}

	private static KillConfig ParseConfig (string path)
	{
		if (path.Length == 0) {
			return new KillConfig ();
		}
		if (string.Equals (path, JsonPath, StringComparison.OrdinalIgnoreCase)) {
			try {
				using JsonDocument doc = JsonDocument.Parse (File.ReadAllText (path));
				JsonElement root = doc.RootElement;
				KillConfig cfg = new KillConfig ();
				if (root.TryGetProperty ("intervalSeconds", out JsonElement iv) && iv.ValueKind == JsonValueKind.Number) {
					cfg.IntervalSeconds = iv.GetInt32 ();
				}
				if (root.TryGetProperty ("processes", out JsonElement ps) && ps.ValueKind == JsonValueKind.Array) {
					foreach (JsonElement p in ps.EnumerateArray ()) {
						string? s = p.GetString ();
						if (!string.IsNullOrWhiteSpace (s)) {
							cfg.Processes.Add (s.Trim ());
						}
					}
				}
				if (root.TryGetProperty ("commands", out JsonElement cm) && cm.ValueKind == JsonValueKind.Array) {
					foreach (JsonElement c in cm.EnumerateArray ()) {
						string? s = c.GetString ();
						if (!string.IsNullOrWhiteSpace (s)) {
							cfg.Commands.Add (s.Trim ());
						}
					}
				}
				return cfg;
			} catch {
				// 解析失败落在下面的空配置，下一轮文件变更时会重试
			}
			return new KillConfig ();
		}
		try {
			KillConfig cfg = new KillConfig ();
			foreach (string raw in File.ReadAllLines (path)) {
				string line = raw.Trim ();
				if (line.Length == 0 || line.StartsWith ("#", StringComparison.Ordinal)) {
					continue;
				}
				// 以 / 开头视为原始 taskkill 参数，否则视为进程名
				if (line.StartsWith ("/", StringComparison.Ordinal)) {
					cfg.Commands.Add (line);
				} else {
					cfg.Processes.Add (line);
				}
			}
			return cfg;
		} catch {
			return new KillConfig ();
		}
	}

	// sample 原版「自动查杀」的两组名单：
	//  · PPT 小工具：检测到 PPTService 或 SeewoIwbAssistant 才触发，同组一并结束 Sia.Guard
	//  · 希沃白板 5：检测到 EasiNote 才触发
	private static readonly string[] SeewoPptGroup = { "PPTService", "SeewoIwbAssistant", "Sia.Guard" };
	private static readonly string[] SeewoEasiNoteGroup = { "EasiNote" };
	private static string _lastKilledKey = "";

	private static void RunKill (KillConfig cfg)
	{
		// 汇总本轮候选进程名（去重）。托盘名单只在托盘总开关开启时参与。
		HashSet<string> targets = new HashSet<string> (StringComparer.OrdinalIgnoreCase);
		if (_trayEnabled) {
			foreach (string p in cfg.Processes) {
				targets.Add (p);
			}
		}

		// 取一次全量进程快照：比逐个 GetProcessesByName 快得多（后者每次都会枚举全部进程）。
		HashSet<string> running = SnapshotRunningProcesses ();
		if (_seewoPptService && (running.Contains ("PPTService") || running.Contains ("SeewoIwbAssistant"))) {
			foreach (string p in SeewoPptGroup) {
				targets.Add (p);
			}
		}
		if (_seewoEasiNote && running.Contains ("EasiNote")) {
			foreach (string p in SeewoEasiNoteGroup) {
				targets.Add (p);
			}
		}

		// 只把「确实存在」的进程名放进 taskkill 命令。
		// 原实现每 5 秒无条件拉起一个 taskkill 进程，在非希沃机器上也照起不误，纯属浪费。
		List<string> hit = new List<string> ();
		foreach (string p in targets) {
			string bare = p.EndsWith (".exe", StringComparison.OrdinalIgnoreCase)
				? p.Substring (0, p.Length - 4) : p;
			if (bare.Length == 0) {
				continue;
			}
			// 绝不误杀自身（含改名前的旧版可执行文件名）
			if (bare.Equals ("painter", StringComparison.OrdinalIgnoreCase)
			    || bare.Equals ("InkCanvas", StringComparison.OrdinalIgnoreCase)
			    || bare.Equals ("Ink Canvas", StringComparison.OrdinalIgnoreCase)) {
				continue;
			}
			if (running.Contains (bare)) {
				hit.Add (bare);
			}
		}

		if (hit.Count != 0) {
			hit.Sort (StringComparer.OrdinalIgnoreCase);
			StringBuilder sb = new StringBuilder ("/F");
			foreach (string bare in hit) {
				sb.Append (" /IM ").Append (QuoteIfNeeded (bare + ".exe"));
			}
			RunTaskKill (sb.ToString ());

			// 名单未变化时只记一次日志 / 只回调一次：希沃进程被反复拉起时既不刷日志也不反复弹提示
			string key = string.Join (",", hit);
			if (key != _lastKilledKey) {
				_lastKilledKey = key;
				try { AppLog.Write ("查杀", "结束进程：" + key); } catch { }
				try { Killed?.Invoke (hit.ToArray ()); } catch { }
			}
		} else {
			_lastKilledKey = "";
		}

		// 用户自定义原始命令无法预检，仍按托盘开关控制
		if (_trayEnabled) {
			foreach (string c in cfg.Commands) {
				RunTaskKill (c);
			}
		}
	}

	private static HashSet<string> SnapshotRunningProcesses ()
	{
		HashSet<string> set = new HashSet<string> (StringComparer.OrdinalIgnoreCase);
		try {
			Process[] all = Process.GetProcesses ();
			foreach (Process p in all) {
				try { set.Add (p.ProcessName); } catch { }
				finally { try { p.Dispose (); } catch { } }
			}
		} catch {
		}
		return set;
	}

	private static string QuoteIfNeeded (string s)
	{
		return s.IndexOf (' ') >= 0 ? "\"" + s + "\"" : s;
	}

	private static void RunTaskKill (string args)
	{
		try {
			using Process p = new Process ();
			p.StartInfo = new ProcessStartInfo ("taskkill", args) {
				WindowStyle = ProcessWindowStyle.Hidden,
				CreateNoWindow = true,
				UseShellExecute = false
			};
			p.Start ();
		} catch {
		}
	}

	/// <summary>
	/// 若侧载文件均不存在，则在程序目录写入一个示例 killlist.json，
	/// 方便用户照着格式填写要查杀的进程。默认 enabled=false，
	/// 即便用户随后在托盘开启，示例里的进程名也只是原先的内置默认值。
	/// </summary>
	private static void EnsureSideLoadFile ()
	{
		try {
			if (!File.Exists (JsonPath) && !File.Exists (TxtPath)) {
				File.WriteAllText (JsonPath, SampleJson (), Encoding.UTF8);
			}
		} catch {
		}
	}

	private static string SampleJson ()
	{
		return "{\n" +
		       "  \"intervalSeconds\": 5,\n" +
		       "  \"processes\": [\n" +
		       "    \"EasiNote\",\n" +
		       "    \"PPTService\",\n" +
		       "    \"SeewoServiceAssistant\",\n" +
		       "    \"SeewoHugoLauncher\",\n" +
		       "    \"SeewoIwbAssistant\",\n" +
		       "    \"SeewoAbility\",\n" +
		       "    \"SeewoCore\",\n" +
		       "    \"Sia.Guard\"\n" +
		       "  ],\n" +
		       "  \"commands\": []\n" +
		       "}\n";
	}

	private sealed class KillConfig
	{
		public int IntervalSeconds = 5;

		public List<string> Processes = new List<string> ();

		public List<string> Commands = new List<string> ();
	}
}
