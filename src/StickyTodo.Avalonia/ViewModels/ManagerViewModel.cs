using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Entitlements;
using StickyTodo.Core.Localization;
using StickyTodo.Core.Services;
using StickyTodo.Notes.Services;

namespace StickyTodo.Notes.ViewModels;

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

    /// <summary>待办 + 已完成；0 表示这个看板没有任务概念。</summary>
    public int Total => Pending + Done;

    /// <summary>完成百分比（0–100，取整）。</summary>
    public double DonePercent => Total == 0 ? 0 : Math.Round(Done * 100.0 / Total);

    /// <summary>只有带待办的看板才画完成度条。</summary>
    public bool ShowProgress =>
        !IsMemo && !IsCalendar && !IsToken && !IsToolBoard && Board.Kind != BoardKind.Custom;

    public string PercentText => ShowProgress ? $"{(int)DonePercent}%" : string.Empty;

    public string Summary => IsMemo
        ? Loc.T("manager.summaryMemo")
        : IsCalendar
            ? Loc.T("manager.summaryCalendar")
            : IsToken
                ? Loc.T("manager.summaryToken")
                : IsToolBoard
                    ? Loc.T("manager.summaryTool")
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

        // 直接写字段而不是属性：构造期间还没有绑定，不需要通知
        _gridMode = settings.ManagerGridMode;
    }

    public ObservableCollection<BoardRowViewModel> Boards { get; } = new();

    public bool HasNoBoards => Boards.Count == 0;

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

    /// <summary>看板区的显示形式：false=列表行，true=图标网格（构造时从设置恢复，切换后写回）。</summary>
    [ObservableProperty]
    private bool _gridMode;

    /// <summary>切换按钮上的图标：现在是列表就显示网格图标，反之显示列表图标。</summary>
    public string ViewToggleGlyph => GridMode ? "☰" : "▦";

    /// <summary>切换按钮的提示文案（说清楚点一下会变成哪种形式）。</summary>
    public string ViewToggleHint => Loc.T(GridMode ? "manager.viewList" : "manager.viewGrid");

    /// <summary>列表 / 图标网格切换：取反后写回设置，下次打开管理面板还是这个形式。</summary>
    public void ToggleViewMode()
    {
        GridMode = !GridMode;
        _settings.ManagerGridMode = GridMode;
    }

    /// <summary>模式一变，按钮的图标和提示文案跟着换。</summary>
    partial void OnGridModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ViewToggleGlyph));
        OnPropertyChanged(nameof(ViewToggleHint));
    }

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private BoardRowViewModel? _selectedBoard;

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

        OnPropertyChanged(nameof(HasNoBoards));
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

    /// <summary>
    /// 安装市场看板：同名自定义看板就覆盖内容，没有就新建一个再写入（与 WPF 版同一套逻辑）。
    /// 名字是市场的「已安装」判定依据，所以覆盖也按名字找。
    /// </summary>
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
