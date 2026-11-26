using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using Painter.Helpers;

namespace desk;

internal sealed class DxgiCapture : IDisposable
{
	private sealed class Out
	{
		public IDXGIOutputDuplication Dup = null;

		public ID3D11Texture2D Staging = null;

		public bool SystemMemory;

		public Rectangle Rect;

		public int Ox;

		public int Oy;

		public int Wp;

		public int Hp;

		public byte[] Row = Array.Empty<byte> ();
	}

	private const uint AcquireTimeoutMs = 20u;

	private const uint DxgiResourcePriorityMaximum = 3355443200u;

	private long _nextRetryAt;

	private int _reinitFails;

	private long _lastLogTick;

	private Mutex? _dxgiMutex;

	private ID3D11Device? _device;

	private ID3D11DeviceContext? _context;

	private byte[]? _pixels;

	private int _w;

	private int _h;

	private Rectangle _union;

	private readonly List<Out> _outputs = new List<Out> ();

	public static bool Enabled { get; set; } = true;

	public bool Available { get; private set; }

	public string? LastError { get; private set; }

	public Action<string>? Log { get; set; }

	public byte[]? Pixels => _pixels;

	public Rectangle Bounds => _union;

	public long Frames { get; private set; }

	public long EmptyFrames { get; private set; }

	private int _exceptStreak;

	public bool Banned { get; private set; }

	public bool TryGetFrame (out int w, out int h)
	{
		w = 0;
		h = 0;
		if (!Enabled || Banned) {
			return false;
		}
		// Win7 没有 DXGI Desktop Duplication：直接停用，让调用方走 GDI BitBlt，
		// 避免每 800ms 反复尝试初始化、刷错误日志。
		if (!OsSupport.SupportsDesktopDuplication) {
			Banned = true;
			Available = false;
			LastError = "Windows 7 不支持 DXGI 桌面复制，已使用 GDI 抓屏";
			Status ("[抓屏] " + LastError);
			return false;
		}
		try {
		if (_outputs.Count == 0) {
			long tickCount = NetFx.TickCount64;
			if (tickCount < _nextRetryAt) {
				return false;
			}
			_nextRetryAt = tickCount + 800;
			bool available = Available;
			TryInit ();
			if (_outputs.Count == 0) {
				if (++_reinitFails >= 3) {
					Available = false;
					Status ($"[抓屏] DXGI 连续 {_reinitFails} 次重建失败，回退 GDI BitBlt：{LastError}");
				} else if (available) {
					Status ($"[抓屏] DXGI 重建失败（第 {_reinitFails}/3 次），本帧跳过、不回退 GDI：{LastError}");
				}
				return false;
			}
			_reinitFails = 0;
		}
		bool flag = false;
		bool flag2 = false;
		int num = _w * 4;
		for (int i = 0; i < _outputs.Count; i++) {
			Out obj = _outputs [i];
			OutduplFrameInfo frameInfo;
			IDXGIResource desktopResource;
			Result result = obj.Dup.AcquireNextFrame (20, out frameInfo, out desktopResource);
			if (result == Vortice.DXGI.ResultCode.WaitTimeout) {
				continue;
			}
			if (result.Failure || desktopResource == null) {
				flag2 = true;
				continue;
			}
			using (desktopResource) {
				try {
					if (frameInfo.AccumulatedFrames == 0) {
						continue;
					}
					using ID3D11Texture2D iD3D11Texture2D = desktopResource.QueryInterface<ID3D11Texture2D> ();
					ID3D11Texture2D resource = (obj.SystemMemory ? iD3D11Texture2D : obj.Staging);
					if (!obj.SystemMemory) {
						_context.CopyResource (obj.Staging, iD3D11Texture2D);
					}
					MappedSubresource mappedResource;
					Result result2 = _context.Map (resource, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out mappedResource);
					int num2 = 0;
					while (result2.Code == -2005270518 && num2 < 5) {
						Thread.Sleep (10);
						result2 = _context.Map (resource, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out mappedResource);
						num2++;
					}
					if (result2.Code == -2005270518) {
						continue;
					}
					if (result2.Code < 0) {
						flag2 = true;
						continue;
					}
					try {
						for (int j = 0; j < obj.Hp; j++) {
							nint source = IntPtr.Add (mappedResource.DataPointer, (int)(j * mappedResource.RowPitch));
							Marshal.Copy (source, obj.Row, 0, obj.Wp * 4);
							for (int k = 3; k < obj.Wp * 4; k += 4) {
								obj.Row [k] = byte.MaxValue;
							}
							Buffer.BlockCopy (obj.Row, 0, _pixels, (obj.Oy + j) * num + obj.Ox * 4, obj.Wp * 4);
						}
					} finally {
						_context.Unmap (resource, 0);
					}
					flag = true;
				} catch {
					flag2 = true;
				} finally {
					try {
						obj.Dup.ReleaseFrame ();
					} catch {
					}
				}
			}
		}
		if (flag2) {
			LostAll ("某输出 AcquireNextFrame 失败");
		}
		if (flag) {
			Frames++;
			w = _w;
			h = _h;
			_exceptStreak = 0;
			return true;
		}
		EmptyFrames++;
		return false;
		} catch (Exception ex) {
			_exceptStreak++;
			LastError = "DXGI 取帧异常：" + ex.Message;
			Status ($"[抓屏] DXGI 取帧异常（{_exceptStreak}/3）：{ex.Message}");
			if (_exceptStreak >= 3) {
				Banned = true;
				Available = false;
				Status ("[抓屏] DXGI 连续异常，已自动降级回退 GDI BitBlt（仅本实例走 GDI，不影响同进程其它实例）");
			}
			return false;
		}
	}

	private void TryInit ()
	{
		if (_dxgiMutex == null) {
			bool flag = false;
			bool flag2 = true;
			string[] array = new string[2] { "Global\\" + AppIdentity.DxgiMutexName, AppIdentity.DxgiMutexName };
			foreach (string name in array) {
				try {
					bool createdNew;
					Mutex mutex = new Mutex (initiallyOwned: true, name, out createdNew);
					if (createdNew) {
						_dxgiMutex = mutex;
						flag = true;
						flag2 = false;
					} else {
						mutex.Dispose ();
						flag2 = false;
					}
				} catch (UnauthorizedAccessException) {
					continue;
				} catch (Exception ex2) {
					Status ("[抓屏] 互斥检查异常（" + ex2.Message + "），继续尝试 DXGI");
					flag2 = false;
				}
				break;
			}
			if (!flag) {
				if (!flag2) {
					LastError = "桌面复制被其它进程占用，回退 GDI BitBlt";
					Status ("[抓屏] " + LastError);
					return;
				}
				Status ("[抓屏] 无法创建桌面复制互斥（权限不足），直接尝试 DXGI");
			}
		}
		foreach (Out output in _outputs) {
			try {
				output.Dup?.Dispose ();
			} catch {
			}
			try {
				output.Staging?.Dispose ();
			} catch {
			}
		}
		_outputs.Clear ();
		try {
			_context?.Dispose ();
		} catch {
		}
		try {
			_device?.Dispose ();
		} catch {
		}
		_context = null;
		_device = null;
		_pixels = null;
		try {
			// 11_1 特性级别在 Windows 7 上会让 D3D11CreateDevice 直接返回 E_INVALIDARG，
			// 所以只在 Windows 8+ 的系统请求里带上它。
			FeatureLevel[] featureLevels = OsSupport.IsWindows8OrGreater
				? new FeatureLevel[4] {
					FeatureLevel.Level_11_1,
					FeatureLevel.Level_11_0,
					FeatureLevel.Level_10_1,
					FeatureLevel.Level_10_0
				}
				: new FeatureLevel[3] {
					FeatureLevel.Level_11_0,
					FeatureLevel.Level_10_1,
					FeatureLevel.Level_10_0
				};
			ID3D11Device device;
			ID3D11DeviceContext immediateContext;
			Result result = D3D11.D3D11CreateDevice ((IDXGIAdapter?)null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, featureLevels, out device, out immediateContext);
			if (result.Failure || device == null || immediateContext == null) {
				LastError = "D3D11CreateDevice 失败：" + result.Description;
				return;
			}
			_device = device;
			_context = immediateContext;
			using IDXGIDevice iDXGIDevice = device.QueryInterface<IDXGIDevice> ();
			if (iDXGIDevice.GetAdapter (out var adapter).Failure || adapter == null) {
				LastError = "取 DXGI 适配器失败";
				return;
			}
			using (adapter) {
				int num = 0;
				bool flag3 = true;
				IDXGIOutput outputOut;
				while (!adapter.EnumOutputs (num, out outputOut).Failure && !(outputOut == null)) {
					num++;
					using (outputOut) {
						RawRect desktopCoordinates = outputOut.Description.DesktopCoordinates;
						Rectangle rectangle = Rectangle.FromLTRB (desktopCoordinates.Left, desktopCoordinates.Top, desktopCoordinates.Right, desktopCoordinates.Bottom);
						if (rectangle.Width <= 0 || rectangle.Height <= 0) {
							continue;
						}
						IDXGIOutputDuplication iDXGIOutputDuplication = null;
						bool systemMemory = false;
						try {
							using IDXGIOutput1 iDXGIOutput = outputOut.QueryInterface<IDXGIOutput1> ();
							iDXGIOutputDuplication = iDXGIOutput.DuplicateOutput (_device);
							if (iDXGIOutputDuplication == null) {
								Status ($"[抓屏] 输出#{num} DuplicateOutput 返回空，跳过");
								continue;
							}
							try {
								systemMemory = iDXGIOutputDuplication.Description.DesktopImageInSystemMemory;
							} catch {
							}
						} catch (Exception ex3) when (ex3.Message.Contains ("0x80070057") || ex3.Message.Contains ("E_INVALIDARG")) {
							Thread.Sleep (600);
							try {
								using IDXGIOutput1 iDXGIOutput2 = outputOut.QueryInterface<IDXGIOutput1> ();
								iDXGIOutputDuplication = iDXGIOutput2.DuplicateOutput (_device);
								if (iDXGIOutputDuplication != null) {
									try {
										systemMemory = iDXGIOutputDuplication.Description.DesktopImageInSystemMemory;
									} catch {
									}
								}
							} catch {
							}
							if (iDXGIOutputDuplication == null) {
								Status ($"[抓屏] 输出#{num} 初始化失败，跳过：{ex3.Message}");
								continue;
							}
						} catch (Exception ex4) {
							bool flag4 = ex4.Message.Contains ("E_INVALIDARG") || ex4.Message.Contains ("0x80070057");
							Status ($"[抓屏] 输出#{num} 初始化失败，跳过：{ex4.Message}" + (flag4 ? "（通常为其它录屏程序/本程序旧实例仍占用该显示的桌面复制；请关闭 OBS 等并结束所有 painter 进程后重试）" : ""));
							goto end_IL_02b0;
						}
						Texture2DDescription description = new Texture2DDescription {
							Width = rectangle.Width,
							Height = rectangle.Height,
							MipLevels = 1,
							ArraySize = 1,
							Format = Format.B8G8R8A8_UNorm,
							SampleDescription = new SampleDescription (1, 0),
							Usage = ResourceUsage.Staging,
							BindFlags = BindFlags.None,
							CPUAccessFlags = CpuAccessFlags.Read,
							MiscFlags = ResourceOptionFlags.None
						};
						ID3D11Texture2D iD3D11Texture2D = _device.CreateTexture2D (description);
						using (ID3D11Resource iD3D11Resource = iD3D11Texture2D.QueryInterface<ID3D11Resource> ()) {
							iD3D11Resource.EvictionPriority = unchecked((int)DxgiResourcePriorityMaximum);
						}
						if (flag3) {
							_union = rectangle;
							flag3 = false;
						} else {
							_union = Rectangle.Union (_union, rectangle);
						}
						_outputs.Add (new Out {
							Dup = iDXGIOutputDuplication,
							Staging = iD3D11Texture2D,
							SystemMemory = systemMemory,
							Rect = rectangle,
							Wp = rectangle.Width,
							Hp = rectangle.Height,
							Row = new byte[rectangle.Width * 4]
						});
						end_IL_02b0:;
					}
				}
				if (_outputs.Count == 0) {
					LastError = "没有可用的显示输出（可能已被其他抓屏程序独占）";
					return;
				}
				foreach (Out output2 in _outputs) {
					output2.Ox = output2.Rect.Left - _union.Left;
					output2.Oy = output2.Rect.Top - _union.Top;
				}
				_w = _union.Width;
				_h = _union.Height;
				_pixels = new byte[_w * _h * 4];
				for (int j = 3; j < _pixels.Length; j += 4) {
					_pixels [j] = byte.MaxValue;
				}
				bool flag5 = _outputs.Any ((Out o) => o.SystemMemory);
				Available = true;
				Status ($"[抓屏] DXGI Desktop Duplication 就绪（{_outputs.Count} 显示器 {_w}x{_h}）GDI 回读已停用；驱动={(flag5 ? "系统内存模式(fastlane 可用)" : "显存模式")}");
			}
		} catch (Exception ex5) {
			LastError = ex5.Message;
		}
	}

	private void LostAll (string why)
	{
		LastError = why;
		foreach (Out output in _outputs) {
			try {
				output.Dup.Dispose ();
			} catch {
			}
			try {
				output.Staging.Dispose ();
			} catch {
			}
		}
		_outputs.Clear ();
		_nextRetryAt = NetFx.TickCount64 + 300;
		Status ("[抓屏] DXGI 画面丢失：" + why + "；正在重建（期间跳帧，不回退 GDI）");
	}

	private void Status (string s)
	{
		long tickCount = NetFx.TickCount64;
		if (tickCount - _lastLogTick < 500) {
			return;
		}
		_lastLogTick = tickCount;
		try {
			Log?.Invoke (s);
		} catch {
		}
	}

	public void Dispose ()
	{
		foreach (Out output in _outputs) {
			try {
				output.Dup.Dispose ();
			} catch {
			}
			try {
				output.Staging.Dispose ();
			} catch {
			}
		}
		_outputs.Clear ();
		try {
			_context?.Dispose ();
		} catch {
		}
		try {
			_device?.Dispose ();
		} catch {
		}
		try {
			_dxgiMutex?.Dispose ();
		} catch {
		}
		_dxgiMutex = null;
		_context = null;
		_device = null;
		_pixels = null;
		Available = false;
	}
}
