using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using System.Net.Http;

namespace desk;

internal sealed class RemoteDesktopServer : IDisposable
{
	private sealed class EncReq
	{
		public int W;

		public int H;

		public bool UseJpeg;

		public byte[] FullRaw;

		public List<(Rectangle Rect, byte[] Rgba)> Blocks;

		public bool JpegBlocks;

		public long Seq;
	}

	private readonly TcpListener _listener;

	private TcpClient? _client;

	private NetworkStream? _stream;

	private CancellationTokenSource? _cts;

	private Task? _acceptTask;

	/// <summary>Stop()/Dispose() 已发起。用于把 Stop 唤醒 Accept 时抛出的
	/// ObjectDisposedException / SocketException 判定为预期关闭，而不是故障。</summary>
	private volatile bool _stopping;

	private volatile bool _forceFull;

	private bool _jpegBlocksOk = true;

	private const int JpegDegradeThresholdMs = 400;

	private volatile bool _jpegEncoderHealthy;

	private long _jpegProbeNextAt;

	private volatile bool _jpegProbeRunning;

	private int _jpegProbeStreak;

	private const int JpegProbeBudgetMs = 60;

	private const int JpegProbeW = 320;

	private const int JpegProbeH = 180;

	private bool _rawFullFrame;

	private const int FullFrameDegradeMs = 300;

	private readonly object _encGate = new object ();

	private EncReq _encReq;

	private long _encSeq;

	private long _encEnqueued;

	private readonly object _frameQGate = new object ();

	private byte[]? _pendingFrame;

	private long _frameSeq;

	private long _statBytesWin;

	private long _statFramesWin;

	private int _statTicks;

	private volatile string _lastFrameKind = "-";

	private readonly SemaphoreSlim _writeLock = new SemaphoreSlim (1, 1);

	private readonly FileTransferManager _files;

	private readonly DxgiCapture _dxgi = new DxgiCapture ();

	private bool _dxgiTryInit = true;

	private bool _dxgiLogFirst = true;

	private long _dxgiEmptySince;

	private long _lastDxgiEmptyLog;

	private long _lastDxgiSendTick;

	private long _lastPushErrorLog;

	private long _lastInputErrorLog;

	private long _lastSlowLog;

	private long _lastSendSlowLog;

	private int _logSentInSec;

	private long _logSecStart;

	private volatile bool _logForwardBroken;

	public Func<int, string, long, bool>? FileOfferPrompt;

	private AudioCapture? _audio;

	private readonly PortForward _portForward;

	private const int WriteLockWaitMs = 3000;

	private const int VideoSendTimeoutMs = 3000;

	private int _lastCursorHash;

	private const int CursorAnimationRefreshMs = 600;

	private bool ForceRawFullFrame => Mode == Protocol.EncodingMode.RawRegions;

	public int SlowFrameMs { get; set; } = 150;

	public FileTransferManager Files => _files;

	public Protocol.EncodingMode Mode { get; set; } = Protocol.EncodingMode.JpegRegions;

	public long JpegQuality { get; set; } = 70L;

	public int MaxFrameWidth { get; set; } = 1920;

	public int TargetFps { get; set; } = 60;

	public int ResyncIntervalSec { get; set; } = 4;

	public int BlockSize { get; set; } = 32;

	public int MaxRegionBlocks { get; set; } = 40;

	public int RegionFullFramePercent { get; set; } = 30;

	public int MaxPayloadBytes { get; set; } = 1200000;

	public bool FollowInputDesktop { get; set; }

	public string? ServerPassword { get; set; }

	public PortForward PortForwarding => _portForward;

	public event Action<string>? Log;

	public event Action<int, long>? Stats;

	public event Action<Protocol.QualityConfig>? QualityChanged;

	public event Action<string>? ClipboardReceived;

	public event Action<ClipFormat, byte[]>? ClipboardDataReceived;

	public event Action<byte[]>? AudioFrameReady;

	public event Action? ViewerConnected;

	public event Action? ViewerDisconnected;

	private void EnqueueFrame (byte[] payload)
	{
		lock (_frameQGate) {
			_pendingFrame = payload;
			_frameSeq++;
		}
	}

	private async Task SendFramesAsync (NetworkStream stream, CancellationToken ct)
	{
		long sentBytes = 0L;
		long sentFrames = 0L;
		Stopwatch statSw = Stopwatch.StartNew ();
		try {
			while (!ct.IsCancellationRequested) {
				byte[] payload = null;
				lock (_frameQGate) {
					if (_pendingFrame != null) {
						payload = _pendingFrame;
						_pendingFrame = null;
						_ = _frameSeq;
					}
				}
				if (payload == null) {
					await Task.Delay (4, ct);
					continue;
				}
				Stopwatch swSend = Stopwatch.StartNew ();
				await SendLocked (Protocol.MessageType.VideoFrame, payload, 3000);
				long sendMs = swSend.ElapsedMilliseconds;
				sentBytes += payload.Length;
				sentFrames++;
				if (sendMs >= 1500) {
					long now = NetFx.TickCount64;
					if (now - _lastSendSlowLog > 2000) {
						_lastSendSlowLog = now;
						this.Log?.Invoke ($"[发送阻塞] 单帧写入耗时 {sendMs}ms（M端 未及时确认 / 网络拥塞 / 写锁被其他发送占用）——冻结在发送侧");
					}
				}
				if (statSw.ElapsedMilliseconds >= 1000) {
					long winMs = statSw.ElapsedMilliseconds;
					int fps = (int)((double)sentFrames * 1000.0 / (double)winMs);
					int bps = (int)(sentBytes * 1000 / winMs);
					this.Stats?.Invoke (fps, bps);
					_statBytesWin += sentBytes;
					_statFramesWin += sentFrames;
					sentBytes = 0L;
					sentFrames = 0L;
					statSw.Restart ();
					if (++_statTicks >= 5) {
						_statTicks = 0;
						long avgKB = ((_statFramesWin > 0) ? (_statBytesWin / _statFramesWin / 1024) : 0);
						this.Log?.Invoke ($"[统计] {fps}fps {bps / 1024}KB/s 平均{avgKB}KB/帧（近5秒{_statFramesWin}帧） | 模式{Mode} 质量{JpegQuality} {MaxFrameWidth}px/{TargetFps}fps | 整帧={(_rawFullFrame ? "Deflate原始" : "JPEG")} 区域块={(_jpegBlocksOk ? "JPEG" : "原始RGB")} | 最近帧：{_lastFrameKind}");
						_statBytesWin = 0L;
						_statFramesWin = 0L;
					}
				}
			}
		} catch (OperationCanceledException) {
		} catch (Exception ex2) {
			Exception ex3 = ex2;
			this.Log?.Invoke ("帧发送异常：" + ex3.Message);
		}
	}

	public RemoteDesktopServer (int port)
	{
		_listener = new TcpListener (IPAddress.Any, port);
		Log += delegate(string s) {
			AppLog.Write ("S端", s);
			if (!_logForwardBroken) {
				long tickCount = NetFx.TickCount64;
				if (tickCount - _logSecStart >= 1000) {
					_logSecStart = tickCount;
					_logSentInSec = 0;
				}
				if (_logSentInSec < 20) {
					_logSentInSec++;
					SendLogToViewer (s);
				}
			}
		};
		InputInjector.Log = delegate(string s) {
			this.Log?.Invoke (s);
		};
		_files = new FileTransferManager ((Protocol.MessageType t, byte[] p) => SendLocked (t, p));
		_files.Log = delegate(string s) {
			this.Log?.Invoke (s);
		};
		_portForward = new PortForward ((byte[] frame) => SendLocked (Protocol.MessageType.PortForward, frame)) {
			IsServer = true
		};
		_portForward.Log = delegate(string s) {
			this.Log?.Invoke (s);
		};
		_files.OnIncomingOffer = (int id, string name, long size) => FileOfferPrompt?.Invoke (id, name, size) ?? false;

		// Win7 默认就落在兼容档，避免网页画质设置到达前先用 1920/60 把 GDI 抓屏压垮。
		if (!Painter.Helpers.OsSupport.SupportsDesktopDuplication) {
			MaxFrameWidth = 1280;
			TargetFps = 12;
			JpegQuality = 60;
		}
	}

	public int Port {
		get {
			TcpListener tcpListener = _listener;
			if (tcpListener == null) {
				return 0;
			}
			return ((IPEndPoint)tcpListener.LocalEndpoint).Port;
		}
	}

	public void Start ()
	{
		_stopping = false;
		_listener.Start ();
		_cts = new CancellationTokenSource ();
		_acceptTask = Task.Run (async delegate {
			await AcceptLoopAsync (_cts.Token);
		});
		this.Log?.Invoke ($"主机已启动，监听端口 {((IPEndPoint)_listener.LocalEndpoint).Port}｜被控端构建 {AppLog.BuildStamp}");
	}

	public async Task StartViaRelayAsync (string relayHost, int relayPort, string token)
	{
		_cts = new CancellationTokenSource ();
		TcpClient client = new TcpClient ();
		try {
			await client.ConnectAsync (relayHost, relayPort);
			NetworkStream stream = client.GetStream ();
			byte[] tb = Encoding.UTF8.GetBytes (token);
			byte[] relayHandshake = new byte[1 + 4 + tb.Length];
			relayHandshake [0] = 72;
			Buffer.BlockCopy (BitConverter.GetBytes (tb.Length), 0, relayHandshake, 1, 4);
			Buffer.BlockCopy (tb, 0, relayHandshake, 5, tb.Length);
			await stream.WriteAsync (relayHandshake, 0, relayHandshake.Length, CancellationToken.None);
			this.Log?.Invoke ($"已连到中继服务器 {relayHost}:{relayPort}，令牌“{token}”，等待查看端…");
			CancellationToken ct = _cts.Token;
			_ = Task.Run (async delegate {
				await HandleClientAsync (client, ct);
			});
		} catch {
			// 握手失败时 client 尚未交给 HandleClientAsync 的 using(client) 接管，
			// 必须在这里释放，否则每次重试都泄漏一个半开 socket。
			client.Dispose ();
			throw;
		}
	}

	private async Task AcceptLoopAsync (CancellationToken ct)
	{
		while (!ct.IsCancellationRequested && !_stopping) {
			try {
				TcpClient client = await _listener.AcceptTcpClientAsync ();
				if (_client != null && _client.Connected) {
					this.Log?.Invoke ("已有查看端连接，拒绝新的连接");
					client.Close ();
					continue;
				}
				_client = client;
				try {
					client.NoDelay = true;
					client.SendBufferSize = 1048576;
					client.ReceiveBufferSize = 1048576;
				} catch {
				}
				Task.Run (async delegate {
					await HandleClientAsync (client, ct);
				});
			} catch (OperationCanceledException) {
				break;
			} catch (ObjectDisposedException) {
				// Stop() 靠 _listener.Stop() 释放底层 socket 来唤醒阻塞中的
				// AcceptTcpClientAsync，这是预期关闭路径，不是故障，也不该刷日志。
				break;
			} catch (System.Net.Sockets.SocketException) when (_stopping || ct.IsCancellationRequested) {
				break;
			} catch (Exception ex2) {
				this.Log?.Invoke ("接受连接异常：" + ex2.Message);
				// 原实现直接吞掉，crash.log 永远看不到；留一份限频现场。
				CrashDiagnostics.NoteHandled ("RemoteDesktopServer.AcceptLoop", ex2);
				// 监听持续失败时立即重试会形成高 CPU 空转并刷爆日志，退避 500ms。
				try { await Task.Delay (500, ct); } catch { break; }
			}
		}
	}

	private async Task HandleClientAsync (TcpClient client, CancellationToken ct)
	{
		using (client) {
			NetworkStream stream = client.GetStream ();
			_stream = stream;
			try {
				(Protocol.MessageType type, byte[] payload)? login = await Protocol.ReadMessageAsync (stream, ct);
				if (!login.HasValue || login.Value.type != Protocol.MessageType.LoginRequest) {
					this.Log?.Invoke ("未收到登录请求，断开");
					return;
				}
				string password = Encoding.UTF8.GetString (login.Value.payload);
				if (!CheckPassword (password)) {
					await Protocol.SendAsync (stream, Protocol.MessageType.LoginResponse, new byte[1].Concat (Encoding.UTF8.GetBytes ("密码错误")).ToArray ());
					this.Log?.Invoke ("密码错误，拒绝连接");
					return;
				}
				await Protocol.SendAsync (stream, Protocol.MessageType.LoginResponse, new byte[1] { 1 });
				_logForwardBroken = false;
				this.Log?.Invoke ($"查看端已连接：{client.Client.RemoteEndPoint}");
				this.ViewerConnected?.Invoke ();
				CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource (ct);
				try {
					SendLocked (Protocol.MessageType.SupportedEncoding, new byte[2] {
						(byte)EncAbility (),
						8
					});
					SendPermissions ();
					Task pushTask = Task.Run (async delegate {
						await PushFramesAsync (stream, linked.Token);
					});
					Task inputTask = Task.Run (async delegate {
						await ReceiveInputAsync (stream, linked.Token);
					});
					Task cursorTask = Task.Run (async delegate {
						await CursorLoopAsync (stream, linked.Token);
					});
					Task sendTask = Task.Run (async delegate {
						await SendFramesAsync (stream, linked.Token);
					});
					Task finished = await Task.WhenAny (pushTask, inputTask, cursorTask, sendTask);
					linked.Cancel ();
					this.Log?.Invoke ("连接子任务[" + ((finished == pushTask) ? "抓屏推送" : ((finished == inputTask) ? "输入接收" : ((finished == cursorTask) ? "光标同步" : "帧发送"))) + "]已结束，拆除本次连接");
					await Task.WhenAll (pushTask, inputTask, cursorTask, sendTask);
				} finally {
					if (linked != null) {
						((IDisposable)linked).Dispose ();
					}
				}
			} catch (OperationCanceledException) {
			} catch (Exception ex2) {
				Exception ex3 = ex2;
				this.Log?.Invoke ("连接处理异常：" + ex3.Message);
			} finally {
				_stream = null;
				_client = null;
				_forceFull = true;
				_audio?.Stop ();
				Features.DoBlockInput (on: false);
				Features.SetPrivacyMode (on: false);
				this.Log?.Invoke ("查看端已断开");
				this.ViewerDisconnected?.Invoke ();
			}
		}
	}

	private bool CheckPassword (string password)
	{
		string serverPassword = ServerPassword;
		if (string.IsNullOrEmpty (serverPassword)) {
			return true;
		}
		return password == serverPassword;
	}

	private async Task PushFramesAsync (NetworkStream stream, CancellationToken ct)
	{
		double interval = 1000.0 / (double)Math.Max (1, TargetFps);
		Task.Run (() => EncodeWorkerAsync (ct));
		Stopwatch sw = Stopwatch.StartNew ();
		int tick = 0;
		int lastW = 0;
		int lastH = 0;
		int ticksPerResync = Math.Max (1, ResyncIntervalSec * Math.Max (1, TargetFps));
		Bitmap capBmp = null;
		Bitmap scaledBmp = null;
		byte[] prevBytes = null;
		byte[] curBuf = null;
		byte[] prevBuf = null;
		int failCount = 0;
		try {
			while (!ct.IsCancellationRequested) {
				sw.Restart ();
				tick++;
				long tRaw = 0L;
				long tCmp = 0L;
				string frameKind = "整帧";
				string stage = "抓屏";
				try {
					if (FollowInputDesktop) {
						DesktopSwitcher.SyncToInputDesktop ();
					}
					Rectangle bounds = SystemInformation.VirtualScreen;
					if (_dxgiTryInit) {
						_dxgiTryInit = false;
						_dxgi.Log = delegate(string s) {
							this.Log?.Invoke (s);
						};
					}
					byte[] capPixels = null;
					int capW = 0;
					int capH = 0;
					bool dxgiGot = false;
					if (DxgiCapture.Enabled) {
						if (_dxgi.TryGetFrame (out capW, out capH)) {
							capPixels = _dxgi.Pixels;
							dxgiGot = true;
						}
						if (_dxgiLogFirst) {
							_dxgiLogFirst = false;
							this.Log?.Invoke (_dxgi.Available ? $"抓屏后端：DXGI Desktop Duplication（{_dxgi.Bounds.Width}x{_dxgi.Bounds.Height}），不再走 GDI 回读" : ("抓屏后端：GDI BitBlt（DXGI 不可用：" + _dxgi.LastError + "）"));
						}
					}
					byte[] curBytes;
					int w;
					int h;
					if (dxgiGot) {
						int dw = NetFx.Clamp (MaxFrameWidth, 160, 4096);
						int dh = (int)Math.Round ((double)capH * (double)dw / (double)capW);
						curBytes = ScreenCapture.ScaleRaw (capPixels, capW, capH, dw, dh, ref curBuf);
						w = dw;
						h = dh;
						_dxgiEmptySince = 0L;
						_lastDxgiSendTick = NetFx.TickCount64;
						goto IL_0894;
					}
					if (_dxgi.Available) {
						long nowMs = NetFx.TickCount64;
						if (nowMs - _lastDxgiSendTick >= 1000 && _dxgi.Pixels != null) {
							_lastDxgiSendTick = nowMs;
							int dw2 = NetFx.Clamp (MaxFrameWidth, 160, 4096);
							int dh2 = (int)Math.Round ((double)_dxgi.Bounds.Height * (double)dw2 / (double)_dxgi.Bounds.Width);
							byte[] hb = ScreenCapture.ScaleRaw (_dxgi.Pixels, _dxgi.Bounds.Width, _dxgi.Bounds.Height, dw2, dh2, ref curBuf);
							BuildFullOnWorker (hb, dw2, dh2);
							_lastFrameKind = "心跳整帧(桌面静止)";
							await DelayFrameAsync (interval, sw, ct);
							continue;
						}
						if (_dxgiEmptySince == 0) {
							_dxgiEmptySince = nowMs;
						} else if (nowMs - _dxgiEmptySince >= 1200 && nowMs - _lastDxgiEmptyLog >= 3000) {
							_lastDxgiEmptyLog = nowMs;
							this.Log?.Invoke ($"[抓屏] DXGI 已 {(double)(nowMs - _dxgiEmptySince) / 1000.0:F1}s 没有新帧（累计空帧 {_dxgi.EmptyFrames}）：源端桌面没有变化，不是抓屏卡住。若出现在右键/打开文件夹期间，即 explorer 被 shell 扩展阻塞。");
						}
						await DelayFrameAsync (interval, sw, ct);
						continue;
					}
					// Win7 没有 WDA_EXCLUDEFROMCAPTURE，隐私黑窗会被 GDI BitBlt 抓进画面，
					// 远控端会跟着变黑。此时冻结在开启隐私前的最后一帧（不发新帧），
					// 效果接近 Win10「黑窗被排除捕获、远控端仍看到桌面」；Win8+ 有 DXGI，
					// 完全不走这里，实时画面路径不变。
					if (desk.Features.PrivacyOn && !Painter.Helpers.OsSupport.SupportsDesktopDuplication) {
						await DelayFrameAsync (interval, sw, ct);
						continue;
					}
					capBmp = ScreenCapture.CaptureVirtualScreen (bounds, capBmp);
					scaledBmp = ScreenCapture.ScaleToWidth (capBmp, NetFx.Clamp (MaxFrameWidth, 160, 4096), scaledBmp);
					byte[] rb = ScreenCapture.GetRaw32 (scaledBmp, ref curBuf).bytes;
					curBytes = rb;
					w = scaledBmp.Width;
					h = scaledBmp.Height;
					goto IL_0894;
					IL_0894:
					long tCap = sw.ElapsedMilliseconds;
					stage = "缩放";
					long tScale = sw.ElapsedMilliseconds;
					byte[] payload;
					byte[] array;
					if (Mode == Protocol.EncodingMode.JpegFullFrame) {
						BuildFullOnWorker (curBytes, w, h);
						payload = null;
						lastW = w;
						lastH = h;
						goto IL_105e;
					}
					stage = "取像素";
					tRaw = sw.ElapsedMilliseconds;
					if (_forceFull || prevBytes == null || w != lastW || h != lastH || tick % ticksPerResync == 0) {
						_forceFull = false;
						BuildFullOnWorker (curBytes, w, h);
						payload = null;
						prevBytes = curBytes;
						byte[] array2 = curBuf;
						curBuf = prevBuf;
						prevBuf = array2;
						lastW = w;
						lastH = h;
						goto IL_105e;
					}
					List<Rectangle> rawBlocks = ScreenCapture.CompareBlocks (prevBytes, curBytes, w, h, BlockSize);
					tCmp = sw.ElapsedMilliseconds;
					prevBytes = curBytes;
					array = curBuf;
					curBuf = prevBuf;
					prevBuf = array;
					if (rawBlocks.Count != 0) {
						List<Rectangle> blocks = ScreenCapture.MergeBlocks (rawBlocks, BlockSize, w, h);
						_ = blocks.Count;
						long changed2 = 0L;
						foreach (Rectangle b in blocks) {
							changed2 += (long)b.Width * (long)b.Height;
						}
						int areaPct = (int)(changed2 * 100 / Math.Max (1L, (long)w * (long)h));
						bool tooMany = blocks.Count > MaxRegionBlocks;
						bool tooLarge = changed2 * 100 >= (long)w * (long)h * RegionFullFramePercent;
						if (tooMany || tooLarge) {
							BuildFullOnWorker (curBytes, w, h);
							payload = null;
							frameKind = $"整帧(块{blocks.Count}>{MaxRegionBlocks} 或 面积{areaPct}%>={RegionFullFramePercent}%)";
						} else {
							KickJpegProbe ();
							bool jpegBlocks = _jpegBlocksOk && _jpegEncoderHealthy;
							PostRegion (curBytes, blocks, w, h, jpegBlocks);
							payload = null;
							frameKind = $"区域{blocks.Count}块/{areaPct}%{(jpegBlocks ? "" : "(原始块)")}";
						}
						goto IL_105e;
					}
					await DelayFrameAsync (interval, sw, ct);
					goto end_IL_01b9;
					IL_105e:
					stage = "入队";
					_lastFrameKind = frameKind;
					if (payload != null) {
						EnqueueFrame (payload);
					}
					long tSend = sw.ElapsedMilliseconds;
					long cost = sw.ElapsedMilliseconds;
					if (cost >= SlowFrameMs) {
						long now = NetFx.TickCount64;
						if (now - _lastSlowLog > 1500) {
							_lastSlowLog = now;
							(uint, uint) tuple = ScreenCapture.GuiResourceCounts ();
							uint gdiCnt = tuple.Item1;
							uint userCnt = tuple.Item2;
							Action<string>? action = this.Log;
							if (action != null) {
								byte[] array3 = payload;
								// net462 没有 System.Runtime.CompilerServices.DefaultInterpolatedStringHandler，
								// 还原成普通字符串插值（插值语义与原 handler 完全一致）
								string suffix;
								if (gdiCnt > 8000u || userCnt > 8000u) {
									suffix = "（句柄偏高，逼近 10000 上限，GDI+ 会变慢）";
								} else if (!DxgiCapture.Enabled || _dxgi.Banned) {
									suffix = "（已回退 GDI 抓屏）";
								} else {
									suffix = "";
								}
								action ($"[慢帧 {cost}ms] 抓屏{tCap} 缩放{tScale - tCap} 取像素{tRaw - tScale} 组帧+发送{tSend - Math.Max (tRaw, tCmp)} 其中发送{cost - tSend} | {frameKind} | {((array3 != null) ? array3.Length : 0) / 1024}KB {w}x{h} 模式{Mode} 质量{JpegQuality} | GDI对象{gdiCnt} USER对象{userCnt}{suffix}");
							}
						}
					}
					goto IL_160f;
					end_IL_01b9:;
				} catch (OperationCanceledException) {
					break;
				} catch (Exception ex2) {
					_forceFull = true;
					int num = failCount + 1;
					failCount = num;
					if (num >= 3) {
						failCount = 0;
						try {
							capBmp?.Dispose ();
						} catch {
						}
						try {
							scaledBmp?.Dispose ();
						} catch {
						}
						capBmp = null;
						scaledBmp = null;
						prevBytes = null;
						curBuf = null;
						prevBuf = null;
						this.Log?.Invoke ("抓屏位图已重建（此前连续失败，疑似 GDI+ 资源异常）");
					}
					long now2 = NetFx.TickCount64;
					if (now2 - _lastPushErrorLog > 5000) {
						_lastPushErrorLog = now2;
						var (gdiCnt2, userCnt2) = ScreenCapture.GuiResourceCounts ();
						this.Log?.Invoke ($"推送帧异常（将自动重试）[卡在：{stage}]：{ex2.GetType ().Name} {ex2.Message} | GDI对象{gdiCnt2} USER对象{userCnt2}");
					}
					try {
						await Task.Delay (300, ct);
					} catch (OperationCanceledException) {
						break;
					}
					goto IL_160f;
				}
				continue;
				IL_160f:
				await DelayFrameAsync (interval, sw, ct);
			}
		} finally {
			try {
				capBmp?.Dispose ();
			} catch {
			}
			try {
				scaledBmp?.Dispose ();
			} catch {
			}
		}
	}

	private static async Task DelayFrameAsync (double interval, Stopwatch sw, CancellationToken ct)
	{
		long remain = (long)Math.Round (interval - sw.Elapsed.TotalMilliseconds);
		if (remain > 0) {
			await Task.Delay ((int)Math.Min (remain, 1000L), ct);
		}
	}

	private byte[] BuildFullFrame (Bitmap scaled)
	{
		if (_rawFullFrame) {
			(byte[] bytes, int stride, int width, int height) raw = ScreenCapture.GetRaw32 (scaled);
			byte[] item = raw.bytes;
			int item2 = raw.stride;
			int item3 = raw.width;
			int item4 = raw.height;
			byte[] array = ScreenCapture.DeflateRaw (item, Math.Abs (item2) * item4);
			MemoryStream memoryStream = new MemoryStream (array.Length + 32);
			memoryStream.WriteByte (0);
			memoryStream.WriteByte (2);
			Protocol.WriteInt32 (memoryStream, item3);
			Protocol.WriteInt32 (memoryStream, item4);
			Protocol.WriteInt32 (memoryStream, array.Length);
			memoryStream.Write (array, 0, array.Length);
			return memoryStream.ToArray ();
		}
		byte[] array2 = ScreenCapture.EncodeJpeg (scaled, JpegQuality);
		MemoryStream memoryStream2 = new MemoryStream ();
		memoryStream2.WriteByte (0);
		memoryStream2.WriteByte (0);
		Protocol.WriteInt32 (memoryStream2, scaled.Width);
		Protocol.WriteInt32 (memoryStream2, scaled.Height);
		Protocol.WriteInt32 (memoryStream2, array2.Length);
		memoryStream2.Write (array2, 0, array2.Length);
		return memoryStream2.ToArray ();
	}

	private byte[] BuildFullFrameChecked (Bitmap scaled, Stopwatch sw, long tick)
	{
		if (ForceRawFullFrame) {
			_rawFullFrame = true;
			return BuildFullFrame (scaled);
		}
		KickJpegProbe ();
		if (!_jpegEncoderHealthy) {
			_rawFullFrame = true;
			return BuildFullFrame (scaled);
		}
		long elapsedMilliseconds = sw.ElapsedMilliseconds;
		byte[] result = BuildFullFrame (scaled);
		long num = sw.ElapsedMilliseconds - elapsedMilliseconds;
		if (num >= 300) {
			_rawFullFrame = true;
			_jpegEncoderHealthy = false;
			_jpegProbeNextAt = 0L;
			this.Log?.Invoke ($"[编码降级] 整帧 JPEG 编码耗时 {num}ms（GDI+/WIC 被系统图形锁阻塞），已切到 Deflate 原始像素整帧（带宽上升）；是否恢复 JPEG 交由后台探测决定。");
		} else {
			_rawFullFrame = false;
		}
		return result;
	}

	private void KickJpegProbe ()
	{
		if (_jpegProbeRunning) {
			return;
		}
		long tickCount = NetFx.TickCount64;
		if (tickCount < _jpegProbeNextAt) {
			return;
		}
		_jpegProbeNextAt = tickCount + (_jpegEncoderHealthy ? 15000 : 5000);
		_jpegProbeRunning = true;
		Task.Run (delegate {
			bool flag = false;
			try {
				Task<long> task = Task.Run (delegate {
					int num = 320;
					int num2 = 180;
					byte[] array = new byte[num * num2 * 4];
					for (int i = 0; i < num2; i++) {
						for (int j = 0; j < num; j++) {
							int num3 = (i * num + j) * 4;
							bool flag4 = j >= 10 && j < num - 10 && i >= 10 && i < num2 - 10;
							byte b;
							array [num3 + 2] = (b = (byte)(flag4 ? 176 : 105));
							array [num3] = b;
							array [num3 + 1] = (byte)(flag4 ? 202 : 105);
							array [num3 + 3] = byte.MaxValue;
						}
					}
					Stopwatch stopwatch = Stopwatch.StartNew ();
					ScreenCapture.EncodeJpegRaw (array, num, num2, JpegQuality);
					return stopwatch.ElapsedMilliseconds;
				});
				flag = task.Wait (60) && task.Result < 60;
				if (!flag) {
					_jpegProbeNextAt = NetFx.TickCount64 + 20000;
				}
			} catch {
				flag = false;
			} finally {
				_jpegProbeStreak = (flag ? (_jpegProbeStreak + 1) : 0);
				bool flag2 = _jpegProbeStreak >= 2;
				bool flag3 = flag2 != _jpegEncoderHealthy;
				_jpegEncoderHealthy = flag2;
				_jpegProbeRunning = false;
				if (flag3) {
					this.Log?.Invoke (flag ? "[编码] 后台探测：JPEG 编码器已恢复，整帧/区域块将重新使用 JPEG。" : "[编码] 后台探测：JPEG 编码器仍被系统图形锁阻塞，继续使用 Deflate 原始像素。");
				}
			}
		});
	}

	private async Task EncodeWorkerAsync (CancellationToken ct)
	{
		while (!ct.IsCancellationRequested) {
			EncReq req;
			lock (_encGate) {
				req = _encReq;
				_encReq = null;
			}
			if (req == null) {
				await Task.Delay (2, ct);
				continue;
			}
			Task.Run (delegate {
				EncodeOne (req, ct);
			}, ct);
		}
	}

	private void EncodeOne (EncReq req, CancellationToken ct)
	{
		if (ct.IsCancellationRequested) {
			return;
		}
		try {
			if (req.Blocks == null) {
				Stopwatch stopwatch = Stopwatch.StartNew ();
				byte[] frame = ((!req.UseJpeg) ? ScreenCapture.DeflateRaw (req.FullRaw, req.FullRaw.Length) : ScreenCapture.EncodeJpegRaw (req.FullRaw, req.W, req.H, JpegQuality));
				long elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
				if (req.UseJpeg && elapsedMilliseconds >= 300) {
					_jpegEncoderHealthy = false;
					_jpegProbeNextAt = 0L;
					this.Log?.Invoke ($"[编码降级] 整帧 JPEG 编码耗时 {elapsedMilliseconds}ms（GDI+/WIC 被系统图形锁阻塞），已切到 Deflate 原始像素整帧；是否恢复 JPEG 交由后台探测决定。");
				}
				EnqueueIfLatest (req, BuildFullPayload (frame, req.UseJpeg, req.W, req.H));
				return;
			}
			Stopwatch stopwatch2 = Stopwatch.StartNew ();
			MemoryStream memoryStream = new MemoryStream ();
			byte value = (byte)(1 | (req.JpegBlocks ? 2 : 0));
			memoryStream.WriteByte (value);
			memoryStream.WriteByte (0);
			Protocol.WriteInt32 (memoryStream, req.W);
			Protocol.WriteInt32 (memoryStream, req.H);
			Protocol.WriteInt32 (memoryStream, req.Blocks.Count);
			foreach (var block in req.Blocks) {
				Protocol.WriteInt32 (memoryStream, block.Rect.X);
				Protocol.WriteInt32 (memoryStream, block.Rect.Y);
				Protocol.WriteInt32 (memoryStream, block.Rect.Width);
				Protocol.WriteInt32 (memoryStream, block.Rect.Height);
				byte[] array = ((!req.JpegBlocks) ? block.Rgba : ScreenCapture.EncodeJpegRaw (block.Rgba, block.Rect.Width, block.Rect.Height, JpegQuality));
				Protocol.WriteInt32 (memoryStream, array.Length);
				memoryStream.Write (array, 0, array.Length);
			}
			byte[] array2 = memoryStream.ToArray ();
			long elapsedMilliseconds2 = stopwatch2.ElapsedMilliseconds;
			if (req.JpegBlocks && elapsedMilliseconds2 >= 400) {
				_jpegBlocksOk = false;
				_jpegEncoderHealthy = false;
				_jpegProbeNextAt = 0L;
				this.Log?.Invoke ($"[编码降级] 分块 JPEG 编码耗时 {elapsedMilliseconds2}ms（GDI+ 被系统图形锁阻塞），已切换为原始 RGBA 块；是否恢复 JPEG 交由后台探测决定，不再原地重试。");
			}
			if (array2.Length > MaxPayloadBytes && req.FullRaw != null) {
				byte[] frame2 = ((!req.JpegBlocks) ? ScreenCapture.DeflateRaw (req.FullRaw, req.FullRaw.Length) : ScreenCapture.EncodeJpegRaw (req.FullRaw, req.W, req.H, JpegQuality));
				EnqueueIfLatest (req, BuildFullPayload (frame2, req.JpegBlocks, req.W, req.H));
			} else {
				EnqueueIfLatest (req, array2);
			}
		} catch (Exception ex) {
			this.Log?.Invoke ("[编码] 工作线程异常（已忽略，继续）：" + ex.GetType ().Name + " " + ex.Message);
		}
	}

	private void EnqueueIfLatest (EncReq req, byte[] payload)
	{
		long num = Interlocked.Read (ref _encEnqueued);
		if (req.Seq > num && Interlocked.CompareExchange (ref _encEnqueued, req.Seq, num) == num) {
			EnqueueFrame (payload);
		}
	}

	private void BuildFullOnWorker (byte[] raw, int w, int h)
	{
		if (ForceRawFullFrame) {
			PostFull (raw, w, h, useJpeg: false);
			return;
		}
		KickJpegProbe ();
		PostFull (raw, w, h, _jpegEncoderHealthy);
	}

	private void PostFull (byte[] raw, int w, int h, bool useJpeg)
	{
		EncReq encReq = new EncReq {
			W = w,
			H = h,
			UseJpeg = useJpeg,
			FullRaw = raw
		};
		lock (_encGate) {
			encReq.Seq = ++_encSeq;
			_encReq = encReq;
		}
	}

	private void PostRegion (byte[] curBytes, List<Rectangle> blocks, int w, int h, bool jpegBlocks)
	{
		byte[] array = new byte[curBytes.Length];
		Buffer.BlockCopy (curBytes, 0, array, 0, curBytes.Length);
		List<(Rectangle, byte[])> list = new List<(Rectangle, byte[])> (blocks.Count);
		int num = w * 4;
		foreach (Rectangle block in blocks) {
			byte[] array2 = new byte[block.Width * block.Height * 4];
			for (int i = 0; i < block.Height; i++) {
				int srcOffset = (block.Y + i) * num + block.X * 4;
				Buffer.BlockCopy (curBytes, srcOffset, array2, i * block.Width * 4, block.Width * 4);
			}
			list.Add ((block, array2));
		}
		EncReq encReq = new EncReq {
			W = w,
			H = h,
			JpegBlocks = jpegBlocks,
			FullRaw = array,
			Blocks = list
		};
		lock (_encGate) {
			encReq.Seq = ++_encSeq;
			_encReq = encReq;
		}
	}

	private static byte[] BuildFullPayload (byte[] frame, bool useJpeg, int w, int h)
	{
		MemoryStream memoryStream = new MemoryStream ();
		memoryStream.WriteByte (0);
		memoryStream.WriteByte ((byte)((!useJpeg) ? 2 : 0));
		Protocol.WriteInt32 (memoryStream, w);
		Protocol.WriteInt32 (memoryStream, h);
		Protocol.WriteInt32 (memoryStream, frame.Length);
		memoryStream.Write (frame, 0, frame.Length);
		return memoryStream.ToArray ();
	}

	private static Bitmap BitmapFromRaw (byte[] raw, int w, int h)
	{
		// 校验源长度与位图容量，防止超长数据越界写 GDI 缓冲区（AccessViolation 无法被 catch 拦住）。
		long need = (long)w * h * 4;
		Bitmap bitmap = new Bitmap (w, h, PixelFormat.Format32bppArgb);
		BitmapData bitmapData = bitmap.LockBits (new Rectangle (0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
		try {
			if (need <= int.MaxValue && raw.Length <= need) {
				Marshal.Copy (raw, 0, bitmapData.Scan0, raw.Length);
			}
		} finally {
			bitmap.UnlockBits (bitmapData);
		}
		return bitmap;
	}

	private byte[] BuildRegionFrame (List<Rectangle> blocks, byte[] curBytes, int w, int h, bool jpegBlocks, Bitmap scaled)
	{
		MemoryStream memoryStream = new MemoryStream ();
		byte value = (byte)(1 | (jpegBlocks ? 2 : 0));
		memoryStream.WriteByte (value);
		memoryStream.WriteByte (0);
		Protocol.WriteInt32 (memoryStream, w);
		Protocol.WriteInt32 (memoryStream, h);
		Protocol.WriteInt32 (memoryStream, blocks.Count);
		int num = w * 4;
		foreach (Rectangle block in blocks) {
			Protocol.WriteInt32 (memoryStream, block.X);
			Protocol.WriteInt32 (memoryStream, block.Y);
			Protocol.WriteInt32 (memoryStream, block.Width);
			Protocol.WriteInt32 (memoryStream, block.Height);
			byte[] array;
			if (jpegBlocks) {
				using Bitmap bitmap = new Bitmap (block.Width, block.Height, PixelFormat.Format32bppArgb);
				using (Graphics graphics = Graphics.FromImage (bitmap)) {
					graphics.DrawImage (scaled, new Rectangle (0, 0, block.Width, block.Height), block, GraphicsUnit.Pixel);
				}
				array = ScreenCapture.EncodeJpeg (bitmap, JpegQuality);
			} else {
				array = new byte[block.Width * block.Height * 4];
				for (int i = 0; i < block.Height; i++) {
					int srcOffset = (block.Y + i) * num + block.X * 4;
					Buffer.BlockCopy (curBytes, srcOffset, array, i * block.Width * 4, block.Width * 4);
				}
			}
			Protocol.WriteInt32 (memoryStream, array.Length);
			memoryStream.Write (array, 0, array.Length);
		}
		return memoryStream.ToArray ();
	}

	private async Task ReceiveInputAsync (NetworkStream stream, CancellationToken ct)
	{
		while (!ct.IsCancellationRequested) {
			(Protocol.MessageType type, byte[] payload)? msg = await Protocol.ReadMessageAsync (stream, ct);
			if (!msg.HasValue) {
				this.Log?.Invoke ("对端正常关闭连接（FIN），本次会话结束");
				break;
			}
			try {
				switch (msg.Value.type) {
				case Protocol.MessageType.MouseEvent:
					HandleMouse (msg.Value.payload);
					break;
				case Protocol.MessageType.KeyEvent:
					HandleKey (msg.Value.payload);
					break;
				case Protocol.MessageType.UnicodeText:
					HandleUnicodeText (msg.Value.payload);
					break;
				case Protocol.MessageType.ClipboardText:
					if (msg.Value.payload.Length > 1) {
						int off = 1;
						string text = Protocol.ReadString (msg.Value.payload, ref off);
						// Web 端没有 ViewerForm 消费 ClipboardReceived 事件，这里直接把远端文本
						// 写入被控端本机剪贴板（桌面客户端链路也受益），受 Clipboard 权限门控。
						if (Features.IsAllowed (Protocol.Permission.Clipboard)) {
							try { WinClipboard.Write (ClipFormat.Text, Encoding.UTF8.GetBytes (text)); } catch { }
						}
						this.ClipboardReceived?.Invoke (text);
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
					HandleFileControlAsync (msg.Value.payload, ct);
					break;
				case Protocol.MessageType.FileData:
					HandleFileDataAsync (msg.Value.payload, ct);
					break;
				case Protocol.MessageType.QualitySettings:
					ApplyRemoteQuality (msg.Value.payload);
					break;
				case Protocol.MessageType.Ping:
					SendLocked (Protocol.MessageType.Pong, msg.Value.payload);
					break;
				case Protocol.MessageType.ForceKeyFrame:
					_forceFull = true;
					break;
				case Protocol.MessageType.PermissionInfo:
					if (msg.Value.payload.Length >= 2) {
						Features.Set ((Protocol.Permission)msg.Value.payload [0], msg.Value.payload [1] != 0);
					}
					SendPermissions ();
					break;
				case Protocol.MessageType.ControlAction:
					if (msg.Value.payload.Length >= 1) {
						HandleControlAction ((Protocol.ControlActionId)msg.Value.payload [0]);
					}
					break;
				case Protocol.MessageType.PrivacyMode: {
					bool on2 = msg.Value.payload.Length >= 1 && msg.Value.payload [0] != 0;
					bool ok = Features.IsAllowed (Protocol.Permission.PrivacyMode) && Features.SetPrivacyMode (on2);
					this.Log?.Invoke (ok ? ("隐私模式已" + (on2 ? "开启" : "关闭")) : "隐私模式切换失败（可能未授权）");
					MemoryStream r = new MemoryStream ();
					r.WriteByte ((byte)(on2 ? 1u : 0u));
					r.WriteByte ((byte)(ok ? 1u : 0u));
					await SendLocked (Protocol.MessageType.PrivacyMode, r.ToArray ());
					break;
				}
				case Protocol.MessageType.TestDelay:
					SendLocked (Protocol.MessageType.TestDelay, msg.Value.payload);
					break;
				case Protocol.MessageType.AudioToggle: {
					bool on = msg.Value.payload.Length >= 1 && msg.Value.payload [0] != 0;
					SetAudio (on);
					break;
				}
				case Protocol.MessageType.PortForward:
					await _portForward.OnFrameAsync (msg.Value.payload);
					break;
				case Protocol.MessageType.CliprdrFiles:
					if (msg.Value.payload.Length >= 4) {
						_files.ExpectClipboardFiles (Protocol.ReadInt32 (msg.Value.payload, 0));
					}
					break;
				case Protocol.MessageType.SupportedDecoding:
					if (msg.Value.payload.Length >= 2) {
						this.Log?.Invoke ($"控制端解码能力：{msg.Value.payload [0]}，优先 {msg.Value.payload [1]}");
					}
					break;
				case Protocol.MessageType.Disconnect:
					return;
				}
			} catch (Exception ex) {
				long now = NetFx.TickCount64;
				if (now - _lastInputErrorLog > 5000) {
					_lastInputErrorLog = now;
					this.Log?.Invoke ("处理输入异常（已跳过）：" + ex.Message);
				}
			}
		}
	}

	private void SendLogToViewer (string text)
	{
		try {
			MemoryStream memoryStream = new MemoryStream ();
			Protocol.WriteString (memoryStream, text);
			SendLocked (Protocol.MessageType.LogLine, memoryStream.ToArray ());
		} catch {
		}
	}

	/// <summary>是否允许本实例采集 / 发送声音。Web 控制台不需要声音，创建时置 false 即可彻底不采集。</summary>
	public bool AudioEnabled { get; set; } = true;

	public void SetAudio (bool on)
	{
		// 本实例已禁用声音（例如 Web 控制台）：无论控制端怎么请求都不采集、不发送。
		if (!AudioEnabled) {
			_audio?.Stop ();
			if (on) this.Log?.Invoke ("本端已禁用声音传输（Web 控制台不需要音频）。");
			return;
		}
		if (on && !Features.IsAllowed (Protocol.Permission.Audio)) {
			this.Log?.Invoke ("控制端请求声音，但本端未授权音频。");
		} else if (on) {
			if (_audio == null) {
				_audio = new AudioCapture ();
				_audio.Log = delegate(string s) {
					this.Log?.Invoke (s);
				};
				_audio.OnFormat = delegate {
					MemoryStream memoryStream = new MemoryStream ();
					Protocol.WriteInt32 (memoryStream, 48000);
					memoryStream.WriteByte (2);
					SendLocked (Protocol.MessageType.AudioFormat, memoryStream.ToArray ());
				};
				_audio.OnFrame = delegate(byte[] d) {
					MemoryStream memoryStream = new MemoryStream ();
					Protocol.WriteInt32 (memoryStream, d.Length);
					memoryStream.Write (d, 0, d.Length);
					SendLocked (Protocol.MessageType.AudioFrame, memoryStream.ToArray ());
					this.AudioFrameReady?.Invoke (d);
				};
			}
			_audio.Start ();
		} else {
			_audio?.Stop ();
		}
	}

	public Task SendClipboardDataAsync (ClipFormat fmt, byte[] data)
	{
		MemoryStream memoryStream = new MemoryStream ();
		memoryStream.WriteByte ((byte)fmt);
		Protocol.WriteInt32 (memoryStream, data.Length);
		memoryStream.Write (data, 0, data.Length);
		return SendLocked (Protocol.MessageType.ClipboardData, memoryStream.ToArray ());
	}

	public async Task SendClipboardAsync (string text)
	{
		MemoryStream ms = new MemoryStream ();
		ms.WriteByte (1);
		Protocol.WriteString (ms, text);
		await SendLocked (Protocol.MessageType.ClipboardText, ms.ToArray ());
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

	private async Task SendLocked (Protocol.MessageType type, byte[] payload, int timeoutMs = 20000)
	{
		if (!(await _writeLock.WaitAsync (3000))) {
			// 剪贴板/画质/权限等消息都走这里，静默 return 会让调用方以为已发送成功，
			// 弱网拥塞时再也查不出为什么功能失效。
			this.Log?.Invoke ($"发送跳过（{type}，{payload.Length}字节）：写锁等待 3 秒超时，可能网络拥塞或对端不再收数据");
			return;
		}
		Exception fail = null;
		try {
			if (_stream != null) {
				await Protocol.SendAsync (_stream, type, payload, _cts.Token, timeoutMs);
			}
		} catch (Exception ex) {
			Exception ex2 = ex;
			fail = ex2;
		} finally {
			_writeLock.Release ();
		}
		if (fail != null) {
			SocketException sock = (fail as SocketException) ?? (fail.InnerException as SocketException);
			string why = ((sock != null) ? ("SocketError=" + sock.SocketErrorCode) : ((fail is OperationCanceledException) ? $"写超时(>{timeoutMs / 1000}s，对端不再收数据)" : "未知"));
			this.Log?.Invoke ($"发送失败（{type}，{payload.Length}字节）：{fail.GetType ().Name} {why} {fail.Message}");
			if (type == Protocol.MessageType.LogLine) {
				_logForwardBroken = true;
			}
			try {
				_stream?.Close ();
			} catch {
			}
			_stream = null;
		}
	}

	private Task HandleFileControlAsync (byte[] p, CancellationToken ct)
	{
		return _files.OnControlAsync (p);
	}

	private Task HandleFileDataAsync (byte[] p, CancellationToken ct)
	{
		return _files.OnDataAsync (p);
	}

	/// <summary>
	/// Win7 只有 GDI BitBlt 抓屏（约 250ms/帧），如果照搬网页端默认的 1920px/60fps，
	/// 抓屏线程会被压垮，表现就是「屏幕共享卡住 / 画面不动 / 鼠标输入延迟」。
	/// 这里按系统能力自动降档：宁可帧率低也要保证画面持续刷新、输入不堆积。
	/// Win8+ 有 DXGI 桌面复制，原样返回，不受影响。
	/// </summary>
	private Protocol.QualityConfig ClampQualityForOs (Protocol.QualityConfig q)
	{
		if (Painter.Helpers.OsSupport.SupportsDesktopDuplication) return q;
		int width = NetFx.Clamp (q.MaxWidth, 160, 1280);
		int fps = NetFx.Clamp (q.Fps, 1, 12);
		int quality = NetFx.Clamp (q.Quality, 1, 60);
		if (width == q.MaxWidth && fps == q.Fps && quality == q.Quality) return q;
		return new Protocol.QualityConfig (q.Mode, quality, width, fps, q.ResyncSec, q.BlockSize);
	}

	private void ApplyRemoteQuality (byte[] payload)
	{
		Protocol.QualityConfig obj = ClampQualityForOs (Protocol.QualityConfig.Decode (payload));
		bool flag = obj.MaxWidth != MaxFrameWidth;
		Mode = obj.Mode;
		JpegQuality = obj.Quality;
		MaxFrameWidth = obj.MaxWidth;
		TargetFps = obj.Fps;
		ResyncIntervalSec = obj.ResyncSec;
		BlockSize = obj.BlockSize;
		_forceFull = true;
		this.Log?.Invoke ($"主控端已调整画质：{obj.MaxWidth}px / 质量{obj.Quality}% / {obj.Fps}fps / 模式{obj.Mode}" + (flag ? "（分辨率变化，下一帧重建缓冲）" : "") + (Painter.Helpers.OsSupport.SupportsDesktopDuplication ? "" : "（Win7 兼容模式：已自动降帧降分辨率）"));
		this.QualityChanged?.Invoke (obj);
	}

	/// <summary>当前生效的画质参数快照。</summary>
	public Protocol.QualityConfig CurrentQuality => new Protocol.QualityConfig (Mode, (int)JpegQuality, MaxFrameWidth, TargetFps, ResyncIntervalSec, BlockSize);

	/// <summary>是否有查看端（主控端 / Web 桥接）在线。无人在线时本端不抓屏。</summary>
	public bool HasViewer {
		get {
			TcpClient? client = _client;
			return client != null && client.Connected && _stream != null;
		}
	}

	/// <summary>
	/// 被控端本地调整画质：立即在本端生效，并把新参数同步给已连接的查看端
	/// （主控端桌面客户端 / Web 桥接），使两端显示一致。
	/// </summary>
	public void SetQualityLocal (Protocol.QualityConfig q)
	{
		q = ClampQualityForOs (q); // Win7 强制低帧低分辨率，避免 GDI 抓屏卡死
		bool widthChanged = q.MaxWidth != MaxFrameWidth;
		Mode = q.Mode;
		JpegQuality = NetFx.Clamp (q.Quality, 1, 100);
		MaxFrameWidth = NetFx.Clamp (q.MaxWidth, 160, 4096);
		TargetFps = NetFx.Clamp (q.Fps, 1, 60);
		ResyncIntervalSec = NetFx.Clamp (q.ResyncSec, 1, 10);
		BlockSize = NetFx.Clamp (q.BlockSize, 8, 128);
		_forceFull = true;
		Protocol.QualityConfig currentQuality = CurrentQuality;
		this.Log?.Invoke ($"本端已调整画质：{currentQuality.MaxWidth}px / 质量{currentQuality.Quality}% / {currentQuality.Fps}fps / 模式{currentQuality.Mode}" + (widthChanged ? "（分辨率变化，下一帧重建缓冲）" : "") + (Painter.Helpers.OsSupport.SupportsDesktopDuplication ? "" : "（Win7 兼容模式：已自动降帧降分辨率）"));
		this.QualityChanged?.Invoke (currentQuality);
		if (_stream != null) {
			_ = SendLocked (Protocol.MessageType.QualitySettings, currentQuality.Encode ());
		}
	}

	private void SendPermissions ()
	{
		Protocol.Permission[] values = (Protocol.Permission[])Enum.GetValues (typeof (Protocol.Permission));
		foreach (Protocol.Permission permission in values) {
			if (permission != 0) {
				SendLocked (Protocol.MessageType.PermissionInfo, new byte[2] {
					(byte)permission,
					(byte)(Features.IsAllowed (permission) ? 1u : 0u)
				});
			}
		}
	}

	public void NotifyPermissionsChanged ()
	{
		SendPermissions ();
	}

	private void HandleControlAction (Protocol.ControlActionId action)
	{
		switch (action) {
		case Protocol.ControlActionId.CtrlAltDel: {
			bool flag = Features.DoCtrlAltDel ();
			this.Log?.Invoke (flag ? "已发送 Ctrl+Alt+Del" : "发送 Ctrl+Alt+Del 失败（需 SYSTEM 权限，可安装 UAC 支持）");
			break;
		}
		case Protocol.ControlActionId.LockScreen: {
			bool flag = Features.DoLockScreen ();
			this.Log?.Invoke (flag ? "已锁定屏幕" : "锁屏失败");
			break;
		}
		case Protocol.ControlActionId.BlockInputOn: {
			bool flag = Features.IsAllowed (Protocol.Permission.BlockInput) && Features.DoBlockInput (on: true);
			this.Log?.Invoke (flag ? "已屏蔽本机输入" : "屏蔽输入失败（可能未授权）");
			break;
		}
		case Protocol.ControlActionId.BlockInputOff:
			Features.DoBlockInput (on: false);
			this.Log?.Invoke ("已恢复本机输入");
			break;
		case Protocol.ControlActionId.RemoteRestart: {
			bool flag = Features.IsAllowed (Protocol.Permission.Restart) && Features.DoRestart ();
			this.Log?.Invoke (flag ? "收到远程重启指令，2 秒后重启" : "远程重启被拒绝（未授权）");
			break;
		}
		case Protocol.ControlActionId.ReadClipboard: {
			// Web 端「取回远端剪贴板」：必须 STA 线程读，交给 ClipboardSync 那套无法直接调用，
			// 这里起一个临时 STA 线程读取并回发 type=7（短操作，随即退出）。
			if (!Features.IsAllowed (Protocol.Permission.Clipboard)) break;
			try {
				Thread t = new Thread (delegate () {
					try {
						(ClipFormat fmt, byte[] data)? clip = WinClipboard.Read ();
						if (clip.HasValue && clip.Value.fmt == ClipFormat.Text && clip.Value.data.Length > 0) {
							string text = Encoding.UTF8.GetString (clip.Value.data);
							_ = SendClipboardAsync (text);
						}
					} catch {
					}
				});
				t.SetApartmentState (ApartmentState.STA);
				t.IsBackground = true;
				t.Start ();
			} catch {
			}
			break;
		}
		}
	}

	private static Protocol.CodecAbility EncAbility ()
	{
		return Protocol.CodecAbility.None;
	}

	private async Task CursorLoopAsync (NetworkStream stream, CancellationToken ct)
	{
		nint lastHandle = IntPtr.Zero;
		long lastCaptureTick = 0L;
		Point? lastPos = null;
		while (!ct.IsCancellationRequested) {
			try {
				Point? pos = Features.CursorPos ();
				if (pos.HasValue && (!lastPos.HasValue || lastPos.Value != pos.Value)) {
					lastPos = pos;
					Rectangle b = SystemInformation.VirtualScreen;
					int nx = (int)NetFx.Clamp (Math.Round ((double)(pos.Value.X - b.Left) / (double)Math.Max (1, b.Width) * 65535.0), 0.0, 65535.0);
					int ny = (int)NetFx.Clamp (Math.Round ((double)(pos.Value.Y - b.Top) / (double)Math.Max (1, b.Height) * 65535.0), 0.0, 65535.0);
					MemoryStream ms = new MemoryStream (4);
					ms.WriteByte ((byte)(nx & 0xFF));
					ms.WriteByte ((byte)(nx >> 8));
					ms.WriteByte ((byte)(ny & 0xFF));
					ms.WriteByte ((byte)(ny >> 8));
					await SendLocked (Protocol.MessageType.CursorPosition, ms.ToArray ());
				}
				nint handle = ScreenCapture.GetCursorHandle ();
				bool shapeChanged = handle != lastHandle;
				bool refreshDue = NetFx.TickCount64 - lastCaptureTick >= 600;
				if (shapeChanged || refreshDue) {
					lastHandle = handle;
					lastCaptureTick = NetFx.TickCount64;
					int hotX;
					int hotY;
					Bitmap bmp = Features.CaptureCursor (out hotX, out hotY);
					if (bmp != null) {
						using (bmp) {
							int hash = BitmapHash (bmp);
							if (hash != _lastCursorHash) {
								_lastCursorHash = hash;
								byte[] data = ScreenCapture.GetRaw32 (bmp).bytes;
								MemoryStream ms2 = new MemoryStream ();
								Protocol.WriteInt32 (ms2, hash);
								ms2.WriteByte ((byte)hotX);
								ms2.WriteByte ((byte)hotY);
								Protocol.WriteInt32 (ms2, bmp.Width);
								Protocol.WriteInt32 (ms2, bmp.Height);
								ms2.Write (data, 0, data.Length);
								await SendLocked (Protocol.MessageType.CursorData, ms2.ToArray ());
							}
						}
					}
				}
			} catch {
			}
			try {
				await Task.Delay (150, ct);
			} catch (OperationCanceledException) {
				break;
			}
		}
	}

	private static int BitmapHash (Bitmap bmp)
	{
		try {
			byte[] item = ScreenCapture.GetRaw32 (bmp).bytes;
			int num = 17;
			for (int i = 0; i < item.Length; i += 97) {
				num = num * 31 + item [i];
			}
			return num;
		} catch {
			return 0;
		}
	}

	private static void HandleMouse (byte[] p)
	{
		if (p.Length < 8) {
			return;
		}
		byte b = p [0];
		ushort xNorm = (ushort)(p [1] | (p [2] << 8));
		ushort yNorm = (ushort)(p [3] | (p [4] << 8));
		byte button = p [5];
		short delta = (short)(p [6] | (p [7] << 8));
		switch (b) {
		case 0:
			if (Features.IsAllowed (Protocol.Permission.Keyboard)) {
				InputInjector.MoveAbsolute (xNorm, yNorm);
			}
			break;
		case 1:
			if (Features.IsAllowed (Protocol.Permission.Keyboard)) {
				// 用事件自带的坐标先定位再按键：按钮消息自包含，
				// 不再依赖「前一条 move 消息」是否按时到达，避免画错位置/卡住。
				InputInjector.MoveAbsolute (xNorm, yNorm);
				InputInjector.ButtonDown (button);
			}
			break;
		case 2:
			if (Features.IsAllowed (Protocol.Permission.Keyboard)) {
				InputInjector.MoveAbsolute (xNorm, yNorm);
				InputInjector.ButtonUp (button);
			}
			break;
		case 3:
			if (Features.IsAllowed (Protocol.Permission.Keyboard)) {
				InputInjector.MoveAbsolute (xNorm, yNorm);
				InputInjector.Wheel (delta);
			}
			break;
		case 4:
			// 相对移动要在 p[6..7] 和 p[8..9] 各取一个 short，共需 10 字节；
			// 入口只校验了 8 字节，长度 8/9 的包会让 ToInt16(p,8) 越界抛异常。
			if (p.Length < 10) {
				return;
			}
			if (Features.IsAllowed (Protocol.Permission.Keyboard)) {
				InputInjector.MoveRelative (BitConverter.ToInt16 (p, 6), BitConverter.ToInt16 (p, 8));
			}
			break;
		}
	}

	// 浏览器用 beforeinput / IME 拿到的文本走这里注入（UTF-16LE 码元序列）。
	// 与 HandleKey 一样必须在最外层做权限检查，否则关闭键盘权限后仍能输入文字。
	private static void HandleUnicodeText (byte[] p)
	{
		if (!Features.IsAllowed (Protocol.Permission.Keyboard)) return;
		if (p == null || p.Length < 2 || p.Length % 2 != 0) return;
		try {
			InputInjector.Unicode (Encoding.Unicode.GetString (p));
		} catch {
		}
	}

	private static void HandleKey (byte[] p)
	{
		// 权限检查必须在最外层：旧实现把「短包兼容」和「无权限」合并进同一个 else，
		// 关闭键鼠控制后仍会走 InputInjector.Key 注入键盘，等于权限开关失效。
		if (!Features.IsAllowed (Protocol.Permission.Keyboard)) return;
		if (p.Length >= 5) {
			int num = BitConverter.ToInt32 (p, 0);
			bool down = p [4] != 0;
			if (p.Length >= 8) {
				InputInjector.KeyWithLockSync (num, down, p [5] != 0, p [6] != 0, p [7] != 0);
			} else {
				InputInjector.Key ((ushort)num, down);
			}
		}
	}

	public void Stop ()
	{
		// 必须先置位：AcceptLoopAsync 被 Stop 唤醒而抛异常时据此判定为预期关闭。
		_stopping = true;
		try {
			_cts?.Cancel ();
		} catch {
		}
		try {
			_listener.Stop ();
		} catch {
		}
		try {
			_client?.Close ();
		} catch {
		}
	}

	public void Dispose ()
	{
		Stop ();
		try {
			_dxgi?.Dispose ();
		} catch {
		}
		try { _portForward?.Dispose (); } catch { } // 释放端口转发监听与通道
		_cts?.Dispose ();
	}
}
