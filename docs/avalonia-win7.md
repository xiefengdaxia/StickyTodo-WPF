# Avalonia 11.x + .NET 6 / Windows 7 兼容版

这是 StickyTodo 的 **Avalonia 完整移植版**：功能与 WPF 版对齐，同时产物能运行在 **Windows 7 SP1（x64 / x86）** 上。

## 1. 技术组合

| 项 | 选择 | 原因 |
|---|---|---|
| UI 框架 | Avalonia **11.3.22** | 11.x 仍发布 `net6.0` 资产；12.x 换到 SkiaSharp 3.x 后 Win7 彻底不可用 |
| 运行时 | **.NET 6.0**（自包含） | .NET 6 是最后一个支持 Windows 7 SP1 的 .NET 运行时 |
| 目标系统 | Windows 7 SP1 / 8 / 8.1 / 10 / 11 | 同一份 exe 在更高版本 Windows 上也能运行 |
| 架构 | `win-x64` + `win-x86` | 老机器仍有 32 位系统 |
| 发布方式 | self-contained + single-file | 目标机器无需安装 .NET；双击即用 |

### 官方支持边界

Avalonia 官方**不支持** Windows 7，Win7 属于 best-effort：

- 11.1 起修复了 Win7 启动时因 WinRT API 缺失导致的崩溃（AvaloniaUI/Avalonia#14309）。
- 仍可能存在 Win7 特有的边角问题，建议在真实 Win7 机器上做回归。
- 只建议用于**内网、离线、可控环境**。

## 2. 工程结构

```
src/StickyTodo.Core/            # 业务/实体/日历，多目标 net8.0;net6.0
src/StickyTodo.Infrastructure/  # SQLite/Dapper/Token，多目标 net8.0;net6.0
src/StickyTodo.Avalonia/        # Avalonia UI（net6.0，命名空间 StickyTodo.Notes）
tools/publish-avalonia-win7.ps1 # Win7 便携包发布脚本
tools/win7-portable-readme.txt  # 便携包内 README 模板
```

WPF 版（`src/StickyTodo.App`）保持 `net8.0-windows` 不变，两者共用 Core / Infrastructure。

> 命名空间用 `StickyTodo.Notes` 而不是 `StickyTodo.Avalonia`：后者会和根命名空间
> `Avalonia` 冲突，导致 `Avalonia.Controls` 之类被解析成 `StickyTodo.Avalonia.Controls`。

## 3. 功能对照

| 功能 | WPF 版 | Avalonia 版 |
|---|---|---|
| 待办看板（行内编辑、回车新增、Todo/Done 视图） | ✅ | ✅ |
| 标签（筛选胶囊、拖到胶囊打标、右键改名/删除） | ✅ | ✅ |
| 拖拽排序 | ✅ | ✅ |
| 右键菜单（日期/提醒/重复/标签/删除） | ✅ | ✅ |
| 便签看板（自动保存） | ✅ | ✅ |
| 日历看板（农历/节气/节假日/黄历、拖拽改日期） | ✅ | ✅ |
| Token 用量看板（范围/间隔/趋势图/按模型） | ✅ | ✅ |
| 番茄钟（专注/休息/今日计数） | ✅ | ✅ |
| 提醒（到点弹窗、稍后 10 分钟） | ✅ | ✅ |
| 全局快捷键 Ctrl+Alt+N/H/L | ✅ | ✅ |
| 托盘图标 + 看板开关菜单 | ✅ | ✅ |
| 皮肤 / 背景透明度 / 整窗透明度 | ✅ | ✅ |
| 四语言界面 + 热切换（不重启） | ✅ | ✅ |
| 窗口位置记忆、互相吸附 | ✅ | ✅ |
| 会话恢复、开机自启、单实例 | ✅ | ✅ |
| 窗口尺寸记忆 + 边缘缩放 | ✅ | ✅ |
| 便携模式（`portable.marker`） | ✅ | ✅ |
| 动效（淡入、悬停过渡） | ✅ | 部分（淡入/悬停；Avalonia 无 WPF 触发器动画） |

## 4. 关键兼容设置

### 4.1 渲染与合成

`Program.cs`：

```csharp
.With(new Win32PlatformOptions
{
    RenderingMode = new[] { Win32RenderingMode.AngleEgl, Win32RenderingMode.Software },
    CompositionMode = new[]
    {
        Win32CompositionMode.WinUIComposition,   // Win10 1803+
        Win32CompositionMode.DirectComposition,  // Win8+
        Win32CompositionMode.RedirectionSurface  // Win7 兜底
    }
})
```

顺序交给 Avalonia 自己挑，它会在不支持的平台上自动降级。

### 4.2 **Win7 没有窗口透明**（重要）

实测：`RedirectionSurface` 合成下 `TransparencyLevelHint="Transparent"` **不生效**，
窗口会整片发黑。所以 `Views/Chrome.cs` 与 `StickyWindowBase.ApplyNoteChrome()` 会在窗口
显示后检查 `ActualTransparencyLevel`：

- 支持透明（Win8+）→ 保留圆角、阴影、留白、半透明纸面；
- 不支持（Win7）→ 切成**不透明直角外观**，窗口底色用纸色，并设置
  `TransparencyBackgroundFallback` 兜底。

也就是说 **Win7 上便签是方的、不透的**，这是平台限制，不是 bug。

### 4.3 其它

- `app.manifest` 写入 Win7~Win11 的 `supportedOS`，DPI 使用 Win7 认识的 `dpiAware=true`。
- 全局快捷键用**独立的 message-only 隐藏窗口**收 `WM_HOTKEY`（Avalonia 没有公开的窗口消息钩子），与管理面板生命周期解耦。
- 单实例用 `Local\` 互斥体。

## 5. Avalonia 与 WPF 的行为差异（移植时的坑）

| 坑 | 现象 | 处理 |
|---|---|---|
| 索引器绑定 | `{Binding [key]}` 在源发出 `Item[]`/空名通知后**不刷新** | 改用一条文案一个 `LocalizedString` 对象，见 `Localization/LocExtension.cs` |
| TextBox 模板 | 自绘模板缺 `PART_TextPresenter` 直接编译失败 | 只改属性（背景/圆角/内边距），模板留给 FluentTheme |
| `Track` 部件名 | `DecreaseRepeatButton` → `DecreaseButton` | 同理，Slider 只调属性 |
| 拖拽事件名 | `DragOver`/`Drop` 不是 `Control` 自己的事件 | 必须写 `DragDrop.DragOver` / `DragDrop.Drop` |
| 内容多赋值 | 一个 `Button` 里放两个子元素 → `Content` 多次赋值报错 | 用一个 `Panel` 包住 |
| 窗口消息钩子 | 没有 `HwndSource.AddHook` | 自建 message-only 窗口 |
| 托盘通知 | 没有 `NotifyIcon.ShowBalloonTip` | 用左下角小窗（`ToastWindow`）替代 |
| `nint` | net6.0 没有 `nint.Zero` | 用 `IntPtr.Zero` |
| 窗口尺寸 | `Width` 是逻辑像素，`Position` 是物理像素 | 用 `Screen.Scaling` 换算（`ScreenHelper`） |
| `Control.TranslatePoint` | 不在 `Control` 上，也不在 `Visual` 上 | 用扩展方法 `Avalonia.VisualExtensions.TranslatePoint(v, p, root)` |
| 透明容器 | `Background` 为 null 的容器**收不到指针事件** | 需要拖动/点击的标题栏必须写 `Background="Transparent"` |

## 6. 已修的 UI 缺陷记录

### 6.1 待办行拖拽排序（v1.0.1）

**① 按下即拖放，单击也会触发**

原实现直接在 `PointerPressed` 里调 `DoDragDrop`：按下就进入模态拖放循环，
用户感觉"按一下就粘住"，松手在原位，看起来就是"拖不动"。

**② 系统 OLE 拖放不可靠**（根因）

`DragDrop.DoDragDropAsync` 在 Windows 上最终走 OLE 的 `DoDragDrop` + 模态消息循环，
它要求窗口注册为 OLE 拖放目标。在无边框 + 自定义合成的窗口上这条链路并不稳定，
表现为"拖不动"且不抛异常。

**最终方案：完全不用 OLE，改为应用内纯指针拖拽**

```
PointerPressed  → 只"武装"（记起点 + 捕获指针）
PointerMoved    → 移动超过 4px 判定为拖拽；实时命中测试，高亮落点
PointerReleased → 按落点写库（落点没变就不写）
```

落点用**指针位置逐行命中测试**（不再依赖 `e.Source`，拖到行缝隙上也能正确判定）；
拖到标签胶囊上则变成"打标签"，拖到「全部 / 未分类」上则是"清空标签"。

> 回归入口：`--drag-trial <report.txt>` 自动走一遍"找把手 → 按下 → 越过阈值 → 悬停 → 松手 → 写库"，
> 报告 `RESULT: OK` 表示 UI 事件与数据写入都正常。
> 注意：**OLE 模态循环无法用 `mouse_event` 可靠模拟**，用模拟鼠标测拖拽时坐标差几像素
> 就会点到别的控件上，测出来的是脚本不是功能。

### 6.2 窗口拖动（v1.0.2）

三个窗口拖不动，都是同一类问题：

| 窗口 | 原因 | 修复 |
|---|---|---|
| 管理面板 左侧品牌区 | `StackPanel` 没设 `Background`，收不到 `PointerPressed` | 加 `Background="Transparent"` |
| 管理面板 右侧标题区 | 原来是透明 `Grid` | 换成带背景的 `Border` |
| 管理面板 右侧空白区 | 列表外没有拖动入口 | 列表空白处也可拖动（点卡片仍是选中） |

> **规律**：Avalonia 里 `Background` 为 null 的容器不参与命中测试，`PointerPressed` 不触发。
> 需要拖拽的标题栏，必须给一个非 null 的背景。

### 6.3 番茄钟与提醒弹窗拖动（v1.0.3）

| 窗口 | 原因 | 修复 |
|---|---|---|
| 番茄钟 | 代码里有 `TitleBar_PointerPressed`，但 XAML **从未绑定** 过它（死代码） | 补上 `PointerPressed="TitleBar_PointerPressed"` |
| 提醒弹窗 | **完全没写拖动逻辑**，只能固定在右下角 | 窗口级隧道事件：整卡片都能拖，点在按钮上让位给按钮 |

### 6.4 托盘图标残留（v1.0.4）

**现象**：程序退出后任务栏托盘里图标还在，再启动就出现两个。

**根因**：Avalonia 的 `TrayIcon.Dispose()` 只负责发出 `Shell_NotifyIcon(NIM.DELETE)`，
这条消息是**异步投递给 explorer.exe** 的。原来的 `ExitApplication` 在 Dispose 之后
立刻 `Environment.Exit(0)`，explorer 还没处理完删除请求，进程就没了，图标永久残留。

**修复**：

```csharp
RunOnUiThread(Dispose);              // 1) UI 线程上发出删除请求
Thread.Sleep(300);                   // 2) 给 explorer 处理时间
desktop.Shutdown(0);                 // 3) 走正常关闭流程
Thread.Sleep(300);                   // 4) 再等一拍
Environment.Exit(0);                 // 5) 兜底
```

**验证**（用 `TB_BUTTONCOUNT` 读通知区域按钮数）：

```
启动前基线          overflow count = 38
运行中              （+1）
正常退出后          overflow count = 38   ← 不再残留
强制杀进程（对照）  overflow count = 39   ← 说明旧行为确实会残留
```

### 6.5 窗口尺寸记忆与边缘缩放（v1.0.4）

**现象**：无边框窗口无法用鼠标边缘调整大小。

**根因**：`CanResize="True"` 在 `SystemDecorations="None"` 的窗口上不会自动提供边缘命中，
必须自己判定命中区域并调用 `BeginResizeDrag`。

**修复**（`StickyWindowBase`）：

```csharp
// 隧道阶段拦截，优先级高于窗口内的按钮
AddHandler(PointerMovedEvent, OnEdgePointerMoved, Tunnel, handledEventsToo: true);
AddHandler(PointerPressedEvent, OnEdgePointerPressed, Tunnel, handledEventsToo: true);

// 6px 边缘带内 → 换成对应缩放光标；按下则 BeginResizeDrag
```

尺寸变化会经 400ms 防抖写回 `Board.Window`，重启后按原尺寸恢复（实测缩放后
607x787，重启恢复 607x787）。

## 7. 构建

```powershell
dotnet build src\StickyTodo.Avalonia\StickyTodo.Avalonia.csproj -c Release
```

nuget.org 不通时指定镜像：

```powershell
dotnet build src\StickyTodo.Avalonia\StickyTodo.Avalonia.csproj -c Release `
  --source https://repo.huaweicloud.com/repository/nuget/v3/index.json
```

运行（本机需有 .NET 6 运行时）：

```powershell
dotnet run --project src\StickyTodo.Avalonia\StickyTodo.Avalonia.csproj -- --demo
```

### 命令行参数

| 参数 | 作用 |
|---|---|
| `--demo` | 种示例数据并打开每种看板 + 设置页 + 番茄钟 |
| `--autostart` | 开机自启模式：不弹管理面板，只恢复上次的看板 |
| `--lang en` | 强制本次运行使用某语言（zh-CN / en / ja / ko） |
| `--skin mint --opacity 0.8` | 临时覆盖皮肤与背景透明度 |
| `--window-opacity 0.9` | 临时覆盖整窗透明度 |
| `--token-demo` | Token 看板用假数据源 |
| `--selftest` | 无界面自检，退出码 0/1 |
| `--drag-trial <file>` | 拖拽排序回归：自动验证把手事件、落点解析与数据库写入，结果写进文件 |
| `--exit-trial` | 退出回归：验证托盘图标与快捷键被正确清理 |

## 8. 发布 Win7 便携包

```powershell
.\tools\publish-avalonia-win7.ps1 -Version 1.0.0 `
  -NuGetSource https://repo.huaweicloud.com/repository/nuget/v3/index.json
```

产物：

```
publish-avalonia/
  StickyTodo-Avalonia-1.0.0-win-x64-portable.zip
  StickyTodo-Avalonia-1.0.0-win-x86-portable.zip
```

脚本会对每个 exe 运行 `--selftest` 冒烟测试（运行时、数据目录、SQLite 原生库）。

## 9. Win7 机器前置条件

- Windows 7 **SP1**（7601），x64 或 x86。
- 建议安装：
  - **Microsoft Visual C++ 2015-2022 Redistributable**（对应架构）
  - **KB4474419**：SHA-2 代码签名支持
  - **KB3063858**（或 KB2533623）：`SetDefaultDllDirectories`
  - **KB2999226**：Universal C Runtime（UCRT）
  - **KB2670838**：Platform Update（Direct3D 11 / ANGLE）；缺少时回退软件渲染
- 中文界面依赖系统中文字体（中文版 Win7 自带微软雅黑）。
- 单文件 exe 首次启动会解压原生库到 `%TEMP%`，需保证临时目录可写。

## 10. 已知限制

- Win7 无窗口透明 / 模糊（见 4.2），便签为直角不透明外观。
- Avalonia 官方不支持 Win7，问题不一定能获得上游修复。
- .NET 6 已 EOL（2024-11），不再有安全更新；仅建议内网/离线使用。
- 软件渲染模式下动画和滚动会明显变慢。
- WPF 版的一次性回归脚本（`--shot` 截图、`--ui-probe` 等）未移植；Avalonia 版保留 `--selftest`、`--demo`、`--drag-trial`、`--exit-trial`。

## 11. 自检

```powershell
StickyTodo.Avalonia.exe --selftest
```

在当前目录生成 `avalonia-selftest.txt`，退出码 `0` 表示运行时、数据目录、SQLite 均正常。
