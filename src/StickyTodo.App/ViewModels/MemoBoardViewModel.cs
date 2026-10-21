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
public partial class MemoBoardViewModel : ObservableObject, IAppearanceHost
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
        _windowOpacity = skin.WindowOpacity;
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
        UpdateStatus(note?.UpdatedAt);
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

    public double CurrentOpacity => _skin.Opacity;

    public double CurrentWindowOpacity => _skin.WindowOpacity;

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
        _skin.SetWindowOpacity(opacity);
        WindowOpacity = _skin.WindowOpacity;
        OnPropertyChanged(nameof(CurrentWindowOpacity));
    }

    public void RefreshAppearance()
    {
        OnPropertyChanged(nameof(CurrentSkinKey));
        OnPropertyChanged(nameof(CurrentOpacity));
        OnPropertyChanged(nameof(CurrentWindowOpacity));
        WindowOpacity = _skin.WindowOpacity;
    }
}