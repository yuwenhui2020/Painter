using System.IO.Pipes;

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

internal static class UacBridge
{
	public static bool TrySendSas ()
	{
		// 改名前装下的服务仍在运行时只认旧管道名，先试新名再依次回退到两代旧名
		string[] array = new string[3] {
			AppIdentity.SasPipeName,
			AppIdentity.LegacySasPipeNameInkCanvas,
			AppIdentity.LegacySasPipeName
		};
		foreach (string name in array) {
			try {
				using NamedPipeClientStream namedPipeClientStream = new NamedPipeClientStream (".", name, PipeDirection.Out);
				namedPipeClientStream.Connect (300);
				namedPipeClientStream.WriteByte (83);
				namedPipeClientStream.Flush ();
				return true;
			} catch {
			}
		}
		return false;
	}
}
