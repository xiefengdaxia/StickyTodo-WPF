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

    private void HideDetail_Click(object? sender, RoutedEventArgs e) => _viewModel.HideDetail();

    private void ToggleTopmost_Click(object? sender, RoutedEventArgs e) => _viewModel.ToggleTopmost();

    private void ToggleLock_Click(object? sender, RoutedEventArgs e) => _viewModel.ToggleLock();

    private void Appearance_Click(object? sender, RoutedEventArgs e) => ShowAppearanceMenu((Control)sender!);

    private void OpenManager_Click(object? sender, RoutedEventArgs e) => App.Services?.ShowManager();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
