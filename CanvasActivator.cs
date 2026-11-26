using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Painter.Helpers;

namespace Painter
{
    /// <summary>
    /// 单实例互唤通道。合并后 desk 与画板同进程，重复启动本 exe 时不再像原来那样
    /// 只弹一个"已有一个程序实例正在运行"，而是通过命名管道通知已有实例把画板唤出来——
    /// 这正是"相互唤起对方"的落地方式：任何入口启动都会得到完整的一套（画板 + 托盘 + 远控）。
    /// </summary>
    internal sealed class CanvasActivator : IDisposable
    {
        private const string MutexName = "Painter";
        private const string PipeName = "Painter_Activate_v1";

        private readonly Mutex _mutex;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _listenTask;
        private bool _disposed;
        private volatile bool _shuttingDown;

        /// <summary>true 表示本进程是首个实例（应完整启动画板 + desk）。</summary>
        public bool IsFirstInstance { get; }

        public CanvasActivator()
        {
            bool createdNew;
            try
            {
                _mutex = new Mutex(true, MutexName, out createdNew);
                IsFirstInstance = createdNew;
            }
            catch (UnauthorizedAccessException)
            {
                _mutex = null;
                IsFirstInstance = false;
            }
            catch (AbandonedMutexException)
            {
                // 前一个进程异常终止遗弃了互斥锁，当前进程已获得所有权，视为首个实例
                _mutex = new Mutex(true, MutexName, out createdNew);
                IsFirstInstance = createdNew;
            }
        }

        /// <summary>通知已在运行的实例显示画板。成功则调用方应立即退出本进程。</summary>
        public static bool TryNotifyExistingInstance(int timeoutMs = 2000)
        {
            try
            {
                using (NamedPipeClientStream client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    client.Connect(timeoutMs);
                    client.WriteByte(1);
                    client.Flush();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>在后台任务上循环监听"唤出画板"请求，使用异步模式避免同步阻塞竞态。</summary>
        public void StartListening(Action onActivate)
        {
            if (!IsFirstInstance || onActivate == null)
            {
                return;
            }

            _listenTask = Task.Run(async () =>
            {
                var token = _cts.Token;
                while (!token.IsCancellationRequested)
                {
                    NamedPipeServerStream server = null;
                    try
                    {
                        server = new NamedPipeServerStream(
                            PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                        // 异步等待连接，取消令牌触发时会抛 OperationCanceledException
                        await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                        // 连接已建立，读取激活字节
                        int b = server.ReadByte();
                        if (b == 1 && !_shuttingDown)
                        {
                            try
                            {
                                // 增加异常保护：确保激活回调不会导致进程异常退出
                                onActivate();
                            }
                            catch (Exception ex)
                            {
                                try { LogHelper.NewLog("[CanvasActivator] 激活回调异常: " + ex); } catch { }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break; // 正常取消退出
                    }
                    catch (ObjectDisposedException)
                    {
                        break; // Dispose 中关闭了管道
                    }
                    catch (Exception ex)
                    {
                        // 任何其他异常：记录日志并短暂延迟后重试，避免因瞬态错误导致监听永久停止
                        try { LogHelper.NewLog("[CanvasActivator] 监听循环异常: " + ex); } catch { }
                        try { await Task.Delay(300, token).ConfigureAwait(false); } catch { break; }
                    }
                    finally
                    {
                        try { server?.Dispose(); } catch { }
                    }
                }
            }, _cts.Token);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _shuttingDown = true;
            try { _cts.Cancel(); } catch { }

            // 等待监听任务结束（最多 1.5 秒）
            try
            {
                if (_listenTask != null)
                {
                    _listenTask.Wait(TimeSpan.FromMilliseconds(1500));
                }
            }
            catch { }

            try { _cts.Dispose(); } catch { }
            try { _mutex?.Dispose(); } catch { }
        }
    }
}