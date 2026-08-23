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
        new AgentConfig
        {
            SaveSessions = false,
            SessionDir = SessionDir,
            AllowCommands = allowCommands,
            // 显式设有限上限：FakeProvider 循环返回同一工具调用时靠它截断（生产默认 0=无限，
            // 依赖默认值的测试会在 write_file 被拦截等不置 StopRequested 的场景无限循环）
            MaxToolIterations = 5,
        },
        provider,
        ToolRegistry.CreateDefault());

    [Fact]
    public void SessionOnlySystemPrompt_IsUsedForCodeMode_AndNotSerialized()
    {
        // 回归：ADOFAI 等注入的上下文曾直接改 config.SystemPrompt，
        // /model、/thinking、/access 等命令保存配置时会把注入内容永久写进用户的 codeagent.json
        var config = new AgentConfig
        {
            SaveSessions = false,
            SessionDir = SessionDir,
            SessionOnlySystemPrompt = "注入的会话级提示",
        };
        var agent = new AgentClass(config, new FakeProvider(), ToolRegistry.CreateDefault());

        // code 模式生效的是会话级注入提示
        Assert.Equal("注入的会话级提示", agent.CurrentSystemPrompt);

        // 切到别的模式再切回 code：会话级提示仍然生效
        agent.SetMode(CodeAgent.Modes.Find("review", config));
        agent.SetMode(CodeAgent.Modes.Find("code", config));
        Assert.Equal("注入的会话级提示", agent.CurrentSystemPrompt);

        // 序列化只包含原始 SystemPrompt（SessionOnlySystemPrompt 是 JsonIgnore）
        var path = Path.Combine(_dir, "roundtrip.json");
        AgentConfig.Save(config, path);
        var json = File.ReadAllText(path);
        Assert.DoesNotContain("注入的会话级提示", json);
        Assert.Contains("systemPrompt", json); // 原字段仍在
    }

    [Fact]
    public void SessionOnlySystemPrompt_Null_FallsBackToConfiguredPrompt()
    {
        var config = new AgentConfig { SaveSessions = false, SessionDir = SessionDir, SystemPrompt = "自定义提示" };
        var agent = new AgentClass(config, new FakeProvider(), ToolRegistry.CreateDefault());
        Assert.Equal("自定义提示", agent.CurrentSystemPrompt);
    }

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
    public async Task ReadOnlyMode_BlocksWriteToolExecution()
    {
        // 回归：plan 等只读模式下，即使模型强行调用 write_file，执行层也必须拦截
        var provider = new FakeProvider
        {
            NextResponse = new ProviderResponse
            {
                ToolCalls =
                [
                    new ToolCall { Id = "w1", Name = "write_file", ArgumentsJson = """{"path":"x.txt","content":"x"}""" },
                ],
            },
        };
        var agent = MakeAgent(provider);
        agent.SetMode(CodeAgent.Modes.Find("plan", new AgentConfig()));

        var result = await agent.RunAsync("尝试写入", CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(SessionDir, "x.txt"))); // 文件未被写入
        Assert.Contains(agent.Messages, m => m.Role == MessageRole.Tool
            && m.Content != null && m.Content.Contains("不可用")); // 工具结果标记为被拦截
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
    public async Task UndoLastTurn_NoPriorTurn_ReturnsNull()
    {
        var provider = new FakeProvider { NextResponse = new ProviderResponse { Text = "ok" } };
        var agent = MakeAgent(provider);

        // 从未运行过回合：没有可撤回的轮次
        Assert.Null(agent.UndoLastTurn());
    }

    [Fact]
    public async Task Reset_WithCustomSystemPrompt_KeepsIt()
    {
        // 回归：code 模式配置了自定义 systemPrompt 时，/clear（Reset）不应丢失它
        var custom = "我是自定义系统提示";
        var provider = new FakeProvider { NextResponse = new ProviderResponse { Text = "ok" } };
        var agent = new AgentClass(
            new AgentConfig { SaveSessions = false, SessionDir = SessionDir, SystemPrompt = custom },
            provider,
            ToolRegistry.CreateDefault());
        await agent.RunAsync("一些对话", CancellationToken.None);

        agent.Reset();

        Assert.Equal(1, agent.MessageCount);
        Assert.Contains(custom, agent.Messages[0].Content!); // 自定义提示在 Reset 后保留
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

    [Fact]
    public async Task RunAsync_MaxToolIterations_StopsRunawayLoop()
    {
        // 回归：模型一直返回工具调用时应被 MaxToolIterations 截断，而不是无限循环。
        // 用会报错但不置位 StopRequested 的 read_file（路径不存在），确保循环不被提前结束。
        var provider = new FakeProvider
        {
            NextResponse = new ProviderResponse
            {
                ToolCalls = [new ToolCall { Id = "loop", Name = "read_file", ArgumentsJson = """{"path":"no-such-file"}""" }],
            },
        };
        var config = new AgentConfig
        {
            SaveSessions = false,
            SessionDir = SessionDir,
            MaxToolIterations = 3, // 故意设小
        };
        var agent = new AgentClass(config, provider, ToolRegistry.CreateDefault());

        var result = await agent.RunAsync("一直调用工具", CancellationToken.None);

        Assert.Contains("最大工具调用轮数", result); // 达到上限给出提示
        Assert.True(agent.ProviderCalls <= 4, $"不应超过上限太多（实际调用 {agent.ProviderCalls} 次）");
        Assert.True(agent.LastTurnFailed, "达到轮数上限应标记为失败（REPL 显示 ⚠、一次性模式退出码非 0）");
    }

    [Fact]
    public async Task RunAsync_SameFileDifferentPathSpellings_SerializesWrites()
    {
        // 回归：write_file("same.txt") 与 edit_file("./same.txt") 文本不同但指向同一文件，
        // 曾被判为无冲突而并行执行，造成丢失更新（edit 竞争读不到 write 的结果）。
        // 冲突判定是纯逻辑（DetectWriteConflict），这里直接验证归一化后能识别同一文件
        ToolCall[] calls =
        [
            new() { Id = "w1", Name = "write_file", ArgumentsJson = """{"path":"same.txt","content":"x"}""" },
            new() { Id = "e1", Name = "edit_file", ArgumentsJson = """{"path":"./same.txt","old_string":"a","new_string":"b"}""" },
        ];
        string Resolve(string p) => Path.GetFullPath(Path.Combine(_dir, p));
        Assert.True(AgentClass.DetectWriteConflict(calls, Resolve));

        // 大小写与子目录回溯也算同一文件（Windows 大小写不敏感）
        ToolCall[] caseCalls =
        [
            new() { Id = "w2", Name = "write_file", ArgumentsJson = """{"path":"Same.TXT","content":"x"}""" },
            new() { Id = "e2", Name = "edit_file", ArgumentsJson = """{"path":"sub/../same.txt","old_string":"a","new_string":"b"}""" },
        ];
        Assert.True(AgentClass.DetectWriteConflict(caseCalls, Resolve));

        // 不同文件、以及不含写操作的批次：无冲突
        ToolCall[] noConflict =
        [
            new() { Id = "w3", Name = "write_file", ArgumentsJson = """{"path":"a.txt","content":"x"}""" },
            new() { Id = "e3", Name = "edit_file", ArgumentsJson = """{"path":"b.txt","old_string":"a","new_string":"b"}""" },
            new() { Id = "r1", Name = "read_file", ArgumentsJson = """{"path":"a.txt"}""" },
        ];
        Assert.False(AgentClass.DetectWriteConflict(noConflict, Resolve));
        // 生产路径归一化本身：同一 Agent 实例下不同写法解析到同一绝对路径
        var agent = new AgentClass(
            new AgentConfig { SaveSessions = false }, new FakeProvider(),
            ToolRegistry.CreateDefault(), workingDirectory: _dir);
        var a1 = agent.ResolveForConflict("same.txt");
        var a2 = agent.ResolveForConflict("./sub/../Same.txt");
        Assert.Equal(a1, a2, StringComparer.OrdinalIgnoreCase);
    }
}
