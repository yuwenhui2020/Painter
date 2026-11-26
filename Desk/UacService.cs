using System.ServiceProcess;
using System.Threading;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace desk;

/// <summary>UAC 支持服务：以 LocalSystem 在 session 0 运行，负责在用户会话内拉起 -host 被控端。
/// 合并进 painter 后由 <see cref="DeskBootstrap"/>（-service 分支）承载。</summary>
public sealed class UacService : ServiceBase
{
	private Thread? _worker;

	private volatile bool _stopping;

	public UacService ()
	{
		base.ServiceName = AppIdentity.ServiceName;
		base.CanStop = true;
		base.CanShutdown = true;
		base.CanHandleSessionChangeEvent = true;
	}

	protected override void OnStart (string[] args)
	{
		_stopping = false;
		_worker = new Thread (Loop) {
			IsBackground = true
		};
		_worker.Start ();
	}

	protected override void OnStop ()
	{
		_stopping = true;
		_worker?.Join (5000);
		SessionLauncher.StopAll ();
	}

	protected override void OnShutdown ()
	{
		OnStop ();
	}

	protected override void OnSessionChange (SessionChangeDescription changeDescription)
	{
		base.OnSessionChange (changeDescription);
		try {
			SessionLauncher.EnsureHostRunning ();
		} catch {
		}
	}

	private void Loop ()
	{
		// 裸后台线程：整圈包 try/catch，避免 EnsureHostRunning / Sleep 的异常逃逸导致进程退出。
		while (!_stopping) {
			try {
				SessionLauncher.EnsureHostRunning ();
				for (int i = 0; i < 30; i++) {
					if (_stopping) {
						break;
					}
					Thread.Sleep (100);
				}
			} catch {
				try { Thread.Sleep (100); } catch { break; }
			}
		}
	}
}
