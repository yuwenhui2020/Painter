using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace desk;

/// <summary>RawWsServer 解析出的握手信息：请求路径、查询串、对端 IP。</summary>
internal sealed class RawWsRequest
{
    public string Path = "/";
    public string RemoteIp = "?";
    public readonly Dictionary<string, string> Query = new(StringComparer.OrdinalIgnoreCase);

    public string? GetQuery(string name) => Query.TryGetValue(name, out var v) ? v : null;
}

/// <summary>
/// Windows 7 兼容的 WebSocket 服务。
/// HTTP.sys 从 Windows 8 才支持 WebSocket 升级（Win7 上 HttpListener.AcceptWebSocketAsync
/// 直接抛 PlatformNotSupportedException），所以这里用普通 TcpListener 手写 RFC6455 握手与分帧。
/// 连接交给 <see cref="RawWebSocket"/>（WebSocket 子类）后，WebServer 里原有的 WS 处理逻辑无需改动。
/// 端口取 httpPort+1..httpPort+12 中第一个可用的（全占用时让系统分配临时端口），
/// 前端通过 GET /wsconfig 获知；启动失败会由 WebServer 每 10 秒自动重试。
/// </summary>
internal sealed class RawWsServer : IDisposable
{
    private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const int MaxHeaderBytes = 16 * 1024;

    private readonly int _basePort;
    private readonly bool _loopbackOnly;
    private readonly Func<RawWsRequest, RawWebSocket, CancellationToken, Task> _handler;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;

    public int Port { get; private set; }

    public RawWsServer(int basePort, bool loopbackOnly,
        Func<RawWsRequest, RawWebSocket, CancellationToken, Task> handler)
    {
        _basePort = basePort;
        _loopbackOnly = loopbackOnly;
        _handler = handler;
    }

    public void Start()
    {
        // 老机器上 httpPort+1..+4 常被其它服务占满，会让 /wsconfig 永远返回
        // wsPort=0、Win7 上全部 Web 远控失效。把候选扩到 +12，最后让系统从临时
        // 端口里分配一个（port=0），保证兼容通道总能拿到可用端口。
        const int MaxOffset = 12;
        for (int offset = 1; offset <= MaxOffset; offset++)
        {
            int port = _basePort + offset;
            if (port > 65535) break;
            var listener = TryBind(port);
            if (listener == null) continue;
            Bind(listener, port);
            return;
        }
        // 系统临时端口不在防火墙的 +1..+12 放行范围内，局域网会被拦。
        // 只有回环模式（本机访问，无需放行）才允许用临时端口兜底；局域网模式下
        // 宁可抛错让 WebServer 每 10 秒重试，也不留下「连得上但进不来」的假成功。
        if (_loopbackOnly)
        {
            var dynamicListener = TryBind(0);
            if (dynamicListener != null)
            {
                int actual = ((IPEndPoint)dynamicListener.LocalEndpoint).Port;
                Bind(dynamicListener, actual);
                return;
            }
        }
        throw new InvalidOperationException(
            $"WebSocket 兼容通道无法绑定端口（已尝试 {_basePort + 1}~{_basePort + MaxOffset}），请检查端口是否被占用或稍后自动重试");
    }

    private void Bind(TcpListener listener, int port)
    {
        _listener = listener;
        Port = port;
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private TcpListener? TryBind(int port)
    {
        // HTTP 只绑定了回环时（没拿到 URL ACL），兼容通道也必须只监听回环，
        // 否则会凭空把端口暴露到局域网。
        if (_loopbackOnly)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, port);
                l.Start(16);
                return l;
            }
            catch { return null; }
        }

        // 先按双栈绑定，localhost 解析到 ::1 时也能连上；IPv6 栈不可用时退回纯 IPv4。
        // 不设 SO_REUSEADDR：它允许多个套接字同时绑同一端口，在 Win7 上容易造成
        // 「启动成功但连接被别人抢走」的假成功。
        try
        {
            var l = new TcpListener(IPAddress.IPv6Any, port);
            bool dual = true;
            try { l.Server.DualMode = true; } catch { dual = false; }
            if (dual)
            {
                l.Start(16);
                return l;
            }
            try { l.Server.Close(); } catch { }
        }
        catch { }

        try
        {
            var l = new TcpListener(IPAddress.Any, port);
            l.Start(16);
            return l;
        }
        catch { return null; }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { break; }
            catch when (ct.IsCancellationRequested) { break; }
            catch
            {
                await Task.Delay(100).ConfigureAwait(false);
                continue;
            }
            _ = Task.Run(() => ServeClientAsync(client, ct));
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken serverCt)
    {
        NetworkStream? stream = null;
        RawWebSocket? ws = null;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        // 握手阶段加超时，防止半开连接堆积；握手成功后取消超时，长连接不受影响。
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            client.NoDelay = true;
            stream = client.GetStream();
            var handshake = await ReadHandshakeAsync(stream, client, cts.Token).ConfigureAwait(false);
            if (handshake.Request == null)
            {
                await WriteHttpErrorAsync(stream, handshake.Error ?? "400 Bad Request").ConfigureAwait(false);
                return;
            }

            string accept;
            using (var sha1 = SHA1.Create())
            {
                accept = Convert.ToBase64String(sha1.ComputeHash(
                    Encoding.ASCII.GetBytes(handshake.Key + WsGuid)));
            }
            string response = "HTTP/1.1 101 Switching Protocols\r\n" +
                              "Upgrade: websocket\r\n" +
                              "Connection: Upgrade\r\n" +
                              "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
            var head = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(head, 0, head.Length, cts.Token).ConfigureAwait(false);
            await stream.FlushAsync(cts.Token).ConfigureAwait(false);

            cts.CancelAfter(Timeout.Infinite);
            // 握手包可能和首帧粘在同一个 TCP 段里；多读到的字节交给 RawWebSocket 当预读缓冲，
            // 否则这些帧会被丢掉（个别客户端不等待 101 就直接发数据）。
            ws = new RawWebSocket(client, stream, handshake.Request.RemoteIp, handshake.Leftover);
            await _handler(handshake.Request, ws, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // 握手失败 / 对端断开 / 服务停止，均属正常收尾
        }
        finally
        {
            try { ws?.Dispose(); } catch { }
            try { stream?.Dispose(); } catch { }
            try { client.Dispose(); } catch { }
            try { cts.Dispose(); } catch { }
        }
    }

    private sealed class HandshakeResult
    {
        public RawWsRequest? Request;
        public string Key = "";
        public string? Error;
        /// <summary>握手头之后已经读到、尚未消费的字节（可能是客户端首帧）。</summary>
        public byte[]? Leftover;
    }

    /// <summary>读取并校验握手请求；不合法时 Request 为 null，Error 带 HTTP 状态描述。</summary>
    private static async Task<HandshakeResult> ReadHandshakeAsync(
        NetworkStream stream, TcpClient client, CancellationToken ct)
    {
        var result = new HandshakeResult();
        var buf = new byte[MaxHeaderBytes];
        int len = 0;
        int end = -1;
        while (len < buf.Length)
        {
            int n = await stream.ReadAsync(buf, len, buf.Length - len, ct).ConfigureAwait(false);
            if (n <= 0) return result;
            len += n;
            string probe = Encoding.ASCII.GetString(buf, 0, len);
            end = probe.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0) break;
        }
        if (end < 0) { result.Error = "431 Request Header Fields Too Large"; return result; }

        string headText = Encoding.ASCII.GetString(buf, 0, end);
        string[] lines = headText.Split(new[] { "\r\n" }, StringSplitOptions.None);
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length < 3 || !string.Equals(requestLine[0], "GET", StringComparison.OrdinalIgnoreCase))
        {
            result.Error = "405 Method Not Allowed";
            return result;
        }

        string target = requestLine[1];
        if (!target.StartsWith("/", StringComparison.Ordinal))
        {
            result.Error = "400 Bad Request";
            return result;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
        }

        if (!headers.TryGetValue("Upgrade", out var upgrade) ||
            upgrade.IndexOf("websocket", StringComparison.OrdinalIgnoreCase) < 0)
        {
            result.Error = "400 Bad Request";
            return result;
        }
        if (!headers.TryGetValue("Sec-WebSocket-Version", out var version) || version != "13")
        {
            result.Error = "426 Upgrade Required";
            return result;
        }
        if (!headers.TryGetValue("Sec-WebSocket-Key", out var key) || key.Length == 0)
        {
            result.Error = "400 Bad Request";
            return result;
        }

        var req = new RawWsRequest();
        int q = target.IndexOf('?');
        string rawPath = q >= 0 ? target.Substring(0, q) : target;
        try { req.Path = WebUtility.UrlDecode(rawPath); } catch { req.Path = rawPath; }
        if (q >= 0) ParseQuery(target.Substring(q + 1), req.Query);
        try
        {
            if (client.Client.RemoteEndPoint is IPEndPoint ep)
            {
                // 双栈监听时 IPv4 客户端显示为 ::ffff:a.b.c.d，转回更易读的 IPv4 形式
                var addr = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
                req.RemoteIp = addr.ToString();
            }
        }
        catch
        {
        }
        result.Request = req;
        result.Key = key;
        int consumed = end + 4;
        if (consumed < len)
        {
            result.Leftover = new byte[len - consumed];
            Buffer.BlockCopy(buf, consumed, result.Leftover, 0, result.Leftover.Length);
        }
        return result;
    }

    private static void ParseQuery(string raw, Dictionary<string, string> into)
    {
        foreach (var part in raw.Split('&'))
        {
            if (part.Length == 0) continue;
            int eq = part.IndexOf('=');
            string k = eq >= 0 ? part.Substring(0, eq) : part;
            string v = eq >= 0 ? part.Substring(eq + 1) : "";
            try
            {
                k = WebUtility.UrlDecode(k);
                v = WebUtility.UrlDecode(v);
            }
            catch
            {
            }
            if (k.Length > 0) into[k] = v;
        }
    }

    private static async Task WriteHttpErrorAsync(NetworkStream stream, string status)
    {
        try
        {
            var body = Encoding.ASCII.GetBytes(status);
            string head = "HTTP/1.1 " + status + "\r\nConnection: close\r\nContent-Length: " + body.Length + "\r\n\r\n";
            var headBytes = Encoding.ASCII.GetBytes(head);
            await stream.WriteAsync(headBytes, 0, headBytes.Length).ConfigureAwait(false);
            await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener = null; } catch { }
        try { _cts.Dispose(); } catch { }
    }
}

/// <summary>
/// 建立在普通 TCP 之上的 <see cref="WebSocket"/> 实现（仅服务端，帧不加掩码）。
/// 只覆盖 WebServer 实际用到的能力：文本/二进制收发、分片、ping/pong、close。
/// </summary>
internal sealed class RawWebSocket : WebSocket
{
    // 与 Protocol.MaxMessageBytes 对齐；实际视频帧受 RemoteDesktopServer.MaxPayloadBytes
    // 约束（约 1.2MB），这里放宽上限只是让兼容通道与 HTTP.sys WebSocket 能力一致。
    private const int MaxMessageBytes = 64 * 1024 * 1024;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _recvLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private WebSocketState _state = WebSocketState.Open;
    private WebSocketCloseStatus? _closeStatus;
    private string? _closeDescription;
    private bool _closeSent;

    // 已收齐、等待交给调用方的一整条消息（用于调用方缓冲区小于消息时多次交付）
    private byte[]? _pending;
    private int _pendingOffset;
    private WebSocketMessageType _pendingType;

    // 与握手包同一个 TCP 段里多读到的字节，按流的前缀消费
    private readonly byte[]? _preBuffer;
    private int _preOffset;

    // 正在拼装的分片消息
    private MemoryStream? _fragment;
    private WebSocketMessageType _fragmentType;

    public RawWebSocket(TcpClient client, NetworkStream stream, string peer, byte[]? preBuffer = null)
    {
        _client = client;
        _stream = stream;
        _preBuffer = preBuffer;
        Peer = peer;
    }

    public string Peer { get; }

    public override WebSocketCloseStatus? CloseStatus => _closeStatus;
    public override string? CloseStatusDescription => _closeDescription;
    public override WebSocketState State => _state;
    public override string? SubProtocol => null;

    // 是否正处于一条分片消息的中间（兼容通道需自己维护，HTTP.sys 由框架维护）
    private bool _sendingFragment;

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
        bool endOfMessage, CancellationToken cancellationToken)
    {
        if (_state != WebSocketState.Open)
            throw new WebSocketException(WebSocketError.InvalidState, "WebSocket is not open.");
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 首帧带 Text/Binary opcode，续帧必须是 0x0；HTTP.sys WebSocket 由框架
            // 处理，这里显式对齐，避免分片调用时产出非法帧。
            byte opcode = _sendingFragment
                ? (byte)0x0
                : (messageType == WebSocketMessageType.Text ? (byte)0x1 : (byte)0x2);
            _sendingFragment = !endOfMessage;
            byte[] header = BuildHeader(opcode, endOfMessage, buffer.Count);
            await _stream.WriteAsync(header, 0, header.Length, cancellationToken).ConfigureAwait(false);
            if (buffer.Count > 0 && buffer.Array != null)
                await _stream.WriteAsync(buffer.Array, buffer.Offset, buffer.Count, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private Task WriteFrameAsync(byte opcode, byte[] payload, CancellationToken ct)
    {
        byte[] header = BuildHeader(opcode, true, payload.Length);
        return WriteFrameCoreAsync(header, payload, 0, payload.Length, ct);
    }

    private async Task WriteFrameCoreAsync(byte[] header, byte[] payload, int offset, int count, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(header, 0, header.Length, ct).ConfigureAwait(false);
            if (count > 0) await _stream.WriteAsync(payload, offset, count, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static byte[] BuildHeader(byte opcode, bool fin, int length)
    {
        byte[] header;
        if (length < 126)
        {
            header = new byte[2];
            header[1] = (byte)length;
        }
        else if (length <= 65535)
        {
            header = new byte[4];
            header[1] = 126;
            header[2] = (byte)(length >> 8);
            header[3] = (byte)length;
        }
        else
        {
            header = new byte[10];
            header[1] = 127;
            long l = length;
            for (int i = 0; i < 8; i++) header[9 - i] = (byte)(l >> (8 * i));
        }
        header[0] = (byte)((fin ? 0x80 : 0x00) | (opcode & 0x0F));
        return header;
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,
        CancellationToken cancellationToken)
    {
        await _recvLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReceiveCoreAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { _recvLock.Release(); } catch { }
        }
    }

    private async Task<WebSocketReceiveResult> ReceiveCoreAsync(ArraySegment<byte> buffer, CancellationToken ct)
    {
        while (true)
        {
            if (_pending != null) return Deliver(buffer);
            if (_state == WebSocketState.Closed || _state == WebSocketState.Aborted)
                return ClosedResult();

            byte[] head = new byte[2];
            await ReadExactAsync(head, 0, 2, ct).ConfigureAwait(false);
            bool fin = (head[0] & 0x80) != 0;
            int rsv = head[0] & 0x70;
            int opcode = head[0] & 0x0F;
            bool masked = (head[1] & 0x80) != 0;
            long length = head[1] & 0x7F;

            if (length == 126)
            {
                var ext = new byte[2];
                await ReadExactAsync(ext, 0, 2, ct).ConfigureAwait(false);
                length = (ext[0] << 8) | ext[1];
            }
            else if (length == 127)
            {
                var ext = new byte[8];
                await ReadExactAsync(ext, 0, 8, ct).ConfigureAwait(false);
                length = 0;
                for (int i = 0; i < 8; i++) length = (length << 8) | ext[i];
            }

            if (rsv != 0)
            {
                await FailAsync(WebSocketCloseStatus.ProtocolError, "RSV bits set", ct).ConfigureAwait(false);
                return ClosedResult();
            }
            if (length < 0 || length > MaxMessageBytes)
            {
                await FailAsync(WebSocketCloseStatus.MessageTooBig, "message too big", ct).ConfigureAwait(false);
                return ClosedResult();
            }
            if (!masked)
            {
                // RFC6455：客户端到服务端的帧必须掩码
                await FailAsync(WebSocketCloseStatus.ProtocolError, "unmasked client frame", ct).ConfigureAwait(false);
                return ClosedResult();
            }

            var mask = new byte[4];
            await ReadExactAsync(mask, 0, 4, ct).ConfigureAwait(false);
            int count = (int)length;
            var payload = count == 0 ? Array.Empty<byte>() : new byte[count];
            if (count > 0)
            {
                await ReadExactAsync(payload, 0, count, ct).ConfigureAwait(false);
                for (int i = 0; i < count; i++) payload[i] ^= mask[i & 3];
            }

            if (opcode == 0x8)
            {
                HandleClosePayload(payload);
                try
                {
                    await SendCloseAsync(_closeStatus ?? WebSocketCloseStatus.NormalClosure, null, ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                }
                _state = WebSocketState.Closed;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, _closeStatus, _closeDescription);
            }
            if (opcode == 0x9)
            {
                try { await WriteFrameAsync(0xA, payload, ct).ConfigureAwait(false); } catch { }
                continue;
            }
            if (opcode == 0xA) continue;

            bool isText = opcode == 0x1;
            bool isBinary = opcode == 0x2;
            bool isContinuation = opcode == 0x0;
            if (!isText && !isBinary && !isContinuation)
            {
                await FailAsync(WebSocketCloseStatus.ProtocolError, "unknown opcode", ct).ConfigureAwait(false);
                return ClosedResult();
            }
            if (isContinuation && _fragment == null)
            {
                await FailAsync(WebSocketCloseStatus.ProtocolError, "unexpected continuation", ct).ConfigureAwait(false);
                return ClosedResult();
            }
            if ((isText || isBinary) && _fragment != null)
            {
                await FailAsync(WebSocketCloseStatus.ProtocolError, "nested data frame", ct).ConfigureAwait(false);
                return ClosedResult();
            }

            if (isText || isBinary)
            {
                if (fin)
                {
                    _pending = payload;
                    _pendingOffset = 0;
                    _pendingType = isText ? WebSocketMessageType.Text : WebSocketMessageType.Binary;
                }
                else
                {
                    try { _fragment?.Dispose(); } catch { } // 上一条未完成的分片（连接中断残留）先释放
                    _fragment = new MemoryStream();
                    _fragmentType = isText ? WebSocketMessageType.Text : WebSocketMessageType.Binary;
                    _fragment.Write(payload, 0, payload.Length);
                }
            }
            else // continuation
            {
                if (_fragment!.Length + payload.Length > MaxMessageBytes)
                {
                    await FailAsync(WebSocketCloseStatus.MessageTooBig, "message too big", ct).ConfigureAwait(false);
                    return ClosedResult();
                }
                _fragment.Write(payload, 0, payload.Length);
                if (fin)
                {
                    _pending = _fragment.ToArray();
                    _pendingOffset = 0;
                    _pendingType = _fragmentType;
                    try { _fragment.Dispose(); } catch { } // 分片流用完即释放
                    _fragment = null;
                }
            }

            if (_pending != null) return Deliver(buffer);
        }
    }

    private WebSocketReceiveResult Deliver(ArraySegment<byte> buffer)
    {
        var src = _pending!;
        int remaining = src.Length - _pendingOffset;
        int n = Math.Min(buffer.Count, remaining);
        if (n > 0 && buffer.Array != null)
            Buffer.BlockCopy(src, _pendingOffset, buffer.Array, buffer.Offset, n);
        _pendingOffset += n;
        bool end = _pendingOffset >= src.Length;
        var type = _pendingType;
        if (end)
        {
            _pending = null;
            _pendingOffset = 0;
        }
        return new WebSocketReceiveResult(n, type, end);
    }

    private WebSocketReceiveResult ClosedResult() =>
        new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, _closeStatus, _closeDescription);

    private void HandleClosePayload(byte[] payload)
    {
        if (payload.Length >= 2)
        {
            _closeStatus = (WebSocketCloseStatus)((payload[0] << 8) | payload[1]);
            if (payload.Length > 2)
            {
                try { _closeDescription = Encoding.UTF8.GetString(payload, 2, payload.Length - 2); } catch { }
            }
        }
    }

    /// <summary>协议错误：回一帧 close 并进入 Closed，调用方随后会退出循环。</summary>
    private async Task FailAsync(WebSocketCloseStatus status, string reason, CancellationToken ct)
    {
        try { await SendCloseAsync(status, reason, ct).ConfigureAwait(false); } catch { }
        _state = WebSocketState.Closed;
    }

    private async Task SendCloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
    {
        if (_closeSent) return;
        _closeSent = true;

        byte[] reason = Array.Empty<byte>();
        if (!string.IsNullOrEmpty(description))
        {
            var all = Encoding.UTF8.GetBytes(description);
            int n = Math.Min(all.Length, 123);
            reason = new byte[n];
            Buffer.BlockCopy(all, 0, reason, 0, n);
        }
        var payload = new byte[2 + reason.Length];
        payload[0] = (byte)(((int)status >> 8) & 0xFF);
        payload[1] = (byte)((int)status & 0xFF);
        if (reason.Length > 0) Buffer.BlockCopy(reason, 0, payload, 2, reason.Length);

        await WriteFrameAsync(0x8, payload, ct).ConfigureAwait(false);
        if (_state == WebSocketState.Open) _state = WebSocketState.CloseSent;
    }

    public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
        CancellationToken cancellationToken)
    {
        if (_state == WebSocketState.Closed || _state == WebSocketState.Aborted) return;
        try { await SendCloseAsync(closeStatus, statusDescription, cancellationToken).ConfigureAwait(false); }
        catch { }

        // 等对端回 close（最多 1 秒）；超时/被取消都直接结束，避免拖住调用方
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(1000);
            var buf = new byte[256];
            while (_state == WebSocketState.CloseSent && !cts.IsCancellationRequested)
            {
                var r = await ReceiveAsync(new ArraySegment<byte>(buf), cts.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch
        {
        }
        finally
        {
            _state = WebSocketState.Closed;
        }
    }

    public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
        CancellationToken cancellationToken)
    {
        if (_state == WebSocketState.Closed || _state == WebSocketState.Aborted) return;
        try { await SendCloseAsync(closeStatus, statusDescription, cancellationToken).ConfigureAwait(false); }
        catch { }
    }

    private async Task ReadExactAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int read = 0;
        if (_preBuffer != null && _preOffset < _preBuffer.Length)
        {
            int n = Math.Min(count, _preBuffer.Length - _preOffset);
            Buffer.BlockCopy(_preBuffer, _preOffset, buffer, offset, n);
            _preOffset += n;
            read = n;
        }
        while (read < count)
        {
            int n = await _stream.ReadAsync(buffer, offset + read, count - read, ct).ConfigureAwait(false);
            if (n <= 0) throw new IOException("WebSocket 连接已断开");
            read += n;
        }
    }

    public override void Abort()
    {
        _state = WebSocketState.Aborted;
        try { _fragment?.Dispose(); _fragment = null; } catch { }
        try { _stream.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
    }

    public override void Dispose()
    {
        if (_state != WebSocketState.Closed && _state != WebSocketState.Aborted)
            _state = WebSocketState.Closed;
        try { _fragment?.Dispose(); _fragment = null; } catch { }
        try { _stream.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
    }
}
