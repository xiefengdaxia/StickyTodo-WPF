using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Serilog;

namespace StickyTodo.App.Services;

/// <summary>
/// 空闲时把工作集还给系统。
///
/// 便签 / 桌面时钟这类挂件 99% 的时间都摊在桌面上不动，但 WPF 一旦把窗口渲染出来，
/// 位图表面、字体缓存、XAML 树就会一直占着物理内存。这里做两件事：
///   1. 只在「用户不在用我们」的时候动手，绝不打断正在进行的操作；
///   2. 先做一次压缩 GC，再调用 <c>SetProcessWorkingSetSize(-1, -1)</c> 让 Windows 回收工作集。
///
/// 「不在用我们」有两种情况，满足任意一种就修剪：
///   * 整个桌面 25 秒没有键鼠输入（用户离开了）；
///   * 当前前台窗口不是我们的（用户在 VS Code / 浏览器里干活，这时候把我们换出去不影响手感）。
/// 只有桌面上一直是我们自己的窗口时才不修剪——那种情况下换页会被用户直接感知到。
///
/// 被换出的页并没有丢，下次点到时会按页换回来（活跃页只有几 MB），
/// 所以观感上最多是「首次点击略微一顿」，换来的是长期低内存。
/// </summary>
public sealed class MemoryTrimmer : IDisposable
{
    /// <summary>整个桌面多久没有输入就算「用户不在」。</summary>
    private static readonly TimeSpan IdleBeforeTrim = TimeSpan.FromSeconds(25);

    /// <summary>多久没被前台激活（用户在别的程序里）就可以修剪。</summary>
    private static readonly TimeSpan BackgroundBeforeTrim = TimeSpan.FromSeconds(20);

    /// <summary>检查间隔。</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    /// <summary>启动后先安静这么久再第一次修剪，避开窗口入场动画。</summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(15);

    /// <summary>两次修剪之间的最小间隔。</summary>
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(45);

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _sinceStart = Stopwatch.StartNew();
    private DateTime _lastTrim = DateTime.MinValue;
    private DateTime _lastForeground = DateTime.UtcNow;

    private MemoryTrimmer()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = CheckInterval };
        _timer.Tick += (_, _) => TryTrim();
    }

    public static MemoryTrimmer Start()
    {
        var trimmer = new MemoryTrimmer();
        trimmer._timer.Start();
        Log.Debug("[mem] 空闲内存修剪已启用");
        return trimmer;
    }

    /// <summary>立刻修剪一次（演示/回归用）。</summary>
    public static void TrimNow() => Trim();

    private void TryTrim()
    {
        if (_sinceStart.Elapsed < StartupGrace)
        {
            return;
        }

        // 刚修剪过就别反复来（每次修剪都会让后续操作多几次缺页）
        if (DateTime.UtcNow - _lastTrim < MinGap)
        {
            return;
        }

        if (OurWindowIsForeground())
        {
            _lastForeground = DateTime.UtcNow;
            return;
        }

        var desktopIdle = IdleSeconds();
        var backgroundFor = (DateTime.UtcNow - _lastForeground).TotalSeconds;

        if (desktopIdle < IdleBeforeTrim.TotalSeconds && backgroundFor < BackgroundBeforeTrim.TotalSeconds)
        {
            return;
        }

        _lastTrim = DateTime.UtcNow;
        Trim();
    }

    private static void Trim()
    {
        var before = 0d;

        try
        {
            using var process = Process.GetCurrentProcess();
            before = process.WorkingSet64 / 1024d / 1024d;

            // 压缩 GC：把还能回收的托管对象先收掉，避免修剪后又立刻涨回来
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: true);

            // -1, -1 = 让系统按需回收工作集
            SetProcessWorkingSetSize(process.Handle, -1, -1);

            process.Refresh();
            var after = process.WorkingSet64 / 1024d / 1024d;

            Log.Debug("[mem] 工作集修剪 {Before:N1} MB -> {After:N1} MB", before, after);
        }
        catch (Exception ex)
        {
            // 修剪失败不影响使用，最多就是内存高一点
            Log.Debug(ex, "[mem] 工作集修剪失败");
        }
    }

    /// <summary>当前前台窗口是不是我们自己的（含托盘菜单这类弹出层）。</summary>
    private static bool OurWindowIsForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == nint.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == Environment.ProcessId;
    }

    /// <summary>桌面空闲秒数（没有键鼠输入）。取不到时返回一个很大的值，宁可修剪也不卡用户。</summary>
    private static double IdleSeconds()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };

        if (!GetLastInputInfo(ref info))
        {
            return double.MaxValue;
        }

        var idleMs = unchecked(Environment.TickCount - (int)info.dwTime);
        return idleMs < 0 ? double.MaxValue : idleMs / 1000d;
    }

    public void Dispose() => _timer.Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, int minimum, int maximum);
}
