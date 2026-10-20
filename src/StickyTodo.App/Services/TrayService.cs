using System;
using System.Windows;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace StickyTodo.App.Services;

/// <summary>
/// 托盘图标：关闭窗口不退出，双击托盘还原，右键菜单退出。
/// </summary>
public sealed class TrayService : IDisposable
{
    private WinForms.NotifyIcon? _notifyIcon;

    public void Start()
    {
        if (_notifyIcon is not null)
        {
            return;
        }

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("显示面板", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Text = "StickyTodo",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private static void ShowMainWindow()
    {
        var app = System.Windows.Application.Current;
        app?.Dispatcher.Invoke(() =>
        {
            var window = app.MainWindow;
            if (window is null)
            {
                return;
            }

            window.Show();
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
        });
    }

    private static void ExitApplication()
    {
        var app = System.Windows.Application.Current;
        app?.Dispatcher.Invoke(() =>
        {
            if (app.MainWindow is MainWindow main)
            {
                main.RequestClose();
            }

            app.Shutdown();
        });
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