using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.App.Services;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Services;
using StickyTodo.Mcp;

namespace StickyTodo.App.ViewModels;

/// <summary>
/// 自定义看板的视图模型：持有看板行的最新内容（HTML/脚本）与采集器快照。
/// 窗口负责 WebView2 渲染；这里只管「现在该显示什么」。
/// 同时实现 <see cref="IAppearanceHost"/>：皮肤、背景透明度、整窗透明度都按看板 id 存，
/// 🎨 外观菜单和待办/便签看板共用同一套代码。
/// </summary>
public partial class CustomBoardViewModel : ObservableObject, IAppearanceHost
{
    private readonly Board _board;
    private readonly TodoService _todos;
    private readonly SkinService _skin;
    private readonly AppSettingsService _settings;
    private CollectorHost.CollectorSnapshot? _snapshot;

    public CustomBoardViewModel(Board board, TodoService todos, SkinService skin, AppSettingsService settings, WindowManager windows)
    {
        _board = board;
        _todos = todos;
        _skin = skin;
        _settings = settings;
        Windows = windows;
        BoardName = board.Name;
        _windowOpacity = settings.GetWindowOpacity(board.Id);
        _isTopmost = settings.GetBoardTopmost(board.Id);
        _isLocked = settings.GetBoardLocked(board.Id);
    }

    public WindowManager Windows { get; }

    public string BoardId => _board.Id;

    /// <summary>当前要渲染的 HTML（看板没存就用内置模板）。</summary>
    public string? CurrentHtml { get; private set; }

    /// <summary>采集器最近一次结果（可能为 null：还没跑过/没有采集器）。</summary>
    public CollectorHost.CollectorSnapshot? Snapshot => _snapshot;

    [ObservableProperty]
    private string _boardName = string.Empty;

    /// <summary>底部状态条：更新时间 / 下次采集 / 错误摘要。</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>WebView2 不可用时的文本兜底内容（数据 JSON / 错误说明）。</summary>
    [ObservableProperty]
    private string _fallbackText = string.Empty;

    /// <summary>true = WebView2 起不来，改用文本兜底。</summary>
    [ObservableProperty]
    private bool _webViewFailed;

    /// <summary>整窗不透明度（连文字一起），绑定到 Window.Opacity。</summary>
    [ObservableProperty]
    private double _windowOpacity = 1.0;

    /// <summary>置顶（绑定 Window.Topmost），按看板 id 存。</summary>
    [ObservableProperty]
    private bool _isTopmost;

    /// <summary>锁定穿透（WebView 点不到 + 窗口鼠标穿透），按看板 id 存。</summary>
    [ObservableProperty]
    private bool _isLocked;

    // ---------------- 标题栏图标 ----------------

    private BoardGlyph Icon => BoardIcons.Resolve(_settings.GetBoardIcon(_board.Id));

    /// <summary>标题栏图标：管理面板里选的那个 key，走 <see cref="BoardIcons"/> 同一份字形。</summary>
    public string Glyph => Icon.Glyph;

    /// <summary>画 <see cref="Glyph"/> 的字体（单色符号字体）。</summary>
    public FontFamily GlyphFont => Icon.Font;

    /// <summary>换了图标要重画标题栏；窗口关闭时记得 <see cref="StopIconWatch"/>。</summary>
    public void StartIconWatch() => _settings.SettingChanged += OnSettingChanged;

    public void StopIconWatch() => _settings.SettingChanged -= OnSettingChanged;

    private void OnSettingChanged(string key)
    {
        if (string.Equals(key, AppSettingsService.BoardIconKey(_board.Id), StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(Glyph));
            OnPropertyChanged(nameof(GlyphFont));
        }
    }

    public void ToggleTopmost()
    {
        IsTopmost = !IsTopmost;
        _settings.SetBoardTopmost(BoardId, IsTopmost);
    }

    public void ToggleLock()
    {
        IsLocked = !IsLocked;
        _settings.SetBoardLocked(BoardId, IsLocked);
    }

    /// <summary>重读看板行（MCP 改过内容后 WindowManager 调）。返回 HTML 是否有变化。</summary>
    public bool RefreshFromBoard()
    {
        var board = _todos.GetBoard(_board.Id);
        if (board is null)
        {
            return false;
        }

        var html = string.IsNullOrWhiteSpace(board.CustomHtml)
            ? CustomBoardTemplate.BuildHtml(board.Name)
            : board.CustomHtml;
        var changed = !string.Equals(html, CurrentHtml, StringComparison.Ordinal);
        BoardName = board.Name;
        CurrentHtml = html;
        return changed;
    }

    public void SetSnapshot(CollectorHost.CollectorSnapshot? snapshot)
    {
        _snapshot = snapshot;
        StatusText = BuildStatus(snapshot);
        if (snapshot is not null)
        {
            FallbackText = snapshot.Error is not null
                ? "采集错误：" + snapshot.Error + "\n\n" + (snapshot.DataJson ?? "（还没有数据）")
                : snapshot.DataJson ?? "（还没有数据）";
        }
    }

    private static string BuildStatus(CollectorHost.CollectorSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return "没有采集器（AI 可以用 update_custom_board 配一个）";
        }

        var text = snapshot.FetchedAt is null
            ? "等待首次采集"
            : "更新于 " + snapshot.FetchedAt.Value.ToLocalTime().ToString("MM-dd HH:mm:ss");
        if (snapshot.NextRunAt is not null)
        {
            text += " · 下次 " + snapshot.NextRunAt.Value.ToLocalTime().ToString("HH:mm:ss");
        }

        if (snapshot.Error is not null)
        {
            text += " · " + snapshot.Error;
        }

        return text;
    }

    // ---------------- 外观（IAppearanceHost）：皮肤 + 背景透明度 + 整窗透明度 ----------------

    /// <summary>外观设置存在哪个 key 下：看板用它自己的 id。</summary>
    public string AppearanceKey => BoardId;

    public string CurrentSkinKey => _skin.Current.Key;

    /// <summary>本看板的纸面透明度：单独设过用看板的，否则跟随全局滑块。</summary>
    public double CurrentOpacity => _settings.GetBoardBgOpacity(AppearanceKey, _skin.Opacity);

    /// <summary>纸面背景（按本看板的背景透明度重建；窗口绑定它替代全局 Brush.Paper）。</summary>
    public Brush PaperBrush => _skin.Current.CreateBackground(CurrentOpacity);

    /// <summary>标题栏浅色覆盖，跟纸面同一档透明度。</summary>
    public Brush PaperDeepBrush => _skin.Current.CreateHeaderOverlay(CurrentOpacity);

    /// <summary>本看板自己的整窗透明度（没单独设过就跟随全局）。</summary>
    public double CurrentWindowOpacity => _settings.GetWindowOpacity(AppearanceKey);

    /// <summary>这个看板有没有单独设过背景透明度（false = 跟随全局）。</summary>
    public bool HasOwnOpacity => _settings.HasBoardBgOpacity(AppearanceKey);

    /// <summary>这个看板有没有单独设过整窗透明度（false = 跟随全局）。</summary>
    public bool HasOwnWindowOpacity => _settings.HasWindowOpacity(AppearanceKey);

    public void SetSkin(string skinKey)
    {
        _skin.SetSkin(skinKey);
        OnPropertyChanged(nameof(CurrentSkinKey));
        OnPropertyChanged(nameof(PaperBrush));
        OnPropertyChanged(nameof(PaperDeepBrush));
    }

    public void SetOpacity(double opacity)
    {
        // 只改这个看板的纸面透明度，其它看板和全局滑块都不受影响
        _settings.SetBoardBgOpacity(AppearanceKey, opacity);
        OnPropertyChanged(nameof(CurrentOpacity));
        OnPropertyChanged(nameof(PaperBrush));
        OnPropertyChanged(nameof(PaperDeepBrush));
    }

    /// <summary>取消背景透明度的单独设置，回到跟随全局。</summary>
    public void ResetOpacity()
    {
        _settings.ClearBoardBgOpacity(AppearanceKey);
        OnPropertyChanged(nameof(CurrentOpacity));
        OnPropertyChanged(nameof(PaperBrush));
        OnPropertyChanged(nameof(PaperDeepBrush));
    }

    public void SetWindowOpacity(double opacity)
    {
        _settings.SetWindowOpacity(AppearanceKey, opacity);
        WindowOpacity = _settings.GetWindowOpacity(AppearanceKey);
        OnPropertyChanged(nameof(CurrentWindowOpacity));
    }

    /// <summary>取消整窗透明度的单独设置，回到跟随全局。</summary>
    public void ResetWindowOpacity()
    {
        _settings.ClearWindowOpacity(AppearanceKey);
        WindowOpacity = _settings.GetWindowOpacity(AppearanceKey);
        OnPropertyChanged(nameof(CurrentWindowOpacity));
    }

    /// <summary>皮肤被别的窗口改过之后刷新一下绑定值。</summary>
    public void RefreshAppearance()
    {
        OnPropertyChanged(nameof(CurrentSkinKey));
        OnPropertyChanged(nameof(CurrentOpacity));
        OnPropertyChanged(nameof(PaperBrush));
        OnPropertyChanged(nameof(PaperDeepBrush));
        OnPropertyChanged(nameof(CurrentWindowOpacity));
        WindowOpacity = _settings.GetWindowOpacity(AppearanceKey);
    }
}
