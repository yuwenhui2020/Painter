using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace desk;

public class Form1 : Form
{
	private TextBox txtHost;

	private TextBox txtPort;

	private TextBox txtPassword;

	private TextBox txtLocalPwd;

	private TextBox txtToken;

	private CheckBox chkRelay;

	private CheckBox chkRemember;

	private ComboBox cmbSaved;

	private Label lblInfo;

	private Button btnUac;

	private TextBox txtWebPort;

	private Button btnWeb;

	private Label lblWeb;

	private WebServer? _web;

	private FtpServer _ftp;

	private RemoteDesktopServer? _webSrv;

	private int _webPort = 9090;

	private volatile bool _webStarting;

	private RelayServer? _relay;

	private RemoteDesktopServer? _hostServer;

	private Button btnHost;

	private Label lblHostStatus;

	private ComboBox cmbHostMode;

	private ComboBox cmbHostRes;

	private NumericUpDown nudHostQuality;

	private ComboBox cmbHostFps;

	private bool _hostApplying;

	private bool _hostViewerOnline;

	private int _hostFps;

	private long _hostBps;

	private NotifyIcon _tray;

	private ContextMenuStrip _trayMenu;

	private ToolStripMenuItem _miShow;

	private ToolStripMenuItem _miCanvas;

	private ToolStripMenuItem _miWeb;

	private ToolStripMenuItem _miHost;

	private ToolStripMenuItem _miPwd;

	private ToolStripMenuItem _miKill;

	private ToolStripMenuItem _miShowImg;

	private ToolStripMenuItem _miNet;

	private bool _allowShow;

	private volatile bool _exiting;

	private bool _passwordEnabled = true;

	private IContainer components;

	private bool _hostStarting;

	public IHostBridge? Host { get; set; }

	private string EffectivePassword
	{
		get
		{
			if (!_passwordEnabled)
			{
				return "";
			}
			return txtLocalPwd.Text ?? "";
		}
	}

	public Form1()
	{
		Form1 form = this;
		StartupTiming.Mark("Form1 构造进入", uiThread: true);
		InitializeComponent();
		Features.RegisterUiAnchor(this);
		StartupTiming.Mark("Form1.InitializeComponent 完成", uiThread: true);
		base.Icon = AppIcon.Get();
		StartupTiming.Mark("AppIcon.Get 完成", uiThread: true);
		BuildUi();
		StartupTiming.Mark("BuildUi 完成", uiThread: true);
		Settings.Load();
		txtLocalPwd.Text = Settings.LocalHostPassword;
		txtPort.Text = Settings.HostPort.ToString();
		RefreshSavedList();
		UpdateInfo();
		RefreshUacButton();
		_passwordEnabled = Settings.RemotePasswordEnabled;
		StartupTiming.Mark("Settings/列表/信息 刷新完成", uiThread: true);
		BuildTray();
		StartupTiming.Mark("BuildTray 完成", uiThread: true);
		ProcessKiller.Init();
		ProcessKiller.Enabled = Settings.KillEnabled;
		if (!base.IsHandleCreated)
		{
			_ = base.Handle;
		}
		StartupTiming.Mark("Form1 构造完成（句柄已建）", uiThread: true);
		WebControl.Register(ApplyWebEnabled);
		Timer timer = new Timer
		{
			Interval = 300
		};
		components.Add(timer);
		timer.Tick += delegate
		{
			timer.Stop();
			StartupTiming.Mark("AutoStartup 定时器触发", uiThread: true);
			form.AutoStartup();
			StartupTiming.Mark("AutoStartup 返回（UI 恢复响应）", uiThread: true);
		};
		timer.Start();
	}

	private string ApplyWebEnabled(bool enabled)
	{
		if (base.InvokeRequired)
		{
			string text = "";
			try
			{
				Invoke((Action)delegate
				{
					text = ApplyWebEnabled(enabled);
				});
				return text;
			}
			catch (Exception ex)
			{
				return "操作失败：" + ex.Message;
			}
		}
		if (base.IsDisposed || !base.IsHandleCreated)
		{
			return "控制面板尚未就绪，请稍后重试";
		}
		if (enabled)
		{
			if (_web != null || _webStarting)
			{
				return "Web 控制台已在运行";
			}
			BtnWeb_Click(this, EventArgs.Empty);
			return "正在启动 Web 控制台（端口 " + WebStatus.Port + "）";
		}
		if (_webStarting)
		{
			return "正在启动中，请稍后再试";
		}
		if (_web == null)
		{
			return "Web 控制台未运行";
		}
		StopWeb();
		return "已停止 Web 控制台";
	}

	private void AutoStartup()
	{
		try
		{
			LanDiscovery.StartResponder((Settings.HostPort > 0) ? Settings.HostPort : 9090);
			StartupTiming.Mark("  LanDiscovery 常驻应答器已启动（UDP 21118）", uiThread: true);
			try
			{
				MchatEcoBridge.Start();
				MchatEcoCommands.Start();
				StartupTiming.Mark("  MchatEcoBridge 生态桥已启动（UDP 45777 / TCP 45778）", uiThread: true);
			}
			catch (Exception ex)
			{
				StartupTiming.Mark("  MchatEcoBridge 启动失败（已忽略）: " + ex.Message);
			}
			bool flag = UacSupport.IsInstalled();
			StartupTiming.Mark("  UacSupport.IsInstalled = " + flag, uiThread: true);
			if (!flag)
			{
				StartHostServer();
				StartupTiming.Mark("  StartHostServer 完成（DXGI 抓屏+编码器初始化）", uiThread: true);
			}
			WebStatus.MarkStopped();
			StartupTiming.Mark("  Web 控制台保持关闭（默认）", uiThread: true);
			StartFtp();
			StartupTiming.Mark("  StartFtp 完成", uiThread: true);
			int fwWebPort = ((_webPort > 0) ? _webPort : 9090);
			Task.Run(delegate
			{
				try
				{
					FirewallSetup.EnsureNetworkAccess(fwWebPort, 2121, 2122, 2152, force: false, WebUiLog);
				}
				catch (Exception ex3)
				{
					AppLog.Write("Firewall", "EnsureNetworkAccess 失败（已忽略，不影响画板）: " + ex3.Message);
				}
			});
			RefreshTrayMenu();
			StartupTiming.Mark("  H264 自动探测已跳过（可用 -h264probe 手动诊断）", uiThread: true);
		}
		catch (Exception ex2)
		{
			AppLog.Write("启动", "自动启动失败：" + ex2.Message);
		}
	}

	protected override void SetVisibleCore(bool value)
	{
		base.SetVisibleCore(_allowShow && value);
	}

	private void ShowMain()
	{
		_allowShow = true;
		Show();
		base.WindowState = FormWindowState.Normal;
		base.ShowInTaskbar = true;
		BringToFront();
		Activate();
	}

	private void HideToTray()
	{
		_allowShow = false;
		Hide();
	}

	private void BuildTray()
	{
		ShowController.Log = delegate(string s)
		{
			try
			{
				AppLog.Write("Show", s);
			}
			catch
			{
			}
		};
		_miShow = new ToolStripMenuItem("远程控制面板")
		{
			Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold)
		};
		_miShow.Click += delegate
		{
			ShowMain();
		};
		_miCanvas = new ToolStripMenuItem("退出画板");
		_miCanvas.Click += delegate
		{
			ToggleCanvas();
		};
		_miWeb = new ToolStripMenuItem("启动 Web 远控");
		_miWeb.Click += delegate
		{
			ToggleWeb();
		};
		_miHost = new ToolStripMenuItem("启动被控");
		_miHost.Click += delegate
		{
			ToggleHost();
		};
		_miPwd = new ToolStripMenuItem("启用访问密码");
		_miPwd.Click += delegate
		{
			TogglePassword();
		};
		_miKill = new ToolStripMenuItem("启用后台查杀")
		{
			Checked = ProcessKiller.Enabled
		};
		_miKill.Click += delegate
		{
			SetKillEnabled(!ProcessKiller.Enabled);
		};
		_miShowImg = new ToolStripMenuItem("图片展示")
		{
			Checked = ShowController.IsOpen
		};
		_miShowImg.Click += delegate
		{
			if (ShowController.IsOpen)
			{
				ShowController.Close();
			}
			else
			{
				ShowController.Open();
			}
		};
		_miNet = new ToolStripMenuItem("网络访问权限（管理员）");
		_miNet.Click += delegate
		{
			RequestNetworkAccess();
		};
		ToolStripMenuItem toolStripMenuItem = new ToolStripMenuItem("退出");
		toolStripMenuItem.Click += delegate
		{
			_exiting = true;
			if (_tray != null)
			{
				_tray.Visible = false;
			}
			bool flag = false;
			try
			{
				if (Host != null)
				{
					Host.Shutdown();
					flag = true;
				}
			}
			catch
			{
			}
			if (!flag)
			{
				Environment.Exit(0);
			}
			Task.Delay(8000).ContinueWith(delegate
			{
				try
				{
					Environment.Exit(0);
				}
				catch
				{
				}
			});
		};
		_trayMenu = new ContextMenuStrip();
		_trayMenu.Items.AddRange(new ToolStripItem[10]
		{
			_miShow,
			_miCanvas,
			new ToolStripSeparator(),
			_miWeb,
			_miHost,
			_miPwd,
			_miKill,
			_miShowImg,
			_miNet,
			new ToolStripSeparator()
		});
		_trayMenu.Items.Add(toolStripMenuItem);
		_trayMenu.Opening += delegate
		{
			RefreshTrayMenu();
		};
		_tray = new NotifyIcon
		{
			Icon = AppIcon.Get(),
			Text = "painter",
			Visible = true,
			ContextMenuStrip = _trayMenu
		};
		_tray.MouseClick += delegate(object? sender, MouseEventArgs e)
		{
			if (e.Button == MouseButtons.Left)
			{
				ShowMain();
			}
		};
	}

	private void RefreshTrayMenu()
	{
		if (_trayMenu == null || _miWeb == null || _miHost == null || _miPwd == null)
		{
			return;
		}
		if (_miCanvas != null)
		{
			bool flag = false;
			try
			{
				flag = Host != null && Host.IsCanvasVisible;
			}
			catch
			{
			}
			_miCanvas.Visible = Host != null;
			_miCanvas.Text = (flag ? "退出画板" : "开启画板");
		}
		_miWeb.Text = ((_web != null) ? "停止 Web 远控" : "启动 Web 远控");
		if (UacSupport.IsInstalled())
		{
			_miHost.Text = "被控端（由系统服务接管）";
			_miHost.Enabled = false;
		}
		else
		{
			_miHost.Text = ((_hostServer != null) ? "停止被控" : "启动被控");
			_miHost.Enabled = true;
		}
		bool visible = !string.IsNullOrEmpty(txtLocalPwd.Text);
		_miPwd.Visible = visible;
		_miPwd.Text = (_passwordEnabled ? "关闭访问密码" : "启用访问密码");
		_miPwd.Checked = _passwordEnabled;
		if (_miKill != null)
		{
			_miKill.Checked = ProcessKiller.Enabled;
			_miKill.Text = (ProcessKiller.Enabled ? "停止后台查杀" : "启用后台查杀");
		}
		if (_miShowImg != null)
		{
			_miShowImg.Checked = ShowController.IsOpen;
			_miShowImg.Text = (ShowController.IsOpen ? "关闭图片展示" : "图片展示");
		}
	}

	private void SetKillEnabled(bool on)
	{
		ProcessKiller.Enabled = on;
		Settings.KillEnabled = on;
		Settings.Save();
		RefreshTrayMenu();
	}

	private void RequestNetworkAccess()
	{
		int port = 9090;
		if (int.TryParse(txtWebPort.Text.Trim(), out var result) && result > 0 && result <= 65535)
		{
			port = result;
		}
		_webPort = port;
		WebStatus.SetPort(port);
		WebUiLog("正在申请网络访问权限…");
		Task.Run(delegate
		{
			try
			{
				FirewallSetup.EnsureNetworkAccess(port, 2121, 2122, 2152, force: true, WebUiLog);
			}
			catch (Exception ex)
			{
				AppLog.Write("Firewall", "EnsureNetworkAccess 失败（已忽略，不影响画板）: " + ex.Message);
			}
		});
	}

	private void ToggleCanvas()
	{
		if (Host != null)
		{
			try
			{
				Host.SetCanvasVisible(!Host.IsCanvasVisible);
			}
			catch (Exception ex)
			{
				AppLog.Write("画板", "切换画板显示失败：" + ex.Message);
			}
			RefreshTrayMenu();
		}
	}

	private void ToggleWeb()
	{
		if (_web != null)
		{
			StopWeb();
		}
		else
		{
			BtnWeb_Click(this, EventArgs.Empty);
		}
		RefreshTrayMenu();
	}

	private void ToggleHost()
	{
		if (_hostServer != null)
		{
			StopHostServer();
		}
		else
		{
			StartHostServer();
		}
		RefreshTrayMenu();
	}

	private void TogglePassword()
	{
		if (!string.IsNullOrEmpty(txtLocalPwd.Text))
		{
			_passwordEnabled = !_passwordEnabled;
			Settings.RemotePasswordEnabled = _passwordEnabled;
			Settings.Save();
			ApplyPassword();
			RefreshTrayMenu();
			UpdateInfo();
		}
	}

	private void ApplyPassword()
	{
		string effectivePassword = EffectivePassword;
		try
		{
			if (_hostServer != null)
			{
				_hostServer.ServerPassword = effectivePassword;
			}
			if (_webSrv != null)
			{
				_webSrv.ServerPassword = effectivePassword;
			}
			if (_web != null)
			{
				_web.Password = effectivePassword;
			}
		}
		catch
		{
		}
	}

	public void RequestShutdownCleanup()
	{
		_exiting = true;
		StopExternalServices();
		Action action = delegate
		{
			try
			{
				if (_tray != null)
				{
					_tray.Visible = false;
				}
			}
			catch
			{
			}
			try
			{
				Close();
			}
			catch
			{
			}
			try
			{
				Dispose();
			}
			catch
			{
			}
		};
		try
		{
			if (!base.IsDisposed && base.IsHandleCreated)
			{
				if (base.InvokeRequired)
				{
					BeginInvoke(action);
				}
				else
				{
					action();
				}
			}
		}
		catch
		{
		}
	}

	private void StopExternalServices()
	{
		try
		{
			LanDiscovery.StopResponder();
		}
		catch
		{
		}
		try
		{
			MchatEcoCommands.Stop();
		}
		catch
		{
		}
		try
		{
			MchatEcoBridge.Stop();
		}
		catch
		{
		}
		try
		{
			_relay?.Stop();
		}
		catch
		{
		}
		try
		{
			_relay = null;
		}
		catch
		{
		}
		try
		{
			_hostServer?.Dispose();
		}
		catch
		{
		}
		try
		{
			_hostServer = null;
		}
		catch
		{
		}
		try
		{
			_webSrv?.Dispose();
		}
		catch
		{
		}
		try
		{
			_webSrv = null;
		}
		catch
		{
		}
		try
		{
			_web?.Dispose();
		}
		catch
		{
		}
		try
		{
			_web = null;
		}
		catch
		{
		}
		try
		{
			_ftp?.Dispose();
		}
		catch
		{
		}
		try
		{
			_ftp = null;
		}
		catch
		{
		}
		try
		{
			WebControl.Unregister();
		}
		catch
		{
		}
	}

	protected override void OnFormClosing(FormClosingEventArgs e)
	{
		if (!_exiting && _tray != null && e.CloseReason == CloseReason.UserClosing)
		{
			e.Cancel = true;
			HideToTray();
		}
		else
		{
			base.OnFormClosing(e);
		}
	}

	protected override void OnFormClosed(FormClosedEventArgs e)
	{
		try
		{
			if (_tray != null)
			{
				_tray.Visible = false;
				_tray.Dispose();
				_tray = null;
			}
		}
		catch
		{
		}
		base.OnFormClosed(e);
	}

	private void BuildUi()
	{
		Text = "远程桌面 (C# Remote Desktop)";
		base.AutoScaleMode = AutoScaleMode.Dpi;
		base.AutoScaleDimensions = new SizeF(96f, 96f);
		base.ClientSize = new Size(396, 706);
		base.FormBorderStyle = FormBorderStyle.FixedDialog;
		base.MaximizeBox = false;
		base.StartPosition = FormStartPosition.CenterScreen;
		GroupBox groupBox = new GroupBox
		{
			Text = "本机（被控端）",
			Location = new Point(12, 12),
			Size = new Size(372, 176)
		};
		Label label = new Label
		{
			Text = "被控密码:",
			Location = new Point(16, 24),
			AutoSize = true
		};
		txtLocalPwd = new TextBox
		{
			Location = new Point(90, 21),
			Size = new Size(262, 23),
			PasswordChar = '*'
		};
		txtLocalPwd.TextChanged += delegate
		{
			Settings.LocalHostPassword = txtLocalPwd.Text ?? "";
			ApplyPassword();
			RefreshTrayMenu();
			UpdateInfo();
		};
		lblHostStatus = new Label
		{
			Location = new Point(16, 50),
			Size = new Size(340, 20),
			Text = "未启动",
			ForeColor = SystemColors.GrayText
		};
		cmbHostMode = new ComboBox
		{
			Location = new Point(52, 79),
			Size = new Size(106, 23),
			DropDownStyle = ComboBoxStyle.DropDownList
		};
		cmbHostMode.Items.AddRange(new object[3] { "JPEG区域", "原始RGB", "JPEG全帧" });
		cmbHostMode.SelectedIndex = 0;
		cmbHostMode.SelectedIndexChanged += HostQualityChanged;
		cmbHostRes = new ComboBox
		{
			Location = new Point(210, 79),
			Size = new Size(64, 23),
			DropDownStyle = ComboBoxStyle.DropDownList
		};
		cmbHostRes.Items.AddRange(new object[6] { "640", "800", "1024", "1280", "1600", "1920" });
		cmbHostRes.SelectedIndex = 5;
		cmbHostRes.SelectedIndexChanged += HostQualityChanged;
		nudHostQuality = new NumericUpDown
		{
			Location = new Point(320, 79),
			Size = new Size(40, 23),
			Minimum = 1m,
			Maximum = 100m,
			Value = 70m
		};
		nudHostQuality.ValueChanged += HostQualityChanged;
		cmbHostFps = new ComboBox
		{
			Location = new Point(52, 109),
			Size = new Size(60, 23),
			DropDownStyle = ComboBoxStyle.DropDownList
		};
		cmbHostFps.Items.AddRange(new object[6] { "5", "10", "15", "20", "30", "60" });
		cmbHostFps.SelectedIndex = 5;
		cmbHostFps.SelectedIndexChanged += HostQualityChanged;
		groupBox.Controls.AddRange(new Control[12]
		{
			label,
			txtLocalPwd,
			lblHostStatus,
			new Label
			{
				Text = "编码",
				Location = new Point(16, 82),
				AutoSize = true
			},
			cmbHostMode,
			new Label
			{
				Text = "分辨率",
				Location = new Point(164, 82),
				AutoSize = true
			},
			cmbHostRes,
			new Label
			{
				Text = "质量",
				Location = new Point(286, 82),
				AutoSize = true
			},
			nudHostQuality,
			new Label
			{
				Text = "帧率",
				Location = new Point(16, 112),
				AutoSize = true
			},
			cmbHostFps,
			new Label
			{
				Text = "权限",
				Location = new Point(164, 112),
				AutoSize = true
			}
		});
		Control.ControlCollection controls = groupBox.Controls;
		Control[] controls2 = HostPermChecks();
		controls.AddRange(controls2);
		GroupBox groupBox2 = new GroupBox
		{
			Text = "控制其他设备",
			Location = new Point(12, 196),
			Size = new Size(372, 200)
		};
		Label label2 = new Label
		{
			Text = "已保存:",
			Location = new Point(16, 20),
			AutoSize = true
		};
		cmbSaved = new ComboBox
		{
			Location = new Point(80, 17),
			Size = new Size(280, 23),
			DropDownStyle = ComboBoxStyle.DropDownList,
			DropDownWidth = 340
		};
		cmbSaved.SelectedIndexChanged += CmbSaved_SelectedIndexChanged;
		Label label3 = new Label
		{
			Text = "地址:",
			Location = new Point(16, 50),
			AutoSize = true
		};
		txtHost = new TextBox
		{
			Text = "127.0.0.1",
			Location = new Point(80, 47),
			Size = new Size(280, 23)
		};
		Label label4 = new Label
		{
			Text = "端口:",
			Location = new Point(16, 80),
			AutoSize = true
		};
		txtPort = new TextBox
		{
			Text = "9000",
			Location = new Point(80, 77),
			Size = new Size(120, 23)
		};
		Label label5 = new Label
		{
			Text = "远程密码:",
			Location = new Point(16, 110),
			AutoSize = true
		};
		txtPassword = new TextBox
		{
			Location = new Point(80, 107),
			Size = new Size(280, 23),
			PasswordChar = '*'
		};
		Label label6 = new Label
		{
			Text = "中继令牌:",
			Location = new Point(16, 140),
			AutoSize = true
		};
		txtToken = new TextBox
		{
			Text = "",
			Location = new Point(80, 137),
			Size = new Size(280, 23)
		};
		chkRelay = new CheckBox
		{
			Text = "通过中继连接(跨 NAT)",
			Location = new Point(16, 170),
			AutoSize = true
		};
		chkRemember = new CheckBox
		{
			Text = "记住密码",
			Location = new Point(240, 170),
			AutoSize = true
		};
		groupBox2.Controls.AddRange(new Control[12]
		{
			label2, cmbSaved, label3, txtHost, label4, txtPort, label5, txtPassword, label6, txtToken,
			chkRelay, chkRemember
		});
		base.Controls.AddRange(new Control[2] { groupBox, groupBox2 });
		btnHost = new Button
		{
			Text = "启动被控",
			Location = new Point(12, 404),
			Size = new Size(180, 36),
			BackColor = Color.LightSteelBlue
		};
		btnHost.Click += BtnHost_Click;
		Button button = new Button
		{
			Text = "连接其他设备",
			Location = new Point(204, 404),
			Size = new Size(180, 36),
			BackColor = Color.LightGreen
		};
		button.Click += BtnConnect_Click;
		Button button2 = new Button
		{
			Text = "启动中继",
			Location = new Point(12, 448),
			Size = new Size(180, 32),
			BackColor = Color.Khaki
		};
		button2.Click += BtnRelay_Click;
		btnUac = new Button
		{
			Text = "安装 UAC 支持",
			Location = new Point(204, 448),
			Size = new Size(180, 32),
			BackColor = Color.LightSalmon
		};
		btnUac.Click += BtnUac_Click;
		base.Controls.AddRange(new Control[4] { btnHost, button, button2, btnUac });
		lblInfo = new Label
		{
			Location = new Point(12, 488),
			Size = new Size(372, 36),
			Text = "正在初始化…",
			Font = new Font("微软雅黑", 8f),
			ForeColor = SystemColors.GrayText
		};
		base.Controls.Add(lblInfo);
		Button button3 = new Button
		{
			Text = "查看运行日志",
			Location = new Point(12, 562),
			Size = new Size(372, 32),
			BackColor = Color.LightGray
		};
		button3.Click += delegate
		{
			new LogForm().Show(this);
		};
		base.Controls.Add(button3);
		Button button4 = new Button
		{
			Text = "局域网设备",
			Location = new Point(12, 528),
			Size = new Size(372, 28),
			BackColor = Color.LightCyan
		};
		button4.Click += BtnLan_Click;
		base.Controls.Add(button4);
		GroupBox groupBox3 = new GroupBox
		{
			Text = "Web 远控（浏览器）",
			Location = new Point(12, 600),
			Size = new Size(372, 92)
		};
		Label label7 = new Label
		{
			Text = "端口:",
			Location = new Point(16, 22),
			AutoSize = true
		};
		txtWebPort = new TextBox
		{
			Text = 9090.ToString(),
			Location = new Point(70, 19),
			Size = new Size(90, 23)
		};
		btnWeb = new Button
		{
			Text = "启动 Web 远控",
			Location = new Point(170, 17),
			Size = new Size(190, 30),
			BackColor = Color.LightSteelBlue
		};
		btnWeb.Click += BtnWeb_Click;
		lblWeb = new Label
		{
			Location = new Point(16, 54),
			Size = new Size(340, 32),
			Text = "未启动（浏览器访问 http://<IP>:" + 9090 + "/）",
			Font = new Font("微软雅黑", 8f),
			ForeColor = SystemColors.GrayText
		};
		groupBox3.Controls.AddRange(new Control[4] { label7, txtWebPort, btnWeb, lblWeb });
		base.Controls.Add(groupBox3);
	}

	private void RefreshSavedList()
	{
		cmbSaved.Items.Clear();
		cmbSaved.Items.Add("（手动输入新设备）");
		foreach (SavedConnection savedConnection in Settings.SavedConnections)
		{
			string text = (savedConnection.UseRelay ? " [中继]" : "");
			string text2 = (string.IsNullOrEmpty(savedConnection.Password) ? "" : " ●");
			cmbSaved.Items.Add($"{savedConnection.Host}:{savedConnection.Port}{text}{text2}");
		}
		cmbSaved.SelectedIndex = 0;
	}

	private void CmbSaved_SelectedIndexChanged(object? sender, EventArgs e)
	{
		int num = cmbSaved.SelectedIndex - 1;
		if (num >= 0 && num < Settings.SavedConnections.Count)
		{
			SavedConnection savedConnection = Settings.SavedConnections[num];
			txtHost.Text = savedConnection.Host;
			txtPort.Text = savedConnection.Port.ToString();
			txtPassword.Text = savedConnection.Password;
			chkRelay.Checked = savedConnection.UseRelay;
			txtToken.Text = savedConnection.Token;
		}
	}

	private void UpdateInfo()
	{
		StringBuilder stringBuilder = new StringBuilder();
		try
		{
			List<string> list = (from ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList
				where ip.AddressFamily == AddressFamily.InterNetwork
				select ip.ToString()).ToList();
			stringBuilder.AppendLine("本机 IP：" + ((list.Count > 0) ? string.Join("，", list) : "127.0.0.1"));
		}
		catch
		{
			stringBuilder.AppendLine("本机 IP：127.0.0.1");
		}
		stringBuilder.Append(string.IsNullOrEmpty(Settings.LocalHostPassword) ? "未设被控密码" : "已设被控密码");
		lblInfo.Text = stringBuilder.ToString();
	}

	private int ParsePort()
	{
		if (!int.TryParse(txtPort.Text.Trim(), out var result) || result <= 0 || result > 65535)
		{
			MessageBox.Show("端口应为 1~65535 之间的整数。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
			return -1;
		}
		return result;
	}

	private void RefreshUacButton()
	{
		btnUac.Text = (UacSupport.IsInstalled() ? "卸载 UAC 支持" : "安装 UAC 支持");
	}

	private void BtnUac_Click(object? sender, EventArgs e)
	{
		if (UacSupport.IsInstalled())
		{
			if (MessageBox.Show("卸载系统级被控端？\n卸载后将无法远程看到 / 控制 UAC 界面，恢复为普通被控模式。", "UAC 支持", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
			{
				return;
			}
			(bool, string) tuple = UacSupport.Uninstall();
			MessageBox.Show(tuple.Item1 ? "已卸载，已恢复普通被控模式。" : ("卸载失败：" + tuple.Item2), "UAC 支持", MessageBoxButtons.OK, tuple.Item1 ? MessageBoxIcon.Asterisk : MessageBoxIcon.Hand);
		}
		else
		{
			(bool, string) tuple2 = UacSupport.Install();
			MessageBox.Show(tuple2.Item1 ? "已安装并启动系统级被控端。\n现在远程时可以显示并控制 UAC 界面了。\n\n（该服务会随开机自动启动；被控端由它接管，端口与被控密码沿用当前设置。）" : ("安装失败：" + tuple2.Item2), "UAC 支持", MessageBoxButtons.OK, tuple2.Item1 ? MessageBoxIcon.Asterisk : MessageBoxIcon.Hand);
		}
		RefreshUacButton();
		UpdateInfo();
	}

	private void BtnHost_Click(object? sender, EventArgs e)
	{
		if (_hostServer != null)
		{
			StopHostServer();
		}
		else
		{
			StartHostServer();
		}
	}

	private async void StartHostServer()
	{
		if (_hostServer != null || _hostStarting)
		{
			return;
		}
		_hostStarting = true;
		try
		{
			int num = ParsePort();
			if (num < 0)
			{
				return;
			}
			LanDiscovery.StartResponder(num);
			if (UacSupport.IsInstalled())
			{
				MessageBox.Show("已启用系统级被控端（UAC 支持），本机由 SYSTEM 进程共享屏幕，\n无需（也不能）再在这里启动被控端。\n\n如需改用普通模式，请先点“卸载 UAC 支持”。", "被控端已由系统服务接管", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				return;
			}
			Settings.LocalHostPassword = txtLocalPwd.Text;
			Settings.HostPort = num;
			Settings.Save();
			Settings.SaveHostConfig();
			UpdateInfo();
			RemoteDesktopServer remoteDesktopServer = new RemoteDesktopServer(num)
			{
				ServerPassword = EffectivePassword
			};
			if (chkRelay.Checked)
			{
				string text = txtToken.Text.Trim();
				if (string.IsNullOrEmpty(text))
				{
					MessageBox.Show("中继模式需要填写“中继令牌”。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
					remoteDesktopServer.Dispose();
					return;
				}
				try
				{
					await remoteDesktopServer.StartViaRelayAsync(txtHost.Text.Trim(), num, text);
				}
				catch (Exception ex)
				{
					MessageBox.Show("连接中继失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
					remoteDesktopServer.Dispose();
					return;
				}
			}
			else
			{
				try
				{
					remoteDesktopServer.Start();
				}
				catch (Exception ex2)
				{
					MessageBox.Show("启动被控端失败：" + ex2.Message + "\n请确认端口未被占用或以管理员权限运行。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
					remoteDesktopServer.Dispose();
					return;
				}
			}
			_hostServer = remoteDesktopServer;
			WireHostServer(remoteDesktopServer);
			Protocol.QualityConfig q = HostCurrentConfig();
			remoteDesktopServer.Mode = q.Mode;
			remoteDesktopServer.JpegQuality = q.Quality;
			remoteDesktopServer.MaxFrameWidth = q.MaxWidth;
			remoteDesktopServer.TargetFps = q.Fps;
			remoteDesktopServer.ResyncIntervalSec = q.ResyncSec;
			remoteDesktopServer.BlockSize = q.BlockSize;
			SyncHostQualityToUi(q);
			RefreshHostUi();
		}
		finally
		{
			_hostStarting = false;
		}
	}

	private void WireHostServer(RemoteDesktopServer srv)
	{
		srv.Stats += delegate(int fps, long bps)
		{
			Ui(delegate
			{
				_hostFps = fps;
				_hostBps = bps;
				RefreshHostUi();
			});
		};
		srv.QualityChanged += delegate(Protocol.QualityConfig q)
		{
			Ui(delegate
			{
				SyncHostQualityToUi(q);
			});
		};
		srv.ViewerConnected += delegate
		{
			Ui(delegate
			{
				_hostViewerOnline = true;
				RefreshHostUi();
			});
		};
		srv.ViewerDisconnected += delegate
		{
			Ui(delegate
			{
				_hostViewerOnline = false;
				_hostFps = 0;
				_hostBps = 0L;
				RefreshHostUi();
			});
		};
	}

	private void Ui(Action a)
	{
		if (base.IsDisposed || !base.IsHandleCreated)
		{
			return;
		}
		try
		{
			if (base.InvokeRequired)
			{
				BeginInvoke(a);
			}
			else
			{
				a();
			}
		}
		catch
		{
		}
	}

	private void RefreshHostUi()
	{
		if (btnHost == null || lblHostStatus == null)
		{
			return;
		}
		if (_hostServer == null)
		{
			btnHost.Text = "启动被控";
			btnHost.BackColor = Color.LightSteelBlue;
			lblHostStatus.Text = "未启动（无人连接时不抓屏）";
			lblHostStatus.ForeColor = SystemColors.GrayText;
			return;
		}
		btnHost.Text = "停止被控";
		btnHost.BackColor = Color.LightCoral;
		string arg = ((_hostBps >= 1000000) ? $"{(double)_hostBps / 1000000.0:F2} MB/s" : $"{_hostBps / 1000} KB/s");
		if (_hostViewerOnline)
		{
			lblHostStatus.Text = $"● 查看端已连接 · {_hostFps}fps · {arg}";
			lblHostStatus.ForeColor = Color.Green;
		}
		else
		{
			lblHostStatus.Text = "○ 已启动，等待查看端（空闲不抓屏）";
			lblHostStatus.ForeColor = Color.DimGray;
		}
	}

	private void StopHostServer()
	{
		try
		{
			_hostServer?.Dispose();
		}
		catch
		{
		}
		_hostServer = null;
		_hostViewerOnline = false;
		_hostFps = 0;
		_hostBps = 0L;
		RefreshHostUi();
	}

	private void HostQualityChanged(object? sender, EventArgs e)
	{
		if (!_hostApplying && (_hostServer != null || _webSrv != null))
		{
			Protocol.QualityConfig qualityLocal = HostCurrentConfig();
			_hostServer?.SetQualityLocal(qualityLocal);
			_webSrv?.SetQualityLocal(qualityLocal);
		}
	}

	private Protocol.QualityConfig HostCurrentConfig()
	{
		Protocol.EncodingMode[] array = new Protocol.EncodingMode[3]
		{
			Protocol.EncodingMode.JpegRegions,
			Protocol.EncodingMode.RawRegions,
			Protocol.EncodingMode.JpegFullFrame
		};
		Protocol.EncodingMode mode = array[NetFx.Clamp(cmbHostMode.SelectedIndex, 0, array.Length - 1)];
		int result = 1280;
		int.TryParse(cmbHostRes.SelectedItem?.ToString(), out result);
		int quality = (int)nudHostQuality.Value;
		int result2 = 60;
		int.TryParse(cmbHostFps.SelectedItem?.ToString(), out result2);
		return new Protocol.QualityConfig(mode, quality, result, result2, 4, 32);
	}

	private void SyncHostQualityToUi(Protocol.QualityConfig q)
	{
		_hostApplying = true;
		try
		{
			ComboBox comboBox = cmbHostMode;
			comboBox.SelectedIndex = q.Mode switch
			{
				Protocol.EncodingMode.RawRegions => 1, 
				Protocol.EncodingMode.JpegFullFrame => 2, 
				_ => 0, 
			};
			cmbHostRes.SelectedIndex = NearestComboIndex(cmbHostRes, q.MaxWidth);
			nudHostQuality.Value = NetFx.Clamp(q.Quality, (int)nudHostQuality.Minimum, (int)nudHostQuality.Maximum);
			cmbHostFps.SelectedIndex = NearestComboIndex(cmbHostFps, q.Fps);
		}
		finally
		{
			_hostApplying = false;
		}
	}

	private static int NearestComboIndex(ComboBox cb, int value)
	{
		int result = 0;
		int num = int.MaxValue;
		for (int i = 0; i < cb.Items.Count; i++)
		{
			if (int.TryParse(cb.Items[i]?.ToString(), out var result2))
			{
				int num2 = Math.Abs(result2 - value);
				if (num2 < num)
				{
					num = num2;
					result = i;
				}
			}
		}
		return result;
	}

	private CheckBox[] HostPermChecks()
	{
		(int, string, Protocol.Permission)[] array = new(int, string, Protocol.Permission)[5]
		{
			(16, "键鼠控制", Protocol.Permission.Keyboard),
			(90, "剪贴板", Protocol.Permission.Clipboard),
			(150, "文件", Protocol.Permission.File),
			(200, "屏蔽输入", Protocol.Permission.BlockInput),
			(270, "隐私模式", Protocol.Permission.PrivacyMode)
		};
		CheckBox[] array2 = new CheckBox[array.Length];
		for (int i = 0; i < array.Length; i++)
		{
			(int, string, Protocol.Permission) tuple = array[i];
			CheckBox cb = new CheckBox
			{
				Text = tuple.Item2,
				Location = new Point(tuple.Item1, 138),
				AutoSize = true,
				Checked = Features.IsAllowed(tuple.Item3)
			};
			Protocol.Permission perm = tuple.Item3;
			cb.CheckedChanged += delegate
			{
				Features.Set(perm, cb.Checked);
				_hostServer?.NotifyPermissionsChanged();
				_webSrv?.NotifyPermissionsChanged();
			};
			array2[i] = cb;
		}
		return array2;
	}

	private async void BtnLan_Click(object? sender, EventArgs e)
	{
		Button btn = (Button)sender;
		btn.Enabled = false;
		btn.Text = "正在搜索局域网…";
		try
		{
			List<LanPeer> list = await LanDiscovery.DiscoverAsync();
			if (list.Count == 0)
			{
				MessageBox.Show("局域网内未发现其它运行本程序的电脑。\n（请确认对方已启动本程序，且防火墙允许 UDP 21118。）", "局域网设备", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				return;
			}
			Form dlg = new Form
			{
				Text = "局域网设备",
				ClientSize = new Size(460, 300),
				StartPosition = FormStartPosition.CenterParent,
				FormBorderStyle = FormBorderStyle.FixedDialog,
				MaximizeBox = false
			};
			try
			{
				ListView lv = new ListView
				{
					View = View.Details,
					FullRowSelect = true,
					GridLines = true,
					Location = new Point(8, 8),
					Size = new Size(444, 250)
				};
				lv.Columns.Add("主机名", 130);
				lv.Columns.Add("IP:端口", 150);
				lv.Columns.Add("MAC", 150);
				foreach (LanPeer item in list)
				{
					ListViewItem value = new ListViewItem(item.HostName)
					{
						SubItems = 
						{
							$"{item.Ip}:{item.Port}",
							item.Mac
						},
						Tag = item
					};
					lv.Items.Add(value);
				}
				Button button = new Button
				{
					Text = "连接选中",
					Location = new Point(256, 264),
					Size = new Size(96, 28)
				};
				button.Click += delegate
				{
					if (lv.SelectedItems.Count != 0)
					{
						LanPeer lanPeer2 = (LanPeer)lv.SelectedItems[0].Tag;
						txtHost.Text = lanPeer2.Ip;
						txtPort.Text = lanPeer2.Port.ToString();
						dlg.Close();
					}
				};
				Button button2 = new Button
				{
					Text = "网络唤醒",
					Location = new Point(356, 264),
					Size = new Size(96, 28)
				};
				button2.Click += delegate
				{
					if (lv.SelectedItems.Count != 0)
					{
						LanPeer lanPeer = (LanPeer)lv.SelectedItems[0].Tag;
						MessageBox.Show(LanDiscovery.WakeOnLan(lanPeer.Mac) ? ("已向 " + lanPeer.HostName + "（" + lanPeer.Mac + "）发送唤醒包。") : "唤醒失败：该设备未提供有效的 MAC 地址。", "网络唤醒", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					}
				};
				dlg.Controls.AddRange(new Control[3] { lv, button, button2 });
				dlg.ShowDialog(this);
			}
			finally
			{
				if (dlg != null)
				{
					((IDisposable)dlg).Dispose();
				}
			}
		}
		finally
		{
			btn.Enabled = true;
			btn.Text = "局域网设备";
		}
	}

	private void BtnRelay_Click(object? sender, EventArgs e)
	{
		int num = ParsePort();
		if (num < 0)
		{
			return;
		}
		if (_relay != null)
		{
			_relay.Stop();
			_relay = null;
			MessageBox.Show("中继服务器已停止。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		try
		{
			_relay = new RelayServer(num);
			_relay.Start();
			MessageBox.Show($"中继服务器已启动，监听端口 {num}。\n把它部署在公网机器上即可跨 NAT 打通主机与查看端。", "中继服务器", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
		}
		catch (Exception ex)
		{
			_relay = null;
			MessageBox.Show("启动中继失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		}
	}

	private void BtnWeb_Click(object? sender, EventArgs e)
	{
		if (_web != null)
		{
			StopWeb();
		}
		else if (!_webStarting)
		{
			int port = 9090;
			if (int.TryParse(txtWebPort.Text.Trim(), out var result) && result > 0 && result <= 65535)
			{
				port = result;
			}
			_webPort = port;
			WebStatus.SetPort(port);
			string pwd = EffectivePassword;
			btnWeb.Enabled = false;
			WebUiLog("正在启动 Web 远控…");
			Protocol.QualityConfig quality = HostCurrentConfig();
			_webStarting = true;
			Task.Run(delegate
			{
				StartWebInternal(port, pwd, quality);
			});
		}
	}

	private void AutoStartWeb()
	{
		if (_web == null && !_webStarting)
		{
			int port = 9090;
			if (int.TryParse(txtWebPort.Text.Trim(), out var result) && result > 0 && result <= 65535)
			{
				port = result;
			}
			_webPort = port;
			WebStatus.SetPort(port);
			string pwd = EffectivePassword;
			WebUiLog("正在启动 Web 远控…");
			Protocol.QualityConfig quality = HostCurrentConfig();
			_webStarting = true;
			Task.Run(delegate
			{
				StartWebInternal(port, pwd, quality);
			});
		}
	}

	private void StartFtp()
	{
		if (_ftp != null)
		{
			return;
		}
		int port = 2121;
		try
		{
			_ftp = new FtpServer(port);
			_ftp.Log = delegate(string s)
			{
				WebUiLog(s);
			};
			_ftp.Start();
		}
		catch (Exception ex)
		{
			try
			{
				_ftp?.Dispose();
			}
			catch
			{
			}
			_ftp = null;
			WebUiLog("FTP 启动失败：" + ex.Message);
		}
	}

	private void StartWebInternal(int port, string pwd, Protocol.QualityConfig quality)
	{
		RemoteDesktopServer remoteDesktopServer = null;
		WebServer webServer = null;
		bool flag = false;
		try
		{
			try
			{
				FirewallSetup.EnsureNetworkAccess(port, 2121, 2122, 2152, force: false, delegate(string s)
				{
					AppLog.Write("Firewall", s);
				});
			}
			catch (Exception ex)
			{
				AppLog.Write("Firewall", "启动 Web 前申请网络访问失败（已忽略）: " + ex.Message);
			}
			DxgiCapture.Enabled = true;
			remoteDesktopServer = new RemoteDesktopServer(0)
			{
				ServerPassword = pwd,
				AudioEnabled = false
			};
			remoteDesktopServer.Mode = quality.Mode;
			remoteDesktopServer.JpegQuality = quality.Quality;
			remoteDesktopServer.MaxFrameWidth = quality.MaxWidth;
			remoteDesktopServer.TargetFps = quality.Fps;
			remoteDesktopServer.ResyncIntervalSec = quality.ResyncSec;
			remoteDesktopServer.BlockSize = quality.BlockSize;
			remoteDesktopServer.Log += delegate(string s)
			{
				WebUiLog(s);
			};
			remoteDesktopServer.Start();
			int port2 = remoteDesktopServer.Port;
			webServer = new WebServer(port, port2, pwd);
			webServer.Start();
			_webSrv = remoteDesktopServer;
			_web = webServer;
			flag = true;
			WebStatus.MarkRunning(port, webServer.IsLoopbackOnly);
			string arg = (webServer.IsLoopbackOnly ? "仅本机可访问（URL ACL / 防火墙未放行，请在扳手快捷设置中重新放行并重启 Web 远控）" : string.Join(" 或 ", WebStatus.GetAllUrls()));
			WebUiLog(string.Format("已启动 http://localhost:{0}/ ｜ 局域网 {1} ｜ 密码：{2}", port, arg, string.IsNullOrEmpty(pwd) ? "空" : pwd));
			Ui(delegate
			{
				btnWeb.Text = "停止 Web 远控";
				btnWeb.Enabled = true;
			});
		}
		catch (Exception ex2)
		{
			if (!flag)
			{
				try
				{
					webServer?.Dispose();
				}
				catch
				{
				}
				try
				{
					remoteDesktopServer?.Dispose();
				}
				catch
				{
				}
				_web = null;
				_webSrv = null;
				WebStatus.MarkStopped();
			}
			WebUiLog("启动失败：" + ex2.Message);
			Ui(delegate
			{
				btnWeb.Enabled = true;
				btnWeb.Text = "启动 Web 远控";
			});
		}
		finally
		{
			_webStarting = false;
		}
	}

	private void StopWeb()
	{
		try
		{
			_web?.Dispose();
		}
		catch
		{
		}
		try
		{
			_webSrv?.Dispose();
		}
		catch
		{
		}
		_web = null;
		_webSrv = null;
		_webStarting = false;
		WebStatus.MarkStopped();
		if (btnWeb != null)
		{
			btnWeb.Text = "启动 Web 远控";
		}
		if (lblWeb != null)
		{
			lblWeb.Text = "已停止。";
		}
	}

	private void WebUiLog(string msg)
	{
		try
		{
			Label label = lblWeb;
			if (label != null && !label.IsDisposed && label.IsHandleCreated)
			{
				if (label.InvokeRequired)
				{
					label.BeginInvoke(new Action<string>(WebUiLog), msg);
				}
				else
				{
					label.Text = msg;
				}
			}
		}
		catch
		{
		}
	}

	private static string GetLanIp()
	{
		try
		{
			List<string> list = (from ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList
				where ip.AddressFamily == AddressFamily.InterNetwork
				select ip.ToString()).ToList();
			return (list.Count > 0) ? list[0] : "127.0.0.1";
		}
		catch
		{
			return "127.0.0.1";
		}
	}

	private async void BtnConnect_Click(object? sender, EventArgs e)
	{
		int num = ParsePort();
		if (num < 0)
		{
			return;
		}
		string text = txtHost.Text.Trim();
		if (string.IsNullOrEmpty(text))
		{
			MessageBox.Show("请填写主机/中继地址。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
			return;
		}
		RemoteDesktopClient client = new RemoteDesktopClient();
		ViewerForm viewer = new ViewerForm(client, text, num, txtPassword.Text);
		viewer.Show();
		bool flag;
		try
		{
			if (!chkRelay.Checked)
			{
				await client.ConnectAsync(text, num, txtPassword.Text);
			}
			else
			{
				string text2 = txtToken.Text.Trim();
				if (string.IsNullOrEmpty(text2))
				{
					viewer.Close();
					MessageBox.Show("中继模式需要填写“中继令牌”。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
					return;
				}
				await client.ConnectViaRelayAsync(text, num, text2, txtPassword.Text);
			}
			flag = true;
		}
		catch (Exception ex)
		{
			viewer.Close();
			MessageBox.Show("连接失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			client.Dispose();
			return;
		}
		if (flag)
		{
			SaveCurrentConnection(chkRemember.Checked);
		}
	}

	private void SaveCurrentConnection(bool remember)
	{
		string host = txtHost.Text.Trim();
		int port = ParsePort();
		if (port >= 0 && !string.IsNullOrEmpty(host))
		{
			SavedConnection savedConnection = Settings.SavedConnections.FirstOrDefault((SavedConnection c) => c.Host == host && c.Port == port && c.UseRelay == chkRelay.Checked);
			if (savedConnection == null)
			{
				savedConnection = new SavedConnection
				{
					Host = host,
					Port = port
				};
				Settings.SavedConnections.Add(savedConnection);
			}
			savedConnection.UseRelay = chkRelay.Checked;
			savedConnection.Token = (chkRelay.Checked ? txtToken.Text.Trim() : "");
			savedConnection.Password = (remember ? txtPassword.Text : "");
			if (Settings.SavedConnections.Count > 30)
			{
				Settings.SavedConnections.RemoveAt(0);
			}
			Settings.Save();
			RefreshSavedList();
		}
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_exiting = true;
			StopExternalServices();
			try
			{
				if (_tray != null)
				{
					_tray.Visible = false;
					_tray.Dispose();
					_tray = null;
				}
			}
			catch
			{
			}
			try
			{
				_trayMenu?.Dispose();
			}
			catch
			{
			}
			try
			{
				_trayMenu = null;
			}
			catch
			{
			}
		}
		if (disposing && components != null)
		{
			components.Dispose();
		}
		base.Dispose(disposing);
	}

	private void InitializeComponent()
	{
		this.components = new System.ComponentModel.Container();
		base.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
		base.ClientSize = new System.Drawing.Size(800, 450);
		this.Text = "painter 控制面板";
	}
}
