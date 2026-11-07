using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using StickyTodo.App.Services;
using StickyTodo.App.Views;
using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Entitlements;
using StickyTodo.Core.Services;
using StickyTodo.Infrastructure;
using StickyTodo.Infrastructure.Persistence;

namespace StickyTodo.App;

/// <summary>
/// 冒烟/自检入口（<c>--selftest</c> / <c>--portable-check</c> / <c>--mem-report</c>），
/// 以及生产代码要用的几个小成员（ShowSettings、ReadOptionValue、ToolDemoArgument）。
/// 与 App.Demos.cs 拆开：后者只在 Debug 构建编译（见 csproj 的 Compile Remove），
/// 让 Release 包不带几千行演示/截图逻辑，但 CI 的冒烟测试照常可用。
/// </summary>
public partial class App : System.Windows.Application
{
    /// <summary>
    /// 命令行的 --xxx-demo / --xxx-check 分发。返回 true 表示「已经接管了这次启动」，
    /// OnStartup 直接 return，不再进正常流程。
    /// </summary>
    private bool TryRunDemo(string[] args)
    {
        // 诊断用：个别演示需要对比硬件/软件渲染，--soft-render 保持兼容（现在已是默认）
        if (args.Contains("--soft-render", StringComparer.OrdinalIgnoreCase))
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        }
        else if (args.Contains("--hardware-render", StringComparer.OrdinalIgnoreCase))
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;
        }

#if DEBUG
        // —— 各类 --xxx-demo 截图/回归入口只随 Debug 构建编译：
        //    App.Demos.cs 在 Release 被 csproj 排除，未知参数会落到最后的正常启动。
        // 视图回归：分别在待办视图 / 已完成视图下截图
        if (args.Contains("--view-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunViewDemo(ReadOptionValue(args, "--shot"));
            return true;
        }

        // 设置页回归：打开设置窗口截图，验证皮肤色卡与滑块渲染
        if (args.Contains("--settings-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunSettingsDemo(ReadOptionValue(args, "--shot"), args.Contains("--tall", StringComparer.OrdinalIgnoreCase));
            return true;
        }

        // 皮肤联动检查：设置页换皮肤后，便签窗口的资源确实跟着变
        if (args.Contains("--skin-check", StringComparer.OrdinalIgnoreCase))
        {
            RunSkinCheck();
            return true;
        }

        // 右键菜单回归：展开菜单并截图，验证自绘样式
        if (args.Contains("--menu-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunMenuDemo(ReadOptionValue(args, "--shot"));
            return true;
        }

        // 行内编辑回归：验证改标题、草稿行新增、空标题拦截
        if (args.Contains("--inline-edit-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunInlineEditDemo(ReadOptionValue(args, "--shot"));
            return true;
        }

        // 交互回归：拖拽排序 / 拖到标签 / 清空标签
        if (args.Contains("--interaction-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunInteractionDemo();
            return true;
        }

        // UI 探针：打印拖动把手与行的屏幕坐标
        if (args.Contains("--ui-probe", StringComparer.OrdinalIgnoreCase))
        {
            RunUiProbe(null);
            return true;
        }

        // UI 探针（保持窗口开着，供外部真实鼠标拖拽）
        if (args.Contains("--ui-probe-hot", StringComparer.OrdinalIgnoreCase))
        {
            RunUiProbe("hot");
            return true;
        }

        // 主窗体右键菜单回归：展开卡片菜单并截图
        if (args.Contains("--manager-menu-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunManagerMenuDemo(ReadOptionValue(args, "--shot"));
            return true;
        }

        // 标签计数回归：加/删标签后检查胶囊计数是否跟着变
        if (args.Contains("--chip-count-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunChipCountDemo();
            return true;
        }

        // 番茄时钟回归：走完整番茄周期，验证计时/暂停/跳过/休息
        if (args.Contains("--pomodoro-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunPomodoroDemo(ReadOptionValue(args, "--shot"));
            return true;
        }

        // 多语言回归：四种语言逐一切换并校验界面文案
        if (args.Contains("--i18n-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunI18nDemo(ReadOptionValue(args, "--shot"));
            return true;
        }

        // 动效回归：确认各处动画真的在跑、且不抛异常
        if (args.Contains("--motion-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunMotionDemo(ReadOptionValue(args, "--shot"));
            return true;
        }

        // 日历回归：农历/节气/节假日/补班/黄历/年月下拉切换
        if (args.Contains("--calendar-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunCalendarDemo(ReadOptionValue(args, "--shot"));
            return true;
        }

        // Token 便签回归：假数据源 + 范围/间隔/手动刷新/点击看明细
        if (args.Contains("--token-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunTokenDemo(ReadOptionValue(args, "--shot"), args.Contains("--live", StringComparer.OrdinalIgnoreCase));
            return true;
        }

        // 桌面时钟联动回归：今日日程摘要 + 点日程/天气自动建看板并打开
        if (args.Contains("--clock-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunClockDemo();
            return true;
        }

        // 会话恢复回归：打开若干看板 → 重启 → 验证窗口数量与位置都回来了
        if (args.Contains("--session-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunSessionDemo(args.Contains("--phase2", StringComparer.OrdinalIgnoreCase), ReadOptionValue(args, "--shot"));
            return true;
        }

        // 开机自启回归：写注册表 → 读回 → 清理
        if (args.Contains("--autostart-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunAutoStartDemo();
            return true;
        }

        // 设置窗口反复开关回归：验证「关掉再打开」不会崩
        if (args.Contains("--settings-reopen-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunSettingsReopenDemo();
            return true;
        }

        // 整窗透明度回归：设成不同值后检查 Window.Opacity 是否真的变了
        if (args.Contains("--window-opacity-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunWindowOpacityDemo(ReadOptionValue(args, "--shot"));
            return true;
        }

        // 锁定白屏回归：锁定后截图 + 检查内容区可见性
        if (args.Contains("--lock-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunLockDemo(ReadOptionValue(args, "--shot"));
            return true;
        }

        // 待办列表增量更新 + 删除可撤销回归
        if (args.Contains("--row-undo-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunRowUndoDemo(ReadOptionValue(args, "--shot"));
            return true;
        }

        // 看板内搜索回归
        if (args.Contains("--search-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunSearchDemo(ReadOptionValue(args, "--shot"));
            return true;
        }

        // 所有看板同时打开：观察摊满屏幕时的内存与 CPU
        if (args.Contains("--all-boards-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunAllBoardsDemo();
            return true;
        }

        // 托盘「把窗口收回本屏幕」回归
        if (args.Contains("--reset-windows-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunResetWindowsDemo(ReadOptionValue(args, "--shot"));
            return true;
        }

        // 截图 / 标注 / 贴图回归
        if (args.Contains("--snip-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunSnipDemo(ReadOptionValue(args, "--shot"));
            return true;
        }

        // 每个窗口自己的透明度回归
        if (args.Contains("--opacity-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunOpacityDemo(ReadOptionValue(args, "--shot"));
            return true;
        }

        // 选中标签后新增待办自动归到该标签
        if (args.Contains("--tag-add-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunTagAddDemo(ReadOptionValue(args, "--shot"));
            return true;
        }

        // 四象限视图回归
        if (args.Contains("--quadrant-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunQuadrantDemo(ReadOptionValue(args, "--shot"));
            return true;
        }

        // 换皮肤确认框截图
        if (args.Contains("--confirm-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunConfirmDemo(ReadOptionValue(args, "--shot"));
            return true;
        }

        // 悬停操作回归：让 ✓/✕/↺ 显示出来并截图
        if (args.Contains("--hover-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunHoverDemo(ReadOptionValue(args, "--shot"));
            return true;
        }

        // 检查更新回归：真实请求 GitHub（--fake 用假版本、--fail 用假失败，便于离线截图）
        if (args.Contains("--update-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunUpdateDemo(
                ReadOptionValue(args, "--shot"),
                args.Contains("--fake", StringComparer.OrdinalIgnoreCase),
                args.Contains("--fail", StringComparer.OrdinalIgnoreCase));
            return true;
        }

        // 拖到标签回归：应用内自动完成「拖到标签」全链路并断言
        if (args.Contains("--tag-drag-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunTagDragDemo();
            return true;
        }

        // 拖到标签回归：打印行与标签胶囊的屏幕坐标，供外部真实鼠标拖拽
        if (args.Contains("--tag-drag-probe", StringComparer.OrdinalIgnoreCase))
        {
            RunTagDragProbe(args.Contains("--hot", StringComparer.OrdinalIgnoreCase));
            return true;
        }

        // 无界面回归：验证重复待办「勾选后滚到下一次」的完整链路
        if (args.Contains("--recurrence-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunRecurrenceDemo();
            return true;
        }
#endif

        // 无界面自检：StickyTodo.exe --selftest（CI / 冒烟测试用）
        if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            RunSelfTest();
            return true;
        }

        // 便携模式回归：验证数据目录确实跟着 exe 走（发布脚本冒烟用）
        if (args.Contains("--portable-check", StringComparer.OrdinalIgnoreCase))
        {
            RunPortableCheck();
            return true;
        }

        // 内存构成报告（只看数字）。会开管理面板 + 两个待办看板；
        // 里面的「工作」看板改成「已有就复用」，避免在真实库里每跑一次多一个
        if (args.Contains("--mem-report", StringComparer.OrdinalIgnoreCase))
        {
            RunMemReport();
            return true;
        }

        return false;
    }

    /// <summary>无界面自检：建库 + 种子看板 + 打印关键信息，成功退出码 0。</summary>
    private void RunSelfTest()
    {
        AppPaths.EnsureCreated();
        ConfigureLogging();

        try
        {
            var repository = new SqliteTodoRepository(AppPaths.DatabasePath);
            repository.Initialize();

            // 回归/演示路径也要跟随设置里的语言
            ApplyLanguageForCurrentRun();

            var service = new TodoService(repository, new PermanentFreeEntitlement());
            var boards = service.GetBoards();
            var board = service.EnsureDefaultBoard();
            var todos = service.GetTodos(board.Id);

            Log.Information("[selftest] db={Db} schema=v2 boards={Boards} default={Default} todos={Todos}",
                AppPaths.DatabasePath, boards.Count, board.Name, todos.Count);

            foreach (var item in service.GetBoards())
            {
                Log.Information("[selftest] board={Name} window={Window}",
                    item.Name, item.Window?.ToString() ?? "未记录");
            }
            Log.Information("[selftest] plan={Plan} tier={Tier} maxBoards={MaxBoards} maxTags={MaxTags} ics={Ics} export={Export}",
                service.Entitlement.DisplayName, service.Entitlement.Tier,
                service.Entitlement.MaxBoards, service.Entitlement.MaxTags,
                service.Entitlement.CanUseIcsCalendar, service.Entitlement.CanExportExcel);

            Log.CloseAndFlush();
            Shutdown(0);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[selftest] 失败");
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    /// <summary>
    /// --portable-check：便携模式自检。把数据目录的解析结果打到日志里，
    /// 供发布脚本断言"标记文件存在时数据确实落在 exe 同目录"。
    /// </summary>
    private void RunPortableCheck()
    {
        AppPaths.EnsureCreated();
        ConfigureLogging();

        try
        {

            Log.Information(
                "[portable] exe 目录={ExeDir} | 便携模式={Portable} | 环境变量覆盖={EnvOverride} | 数据目录={DataDir} | 数据库={Db}",
                AppPaths.ExecutableDirectory,
                AppPaths.IsPortable,
                AppPaths.IsOverriddenByEnvironment,
                AppPaths.DataDirectory,
                AppPaths.DatabasePath);

            // 真正建一次库，确认便携目录可写、SQLite 能落盘
            _host = BuildHost();
            var repository = _host.Services.GetRequiredService<ITodoRepository>();
            repository.Initialize();
            _host.Services.GetRequiredService<TodoService>().EnsureDefaultBoard();

            // 期望：便携模式下数据目录 = exe 目录\data
            var expectedPortable = Path.Combine(AppPaths.ExecutableDirectory, AppPaths.PortableDataFolderName);
            var portableOk = !AppPaths.IsPortable ||
                             string.Equals(
                                 Path.GetFullPath(AppPaths.DataDirectory),
                                 Path.GetFullPath(expectedPortable),
                                 StringComparison.OrdinalIgnoreCase);

            var dbOk = File.Exists(AppPaths.DatabasePath);
            var logDirOk = Directory.Exists(AppPaths.LogDirectory);

            Log.Information("[portable] 数据目录符合便携规则={PortableOk} 数据库已建立={DbOk} 日志目录={LogOk}",
                portableOk, dbOk, logDirOk);

            var ok = portableOk && dbOk && logDirOk;
            Log.Information("[portable] 总结果={Result}", ok ? "通过" : "失败");

            Log.CloseAndFlush();
            Shutdown(ok ? 0 : 2);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[portable] 异常");
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    /// <summary>内存报告小工具：一行一个阶段。</summary>
    private static void LogMemory(string stage)
    {
        using var proc = System.Diagnostics.Process.GetCurrentProcess();
        proc.Refresh();

        Log.Information(
            "[mem] {Stage,-14} 工作集={WS,7:N1} MB  私有={Private,7:N1} MB  托管={Managed,6:N1} MB  线程={Threads,3}",
            stage,
            proc.WorkingSet64 / 1024d / 1024d,
            proc.PrivateMemorySize64 / 1024d / 1024d,
            GC.GetTotalMemory(false) / 1024d / 1024d,
            proc.Threads.Count);
    }

    /// <summary>
    /// --mem-report：只打印内存构成，用来判断「涨在托管堆还是原生资源」。
    /// 输出：工作集 / 私有字节 / 托管堆 / 线程 / 句柄 / 已加载程序集。
    /// </summary>
    private async void RunMemReport()
    {
        AppPaths.EnsureCreated();
        ConfigureLogging();

        try
        {
            _host = BuildHost();

            LogMemory("宿主已建");

            var repository = _host.Services.GetRequiredService<ITodoRepository>();
            repository.Initialize();

            ApplyLanguageForCurrentRun();
            _host.Services.GetRequiredService<TodoService>().EnsureDefaultBoard();

            LogMemory("数据库就绪");

            // 和正常启动一样：打开管理面板
            var manager = _host.Services.GetRequiredService<ManagerWindow>();
            manager.Show();
            MainWindow = manager;

            await Task.Delay(2500);
            LogMemory("管理面板已显示");

            // 再开两个待办看板，看看每个看板窗口要多少（模拟「摊了三个便签」的常见用法）。
            // 「工作」看板已有就复用：这个入口 Release 也带着，别在用户库里反复建看板
            var windows = _host.Services.GetRequiredService<WindowManager>();
            var todoService = _host.Services.GetRequiredService<TodoService>();
            var board = todoService.EnsureDefaultBoard();
            windows.OpenBoard(board);

            var second = todoService.GetBoards().FirstOrDefault(b => b.Name == "工作")
                ?? todoService.CreateBoard("工作");
            windows.OpenBoard(second);

            await Task.Delay(2500);
            LogMemory("三个窗口已打开");

            // 空闲时先让 GC 收一次，看看「静下来」之后的真实占用
            await Task.Run(() =>
            {
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            });

            await Task.Delay(1500);

            LogMemory("静置后");
            MemoryTrimmer.TrimNow();
            await Task.Delay(800);
            LogMemory("修剪工作集后");

            var proc = System.Diagnostics.Process.GetCurrentProcess();
            proc.Refresh();

            var managed = GC.GetTotalMemory(forceFullCollection: true);
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            LogMemory("GC 之后");

            Log.Information(
                "[mem] 工作集={WS:N1} MB 私有={Private:N1} MB 托管堆={Managed:N1} MB " +
                "线程={Threads} 句柄={Handles} 程序集={Assemblies}",
                proc.WorkingSet64 / 1024d / 1024d,
                proc.PrivateMemorySize64 / 1024d / 1024d,
                managed / 1024d / 1024d,
                proc.Threads.Count,
                proc.HandleCount,
                assemblies.Length);

            foreach (var group in assemblies
                         .GroupBy(a => a.GetName().Name?.Split('.')[0] ?? "?")
                         .OrderByDescending(g => g.Count()))
            {
                Log.Information("[mem] 程序集 {Group} x{Count}", group.Key, group.Count());
            }

            Shutdown(0);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[mem] 失败");
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    /// <summary>打开（或置前）设置窗口。托盘菜单和主窗体按钮都走这里。</summary>
    public void ShowSettings()
    {
        if (_host is null)
        {
            return;
        }

        try
        {
            var window = _host.Services.GetRequiredService<SettingsWindow>();

            if (window.IsVisible)
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                window.Activate();
                return;
            }

            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            // 窗口状态异常不能让整个程序挂掉
            Log.Error(ex, "打开设置窗口失败");
        }
    }

    private static string? ReadOptionValue(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string? ToolDemoArgument(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--create-tool-demo", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
