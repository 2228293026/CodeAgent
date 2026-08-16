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

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""",
                    Encoding.UTF8, "application/json"),
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
}
