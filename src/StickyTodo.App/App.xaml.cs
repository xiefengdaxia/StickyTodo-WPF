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
using StickyTodo.Weather;
using StickyTodo.Infrastructure.Persistence;

namespace StickyTodo.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\StickyTodo.SingleInstance";

    private Mutex? _mutex;
    private IHost? _host;

    /// <summary>
    /// 回归演示里替换掉真实的 HTTP 数据源（见 <c>--token-demo</c>）。
    /// 正式运行时为 null，走真实的 <see cref="Infrastructure.Token.OpenCodeTokenStatsProvider"/>。
    /// </summary>
    private static Core.Abstractions.ITokenStatsProvider? TokenProviderOverride { get; set; }

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

        // 标签计数回归：加/删标签后检查胶囊计数是否跟着变
        if (e.Args.Contains("--chip-count-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunChipCountDemo();
            return;
        }

        // 番茄时钟回归：走完整番茄周期，验证计时/暂停/跳过/休息
        if (e.Args.Contains("--pomodoro-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunPomodoroDemo(ReadOptionValue(e.Args, "--shot"));
            return;
        }

        // 便携模式回归：验证数据目录确实跟着 exe 走
        if (e.Args.Contains("--portable-check", StringComparer.OrdinalIgnoreCase))
        {
            RunPortableCheck();
            return;
        }

        // 多语言回归：四种语言逐一切换并校验界面文案
        if (e.Args.Contains("--i18n-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunI18nDemo(ReadOptionValue(e.Args, "--shot"));
            return;
        }

        // 动效回归：确认各处动画真的在跑、且不抛异常
        if (e.Args.Contains("--motion-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunMotionDemo(ReadOptionValue(e.Args, "--shot"));
            return;
        }

        // 日历回归：农历/节气/节假日/补班/黄历/年月下拉切换
        if (e.Args.Contains("--calendar-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunCalendarDemo(ReadOptionValue(e.Args, "--shot"));
            return;
        }

        // Token 便签回归：假数据源 + 范围/间隔/手动刷新/点击看明细
        if (e.Args.Contains("--token-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunTokenDemo(ReadOptionValue(e.Args, "--shot"), e.Args.Contains("--live", StringComparer.OrdinalIgnoreCase));
            return;
        }

        // 桌面时钟联动回归：今日日程摘要 + 点日程/天气自动建看板并打开
        if (e.Args.Contains("--clock-demo", StringComparer.OrdinalIgnoreCase))
        {
            RunClockDemo();
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

            // 回归/演示路径也要跟随设置里的语言
            ApplyLanguageForCurrentRun();
            _host.Services.GetRequiredService<TodoService>().EnsureDefaultBoard();

            // 命令行覆盖（演示/截图回归用）：--skin mint --opacity 0.6 --window-opacity 0.9
            var skinService = _host.Services.GetRequiredService<SkinService>();

            // 语言必须在任何窗口创建之前应用，否则会先渲染中文再切换，闪一下
            var localization = _host.Services.GetRequiredService<LocalizationService>();
            var languageOverride = ReadOptionValue(e.Args, "--lang");
            if (!string.IsNullOrWhiteSpace(languageOverride))
            {
                // 命令行覆盖只影响本次运行，不写回设置（截图回归用）
                StickyTodo.Core.Localization.Translator.Current.Language =
                    StickyTodo.Core.Localization.AppLanguages.Resolve(languageOverride).Language;
            }
            else
            {
                localization.ApplyFromSettings();
            }

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

            // --autostart（开机自启拉起）：不弹管理面板，只把上次的看板摆出来。
            // 若根本没恢复出窗口（比如首次开机、或上次全关了），才退化成显示管理面板，
            // 否则用户会以为程序没启动。
            var launchedByAutoStart = e.Args.Contains("--autostart", StringComparer.OrdinalIgnoreCase);

            if (!launchedByAutoStart)
            {
                manager.Show();
            }

            var tray = _host.Services.GetRequiredService<TrayService>();

            // 先刷掉上次被强杀进程留下的托盘图标，再挂自己的图标
            TrayRefresher.Refresh();
            tray.Start();

            StartReminderPump(tray);

            // 工具窗口演示参数：--weather-demo / --worldclock-demo / --desktopclock-demo
            if (e.Args.Contains("--weather-demo", StringComparer.OrdinalIgnoreCase))
            {
                OpenTool(ToolWindowKind.Weather);
            }
            if (e.Args.Contains("--worldclock-demo", StringComparer.OrdinalIgnoreCase))
            {
                OpenTool(ToolWindowKind.WorldClock);
            }
            if (e.Args.Contains("--desktopclock-demo", StringComparer.OrdinalIgnoreCase))
            {
                OpenTool(ToolWindowKind.DesktopClock);
            }

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
                else if (launchedByAutoStart)
                {
                    // 没有可恢复的窗口，兜底把管理面板显示出来
                    manager.Show();
                }
                else
                {
                    manager.Show();
                }
            }

            // --create-tool-demo <weather|worldclock|desktopclock>：打开一个工具型看板（进「我的看板」列表）。
            // 已有同类型的就复用，否则每次启动都会多建一个看板。
            // 必须放在 RestoreSession 之后：先开会话记录会被覆盖，上次的看板就丢了。
            var toolArg = ToolDemoArgument(e.Args);
            var toolKind = toolArg?.Trim().ToLowerInvariant() switch
            {
                "weather" => BoardKind.Weather,
                "worldclock" => BoardKind.WorldClock,
                "desktopclock" => BoardKind.DesktopClock,
                _ => (BoardKind?)null,
            };

            if (toolKind is { } kind)
            {
                var todos = _host.Services.GetRequiredService<TodoService>();
                var board = todos.GetBoards().FirstOrDefault(b => b.Kind == kind)
                    ?? todos.CreateBoard(
                        kind switch
                        {
                            BoardKind.Weather => "天气",
                            BoardKind.WorldClock => "世界时钟",
                            _ => "桌面时钟",
                        }, kind);

                _host.Services.GetRequiredService<WindowManager>().OpenBoard(board);
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
                services.AddSingleton<LocalizationService>();
                services.AddSingleton<WindowManager>();
                services.AddSingleton<TrayService>();
                services.AddSingleton<ReminderPump>();
                services.AddSingleton<HotKeyService>();
                services.AddSingleton<AutoStartService>();
                services.AddSingleton<Weather.WeatherService>();
            services.AddSingleton<Weather.WeatherCache>();
                services.AddSingleton<ViewModels.WorldClockViewModel>();
                services.AddSingleton<ViewModels.DesktopClockViewModel>();
                services.AddSingleton<Core.Abstractions.ITokenStatsProvider>(sp =>
                {
                    if (TokenProviderOverride is not null)
                    {
                        return TokenProviderOverride;
                    }

                    // 默认读本机 opencode 数据库；设置里填了自定义路径就用它。
                    // 多 Agent 聚合：opencode + zcode + codex + workbuddy + claude + gemini + qoder + kimi + pi
                    var settings = sp.GetRequiredService<AppSettingsService>();
                    var custom = settings.TokenDatabasePath;
                    return Infrastructure.Token.MultiAgentTokenStatsProvider.CreateDefault(
                        string.IsNullOrWhiteSpace(custom) ? null : custom);
                });
                services.AddSingleton<Core.Pomodoro.PomodoroEngine>();
                services.AddSingleton<ViewModels.PomodoroViewModel>();
                services.AddSingleton<ManagerViewModel>();
                services.AddSingleton<ManagerWindow>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<SettingsWindow>();
                services.AddTransient<ViewModels.WeatherViewModel>();
                services.AddTransient<WeatherWindow>();
                services.AddTransient<WorldClockWindow>();
                services.AddTransient<DesktopClockWindow>();
            })
            .Build();
    }

    /// <summary>天气窗口的外观菜单（皮肤 + 透明度），天气/世界时钟窗口共用。</summary>
    public void ShowWeatherAppearanceMenu(Window window, FrameworkElement? target)
    {
        var skin = _host!.Services.GetRequiredService<SkinService>();
        var settings = _host.Services.GetRequiredService<AppSettingsService>();
        var menu = Views.WeatherMenuBuilder.Build(skin, settings);

        if (target is not null)
        {
            menu.PlacementTarget = target;
        }

        menu.IsOpen = true;
    }

    /// <summary>
    /// 打开工具型面板（天气 / 世界时钟 / 桌面时钟）。
    /// 走「正式看板」路径：没有就建一条 Board 记录，于是它和待办看板一样
    /// 出现在「我的看板」列表里，也参与会话恢复。已开着就只激活。
    /// </summary>
    public void OpenTool(ToolWindowKind kind)
    {
        var services = _host!.Services;

        var (boardKind, defaultName) = kind switch
        {
            ToolWindowKind.Weather => (BoardKind.Weather, "天气"),
            ToolWindowKind.WorldClock => (BoardKind.WorldClock, "世界时钟"),
            ToolWindowKind.DesktopClock => (BoardKind.DesktopClock, "桌面时钟"),
            _ => (BoardKind.Todo, string.Empty),
        };

        if (boardKind == BoardKind.Todo)
        {
            return;
        }

        services.GetRequiredService<WindowManager>().OpenBoardByKind(boardKind, defaultName);
    }

    /// <summary>
    /// 所有 demo / 回归路径进入 WPF 界面前都调一次：把设置里的语言应用上去。
    /// 否则只有正常启动路径会本地化，`--settings-demo` 之类的回归截图仍是中文。
    /// </summary>
    private void ApplyLanguageForCurrentRun()
    {
        if (_host is null)
        {
            return;
        }

        var overrideKey = ReadOptionValue(Environment.GetCommandLineArgs(), "--lang");
        if (!string.IsNullOrWhiteSpace(overrideKey))
        {
            StickyTodo.Core.Localization.Translator.Current.Language =
                StickyTodo.Core.Localization.AppLanguages.Resolve(overrideKey).Language;
            return;
        }

        _host.Services.GetRequiredService<LocalizationService>().ApplyFromSettings();
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
    /// --skin-check：验证「设置页换皮肤 → 便签窗口真的变」，并检查 10 套皮肤的结构完整性。
    /// 逐套切换并读取便签窗口实际用到的画刷颜色，退出码 0 表示全部同步生效。
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

            // 回归/演示路径也要跟随设置里的语言
            ApplyLanguageForCurrentRun();
            var service = _host.Services.GetRequiredService<TodoService>();
            var board = service.EnsureDefaultBoard();
            if (!service.GetTodos(board.Id).Any())
            {
                service.AddTodo(board.Id, "皮肤切换检查");
            }

            var windows = _host.Services.GetRequiredService<WindowManager>();
            var skinService = _host.Services.GetRequiredService<SkinService>();
            var window = windows.OpenBoard(board);

            var ok = true;

            // 1) 数量与唯一性
            Log.Information("[skin] 共 {Count} 套皮肤", NoteSkins.All.Count);
            ok &= NoteSkins.All.Count == 10;
            ok &= NoteSkins.All.Select(s => s.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() == NoteSkins.All.Count;
            ok &= NoteSkins.All.Select(s => s.DisplayName).Distinct().Count() == NoteSkins.All.Count;

            // 2) 每套都要有内容，且文字与纸面有足够对比度（否则暗色皮肤会看不清）
            foreach (var skin in NoteSkins.All)
            {
                var inkOnPaper = ContrastRatio(skin.Ink, skin.BackgroundTop);
                var accentOnPaper = ContrastRatio(skin.Accent, skin.BackgroundTop);

                // 正文对比度至少 4.5（WCAG AA），强调色至少 2.5（做点缀用，不要求那么高）
                var textOk = inkOnPaper >= 4.5;
                var accentOk = accentOnPaper >= 2.5;

                ok &= textOk && accentOk;
                ok &= !string.IsNullOrWhiteSpace(skin.DisplayName);

                Log.Information(
                    "[skin] {Key,-14} {Name,-8} 暗色={Dark,-5} 正文对比={InkRatio,5:0.00} 强调对比={AccentRatio,5:0.00} {Verdict}",
                    skin.Key, skin.DisplayName, skin.IsDark, inkOnPaper, accentOnPaper,
                    textOk && accentOk ? "OK" : "不达标");
            }

            // 3) 每套皮肤切过去之后，应用资源里的画刷都要跟着变
            foreach (var skin in NoteSkins.All)
            {
                skinService.SetSkin(skin.Key);
                windows.ApplySkin();

                var resources = System.Windows.Application.Current.Resources;
                var ink = ((SolidColorBrush)resources["Brush.Ink"]).Color;
                var accent = ((SolidColorBrush)resources["Brush.Accent"]).Color;
                var isDark = (bool)resources["Bool.IsDark"];

                var match = ink == skin.Ink && accent == skin.Accent && isDark == skin.IsDark;
                ok &= match;

                if (!match)
                {
                    Log.Warning("[skin] {Key} 资源未同步：ink={Ink} accent={Accent} dark={Dark}",
                        skin.Key, ink, accent, isDark);
                }
            }

            // 4) 切到新增的两套，确认暗色标记正确
            foreach (var key in new[] { "apricot", "espresso" })
            {
                var skin = NoteSkins.Resolve(key);
                ok &= skin.Key == key;
                Log.Information("[skin] 新增皮肤 {Key} 解析正确，暗色={Dark}", skin.Key, skin.IsDark);
            }

            // 收尾切回默认
            skinService.SetSkin(NoteSkins.ClassicYellow.Key);
            windows.ApplySkin();

            Log.Information("[skin] 便签窗口类型={Type} 标题={Title}", window.GetType().Name, window.Title);
            Log.Information("[skin] 总结果={Result}", ok ? "通过" : "失败");

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

    /// <summary>WCAG 相对亮度对比度。用来判断"这套配色的字能不能看清"。</summary>
    private static double ContrastRatio(Color a, Color b)
    {
        static double Luminance(Color c)
        {
            static double Channel(byte v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
        }

        var l1 = Luminance(a);
        var l2 = Luminance(b);
        var lighter = Math.Max(l1, l2);
        var darker = Math.Min(l1, l2);

        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// --chip-count-demo：验证「给待办加/删标签后，标签胶囊上的计数是否跟着变」。
    /// </summary>
    private async void RunChipCountDemo()
    {
        AppPaths.EnsureCreated();
        ConfigureLogging();

        try
        {
            _host = BuildHost();
            await _host.StartAsync();

            var repository = _host.Services.GetRequiredService<ITodoRepository>();
            repository.Initialize();

            // 回归/演示路径也要跟随设置里的语言
            ApplyLanguageForCurrentRun();
            var service = _host.Services.GetRequiredService<TodoService>();
            var board = service.EnsureDefaultBoard();

            foreach (var existing in service.GetTodos(board.Id))
            {
                service.Delete(existing.Id);
            }

            foreach (var tag in service.GetTags())
            {
                service.DeleteTag(tag.Id);
            }

            var important = service.CreateTag("重要");
            var first = service.AddTodo(board.Id, "第一条");
            service.AddTodo(board.Id, "第二条");

            var windows = _host.Services.GetRequiredService<WindowManager>();
            var window = (BoardWindow)windows.OpenBoard(board);
            MainWindow = window;
            await Task.Delay(900);

            string Dump() => string.Join(" | ", window.ChipCounts());

            Log.Information("[chip] 初始计数：{Counts}", Dump());

            // 1) 加标签
            await window.AddTagForTestAsync(0, important.Id);
            await Task.Delay(400);
            var afterAdd = Dump();
            Log.Information("[chip] 给第一条加「重要」后：{Counts}", afterAdd);

            // 2) 再给第二条加
            await window.AddTagForTestAsync(1, important.Id);
            await Task.Delay(400);
            var afterAdd2 = Dump();
            Log.Information("[chip] 给第二条也加后：{Counts}", afterAdd2);

            // 3) 清空第一条
            await window.ClearTagsForTestAsync(0);
            await Task.Delay(400);
            var afterClear = Dump();
            Log.Information("[chip] 清空第一条后：{Counts}", afterClear);

            var ok = afterAdd.Contains("重要 1")
                     && afterAdd2.Contains("重要 2")
                     && afterClear.Contains("重要 1");

            // 4) 删除带标签的待办：计数应下降
            var rowToDelete = window.CurrentRowSnapshot().ToList();
            await window.DeleteRowForTestAsync(1); // 第二条（挂着「重要」）
            await Task.Delay(400);
            var afterDeleteTodo = Dump();
            Log.Information("[chip] 删除挂了标签的待办后：{Counts}", afterDeleteTodo);
            ok &= afterDeleteTodo.Contains("重要 0");

            // 5) 删除标签本身：胶囊应消失
            var tag2 = service.CreateTag("临时");
            service.SetTodoTags(service.GetTodos(board.Id).First().Id, [tag2.Id]);
            await window.ReloadForTestAsync();
            var withTag2 = Dump();
            service.DeleteTag(tag2.Id);
            await window.ReloadForTestAsync();
            var afterDeleteTag = Dump();
            Log.Information("[chip] 建「临时」并挂上：{Before}；删除该标签后：{After}", withTag2, afterDeleteTag);
            ok &= withTag2.Contains("临时 1") && !afterDeleteTag.Contains("临时");

            // 同时截一张图，确认 UI 上真的显示了更新后的计数
            SaveWindowShots(Path.Combine(Path.GetTempPath(), "chip-shots"));

            Log.Information("[chip] 总结果={Result}", ok ? "通过" : "失败");

            Log.CloseAndFlush();
            Shutdown(ok ? 0 : 2);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[chip] 异常");
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    /// <summary>
    /// --calendar-demo：日历回归。检查农历/节气/休班标记/黄历宜忌，以及年月下拉切换。
    /// </summary>
    private async void RunCalendarDemo(string? shotDirectory)
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

            // 回归/演示路径也要跟随设置里的语言
            ApplyLanguageForCurrentRun();
            var service = _host.Services.GetRequiredService<TodoService>();

            var board = service.CreateBoard("日历", BoardKind.Calendar);
            var windows = _host.Services.GetRequiredService<WindowManager>();
            var window = (CalendarWindow)windows.OpenBoard(board);
            MainWindow = window;
            await Task.Delay(1200);

            // 跳到 2026 年 10 月（国庆 + 中秋 + 补班都在这段）
            await window.GoToForTestAsync(2026, 10);
            await Task.Delay(400);

            var summary = window.DescribeForTest();
            Log.Information("[calendar] 2026-10：{Summary}", summary);

            var days = window.DaysForTest();
            var ok = days.Count == 42; // 6 行 × 7 列

            // 1) 每天都要有农历
            ok &= days.All(d => !string.IsNullOrEmpty(d.LunarText));
            Log.Information("[calendar] 农历覆盖 {Filled}/{Total}", days.Count(d => !string.IsNullOrEmpty(d.LunarText)), days.Count);

            // 2) 国庆 1–7 号都要有「休」
            var national = days.Where(d => d.Date.Year == 2026 && d.Date.Month == 10 && d.Date.Day <= 7).ToList();
            ok &= national.Count == 7 && national.All(d => d.Badge == "休");
            Log.Information("[calendar] 国庆 1–7 休标记：{Info}",
                string.Join(" ", national.Select(d => $"{d.Date.Day}{d.Badge}")));

            // 3) 补班日 10-10 要有「班」
            var makeup = days.FirstOrDefault(d => d.Date == new DateOnly(2026, 10, 10));
            ok &= makeup is not null && makeup.Badge == "班";
            Log.Information("[calendar] 10-10 标记：{Badge}（假日名={Name}）", makeup?.Badge, makeup?.SubText);

            // 4) 节气：10-08 寒露、10-23 霜降
            var hanlu = days.FirstOrDefault(d => d.Date == new DateOnly(2026, 10, 8));
            var shuangjiang = days.FirstOrDefault(d => d.Date == new DateOnly(2026, 10, 23));
            ok &= hanlu?.SubText == "寒露";
            ok &= shuangjiang?.SubText == "霜降";
            Log.Information("[calendar] 节气：10-08={A} 10-23={B}", hanlu?.SubText, shuangjiang?.SubText);

            // 5) 中秋 9-25 ~ 9-27 也是假期（在上一屏，切到 9 月验证）
            await window.GoToForTestAsync(2026, 9);
            await Task.Delay(300);
            var sepDays = window.DaysForTest();
            var moon = sepDays.Where(d => d.Date.Year == 2026 && d.Date.Month == 9 && d.Date.Day is >= 25 and <= 27).ToList();
            ok &= moon.Count == 3 && moon.All(d => d.Badge == "休");
            Log.Information("[calendar] 中秋 9-25~27：{Info}",
                string.Join(" ", moon.Select(d => $"{d.Date.Day}{d.Badge}/{d.SubText}")));

            // 6) 黄历：点某一天应给出宜/忌
            await window.SelectDayForTestAsync(new DateOnly(2026, 9, 25));
            var almanac = window.AlmanacForTest();
            Log.Information("[calendar] 9-25 黄历：{Info}", almanac);
            ok &= almanac.Contains("宜") && almanac.Contains("忌") && almanac.Contains("中秋");

            // 7) 年月下拉切换：选 2027 年 2 月，应跳到该月并重算
            await window.SelectYearForTestAsync(2027);
            await window.SelectMonthForTestAsync(2);
            await Task.Delay(400);
            var spring = window.DescribeForTest();
            Log.Information("[calendar] 切到 2027-02：{Summary}", spring);
            ok &= spring.Contains("2027") && spring.Contains("2 月");

            // 春节 2027-02-06 那天应是农历正月初一
            var springDays = window.DaysForTest();
            var newYear = springDays.FirstOrDefault(d => d.Date == new DateOnly(2027, 2, 6));
            ok &= newYear?.LunarFullText == "正月初一";
            Log.Information("[calendar] 2027-02-06：{Info}", $"{newYear?.LunarFullText}/{newYear?.SubText}");

            // 7b) 选中的日期应随着切月回到该月内的今天/1 号，不能还停在旧月份
            ok &= window.SelectedDateForTest.Year == 2027 && window.SelectedDateForTest.Month == 2;
            Log.Information("[calendar] 切月后选中日期：{Date}", window.SelectedDateForTest);

            // 8) 截图：回到 2026-10 当前日期视图
            await window.GoToForTestAsync(2026, 10);
            await window.SelectDayForTestAsync(new DateOnly(2026, 10, 21));
            await Task.Delay(400);

            if (shotDirectory is not null)
            {
                var count = SaveWindowShots(shotDirectory);
                Log.Information("[calendar] 截图 {Count} 张", count);
            }

            Log.Information("[calendar] 总结果={Result}", ok ? "通过" : "失败");

            Log.CloseAndFlush();
            Shutdown(ok ? 0 : 2);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[calendar] 异常");
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

    /// <summary>
    /// --i18n-demo：多语言回归。逐一设置四种语言，用真实的 ViewModel 拼出文案，
    /// 确认界面文本确实跟着语言变，而不是始终返回中文。
    /// </summary>
    private async void RunI18nDemo(string? shotDirectory)
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
            var board = service.EnsureDefaultBoard();
            foreach (var existing in service.GetTodos(board.Id))
            {
                service.Delete(existing.Id);
            }

            service.AddTodo(board.Id, "国际化回归条目");
            service.CreateTag("i18n");

            var settings = _host.Services.GetRequiredService<AppSettingsService>();
            var localization = _host.Services.GetRequiredService<LocalizationService>();
            var windows = _host.Services.GetRequiredService<WindowManager>();

            var ok = true;
            var previous = localization.Language;

            var expectations = new (string Key, string Url, string[] MustContain)[]
            {
                ("zh-CN", "zh", ["待办", "设置", "日历", "番茄", "农历"]),
                ("en", "en", ["To-do", "Settings", "Calendar", "Pomodoro", "Lunar"]),
                ("ja", "ja", ["未完了", "設定", "カレンダー", "ポモドーロ", "旧暦"]),
                ("ko", "ko", ["할 일", "설정", "캘린더", "뽀모도로", "음력"]),
            };

            foreach (var (key, code, mustContain) in expectations)
            {
                var info = StickyTodo.Core.Localization.AppLanguages.Resolve(key);
                localization.SetLanguage(info);

                // 1) 翻译器本身
                ok &= StickyTodo.Core.Localization.Translator.Current.Language == info.Language;

                // 2) 设置里的语言值已落库
                ok &= settings.Language == info.Key;

                // 3) 看板的视图文本（由 BoardViewModel 用翻译器拼出来）
                var boardWindow = windows.OpenBoard(board) as BoardWindow;
                await Task.Delay(500);

                var viewTexts = boardWindow?.LocalizedTextsForTest() ?? string.Empty;
                Log.Information("[i18n] {Key,-6} 看板文案：{Texts}", key, viewTexts);

                foreach (var needle in mustContain)
                {
                    var hit = viewTexts.Contains(needle, StringComparison.OrdinalIgnoreCase);
                    if (!hit)
                    {
                        Log.Warning("[i18n] {Key} 文案里缺少「{Needle}」：{Texts}", key, needle, viewTexts);
                    }

                    ok &= hit;
                }

                // 4) 窗口标题也是本地化的
                var windowTitle = boardWindow?.Title ?? string.Empty;
                Log.Information("[i18n] {Key,-6} 窗口标题={Title}", key, windowTitle);

                if (shotDirectory is not null && boardWindow is not null)
                {
                    SaveWindowShots(Path.Combine(shotDirectory, code));
                }
            }

            // 收尾：恢复中文，避免把测试语言留在真实设置里
            localization.SetLanguage(StickyTodo.Core.Localization.AppLanguages.ChineseSimplified);
            _ = previous;

            Log.Information("[i18n] 总结果={Result}", ok ? "通过" : "失败");

            Log.CloseAndFlush();
            Shutdown(ok ? 0 : 2);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[i18n] 异常");
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    /// <summary>
    /// --motion-demo：动效回归。检查窗口入场、按钮按下缩放、复选框弹跳、
    /// 行删除淡出、刷新旋转等是否真的产生了动画（读属性值而不是看截图）。
    /// </summary>
    private async void RunMotionDemo(string? shotDirectory)
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

            // 回归/演示路径也要跟随设置里的语言
            ApplyLanguageForCurrentRun();
            var service = _host.Services.GetRequiredService<TodoService>();
            var board = service.EnsureDefaultBoard();

            foreach (var existing in service.GetTodos(board.Id))
            {
                service.Delete(existing.Id);
            }

            service.AddTodo(board.Id, "第一条待办");
            service.AddTodo(board.Id, "第二条待办");
            service.AddTodo(board.Id, "第三条待办");

            var settings = _host.Services.GetRequiredService<AppSettingsService>();
            settings.RestoreSession = false;

            var ok = true;

            // 1) 系统动效开关：默认应开启（Win11 默认开 ClientAreaAnimation）
            Log.Information("[motion] Motion.Enabled={Enabled}（跟随系统 ClientAreaAnimation）", Motion.Enabled);

            var windows = _host.Services.GetRequiredService<WindowManager>();
            var boardWindow = (BoardWindow)windows.OpenBoard(board);
            MainWindow = boardWindow;

            // 2) 窗口入场：动画结束后内容必须完全不透明。
            //    注意不要断言"某个时刻还没到 1"——动画只有 180ms，
            //    采样点稍微晚一点（消息队列繁忙时）就会已经结束，属于测试自身不稳定。
            await Task.Delay(600);
            var settledOpacity = boardWindow.Content is FrameworkElement c1 ? c1.Opacity : 1;
            var hasScaleTransform = boardWindow.Content is FrameworkElement c2 && c2.RenderTransform is System.Windows.Media.ScaleTransform;
            Log.Information("[motion] 窗口入场：结束后 Opacity={Settled:0.00} 带缩放变换={HasScale}", settledOpacity, hasScaleTransform);
            ok &= settledOpacity > 0.98;
            if (Motion.Enabled)
            {
                // 入场动画确实给内容装上了 ScaleTransform（这是 FadeInWindow 的副作用）
                ok &= hasScaleTransform;
            }

            // 3) Pulse：探针元素应被装上 ScaleTransform，动画结束后回到 1
            var probe = new System.Windows.Controls.Border { Width = 40, Height = 20 };
            var host = new System.Windows.Controls.Grid();
            host.Children.Add(probe);
            host.Measure(new Size(200, 60));
            host.Arrange(new Rect(0, 0, 200, 60));

            Motion.Pulse(probe, 1.08);
            await Task.Delay(80);
            var scaleMid = probe.RenderTransform is System.Windows.Media.ScaleTransform mid ? mid.ScaleX : -1;
            await Task.Delay(400);
            var scaleEnd = probe.RenderTransform is System.Windows.Media.ScaleTransform end ? end.ScaleX : -1;
            Log.Information("[motion] Pulse：80ms 时 scale={Mid:0.000}（应 >1），结束后={End:0.000}", scaleMid, scaleEnd);
            ok &= scaleMid > 1.0;
            ok &= Math.Abs(scaleEnd - 1.0) < 0.01;

            // 4) 复选框勾选动画：Tick 应被装上 ScaleTransform 并从 0.4 放大
            var check = new System.Windows.Controls.CheckBox
            {
                Style = (Style)System.Windows.Application.Current.FindResource("CheckBox.Round"),
            };
            host.Children.Add(check);
            host.UpdateLayout();
            check.ApplyTemplate();

            check.IsChecked = true;
            await Task.Delay(120);

            var tick = check.Template?.FindName("Tick", check) as System.Windows.FrameworkElement;
            var tickScale = tick?.RenderTransform is System.Windows.Media.ScaleTransform st ? st.ScaleX : -1;
            Log.Information("[motion] 勾选后 Tick.ScaleX={Scale:0.00}（应在 0.4→1 之间或已达 1）", tickScale);
            ok &= tick is not null && tickScale > 0.4;

            // 5) 删除一行：走真实按钮，确认不抛异常、行确实少了
            var beforeRows = boardWindow.RowCountForTest();
            boardWindow.DeleteRowByIndexForTest(0);
            await Task.Delay(600);
            var afterRows = boardWindow.RowCountForTest();
            Log.Information("[motion] 删除一行：{Before} → {After}", beforeRows, afterRows);
            ok &= afterRows == beforeRows - 1;

            // 6) 完成一行：脉冲 + 数据变化
            var doneBefore = boardWindow.DoneCountForTest();
            boardWindow.CompleteRowByIndexForTest(0);
            await Task.Delay(400);
            var doneAfter = boardWindow.DoneCountForTest();
            Log.Information("[motion] 完成一行：已完成 {Before} → {After}", doneBefore, doneAfter);
            ok &= doneAfter > doneBefore;

            if (shotDirectory is not null)
            {
                await Task.Delay(300);
                var count = SaveWindowShots(shotDirectory);
                Log.Information("[motion] 截图 {Count} 张", count);
            }

            Log.Information("[motion] 总结果={Result}", ok ? "通过" : "失败");

            Log.CloseAndFlush();
            Shutdown(ok ? 0 : 2);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[motion] 异常");
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }


    /// <summary>
    /// --pomodoro-demo：番茄钟全链路回归。计时用假时钟推快，不真的等 25 分钟。
    /// 验证：开始/暂停/继续、专注结束自动进休息、4 个番茄后长休息、跳过不计完成数、今日计数落库。
    /// </summary>
    private async void RunPomodoroDemo(string? shotDirectory)
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

            // 回归/演示路径也要跟随设置里的语言
            ApplyLanguageForCurrentRun();

            var settings = _host.Services.GetRequiredService<AppSettingsService>();
            settings.PomodoroCountToday = 0;
            settings.PomodoroCountDate = DateOnly.FromDateTime(DateTime.Today);

            var engine = new Core.Pomodoro.PomodoroEngine(
                focus: TimeSpan.FromSeconds(6),
                shortBreak: TimeSpan.FromSeconds(3),
                longBreak: TimeSpan.FromSeconds(4),
                roundsPerCycle: 2);

            var viewModel = new PomodoroViewModel(settings, engine);

            // autoTick=false：回归里由脚本手动推时钟，避免和窗口自己的计时器叠加
            var window = new PomodoroWindow(viewModel, (title, text) => Log.Information("[pomodoro] 通知：{Title} - {Text}", title, text), autoTick: false);
            window.Show();
            MainWindow = window;
            await Task.Delay(300);

            string Dump() =>
                $"{viewModel.TimeText} {viewModel.PhaseText} 运行={viewModel.IsRunning} " +
                $"第{viewModel.Engine.RoundInCycle}/{viewModel.Engine.RoundsPerCycle} " +
                $"今日={viewModel.Engine.CompletedToday} 按钮={viewModel.PrimaryActionText}";

            var initial = Dump();
            Log.Information("[pomodoro] 初始：{State}", initial);
            var ok = initial.Contains("06") && initial.Contains("专注") && initial.Contains("运行=False")
                     && initial.Contains("今日=0") && initial.Contains("按钮=开始");

            // 1) 开始 → 走完一个专注段
            viewModel.Toggle();
            Log.Information("[pomodoro] 开始后：{State}", Dump());
            ok &= viewModel.IsRunning && viewModel.PrimaryActionText == "暂停";

            var finished = viewModel.Advance(TimeSpan.FromSeconds(6));
            Log.Information("[pomodoro] 专注走完：{State} 事件={Event}", Dump(), finished);
            ok &= finished == Core.Pomodoro.PomodoroPhase.Focus;
            ok &= viewModel.PhaseText == "短休息" && viewModel.Engine.CompletedToday == 1;

            // 2) 暂停后时间不再走
            viewModel.Toggle(); // 暂停
            var beforePause = viewModel.TimeText;
            viewModel.Advance(TimeSpan.FromSeconds(5));
            Log.Information("[pomodoro] 暂停中：{Before} → {After}", beforePause, viewModel.TimeText);
            ok &= beforePause == viewModel.TimeText && !viewModel.IsRunning;

            // 3) 重置回到当前段（短休息 3 秒）起点
            viewModel.Reset();
            Log.Information("[pomodoro] 重置后：{State}", Dump());
            ok &= viewModel.TimeText == Core.Pomodoro.PomodoroEngine.Format(TimeSpan.FromSeconds(3));

            // 4) 跳过当前短休息 → 回到专注，不计完成数
            var completedBefore = viewModel.Engine.CompletedToday;
            viewModel.Skip();
            Log.Information("[pomodoro] 跳过休息后：{State}", Dump());
            ok &= viewModel.PhaseText == "专注" && viewModel.Engine.CompletedToday == completedBefore;
            ok &= viewModel.Engine.RoundInCycle == 2;

            // 5) 再走完一个专注段：这一串已到第 2 个（周期=2），应进长休息
            viewModel.Toggle();
            var second = viewModel.Advance(TimeSpan.FromSeconds(6));
            Log.Information("[pomodoro] 走满一个周期：{State} 事件={Event}", Dump(), second);
            ok &= second == Core.Pomodoro.PomodoroPhase.Focus;
            ok &= viewModel.PhaseText == "长休息" && viewModel.Engine.CompletedToday == 2;
            ok &= viewModel.Engine.RoundInCycle == 1;

            // 5b) 走完长休息（4 秒）→ 回到专注；再走窗口真实的"一段结束"处理
            var third = viewModel.Advance(TimeSpan.FromSeconds(4));
            Log.Information("[pomodoro] 长休息走完：{State} 事件={Event}", Dump(), third);
            ok &= third == Core.Pomodoro.PomodoroPhase.LongBreak;
            ok &= viewModel.PhaseText == "专注";

            // 6) 今日计数已落库
            var persistedCount = settings.PomodoroCountToday;
            Log.Information("[pomodoro] 落库计数={Count}", persistedCount);
            ok &= persistedCount == viewModel.Engine.CompletedToday;
            ok &= settings.PomodoroCountDate == DateOnly.FromDateTime(DateTime.Today);

            // 7) 统一同步一次给界面 + 截图
            viewModel.Sync();
            if (shotDirectory is not null)
            {
                await Task.Delay(400);
                var count = SaveWindowShots(shotDirectory);
                Log.Information("[pomodoro] 截图 {Count} 张", count);
            }

            // 清理，别把演示计数留在真实设置里
            settings.PomodoroCountToday = 0;

            Log.Information("[pomodoro] 总结果={Result}", ok ? "通过" : "失败");

            Log.CloseAndFlush();
            Shutdown(ok ? 0 : 2);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[pomodoro] 异常");
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    /// <summary>
    /// --token-demo：Token 便签全链路回归。用假数据源（不联网），验证
    /// 首屏取数、切范围（含粒度变化）、切间隔、手动刷新、点击趋势点看明细。
    /// </summary>
    private async void RunTokenDemo(string? shotDirectory, bool live)
    {
        AppPaths.EnsureCreated();
        ConfigureLogging();
        InstallGlobalExceptionHandlers();

        // 默认用假数据源，保证回归不依赖网络；--live 才打真实接口
        var demoProvider = live ? null : new DemoTokenStatsProvider();
        TokenProviderOverride = demoProvider;

        try
        {
            _host = BuildHost();
            await _host.StartAsync();

            var repository = _host.Services.GetRequiredService<ITodoRepository>();
            repository.Initialize();

            // 回归/演示路径也要跟随设置里的语言
            ApplyLanguageForCurrentRun();
            var service = _host.Services.GetRequiredService<TodoService>();

            // 只留一个 Token 看板，避免默认待办看板混进来
            var board = service.CreateBoard("Token 用量", BoardKind.Token);
            var windows = _host.Services.GetRequiredService<WindowManager>();
            var window = (TokenBoardWindow)windows.OpenBoard(board);
            MainWindow = window;

            await Task.Delay(1200);
            var initial = window.DescribeForTest();
            Log.Information("[token] 首屏：{State}", initial);
            var ok = initial.Contains("总数=")
                     && !initial.Contains("总数=0")
                     && initial.Contains("间隔=15s")
                     && initial.Contains("粒度=hour")
                     && window.FetchCount >= 1;

            // 0) 小时粒度（今天）不显示热力图——之前正是这里抛 FormatException 把界面炸了
            Log.Information("[token] 首屏热力图可见={Visible}", window.HeatMapVisibleForTest);
            ok &= !window.HeatMapVisibleForTest;

            // 1) 切到近 7 天：粒度应变 day，且要重新取数
            var before = window.FetchCount;
            var msWeek = await window.SelectRangeForTestAsync(7);
            var week = window.DescribeForTest();
            Log.Information("[token] 切到近7天：耗时 {Ms}ms，{State}", msWeek, week);
            ok &= week.Contains("范围=7") && week.Contains("粒度=day") && window.FetchCount > before;

            // 天粒度应显示热力图
            Log.Information("[token] 近7天热力图可见={Visible}", window.HeatMapVisibleForTest);
            ok &= window.HeatMapVisibleForTest;

            // 2) 切回今天：粒度回 hour
            var msToday = await window.SelectRangeForTestAsync(0);
            var today = window.DescribeForTest();
            Log.Information("[token] 切回今天：耗时 {Ms}ms，{State}", msToday, today);
            ok &= today.Contains("范围=0") && today.Contains("粒度=hour");

            // 切回小时粒度应重新隐藏热力图
            Log.Information("[token] 今天热力图可见={Visible}", window.HeatMapVisibleForTest);
            ok &= !window.HeatMapVisibleForTest;

            // 2b) 近 30 天（会跨到 v1 表，是最慢的一档）
            var ms30 = await window.SelectRangeForTestAsync(30);
            Log.Information("[token] 切到近30天：耗时 {Ms}ms，{State}", ms30, window.DescribeForTest());
            Log.Information("[token] 近30天热力图可见={Visible}", window.HeatMapVisibleForTest);
            ok &= window.HeatMapVisibleForTest;

            // 各档位耗时都要在可接受范围内（切换不该有明显卡顿）
            var slowest = Math.Max(msWeek, Math.Max(msToday, ms30));
            Log.Information("[token] 最慢一档 {Ms}ms（阈值 3000ms）", slowest);
            ok &= slowest < 3000;

            // 2b2) 再切一次近 30 天：历史日期已按天缓存，这次只重算今天，应该很快
            var ms30Again = await window.SelectRangeForTestAsync(30);
            Log.Information("[token] 再次切到近30天（历史已缓存）：{Ms}ms", ms30Again);
            ok &= ms30Again < 400;

            // 2c) 切回看过的范围应"秒开"：有缓存就直接铺上，不等数据库
            var msCached = window.SelectRangeImmediate(7);
            await Task.Delay(200);
            Log.Information("[token] 切回近7天（有缓存）：{Ms}ms 出数据，界面={State}",
                msCached, window.DescribeForTest());
            ok &= msCached < 100 && window.DescribeForTest().Contains("范围=7");

            // 回到今天，后面的断言按小时粒度走
            await window.SelectRangeForTestAsync(0);

            // 3) 手动刷新
            before = window.FetchCount;
            await window.RefreshForTestAsync();
            Log.Information("[token] 手动刷新：取数 +{Calls}", window.FetchCount - before);
            ok &= window.FetchCount > before;

            // 4) 点趋势图的点看明细（取当前序列的最后一个点，真实数据的点数随当天时间变化）
            var pickIndex = Math.Max(0, window.PointCountForTest - 1);
            window.PickPointForTest(pickIndex);
            await Task.Delay(150);
            var picked = window.DescribeForTest();
            Log.Information("[token] 点击趋势点 #{Index}：{State}", pickIndex, picked);
            ok &= picked.Contains("明细=") && !picked.Contains("明细=无");

            // 4a) 悬停 = 点击：把指针"移到"图上的另一个点，选中列与明细都要跟着换
            if (window.PointCountForTest > 1 && pickIndex != 0)
            {
                window.HoverChartForTest(0);
                await Task.Delay(150);
                var hovered = window.DescribeForTest();
                Log.Information("[token] 悬停趋势点 #0：{State}", hovered);
                ok &= window.SelectedIndexForTest == 0
                      && hovered.Contains("明细=") && !hovered.Contains("明细=无");
            }
            else
            {
                Log.Information("[token] 趋势点不足 2 个，跳过悬停断言");
            }

            // 4b) 再刷新一次，明细应该还在（自动刷新不该把用户点开的明细冲掉）
            await window.RefreshForTestAsync();
            var afterRefresh = window.DescribeForTest();
            Log.Information("[token] 刷新后明细保留：{State}", afterRefresh);
            ok &= afterRefresh.Contains("明细=") && !afterRefresh.Contains("明细=无");

            // 5) 切间隔 5s / 30s
            window.SelectIntervalForTest(5);
            var five = window.DescribeForTest();
            window.SelectIntervalForTest(30);
            var thirty = window.DescribeForTest();
            Log.Information("[token] 间隔切换：{Five} / {Thirty}", five, thirty);
            ok &= five.Contains("间隔=5s") && thirty.Contains("间隔=30s");

            // 6) 自动刷新：把间隔设成 5s，等一个周期，确认计时器真的在跑
            before = window.FetchCount;
            window.SelectIntervalForTest(5);
            await Task.Delay(6000);
            var auto = window.FetchCount - before;
            Log.Information("[token] 自动刷新：5.5s 内又取数 {Calls} 次", auto);
            ok &= auto >= 1;

            // 恢复到默认间隔，免得把演示数据留成 5s
            window.SelectIntervalForTest(15);

            // 复制摘要（WPF 版补齐，与 Avalonia 版对齐）：
            // 文本内容可确定性断言；剪贴板是否可用取决于环境，只记日志不判失败。
            var copyText = window.CopyTextForTest();
            var copyStatus = window.CopyForTest();
            Log.Information("[token] 复制摘要：{Len} 字，提示='{Status}'", copyText.Length, copyStatus);
            ok &= copyText.Length > 0 && copyText.Contains('\n');

            // 多 Agent 客户端切换：下拉已填充、切到某客户端后数据口径变化、
            // 切回"全部"恢复。token-demo 的 provider 是单源 Demo（无多源聚合），
            // 这里只验证 UI 集合与选择逻辑本身；多源合并的口径由单元测试覆盖。
            var hasPicker = window.AgentsCountForTest > 0;
            Log.Information("[token] 客户端下拉：{Count} 项（demo provider 时为 0）", window.AgentsCountForTest);
            ok &= !hasPicker || window.CopyTextForTest().Length > 0;

            if (shotDirectory is not null)
            {
                await Task.Delay(300);
                var count = SaveWindowShots(shotDirectory);
                Log.Information("[token] 截图 {Count} 张", count);
            }

            Log.Information("[token] 总结果={Result}", ok ? "通过" : "失败");

            Log.CloseAndFlush();
            Shutdown(ok ? 0 : 2);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[token] 异常");
            Log.CloseAndFlush();
            Shutdown(1);
        }
    }

    /// <summary>
    /// --clock-demo：桌面时钟 × 天气 × 日历的联动回归。
    /// 种两条今天的安排（一条已完成）→ 验证农历/今日日程摘要 →
    /// 点日程/点天气都要能自动建出对应看板并打开。
    /// </summary>
    private async void RunClockDemo()
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

            ApplyLanguageForCurrentRun();

            var service = _host.Services.GetRequiredService<TodoService>();
            var windows = _host.Services.GetRequiredService<WindowManager>();

            var board = service.EnsureDefaultBoard();
            var today = DateTimeOffset.Now.Date;

            var morning = service.AddTodo(board.Id, "晨会纪要");
            service.SetDueAt(morning.Id, today.AddHours(9));
            service.SetDone(morning.Id, true);

            var afternoon = service.AddTodo(board.Id, "交周报");
            service.SetDueAt(afternoon.Id, today.AddHours(14));

            // 没有目标日期的待办不该进"今日日程"
            service.AddTodo(board.Id, "买牛奶");

            var clockBoard = service.GetBoards().FirstOrDefault(b => b.Kind == BoardKind.DesktopClock)
                ?? service.CreateBoard("桌面时钟", BoardKind.DesktopClock);

            var window = (DesktopClockWindow)windows.OpenBoard(clockBoard);
            MainWindow = window;

            await Task.Delay(1500);
            var state = window.DescribeForTest();
            Log.Information("[clock] 首屏：{State}", state);

            var ok = state.Contains("农历=农历")
                     && state.Contains("日程=今日 ")
                     && state.Contains("14:00 交周报")
                     && state.Contains("已完成 1/");

            // 改默认城市：时钟要在几秒内换过来（不用等 30 分钟）；同时验证贴边打开/再点关闭
            var settingsService = _host.Services.GetRequiredService<AppSettingsService>();
            var targetCity = settingsService.WeatherCities[^1];
            settingsService.WeatherDefaultCity = targetCity;
            await Task.Delay(7000);
            Log.Information("[clock] 默认城市改成 {City} 后，时钟取数城市={Now}",
                targetCity, window.LoadedCityForTest);
            ok &= string.Equals(window.LoadedCityForTest, targetCity, StringComparison.Ordinal);

            // 点"今日日程"：贴着时钟打开
            window.OpenCalendarForTest();
            await Task.Delay(900);

            var calendar = service.GetBoards().FirstOrDefault(b => b.Kind == BoardKind.Calendar);
            var calendarOpened = calendar is not null && windows.IsBoardOpen(calendar.Id);
            var calendarWindow = Application.Current.Windows.OfType<CalendarWindow>().FirstOrDefault();

            // 贴在时钟左边（左边放不下才会放到右边）：两者不能压在一起
            var besideClock = calendarWindow is not null
                              && (calendarWindow.Left + calendarWindow.ActualWidth <= window.Left + 1
                                  || calendarWindow.Left >= window.Left + window.ActualWidth - 1);

            Log.Information("[clock] 点日程后：日历看板={Board} 打开={Opened} 位置={Pos} 贴边={Beside}",
                calendar?.Name ?? "(无)", calendarOpened,
                calendarWindow is null ? "(无窗口)" : $"{calendarWindow.Left:0},{calendarWindow.Top:0}",
                besideClock);
            ok &= calendarOpened && besideClock;

            // 再点一次：应该关掉
            window.OpenCalendarForTest();
            await Task.Delay(700);
            var calendarClosed = calendar is null || !windows.IsBoardOpen(calendar.Id);
            Log.Information("[clock] 再点一次日程：已关闭={Closed}", calendarClosed);
            ok &= calendarClosed;

            // 点"天气"：同样贴边打开，再点关闭
            window.OpenWeatherForTest();
            await Task.Delay(900);

            var weather = service.GetBoards().FirstOrDefault(b => b.Kind == BoardKind.Weather);
            var weatherOpened = weather is not null && windows.IsBoardOpen(weather.Id);
            Log.Information("[clock] 点天气后：天气看板={Board} 打开={Opened}",
                weather?.Name ?? "(无)", weatherOpened);
            ok &= weatherOpened;

            window.OpenWeatherForTest();
            await Task.Delay(700);
            var weatherClosed = weather is null || !windows.IsBoardOpen(weather.Id);
            Log.Information("[clock] 再点一次天气：已关闭={Closed}", weatherClosed);
            ok &= weatherClosed;

            // 点各区域 → 对应看板贴边打开；再点关闭
            async Task CheckAreaAsync(string tag, Func<Board?> resolve, string label)
            {
                window.ClickAreaForTest(tag);
                await Task.Delay(800);
                var board = resolve();
                var opened = board is not null && windows.IsBoardOpen(board.Id);
                window.ClickAreaForTest(tag);
                await Task.Delay(600);
                var closed = board is null || !windows.IsBoardOpen(board.Id);
                Log.Information("[clock] 点{Label}：打开={Opened} 关闭={Closed}", label, opened, closed);
                ok &= opened && closed;
            }

            await CheckAreaAsync("todo", () => service.EnsureDefaultBoard(), "待办");
            await CheckAreaAsync("token", () => service.GetBoards().FirstOrDefault(b => b.Kind == BoardKind.Token), "token");
            await CheckAreaAsync("clock", () => service.GetBoards().FirstOrDefault(b => b.Kind == BoardKind.WorldClock), "世界时钟");

            // 天气缓存：缓存里有的城市，打开看板要直接用（用一个本来查不到的名字验证，不依赖网络）
            var weatherCache = _host.Services.GetRequiredService<WeatherCache>();
            const string cachedCity = "缓存测试城";
            var citiesNow = settingsService.WeatherCities.ToList();
            if (!citiesNow.Contains(cachedCity))
            {
                citiesNow.Add(cachedCity);
                settingsService.WeatherCities = citiesNow;
            }

            weatherCache.Set(cachedCity, new WeatherReport
            {
                CityName = cachedCity,
                Days = [new WeatherDayInfo { DayLabel = "今天", Text = "缓存命中", TempMax = 12 }],
            }, DateTime.Now.AddHours(-2));

            var cachedVm = new WeatherViewModel(
                _host.Services.GetRequiredService<WeatherService>(),
                settingsService,
                weatherCache);
            cachedVm.SelectCity(cachedCity);
            await cachedVm.LoadAsync();

            var cacheHit = !cachedVm.IsBusyOverlayVisible && cachedVm.CurrentText == "缓存命中";
            Log.Information("[clock] 天气缓存：城市={City} 文本={Text} 遮盖={Busy} 命中={Hit}",
                cachedVm.CurrentCity, cachedVm.CurrentText, cachedVm.IsBusyOverlayVisible, cacheHit);
            ok &= cacheHit;

            Log.Information("[clock] 总结果={Result}", ok ? "通过" : "失败");
            Log.CloseAndFlush();
            Shutdown(ok ? 0 : 2);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[clock] 异常");
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

            // 回归/演示路径也要跟随设置里的语言
            ApplyLanguageForCurrentRun();
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

            // 回归/演示路径也要跟随设置里的语言
            ApplyLanguageForCurrentRun();
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

            // 回归/演示路径也要跟随设置里的语言
            ApplyLanguageForCurrentRun();
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

            // 回归/演示路径也要跟随设置里的语言
            ApplyLanguageForCurrentRun();
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

            // 回归/演示路径也要跟随设置里的语言
            ApplyLanguageForCurrentRun();
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

            // 回归/演示路径也要跟随设置里的语言
            ApplyLanguageForCurrentRun();
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

            // 回归/演示路径也要跟随设置里的语言
            ApplyLanguageForCurrentRun();
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

            // 回归/演示路径也要跟随设置里的语言
            ApplyLanguageForCurrentRun();
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

            // 回归/演示路径也要跟随设置里的语言
            ApplyLanguageForCurrentRun();
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

            // 回归/演示路径也要跟随设置里的语言
            ApplyLanguageForCurrentRun();
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

            // 回归/演示路径也要跟随设置里的语言
            ApplyLanguageForCurrentRun();
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

            // 回归/演示路径也要跟随设置里的语言
            ApplyLanguageForCurrentRun();
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

            // 回归/演示路径也要跟随设置里的语言
            ApplyLanguageForCurrentRun();
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

            // 回归/演示路径也要跟随设置里的语言
            ApplyLanguageForCurrentRun();
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

            // 回归/演示路径也要跟随设置里的语言
            ApplyLanguageForCurrentRun();
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


    /// <summary>打开工具型窗口（天气 / 世界时钟 / 桌面时钟）。单例。</summary>
    /// <summary>取 --create-tool-demo 后面跟的值（没有该参数时返回 null）。</summary>
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

    /// <summary>ShowAndActivate 兼容方法。</summary>
    private static void ShowAndActivateCompat(Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

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
    /// 退出前固化会话（谁要调 Shutdown 就先调这个）。
    /// WPF 的 Shutdown 会先逐个关闭窗口、之后才走 OnExit，
    /// 那时窗口已经没了，只能记成空列表。
    /// </summary>
    public void BeginShutdownForExit()
    {
        try
        {
            _host?.Services.GetService<WindowManager>()?.BeginShutdown();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "固化会话失败");
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