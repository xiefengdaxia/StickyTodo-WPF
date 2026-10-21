using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StickyTodo.App.Services;
using StickyTodo.App.ViewModels;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Localization;

namespace StickyTodo.App.Views;

/// <summary>日历看板：月视图 + 当日日程，支持把日程拖到别的日期改期。</summary>
public partial class CalendarWindow : Window, IStickyWindow
{
    private const double ResizeBorder = 8d;
    private const int CascadeStep = 28;

    private readonly CalendarViewModel _viewModel;
    private readonly WindowManager _windows;
    private bool _geometryRestored;
    private bool _isFullScreen;

    /// <summary>首次加载完成前，下拉框的 SelectionChanged 不该触发跳转。</summary>
    private bool _loaded;

    public CalendarWindow(CalendarViewModel viewModel, WindowManager windows)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _windows = windows;
        DataContext = viewModel;

        Loaded += async (_, _) =>
        {
            RestoreGeometry();
            await _viewModel.LoadAsync();
            SyncMonthBoxes();
            _loaded = true;

            // 位置与内容都就绪后再淡入，避免看到"先跳位再显示"
            Motion.FadeInWindow(this);
        };

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

    /// <summary>把下拉框的选中项同步到 ViewModel 当前的年月。</summary>
    private void SyncMonthBoxes()
    {
        YearBox.SelectedItem = _viewModel.Year;
        MonthBox.SelectedItem = _viewModel.Month;
    }

    // ---------------- 导航 ----------------

    private async void PrevMonth_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.GoToMonthAsync(-1);
        SyncMonthBoxes();
    }

    private async void NextMonth_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.GoToMonthAsync(1);
        SyncMonthBoxes();
    }

    private async void Today_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.GoToTodayAsync();
        SyncMonthBoxes();
    }

    /// <summary>年下拉框：选完跳到该年同月。</summary>
    private async void YearBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || YearBox.SelectedItem is not int year)
        {
            return;
        }

        await _viewModel.SelectYearAsync(year);
    }

    /// <summary>月下拉框。</summary>
    private async void MonthBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || MonthBox.SelectedItem is not int month)
        {
            return;
        }

        await _viewModel.SelectMonthAsync(month);
    }

    private void Cell_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CalendarDayViewModel day })
        {
            _viewModel.SelectDay(day);

            // 黄历内容整体换了一批，淡入一下让人注意到"下面变了"
            Motion.FadeInElement(AlmanacPanel, fromScale: 1, duration: Motion.Fast);
        }
    }

    private async void EntryCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: CalendarEntryViewModel entry } check)
        {
            await _viewModel.ToggleEntryAsync(entry, check.IsChecked == true);
        }
    }

    private void DeleteEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CalendarEntryViewModel entry })
        {
            return;
        }

        var confirm = MessageBox.Show(
            Loc.F("cal.deleteConfirm", entry.Title),
            Loc.T("cal.deleteTitle"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        ConfirmDelete(entry.TodoId);
    }

    private void ConfirmDelete(string todoId)
    {
        try
        {
            _windows.DeleteTodoAndRefresh(todoId);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Loc.T("cal.deleteFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------------- 拖动改期 ----------------

    private void Cell_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(CalendarEntryViewModel))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Cell_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(CalendarEntryViewModel)) is not CalendarEntryViewModel entry)
        {
            return;
        }

        if (sender is not FrameworkElement { DataContext: CalendarDayViewModel day })
        {
            return;
        }

        await _viewModel.MoveEntryToDateAsync(entry, day.Date);
    }

    // ---------------- 从当日列表拖到别的日期 = 改期 ----------------

    private Point _entryDragStart;
    private CalendarEntryViewModel? _entryDragCandidate;
    private bool _entryDragArmed;

    private void Entry_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<CheckBox>(source) is not null || FindAncestor<Button>(source) is not null)
        {
            _entryDragArmed = false;
            return;
        }

        _entryDragCandidate = (sender as FrameworkElement)?.DataContext as CalendarEntryViewModel;
        _entryDragStart = e.GetPosition(this);
        _entryDragArmed = _entryDragCandidate is not null;
    }

    private void Entry_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_entryDragArmed || _entryDragCandidate is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _entryDragStart.X) < 6 && Math.Abs(position.Y - _entryDragStart.Y) < 6)
        {
            return;
        }

        _entryDragArmed = false;
        DragDrop.DoDragDrop((DependencyObject)sender, _entryDragCandidate, DragDropEffects.Move);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    // ---------------- 标题栏 ----------------

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

    // ---------------- 位置记忆 / 缩放 ----------------

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
        var (cascadeLeft, cascadeTop) = ScreenHelper.ClampIntoWorkArea(this, 120 + offset, 120 + offset, Width, Height);
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

    /// <summary>给回归用：跳到指定年月。</summary>
    internal async Task GoToForTestAsync(int year, int month)
    {
        await _viewModel.SelectYearAsync(year);
        await _viewModel.SelectMonthAsync(month);
        SyncMonthBoxes();
    }

    /// <summary>给回归用：切换年下拉。</summary>
    internal async Task SelectYearForTestAsync(int year)
    {
        YearBox.SelectedItem = year;
        await _viewModel.SelectYearAsync(year);
        await Task.Delay(50);
    }

    /// <summary>给回归用：切换月下拉。</summary>
    internal async Task SelectMonthForTestAsync(int month)
    {
        MonthBox.SelectedItem = month;
        await _viewModel.SelectMonthAsync(month);
        await Task.Delay(50);
    }

    /// <summary>给回归用：点某一天。</summary>
    internal async Task SelectDayForTestAsync(DateOnly date)
    {
        var day = _viewModel.Days.FirstOrDefault(d => d.Date == date);
        if (day is not null)
        {
            _viewModel.SelectDay(day);
        }

        await Task.Delay(100);
    }

    /// <summary>给回归用：当前月份的格子数据。</summary>
    internal IReadOnlyList<ViewModels.CalendarDayViewModel> DaysForTest() => _viewModel.Days.ToList();

    /// <summary>给回归用：当前黄历文字。</summary>
    internal string AlmanacForTest() =>
        $"{_viewModel.SelectedDateText} | {_viewModel.AlmanacDateText} | {_viewModel.AlmanacMetaText} | " +
        $"{_viewModel.AlmanacYiText} | {_viewModel.AlmanacJiText}";

    /// <summary>给回归用：当前年月与状态。</summary>
    internal string DescribeForTest() =>
        $"{_viewModel.Year} 年 {_viewModel.Month} 月 · {_viewModel.MonthTitle} · {_viewModel.StatusText} " +
        $"· 格子={_viewModel.Days.Count} · 选中={_viewModel.SelectedDateText}";

    /// <summary>给回归用：当前选中的日期。</summary>
    internal DateOnly SelectedDateForTest => _viewModel.SelectedDate;
}