using System;
using System.Collections.Generic;
using Concentus.Enums;
using Concentus.Structs;
using NAudio.Wave;

using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace desk;

internal sealed class AudioCapture : IDisposable
{
	private const int SampleRate = 48000;

	private const int Channels = 2;

	private const int FrameSamples = 480;

	private const int ZeroLimit = 800;

	private WasapiLoopbackCapture? _cap;

	private OpusEncoder? _enc;

	private readonly List<float> _buf = new List<float> ();

	private readonly object _gate = new object ();

	private int _zeroCount;

	private bool _muted;

	public Action<byte[]>? OnFrame;

	public Action? OnFormat;

	public Action<string>? Log;

	public bool Running => _cap != null;

	public void Start ()
	{
		if (_cap != null) {
			return;
		}
		try {
			_cap = new WasapiLoopbackCapture ();
			_cap.DataAvailable += OnData;
			_cap.RecordingStopped += delegate {
			};
			_cap.StartRecording ();
			_enc = new OpusEncoder (48000, 2, OpusApplication.OPUS_APPLICATION_AUDIO);
			_enc.Bitrate = 96000;
			OnFormat?.Invoke ();
			Log?.Invoke ("远端声音采集已启动（WASAPI loopback + Opus 48kHz）");
		} catch (Exception ex) {
			Log?.Invoke ("声音采集启动失败：" + ex.Message);
			Stop ();
		}
	}

	private void OnData (object? sender, WaveInEventArgs e)
	{
		WasapiLoopbackCapture? cap = _cap;
		int num = ((cap != null) ? (cap.WaveFormat.BitsPerSample / 8) : 4);
		int num2 = _cap?.WaveFormat.Channels ?? 2;
		int num3 = _cap?.WaveFormat.SampleRate ?? 48000;
		List<float> list = new List<float> ();
		for (int i = 0; i + num <= e.BytesRecorded; i += num * num2) {
			float item = ((num == 4) ? BitConverter.ToSingle (e.Buffer, i) : ((float)BitConverter.ToInt16 (e.Buffer, i) / 32768f));
			list.Add (item);
		}
		lock (_gate) {
			if (num3 != 48000 && list.Count > 1) {
				List<float> list2 = new List<float> ();
				double num4 = (double)num3 / 48000.0;
				for (double num5 = 0.0; num5 < (double)list.Count; num5 += num4) {
					int num6 = (int)num5;
					if (num6 >= list.Count) {
						break;
					}
					list2.Add (list [num6]);
				}
				_buf.AddRange (list2);
			} else {
				_buf.AddRange (list);
			}
		}
		lock (_gate) {
			while (_buf.Count >= 960) {
				EncodeOneFrame ();
			}
		}
	}

	private void EncodeOneFrame ()
	{
		if (_enc == null) {
			_buf.RemoveRange (0, Math.Min (_buf.Count, 960));
			return;
		}
		float[] array = new float[960];
		bool flag = true;
		for (int i = 0; i < array.Length; i++) {
			if ((array [i] = _buf [i]) != 0f) {
				flag = false;
			}
		}
		_buf.RemoveRange (0, array.Length);
		if (flag) {
			if (++_zeroCount > 800) {
				_muted = true;
				return;
			}
		} else {
			_zeroCount = 0;
			_muted = false;
		}
		if (_muted) {
			return;
		}
		try {
			byte[] array2 = new byte[4000];
			int num = _enc.Encode (array, 0, 480, array2, 0, array2.Length);
			if (num > 0) {
				byte[] array3 = new byte[num];
				Buffer.BlockCopy (array2, 0, array3, 0, num);
				OnFrame?.Invoke (array3);
			}
		} catch {
		}
	}

	public void Stop ()
	{
		try {
			_cap?.StopRecording ();
		} catch {
		}
		try {
			_cap?.Dispose ();
		} catch {
		}
		_cap = null;
		_enc = null;
		lock (_gate) {
			_buf.Clear ();
			_zeroCount = 0;
			_muted = false;
		}
	}

	public void Dispose ()
	{
		Stop ();
	}
}
