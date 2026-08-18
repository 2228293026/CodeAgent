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

/// <summary>Provider 响应解析与工厂的边界测试(补充 OpenAiProviderTests / ProviderFactoryTests)。</summary>
public class ProviderEdgeTests
{
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string OverrideBody { get; set; } = "";
        public int StatusCode { get; set; } = 200;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = OverrideBody.Length > 0
                ? OverrideBody
                : """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)StatusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static OpenAiProvider MakeOpenAi(CaptureHandler handler) =>
        new(new ProviderOptions { ApiKey = "k", Model = "m", BaseUrl = "https://x.test/v1" }, new HttpClient(handler));

    private static readonly ProviderMessage[] Msgs = [new() { Role = MessageRole.User, Content = "hi" }];

    // ===== OpenAI 响应解析 =====

    [Fact]
    public async Task ChatAsync_UsageTokens_AreParsed()
    {
        var h = new CaptureHandler
        {
            OverrideBody = """{"choices":[{"message":{"content":"done"}}],"usage":{"prompt_tokens":12,"completion_tokens":34}}""",
        };
        var resp = await MakeOpenAi(h).ChatAsync(Msgs, [], "off", CancellationToken.None);
        Assert.Equal(12, resp.InputTokens);
        Assert.Equal(34, resp.OutputTokens);
        Assert.Equal("done", resp.Text);
    }

    [Fact]
    public async Task ChatAsync_CachedTokens_AreParsed()
    {
        var h = new CaptureHandler
        {
            OverrideBody = """{"choices":[{"message":{"content":"x"}}],"usage":{"prompt_tokens":100,"completion_tokens":5,"prompt_tokens_details":{"cached_tokens":60}}}""",
        };
        var resp = await MakeOpenAi(h).ChatAsync(Msgs, [], "off", CancellationToken.None);
        Assert.Equal(60, resp.CachedTokens);
    }

    [Fact]
    public async Task ChatAsync_ToolCall_IsParsedFromResponse()
    {
        var h = new CaptureHandler
        {
            OverrideBody = """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"tc1","function":{"name":"read_file","arguments":"{\"path\":\"a.txt\"}"}}]}}]}""",
        };
        var resp = await MakeOpenAi(h).ChatAsync(Msgs, [], "off", CancellationToken.None);
        var call = Assert.Single(resp.ToolCalls);
        Assert.Equal("tc1", call.Id);
        Assert.Equal("read_file", call.Name);
        Assert.Contains("a.txt", call.ArgumentsJson);
        Assert.Null(resp.Text); // 无文本
    }

    [Fact]
    public async Task ChatAsync_MissingToolCallFields_UseFallbacks()
    {
        var h = new CaptureHandler
        {
            OverrideBody = """{"choices":[{"message":{"content":null,"tool_calls":[{"function":{"arguments":"{}"}}]}}]}""",
        };
        var resp = await MakeOpenAi(h).ChatAsync(Msgs, [], "off", CancellationToken.None);
        var call = Assert.Single(resp.ToolCalls);
        Assert.False(string.IsNullOrEmpty(call.Id));       // 缺 id → GUID 兜底
        Assert.Equal("unknown", call.Name);                // 缺 name → unknown
        Assert.Equal("{}", call.ArgumentsJson);            // 缺 arguments → {}
    }

    [Fact]
    public async Task ChatAsync_EmptyContent_ReturnsNullText()
    {
        var h = new CaptureHandler { OverrideBody = """{"choices":[{"message":{"content":""}}]}""" };
        var resp = await MakeOpenAi(h).ChatAsync(Msgs, [], "off", CancellationToken.None);
        Assert.Null(resp.Text); // 空白文本 → null
    }

    [Fact]
    public async Task ChatAsync_HttpError_ThrowsWithRetryableFlag()
    {
        var h = new CaptureHandler { StatusCode = 503, OverrideBody = "{}" };
        var ex = await Assert.ThrowsAsync<ProviderException>(
            () => MakeOpenAi(h).ChatAsync(Msgs, [], "off", CancellationToken.None));
        Assert.True(ex.Retryable); // 5xx 可重试
    }

    [Fact]
    public async Task ChatAsync_HttpError4xx_NotRetryable()
    {
        var h = new CaptureHandler { StatusCode = 400, OverrideBody = "bad request" };
        var ex = await Assert.ThrowsAsync<ProviderException>(
            () => MakeOpenAi(h).ChatAsync(Msgs, [], "off", CancellationToken.None));
        Assert.False(ex.Retryable); // 4xx 不重试
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task ChatAsync_InvalidJsonResponse_Throws()
    {
        var h = new CaptureHandler { OverrideBody = "not json at all" };
        await Assert.ThrowsAsync<ProviderException>(
            () => MakeOpenAi(h).ChatAsync(Msgs, [], "off", CancellationToken.None));
    }

    // ===== ProviderFactory 边界 =====

    [Fact]
    public void Create_MissingProviderKey_AddsDefaults()
    {
        // 缺 providers 键时自动补默认项（无 ApiKey，靠环境变量满足构造校验）
        var cfg = new AgentConfig { Provider = "ghost", Providers = new(StringComparer.OrdinalIgnoreCase) { ["real"] = new ProviderOptions() } };
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        try
        {
            var p = ProviderFactory.Create(cfg);
            Assert.IsType<OpenAiProvider>(p);
            Assert.True(cfg.Providers.ContainsKey("ghost")); // 自动补默认项
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        }
    }

    [Fact]
    public void Create_TypeClaude_AliasesToAnthropic()
    {
        var cfg = new AgentConfig
        {
            Provider = "c",
            Providers = new(StringComparer.OrdinalIgnoreCase) { ["c"] = new ProviderOptions { Type = "claude", ApiKey = "test-key" } },
        };
        var p = ProviderFactory.Create(cfg);
        Assert.IsType<AnthropicProvider>(p);
    }

    [Fact]
    public void Create_BlankProvider_FallsBackToOpenAi()
    {
        var cfg = new AgentConfig { Provider = "", Providers = new() };
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        try
        {
            var p = ProviderFactory.Create(cfg);
            Assert.IsType<OpenAiProvider>(p);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        }
    }

    // ===== KnownReasoningModels 前缀表 =====

    [Theory]
    [InlineData("o3-mini")]          // 命中 → 返回升序档位列表
    [InlineData("openai/o4-mini")]   // 去厂商前缀后命中
    [InlineData("deepseek-r1")]
    [InlineData("deepseek-reasoner")]
    [InlineData("claude-3-7-sonnet-latest")] // 3.7 起支持 thinking
    [InlineData("claude-opus-4-1")]
    [InlineData("claude-sonnet-4-5")]
    public void KnownReasoningModels_ReasoningPrefixes_ReturnEfforts(string model)
    {
        Assert.Equal(["low", "medium", "high"], KnownReasoningModels.TryGet(model));
    }

    [Theory]
    [InlineData("gpt-4o")]           // 普通模型：不在表中
    [InlineData("claude-3-5-sonnet-latest")] // 3.5 系列不支持 thinking：返回 null 而非档位
    [InlineData("anthropic/claude-3-5-haiku")]
    [InlineData("claude-2.1")]
    [InlineData("qwen2.5-coder:7b")]
    [InlineData("")]
    [InlineData(null)]
    public void KnownReasoningModels_OtherModels_ReturnNull(string? model)
    {
        Assert.Null(KnownReasoningModels.TryGet(model));
    }
}
