// ============================================================
// MchatEcoBridge —— 白板端（Painter）接入 Mchat 生态的适配层。
//
// 目的：让本程序能被 MchatA（安卓）/ MchatW（PC）在「设备发现」里看到，
// 并能接收它们通过局域网 mesh 频道广播的消息（文本 / 位置 / 音乐视频卡片）。
//
// 设计原则：
//   · 完全自包含，不依赖白板任何现有类型；发现/监听失败只记日志，绝不影响
//     画板、远控、Web、FTP 等既有功能 —— 保证白板独立可运行。
//   · 与白板自有协议（UDP 21118 PAINTER-PING、小端二进制远控帧）并行，
//     两套互不复写：自有端口 21118/9090/9012 保持原样，本桥只用 45777/45778。
//   · 协议与 MchatA / MchatW / MchatTV 基准逐字节一致：
//       发现：UDP 224.0.0.167:45777 (+ 广播)，hello 字段 t/id/name/port/qp/rd/dc，5s 心跳，probe 立即回 hello；
//       消息：TCP 45778，帧 = 4 字节大端头长 + UTF8 JSON 头 + 4 字节大端体长 + body，回执 'K'；
//       JSON 头字段：id/kind/from/fromName/conv/text/time（+ location 的 lat/lon/alt/bearing）。
//
// 收到的消息通过 MessageReceived 事件暴露，白板 UI/命令层可按需订阅
// （例如把 text 当远控命令、把 location 显示在侧栏），不订阅也无害。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace desk;

internal static class MchatEcoBridge
{
    const string MulticastAddress = "224.0.0.167";
    const int DiscoveryPort = 45777;
    const int TcpPort = 45778;
    const int HeartbeatMs = 5000;
    const int MaxHeader = 8 * 1024 * 1024;
    const int MaxBody = 32 * 1024 * 1024;

    static CancellationTokenSource _cts;
    static UdpClient _udp;
    static TcpListener _tcp;
    static string _peerId;
    static readonly object _seenGate = new object();
    static readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>收到生态消息。参数是完整的消息字典（含 id/kind/from/fromName/conv/text/time）。</summary>
    public static event Action<Dictionary<string, object>, string> MessageReceived;

    public static bool Running => _cts != null && !_cts.IsCancellationRequested;

    public static void Start()
    {
        if (Running) return;
        _peerId = "painter-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        try { Task.Run(() => UdpLoop(token), token); } catch { }
        try { Task.Run(() => TcpLoop(token), token); } catch { }
    }

    public static void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Close(); } catch { }
        try { _tcp?.Stop(); } catch { }
        // 确定性释放 IDisposable，避免 Start/Stop 循环依赖 GC 兜底
        try { _udp?.Dispose(); } catch { }
        try { _tcp?.Server?.Dispose(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _cts = null; _udp = null; _tcp = null;
    }

    /// <summary>
    /// 向生态对端投递一条消息（短连接 TCP 一帧）。供命令回执/主动通知使用。
    /// port 省略时用生态默认 45778；MchatA/MchatW 的对端端口可从 hello 的 port 字段拿到。
    /// </summary>
    public static bool SendText(string ip, string text, string kind = "text", int port = TcpPort,
                                string conv = "chan:mesh")
        => SendBytes(ip, text, null, kind, port, conv, null);

    /// <summary>
    /// 向生态对端投递一帧带二进制体（如 whiteboard.stroke 的 .icstk 字节）。
    /// 帧 = 4B大端头长 + JSON头 + 4B大端体长 + body，与 MchatA/MchatW/TV 完全一致。
    /// </summary>
    public static bool SendBytes(string ip, string text, byte[] body, string kind,
                                 int port = TcpPort, string conv = "chan:mesh", string name = null)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;
        if (body != null && body.Length > MaxBody) return false;
        try
        {
            using (var client = new TcpClient())
            {
                client.Connect(ip, port);
                using (NetworkStream s = client.GetStream())
                {
                    var msg = new Dictionary<string, object>
                    {
                        ["id"] = Guid.NewGuid().ToString("N"),
                        ["kind"] = kind,
                        ["from"] = _peerId,
                        ["fromName"] = "画板",
                        ["conv"] = conv,
                        ["text"] = text ?? "",
                        ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    if (!string.IsNullOrEmpty(name)) msg["name"] = name;
                    string json = ToJson(msg);
                    byte[] hb = Encoding.UTF8.GetBytes(json);
                    byte[] hl = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(hb.Length));
                    s.Write(hl, 0, 4);
                    s.Write(hb, 0, hb.Length);
                    int blen = body == null ? 0 : body.Length;
                    byte[] bl = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(blen));
                    s.Write(bl, 0, 4);
                    if (blen > 0) s.Write(body, 0, blen);
                    s.Flush();
                    try { s.ReadByte(); } catch { }
                }
            }
            return true;
        }
        catch { return false; }
    }

    static string ToJson(Dictionary<string, object> d)
    {
        var sb = new StringBuilder("{");
        bool first = true;
        foreach (var kv in d)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(Esc(kv.Key)).Append("\":");
            object v = kv.Value;
            if (v is string str) sb.Append('"').Append(Esc(str)).Append('"');
            else if (v is bool b) sb.Append(b ? "true" : "false");
            else sb.Append(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.Append('}').ToString();
    }

    static string Esc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static async Task UdpLoop(CancellationToken token)
    {
        try
        {
            _udp = new UdpClient();
            _udp.EnableBroadcast = true;
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            try { _udp.JoinMulticastGroup(IPAddress.Parse(MulticastAddress)); } catch { }
        }
        catch (Exception ex) { AppLog.Write("MchatEco", "UDP 绑定失败: " + ex.Message); return; }

        // 心跳：定时广播 hello，并在启动时先发一次 probe 让对端立即回 hello
        _ = Task.Run(async () =>
        {
            try { SendUdp(Hello("probe")); } catch { }
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatMs, token).ConfigureAwait(false);
                try { SendUdp(Hello("hello")); } catch { }
            }
        }, token);

        try
        {
            while (!token.IsCancellationRequested)
            {
                var r = await _udp.ReceiveAsync().ConfigureAwait(false);
                string text = Encoding.UTF8.GetString(r.Buffer);
                Dictionary<string, object> obj = ParseJson(text);
                if (obj == null) continue;
                string t = Str(obj, "t");
                if (t == "probe") SendUdp(Hello("hello"), r.RemoteEndPoint);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Write("MchatEco", "UDP loop: " + ex.Message); }
    }

    static void SendUdp(string json, IPEndPoint target = null)
    {
        if (_udp == null) return;
        byte[] b = Encoding.UTF8.GetBytes(json);
        if (target != null) { _udp.Send(b, b.Length, target); return; }
        _udp.Send(b, b.Length, new IPEndPoint(IPAddress.Parse(MulticastAddress), DiscoveryPort));
        _udp.Send(b, b.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
    }

    static string Hello(string t)
    {
        // rd=0/dc=""：白板使用自有 HTTP tap/swipe 远控，不宣称 desk 9012，避免手机误判可直连。
        // ws：网页远控端口（WebStatus.Port，默认 9090），手机据此给出「打开网页远控」入口。
        return "{\"t\":\"" + t + "\",\"id\":\"" + _peerId + "\",\"name\":\"画板\",\"port\":" + TcpPort
             + ",\"qp\":0,\"rd\":0,\"dc\":\"\",\"ws\":" + WebStatus.Port + "}";
    }

    static async Task TcpLoop(CancellationToken token)
    {
        try
        {
            _tcp = new TcpListener(IPAddress.IPv6Any, TcpPort);
            _tcp.Server.DualMode = true;
            _tcp.Start();
        }
        catch
        {
            try { _tcp = new TcpListener(IPAddress.Any, TcpPort); _tcp.Start(); }
            catch (Exception ex) { AppLog.Write("MchatEco", "TCP 监听失败: " + ex.Message); return; }
        }
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await _tcp.AcceptTcpClientAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleClient(client), token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Write("MchatEco", "TCP accept: " + ex.Message); }
    }

    static void HandleClient(TcpClient client)
    {
        try
        {
            using (client)
            using (NetworkStream s = client.GetStream())
            {
                int hlen = ReadLen(s, MaxHeader);
                if (hlen <= 0) return;
                byte[] hb = ReadExact(s, hlen);
                int blen = ReadLen(s, MaxBody);
                byte[] body = blen > 0 ? ReadExact(s, blen) : Array.Empty<byte>();
                string header = Encoding.UTF8.GetString(hb);
                Dictionary<string, object> msg = ParseJson(header);
                if (msg != null)
                {
                    if (body != null && body.Length > 0) msg["body"] = body;
                    string ip = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                    string id = Str(msg, "id");
                    bool fresh;
                    lock (_seenGate)
                    {
                        // 去重 ID 永久累积会让常驻进程内存单调增长；上限到了整表清空，
                        // 去重本是 best-effort，重复消息间隔通常很短，清空不会造成可见问题。
                        if (_seen.Count >= 4096) _seen.Clear();
                        fresh = !string.IsNullOrEmpty(id) && _seen.Add(id);
                    }
                    if (fresh)
                    {
                        var h = MessageReceived;
                        if (h != null) try { h(msg, ip); } catch { }
                    }
                }
                try { s.WriteByte((byte)'K'); s.Flush(); } catch { }
            }
        }
        catch { }
    }

    static int ReadLen(Stream s, int max)
    {
        byte[] b = ReadExact(s, 4);
        if (b == null || b.Length < 4) return -1;
        int v = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(b, 0));
        return v >= 0 && v <= max ? v : -1;
    }

    static byte[] ReadExact(Stream s, int n)
    {
        byte[] b = new byte[n];
        int off = 0;
        while (off < n)
        {
            int r = s.Read(b, off, n - off);
            if (r <= 0) break;
            off += r;
        }
        return off == n ? b : Array.Empty<byte>();
    }

    static Dictionary<string, object> ParseJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using (var doc = System.Text.Json.JsonDocument.Parse(text))
            {
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
                var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    switch (p.Value.ValueKind)
                    {
                        case System.Text.Json.JsonValueKind.String: d[p.Name] = p.Value.GetString() ?? ""; break;
                        case System.Text.Json.JsonValueKind.Number:
                            d[p.Name] = p.Value.TryGetInt64(out var l) ? l : p.Value.GetDouble(); break;
                        case System.Text.Json.JsonValueKind.True: d[p.Name] = true; break;
                        case System.Text.Json.JsonValueKind.False: d[p.Name] = false; break;
                        default: d[p.Name] = p.Value.ToString(); break;
                    }
                }
                return d;
            }
        }
        catch { return null; }
    }

    static string Str(Dictionary<string, object> d, string key)
    {
        if (d == null || !d.TryGetValue(key, out var v) || v == null) return "";
        return v.ToString();
    }
}
