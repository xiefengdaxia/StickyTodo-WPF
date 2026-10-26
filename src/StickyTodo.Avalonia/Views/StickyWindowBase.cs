using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Geometry;
using StickyTodo.Core.Localization;
using StickyTodo.Core.Services;
using StickyTodo.Notes.Services;
using StickyTodo.Notes.ViewModels;

namespace StickyTodo.Notes.Views;

/// <summary>
/// 所有便签窗口的公共部分：
///   * 无边框窗口的拖动（自己实现，不依赖系统标题栏）
///   * 位置/大小记忆（写回 Board.Window）
///   * 与其它便签窗口的吸附对齐
///   * 外观菜单（皮肤 + 背景透明度 + 整窗透明度）
///   * IStickyWindow（全局快捷键需要）
/// </summary>
public abstract class StickyWindowBase : Window, IStickyWindow
{
    /// <summary>纸面到窗口边缘的留白，给阴影用。</summary>
    protected const double ShadowMargin = 10;

    /// <summary>吸附触发阈值（逻辑像素）。</summary>
    protected const double SnapThreshold = 12;

    /// <summary>没记录过位置时，多个窗口层叠排开的步长。</summary>
    protected const int CascadeStep = 26;

    private DispatcherTimer? _saveTimer;
    private bool _geometryRestored;
    private bool _isSnapping;

    protected StickyWindowBase(WindowManager windows, AppSettingsService settings)
    {
        Windows = windows;
        Settings = settings;
    }

    protected WindowManager Windows { get; }

    protected AppSettingsService Settings { get; }

    protected abstract Board Board { get; }

    protected abstract IAppearanceHost Appearance { get; }

    public string BoardId => Board.Id;

    public abstract void ToggleLock();

    public void RefreshAppearance()
    {
        Appearance.RefreshAppearance();
        ApplyNoteChrome();
    }

    public void ShowAndActivate()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <summary>
    /// 子类在构造末尾调用：把标题栏接上拖动、把窗体外壳接上几何记忆。
    /// </summary>
    protected void InitStickyWindow(Control titleBar, Control noteRoot)
    {
        _noteRoot = noteRoot;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveGeometry();
        };

        titleBar.PointerPressed += OnTitleBarPressed;

        // 最大化时要额外去掉圆角与留白
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                ApplyNoteChrome();
            }
        };

        Opened += async (_, _) =>
        {
            ApplyNoteChrome();
            RestoreGeometry();
            await OnWindowLoadedAsync();

            // 位置已经定好，再做入场（先定位后淡入，避免看到"从别处跳过来"）
            Motion.FadeInWindow(this);
        };

        PositionChanged += (_, _) =>
        {
            if (!_geometryRestored || _isSnapping)
            {
                return;
            }

            SnapToNeighbours();
            ScheduleSave();
        };

        SizeChanged += (_, _) =>
        {
            if (_geometryRestored)
            {
                ScheduleSave();
            }
        };

        Closing += (_, _) =>
        {
            _saveTimer?.Stop();
            SaveGeometry();
        };

        // 无边框窗口：自己判断指针是否落在边缘，落在边缘就切成缩放光标 + 拖拽缩放
        AddHandler(PointerMovedEvent, OnEdgePointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnEdgePointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>边缘判定宽度（逻辑像素）。</summary>
    private const double ResizeBorder = 6;

    private void OnEdgePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!CanResize || WindowState != WindowState.Normal)
        {
            return;
        }

        var edge = HitEdge(e.GetPosition(this));
        Cursor = edge switch
        {
            Edge.Left or Edge.Right => new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast),
            Edge.Top or Edge.Bottom => new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeNorthSouth),
            Edge.TopLeft or Edge.BottomRight => new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.TopLeftCorner),
            Edge.TopRight or Edge.BottomLeft => new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.TopRightCorner),
            _ => Avalonia.Input.Cursor.Default,
        };
    }

    private void OnEdgePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!CanResize || WindowState != WindowState.Normal)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var edge = HitEdge(e.GetPosition(this));
        if (edge == Edge.None)
        {
            return;
        }

        try
        {
            BeginResizeDrag(ToWindowEdge(edge), e);
            e.Handled = true;
        }
        catch (Exception)
        {
            // 某些合成/远程桌面场景下不支持缩放，忽略
        }
    }

    private enum Edge { None, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    private Edge HitEdge(Avalonia.Point position)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;

        var onLeft = position.X <= ResizeBorder;
        var onRight = position.X >= width - ResizeBorder;
        var onTop = position.Y <= ResizeBorder;
        var onBottom = position.Y >= height - ResizeBorder;

        return (onTop, onBottom, onLeft, onRight) switch
        {
            (true, _, true, _) => Edge.TopLeft,
            (true, _, _, true) => Edge.TopRight,
            (_, true, true, _) => Edge.BottomLeft,
            (_, true, _, true) => Edge.BottomRight,
            (true, _, _, _) => Edge.Top,
            (_, true, _, _) => Edge.Bottom,
            (_, _, true, _) => Edge.Left,
            (_, _, _, true) => Edge.Right,
            _ => Edge.None,
        };
    }

    private static WindowEdge ToWindowEdge(Edge edge) => edge switch
    {
        Edge.Left => WindowEdge.West,
        Edge.Right => WindowEdge.East,
        Edge.Top => WindowEdge.North,
        Edge.Bottom => WindowEdge.South,
        Edge.TopLeft => WindowEdge.NorthWest,
        Edge.TopRight => WindowEdge.NorthEast,
        Edge.BottomLeft => WindowEdge.SouthWest,
        Edge.BottomRight => WindowEdge.SouthEast,
        _ => WindowEdge.North,
    };

    private void ApplyWindowStateStyle(Control noteRoot)
    {
        var maximized = WindowState == WindowState.Maximized;

        if (noteRoot is Border border)
        {
            border.CornerRadius = maximized ? new Avalonia.CornerRadius(0) : new Avalonia.CornerRadius(12);
            border.Margin = maximized ? new Avalonia.Thickness(0) : new Avalonia.Thickness(ShadowMargin);
            border.BoxShadow = maximized ? default : Avalonia.Media.BoxShadows.Parse("0 3 18 0 #4D000000");
        }
    }

    private Control? _noteRoot;

    /// <summary>
    /// 便签外壳外观：透明能力（Win7 没有）+ 最大化状态共同决定。
    /// 透明不可用时切成不透明直角外观，否则 RedirectionSurface 下会整片发黑。
    /// </summary>
    private void ApplyNoteChrome()
    {
        if (_noteRoot is not Border border)
        {
            return;
        }

        var transparent = ActualTransparencyLevel != WindowTransparencyLevel.None;
        var skin = App.Services?.Skin.Current;
        Avalonia.Media.IBrush opaque = skin is null
            ? Avalonia.Media.Brushes.Transparent
            : new Avalonia.Media.SolidColorBrush(skin.BackgroundBottom);

        TransparencyBackgroundFallback = opaque;

        if (!transparent)
        {
            border.CornerRadius = new Avalonia.CornerRadius(0);
            border.Margin = new Avalonia.Thickness(0);
            border.BoxShadow = default;
            border.BorderThickness = default;
            Background = opaque;
            return;
        }

        ApplyWindowStateStyle(border);
    }

    /// <summary>窗口第一次显示后由子类去加载数据。</summary>
    protected virtual Task OnWindowLoadedAsync() => Task.CompletedTask;

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // 标题栏上的按钮不参与拖动
        if (e.Source is Control control && IsInsideButton(control))
        {
            return;
        }

        try
        {
            BeginMoveDrag(e);
        }
        catch (Exception)
        {
            // 某些合成/远程桌面场景下不支持拖动，忽略
        }
    }

    private static bool IsInsideButton(Control control)
    {
        var current = control;
        while (current is not null)
        {
            if (current is Button or CheckBox or TextBox or ComboBox)
            {
                return true;
            }

            current = current.Parent as Control;
        }

        return false;
    }

    // ---------------- 位置记忆与吸附 ----------------

    private void RestoreGeometry()
    {
        if (_geometryRestored)
        {
            return;
        }

        _geometryRestored = true;

        if (Board.Window is { IsValid: true } state)
        {
            Width = state.Width;
            Height = state.Height;
            var (left, top) = ScreenHelper.ClampIntoWorkArea(this, state.Left, state.Top, state.Width, state.Height);
            Position = ToPixel(new Avalonia.Point(left, top));
            return;
        }

        // 没记录过：从管理面板附近层叠排开
        var offset = CascadeStep * (Windows.OpenWindowCount + 1);
        var (cascadeLeft, cascadeTop) = ScreenHelper.ClampIntoWorkArea(this, 60 + offset, 60 + offset, Width, Height);
        Position = ToPixel(new Avalonia.Point(cascadeLeft, cascadeTop));
    }

    /// <summary>便签纸面（不含阴影留白）在屏幕上的矩形。</summary>
    private SnapRect CurrentNoteRect()
    {
        var p = CurrentPositionInLogicalUnits();
        return new SnapRect(
            p.X + ShadowMargin,
            p.Y + ShadowMargin,
            Math.Max(1, Width - (ShadowMargin * 2)),
            Math.Max(1, Height - (ShadowMargin * 2)));
    }

    private Avalonia.Point CurrentPositionInLogicalUnits()
    {
        var scale = CurrentScaling();
        return new Avalonia.Point(Position.X / scale, Position.Y / scale);
    }

    private double CurrentScaling()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        var scale = screen?.Scaling ?? 1;
        return scale <= 0 ? 1 : scale;
    }

    private Avalonia.PixelPoint ToPixel(Avalonia.Point logical)
    {
        var scale = CurrentScaling();
        return new Avalonia.PixelPoint(
            (int)Math.Round(logical.X * scale),
            (int)Math.Round(logical.Y * scale));
    }

    private void SnapToNeighbours()
    {
        if (WindowState != WindowState.Normal || !IsVisible)
        {
            return;
        }

        var note = CurrentNoteRect();
        var workArea = ScreenHelper.GetWorkArea(this);
        var others = Windows.GetOtherWindowRects(this, ShadowMargin);

        var snapped = WindowSnapper.Snap(note, workArea, others, SnapThreshold);
        if (Math.Abs(snapped.Left - note.Left) < 0.5 && Math.Abs(snapped.Top - note.Top) < 0.5)
        {
            return;
        }

        _isSnapping = true;
        try
        {
            Position = ToPixel(new Avalonia.Point(snapped.Left - ShadowMargin, snapped.Top - ShadowMargin));
        }
        finally
        {
            _isSnapping = false;
        }
    }

    private void ScheduleSave()
    {
        _saveTimer?.Stop();
        _saveTimer?.Start();
    }

    private void SaveGeometry()
    {
        if (!_geometryRestored || WindowState != WindowState.Normal)
        {
            return;
        }

        var p = CurrentPositionInLogicalUnits();
        Board.Window = new BoardWindowState(p.X, p.Y, Width, Height);
        Windows.SaveBoardGeometry(Board);
    }

    // ---------------- 外观菜单 ----------------

    /// <summary>「🎨 外观」菜单：皮肤 + 背景透明度 + 整窗透明度。</summary>
    protected void ShowAppearanceMenu(Control target)
    {
        var menu = AppearanceMenu.Build(Appearance, Windows);
        menu.PlacementTarget = target;
        menu.Open(target);
    }

    // ---------------- 看板改名 ----------------

    protected async Task RenameBoardViaPromptAsync(Func<string, Task> rename)
    {
        var name = await PromptWindow.PromptAsync(this, Loc.T("common.rename"), Loc.T("prompt.boardName"), Board.Name);
        if (name is not null)
        {
            await rename(name);
        }
    }
}
