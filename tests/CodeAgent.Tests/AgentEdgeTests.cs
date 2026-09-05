using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent;
using CodeAgent.Providers;
using CodeAgent.Tools;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>Agent 生命周期/统计/权限/压缩的边界测试(补充 AgentLoopTests / AgentTrimHistoryTests)。</summary>
public class AgentEdgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-agent-" + Guid.NewGuid().ToString("N"));
    private string SessionDir => Path.Combine(_dir, ".codeagent", "sessions");

    public AgentEdgeTests() => Directory.CreateDirectory(SessionDir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
    }

    private AgentClass MakeAgent(FakeProvider provider) => new(
        new AgentConfig
        {
            SaveSessions = false,
            SessionDir = SessionDir,
            MaxToolIterations = 5,
        },
        provider,
        ToolRegistry.CreateDefault());

    private AgentClass MakeAgent(IAgentProvider provider, AgentConfig? config = null) => new(
        config ?? new AgentConfig
        {
            SaveSessions = false,
            SessionDir = SessionDir,
            MaxToolIterations = 5,
        },
        provider,
        ToolRegistry.CreateDefault());

    [Fact]
    public void SetFileAccess_UpdatesWorkspace()
    {
        var agent = MakeAgent(new FakeProvider());
        Assert.False(agent.Context.Workspace.FullAccess); // 默认 strict

        agent.SetFileAccess("full");
        Assert.True(agent.Context.Workspace.FullAccess);

        agent.SetFileAccess("strict");
        Assert.False(agent.Context.Workspace.FullAccess);
    }

    [Fact]
    public async Task RunAsync_RunawayLoop_CountsCallsAndRounds()
    {
        // read_file 报错不置 StopRequested：循环直到 MaxToolIterations=5
        var provider = new FakeProvider
        {
            NextResponse = new ProviderResponse
            {
                ToolCalls = [new ToolCall { Id = "l", Name = "read_file", ArgumentsJson = """{"path":"no-such"}""" }],
            },
        };
        var agent = MakeAgent(provider);

        var result = await agent.RunAsync("循环", CancellationToken.None);
        Assert.Contains("最大工具调用轮数", result);
        Assert.Equal(5, agent.ProviderCalls);
        Assert.Equal(5, agent.TurnRounds);
        Assert.Equal(5, agent.TurnToolCalls);
        Assert.True(agent.LastTurnFailed);
    }

    [Fact]
    public async Task RunAsync_TurnTokens_AccumulateAcrossCalls()
    {
        var provider = new FakeProvider
        {
            NextResponse = new ProviderResponse
            {
                InputTokens = 100,
                OutputTokens = 20,
                ToolCalls = [new ToolCall { Id = "l", Name = "read_file", ArgumentsJson = """{"path":"no-such"}""" }],
            },
        };
        var agent = MakeAgent(provider);

        await agent.RunAsync("x", CancellationToken.None); // 5 轮
        Assert.Equal(500, agent.TurnInputTokens);  // 100 * 5
        Assert.Equal(100, agent.TurnOutputTokens); // 20 * 5
        Assert.Equal(500, agent.TotalInputTokens);
        Assert.Equal(100, agent.TotalOutputTokens);
    }

    [Fact]
    public async Task RunAsync_TotalTokens_AccumulateAcrossTurns()
    {
        var provider = new FakeProvider
        {
            NextResponse = new ProviderResponse { Text = "ok", InputTokens = 50, OutputTokens = 10 },
        };
        var agent = MakeAgent(provider);
        await agent.RunAsync("第一轮", CancellationToken.None);
        await agent.RunAsync("第二轮", CancellationToken.None);

        Assert.Equal(100, agent.TotalInputTokens);  // 50 * 2
        Assert.Equal(20, agent.TotalOutputTokens);  // 10 * 2
        Assert.Equal(2, agent.ProviderCalls);
    }

    [Fact]
    public void EditPreviewText_ShowsChangedLinesNotRawFragments()
    {
        // 回归：edit_file 预览曾只显示截断的 old/new 片段（共享长前缀时看不出差异）；
        // 现在直接给行级 diff
        var args = new System.Text.Json.Nodes.JsonObject
        {
            ["path"] = "a.cs",
            ["old_string"] = "case \"/compact\":\n                // 用户主动压缩上下文",
            ["new_string"] = "case \"/compact\":\n                // 压缩历史为摘要",
        };
        var preview = AgentClass.EditPreviewText(args);
        Assert.Contains("-                 // 用户主动压缩上下文", preview); // 旧内容（红）
        Assert.Contains("+                 // 压缩历史为摘要", preview);         // 新内容（绿）
        Assert.DoesNotContain("old_string=", preview); // 不再是原始参数摘要
    }

    [Fact]
    public void EditPreviewText_NoArgsOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", AgentClass.EditPreviewText(null));
        Assert.Equal("", AgentClass.EditPreviewText(new System.Text.Json.Nodes.JsonObject()));
    }

    [Fact]
    public void SummarizeCall_ChineseArg_ShownAsIs()
    {
        // 回归：参数值曾用 JsonNode.ToJsonString() 提取，默认编码器把中文转义成 \uXXXX，
        // 工具摘要行显示 docs/项目… 而非 docs/项目介绍
        var s = AgentClass.SummarizeCall("read_file", """{"path":"docs/项目介绍.md"}""");
        Assert.Contains("docs/项目介绍.md", s);
        Assert.DoesNotContain("\\u", s);
    }

    [Fact]
    public void SummarizeCall_NumericArg_Stringified()
    {
        var s = AgentClass.SummarizeCall("read_file", """{"path":"a.cs","offset":10}""");
        Assert.Contains("offset=10", s); // 非字符串标量仍显示为其 JSON 文本
    }

    [Fact]
    public void SummarizeCall_EnvValues_AreRedacted()
    {
        // 隐私：env 键值对常携带 API Key 等敏感值，摘要行/会话日志不得出现明文
        var s = AgentClass.SummarizeCall("run_command",
            """{"command":"deploy","env":{"API_KEY":"sk-secret-123","TOKEN":"hunter2"}}""");

        Assert.Contains("env=(已省略)", s);
        Assert.DoesNotContain("sk-secret-123", s);
        Assert.DoesNotContain("hunter2", s);
        Assert.Contains("command=deploy", s); // 其他参数照常展示
    }

    [Fact]
    public void SummarizeCall_LongEmojiArg_NotSplitSurrogate()
    {
        // 回归：超长参数值曾用 v[..60] 截断，切点落在代理对中间会把 emoji 劈成半个码点（终端乱码）
        var emojis = string.Concat(Enumerable.Repeat("😀", 50)); // 100 chars
        var s = AgentClass.SummarizeCall("read_file", $$"""{"path":"{{emojis}}.cs"}""");
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsLowSurrogate(s[i]) && i > 0 && char.IsHighSurrogate(s[i - 1]))
                continue; // 正常配对的低位代理（高位在前一条分支已校验）
            if (char.IsHighSurrogate(s[i]))
                Assert.True(i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]), $"代理对在 {i} 处被劈开");
            else
                Assert.False(char.IsLowSurrogate(s[i]), $"孤立低位代理在 {i} 处");
        }
    }

    [Fact]
    public async Task ContextTracks_UsageAndReset()
    {
        // ctx 口径：最近一次请求的 prompt_tokens；无 usage 或 /clear 后退回字符估算
        var provider = new FakeProvider { NextResponse = new ProviderResponse { Text = "ok", InputTokens = 4321 } };
        var agent = MakeAgent(provider);
        await agent.RunAsync("你好", CancellationToken.None);
        Assert.Equal(4321, agent.ContextTokens);

        agent.Reset(); // 清空后 prompt_tokens 失效，退回估算（仅系统提示，量级很小）
        Assert.True(agent.ContextTokens < 2000, $"清空后 ctx 应为估算值（当前 {agent.ContextTokens}）");
    }

    [Fact]
    public async Task Reset_ClearsLastTurnFailed()
    {
        // /clear 后新会话不应残留上一回合的失败状态：状态栏红标会误导用户
        var provider = new FakeProvider
        {
            NextResponse = new ProviderResponse
            {
                ToolCalls = [new ToolCall { Id = "l", Name = "read_file", ArgumentsJson = """{"path":"no-such"}""" }],
            },
        };
        var agent = MakeAgent(provider);
        await agent.RunAsync("触发失败", CancellationToken.None);
        Assert.True(agent.LastTurnFailed);

        agent.Reset();
        Assert.False(agent.LastTurnFailed);
    }

    [Fact]
    public void ContextTokens_Estimate_WhenNoUsage()
    {
        var agent = MakeAgent(new FakeProvider()); // 未运行：无 usage
        Assert.True(agent.ContextTokens > 0); // 系统提示的字符估算
    }

    [Fact]
    public async Task UndoLastTurn_Consecutive_RemovesBothTurns()
    {
        var provider = new FakeProvider { NextResponse = new ProviderResponse { Text = "ok" } };
        var agent = MakeAgent(provider);
        await agent.RunAsync("一", CancellationToken.None);
        await agent.RunAsync("二", CancellationToken.None);
        Assert.Equal(5, agent.MessageCount); // system + 2×(user+assistant)

        Assert.NotNull(agent.UndoLastTurn());   // 撤回第二轮
        Assert.Equal(3, agent.MessageCount);
        // 多级撤回：连续 ESC 逐轮回退，再撤掉第一轮
        Assert.NotNull(agent.UndoLastTurn());
        Assert.Equal(1, agent.MessageCount);     // 仅剩 system
        Assert.Null(agent.UndoLastTurn());       // 无轮可撤
    }

    [Fact]
    public async Task CompactAsync_ShortHistory_ReturnsFalse()
    {
        // 对话过短（压缩块 < 3 条消息）→ 返回 false（/compact 提示无需压缩）
        var provider = new FakeProvider { NextResponse = new ProviderResponse { Text = "ok" } };
        var agent = MakeAgent(provider);
        await agent.RunAsync("短对话", CancellationToken.None); // system + user + assistant = 3 条

        Assert.False(await agent.CompactAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_EmptyResponse_MarksFailed()
    {
        // 模型空回复（无文本无工具调用）→ LastTurnFailed=true，返回明确提示
        var provider = new FakeProvider { NextResponse = new ProviderResponse { Text = null } };
        var agent = MakeAgent(provider);

        var result = await agent.RunAsync("任务", CancellationToken.None);
        Assert.True(agent.LastTurnFailed);
        Assert.Contains("未返回内容", result);
    }

    [Fact]
    public async Task RunAsync_StopTool_EndsImmediately()
    {
        var provider = new FakeProvider
        {
            NextResponse = new ProviderResponse
            {
                ToolCalls = [new ToolCall { Id = "s", Name = "stop", ArgumentsJson = """{"reason":"done"}""" }],
            },
        };
        var agent = MakeAgent(provider);

        var result = await agent.RunAsync("结束", CancellationToken.None);
        Assert.Contains("stop", result);
        Assert.Equal(1, agent.ProviderCalls); // 一轮即结束
        Assert.True(agent.Context.StopRequested);
    }

    [Fact]
    public async Task Reset_KeepsSystemAndClearsStats_ButNotTotal()
    {
        var provider = new FakeProvider { NextResponse = new ProviderResponse { Text = "ok", InputTokens = 10 } };
        var agent = MakeAgent(provider);
        await agent.RunAsync("x", CancellationToken.None);
        Assert.Equal(10, agent.TotalInputTokens);

        agent.Reset();
        Assert.Equal(1, agent.MessageCount); // 仅 system
        Assert.Equal(MessageRole.System, agent.Messages[0].Role);
        Assert.Equal(10, agent.TotalInputTokens); // 会话级累计不清零（/stats 口径）
    }

    [Fact]
    public async Task UndoLastTurn_RollsSessionLog()
    {
        // 撤回曾只改内存不落盘，--continue 会把已撤回的轮次带回来；
        // 现在撤回滚动新日志并重写剩余消息
        static string[] ReadShared(string p)
        {
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        var provider = new FakeProvider
        {
            NextResponse = new ProviderResponse { Text = "ok" },
        };
        var agent = new AgentClass(
            new AgentConfig { SaveSessions = true, SessionDir = SessionDir, MaxToolIterations = 5 },
            provider, ToolRegistry.CreateDefault(), workingDirectory: _dir);
        await agent.RunAsync("第一轮", CancellationToken.None);
        var firstLog = agent.SessionPath;
        Assert.NotNull(firstLog);

        Assert.NotNull(agent.UndoLastTurn());

        // 新日志已滚动，且内容不含被撤回的「第一轮」
        Assert.NotEqual(firstLog, agent.SessionPath);
        var rolled = ReadShared(agent.SessionPath!);
        Assert.DoesNotContain(rolled, l => l.Contains("第一轮"));
        Assert.Contains(rolled, l => l.Contains("system")); // 自包含：system 提示在
    }

    [Fact]
    public void ContextTokens_CjkHeavyContent_EstimatesPerChar()
    {
        // 中文按每字 ~1 token 估算：chars/4 会把纯中文会话的 ctx 低估约 4 倍
        var agent = new AgentClass(
            new AgentConfig { SaveSessions = false, SystemPrompt = "中文系统提示一二三四五" },
            new FakeProvider(), ToolRegistry.CreateDefault(), workingDirectory: _dir);

        // 无 usage → 走估算分支；系统提示 11 个全角字 → ≥ 11
        Assert.True(agent.ContextTokens >= 11, $"CJK 估算 {agent.ContextTokens} 应接近字数而非字数/4");
    }
    [Fact]
    public void SetMode_UnknownCustomTool_WarnsButApplies()
    {
        var provider = new FakeProvider();
        var agent = MakeAgent(provider);
        var mode = new AgentMode("custom", "c", "prompt", ["read_file", "read_filles"]); // 末项拼错

        agent.SetMode(mode);

        Assert.Equal("custom", agent.CurrentMode.Name); // 模式仍生效
        var tools = agent.ToolsForMode();
        Assert.Single(tools); // 只有合法的 read_file 生效
    }

    [Fact]
    public async Task Reset_ClearsLastPrompt()
    {
        // /clear 后 /retry 不应把旧问题复活进新会话：LastPrompt 随对话一起清空
        var provider = new FakeProvider { NextResponse = new ProviderResponse { Text = "ok" } };
        var agent = MakeAgent(provider);
        await agent.RunAsync("旧问题", CancellationToken.None);
        Assert.NotNull(agent.LastPrompt);

        agent.Reset();

        Assert.Null(agent.LastPrompt);
    }

    [Fact]
    public void CurrentSystemPrompt_TracksModeSwitch()
    {
        var agent = MakeAgent(new FakeProvider());
        var before = agent.CurrentSystemPrompt;
        agent.SetMode(Modes.Find("plan", new AgentConfig()));
        Assert.NotEqual(before, agent.CurrentSystemPrompt); // 切模式后提示变化
        Assert.Contains("PLAN mode", agent.CurrentSystemPrompt);
    }


    /// <summary>脚本化 Provider：第一次调用先回调思考增量再抛可重试异常，之后返回正常回复。
    /// 用于断言「已显示思考内容后不再自动重试」。</summary>
    private sealed class ReasoningThenFailProvider : IAgentProvider
    {
        public int StreamCalls;
        public string Name => "rtf";
        public ProviderResponse? NextResponse { get; set; } = new() { Text = "ok" };

        public Task<ProviderResponse> ChatAsync(IReadOnlyList<ProviderMessage> messages, IReadOnlyList<ToolSpec> tools, string thinkingEffort, CancellationToken ct) =>
            Task.FromResult(NextResponse!);

        public Task<ProviderResponse> ChatStreamAsync(IReadOnlyList<ProviderMessage> messages, IReadOnlyList<ToolSpec> tools, string thinkingEffort, Action<string>? onText, Action<string>? onReasoning, Action<string>? onToolFragment, CancellationToken ct)
        {
            StreamCalls++;
            if (StreamCalls == 1)
            {
                onReasoning?.Invoke("思考片段");
                throw new ProviderException("可重试错误") { Retryable = true };
            }
            return Task.FromResult(NextResponse!);
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(["m"]);
    }

    [Fact]
    public async Task StreamRetry_AfterReasoningShown_DoesNotRetry()
    {
        // 回归：思考内容已暗色打印后再遇到可重试错误，若重试会把同一段思考重复打印；
        // 应直接失败上抛（调用方捕获 ProviderException 提示），而不是重发请求
        var provider = new ReasoningThenFailProvider();
        var agent = MakeAgent(provider);
        await Assert.ThrowsAsync<ProviderException>(() => agent.RunAsync("x", CancellationToken.None));
        Assert.Equal(1, provider.StreamCalls); // 只调用一次：没有自动重试
    }
}
