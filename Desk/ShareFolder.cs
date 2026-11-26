using System;
using System.IO;

using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace desk
{
    /// <summary>
    /// 局域网文件传输的共享根目录：桌面下的 “share” 文件夹（不存在则自动新建）。
    /// FTP 服务与网页端 /file 接口共用同一个根，确保两边看到的是同一批文件。
    /// </summary>
    internal static class ShareFolder
    {
        private static string _root;

        /// <summary>共享根目录的绝对路径（首次访问时创建）。</summary>
        public static string Root
        {
            get
            {
                if (_root != null) return _root;
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var p = System.IO.Path.Combine(desktop, "share");
                try { System.IO.Directory.CreateDirectory(p); }
                catch (Exception ex) { Log?.Invoke("创建 share 目录失败：" + ex.Message); }
                _root = p;
                return p;
            }
        }

        public static Action<string> Log;

        /// <summary>
        /// 把相对路径（支持 / 或 \ 分隔，可带或不带前导分隔符）解析为 share 根下的绝对路径，
        /// 并做目录穿越防护：任何超出 share 根的操作都会被拒绝。
        /// </summary>
        public static string Resolve(string relative)
        {
            var rel = (relative ?? "").Replace('\\', '/').Trim('/');
            var root = System.IO.Path.GetFullPath(Root);
            // root 必须带结尾分隔符再比较，否则 share_backup 等同级目录也会通过前缀校验
            string rootWithSep = root.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString())
                ? root : root + System.IO.Path.DirectorySeparatorChar;
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(rootWithSep, rel));
            if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                !full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("非法路径（超出 share 根目录）");
            return full;
        }
    }
}
