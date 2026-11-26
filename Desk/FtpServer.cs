using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Windows.Forms;

namespace desk
{
    /// <summary>
    /// 极简 FTP 服务（被动模式 + 匿名登录），供局域网内任意 FTP 客户端（如 FileZilla）连接，
    /// 根目录固定为 ShareFolder.Root（桌面 share 文件夹）。
    /// 仅实现常用命令：USER/PASS/SYST/FEAT/PWD/CWD/CDUP/TYPE/PASV/LIST/NLST/SIZE/RETR/STOR/MKD/RMD/DELE/RNFR/RNTO/NOOP/QUIT/OPTS。
    /// </summary>
    internal sealed class FtpServer : IDisposable
    {
        private readonly int _port;
        private readonly string _root;
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private int _pasvPort = 2122;
        public const int PasvRangeEnd = 2152;    // 被动数据端口范围 2122–2152，启动时一并放行防火墙
        public const int PasvRangeStart = 2122;

        public Action<string> Log;

        public FtpServer(int port)
        {
            _port = port;
            _root = ShareFolder.Root;
        }

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoop(_cts.Token));
            Log?.Invoke($"[FTP] 已启动 ftp://0.0.0.0:{_port}/  根目录：{_root}");
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
        }

        // ---------- 接受连接 ----------
        private void AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    _ = Task.Run(() => HandleClient(client, ct));
                }
                catch (Exception ex) when (!(ex is ObjectDisposedException))
                {
                    Log?.Invoke("[FTP] 接受连接失败：" + ex.Message);
                }
            }
        }

        private void HandleClient(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };
                string cwd = "";
                TcpListener pasv = null;
                try
                {
                    Send(writer, "220 painter FTP server ready.");
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        int sp = line.IndexOf(' ');
                        string cmd = (sp < 0 ? line : line.Substring(0, sp)).ToUpperInvariant();
                        string arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();

                        // QUIT 时返回 false 结束会话
                        if (!HandleCommand(cmd, arg, ref cwd, ref pasv, client, writer))
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log?.Invoke("[FTP] 会话异常：" + ex.Message);
                }
                finally
                {
                    try { pasv?.Stop(); } catch { }
                }
            }
        }

        // ---------- 命令分发 ----------
        // 返回 false 表示应结束会话（QUIT）
        private bool HandleCommand(string cmd, string arg, ref string cwd, ref TcpListener pasv, TcpClient client, StreamWriter w)
        {
            switch (cmd)
            {
                case "USER":
                    Send(w, "331 Anonymous login ok, send password.");
                    return true;
                case "PASS":
                    Send(w, "230 Logged in (anonymous).");
                    return true;
                case "SYST":
                    Send(w, "215 UNIX Type: L8");
                    return true;
                case "FEAT":
                    Send(w, "211-Extensions supported\r\n PASV\r\n UTF8\r\n211 End");
                    return true;
                case "OPTS":
                    Send(w, "200 OPTS okay.");
                    return true;
                case "NOOP":
                    Send(w, "200 NOOP ok.");
                    return true;
                case "PWD":
                case "XPWD":
                    Send(w, "257 \"/" + cwd.Replace('\\', '/').Trim('/') + "\" is current directory.");
                    return true;
                case "CWD":
                case "XCWD":
                    try
                    {
                        string target = ResolveRel(cwd, arg);
                        if (!Directory.Exists(target)) { Send(w, "550 Directory not found."); return true; }
                        cwd = RelOf(target);
                        Send(w, "250 CWD command successful.");
                    }
                    catch (Exception ex) { Send(w, "550 " + ex.Message); }
                    return true;
                case "CDUP":
                    try
                    {
                        string target = ResolveRel(cwd, "..");
                        cwd = RelOf(target);
                        Send(w, "250 CDUP command successful.");
                    }
                    catch (Exception ex) { Send(w, "550 " + ex.Message); }
                    return true;
                case "TYPE":
                    Send(w, "200 Type set okay.");
                    return true;
                case "PASV":
                    return DoPasv(ref pasv, client, w);
                case "LIST":
                case "NLST":
                    return DoList(arg, cwd, ref pasv, w);
                case "SIZE":
                    return DoSize(arg, cwd, w);
                case "RETR":
                    return DoRetr(arg, cwd, ref pasv, w);
                case "STOR":
                case "STOU":
                    return DoStor(arg, cwd, ref pasv, w);
                case "MKD":
                case "XMKD":
                    return DoMkd(arg, cwd, w);
                case "RMD":
                case "XRMD":
                    return DoRmd(arg, cwd, w);
                case "DELE":
                    return DoDele(arg, cwd, w);
                case "RNFR":
                    _renameFrom = ResolveRel(cwd, arg);
                    Send(w, "350 File exists, ready for destination.");
                    return true;
                case "RNTO":
                    return DoRnto(arg, cwd, w);
                case "QUIT":
                    Send(w, "221 Goodbye.");
                    return false;
                default:
                    Send(w, "500 Command not understood.");
                    return true;
            }
        }

        private string _renameFrom;

        // ---------- 数据连接（被动模式） ----------
        private bool DoPasv(ref TcpListener pasv, TcpClient client, StreamWriter w)
        {
            try { pasv?.Stop(); } catch { }
            if (!TryCreatePasv(out var l, out int port))
            {
                Send(w, "425 Can't open passive connection.");
                return true;
            }
            pasv = l;
            var ip = ((IPEndPoint)client.Client.LocalEndPoint).Address;
            byte[] b = ip.GetAddressBytes();
            int p1 = port / 256, p2 = port % 256;
            Send(w, $"227 Entering Passive Mode ({b[0]},{b[1]},{b[2]},{b[3]},{p1},{p2}).");
            return true;
        }

        private bool TryCreatePasv(out TcpListener l, out int port)
        {
            l = null; port = 0;
            int range = PasvRangeEnd - 2122 + 1;
            int start = _pasvPort;
            for (int i = 0; i < range; i++)
            {
                int p = 2122 + ((start + i) % range);
                try
                {
                    var t = new TcpListener(IPAddress.Any, p);
                    t.Start();
                    l = t; port = p;
                    _pasvPort = p + 1;
                    return true;
                }
                catch { }
            }
            return false;
        }

        // 等待被动数据连接（30s 超时）
        private TcpClient AcceptPasv(ref TcpListener pasv, StreamWriter w)
        {
            if (pasv == null) { Send(w, "425 Use PASV first."); return null; }
            try
            {
                var ar = pasv.BeginAcceptTcpClient(null, null);
                if (!ar.AsyncWaitHandle.WaitOne(30000))
                {
                    Send(w, "425 Data connection timeout.");
                    return null;
                }
                var c = pasv.EndAcceptTcpClient(ar);
                try { pasv.Stop(); } catch { }
                pasv = null;
                return c;
            }
            catch (Exception ex)
            {
                Send(w, "425 " + ex.Message);
                return null;
            }
        }

        // ---------- 目录/文件操作 ----------
        private bool DoList(string arg, string cwd, ref TcpListener pasv, StreamWriter w)
        {
            string dir = ResolveRel(cwd, arg);
            if (!Directory.Exists(dir)) { Send(w, "550 Directory not found."); return true; }
            var data = AcceptPasv(ref pasv, w);
            if (data == null) return true;
            using (data)
            {
                var ds = data.GetStream();
                var sb = new StringBuilder();
                foreach (var d in Directory.GetDirectories(dir))
                    sb.Append(FormatEntry(d, true));
                foreach (var f in Directory.GetFiles(dir))
                    sb.Append(FormatEntry(f, false));
                var bytes = Encoding.ASCII.GetBytes(sb.ToString());
                ds.Write(bytes, 0, bytes.Length);
            }
            Send(w, "226 Transfer complete.");
            return true;
        }

        private static string FormatEntry(string path, bool isDir)
        {
            var fi = new FileInfo(path);
            long size = isDir ? 0 : fi.Length;
            string time = fi.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm");
            string perm = isDir ? "drwxr-xr-x" : "-rw-r--r--";
            return $"{perm} 1 user group {size} {time} {fi.Name}\r\n";
        }

        private bool DoSize(string arg, string cwd, StreamWriter w)
        {
            string fp = ResolveRel(cwd, arg);
            if (!File.Exists(fp)) { Send(w, "550 File not found."); return true; }
            Send(w, "213 " + new FileInfo(fp).Length);
            return true;
        }

        private bool DoRetr(string arg, string cwd, ref TcpListener pasv, StreamWriter w)
        {
            string fp = ResolveRel(cwd, arg);
            if (!File.Exists(fp)) { Send(w, "550 File not found."); return true; }
            var data = AcceptPasv(ref pasv, w);
            if (data == null) return true;
            using (data)
            {
                var ds = data.GetStream();
                Send(w, "150 Opening data connection for RETR.");
                using (var fs = File.OpenRead(fp))
                    fs.CopyTo(ds);
            }
            Send(w, "226 Transfer complete.");
            return true;
        }

        private bool DoStor(string arg, string cwd, ref TcpListener pasv, StreamWriter w)
        {
            string fp = ResolveRel(cwd, arg);
            var data = AcceptPasv(ref pasv, w);
            if (data == null) return true;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fp));
                var ds = data.GetStream();
                Send(w, "150 Opening data connection for STOR.");
                using (data)
                using (var fs = File.Create(fp))
                    ds.CopyTo(fs);
                Send(w, "226 Transfer complete.");
            }
            catch (Exception ex) { Send(w, "550 " + ex.Message); }
            return true;
        }

        private bool DoMkd(string arg, string cwd, StreamWriter w)
        {
            try
            {
                string dp = ResolveRel(cwd, arg);
                Directory.CreateDirectory(dp);
                Send(w, "257 \"" + RelOf(dp).Replace('\\', '/') + "\" created.");
            }
            catch (Exception ex) { Send(w, "550 " + ex.Message); }
            return true;
        }

        private bool DoRmd(string arg, string cwd, StreamWriter w)
        {
            try
            {
                string dp = ResolveRel(cwd, arg);
                if (!Directory.Exists(dp)) { Send(w, "550 Directory not found."); return true; }
                Directory.Delete(dp, true);
                Send(w, "250 Directory removed.");
            }
            catch (Exception ex) { Send(w, "550 " + ex.Message); }
            return true;
        }

        private bool DoDele(string arg, string cwd, StreamWriter w)
        {
            try
            {
                string fp = ResolveRel(cwd, arg);
                if (File.Exists(fp)) File.Delete(fp);
                else if (Directory.Exists(fp)) Directory.Delete(fp, true);
                else { Send(w, "550 Not found."); return true; }
                Send(w, "250 File deleted.");
            }
            catch (Exception ex) { Send(w, "550 " + ex.Message); }
            return true;
        }

        private bool DoRnto(string arg, string cwd, StreamWriter w)
        {
            try
            {
                if (_renameFrom == null) { Send(w, "503 RNFR required first."); return true; }
                string to = ResolveRel(cwd, arg);
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                if (Directory.Exists(_renameFrom)) Directory.Move(_renameFrom, to);
                else File.Move(_renameFrom, to);
                _renameFrom = null;
                Send(w, "250 Rename successful.");
            }
            catch (Exception ex) { Send(w, "550 " + ex.Message); }
            return true;
        }

        // ---------- 路径工具 ----------
        private string ResolveRel(string cwd, string arg)
        {
            if (string.IsNullOrEmpty(arg) || arg == ".") return ShareFolder.Resolve(cwd);
            if (arg.StartsWith("/")) return ShareFolder.Resolve(arg); // 绝对（相对根）
            return ShareFolder.Resolve(cwd + "/" + arg);
        }

        private static string RelOf(string absolute)
        {
            var root = ShareFolder.Root;
            var full = Path.GetFullPath(absolute);
            if (full.Length <= root.Length) return "";
            var rel = full.Substring(root.Length).Replace('\\', '/').Trim('/');
            return rel;
        }

        private static void Send(StreamWriter w, string s)
        {
            try { w.Write(s + "\r\n"); w.Flush(); } catch { }
        }
    }
}
