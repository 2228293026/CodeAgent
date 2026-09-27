using System;
using System.Collections.Generic;
using System.Linq;
using CodeAgent.Tools;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 任务清单在**受限模式**下的可用性。
///
/// <c>update_tasks</c> 只改内存里的进度面板，不碰工作区。此前它会被
/// <c>AllowedTools</c> 的过滤一并挡掉——于是只读模式下进度面板永远空白，
/// 而只读恰恰是最需要进度可见的场景（长时间排查、大范围阅读）。挡它没有任何安全收益。
/// </summary>
public sealed class TaskListModeTests
{
    private static AgentConfig Cfg(params string[] allowed) => new()
    {
        SaveSessions = false,
        Modes =
        [
            new AgentModeConfig
            {
                Name = "ro",
                Description = "只读",
                SystemPrompt = "只读排查",
                Tools = [.. allowed],
            },
        ],
    };

    private static AgentClass Make(AgentConfig cfg, FakeProvider? provider = null) =>
        new(cfg, provider ?? new FakeProvider(), ToolRegistry.CreateDefault());

    private static void UseReadOnly(AgentClass agent, AgentConfig cfg) =>
        agent.SetMode(Modes.Find("ro", cfg));

    [Fact]
    public void UpdateTasks_IsVisibleEvenInAReadOnlyMode()
    {
        var cfg = Cfg("read_file", "grep", "glob");
        var agent = Make(cfg);
        UseReadOnly(agent, cfg);
        Assert.Contains(AgentClass.AlwaysAvailableTool, agent.ToolsForMode().Select(t => t.Name));
    }

    [Fact]
    public void UpdateTasks_IsVisibleWhenTheModeAllowsNothing()
    {
        // 连一个只读工具都没给的面板模式：进度依然该看得见
        var cfg = Cfg();
        var agent = Make(cfg);
        UseReadOnly(agent, cfg);
        Assert.Contains(AgentClass.AlwaysAvailableTool, agent.ToolsForMode().Select(t => t.Name));
    }

    [Fact]
    public void ReadOnlyMode_StillHidesWriteTools()
    {
        // 前提没被破坏：这条修的是"面板工具被误伤"，不是"模式限制整体失效了"
        var cfg = Cfg("read_file", "grep");
        var agent = Make(cfg);
        UseReadOnly(agent, cfg);
        var names = agent.ToolsForMode().Select(t => t.Name).ToList();
        Assert.Contains("read_file", names);
        Assert.DoesNotContain("write_file", names);
        Assert.DoesNotContain("edit_file", names);
        Assert.DoesNotContain("run_command", names);
    }

    [Fact]
    public void ReadOnlyMode_StillBlocksWriteToolCalls()
    {
        // 模型硬调也要拦住：防御性分支不能因为上面那条放宽而漏掉
        var cfg = Cfg("read_file");
        var agent = Make(cfg);
        UseReadOnly(agent, cfg);
        Assert.False(agent.ToolAllowedInMode("write_file"));
        Assert.False(agent.ToolAllowedInMode("run_command"));
        Assert.True(agent.ToolAllowedInMode("read_file"));
    }

    [Fact]
    public void UpdateTasks_CallIsNotBlockedInAReadOnlyMode()
    {
        var cfg = Cfg("read_file");
        var agent = Make(cfg);
        UseReadOnly(agent, cfg);
        Assert.True(agent.ToolAllowedInMode(AgentClass.AlwaysAvailableTool));
    }

    [Fact]
    public void UnrestrictedMode_ChangesNothing()
    {
        // AllowedTools 为空 = 不限制：这是默认值，不能因为上面那条而变出意外
        var agent = Make(new AgentConfig { SaveSessions = false });
        Assert.Contains(AgentClass.AlwaysAvailableTool, agent.ToolsForMode().Select(t => t.Name));
        Assert.True(agent.ToolAllowedInMode("write_file"));
    }

    [Fact]
    public async System.Threading.Tasks.Task UpdateTasks_ActuallyRunsInAReadOnlyMode()
    {
        // 端到端：只读模式里模型真的能推进度（"只读"约束的是工作区，不是 UI 状态）
        var queue = new Queue<CodeAgent.Providers.ProviderResponse>();
        queue.Enqueue(new CodeAgent.Providers.ProviderResponse
        {
            ToolCalls =
            [
                new CodeAgent.Providers.ToolCall
                {
                    Id = "t1",
                    Name = "update_tasks",
                    ArgumentsJson = """{"tasks":[{"content":"在只读模式下排查","status":"in_progress"}]}""",
                },
            ],
        });
        queue.Enqueue(new CodeAgent.Providers.ProviderResponse { Text = "排查中。" });
        var cfg = Cfg("read_file");
        var agent = Make(cfg, new FakeProvider { ResponseQueue = queue });
        UseReadOnly(agent, cfg);
        await agent.RunAsync("查一下有没有死循环", System.Threading.CancellationToken.None);
        Assert.Equal(1, agent.Tasks.Count);
        Assert.Equal("在只读模式下排查", agent.Tasks.Tasks[0].Content);
    }

    [Fact]
    public void TheExemptToolIsActuallyTheTaskTool()
    {
        // 常量写错名字的话，上面全部会变成"测了另一个不存在的东西"
        Assert.Equal("update_tasks", AgentClass.AlwaysAvailableTool);
        Assert.Contains(ToolRegistry.CreateDefault().ToToolSpecs(), s => s.Name == AgentClass.AlwaysAvailableTool);
    }

    [Fact]
    public void ModeFilterAndCallGuardUseTheSameRule()
    {
        // 两处各自判断就会走偏：模型看得见工具却调不动，或反之
        var src = System.IO.File.ReadAllText(Find());
        Assert.Contains("ToolAllowedInMode(tc.Name)", src);
        Assert.DoesNotContain("!allowed.Contains(tc.Name", src);
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
