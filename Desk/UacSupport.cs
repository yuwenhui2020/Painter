using System;
using System.Diagnostics;

using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace desk;

internal static class UacSupport
{
	// 一次启动里 UacSupport.IsInstalled() 会被调用约 4 次，每次都 Process.Start("sc.exe")+WaitForExit
	// （约 20-50ms/次）。服务安装状态在一次会话内几乎不变，缓存数秒即可省掉 3 次子进程创建。
	private static readonly Dictionary<string, (int code, string output, long at)> ScCache = new ();
	private static readonly object ScCacheLock = new ();

	/// <summary>是否使用短缓存。安装/卸载/停止/删除等会改变服务状态的命令必须传 false，
	/// 且其后续的状态查询轮询也要绕过缓存，否则会一直读到旧 RUNNING。</summary>
	private static (int code, string output) RunSc (string args, bool useCache = true)
	{
		long now = Environment.TickCount;
		if (useCache) {
			lock (ScCacheLock) {
				if (ScCache.TryGetValue (args, out var hit) && (uint)(now - hit.at) < 5000u)
					return (hit.code, hit.output);
			}
		}

		var result = RunScUncached (args);

		lock (ScCacheLock) {
			if (useCache) {
				ScCache [args] = (result.code, result.output, now);
				if (ScCache.Count > 16) ScCache.Clear ();
			} else {
				// 状态已改变：清掉该服务的所有 query 缓存，让下次 IsInstalled/IsRunning 读真实状态
				foreach (string key in ScCache.Keys.ToArray ())
					if (key.StartsWith ("query ", StringComparison.Ordinal)) ScCache.Remove (key);
			}
		}
		return result;
	}

	private static (int code, string output) RunScUncached (string args)
	{
		try {
			ProcessStartInfo startInfo = new ProcessStartInfo ("sc.exe", args) {
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			using Process process = Process.Start (startInfo);
			if (process == null) {
				return (code: -1, output: "无法启动 sc.exe");
			}
			string item = process.StandardOutput.ReadToEnd () + process.StandardError.ReadToEnd ();
			process.WaitForExit (15000);
			return (code: process.ExitCode, output: item);
		} catch (Exception ex) {
			return (code: -1, output: ex.Message);
		}
	}

	public static bool IsInstalled ()
	{
		return RunSc ($"query {AppIdentity.ServiceName}").code == 0;
	}

	public static bool IsRunning ()
	{
		return RunSc ($"query {AppIdentity.ServiceName}").output.Contains ("RUNNING");
	}

	/// <summary>删除改名前的服务（desk_uac 与 InkCanvasUac）。它们与新服务指向同一个 exe，
	/// 不删就会留下无人管理的自启动项。sc 在中文系统上输出的也是 RUNNING/STOPPED 这类
	/// 英文状态，所以判断存在与否只看退出码，判断是否真停下来了才去看文本。</summary>
	private static void RemoveLegacyService ()
	{
		RemoveService (AppIdentity.LegacyServiceName);
		RemoveService (AppIdentity.LegacyServiceNameInkCanvas);
	}

	private static void RemoveService (string legacy)
	{
		if (RunSc ($"query {legacy}").code != 0) {
			return;
		}
		if (RunSc ($"query {legacy}", useCache: false).output.Contains ("RUNNING")) {
			RunSc ($"stop {legacy}", useCache: false);
			for (int i = 0; i < 20; i++) {
				if (RunSc ($"query {legacy}", useCache: false).output.Contains ("STOPPED")) {
					break;
				}
				Thread.Sleep (250);
			}
		}
		RunSc ($"delete {legacy}", useCache: false);
	}

	public static (bool ok, string msg) Install ()
	{
		string value = NetFx.ProcessPath ?? "";
		if (string.IsNullOrEmpty (value)) {
			return (ok: false, msg: "无法获取程序路径");
		}
		RemoveLegacyService ();
		string name = AppIdentity.ServiceName;
		(int, string) tuple = RunSc ($"create {name} binPath= \"\\\"{value}\\\" -service\" start= auto obj= LocalSystem", useCache: false);
		if (tuple.Item1 != 0) {
			return (ok: false, msg: tuple.Item2);
		}
		RunSc ($"description {name} \"以 SYSTEM 权限在用户会话内运行远程桌面被控端，用于显示并控制 UAC 安全桌面\"");
		(int, string) tuple2 = RunSc ($"start {name}", useCache: false);
		return (ok: true, msg: (tuple2.Item1 == 0) ? "已安装并启动。" : ("已安装，但启动失败：" + tuple2.Item2));
	}

	public static (bool ok, string msg) Uninstall ()
	{
		string name = AppIdentity.ServiceName;
		RunSc ($"stop {name}", useCache: false);
		(int, string) tuple = RunSc ($"delete {name}", useCache: false);
		RemoveLegacyService ();
		return (ok: tuple.Item1 == 0, msg: (tuple.Item1 == 0) ? "已卸载。" : tuple.Item2);
	}
}
