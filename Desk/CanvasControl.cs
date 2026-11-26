using System;

namespace desk;

/// <summary>
/// Web 端画板工具按钮 → WPF 主窗口的静态桥。
/// desk 侧运行在 HttpListener 工作线程，不引用 PresentationFramework；
/// 由 MainWindow 在构造时注册一个把动作切回 UI 线程的实现。
/// </summary>
public static class CanvasControl
{
    /// <summary>
    /// (cmd, arg) => 是否已受理。
    /// cmd: color / eraser / clear / mode / whiteboard / replay / shape；arg: 颜色名（color 时用）。
    /// </summary>
    public static Func<string, string, bool>? Apply;

    /// <summary>
    /// 墨迹识别开关的当前状态。WPF 侧注册为只读设置字段的委托，
    /// HttpListener 工作线程可直接调用，不需要切回 UI 线程。
    /// </summary>
    public static Func<bool>? ShapeStateProvider;

    /// <summary>
    /// 导出当前画布笔迹为 ISF 字节（.icstk 内容）。WPF 侧注册，内部切 UI 线程。
    /// 供生态白板笔迹共享（whiteboard.stroke）使用；返回 null 表示不可用。
    /// </summary>
    public static Func<byte[]>? ExportStrokes;

    /// <summary>
    /// 把 ISF 字节（.icstk 内容）合并到当前画布。WPF 侧注册，内部切 UI 线程。
    /// 返回是否已受理。
    /// </summary>
    public static Func<byte[], bool>? ImportStrokes;

    public static bool Invoke(string cmd, string arg = "")
    {
        var apply = Apply;
        if (apply == null) return false;
        try { return apply(cmd, arg); }
        catch { return false; }
    }
}
