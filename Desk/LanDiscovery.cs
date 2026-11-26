using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows.Forms;

namespace desk;

internal static class LanDiscovery
{
	public const int Port = 21118;

	private const string Ping = AppIdentity.LanPing;

	private const string Pong = AppIdentity.LanPong;

	private static UdpClient? _listener;

	private static int _localPort;

	public static void StartResponder (int localPort)
	{
		_localPort = localPort;
		try {
			_listener?.Close ();
			_listener = new UdpClient (new IPEndPoint (IPAddress.Any, 21118)) {
				EnableBroadcast = true
			};
			Task.Run ((Func<Task?>)ListenLoop);
		} catch {
		}
	}

	public static void StopResponder ()
	{
		try {
			_listener?.Close ();
		} catch {
		}
		_listener = null;
	}

	private static async Task ListenLoop ()
	{
		UdpClient c = _listener;
		if (c == null) {
			return;
		}
		try {
			while (true) {
				UdpReceiveResult r = await c.ReceiveAsync ();
				string text = Encoding.UTF8.GetString (r.Buffer);
				IPAddress src = r.RemoteEndPoint.Address;

				// —— 手机 App 的远程控制命令：PAINTER-CMD|动作[|参数] ——
				// 必须先过同网段鉴权，绝不允许公网来源开关本机 Web 服务。
				if (text.StartsWith (AppIdentity.LanCmd, StringComparison.Ordinal)) {
					HandleCommand (text, src, c, r.RemoteEndPoint);
					continue;
				}

				// 改名前的旧版本只会认 DESK-PONG / INKCANVAS-PONG，
				// 按它发来的魔术字原样回应才不会被漏掉
				string magic = null;
				if (text.StartsWith (Ping, StringComparison.Ordinal)) {
					magic = Pong;
				} else if (text.StartsWith (AppIdentity.LegacyLanPingInkCanvas, StringComparison.Ordinal)) {
					magic = AppIdentity.LegacyLanPongInkCanvas;
				} else if (text.StartsWith (AppIdentity.LegacyLanPing, StringComparison.Ordinal)) {
					magic = AppIdentity.LegacyLanPong;
				}
				if (magic != null && !IsLocalAddress (src)) {
					// 新版 PONG 在原 4 段后追加：类型|Web端口|Web是否运行|是否仅回环。
					// 手机据此把本机识别为"画板设备"、显示 Web 开关状态、拼出访问地址。
					// 旧客户端只读前 4 段，完全兼容。
					string reply = $"{magic}|{Dns.GetHostName ()}|{_localPort}|{LocalMac ()}"
						+ $"|{AppIdentity.LanKind}|{WebStatus.Port}|{(WebStatus.IsRunning ? 1 : 0)}|{(WebStatus.IsLoopbackOnly ? 1 : 0)}";
					byte[] bytes = Encoding.UTF8.GetBytes (reply);
					await c.SendAsync (bytes, bytes.Length, r.RemoteEndPoint);
				}
			}
		} catch {
		}
	}

	/// <summary>
	/// 处理手机 App 的远程控制命令。仅接受同一局域网内的来源；
	/// Web 开关最终落到 WebControl.SetEnabled（内部会切回 WinForms UI 线程，跨线程安全）。
	/// </summary>
	private static void HandleCommand (string text, IPAddress src, UdpClient c, IPEndPoint remote)
	{
		string[] parts = text.Split ('|');
		string action = parts.Length >= 2 ? parts [1].Trim ().ToLowerInvariant () : "";
		if (action == "") {
			return;
		}
		// 鉴权：源地址必须与本机任一网卡处于同一网段；本机/回环/公网来源一律拒绝。
		if (!IsInSameSubnet (src)) {
			SendAck (c, remote, action, "denied", "拒绝：来源不在同一局域网");
			return;
		}
		try {
			string result = "ok";
			string note = "";
			switch (action) {
			case "web-on":
				note = WebControl.SetEnabled (true);
				break;
			case "web-off":
				note = WebControl.SetEnabled (false);
				break;
			case "status":
			case "hello":
				note = "在线";
				break;
			default:
				result = "error";
				note = "未知命令：" + action;
				break;
			}
			SendAck (c, remote, action, result, note);
		} catch (Exception ex) {
			SendAck (c, remote, action, "error", "执行失败：" + ex.Message);
		}
	}

	private static void SendAck (UdpClient c, IPEndPoint remote, string action, string result, string note)
	{
		try {
			// PAINTER-ACK|动作|结果|Web端口|Web是否运行|是否仅回环|说明
			string ack = $"{AppIdentity.LanAck}|{action}|{result}|{WebStatus.Port}|{(WebStatus.IsRunning ? 1 : 0)}|{(WebStatus.IsLoopbackOnly ? 1 : 0)}|{note}";
			byte[] bytes = Encoding.UTF8.GetBytes (ack);
			c.Send (bytes, bytes.Length, remote);
		} catch {
		}
	}

	/// <summary>源 IP 是否与本机任一处于 Up 状态的网卡同网段（私网/回环/公网来源严格区分）。
	/// internal：生态桥 MchatEcoBridge 处理 Mchat 频道命令时复用同一套鉴权。</summary>
	internal static bool IsInSameSubnet (IPAddress src)
	{
		try {
			if (src == null || IPAddress.IsLoopback (src) || IsLocalAddress (src)) {
				return false;
			}
			byte[] s = src.GetAddressBytes ();
			if (s.Length != 4) {
				return false;
			}
			NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces ();
			foreach (NetworkInterface nic in nics) {
				if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) {
					continue;
				}
				foreach (UnicastIPAddressInformation ua in nic.GetIPProperties ().UnicastAddresses) {
					if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) {
						continue;
					}
					byte[] ip = ua.Address.GetAddressBytes ();
					byte[] mask = ua.IPv4Mask.GetAddressBytes ();
					bool same = ip.Length == 4 && mask.Length == 4;
					for (int i = 0; same && i < 4; i++) {
						if ((s [i] & mask [i]) != (ip [i] & mask [i])) {
							same = false;
						}
					}
					if (same) {
						return true;
					}
				}
			}
		} catch {
		}
		return false;
	}

	public static async Task<List<LanPeer>> DiscoverAsync (int timeoutMs = 1500)
	{
		List<LanPeer> found = new List<LanPeer> ();
		HashSet<string> seen = new HashSet<string> ();
		UdpClient c = null;
		try {
			c = new UdpClient {
				EnableBroadcast = true
			};
			c.Client.ReceiveTimeout = timeoutMs;
			new IPEndPoint (IPAddress.Any, 0);
			byte[] msg = Encoding.UTF8.GetBytes (Ping);
			NetworkInterface[] allNetworkInterfaces = NetworkInterface.GetAllNetworkInterfaces ();
			foreach (NetworkInterface ni in allNetworkInterfaces) {
				if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) {
					continue;
				}
				foreach (UnicastIPAddressInformation ua in ni.GetIPProperties ().UnicastAddresses) {
					if (ua.Address.AddressFamily != AddressFamily.InterNetwork) {
						continue;
					}
					IPAddress mask = ua.IPv4Mask;
					if (mask != null) {
						IPAddress bcast = GetBroadcast (ua.Address, mask);
						try {
							await c.SendAsync (msg, msg.Length, new IPEndPoint (bcast, 21118));
						} catch {
						}
					}
				}
			}
			try {
				await c.SendAsync (msg, msg.Length, new IPEndPoint (IPAddress.Broadcast, 21118));
			} catch {
			}
			Stopwatch sw = Stopwatch.StartNew ();
			while (sw.ElapsedMilliseconds < timeoutMs) {
				Task<UdpReceiveResult> t = c.ReceiveAsync ();
				if (await Task.WhenAny (t, Task.Delay (Math.Max (1, timeoutMs - (int)sw.ElapsedMilliseconds))) != t) {
					break;
				}
				UdpReceiveResult r = await t;
				string text = Encoding.UTF8.GetString (r.Buffer);
				if (!text.StartsWith (Pong, StringComparison.Ordinal) && !text.StartsWith (AppIdentity.LegacyLanPong, StringComparison.Ordinal)) {
					continue;
				}
				string[] parts = text.Split ('|');
				if (parts.Length >= 3) {
					string ip = r.RemoteEndPoint.Address.ToString ();
					if (seen.Add (ip)) {
						found.Add (new LanPeer {
							Ip = ip,
							HostName = parts [1],
							Port = (int.TryParse (parts [2], out var p) ? p : 9000),
							Mac = ((parts.Length >= 4) ? parts [3] : "")
						});
					}
				}
			}
		} catch {
		} finally {
			try {
				c?.Close ();
			} catch {
			}
		}
		return found;
	}

	public static bool WakeOnLan (string mac, IPAddress? bcast = null)
	{
		try {
			byte[] array = ParseMac (mac);
			if (array == null) {
				return false;
			}
			List<byte> list = new List<byte> ();
			for (int i = 0; i < 6; i++) {
				list.Add (byte.MaxValue);
			}
			for (int j = 0; j < 16; j++) {
				list.AddRange (array);
			}
			using UdpClient udpClient = new UdpClient {
				EnableBroadcast = true
			};
			IPAddress address = bcast ?? IPAddress.Broadcast;
			udpClient.Send (list.ToArray (), list.Count, new IPEndPoint (address, 9));
			return true;
		} catch {
			return false;
		}
	}

	private static byte[]? ParseMac (string mac)
	{
		string text = mac.Replace (":", "").Replace ("-", "").Replace (" ", "");
		if (text.Length != 12) {
			return null;
		}
		try {
			byte[] array = new byte[6];
			for (int i = 0; i < 6; i++) {
				array [i] = Convert.ToByte (text.Substring (i * 2, 2), 16);
			}
			return array;
		} catch {
			return null;
		}
	}

	private static IPAddress GetBroadcast (IPAddress addr, IPAddress mask)
	{
		byte[] addressBytes = addr.GetAddressBytes ();
		byte[] addressBytes2 = mask.GetAddressBytes ();
		byte[] array = new byte[4];
		for (int i = 0; i < 4; i++) {
			array [i] = (byte)(addressBytes [i] | (addressBytes2 [i] ^ 0xFF));
		}
		return new IPAddress (array);
	}

	private static bool IsLocalAddress (IPAddress addr)
	{
		try {
			NetworkInterface[] allNetworkInterfaces = NetworkInterface.GetAllNetworkInterfaces ();
			foreach (NetworkInterface networkInterface in allNetworkInterfaces) {
				foreach (UnicastIPAddressInformation unicastAddress in networkInterface.GetIPProperties ().UnicastAddresses) {
					if (unicastAddress.Address.Equals (addr)) {
						return true;
					}
				}
			}
			return IPAddress.IsLoopback (addr);
		} catch {
			return false;
		}
	}

	public static string LocalMac ()
	{
		try {
			NetworkInterface[] allNetworkInterfaces = NetworkInterface.GetAllNetworkInterfaces ();
			foreach (NetworkInterface networkInterface in allNetworkInterfaces) {
				if (networkInterface.OperationalStatus != OperationalStatus.Up || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback) {
					continue;
				}
				PhysicalAddress physicalAddress = networkInterface.GetPhysicalAddress ();
				if (physicalAddress != null && physicalAddress.GetAddressBytes ().Length == 6) {
					return string.Join (":", from b in physicalAddress.GetAddressBytes ()
						select b.ToString ("X2"));
				}
			}
		} catch {
		}
		return "";
	}
}
