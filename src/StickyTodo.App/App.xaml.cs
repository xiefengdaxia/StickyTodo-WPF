using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using StickyTodo.App.Services;
using StickyTodo.App.ViewModels;
using StickyTodo.App.Views;
using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Entitlements;
using StickyTodo.Core.Recurrence;
using StickyTodo.Core.Services;
using StickyTodo.Infrastructure;
using StickyTodo.Infrastructure.Persistence;

namespace StickyTodo.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\StickyTodo.SingleInstance";

    private Mutex? _mutex;
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 无界面自检：StickyTodo.exe --selftest（CI / 冒烟测试用）
        if (e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            RunSelfTest();
            return;
        }

        // 无界面回归：验证重复待办「勾选后滚到下一次」的完整链路
        if (e.Args.Contains("--recurrence-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunRecurrenceDemo();
            return;
        }

        // 视图回归：分别在待办视图 / 已完成视图下截图
        if (e.Args.Contains("--view-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunViewDemo(ReadOptionValue(e.Args, "--shot"));
            return;
        }

        // 设置页回归：打开设置窗口截图，验证皮肤色卡与滑块渲染
        if (e.Args.Contains("--settings-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunSettingsDemo(ReadOptionValue(e.Args, "--shot"), e.Args.Contains("--tall", StringComparer.OrdinalIgnoreCase));
            return;
        }

        // 皮肤联动检查：设置页换皮肤后，便签窗口的资源确实跟着变
        if (e.Args.Contains("--skin-check", StringComparer.OrdinalIgnoreCase))
        {
            RunSkinCheck();
            return;
        }

        // 右键菜单回归：展开菜单并截图，验证自绘样式
        if (e.Args.Contains("--menu-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunMenuDemo(ReadOptionValue(e.Args, "--shot"));
            return;
        }

        // 行内编辑回归：验证改标题、草稿行新增、空标题拦截
        if (e.Args.Contains("--inline-edit-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunInlineEditDemo(ReadOptionValue(e.Args, "--shot"));
            return;
        }

        // 交互回归：拖拽排序 / 拖到标签 / 清空标签
        if (e.Args.Contains("--interaction-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunInteractionDemo();
            return;
        }

        // UI 探针：打印拖动把手与行的屏幕坐标
        if (e.Args.Contains("--ui-probe", StringComparer.OrdinalIgnoreCase))
        {
            RunUiProbe(null);
            return;
        }

        // UI 探针（保持窗口开着，供外部真实鼠标拖拽）
        if (e.Args.Contains("--ui-probe-hot", StringComparer.OrdinalIgnoreCase))
        {
            RunUiProbe("hot");
            return;
        }

        // 主窗体右键菜单回归：展开卡片菜单并截图
        if (e.Args.Contains("--manager-menu-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunManagerMenuDemo(ReadOptionValue(e.Args, "--shot"));
            return;
        }

        // 会话恢复回归：打开若干看板 → 重启 → 验证窗口数量与位置都回来了
        if (e.Args.Contains("--session-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunSessionDemo(e.Args.Contains("--phase2", StringComparer.OrdinalIgnoreCase), ReadOptionValue(e.Args, "--shot"));
            return;
        }

        // 开机自启回归：写注册表 → 读回 → 清理
        if (e.Args.Contains("--autostart-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunAutoStartDemo();
            return;
        }

        // 设置窗口反复开关回归：验证「关掉再打开」不会崩
        if (e.Args.Contains("--settings-reopen-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunSettingsReopenDemo();
            return;
        }

        // 整窗透明度回归：设成不同值后检查 Window.Opacity 是否真的变了
        if (e.Args.Contains("--window-opacity-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunWindowOpacityDemo(ReadOptionValue(e.Args, "--shot"));
            return;
        }

        // 锁定白屏回归：锁定后截图 + 检查内容区可见性
        if (e.Args.Contains("--lock-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunLockDemo(ReadOptionValue(e.Args, "--shot"));
            return;
        }

        // 悬停操作回归：让 ✓/✕/↺ 显示出来并截图
        if (e.Args.Contains("--hover-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunHoverDemo(ReadOptionValue(e.Args, "--shot"));
            return;
        }

        // 拖到标签回归：应用内自动完成「拖到标签」全链路并断言
        if (e.Args.Contains("--tag-drag-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunTagDragDemo();
            return;
        }

        // 拖到标签回归：打印行与标签胶囊的屏幕坐标，供外部真实鼠标拖拽
        if (e.Args.Contains("--tag-drag-probe", StringComparer.OrdinalIgnoreCase))
        {
            RunTagDragProbe(e.Args.Contains("--hot", StringComparer.OrdinalIgnoreCase));
            return;
        }

        _mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("StickyTodo 已经在运行。", "StickyTodo", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var isDemo = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        var shotDirectory = ReadOptionValue(e.Args, "--shot");

        AppPaths.EnsureCreated();
        ConfigureLogging();
        InstallGlobalExceptionHandlers();

        try
        {
            _host = BuildHost();
            await _host.StartAsync();

            var repository = _host.Services.GetRequiredService<ITodoRepository>();
            repository.Initialize();
            _host.Services.GetRequiredService<TodoService>().EnsureDefaultBoard();

            // 命令行覆盖（演示/截图回归用）：--skin mint --opacity 0.6 --window-opacity 0.9
            var skinService = _host.Services.GetRequiredService<SkinService>();
            var skinOverride = ReadOptionValue(e.Args, "--skin");
            var opacityOverride = ReadOptionValue(e.Args, "--opacity");
            var windowOpacityOverride = ReadOptionValue(e.Args, "--window-opacity");
            skinService.Apply(
                skinOverride,
                double.TryParse(opacityOverride, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedOpacity)
                    ? parsedOpacity
                    : null);

            if (double.TryParse(windowOpacityOverride, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedWindowOpacity))
            {
                skinService.SetWindowOpacity(parsedWindowOpacity);
            }

            var manager = _host.Services.GetRequiredService<ManagerWindow>();
            MainWindow = manager;
            manager.Show();

            var tray = _host.Services.GetRequiredService<TrayService>();
            tray.Start();

            StartReminderPump(tray);
            AttachHotKeys(manager);

            // 开机自启：注册表与用户设置对齐（换了安装路径时自动更新）
            SyncAutoStart();

            // 恢复上次会话：重新打开上次开着的便签/日历看板（位置大小由各窗口自己恢复）
            if (!isDemo)
            {
                var restored = _host.Services.GetRequiredService<WindowManager>().RestoreSession();
                if (restored > 0)
                {
                    Log.Information("已恢复上次会话的 {Count} 个看板窗口", restored);
                }
            }

            if (isDemo)
            {
                RunDemo(shotDirectory, e.Args.Contains("--reminder-demo", StringComparer.OrdinalIgnoreCase));
                _ = manager.RefreshAsync();
            }

            Log.Information("StickyTodo 启动完成，数据目录 {Directory}", AppPaths.DataDirectory);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "启动失败");
            MessageBox.Show($"启动失败：{ex.Message}\n\n日志位置：{AppPaths.LogDirectory}",
                "StickyTodo", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    /// <summary>按用户设置同步开机自启的注册表项。</summary>
    private void SyncAutoStart()
    {
        try
        {
            var settings = _host!.Services.GetRequiredService<AppSettingsService>();
            var autoStart = _host.Services.GetRequiredService<AutoStartService>();
            autoStart.Sync(settings.AutoStart);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "同步开机自启设置失败");
        }
    }

    /// <summary>
    /// 全局异常兜底：UI 线程异常记日志并提示，不让进程直接消失。
    /// 之前没有这一层，"点两次设置"就足以让整个程序崩溃退出。
    /// </summary>
    private void InstallGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "UI 线程未处理异常");

            // 标记已处理，程序继续运行；弹窗也做兜底，避免二次异常
            try
            {
                MessageBox.Show(
                    $"操作失败：{args.Exception.Message}\n\n详细信息已写入日志：{AppPaths.LogDirectory}",
                    "StickyTodo",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (Exception)
            {
                // 弹窗失败就只记日志
            }

            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                Log.Fatal(ex, "非 UI 线程未处理异常");
            }
            else
            {
                Log.Fatal("非 UI 线程未处理异常：{Object}", args.ExceptionObject);
            }

            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "未观察的任务异常");
            args.SetObserved();
        };
    }

    private static void ConfigureLogging()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Debug()
            .WriteTo.File(
                Path.Combine(AppPaths.LogDirectory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            .CreateLogger();
    }

    /// <summary>
    /// 刻意不用 Host.CreateDefaultBuilder：那会去读 appsettings.json / 环境变量 / 命令行配置，
    /// 这里只要一个最小宿主，配置来源全部关闭，保证程序行为完全由本地代码决定。
    /// </summary>
    private IHost BuildHost()
    {
        return new HostBuilder()
            .UseSerilog()
            .ConfigureServices(services =>
            {
                // ★ 发行版策略：所有用户都是永久会员。
                //   想改回“免费版 + 付费升级”，把这一行换成 FreeTierEntitlement 即可。
                services.AddSingleton<IEntitlementService, PermanentFreeEntitlement>();

                services.AddSingleton<ITodoRepository>(_ => new SqliteTodoRepository(AppPaths.DatabasePath));
                services.AddSingleton<TodoService>();
                services.AddSingleton<AppSettingsService>();
                services.AddSingleton<SkinService>();
                services.AddSingleton<WindowManager>();
                services.AddSingleton<TrayService>();
                services.AddSingleton<ReminderPump>();
                services.AddSingleton<HotKeyService>();
                services.AddSingleton<AutoStartService>();
                services.AddSingleton<ManagerViewModel>();
                services.AddSingleton<ManagerWindow>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<SettingsWindow>();
            })
            .Build();
    }

    /// <summary>
    /// 本地提醒：到点弹一个右下角小窗 + 托盘气泡。全部在本机完成。
    /// </summary>
    private void StartReminderPump(TrayService tray)
    {
        var pump = _host!.Services.GetRequiredService<ReminderPump>();
        var todoService = _host.Services.GetRequiredService<TodoService>();

        pump.ReminderDue += (_, item) =>
        {
            try
            {
                tray.Notify("待办提醒", item.Title);

                var boardName = todoService.GetBoards()
                    .FirstOrDefault(b => b.Id == item.BoardId)?.Name ?? "看板";

                new ReminderPopup(item, boardName, todoService).Show();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "弹出提醒失败");
            }
        };

        pump.Start();
    }

    private void AttachHotKeys(Window host)
    {
        var hotKeys = _host!.Services.GetRequiredService<HotKeyService>();
        var windows = _host.Services.GetRequiredService<WindowManager>();

        hotKeys.Attach(
            host,
            onNewTodo: windows.OpenDefaultBoardForNewTodo,
            onToggleBoards: windows.ToggleBoardsVisibility,
            onLockActive: () => windows.ToggleActiveLock());

        Log.Information("全局快捷键已注册：{New} 新建待办 / {Toggle} 显示隐藏 / {Lock} 锁定当前看板",
            HotKeyService.NewTodoHotKey, HotKeyService.ToggleBoardsHotKey, HotKeyService.LockActiveHotKey);
    }

    /// <summary>把完成时间改到过去某天（只用于演示数据，让三种日期写法都能看到）。</summary>
    private static void BackdateCompletion(string todoId, DateTimeOffset doneAt)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={AppPaths.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE todo SET done_at = @doneAt WHERE id = @id;";
        command.Parameters.AddWithValue("@doneAt", doneAt.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("@id", todoId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// --skin-check：验证「设置页换皮肤 → 便签窗口真的变」。
    /// 依次切换两套皮肤并读取便签窗口实际用到的画刷颜色，退出码 0 表示同步生效。
    /// </summary>
    private void RunSkinCheck()
    {
        AppPaths.EnsureCreated();
        ConfigureLogging();

        try
        {
            _host = BuildHost();
            var repository = _host.Services.GetRequiredService<ITodoRepository>();
            repository.Initialize();
            var service = _host.Services.GetRequiredService<TodoService>();
            var board = service.EnsureDefaultBoard();
            if (!service.GetTodos(board.Id).Any())
            {
                service.AddTodo(board.Id, "皮肤切换检查");
            }

            var windows = _host.Services.GetRequiredService<WindowManager>();
            var skinService = _host.Services.GetRequiredService<SkinService>();
            var window = windows.OpenBoard(board);

            var results = new List<string>();
            var ok = true;

            foreach (var key in new[] { "graphite", "sticky-yellow" })
            {
                skinService.SetSkin(key);
                windows.ApplySkin();

                var expected = NoteSkins.Resolve(key);
                var actual = ((SolidColorBrush)System.Windows.Application.Current.Resources["Brush.Ink"]).Color;
                var match = actual == expected.Ink;
                ok &= match;

                results.Add($"皮肤={key} 期望墨色={expected.Ink} 实际={actual} 一致={match}");
            }

            Log.Information("[skin] {Results}", string.Join(" | ", results));
            Log.Information("[skin] 便签窗口类型={Type} 标题={Title}", window.GetType().Name, window.Title);

            Log.CloseAndFlush();
            Shutdown(ok ? 0 : 2);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[skin] 失败");
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    /// <summary>
    /// --session-demo：两阶段验证"重启后恢复上次打开的看板"。
    ///   阶段 1（默认）：打开 3 个看板并把窗口移到固定位置，写入会话；
    ///   阶段 2（--phase2）：模拟重启——重新建 Host，只调 RestoreSession，验证窗口数与位置。
    /// 两阶段共用同一个数据目录，所以等于真的"关掉再开"。
    /// </summary>
    private async void RunSessionDemo(bool phase2, string? shotDirectory)
    {
        AppPaths.EnsureCreated();
        ConfigureLogging();
        InstallGlobalExceptionHandlers();

        try
        {
            _host = BuildHost();
            await _host.StartAsync();

            var repository = _host.Services.GetRequiredService<ITodoRepository>();
            repository.Initialize();
            var service = _host.Services.GetRequiredService<TodoService>();
            service.EnsureDefaultBoard();

            var windows = _host.Services.GetRequiredService<WindowManager>();
            var settings = _host.Services.GetRequiredService<AppSettingsService>();

            if (!phase2)
            {
                // ---- 阶段 1：摆好 3 个窗口，记下预期 ----
                foreach (var existing in service.GetTodos(service.GetBoards()[0].Id))
                {
                    service.Delete(existing.Id);
                }

                var boards = service.GetBoards().ToList();
                if (boards.Count < 3)
                {
                    service.CreateBoard("会话测试便签", BoardKind.Memo);
                    service.CreateBoard("会话测试日历", BoardKind.Calendar);
                    boards = service.GetBoards().ToList();
                }

                var opened = new List<(string Id, double Left, double Top)>();
                double x = 60;
                foreach (var board in boards.Take(3))
                {
                    var w = windows.OpenBoard(board);
                    w.Left = x;
                    w.Top = 120;
                    await Task.Delay(350);
                    opened.Add((board.Id, w.Left, w.Top));
                    x += 380;
                }

                await Task.Delay(600); // 等防抖保存落盘
                windows.PersistSession();

                // 用全新 repository 实例读一遍（模拟下个进程），确认真的持久化了
                var fresh = new SqliteTodoRepository(AppPaths.DatabasePath);
                fresh.Initialize();
                var freshSettings = new AppSettingsService(fresh);
                var fromDisk = freshSettings.OpenBoards;
                var rawValue = fresh.GetSettings().TryGetValue(AppSettingsService.OpenBoardsKey, out var v) ? v : "(键不存在)";

                Log.Information("[session] 阶段1：打开 {Count} 个窗口，位置={Pos}",
                    opened.Count,
                    string.Join("; ", opened.Select(o => $"{o.Left:0},{o.Top:0}")));
                Log.Information("[session] 阶段1：新连接裸值={Raw} 解析结果={Parsed}",
                    string.IsNullOrEmpty(rawValue) ? "(空串)" : rawValue,
                    fromDisk.Count == 0 ? "(空)" : string.Join(",", fromDisk));

                // 不在这里 CloseAndFlush：OnExit 还要关窗，提前关日志会把后续写入丢掉
                windows.BeginShutdown();
                Shutdown(0);
                return;
            }

            // ---- 阶段 2：模拟重启后恢复 ----
            var wanted = settings.OpenBoards;
            Serilog.Log.Information("[session] 阶段2：注入的 settings 读到 {Count} 个；直连库读到 {Raw}",
                wanted.Count,
                string.Join(",", new AppSettingsService(new SqliteTodoRepository(AppPaths.DatabasePath)).OpenBoards));
            var restored = windows.RestoreSession();
            await Task.Delay(700);

            var actual = windows.OpenWindowCount;
            var positions = new List<string>();
            foreach (var board in service.GetBoards().Where(b => windows.IsBoardOpen(b.Id)))
            {
                positions.Add($"{board.Name}@{board.Window?.Left:0},{board.Window?.Top:0}");
            }

            Log.Information("[session] 阶段2：会话记录={Wanted} 恢复窗口数={Actual} 位置={Pos}",
                string.Join(",", wanted), actual, string.Join("; ", positions));

            if (shotDirectory is not null)
            {
                SaveWindowShots(shotDirectory);
            }

            var ok = restored == wanted.Count && actual == wanted.Count && wanted.Count > 0;
            Log.Information("[session] 总结果={Result}", ok ? "通过" : "失败");

            Log.CloseAndFlush();
            Shutdown(ok ? 0 : 2);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[session] 异常");
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    /// <summary>
    /// --autostart-demo：验证开机自启确实写进注册表，并能读回、能删除。
    /// 结束时恢复原始状态，不留垃圾。
    /// </summary>
    private void RunAutoStartDemo()
    {
        AppPaths.EnsureCreated();
        ConfigureLogging();

        var ok = true;

        try
        {
            var autoStart = new AutoStartService();

            var originalCommand = autoStart.CurrentCommand();
            var originalEnabled = autoStart.IsEnabled();
            Log.Information("[autostart] 初始状态：已启用={Enabled} 命令={Cmd}",
                originalEnabled, originalCommand ?? "(无)");

            // 开
            var setOk = autoStart.SetEnabled(true, out var err);
            var enabled = autoStart.IsEnabled();
            var command = autoStart.CurrentCommand();
            var exePath = AutoStartService.ExecutablePath();

            Log.Information("[autostart] 开启：成功={Ok} 错误={Err} 注册表已存在={Enabled} 命令={Cmd}",
                setOk, err ?? "(无)", enabled, command ?? "(无)");
            Log.Information("[autostart] 当前 exe 路径={Exe}", exePath);

            ok &= setOk && enabled && command is not null && command.Contains("StickyTodo", StringComparison.OrdinalIgnoreCase);

            // 关
            var offOk = autoStart.SetEnabled(false, out var err2);
            var disabled = autoStart.IsEnabled();
            Log.Information("[autostart] 关闭：成功={Ok} 错误={Err} 注册表已清除={Cleared}",
                offOk, err2 ?? "(无)", !disabled);

            ok &= offOk && !disabled;

            // 还原用户原本的设置
            if (originalEnabled)
            {
                autoStart.SetEnabled(true, out _);
            }

            Log.Information("[autostart] 总结果={Result}", ok ? "通过" : "失败");
        }
        catch (Exception ex)
        {
            ok = false;
            Log.Fatal(ex, "[autostart] 异常");
        }

        Log.CloseAndFlush();
        Shutdown(ok ? 0 : 2);
    }

    /// <summary>
    /// --settings-reopen-demo：复现「打开设置 → 关闭 → 再打开」这条崩溃路径。
    /// 循环 5 次，每次检查窗口是否还能正常 Show 出来。
    /// </summary>
    private async void RunSettingsReopenDemo()
    {
        AppPaths.EnsureCreated();
        ConfigureLogging();
        InstallGlobalExceptionHandlers();

        var ok = true;

        try
        {
            _host = BuildHost();
            await _host.StartAsync();

            var repository = _host.Services.GetRequiredService<ITodoRepository>();
            repository.Initialize();
            _host.Services.GetRequiredService<TodoService>().EnsureDefaultBoard();

            var manager = _host.Services.GetRequiredService<ManagerWindow>();
            MainWindow = manager;
            manager.Show();

            for (var round = 1; round <= 5; round++)
            {
                ShowSettings();
                await Task.Delay(400);

                var window = _host.Services.GetRequiredService<SettingsWindow>();
                var shownOk = window.IsVisible;

                // 模拟点标题栏的 ✕（应只是隐藏，不销毁；之后再 Show 才能成功）
                window.Close();

                // 等一会再开，模拟用户连续操作
                await Task.Delay(300);

                Log.Information("[reopen] 第 {Round} 轮：打开成功={Shown}，关闭后 IsVisible={Hidden}",
                    round, shownOk, window.IsVisible);

                ok &= shownOk;
            }

            // 最后一轮：正常打开着退出，确认没有残留
            ShowSettings();
            await Task.Delay(300);
            var finalVisible = _host.Services.GetRequiredService<SettingsWindow>().IsVisible;
            ok &= finalVisible;

            Log.Information("[reopen] 最终仍可打开={Final} 总结果={Result}",
                finalVisible, ok ? "通过" : "失败");
        }
        catch (Exception ex)
        {
            ok = false;
            Log.Fatal(ex, "[reopen] 异常");
        }

        Log.CloseAndFlush();
        Shutdown(ok ? 0 : 2);
    }

    /// <summary>
    /// --window-opacity-demo：验证「整窗透明度」真的作用到窗口上。
    /// 依次设 100% / 60% / 40%，读取每个窗口的 Window.Opacity 并截图。
    /// </summary>
    private void RunWindowOpacityDemo(string? shotDirectory)
    {
        AppPaths.EnsureCreated();
        ConfigureLogging();

        try
        {
            _host = BuildHost();
            var repository = _host.Services.GetRequiredService<ITodoRepository>();
            repository.Initialize();
            var service = _host.Services.GetRequiredService<TodoService>();
            var board = service.EnsureDefaultBoard();

            foreach (var existing in service.GetTodos(board.Id))
            {
                service.Delete(existing.Id);
            }

            service.AddTodo(board.Id, "整窗透明度检查");

            var memoBoard = service.GetBoards().FirstOrDefault(b => b.Kind == BoardKind.Memo);
            var windows = _host.Services.GetRequiredService<WindowManager>();
            var settings = _host.Services.GetRequiredService<SettingsViewModel>();
            var todoWindow = windows.OpenBoard(board);
            MainWindow = todoWindow;

            var steps = new Queue<(string Name, double Value)>();
            steps.Enqueue(("op100", 1.0));
            steps.Enqueue(("op60", 0.60));
            steps.Enqueue(("op40", 0.40));

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            timer.Tick += (_, _) =>
            {
                if (steps.Count == 0)
                {
                    timer.Stop();
                    Shutdown(0);
                    return;
                }

                var (name, value) = steps.Dequeue();

                // 走界面实际使用的那条路：设置页 ViewModel → SkinService → 窗口
                settings.WindowOpacity = value * 100;

                var actual = todoWindow.Opacity;
                Log.Information("[winop] 设置={Target:0.00} 窗口实际 Opacity={Actual:0.00} 一致={Match}",
                    value, actual, Math.Abs(actual - value) < 0.01);

                if (shotDirectory is not null)
                {
                    SaveWindowShots(Path.Combine(shotDirectory, name));
                }
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[winop] 失败");
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    /// <summary>
    /// --lock-demo：锁定看板后截图并检查内容区，用来复现「锁定后白屏」。
    /// </summary>
    private void RunLockDemo(string? shotDirectory)
    {
        AppPaths.EnsureCreated();
        ConfigureLogging();

        try
        {
            _host = BuildHost();
            var repository = _host.Services.GetRequiredService<ITodoRepository>();
            repository.Initialize();
            var service = _host.Services.GetRequiredService<TodoService>();
            var board = service.EnsureDefaultBoard();

            foreach (var existing in service.GetTodos(board.Id))
            {
                service.Delete(existing.Id);
            }

            service.AddTodo(board.Id, "锁定后这条还应该看得见");
            service.AddTodo(board.Id, "第二条待办");

            var windows = _host.Services.GetRequiredService<WindowManager>();
            var window = (BoardWindow)windows.OpenBoard(board);
            MainWindow = window;

            var step = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
            timer.Tick += (_, _) =>
            {
                step++;
                if (step == 1)
                {
                    if (shotDirectory is not null)
                    {
                        SaveWindowShots(Path.Combine(shotDirectory, "unlocked"));
                    }

                    Log.Information("[lock] 锁定前截图完成");
                    window.SwitchLockForTest();
                }
                else
                {
                    timer.Stop();
                    var info = window.DescribeContentVisibility();
                    Log.Information("[lock] 锁定后：{Info}", info);

                    if (shotDirectory is not null)
                    {
                        SaveWindowShots(Path.Combine(shotDirectory, "locked"));
                    }

                    Log.Information("[lock] 锁定后截图完成");
                    Shutdown(0);
                }
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[lock] 失败");
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    /// <summary>
    /// --hover-demo：分别截「待办视图悬停（✓ ✕）」和「已完成视图悬停（↺ ✕）」两张图。
    /// </summary>
    private void RunHoverDemo(string? shotDirectory)
    {
        AppPaths.EnsureCreated();
        ConfigureLogging();

        try
        {
            _host = BuildHost();
            var repository = _host.Services.GetRequiredService<ITodoRepository>();
            repository.Initialize();
            var service = _host.Services.GetRequiredService<TodoService>();
            var board = service.EnsureDefaultBoard();

            foreach (var existing in service.GetTodos(board.Id))
            {
                service.Delete(existing.Id);
            }

            service.AddTodo(board.Id, "鼠标移上来会看到 ✓ 和 ✕");
            service.AddTodo(board.Id, "第二条待办");
            var doneItem = service.AddTodo(board.Id, "这条是已完成的");
            service.SetDone(doneItem.Id, true);

            var windows = _host.Services.GetRequiredService<WindowManager>();
            var window = (BoardWindow)windows.OpenBoard(board);
            MainWindow = window;

            var step = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            timer.Tick += (_, _) =>
            {
                step++;
                if (step == 1)
                {
                    // 待办视图：悬停第一行
                    window.SwitchView(TodoViewMode.Todo);
                    window.ForceRowHover(0);
                    if (shotDirectory is not null)
                    {
                        SaveWindowShots(Path.Combine(shotDirectory, "todo"));
                    }

                    Log.Information("[hover] 待办视图已截图");
                }
                else if (step == 2)
                {
                    // 已完成视图：悬停第一行（显示 ↺）
                    window.SwitchView(TodoViewMode.Done);
                    window.ForceRowHover(0);
                    if (shotDirectory is not null)
                    {
                        SaveWindowShots(Path.Combine(shotDirectory, "done"));
                    }

                    Log.Information("[hover] 已完成视图已截图");
                    timer.Stop();
                    Shutdown(0);
                }
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[hover] 失败");
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    /// <summary>
    /// --tag-drag-demo：应用内走完整链路验证「把待办拖到标签」：
    /// 模拟 Drop 调用的同一套方法（AddTagAsync / ClearTagsAsync），断言标签真的落库。
    /// 这条链路正是 Chip_Drop 里执行的代码，因此能证明拖放功能本身是通的。
    /// </summary>
    private async void RunTagDragDemo()
    {
        AppPaths.EnsureCreated();
        ConfigureLogging();

        try
        {
            _host = BuildHost();
            var repository = _host.Services.GetRequiredService<ITodoRepository>();
            repository.Initialize();
            var service = _host.Services.GetRequiredService<TodoService>();
            var board = service.EnsureDefaultBoard();

            foreach (var existing in service.GetTodos(board.Id))
            {
                service.Delete(existing.Id);
            }

            var tag = service.GetTags().FirstOrDefault(t => t.Name == "重要")
                ?? service.CreateTag("重要");
            service.AddTodo(board.Id, "拖到标签上的待办");

            var windows = _host.Services.GetRequiredService<WindowManager>();
            var window = (BoardWindow)windows.OpenBoard(board);
            MainWindow = window;

            await Task.Delay(1200);

            var before = window.TagTextOfFirstRowFromDb();
            Log.Information("[tagdrag] 拖动前标签={Before}", string.IsNullOrEmpty(before) ? "(无)" : before);

            // 与 Chip_Drop 完全相同的调用
            var applied = await window.SimulateDropOnChipAsync("重要");
            var after = window.TagTextOfFirstRowFromDb();
            Log.Information("[tagdrag] SimulateDrop={Applied} 拖动后标签={After}",
                applied, string.IsNullOrEmpty(after) ? "(无)" : after);

            // 再验证拖到「未分类」= 清空标签
            var cleared = await window.SimulateDropOnChipAsync("未分类");
            var final = window.TagTextOfFirstRowFromDb();
            Log.Information("[tagdrag] 清空后标签={Final} 清空生效={Ok}",
                string.IsNullOrEmpty(final) ? "(无)" : final, string.IsNullOrEmpty(final));

            var ok = applied && after.Contains("重要") && cleared && string.IsNullOrEmpty(final);
            Log.Information("[tagdrag] 总结果={Result}", ok ? "通过" : "失败");

            Log.CloseAndFlush();
            Shutdown(ok ? 0 : 2);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[tagdrag] 失败");
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    /// <summary>
    /// --tag-drag-probe：打印「行」与「标签胶囊」的屏幕坐标；加 --hot 则保持窗口开着，
    /// 供外部脚本用真实鼠标把待办拖到标签上验证。
    /// </summary>
    private void RunTagDragProbe(bool keepOpen)
    {
        AppPaths.EnsureCreated();
        ConfigureLogging();

        try
        {
            _host = BuildHost();
            var repository = _host.Services.GetRequiredService<ITodoRepository>();
            repository.Initialize();
            var service = _host.Services.GetRequiredService<TodoService>();
            var board = service.EnsureDefaultBoard();

            foreach (var existing in service.GetTodos(board.Id))
            {
                service.Delete(existing.Id);
            }

            if (!service.GetTags().Any())
            {
                service.CreateTag("重要");
            }

            service.AddTodo(board.Id, "把这一行拖到上面的标签上");

            var windows = _host.Services.GetRequiredService<WindowManager>();
            var window = (BoardWindow)windows.OpenBoard(board);
            MainWindow = window;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();

                var handle = window.FirstDragHandlePoint();
                var chip = window.ChipScreenPoint("重要");
                var tagBefore = window.TagTextOfFirstRowFromDb();

                Log.Information("[tagdrag] 把手=({HX:0},{HY:0}) 标签「重要」=({CX:0},{CY:0}) 拖动前标签={Before}",
                    handle.X, handle.Y, chip.X, chip.Y, string.IsNullOrEmpty(tagBefore) ? "(无)" : tagBefore);

                if (keepOpen)
                {
                    // 保持窗口开着，2 秒后复查（外部脚本此时已完成拖拽）
                    var verify = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                    verify.Tick += (_, _) =>
                    {
                        verify.Stop();
                        var after = window.TagTextOfFirstRowFromDb();
                        Log.Information("[tagdrag] 拖动后标签={After} 结果={Result}",
                            string.IsNullOrEmpty(after) ? "(无)" : after,
                            after.Contains("重要") ? "成功" : "失败");

                        Log.CloseAndFlush();
                        Shutdown(after.Contains("重要") ? 0 : 2);
                    };
                    verify.Start();
                }
                else
                {
                    Log.CloseAndFlush();
                    Shutdown(0);
                }
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[tagdrag] 失败");
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    /// <summary>
    /// --manager-menu-demo：在主窗体第一张看板卡片上弹右键菜单并截图，
    /// 验证「重命名 / 删除」确实存在且是自绘样式。
    /// </summary>
    private void RunManagerMenuDemo(string? shotDirectory)
    {
        AppPaths.EnsureCreated();
        ConfigureLogging();

        try
        {
            _host = BuildHost();
            var repository = _host.Services.GetRequiredService<ITodoRepository>();
            repository.Initialize();
            var service = _host.Services.GetRequiredService<TodoService>();
            service.EnsureDefaultBoard();
            if (service.GetBoards().Count < 2)
            {
                service.CreateBoard("第二个看板");
            }

            var manager = _host.Services.GetRequiredService<ManagerWindow>();
            MainWindow = manager;
            manager.Show();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var items = manager.ShowFirstRowContextMenu();
                Log.Information("[manager-menu] 菜单项={Items}", string.Join(" | ", items));

                var shot = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                shot.Tick += (_, _) =>
                {
                    shot.Stop();
                    if (shotDirectory is not null)
                    {
                        SavePopupShots(shotDirectory);
                    }

                    var hasRename = items.Any(i => i.Contains("重命名"));
                    var hasDelete = items.Any(i => i == "删除");
                    Shutdown(hasRename && hasDelete ? 0 : 2);
                };
                shot.Start();
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[manager-menu] 失败");
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    /// <summary>--ui-probe：打印拖动把手的屏幕坐标，供外部脚本做真实鼠标拖拽。</summary>
    private void RunUiProbe(string? shotDirectory)
    {
        AppPaths.EnsureCreated();
        ConfigureLogging();

        try
        {
            _host = BuildHost();
            var repository = _host.Services.GetRequiredService<ITodoRepository>();
            repository.Initialize();
            var service = _host.Services.GetRequiredService<TodoService>();
            var board = service.EnsureDefaultBoard();

            foreach (var existing in service.GetTodos(board.Id))
            {
                service.Delete(existing.Id);
            }

            service.AddTodo(board.Id, "第一行");
            service.AddTodo(board.Id, "第二行");
            service.AddTodo(board.Id, "第三行");

            var windows = _host.Services.GetRequiredService<WindowManager>();
            var window = (BoardWindow)windows.OpenBoard(board);
            MainWindow = window;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var p1 = window.FirstDragHandlePoint();
                var p3 = window.RowScreenPoint(2);

                Log.Information("[probe] dragHandle=({X:0},{Y:0}) row3=({R3X:0},{R3Y:0}) 顺序={Order}",
                    p1.X, p1.Y, p3.X, p3.Y, string.Join(",", window.CurrentRowSnapshot()));

                // --ui-probe 一次性打印后退出；--ui-probe-hot 保持窗口开着等待外部拖拽
                if (shotDirectory is null)
                {
                    Log.CloseAndFlush();
                    Shutdown(0);
                }
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[probe] 失败");
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    /// <summary>
    /// --interaction-demo：验证本轮修的交互（拖拽排序、拖到标签改标签、点空白退出编辑）。
    /// 直接调 ViewModel/Service 层，绕开鼠标模拟，但走的都是界面实际调用的同一批方法。
    /// </summary>
    private async void RunInteractionDemo()
    {
        AppPaths.EnsureCreated();
        ConfigureLogging();

        try
        {
            _host = BuildHost();
            var repository = _host.Services.GetRequiredService<ITodoRepository>();
            repository.Initialize();
            var service = _host.Services.GetRequiredService<TodoService>();
            var board = service.EnsureDefaultBoard();

            foreach (var existing in service.GetTodos(board.Id))
            {
                service.Delete(existing.Id);
            }

            var a = service.AddTodo(board.Id, "A");
            service.AddTodo(board.Id, "B");
            service.AddTodo(board.Id, "C");

            var tag = service.GetTags().FirstOrDefault() ?? service.CreateTag("重要");

            // 1) 拖拽排序：把 C 拖到 A 前面
            var before = service.GetTodos(board.Id).Select(t => t.Title).ToArray();
            var c = service.GetTodos(board.Id).First(t => t.Title == "C");
            service.MoveBefore(board.Id, c.Id, a.Id);
            var afterMove = service.GetTodos(board.Id).Select(t => t.Title).ToArray();
            Log.Information("[interaction] 排序前={Before} 排序后={After}", string.Join(",", before), string.Join(",", afterMove));

            // 2) 拖到标签：把 A 打上标签
            service.SetTodoTags(a.Id, [tag.Id]);
            var map = service.GetTodoTagMap(board.Id);
            var tagged = map.TryGetValue(a.Id, out var ids) && ids.Contains(tag.Id);
            Log.Information("[interaction] A 打了标签「{Tag}」={Tagged}", tag.Name, tagged);

            // 3) 拖到「全部/未分类」：清空标签
            service.SetTodoTags(a.Id, []);
            var cleared = !service.GetTodoTagMap(board.Id).TryGetValue(a.Id, out var left) || left.Count == 0;
            Log.Information("[interaction] 清空标签={Cleared}", cleared);

            // 4) 点空白处退出编辑态（界面层）
            var windows = _host.Services.GetRequiredService<WindowManager>();
            var window = (BoardWindow)windows.OpenBoard(board);
            var editOk = await Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(300);
                window.BeginEditFirstRow();
                var before = window.EditingRowCount();
                await window.SimulateBlankAreaClickAsync();
                var after = window.EditingRowCount();
                Log.Information("[interaction] 点空白退出编辑：编辑中 {Before} -> {After}", before, after);
                return before == 1 && after == 0;
            }).Task.Unwrap();

            var ok = afterMove.SequenceEqual(["C", "A", "B"]) && tagged && cleared && editOk;

            Log.CloseAndFlush();
            Shutdown(ok ? 0 : 2);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[interaction] 失败");
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    /// <summary>
    /// --inline-edit-demo：不开界面，直接验证行内编辑的完整链路：
    /// 改标题落库 / 空标题被拦 / 草稿行新增 / 新增后顺序正确。
    /// </summary>
    private void RunInlineEditDemo(string? shotDirectory)
    {
        AppPaths.EnsureCreated();
        ConfigureLogging();

        try
        {
            _host = BuildHost();
            var repository = _host.Services.GetRequiredService<ITodoRepository>();
            repository.Initialize();
            var service = _host.Services.GetRequiredService<TodoService>();
            var board = service.EnsureDefaultBoard();

            // 清空后造固定数据，保证重复运行结论稳定
            foreach (var existing in service.GetTodos(board.Id))
            {
                service.Delete(existing.Id);
            }

            service.AddTodo(board.Id, "第一条");
            service.AddTodo(board.Id, "第二条");
            var first = service.GetTodos(board.Id)[0];

            // 1) 改标题
            service.UpdateTitle(first.Id, "  第一条（改过）  ");
            var renamed = service.GetTodos(board.Id).First(t => t.Id == first.Id);
            Log.Information("[inline] 改标题：新={Title}", renamed.Title);

            // 2) 空标题应被拦
            var emptyBlocked = false;
            try
            {
                service.UpdateTitle(first.Id, "   ");
            }
            catch (ArgumentException)
            {
                emptyBlocked = true;
            }

            Log.Information("[inline] 空标题被拦={Blocked}", emptyBlocked);

            // 3) 新增（模拟草稿行提交）
            service.AddTodo(board.Id, "草稿行新增的");
            var titles = service.GetTodos(board.Id).Select(t => t.Title).ToArray();
            Log.Information("[inline] 新增后列表={Titles}", string.Join(" | ", titles));

            var ok = renamed.Title == "第一条（改过）"
                     && emptyBlocked
                     && titles.Length == 3
                     && titles[^1] == "草稿行新增的";

            // 4) 界面截图：让第一行进入编辑态，看聚焦样式
            if (shotDirectory is not null)
            {
                var windows = _host.Services.GetRequiredService<WindowManager>();
                var window = (BoardWindow)windows.OpenBoard(board);

                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    window.FocusFirstRowTitle();

                    var shot = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                    shot.Tick += (_, _) =>
                    {
                        shot.Stop();
                        SaveWindowShots(shotDirectory);
                        Log.Information("[inline] 已截图（第一行处于编辑态）");
                        Log.CloseAndFlush();
                        Shutdown(ok ? 0 : 2);
                    };
                    shot.Start();
                };
                timer.Start();
                return;
            }

            Log.CloseAndFlush();
            Shutdown(ok ? 0 : 2);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[inline] 失败");
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    /// <summary>
    /// --menu-demo：把待办的右键菜单真正展开并截图，验证自绘菜单样式（圆角、勾选、子菜单箭头）。
    /// </summary>
    private void RunMenuDemo(string? shotDirectory)
    {
        AppPaths.EnsureCreated();
        ConfigureLogging();

        try
        {
            _host = BuildHost();
            var repository = _host.Services.GetRequiredService<ITodoRepository>();
            repository.Initialize();
            var service = _host.Services.GetRequiredService<TodoService>();
            var board = service.EnsureDefaultBoard();

            // --skin 覆盖（验证深色皮肤下菜单配色也跟随）
            var skinOverride = ReadOptionValue(Environment.GetCommandLineArgs(), "--skin");
            if (skinOverride is not null)
            {
                var skinService = _host.Services.GetRequiredService<SkinService>();
                skinService.SetSkin(skinOverride);
                _host.Services.GetRequiredService<WindowManager>().ApplySkin();
            }

            if (!service.GetTodos(board.Id).Any())
            {
                var todo = service.AddTodo(board.Id, "右键这条待办看看菜单");
                service.SetDueAt(todo.Id, DateTimeOffset.Now.Date.AddDays(2));
                service.SetRecurrence(todo.Id, "FREQ=WEEKLY;BYDAY=FR");
                service.AddTodo(board.Id, "另一条待办");
                if (!service.GetTags().Any())
                {
                    var tag = service.CreateTag("重要");
                    service.SetTodoTags(todo.Id, [tag.Id]);
                }
            }

            var windows = _host.Services.GetRequiredService<WindowManager>();
            var window = (BoardWindow)windows.OpenBoard(board);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                window.ShowTodoContextMenuForFirstRow();

                var shot = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                shot.Tick += (_, _) =>
                {
                    shot.Stop();
                    if (shotDirectory is not null)
                    {
                        SavePopupShots(shotDirectory);
                    }

                    // 再展开「标签」子菜单，验证父子菜单样式一致
                    window.OpenFirstSubmenu();
                    var shot2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                    shot2.Tick += (_, _) =>
                    {
                        shot2.Stop();
                        if (shotDirectory is not null)
                        {
                            SavePopupShots(Path.Combine(shotDirectory, "submenu"));
                        }

                        // 关掉待办菜单，再弹「外观」菜单，验证另一套菜单的配色
                        window.CloseTodoContextMenu();
                        window.ShowAppearanceMenuForTest();

                        var shot3 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                        shot3.Tick += (_, _) =>
                        {
                            shot3.Stop();
                            if (shotDirectory is not null)
                            {
                                SavePopupShots(Path.Combine(shotDirectory, "appearance"));
                            }

                            Log.Information("[menu] 待办菜单 / 子菜单 / 外观菜单均已渲染，弹出层数量={Count}",
                                CountOpenPopups());
                            Shutdown(0);
                            Shutdown(0);
                        };
                        shot3.Start();
                    };
                    shot2.Start();
                };
                shot.Start();
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[menu] 失败");
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    /// <summary>截当前所有打开着的弹出层（ContextMenu 是独立 Popup，不在 Window 的视觉树里）。</summary>
    private void SavePopupShots(string directory)
    {
        Directory.CreateDirectory(directory);
        var index = 0;

        foreach (PresentationSource source in PresentationSource.CurrentSources)
        {
            if (source.RootVisual is not FrameworkElement root || root.ActualWidth < 1 || root.ActualHeight < 1)
            {
                continue;
            }

            const double scale = 2d;
            var width = (int)Math.Ceiling(root.ActualWidth * scale);
            var height = (int)Math.Ceiling(root.ActualHeight * scale);

            var target = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            target.Render(CreateBackdropVisual(width, height));
            target.Render(root);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));

            var file = Path.Combine(directory, $"popup-{index:D2}.png");
            using var stream = File.Create(file);
            encoder.Save(stream);
            index++;
        }

        Log.Information("[menu] 已输出 {Count} 张弹出层截图", index);
    }

    private static int CountOpenPopups() =>
        PresentationSource.CurrentSources
            .OfType<System.Windows.Interop.HwndSource>()
            .Count(s => s.RootVisual is FrameworkElement { IsVisible: true });

    /// <summary>
    /// --settings-demo：打开设置页截图，验证皮肤色卡、滑块、快捷键与数据区渲染。
    /// </summary>
    private void RunSettingsDemo(string? shotDirectory, bool tall)
    {
        AppPaths.EnsureCreated();
        ConfigureLogging();

        try
        {
            _host = BuildHost();
            var repository = _host.Services.GetRequiredService<ITodoRepository>();
            repository.Initialize();
            _host.Services.GetRequiredService<TodoService>().EnsureDefaultBoard();

            var settings = _host.Services.GetRequiredService<SettingsWindow>();
            MainWindow = settings;

            // 截图模式下临时加高，让「快捷键 / 数据」也进入一屏（仅影响本次进程）
            if (tall)
            {
                settings.Height = 1060;
                settings.Width = 540;
            }

            settings.Show();

            // --skin 覆盖：验证「设置页选皮肤 → 便签窗口真的变」
            var skinOverride = ReadOptionValue(Environment.GetCommandLineArgs(), "--skin");
            if (skinOverride is not null)
            {
                var skinService = _host.Services.GetRequiredService<SkinService>();
                skinService.SetSkin(skinOverride);
                _host.Services.GetRequiredService<WindowManager>().ApplySkin();
            }

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (shotDirectory is not null)
                {
                    var count = SaveWindowShots(shotDirectory);
                    Log.Information("[settings] 已输出 {Count} 张截图到 {Dir}", count, shotDirectory);
                }

                Shutdown(0);
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[settings] 失败");
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    /// <summary>
    /// --view-demo：造几条待办（含已完成的），依次在「待办视图」和「已完成视图」下截图，
    /// 用来验证视图切换的渲染是否正常。退出码 0 表示两个视图都成功渲染。
    /// </summary>
    private void RunViewDemo(string? shotDirectory)
    {
        AppPaths.EnsureCreated();
        ConfigureLogging();

        try
        {
            _host = BuildHost();

            var repository = _host.Services.GetRequiredService<ITodoRepository>();
            repository.Initialize();
            var service = _host.Services.GetRequiredService<TodoService>();
            var board = service.EnsureDefaultBoard();

            if (!service.GetTodos(board.Id).Any())
            {
                service.AddTodo(board.Id, "整理本周发布清单");
                service.AddTodo(board.Id, "把便签拖到屏幕边缘试试吸附");
                service.AddTodo(board.Id, "右键可以设目标日期和提醒");

                // 完成时间故意拉开，方便一眼看出「今天/昨天/具体日期」三种写法
                var done1 = service.AddTodo(board.Id, "写下第一条待办");
                service.SetDone(done1.Id, true);

                var done2 = service.AddTodo(board.Id, "配置好数据目录");
                service.SetDone(done2.Id, true);

                var done3 = service.AddTodo(board.Id, "定下界面配色");
                service.SetDone(done3.Id, true);
                BackdateCompletion(done3.Id, DateTimeOffset.Now.AddDays(-1));
            }

            var windows = _host.Services.GetRequiredService<WindowManager>();
            var window = (BoardWindow)windows.OpenBoard(board);

            var steps = new Queue<(string Name, TodoViewMode Mode)>();
            steps.Enqueue(("todo", TodoViewMode.Todo));
            steps.Enqueue(("done", TodoViewMode.Done));
            steps.Enqueue(("done-to-todo", TodoViewMode.Todo));

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            timer.Tick += (_, _) =>
            {
                if (steps.Count == 0)
                {
                    timer.Stop();
                    Log.Information("[view] 两个视图都已渲染完成");
                    Shutdown(0);
                    return;
                }

                var (name, mode) = steps.Dequeue();
                window.SwitchView(mode);

                var shot = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                shot.Tick += (_, _) =>
                {
                    shot.Stop();
                    var rows = window.CurrentRowSnapshot();
                    var (inputEnabled, addEnabled) = window.CurrentInputState();
                    Log.Information("[view] 视图={Name} 行数={Rows} 输入框可用={Input} 添加可用={Add} 内容={Items}",
                        name, rows.Count, inputEnabled, addEnabled, string.Join(" | ", rows));
                    if (shotDirectory is not null)
                    {
                        SaveWindowShots(Path.Combine(shotDirectory, name));
                    }
                };
                shot.Start();
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[view] 失败");
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    /// <summary>
    /// --recurrence-demo：不开界面，验证重复待办的完整链路（建规则 → 勾选 → 滚动 → 日历展开）。
    /// 输出到日志，退出码 0 表示全部断言通过。
    /// </summary>
    private void RunRecurrenceDemo()
    {
        AppPaths.EnsureCreated();
        ConfigureLogging();

        try
        {
            var repository = new SqliteTodoRepository(AppPaths.DatabasePath);
            repository.Initialize();
            var service = new TodoService(repository, new PermanentFreeEntitlement());
            var board = service.EnsureDefaultBoard();

            var start = DateTimeOffset.Now.Date.AddHours(9);
            var todo = service.AddTodo(board.Id, "每周复盘", start);
            service.SetRecurrence(todo.Id, "FREQ=WEEKLY;BYDAY=FR");

            var item = service.GetTodos(board.Id).First(t => t.Id == todo.Id);
            Log.Information("[recurrence] 规则={Rule} 描述={Desc} 下一次={Next}",
                item.Recurrence, RecurrenceEngine.Describe(item.Recurrence), service.GetNextOccurrence(item));

            var before = item.DueAt;
            service.CompleteOccurrence(todo.Id);
            var after = service.GetTodos(board.Id).First(t => t.Id == todo.Id);

            Log.Information("[recurrence] 勾选后：原来={Before:yyyy-MM-dd ddd} 现在={After:yyyy-MM-dd ddd} 未完成={Undone} 完成历史={History}",
                before, after.DueAt, !after.IsDone, service.GetCompletions(todo.Id).Count);

            var from = DateTimeOffset.Now.Date.AddDays(-1);
            var to = from.AddDays(60);
            var entries = service.GetCalendarEntries(from, to);
            Log.Information("[recurrence] 未来 60 天日历展开 {Count} 次：{Days}",
                entries.Count, string.Join(", ", entries.Select(x => x.Occurrence.ToString("MM-dd"))));

            var ok = after.DueAt > before && !after.IsDone
                     && service.GetCompletions(todo.Id).Count == 1
                     && entries.Count >= 8;

            Log.CloseAndFlush();
            Shutdown(ok ? 0 : 2);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[recurrence] 失败");
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    /// <summary>
    /// --demo：库里还没有任何待办时种一份示例数据，并打开两个便签窗口摆到相邻位置，
    /// 用来验证多窗口 + 吸附 + 自动截图的回归链路（截图脚本见 tools/capture-demo.ps1）。
    /// </summary>
    private void RunDemo(string? shotDirectory, bool fireReminder)
    {
        var service = _host!.Services.GetRequiredService<TodoService>();
        var windows = _host.Services.GetRequiredService<WindowManager>();

        var defaultBoard = service.EnsureDefaultBoard();
        var boards = service.GetBoards();
        var workBoard = boards.FirstOrDefault(b => b.Id != defaultBoard.Id) ?? service.CreateBoard("工作");

        if (!service.GetTodos(defaultBoard.Id).Any() && !service.GetTodos(workBoard.Id).Any())
        {
            service.AddTodo(defaultBoard.Id, "把便签拖到屏幕边缘，会自动贴边吸附");
            service.AddTodo(defaultBoard.Id, "拖住待办上下拖动可以排序");
            service.AddTodo(defaultBoard.Id, "右键待办可以挂标签 / 设目标日期 / 设提醒");

            var done = service.AddTodo(defaultBoard.Id, "写下第一条待办：已完成的样子");
            service.SetDone(done.Id, true);

            var meeting = service.AddTodo(workBoard.Id, "整理本周发布清单");
            service.SetDueAt(meeting.Id, DateTimeOffset.Now.Date.AddDays(2));
            service.SetRemindAt(meeting.Id, DateTimeOffset.Now.AddMinutes(20));

            service.AddTodo(workBoard.Id, "周一 10:00 项目周会");

            if (!service.GetTags().Any())
            {
                var important = service.CreateTag("重要");
                service.SetTodoTags(meeting.Id, [important.Id]);
            }
        }

        // 便签/随笔看板：一张大白纸
        var memoBoard = service.GetBoards().FirstOrDefault(b => b.Kind == BoardKind.Memo)
            ?? service.CreateBoard("随手记", BoardKind.Memo);
        if (string.IsNullOrWhiteSpace(service.GetNote(memoBoard.Id)?.Content))
        {
            service.SaveNote(memoBoard.Id,
                "随手记\n\n" +
                "· 这个看板只有一张纸，边写边自动保存\n" +
                "· 左下角会显示字数与保存时间\n" +
                "· 想清空就全选删掉，内容会立刻同步到本地数据库\n\n" +
                "小黄条式的便签用法：把几个看板摊在桌面上，写今天要做的事。");
        }

        // 日历看板（月视图）
        var calendarBoard = service.GetBoards().FirstOrDefault(b => b.Kind == BoardKind.Calendar)
            ?? service.CreateBoard("日历", BoardKind.Calendar);

        // 重复待办示例：每周一次，落在日历上能看到展开
        if (!service.GetTodos(workBoard.Id).Any(t => t.Recurrence is not null))
        {
            var weekly = service.AddTodo(workBoard.Id, "每周复盘", NextWeekday(DateTimeOffset.Now, DayOfWeek.Friday));
            service.SetRecurrence(weekly.Id, "FREQ=WEEKLY;BYDAY=FR");        }

        var first = windows.OpenBoard(defaultBoard);
        var second = windows.OpenBoard(workBoard);
        var third = windows.OpenBoard(memoBoard);
        var fourth = windows.OpenBoard(calendarBoard);

        if (fireReminder)
        {
            // --reminder-demo：造一条已经到点的提醒，立刻走一遍「查询 → 标记 → 弹窗」链路
            var target = service.GetTodos(workBoard.Id).FirstOrDefault();
            if (target is not null)
            {
                service.SetRemindAt(target.Id, DateTimeOffset.Now.AddMinutes(-1));
                _ = _host!.Services.GetRequiredService<ReminderPump>().CheckAsync();
            }
        }

        // 先摆第一个窗口（会被吸到屏幕左边），再摆第二个（会吸到第一个的右边）。
        // 坐标故意偏几像素，用来验证吸附确实生效（窗口外圈 10px 是阴影留白，不算纸面）。
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            first.Left = -4;
            first.Top = 140;
            second.Left = 336;
            second.Top = 140;
            third.Left = 700;
            third.Top = 140;
            fourth.Left = 1000;
            fourth.Top = 60;
            Log.Information("[demo] 窗口1=({L1},{T1},{W1}x{H1}) 窗口2=({L2},{T2},{W2}x{H2})",
                first.Left, first.Top, first.ActualWidth, first.ActualHeight,
                second.Left, second.Top, second.ActualWidth, second.ActualHeight);

            if (MainWindow is { } manager)
            {
                Log.Information("[demo] 管理面板=({L},{T},{W}x{H})",
                    manager.Left, manager.Top, manager.ActualWidth, manager.ActualHeight);
            }

            if (shotDirectory is not null)
            {
                // 等一帧渲染完成再截图
                var shotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                shotTimer.Tick += (_, _) =>
                {
                    shotTimer.Stop();
                    try
                    {
                        var count = SaveWindowShots(shotDirectory);
                        Log.Information("[demo] 已输出 {Count} 张截图到 {Dir}", count, shotDirectory);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[demo] 截图失败");
                    }

                    Shutdown(0);
                };
                shotTimer.Start();
            }
        };
        timer.Start();
    }

    /// <summary>
    /// 把每个可见窗口的视觉树渲染成 PNG。用 RenderTargetBitmap 而不是屏幕 BitBlt，
    /// 好处是结果与 DPI 缩放无关，也不会被其它窗口遮挡。
    /// 便签窗口是半透明的，所以先铺一层彩色背景，一眼就能看出透明度是否生效。
    /// </summary>
    private int SaveWindowShots(string directory)
    {
        Directory.CreateDirectory(directory);
        var saved = 0;
        var index = 0;

        foreach (Window window in Windows)
        {
            if (window is not { IsVisible: true } || window.ActualWidth < 1 || window.ActualHeight < 1)
            {
                continue;
            }

            const double scale = 2d; // 2 倍渲染，方便查看细节
            var width = (int)Math.Ceiling(window.ActualWidth * scale);
            var height = (int)Math.Ceiling(window.ActualHeight * scale);

            var target = new RenderTargetBitmap(
                width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);

            if (window is BoardWindow)
            {
                target.Render(CreateBackdropVisual(width, height));
            }

            target.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));

            var title = string.IsNullOrWhiteSpace(window.Title) ? "window" : window.Title;
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                title = title.Replace(invalid, '_');
            }

            var file = Path.Combine(directory, $"{index:D2}-{title}.png");
            using var stream = File.Create(file);
            encoder.Save(stream);

            saved++;
            index++;
        }

        return saved;
    }

    /// <summary>截图用的假桌面背景：彩色渐变 + 几个色块，用来检查便签是否真的半透明。</summary>
    private static DrawingVisual CreateBackdropVisual(int width, int height)
    {
        var visual = new DrawingVisual();
        using var context = visual.RenderOpen();

        var gradient = new LinearGradientBrush(
            Color.FromRgb(0x1B, 0x3A, 0x6B), Color.FromRgb(0x0E, 0x8A, 0x74),
            new Point(0, 0), new Point(1, 1));
        context.DrawRectangle(gradient, null, new Rect(0, 0, width, height));

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), width * 0.02);
        for (var i = 0; i < 6; i++)
        {
            var size = width * 0.28;
            context.DrawEllipse(
                new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xD5, 0x4F)),
                pen,
                new Point(width * (0.12 + (0.16 * i)), height * (0.2 + (0.11 * (i % 4)))),
                size,
                size);
        }

        context.DrawLine(
            new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), width * 0.006),
            new Point(0, height * 0.72),
            new Point(width, height * 0.42));

        return visual;
    }

    /// <summary>打开（或激活）设置窗口。托盘菜单和主窗体左侧入口都走这里。</summary>
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

    /// <summary>下一个指定星期几（含今天）。</summary>
    private static DateTimeOffset NextWeekday(DateTimeOffset from, DayOfWeek target)
    {
        var delta = ((int)target - (int)from.DayOfWeek + 7) % 7;
        return from.Date.AddDays(delta == 0 ? 7 : delta).AddHours(9);
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

    protected override async void OnExit(ExitEventArgs e)
    {
        // 必须在最前面：WPF 会在 Shutdown 过程中逐个关闭窗口，
        // 那时若还允许写会话，会把记录清成空列表，下次启动就恢复不了。
        try
        {
            _host?.Services.GetService<WindowManager>()?.BeginShutdown();
        }
        catch (Exception)
        {
            // 退出路径上不抛
        }

        try
        {
            if (_host is not null)
            {
                _host.Services.GetService<WindowManager>()?.CloseAll();
                _host.Services.GetService<TrayService>()?.Dispose();
                await _host.StopAsync(TimeSpan.FromSeconds(3));
                _host.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "退出清理失败");
        }
        finally
        {
            Log.CloseAndFlush();
            _mutex?.Dispose();
        }

        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        Shutdown();
    }
}