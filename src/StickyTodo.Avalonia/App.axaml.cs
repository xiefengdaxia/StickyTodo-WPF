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
using StickyTodo.Updates;

namespace StickyTodo.Notes;

public partial class App : Application
{
    /// <summary>全局服务图。窗口由 Avalonia XAML 载入器创建时通过它取依赖。</summary>
    public static AppServices? Services { get; private set; }

    private bool _shuttingDown;

    /// <summary>空闲时回收工作集（见 MemoryTrimmer）。</summary>
    private MemoryTrimmer? _memoryTrimmer;

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
            _memoryTrimmer?.Dispose();
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

    /// <summary>
    /// --update-demo：检查更新弹窗回归。用一条假的新版本（不联网，截图可重复）；
    /// 加 <c>--fail</c> 时改成假的失败态，验证失败文案与细节展示。
    /// </summary>
    private static void ShowUpdateDemo(AppServices services, bool fail)
    {
        var repository = services.Updates.Repository;

        if (fail)
        {
            UpdateDialog.ShowResult(
                null,
                UpdateCheckResult.Failed(
                    AppVersionInfo.Current,
                    UpdateFailure.Network,
                    "The SSL connection could not be established, see inner exception. (示例)"),
                repository);
            return;
        }

        var notes = string.Join("\n",
            "### 新增",
            "- 检查更新：按仓库地址比对最新 Release，不再靠手动看版本号",
            "- 待办行内容垂直居中",
            "- 标签与完成/删除按钮共用右侧位置，鼠标悬停互换",
            string.Empty,
            "### 修复",
            "- 天气接口连不上时明确显示「暂无数据」，不拿别的城市顶替");

        var release = new UpdateRelease(
            "v9.9.9",
            new Version(9, 9, 9),
            "StickyTodo 9.9.9",
            notes,
            repository.ReleasesPageUrl + "/tag/v9.9.9",
            repository.ReleasesPageUrl,
            DateTimeOffset.UtcNow);

        UpdateDialog.ShowResult(
            null,
            UpdateCheckResult.Available(AppVersionInfo.Current, new Version(9, 9, 9), release),
            repository);

        Log.Info("[update] 已弹出假的新版本弹窗（当前 " + AppVersionInfo.Display(AppVersionInfo.Current) + "）");
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

        // 先刷掉上次被强杀进程留下的托盘图标，再挂自己的图标；2 秒后再补一次
        TrayRefresher.Refresh();
        services.Tray.Start();
        Avalonia.Threading.DispatcherTimer.RunOnce(() => TrayRefresher.Refresh(), TimeSpan.FromSeconds(2));
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

            // 检查更新回归：--update-demo 弹假的新版本，--update-demo --fail 弹假的失败态
            if (HasFlag(args, "--update-demo"))
            {
                try
                {
                    ShowUpdateDemo(services, HasFlag(args, "--fail"));
                }
                catch (Exception ex)
                {
                    Log.Error("[update] 弹窗失败：" + ex);
                }
            }

            // --create-tool-demo <weather|worldclock|desktopclock>：打开一个工具型看板（进看板列表）。
            // 已有同类型的就复用，否则每次启动都会多建一个看板。
            var toolBoard = ReadOption(args, "--create-tool-demo");
            var toolKind = toolBoard?.Trim().ToLowerInvariant() switch
            {
                "weather" => Core.Entities.BoardKind.Weather,
                "worldclock" => Core.Entities.BoardKind.WorldClock,
                "desktopclock" => Core.Entities.BoardKind.DesktopClock,
                "networkspeed" => Core.Entities.BoardKind.NetworkSpeed,
                "calendar" => Core.Entities.BoardKind.Calendar,
                "performance" => Core.Entities.BoardKind.Performance,
                _ => (Core.Entities.BoardKind?)null,
            };

            if (toolKind is { } kind)
            {
                var created = services.Todos.GetBoards().FirstOrDefault(b => b.Kind == kind)
                    ?? services.Todos.CreateBoard(
                        kind switch
                        {
                            Core.Entities.BoardKind.Weather => "天气",
                            Core.Entities.BoardKind.WorldClock => "世界时钟",
                            Core.Entities.BoardKind.NetworkSpeed => "网速",
                            Core.Entities.BoardKind.Calendar => "日历",
                            Core.Entities.BoardKind.Performance => "性能",
                            _ => "桌面时钟",
                        }, kind);

                // OpenBoard 会触发 BoardsChanged，管理面板自动刷新（见 AppServices）
                services.Windows.OpenBoard(created);
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

        // 待办列表增量更新 + 删除可撤销回归
        if (HasFlag(args, "--row-undo-demo"))
        {
            _ = RunRowUndoDemoAsync(services);
        }

        // 看板内搜索回归
        if (HasFlag(args, "--search-demo"))
        {
            _ = RunSearchDemoAsync(services);
        }

        Log.Info($"StickyTodo 启动完成，数据目录 {AppPaths.DataDirectory}");

        // 空闲时把工作集还给系统（详见 MemoryTrimmer 的注释）
        _memoryTrimmer = MemoryTrimmer.Start();
    }

    /// <summary>
    /// --row-undo-demo：验证这一轮新加的两件事（Avalonia 版）：
    ///   ① 待办列表「增量更新」——勾掉一条后，别的行对象还是原来那几个实例；
    ///   ② 删除可撤销——删掉一条后提示条浮出，撤销能把标题 / 标签 / 位置都还原。
    /// 顺带断言 XAML 里「已删除…撤销」那条真的会显示（不是只改了 ViewModel）。
    /// 全部通过 exit=0，否则 exit=2。
    /// </summary>
    private static async Task RunRowUndoDemoAsync(AppServices services)
    {
        try
        {
            var board = services.Todos.EnsureDefaultBoard();

            foreach (var existing in services.Todos.GetTodos(board.Id))
            {
                services.Todos.Delete(existing.Id);
            }

            services.Todos.AddTodo(board.Id, "第一条");
            var second = services.Todos.AddTodo(board.Id, "第二条");
            services.Todos.AddTodo(board.Id, "第三条");

            var tag = services.Todos.CreateTag("重要");
            services.Todos.SetTodoTags(second.Id, [tag.Id]);

            var window = (BoardWindow)services.Windows.OpenBoard(board);

            // 等窗口 Loaded 里的首次 LoadAsync 落地
            await Task.Delay(900);

            // ---- ① 增量更新：勾掉最后一条，其它行的对象实例必须原封不动 ----
            var firstRef = window.RowsForTest[0];
            var secondRef = window.RowsForTest[1];
            var thirdRef = window.RowsForTest[2];

            await window.CompleteRowByIndexForTestAsync(2);

            var syncOk = window.RowsForTest.Count == 2
                         && ReferenceEquals(window.RowsForTest[0], firstRef)
                         && ReferenceEquals(window.RowsForTest[1], secondRef)
                         && !ReferenceEquals(window.RowsForTest[0], thirdRef);
            Log.Info($"[rowundo] ①增量更新：行数={window.RowsForTest.Count} 行对象原地复用={syncOk}");

            // ---- ② 删除：提示条要真的浮出来 ----
            await window.DeleteRowByIndexForTestAsync(1);

            var undoBar = window.FindControl<Avalonia.Controls.StackPanel>("UndoBar");
            var statusLine = window.FindControl<Avalonia.Controls.TextBlock>("StatusLine");
            var undoText = window.FindControl<Avalonia.Controls.TextBlock>("UndoText");

            var barOk = undoBar is { IsVisible: true }
                        && statusLine is { IsVisible: false }
                        && undoText?.Text.Contains("第二条", StringComparison.Ordinal) == true;
            Log.Info($"[rowundo] ②删除：行数={window.RowsForTest.Count} 提示条浮出={barOk} 文案={undoText?.Text}");

            // ---- ③ 撤销：标题 / 标签 / 行数都回来 ----
            await window.UndoDeleteForTestAsync();

            var restored = services.Todos.GetTodos(board.Id).FirstOrDefault(t => t.Title == "第二条");
            IReadOnlyList<string> restoredTags = [];
            if (restored is not null
                && services.Todos.GetTodoTagMap(board.Id).TryGetValue(restored.Id, out var ids))
            {
                restoredTags = ids;
            }

            var undoOk = window.RowsForTest.Count == 2
                         && undoBar is { IsVisible: false }
                         && restored is not null
                         && restoredTags.Contains(tag.Id);
            Log.Info($"[rowundo] ③撤销：行数={window.RowsForTest.Count} 标签回来了={string.Join(",", restoredTags)} 断言={undoOk}");

            var allOk = syncOk && barOk && undoOk;
            Log.Info($"[rowundo] 总结果={(allOk ? "通过" : "失败")}");
            Environment.ExitCode = allOk ? 0 : 2;
        }
        catch (Exception ex)
        {
            Log.Error("[rowundo] 失败：" + ex);
            Environment.ExitCode = 1;
        }
        finally
        {
            services.ExitApplication();
        }
    }

    /// <summary>
    /// --search-demo：验证看板内搜索（Avalonia 版）——
    /// 按标题 / 按标签 / 搜不到 / 清空恢复，并检查搜索框真的显示出来了。
    /// </summary>
    private static async Task RunSearchDemoAsync(AppServices services)
    {
        try
        {
            var board = services.Todos.EnsureDefaultBoard();

            foreach (var existing in services.Todos.GetTodos(board.Id))
            {
                services.Todos.Delete(existing.Id);
            }

            services.Todos.AddTodo(board.Id, "买牛奶");
            var tagged = services.Todos.AddTodo(board.Id, "写周报");
            services.Todos.AddTodo(board.Id, "去健身房");
            var tag = services.Todos.CreateTag("工作");
            services.Todos.SetTodoTags(tagged.Id, [tag.Id]);

            var window = (BoardWindow)services.Windows.OpenBoard(board);
            await Task.Delay(900);

            var allCount = window.RowsForTest.Count;

            window.SetSearchForTest("牛奶");
            var byTitle = window.RowsForTest.Count == 1;

            window.SetSearchForTest("工作");
            var byTag = window.RowsForTest.Count == 1;

            window.SetSearchForTest("不存在的关键字");
            var noneOk = window.RowsForTest.Count == 0;

            window.ClearSearchForTest();
            var restoreOk = window.RowsForTest.Count == allCount;

            var box = window.FindControl<Avalonia.Controls.TextBox>("SearchBox");
            Log.Info($"[search] 全部={allCount} 按标题={byTitle} 按标签={byTag} 无结果={noneOk} 清空恢复={restoreOk} 搜索框={box?.IsVisible}");

            var allOk = byTitle && byTag && noneOk && restoreOk;
            Log.Info($"[search] 总结果={(allOk ? "通过" : "失败")}");
            Environment.ExitCode = allOk ? 0 : 2;
        }
        catch (Exception ex)
        {
            Log.Error("[search] 失败：" + ex);
            Environment.ExitCode = 1;
        }
        finally
        {
            services.ExitApplication();
        }
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
