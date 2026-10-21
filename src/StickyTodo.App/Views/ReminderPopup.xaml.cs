using System.Windows;
using StickyTodo.App.Services;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Services;

namespace StickyTodo.App.Views;

/// <summary>本地提醒小窗：出现在右下角，可稍后 10 分钟或直接知道了。</summary>
public partial class ReminderPopup : Window
{
    private readonly TodoItem _item;
    private readonly TodoService _service;

    public ReminderPopup(TodoItem item, string boardName, TodoService service)
    {
        InitializeComponent();
        _item = item;
        _service = service;

        TimeText.Text = DateTimeOffset.Now.ToString("HH:mm");
        TitleText.Text = item.Title;
        BoardText.Text = $"来自看板：{boardName}";

        Loaded += (_, _) =>
        {
            MoveToBottomRight();

            // 从下方滑入 + 淡入：提醒出现得更"有动静"，不容易被忽略
            if (Content is FrameworkElement content)
            {
                Motion.SlideInFromTop(content, offset: -10);
            }
        };
    }

    private void MoveToBottomRight()
    {
        var area = ScreenHelper.GetWorkArea(this);
        Left = area.Right - ActualWidth - 8;
        Top = area.Bottom - ActualHeight - 8;
    }

    private void Snooze_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _service.Snooze(_item.Id, TimeSpan.FromMinutes(10));
        }
        catch (Exception)
        {
            // 待办可能已被删除，忽略
        }

        Close();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}