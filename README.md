# StickyTodo

**简体中文** | [English](README.en.md) | [日本語](README.ja.md) | [한국어](README.ko.md)

一个从零写的 Windows 桌面便签 / 待办应用：WPF + .NET 8 + SQLite。**全程本地运行**——不联服务器、不做账号、不埋点、不检查更新；**所有用户默认就是永久会员，全部功能无限制**。界面支持**简体中文 / English / 日本語 / 한국어**四种语言，可在设置里随时切换。

> 借鉴了同类工具的产品形态（多看板、一板一窗、重复任务、日历），但**代码、UI、文案、配色全部为原创实现**，没有复制任何第三方程序的反编译产物、资源文件或私有接口。

| 便签（默认 92% 背景） | 背景 55%（看得见桌面） | 石墨夜色皮肤 |
|---|---|---|
| ![便签](artifacts/board-default.png) | ![半透明](artifacts/board-translucent.png) | ![暗色](artifacts/board-dark.png) |

10 套皮肤里的两套新配色：

| 暖橘杏（亮色） | 深棕咖（暗色） |
|---|---|
| ![暖橘杏](artifacts/board-apricot.png) | ![深棕咖](artifacts/board-espresso.png) |

![应用图标](artifacts/icon.png)

| 主窗体（无边框 + 侧栏） | 独立设置页 | 主窗体卡片右键菜单 |
|---|---|---|
| ![主窗体](artifacts/manager.png) | ![设置](artifacts/settings.png) | ![看板菜单](artifacts/manager-menu.png) |

悬停操作（按钮固定在最右侧，鼠标移入本行才出现）：

| 待办行：✓ 标记完成 + ✕ 删除 | 已完成行：↺ 撤回 + ✕ 删除 |
|---|---|
| ![悬停待办](artifacts/hover-todo.png) | ![悬停已完成](artifacts/hover-done.png) |

锁定看板（内容只变暗不可编辑，不会白屏）：`artifacts/board-locked.png`

| 待办右键菜单 | 子菜单（含勾选） | 外观菜单 | 深色皮肤下的菜单 |
|---|---|---|---|
| ![菜单](artifacts/menu-todo.png) | ![子菜单](artifacts/menu-submenu.png) | ![外观](artifacts/menu-appearance.png) | ![深色菜单](artifacts/menu-dark.png) |

便签 / 便签看板 / 日历看板 / 到点提醒：

| 待办便签 | 便签看板（边写边存） | 日历看板（农历 + 休班 + 黄历） | 到点提醒 |
|---|---|---|---|
| ![便签](artifacts/board-work.png) | ![便签看板](artifacts/board-memo.png) | ![日历](artifacts/calendar.png) | ![提醒](artifacts/reminder.png) |

Token 用量看板与番茄时钟：

| Token 用量（趋势 + 明细） | 番茄时钟 |
|---|---|
| ![Token](artifacts/token-board.png) | ![番茄](artifacts/pomodoro.png) |

日历的黄历面板（点某一天就显示宜忌）与农历标记：

![黄历](artifacts/calendar-lunar.png)

带目标日期、提醒、重复规则的待办列表：`artifacts/board-work.png`

TODO / Done 视图切换：

| 待办视图（未完成） | Done 视图（含完成日期） | 行内编辑（点标题直接改） |
|---|---|---|
| ![待办视图](artifacts/board-todo-view.png) | ![Done视图](artifacts/board-done-view.png) | ![行内编辑](artifacts/board-inline-edit.png) |

四种语言的界面（看板名为用户数据，保持原样）：

| English | 日本語 | 한국어 |
|---|---|---|
| ![EN](artifacts/i18n-en.png) | ![JA](artifacts/i18n-ja.png) | ![KO](artifacts/i18n-ko.png) |

设置页也可切换语言（下图为日文界面）：

![日文设置页](artifacts/i18n-settings-ja.png)

---

## 0. 下载即用（便携版）

不想装 .NET 运行时？直接下便携版：

**[⬇ 下载最新版 StickyTodo-x.x.x-win-x64-portable.zip](https://github.com/xiefengdaxia/StickyTodo-WPF/releases/latest)**

解压后双击 `StickyTodo.exe` 即可，**单文件 70 MB，内含 .NET 8 运行时，零依赖**。

- 压缩包里带了 `portable.marker`，所以数据（`sticky.db` + 日志）存在 exe 旁边的 `data\` 里 —— **拷走整个文件夹就能带走全部数据**，适合放 U 盘
- 删掉 `portable.marker` 就切回常规模式（数据存到 `%LOCALAPPDATA%\StickyTodo`）
- 首次运行可能被 SmartScreen 拦（未签名），点「更多信息」→「仍要运行」
- 系统要求：Windows 10 1809 (x64) 或更高

自己打包：

```powershell
.\tools\publish-portable.ps1 -Version 1.0.0                    # 只打包
.\tools\publish-portable.ps1 -Version 1.0.0 -CreateRelease     # 打包并发布到 GitHub Release
```

脚本会依次做：跑单测 → 单文件发布 → 放便携标记 → 打 ZIP → **冒烟测试**（用 `--portable-check` 起一次真实进程，确认数据确实落在 exe 旁边）→ 可选上传 Release。

**运行环境要求**

| 项目 | 要求 |
|---|---|
| 操作系统 | **Windows 10 1809（x64）及以上**，Windows 11 全部版本 |
| CPU 架构 | **x64**（ARM64 设备可通过 x64 仿真运行，但未实测） |
| .NET 运行时 | **不需要安装**（.NET 8 自包含） |
| 其它依赖 | **无**。WPF 图形栈、SQLite 原生库、VC++ 运行库都已内置 |
| 磁盘 | 约 71 MB（exe 本身）；数据几 MB 起 |
| 权限 | 普通用户即可，**不需要管理员权限**（开机自启只写 HKCU） |

不支持的：Windows 7/8/8.1（.NET 8 与 WPF 的新 DPI API 都不支持）、Linux/macOS。32 位系统请使用下方 Avalonia 版。

**关于临时目录**：单文件发布会把原生库（wpfgfx_cor3.dll、e_sqlite3.dll 等约 10 MB）在首次运行时解压到
%TEMP%\.net\StickyTodo\。这意味着 %TEMP% 必须可写；如果企业的临时目录策略很严，程序会启动失败。
需要完全免解压的话，改用「单目录自包含发布」（去掉 PublishSingleFile）即可，代价是变成一个几十个文件的文件夹。

---

## 0.1 Windows 7 SP1 / 32 位：Avalonia 版

WPF 版受 .NET 8 限制无法支持 Win7 和 32 位系统。为此仓库里另有一个 **Avalonia 11.3.22 + .NET 6** 的完整移植版，可发布到 **Windows 7 SP1（x64 / x86）**：

```powershell
.\tools\publish-avalonia-win7.ps1 -Version 1.5.1
```

产物（self-contained 单文件，目标机无需装 .NET）：

```
publish-avalonia/
  StickyTodo-Avalonia-1.5.1-win-x64-portable.zip   # ~38 MB
  StickyTodo-Avalonia-1.5.1-win-x86-portable.zip   # ~35 MB
```

功能与 WPF 版对齐：待办看板、便签、日历（农历/节气/黄历）、Token 用量（含**复制摘要**）、番茄钟、提醒、托盘、全局快捷键、皮肤与透明度、四语言热切换、会话恢复、便携模式，以及 **天气看板**（Open-Meteo 免费接口，唯一联网功能；城市可增删、支持**模糊搜索**全国省市区县、可设**默认城市**；区县查不到会自动按所属地级市查，查不到就明确提示暂无数据，绝不拿别的城市顶上）、**世界时钟**（全球十大城市）、**桌面时钟挂件**（阳历/农历+节气节日/时间，下面是**今日天气**和**今日日程**——点天气行直接打开天气看板、点日程行打开日历，没建过看板会自动建，可嵌入桌面）。

> **Win7 上的已知限制**：Win7 只能用 `RedirectionSurface` 合成，**不支持窗口透明**，所以便签会以直角、不透明外观显示（Win8+ 保持圆角与半透明）。

细节、兼容设置、Avalonia 移植踩坑记录与 Win7 前置补丁见 **[docs/avalonia-win7.md](docs/avalonia-win7.md)**。


---
## 1. 快速开始

```powershell
dotnet build -c Debug                       # 编译
dotnet test                                 # 单元测试（245 个）
```

```powershell
$exe = ".\src\StickyTodo.App\bin\Debug\net8.0-windows\StickyTodo.exe"

& $exe                                     # 正常使用
& $exe --lang en                           # 强制英文界面（本次运行有效，不改设置）
& $exe --selftest                          # 无界面自检：建库 + 种子看板，退出码 0
& $exe --demo                              # 种一份示例数据并打开两个便签窗口（库里没数据时才种）
& $exe --demo --shot D:\shots              # 演示 + 把每个窗口渲染成 PNG，然后自动退出
& $exe --demo --reminder-demo --shot D:\shots   # 额外造一条到点提醒，验证弹窗链路
& $exe --recurrence-demo                    # 重复待办端到端验证（建规则→勾选→滚动→日历展开），退出码 0 = 通过
& $exe --view-demo --shot D:\shots          # 分别在待办视图/已完成视图下截图，验证视图切换
& $exe --settings-demo --shot D:\shots --tall   # 截设置页（--tall 临时加高，让全部区块入镜）
& $exe --skin-check                         # 验证换皮肤后便签资源确实同步，退出码 0 = 通过
& $exe --menu-demo --shot D:\shots          # 展开右键菜单/子菜单/外观菜单并截图（截图走弹出层，不在窗口里）
& $exe --menu-demo --shot D:\shots --skin graphite   # 深色皮肤下的菜单配色
& $exe --inline-edit-demo                   # 行内编辑端到端验证（改标题/空标题拦截/草稿行新增），退出码 0 = 通过
& $exe --inline-edit-demo --shot D:\shots   # 同上，并截一张「第一行处于编辑态」的图
& $exe --interaction-demo                   # 拖拽排序 / 拖到标签 / 清空标签 / 点空白退出编辑，退出码 0 = 通过
& $exe --tag-drag-demo                      # 拖到标签全链路（打标签 → 清空标签），退出码 0 = 通过
& $exe --hover-demo --shot D:\shots         # 截「悬停显示 ✓ ✕」与「已完成悬停显示 ↺」
& $exe --chip-count-demo                    # 标签计数回归：加/删标签后胶囊计数是否跟着变
& $exe --session-demo                       # 会话恢复第 1 阶段：开 3 个看板并写入会话
& $exe --session-demo --phase2              # 会话恢复第 2 阶段：模拟重启，验证 3 个窗口与位置都回来
& $exe --autostart-demo                     # 开机自启回归：写注册表 → 读回 → 删除 → 还原
& $exe --token-demo --shot D:\shots         # Token 看板回归（假数据源，不读库）
& $exe --token-demo --live                  # Token 看板回归，读真实的本机 opencode 库
& $exe --calendar-demo --shot D:\shots      # 日历回归：农历/节气/休班/黄历/年月下拉
& $exe --pomodoro-demo --shot D:\shots      # 番茄钟回归：整周期、暂停、跳过、今日计数
& $exe --motion-demo --shot D:\shots        # 动效回归：入场/脉冲/勾选/删除/完成，读属性验证动画真在跑
& $exe --i18n-demo --shot D:\shots          # 多语言回归：四种语言逐一切换并校验界面文案
& $exe --portable-check                      # 便携模式自检：打印数据目录并确认数据库落在正确位置
& $exe --manager-menu-demo --shot D:\shots  # 主窗体卡片右键菜单（验证含「重命名」「删除」）
& $exe --settings-reopen-demo              # 反复打开/关闭设置页 5 轮，验证不会崩（退出码 0 = 通过）
& $exe --window-opacity-demo --shot D:\shots  # 整窗透明度 100/60/40% 并读回 Window.Opacity 断言
& $exe --ui-probe                           # 打印拖动把手与行的屏幕坐标（配合 tools/drag-test.ps1 做真实鼠标拖拽）
& $exe --skin graphite --opacity 0.6 --window-opacity 0.9   # 启动时覆盖外观（也用于截图回归）
```

`--shot` 用 `RenderTargetBitmap` 渲染窗口自身，不依赖屏幕截图，因此**不受 DPI 缩放/窗口遮挡影响**，可以直接放进 CI 做 UI 回归（产物见 `artifacts/`）。

运行时文件位置：

| 内容 | 路径 |
|---|---|
| 数据库 | `%LOCALAPPDATA%\StickyTodo\sticky.db` |
| 日志 | `%LOCALAPPDATA%\StickyTodo\logs\app-YYYYMMDD.log` |
| 便携/独立数据目录 | 设置环境变量 `STICKYTODO_DATA_DIR` 即可（演示与 CI 用） |

删掉数据目录即恢复出厂状态。

---

## 2. 解决方案结构

```
StickyTodo.sln
├─ src/StickyTodo.Core             领域层（仅依赖 Ical.Net，无 UI/DB 依赖）
│   ├─ Entities/                   Board / BoardWindowState / TodoItem / TodoCompletion / Tag / Note
│   ├─ Abstractions/               ITodoRepository / ITokenStatsProvider
│   ├─ Localization/               AppLanguage / UiStrings（四语文案）/ Translator
│   ├─ Entitlements/               IEntitlementService + PermanentFree / FreeTier
│   ├─ Geometry/                   SnapRect / WindowSnapper（吸附算法）
│   ├─ Calendar/                   MonthGrid / CalendarProjection / LunarCalendar / SolarTerms / ChineseHolidays / Almanac / Festivals
│   ├─ Pomodoro/                   PomodoroEngine（纯状态机，可单测）
│   ├─ Recurrence/                 RecurrenceEngine（RFC 5545 RRULE）
│   └─ Services/                   TodoService、AppSettingsService、TodoOrdering、DueDateText
├─ src/StickyTodo.Infrastructure   基础设施（SQLite / 文件系统）
│   ├─ Persistence/SqliteTodoRepository.cs
│   ├─ Token/OpenCodeTokenStatsProvider.cs
│   └─ AppPaths.cs
├─ src/StickyTodo.App              WPF 表现层（net8.0-windows）
│   ├─ App.xaml(.cs)               最小 Host + DI + Serilog + 单实例 + 自检/演示
│   ├─ Views/                      ManagerWindow / SettingsWindow / BoardWindow / MemoBoardWindow / CalendarWindow / TokenBoardWindow / PomodoroWindow / PromptWindow / ReminderPopup
│   ├─ ViewModels/                 CommunityToolkit.Mvvm（四类看板 + 设置页 + 番茄钟）
│   ├─ Controls/                   TrendChart（自绘趋势图）
│   ├─ Services/                   WindowManager / TrayService / SkinService / LocalizationService / Motion / ScreenHelper
│   └─ Converters/                 含多语言转换器（Loc / LocFormat / LocYear / LocMonth）
└─ tests/StickyTodo.Tests          xUnit（245 个）
```

图标生成器在 `tools/IconGen`（WPF 矢量绘制 → 多尺寸 ICO）：

```powershell
dotnet run --project tools/IconGen -c Release -- src/StickyTodo.App/Assets/stickytodo.ico
```

依赖方向：`App → Infrastructure → Core`。Core 不知道数据库、窗口和 DI 的存在。

---

## 3. 当前功能

**多语言（4 种）**
- 界面支持 **简体中文 / English / 日本語 / 한국어**，在设置页「语言」下拉里切换，**立即生效不用重启**
- 切换语言时会遍历所有已打开窗口，把带转换器的绑定重新求值（`LocalizationService.RefreshAllWindows`），因此切完当场就能看到全部文案变化
- 语言选择落库（`appearance.language`），下次启动自动恢复；启动时在创建任何窗口**之前**应用，避免先闪一下中文
- `--lang en|ja|ko|zh-CN` 可临时强制某语言（截图回归用，不写回设置）
- **农历术语保留中文**：农历日期、干支、建除十二神、节气名、宜/忌条目本身不翻译（这些是中文历法专有概念，硬译反而看不懂），只有「农历」「节气」「宜」「忌」这些**标签**跟随界面语言
- 数字单位按语言切换：中文用「万/亿」，其它语言用 `M/B`
- `` `--` `` 文案一致性由单测守住：`LocalizationTests` 校验 4 种语言齐全、占位符 `{0}/{1}` 跨语言一致、每条译文都能被 `string.Format` 正确处理

**外观（贴纸感）**
- **所有窗口都没有系统标题栏**：主窗体、设置页、便签窗口、输入框全部自绘标题栏（可拖动，主窗与设置页支持边缘缩放与最大化）
- 便签窗口**无边框 + 圆角 + 投影**，边缘 8px 可缩放（自己处理 `WM_NCHITTEST`）
- **10 套皮肤**：经典黄 / 素白 / 薄荷绿 / 天青蓝 / 淡紫 / 樱花粉 / 暖橘杏 / 石墨夜色 / 午夜墨蓝 / 深棕咖（3 套暗色，自动切浅色文字）
- **两种透明度分别可调，10% 一档共 10 档**：背景透明度 10–100%（只让纸变透、文字始终清晰），整窗透明度 10–100%（连文字一起淡）
- **只有便签/便签看板/日历看板透明**；主窗体与设置页是不透明窗口
- 自绘控件：圆角复选框、细滑块、扁平滚动条、分段切换、卡片、**右键菜单**（含子菜单箭头、勾选标记、分隔线，颜色跟随皮肤）
- 标题栏悬浮按钮：📌 置顶、🔒 锁定、🎨 快速换肤、⋯ 主窗体、✕ 关闭
- 便签不在任务栏占位（贴纸风格），入口走托盘和主窗体

**设置页（独立窗口）**
- **语言下拉**：四种语言原生名称，选定立即生效
- 10 套配色以**色卡网格**呈现（两行各 5 个），点一下所有窗口立即生效（不需要重启）
- 两个透明度滑块，实时预览并即时保存
- 三个全局快捷键一览
- 数据区：会员状态、内容统计（看板/待办/已完成/标签数）、本地存储路径
- 入口：主窗体左下「设置」、托盘右键菜单「设置」

**看板**
- 一个看板一个独立窗口，可同时开多个；再从托盘/管理面板打开同一个看板会激活已有窗口
- **四种看板类型**：待办看板（清单）、便签看板（一整张纸，边写边自动保存）、日历看板（月视图）、Token 用量看板
- 窗口位置与大小按看板分别记忆（重启后回到原位）；换显示器后自动夹回可见区域
- **贴边吸附**：拖到屏幕边缘、或靠近其它便签窗口时自动对齐（纸面边缘对齐，排除投影留白）
- 管理面板：新建待办/便签/日历看板、重命名、删除、双击打开，显示类型与待办数
- **卡片右键菜单**：打开 / 重命名… / 删除（自动选中被右键的那张卡片）
- **整个窗体都能拖动**（除按钮、列表项、输入框、滚动条外），双击任意非交互区最大化/还原
- 托盘图标：可勾选式开关任意看板窗口、显示/隐藏全部、打开管理面板、退出；点 X 只收进托盘

**日历看板**
- 月视图固定 6 周网格（周一开头），今天高亮、周末标红、跨月日期淡化；星期表头随语言变化
- **每格显示农历**（初一显示「X月」，其余显示「初二 / 廿三」）
- **节气 / 传统节日 / 法定节假日**都会写在格子里：节气（立春、清明、霜降…）、除夕、春节、元宵、端午、七夕、中秋、重阳、腊八，以及元旦、劳动节、国庆等
- **「休」「班」角标**：红色「休」= 放假，灰色「班」= 调休补班（数据来自国务院 2024–2026 年放假通知，手工录入，不联网）
- 有日程的格子显示色点 + 标题摘要；下方是选中日期的日程列表
- **点击某天显示黄历**：农历日期、干支年/生肖、日干支、建除十二神，以及**宜 / 忌**各 5 项
- **年月下拉框**：可以直接跳任意年（前后 60/30 年）任意月，也能用 ‹ › / 今天 导航；切月后选中日期自动跟进到该月
- 点日程可勾选完成，勾选后自动刷新
- **拖日程到别的日期格 = 改期**（保留原时刻，只换日期）
- 数据来自**所有看板**的待办，重复待办会按规则展开成多次显示

**Token 用量看板**
- **数据来自本机 opencode 数据库**（默认 `~/.local/share/opencode/opencode.db`，可在设置里改路径），**完全不联网**，只读打开、不写别人的库
- 只读 `session_message`（v2）一张表；旧版 `message`（v1）内容已全部迁入 v2、官方不再写它，代码里完全忽略
- **切换范围不卡**：整段 JSON 每行只解析一次（多路径 `json_extract`）、时间条件下推进 WHERE、一条 SQL 同时算汇总/趋势/模型；历史日期按天缓存在内存里，只有"今天"每次重算
- **自动刷新间隔 5 / 15 / 30 秒**可选，也可以随时点 ⟳ 手动刷新
- **统计范围**：今天（按小时）/ 近 3 天 / 近 7 天 / 近 30 天（7 天以上自动按天聚合）
- 顶部汇总：总 token、调用次数、缓存比例，下面一行拆开显示输入 / 输出 / 推理 / 缓存读
- **趋势图**：堆叠柱（输入/输出/推理/缓存读/缓存写）+ 折线（调用次数），**点击任意柱子/折线点显示该时段明细**，自动刷新不会冲掉已点开的明细
- **按模型**：每个模型的 token 总数、占比、调用次数和分项
- 取数失败只在页脚提示，不会弹窗打断；断网时保持上一次的数据

**番茄时钟**
- 托盘菜单「番茄时钟」打开，独立悬浮小窗，默认置顶
- 25 分钟专注 + 5 分钟短休息，**每 4 个番茄换 15 分钟长休息**（周期数在 `PomodoroEngine` 里可配）
- 圆环进度 + 大字倒计时；**专注 / 休息两种配色**，圆环 30fps 补间
- 开始 / 暂停 / 继续 / 重置 / 跳过；专注结束弹托盘通知，休息结束自动进入下一轮专注
- **今日完成个数落库**，跨天自动归零

**待办（表格行内编辑）**
- **没有「添加」按钮、没有勾选框**：列表末尾常驻一行草稿行「＋ 在这里写一条，回车即添加」
- **鼠标移入某行才显示操作按钮**（固定在**行尾最右侧**，不占用标题空间）：待办行是 `✓`（标记完成）和 `✕`（删除）；已完成行是 `↺`（撤回为待办）和 `✕`
- **第二行的徽标与标签名和标题左边缘对齐**
- **每一行都能直接改**：点标题即可编辑，回车提交并跳到下一行，Tab/点别处也会自动提交，Esc 撤销
  - 空标题会被拦下、恢复原值并在行内提示「内容不能为空」
  - 聚焦时该行高亮 + 输入框琥珀描边，不会误改别的行
- **拖动把手**（每行左侧三点）：上下拖 = 调整顺序；拖到上方标签胶囊 = 打上该标签；拖到「全部 / 未分类」= 清空标签
- **点空白处自动退出编辑**：点窗口里任何非输入区，正在编辑的行会提交并失焦
- **TODO / Done 视图切换**：底部分段按钮（`待办 N` / `已完成 N`）一键切换
  - 待办视图只看未完成，沿用你手动拖拽的顺序
  - Done 视图只看已完成，**按完成时间倒序**（最近完成的在最上面），每行显示完成时间标签（今天 14:41 / 昨天 09:05 / 10月18日 20:11 / 2025年12月30日 08:00）
  - Done 视图下新增输入框自动禁用，避免"加了却看不见"
- **目标日期（倒数日）**：右键设置今天/明天/本周末/自定义，行上显示「还剩 N 天 / 今天到期 / 已过期 N 天」，过期标红
- **到点提醒**：右键设 30 分钟后 / 1 小时后 / 今天 18:00 / 明天 9:00 / 自定义，到点弹右下角小窗 + 托盘气泡，可「稍后 10 分钟」；20 秒轮询一次，全部本地
- **重复待办（RFC 5545 RRULE）**：右键选「每天 / 每个工作日 / 每周 / 每两周 / 每月 / 每月最后一天 / 每年」，或填自定义 RRULE（如 `FREQ=MONTHLY;BYDAY=2TU`）
  - 勾选完成时不会消失，而是**记一笔完成历史 + 目标日期滚到下一次**（提醒时间跟着顺延）
  - 行上显示 🔁 徽标与中文描述（「每周五」「每月最后一天」）
- **拖拽排序**（用「移到某条之前」而不是下标，因此先按标签过滤再拖动也不会错位）
- 右键菜单：切换完成状态、挂/摘标签、设目标日期、设提醒、设重复、删除

**标签**
- 全局标签，一条待办可挂多个；筛选胶囊 `全部 / 未分类 / 各标签`，带计数
- 新建/重命名/删除标签；删除标签不影响待办本身

**全局快捷键**（可在别处窗口抢焦点时使用）
- `Ctrl+Alt+N` 新建待办（打开默认待办看板并聚焦输入框）
- `Ctrl+Alt+H` 显示 / 隐藏全部便签
- `Ctrl+Alt+L` 锁定 / 解锁当前活跃看板
- 注册失败（被别的程序占用）时静默跳过，不影响启动

**动效（统一的即时反馈）**
- **窗口入场**：位置定好之后淡入 + 轻微放大（动画作用在内容上而不是 Window 本身，避免透明窗口在动画期间被位图化导致文字发虚）
- **按钮**：悬停时高亮层淡入（不是硬切颜色）、按下时轻微缩小 4% 再弹回，`Primary` / `Ghost` / 标题栏圆钮 / 标签胶囊都适用
- **复选框**：勾选时方块先放大到 1.18 再回弹，勾从 40% 放大到 100%（`BackEase`），取消勾选反向收回
- **滑块**：拖动圆点悬停放大 1.2、拖动中 1.35
- **待办行**：鼠标移入时行尾按钮淡入；`✓` 完成时该行脉冲一下；`✕` 删除时该行先淡出再真正删除；回车新增后新行从上方滑入
- **标签胶囊**：选中态加粗变色，悬停有高亮渐变
- **列表卡片**（管理面板）：悬停放大 1.2%
- **日历格**：悬停放大 4% 并高亮；点击某天时下方黄历面板淡入
- **提醒弹窗**：从下方滑入 + 淡入，不容易被忽略
- **Token 看板**：取数时刷新图标持续旋转；首次加载时按钮变暗，切范围时不闪
- **番茄钟**：进度圆环以 30fps 补间，走得像"流动"而不是每 250ms 跳一格
- **尊重系统设置**：Windows「辅助功能 → 视觉效果 → 动画效果」关掉后，所有动效自动跳过（`Motion.Enabled` 读 `SystemParameters.ClientAreaAnimation`）

**其他**
- 单实例（`Local\StickyTodo.SingleInstance`）
- Serilog 滚动日志 + 全局异常兜底
- 数据库自动迁移（`PRAGMA user_version`，已实现 v1→v4）

---

## 4. 数据模型

```sql
board(id, name, sort, is_default, kind, created_at,
      win_left, win_top, win_width, win_height)          -- kind: todo | memo | calendar | token
todo(id, board_id→board.id ON DELETE CASCADE, title, note,
     is_done, done_at, due_at, remind_at, reminded_at, recurrence,
     sort, created_at, updated_at)                        -- recurrence: RFC5545 RRULE
todo_completion(id, todo_id→todo.id, occurrence_at, completed_at)
tag(id, name UNIQUE COLLATE NOCASE, color, sort, created_at)
todo_tag(todo_id→todo.id, tag_id→tag.id, PRIMARY KEY(todo_id, tag_id))
note(id, board_id→board.id UNIQUE, content, created_at, updated_at)
setting(key PRIMARY KEY, value)
```

设计要点：

- **时间统一存 Unix 秒（INTEGER）**，读出时 `.ToLocalTime()` 还原成本地时间（否则显示的提醒时间会差一个时区，这个坑已经踩过并修掉）；`DateTimeOffset` 只在仓储边界转换。
- **逻辑外键 + 级联删除**：每个连接开 `Foreign Keys=True`，删看板自动清待办，删标签自动清关联。
- **WAL 模式**（`journal_mode=WAL`、`synchronous=NORMAL`），多窗口并发读更稳。
- **迁移用 `PRAGMA user_version` 递增**，`Initialize()` 幂等；`SchemaMigrationTests` 用一个 v1 老库验证自动升级且数据不丢。
- 仓储短连接 + 连接池，不持有长连接，避免多窗口/后台线程竞态。

---

## 5. 完全本地化（可验证，不是口号）

- Token 用量看板读的是**本机 opencode 的 SQLite 数据库**，是本地文件读取，同样不联网、不发送任何数据。
- 没有账号体系、没有云同步、没有埋点、没有版本检查。
- 宿主刻意用 `new HostBuilder()` 而不是 `Host.CreateDefaultBuilder()`：后者会去读 `appsettings.json` / 环境变量 / 命令行配置，这里配置来源全部关闭，行为只由代码决定。
- 可执行的保证：`tests/StickyTodo.Tests/NoNetworkTests.cs` 断言 **Core 与 Infrastructure 程序集的引用列表里没有** `System.Net.Http` / `System.Net.Sockets` / `System.Net.Requests` / `System.Net.WebClient`。一旦有人往这两层加回网络依赖，测试立刻失败。

---

## 6. 会员策略（重点）

发行版注册的是 `PermanentFreeEntitlement`：

```csharp
// src/StickyTodo.App/App.xaml.cs
services.AddSingleton<IEntitlementService, PermanentFreeEntitlement>();
```

`PermanentFreeEntitlement` = 所有上限 `int.MaxValue`、`IsPro = true`、`Tier = Lifetime`，所以：

- 界面上的会员徽标、权益判断、额度校验**逻辑全部保留且正常工作**，不是把判断删掉；
- 用户体验上等于永久免费、无功能阉割；
- 将来若要收费，只需换成 `FreeTierEntitlement`（已实现 2 看板 / 3 标签 / 5 条重复规则 / 不含 ICS），或新增一个「从本地授权文件读档位」的实现，**业务与界面代码一行不用改**。

额度判断只允许出现在 `TodoService` 这一层，界面只负责把 `EntitlementLimitException` 显示出来。

---

## 7. 已完成 / 路线图

**M1（已完成）**：分层骨架、DI、日志、全局异常、单实例、SQLite 建库与迁移、待办 CRUD、MVVM 主面板、托盘、权益抽象。

**M2（已完成）**
- [x] 多看板 + 一个看板一个窗口 + 窗口位置记忆
- [x] 贴边吸附（屏幕边缘 + 相邻便签），纯函数 + 8 个单测
- [x] 拖拽排序（`TodoOrdering.MoveBefore` + 6 个单测）
- [x] 标签与筛选（含计数、右键挂标签、标签增删改）
- [x] 托盘动态看板菜单、管理面板
- [x] `--demo` / `--shot` 演示与 UI 回归截图

**M2.5 外观（已完成）**
- [x] 便签窗口无边框 + 圆角 + 投影 + 自绘标题栏 + 边缘缩放
- [x] 背景半透明（10 档，默认 92%），文字始终清晰
- [x] **整窗透明度**（连文字一起，10 档）可与背景透明度**分别调**
- [x] **主窗体与设置页也改成无边框自绘标题栏**，主窗改为「左侧操作栏 + 右侧看板卡片」布局
- [x] **独立设置页**：色卡网格选皮肤 / 双透明度滑块 / 快捷键一览 / 数据统计与存储路径
- [x] 皮肤扩到 **10 套**（新增「暖橘杏」亮色、「深棕咖」暗色，补齐 3 亮彩 + 3 彩色 + 1 中性 + 3 暗色），画刷走 `DynamicResource`，切换即时全局生效；`--skin-check` 会逐套校验对比度（正文 ≥4.5、强调 ≥2.5）与资源同步
- [x] 自绘控件统一：圆角复选框、细滑块、扁平滚动条、分段切换、卡片
- [x] **右键菜单自绘**：圆角弹层 + 子菜单箭头 + 勾选标记 + 分隔线，颜色跟随皮肤（含深色）
- [x] **透明度 100% 真不透明**：全不透明时改用无 alpha 画刷（否则 WPF 仍按半透明图层合成）
- [x] **主窗体全窗可拖** + 卡片右键（打开/重命名/删除）
- [x] **待办行拖动把手** + 拖到标签胶囊改标签
- [x] **点空白退出编辑态**
- [x] **行内操作按钮替代勾选框**：悬停显示 ✓（完成）/ ✕（删除），已完成行显示 ↺（撤回）
- [x] **标题栏瘦身**：去掉计数行，只留看板名与窗口按钮
- [x] **拖到标签修复**：`DragOver` 返回的 effect 与拖源头不一致导致 Drop 被取消
- [x] **应用图标**：便签纸 + 折角 + 对勾，9 个尺寸（16–256）多尺寸 ICO，窗口与托盘共用
- [x] **悬停按钮移到行尾最右侧**，第二行标签与标题左边缘对齐
- [x] **修复锁定后白屏**：`IsEnabled=false` 时 ListBox 默认模板会画出系统白底，改为透明模板
- [x] **主窗体与设置页改为不透明窗口**，透明度只作用于看板；滑块 10% 一档
- [x] **修复「开关设置窗口后程序崩溃」**：单例窗口被 Close 后再 Show 会抛异常，改为关闭仅隐藏 + 全局异常兜底
- [x] 置顶 / 锁定（按看板记忆，存 `setting` 表）
- [x] 截图回归带假桌面背景，一眼验证透明度

**M3 第一批（已完成）**
- [x] 看板类型：待办看板 / 便签看板（沉浸写作，800ms 防抖自动保存，字数与保存时间）
- [x] 倒数日：目标日期 + 「还剩 N 天 / 已过期 N 天」行内徽标
- [x] 到点提醒：本地轮询 + 右下角提醒小窗 + 托盘气泡 + 稍后 10 分钟
- [x] 全局快捷键：新建待办 / 显示隐藏全部便签 / 锁定当前看板

**M3 第二批（已完成）**
- [x] 日历看板：月视图 6 周网格 + 当日日程列表 + 拖拽改期
- [x] 重复待办：基于 iCalendar RRULE（`Ical.Net`），日/周/月/年/工作日/第 N 个周几
- [x] 重复待办勾选语义：记完成历史 + 目标日期滚动，不消失
- [x] 重复规则中文描述 + 自定义 RRULE 输入 + 校验

**M4 待做**
- [ ] 看板内搜索、多标签同时筛选
- [ ] 导入导出 Excel（NPOI）/ ICS 订阅
- [ ] 日历周视图 / 日视图
- [ ] 重复规则的「跳过这一次」与「编辑单次」

**已完成（补充）**
- [x] TODO / Done 视图切换 + Done 视图显示完成日期 + 按完成时间倒序
- [x] **待办改为表格行内编辑**：去掉「添加」按钮与输入框，末尾常驻草稿行，每行标题可直接改（回车下移 / Esc 撤销 / 空标题拦截）
- [x] **行高压缩 + 标签固定右侧**：一行放下「把手 / 标题 / 标签 / 行尾按钮」，标签始终贴右不再随标题长短跳动
- [x] **管理面板计数实时刷新**：切回窗口（`Activated`）与看板变化（`BoardsChanged`）时重算标签计数
- [x] **会话恢复**（记录上次开着的看板及其位置）：`BeginShutdown` 幂等，修掉 WPF 先关窗后 `OnExit` 导致记录被清空的问题
- [x] **开机自启**（注册表 `Run` + `--autostart`）：自启时只恢复上次的看板，不弹管理面板；没有可恢复看板时才回落显示

**M5（已完成）**
- [x] **Token 用量看板（读本机 opencode 库）**：5/15/30 秒自动刷新 + 手动刷新、今天/近3/7/30 天范围、趋势图（堆叠柱 + 折线）、点柱子看时段明细、按模型占比
- [x] **日历看板升级**：农历日期、二十四节气、传统节日、法定节假日「休」/ 调休补班「班」、点击某天显示黄历宜忌（干支 + 建除十二神 + 宜/忌）、年月下拉框任意跳转
- [x] **番茄时钟**：25/5/15 分钟三段、圆环进度、开始/暂停/重置/跳过、托盘通知、今日完成数落库跨天归零
- [x] **全面动效**：窗口入场、按钮按下缩放、复选框弹跳、行操作反馈、刷新旋转、圆环补间，且尊重系统「减少动效」设置

**M6（已完成）**
- [x] **四语言界面**：简体中文 / English / 日本語 / 한국어，设置页即时切换，农历术语保留中文；单测守住译文完整性与占位符一致性
- [x] **README 四语版本**：`README.md`（中）/ `.en.md` / `.ja.md` / `.ko.md`，顶部互相链接

**M4 外延**
- [ ] ICS 订阅与导入导出
- [ ] Excel 导入导出（NPOI）
- [ ] 更多语言（可选）
- [ ] 便携模式打包（单文件 / Velopack）

---

## 8. 已知限制

- 没有标题栏自定义皮肤，用的是系统标题栏（拖拽/贴边依赖它，改动前要考虑手感）。
- 吸附按窗口外框计算，不同 DPI 显示器之间是近似值。
- 标签筛选是单选胶囊，多头筛选（同时选两个标签）还没做。
- 日历只做月视图；重复规则暂不支持「跳过这一次」和「只改单次」（改了就改整条规则）。
- **节假日/调休数据是手工录入的**（覆盖 2024–2026 年），国务院每年 11 月左右发布次年安排，届时需要往 `ChineseHolidays` 里补数据；超出范围的年份只有「周末休息」的默认判断。
- **二十四节气用通用日数公式计算**，个别年份可能与官方历书差一天（公式内的特例表已覆盖已知偏差）。
- **黄历的宜忌是按建除十二神给的通用条目**，不是逐日通书，只作参考；不涉及择日、神煞、时辰吉凶。
- **非中文界面下农历内容仍显示中文**：这是刻意的取舍（见上文「多语言」）。如果希望完全本地化，可以自行在 `UiStrings` 里补译文并把 `Almanac` 的条目也做成资源。
- **Token 看板依赖 opencode 的库表结构**（`session_message.data` 里的 `tokens` / `model` 字段）。opencode 改版导致字段变动时读数会变 0，需要跟着调整 SQL；库不存在时页脚会提示去设置里指定路径。
- Token 看板的**首次**打开某个大范围（近 30 天要读 100MB+）仍需 1 秒左右，之后同一范围是毫秒级；历史日期的缓存只存在内存里，重启程序后第一次进入需要重算。
- Token 统计的是**消息级用量累加**，与 opencode 自己按会话累加的 `session_v2.tokens_*` 在口径上可能有细微差别（消息级更细，也更能按时间分桶）。
- 重复待办的完成历史目前只记录、未展示（将来用在「本月完成了多少次」这类统计上）。
- UI 层没有自动化测试，靠 `--selftest` / `--recurrence-demo` / `--shot` 等 12 个回归模式做保障。

---

## 许可证

[MIT](LICENSE) © 2026 xiefengdaxia

代码、UI、文案、配色全部为本项目原创实现，没有复制任何第三方程序的反编译产物、资源文件或私有接口。
渲染用的系统字体（如「Microsoft YaHei UI」/「Yu Gothic UI」/「Malgun Gothic」）需遵守其各自的授权，不属于本项目分发内容。
