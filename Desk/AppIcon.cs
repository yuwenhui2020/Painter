using System.Drawing;
using System.IO;
using System.Windows.Forms;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace desk;

/// <summary>
/// 程序图标统一入口。
/// 优先使用嵌入的多尺寸图标（缩放不糊）；若资源缺失，则回退到 EXE 自带的图标。
/// </summary>
internal static class AppIcon
{
	private static Icon? _cached;

	/// <summary>程序图标：各窗体标题栏 / 任务栏 / 托盘。</summary>
	public static Icon Get ()
	{
		if (_cached == null) {
			_cached = Load ("Painter.App.ico");
		}
		return _cached;
	}

	private static Icon Load (string logicalName)
	{
		try {
			using Stream? s = typeof (AppIcon).Assembly.GetManifestResourceStream (logicalName);
			if (s != null) {
				return new Icon (s);
			}
		} catch {
		}
		try {
			return Icon.ExtractAssociatedIcon (Application.ExecutablePath) ?? SystemIcons.Application;
		} catch {
			return SystemIcons.Application;
		}
	}
}
