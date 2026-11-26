using Newtonsoft.Json;

namespace Painter
{
    public class Settings
    {
        [JsonProperty("advanced")]
        public Advanced Advanced { get; set; } = new Advanced();
        [JsonProperty("appearance")]
        public Appearance Appearance { get; set; } = new Appearance();
        [JsonProperty("automation")]
        public Automation Automation { get; set; } = new Automation();
        [JsonProperty("behavior")]
        public PowerPointSettings PowerPointSettings { get; set; } = new PowerPointSettings();
        [JsonProperty("canvas")]
        public Canvas Canvas { get; set; } = new Canvas();
        [JsonProperty("gesture")]
        public Gesture Gesture { get; set; } = new Gesture();
        [JsonProperty("inkToShape")]
        public InkToShape InkToShape { get; set; } = new InkToShape();
        [JsonProperty("startup")]
        public Startup Startup { get; set; } = new Startup();
        [JsonProperty("roster")]
        public Roster Roster { get; set; } = new Roster();
        [JsonProperty("smart")]
        public Smart Smart { get; set; } = new Smart();
    }

    /// <summary>
    /// 「智能画板」相关开关。全部默认安全可用；任何一项都可在设置里单独关闭。
    /// 这些字段是后加的，旧版 Settings.json 缺节点时由 LoadSettings 补足。
    /// </summary>
    public class Smart
    {
        // 智能形状识别：用纯几何算法把单笔手绘规整为直线/圆/椭圆/矩形/三角形/箭头，
        // 不依赖 x86-only 的 InkAnalyzer，速度比旧引擎快一个数量级且 Win7-Win11 一致。
        [JsonProperty("geometryShapeEnabled")]
        public bool GeometryShapeEnabled { get; set; } = true;

        // 触发几何识别的最小笔迹跨度（像素）。低于此值视为小涂鸦，不规整，避免误判。
        [JsonProperty("shapeMinSpan")]
        public double ShapeMinSpan { get; set; } = 40;

        // 规整判定容差：越大越容易判成标准形状。0.25 对教学手绘比较宽容。
        [JsonProperty("shapeTolerance")]
        public double ShapeTolerance { get; set; } = 0.25;

        // 智能笔迹平滑：收笔时对点序列做轻量低通滤波，去掉手指/鼠标抖动，书写更顺滑。
        // 只在非压感笔、且点数足够时启用；压感笔本身精度高，不动它。
        [JsonProperty("strokeSmoothingEnabled")]
        public bool StrokeSmoothingEnabled { get; set; } = true;

        // 平滑强度 0~1。0.35 在「去抖」和「跟手」之间较平衡。
        [JsonProperty("strokeSmoothingStrength")]
        public double StrokeSmoothingStrength { get; set; } = 0.35;

        // 智能教学工具（激光笔/聚光灯/放大镜）是否在主界面显示入口。
        [JsonProperty("teachingToolsEnabled")]
        public bool TeachingToolsEnabled { get; set; } = true;

        // 是否在启动时按系统 + 硬件自动生成最优配置（透明覆盖层 / 远控降级等）。
        [JsonProperty("autoProfileEnabled")]
        public bool AutoProfileEnabled { get; set; } = true;

        // 激光笔 / 聚光灯 / 放大镜的半径（像素）。
        [JsonProperty("laserRadius")]
        public double LaserRadius { get; set; } = 12;
    }

    /// <summary>点名器导入名单的元信息：记录导入时间（年-月-日），防止学生私自替换名单。</summary>
    public class Roster
    {
        [JsonProperty("importDate")]
        public string ImportDate { get; set; } = "";
        [JsonProperty("fileName")]
        public string FileName { get; set; } = "";
    }

    public class Canvas
    {
        [JsonProperty("inkWidth")]
        public double InkWidth { get; set; } = 2.5;
        [JsonProperty("isShowCursor")]
        public bool IsShowCursor { get; set; } = false;
        // 0 = 按墨迹尾部模拟笔锋，1 = 按速度模拟笔锋，2 = 无笔锋。
        // 笔锋要在每笔收笔后重建全部 StylusPoint 并触发整条重绘，是低配一体机上
        // 最明显的收笔延迟来源，因此默认取「无笔锋」；需要观感可在设置里改回。
        [JsonProperty("inkStyle")]
        public int InkStyle { get; set; } = 2;
        /// <summary>按机器能力自动降级墨迹保真度（低配/Win7 自动关笔锋与墨迹识别）。</summary>
        [JsonProperty("autoPerformance")]
        public bool AutoPerformance { get; set; } = true;
        /// <summary>自动优化只执行一次；执行过之后尊重用户手动选择的笔锋与墨迹识别设置。</summary>
        [JsonProperty("autoPerformanceApplied")]
        public bool AutoPerformanceApplied { get; set; } = false;
        [JsonProperty("eraserSize")]
        public int EraserSize { get; set; } = 2;
        [JsonProperty("eraserType")]
        public int EraserType { get; set; } = 0; // 0 - 图标切换模式      1 - 面积擦     2 - 线条擦
        [JsonProperty("hideStrokeWhenSelecting")]
        public bool HideStrokeWhenSelecting { get; set; } = true;

        [JsonProperty("usingWhiteboard")]
        public bool UsingWhiteboard { get; set; }

        [JsonProperty("hyperbolaAsymptoteOption")]
        public OptionalOperation HyperbolaAsymptoteOption { get; set; } = OptionalOperation.Ask;
    }

    public enum OptionalOperation
    {
        Yes,
        No,
        Ask
    }

    public class Gesture
    {
        [JsonIgnore]
        public bool IsEnableTwoFingerGesture => IsEnableTwoFingerZoom || IsEnableTwoFingerTranslate || IsEnableTwoFingerRotation;
        [JsonProperty("isDisableLockSmithByDefault")]
        public bool IsDisableLockSmithByDefault { get; set; } = true;
        [JsonProperty("isEnableTwoFingerZoom")]
        public bool IsEnableTwoFingerZoom { get; set; } = true;
        [JsonProperty("isEnableTwoFingerTranslate")]
        public bool IsEnableTwoFingerTranslate { get; set; } = true;
        [JsonProperty("isEnableTwoFingerRotation")]
        public bool IsEnableTwoFingerRotation { get; set; } = false;
        [JsonProperty("isEnableTwoFingerRotationOnSelection")]
        public bool IsEnableTwoFingerRotationOnSelection { get; set; } = false;

    }

    public class Startup
    {
        [JsonProperty("isAutoHideCanvas")]
        public bool IsAutoHideCanvas { get; set; } = true;
        [JsonProperty("isAutoEnterModeFinger")]
        public bool IsAutoEnterModeFinger { get; set; } = false;
    }

    public class Appearance
    {
        [JsonProperty("isTransparentButtonBackground")]
        public bool IsTransparentButtonBackground { get; set; } = true;
        [JsonProperty("isShowExitButton")]
        public bool IsShowExitButton { get; set; } = true;
        [JsonProperty("isShowEraserButton")]
        public bool IsShowEraserButton { get; set; } = true;
        [JsonProperty("isShowHideControlButton")]
        public bool IsShowHideControlButton { get; set; } = false;
        [JsonProperty("isShowLRSwitchButton")]
        public bool IsShowLRSwitchButton { get; set; } = false;
        [JsonProperty("isShowModeFingerToggleSwitch")]
        public bool IsShowModeFingerToggleSwitch { get; set; } = true;
        [JsonProperty("theme")]
        public int Theme { get; set; } = 0;
    }

    public class PowerPointSettings
    {
        [JsonProperty("isShowPPTNavigation")]
        public bool IsShowPPTNavigation { get; set; } = true;
        [JsonProperty("powerPointSupport")]
        public bool PowerPointSupport { get; set; } = true;
        [JsonProperty("isShowCanvasAtNewSlideShow")]
        public bool IsShowCanvasAtNewSlideShow { get; set; } = true;
        [JsonProperty("isNoClearStrokeOnSelectWhenInPowerPoint")]
        public bool IsNoClearStrokeOnSelectWhenInPowerPoint { get; set; } = true;
        [JsonProperty("isShowStrokeOnSelectInPowerPoint")]
        public bool IsShowStrokeOnSelectInPowerPoint { get; set; } = false;
        [JsonProperty("isAutoSaveStrokesInPowerPoint")]
        public bool IsAutoSaveStrokesInPowerPoint { get; set; } = true;
        [JsonProperty("isAutoSaveScreenShotInPowerPoint")]
        public bool IsAutoSaveScreenShotInPowerPoint { get; set; } = false;
        [JsonProperty("isNotifyPreviousPage")]
        public bool IsNotifyPreviousPage { get; set; } = false;
        [JsonProperty("isNotifyHiddenPage")]
        public bool IsNotifyHiddenPage { get; set; } = false;
        /// <summary>历史配置迁移标记：旧版本把 true 写进过 isNotifyHiddenPage，需一次性归零。</summary>
        [JsonProperty("hiddenPageNoticeMigrated")]
        public bool HiddenPageNoticeMigrated { get; set; } = false;
        [JsonProperty("isEnableTwoFingerGestureInPresentationMode")]
        public bool IsEnableTwoFingerGestureInPresentationMode { get; set; } = true;
        [JsonProperty("isEnableFingerGestureSlideShowControl")]
        public bool IsEnableFingerGestureSlideShowControl { get; set; } = true;
        [JsonProperty("isSupportWPS")]
        public bool IsSupportWPS { get; set; } = true;

        /// <summary>PPT 放映翻页按钮位置：0 = 屏幕两侧底部（默认），1 = 屏幕两侧竖直居中。</summary>
        [JsonProperty("pptNavButtonPosition")]
        public int PptNavButtonPosition { get; set; } = 0;

        /// <summary>点击翻页按钮中间的页数时是否展开幻灯片缩略图（可点击跳页）。</summary>
        [JsonProperty("pptThumbnailEnabled")]
        public bool PptThumbnailEnabled { get; set; } = true;
    }

    public class Automation
    {
        [JsonProperty("isAutoSaveStrokesAtScreenshot")]
        public bool IsAutoSaveStrokesAtScreenshot { get; set; } = false;

        [JsonProperty("isAutoSaveStrokesAtClear")]
        public bool IsAutoSaveStrokesAtClear { get; set; } = false;

        [JsonProperty("isAutoClearWhenExitingWritingMode")]
        public bool IsAutoClearWhenExitingWritingMode { get; set; } = false;

        [JsonProperty("minimumAutomationStrokeNumber")]
        public int MinimumAutomationStrokeNumber { get; set; } = 0;

        // 原版 Painter 的「自动查杀希沃」系列开关：两组进程互不影响。
        // 「PPT 小工具」默认开启；希沃白板 5 保持默认关闭。
        [JsonProperty("isAutoKillPptService")]
        public bool IsAutoKillPptService { get; set; } = true;

        [JsonProperty("isAutoKillEasiNote")]
        public bool IsAutoKillEasiNote { get; set; } = false;

        // 历史配置迁移标记：旧版本默认关闭并把 false 写进了 Settings.json，
        // 需一次性改为 true，之后用户自行关闭的选择不再被重置。
        [JsonProperty("autoKillPptServiceMigrated")]
        public bool AutoKillPptServiceMigrated { get; set; } = false;

    }

    public class Advanced
    {
        [JsonProperty("isSpecialScreen")]
        public bool IsSpecialScreen { get; set; } = false;
        /// <summary>检测到希沃硬件时自动打开「特殊屏幕模式」；用户手动改过该开关后自动置 false。</summary>
        [JsonProperty("isAutoDetectSeewo")]
        public bool IsAutoDetectSeewo { get; set; } = true;
        [JsonProperty("isQuadIR")]
        public bool IsQuadIR { get; set; } = false;
        [JsonProperty("touchMultiplier")]
        public double TouchMultiplier { get; set; } = 0.25;
        [JsonProperty("eraserBindTouchMultiplier")]
        public bool EraserBindTouchMultiplier { get; set; } = false;
        /// <summary>
        /// 是否把「接触面积明显偏大的触摸」自动识别为橡皮擦（手掌/手背大面积擦除）。
        /// 默认关闭：只允许通过橡皮按钮或笔尾按键擦除，避免笔尖按重、衣服误触时被判成手掌擦，
        /// 写着写着墨迹突然消失。需要大面积擦除时可在设置里打开。
        /// </summary>
        [JsonProperty("isEnableTouchAreaEraser")]
        public bool IsEnableTouchAreaEraser { get; set; } = false;
        /// <summary>
        /// 橡皮擦接触面积识别阈值（校正后的触摸宽度，设备无关单位）。触摸宽度超过该值才判为橡皮。
        /// 值越大越不容易误判——原实现写死 5（笔模式）/15（手指模式），在希沃大屏上笔尖按重一点
        /// 就会被当成手掌擦，写着写着突然变橡皮。默认提高到 12（手指模式按 3 倍派生为 36）。
        /// 调大：书写更稳但需要更明显的接触才擦除；调小：橡皮更灵敏但易误触。
        /// </summary>
        [JsonProperty("eraserTouchThreshold")]
        public double EraserTouchThreshold { get; set; } = 12;
        [JsonProperty("isLogEnabled")]
        public bool IsLogEnabled { get; set; } = false;
    }

    public class InkToShape
    {
        // 手写识别成图形要在收笔时跑 3~6 次 InkAnalyzer.Analyze（仅 32 位进程），
        // 在希沃等 x86 一体机上每笔都会造成明显停顿，默认关闭；需要时在设置里打开。
        [JsonProperty("isInkToShapeEnabled")]
        public bool IsInkToShapeEnabled { get; set; } = false;
    }
}
