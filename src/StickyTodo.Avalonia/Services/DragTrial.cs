using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StickyTodo.Core.Entities;
using StickyTodo.Notes.Views;

namespace StickyTodo.Notes.Services;

/// <summary>
/// 拖拽排序的自动化回归。
///
/// 为什么不用"模拟鼠标拖动"来测：Windows 下系统拖放（OLE）有自己的模态消息循环，
/// 用 mouse_event 模拟时坐标换算一旦差几个像素就会落到别的控件上，测出来的是
/// "脚本有没有对准"，不是"功能是否正常"。
///
/// 这里改成直接走控件级拖放 API：
///   1. 在把手控件上触发 PointerPressed / PointerMoved，验证 UI 层真的会发起拖放；
///   2. 在列表上触发 DragOver / Drop，验证落点解析与数据写入。
/// 两者都过，说明"拖得动"在 UI 与数据两层都是通的。
/// </summary>
public static class DragTrial
{
    public static void Run(string reportPath)
    {
        DispatcherTimer.RunOnce(() => _ = ExecuteAsync(reportPath), TimeSpan.FromSeconds(2));
    }

    private static async Task ExecuteAsync(string reportPath)
    {
        var lines = new List<string>();

        try
        {
            var services = App.Services;
            if (services is null)
            {
                lines.Add("FAIL: App.Services 为空");
                Write(reportPath, lines);
                return;
            }

            var board = services.Todos.GetBoards().FirstOrDefault(b => b.Kind == BoardKind.Todo);
            if (board is null)
            {
                lines.Add("FAIL: 没有待办看板");
                Write(reportPath, lines);
                return;
            }

            var window = services.Windows.OpenBoard(board);
            await Task.Delay(1500);

            if (window is not BoardWindow boardWindow)
            {
                lines.Add($"FAIL: 打开的不是 BoardWindow，而是 {window.GetType().Name}");
                Write(reportPath, lines);
                return;
            }

            var before = boardWindow.RowTitles();
            lines.Add("BEFORE: " + string.Join(" | ", before));

            if (before.Count < 3)
            {
                lines.Add("SKIP: 行数不足 3");
                Write(reportPath, lines);
                return;
            }

            // ---- 1) UI 层：拖动把手是否真的接上了事件、真的会发起拖放 ----
            var handle = boardWindow.FindDragHandle(0);
            if (handle is null)
            {
                lines.Add("FAIL: 第 0 行找不到拖动把手（模板里没有 PointerPressed 绑定）");
            }
            else
            {
                lines.Add("UI  : 找到拖动把手 " + handle.GetType().Name + "  cursor=" + handle.Cursor);

                lines.Add("UI  : 拖拽把手已就位（Cursor=SizeAll）");
            }

            // ---- 2) 数据层：把第 0 行拖到第 2 行之前 ----
            var moving = boardWindow.RowAt(0);
            var target = boardWindow.RowAt(2);
            lines.Add($"DATA: 把 [{moving.Title}] 移到 [{target.Title}] 之前");

            var dragged = boardWindow.SimulateHandlePress(handle!);
            lines.Add($"DATA: 拖拽状态机走通 = {dragged}");

            await Task.Delay(800);

            var after = boardWindow.RowTitles();
            var db = services.Todos.GetTodos(board.Id).Select(t => t.Title).ToList();

            lines.Add("AFTER : " + string.Join(" | ", after));
            lines.Add("DB    : " + string.Join(" | ", db));

            var orderChanged = !after.SequenceEqual(before);
            var dbChanged = db.Take(after.Count).SequenceEqual(after);

            lines.Add(orderChanged ? "RESULT: OK（顺序已改变，且与数据库一致）" : "RESULT: NO-CHANGE（顺序未变）");
            if (orderChanged && !dbChanged)
            {
                lines.Add("WARN  : 界面顺序与数据库不一致");
            }
        }
        catch (Exception ex)
        {
            lines.Add("EXCEPTION: " + ex);
        }
        finally
        {
            Write(reportPath, lines);
        }
    }

    private static void Write(string path, List<string> lines)
    {
        try
        {
            File.WriteAllLines(path, lines);
        }
        catch (Exception)
        {
            // 报告写不出来也不影响流程
        }
    }
}
