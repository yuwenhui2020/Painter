using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace desk;

internal static class WinClipboard
{
	private const string OwnerFmt = AppIdentity.ClipboardOwnerFormat;

	public static string[]? ReadFiles ()
	{
		try {
			IDataObject dataObject = Clipboard.GetDataObject ();
			if (dataObject == null) {
				return null;
			}
			if (dataObject.GetDataPresent (OwnerFmt)) {
				return null;
			}
			if (!dataObject.GetDataPresent (DataFormats.FileDrop)) {
				return null;
			}
			if (dataObject.GetData (DataFormats.FileDrop) is string[] array && array.Length != 0) {
				return array;
			}
			return null;
		} catch {
			return null;
		}
	}

	public static void SetFiles (IEnumerable<string> paths)
	{
		try {
			DataObject dataObject = new DataObject ();
			string[] array = paths.Where (File.Exists).ToArray ();
			if (array.Length != 0) {
				dataObject.SetData (DataFormats.FileDrop, array);
				dataObject.SetData (OwnerFmt, true);
				Clipboard.SetDataObject (dataObject, copy: true, 4, 200);
			}
		} catch {
		}
	}

	public static (ClipFormat fmt, byte[] data)? Read ()
	{
		try {
			IDataObject dataObject = Clipboard.GetDataObject ();
			if (dataObject == null) {
				return null;
			}
			if (dataObject.GetDataPresent (OwnerFmt)) {
				return null;
			}
			if (dataObject.GetDataPresent (DataFormats.Bitmap) && dataObject.GetData (DataFormats.Bitmap) is Bitmap bitmap) {
				using MemoryStream memoryStream = new MemoryStream ();
				using (bitmap) bitmap.Save (memoryStream, ImageFormat.Png);
				return (ClipFormat.Png, memoryStream.ToArray ());
			}
			if (dataObject.GetDataPresent (DataFormats.Rtf)) {
				string text = dataObject.GetData (DataFormats.Rtf) as string;
				if (!string.IsNullOrEmpty (text)) {
					return (ClipFormat.Rtf, Encoding.UTF8.GetBytes (text));
				}
			}
			if (dataObject.GetDataPresent (DataFormats.Html)) {
				string text2 = dataObject.GetData (DataFormats.Html) as string;
				if (!string.IsNullOrEmpty (text2)) {
					return (ClipFormat.Html, Encoding.UTF8.GetBytes (text2));
				}
			}
			if (dataObject.GetDataPresent (DataFormats.UnicodeText)) {
				string text3 = dataObject.GetData (DataFormats.UnicodeText) as string;
				if (!string.IsNullOrEmpty (text3)) {
					return (ClipFormat.Text, Encoding.UTF8.GetBytes (text3));
				}
			}
			if (dataObject.GetDataPresent (DataFormats.Text)) {
				string text4 = dataObject.GetData (DataFormats.Text) as string;
				if (!string.IsNullOrEmpty (text4)) {
					return (ClipFormat.Text, Encoding.UTF8.GetBytes (text4));
				}
			}
			return null;
		} catch {
			return null;
		}
	}

	public static void Write (ClipFormat fmt, byte[] data)
	{
		try {
			DataObject dataObject = new DataObject ();
			switch (fmt) {
			case ClipFormat.Png: {
				using (MemoryStream stream = new MemoryStream (data)) {
					using Bitmap bitmap = new Bitmap (stream);
					dataObject.SetData (DataFormats.Bitmap, bitmap.Clone ());
				}
				break;
			}
			case ClipFormat.Rtf:
				dataObject.SetData (DataFormats.Rtf, Encoding.UTF8.GetString (data));
				break;
			case ClipFormat.Html:
				dataObject.SetData (DataFormats.Html, Encoding.UTF8.GetString (data));
				break;
			default:
				dataObject.SetData (DataFormats.UnicodeText, Encoding.UTF8.GetString (data));
				dataObject.SetData (DataFormats.Text, Encoding.UTF8.GetString (data));
				break;
			}
			dataObject.SetData (OwnerFmt, true);
			Clipboard.SetDataObject (dataObject, copy: true, 4, 200);
		} catch {
		}
	}
}
