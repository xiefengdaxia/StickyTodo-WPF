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
    private MemoryTrimmer? _memoryTrimmer;

    /// <summary>
    /// 回归演示里替换掉真实的 HTTP 数据源（见 <c>--token-demo</c>）。
    /// 正式运行时为 null，走真实的 <see cref="Infrastructure.Token.OpenCodeTokenStatsProvider"/>。
    /// </summary>
    private static Core.Abstractions.ITokenStatsProvider? TokenProviderOverride { get; set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 启动耗时打点：日志里能看到每个阶段各花了多久（用户报「启动慢」时先看这个）
        var startupWatch = System.Diagnostics.Stopwatch.StartNew();

        // 渲染模式必须在任何窗口出现之前定好。
        // 默认走软件渲染：这套 UI 全是不透明底 + 圆角 + 投影的自绘窗口，
        // 硬件加速在这里没有收益，反而多占一份 D3D 表面（实测省 ~80 MB 私有内存）。
        // 想知道硬件加速的对比，可以用 --hardware-render 启动。
        if (!e.Args.Contains("--hardware-render", StringComparer.OrdinalIgnoreCase))
        {
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        }

        // 所有命令行演示/回归入口都在 App.Demos.cs
        if (TryRunDemo(e.Args))
        {
            return;
        }

        _mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            ConfirmWindow.Notify(null, "StickyTodo", "StickyTodo 已经在运行。");
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
            Log.Information("[startup] 依赖注入就绪 {Ms} ms", startupWatch.ElapsedMilliseconds);

            await _host.StartAsync();
            Log.Information("[startup] 后台宿主已启动 {Ms} ms", startupWatch.ElapsedMilliseconds);

            var repository = _host.Services.GetRequiredService<ITodoRepository>();
            repository.Initialize();
            Log.Information("[startup] 数据库就绪 {Ms} ms", startupWatch.ElapsedMilliseconds);

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

            var launchedByAutoStart = e.Args.Contains("--autostart", StringComparer.OrdinalIgnoreCase);

            // 先把上次的看板/时钟恢复出来，再去做托盘、热键这些「后台设施」：
            // 用户最关心的是窗口什么时候出现，托盘晚半秒没人会注意。
            var restored = 0;
            if (!isDemo)
            {
                restored = _host.Services.GetRequiredService<WindowManager>().RestoreSession();
                if (restored > 0)
                {
                    Log.Information("已恢复上次会话的 {Count} 个窗口", restored);
                }
            }

            // 只有「没有可恢复的窗口」时才弹主窗体。
            // 上次留着便签/时钟，说明用户要的就是那套桌面；再弹管理面板只会挡视线。
            var showManager = isDemo || restored == 0;
            if (showManager)
            {
                manager.Show();
            }

            // 开机自启：把恢复出来的看板/时钟压到桌面底层，别一开机就盖住别的程序。
            // 等窗口真正显示出来再动 Z 序，否则句柄还没建好。
            if (launchedByAutoStart && restored > 0 && (Settings?.StartupDesktopLevel ?? true))
            {
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    new Action(() =>
                    {
                        try
                        {
                            var count = _host.Services.GetRequiredService<WindowManager>().SendBoardsToDesktopLevel();
                            Log.Information("[startup] 开机自启：{Count} 个窗口已沉到桌面底层", count);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "[startup] 沉到桌面底层失败");
                        }
                    }));
            }

            Log.Information("[startup] 恢复窗口={Restored} 显示主窗体={ShowManager}（开机自启={AutoStart}）用时 {Ms} ms",
                restored, showManager, launchedByAutoStart, startupWatch.ElapsedMilliseconds);

            var tray = _host.Services.GetRequiredService<TrayService>();

            // 先刷掉上次被强杀进程留下的托盘图标，再挂自己的图标
            TrayRefresher.Refresh();
            tray.Start();

            StartReminderPump(tray);

            // 托盘菜单回归：--tray-menu-demo 启动 8 秒后弹一次菜单（保持打开直到点击别处），供外部截图
            if (e.Args.Contains("--tray-menu-demo", StringComparer.OrdinalIgnoreCase))
            {
                var menuTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                menuTimer.Tick += (_, _) =>
                {
                    menuTimer.Stop();
                    ShowTrayMenu();
                };
                menuTimer.Start();
            }

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

            // --create-tool-demo <weather|worldclock|desktopclock>：打开一个工具型看板（进「我的看板」列表）。
            // 已有同类型的就复用，否则每次启动都会多建一个看板。
            // 必须放在 RestoreSession 之后：先开会话记录会被覆盖，上次的看板就丢了。
            var toolArg = ToolDemoArgument(e.Args);
            var toolKind = toolArg?.Trim().ToLowerInvariant() switch
            {
                "weather" => BoardKind.Weather,
                "worldclock" => BoardKind.WorldClock,
                "desktopclock" => BoardKind.DesktopClock,
                "networkspeed" => BoardKind.NetworkSpeed,
                "performance" => BoardKind.Performance,
                "calendar" => BoardKind.Calendar,
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
                            BoardKind.NetworkSpeed => "网速",
                            BoardKind.Performance => "性能",
                            BoardKind.Calendar => "日历",
                            _ => "桌面时钟",
                        }, kind);

                _host.Services.GetRequiredService<WindowManager>().OpenBoard(board);
            }

#if DEBUG
            // --demo 会往真实库里写演示数据，只随 Debug 构建保留（RunDemo 在 App.Demos.cs）
            if (isDemo)
            {
                RunDemo(shotDirectory, e.Args.Contains("--reminder-demo", StringComparer.OrdinalIgnoreCase));
                _ = manager.RefreshAsync();
            }
#endif

            Log.Information("StickyTodo 启动完成，数据目录 {Directory}，应用内 {Ms} ms，进程起 {Total} ms（含运行时初始化）",
                AppPaths.DataDirectory, startupWatch.ElapsedMilliseconds, SinceProcessStartMs());

            // 空闲时把工作集还给系统（详见 MemoryTrimmer 的注释）
            _memoryTrimmer = MemoryTrimmer.Start();

            // 护眼：滤镜盖回来 / 豆沙绿校色恢复
            _host!.Services.GetRequiredService<EyeCareFilter>().ApplyFromSettings();
            _host!.Services.GetRequiredService<EyeGammaService>().ApplyFromSettings();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "启动失败");
            ConfirmWindow.Notify(null, "StickyTodo",
                $"启动失败：{ex.Message}\n\n日志位置：{AppPaths.LogDirectory}");
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
                ConfirmWindow.Notify(
                    null,
                    "StickyTodo",
                    $"操作失败：{args.Exception.Message}\n\n详细信息已写入日志：{AppPaths.LogDirectory}");
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
            // 文件 sink 是逐条同步落盘的，Debug 级别里混着拖拽这类高频事件，
            // Release 降到 Information：拖动窗口时不再在 UI 线程上持续写磁盘
#if DEBUG
            .MinimumLevel.Debug()
#else
            .MinimumLevel.Information()
#endif
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
                services.AddSingleton<PinManager>();
                services.AddSingleton<EyeCareFilter>();
                services.AddSingleton<EyeGammaService>();
                services.AddSingleton<AutoStartService>();
                services.AddSingleton<Weather.WeatherService>();
            services.AddSingleton<Weather.WeatherCache>();
                // 检查更新：仓库地址来自 git remote（见 UpdateRepository.StickyTodo）
                services.AddSingleton(new StickyTodo.Updates.GitHubUpdateChecker());
                // 世界时钟/桌面时钟的 VM 由 WindowManager 直接 new（每窗口一份），不走 DI
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

    /// <summary>
    /// 从进程启动到现在过了多久（毫秒）。用来把「运行时初始化 + 单文件解压」也算进启动耗时，
    /// 日志里的 [startup] 打点只从 OnStartup 开始计时。
    /// </summary>
    private static long SinceProcessStartMs()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return (long)(DateTime.Now - process.StartTime).TotalMilliseconds;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>当前进程的设置服务（工具窗/贴图窗没有 DI 时用它取透明度）。</summary>
    public static AppSettingsService? Settings =>
        (Current as App)?._host?.Services.GetService<AppSettingsService>();

    /// <summary>按存下来的值给工具窗设整窗透明度（没有单独设置就跟随全局）。</summary>
    public static void ApplyToolWindowOpacity(Window window)
    {
        if (Settings is { } settings)
        {
            ToolWindowAppearance.Apply(window, settings);
        }
    }

    /// <summary>天气窗口的外观菜单（皮肤 + 透明度），天气/世界时钟窗口共用。</summary>
    public void ShowWeatherAppearanceMenu(Window window, FrameworkElement? target)
    {
        var skin = _host!.Services.GetRequiredService<SkinService>();
        var settings = _host.Services.GetRequiredService<AppSettingsService>();

        // 每个工具窗用自己的 key 存透明度（跟着窗口类型走，不跟具体实例）
        var key = ToolWindowAppearance.KeyFor(window);
        var menu = Views.WeatherMenuBuilder.Build(skin, settings, window, key);

        if (target is not null)
        {
            menu.PlacementTarget = target;
        }

        menu.IsOpen = true;
    }

    /// <summary>
    /// 托盘右键菜单（桌面时钟里点右键也会调这里，等于"在时钟上右键 = 托盘菜单"）。
    /// </summary>
    public void ShowTrayMenu()
    {
        try
        {
            _host?.Services.GetRequiredService<TrayService>().ShowMenuAtCursor();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "弹出托盘菜单失败");
        }
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
        var pins = _host.Services.GetRequiredService<PinManager>();

        var registered = hotKeys.Attach(
            host,
            onNewTodo: windows.OpenDefaultBoardForNewTodo,
            onToggleBoards: windows.ToggleBoardsVisibility,
            onLockActive: () => windows.ToggleActiveLock(),
            onSnip: () => pins.CaptureAndPin(),
            onPinAgain: () => pins.PinLast());

        Log.Information(
            "全局快捷键已注册 {Count}/5：{New} 新建待办 / {Toggle} 显示隐藏 / {Lock} 锁定当前看板 / {Snip} 截图 / {Pin} 再贴一张",
            registered,
            HotKeyService.NewTodoHotKey, HotKeyService.ToggleBoardsHotKey, HotKeyService.LockActiveHotKey,
            HotKeyService.SnipHotKey, HotKeyService.PinAgainHotKey);
    }

#if DEBUG
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
#endif

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
                _host.Services.GetService<EyeCareFilter>()?.Dispose();
                _host.Services.GetService<EyeGammaService>()?.Dispose();
                _memoryTrimmer?.Dispose();
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

