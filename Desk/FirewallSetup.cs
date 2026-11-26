using System;
using System.Diagnostics;
using System.IO;
using Painter.Helpers;

namespace desk;

/// <summary>
/// 网络访问权限（URL ACL + 防火墙入站）的一次性提权配置。
/// 以前每个模块各自 netsh runas，FTP 更是不做判断，导致每次启动都弹 UAC；
/// 这里统一成“规则已存在就直接记账，缺配置时整台设备只申请一次”。
/// 申请结果（包括用户点了“否”）落盘到 LocalAppData，之后启动不再打扰，
/// 需要重试时由托盘菜单「网络访问权限（管理员）」手动触发。
/// </summary>
internal static class FirewallSetup
{
	private static readonly object Gate = new object ();

	private static string MarkerDir {
		get {
			return Path.Combine (
				Environment.GetFolderPath (Environment.SpecialFolder.LocalApplicationData),
				AppIdentity.DataFolder, "firewall");
		}
	}

	private static string MarkerPath (string key)
	{
		return Path.Combine (MarkerDir, key + ".ok");
	}

	private static bool WasRequested (string key)
	{
		try { return File.Exists (MarkerPath (key)); } catch { return false; }
	}

	private static void MarkRequested (string key)
	{
		try {
			Directory.CreateDirectory (MarkerDir);
			File.WriteAllText (MarkerPath (key), DateTime.Now.ToString ("u"));
		} catch {
		}
	}

	private static bool QueryContains (string fileName, string arguments, string expected)
	{
		try {
			ProcessStartInfo psi = new ProcessStartInfo (fileName, arguments) {
				RedirectStandardOutput = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};
			using Process p = Process.Start (psi);
			if (p == null) {
				return false;
			}
			string output = p.StandardOutput.ReadToEnd ();
			p.WaitForExit (5000);
			return output.Contains (expected);
		} catch {
			return false;
		}
	}

	/// <summary>防火墙规则是否存在。普通权限即可查询，不会弹 UAC。</summary>
	public static bool RuleExists (string ruleName)
	{
		return QueryContains ("netsh", $"advfirewall firewall show rule name=\"{ruleName}\"", ruleName);
	}

	/// <summary>
	/// URL ACL 是否已注册。必须用强通配符 +：HTTP.sys 里 * 是弱通配符，
	/// 不匹配 IP 地址形式的 URL（http://192.168.x.x:9090/），浏览器访问会拿到
	/// 无效响应，Chrome 报 ERR_HTTP_RESPONSE_CODE_FAILURE；Win7 的 HTTP.sys 尤其严格。
	/// </summary>
	public static bool UrlAclExists (int port)
	{
		return QueryContains ("netsh", $"http show urlacl url=http://+:{port}/", $"http://+:{port}/");
	}

	/// <summary>
	/// 保证 Web（URL ACL + 入站规则）与 FTP（入站规则）已放行。
	/// 两项合并进同一份 netsh 脚本，因此一台设备第一次启动最多只弹一次 UAC。
	/// </summary>
	/// <param name="force">手动重新申请：忽略“已申请”标记重新提权注册。</param>
	public static bool EnsureNetworkAccess (
		int webPort, int ftpPort, int ftpPasvStart, int ftpPasvEnd, bool force, Action<string> log)
	{
		lock (Gate) {
			string webRule = AppIdentity.FirewallWebRulePrefix + webPort;
			string ftpRule = AppIdentity.FirewallFtpRulePrefix + ftpPort;
			string legacyWebRule = AppIdentity.LegacyFirewallWebRulePrefix + webPort;
			string legacyFtpRule = AppIdentity.LegacyFirewallFtpRulePrefix + ftpPort;
			// 改名前的规则名（Ink Canvas 那一代）也要删掉，避免系统防火墙里残留失效规则。
			string legacyInkCanvasWebRule = AppIdentity.LegacyFirewallWebRulePrefixInkCanvas + webPort;
			string legacyInkCanvasFtpRule = AppIdentity.LegacyFirewallFtpRulePrefixInkCanvas + ftpPort;
			// 版本号 +2：旧设备只注册过弱通配符 * 的 ACL，必须强制重跑一次脚本
			// 换成强通配符 +，否则旧标记 web-9090.ok 会让迁移永远不发生。
			string webKey = "web2-" + webPort;
			string ftpKey = "ftp-" + ftpPort;
			// 局域网发现（手机 App 扫描画板设备 + 远程开关 Web）走 UDP 21118，
			// 必须单独放行入站，否则路由器隔离/系统防火墙会丢掉手机广播。
			string discoveryRule = AppIdentity.FirewallDiscoveryRule;
			string discoveryKey = "discovery-" + LanDiscovery.Port;

			// Win7 没有 HTTP.sys WebSocket，WebServer 会在 webPort+1..+12 上开兼容 TCP 通道，
			// 单独放行这一段（Win8+ 用不到，保持原有规则不变，避免老设备重复弹 UAC）。
			bool needWsCompat = OsSupport.IsWindows7OrOlder;
			string wsRule = AppIdentity.FirewallWebRulePrefix + "ws" + webPort;
			string wsKey = "webws-" + webPort;
			int wsPortEnd = Math.Min (webPort + 12, 65535);

			bool webMissing = force || (!WasRequested (webKey) && !(RuleExists (webRule) && UrlAclExists (webPort)));
			bool ftpMissing = ftpPort > 0 && (force || (!WasRequested (ftpKey) && !RuleExists (ftpRule)));
			bool wsMissing = needWsCompat && (force || (!WasRequested (wsKey) && !RuleExists (wsRule)));
			bool discoveryMissing = force || (!WasRequested (discoveryKey) && !RuleExists (discoveryRule));

			// 已经配置过：补记标记后直接返回，永不重复弹 UAC。
			if (!webMissing && !ftpMissing && !wsMissing && !discoveryMissing) {
				if (!WasRequested (webKey)) {
					MarkRequested (webKey);
				}
				if (ftpPort > 0 && !WasRequested (ftpKey)) {
					MarkRequested (ftpKey);
				}
				if (needWsCompat && !WasRequested (wsKey)) {
					MarkRequested (wsKey);
				}
				// 能进到这里说明 discoveryMissing 为 false（规则已存在），只补记缺失的标记
				if (!WasRequested (discoveryKey)) {
					MarkRequested (discoveryKey);
				}
				return true;
			}

			string script = null;
			try {
				string body = "";
				if (webMissing) {
					// user 只能写 Everyone：netsh 不接受 *S-1-1-0 形式的 SID（实测报错 1332 参数错误），
					// 中文系统上会把它解析成内置的 \Everyone。
					// 先删旧的弱通配符 * 再加强通配符 +：两者并存时 HTTP.sys 仍可能拿 * 去匹配 IP 访问，
					// 导致浏览器 ERR_HTTP_RESPONSE_CODE_FAILURE。
					body += $"http delete urlacl url=http://*:{webPort}/\r\n" +
						$"http delete urlacl url=http://+:{webPort}/\r\n" +
						$"http add urlacl url=http://+:{webPort}/ user=Everyone\r\n" +
						$"advfirewall firewall delete rule name=\"{webRule}\"\r\n" +
						$"advfirewall firewall delete rule name=\"{legacyWebRule}\"\r\n" +
						$"advfirewall firewall delete rule name=\"{legacyInkCanvasWebRule}\"\r\n" +
						$"advfirewall firewall add rule name=\"{webRule}\" dir=in action=allow protocol=TCP localport={webPort}\r\n";

					// 端口由 9012 迁移到 9090：顺手清掉旧端口遗留的 URL ACL 与入站规则，
					// 避免系统里残留一堆失效规则，用户排查"到底放行没放行"时被误导。
					if (webPort != WebStatus.LegacyPort) {
						int old = WebStatus.LegacyPort;
						body += $"http delete urlacl url=http://*:{old}/\r\n" +
							$"http delete urlacl url=http://+:{old}/\r\n" +
							$"advfirewall firewall delete rule name=\"{AppIdentity.FirewallWebRulePrefix}{old}\"\r\n" +
							$"advfirewall firewall delete rule name=\"{AppIdentity.LegacyFirewallWebRulePrefix}{old}\"\r\n";
					}
				}
				if (ftpMissing) {
					body += $"advfirewall firewall delete rule name=\"{ftpRule}\"\r\n" +
						$"advfirewall firewall delete rule name=\"{legacyFtpRule}\"\r\n" +
						$"advfirewall firewall delete rule name=\"{legacyInkCanvasFtpRule}\"\r\n" +
						$"advfirewall firewall add rule name=\"{ftpRule}\" dir=in action=allow protocol=TCP localport={ftpPort},{ftpPasvStart}-{ftpPasvEnd}\r\n";
				}
				if (wsMissing) {
					body += $"advfirewall firewall delete rule name=\"{wsRule}\"\r\n" +
						$"advfirewall firewall add rule name=\"{wsRule}\" dir=in action=allow protocol=TCP localport={webPort + 1}-{wsPortEnd}\r\n";
				}
				if (discoveryMissing) {
					body += $"advfirewall firewall delete rule name=\"{discoveryRule}\"\r\n" +
						$"advfirewall firewall add rule name=\"{discoveryRule}\" dir=in action=allow protocol=UDP localport={LanDiscovery.Port}\r\n";
				}

				script = Path.Combine (Path.GetTempPath (),
					$"{AppIdentity.NetShScriptPrefix}net_{webPort}_{DateTime.Now.Ticks}.txt");
				try {
					File.WriteAllText (script, body);
					ProcessStartInfo psi = new ProcessStartInfo ("netsh", $"-f \"{script}\"") {
						Verb = "runas",
						CreateNoWindow = true,
						WindowStyle = ProcessWindowStyle.Hidden,
						UseShellExecute = true,
					};
					using Process p = Process.Start (psi);
					if (p != null && !p.WaitForExit (10000)) {
						try { p.Kill (); } catch { }
					}
				} finally {
					try { File.Delete (script); } catch { }
				}

				// 无论成功与否都记账：宁可用户在托盘菜单手动重试，也不要每次启动都弹 UAC。
				MarkRequested (webKey);
				if (ftpPort > 0) {
					MarkRequested (ftpKey);
				}
				if (needWsCompat) {
					MarkRequested (wsKey);
				}
				if (discoveryMissing) {
					MarkRequested (discoveryKey);
				}

				bool ok = (!webMissing || (RuleExists (webRule) && UrlAclExists (webPort))) &&
					(!ftpMissing || RuleExists (ftpRule)) &&
					(!wsMissing || RuleExists (wsRule)) &&
					(!discoveryMissing || RuleExists (discoveryRule));
				log?.Invoke (ok
					? $"[网络] 防火墙 / URL ACL 已配置（Web 端口 {webPort}：入站 TCP + http://+:{webPort}/ URL ACL）"
					: $"[网络] 未获得管理员授权，局域网访问（端口 {webPort}）可能不可用；可在托盘菜单「网络访问权限」重新申请");
				return ok;
			} catch {
				// 用户点了“否”时 ShellExecute 抛 Win32Exception，属预期：记账后不再打扰。
				MarkRequested (webKey);
				if (ftpPort > 0) {
					MarkRequested (ftpKey);
				}
				if (needWsCompat) {
					MarkRequested (wsKey);
				}
				if (discoveryMissing) {
					MarkRequested (discoveryKey);
				}
				log?.Invoke ("[网络] 已取消管理员授权，局域网访问可能不可用；可在托盘菜单「网络访问权限」重新申请");
				return false;
			}
		}
	}
}
