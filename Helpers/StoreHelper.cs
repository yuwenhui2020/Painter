namespace Painter.Helpers
{
    public static class StoreHelper
    {
        public static bool IsStoreApp
        {
            get
            {
                // Win7 没有 WinRT 运行时：直接短路，避免每次启动都触发
                // WinRT 元数据激活失败和 FirstChanceException 崩溃日志噪声。
                // Win8+ 仍保留原有的 Package.Current 探测（Store/MSIX 版路径依赖它）。
                if (OsSupport.IsWindows7OrOlder) return false;
                try
                {
                    object GetCurrentPackage()
                    {
                        return Windows.ApplicationModel.Package.Current;
                    }

                    if (GetCurrentPackage() != null)
                    {
                        return true;
                    }
                    return false;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
