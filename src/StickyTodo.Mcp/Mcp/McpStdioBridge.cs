using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StickyTodo.Mcp;

/// <summary>
/// MCP stdio 桥：给只支持 stdio 的 MCP 客户端（Claude Desktop、Cursor 等）用。
///
/// 应用主程序带 <c>--mcp</c> 参数启动时走到这里（不建 UI、不占单实例互斥量）：
/// 从 stdin 逐行读 JSON-RPC 消息 → POST 到正在运行的应用的 MCP HTTP 端点 → stdout 回写响应。
/// 应用没在跑时自动把它拉起来（无参数正常启动），轮询端点直到可达。
///
/// 注意：WinExe 没有控制台，但客户端是以「重定向管道」方式拉起本进程的，
/// 所以 Console.In/Out 的重定向句柄照常工作；未重定向时直接退出。
/// </summary>
public static class McpStdioBridge
{
    public const string BridgeArgument = "--mcp";

    /// <summary>等应用起起来的时长（毫秒）。</summary>
    private const int LaunchWaitMs = 12000;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public static bool HasFlag(IReadOnlyList<string> args) =>
        args.Any(a => string.Equals(a, BridgeArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 桥主循环。返回进程退出码。
    /// </summary>
    /// <param name="endpointFile">应用写的 mcp.endpoint 文件（内容 host:port），优先于 preferredPort。</param>
    /// <param name="preferredPort">读不到 endpoint 文件时的回退端口（应用的设置值）。</param>
    /// <param name="tokenPath">Bearer 令牌文件路径。</param>
    /// <param name="appExePath">应用 exe（自动拉起用），不存在则跳过自动启动。</param>
    public static int Run(string endpointFile, int preferredPort, string tokenPath, string? appExePath)
    {
        if (!Console.IsInputRedirected)
        {
            // 双击 exe / 命令行直跑：不是 MCP 客户端在说话，直接退出别挂住
            return 1;
        }

        // 桥进程只做转发，日志写 stderr 不污染 stdout 的 JSON-RPC 流
        using var error = Console.Error;

        int port;
        try
        {
            port = ReadEndpointPort(endpointFile) ?? preferredPort;
        }
        catch (Exception)
        {
            port = preferredPort;
        }

        var token = ReadToken(tokenPath);
        var endpoint = $"http://127.0.0.1:{port}/mcp";

        if (!WaitForAppAsync(endpoint, appExePath, error).GetAwaiter().GetResult())
        {
            error.WriteLine($"StickyTodo MCP 桥：{LaunchWaitMs}ms 内没能连上 {endpoint}（应用没有运行或 MCP 未启用）。");
            return 2;
        }

        // 不设 Console.OutputEncoding：管道输出下没有控制台屏幕缓冲区，那行会抛
        // 「句柄无效」。stdout 用下面显式的 UTF-8（无 BOM）StreamWriter 写。
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        using var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);

        string? line;
        while ((line = stdin.ReadLine()) is not null)
        {
            var message = line.Trim();
            if (message.Length == 0)
            {
                continue;
            }

            var response = ForwardAsync(endpoint, token, message).GetAwaiter().GetResult();
            if (response is not null)
            {
                stdout.WriteLine(response);
            }
        }

        return 0;
    }

    private static int? ReadEndpointPort(string endpointFile)
    {
        var text = File.ReadAllText(endpointFile).Trim();
        // 内容形如 127.0.0.1:52703
        var colon = text.LastIndexOf(':');
        return colon >= 0 && int.TryParse(text[(colon + 1)..], out var port) ? port : null;
    }

    private static string ReadToken(string tokenPath)
    {
        try
        {
            return File.ReadAllText(tokenPath).Trim();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>先探测端点；不通则尝试拉起应用并轮询。返回最终是否可达。</summary>
    private static async Task<bool> WaitForAppAsync(string endpoint, string? appExePath, TextWriter error)
    {
        if (await ReachableAsync(endpoint).ConfigureAwait(false))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(appExePath) && File.Exists(appExePath))
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(appExePath)
                {
                    UseShellExecute = true,
                });
                error.WriteLine("StickyTodo MCP 桥：应用没有在运行，已自动启动，等待 MCP 端点就绪…");
            }
            catch (Exception ex)
            {
                error.WriteLine("StickyTodo MCP 桥：自动启动应用失败：" + ex.Message);
            }
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(LaunchWaitMs);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500).ConfigureAwait(false);
            if (await ReachableAsync(endpoint).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>能不能建立 TCP 连接（不等同鉴权通过，只说明应用和 MCP 在跑）。</summary>
    private static async Task<bool> ReachableAsync(string endpoint)
    {
        try
        {
            var uri = new Uri(endpoint);
            using var client = new TcpClient();
            await client.ConnectAsync(uri.Host, uri.Port).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>转发一条 JSON-RPC 消息。返回要写回 stdout 的响应（通知返回 null）。</summary>
    private static async Task<string?> ForwardAsync(string endpoint, string token, string message)
    {
        var hasId = HasRequestId(message);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            request.Content = new StringContent(message, Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request).ConfigureAwait(false);

            if (!hasId)
            {
                return null; // 通知：无论 202 还是错误都不回写
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return ErrorResponse(message, -32000, $"应用返回了空响应（HTTP {(int)response.StatusCode}）。");
            }

            return body;
        }
        catch (Exception ex)
        {
            if (!hasId)
            {
                return null;
            }

            return ErrorResponse(message, -32000, "转发失败：" + ex.Message);
        }
    }

    /// <summary>粗判 JSON-RPC 报文有没有 id（决定要不要回写 stdout）。</summary>
    private static bool HasRequestId(string message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            return document.RootElement.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string ErrorResponse(string message, int code, string error)
    {
        JsonNode? id = null;
        try
        {
            using var document = JsonDocument.Parse(message);
            if (document.RootElement.TryGetProperty("id", out var idNode) && idNode.ValueKind != JsonValueKind.Null)
            {
                id = JsonNode.Parse(idNode.GetRawText());
            }
        }
        catch (Exception)
        {
        }

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = error },
        }.ToJsonString();
    }
}
