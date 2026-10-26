using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace StickyTodo.Notes.Services;

/// <summary>
/// 统一的小动效。原则：
///   * 只做"确认发生了"的反馈，不做花哨的装饰；
///   * 时长都在 120–260ms，慢了会显得卡；
///   * 一律允许在系统设置"关闭动画"时静默跳过（见 <see cref="Enabled"/>）。
///
/// Avalonia 版用 <see cref="Animation"/> + 时钟推进，接口语义与 WPF 版保持一致。
/// </summary>
public static class Motion
{
    /// <summary>
    /// 是否启用动效。用户可以在 Windows「辅助功能 → 视觉效果 → 动画效果」里关掉。
    /// Avalonia 没有 SystemParameters.ClientAreaAnimation 的等价物，
    /// 这里读注册表同一个开关，读不到就默认开启。
    /// </summary>
    public static bool Enabled { get; set; } = ReadSystemPreference();

    private static bool ReadSystemPreference()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Control Panel\Desktop\WindowMetrics");
            var value = key?.GetValue("MinAnimate") as string;
            return value is null || value != "0";
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan Base = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(260);

    /// <summary>
    /// 窗口入场：淡入 + 轻微放大。
    /// 动画作用在窗口**内容**而不是 Window 本身——给 Window 设缩放会让文字发虚。
    /// </summary>
    public static void FadeInWindow(Window window, double fromScale = 0.985, TimeSpan? duration = null)
    {
        if (!Enabled || window is null)
        {
            return;
        }

        if (window.Content is Control content)
        {
            FadeInElement(content, fromScale, duration);
        }
    }

    /// <summary>元素入场：淡入 + 轻微放大。窗口内容和普通元素都能用。</summary>
    public static void FadeInElement(Control element, double fromScale = 0.985, TimeSpan? duration = null)
    {
        if (!Enabled || element is null)
        {
            return;
        }

        var d = duration ?? Base;

        element.RenderTransformOrigin = RelativePoint.Center;

        var animation = new Animation
        {
            Duration = d,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                KeyFrame(0, (Visual.OpacityProperty, 0d)),
                KeyFrame(1, (Visual.OpacityProperty, 1d)),
            },
        };

        _ = animation.RunAsync(element);
    }

    /// <summary>给任意元素做一次"弹一下"的强调（用于完成待办、刷新成功这类瞬时反馈）。</summary>
    public static void Pulse(Control element, double peak = 1.06)
    {
        if (!Enabled || element is null)
        {
            return;
        }

        element.RenderTransformOrigin = RelativePoint.Center;

        var transform = new ScaleTransform(1, 1);
        element.RenderTransform = transform;

        _ = AnimateScaleAsync(element, transform, peak);
    }

    private static async Task AnimateScaleAsync(Control element, ScaleTransform transform, double peak)
    {
        try
        {
            var animation = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(90),
                Easing = new CubicEaseOut(),
                FillMode = FillMode.Forward,
                Children =
                {
                    KeyFrame(0, (ScaleTransform.ScaleXProperty, 1d), (ScaleTransform.ScaleYProperty, 1d)),
                    KeyFrame(1, (ScaleTransform.ScaleXProperty, peak), (ScaleTransform.ScaleYProperty, peak)),
                },
            };
            await animation.RunAsync(transform);

            var back = new Animation
            {
                Duration = Base,
                Easing = new CubicEaseOut(),
                FillMode = FillMode.Forward,
                Children =
                {
                    KeyFrame(0, (ScaleTransform.ScaleXProperty, peak), (ScaleTransform.ScaleYProperty, peak)),
                    KeyFrame(1, (ScaleTransform.ScaleXProperty, 1d), (ScaleTransform.ScaleYProperty, 1d)),
                },
            };
            await back.RunAsync(transform);
        }
        catch (Exception)
        {
            // 元素被回收时动画会中断，忽略
        }
    }

    /// <summary>
    /// 元素淡出并在结束后执行回调（用于删除行、关闭弹层）。
    /// 关掉动效时直接执行回调。
    /// </summary>
    public static void FadeOutAndRemove(Control element, Action? after = null, TimeSpan? duration = null)
    {
        if (!Enabled || element is null)
        {
            after?.Invoke();
            return;
        }

        _ = FadeOutAsync(element, after, duration ?? Fast);
    }

    private static async Task FadeOutAsync(Control element, Action? after, TimeSpan duration)
    {
        try
        {
            var animation = new Animation
            {
                Duration = duration,
                Easing = new CubicEaseOut(),
                FillMode = FillMode.Forward,
                Children =
                {
                    KeyFrame(0, (Visual.OpacityProperty, element.Opacity)),
                    KeyFrame(1, (Visual.OpacityProperty, 0d)),
                },
            };
            await animation.RunAsync(element);
        }
        catch (Exception)
        {
            // 忽略
        }

        after?.Invoke();
    }

    /// <summary>把一个元素从上方几像素处滑入并淡入（用于新增行、提醒弹窗、Toast）。</summary>
    public static void SlideInFromTop(Control element, double offset = 8)
    {
        if (!Enabled || element is null)
        {
            return;
        }

        element.RenderTransform = new TranslateTransform(0, -offset);
        element.Opacity = 0;

        var animation = new Animation
        {
            Duration = Slow,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                KeyFrame(0, (Visual.OpacityProperty, 0d)),
                KeyFrame(1, (Visual.OpacityProperty, 1d)),
                KeyFrame(0, (TranslateTransform.YProperty, -offset)),
                KeyFrame(1, (TranslateTransform.YProperty, 0d)),
            },
        };

        _ = animation.RunAsync(element);
    }

    private static KeyFrame KeyFrame(double cue, params (AvaloniaProperty Property, double Value)[] setters)
    {
        var frame = new KeyFrame { Cue = new Cue(cue) };
        foreach (var (property, value) in setters)
        {
            frame.Setters.Add(new Setter(property, value));
        }

        return frame;
    }
}
