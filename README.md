# Painter 画板

Painter 是AhoChat系列的windows画板端!可以搭配[AhochatA](https://github.com/yuwenhui2020/AhoChatA)使用

Painter 是一款运行在 Windows 上的**透明全屏数字墨迹覆盖层**工具，面向课堂讲授、会议演示与屏幕标注场景。它悬浮在所有窗口之上，可以在任意界面（尤其是 PowerPoint 放映）上直接书写、圈画、擦除，同时内置截图、抽奖、倒计时、墨迹回放、物理教学图形、远程桌面与 Web 控制台。

项目由 Ink Canvas 演进而来，针对希沃白板等一体机做了专门优化：启动快、资源占用低，并把黑/白板与幻灯片放映下的书写体验统一起来。程序为 **32 位 x86** 的单 exe，画板、托盘、远控与 PPT 控制模块同进程运行；关闭画板只是缩回后台托盘，只有显式退出才会结束进程。

---

## 快速开始

### 运行环境

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 7 SP1 / 8 / 8.1 / 10 / 11 |
| .NET Framework | 4.6.2 或更高（Win10/11 自带 4.8，无需单独安装；Win7 需先装 SP1 再装 4.8） |
| Office（可选） | 需要 PPT 放映联动时安装 Microsoft Office；WPS 支持有限 |
| 架构 | 程序固定为 x86；依赖 `IACore/IALoader/IAWinFX` 等 32 位原生墨迹识别库 |

### 运行

- 直接运行 `painter.exe`。

### 从源码构建

```bat
build.bat
```

脚本会自动定位 Visual Studio 的 MSBuild，执行 `Release|x86` 全量重编译，产物为 `bin\x86\Release\painter.exe`。需要 Visual Studio 的“.NET 桌面开发”工作负载，以及 .NET Framework 4.6.2 目标包。也可用 Visual Studio 直接打开 `Painter.sln`。

---

## 使用模式

| 模式 | 说明 |
| --- | --- |
| **幻灯片模式** | 检测正在放映的 PowerPoint，提供从头放映/结束放映/上一页/下一页；支持画笔、图形工具、翻页自动换笔迹、自动保存墨迹与自动截图；遇到隐藏幻灯片会询问是否取消隐藏。 |
| **画板模式（黑板/白板）** | 一整块类似希沃白板的无限画板，支持新增页面与页面切换、多指书写；默认黑板，可在设置中切换为白板。 |
| **屏幕画笔模式** | 保留原屏幕内容，鼠标变为画笔直接在屏幕上书写标注，适合浏览器、PDF、视频会议等任意窗口。 |

---

## 主要功能

### 书写与墨迹

- 笔细的一端书写、粗的一端当橡皮（按笔画擦除），无需切换模式；手指或倾斜笔尖可精确擦除小范围墨迹。
- 单条或多条墨迹可选中后缩放、旋转、移动、克隆；全屏支持双指缩放/拖动/旋转。
- 画笔粗细、颜色、笔锋（按速度/尾迹模拟）可调；压感笔支持。
- 撤销/恢复：`Ctrl+Z` 撤销，再次点击界面“撤销/恢复”按钮可重做。
- 墨迹自动保存到 `文档\painter Strokes\Auto Saved\Presentations\`，手动保存到 `文档\painter Strokes\User Saved\`；可在设置中关闭自动保存或调整触发页数。

### 智能几何识别与笔迹平滑

- **几何识别**：收笔时用纯 C# 几何算法把单笔手绘规整为直线、箭头、矩形、三角形、圆/椭圆。序列化去重 → 闭合判定 → RDP 抽角点 → 径向方差分类，O(n) 复杂度，无 x86 原生依赖，Win7–Win11 行为一致。识别失败时保留原始手绘，不会误规整。
- **总开关约束**：几何识别与旧的 InkAnalyzer 一样受“墨迹识别”总开关控制；关闭后不会有任何笔迹被转成形状。旧引擎作为多笔识别的兜底保留。
- **笔迹平滑**：收笔时对非压感笔、点数 > 4 的笔画做保端点低通滤波，去掉手指/鼠标抖动；压感笔保持原始精度。强度可在配置中调整（默认 0.35）。
- 相关开关在 `Settings.json` 的 `smart` 节点，见下文“设置说明”。

### 智能教学工具

三个全局热键控制最顶层透明覆盖窗口，鼠标事件穿透，不影响书写与 PPT 翻页：

| 工具 | 热键 | 效果 |
| --- | --- | --- |
| 激光笔 | `Ctrl+Alt+L` | 红色光点跟随光标，静止约 1.2 秒自动淡出，半径可配 |
| 聚光灯 | `Ctrl+Alt+S` | 全屏半透明黑遮罩 + 跟随光标的透明圆洞 |
| 放大镜 | `Ctrl+Alt+M` | 光标周围 3 倍放大，圆形裁剪显示；光标静止时降频刷新 |

再次按同一热键关闭当前工具；按另一个热键直接切换工具。该功能可用 `smart.teachingToolsEnabled` 关闭。

### 物理教学工具

面向电磁学板书的专用工具，**所有符号都是普通墨迹**，走既有保存/撤销体系：

1. 用圆工具画一个圆（正圆/椭圆均可，手绘圆被规整后也行）。
2. 切换到“选择墨迹”（`Alt+S`）并单击这个圆，底部浮动条会多出一排“物理”按钮。
3. 选择 `⊙ 点`（矢量垂直纸面向外）、`⊗ 叉`（矢量垂直纸面向里）或“清除”；符号按圆的短轴自动缩放并居中铺满，切换与清除都可撤销。
4. 浮动菜单还可插入电流方向箭头、电阻、电容、电感线圈、二极管、接地、电源、开关、LED 等电磁符号。

另有两种入口：图形库最后两个按钮可拖出矩形区域，自动整齐铺满 `⊙` / `⊗` 阵列（数量随区域大小增减，有上限防卡顿）。圆/椭圆绘制采用按周长自适应采样（约每 5 像素一个点），大圆预览的点数约为旧实现的 1/6，跟手度明显提升。

### 预设图形库

支持直线、虚线、带箭头直线、平行线（适合画匀强电场线）、带/不带焦点的椭圆、双曲线、抛物线、正圆、虚线圆、圆柱、圆锥、长方体、平面直角/空间直角坐标系，以及竖直/水平平面镜、凸透镜、凹透镜等光学图形。图形库新增两行物理图元：

| 图元 | 模式 | 图元 | 模式 |
| --- | --- | --- | --- |
| 电源/电池 | 33 | 正弦波形 | 37 |
| 定值电阻 | 34 | 弹簧 | 38 |
| 开关 | 35 | 正点电荷 ⊕ | 39 |
| 小灯泡 | 36 | 负点电荷 ⊖ | 40 |

磁场向外 ⊙ / 向里 ⊗ 为模式 41 / 42。图形库标题为“图形 / 物理教学图形库”。

### 截图与保存

点击相机图标（或 `Alt+C`）截取全屏，统一保存到桌面的 `笔迹截图` 文件夹：

| 场景 | 位置 | 文件名 |
| --- | --- | --- |
| PowerPoint 放映中 | `桌面\笔迹截图\<PPT名称>\` | `第X页墨迹.png`（X 为页码） |
| 桌面/屏幕标注 | `桌面\笔迹截图\屏幕标记\` | `年-月-日 时：分：秒.png`（全角冒号 `：`，Windows 文件名限制） |

PPT 名称中的非法文件名字符会自动替换为下划线。可在设置中开启“截图时自动保存墨迹”“清屏时自动截图”。

### 其它教学小工具

- **倒计时**：美观的 UI，可接近全屏显示。
- **抽奖**：可导入名单（建议配合 Excel），设置抽取人数，随机点名。
- **墨迹回放**：自动从头把屏幕中的墨迹重新书写一遍。
- **PPT 缩略图总览**：放映时点击页码可展开自带缩略图面板，点击任意缩略图跳页；关闭开关或导出失败时回退到 PowerPoint 原生导航窗格。缩略图缓存在系统临时目录，不写安装目录。

### 远程控制与 Web 控制台

托盘模块与画板同进程运行，提供远程桌面、剪贴板同步、图片展示、PPT 控制与 Web 远控。`painter.exe` 支持三种独立启动模式，供 UAC 支持与会话启动器使用：

- `painter.exe -service`：session 0 服务模式（无交互桌面，不加载 WPF）。
- `painter.exe -host`：远程桌面被控端。
- `painter.exe -web`：Web 远控网关。

Web 控制台的端口/密码在托盘界面中配置，启动后用浏览器访问 `http://<本机IP>:<端口>/`，页面入口包括：

- `/ppt` —— 幻灯片控制与画面
- `/remotedesk` —— 远程桌面
- `/show` —— 演示控制
- `/share` —— 共享文件夹
- `/file` —— 文件传输
- `/system` —— 系统操作

Win7 上 DXGI 桌面复制自动回退 GDI BitBlt，H264 不可用时走 JPEG/软件路径；首次运行若被防火墙拦截，需放行程序所用端口。

---

## 快捷键

| 按键 | 功能 |
| --- | --- |
| `Shift+Esc` | 结束放映 / 退出 |
| `Ctrl+Z` | 撤销 |
| `Alt+S` / `Alt+D` / `Alt+E` | 选择墨迹 / 画笔 / 橡皮擦 |
| `Alt+C` | 屏幕截图 |
| `Alt+1` ~ `Alt+5` | 切换画笔颜色（黑/红/绿/蓝/黄），同时退出橡皮状态 |
| `Alt+V` | 隐藏/显示画板 |
| `Alt+L` | 画直线 |
| `Ctrl+Alt+L` / `Ctrl+Alt+S` / `Ctrl+Alt+M` | 激光笔 / 聚光灯 / 放大镜（全局） |
| 方向键 / `PageUp` / `PageDown` / `P` / `N` / `Space` / 滚轮 | 放映时翻页 |
| `Esc` | 结束放映 |

---

## 设置说明

运行后会在程序目录生成 `Settings.json`，修改后保存即自动落盘。主要分组：

| 节点 | 内容 |
| --- | --- |
| `advanced` | 特殊屏幕、触摸面积橡皮擦、触摸倍率、日志开关 |
| `appearance` | 主题、透明按钮背景、各按钮显示/隐藏 |
| `automation` | 截图/清屏时自动保存墨迹、自动查杀希沃/PPT 小工具 |
| `powerPointSettings` | PPT 支持开关、放映时显示画板、翻页按钮位置、缩略图总览、WPS 支持 |
| `canvas` | 画笔粗细、笔锋、橡皮、白板/黑板、自动性能降级 |
| `gesture` | 双指缩放/平移/旋转 |
| `inkToShape` | 墨迹识别总开关 |
| `roster` | 点名名单导入时间与文件名 |
| `startup` | 启动自动进入手指模式/自动隐藏画板 |
| `smart` | 智能功能，见下表 |

`smart` 节点：

```json
"smart": {
  "geometryShapeEnabled": true,
  "shapeMinSpan": 40,
  "shapeTolerance": 0.25,
  "strokeSmoothingEnabled": true,
  "strokeSmoothingStrength": 0.35,
  "teachingToolsEnabled": true,
  "autoProfileEnabled": true,
  "laserRadius": 12
}
```

- `geometryShapeEnabled`：几何识别开关；`shapeMinSpan`：触发识别的最小笔迹跨度（像素）；`shapeTolerance`：判定容差（越大越容易规整）。
- `strokeSmoothingEnabled` / `strokeSmoothingStrength`：笔迹平滑开关与强度（0–1）。
- `teachingToolsEnabled`：教学工具热键开关；`laserRadius`：激光点半径（4–80）。
- `autoProfileEnabled`：按系统/硬件自动选择能力档位。
- 旧版配置缺少 `smart` 节点时会自动补齐默认值，不会破坏原有设置。

> 配置读写使用 `System.Text.Json` 的 camelCase 命名策略；`Settings.cs` 上的 Newtonsoft `[JsonProperty]` 标注仅作兼容注释，实际键名以 CLR 属性名的小驼峰形式为准。

---

## 目录结构

```
Painter/
├── Program.cs                  进程入口（含 Win7 初始化与 -service/-host/-web 模式分派）
├── App.xaml(.cs)               应用启动、单实例互唤、托盘/远控拉起与进程清理
├── MainWindow.xaml(.cs)        画板主界面与全部交互逻辑
├── Settings.cs                 配置数据模型
├── CanvasActivator.cs          画板激活逻辑
├── CountdownTimerWindow.*      倒计时
├── RandWindow.*                抽奖
├── ChangeLogWindow.* / WelcomeWindow.* / NamesInputWindow.*  其它窗口
├── Controls/                   自定义控件
├── Desk/                       托盘、远程桌面、Web 控制台、PPT 控制、文件传输等后台模块
├── Helpers/                    几何识别、笔迹平滑、物理工具、教学工具、日志、热键、OS 能力判断等
├── Resources/                  图标、图片、样式、矢量图形库
├── web/                        Web 控制台静态页面（app.js / ppt / remotedesk / show / share / file / system）
├── Properties/                 程序集信息
├── bin/x86/Release/            编译产物（painter.exe 及运行依赖）
├── check-win7.ps1              Win7 启动自检（PowerShell 2.0 兼容）
├── run-check-win7.bat          Win7 推荐启动入口
├── build.bat                   一键全量编译
├── Manual.md                   详细使用说明
├── DEVELOPMENT.md              架构与开发说明
├── 智能化与跨平台增强说明.md      智能功能与平台覆盖说明
├── 物理教学工具与丝滑度优化说明.md  物理工具与绘制优化说明
├── WIN7兼容性修复说明.md         Win7 缺 DLL 问题分析与修复记录
└── privacy.txt                 隐私政策
```

---

## Windows 7 兼容性

- 程序固定 x86，因为墨迹识别依赖 32 位原生 DLL；在 64 位 Windows 上通过 WOW64 正常运行。
- **SkiaSharp 固定在 2.88.8**：3.x 的原生库依赖 Windows 10 独有的 `d3d12.dll` / `D3DCOMPILER_47.dll`，在 Win7 上启动即报“缺少多个 DLL”。2.88.8 的原生库只依赖 Win7 自带的 `FONTSUB/KERNEL32/USER32/ole32`，所需 JPEG 编解码 API 两版一致。`App.config` 与 `painter.exe.config` 中保留了 3.119→2.88 的绑定重定向。
- 源码已针对 Win7 做分支处理：TLS 1.2 强制启用、`SetProcessDPIAware()` 兜底、DXGI 桌面复制回退 GDI、H264 编码不可用时走软件路径、跳过 WinRT/Mica/Acrylic 材质。
- `app.manifest` 声明了 Win7–Win11 全系列 supportedOS；图标为自制矢量图标，不依赖 Segoe MDL2 字体。
- 用 `run-check-win7.bat` 启动可提前发现 .NET 缺失、文件不全、位数错误或 SkiaSharp 版本回退。

特别感谢 Ink Canvas 项目的各位贡献者。

## 许可

本项目遵循 [GPL-3.0](LICENSE)。使用、修改与分发请遵守许可证条款。
