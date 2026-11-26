using System;
using Concentus.Structs;
using NAudio.Wave;

using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace desk;

internal sealed class AudioPlayback : IDisposable
{
	private const int SampleRate = 48000;

	private const int Channels = 2;

	private readonly OpusDecoder _dec = new OpusDecoder (48000, 2);

	private readonly BufferedWaveProvider _prov = new BufferedWaveProvider (new WaveFormat (48000, 16, 2)) {
		DiscardOnBufferOverflow = true
	};

	private IWavePlayer? _out;

	public void Start ()
	{
		if (_out != null) {
			return;
		}
		try {
			_prov.BufferDuration = TimeSpan.FromMilliseconds (400.0);
			_out = new WasapiOut ();
			_out.Init (_prov);
			_out.Play ();
		} catch {
		}
	}

	public void Feed (byte[] opusData)
	{
		try {
			short[] array = new short[960];
			int num = _dec.Decode (opusData, 0, opusData.Length, array, 0, 480);
			if (num > 0) {
				byte[] array2 = new byte[num * 2 * 2];
				Buffer.BlockCopy (array, 0, array2, 0, array2.Length);
				Start ();
				_prov.AddSamples (array2, 0, array2.Length);
			}
		} catch {
		}
	}

	public void Stop ()
	{
		try {
			_out?.Stop ();
		} catch {
		}
		try {
			_out?.Dispose ();
		} catch {
		}
		_out = null;
	}

	public void Dispose ()
	{
		Stop ();
	}
}
