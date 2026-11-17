using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.App.Services;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Localization;
using StickyTodo.Core.Services;

namespace StickyTodo.App.ViewModels;

/// <summary>
/// 便签/随笔看板：整块区域就是一张白纸，边写边自动保存。
/// </summary>
public partial class MemoBoardViewModel : ObservableObject, IAppearanceHost, ISessionTopmost
{
    private readonly TodoService _service;
    private readonly AppSettingsService _settings;
    private readonly SkinService _skin;

    public MemoBoardViewModel(TodoService service, Board board, AppSettingsService settings, SkinService skin)
    {
        _service = service;
        _settings = settings;
        _skin = skin;
        Board = board;
        _boardName = board.Name;
        _isTopmost = settings.GetBoardTopmost(board.Id);
        _isLocked = settings.GetBoardLocked(board.Id);
        _windowOpacity = settings.GetWindowOpacity(board.Id);
    }

    public Board Board { get; }

    public string BoardId => Board.Id;

    [ObservableProperty]
    private string _boardName;

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private bool _isTopmost;

    [ObservableProperty]
    private bool _isLocked;

    [ObservableProperty]
    private double _windowOpacity = 1.0;

    /// <summary>
    /// true = 预览（只读 FlowDocument），false = 编辑（原来的 TextBox）。
    /// 切换只动显示，正文一个字都不动。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    private bool _isPreview;

    /// <summary>底部那行一次性提示（比如「已导出：…」）。</summary>
    [ObservableProperty]
    private string? _noticeText;

    public bool IsEditing => !IsPreview;

    public string EditTabText => Loc.T("memo.edit");

    public string PreviewTabText => Loc.T("memo.preview");

    /// <summary>编辑模式下的 Markdown 语法提示（文案已经在 UiStrings 里）。</summary>
    public string MarkdownHint => Loc.T("memo.mdHint");

    /// <summary>文字变了就交给窗口去 debounce 保存，这里只负责记状态。</summary>
    public bool IsDirty { get; private set; }

    public async Task LoadAsync()
    {
        // 本地还有没落库的编辑（800ms 防抖窗口里，或者刚剪切完）：
        // 这时候用库里的旧正文覆盖，用户看到的就是「改了一下自己又变回去了」。
        if (IsDirty)
        {
            return;
        }

        var note = await Task.Run(() => _service.GetNote(BoardId));

        if (note is null)
        {
            // 这张便签从来没有正文记录 = 第一次打开：预填一段示例，
            // 顺手把 Markdown / 公式 / mermaid / 表格的样子演示一遍。
            Content = MarkdownRenderer.SampleDocument;
            IsDirty = true;
            StatusText = Loc.T("memo.unsaved");
            await SaveIfDirtyAsync();
            return;
        }

        Content = note.Content;
        IsDirty = false;
        UpdateStatus(note.UpdatedAt);
    }

    /// <summary>切换编辑 / 预览。正文不动，只是换个显示方式。</summary>
    public void SetPreview(bool preview) => IsPreview = preview;

    /// <summary>底部提示（导出成功之类），下一次编辑/保存时会被状态行覆盖掉。</summary>
    public void SetNotice(string? text) => NoticeText = text;

    /// <summary>
    /// 再开一张便签：和管理面板「新建便签」走同一条路（<see cref="TodoService.CreateBoard"/> +
    /// <see cref="WindowManager.OpenBoard"/>），只是不起名字对话框，直接用默认名加序号。
    /// 建不出来（比如看板数已到上限）返回 null，错误照旧显示在底栏。
    /// </summary>
    public Board? CreateSiblingMemo()
    {
        try
        {
            ErrorText = null;

            var name = Loc.T("manager.newMemoDefault");
            var index = _service.GetBoards().Count(b => b.Kind == BoardKind.Memo) + 1;
            if (index > 1)
            {
                name = $"{name} {index}";
            }

            return _service.CreateBoard(name, BoardKind.Memo);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            return null;
        }
    }

    public void MarkDirty()
    {
        IsDirty = true;
        NoticeText = null;
        StatusText = Loc.T("memo.saving");
    }

    public async Task SaveIfDirtyAsync()
    {
        if (!IsDirty)
        {
            return;
        }

        try
        {
            ErrorText = null;
            var text = Content;
            var note = await Task.Run(() => _service.SaveNote(BoardId, text));
            IsDirty = false;
            UpdateStatus(note.UpdatedAt);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            StatusText = Loc.T("memo.saveFailed");
        }
    }

    private void UpdateStatus(DateTimeOffset? updatedAt)
    {
        var chars = Content.Count(c => !char.IsWhiteSpace(c));
        var saved = updatedAt is null ? Loc.T("memo.unsaved") : Loc.F("memo.saved", updatedAt.Value.ToString("MM-dd HH:mm"));
        StatusText = Loc.F("memo.status", chars, saved);
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

    public async Task RenameBoardAsync(string name)
    {
        try
        {
            ErrorText = null;
            await Task.Run(() => _service.RenameBoard(BoardId, name));
            Board.Name = name;
            BoardName = name;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    public string CurrentSkinKey => _skin.Current.Key;

    /// <summary>本看板的纸面透明度：单独设过用看板的，否则跟随全局滑块。</summary>
    public double CurrentOpacity => _settings.GetBoardBgOpacity(AppearanceKey, _skin.Opacity);

    /// <summary>纸面背景（按本看板的背景透明度重建；窗口绑定它替代全局 Brush.Paper）。</summary>
    public System.Windows.Media.Brush PaperBrush => _skin.Current.CreateBackground(CurrentOpacity);

    /// <summary>标题栏浅色覆盖，跟纸面同一档透明度。</summary>
    public System.Windows.Media.Brush PaperDeepBrush => _skin.Current.CreateHeaderOverlay(CurrentOpacity);

    /// <summary>这个看板有没有单独设过背景透明度（false = 跟随全局）。</summary>
    public bool HasOwnOpacity => _settings.HasBoardBgOpacity(AppearanceKey);

    /// <summary>本看板自己的整窗透明度（没单独设过就跟随全局）。</summary>
    public double CurrentWindowOpacity => _settings.GetWindowOpacity(AppearanceKey);

    /// <summary>外观设置存在哪个 key 下：看板用它自己的 id。</summary>
    public string AppearanceKey => BoardId;

    /// <summary>这个看板有没有单独设过透明度（false = 跟随全局）。</summary>
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
        // 只改这个看板，其它窗口不受影响
        _settings.SetWindowOpacity(AppearanceKey, opacity);
        WindowOpacity = _settings.GetWindowOpacity(AppearanceKey);
        OnPropertyChanged(nameof(CurrentWindowOpacity));
    }

    /// <summary>取消本看板的单独设置，回到「跟随全局」。</summary>
    public void ResetWindowOpacity()
    {
        _settings.ClearWindowOpacity(AppearanceKey);
        WindowOpacity = _settings.GetWindowOpacity(AppearanceKey);
        OnPropertyChanged(nameof(CurrentWindowOpacity));
    }

    public void RefreshAppearance()
    {
        OnPropertyChanged(nameof(CurrentSkinKey));
        OnPropertyChanged(nameof(CurrentOpacity));
        OnPropertyChanged(nameof(PaperBrush));
        OnPropertyChanged(nameof(PaperDeepBrush));
        OnPropertyChanged(nameof(CurrentWindowOpacity));

        // 单独设过就用这个看板自己的值，否则跟随全局
        WindowOpacity = _settings.GetWindowOpacity(AppearanceKey);
    }
}