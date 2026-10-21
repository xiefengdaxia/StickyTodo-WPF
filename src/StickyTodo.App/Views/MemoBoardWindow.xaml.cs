using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StickyTodo.App.Services;
using StickyTodo.App.ViewModels;
using StickyTodo.Core.Entities;

namespace StickyTodo.App.Views;

/// <summary>
/// 便签/随笔窗口：无边框半透明 + 一整块可写的纸，停止输入 800ms 后自动保存。
/// </summary>
public partial class MemoBoardWindow : Window, IStickyWindow
{
    private const double ShadowMargin = 10d;
    private const double ResizeBorder = 8d;
    private const int CascadeStep = 28;

    private readonly MemoBoardViewModel _viewModel;
    private readonly WindowManager _windows;
    private readonly DispatcherTimer _saveTimer;
    private bool _geometryRestored;
    private bool _isFullScreen;

    public MemoBoardWindow(MemoBoardViewModel viewModel, WindowManager windows)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _windows = windows;
        DataContext = viewModel;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await _viewModel.SaveIfDirtyAsync();
        };

        Loaded += async (_, _) =>
        {
            RestoreGeometry();
            await _viewModel.LoadAsync();
            if (!_viewModel.IsLocked)
            {
                Editor.Focus();
            }

            Motion.FadeInWindow(this);
        };

        Closing += async (_, _) =>
        {
            _saveTimer.Stop();
            SaveGeometry();
            await _viewModel.SaveIfDirtyAsync();
        };

        StateChanged += (_, _) =>
        {
            _isFullScreen = WindowState == WindowState.Maximized;
            if (_isFullScreen)
            {
                NoteRoot.CornerRadius = new CornerRadius(0);
                NoteRoot.Effect = null;
                NoteRoot.Margin = new Thickness(0);
            }
        };
    }

    public string BoardId => _viewModel.BoardId;

    public void ToggleLock() => _viewModel.ToggleLock();

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    public void RefreshAppearance() => _viewModel.RefreshAppearance();

    private void Editor_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _viewModel.MarkDirty();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleTopmost_Click(object sender, RoutedEventArgs e) => _viewModel.ToggleTopmost();

    private void ToggleLock_Click(object sender, RoutedEventArgs e) => _viewModel.ToggleLock();

    private void Appearance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            AppearanceMenu.Show(element, _viewModel, _windows);
            e.Handled = true;
        }
    }

    private void OpenManager_Click(object sender, RoutedEventArgs e) =>
        (System.Windows.Application.Current.MainWindow as ManagerWindow)?.ShowAndActivate();

    // ---------------- 位置记忆（便签窗口不参与吸附，避免两张纸互相粘住） ----------------

    private void RestoreGeometry()
    {
        if (_geometryRestored)
        {
            return;
        }

        _geometryRestored = true;

        if (_viewModel.Board.Window is { IsValid: true } state)
        {
            Width = state.Width;
            Height = state.Height;
            var (left, top) = ScreenHelper.ClampIntoWorkArea(this, state.Left, state.Top, state.Width, state.Height);
            Left = left;
            Top = top;
            return;
        }

        var offset = CascadeStep * (_windows.OpenWindowCount + 1);
        var (cascadeLeft, cascadeTop) = ScreenHelper.ClampIntoWorkArea(this, 80 + offset, 80 + offset, Width, Height);
        Left = cascadeLeft;
        Top = cascadeTop;
    }

    private void SaveGeometry()
    {
        if (!_geometryRestored || WindowState != WindowState.Normal)
        {
            return;
        }

        _viewModel.Board.Window = new BoardWindowState(Left, Top, ActualWidth, ActualHeight);
        _windows.SaveBoardGeometry(_viewModel.Board);
    }

    // ---------------- 边缘缩放 ----------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != 0x0084 || _isFullScreen || ResizeMode == ResizeMode.NoResize)
        {
            return nint.Zero;
        }

        long raw = lParam.ToInt64();
        var point = new NativePoint
        {
            X = (int)(short)(raw & 0xFFFF),
            Y = (int)(short)((raw >> 16) & 0xFFFF),
        };

        if (!NativeMethods.ScreenToClient(hwnd, ref point))
        {
            return nint.Zero;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var x = point.X / dpi.DpiScaleX;
        var y = point.Y / dpi.DpiScaleY;

        var onLeft = x <= ResizeBorder;
        var onRight = x >= ActualWidth - ResizeBorder;
        var onTop = y <= ResizeBorder;
        var onBottom = y >= ActualHeight - ResizeBorder;

        var hit = onTop && onLeft ? 13
            : onTop && onRight ? 14
            : onBottom && onLeft ? 16
            : onBottom && onRight ? 17
            : onLeft ? 10
            : onRight ? 11
            : onTop ? 12
            : onBottom ? 15
            : 0;

        if (hit == 0)
        {
            return nint.Zero;
        }

        handled = true;
        return new nint(hit);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _saveTimer.Stop();
        SaveGeometry();
        base.OnClosing(e);
    }
}