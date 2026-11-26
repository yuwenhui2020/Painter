using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace desk;

internal static class AppLog
{
	private static readonly object _sync = new object ();

	private static readonly List<string> _lines = new List<string> ();

	private const int MaxLines = 3000;

	private const long MaxFileBytes = 4194304L;

	private static int _sinceCheck;

	public static string FilePath { get; } = Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.LocalApplicationData), AppIdentity.DataFolder, AppIdentity.LogFileName);

	public static string BuildStamp { get; } = ComputeBuildStamp ();

	public static event Action<string>? LineAdded;

	private static string ComputeBuildStamp ()
	{
		try {
			string text = NetFx.ProcessPath;
			if (string.IsNullOrEmpty (text)) {
				text = typeof(AppLog).Assembly.Location;
			}
			if (string.IsNullOrEmpty (text)) {
				return "?";
			}
			return File.GetLastWriteTime (text).ToString ("MM-dd HH:mm:ss");
		} catch {
			return "?";
		}
	}

	public static void Write (string tag, string msg)
	{
		string text;
		try {
			text = $"[{DateTime.Now:HH:mm:ss.fff}] [{tag}] {msg}";
		} catch {
			return;
		}
		lock (_sync) {
			_lines.Add (text);
			if (_lines.Count > 3000) {
				_lines.RemoveRange (0, _lines.Count - 3000);
			}
		}
		try {
			AppLog.LineAdded?.Invoke (text);
		} catch {
		}
		try {
			Directory.CreateDirectory (Path.GetDirectoryName (FilePath));
			File.AppendAllText (FilePath, text + "\r\n", Encoding.UTF8);
			if (++_sinceCheck < 200) {
				return;
			}
			_sinceCheck = 0;
			FileInfo fileInfo = new FileInfo (FilePath);
			if (fileInfo.Exists && fileInfo.Length > 4194304) {
				string text2 = FilePath + ".old";
				try {
					File.Delete (text2);
				} catch {
				}
				try {
					File.Move (FilePath, text2);
					return;
				} catch {
					return;
				}
			}
		} catch {
		}
	}

	public static string All ()
	{
		lock (_sync) {
			return string.Join (Environment.NewLine, _lines);
		}
	}

	public static void Clear ()
	{
		lock (_sync) {
			_lines.Clear ();
		}
		try {
			File.Delete (FilePath);
		} catch {
		}
	}
}
