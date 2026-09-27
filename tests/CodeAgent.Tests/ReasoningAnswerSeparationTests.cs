using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Providers;
using CodeAgent.Tools;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 思考内容与正文之间的换行。
///
/// 此前思考（暗色）与正文**共用一行**：
///   The user asks: … No file edits needed.CodeAgent 是一个用 C#/.NET 写的…
/// 有颜色时只是"暗色/正常"的细微差别；<b>无颜色或重定向到文件时两者完全无法区分</b>
/// ——转存下来的会话记录里，模型的思考过程和最终答案混成一段。
/// </summary>
[Collection("ConsoleOutput")]
public sealed class ReasoningAnswerSeparationTests
{
    private sealed class ReasoningThenTextProvider : IAgentProvider
    {
        public Task<ProviderResponse> ChatAsync(IReadOnlyList<ProviderMessage> messages, IReadOnlyList<ToolSpec> tools, string thinkingEffort, CancellationToken ct)
            => Task.FromResult(new ProviderResponse { Text = "答案" });

        public Task<ProviderResponse> ChatStreamAsync(
            IReadOnlyList<ProviderMessage> messages, IReadOnlyList<ToolSpec> tools, string thinkingEffort,
            Action<string>? onText, Action<string>? onReasoning, Action<string>? onToolFragment, CancellationToken ct)
        {
            onReasoning?.Invoke("模型在思考。");
            onText?.Invoke("最终答案");
            return Task.FromResult(new ProviderResponse { Text = "最终答案" });
        }

        public Task<ProviderResponse> CompactAsync(IReadOnlyList<ProviderMessage> messages, string? focus, CancellationToken ct)
            => Task.FromResult(new ProviderResponse { Text = "摘要" });
        public string Name => "fake";
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private static string Capture(Func<System.Threading.Tasks.Task> act)
    {
        var original = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try { act().GetAwaiter().GetResult(); }
        finally { Console.SetOut(original); }
        return sw.ToString().Replace("\r\n", "\n");
    }

    [Fact]
    public void AnswerStartsOnItsOwnLine()
    {
        var outp = Capture(async () =>
        {
            var agent = new AgentClass(
                new AgentConfig { SaveSessions = false, StreamOutput = true },
                new ReasoningThenTextProvider(),
                ToolRegistry.CreateDefault());
            await agent.RunAsync("任务", CancellationToken.None);
        });

        Assert.Contains("模型在思考。", outp);
        Assert.Contains("最终答案", outp);
        // 核心回归：思考与正文此前粘在同一行
        Assert.DoesNotContain("模型在思考。最终答案", outp);
        var reasoningAt = outp.IndexOf("模型在思考。", StringComparison.Ordinal);
        var answerAt = outp.IndexOf("最终答案", StringComparison.Ordinal);
        Assert.True(reasoningAt >= 0 && answerAt > reasoningAt, "正文应在思考之后");
        var between = outp[reasoningAt..answerAt];
        Assert.Contains("\n", between);
    }

    [Fact]
    public void BlankReasoningDoesNotAddAStrayNewline()
    {
        // 空白思考增量不该触发换行，否则每次空增量都在正文前多空一行
        var agent = new AgentClass(
            new AgentConfig { SaveSessions = false, StreamOutput = true },
            new BlankReasoningProvider(),
            ToolRegistry.CreateDefault());
        var outp = Capture(async () => { await agent.RunAsync("任务", CancellationToken.None); });
        Assert.Contains("最终答案", outp);
        Assert.DoesNotContain("\n\n最终答案", outp);
    }

    private sealed class BlankReasoningProvider : IAgentProvider
    {
        public Task<ProviderResponse> ChatAsync(IReadOnlyList<ProviderMessage> messages, IReadOnlyList<ToolSpec> tools, string thinkingEffort, CancellationToken ct)
            => Task.FromResult(new ProviderResponse { Text = "答案" });

        public Task<ProviderResponse> ChatStreamAsync(
            IReadOnlyList<ProviderMessage> messages, IReadOnlyList<ToolSpec> tools, string thinkingEffort,
            Action<string>? onText, Action<string>? onReasoning, Action<string>? onToolFragment, CancellationToken ct)
        {
            onReasoning?.Invoke("   ");
            onText?.Invoke("最终答案");
            return Task.FromResult(new ProviderResponse { Text = "最终答案" });
        }

        public Task<ProviderResponse> CompactAsync(IReadOnlyList<ProviderMessage> messages, string? focus, CancellationToken ct)
            => Task.FromResult(new ProviderResponse { Text = "摘要" });
        public string Name => "fake";
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    [Fact]
    public void TheSeparatorIsClearedSoItIsNotEmittedTwice()
    {
        // 换行后必须把 _reasoningShown 复位，否则"只思考没正文"分支会再补一个换行
        var src = File.ReadAllText(Find());
        Assert.Contains("_reasoningShown = false; // 行已断开，别让后面再补一次", src);
    }

    private static string Find()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = Path.Combine(dir, "src", "CodeAgent", "Agent", "Agent.cs");
                if (File.Exists(f) && new FileInfo(f).Length > 1000)
                    return f;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/Agent/Agent.cs");
    }
}
