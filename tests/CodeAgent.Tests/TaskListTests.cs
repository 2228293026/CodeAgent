using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using CodeAgent.Tools;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

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

    [Fact]
    public async System.Threading.Tasks.Task Registry_AttachesTheListAndTheCallReachesIt()
    {
        // 上面那条只断言 Attach 之后 list.IsEmpty —— Attach 本来就不改列表，
        // 所以**挂没挂上都能过**，是条空转检查。必须真的经注册表分发一次工具调用，
        // 看清单有没有被填上——那才是模型实际走的路径。
        var reg = ToolRegistry.CreateDefault();
        var list = new TaskList();
        reg.AttachTaskList(list);
        var ack = await reg.ExecuteAsync("update_tasks", """
            {"tasks":[{"content":"第一步","status":"completed"},{"content":"第二步","status":"pending"}]}
            """, Ctx(), CancellationToken.None);
        Assert.Equal(2, list.Count);
        Assert.Equal("第一步", list.Tasks[0].Content);
        Assert.Contains("2", ack);
    }

    [Fact]
    public async System.Threading.Tasks.Task ModelCallingTheToolActuallyFillsTheList()
    {
        // 端到端：模型发出 update_tasks 工具调用 → Agent 执行 → agent.Tasks 有内容。
        // 这是"清单到底能不能被模型填上"的唯一有说服力的验证；
        // 前面那些都只是直接调工具，绕过了 Agent 真正走的执行路径。
        var queue = new Queue<Providers.ProviderResponse>();
        queue.Enqueue(new Providers.ProviderResponse
        {
            ToolCalls =
            [
                new Providers.ToolCall
                {
                    Id = "t1",
                    Name = "update_tasks",
                    ArgumentsJson = """{"tasks":[{"content":"先摸清结构","status":"completed"},{"content":"再改代码","status":"in_progress"}]}""",
                },
            ],
        });
        queue.Enqueue(new Providers.ProviderResponse { Text = "已完成第一步。" });
        var provider = new FakeProvider { ResponseQueue = queue };
        var agent = new AgentClass(
            new AgentConfig { SaveSessions = false },
            provider,
            ToolRegistry.CreateDefault());
        await agent.RunAsync("把函数抽出来", CancellationToken.None);
        Assert.Equal(2, agent.Tasks.Count);
        Assert.Equal("先摸清结构", agent.Tasks.Tasks[0].Content);
        Assert.Equal(CodeAgent.Tools.TaskStatus.InProgress, agent.Tasks.Tasks[1].Status);
        Assert.Equal(1, agent.Tasks.CompletedCount);
    }

    [Fact]
    public async System.Threading.Tasks.Task SystemPromptIsWhatTellsTheModelToDoThat()
    {
        // 端到端的另一半：工具能跑不等于模型会跑。提示里没有这条，
        // 上面那个测试里的模型不会被真实模型那样调用。
        Assert.Contains("update_tasks", agent_prompt());
        static string agent_prompt() => AgentConfig.DefaultSystemPrompt;
    }

    [Fact]
    public void Registry_AttachAloneDoesNotFillTheList()
    {
        // 反向说明：上面那条有意义，前提是"只 Attach 不调用"确实不会改清单。
        // 没有这条，将来有人给 Attach 加上副作用，会误以为上面那条在验证接线。
        var list = new TaskList();
        ToolRegistry.CreateDefault().AttachTaskList(list);
        Assert.True(list.IsEmpty);
    }

    private static AgentContext Ctx() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(System.IO.Path.GetTempPath()),
    };
}
