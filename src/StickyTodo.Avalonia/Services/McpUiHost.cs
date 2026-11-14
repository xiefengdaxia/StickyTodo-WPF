using Avalonia.Controls;
using Avalonia.Threading;
using StickyTodo.Core.Services;
using StickyTodo.Mcp;
using StickyTodo.Notes.ViewModels;

namespace StickyTodo.Notes.Services;

/// <summary>
/// Avalonia 侧的 MCP UI 宿主：把窗口/托盘/护眼这些「必须回 UI 线程」的能力交给 MCP 工具层。
/// </summary>
public sealed class McpUiHost : IMcpUiHost
{
    private readonly WindowManager _windows;
    private readonly TodoService _todos;
    private readonly EyeCareFilter _eyeCare;
    private readonly EyeGammaService _eyeGamma;
    private readonly Func<TrayService> _tray;

    public McpUiHost(
        WindowManager windows,
        TodoService todos,
        EyeCareFilter eyeCare,
        EyeGammaService eyeGamma,
        Func<TrayService> tray)
    {
        _windows = windows;
        _todos = todos;
        _eyeCare = eyeCare;
        _eyeGamma = eyeGamma;
        _tray = tray;
    }

    private Dispatcher Dispatcher => Dispatcher.UIThread;

    public T RunOnUi<T>(Func<T> action) =>
        Dispatcher.CheckAccess() ? action() : Dispatcher.Invoke(action);

    public void RunOnUi(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.Invoke(action);
        }
    }

    public bool OpenBoard(string boardId)
    {
        var board = _todos.GetBoard(boardId);
        if (board is null)
        {
            return false;
        }

        return RunOnUi(() =>
        {
            _windows.OpenBoard(board);
            return true;
        });
    }

    public bool CloseBoard(string boardId)
    {
        if (_todos.GetBoard(boardId) is null)
        {
            return false;
        }

        if (!_windows.IsBoardOpen(boardId))
        {
            return true;
        }

        return RunOnUi(() =>
        {
            _windows.CloseBoard(boardId);
            return true;
        });
    }

    public IReadOnlyList<string> OpenBoardIds => _windows.OpenBoardIds;

    public Task RefreshOpenBoardsAsync()
    {
        RunOnUi(_windows.RefreshOpenBoards);
        return Task.CompletedTask;
    }

    public void ReloadCustomBoard(string boardId) => RunOnUi(() => _windows.ReloadCustomBoard(boardId));

    public void ApplyEyeSettings() => RunOnUi(() =>
    {
        _eyeCare.ApplyFromSettings();
        _eyeGamma.ApplyFromSettings();
    });

    public void Notify(string title, string message) => RunOnUi(() => _tray().Notify(title, message));

    public Task<byte[]?> CaptureScreenshotAsync(CancellationToken cancellation = default) =>
        Task.Run(() => ScreenCaptureService.CaptureVirtualScreenPng(), cancellation);
}
