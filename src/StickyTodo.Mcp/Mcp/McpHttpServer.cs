using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StickyTodo.Mcp;

/// <summary>
/// MCP 的本地 HTTP 服务端（Streamable HTTP 变体）。
///
/// 故意用 TcpListener + 手写最小 HTTP/1.1，而不是 HttpListener/Kestrel：
/// - HTTP.SYS 的 URLACL 在普通用户权限下不一定放行，TcpListener 没有任何权限要求；
/// - 客户端只会 POST/GET 一个端点，最小实现就够，顺带少一个依赖；
/// - 只绑 127.0.0.1，配 Bearer 令牌，局域网/其它网页都够不着。
/// </summary>
public sealed class McpHttpServer : IDisposable
{
    /// <summary>请求体上限（MCP 消息不会太大，给个保险丝）。</summary>
    private const int MaxBodyBytes = 4 * 1024 * 1024;

    private const int MaxHeaderBytes = 32 * 1024;

    /// <summary>首选端口被占时向后顺延尝试的个数。</summary>
    private const int PortScanExtra = 20;

    private readonly McpProtocolHandler _handler;
    private readonly Func<string> _tokenProvider;
    private readonly Func<int> _preferredPortProvider;
    private readonly Action<string>? _log;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public McpHttpServer(McpProtocolHandler handler, Func<string> tokenProvider, Func<int> preferredPortProvider, Action<string>? log = null)
    {
        _handler = handler;
        _tokenProvider = tokenProvider;
        _preferredPortProvider = preferredPortProvider;
        _log = log;
    }

    /// <summary>服务是否在跑。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>实际监听的端口（首选端口被占会顺延；没跑起来为 0）。</summary>
    public int BoundPort { get; private set; }

    /// <summary>启动/停止/端口变化时触发（设置页据此刷新状态文本）。</summary>
    public event EventHandler? StateChanged;

    /// <summary>端点地址，如 http://127.0.0.1:52700/mcp（没跑起来返回 null）。</summary>
    public string? Endpoint => IsRunning ? $"http://127.0.0.1:{BoundPort}/mcp" : null;

    public void Start()
    {
        var preferred = _preferredPortProvider();
        TcpListener? bound = null;
        var boundPort = 0;
        for (var candidate = preferred; candidate <= preferred + PortScanExtra; candidate++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, candidate);
                listener.Start();
                bound = listener;
                boundPort = candidate;
                break;
            }
            catch (SocketException)
            {
                // 端口被占：试下一个
            }
        }

        if (bound is null)
        {
            Log($"MCP 服务启动失败：{preferred}~{preferred + PortScanExtra} 端口都被占用。");
            return;
        }

        _listener = bound;
        BoundPort = boundPort;
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(bound, _cts.Token));
        IsRunning = true;
        Log($"MCP 服务已启动：{Endpoint}");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        Task? loop;
        lock (this)
        {
            if (!IsRunning)
            {
                return;
            }

            IsRunning = false;
            _cts?.Cancel();
            loop = _acceptLoop;
            _listener?.Stop();
            _listener = null;
            _acceptLoop = null;
            BoundPort = 0;
        }

        try
        {
            loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // 退出阶段忽略
        }

        Log("MCP 服务已停止。");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            client.ReceiveTimeout = 15000;
            client.SendTimeout = 15000;
            using var _ = client;
            await using var stream = client.GetStream();

            // ---- 请求行 + 头 ----
            var requestLine = await ReadRequestAsync(stream, ct).ConfigureAwait(false);
            if (requestLine is null)
            {
                return; // 连接空开（端口探测）之类，直接关
            }

            var parts = requestLine.Value.Line.Split(' ');
            var method = parts.Length > 0 ? parts[0].ToUpperInvariant() : string.Empty;
            var path = parts.Length > 1 ? parts[1] : string.Empty;

            // ---- 鉴权（除 OPTIONS 预检外都要令牌）----
            if (method == "OPTIONS")
            {
                await WriteResponseAsync(stream, 204, string.Empty, cors: true).ConfigureAwait(false);
                return;
            }

            if (path is not ("/mcp" or "/mcp/"))
            {
                await WriteResponseAsync(stream, 404, JsonErrorBody(-32000, "只有 /mcp 这个端点。")).ConfigureAwait(false);
                return;
            }

            var token = requestLine.Value.Headers.TryGetValue("authorization", out var auth) ? auth : null;
            if (!IsAuthorized(token))
            {
                await WriteResponseAsync(stream, 401, JsonErrorBody(-32000, "缺少或错误的 Bearer 令牌（应用设置页可查看）。")).ConfigureAwait(false);
                return;
            }

            // ---- 分发 ----
            switch (method)
            {
                case "POST":
                    await HandlePostAsync(stream, requestLine.Value, ct).ConfigureAwait(false);
                    return;
                case "GET":
                    // 没实现 SSE 流：明确 405，客户端会按协议降级只用 POST
                    await WriteResponseAsync(stream, 405, JsonErrorBody(-32000, "本服务端不支持 SSE 流（GET），请用 POST。"), extra: "Allow: POST, DELETE").ConfigureAwait(false);
                    return;
                case "DELETE":
                    // 会话结束（无状态服务端没有会话可销毁）
                    await WriteResponseAsync(stream, 200, string.Empty).ConfigureAwait(false);
                    return;
                default:
                    await WriteResponseAsync(stream, 405, JsonErrorBody(-32000, "不支持的方法。")).ConfigureAwait(false);
                    return;
            }
        }
        catch (Exception ex)
        {
            Log("MCP 连接处理异常：" + ex.Message);
        }
    }

    private async Task HandlePostAsync(NetworkStream stream, (string Line, Dictionary<string, string> Headers, byte[] Body) request, CancellationToken ct)
    {
        var bodyText = Encoding.UTF8.GetString(request.Body);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bodyText);
        }
        catch (Exception)
        {
            await WriteResponseAsync(stream, 400, JsonErrorBody(-32700, "JSON 解析失败。")).ConfigureAwait(false);
            return;
        }

        using (document)
        {
            var response = await _handler.HandleAsync(document.RootElement, ct).ConfigureAwait(false);
            if (response is null)
            {
                // 通知（无 id）：202 Accepted，无响应体
                await WriteResponseAsync(stream, 202, string.Empty).ConfigureAwait(false);
                return;
            }

            await WriteResponseAsync(stream, 200, response).ConfigureAwait(false);
        }
    }

    /// <summary>读请求行、头和 body。返回 null 表示流提前结束。</summary>
    private static async Task<(string Line, Dictionary<string, string> Headers, byte[] Body)?> ReadRequestAsync(
        NetworkStream stream, CancellationToken ct)
    {
        var headerBytes = new MemoryStream();
        var buffer = new byte[4096];
        var headerDone = false;
        var totalHeader = 0;

        while (!headerDone)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            totalHeader += read;
            if (totalHeader > MaxHeaderBytes)
            {
                throw new InvalidOperationException("请求头过大。");
            }

            headerBytes.Write(buffer, 0, read);
            var raw = headerBytes.ToArray();
            var text = Encoding.Latin1.GetString(raw);
            var index = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (index >= 0)
            {
                // 头读到了：把多出来的 body 部分推回去（这里头一定在最后一包里，MCP 消息不大）
                headerDone = true;
            }
        }

        var rawAll = headerBytes.ToArray();
        var rawText = Encoding.Latin1.GetString(rawAll);
        var headerEnd = rawText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0)
        {
            return null;
        }

        var headerText = rawText[..headerEnd];
        var lines = headerText.Split("\r\n");
        var line = lines[0];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon > 0)
            {
                var name = lines[i][..colon].Trim();
                var value = lines[i][(colon + 1)..].Trim();
                headers[name] = value;
            }
        }

        var contentLength = 0;
        if (headers.TryGetValue("content-length", out var lengthText))
        {
            _ = int.TryParse(lengthText, out contentLength);
        }

        if (contentLength < 0 || contentLength > MaxBodyBytes)
        {
            throw new InvalidOperationException("请求体过大。");
        }

        var bodyStart = headerEnd + 4;
        var body = new byte[contentLength];
        var buffered = rawAll.Length - bodyStart;
        if (buffered > 0)
        {
            Array.Copy(rawAll, bodyStart, body, 0, Math.Min(buffered, contentLength));
        }

        var bodyFilled = Math.Min(buffered, contentLength);
        while (bodyFilled < contentLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(bodyFilled, contentLength - bodyFilled), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            bodyFilled += read;
        }

        return (line, headers, body);
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string body, bool cors = false, string? extra = null)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
        builder.Append("Content-Type: application/json; charset=utf-8\r\n");
        builder.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
        builder.Append("Connection: close\r\n");
        if (cors || status == 204)
        {
            builder.Append("Access-Control-Allow-Origin: *\r\n");
            builder.Append("Access-Control-Allow-Methods: POST, GET, DELETE, OPTIONS\r\n");
            builder.Append("Access-Control-Allow-Headers: authorization, content-type, mcp-session-id, mcp-protocol-version\r\n");
        }

        if (extra is not null)
        {
            builder.Append(extra).Append("\r\n");
        }

        builder.Append("\r\n");

        var headBytes = Encoding.Latin1.GetBytes(builder.ToString());
        await stream.WriteAsync(headBytes).ConfigureAwait(false);
        if (bodyBytes.Length > 0)
        {
            await stream.WriteAsync(bodyBytes).ConfigureAwait(false);
        }

        await stream.FlushAsync().ConfigureAwait(false);
    }

    private bool IsAuthorized(string? authorizationHeader)
    {
        var expected = _tokenProvider();
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false; // 没生成过令牌：拒绝一切
        }

        if (authorizationHeader is null)
        {
            return false;
        }

        var parts = authorizationHeader.Split(' ', 2);
        return parts.Length == 2 &&
               parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase) &&
               FixedTimeEquals(parts[1].Trim(), expected);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        // 常数时间比较，避免逐字节短路暴露令牌内容
        var left = Encoding.UTF8.GetBytes(a);
        var right = Encoding.UTF8.GetBytes(b);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static string JsonErrorBody(int code, string message) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = null, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString();

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        202 => "Accepted",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        404 => "Not Found",
        405 => "Method Not Allowed",
        _ => "Error",
    };

    private void Log(string message) => _log?.Invoke(message);
}
