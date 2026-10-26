using System.IO;
using Microsoft.Win32;

namespace StickyTodo.Notes.Services;

/// <summary>
/// 开机自启：写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run。
/// 用当前用户级注册表项，不需要管理员权限，也不污染系统级配置。
/// </summary>
public sealed class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "StickyTodo";

    /// <summary>注册表里现在是否已经有本程序的启动项。</summary>
    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>注册表里的命令（用于判断是否需要更新路径）。</summary>
    public string? CurrentCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 开/关自启。返回是否成功；失败时<paramref name="error"/>带原因。
    /// </summary>
    public bool SetEnabled(bool enabled, out string? error)
    {
        error = null;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                error = "无法打开注册表启动项";
                return false;
            }

            if (enabled)
            {
                var exe = ExecutablePath();
                if (string.IsNullOrWhiteSpace(exe))
                {
                    error = "拿不到程序路径";
                    return false;
                }

                // 带引号，防止路径里有空格；带 --autostart 让程序启动时只恢复看板、不弹管理面板
                key.SetValue(ValueName, $"\"{exe}\" --autostart", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 让注册表与"用户期望值"保持一致：路径变了（比如换了安装位置）要更新。
    /// </summary>
    public void Sync(bool desired)
    {
        var current = CurrentCommand();
        var exe = ExecutablePath();
        var expected = string.IsNullOrWhiteSpace(exe) ? null : $"\"{exe}\" --autostart";

        if (!desired)
        {
            if (current is not null)
            {
                SetEnabled(false, out _);
            }

            return;
        }

        if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
        {
            SetEnabled(true, out _);
        }
    }

    /// <summary>当前 exe 的完整路径（单文件发布时也正确）。</summary>
    public static string ExecutablePath()
    {
        // 优先用 ProcessPath：拼进单个 exe 时 Assembly.Location 会是空串
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var location = Environment.GetCommandLineArgs().FirstOrDefault();
        return location is not null && location.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(location)
            : string.Empty;
    }
}
