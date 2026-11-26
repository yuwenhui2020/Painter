using System.Collections.Generic;

namespace Painter.Helpers
{
    /// <summary>
    /// 开源参考项目条目：名称、仓库地址与一句话说明。
    /// </summary>
    public sealed class OpenSourceReference
    {
        public OpenSourceReference(string name, string url, string description)
        {
            Name = name;
            Url = url;
            Description = description;
        }

        public string Name { get; }
        public string Url { get; }
        public string Description { get; }
    }

    /// <summary>
    /// 开源参考项目列表。新增参考项目时只需在 All 中追加一条。
    /// </summary>
    public static class OpenSourceReferences
    {
        public static IReadOnlyList<OpenSourceReference> All { get; } = new List<OpenSourceReference>
        {
            new OpenSourceReference(
                "Ink-Canvas",
                "https://github.com/WXRIW/Ink-Canvas",
                "面向交互式白板的开源画板项目，本项目的主界面与墨迹交互均参考自它。"),
        };
    }
}
