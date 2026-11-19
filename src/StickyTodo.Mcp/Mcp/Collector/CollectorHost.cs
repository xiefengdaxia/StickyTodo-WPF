using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jint;
using Jint.Native;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Services;

namespace StickyTodo.Mcp;

/// <summary>
/// 自定义看板的数据采集器：按看板上 AI 编写的 JS 脚本定时抓取数据。
///
/// 安全模型：
/// - 脚本跑在 Jint 沙盒里（全新 Engine、内存/语句/超时限额），拿不到文件、进程、数据库；
/// - 唯一的能力出口是 <c>http_get / http_post / http</c> 三个网络函数（只能 http/https）；
/// - 约定脚本定义 <c>collect()</c> 并同步返回可 JSON 序列化的对象（先用 http 抓数据再整理）。
///
/// 运行模型：后台 1 秒节拍循环，每 30 秒与看板列表重新同步；
/// 到期的看板并发丢到线程池执行，结果（或错误）缓存在内存里，通过
/// <see cref="DataChanged"/> 通知窗口刷新。看板没开时也照常采集，重开窗口即刻有数据。
/// </summary>
public sealed class CollectorHost : IDisposable
{
    /// <summary>一个看板最近一次采集的结果快照。</summary>
    public sealed record CollectorSnapshot(
        string? DataJson,
        string? Error,
        DateTimeOffset? FetchedAt,
        DateTimeOffset? NextRunAt,
        IReadOnlyList<string> LogTail);

    /// <summary>脚本运行结果的 HTTP 包装（JS 侧直接访问 .ok/.status/.body）。</summary>
    public sealed record HttpResult(bool Ok, int Status, string StatusText, string Body);

    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private const int MaxDataJsonBytes = 2 * 1024 * 1024;
    private const int LogTailLimit = 50;
    private const int LogLineLimit = 500;
    private const int DefaultTimeoutMs = 10000;
    private const int MaxTimeoutMs = 30000;

    /// <summary>单次脚本运行的沙盒限额：内存 64MB、墙钟 30 秒（语句数另见 MaxStatements）。</summary>
    private const int ScriptMemoryBytes = 64 * 1024 * 1024;

    private const int ScriptTimeoutSeconds = 30;

    private static readonly HttpClient SharedHttp = CreateClient();

    private readonly TodoService _todos;
    private readonly object _gate = new();
    private readonly Dictionary<string, BoardRun> _runs = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>
    /// 采集脚本里 mcp(tool, args) 的落地实现：由 McpService 注入，进程内直调 MCP 工具，
    /// 脚本自己永远拿不到令牌。（不注入时脚本调 mcp() 会得到明确的错误。）
    /// </summary>
    private Func<string, string?, string?>? _mcpBridge;

    /// <summary>
    /// 看板界面的状态（区间、勾选…）读取器。App 端接 BoardUiStateStore，
    /// 脚本里通过 uiState 拿到用户勾了哪些模型，只给勾上的取价。
    /// </summary>
    private Func<string, string?>? _uiStateProvider;

    /// <summary>某看板采集结果变化（成功或失败都会触发）。事件在后台线程抛出。</summary>
    public event EventHandler<string>? DataChanged;

    public CollectorHost(TodoService todos)
    {
        _todos = todos;
    }

    private static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        // 看板的取价接口在 CDN 后面（a6api → apisnode 两个边缘 IP）。机器休眠、换网络、
        // 边缘换证书之后，连接池里那条老 TLS 连接会一直握手失败（The SSL connection could
        // not be established），所以别让它抱太久：到点就换新连接。
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    {
        // 单进程共享：频繁轮询别把连接池/TLS 握手打爆（同 WeatherService 的口径）。
        // 单次调用真正的时间上限由调用方传的 timeout_ms 经 CancellationToken 控制。
        Timeout = TimeSpan.FromSeconds(35),
    };

    // ---------------- 对外 API ----------------

    /// <summary>注入 mcp() 桥（McpService 构造尾部调用；不注入时脚本调 mcp() 会报错）。</summary>
    public void AttachMcpBridge(Func<string, string?, string?> bridge) => _mcpBridge = bridge;

    /// <summary>注入看板界面状态读取器（boardId → JSON），脚本里通过 uiState 用。</summary>
    public void AttachUiStateProvider(Func<string, string?> provider) => _uiStateProvider = provider;

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_cts.Token));
        }
    }

    public void Stop()
    {
        Task? loop;
        lock (_gate)
        {
            if (_loop is null)
            {
                return;
            }

            _cts?.Cancel();
            loop = _loop;
            _loop = null;
        }

        try
        {
            loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // 退出阶段忽略
        }
    }

    public void Dispose() => Stop();

    /// <summary>看板列表/脚本内容变了（MCP 创建或更新自定义看板后调用），立即重新同步。</summary>
    public void NotifyBoardsChanged() => Resync();

    /// <summary>取看板最近一次采集快照；从未跑过/没有采集器返回 null。</summary>
    public CollectorSnapshot? GetSnapshot(string boardId)
    {
        lock (_gate)
        {
            return _runs.TryGetValue(boardId, out var run) ? run.Snapshot : null;
        }
    }

    /// <summary>让采集器立即跑一次并等它跑完（MCP 的 run_collector 工具用）。返回是否真的跑了。</summary>
    public async Task<bool> RunNowAsync(string boardId)
    {
        BoardRun? run;
        lock (_gate)
        {
            if (!_runs.TryGetValue(boardId, out run) || run.Running)
            {
                return false;
            }

            run.Running = true;
        }

        try
        {
            await Task.Run(() => RunOne(run)).ConfigureAwait(false);
            return true;
        }
        finally
        {
            // RunOne 的 finally 只在正常进入时重置节奏；这里兜底防死锁
            lock (_gate)
            {
                if (run.Running)
                {
                    run.Running = false;
                    run.NextRun = DateTimeOffset.Now.AddSeconds(run.IntervalSeconds);
                }
            }
        }
    }

    /// <summary>AI 直接推数据到看板（不走网络采集）。dataJson 必须是合法 JSON。</summary>
    public bool PushData(string boardId, string dataJson)
    {
        // 先验证是合法 JSON，避免脏数据塞给看板
        try
        {
            using var _ = JsonDocument.Parse(dataJson);
        }
        catch (Exception)
        {
            return false;
        }

        if (dataJson.Length > MaxDataJsonBytes)
        {
            return false;
        }

        BoardRun? run;
        lock (_gate)
        {
            if (!_runs.TryGetValue(boardId, out run))
            {
                // 没有采集器的看板也允许推数据：临时建一条快照记录
                var board = _todos.GetBoard(boardId);
                if (board is null || board.Kind != BoardKind.Custom)
                {
                    return false;
                }

                run = new BoardRun(board, DateTimeOffset.Now) { IsPushOnly = true };
                // 推送条目没有脚本可跑：不参与调度循环，否则 1 秒内就会被跑一次、
                // 挂上一条「没有采集脚本」的误报错误
                run.NextRun = DateTimeOffset.MaxValue;
                run.Snapshot = new CollectorSnapshot(null, null, null, null, Array.Empty<string>());
                _runs[boardId] = run;
            }

            run.Snapshot = new CollectorSnapshot(dataJson, null, DateTimeOffset.Now, run.Snapshot?.NextRunAt, run.Snapshot?.LogTail ?? Array.Empty<string>());
        }

        DataChanged?.Invoke(this, boardId);
        return true;
    }

    // ---------------- 调度循环 ----------------

    private async Task LoopAsync(CancellationToken ct)
    {
        var sinceResync = TimeSpan.Zero;
        Resync();
        DispatchDue();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            sinceResync += TimeSpan.FromSeconds(1);
            if (sinceResync >= TimeSpan.FromSeconds(30))
            {
                sinceResync = TimeSpan.Zero;
                Resync();
            }

            DispatchDue();
        }
    }

    /// <summary>与看板列表重新同步：新采集器登记、脚本/间隔变更重置节奏、消失的移除。</summary>
    private void Resync()
    {
        List<Board> boards;
        try
        {
            boards = _todos.GetBoards().Where(b => b.Kind == BoardKind.Custom).ToList();
        }
        catch (Exception)
        {
            return; // 库暂时读不了（锁竞争等）：下个 30 秒再试
        }

        lock (_gate)
        {
            var alive = new HashSet<string>(StringComparer.Ordinal);

            foreach (var board in boards)
            {
                alive.Add(board.Id);

                var scripted = !string.IsNullOrWhiteSpace(board.CustomScript);
                var fingerprint = scripted ? ScriptFingerprint(board.CustomScript!) : string.Empty;
                var interval = scripted
                    ? Core.Services.TodoService.ClampCustomInterval(board.CustomIntervalSeconds)
                      ?? Core.Services.TodoService.DefaultCustomIntervalSeconds
                    : Core.Services.TodoService.DefaultCustomIntervalSeconds;

                if (_runs.TryGetValue(board.Id, out var existing))
                {
                    if (!scripted)
                    {
                        // 脚本被清空：把运行条目标记成"无脚本"，下面统一移除（AI 推数据的记录除外）
                        existing.CustomScript = null;
                        existing.Fingerprint = string.Empty;
                        continue;
                    }

                    if (existing.Fingerprint != fingerprint || existing.IntervalSeconds != interval)
                    {
                        existing.Fingerprint = fingerprint;
                        existing.CustomScript = board.CustomScript;
                        existing.IntervalSeconds = interval;
                        existing.NextRun = DateTimeOffset.Now;
                    }
                    continue;
                }

                if (!scripted)
                {
                    continue; // 没有采集器的看板不登记（push_custom_data 会自建）
                }

                _runs[board.Id] = new BoardRun(board, DateTimeOffset.Now)
                {
                    Fingerprint = fingerprint,
                    IntervalSeconds = interval,
                    NextRun = DateTimeOffset.Now,
                };
            }

            // 看板被删掉，或者脚本被清空（且不是 AI 推数据的记录）：移除运行条目
            var gone = _runs.Where(kv =>
                    !alive.Contains(kv.Key) ||
                    (string.IsNullOrWhiteSpace(kv.Value.CustomScript) && !kv.Value.IsPushOnly))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var id in gone)
            {
                _runs.Remove(id);
            }
        }
    }

    private void DispatchDue()
    {
        List<BoardRun> due;
        var now = DateTimeOffset.Now;
        lock (_gate)
        {
            due = _runs.Values.Where(r => !r.Running && r.NextRun <= now).ToList();
            foreach (var run in due)
            {
                run.Running = true;
            }
        }

        foreach (var run in due)
        {
            _ = Task.Run(() => RunOne(run));
        }
    }

    private void RunOne(BoardRun run)
    {
        try
        {
            if (run.IsPushOnly)
            {
                return; // AI 推数据的条目没有脚本，不执行
            }

            // 每次执行前重读看板，拿最新脚本（update_custom_board 改完立即生效）
            var board = _todos.GetBoard(run.BoardId);
            if (board is null || string.IsNullOrWhiteSpace(board.CustomScript))
            {
                run.Snapshot = run.Snapshot with { Error = "看板不存在或没有采集脚本。" };
                return;
            }

            var log = new List<string>();
            DateTimeOffset now = DateTimeOffset.Now;
            string dataJson;
            try
            {
                dataJson = ExecuteScript(board, run.Snapshot?.DataJson, log);
                if (dataJson.Length > MaxDataJsonBytes)
                {
                    throw new InvalidOperationException("采集结果超过 2MB。");
                }
            }
            catch (Exception ex)
            {
                var detail = DescribeError(ex);
                var message = detail.Length > LogLineLimit * 2 ? detail[..(LogLineLimit * 2)] : detail;
                // 保留上一次的好数据，让看板继续有东西可看，错误挂在旁边
                run.Snapshot = new CollectorSnapshot(run.Snapshot?.DataJson, message, now, null, log);
                DataChanged?.Invoke(this, run.BoardId);
                return;
            }

            run.Snapshot = new CollectorSnapshot(dataJson, null, now, null, log);
            DataChanged?.Invoke(this, run.BoardId);
        }
        catch (Exception ex)
        {
            // 读库失败等意外异常：同样落到错误快照，别把调度循环炸了
            run.Snapshot = run.Snapshot with { Error = "采集失败：" + Truncate(ex.Message, LogLineLimit * 2) };
        }
        finally
        {
            var next = DateTimeOffset.Now.AddSeconds(run.IntervalSeconds);
            lock (_gate)
            {
                run.Running = false;
                run.NextRun = next;
                if (run.Snapshot is not null)
                {
                    run.Snapshot = run.Snapshot with { NextRunAt = next };
                }
            }
        }
    }

    private static string ScriptFingerprint(string script) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script)));

    // ---------------- 脚本执行 ----------------

    /// <summary>注入给脚本的 JS 前导：把 C# 的原始参数接口包成好用的 http()/http_get()/http_post()。</summary>
    private const string ScriptPreamble = """
        var __http_req = __httpReq;
        function http(url, options) {
            options = options || {};
            var headers = options.headers ? JSON.stringify(options.headers) : "{}";
            return __http_req(String(options.method || "GET"), String(url), options.body == null ? null : String(options.body),
                options.timeout_ms || 10000, headers);
        }
        function http_get(url, timeout_ms) {
            return __http_req("GET", String(url), null, timeout_ms || 10000, "{}");
        }
        function http_post(url, body, timeout_ms) {
            return __http_req("POST", String(url), body == null ? null : String(body), timeout_ms || 10000, "{}");
        }
        function mcp(name, args) {
            var text = __mcpCall(String(name), args == null ? null : JSON.stringify(args));
            if (text == null || text === "") { return null; }
            // 工具报错时不抛异常，返回 { __mcpError: "错误：…" } 让脚本自己判断
            return JSON.parse(text);
        }
        var prior = null;
        if (__priorJson) {
            try { prior = JSON.parse(__priorJson); } catch (e) { prior = null; }
        }
        var uiState = null;
        if (__uiStateJson) {
            try { uiState = JSON.parse(__uiStateJson); } catch (e) { uiState = null; }
        }
        """;

    private string ExecuteScript(Board board, string? priorDataJson, List<string> log)
    {
        using var engine = new Engine(options =>
        {
            options.LimitMemory(ScriptMemoryBytes);
            options.TimeoutInterval(TimeSpan.FromSeconds(ScriptTimeoutSeconds));
            options.MaxStatements(200_000);
        });

        engine.SetValue("__priorJson", priorDataJson);
        engine.SetValue("__uiStateJson", _uiStateProvider?.Invoke(board.Id));
        engine.SetValue("console", new ConsoleShim(log));
        engine.SetValue("__httpReq", new Func<string, string, string?, long, string, HttpResult>(
            (method, url, body, timeoutMs, headersJson) => HttpCall(method, url, body, timeoutMs, headersJson, log)));
        engine.SetValue("__mcpCall", new Func<string, string?, string?>((name, argsJson) =>
        {
            var bridge = _mcpBridge;
            if (bridge is null)
            {
                throw new InvalidOperationException("mcp() 不可用：采集器没有连到 MCP 服务。");
            }

            return bridge(name, argsJson);
        }));

        engine.Execute(ScriptPreamble);
        engine.Execute(board.CustomScript!);

        var result = engine.Evaluate("typeof collect === 'function' ? JSON.stringify(collect()) : undefined");
        if (result.IsUndefined() || result.IsNull())
        {
            throw new InvalidOperationException("脚本必须定义 collect() 并返回数据。");
        }

        return result.AsString();
    }

    private static HttpResult HttpCall(string method, string url, string? body, long timeoutMs, string headersJson, List<string> log)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("只支持 http/https 地址：" + Truncate(url, 200));
        }

        var verb = new HttpMethod(ValidateMethod(method));

        // 每次尝试都要一条新请求（HttpRequestMessage 不能重发）
        HttpRequestMessage CreateRequest()
        {
            var request = new HttpRequestMessage(verb, uri);
            if (!string.IsNullOrEmpty(headersJson))
            {
                try
                {
                    using var document = JsonDocument.Parse(headersJson);
                    foreach (var header in document.RootElement.EnumerateObject())
                    {
                        if (header.Value.ValueKind == JsonValueKind.String && !IsForbiddenHeader(header.Name))
                        {
                            request.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString());
                        }
                    }
                }
                catch (Exception)
                {
                    log.Add("headers 不是合法 JSON，已忽略");
                }
            }

            if (!string.IsNullOrEmpty(body))
            {
                request.Content = new StringContent(body, Encoding.UTF8);
            }

            return request;
        }

        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 500, MaxTimeoutMs));
        using var cts = new CancellationTokenSource(timeout);

        // 网络层失败一律折成 status=0 的结果，不往脚本里抛异常：
        // Jint 的 JS try/catch 接不住 .NET 异常，一抛就整段脚本中止，
        // 看板只剩一行错误、其它模型的数据也一起没了。
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var attemptStartedAt = Environment.TickCount64;
            try
            {
                using var request = CreateRequest();
                using var response = SharedHttp.SendAsync(request, cts.Token).GetAwaiter().GetResult();
                var text = response.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
                if (text.Length > MaxResponseBytes)
                {
                    text = text[..MaxResponseBytes];
                    log.Add("响应超过 2MB，已截断");
                }

                return new HttpResult(
                    response.IsSuccessStatusCode,
                    (int)response.StatusCode,
                    response.ReasonPhrase ?? string.Empty,
                    text);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                log.Add("请求超时：" + Truncate(url, 120));
                return new HttpResult(false, 0, "请求超时（" + (long)timeout.TotalMilliseconds + " ms）", string.Empty);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                var detail = DescribeError(ex);

                // 只在「几毫秒就失败」时才重试：老 TLS 连接被边缘关掉属于这一类；
                // 真超时/慢网络就不要再等一轮了。
                var fastFail = Environment.TickCount64 - attemptStartedAt < Math.Max(1000, (long)timeout.TotalMilliseconds / 2);
                if (attempt == 1 && fastFail)
                {
                    log.Add("连接失败，换新连接重试：" + Truncate(detail, 200));
                    Thread.Sleep(200);
                    continue;
                }

                log.Add("请求失败：" + Truncate(detail, 300));
                return new HttpResult(false, 0, Truncate(detail, 300), string.Empty);
            }
        }

        return new HttpResult(false, 0, "请求失败", string.Empty);
    }

    /// <summary>
    /// 异常链压成一行：SSL 这类错误的真正原因全在 inner exception 里
    /// （外层只有一句 "The SSL connection could not be established"）。
    /// </summary>
    private static string DescribeError(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var text = e.Message.Trim();
            if (text.Length > 0 && !parts.Contains(text))
            {
                parts.Add(text);
            }
        }

        return string.Join(" ← ", parts);
    }

    private static string ValidateMethod(string method)
    {
        var clean = method.Trim().ToUpperInvariant();
        return clean is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD"
            ? clean
            : throw new InvalidOperationException("不支持的 HTTP 方法：" + Truncate(method, 40));
    }

    private static bool IsForbiddenHeader(string name) =>
        name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Connection", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string value, int limit) =>
        value.Length <= limit ? value : value[..limit] + "…";

    /// <summary>暴露给脚本的 console 对象，日志留尾给 get_collector_status / 看板诊断。</summary>
    private sealed class ConsoleShim
    {
        private readonly List<string> _log;

        public ConsoleShim(List<string> log)
        {
            _log = log;
        }

        public void log(object? value) => Append(value);

        public void warn(object? value) => Append("[warn] " + value);

        public void error(object? value) => Append("[error] " + value);

        private void Append(object? value)
        {
            lock (_log)
            {
                if (_log.Count >= LogTailLimit)
                {
                    return;
                }

                var text = value?.ToString() ?? "undefined";
                _log.Add(Truncate(text, LogLineLimit));
            }
        }
    }

    /// <summary>一个待采集看板的运行时状态（调度用，不对外）。</summary>
    private sealed class BoardRun
    {
        public BoardRun(Board board, DateTimeOffset now)
        {
            BoardId = board.Id;
            CustomScript = board.CustomScript;
            Snapshot = new CollectorSnapshot(null, null, null, now, Array.Empty<string>());
        }

        public string BoardId { get; }

        public string? CustomScript { get; set; }

        /// <summary>这条记录是 push_custom_data 自建的（看板本身没有采集脚本）。</summary>
        public bool IsPushOnly { get; init; }

        public string Fingerprint { get; set; } = string.Empty;

        public int IntervalSeconds { get; set; } = Core.Services.TodoService.DefaultCustomIntervalSeconds;

        public DateTimeOffset NextRun { get; set; }

        public bool Running { get; set; }

        public CollectorSnapshot? Snapshot { get; set; }
    }
}
