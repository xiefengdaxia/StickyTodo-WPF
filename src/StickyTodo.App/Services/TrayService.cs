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
    private readonly EyeCareFilter _eyeCare;
    private readonly EyeGammaService _eyeGamma;

    private TrayIconHost? _tray;
    private WinForms.ContextMenuStrip? _menu;
    private WinForms.ToolStripMenuItem? _boardsMenu;
    private PomodoroWindow? _pomodoroWindow;
    private WinForms.ToolStripMenuItem? _eyeFilterToggle;
    private WinForms.ToolStripMenuItem? _eyeGammaToggle;
    private WinForms.ToolStripMenuItem? _eyeTempToggle;
    private WinForms.ToolStripMenuItem? _eyeTempCompositorItem;
    private readonly List<(WinForms.ToolStripMenuItem Item, string Color)> _eyePresetItems = new();
    private readonly List<(WinForms.ToolStripMenuItem Item, int Strength)> _eyeStrengthItems = new();
    private readonly List<(WinForms.ToolStripMenuItem Item, int Kelvin)> _eyeTempItems = new();

    public TrayService(TodoService todoService, WindowManager windows, IServiceProvider services, EyeCareFilter eyeCare, EyeGammaService eyeGamma)
    {
        _todoService = todoService;
        _windows = windows;
        _services = services;
        _eyeCare = eyeCare;
        _eyeGamma = eyeGamma;
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

        // 桌面时钟没有自己的外观菜单，透明度放在这里
        var clockOpacity = new WinForms.ToolStripMenuItem(Loc.T("tray.clockOpacity"));
        foreach (var percent in new[] { 40, 55, 70, 85, 100 })
        {
            var value = percent / 100d;
            var item = new WinForms.ToolStripMenuItem($"{percent}%");
            item.Click += (_, _) => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                _menu?.Close();

                var settings = System.Windows.Application.Current is App app ? App.Settings : null;
                settings?.SetWindowOpacity("tool.desktopclock", value);

                foreach (Window window in System.Windows.Application.Current!.Windows)
                {
                    if (window is DesktopClockWindow)
                    {
                        window.Opacity = value;
                    }
                }
            });
            clockOpacity.DropDownItems.Add(item);
        }

        clockOpacity.DropDownItems.Add(new WinForms.ToolStripSeparator());
        var clockFollow = new WinForms.ToolStripMenuItem(Loc.T("menu.opacityFollowGlobal"));
        clockFollow.Click += (_, _) => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            _menu?.Close();
            App.Settings?.ClearWindowOpacity("tool.desktopclock");

            foreach (Window window in System.Windows.Application.Current!.Windows)
            {
                if (window is DesktopClockWindow)
                {
                    window.Opacity = App.Settings?.GetWindowOpacity("tool.desktopclock") ?? 1.0;
                }
            }
        });
        clockOpacity.DropDownItems.Add(clockFollow);

        menu.Items.Add(clockOpacity);

        // 背景透明度（纸色 alpha，全局共用）：桌面时钟没有自己的 🎨 菜单，和整窗透明度放一起方便调
        var clockBackground = new WinForms.ToolStripMenuItem(
            Loc.F("menu.bgOpacity", Math.Round((App.Settings?.Opacity ?? 0.92) * 100)));
        foreach (var percent in new[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 })
        {
            var value = percent / 100d;
            var item = new WinForms.ToolStripMenuItem($"{percent}%");
            item.Click += (_, _) => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                _menu?.Close();

                if (_services.GetService(typeof(SkinService)) is SkinService skin)
                {
                    skin.SetOpacity(value);
                }
            });
            clockBackground.DropDownItems.Add(item);
        }

        menu.Items.Add(clockBackground);

        // 护眼：豆沙绿校色 + 防蓝光滤镜，豆沙绿皮肤快捷入口
        var eye = new WinForms.ToolStripMenuItem(Loc.T("menu.eye"));

        // 色温：只改颜色比例，画面不发灰
        _eyeTempToggle = new WinForms.ToolStripMenuItem(Loc.T("menu.eyeTemp")) { CheckOnClick = true };
        _eyeTempToggle.Click += (_, _) => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            _menu?.Close();
            var settings = App.Settings;
            if (settings is null)
            {
                return;
            }

            settings.EyeTempEnabled = _eyeTempToggle.Checked;

            if (_eyeTempToggle.Checked)
            {
                settings.EyeGammaEnabled = false;
                settings.EyeFilterEnabled = false;
            }

            _eyeGamma.ApplyFromSettings();
            _eyeCare.ApplyFromSettings();
        });
        eye.DropDownItems.Add(_eyeTempToggle);

        var tempLevels = new WinForms.ToolStripMenuItem(Loc.T("menu.eyeTempLevels"));
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
            var tempItem = new WinForms.ToolStripMenuItem(Loc.T(key)) { CheckOnClick = false };
            tempItem.Click += (_, _) => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                _menu?.Close();
                var settings = App.Settings;
                if (settings is null)
                {
                    return;
                }

                settings.EyeTempKelvin = kelvin;
                settings.EyeTempEnabled = true;
                settings.EyeGammaEnabled = false;
                settings.EyeFilterEnabled = false;
                _eyeGamma.ApplyFromSettings();
                _eyeCare.ApplyFromSettings();
            });
            _eyeTempItems.Add((tempItem, kelvin));
            tempLevels.DropDownItems.Add(tempItem);
        }

        eye.DropDownItems.Add(tempLevels);

        _eyeTempCompositorItem = new WinForms.ToolStripMenuItem(Loc.T("menu.eyeTempCompositor")) { CheckOnClick = true };
        _eyeTempCompositorItem.Click += (_, _) => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            _menu?.Close();
            var settings = App.Settings;
            if (settings is null)
            {
                return;
            }

            settings.EyeTempCompositor = _eyeTempCompositorItem.Checked;
            _eyeGamma.ApplyFromSettings();
        });
        eye.DropDownItems.Add(_eyeTempCompositorItem);

        eye.DropDownItems.Add(new WinForms.ToolStripSeparator());

        _eyeGammaToggle = new WinForms.ToolStripMenuItem(Loc.T("menu.eyeGamma")) { CheckOnClick = true };
        _eyeGammaToggle.Click += (_, _) => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            _menu?.Close();
            var settings = App.Settings;
            if (settings is null)
            {
                return;
            }

            settings.EyeGammaEnabled = _eyeGammaToggle.Checked;

            if (_eyeGammaToggle.Checked)
            {
                settings.EyeTempEnabled = false;
                settings.EyeFilterEnabled = false;
            }

            _eyeGamma.ApplyFromSettings();
            _eyeCare.ApplyFromSettings();
        });
        eye.DropDownItems.Add(_eyeGammaToggle);

        _eyeFilterToggle = new WinForms.ToolStripMenuItem(Loc.T("settings.eyeFilter")) { CheckOnClick = true };
        _eyeFilterToggle.Click += (_, _) => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            _menu?.Close();
            var settings = App.Settings;
            if (settings is null)
            {
                return;
            }

            settings.EyeFilterEnabled = _eyeFilterToggle.Checked;

            if (_eyeFilterToggle.Checked)
            {
                settings.EyeTempEnabled = false;
                settings.EyeGammaEnabled = false;
            }

            _eyeCare.ApplyFromSettings();
            _eyeGamma.ApplyFromSettings();
        });
        eye.DropDownItems.Add(_eyeFilterToggle);

        eye.DropDownItems.Add(new WinForms.ToolStripSeparator());

        foreach (var (hex, key) in EyeCareFilter.PresetList)
        {
            var preset = new WinForms.ToolStripMenuItem(Loc.T(key)) { CheckOnClick = false };
            preset.Click += (_, _) => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                _menu?.Close();
                var settings = App.Settings;
                if (settings is null)
                {
                    return;
                }

                settings.EyeFilterColor = hex;
                settings.EyeFilterEnabled = true;
                settings.EyeTempEnabled = false;
                settings.EyeGammaEnabled = false;
                _eyeCare.ApplyFromSettings();
                _eyeGamma.ApplyFromSettings();
            });
            _eyePresetItems.Add((preset, hex));
            eye.DropDownItems.Add(preset);
        }

        foreach (var (strength, key) in new[] { (15, "eye.strength.light"), (25, "eye.strength.medium"), (40, "eye.strength.heavy") })
        {
            var strengthItem = new WinForms.ToolStripMenuItem(Loc.T(key)) { CheckOnClick = false };
            strengthItem.Click += (_, _) => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                _menu?.Close();
                var settings = App.Settings;
                if (settings is null)
                {
                    return;
                }

                settings.EyeFilterStrength = strength;
                settings.EyeFilterEnabled = true;
                settings.EyeTempEnabled = false;
                settings.EyeGammaEnabled = false;
                _eyeCare.ApplyFromSettings();
                _eyeGamma.ApplyFromSettings();
            });
            _eyeStrengthItems.Add((strengthItem, strength));
            eye.DropDownItems.Add(strengthItem);
        }

        eye.DropDownItems.Add(new WinForms.ToolStripSeparator());

        var eyeSprout = new WinForms.ToolStripMenuItem(Loc.T("menu.eyeSprout"));
        eyeSprout.Click += (_, _) => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            _menu?.Close();
            if (_services.GetService(typeof(SkinService)) is SkinService skin)
            {
                skin.SetSkin(NoteSkins.BeanSprout.Key);
                _windows.ApplySkin();
            }
        });
        eye.DropDownItems.Add(eyeSprout);

        var eyeSettings = new WinForms.ToolStripMenuItem(Loc.T("menu.eyeSettings"));
        eyeSettings.Click += (_, _) => (System.Windows.Application.Current as App)?.ShowSettings();
        eye.DropDownItems.Add(eyeSettings);

        menu.Items.Add(eye);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        // 截图 / 贴图
        var pins = _services.GetRequiredService<PinManager>();

        var snip = new WinForms.ToolStripMenuItem(Loc.T("tray.snip"))
        {
            ShortcutKeyDisplayString = HotKeyService.SnipHotKey,
            ShowShortcutKeys = true,
        };
        snip.Click += (_, _) => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            _menu?.Close();
            pins.CaptureAndPin();
        });
        menu.Items.Add(snip);

        var pinAgain = new WinForms.ToolStripMenuItem(Loc.T("tray.pinAgain"))
        {
            ShortcutKeyDisplayString = HotKeyService.PinAgainHotKey,
            ShowShortcutKeys = true,
        };
        pinAgain.Click += (_, _) => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            _menu?.Close();
            pins.PinLast();
        });
        menu.Items.Add(pinAgain);

        var unlockPins = new WinForms.ToolStripMenuItem(Loc.T("tray.unlockPins"));
        unlockPins.Click += (_, _) => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            _menu?.Close();
            pins.UnlockAll();
        });
        menu.Items.Add(unlockPins);

        var closePins = new WinForms.ToolStripMenuItem(Loc.T("tray.closePins"));
        closePins.Click += (_, _) => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            _menu?.Close();
            pins.CloseAll();
        });
        menu.Items.Add(closePins);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        _boardsMenu = new WinForms.ToolStripMenuItem(Loc.T("tray.boards"));
        menu.Items.Add(_boardsMenu);

        // 快捷键走 ShortcutKeyDisplayString：菜单会自动右对齐，不会挤在标题后面
        var toggleAll = new WinForms.ToolStripMenuItem(Loc.T("tray.toggleAll"))
        {
            ShortcutKeyDisplayString = HotKeyService.ToggleBoardsHotKey,
            ShowShortcutKeys = true,
        };
        toggleAll.Click += (_, _) => _windows.ToggleBoardsVisibility();
        menu.Items.Add(toggleAll);

        // 换显示器 / 改分辨率后窗口跑到屏幕外时，一键收回当前屏幕右上角
        var resetWindows = new WinForms.ToolStripMenuItem(Loc.T("tray.resetWindows"));
        resetWindows.Click += (_, _) => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            // 先把菜单收起来，免得窗口移动时菜单还挂在屏幕上
            _menu?.Close();

            var moved = _windows.ResetOpenWindowsToScreen();
            if (moved == 0)
            {
                Notify(Loc.T("tray.resetWindowsNone"), "StickyTodo");
            }
        });
        menu.Items.Add(resetWindows);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var exit = new WinForms.ToolStripMenuItem(Loc.T("common.exit"));
        exit.Click += (_, _) => ExitApplication();
        menu.Items.Add(exit);

        // 用固定 GUID 登记：被杀掉的旧进程留下的图标会被这次登记接管，不再有僵尸图标
        _tray = new TrayIconHost(AppIcon.TrayIcon, "StickyTodo", ShowManager, ShowMenuAtCursor);
        menu.Opening += (_, _) => RefreshEyeMenuStates();

        RebuildBoardsMenu();
        _windows.BoardsChanged += (_, _) => RebuildBoardsMenu();
    }

    /// <summary>菜单弹出前同步护眼项的勾选态（设置页可能刚改过）。</summary>
    private void RefreshEyeMenuStates()
    {
        if (_eyeFilterToggle is null || _eyeGammaToggle is null || _eyeTempToggle is null
            || _eyeTempCompositorItem is null)
        {
            return;
        }

        var settings = App.Settings;
        _eyeGammaToggle.Checked = settings?.EyeGammaEnabled ?? false;
        _eyeFilterToggle.Checked = settings?.EyeFilterEnabled ?? false;
        _eyeTempToggle.Checked = settings?.EyeTempEnabled ?? false;

        var kelvin = settings?.EyeTempKelvin ?? AppSettingsService.DefaultEyeTempKelvin;
        var tempOn = settings?.EyeTempEnabled ?? false;
        _eyeTempCompositorItem.Checked = settings?.EyeTempCompositor ?? false;
        foreach (var (item, value) in _eyeTempItems)
        {
            item.Checked = tempOn && value == kelvin;
        }

        var color = settings?.EyeFilterColor ?? string.Empty;
        foreach (var (item, hex) in _eyePresetItems)
        {
            item.Checked = string.Equals(hex, color, StringComparison.OrdinalIgnoreCase);
        }

        var strength = settings?.EyeFilterStrength ?? AppSettingsService.DefaultEyeFilterStrength;
        foreach (var (item, value) in _eyeStrengthItems)
        {
            item.Checked = value == strength
                || (value == 15 && strength <= 15)
                || (value == 25 && strength is > 15 and < 40)
                || (value == 40 && strength >= 40);
        }
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
    public void ShowMenuAtCursor()
    {
        try
        {
            if (_menu is null)
            {
                return;
            }

            // 每次弹出前套一遍皮肤：用户可能刚换过皮肤
            if (_services.GetService(typeof(SkinService)) is SkinService skin)
            {
                TrayMenuSkin.Apply(_menu, skin.Current);
            }

            // 关键：先把托盘窗口抢到前台，菜单才会在「点别处」时收到失活消息自动收起。
            // 少这一步的典型症状就是菜单弹出来就赖着不走。
            _tray?.BringToForeground();
            _menu.Show(WinForms.Cursor.Position);
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