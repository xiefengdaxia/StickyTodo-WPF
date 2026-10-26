using Avalonia.Controls;
using Avalonia.Interactivity;
using StickyTodo.Core.Localization;

namespace StickyTodo.Notes.Views;

/// <summary>通用轻提示：托盘气泡在 Avalonia 里没有等价 API，用一个小窗代替。</summary>
public partial class ToastWindow : Window
{
    public ToastWindow(string title, string body)
    {
        InitializeComponent();

        HeadingText.Text = title;
        BodyText.Text = body;

        Opened += (_, _) =>
        Chrome.Apply(this, Shell, 12, 10);
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            var scale = screen?.Scaling ?? 1;
            var area = screen?.WorkingArea;

            if (area is { } work)
            {
                Position = new Avalonia.PixelPoint(
                    work.X + work.Width - (int)Math.Round(Bounds.Width * scale) - 16,
                    work.Y + work.Height - (int)Math.Round(Bounds.Height * scale) - 16);
            }

            if (Content is Control content)
            {
                Services.Motion.SlideInFromTop(content, offset: -10);
            }
        };
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) => Close();
}
