using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace desk;

internal static class Features
{
	private sealed class BlackScreenForm : Form
	{
		private const uint WS_EX_TRANSPARENT = 32u;

		private const uint WS_EX_LAYERED = 524288u;

		private const uint WS_EX_NOACTIVATE = 134217728u;

		private const uint LWA_ALPHA = 2u;

		protected override CreateParams CreateParams {
			get {
				CreateParams createParams = base.CreateParams;
				createParams.ExStyle |= 134742048;
				return createParams;
			}
		}

		[DllImport ("user32.dll")]
		private static extern bool SetLayeredWindowAttributes (nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

		public BlackScreenForm (Rectangle bounds)
		{
			base.FormBorderStyle = FormBorderStyle.None;
			base.StartPosition = FormStartPosition.Manual;
			base.Bounds = bounds;
			BackColor = Color.Black;
			base.TopMost = true;
			base.ShowInTaskbar = false;
		}

		protected override void OnHandleCreated (EventArgs e)
		{
			base.OnHandleCreated (e);
			try {
				SetLayeredWindowAttributes (base.Handle, 0u, byte.MaxValue, 2u);
			} catch {
			}
			try {
				// WDA_EXCLUDEFROMCAPTURE(17) 只有 Win10 2004(build 19041)+ 支持；
				// Win7/8 只认 WDA_MONITOR(1)。按版本选值，避免 Win7 上整个调用被系统
				// 拒绝、隐私黑窗连普通截屏都挡不住。
				uint affinity = (Painter.Helpers.OsSupport.IsWindows10OrGreater
					&& Painter.Helpers.OsSupport.Version.Build >= 19041)
					? WDA_EXCLUDEFROMCAPTURE : WDA_MONITOR;
				SetWindowDisplayAffinity (base.Handle, affinity);
			} catch {
			}
		}

		protected override void WndProc (ref Message m)
		{
			if (m.Msg < 512 || m.Msg > 525) {
				base.WndProc (ref m);
			}
		}
	}

	private struct ICONINFO
	{
		public bool fIcon;

		public int xHotspot;

		public int yHotspot;

		public nint hbmMask;

		public nint hbmColor;
	}

	private struct CURSORINFO
	{
		public int cbSize;

		public int flags;

		public nint hCursor;

		public POINTAPI ptScreenPos;
	}

	private struct POINTAPI
	{
		public int x;

		public int y;
	}

	// 权限位由网络线程 Set、抓屏/UI 线程读，用 volatile + Interlocked 保证可见与原子；
	// 黑窗列表可能被并发开关，统一用锁保护增删枚举，避免"集合被修改"异常。
	private static int _allowed = (int)~Protocol.Permission.Recording;
	private static readonly object _permLock = new object ();

	private static readonly List<Form> _blackWindows = new List<Form> ();
	private static readonly object _blackLock = new object ();

	// 隐私黑窗必须在拥有消息泵的 desk UI 线程上创建/关闭：SetPrivacyMode 的调用方
	// 是网络接收任务，直接 Show()/Close() 会形成跨线程控件访问，异常又被吞掉导致黑窗残留。
	private static Form? _uiAnchor;
	private static readonly object _uiAnchorLock = new object ();

	/// <summary>由 desk 模块的 Form1 在创建后注册，用于把 WinForms 操作切回 UI 线程。</summary>
	public static void RegisterUiAnchor (Form anchor)
	{
		lock (_uiAnchorLock) _uiAnchor = anchor;
	}

	/// <summary>在 desk UI 线程上执行；未注册锚点（早期启动）时退化为直接执行。</summary>
	private static void OnUi (Action action)
	{
		Form? anchor;
		lock (_uiAnchorLock) anchor = _uiAnchor;
		if (anchor != null && !anchor.IsDisposed && anchor.IsHandleCreated && !anchor.InvokeRequired) {
			action ();
			return;
		}
		if (anchor != null && !anchor.IsDisposed && anchor.IsHandleCreated) {
			try { anchor.BeginInvoke (action); return; } catch { }
		}
		action ();
	}

	private static volatile bool _privacyOn;

	private const uint WDA_EXCLUDEFROMCAPTURE = 17u;

	private const uint WDA_MONITOR = 1u;

	private const int CURSOR_SHOWING = 1;

	private const uint DI_NORMAL = 3u;

	public static Protocol.Permission Allowed {
		get {
			return (Protocol.Permission)Volatile.Read (ref _allowed);
		}
		set {
			Volatile.Write (ref _allowed, (int)value);
		}
	}

	public static bool PrivacyOn => _privacyOn;

	public static bool IsAllowed (Protocol.Permission p)
	{
		int allowed = Volatile.Read (ref _allowed);
		return (allowed & (int)p) != 0;
	}

	public static void Set (Protocol.Permission p, bool on)
	{
		// 读改写用 CAS 循环保证原子，避免并发 Set 丢失更新。
		int bit = (int)p;
		while (true) {
			int cur = Volatile.Read (ref _allowed);
			int next = on ? (cur | bit) : (cur & ~bit);
			if (Interlocked.CompareExchange (ref _allowed, next, cur) == cur) return;
		}
	}

	[DllImport ("user32.dll")]
	private static extern bool LockWorkStation ();

	[DllImport ("user32.dll")]
	private static extern bool BlockInput (bool fBlockIt);

	public static bool DoLockScreen ()
	{
		return LockWorkStation ();
	}

	public static bool DoBlockInput (bool on)
	{
		return BlockInput (on);
	}

	public static bool DoCtrlAltDel ()
	{
		try {
			if (UacSupport.IsInstalled () && UacBridge.TrySendSas ()) {
				return true;
			}
			return NativeSendSas ();
		} catch {
			return false;
		}
	}

	[DllImport ("sas.dll", EntryPoint = "SendSAS")]
	[return: MarshalAs (UnmanagedType.Bool)]
	private static extern bool NativeSendSasImpl ();

	private static bool NativeSendSas ()
	{
		try {
			return NativeSendSasImpl ();
		} catch (DllNotFoundException) {
			return false;
		} catch (EntryPointNotFoundException) {
			return false;
		}
	}

	public static bool DoRestart ()
	{
		try {
			ProcessStartInfo startInfo = new ProcessStartInfo ("shutdown.exe", "/r /t 2 /c \"远程桌面发起的重启\"") {
				UseShellExecute = false,
				CreateNoWindow = true
			};
			Process.Start (startInfo);
			return true;
		} catch {
			return false;
		}
	}

	[DllImport ("user32.dll")]
	private static extern bool SetWindowDisplayAffinity (nint hwnd, uint dwAffinity);

	public static bool SetPrivacyMode (bool on)
	{
		if (on == _privacyOn) {
			return true;
		}
		try {
			OnUi (delegate {
				try {
					if (on) {
						Screen[] allScreens = Screen.AllScreens;
						foreach (Screen screen in allScreens) {
							BlackScreenForm blackScreenForm = new BlackScreenForm (screen.Bounds);
							blackScreenForm.Show ();
							lock (_blackLock) _blackWindows.Add (blackScreenForm);
						}
					} else {
						Form[] toClose;
						lock (_blackLock) {
							toClose = _blackWindows.ToArray ();
							_blackWindows.Clear ();
						}
						foreach (Form blackWindow in toClose) {
							try { blackWindow.Hide (); } catch { }
							try { blackWindow.Close (); } catch { }
							try { blackWindow.Dispose (); } catch { }
						}
					}
				} catch (Exception ex) {
					AppLog.Write ("Privacy", "切换隐私模式失败：" + ex.Message);
				}
			});
			_privacyOn = on;
			return true;
		} catch {
			return false;
		}
	}

	[DllImport ("user32.dll")]
	private static extern bool GetCursorInfo (ref CURSORINFO pci);

	[DllImport ("user32.dll")]
	private static extern bool GetIconInfo (nint hIcon, ref ICONINFO piconinfo);

	[DllImport ("user32.dll")]
	private static extern bool DrawIconEx (nint hdc, int xLeft, int yTop, nint hIcon, int cxWidth, int cyWidth, int istepIfAniCur, nint hbrFlickerFreeDraw, uint diFlags);

	public static Bitmap? CaptureCursor (out int hotX, out int hotY)
	{
		hotX = (hotY = 0);
		try {
			CURSORINFO pci = new CURSORINFO {
				cbSize = Marshal.SizeOf<CURSORINFO> ()
			};
			if (!GetCursorInfo (ref pci)) {
				return null;
			}
			if ((pci.flags & 1) == 0 || pci.hCursor == IntPtr.Zero) {
				return null;
			}
			ICONINFO piconinfo = default(ICONINFO);
			if (!GetIconInfo (pci.hCursor, ref piconinfo)) {
				return null;
			}
			try {
				hotX = piconinfo.xHotspot;
				hotY = piconinfo.yHotspot;
				Bitmap bitmap = new Bitmap (32, 32, PixelFormat.Format32bppArgb);
				using (Graphics graphics = Graphics.FromImage (bitmap)) {
					nint hdc = graphics.GetHdc ();
					try {
						DrawIconEx (hdc, 0, 0, pci.hCursor, 32, 32, 0, IntPtr.Zero, 3u);
					} finally {
						graphics.ReleaseHdc (hdc);
					}
				}
				return bitmap;
			} finally {
				if (piconinfo.hbmMask != IntPtr.Zero) {
					DeleteObject (piconinfo.hbmMask);
				}
				if (piconinfo.hbmColor != IntPtr.Zero) {
					DeleteObject (piconinfo.hbmColor);
				}
			}
		} catch {
			return null;
		}
	}

	[DllImport ("gdi32.dll")]
	private static extern bool DeleteObject (nint hObject);

	public static Point? CursorPos ()
	{
		try {
			CURSORINFO pci = new CURSORINFO {
				cbSize = Marshal.SizeOf<CURSORINFO> ()
			};
			if (!GetCursorInfo (ref pci)) {
				return null;
			}
			if ((pci.flags & 1) == 0) {
				return null;
			}
			return new Point (pci.ptScreenPos.x, pci.ptScreenPos.y);
		} catch {
			return null;
		}
	}
}
