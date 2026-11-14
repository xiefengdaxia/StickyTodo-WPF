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

        Loaded += async (_, _) => await InitializeWebAsync();
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

    /// <summary>把当前快照发给页面：board 元信息 + data/error 二选一。</summary>
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

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

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
