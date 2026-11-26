using System;
using System.Reflection;

namespace Painter.Helpers
{
    /// <summary>
    /// 项目对外信息集中定义：作者、源码主页、更新记录、问题反馈与当前版本。
    /// 设置页“项目信息”分组直接引用这些常量，避免多处硬编码导致链接或作者信息不一致。
    /// </summary>
    public static class ProjectInfo
    {
        /// <summary>当前项目作者 / 维护者。</summary>
        public const string Author = "yuwenhui2020";

        public const string AuthorUrl = "https://github.com/yuwenhui2020";

        /// <summary>GitHub 仓库主页（用户 tracking 更新的入口）。</summary>
        public const string ProjectUrl = "https://github.com/yuwenhui2020/Painter";

        public const string ReleasesUrl = ProjectUrl + "/releases";

        public const string IssuesUrl = ProjectUrl + "/issues";

        public const string ReadmeUrl = ProjectUrl + "#readme";

        /// <summary>同系列的 AhoChat Android 端。</summary>
        public const string CompanionUrl = "https://github.com/yuwenhui2020/AhoChatA";

        // XAML 的 Hyperlink.NavigateUri 需要 Uri 类型；这里直接提供 Uri，避免依赖字符串转换。
        public static readonly Uri AuthorUri = new Uri(AuthorUrl, UriKind.Absolute);
        public static readonly Uri ProjectUri = new Uri(ProjectUrl, UriKind.Absolute);
        public static readonly Uri ReleasesUri = new Uri(ReleasesUrl, UriKind.Absolute);
        public static readonly Uri IssuesUri = new Uri(IssuesUrl, UriKind.Absolute);
        public static readonly Uri ReadmeUri = new Uri(ReadmeUrl, UriKind.Absolute);
        public static readonly Uri CompanionUri = new Uri(CompanionUrl, UriKind.Absolute);

        /// <summary>当前程序集版本，用于设置页展示，便于用户反馈问题时附上版本号。</summary>
        public static string VersionText
        {
            get
            {
                try
                {
                    return Assembly.GetExecutingAssembly().GetName().Version.ToString();
                }
                catch
                {
                    return "未知";
                }
            }
        }
    }
}
