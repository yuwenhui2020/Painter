using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using System.Linq;
using System.Net.Http;
using System.Windows.Forms;

namespace desk;

internal sealed class RemoteDesktopClient : IDisposable
{
	private TcpClient? _client;

	private NetworkStream? _stream;

	private CancellationTokenSource? _cts;

	private Task? _recvTask;

	private readonly SemaphoreSlim _writeLock = new SemaphoreSlim (1, 1);

	private FileTransferManager _files = null;

	private PortForward _pf = null;

	public Func<int, string, long, bool>? FileOfferPrompt;

	private Task? _pingTask;

	private int _pingSeq;

	private long _lastRecvErrorLog;

	private byte[]? _backing;

	private int _backingW;

	private int _backingH;

	private volatile int _pendingFrames;

	private long _statRecvFrames;

	private long _statDropBusy;

	private long _statDropBackpressure;

	private long _statStaleBacking;

	private long _statDecodeMs;

	private long _statDecodeCount;

	private readonly SemaphoreSlim _decodeGate = new SemaphoreSlim (1, 1);

	private long _lastKeyFrameReq;

	public FileTransferManager Files => _files;

	public PortForward PortForwarding => _pf;

	public object FrameLock { get; } = new object ();

	public event Action<string>? Log;

	public event Action<int, int, byte[]>? FrameReceived;

	public event Action? Disconnected;

	public event Action<long>? RttMeasured;

	public event Action<string>? ClipboardReceived;

	public event Action<ClipFormat, byte[]>? ClipboardDataReceived;

	public event Action<int, int>? AudioFormatReceived;

	public event Action<byte[]>? AudioFrameReceived;

	public event Action<Protocol.Permission, bool>? PermissionChanged;

	public event Action<Protocol.QualityConfig>? QualityReceived;

	public event Action<Bitmap, int, int>? CursorDataReceived;

	public event Action<Point>? CursorPosReceived;

	public event Action<bool, bool>? PrivacyStateReceived;

	public event Action<long>? DelayProbeReply;

	public event Action<Protocol.CodecAbility, Protocol.CodecAbility>? EncodingAbilityReceived;

	public RemoteDesktopClient ()
	{
		Log += delegate(string s) {
			AppLog.Write ("M端", s);
		};
		_files = new FileTransferManager (SendLocked);
		_files.Log = delegate(string s) {
			this.Log?.Invoke (s);
		};
		_pf = new PortForward ((byte[] frame) => SendLocked (Protocol.MessageType.PortForward, frame)) {
			IsServer = false
		};
		_pf.Log = delegate(string s) {
			this.Log?.Invoke (s);
		};
	}

	public async Task<bool> ConnectAsync (string host, int port, string password)
	{
		ResetConnection ();
		TcpClient client = new TcpClient ();
		TuneSocket (client);
		await client.ConnectAsync (host, port);
		NetworkStream stream = client.GetStream ();
		_client = client;
		_stream = stream;
		return await EstablishAsync (host, port, password, stream);
	}

	public async Task<bool> ConnectViaRelayAsync (string relayHost, int relayPort, string token, string password)
	{
		ResetConnection ();
		TcpClient client = new TcpClient ();
		TuneSocket (client);
		await client.ConnectAsync (relayHost, relayPort);
		NetworkStream stream = client.GetStream ();
		_client = client;
		_stream = stream;
		MemoryStream hs = new MemoryStream ();
		hs.WriteByte (86);
		byte[] tb = Encoding.UTF8.GetBytes (token);
		hs.Write (BitConverter.GetBytes (tb.Length), 0, 4);
		hs.Write (tb, 0, tb.Length);
		byte[] relayHandshake = hs.ToArray ();
		await stream.WriteAsync (relayHandshake, 0, relayHandshake.Length, CancellationToken.None);
		return await EstablishAsync (relayHost, relayPort, password, stream);
	}

	private async Task<bool> EstablishAsync (string host, int port, string password, NetworkStream stream)
	{
		_files.OnIncomingOffer = (int id, string name, long size) => FileOfferPrompt?.Invoke (id, name, size) ?? false;
		await Protocol.SendAsync (stream, Protocol.MessageType.LoginRequest, Encoding.UTF8.GetBytes (password));
		(Protocol.MessageType type, byte[] payload)? resp = await Protocol.ReadMessageAsync (stream, CancellationToken.None);
		if (!resp.HasValue || resp.Value.type != Protocol.MessageType.LoginResponse) {
			throw new Exception ("未收到登录响应");
		}
		if (resp.Value.payload.Length == 0 || resp.Value.payload [0] != 1) {
			string reason = ((resp.Value.payload.Length > 1) ? Encoding.UTF8.GetString (resp.Value.payload, 1, resp.Value.payload.Length - 1) : "登录失败");
			throw new Exception (reason);
		}
		_cts = new CancellationTokenSource ();
		CancellationToken ct = _cts.Token;
		_recvTask = Task.Run (() => RecvLoopAsync (stream, ct));
		_pingTask = Task.Run (() => PingLoopAsync (stream, ct));
		this.Log?.Invoke ($"已连接到 {host}:{port}｜主控端构建 {AppLog.BuildStamp}");
		return true;
	}

	private static void TuneSocket (TcpClient c)
	{
		try {
			c.NoDelay = true;
			c.SendBufferSize = 1048576;
			c.ReceiveBufferSize = 1048576;
		} catch {
		}
	}

	private async Task PingLoopAsync (NetworkStream stream, CancellationToken ct)
	{
		while (!ct.IsCancellationRequested) {
			try {
				byte[] p = new byte[9];
				BitConverter.GetBytes (NetFx.TickCount64).CopyTo (p, 0);
				p [8] = (byte)(_pingSeq++ & 0xFF);
				await SendLockedCore (stream, Protocol.MessageType.Ping, p);
			} catch {
			}
			try {
				await Task.Delay (1000, ct);
			} catch (OperationCanceledException) {
				break;
			}
		}
	}

	private async Task RecvLoopAsync (NetworkStream stream, CancellationToken ct)
	{
		try {
			while (!ct.IsCancellationRequested) {
				(Protocol.MessageType type, byte[] payload)? msg = await Protocol.ReadMessageAsync (stream, ct);
				if (!msg.HasValue) {
					break;
				}
				try {
					switch (msg.Value.type) {
					case Protocol.MessageType.VideoFrame:
						EnqueueIncomingFrame (msg.Value.payload);
						break;
					case Protocol.MessageType.ClipboardText:
						if (msg.Value.payload.Length > 1) {
							int off2 = 1;
							this.ClipboardReceived?.Invoke (Protocol.ReadString (msg.Value.payload, ref off2));
						}
						break;
					case Protocol.MessageType.ClipboardData:
						if (msg.Value.payload.Length >= 5) {
							ClipFormat fmt = (ClipFormat)msg.Value.payload [0];
							int len = Protocol.ReadInt32 (msg.Value.payload, 1);
							if (len > 0 && 5 + len <= msg.Value.payload.Length) {
								byte[] data = new byte[len];
								Buffer.BlockCopy (msg.Value.payload, 5, data, 0, len);
								this.ClipboardDataReceived?.Invoke (fmt, data);
							}
						}
						break;
					case Protocol.MessageType.FileControl:
						HandleFileControlAsync (msg.Value.payload);
						break;
					case Protocol.MessageType.FileData:
						HandleFileDataAsync (msg.Value.payload);
						break;
					case Protocol.MessageType.Pong:
						if (msg.Value.payload.Length >= 8) {
							long sent = BitConverter.ToInt64 (msg.Value.payload, 0);
							this.RttMeasured?.Invoke (NetFx.TickCount64 - sent);
						}
						break;
					case Protocol.MessageType.PermissionInfo:
						if (msg.Value.payload.Length >= 2) {
							this.PermissionChanged?.Invoke ((Protocol.Permission)msg.Value.payload [0], msg.Value.payload [1] != 0);
						}
						break;
					case Protocol.MessageType.QualitySettings:
						this.QualityReceived?.Invoke (Protocol.QualityConfig.Decode (msg.Value.payload));
						break;
					case Protocol.MessageType.CursorPosition:
						if (msg.Value.payload.Length >= 4) {
							this.CursorPosReceived?.Invoke (new Point (msg.Value.payload [0] | (msg.Value.payload [1] << 8), msg.Value.payload [2] | (msg.Value.payload [3] << 8)));
						}
						break;
					case Protocol.MessageType.CursorData:
						if (msg.Value.payload.Length >= 14) {
							int hotX = msg.Value.payload [4];
							int hotY = msg.Value.payload [5];
							int w = Protocol.ReadInt32 (msg.Value.payload, 6);
							int h = Protocol.ReadInt32 (msg.Value.payload, 10);
							// 尺寸来自网络：先按合理范围与乘法溢出校验，再计算字节数，
							// 否则 w*h*4 溢出成小正数会绕过校验并巨量分配/越界拷贝。
							if (w > 0 && h > 0 && w <= 256 && h <= 256) {
								long needLong = (long)w * h * 4;
								if (needLong <= int.MaxValue && msg.Value.payload.Length >= 14L + needLong) {
									int need = (int)needLong;
									Bitmap bmp = new Bitmap (w, h, PixelFormat.Format32bppArgb);
									BitmapData bd = bmp.LockBits (new Rectangle (0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
									Marshal.Copy (msg.Value.payload, 14, bd.Scan0, need);
									bmp.UnlockBits (bd);
									this.CursorDataReceived?.Invoke (bmp, hotX, hotY);
								}
							}
						}
						break;
					case Protocol.MessageType.PrivacyMode:
						if (msg.Value.payload.Length >= 2) {
							this.PrivacyStateReceived?.Invoke (msg.Value.payload [0] != 0, msg.Value.payload [1] != 0);
						}
						break;
					case Protocol.MessageType.TestDelay:
						if (msg.Value.payload.Length >= 9 && msg.Value.payload [8] == 1) {
							this.DelayProbeReply?.Invoke (NetFx.TickCount64 - BitConverter.ToInt64 (msg.Value.payload, 0));
						}
						break;
					case Protocol.MessageType.AudioFormat:
						if (msg.Value.payload.Length >= 5) {
							this.AudioFormatReceived?.Invoke (Protocol.ReadInt32 (msg.Value.payload, 0), msg.Value.payload [4]);
						}
						break;
					case Protocol.MessageType.AudioFrame: {
						int len2 = Protocol.ReadInt32 (msg.Value.payload, 0);
						if (len2 > 0 && 4 + len2 <= msg.Value.payload.Length) {
							byte[] d = new byte[len2];
							Buffer.BlockCopy (msg.Value.payload, 4, d, 0, len2);
							this.AudioFrameReceived?.Invoke (d);
						}
						break;
					}
					case Protocol.MessageType.PortForward:
						await _pf.OnFrameAsync (msg.Value.payload);
						break;
					case Protocol.MessageType.CliprdrFiles:
						if (msg.Value.payload.Length >= 4) {
							Files.ExpectClipboardFiles (Protocol.ReadInt32 (msg.Value.payload, 0));
						}
						break;
					case Protocol.MessageType.SupportedEncoding:
						if (msg.Value.payload.Length >= 2) {
							this.EncodingAbilityReceived?.Invoke ((Protocol.CodecAbility)msg.Value.payload [0], (Protocol.CodecAbility)msg.Value.payload [1]);
						}
						break;
					case Protocol.MessageType.LogLine: {
						int off = 0;
						string text = Protocol.ReadString (msg.Value.payload, ref off);
						AppLog.Write ("S端", text);
						this.Log?.Invoke (text);
						break;
					}
					}
				} catch (Exception ex) {
					long now = NetFx.TickCount64;
					if (now - _lastRecvErrorLog > 5000) {
						_lastRecvErrorLog = now;
						this.Log?.Invoke ("处理消息异常（已跳过）：" + ex.Message);
					}
				}
			}
		} catch (OperationCanceledException) {
		} catch (Exception ex3) {
			Exception ex4 = ex3;
			this.Log?.Invoke ("接收异常（连接中断）：" + ex4.GetType ().Name + " " + ex4.Message);
		} finally {
			if (_stream == stream) {
				this.Log?.Invoke ("与主机断开");
				this.Disconnected?.Invoke ();
			}
		}
	}

	public string TakeFrameStats (long windowMs)
	{
		long num = Interlocked.Exchange (ref _statRecvFrames, 0L);
		long value = Interlocked.Exchange (ref _statDropBusy, 0L);
		long value2 = Interlocked.Exchange (ref _statDropBackpressure, 0L);
		long value3 = Interlocked.Exchange (ref _statStaleBacking, 0L);
		long num2 = Interlocked.Exchange (ref _statDecodeMs, 0L);
		long num3 = Interlocked.Exchange (ref _statDecodeCount, 0L);
		double num4 = Math.Max (0.001, (double)windowMs / 1000.0);
		return $"收{num}帧({(double)num / num4:F1}fps) 解码均{((num3 > 0) ? (num2 / num3) : 0)}ms | 丢(解码忙){value} 丢(绘制背压){value2} 尺寸不符{value3} | 在途{Volatile.Read (ref _pendingFrames)}";
	}

	private void EnqueueIncomingFrame (byte[] payload)
	{
		if (!_decodeGate.Wait (0)) {
			Interlocked.Increment (ref _statDropBusy);
			long tickCount = NetFx.TickCount64;
			if (tickCount - _lastKeyFrameReq > 1000) {
				_lastKeyFrameReq = tickCount;
				RequestKeyFrameAsync ();
			}
			return;
		}
		Task.Run (delegate {
			try {
				HandleFrame (payload);
			} finally {
				_decodeGate.Release ();
			}
		});
	}

	private void HandleFrame (byte[] p)
	{
		long tickCount = NetFx.TickCount64;
		bool flag = false;
		try {
			lock (FrameLock) {
				flag = HandleFrameCore (p);
			}
		} catch (Exception ex) {
			this.Log?.Invoke ("解码帧异常：" + ex.Message);
		} finally {
			Interlocked.Add (ref _statDecodeMs, NetFx.TickCount64 - tickCount);
			Interlocked.Increment (ref _statDecodeCount);
			if (!flag) {
				ReleasePending ();
			}
		}
	}

	private void ReleasePending ()
	{
		int num;
		do {
			num = Volatile.Read (ref _pendingFrames);
		} while (num > 0 && Interlocked.CompareExchange (ref _pendingFrames, num - 1, num) != num);
	}

	private bool HandleFrameCore (byte[] p)
	{
		if (Volatile.Read (ref _pendingFrames) > 2 && p.Length >= 10 && p [0] == 0) {
			Interlocked.Increment (ref _statDropBackpressure);
			return false;
		}
		Interlocked.Increment (ref _pendingFrames);
		Interlocked.Increment (ref _statRecvFrames);
		if (p.Length < 14) {
			return false;
		}
		byte b = p [0];
		byte b2 = p [1];
		int num = Protocol.ReadInt32 (p, 2);
		int num2 = Protocol.ReadInt32 (p, 6);
		switch (b2) {
		case 2: {
			int num13 = Protocol.ReadInt32 (p, 10);
			if (14 + num13 > p.Length) {
				return false;
			}
			byte[] array7 = ScreenCapture.InflateRaw (p, 14, num13, num * num2 * 4);
			if (array7 == null) {
				return false;
			}
			_backing = array7;
			_backingW = num;
			_backingH = num2;
			this.FrameReceived?.Invoke (num, num2, array7);
			return this.FrameReceived != null;
		}
		default: {
			if ((b & 1) == 0) {
				int num3 = Protocol.ReadInt32 (p, 10);
				if (14 + num3 > p.Length) {
					return false;
				}
				byte[] array = new byte[num3];
				Buffer.BlockCopy (p, 14, array, 0, num3);
				byte[] array2 = ScreenCapture.DecodeJpegToBytes (array);
				if (array2 == null) {
					return false;
				}
				_backing = array2;
				_backingW = num;
				_backingH = num2;
				this.FrameReceived?.Invoke (num, num2, array2);
				return this.FrameReceived != null;
			}
			if (_backing == null || _backingW != num || _backingH != num2) {
				Interlocked.Increment (ref _statStaleBacking);
				return false;
			}
			int num4 = 10;
			int num5 = Protocol.ReadInt32 (p, num4);
			num4 += 4;
			bool flag = (b & 2) != 0;
			int num6 = num * 4;
			for (int i = 0; i < num5; i++) {
				int num7 = Protocol.ReadInt32 (p, num4);
				num4 += 4;
				int num8 = Protocol.ReadInt32 (p, num4);
				num4 += 4;
				int num9 = Protocol.ReadInt32 (p, num4);
				num4 += 4;
				int num10 = Protocol.ReadInt32 (p, num4);
				num4 += 4;
				int num11 = Protocol.ReadInt32 (p, num4);
				num4 += 4;
				if (num4 + num11 > p.Length || num9 <= 0 || num10 <= 0 || num11 < num9 * num10 * 4) {
					break;
				}
				if (flag) {
					byte[] array3 = new byte[num11];
					Buffer.BlockCopy (p, num4, array3, 0, num11);
					num4 += num11;
					byte[] array4 = ScreenCapture.DecodeJpegToBytes (array3);
					if (array4 != null) {
						for (int j = 0; j < num10; j++) {
							Buffer.BlockCopy (array4, j * num9 * 4, _backing, ((num8 + j) * _backingW + num7) * 4, num9 * 4);
						}
					}
				} else {
					byte[] array5 = new byte[num11];
					Buffer.BlockCopy (p, num4, array5, 0, num11);
					num4 += num11;
					for (int k = 0; k < num10; k++) {
						Buffer.BlockCopy (array5, k * num9 * 4, _backing, ((num8 + k) * _backingW + num7) * 4, num9 * 4);
					}
				}
			}
			this.FrameReceived?.Invoke (num, num2, _backing);
			return this.FrameReceived != null;
		}
		}
	}

	public void SendMouseMove (ushort xNorm, ushort yNorm)
	{
		SendMouse (0, xNorm, yNorm, 0, 0);
	}

	public void SendMouseDown (byte button, ushort xNorm, ushort yNorm)
	{
		SendMouse (1, xNorm, yNorm, button, 0);
	}

	public void SendMouseUp (byte button, ushort xNorm, ushort yNorm)
	{
		SendMouse (2, xNorm, yNorm, button, 0);
	}

	public void SendMouseWheel (short delta, ushort xNorm, ushort yNorm)
	{
		SendMouse (3, xNorm, yNorm, 0, delta);
	}

	private void SendMouse (byte type, ushort xNorm, ushort yNorm, byte button, short wheel)
	{
		SendLocked (Protocol.MessageType.MouseEvent, new byte[8] {
			type,
			(byte)(xNorm & 0xFF),
			(byte)(xNorm >> 8),
			(byte)(yNorm & 0xFF),
			(byte)(yNorm >> 8),
			button,
			(byte)(wheel & 0xFF),
			(byte)(wheel >> 8)
		});
	}

	public void SendKey (int vk, bool down)
	{
		byte[] array = new byte[8];
		BitConverter.GetBytes (vk).CopyTo (array, 0);
		array [4] = (byte)(down ? 1 : 0);
		array [5] = (byte)(InputInjector.GetLockState (20) ? 1u : 0u);
		array [6] = (byte)(InputInjector.GetLockState (144) ? 1u : 0u);
		array [7] = (byte)(InputInjector.GetLockState (145) ? 1u : 0u);
		SendLocked (Protocol.MessageType.KeyEvent, array);
	}

	public Task SendQualityAsync (Protocol.QualityConfig q)
	{
		return SendLocked (Protocol.MessageType.QualitySettings, q.Encode ());
	}

	public void MarkFrameConsumed ()
	{
		ReleasePending ();
	}

	public Task RequestKeyFrameAsync ()
	{
		return SendLocked (Protocol.MessageType.ForceKeyFrame, Array.Empty<byte> ());
	}

	public void SendMouseRelative (int dx, int dy)
	{
		byte[] array = new byte[10] { 4, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		BitConverter.GetBytes ((short)NetFx.Clamp (dx, -32768, 32767)).CopyTo (array, 6);
		BitConverter.GetBytes ((short)NetFx.Clamp (dy, -32768, 32767)).CopyTo (array, 8);
		SendLocked (Protocol.MessageType.MouseEvent, array);
	}

	public Task SendControlActionAsync (Protocol.ControlActionId action)
	{
		return SendLocked (Protocol.MessageType.ControlAction, new byte[1] { (byte)action });
	}

	public Task SendPrivacyModeAsync (bool on)
	{
		return SendLocked (Protocol.MessageType.PrivacyMode, new byte[1] { (byte)(on ? 1u : 0u) });
	}

	public Task SendPermissionAsync (Protocol.Permission p, bool on)
	{
		return SendLocked (Protocol.MessageType.PermissionInfo, new byte[2] {
			(byte)p,
			(byte)(on ? 1u : 0u)
		});
	}

	public Task SendTestDelayAsync (long timeMs, long lastDelay, int targetBitrate)
	{
		byte[] array = new byte[17];
		BitConverter.GetBytes (timeMs).CopyTo (array, 0);
		array [8] = 1;
		BitConverter.GetBytes ((int)lastDelay).CopyTo (array, 9);
		BitConverter.GetBytes (targetBitrate).CopyTo (array, 13);
		return SendLocked (Protocol.MessageType.TestDelay, array);
	}

	public Task SetRemoteAudioAsync (bool on)
	{
		return SendLocked (Protocol.MessageType.AudioToggle, new byte[1] { (byte)(on ? 1u : 0u) });
	}

	public Task SendSupportedDecodingAsync (Protocol.CodecAbility ability, Protocol.CodecAbility prefer)
	{
		return SendLocked (Protocol.MessageType.SupportedDecoding, new byte[2] {
			(byte)ability,
			(byte)prefer
		});
	}

	public async Task SendClipboardAsync (string text)
	{
		MemoryStream ms = new MemoryStream ();
		ms.WriteByte (1);
		Protocol.WriteString (ms, text);
		await SendLocked (Protocol.MessageType.ClipboardText, ms.ToArray ());
	}

	public Task SendClipboardDataAsync (ClipFormat fmt, byte[] data)
	{
		MemoryStream memoryStream = new MemoryStream ();
		memoryStream.WriteByte ((byte)fmt);
		Protocol.WriteInt32 (memoryStream, data.Length);
		memoryStream.Write (data, 0, data.Length);
		return SendLocked (Protocol.MessageType.ClipboardData, memoryStream.ToArray ());
	}

	public async Task SendFileAsync (string path)
	{
		await _files.SendFileAsync (path);
	}

	public async Task SendFolderAsync (string dir)
	{
		await _files.SendFolderAsync (dir);
	}

	public async Task SendClipboardFilesAsync (IEnumerable<string> paths)
	{
		await _files.SendClipboardFilesAsync (paths);
	}

	private Task HandleFileControlAsync (byte[] p)
	{
		return _files.OnControlAsync (p);
	}

	private Task HandleFileDataAsync (byte[] p)
	{
		return _files.OnDataAsync (p);
	}

	private Task SendLocked (Protocol.MessageType type, byte[] payload)
	{
		return SendLockedCore (_stream, type, payload);
	}

	private async Task SendLockedCore (NetworkStream? stream, Protocol.MessageType type, byte[] payload)
	{
		if (stream == null || !(await _writeLock.WaitAsync (3000))) {
			return;
		}
		bool fatal = false;
		try {
			if (type == Protocol.MessageType.TestDelay && payload.Length >= 8) {
				BitConverter.GetBytes (NetFx.TickCount64).CopyTo (payload, 0);
			}
			await Protocol.SendAsync (stream, type, payload);
		} catch {
			fatal = true;
		} finally {
			_writeLock.Release ();
		}
		if (fatal) {
			Disconnect ();
		}
	}

	public void Disconnect ()
	{
		try {
			_cts?.Cancel ();
		} catch {
		}
		try {
			_client?.Close ();
		} catch {
		}
	}

	private void ResetConnection ()
	{
		Disconnect ();
		_client = null;
		_stream = null;
		_cts = null;
		_recvTask = null;
		_pingTask = null;
	}

	public void Dispose ()
	{
		Disconnect ();
		lock (FrameLock) {
			_backing = null;
		}
		try { _pf?.Dispose (); } catch { } // 释放端口转发监听与通道，避免端口被永久占用
		_cts?.Dispose ();
	}
}
