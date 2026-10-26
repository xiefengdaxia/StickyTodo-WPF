using Avalonia.Controls;
using Avalonia.Interactivity;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Localization;
using StickyTodo.Core.Services;
using StickyTodo.Notes.Services;

namespace StickyTodo.Notes.Views;

/// <summary>本地提醒小窗：出现在右下角，可稍后 10 分钟或直接知道了。</summary>
public partial class ReminderPopup : Window
{
    private readonly TodoItem _item;
    private readonly TodoService _service;

    public ReminderPopup(TodoItem item, string boardName, TodoService service)
    {
        _item = item;
        _service = service;

        InitializeComponent();

        TimeText.Text = DateTimeOffset.Now.ToString("HH:mm");
        TitleText.Text = item.Title;
        BoardText.Text = Loc.F("reminder.fromBoard", boardName);

        Opened += (_, _) =>
        {
            Chrome.Apply(this, Shell, 12, 10);
            MoveToBottomRight();

            if (Content is Control content)
            {
                Motion.SlideInFromTop(content, offset: -10);
            }
        };

        // 拖动：整个弹窗任意空白处都能拖（它没有真正的标题栏，用户会直接拖卡片）
        AddHandler(PointerPressedEvent, OnPopupPointerPressed, RoutingStrategies.Tunnel);
    }

    /// <summary>拖动弹窗。点在按钮上时交给按钮，不启动拖动。</summary>
    private void OnPopupPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is Control source)
        {
            var current = source;
            while (current is not null)
            {
                if (current is Button)
                {
                    return;
                }

                current = current.Parent as Control;
            }
        }

        try
        {
            BeginMoveDrag(e);
            e.Handled = true;
        }
        catch (Exception)
        {
            // 某些合成/远程桌面场景下不支持拖动，忽略
        }
    }

    private void MoveToBottomRight()
    {
        var area = ScreenHelper.GetWorkArea(this);
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        var scale = screen?.Scaling ?? 1;

        Width = 300;
        Position = new Avalonia.PixelPoint(
            (int)Math.Round((area.Right - 300 - 8) * scale),
            (int)Math.Round((area.Bottom - Bounds.Height - 8) * scale));
    }

    private void Snooze_Click(object? sender, RoutedEventArgs e)
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

    private void Ok_Click(object? sender, RoutedEventArgs e) => Close();
}
