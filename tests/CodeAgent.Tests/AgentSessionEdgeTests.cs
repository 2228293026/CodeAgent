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

    /// <summary>开启会话日志的 Agent（.jsonl 自动落盘），用于 --continue / /resume 恢复路径。</summary>
    private AgentClass MakeLoggedAgent(FakeProvider provider) => new(
        new AgentConfig
        {
            SaveSessions = true,
            SessionDir = SessionDir,
            ExportDir = ExportDir,
            MaxToolIterations = 5,
        },
        provider,
        ToolRegistry.CreateDefault());

    [Fact]
    public async Task SessionLog_RoundTrip_RestoresConversation()
    {
        // --continue 的基础：每条消息自动写 .jsonl，另一个 Agent 实例可完整恢复对话
        var provider = new FakeProvider { NextResponse = new ProviderResponse { Text = "回复甲" } };
        var agent = MakeLoggedAgent(provider);
        await agent.RunAsync("第一轮", CancellationToken.None);
        provider.NextResponse = new ProviderResponse { Text = "回复乙" };
        await agent.RunAsync("第二轮", CancellationToken.None);
        agent.Close();
        Assert.NotNull(agent.SessionPath);

        var restored = MakeLoggedAgent(new FakeProvider { NextResponse = new ProviderResponse { Text = "ok" } });
        Assert.True(restored.LoadSessionLog(agent.SessionPath!));
        Assert.Equal(agent.MessageCount, restored.MessageCount);
        Assert.Contains(restored.Messages, m => m.Role == MessageRole.User && m.Content == "第一轮");
        Assert.Contains(restored.Messages, m => m.Role == MessageRole.Assistant && m.Content == "回复乙");
        // 恢复的会话没有「上一轮」可撤回
        Assert.Null(restored.UndoLastTurn());
    }

    [Fact]
    public async Task SessionLog_Resume_RolledToNewLogFile()
    {
        // 恢复后滚动新日志：新文件自包含（再次恢复不依赖旧文件），旧文件保持原样
        var agent = MakeLoggedAgent(new FakeProvider { NextResponse = new ProviderResponse { Text = "ok" } });
        await agent.RunAsync("对话", CancellationToken.None);
        agent.Close();
        var firstPath = agent.SessionPath!;

        var restored = MakeLoggedAgent(new FakeProvider { NextResponse = new ProviderResponse { Text = "ok" } });
        Assert.True(restored.LoadSessionLog(firstPath));
        Assert.NotEqual(firstPath, restored.SessionPath);

        // 新日志可再次恢复（自包含）
        var again = MakeLoggedAgent(new FakeProvider { NextResponse = new ProviderResponse { Text = "ok" } });
        restored.Close();
        Assert.True(again.LoadSessionLog(restored.SessionPath!));
        Assert.Contains(again.Messages, m => m.Content == "对话");
    }

    [Fact]
    public async Task Reset_RollsToNewSessionLog()
    {
        // /clear 后新开日志文件：--continue 恢复最近会话不会带回已清空的历史
        var agent = MakeLoggedAgent(new FakeProvider { NextResponse = new ProviderResponse { Text = "ok" } });
        await agent.RunAsync("旧对话", CancellationToken.None);
        var firstPath = agent.SessionPath!;

        agent.Reset();
        Assert.NotEqual(firstPath, agent.SessionPath);

        agent.Close();
        var after = MakeLoggedAgent(new FakeProvider { NextResponse = new ProviderResponse { Text = "ok" } });
        Assert.True(after.LoadSessionLog(agent.SessionPath!));
        Assert.DoesNotContain(after.Messages, m => m.Content == "旧对话"); // 新日志不含清空前内容
    }

    [Fact]
    public void LoadSessionLog_MissingFile_ReturnsFalse() =>
        Assert.False(MakeAgent(new FakeProvider()).LoadSessionLog(Path.Combine(SessionDir, "nope.jsonl")));

    [Fact]
    public void LoadSessionLog_DropsTrailingIncompleteToolRound()
    {
        // 回归：ESC 取消回合时 assistant(toolCalls) 已写日志、结果没写全；带着孤儿 tool_calls
        // 恢复会让下次请求被 API 拒绝（tool_calls 必须跟结果）。恢复时从尾丢弃未完成的工具轮
        var path = Path.Combine(SessionDir, "cancelled.jsonl");
        File.WriteAllLines(path,
        [
            """{"ts":"10:00:00","role":"user","tool":null,"toolCallId":null,"content":"任务","toolCalls":null,"error":false}""",
            """{"ts":"10:00:01","role":"assistant","tool":null,"toolCallId":null,"content":null,"toolCalls":[{"Id":"c1","Name":"read_file","ArgumentsJson":"{\"path\":\"a\"}"}],"error":false}""",
            """{"ts":"10:00:02","role":"tool","tool":"read_file","toolCallId":"c1","content":"结果","toolCalls":null,"error":false}""",
        ]);
        var agent = MakeAgent(new FakeProvider());

        Assert.True(agent.LoadSessionLog(path));
        // 剩 system（补插）+ user：未完成的 assistant+tool 轮被整体丢弃
        Assert.Equal(2, agent.MessageCount);
        Assert.Equal(MessageRole.User, agent.Messages[^1].Role);
    }

    [Fact]
    public void RecentSessionLogs_SkipsEmptyLogs_AndOrdersNewestFirst()
    {
        // 回归：启动后未对话就退出会留下 0 字节日志；曾混进 /resume 列表与 --continue 的
        // 「最近一次」，恢复时报「文件可能损坏」的误导错误
        var empty = Path.Combine(SessionDir, "20260817-100000.jsonl");
        File.WriteAllText(empty, ""); // 空日志：应被跳过
        var older = Path.Combine(SessionDir, "20260817-090000.jsonl");
        File.WriteAllText(older, "{\"role\":\"user\",\"content\":\"a\"}\n");
        var newer = Path.Combine(SessionDir, "20260817-110000.jsonl");
        File.WriteAllText(newer, "{\"role\":\"user\",\"content\":\"b\"}\n");
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-5));

        var logs = Program.RecentSessionLogs(new AgentConfig { SessionDir = SessionDir });
        Assert.Equal([newer, older], logs); // 空文件被跳过，新的在前
        Assert.Equal(newer, Program.LatestSessionLog(new AgentConfig { SessionDir = SessionDir }));
    }

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
    [Fact]
    public async Task LoadSession_RollsSessionLog()
    {
        // 回归：/load 曾不滚动日志——加载命名快照后 --continue 恢复的是旧对话
        AgentClass Logged(FakeProvider p) => new(new AgentConfig
        {
            SaveSessions = true,
            SessionDir = SessionDir,
            ExportDir = ExportDir,
            MaxToolIterations = 5,
        }, p, ToolRegistry.CreateDefault());
        var agent = Logged(new FakeProvider { NextResponse = new ProviderResponse { Text = "旧对话内容" } });
        await agent.RunAsync("旧问题", CancellationToken.None);
        agent.SaveSession("snap");
        agent.Close(); // 释放日志句柄，避免与 agent2 同目录滚动时的共享冲突

        var agent2 = Logged(new FakeProvider { NextResponse = new ProviderResponse { Text = "回复" } });
        await agent2.RunAsync("新问题", CancellationToken.None); // agent2 当前日志 = 新问题
        agent2.LoadSession("snap");
        agent2.Close();

        // --continue 读取最新日志：应包含加载的「旧对话内容」
        var latest = System.Linq.Enumerable.Single(Program.RecentSessionLogs(new AgentConfig { SessionDir = SessionDir }, 1));
        Assert.Contains("旧对话内容", File.ReadAllText(latest)); // 中文明文落盘
    }


    [Fact]
    public void SessionLogSummary_FirstUserMessage_AsPreview()
    {
        // /resume 列表摘要：文件名只是时间戳认不出会话，预览取首条用户消息 + 统计条数
        var log = Path.Combine(SessionDir, "20260819-120000.jsonl");
        File.WriteAllLines(log,
        [
            """{"ts":"12:00:00","role":"system","tool":null,"toolCallId":null,"content":"系统提示","toolCalls":null,"error":false}""",
            """{"ts":"12:00:01","role":"user","tool":null,"toolCallId":null,"content":"帮我写一个 README","toolCalls":null,"error":false}""",
            """{"ts":"12:00:05","role":"assistant","tool":null,"toolCallId":null,"content":"好的","toolCalls":null,"error":false}""",
        ]);

        var (preview, count) = AgentClass.SessionLogSummary(log);
        Assert.Equal("帮我写一个 README", preview);
        Assert.Equal(3, count);
    }

    [Fact]
    public void SessionLogSummary_MultilineUser_PreviewFolded()
    {
        // 多行用户输入折叠为 ⏎ 单行预览（列表不能被换行打乱）
        var log = Path.Combine(SessionDir, "20260819-120100.jsonl");
        File.WriteAllLines(log,
        [
            """{"ts":"12:01:00","role":"system","tool":null,"toolCallId":null,"content":"s","toolCalls":null,"error":false}""",
            "{\"ts\":\"12:01:01\",\"role\":\"user\",\"tool\":null,\"toolCallId\":null,\"content\":\"第一行\\n第二行\",\"toolCalls\":null,\"error\":false}",
        ]);

        var (preview, count) = AgentClass.SessionLogSummary(log);
        Assert.Equal("第一行 ⏎ 第二行", preview);
        Assert.Equal(2, count);
    }

    [Fact]
    public void SessionLogSummary_NoUserOrNullContent_ReturnsNullPreview()
    {
        // 无用户消息（如只有 system）或 user content 为 null：预览为 null，条数照常
        var log = Path.Combine(SessionDir, "20260819-120200.jsonl");
        File.WriteAllLines(log,
        [
            """{"ts":"12:02:00","role":"system","tool":null,"toolCallId":null,"content":"s","toolCalls":null,"error":false}""",
            """{"ts":"12:02:01","role":"assistant","tool":null,"toolCallId":null,"content":null,"toolCalls":[],"error":false}""",
        ]);

        var (preview, count) = AgentClass.SessionLogSummary(log);
        Assert.Null(preview);
        Assert.Equal(2, count);

        Assert.Equal((string?)null, AgentClass.SessionLogSummary(Path.Combine(SessionDir, "nope.jsonl")).Preview);
    }
}
