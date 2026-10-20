# StickyTodo

一个从零写的 Windows 桌面便签 / 待办应用：WPF + .NET 8 + SQLite。**全程本地运行**——不联服务器、不做账号、不埋点、不检查更新；**所有用户默认就是永久会员，全部功能无限制**。

> 借鉴了同类工具的产品形态（多看板、一板一窗、重复任务、日历），但**代码、UI、文案、配色全部为原创实现**，没有复制任何第三方程序的反编译产物、资源文件或私有接口。

| 便签（默认 92% 背景） | 背景 55%（看得见桌面） | 石墨夜色皮肤 |
|---|---|---|
| ![便签](artifacts/board-default.png) | ![半透明](artifacts/board-translucent.png) | ![暗色](artifacts/board-dark.png) |

便签/随笔看板、到点提醒、管理面板：

| 便签看板（边写边存） | 到点提醒 | 看板管理 |
|---|---|---|
| ![便签看板](artifacts/board-memo.png) | ![提醒](artifacts/reminder.png) | ![管理](artifacts/manager.png) |

带目标日期与提醒的待办列表：`artifacts/board-work.png`

---

## 1. 快速开始

```powershell
dotnet build -c Debug                       # 编译
dotnet test                                 # 单元测试（34 个）
```

```powershell
$exe = ".\src\StickyTodo.App\bin\Debug\net8.0-windows\StickyTodo.exe"

& $exe                                     # 正常使用
& $exe --selftest                          # 无界面自检：建库 + 种子看板，退出码 0
& $exe --demo                              # 种一份示例数据并打开两个便签窗口（库里没数据时才种）
& $exe --demo --shot D:\shots              # 演示 + 把每个窗口渲染成 PNG，然后自动退出
& $exe --demo --reminder-demo --shot D:\shots   # 额外造一条到点提醒，验证弹窗链路
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
├─ src/StickyTodo.Core             领域层（零第三方依赖，可单测）
│   ├─ Entities/                   Board / BoardWindowState / TodoItem / Tag
│   ├─ Abstractions/               ITodoRepository
│   ├─ Entitlements/               IEntitlementService + PermanentFree / FreeTier
│   ├─ Geometry/                   SnapRect / WindowSnapper（吸附算法）
│   └─ Services/                   TodoService（用例 + 权益闸门）、TodoOrdering（排序）
├─ src/StickyTodo.Infrastructure   基础设施（SQLite / 文件系统）
│   ├─ Persistence/SqliteTodoRepository.cs
│   └─ AppPaths.cs
├─ src/StickyTodo.App              WPF 表现层（net8.0-windows）
│   ├─ App.xaml(.cs)               最小 Host + DI + Serilog + 单实例 + 自检/演示
│   ├─ Views/                      ManagerWindow / BoardWindow / PromptWindow
│   ├─ ViewModels/                 CommunityToolkit.Mvvm
│   ├─ Services/                   WindowManager / TrayService / ScreenHelper
│   └─ Converters/
└─ tests/StickyTodo.Tests          xUnit
```

依赖方向：`App → Infrastructure → Core`。Core 不知道数据库、窗口和 DI 的存在。

---

## 3. 当前功能

**外观（贴纸感）**
- 便签窗口**无边框 + 圆角 + 投影**，标题栏可拖动，边缘 8px 可缩放（自己处理 `WM_NCHITTEST`，因为 `WindowStyle=None` 后系统不再提供）
- **背景半透明**：只让背景层透明，文字始终不透明清晰；`35% ~ 100%` 十档可调，默认 92%
- **6 套皮肤**：经典黄 / 薄荷绿 / 天青蓝 / 淡紫 / 樱花粉 / 石墨夜色（暗色皮肤自动切换浅色文字）
- 标题栏悬浮按钮：📌 置顶（默认开）、🔒 锁定（锁后内容区禁用，防误触）、🎨 皮肤与透明度、⋯ 看板管理、✕ 关闭
- 便签不在任务栏占位（贴纸风格），入口走托盘和管理面板

**看板**
- 一个看板一个独立窗口，可同时开多个；再从托盘/管理面板打开同一个看板会激活已有窗口
- **两种看板类型**：待办看板（清单）与便签看板（一整张纸，边写边自动保存，显示字数与保存时间）
- 窗口位置与大小按看板分别记忆（重启后回到原位）；换显示器后自动夹回可见区域
- **贴边吸附**：拖到屏幕边缘、或靠近其它便签窗口时自动对齐（纸面边缘对齐，排除投影留白）
- 管理面板：新建待办/便签看板、重命名、删除、双击打开，显示类型与待办数
- 托盘图标：可勾选式开关任意看板窗口、显示/隐藏全部、打开管理面板、退出；点 X 只收进托盘

**待办**
- 回车快速添加、勾选完成（划线灰显）、悬停删除、显示/隐藏已完成
- **目标日期（倒数日）**：右键设置今天/明天/本周末/自定义，行上显示「还剩 N 天 / 今天到期 / 已过期 N 天」，过期标红
- **到点提醒**：右键设 30 分钟后 / 1 小时后 / 今天 18:00 / 明天 9:00 / 自定义，到点弹右下角小窗 + 托盘气泡，可「稍后 10 分钟」；20 秒轮询一次，全部本地
- **拖拽排序**（用「移到某条之前」而不是下标，因此先按标签过滤再拖动也不会错位）
- 右键菜单：切换完成状态、挂/摘标签、设目标日期、设提醒、删除

**标签**
- 全局标签，一条待办可挂多个；筛选胶囊 `全部 / 未分类 / 各标签`，带计数
- 新建/重命名/删除标签；删除标签不影响待办本身

**全局快捷键**（可在别处窗口抢焦点时使用）
- `Ctrl+Alt+N` 新建待办（打开默认待办看板并聚焦输入框）
- `Ctrl+Alt+H` 显示 / 隐藏全部便签
- `Ctrl+Alt+L` 锁定 / 解锁当前活跃看板
- 注册失败（被别的程序占用）时静默跳过，不影响启动

**其他**
- 单实例（`Local\StickyTodo.SingleInstance`）
- Serilog 滚动日志 + 全局异常兜底
- 数据库自动迁移（`PRAGMA user_version`，已实现 v1→v4）

---

## 4. 数据模型

```sql
board(id, name, sort, is_default, kind, created_at,
      win_left, win_top, win_width, win_height)          -- kind: todo | memo
todo(id, board_id→board.id ON DELETE CASCADE, title, note,
     is_done, done_at, due_at, remind_at, reminded_at, sort, created_at, updated_at)
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

- 没有任何 HTTP 客户端、没有账号体系、没有云同步、没有埋点、没有版本检查。
- 宿主刻意用 `new HostBuilder()` 而不是 `Host.CreateDefaultBuilder()`：后者会去读 `appsettings.json` / 环境变量 / 命令行配置，这里配置来源全部关闭，行为只由代码决定。
- 可执行的保证：`tests/StickyTodo.Tests/NoNetworkTests.cs` 断言 **Core 与 Infrastructure 程序集的引用列表里没有** `System.Net.Http` / `System.Net.Sockets` / `System.Net.Requests` / `System.Net.WebClient`。一旦有人加回网络依赖，测试立刻失败。

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
- [x] 无边框 + 圆角 + 投影 + 自绘标题栏 + 边缘缩放
- [x] 背景半透明（10 档，默认 92%），文字始终清晰
- [x] **整窗透明度**（连文字一起，6 档）可与背景透明度**分别调**
- [x] 6 套皮肤（含暗色），皮肤画刷走 `DynamicResource`，切换即时全局生效
- [x] 置顶 / 锁定（按看板记忆，存 `setting` 表）
- [x] 截图回归带假桌面背景，一眼验证透明度

**M3 第一批（已完成）**
- [x] 看板类型：待办看板 / 便签看板（沉浸写作，800ms 防抖自动保存，字数与保存时间）
- [x] 倒数日：目标日期 + 「还剩 N 天 / 已过期 N 天」行内徽标
- [x] 到点提醒：本地轮询 + 右下角提醒小窗 + 托盘气泡 + 稍后 10 分钟
- [x] 全局快捷键：新建待办 / 显示隐藏全部便签 / 锁定当前看板

**M3 第二批（待做）**
- [ ] 日历看板（月视图 + 当日日程列表 + 拖待办到日期）
- [ ] 重复待办：基于 iCalendar RRULE（`Ical.Net`），日/周/月/年/工作日/第 N 个周几
- [ ] 看板内搜索、多标签同时筛选
- [ ] 导入导出 Excel（NPOI）/ ICS 订阅

**M4 外延**
- [ ] ICS 订阅与导入导出
- [ ] Excel 导入导出（NPOI）
- [ ] 多语言资源、主题切换
- [ ] 便携模式打包（单文件 / Velopack）

---

## 8. 已知限制

- 没有标题栏自定义皮肤，用的是系统标题栏（拖拽/贴边依赖它，改动前要考虑手感）。
- 吸附按窗口外框计算，不同 DPI 显示器之间是近似值。
- 标签筛选是单选胶囊，多头筛选（同时选两个标签）还没做。
- `due_at` 已入库但尚无 UI 与提醒逻辑（M3）。
- UI 层没有自动化测试，靠 `--selftest` + `--shot` 做回归。