using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Localization;
using StickyTodo.Core.Services;
using StickyTodo.Notes.Views;

namespace StickyTodo.Notes.Services;

/// <summary>
/// 托盘图标：菜单里可以开关任意看板窗口；点 X 只是收进托盘，退出走这里。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly TodoService _todoService;
    private readonly WindowManager _windows;
    private readonly AppServices _services;

    private TrayIcon? _tray;
    private NativeMenu? _boardsMenu;

    public TrayService(TodoService todoService, WindowManager windows, AppServices services)
    {
        _todoService = todoService;
        _windows = windows;
        _services = services;
    }

    public void Start()
    {
        if (_tray is not null)
        {
            return;
        }

        _tray = new TrayIcon
        {
            Icon = AppIcon.Icon,
            ToolTipText = "StickyTodo",
            IsVisible = true,
        };

        _tray.Clicked += (_, _) => ShowManager();

        BuildMenu();

        if (Application.Current is { } app)
        {
            // 追加而不是覆盖：避免把别处注册的托盘图标顶掉
            var icons = TrayIcon.GetIcons(app);
            if (icons is null)
            {
                TrayIcon.SetIcons(app, new TrayIcons { _tray });
            }
            else if (!icons.Contains(_tray))
            {
                icons.Add(_tray);
            }
        }

        RebuildBoardsMenu();
        _windows.BoardsChanged += (_, _) => RebuildBoardsMenu();
        Services.Log.Info("[tray] 托盘图标已创建");
    }

    /// <summary>语言切换后重建菜单标题。</summary>
    public void Rebuild()
    {
        if (_tray is null)
        {
            return;
        }

        BuildMenu();
        RebuildBoardsMenu();
    }

    private void BuildMenu()
    {
        if (_tray is null)
        {
            return;
        }

        var menu = new NativeMenu();

        menu.Add(Item(Loc.T("tray.manager"), () => ShowManager()));
        menu.Add(Item(Loc.T("common.settings"), () => _services.ShowSettings()));
        menu.Add(Item(Loc.T("tray.pomodoro"), () => _services.ShowPomodoro()));
        menu.Add(Item("天气", () => _services.OpenToolWindow(ToolWindowKind.Weather)));
        menu.Add(Item("世界时钟", () => _services.OpenToolWindow(ToolWindowKind.WorldClock)));
        menu.Add(Item("桌面时钟", () => _services.OpenToolWindow(ToolWindowKind.DesktopClock)));

        _boardsMenu = new NativeMenu();
        menu.Add(new NativeMenuItem { Header = Loc.T("tray.boards"), Menu = _boardsMenu });

        menu.Add(Item(Loc.T("tray.toggleAll") + "  " + HotKeyService.ToggleBoardsHotKey,
            () => _windows.ToggleBoardsVisibility()));

        menu.Add(new NativeMenuItemSeparator());

        menu.Add(Item(Loc.T("common.exit"), () => _services.ExitApplication()));

        _tray.Menu = menu;
    }

    private static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void RebuildBoardsMenu()
    {
        if (_boardsMenu is null)
        {
            return;
        }

        _boardsMenu.Items.Clear();

        var boards = _todoService.GetBoards();
        if (boards.Count == 0)
        {
            _boardsMenu.Add(new NativeMenuItem { Header = Loc.T("tray.noBoards"), IsEnabled = false });
            return;
        }

        foreach (var board in boards)
        {
            var item = new NativeMenuItem
            {
                Header = $"{board.Name}（{BoardKinds.DisplayName(board.Kind)}）",
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = _windows.IsBoardOpen(board.Id),
            };

            var captured = board;
            item.Click += (_, _) =>
            {
                if (_windows.IsBoardOpen(captured.Id))
                {
                    _windows.CloseBoard(captured.Id);
                }
                else
                {
                    _windows.OpenBoard(captured);
                }
            };

            _boardsMenu.Add(item);
        }
    }

    private void ShowManager() => Dispatcher.UIThread.Post(() => _services.ShowManager());

    /// <summary>托盘气泡通知（提醒 / 番茄钟结束用，纯本地，不经过任何网络）。</summary>
    public void Notify(string title, string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var toast = new ToastWindow(title, text);
                toast.Show();
            }
            catch (Exception)
            {
                // 通知失败不影响主流程
            }
        });
    }

    public void Dispose()
    {
        if (_tray is null)
        {
            return;
        }

        try
        {
            // 先从 TrayIcons 集合里移除，再 Dispose。
            // 只 Dispose 不摘除的话，Avalonia 的通知区域句柄不会立刻回收，
            // 进程退出后任务栏里会留下"幽灵图标"。
            if (Application.Current is { } app && TrayIcon.GetIcons(app) is { } icons)
            {
                icons.Remove(_tray);
            }

            _tray.IsVisible = false;
            _tray.Dispose();
        }
        catch (Exception ex)
        {
            Services.Log.Warn("[tray] 销毁托盘图标失败：" + ex.Message);
        }
        finally
        {
            _tray = null;
            _boardsMenu = null;
        }
    }
}
