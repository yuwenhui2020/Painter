using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace desk;

internal sealed class LogForm : Form
{
	private readonly TextBox _txt;

	private const int UiMaxLines = 3000;

	public LogForm ()
	{
		Icon = AppIcon.Get ();
		base.AutoScaleMode = AutoScaleMode.Dpi;
		base.AutoScaleDimensions = new SizeF (96f, 96f);
		Text = "运行日志（可全选复制）";
		base.ClientSize = new Size (900, 600);
		base.StartPosition = FormStartPosition.CenterScreen;
		_txt = new TextBox {
			Multiline = true,
			ReadOnly = true,
			ScrollBars = ScrollBars.Both,
			WordWrap = false,
			Location = new Point (8, 8),
			Size = new Size (884, 540),
			Font = new Font ("Consolas", 9f),
			BackColor = Color.White
		};
		base.Controls.Add (_txt);
		Button button = new Button {
			Text = "复制全部",
			Location = new Point (8, 556),
			Size = new Size (110, 32)
		};
		button.Click += delegate {
			try {
				Clipboard.SetText (_txt.Text);
				MessageBox.Show (this, "日志已复制到剪贴板，可直接粘贴。", "日志");
			} catch (Exception ex) {
				MessageBox.Show (this, "复制失败：" + ex.Message, "日志");
			}
		};
		Button button2 = new Button {
			Text = "全选",
			Location = new Point (126, 556),
			Size = new Size (80, 32)
		};
		button2.Click += delegate {
			_txt.Focus ();
			_txt.SelectAll ();
		};
		Button button3 = new Button {
			Text = "重新载入",
			Location = new Point (214, 556),
			Size = new Size (100, 32)
		};
		button3.Click += delegate {
			Reload ();
		};
		Button button4 = new Button {
			Text = "打开日志文件",
			Location = new Point (322, 556),
			Size = new Size (120, 32)
		};
		button4.Click += delegate {
			try {
				Process.Start (new ProcessStartInfo (AppLog.FilePath) {
					UseShellExecute = true
				});
			} catch (Exception ex) {
				MessageBox.Show (this, "打开失败：" + ex.Message, "日志");
			}
		};
		Button button5 = new Button {
			Text = "清空日志",
			Location = new Point (450, 556),
			Size = new Size (100, 32)
		};
		button5.Click += delegate {
			AppLog.Clear ();
			Reload ();
		};
		base.Controls.AddRange (new Control[5] { button, button2, button3, button4, button5 });
		AppLog.LineAdded += OnLine;
		Reload ();
	}

	private void Reload ()
	{
		_txt.Text = AppLog.All ();
		_txt.SelectionStart = _txt.TextLength;
		_txt.ScrollToCaret ();
	}

	private void OnLine (string line)
	{
		if (base.IsDisposed) {
			return;
		}
		try {
			if (base.InvokeRequired) {
				BeginInvoke ((Action)(() => {
					OnLine (line);
				}));
				return;
			}
			_txt.AppendText (line + "\r\n");
			if (_txt.Lines.Length > 3000) {
				string[] value = _txt.Lines.Skip (_txt.Lines.Length - 3000).ToArray ();
				_txt.Text = string.Join ("\r\n", value);
			}
			_txt.SelectionStart = _txt.TextLength;
			_txt.ScrollToCaret ();
		} catch {
		}
	}

	protected override void OnFormClosed (FormClosedEventArgs e)
	{
		AppLog.LineAdded -= OnLine;
		base.OnFormClosed (e);
	}
}
