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
}
