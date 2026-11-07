using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Localization;
using StickyTodo.Core.Services;
using StickyTodo.Notes.Services;

namespace StickyTodo.Notes.ViewModels;

/// <summary>
/// 便签/随笔看板：整块区域就是一张白纸，边写边自动保存。
/// </summary>
public partial class MemoBoardViewModel : ObservableObject, IAppearanceHost, IRefreshLocalized
{
    private readonly TodoService _service;
    private readonly AppSettingsService _settings;
    private readonly SkinService _skin;

    /// <summary>最近一次保存时间，语言切换时重算状态行要用。</summary>
    private DateTimeOffset? _updatedAt;

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

    /// <summary>文字变了就交给窗口去 debounce 保存，这里只负责记状态。</summary>
    public bool IsDirty { get; private set; }

    public async Task LoadAsync()
    {
        var note = await Task.Run(() => _service.GetNote(BoardId));
        Content = note?.Content ?? string.Empty;
        IsDirty = false;
        _updatedAt = note?.UpdatedAt;
        UpdateStatus(_updatedAt);
    }

    public void MarkDirty()
    {
        IsDirty = true;
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
            _updatedAt = note.UpdatedAt;
            UpdateStatus(_updatedAt);
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

    /// <summary>语言切换后重算状态行。</summary>
    public void RefreshLocalizedText() => UpdateStatus(_updatedAt);

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

    public double CurrentOpacity => _skin.Opacity;

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
    }

    public void SetOpacity(double opacity)
    {
        _skin.SetOpacity(opacity);
        OnPropertyChanged(nameof(CurrentOpacity));
    }

    public void SetWindowOpacity(double opacity)
    {
        // 只改这个看板：存到 board.<id> 下，其它窗口不受影响
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
        OnPropertyChanged(nameof(CurrentWindowOpacity));
        WindowOpacity = _settings.GetWindowOpacity(AppearanceKey);
    }
}
