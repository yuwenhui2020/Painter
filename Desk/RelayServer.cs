using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows.Forms;

namespace desk;

internal sealed class RelayServer : IDisposable
{
	private class Pending
	{
		public TcpClient? Host;

		public TcpClient? Viewer;

		public TaskCompletionSource<bool>? Ready;
	}

	private TcpListener _listener = null;

	private CancellationTokenSource? _cts;

	private readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending> ();

	private readonly object _gate = new object ();

	public int Port { get; }

	public RelayServer (int port)
	{
		Port = port;
	}

	public void Start ()
	{
		_listener = new TcpListener (IPAddress.Any, Port);
		_listener.Start ();
		_cts = new CancellationTokenSource ();
		Task.Run (async delegate {
			await AcceptLoopAsync (_cts.Token);
		});
	}

	private async Task AcceptLoopAsync (CancellationToken ct)
	{
		while (!ct.IsCancellationRequested) {
			TcpClient client;
			try {
				client = await _listener.AcceptTcpClientAsync ();
			} catch {
				break;
			}
			Task.Run (async delegate {
				try {
					NetworkStream stream = client.GetStream ();
					byte[] header = new byte[5];
					if (!(await ReadExactAsync (stream, header, 5, ct))) {
						client.Dispose ();
					} else {
						int tlen = BitConverter.ToInt32 (header, 1);
						// 长度来自网络：无上限/负值校验会让恶意或畸形握手包触发 ~2GB 分配甚至 OOM 崩溃。
						if (tlen <= 0 || tlen > 8192) {
							client.Dispose ();
							return;
						}
						byte[] tbuf = new byte[tlen];
						if (!(await ReadExactAsync (stream, tbuf, tlen, ct))) {
							client.Dispose ();
						} else {
							string token = Encoding.UTF8.GetString (tbuf);
							bool isHost = header [0] == 72;
							Pending p;
							lock (_gate) {
								if (!_pending.TryGetValue (token, out p)) {
									p = new Pending ();
									_pending [token] = p;
								}
							}
							if (isHost) {
								p.Host = client;
							} else {
								p.Viewer = client;
							}
							if (p.Ready == null) {
								p.Ready = new TaskCompletionSource<bool> ();
								Task.Delay (30000, ct).ContinueWith (delegate {
									try {
										// TrySetCanceled 成功说明 30 秒内没凑齐两端：清掉已取消的条目，
										// 否则同一个 token 再次连接会永久命中这个已取消的 Ready。
										if (p.Ready.TrySetCanceled ()) {
											lock (_gate) {
												if (_pending.TryGetValue (token, out var pending) && pending == p)
													_pending.Remove (token);
											}
											try { p.Host?.Dispose (); } catch { }
											try { p.Viewer?.Dispose (); } catch { }
										}
									} catch {
									}
								}, ct);
							}
							if ((isHost && p.Viewer != null) || (!isHost && p.Host != null)) {
								p.Ready.TrySetResult (true);
							}
							try {
								await p.Ready.Task;
							} catch {
								client.Dispose ();
								lock (_gate) {
									// 配对失败/超时：移除本端引用并清掉无用的待配对项
									if (_pending.TryGetValue (token, out var pending) && pending == p) {
										if (isHost) p.Host = null; else p.Viewer = null;
										if (p.Host == null && p.Viewer == null) _pending.Remove (token);
									}
								}
								return;
							}
							TcpClient other = (isHost ? p.Viewer : p.Host);
							// 另一端可能在配对窗口内超时/异常退出，此处快照可能为 null
							if (other == null) {
								client.Dispose ();
								lock (_gate) { _pending.Remove (token); }
								return;
							}
							await BridgeAsync (stream, other.GetStream (), ct);
							client.Dispose ();
							other.Dispose ();
							lock (_gate) {
								_pending.Remove (token);
							}
						}
					}
				} catch {
					try {
						client.Dispose ();
					} catch {
					}
				}
			});
		}
	}

	private async Task BridgeAsync (NetworkStream a, NetworkStream b, CancellationToken ct)
	{
		await Task.WhenAny (Pump (a, b), Pump (b, a));
		async Task Pump (NetworkStream src, NetworkStream dst)
		{
			byte[] buf = new byte[65536];
			try {
				while (!ct.IsCancellationRequested) {
					int n = await src.ReadAsync (buf, 0, buf.Length, ct);
					if (n == 0) {
						break;
					}
					await dst.WriteAsync (buf, 0, n, ct);
				}
			} catch {
			}
		}
	}

	private static async Task<bool> ReadExactAsync (NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
	{
		int n;
		for (int read = 0; read < count; read += n) {
			n = await stream.ReadAsync (buffer, read, count - read, ct);
			if (n == 0) {
				return false;
			}
		}
		return true;
	}

	public void Stop ()
	{
		try {
			_cts?.Cancel ();
		} catch {
		}
		try {
			_listener.Stop ();
		} catch {
		}
	}

	public void Dispose ()
	{
		Stop ();
	}
}
