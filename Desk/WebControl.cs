using System;

namespace desk;

/// <summary>
/// Web 控制台的启停入口，供 WPF「快捷设置」里的开关调用。
/// desk 侧的 Form1 在构造时注册实际实现；WPF 侧不需要引用 WinForms 控件，
/// 也不必知道 Form1 在哪个线程上运行。
/// Web 控制台默认不启动，只有用户主动打开开关（或走托盘菜单）时才拉起服务。
/// </summary>
public static class WebControl
{
    // Form1 的 STA 线程注册，WPF 侧 Task.Run 轮询读取，需保证跨线程可见
    private static volatile Func<bool, string>? _apply;

    public static void Register(Func<bool, string> apply) => _apply = apply;

    public static void Unregister() => _apply = null;

    public static bool IsAvailable => _apply != null;

    /// <summary>enabled=true 启动、false 停止。返回可直接展示给用户的状态文本。</summary>
    public static string SetEnabled(bool enabled)
    {
        var apply = _apply;
        if (apply == null) return "Web 控制模块尚未就绪，请稍后重试";
        try { return apply(enabled); }
        catch (Exception ex) { return "操作失败：" + ex.Message; }
    }
}
