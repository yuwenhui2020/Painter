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

internal static class InputInjector
{
	private struct MOUSEINPUT
	{
		public int dx;

		public int dy;

		public uint mouseData;

		public uint dwFlags;

		public uint time;

		public nint extraInfo;
	}

	private struct KEYBDINPUT
	{
		public ushort wVk;

		public ushort wScan;

		public uint dwFlags;

		public uint time;

		public nint extraInfo;
	}

	[StructLayout (LayoutKind.Explicit)]
	private struct INPUTUNION
	{
		[FieldOffset (0)]
		public MOUSEINPUT mi;

		[FieldOffset (0)]
		public KEYBDINPUT ki;
	}

	private struct INPUT
	{
		public int type;

		public INPUTUNION u;
	}

	private const int INPUT_MOUSE = 0;

	private const int INPUT_KEYBOARD = 1;

	private const uint MOUSEEVENTF_MOVE = 1u;

	private const uint MOUSEEVENTF_LEFTDOWN = 2u;

	private const uint MOUSEEVENTF_LEFTUP = 4u;

	private const uint MOUSEEVENTF_RIGHTDOWN = 8u;

	private const uint MOUSEEVENTF_RIGHTUP = 16u;

	private const uint MOUSEEVENTF_MIDDLEDOWN = 32u;

	private const uint MOUSEEVENTF_MIDDLEUP = 64u;

	private const uint MOUSEEVENTF_WHEEL = 2048u;

	private const uint MOUSEEVENTF_ABSOLUTE = 32768u;

	private const uint MOUSEEVENTF_VIRTUALDESK = 16384u;

	private const uint KEYEVENTF_EXTENDEDKEY = 1u;

	private const uint KEYEVENTF_KEYUP = 2u;

	// KEYEVENTF_UNICODE：wVk=0、wScan=UTF-16 码元，SendInput 会把它当作字符输入
	// （走 WM_CHAR），不受键盘布局/输入法影响，是浏览器远控输入中文的正确通道。
	private const uint KEYEVENTF_UNICODE = 4u;

	private const int SM_XVIRTUALSCREEN = 76;

	private const int SM_YVIRTUALSCREEN = 77;

	private const int SM_CXVIRTUALSCREEN = 78;

	private const int SM_CYVIRTUALSCREEN = 79;

	private static double _lastPx;

	private static double _lastPy;

	private static long _lastFailLog;

	private const ushort VK_CAPITAL = 20;

	private const ushort VK_NUMLOCK = 144;

	private const ushort VK_SCROLL = 145;

	public static Action<string>? Log { get; set; }

	[DllImport ("user32.dll", SetLastError = true)]
	private static extern uint SendInput (uint nInputs, INPUT[] pInputs, int cbSize);

	[DllImport ("user32.dll")]
	private static extern int GetSystemMetrics (int nIndex);

	private static (int left, int top, int w, int h) VirtualDesktop ()
	{
		int num = GetSystemMetrics (78);
		int num2 = GetSystemMetrics (79);
		if (num <= 0) {
			num = 1;
		}
		if (num2 <= 0) {
			num2 = 1;
		}
		return (left: GetSystemMetrics (76), top: GetSystemMetrics (77), w: num, h: num2);
	}

	private static void CheckResult (uint inserted, string what)
	{
		if (inserted == 0) {
			long tickCount = NetFx.TickCount64;
			if (tickCount - _lastFailLog >= 5000) {
				_lastFailLog = tickCount;
				Log?.Invoke ("输入注入被系统拒绝（" + what + "）。通常是权限不足：请以管理员身份运行本程序，否则无法控制权限更高的程序窗口（UIPI 限制）。");
			}
		}
	}

	public static void MoveAbsolute (ushort xNorm, ushort yNorm)
	{
		(int left, int top, int w, int h) tuple = VirtualDesktop ();
		int item = tuple.left;
		int item2 = tuple.top;
		int item3 = tuple.w;
		int item4 = tuple.h;
		double num = (double)item + (double)(int)xNorm / 65535.0 * (double)item3;
		double num2 = (double)item2 + (double)(int)yNorm / 65535.0 * (double)item4;
		_lastPx = num;
		_lastPy = num2;
		int dx = ClampNorm ((num - (double)item) / (double)item3 * 65535.0);
		int dy = ClampNorm ((num2 - (double)item2) / (double)item4 * 65535.0);
		INPUT iNPUT = new INPUT {
			type = 0,
			u = new INPUTUNION {
				mi = new MOUSEINPUT {
					dx = dx,
					dy = dy,
					dwFlags = 49153u
				}
			}
		};
		CheckResult (SendInput (1u, new INPUT[1] { iNPUT }, Marshal.SizeOf<INPUT> ()), "鼠标移动");
	}

	private static int ClampNorm (double v)
	{
		int num = (int)Math.Round (v);
		return (num >= 0) ? ((num > 65535) ? 65535 : num) : 0;
	}

	private static (int dx, int dy) NormFromPixel ()
	{
		var (num, num2, num3, num4) = VirtualDesktop ();
		return (dx: ClampNorm ((_lastPx - (double)num) / (double)num3 * 65535.0), dy: ClampNorm ((_lastPy - (double)num2) / (double)num4 * 65535.0));
	}

	public static void MoveRelative (int dx, int dy)
	{
		dx = NetFx.Clamp (dx, -10000, 10000);
		dy = NetFx.Clamp (dy, -10000, 10000);
		INPUT iNPUT = new INPUT {
			type = 0,
			u = new INPUTUNION {
				mi = new MOUSEINPUT {
					dx = dx,
					dy = dy,
					dwFlags = 1u
				}
			}
		};
		CheckResult (SendInput (1u, new INPUT[1] { iNPUT }, Marshal.SizeOf<INPUT> ()), "鼠标相对移动");
	}

	public static void ButtonDown (byte button)
	{
		SendMouseButton (button, down: true);
	}

	public static void ButtonUp (byte button)
	{
		SendMouseButton (button, down: false);
	}

	private static void SendMouseButton (byte button, bool down)
	{
		uint num = button switch {
			1 => down ? 2u : 4u, 
			2 => down ? 8u : 16u, 
			3 => down ? 32u : 64u, 
			_ => 0u, 
		};
		if (num != 0) {
			(int dx, int dy) tuple = NormFromPixel ();
			int item = tuple.dx;
			int item2 = tuple.dy;
			INPUT iNPUT = new INPUT {
				type = 0,
				u = new INPUTUNION {
					mi = new MOUSEINPUT {
						dx = item,
						dy = item2,
						dwFlags = (num | 0x8000 | 0x4000)
					}
				}
			};
			CheckResult (SendInput (1u, new INPUT[1] { iNPUT }, Marshal.SizeOf<INPUT> ()), "鼠标按键");
		}
	}

	public static void Wheel (short delta)
	{
		(int dx, int dy) tuple = NormFromPixel ();
		int item = tuple.dx;
		int item2 = tuple.dy;
		INPUT iNPUT = new INPUT {
			type = 0,
			u = new INPUTUNION {
				mi = new MOUSEINPUT {
					dx = item,
					dy = item2,
					mouseData = (uint)delta,
					dwFlags = 51200u
				}
			}
		};
		CheckResult (SendInput (1u, new INPUT[1] { iNPUT }, Marshal.SizeOf<INPUT> ()), "鼠标滚轮");
	}

	[DllImport ("user32.dll")]
	private static extern short GetKeyState (int nVirtKey);

	public static bool GetLockState (ushort vk)
	{
		return (GetKeyState (vk) & 1) != 0;
	}

	private static bool IsExtendedKey (ushort vk)
	{
		switch (vk) {
		case 162:
		case 163:
		case 165:
			return true;
		case 33:
		case 34:
		case 35:
		case 36:
			return true;
		case 37:
		case 38:
		case 39:
		case 40:
			return true;
		case 45:
		case 46:
			return true;
		case 91:
		case 92:
		case 93:
			return true;
		case 144:
			return true;
		case 19:
			return true;
		default:
			return false;
		}
	}

	public static void Key (ushort vk, bool down)
	{
		INPUT iNPUT = new INPUT {
			type = 1,
			u = new INPUTUNION {
				ki = new KEYBDINPUT {
					wVk = vk,
					wScan = 0,
					dwFlags = (uint)((IsExtendedKey (vk) ? 1 : 0) | ((!down) ? 2 : 0))
				}
			}
		};
		CheckResult (SendInput (1u, new INPUT[1] { iNPUT }, Marshal.SizeOf<INPUT> ()), "键盘");
	}

	/// <summary>
	/// 以 Unicode 码元注入文本（浏览器远控输入中文 / 表情 / IME 结果的正确通道）。
	/// 每个 UTF-16 码元发一对 keydown+keyup（KEYEVENTF_UNICODE），一次 SendInput 提交，
	/// 目标是前台窗口的插入点。代理对（emoji）由两个码元自然组成，无需特殊处理。
	/// </summary>
	public static void Unicode (string text)
	{
		if (string.IsNullOrEmpty (text)) return;
		// 每个码元 2 个 INPUT（down + up）；限制长度防止超长文本一次提交耗尽系统队列。
		int count = text.Length > 512 ? 512 : text.Length;
		INPUT[] inputs = new INPUT[count * 2];
		for (int i = 0; i < count; i++) {
			inputs [i * 2] = new INPUT {
				type = 1,
				u = new INPUTUNION {
					ki = new KEYBDINPUT { wVk = 0, wScan = text [i], dwFlags = KEYEVENTF_UNICODE }
				}
			};
			inputs [i * 2 + 1] = new INPUT {
				type = 1,
				u = new INPUTUNION {
					ki = new KEYBDINPUT { wVk = 0, wScan = text [i], dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP }
				}
			};
		}
		CheckResult (SendInput ((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT> ()), "Unicode 文本");
	}

	private static void TapKey (ushort vk)
	{
		Key (vk, down: true);
		Key (vk, down: false);
	}

	public static void KeyWithLockSync (int vk, bool down, bool caps, bool num, bool scroll)
	{
		bool flag = GetLockState (20) != caps;
		bool flag2 = GetLockState (144) != num;
		bool flag3 = GetLockState (145) != scroll;
		if (flag) {
			TapKey (20);
		}
		if (flag2) {
			TapKey (144);
		}
		if (flag3) {
			TapKey (145);
		}
		Key ((ushort)vk, down);
		if (flag) {
			TapKey (20);
		}
		if (flag2) {
			TapKey (144);
		}
		if (flag3) {
			TapKey (145);
		}
	}
}
