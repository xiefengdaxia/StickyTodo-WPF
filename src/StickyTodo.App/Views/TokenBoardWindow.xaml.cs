using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StickyTodo.App.Services;
using StickyTodo.App.ViewModels;
using StickyTodo.Core.Entities;

namespace StickyTodo.App.Views;

/// <summary>
/// Token 用量看板：按设定的间隔自动向上游面板取数，也可以手动点刷新。
/// 这是全程序唯一需要联网的界面，其它看板照旧完全本地。
/// </summary>
public partial class TokenBoardWindow : Window, IStickyWindow
{
    private const double ResizeBorder = 8d;
    private const int CascadeStep = 28;

    private readonly TokenBoardViewModel _viewModel;
    private readonly WindowManager _windows;
    private bool _geometryRestored;
    private bool _isFullScreen;

    public TokenBoardWindow(TokenBoardViewModel viewModel, WindowManager windows)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _windows = windows;
        DataContext = viewModel;

        Chart.PointPicked += (_, index) => _viewModel.ShowPointDetail(index);

        // 悬停 = 点击：指针扫过哪一根柱/点，就显示它的明细
        Chart.PointHovered += (_, index) => _viewModel.ShowPointDetail(index);

        // 取数时让刷新图标转起来；停下时解除动画（回到静态的 ⟳）
        IDisposable? spinHandle = null;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(TokenBoardViewModel.IsBusy))
            {
                return;
            }

            if (_viewModel.IsBusy)
            {
                spinHandle ??= Motion.Spin(RefreshGlyph);
            }
            else
            {
                spinHandle?.Dispose();
                spinHandle = null;
            }
        };

        Loaded += async (_, _) =>
        {
            RestoreGeometry();
            await _viewModel.LoadAsync();
            Motion.FadeInWindow(this);
        };

        Closed += (_, _) => _viewModel.Dispose();

        StateChanged += (_, _) =>
        {
            _isFullScreen = WindowState == WindowState.Maximized;
            if (_isFullScreen)
            {
                NoteRoot.CornerRadius = new CornerRadius(0);
                NoteRoot.Effect = null;
                NoteRoot.Margin = new Thickness(0);
            }
        };
    }

    public string BoardId => _viewModel.BoardId;

    public void ToggleLock() => _viewModel.ToggleLock();

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    public void RefreshAppearance() => _viewModel.RefreshAppearance();

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        // 手动刷新不重置计时器，点完继续按原间隔跑
        await _viewModel.RefreshCommand.ExecuteAsync(null);
    }

    private void HideDetail_Click(object sender, RoutedEventArgs e) => _viewModel.HideDetail();

    private void Copy_Click(object sender, RoutedEventArgs e) => _viewModel.CopyToClipboardCommand.Execute(null);

    /// <summary>热力图点某天：与趋势图同一条明细链路。</summary>
    private void HeatMap_DayPicked(object sender, int seriesIndex)
        => _viewModel.ShowPointDetail(seriesIndex);

    private void Agent_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // TwoWay 绑定已把 SelectedAgent 写回；这里只负责触发取数。
        // 初始化期间的 SelectionChanged（ItemsSource 刚填时）也会进来：
        // ApplyAgentChange 里幂等（重复 SetAgentFilter / 重取一次无害）。
        if (_viewModel.SelectedAgent is { } agent)
        {
            _viewModel.ApplyAgentChange(agent);
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleTopmost_Click(object sender, RoutedEventArgs e) => _viewModel.ToggleTopmost();

    private void ToggleLock_Click(object sender, RoutedEventArgs e) => _viewModel.ToggleLock();

    private void Appearance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            AppearanceMenu.Show(element, _viewModel, _windows);
            e.Handled = true;
        }
    }

    private void OpenManager_Click(object sender, RoutedEventArgs e) =>
        (System.Windows.Application.Current.MainWindow as ManagerWindow)?.ShowAndActivate();

    // ---------------- 位置记忆 ----------------

    private void RestoreGeometry()
    {
        if (_geometryRestored)
        {
            return;
        }

        _geometryRestored = true;

        if (_viewModel.Board.Window is { IsValid: true } state)
        {
            Width = state.Width;
            Height = state.Height;
            var (left, top) = ScreenHelper.ClampIntoWorkArea(this, state.Left, state.Top, state.Width, state.Height);
            Left = left;
            Top = top;
            return;
        }

        var offset = CascadeStep * (_windows.OpenWindowCount + 1);
        var (cascadeLeft, cascadeTop) = ScreenHelper.ClampIntoWorkArea(this, 90 + offset, 90 + offset, Width, Height);
        Left = cascadeLeft;
        Top = cascadeTop;
    }

    private void SaveGeometry()
    {
        if (!_geometryRestored || WindowState != WindowState.Normal)
        {
            return;
        }

        _viewModel.Board.Window = new BoardWindowState(Left, Top, ActualWidth, ActualHeight);
        _windows.SaveBoardGeometry(_viewModel.Board);
    }

    // ---------------- 边缘缩放 ----------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != 0x0084 || _isFullScreen || ResizeMode == ResizeMode.NoResize)
        {
            return nint.Zero;
        }

        long raw = lParam.ToInt64();
        var point = new NativePoint
        {
            X = (int)(short)(raw & 0xFFFF),
            Y = (int)(short)((raw >> 16) & 0xFFFF),
        };

        if (!NativeMethods.ScreenToClient(hwnd, ref point))
        {
            return nint.Zero;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var x = point.X / dpi.DpiScaleX;
        var y = point.Y / dpi.DpiScaleY;

        var onLeft = x <= ResizeBorder;
        var onRight = x >= ActualWidth - ResizeBorder;
        var onTop = y <= ResizeBorder;
        var onBottom = y >= ActualHeight - ResizeBorder;

        var hit = onTop && onLeft ? 13
            : onTop && onRight ? 14
            : onBottom && onLeft ? 16
            : onBottom && onRight ? 17
            : onLeft ? 10
            : onRight ? 11
            : onTop ? 12
            : onBottom ? 15
            : 0;

        if (hit == 0)
        {
            return nint.Zero;
        }

        handled = true;
        return new nint(hit);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveGeometry();
        base.OnClosing(e);
    }

    // ---------------- 回归测试用入口 ----------------

    /// <summary>给回归用：切换统计范围并等一次刷新完成。返回耗时。</summary>
    internal async Task<long> SelectRangeForTestAsync(int days)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var before = _viewModel.FetchCount;

        _viewModel.SelectRange(days);

        // 等这次刷新真的完成（取数次数增加且不再 busy）
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (_viewModel.FetchCount > before && !_viewModel.IsBusy)
            {
                break;
            }

            await Task.Delay(20);
        }

        watch.Stop();
        return watch.ElapsedMilliseconds;
    }

    /// <summary>给回归用：切换自动刷新间隔。</summary>
    internal void SelectIntervalForTest(int seconds) => _viewModel.SelectInterval(seconds);

    /// <summary>给回归用：点一下手动刷新并等待完成。</summary>
    internal async Task RefreshForTestAsync()
    {
        await _viewModel.RefreshCommand.ExecuteAsync(null);
        while (_viewModel.IsBusy)
        {
            await Task.Delay(50);
        }
    }

    /// <summary>给回归用：模拟点击趋势图的第 index 个点。</summary>
    internal void PickPointForTest(int index)
    {
        _viewModel.ShowPointDetail(index);
        Chart.SelectedIndex = index;
    }

    /// <summary>给回归用：模拟把指针悬停到趋势图第 index 个点（验证"悬停=点击"）。</summary>
    internal void HoverChartForTest(int index) => Chart.HoverForTest(index);

    /// <summary>给回归用：当前选中的点（-1 = 没选中）。</summary>
    internal int SelectedIndexForTest => _viewModel.SelectedIndex;

    /// <summary>给回归用：把当前界面状态收成一行日志。</summary>
    internal string DescribeForTest()
    {
        var vm = _viewModel;
        return $"总数={vm.TotalText} 调用={vm.StepsText} 缓存比={vm.CacheRatioText} " +
               $"点数={vm.Stats.Series.Count} 模型={vm.Models.Count} " +
               $"范围={vm.RangeDays} 粒度={vm.Granularity} 间隔={vm.RefreshSeconds}s " +
               $"取数={vm.FetchCount} 明细={(vm.HasDetail ? vm.DetailTitle : "无")} 错误={vm.ErrorText ?? "无"}";
    }

    /// <summary>给回归用：累计成功取数次数。</summary>
    internal int FetchCount => _viewModel.FetchCount;

    /// <summary>给回归用：切到一个范围，只测"界面多久出数据"（不等后台刷新）。</summary>
    internal long SelectRangeImmediate(int days)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _viewModel.SelectRange(days);
        watch.Stop();
        return watch.ElapsedMilliseconds;
    }

    /// <summary>给回归用：当前趋势图有多少个点。</summary>
    internal int PointCountForTest => _viewModel.Stats.Series.Count;

    /// <summary>Regression hook: simulate clicking the copy button; returns the status text.</summary>
    internal string CopyForTest()
    {
        _viewModel.CopyToClipboardCommand.Execute(null);
        return _viewModel.CopyStatusText ?? "(null)";
    }

    /// <summary>Regression hook: the assembled copy text (no clipboard touch).</summary>
    internal string CopyTextForTest() => _viewModel.CopyTextForTest();

    /// <summary>回归用：客户端下拉的选项数（非聚合 provider 为 0）。</summary>
    internal int AgentsCountForTest => _viewModel.Agents.Count;

    /// <summary>回归用：热力图区域当前是否真的可见（验证 IsDaily → Visibility 绑定）。</summary>
    internal bool HeatMapVisibleForTest => HeatSection.Visibility == Visibility.Visible;
}