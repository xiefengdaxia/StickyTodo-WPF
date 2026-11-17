using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Media;
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
    private readonly AppSettingsService? _settings;

    public BoardRowViewModel(Board board, int pending, int done, AppSettingsService? settings = null)
    {
        Board = board;
        Pending = pending;
        Done = done;
        _settings = settings;
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

    /// <summary>
    /// 卡片左侧的类型图标。AI 看板（Custom）的图标可以在管理面板里换，
    /// 存在设置键 <c>board.{id}.icon</c> 里；没设过是缺省 key <c>star</c>。
    /// 字形与字体都从 <see cref="BoardIcons"/> 取，和桌面时钟列画的一样。
    /// </summary>
    private BoardGlyph Icon => Board.Kind == BoardKind.Custom && _settings is not null
        ? BoardIcons.Resolve(_settings.GetBoardIcon(Board.Id))
        : BoardIcons.ForKind(Board.Kind);

    public string KindGlyph => Icon.Glyph;

    /// <summary>画 <see cref="KindGlyph"/> 用的字体（单色符号字体，不是彩色 emoji）。</summary>
    public FontFamily KindGlyphFont => Icon.Font;

    /// <summary>只有 AI 看板能换图标（其余类型各有固定图形，换了会认不出来）。</summary>
    public bool CanChangeIcon => Board.Kind == BoardKind.Custom;

    /// <summary>换图标：写设置 + 通知界面重画（列表和图标网格共用这一个属性）。</summary>
    public void ChangeIcon(string icon)
    {
        if (!CanChangeIcon || _settings is null)
        {
            return;
        }

        _settings.SetBoardIcon(Board.Id, icon);
        OnPropertyChanged(nameof(KindGlyph));
        OnPropertyChanged(nameof(KindGlyphFont));
    }

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

    /// <summary>待办 + 已完成，0 表示这个看板没有任务概念（工具/自定义看板）。</summary>
    public int Total => Pending + Done;

    /// <summary>完成百分比（0–100，四舍五入到整数）。</summary>
    public double DonePercent => Total == 0 ? 0 : Math.Round(Done * 100.0 / Total);

    /// <summary>只有带待办的看板才画完成度条，工具/便签/自定义看板不画。</summary>
    public bool ShowProgress =>
        !IsMemo && !IsCalendar && !IsToken && !IsToolBoard && Board.Kind != BoardKind.Custom;

    public string PercentText => ShowProgress ? $"{(int)DonePercent}%" : string.Empty;

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
                    todos.Count(t => t.IsDone),
                    _settings));
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