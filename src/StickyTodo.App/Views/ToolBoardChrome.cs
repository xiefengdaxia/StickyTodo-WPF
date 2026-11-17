using System.Windows;
using System.Windows.Controls;
using StickyTodo.App.Services;
using StickyTodo.Core.Entities;

namespace StickyTodo.App.Views;

/// <summary>
/// 工具型看板（天气 / 世界时钟 / 桌面时钟 / 网速 / 性能）标题栏上的通用按钮：
/// 置顶 📌、锁定 🔒、看板管理 ☰。
///
/// 行为和待办看板完全一致，不另起一套：
/// 置顶 = 切窗口 <see cref="Window.Topmost"/> 并落 <c>board.{id}.topmost</c>；
/// 锁定 = 落 <c>board.{id}.locked</c> + 整窗鼠标穿透，并在锁定按钮位置浮一个解锁徽标。
/// </summary>
internal static class ToolBoardChrome
{
    /// <summary>接上三颗按钮的点击与初始状态（必须在 InitializeComponent 之后调用）。</summary>
    public static void Attach(
        Window window,
        Board board,
        Button pinButton,
        Button lockButton,
        Button manageButton,
        Action<bool>? topmostChanged = null)
    {
        var settings = App.Settings;
        UnlockChipWindow? chip = null;

        void SetPin(bool value)
        {
            window.Topmost = value;
            App.Settings?.SetBoardTopmost(board.Id, value);
            pinButton.Opacity = value ? 1 : 0.5;
            topmostChanged?.Invoke(value);
        }

        void SetLocked(bool value)
        {
            ClickThrough.Apply(window, value);
            App.Settings?.SetBoardLocked(board.Id, value);

            // 锁定时窗口已经点不到了：🔒 自己藏起来，让位给浮在上面的解锁徽标
            lockButton.Content = value ? "🔒" : "🔓";
            lockButton.Opacity = value ? 0 : 0.5;

            if (!value)
            {
                chip?.Close();
                chip = null;
                return;
            }

            if (chip is { IsLoaded: true })
            {
                return;
            }

            chip = new UnlockChipWindow(window, lockButton, () => SetLocked(false));
            chip.Closed += (_, _) => chip = null;
            chip.Show();
            chip.FollowAnchor();
        }

        // 置顶可以建句柄之前就设，锁定的穿透必须等 HWND 建好。
        // 工具窗原来没有置顶概念：用户没点过 📌 就保持原来的"不置顶"，点过就按存下来的值恢复。
        window.Topmost = settings is not null
                         && settings.HasBoardTopmost(board.Id)
                         && settings.GetBoardTopmost(board.Id);
        pinButton.Opacity = window.Topmost ? 1 : 0.5;
        lockButton.Opacity = 0.5;

        pinButton.Click += (_, _) => SetPin(!window.Topmost);
        lockButton.Click += (_, _) => SetLocked(App.Settings?.GetBoardLocked(board.Id) != true);

        manageButton.Click += (_, _) =>
            (System.Windows.Application.Current.MainWindow as ManagerWindow)?.ShowAndActivate();

        window.SourceInitialized += (_, _) =>
        {
            if (App.Settings?.GetBoardLocked(board.Id) == true)
            {
                SetLocked(true);
            }
        };

        window.Closed += (_, _) =>
        {
            chip?.Close();
            chip = null;
            ClickThrough.Reset(window);
        };
    }
}
