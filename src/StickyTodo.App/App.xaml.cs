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
using StickyTodo.Core.Entitlements;
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

            // 命令行覆盖（演示/截图回归用）：--skin mint --opacity 0.6
            var skinService = _host.Services.GetRequiredService<SkinService>();
            var skinOverride = ReadOptionValue(e.Args, "--skin");
            var opacityOverride = ReadOptionValue(e.Args, "--opacity");
            skinService.Apply(
                skinOverride,
                double.TryParse(opacityOverride, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedOpacity)
                    ? parsedOpacity
                    : null);

            var manager = _host.Services.GetRequiredService<ManagerWindow>();
            MainWindow = manager;
            manager.Show();

            _host.Services.GetRequiredService<TrayService>().Start();

            if (isDemo)
            {
                RunDemo(shotDirectory);
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
                services.AddSingleton<ManagerViewModel>();
                services.AddSingleton<ManagerWindow>();
            })
            .Build();
    }

    /// <summary>
    /// --demo：库里还没有任何待办时种一份示例数据，并打开两个便签窗口摆到相邻位置，
    /// 用来验证多窗口 + 吸附 + 自动截图的回归链路（截图脚本见 tools/capture-demo.ps1）。
    /// </summary>
    private void RunDemo(string? shotDirectory)
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
            service.AddTodo(defaultBoard.Id, "右键待办可以挂标签");

            var done = service.AddTodo(defaultBoard.Id, "写下第一条待办：已完成的样子");
            service.SetDone(done.Id, true);

            service.AddTodo(workBoard.Id, "整理本周发布清单");
            service.AddTodo(workBoard.Id, "周一 10:00 项目周会");

            if (!service.GetTags().Any())
            {
                var important = service.CreateTag("重要");
                var firstWorkTodo = service.GetTodos(workBoard.Id).First();
                service.SetTodoTags(firstWorkTodo.Id, [important.Id]);
            }
        }

        var first = windows.OpenBoard(defaultBoard);
        var second = windows.OpenBoard(workBoard);

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

    private static string? ReadOptionValue(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
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