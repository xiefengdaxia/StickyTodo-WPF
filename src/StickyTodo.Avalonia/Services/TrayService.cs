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

        // 护眼：色温 + 豆沙绿校色 + 防蓝光滤镜，豆沙绿皮肤快捷入口
        var eyeMenu = new NativeMenu();

        var tempToggle = new NativeMenuItem
        {
            Header = Loc.T("menu.eyeTemp"),
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = _services.Settings.EyeTempEnabled,
        };
        tempToggle.Click += (_, _) =>
        {
            var enable = !_services.Settings.EyeTempEnabled;
            _services.Settings.EyeTempEnabled = enable;

            if (enable)
            {
                _services.Settings.EyeGammaEnabled = false;
                _services.Settings.EyeFilterEnabled = false;
            }

            _services.EyeGamma.ApplyFromSettings();
            _services.EyeCare.ApplyFromSettings();
        };
        eyeMenu.Add(tempToggle);

        var tempLevels = new NativeMenu();
        foreach (var (kelvin, key) in new[]
                 {
                     (6500, "eye.temp.6500"),
                     (5500, "eye.temp.5500"),
                     (4500, "eye.temp.4500"),
                     (3400, "eye.temp.3400"),
                     (2700, "eye.temp.2700"),
                     (2000, "eye.temp.2000"),
                 })
        {
            var level = new NativeMenuItem
            {
                Header = Loc.T(key),
                ToggleType = NativeMenuItemToggleType.Radio,
                IsChecked = _services.Settings.EyeTempEnabled && kelvin == _services.Settings.EyeTempKelvin,
            };
            var value = kelvin;
            level.Click += (_, _) =>
            {
                _services.Settings.EyeTempKelvin = value;
                _services.Settings.EyeTempEnabled = true;
                _services.Settings.EyeGammaEnabled = false;
                _services.Settings.EyeFilterEnabled = false;
                _services.EyeGamma.ApplyFromSettings();
                _services.EyeCare.ApplyFromSettings();
            };
            tempLevels.Add(level);
        }

        eyeMenu.Add(new NativeMenuItem { Header = Loc.T("menu.eyeTempLevels"), Menu = tempLevels });

        eyeMenu.Add(new NativeMenuItemSeparator());

        var gammaToggle = new NativeMenuItem
        {
            Header = Loc.T("menu.eyeGamma"),
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = _services.Settings.EyeGammaEnabled,
        };
        gammaToggle.Click += (_, _) =>
        {
            var enable = !_services.Settings.EyeGammaEnabled;
            _services.Settings.EyeGammaEnabled = enable;

            if (enable)
            {
                _services.Settings.EyeTempEnabled = false;
                _services.Settings.EyeFilterEnabled = false;
            }

            _services.EyeGamma.ApplyFromSettings();
            _services.EyeCare.ApplyFromSettings();
        };
        eyeMenu.Add(gammaToggle);

        var filterToggle = new NativeMenuItem
        {
            Header = Loc.T("settings.eyeFilter"),
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = _services.Settings.EyeFilterEnabled,
        };
        filterToggle.Click += (_, _) =>
        {
            var enable = !_services.Settings.EyeFilterEnabled;
            _services.Settings.EyeFilterEnabled = enable;

            if (enable)
            {
                _services.Settings.EyeTempEnabled = false;
                _services.Settings.EyeGammaEnabled = false;
            }

            _services.EyeCare.ApplyFromSettings();
            _services.EyeGamma.ApplyFromSettings();
        };
        eyeMenu.Add(filterToggle);

        eyeMenu.Add(new NativeMenuItemSeparator());

        var strength = _services.Settings.EyeFilterStrength;
        foreach (var (hex, key) in EyeCareFilter.PresetList)
        {
            var preset = new NativeMenuItem
            {
                Header = Loc.T(key),
                ToggleType = NativeMenuItemToggleType.Radio,
                IsChecked = string.Equals(hex, _services.Settings.EyeFilterColor, StringComparison.OrdinalIgnoreCase),
            };
            var presetHex = hex;
            preset.Click += (_, _) =>
            {
                _services.Settings.EyeFilterColor = presetHex;
                _services.Settings.EyeFilterEnabled = true;
                _services.Settings.EyeTempEnabled = false;
                _services.Settings.EyeGammaEnabled = false;
                _services.EyeCare.ApplyFromSettings();
                _services.EyeGamma.ApplyFromSettings();
            };
            eyeMenu.Add(preset);
        }

        foreach (var (value, key) in new[] { (15, "eye.strength.light"), (25, "eye.strength.medium"), (40, "eye.strength.heavy") })
        {
            var strengthItem = new NativeMenuItem
            {
                Header = Loc.T(key),
                ToggleType = NativeMenuItemToggleType.Radio,
                IsChecked = value == strength
                    || (value == 15 && strength <= 15)
                    || (value == 25 && strength is > 15 and < 40)
                    || (value == 40 && strength >= 40),
            };
            var strengthValue = value;
            strengthItem.Click += (_, _) =>
            {
                _services.Settings.EyeFilterStrength = strengthValue;
                _services.Settings.EyeFilterEnabled = true;
                _services.Settings.EyeTempEnabled = false;
                _services.Settings.EyeGammaEnabled = false;
                _services.EyeCare.ApplyFromSettings();
                _services.EyeGamma.ApplyFromSettings();
            };
            eyeMenu.Add(strengthItem);
        }

        eyeMenu.Add(new NativeMenuItemSeparator());
        eyeMenu.Add(Item(Loc.T("menu.eyeSprout"), () =>
        {
            _services.Skin.SetSkin(NoteSkins.BeanSprout.Key);
            _services.Windows.ApplySkin();
        }));
        eyeMenu.Add(Item(Loc.T("menu.eyeSettings"), () => _services.ShowSettings()));

        menu.Add(new NativeMenuItem { Header = Loc.T("menu.eye"), Menu = eyeMenu });

        _boardsMenu = new NativeMenu();
        menu.Add(new NativeMenuItem { Header = Loc.T("tray.boards"), Menu = _boardsMenu });

        menu.Add(Item(Loc.T("tray.toggleAll") + "  " + HotKeyService.ToggleBoardsHotKey,
            () => _windows.ToggleBoardsVisibility()));

        // 换显示器 / 改分辨率后窗口跑到屏幕外时，一键收回当前屏幕右上角
        menu.Add(Item(Loc.T("tray.resetWindows"), ResetWindows));

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

    /// <summary>
    /// 弹出"皮肤版"右键菜单（桌面时钟右键会调这里）。
    ///
    /// 说明：任务栏图标本身的右键菜单走 Avalonia 的 <see cref="NativeMenu"/>，
    /// 那是系统原生 Win32 菜单、由 Explorer 绘制，应用改不了配色；
    /// 而窗口内的右键菜单是我们自己的 Avalonia 控件，可以完全跟随皮肤。
    /// </summary>
    public void ShowMenuAtCursor(Control anchor)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var menu = BuildContextMenu();
                menu.Placement = PlacementMode.Pointer;
                menu.Open(anchor);
            }
            catch (Exception ex)
            {
                Services.Log.Warn("[tray] 弹出右键菜单失败：" + ex.Message);
            }
        });
    }

    /// <summary>构造跟随皮肤的 Avalonia 右键菜单（与托盘原生菜单同结构）。</summary>
    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        var items = menu.Items;

        items.Add(MenuItem(Loc.T("tray.manager"), () => _services.ShowManager()));
        items.Add(MenuItem(Loc.T("common.settings"), () => _services.ShowSettings()));
        items.Add(MenuItem(Loc.T("tray.pomodoro"), () => _services.ShowPomodoro()));
        items.Add(MenuItem("天气", () => _services.OpenToolWindow(ToolWindowKind.Weather)));
        items.Add(MenuItem("世界时钟", () => _services.OpenToolWindow(ToolWindowKind.WorldClock)));
        items.Add(MenuItem("桌面时钟", () => _services.OpenToolWindow(ToolWindowKind.DesktopClock)));

        var boards = new MenuItem { Header = Loc.T("tray.boards") };
        var boardList = _todoService.GetBoards();
        if (boardList.Count == 0)
        {
            boards.Items.Add(new MenuItem { Header = Loc.T("tray.noBoards"), IsEnabled = false });
        }
        else
        {
            foreach (var board in boardList)
            {
                var captured = board;
                var row = new MenuItem
                {
                    Header = $"{captured.Name}（{BoardKinds.DisplayName(captured.Kind)}）",
                    ToggleType = MenuItemToggleType.CheckBox,
                    IsChecked = _windows.IsBoardOpen(captured.Id),
                };
                row.Click += (_, _) =>
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
                boards.Items.Add(row);
            }
        }

        items.Add(boards);

        items.Add(MenuItem(Loc.T("tray.toggleAll") + "  " + HotKeyService.ToggleBoardsHotKey,
            () => _windows.ToggleBoardsVisibility()));

        items.Add(MenuItem(Loc.T("tray.resetWindows"), ResetWindows));

        items.Add(new Separator());
        items.Add(MenuItem(Loc.T("common.exit"), () => _services.ExitApplication()));

        return menu;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>把打开的看板 / 时钟收回当前屏幕右上角。</summary>
    private void ResetWindows()
    {
        var moved = _windows.ResetOpenWindowsToScreen();
        if (moved == 0)
        {
            Notify(Loc.T("tray.resetWindowsNone"), "StickyTodo");
        }
    }

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
