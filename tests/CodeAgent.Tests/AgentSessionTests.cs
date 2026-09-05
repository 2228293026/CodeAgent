using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Providers;
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

    private static AgentClass MakeAgent(string sessionDir, out string sessionPath, FakeProvider? provider = null)
    {
        var config = new AgentConfig
        {
            SaveSessions = false, // 测试不写 jsonl 日志
            SessionDir = sessionDir, // 绝对路径：与工作目录无关
            MaxToolIterations = 5,  // 防止无限制循环：FakeProvider 返回 ToolCall 时依赖此限
        };
        provider ??= new FakeProvider { NextResponse = new Providers.ProviderResponse { Text = "ok" } };
        var agent = new AgentClass(config, provider, ToolRegistry.CreateDefault());
        sessionPath = Path.Combine(sessionDir, "s.json");
        return agent;
    }

    [Fact]
    public void SearchSessionLog_FindsKeyword_IgnoresCase_FoldsNewlines()
    {
        var path = Path.Combine(_sessionDir, "search.jsonl");
        File.WriteAllLines(path,
        [
            """{"ts":"10:00:00","role":"user","content":"帮我修复登录bug"}""",
            """{"ts":"10:00:01","role":"assistant","content":"好的，\n我先看看 Program.cs"}""",
            """{"ts":"10:00:02","role":"user","content":"另一个话题"}""",
            "{ 损坏行 ",
        ]);

        var hits = AgentClass.SearchSessionLog(path, "登录");

        Assert.Single(hits);
        Assert.Equal("user", hits[0].Role);
        Assert.Contains("登录bug", hits[0].Snippet);
    }

    [Fact]
    public void SearchSessionLog_MultiLineContent_SnippetFolded()
    {
        var path = Path.Combine(_sessionDir, "search2.jsonl");
        File.WriteAllLines(path,
        [
            """{"ts":"10:00:00","role":"assistant","content":"第一行\n关键词在这里\n第三行"}""",
        ]);

        var hits = AgentClass.SearchSessionLog(path, "关键词");

        Assert.Single(hits);
        Assert.Contains("⏎", hits[0].Snippet); // 换行折叠为可见标记
        Assert.DoesNotContain("\n", hits[0].Snippet);
    }

    [Fact]
    public void SearchSessionLog_CapsHits_AndNoMatch_ReturnsEmpty()
    {
        var path = Path.Combine(_sessionDir, "search3.jsonl");
        File.WriteAllLines(path,
        [
            """{"role":"user","content":"hit one fix"}""",
            """{"role":"user","content":"hit two fix"}""",
            """{"role":"user","content":"hit three fix"}""",
            """{"role":"user","content":"hit four fix"}""",
        ]);

        Assert.Equal(3, AgentClass.SearchSessionLog(path, "fix").Count); // maxHits 默认 3
        Assert.Empty(AgentClass.SearchSessionLog(path, "不存在"));
        Assert.Empty(AgentClass.SearchSessionLog(Path.Combine(_sessionDir, "missing.jsonl"), "fix"));
    }

    [Fact]
    public void SearchSessionLog_LongContent_SnippetWindowedWithEllipsis()
    {
        var path = Path.Combine(_sessionDir, "search4.jsonl");
        var longContent = new string('前', 100) + "目标词" + new string('后', 100);
        File.WriteAllLines(path, ["{\"role\":\"user\",\"content\":\"" + longContent + "\"}"]);

        var hits = AgentClass.SearchSessionLog(path, "目标词");

        Assert.Single(hits);
        Assert.StartsWith("…", hits[0].Snippet); // 命中点前有内容：窗口前省略号
        Assert.EndsWith("…", hits[0].Snippet);
    }

    [Fact]
    public async Task ThinkingFields_SurviveSessionLog_Roundtrip()
    {
        // 回归：Anthropic thinking（文本+签名）必须随会话日志落盘并恢复，
        // 否则 --continue 后下一轮工具调用请求缺失 thinking 块被 API 400
        var provider = new FakeProvider
        {
            NextResponse = new Providers.ProviderResponse
            {
                Text = "ok",
                ThinkingText = "推理过程",
                ThinkingSignature = "sig-1",
            },
        };
        var config = new AgentConfig { SaveSessions = true, SessionDir = _sessionDir };
        var agent = new AgentClass(config, provider, ToolRegistry.CreateDefault());
        await agent.RunAsync("你好", CancellationToken.None);

        // 内存中的 assistant 消息带上了 thinking 字段
        var asst = agent.Messages.Single(m => m.Role == MessageRole.Assistant);
        Assert.Equal("推理过程", asst.ThinkingText);
        Assert.Equal("sig-1", asst.ThinkingSignature);
        agent.Close();

        var log = Directory.GetFiles(_sessionDir, "*.jsonl").Single();
        var restored = AgentClass.ReadSessionLogFile(log);
        var rAsst = restored.Single(m => m.Role == MessageRole.Assistant);
        Assert.Equal("推理过程", rAsst.ThinkingText);   // 日志往返后仍在
        Assert.Equal("sig-1", rAsst.ThinkingSignature);
    }

    [Fact]
    public void SearchSessionLog_SkipsSlashCommandUserLines()
    {
        // 回归：斜杠命令行（/model gpt 等）是操作记录不是对话内容，
        // /find 命中它们纯属噪音
        var path = Path.Combine(_sessionDir, "search5.jsonl");
        File.WriteAllLines(path,
        [
            """{"role":"user","content":"/model gpt-4o"}""",
            """{"role":"assistant","content":"已切换模型 gpt-4o"}""",
            """{"role":"user","content":"帮我看看 model 层的设计"}""",
        ]);

        var hits = AgentClass.SearchSessionLog(path, "model");

        Assert.Single(hits); // 只命中 assistant 回复与真实用户输入，跳过命令行
    }

    [Fact]
    public void SearchSnapshot_FindsKeyword_SkipsSlashCommands()
    {
        // /find 的快照来源：/save 的 .json 也能搜；斜杠命令行同口径跳过
        var path = Path.Combine(_sessionDir, "snap-search.json");
        File.WriteAllText(path,
            """
            [
              {"role":"user","content":"/model gpt-4o"},
              {"role":"assistant","content":"已切换到 gpt-4o 模型"},
              {"role":"user","content":"讲讲 model 层设计"}
            ]
            """);

        // 命令行（/model gpt-4o）被跳过：搜 "gpt-4o" 只命中 assistant 回复
        Assert.Single(AgentClass.SearchSnapshot(path, "gpt-4o"));
        Assert.Equal("assistant", AgentClass.SearchSnapshot(path, "gpt-4o")[0].Role);
        // 真实用户输入正常命中
        Assert.Single(AgentClass.SearchSnapshot(path, "model"));
        Assert.Equal("user", AgentClass.SearchSnapshot(path, "model")[0].Role);
    }

    [Fact]
    public async Task Reset_ZeroesTurnStats()
    {
        // /clear 后状态栏不应残留上一回合的 token 统计
        var provider = new FakeProvider { NextResponse = new Providers.ProviderResponse { Text = "ok", InputTokens = 100, OutputTokens = 50 } };
        var agent = new AgentClass(new AgentConfig { SaveSessions = false, SessionDir = _sessionDir }, provider, ToolRegistry.CreateDefault());
        await agent.RunAsync("hi", CancellationToken.None);
        Assert.True(agent.TurnInputTokens > 0);

        agent.Reset();

        Assert.Equal(0, agent.TurnInputTokens);
        Assert.Equal(0, agent.TurnOutputTokens);
        Assert.Equal(0, agent.TurnToolCalls);
        Assert.Equal(0, agent.TurnRounds);
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
    public async Task LoadSession_DoesNotPreserveLastTurnFailed()
    {
        // 回归：/resume 加载的会话不应残留上一回合的失败状态（状态栏红标误导）
        var provider = new FakeProvider
        {
            NextResponse = new ProviderResponse
            {
                ToolCalls = [new ToolCall { Id = "l", Name = "read_file", ArgumentsJson = """{"path":"no-such"}""" }],
            },
        };
        var agent = MakeAgent(_sessionDir, out var path, provider);
        await agent.RunAsync("触发", CancellationToken.None);
        Assert.True(agent.LastTurnFailed);
        agent.SaveSession("s");

        var restored = MakeAgent(_sessionDir, out _);
        restored.LoadSession("s");
        Assert.False(restored.LastTurnFailed);
    }

    [Fact]
    public async Task LoadSession_ResetsTurnCounters()
    {
        // 加载的会话在首轮前不应残留上一会话的回合 token/耗时（/stats 等显示误导）
        var agent = MakeAgent(_sessionDir, out var path);
        await agent.RunAsync("你好", CancellationToken.None);
        agent.SaveSession("s");
        Assert.True(agent.TurnRounds > 0);

        var restored = MakeAgent(_sessionDir, out _);
        restored.LoadSession("s");
        Assert.Equal(0, restored.TurnRounds);
        Assert.Equal(0, restored.TurnToolCalls);
        Assert.Equal(0, restored.TurnInputTokens);
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
        // / 在 Windows 与 Linux/macOS 上都是非法文件名分隔符，跨平台可断言
        var agent = MakeAgent(_sessionDir, out _);
        agent.SaveSession("a/b");
        Assert.True(File.Exists(Path.Combine(_sessionDir, "a_b.json")));
    }

    [Fact]
    public async Task SaveLoadSession_RoundTripsToolCallMessages()
    {
        // 工具调用消息的 DTO 字段（toolCallId/toolName/isError/toolCalls）也应完整往返
        var provider = new FakeProvider
        {
            NextResponse = new Providers.ProviderResponse
            {
                ToolCalls =
                [
                    new Providers.ToolCall { Id = "call_1", Name = "stop", ArgumentsJson = """{"reason":"x"}""" },
                ],
            },
        };
        var agent = MakeAgent(_sessionDir, out _, provider);
        await agent.RunAsync("任务", CancellationToken.None);

        agent.SaveSession("tools");
        var restored = MakeAgent(_sessionDir, out _);
        restored.LoadSession("tools");

        // 找到 assistant 的工具调用与对应的 tool 结果，校验映射未丢失
        var asst = restored.Messages.First(m => m.ToolCalls is { Count: > 0 });
        Assert.Equal("call_1", asst.ToolCalls![0].Id);
        Assert.Equal("stop", asst.ToolCalls[0].Name);

        var toolMsg = restored.Messages.First(m => m.Role == MessageRole.Tool);
        Assert.Equal("call_1", toolMsg.ToolCallId);
        Assert.Equal("stop", toolMsg.ToolName);
    }

    [Fact]
    public void ExportMarkdown_RespectsConfiguredExportDir()
    {
        // 回归：导出目录曾硬编码 .codeagent/exports；现在应使用 config.ExportDir
        var exportDir = Path.Combine(Path.GetTempPath(), "codeagent-export-" + Guid.NewGuid().ToString("N"), "out");
        var config = new AgentConfig
        {
            SaveSessions = false,
            SessionDir = _sessionDir,
            ExportDir = Path.GetRelativePath(Environment.CurrentDirectory, exportDir),
        };
        var agent = new AgentClass(config, new FakeProvider(), ToolRegistry.CreateDefault());

        var file = agent.ExportMarkdown(null);
        Assert.True(File.Exists(file));
        Assert.StartsWith(exportDir, Path.GetFullPath(file));
        Assert.Contains("CodeAgent 会话", File.ReadAllText(file));
    }

    [Fact]
    public async Task ExportMarkdown_ContainsConversationSections()
    {
        // 导出内容应包含用户/助手段落与工具调用行，方便回看
        var provider = new FakeProvider
        {
            NextResponse = new Providers.ProviderResponse
            {
                ToolCalls =
                [
                    new Providers.ToolCall { Id = "call_1", Name = "stop", ArgumentsJson = """{"reason":"done"}""" },
                ],
            },
        };
        var exportDir = Path.Combine(Path.GetTempPath(), "codeagent-export-ctx-" + Guid.NewGuid().ToString("N"), "out");
        var config = new AgentConfig
        {
            SaveSessions = false,
            SessionDir = _sessionDir,
            ExportDir = Path.GetRelativePath(Environment.CurrentDirectory, exportDir),
        };
        var agent = new AgentClass(config, provider, ToolRegistry.CreateDefault());
        await agent.RunAsync("帮我看看项目", CancellationToken.None);

        var file = agent.ExportMarkdown(null);
        var text = File.ReadAllText(file);

        Assert.Contains("## 用户", text);
        Assert.Contains("帮我看看项目", text); // 用户消息内容
        Assert.Contains("## 助手", text);       // 助手段落
        Assert.Contains("## 工具：stop", text); // 工具结果段落
        Assert.Contains("调用工具 `stop`", text); // 工具调用行
    }

    [Fact]
    public async Task ExportMarkdown_ByName_LoadsSavedSession()
    {
        // /export <名>：从磁盘读取命名会话再导出，标题应带会话名
        var provider = new FakeProvider { NextResponse = new Providers.ProviderResponse { Text = "你好" } };
        var exportDir = Path.Combine(Path.GetTempPath(), "codeagent-export-name-" + Guid.NewGuid().ToString("N"), "out");
        var config = new AgentConfig
        {
            SaveSessions = false,
            SessionDir = _sessionDir,
            ExportDir = Path.GetRelativePath(Environment.CurrentDirectory, exportDir),
        };
        var agent = new AgentClass(config, provider, ToolRegistry.CreateDefault());
        await agent.RunAsync("任务", CancellationToken.None);
        agent.SaveSession("named-session");

        var file = agent.ExportMarkdown("named-session");
        var text = File.ReadAllText(file);

        Assert.True(File.Exists(file));
        Assert.Contains("CodeAgent 会话：named-session", text); // 标题带会话名
        Assert.Contains("## 用户", text);
        Assert.Contains("任务", text);
        Assert.Contains("## 助手", text);
        Assert.Contains("你好", text);
    }

    [Fact]
    public void ExportMarkdown_UnknownName_ThrowsFileNotFound()
    {
        var exportDir = Path.Combine(Path.GetTempPath(), "codeagent-export-miss-" + Guid.NewGuid().ToString("N"), "out");
        var config = new AgentConfig
        {
            SaveSessions = false,
            SessionDir = _sessionDir,
            ExportDir = Path.GetRelativePath(Environment.CurrentDirectory, exportDir),
        };
        var agent = new AgentClass(config, new FakeProvider(), ToolRegistry.CreateDefault());

        Assert.Throws<FileNotFoundException>(() => agent.ExportMarkdown("no-such-session"));
    }

    [Fact]
    public void ExportMarkdown_TraversalName_StaysInsideExportDir()
    {
        // 回归：/export ../evil 曾把 name 直接拼进导出路径（写入 ExportDir 父目录，路径穿越）；
        // 现在导出名与保存名一样经 sanitize（../evil → .._evil.md），文件必须落在 ExportDir 内
        var exportDir = Path.Combine(Path.GetTempPath(), "codeagent-export-trav-" + Guid.NewGuid().ToString("N"), "out");
        var config = new AgentConfig
        {
            SaveSessions = false,
            SessionDir = _sessionDir,
            ExportDir = Path.GetRelativePath(Environment.CurrentDirectory, exportDir),
        };
        var agent = new AgentClass(config, new FakeProvider(), ToolRegistry.CreateDefault());

        // 先保存一个「穿越名」会话（SaveSession 走 SessionFilePath 的 sanitize）
        agent.SaveSession("../evil");
        var file = agent.ExportMarkdown("../evil");

        var full = Path.GetFullPath(file);
        Assert.StartsWith(Path.GetFullPath(exportDir), full); // 导出文件在 ExportDir 内
        Assert.EndsWith(".._evil.md", Path.GetFileName(file)); // 名字已 sanitize
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(exportDir)!, "evil.md")),
            "不得在 ExportDir 父目录写出 evil.md（路径穿越）");
    }

    [Fact]
    public async Task LoadSession_ResetsUndoStart()
    {
        // 回归：/load 曾不重置 LastTurnStartCount，加载会话后空输入按 ESC 会按过期索引
        // RemoveRange 删掉刚加载的消息；现在加载后没有「上一轮」可撤回
        var agent = MakeAgent(_sessionDir, out var path);
        await agent.RunAsync("第一轮", CancellationToken.None);
        await agent.RunAsync("第二轮", CancellationToken.None);
        var saved = agent.MessageCount;
        agent.SaveSession("multi");

        var restored = MakeAgent(_sessionDir, out _);
        restored.LoadSession("multi");
        Assert.Equal(saved, restored.MessageCount);

        Assert.Null(restored.UndoLastTurn()); // 加载的会话无「上一轮」可撤回
        Assert.Equal(saved, restored.MessageCount); // 消息未被误删
    }

    [Fact]
    public async Task Reset_ResetsUndoStart()
    {
        // 回归：/clear 曾不重置 LastTurnStartCount；现在清空后 ESC 撤回应是无操作
        var agent = MakeAgent(_sessionDir, out _);
        await agent.RunAsync("你好", CancellationToken.None);
        Assert.NotNull(agent.UndoLastTurn()); // 清空前可撤回

        agent.Reset();
        Assert.Null(agent.UndoLastTurn()); // 清空后无「上一轮」可撤回
        Assert.Equal(1, agent.MessageCount); // 仅剩 system
    }
}
