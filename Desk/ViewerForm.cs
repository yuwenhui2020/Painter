using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;

namespace desk;

internal sealed class ViewerForm : Form
{
	private sealed class FrameView : Control
	{
		private readonly object _lock;

		private Bitmap? _img;

		private Point? _cursorScreen;

		public int RemoteHotX;

		public int RemoteHotY;

		public string? ToastText { get; set; }

		public Bitmap? RemoteCursor { get; set; }

		public Point? RemoteCursorAt { get; set; }

		public FrameView (object frameLock)
		{
			_lock = frameLock;
			SetStyle (ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, value: true);
			DoubleBuffered = true;
		}

		public void SetCursorScreen (Point? screenPos)
		{
			if (!(_cursorScreen == screenPos)) {
				_cursorScreen = screenPos;
				Invalidate ();
			}
		}

		public void SetImage (int w, int h, byte[]? bgra)
		{
			if (bgra == null) {
				Bitmap? old;
				lock (_lock) {
					old = _img;
					_img = null;
				}
				try { old?.Dispose (); } catch { } // 置空前释放旧位图，避免反复空帧累积 GDI 对象
				Invalidate ();
				return;
			}
			Bitmap bitmap;
			lock (_lock) {
				if (_img == null || _img.Width != w || _img.Height != h) {
					bitmap = _img;
					_img = new Bitmap (w, h, PixelFormat.Format32bppArgb);
				} else {
					bitmap = null;
				}
				Rectangle rect = new Rectangle (0, 0, w, h);
				BitmapData bitmapData = _img.LockBits (rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
				try {
					for (int i = 0; i < h; i++) {
						Marshal.Copy (bgra, i * w * 4, IntPtr.Add (bitmapData.Scan0, i * bitmapData.Stride), w * 4);
					}
				} finally {
					_img.UnlockBits (bitmapData);
				}
			}
			bitmap?.Dispose ();
			Invalidate ();
		}

		public void Detach ()
		{
			lock (_lock) {
				_img = null;
				_cursorScreen = null;
			}
			Invalidate ();
		}

		public Rectangle GetImageRect ()
		{
			lock (_lock) {
				Bitmap img = _img;
				Size clientSize = base.ClientSize;
				if (img == null || img.Width <= 0 || img.Height <= 0) {
					return Rectangle.Empty;
				}
				if (clientSize.Width <= 0 || clientSize.Height <= 0) {
					return Rectangle.Empty;
				}
				float num = Math.Min ((float)clientSize.Width / (float)img.Width, (float)clientSize.Height / (float)img.Height);
				int num2 = (int)((float)img.Width * num);
				int num3 = (int)((float)img.Height * num);
				return new Rectangle ((clientSize.Width - num2) / 2, (clientSize.Height - num3) / 2, num2, num3);
			}
		}

		protected override void OnPaint (PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			graphics.InterpolationMode = InterpolationMode.Low;
			graphics.SmoothingMode = SmoothingMode.HighSpeed;
			graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
			graphics.CompositingQuality = CompositingQuality.HighSpeed;
			graphics.Clear (Color.Black);
			lock (_lock) {
				Bitmap img = _img;
				Rectangle imageRect = GetImageRect ();
				if (img != null && imageRect.Width > 0 && imageRect.Height > 0) {
					graphics.DrawImage (img, imageRect);
				}
			}
			DrawRemoteCursor (graphics);
			DrawLocalCursor (graphics);
			DrawToast (graphics);
		}

		private void DrawRemoteCursor (Graphics g)
		{
			Bitmap remoteCursor = RemoteCursor;
			Point? remoteCursorAt = RemoteCursorAt;
			if (remoteCursor != null && remoteCursorAt.HasValue) {
				Rectangle imageRect = GetImageRect ();
				if (imageRect.Width > 0 && imageRect.Contains (remoteCursorAt.Value)) {
					g.DrawImage (remoteCursor, new Rectangle (remoteCursorAt.Value.X - RemoteHotX, remoteCursorAt.Value.Y - RemoteHotY, remoteCursor.Width, remoteCursor.Height));
				}
			}
		}

		private void DrawToast (Graphics g)
		{
			string toastText = ToastText;
			if (string.IsNullOrEmpty (toastText)) {
				return;
			}
			using SolidBrush brush = new SolidBrush (Color.FromArgb (200, 0, 0, 0));
			using SolidBrush brush2 = new SolidBrush (Color.White);
			SizeF sizeF = g.MeasureString (toastText, Font);
			RectangleF rect = new RectangleF (16f, 16f, sizeF.Width + 24f, sizeF.Height + 14f);
			g.FillRectangle (brush, rect);
			g.DrawString (toastText, Font, brush2, new PointF (rect.X + 12f, rect.Y + 7f));
		}

		private void DrawLocalCursor (Graphics g)
		{
			Point? cursorScreen = _cursorScreen;
			if (!cursorScreen.HasValue) {
				return;
			}
			Rectangle imageRect = GetImageRect ();
			if (imageRect.Width > 0 && imageRect.Height > 0) {
				Point pt = PointToClient (cursorScreen.Value);
				if (imageRect.Contains (pt)) {
					Cursors.Arrow.Draw (g, new Rectangle (pt.X, pt.Y, 32, 32));
				}
			}
		}
	}

	private const int VK_ESCAPE = 27;

	private const int MaxReconnectTries = 20;

	private const int WheelNotch = 120;

	private double _wheelAcc;

	private const int VK_CONTROL = 17;

	private const int VK_LCONTROL = 162;

	private const int VK_RCONTROL = 163;

	private static readonly int[] QualityLevels = new int[5] { 40, 55, 70, 85, 92 };

	private const int FpsMin = 5;

	private const int FpsMax = 60;

	private const int FpsStep = 5;

	private static readonly int[] Resolutions = new int[7] { 640, 800, 1024, 1280, 1600, 1920, 7680 };

	private static readonly Protocol.EncodingMode[] Modes = new Protocol.EncodingMode[3] {
		Protocol.EncodingMode.JpegRegions,
		Protocol.EncodingMode.RawRegions,
		Protocol.EncodingMode.JpegFullFrame
	};

	private readonly RemoteDesktopClient _client;

	// 记录已按下的键鼠：在失焦/移出画面/释放控制/关闭窗口时必须补发抬起，
	// 否则被控端按键会一直保持按下状态（画笔持续画线、Ctrl 卡住）。
	private readonly HashSet<byte> _downMouseButtons = new HashSet<byte> ();
	private readonly HashSet<int> _downKeys = new HashSet<int> ();

	private readonly FrameView _view;

	private readonly Label _lblStats;

	private readonly Label _lblHint;

	private readonly CheckBox _chkClip;

	private readonly Button _btnRelease;

	private readonly Button _btnFull;

	private Button _btnSendFile = null;

	private readonly ComboBox _cmbQuality;

	private readonly ComboBox _cmbRes;

	private readonly ComboBox _cmbFps;

	private readonly ComboBox _cmbMode;

	private readonly CheckBox _chkAuto;

	private readonly Panel _topPanel;

	private readonly Panel _bottomPanel;

	private readonly Timer _adaptTimer = new Timer {
		Interval = 1000
	};

	private readonly Timer _toastTimer = new Timer {
		Interval = 4000
	};

	private readonly Timer _reconnectTimer = new Timer {
		Interval = 3000
	};

	private readonly string _host;

	private readonly int _port;

	private readonly string _password;

	private int _reconnectTries;

	private int _statLogTicks;

	private LowLevelInputHook? _hook;

	private int _frameCount;

	private readonly Stopwatch _fpsSw = Stopwatch.StartNew ();

	private readonly ClipboardSync _clip;

	private bool _controlEnabled = true;

	private bool _closing;

	private bool _reconnecting;

	private bool _cursorHidden;

	private bool _overImage;

	private int _level = 2;

	private int _prefLevel = 2;

	private int _prefResIndex = 5;

	private int _prefFps = 60;

	private int _lastFps = -1;

	private long _lastFpsUpdate;

	private long _rttMs = -1L;

	private string _resText = "-";

	private int _goodStreak;

	private int _badStreak;

	private bool _applying;

	private bool _qualitySent;

	private long _lastRttUpdate;

	private long _cooldownUntil;

	private Action<long>? _rttHandler;

	private long _lastFrameTick;

	private long _lastKeyFrameReq;

	private bool _stallNotified;

	private bool _fullScreen;

	private readonly Dictionary<Protocol.Permission, bool> _perms = new Dictionary<Protocol.Permission, bool> ();

	private CheckBox _chkRemoteCursor = null;

	private CheckBox _chkAudio = null;

	private AudioPlayback? _audio;

	private CheckBox _chkRelative = null;

	private Button _btnBlock = null;

	private Button _btnPrivacy = null;

	private TransfersForm? _transfers;

	private Bitmap? _remoteCursor;

	private int _remoteHotX;

	private int _remoteHotY;

	private Point? _remoteCursorPos;

	private int _remoteW;

	private int _remoteH;

	private bool _privacyOn;

	private long _probeDelay = -1L;

	private long _lastProbeAt;

	private Point? _lastLocalPos;

	private bool _ctrlDown;

	private Rectangle _prevBounds;

	private FormBorderStyle _prevBorder = FormBorderStyle.Sizable;

	private long _restartUntil;

	private int CurrentFps {
		get {
			int result;
			return int.TryParse ((string)_cmbFps.SelectedItem, out result) ? result : 60;
		}
	}

	private int TargetFps => CurrentFps;

	private bool RttStale => NetFx.TickCount64 - _lastRttUpdate > 4000;

	private bool InCooldown => NetFx.TickCount64 < _cooldownUntil;

	private bool InputAllowed {
		get {
			bool value;
			return !_perms.TryGetValue (Protocol.Permission.Keyboard, out value) || value;
		}
	}

	[DllImport ("user32.dll")]
	private static extern nint GetForegroundWindow ();

	public ViewerForm (RemoteDesktopClient client, string host, int port, string password)
	{
		_client = client;
		Icon = AppIcon.Get ();
		_host = host;
		_port = port;
		_password = password;
		base.AutoScaleMode = AutoScaleMode.Dpi;
		base.AutoScaleDimensions = new SizeF (96f, 96f);
		Text = $"正在控制 {host}:{port}";
		base.StartPosition = FormStartPosition.CenterScreen;
		base.ClientSize = new Size (1280, 800);
		base.KeyPreview = true;
		Color color = Color.FromArgb (255, 24, 24, 24);
		_view = new FrameView (_client.FrameLock) {
			Dock = DockStyle.Fill,
			BackColor = Color.Black
		};
		base.Controls.Add (_view);
		FlowLayoutPanel flowLayoutPanel = (FlowLayoutPanel)(_topPanel = new FlowLayoutPanel {
			Dock = DockStyle.Top,
			AutoSize = true,
			WrapContents = true,
			FlowDirection = FlowDirection.LeftToRight,
			BackColor = color,
			Padding = new Padding (6, 4, 6, 4)
		});
		_lblStats = new Label {
			Text = "FPS: -   分辨率: -   延迟: -   画质: -",
			Size = new Size (360, 22),
			AutoSize = false,
			TextAlign = ContentAlignment.MiddleLeft,
			ForeColor = Color.White,
			BackColor = color
		};
		_chkClip = new CheckBox {
			Text = "剪贴板同步",
			AutoSize = true,
			Checked = true,
			ForeColor = Color.White,
			BackColor = color,
			Margin = new Padding (8, 5, 2, 2)
		};
		_btnSendFile = new Button {
			Text = "发送文件 →",
			Size = new Size (92, 24),
			BackColor = color,
			ForeColor = Color.White,
			Margin = new Padding (2, 1, 2, 1)
		};
		_btnSendFile.Click += BtnSendFile_Click;
		_cmbQuality = new ComboBox {
			DropDownStyle = ComboBoxStyle.DropDownList,
			Size = new Size (80, 23),
			BackColor = color,
			ForeColor = Color.White,
			Margin = new Padding (2, 1, 2, 1)
		};
		_cmbQuality.Items.AddRange (new object[3] { "流畅", "均衡", "清晰" });
		_cmbQuality.SelectedIndex = 1;
		_cmbQuality.SelectedIndexChanged += delegate {
			if (!_applying) {
				_prefLevel = _cmbQuality.SelectedIndex switch {
					0 => 1, 
					1 => 2, 
					_ => 3, 
				};
				ApplyLevel (_prefLevel, send: false);
				ManualChanged ();
			}
		};
		_cmbRes = new ComboBox {
			DropDownStyle = ComboBoxStyle.DropDownList,
			Size = new Size (78, 23),
			BackColor = color,
			ForeColor = Color.White,
			Margin = new Padding (2, 1, 2, 1)
		};
		_cmbRes.Items.AddRange (new object[7] { "640", "800", "1024", "1280", "1600", "1920", "原始" });
		_cmbRes.SelectedIndex = 5;   // 默认 1920
		_cmbRes.SelectedIndexChanged += delegate {
			if (!_applying) {
				_prefResIndex = _cmbRes.SelectedIndex;
				ManualChanged ();
			}
		};
		_cmbFps = new ComboBox {
			DropDownStyle = ComboBoxStyle.DropDownList,
			Size = new Size (62, 23),
			BackColor = color,
			ForeColor = Color.White,
			Margin = new Padding (2, 1, 2, 1)
		};
		for (int num = 5; num <= 60; num += 5) {
			_cmbFps.Items.Add (num.ToString ());
		}
		_cmbFps.SelectedIndex = 11;   // 默认 60fps
		_cmbFps.SelectedIndexChanged += delegate {
			if (!_applying) {
				_prefFps = int.Parse ((string)_cmbFps.SelectedItem);
				ManualChanged ();
			}
		};
		_cmbMode = new ComboBox {
			DropDownStyle = ComboBoxStyle.DropDownList,
			Size = new Size (132, 23),
			DropDownWidth = 200,
			BackColor = color,
			ForeColor = Color.White,
			Margin = new Padding (2, 1, 2, 1)
		};
		_cmbMode.Items.AddRange (new object[3] { "JPEG区域(均衡)", "原始RGB(局域网)", "JPEG全帧(兼容)" });
		_cmbMode.SelectedIndex = 0;
		_cmbMode.SelectedIndexChanged += delegate {
			if (!_applying) {
				ManualChanged ();
			}
		};
		_chkAuto = new CheckBox {
			Text = "自动优化卡顿",
			AutoSize = true,
			Checked = true,
			ForeColor = Color.White,
			BackColor = color,
			Margin = new Padding (8, 5, 2, 2)
		};
		_chkAuto.CheckedChanged += delegate {
			_goodStreak = (_badStreak = 0);
			RefreshStats ();
		};
		_btnRelease = new Button {
			Text = "释放鼠标(Esc)",
			Size = new Size (120, 24),
			BackColor = color,
			ForeColor = Color.White,
			Margin = new Padding (2, 1, 2, 1)
		};
		_btnRelease.Click += delegate {
			SetControlEnabled (!_controlEnabled);
		};
		_btnFull = new Button {
			Text = "全屏",
			Size = new Size (70, 24),
			BackColor = color,
			ForeColor = Color.White,
			Margin = new Padding (2, 1, 2, 1)
		};
		_btnFull.Click += delegate {
			ToggleFullScreen ();
		};
		Button button = new Button {
			Text = "日志",
			Size = new Size (60, 24),
			BackColor = color,
			ForeColor = Color.White,
			Margin = new Padding (2, 1, 2, 1)
		};
		button.Click += delegate {
			LogForm logForm = new LogForm ();
			logForm.Show (this);
		};
		flowLayoutPanel.Controls.AddRange (new Control[15] {
			_lblStats,
			_chkClip,
			_btnSendFile,
			NewBarLabel ("画质", color),
			_cmbQuality,
			NewBarLabel ("分辨率", color),
			_cmbRes,
			NewBarLabel ("帧率", color),
			_cmbFps,
			NewBarLabel ("编码", color),
			_cmbMode,
			_chkAuto,
			_btnRelease,
			_btnFull,
			button
		});
		base.Controls.Add (flowLayoutPanel);
		base.Controls.Add (BuildActionBar (color));
		Panel panel = (_bottomPanel = new Panel {
			Dock = DockStyle.Bottom,
			Height = 28,
			BackColor = color,
			Padding = new Padding (0)
		});
		_lblHint = new Label {
			Dock = DockStyle.Fill,
			AutoSize = false,
			TextAlign = ContentAlignment.MiddleCenter,
			ForeColor = Color.White,
			BackColor = color
		};
		panel.Controls.Add (_lblHint);
		base.Controls.Add (panel);
		_client.FrameReceived += OnFrame;
		_client.Disconnected += OnDisconnected;
		_client.Log += delegate(string s) {
			SetHint (s);
		};
		_rttHandler = delegate(long ms) {
			Ui (delegate {
				_rttMs = ms;
				_lastRttUpdate = NetFx.TickCount64;
				RefreshStats ();
			});
		};
		_client.RttMeasured += _rttHandler;
		_client.QualityReceived += delegate(Protocol.QualityConfig q) {
			Ui (delegate {
				ApplyRemoteQuality (q);
			});
		};
		_client.PermissionChanged += delegate(Protocol.Permission p, bool on) {
			Ui (delegate {
				_perms [p] = on;
				AppLog.Write ("权限", $"{p} = {(on ? "允许" : "拒绝")}");
				ApplyPermissions ();
			});
		};
		_client.CursorDataReceived += delegate(Bitmap bmp, int hx, int hy) {
			Ui (delegate {
				_view.RemoteCursor?.Dispose ();
				_view.RemoteCursor = bmp;
				_view.RemoteHotX = hx;
				_view.RemoteHotY = hy;
				_view.Invalidate ();
			});
		};
		_client.CursorPosReceived += delegate(Point pt) {
			Ui (delegate {
				_remoteCursorPos = pt;
				UpdateRemoteCursorPos ();
				_view.Invalidate ();
			});
		};
		_client.PrivacyStateReceived += delegate(bool on, bool ok) {
			Ui (delegate {
				_privacyOn = on && ok;
				_btnPrivacy.Text = (_privacyOn ? "退出隐私" : "隐私模式");
				SetHint (ok ? ("被控端隐私模式已" + (on ? "开启（对方屏幕已黑屏）" : "关闭")) : "被控端拒绝或未开启隐私模式");
			});
		};
		_client.DelayProbeReply += delegate(long ms) {
			Ui (delegate {
				_probeDelay = ms;
				_lastProbeAt = NetFx.TickCount64;
			});
		};
		_client.AudioFormatReceived += delegate(int rate, int ch) {
			AppLog.Write ("音频", $"远端音频格式 {rate}Hz {ch}声道");
		};
		_client.AudioFrameReceived += delegate(byte[] d) {
			if (!_chkAudio.Checked) {
				return;
			}
			try {
				if (_audio == null) {
					_audio = new AudioPlayback ();
				}
				_audio.Feed (d);
			} catch {
			}
		};
		_client.EncodingAbilityReceived += delegate(Protocol.CodecAbility ability, Protocol.CodecAbility prefer) {
			AppLog.Write ("能力协商", $"被控端编码能力 {ability}，优先 {prefer}");
		};
		_clip = new ClipboardSync (() => WinClipboard.Read (), delegate(ClipFormat fmt, byte[] data) {
			WinClipboard.Write (fmt, data);
		}, delegate(ClipFormat fmt, byte[] data) {
			_client.SendClipboardDataAsync (fmt, data);
		});
		_clip.Enabled = _chkClip.Checked;
		_chkClip.CheckedChanged += delegate {
			_clip.Enabled = _chkClip.Checked;
		};
		_client.ClipboardDataReceived += delegate(ClipFormat fmt, byte[] data) {
			Ui (delegate {
				_clip.OnRemoteData (fmt, data);
			});
		};
		_clip.GetFiles = () => WinClipboard.ReadFiles ();
		_clip.SendFiles = delegate(string[] files) {
			_client.SendClipboardFilesAsync (files);
		};
		_client.Files.OnClipboardFilesReady = delegate(List<string> list) {
			Ui (delegate {
				SetHint ($"已收到对方剪贴板上的 {list.Count} 个文件，可直接在本机粘贴。");
			});
		};
		_client.FileOfferPrompt = (int id, string name, long size) => AskAcceptFile (name, size);
		_adaptTimer.Tick += AdaptTick;
		_toastTimer.Tick += ToastTick;
		_reconnectTimer.Tick += ReconnectTick;
		base.Shown += delegate {
			_lastRttUpdate = NetFx.TickCount64;
			InstallHook ();
			_clip.Start ();
			_adaptTimer.Start ();
			UpdateCursor ();
			UpdateHint ();
			RefreshStats ();
		};
	}

	private FlowLayoutPanel BuildActionBar (Color dark)
	{
		FlowLayoutPanel flowLayoutPanel = new FlowLayoutPanel {
			Dock = DockStyle.Top,
			AutoSize = true,
			WrapContents = true,
			FlowDirection = FlowDirection.LeftToRight,
			BackColor = dark,
			Padding = new Padding (6, 0, 6, 4)
		};
		Button button2 = NewActionButton ("Ctrl+Alt+Del", dark, 110);
		button2.Click += delegate {
			_client.SendControlActionAsync (Protocol.ControlActionId.CtrlAltDel);
		};
		Button button3 = NewActionButton ("锁屏", dark, 70);
		button3.Click += delegate {
			_client.SendControlActionAsync (Protocol.ControlActionId.LockScreen);
		};
		_btnBlock = NewActionButton ("屏蔽对方输入", dark, 120);
		_btnBlock.Click += delegate {
			bool flag = _btnBlock.Text.Contains ("屏蔽");
			_client.SendControlActionAsync (flag ? Protocol.ControlActionId.BlockInputOn : Protocol.ControlActionId.BlockInputOff);
			_btnBlock.Text = (flag ? "恢复对方输入" : "屏蔽对方输入");
		};
		Button button4 = NewActionButton ("发送文件夹", dark, 100);
		button4.Click += delegate {
			using FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog {
				Description = "选择要发送的文件夹（含子目录）"
			};
			if (folderBrowserDialog.ShowDialog (this) == DialogResult.OK) {
				if (_transfers == null) {
					_transfers = new TransfersForm (_client.Files);
				}
				if (_transfers.IsDisposed) {
					_transfers = new TransfersForm (_client.Files);
				}
				_transfers.Show ();
				_transfers.BringToFront ();
				_client.SendFolderAsync (folderBrowserDialog.SelectedPath);
			}
		};
		Button button5 = NewActionButton ("传输进度", dark, 90);
		button5.Click += delegate {
			if (_transfers == null) {
				_transfers = new TransfersForm (_client.Files);
			}
			if (_transfers.IsDisposed) {
				_transfers = new TransfersForm (_client.Files);
			}
			_transfers.Show ();
			_transfers.BringToFront ();
		};
		Button button6 = NewActionButton ("重启对方", dark, 90);
		button6.Click += delegate {
			RestartRemote ();
		};
		Button button7 = NewActionButton ("端口转发", dark, 90);
		button7.Click += delegate {
			Form dlg = new Form {
				Text = "端口转发（本机 → 对方网络）",
				ClientSize = new Size (360, 180),
				StartPosition = FormStartPosition.CenterParent,
				FormBorderStyle = FormBorderStyle.FixedDialog,
				MaximizeBox = false,
				MinimizeBox = false
			};
			try {
				Label label = new Label {
					Text = "本机监听端口(0=自动):",
					Location = new Point (12, 16),
					Size = new Size (140, 20)
				};
				TextBox txtLp = new TextBox {
					Text = "0",
					Location = new Point (160, 13),
					Size = new Size (180, 23)
				};
				Label label2 = new Label {
					Text = "目标地址(对方侧):",
					Location = new Point (12, 48),
					Size = new Size (140, 20)
				};
				TextBox txtHost2 = new TextBox {
					Text = "127.0.0.1",
					Location = new Point (160, 45),
					Size = new Size (180, 23)
				};
				Label label3 = new Label {
					Text = "目标端口:",
					Location = new Point (12, 80),
					Size = new Size (140, 20)
				};
				TextBox txtRp = new TextBox {
					Text = "3389",
					Location = new Point (160, 77),
					Size = new Size (180, 23)
				};
				Button button8 = new Button {
					Text = "启动",
					Location = new Point (160, 120),
					Size = new Size (84, 28)
				};
				button8.Click += async delegate {
					if (!int.TryParse (txtLp.Text.Trim (), out var lp)) {
						lp = 0;
					}
					if (!int.TryParse (txtRp.Text.Trim (), out var rp)) {
						MessageBox.Show (dlg, "目标端口无效");
					} else {
						dlg.Close ();
						int actual = await _client.PortForwarding.StartLocalAsync (lp, txtHost2.Text.Trim (), rp);
						SetHint ($"端口转发已启动：本机 127.0.0.1:{actual} → 对方 {txtHost2.Text.Trim ()}:{rp}");
					}
				};
				dlg.Controls.AddRange (new Control[7] { label, txtLp, label2, txtHost2, label3, txtRp, button8 });
				dlg.ShowDialog (this);
			} finally {
				if (dlg != null) {
					((IDisposable)dlg).Dispose ();
				}
			}
		};
		_btnPrivacy = NewActionButton ("隐私模式", dark, 90);
		_btnPrivacy.Click += delegate {
			_client.SendPrivacyModeAsync (!_privacyOn);
		};
		_chkAudio = new CheckBox {
			Text = "对方声音",
			AutoSize = true,
			ForeColor = Color.White,
			BackColor = dark,
			Margin = new Padding (8, 5, 2, 2)
		};
		_chkAudio.CheckedChanged += delegate {
			_client.SetRemoteAudioAsync (_chkAudio.Checked);
			SetHint (_chkAudio.Checked ? "已请求转发对方声音。" : "已关闭对方声音。");
			if (!_chkAudio.Checked) {
				_audio?.Stop ();
			}
		};
		_chkRemoteCursor = new CheckBox {
			Text = "显示对方光标",
			AutoSize = true,
			ForeColor = Color.White,
			BackColor = dark,
			Margin = new Padding (8, 5, 2, 2)
		};
		_chkRemoteCursor.CheckedChanged += delegate {
			UpdateCursor ();
			_view.Invalidate ();
		};
		_chkRelative = new CheckBox {
			Text = "相对鼠标(游戏)",
			AutoSize = true,
			ForeColor = Color.White,
			BackColor = dark,
			Margin = new Padding (8, 5, 2, 2)
		};
		_chkRelative.CheckedChanged += delegate {
			_lastLocalPos = null;
			SetHint (_chkRelative.Checked ? "相对鼠标模式：以位移增量控制对方，适合游戏/3D 应用。" : "");
			if (!_chkRelative.Checked) {
				UpdateHint ();
			}
		};
		flowLayoutPanel.Controls.AddRange (new Control[11] {
			button2, button3, _btnBlock, button6, _btnPrivacy, button4, button5, button7, _chkAudio,
			_chkRemoteCursor, _chkRelative
		});
		return flowLayoutPanel;
	}

	private static Button NewActionButton (string text, Color dark, int width)
	{
		return new Button {
			Text = text,
			Size = new Size (width, 24),
			BackColor = dark,
			ForeColor = Color.White,
			Margin = new Padding (2, 1, 2, 1)
		};
	}

	private static Label NewBarLabel (string text, Color dark)
	{
		return new Label {
			Text = text,
			AutoSize = true,
			ForeColor = Color.White,
			BackColor = dark,
			Margin = new Padding (8, 6, 0, 2)
		};
	}

	private void ApplyLevel (int level, bool send)
	{
		level = NetFx.Clamp (level, 0, QualityLevels.Length - 1);
		_level = level;
		_applying = true;
		_cmbQuality.SelectedIndex = ((level > 1) ? ((level <= 2) ? 1 : 2) : 0);
		_applying = false;
		if (send) {
			PushQuality ();
		}
		RefreshStats ();
	}

	private void PushQuality ()
	{
		_client.SendQualityAsync (CurrentConfig ());
	}

	/// <summary>被控端主动改画质后同步到本端：只更新界面、不再回发，避免两端互相顶牛。</summary>
	private void ApplyRemoteQuality (Protocol.QualityConfig q)
	{
		_applying = true;
		try {
			int modeIndex = Array.IndexOf (Modes, q.Mode);
			_cmbMode.SelectedIndex = ((modeIndex >= 0) ? modeIndex : 0);
			int num = NearestIndex (Resolutions, q.MaxWidth);
			_cmbRes.SelectedIndex = num;
			_prefResIndex = num;
			int[] array = new int [_cmbFps.Items.Count];
			for (int i = 0; i < array.Length; i++) {
				int.TryParse (_cmbFps.Items [i]?.ToString (), out array [i]);
			}
			int num2 = NearestIndex (array, q.Fps);
			if (num2 >= 0 && num2 < _cmbFps.Items.Count) {
				_cmbFps.SelectedIndex = num2;
				_prefFps = q.Fps;
			}
			int num3 = NetFx.Clamp (NearestIndex (QualityLevels, q.Quality), 0, QualityLevels.Length - 1);
			_level = num3;
			_prefLevel = num3;
			_cmbQuality.SelectedIndex = ((_level > 1) ? ((_level <= 2) ? 1 : 2) : 0);
			_chkAuto.Checked = false;
			_qualitySent = true;
		} finally {
			_applying = false;
		}
		RefreshStats ();
		SetHint ($"被控端已调整画质：{q.MaxWidth}px / 质量{q.Quality}% / {q.Fps}fps");
	}

	private static int NearestIndex (int[] arr, int value)
	{
		int num = 0;
		int num2 = int.MaxValue;
		for (int i = 0; i < arr.Length; i++) {
			int num3 = Math.Abs (arr [i] - value);
			if (num3 < num2) {
				num2 = num3;
				num = i;
			}
		}
		return num;
	}

	private Protocol.QualityConfig CurrentConfig ()
	{
		Protocol.EncodingMode mode = Modes [NetFx.Clamp (_cmbMode.SelectedIndex, 0, Modes.Length - 1)];
		int quality = QualityLevels [_level];
		int maxWidth = Resolutions [NetFx.Clamp (_cmbRes.SelectedIndex, 0, Resolutions.Length - 1)];
		int currentFps = CurrentFps;
		return new Protocol.QualityConfig (mode, quality, maxWidth, currentFps, 1, 32);
	}

	private void StepResolution (int delta)
	{
		int num = NetFx.Clamp (_cmbRes.SelectedIndex + delta, 0, Resolutions.Length - 1);
		if (num != _cmbRes.SelectedIndex) {
			_applying = true;
			_cmbRes.SelectedIndex = num;
			_applying = false;
		}
	}

	private bool StepFpsDown ()
	{
		int currentFps = CurrentFps;
		if (currentFps <= 5) {
			return false;
		}
		SetFpsCombo (Math.Max (5, currentFps - 5));
		return true;
	}

	private bool StepFpsUp ()
	{
		int currentFps = CurrentFps;
		if (currentFps >= _prefFps) {
			return false;
		}
		SetFpsCombo (Math.Min (_prefFps, currentFps + 5));
		return true;
	}

	private void SetFpsCombo (int fps)
	{
		int value = (fps - 5) / 5;
		value = NetFx.Clamp (value, 0, _cmbFps.Items.Count - 1);
		_applying = true;
		_cmbFps.SelectedIndex = value;
		_applying = false;
	}

	private void ManualChanged ()
	{
		_badStreak = 0;
		_goodStreak = 0;
		_cooldownUntil = NetFx.TickCount64 + 10000;
		PushQuality ();
		RefreshStats ();
	}

	private void LogFrameStats ()
	{
		if (++_statLogTicks >= 5) {
			_statLogTicks = 0;
			AppLog.Write ("M端", "[统计] " + _client.TakeFrameStats (5000L) + $" | 探针延迟{_probeDelay}ms RTT{_rttMs}ms");
		}
	}

	private void AdaptTick (object? sender, EventArgs e)
	{
		if (_closing) {
			return;
		}
		Watchdog ();
		LogFrameStats ();
		if (!_chkAuto.Checked) {
			return;
		}
		if (InCooldown) {
			_badStreak = 0;
			_goodStreak = 0;
			return;
		}
		_client.SendTestDelayAsync (NetFx.TickCount64, (_probeDelay < 0) ? 0 : _probeDelay, 0);
		bool flag = NetFx.TickCount64 - _lastProbeAt > 4000 || RttStale;
		long num = ((_probeDelay >= 0) ? _probeDelay : _rttMs);
		bool flag2 = flag || (num >= 0 && num > 400);
		bool flag3 = NetFx.TickCount64 - _lastFpsUpdate < 3000 && _lastFps >= 0 && TargetFps > 0 && (double)_lastFps < Math.Max (3.0, (double)TargetFps * 0.5);
		bool flag4 = flag2 || (flag3 && (num < 0 || num > 150));
		bool flag5 = !flag && num >= 0 && num < 120 && !flag3;
		if (flag4) {
			_badStreak++;
			_goodStreak = 0;
		} else if (flag5) {
			_goodStreak++;
			_badStreak = 0;
		} else {
			_badStreak = 0;
			_goodStreak = 0;
		}
		int num2 = Math.Max (0, _prefLevel - 2);
		int num3 = Math.Max (0, _prefResIndex - 1);
		if (_badStreak >= 3) {
			_badStreak = 0;
			if (_level > num2) {
				ApplyLevel (_level - 1, send: false);
			} else if (_cmbRes.SelectedIndex > num3) {
				StepResolution (-1);
			} else if (!StepFpsDown ()) {
				return;
			}
			PushQuality ();
			_cooldownUntil = NetFx.TickCount64 + 4000;
			RefreshStats ();
		} else {
			if (_goodStreak < 8) {
				return;
			}
			_goodStreak = 0;
			if (StepFpsUp () || _cmbRes.SelectedIndex < _prefResIndex || _level < _prefLevel) {
				if (_cmbRes.SelectedIndex < _prefResIndex) {
					StepResolution (1);
				} else if (_level < _prefLevel) {
					ApplyLevel (_level + 1, send: false);
				}
				PushQuality ();
				_cooldownUntil = NetFx.TickCount64 + 4000;
				RefreshStats ();
			}
		}
	}

	private void RefreshStats ()
	{
		if (_lblStats != null && !base.IsDisposed) {
			string value = (RttStale ? "超时" : (_rttMs + "ms"));
			string value2 = ((_lastFps < 0) ? "-" : _lastFps.ToString ());
			_lblStats.Text = $"FPS: {value2}   分辨率: {_resText}   延迟: {value}   画质: {_level + 1}/5{(_chkAuto.Checked ? " 自动" : "")}";
		}
	}

	private void InstallHook ()
	{
		_hook = new LowLevelInputHook {
			IsActive = () => !_closing && base.IsHandleCreated,
			KeyFilter = KeyFilter
		};
		_hook.MouseMove += delegate(int sx, int sy) {
			bool flag = IsOverImage (sx, sy);
			if (flag != _overImage) {
				_overImage = flag;
				UpdateCursor ();
			}
			_view.SetCursorScreen ((_controlEnabled && flag) ? new Point? (new Point (sx, sy)) : ((Point?)null));
			if (!ShouldForward (sx, sy)) {
				_lastLocalPos = null;
			} else if (_chkRelative.Checked) {
				if (_lastLocalPos.HasValue) {
					_client.SendMouseRelative (sx - _lastLocalPos.Value.X, sy - _lastLocalPos.Value.Y);
				}
				_lastLocalPos = new Point (sx, sy);
			} else {
				ToNorm (sx, sy, out var xNorm, out var yNorm);
				_client.SendMouseMove (xNorm, yNorm);
			}
		};
		_hook.MouseDown += delegate(byte btn, int sx, int sy) {
			if (!_controlEnabled) {
				if (WindowHasFocus () && IsOverImage (sx, sy)) {
					SetControlEnabled (on: true);
				}
			} else if (ShouldForward (sx, sy)) {
				ToNorm (sx, sy, out var xNorm, out var yNorm);
				_client.SendMouseDown (btn, xNorm, yNorm);
				_downMouseButtons.Add (btn);
			}
			};
			_hook.MouseUp += delegate(byte btn, int sx, int sy) {
			// 只要之前按下过就补发抬起：松手时鼠标可能已移出画面或窗口失焦，
			// 用 ShouldForward 过滤会漏掉 up，导致远端设备一直保持按下。
			if (_downMouseButtons.Remove (btn)) {
				ToNorm (sx, sy, out var xNorm, out var yNorm);
				try { _client.SendMouseUp (btn, xNorm, yNorm); } catch { }
			}
			};
		_hook.MouseWheel += delegate(short delta, int sx, int sy) {
			if (ShouldForward (sx, sy)) {
				ToNorm (sx, sy, out var xNorm, out var yNorm);
				_wheelAcc += delta;
				int num = (int)(_wheelAcc / 120.0);
				if (num != 0) {
					_wheelAcc -= (double)num * 120.0;
					_client.SendMouseWheel ((short)(num * 120), xNorm, yNorm);
				}
			}
		};
		_hook.Key += delegate(int vk, bool down) {
			if (!_closing && WindowHasFocus ()) {
				if (vk == 27) {
					if (down) {
						SetControlEnabled (!_controlEnabled);
					}
				} else if (_controlEnabled) {
					_client.SendKey (vk, down);
					if (down) _downKeys.Add (vk); else _downKeys.Remove (vk);
				}
			}
		};
	}

	/// <summary>补发所有仍按下的键鼠抬起，避免失焦/释放控制后远端状态卡住。</summary>
	private void ReleaseAllInputs ()
	{
		try {
			foreach (int vk in _downKeys) { try { _client.SendKey (vk, false); } catch { } }
			foreach (byte btn in _downMouseButtons) { try { _client.SendMouseUp (btn, 0, 0); } catch { } }
		} finally {
			_downKeys.Clear ();
			_downMouseButtons.Clear ();
		}
	}

	private bool WindowHasFocus ()
	{
		return base.IsHandleCreated && !_closing && GetForegroundWindow () == base.Handle;
	}

	private bool ShouldForward (int sx, int sy)
	{
		return _controlEnabled && InputAllowed && WindowHasFocus () && IsOverImage (sx, sy);
	}

	private void SetControlEnabled (bool on)
	{
		// 释放控制前先补发残留按键（Esc 本身不入 _downKeys）
		if (!on) ReleaseAllInputs ();
		_controlEnabled = on;
		_btnRelease.Text = (on ? "释放鼠标(Esc)" : "恢复控制(Esc)");
		_view.SetCursorScreen (null);
		UpdateCursor ();
		UpdateHint ();
	}

	private void ToggleFullScreen ()
	{
		SetFullScreen (!_fullScreen);
	}

	private void SetFullScreen (bool on)
	{
		if (_fullScreen == on) {
			return;
		}
		_fullScreen = on;
		SuspendLayout ();
		try {
			if (on) {
				_prevBounds = base.Bounds;
				_prevBorder = base.FormBorderStyle;
				base.FormBorderStyle = FormBorderStyle.None;
				_topPanel.Visible = false;
				_bottomPanel.Visible = false;
				base.WindowState = FormWindowState.Normal;
				base.Bounds = Screen.FromControl (this).Bounds;
			} else {
				base.FormBorderStyle = FormBorderStyle.Sizable;
				base.WindowState = FormWindowState.Normal;
				base.Bounds = _prevBounds;
				_topPanel.Visible = true;
				_bottomPanel.Visible = true;
			}
		} finally {
			ResumeLayout (performLayout: true);
		}
		_btnFull.Text = (on ? "退出全屏" : "全屏");
		UpdateCursor ();
		if (on) {
			ShowToast ("全屏模式：按 Ctrl + Esc 退出全屏");
		} else {
			UpdateHint ();
		}
	}

	private void UpdateRemoteCursorPos ()
	{
		if (!_remoteCursorPos.HasValue) {
			_view.RemoteCursorAt = null;
			return;
		}
		Rectangle imageRect = _view.GetImageRect ();
		if (imageRect.Width <= 0 || imageRect.Height <= 0) {
			_view.RemoteCursorAt = null;
			return;
		}
		int x = imageRect.X + (int)Math.Round ((double)_remoteCursorPos.Value.X / 65535.0 * (double)imageRect.Width);
		int y = imageRect.Y + (int)Math.Round ((double)_remoteCursorPos.Value.Y / 65535.0 * (double)imageRect.Height);
		_view.RemoteCursorAt = (imageRect.Contains (new Point (x, y)) ? new Point? (new Point (x, y)) : ((Point?)null));
	}

	private void ApplyPermissions ()
	{
		_chkClip.Enabled = Allow (Protocol.Permission.Clipboard);
		if (!_chkClip.Enabled) {
			_chkClip.Checked = false;
			_clip.Enabled = false;
		}
		_btnSendFile.Enabled = Allow (Protocol.Permission.File);
		_btnBlock.Enabled = Allow (Protocol.Permission.BlockInput);
		_btnPrivacy.Enabled = Allow (Protocol.Permission.PrivacyMode);
		if (!Allow (Protocol.Permission.Keyboard)) {
			SetHint ("对方已禁用键盘/鼠标控制。", Color.DarkRed);
		}
		bool Allow (Protocol.Permission p)
		{
			bool value;
			return !_perms.TryGetValue (p, out value) || value;
		}
	}

	private bool KeyFilter (int vk, bool down)
	{
		if (vk == 17 || vk == 162 || vk == 163) {
			_ctrlDown = down;
			return false;
		}
		if (vk == 27 && down && _ctrlDown) {
			// 只在本窗口聚焦且处于全屏时吞掉 Ctrl+Esc 用于退出全屏；
			// 否则全局拦截会让用户在其它程序里也无法打开开始菜单/任务管理器。
			if (_fullScreen && WindowHasFocus ()) {
				SetFullScreen (on: false);
				return true;
			}
		}
		return false;
	}

	private void Watchdog ()
	{
		if (!_qualitySent || _closing) {
			return;
		}
		long num = NetFx.TickCount64 - _lastFrameTick;
		if (num >= 6000) {
			if (!_stallNotified) {
				_stallNotified = true;
				ShowToast ("画面已停止更新，正在尝试恢复…（请检查对方是否锁屏 / UAC 弹窗）");
			}
			if (NetFx.TickCount64 - _lastKeyFrameReq > 3000) {
				_lastKeyFrameReq = NetFx.TickCount64;
				_client.RequestKeyFrameAsync ();
			}
		}
	}

	private void ShowToast (string text)
	{
		Ui (delegate {
			_view.ToastText = text;
			_view.Invalidate ();
		});
		_toastTimer.Stop ();
		_toastTimer.Start ();
	}

	private void ToastTick (object? sender, EventArgs e)
	{
		_toastTimer.Stop ();
		Ui (delegate {
			_view.ToastText = null;
			_view.Invalidate ();
		});
	}

	private void UpdateCursor ()
	{
		bool flag = _controlEnabled && !_closing && WindowHasFocus () && _overImage;
		if (flag && !_cursorHidden) {
			System.Windows.Forms.Cursor.Hide ();
			_cursorHidden = true;
		} else if (!flag && _cursorHidden) {
			System.Windows.Forms.Cursor.Show ();
			_cursorHidden = false;
		}
	}

	private void UpdateHint ()
	{
		SetHint (_controlEnabled ? "控制中：鼠标在画面内即控制对方；Esc 释放鼠标；全屏时按 Ctrl+Esc 退出全屏。" : "已释放：可自由操作本机；按 Esc 或点“恢复控制”继续控制对方；全屏时按 Ctrl+Esc 退出全屏。");
	}

	private void OnFrame (int w, int h, byte[] bgra)
	{
		_frameCount++;
		_remoteW = w;
		_remoteH = h;
		_lastFrameTick = NetFx.TickCount64;
		_stallNotified = false;
		Ui (delegate {
			_view.SetImage (w, h, bgra);
			_client.MarkFrameConsumed ();
		});
		if (!_qualitySent) {
			_qualitySent = true;
			_client.SendSupportedDecodingAsync (Protocol.CodecAbility.None, Protocol.CodecAbility.None);
			ApplyLevel (_level, send: true);
		}
		if (_fpsSw.ElapsedMilliseconds >= 1000) {
			int lastFps = (int)((double)_frameCount * 1000.0 / (double)_fpsSw.ElapsedMilliseconds);
			_frameCount = 0;
			_fpsSw.Restart ();
			_lastFps = lastFps;
			_lastFpsUpdate = NetFx.TickCount64;
			_resText = $"{w}x{h}";
			Ui (RefreshStats);
		}
	}

	private void OnDisconnected ()
	{
		Ui (delegate {
			_view.SetImage (0, 0, null);
		});
		if (!_closing) {
			_reconnectTries = 0;
			// Disconnected 来自接收线程：Forms.Timer 必须在 UI 线程 Start，
			// 否则计时器窗口脱离消息泵，ReconnectTick 永不触发。
			Ui (delegate { if (!_closing) _reconnectTimer.Start (); });
			if (NetFx.TickCount64 < _restartUntil) {
				SetHint ("对方正在重启，等待重新连接…");
			} else {
				SetHint ("连接中断，正在自动重连…");
			}
		}
	}

	private void RestartRemote ()
	{
		if (MessageBox.Show (this, "确定要重启对方电脑吗？", "远程重启", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) == DialogResult.Yes) {
			_restartUntil = NetFx.TickCount64 + 300000;
			_client.SendControlActionAsync (Protocol.ControlActionId.RemoteRestart);
			SetHint ("已发送重启指令，正在等待对方重启…");
		}
	}

	private async void ReconnectTick (object? sender, EventArgs e)
	{
		if (_closing) {
			_reconnectTimer.Stop ();
		} else {
			if (_reconnecting) {
				return;
			}
			_reconnecting = true;
			_reconnectTries++;
			try {
				SetHint ($"正在重连（第 {_reconnectTries} 次）…");
				if (await _client.ConnectAsync (_host, _port, _password)) {
					_reconnectTimer.Stop ();
					_reconnectTries = 0;
					_qualitySent = false;
					_lastFrameTick = NetFx.TickCount64;
					_stallNotified = false;
					SetHint ("已重新连接。");
					UpdateHint ();
				}
			} catch {
			} finally {
				_reconnecting = false;
			}
			if (_reconnectTries >= 20) {
				_reconnectTimer.Stop ();
				SetHint ($"重连失败（已尝试 {20} 次），请手动重新连接。", Color.DarkRed);
			}
		}
	}

	private void SetHint (string text, Color? back = null)
	{
		Ui (delegate {
			_lblHint.Text = text;
			_lblHint.BackColor = back ?? Color.FromArgb (255, 24, 24, 24);
		});
	}

	private void Ui (Action a)
	{
		try {
			if (!_closing && !base.IsDisposed && base.IsHandleCreated) {
				BeginInvoke (a);
			}
		} catch {
		}
	}

	private void BtnSendFile_Click (object? sender, EventArgs e)
	{
		using OpenFileDialog openFileDialog = new OpenFileDialog {
			Title = "选择要发送给主机的文件"
		};
		if (openFileDialog.ShowDialog (this) == DialogResult.OK) {
			_client.SendFileAsync (openFileDialog.FileName);
		}
	}

	private bool AskAcceptFile (string name, long size)
	{
		if (!base.InvokeRequired) {
			return ShowFileDialog (name, size);
		}
		Task<bool> task = Task.Run (() => (bool)Invoke ((Func<bool>)(() => ShowFileDialog (name, size))));
		return task.Wait (90000) && task.Result;
	}

	private bool ShowFileDialog (string name, long size)
	{
		DialogResult dialogResult = MessageBox.Show ($"主机请求发送文件：\n{name}\n大小：{(double)size / 1024.0:F1} KB\n\n是否接收？", "文件接收请求", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
		return dialogResult == DialogResult.Yes;
	}

	private void ToNorm (int sx, int sy, out ushort xNorm, out ushort yNorm)
	{
		Rectangle imageRect = _view.GetImageRect ();
		if (imageRect.Width <= 0 || imageRect.Height <= 0) {
			xNorm = (yNorm = 0);
			return;
		}
		Point point = _view.PointToScreen (imageRect.Location);
		float value = (float)(sx - point.X) / (float)imageRect.Width;
		float value2 = (float)(sy - point.Y) / (float)imageRect.Height;
		value = NetFx.Clamp (value, 0f, 1f);
		value2 = NetFx.Clamp (value2, 0f, 1f);
		xNorm = (ushort)NetFx.Clamp ((int)Math.Round (value * 65535f), 0, 65535);
		yNorm = (ushort)NetFx.Clamp ((int)Math.Round (value2 * 65535f), 0, 65535);
	}

	private bool IsOverImage (int sx, int sy)
	{
		Rectangle imageRect = _view.GetImageRect ();
		if (imageRect.Width <= 0 || imageRect.Height <= 0) {
			return false;
		}
		Point point = _view.PointToScreen (imageRect.Location);
		return sx >= point.X && sx < point.X + imageRect.Width && sy >= point.Y && sy < point.Y + imageRect.Height;
	}

	protected override void OnActivated (EventArgs e)
	{
		base.OnActivated (e);
		UpdateCursor ();
	}

	protected override void OnDeactivate (EventArgs e)
	{
		base.OnDeactivate (e);
		// 失焦后本地收不到 keyup/mouseup，立即向被控端补发，防止按键卡住
		ReleaseAllInputs ();
		_view.SetCursorScreen (null);
		UpdateCursor ();
	}

	protected override void OnFormClosing (FormClosingEventArgs e)
	{
		_closing = true;
		ReleaseAllInputs ();
		_adaptTimer.Stop ();
		_adaptTimer.Dispose ();
		_toastTimer.Stop ();
		_toastTimer.Dispose ();
		_reconnectTimer.Stop ();
		_reconnectTimer.Dispose ();
		_audio?.Stop ();
		_audio?.Dispose ();
		if (_cursorHidden) {
			System.Windows.Forms.Cursor.Show ();
			_cursorHidden = false;
		}
		_hook?.Dispose ();
		_hook = null;
		if (_rttHandler != null) {
			_client.RttMeasured -= _rttHandler;
		}
		_client.Disconnected -= OnDisconnected;
		_client.FrameReceived -= OnFrame;
		_view.Detach ();
		_clip.Dispose ();
		// Disconnect 只 Cancel/Close，端口转发监听、帧缓冲和 CTS 要 Dispose 才确定性释放；
		// 此时 _closing=true 且重连定时器已停，实例不会被重连复用，可安全完整释放。
		_client.Dispose ();
		base.OnFormClosing (e);
	}
}
