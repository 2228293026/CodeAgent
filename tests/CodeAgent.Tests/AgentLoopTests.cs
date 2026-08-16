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

    [Fact]
    public async Task SetMode_SwitchesSystemPromptAndToolScope()
    {
        var provider = new FakeProvider { NextResponse = new ProviderResponse { Text = "ok" } };
        var agent = MakeAgent(provider);

        agent.SetMode(CodeAgent.Modes.Find("plan", new AgentConfig()));

        Assert.Equal("plan", agent.CurrentMode.Name);
        Assert.Contains("PLAN mode", agent.Messages[0].Content!); // 系统提示已切换
        var tools = agent.ToolsForMode();
        Assert.Contains(tools, t => t.Name == "read_file");
        Assert.DoesNotContain(tools, t => t.Name == "write_file"); // 只读模式隐藏写工具
        Assert.DoesNotContain(tools, t => t.Name == "run_command");
    }

    [Fact]
    public async Task SetMode_CustomSystemPrompt_SurvivesModeRoundTrip()
    {
        // 回归：code 模式使用配置的自定义 systemPrompt，切走再切回不应丢失
        var custom = "我是自定义系统提示";
        var provider = new FakeProvider { NextResponse = new ProviderResponse { Text = "ok" } };
        var agent = new AgentClass(
            new AgentConfig { SaveSessions = false, SessionDir = SessionDir, SystemPrompt = custom },
            provider,
            ToolRegistry.CreateDefault());

        Assert.Contains(custom, agent.Messages[0].Content!); // 初始用自定义提示
        agent.SetMode(CodeAgent.Modes.Find("plan", new AgentConfig()));
        Assert.Contains("PLAN mode", agent.Messages[0].Content!); // 切到 plan
        agent.SetMode(CodeAgent.Modes.Find("code", new AgentConfig()));
        Assert.Contains(custom, agent.Messages[0].Content!); // 切回 code 恢复自定义提示
    }
}
