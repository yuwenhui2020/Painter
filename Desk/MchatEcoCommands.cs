// ============================================================
// MchatEcoCommands —— 把 Mchat 生态频道的消息接到白板实际能力上。
//
// 收到 MchatA / MchatW 通过 45778 帧发来的命令后，复用白板既有的
// PAINTER-CMD 命令语义（web-on / web-off / status / hello），经同一套
// 局域网鉴权后执行 Web 控制台开关，并把结果以 painterack 帧回送。
//
// 命令识别（两种写法都支持，兼容手机端与网页端）：
//   ① text = "PAINTER-CMD|web-on"（与 UDP 21118 命令完全一致）
//   ② kind = "paintercmd" / "command"，text = "web-on"
// 非命令消息不会触发任何动作，白板可安全常驻订阅。
// ============================================================
using System;
using System.Collections.Generic;
using System.Net;

namespace desk;

internal static class MchatEcoCommands
{
    const string CmdPrefix = "PAINTER-CMD|";

    public static void Start()
    {
        MchatEcoBridge.MessageReceived -= OnMessage;
        MchatEcoBridge.MessageReceived += OnMessage;
    }

    public static void Stop()
    {
        MchatEcoBridge.MessageReceived -= OnMessage;
    }

    static void OnMessage(Dictionary<string, object> msg, string fromIp)
    {
        try
        {
            string kind = GetStr(msg, "kind");

            // —— 白板笔迹共享：收到 .icstk 字节（ISF），合并进当前画布 ——
            // 由 MchatA/MchatW 的 /api/send(kind=whiteboard.stroke, base64) 或其它白板
            // 经 SendBytes 推来。body 在 MchatEcoBridge 里已塞进 msg["body"]。
            if (kind == "whiteboard.stroke")
            {
                var body = GetBody(msg);
                if (body != null && body.Length > 0)
                {
                    bool ok = CanvasControl.ImportStrokes != null && CanvasControl.ImportStrokes(body);
                    AppLog.Write("MchatEco", "收到白板笔迹 " + body.Length + " B <- " + fromIp + " : " + (ok ? "已导入" : "未受理"));
                    MchatEcoBridge.SendText(fromIp, ok ? "stroke-ok" : "stroke-fail", kind: "painterack");
                }
                return;
            }

            string text = GetStr(msg, "text");
            string action = "";

            if (text.StartsWith(CmdPrefix, StringComparison.OrdinalIgnoreCase))
                action = text.Substring(CmdPrefix.Length).Split('|')[0].Trim().ToLowerInvariant();
            else if (kind == "paintercmd" || kind == "command")
                action = text.Trim().ToLowerInvariant();

            if (string.IsNullOrEmpty(action)) return;

            IPAddress src;
            if (!IPAddress.TryParse(fromIp, out src) || !LanDiscovery.IsInSameSubnet(src))
            {
                Reply(fromIp, action, "denied", "拒绝：来源不在同一局域网");
                return;
            }

            string result = "ok", note;
            switch (action)
            {
                case "web-on":
                    note = WebControl.SetEnabled(true);
                    break;
                case "web-off":
                    note = WebControl.SetEnabled(false);
                    break;
                case "status":
                case "hello":
                    note = "在线 · Web " + (WebStatus.IsRunning ? "运行中 :" + WebStatus.Port : "已关闭");
                    break;
                case "stroke-get":
                    // 把当前画布笔迹导出成 .icstk 字节，回送请求方。
                    byte[] isf = CanvasControl.ExportStrokes != null ? CanvasControl.ExportStrokes() : null;
                    if (isf != null && isf.Length > 0)
                    {
                        MchatEcoBridge.SendBytes(fromIp, "whiteboard.stroke", isf,
                            kind: "whiteboard.stroke", name: "painter-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".icstk");
                        note = "已发送笔迹 " + isf.Length + " B";
                    }
                    else { result = "error"; note = "画布为空或导出不可用"; }
                    break;
                default:
                    result = "error";
                    note = "未知命令：" + action;
                    break;
            }
            Reply(fromIp, action, result, note);
            AppLog.Write("MchatEco", "命令 " + action + " <- " + fromIp + " : " + result + " " + note);
        }
        catch (Exception ex)
        {
            AppLog.Write("MchatEco", "命令处理失败: " + ex.Message);
        }
    }

    static void Reply(string ip, string action, string result, string note)
    {
        MchatEcoBridge.SendText(ip,
            "PAINTER-ACK|" + action + "|" + result + "|" + WebStatus.Port
            + "|" + (WebStatus.IsRunning ? 1 : 0) + "|" + (WebStatus.IsLoopbackOnly ? 1 : 0) + "|" + note,
            kind: "painterack");
    }

    static string GetStr(Dictionary<string, object> d, string key)
    {
        if (d == null || !d.TryGetValue(key, out var v) || v == null) return "";
        return v.ToString();
    }

    static byte[] GetBody(Dictionary<string, object> d)
    {
        if (d == null || !d.TryGetValue("body", out var v) || v == null) return null;
        return v as byte[];
    }
}
