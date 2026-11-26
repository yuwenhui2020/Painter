using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SkiaSharp;

using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace desk;

internal static class ScreenCapture
{
	private struct POINT
	{
		public int x;

		public int y;
	}

	private struct CURSORINFO
	{
		public int cbSize;

		public int flags;

		public nint hCursor;

		public POINT ptScreenPos;
	}

	private const int STRETCH_HALFTONE = 4;

	private const uint SRCCOPY = 13369376u;

	private const uint CAPTUREBLT = 1073741824u;

	private const int CURSOR_SHOWING = 1;

	private const uint DI_NORMAL = 3u;

	private const uint GR_GDIOBJECTS = 0u;

	private const uint GR_USEROBJECTS = 1u;

	public static bool UseCaptureBlt { get; set; }

	[DllImport ("user32.dll")]
	private static extern nint GetDC (nint hWnd);

	[DllImport ("user32.dll")]
	private static extern int ReleaseDC (nint hWnd, nint hDC);

	[DllImport ("gdi32.dll")]
	private static extern bool BitBlt (nint hdcDest, int xDest, int yDest, int w, int h, nint hdcSrc, int xSrc, int ySrc, uint rop);

	[DllImport ("gdi32.dll")]
	private static extern bool StretchBlt (nint hdcDest, int xDest, int yDest, int wDest, int hDest, nint hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);

	[DllImport ("gdi32.dll")]
	private static extern int SetStretchBltMode (nint hdc, int mode);

	private static Bitmap Prepare (Bitmap? reuse, int w, int h, PixelFormat fmt)
	{
		if (reuse != null) {
			if (reuse.Width == w && reuse.Height == h && reuse.PixelFormat == fmt) {
				return reuse;
			}
			reuse.Dispose ();
		}
		return new Bitmap (w, h, fmt);
	}

	public static Bitmap CaptureVirtualScreen ()
	{
		return CaptureVirtualScreen (SystemInformation.VirtualScreen, null);
	}

	public static Bitmap CaptureVirtualScreen (Rectangle bounds)
	{
		return CaptureVirtualScreen (bounds, null);
	}

	public static Bitmap CaptureVirtualScreen (Rectangle bounds, Bitmap? reuse)
	{
		Bitmap bitmap = Prepare (reuse, bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
		nint dC = GetDC (IntPtr.Zero);
		try {
			using Graphics graphics = Graphics.FromImage (bitmap);
			nint hdc = graphics.GetHdc ();
			try {
				uint rop = (uint)(0xCC0020 | (UseCaptureBlt ? 1073741824 : 0));
				if (!BitBlt (hdc, 0, 0, bounds.Width, bounds.Height, dC, bounds.X, bounds.Y, rop)) {
					BitBlt (hdc, 0, 0, bounds.Width, bounds.Height, dC, bounds.X, bounds.Y, 13369376u);
				}
			} finally {
				graphics.ReleaseHdc (hdc);
			}
		} finally {
			if (dC != IntPtr.Zero) {
				ReleaseDC (IntPtr.Zero, dC);
			}
		}
		return bitmap;
	}

	[DllImport ("user32.dll")]
	private static extern bool GetCursorInfo (ref CURSORINFO pci);

	[DllImport ("user32.dll")]
	private static extern bool DrawIconEx (nint hdc, int x, int y, nint hIcon, int cxWidth, int cyWidth, int istepIfAniCur, nint hbrFlickerFreeDraw, uint diFlags);

	public static void DrawCursorOnto (Bitmap bmp, Rectangle screenBounds)
	{
		try {
			if (screenBounds.Width <= 0 || screenBounds.Height <= 0) {
				return;
			}
			CURSORINFO pci = new CURSORINFO {
				cbSize = Marshal.SizeOf<CURSORINFO> ()
			};
			if (!GetCursorInfo (ref pci) || (pci.flags & 1) == 0 || pci.hCursor == IntPtr.Zero) {
				return;
			}
			float num = (float)bmp.Width / (float)screenBounds.Width;
			float num2 = (float)bmp.Height / (float)screenBounds.Height;
			int x = (int)((float)(pci.ptScreenPos.x - screenBounds.X) * num);
			int y = (int)((float)(pci.ptScreenPos.y - screenBounds.Y) * num2);
			using Graphics graphics = Graphics.FromImage (bmp);
			nint hdc = graphics.GetHdc ();
			try {
				DrawIconEx (hdc, x, y, pci.hCursor, 0, 0, 0, IntPtr.Zero, 3u);
			} finally {
				graphics.ReleaseHdc (hdc);
			}
		} catch {
		}
	}

	public static nint GetCursorHandle ()
	{
		try {
			CURSORINFO pci = new CURSORINFO {
				cbSize = Marshal.SizeOf<CURSORINFO> ()
			};
			if (!GetCursorInfo (ref pci)) {
				return IntPtr.Zero;
			}
			if ((pci.flags & 1) == 0) {
				return IntPtr.Zero;
			}
			return pci.hCursor;
		} catch {
			return IntPtr.Zero;
		}
	}

	public static Bitmap ScaleToWidth (Bitmap src, int maxWidth)
	{
		return ScaleToWidth (src, maxWidth, null);
	}

	public static Bitmap ScaleToWidth (Bitmap src, int maxWidth, Bitmap? reuse)
	{
		int num;
		int num2;
		if (src.Width <= maxWidth) {
			num = src.Width;
			num2 = src.Height;
		} else {
			float num3 = (float)maxWidth / (float)src.Width;
			num = maxWidth;
			num2 = (int)((float)src.Height * num3);
		}
		// GDI+ DrawImage 由本机代码实现（Win7 起全版本可用），比纯托管逐像素双线性
		// 快一个数量级。所有平台统一走这条路径，既让 Win7 的抓屏流畅度与 Win10/11
		// 一致，也消除了「Win7 走 GDI 回退、Win10 走另一套实现」的差异。
		// 任何异常都回退到下面的原有托管实现，功能不受影响。
		Bitmap? fast = null;
		try {
			bool canReuse = reuse != null && reuse.Width == num && reuse.Height == num2
				&& reuse.PixelFormat == PixelFormat.Format32bppArgb;
			fast = canReuse ? reuse : new Bitmap (num, num2, PixelFormat.Format32bppArgb);
			using (Graphics g = Graphics.FromImage (fast)) {
				g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
				g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
				g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
				g.DrawImage (src, new Rectangle (0, 0, num, num2),
					0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
			}
			Bitmap result = fast;
			fast = null;
			if (!ReferenceEquals (result, reuse) && reuse != null) {
				try { reuse.Dispose (); } catch { }
			}
			return result;
		} catch {
			if (fast != null && !ReferenceEquals (fast, reuse)) {
				try { fast.Dispose (); } catch { }
			}
		}

		Bitmap bitmap = Prepare (reuse, num, num2, PixelFormat.Format32bppArgb);
		Rectangle rect = new Rectangle (0, 0, src.Width, src.Height);

		// 下面的逐像素拷贝只按 24bpp(3字节)/其它(4字节) 处理；若源是 8/16bpp、
		// Indexed 等格式，按 4 字节/像素读会越过 GDI 位图缓冲区造成 AccessViolation。
		// 非 24/32bpp 的罕见情况改用安全的 GDI+ 缩放，绝不走原生指针数学。
		PixelFormat pf = src.PixelFormat;
		bool is24 = pf == PixelFormat.Format24bppRgb;
		bool is32 = pf == PixelFormat.Format32bppArgb || pf == PixelFormat.Format32bppPArgb
			|| pf == PixelFormat.Format32bppRgb;
		if (!is24 && !is32) {
			try {
				using (Graphics g = Graphics.FromImage (bitmap)) {
					g.DrawImage (src, new Rectangle (0, 0, num, num2),
						0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
				}
			} catch { }
			return bitmap;
		}

		BitmapData bitmapData = src.LockBits (rect, ImageLockMode.ReadOnly, src.PixelFormat);
		BitmapData bitmapData2 = bitmap.LockBits (new Rectangle (0, 0, num, num2), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
		try {
			int num4 = ((bitmapData.PixelFormat == PixelFormat.Format24bppRgb) ? 3 : 4);
			if (num == src.Width && num2 == src.Height) {
				byte[] array = new byte[num * 4];
				for (int i = 0; i < num2; i++) {
					nint num5 = IntPtr.Add (bitmapData.Scan0, i * bitmapData.Stride);
					if (num4 == 4) {
						Marshal.Copy (num5, array, 0, num * 4);
						for (int j = 3; j < num * 4; j += 4) {
							array [j] = byte.MaxValue;
						}
					} else {
						int num6 = 0;
						int num7 = 0;
						while (num6 < num) {
							array [num7] = Marshal.ReadByte (num5, num6 * 3);
							array [num7 + 1] = Marshal.ReadByte (num5, num6 * 3 + 1);
							array [num7 + 2] = Marshal.ReadByte (num5, num6 * 3 + 2);
							array [num7 + 3] = byte.MaxValue;
							num6++;
							num7 += 4;
						}
					}
					Marshal.Copy (array, 0, IntPtr.Add (bitmapData2.Scan0, i * bitmapData2.Stride), num * 4);
				}
			} else {
				int stride = bitmapData.Stride;
				byte[] array2 = new byte[src.Height * stride];
				Marshal.Copy (bitmapData.Scan0, array2, 0, array2.Length);
				int stride2 = bitmapData2.Stride;
				byte[] array3 = new byte[num2 * stride2];
				float num8 = (float)src.Width / (float)num;
				float num9 = (float)src.Height / (float)num2;
				for (int k = 0; k < num2; k++) {
					float num10 = ((float)k + 0.5f) * num9 - 0.5f;
					int num11 = ClampI ((int)num10, 0, src.Height - 1);
					int num12 = ClampI (num11 + 1, 0, src.Height - 1);
					float num13 = num10 - (float)num11;
					int num14 = num11 * stride;
					int num15 = num12 * stride;
					int num16 = k * stride2;
					for (int l = 0; l < num; l++) {
						float num17 = ((float)l + 0.5f) * num8 - 0.5f;
						int num18 = ClampI ((int)num17, 0, src.Width - 1);
						int num19 = ClampI (num18 + 1, 0, src.Width - 1);
						float num20 = num17 - (float)num18;
						int num21 = num14 + num18 * num4;
						int num22 = num14 + num19 * num4;
						int num23 = num15 + num18 * num4;
						int num24 = num15 + num19 * num4;
						byte b = array2 [num21];
						byte b2 = array2 [num21 + 1];
						byte b3 = array2 [num21 + 2];
						byte b4 = array2 [num22];
						byte b5 = array2 [num22 + 1];
						byte b6 = array2 [num22 + 2];
						byte b7 = array2 [num23];
						byte b8 = array2 [num23 + 1];
						byte b9 = array2 [num23 + 2];
						byte b10 = array2 [num24];
						byte b11 = array2 [num24 + 1];
						byte b12 = array2 [num24 + 2];
						int num25 = (int)((float)(int)b + (float)(b4 - b) * num20 + (float)(b7 - b) * num13 + (float)(b10 - b7 - b4 + b) * num20 * num13);
						int num26 = (int)((float)(int)b2 + (float)(b5 - b2) * num20 + (float)(b8 - b2) * num13 + (float)(b11 - b8 - b5 + b2) * num20 * num13);
						int num27 = (int)((float)(int)b3 + (float)(b6 - b3) * num20 + (float)(b9 - b3) * num13 + (float)(b12 - b9 - b6 + b3) * num20 * num13);
						int num28 = num16 + l * 4;
						array3 [num28] = (byte)num25;
						array3 [num28 + 1] = (byte)num26;
						array3 [num28 + 2] = (byte)num27;
						array3 [num28 + 3] = byte.MaxValue;
					}
				}
				Marshal.Copy (array3, 0, bitmapData2.Scan0, array3.Length);
			}
		} finally {
			src.UnlockBits (bitmapData);
			bitmap.UnlockBits (bitmapData2);
		}
		return bitmap;
	}

	private static int ClampI (int v, int lo, int hi)
	{
		return (v < lo) ? lo : ((v > hi) ? hi : v);
	}

	public static byte[] EncodeJpeg (Bitmap bmp, long quality)
	{
		var (rgba, _, w, h) = GetRaw32 (bmp);
		return EncodeJpegRaw (rgba, w, h, quality);
	}

	// SkiaSharp 的原生库依赖 VC++ 2015-2022 运行库；老 Win7 教学机可能没有安装。
	// 一旦原生库加载失败就永久切到 GDI+，避免每帧都抛异常，也避免整个远控画面功能不可用。
	private static bool _skiaUnavailable;

	public static byte[] EncodeJpegRaw (byte[] rgba, int w, int h, long quality)
	{
		if (!_skiaUnavailable) {
			try {
				GCHandle gCHandle = GCHandle.Alloc (rgba, GCHandleType.Pinned);
				try {
					using SKBitmap sKBitmap = new SKBitmap (w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
					sKBitmap.SetPixels (gCHandle.AddrOfPinnedObject ());
					using SKImage sKImage = SKImage.FromBitmap (sKBitmap);
					using SKData sKData = sKImage.Encode (SKEncodedImageFormat.Jpeg, (int)quality);
					return sKData.ToArray ();
				} finally {
					gCHandle.Free ();
				}
			} catch (Exception ex) {
				_skiaUnavailable = true;
				Debug.WriteLine ("[抓屏] SkiaSharp 不可用，JPEG 编码改用 GDI+：" + ex.Message);
			}
		}
		return EncodeJpegGdi (rgba, w, h, quality);
	}

	private static byte[] EncodeJpegGdi (byte[] bgra, int w, int h, long quality)
	{
		using Bitmap bitmap = new Bitmap (w, h, PixelFormat.Format32bppArgb);
		Rectangle rect = new Rectangle (0, 0, w, h);
		BitmapData data = bitmap.LockBits (rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
		try {
			int rowBytes = w * 4;
			if (data.Stride == rowBytes) {
				Marshal.Copy (bgra, 0, data.Scan0, rowBytes * h);
			} else {
				for (int y = 0; y < h; y++) {
					Marshal.Copy (bgra, y * rowBytes, IntPtr.Add (data.Scan0, y * data.Stride), rowBytes);
				}
			}
		} finally {
			bitmap.UnlockBits (data);
		}

		ImageCodecInfo? jpegCodec = null;
		foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders ()) {
			if (codec.FormatID == ImageFormat.Jpeg.Guid) {
				jpegCodec = codec;
				break;
			}
		}

		using MemoryStream stream = new MemoryStream ();
		if (jpegCodec == null) {
			bitmap.Save (stream, ImageFormat.Jpeg);
			return stream.ToArray ();
		}
		using EncoderParameters parameters = new EncoderParameters (1);
		parameters.Param [0] = new EncoderParameter (
			System.Drawing.Imaging.Encoder.Quality, Math.Max (5L, Math.Min (100L, quality)));
		bitmap.Save (stream, jpegCodec, parameters);
		return stream.ToArray ();
	}

	public static byte[] EncodeJpeg (byte[] rgba, int w, int h, long quality)
	{
		return EncodeJpegRaw (rgba, w, h, quality);
	}

	public static byte[] ScaleRaw (byte[] src, int sw, int sh, int dw, int dh, ref byte[]? buf)
	{
		if (dw <= 0 || dh <= 0) {
			return buf ?? Array.Empty<byte> ();
		}
		int num = dw * dh * 4;
		if (buf == null || buf.Length < num) {
			buf = new byte[num];
		}
		if (dw == sw && dh == sh) {
			Buffer.BlockCopy (src, 0, buf, 0, num);
			for (int i = 3; i < num; i += 4) {
				buf [i] = byte.MaxValue;
			}
			return buf;
		}
		float num2 = (float)sw / (float)dw;
		float num3 = (float)sh / (float)dh;
		int num4 = sw * 4;
		int num5 = dw * 4;
		for (int j = 0; j < dh; j++) {
			float num6 = ((float)j + 0.5f) * num3 - 0.5f;
			int num7 = ClampI ((int)num6, 0, sh - 1);
			int num8 = ClampI (num7 + 1, 0, sh - 1);
			float num9 = num6 - (float)num7;
			int num10 = num7 * num4;
			int num11 = num8 * num4;
			int num12 = j * num5;
			for (int k = 0; k < dw; k++) {
				float num13 = ((float)k + 0.5f) * num2 - 0.5f;
				int num14 = ClampI ((int)num13, 0, sw - 1);
				int num15 = ClampI (num14 + 1, 0, sw - 1);
				float num16 = num13 - (float)num14;
				int num17 = num10 + num14 * 4;
				int num18 = num10 + num15 * 4;
				int num19 = num11 + num14 * 4;
				int num20 = num11 + num15 * 4;
				byte b = src [num17];
				byte b2 = src [num17 + 1];
				byte b3 = src [num17 + 2];
				byte b4 = src [num18];
				byte b5 = src [num18 + 1];
				byte b6 = src [num18 + 2];
				byte b7 = src [num19];
				byte b8 = src [num19 + 1];
				byte b9 = src [num19 + 2];
				byte b10 = src [num20];
				byte b11 = src [num20 + 1];
				byte b12 = src [num20 + 2];
				int num21 = (int)((float)(int)b + (float)(b4 - b) * num16 + (float)(b7 - b) * num9 + (float)(b10 - b7 - b4 + b) * num16 * num9);
				int num22 = (int)((float)(int)b2 + (float)(b5 - b2) * num16 + (float)(b8 - b2) * num9 + (float)(b11 - b8 - b5 + b2) * num16 * num9);
				int num23 = (int)((float)(int)b3 + (float)(b6 - b3) * num16 + (float)(b9 - b3) * num9 + (float)(b12 - b9 - b6 + b3) * num16 * num9);
				int num24 = num12 + k * 4;
				buf [num24] = (byte)num21;
				buf [num24 + 1] = (byte)num22;
				buf [num24 + 2] = (byte)num23;
				buf [num24 + 3] = byte.MaxValue;
			}
		}
		return buf;
	}

	/// <summary>
	/// 单帧解码的安全上限。JPEG 头里的宽高是攻击者可控的（这里处理的是远端传来的帧），
	/// 不加限制时 65535x65535 会在原生层申请 17GB 失败 → SKBitmap 像素指针为 NULL，
	/// 随后的解码写入 / Marshal.Copy 直接踩空指针抛 AccessViolationException。
	/// </summary>
	private const int MaxDecodedDimension = 8192;

	private static SKImageInfo? GetSafeJpegImageInfo (SKCodec codec)
	{
		int w = codec.Info.Width;
		int h = codec.Info.Height;
		if (w <= 0 || h <= 0 || w > MaxDecodedDimension || h > MaxDecodedDimension) {
			return null;
		}
		// 额外按总字节数兜一次，防止两个维度都接近上限时乘积溢出
		long bytes = (long)w * h * 4;
		if (bytes <= 0 || bytes > 256L * 1024 * 1024) {
			return null;
		}
		return new SKImageInfo (w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
	}

	public static Bitmap? DecodeJpeg (byte[] jpeg)
	{
		if (!_skiaUnavailable) {
			try {
				return DecodeJpegSkia (jpeg);
			} catch (Exception ex) {
				_skiaUnavailable = true;
				Debug.WriteLine ("[抓屏] SkiaSharp 不可用，JPEG 解码改用 GDI+：" + ex.Message);
			}
		}
		return DecodeJpegGdi (jpeg);
	}

	private static Bitmap? DecodeJpegSkia (byte[] jpeg)
	{
		using SKMemoryStream stream = new SKMemoryStream (jpeg);
		using SKCodec sKCodec = SKCodec.Create (stream);
		if (sKCodec == null) {
			return null;
		}
		SKImageInfo? safeInfo = GetSafeJpegImageInfo (sKCodec);
		if (safeInfo == null) {
			return null;
		}
		SKImageInfo info = safeInfo.Value;
		using SKBitmap sKBitmap = new SKBitmap (info);
		if (sKBitmap.Width <= 0 || sKBitmap.Height <= 0) {
			return null;
		}
		nint srcPixels = sKBitmap.GetPixels ();
		if (srcPixels == IntPtr.Zero) {
			// 原生分配失败：继续解码会写入空指针
			return null;
		}
		if (sKCodec.GetPixels (info, srcPixels) != SKCodecResult.Success) {
			return null;
		}
		Bitmap bitmap = new Bitmap (sKBitmap.Width, sKBitmap.Height, PixelFormat.Format32bppArgb);
		Rectangle rect = new Rectangle (0, 0, sKBitmap.Width, sKBitmap.Height);
		BitmapData bitmapData = bitmap.LockBits (rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
		try {
			using SKPixmap sKPixmap = sKBitmap.PeekPixels ();
			if (sKPixmap == null) {
				bitmap.Dispose ();
				return null;
			}
			nint pixels = sKPixmap.GetPixels ();
			int rowBytes = sKPixmap.RowBytes;
			int stride = bitmapData.Stride;
			if (pixels == IntPtr.Zero || rowBytes <= 0 || stride <= 0) {
				bitmap.Dispose ();
				return null;
			}
			int num = Math.Min (rowBytes, stride);
			if (num <= 0) {
				bitmap.Dispose ();
				return null;
			}
			byte[] array = new byte[num];
			for (int i = 0; i < sKBitmap.Height; i++) {
				Marshal.Copy (IntPtr.Add (pixels, i * rowBytes), array, 0, num);
				Marshal.Copy (array, 0, IntPtr.Add (bitmapData.Scan0, i * stride), num);
			}
		} catch {
			bitmap.Dispose ();
			throw;
		} finally {
			bitmap.UnlockBits (bitmapData);
		}
		return bitmap;
	}

	private static Bitmap? DecodeJpegGdi (byte[] jpeg)
	{
		try {
			using MemoryStream ms = new MemoryStream (jpeg, writable: false);
			using Image img = Image.FromStream (ms, useEmbeddedColorManagement: false, validateImageData: false);
			int w = img.Width;
			int h = img.Height;
			if (w <= 0 || h <= 0 || w > MaxDecodedDimension || h > MaxDecodedDimension) {
				return null;
			}
			if ((long)w * h * 4 > 256L * 1024 * 1024) {
				return null;
			}
			Bitmap bitmap = new Bitmap (w, h, PixelFormat.Format32bppArgb);
			using (Graphics g = Graphics.FromImage (bitmap)) {
				g.DrawImage (img, 0, 0, w, h);
			}
			return bitmap;
		} catch {
			return null;
		}
	}

	public static byte[]? DecodeJpegToBytes (byte[] jpeg)
	{
		if (!_skiaUnavailable) {
			try {
				return DecodeJpegToBytesSkia (jpeg);
			} catch (Exception ex) {
				_skiaUnavailable = true;
				Debug.WriteLine ("[抓屏] SkiaSharp 不可用，JPEG 解码改用 GDI+：" + ex.Message);
			}
		}
		using Bitmap? bitmap = DecodeJpegGdi (jpeg);
		if (bitmap == null) {
			return null;
		}
		byte[]? scratch = null;
		return GetRaw32 (bitmap, ref scratch).bytes;
	}

	private static byte[]? DecodeJpegToBytesSkia (byte[] jpeg)
	{
		using SKMemoryStream stream = new SKMemoryStream (jpeg);
		using SKCodec sKCodec = SKCodec.Create (stream);
		if (sKCodec == null) {
			return null;
		}
		SKImageInfo? safeInfo = GetSafeJpegImageInfo (sKCodec);
		if (safeInfo == null) {
			return null;
		}
		int width = safeInfo.Value.Width;
		int height = safeInfo.Value.Height;
		using SKBitmap sKBitmap = new SKBitmap (safeInfo.Value);
		if (sKBitmap.Width <= 0 || sKBitmap.Height <= 0) {
			return null;
		}
		nint dstPixels = sKBitmap.GetPixels ();
		if (dstPixels == IntPtr.Zero) {
			return null;
		}
		if (sKCodec.GetPixels (safeInfo.Value, dstPixels) != SKCodecResult.Success) {
			return null;
		}
		using SKPixmap sKPixmap = sKBitmap.PeekPixels ();
		if (sKPixmap == null) {
			return null;
		}
		nint pixels = sKPixmap.GetPixels ();
		int rowBytes = sKPixmap.RowBytes;
		if (pixels == IntPtr.Zero || rowBytes < width * 4) {
			return null;
		}
		int num = width * 4;
		byte[] array = new byte[num * height];
		byte[] array2 = new byte[num];
		for (int i = 0; i < height; i++) {
			Marshal.Copy (IntPtr.Add (pixels, i * rowBytes), array2, 0, num);
			Buffer.BlockCopy (array2, 0, array, i * num, num);
		}
		return array;
	}

	public static byte[] DeflateRaw (byte[] raw, int length)
	{
		using MemoryStream memoryStream = new MemoryStream (Math.Max (4096, length / 3));
		using (DeflateStream deflateStream = new DeflateStream (memoryStream, CompressionLevel.Fastest, leaveOpen: true)) {
			deflateStream.Write (raw, 0, length);
		}
		return memoryStream.ToArray ();
	}

	public static byte[]? InflateRaw (byte[] data, int offset, int length, int expected)
	{
		try {
			using MemoryStream stream = new MemoryStream (data, offset, length, writable: false);
			using DeflateStream deflateStream = new DeflateStream (stream, CompressionMode.Decompress);
			byte[] array = new byte[expected];
			int i;
			int num;
			for (i = 0; i < expected; i += num) {
				num = deflateStream.Read (array, i, expected - i);
				if (num <= 0) {
					break;
				}
			}
			return (i == expected) ? array : null;
		} catch {
			return null;
		}
	}

	[DllImport ("user32.dll")]
	private static extern uint GetGuiResources (nint hProcess, uint uiFlags);

	public static (uint gdi, uint user) GuiResourceCounts ()
	{
		try {
			using Process process = Process.GetCurrentProcess ();
			return (gdi: GetGuiResources (process.Handle, 0u), user: GetGuiResources (process.Handle, 1u));
		} catch {
			return (gdi: 0u, user: 0u);
		}
	}

	public static (byte[] bytes, int stride, int width, int height) GetRaw32 (Bitmap bmp)
	{
		byte[] buf = null;
		return GetRaw32 (bmp, ref buf);
	}

	public static (byte[] bytes, int stride, int width, int height) GetRaw32 (Bitmap bmp, ref byte[]? buf)
	{
		Rectangle rect = new Rectangle (0, 0, bmp.Width, bmp.Height);
		BitmapData bitmapData = bmp.LockBits (rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
		try {
			int stride = bitmapData.Stride;
			int num = Math.Abs (stride) * bmp.Height;
			if (buf == null || buf.Length < num) {
				buf = new byte[num];
			}
			Marshal.Copy (bitmapData.Scan0, buf, 0, num);
			return (bytes: buf, stride: stride, width: bmp.Width, height: bmp.Height);
		} finally {
			bmp.UnlockBits (bitmapData);
		}
	}

	public static List<Rectangle> CompareBlocks (byte[] prev, byte[] cur, int width, int height, int blockSize)
	{
		List<Rectangle> list = new List<Rectangle> ();
		int num = width * 4;
		int num2 = (width + blockSize - 1) / blockSize;
		int num3 = (height + blockSize - 1) / blockSize;
		for (int i = 0; i < num3; i++) {
			int num4 = i * blockSize;
			int num5 = Math.Min (blockSize, height - num4);
			for (int j = 0; j < num2; j++) {
				int num6 = j * blockSize;
				int num7 = Math.Min (blockSize, width - num6);
				bool flag = false;
				for (int k = 0; k < num5; k++) {
					int start = (num4 + k) * num + num6 * 4;
					if (!cur.AsSpan (start, num7 * 4).SequenceEqual (prev.AsSpan (start, num7 * 4))) {
						flag = true;
						break;
					}
				}
				if (flag) {
					list.Add (new Rectangle (num6, num4, num7, num5));
				}
			}
		}
		return list;
	}

	public static List<Rectangle> MergeBlocks (List<Rectangle> blocks, int blockSize, int width, int height)
	{
		if (blocks.Count <= 1) {
			return blocks;
		}
		if (blockSize <= 0) {
			return blocks;
		}
		int num = (width + blockSize - 1) / blockSize;
		int num2 = (height + blockSize - 1) / blockSize;
		bool[,] array = new bool[num2, num];
		foreach (Rectangle block in blocks) {
			int num3 = block.X / blockSize;
			int num4 = block.Y / blockSize;
			if (num3 >= 0 && num3 < num && num4 >= 0 && num4 < num2) {
				array [num4, num3] = true;
			}
		}
		List<Rectangle> list = new List<Rectangle> ();
		for (int i = 0; i < num2; i++) {
			for (int j = 0; j < num; j++) {
				if (!array [i, j]) {
					continue;
				}
				int k;
				for (k = 1; j + k < num && array [i, j + k]; k++) {
				}
				int l;
				for (l = 1; i + l < num2; l++) {
					bool flag = true;
					for (int m = 0; m < k; m++) {
						if (!array [i + l, j + m]) {
							flag = false;
							break;
						}
					}
					if (!flag) {
						break;
					}
				}
				for (int n = 0; n < l; n++) {
					for (int num5 = 0; num5 < k; num5++) {
						array [i + n, j + num5] = false;
					}
				}
				int num6 = j * blockSize;
				int num7 = i * blockSize;
				list.Add (new Rectangle (num6, num7, Math.Min (k * blockSize, width - num6), Math.Min (l * blockSize, height - num7)));
			}
		}
		return list;
	}
}
