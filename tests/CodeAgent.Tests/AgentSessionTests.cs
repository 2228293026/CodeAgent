using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

public class AgentSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-session-" + Guid.NewGuid().ToString("N"));
    private readonly string _sessionDir = Path.Combine(Path.GetTempPath(), "codeagent-session-" + Guid.NewGuid().ToString("N"), ".codeagent", "sessions");

    public AgentSessionTests() => Directory.CreateDirectory(_sessionDir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
        try { Directory.Delete(Path.GetDirectoryName(_sessionDir)!, true); } catch { /* 忽略 */ }
    }

    private static AgentClass MakeAgent(string sessionDir, out string sessionPath)
    {
        var config = new AgentConfig
        {
            SaveSessions = false, // 测试不写 jsonl 日志
            SessionDir = sessionDir, // 绝对路径：与工作目录无关
        };
        var provider = new FakeProvider { NextResponse = new Providers.ProviderResponse { Text = "ok" } };
        var agent = new AgentClass(config, provider, ToolRegistry.CreateDefault());
        sessionPath = Path.Combine(sessionDir, "s.json");
        return agent;
    }

    [Fact]
    public async Task SaveLoadSession_RoundTripsMessages()
    {
        var agent = MakeAgent(_sessionDir, out var path);
        await agent.RunAsync("你好", CancellationToken.None);
        Assert.Equal(3, agent.MessageCount); // system + user + assistant

        agent.SaveSession("s");
        Assert.True(File.Exists(path));

        // 新 Agent 从磁盘恢复，消息应一致
        var restored = MakeAgent(_sessionDir, out _);
        restored.LoadSession("s");
        Assert.Equal(3, restored.MessageCount);
        Assert.Equal("你好", restored.Messages[1].Content);
        Assert.Equal("ok", restored.Messages[2].Content);
    }

    [Fact]
    public void LoadSession_MissingName_Throws()
    {
        var agent = MakeAgent(_sessionDir, out _);
        Assert.Throws<FileNotFoundException>(() => agent.LoadSession("nope"));
    }

    [Fact]
    public void SaveSession_SanitizesInvalidFileNameChars()
    {
        var agent = MakeAgent(_sessionDir, out _);
        agent.SaveSession("a/b:c*");
        Assert.True(File.Exists(Path.Combine(_sessionDir, "a_b_c_.json")));
    }
}
