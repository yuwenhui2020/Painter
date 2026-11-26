using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading;

using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace desk;

/// <summary>
/// Web 上传时的运行环境快照。由 WPF 主窗口注册 <see cref="ShowController.ContextProvider"/> 提供，
/// 只读取纯字段，可安全地在 HTTP / WebSocket 工作线程上调用。
/// </summary>
public sealed class ShareContext
{
    public bool IsPpt;                 // 当前是否处于 PPT 放映模式
    public string PresentationName = "";
    public int SlidesCount;
    public int Slide;                  // 上传时所在的幻灯片序号（1 基）
    public string FolderPath = "";     // 墨迹自动保存的同一文件夹
}

/// <summary>下发给网页端的 PPT 图片条目（不含图片字节，避免把大图塞进状态广播）。</summary>
public sealed class PptItemInfo
{
    public int index { get; set; }
    public string name { get; set; } = "";
    public int slide { get; set; }
    public bool visible { get; set; } = true;
}

/// <summary>PPT 放映模式下上传的一张图片（不进内置白板，直接叠加显示在当前屏幕）。</summary>
internal sealed class PptShareImage
{
    public string Name = "";
    public byte[] Data = null!;
    public int Width;
    public int Height;
    public int Slide;
    public bool Visible = true;
    public string File = "";
}

/// <summary>自动保存清单里的一条记录，与图片文件一起放在墨迹的同一文件夹。</summary>
internal sealed class PptShareRecord
{
    public string name { get; set; } = "";
    public int slide { get; set; }
    public bool visible { get; set; } = true;
    public string file { get; set; } = "";
}

/// <summary>
/// “图片展示”功能的核心状态机（与具体 UI / 网络无关）。
///
/// 设计要点：
///   · 图片以原始字节流 + 尺寸保存在进程内（课堂演示量级，内存占用可接受）。
///   · 所有增删改都在同一把锁内完成，再由 RaiseState 把快照广播给两类订阅者：
///       ① 画板（WPF InkCanvas）—— 订阅 StateChanged，在 painterCanvas 之下渲染图片，
///          并把同一变换同时作用于图片与笔迹，实现“图片 + 标注一起缩放 / 平移”；
///       ② WebServer 的 /show/ws 客户端（手机端）—— 推送 JSON 状态，保持多端同步。
///   · 电脑端本地交互（滚轮 / 拖拽）与手机端指令都走同一组方法，
///     因此“电脑缩放”与“手机缩放”是双向同步的，不会出现两端不一致。
///   · 每张图片各自保存一份视图变换（zoom / offX / offY），因此“每张图独立放大缩小”，翻页互不干扰。
/// </summary>
public static class ShowController
{
	public static Action<string>? Log;

	private static readonly object _lock = new();
	private static readonly List<ShowImage> _images = new();
	private static int _current = -1;
	private static bool _open;

	// 视图变换（以“主屏像素”为单位）：zoom 是相对“适配铺满”的倍率，off 为平移偏移。
	// 每张图片各自保存一份，因此“每张图独立放大缩小”，翻页不会互相影响。
	// 视图变换数组随图片数动态扩容：固定 100 槽会让第 101 张起与第 1 张共用变换，
	// 并把任意 >100 的索引截断到同一张图（课堂批量上传可轻松超过 100 张）。
	private static double[] _zoomArr = new double[100];
	private static int[] _offXArr = new int[100];
	private static int[] _offYArr = new int[100];
	private static double[] _angleArr = new double[100];   // 旋转角度（度），与缩放/平移一起写进同一个矩阵
	private static int _screenW = 1920, _screenH = 1080;

	/// <summary>确保每张图片都有独立的变换槽位（调用方持有 _lock）。</summary>
	private static void EnsureViewCapacity(int count)
	{
		if (count <= _zoomArr.Length) return;
		int size = Math.Max(count, _zoomArr.Length * 2);
		Array.Resize(ref _zoomArr, size);
		Array.Resize(ref _offXArr, size);
		Array.Resize(ref _offYArr, size);
		Array.Resize(ref _angleArr, size);
		for (int i = count; i < size; i++) _zoomArr[i] = 1.0;
	}

	// PPT 放映模式上传的图片：与内置白板图片分开存放，绝不触发“强制切白板”。
	private static readonly List<PptShareImage> _pptImages = new();
	private static string _pptFolder = "";

	/// <summary>
	/// 上传发生时的运行环境。由 WPF 主窗口在构造时注册；返回值必须是纯数据快照，
	/// 因为该委托会在 HttpListener 的工作线程上被调用，不能触碰 DependencyObject。
	/// </summary>
	public static Func<ShareContext>? ContextProvider;

	private static ShareContext SafeContext()
	{
		try { return ContextProvider?.Invoke() ?? new ShareContext(); }
		catch { return new ShareContext(); }
	}

	private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions {
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		WriteIndented = false
	};

	static ShowController()
	{
		for (int i = 0; i < _zoomArr.Length; i++) _zoomArr[i] = 1.0;
	}

	/// <summary>状态变更广播（画板渲染 + 手机端 WS 推送）。</summary>
	public static event Action<ShowState>? StateChanged;

	// ---------- 只读快照 ----------
	public static bool IsOpen { get { lock (_lock) return _open; } }
	public static int Count { get { lock (_lock) return _images.Count; } }

	private static int Ci() { return (_current >= 0 && _current < _images.Count) ? _current : 0; }

	public static ShowState GetState()
	{
		var ctx = SafeContext();
		lock (_lock) {
			var cur = (_current >= 0 && _current < _images.Count) ? _images[_current] : null;
			int i = Ci();
			return new ShowState {
				open = _open,
				count = _images.Count,
				index = _current,
				zoom = _zoomArr[i],
				offX = _offXArr[i],
				offY = _offYArr[i],
				angle = _angleArr[i],
				screenW = _screenW,
				screenH = _screenH,
				imgW = cur?.Width ?? 0,
				imgH = cur?.Height ?? 0,
				pptMode = ctx.IsPpt,
				pptSlide = ctx.Slide,
				pptItems = SnapshotPptItemsLocked(),
			};
		}
	}

	/// <summary>返回指定序号图片的视图状态（供画板按白板页独立渲染，独立于当前选中项）。</summary>
	public static ShowState GetStateForIndex(int idx)
	{
		var ctx = SafeContext();
		lock (_lock) {
			int i = (idx >= 0 && idx < _images.Count) ? idx : (_images.Count > 0 ? 0 : -1);
			var cur = (i >= 0) ? _images[i] : null;
			return new ShowState {
				open = _open,
				count = _images.Count,
				index = i,
				zoom = i >= 0 ? _zoomArr[i] : 1,
				offX = i >= 0 ? _offXArr[i] : 0,
				offY = i >= 0 ? _offYArr[i] : 0,
				angle = i >= 0 ? _angleArr[i] : 0,
				screenW = _screenW,
				screenH = _screenH,
				imgW = cur?.Width ?? 0,
				imgH = cur?.Height ?? 0,
				pptMode = ctx.IsPpt,
				pptSlide = ctx.Slide,
				pptItems = SnapshotPptItemsLocked(),
			};
		}
	}

	/// <summary>当前放映模式（供画板判断该显示白板图片还是屏幕叠加图片）。</summary>
	public static bool IsPptMode { get { var c = SafeContext(); return c.IsPpt; } }
	public static int PptSlide { get { var c = SafeContext(); return c.Slide; } }
	public static string PptFolder { get { var c = SafeContext(); return c.FolderPath; } }

	/// <summary>必须在 _lock 内调用。</summary>
	private static PptItemInfo[] SnapshotPptItemsLocked()
	{
		var arr = new PptItemInfo[_pptImages.Count];
		for (int k = 0; k < _pptImages.Count; k++) {
			var im = _pptImages[k];
			arr[k] = new PptItemInfo { index = k, name = im.Name, slide = im.Slide, visible = im.Visible };
		}
		return arr;
	}

	// ---------- 供画板（WPF）查询 ----------
	/// <summary>返回当前图片的字节流与原始尺寸，供画板解码渲染。无图时返回 null。</summary>
	public static (byte[]? data, int w, int h) GetCurrentImageInfo()
	{
		lock (_lock) {
			if (_current < 0 || _current >= _images.Count) return (null, 0, 0);
			var img = _images[_current];
			return (img.Data, img.Width, img.Height);
		}
	}

	/// <summary>返回指定序号图片的字节流与原始尺寸，供画板按白板页解码渲染。无图时返回 null。</summary>
	public static (byte[]? data, int w, int h) GetImageInfo(int index)
	{
		lock (_lock) {
			if (index < 0 || index >= _images.Count) return (null, 0, 0);
			var img = _images[index];
			return (img.Data, img.Width, img.Height);
		}
	}

	internal static (double zoom, int offX, int offY) GetView()
	{
		lock (_lock) { int i = Ci(); return (_zoomArr[i], _offXArr[i], _offYArr[i]); }
	}

	public static void SetScreenSize(int w, int h)
	{
		lock (_lock) { if (w > 0) _screenW = w; if (h > 0) _screenH = h; }
	}

	internal static void OnFormReady()
	{
		// 兼容占位：画板（WPF）自行订阅 StateChanged，无需窗体回调
		RaiseState();
	}

	/// <summary>PPT 换页 / 进出放映时主动广播一次状态，让画板刷新屏幕叠加层。</summary>
	public static void Refresh() { RaiseState(); }

	internal static void OnFormClosed()
	{
	}

	// ---------- 生命周期 ----------
	public static void Open()
	{
		lock (_lock) _open = true;
		RaiseState();
	}

	public static void Close()
	{
		lock (_lock) _open = false;
		RaiseState();
	}

	// ---------- 图片管理 ----------
	/// <summary>
	/// 追加一批图片（字节流）。非 PPT 模式进入内置白板（一页一图）；
	/// PPT 放映模式则绑定当前幻灯片页码、直接叠加显示在当前屏幕，并自动保存到墨迹的同一文件夹。
	/// 返回成功解码的数量。
	/// </summary>
	public static int AddImages(List<(string name, byte[] data)> imgs)
	{
		var ctx = SafeContext();

		// 关键：GDI+ 探尺寸是最耗时的一步，必须在锁外做。
		// 若放在 lock(_lock) 内，UI 线程每次输入（IsWbImagePage/ShowOnState）抢同一把锁都会被阻塞，
		// 表现为“一放图片整个界面卡住”。这里先在锁外解码完，再快速加锁入列。
		var probed = new List<ShowImage>();
		foreach (var (name, data) in imgs) {
			try {
				using var ms = new MemoryStream(data, false);
				using var probe = Image.FromStream(ms);
				probed.Add(new ShowImage { Name = name, Data = data, Width = probe.Width, Height = probe.Height });
			} catch (Exception ex) {
				Log?.Invoke($"[Show] 图片解码失败：{name}（{data.Length} 字节）—— {ex.Message}");
			}
		}
		if (probed.Count == 0) return 0;

		// ---- PPT 放映模式：不进内置白板 ----
		if (ctx.IsPpt && !string.IsNullOrEmpty(ctx.FolderPath)) {
			lock (_lock) {
				_pptFolder = ctx.FolderPath;
				foreach (var p in probed) {
					_pptImages.Add(new PptShareImage {
						Name = p.Name, Data = p.Data, Width = p.Width, Height = p.Height,
						Slide = ctx.Slide, Visible = true
					});
				}
			}
			PersistPpt();
			RaiseState();
			return probed.Count;
		}

		// ---- 非 PPT 模式：内置白板，一页一图（原有行为）----
		int ok = 0;
		lock (_lock) {
			bool wasEmpty = _images.Count == 0;
			foreach (var img in probed) {
				int slot = _images.Count;
				_images.Add(img); ok++;
				EnsureViewCapacity(_images.Count);
				_zoomArr[slot] = 1.0; _offXArr[slot] = 0; _offYArr[slot] = 0; _angleArr[slot] = 0;
			}
			if (wasEmpty && _images.Count > 0) _current = 0;
			if (_images.Count > 0) { _open = true; }
		}
		RaiseState();
		return ok;
	}

	// ---------- PPT 放映模式图片 ----------
	private const string PptManifestName = "share_manifest.json";

	/// <summary>把 PPT 图片与清单写进墨迹自动保存的同一文件夹。非 PPT 上传完全不写盘。</summary>
	private static void PersistPpt()
	{
		string folder;
		List<PptShareImage> snapshot;
		lock (_lock) { folder = _pptFolder; snapshot = new List<PptShareImage>(_pptImages); }
		if (string.IsNullOrEmpty(folder)) return;
		try {
			Directory.CreateDirectory(folder);
			var recs = new List<PptShareRecord>(snapshot.Count);
			foreach (var im in snapshot) {
				lock (_lock) {
					if (string.IsNullOrEmpty(im.File))
						im.File = "share_" + im.Slide.ToString("0000") + "_" + Guid.NewGuid().ToString("N").Substring(0, 10) + ".pic";
				}
				var full = Path.Combine(folder, im.File);
				if (!File.Exists(full)) File.WriteAllBytes(full, im.Data);
				recs.Add(new PptShareRecord { name = im.Name, slide = im.Slide, visible = im.Visible, file = im.File });
			}
			File.WriteAllText(Path.Combine(folder, PptManifestName), JsonSerializer.Serialize(recs, _jsonOpts));
		} catch (Exception ex) { Log?.Invoke($"[Share] PPT 图片自动保存失败：{ex.Message}"); }
	}

	/// <summary>进入某份演示文稿的放映时，把上次自动保存的图片读回内存（换页翻回来即可重现）。</summary>
	public static void LoadPptImages(string folder)
	{
		var loaded = new List<PptShareImage>();
		try {
			var mf = Path.Combine(folder, PptManifestName);
			if (File.Exists(mf)) {
				var recs = JsonSerializer.Deserialize<List<PptShareRecord>>(File.ReadAllText(mf), _jsonOpts);
				if (recs != null) {
					foreach (var r in recs) {
						try {
							var full = Path.Combine(folder, r.file ?? "");
							if (!File.Exists(full)) continue;
							var data = File.ReadAllBytes(full);
							int w = 0, h = 0;
							try { using var ms = new MemoryStream(data, false); using var pi = Image.FromStream(ms); w = pi.Width; h = pi.Height; } catch { }
							loaded.Add(new PptShareImage { Name = r.name ?? "", Data = data, Width = w, Height = h, Slide = r.slide, Visible = r.visible, File = r.file ?? "" });
						} catch { }
					}
				}
			}
		} catch (Exception ex) { Log?.Invoke($"[Share] 读取已保存的 PPT 图片失败：{ex.Message}"); }
		lock (_lock) { _pptFolder = folder; _pptImages.Clear(); _pptImages.AddRange(loaded); }
		RaiseState();
	}

	/// <summary>网页端随时控制某张 PPT 图片的显示 / 隐藏。</summary>
	public static void SetPptVisible(int index, bool visible)
	{
		lock (_lock) {
			if (index < 0 || index >= _pptImages.Count) return;
			_pptImages[index].Visible = visible;
		}
		PersistPpt();
		RaiseState();
	}

	public static void SetPptAllVisible(bool visible)
	{
		lock (_lock) { foreach (var im in _pptImages) im.Visible = visible; }
		PersistPpt();
		RaiseState();
	}

	public static void RemovePpt(int index)
	{
		string? toDelete = null;
		string folder;
		lock (_lock) {
			if (index < 0 || index >= _pptImages.Count) return;
			toDelete = _pptImages[index].File;
			folder = _pptFolder;
			_pptImages.RemoveAt(index);
		}
		try { if (!string.IsNullOrEmpty(toDelete) && !string.IsNullOrEmpty(folder)) { var p = Path.Combine(folder, toDelete); if (File.Exists(p)) File.Delete(p); } } catch { }
		PersistPpt();
		RaiseState();
	}

	public static void ClearPpt()
	{
		lock (_lock) {
			foreach (var im in _pptImages) {
				try { if (!string.IsNullOrEmpty(im.File) && !string.IsNullOrEmpty(_pptFolder)) { var p = Path.Combine(_pptFolder, im.File); if (File.Exists(p)) File.Delete(p); } } catch { }
			}
			_pptImages.Clear();
		}
		PersistPpt();
		RaiseState();
	}

	/// <summary>返回指定幻灯片上、且网页端设为可见的图片（供画板直接叠加显示在当前屏幕）。</summary>
	public static List<(byte[] data, int w, int h, int index)> GetVisiblePptImages(int slide)
	{
		lock (_lock) {
			var res = new List<(byte[] data, int w, int h, int index)>();
			for (int k = 0; k < _pptImages.Count; k++) {
				var im = _pptImages[k];
				if (im.Visible && im.Slide == slide) res.Add((im.Data, im.Width, im.Height, k));
			}
			return res;
		}
	}

	/// <summary>删除指定索引图片（手机端“移除”使用）。会同步前移“当前项”指针与视图变换数组，
	/// 保证删掉当前项之前的图片后，大屏仍停留在同一张图上（仅索引变化）。</summary>
	public static void RemoveAt(int index)
	{
		lock (_lock) {
			if (index < 0 || index >= _images.Count) return;
			_images.RemoveAt(index);
			// 视图变换数组与图片列表同步前移，保持“每张图独立缩放”的对齐
			for (int i = index; i < _images.Count; i++) {
				_zoomArr[i] = _zoomArr[i + 1];
				_offXArr[i] = _offXArr[i + 1];
				_offYArr[i] = _offYArr[i + 1];
				_angleArr[i] = _angleArr[i + 1];
			}
			if (_images.Count == 0) {
				_current = -1;
				_zoomArr[0] = 1; _offXArr[0] = 0; _offYArr[0] = 0; _angleArr[0] = 0;
			} else {
				if (_current > index) _current--;                                          // 删在前面 → 当前指针前移
				else if (_current == index) _current = Math.Min(_current, _images.Count - 1); // 删当前项 → 落到后一项 / 末项
				if (_current >= _images.Count) _current = _images.Count - 1;
				if (_current < 0) _current = 0;
			}
		}
		RaiseState();
	}

	// ---------- 轮播 ----------
	public static void Next()
	{
		lock (_lock) {
			if (_images.Count == 0) return;
			_current = (_current + 1) % _images.Count;
		}
		RaiseState();
	}

	public static void Prev()
	{
		lock (_lock) {
			if (_images.Count == 0) return;
			_current = (_current - 1 + _images.Count) % _images.Count;
		}
		RaiseState();
	}

	public static void Goto(int index)
	{
		lock (_lock) {
			if (index < 0 || index >= _images.Count) return;
			_current = index;
		}
		RaiseState();
	}

	// ---------- 视图变换（按当前图片各自的槽位存储） ----------
	public static void Zoom(double factor, double? cxNorm = null, double? cyNorm = null)
	{
		double cx = cxNorm ?? 0.5;
		double cy = cyNorm ?? 0.5;
		lock (_lock) ApplyZoom(_zoomArr[Ci()] * factor, cx, cy);
		RaiseState();
	}

	public static void SetZoom(double zoom, double cxNorm, double cyNorm)
	{
		lock (_lock) ApplyZoom(zoom, cxNorm, cyNorm);
		RaiseState();
	}

	public static void Pan(int dx, int dy)
	{
		lock (_lock) { int i = Ci(); _offXArr[i] += dx; _offYArr[i] += dy; }
		RaiseState();
	}

	/// <summary>旋转当前图片（角度为度）。图片与墨迹共用同一矩阵，因此相对位置保持不变。</summary>
	public static void Rotate(double deltaDegrees)
	{
		lock (_lock) {
			int i = Ci();
			_angleArr[i] += deltaDegrees;
			if (_angleArr[i] > 3600 || _angleArr[i] < -3600) _angleArr[i] %= 360;
		}
		RaiseState();
	}

	public static void ResetView()
	{
		lock (_lock) { int i = Ci(); _zoomArr[i] = 1; _offXArr[i] = 0; _offYArr[i] = 0; _angleArr[i] = 0; }
		RaiseState();
	}

	// ---------- 内部 ----------
	private static void ApplyZoom(double newZoom, double cxNorm, double cyNorm)
	{
		int i = Ci();
		newZoom = Math.Max(0.05, Math.Min(20, newZoom));
		int sw = _screenW, sh = _screenH;
		double fit = FitScale();
		double oldScale = fit * _zoomArr[i];
		double newScale = fit * newZoom;
		if (oldScale <= 0) oldScale = 1;
		if (newScale <= 0) newScale = 1;
		double ccx = cxNorm * sw;
		double ccy = cyNorm * sh;
		// 让 (cx,cy) 处的图像世界坐标在缩放前后保持不动 → 调整平移偏移
		double wx = (ccx - (sw / 2.0 + _offXArr[i])) / oldScale;
		double wy = (ccy - (sh / 2.0 + _offYArr[i])) / oldScale;
		_offXArr[i] = (int)Math.Round(ccx - sw / 2.0 - wx * newScale);
		_offYArr[i] = (int)Math.Round(ccy - sh / 2.0 - wy * newScale);
		_zoomArr[i] = newZoom;
	}

	private static double FitScale()
	{
		var img = (_current >= 0 && _current < _images.Count) ? _images[_current] : null;
		// 损坏图片以 0 宽高入库，直接相除会得到 Infinity 污染渲染变换矩阵
		if (img == null || img.Width <= 0 || img.Height <= 0 || _screenW <= 0 || _screenH <= 0) return 1;
		double s = Math.Min((double)_screenW / img.Width, (double)_screenH / img.Height);
		return s > 0 && !double.IsInfinity(s) && !double.IsNaN(s) ? s : 1;
	}

	private static void RaiseState()
	{
		var ctx = SafeContext();
		ShowState s;
		lock (_lock) {
			var cur = (_current >= 0 && _current < _images.Count) ? _images[_current] : null;
			int i = Ci();
			s = new ShowState {
				open = _open,
				count = _images.Count,
				index = _current,
				zoom = _zoomArr[i],
				offX = _offXArr[i],
				offY = _offYArr[i],
				angle = _angleArr[i],
				screenW = _screenW,
				screenH = _screenH,
				imgW = cur?.Width ?? 0,
				imgH = cur?.Height ?? 0,
				pptMode = ctx.IsPpt,
				pptSlide = ctx.Slide,
				pptItems = SnapshotPptItemsLocked(),
			};
		}
		try { StateChanged?.Invoke(s); } catch (Exception ex) { Log?.Invoke($"[Show] 状态广播异常：{ex}"); }
	}
}

/// <summary>一张待展示的图片（原始字节流 + 尺寸，供画板解码渲染）。</summary>
internal sealed class ShowImage
{
	public string Name = "";
	public byte[] Data = null!;
	public int Width;
	public int Height;
}

/// <summary>展示状态快照（JSON 序列化后下发手机端 / 供画板渲染）。</summary>
public sealed class ShowState
{
	// 旧版网页端 (show.js) 用 msg.type === "state" 判断消息类型，此前服务端漏了这个字段，
	// 导致手机遥控页永远收不到状态更新。这里补上，同时也供新的 /share 页面使用。
	public string type { get; set; } = "state";
	public bool open { get; set; }
	public int count { get; set; }
	public int index { get; set; }
	public double zoom { get; set; }
	public int offX { get; set; }
	public int offY { get; set; }
	public double angle { get; set; }        // 旋转角度（度）
	public int screenW { get; set; }
	public int screenH { get; set; }
	public int imgW { get; set; }
	public int imgH { get; set; }
	public bool pptMode { get; set; }        // 当前是否处于 PPT 放映模式
	public int pptSlide { get; set; }        // 当前幻灯片页码
	public PptItemInfo[] pptItems { get; set; } = Array.Empty<PptItemInfo>();
}
