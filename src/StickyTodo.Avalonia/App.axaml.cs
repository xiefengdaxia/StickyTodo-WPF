using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using StickyTodo.Core.Localization;
using StickyTodo.Infrastructure;
using StickyTodo.Notes.Services;
using StickyTodo.Notes.ViewModels;
using StickyTodo.Notes.Views;

namespace StickyTodo.Notes;

public partial class App : Application
{
    /// <summary>全局服务图。窗口由 Avalonia XAML 载入器创建时通过它取依赖。</summary>
    public static AppServices? Services { get; private set; }

    private bool _shuttingDown;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        // 便签应用：关掉管理面板不等于退出，必须显式退出
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        desktop.Exit += (_, _) =>
        {
            Services?.Windows.BeginShutdown();

            // 注意：这里也走同一套清理，避免"点 X 关到托盘后再从别处退出"留下幽灵图标
            Services?.CleanupOnExit();
        };

        var args = Environment.GetCommandLineArgs();
        var launchedByAutoStart = HasFlag(args, "--autostart");
        var demo = HasFlag(args, "--demo");

        try
        {
            Start(desktop, args, launchedByAutoStart, demo);
        }
        catch (Exception ex)
        {
            Log.Error("启动失败：" + ex);
            Services?.Dispose();
            desktop.Shutdown(-1);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Start(
        IClassicDesktopStyleApplicationLifetime desktop,
        string[] args,
        bool launchedByAutoStart,
        bool demo)
    {
        AppPaths.EnsureCreated();
        Log.Init(AppPaths.LogDirectory);

        Services = new AppServices(useDemoTokenProvider: HasFlag(args, "--token-demo"));

        var services = Services;

        // 语言热切换：一发声就把所有文案重算一遍。
        // 注意必须赶在"应用语言"之前订阅，否则启动时那次语言应用（--lang / 设置里的语言）
        // 会发生在订阅之前，已经加载好的 XAML 文案就停在旧语言上了。
        Translator.Current.LanguageChanged += (_, _) =>
        {
            // 先刷 XAML 里的 {loc:Loc} 文案，再让各 ViewModel 重算 C# 拼出来的文案
            Localization.LocalizedString.RefreshAll();

            services.ManagerViewModel.RefreshLocalizedText();
            services.SettingsViewModel.RefreshText();
            services.PomodoroViewModel.Sync();
            services.Windows.RefreshLocalizedText();
            services.Tray.Rebuild();
        };

        services.Repository.Initialize();
        services.Todos.EnsureDefaultBoard();

        // 语言必须在任何窗口创建之前应用，否则会先渲染中文再切换，闪一下
        var languageOverride = ReadOption(args, "--lang");
        if (!string.IsNullOrWhiteSpace(languageOverride))
        {
            // 命令行覆盖只影响本次运行，不写回设置
            Translator.Current.Language = AppLanguages.Resolve(languageOverride).Language;
        }
        else
        {
            services.Localization.ApplyFromSettings();
        }

        var skinOverride = ReadOption(args, "--skin");
        var opacityOverride = ParseDouble(ReadOption(args, "--opacity"));
        services.Skin.Apply(skinOverride, opacityOverride);

        var windowOpacityOverride = ParseDouble(ReadOption(args, "--window-opacity"));
        if (windowOpacityOverride is { } wo)
        {
            services.Skin.SetWindowOpacity(wo);
        }

        desktop.MainWindow = services.Manager;

        // 开机自启拉起：不弹管理面板，只把上次的看板摆出来
        if (!launchedByAutoStart && !demo)
        {
            services.ShowManager();
        }

        StartReminderPump(services);
        services.Tray.Start();
        AttachHotKeys(services);
        services.AutoStart.Sync(services.Settings.AutoStart);

        if (!demo)
        {
            var restored = services.Windows.RestoreSession();
            Log.Info($"已恢复上次会话的 {restored} 个看板窗口");

            // 没有可恢复的窗口时兜底显示管理面板，否则用户会以为程序没启动
            if (restored == 0)
            {
                services.ShowManager();
            }
        }
        else
        {
            SeedDemoData(services);
            services.ShowManager();

            // 演示模式：把每种看板都打开一个，方便一次性看全；顺带开设置页与番茄钟
            foreach (var board in services.Todos.GetBoards())
            {
                services.OpenBoard(board);
            }

            services.ShowSettings();
            services.ShowPomodoro();

            // 工具窗口演示：--weather-demo / --worldclock-demo / --desktopclock-demo
            if (HasFlag(args, "--weather-demo"))
            {
                services.OpenToolWindow(ToolWindowKind.Weather);
            }
            if (HasFlag(args, "--worldclock-demo"))
            {
                services.OpenToolWindow(ToolWindowKind.WorldClock);
            }
            if (HasFlag(args, "--desktopclock-demo"))
            {
                services.OpenToolWindow(ToolWindowKind.DesktopClock);
            }
        }

        // 退出回归：验证托盘图标与快捷键被正确清理（--exit-trial）
        if (HasFlag(args, "--exit-trial"))
        {
            DispatcherTimer.RunOnce(() =>
            {
                Log.Info("[exit-trial] 触发 ExitApplication");
                services.ExitApplication();
            }, TimeSpan.FromSeconds(2));
        }

        // 拖拽回归：自动走一遍"拖动把手 → 落到另一行之前"，用于验证排序链路
        var dragTrial = ReadOption(args, "--drag-trial");
        if (!string.IsNullOrWhiteSpace(dragTrial))
        {
            DragTrial.Run(dragTrial);
        }

        Log.Info($"StickyTodo 启动完成，数据目录 {AppPaths.DataDirectory}");
    }

    /// <summary>本地提醒：到点弹右下角小窗 + 托盘通知。全部在本机完成。</summary>
    private void StartReminderPump(AppServices services)
    {
        services.Reminders.ReminderDue += (_, item) =>
        {
            try
            {
                services.Tray.Notify(Loc.T("reminder.title"), item.Title);

                var boardName = services.Todos.GetBoards()
                    .FirstOrDefault(b => b.Id == item.BoardId)?.Name ?? "看板";

                new ReminderPopup(item, boardName, services.Todos).Show();
            }
            catch (Exception ex)
            {
                Log.Warn("弹出提醒失败：" + ex.Message);
            }
        };

        services.Reminders.Start();
    }

    private static void AttachHotKeys(AppServices services)
    {
        services.HotKeys.Attach(
            onNewTodo: services.Windows.OpenDefaultBoardForNewTodo,
            onToggleBoards: services.Windows.ToggleBoardsVisibility,
            onLockActive: () => services.Windows.ToggleActiveLock());

        Log.Info($"全局快捷键已注册：{HotKeyService.NewTodoHotKey} 新建待办 / "
                 + $"{HotKeyService.ToggleBoardsHotKey} 显示隐藏 / {HotKeyService.LockActiveHotKey} 锁定当前看板");
    }

    /// <summary>--demo：种一份示例数据，方便在没有真实数据时看效果。</summary>
    private static void SeedDemoData(AppServices services)
    {
        try
        {
            var board = services.Todos.GetBoards().FirstOrDefault(b => b.Kind == Core.Entities.BoardKind.Todo)
                        ?? services.Todos.EnsureDefaultBoard();

            var existing = services.Todos.GetTodos(board.Id);
            if (existing.Count > 0)
            {
                return;
            }

            var now = DateTimeOffset.Now;
            services.Todos.AddTodo(board.Id, "点行尾的 ✓ 标记完成");
            services.Todos.AddTodo(board.Id, "点标题可以直接改，回车保存");
            services.Todos.AddTodo(board.Id, "拖动左侧的手柄可以排序");
            var withDue = services.Todos.AddTodo(board.Id, "明天到期的这条会显示倒数日");
            services.Todos.SetDueAt(withDue.Id, now.AddDays(1));
            var withRemind = services.Todos.AddTodo(board.Id, "这条会在一小时后提醒你");
            services.Todos.SetRemindAt(withRemind.Id, now.AddHours(1));

            // 一条"已经到点"的提醒：启动时提醒泵第一次检查就会弹出来，用来验证提醒链路
            var dueNow = services.Todos.AddTodo(board.Id, "到点提醒：验证提醒弹窗");
            services.Todos.SetRemindAt(dueNow.Id, now.AddMinutes(-1));

            var memo = services.Todos.GetBoards().FirstOrDefault(b => b.Kind == Core.Entities.BoardKind.Memo);
            if (memo is null)
            {
                memo = services.Todos.CreateBoard("随手记", Core.Entities.BoardKind.Memo);
            }

            if (services.Todos.GetNote(memo.Id) is null)
            {
                services.Todos.SaveNote(memo.Id, "便签板：想到什么写什么，自动保存。" + Environment.NewLine + "换行、缩进都可以。");
            }

            // 日历与 Token 看板也各来一个，演示时窗口类型齐全
            if (!services.Todos.GetBoards().Any(b => b.Kind == Core.Entities.BoardKind.Calendar))
            {
                services.Todos.CreateBoard("日程", Core.Entities.BoardKind.Calendar);
            }

            if (!services.Todos.GetBoards().Any(b => b.Kind == Core.Entities.BoardKind.Token))
            {
                services.Todos.CreateBoard("Token 用量", Core.Entities.BoardKind.Token);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("示例数据生成失败：" + ex.Message);
        }
    }

    /// <summary>退出时固化会话记录。</summary>
    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string? ReadOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static double? ParseDouble(string? text) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
