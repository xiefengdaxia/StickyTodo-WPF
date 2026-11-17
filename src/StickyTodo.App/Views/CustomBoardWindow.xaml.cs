using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using StickyTodo.App.Services;
using StickyTodo.App.ViewModels;
using StickyTodo.Core.Entities;
using StickyTodo.Infrastructure;
using StickyTodo.Mcp;

namespace StickyTodo.App.Views;

/// <summary>
/// 自定义看板窗口：AI 用 MCP 写的 HTML 在 WebView2 里渲染，
/// 采集器抓到的数据（或 AI 推送的数据）通过 postMessage 打给页面。
/// WebView2 运行时缺失时降级为纯文本显示采集数据。
/// </summary>
public partial class CustomBoardWindow : Window
{
    private readonly CustomBoardViewModel _viewModel;
    private readonly WindowGeometryMemory _geometry;
    private CollectorHost? _collector;
    private bool _webReady;

    public CustomBoardWindow(CustomBoardViewModel viewModel, Board board, WindowManager windows)
    {
        _viewModel = viewModel;
        _geometry = WindowGeometryMemory.Attach(this, board, windows, restoreSize: true);
        InitializeComponent();
        DataContext = viewModel;

        // 位置在窗口可见之前摆好（同天气窗口的做法）
        SourceInitialized += (_, _) =>
        {
            if (!_geometry.Restore())
            {
                ScreenHelper.FitIntoWorkArea(this);
            }

            _geometry.Arm();
        };

        Loaded += async (_, _) =>
        {
            await InitializeWebAsync();
            ApplyLockInteraction();
        };
        Closed += (_, _) => DetachCollector();
    }

    /// <summary>WindowManager 组装窗口后注入采集器（订阅数据变化 + 刷新按钮用）。</summary>
    public void AttachCollector(CollectorHost? collector)
    {
        DetachCollector();
        _collector = collector;
        if (_collector is null)
        {
            return;
        }

        _collector.DataChanged += OnCollectorDataChanged;
        Dispatcher.BeginInvoke(() =>
        {
            _viewModel.SetSnapshot(_collector.GetSnapshot(_viewModel.BoardId));
            PushDataToWeb();
        });
    }

    private void DetachCollector()
    {
        if (_collector is not null)
        {
            _collector.DataChanged -= OnCollectorDataChanged;
            _collector = null;
        }
    }

    private void OnCollectorDataChanged(object? sender, string boardId)
    {
        if (boardId != _viewModel.BoardId)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            _viewModel.SetSnapshot(_collector?.GetSnapshot(boardId));
            PushDataToWeb();
        });
    }

    private async Task InitializeWebAsync()
    {
        _viewModel.RefreshFromBoard();

        try
        {
            var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                userDataFolder: AppPaths.WebView2DataDirectory);
            await Web.EnsureCoreWebView2Async(environment);

            // 页面保存不了状态（opaque origin 下 localStorage 会被拒），改由宿主落盘
            Web.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            // WebView2 是独立的合成表面，Window.Opacity 管不到它 —— 整窗透明度得靠页面自己
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            // 看板是「展示面板」不是浏览器：弹窗和下载一律拦下
            Web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            Web.CoreWebView2.DownloadStarting += (_, e) => e.Cancel = true;
            Web.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                _webReady = true;
                PushDataToWeb();
            };

            _viewModel.WebViewFailed = false;
            await NavigateCurrentHtmlAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[custom] WebView2 初始化失败，降级为文本显示");
            _webReady = false;
            _viewModel.WebViewFailed = true;
            if (_collector is not null)
            {
                _viewModel.SetSnapshot(_collector.GetSnapshot(_viewModel.BoardId));
            }
        }
    }

    private async Task NavigateCurrentHtmlAsync()
    {
        var html = _viewModel.CurrentHtml ?? CustomBoardTemplate.BuildHtml(_viewModel.BoardName);
        if (html.Length > CustomBoardTemplate.MaxHtmlBytes)
        {
            Serilog.Log.Warning("[custom] HTML 超过 {Limit} 字节，拒绝加载", CustomBoardTemplate.MaxHtmlBytes);
            _viewModel.WebViewFailed = true;
            _viewModel.FallbackText = "HTML 超过大小限制（1MB），未加载。";
            return;
        }

        Web.NavigateToString(html);
        await Task.CompletedTask;
    }

    /// <summary>看板内容被 MCP 改过（WindowManager 转发，UI 线程）：HTML 变了就重新导航。</summary>
    public async void ReloadContent()
    {
        var changed = _viewModel.RefreshFromBoard();
        if (!_webReady)
        {
            await InitializeWebAsync();
            return;
        }

        if (changed)
        {
            await NavigateCurrentHtmlAsync();
        }

        PushDataToWeb();
    }

    /// <summary>把当前快照发给页面：board 元信息 + 页面界面状态 + data/error 二选一。</summary>
    private void PushDataToWeb()
    {
        if (!_webReady || Web.CoreWebView2 is null)
        {
            return;
        }

        var snapshot = _viewModel.Snapshot;
        try
        {
            Web.CoreWebView2.PostWebMessageAsJson(new JsonObject
            {
                ["type"] = "board",
                ["name"] = _viewModel.BoardName,
            }.ToJsonString());

            PushUiState();
            ApplyWebOpacity();
            ApplyWebBackground();

            if (snapshot is null)
            {
                return;
            }

            if (snapshot.Error is not null)
            {
                Web.CoreWebView2.PostWebMessageAsJson(new JsonObject
                {
                    ["type"] = "error",
                    ["message"] = snapshot.Error,
                }.ToJsonString());
                return;
            }

            if (snapshot.DataJson is not null)
            {
                Web.CoreWebView2.PostWebMessageAsJson(new JsonObject
                {
                    ["type"] = "data",
                    ["data"] = JsonNode.Parse(snapshot.DataJson),
                    ["fetchedAt"] = snapshot.FetchedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                }.ToJsonString());
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "[custom] 推送数据到看板失败");
        }
    }

    /// <summary>整窗不透明度变化时同步给网页（HwndHost 内容不参与 WPF 的 Opacity）。</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CustomBoardViewModel.WindowOpacity))
        {
            Dispatcher.BeginInvoke(ApplyWebOpacity);
        }
        else if (e.PropertyName == nameof(CustomBoardViewModel.CurrentOpacity))
        {
            Dispatcher.BeginInvoke(ApplyWebBackground);
        }
    }

    private void ApplyWebOpacity()
    {
        if (!_webReady || Web.CoreWebView2 is null)
        {
            return;
        }

        var alpha = Math.Clamp(_viewModel.WindowOpacity, 0.2, 1.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            _ = Web.CoreWebView2.ExecuteScriptAsync("document.documentElement.style.opacity='" + alpha + "'");
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "[custom] 同步网页透明度失败");
        }
    }

    /// <summary>看板「背景」透明度变化时同步给网页（网页自己画的卡片底，WPF 的纸面被它盖住了）。</summary>
    private void ApplyWebBackground()
    {
        if (!_webReady || Web.CoreWebView2 is null)
        {
            return;
        }

        var alpha = Math.Clamp(_viewModel.CurrentOpacity, 0.15, 1.0);
        var color = BrushColor(_viewModel.PaperBrush);
        try
        {
            // 只给页面一个 alpha 变量，由页面自己画圆角卡片。
            // 注意：不要再给 <html> 铺底色 —— WebView 是矩形 HWND，
            // 铺了底色就会盖住卡片圆角，看起来像"白色粗边框"。
            var script = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "document.documentElement.style.setProperty('--paper-alpha','{0:0.###}');",
                alpha);
            _ = Web.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "[custom] 同步网页背景透明度失败");
        }
    }

    /// <summary>把画笔压成纯色（渐变取第一段），背景只关心色相。</summary>
    private static System.Windows.Media.Color BrushColor(System.Windows.Media.Brush? brush) => brush switch
    {
        System.Windows.Media.SolidColorBrush solid => solid.Color,
        System.Windows.Media.GradientBrush gradient when gradient.GradientStops.Count > 0 => gradient.GradientStops[0].Color,
        _ => System.Windows.Media.Colors.White,
    };

    private void PushUiState()
    {
        var saved = BoardUiStateStore.Load(_viewModel.BoardId);
        if (string.IsNullOrWhiteSpace(saved) || Web.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            Web.CoreWebView2.PostWebMessageAsJson(new JsonObject
            {
                ["type"] = "ui",
                ["state"] = JsonNode.Parse(saved),
            }.ToJsonString());
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "[custom] 推送看板界面状态失败");
        }
    }

    /// <summary>页面把界面状态（区间、勾选…）交回来，宿主落盘，下次打开再推回去。</summary>
    private void OnWebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var text = e.TryGetWebMessageAsString();
            if (string.IsNullOrWhiteSpace(text) || text.Length > BoardUiStateStore.MaxBytes)
            {
                return;
            }

            using var document = System.Text.Json.JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) ||
                type.GetString() != "ui.state" ||
                !root.TryGetProperty("state", out var state))
            {
                return;
            }

            BoardUiStateStore.Save(_viewModel.BoardId, state.GetRawText());
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "[custom] 保存看板界面状态失败");
        }
    }

    private UnlockChipWindow? _unlockChip;

    /// <summary>
    /// 免控件的缩放：拦截 <c>WM_NCHITTEST</c>，光标贴近窗口边缘时直接告诉系统
    /// “这里是左/右/上/下边框”，由 Windows 自己完成拖拽缩放。
    ///
    /// 这样做不往界面上画任何东西（之前那套隐形 Thumb 必须占一圈纸面，结果就是白边），
    /// 也不会被 WebView2 的 HWND 挡住——命中测试发生在父窗口的窗口过程里。
    /// </summary>
    private const int WmNcHitTest = 0x0084;
    private const int ResizeGripDip = 14;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source)
        {
            source.AddHook(NcHitTestHook);
        }
    }

    private IntPtr NcHitTestHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmNcHitTest || WindowState != WindowState.Normal || ResizeMode == ResizeMode.NoResize || _viewModel.IsLocked)
        {
            return IntPtr.Zero;
        }

        try
        {
            // lParam = 屏幕坐标（低 16 位 x，高 16 位 y）
            var packed = lParam.ToInt64();
            var screenX = (short)(packed & 0xFFFF);
            var screenY = (short)((packed >> 16) & 0xFFFF);
            var point = PointFromScreen(new Point(screenX, screenY));

            const double grip = ResizeGripDip;
            var left = point.X <= grip;
            var right = point.X >= ActualWidth - grip;
            var top = point.Y <= grip;
            var bottom = point.Y >= ActualHeight - grip;

            int? hit = (left, right, top, bottom) switch
            {
                (true, _, true, _) => 13,   // HTTOPLEFT
                (_, true, true, _) => 14,   // HTTOPRIGHT
                (true, _, _, true) => 16,   // HTBOTTOMLEFT
                (_, true, _, true) => 17,   // HTBOTTOMRIGHT
                (true, _, _, _) => 10,      // HTLEFT
                (_, true, _, _) => 11,      // HTRIGHT
                (_, _, true, _) => 12,      // HTTOP
                (_, _, _, true) => 15,      // HTBOTTOM
                _ => (int?)null,
            };

            if (hit is null)
            {
                return IntPtr.Zero;
            }

            handled = true;
            return new IntPtr(hit.Value);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>八个隐形热区拖边改大小（WindowStyle=None 的窗口没有系统边框）。</summary>
    private void ResizeThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (WindowState != WindowState.Normal || sender is not FrameworkElement thumb)
        {
            return;
        }

        var tag = thumb.Tag as string ?? string.Empty;
        var dx = e.HorizontalChange;
        var dy = e.VerticalChange;
        var width = ActualWidth;
        var height = ActualHeight;
        var left = Left;
        var top = Top;

        if (tag.Contains('L')) { width -= dx; left += dx; }
        if (tag.Contains('R')) { width += dx; }
        if (tag.Contains('T')) { height -= dy; top += dy; }
        if (tag.Contains('B')) { height += dy; }

        if (width < MinWidth)
        {
            if (tag.Contains('L')) { left -= MinWidth - width; }
            width = MinWidth;
        }

        if (height < MinHeight)
        {
            if (tag.Contains('T')) { top -= MinHeight - height; }
            height = MinHeight;
        }

        Width = width;
        Height = height;
        if (tag.Contains('L')) { Left = left; }
        if (tag.Contains('T')) { Top = top; }
    }

    /// <summary>标题栏 ↻：立刻重跑一次采集器（页面里自己的刷新不受影响）。</summary>
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_collector is null)
        {
            _viewModel.StatusText = "这个看板没有采集器。";
            return;
        }

        RefreshButton.IsEnabled = false;
        try
        {
            await _collector.RunNowAsync(_viewModel.BoardId);
            _viewModel.SetSnapshot(_collector.GetSnapshot(_viewModel.BoardId));
            PushDataToWeb();
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void ToggleTopmost_Click(object sender, RoutedEventArgs e) => _viewModel.ToggleTopmost();

    private void ToggleLock_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ToggleLock();
        ApplyLockInteraction();
    }

    /// <summary>锁定 = 置灰 + 鼠标穿透；靠右上角的小锁徽标（或 Ctrl+Alt+L）解锁。</summary>
    private void ApplyLockInteraction()
    {
        var locked = _viewModel.IsLocked;
        ClickThrough.Apply(this, locked);
        Web.IsHitTestVisible = !locked;

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

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>标题栏 ☰：打开看板管理面板（和其它看板同一个入口）。</summary>
    private void OpenManager_Click(object sender, RoutedEventArgs e) =>
        (System.Windows.Application.Current.MainWindow as ManagerWindow)?.ShowAndActivate();

    // ---------------- 外观：皮肤 + 透明度（🎨 按钮，与待办/便签看板共用一套菜单） ----------------

    private void Appearance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        AppearanceMenu.Show(element, _viewModel, _viewModel.Windows);
        e.Handled = true;
    }

    /// <summary>皮肤/透明度被别的窗口改过之后刷新绑定值（WindowManager 调）。</summary>
    public void RefreshAppearance() => _viewModel.RefreshAppearance();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source && IsInteractiveControl(source))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleLock();
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove 只允许在左键按下时调用，事件重入时可能抛——忽略即可
        }
    }

    private static bool IsInteractiveControl(DependencyObject source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Button)
            {
                return true;
            }

            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void ToggleLock()
    {
        // 自定义看板不做穿透锁定（里面的网页要响应鼠标），双击标题栏无操作
    }
}
