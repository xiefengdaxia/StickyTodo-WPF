using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using StickyTodo.Core.Entities;
using StickyTodo.Notes.Services;
using StickyTodo.Notes.ViewModels;

namespace StickyTodo.Notes.Views;

/// <summary>Token 用量看板：范围切换 + 自动刷新 + 趋势图 + 按模型明细。</summary>
public partial class TokenBoardWindow : StickyWindowBase
{
    private readonly TokenBoardViewModel _viewModel;

    public TokenBoardWindow(TokenBoardViewModel viewModel, WindowManager windows)
        : base(windows, App.Services!.Settings)
    {
        _viewModel = viewModel;

    InitializeComponent();

    // 锁定状态变化（按钮 / 热键 / 托盘）：应用穿透 + 解锁徽标
    Opened += (_, _) => ApplyLockInteraction();
    DataContextChanged += (_, _) => HookLockNotifications();
    Closed += (_, _) =>
    {
        ClickThrough.Apply(this, false);
        _unlockChip?.Close();
    };
        DataContext = viewModel;

        // 复制按钮需要访问剪贴板，剪贴板挂在 TopLevel 上，
        // 所以等窗口建好后把自身交给 ViewModel。
        _viewModel.AttachOwner(this);

        InitStickyWindow(TitleBar, NoteRoot);

        Chart.PointPicked += (_, index) =>
        {
            if (index < 0)
            {
                _viewModel.HideDetail();
            }
            else
            {
                _viewModel.ShowPointDetail(index);
            }
        };

        // 悬停 = 点击：指针扫过哪一根柱/点，就显示它的明细
        Chart.PointHovered += (_, index) => _viewModel.ShowPointDetail(index);

        Closed += (_, _) => _viewModel.Dispose();
    }

    protected override Board Board => _viewModel.Board;

    protected override IAppearanceHost Appearance => _viewModel;

    protected override async Task OnWindowLoadedAsync()
    {
        await _viewModel.LoadAsync();
    }

    public override void ToggleLock() => _viewModel.ToggleLock();

    private void Range_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: TokenRangeOption option })
        {
            _viewModel.SelectRange(option.Days);
        }
    }

    private void Interval_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: TokenIntervalOption option })
        {
            _viewModel.SelectInterval(option.Seconds);
        }
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e) => _viewModel.RefreshCommand.Execute(null);

    private void Agent_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // TwoWay 绑定已把 SelectedAgent 写回；这里只负责触发取数。
        // 初始化期间的 SelectionChanged 会被 SelectAgent 里的"同一选项"判断挡掉。
        if (_viewModel.SelectedAgent is { } agent)
        {
            _viewModel.ApplyAgentChange(agent);
        }
    }

    private void Copy_Click(object? sender, RoutedEventArgs e) => _viewModel.CopyToClipboardCommand.Execute(null);

    private void HideDetail_Click(object? sender, RoutedEventArgs e) => _viewModel.HideDetail();

    /// <summary>热力图点某天：与趋势图同一条明细链路。</summary>
    private void HeatMap_DayPicked(object? sender, int seriesIndex)
        => _viewModel.ShowPointDetail(seriesIndex);

    private void ToggleTopmost_Click(object? sender, RoutedEventArgs e) => _viewModel.ToggleTopmost();

private void ToggleLock_Click(object? sender, RoutedEventArgs e) => _viewModel.ToggleLock();

private UnlockChipWindow? _unlockChip;

/// <summary>
/// 锁定 = 只供查看：窗口设成鼠标穿透（点下去的是下面那层），
/// 只在原「锁定」按钮位置浮一个解锁徽标，整个看板里只有它能点。
/// </summary>
    /// <summary>锁定按钮之外的路径（热键 / 托盘）改锁定状态时也要联动。</summary>
    private void HookLockNotifications()
    {
        if (DataContext is System.ComponentModel.INotifyPropertyChanged vm)
        {
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == "IsLocked")
                {
                    ApplyLockInteraction();
                }
            };
        }
    }

private void ApplyLockInteraction()
{
    var locked = _viewModel.IsLocked;
    ClickThrough.Apply(this, locked);

    if (!locked)
    {
        _unlockChip?.Close();
        _unlockChip = null;
        return;
    }

    if (_unlockChip is { IsVisible: true })
    {
        return;
    }

    _unlockChip = new UnlockChipWindow(this, LockButton, () =>
    {
        if (_viewModel.IsLocked)
        {
            _viewModel.ToggleLock();
        }

        ApplyLockInteraction();
    });
    _unlockChip.Closed += (_, _) => _unlockChip = null;
    _unlockChip.Show();
    _unlockChip.Follow();
}

    private void Appearance_Click(object? sender, RoutedEventArgs e) => ShowAppearanceMenu((Control)sender!);

    private void OpenManager_Click(object? sender, RoutedEventArgs e) => App.Services?.ShowManager();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
