using Avalonia.Controls;
using Avalonia.Threading;
using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Entitlements;
using StickyTodo.Core.Pomodoro;
using StickyTodo.Core.Services;
using StickyTodo.Infrastructure;
using StickyTodo.Infrastructure.Persistence;
using StickyTodo.Infrastructure.Token;
using StickyTodo.Notes.ViewModels;
using StickyTodo.Notes.Views;

namespace StickyTodo.Notes.Services;

/// <summary>
/// 组合根：手工装配所有服务与常用窗口。
///
/// 刻意不用依赖注入容器：Win7 版要的是"启动快、依赖少、启动路径一眼看穿"，
/// 这个应用的服务图也很小，手写装配反而更清楚。
/// </summary>
public sealed class AppServices : IDisposable
{
    public AppServices(bool useDemoTokenProvider = false)
    {
        Entitlement = new PermanentFreeEntitlement();
        Repository = new SqliteTodoRepository(AppPaths.DatabasePath);

        // 必须先建库/迁移，后面读设置才查得到 setting 表
        Repository.Initialize();

        Todos = new TodoService(Repository, Entitlement);
        Settings = new AppSettingsService(Repository);
        Skin = new SkinService(Settings);
        Localization = new LocalizationService(Settings);
        AutoStart = new AutoStartService();

        TokenProvider = useDemoTokenProvider
            ? new DemoTokenStatsProvider()
            : new OpenCodeTokenStatsProvider(
                string.IsNullOrWhiteSpace(Settings.TokenDatabasePath) ? null : Settings.TokenDatabasePath);

        PomodoroEngine = new PomodoroEngine();
        Windows = new WindowManager(Todos, Settings, Skin, TokenProvider);
        HotKeys = new HotKeyService();
        Reminders = new ReminderPump(Todos);
        Tray = new TrayService(Todos, Windows, this);

        ManagerViewModel = new ManagerViewModel(Todos, Entitlement);
        SettingsViewModel = new SettingsViewModel(Skin, Settings, Todos, Entitlement, Windows, AutoStart, Localization);
        PomodoroViewModel = new PomodoroViewModel(Settings, PomodoroEngine);

        Manager = new ManagerWindow(ManagerViewModel);
        SettingsWindow = new SettingsWindow(SettingsViewModel);
        AttachManagerAutoRefresh();
    }

    public IEntitlementService Entitlement { get; }

    public ITodoRepository Repository { get; }

    public TodoService Todos { get; }

    public AppSettingsService Settings { get; }

    public SkinService Skin { get; }

    public LocalizationService Localization { get; }

    public AutoStartService AutoStart { get; }

    public ITokenStatsProvider TokenProvider { get; }

    public PomodoroEngine PomodoroEngine { get; }

    public WindowManager Windows { get; }

    public HotKeyService HotKeys { get; }

    public ReminderPump Reminders { get; }

    public TrayService Tray { get; }

    public ManagerViewModel ManagerViewModel { get; }

    public SettingsViewModel SettingsViewModel { get; }

    public PomodoroViewModel PomodoroViewModel { get; }

    public ManagerWindow Manager { get; }

    public SettingsWindow SettingsWindow { get; }

    private PomodoroWindow? _pomodoroWindow;

    // ---------------- 窗口 ----------------

    public void ShowManager()
    {
        Manager.ShowAndActivate();
        _ = Manager.RefreshAsync();
    }

    /// <summary>
    /// 看板增删（含托盘 / 启动参数 / 工具看板这些不走管理面板按钮的路径）之后，
    /// 「我的看板」列表自动刷新。与 WPF 版 ManagerWindow 订阅 BoardsChanged 的行为对齐。
    /// </summary>
    private void AttachManagerAutoRefresh()
    {
        Windows.BoardsChanged += (_, _) => _ = Manager.RefreshAsync();
    }

    public void ShowSettings()
    {
        SettingsWindow.ShowAndActivate();
    }

    public void ShowPomodoro()
    {
        if (_pomodoroWindow is { IsVisible: true })
        {
            _pomodoroWindow.ShowAndActivate();
            return;
        }

        _pomodoroWindow = new PomodoroWindow(PomodoroViewModel, Tray.Notify);
        _pomodoroWindow.Closed += (_, _) => _pomodoroWindow = null;
        _pomodoroWindow.Show();
    }

    public void OpenBoard(Core.Entities.Board board)
    {
        Windows.OpenBoard(board);
        _ = Manager.RefreshAsync();
    }

    public void CloseBoard(string boardId)
    {
        Windows.CloseBoard(boardId);
        _ = Manager.RefreshAsync();
    }

    // ---------------- 工具型窗口（天气 / 世界时钟 / 桌面时钟） ----------------

    private Weather.WeatherService? _weatherService;

    /// <summary>天气服务（惰性创建；只有打开天气/桌面时钟才会用到网络）。</summary>
    public Weather.WeatherService Weather => _weatherService ??= new Weather.WeatherService();

    /// <summary>
    /// 打开工具型面板。走「正式看板」路径：没有就建一条 Board 记录，
    /// 这样它和待办看板一样出现在「我的看板」列表里，也参与会话恢复。
    /// 单例语义：已开着就只激活。
    /// </summary>
    public void OpenToolWindow(ToolWindowKind kind)
    {
        var (boardKind, defaultName) = kind switch
        {
            ToolWindowKind.Weather => (Core.Entities.BoardKind.Weather, "天气"),
            ToolWindowKind.WorldClock => (Core.Entities.BoardKind.WorldClock, "世界时钟"),
            ToolWindowKind.DesktopClock => (Core.Entities.BoardKind.DesktopClock, "桌面时钟"),
            _ => (Core.Entities.BoardKind.Todo, string.Empty),
        };

        if (boardKind == Core.Entities.BoardKind.Todo)
        {
            return;
        }

        var board = Todos.GetBoards().FirstOrDefault(b => b.Kind == boardKind)
            ?? Todos.CreateBoard(defaultName, boardKind);

        Windows.OpenBoard(board);
    }

    /// <summary>退出：先固化会话，再关窗，最后清理托盘与快捷键。</summary>
    public void ExitApplication()
    {
        Log.Info("[exit] 开始退出");

        Windows.BeginShutdown();
        Windows.CloseAll();

        // 托盘图标必须在 UI 线程上销毁：Avalonia 的 TrayIcon 内部持有
        // Windows 通知区域句柄。同时 Dispose 只负责发出 Shell_NotifyIcon(NIM.DELETE)，
        // 这条消息是异步投递给 explorer.exe 的——如果紧接着就 Environment.Exit，
        // explorer 还没处理完，图标就永久残留在托盘里（下次启动出现两个）。
        RunOnUiThread(Dispose);

        // 给 explorer 一点时间真正删除图标，再结束进程
        Log.Info("[exit] 已发出托盘删除请求，等待 shell 处理");
        Thread.Sleep(TrayCleanupDelayMs);

        // 优先走 Avalonia 自己的 shutdown：让所有窗口按正常生命周期关闭，
        // 托盘宿主窗口也会被正确销毁。只有拿不到 lifetime 时才硬退出。
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            Log.Info("[exit] 调用 Shutdown()");
            EnqueueShutdown(desktop);
        }
        else
        {
            Log.Info("[exit] 没有 lifetime，直接结束进程");
            Environment.Exit(0);
        }
    }

    /// <summary>
    /// 在 UI 线程上延后一拍再 Shutdown。
    /// 直接在退出按钮的点击处理里 Shutdown，会在事件冒泡过程中销毁窗口，
    /// Avalonia 有时会抛 ObjectDisposedException 并跳过清理。
    /// </summary>
    private static void EnqueueShutdown(Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                Log.Info("[exit] 执行 Shutdown");
                desktop.Shutdown(0);
                Log.Info("[exit] Shutdown 完成");

                // Shutdown 之后事件循环会退出；兜底再等一下，确保 explorer 收到删除请求
                Thread.Sleep(TrayCleanupDelayMs);
            }
            catch (Exception ex)
            {
                Log.Warn("[exit] Shutdown 异常，改为强制退出：" + ex.Message);
            }

            Log.Info("[exit] 结束进程");
            Environment.Exit(0);
        }, DispatcherPriority.Background);
    }

    /// <summary>托盘删除后等待 shell 处理的时间（毫秒）。</summary>
    private const int TrayCleanupDelayMs = 300;

    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Invoke(action);
        }
    }

    /// <summary>App 生命周期结束时的清理（不含 Environment.Exit）。</summary>
    public void CleanupOnExit() => Dispose();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            Tray.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("[exit] 关闭托盘失败：" + ex.Message);
        }

        try
        {
            HotKeys.Dispose();
            Reminders.Dispose();
        }
        catch (Exception)
        {
            // 退出阶段忽略清理异常
        }
    }

    private bool _disposed;
}
