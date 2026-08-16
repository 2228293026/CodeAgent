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

public class OpenAiProviderTests
{
    /// <summary>捕获请求体并返回固定响应的假 HttpClient 处理器。</summary>
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? LastBody;

        /// <summary>覆写响应体（默认返回 chat 响应）。</summary>
        public string OverrideBody { get; set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            var body = OverrideBody.Length > 0
                ? OverrideBody
                : """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task ChatAsync_ToolCallAssistantWithoutText_SendsNullContent()
    {
        // 回归：assistant 带 tool_calls 且无文本时应传 content=null，
        // 部分 OpenAI 兼容 API 会拒绝空字符串 content。
        var handler = new CaptureHandler();
        var provider = new OpenAiProvider(
            new ProviderOptions { ApiKey = "test-key" }, new HttpClient(handler));

        var messages = new[]
        {
            new ProviderMessage { Role = MessageRole.System, Content = "sys" },
            new ProviderMessage { Role = MessageRole.User, Content = "hi" },
            new ProviderMessage
            {
                Role = MessageRole.Assistant,
                ToolCalls = [new ToolCall { Id = "call_1", Name = "read_file", ArgumentsJson = """{"path":"a.txt"}""" }],
            },
            new ProviderMessage { Role = MessageRole.Tool, ToolCallId = "call_1", ToolName = "read_file", Content = "result" },
        };

        await provider.ChatAsync(messages, [], "off", CancellationToken.None);

        var msgs = JsonNode.Parse(handler.LastBody!)?["messages"]?.AsArray();
        Assert.NotNull(msgs);
        var asst = msgs![2];
        Assert.Equal("assistant", asst["role"]!.GetValue<string>());
        Assert.Null(asst["content"]); // 应为 null 而非 ""
        Assert.NotNull(asst["tool_calls"]);
    }

    [Fact]
    public async Task ListModelsAsync_ParsesModelIds()
    {
        var handler = new CaptureHandler();
        // 覆写响应：模型列表 JSON
        handler.OverrideBody = """
            {"data":[{"id":"gpt-4o"},{"id":"gpt-4o-mini"},{"id":"o3"}]}
            """;
        var provider = new OpenAiProvider(
            new ProviderOptions { ApiKey = "test-key" }, new HttpClient(handler));

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["gpt-4o", "gpt-4o-mini", "o3"], models);
    }
}
