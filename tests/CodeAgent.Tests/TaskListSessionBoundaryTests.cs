using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeAgent.Tools;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 任务清单与**会话切换**的边界。
///
/// <see cref="AgentClass.Reset"/> 里逐条清掉"新会话不应残留上一轮状态"（token、耗时、
/// 上一次提问、失败标记、流式标记）。任务清单是同一类状态，但当时它还不存在，
/// 所以没人往那份清单里加它——于是 /clear 之后 /tasks 仍显示上一段对话的待办。
/// 一份过期的清单看起来像新对话还有一堆活儿，而模型根本不会去接着做。
/// </summary>
public sealed class TaskListSessionBoundaryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-tl-" + Guid.NewGuid().ToString("N"));

    public TaskListSessionBoundaryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static AgentClass Make() => new(
        new AgentConfig { SaveSessions = false, SessionDir = "sessions" },
        new FakeProvider(),
        ToolRegistry.CreateDefault());

    private static void Fill(AgentClass agent)
    {
        agent.Tasks.Replace(
        [
            new AgentTask("上一轮的任务甲", TaskStatus.Completed),
            new AgentTask("上一轮的任务乙", TaskStatus.InProgress),
        ]);
    }

    [Fact]
    public void Clear_DropsTheTaskList()
    {
        var agent = Make();
        Fill(agent);
        Assert.Equal(2, agent.Tasks.Count);
        agent.Reset();
        Assert.True(agent.Tasks.IsEmpty);
    }

    [Fact]
    public void Clear_IsIdempotent()
    {
        var agent = Make();
        agent.Reset();
        agent.Reset();
        Assert.True(agent.Tasks.IsEmpty);
    }

    [Fact]
    public void LoadSession_DropsTheTaskList()
    {
        // 加载的是别人的历史，清单是**当前这次工作**的进度——留着只会误导
        var agent = Make();
        Fill(agent);
        var name = "snap1";
        agent.SaveSession(name);
        Fill(agent);
        agent.LoadSession(name);
        Assert.True(agent.Tasks.IsEmpty);
    }

    [Fact]
    public void LoadSessionLog_DropsTheTaskList()
    {
        var agent = Make();
        Fill(agent);
        var log = Path.Combine(_dir, "s.jsonl");
        System.IO.File.WriteAllText(log, System.Text.Json.JsonSerializer.Serialize(new
        {
            role = "user",
            content = "之前在做什么",
        }));
        Assert.True(agent.LoadSessionLog(log));
        Assert.True(agent.Tasks.IsEmpty);
    }

    [Fact]
    public void Reset_StillClearsEverythingElse()
    {
        // 前提没被破坏：加一行 Tasks.Clear() 不能影响原有的清理项
        var agent = Make();
        Fill(agent);
        agent.Reset();
        Assert.Equal(0, agent.TurnRounds);
        Assert.Equal(0, agent.TurnInputTokens);
        Assert.Equal(0, agent.TurnOutputTokens);
        Assert.Null(agent.LastPrompt);
        Assert.False(agent.LastTurnFailed);
    }

    [Fact]
    public async System.Threading.Tasks.Task Compact_KeepsTheTaskList()
    {
        // /compact 是压缩历史、**对话继续**——模型可能确实还在做那些任务。
        // 清掉它等于让模型在自己的压缩历史里丢掉了工作计划。
        // 行为测试而不是源码 grep：CompactAsync 只是一行委托，grep 方法体读不到东西。
        var provider = new FakeProvider { NextResponse = new CodeAgent.Providers.ProviderResponse { Text = "ok" } };
        var agent = new AgentClass(
            new AgentConfig { SaveSessions = false, MaxHistoryChars = 100_000 },
            provider,
            ToolRegistry.CreateDefault());
        for (var i = 0; i < 6; i++)
            await agent.RunAsync($"第 {i} 轮请求", System.Threading.CancellationToken.None);
        Fill(agent);
        provider.NextResponse = new CodeAgent.Providers.ProviderResponse { Text = "这是摘要" };
        Assert.True(await agent.CompactAsync(System.Threading.CancellationToken.None));
        Assert.Equal(2, agent.Tasks.Count);
        Assert.Equal("上一轮的任务甲", agent.Tasks.Tasks[0].Content);
    }
    [Fact]
    public void EverySessionSwitchPath_ClearsTheTaskList()
    {
        // 守卫：新增一种"换对话"的入口时，这条会要求一并处理清单
        var reset = File.ReadAllText(Path.Combine(SrcRoot(), "Agent", "Agent.cs"));
        var session = File.ReadAllText(Path.Combine(SrcRoot(), "Agent", "Agent.Session.cs"));
        Assert.True(Count(reset, "Tasks.Clear()") == 1, "Reset() 里应恰好清一次任务清单");
        Assert.True(Count(session, "Tasks.Clear()") == 2, "LoadSession 与 LoadSessionLog 各应清一次");
    }

    private static int Count(string haystack, string needle)
    {
        var n = 0;
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (string.CompareOrdinal(haystack, i, needle, 0, needle.Length) == 0)
                n++;
        return n;
    }

    private static string Find() => Path.Combine(SrcRoot(), "Agent", "Agent.Session.cs");

    private static string SrcRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var d = Path.Combine(dir, "src", "CodeAgent");
                if (Directory.Exists(d) && new FileInfo(Path.Combine(d, "Program.cs")).Length > 1000)
                    return d;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new DirectoryNotFoundException("找不到 src/CodeAgent");
    }
}
