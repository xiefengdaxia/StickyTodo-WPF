using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StickyTodo.Mcp;

/// <summary>
/// MCP JSON-RPC 协议处理（无状态，框架无关）。
/// 只实现稳定核心：initialize / ping / tools\/list / tools\/call，
/// resources 与 prompts 能力未实现，但会返回空列表而不是报错，兼容各客户端的探测。
/// 消息在 HTTP 服务端先做鉴权与解析，这里只处理已解析的 JsonElement。
/// </summary>
public sealed class McpProtocolHandler
{
    /// <summary>支持的协议版本（初始化时客户端请求什么就回什么，超出集合才回最新）。</summary>
    public const string ProtocolVersion = "2025-06-18";

    public const string ServerName = "stickytodo";

    private static readonly string[] KnownVersions =
    [
        "2024-11-05",
        "2025-03-26",
        ProtocolVersion,
    ];

    private readonly McpToolRegistry _registry;
    private readonly string _serverVersion;

    /// <summary>响应信封序列化：中文不转义成 unicode（可读、省 token）。只用于输出。</summary>
    private static readonly JsonSerializerOptions JsonOutputOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public McpProtocolHandler(McpToolRegistry registry)
    {
        _registry = registry;
        _serverVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "1.0.0";
    }

    /// <summary>
    /// 处理一条已解析的 JSON-RPC 消息。
    /// 通知（无 id）返回 null → HTTP 层回 202；请求返回响应 JSON 字符串 → HTTP 层回 200。
    /// </summary>
    public async Task<string?> HandleAsync(JsonElement message, CancellationToken cancellation = default)
    {
        var hasId = TryGetId(message, out var id);
        var method = message.TryGetProperty("method", out var methodNode) && methodNode.ValueKind == JsonValueKind.String
            ? methodNode.GetString()
            : null;

        if (method is null)
        {
            // 没有方法的报文（客户端发来的响应之类）：按通知吞掉
            return null;
        }

        if (!hasId)
        {
            // notifications/initialized 等通知：不需要响应
            return null;
        }

        JsonNode result;
        try
        {
            result = await ExecuteMethodAsync(method, message, cancellation).ConfigureAwait(false);
        }
        catch (McpInvalidParamsException ex)
        {
            return BuildError(id, -32602, ex.Message);
        }
        catch (McpMethodNotFoundException ex)
        {
            return BuildError(id, -32601, ex.Message);
        }
        catch (Exception ex)
        {
            return BuildError(id, -32603, "内部错误：" + ex.Message);
        }

        return BuildResult(id, result);
    }

    private async Task<JsonNode> ExecuteMethodAsync(string method, JsonElement message, CancellationToken cancellation)
    {
        JsonElement Params() =>
            message.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;

        switch (method)
        {
            case "initialize":
            {
                var requested = Params().TryGetProperty("protocolVersion", out var pv) && pv.ValueKind == JsonValueKind.String
                    ? pv.GetString()
                    : null;
                var version = requested is not null && KnownVersions.Contains(requested) ? requested : ProtocolVersion;

                var capabilities = new JsonObject
                {
                    ["tools"] = new JsonObject { ["listChanged"] = false },
                };
                return new JsonObject
                {
                    ["protocolVersion"] = version,
                    ["capabilities"] = capabilities,
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = ServerName,
                        ["version"] = _serverVersion,
                    },
                    ["instructions"] = "StickyTodo 桌面便签/待办应用的 MCP 服务端。" +
                        "可以创建/关闭看板、读写待办与便签、控制番茄钟、改外观与护眼设置、" +
                        "查天气与 Token 用量、截屏、发通知；还能创建「自定义看板」：" +
                        "你写 HTML 定制 UI，写 collect() 脚本定时抓数据回填到看板。先调 tools/list 看全部工具。",
                };
            }

            case "ping":
                return new JsonObject();

            case "tools/list":
            {
                var tools = new JsonArray();
                foreach (var tool in _registry.ListTools())
                {
                    tools.Add(new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["inputSchema"] = JsonNode.Parse(tool.InputSchema.ToJsonString()),
                    });
                }

                return new JsonObject { ["tools"] = tools };
            }

            case "tools/call":
            {
                var @params = Params();
                if (@params.ValueKind != JsonValueKind.Object)
                {
                    throw new McpInvalidParamsException("tools/call 需要 params.name。");
                }

                var name = @params.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String
                    ? nameNode.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new McpInvalidParamsException("tools/call 需要 params.name。");
                }

                var arguments = new JsonObject();
                if (@params.TryGetProperty("arguments", out var argsNode) && argsNode.ValueKind == JsonValueKind.Object)
                {
                    arguments = JsonObject.Parse(argsNode.GetRawText()) as JsonObject
                                ?? throw new McpInvalidParamsException("params.arguments 必须是对象。");
                }

                return (await _registry.ExecuteAsync(name, arguments, cancellation).ConfigureAwait(false)).ToJson();
            }

            // 未实现的能力探测：返回空集合，别让客户端直接报错
            case "resources/list":
                return new JsonObject { ["resources"] = new JsonArray() };

            case "resources/templates/list":
                return new JsonObject { ["resourceTemplates"] = new JsonArray() };

            case "prompts/list":
                return new JsonObject { ["prompts"] = new JsonArray() };

            default:
                throw new McpMethodNotFoundException(method);
        }
    }

    private static bool TryGetId(JsonElement message, out JsonElement id)
    {
        if (message.TryGetProperty("id", out var idNode) && idNode.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            id = idNode;
            return true;
        }

        id = default;
        return false;
    }

    private static string BuildResult(JsonElement id, JsonNode result)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = CloneId(id),
            ["result"] = result,
        };
        return response.ToJsonString(JsonOutputOptions);
    }

    private static string BuildError(JsonElement id, int code, string message)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = CloneId(id),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };
        return response.ToJsonString(JsonOutputOptions);
    }

    private static JsonNode? CloneId(JsonElement id) => JsonNode.Parse(id.GetRawText());
}
