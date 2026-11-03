using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using StickyTodo.Notes.Services;

namespace StickyTodo.Notes.Services;

/// <summary>
/// 空闲时把工作集还给系统（Avalonia 版，与 WPF 版同一套策略）。
///
/// 便签类应用 99% 的时间是摊在桌面上不动的，但界面一旦画出来，位图表面、
/// 字体缓存、控件树就会一直占着物理内存。这里只在「25 秒没有键鼠输入」时动手，
/// 先做一次压缩 GC，再让 Windows 回收工作集；页面并没有丢，点到时按页换回来。
/// </summary>
public sealed class MemoryTrimmer : IDisposable
{
    private static readonly TimeSpan IdleBeforeTrim = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(20);

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _sinceStart = Stopwatch.StartNew();
    private DateTime _lastTrim = DateTime.MinValue;

    private MemoryTrimmer()
    {
        _timer = new DispatcherTimer(CheckInterval, DispatcherPriority.Background, (_, _) => TryTrim());
    }

    public static MemoryTrimmer Start()
    {
        var trimmer = new MemoryTrimmer();
        trimmer._timer.Start();
        Log.Debug("[mem] 空闲内存修剪已启用");
        return trimmer;
    }

    private void TryTrim()
    {
        if (_sinceStart.Elapsed < StartupGrace)
        {
            return;
        }

        if (DateTime.UtcNow - _lastTrim < TimeSpan.FromMinutes(2))
        {
            return;
        }

        if (IdleSeconds() < IdleBeforeTrim.TotalSeconds)
        {
            return;
        }

        _lastTrim = DateTime.UtcNow;
        Trim();
    }

    private static void Trim()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var before = process.WorkingSet64 / 1024d / 1024d;

            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: true);
            SetProcessWorkingSetSize(process.Handle, -1, -1);

            process.Refresh();
            Log.Debug($"[mem] 工作集修剪 {before:N1} MB -> {process.WorkingSet64 / 1024d / 1024d:N1} MB");
        }
        catch (Exception ex)
        {
            Log.Debug("[mem] 工作集修剪失败：" + ex.Message);
        }
    }

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

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, int minimum, int maximum);
}
