using System.Runtime.InteropServices;
using StickyTodo.Infrastructure;
using StickyTodo.Infrastructure.Persistence;

namespace StickyTodo.Notes.Services;

/// <summary>
/// 无 UI 冒烟测试入口（--selftest）。发布脚本在打包后调用，
/// 确认运行时能启动、数据目录可写、SQLite 原生库可加载。
/// </summary>
internal static class SelfTest
{
    public static int Run()
    {
        var lines = new List<string>();
        var passed = true;

        lines.Add("StickyTodo Avalonia - selftest");
        lines.Add($"OS       : {RuntimeInformation.OSDescription} ({Environment.OSVersion.Version})");
        lines.Add($"Runtime  : {RuntimeInformation.FrameworkDescription}");
        lines.Add($"Arch     : {RuntimeInformation.ProcessArchitecture}");
        lines.Add($"Win7     : {Win7Compat.IsWindows7OrOlder}");

        try
        {
            AppPaths.EnsureCreated();
            lines.Add($"DataDir  : {AppPaths.DataDirectory}");
            lines.Add($"Portable : {AppPaths.IsPortable}");
        }
        catch (Exception exception)
        {
            passed = false;
            lines.Add($"DataDir  : FAIL - {exception.GetType().Name}: {exception.Message}");
        }

        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"stickytodo-avalonia-selftest-{Guid.NewGuid():N}.db");

        try
        {
            var repository = new SqliteTodoRepository(databasePath);
            repository.Initialize();

            var board = repository.AddBoard("selftest", isDefault: true);
            var todo = repository.AddTodo(board.Id, "sqlite-ok");
            var found = repository.GetTodos(board.Id).Any(item => item.Id == todo.Id);

            if (!found)
            {
                throw new InvalidOperationException("写入后读不到记录");
            }

            lines.Add("SQLite   : PASS");
        }
        catch (Exception exception)
        {
            passed = false;
            lines.Add($"SQLite   : FAIL - {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            TryDelete(databasePath);
            TryDelete(databasePath + "-wal");
            TryDelete(databasePath + "-shm");
        }

        lines.Add(passed ? "RESULT   : OK" : "RESULT   : FAILED");

        try
        {
            var reportPath = Path.Combine(Directory.GetCurrentDirectory(), "avalonia-selftest.txt");
            File.WriteAllLines(reportPath, lines);
            lines.Add($"Report   : {reportPath}");
        }
        catch
        {
            // 报告写不出来时仍然靠退出码判断
        }

        foreach (var line in lines)
        {
            Console.WriteLine(line);
        }

        return passed ? 0 : 1;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}