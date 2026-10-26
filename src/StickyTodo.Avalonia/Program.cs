using Avalonia;
using StickyTodo.Notes;
using StickyTodo.Notes.Services;

internal static class Program
{
    /// <summary>单实例互斥体名（Local\ 前缀表示只在当前登录会话内生效）。</summary>
    private const string MutexName = @"Local\StickyTodo.SingleInstance";

    [System.STAThread]
    public static int Main(string[] args)
    {
        // 发布脚本用这个入口做冒烟测试：不弹窗、只检查运行时 / 数据目录 / SQLite。
        if (args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            return SelfTest.Run();
        }

        // 单实例：重复启动只提示一次，然后把已有窗口叫到前面
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            Log.Init(StickyTodo.Infrastructure.AppPaths.LogDirectory);
            Log.Warn("StickyTodo 已经在运行，本次启动忽略。");
            return 0;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // 渲染/合成顺序交给 Avalonia 自己挑：
            //   Win10/11 → WinUIComposition / DirectComposition
            //   Win7     → 自动落到 RedirectionSurface（不支持透明，窗口会切到不透明外观，
            //              见 Views/Chrome.cs 的降级处理）
            // 关键顺序：CompositionMode 里 WinUIComposition 必须在最前，
            // 否则窗口会被强制走 RedirectionSurface，连拖放事件的命中测试都会异常。
            .With(new Win32PlatformOptions
            {
                RenderingMode = new[]
                {
                    Win32RenderingMode.AngleEgl,
                    Win32RenderingMode.Software
                },
                CompositionMode = new[]
                {
                    Win32CompositionMode.WinUIComposition,
                    Win32CompositionMode.DirectComposition,
                    Win32CompositionMode.RedirectionSurface
                }
            })
            .LogToTrace();
}
