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
        var asst = msgs![2]!;
        Assert.Equal("assistant", asst["role"]!.GetValue<string>());
        Assert.Null(asst["content"]); // 应为 null 而非 ""
        Assert.NotNull(asst["tool_calls"]);
    }

    [Fact]
    public async Task ChatAsync_EmptyChoices_NotThrown()
    {
        // 回归：choices 为空数组的响应曾抛 ArgumentOutOfRangeException，
        // 现在应安全返回空响应并解析 usage
        var handler = new CaptureHandler
        {
            OverrideBody = """{"choices":[],"usage":{"prompt_tokens":3,"completion_tokens":1}}""",
        };
        var provider = new OpenAiProvider(
            new ProviderOptions { ApiKey = "test-key" }, new HttpClient(handler));

        var resp = await provider.ChatAsync(
            [new ProviderMessage { Role = MessageRole.User, Content = "hi" }],
            [], "off", CancellationToken.None);

        Assert.Null(resp.Text);
        Assert.Empty(resp.ToolCalls);
        Assert.Equal(3, resp.InputTokens);
        Assert.Equal(1, resp.OutputTokens);
    }

    /// <summary>返回指定状态码响应的假 HttpClient 处理器。</summary>
    private sealed class StatusHandler : HttpMessageHandler
    {
        public int StatusCode { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)StatusCode)
            {
                Content = new StringContent("""{"error":{"message":"boom"}}""", Encoding.UTF8, "application/json"),
            });
    }

    [Theory]
    [InlineData(429, true)]   // 限流 → 可重试
    [InlineData(500, true)]   // 服务端错误 → 可重试
    [InlineData(503, true)]
    [InlineData(400, false)]  // 请求错误 → 不可重试
    [InlineData(401, false)]  // 鉴权失败 → 不可重试
    public async Task ChatAsync_ErrorStatus_ClassifiesRetryable(int statusCode, bool expectRetryable)
    {
        var provider = new OpenAiProvider(
            new ProviderOptions { ApiKey = "test-key" }, new HttpClient(new StatusHandler { StatusCode = statusCode }));

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.ChatAsync(
                [new ProviderMessage { Role = MessageRole.User, Content = "hi" }],
                [], "off", CancellationToken.None));

        Assert.Equal(statusCode, ex.StatusCode);
        Assert.Equal(expectRetryable, ex.Retryable); // 重试分类决定 Agent 是否自动重试
    }

    [Fact]
    public async Task ChatAsync_ContentAsArray_JoinsTextBlocks()
    {
        // 回归：部分兼容服务把 content 返回为分块数组；原实现先对 JsonArray 调 GetValue<string>
        // 直接抛 InvalidOperationException，后面的数组分支永远不可达
        var handler = new CaptureHandler
        {
            OverrideBody = """{"choices":[{"message":{"role":"assistant","content":[{"type":"text","text":"你"},{"type":"text","text":"好"}]}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""",
        };
        var provider = new OpenAiProvider(
            new ProviderOptions { ApiKey = "test-key" }, new HttpClient(handler));

        var resp = await provider.ChatAsync(
            [new ProviderMessage { Role = MessageRole.User, Content = "hi" }],
            [], "off", CancellationToken.None);

        Assert.Equal("你好", resp.Text);
    }

    [Fact]
    public async Task GetContextWindowAsync_ReadsMetadataFields()
    {
        // OpenRouter 风格 /models 元数据带窗口字段时可自动探测
        var handler = new CaptureHandler
        {
            OverrideBody = """{"data":[{"id":"hy3:free","context_length":131072,"top_provider":{"context_length":131072}}]}""",
        };
        var provider = new OpenAiProvider(
            new ProviderOptions { ApiKey = "test-key" }, new HttpClient(handler));
        Assert.Equal(131072, await provider.GetContextWindowAsync("hy3:free", CancellationToken.None));
    }

    [Fact]
    public async Task GetContextWindowAsync_TopProviderFallback()
    {
        var handler = new CaptureHandler
        {
            OverrideBody = """{"data":[{"id":"m1","top_provider":{"context_length":65536}}]}""",
        };
        var provider = new OpenAiProvider(
            new ProviderOptions { ApiKey = "test-key" }, new HttpClient(handler));
        Assert.Equal(65536, await provider.GetContextWindowAsync("m1", CancellationToken.None));
    }

    [Fact]
    public async Task GetContextWindowAsync_NoFieldsOrNullModel_ReturnsNull()
    {
        // 标准 OpenAI 协议 /models 无窗口字段：返回 null（显示层回退到模型表/纯数字）
        var handler = new CaptureHandler
        {
            OverrideBody = """{"data":[{"id":"gpt-4o","object":"model","created":1}]}""",
        };
        var provider = new OpenAiProvider(
            new ProviderOptions { ApiKey = "test-key" }, new HttpClient(handler));
        Assert.Null(await provider.GetContextWindowAsync("gpt-4o", CancellationToken.None));
        Assert.Null(await provider.GetContextWindowAsync("not-in-list", CancellationToken.None));
    }

    [Fact]
    public async Task GetContextWindowAsync_SkipsNullAndScalarEntries()
    {
        // 回归：data 混入 null / 标量项时曾对 null 解引用（被 catch 吞掉误报 null），
        // 对标量项索引会抛 InvalidOperationException；非对象项应被跳过而不是中断探测
        var handler = new CaptureHandler
        {
            OverrideBody = """{"data":[null,"stray",{"id":"m2","context_length":8192}]}""",
        };
        var provider = new OpenAiProvider(
            new ProviderOptions { ApiKey = "test-key" }, new HttpClient(handler));
        Assert.Equal(8192, await provider.GetContextWindowAsync("m2", CancellationToken.None));
    }

    [Fact]
    public async Task ChatAsync_RequestTimeout_ThrowsProviderException()
    {
        // 用短超时的 HttpClient：SlowHandler 永远延迟，触发超时路径（避免等默认 100s）
        var http = new HttpClient(new SlowHandler()) { Timeout = TimeSpan.FromSeconds(2) };
        var provider = new OpenAiProvider(new ProviderOptions { ApiKey = "test-key" }, http);

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.ChatAsync(
                [new ProviderMessage { Role = MessageRole.User, Content = "hi" }],
                [], "off", CancellationToken.None));
        Assert.Contains("超时", ex.Message);
    }

    /// <summary>永远延迟的处理器：触发 HttpClient 超时（用极短超时验证）。</summary>
    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => new HttpResponseMessage(), ct);
    }

    [Fact]
    public async Task ChatAsync_ReasoningModel_UsesMaxCompletionTokens_NoTemperature()
    {
        // 回归：o1/o3/o4/gpt-5 系列不接受 temperature 与 max_tokens，曾直接 400
        var handler = new CaptureHandler
        {
            OverrideBody = """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""",
        };
        var provider = new OpenAiProvider(
            new ProviderOptions { ApiKey = "k", Model = "openai/o3-mini" }, new HttpClient(handler));

        await provider.ChatAsync(
            [new ProviderMessage { Role = MessageRole.User, Content = "hi" }],
            [], "off", CancellationToken.None);

        var body = JsonNode.Parse(handler.LastBody!)!;
        Assert.Null(body["temperature"]);
        Assert.Null(body["max_tokens"]);
        Assert.Equal(8192, (int)body["max_completion_tokens"]!);
    }

    [Fact]
    public async Task ChatAsync_RegularModel_KeepsTemperatureAndMaxTokens()
    {
        var handler = new CaptureHandler
        {
            OverrideBody = """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""",
        };
        var provider = new OpenAiProvider(
            new ProviderOptions { ApiKey = "k", Model = "deepseek-chat", Temperature = 0.3 }, new HttpClient(handler));

        await provider.ChatAsync(
            [new ProviderMessage { Role = MessageRole.User, Content = "hi" }],
            [], "off", CancellationToken.None);

        var body = JsonNode.Parse(handler.LastBody!)!;
        Assert.Equal(0.3, (double)body["temperature"]!, 5);
        Assert.Equal(8192, (int)body["max_tokens"]!);
        Assert.Null(body["max_completion_tokens"]);
    }

    [Theory]
    [InlineData("off", false)]
    [InlineData("auto", false)] // auto：模型 gpt-4o 不在推理表且 /models 无 reasoning 字段 → 不发送
    [InlineData("low", true)]
    [InlineData("medium", true)]
    [InlineData("high", true)]
    public async Task ChatAsync_ThinkingEffort_ControlsReasoningEffort(string effort, bool expectSent)
    {
        var handler = new CaptureHandler
        {
            OverrideBody = """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""",
        };
        var provider = new OpenAiProvider(
            new ProviderOptions { ApiKey = "k" }, new HttpClient(handler));

        await provider.ChatAsync(
            [new ProviderMessage { Role = MessageRole.User, Content = "hi" }],
            [], effort, CancellationToken.None);

        var body = JsonNode.Parse(handler.LastBody!)!;
        if (expectSent)
            Assert.Equal(effort, (string)body["reasoning_effort"]!);
        else
            Assert.Null(body["reasoning_effort"]);
    }

    [Fact]
    public async Task GetSupportedEffortsAsync_MetadataEffortField_ReturnsEfforts()
    {
        // OpenRouter 风格：/models 元数据 reasoning.effort 声明档位 → 返回升序档位列表
        var handler = new CaptureHandler
        {
            OverrideBody = """{"data":[{"id":"hy3:free","reasoning":{"effort":{"low":true,"medium":true,"high":false}}}]}""",
        };
        var provider = new OpenAiProvider(
            new ProviderOptions { ApiKey = "test-key", Model = "hy3:free" }, new HttpClient(handler));
        Assert.Equal(["low", "medium"], await provider.GetSupportedEffortsAsync("hy3:free", CancellationToken.None));
    }

    [Fact]
    public async Task GetSupportedEffortsAsync_NoMetadataField_FallsBackToPrefixTable()
    {
        // /models 元数据无 effort 字段（标准 OpenAI 协议）→ 回退内置前缀表
        var handler = new CaptureHandler
        {
            OverrideBody = """{"data":[{"id":"o3-mini","object":"model","created":1}]}""",
        };
        var provider = new OpenAiProvider(
            new ProviderOptions { ApiKey = "test-key" }, new HttpClient(handler));
        Assert.Equal(["low", "medium", "high"], await provider.GetSupportedEffortsAsync("o3-mini", CancellationToken.None));
    }

    [Fact]
    public async Task GetSupportedEffortsAsync_UnknownModel_ReturnsNull()
    {
        // 元数据无能力字段且不在前缀表 → null（按不支持处理，但语义上"无法确定"）
        var handler = new CaptureHandler
        {
            OverrideBody = """{"data":[{"id":"gpt-4o","object":"model","created":1}]}""",
        };
        var provider = new OpenAiProvider(
            new ProviderOptions { ApiKey = "test-key" }, new HttpClient(handler));
        Assert.Null(await provider.GetSupportedEffortsAsync("gpt-4o", CancellationToken.None));
    }

    [Fact]
    public async Task ChatAsync_AutoEffort_SupportedModel_SendsHighestEffort()
    {
        // auto + 模型支持推理（前缀表命中 o3-mini）→ 取最高可用档 high
        var handler = new CaptureHandler
        {
            OverrideBody = """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""",
        };
        var provider = new OpenAiProvider(
            new ProviderOptions { ApiKey = "k", Model = "o3-mini" }, new HttpClient(handler));

        await provider.ChatAsync(
            [new ProviderMessage { Role = MessageRole.User, Content = "hi" }],
            [], "auto", CancellationToken.None);

        var body = JsonNode.Parse(handler.LastBody!)!;
        Assert.Equal("high", (string)body["reasoning_effort"]!);
    }

    [Fact]
    public async Task ChatAsync_AutoEffort_UnsupportedModel_DoesNotSend()
    {
        // auto + 模型不支持推理（前缀表未命中）→ 不发 reasoning_effort，避免 400
        var handler = new CaptureHandler
        {
            OverrideBody = """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""",
        };
        var provider = new OpenAiProvider(
            new ProviderOptions { ApiKey = "k", Model = "deepseek-chat" }, new HttpClient(handler));

        await provider.ChatAsync(
            [new ProviderMessage { Role = MessageRole.User, Content = "hi" }],
            [], "auto", CancellationToken.None);

        var body = JsonNode.Parse(handler.LastBody!)!;
        Assert.Null(body["reasoning_effort"]);
    }

    /// <summary>/models 先失败后成功的可变处理器：模拟瞬时网络故障恢复。</summary>
    private sealed class FlakyModelsHandler : HttpMessageHandler
    {
        public bool ModelsFailing = true;
        public string? LastBody;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            HttpResponseMessage Resp(string json) => new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            if (request.RequestUri!.AbsolutePath.EndsWith("/models"))
            {
                return Task.FromResult(ModelsFailing
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") }
                    : Resp("""{"data":[{"id":"qwen3-max","reasoning":{"effort":{"low":true,"high":true}}}]}"""));
            }
            LastBody = request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            return Task.FromResult(Resp("""{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}"""));
        }
    }

    [Fact]
    public async Task ChatAsync_AutoEffort_TransientProbeFailure_IsNotCached()
    {
        // 回归：探测失败曾把「回退前缀表」的结果也写进缓存——qwen3-max 不在前缀表，
        // 一次断网后 auto 永远 off，网络恢复也不重试。失败结果不得缓存。
        var handler = new FlakyModelsHandler();
        var provider = new OpenAiProvider(
            new ProviderOptions { ApiKey = "k", Model = "qwen3-max", BaseUrl = "http://effort-cache-test.local/v1" },
            new HttpClient(handler));

        // 第一次：/models 500 → 探测失败 → 回退前缀表（未命中）→ off
        await provider.ChatAsync([new ProviderMessage { Role = MessageRole.User, Content = "hi" }],
            [], "auto", CancellationToken.None);
        Assert.Null(JsonNode.Parse(handler.LastBody!)!["reasoning_effort"]);

        // 网络恢复：/models 返回 reasoning.effort 元数据 → 未被缓存污染，重新探测 → high
        handler.ModelsFailing = false;
        await provider.ChatAsync([new ProviderMessage { Role = MessageRole.User, Content = "hi" }],
            [], "auto", CancellationToken.None);
        Assert.Equal("high", (string)JsonNode.Parse(handler.LastBody!)!["reasoning_effort"]!);
    }
    [Fact]
    public async Task ChatAsync_FinishReasonLength_IsSurfaced()
    {
        // 截断（finish_reason=length）必须暴露给调用方：Agent 据此显示 ⚠ 提示，否则半句话静默呈现
        var handler = new CaptureHandler
        {
            OverrideBody = """{"choices":[{"message":{"role":"assistant","content":"半句话"},"finish_reason":"length"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""",
        };
        var provider = new OpenAiProvider(new ProviderOptions { ApiKey = "test-key" }, new HttpClient(handler));

        var resp = await provider.ChatAsync(
            [new ProviderMessage { Role = MessageRole.User, Content = "hi" }],
            [], "off", CancellationToken.None);

        Assert.Equal("length", resp.FinishReason);
    }
}
