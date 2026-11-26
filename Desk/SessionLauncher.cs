using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace desk;

internal static class SessionLauncher
{
	private static readonly Dictionary<uint, int> _hostPids = new Dictionary<uint, int> ();

	public static void EnsureHostRunning ()
	{
		uint num = Native.WTSGetActiveConsoleSessionId ();
		if (num != uint.MaxValue && num != 0 && (!_hostPids.TryGetValue (num, out var value) || !IsAlive (value))) {
			int num2 = LaunchInSession (num);
			if (num2 > 0) {
				_hostPids [num] = num2;
			}
		}
	}

	public static void StopAll ()
	{
		foreach (KeyValuePair<uint, int> hostPid in _hostPids) {
			try {
				Process processById = Process.GetProcessById (hostPid.Value);
				processById.Kill ();
				processById.WaitForExit (3000);
			} catch {
			}
		}
		_hostPids.Clear ();
	}

	private static bool IsAlive (int pid)
	{
		try {
			return !Process.GetProcessById (pid).HasExited;
		} catch {
			return false;
		}
	}

	private static int LaunchInSession (uint session)
	{
		string text = NetFx.ProcessPath ?? "";
		if (string.IsNullOrEmpty (text)) {
			return 0;
		}
		uint num = FindProcessInSession ("winlogon.exe", session);
		if (num == 0) {
			return 0;
		}
		nint num2 = Native.OpenProcess (4096u, bInheritHandle: false, num);
		if (num2 == IntPtr.Zero) {
			return 0;
		}
		try {
			if (!Native.OpenProcessToken (num2, 2u, out var TokenHandle) || TokenHandle == IntPtr.Zero) {
				return 0;
			}
			try {
				if (!Native.DuplicateTokenEx (TokenHandle, 983551u, IntPtr.Zero, 2, 1, out var phNewToken) || phNewToken == IntPtr.Zero) {
					return 0;
				}
				try {
					Native.STARTUPINFO lpStartupInfo = new Native.STARTUPINFO {
						cb = Marshal.SizeOf<Native.STARTUPINFO> (),
						lpDesktop = "WinSta0\\Default"
					};
					string lpCommandLine = "\"" + text + "\" -host";
					if (!Native.CreateProcessAsUser (phNewToken, null, lpCommandLine, IntPtr.Zero, IntPtr.Zero, bInheritHandles: false, 134217728u, IntPtr.Zero, null, ref lpStartupInfo, out var lpProcessInformation)) {
						return 0;
					}
					Native.CloseHandle (lpProcessInformation.hThread);
					Native.CloseHandle (lpProcessInformation.hProcess);
					return (int)lpProcessInformation.dwProcessId;
				} finally {
					Native.CloseHandle (phNewToken);
				}
			} finally {
				Native.CloseHandle (TokenHandle);
			}
		} finally {
			Native.CloseHandle (num2);
		}
	}

	private static uint FindProcessInSession (string exeName, uint session)
	{
		nint num = Native.CreateToolhelp32Snapshot (2u, 0u);
		if (num == IntPtr.Zero || num == new IntPtr (-1)) {
			return 0u;
		}
		try {
			Native.PROCESSENTRY32 lppe = new Native.PROCESSENTRY32 {
				dwSize = (uint)Marshal.SizeOf<Native.PROCESSENTRY32> ()
			};
			if (!Native.Process32First (num, ref lppe)) {
				return 0u;
			}
			do {
				if (string.Equals (lppe.szExeFile, exeName, StringComparison.OrdinalIgnoreCase) && Native.ProcessIdToSessionId (lppe.th32ProcessID, out var pSessionId) && pSessionId == session) {
					return lppe.th32ProcessID;
				}
			} while (Native.Process32Next (num, ref lppe));
			return 0u;
		} finally {
			Native.CloseHandle (num);
		}
	}
}
