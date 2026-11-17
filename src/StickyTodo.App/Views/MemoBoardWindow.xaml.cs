using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StickyTodo.App.Services;
using StickyTodo.App.ViewModels;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Localization;

namespace StickyTodo.App.Views;

/// <summary>
/// 便签/随笔窗口：无边框半透明 + 一整块可写的纸，停止输入 800ms 后自动保存。
///
/// 两种显示方式：编辑（原来的 TextBox，边写边存）和预览（把同一份 Markdown
/// 用 <see cref="MarkdownRenderer"/> 渲染成只读的 FlowDocument）。切换只换显示，正文不动。
/// </summary>
public partial class MemoBoardWindow : Window, IStickyWindow, IBoardDataRefresh
{
    private const double ShadowMargin = 10d;
    private const double ResizeBorder = 8d;
    private const int CascadeStep = 28;

    private readonly MemoBoardViewModel _viewModel;
    private readonly WindowManager _windows;
    private readonly DispatcherTimer _saveTimer;

    /// <summary>MCP 改了数据后让窗口重读一遍（IBoardDataRefresh）。</summary>
    public Task ReloadDataAsync() => _viewModel.LoadAsync();
    private bool _geometryRestored;
    private bool _isFullScreen;

    public MemoBoardWindow(MemoBoardViewModel viewModel, WindowManager windows)
    {
    InitializeComponent();

        // 锁定状态变化（按钮 / 热键 / 托盘都走这里）：句柄就绪后应用穿透 + 解锁徽标
        SourceInitialized += (_, _) => ApplyLockInteraction();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is INotifyPropertyChanged vm)
            {
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == "IsLocked")
                    {
                        ApplyLockInteraction();
                    }
                };
            }
        };
        Closed += (_, _) =>
        {
            ClickThrough.Reset(this);
            _unlockChip?.Close();
        };
        _viewModel = viewModel;
        _windows = windows;
        DataContext = viewModel;

        AttachEditingCommands();
        AttachModeWatcher();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await _viewModel.SaveIfDirtyAsync();
        };

        Loaded += async (_, _) =>
        {
            RestoreGeometry();
            await _viewModel.LoadAsync();

            RefreshModeTabs();
            if (_viewModel.IsPreview)
            {
                RebuildPreview();
            }
            else if (!_viewModel.IsLocked)
            {
                Editor.Focus();
            }

            Motion.FadeInWindow(this);
        };

        Closing += async (_, _) =>
        {
            _saveTimer.Stop();
            SaveGeometry();
            await _viewModel.SaveIfDirtyAsync();
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

    // ---------------- 编辑 / 预览 ----------------

    /// <summary>两边的高亮靠换样式（SegmentTab / SegmentTab.Active），和待办窗口的分段按钮一致。</summary>
    private void RefreshModeTabs()
    {
        var preview = _viewModel.IsPreview;
        EditTab.Style = (Style)FindResource(preview ? "SegmentTab" : "SegmentTab.Active");
        PreviewTab.Style = (Style)FindResource(preview ? "SegmentTab.Active" : "SegmentTab");
    }

    private void EditTab_Click(object sender, RoutedEventArgs e) => SwitchMode(preview: false);

    private void PreviewTab_Click(object sender, RoutedEventArgs e) => SwitchMode(preview: true);

    /// <summary>切换显示方式：正文一个字都不动，只换控件。</summary>
    private void SwitchMode(bool preview)
    {
        if (_viewModel.IsPreview == preview)
        {
            return;
        }

        _viewModel.SetPreview(preview);
        RefreshModeTabs();

        if (preview)
        {
            RebuildPreview();
        }
        else if (!_viewModel.IsLocked)
        {
            // 回到编辑：光标放到正文末尾，接着写
            Editor.Focus();
            Editor.CaretIndex = Editor.Text.Length;
        }
    }

    /// <summary>把当前正文渲染成预览文档。渲染失败只记日志，不留白屏。</summary>
    private void RebuildPreview()
    {
        try
        {
            Previewer.Document = MarkdownRenderer.ToFlowDocument(_viewModel.Content);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[memo] 预览渲染失败");
        }
    }

    /// <summary>正文被外部改掉（MCP 刷新）或切到预览时，预览要跟着重画。</summary>
    private void AttachModeWatcher()
    {
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MemoBoardViewModel.IsPreview))
            {
                RefreshModeTabs();
            }
            else if (e.PropertyName == nameof(MemoBoardViewModel.Content) && _viewModel.IsPreview)
            {
                RebuildPreview();
            }
        };
    }

    // ---------------- 剪切 / 复制 / 粘贴 / 全选 / 撤销 / 重做 ----------------

    /// <summary>
    /// 窗口级接管编辑命令。
    ///
    /// WPF 的 Ctrl+X 走 <see cref="ApplicationCommands.Cut"/>，命令是「从焦点元素往上冒泡」找
    /// 执行目标的：只有焦点正好在 TextBox 上，才由 TextBoxBase 自己的类级 CommandBinding 接住。
    /// 便签窗口里焦点经常不在正文上（点过标题栏 / 顶部按钮 / 底栏之后焦点落在 Button 或窗口本身；
    /// 预览模式焦点还在只读文档上），这时 Cut 找不到目标，被静默丢掉——
    /// 表现就是「Ctrl+C / Ctrl+V 有时好使，Ctrl+X 干脆没反应」。
    ///
    /// 这里在窗口级同时挂 CommandBinding 和 KeyBinding，把 6 个编辑命令固定路由到正文控件
    /// （预览模式则路由到只读文档的选区），不再依赖「焦点恰好落在谁身上」。
    /// 其它按键（F1 截图等）一概不碰。
    /// </summary>
    private void AttachEditingCommands()
    {
        BindEditingCommand(ApplicationCommands.Cut, Key.X, CutSelection);
        BindEditingCommand(ApplicationCommands.Copy, Key.C, CopySelection);
        BindEditingCommand(ApplicationCommands.Paste, Key.V, PasteClipboard);
        BindEditingCommand(ApplicationCommands.SelectAll, Key.A, SelectAllContent);
        BindEditingCommand(ApplicationCommands.Undo, Key.Z, UndoEdit);
        BindEditingCommand(ApplicationCommands.Redo, Key.Y, RedoEdit);
    }

    private void BindEditingCommand(RoutedUICommand command, Key key, Func<bool> action)
    {
        CommandBindings.Add(new CommandBinding(command, (_, e) =>
        {
            if (action())
            {
                e.Handled = true;
            }
        }));

        InputBindings.Add(new KeyBinding(command, key, ModifierKeys.Control));
    }

    /// <summary>现在能不能改正文：预览 / 锁定都算不能改。</summary>
    private bool CanEdit => !_viewModel.IsPreview && !_viewModel.IsLocked;

    private bool CutSelection()
    {
        if (!CanEdit)
        {
            return false;
        }

        if (Editor.SelectionLength == 0)
        {
            return false;
        }

        Editor.Cut();
        return true;
    }

    private bool CopySelection()
    {
        if (_viewModel.IsPreview)
        {
            CopyPreviewSelection();
            return true;
        }

        if (Editor.SelectionLength == 0)
        {
            return false;
        }

        Editor.Copy();
        return true;
    }

    private bool PasteClipboard()
    {
        // 焦点本来就在正文里：让 TextBox 自己处理，免得粘两遍
        if (!CanEdit || Editor.IsKeyboardFocusWithin || !HasClipboardText())
        {
            return false;
        }

        Editor.Paste();
        return true;
    }

    private bool SelectAllContent()
    {
        if (_viewModel.IsPreview)
        {
            if (Previewer.Document is not { } document)
            {
                return false;
            }

            Previewer.Selection.Select(document.ContentStart, document.ContentEnd);
            Previewer.Focus();
            return true;
        }

        Editor.SelectAll();
        Editor.Focus();
        return true;
    }

    /// <summary>剪贴板里有没有文本（剪贴板被别的进程锁着时别抛到用户脸上）。</summary>
    private static bool HasClipboardText()
    {
        try
        {
            return Clipboard.ContainsText();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool UndoEdit()
    {
        // 焦点在正文里就走 TextBox 自己的撤销栈，别撤销两次
        if (!CanEdit || Editor.IsKeyboardFocusWithin || !Editor.CanUndo)
        {
            return false;
        }

        Editor.Undo();
        return true;
    }

    private bool RedoEdit()
    {
        if (!CanEdit || Editor.IsKeyboardFocusWithin || !Editor.CanRedo)
        {
            return false;
        }

        Editor.Redo();
        return true;
    }

    /// <summary>预览模式下 Ctrl+C：抠出选区（没选区就整篇）的纯文本。</summary>
    private void CopyPreviewSelection()
    {
        if (Previewer.Document is not { } document)
        {
            return;
        }

        TextRange range = Previewer.Selection is { IsEmpty: false } selection
            ? selection
            : new TextRange(document.ContentStart, document.ContentEnd);

        var text = range.Text.Trim();
        if (text.Length > 0)
        {
            TrySetClipboard(text);
        }
    }

    /// <summary>剪贴板常被别的进程占着，占一次就抛异常；重试几次，失败就算了。</summary>
    private static bool TrySetClipboard(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (Exception)
            {
                System.Threading.Thread.Sleep(30);
            }
        }

        return false;
    }

    // ---------------- 导出 ----------------

    /// <summary>点标题栏的 ⤓：把三个导出选项弹在按钮下面。</summary>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } element)
        {
            return;
        }

        menu.PlacementTarget = element;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private async void ExportMarkdown_Click(object sender, RoutedEventArgs e) =>
        await ExportAsync(ExportFormat.Markdown);

    private async void ExportHtml_Click(object sender, RoutedEventArgs e) =>
        await ExportAsync(ExportFormat.Html);

    private async void ExportText_Click(object sender, RoutedEventArgs e) =>
        await ExportAsync(ExportFormat.Text);

    private async Task ExportAsync(ExportFormat format)
    {
        try
        {
            _viewModel.ErrorText = null;

            // 先把还没落库的正文存掉，导出的一定是最新内容
            await _viewModel.SaveIfDirtyAsync();

            var content = _viewModel.Content;
            var path = ExportService.AskSavePath(this, _viewModel.BoardName, format);
            if (path is null)
            {
                return;
            }

            var text = format switch
            {
                ExportFormat.Markdown => ExportService.MemoMarkdown(content),
                ExportFormat.Html => ExportService.MemoHtml(content, _viewModel.BoardName),
                _ => ExportService.MemoText(content),
            };

            await Task.Run(() => ExportService.WriteText(path, text));
            _viewModel.SetNotice(Loc.F("memo.exported", path));
        }
        catch (Exception ex)
        {
            _viewModel.ErrorText = ex.Message;
        }
    }

    // ---------------- 新建便签 ----------------

    /// <summary>标题栏的 ＋：建一张新便签并直接打开，不经过管理面板。</summary>
    private void NewMemo_Click(object sender, RoutedEventArgs e)
    {
        var board = _viewModel.CreateSiblingMemo();
        if (board is null)
        {
            return;
        }

        _windows.OpenBoard(board);
    }

    // ---------------- 正文与窗口 ----------------

    private void Editor_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _viewModel.MarkDirty();
        _saveTimer.Stop();
        _saveTimer.Start();
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

private void ToggleLock_Click(object sender, RoutedEventArgs e)
{
    _viewModel.ToggleLock();
    ApplyLockInteraction();
}

private UnlockChipWindow? _unlockChip;

/// <summary>
/// 锁定 = 只供查看：窗口设成鼠标穿透（点下去的是下面那层窗口/桌面），
/// 只在原「锁定」按钮位置浮一个解锁徽标，整个看板里只有它能点。
/// </summary>
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

    if (_unlockChip is { IsLoaded: true })
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
    _unlockChip.FollowAnchor();
}

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

    // ---------------- 位置记忆（便签窗口不参与吸附，避免两张纸互相粘住） ----------------

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
        var (cascadeLeft, cascadeTop) = ScreenHelper.ClampIntoWorkArea(this, 80 + offset, 80 + offset, Width, Height);
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
        _saveTimer.Stop();
        SaveGeometry();
        base.OnClosing(e);
    }
}
