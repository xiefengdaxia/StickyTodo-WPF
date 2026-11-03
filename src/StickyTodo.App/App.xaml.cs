using System.IO;
using System.Threading;
using System.Windows;
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

    /// <summary>
    /// 回归演示里替换掉真实的 HTTP 数据源（见 <c>--token-demo</c>）。
    /// 正式运行时为 null，走真实的 <see cref="Infrastructure.Token.OpenCodeTokenStatsProvider"/>。
    /// </summary>
    private static Core.Abstractions.ITokenStatsProvider? TokenProviderOverride { get; set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
                // 检查更新：仓库地址来自 git remote（见 UpdateRepository.StickyTodo）
                services.AddSingleton(new StickyTodo.Updates.GitHubUpdateChecker());
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

