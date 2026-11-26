using System;
using System.Windows;
using System.Windows.Media;
using iNKORE.UI.WPF.Modern.Helpers.Styles;

namespace Painter.Helpers
{
    /// <summary>
    /// 给普通窗口挂上系统背景材质：Win11 22H2+ 用 Mica，Win10 1803+ 退回亚克力，
    /// 都不支持时保持原来的纯色背景。
    /// 分层窗口（AllowsTransparency=True）没有 DWM 背景，不能使用；
    /// 主画板正是这种覆盖在其它程序之上的透明窗口，所以材质只用于独立对话框。
    /// </summary>
    internal static class WindowBackdrop
    {
        public static void Apply(Window window)
        {
            if (window == null || window.AllowsTransparency) return;

            // 必须等首次呈现完成后再挂材质：如果在窗口显示前就把背景改成透明，
            // Win10 上首帧会只显示模糊层，WPF 内容要等下一次强制重绘才出现。
            window.ContentRendered += OnContentRendered;
        }

        static void OnContentRendered(object sender, EventArgs e)
        {
            var window = sender as Window;
            if (window == null) return;
            window.ContentRendered -= OnContentRendered;
            ApplyCore(window);
        }

        static void ApplyCore(Window window)
        {
            // Win7/8 没有 DWM 背景材质，装上反而会让窗口变透明或黑底。
            if (!OsSupport.SupportsBackdropMaterial) return;
            try
            {
                // 背景必须让出透明区域，材质才能透出来；
                // 材质没挂上时恢复原背景，避免窗口变成黑底。
                Brush original = window.Background;
                window.Background = Brushes.Transparent;

                bool applied = false;
                if (MicaHelper.IsSupported(BackdropType.Mica))
                    applied = MicaHelper.Apply(window, BackdropType.Mica, true);
                else if (AcrylicHelper.IsSupported())
                    applied = AcrylicHelper.Apply(window, true);

                if (!applied)
                    window.Background = original;
            }
            catch
            {
                // 材质只是装饰，失败时保留对话框原有的纯色背景
            }
        }
    }
}
