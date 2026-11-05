using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using StickyTodo.App.Services;

namespace StickyTodo.App.Views;

/// <summary>
/// 贴图窗体：把截图贴在桌面上。
///
/// 交互（参考 QQ 截图）：
///   * 拖动 = 移动；
///   * 未锁定时滚轮 = 等比缩放窗口（图片按 Stretch=Uniform 自适应，不变形）；
///   * 右键 = 置顶 / 锁定（鼠标穿透）/ 原始大小 / 复制 / 另存为 / 关闭；
///   * 双击 = 关闭。
/// 锁定后窗口鼠标穿透，只留右上角一个小徽标用来解锁。
/// </summary>
public partial class PinWindow : Window
{
    private readonly BitmapSource _image;
    private readonly double _baseWidth;
    private readonly double _baseHeight;

    private double _zoom = 1;
    private bool _isTopmost = true;
    private bool _locked;
    private UnlockChipWindow? _unlockChip;

    /// <summary>窗口被关掉时通知管理器（清列表用）。</summary>
    public event EventHandler? PinClosed;

    public PinWindow(BitmapSource image, Point? topLeft = null)
    {
        InitializeComponent();

        _image = image;
        Picture.Source = image;

        // 1:1 显示：图片像素 / 本窗口的 DPI
        var dpi = VisualTreeHelper.GetDpi(this);
        _baseWidth = image.PixelWidth / (dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1);
        _baseHeight = image.PixelHeight / (dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1);

        Width = _baseWidth;
        Height = _baseHeight;

        if (topLeft is { } point)
        {
            Left = point.X;
            Top = point.Y;
        }

        SourceInitialized += (_, _) =>
        {
            ClickThrough.HideFromAltTab(this);

            if (topLeft is null)
            {
                // 没有指定位置：摆在鼠标所在屏幕的中间偏上
                var area = ScreenHelper.GetWorkArea(this);
                Left = area.Left + (area.Width - Width) / 2;
                Top = area.Top + (area.Height - Height) / 3;
            }

            ClampIntoView();
        };

        MouseLeftButtonDown += OnMouseDown;
        MouseWheel += OnWheel;
        MouseRightButtonUp += OnRightButtonUp;
        MouseDoubleClick += (_, _) => Close();

        // Esc = 关闭（贴图窗默认是激活的，点一下图就有键盘焦点）
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };

        Closed += (_, _) =>
        {
            _unlockChip?.Close();
            _unlockChip = null;
            PinClosed?.Invoke(this, EventArgs.Empty);
        };
    }

    public bool IsTopmostPin => _isTopmost;

    public bool IsLockedPin => _locked;

    // ---------------- 移动 / 缩放 ----------------

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_locked)
        {
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 双击时 DragMove 会抛，忽略即可
            }
        }
    }

    /// <summary>滚轮等比缩放：以窗口中心为基准，缩完夹进屏幕。</summary>
    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_locked)
        {
            return;
        }

        ApplyZoom(e.Delta > 0 ? 1.1 : 1 / 1.1);
        e.Handled = true;
    }

    /// <summary>按倍数缩放（滚轮和回归测试共用）。保持长宽比。</summary>
    public void ApplyZoom(double factor)
    {
        var next = Math.Clamp(_zoom * factor, 0.15, 5);

        if (Math.Abs(next - _zoom) < 0.001)
        {
            return;
        }

        var centerX = Left + (Width / 2);
        var centerY = Top + (Height / 2);

        _zoom = next;
        Width = _baseWidth * _zoom;
        Height = _baseHeight * _zoom;

        Left = centerX - (Width / 2);
        Top = centerY - (Height / 2);

        ClampIntoView();
    }

    /// <summary>当前缩放比例（1 = 截图原始尺寸）。</summary>
    public double Zoom => _zoom;

    /// <summary>截图原始尺寸下的窗口大小（DIP）。</summary>
    public (double Width, double Height) BaseSize => (_baseWidth, _baseHeight);

    /// <summary>恢复到截图的原始尺寸。</summary>
    public void ResetZoom()
    {
        var centerX = Left + (Width / 2);
        var centerY = Top + (Height / 2);

        _zoom = 1;
        Width = _baseWidth;
        Height = _baseHeight;

        Left = centerX - (Width / 2);
        Top = centerY - (Height / 2);

        ClampIntoView();
    }

    private void ClampIntoView()
    {
        var area = ScreenHelper.GetWorkArea(this);
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }

    // ---------------- 置顶 / 锁定 ----------------

    public void ToggleTopmost()
    {
        _isTopmost = !_isTopmost;
        Topmost = _isTopmost;

        if (_unlockChip is not null)
        {
            ClickThrough.PlaceAbove(_unlockChip, this);
        }
    }

    public void SetLocked(bool locked)
    {
        _locked = locked;
        ClickThrough.Apply(this, locked);

        if (!locked)
        {
            _unlockChip?.Close();
            _unlockChip = null;
            return;
        }

        _unlockChip = new UnlockChipWindow(this, Shell, () =>
        {
            SetLocked(false);
        });

        _unlockChip.Show();
        _unlockChip.FollowAnchor();
    }

    // ---------------- 右键菜单 ----------------

    private void OnRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_locked)
        {
            return;
        }

        var menu = new ContextMenu();

        var topmost = new MenuItem
        {
            Header = "置顶",
            IsCheckable = true,
            IsChecked = _isTopmost,
        };
        topmost.Click += (_, _) => ToggleTopmost();
        menu.Items.Add(topmost);

        var lockItem = new MenuItem
        {
            Header = "锁定（鼠标穿透）",
            IsCheckable = true,
            IsChecked = _locked,
        };
        lockItem.Click += (_, _) => SetLocked(true);
        menu.Items.Add(lockItem);

        menu.Items.Add(new Separator());

        var reset = new MenuItem { Header = "原始大小" };
        reset.Click += (_, _) => ResetZoom();
        menu.Items.Add(reset);

        var copy = new MenuItem { Header = "复制图片" };
        copy.Click += (_, _) => CopyImage();
        menu.Items.Add(copy);

        var save = new MenuItem { Header = "另存为…" };
        save.Click += (_, _) => SaveImage();
        menu.Items.Add(save);

        menu.Items.Add(new Separator());

        var close = new MenuItem { Header = "关闭" };
        close.Click += (_, _) => Close();
        menu.Items.Add(close);

        menu.PlacementTarget = Shell;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void CopyImage()
    {
        try
        {
            Clipboard.SetImage(_image);
        }
        catch (Exception)
        {
            // 剪贴板被别的程序占着：忽略
        }
    }

    private void SaveImage()
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存贴图",
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg",
            FileName = $"StickyTodo-{DateTime.Now:yyyyMMdd-HHmmss}.png",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            BitmapEncoder encoder = dialog.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                ? new JpegBitmapEncoder { QualityLevel = 92 }
                : new PngBitmapEncoder();

            encoder.Frames.Add(BitmapFrame.Create(_image));

            using var stream = System.IO.File.Create(dialog.FileName);
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            ConfirmWindow.Notify(this, "保存失败", ex.Message);
        }
    }
}
