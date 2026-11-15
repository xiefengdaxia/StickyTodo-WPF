using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using StickyTodo.Core.Entities;
using StickyTodo.Infrastructure;
using StickyTodo.Mcp;
using StickyTodo.Notes.Services;
using StickyTodo.Notes.ViewModels;

namespace StickyTodo.Notes.Views;

/// <summary>
/// 自定义看板窗口：AI 用 MCP 写的 HTML 在 WebView2 里渲染（NativeControlHost 嵌入），
/// 采集器抓到的数据（或 AI 推送的数据）通过 postMessage 打给页面。
/// WebView2 运行时缺失时降级为纯文本显示采集数据。
/// </summary>
public partial class CustomBoardWindow : Window
{
    private readonly CustomBoardViewModel _viewModel;
    private readonly WindowGeometryMemory _geometry;
    private CollectorHost? _collector;

    public CustomBoardWindow(CustomBoardViewModel viewModel, Board board, WindowManager windows)
    {
        _viewModel = viewModel;
        _geometry = WindowGeometryMemory.Attach(this, board, windows, restoreSize: true);
        InitializeComponent();
        DataContext = viewModel;

        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // 有记录就摆回上次的位置；没记录才居中，并保证不超出工作区
        Opened += (_, _) =>
        {
            if (!_geometry.Restore())
            {
                Services.ScreenHelper.FitIntoWorkArea(this);
            }

            _geometry.Arm();
        };

        Opened += async (_, _) => await InitializeWebAsync();

        Web.Ready += (_, _) => PushDataToWeb();
        Web.Failed += (_, _) => _viewModel.WebViewFailed = true;

        // 页面加载完再推一次（Ready 可能早于导航完成，那时候 postMessage 会被丢掉）
        Web.NavigationCompleted += (_, _) => PushDataToWeb();

        // 页面交回来的界面状态（区间、勾选…）由宿主落盘，下次打开再推回去
        Web.WebMessageReceived += OnWebMessageReceived;

        // WebView2 是独立合成表面，Window.Opacity / 纸面色都盖不到它 —— 只能告诉页面自己
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        Closed += (_, _) =>
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            DetachCollector();
        };
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
        _viewModel.SetSnapshot(_collector.GetSnapshot(_viewModel.BoardId));
        PushDataToWeb();
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

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _viewModel.SetSnapshot(_collector?.GetSnapshot(boardId));
            PushDataToWeb();
        });
    }

    private async Task InitializeWebAsync()
    {
        _viewModel.RefreshFromBoard();
        var html = _viewModel.CurrentHtml ?? CustomBoardTemplate.BuildHtml(_viewModel.BoardName);
        if (html.Length > CustomBoardTemplate.MaxHtmlBytes)
        {
            Log.Warn("[custom] HTML 超过 " + CustomBoardTemplate.MaxHtmlBytes + " 字节，拒绝加载");
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
        if (changed)
        {
            await InitializeWebAsync();
        }

        PushDataToWeb();
    }

    /// <summary>把当前快照发给页面：board 元信息 + 上一次的界面状态 + data/error 二选一。</summary>
    private void PushDataToWeb()
    {
        var snapshot = _viewModel.Snapshot;
        try
        {
            Web.PostJson(new JsonObject
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
                Web.PostJson(new JsonObject
                {
                    ["type"] = "error",
                    ["message"] = snapshot.Error,
                }.ToJsonString());
                return;
            }

            if (snapshot.DataJson is not null)
            {
                Web.PostJson(new JsonObject
                {
                    ["type"] = "data",
                    ["data"] = JsonNode.Parse(snapshot.DataJson),
                    ["fetchedAt"] = snapshot.FetchedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                }.ToJsonString());
            }
        }
        catch (Exception ex)
        {
            Log.Debug("[custom] 推送数据到看板失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 把上次落盘的界面状态推回页面（<c>{type:'ui', state:{…}}</c>）。
    /// 页面是 opaque origin，<c>localStorage</c> 会被浏览器拒，所以状态只能由宿主保管。
    /// </summary>
    private void PushUiState()
    {
        var saved = BoardUiStateStore.Load(_viewModel.BoardId);
        if (string.IsNullOrWhiteSpace(saved))
        {
            return;
        }

        try
        {
            Web.PostJson(new JsonObject
            {
                ["type"] = "ui",
                ["state"] = JsonNode.Parse(saved),
            }.ToJsonString());
        }
        catch (Exception ex)
        {
            Log.Debug("[custom] 推送看板界面状态失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 页面把界面状态（区间、勾选…）交回来，宿主落盘，下次打开再推回去。
    /// 安全边界同 WPF 版：只认 <c>type = ui.state</c> 且 <c>state</c> 是对象、整体不超 64KB 的消息。
    /// </summary>
    private void OnWebMessageReceived(object? sender, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > BoardUiStateStore.MaxBytes)
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                type.GetString() != "ui.state" ||
                !root.TryGetProperty("state", out var state) ||
                state.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            BoardUiStateStore.Save(_viewModel.BoardId, state.GetRawText());
        }
        catch (Exception ex)
        {
            Log.Debug("[custom] 保存看板界面状态失败：" + ex.Message);
        }
    }

    /// <summary>整窗不透明度 / 背景透明度变了：同步给网页（HwndHost 里的内容不参与窗口 Opacity）。</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CustomBoardViewModel.WindowOpacity))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(ApplyWebOpacity);
        }
        else if (e.PropertyName == nameof(CustomBoardViewModel.CurrentOpacity))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(ApplyWebBackground);
        }
    }

    /// <summary>整窗透明度注入页面（页面里所有内容一起淡出，和窗口 Opacity 一个手感）。</summary>
    private void ApplyWebOpacity()
    {
        var alpha = Math.Clamp(_viewModel.WindowOpacity, 0.2, 1.0)
            .ToString("0.###", CultureInfo.InvariantCulture);
        Web.ExecuteScript("document.documentElement.style.opacity='" + alpha + "'");
    }

    /// <summary>看板「背景」透明度注入页面（网页自己画的卡片底，窗口的纸面被它盖住了）。</summary>
    private void ApplyWebBackground()
    {
        var alpha = Math.Clamp(_viewModel.CurrentOpacity, 0.15, 1.0)
            .ToString("0.###", CultureInfo.InvariantCulture);

        // 只给页面一个 alpha 变量，由页面自己画圆角卡片。
        // 注意：不要再给 <html> 铺底色 —— WebView 是矩形 HWND，铺了底色就会盖住卡片圆角。
        Web.ExecuteScript("document.documentElement.style.setProperty('--paper-alpha','" + alpha + "');");
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
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

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    // ---------------- 外观：皮肤 + 透明度（🎨 按钮，与待办/便签看板共用一套菜单） ----------------

    private void Appearance_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control target)
        {
            var menu = Views.AppearanceMenu.Build(_viewModel, _viewModel.Windows);
            menu.PlacementTarget = target;
            menu.Open(target);
        }
    }

    /// <summary>皮肤/透明度被别的窗口改过之后刷新绑定值（WindowManager 调）。</summary>
    public void RefreshAppearance() => _viewModel.RefreshAppearance();

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is Control source)
        {
            var current = source;
            while (current is not null)
            {
                if (current is Button)
                {
                    return;
                }

                current = current.Parent as Control;
            }
        }

        try
        {
            BeginMoveDrag(e);
        }
        catch (Exception)
        {
            // 忽略
        }
    }
}
