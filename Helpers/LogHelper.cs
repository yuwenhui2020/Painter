using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace Painter.Helpers
{
    class LogHelper
    {
        public static string LogFile = "Log.txt";

        /// <summary>日志文件大小上限，超过后改名归档（防止长时间使用无限膨胀）。</summary>
        private const long MaxLogBytes = 5 * 1024 * 1024;

        /// <summary>
        /// 日志改为后台线程异步写入：WriteLogToFile 只负责格式化并入队，
        /// 真正的文件 I/O 由专用后台线程完成，避免 UI 线程、PPT COM 回调线程
        /// 在热路径（翻页、截图、加载墨迹）上被同步文件写入阻塞，造成界面卡顿。
        /// 后台线程是 IsBackground，会随进程退出自动结束；退出前调用 Flush() 可清空队列。
        /// </summary>
        private static readonly ConcurrentQueue<string> _logQueue = new ConcurrentQueue<string>();
        private static readonly object _writerInitLock = new object();
        private static bool _writerStarted;

        public static void NewLog(string str)
        {
            WriteLogToFile(str, LogType.Info);
        }

        /// <summary>原实现是空方法，传入的异常被完全丢弃。现改为写入日志。</summary>
        public static void NewLog(Exception ex)
        {
            if (ex == null) return;
            WriteLogToFile(ex.ToString(), LogType.Error);
        }

        public static void WriteLogToFile(string str, LogType logType = LogType.Info)
        {
            string line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}{3}",
                DateTime.Now, LogTypeName(logType), str, Environment.NewLine);
            _logQueue.Enqueue(line);
            EnsureWriter();
        }

        /// <summary>进程退出前调用：最多等待 1 秒清空队列，避免尾部日志丢失。</summary>
        public static void Flush()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (_logQueue.Count > 0 && sw.ElapsedMilliseconds < 1000)
            {
                // 同步帮写：主线程直接取一条写一条，不依赖后台线程调度
                if (_logQueue.TryDequeue(out var line))
                {
                    TryWrite(line);
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
        }

        private static string LogTypeName(LogType logType)
        {
            switch (logType)
            {
                case LogType.Event: return "Event";
                case LogType.Trace: return "Trace";
                case LogType.Error: return "Error";
                default: return "Info";
            }
        }

        private static void EnsureWriter()
        {
            if (_writerStarted) return;
            lock (_writerInitLock)
            {
                if (_writerStarted) return;
                _writerStarted = true;
                var thread = new Thread(WriterLoop)
                {
                    IsBackground = true,
                    Name = "LogWriter"
                };
                thread.Start();
            }
        }

        private static void WriterLoop()
        {
            // 裸后台线程：一旦异常逃逸（如 Sleep 被中断、磁盘瞬时不可写）会直接终止进程。
            // 整圈包一层 try/catch，单条失败只跳过本轮，日志线程常驻不退出。
            while (true)
            {
                try
                {
                    if (_logQueue.TryDequeue(out var line))
                    {
                        TryWrite(line);
                    }
                    else
                    {
                        // 队列空时短暂休眠，避免空转占满 CPU
                        Thread.Sleep(20);
                    }
                }
                catch
                {
                    try { Thread.Sleep(50); } catch { break; }
                }
            }
        }

        private static void TryWrite(string line)
        {
            try
            {
                string root = App.RootPath;
                if (string.IsNullOrEmpty(root)) return;
                // CreateDirectory 本身幂等，避免 Exists + Create 的竞态
                Directory.CreateDirectory(root);
                string file = root + LogFile;
                RollLogIfTooLarge(file);
                // File.AppendAllText 内部用 using + FileShare.Read，写完即关闭，
                // 不会像 StreamWriter 那样把文件长期锁住
                File.AppendAllText(file, line, Encoding.UTF8);
            }
            catch
            {
                // 日志失败不能影响主流程
            }
        }

        /// <summary>超过上限时把当前日志改名为 Log.txt.1（只保留一份历史），避免无限膨胀。</summary>
        private static void RollLogIfTooLarge(string file)
        {
            try
            {
                FileInfo info = new FileInfo(file);
                if (!info.Exists || info.Length < MaxLogBytes) return;
                string archived = file + ".1";
                if (File.Exists(archived)) File.Delete(archived);
                File.Move(file, archived);
            }
            catch
            {
            }
        }

        public enum LogType
        {
            Info,
            Trace,
            Error,
            Event
        }
    }
}
