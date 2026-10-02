using System;
using System.Collections.Generic;
using System.Linq;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 任务面板是用户**唯一**能看见"模型正在做什么"的地方，而它的回执曾经把
/// 超上限丢弃的部分**说成已经记下了**。
///
/// <c>TaskList.Replace</c> 里是 <c>foreach (var t in tasks.Take(MaxTasks))</c>——
/// 模型提交 60 项，清单只留 50 项，而 <c>RenderAck</c> 报的是
/// <c>{tasks.Count}</c>，也就是**保留后**的数：
///
///   <c>任务清单已更新：50 项（完成 0，进行中 0）。</c>
///
/// 模型据此以为 60 项都记下了，会按"我记了 60 项"继续工作，
/// 而任务 51~60 从来没进过清单——用户看面板时也只看到 50 项，
/// 却得不到任何"还有 10 项被丢了"的提示。被丢的往往正是排在后面的、还没开始的活。
///
/// 这条把"被丢弃的条目数必须在回执里出现"钉住。
/// </summary>
public sealed class TaskListDropIsDisclosedTests
{
    private static List<AgentTask> Many(int n) =>
        Enumerable.Range(1, n).Select(i => new AgentTask($"任务 {i}", TaskStatus.Pending)).ToList();

    private static TaskList NewList()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ca-tl-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "codeagent.json"), "{}");
        return new TaskList();
    }

    [Fact]
    public void TheAckSaysHowManyWereDropped()
    {
        var list = NewList();
        list.Replace(Many(60));
        var ack = UpdateTasksTool.RenderAck(list);
        Assert.Contains("超出上限", ack);
        Assert.Contains("未记录", ack);
        Assert.Contains("10", ack);
    }

    [Fact]
    public void TheKeptCountIsStillReported()
    {
        var list = NewList();
        list.Replace(Many(60));
        var ack = UpdateTasksTool.RenderAck(list);
        Assert.Contains("50 项", ack);       // 保留后的数仍在
        Assert.Equal(TaskList.MaxTasks, list.Count);
    }

    [Fact]
    public void NothingIsClaimedWhenNothingIsDropped()
    {
        var list = NewList();
        list.Replace(Many(10));
        var ack = UpdateTasksTool.RenderAck(list);
        Assert.DoesNotContain("未记录", ack);
        Assert.DoesNotContain("超出上限", ack);
        Assert.Contains("10 项", ack);
    }

    [Fact]
    public void ExactlyTheCapIsAccepted()
    {
        // 边界：正好 50 项不是"超上限"，不能报没记录
        var list = NewList();
        list.Replace(Many(TaskList.MaxTasks));
        Assert.Equal(TaskList.MaxTasks, list.Count);
        Assert.DoesNotContain("未记录", UpdateTasksTool.RenderAck(list));
        Assert.Equal(0, list.LastDropped);
    }

    [Fact]
    public void OneOverTheCapStillReportsOne()
    {
        var list = NewList();
        list.Replace(Many(TaskList.MaxTasks + 1));
        Assert.Equal(TaskList.MaxTasks, list.Count);
        Assert.Equal(1, list.LastDropped);
        Assert.Contains("另有 1 项", UpdateTasksTool.RenderAck(list));
    }

    [Fact]
    public void BlankEntriesDoNotCountAsDropped()
    {
        // 空内容会被丢弃（Parse/Replace 都跳过），但那是"模型没写内容"，
        // 不是"超上限"——两件事不能混成一条消息
        var list = NewList();
        var tasks = Many(5);
        tasks.Add(new AgentTask("   ", TaskStatus.Pending));
        list.Replace(tasks);
        Assert.Equal(0, list.LastDropped);
        Assert.Equal(5, list.Count);
        Assert.DoesNotContain("未记录", UpdateTasksTool.RenderAck(list));
    }

    [Fact]
    public void ReplaceResetsTheDropCountOnTheNextCall()
    {
        // 第二次调用不能沿用上一次的丢弃数，否则回执会一直说有东西被丢
        var list = NewList();
        list.Replace(Many(60));
        Assert.True(list.LastDropped > 0);
        list.Replace(Many(3));
        Assert.Equal(0, list.LastDropped);
        Assert.DoesNotContain("未记录", UpdateTasksTool.RenderAck(list));
    }

    [Fact]
    public void TheEmptyAckIsUnaffected()
    {
        var list = NewList();
        list.Clear();
        Assert.Equal("任务清单已清空。", UpdateTasksTool.RenderAck(list));
    }
}
