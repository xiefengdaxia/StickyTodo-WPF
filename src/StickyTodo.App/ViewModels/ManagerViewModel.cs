using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.App.Services;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Entitlements;
using StickyTodo.Core.Localization;
using StickyTodo.Core.Services;

namespace StickyTodo.App.ViewModels;

/// <summary>看板列表里的一行。</summary>
public sealed class BoardRowViewModel : ObservableObject
{
    public BoardRowViewModel(Board board, int pending, int done)
    {
        Board = board;
        Pending = pending;
        Done = done;
    }

    public Board Board { get; }

    public string Name => Board.Name;

    public string KindText => Loc.T(Board.Kind switch
    {
        BoardKind.Memo => "manager.kindMemo",
        BoardKind.Calendar => "manager.kindCalendar",
        BoardKind.Token => "manager.kindToken",
        BoardKind.Weather => "manager.kindWeather",
        BoardKind.WorldClock => "manager.kindWorldClock",
        BoardKind.DesktopClock => "manager.kindDesktopClock",
        BoardKind.NetworkSpeed => "manager.kindNetworkSpeed",
        BoardKind.Performance => "manager.kindPerformance",
        BoardKind.Custom => "manager.kindCustom",
        _ => "manager.kindTodo",
    });

    /// <summary>卡片左侧的类型图标。</summary>
    public string KindGlyph => Board.Kind switch
    {
        BoardKind.Memo => "✎",
        BoardKind.Calendar => "▦",
        BoardKind.Token => "◎",
        BoardKind.Weather => "🌤",
        BoardKind.WorldClock => "🌍",
        BoardKind.DesktopClock => "🕔",
        BoardKind.NetworkSpeed => "📶",
        BoardKind.Performance => "📈",
        BoardKind.Custom => "✨",
        _ => "✓",
    };

    public bool IsMemo => Board.Kind == BoardKind.Memo;

    public bool IsCalendar => Board.Kind == BoardKind.Calendar;

    public bool IsToken => Board.Kind == BoardKind.Token;

    /// <summary>工具型看板（天气/世界时钟/桌面时钟）：没有待办数量。</summary>
    public bool IsToolBoard => BoardKinds.IsToolBoard(Board.Kind);

    public int Pending { get; }

    public int Done { get; }

    public string Summary => IsMemo
        ? Loc.T("manager.summaryMemo")
        : IsCalendar
            ? Loc.T("manager.summaryCalendar")
            : IsToken
                ? Loc.T("manager.summaryToken")
                : IsToolBoard
                    ? Loc.T("manager.summaryTool")
                    : Board.Kind == BoardKind.Custom
                        ? Loc.T("manager.summaryCustom")
                        : Loc.F("manager.summaryTodo", Pending, Done);

    public string WindowHint => Board.Window is null ? string.Empty : Loc.T("manager.rememberedGeometry");

    /// <summary>语言切换后通知这一行所有拼接文本重算。</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(WindowHint));
    }
}

/// <summary>管理面板：看板清单 + 增删改名。</summary>
public partial class ManagerViewModel : ObservableObject
{
    private readonly TodoService _service;
    private readonly AppSettingsService _settings;
    private readonly IEntitlementService _entitlement;

    public ManagerViewModel(TodoService service, AppSettingsService settings, IEntitlementService entitlement)
    {
        _service = service;
        _settings = settings;
        _entitlement = entitlement;
        _gridMode = settings.ManagerGridMode;
    }

    public ObservableCollection<BoardRowViewModel> Boards { get; } = new();

    public string EntitlementBadge =>
        _entitlement.Tier == PlanTier.Lifetime
            ? Loc.F("settings.entitlementLifetime", EntitlementName)
            : Loc.F("settings.entitlementLimited", EntitlementName);

    public string StorageHint => Loc.T("manager.storageHint");

    /// <summary>语言切换后重算卡片上由 C# 拼出来的文本。</summary>
    public void RefreshLocalizedText()
    {
        foreach (var row in Boards)
        {
            row.RefreshLocalizedText();
        }

        OnPropertyChanged(nameof(EntitlementBadge));
        OnPropertyChanged(nameof(StorageHint));
        OnPropertyChanged(nameof(FooterHint));
        OnPropertyChanged(nameof(ViewToggleHint));
    }

    /// <summary>会员档位显示名（按当前语言）。</summary>
    private string EntitlementName =>
        Loc.T(_entitlement.Tier == PlanTier.Lifetime ? "entitlement.lifetime" : "entitlement.free");

    public string FooterHint =>
        Loc.F("manager.footerHint", HotKeyService.NewTodoHotKey);

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private BoardRowViewModel? _selectedBoard;

    /// <summary>看板列表的展示形式：false = 行列表，true = 图标网格（存设置里，下次打开沿用）。</summary>
    [ObservableProperty]
    private bool _gridMode;

    /// <summary>切换按钮上的图标：列表模式显示"切到网格"，网格模式显示"切到列表"。</summary>
    public string ViewToggleGlyph => GridMode ? "☰" : "▦";

    /// <summary>切换按钮的提示文案（跟着当前模式变）。</summary>
    public string ViewToggleHint => Loc.T(GridMode ? "manager.viewList" : "manager.viewGrid");

    partial void OnGridModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ViewToggleGlyph));
        OnPropertyChanged(nameof(ViewToggleHint));
    }

    /// <summary>列表 ⇄ 图标网格。</summary>
    public void ToggleViewMode()
    {
        GridMode = !GridMode;
        try
        {
            _settings.ManagerGridMode = GridMode;
        }
        catch (Exception)
        {
            // 记不住也不影响这次切换
        }
    }

    public async Task LoadAsync()
    {
        var rows = await Task.Run(() =>
        {
            var list = new List<BoardRowViewModel>();
            foreach (var board in _service.GetBoards())
            {
                var todos = _service.GetTodos(board.Id);
                list.Add(new BoardRowViewModel(
                    board,
                    todos.Count(t => !t.IsDone),
                    todos.Count(t => t.IsDone)));
            }

            return list;
        });

        Boards.Clear();
        foreach (var row in rows)
        {
            Boards.Add(row);
        }
    }

    public async Task<Board?> CreateBoardAsync(string name, BoardKind kind = BoardKind.Todo)
    {
        try
        {
            ErrorText = null;
            var board = await Task.Run(() => _service.CreateBoard(name, kind));
            await LoadAsync();
            return board;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            return null;
        }
    }

    public async Task<bool> RenameBoardAsync(Board board, string name)
    {
        try
        {
            ErrorText = null;
            await Task.Run(() => _service.RenameBoard(board.Id, name));
            await LoadAsync();
            return true;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            return false;
        }
    }

    public async Task<bool> DeleteBoardAsync(Board board)
    {
        try
        {
            ErrorText = null;
            await Task.Run(() => _service.DeleteBoard(board.Id));
            await LoadAsync();
            return true;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            return false;
        }
    }

    /// <summary>从看板市场安装：同名自定义看板已存在就覆盖内容，否则新建一个。</summary>
    public async Task<Board?> InstallMarketBoardAsync(string name, string html, string? script, int? intervalSeconds)
    {
        try
        {
            ErrorText = null;
            var existing = Boards
                .Select(row => row.Board)
                .FirstOrDefault(b => b.Kind == BoardKind.Custom &&
                                     string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                await Task.Run(() => _service.SaveCustomBoard(existing.Id, html, script, intervalSeconds));
                await LoadAsync();
                return existing;
            }

            var board = await Task.Run(() =>
            {
                var created = _service.CreateBoard(name, BoardKind.Custom);
                _service.SaveCustomBoard(created.Id, html, script, intervalSeconds);
                return created;
            });
            await LoadAsync();
            return board;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            return null;
        }
    }

    /// <summary>卸载市场看板（就是删除这个自定义看板）。</summary>
    public Task<bool> UninstallBoardAsync(Board board) => DeleteBoardAsync(board);
}