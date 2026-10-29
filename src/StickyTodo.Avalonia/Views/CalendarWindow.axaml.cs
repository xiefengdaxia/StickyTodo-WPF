using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using StickyTodo.Core.Entities;
using StickyTodo.Notes.Services;
using StickyTodo.Notes.ViewModels;

namespace StickyTodo.Notes.Views;

/// <summary>日历看板：月视图 + 农历/节气/节假日 + 当日日程 + 黄历。</summary>
public partial class CalendarWindow : StickyWindowBase
{
    private const string DragFormat = "stickytodo/todo-id";

    private readonly CalendarViewModel _viewModel;
    private bool _suppressSelection;

    public CalendarWindow(CalendarViewModel viewModel, WindowManager windows)
        : base(windows, App.Services!.Settings)
    {
        _viewModel = viewModel;

        InitializeComponent();
        DataContext = viewModel;

        InitStickyWindow(TitleBar, NoteRoot);
    }

    protected override Board Board => _viewModel.Board;

    protected override IAppearanceHost Appearance => _viewModel;

    protected override async Task OnWindowLoadedAsync()
    {
        await RefreshSelectionAsync();
        await _viewModel.LoadAsync();
    }

    public override void ToggleLock() => _viewModel.ToggleLock();

    private async void PrevMonth_Click(object? sender, RoutedEventArgs e)
    {
        await _viewModel.GoToMonthAsync(-1);
        await RefreshSelectionAsync();
    }

    private async void NextMonth_Click(object? sender, RoutedEventArgs e)
    {
        await _viewModel.GoToMonthAsync(1);
        await RefreshSelectionAsync();
    }

    private async void Today_Click(object? sender, RoutedEventArgs e) => await GoToTodayAsync();

    /// <summary>跳回今天（桌面时钟的"今日日程"点开时也走这里）。</summary>
    public async Task GoToTodayAsync()
    {
        await _viewModel.GoToTodayAsync();
        await RefreshSelectionAsync();
    }

    private async void YearBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || YearBox.SelectedItem is not int year)
        {
            return;
        }

        await _viewModel.SelectYearAsync(year);
    }

    private async void MonthBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || MonthBox.SelectedItem is not int month)
        {
            return;
        }

        await _viewModel.SelectMonthAsync(month);
    }

    /// <summary>把下拉框选中项与 ViewModel 对齐（防止刷新后跳回旧值）。</summary>
    private async Task RefreshSelectionAsync()
    {
        _suppressSelection = true;
        try
        {
            YearBox.SelectedItem = _viewModel.Year;
            MonthBox.SelectedItem = _viewModel.Month;
        }
        finally
        {
            _suppressSelection = false;
        }

        await Task.CompletedTask;
    }

    // ---------------- 日期格子 ----------------

    private void Cell_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: CalendarDayViewModel day })
        {
            return;
        }

        _viewModel.SelectDay(day);

        // 从当天条目上拖动 = 把待办挪到别的日期
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && day.HasEntries)
        {
            var entry = day.Entries.FirstOrDefault();
            if (entry is not null)
            {
                _ = StartEntryDragAsync(e, entry);
            }
        }
    }

    private async Task StartEntryDragAsync(PointerEventArgs e, CalendarEntryViewModel entry)
    {
        var data = new DataObject();
        data.Set(DragFormat, entry.TodoId);

        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    private void Cell_DragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DragFormat))
        {
            e.DragEffects = DragDropEffects.Move;
        }
    }

    private async void Cell_Drop(object? sender, DragEventArgs e)
    {
        if (sender is not Control { Tag: CalendarDayViewModel day })
        {
            return;
        }

        if (e.Data.Get(DragFormat) is not string todoId)
        {
            return;
        }

        var entry = _viewModel.SelectedDayEntries.FirstOrDefault(x => x.TodoId == todoId)
                    ?? _viewModel.Days.SelectMany(d => d.Entries).FirstOrDefault(x => x.TodoId == todoId);

        if (entry is null)
        {
            return;
        }

        await _viewModel.MoveEntryToDateAsync(entry, day.Date);
        e.Handled = true;
    }

    // ---------------- 当日条目 ----------------

    private void Entry_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: CalendarEntryViewModel entry })
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _ = StartEntryDragAsync(e, entry);
        }
    }

    private async void EntryCheck_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: CalendarEntryViewModel entry } box)
        {
            return;
        }

        // 复选框只在点击瞬间看一次目标状态，随后由数据刷新决定真实显示
        await _viewModel.ToggleEntryAsync(entry, box.IsChecked == true);
    }

    private async void DeleteEntry_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: CalendarEntryViewModel entry })
        {
            await _viewModel.DeleteEntryAsync(entry);
        }
    }

    // ---------------- 标题栏 ----------------

    private void ToggleTopmost_Click(object? sender, RoutedEventArgs e) => _viewModel.ToggleTopmost();

    private void ToggleLock_Click(object? sender, RoutedEventArgs e) => _viewModel.ToggleLock();

    private void Appearance_Click(object? sender, RoutedEventArgs e) => ShowAppearanceMenu((Control)sender!);

    private void OpenManager_Click(object? sender, RoutedEventArgs e) => App.Services?.ShowManager();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
