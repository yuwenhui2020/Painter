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

internal sealed class SavedConnection
{
	public string Host { get; set; } = "";

	public int Port { get; set; } = 9000;

	public string Password { get; set; } = "";

	public bool UseRelay { get; set; }

	public string Token { get; set; } = "";
}
