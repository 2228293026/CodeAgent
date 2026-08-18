using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CodeAgent;
using CodeAgent.Providers;
using Xunit;

namespace CodeAgent.Tests;

public class AnthropicProviderStreamTests
{
    /// <summary>返回固定 SSE 事件流的假 HttpClient 处理器。</summary>
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

    private static AnthropicProvider MakeProvider(SseHandler handler) =>
        new(new ProviderOptions { ApiKey = "test-key" }, new HttpClient(handler));

    [Fact]
    public async Task ChatStreamAsync_TextDeltas_AreAccumulated()
    {
        var handler = new SseHandler
        {
            Body = """
                event: message_start
                data: {"type":"message_start","message":{"usage":{"input_tokens":10,"output_tokens":1,"input_tokens_details":{"cache_read_input_tokens":3}}}}

                event: content_block_start
                data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"你好"}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"，世界"}}

                event: message_delta
                data: {"type":"message_delta","usage":{"output_tokens":7}}

                event: message_stop
                data: {"type":"message_stop"}
                """,
        };
        var provider = MakeProvider(handler);
        var text = new StringBuilder();

        var resp = await provider.ChatStreamAsync(
            [new ProviderMessage { Role = MessageRole.User, Content = "hi" }],
            [], "off", t => text.Append(t), null, null, CancellationToken.None);

        Assert.Equal("你好，世界", text.ToString());
        Assert.Equal("你好，世界", resp.Text);
        Assert.Equal(10, resp.InputTokens);
        Assert.Equal(7, resp.OutputTokens);
        Assert.Equal(3, resp.CachedTokens); // cache_read_input_tokens
    }

    [Fact]
    public async Task ChatStreamAsync_ThinkingDelta_IsReported()
    {
        var handler = new SseHandler
        {
            Body = """
                event: content_block_start
                data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"推理中"}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"…继续"}}

                event: content_block_start
                data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"最终答案"}}

                event: message_stop
                data: {"type":"message_stop"}
                """,
        };
        var provider = MakeProvider(handler);
        var reasoning = new StringBuilder();

        var resp = await provider.ChatStreamAsync(
            [new ProviderMessage { Role = MessageRole.User, Content = "q" }],
            [], "off", null, r => reasoning.Append(r), null, CancellationToken.None);

        Assert.Equal("推理中…继续", reasoning.ToString());
        Assert.Equal("最终答案", resp.Text);
    }

    [Fact]
    public async Task ChatStreamAsync_ToolUseInput_IsAccumulated()
    {
        var handler = new SseHandler
        {
            Body = """
                event: content_block_start
                data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_1","name":"read_file","input":{}}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"path\":"}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"\"a.txt\"}"}}

                event: message_stop
                data: {"type":"message_stop"}
                """,
        };
        var provider = MakeProvider(handler);
        var frags = new StringBuilder();

        var resp = await provider.ChatStreamAsync(
            [new ProviderMessage { Role = MessageRole.User, Content = "read" }],
            [], "off", null, null, f => frags.Append(f), CancellationToken.None);

        Assert.Single(resp.ToolCalls);
        var tc = resp.ToolCalls[0];
        Assert.Equal("toolu_1", tc.Id);
        Assert.Equal("read_file", tc.Name);
        Assert.Equal("""{"path":"a.txt"}""", tc.ArgumentsJson);
        Assert.Equal("""{"path":"a.txt"}""", frags.ToString());
    }

    [Fact]
    public async Task ChatStreamAsync_ErrorEvent_ThrowsProviderException()
    {
        var handler = new SseHandler
        {
            Body = """
                event: error
                data: {"type":"error","error":{"type":"overloaded_error","message":"服务过载"}}
                """,
        };
        var provider = MakeProvider(handler);

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.ChatStreamAsync(
                [new ProviderMessage { Role = MessageRole.User, Content = "hi" }],
                [], "off", null, null, null, CancellationToken.None));
        Assert.Contains("服务过载", ex.Message);
    }

    [Fact]
    public async Task ChatStreamAsync_PayloadIncludesMessagesAndSystem()
    {
        // 回归：手工拼 payload 曾在重构时丢掉 messages 字段（流式测试只断言响应解析，
        // 请求体缺字段照样通过）。锁定请求体的必备字段。
        var handler = new SseHandler { Body = "data: [DONE]\n\n" };
        var provider = new AnthropicProvider(
            new ProviderOptions { ApiKey = "k", Model = "claude-sonnet-4-5" }, new HttpClient(handler));

        await provider.ChatStreamAsync(
            [new ProviderMessage { Role = MessageRole.System, Content = "sys" },
             new ProviderMessage { Role = MessageRole.User, Content = "hi" }],
            [], "off", null, null, null, CancellationToken.None);

        var body = JsonNode.Parse(handler.LastRequest ?? "");
        Assert.NotNull(body?["messages"]);
        Assert.Single(body!["messages"]!.AsArray()); // system 走顶层，不进 messages
        Assert.Equal("sys", body["system"]!.GetValue<string>());
        Assert.Equal(true, body["stream"]!.GetValue<bool>());
    }
}
