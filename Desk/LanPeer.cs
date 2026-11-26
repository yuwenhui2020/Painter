using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace desk;

internal sealed class LanPeer
{
	public string Ip = "";

	public string HostName = "";

	public string Mac = "";

	public int Port;

	public override string ToString ()
	{
		return $"{HostName}（{Ip}:{Port}）{(string.IsNullOrEmpty (Mac) ? "" : ("  MAC:" + Mac))}";
	}
}
