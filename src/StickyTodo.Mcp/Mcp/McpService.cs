using System.Security.Cryptography;
using System.Text;
using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Pomodoro;
using StickyTodo.Core.Services;
using StickyTodo.Weather;

namespace StickyTodo.Mcp;

/// <summary>
/// MCP 接入的组装与生命周期（两端共用）：工具注册表 + 协议处理 + HTTP 服务端 + 采集器。
/// UI 栈只做三件事：构造它、启动时 <see cref="ApplyFromSettings"/>、退出时 Dispose。
/// 设置页的开关/端口变更也走 <see cref="ApplyFromSettings"/>（未变化时是空操作）。
/// </summary>
public sealed class McpService : IDisposable
{
    private readonly AppSettingsService _settings;
    private readonly string _tokenPath;
    private readonly string _endpointPath;
    private readonly Action<string>? _log;

    private string? _lastToken;

    private readonly CollectorHost _collector;

    public McpService(
        IMcpUiHost ui,
        TodoService todos,
        AppSettingsService settings,
        PomodoroEngine pomodoro,
        WeatherService weather,
        ITokenStatsProvider tokens,
        string tokenPath,
        string endpointPath,
        Action<string>? log = null)
    {
        _settings = settings;
        _tokenPath = tokenPath;
        _endpointPath = endpointPath;
        _log = log;

        _collector = new CollectorHost(todos);
        Registry = new McpToolRegistry(todos, settings, pomodoro, weather, tokens, _collector, ui);
        Handler = new McpProtocolHandler(Registry);
        Server = new McpHttpServer(Handler, ReadToken, () => settings.McpPort, Log);
    }

    /// <summary>数据采集器：进程活着就一直在跑（只有配了脚本的看板才有开销）。</summary>
    public CollectorHost Collector => _collector;

    public McpToolRegistry Registry { get; }

    public McpProtocolHandler Handler { get; }

    public McpHttpServer Server { get; }

    /// <summary>启动采集器（幂等）。与 MCP 开关无关——看板数据采集独立于接入开关。</summary>
    public void StartCollector() => Collector.Start();

    /// <summary>
    /// 按设置同步 MCP 服务状态：开关没开 → 停；开了 → 确保令牌存在并启动（端口/令牌变了会重启）。
    /// </summary>
    public void ApplyFromSettings()
    {
        if (!_settings.McpEnabled)
        {
            if (Server.IsRunning)
            {
                Server.Stop();
                ClearEndpointFile();
            }

            return;
        }

        var token = EnsureToken();
        var needsRestart = !Server.IsRunning || Server.BoundPort != _settings.McpPort || _lastToken != token;
        if (!needsRestart)
        {
            return;
        }

        Server.Stop();
        Server.Start();
        _lastToken = token;

        if (Server.IsRunning)
        {
            WriteEndpointFile(Server.BoundPort);
        }
        else
        {
            ClearEndpointFile();
        }
    }

    /// <summary>读令牌（没有则生成并落盘）。stdio 桥与 HTTP 服务端共用这一个来源。</summary>
    public string EnsureToken()
    {
        var existing = ReadToken();
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToHexString(bytes).ToLowerInvariant();
        File.WriteAllText(_tokenPath, token + Environment.NewLine, Encoding.ASCII);
        Log("已生成新的 MCP 接入令牌。");
        return token;
    }

    /// <summary>只读令牌，不生成（设置页展示用；没启用过 MCP 时为 null）。</summary>
    public string? PeekToken()
    {
        var token = ReadToken();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    /// <summary>给 AI 客户端用的接入配置片段（HTTP + stdio 两种写法）。复制时顺手确保令牌存在。</summary>
    public string BuildClientConfigSnippet()
    {
        var token = EnsureToken();
        var port = Server.IsRunning ? Server.BoundPort : _settings.McpPort;
        var endpoint = $"http://127.0.0.1:{port}/mcp";
        var exe = (Environment.ProcessPath ?? "StickyTodo.exe").Replace("\\", "\\\\");

        // 模板里全是 JSON 花括号，手拼比内插原始字符串省事
        var lines = new[]
        {
            "// 方式一：HTTP（推荐，ZCode / Claude Code 等）",
            "{",
            "  \"mcpServers\": {",
            "    \"stickytodo\": {",
            "      \"url\": \"" + endpoint + "\",",
            "      \"headers\": { \"Authorization\": \"Bearer " + token + "\" }",
            "    }",
            "  }",
            "}",
            string.Empty,
            "// 方式二：stdio（Claude Desktop 等；需应用已启用 MCP）",
            "{",
            "  \"mcpServers\": {",
            "    \"stickytodo\": {",
            "      \"command\": \"" + exe + "\",",
            "      \"args\": [\"--mcp\"]",
            "    }",
            "  }",
            "}",
        };
        return string.Join(Environment.NewLine, lines);
    }

    private string ReadToken()
    {
        try
        {
            return File.ReadAllText(_tokenPath).Trim();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private void WriteEndpointFile(int port)
    {
        try
        {
            File.WriteAllText(_endpointPath, "127.0.0.1:" + port);
        }
        catch (Exception ex)
        {
            Log("写 mcp.endpoint 失败：" + ex.Message);
        }
    }

    private void ClearEndpointFile()
    {
        try
        {
            File.Delete(_endpointPath);
        }
        catch (Exception)
        {
            // 没有这个文件很正常
        }
    }

    private void Log(string message) => _log?.Invoke(message);

    public void Dispose()
    {
        Server.Dispose();
        Collector.Dispose();
        ClearEndpointFile();
    }
}
