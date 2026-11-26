using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace desk;

/// <summary>
/// Web 控制台运行状态的单一真相来源：默认端口、当前端口、是否在运行、本机局域网 IP。
///   · desk 侧（Form1 / DeskBootstrap）在 Web 服务启动 / 停止时写入；
///   · WPF 侧（扳手快捷设置）只读，用来显示 "http://IP:端口/"。
/// 全部是静态字段 + volatile / lock，跨线程（WinForms 线程、WPF 线程、后台线程）安全，
/// 不触碰任何 DependencyObject / WinForms 控件，避免跨线程访问异常。
/// </summary>
public static class WebStatus
{
    /// <summary>Web 控制台默认端口（全程序统一使用此常量，勿再散落硬编码）。</summary>
    public const int DefaultPort = 9090;

    /// <summary>迁移前的旧默认端口，防火墙清理时用。</summary>
    public const int LegacyPort = 9012;

    private static volatile int _port = DefaultPort;
    private static volatile bool _running;
    private static volatile bool _loopbackOnly;

    /// <summary>当前 Web 端口（未启动时为默认端口）。</summary>
    public static int Port => _port;

    /// <summary>Web 服务是否已启动。</summary>
    public static bool IsRunning => _running;

    /// <summary>HTTP 只绑定了 127.0.0.1（URL ACL / 防火墙未放行），局域网暂不可访问。</summary>
    public static bool IsLoopbackOnly => _loopbackOnly;

    public static void MarkRunning(int port, bool loopbackOnly = false)
    {
        if (port > 0 && port <= 65535) _port = port;
        _loopbackOnly = loopbackOnly;
        _running = true;
    }

    public static void MarkStopped()
    {
        _running = false;
        _loopbackOnly = false;
    }

    public static void SetPort(int port)
    {
        if (port > 0 && port <= 65535) _port = port;
    }

    /// <summary>返回 "http://{局域网IP}:{端口}/"，供用户照着在手机/其它电脑浏览器打开。</summary>
    public static string GetUrl() => "http://" + GetLanIp() + ":" + _port + "/";

    /// <summary>
    /// 返回本机所有可用 IPv4 的完整访问地址（含端口），供快捷菜单逐条显示 / 点击。
    /// 顺序：私有局域网地址优先 → 其它可路由地址 → 回环地址兜底。
    /// </summary>
    public static List<string> GetAllUrls()
    {
        int port = _port;
        return GetAllLanIps().Select(ip => "http://" + ip + ":" + port + "/").ToList();
    }

    private static readonly object IpLock = new object();
    private static List<string> _cachedIps = new List<string>();
    private static DateTime _ipTimeUtc = DateTime.MinValue;

    /// <summary>
    /// 枚举本机全部可用 IPv4：网卡状态 Up、非 APIPA(169.254)、非 0.0.0.0。
    /// 回环地址仅在没有其它地址时兜底（Web 已监听 0.0.0.0，本机访问始终可用）。
    /// 结果缓存 10 秒，避免每次展开快捷菜单都重新枚举网卡。
    /// </summary>
    public static List<string> GetAllLanIps()
    {
        lock (IpLock)
        {
            if (_cachedIps.Count > 0 && (DateTime.UtcNow - _ipTimeUtc).TotalSeconds < 10)
                return new List<string>(_cachedIps);

            var ipv4 = new List<string>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    IPInterfaceProperties props = nic.GetIPProperties();
                    if (props == null) continue;
                    foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string s = ua.Address.ToString();
                        if (s.StartsWith("169.254.") || s == "0.0.0.0") continue;
                        if (!ipv4.Contains(s)) ipv4.Add(s);
                    }
                }
            }
            catch { /* 部分环境枚举网卡会抛异常，走 DNS 兜底 */ }

            if (ipv4.Count == 0)
            {
                try
                {
                    IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());
                    foreach (IPAddress a in host.AddressList)
                    {
                        if (a.AddressFamily != AddressFamily.InterNetwork) continue;
                        string s = a.ToString();
                        if (s.StartsWith("169.254.") || s == "0.0.0.0") continue;
                        if (!ipv4.Contains(s)) ipv4.Add(s);
                    }
                }
                catch { /* DNS 不可用，最终回退回环地址 */ }
            }

            // 私有局域网地址优先，其它地址次之，回环地址最后。
            ipv4.Sort((a, b) => IpRank(b).CompareTo(IpRank(a)));
            if (ipv4.Count == 0) ipv4.Add("127.0.0.1");

            _cachedIps = new List<string>(ipv4);
            _ipTimeUtc = DateTime.UtcNow;
            return new List<string>(_cachedIps);
        }
    }

    private static int IpRank(string ip)
    {
        if (ip.StartsWith("127.")) return 0;
        if (ip.StartsWith("192.168.") || ip.StartsWith("10.")
            || (ip.StartsWith("172.") && Is172Private(ip))) return 3;
        return 2;
    }

    /// <summary>取最可能被手机/其它电脑使用的局域网 IP（保留给旧调用点）。</summary>
    public static string GetLanIp()
    {
        List<string> list = GetAllLanIps();
        return list.FirstOrDefault(s => IpRank(s) == 3)
            ?? list.FirstOrDefault(s => !s.StartsWith("127."))
            ?? "127.0.0.1";
    }

    private static bool Is172Private(string ip)
    {
        var parts = ip.Split('.');
        return parts.Length >= 2 && int.TryParse(parts[1], out int second)
            && second >= 16 && second <= 31;
    }
}
