using System.Text.Json.Nodes;

namespace StickyTodo.Mcp;

/// <summary>MCP tools/call 的返回内容块。</summary>
public sealed record McpContentBlock(string Type, string Text, byte[]? ImagePng, string MimeType)
{
    public static McpContentBlock CreateText(string text) => new("text", text, null, string.Empty);

    public static McpContentBlock CreateImage(byte[] png, string mimeType = "image/png") =>
        new("image", string.Empty, png, mimeType);

    public JsonObject ToJson()
    {
        var node = new JsonObject { ["type"] = Type };
        if (Type == "text")
        {
            node["text"] = Text;
        }
        else if (ImagePng is not null)
        {
            node["data"] = Convert.ToBase64String(ImagePng);
            node["mimeType"] = MimeType;
        }

        return node;
    }
}

/// <summary>MCP 工具的执行结果。</summary>
public sealed record McpToolResult(IReadOnlyList<McpContentBlock> Content, bool IsError)
{
    public static McpToolResult Text(string text) => new([McpContentBlock.CreateText(text)], false);

    /// <summary>失败结果：文本里带「错误：」前缀，isError 标记为 true。</summary>
    public static McpToolResult Error(string message) =>
        new([McpContentBlock.CreateText("错误：" + message)], true);

    public static McpToolResult Image(byte[] png, string caption) => new(
        [McpContentBlock.CreateText(caption), McpContentBlock.CreateImage(png)],
        false);

    public JsonObject ToJson()
    {
        var content = new JsonArray();
        foreach (var block in Content)
        {
            content.Add(block.ToJson());
        }

        var result = new JsonObject { ["content"] = content };
        if (IsError)
        {
            result["isError"] = true;
        }

        return result;
    }
}

/// <summary>JSON-RPC 层的参数错误（tools/call 里没有必须的参数等）。</summary>
public sealed class McpInvalidParamsException(string message) : Exception(message);

/// <summary>JSON-RPC 层的未知方法。</summary>
public sealed class McpMethodNotFoundException(string method) : Exception($"Method not found: {method}");
