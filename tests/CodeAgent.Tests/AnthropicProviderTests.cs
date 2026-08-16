using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent;
using CodeAgent.Providers;
using Xunit;

namespace CodeAgent.Tests;

public class AnthropicProviderTests
{
    /// <summary>捕获请求体并返回固定响应的假 HttpClient 处理器。</summary>
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? LastBody;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":1,"output_tokens":1}}""",
                    Encoding.UTF8, "application/json"),
            });
        }
    }

    private static AnthropicProvider MakeProvider(CaptureHandler handler) =>
        new(new ProviderOptions { ApiKey = "test-key" }, new HttpClient(handler));

    [Fact]
    public async Task ChatAsync_ToolResults_AreMergedIntoUserMessages()
    {
        var handler = new CaptureHandler();
        var provider = MakeProvider(handler);

        var messages = new[]
        {
            new ProviderMessage { Role = MessageRole.System, Content = "sys" },
            new ProviderMessage { Role = MessageRole.User, Content = "hi" },
            new ProviderMessage
            {
                Role = MessageRole.Assistant,
                ToolCalls = [new ToolCall { Id = "call_1", Name = "read_file", ArgumentsJson = """{"path":"a"}""" }],
            },
            new ProviderMessage { Role = MessageRole.Tool, ToolCallId = "call_1", ToolName = "read_file", Content = "result" },
        };

        await provider.ChatAsync(messages, [], "off", CancellationToken.None);

        var msgs = JsonNode.Parse(handler.LastBody!)?["messages"]?.AsArray();
        Assert.NotNull(msgs);
        // tool_result 应以 user 角色出现（Anthropic 要求）
        var last = msgs![^1];
        Assert.Equal("user", last["role"]!.GetValue<string>());
        var content = last["content"]!.AsArray();
        Assert.Equal("tool_result", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("call_1", content[0]!["tool_use_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task ChatAsync_ThinkingEnabled_ClampsBudgetToMaxTokens()
    {
        var handler = new CaptureHandler();
        // maxTokens 很小：high 预算 16384 应被收敛到 max_tokens - 1024 = 1024
        var provider = new AnthropicProvider(
            new ProviderOptions { ApiKey = "test-key", MaxTokens = 2048 }, new HttpClient(handler));

        var messages = new[]
        {
            new ProviderMessage { Role = MessageRole.System, Content = "sys" },
            new ProviderMessage { Role = MessageRole.User, Content = "hi" },
        };

        await provider.ChatAsync(messages, [], "high", CancellationToken.None);

        var body = JsonNode.Parse(handler.LastBody!);
        Assert.NotNull(body?["thinking"]);
        var budget = body!["thinking"]!["budget_tokens"]!.GetValue<int>();
        Assert.Equal(1024, budget); // max_tokens(2048) - 1024
        Assert.Null(body["temperature"]); // thinking 模式下省略 temperature
    }

    [Fact]
    public async Task ChatAsync_ThinkingOff_SendsTemperature()
    {
        var handler = new CaptureHandler();
        var provider = MakeProvider(handler);

        var messages = new[]
        {
            new ProviderMessage { Role = MessageRole.System, Content = "sys" },
            new ProviderMessage { Role = MessageRole.User, Content = "hi" },
        };

        await provider.ChatAsync(messages, [], "off", CancellationToken.None);

        var body = JsonNode.Parse(handler.LastBody!);
        Assert.NotNull(body?["temperature"]);
        Assert.Null(body!["thinking"]);
    }

    [Fact]
    public async Task ChatAsync_ConsecutiveSameRoleMessages_AreMerged()
    {
        // 回归：Anthropic 要求角色交替；连续同角色（如两条 user）应合并为一个消息的内容块
        var handler = new CaptureHandler();
        var provider = MakeProvider(handler);

        var messages = new[]
        {
            new ProviderMessage { Role = MessageRole.System, Content = "sys" },
            new ProviderMessage { Role = MessageRole.User, Content = "第一问" },
            new ProviderMessage { Role = MessageRole.User, Content = "第二问" }, // 连续 user
        };

        await provider.ChatAsync(messages, [], "off", CancellationToken.None);

        var msgs = JsonNode.Parse(handler.LastBody!)?["messages"]?.AsArray();
        Assert.NotNull(msgs);
        Assert.Equal(1, msgs!.Count); // 两条连续 user 已合并为 1 条消息
        var user = msgs[0];
        Assert.Equal("user", user["role"]!.GetValue<string>());
        var content = user["content"]!.AsArray();
        Assert.Equal(2, content.Count); // 两个文本块
        Assert.Equal("第一问", content[0]!["text"]!.GetValue<string>());
        Assert.Equal("第二问", content[1]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task ChatAsync_AssistantToolUse_IsBuiltAsContentBlock()
    {
        // assistant 消息的 tool_calls 应转成 tool_use 内容块（含 id/name/input）
        var handler = new CaptureHandler();
        var provider = MakeProvider(handler);

        var messages = new[]
        {
            new ProviderMessage { Role = MessageRole.System, Content = "sys" },
            new ProviderMessage { Role = MessageRole.User, Content = "hi" },
            new ProviderMessage
            {
                Role = MessageRole.Assistant,
                Content = "我先看看",
                ToolCalls = [new ToolCall { Id = "call_9", Name = "glob", ArgumentsJson = """{"pattern":"*.cs"}""" }],
            },
        };

        await provider.ChatAsync(messages, [], "off", CancellationToken.None);

        var msgs = JsonNode.Parse(handler.LastBody!)?["messages"]?.AsArray();
        Assert.NotNull(msgs);
        var asst = msgs![^1];
        Assert.Equal("assistant", asst["role"]!.GetValue<string>());
        var content = asst["content"]!.AsArray();
        Assert.Equal(2, content.Count); // 文本块 + tool_use 块
        Assert.Equal("text", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("tool_use", content[1]!["type"]!.GetValue<string>());
        Assert.Equal("call_9", content[1]!["id"]!.GetValue<string>());
        Assert.Equal("glob", content[1]!["name"]!.GetValue<string>());
        Assert.Equal("""{"pattern":"*.cs"}""", content[1]!["input"]!.ToJsonString());
    }
}
