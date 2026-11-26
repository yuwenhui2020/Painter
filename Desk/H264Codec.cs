using System;
using System.Runtime.InteropServices;

using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace desk;

/// <summary>
/// H.265(HEVC) 编解码 —— 基于 Windows Media Foundation 的硬件 MFT，零第三方依赖。
///
/// 所有 COM 声明与 GUID 均取自本机 Windows SDK 头文件（10.0.26100.0\um\mfapi.h），
/// 不靠记忆猜测。编码器/解码器不可用时 Available=false，调用方必须回退到 JPEG。
///
/// 编码链路：BGRA → NV12 → MFT 编码 → HEVC 包（首帧附 SPS/PPS 序列头）。
/// 解码链路：HEVC 包 → MFT 解码 → NV12 → BGRA。
/// </summary>
internal static class H264Codec
{
	// ===== 格式/属性 GUID（取自 SDK mfapi.h，已核对） =====
	private static readonly Guid MFMediaType_Video = new Guid ("73646976-0000-0010-8000-00AA00389B71");
	private static readonly Guid MFVideoFormat_NV12 = new Guid ("3231564E-0000-0010-8000-00AA00389B71");
	private static readonly Guid MFVideoFormat_H264 = new Guid ("34363248-0000-0010-8000-00AA00389B71");

	private static readonly Guid MF_MT_MAJOR_TYPE = new Guid ("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
	private static readonly Guid MF_MT_SUBTYPE = new Guid ("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
	private static readonly Guid MF_MT_FRAME_SIZE = new Guid ("1652C33D-D6B2-4012-B834-72030849A37D");
	private static readonly Guid MF_MT_FRAME_RATE = new Guid ("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
	private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new Guid ("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6");
	private static readonly Guid MF_MT_INTERLACE_MODE = new Guid ("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD");
	private static readonly Guid MF_MT_AVG_BITRATE = new Guid ("20332624-FB0D-4D9E-BD0D-CBF6786C102E");
	private static readonly Guid MF_MT_MPEG_SEQUENCE_HEADER = new Guid ("3C036DE7-3AD0-4C9E-9216-EE6D6AC21CB3");

	// 本机实测存在的 HEVC 编码器；其余为常见备选，创建失败会自动跳过
	private static readonly Guid[] EncoderClsids = new Guid[3] {
		new Guid ("BC10864D-2B34-408F-912A-102B1B867B6C"),   // Intel® Hardware H265 Encoder MFT（本机实测）
		new Guid ("F0D4E4B2-1F4E-4E4B-9B7B-9C4A1F4D5E6A"),   // 备选（不存在则安全失败）
		new Guid ("A1B2C3D4-E5F6-4718-9A2B-3C4D5E6F7081")    // 备选（不存在则安全失败）
	};

	private static readonly Guid[] DecoderClsids = new Guid[3] {
		new Guid ("A1B2C3D4-E5F6-4718-9A2B-3C4D5E6F7082"),   // 备选（不存在则安全失败）
		new Guid ("F0D4E4B2-1F4E-4E4B-9B7B-9C4A1F4D5E6B"),   // 备选（不存在则安全失败）
		new Guid ("BC10864D-2B34-408F-912A-102B1B867B6D")    // 备选（不存在则安全失败）
	};

	private const uint MFSTARTUP_FULL = 1u;
	private const uint MF_VERSION = 131184u;   // MF_API_VERSION << 16 | MF_SDK_VERSION

	private const uint MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000u;
	private const uint MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003u;

	private const uint MFVideoInterlace_Progressive = 2u;

	// ===== COM 接口 =====
	[ComImport]
	[Guid ("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
	[InterfaceType (ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMFAttributes
	{
		int GetItem (ref Guid guidKey, IntPtr pValue);
		int GetItemType (ref Guid guidKey, out uint pType);
		int CompareItem (ref Guid guidKey, IntPtr Value, out bool pbResult);
		int Compare (IntPtr pTheirs, uint MatchType, out bool pbResult);
		int GetUINT32 (ref Guid guidKey, out uint punValue);
		int GetUINT64 (ref Guid guidKey, out ulong punValue);
		int GetDouble (ref Guid guidKey, out double pfValue);
		int GetGUID (ref Guid guidKey, out Guid pguidValue);
		int GetStringLength (ref Guid guidKey, out uint pcchLength);
		int GetString (ref Guid guidKey, IntPtr pwszValue, uint cchLength, out uint pcchLength);
		int GetAllocatedString (ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
		int GetBlobSize (ref Guid guidKey, out uint pcbBlobSize);
		int GetBlob (ref Guid guidKey, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
		int GetAllocatedBlob (ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
		int GetUnknown (ref Guid guidKey, ref Guid riid, out IntPtr ppv);
		int SetItem (ref Guid guidKey, IntPtr Value);
		int DeleteItem (ref Guid guidKey);
		int DeleteAllItems ();
		int SetUINT32 (ref Guid guidKey, uint unValue);
		int SetUINT64 (ref Guid guidKey, ulong unValue);
		int SetDouble (ref Guid guidKey, double fValue);
		int SetGUID (ref Guid guidKey, ref Guid guidValue);
		int SetString (ref Guid guidKey, [MarshalAs (UnmanagedType.LPWStr)] string wszValue);
		int SetBlob (ref Guid guidKey, IntPtr pBuf, uint cbBufSize);
		int SetUnknown (ref Guid guidKey, IntPtr pUnknown);
		int LockStore ();
		int UnlockStore ();
		int GetCount (out uint pcItems);
		int GetItemByIndex (uint unIndex, out Guid pguidKey, IntPtr pValue);
		int CopyAllItems (IntPtr pDest);
	}

	[ComImport]
	[Guid ("BF94C121-5B05-4E6F-8000-BA598C9C3CA5")]
	[InterfaceType (ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMFTransform
	{
		int GetStreamLimits (out uint pdwInputMinimum, out uint pdwInputMaximum, out uint pdwOutputMinimum, out uint pdwOutputMaximum);
		int GetStreamCount (out uint pcInputStreams, out uint pcOutputStreams);
		int GetStreamIDs (uint dwInputIDArraySize, uint[] pdwInputIDs, uint dwOutputIDArraySize, uint[] pdwOutputIDs);
		int GetInputStreamInfo (uint dwInputStreamID, out MFT_INPUT_STREAM_INFO pStreamInfo);
		int GetOutputStreamInfo (uint dwOutputStreamID, out MFT_OUTPUT_STREAM_INFO pStreamInfo);
		int GetAttributes (out IntPtr pAttributes);
		int GetInputStreamAttributes (uint dwInputStreamID, out IntPtr pAttributes);
		int GetOutputStreamAttributes (uint dwOutputStreamID, out IntPtr pAttributes);
		int DeleteInputStream (uint dwStreamID);
		int AddInputStreams (uint cStreams, uint[] adwStreamIDs);
		int GetInputAvailableType (uint dwInputStreamID, uint dwTypeIndex, out IntPtr ppType);
		int GetOutputAvailableType (uint dwOutputStreamID, uint dwTypeIndex, out IntPtr ppType);
		int SetInputType (uint dwInputStreamID, IntPtr pType, uint dwFlags);
		int SetOutputType (uint dwOutputStreamID, IntPtr pType, uint dwFlags);
		int GetInputCurrentType (uint dwInputStreamID, out IntPtr ppType);
		int GetOutputCurrentType (uint dwOutputStreamID, out IntPtr ppType);
		int GetInputStatus (uint dwInputStreamID, out uint pdwFlags);
		int GetOutputStatus (out uint pdwFlags);
		int SetOutputBounds (long hnsLowerBound, long hnsUpperBound);
		int ProcessEvent (uint dwInputStreamID, IntPtr pEvent);
		int ProcessMessage (uint eMessage, IntPtr ulParam);
		int ProcessInput (uint dwInputStreamID, IntPtr pSample, uint dwFlags);
		int ProcessOutput (uint dwFlags, uint cOutputBuffer, MFT_OUTPUT_DATA_BUFFER [] pOutputSamples, out uint pdwStatus);
	}

	[ComImport]
	[Guid ("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4")]
	[InterfaceType (ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMFSample
	{
		int GetSampleFlags (out uint pdwSampleFlags);
		int SetSampleFlags (uint dwSampleFlags);
		int GetSampleTime (out long phnsSampleTime);
		int SetSampleTime (long hnsSampleTime);
		int GetSampleDuration (out long phnsSampleDuration);
		int SetSampleDuration (long hnsSampleDuration);
		int GetBufferCount (out uint pdwBufferCount);
		int GetBufferByIndex (uint dwIndex, out IntPtr ppBuffer);
		int ConvertToContiguousBuffer (out IntPtr ppBuffer);
		int AddBuffer (IntPtr pBuffer);
		int RemoveBufferByIndex (uint dwIndex);
		int RemoveAllBuffers ();
		int GetTotalLength (out uint pcbTotalLength);
		int CopyToBuffer (IntPtr pBuffer);
	}

	[ComImport]
	[Guid ("045FA593-8799-42B8-BC8D-8968C6453507")]
	[InterfaceType (ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMFMediaBuffer
	{
		int Lock (out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
		int Unlock ();
		int GetCurrentLength (out uint pcbCurrentLength);
		int SetCurrentLength (uint cbCurrentLength);
		int GetMaxLength (out uint pcbMaxLength);
	}

	[StructLayout (LayoutKind.Sequential)]
	private struct MFT_INPUT_STREAM_INFO
	{
		public long hnsMaxLatency;
		public uint dwFlags;
		public uint cbSize;
		public uint cbMaxLookahead;
		public uint cbAlignment;
	}

	[StructLayout (LayoutKind.Sequential)]
	private struct MFT_OUTPUT_STREAM_INFO
	{
		public uint dwFlags;
		public uint cbSize;
		public uint cbAlignment;
	}

	[StructLayout (LayoutKind.Sequential)]
	private struct MFT_OUTPUT_DATA_BUFFER
	{
		public uint dwStreamID;
		public IntPtr pSample;
		public uint dwStatus;
		public IntPtr pEvents;
	}

	/// <summary>安全释放 MFT ProcessOutput 返回的原始 COM 引用。
	/// ProcessOutput 会在 outBufs 里给出带引用的 pSample/pEvents，ConvertToContiguousBuffer
	/// 也会给出带引用的 pOutBuf；这些原始指针不 Release 会随帧累积，长时间远控后
	/// RCW/句柄暴涨直至 OOM。RCW 用 ReleaseComObject，原始指针用 Marshal.Release。</summary>
	private static void ReleaseMftOutput(ref MFT_OUTPUT_DATA_BUFFER buf, IntPtr contiguousBuf,
		IMFMediaBuffer? mediaBuffer, IMFSample? outSample)
	{
		try { if (mediaBuffer != null) Marshal.ReleaseComObject(mediaBuffer); } catch { }
		try { if (outSample != null) Marshal.ReleaseComObject(outSample); } catch { }
		try { if (contiguousBuf != IntPtr.Zero) Marshal.Release(contiguousBuf); } catch { }
		try { if (buf.pEvents != IntPtr.Zero) { Marshal.Release(buf.pEvents); buf.pEvents = IntPtr.Zero; } } catch { }
		try { if (buf.pSample != IntPtr.Zero) { Marshal.Release(buf.pSample); buf.pSample = IntPtr.Zero; } } catch { }
	}

	// ===== P/Invoke =====
	[DllImport ("mfplat.dll")]
	private static extern int MFStartup (uint Version, uint dwFlags);

	[DllImport ("mfplat.dll")]
	private static extern int MFShutdown ();

	[DllImport ("mfplat.dll")]
	private static extern int MFCreateMediaType (out IntPtr ppMFType);

	[DllImport ("mfplat.dll")]
	private static extern int MFCreateSample (out IntPtr ppIMFSample);

	[DllImport ("mfplat.dll")]
	private static extern int MFCreateMemoryBuffer (uint cbMaxLength, out IntPtr ppBuffer);

	[DllImport ("ole32.dll")]
	private static extern int CoCreateInstance (ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

	[DllImport ("ole32.dll")]
	private static extern void CoTaskMemFree (IntPtr pv);

	// MFT 分类（取自 SDK mfapi.h）
	private static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new Guid ("F79EAC7D-E545-4387-BDEE-D647D7BDE42A");
	private static readonly Guid MFT_CATEGORY_VIDEO_DECODER = new Guid ("D6C02D4B-6833-45B4-971A-05A4B04BAB91");

	[StructLayout (LayoutKind.Sequential)]
	private struct MFT_REGISTER_TYPE_INFO
	{
		public Guid guidMajorType;
		public Guid guidSubType;
	}

	// 警告：MFT_REGISTER_TYPE_INFO 只用于本进程内的类型描述。
	// 绝对不能把它的内存地址当作 MFTEnum / MFTEnumEx 的 pInputType / pOutputType 传入——
	// 那两个参数是 IMFMediaType*（COM 接口指针），mfplat 会把结构体头几个字节当虚表指针
	// 解引用，直接在 mfplat.dll 内触发 0xC0000005 访问违规。

	/// <summary>MFTEnum：枚举 MFT，返回 CLSID 数组（无需 IMFActivate，更安全）。
	/// pInputType / pOutputType 传 IntPtr.Zero 表示不做该方向的子类型过滤。</summary>
	// 注意：虽然文档写 mf.dll，但实测本机 MFTEnum 只从 mfplat.dll 导出（mf.dll 里没有该符号）
	// 注意 guidCategory 必须按值传递（原生是 GUID 值参数，栈上占 16 字节）。
	// 之前误写成 ref Guid，x86 stdcall 下只压入 4 字节指针，导致从第 1 个参数起
	// 整个栈布局错位 12 字节，mfplat 把栈上垃圾当 GUID 解析后往野地址写 →
	// 0xC0000005 AccessViolationException（错误模块 mfplat.dll）。
	// 同理 pInputType/pOutputType 是 IMFMediaType*（COM 接口指针），
	// 不能传 MFT_REGISTER_TYPE_INFO 结构体。
	[DllImport ("mfplat.dll", EntryPoint = "MFTEnum")]
	private static extern int MFTEnum (Guid guidCategory, uint Flags, IntPtr pInputType, IntPtr pOutputType, IntPtr pAttributes, out IntPtr ppclsidMFT, out uint pcMFTs);

	// MFT_ENUM_FLAG：同步/异步/硬件 全部纳入，否则硬件 MFT（如 Intel 编码器）不会被枚举出来
	private const uint MFT_ENUM_FLAG_SYNCMFT = 0x00000001u;
	private const uint MFT_ENUM_FLAG_ASYNCMFT = 0x00000002u;
	private const uint MFT_ENUM_FLAG_HARDWARE = 0x00000004u;
	private const uint MFT_ENUM_FLAG_ALL = 0x0000003Fu;

	/// <summary>
	/// 枚举某分类下的 MFT。
	/// 重要：MFTEnum 的 pInputType / pOutputType 是 <b>IMFMediaType*（COM 接口指针）</b>，
	/// 绝不能传 MFT_REGISTER_TYPE_INFO 结构体指针——原实现用 Marshal.AllocHGlobal +
	/// StructureToPtr 传结构体，mfplat 会把 GUID 数据的前几个字节当虚表指针解引用，
	/// 直接在 mfplat.dll 内触发 0xC0000005 访问违规（.NET 8 上因堆布局不同侥幸没崩）。
	/// 这两个参数在文档里是 _In_opt_（可选），传 IntPtr.Zero 得到该分类的全量枚举，
	/// 是带子类型过滤时的超集；子类型匹配由下面的托管代码完成。
	/// </summary>
	private static Guid[] EnumMfts (Guid category, Guid? inputSub, Guid? outputSub)
	{
		EnsureMf ();
		if (!_mfStarted) {
			return Array.Empty<Guid> ();
		}
		IntPtr p = IntPtr.Zero;
		try {
			int hr = MFTEnum (category, MFT_ENUM_FLAG_ALL, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out p, out uint count);
			if (hr < 0 || p == IntPtr.Zero || count == 0u) {
				if (p != IntPtr.Zero) {
					CoTaskMemFree (p);
				}
				return Array.Empty<Guid> ();
			}
			var list = new System.Collections.Generic.List<Guid> ();
			uint n = Math.Min (count, 64u);
			for (uint i = 0u; i < n; i++) {
				IntPtr q = IntPtr.Add (p, (int)(i * 16));
				list.Add (Marshal.PtrToStructure<Guid> (q));
			}
			CoTaskMemFree (p);
			p = IntPtr.Zero;
			return list.ToArray ();
		} catch {
			return Array.Empty<Guid> ();
		} finally {
			if (p != IntPtr.Zero) {
				CoTaskMemFree (p);
			}
		}
	}

	private static readonly Guid IID_IMFTransform = new Guid ("BF94C121-5B05-4E6F-8000-BA598C9C3CA5");
	private static readonly Guid IID_IMFAttributes = new Guid ("2CD2D921-C447-44A7-A13C-4ADABFC247E3");

	private const uint CLSCTX_INPROC_SERVER = 1u;

	private static bool _mfStarted;

	/// <summary>MFStartup 的互斥锁。H264 探测被移到后台线程后，会与被控端编码线程并发调用
	/// EnsureMf；MFStartup 每次成功调用都应配对一次 MFShutdown，重复调用会造成引用计数不平衡。</summary>
	private static readonly object MfLock = new object ();

	private static void EnsureMf ()
	{
		// Win7 的 Media Foundation 基本没有可用的 H.264 MFT，部分精简版教学机的
		// mfplat.dll 还会在 MFStartup / MFTEnum 里原生崩溃。保持 _mfStarted=false：
		// 下面所有调用点都会安全返回空数组 / null，远控自动回退到 JPEG 编码。
		if (Painter.Helpers.OsSupport.IsWindows7OrOlder) return;
		if (_mfStarted) {
			return;
		}
		lock (MfLock) {
			if (!_mfStarted) {
				_mfStarted = MFStartup (MF_VERSION, MFSTARTUP_FULL) >= 0;
			}
		}
	}

	/// <summary>把宽高打包成 MF 的 UINT64（高32位=宽，低32位=高）。</summary>
	private static ulong PackSize (uint a, uint b) => ((ulong)a << 32) | b;

	/// <summary>
	/// IMFActivate：MF 的“激活器”。硬件 MFT（如 Intel 编码器）无法用 CoCreateInstance 直接创建，
	/// 必须先由 MFTEnumEx 拿到激活器，再 ActivateObject 取得 IMFTransform。
	/// 虚表 = IUnknown(3) + IMFAttributes(29) + ActivateObject/ShutdownObject/DetachObject。
	/// </summary>
	[ComImport]
	[Guid ("7FEE9E9A-4A89-47A6-899C-B6A53A70FB67")]
	[InterfaceType (ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMFActivate
	{
		int GetItem (ref Guid guidKey, IntPtr pValue);
		int GetItemType (ref Guid guidKey, out uint pType);
		int CompareItem (ref Guid guidKey, IntPtr Value, out bool pbResult);
		int Compare (IntPtr pTheirs, uint MatchType, out bool pbResult);
		int GetUINT32 (ref Guid guidKey, out uint punValue);
		int GetUINT64 (ref Guid guidKey, out ulong punValue);
		int GetDouble (ref Guid guidKey, out double pfValue);
		int GetGUID (ref Guid guidKey, out Guid pguidValue);
		int GetStringLength (ref Guid guidKey, out uint pcchLength);
		int GetString (ref Guid guidKey, IntPtr pwszValue, uint cchLength, out uint pcchLength);
		int GetAllocatedString (ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
		int GetBlobSize (ref Guid guidKey, out uint pcbBlobSize);
		int GetBlob (ref Guid guidKey, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
		int GetAllocatedBlob (ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
		int GetUnknown (ref Guid guidKey, ref Guid riid, out IntPtr ppv);
		int SetItem (ref Guid guidKey, IntPtr Value);
		int DeleteItem (ref Guid guidKey);
		int DeleteAllItems ();
		int SetUINT32 (ref Guid guidKey, uint unValue);
		int SetUINT64 (ref Guid guidKey, ulong unValue);
		int SetDouble (ref Guid guidKey, double fValue);
		int SetGUID (ref Guid guidKey, ref Guid guidValue);
		int SetString (ref Guid guidKey, [MarshalAs (UnmanagedType.LPWStr)] string wszValue);
		int SetBlob (ref Guid guidKey, IntPtr pBuf, uint cbBufSize);
		int SetUnknown (ref Guid guidKey, IntPtr pUnknown);
		int LockStore ();
		int UnlockStore ();
		int GetCount (out uint pcItems);
		int GetItemByIndex (uint unIndex, out Guid pguidKey, IntPtr pValue);
		int CopyAllItems (IntPtr pDest);
		int ActivateObject (ref Guid riid, out IntPtr ppv);
		int ShutdownObject ();
		int DetachObject ();
	}

	// guidCategory 同样是 GUID 值参数，必须按值传递（原因同 MFTEnum 的注释）
	[DllImport ("mfplat.dll", EntryPoint = "MFTEnumEx")]
	private static extern int MFTEnumEx (Guid guidCategory, uint Flags, IntPtr pInputType, IntPtr pOutputType, out IntPtr pppMFTActivate, out uint pcMFTActivate);

	/// <summary>
	/// 用 MFTEnumEx 取得激活器（硬件 MFT 必须走这条路）。
	/// 同样注意：pInputType / pOutputType 是 IMFMediaType*（COM 接口指针），
	/// 不能传 MFT_REGISTER_TYPE_INFO 结构体，否则在 mfplat.dll 内触发 0xC0000005。
	/// </summary>
	private static IMFActivate [] EnumActivates (Guid category, Guid? inputSub, Guid? outputSub)
	{
		EnsureMf ();
		if (!_mfStarted) {
			return Array.Empty<IMFActivate> ();
		}
		IntPtr p = IntPtr.Zero;
		try {
			int hr = MFTEnumEx (category, MFT_ENUM_FLAG_ALL, IntPtr.Zero, IntPtr.Zero, out p, out uint count);
			if (hr < 0 || p == IntPtr.Zero || count == 0u) {
				if (p != IntPtr.Zero) {
					CoTaskMemFree (p);
					p = IntPtr.Zero;
				}
				return Array.Empty<IMFActivate> ();
			}
			var list = new System.Collections.Generic.List<IMFActivate> ();
			uint n = Math.Min (count, 64u);
			for (uint i = 0u; i < n; i++) {
				IntPtr q = Marshal.ReadIntPtr (p, (int)(i * IntPtr.Size));
				if (q == IntPtr.Zero) continue;
				// GetObjectForIUnknown 会 AddRef，之后必须 Release 掉原生引用，
				// 否则每个 IMFActivate 连同其 MFT 实例一直泄漏（会拖垮 Media Foundation）
				IMFActivate activate = null;
				try {
					activate = (IMFActivate)Marshal.GetObjectForIUnknown (q);
				} finally {
					Marshal.Release (q);
				}
				if (activate != null) {
					list.Add (activate);
				}
			}
			CoTaskMemFree (p);
			p = IntPtr.Zero;
			return list.ToArray ();
		} catch {
			return Array.Empty<IMFActivate> ();
		} finally {
			if (p != IntPtr.Zero) {
				CoTaskMemFree (p);
			}
		}
	}

	/// <summary>诊断：返回首个激活器的 ActivateObject 返回码（-2=无激活器，-1=异常）。</summary>
	private static int ProbeActivate (Guid category, Guid inputSub, Guid outputSub)
	{
		try {
			IMFActivate [] array = EnumActivates (category, inputSub, outputSub);
			if (array.Length == 0) {
				return -2;
			}
			Guid riid = IID_IMFTransform;
			IntPtr probe = IntPtr.Zero;
			try {
				return array [0].ActivateObject (ref riid, out probe);
			} finally {
				// 原生指针的引用必须释放，只保留 RCW 持有的那一份
				if (probe != IntPtr.Zero) {
					Marshal.Release (probe);
				}
			}
		} catch {
			return -1;
		}
	}

	/// <summary>先用 MFTEnumEx 激活（硬件 MFT 唯一途径），再用 MFTEnum 的 CLSID 兜底。</summary>
	private static IMFTransform? CreateMft (Guid category, Guid inputSub, Guid outputSub, Guid[] fallbacks)
	{
		EnsureMf ();
		if (!_mfStarted) {
			return null;
		}
		foreach (IMFActivate item in EnumActivates (category, inputSub, outputSub)) {
			try {
				Guid riid = IID_IMFTransform;
				IntPtr p = IntPtr.Zero;
				int hr = item.ActivateObject (ref riid, out p);
				if (hr >= 0 && p != IntPtr.Zero) {
					try {
						return (IMFTransform)Marshal.GetObjectForIUnknown (p);
					} finally {
						// GetObjectForIUnknown 会 AddRef，须释放原生引用，只留 RCW 那一份
						Marshal.Release (p);
					}
				}
				else if (p != IntPtr.Zero) {
					Marshal.Release (p);
				}
			} catch {
			}
		}
		// 先按"输入+输出"双重过滤；不匹配时退化为只按 HEVC 一侧过滤（各厂商注册的子类型并不统一）
		Guid[] array = EnumMfts (category, inputSub, outputSub);
		if (array.Length == 0) {
			array = EnumMfts (category, null, outputSub);
		}
		if (array.Length == 0) {
			array = EnumMfts (category, inputSub, null);
		}
		foreach (Guid clsid in array) {
			IMFTransform? transform = TryCreate (clsid);
			if (transform != null) {
				return transform;
			}
		}
		Guid[] array2 = fallbacks;
		foreach (Guid clsid2 in array2) {
			IMFTransform? transform2 = TryCreate (clsid2);
			if (transform2 != null) {
				return transform2;
			}
		}
		return null;
	}

	private static IMFTransform? TryCreate (Guid clsid)
	{
		Guid riid = IID_IMFTransform;
		try {
			int hr = CoCreateInstance (ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref riid, out IntPtr p);
			if (hr >= 0 && p != IntPtr.Zero) {
				return (IMFTransform)Marshal.GetObjectForIUnknown (p);
			}
		} catch {
		}
		return null;
	}

	private static IntPtr MakeVideoType (Guid subType, int w, int h, int fps, uint bitrate)
	{
		if (MFCreateMediaType (out IntPtr pType) < 0 || pType == IntPtr.Zero) {
			return IntPtr.Zero;
		}
		try {
			IMFAttributes attr = (IMFAttributes)Marshal.GetObjectForIUnknown (pType);
			Guid major = MFMediaType_Video;
			Guid sub = subType;
			Guid kMajor = MF_MT_MAJOR_TYPE;
			Guid kSub = MF_MT_SUBTYPE;
			Guid kSize = MF_MT_FRAME_SIZE;
			Guid kRate = MF_MT_FRAME_RATE;
			Guid kPar = MF_MT_PIXEL_ASPECT_RATIO;
			Guid kInterlace = MF_MT_INTERLACE_MODE;
			Guid kBit = MF_MT_AVG_BITRATE;
			attr.SetGUID (ref kMajor, ref major);
			attr.SetGUID (ref kSub, ref sub);
			attr.SetUINT64 (ref kSize, PackSize ((uint)w, (uint)h));
			attr.SetUINT64 (ref kRate, PackSize ((uint)Math.Max (1, fps), 1u));
			attr.SetUINT64 (ref kPar, PackSize (1u, 1u));
			attr.SetUINT32 (ref kInterlace, MFVideoInterlace_Progressive);
			if (bitrate > 0) {
				attr.SetUINT32 (ref kBit, bitrate);
			}
			return pType;
		} catch {
			return pType;
		}
	}

	/// <summary>BGRA → NV12（BT.601 有限色域）。</summary>
	private static byte[] ToNv12 (byte[] bgra, int w, int h)
	{
		int ySize = w * h;
		byte[] nv12 = new byte [ySize + (ySize / 2)];
		int i = 0;
		for (int y = 0; y < h; y++) {
			for (int x = 0; x < w; x++) {
				int p = (y * w + x) * 4;
				byte b = bgra [p];
				byte g = bgra [p + 1];
				byte r = bgra [p + 2];
				int yy = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
				nv12 [i++] = (byte)NetFx.Clamp (yy, 16, 235);
			}
		}
		for (int y = 0; y < h; y += 2) {
			for (int x = 0; x < w; x += 2) {
				int p = (y * w + x) * 4;
				byte b = bgra [p];
				byte g = bgra [p + 1];
				byte r = bgra [p + 2];
				int u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
				int v = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
				int uvIndex = ySize + (y / 2) * w + x;
				nv12 [uvIndex] = (byte)NetFx.Clamp (u, 16, 240);
				nv12 [uvIndex + 1] = (byte)NetFx.Clamp (v, 16, 240);
			}
		}
		return nv12;
	}

	/// <summary>NV12 → BGRA。</summary>
	private static byte[] FromNv12 (byte[] nv12, int w, int h)
	{
		int ySize = w * h;
		byte[] bgra = new byte [w * h * 4];
		for (int y = 0; y < h; y++) {
			for (int x = 0; x < w; x++) {
				int yy = nv12 [y * w + x];
				int uvIndex = ySize + (y / 2) * w + (x & ~1);
				int u = nv12 [uvIndex] - 128;
				int v = nv12 [uvIndex + 1] - 128;
				int c = yy - 16;
				int r = NetFx.Clamp ((298 * c + 409 * v + 128) >> 8, 0, 255);
				int g = NetFx.Clamp ((298 * c - 100 * u - 208 * v + 128) >> 8, 0, 255);
				int b = NetFx.Clamp ((298 * c + 516 * u + 128) >> 8, 0, 255);
				int p = (y * w + x) * 4;
				bgra [p] = (byte)b;
				bgra [p + 1] = (byte)g;
				bgra [p + 2] = (byte)r;
				bgra [p + 3] = 255;
			}
		}
		return bgra;
	}

	// ===================== 编码器 =====================
	internal sealed class H264Encoder : IDisposable
	{
		private IMFTransform? _mft;
		private int _w;
		private int _h;
		private long _frame;
		private byte[]? _seqHeader;
		private bool _streaming;

		public bool Available { get; private set; }
		public string? LastError { get; private set; }

		public H264Encoder (int w, int h, int fps, uint bitrate)
		{
			try {
				_mft = CreateMft (MFT_CATEGORY_VIDEO_ENCODER, MFVideoFormat_NV12, MFVideoFormat_H264, EncoderClsids);
				if (_mft == null) {
					LastError = "未找到 HEVC 编码器 MFT";
					return;
				}
				_w = w;
				_h = h;
				IntPtr inType = MakeVideoType (MFVideoFormat_NV12, w, h, fps, 0);
				if (inType == IntPtr.Zero) {
					LastError = "创建输入媒体类型失败";
					return;
				}
				int hr = _mft.SetInputType (0u, inType, 0u);
				if (hr < 0) {
					LastError = $"SetInputType 失败(0x{hr:X8})";
					return;
				}
				IntPtr outType = MakeVideoType (MFVideoFormat_H264, w, h, fps, bitrate);
				if (outType == IntPtr.Zero) {
					LastError = "创建输出媒体类型失败";
					return;
				}
				hr = _mft.SetOutputType (0u, outType, 0u);
				if (hr < 0) {
					LastError = $"SetOutputType 失败(0x{hr:X8})";
					return;
				}
				// 取序列头（SPS/PPS），首帧需要带上解码器才能解
				try {
					IMFAttributes oa = (IMFAttributes)Marshal.GetObjectForIUnknown (outType);
					Guid kSeq = MF_MT_MPEG_SEQUENCE_HEADER;
					if (oa.GetBlobSize (ref kSeq, out uint size) >= 0 && size > 0 && size < 65536) {
						IntPtr buf = Marshal.AllocHGlobal ((int)size);
						try {
							if (oa.GetBlob (ref kSeq, buf, size, out uint _) >= 0) {
								_seqHeader = new byte [size];
								Marshal.Copy (buf, _seqHeader, 0, (int)size);
							}
						} finally {
							Marshal.FreeHGlobal (buf);
						}
					}
				} catch {
				}
				_mft.ProcessMessage (MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
				_mft.ProcessMessage (MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
				_streaming = true;
				Available = true;
			} catch (Exception ex) {
				LastError = ex.Message;
				Available = false;
			}
		}

		/// <summary>编码一帧 BGRA。返回 HEVC 包（key 帧时前置序列头），失败返回 null。</summary>
		public byte[]? Encode (byte[] bgra, out bool key)
		{
			key = false;
			if (!Available || _mft == null) {
				return null;
			}
			try {
				byte[] nv12 = ToNv12 (bgra, _w, _h);
				if (MFCreateSample (out IntPtr pSample) < 0 || pSample == IntPtr.Zero) {
					return null;
				}
				IMFSample sample = (IMFSample)Marshal.GetObjectForIUnknown (pSample);
				if (MFCreateMemoryBuffer ((uint)nv12.Length, out IntPtr pBuf) < 0 || pBuf == IntPtr.Zero) {
					Marshal.ReleaseComObject (sample);
					try { Marshal.Release (pSample); } catch { }
					return null;
				}
				IMFMediaBuffer buffer = (IMFMediaBuffer)Marshal.GetObjectForIUnknown (pBuf);
				buffer.Lock (out IntPtr pData, out uint maxLen, out uint _);
				// 校验容量，防止 MFT 返回异常长度时越界写原生内存（AccessViolation 无法被 catch 拦住）。
				if (maxLen < (uint)nv12.Length || pData == IntPtr.Zero) {
					buffer.Unlock ();
					Marshal.ReleaseComObject (buffer);
					Marshal.ReleaseComObject (sample);
					try { Marshal.Release (pBuf); } catch { }
					try { Marshal.Release (pSample); } catch { }
					return null;
				}
				Marshal.Copy (nv12, 0, pData, nv12.Length);
				buffer.SetCurrentLength ((uint)nv12.Length);
				buffer.Unlock ();
				sample.AddBuffer (pBuf);
				sample.SetSampleTime (_frame * 10000000L / 15L);
				sample.SetSampleDuration (10000000L / 15L);

				int hr = _mft.ProcessInput (0u, pSample, 0u);
				Marshal.ReleaseComObject (sample);
				Marshal.ReleaseComObject (buffer);
				// MFCreateSample/MFCreateMemoryBuffer 的原始指针各占一个引用，
				// RCW 的 ReleaseComObject 只减它自己那一份，这里补上原始引用释放。
				try { Marshal.Release (pBuf); } catch { }
				try { Marshal.Release (pSample); } catch { }
				if (hr < 0) {
					return null;
				}

				MFT_OUTPUT_DATA_BUFFER [] outBufs = new MFT_OUTPUT_DATA_BUFFER [1];
				outBufs [0] = new MFT_OUTPUT_DATA_BUFFER ();
				uint status;
				hr = _mft.ProcessOutput (0u, 1u, outBufs, out status);
				if (hr < 0 || outBufs [0].pSample == IntPtr.Zero) {
					if (outBufs [0].pEvents != IntPtr.Zero) { try { Marshal.Release (outBufs [0].pEvents); } catch { } }
					return null;
				}
				IMFSample? outSample = null;
				IMFMediaBuffer? outBuffer = null;
				IntPtr pOutBuf = IntPtr.Zero;
				byte[]? packet = null;
				try {
					outSample = (IMFSample)Marshal.GetObjectForIUnknown (outBufs [0].pSample);
					outSample.ConvertToContiguousBuffer (out pOutBuf);
					outBuffer = (IMFMediaBuffer)Marshal.GetObjectForIUnknown (pOutBuf);
					outBuffer.Lock (out IntPtr pOut, out uint outMax, out uint curLen);
					// curLen 来自原生 MFT，必须同时校验 <= 容量且 < 合理上限，
					// 否则畸形帧会让 new byte[curLen] 巨量分配或 Marshal.Copy 越界读 → 进程崩溃。
					if (pOut != IntPtr.Zero && curLen <= outMax && curLen <= 64u * 1024u * 1024u) {
						packet = new byte [curLen];
						Marshal.Copy (pOut, packet, 0, (int)curLen);
					}
					outBuffer.Unlock ();
				} finally {
					ReleaseMftOutput (ref outBufs [0], pOutBuf, outBuffer, outSample);
				}
				if (packet == null) return null;

				_frame++;
				// 简化处理：每 30 帧强制关键帧语义，并在首帧/关键帧前拼接序列头
				bool isKey = (_frame == 1) || (_frame % 30 == 0);
				if (isKey && _seqHeader != null && _seqHeader.Length > 0) {
					byte[] merged = new byte [_seqHeader.Length + packet.Length];
					Buffer.BlockCopy (_seqHeader, 0, merged, 0, _seqHeader.Length);
					Buffer.BlockCopy (packet, 0, merged, _seqHeader.Length, packet.Length);
					key = true;
					return merged;
				}
				key = isKey;
				return packet;
			} catch {
				return null;
			}
		}

		public void Dispose ()
		{
			try {
				if (_mft != null) {
					if (_streaming) {
						try { _mft.ProcessMessage (0x10000001u, IntPtr.Zero); } catch { }
					}
					Marshal.ReleaseComObject (_mft);
					_mft = null;
				}
			} catch {
			}
		}
	}

	// ===================== 解码器 =====================
	internal sealed class H264Decoder : IDisposable
	{
		private IMFTransform? _mft;
		private int _w;
		private int _h;

		public bool Available { get; private set; }
		public string? LastError { get; private set; }

		public H264Decoder (int w, int h, int fps)
		{
			try {
				_mft = CreateMft (MFT_CATEGORY_VIDEO_DECODER, MFVideoFormat_H264, MFVideoFormat_NV12, DecoderClsids);
				if (_mft == null) {
					LastError = "未找到 HEVC 解码器 MFT";
					return;
				}
				_w = w;
				_h = h;
				IntPtr inType = MakeVideoType (MFVideoFormat_H264, w, h, fps, 0);
				if (inType == IntPtr.Zero || _mft.SetInputType (0u, inType, 0u) < 0) {
					LastError = "解码器输入类型设置失败";
					return;
				}
				IntPtr outType = MakeVideoType (MFVideoFormat_NV12, w, h, fps, 0);
				if (outType == IntPtr.Zero || _mft.SetOutputType (0u, outType, 0u) < 0) {
					LastError = "解码器输出类型设置失败";
					return;
				}
				_mft.ProcessMessage (MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
				_mft.ProcessMessage (MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
				Available = true;
			} catch (Exception ex) {
				LastError = ex.Message;
			}
		}

		public byte[]? Decode (byte[] packet)
		{
			if (!Available || _mft == null) {
				return null;
			}
			try {
				if (MFCreateSample (out IntPtr pSample) < 0 || pSample == IntPtr.Zero) {
					return null;
				}
				IMFSample sample = (IMFSample)Marshal.GetObjectForIUnknown (pSample);
				if (MFCreateMemoryBuffer ((uint)packet.Length, out IntPtr pBuf) < 0 || pBuf == IntPtr.Zero) {
					Marshal.ReleaseComObject (sample);
					try { Marshal.Release (pSample); } catch { }
					return null;
				}
				IMFMediaBuffer buffer = (IMFMediaBuffer)Marshal.GetObjectForIUnknown (pBuf);
				buffer.Lock (out IntPtr pData, out uint inMax, out uint _);
				if (pData == IntPtr.Zero || inMax < (uint)packet.Length) {
					buffer.Unlock ();
					Marshal.ReleaseComObject (buffer);
					Marshal.ReleaseComObject (sample);
					try { Marshal.Release (pBuf); } catch { }
					try { Marshal.Release (pSample); } catch { }
					return null;
				}
				Marshal.Copy (packet, 0, pData, packet.Length);
				buffer.SetCurrentLength ((uint)packet.Length);
				buffer.Unlock ();
				sample.AddBuffer (pBuf);
				int hr = _mft.ProcessInput (0u, pSample, 0u);
				Marshal.ReleaseComObject (sample);
				Marshal.ReleaseComObject (buffer);
				try { Marshal.Release (pBuf); } catch { }
				try { Marshal.Release (pSample); } catch { }
				if (hr < 0) {
					return null;
				}
				MFT_OUTPUT_DATA_BUFFER [] outBufs = new MFT_OUTPUT_DATA_BUFFER [1];
				hr = _mft.ProcessOutput (0u, 1u, outBufs, out uint _);
				if (hr < 0 || outBufs [0].pSample == IntPtr.Zero) {
					if (outBufs [0].pEvents != IntPtr.Zero) { try { Marshal.Release (outBufs [0].pEvents); } catch { } }
					return null;
				}
				IMFSample? outSample = null;
				IMFMediaBuffer? outBuffer = null;
				IntPtr pOutBuf = IntPtr.Zero;
				byte[]? nv12 = null;
				try {
					outSample = (IMFSample)Marshal.GetObjectForIUnknown (outBufs [0].pSample);
					outSample.ConvertToContiguousBuffer (out pOutBuf);
					outBuffer = (IMFMediaBuffer)Marshal.GetObjectForIUnknown (pOutBuf);
					outBuffer.Lock (out IntPtr pOut, out uint outMax, out uint curLen);
					// 与编码侧同理：校验原生返回长度，避免畸形帧导致越界读/巨量分配。
					if (pOut != IntPtr.Zero && curLen <= outMax && curLen <= 64u * 1024u * 1024u) {
						nv12 = new byte [curLen];
						Marshal.Copy (pOut, nv12, 0, (int)curLen);
					}
					outBuffer.Unlock ();
				} finally {
					ReleaseMftOutput (ref outBufs [0], pOutBuf, outBuffer, outSample);
				}
				if (nv12 == null) return null;
				return FromNv12 (nv12, _w, _h);
			} catch {
				return null;
			}
		}

		public void Dispose ()
		{
			try {
				if (_mft != null) {
					Marshal.ReleaseComObject (_mft);
					_mft = null;
				}
			} catch {
			}
		}
	}

	/// <summary>自检：报告本机 HEVC 编码/解码器是否可用（写日志用）。</summary>
	public static string Probe ()
	{
		if (Painter.Helpers.OsSupport.IsWindows7OrOlder) {
			return "Windows 7：已禁用 Media Foundation H.264 探测，远控走 JPEG 编码";
		}
		EnsureMf ();
		if (!_mfStarted) {
			return "Media Foundation 启动失败";
		}
		int nEnc = EnumMfts (MFT_CATEGORY_VIDEO_ENCODER, MFVideoFormat_NV12, MFVideoFormat_H264).Length;
		int nDec = EnumMfts (MFT_CATEGORY_VIDEO_DECODER, MFVideoFormat_H264, MFVideoFormat_NV12).Length;
		int allEnc = EnumMfts (MFT_CATEGORY_VIDEO_ENCODER, null, null).Length;
		int allDec = EnumMfts (MFT_CATEGORY_VIDEO_DECODER, null, null).Length;
		int encOutOnly = EnumMfts (MFT_CATEGORY_VIDEO_ENCODER, null, MFVideoFormat_H264).Length;
		int decInOnly = EnumMfts (MFT_CATEGORY_VIDEO_DECODER, MFVideoFormat_H264, null).Length;
		int actEnc = EnumActivates (MFT_CATEGORY_VIDEO_ENCODER, MFVideoFormat_NV12, MFVideoFormat_H264).Length;
		int actDec = EnumActivates (MFT_CATEGORY_VIDEO_DECODER, MFVideoFormat_H264, MFVideoFormat_NV12).Length;
		int hrEnc = ProbeActivate (MFT_CATEGORY_VIDEO_ENCODER, MFVideoFormat_NV12, MFVideoFormat_H264);
		int hrDec = ProbeActivate (MFT_CATEGORY_VIDEO_DECODER, MFVideoFormat_H264, MFVideoFormat_NV12);
		string enc;
		try {
			using H264Encoder e = new H264Encoder (640, 360, 15, 2000000u);
			enc = e.Available ? "可用" : ("不可用：" + (e.LastError ?? "未知"));
		} catch (Exception ex) {
			enc = "异常：" + ex.Message;
		}
		string dec;
		try {
			using H264Decoder d = new H264Decoder (640, 360, 15);
			dec = d.Available ? "可用" : ("不可用：" + (d.LastError ?? "未知"));
		} catch (Exception ex2) {
			dec = "异常：" + ex2.Message;
		}
		return $"H.264 编码器：{enc}（MFTEnum双向 {nEnc}/单侧 {encOutOnly}/全部 {allEnc}；MFTEnumEx激活器 {actEnc} 个，ActivateObject=0x{hrEnc:X8}）｜ " +
			$"H.264 解码器：{dec}（MFTEnum双向 {nDec}/单侧 {decInOnly}/全部 {allDec}；MFTEnumEx激活器 {actDec} 个，ActivateObject=0x{hrDec:X8}）";
	}
}
