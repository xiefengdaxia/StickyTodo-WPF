using System.IO;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using StickyTodo.App.Services;
using StickyTodo.App.ViewModels;
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

        // 无界面自检：dotnet run -- --selftest（CI / 冒烟测试用）
        if (e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            RunSelfTest();
            return;
        }

        // 单实例：第二个进程直接提示并退出（后续可换成命名管道唤醒已有窗口）
        _mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("StickyTodo 已经在运行。", "StickyTodo", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppPaths.EnsureCreated();
        ConfigureLogging();

        try
        {
            _host = BuildHost();
            await _host.StartAsync();

            var repository = _host.Services.GetRequiredService<ITodoRepository>();
            repository.Initialize();
            _host.Services.GetRequiredService<TodoService>().EnsureDefaultBoard();

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();

            _host.Services.GetRequiredService<TrayService>().Start();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "启动失败");
            MessageBox.Show($"启动失败：{ex.Message}\n\n日志位置：{AppPaths.LogDirectory}",
                "StickyTodo", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
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
            var board = service.EnsureDefaultBoard();

            Log.Information("[selftest] db={Db} boards={Boards} default={Default} todos={Todos}",
                AppPaths.DatabasePath, service.GetBoards().Count, board.Name, service.GetTodos(board.Id).Count);
            Log.Information("[selftest] plan={Plan} tier={Tier} maxBoards={MaxBoards} ics={Ics} export={Export}",
                service.Entitlement.DisplayName, service.Entitlement.Tier,
                service.Entitlement.MaxBoards, service.Entitlement.CanUseIcsCalendar,
                service.Entitlement.CanExportExcel);

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

    private IHost BuildHost()
    {
        return Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(services =>
            {
                // ★ 发行版策略：所有用户都是永久会员。
                //   想改回“免费版 + 付费升级”，只需把这行换成 FreeTierEntitlement。
                services.AddSingleton<IEntitlementService, PermanentFreeEntitlement>();

                services.AddSingleton<ITodoRepository>(_ => new SqliteTodoRepository(AppPaths.DatabasePath));
                services.AddSingleton<TodoService>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
                services.AddSingleton<TrayService>();
            })
            .Build();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                await _host.StopAsync(TimeSpan.FromSeconds(3));
                _host.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "退出时停止宿主失败");
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