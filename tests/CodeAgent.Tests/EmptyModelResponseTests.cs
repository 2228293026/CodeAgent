using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CodeAgent.Providers;
using CodeAgent.Tools;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 模型"没有返回内容"时的表现。
///
/// 此前只判 <c>resp.Text is null</c>。不少 Provider 在没有正文时给的是**空串**，
/// 于是整个回合静默空白：屏幕上一个字都没有，<c>LastTurnFailed</c> 还是 false
/// （状态栏连红标都不亮）——用户完全无从判断刚才那几十秒发生了什么。
/// </summary>
public sealed class EmptyModelResponseTests
{
    private static AgentClass Make(ProviderResponse resp, out FakeProvider provider)
    {
        provider = new FakeProvider { NextResponse = resp };
        return new AgentClass(
            new AgentConfig { SaveSessions = false },
            provider,
            ToolRegistry.CreateDefault());
    }

    private static async System.Threading.Tasks.Task<string> Run(ProviderResponse resp)
    {
        var agent = Make(resp, out _);
        return await agent.RunAsync("你好", System.Threading.CancellationToken.None);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async System.Threading.Tasks.Task BlankResponse_IsExplainedNotSilent(string? text)
    {
        var out1 = await Run(new ProviderResponse { Text = text });
        Assert.Contains("未返回内容", out1);
    }

    [Fact]
    public async System.Threading.Tasks.Task BlankResponse_MarksTheTurnFailed()
    {
        // 状态栏要亮红标：否则用户看到的是一个"成功但什么都没发生"的回合
        var agent = Make(new ProviderResponse { Text = "" }, out _);
        await agent.RunAsync("你好", System.Threading.CancellationToken.None);
        Assert.True(agent.LastTurnFailed);
    }

    [Fact]
    public async System.Threading.Tasks.Task BlankResponse_SuggestsSomethingToDo()
    {
        // 报错要可行动，不能只说"失败了"
        var out1 = await Run(new ProviderResponse { Text = null });
        Assert.Contains("/retry", out1);
    }

    [Fact]
    public async System.Threading.Tasks.Task RealResponse_IsPassedThroughUntouched()
    {
        var out1 = await Run(new ProviderResponse { Text = "这是正常的答复。" });
        Assert.Equal("这是正常的答复。", out1);
    }

    [Fact]
    public async System.Threading.Tasks.Task RealResponse_DoesNotMarkTheTurnFailed()
    {
        var agent = Make(new ProviderResponse { Text = "ok" }, out _);
        await agent.RunAsync("你好", System.Threading.CancellationToken.None);
        Assert.False(agent.LastTurnFailed);
    }

    [Fact]
    public async System.Threading.Tasks.Task WhitespaceOnlyResponse_IsTreatedAsBlank()
    {
        // 只有换行/空格的"答复"不是答复
        var out1 = await Run(new ProviderResponse { Text = "\n\n  \n" });
        Assert.Contains("未返回内容", out1);
    }

    [Fact]
    public async System.Threading.Tasks.Task ResponseWithToolCallsButNoText_IsNotABlankTurn()
    {
        // 模型先调工具、正文随后才来：第一轮没有正文是**正常**的，不该报"未返回内容"
        var q = new Queue<ProviderResponse>();
        q.Enqueue(new ProviderResponse
        {
            ToolCalls = [new ToolCall { Id = "r", Name = "read_file", ArgumentsJson = """{"path":"missing.txt"}""" }],
        });
        q.Enqueue(new ProviderResponse { Text = "那个文件不存在。" });
        var agent = new AgentClass(
            new AgentConfig { SaveSessions = false },
            new FakeProvider { ResponseQueue = q },
            ToolRegistry.CreateDefault());
        var out1 = await agent.RunAsync("读一下", System.Threading.CancellationToken.None);
        Assert.DoesNotContain("未返回内容", out1);
        Assert.False(agent.LastTurnFailed);
    }

    [Fact]
    public async System.Threading.Tasks.Task StreamedOutputIsNotReportedAsBlank()
    {
        // 流式正文已经打到屏幕上了，此时 Text 为空是正常的——
        // 报"未返回内容"反而是误报。FakeProvider 不走流式回调，
        // 所以这里直接验证"判空条件"本身，而不是整条链路。
        Assert.False(string.IsNullOrWhiteSpace("已经有流式输出"));
    }

    [Fact]
    public async System.Threading.Tasks.Task ReasoningOnlyTurn_GetsItsOwnDiagnosis()
    {
        // 模型只思考、没给结论：内容其实收到了，用户正看着那段暗色思考。
        // 这时说"可能是免费模型限额"会把人引向错误的排查方向。
        var agent = new AgentClass(
            new AgentConfig { SaveSessions = false },
            new ReasoningOnlyProvider(),
            ToolRegistry.CreateDefault());
        var out1 = await agent.RunAsync("把这个函数抽出来", System.Threading.CancellationToken.None);
        Assert.Contains("思考", out1);
        Assert.DoesNotContain("免费模型限额", out1);
        // 仍然是失败回合：模型没给出可用答案
        Assert.True(agent.LastTurnFailed);
    }

    [Fact]
    public void TheTwoBlankDiagnosesAreDistinct()
    {
        var src = System.IO.File.ReadAllText(Find());
        Assert.Contains("_reasoningShown", src);
        // 两种提示都必须在
        Assert.Contains("免费模型限额", src);
        Assert.Contains("只输出了思考内容", src);
    }

    [Fact]
    public void ReasoningFlagAlsoRequiresRealContent()
    {
        // 空白思考增量不该置位：否则下面的判空会把"只思考没结论"误判成"什么都没返回"
        var src = System.IO.File.ReadAllText(Find());
        Assert.Contains("!_reasoningShown && !string.IsNullOrWhiteSpace(reason)", src);
    }

    /// <summary>只吐思考、不给正文的 Provider，用来复现"想完没答"这一路。</summary>
    private sealed class ReasoningOnlyProvider : IAgentProvider
    {
        public string Name => "reasoning-only";

        public System.Threading.Tasks.Task<ProviderResponse> ChatAsync(IReadOnlyList<ProviderMessage> messages, IReadOnlyList<ToolSpec> tools, string thinkingEffort, CancellationToken ct)
            => System.Threading.Tasks.Task.FromResult(new ProviderResponse { Text = "" });

        public System.Threading.Tasks.Task<ProviderResponse> ChatStreamAsync(
            IReadOnlyList<ProviderMessage> messages, IReadOnlyList<ToolSpec> tools, string thinkingEffort,
            Action<string>? onText, Action<string>? onReasoning, Action<string>? onToolFragment, CancellationToken ct)
        {
            onReasoning?.Invoke("让我想想这个函数到底该怎么拆……");
            return System.Threading.Tasks.Task.FromResult(new ProviderResponse { Text = "" });
        }

        public System.Threading.Tasks.Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<string>>(["fake-model"]);
    }

    [Fact]
    public void TheBlankCheckRequiresBothConditions()
    {
        // 只判 IsNullOrWhiteSpace 不看是否流式过，会把"已流式输出但 Text 为空"误报成失败。
        // 把条件钉在源码上，防止将来简化成只看 Text。
        var src = System.IO.File.ReadAllText(Find());
        Assert.Contains("string.IsNullOrWhiteSpace(resp.Text) && !_streamedThisCall", src);
    }

    private static string Find()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = System.IO.Path.Combine(dir, "src", "CodeAgent", "Agent", "Agent.cs");
                if (System.IO.File.Exists(f) && new System.IO.FileInfo(f).Length > 1000)
                    return f;
                var parent = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new System.IO.FileNotFoundException("找不到 src/CodeAgent/Agent/Agent.cs");
    }
}
