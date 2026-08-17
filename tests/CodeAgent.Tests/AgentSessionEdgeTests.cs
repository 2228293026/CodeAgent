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

/// <summary>Agent 会话保存/加载/导出与路径安全的边界测试(补充 AgentSessionTests)。</summary>
public class AgentSessionEdgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-session-" + Guid.NewGuid().ToString("N"));
    private string SessionDir => Path.Combine(_dir, ".codeagent", "sessions");
    private string ExportDir => Path.Combine(_dir, ".codeagent", "exports");

    public AgentSessionEdgeTests() => Directory.CreateDirectory(SessionDir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
    }

    private AgentClass MakeAgent(FakeProvider provider) => new(
        new AgentConfig
        {
            SaveSessions = false,
            SessionDir = SessionDir,
            ExportDir = ExportDir,
            MaxToolIterations = 5,
        },
        provider,
        ToolRegistry.CreateDefault());

    [Fact]
    public async Task SaveSession_CreatesJsonFile()
    {
        var agent = MakeAgent(new FakeProvider { NextResponse = new ProviderResponse { Text = "ok" } });
        await agent.RunAsync("你好", CancellationToken.None);

        agent.SaveSession("snap1");
        Assert.True(File.Exists(Path.Combine(SessionDir, "snap1.json")));
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsMessages()
    {
        var provider = new FakeProvider { NextResponse = new ProviderResponse { Text = "ok" } };
        var agent = MakeAgent(provider);
        await agent.RunAsync("第一轮", CancellationToken.None);
        var before = agent.MessageCount;

        agent.SaveSession("rt");
        var agent2 = MakeAgent(provider);
        agent2.LoadSession("rt");
        Assert.Equal(before, agent2.MessageCount);
        Assert.Equal("第一轮", agent2.Messages[1].Content); // user 消息恢复
    }

    [Fact]
    public void LoadSession_Missing_ThrowsFileNotFound()
    {
        var agent = MakeAgent(new FakeProvider());
        Assert.Throws<FileNotFoundException>(() => agent.LoadSession("nope"));
    }

    [Fact]
    public async Task LoadSession_ResetsUndoAnchor()
    {
        var provider = new FakeProvider { NextResponse = new ProviderResponse { Text = "ok" } };
        var agent = MakeAgent(provider);
        await agent.RunAsync("x", CancellationToken.None);
        agent.SaveSession("anchor");

        var agent2 = MakeAgent(provider);
        agent2.LoadSession("anchor");
        // 加载后 ESC 撤回不应按过期索引删掉刚加载的消息：撤回应返回 null（无「上一轮」）
        Assert.Null(agent2.UndoLastTurn());
        Assert.True(agent2.MessageCount >= 3);
    }

    [Fact]
    public async Task ExportMarkdown_ContainsRolesAndContent()
    {
        var agent = MakeAgent(new FakeProvider { NextResponse = new ProviderResponse { Text = "完成" } });
        await agent.RunAsync("任务说明", CancellationToken.None);

        var file = agent.ExportMarkdown(null);
        Assert.True(File.Exists(file));
        var text = File.ReadAllText(file);
        Assert.Contains("## 用户", text);
        Assert.Contains("任务说明", text);
        Assert.Contains("## 助手", text);
        Assert.Contains("完成", text);
    }

    [Fact]
    public async Task ExportMarkdown_ToolCall_IsListed()
    {
        var provider = new FakeProvider
        {
            NextResponse = new ProviderResponse
            {
                ToolCalls = [new ToolCall { Id = "t", Name = "stop", ArgumentsJson = """{"reason":"done"}""" }],
            },
        };
        var agent = MakeAgent(provider);
        await agent.RunAsync("调用工具", CancellationToken.None);

        var text = File.ReadAllText(agent.ExportMarkdown(null));
        Assert.Contains("调用工具 `stop`", text);
    }

    [Fact]
    public async Task SaveSession_PathTraversalName_IsSanitized()
    {
        // 回归：会话名含 ../ 时 '/' 被替换为 _（防目录穿越），文件仍落在会话目录内
        var agent = MakeAgent(new FakeProvider { NextResponse = new ProviderResponse { Text = "ok" } });
        await agent.RunAsync("x", CancellationToken.None);

        agent.SaveSession("../evil");
        var files = Directory.GetFiles(SessionDir, "*.json");
        var file = Assert.Single(files);
        Assert.Equal(".._evil.json", Path.GetFileName(file)); // ../ → .._（斜杠被 sanitize）
    }

    [Fact]
    public async Task ExportMarkdown_MissingNamedSession_Throws()
    {
        // 导出不存在的命名会话：明确报错而非写入异常路径
        var agent = MakeAgent(new FakeProvider { NextResponse = new ProviderResponse { Text = "ok" } });
        await agent.RunAsync("x", CancellationToken.None);

        Assert.Throws<FileNotFoundException>(() => agent.ExportMarkdown("no-such-session"));
    }

    [Fact]
    public async Task SaveSession_InvalidName_IsSanitized()
    {
        var agent = MakeAgent(new FakeProvider { NextResponse = new ProviderResponse { Text = "ok" } });
        await agent.RunAsync("x", CancellationToken.None);

        agent.SaveSession("a/b:?*");
        // 只验证：至少生成了某个 .json 且没有目录穿越（a/ 不被当成子目录）
        Assert.Single(Directory.GetFiles(SessionDir, "*.json"));
        Assert.False(Directory.Exists(Path.Combine(SessionDir, "a")));
    }
}
