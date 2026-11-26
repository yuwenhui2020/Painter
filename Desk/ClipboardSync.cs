using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

using System.Collections.Generic;
using System.Drawing;
using System.Net.Http;
using System.Threading.Tasks;

namespace desk;

internal sealed class ClipboardSync : IDisposable
{
	private const int PollIntervalMs = 400;

	private readonly Func<(ClipFormat fmt, byte[] data)?> _getLocal;

	private readonly Action<ClipFormat, byte[]> _setLocal;

	private readonly Action<ClipFormat, byte[]> _sendRemote;

	private volatile string _lastSig = "";

	private readonly Thread _worker;

	private volatile bool _stop;

	private int _started;

	public bool Enabled { get; set; } = true;

	public Func<string[]?>? GetFiles { get; set; }

	public Action<string[]>? SendFiles { get; set; }

	public ClipboardSync (Func<(ClipFormat fmt, byte[] data)?> getLocal, Action<ClipFormat, byte[]> setLocal, Action<ClipFormat, byte[]> sendRemote)
	{
		_getLocal = getLocal;
		_setLocal = setLocal;
		_sendRemote = sendRemote;
		_worker = new Thread (ThreadProc) {
			IsBackground = true,
			Name = "clipboard-sync"
		};
		_worker.SetApartmentState (ApartmentState.STA);
	}

	public void Start ()
	{
		if (Interlocked.Exchange (ref _started, 1) == 0) {
			_worker.Start ();
		}
	}

	private void ThreadProc ()
	{
		System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer {
			Interval = 400
		};
		timer.Tick += delegate {
			try {
				Poll ();
			} catch {
			}
			if (_stop) {
				Application.ExitThread ();
			}
		};
		timer.Start ();
		try {
			Application.Run ();
		} catch {
		}
		timer.Dispose ();
	}

	private void Poll ()
	{
		if (_stop || !Enabled) {
			return;
		}
		try {
			string[] array = GetFiles?.Invoke ();
			if (array != null && array.Length != 0) {
				// 文件名与长度之间加分隔符：否则 "a"+12 与 "a1"+2 都得到 a12，签名碰撞会漏同步。
				string text = "files:" + array.Length + ":" + string.Join ("|", array.Select ((string f) => Path.GetFileName (f) + "#" + new FileInfo (f).Length));
				if (text != _lastSig) {
					// 只在发送成功后记录签名：网络异常时下一次 Poll 才能重试，否则本次复制永久丢失。
					try {
						SendFiles?.Invoke (array);
						_lastSig = text;
					} catch {
					}
				}
				return;
			}
		} catch {
		}
		(ClipFormat, byte[])? tuple;
		try {
			tuple = _getLocal ();
		} catch {
			return;
		}
		if (!tuple.HasValue) {
			return;
		}
		(ClipFormat, byte[]) value = tuple.Value;
		ClipFormat item = value.Item1;
		byte[] item2 = value.Item2;
		string text2 = $"{item}:{item2.Length}:{Convert.ToBase64String (item2, 0, Math.Min (32, item2.Length))}";
		if (text2 == _lastSig) {
			return;
		}
		try {
			_sendRemote (item, item2);
			_lastSig = text2;   // 发送成功才记签名，失败时下轮重试
		} catch {
		}
	}

	public void OnRemoteData (ClipFormat fmt, byte[] data)
	{
		if (fmt != ClipFormat.None && data.Length != 0) {
			try {
				_setLocal (fmt, data);
			} catch {
			}
			_lastSig = $"{fmt}:{data.Length}:{Convert.ToBase64String (data, 0, Math.Min (32, data.Length))}";
		}
	}

	public void Dispose ()
	{
		_stop = true;
	}
}
