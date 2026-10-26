using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using StickyTodo.Core.Localization;
using StickyTodo.Core.Pomodoro;
using StickyTodo.Notes.ViewModels;

namespace StickyTodo.Notes.Views;

/// <summary>
/// 番茄时钟窗口：25 分钟专注 + 5 分钟休息。
/// 计时逻辑在 Core 的 <see cref="PomodoroEngine"/>，这里只负责每秒推进与渲染。
/// </summary>
public partial class PomodoroWindow : Window
{
    private readonly PomodoroViewModel _viewModel;
    private readonly DispatcherTimer _timer;
    private readonly Action<string, string>? _notify;
    private DateTimeOffset _lastTick;

    public PomodoroWindow(PomodoroViewModel viewModel, Action<string, string>? notify = null)
    {
        _viewModel = viewModel;
        _notify = notify;

        InitializeComponent();
        DataContext = viewModel;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += OnTick;

        Opened += (_, _) =>
        {
            Chrome.Apply(this, NoteRoot, 14, 10);
            _lastTick = DateTimeOffset.Now;
            _timer.Start();
        };

        Closed += (_, _) =>
        {
            _timer.Stop();
            _viewModel.Flush();
        };
    }

    public PomodoroViewModel ViewModel => _viewModel;

    public void ShowAndActivate()
    {
        if (!IsVisible)
        {
            Show();
        }

        Activate();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.Now;
        var elapsed = now - _lastTick;
        _lastTick = now;

        if (elapsed <= TimeSpan.Zero)
        {
            return;
        }

        var finished = _viewModel.Advance(elapsed);
        if (finished is null)
        {
            return;
        }

        // 阶段结束时给一个提醒：托盘气泡 + 标题
        var title = Loc.T("pomodoro.title");
        var body = finished == PomodoroPhase.Focus ? Loc.T("pomodoro.focusDoneBody") : Loc.T("pomodoro.breakDoneBody");
        _notify?.Invoke(title, body);
    }

    private void Toggle_Click(object? sender, RoutedEventArgs e) => _viewModel.Toggle();

    private void Reset_Click(object? sender, RoutedEventArgs e) => _viewModel.Reset();

    private void Skip_Click(object? sender, RoutedEventArgs e) => _viewModel.Skip();

    private void TitleBar_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // 标题栏上的按钮不参与拖动
        if (e.Source is Control source)
        {
            var current = source;
            while (current is not null)
            {
                if (current is Button)
                {
                    return;
                }

                current = current.Parent as Control;
            }
        }

        try
        {
            BeginMoveDrag(e);
        }
        catch (Exception)
        {
            // 某些合成/远程桌面场景下不支持拖动，忽略
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
