using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using StickyTodo.App.Services;
using StickyTodo.App.ViewModels;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Localization;
using StickyTodo.Updates;

namespace StickyTodo.App.Views;

/// <summary>
/// 主窗体：无边框，左侧操作区 + 右侧看板卡片列表。
/// 点 X 收进托盘，退出走左侧「退出」按钮或托盘菜单。
/// </summary>
public partial class ManagerWindow : Window
{
    private readonly ManagerViewModel _viewModel;
    private readonly WindowManager _windows;
    private readonly GitHubUpdateChecker _updates;
    private bool _allowClose;
    private bool _checkingUpdate;

    private void ViewToggle_Click(object sender, RoutedEventArgs e) => _viewModel.ToggleViewMode();

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ManagerViewModel.GridMode))
        {
            ApplyViewMode();
        }
    }

    /// <summary>按当前模式上模板：行是 StackPanel + 横向条目，图标是 WrapPanel + 方形卡片。</summary>
    private void ApplyViewMode()
    {
        var grid = _viewModel.GridMode;
        BoardList.ItemsPanel = (ItemsPanelTemplate)FindResource(grid ? "BoardTilePanel" : "BoardRowPanel");
        BoardList.ItemTemplate = (DataTemplate)FindResource(grid ? "BoardTileTemplate" : "BoardRowTemplate");
        BoardList.ItemContainerStyle = (Style)FindResource(grid ? "BoardTileItemStyle" : "BoardRowItemStyle");
    }

    public ManagerWindow(ManagerViewModel viewModel, WindowManager windows, GitHubUpdateChecker updates)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _windows = windows;
        _updates = updates;
        DataContext = viewModel;

        // 看板列表的展示形式（行 / 图标网格）：跟着 VM 走，切换时重上模板与面板
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += (_, _) => ApplyViewMode();
        Closed += (_, _) => _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        Loaded += async (_, _) =>
        {
            _rendered = true;
            await _viewModel.LoadAsync();
            Motion.FadeInWindow(this);
        };

        // 管理面板常驻，里面的"待办/已完成"计数必须跟着数据变化刷新。
        // 之前只在 Loaded 里算一次，改了待办后数字一直停在旧值。
        Activated += async (_, _) =>
        {
            if (_rendered)
            {
                await _viewModel.LoadAsync();
            }
        };

        // 便签窗口打开/关闭时也刷新一次
        _windows.BoardsChanged += async (_, _) =>
        {
            if (_rendered)
            {
                await _viewModel.LoadAsync();
            }
        };

        // 整个窗口都能拖（含圆角外的那圈边距），只要不是点在交互控件上
        PreviewMouseLeftButtonDown += Window_PreviewMouseLeftButtonDown;
    }

    private bool _rendered;

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInteractive(source))
        {
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标提前抬起，忽略
        }
    }

    /// <summary>给回归脚本用：在第一张看板卡片上弹出右键菜单，返回菜单项文字。</summary>
    public IReadOnlyList<string> ShowFirstRowContextMenu()
    {
        BoardList.UpdateLayout();
        if (_viewModel.Boards.Count == 0)
        {
            return [];
        }

        var menu = BoardList.ContextMenu;
        if (menu is null)
        {
            return [];
        }

        _contextRow = _viewModel.Boards[0];
        BoardList.SelectedIndex = 0;

        menu.PlacementTarget = BoardList;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
        menu.IsOpen = true;

        return menu.Items
            .OfType<System.Windows.Controls.MenuItem>()
            .Select(i => i.Header?.ToString() ?? string.Empty)
            .Where(s => s.Length > 0)
            .ToList();
    }

    /// <summary>外部（比如演示数据种完之后）要求刷新列表。</summary>
    public async Task RefreshAsync()
    {
        await _viewModel.LoadAsync();
    }

    private Board? SelectedBoard => _viewModel.SelectedBoard?.Board;

    // ---------------- 标题栏按钮 ----------------

    /// <summary>
    /// 拖动窗体：只要鼠标按在"非交互控件"上就拖动。
    /// 之前只在标题栏绑定，导致列表区、空白区都拖不动。
    /// </summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 点在按钮 / 列表项 / 输入框上时不抢事件
        if (e.OriginalSource is DependencyObject source && IsInteractive(source))
        {
            return;
        }

        // 双击标题区 = 最大化/还原
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

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

    /// <summary>点到按钮/列表项上时不要把拖动抢走。</summary>
    private static bool IsInteractive(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            switch (current)
            {
                case System.Windows.Controls.Primitives.ButtonBase:
                case System.Windows.Controls.Primitives.TextBoxBase:
                case System.Windows.Controls.Primitives.ScrollBar:
                case System.Windows.Controls.ListBoxItem:
                    return true;
            }

            current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

        ApplyChromeForState();
    }

    /// <summary>最大化时去掉圆角/外圈留白，否则四角会露出桌面。</summary>
    private void ApplyChromeForState()
    {
        var maximized = WindowState == WindowState.Maximized;

        // 首次调用时把设计里的投影存下来，还原时要用同一个实例
        _shellEffect ??= Shell.Effect;

        Shell.Margin = maximized ? new Thickness(0) : new Thickness(12);
        Shell.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(14);
        Shell.Effect = maximized ? null : _shellEffect;
    }

    private System.Windows.Media.Effects.Effect? _shellEffect;

    // ---------------- 无边框窗口的边缘缩放 ----------------

    private const double ResizeBorder = 8d;

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
        if (msg != 0x0084 || WindowState == WindowState.Maximized || ResizeMode == ResizeMode.NoResize)
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

        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
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

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    private static void ExitApp()
    {
        var app = System.Windows.Application.Current;
        app?.Dispatcher.Invoke(() =>
        {
            // 先固化会话再退出（Shutdown 会先关窗口，晚了就记成空）
            (app as App)?.BeginShutdownForExit();
            app.Shutdown();
        });
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApp();

    private void OpenSettings_Click(object sender, RoutedEventArgs e) =>
        (System.Windows.Application.Current as App)?.ShowSettings();

    /// <summary>
    /// 检查更新：问 GitHub 仓库的最新 release，把结果丢给 <see cref="UpdateDialog"/>。
    /// 检查期间按钮变灰并显示「正在检查」，避免连点。
    /// </summary>
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingUpdate || sender is not Button button)
        {
            return;
        }

        _checkingUpdate = true;
        button.IsEnabled = false;
        button.SetBinding(
            ContentControl.ContentProperty,
            new Binding { Converter = (IValueConverter)FindResource("Loc"), ConverterParameter = "update.checking" });

        try
        {
            var result = await _updates.CheckAsync();
            UpdateDialog.ShowResult(this, result, _updates.Repository);
        }
        finally
        {
            // 恢复成走翻译器的绑定，切语言时这条文案还能跟着变
            button.SetBinding(
                ContentControl.ContentProperty,
                new Binding { Converter = (IValueConverter)FindResource("Loc"), ConverterParameter = "update.check" });
            button.IsEnabled = true;
            _checkingUpdate = false;
        }
    }

    // ---------------- 看板操作 ----------------

    private async void CreateTodo_Click(object sender, RoutedEventArgs e) =>
        await CreateBoardAsync(BoardKind.Todo);

    private async void CreateMemo_Click(object sender, RoutedEventArgs e) =>
        await CreateBoardAsync(BoardKind.Memo);

    private async void CreateCalendar_Click(object sender, RoutedEventArgs e) =>
        await CreateBoardAsync(BoardKind.Calendar);

    private async void CreateToken_Click(object sender, RoutedEventArgs e) =>
        await CreateBoardAsync(BoardKind.Token);

    private async void CreateWeather_Click(object sender, RoutedEventArgs e) =>
        await CreateBoardAsync(BoardKind.Weather);

    private async void CreateWorldClock_Click(object sender, RoutedEventArgs e) =>
        await CreateBoardAsync(BoardKind.WorldClock);

    private async void CreateDesktopClock_Click(object sender, RoutedEventArgs e) =>
        await CreateBoardAsync(BoardKind.DesktopClock);

    private async void CreateNetworkSpeed_Click(object sender, RoutedEventArgs e) =>
        await CreateBoardAsync(BoardKind.NetworkSpeed);

    private async void CreatePerformance_Click(object sender, RoutedEventArgs e) =>
        await CreateBoardAsync(BoardKind.Performance);

    /// <summary>
    /// 「+ AI 看板」不再直接建一块空看板：AI 看板的 HTML 和取数脚本是 AI 通过 MCP 写的，
    /// 面板这边能做的是把「怎么让 AI 帮你做一块」讲清楚，所以改成弹引导窗。
    /// （想装现成的可以走市场，见 <see cref="OpenMarketWindow"/>。）
    /// </summary>
    private void CreateCustom_Click(object sender, RoutedEventArgs e) => ShowAiBoardGuide();

    /// <summary>当前开着的引导窗（同一时刻只留一个，连点不会叠出一堆）。</summary>
    private AiBoardGuideWindow? _guideWindow;

    /// <summary>
    /// 弹出引导窗。原来「点了没反应」的真因不在 Owner，而在引导窗自己的 XAML：
    /// 那个示例话术的 <c>TextBox.Text</c> 用的是没有 Path 的 <c>{Binding Converter=…}</c>，
    /// 而 TextBox.Text 默认双向绑定 → <c>InitializeComponent()</c> 抛
    /// 「双向绑定需要 Path 或 XPath」，异常被这里的 catch 吃掉，于是界面上什么都看不到。
    /// 现在 XAML 修好了，并且：非模态 Show + Activate + 短暂置顶（不再用 ShowDialog
    /// 把整个管理面板禁掉），真出错时除了写日志，还把真实原因弹给用户看。
    /// </summary>
    private void ShowAiBoardGuide()
    {
        try
        {
            if (_guideWindow is { IsLoaded: true })
            {
                _guideWindow.Activate();
                return;
            }

            var guide = new AiBoardGuideWindow
            {
                // 引导窗里「逛市场」由管理面板去开（市场窗口的单例和 owner 都在这里）
                OpenMarket = OpenMarketWindow,
            };

            // Owner 只在管理面板真的可见时才挂：面板收进托盘 / 句柄已销毁时挂 Owner 会抛异常
            if (IsVisible)
            {
                guide.Owner = this;
            }
            else
            {
                guide.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            guide.Closed += (_, _) =>
            {
                if (ReferenceEquals(_guideWindow, guide))
                {
                    _guideWindow = null;
                }
            };

            _guideWindow = guide;
            guide.Show();
            guide.Activate();

            // 短暂置顶：保证它一定浮在管理面板上面，几百毫秒后放开（不长期抢别家窗口）
            guide.Topmost = true;
            var restore = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400),
            };
            restore.Tick += (_, _) =>
            {
                restore.Stop();
                guide.Topmost = false;
            };
            restore.Start();
        }
        catch (Exception ex)
        {
            _guideWindow = null;
            Serilog.Log.Error(ex, "[ai-board] 打开 AI 看板引导窗失败");

            // 只记日志的话，用户看到的就是「点了没反应」——必须把真实原因摆出来
            ShowGuideError(ex);
        }
    }

    /// <summary>把引导窗的真实错误弹给用户：XamlParseException 的原因藏在 InnerException 里。</summary>
    private void ShowGuideError(Exception ex)
    {
        var root = ex.GetBaseException();
        var detail = ReferenceEquals(root, ex)
            ? ex.Message
            : $"{ex.Message}{Environment.NewLine}→ {root.Message}";

        try
        {
            MessageBox.Show(
                this,
                $"{Loc.T("manager.aiGuideTitle")}{Environment.NewLine}{Environment.NewLine}{detail}",
                "StickyTodo",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception showError)
        {
            Serilog.Log.Error(showError, "[ai-board] 连错误提示都弹不出来");
        }
    }

    private static MarketWindow? _marketWindow;

    private void Market_Click(object sender, RoutedEventArgs e) => OpenMarketWindow();

    /// <summary>
    /// 打开看板市场（左侧「🧩 看板市场」按钮和 AI 看板引导窗里的入口都走这里，
    /// 单例窗口和 owner 都归管理面板管）。
    /// </summary>
    internal void OpenMarketWindow()
    {
        if (_marketWindow is { IsLoaded: true })
        {
            _marketWindow.Activate();
            return;
        }

        _marketWindow = new MarketWindow(_viewModel, _windows) { Owner = this };
        _marketWindow.Closed += (_, _) => _marketWindow = null;
        _marketWindow.Show();
    }

    private async Task CreateBoardAsync(BoardKind kind)
    {
        var (title, initial) = kind switch
        {
            BoardKind.Memo => (Loc.T("manager.newBoardTitle") + " · " + Loc.T("manager.kindMemo"), Loc.T("manager.newMemoDefault")),
            BoardKind.Calendar => (Loc.T("manager.newBoardTitle") + " · " + Loc.T("manager.kindCalendar"), Loc.T("manager.newCalendarDefault")),
            BoardKind.Token => (Loc.T("manager.newBoardTitle") + " · " + Loc.T("manager.kindToken"), Loc.T("token.title")),
            BoardKind.Weather => (Loc.T("manager.newBoardTitle") + " · " + Loc.T("manager.kindWeather"), Loc.T("manager.newWeatherDefault")),
            BoardKind.WorldClock => (Loc.T("manager.newBoardTitle") + " · " + Loc.T("manager.kindWorldClock"), Loc.T("manager.newWorldClockDefault")),
            BoardKind.DesktopClock => (Loc.T("manager.newBoardTitle") + " · " + Loc.T("manager.kindDesktopClock"), Loc.T("manager.newDesktopClockDefault")),
            BoardKind.NetworkSpeed => (Loc.T("manager.newBoardTitle") + " · " + Loc.T("manager.kindNetworkSpeed"), Loc.T("manager.newNetworkSpeedDefault")),
            BoardKind.Performance => (Loc.T("manager.newBoardTitle") + " · " + Loc.T("manager.kindPerformance"), Loc.T("manager.newPerformanceDefault")),
            BoardKind.Custom => (Loc.T("manager.newBoardTitle") + " · " + Loc.T("manager.kindCustom"), Loc.T("manager.newCustomDefault")),
            _ => (Loc.T("manager.newBoardTitle") + " · " + Loc.T("manager.kindTodo"), Loc.T("board.untitled")),
        };

        var name = PromptWindow.Prompt(this, title, Loc.T("manager.boardName"), initial);
        if (name is null)
        {
            return;
        }

        var board = await _viewModel.CreateBoardAsync(name, kind);
        if (board is not null)
        {
            _windows.OpenBoard(board);
        }
    }

    private async void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BoardRowViewModel row })
        {
            return;
        }

        await DeleteBoardAsync(row.Board);
    }

    // ---------------- 卡片右键菜单 ----------------

    private BoardRowViewModel? _contextRow;

    /// <summary>右键先把被点的那张卡片选中，菜单动作才能作用到正确的看板。</summary>
    private void BoardList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<System.Windows.Controls.ListBoxItem>(e.OriginalSource as DependencyObject);
        _contextRow = item?.DataContext as BoardRowViewModel;

        if (item is not null)
        {
            item.IsSelected = true;
        }
        else
        {
            e.Handled = true;
        }
    }

    private void BoardList_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (_contextRow is null)
        {
            e.Handled = true;
            return;
        }

        // 「换个图标」只对 AI 看板（Custom）有意义，其余类型的图形是固定的
        RowMenuIcon.Visibility = _contextRow.CanChangeIcon
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private BoardRowViewModel? MenuRow => _contextRow ?? _viewModel.SelectedBoard;

    private void RowMenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if (MenuRow is { } row)
        {
            _windows.OpenBoard(row.Board);
        }
    }

    private async void RowMenuRename_Click(object sender, RoutedEventArgs e)
    {
        if (MenuRow is not { } row)
        {
            return;
        }

        await RenameBoardAsync(row.Board);
    }

    private async void RowMenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (MenuRow is not { } row)
        {
            return;
        }

        await DeleteBoardAsync(row.Board);
    }

    /// <summary>「换个图标」：只对 AI 看板开放（右键菜单里其余行看不到这一项）。</summary>
    private void RowMenuIcon_Click(object sender, RoutedEventArgs e)
    {
        if (MenuRow is not { CanChangeIcon: true } row)
        {
            return;
        }

        ShowIconPicker(row);
    }

    /// <summary>当前张开的图标选择板（同一时刻只留一个）。</summary>
    private System.Windows.Controls.Primitives.Popup? _iconPicker;

    /// <summary>
    /// 图标选择板：12 个预设 emoji 平铺，点一下就写设置并重画这一行。
    /// 用 <see cref="System.Windows.Controls.Primitives.Popup"/> 而不是新窗口——
    /// 不需要额外窗口的生命周期，点外面自动收起，列表 / 图标网格两种视图走的是同一份菜单。
    /// </summary>
    private void ShowIconPicker(BoardRowViewModel row)
    {
        CloseIconPicker();

        var grid = new System.Windows.Controls.WrapPanel { Width = 208, Margin = new Thickness(2) };

        var popup = new System.Windows.Controls.Primitives.Popup
        {
            AllowsTransparency = true,
            StaysOpen = false,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            PlacementTarget = BoardList,
        };

        foreach (var icon in StickyTodo.Core.Services.AppSettingsService.BoardIconPresets)
        {
            var button = new Button
            {
                Content = icon,
                Width = 36,
                Height = 34,
                FontSize = 15,
                Margin = new Thickness(2),
                Style = (Style)FindResource("Button.Ghost"),
            };

            var chosen = icon;
            button.Click += (_, _) =>
            {
                CloseIconPicker();
                row.ChangeIcon(chosen);
            };

            grid.Children.Add(button);
        }

        popup.Child = new Border
        {
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(5),
            Background = (System.Windows.Media.Brush)FindResource("Brush.PaperOpaque"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("Brush.Line"),
            BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 3,
                Opacity = 0.28,
                Color = System.Windows.Media.Colors.Black,
                Direction = 270,
            },
            Child = grid,
        };

        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_iconPicker, popup))
            {
                _iconPicker = null;
            }
        };

        _iconPicker = popup;
        popup.IsOpen = true;
    }

    private void CloseIconPicker()
    {
        if (_iconPicker is not { } popup)
        {
            return;
        }

        _iconPicker = null;
        popup.IsOpen = false;
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedBoard is not { } board)
        {
            Warn(Loc.T("manager.pickFirst"));
            return;
        }

        await DeleteBoardAsync(board);
    }

    private async Task DeleteBoardAsync(Board board)
    {
        var confirmed = ConfirmWindow.Confirm(
            this,
            Loc.T("cal.deleteTitle"),
            Loc.F("manager.deleteConfirm", board.Name),
            Loc.T("common.delete"));

        if (!confirmed)
        {
            return;
        }

        var succeeded = await _viewModel.DeleteBoardAsync(board);
        if (succeeded)
        {
            _windows.CloseBoard(board.Id);
        }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedBoard is not { } board)
        {
            Warn(Loc.T("manager.pickFirst"));
            return;
        }

        await RenameBoardAsync(board);
    }

    /// <summary>重命名（左侧按钮和卡片右键共用）。</summary>
    private async Task RenameBoardAsync(Board board)
    {
        var name = PromptWindow.Prompt(this, Loc.T("manager.renameBoardTitle"), Loc.T("manager.boardName"), board.Name);
        if (name is null)
        {
            return;
        }

        await _viewModel.RenameBoardAsync(board, name);
        _windows.CloseBoard(board.Id);
        if (_viewModel.SelectedBoard is { } row)
        {
            _windows.OpenBoard(row.Board);
        }
    }

    private void BoardList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedBoard is not { } board)
        {
            return;
        }

        _windows.OpenBoard(board);
    }

    private void Warn(string message) =>
        ConfirmWindow.Notify(this, "StickyTodo", message);

    // ---------------- 生命周期 ----------------

    /// <summary>点 X 收进托盘；退出走左侧按钮或托盘菜单。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    public void RequestClose()
    {
        _allowClose = true;
        Close();
    }

    /// <summary>从便签窗口的「⋯」按钮唤起管理面板。</summary>
    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }
}
