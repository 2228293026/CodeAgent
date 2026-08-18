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

public class AgentTrimHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-trim-" + Guid.NewGuid().ToString("N"));
    private string SessionDir => Path.Combine(_dir, ".codeagent", "sessions");

    public AgentTrimHistoryTests() => Directory.CreateDirectory(SessionDir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
    }

    private AgentClass MakeAgent(FakeProvider provider, int maxHistoryChars) => new(
        new AgentConfig
        {
            SaveSessions = false,
            SessionDir = SessionDir,
            MaxHistoryChars = maxHistoryChars,
            MaxToolIterations = 10,
        },
        provider,
        ToolRegistry.CreateDefault());

    [Fact]
    public async Task LongConversation_IsTrimmedToHistoryLimit()
    {
        // 历史超限时应被裁剪：system 保留、总字符量收敛到上限附近
        var provider = new FakeProvider
        {
            NextResponse = new ProviderResponse { Text = new string('x', 2000) }, // 每次回复很大
        };
        var agent = MakeAgent(provider, maxHistoryChars: 3000);

        for (int i = 0; i < 6; i++)
            await agent.RunAsync($"第 {i} 轮请求", CancellationToken.None);

        var totalChars = agent.Messages.Sum(m => m.Content?.Length ?? 0);
        Assert.True(agent.MessageCount < 13, $"历史应被裁剪（当前 {agent.MessageCount} 条）");
        Assert.Equal(MessageRole.System, agent.Messages[0].Role); // system 保留
        // 未裁剪时 6 轮 × 2000 字符 ≈ 12000+；应收敛到远低于该值
        Assert.True(totalChars < 10000, $"总字符量应收敛（当前 {totalChars}）");
    }

    [Fact]
    public async Task TrimmedHistory_KeepsFirstUserAnchored()
    {
        // FailSummarization=true：LLM 摘要失败 → 走兜底裁剪路径，该路径保留 system 与首条 user
        var provider = new FakeProvider
        {
            FailSummarization = true,
            NextResponse = new ProviderResponse { Text = new string('y', 1500) },
        };
        var agent = MakeAgent(provider, maxHistoryChars: 2000);

        await agent.RunAsync("最初的请求", CancellationToken.None);
        for (int i = 0; i < 8; i++)
            await agent.RunAsync($"后续请求 {i}", CancellationToken.None);

        // 首条 user 消息应保留（锚点），后续内容被裁剪
        Assert.Contains(agent.Messages, m => m.Role == MessageRole.User && m.Content == "最初的请求");
        Assert.True(agent.MessageCount >= 3, "至少保留 system + 首条 user + 至少一条回复");
    }

    [Fact]
    public async Task ToolCallMessages_SurviveTrimming()
    {
        // 工具调用轮（assistant 带 tool_calls + tool 结果）在裁剪后不应出现「孤儿」tool 结果
        var provider = new FakeProvider
        {
            NextResponse = new ProviderResponse
            {
                Text = new string('z', 1200), // 先占满历史
                ToolCalls = [new ToolCall { Id = "c1", Name = "stop", ArgumentsJson = "{}" }],
            },
        };
        var agent = MakeAgent(provider, maxHistoryChars: 1500);

        // 第一轮返回大文本（触发裁剪路径），第二轮带工具调用
        await agent.RunAsync("第一轮", CancellationToken.None);
        provider.NextResponse = new ProviderResponse
        {
            ToolCalls = [new ToolCall { Id = "c2", Name = "stop", ArgumentsJson = "{}" }],
        };
        await agent.RunAsync("第二轮", CancellationToken.None);

        // 裁剪后：每个 Tool 结果都应有对应的 assistant tool_calls 在其之前（无孤儿）
        for (int i = 1; i < agent.Messages.Count; i++)
        {
            if (agent.Messages[i].Role == MessageRole.Tool)
            {
                Assert.True(agent.Messages[i - 1].Role == MessageRole.Assistant
                            && agent.Messages[i - 1].ToolCalls is { Count: > 0 },
                    "tool 结果前应有带 tool_calls 的 assistant 消息（无孤儿 tool 结果）");
            }
        }
    }

    [Fact]
    public async Task Compact_OnEmptyHistory_ReturnsFalse()
    {
        // 回归：/clear 后直接 /compact（只剩 system 一条）曾因 GetRange 越界抛异常，
        // 用户看到的是堆栈信息而非友好的「对话过短」提示
        var agent = MakeAgent(new FakeProvider(), maxHistoryChars: 100_000);
        agent.Reset();
        Assert.False(await agent.CompactAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Compact_MultipleTurns_ReplacesEarlyHistoryWithSummary()
    {
        var provider = new FakeProvider { NextResponse = new ProviderResponse { Text = "ok" } };
        var agent = MakeAgent(provider, maxHistoryChars: 100_000);
        for (int i = 0; i < 6; i++)
            await agent.RunAsync($"第 {i} 轮请求", CancellationToken.None);
        var before = agent.MessageCount;

        provider.NextResponse = new ProviderResponse { Text = "这是摘要" }; // 摘要请求（单条 user）的回复
        Assert.True(await agent.CompactAsync(CancellationToken.None));

        // 最早的对话被替换为一条 system 摘要；整体消息数减少，首条原始 user 不再保留
        Assert.True(agent.MessageCount < before, $"压缩应减少消息数（{before} → {agent.MessageCount}）");
        Assert.Contains(agent.Messages, m => m.Role == MessageRole.System && (m.Content ?? "").Contains("这是摘要"));
        Assert.DoesNotContain(agent.Messages, m => m.Content == "第 0 轮请求");
    }

    [Fact]
    public async Task UndoAfterTrimming_RemovesOnlyTheLastTurn()
    {
        // 回归：历史裁剪移除最早消息后，ESC 撤回（UndoLastTurn）曾按过期的 LastTurnStartCount
        // 定位，可能删错消息；现在撤回索引随裁剪前移，应只删除本轮消息。
        // FailSummarization=true 强制走兜底裁剪路径（该路径保留首条 user 锚点），
        // 正是 LastTurnStartCount 需要随 RemoveAt 前移的路径。
        var provider = new FakeProvider
        {
            FailSummarization = true,
            NextResponse = new ProviderResponse { Text = new string('y', 1500) }, // 每轮都触发裁剪
        };
        var agent = MakeAgent(provider, maxHistoryChars: 2000);

        for (int i = 0; i < 8; i++)
            await agent.RunAsync($"第 {i} 轮", CancellationToken.None);

        var beforeUndo = agent.MessageCount;
        // 撤回后消息数应回到本轮起点（起点随裁剪前移后定位准确）
        var expectedAfterUndo = agent.LastTurnStartCount;
        var desc = agent.UndoLastTurn();
        Assert.NotNull(desc);
        Assert.Equal(expectedAfterUndo, agent.MessageCount);
        Assert.True(agent.MessageCount < beforeUndo, "撤回应减少消息数");

        // 多级撤回：继续撤上一轮，同样回到其（已随裁剪前移的）起点
        var expectedSecond = agent.LastTurnStartCount;
        var desc2 = agent.UndoLastTurn();
        Assert.NotNull(desc2);
        Assert.Equal(expectedSecond, agent.MessageCount);
        // 兜底路径保留首条 user 锚点
        Assert.Contains(agent.Messages, m => m.Role == MessageRole.User && m.Content == "第 0 轮");
    }

    [Fact]
    public async Task UndoAfterSummarization_StaysConsistent()
    {
        // 多级撤回在历史被 LLM 压缩后仍保持一致：起点随压缩前移/丢弃（不可越过压缩点），
        // 反复撤回最终回到 null，且绝不删穿 system
        var provider = new FakeProvider { NextResponse = new ProviderResponse { Text = new string('y', 1500) } };
        var agent = MakeAgent(provider, maxHistoryChars: 2000);
        for (int i = 0; i < 8; i++)
            await agent.RunAsync($"第 {i} 轮请求", CancellationToken.None);

        int guard = 0;
        while (agent.UndoLastTurn() is not null)
        {
            Assert.True(++guard < 20, "撤回应逐层耗尽，不得无限成功");
            Assert.Equal(MessageRole.System, agent.Messages[0].Role);
            Assert.True(agent.MessageCount >= 1, "不得删穿 system");
        }
        Assert.Null(agent.UndoLastTurn()); // 最终无轮可撤
    }
    [Fact]
    public void PruneSessionLogs_KeepsNewest_AndSkipsCurrent()
    {
        var dir = Path.Combine(_dir, "sess");
        Directory.CreateDirectory(dir);
        for (int i = 1; i <= 5; i++)
            File.WriteAllText(Path.Combine(dir, $"20260101-00000{i}.jsonl"), "{}");

        var deleted = AgentClass.PruneSessionLogs(dir, keep: 3,
            exceptPath: Path.Combine(dir, "20260101-000001.jsonl")); // 最旧的恰是「当前」日志

        Assert.Equal(1, deleted); // 只删掉 000002（000001 受保护，000004/5 保留凑满 keep+1）
        Assert.True(File.Exists(Path.Combine(dir, "20260101-000001.jsonl")));
        Assert.False(File.Exists(Path.Combine(dir, "20260101-000002.jsonl")));
        Assert.True(File.Exists(Path.Combine(dir, "20260101-000005.jsonl")));
    }

    [Fact]
    public void PruneSessionLogs_ZeroKeep_Disabled()
    {
        var dir = Path.Combine(_dir, "sess0");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.jsonl"), "{}");
        Assert.Equal(0, AgentClass.PruneSessionLogs(dir, keep: 0));
        Assert.True(File.Exists(Path.Combine(dir, "a.jsonl")));
    }

}
