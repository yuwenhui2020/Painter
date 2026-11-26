using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Windows.Forms;

namespace desk;

internal static class Protocol
{
	public enum MessageType : byte
	{
		LoginRequest = 1,
		LoginResponse,
		VideoFrame,
		MouseEvent,
		KeyEvent,
		Disconnect,
		ClipboardText,
		FileControl,
		FileData,
		QualitySettings,
		Ping,
		Pong,
		ForceKeyFrame,
		LogLine,
		PermissionInfo,
		CursorData,
		CursorPosition,
		ChatMessage, // 保留占位（不再处理），以免后续枚举值序号变化影响协议兼容
		ControlAction,
		PrivacyMode,
		TestDelay,
		SupportedDecoding,
		SupportedEncoding,
		ClipboardData,
		AudioFormat,
		AudioFrame,
		AudioToggle,
		CliprdrFiles,
		PortForward,
		// v2 扩展：浏览器端 Unicode 文本输入（UTF-16LE 码元）。追加在末尾，
		// 旧端收到未知 type 会走 switch default 忽略，不影响兼容。
		UnicodeText = 50
	}

	[Flags]
	public enum Permission : byte
	{
		Keyboard = 1,
		Clipboard = 2,
		Audio = 4,
		File = 8,
		Restart = 0x10,
		Recording = 0x20,
		BlockInput = 0x40,
		PrivacyMode = 0x80
	}

	public enum ControlActionId : byte
	{
		CtrlAltDel = 1,
		LockScreen,
		BlockInputOn,
		BlockInputOff,
		RemoteRestart,
		// Web 端：读取被控端当前剪贴板文本，服务端用 ClipboardText(type=7) 回发。
		ReadClipboard = 6
	}

	[Flags]
	public enum CodecAbility : byte
	{
		None = 0,
		Vp8 = 1,
		Vp9 = 2,
		Av1 = 4,
		H265 = 0x10
	}

	public readonly record struct QualityConfig (EncodingMode Mode, int Quality, int MaxWidth, int Fps, int ResyncSec, int BlockSize)
	{
		public static QualityConfig Default => new QualityConfig (EncodingMode.JpegRegions, 70, 1920, 60, 4, 32);

		public byte[] Encode ()
		{
			MemoryStream memoryStream = new MemoryStream (9);
			memoryStream.WriteByte ((byte)Mode);
			memoryStream.WriteByte ((byte)NetFx.Clamp (Quality, 1, 100));
			WriteInt32 (memoryStream, MaxWidth);
			memoryStream.WriteByte ((byte)NetFx.Clamp (Fps, 1, 60));
			memoryStream.WriteByte ((byte)NetFx.Clamp (ResyncSec, 1, 10));
			memoryStream.WriteByte ((byte)NetFx.Clamp (BlockSize, 8, 128));
			return memoryStream.ToArray ();
		}

		public static QualityConfig Decode (byte[] p)
		{
			if (p.Length < 9) {
				return Default;
			}
			return new QualityConfig ((EncodingMode)p [0], p [1], ReadInt32 (p, 2), p [6], p [7], p [8]);
		}
	}

	public enum EncodingMode : byte
	{
		JpegRegions,
		RawRegions,
		JpegFullFrame,
		H265
	}

	public const string ClipFileRoot = "__clipboard__";

	public const int RestartGraceMinutes = 5;

	public const byte MOUSE_RELATIVE = 4;

	public const int MaxMessageBytes = 67108864;

	public const int ReadIdleTimeoutMs = 30000;

	public const int SendTimeoutMs = 20000;

	public const byte FLAG_REGION = 1;

	public const byte FLAG_JPEG_BLOCK = 2;

	public const byte CODEC_JPEG = 0;

	public const byte CODEC_RAW = 2;

	public const byte CODEC_H265 = 3;

	public const byte FILE_OFFER = 1;

	public const byte FILE_ACCEPT = 2;

	public const byte FILE_REJECT = 3;

	public const byte FILE_DONE = 4;

	public const byte FILE_CANCEL = 5;

	public static byte[] Encode (MessageType type, byte[]? payload)
	{
		if (payload == null) {
			payload = Array.Empty<byte> ();
		}
		int num = 1 + payload.Length;
		byte[] array = new byte[4 + num];
		array [0] = (byte)(num & 0xFF);
		array [1] = (byte)((num >> 8) & 0xFF);
		array [2] = (byte)((num >> 16) & 0xFF);
		array [3] = (byte)((num >> 24) & 0xFF);
		array [4] = (byte)type;
		if (payload.Length != 0) {
			Buffer.BlockCopy (payload, 0, array, 5, payload.Length);
		}
		return array;
	}

	public static async Task<(MessageType type, byte[] payload)?> ReadMessageAsync (NetworkStream stream, CancellationToken ct)
	{
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource (ct);
		timeout.CancelAfter (30000);
		CancellationToken t = timeout.Token;
		byte[] lenBuf = new byte[4];
		if (!(await ReadExactAsync (stream, lenBuf, 0, 4, t))) {
			return null;
		}
		int bodyLen = lenBuf [0] | (lenBuf [1] << 8) | (lenBuf [2] << 16) | (lenBuf [3] << 24);
		if (bodyLen < 1 || bodyLen > 67108864) {
			throw new IOException ($"非法报文长度 {bodyLen}，流已错位，断开并重连");
		}
		byte[] body = new byte[bodyLen];
		if (!(await ReadExactAsync (stream, body, 0, bodyLen, t))) {
			return null;
		}
		MessageType type = (MessageType)body [0];
		byte[] payload = new byte[bodyLen - 1];
		if (payload.Length != 0) {
			Buffer.BlockCopy (body, 1, payload, 0, payload.Length);
		}
		return (type, payload);
	}

	public static async Task<bool> ReadExactAsync (NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
	{
		int n;
		for (int read = 0; read < count; read += n) {
			n = await stream.ReadAsync (buffer, offset + read, count - read, ct);
			if (n == 0) {
				return false;
			}
		}
		return true;
	}

	public static async Task SendAsync (NetworkStream stream, MessageType type, byte[]? payload, CancellationToken ct = default(CancellationToken), int timeoutMs = 20000)
	{
		byte[] data = Encode (type, payload);
		using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource (ct);
		cts.CancelAfter (timeoutMs);
		await stream.WriteAsync (data, 0, data.Length, cts.Token);
	}

	public static void WriteString (MemoryStream ms, string s)
	{
		byte[] bytes = Encoding.UTF8.GetBytes (s);
		WriteInt32 (ms, bytes.Length);
		ms.Write (bytes, 0, bytes.Length);
	}

	public static string ReadString (byte[] buf, ref int offset)
	{
		int num = ReadInt32 (buf, offset);
		offset += 4;
		// 校验长度来自网络/远端，负值或超出缓冲区会在 GetString 抛
		// ArgumentOutOfRangeException；若调用点在 try 之外则可能逃逸并终止进程。
		// 这里统一抛格式异常，便于调用方捕获并断开连接。
		if (num < 0 || num > buf.Length - offset) {
			throw new FormatException ("协议字符串长度非法");
		}
		string result = Encoding.UTF8.GetString (buf, offset, num);
		offset += num;
		return result;
	}

	public static void WriteInt32 (MemoryStream ms, int value)
	{
		ms.Write (BitConverter.GetBytes (value), 0, 4);
	}

	public static int ReadInt32 (byte[] buf, int offset)
	{
		// 报文被截断时给出明确异常，而不是 IndexOutOfRangeException 被上层空 catch 吞掉
		if (buf == null || offset < 0 || offset + 4 > buf.Length)
			throw new FormatException ("ReadInt32: 数据不足或偏移越界");
		return buf [offset] | (buf [offset + 1] << 8) | (buf [offset + 2] << 16) | (buf [offset + 3] << 24);
	}
}
