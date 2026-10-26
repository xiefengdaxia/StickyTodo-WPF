using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using StickyTodo.Core.Entities;
using StickyTodo.Notes.Services;
using StickyTodo.Notes.ViewModels;

namespace StickyTodo.Notes.Views;

/// <summary>便签/随笔看板：整块区域就是一张白纸，边写边自动保存。</summary>
public partial class MemoBoardWindow : StickyWindowBase
{
    private readonly MemoBoardViewModel _viewModel;
    private readonly DispatcherTimer _saveTimer;
    private bool _loading;

    public MemoBoardWindow(MemoBoardViewModel viewModel, WindowManager windows)
        : base(windows, App.Services!.Settings)
    {
        _viewModel = viewModel;

        InitializeComponent();
        DataContext = viewModel;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await _viewModel.SaveIfDirtyAsync();
        };

        InitStickyWindow(TitleBar, NoteRoot);
    }

    protected override Board Board => _viewModel.Board;

    protected override IAppearanceHost Appearance => _viewModel;

    protected override async Task OnWindowLoadedAsync()
    {
        _loading = true;
        await _viewModel.LoadAsync();
        _loading = false;
    }

    public override void ToggleLock() => _viewModel.ToggleLock();

    private void Editor_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _viewModel.MarkDirty();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void ToggleTopmost_Click(object? sender, RoutedEventArgs e) => _viewModel.ToggleTopmost();

    private void ToggleLock_Click(object? sender, RoutedEventArgs e) => _viewModel.ToggleLock();

    private void Appearance_Click(object? sender, RoutedEventArgs e) => ShowAppearanceMenu((Control)sender!);

    private void OpenManager_Click(object? sender, RoutedEventArgs e) => App.Services?.ShowManager();

    private async void Close_Click(object? sender, RoutedEventArgs e)
    {
        _saveTimer.Stop();
        await _viewModel.SaveIfDirtyAsync();
        Close();
    }
}
