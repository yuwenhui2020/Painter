namespace desk;

/// <summary>
/// 对外可见的产品标识集中定义（服务名、命名管道、防火墙规则名、数据目录与文件名等）。
/// 这些值一旦落到系统里就不易回收，所以只在这里写一次；旧产品名（desk）留在 Legacy* 里，
/// 供安装/注册时顺带清理，以及混版本场景下的兼容回退。
/// </summary>
internal static class AppIdentity
{
	/// <summary>产品显示名：提示文案、对话框标题与进程说明使用，与可执行文件名一致。</summary>
	public const string ProductName = "painter";

	/// <summary>数据目录名：%AppData%\Painter、%LocalAppData%\Painter、%ProgramData%\Painter。</summary>
	public const string DataFolder = "Painter";

	/// <summary>改名前的数据目录名，仅用于迁移旧配置。</summary>
	public const string LegacyDataFolder = "desk";

	/// <summary>改名前的数据目录名（Ink Canvas 那一代），仅用于一次性迁移。</summary>
	public const string LegacyDataFolderInkCanvas = "InkCanvas";

	/// <summary>UAC 支持服务名（sc.exe 命令里不带引号使用，故不能含空格）。</summary>
	public const string ServiceName = "PainterUac";

	public const string LegacyServiceName = "desk_uac";

	/// <summary>改名前的服务名（Ink Canvas 那一代），仅用于卸载清理。</summary>
	public const string LegacyServiceNameInkCanvas = "InkCanvasUac";

	/// <summary>向 SYSTEM 服务请求 SendSAS 的命名管道名。</summary>
	public const string SasPipeName = "PainterUacSas";

	public const string LegacySasPipeName = "desk_uac_sas";

	public const string LegacySasPipeNameInkCanvas = "InkCanvasUacSas";

	/// <summary>防火墙规则名前缀，后接监听端口号。</summary>
	public const string FirewallWebRulePrefix = "Painter Web ";

	public const string FirewallFtpRulePrefix = "Painter FTP ";

	/// <summary>局域网发现（手机扫描画板设备）UDP 入站规则名。</summary>
	public const string FirewallDiscoveryRule = "Painter LAN Discovery";


	public const string LegacyFirewallWebRulePrefix = "desk Web ";

	public const string LegacyFirewallFtpRulePrefix = "desk FTP ";

	/// <summary>改名前的防火墙规则前缀（Ink Canvas 那一代），仅用于清理残留规则。</summary>
	public const string LegacyFirewallWebRulePrefixInkCanvas = "InkCanvas Web ";

	public const string LegacyFirewallFtpRulePrefixInkCanvas = "InkCanvas FTP ";

	/// <summary>运行日志文件名（位于 LocalAppData\Painter）。</summary>
	public const string LogFileName = "painter.log";

	/// <summary>Web 独立模式日志（程序目录下）与 -host 被控端日志（临时目录下）。</summary>
	public const string WebLogFileName = "painter_web.log";

	public const string HostLogFileName = "painter_uac_host.log";

	/// <summary>netsh 脚本临时文件名前缀，后接端口与 .txt。</summary>
	public const string NetShScriptPrefix = "painter_";

	/// <summary>剪贴板文件传输的临时目录名与所有权重载格式名。</summary>
	public const string ClipboardTempFolder = "painter_clip";

	public const string ClipboardOwnerFormat = "painter.clipboard.owner";

	/// <summary>限制 DXGI 抓屏实例数的互斥量名。</summary>
	public const string DxgiMutexName = "PainterDxgiCapture";

	/// <summary>局域网发现的广播魔术字。</summary>
	public const string LanPing = "PAINTER-PING";

	public const string LanPong = "PAINTER-PONG";

	/// <summary>局域网远程控制魔术字（手机 App → PC）：PAINTER-CMD|动作[|参数]。
	/// 动作：web-on 打开 Web 控制台、web-off 关闭、status 查询状态、hello 仅探测在线。</summary>
	public const string LanCmd = "PAINTER-CMD";

	/// <summary>PC → 手机的控制回执：PAINTER-ACK|动作|结果(ok/denied/error)|Web端口|Web是否运行(0/1)|说明。</summary>
	public const string LanAck = "PAINTER-ACK";

	/// <summary>画板/桌面程序类型标识，手机据此把 PONG 识别为"画板设备"。</summary>
	public const string LanKind = "PAINTER";

	public const string LegacyLanPing = "DESK-PING";

	public const string LegacyLanPong = "DESK-PONG";

	/// <summary>改名前的局域网魔术字（Ink Canvas 那一代），仅用于兼容仍在跑旧版的客户端。</summary>
	public const string LegacyLanPingInkCanvas = "INKCANVAS-PING";

	public const string LegacyLanPongInkCanvas = "INKCANVAS-PONG";
}
