using System;
using System.Diagnostics;
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

internal sealed class LowLevelInputHook : IDisposable
{
	private delegate nint HookProc (int nCode, nint wParam, nint lParam);

	private struct POINT
	{
		public int x;

		public int y;
	}

	private struct KBDLLHOOKSTRUCT
	{
		public uint vkCode;

		public uint scanCode;

		public uint flags;

		public uint time;

		public nint dwExtraInfo;
	}

	private struct MSLLHOOKSTRUCT
	{
		public POINT pt;

		public uint mouseData;

		public uint flags;

		public uint time;

		public nint dwExtraInfo;
	}

	private const int WH_KEYBOARD_LL = 13;

	private const int WH_MOUSE_LL = 14;

	private const int LLKHF_INJECTED = 16;

	private const int LLKHF_UP = 128;

	private const int LLMHF_INJECTED = 1;

	private readonly HookProc _keyboardProc;

	private readonly HookProc _mouseProc;

	private nint _keyboardHook;

	private nint _mouseHook;

	public Func<bool>? IsActive { get; set; }

	public Func<int, bool, bool>? KeyFilter { get; set; }

	public event Action<int, int>? MouseMove;

	public event Action<byte, int, int>? MouseDown;

	public event Action<byte, int, int>? MouseUp;

	public event Action<short, int, int>? MouseWheel;

	public event Action<int, bool>? Key;

	[DllImport ("user32.dll", SetLastError = true)]
	private static extern nint SetWindowsHookEx (int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

	[DllImport ("user32.dll", SetLastError = true)]
	private static extern bool UnhookWindowsHookEx (nint hhk);

	[DllImport ("user32.dll")]
	private static extern nint CallNextHookEx (nint hhk, int nCode, nint wParam, nint lParam);

	[DllImport ("user32.dll")]
	private static extern nint GetForegroundWindow ();

	[DllImport ("kernel32.dll")]
	private static extern nint GetModuleHandle (string lpModuleName);

	public LowLevelInputHook ()
	{
		_keyboardProc = KeyboardHook;
		_mouseProc = MouseHook;
		nint moduleHandle = GetModuleHandle (Process.GetCurrentProcess ().MainModule.ModuleName);
		_keyboardHook = SetWindowsHookEx (13, _keyboardProc, moduleHandle, 0u);
		_mouseHook = SetWindowsHookEx (14, _mouseProc, moduleHandle, 0u);
		if (_keyboardHook == 0 || _mouseHook == 0) {
			// 钩子安装失败必须留痕：否则界面显示"控制中"但键鼠转发完全失效且无任何提示
			AppLog.Write ("Hook", $"低级输入钩子安装失败（键盘={_keyboardHook}, 鼠标={_mouseHook}）");
		}
	}

	private bool Active ()
	{
		return IsActive == null || IsActive ();
	}

	private nint KeyboardHook (int nCode, nint wParam, nint lParam)
	{
		// 钩子回调运行在非托管 → 托管的边界上：任何逃逸到非托管代码的异常
		// 都会让 CLR 直接 failfast 终止进程（全局异常处理器也救不了）。
		// 因此整段兜底，出错也只吞掉并放行消息，绝不向上抛。
		try {
			if (nCode >= 0) {
				KBDLLHOOKSTRUCT kBDLLHOOKSTRUCT = Marshal.PtrToStructure<KBDLLHOOKSTRUCT> (lParam);
				if ((kBDLLHOOKSTRUCT.flags & 0x10) == 0) {
					bool arg = (kBDLLHOOKSTRUCT.flags & 0x80) == 0;
					if (KeyFilter != null && KeyFilter ((int)kBDLLHOOKSTRUCT.vkCode, arg)) {
						return 1;
					}
					if (Active ()) {
						this.Key?.Invoke ((int)kBDLLHOOKSTRUCT.vkCode, arg);
					}
				}
			}
		} catch { /* 钩子回调绝不允许异常逃逸 */ }
		return CallNextHookEx (_keyboardHook, nCode, wParam, lParam);
	}

	private nint MouseHook (int nCode, nint wParam, nint lParam)
	{
		try {
		if (nCode >= 0) {
			MSLLHOOKSTRUCT mSLLHOOKSTRUCT = Marshal.PtrToStructure<MSLLHOOKSTRUCT> (lParam);
			if ((mSLLHOOKSTRUCT.flags & 1) == 0 && Active ()) {
				int x = mSLLHOOKSTRUCT.pt.x;
				int y = mSLLHOOKSTRUCT.pt.y;
				switch ((uint)wParam) {
				case 512u:
					this.MouseMove?.Invoke (x, y);
					break;
				case 513u:
					this.MouseDown?.Invoke (1, x, y);
					break;
				case 514u:
					this.MouseUp?.Invoke (1, x, y);
					break;
				case 516u:
					this.MouseDown?.Invoke (2, x, y);
					break;
				case 517u:
					this.MouseUp?.Invoke (2, x, y);
					break;
				case 519u:
					this.MouseDown?.Invoke (3, x, y);
					break;
				case 520u:
					this.MouseUp?.Invoke (3, x, y);
					break;
				case 522u: {
					short arg = (short)((mSLLHOOKSTRUCT.mouseData >> 16) & 0xFFFF);
					this.MouseWheel?.Invoke (arg, x, y);
					break;
				}
				}
			}
		}
		} catch { /* 钩子回调绝不允许异常逃逸 */ }
		return CallNextHookEx (_mouseHook, nCode, wParam, lParam);
	}

	public void Dispose ()
	{
		if (_keyboardHook != IntPtr.Zero) {
			UnhookWindowsHookEx (_keyboardHook);
			_keyboardHook = IntPtr.Zero;
		}
		if (_mouseHook != IntPtr.Zero) {
			UnhookWindowsHookEx (_mouseHook);
			_mouseHook = IntPtr.Zero;
		}
	}
}
