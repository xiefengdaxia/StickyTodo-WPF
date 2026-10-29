using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using StickyTodo.App.Views;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Localization;
using StickyTodo.Core.Services;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace StickyTodo.App.Services;

/// <summary>
/// 托盘图标：菜单里可以开关任意看板窗口；点 X 只是收进托盘，退出走这里。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly TodoService _todoService;
    private readonly WindowManager _windows;
    private readonly IServiceProvider _services;

    private TrayIconHost? _tray;
    private WinForms.ContextMenuStrip? _menu;
    private WinForms.ToolStripMenuItem? _boardsMenu;
    private PomodoroWindow? _pomodoroWindow;

    public TrayService(TodoService todoService, WindowManager windows, IServiceProvider services)
    {
        _todoService = todoService;
        _windows = windows;
        _services = services;
    }

    /// <summary>打开番茄时钟（已经开着就激活）。</summary>
    public void ShowPomodoro()
    {
        if (_pomodoroWindow is { IsLoaded: true })
        {
            _pomodoroWindow.ShowAndActivate();
            return;
        }

        var viewModel = _services.GetRequiredService<ViewModels.PomodoroViewModel>();
        _pomodoroWindow = new PomodoroWindow(viewModel, Notify);
        _pomodoroWindow.Closed += (_, _) => _pomodoroWindow = null;
        _pomodoroWindow.Show();
    }

    public void Start()
    {
        if (_tray is not null)
        {
            return;
        }

        var menu = new WinForms.ContextMenuStrip();
        _menu = menu;

        var manager = new WinForms.ToolStripMenuItem(Loc.T("tray.manager"));
        manager.Click += (_, _) => ShowManager();
        menu.Items.Add(manager);

        var settings = new WinForms.ToolStripMenuItem(Loc.T("common.settings"));
        settings.Click += (_, _) => (System.Windows.Application.Current as App)?.ShowSettings();
        menu.Items.Add(settings);

        var pomodoro = new WinForms.ToolStripMenuItem(Loc.T("tray.pomodoro"));
        pomodoro.Click += (_, _) => System.Windows.Application.Current?.Dispatcher.Invoke(ShowPomodoro);
        menu.Items.Add(pomodoro);

        var weather = new WinForms.ToolStripMenuItem("天气");
        weather.Click += (_, _) => (System.Windows.Application.Current as App)?.OpenTool(ToolWindowKind.Weather);
        menu.Items.Add(weather);

        var worldClock = new WinForms.ToolStripMenuItem("世界时钟");
        worldClock.Click += (_, _) => (System.Windows.Application.Current as App)?.OpenTool(ToolWindowKind.WorldClock);
        menu.Items.Add(worldClock);

        var desktopClock = new WinForms.ToolStripMenuItem("桌面时钟");
        desktopClock.Click += (_, _) => (System.Windows.Application.Current as App)?.OpenTool(ToolWindowKind.DesktopClock);
        menu.Items.Add(desktopClock);

        _boardsMenu = new WinForms.ToolStripMenuItem(Loc.T("tray.boards"));
        menu.Items.Add(_boardsMenu);

        var toggleLabel = Loc.T("tray.toggleAll") + "\t" + HotKeyService.ToggleBoardsHotKey;
        var toggleAll = new WinForms.ToolStripMenuItem(toggleLabel);
        toggleAll.Click += (_, _) => _windows.ToggleBoardsVisibility();
        menu.Items.Add(toggleAll);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var exit = new WinForms.ToolStripMenuItem(Loc.T("common.exit"));
        exit.Click += (_, _) => ExitApplication();
        menu.Items.Add(exit);

        // 用固定 GUID 登记：被杀掉的旧进程留下的图标会被这次登记接管，不再有僵尸图标
        _tray = new TrayIconHost(AppIcon.TrayIcon, "StickyTodo", ShowManager, ShowMenuAtCursor);

        RebuildBoardsMenu();
        _windows.BoardsChanged += (_, _) => RebuildBoardsMenu();
    }

    private void RebuildBoardsMenu()
    {
        if (_boardsMenu is null)
        {
            return;
        }

        _boardsMenu.DropDownItems.Clear();

        var boards = _todoService.GetBoards();
        if (boards.Count == 0)
        {
            _boardsMenu.DropDownItems.Add(new WinForms.ToolStripMenuItem(Loc.T("tray.noBoards")) { Enabled = false });
            return;
        }

        foreach (var board in boards)
        {
            var item = new WinForms.ToolStripMenuItem($"{board.Name}　({BoardKinds.DisplayName(board.Kind)})")
            {
                Checked = _windows.IsBoardOpen(board.Id),
                CheckOnClick = false,
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
            _boardsMenu.DropDownItems.Add(item);
        }
    }

    /// <summary>托盘气泡通知（提醒用，纯本地，不经过任何网络）。</summary>
    public void Notify(string title, string text)
    {
        if (_tray is null)
        {
            return;
        }

        try
        {
            _tray.ShowBalloon(title, text);
        }
        catch (Exception)
        {
            // 通知失败不影响主流程
        }
    }

    /// <summary>右键托盘图标：在鼠标位置弹菜单。</summary>
    private void ShowMenuAtCursor()
    {
        try
        {
            _menu?.Show(WinForms.Cursor.Position);
        }
        catch (Exception)
        {
            // 菜单弹不出来不影响主流程
        }
    }

    private void ShowManager()
    {
        var app = System.Windows.Application.Current;
        app?.Dispatcher.Invoke(() =>
        {
            var window = _services.GetRequiredService<ManagerWindow>();
            window.ShowAndActivate();
        });
    }

    private void ExitApplication()
    {
        var app = System.Windows.Application.Current;
        app?.Dispatcher.Invoke(() =>
        {
            // 先固化"当前开着哪些看板"，再 Shutdown。
            // WPF 的 Shutdown 会先逐个关窗口、之后才走 OnExit，那时再记就只剩空列表了。
            (_services.GetService(typeof(WindowManager)) as WindowManager)?.BeginShutdown();
            app.Shutdown();
        });
    }

    public void Dispose()
    {
        _tray?.Dispose();
        _tray = null;
        _menu?.Dispose();
        _menu = null;
    }
}