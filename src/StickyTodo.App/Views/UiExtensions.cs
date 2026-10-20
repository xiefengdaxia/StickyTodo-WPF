using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StickyTodo.App.Views;

/// <summary>界面小工具：拖动反馈用的缩放变换。</summary>
internal static class UiExtensions
{
    /// <summary>
    /// 给控件套一个以中心为原点的缩放。<paramref name="scale"/> 为 1 时移除变换。
    /// 每次都新建变换对象，避免共享导致的相互影响。
    /// </summary>
    internal static void ScaleTransformForDrag(this FrameworkElement element, double scale)
    {
        if (element.RenderTransform is ScaleTransform existing && existing.IsFrozen is false)
        {
            existing.ScaleX = scale;
            existing.ScaleY = scale;
            return;
        }

        if (Math.Abs(scale - 1.0) < 0.001)
        {
            element.RenderTransform = Transform.Identity;
            return;
        }

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = new ScaleTransform(scale, scale);
    }
}