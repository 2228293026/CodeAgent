using System;
using System.IO;
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

public class OpenAiProviderStreamTests
{
    /// <summary>返回固定 SSE 响应流的假 HttpClient 处理器。</summary>
    private sealed class SseHandler : HttpMessageHandler
    {
        public string Body { get; init; } = "";
        public string? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Body, Encoding.UTF8, "text/event-stream"),
            };
            return Task.FromResult(resp);
        }
    }

    private static OpenAiProvider MakeProvider(SseHandler handler) =>
        new(new ProviderOptions { ApiKey = "test-key" }, new HttpClient(handler));

    [Fact]
    public async Task ChatStreamAsync_AccumulatesTextAndUsage()
    {
        var handler = new SseHandler
        {
            Body = """
                data: {"choices":[{"delta":{"role":"assistant","content":"你好"}}]}

                data: {"choices":[{"delta":{"content":"，世界"}}]}

                data: {"usage":{"prompt_tokens":12,"completion_tokens":5}}

                data: [DONE]
                """,
        };
        var provider = MakeProvider(handler);
        var text = new StringBuilder();
        string? reasoning = null;

        var resp = await provider.ChatStreamAsync(
            [new ProviderMessage { Role = MessageRole.User, Content = "hi" }],
            [], "off", t => text.Append(t), r => reasoning = r, null, CancellationToken.None);

        Assert.Equal("你好，世界", text.ToString());
        Assert.Equal("你好，世界", resp.Text);
        Assert.Equal(12, resp.InputTokens);
        Assert.Equal(5, resp.OutputTokens);
        Assert.Null(reasoning); // 无 reasoning 增量
    }

    [Fact]
    public async Task ChatStreamAsync_ReasoningContent_IsReported()
    {
        var handler = new SseHandler
        {
            Body = """
                data: {"choices":[{"delta":{"reasoning_content":"先想想"}}]}

                data: {"choices":[{"delta":{"content":"答案"}}]}

                data: [DONE]
                """,
        };
        var provider = MakeProvider(handler);
        var reasoning = new StringBuilder();

        var resp = await provider.ChatStreamAsync(
            [new ProviderMessage { Role = MessageRole.User, Content = "q" }],
            [], "off", null, r => reasoning.Append(r), null, CancellationToken.None);

        Assert.Equal("先想想", reasoning.ToString());
        Assert.Equal("答案", resp.Text);
    }

    [Fact]
    public async Task ChatStreamAsync_ToolCallFragments_AreAccumulated()
    {
        // 工具调用参数按 index 分片到达，应累积成完整 JSON 并按 index 排序
        var handler = new SseHandler
        {
            Body = """
                data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"read_file","arguments":"{\"path\":"}}]}}]}

                data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"a.txt\"}"}}]}}]}

                data: [DONE]
                """,
        };
        var provider = MakeProvider(handler);
        var frags = new StringBuilder();

        var resp = await provider.ChatStreamAsync(
            [new ProviderMessage { Role = MessageRole.User, Content = "read" }],
            [], "off", null, null, f => frags.Append(f), CancellationToken.None);

        Assert.Single(resp.ToolCalls);
        var tc = resp.ToolCalls[0];
        Assert.Equal("call_1", tc.Id);
        Assert.Equal("read_file", tc.Name);
        Assert.Equal("""{"path":"a.txt"}""", tc.ArgumentsJson);
        Assert.Equal("""{"path":"a.txt"}""", frags.ToString()); // 所有分片都经 onToolFragment 回调
    }

    [Fact]
    public async Task ChatStreamAsync_ToolCallsOutOfOrder_AreSortedByIndex()
    {
        // 同一轮多个工具调用：index 1 先到、index 0 后到，最终按 index 排序
        var handler = new SseHandler
        {
            Body = """
                data: {"choices":[{"delta":{"tool_calls":[{"index":1,"id":"b","function":{"name":"glob","arguments":"{\"pattern\":\"*\"}"}}]}}]}

                data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"a","function":{"name":"read_file","arguments":"{\"path\":\"x\"}"}}]}}]}

                data: [DONE]
                """,
        };
        var provider = MakeProvider(handler);

        var resp = await provider.ChatStreamAsync(
            [new ProviderMessage { Role = MessageRole.User, Content = "both" }],
            [], "off", null, null, null, CancellationToken.None);

        Assert.Equal(2, resp.ToolCalls.Count);
        Assert.Equal("a", resp.ToolCalls[0].Id); // index 0 在前
        Assert.Equal("read_file", resp.ToolCalls[0].Name);
        Assert.Equal("b", resp.ToolCalls[1].Id);
        Assert.Equal("glob", resp.ToolCalls[1].Name);
    }

    [Fact]
    public async Task ChatStreamAsync_SendsStreamFlagsInRequest()
    {
        var handler = new SseHandler { Body = "data: [DONE]\n" };
        var provider = MakeProvider(handler);

        await provider.ChatStreamAsync(
            [new ProviderMessage { Role = MessageRole.User, Content = "hi" }],
            [], "off", null, null, null, CancellationToken.None);

        var payload = JsonNode.Parse(handler.LastRequest!)!;
        Assert.True(payload["stream"]!.GetValue<bool>());
        Assert.NotNull(payload["stream_options"]); // include_usage
    }

    [Fact]
    public async Task ChatStreamAsync_NonStringDeltaFields_SkippedNotThrown()
    {
        // 回归：不合规代理可能把 reasoning/content 发成非字符串（数字/对象）；
        // 曾用 GetValue<string> 直接抛异常中断整个流，现在跳过该增量
        var handler = new SseHandler
        {
            Body = """
                data: {"choices":[{"delta":{"reasoning_content":123,"content":"好的"}}]}

                data: {"choices":[{"delta":{"content":{"type":"text","text":"x"}}}]}

                data: [DONE]
                """,
        };
        var provider = MakeProvider(handler);

        var resp = await provider.ChatStreamAsync(
            [new ProviderMessage { Role = MessageRole.User, Content = "hi" }], [], "off",
            null, null, null, CancellationToken.None);

        Assert.Equal("好的", resp.Text); // 非字符串增量被跳过，合法增量照常累积
    }

    [Fact]
    public async Task ChatStreamAsync_MidStreamErrorEvent_ThrowsWithMessage()
    {
        // 回归：流中途的 data:{"error":...} 曾被静默跳过，用户只看到空回复
        var http = new HttpClient(new SseHandler
        {
            Body = "data: {\"error\":{\"message\":\"rate limited\",\"type\":\"rate_limit_error\"}}\n\ndata: [DONE]\n\n",
        });
        var provider = new OpenAiProvider(new ProviderOptions { ApiKey = "k" }, http);

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.ChatStreamAsync(
                [new ProviderMessage { Role = MessageRole.User, Content = "hi" }],
                [], "off", null, null, null, CancellationToken.None));
        Assert.Contains("rate limited", ex.Message);
    }

    [Fact]
    public async Task ChatStreamAsync_RateLimitError_IsRetryable()
    {
        // 限流类型的流中错误应标记 Retryable（Agent 在未输出文本时会自动退避重试）
        var http = new HttpClient(new SseHandler
        {
            Body = "data: {\"error\":{\"message\":\"slow down\",\"code\":429}}\n\ndata: [DONE]\n\n",
        });
        var provider = new OpenAiProvider(new ProviderOptions { ApiKey = "k" }, http);

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.ChatStreamAsync(
                [new ProviderMessage { Role = MessageRole.User, Content = "hi" }],
                [], "off", null, null, null, CancellationToken.None));
        Assert.True(ex.Retryable);
        Assert.Equal(429, ex.StatusCode);
    }
}
