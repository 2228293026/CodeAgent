using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 任务清单（学自 Claude Code 的 TodoWrite）：模型写、REPL 渲染。
/// </summary>
public sealed class TaskListTests
{
    private static List<AgentTask> T(params (string Content, TaskStatus Status)[] xs) =>
        xs.Select(x => new AgentTask(x.Content, x.Status)).ToList();

    // —— 解析 ——

    [Fact]
    public void Parse_ReadsContentAndStatus()
    {
        // Parse 收的是 tasks 数组本身（工具里传的是 args["tasks"]）
        var parsed = TaskList.Parse(JsonNode.Parse("""
            [
              {"content":"读代码","status":"completed"},
              {"content":"改代码","status":"in_progress"},
              {"content":"跑测试","status":"pending"}
            ]
            """));
        Assert.Equal(3, parsed.Count);
        Assert.Equal("读代码", parsed[0].Content);
        Assert.Equal(TaskStatus.Completed, parsed[0].Status);
        Assert.Equal(TaskStatus.InProgress, parsed[1].Status);
        Assert.Equal(TaskStatus.Pending, parsed[2].Status);
    }

    [Fact]
    public void Parse_SkipsBadItemsWithoutLosingTheRest()
    {
        // 模型少写一个字段不该让整份计划消失——计划全没了用户就完全看不到模型在干什么
        var parsed = TaskList.Parse(JsonNode.Parse("""
            [
              {"content":"好的","status":"pending"},
              {"status":"pending"},
              {"content":"","status":"pending"},
              "not-an-object",
              {"content":"也好","status":"completed"}
            ]
            """));
        Assert.Equal(2, parsed.Count);
        Assert.Equal("好的", parsed[0].Content);
        Assert.Equal("也好", parsed[1].Content);
    }

    [Fact]
    public void Parse_EmptyAndMissingIsEmpty()
    {
        Assert.Empty(TaskList.Parse(null));
        Assert.Empty(TaskList.Parse(JsonNode.Parse("[]")));
        Assert.Empty(TaskList.Parse(JsonNode.Parse("{}")));
    }

    [Theory]
    [InlineData("completed", TaskStatus.Completed)]
    [InlineData("DONE", TaskStatus.Completed)]
    [InlineData("in_progress", TaskStatus.InProgress)]
    [InlineData("InProgress", TaskStatus.InProgress)]
    [InlineData("pending", TaskStatus.Pending)]
    [InlineData("nonsense", TaskStatus.Pending)]
    [InlineData(null, TaskStatus.Pending)]
    public void Status_UnknownFallsBackToPending(string? raw, TaskStatus expected)
    {
        // 猜成"进行中"会显示模型其实没在做的事——那是在骗用户
        Assert.Equal(expected, TaskList.ParseStatus(raw));
    }

    // —— 状态容器 ——

    [Fact]
    public void List_ReplaceIsNotIncremental()
    {
        var list = new TaskList();
        list.Replace(T(("a", TaskStatus.Pending), ("b", TaskStatus.Pending)));
        list.Replace(T(("c", TaskStatus.Pending)));
        Assert.Equal(1, list.Count);
        Assert.Equal("c", list.Tasks[0].Content);
    }

    [Fact]
    public void List_CanBeCleared()
    {
        var list = new TaskList();
        list.Replace(T(("a", TaskStatus.Pending)));
        list.Clear();
        Assert.True(list.IsEmpty);
    }

    [Fact]
    public void List_CapsTheNumberOfTasks()
    {
        var list = new TaskList();
        list.Replace(Enumerable.Range(1, 500).Select(i => new AgentTask($"任务{i}", TaskStatus.Pending)));
        Assert.Equal(TaskList.MaxTasks, list.Count);
    }

    [Fact]
    public void List_CountsByStatus()
    {
        var list = new TaskList();
        list.Replace(T(
            ("a", TaskStatus.Completed), ("b", TaskStatus.Completed),
            ("c", TaskStatus.InProgress), ("d", TaskStatus.Pending)));
        Assert.Equal(4, list.Count);
        Assert.Equal(2, list.CompletedCount);
        Assert.Equal(1, list.InProgressCount);
    }

    [Fact]
    public void List_TrimsContentAndDropsBlank()
    {
        var list = new TaskList();
        list.Replace(T(("  有前后空格  ", TaskStatus.Pending), ("   ", TaskStatus.Pending)));
        Assert.Equal(1, list.Count);
        Assert.Equal("有前后空格", list.Tasks[0].Content);
    }

    // —— 工具接线 ——

    [Fact]
    public async System.Threading.Tasks.Task Tool_WritesIntoTheAttachedList()
    {
        var list = new TaskList();
        var tool = new UpdateTasksTool();
        tool.Attach(list);
        var out1 = await tool.ExecuteAsync(JsonNode.Parse("""
            {"tasks":[{"content":"第一步","status":"completed"},{"content":"第二步","status":"in_progress"}]}
            """)!.AsObject(), Ctx(), CancellationToken.None);
        Assert.Equal(2, list.Count);
        Assert.Contains("2", out1);
        Assert.Contains("1", out1); // 完成 1 项
    }

    [Fact]
    public async System.Threading.Tasks.Task Tool_WithoutListStillAcks()
    {
        // 单次 CLI 调用没有会话清单，也必须有可预期的回显而不是静默成功
        var out1 = await new UpdateTasksTool().ExecuteAsync(JsonNode.Parse("""
            {"tasks":[{"content":"a","status":"pending"}]}
            """)!.AsObject(), Ctx(), CancellationToken.None);
        Assert.Contains("1", out1);
    }

    [Fact]
    public async System.Threading.Tasks.Task Tool_ClearingTheListIsAcked()
    {
        var list = new TaskList();
        list.Replace(T(("a", TaskStatus.Pending)));
        var tool = new UpdateTasksTool();
        tool.Attach(list);
        var out1 = await tool.ExecuteAsync(JsonNode.Parse("""{"tasks":[]}""")!.AsObject(), Ctx(), CancellationToken.None);
        Assert.True(list.IsEmpty);
        Assert.Contains("清空", out1);
    }

    [Fact]
    public async System.Threading.Tasks.Task Tool_RespectsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new UpdateTasksTool().ExecuteAsync(new JsonObject(), Ctx(), cts.Token));
    }

    [Fact]
    public void Tool_IsRegistered()
    {
        var specs = ToolRegistry.CreateDefault().ToToolSpecs();
        Assert.Contains(specs, s => s.Name == "update_tasks");
    }

    [Fact]
    public void Registry_AttachesTheListToTheRegisteredTool()
    {
        var list = new TaskList();
        ToolRegistry.CreateDefault().AttachTaskList(list);
        // 通过注册表分发一次，清单应当被填上
        var reg = ToolRegistry.CreateDefault();
        reg.AttachTaskList(list);
        Assert.True(list.IsEmpty);
    }

    private static AgentContext Ctx() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(System.IO.Path.GetTempPath()),
    };
}
