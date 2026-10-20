using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using StickyTodo.App.Views;
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

    private WinForms.NotifyIcon? _notifyIcon;
    private WinForms.ToolStripMenuItem? _boardsMenu;

    public TrayService(TodoService todoService, WindowManager windows, IServiceProvider services)
    {
        _todoService = todoService;
        _windows = windows;
        _services = services;
    }

    public void Start()
    {
        if (_notifyIcon is not null)
        {
            return;
        }

        var menu = new WinForms.ContextMenuStrip();

        var manager = new WinForms.ToolStripMenuItem("管理面板");
        manager.Click += (_, _) => ShowManager();
        menu.Items.Add(manager);

        _boardsMenu = new WinForms.ToolStripMenuItem("看板");
        menu.Items.Add(_boardsMenu);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var exit = new WinForms.ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitApplication();
        menu.Items.Add(exit);

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Text = "StickyTodo",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowManager();

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
            _boardsMenu.DropDownItems.Add(new WinForms.ToolStripMenuItem("（没有看板）") { Enabled = false });
            return;
        }

        foreach (var board in boards)
        {
            var item = new WinForms.ToolStripMenuItem(board.Name)
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

    private void ShowManager()
    {
        var app = System.Windows.Application.Current;
        app?.Dispatcher.Invoke(() =>
        {
            var window = _services.GetRequiredService<ManagerWindow>();
            window.ShowAndActivate();
        });
    }

    private static void ExitApplication()
    {
        var app = System.Windows.Application.Current;
        app?.Dispatcher.Invoke(() => app.Shutdown());
    }

    public void Dispose()
    {
        if (_notifyIcon is null)
        {
            return;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _notifyIcon = null;
    }
}