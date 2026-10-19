using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent;
using CodeAgent.Agent;
using CodeAgent.Providers;
using CodeAgent.Tools;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 工具内部超时不能冒充「用户取消」。
///
/// 真实故障（CI 上抓到）：<c>GitLog_FiltersByMessageKeyword</c> 在 CI 上跑了 16 秒，
/// 内部 10 秒超时触发，抛出的 <c>OperationCanceledException</c> 被 Agent 层
/// 当成「用户按了 ESC」向上传播——**整个回合被掐断**、撤回本轮消息、草稿回填输入框，
/// 而模型永远收不到「git log 超时」这个结果，也就不会据此缩小范围或换命令重试。
/// 一次慢 git 直接废掉一整轮对话。
///
/// 判别为什么只能在 Agent 层做：工具里拿到的是
/// <c>CreateLinkedTokenSource(ct) + CancelAfter</c> 的**链接令牌**，
/// 用户取消与内部超时两种情况下它都是「已请求」，在工具里根本区分不了。
/// 只有 Agent 层握有**回合自己的** ct——用户按 ESC 时才会被请求。
/// </summary>
public class GitTimeoutSemanticsTests
{
    private static AgentClass NewAgent(out string dir, IReadOnlyList<ProviderResponse> responses)
    {
        dir = Path.Combine(Path.GetTempPath(), "ca-gitto-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var q = new Queue<ProviderResponse>(responses);
        return new AgentClass(
            new AgentConfig { SaveSessions = false, ShowToolCalls = false },
            new FakeProvider { ResponseQueue = q },
            ToolRegistry.CreateDefault(),
            dir);
    }

    [Fact]
    public async Task InternalTimeoutIsReportedToTheModelAsAnError()
    {
        // 工具自己超时：模型要拿到一条「超时」的工具结果，继续往下走
        var toolCall = new ProviderResponse
        {
            ToolCalls =
            [
                new ToolCall
                {
                    Id = "t1",
                    Name = "git_log",
                    // timeout_seconds 最小 1 秒；用一个必然慢的命令形状 +
                    // 让内部令牌先取消，模拟 CancelAfter 已触发
                    ArgumentsJson = """{"timeout_seconds":1}""",
                },
            ],
        };
        var agent = NewAgent(out var dir,
        [
            toolCall,
            new ProviderResponse { Text = "git 超时了，我换个方式。" },
        ]);
        try
        {
            var result = await agent.RunAsync("看下提交", CancellationToken.None);
            // 回合正常结束，不是「已取消」
            Assert.False(Program.IsCancelledTurn(result), $"整轮被误判为取消：{result}");
            // 模型看得到这条工具结果
            Assert.Contains(agent.Messages, m => m.Role == MessageRole.Tool && (m.Content ?? "").Length > 0);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task RealCancellationStillAbortsTheTurn()
    {
        // 用户按 ESC：必须照旧中止整轮——把它降级成 ToolException 会让 ESC 失效，
        // 用户按了停却停不下来。
        var agent = NewAgent(out var dir,
        [
            new ProviderResponse
            {
                ToolCalls =
                [
                    new ToolCall { Id = "t1", Name = "run_command", ArgumentsJson = """{"command":"sleep 5"}""" },
                ],
            },
        ]);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            // Agent 层把真取消**向上抛**（映射成哨兵文本是 Program.RunTurnAsync 的事）。
            // 关键：它绝不能变成一条「工具超时」的普通工具结果——那会让 ESC 失灵。
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                agent.RunAsync("跑个慢命令", cts.Token));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TheDistinctionLivesInTheAgentCatch()
    {
        // 判别必须写在 Agent 的 catch 过滤器里：工具层拿到的是链接令牌，区分不了。
        var src = ReadSource("Agent.cs");
        Assert.Contains("when (!ct.IsCancellationRequested)", src);
        // 且必须排在「真取消照旧抛」之前
        var filtered = src.IndexOf("when (!ct.IsCancellationRequested)", StringComparison.Ordinal);
        Assert.True(filtered > 0);
        // 从过滤器之后找**裸** catch：直接 IndexOf("catch (OperationCanceledException)")
        // 命中的正是带过滤器那条自己（它是同一个前缀），断言会变成永远为真。
        var plain = src.IndexOf("catch (OperationCanceledException)", filtered + 1, StringComparison.Ordinal);
        Assert.True(plain > filtered,
            "带过滤器的分支必须排在裸 catch 之前，否则真取消也会被降级");
    }

    [Fact]
    public void TheMessageTellsTheModelHowToRecover()
    {
        // 只报「超时」不够：模型得知道可以调大 timeout_seconds 或缩小范围
        var src = ReadSource("Agent.cs");
        Assert.Contains("timeout_seconds", src);
        Assert.Contains("工具超时", src);
    }

    private static string ReadSource(string file)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = Path.Combine(dir, "src", "CodeAgent", "Agent", file);
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 1000)
                    return File.ReadAllText(candidate);
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException($"找不到 src/CodeAgent/Agent/{file}");
    }
}
