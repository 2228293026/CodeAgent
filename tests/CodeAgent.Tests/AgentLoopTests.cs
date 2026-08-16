using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Providers;
using CodeAgent.Tools;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

public class AgentLoopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-loop-" + Guid.NewGuid().ToString("N"));
    private string SessionDir => Path.Combine(_dir, ".codeagent", "sessions");

    public AgentLoopTests() => Directory.CreateDirectory(SessionDir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
    }

    private AgentClass MakeAgent(FakeProvider provider, bool allowCommands = true) => new(
        new AgentConfig { SaveSessions = false, SessionDir = SessionDir, AllowCommands = allowCommands },
        provider,
        ToolRegistry.CreateDefault());

    [Fact]
    public async Task RunAsync_ExecutesStopToolAndEndsTurn()
    {
        // 模型返回 stop 工具调用 → Agent 执行工具、置位 StopRequested、结束本轮
        var provider = new FakeProvider
        {
            NextResponse = new ProviderResponse
            {
                ToolCalls =
                [
                    new ToolCall { Id = "t1", Name = "stop", ArgumentsJson = """{"reason":"done"}""" },
                ],
            },
        };
        var agent = MakeAgent(provider);

        var result = await agent.RunAsync("开始", CancellationToken.None);

        Assert.Contains("stop", result);
        Assert.True(agent.Context.StopRequested);
        // system + user + assistant(tool call) + tool 结果
        Assert.True(agent.MessageCount >= 4);
    }

    [Fact]
    public async Task RunAsync_FinalText_IsReturned()
    {
        var provider = new FakeProvider { NextResponse = new ProviderResponse { Text = "完成！" } };
        var agent = MakeAgent(provider);

        var result = await agent.RunAsync("任务", CancellationToken.None);

        Assert.Equal("完成！", result);
        Assert.False(agent.LastTurnFailed);
        Assert.Equal(3, agent.MessageCount); // system + user + assistant
    }

    [Fact]
    public async Task UndoLastTurn_RemovesLastRound()
    {
        var provider = new FakeProvider { NextResponse = new ProviderResponse { Text = "ok" } };
        var agent = MakeAgent(provider);
        await agent.RunAsync("第一轮", CancellationToken.None);
        Assert.Equal(3, agent.MessageCount);

        provider.NextResponse = new ProviderResponse { Text = "第二轮回复" };
        await agent.RunAsync("第二轮", CancellationToken.None);
        Assert.Equal(5, agent.MessageCount);

        var desc = agent.UndoLastTurn();
        Assert.NotNull(desc);
        Assert.Equal(3, agent.MessageCount); // 撤回第二轮，回到第一轮后的状态
    }

    [Fact]
    public async Task Reset_ClearsHistoryKeepingSystem()
    {
        var provider = new FakeProvider { NextResponse = new ProviderResponse { Text = "ok" } };
        var agent = MakeAgent(provider);
        await agent.RunAsync("一些对话", CancellationToken.None);
        Assert.Equal(3, agent.MessageCount);

        agent.Reset();

        Assert.Equal(1, agent.MessageCount); // 仅剩 system
        Assert.Equal(MessageRole.System, agent.Messages[0].Role);
    }
}
