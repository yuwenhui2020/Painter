using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Windows.Forms;
using Painter.Helpers;

namespace desk;

/// <summary>
/// Web 网关：把"被控端(S端)"的画面通过 WebSocket 扇出给任意多个浏览器，
/// 并把任意浏览器的输入扇入到同一条 S端 查看端连接。
///
/// 设计要点：
///   · 本进程内只与 S端 建立【一条】TCP 查看端连接（自动用密码登录），
///     因此 S端 仍是"单查看端"，但一个桌面会话可被 N 个浏览器同时观看/控制 —— 正是多并发需求。
///   · 所有浏览器共享这一条 S端 连接：S端 推来的画面帧被广播给每个浏览器（每个浏览器有独立的
///     有界通道，慢客户端自动丢弃最旧帧，不会拖垮广播）；任意浏览器发来的输入被串行写入 S端。
///   · S端 连接断开会自动重连并重新登录；浏览器连接/断开互不影响。
/// </summary>
internal sealed class WebServer : IDisposable
{
    private readonly int _httpPort;
    private readonly int _serverTcpPort;
    private string _password;

    /// <summary>远程访问密码（可运行时变更，用于托盘菜单"启用/关闭密码"立即生效）。</summary>
    public string Password { get => _password; set => _password = value; }
    private readonly string _wwwRoot;

    private string _boundPrefix = "";

    /// <summary>
    /// 只绑定了 http://127.0.0.1:端口/（URL ACL / 防火墙未放行），局域网无法访问。
    /// 供 UI 明确提示用户去重新放行并重启 Web 服务，避免照着局域网 IP 白试。
    /// </summary>
    public bool IsLoopbackOnly => _boundPrefix.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase);

    private HttpListener? _listener = null!;
    private CancellationTokenSource _cts = null!;

    // Win7 兼容 WebSocket 通道（HTTP.sys 在 Win7 上没有 WebSocket 支持）
    private RawWsServer? _rawWs;
    public int CompatWsPort => _rawWs?.Port ?? 0;

    // 纯 TCP Web 服务：HttpListener（HTTP.sys / URL ACL）完全不可用时的兜底。
    // 不依赖任何系统 HTTP 组件，普通用户权限即可绑定，保证网页控制台仍能打开。
    private RawHttpServer? _rawHttp;
    /// <summary>是否运行在无系统组件依赖的纯 TCP 模式（HttpListener 不可用时的备选）。</summary>
    public bool IsRawHttpMode => _rawHttp != null;

    // 共享的被控端(S端)查看端连接
    private TcpClient _srvTcp = null!;
    private NetworkStream _srvStream = null!;
    private CancellationTokenSource _srvCts = null!;
    private readonly object _srvLock = new();
    private readonly SemaphoreSlim _srvWriteLock = new(1, 1);

    // S端 桥接任务（懒启动）：仅当首个浏览器接入时才建立连接，全部断开后自动断开。
    private Task? _srvTask;
    private readonly object _srvStartLock = new();   // 保护 _srvTask / _srvCts 的启停

    // 给后加入的浏览器"立刻有画面/光标"用的缓存（完整 WS 报文：[len][type][payload]）
    private byte[]? _lastFramePkt;
    private byte[]? _lastCursorDataPkt;
    private byte[]? _lastCursorPosPkt;

    // ---- 浏览器客户端 ----
    private readonly List<Client> _clients = new();
    private readonly object _clientsLock = new();
    private int _clientSeq;

    // ---- PPT 放映控制台客户端（/screen、/textws 的 WebSocket） ----
    private readonly List<WebSocket> _pptScreens = new();
    private readonly object _pptLock = new();
    private Task? _pptKeyTask;

    // ---- /show 图片展示（手机遥控大屏）----
    private readonly List<WebSocket> _showClients = new();
    private readonly object _showLock = new();

    // System.Net.WebSockets.WebSocket 不允许同一实例并发 SendAsync（会抛 InvalidOperationException）。
    // 广播路径与首帧/尾帧补发可能撞到同一个 socket，按实例串行化，避免静默丢帧。
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WebSocket, SemaphoreSlim> _wsSendGates = new();
    private static SemaphoreSlim WsSendGate(WebSocket ws) =>
        _wsSendGates.GetValue(ws, _ => new SemaphoreSlim(1, 1));

    private static async Task SendWsAsync(WebSocket ws, ArraySegment<byte> data,
        WebSocketMessageType type, CancellationToken ct, bool fireAndForget = false)
    {
        var gate = WsSendGate(ws);
        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(data, type, true, ct).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
        catch
        {
            if (!fireAndForget) throw;
        }
    }

    // 图片平移时每个 PreviewMouseMove 都会广播一次状态：JSON 序列化 + N 路
    // fire-and-forget SendAsync 会打满线程池。手机端限到 ~30Hz，并用尾帧补发
    // 保证拖动结束时拿到的是最终状态；WPF 本端渲染仍由 StateChanged 即时驱动。
    private readonly object _showThrottleLock = new();
    private long _showLastSentMs;
    private ShowState _showPending;
    private int _showTrailingScheduled;

    // Win7 兼容通道：启动失败原因、自动重试定时器与并发闸门。
    private volatile string _compatWsError = "";
    private System.Threading.Timer _compatWsRetry;
    private bool _compatWsNeeded;
    private int _compatWsStarting;

    // JSON：让中文按原样输出（不转 \uXXXX），仅影响可读性
    private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // 自定义控制消息（仅浏览器 <-> WebServer，不下发给 S端）
    private const byte CtrlPeerCursor = 50;   // 某浏览器虚拟光标位置
    private const byte CtrlPeerList   = 51;   // 欢迎包（含自身 id 与所有同伴）
    private const byte CtrlPeerLeave  = 52;   // 某浏览器离开
    private const byte VirtualCursor  = 60;   // 浏览器仅在悬停时上报的虚拟光标位置
    private const byte CtrlAuth       = 20;   // 浏览器上报访问密码（仅浏览器 <-> WebServer）
    private const byte CtrlAuthOk     = 21;   // 鉴权结果（1=通过 / 0=失败 / 2=需要密码）

    private sealed class Client
    {
        public WebSocket Ws = null!;
        public Channel<byte[]> Chan = null!;
        public CancellationTokenSource Cts = null!;
        public int Id;                 // 自增整数编号（与浏览器端 myId 一致）
        public string Ip = "";         // 操作者 IP（标注在光标旁）
        public int VX = 32768, VY = 32735;   // 归一化虚拟光标位置（0..65535）
        public bool Authed = true;     // 是否已通过访问密码（未设密码则默认 true）
        public bool Removed;           // 拆除标记：防止多个入口重复清理同一连接
    }

    public WebServer(int httpPort, int serverTcpPort, string? password = null)
    {
        _httpPort = httpPort;
        _serverTcpPort = serverTcpPort;
        _password = password ?? "";
        _wwwRoot = ResolveWwwRoot();
        SystemMedia.Log = msg => Log(msg);

    }

    /// <summary>
    /// 解析 web 静态根目录：优先运行目录下的 web/，否则向上目录回退（便于在源码树下直接调试运行）。
    /// </summary>
    private static string ResolveWwwRoot()
    {
        var baseDir = AppContext.BaseDirectory;
        string Try(string dir) => Path.Combine(dir, "web");
        if (File.Exists(Path.Combine(Try(baseDir), "index.html"))) return Try(baseDir);
        var d = new DirectoryInfo(baseDir);
        while (d != null)
        {
            if (File.Exists(Path.Combine(Try(d.FullName), "index.html"))) return Try(d.FullName);
            d = d.Parent;
        }
        return Try(baseDir);
    }

    public void Start()
    {
        // 关键点：URL ACL（http://+:9090/）通常是"首次运行时已持久注册"的，
        // 因此这里【先直接尝试绑定】，不再同步等待 UAC 弹窗——否则 UAC 未被及时放行时
        // 会卡住整个 Web 端启动，表现为"程序开着但 9090 死活连不上"。
        // ACL/防火墙的注册改为"绑定成功后后台异步"执行，绝不阻塞启动。

        // 依次尝试：所有网卡(+/ *) → 仅回环(127.0.0.1)。
        // + 必须排在 * 前面：HTTP.sys 中 * 是弱通配符，不匹配 IP 地址形式的 URL，
        // 用 * 绑定后浏览器访问 http://192.168.x.x:9090/ 会拿到无效响应
        // （ERR_HTTP_RESPONSE_CODE_FAILURE）。URL ACL 也已同步改用 +。
        string[] prefixes = new[]
        {
            $"http://+:{_httpPort}/",
            $"http://*:{_httpPort}/",
            $"http://127.0.0.1:{_httpPort}/",
        };
        // PAINTER_RAWHTTP=1 可强制走纯 TCP 备选模式（便于验证 / 规避有问题的 HTTP.sys）。
        bool forceRawHttp = IsFlagOn(Environment.GetEnvironmentVariable("PAINTER_RAWHTTP"));
        foreach (var pfx in prefixes)
        {
            if (forceRawHttp) break;
            HttpListener l = null;
            try
            {
                l = new HttpListener();
                l.Prefixes.Add(pfx);
                l.Start();
                _listener = l;
                _boundPrefix = pfx;
                break;
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5)
            {
                try { l.Close(); } catch { }
                Log($"[Web] 前缀 {pfx} 权限不足（需管理员 / URL ACL），回退尝试下一个：{ex.Message}");
            }
            catch (Exception ex)
            {
                try { l.Close(); } catch { }
                Log($"[Web] 前缀 {pfx} 启动失败：{ex.Message}");
            }
        }
        if (_listener == null)
        {
            // HttpListener 依赖 HTTP.sys + URL ACL，在部分 Win7 / 受限账户下可能全部绑定失败。
            // 此时降级到纯 TCP Web 服务（零系统组件依赖），保证网页控制台仍然可用。
            Log("[Web] HttpListener 无法绑定，自动降级到纯 TCP Web 服务（不依赖系统 HTTP 组件）");
            StartRawHttpFallback();
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        // 仅 HttpListener 模式需要它的 AcceptLoop；纯 TCP 降级模式由 RawHttpServer 自行接收。
        if (_listener != null) _ = Task.Run(() => AcceptLoopAsync(ct));

        // Win7 的 HTTP.sys 没有 WebSocket（AcceptWebSocketAsync 直接抛 PlatformNotSupportedException），
        // 改为在另一个 TCP 端口上另起兼容通道，RFC6455 由 RawWebSocket 实现。
        // PAINTER_RAWWS=1 可在 Win10/11 上强制走该通道，便于回归测试。
        bool forceRawWs = IsFlagOn(Environment.GetEnvironmentVariable("PAINTER_RAWWS"));
        // 纯 TCP 模式同端口已支持 WS，无需另开兼容通道。
        _compatWsNeeded = _listener != null && (forceRawWs || !OsSupport.SupportsHttpSysWebSocket);

        // 端口被占用等临时故障不应让 Win7 的 Web 远控永久不可用：保留失败原因，
        // 由 /wsconfig 明确告知前端，并每 10 秒自动重试直到成功。
        // 用局部函数而非自引用 lambda：C# 不允许在变量赋值前捕获自己（CS0165）。
        void tryStartCompat()
        {
            if (_rawWs != null || !_compatWsNeeded) return;
            if (System.Threading.Interlocked.Exchange(ref _compatWsStarting, 1) == 1) return;
            try
            {
                bool loopbackOnly = _boundPrefix.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase);
                var raw = new RawWsServer(_httpPort, loopbackOnly, DispatchRawWsAsync);
                raw.Start();
                _rawWs = raw;
                _compatWsError = "";
                var timer = _compatWsRetry;
                _compatWsRetry = null;
                try { timer?.Dispose(); } catch { }
                Log($"[Web] WebSocket 兼容通道已启动：端口 {raw.Port}（{(forceRawWs ? "已按 PAINTER_RAWWS 强制启用" : "当前系统无 HTTP.sys WebSocket")}）");
            }
            catch (Exception ex)
            {
                _compatWsError = "WebSocket 兼容通道启动失败：" + ex.Message;
                Log("[Web] " + _compatWsError + "，10 秒后自动重试");
                if (_compatWsRetry == null)
                {
                    _compatWsRetry = new System.Threading.Timer(_ =>
                    {
                        try { tryStartCompat(); } catch { }
                    }, null, 10000, 10000);
                }
            }
            finally { System.Threading.Interlocked.Exchange(ref _compatWsStarting, 0); }
        }
        if (_compatWsNeeded) tryStartCompat();

        // 图片展示：把状态变更广播给所有手机遥控端，保持多端同步
        ShowController.StateChanged += BroadcastShowState;
        // 注意：到 S端 的共享查看端连接改为【懒启动】——仅当首个浏览器（已鉴权）接入时才建立，
        // 所有浏览器断开后自动断开。这样 S端 真正进入空闲（不抓屏、不空转），
        // 且 WebServer 日志能如实反映"是否有 Web 端真正连上"。
        if (_rawHttp != null)
            Log($"[Web] 纯 TCP 模式已启动：http://0.0.0.0:{_rawHttp.Port}/ （监听全部网卡，不依赖系统 HTTP 组件，HTTP+WS 同端口；桥接被控端 TCP 端口 {_serverTcpPort}）");
        else
            Log($"[Web] HTTP/WS 服务已启动：{_boundPrefix} （桥接被控端 TCP 端口 {_serverTcpPort}，按需懒连接，支持多浏览器并发）");

        // 本机自检：实际用 HTTP 请求自己一次，确认回环可达（排除"绑了但连不上"的防火墙/配置问题）。
        _ = Task.Run(() => SelfTestAsync(_httpPort));
    }

    /// <summary>启动纯 TCP Web 服务兜底：HTTP 与 WebSocket 同端口，零系统组件依赖。</summary>
    private void StartRawHttpFallback()
    {
        _rawHttp = new RawHttpServer(_httpPort, false, HandleRawHttp, DispatchRawWsAsync);
        _rawHttp.Start();
        _boundPrefix = $"http://+:{_rawHttp.Port}/";
    }

    /// <summary>
    /// 纯 TCP 模式的 HTTP 路由：与 HttpListener 模式共用同一套鉴权、静态资源与业务逻辑，
    /// 覆盖网页控制台所需的核心端点。返回 null 视为 404。
    /// </summary>
    private RawHttpServer.Response HandleRawHttp(RawHttpServer.Request req)
    {
        string path = req.Path ?? "/";
        if (path == "/") path = "/index.html";
        else if (path == "/remotedesk" || path == "/remotedesk/") path = "/remotedesk/index.html";
        else if (path == "/share" || path == "/share/") path = "/share/index.html";
        else if (path == "/ppt" || path == "/ppt/") path = "/ppt/index.html";
        else if (path == "/show" || path == "/show/") path = "/show/index.html";
        else if (path == "/file" || path == "/file/") path = "/file/index.html";
        else if (path == "/system" || path == "/system/") path = "/system/index.html";

        // 前端据此决定 WS 端口：纯 TCP 模式同端口，wsPort=0 表示同源。
        if (path == "/wsconfig")
        {
            string json = "{\"httpPort\":" + _httpPort + ",\"wsPort\":0"
                          + ",\"compat\":false,\"needCompat\":false,\"error\":\"\"}";
            return new RawHttpServer.Response { Status = 200, ContentType = "application/json; charset=utf-8",
                Body = Encoding.UTF8.GetBytes(json), NoStore = true };
        }

        if (path == "/control" || path == "/ppt/control") return RawPptControl(req);
        if (path.StartsWith("/api/draw", StringComparison.OrdinalIgnoreCase)) return RawDrawApi(req);
        if (path.StartsWith("/api/system", StringComparison.OrdinalIgnoreCase)) return RawSystemApi(req);
        if (path.StartsWith("/show/api/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/share/api/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/file/api/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/file/dl", StringComparison.OrdinalIgnoreCase))
        {
            return new RawHttpServer.Response { Status = 501,
                Body = Encoding.UTF8.GetBytes("该接口在纯 TCP 备选模式下暂不可用，请改用主服务器模式") };
        }

        // 静态文件
        string rootPrefix = _wwwRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (path.Contains("..")) return new RawHttpServer.Response { Status = 404, Body = Array.Empty<byte>() };
        string filePath = Path.GetFullPath(Path.Combine(_wwwRoot, path.TrimStart('/')));
        if (!filePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
            return new RawHttpServer.Response { Status = 404, Body = Encoding.UTF8.GetBytes("404 Not Found") };
        try
        {
            var data = File.ReadAllBytes(filePath);
            return new RawHttpServer.Response { Status = 200, ContentType = Mime(filePath), Body = data };
        }
        catch (Exception ex)
        {
            Log($"[Web] 纯 TCP 读取文件失败 {filePath}：{ex.Message}");
            return new RawHttpServer.Response { Status = 500, Body = Encoding.UTF8.GetBytes("500") };
        }
    }

    private static RawHttpServer.Response RawText(int code, string text, string mime = "text/plain; charset=utf-8")
        => new RawHttpServer.Response { Status = code, ContentType = mime, Body = Encoding.UTF8.GetBytes(text) };

    private string ReadBodyText(RawHttpServer.Request req)
    {
        try { return Encoding.UTF8.GetString(req.Body ?? Array.Empty<byte>()); } catch { return ""; }
    }

    private RawHttpServer.Response RawPptControl(RawHttpServer.Request req)
    {
        try
        {
            if (!string.IsNullOrEmpty(_password) && req.GetQuery("code") != _password)
                return RawText(401, "未授权：请在访问地址后带上 ?code=<访问密码>");
            if (req.Method == "GET")
            {
                var (current, total, running) = PptController.GetStatus();
                string json = "{\"currentSlide\":" + current + ",\"totalSlides\":" + total
                              + ",\"running\":" + (running ? "true" : "false") + "}";
                return RawText(200, json, "application/json; charset=utf-8");
            }
            if (req.Method == "POST")
            {
                string cmd = "";
                try { using var doc = JsonDocument.Parse(ReadBodyText(req));
                    if (doc.RootElement.TryGetProperty("cmd", out var c)) cmd = c.GetString() ?? ""; } catch { }
                if (string.IsNullOrEmpty(cmd)) return RawText(400, "缺少 cmd");
                bool ok = PptController.Run(cmd);
                return RawText(200, ok ? "OK" : "执行失败（PPT 未打开或不支持该命令）");
            }
            return RawText(405, "不支持的请求方法");
        }
        catch (Exception ex) { Log($"[Web] PPT 控制接口异常：{ex.Message}"); return RawText(500, "服务器内部错误"); }
    }

    private RawHttpServer.Response RawDrawApi(RawHttpServer.Request req)
    {
        try
        {
            if (!CheckCodeAuth(req.GetQuery("code")))
                return RawText(401, "{\"ok\":false,\"error\":\"unauthorized\"}", "application/json; charset=utf-8");
            string cmd = (req.GetQuery("cmd") ?? "").ToLowerInvariant();
            string arg = req.GetQuery("arg") ?? "";
            if (req.Method == "POST")
            {
                try { using var doc = JsonDocument.Parse(ReadBodyText(req));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("cmd", out var c)) cmd = (c.GetString() ?? cmd).ToLowerInvariant();
                    if (root.TryGetProperty("arg", out var a)) arg = a.GetString() ?? arg; } catch { }
            }
            bool isShapeQuery = cmd == "shape" && string.Equals(arg, "query", StringComparison.OrdinalIgnoreCase);
            bool ok = isShapeQuery ? CanvasControl.ShapeStateProvider != null : CanvasControl.Invoke(cmd, arg);
            bool hasShapeState = false, shapeOn = false;
            try { var q = CanvasControl.ShapeStateProvider; if (q != null) { shapeOn = q(); hasShapeState = true; } } catch { }
            string stateJson = hasShapeState ? ",\"shapeOn\":" + (shapeOn ? "true" : "false") : "";
            return RawText(ok ? 200 : 503,
                ok ? "{\"ok\":true" + stateJson + "}" : "{\"ok\":false,\"error\":\"画板未就绪或命令不支持\"}",
                "application/json; charset=utf-8");
        }
        catch (Exception ex) { Log("[Web] 画板控制接口异常：" + ex.Message);
            return RawText(500, "{\"ok\":false,\"error\":\"internal\"}", "application/json; charset=utf-8"); }
    }

    private RawHttpServer.Response RawSystemApi(RawHttpServer.Request req)
    {
        try
        {
            if (!CheckCodeAuth(req.GetQuery("code")))
                return RawText(401, "{\"error\":\"unauthorized\"}", "application/json; charset=utf-8");
            string action = (req.GetQuery("action") ?? "").ToLowerInvariant();
            int v = 0; int.TryParse(req.GetQuery("v") ?? "", out v);
            if (req.Method == "POST")
            {
                try { using var doc = JsonDocument.Parse(ReadBodyText(req));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("action", out var a)) action = (a.GetString() ?? action).ToLowerInvariant();
                    if (root.TryGetProperty("v", out var ve))
                    {
                        if (ve.ValueKind == JsonValueKind.Number) v = ve.GetInt32();
                        else int.TryParse(ve.GetString(), out v);
                    } } catch { }
            }
            switch (action)
            {
                case "pause": SystemMedia.ToggleMediaPlayPause(); break;
                case "volup": SystemMedia.AdjustVolume(5); break;
                case "voldown": SystemMedia.AdjustVolume(-5); break;
                case "vol":
                case "setvol": SystemMedia.SetVolumePercent(v); break;
                case "mute": SystemMedia.ToggleMute(); break;
            }
            var st = SystemMedia.GetState();
            string json = "{\"action\":\"" + action + "\",\"volume\":" + st.volumePercent
                        + ",\"muted\":" + (st.muted ? "true" : "false")
                        + ",\"ok\":" + (st.volumePercent >= 0 ? "true" : "false") + "}";
            return RawText(200, json, "application/json; charset=utf-8");
        }
        catch (Exception ex) { Log($"[Web] 系统控制接口异常：{ex.Message}");
            return RawText(500, "{\"error\":\"internal\"}", "application/json; charset=utf-8"); }
    }

    /// <summary>
    /// 本机自检：用 HttpClient 实际请求一次 http://127.0.0.1:port/ ，把可达性明确打出来。
    /// 回环(127.0.0.1)不受 Windows 防火墙限制，若这里都失败说明是程序/绑定问题；
    /// 若能通但用机器 IP / 局域网访问失败，则是防火墙未放行（需以管理员运行或允许 UAC）。
    /// </summary>
    private async Task SelfTestAsync(int port)
    {
        await Task.Delay(800);
        foreach (var host in new[] { "127.0.0.1", "localhost" })
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4), BaseAddress = new Uri($"http://{host}:{port}/") };
                var resp = await client.GetAsync("");
                var ok = resp.IsSuccessStatusCode;
                var len = ok ? (await resp.Content.ReadAsByteArrayAsync()).Length : 0;
                Log($"[Web] 本机自检 http://{host}:{port}/ => {(ok ? "可访问(" + len + "字节)" : "HTTP " + (int)resp.StatusCode)}");
            }
            catch (Exception ex)
            {
                Log($"[Web] 本机自检 http://{host}:{port}/ => 失败：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 懒启动：首个已鉴权浏览器接入时才建立到 S端 的查看端连接；
    /// 这样 S端 仅在确实有人观看时才抓屏，空闲时彻底停止（不空转），
    /// 且 WebServer 日志能如实反映"是否有 Web 端真正连上"。
    /// </summary>
    private void EnsureServerConnected()
    {
        bool start = false;
        lock (_srvStartLock)
        {
            // 未启动 / 已结束 / 正在退出（已请求取消）——都重新拉起一条连接
            if (_srvTask == null || _srvTask.IsCompleted || (_srvCts != null && _srvCts.IsCancellationRequested))
            {
                // 已结束的旧 CTS 先释放，避免反复重连时 CTS/WaitHandle 持续累积。
                // 仍在跑（仅被请求取消）的交给它自己的 continuation 释放，避免 token 用后即毁。
                if (_srvTask != null && _srvTask.IsCompleted)
                {
                    try { _srvCts?.Dispose(); } catch { }
                }
                else if (_srvCts != null)
                {
                    CancellationTokenSource old = _srvCts;
                    _srvTask?.ContinueWith(_ => { try { old.Dispose(); } catch { } },
                        TaskScheduler.Default);
                }
                _srvCts = new CancellationTokenSource();
                CancellationToken token = _srvCts.Token;
                _srvTask = Task.Run(() => ServerConnectLoopAsync(token));
                start = true;
            }
        }
        if (start) Log("[Web] 有浏览器接入，已拉起被控端抓屏连接（按需）");
    }

    /// <summary>最后一个已鉴权浏览器断开后，断开 S端 连接使其回到空闲（停止抓屏）。</summary>
    private void StopServerIfIdle()
    {
        bool stop = false;
        lock (_srvStartLock)
        {
            if (_srvTask != null && !_srvTask.IsCompleted)
            {
                int n = 0;
                lock (_clientsLock) foreach (var c in _clients) if (c.Authed) n++;
                if (n == 0) { stop = true; _srvCts?.Cancel(); }
            }
        }
        if (stop) Log("[Web] 无浏览器连接，已断开被控端抓屏连接（空闲）");
    }

    // ---------- HTTP / WS 监听 ----------
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                if (ct.IsCancellationRequested) break;
                await Task.Delay(200, ct);
                continue;
            }
            if (ct.IsCancellationRequested) { try { ctx.Response.Abort(); } catch { } break; }

            _ = Task.Run(async () =>
            {
                try
                {
                    string lp = ctx.Request.Url?.LocalPath ?? "/";
                    if (ctx.Request.IsWebSocketRequest)
                    {
                        // 改用 await 而非 .Wait：连接存活期间不再阻塞一个线程池线程，
                        // 由 IOCP 驱动，避免大量长连时耗尽线程池导致服务端失去响应。
                        if (lp == "/ws") await HandleWsAsync(ctx, ct).ConfigureAwait(false);
                        else if (lp == "/screen" || lp == "/textws") await HandlePptWsAsync(ctx, ct).ConfigureAwait(false);
                        else if (lp == "/show/ws" || lp == "/share/ws") await HandleShowWsAsync(ctx, ct).ConfigureAwait(false);
                        else OnGet(ctx);
                    }
                    else if (lp == "/control" || lp == "/ppt/control")
                    {
                        OnPptControl(ctx);
                    }
                    else if (lp.StartsWith("/show/api/", StringComparison.OrdinalIgnoreCase)
                          || lp.StartsWith("/share/api/", StringComparison.OrdinalIgnoreCase))
                    {
                        OnShowApi(ctx);
                    }
                    else if (lp.StartsWith("/file/api/", StringComparison.OrdinalIgnoreCase))
                    {
                        OnFileApi(ctx);
                    }
                    else if (lp.StartsWith("/file/dl", StringComparison.OrdinalIgnoreCase))
                    {
                        OnFileDownload(ctx);
                    }
                    else if (lp == "/wsconfig")
                    {
                    	OnWsConfig(ctx);
                    }
                    else if (lp.StartsWith("/api/system", StringComparison.OrdinalIgnoreCase))
                    {
                    	OnSystemApi(ctx);
                    }
                    else if (lp.StartsWith("/api/draw", StringComparison.OrdinalIgnoreCase))
                    {
                    	OnDrawApi(ctx);
                    }
                    else OnGet(ctx);
                }
                catch { try { ctx.Response.Abort(); } catch { } }
            });
        }
    }

    // ---------- 静态文件 ----------
    private void OnGet(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        string path = Uri.UnescapeDataString(req.Url?.AbsolutePath ?? "/");
        if (path == "/") path = "/index.html";                       // 根目录 = Web 功能导航页
        else if (path == "/remotedesk" || path == "/remotedesk/") path = "/remotedesk/index.html";
        else if (path == "/share" || path == "/share/") path = "/share/index.html";
        else if (path == "/share/ws" || path == "/share/ws/") path = "/share/index.html";
        else if (path == "/ppt" || path == "/ppt/") path = "/ppt/index.html";
        else if (path == "/show" || path == "/show/") path = "/show/index.html";
        else if (path == "/file" || path == "/file/") path = "/file/index.html";
        else if (path == "/system" || path == "/system/") path = "/system/index.html";
        // 二次解码（%2f / %2e）可能拼出 ../；且 _wwwRoot 不带尾部分隔符时，
        // "C:\...\web" 会把同级 "C:\...\web.bak" 也判成合法前缀。规范成带分隔符再比较。
        string rootPrefix = _wwwRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (path.Contains("..")) { res.StatusCode = 404; res.Close(); return; }
        string filePath = Path.GetFullPath(Path.Combine(_wwwRoot, path.TrimStart('/')));
        if (!filePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(filePath))
        {
            res.StatusCode = 404;
            res.Close();
            return;
        }
        try
        {
            var data = File.ReadAllBytes(filePath);
            res.ContentType = Mime(filePath);
            res.ContentLength64 = data.Length;
            res.OutputStream.Write(data, 0, data.Length);
        }
        catch (Exception ex) { Log($"[Web] 读取文件失败 {filePath}：{ex.Message}"); res.StatusCode = 500; }
        finally { try { res.Close(); } catch { } }
    }

    private static string Mime(string p)
    {
        return Path.GetExtension(p).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".ico" => "image/x-icon",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            ".json" => "application/json; charset=utf-8",
            _ => "application/octet-stream",
        };
    }

    private static bool IsFlagOn(string? value)
    {
        return !string.IsNullOrEmpty(value)
            && (value == "1"
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>前端据此决定 WebSocket 连哪个端口：兼容通道启用时 wsPort 为实际端口，否则 0（与页面同源）。</summary>
    private void OnWsConfig(HttpListenerContext ctx)
    {
        int wsPort = _rawWs?.Port ?? 0;
        string err = (_compatWsError ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", " ").Replace("\n", " ");
        string json = "{\"httpPort\":" + _httpPort + ",\"wsPort\":" + wsPort
                      + ",\"compat\":" + (wsPort > 0 ? "true" : "false")
                      + ",\"needCompat\":" + (_compatWsNeeded ? "true" : "false")
                      + ",\"error\":\"" + err + "\"}";
        try { ctx.Response.AddHeader("Cache-Control", "no-store"); } catch { }
        WriteText(ctx.Response, 200, json, "application/json; charset=utf-8");
    }

    /// <summary>把兼容通道上的握手请求分发到与 HTTP.sys 路径相同的处理逻辑（鉴权方式保持一致）。</summary>
    private async Task DispatchRawWsAsync(RawWsRequest req, RawWebSocket ws, CancellationToken ct)
    {
        switch (req.Path)
        {
            case "/ws":
                await HandleWsCoreAsync(ws, req.RemoteIp, ct).ConfigureAwait(false);
                break;
            case "/screen":
            case "/textws":
                if (!CheckCodeAuth(req.GetQuery("code")))
                {
                    await CloseWsAsync(ws, WebSocketCloseStatus.PolicyViolation, "unauthorized", ct).ConfigureAwait(false);
                    return;
                }
                await HandlePptWsCoreAsync(ws, ct).ConfigureAwait(false);
                break;
            case "/show/ws":
            case "/share/ws":
                if (!CheckCodeAuth(req.GetQuery("code")))
                {
                    await CloseWsAsync(ws, WebSocketCloseStatus.PolicyViolation, "unauthorized", ct).ConfigureAwait(false);
                    return;
                }
                await HandleShowWsCoreAsync(ws, ct).ConfigureAwait(false);
                break;
            default:
                await CloseWsAsync(ws, WebSocketCloseStatus.PolicyViolation, "unknown path", ct).ConfigureAwait(false);
                break;
        }
    }

    private static async Task CloseWsAsync(WebSocket ws, WebSocketCloseStatus status, string reason, CancellationToken ct)
    {
        try { await ws.CloseAsync(status, reason, ct).ConfigureAwait(false); } catch { }
    }

    // ---------- PPT 放映控制台：/ppt 页面 + /control 接口 + /screen 画面 ----------

    /// <summary>GET /control?cmd=status 返回放映状态；POST /control {"cmd":"..."} 执行放映命令。</summary>
    private void OnPptControl(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            bool needAuth = !string.IsNullOrEmpty(_password);
            if (needAuth)
            {
                string? code = req.QueryString["code"];
                if (string.IsNullOrEmpty(code) || code != _password)
                {
                    WriteText(res, 401, "未授权：请在访问地址后带上 ?code=<访问密码>");
                    return;
                }
            }

            if (req.HttpMethod == "GET")
            {
                var (current, total, running) = PptController.GetStatus();
                string json = "{\"currentSlide\":" + current + ",\"totalSlides\":" + total
                              + ",\"running\":" + (running ? "true" : "false") + "}";
                WriteText(res, 200, json, "application/json; charset=utf-8");
                return;
            }

            if (req.HttpMethod == "POST")
            {
                string body;
                using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
                string cmd = "";
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("cmd", out var c)) cmd = c.GetString() ?? "";
                }
                catch { }
                if (string.IsNullOrEmpty(cmd)) { WriteText(res, 400, "缺少 cmd"); return; }
                bool ok = PptController.Run(cmd);
                WriteText(res, 200, ok ? "OK" : "执行失败（PPT 未打开或不支持该命令）");
                return;
            }

            WriteText(res, 405, "不支持的请求方法");
        }
        catch (Exception ex)
        {
            Log($"[Web] PPT 控制接口异常：{ex.Message}");
            try { WriteText(res, 500, "服务器内部错误"); } catch { }
        }
    }

    // ---------- /api/draw：Web 端画板工具（颜色 / 橡皮 / 清空 / 笔鼠切换 / 白板） ----------
    private void OnDrawApi(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            if (!CheckCodeAuth(req.QueryString["code"]))
            {
                WriteText(res, 401, "{\"ok\":false,\"error\":\"unauthorized\"}", "application/json; charset=utf-8");
                return;
            }
            string cmd = (req.QueryString["cmd"] ?? "").ToLowerInvariant();
            string arg = req.QueryString["arg"] ?? "";
            if (req.HttpMethod == "POST")
            {
                using (var sr = new StreamReader(req.InputStream, Encoding.UTF8))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(sr.ReadToEnd());
                        if (doc.RootElement.TryGetProperty("cmd", out var c)) cmd = (c.GetString() ?? cmd).ToLowerInvariant();
                        if (doc.RootElement.TryGetProperty("arg", out var a)) arg = a.GetString() ?? arg;
                    }
                    catch { }
                }
            }
            // 墨迹识别是开关：Web 端要显示当前开/关，响应里带回状态。
            // cmd=shape&arg=query 只查询，不翻转开关。
            bool isShapeQuery = cmd == "shape"
                && string.Equals(arg, "query", StringComparison.OrdinalIgnoreCase);
            bool ok = isShapeQuery
                ? CanvasControl.ShapeStateProvider != null
                : CanvasControl.Invoke(cmd, arg);
            bool hasShapeState = false, shapeOn = false;
            try
            {
                var query = CanvasControl.ShapeStateProvider;
                if (query != null) { shapeOn = query(); hasShapeState = true; }
            }
            catch { }
            string stateJson = hasShapeState
                ? ",\"shapeOn\":" + (shapeOn ? "true" : "false")
                : "";
            WriteText(res, ok ? 200 : 503,
                ok ? "{\"ok\":true" + stateJson + "}"
                   : "{\"ok\":false,\"error\":\"画板未就绪或命令不支持\"}",
                "application/json; charset=utf-8");
        }
        catch (Exception ex)
        {
            Log("[Web] 画板控制接口异常：" + ex.Message);
            try { WriteText(res, 500, "{\"ok\":false,\"error\":\"internal\"}", "application/json; charset=utf-8"); } catch { }
        }
    }

    // ---------- /api/system 系统媒体与音量控制 ----------
    private void OnSystemApi(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            if (!CheckCodeAuth(req.QueryString["code"]))
            {
                WriteText(res, 401, "{\"error\":\"unauthorized\"}", "application/json; charset=utf-8");
                return;
            }

            string action = (req.QueryString["action"] ?? "").ToLowerInvariant();
            int v = 0;
            int.TryParse(req.QueryString["v"] ?? "", out v);

            if (req.HttpMethod == "POST")
            {
                using (var sr = new StreamReader(req.InputStream, Encoding.UTF8))
                {
                    var body = sr.ReadToEnd();
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("action", out var a)) action = (a.GetString() ?? action).ToLowerInvariant();
                        if (root.TryGetProperty("v", out var ve))
                        {
                            if (ve.ValueKind == JsonValueKind.Number) v = ve.GetInt32();
                            else int.TryParse(ve.GetString(), out v);
                        }
                    }
                    catch { }
                }
            }

            switch (action)
            {
                case "pause":
                    SystemMedia.ToggleMediaPlayPause();
                    break;
                case "volup":
                    SystemMedia.AdjustVolume(5);
                    break;
                case "voldown":
                    SystemMedia.AdjustVolume(-5);
                    break;
                case "vol":
                case "setvol":
                    SystemMedia.SetVolumePercent(v);
                    break;
                case "mute":
                    SystemMedia.ToggleMute();
                    break;
                case "get":
                default:
                    break;
            }

            var st = SystemMedia.GetState();
            string json = "{\"action\":\"" + action + "\",\"volume\":" + st.volumePercent
                        + ",\"muted\":" + (st.muted ? "true" : "false")
                        + ",\"ok\":" + (st.volumePercent >= 0 ? "true" : "false") + "}";
            WriteText(res, 200, json, "application/json; charset=utf-8");
        }
        catch (Exception ex)
        {
            Log($"[Web] 系统控制接口异常：{ex.Message}");
            try { WriteText(res, 500, "{\"error\":\"internal\"}", "application/json; charset=utf-8"); } catch { }
        }
    }

    // ---------- /show 图片展示：手机遥控大屏 ----------

    /// <summary>WebSocket：手机端与控制端双向同步。文本帧为 JSON 控制指令；状态变更由 ShowController 广播。</summary>
    private async Task HandleShowWsAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        WebSocket ws;
        try
        {
            var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
            ws = wsCtx.WebSocket;
        }
        catch (Exception ex)
        {
            Log($"[Show] WS 握手失败：{ex.Message}");
            return;
        }

        if (!CheckShowAuth(ctx))
        {
            await CloseWsAsync(ws, WebSocketCloseStatus.PolicyViolation, "unauthorized", ct);
            return;
        }

        await HandleShowWsCoreAsync(ws, ct);
    }

    /// <summary>展示端 WebSocket 已完成握手与鉴权后的处理（HTTP.sys 与 Win7 兼容通道共用）。</summary>
    private async Task HandleShowWsCoreAsync(WebSocket ws, CancellationToken ct)
    {
        lock (_showLock) _showClients.Add(ws);
        Log($"[Show] 手机遥控已连接（当前 {_showClients.Count} 个）");
        await SendWsAsync(ws, new ArraySegment<byte>(Encoding.UTF8.GetBytes(SerializeShowState(ShowController.GetState()))), WebSocketMessageType.Text, ct);

        var buf = new byte[8192];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (r.Count == 0) continue;
                string? text;
                try { text = Encoding.UTF8.GetString(buf, 0, r.Count); }
                catch { continue; }
                HandleShowMessage(text);
            }
        }
        catch { }
        finally
        {
            lock (_showLock) _showClients.Remove(ws);
            try { ws.Dispose(); } catch { }
            Log($"[Show] 手机遥控已断开（当前 {_showClients.Count} 个）");
        }
    }

    /// <summary>校验访问密码（与 PPT 接口一致）：未设密码则免鉴权，否则必须 ?code=。</summary>
    private bool CheckShowAuth(HttpListenerContext ctx)
    {
        return CheckCodeAuth(ctx.Request.QueryString["code"]);
    }

    private bool CheckCodeAuth(string? code)
    {
        if (string.IsNullOrEmpty(_password)) return true;
        return !string.IsNullOrEmpty(code) && code == _password;
    }

    // 手机端 / Web 端可能手改请求或发来类型不符的字段。裸 GetInt32()/GetDouble()
    // 在字段是字符串/null/对象时会抛 InvalidOperationException，中断整条指令；
    // 这里对类型做校验并回退默认值，保证一条坏字段不会拖垮其它控制。
    private static int JsonInt(JsonElement root, string name, int fallback = 0)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var e)
           && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) ? v : fallback;

    private static double JsonDouble(JsonElement root, string name, double fallback = 0)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var e)
           && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var v) ? v : fallback;

    /// <summary>解析并执行手机端指令（轮播 / 缩放 / 平移 / 退出 / 开启）。</summary>
    private void HandleShowMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("action", out var a)) return;
            string action = a.GetString() ?? "";
            switch (action)
            {
                case "open": ShowController.Open(); break;
                case "exit": ShowController.Close(); break;
                case "next": ShowController.Next(); break;
                case "prev": ShowController.Prev(); break;
                case "goto":
                    if (root.TryGetProperty("index", out var idx) && idx.ValueKind == JsonValueKind.Number && idx.TryGetInt32(out var gotoIdx))
                        ShowController.Goto(gotoIdx);
                    break;
                case "zoom":
                {
                    double factor = JsonDouble(root, "factor", 1.2);
                    double? cx = root.TryGetProperty("cx", out var cxv) && cxv.ValueKind == JsonValueKind.Number && cxv.TryGetDouble(out var cxn) ? cxn : (double?)null;
                    double? cy = root.TryGetProperty("cy", out var cyv) && cyv.ValueKind == JsonValueKind.Number && cyv.TryGetDouble(out var cyn) ? cyn : (double?)null;
                    ShowController.Zoom(factor, cx, cy);
                    break;
                }
                case "pan":
                {
                    ShowController.Pan(JsonInt(root, "dx"), JsonInt(root, "dy"));
                    break;
                }
                case "reset": ShowController.ResetView(); break;
                case "delete":
                {
                    // /show 页面同时服务白板与 PPT 放映两种模式：PPT 模式下图片在 _pptImages，
                    // 只调用 RemoveAt 会误删一张无关白板图片（两套列表序号互不相干）。
                    int delIdx = JsonInt(root, "index", -1);
                    if (delIdx >= 0) {
                        if (ShowController.IsPptMode) ShowController.RemovePpt(delIdx);
                        else ShowController.RemoveAt(delIdx);
                    }
                    break;
                }
                case "rotate":
                {
                    ShowController.Rotate(JsonDouble(root, "delta", 15));
                    break;
                }
                // PPT 放映模式：网页端随时控制每张图片的显示 / 隐藏时机
                case "visible":
                {
                    int vi = JsonInt(root, "index", -1);
                    if (vi < 0) break;
                    bool on = true;
                    if (root.TryGetProperty("value", out var vv)) {
                        if (vv.ValueKind == JsonValueKind.False) on = false;
                        else if (vv.ValueKind == JsonValueKind.True) on = true;
                        else if (vv.ValueKind == JsonValueKind.Number && vv.TryGetInt32(out var num)) on = num != 0;
                        else if (vv.ValueKind == JsonValueKind.String) on = vv.GetString() != "0" && vv.GetString() != "false";
                    }
                    ShowController.SetPptVisible(vi, on);
                    break;
                }
                case "showall": ShowController.SetPptAllVisible(true); break;
                case "hideall": ShowController.SetPptAllVisible(false); break;
                case "pptremove":
                {
                    int pri = JsonInt(root, "index", -1);
                    if (pri >= 0) ShowController.RemovePpt(pri);
                    break;
                }
                case "pptclear": ShowController.ClearPpt(); break;
            }
        }
        catch (Exception ex)
        {
            Log($"[Show] WS 指令解析异常：{ex.Message}");
        }
    }

    /// <summary>POST /show/api/upload：接收手机端批量上传的图片（base64 JSON），追加到展示列表。</summary>
    private void OnShowApi(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            string path = req.Url?.AbsolutePath ?? "";
            if (!CheckShowAuth(ctx))
            {
                WriteText(res, 401, "{\"error\":\"unauthorized\"}", "application/json; charset=utf-8");
                return;
            }
            if (path.EndsWith("/upload", StringComparison.OrdinalIgnoreCase) && req.HttpMethod == "POST")
            {
                string body;
                using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
                var imgs = new List<(string name, byte[] data)>();
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("images", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var it in arr.EnumerateArray())
                        {
                            string name = "";
                            string data = "";
                            if (it.TryGetProperty("name", out var n)) name = n.GetString() ?? "";
                            if (it.TryGetProperty("data", out var d)) data = d.GetString() ?? "";
                            if (data.Length > 0)
                            {
                                try { imgs.Add((name, Convert.FromBase64String(data))); }
                                catch (Exception ex) { Log($"[Show] 图片 base64 解码失败：{ex.Message}"); }
                            }
                        }
                    }
                }
                catch (Exception ex) { Log($"[Show] 上传 JSON 解析失败：{ex.Message}"); }
                int added = ShowController.AddImages(imgs);
                bool okAll = (imgs.Count == 0) || (added == imgs.Count);
                WriteText(res, 200, "{\"ok\":" + (okAll ? "true" : "false") + ",\"added\":" + added + ",\"count\":" + ShowController.Count + ",\"pptMode\":" + (ShowController.IsPptMode ? "true" : "false") + ",\"pptSlide\":" + ShowController.PptSlide + "}", "application/json; charset=utf-8");
                return;
            }

            // 网页端首屏 / WS 不可用时的兜底：拉取完整状态
            if (path.EndsWith("/state", StringComparison.OrdinalIgnoreCase)) {
                WriteText(res, 200, SerializeShowState(ShowController.GetState()), "application/json; charset=utf-8");
                return;
            }

            // HTTP 兜底：控制 PPT 模式下每张图片的显示 / 隐藏
            if (path.EndsWith("/visible", StringComparison.OrdinalIgnoreCase) && req.HttpMethod == "POST") {
                string body;
                using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
                int idx = -1; bool on = true;
                try {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("index", out var iv)) idx = iv.GetInt32();
                    if (doc.RootElement.TryGetProperty("value", out var ov)) {
                        if (ov.ValueKind == JsonValueKind.False) on = false;
                        else if (ov.ValueKind == JsonValueKind.True) on = true;
                        else if (ov.ValueKind == JsonValueKind.Number && ov.TryGetInt32(out var n)) on = n != 0;
                    }
                } catch { }
                if (idx >= 0) ShowController.SetPptVisible(idx, on);
                WriteText(res, 200, SerializeShowState(ShowController.GetState()), "application/json; charset=utf-8");
                return;
            }
            WriteText(res, 404, "{\"error\":\"not found\"}", "application/json; charset=utf-8");
        }
        catch (Exception ex)
        {
            Log($"[Show] /show/api 异常：{ex.Message}");
            try { WriteText(res, 500, "{\"error\":\"server error\"}", "application/json; charset=utf-8"); } catch { }
        }
    }

    private static string SerializeShowState(ShowState s) => JsonSerializer.Serialize(s, JsonOpts);

    /// <summary>把展示状态广播给所有手机遥控端（由 ShowController.StateChanged 触发）。</summary>
    private void BroadcastShowState(ShowState s)
    {
        long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        lock (_showThrottleLock)
        {
            if (now - _showLastSentMs < 33)
            {
                _showPending = s;
                if (System.Threading.Interlocked.Exchange(ref _showTrailingScheduled, 1) == 0)
                {
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            await System.Threading.Tasks.Task.Delay(33).ConfigureAwait(false);
                            ShowState pending;
                            lock (_showThrottleLock)
                            {
                                pending = _showPending;
                                _showPending = null;
                                _showLastSentMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                            }
                            if (pending != null) SendShowState(pending);
                        }
                        catch { }
                        finally { System.Threading.Interlocked.Exchange(ref _showTrailingScheduled, 0); }
                    });
                }
                return;
            }
            _showLastSentMs = now;
        }
        SendShowState(s);
    }

    private void SendShowState(ShowState s)
    {
        List<WebSocket> snapshot;
        lock (_showLock)
        {
            if (_showClients.Count == 0) return;
            snapshot = new List<WebSocket>(_showClients);
        }
        var data = new ArraySegment<byte>(Encoding.UTF8.GetBytes(SerializeShowState(s)));
        foreach (var c in snapshot)
        {
            if (c.State != WebSocketState.Open) continue;
            _ = SendWsAsync(c, data, WebSocketMessageType.Text, CancellationToken.None, fireAndForget: true);
        }
    }

    private static void WriteText(HttpListenerResponse res, int code, string text, string mime = "text/plain; charset=utf-8")
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        res.StatusCode = code;
        res.ContentType = mime;
        res.ContentLength64 = data.Length;
        res.OutputStream.Write(data, 0, data.Length);
        try { res.Close(); } catch { }
    }

    // ---------- 局域网文件传输：/file 网页端文件管理器 ----------
    private class FileItemDto
    {
        public string name { get; set; }
        public bool isDir { get; set; }
        public long size { get; set; }
        public string mtime { get; set; }
    }

    private static FileItemDto MakeItem(string path, bool isDir)
    {
        var fi = new FileInfo(path);
        return new FileItemDto { name = fi.Name, isDir = isDir, size = isDir ? 0 : fi.Length, mtime = fi.LastWriteTimeUtc.ToString("o") };
    }

    /// <summary>/file/api/list|upload|mkdir|delete：列目录 / 上传 / 新建文件夹 / 删除。均复用 CheckShowAuth 鉴权。</summary>
    private void OnFileApi(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            if (!CheckShowAuth(ctx))
            {
                WriteText(res, 401, "{\"error\":\"unauthorized\"}", "application/json; charset=utf-8");
                return;
            }
            string action = req.Url.AbsolutePath.Substring("/file/api/".Length).Trim('/').ToLowerInvariant();
            // 写操作必须 POST：否则任意网页用 <img src="...delete?..."> 就能 GET 删除共享文件（CSRF）
            if (action != "list" && !string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                WriteText(res, 405, "{\"error\":\"method not allowed\"}", "application/json; charset=utf-8");
                return;
            }
            var q = req.QueryString;
            string rel = q["path"] ?? "";
            string name = q["name"] ?? "";
            if (action == "list")
            {
                string full = ShareFolder.Resolve(rel);
                var items = new List<FileItemDto>();
                if (Directory.Exists(full))
                {
                    foreach (var d in Directory.GetDirectories(full)) items.Add(MakeItem(d, true));
                    foreach (var f in Directory.GetFiles(full)) items.Add(MakeItem(f, false));
                }
                items.Sort((a, b) => a.isDir == b.isDir
                    ? string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase)
                    : (a.isDir ? -1 : 1));
                WriteText(res, 200, JsonSerializer.Serialize(new { path = rel, items }, JsonOpts), "application/json; charset=utf-8");
            }
            else if (action == "mkdir" || action == "upload" || action == "delete")
            {
                // 允许 name 携带 / 以支持拖拽文件夹的嵌套上传；越界路径由 ShareFolder.Resolve 拦截
                if (string.IsNullOrWhiteSpace(name))
                {
                    WriteText(res, 400, "{\"error\":\"name required\"}", "application/json; charset=utf-8");
                    return;
                }
                string target = ShareFolder.Resolve(rel + "/" + name);
                if (action == "mkdir")
                {
                    Directory.CreateDirectory(target);
                    WriteText(res, 200, "{\"ok\":true}", "application/json; charset=utf-8");
                }
                else if (action == "delete")
                {
                    if (Directory.Exists(target)) Directory.Delete(target, true);
                    else if (File.Exists(target)) File.Delete(target);
                    WriteText(res, 200, "{\"ok\":true}", "application/json; charset=utf-8");
                }
                else // upload：请求体即文件原始字节
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var fs = File.Create(target))
                        req.InputStream.CopyTo(fs);
                    WriteText(res, 200, JsonSerializer.Serialize(new { ok = true, size = new FileInfo(target).Length }, JsonOpts), "application/json; charset=utf-8");
                }
            }
            else
            {
                WriteText(res, 404, "{\"error\":\"not found\"}", "application/json; charset=utf-8");
            }
        }
        catch (Exception ex)
        {
            Log($"[File] API 异常：{ex.Message}");
            try { WriteText(res, 500, "{\"error\":\"server error\"}", "application/json; charset=utf-8"); } catch { }
        }
    }

    /// <summary>/file/dl?path=...&inline=1：下载文件；inline=1 时以内联方式返回（供网页预览）。</summary>
    private void OnFileDownload(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            if (!CheckShowAuth(ctx))
            {
                WriteText(res, 401, "{\"error\":\"unauthorized\"}", "application/json; charset=utf-8");
                return;
            }
            string rel = req.QueryString["path"] ?? "";
            string fp = ShareFolder.Resolve(rel);
            if (!File.Exists(fp)) { res.StatusCode = 404; res.Close(); return; }
            bool inline = (req.QueryString["inline"] ?? "") == "1";
            var data = File.ReadAllBytes(fp);
            res.ContentType = Mime(fp);
            string disp = inline ? "inline" : "attachment";
            try { res.AddHeader("Content-Disposition", $"{disp}; filename=\"{Uri.EscapeDataString(Path.GetFileName(fp))}\""); } catch { }
            res.ContentLength64 = data.Length;
            res.OutputStream.Write(data, 0, data.Length);
        }
        catch (Exception ex)
        {
            Log($"[File] 下载异常：{ex.Message}");
            try { res.StatusCode = 500; } catch { }
        }
        finally { try { res.Close(); } catch { } }
    }

    /// <summary>PPT 控制台的 WebSocket：下发整帧 JPEG 供预览，接收 click / textInput。</summary>
    private async Task HandlePptWsAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        WebSocket ws;
        try
        {
            var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
            ws = wsCtx.WebSocket;
        }
        catch (Exception ex)
        {
            Log($"[Web] PPT WS 握手失败：{ex.Message}");
            return;
        }

        if (!CheckShowAuth(ctx))
        {
            await CloseWsAsync(ws, WebSocketCloseStatus.PolicyViolation, "unauthorized", ct);
            return;
        }

        await HandlePptWsCoreAsync(ws, ct);
    }

    /// <summary>PPT 控制台 WebSocket 已完成握手与鉴权后的处理（HTTP.sys 与 Win7 兼容通道共用）。</summary>
    private async Task HandlePptWsCoreAsync(WebSocket ws, CancellationToken ct)
    {
        lock (_pptLock) _pptScreens.Add(ws);
        EnsureServerConnected();     // 预览要画面：按需拉起 S端 抓屏
        EnsurePptKeyLoop();          // 定期向 S端 要整帧，保证预览持续刷新
        Log($"[Web] PPT 控制台已连接（当前 {_pptScreens.Count} 个）");

        try
        {
            var (left, top, w, h) = VirtualScreenBounds();
            string info = "{\"action\":\"screenInfo\",\"width\":" + w + ",\"height\":" + h
                          + ",\"offsetX\":" + left + ",\"offsetY\":" + top
                          + ",\"captureWidth\":" + w + ",\"captureHeight\":" + h + "}";
            await SendWsAsync(ws, new ArraySegment<byte>(Encoding.UTF8.GetBytes(info)), WebSocketMessageType.Text, ct);
        }
        catch { }

        var buf = new byte[8192];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (r.Count == 0) continue;
                HandlePptWsMessage(Encoding.UTF8.GetString(buf, 0, r.Count));
            }
        }
        catch { }
        finally
        {
            lock (_pptLock) _pptScreens.Remove(ws);
            try { ws.Dispose(); } catch { }
            Log($"[Web] PPT 控制台已断开（当前 {_pptScreens.Count} 个）");
        }
    }

    private void HandlePptWsMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("action", out var a)) return;
            string action = a.GetString() ?? "";

            if (action == "click")
            {
                int x = root.TryGetProperty("x", out var px) ? px.GetInt32() : 0;
                int y = root.TryGetProperty("y", out var py) ? py.GetInt32() : 0;
                var (left, top, w, h) = VirtualScreenBounds();
                ushort nx = (ushort)NetFx.Clamp((long)(x - left) * 65535L / Math.Max(1, w - 1), 0, 65535);
                ushort ny = (ushort)NetFx.Clamp((long)(y - top) * 65535L / Math.Max(1, h - 1), 0, 65535);
                InputInjector.MoveAbsolute(nx, ny);
                InputInjector.ButtonDown(1);      // 1 = 鼠标左键
                InputInjector.ButtonUp(1);
            }
            else if (action == "textInput")
            {
                if (root.TryGetProperty("text", out var t))
                {
                    string s = t.GetString() ?? "";
                    if (s.Length > 0)
                    {
                        try { System.Windows.Forms.SendKeys.SendWait(s); }
                        catch (Exception ex) { Log($"[Web] PPT 文本输入失败：{ex.Message}"); }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[Web] PPT WS 消息处理异常：{ex.Message}");
        }
    }

    /// <summary>把 S端 的整帧 JPEG 直接下发给 PPT 控制台（页面按整图显示，故只转发整帧）。</summary>
    private void TryForwardPptFrame(byte[] payload)
    {
        List<WebSocket> snapshot;
        lock (_pptLock)
        {
            if (_pptScreens.Count == 0) return;
            snapshot = new List<WebSocket>(_pptScreens);
        }
        if (payload.Length < 14) return;
        if ((payload[0] & 1) != 0) return;   // 区域帧：内含多个分块 JPEG，不能当整图用
        if (payload[1] != 0) return;         // 非 JPEG（H.264 / Deflate 原始）
        int len = Protocol.ReadInt32(payload, 10);
        if (len <= 0 || 14 + len > payload.Length) return;
        var jpeg = new byte[len];
        Buffer.BlockCopy(payload, 14, jpeg, 0, len);
        foreach (var ws in snapshot)
        {
            if (ws.State != WebSocketState.Open) continue;
            _ = SendWsAsync(ws, new ArraySegment<byte>(jpeg), WebSocketMessageType.Binary, CancellationToken.None, fireAndForget: true);
        }
    }

    /// <summary>有 PPT 控制台在线时，定期向 S端 索取整帧，保证预览持续更新。</summary>
    private void EnsurePptKeyLoop()
    {
        if (_pptKeyTask != null && !_pptKeyTask.IsCompleted) return;
        _pptKeyTask = Task.Run(async delegate
        {
            while (true)
            {
                await Task.Delay(700);
                int n;
                lock (_pptLock) n = _pptScreens.Count;
                if (n == 0) return;
                try { await SendToServerAsync(Protocol.MessageType.ForceKeyFrame, Array.Empty<byte>(), CancellationToken.None); }
                catch { }
            }
        });
    }

    private static (int left, int top, int width, int height) VirtualScreenBounds()
    {
        try
        {
            var r = System.Windows.Forms.SystemInformation.VirtualScreen;
            return (r.Left, r.Top, r.Width, r.Height);
        }
        catch
        {
            return (0, 0, 1920, 1080);
        }
    }

    // ---------- WebSocket：每个浏览器一个 Client ----------
    private async Task HandleWsAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        WebSocket ws;
        try
        {
            var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
            ws = wsCtx.WebSocket;
        }
        catch (Exception ex)
        {
            Log($"[Web] WS 握手失败：{ex.Message}");
            return;
        }

        var ip = ctx.Request.RemoteEndPoint?.Address.ToString() ?? "?";
        await HandleWsCoreAsync(ws, ip, ct);
    }

    /// <summary>浏览器 WebSocket 已完成握手后的处理（鉴权在帧内进行；HTTP.sys 与 Win7 兼容通道共用）。</summary>
    private async Task HandleWsCoreAsync(WebSocket ws, string ip, CancellationToken ct)
    {
        bool needAuth = !string.IsNullOrEmpty(_password);
        var client = new Client
        {
            Ws = ws,
            Chan = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            }),
            Cts = new CancellationTokenSource(),
            Id = Interlocked.Increment(ref _clientSeq),
            Ip = ip,
            Authed = !needAuth,   // 未设密码则免鉴权
        };

        if (client.Authed)
        {
            // 免鉴权：先放行，再把缓存画面 / 光标 + 同伴列表发给新客户端
            try { await SendWsAsync(ws, new ArraySegment<byte>(EncodeCtrl(CtrlAuthOk, new byte[] { 1 })), WebSocketMessageType.Binary, client.Cts.Token); }
            catch { }
            try { await SendInitialToClient(client); }
            catch (Exception ex) { Log($"[Web] 向新客户端发送初始帧失败：{ex.Message}"); RemoveClient(client); return; }
        }
        else
        {
            // 需要密码：先发"需鉴权"提示，等浏览器上报密码后再放画面
            try { await SendWsAsync(ws, new ArraySegment<byte>(EncodeCtrl(CtrlAuthOk, new byte[] { 2 })), WebSocketMessageType.Binary, client.Cts.Token); }
            catch { RemoveClient(client); return; }
        }

        lock (_clientsLock) _clients.Add(client);
        if (client.Authed) { BroadcastPeer(client); EnsureServerConnected(); }   // 已上线：通知同伴并懒拉起 S端 抓屏
        Log($"[Web] 浏览器已连接（{client.Ip} / #{client.Id}，当前 {_clients.Count} 个）{(needAuth ? "，等待鉴权" : "")}");
        _ = Task.Run(() => ClientSendLoopAsync(client));
        await PumpWsToSrvAsync(client, ct);
    }

    /// <summary>把 S端 推来的报文扇出给所有浏览器。</summary>
    private void Broadcast(byte[] packet)
    {
        List<Client> snapshot;
        lock (_clientsLock) snapshot = new List<Client>(_clients);
        foreach (var c in snapshot)
            if (c.Authed) c.Chan.Writer.TryWrite(packet); // 未鉴权客户端先不接收画面
    }

    /// <summary>把缓存的画面/光标 + 同伴欢迎包发给指定客户端（鉴权通过或免鉴权时调用）。</summary>
    private async Task SendInitialToClient(Client client)
    {
        if (_lastCursorDataPkt != null) await SendWsAsync(client.Ws, new ArraySegment<byte>(_lastCursorDataPkt), WebSocketMessageType.Binary, client.Cts.Token);
        if (_lastCursorPosPkt != null) await SendWsAsync(client.Ws, new ArraySegment<byte>(_lastCursorPosPkt), WebSocketMessageType.Binary, client.Cts.Token);
        if (_lastFramePkt != null) await SendWsAsync(client.Ws, new ArraySegment<byte>(_lastFramePkt), WebSocketMessageType.Binary, client.Cts.Token);
        await SendWsAsync(client.Ws, new ArraySegment<byte>(BuildPeerList(client)), WebSocketMessageType.Binary, client.Cts.Token);
    }

    // ---------- 多鼠标：自定义控制消息 ----------
    /// <summary>构造浏览器控制帧：[len(4,LE)][type(1)][payload]，与 app.js 解析端对齐。</summary>
    private static byte[] EncodeCtrl(byte type, byte[] payload)
    {
        int body = 1 + payload.Length;
        var pkt = new byte[4 + body];
        pkt[0] = (byte)body;
        pkt[1] = (byte)(body >> 8);
        pkt[2] = (byte)(body >> 16);
        pkt[3] = (byte)(body >> 24);
        pkt[4] = type;
        if (payload.Length > 0) Buffer.BlockCopy(payload, 0, pkt, 5, payload.Length);
        return pkt;
    }

    /// <summary>把某客户端的虚拟光标位置广播给所有浏览器。</summary>
    private void BroadcastPeer(Client c)
    {
        var pkt = BuildPeerCursor(c);
        List<Client> snapshot;
        lock (_clientsLock) snapshot = new List<Client>(_clients);
        // 同伴光标帧含操作者 IP，未鉴权客户端不应收到
        foreach (var k in snapshot) if (k.Authed) k.Chan.Writer.TryWrite(pkt);
    }

    /// <summary>构建"某浏览器虚拟光标位置"帧（type=50）：id(4)+x(2)+y(2)+ipLen(1)+ip。</summary>
    private byte[] BuildPeerCursor(Client c)
    {
        var ip = Encoding.UTF8.GetBytes(c.Ip);
        var payload = new byte[4 + 2 + 2 + 1 + ip.Length];
        int off = 0;
        payload[off++] = (byte)c.Id; payload[off++] = (byte)(c.Id >> 8);
        payload[off++] = (byte)(c.Id >> 16); payload[off++] = (byte)(c.Id >> 24);
        payload[off++] = (byte)c.VX; payload[off++] = (byte)(c.VX >> 8);
        payload[off++] = (byte)c.VY; payload[off++] = (byte)(c.VY >> 8);
        payload[off++] = (byte)ip.Length;
        Buffer.BlockCopy(ip, 0, payload, off, ip.Length);
        return EncodeCtrl(CtrlPeerCursor, payload);
    }

    /// <summary>构建"欢迎包"帧（type=51）：myId(4)+count(2)+[id(4)+x(2)+y(2)+ipLen(1)+ip]*，单独发给新客户端。</summary>
    private byte[] BuildPeerList(Client self)
    {
        List<Client> snapshot;
        lock (_clientsLock) snapshot = new List<Client>(_clients);
        int size = 4 + 2; // myId + count
        foreach (var c in snapshot) size += 4 + 2 + 2 + 1 + Encoding.UTF8.GetByteCount(c.Ip);
        var payload = new byte[size];
        int off = 0;
        payload[off++] = (byte)self.Id; payload[off++] = (byte)(self.Id >> 8);
        payload[off++] = (byte)(self.Id >> 16); payload[off++] = (byte)(self.Id >> 24);
        int count = snapshot.Count;
        payload[off++] = (byte)count; payload[off++] = (byte)(count >> 8);
        foreach (var c in snapshot)
        {
            var ip = Encoding.UTF8.GetBytes(c.Ip);
            payload[off++] = (byte)c.Id; payload[off++] = (byte)(c.Id >> 8);
            payload[off++] = (byte)(c.Id >> 16); payload[off++] = (byte)(c.Id >> 24);
            payload[off++] = (byte)c.VX; payload[off++] = (byte)(c.VX >> 8);
            payload[off++] = (byte)c.VY; payload[off++] = (byte)(c.VY >> 8);
            payload[off++] = (byte)ip.Length;
            Buffer.BlockCopy(ip, 0, payload, off, ip.Length);
            off += ip.Length;
        }
        return EncodeCtrl(CtrlPeerList, payload);
    }

    /// <summary>构建"同伴离开"帧（type=52）：id(4)。</summary>
    private byte[] BuildPeerLeave(int id)
    {
        var payload = new byte[4];
        payload[0] = (byte)id; payload[1] = (byte)(id >> 8);
        payload[2] = (byte)(id >> 16); payload[3] = (byte)(id >> 24);
        return EncodeCtrl(CtrlPeerLeave, payload);
    }

    private async Task ClientSendLoopAsync(Client c)
    {
        try
        {
            var ct = c.Cts.Token;
            while (await c.Chan.Reader.WaitToReadAsync(ct))
            {
                while (c.Chan.Reader.TryRead(out var packet))
                {
                    try { await SendWsAsync(c.Ws, new ArraySegment<byte>(packet), WebSocketMessageType.Binary, ct); }
                    catch { return; }
                }
            }
        }
        catch { }
        finally { RemoveClient(c); }
    }

    /// <summary>读取单个浏览器发来的输入，扇入到共享的 S端 连接。</summary>
    private async Task PumpWsToSrvAsync(Client client, CancellationToken ct)
    {
        var ws = client.Ws;
        try
        {
            var buf = new byte[1 << 20];
            byte[] packet = new byte[1 << 16];   // 跨 WS 分片重组后的完整包
            int packetLen = 0;
            bool discarding = false;              // 超大消息：丢弃直到 EndOfMessage
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var seg = new ArraySegment<byte>(buf);
                var res = await ws.ReceiveAsync(seg, ct);
                if (res.MessageType == WebSocketMessageType.Close) break;
                if (res.Count == 0) continue;

                // WebSocket 消息可分片：累加到 EndOfMessage 再解析。
                // 旧实现忽略分片，>1MB 的消息（如长剪贴板文本）首片就被丢弃。
                if (discarding) {
                    if (res.EndOfMessage) discarding = false;
                    continue;
                }
                if (packetLen + res.Count > packet.Length) {
                    int newSize = packet.Length;
                    while (newSize < packetLen + res.Count) newSize *= 2;
                    if (newSize > Protocol.MaxMessageBytes + 4) {
                        // 整条消息都超限：必须丢到 EndOfMessage，否则后续 continuation
                        // 会被当成新包解析，可能把伪造内容转发给被控端。
                        packetLen = 0; discarding = true; continue;
                    }
                    Array.Resize(ref packet, newSize);
                }
                Buffer.BlockCopy(buf, 0, packet, packetLen, res.Count);
                packetLen += res.Count;
                if (!res.EndOfMessage) continue;

                int n = packetLen;
                packetLen = 0;
                if (n < 5) continue;
                int bodyLen = packet[0] | (packet[1] << 8) | (packet[2] << 16) | (packet[3] << 24);
                if (bodyLen < 1 || bodyLen > Protocol.MaxMessageBytes || 4 + bodyLen != n) continue;
                int rawType = packet[4];
                var payload = new byte[bodyLen - 1];
                if (payload.Length > 0) Buffer.BlockCopy(packet, 5, payload, 0, payload.Length);

                // 浏览器上报访问密码（仅浏览器 <-> WebServer，不下发 S端）
                if (rawType == CtrlAuth)
                {
                    if (!client.Authed)
                    {
                        string pwd = payload.Length > 0 ? Encoding.UTF8.GetString(payload) : "";
                        bool ok = pwd == _password;
                        client.Authed = ok;
                        try { await SendWsAsync(ws, new ArraySegment<byte>(EncodeCtrl(CtrlAuthOk, new byte[] { ok ? (byte)1 : (byte)0 })), WebSocketMessageType.Binary, client.Cts.Token); } catch { }
                        if (ok)
                        {
                            try { await SendInitialToClient(client); } catch { }
                            BroadcastPeer(client);
                            EnsureServerConnected();   // 鉴权通过：懒拉起 S端 抓屏连接
                        }
                    }
                    continue;
                }

                // 未鉴权：丢弃一切输入与转发
                if (!client.Authed) continue;

                // 声音无需传输到 Web 端：忽略浏览器发来的"开启声音"请求，避免误触发采集
                if (rawType == (int)Protocol.MessageType.AudioToggle) continue;

                // 仅悬停上报的虚拟光标（不下发 S端）：更新自身虚拟坐标并广播给同伴
                if (rawType == VirtualCursor)
                {
                    if (payload.Length >= 4)
                    {
                        client.VX = payload[0] | (payload[1] << 8);
                        client.VY = payload[2] | (payload[3] << 8);
                        BroadcastPeer(client);
                    }
                    continue;
                }

                // 鼠标事件：按下/抬起/滚轮前先把真实光标瞬移到该位置
                if (rawType == (int)Protocol.MessageType.MouseEvent && payload.Length >= 1)
                {
                    int sub = payload[0];
                    int mx = payload.Length >= 3 ? (payload[1] | (payload[2] << 8)) : client.VX;
                    int my = payload.Length >= 5 ? (payload[3] | (payload[4] << 8)) : client.VY;
                    client.VX = mx; client.VY = my;
                    // 一条消息转发即可：RemoteDesktopServer.HandleMouse 会用 payload 自带的
                    // x/y 先定位再按键/滚轮。以前按下/抬起/滚轮额外发一条 move（两次 TCP 写、
                    // 两次抢 _srvWriteLock），高频拖动时会让按键落后于移动、画错位置。
                    await SendToServerAsync(Protocol.MessageType.MouseEvent, payload, ct);
                    BroadcastPeer(client);
                    continue;
                }

                await SendToServerAsync((Protocol.MessageType)rawType, payload, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"[Web] 浏览器输入读取异常：{ex.Message}"); }
        finally { RemoveClient(client); }
    }

    private async Task SendToServerAsync(Protocol.MessageType type, byte[] payload, CancellationToken ct)
    {
        NetworkStream? s;
        lock (_srvLock) s = _srvStream;
        if (s == null) return;
        try
        {
            await _srvWriteLock.WaitAsync(ct);
            try { await Protocol.SendAsync(s, type, payload, ct); }
            finally { _srvWriteLock.Release(); }
        }
        catch (Exception ex) { Log($"[Web] 写入被控端失败：{ex.Message}"); }
    }

    // ---------- 共享 S端 连接（登录 + 重连 + 心跳 + 扇出） ----------
    private async Task ServerConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? tcp = null;
            NetworkStream? stream = null;
            try
            {
                tcp = new TcpClient();
                tcp.NoDelay = true;
                await tcp.ConnectAsync(IPAddress.Loopback, _serverTcpPort);
                stream = tcp.GetStream();

                // 用配置的密码登录（WebServer 作为唯一查看端）
                await Protocol.SendAsync(stream, Protocol.MessageType.LoginRequest,
                    Encoding.UTF8.GetBytes(_password), ct);
                var resp = await Protocol.ReadMessageAsync(stream, ct);
                if (resp == null || resp.Value.type != Protocol.MessageType.LoginResponse
                    || resp.Value.payload.Length < 1 || resp.Value.payload[0] != 1)
                {
                    Log($"[Web] 被控端登录失败（密码错误？），5 秒后重试");
                    try { stream.Dispose(); tcp.Dispose(); } catch { }
                    await Task.Delay(5000, ct);
                    continue;
                }
                Log($"[Web] 已作为查看端登录被控端，开始把画面扇出给浏览器");

                // 请求立即来一整帧，便于后加入的浏览器立刻有画面
                try { await Protocol.SendAsync(stream, Protocol.MessageType.ForceKeyFrame, Array.Empty<byte>(), ct); }
                catch { }

                lock (_srvLock)
                {
                    try { _srvStream?.Dispose(); _srvTcp?.Dispose(); } catch { }
                    _srvTcp = tcp; _srvStream = stream;
                }

                // 心跳（防止 S端 读空闲超时断开本查看端）
                _ = Task.Run(() => PingLoopAsync(stream, ct));
                // 读取并扇出
                await PumpSrvToClientsAsync(stream, ct);

                // 连接断开
                lock (_srvLock) { if (_srvStream == stream) { _srvStream = null!; _srvTcp = null!; } }
                Log($"[Web] 被控端连接断开，准备重连");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"[Web] 被控端连接异常：{ex.Message}");
                try { lock (_srvLock) { if (_srvStream == stream) { _srvStream = null!; _srvTcp = null!; } } } catch { }
                try { await Task.Delay(2000, ct); } catch { }
            }
            finally
            {
                try { stream?.Dispose(); } catch { }
                try { tcp?.Dispose(); } catch { }
            }
        }
    }

    private async Task PumpSrvToClientsAsync(NetworkStream stream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var msg = await Protocol.ReadMessageAsync(stream, ct);
            if (msg == null) break;
            var (type, payload) = msg.Value;
            // 声音无需传输到 Web 端：即便被控端（或旧版本 / 其它查看端）开启了采集，
            // 也在这里把音频报文挡在网关内，绝不扇出给浏览器，省带宽也免隐私风险。
            if (type == Protocol.MessageType.AudioFormat
                || type == Protocol.MessageType.AudioFrame) continue;
            byte[] packet = Protocol.Encode(type, payload);
            if (type == Protocol.MessageType.VideoFrame)
            {
                // 只缓存整帧作为新客户端首帧：区域差分帧缺少底图，发给新浏览器会黑屏/花屏
                // 直到下一次整帧重同步（默认 4 秒）。flags 最低位 = FLAG_REGION。
                if (payload.Length > 0 && (payload[0] & 1) == 0) _lastFramePkt = packet;
                TryForwardPptFrame(payload);
            }
            else if (type == Protocol.MessageType.CursorData) _lastCursorDataPkt = packet;
            else if (type == Protocol.MessageType.CursorPosition) _lastCursorPosPkt = packet;
            Broadcast(packet);
        }
    }

    private async Task PingLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        try
        {
            var ts = new byte[8];
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(10000, ct);
                lock (_srvLock) { if (_srvStream != stream) break; }
                BitConverter.GetBytes(DateTime.UtcNow.Ticks).CopyTo(ts, 0);
                await SendToServerAsync(Protocol.MessageType.Ping, ts, ct);
            }
        }
        catch { }
    }

    private void RemoveClient(Client c)
    {
        bool removed;
        lock (_clientsLock)
        {
            // 握手失败、读循环 finally、服务停止批量清理等多个入口都会走到这里，
            // 同一 Client 可能被处理两次：重复 Cancel/Dispose 并刷两条断开日志。
            if (c.Removed) return;
            c.Removed = true;
            removed = _clients.Remove(c);
        }
        // 客户端还没入列就握手失败时也必须清理：旧实现在这里直接 return，
        // 导致 Cts / Channel / WebSocket 全部泄漏，连接永远挂在"已连接"状态。
        if (removed)
        {
            StopServerIfIdle();   // 若已无浏览器，断开 S端 连接让其空闲
            Broadcast(BuildPeerLeave(c.Id));   // 通知其它客户端：该同伴已离开
        }
        try { c.Cts.Cancel(); } catch { }
        try { c.Chan.Writer.TryComplete(); } catch { }
        try { if (c.Ws.State == WebSocketState.Open) c.Ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).Wait(500); } catch { }
        try { c.Ws.Dispose(); } catch { }
        try { c.Cts.Dispose(); } catch { }
        Log($"[Web] 浏览器已断开（当前 {_clients.Count} 个）");
    }

    private static void Log(string s) => WebLog(s);
    private static void WebLog(string s)
    {
        try
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {s}";
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, AppIdentity.WebLogFileName), line + "\r\n");
        }
        catch { }
    }

    public void Dispose()
    {
        try { ShowController.StateChanged -= BroadcastShowState; } catch { }
        // 兼容通道的重试定时器必须在服务停止时一起停掉，否则仍会周期性尝试建端口。
        try { var t = _compatWsRetry; _compatWsRetry = null; t?.Dispose(); } catch { }
        try { _cts?.Cancel(); } catch { }
        try { _srvCts?.Cancel(); } catch { }
        try { _listener?.Close(); } catch { }
        try { _rawWs?.Dispose(); } catch { }
        try { _rawHttp?.Dispose(); } catch { }
        List<Client> snapshot;
        lock (_clientsLock) snapshot = new List<Client>(_clients);
        foreach (var c in snapshot) RemoveClient(c);
        try { lock (_srvLock) { _srvStream?.Dispose(); _srvTcp?.Dispose(); } } catch { }
        try { _cts?.Dispose(); } catch { }
        try { _srvCts?.Dispose(); } catch { }
    }
}
