using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using System.Drawing;
using System.Net.Http;
using System.Threading;
using System.Windows.Forms;

namespace desk;

internal sealed class FileTransferManager
{
	private class Outgoing
	{
		public int Id;

		public FileStream Stream = null;

		public long Size;

		public long Sent;

		public string Path = "";

		public string RelPath = "";

		public bool FromQueue;

		public long StartedAt;
	}

	private class Incoming
	{
		public int Id;

		public FileStream? Stream;

		public long Size;

		public long Received;

		public string Name = "";

		public string SavePath = "";

		public long StartedAt;

		public bool IsClipboard;
	}

	private const int ChunkSize = 65536;

	private readonly Func<Protocol.MessageType, byte[], Task> _send;

	private readonly Dictionary<int, Outgoing> _out = new Dictionary<int, Outgoing> ();

	private readonly Dictionary<int, Incoming> _inc = new Dictionary<int, Incoming> ();

	private int _nextId = 1;

	private readonly object _gate = new object ();

	public Func<int, string, long, bool>? OnIncomingOffer;

	public Action<int, string, long, long>? OnProgress = null;

	public Action<int, string, long>? OnStarted = null;

	public Action<int, string>? OnCompleted = null;

	public Action<string>? Log;

	private readonly Queue<(string full, string rel)> _pending = new Queue<(string, string)> ();

	private bool _sendingQueue;

	private string _queueRoot = "";

	public Action<List<string>>? OnClipboardFilesReady;

	private int _clipExpected;

	private readonly List<string> _clipReceived = new List<string> ();

	public FileTransferManager (Func<Protocol.MessageType, byte[], Task> send)
	{
		_send = send;
	}

	public async Task SendFileAsync (string path)
	{
		if (!File.Exists (path)) {
			Log?.Invoke ("文件不存在：" + path);
			return;
		}
		int id;
		lock (_gate) {
			id = _nextId++;
		}
		FileStream fs = File.OpenRead (path);
		lock (_gate) {
			_out [id] = new Outgoing {
				Id = id,
				Stream = fs,
				Size = fs.Length,
				Sent = 0L,
				Path = path
			};
		}
		await OfferAsync (id, fs, Path.GetFileName (path), "");
	}

	public async Task SendFolderAsync (string dir)
	{
		if (!Directory.Exists (dir)) {
			Log?.Invoke ("目录不存在：" + dir);
			return;
		}
		string root = Path.GetFileName (dir.TrimEnd (Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		if (string.IsNullOrEmpty (root)) {
			root = "folder";
		}
		lock (_gate) {
			_queueRoot = root;
			foreach (string f in Directory.EnumerateFiles (dir, "*", SearchOption.AllDirectories)) {
				string rel = NetFx.GetRelativePath (dir, f);
				_pending.Enqueue ((f, Path.Combine (root, rel).Replace ('\\', '/')));
			}
		}
		Log?.Invoke ($"准备发送文件夹：{dir}（共 {_pending.Count} 个文件）");
		await SendNextFromQueueAsync ();
	}

	public async Task SendClipboardFilesAsync (IEnumerable<string> paths)
	{
		List<string> list = paths.Where (File.Exists).ToList ();
		if (list.Count == 0) {
			return;
		}
		MemoryStream head = new MemoryStream ();
		Protocol.WriteInt32 (head, list.Count);
		await _send (Protocol.MessageType.CliprdrFiles, head.ToArray ());
		lock (_gate) {
			_queueRoot = "__clipboard__";
			foreach (string p in list) {
				_pending.Enqueue ((p, "__clipboard__/" + Path.GetFileName (p)));
			}
		}
		Log?.Invoke ($"剪贴板文件：准备发送 {list.Count} 个文件");
		await SendNextFromQueueAsync ();
	}

	public void ExpectClipboardFiles (int count)
	{
		lock (_gate) {
			_clipExpected = count;
			_clipReceived.Clear ();
		}
	}

	private async Task SendNextFromQueueAsync ()
	{
		(string full, string rel)? item = null;
		lock (_gate) {
			if (_sendingQueue) {
				return;
			}
			if (_pending.Count == 0) {
				Log?.Invoke ("文件夹发送完毕。");
				return;
			}
			item = _pending.Dequeue ();
			_sendingQueue = true;
		}
		FileStream fs = File.OpenRead (item.Value.full);
		int id;
		lock (_gate) {
			id = _nextId++;
		}
		lock (_gate) {
			_out [id] = new Outgoing {
				Id = id,
				Stream = fs,
				Size = fs.Length,
				Sent = 0L,
				Path = item.Value.full,
				RelPath = item.Value.rel,
				FromQueue = true
			};
		}
		await OfferAsync (id, fs, Path.GetFileName (item.Value.full), item.Value.rel);
	}

	private async Task OfferAsync (int id, FileStream fs, string name, string relPath)
	{
		MemoryStream ms = new MemoryStream ();
		ms.WriteByte (1);
		Protocol.WriteInt32 (ms, id);
		Protocol.WriteString (ms, name);
		Protocol.WriteInt32 (ms, (int)(fs.Length >> 32));
		Protocol.WriteInt32 (ms, (int)(fs.Length & 0xFFFFFFFFu));
		Protocol.WriteString (ms, relPath);
		await _send (Protocol.MessageType.FileControl, ms.ToArray ());
		Log?.Invoke ($"已发起发送：{((relPath.Length > 0) ? relPath : name)} ({fs.Length} 字节)，等待对端确认…");
	}

	public async Task OnControlAsync (byte[] p)
	{
		if (p == null || p.Length < 5) { // cmd(1) + id(4)
			return;
		}
		int cmd = p [0];
		int off = 1;
		int id = Protocol.ReadInt32 (p, off);
		off += 4;
		switch (cmd) {
		case 1: {
			string name = Protocol.ReadString (p, ref off);
			long size = ((long)Protocol.ReadInt32 (p, off) << 32) | (uint)Protocol.ReadInt32 (p, off + 4);
			off += 8;
			string relPath = "";
			if (off + 4 <= p.Length) {
				try {
					relPath = Protocol.ReadString (p, ref off);
				} catch {
					relPath = "";
				}
			}
			bool isClip = relPath.StartsWith ("__clipboard__/", StringComparison.Ordinal);
			if (isClip || (OnIncomingOffer?.Invoke (id, (relPath.Length > 0) ? relPath : name, size) ?? false)) {
				string dir = ((!isClip) ? Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.UserProfile), "Downloads") : Path.Combine (Path.GetTempPath (), AppIdentity.ClipboardTempFolder, DateTime.Now.ToString ("yyyyMMddHHmmss")));
				Directory.CreateDirectory (dir);
				string save = ((relPath.Length > 0) ? Path.Combine (dir, relPath.Replace ('/', Path.DirectorySeparatorChar)) : Path.Combine (dir, name));
				// 路径穿越防护：远端发来的 relPath 可能含 ..\，解析后必须仍落在 dir 内，
				// 否则拒绝（既避免越权写入，也避免非法路径异常中断传输任务）。
				try {
					string fullSave = Path.GetFullPath (save);
					string fullDir = Path.GetFullPath (dir);
					if (!fullSave.StartsWith (fullDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
						&& !fullSave.Equals (fullDir, StringComparison.OrdinalIgnoreCase)) {
						Log?.Invoke ("拒绝非法文件路径：" + relPath);
						return;
					}
					save = fullSave;
				} catch {
					Log?.Invoke ("拒绝非法文件路径：" + relPath);
					return;
				}
				Directory.CreateDirectory (Path.GetDirectoryName (save));
				long offset = 0L;
				if (File.Exists (save)) {
					long existLen = new FileInfo (save).Length;
					if (existLen >= size) {
						existLen = 0L;
					}
					offset = existLen;
				}
				FileStream fs = ((offset > 0) ? File.Open (save, FileMode.Open, FileAccess.Write) : File.Create (save));
				if (offset > 0) {
					fs.Seek (offset, SeekOrigin.Begin);
				}
				Incoming inc2 = new Incoming {
					Id = id,
					Name = ((relPath.Length > 0) ? relPath : name),
					Size = size,
					SavePath = save,
					Stream = fs,
					Received = offset,
					StartedAt = NetFx.TickCount64,
					IsClipboard = isClip
				};
				lock (_gate) {
					_inc [id] = inc2;
				}
				OnStarted?.Invoke (id, inc2.Name, size);
				MemoryStream ms = new MemoryStream ();
				ms.WriteByte (2);
				Protocol.WriteInt32 (ms, id);
				Protocol.WriteInt32 (ms, (int)(offset >> 32));
				Protocol.WriteInt32 (ms, (int)(offset & 0xFFFFFFFFu));
				await _send (Protocol.MessageType.FileControl, ms.ToArray ());
				Log?.Invoke ((offset > 0) ? $"断点续传：{inc2.Name}（从 {offset}/{size} 字节继续）" : ("开始接收：" + inc2.Name + " → " + save));
			} else {
				MemoryStream ms2 = new MemoryStream ();
				ms2.WriteByte (3);
				Protocol.WriteInt32 (ms2, id);
				await _send (Protocol.MessageType.FileControl, ms2.ToArray ());
				Log?.Invoke ("已拒绝文件：" + name);
			}
			break;
		}
		case 2: {
			Outgoing o3 = null;
			lock (_gate) {
				_out.TryGetValue (id, out o3);
			}
			if (o3 == null) {
				break;
			}
			if (p.Length >= 13) {
				long offset2 = ((long)Protocol.ReadInt32 (p, 5) << 32) | (uint)Protocol.ReadInt32 (p, 9);
				if (offset2 > 0 && offset2 < o3.Size) {
					o3.Sent = offset2;
					o3.Stream.Seek (offset2, SeekOrigin.Begin);
					Log?.Invoke ($"按对端请求从 {offset2} 字节处续传。");
				}
			}
			o3.StartedAt = NetFx.TickCount64;
			await StreamOutgoingAsync (o3);
			break;
		}
		case 3: {
			Outgoing o2 = null;
			lock (_gate) {
				_out.TryGetValue (id, out o2);
				_out.Remove (id);
			}
			o2?.Stream.Dispose ();
			Log?.Invoke ("对端拒绝了文件发送。");
			break;
		}
		case 4: {
			Incoming inc3 = null;
			lock (_gate) {
				_inc.TryGetValue (id, out inc3);
				_inc.Remove (id);
			}
			if (inc3?.Stream == null) {
				break;
			}
			inc3.Stream.Dispose ();
			OnCompleted?.Invoke (id, inc3.SavePath);
			Log?.Invoke ("文件接收完成：" + inc3.SavePath);
			if (!inc3.IsClipboard) {
				break;
			}
			List<string> ready = new List<string> ();
			lock (_gate) {
				_clipReceived.Add (inc3.SavePath);
				if (_clipReceived.Count >= Math.Max (1, _clipExpected)) {
					ready = new List<string> (_clipReceived);
					_clipReceived.Clear ();
					_clipExpected = 0;
				}
			}
			if (ready.Count > 0) {
				WinClipboard.SetFiles (ready);
				OnClipboardFilesReady?.Invoke (ready);
				Log?.Invoke ($"剪贴板已收到 {ready.Count} 个文件，可直接粘贴。");
			}
			break;
		}
		case 5:
			lock (_gate) {
				if (_out.TryGetValue (id, out Outgoing o)) {
					o.Stream.Dispose ();
					_out.Remove (id);
				}
				if (_inc.TryGetValue (id, out Incoming inc)) {
					inc.Stream?.Dispose ();
					_inc.Remove (id);
				}
			}
			Log?.Invoke ("文件传输已取消。");
			break;
		}
	}

	private async Task StreamOutgoingAsync (Outgoing o)
	{
		byte[] buffer = new byte[65536];
		while (o.Sent < o.Size) {
			int n = await o.Stream.ReadAsync (buffer, 0, Math.Min (65536, (int)Math.Min (2147483647L, o.Size - o.Sent)));
			if (n == 0) {
				break;
			}
			MemoryStream ms = new MemoryStream ();
			Protocol.WriteInt32 (ms, o.Id);
			Protocol.WriteInt32 (ms, n);
			ms.Write (buffer, 0, n);
			await _send (Protocol.MessageType.FileData, ms.ToArray ());
			o.Sent += n;
			await Task.Delay (2);
		}
		o.Stream.Dispose ();
		lock (_gate) {
			_out.Remove (o.Id);
		}
		MemoryStream done = new MemoryStream ();
		done.WriteByte (4);
		Protocol.WriteInt32 (done, o.Id);
		await _send (Protocol.MessageType.FileControl, done.ToArray ());
		Log?.Invoke ("文件发送完成：" + Path.GetFileName (o.Path));
		if (o.FromQueue) {
			lock (_gate) {
				_sendingQueue = false;
			}
			await SendNextFromQueueAsync ();
		}
	}

	public async Task OnDataAsync (byte[] p)
	{
		if (p.Length < 8) {
			return;
		}
		int id = Protocol.ReadInt32 (p, 0);
		int len = Protocol.ReadInt32 (p, 4);
		if (8 + len <= p.Length) {
			Incoming inc = null;
			lock (_gate) {
				_inc.TryGetValue (id, out inc);
			}
			if (inc != null && inc.Stream != null) {
				await inc.Stream.WriteAsync (p, 8, len);
				inc.Received += len;
				OnProgress?.Invoke (id, inc.Name, inc.Received, inc.Size);
			}
		}
	}

	public static long SpeedOf (long bytes, long startedAt)
	{
		long num = NetFx.TickCount64 - startedAt;
		return (num <= 0) ? 0 : ((long)((double)bytes * 1000.0 / (double)num));
	}
}
