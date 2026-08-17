using System;
using System.IO;
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
    public async Task UndoLastTurn_Consecutive_RemovesBothTurns()
    {
        var provider = new FakeProvider { NextResponse = new ProviderResponse { Text = "ok" } };
        var agent = MakeAgent(provider);
        await agent.RunAsync("一", CancellationToken.None);
        await agent.RunAsync("二", CancellationToken.None);
        Assert.Equal(5, agent.MessageCount); // system + 2×(user+assistant)

        Assert.NotNull(agent.UndoLastTurn());   // 撤回第二轮
        Assert.Equal(3, agent.MessageCount);
        // LastTurnStartCount 只记录最近一轮起点：连续第二次撤回返回 null（ESC 语义为撤回最近一轮）
        Assert.Null(agent.UndoLastTurn());
        Assert.Equal(3, agent.MessageCount);    // 第一轮保留
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
}
