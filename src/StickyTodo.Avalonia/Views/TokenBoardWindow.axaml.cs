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

    private void Appearance_Click(object? sender, RoutedEventArgs e) => ShowAppearanceMenu((Control)sender!);

    private void OpenManager_Click(object? sender, RoutedEventArgs e) => App.Services?.ShowManager();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
