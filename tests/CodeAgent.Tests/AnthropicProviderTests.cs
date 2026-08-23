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

        /// <summary>覆写响应体（默认返回 chat 响应）。</summary>
        public string OverrideBody { get; set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            var body = OverrideBody.Length > 0
                ? OverrideBody
                : """{"content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":1,"output_tokens":1}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
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
        var last = msgs![^1]!;
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
    public async Task ChatAsync_TinyMaxTokens_ThinkingFallsBackToTemperature()
    {
        // 回归：maxTokens ≤ 1024 放不下最小思考预算（budget 必须 < max_tokens），
        // 硬启用 thinking 会被 API 400 拒绝；应退回 temperature 让请求本身成功
        var handler = new CaptureHandler();
        var provider = new AnthropicProvider(
            new ProviderOptions { ApiKey = "test-key", MaxTokens = 512 }, new HttpClient(handler));

        var messages = new[]
        {
            new ProviderMessage { Role = MessageRole.System, Content = "sys" },
            new ProviderMessage { Role = MessageRole.User, Content = "hi" },
        };

        await provider.ChatAsync(messages, [], "high", CancellationToken.None);

        var body = JsonNode.Parse(handler.LastBody!);
        Assert.Null(body?["thinking"]);
        Assert.NotNull(body?["temperature"]);
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
    public async Task ChatAsync_ThinkingAuto_ClaudeModel_EnablesThinking()
    {
        // auto + claude 系列（前缀表命中）→ 取最高档 high（thinking 启用，预算收敛到 max_tokens-1024）
        var handler = new CaptureHandler();
        var provider = MakeProvider(handler);

        var messages = new[]
        {
            new ProviderMessage { Role = MessageRole.System, Content = "sys" },
            new ProviderMessage { Role = MessageRole.User, Content = "hi" },
        };

        await provider.ChatAsync(messages, [], "auto", CancellationToken.None);

        var body = JsonNode.Parse(handler.LastBody!);
        Assert.NotNull(body?["thinking"]);
        Assert.Equal(7168, (int)body!["thinking"]!["budget_tokens"]!); // high 预算 16384 收敛到 8192-1024
    }

    [Fact]
    public async Task ChatAsync_ThinkingAuto_NonClaudeModel_TreatedLikeOff()
    {
        // auto + 非 claude 模型（不在推理表）→ 不发 thinking，回退 temperature
        var handler = new CaptureHandler();
        var provider = new AnthropicProvider(
            new ProviderOptions { ApiKey = "test-key", Model = "gpt-4o" }, new HttpClient(handler));

        var messages = new[]
        {
            new ProviderMessage { Role = MessageRole.System, Content = "sys" },
            new ProviderMessage { Role = MessageRole.User, Content = "hi" },
        };

        await provider.ChatAsync(messages, [], "auto", CancellationToken.None);

        var body = JsonNode.Parse(handler.LastBody!);
        Assert.Null(body!["thinking"]);
        Assert.NotNull(body?["temperature"]);
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
        Assert.Single(msgs!); // 两条连续 user 已合并为 1 条消息
        var user = msgs[0]!;
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
        var asst = msgs![^1]!;
        Assert.Equal("assistant", asst["role"]!.GetValue<string>());
        var content = asst["content"]!.AsArray();
        Assert.Equal(2, content.Count); // 文本块 + tool_use 块
        Assert.Equal("text", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("tool_use", content[1]!["type"]!.GetValue<string>());
        Assert.Equal("call_9", content[1]!["id"]!.GetValue<string>());
        Assert.Equal("glob", content[1]!["name"]!.GetValue<string>());
        Assert.Equal("""{"pattern":"*.cs"}""", content[1]!["input"]!.ToJsonString());
    }

    [Fact]
    public async Task ListModelsAsync_ParsesModelIds()
    {
        var handler = new CaptureHandler
        {
            OverrideBody = """{"data":[{"id":"claude-sonnet-4-5"},{"id":"claude-opus-4"}]}""",
        };
        var provider = MakeProvider(handler);

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["claude-sonnet-4-5", "claude-opus-4"], models);
    }

    [Fact]
    public async Task ChatAsync_NonStream_ThinkingBlock_IsCaptured()
    {
        // 回归：thinking 块的文本与签名都要从非流式响应捕获（工具调用轮回传必需）
        var handler = new CaptureHandler
        {
            OverrideBody = """
                {"content":[
                    {"type":"thinking","thinking":"推理过程","signature":"sig-xyz"},
                    {"type":"text","text":"结论"},
                    {"type":"tool_use","id":"t1","name":"stop","input":{}}
                ],"usage":{"input_tokens":1,"output_tokens":1}}
                """,
        };
        var provider = MakeProvider(handler);

        var resp = await provider.ChatAsync(
            [new ProviderMessage { Role = MessageRole.User, Content = "q" }],
            [], "high", CancellationToken.None);

        Assert.Equal("推理过程", resp.ThinkingText);
        Assert.Equal("sig-xyz", resp.ThinkingSignature);
        Assert.Equal("结论", resp.Text);
        Assert.Single(resp.ToolCalls);
    }

    [Fact]
    public async Task ChatAsync_AssistantThinkingBlock_IsRoundTripped()
    {
        // 回传路径：历史里的 assistant 消息带 ThinkingText 时，请求体的 content
        // 必须以 thinking 块开头（文本 + 签名成对）——缺失会被 Anthropic 400 拒绝
        var handler = new CaptureHandler();
        var provider = MakeProvider(handler);

        var messages = new[]
        {
            new ProviderMessage { Role = MessageRole.System, Content = "sys" },
            new ProviderMessage { Role = MessageRole.User, Content = "hi" },
            new ProviderMessage
            {
                Role = MessageRole.Assistant,
                ThinkingText = "推理过程",
                ThinkingSignature = "sig-xyz",
                ToolCalls = [new ToolCall { Id = "c1", Name = "stop", ArgumentsJson = "{}" }],
            },
            new ProviderMessage { Role = MessageRole.Tool, ToolCallId = "c1", ToolName = "stop", Content = "done" },
        };

        await provider.ChatAsync(messages, [], "high", CancellationToken.None);

        var msgs = JsonNode.Parse(handler.LastBody!)?["messages"]?.AsArray();
        Assert.NotNull(msgs);
        var asst = msgs![^2]!; // tool_result 前的 assistant
        Assert.Equal("assistant", asst["role"]!.GetValue<string>());
        var content = asst["content"]!.AsArray();
        Assert.Equal("thinking", content[0]!["type"]!.GetValue<string>()); // thinking 块在最前
        Assert.Equal("推理过程", content[0]!["thinking"]!.GetValue<string>());
        Assert.Equal("sig-xyz", content[0]!["signature"]!.GetValue<string>());
        Assert.Equal("tool_use", content[1]!["type"]!.GetValue<string>()); // 后跟 tool_use
    }

    [Fact]
    public async Task ChatAsync_MidConversationSystem_BecomesUserBlock()
    {
        // 回归：/compact 在历史中间插入的【历史摘要】是 System 消息，
        // Anthropic 没有中间 system 角色——此前被静默丢弃，压缩等于白做
        var handler = new CaptureHandler();
        var provider = MakeProvider(handler);

        var messages = new[]
        {
            new ProviderMessage { Role = MessageRole.System, Content = "sys" },
            new ProviderMessage { Role = MessageRole.User, Content = "第一问" },
            new ProviderMessage { Role = MessageRole.System, Content = "【历史摘要】用户在做 X" },
            new ProviderMessage { Role = MessageRole.User, Content = "继续" },
        };

        await provider.ChatAsync(messages, [], "off", CancellationToken.None);

        // 首条 system 只走顶层字段
        var body = JsonNode.Parse(handler.LastBody!);
        Assert.Equal("sys", body!["system"]!.GetValue<string>());

        // 中间 system 转成 user 文本块保留：连续同角色合并后是单条 user、3 个文本块
        var msgs = body["messages"]!.AsArray();
        Assert.Single(msgs);
        var content = msgs[0]!["content"]!.AsArray();
        Assert.Equal(3, content.Count);
        Assert.Equal("第一问", content[0]!["text"]!.GetValue<string>());
        Assert.Contains("【历史摘要】", content[1]!["text"]!.GetValue<string>()); // 摘要没被丢
        Assert.Equal("继续", content[2]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task ChatAsync_CacheReadTokens_TopLevelField_IsParsed()
    {
        // 回归：Anthropic 的缓存命中在 usage 顶层（cache_read_input_tokens）；
        // 之前只读 OpenAI 风格的嵌套 input_tokens_details，真实响应恒解析为 null
        var handler = new CaptureHandler
        {
            OverrideBody = """
                {"content":[{"type":"text","text":"ok"}],
                 "usage":{"input_tokens":10,"output_tokens":5,"cache_read_input_tokens":8}}
                """,
        };
        var provider = MakeProvider(handler);

        var resp = await provider.ChatAsync(
            [new ProviderMessage { Role = MessageRole.User, Content = "hi" }],
            [], "off", CancellationToken.None);

        Assert.Equal(8, resp.CachedTokens);
    }

    [Fact]
    public async Task ChatAsync_StringTokenCounts_AreParsedNotFatal()
    {
        // 回归：字符串形态的 usage 计数（网关变体）不应抛异常中断解析
        var handler = new CaptureHandler
        {
            OverrideBody = """
                {"content":[{"type":"text","text":"ok"}],
                 "usage":{"input_tokens":"10","output_tokens":"5"}}
                """,
        };
        var provider = MakeProvider(handler);

        var resp = await provider.ChatAsync(
            [new ProviderMessage { Role = MessageRole.User, Content = "hi" }],
            [], "off", CancellationToken.None);

        Assert.Equal(10, resp.InputTokens);
        Assert.Equal(5, resp.OutputTokens);
    }

    [Fact]
    public async Task ChatAsync_NoTools_OmitsToolsField()
    {
        // 回归：空工具列表曾发送 "tools": []（/compact 摘要调用），Anthropic 对此返回 400
        var handler = new CaptureHandler
        {
            OverrideBody = """{"content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":1,"output_tokens":1}}""",
        };
        var provider = new AnthropicProvider(
            new ProviderOptions { ApiKey = "test-key" }, new HttpClient(handler));

        await provider.ChatAsync(
            [new ProviderMessage { Role = MessageRole.User, Content = "hi" }],
            [], "off", CancellationToken.None);

        var body = JsonNode.Parse(handler.LastBody!);
        Assert.Null(body?["tools"]); // 无工具时整个字段不发
    }

    [Fact]
    public async Task ChatAsync_WithTools_IncludesToolsField()
    {
        var handler = new CaptureHandler
        {
            OverrideBody = """{"content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":1,"output_tokens":1}}""",
        };
        var provider = new AnthropicProvider(
            new ProviderOptions { ApiKey = "test-key" }, new HttpClient(handler));

        var spec = new ToolSpec
        {
            Name = "stop",
            Description = "end",
            Parameters = new JsonObject { ["type"] = "object" },
        };
        await provider.ChatAsync(
            [new ProviderMessage { Role = MessageRole.User, Content = "hi" }],
            [spec], "off", CancellationToken.None);

        var body = JsonNode.Parse(handler.LastBody!);
        Assert.NotNull(body?["tools"]);
    }
    [Fact]
    public async Task ListModelsAsync_FollowsPagination()
    {
        // Anthropic /models 每页默认 20 条：has_more + after_id 必须翻页，否则大账号只见第一页
        var page = 0;
        var provider = new AnthropicProvider(
            new ProviderOptions { ApiKey = "k" },
            new HttpClient(new PagedHandler(() => page++ switch
            {
                0 => """{"data":[{"id":"claude-a"},{"id":"claude-b"}],"has_more":true,"last_id":"claude-b"}""",
                _ => """{"data":[{"id":"claude-c"}],"has_more":false}""",
            })));

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["claude-a", "claude-b", "claude-c"], models);
        Assert.Equal(2, page); // 确实请求了第二页
    }

    private sealed class PagedHandler(Func<string> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body(), Encoding.UTF8, "application/json"),
            });
    }
}
