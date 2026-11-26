using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace desk;

/// <summary>
/// 不依赖 HTTP.sys / URL ACL / Windows 防火墙例外的纯 TCP Web 服务器。
/// 作为 HttpListener 方案失效（典型：Win7 无法注册 URL ACL、弱通配符导致
/// ERR_HTTP_RESPONSE_CODE_FAILURE）时的兜底，保证网页控制台仍能打开。
///
/// 同一个 TcpListener 上同时处理普通 HTTP 与 WebSocket 升级：
///   · HTTP 请求通过 <see cref="HttpHandler"/> 委托交给 WebServer 用同一套路由生成响应；
///   · WS 升级复用 <see cref="RawWebSocket"/>，再交给 <see cref="WsHandler"/> 分发，
///     与兼容通道走完全相同的 HandleWsCoreAsync / HandlePptWsCoreAsync / HandleShowWsCoreAsync。
///
/// 只用 System.Net.Sockets / 基础类库，不触碰任何系统 HTTP 组件，普通用户权限即可绑定。
/// </summary>
internal sealed class RawHttpServer : IDisposable
{
    private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxBodyBytes = 8 * 1024 * 1024;

    /// <summary>一次普通 HTTP 请求的输入。</summary>
    internal sealed class Request
    {
        public string Method = "GET";
        public string Path = "/";
        public string RemoteIp = "?";
        public readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Query = new(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = Array.Empty<byte>();
        public string GetQuery(string name) => Query.TryGetValue(name, out var v) ? v : null;
    }

    /// <summary>普通 HTTP 请求的响应。</summary>
    internal sealed class Response
    {
        public int Status = 200;
        public string ContentType = "text/plain; charset=utf-8";
        public byte[] Body = Array.Empty<byte>();
        public bool NoStore;
        public string? ContentDisposition;
    }

    private readonly int _port;
    private readonly bool _loopbackOnly;
    private readonly Func<Request, Response> _httpHandler;
    private readonly Func<RawWsRequest, RawWebSocket, CancellationToken, Task> _wsHandler;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;

    public int Port { get; private set; }

    public RawHttpServer(int port, bool loopbackOnly,
        Func<Request, Response> httpHandler,
        Func<RawWsRequest, RawWebSocket, CancellationToken, Task> wsHandler)
    {
        _port = port;
        _loopbackOnly = loopbackOnly;
        _httpHandler = httpHandler;
        _wsHandler = wsHandler;
    }

    public void Start()
    {
        var listener = TryBind(_port)
            ?? throw new InvalidOperationException($"纯 TCP Web 服务无法绑定端口 {_port}，请检查端口占用");
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private TcpListener? TryBind(int port)
    {
        if (_loopbackOnly)
        {
            try { var l = new TcpListener(IPAddress.Loopback, port); l.Start(16); return l; }
            catch { return null; }
        }
        // 双栈优先，兼容 localhost 解析到 ::1 的客户端；失败退回纯 IPv4。
        try
        {
            var l = new TcpListener(IPAddress.IPv6Any, port);
            bool dual = true;
            try { l.Server.DualMode = true; } catch { dual = false; }
            if (dual) { l.Start(16); return l; }
            try { l.Server.Close(); } catch { }
        }
        catch { }
        try { var l = new TcpListener(IPAddress.Any, port); l.Start(16); return l; }
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
            catch { await Task.Delay(100).ConfigureAwait(false); continue; }
            _ = Task.Run(() => ServeClientAsync(client, ct));
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken serverCt)
    {
        NetworkStream? stream = null;
        RawWebSocket? ws = null;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            client.NoDelay = true;
            stream = client.GetStream();
            var parsed = await ReadRequestAsync(stream, client, cts.Token).ConfigureAwait(false);
            if (parsed == null) return;

            if (parsed.IsWebSocket)
            {
                string accept;
                using (var sha1 = SHA1.Create())
                {
                    accept = Convert.ToBase64String(sha1.ComputeHash(
                        Encoding.ASCII.GetBytes(parsed.WsKey + WsGuid)));
                }
                string response = "HTTP/1.1 101 Switching Protocols\r\n" +
                                  "Upgrade: websocket\r\n" +
                                  "Connection: Upgrade\r\n" +
                                  "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
                var head = Encoding.ASCII.GetBytes(response);
                await stream.WriteAsync(head, 0, head.Length, cts.Token).ConfigureAwait(false);
                await stream.FlushAsync(cts.Token).ConfigureAwait(false);
                cts.CancelAfter(Timeout.Infinite);
                ws = new RawWebSocket(client, stream, parsed.RemoteIp, parsed.Leftover);
                await _wsHandler(parsed.WsRequest!, ws, cts.Token).ConfigureAwait(false);
                return;
            }

            var req = new Request {
                Method = parsed.Method!,
                Path = parsed.Path!,
                RemoteIp = parsed.RemoteIp,
                Body = parsed.Body ?? Array.Empty<byte>(),
            };
            foreach (var kv in parsed.Headers!) req.Headers[kv.Key] = kv.Value;
            foreach (var kv in parsed.Query!) req.Query[kv.Key] = kv.Value;

            Response resp;
            try { resp = _httpHandler(req) ?? new Response { Status = 500, Body = Encoding.UTF8.GetBytes("handler returned null") }; }
            catch (Exception ex) { resp = new Response { Status = 500, Body = Encoding.UTF8.GetBytes("handler error: " + ex.Message) }; }
            await WriteResponseAsync(stream, resp, cts.Token).ConfigureAwait(false);
        }
        catch { }
        finally
        {
            try { ws?.Dispose(); } catch { }
            try { stream?.Dispose(); } catch { }
            try { client.Dispose(); } catch { }
            try { cts.Dispose(); } catch { }
        }
    }

    private static async Task WriteResponseAsync(NetworkStream stream, Response resp, CancellationToken ct)
    {
        var body = resp.Body ?? Array.Empty<byte>();
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(resp.Status).Append(' ').Append(Reason(resp.Status)).Append("\r\n");
        sb.Append("Content-Type: ").Append(resp.ContentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        sb.Append("Connection: close\r\n");
        if (resp.NoStore) sb.Append("Cache-Control: no-store\r\n");
        if (!string.IsNullOrEmpty(resp.ContentDisposition))
            sb.Append("Content-Disposition: ").Append(resp.ContentDisposition).Append("\r\n");
        sb.Append("Access-Control-Allow-Origin: *\r\n\r\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(head, 0, head.Length, ct).ConfigureAwait(false);
        if (body.Length > 0) await stream.WriteAsync(body, 0, body.Length, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string Reason(int code) => code switch
    {
        200 => "OK", 204 => "No Content", 301 => "Moved Permanently", 400 => "Bad Request",
        403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed",
        500 => "Internal Server Error", 503 => "Service Unavailable", _ => "OK"
    };

    private sealed class ParsedRequest
    {
        public string? Method, Path, WsKey, Error;
        public string RemoteIp = "?";
        public Dictionary<string, string>? Headers, Query;
        public byte[]? Body;
        public bool IsWebSocket;
        public byte[]? Leftover;
        public RawWsRequest? WsRequest;
    }

    private static async Task<ParsedRequest?> ReadRequestAsync(NetworkStream stream, TcpClient client, CancellationToken ct)
    {
        var result = new ParsedRequest();
        var buf = new byte[MaxHeaderBytes];
        int len = 0, headerEnd = -1;
        while (len < buf.Length)
        {
            int n = await stream.ReadAsync(buf, len, buf.Length - len, ct).ConfigureAwait(false);
            if (n <= 0) return null;
            len += n;
            int idx = IndexOf(buf, len, "\r\n\r\n");
            if (idx >= 0) { headerEnd = idx; break; }
        }
        if (headerEnd < 0) return null;
        string headText = Encoding.ASCII.GetString(buf, 0, headerEnd);
        string[] lines = headText.Split(new[] { "\r\n" }, StringSplitOptions.None);
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;
        result.Method = requestLine[0].ToUpperInvariant();
        string target = requestLine[1];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon > 0) headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
        }
        result.Headers = headers;
        int q = target.IndexOf('?');
        string rawPath = q >= 0 ? target.Substring(0, q) : target;
        try { result.Path = WebUtility.UrlDecode(rawPath); } catch { result.Path = rawPath; }
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (q >= 0)
        {
            foreach (var part in target.Substring(q + 1).Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                string k = eq >= 0 ? part.Substring(0, eq) : part;
                string v = eq >= 0 ? part.Substring(eq + 1) : "";
                try { k = WebUtility.UrlDecode(k); v = WebUtility.UrlDecode(v); } catch { }
                if (k.Length > 0) query[k] = v;
            }
        }
        result.Query = query;
        try
        {
            if (client.Client.RemoteEndPoint is IPEndPoint ep)
            {
                var addr = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
                result.RemoteIp = addr.ToString();
            }
        }
        catch { }

        int consumed = headerEnd + 4;
        int bodyLen = 0;
        if (headers.TryGetValue("Content-Length", out var cl)) int.TryParse(cl, out bodyLen);
        if (bodyLen < 0) bodyLen = 0;
        if (bodyLen > MaxBodyBytes) bodyLen = MaxBodyBytes;

        // 头部与请求体不能共用 64KB 缓冲：图片上传的 base64 JSON 常超 100KB，
        // 共用会被 buf.Length 卡住而静默截断。这里按 Content-Length 单独把请求体读满。
        int already = Math.Max(0, Math.Min(len - consumed, bodyLen));
        byte[] body = new byte[bodyLen];
        if (already > 0) Buffer.BlockCopy(buf, consumed, body, 0, already);
        int bodyRead = already;
        while (bodyRead < bodyLen)
        {
            int n = await stream.ReadAsync(body, bodyRead, bodyLen - bodyRead, ct).ConfigureAwait(false);
            if (n <= 0) break;
            bodyRead += n;
        }
        if (bodyRead == bodyLen) result.Body = body;
        else
        {
            result.Body = new byte[bodyRead];
            if (bodyRead > 0) Buffer.BlockCopy(body, 0, result.Body, 0, bodyRead);
        }

        string? upgrade = null;
        headers.TryGetValue("Upgrade", out upgrade);
        result.IsWebSocket = upgrade != null && upgrade.IndexOf("websocket", StringComparison.OrdinalIgnoreCase) >= 0;
        if (result.IsWebSocket)
        {
            headers.TryGetValue("Sec-WebSocket-Version", out var ver);
            headers.TryGetValue("Sec-WebSocket-Key", out var key);
            if (ver != "13" || string.IsNullOrEmpty(key)) { result.Error = "426 Upgrade Required"; result.IsWebSocket = false; }
            else result.WsKey = key;
        }
        // 预读缓冲里可能还含本次请求体之外的数据（WS 升级后的帧或流水线下一个请求）。
        // 只把超出请求体的部分作为 Leftover 交给 RawWebSocket，避免重复传递 body 字节。
        int bodyEndInBuf = Math.Min(len, consumed + already);
        if (bodyEndInBuf < len)
        {
            int left = len - bodyEndInBuf;
            result.Leftover = new byte[left];
            Buffer.BlockCopy(buf, bodyEndInBuf, result.Leftover, 0, left);
        }
        result.WsRequest = new RawWsRequest { Path = result.Path ?? "/", RemoteIp = result.RemoteIp };
        foreach (var kv in query) result.WsRequest.Query[kv.Key] = kv.Value;
        return result;
    }

    private static int IndexOf(byte[] buf, int len, string text)
    {
        var needle = Encoding.ASCII.GetBytes(text);
        for (int i = 0; i <= len - needle.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++) { if (buf[i + j] != needle[j]) { ok = false; break; } }
            if (ok) return i;
        }
        return -1;
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
