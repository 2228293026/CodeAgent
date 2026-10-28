using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeAgent.Mcp;

/// <summary>MCP 服务端声明的一个工具。</summary>
public sealed record McpToolInfo(string Name, string Description, JsonObject InputSchema);

/// <summary>MCP 的 JSON-RPC 2.0 信封与结果解析：只做纯文本转换，不碰进程。</summary>
public static class McpMessages
{
    public const string JsonRpcVersion = "2.0";
    public const string ProtocolVersion = "2025-06-18";
    public const string ToolPrefix = "mcp__";

    /// <summary>构造一条请求。没有参数时**不带** params 字段：有些严格的服务端会拒收 null。</summary>
    public static string Request(int id, string method, JsonObject? parameters)
    {
        var message = new JsonObject { ["jsonrpc"] = JsonRpcVersion, ["id"] = id, ["method"] = method };
        if (parameters is not null)
            message["params"] = parameters;
        return message.ToJsonString();
    }

    /// <summary>解析一行响应。服务端往 stdout 混入日志是常见现象，所以非法 JSON 只返回 false、不抛异常。</summary>
    public static bool TryParse(string? line, out JsonObject message)
    {
        message = null!;
        if (string.IsNullOrWhiteSpace(line))
            return false;
        try
        {
            if (JsonNode.Parse(line) is not JsonObject parsed)
                return false;
            message = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>构造一条通知（有 method、无 id），用于 initialized 之类的单向下行消息。</summary>
    public static string Notification(string method, JsonObject? parameters)
    {
        var message = new JsonObject { ["jsonrpc"] = JsonRpcVersion, ["method"] = method };
        if (parameters is not null)
            message["params"] = parameters;
        return message.ToJsonString();
    }

    /// <summary>从 tools/list 的 result 里取出工具清单；缺字段、类型不对的条目直接跳过，不让一个坏掉的工具拖垮整张表。</summary>
    public static IReadOnlyList<McpToolInfo> ParseTools(JsonObject? result)
    {
        var tools = new List<McpToolInfo>();
        if (result?["tools"] is not JsonArray array)
            return tools;
        foreach (var node in array)
        {
            if (node is not JsonObject tool)
                continue;
            if (!TryGetString(tool["name"], out var name) || string.IsNullOrWhiteSpace(name))
                continue;
            TryGetString(tool["description"], out var description);
            tools.Add(new McpToolInfo(name, description ?? string.Empty,
                tool["inputSchema"] as JsonObject ?? new JsonObject { ["type"] = "object" }));
        }
        return tools;
    }

    private static bool TryGetString(JsonNode? node, out string? value)
    {
        value = null;
        if (node is not JsonValue json || !json.TryGetValue(out string? text))
            return false;
        value = text;
        return true;
    }

    /// <summary>把 tools/call 的 result 摊平成文本：多个 text 块用换行拼接；非文本块（图片等）忽略，但绝不会丢掉整个响应。</summary>
    public static string ExtractText(JsonObject? result)
    {
        if (result?["content"] is not JsonArray content)
            return result?.ToJsonString() ?? string.Empty;
        var parts = new List<string>();
        foreach (var node in content)
        {
            if (node is not JsonObject block)
                continue;
            if (!TryGetString(block["type"], out var type) || type != "text")
                continue;
            if (TryGetString(block["text"], out var text) && text is not null)
                parts.Add(text);
        }
        return parts.Count > 0 ? string.Join("\n", parts) : "（该工具没有返回文本内容）";
    }

    /// <summary>把服务端工具名变成全局唯一的工具名（mcp__server__tool）。</summary>
    public static string NamespaceTool(string server, string tool) =>
        $"{ToolPrefix}{server}__{tool}";
}
