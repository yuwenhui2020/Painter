using System.Diagnostics;
using System.Reflection;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace desk;

/// <summary>
/// .NET Framework 4.6.2 兼容层：集中提供原 net8.0 项目直接使用、但 net462 BCL 中不存在的 API。
/// 迁移到 net462 时，把这些调用点统一替换为 <see cref="NetFx"/> 的成员即可。
/// </summary>
internal static class NetFx
{
	/// <summary>
	/// 64 位毫秒计时。<see cref="Environment.TickCount64"/> 在 .NET Core 3.0+ 才提供，
	/// net462 只有会 49.7 天回绕的 <see cref="Environment.TickCount"/>（int）。
	/// 用单调递增的 <see cref="Stopwatch"/> 基准换算出不回绕的 long 毫秒。
	/// </summary>
	private static readonly Stopwatch Clock = Stopwatch.StartNew ();

	private static readonly string ExecutablePath = ResolveProcessPath ();

	/// <summary>等价的 <c>Environment.TickCount64</c>：开机以来的毫秒数（long，不回绕）。</summary>
	public static long TickCount64 => Clock.ElapsedMilliseconds;

	/// <summary>等价的 <c>Environment.ProcessPath</c>（.NET Core 2.1+），net462 需从进程模块取。</summary>
	public static string ProcessPath => ExecutablePath;

	/// <summary>等价的 <c>Math.Clamp(value, min, max)</c>（.NET Core 2.0+）。</summary>
	public static int Clamp (int value, int min, int max)
	{
		if (value < min) {
			return min;
		}
		return value > max ? max : value;
	}

	/// <summary>等价的 <c>Math.Clamp(value, min, max)</c>（.NET Core 2.0+）。</summary>
	public static long Clamp (long value, long min, long max)
	{
		if (value < min) {
			return min;
		}
		return value > max ? max : value;
	}

	/// <summary>等价的 <c>Math.Clamp(value, min, max)</c>（.NET Core 2.0+）。</summary>
	public static float Clamp (float value, float min, float max)
	{
		if (value < min) {
			return min;
		}
		return value > max ? max : value;
	}

	/// <summary>等价的 <c>Math.Clamp(value, min, max)</c>（.NET Core 2.0+）。</summary>
	public static double Clamp (double value, double min, double max)
	{
		if (value < min) {
			return min;
		}
		return value > max ? max : value;
	}

	/// <summary>等价的 <c>Path.GetRelativePath(basePath, fullPath)</c>（.NET Core 2.0+），net462 无此 API。</summary>
	public static string GetRelativePath (string basePath, string fullPath)
	{
		if (string.IsNullOrEmpty (basePath)) {
			return fullPath;
		}
		string normalizedBase = Path.GetFullPath (basePath).TrimEnd (Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string normalizedFull = Path.GetFullPath (fullPath);
		if (normalizedFull.Length <= normalizedBase.Length) {
			return fullPath;
		}
		if (normalizedFull [normalizedBase.Length] != Path.DirectorySeparatorChar
			&& normalizedFull [normalizedBase.Length] != Path.AltDirectorySeparatorChar) {
			return fullPath;
		}
		return normalizedFull.Substring (normalizedBase.Length + 1);
	}

	private static string ResolveProcessPath ()
	{
		try {
			using Process currentProcess = Process.GetCurrentProcess ();
			string fileName = currentProcess.MainModule?.FileName;
			if (!string.IsNullOrEmpty (fileName)) {
				return fileName;
			}
		} catch {
		}
		try {
			string location = Assembly.GetEntryAssembly ()?.Location;
			if (!string.IsNullOrEmpty (location)) {
				return location;
			}
		} catch {
		}
		try {
			return Process.GetCurrentProcess ().ProcessName + ".exe";
		} catch {
			return string.Empty;
		}
	}
}
