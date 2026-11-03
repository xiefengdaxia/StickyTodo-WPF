using System.ComponentModel;
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

    // 锁定状态变化（按钮 / 热键 / 托盘）：应用穿透 + 解锁徽标
    Opened += (_, _) => ApplyLockInteraction();
    DataContextChanged += (_, _) => HookLockNotifications();
    Closed += (_, _) =>
    {
        ClickThrough.Apply(this, false);
        _unlockChip?.Close();
    };
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

    private async void Close_Click(object? sender, RoutedEventArgs e)
    {
        _saveTimer.Stop();
        await _viewModel.SaveIfDirtyAsync();
        Close();
    }
}
