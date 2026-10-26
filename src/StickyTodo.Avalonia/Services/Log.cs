using System.Text;

namespace StickyTodo.Notes.Services;

/// <summary>
/// 极简本地文件日志。只写本机文件，不联网。
/// 放一个自己的实现而不是引 Serilog，是为了让 Win7 版少几个依赖、启动更快。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string _directory = string.Empty;
    private static bool _ready;

    public static void Init(string directory)
    {
        lock (Gate)
        {
            _directory = directory;
            try
            {
                Directory.CreateDirectory(_directory);
                _ready = true;
            }
            catch (Exception)
            {
                _ready = false;
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Debug(string message) => Write("DEBUG", message);

    private static void Write(string level, string message)
    {
        if (!_ready)
        {
            return;
        }

        try
        {
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(level).Append("] ")
                .Append(message)
                .AppendLine()
                .ToString();

            var file = Path.Combine(_directory, $"app-{DateTime.Now:yyyyMMdd}.log");

            lock (Gate)
            {
                File.AppendAllText(file, line, Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // 日志失败绝不能影响主流程
        }
    }
}
