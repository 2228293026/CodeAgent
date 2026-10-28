using System.Text.Json.Nodes;
using CodeAgent.Mcp;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>MCP 协议层：只验证信封形状与结果解析，不启动任何进程。
/// 这类代码一旦把 params 写成 null，或把服务端日志当成响应，排查成本极高。</summary>
public class McpMessagesTests
{
    [Fact]
    public void RequestOmitsParamsWhenNull()
    {
        var json = JsonNode.Parse(McpMessages.Request(7, "tools/list", null))!.AsObject();

        Assert.Equal("2.0", (string?)json["jsonrpc"]);
        Assert.Equal(7, (int)json["id"]!);
        Assert.Equal("tools/list", (string?)json["method"]);
        Assert.False(json.ContainsKey("params"));
    }

    [Fact]
    public void RequestKeepsParamsWhenGiven()
    {
        var message = McpMessages.Request(3, "tools/call", new JsonObject { ["name"] = "search" });
        var json = JsonNode.Parse(message)!.AsObject();

        Assert.Equal("search", (string?)json["params"]!["name"]);
    }

    [Fact]
    public void NotificationCarriesNoId()
    {
        var json = JsonNode.Parse(McpMessages.Notification("notifications/initialized", null))!.AsObject();

        Assert.Equal("2.0", (string?)json["jsonrpc"]);
        Assert.Equal("notifications/initialized", (string?)json["method"]);
        Assert.False(json.ContainsKey("params"));
    }

    [Fact]
    public void TryParseRejectsNoiseButKeepsObjects()
    {
        Assert.False(McpMessages.TryParse(null, out _));
        Assert.False(McpMessages.TryParse("  ", out _));
        Assert.False(McpMessages.TryParse("starting server on stdio", out _));
        Assert.False(McpMessages.TryParse("[1,2,3]", out _));

        Assert.True(McpMessages.TryParse("{\"jsonrpc\":\"2.0\",\"id\":1}", out var parsed));
        Assert.Equal(1, (int)parsed["id"]!);
    }

    [Fact]
    public void ParseToolsSkipsEntriesWithoutName()
    {
        var result = new JsonObject
        {
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "search",
                    ["description"] = "d",
                    ["inputSchema"] = new JsonObject { ["type"] = "object" },
                },
                new JsonObject { ["description"] = "no name" },
                new JsonObject { ["name"] = "bare" },
            },
        };

        var tools = McpMessages.ParseTools(result);

        Assert.Equal(2, tools.Count);
        Assert.Equal("search", tools[0].Name);
        Assert.Equal("d", tools[0].Description);
        Assert.Equal("bare", tools[1].Name);
        Assert.Equal(string.Empty, tools[1].Description);
        Assert.Equal("object", (string?)tools[1].InputSchema["type"]);
        Assert.Empty(McpMessages.ParseTools(null));
    }

    [Fact]
    public void ExtractTextJoinsTextBlocksAndIgnoresOthers()
    {
        var result = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = "a" },
                new JsonObject { ["type"] = "image", ["data"] = "x" },
                new JsonObject { ["type"] = "text", ["text"] = "b" },
            },
        };

        Assert.Equal("a\nb", McpMessages.ExtractText(result));
        Assert.Contains("没有返回文本内容", McpMessages.ExtractText(new JsonObject { ["content"] = new JsonArray() }));
    }

    [Fact]
    public void NamespaceToolUsesPrefixAndDoubleUnderscore()
    {
        Assert.Equal("mcp__fs__read_file", McpMessages.NamespaceTool("fs", "read_file"));
    }
}
