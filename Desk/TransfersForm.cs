using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace desk;

internal sealed class TransfersForm : Form
{
	private readonly FileTransferManager _mgr;

	private readonly ListView _lv;

	private readonly Dictionary<int, ListViewItem> _rows = new Dictionary<int, ListViewItem> ();

	private readonly Dictionary<int, long> _startAt = new Dictionary<int, long> ();

	public TransfersForm (FileTransferManager mgr)
	{
		_mgr = mgr;
		Icon = AppIcon.Get ();
		base.AutoScaleMode = AutoScaleMode.Dpi;
		base.AutoScaleDimensions = new SizeF (96f, 96f);
		Text = "文件传输";
		base.ClientSize = new Size (600, 320);
		base.StartPosition = FormStartPosition.CenterScreen;
		_lv = new ListView {
			View = View.Details,
			FullRowSelect = true,
			GridLines = true,
			Location = new Point (8, 8),
			Size = new Size (584, 270)
		};
		_lv.Columns.Add ("文件", 260);
		_lv.Columns.Add ("大小", 90);
		_lv.Columns.Add ("进度", 90);
		_lv.Columns.Add ("速度", 120);
		base.Controls.Add (_lv);
		Button button = new Button {
			Text = "关闭",
			Location = new Point (500, 286),
			Size = new Size (92, 28)
		};
		button.Click += delegate {
			Close ();
		};
		base.Controls.Add (button);
		FileTransferManager mgr2 = _mgr;
		mgr2.OnStarted = (Action<int, string, long>)Delegate.Combine (mgr2.OnStarted, new Action<int, string, long> (OnStarted));
		FileTransferManager mgr3 = _mgr;
		mgr3.OnProgress = (Action<int, string, long, long>)Delegate.Combine (mgr3.OnProgress, new Action<int, string, long, long> (OnProgress));
		FileTransferManager mgr4 = _mgr;
		mgr4.OnCompleted = (Action<int, string>)Delegate.Combine (mgr4.OnCompleted, new Action<int, string> (OnCompleted));
	}

	private void OnStarted (int id, string name, long size)
	{
		if (base.IsDisposed) {
			return;
		}
		try {
			if (base.InvokeRequired) {
				BeginInvoke ((Action)(() => {
					OnStarted (id, name, size);
				}));
				return;
			}
			ListViewItem listViewItem = new ListViewItem (name);
			listViewItem.SubItems.Add (FormatSize (size));
			listViewItem.SubItems.Add ("0%");
			listViewItem.SubItems.Add ("-");
			listViewItem.Tag = id;
			_lv.Items.Add (listViewItem);
			_rows [id] = listViewItem;
			_startAt [id] = NetFx.TickCount64;
		} catch {
		}
	}

	private void OnProgress (int id, string name, long received, long total)
	{
		if (base.IsDisposed) {
			return;
		}
		try {
			if (base.InvokeRequired) {
				BeginInvoke ((Action)(() => {
					OnProgress (id, name, received, total);
				}));
				return;
			}
			if (!_rows.TryGetValue (id, out ListViewItem value)) {
				OnStarted (id, name, total);
				_rows.TryGetValue (id, out value);
				if (value == null) {
					return;
				}
			}
			long value2;
			long startedAt = (_startAt.TryGetValue (id, out value2) ? value2 : NetFx.TickCount64);
			long num = FileTransferManager.SpeedOf (received, startedAt);
			int value3 = (int)((total > 0) ? (received * 100 / total) : 0);
			value.SubItems [2].Text = $"{value3}%  ({FormatSize (received)}/{FormatSize (total)})";
			value.SubItems [3].Text = ((num > 0) ? (FormatSize (num) + "/s") : "-");
		} catch {
		}
	}

	private void OnCompleted (int id, string path)
	{
		if (base.IsDisposed) {
			return;
		}
		try {
			ListViewItem value;
			if (base.InvokeRequired) {
				BeginInvoke ((Action)(() => {
					OnCompleted (id, path);
				}));
			} else if (_rows.TryGetValue (id, out value)) {
				value.SubItems [2].Text = "完成";
				value.SubItems [3].Text = "-";
			}
		} catch {
		}
	}

	private static string FormatSize (long bytes)
	{
		if (bytes >= 1073741824) {
			return $"{(double)bytes / 1073741824.0:F2} GB";
		}
		if (bytes >= 1048576) {
			return $"{(double)bytes / 1048576.0:F2} MB";
		}
		if (bytes >= 1024) {
			return $"{(double)bytes / 1024.0:F1} KB";
		}
		return bytes + " B";
	}

	protected override void OnFormClosed (FormClosedEventArgs e)
	{
		FileTransferManager mgr = _mgr;
		mgr.OnStarted = (Action<int, string, long>)Delegate.Remove (mgr.OnStarted, new Action<int, string, long> (OnStarted));
		FileTransferManager mgr2 = _mgr;
		mgr2.OnProgress = (Action<int, string, long, long>)Delegate.Remove (mgr2.OnProgress, new Action<int, string, long, long> (OnProgress));
		FileTransferManager mgr3 = _mgr;
		mgr3.OnCompleted = (Action<int, string>)Delegate.Remove (mgr3.OnCompleted, new Action<int, string> (OnCompleted));
		base.OnFormClosed (e);
	}
}
