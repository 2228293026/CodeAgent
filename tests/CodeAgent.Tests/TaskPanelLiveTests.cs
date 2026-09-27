using System;
using System.Collections.Generic;
using System.Linq;
using CodeAgent.Providers;
using CodeAgent.Tools;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 回合内**实时**的任务进度。
///
/// 此前清单只在 <c>/tasks</c>（回合外）和回合摘要（回合结束）出现——
/// 长任务进行的整个过程中，用户看到的只有一个不动的 spinner，
/// ���型列了什么计划、做到哪一步，完全看不见。
/// </summary>
public sealed class TaskPanelLiveTests
{
    // —— 纯规则 ——

    [Fact]
    public void Rule_FirstUpdateAlwaysRenders()
    {
        Assert.True(AgentClass.ShouldRenderTaskPanel(shownThisTurn: false, lastShownDone: 0, done: 0));
    }

    [Fact]
    public void Rule_NoRenderWithoutRealProgress()
    {
        // 模型每完成一步就 update_tasks 一次；无进展的重画会变成刷屏
        Assert.False(AgentClass.ShouldRenderTaskPanel(shownThisTurn: true, lastShownDone: 2, done: 2));
    }

    [Fact]
    public void Rule_RendersWhenDoneCountIncreases()
    {
        Assert.True(AgentClass.ShouldRenderTaskPanel(shownThisTurn: true, lastShownDone: 2, done: 3));
    }

    [Fact]
    public void Rule_DoesNotRenderWhenDoneCountDrops()
    {
        // 清单被重排（模型把已完成项改成进行中）不该触发重画：
        // 那不是进展，刷出来只会让面板反复跳动
        Assert.False(AgentClass.ShouldRenderTaskPanel(shownThisTurn: true, lastShownDone: 3, done: 1));
    }

    [Fact]
    public void Rule_RendersOnceThenOnlyOnProgress()
    {
        var shown = false;
        var last = 0;
        var renders = 0;
        // 五个任务逐个完成 → 只该画 5 次（首次 + 4 次进展），不是 5 次逐条重画
        foreach (var done in new[] { 0, 1, 1, 2, 2, 3, 4 })
        {
            if (AgentClass.ShouldRenderTaskPanel(shown, last, done))
            {
                renders++;
                shown = true;
                last = done;
            }
        }
        Assert.Equal(5, renders);
    }

    // —— 端到端 ——

    /// <summary>一次回合：连续 N 次 update_tasks，最后给一条文本收尾。
    /// 中间**不能**插文本回复——模型一旦给出正文，本回合就结束了。</summary>
    private static Queue<ProviderResponse> Turn(params string[] taskPayloads)
    {
        var q = new Queue<ProviderResponse>();
        for (var i = 0; i < taskPayloads.Length; i++)
            q.Enqueue(new ProviderResponse
            {
                ToolCalls = [new ToolCall { Id = "u" + i, Name = "update_tasks", ArgumentsJson = taskPayloads[i] }],
            });
        q.Enqueue(new ProviderResponse { Text = "好了" });
        return q;
    }

    private static AgentClass Make(FakeProvider p) => new(
        new AgentConfig { SaveSessions = false },
        p,
        ToolRegistry.CreateDefault());

    [Fact]
    public async System.Threading.Tasks.Task TaskToolRunsMidTurnAndTheListEndsUpRight()
    {
        var p = new FakeProvider
        {
            ResponseQueue = Turn(
                """{"tasks":[{"content":"读结构","status":"in_progress"},{"content":"改代码","status":"pending"}]}""",
                """{"tasks":[{"content":"读结构","status":"completed"},{"content":"改代码","status":"in_progress"}]}"""),
        };
        var agent = Make(p);
        await agent.RunAsync("把函数抽出来", System.Threading.CancellationToken.None);
        Assert.Equal(2, agent.Tasks.Count);
        Assert.Equal(1, agent.Tasks.CompletedCount);
        Assert.Equal(CodeAgent.Tools.TaskStatus.InProgress, agent.Tasks.Tasks[1].Status);
    }

    [Fact]
    public async System.Threading.Tasks.Task PanelStateResetsBetweenTurns()
    {
        // 每回合都要重新"首次显示"：否则第二个回合的第一次更新被当成无进展而吞掉，
        // 用户会以为面板卡住了
        var p = new FakeProvider
        {
            ResponseQueue = Turn("""{"tasks":[{"content":"第一轮任务","status":"completed"}]}"""),
        };
        var agent = Make(p);
        await agent.RunAsync("第一件事", System.Threading.CancellationToken.None);
        Assert.Equal("第一轮任务", agent.Tasks.Tasks[0].Content);

        p.ResponseQueue = Turn("""{"tasks":[{"content":"第二轮任务","status":"pending"}]}""");
        await agent.RunAsync("第二件事", System.Threading.CancellationToken.None);
        Assert.Equal("第二轮任务", agent.Tasks.Tasks[0].Content);
    }

    [Fact]
    public async System.Threading.Tasks.Task EmptyTaskListDoesNotCrash()
    {
        var p = new FakeProvider { ResponseQueue = Turn("""{"tasks":[]}""") };
        var agent = Make(p);
        await agent.RunAsync("你好", System.Threading.CancellationToken.None);
        Assert.True(agent.Tasks.IsEmpty);
    }

    [Fact]
    public async System.Threading.Tasks.Task OtherToolsDoNotTriggerThePanel()
    {
        // 普通工具执行不该画任务面板——那东西只跟 update_tasks 有关
        var q = new Queue<ProviderResponse>();
        q.Enqueue(new ProviderResponse
        {
            ToolCalls = [new ToolCall { Id = "r", Name = "read_file", ArgumentsJson = """{"path":"no-such-file"}""" }],
        });
        q.Enqueue(new ProviderResponse { Text = "读不到" });
        var agent = Make(new FakeProvider { ResponseQueue = q });
        await agent.RunAsync("读一下", System.Threading.CancellationToken.None);
        Assert.True(agent.Tasks.IsEmpty);
    }
}
