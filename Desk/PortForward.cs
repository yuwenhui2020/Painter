using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Windows.Forms;

namespace desk;

internal sealed class PortForward : IDisposable
{
	private sealed class Channel
	{
		public int Id;

		public NetworkStream? Remote;

		public TcpClient? RemoteClient;

		public bool Opened;

		public long SendCredit = 262144L;

		/// <summary>流控信用变化信号：替代原 5ms 忙轮询，避免对端不回窗口时永久挂起。</summary>
		public readonly SemaphoreSlim CreditSignal = new SemaphoreSlim (0);

		/// <summary>本通道是否已关闭（用于打断等待）。</summary>
		public volatile bool Closed;
	}

	public const byte FT_OPEN = 1;

	public const byte FT_OPENED = 2;

	public const byte FT_DATA = 3;

	public const byte FT_CLOSE = 4;

	public const byte FT_WINDOW = 5;

	private const int MaxFrame = 65536;

	private readonly Func<byte[], Task> _sendRaw;

	private readonly Dictionary<int, Channel> _channels = new Dictionary<int, Channel> ();

	private readonly object _gate = new object ();

	private int _nextId = 1;

	/// <summary>本机监听的停止令牌：Dispose 时取消并 Stop listener，释放端口。</summary>
	private CancellationTokenSource? _listenCts;
	private TcpListener? _listener;

	public Action<string>? Log;

	public bool IsServer { get; set; }

	public PortForward (Func<byte[], Task> sendRaw)
	{
		_sendRaw = sendRaw;
	}

	public async Task<int> StartLocalAsync (int localPort, string remoteHost, int remotePort)
	{
		_listenCts?.Cancel ();
		try { _listenCts?.Dispose (); } catch { }
		_listenCts = new CancellationTokenSource ();
		CancellationToken ct = _listenCts.Token;

		TcpListener listener = new TcpListener (IPAddress.Loopback, localPort);
		_listener = listener;
		listener.Start ();
		int actual = ((IPEndPoint)listener.LocalEndpoint).Port;
		Log?.Invoke ($"端口转发：本机 {actual} → 远端 {remoteHost}:{remotePort}");
		Task.Run (async delegate {
			try {
				while (!ct.IsCancellationRequested) {
					TcpClient client = await listener.AcceptTcpClientAsync ().ConfigureAwait (false);
					if (ct.IsCancellationRequested) { try { client.Dispose (); } catch { } break; }
					int id = NewId ();
					Channel ch = new Channel {
						Id = id,
						Remote = client.GetStream (),
						RemoteClient = client,
						Opened = true
					};
					lock (_gate) {
						_channels [id] = ch;
					}
					await SendFrameAsync (id, 1, EncodeOpen (remoteHost, remotePort)).ConfigureAwait (false);
					Task.Run (() => PumpLocalAsync (ch));
				}
			} catch {
			}
		});
		return actual;
	}

	private async Task PumpLocalAsync (Channel ch)
	{
		byte[] buf = new byte[65536];
		try {
			while (true) {
				int n = await ch.Remote.ReadAsync (buf, 0, buf.Length);
				if (n <= 0) {
					break;
				}
				await SendFrameAsync (ch.Id, 3, buf.AsSpan (0, n).ToArray ());
			}
		} catch {
		}
		await CloseChannelAsync (ch.Id);
	}

	private async Task HandleOpenAsync (int id, byte[] payload)
	{
		// DecodeOpen 解析远端帧，可能抛格式/越界异常；必须放进 try，
		// 否则 fire-and-forget 任务的异常会逃逸（该任务无外层兜底）。
		string host;
		int port;
		try {
			(host, port) = DecodeOpen (payload);
		} catch (Exception ex) {
			Log?.Invoke ($"端口转发：解析打开请求失败 - {ex.Message}");
			// 3=FT_DATA、4=FT_CLOSE：解析失败不能建立通道，必须回 CLOSE，
			// 否则对端把空数据帧写入不存在的通道并永久挂着该 channel/TcpClient。
			await SendFrameAsync (id, 4, Array.Empty<byte> ());
			return;
		}
		try {
			TcpClient client = new TcpClient ();
			await client.ConnectAsync (host, port);
			Channel ch = new Channel {
				Id = id,
				Remote = client.GetStream (),
				RemoteClient = client,
				Opened = true
			};
			lock (_gate) {
				_channels [id] = ch;
			}
			await SendFrameAsync (id, 2, Array.Empty<byte> ());
			Task.Run (() => PumpRemoteAsync (ch));
			Log?.Invoke ($"端口转发：已连接目标 {host}:{port}");
		} catch (Exception ex) {
			Log?.Invoke ($"端口转发：连接 {host}:{port} 失败 - {ex.Message}");
			await SendFrameAsync (id, 4, Array.Empty<byte> ());
		}
	}

	private async Task PumpRemoteAsync (Channel ch)
	{
		byte[] buf = new byte[65536];
		try {
			while (!ch.Closed) {
				int n = await ch.Remote.ReadAsync (buf, 0, buf.Length).ConfigureAwait (false);
				if (n <= 0) {
					break;
				}
				// 等流控信用：最多等 10 秒。对端崩溃/恶意不回窗口时不再永久挂起，
				// 直接关闭通道释放连接；用信号替代 5ms 忙轮询，降低 CPU 空转。
				long deadline = NetFx.TickCount64 + 10000;
				while (true) {
					lock (_gate) {
						if (ch.SendCredit >= n) { ch.SendCredit -= n; break; }
					}
					if (ch.Closed || NetFx.TickCount64 - deadline >= 0) {
						Log?.Invoke ($"端口转发：通道 {ch.Id} 等待流控超时，已关闭");
						await CloseChannelAsync (ch.Id).ConfigureAwait (false);
						return;
					}
					try { await ch.CreditSignal.WaitAsync (200).ConfigureAwait (false); } catch { }
				}
				await SendFrameAsync (ch.Id, 3, buf.AsSpan (0, n).ToArray ()).ConfigureAwait (false);
			}
		} catch {
		}
		await CloseChannelAsync (ch.Id);
	}

	public async Task OnFrameAsync (byte[] frame)
	{
		if (frame.Length < 5) {
			return;
		}
		int id = Protocol.ReadInt32 (frame, 0);
		byte type = frame [4];
		byte[] payload = ((frame.Length > 5) ? frame.AsSpan (5).ToArray () : Array.Empty<byte> ());
		switch (type) {
		case 1:
			if (IsServer) {
				await HandleOpenAsync (id, payload);
			}
			break;
		case 2: {
			Channel ch3;
			lock (_gate) {
				_channels.TryGetValue (id, out ch3);
			}
			if (ch3 != null) {
				ch3.Opened = true;
			}
			break;
		}
		case 3: {
			Channel ch2;
			lock (_gate) {
				_channels.TryGetValue (id, out ch2);
			}
			if (ch2?.Remote != null) {
				try {
					await ch2.Remote.WriteAsync (payload, 0, payload.Length);
				} catch {
					await CloseChannelAsync (id);
				}
			}
			break;
		}
		case 4:
			await CloseChannelAsync (id);
			break;
		case 5: {
			Channel ch;
			lock (_gate) {
				_channels.TryGetValue (id, out ch);
			}
			if (ch != null && payload.Length >= 4) {
				int grant = Protocol.ReadInt32 (payload, 0);
				lock (_gate) {
					// 防止对端发来异常大/负值让信用溢出或者变成负数导致永久等待。
					if (grant > 0 && ch.SendCredit < 64L * 1024 * 1024) {
						ch.SendCredit += grant;
						if (ch.SendCredit > 64L * 1024 * 1024) ch.SendCredit = 64L * 1024 * 1024;
					}
				}
				try { ch.CreditSignal.Release (); } catch { }
			}
			break;
		}
		}
	}

	private async Task CloseChannelAsync (int id)
	{
		Channel ch;
		lock (_gate) {
			_channels.TryGetValue (id, out ch);
			_channels.Remove (id);
		}
		if (ch == null) {
			return;
		}
		ch.Closed = true;
		try { ch.CreditSignal.Release (); } catch { } // 唤醒等待信用的 PumpRemoteAsync
		try {
			ch.Remote?.Dispose ();
		} catch {
		}
		try {
			ch.RemoteClient?.Close ();
		} catch {
		}
		try {
			await SendFrameAsync (id, 4, Array.Empty<byte> ());
		} catch {
		}
	}

	/// <summary>停止本机监听并释放所有通道，供远控会话结束时调用，避免端口被永久占用。</summary>
	public void Dispose ()
	{
		try { _listenCts?.Cancel (); } catch { }
		try { _listener?.Stop (); } catch { }
		try { _listenCts?.Dispose (); } catch { }
		_listenCts = null;
		_listener = null;
		Channel [] channels;
		lock (_gate) {
			channels = _channels.Values.ToArray ();
			_channels.Clear ();
		}
		foreach (Channel ch in channels) {
			ch.Closed = true;
			try { ch.CreditSignal.Release (); } catch { }
			try { ch.Remote?.Dispose (); } catch { }
			try { ch.RemoteClient?.Close (); } catch { }
			try { ch.CreditSignal.Dispose (); } catch { }
		}
	}

	private int NewId ()
	{
		lock (_gate) {
			return _nextId++;
		}
	}

	public async Task SendFrameAsync (int id, byte type, byte[] payload)
	{
		byte[] buf = new byte[5 + payload.Length];
		BitConverter.GetBytes (id).CopyTo (buf, 0);
		buf [4] = type;
		if (payload.Length != 0) {
			Buffer.BlockCopy (payload, 0, buf, 5, payload.Length);
		}
		await _sendRaw (buf);
	}

	private static byte[] EncodeOpen (string host, int port)
	{
		MemoryStream memoryStream = new MemoryStream ();
		Protocol.WriteString (memoryStream, host);
		Protocol.WriteInt32 (memoryStream, port);
		return memoryStream.ToArray ();
	}

	private static (string host, int port) DecodeOpen (byte[] p)
	{
		int offset = 0;
		string item = Protocol.ReadString (p, ref offset);
		int item2 = Protocol.ReadInt32 (p, offset);
		return (host: item, port: item2);
	}
}
