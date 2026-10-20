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

        var window = _host.Services.GetRequiredService<SettingsWindow>();
        if (window.IsVisible)
        {
            window.Activate();
            return;
        }

        window.Show();
        window.Activate();
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