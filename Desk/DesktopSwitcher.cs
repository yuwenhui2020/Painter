using System;
using System.Runtime.InteropServices;

using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace desk;

internal static class DesktopSwitcher
{
	private const uint GENERIC_ALL = 268435456u;

	private static nint _lastDesktop = IntPtr.Zero;

	private static long _lastCheckTick;

	private const int CheckIntervalMs = 1000;

	[DllImport ("user32.dll")]
	private static extern nint OpenInputDesktop (uint dwFlags, bool fInherit, uint dwDesiredAccess);

	[DllImport ("user32.dll")]
	private static extern bool CloseDesktop (nint hDesktop);

	[DllImport ("user32.dll")]
	private static extern bool SetThreadDesktop (nint hDesktop);

	[DllImport ("user32.dll")]
	private static extern nint GetThreadDesktop (uint dwThreadId);

	[DllImport ("kernel32.dll")]
	private static extern uint GetCurrentThreadId ();

	public static void SyncToInputDesktop ()
	{
		long tickCount = NetFx.TickCount64;
		if (tickCount - _lastCheckTick < 1000) {
			return;
		}
		_lastCheckTick = tickCount;
		nint num = OpenInputDesktop (0u, fInherit: false, 268435456u);
		if (num == IntPtr.Zero) {
			if (_lastDesktop != IntPtr.Zero) {
				_lastDesktop = IntPtr.Zero;
				AppLog.Write ("UAC", "OpenInputDesktop 失败，无法跟随安全桌面（需 SYSTEM 权限）");
			}
			return;
		}
		try {
			nint threadDesktop = GetThreadDesktop (GetCurrentThreadId ());
			if (threadDesktop != num) {
				bool flag = SetThreadDesktop (num);
				AppLog.Write ("UAC", flag ? "抓屏线程已切换到当前输入桌面（UAC 出现时为安全桌面）" : "SetThreadDesktop 失败，无法抓到 UAC 界面");
				if (!flag) {
					return;
				}
			}
			if (_lastDesktop != num) {
				_lastDesktop = num;
				AppLog.Write ("UAC", "输入桌面发生变化，抓屏目标已跟随");
			}
		} finally {
			CloseDesktop (num);
		}
	}
}
