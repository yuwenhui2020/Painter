using System;

namespace desk;

/// <summary>
/// 画板（WPF 侧）向远程控制模块提供的回调，用于「托盘 ⇄ 画板」的双向唤起。
/// 本接口不引用 PresentationFramework，实现类在 Painter 命名空间下。
/// </summary>
public interface IHostBridge
{
	/// <summary>画板当前是否可见（决定托盘菜单显示“退出画板”还是“开启画板”）。</summary>
	bool IsCanvasVisible { get; }

	/// <summary>显示或隐藏画板。visible=false 时画板缩回后台，托盘与远控继续运行。</summary>
	void SetCanvasVisible(bool visible);

	/// <summary>彻底退出整个应用（画板 + 远控 + 托盘）。</summary>
	void Shutdown();
}
