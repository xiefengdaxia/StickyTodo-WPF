using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace StickyTodo.App.Services;

/// <summary>
/// 统一的小动效。原则：
///   * 只做"确认发生了"的反馈，不做花哨的装饰；
///   * 时长都在 120–260ms，慢了会显得卡；
///   * 一律允许在系统设置"关闭动画"时静默跳过（见 <see cref="Enabled"/>）。
/// </summary>
public static class Motion
{
    /// <summary>
    /// 是否启用动效。用户可以在 Windows「辅助功能 → 视觉效果 → 动画效果」里关掉，
    /// 那时我们也跟着关，避免让需要减少动效的人不适。
    /// </summary>
    public static bool Enabled { get; set; } = ReadSystemPreference();

    private static bool ReadSystemPreference()
    {
        try
        {
            // 系统关闭动画时这个值是 false
            return SystemParameters.ClientAreaAnimation;
        }
        catch (Exception)
        {
            return true;
        }
    }

    public static readonly Duration Fast = new(TimeSpan.FromMilliseconds(120));
    public static readonly Duration Base = new(TimeSpan.FromMilliseconds(180));
    public static readonly Duration Slow = new(TimeSpan.FromMilliseconds(260));

    private static readonly IEasingFunction Ease = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction Pop = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 });

    private static T Freeze<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }

    /// <summary>
    /// 窗口入场：淡入 + 轻微放大。
    /// 动画作用在窗口**内容**上而不是 Window 本身——给 Window 设 RenderTransform 会让
    /// AllowsTransparency 的窗口在动画期间被位图化，文字发虚。
    /// </summary>
    public static void FadeInWindow(Window window, double fromScale = 0.985, Duration? duration = null)
    {
        if (!Enabled || window is null)
        {
            return;
        }

        if (window.Content is not FrameworkElement content)
        {
            return;
        }

        FadeInElement(content, fromScale, duration);
    }

    /// <summary>元素入场：淡入 + 轻微放大。窗口内容和普通元素都能用。</summary>
    public static void FadeInElement(FrameworkElement element, double fromScale = 0.985, Duration? duration = null)
    {
        if (!Enabled || element is null)
        {
            return;
        }

        var d = duration ?? Base;

        if (element.RenderTransform is not ScaleTransform scale)
        {
            scale = new ScaleTransform(fromScale, fromScale);
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = scale;
        }

        element.Opacity = 0;

        var storyboard = new Storyboard();

        var fade = new DoubleAnimation(0, 1, d) { EasingFunction = Ease };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, new PropertyPath(nameof(UIElement.Opacity)));
        storyboard.Children.Add(fade);

        if (fromScale < 1)
        {
            foreach (var property in new[]
                     {
                         $"{nameof(UIElement.RenderTransform)}.({nameof(ScaleTransform.ScaleX)})",
                         $"{nameof(UIElement.RenderTransform)}.({nameof(ScaleTransform.ScaleY)})",
                     })
            {
                var grow = new DoubleAnimation(fromScale, 1, d) { EasingFunction = Ease };
                Storyboard.SetTarget(grow, element);
                Storyboard.SetTargetProperty(grow, new PropertyPath(property));
                storyboard.Children.Add(grow);
            }
        }

        storyboard.Begin(element, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>给任意元素做一次"弹一下"的强调（用于完成待办、刷新成功这类瞬时反馈）。</summary>
    public static void Pulse(FrameworkElement element, double peak = 1.06)
    {
        if (!Enabled || element is null)
        {
            return;
        }

        if (element.RenderTransform is not ScaleTransform scale)
        {
            scale = new ScaleTransform(1, 1);
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = scale;
        }

        var storyboard = new Storyboard();

        foreach (var property in new[]
                 {
                     $"{nameof(UIElement.RenderTransform)}.({nameof(ScaleTransform.ScaleX)})",
                     $"{nameof(UIElement.RenderTransform)}.({nameof(ScaleTransform.ScaleY)})",
                 })
        {
            var up = new DoubleAnimation(1, peak, new Duration(TimeSpan.FromMilliseconds(90)))
            {
                EasingFunction = Ease,
            };
            Storyboard.SetTarget(up, element);
            Storyboard.SetTargetProperty(up, new PropertyPath(property));
            storyboard.Children.Add(up);

            var down = new DoubleAnimation(peak, 1, Base)
            {
                BeginTime = TimeSpan.FromMilliseconds(90),
                EasingFunction = Ease,
            };
            Storyboard.SetTarget(down, element);
            Storyboard.SetTargetProperty(down, new PropertyPath(property));
            storyboard.Children.Add(down);
        }

        storyboard.Begin(element, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// 元素淡出并在结束后执行回调（用于删除行、关闭弹层）。
    /// 关掉动效时直接执行回调。
    /// </summary>
    public static void FadeOutAndRemove(FrameworkElement element, Action? after = null, Duration? duration = null)
    {
        if (!Enabled || element is null)
        {
            after?.Invoke();
            return;
        }

        var fade = new DoubleAnimation(element.Opacity, 0, duration ?? Fast) { EasingFunction = Ease };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, new PropertyPath(nameof(UIElement.Opacity)));

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Completed += (_, _) => after?.Invoke();
        storyboard.Begin(element, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// 让元素持续旋转（用于"正在加载"的图标）。返回一个可停止的句柄。
    /// 关掉动效时返回 null，图标保持静止。
    /// </summary>
    public static IDisposable? Spin(FrameworkElement element, double secondsPerTurn = 0.9)
    {
        if (!Enabled || element is null)
        {
            return null;
        }

        if (element.RenderTransform is not RotateTransform rotate)
        {
            rotate = new RotateTransform(0);
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = rotate;
        }

        var animation = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(secondsPerTurn)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };

        rotate.BeginAnimation(RotateTransform.AngleProperty, animation);
        return new SpinHandle(rotate);
    }

    private sealed class SpinHandle : IDisposable
    {
        private readonly RotateTransform _rotate;

        public SpinHandle(RotateTransform rotate) => _rotate = rotate;

        public void Dispose() => _rotate.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    /// <summary>把一个元素从上方几像素处滑入并淡入（用于新增行、提醒弹窗、Toast）。</summary>
    public static void SlideInFromTop(FrameworkElement element, double offset = 8)
    {
        if (!Enabled || element is null)
        {
            return;
        }

        if (element.RenderTransform is not TranslateTransform translate)
        {
            translate = new TranslateTransform();
            element.RenderTransform = translate;
        }

        var storyboard = new Storyboard();

        var slide = new DoubleAnimation(-offset, 0, Slow) { EasingFunction = Ease };
        Storyboard.SetTarget(slide, element);
        Storyboard.SetTargetProperty(slide, new PropertyPath($"{nameof(UIElement.RenderTransform)}.({nameof(TranslateTransform.Y)})"));
        storyboard.Children.Add(slide);

        var fade = new DoubleAnimation(0, 1, Slow) { EasingFunction = Ease };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, new PropertyPath(nameof(UIElement.Opacity)));
        storyboard.Children.Add(fade);

        storyboard.Begin(element, HandoffBehavior.SnapshotAndReplace);
    }
}