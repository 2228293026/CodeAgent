using System;
using System.Collections.Generic;
using System.Linq;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 任务清单的 TUI 渲染：<c>/tasks</c> 面板与回合结束的单行进度。
/// </summary>
public sealed class TaskPanelTests
{
    private static List<AgentTask> T(params (string Content, TaskStatus Status)[] xs) =>
        xs.Select(x => new AgentTask(x.Content, x.Status)).ToList();

    [Fact]
    public void Panel_EmptyProducesNothing()
    {
        // 不打印"暂无任务"——那是一行没信息量的噪音
        Assert.Equal(string.Empty, Program.FormatTaskPanel(new List<AgentTask>(), 80));
    }

    [Fact]
    public void Panel_ShowsProgressCount()
    {
        var panel = Program.FormatTaskPanel(T(
            ("a", TaskStatus.Completed), ("b", TaskStatus.Completed), ("c", TaskStatus.Pending)), 80);
        Assert.Contains("2/3", panel);
        Assert.Contains("完成", panel);
    }

    [Fact]
    public void Panel_ListsEveryTaskOnItsOwnLine()
    {
        var panel = Program.FormatTaskPanel(T(
            ("第一步", TaskStatus.Completed),
            ("第二步", TaskStatus.InProgress),
            ("第三步", TaskStatus.Pending)), 80);
        var lines = panel.Replace("\r\n", "\n").Split('\n');
        Assert.Contains("第一步", panel);
        Assert.Contains("第二步", panel);
        Assert.Contains("第三步", panel);
        Assert.Equal(4, lines.Length); // 表头 + 3 个任务
    }

    [Fact]
    public void Panel_UsesDistinctMarksPerStatus()
    {
        // 三种状态必须看得出区别，否则面板只是一份没有信息的清单
        var panel = Program.FormatTaskPanel(T(
            ("已完成", TaskStatus.Completed),
            ("进行中", TaskStatus.InProgress),
            ("待办", TaskStatus.Pending)), 80);
        var lines = panel.Replace("\r\n", "\n").Split('\n');
        Assert.NotEqual(lines[1].Trim(), lines[2].Trim());
        Assert.NotEqual(lines[1].Trim(), lines[3].Trim());
    }

    [Fact]
    public void Panel_CompletedUsesTheOkGlyph()
    {
        var panel = Program.FormatTaskPanel(T(("x", TaskStatus.Completed)), 80);
        Assert.Contains(SafeColor.Glyphs.Ok, panel);
    }

    [Fact]
    public void Panel_NeverExceedsWidth()
    {
        var tasks = Enumerable.Range(1, 12)
            .Select(i => new AgentTask($"第{i}步：这是一个相当长的任务描述需要被收口处理", TaskStatus.Pending))
            .ToList();
        for (var w = 4; w <= 200; w++)
        {
            var panel = Program.FormatTaskPanel(tasks, w);
            foreach (var line in panel.Replace("\r\n", "\n").Split('\n'))
                Assert.True(TextUtil.DisplayWidth(line) <= w, $"w={w} 超宽 {TextUtil.DisplayWidth(line)}: {line}");
        }
    }

    [Fact]
    public void Panel_NeverShowsAHalfTask()
    {
        // 半个任务看不出要做什么
        var panel = Program.FormatTaskPanel(T((new string('x', 400), TaskStatus.Pending)), 20);
        Assert.Contains(SafeColor.Glyphs.Ellipsis, panel);
    }

    [Fact]
    public void Panel_UnknownWidthShowsEverything()
    {
        var panel = Program.FormatTaskPanel(T((new string('x', 200), TaskStatus.Pending)), 0);
        Assert.Contains(new string('x', 200), panel);
    }

    // —— 单行进度 ——

    [Fact]
    public void Progress_ShowsDoneOverTotal()
    {
        var line = Program.FormatTaskProgressLine(2, 5, 80);
        Assert.Contains("2/5", line);
    }

    [Fact]
    public void Progress_EmptyIsNothing()
    {
        Assert.Equal(string.Empty, Program.FormatTaskProgressLine(0, 0, 80));
    }

    [Fact]
    public void Progress_AllDoneStillRenders()
    {
        var line = Program.FormatTaskProgressLine(5, 5, 80);
        Assert.Contains("5/5", line);
    }

    [Fact]
    public void Progress_NeverExceedsWidth()
    {
        for (var w = 1; w <= 120; w++)
            Assert.True(TextUtil.DisplayWidth(Program.FormatTaskProgressLine(999, 1000, w)) <= w);
    }

    [Fact]
    public void Progress_IsOneLineNotAPanel()
    {
        // 面板一次五个任务就是五行；回合摘要里只给一行结论
        var line = Program.FormatTaskProgressLine(3, 5, 80);
        Assert.DoesNotContain("\n", line);
    }

    [Fact]
    public void TasksCommand_IsInBothCommandLists()
    {
        // /help 里有、Tab 补全里没有 = 用户敲出来补不出来（CommandListSyncTests 也会查）
        var help = string.Join("\n", Program.ReplCommands.Select(c => c.Command));
        Assert.Contains("/tasks", help);
        Assert.Contains("/tasks", string.Join("\n", InputLine.Commands.Select(c => c.Name)));
    }
}
